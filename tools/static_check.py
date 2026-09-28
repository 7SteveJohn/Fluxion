# -*- coding: utf-8 -*-
"""
Fluxion 静态校验（沙箱无法调用 csc，用此项替代）：
  1) 顶层类型重名检测（CS0101/CS0102）
  2) 同一类型内成员重名检测   （CS0102）
  3) 跨文件符号引用解析       （CS0103/CS0117：Program.X / Dlssg.X / Cfg.X / Theme.X / Native.X）
  4) 括号平衡                 （CS1513/CS1022）
  5) C# 6+ 语法违规           （$"" / ?. / ?? / nameof / 表达式体成员）
  6) long -> int 隐式转换风险 （CS0266：数组长度表达式）
用法: python static_check.py <src_dir>
"""
import re
import sys
import os

CS6_PATTERNS = [
    (r'\$#', 'string interpolation $"..." (C#6)'),
    (r"\?\.", 'null-conditional ?. (C#6)'),
    (r"\bnameof\s*\(", 'nameof (C#6)'),
    (r"\busing\s+static\s+", 'using static (C#6)'),
    (r"catch\s*\([^)]*\)\s*when\s*\(", 'exception filter when (C#6)'),
    (r"\bget\s*=>", 'expression-bodied getter (C#6)'),
    (r"\bset\s*=>", 'expression-bodied setter (C#6)'),
]
# 注意: "??" 是 C# 2.0 运算符; lambda "(x) => y" 是 C# 3.0 —— 两者都合法，不报错。
# 只有"表达式体成员"才是 C# 6 特性，需单独判定(排除 lambda 赋值)。
EBO_RE = re.compile(r'\)\s*=>\s*[^{}]*;')

# DllImport 允许的命名参数（.NET Framework 全量）。写错一个只在编译时炸 CS0246，
# 沙箱不能编译 → 由 5c) 规则静态兜住。
DLLIMPORT_NAMED = set("""
CallingConvention CharSet EntryPoint ExactSpelling SetLastError PreserveSig
BestFitMapping ThrowOnUnmappableChar
""".split())
LAMBDA_ASSIGN_RE = re.compile(r'[+\-*/]?=[^=;]*\)\s*=>')

# ---------------------------------------------------------------- CS0108
# 自定义类若继承 WinForms 基类，声明同名成员会"遮蔽"基类成员（CS0108 警告）。
# 有的是无害的（自己不用基类那套语义），有的会真出错：Control.Right 是右边缘坐标、
# Control.Tag 是通用载荷 —— 用 string 覆盖它们会让基类语义静默失效。
WINFORMS_BASES = set("""
Control ScrollableControl ContainerControl Panel Form UserControl Button Label
TextBox RichTextBox ComboBox CheckBox RadioButton ListBox ListView ProgressBar
GroupBox TabControl SplitContainer TableLayoutPanel FlowLayoutPanel TrackBar
NumericUpDown PictureBox Timer NotifyIcon ToolStrip ToolStripMenuItem
""".split())

# Control/Component 及其所有派生类都有的成员（Panel/ScrollableControl/ContainerControl 也覆盖）
CORE_MEMBERS = set("""
Text Tag Name Font ForeColor BackColor Enabled Visible Parent Controls Dock Anchor
Padding Margin Cursor Capture Region Bounds ClientSize Left Top Right Bottom
Width Height Size Location Handle AllowDrop AutoSize AutoScroll TabIndex TabStop
ContextMenuStrip RightToLeft UseWaitCursor CausesValidation DoubleBuffered
BackgroundImage BackgroundImageLayout Site Container DataBindings ImeMode
AccessibleName AccessibleRole Refresh Update ResetText Scale Focus Select
Invalidate CreateGraphics Dispose Show Hide PointToClient PointToScreen
BringToFront SendToBack FindForm
""".split())

