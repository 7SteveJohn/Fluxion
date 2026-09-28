# -*- coding: utf-8 -*-
"""编译并运行 knobs_probe：把 Program.Knobs() 的实际内容导出成 Markdown。
只读探针，不写注册表、不改配置。产物落在 .build\\，不碰正式 exe。"""
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

out = os.path.join(WORK, 'KnobsProbe.exe')
OUT_MD = os.path.join(ROOT, '取值细调-全量档位表-20260919.md')
srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
args = [FW, '/nologo', '/target:exe', '/out:' + out, '/unsafe+', '/main:Fluxion.KnobsProbe',
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        '/r:System.ServiceProcess.dll'] + srcs + [os.path.join(ROOT, 'tools', 'knobs_probe.cs')]
p = subprocess.run(args, capture_output=True, text=True, env=env, encoding='gbk', errors='replace')
print('=== csc rc =', p.returncode)
if p.returncode:
    print((p.stdout or '') + (p.stderr or ''))
    raise SystemExit(1)

r = subprocess.run([out, OUT_MD], capture_output=True, text=True, env=env, cwd=WORK,
                   encoding='gbk', errors='replace', timeout=600)
print(r.stdout or '')
print(r.stderr or '')
print('=== probe rc =', r.returncode)
if r.returncode == 0:
    print('导出文件:', OUT_MD, os.path.getsize(OUT_MD), 'B')

# ---- 统一收尾（2026-09-21 二次收口）----
#  原来这一块是在 globals() 里**猜**结果变量名（找 r2/r/res/proc + 扫 out/txt/body 的 FAIL 字样）——
#  谁改个变量名，探针红了 runner 照样退 0，而"红了却退 0"正是这一轮要修的 bug。
#  现在收尾只有 tools/_probe_runner.py 一份，且探针那次 subprocess 的结果对象**显式**传进来。
from _probe_runner import finish          # noqa: E402
finish(r, verdict=False)
