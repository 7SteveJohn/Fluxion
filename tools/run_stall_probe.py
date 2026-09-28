# -*- coding: utf-8 -*-
r"""run_stall_probe —— 编译并运行 UI 卡顿探针（全量源码 + tools/ui_stall_probe.cs）

输出：Fluxion/ui-snapshots/stall_report.txt
用法：python tools\run_stall_probe.py [b]      # b = 只跑 C 段（跳过 A/B 计时）
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')
WORK = os.path.join(ROOT, '.build')
CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
EXE = os.path.join(WORK, 'StallProbe.exe')
REPORT = os.path.join(ROOT, 'ui-snapshots', 'stall_report.txt')


def build():
    os.makedirs(WORK, exist_ok=True)
    env = dict(os.environ)
    for k in ('http_proxy', 'https_proxy', 'all_proxy',
              'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
        env.pop(k, None)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None)
    env['Path'] = pv
    srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
    srcs.append(os.path.join(ROOT, 'tools', 'ui_stall_probe.cs'))
    args = [CSC, '/nologo', '/target:winexe', '/main:StallProbe', '/out:' + EXE,
            '/platform:x64', '/optimize+', '/unsafe+',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Management.dll',
            '/r:System.Core.dll', '/r:System.IO.Compression.dll',
            '/r:System.IO.Compression.FileSystem.dll'] + srcs
    r = subprocess.run(args, capture_output=True, text=True, env=env,
                       encoding='utf-8', errors='replace')
    out = (r.stdout or '') + (r.stderr or '')
    if out.strip():
        print(out[:4000])
    if r.returncode != 0:
        print('COMPILE FAILED')
        return False
    print('OK ->', EXE, os.path.getsize(EXE), 'bytes')
    return True


if __name__ == '__main__':
    if not build():
        sys.exit(1)
    if os.path.exists(REPORT):
        os.remove(REPORT)
    r = subprocess.run([EXE] + sys.argv[1:], cwd=WORK, timeout=600)
    print('exit', r.returncode)
    if os.path.exists(REPORT):
        print('---- stall_report.txt ----')
        with open(REPORT, encoding='utf-8') as fp:
            print(fp.read())
    else:
        print('!! 报告文件没生成')

# ---- 统一收尾（2026-09-21 二次收口）----
#  原来这一块是在 globals() 里**猜**结果变量名（找 r2/r/res/proc + 扫 out/txt/body 的 FAIL 字样）——
#  谁改个变量名，探针红了 runner 照样退 0，而"红了却退 0"正是这一轮要修的 bug。
#  现在收尾只有 tools/_probe_runner.py 一份，且探针那次 subprocess 的结果对象**显式**传进来。
from _probe_runner import finish          # noqa: E402
finish(r, verdict=False)