# 各基类额外贡献的成员 —— 必须按基类区分，否则会误报
# （例：Control 没有 Value，StatBar.Value 是合法的新成员）
BASE_EXTRA = {
    'Form': set("""DialogResult AcceptButton CancelButton WindowState StartPosition
        FormBorderStyle MaximizeBox MinimizeBox ShowInTaskbar TopMost Icon
        MainMenuStrip KeyPreview Opacity IsMdiContainer ControlBox ActiveMdiChild""".split()),
    'Button': set("""DialogResult FlatStyle Image ImageAlign TextAlign TextImageRelation
        UseVisualStyleBackColor AutoEllipsis Notifier""".split()),
    'Label': set("""FlatStyle Image ImageAlign TextAlign AutoEllipsis UseMnemonic BorderStyle""".split()),
    'TextBox': set("""BorderStyle ReadOnly Multiline WordWrap ScrollBars MaxLength
        PasswordChar UseSystemPasswordChar Lines SelectionStart SelectionLength
        AcceptsReturn AcceptsTab CharacterCasing TextAlign Modified""".split()),
    'RichTextBox': set("""BorderStyle ReadOnly Multiline WordWrap ScrollBars MaxLength
        PasswordChar Lines SelectionStart SelectionLength AcceptsTab TextAlign Modified
        Zoom DetectUrls ShowSelectionMargin Rtf SelectedRtf BulletIndent
        SelectionColor SelectionFont SelectionAlignment""".split()),
    'ComboBox': set("""Items SelectedIndex SelectedItem SelectedValue DropDownStyle
        DropDownWidth DropDownHeight MaxDropDownItems DrawMode ItemHeight MaxLength
        Sorted SelectedText SelectionStart SelectionLength IntegralHeight""".split()),
    'CheckBox': set("""Checked CheckState AutoCheck ThreeState FlatStyle TextAlign
        Image ImageAlign Appearance""".split()),
    'RadioButton': set("""Checked CheckState AutoCheck FlatStyle TextAlign Image
        ImageAlign Appearance""".split()),
    'ListBox': set("""Items SelectedIndex SelectedItem SelectedItems SelectionMode
        MultiColumn ColumnWidth HorizontalScrollbar IntegralHeight Sorted""".split()),
    'ListView': set("""Items SelectedItems Columns View MultiSelect FullRowSelect
        GridLines HeaderStyle CheckBoxes SmallImageList LargeImageList""".split()),
    'ProgressBar': set("""Value Maximum Minimum Step Style MarqueeAnimationSpeed""".split()),
    'NumericUpDown': set("""Value Maximum Minimum Increment DecimalPlaces
        ThousandsSeparator Hexadecimal InterceptArrowKeys ReadOnly""".split()),
    'TrackBar': set("""Value Maximum Minimum TickFrequency LargeChange SmallChange
        Orientation TickStyle""".split()),
    'Timer': set("""Interval Tick""".split()),
    'NotifyIcon': set("""Icon Visible BalloonTipText BalloonTipTitle BalloonTipIcon""".split()),
}


def strip_inactive(text):
    """只保留 #if 分支（丢弃 #else/#elif 分支），并抹掉预处理指令行。

    为什么需要：同一个常量在 #if/#else 两条分支各定义一次是**合法 C#** ——
    编译器只会留下一条。但纯文本分析会把它看成"重复成员"（实测把
    BuildTag / DisplayVersion 判成 DUPLICATE，误报）。抹成空行是为了让行号不错位。
    """
    out, in_else = [], []
    for line in text.split('\n'):
        s = line.strip()
        if s.startswith('#if'):
            in_else.append(False)
            out.append('')
            continue
        if s.startswith('#else') or s.startswith('#elif'):
            if in_else:
                in_else[-1] = True
            out.append('')
            continue
        if s.startswith('#endif'):
            if in_else:
                in_else.pop()
            out.append('')
            continue
        if any(in_else):
            out.append('')
            continue
        out.append(line)
    return '\n'.join(out)


def hiding_members(base):
    """该基类下"声明同名成员会遮蔽基类"的全部成员名。"""
    return CORE_MEMBERS | BASE_EXTRA.get(base, set())


def base_of(clean, tname):
    m = re.search(
        r'^[ \t]{4}(?:(?:public|internal|private|protected)\s+)?'
        r'(?:static\s+|sealed\s+|abstract\s+|unsafe\s+|partial\s+)*'
        r'(?:class|struct)\s+' + re.escape(tname) + r'\s*:\s*([A-Za-z_][\w\.]*)',
        clean, re.M)
    return m.group(1).split('.')[-1] if m else None


# ---------------------------------------------------------------- CS0136
# 只收集"块作用域"的局部变量声明。
# 关键：using/for/foreach/while/lock 的头部变量作用域是"该语句"而非外层块，
# 所以 `using (var b = A) X();` 与后面块内的 `var b` 并不冲突，不能报（这是本检查器
# 第一版的最大误报来源）。判定方式：声明位置若处于圆括号内，且该 `(` 与声明之间没有
# `{`，即视为头部变量 → 跳过。
LOCAL_DECL_RE = re.compile(
    r'(?:^|[;{}():,])\s*'
    r'(?:(?P<mods>(?:const|int|long|short|byte|sbyte|uint|ulong|ushort|bool|char|float|'
    r'double|decimal|string|object|var))\s+)'
    r'(?P<name>[A-Za-z_]\w*)\s*(?P<tail>[=;])', re.M)

