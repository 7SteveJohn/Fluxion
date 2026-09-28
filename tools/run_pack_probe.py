# -*- coding: utf-8 -*-
"""插件包导入（Pack.cs）的回归自检：造沙盒 → 编译探针 → 跑 → 报结果。

为什么"造"包而不是直接用作者的原包：
  · 识别判据是 文件名 + 体积窗口 + INI 内容特征 —— 三者都能用合成内容精确复现；
  · 真实包加起来 950 MB，每次复制/解压太慢；
  · 合成包的体积是**精确控制**的，改判据时一眼能看出是哪一档被动了。
同时另外喂两份**真实发布包**（只读、不解压）验证判据没跑偏 —— 合成与真实都对得上才算过。

两个环境约束（踩过的坑）：
  · 本机安全钩子会拦"批量删除"和大文件删除 → 这个脚本**不删任何东西**，
    沙盒每次用一个新的时间戳目录，放在 %TEMP%\\gb_pack_probe\\ 下（临时目录本就是一次性垃圾）；
  · 大体积占位直接写进 zip（零数据 deflate 后只有几 KB），不在磁盘上留 400 MB 的中间目录。
"""
import datetime
import os
import subprocess
import sys
import tempfile
import zipfile

ROOT = r'D:\youhua\Fluxion'
SRC = os.path.join(ROOT, 'src')
TOOLS = os.path.join(ROOT, 'tools')
WORK = os.path.join(ROOT, '.build')
FRAMES = r'D:\youhua\帧生成方案'
os.makedirs(WORK, exist_ok=True)

# 实物量出来的体积（改这里等于改判据）
SZ032 = 29975840            # 0.3.2 默认包 version.dll
SZ032_ALT = {               # 0.3.2「供替换注入」的 5 个备选（体积各不相同，每个单独签过名）
    'd3d12.dll': 29976352, 'dbghelp.dll': 29993760, 'dinput8.dll': 29974816,
    'dxgi.dll': 29976352, 'winmm.dll': 29987104,
}
SZ030 = 17529120            # 0.3.0 默认包 version.dll
SZ030_ALT = {
    'd3d12.dll': 17529632, 'dbghelp.dll': 17547040, 'dinput8.dll': 17528608,
    'dxgi.dll': 17529632, 'winmm.dll': 17540896,
}
SZ_LEGACY = 15667520        # 旧版 dlssg_for_sm86（0.2.x）
# 运行库的体积不参与识别（只按文件名归位），所以占位用小体积：
# 落盘测试要真复制这些文件，59 MB 的 nvngx_dlss.dll 会让每次探针多搬 100+ MB。
SZ_RUNTIME_BIG = 4 * 1024 * 1024
SZ_RUNTIME_SMALL = 2 * 1024 * 1024


