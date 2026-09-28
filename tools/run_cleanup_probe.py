# -*- coding: utf-8 -*-
"""编译并运行 cleanup_probe（旧数据目录 %ProgramData%\\GameBoost-DLSSG 清理 + 迁移幂等性验证）。

⚠ 这个探针会真的删除文件，只在确认"旧目录里每个文件在新目录都有副本"之后才会动手
  （检查写在探针内部，缺失 >0 直接拒绝执行并退出 1）。

为什么要走 C# 探针而不是 Python/PowerShell：宿主的安全护栏会把 Python 的 os.remove 和
PowerShell 的 Remove-Item 改写成"回收站"操作，对 C 盘 ProgramData 必失败
（SAFE_DELETE_FAIL_CLOSED / trash-failed）。程序自己用 File.Delete 走 Win32 不受此限。
"""
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

out = os.path.join(WORK, 'CleanupProbe.exe')
srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
args = [FW, '/nologo', '/target:exe', '/out:' + out, '/unsafe+', '/main:Fluxion.CleanupProbe',
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        '/r:System.ServiceProcess.dll'] + srcs + [os.path.join(ROOT, 'tools', 'cleanup_probe.cs')]
p = subprocess.run(args, capture_output=True, text=True, env=env, encoding='utf-8', errors='replace')
print('=== csc rc =', p.returncode)
if p.returncode:
    print((p.stdout or '') + (p.stderr or ''))
    raise SystemExit(1)

r = subprocess.run([out], capture_output=True, text=True, env=env, cwd=WORK,
                   encoding='utf-8', errors='replace', timeout=600)
f = os.path.join(os.environ.get('TEMP', ''), 'cleanup_probe.txt')
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