# 语句头部引入的变量（作用域 = 该语句）：using/for/foreach/catch
HEADER_VAR_RES = [
    re.compile(r'\b(?:using|for|foreach)\s*\(\s*(?:var|[A-Za-z_][\w\.<>\[\],]*)\s+'
               r'(?P<name>[A-Za-z_]\w*)\s*(?:in\b|=)'),
    re.compile(r'\bcatch\s*\(\s*[A-Za-z_][\w\.<>\[\],]*\s+(?P<name>[A-Za-z_]\w*)\s*\)'),
]

# 方法声明（用于把"参数"当成外层作用域）：形如 `返回类型 名字(参数) {`。
# 要求圆括号前有两个标识符，因此 `if (x) {` / `foreach (var a in b) {` 这类语句头
# 不会被误当成方法声明。
# 类型部分允许可空标记 `?`（`bool? GpuSupportsDlssFg()`）—— 否则这类方法整条漏登记，
# 引用它的地方会被误报成 UNRESOLVED（2026-09-13 实踩）。
RE_METHOD_DECL = re.compile(
    r'\b[A-Za-z_]\w*(?:\s*<[^<>()]*>)?(?:\s*\?)?(?:\s*\[\s*\])?\s+'
    r'(?P<mname>[A-Za-z_]\w*)\s*\((?P<params>[^()]*)\)\s*(?:where[^{;]*)?\{'
)


def header_span(s, pos):
    """返回包含 pos 的头部变量所属语句的范围 (start, end)。
    start = 头部 '(' 的位置；end = 语句结束（花括号体的 '}' 或无花括号体的 ';'）。"""
    depth, start = 0, -1
    for i in range(pos, -1, -1):
        if s[i] == ')':
            depth += 1
        elif s[i] == '(':
            if depth == 0:
                start = i
                break
            depth -= 1
    if start < 0:
        return None
    d, close = 0, -1
    j = start
    while j < len(s):
        if s[j] == '(':
            d += 1
        elif s[j] == ')':
            d -= 1
            if d == 0:
                close = j
                break
        j += 1
    if close < 0:
        return None
    k = close + 1
    while k < len(s) and s[k].isspace():
        k += 1
    if k < len(s) and s[k] == '{':
        d2 = 0
        while k < len(s):
            if s[k] == '{':
                d2 += 1
            elif s[k] == '}':
                d2 -= 1
                if d2 == 0:
                    return (start, k)
            k += 1
        return (start, len(s) - 1)
    d2 = 0
    while k < len(s):
        c = s[k]
        if c in '([{':
            d2 += 1
        elif c in ')]}':
            if d2 == 0:
                break
            d2 -= 1
        elif c == ';' and d2 == 0:
            return (start, k)
        k += 1
    return (start, k)


def strip_noise(text):
    """去掉字符串字面量/注释，避免误报；用等长空白替换保持行号与偏移。"""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        # 行注释
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '
                i += 1
            continue
        # 块注释
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            out[i] = out[i + 1] = ' '
            i += 2
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            if i < n:
                out[i] = ' '
                if i + 1 < n:
                    out[i + 1] = ' '
                i += 2
            continue
        # 字符串字面量（含 verbatim）
        if c == '@' and i + 1 < n and text[i + 1] == '"':
            out[i] = out[i + 1] = ' '
            i += 2
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out[i] = out[i + 1] = ' '
                        i += 2
                        continue
                    out[i] = ' '
                    i += 1
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            continue
        if c == '"':
            # 保留 $"" 插值的可识别性: 用 '#' 占位引号，避免被字符串剥离掩盖(C#6 检查依赖)
            prev = ''
            for k in range(i - 1, -1, -1):
                if out[k] != ' ':
                    prev = out[k]
                    break
            out[i] = '#' if prev == '$' else ' '
            i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n:
                        out[i + 1] = ' '
                    i += 2
                    continue
                if text[i] == '"':
                    out[i] = ' '
                    i += 1
                    break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            continue
        if c == "'":
            out[i] = ' '
            i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n:
                        out[i + 1] = ' '
                    i += 2
                    continue
                if text[i] == "'":
                    out[i] = ' '
                    i += 1
                    break
                out[i] = ' '
                i += 1
            continue
        i += 1
    return ''.join(out)


def find_type_bodies(clean, lineof):
    """返回 [(typename, body_text, start_line)]，只取命名空间内缩进 4 空格的顶层类型。"""
    types = []
    pat = re.compile(
        r'^(?P<ind>[ \t]*)(?:(?:public|internal|private|protected)\s+)?'
        r'(?:static\s+|sealed\s+|abstract\s+|unsafe\s+|partial\s+)*'
        r'(?:class|struct|enum|interface)\s+(?P<name>\w+)[^\n{;]*\n?\s*\{',
        re.M)
    for m in pat.finditer(clean):
        ind = len(m.group('ind').expandtabs(4))
        if ind != 4:
            continue
        brace = clean.index('{', m.start('name'))
        depth = 0
        j = brace
        while j < len(clean):
            if clean[j] == '{':
                depth += 1
            elif clean[j] == '}':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        types.append((m.group('name'), clean[brace + 1:j], lineof(m.start())))
    return types


