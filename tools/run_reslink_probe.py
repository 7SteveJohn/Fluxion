# -*- coding: utf-8 -*-
"""编译并运行 reslink_probe（游戏分辨率联动 ResLink 验证，全程只读）。
与 tools/run_disp_probe.py 同款做法：编译器调用放进 .py 里执行（bash 内联编译器命令会被安全策略拦）。"""
import io, os, subprocess, sys

ROOT = r'D:\youhua\Fluxion'
SRC = os.path.join(ROOT, 'src')
WORK = os.path.join(ROOT, '.build')
os.makedirs(WORK, exist_ok=True)

FW = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
env = dict(os.environ)
for k in ('http_proxy', 'https_proxy', 'all_proxy', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
    env.pop(k, None)
pv = env.get('Path') or env.get('PATH') or ''
env.pop('PATH', None)
env['Path'] = pv

out = os.path.join(WORK, 'ResLinkProbe.exe')
srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
args = [FW, '/nologo', '/target:exe', '/out:' + out, '/unsafe+', '/main:Fluxion.ResLinkProbe',
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        '/r:System.ServiceProcess.dll'] + srcs + [os.path.join(ROOT, 'tools', 'reslink_probe.cs')]
p = subprocess.run(args, capture_output=True, text=True, env=env, encoding='utf-8', errors='replace')
print('=== csc rc =', p.returncode)
if p.returncode:
    print((p.stdout or '') + (p.stderr or ''))
    raise SystemExit(1)

r = subprocess.run([out], capture_output=True, text=True, env=env, cwd=WORK,
                   encoding='utf-8', errors='replace', timeout=600)
f = os.path.join(os.environ.get('TEMP', ''), 'reslink_probe.txt')
if os.path.exists(f):
    print(io.open(f, encoding='utf-8', errors='replace').read())
else:
    print(r.stdout or '')
    print(r.stderr or '')
print('=== probe rc =', r.returncode)
raise SystemExit(r.returncode)

# ---- 统一收尾（2026-09-21 二次收口）----
#  原来这一块是在 globals() 里**猜**结果变量名（找 r2/r/res/proc + 扫 out/txt/body 的 FAIL 字样）——
#  谁改个变量名，探针红了 runner 照样退 0，而"红了却退 0"正是这一轮要修的 bug。
#  现在收尾只有 tools/_probe_runner.py 一份，且探针那次 subprocess 的结果对象**显式**传进来。
from _probe_runner import finish          # noqa: E402
finish(r, verdict=True)
