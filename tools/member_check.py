# -*- coding: utf-8 -*-
"""
成员访问校验（针对自定义控件的 CS1061 风险）—— 作用域感知版

思路：
  1. 计算每个字符处的花括号深度，得出"最内层块"区间；
  2. 只把 "X v = new T(...)" / "var v = new T(...)" 当作声明，并记录其所属块；
     以及强制转换式声明 "v = ... as T" / "v = (T)..."（目标类型即确定类型）；
  3. 对每次 v.Member 访问，从最内层块向外层查找同名声明来决定 v 的真实类型；
     找不到声明（说明 v 是字段/参数）就跳过，不臆测；
  4. 类型命中本项目自定义控件时，把 Member 与
     "类体第一层声明的成员 ∪ WinForms 继承成员白名单" 比对，缺失即报。
用法: python member_check.py <src_dir>
"""
import re
import sys
import os

OUR_TYPES = ['Card', 'FlatBtn', 'NavBtn', 'StatBar', 'Chart', 'RowItem',
             'DlssgGame', 'DlssgRec', 'StatusItem']

# 基类是下面这些 WinForms 类型的自定义类，会自动加入 OUR_TYPES（含继承链的传递闭包）。
# 之前这里是纯手写清单，新加的 FlatCombo / FlatCheck / FlatInput 没登记进去，
# 结果这三个类的所有调用点都绕过了校验 —— 手写清单必然会漏，改成按基类推导。
WINFORMS_BASES = set("""
Control UserControl Panel GroupBox Button CheckBox RadioButton ComboBox TextBox
RichTextBox Label ListBox ListView TreeView ProgressBar TrackBar Form SplitContainer
TabControl DataGridView PictureBox FlowLayoutPanel TableLayoutPanel
""".split())

CLASS_BASE_RE = re.compile(r'\bclass\s+(?P<n>\w+)\s*:\s*(?P<b>[\w\.]+)')

INHERITED = set("""
Name Text Size Location Width Height Top Left Right Bottom X Y ClientSize Bounds
Visible Enabled BackColor ForeColor Font Padding Margin Dock Anchor AutoSize
Parent Controls Tag Cursor UseWaitCursor AllowDrop Capture
Invalidate Refresh Update PerformLayout CreateGraphics Dispose SuspendLayout
ResumeLayout BringToFront SendToBack Focus Select FindForm
PointToClient PointToScreen Scale SetBounds DrawToBitmap Show ShowDialog Hide
GetType Equals GetHashCode ToString
Click DoubleClick Paint MouseDown MouseMove MouseUp MouseEnter MouseLeave
MouseWheel MouseCaptureChanged DragEnter DragDrop DragLeave DragOver
KeyDown KeyPress KeyUp Resize Layout TextChanged
SelectedIndex SelectedIndexChanged SelectedItem SelectedValue SelectedValueChanged
Items ItemHeight ItemCount DrawMode DrawItem DropDownStyle DropDownWidth
MaxDropDownItems DropDown FlatStyle IntegralHeight Sorted DataSource ValueMember
Checked CheckState CheckedChanged CheckStateChanged ThreeState FlatAppearance
TextAlign BorderStyle ReadOnly Multiline ScrollBars WordWrap Lines SelectionStart
SelectionLength AppendText AutoScroll AutoScrollMinSize AutoScrollPosition
HorizontalScroll VerticalScroll DisplayRectangle
ProgressBar ListBox DialogResult AcceptButton CancelButton FormBorderStyle
StartPosition MaximizeBox MinimizeBox TopMost ShowInTaskbar Opacity Icon Region
AutoScaleMode Language ControlBox HelpButton WindowState SizeGripStyle
IsDisposed IsHandleCreated Handle Invoke BeginInvoke
Value Maximum Minimum Step Percent LargeChange SmallChange TickFrequency
""".split())


