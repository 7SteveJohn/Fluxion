# -*- coding: utf-8 -*-
"""临时：编译并运行 tools/_px_analyze.cs（像素扫描器）。用完可删。"""
import os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, '.build')
CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
EXE = os.path.join(WORK, 'pxa.exe')


def build():
    os.makedirs(WORK, exist_ok=True)
    env = dict(os.environ)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None); env['Path'] = pv
    args = [CSC, '/nologo', '/target:exe', '/out:' + EXE, '/platform:x64',
            '/r:System.dll', '/r:System.Drawing.dll',
            os.path.join(ROOT, 'tools', '_px_analyze.cs')]
    r = subprocess.run(args, capture_output=True, text=True, env=env,
                       encoding='utf-8', errors='replace')
    out = (r.stdout or '') + (r.stderr or '')
    if out.strip():
        print(out[:3000])
    return r.returncode == 0


if __name__ == '__main__':
    if not os.path.exists(EXE) and not build():
        sys.exit(1)
    r = subprocess.run([EXE] + sys.argv[1:], cwd=WORK)
    sys.exit(r.returncode)
