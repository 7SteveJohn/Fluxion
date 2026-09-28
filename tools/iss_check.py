#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
iss_check.py - Inno Setup .iss 脚本静态校验（不依赖 ISCC，可在沙箱里跑）

用法:
    python tools/iss_check.py installer/Fluxion.iss [项目根目录]

查这些（每一条都对应真实会踩的坑）:
  1. BOM            —— 缺 BOM 时中文会变乱码（Inno 按本地 ANSI 读非 ASCII 行）
  2. 行尾格式       —— 统一 CRLF
  3. 段名合法性     —— [Setup] [Files] ... 拼错会直接报 unknown section
  4. 指令行引号成对 —— Inno 里字符串内的 " 要写成 ""，奇数个 = 解析错位
  5. 续行位置       —— 反斜杠续行的支持范围有限，出现就提示复核
  6. Parameters 反解—— 模拟 Inno 的 "" 反转义，把真实命令行打出来人工核验
  7. Source 存在性  —— [Files] 的源文件必须真实存在（相对 .iss 所在目录）
  8. Tasks 引用     —— [Icons]/[Run] 里引用的任务名必须在 [Tasks] 中定义
  9. 关键值一致性   —— AppMutex / 计划任务名 必须与 C# 源码中的字符串一致
 10. 提权守卫       —— postinstall 的 [Run] 启动 requireAdministrator 的 exe 时
                      必须带 shellexec，否则降权执行会报 code 740
 11. 自证           —— 若某段存在却一条都没解析出来，直接判 FAIL（防空壳通过）

