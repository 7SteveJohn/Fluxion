# -*- coding: utf-8 -*-
"""编译并运行 Junk（备份统计与可逆回收）回归。

沙盒：每个时间戳一个新目录，放在 %TEMP%\\gb_junk_probe\\ 下。

⚠ 为什么删除动作只在沙盒里跑：这个探针会真把目录送进回收站（那正是它要验的行为）。
  真实数据目录里有 1.48 GB 的用户备份，Program.DataDir 已用反射改指到沙盒，
  探针看不到、也就动不了真目录 —— 这一条靠 section 0 的两个断言钉住。
"""
import datetime
import os
import subprocess
import sys

ROOT = r'D:\youhua\Fluxion'
SRC = os.path.join(ROOT, 'src')
WORK = os.path.join(ROOT, '.build')
os.makedirs(WORK, exist_ok=True)
FW = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

Sand = os.path.join(os.environ.get('TEMP', '.'), 'gb_junk_probe',
                    datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))


def clean_env():
    env = dict(os.environ)
    for k in ('http_proxy', 'https_proxy', 'all_proxy',
              'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
        env.pop(k, None)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None)
    env['Path'] = pv
    return env


def main():
    os.makedirs(Sand, exist_ok=True)
    exe = os.path.join(WORK, 'JunkProbe.exe')
    srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
    args = [FW, '/nologo', '/target:exe', '/out:' + exe, '/unsafe+',
            '/main:Fluxion.JunkProbe',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
            '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
            '/r:System.ServiceProcess.dll'] \
        + srcs + [os.path.join(ROOT, 'tools', 'junk_probe.cs')]
    env = clean_env()
    p = subprocess.run(args, capture_output=True, text=True, env=env,
                       encoding='utf-8', errors='replace')
    print('=== csc rc =', p.returncode)
    if p.returncode:
        print((p.stdout or '') + (p.stderr or ''))
        return 1
    r = subprocess.run([exe, Sand], capture_output=True, text=True, env=env,
                       cwd=WORK, encoding='utf-8', errors='replace', timeout=600)
    # 探针自己的输出是 UTF-8；控制台可能是 GBK，所以同时落一份文件
    log = os.path.join(WORK, 'junk_probe.txt')
    with open(log, 'w', encoding='utf-8') as f:
        f.write((r.stdout or '') + '\n' + (r.stderr or ''))
    try:
        sys.stdout.buffer.write((r.stdout or '').encode('utf-8', 'replace'))
        sys.stdout.buffer.flush()
    except Exception:
        print(r.stdout or '')
    if r.stderr:
        print(r.stderr)
    print('=== probe rc =', r.returncode)
    print('（完整输出也存了一份：' + log + '，沙盒 ' + Sand + '）')
    from _probe_runner import finish
    finish(r, verdict=True)


if __name__ == '__main__':
    sys.exit(main())
