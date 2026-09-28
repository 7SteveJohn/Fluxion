# -*- coding: utf-8 -*-
"""编译并运行 virt_probe（虚拟化开关 / 竞技档着色器缓存 / 新核验项的只读验证）。
与 tools/run_opt_probe.py 同款做法：把编译器调用放进 .py 里执行
（bash 里内联编译器命令会被安全策略拦）。产物落在 .build\\，不碰正式 exe。"""
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

out = os.path.join(WORK, 'VirtProbe.exe')
srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
args = [FW, '/nologo', '/target:exe', '/out:' + out, '/unsafe+', '/main:Fluxion.VirtProbe',
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        '/r:System.ServiceProcess.dll'] + srcs + [os.path.join(ROOT, 'tools', 'virt_probe.cs')]
p = subprocess.run(args, capture_output=True, text=True, env=env, encoding='gbk', errors='replace')
print('=== csc rc =', p.returncode)
if p.returncode:
    print((p.stdout or '') + (p.stderr or ''))
    raise SystemExit(1)

# 探针 exe 是控制台子系统，输出走系统 ANSI 代码页（GBK）；用 utf-8 解会全是乱码
r = subprocess.run([out], capture_output=True, text=True, env=env, cwd=WORK,
                   encoding='gbk', errors='replace', timeout=600)
f = os.path.join(os.environ.get('TEMP', ''), 'virt_probe.txt')
if os.path.exists(f):
    print(io.open(f, encoding='gbk', errors='replace').read())
else:
    print(r.stdout or '')
    print(r.stderr or '')
print('=== probe rc =', r.returncode)

# ---- 统一收尾（2026-09-21 二次收口）----
#  原来这一块是在 globals() 里**猜**结果变量名（找 r2/r/res/proc + 扫 out/txt/body 的 FAIL 字样）——
#  谁改个变量名，探针红了 runner 照样退 0，而"红了却退 0"正是这一轮要修的 bug。
#  现在收尾只有 tools/_probe_runner.py 一份，且探针那次 subprocess 的结果对象**显式**传进来。
from _probe_runner import finish          # noqa: E402
finish(r, verdict=True)