MEMBER_RE = re.compile(
    r'^[ \t]{4,}(?:[A-Za-z_]\w*\s+)*'
    r'(?P<type>[A-Za-z_][\w\.]*(?:<[^<>]*>)?(?:\?)?(?:\[\])*)\s+'
    r'(?P<name>[A-Za-z_]\w*)\s*'
    r'(?P<tail>[\(=;\{])', re.M)


def depth_map(body):
    """返回与 body 等长的列表：每个字符处的花括号深度(从 0 开始)。"""
    d = []
    cur = 0
    for ch in body:
        if ch == '{':
            d.append(cur)
            cur += 1
        elif ch == '}':
            cur -= 1
            d.append(cur)
        else:
            d.append(cur)
    return d


def build_blocks(clean):
    """返回 (depth[], blocks[])，blocks[i] = (start, end) 为第 i 个 '{' 的块区间。
    用于判断两个块之间的嵌套包含关系（CS0136 需要）。"""
    cur, stack, blocks = 0, [], []
    depth = [0] * len(clean)
    for i, ch in enumerate(clean):
        if ch == '{':
            depth[i] = cur
            stack.append(len(blocks))
            blocks.append([i, -1])
            cur += 1
        elif ch == '}':
            cur -= 1
            depth[i] = cur
            if stack:
                blocks[stack.pop()][1] = i
        else:
            depth[i] = cur
    return depth, [tuple(b) for b in blocks]


def members_of(body, lineof_body):
    """只收集类体第一层(depth==0)的成员，避免把方法内局部变量当成成员。
    返回 name -> [(line, signature)]，signature 用于区分合法重载与真冲突。"""
    res = {}
    dm = depth_map(body)
    for m in MEMBER_RE.finditer(body):
        if dm[m.start()] != 0:
            continue
        name = m.group('name')
        line = lineof_body + body[:m.start()].count('\n')
        if m.group('tail') == '(':
            end = body.find(')', m.end())
            sig = name + body[m.end() - 1:end + 1] if end > 0 else name + '()'
            sig = re.sub(r'\s+', '', sig)
        else:
            sig = name + ':' + m.group('type')
        res.setdefault(name, []).append((line, sig))
    return res


def check_hiding(f, clean, tname, body, sline, problems):
    """CS0108：自定义类继承 WinForms 基类时，声明同名成员会遮蔽基类成员。"""
    base = base_of(clean, tname)
    if base is None or base not in WINFORMS_BASES:
        return
    pool = hiding_members(base)
    dm = depth_map(body)
    for m in MEMBER_RE.finditer(body):
        if dm[m.start()] != 0:
            continue
        # 带 override 的是【合法覆写】（基类成员是 virtual），不是遮蔽 —— 不报 CS0108。
        # 例：FlatInput.Text 覆写 Panel.Text，把读写转发给内部 TextBox，语义不变。
        if re.search(r'\boverride\b', m.group(0)):
            continue
        name = m.group('name')
        if name in pool:
            ln = sline + body[:m.start()].count('\n')
            problems.append('%s:%d CS0108 HIDING -> %s.%s hides inherited %s.%s '
                            '(建议改名，避免基类语义被静默改变)'
                            % (f, ln, tname, name, base, name))


def _paren_header(s, pos):
    """判断 pos 处的声明是否位于 using/for/foreach/while/lock 的头部括号内。
    若是，则该变量作用域为"该语句"，不参与 CS0136 判定。"""
    depth = 0
    start = -1
    for i in range(pos, -1, -1):
        if s[i] == ')':
            depth += 1
        elif s[i] == '(':
            if depth == 0:
                start = i
                break
            depth -= 1
    if start < 0:
        return False
    # 若 `(` 与声明之间已有 `{`，说明声明位于括号内嵌套的块里（如 lambda 体），仍是块作用域
    return '{' not in s[start + 1:pos]