def strip_noise(text):
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '; i += 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            out[i] = out[i + 1] = ' '; i += 2
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n': out[i] = ' '
                i += 1
            for k in (i, i + 1):
                if k < n: out[k] = ' '
            i += 2
            continue
        if c == '"' or c == "'":
            q = c
            out[i] = ' '; i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n: out[i + 1] = ' '
                    i += 2; continue
                if text[i] == q:
                    out[i] = ' '; i += 1; break
                if text[i] != '\n': out[i] = ' '
                i += 1
            continue
        i += 1
    return ''.join(out)


def build_blocks(clean):
    """返回 (depth[], blocks[])；blocks[i] = (start, end) 表示第 i 个 '{' 对应块的区间。"""
    depth = [0] * len(clean)
    d = 0
    stack = []
    blocks = []
    for i, ch in enumerate(clean):
        if ch == '{':
            depth[i] = d
            stack.append(len(blocks))
            blocks.append([i, -1])
            d += 1
        elif ch == '}':
            d -= 1
            depth[i] = d
            if stack:
                blocks[stack.pop()][1] = i
        else:
            depth[i] = d
    return depth, [tuple(b) for b in blocks]


def innermost_chain(pos, blocks):
    """返回包含 pos 的块索引链，最内层在前。"""
    chain = [i for i, (s, e) in enumerate(blocks) if s < pos < e]
    chain.sort(key=lambda i: -blocks[i][0])
    return chain


def find_type_bodies(clean):
    types = {}
    pat = re.compile(
        r'^(?P<ind>[ \t]*)(?:(?:public|internal|private|protected)\s+)?'
        r'(?:static\s+|sealed\s+|abstract\s+|unsafe\s+|partial\s+)*'
        r'(?:class|struct|enum|interface)\s+(?P<name>\w+)[^\n{;]*\n?\s*\{', re.M)
    for m in pat.finditer(clean):
        if len(m.group('ind').expandtabs(4)) != 4:
            continue
        brace = clean.index('{', m.start('name'))
        depth, j = 0, brace
        while j < len(clean):
            if clean[j] == '{': depth += 1
            elif clean[j] == '}':
                depth -= 1
                if depth == 0: break
            j += 1
        types[m.group('name')] = (brace + 1, j)
    return types


def depth_map(body):
    d, cur = [], 0
    for ch in body:
        if ch == '{': d.append(cur); cur += 1
        elif ch == '}': cur -= 1; d.append(cur)
        else: d.append(cur)
    return d


DECL_ANY = re.compile(
    r'(?P<mods>(?:(?:public|internal|private|protected|static|readonly|const|volatile|'
    r'new|override|virtual|sealed|abstract|extern|unsafe|async|partial|event)\s+)*)'
    r'(?P<type>[A-Za-z_][\w\.]*(?:<[^<>]*>)?(?:\[\])*)\s+'
    # 名字后面允许再跟一个泛型参数表：`public T Block<T>(...)` 的方法名是 Block，
    # 漏掉这一段会让这类方法整个从成员表里消失（会把合法调用误报成 CS1061）。
    r'(?P<name>[A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*(?P<tail>[\(=;\{])')


def class_members(body):
    dm = depth_map(body)
    names = set()
    for m in DECL_ANY.finditer(body):
        if dm[m.start()] != 0:
            continue
        names.add(m.group('name'))
    return names


# 类体顶层的"只声明"字段：`Bar barCpu, barMem, barGpu;` / `Spark spCpu, spGpu;`
# ★ 这是本项目最大的一块盲区：控件字段几乎都在一个方法里 new、在另一个方法里用，
#   而旧版工具只解析"同一条块链内的局部变量"，于是跨方法访问全部被静默跳过。
FIELD_RE = re.compile(
    r'(?:^|[;}\n])\s*'
    r'(?:(?:public|private|protected|internal|static|readonly|volatile|new)\s+)*'
    r'(?P<type>[A-Za-z_]\w*)(?:<[^<>]*>)?(?P<arr>(?:\[\])*)\s+'
    r'(?P<names>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*'
    r'(?:=[^;{}]*)?;', re.M)