def clean_env():
    env = dict(os.environ)
    for k in ('http_proxy', 'https_proxy', 'all_proxy',
              'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
        env.pop(k, None)
    pv = env.get('Path') or env.get('PATH') or ''
    env.pop('PATH', None)
    env['Path'] = pv
    return env


def sparse_file(path, size):
    """造一个体积精确的占位文件（内容无所谓 —— 识别只看体积）。"""
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    with open(path, 'wb') as f:
        f.write(b'\0' * size)


def zip_sparse(z, arcname, size):
    """往 zip 里塞一个体积精确的条目（内存里造，不落磁盘）。"""
    z.writestr(arcname, b'\0' * size, compress_type=zipfile.ZIP_DEFLATED, compresslevel=1)


def find_real_pack(pattern):
    """在发布包根目录下按前缀找真实包（0.3.5 / 0.3.2 / 0.3.0）的目录。"""
    if not os.path.isdir(FRAMES):
        return None
    for name in os.listdir(FRAMES):
        d = os.path.join(FRAMES, name)
        if not os.path.isdir(d) or not name.startswith(pattern):
            continue
        subs = [s for s in os.listdir(d) if os.path.isdir(os.path.join(d, s))]
        for want in ('默认version注入', '供替换注入'):
            for s in subs:
                if want in s:
                    return os.path.join(d, s)
        if subs:
            return os.path.join(d, subs[0])
    return None


def real_ini(pattern):
    p = find_real_pack(pattern)
    if p is None:
        return ''
    f = os.path.join(p, 'dlssg_sm86.ini')
    return open(f, encoding='utf-8', errors='replace').read() if os.path.isfile(f) else ''


def build_sandbox(sand):
    os.makedirs(sand, exist_ok=True)
    ini032 = real_ini('0.3.2')
    ini030 = real_ini('0.3.0')
    if not ini032 or 'SM75' not in ini032:
        raise SystemExit('找不到 0.3.2 的真实出厂 INI —— 判据依赖它含 SM75/Turing')

    # ---- 合成 0.3.2 包（默认 version 注入 + 供替换注入，两个目录一起打） ----
    with zipfile.ZipFile(os.path.join(sand, 'pkg032.zip'), 'w') as z:
        base = '0.3.2 DLSS多帧生成 支持20和30系 默认version注入/'
        zip_sparse(z, base + 'version.dll', SZ032)
        zip_sparse(z, base + 'nvngx_dlss.dll', SZ_RUNTIME_BIG)
        zip_sparse(z, base + 'nvngx_dlssg.dll', SZ_RUNTIME_SMALL)
        z.writestr(base + 'dlssg_sm86.ini', ini032)
        for n, sz in SZ032_ALT.items():
            zip_sparse(z, '供替换注入/' + n, sz)
        z.writestr('作者的话.txt', '说明文本，应被归为 other 并跳过\n')

    # ---- 合成 0.3.0 包 ----
    with zipfile.ZipFile(os.path.join(sand, 'pkg030.zip'), 'w') as z:
        base = '0.3.0 DLSS多帧生成 仅30系 默认version注入/'
        zip_sparse(z, base + 'version.dll', SZ030)
        zip_sparse(z, base + 'nvngx_dlss.dll', SZ_RUNTIME_BIG)
        zip_sparse(z, base + 'nvngx_dlssg.dll', SZ_RUNTIME_SMALL)
        z.writestr(base + 'dlssg_sm86.ini', ini030)
        for n, sz in SZ030_ALT.items():
            zip_sparse(z, '供替换注入/' + n, sz)

    # ---- zip-slip 包 ----
    with zipfile.ZipFile(os.path.join(sand, 'evil.zip'), 'w') as z:
        z.writestr('dlssg_sm86.ini', ini032)
        z.writestr('../escaped-by-zipslip.txt', '不该出现在解压目录之外')

    # ---- 旧版（0.2.x）目录：15.67 MB 的代理 + 没有 ini ----
    old = os.path.join(sand, 'legacy')
    sparse_file(os.path.join(old, 'version.dll'), SZ_LEGACY)
    sparse_file(os.path.join(old, 'dinput8.dll'), 15678272)

    # ---- 垃圾目录：体积不在任何已知窗口内 ----
    junk = os.path.join(sand, 'junk')
    sparse_file(os.path.join(junk, 'version.dll'), 2 * 1024 * 1024)
    with open(os.path.join(junk, 'readme.md'), 'w', encoding='utf-8') as f:
        f.write('普通文件\n')

    # ---- 真实包路径清单（探针从文件读，避免命令行传中文） ----
    reals = []
    for pat in ('0.3.5', '0.3.2', '0.3.0'):
        p = find_real_pack(pat)
        if p and os.path.isdir(p):
            reals.append(pat + '\t' + p)
    with open(os.path.join(sand, 'real_packs.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(reals))
    print('sandbox   :', sand)
    print('real packs:', len(reals))


def main():
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    sand = os.path.join(tempfile.gettempdir(), 'gb_pack_probe', stamp)
    build_sandbox(sand)

    FW = os.path.join(os.environ.get('WINDIR', r'C:\Windows'),
                      'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe')
    env = clean_env()
    exe = os.path.join(WORK, 'PackProbe.exe')
    srcs = [os.path.join(SRC, f) for f in ('Core.cs', 'Pack.cs', 'Dlssg.cs', 'Lib.cs', 'Ui.cs')]
    args = [FW, '/nologo', '/target:exe', '/out:' + exe, '/unsafe+',
            '/main:Fluxion.PackImportProbe',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            '/r:System.Web.Extensions.dll', '/r:System.Management.dll', '/r:System.Core.dll',
            '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'] \
           + srcs + [os.path.join(TOOLS, 'pack_probe.cs')]

    p = subprocess.run(args, capture_output=True, text=True, env=env,
                       encoding='utf-8', errors='replace')
    print('=== compile rc =', p.returncode)
    out = (p.stdout or '') + (p.stderr or '')
    if p.returncode:
        print(out)
        raise SystemExit(1)

    r = subprocess.run([exe, sand], capture_output=True, text=True, env=env, cwd=WORK,
                       encoding='utf-8', errors='replace', timeout=600)
    print(r.stdout or '')
    err = (r.stderr or '').strip()
    if err:
        print('[stderr]', err)
    print('=== probe rc =', r.returncode)
    print('（沙盒留在 %s，不自动清理）' % sand)
    from _probe_runner import finish
    finish(r, verdict=True)


if __name__ == '__main__':
    main()
