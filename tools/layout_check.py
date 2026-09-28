# -*- coding: utf-8 -*-
"""
布局越界校验（Ui.cs）

背景：这个项目的界面是纯手写绝对坐标，最典型的两类"UI 错位"靠编译器完全查不出来：
  1. 子控件的 y/h 超出父卡片高度 —— WinForms 会把子控件裁剪在父容器客户区内，
     表现是"这个按钮根本看不见"，而不是报错；
  2. 同一卡片内两个控件矩形相交 —— 表现是"文字压在一起"。

做法（纯文本静态分析，不依赖编译器）：
  · 按方法切分（变量名只在方法内复用，避免串味）；
  · 收集 `X = new Card()` 之类的类型表 + `X.Location/Size = new Point/Size(数字)` 的几何表；
  · 收集 `PARENT.Controls.Add(CHILD)` 与 `MakeCombo(PARENT, x, y, w, ...)` 的父子关系；
  · 对每个已知尺寸的子控件做"是否完全落在父卡片内"检查，再对同父兄弟做相交检查。

用法: python layout_check.py <src_dir>
退出码 0 = 干净，1 = 有可疑布局
"""
import os
import re
import sys

SKIP_METHODS = ('BuildTitleBar',)   # 标题栏是 Dock 布局 + 随窗体宽度计算的坐标，不适用本检查


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
        if c == '"':
            out[i] = ' '; i += 1
            while i < n:
                if text[i] == '\\':
                    out[i] = ' '
                    if i + 1 < n: out[i + 1] = ' '
                    i += 2; continue
                if text[i] == '"':
                    out[i] = ' '; i += 1; break
                if text[i] != '\n': out[i] = ' '
                i += 1
            continue
        i += 1
    return ''.join(out)


RE_NEW = re.compile(r'(?:var\s+)?(?P<v>\w+)\s*=\s*new\s+(?P<t>\w+)\s*\(\s*\)')
RE_LOC = re.compile(r'(?P<v>\w+)\.Location\s*=\s*new\s+Point\s*\(\s*(?P<x>-?\d+)\s*,\s*(?P<y>-?\d+)\s*\)')
RE_SIZE = re.compile(r'(?P<v>\w+)\.Size\s*=\s*new\s+Size\s*\(\s*(?P<w>-?\d+)\s*,\s*(?P<h>-?\d+)\s*\)')
RE_ADD = re.compile(r'(?P<p>\w+)\.Controls\.Add\s*\(\s*(?P<c>\w+)\s*\)')
RE_MAKECOMBO = re.compile(r'MakeCombo\s*\(\s*(?P<p>\w+)\s*,\s*(?P<x>\d+)\s*,\s*(?P<y>\d+)\s*,\s*(?P<w>\d+)')
RE_MAKELABEL = re.compile(r'(?P<v>\w+)\s*=\s*MakeLabel\s*\(\s*(?P<p>\w+)\s*,')
RE_METHOD = re.compile(r'^[ \t]*(?:public|private|protected|internal|static|override|void|[\w<>\[\],\s]+?)\s+'
                       r'(?P<n>\w+)\s*\([^;{]*\)\s*$', re.M)

COMBO_H = 28          # MakeCombo 内部统一 Size = (w, 28)
CHECK_H = 22          # FlatCheck 统一高度
CARD_MARGIN = 0       # 允许贴边（卡片内边距靠肉眼，不做硬性要求）


def method_ranges(clean):
    """返回 [(name, start, end)]，end 为下一个方法签名之前。"""
    heads = [(m.group('n'), m.start()) for m in RE_METHOD.finditer(clean)]
    out = []
    for i, (n, s) in enumerate(heads):
        e = heads[i + 1][1] if i + 1 < len(heads) else len(clean)
        out.append((n, s, e))
    return out


def fragments(body):
    """按分号把方法体切成语句片段（保留行号）。"""
    base = 0
    for line in body.split('\n'):
        for frag in line.split(';'):
            yield base, frag
        base += len(line) + 1