def class_fields(clean, start, end, ours):
    """返回 {字段名: 类型}，只收类型属于 ours 的非数组字段。

    数组字段要跳过：`Pg[] pages` 的元素类型是 Pg，但 pages 本身是数组，
    `pages.Length` 合法 —— 若按 Pg 校验就会误报（已经踩过一次）。
    """
    body = clean[start:end]
    dm = depth_map(body)
    out = {}
    for m in FIELD_RE.finditer(body):
        if dm[m.start()] != 0:
            continue
        if m.group('arr'):
            continue
        t = m.group('type')
        if t not in ours:
            continue
        for nm in m.group('names').split(','):
            nm = nm.strip()
            if nm:
                out[nm] = t
    return out


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    files = ['Core.cs', 'Pack.cs', 'Dlssg.cs', 'Ui.cs']

    cls_members = {}
    clean_all = {}
    type_ranges = {}
    for f in files:
        p = os.path.join(src, f)
        if not os.path.exists(p):
            continue
        clean = strip_noise(open(p, encoding='utf-8').read())
        clean_all[f] = clean
        type_ranges[f] = []
        for tname, (s, e) in find_type_bodies(clean).items():
            cls_members[tname] = class_members(clean[s:e])
            type_ranges[f].append((tname, s, e))

    # 按基类推导 OUR_TYPES（传递闭包：A : B、B : Panel 时 A 也算自定义控件）
    ours = set(OUR_TYPES)
    all_src = '\n'.join(clean_all.values())
    grown = True
    while grown:
        grown = False
        for m in CLASS_BASE_RE.finditer(all_src):
            n, b = m.group('n'), m.group('b').split('.')[-1]
            if n in ours:
                continue
            if b in WINFORMS_BASES or b in ours:
                ours.add(n)
                grown = True
    print('auto-detected custom controls: %s' % ', '.join(sorted(ours - set(OUR_TYPES))))

    NEW_RE = re.compile(r'new\s+(?P<t>[A-Za-z_]\w*)\s*\(')
    # 强制转换式中"目标类型"就是确定类型，可以直接登记变量:
    #   `v = ... as T`   与   `v = (T)...`
    CAST_DECLS = [
        re.compile(r'\b([A-Za-z_]\w*)\s*=\s*[^;{}\n]{0,160}?\bas\s+([A-Za-z_]\w*)'),
        re.compile(r'\b([A-Za-z_]\w*)\s*=\s*\(\s*([A-Za-z_]\w*)\s*\)\s*[^;{}\n]'),
    ]
    DECL_VAR = re.compile(
        r'(?:^|[;{}\(\)\s])'                        # 语句边界
        r'(?:(?:var)|(?:[A-Za-z_][\w\.<>\[\]]*))\s+'
        r'(?P<v>[A-Za-z_]\w*)\s*=\s*$', re.M)

    # 类字段类型表（依赖 ours，所以放在基类推导之后建）
    field_maps = {}
    for f, clean in clean_all.items():
        for tname, s, e in type_ranges.get(f, []):
            field_maps[(f, tname)] = class_fields(clean, s, e, ours)

    def enclosing_type(f, pos):
        for tname, s, e in type_ranges.get(f, []):
            if s <= pos <= e:
                return tname
        return None

    checked = 0
    checked_field = 0
    problems = []
    bound_stats = {}

    for f, clean in clean_all.items():
        depth, blocks = build_blocks(clean)
        # 收集声明: offset -> (varname, type)
        decls = []
        for m in NEW_RE.finditer(clean):
            t = m.group('t')
            if t not in ours:
                continue
            # 向前找变量名
            head = clean[max(0, m.start() - 80):m.start()]
            mm = re.search(r'([A-Za-z_]\w*)\s*=\s*$', head)
            if not mm:
                continue
            decls.append((m.start(), mm.group(1), t))
            bound_stats[t] = bound_stats.get(t, 0) + 1

        # 类型来自强制转换的声明：`var s = l.Tag as Sec;` / `var s = (Sec)o;`
        # ★ 这一族以前完全没登记，是漏掉过真 bug 的坑：`s.Resync()` 在 Sec 上并不存在，
        #   静态检查全绿，直到真编译器报 CS1061 才暴露。转换目标即字面类型，无推断误差。
        for rx in CAST_DECLS:
            for m in rx.finditer(clean):
                v, t = m.group(1), m.group(2)
                if t not in ours:
                    continue
                decls.append((m.start(), v, t))
                bound_stats[t] = bound_stats.get(t, 0) + 1

        # 按 (块, 名字) 归组
        decl_by_block = {}
        for off, v, t in decls:
            ch = innermost_chain(off, blocks)
            bid = ch[0] if ch else -1
            decl_by_block.setdefault(bid, {}).setdefault(v, []).append((off, t))

        for m in re.finditer(r'\b([A-Za-z_]\w*)\.([A-Za-z_]\w*)', clean):
            v, mem = m.group(1), m.group(2)
            if v in ('System', 'Convert', 'File', 'Path', 'Directory', 'Math', 'String',
                     'Environment', 'Encoding', 'DateTime', 'TimeSpan', 'Path2', 'Registry',
                     'Application', 'MessageBox', 'Color', 'Point', 'Size', 'Font', 'FontStyle',
                     'Graphics', 'Rectangle', 'Text', 'Console', 'Thread', 'Process',
                     'AppDomain', 'Object', 'Enum', 'Array', 'List', 'Dictionary', 'StringBuilder'):
                continue
            p = m.start()
            t = None
            for bid in innermost_chain(p, blocks):
                tbl = decl_by_block.get(bid, {})
                if v in tbl:
                    cands = [x for x in tbl[v] if x[0] <= p] or tbl[v]
                    t = cands[-1][1]
                    break
            if t is None:
                # 局部作用域找不到 -> 退到"类字段"：控件字段基本都是 this.xxx 形态
                tn = enclosing_type(f, p)
                if tn is not None:
                    ft = field_maps.get((f, tn), {}).get(v)
                    if ft is not None:
                        t = ft
                        checked_field += 1
            if t is None:
                continue
            checked += 1
            if mem in cls_members.get(t, set()) or mem in INHERITED:
                continue
            ln = clean[:p].count('\n') + 1
            problems.append('%s:%d  %s.%s   (var %s : %s)  -> CS1061 risk' % (f, ln, v, mem, v, t))

    print('=' * 72)
    # ★ 一个文件都没读到 = 参数传错（多半传了项目根而不是 src/）。
    #   旧版这时会打印 PASS 并返回 0："读到 0 个控件访问"与"没有可疑访问"是两回事，
    #   把前者当后者就是把"什么都没检查"伪装成"检查通过"。
    if not clean_all:
        print('ERROR: no source file found under "%s" (expect %s).' % (src, ', '.join(files)))
        print('       did you pass the project root instead of the src directory?')
        return 2
    print('our-control instantiations: %s' % ', '.join('%s x%d' % (k, v) for k, v in sorted(bound_stats.items())))
    fld = {}
    for mp in field_maps.values():
        for k, v in mp.items():
            fld[k] = v
    print('control fields typed     : %d  (%s)' % (
        len(fld), ', '.join('%s:%s' % (k, fld[k]) for k in sorted(fld))))
    print('member accesses resolved : %d   [局部变量链 %d / 控件字段 %d]' % (
        checked, checked - checked_field, checked_field))
    print('=' * 72)
    if not problems:
        print('RESULT: PASS - every member access on our controls resolves')
    else:
        print('RESULT: %d suspect access(es)' % len(problems))
        for x in sorted(set(problems)):
            print('  - ' + x)
    return 0 if not problems else 1


if __name__ == '__main__':
    sys.exit(main())
