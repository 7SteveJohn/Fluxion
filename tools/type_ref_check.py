# -*- coding: utf-8 -*-
"""
type_ref_check.py — 静态类型成员引用校验（补齐 static_check.py 的盲区）

背景：static_check.py 只校验 Program / Dlssg / Config 三个类型的 `Type.Member` 引用。
一旦往别的静态类里加成员（本项目实际发生过：Theme 里加了 S/SF/Prep/Fit/TextTop/…），
这些引用就完全绕过校验，直到 csc 报 CS0117 / CS1061 才发现。

本脚本对指定类型做「定义 vs 引用」双向比对：
  · 定义 = 类体内出现的字段 / 属性 / 方法 / 常量 / 嵌套类型名
  · 引用 = 全项目里的 `Type.Name`
  · 引用存在但定义没有 -> 报错（就是编译期会炸的那类）

用法:
    python type_ref_check.py <src_dir> [Type1 Type2 ...]
    不带类型名时使用 DEFAULT_TYPES。

退出码 0 = 全部解析成功。
"""
import io
import os
import re
import sys

DEFAULT_TYPES = ['Theme', 'Native', 'Program', 'Config', 'Dlssg']

DEFAULT_FILES = ['Core.cs', 'Pack.cs', 'Dlssg.cs', 'Ui.cs']

# 方法名/属性名允许出现在两条正则里，这里统一清洗掉注释与字符串，
# 避免把注释里提到的名字算成定义。
RE_MEMBER = re.compile(
    r'\b(?:public|private|protected|internal)\s+'
    r'(?:(?:static|readonly|const|sealed|override|virtual|abstract|new|unsafe|extern|partial)\s+)*'
    r'[A-Za-z_][\w<>,\[\]\.\?]*\s+'
    r'([A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*(?=[\(;=]|\{)'
)

# 事件 / 委托字段等无修饰符但形如 `Name;` 的枚举成员
RE_BARE = re.compile(r'^\s*([A-Za-z_]\w*)\s*[=;,]\s*$', re.M)

RE_NESTED_TYPE = re.compile(r'\b(?:class|struct|enum|interface)\s+([A-Za-z_]\w*)')


def strip_noise(text):
    """去掉注释与字符串字面量，用【等长空白】替换以保持行号与偏移不变。

    必须是单遍扫描、且每次先判断字符串再判断注释 —— 反过来（先用正则删注释）会
    把字符串里的 `//` `/*` 当成真注释，一路吞掉后面的代码。本脚本第一版就是这么写的，
    结果 Core.cs 里的 Program / Dlssg 两个类型直接被吞成"未找到"（假阴性）。

    实现与 static_check.py 的 strip_noise 等价。
    """
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
        # verbatim 字符串 @"..."
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
        # 普通字符串
        if c == '"':
            out[i] = ' '
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
        # 字符字面量
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


def type_body(text, name):
    """返回 name 类型的花括号体（含嵌套），找不到返回 None。"""
    m = re.search(r'\b(?:class|struct|interface|enum)\s+' + re.escape(name) + r'\b', text)
    if not m:
        return None
    i = text.find('{', m.end())
    if i < 0:
        return None
    depth = 0
    for j in range(i, len(text)):
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                return text[i:j + 1]
    return None


def members_of(body):
    """从类型体里抽出所有可能作为 `Type.X` 被访问的名字。"""
    names = set()
    for m in RE_MEMBER.finditer(body):
        names.add(m.group(1))
    for m in RE_NESTED_TYPE.finditer(body):
        names.add(m.group(1))
    for m in RE_BARE.finditer(body):
        names.add(m.group(1))
    return names


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else '.'
    types = sys.argv[2:] or DEFAULT_TYPES

    texts = {}
    for f in DEFAULT_FILES:
        p = os.path.join(src, f)
        if os.path.exists(p):
            texts[f] = strip_noise(io.open(p, encoding='utf-8-sig').read())
    if not texts:
        print('no source files found under ' + src)
        return 2

    problems = []
    print('=' * 72)
    for tname in types:
        body = None
        for t in texts.values():
            body = type_body(t, tname)
            if body:
                break
        if not body:
            print('%-10s  <type not found - skipped>' % tname)
            continue

        pool = members_of(body)
        refs = {}
        for f, t in texts.items():
            for m in re.finditer(r'\b' + re.escape(tname) + r'\.([A-Za-z_]\w*)', t):
                refs.setdefault(m.group(1), []).append(
                    (f, t[:m.start()].count('\n') + 1))
        bad = sorted(n for n in refs if n not in pool)
        print('%-10s  members=%-4d distinct refs=%-4d unresolved=%d'
              % (tname, len(pool), len(refs), len(bad)))
        for n in bad:
            for f, ln in refs[n]:
                problems.append('%s:%d  %s.%s' % (f, ln, tname, n))

    print('=' * 72)
    if problems:
        print('RESULT: FAIL - %d unresolved member reference(s):' % len(problems))
        for p in problems:
            print('  ' + p)
        return 1
    print('RESULT: PASS - every Type.Member reference resolves')
    return 0


if __name__ == '__main__':
    sys.exit(main())