def check_shadowing(f, clean, tname, body, sline, problems):
    """CS0136：同一方法内，外层作用域的局部变量与"前面某个嵌套块内"的同名局部变量冲突。"""
    depth, blocks = build_blocks(body)
    decls = []   # (offset, name, block_id)

    def block_at(pos):
        best, bs = -1, -1
        for i, (s, e) in enumerate(blocks):
            if s < pos < e and s > bs:
                bs, best = s, i
        return best

    TYPES = ('var', 'int', 'string', 'object', 'bool', 'double', 'long', 'float',
             'char', 'byte', 'short', 'uint', 'decimal')
    hdr_decls = []          # (offset, name, span_start, span_end) —— 头部变量及其语句范围
    for hre in HEADER_VAR_RES:
        for m in hre.finditer(body):
            nm = m.group('name')
            if nm in TYPES:
                continue
            span = header_span(body, m.start('name'))
            if span is None:
                continue
            hdr_decls.append((m.start('name'), nm, span[0], span[1]))

    for m in LOCAL_DECL_RE.finditer(body):
        nm = m.group('name')
        if nm in TYPES:
            continue
        o = m.start('name')
        if _paren_header(body, o):
            # 头部变量：作用域是"该语句"，只与"语句范围内嵌套的同名头部变量"冲突
            span = header_span(body, o)
            if span:
                for ho, hn, hs, he in hdr_decls:
                    if hn == nm and ho != o and span[0] < ho < span[1]:
                        ln = sline + body[:ho].count('\n')
                        problems.append('%s:%d CS0136 SHADOWING -> 嵌套语句头部变量 "%s" 重名'
                                        % (f, ln, nm))
            continue
        bid = block_at(o)
        decls.append((o, nm, bid))
        # 块作用域变量声明在"同名头部变量"之前，且该头部变量落在本块范围内 → CS0136
        for ho, hn, hs, he in hdr_decls:
            if hn != nm or ho < o:
                continue
            if bid >= 0 and blocks[bid][0] < ho < blocks[bid][1]:
                ln = sline + body[:ho].count('\n')
                problems.append('%s:%d CS0136 SHADOWING -> 语句头部变量 "%s" 与先前声明的'
                                '外层局部变量同名' % (f, ln, nm))
                break

    byname = {}
    for off, nm, bid in decls:
        if bid >= 0:
            byname.setdefault(nm, []).append((off, bid))

    reported = set()
    for nm, lst in byname.items():
        for off_a, bid_a in lst:
            for off_b, bid_b in lst:
                if bid_a == bid_b:
                    continue
                sa, ea = blocks[bid_a]
                sb, eb = blocks[bid_b]
                # C# 的局部变量【声明空间是整个所在块】，所以同名局部变量只要出现在
                # 互为嵌套的两个块里，无论谁先谁后都是 CS0136 —— 顺序无关。
                #
                # 旧版这里多要求了一个 off_b > off_a（即"外层声明必须晚于内层声明"），
                # 于是把最常见的形态整个漏掉了：外层先声明、内层块里再声明同名变量。
                # 实测踩过：FlatCheck.OnPaint 里 var box（复选框方框，第 506 行）与
                # using 块内的 var box（文字框，第 547 行）—— 编译器报 CS0136，
                # 而本检查器给了 PASS。
                #
                # bid_b 是 bid_a 的祖先块（A 嵌在 B 里）→ 报内层那一处（off_a），
                # 这样行号与编译器指向的位置一致，便于直接对照修复。
                if sb < sa and ea < eb and off_a not in reported:
                    reported.add(off_a)
                    ln = sline + body[:off_a].count('\n')
                    problems.append('%s:%d CS0136 SHADOWING -> 局部变量 "%s" 在嵌套作用域重复声明'
                                    '（与外层同名变量冲突）' % (f, ln, nm))

    # 方法参数也是"外层局部作用域"，在其方法体内声明同名局部变量同样报 CS0136：
    #     void M(int x) { int x = 1; }   // CS0136
    # 这类错误非常容易在加参数/加临时变量时写出来，且编译前完全看不出来。
    index_by_start = {}
    for i, (s, e) in enumerate(blocks):
        index_by_start[s] = (i, e)
    for m in RE_METHOD_DECL.finditer(body):
        brace = m.end() - 1
        if brace < 0 or body[brace] != '{':
            continue
        hit = index_by_start.get(brace)
        if hit is None:
            continue
        _, bend = hit
        pnames = set()
        for p in m.group('params').split(','):
            ids = re.findall(r'[A-Za-z_]\w*', p)
            if ids:
                pnames.add(ids[-1])
        if not pnames:
            continue
        for d in LOCAL_DECL_RE.finditer(body, brace, bend):
            nm = d.group('name')
            if nm not in pnames:
                continue
            o = d.start('name')
            if o in reported:
                continue
            reported.add(o)
            ln = sline + body[:o].count('\n')
            problems.append('%s:%d CS0136 SHADOWING -> 局部变量 "%s" 与所在方法的参数同名'
                            % (f, ln, nm))


