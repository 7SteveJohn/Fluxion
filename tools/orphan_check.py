#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
orphan_check.py —— 界面层的"孤儿控件"检查（新布局架构的失效模式）

背景（2026-09-11 换布局机制后新增）：
    新 UI 的纵向位置由 Sec 的光标布局推导，唯一的失效模式变成了
    「控件建好了但没挂上去」——编译通过、运行不报错，只是用户看不到。
    这是"看不见的错"在新架构里的对应形态，必须用脚本兜住。

检查四类：
    1. 局部变量里 new 出来的控件/组件，之后**再没被引用过**（建了就扔）。
    2. 分区 Sec 没有挂到页面上（p.Add(sec) / pg.Add(sec) 缺失），
       或者一个分区里一条内容都没加（只剩标题的空壳）。
    3. 类字段（控件/组件类型）**只声明、从未赋值** —— 控件压根不存在。
    4. 方法**定义了却全文件没有任何引用** —— 入口没接线，功能静默消失。

用法：python orphan_check.py <src_dir>
退出码 0 = 通过，1 = 有可疑项。
"""
import os
import re
import sys

FILES = ['Ui.cs']

# 建出来就必须挂上去的类型（自绘控件 + 常用 WinForms 控件 + 组件）
CTRL_TYPES = [
    'Sec', 'Pg', 'FlatBtn', 'Bar', 'Spark',
    'Label', 'Button', 'ComboBox', 'TextBox', 'CheckBox', 'RadioButton',
    'ListView', 'RichTextBox', 'Panel', 'FlowLayoutPanel', 'TableLayoutPanel',
    'GroupBox', 'TabControl', 'StatusStrip', 'ToolStrip',
    'NotifyIcon', 'ContextMenuStrip', 'ToolStripMenuItem', 'Timer',
    'BackgroundWorker', 'Form',
]

DECL = re.compile(
    r'\b(?:var|' + '|'.join(CTRL_TYPES) + r')\s+([A-Za-z_]\w*)\s*=\s*new\s+(' + '|'.join(CTRL_TYPES) + r')\s*[\(<]'
)

# 类字段声明：`FlatBtn btnAddGame, btnDelGame, btnIgnored;`（可带修饰符，无初始化式）
FIELD_DECL = re.compile(
    r'(?m)^[ \t]*(?:(?:public|private|protected|internal)\s+)?(?:static\s+)?(?:readonly\s+)?'
    r'(' + '|'.join(CTRL_TYPES) + r')\s+'
    r'([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*;'
)

# 框架回调：由 WinForms / 运行时按名字调，文件里出现一次是正常的
KNOWN_HOOKS = set("""Main Dispose WndProc OnPaint OnPaintBackground OnResize
OnResizeBegin OnResizeEnd OnLoad OnShown OnClosing OnFormClosing OnClosed
OnActivated OnDeactivate OnMouseDown OnMouseMove OnMouseUp OnMouseWheel
OnMouseEnter OnMouseLeave OnKeyDown OnKeyUp OnKeyPress OnDragEnter OnDragDrop
OnEnter OnLeave OnValidating OnLayout""".split())


def strip_noise(text):
    """去掉注释与字符串（保留行号）：单遍扫描，字符串优先。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out.append(' ')
                i += 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                out.append('\n' if text[i] == '\n' else ' ')
                i += 1
            out.append('  ')
            i += 2
            continue
        if c == '"':
            out.append(' ')
            i += 1
            while i < n:
                if text[i] == '\\':
                    out.append(' ')
                    i += 2
                    continue
                if text[i] == '"':
                    break
                out.append('\n' if text[i] == '\n' else ' ')
                i += 1
            out.append(' ')
            i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def methods(text):
    """返回 [(名字, 起始行, 方法体)]，按 { } 平衡切分。

    返回类型白名单末尾允许可空标记 `?`（`bool? Foo()`）—— 否则这类方法整条漏扫，
    DEAD-METHOD 检查会静默失效（2026-09-13 实踩，与 static_check 同一处坑）。
    """
    res = []
    for m in re.finditer(r'(?:^|\n)\s*(?:static\s+|public\s+|private\s+|protected\s+|internal\s+)*'
                         r'(?:void|int|string|bool|Label|ListView|Panel|IEnumerable<[^>]+>)\??\s+([A-Za-z_]\w*)\s*\([^;{]*\)\s*\{', text):
        i = text.index('{', m.end() - 1)
        depth = 0
        for j in range(i, len(text)):
            if text[j] == '{':
                depth += 1
            elif text[j] == '}':
                depth -= 1
                if depth == 0:
                    res.append((m.group(1), text[:m.start()].count('\n') + 1, text[i:j + 1]))
                    break
    return res


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    problems = []
    stats = {'methods': 0, 'locals': 0, 'secs': 0, 'fields': 0, 'methods_all': 0}
    found = 0

    for f in FILES:
        p = os.path.join(src, f)
        if not os.path.exists(p):
            continue
        found += 1
        text = strip_noise(open(p, encoding='utf-8-sig').read())
        for name, sline, body in methods(text):
            if not name.startswith('Build'):
                continue
            stats['methods'] += 1
            for m in DECL.finditer(body):
                var, typ = m.group(1), m.group(2)
                stats['locals'] += 1
                after = body[m.end():]
                # 变量名在声明之后是否还被引用（赋值、挂载、传参都算）
                if not re.search(r'\b' + re.escape(var) + r'\b', after):
                    ln = sline + body[:m.start()].count('\n')
                    problems.append('%s:%d  ORPHAN -> %s %s 建好后从未被使用（不可见的功能）'
                                    % (f, ln, typ, var))
                    continue
                if typ != 'Sec':
                    continue
                stats['secs'] += 1
                # 分区必须挂到页面上，否则整块内容都不会显示
                if not re.search(r'\.Add\(\s*' + re.escape(var) + r'\s*[,\)]', body):
                    ln = sline + body[:m.start()].count('\n')
                    problems.append('%s:%d  EMPTY-PAGE -> 分区 %s("%s") 没有 Add 到任何页面'
                                    % (f, ln, var, '...'))
                # 分区里至少要有一条内容（Row/Pair/Body/Block/Buttons，含 Block2/Block3 这类带数字的重载）
                if not re.search(re.escape(var) + r'\s*\.\s*(?:Row|Pair|Body|Block|Buttons)\d*\s*[(<]', after):
                    ln = sline + body[:m.start()].count('\n')
                    problems.append('%s:%d  EMPTY-SEC -> 分区 %s 一条内容都没加（只剩标题的空壳）'
                                    % (f, ln, var))

        # ── 检查 3：类字段声明了却从未赋值 ────────────────────────────────
        # 2026-09-13 实踩：Ui.cs 里 `FlatBtn btnAddGame, btnDelGame, btnIgnored;`
        #   —— btnIgnored 只声明、从没 new、也没进任何 Buttons(...)，
        #   于是「忽略清单」按钮在界面上根本不存在（配套的 ShowIgnoredDialog 也没人调）。
        #   编译通过、其余四个校验器全 PASS、功能静默消失 —— 用户看到的是"日志让我点一个不存在的按钮"。
        for m in FIELD_DECL.finditer(text):
            typ, names = m.group(1), m.group(2)
            for nm in [x.strip() for x in names.split(',')]:
                stats['fields'] += 1
                # 声明式里没有 '='，所以在全文任意位置找到赋值即可
                if re.search(r'\b' + re.escape(nm) + r'\s*=(?!=)', text):
                    continue
                ln = text[:m.start()].count('\n') + 1
                problems.append('%s:%d  DEAD-FIELD -> %s %s 只声明、从未赋值（控件不存在，功能静默消失）'
                                % (f, ln, typ, nm))

        # ── 检查 4：方法定义了却全文件没有任何引用 ────────────────────────
        for nm, ln, _body in methods(text):
            if nm in KNOWN_HOOKS or nm.startswith('get_') or nm.startswith('set_'):
                continue
            stats['methods_all'] += 1
            # 用"总出现次数 ≤ 1"判定：只有定义、没有任何调用/传参/委托引用
            if len(re.findall(r'\b' + re.escape(nm) + r'\b', text)) > 1:
                continue
            problems.append('%s:%d  DEAD-METHOD -> %s() 定义了但全文件没有任何引用（入口没接线）'
                            % (f, ln, nm))

    print('=' * 72)
    print('Build* methods : %d      local controls : %d      Sec : %d' %
          (stats['methods'], stats['locals'], stats['secs']))
    print('fields scanned : %d      methods scanned: %d' %
          (stats['fields'], stats['methods_all']))
    print('=' * 72)
    # ★ 一个文件都没读到 = 参数传错（多半传了项目根而不是 src/）。
    #   旧版这时会打印 PASS 并返回 0 —— "读到 0 个控件"和"没有孤儿控件"是两回事，
    #   把前者当后者就是把"什么都没检查"伪装成"检查通过"。
    if found == 0:
        print('ERROR: no source file found under "%s" (expect %s).' % (src, ', '.join(FILES)))
        print('       did you pass the project root instead of the src directory?')
        return 2
    if problems:
        print('RESULT: %d suspect item(s)' % len(problems))
        for x in problems:
            print('  - ' + x)
        return 1
    print('RESULT: PASS - no orphan control, no empty section')
    return 0


if __name__ == '__main__':
    sys.exit(main())
