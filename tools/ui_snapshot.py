# -*- coding: utf-8 -*-
r"""ui_snapshot —— 离屏渲染视觉验收（与源码同编，可访问 internal 类型）

为什么需要它：静态校验 + 编译通过 != 观感正确。2.6.0 就是靠它才发现
「复选框文字被省略号截断」「浅色下输入框与白卡片分不清」这类只有看图才知道的问题。

用法：
    python tools/ui_snapshot.py build                  # 只编译
    python tools/ui_snapshot.py light real 3           # 浅色 · 游戏库页
    python tools/ui_snapshot.py dark  real 0           # 深色 · 仪表盘
    python tools/ui_snapshot.py light showcase         # 样式表（所有改动过的控件）
    python tools/ui_snapshot.py light covers           # 封面多路径下载实测

输出：Fluxion/ui-snapshots/*.png
页面索引：0 仪表盘 1 性能优化 2 帧生成 3 游戏库 4 实时监控 5 说明 6 设置

注意：必须把 src/*.cs 与 tools/ui_snapshot.cs 一起交给 csc 并指定 /main:StyleProbe，
      这样探针才能访问 internal 的 Theme / HeadBox / GameCard / RoundField。
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')
WORK = os.path.join(ROOT, '.build')
CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
EXE = os.path.join(WORK, 'StyleProbe.exe')


def build():
    os.makedirs(WORK, exist_ok=True)
    env = dict(os.environ)
    for k in ('http_proxy', 'https_proxy', 'all_proxy',
              'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
        env.pop(k, None)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None)
    env['Path'] = pv          # 清掉重复的 PATH/PATH，否则 csc 找不到依赖
    srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
    srcs.append(os.path.join(ROOT, 'tools', 'ui_snapshot.cs'))
    args = [CSC, '/nologo', '/target:winexe', '/main:StyleProbe', '/out:' + EXE,
            '/platform:x64', '/optimize+', '/unsafe+',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Management.dll',
            '/r:System.Core.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'] + srcs
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


def stale():
    """任一源文件比 StyleProbe.exe 新 → 需要重编。exe 不在也算。"""
    if not os.path.exists(EXE):
        return True
    e = os.path.getmtime(EXE)
    for f in [os.path.join(SRC, x) for x in
              ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')] + \
             [os.path.join(ROOT, 'tools', 'ui_snapshot.cs')]:
        try:
            if os.path.getmtime(f) > e:
                return True
        except OSError:
            return True
    return False


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(0)
    if sys.argv[1] == 'build':
        sys.exit(0 if build() else 1)
    #  以前是"exe 不在才编译" —— 改了 src 再跑，拿的是上一个 build 的结果，
    #  报告看着像当前代码的、其实不是（2026-09-21 加页码守卫时就被它骗过一次：
    #  守卫明明没生效，原因是根本没重编）。改成任一源文件更新就重编。
    if stale():
        if not build():
            sys.exit(1)
    r = subprocess.run([EXE] + sys.argv[1:], cwd=WORK)
    sys.exit(r.returncode)