def check_arity(f, clean, type_sigs, problems):
    """CS1501/CS1729：调用本项目自己的方法时，实参个数与任何重载都不匹配。

    只检查 `Type.Method(...)` 形式（跨文件/跨类调用的风险所在），
    参数个数的上下界会考虑默认值参数与 params 数组。
    """
    CALL_RE = re.compile(
        r'\b(?P<t>' + '|'.join(sorted(type_sigs.keys())) + r')\.'
        r'(?P<m>[A-Za-z_]\w*)\s*\(')
    for m in CALL_RE.finditer(clean):
        t, name = m.group('t'), m.group('m')
        sigs = type_sigs.get(t, {}).get(name)
        if not sigs:
            continue
        argc = count_args(clean, m.end() - 1)
        if argc < 0:
            continue
        ok = False
        for lo, hi in sigs:
            if lo <= argc <= hi:
                ok = True
                break
        if not ok:
            ln = clean[:m.start()].count('\n') + 1
            ranges = ', '.join(
                ('%d' % lo) if lo == hi else ('%d~%s' % (lo, hi if hi < 999 else 'n'))
                for lo, hi in sorted(sigs))
            problems.append('%s:%d ARITY -> %s.%s(...) 传了 %d 个实参，但声明只接受 %s 个'
                            % (f, ln, t, name, argc, ranges))


def count_args(s, open_pos):
    """从 '(' 位置开始数顶层逗号，返回实参个数；解析失败返回 -1。"""
    depth, argc, content = 0, 0, False
    i = open_pos
    while i < len(s):
        c = s[i]
        if c in '([{':
            depth += 1
            if depth > 1:
                content = True
        elif c in ')]}':
            depth -= 1
            if depth == 0:
                break
        elif c == ',' and depth == 1:
            argc += 1
            content = True
        elif depth == 1 and not c.isspace():
            content = True
        i += 1
    if not content:
        return 0
    return argc + 1


def method_signatures(body):
    """收集类体第一层方法：name -> [(min_args, max_args)]"""
    res = {}
    dm = depth_map(body)
    for m in re.finditer(
            r'^[ \t]{4,}(?:[A-Za-z_]\w*\s+)*'
            r'(?P<type>[A-Za-z_][\w\.<>\[\]]*)\s+'
            r'(?P<name>[A-Za-z_]\w*)\s*\((?P<params>[^)]*)\)', body, re.M):
        if dm[m.start()] != 0:
            continue
        params = m.group('params').strip()
        if params == '':
            res.setdefault(m.group('name'), []).append((0, 0))
            continue
        parts = split_top(params)
        total = len(parts)
        optional = sum(1 for p in parts if '=' in p)
        is_params = any('params' in p.split() for p in parts)
        hi = 999 if is_params else total
        res.setdefault(m.group('name'), []).append((total - optional, hi))
    return res