退出码 0 = 通过，1 = 有问题。
"""
import fnmatch
import os
import re
import sys

VALID_SECTIONS = {
    'setup', 'types', 'components', 'tasks', 'dirs', 'files', 'icons', 'ini',
    'installdelete', 'languages', 'messages', 'custommessages', 'registry',
    'run', 'uninstallrun', 'uninstalldelete', 'code',
}

KEY_RE = re.compile(r'([A-Za-z]\w*)\s*:')


def split_directives(s):
    """把一行拆成 [(key, raw_value, quoted)]。Inno 的字符串里 "" 表示一个字面引号。"""
    out, i = [], 0
    while True:
        m = KEY_RE.search(s, i)
        if not m:
            break
        key = m.group(1)
        j = m.end()
        while j < len(s) and s[j] == ' ':
            j += 1
        if j < len(s) and s[j] == '"':
            k = j + 1
            buf = []
            while k < len(s):
                if s[k] == '"':
                    if k + 1 < len(s) and s[k + 1] == '"':
                        buf.append('""')
                        k += 2
                        continue
                    break
                buf.append(s[k])
                k += 1
            out.append((key, ''.join(buf), True))
            i = k + 1
        else:
            k = s.find(';', j)
            if k < 0:
                k = len(s)
            out.append((key, s[j:k].strip(), False))
            i = k + 1
    return out


def unescape_inno(s):
    """Inno 字符串里 "" 表示一个双引号。"""
    out, i = [], 0
    while i < len(s):
        if s[i] == '"' and i + 1 < len(s) and s[i + 1] == '"':
            out.append('"')
            i += 2
        else:
            out.append(s[i])
            i += 1
    return ''.join(out)


def expand(s, defines):
    return re.sub(r'\{#(\w+)\}', lambda m: defines.get(m.group(1), m.group(0)), s)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    iss = sys.argv[1]
    root = sys.argv[2] if len(sys.argv) > 2 else os.path.dirname(os.path.dirname(os.path.abspath(iss)))
    if not os.path.isfile(iss):
        print('MISSING: ' + iss)
        return 1

    problems, info, warnings = [], [], []
    raw = open(iss, 'rb').read()

    if raw[:3] != b'\xef\xbb\xbf':
        problems.append('缺少 UTF-8 BOM：Inno 会按本地 ANSI 解析，中文会乱码')
    else:
        info.append('BOM: OK')
    crlf, lf = raw.count(b'\r\n'), raw.count(b'\n')
    if lf and crlf != lf:
        problems.append('行尾不统一 (CRLF %d / 换行总数 %d)' % (crlf, lf))
    else:
        info.append('行尾: CRLF x%d' % crlf)

    txt = raw.decode('utf-8-sig')
    defines = {}
    for m in re.finditer(r'^\s*#define\s+(\w+)\s+"([^"]*)"', txt, re.M):
        defines[m.group(1)] = m.group(2)

    lines = txt.split('\n')
    sections, cur = [], None
    rows = []            # (lineno, section, [(key, raw, quoted)...])
    for i, l in enumerate(lines, 1):
        s = l.rstrip('\r')
        st = s.strip()
        m = re.match(r'^\s*\[(\w+)\]\s*$', s)
        if m:
            cur = m.group(1).lower()
            sections.append(m.group(1))
            if cur not in VALID_SECTIONS:
                problems.append('第 %d 行: 未知段名 [%s]' % (i, m.group(1)))
            continue
        if not st or st.startswith(';') or st.startswith('#'):
            continue
        if st.endswith('\\'):
            warnings.append('第 %d 行: 反斜杠续行（[%s]）—— 请确认 Inno 支持该段续行' % (i, cur))
        if s.count('"') % 2:
            problems.append('第 %d 行: 双引号个数为奇数（%d）→ Inno 会解析错位: %s'
                            % (i, s.count('"'), st[:70]))
        rows.append((i, cur, split_directives(st)))
    info.append('段: ' + ', '.join('[' + s + ']' for s in sections))

    # ---------- 7. [Files] Source 存在性 ----------
    issdir = os.path.dirname(os.path.abspath(iss))
    nsrc = 0
    for ln, sec, ds in rows:
        if sec != 'files':
            continue
        for k, v, q in ds:
            if k.lower() == 'source':
                nsrc += 1
                p = os.path.normpath(os.path.join(issdir, expand(v, defines)))
                # Inno 的 Source 支持通配：`dir\*`、`dir\*.dll`、`dir\*.*`。
                # 通配必须按"目录下有没有匹配项"判定，不能拿带 * 的字面量去 os.path.exists
                # —— 否则整目录打包（分享版内置插件那种）会被误报"源文件不存在"。
                if '*' in p or '?' in p:
                    base = os.path.dirname(p)
                    mask = os.path.basename(p)
                    if mask in ('*', '*.*'):
                        ok = os.path.isdir(base) and any(os.scandir(base))
                    else:
                        ok = os.path.isdir(base) and any(fnmatch.fnmatch(n, mask) for n in os.listdir(base))
                    if not ok:
                        problems.append('第 %d 行: [Files] 通配源无匹配 -> %s' % (ln, p))
                elif not os.path.exists(p):
                    problems.append('第 %d 行: [Files] 源文件不存在 -> %s' % (ln, p))
                break
    if 'Files' in sections and nsrc == 0:
        problems.append('[Files] 段存在却解析不出任何 Source —— 校验器失效，非脚本无错')
    info.append('[Files] Source 条目: %d 条，存在性已核' % nsrc)

    # ---------- 8. Tasks 引用 ----------
    tasknames = set()
    for ln, sec, ds in rows:
        if sec == 'tasks':
            for k, v, q in ds:
                if k.lower() == 'name':
                    tasknames.add(v.strip())
    ntaskref = 0
    for ln, sec, ds in rows:
        if sec not in ('icons', 'run', 'registry', 'uninstallrun'):
            continue
        for k, v, q in ds:
            if k.lower() == 'tasks':
                for t in re.split(r'[\s,]+', v.strip()):
                    if not t:
                        continue
                    ntaskref += 1
                    if t not in tasknames:
                        problems.append('第 %d 行: 引用了未定义的任务名 "%s"（[Tasks] 里没有）' % (ln, t))
    info.append('[Tasks] 定义 %d 个，被引用 %d 次' % (len(tasknames), ntaskref))

    # ---------- 9. 关键值一致性 ----------
    mutex, task = defines.get('AppMutex'), defines.get('AppTaskName')
    srcs = []
    for d in ('src', '.'):
        p = os.path.join(root, d)
        if os.path.isdir(p):
            srcs += [os.path.join(p, f) for f in os.listdir(p) if f.endswith('.cs')]
    if not srcs:
        problems.append('找不到 C# 源码，无法核对 AppMutex / 计划任务名')
    else:
        blob = ''.join(open(p, encoding='utf-8', errors='replace').read() for p in srcs)
        if mutex and ('"' + mutex + '"') not in blob:
            problems.append('AppMutex=%s 在 C# 源码里找不到同名字符串' % mutex)
        else:
            info.append('AppMutex 与源码一致: %s' % mutex)
        if task and task not in blob:
            problems.append('计划任务名 %s 在 C# 源码里找不到（应用内开关会与安装器脱钩）' % task)
        else:
            info.append('计划任务名与源码一致: %s' % task)

    # ---------- 6. Parameters 反解 ----------
    print('=== [Run] / [UninstallRun] 实际命令行（模拟 Inno 转义 + 宏展开） ===')
    ncmd = 0
    nentry = 0
    for ln, sec, ds in rows:
        if sec not in ('run', 'uninstallrun'):
            continue
        fname = [v for k, v, q in ds if k.lower() == 'filename']
        flags = [v for k, v, q in ds if k.lower() == 'flags']
        params = [v for k, v, q in ds if k.lower() == 'parameters']
        cmd = unescape_inno(params[0]) if params else ''
        cmd = expand(cmd, defines).replace('{app}', '<APPDIR>').replace('{sys}', '<SYSDIR>')
        exe = expand(fname[0], defines) if fname else '(no Filename)'
        print('  L%-4d %s' % (ln, exe))
        nentry += 1
        if cmd:
            ncmd += 1
            print('        args: %s' % cmd)
        if flags:
            print('        flags: %s' % flags[0])
    # 解析失败 = 一条条目都没认出来。条目存在但没有 Parameters 是合法的
    # （例如只启动 exe、"立即运行"那一项），不能算校验器失效。
    if nentry == 0 and ('Run' in sections or 'UninstallRun' in sections):
        problems.append('存在 [Run]/[UninstallRun] 却解析不出任何条目 —— 校验器失效')

    # ---------- 10. 提权守卫 ----------
    appexe = expand('{#AppExe}', defines) if 'AppExe' in defines else ''
    nguard = 0
    for ln, sec, ds in rows:
        if sec != 'run':
            continue
        flags = [v for k, v, q in ds if k.lower() == 'flags']
        fname = [v for k, v, q in ds if k.lower() == 'filename']
        if not flags or 'postinstall' not in flags[0]:
            continue
        f = expand(fname[0], defines) if fname else ''
        if appexe and appexe in f:
            nguard += 1
            if 'shellexec' not in flags[0]:
                problems.append('第 %d 行: postinstall 启动 %s 却没有 shellexec —— '
                                'postinstall 是降权执行的，会报 CreateProcess code 740' % (ln, appexe))
    info.append('postinstall 启动守卫: 命中 %d 处%s'
                % (nguard, '（OK，带 shellexec）' if nguard else ''))

    print()
    print('=== 信息 ===')
    for x in info:
        print('  ' + x)
    if warnings:
        print('=== 提示 ===')
        for x in warnings:
            print('  ~ ' + x)
    print()
    if problems:
        print('=== 问题 %d 条 ===' % len(problems))
        for x in problems:
            print('  !! ' + x)
        print()
        print('FAIL')
        return 1
    print('PASS')
    return 0


if __name__ == '__main__':
    sys.exit(main())