def analyse(name, body):
    types, geom, parent_of, cards = {}, {}, {}, {}
    for off, frag in fragments(body):
        m = RE_NEW.search(frag)
        if m:
            types[m.group('v')] = m.group('t')
        m = RE_LOC.search(frag)
        if m:
            geom.setdefault(m.group('v'), {})['x'] = int(m.group('x'))
            geom.setdefault(m.group('v'), {})['y'] = int(m.group('y'))
        m = RE_SIZE.search(frag)
        if m:
            geom.setdefault(m.group('v'), {})['w'] = int(m.group('w'))
            geom.setdefault(m.group('v'), {})['h'] = int(m.group('h'))
        m = RE_ADD.search(frag)
        if m:
            parent_of[m.group('c')] = m.group('p')
        m = RE_MAKECOMBO.search(frag)
        if m:
            parent_of['<combo@%d>' % off] = m.group('p')
            geom['<combo@%d>' % off] = {'x': int(m.group('x')), 'y': int(m.group('y')),
                                        'w': int(m.group('w')), 'h': COMBO_H}
        m = RE_MAKELABEL.search(frag)
        if m:
            parent_of[m.group('v')] = m.group('p')

    for v, t in types.items():
        g = geom.get(v)
        if t == 'Card' and g and all(k in g for k in 'xywh'):
            cards[v] = g

    problems = []

    # 1) 子控件是否落在父卡片内
    for child, parent in parent_of.items():
        if parent not in cards:
            continue
        g = geom.get(child)
        if not g or not all(k in g for k in 'xywh'):
            continue
        c = cards[parent]
        # 只对卡片自己的直接子控件做"卡片内"判定；卡片下面还有面板再做一层
        ov_r = (g['x'] + g['w']) - (c['w'] - CARD_MARGIN)
        ov_b = (g['y'] + g['h']) - (c['h'] - CARD_MARGIN)
        if ov_r > 2 or ov_b > 2:
            what = []
            if ov_r > 2: what.append('右溢出 %dpx' % ov_r)
            if ov_b > 2: what.append('底溢出 %dpx' % ov_b)
            problems.append('%s: %s 超出卡片 %s 的%s (卡片 %dx%d, 子 %d,%d %dx%d)'
                            % (name, child, parent, '/'.join(what),
                               c['w'], c['h'], g['x'], g['y'], g['w'], g['h']))

    # 2) 同父兄弟矩形相交（只对两边都有明确尺寸的控件）
    by_parent = {}
    for child, parent in parent_of.items():
        g = geom.get(child)
        if g and all(k in g for k in 'xywh'):
            by_parent.setdefault(parent, []).append((child, g))
    for parent, lst in by_parent.items():
        for i in range(len(lst)):
            for j in range(i + 1, len(lst)):
                a, ga = lst[i]
                b, gb = lst[j]
                ax2, ay2 = ga['x'] + ga['w'], ga['y'] + ga['h']
                bx2, by2 = gb['x'] + gb['w'], gb['y'] + gb['h']
                ox = min(ax2, bx2) - max(ga['x'], gb['x'])
                oy = min(ay2, by2) - max(ga['y'], gb['y'])
                if ox > 2 and oy > 2:
                    problems.append('%s: %s 与 %s 在 %s 内相交 %dx%dpx (重叠区域)'
                                    % (name, a, b, parent, ox, oy))
    return problems


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    p = os.path.join(src, 'Ui.cs')
    clean = strip_noise(open(p, encoding='utf-8').read())
    all_problems = []
    checked = 0
    for name, s, e in method_ranges(clean):
        if not name.startswith('BuildPage') and name not in ('BuildSidebar', 'BuildContent', 'BuildFooter'):
            continue
        if name in SKIP_METHODS:
            continue
        checked += 1
        all_problems += analyse(name, clean[s:e])

    print('=' * 72)
    print('layout blocks checked : %d' % checked)
    print('=' * 72)
    if not all_problems:
        print('RESULT: PASS - no control escapes its card, no sibling overlap')
    else:
        print('RESULT: %d layout suspect(s)' % len(all_problems))
        for x in all_problems:
            print('  - ' + x)
    return 0 if not all_problems else 1


if __name__ == '__main__':
    sys.exit(main())