def split_top(s):
    """按顶层逗号切分（忽略 <>, (), [] 内的逗号）。"""
    out, buf, d = [], '', 0
    for ch in s:
        if ch in '<([{':
            d += 1
        elif ch in '>)]}':
            d -= 1
        if ch == ',' and d == 0:
            out.append(buf.strip())
            buf = ''
        else:
            buf += ch
    if buf.strip():
        out.append(buf.strip())
    return out


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    files = ['Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs']
    problems = []

    all_types = {}
    file_bodies = {}
    type_sigs = {}

    for f in files:
        p = os.path.join(src, f)
        if not os.path.exists(p):
            problems.append('MISSING FILE: ' + p)
            continue
        raw = open(p, encoding='utf-8').read()
        clean = strip_noise(strip_inactive(raw))   # 先按 #if 折叠非活动分支，避免误报重复成员
        file_bodies[f] = clean

        # 4) 括号平衡
        lineof = lambda pos: clean[:pos].count('\n') + 1
        for op, cl, nm in (('{', '}', 'braces'), ('(', ')', 'parens'), ('[', ']', 'brackets')):
            d = clean.count(op) - clean.count(cl)
            if d != 0:
                problems.append('%s: unbalanced %s (%+d)' % (f, nm, d))

        # 5) C#6+ 语法
        for pat, desc in CS6_PATTERNS:
            for m in re.finditer(pat, clean):
                ln = clean[:m.start()].count('\n') + 1
                problems.append('%s:%d C#6+ SYNTAX -> %s' % (f, ln, desc))
        # 表达式体成员（排除 lambda 赋值）
        for m in EBO_RE.finditer(clean):
            seg = clean[max(0, m.start() - 120):m.end()]
            if LAMBDA_ASSIGN_RE.search(seg):
                continue
            ln = clean[:m.start()].count('\n') + 1
            problems.append('%s:%d C#6+ SYNTAX -> expression-bodied member (C#6)' % (f, ln))

        # 5b) 共享缓存 GDI+ 对象被 Dispose —— 本项目实踩过**两次**的坑，务必保留此检查。
        #     Theme.F/FB/Mono（字体）、Theme.Solid（刷子）、Theme.Hair（画笔）返回的都是
        #     【全局共享缓存实例】；把它放进 using，作用域结束时会释放该实例，而缓存字典里
        #     仍留着这个尸体 → 之后所有同色/同字号绘制都在已销毁的 GDI 句柄上进行 →
        #     GDI+ 抛 ArgumentException「参数无效」。WinForms 对 UserPaint 自绘控件绘制失败的
        #     兜底是把控件区域填成系统色并打叉 → 表现为「内容整片消失 / 满屏白块 + 红叉」。
        #     属运行期故障，编译器完全不报错，而且**延迟发作**（第一次用完全正常，之后全崩），
        #     只能靠本检查 + 实机日志 + Theme 里的存活探测三层兜住。
        #     两次事故：v2.0 首版 —— 字体（用户看到满屏白块）；
        #               1.0.1  —— 刷子，`Pg.OnPaintBackground` 里 `using (var b = Theme.Solid(Theme.Bg))`
        #                         使整页底色重绘每次都抛异常 → 滚动/切页后内容整片消失。
        for m in re.finditer(r'using\s*\([^)\n]*Theme\.(F|FB|Mono|Solid|Hair)\s*\(', clean):
            ln = clean[:m.start()].count('\n') + 1
            problems.append('%s:%d CACHED-OBJ-USING -> Theme.%s() 返回的是共享缓存实例，'
                            '放进 using 会被 Dispose，之后所有同色/同字号绘制抛 ArgumentException'
                            '（自绘控件会被填白打叉）；共享缓存只取用、不释放' % (f, ln, m.group(1)))

        # 5c) DllImport 命名参数拼写 —— 手写 P/Invoke 时极易写错（本项目实踩：
        #     写成 PreserveSafeHandle，正确的只有 PreserveSig）。这类错编译器只在编译时报
        #     CS0246，而沙箱不能编译 → 必须静态兜住。
        for m in re.finditer(r'\[DllImport\s*\((?P<args>[^)]*)\)\s*\]', clean):
            for nm in re.finditer(r'(?P<k>\w+)\s*=', m.group('args')):
                if nm.group('k') not in DLLIMPORT_NAMED:
                    ln = clean[:m.start('args') + nm.start()].count('\n') + 1
                    problems.append('%s:%d DLLIMPORT-BAD-NAMED-ARG -> %s 不是 DllImport 的命名参数'
                                    '（合法值：%s）' % (f, ln, nm.group('k'),
                                                     ', '.join(sorted(DLLIMPORT_NAMED))))

        # 5d) Regex.Replace 替换串里的 $N 后接数字会拼成 $NN —— 本项目实踩（2026-09 两次事故的根因）：
        #     SetConfigInt 的替换串写成 "$1" + value.ToString()，值为 0/1/2 时拼成 "$10"/"$11"/"$12"；
        #     .NET 把 $12 当成"第 12 号捕获组"，而模式里只有 1 个组 → 无效组号被原样输出成字面量
        #     → config.json 里出现 `$12,`，整份 JSON 解析失败、全部配置静默回默认（很难查）。
        #     正确写法是 "${1}"：花括号界定组号，后面直接接数字也不会被吞。
        for m in re.finditer(r'"\$\d+"', clean):
            ln = clean[:m.start()].count('\n') + 1
            problems.append('%s:%d REGEX-REPL-DOLLAR -> 替换串 %s 用了不带花括号的组引用；'
                            '后面拼接数字会变成 $NN（无效组号原样输出，写坏 JSON）。应改用 "${1}"'
                            % (f, ln, m.group()))

        # 5e) UI 线程上直接跑阻塞 I/O —— 本项目实踩（2026-09-20 用户反馈"性能优化页时不时卡死、
        #     什么都不显示、点不动"）。Program.GuardTick() 里全是网络 / WMI / 文件活儿：
        #       每拍跑网络哨兵（2 组 × 4 次 ping，单次超时 1500ms = 最坏 12 秒），
        #       每 5 拍（≈20s）跑一次 WMI 事件日志扫描（首次要激活 WMI 服务，慢机器上到秒级）。
        #     它原先被 sysTimer.Tick 在 **UI 线程**上直接调用 → 主线程被同步 I/O 占住 →
        #     WM_PAINT 饿死（整片空白）+ 点不动，而且**不抛任何异常、日志里什么都没有** ——
        #     这是最难查的一类故障（对比：GDI+ 那种至少还有 ArgumentException 可抓）。
        #     正确写法固定为 Ui.cs 的 GuardTickAsync()：ThreadPool + 重入闸。
        #     判据：每个调用点往前 800 字符内必须能看到 QueueUserWorkItem。
        if f == 'Ui.cs':
            for m in re.finditer(r'Program\.GuardTick\s*\(', clean):
                ln = clean[:m.start()].count('\n') + 1
                ctx = clean[max(0, m.start() - 800):m.start()]
                if 'QueueUserWorkItem' not in ctx:
                    problems.append('%s:%d UI-BLOCK-GUARDTICK -> Program.GuardTick() 出现在 UI 线程路径上；'
                                    '它内部有 ping（最坏 12s）与 WMI 事件日志扫描，会把主线程钉住'
                                    '（页面整片空白 + 点不动，且不产生任何异常/日志）。'
                                    '必须走 GuardTickAsync()（ThreadPool + 重入闸）' % (f, ln))

        # 1) 顶层类型收集
        for tname, body, sline in find_type_bodies(clean, lineof):
            all_types.setdefault(tname, []).append((f, sline))
            type_sigs[tname] = method_signatures(body)
            # 2) 真·重复成员（签名完全相同 = CS0102/CS0111；重载合法，不报）
            ms = members_of(body, sline)
            for mname, entries in ms.items():
                seen = {}
                for line, sig in entries:
                    seen.setdefault(sig, []).append(line)
                for sig, lns in seen.items():
                    if len(lns) > 1:
                        problems.append('%s: type %s -> DUPLICATE member "%s" at lines %s'
                                        % (f, tname, sig, lns))
            # 7) CS0108 遮蔽基类成员
            check_hiding(f, clean, tname, body, sline, problems)
            # 8) CS0136 局部变量在嵌套作用域重名
            check_shadowing(f, clean, tname, body, sline, problems)

    # 1) 顶层类型全局重名
    for tname, where in all_types.items():
        if len(where) > 1:
            problems.append('DUPLICATE TYPE %s at %s' % (tname, where))

    # 9) 调用参数个数（CS1501/CS1729）—— 需要先收齐所有类型的签名
    for f, clean in file_bodies.items():
        check_arity(f, clean, type_sigs, problems)

    # 6) long -> int 数组长度风险
    for f, clean in file_bodies.items():
        for m in re.finditer(r'new\s+(?:byte|char|int|string|bool|object|double|float)\s*\[\s*([^\]]+)\]', clean):
            expr = m.group(1).strip()
            ln = clean[:m.start()].count('\n') + 1
            if re.match(r'^\d+$', expr):
                continue
            if expr.endswith('.Length') or expr.endswith('.Count') or re.match(r'^\w+Only$', expr):
                continue
            if 'as int' in expr or re.match(r'^(int|short|byte)\s*[\(\w]', expr):
                continue
            if re.match(r'^\w+$', expr):  # 单一标识符，可能是 int 变量，暂不报警
                continue
            problems.append('%s:%d CHECK int-conversion -> new array[%s]' % (f, ln, expr))

    # 3) 跨文件符号引用解析
    type_members = {}
    for f, clean in file_bodies.items():
        lineof = lambda pos, c=clean: c[:pos].count('\n') + 1
        for tname, body, sline in find_type_bodies(clean, lineof):
            agg = type_members.setdefault(tname, set())
            for mname in members_of(body, sline):
                agg.add(mname)

    check_types = ['Program', 'Dlssg', 'Theme', 'Native', 'Config', 'NvDrs']
    ref_lines = {}
    for f, clean in file_bodies.items():
        for t in check_types:
            for m in re.finditer(r'\b' + t + r'\.([A-Za-z_]\w*)', clean):
                ref_lines.setdefault((t, m.group(1)), set()).add(f)

    # Cfg 是 Program 的静态字段（类型 Config），需别名解析
    cfg_members = type_members.get('Config', set())
    for (t, mem), where in sorted(ref_lines.items()):
        if t == 'Program' and mem == 'Cfg':
            continue                      # 合法字段，跳过
        pool = type_members.get(t, set())
        if mem not in pool:
            problems.append('UNRESOLVED %s.%s  (referenced in %s)' % (t, mem, ','.join(sorted(where))))

    print('=' * 72)
    print('files checked : %s' % ', '.join(files))
    print('top-level types: %d -> %s' % (len(all_types), ', '.join(sorted(all_types))))
    print('Program members: %d' % len(type_members.get('Program', set())))
    print('Dlssg members  : %d' % len(type_members.get('Dlssg', set())))
    print('Config members : %d' % len(cfg_members))
    print('=' * 72)
    if not problems:
        print('RESULT: PASS - no static issue found')
    else:
        print('RESULT: %d issue(s)' % len(problems))
        for p in problems:
            print('  - ' + p)
    return 0 if not problems else 1


if __name__ == '__main__':
    sys.exit(main())
