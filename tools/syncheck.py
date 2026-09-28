# -*- coding: utf-8 -*-
"""编译冒烟检查：把 src 下五个 .cs 编成一个临时 library，只看编译器怎么说。

为什么单独要一个：build.py 会产出并替换 Fluxion.exe —— 那是"编译产物"，需要用户许可。
改代码时反复要的只是"能不能编过"，这一步不碰任何产物，所以单独提供。

    python tools\\syncheck.py            # 只报编译结果
    python tools\\syncheck.py --quiet     # 只在有错时输出

退出码：0 = 编过，1 = 有 error。
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
FW = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
REFS = ("System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll",
        "System.Web.Extensions.dll", "System.Management.dll", "System.ServiceProcess.dll",
        "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll")
SRCS = ("Core.cs", "Pack.cs", "Dlssg.cs", "Lib.cs", "Ui.cs")


def env_clean():
    env = dict(os.environ)
    for k in ("http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"):
        env.pop(k, None)
    pv = env.get("Path") or env.get("PATH") or ""
    env.pop("PATH", None)
    env["Path"] = pv
    return env


def main():
    out = os.path.join(ROOT, ".build", "_syncheck.dll")
    args = [FW, "/nologo", "/target:library", "/out:" + out, "/unsafe+"]
    args += ["/r:" + r for r in REFS]
    args += [os.path.join(SRC, f) for f in SRCS]
    p = subprocess.run(args, capture_output=True, text=True,
                       env=env_clean(), encoding="utf-8", errors="replace")
    txt = ((p.stdout or "") + (p.stderr or "")).strip()
    errs = [ln for ln in txt.split("\n") if "error CS" in ln]
    rc = p.returncode
    if rc == 0:
        print("COMPILE OK")
    else:
        print("COMPILE FAILED (%d error lines)" % len(errs))
        for ln in errs[:40]:
            print("  " + ln)
    return rc or runner_guard()


# ---------------------------------------------------------------------------
#  runner 收尾自检（静态，不跑任何探针）
# ---------------------------------------------------------------------------
#  为什么放在这里：18 个 run_*.py 的收尾曾经是"探针红了 runner 也退 0"（第一版补丁是在
#  globals() 里猜变量名，换个名字就失效）。收尾现在只有 tools/_probe_runner.py 一份，
#  但没有东西盯着"新写的 runner 有没有用它"—— 这一步就是那个盯守：
#    · 每个 run_*.py 必须 import 并调用 finish(<proc>, verdict=<bool>)
#    · 不许再出现 globals().get( 那种猜名字的写法
#    · verdict=True 的，探针源码里必须有按 FAIL 返回非 0 的那一行
def runner_guard():
    tools = os.path.join(ROOT, "tools")
    bad, warn, n = [], [], 0
    for name in sorted(os.listdir(tools)):
        if not (name.startswith("run_") and name.endswith(".py")):
            continue
        n += 1
        src = open(os.path.join(tools, name), encoding="utf-8", errors="replace").read()
        if "globals().get(" in src:
            bad.append("%s：仍在用 globals() 猜结果变量（收尾必须显式传参）" % name)
        if "_probe_runner import finish" not in src:
            bad.append("%s：没有 from _probe_runner import finish" % name)
            continue
        m = re.search(r"finish\(\s*(\w+)\s*,\s*verdict=(True|False)\s*\)", src)
        if not m:
            bad.append("%s：finish(...) 没写成 finish(<proc>, verdict=True|False)" % name)
            continue
        if m.group(2) == "False":          # 诊断型探针：只输出结论，无 PASS/FAIL
            continue
        cs = os.path.join(tools, name[4:-3] + ".cs")
        if not os.path.exists(cs):
            warn.append("%s：找不到同名探针源码 %s（跳过 FAIL 返回检查）" % (name, os.path.basename(cs)))
            continue
        body = open(cs, encoding="utf-8", errors="replace").read()
        # 大小写两种写法都认：`return Fail;` / `return fail == 0 ? 0 : 1;`
        if not re.search(r"return\s+(Fail|fail)\b", body):
            bad.append("%s 声明了 verdict=True，但 %s 里没有按 FAIL 返回非 0（红了也没人知道）"
                       % (name, os.path.basename(cs)))
    if not n:
        bad.append("tools 下一个 run_*.py 都没找到 —— 路径不对？")
    print("RUNNER GUARD: %s（查了 %d 个 runner）" % ("OK" if not bad else "FAILED", n))
    for w in warn:
        print("  [warn] " + w)
    for b in bad:
        print("  [FAIL] " + b)
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
