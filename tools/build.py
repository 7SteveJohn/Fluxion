# -*- coding: utf-8 -*-
"""Fluxion 构建脚本（静态校验 → csc 编译 → 可选 Inno Setup 打包）。

为什么有这个脚本：compile.ps1 依赖 PowerShell 通道（本机执行被策略拦），
而 csc 直接从 Python 调起可以稳定工作。用法：

    python tools\\build.py          # 只校验 + 编译
    python tools\\build.py --pkg    # 校验 + 编译 + 打包安装程序

注意（踩过的坑）：
  · csc 是 .NET 程序，环境里同时存在 http_proxy/HTTP_PROXY 会在启动阶段直接崩
  · 同理 PATH 与 Path 两个键并存也会崩 —— 这里都先归一化再调起
"""
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRCS = ["Core.cs", "Pack.cs", "Dlssg.cs", "Lib.cs", "Ui.cs"]
PY = sys.executable


def run(cmd, env=None, cwd=None):
    p = subprocess.run(cmd, capture_output=True, text=True, env=env, cwd=cwd,
                       encoding="utf-8", errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def clean_env():
    env = dict(os.environ)
    for k in ("http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"):
        env.pop(k, None)
    pv = env.get("Path") or env.get("PATH") or ""
    env.pop("PATH", None)
    env["Path"] = pv
    return env


def main():
    print("=" * 18, "STATIC CHECK", "=" * 18)
    rc, out = run([PY, os.path.join(ROOT, "tools", "static_check.py"), os.path.join(ROOT, "src")])
    print(out.strip()[-3000:])
    if rc != 0:
        sys.exit("static_check FAILED")

    print("=" * 18, "COMPILE", "=" * 18)
    env = clean_env()
    fw = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
    csc = os.path.join(fw, "csc.exe")
    if not os.path.exists(csc):
        csc = os.path.join(r"C:\Windows\Microsoft.NET\Framework\v4.0.30319", "csc.exe")
    out_exe = os.path.join(ROOT, "Fluxion.new.exe")
    final_exe = os.path.join(ROOT, "Fluxion.exe")
    ico = os.path.join(ROOT, "icon", "icon.ico")
    mf = os.path.join(ROOT, "src", "app.manifest")

    # System.IO.Compression(.FileSystem)：Pack.cs 解压作者发布的 zip。
    #   两个都要引 —— ZipArchive 在 Compression 里，ZipFile 才在 FileSystem 里。
    args = [csc, "/nologo", "/target:winexe", "/platform:anycpu", "/unsafe", "/out:" + out_exe,
            "/r:System.dll", "/r:System.Core.dll", "/r:System.Windows.Forms.dll",
            "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll", "/r:System.Management.dll",
            "/r:System.ServiceProcess.dll",
            "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll"]
    if os.path.exists(ico):
        args.append("/win32icon:" + ico)
    if os.path.exists(mf):
        args.append("/win32manifest:" + mf)
    for s in SRCS:
        p = os.path.join(ROOT, "src", s)
        if not os.path.exists(p):
            sys.exit("missing source: " + p)
        args.append(p)

    rc, out = run(args, env=env)
    print(out.strip()[-5000:])
    if rc != 0 or not os.path.exists(out_exe):
        sys.exit("COMPILE FAILED")
    if os.path.exists(final_exe):
        try:
            os.remove(final_exe)
        except OSError:
            os.rename(final_exe, final_exe + ".bak")
            try:
                os.remove(final_exe)
            except OSError:
                pass
    import shutil
    shutil.move(out_exe, final_exe)
    print("COMPILE_OK ->", final_exe, os.path.getsize(final_exe))

    if "--pkg" not in sys.argv:
        return

    print("=" * 18, "ISCC", "=" * 18)
    ver = None
    for line in open(os.path.join(ROOT, "installer", "Fluxion.iss"), encoding="utf-8-sig"):
        if line.startswith("#define AppVersion"):
            ver = line.split('"')[1]
    if ver is None:
        sys.exit("cannot read AppVersion from .iss")
    target = os.path.join(ROOT, "installer_out", "Fluxion-Setup-%s.exe" % ver)
    if os.path.exists(target):
        os.remove(target)
    iscc = os.environ.get("ISCC") or shutil.which("ISCC") or r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if not os.path.exists(iscc):
        sys.exit("ISCC.exe not found; set ISCC env var or install Inno Setup 6")
    rc, out = run([iscc, "/O" + os.path.join(ROOT, "installer_out"),
                   os.path.join(ROOT, "installer", "Fluxion.iss")])
    print(out.strip()[-600:])
    print("iscc rc =", rc, "  SETUP:", os.path.getsize(target) if os.path.exists(target) else "FAIL")


if __name__ == "__main__":
    main()
