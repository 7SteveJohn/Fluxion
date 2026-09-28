# -*- coding: utf-8 -*-
"""cover_refetch.py —— 编译并运行横版封面重抓探针（用法：python tools\\cover_refetch.py）"""
import os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')
WORK = os.path.join(ROOT, '.build')
FW = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
PROBE = os.path.join(ROOT, 'tools', 'cover_refetch.cs')
OUT = os.path.join(WORK, 'CoverRefetch.exe')

def main():
    os.makedirs(WORK, exist_ok=True)
    env = dict(os.environ)
    for k in ('http_proxy', 'https_proxy', 'all_proxy', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
        env.pop(k, None)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None); env['Path'] = pv          # PATH 大小写重复会让 csc 解析到坏值
    srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
    args = [FW, '/nologo', '/target:exe', '/out:' + OUT, '/unsafe+', '/main:CoverRefetch',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'] + srcs + [PROBE]
    p = subprocess.run(args, capture_output=True, text=True, env=env, encoding='utf-8', errors='replace')
    if p.returncode != 0:
        print((p.stdout or '') + (p.stderr or '')); sys.exit(1)
    r = subprocess.run([OUT] + sys.argv[1:], capture_output=True, text=True, env=env, cwd=WORK,
                       encoding='utf-8', errors='replace', timeout=900)
    print(r.stdout or '')
    if r.stderr: print(r.stderr)

if __name__ == '__main__':
    main()
