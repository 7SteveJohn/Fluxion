# -*- coding: utf-8 -*-
"""
Fluxion 扫描规则镜像校验（tools/scan_mirror_check.py）

作用：把 src/Dlssg.cs 里 Scan() / Inspect() / ScoreGame() / NiceTitle() / ProbeUePlugins()
      的判定逻辑镜像成 Python，直接跑真实磁盘目录，输出"扫描会收录哪些条目、标题/能力是什么"。
用途：改完扫描规则先跑这个，避免"编译通过但收录结果不对"（编译只能保证语法，保证不了决策）。

用法：python tools/scan_mirror_check.py            # 默认跑本机已知的几个根
      python tools/scan_mirror_check.py <root>...  # 指定启动器根/游戏目录
"""
import os
import sys

ENTRY_FG = ("nvngx_dlssg.dll", "sl.dlss_g.dll")
ENTRY_UP = ("nvngx_dlss.dll",)
ENTRY_FSR = ("ffx_frameinterpolation_x64.dll", "amd_fidelityfx_dx12.dll",
             "amd_fidelityfx_framegeneration_dx12.dll")

HARD_SKIP = ("unins", "setup", "crash", "redist", "safemode", "bootstrap", "install")
SOFT_SKIP = ("launcher",)          # 仅当目录里没有其它 exe 时才采用（CP2077 根目录只有 REDprelauncher.exe）
STRUCTURAL = ("win64", "win32", "binaries", "client", "x64", "x86", "bin", "game")
TOOL_WORDS = ("clash", "mihomo", "v2ray", "dotnet", "powershell", "steamcmd", "geforce",
              "nvidia", "bandizip", "context menu", "listary", "everything", "igamecenter")

DLL_DEPTH3_SKIP = ("redist", "redistributable", "directx")
UE_SKIP = ("content", "paks", "saved", "movies", "audio")


def find_file(d, name, depth):
    try:
        if os.path.exists(os.path.join(d, name)):
            return True
        if depth <= 0:
            return False
        for sub in sorted(os.listdir(d)):
            p = os.path.join(d, sub)
            if os.path.isdir(p) and sub.lower() not in DLL_DEPTH3_SKIP:
                if find_file(p, name, depth - 1):
                    return True
    except OSError:
        pass
    return False


def probe_ue_plugins(exe_dir):
    """<游戏根>\\Engine\\Plugins 下找 DLSS/FSR 组件（鸣潮的 DLSS FG 就在这里，深度 7）"""
    fg = up = fsr = False

    def walk(d, depth):
        nonlocal fg, up, fsr
        try:
            for f in os.listdir(d):
                n = f.lower()
                if n in ENTRY_FG:
                    fg = True
                elif n in ENTRY_UP:
                    up = True
                elif n in ENTRY_FSR:
                    fsr = True
            if depth <= 0:
                return
            for sub in os.listdir(d):
                p = os.path.join(d, sub)
                if os.path.isdir(p) and sub.lower() not in UE_SKIP:
                    walk(p, depth - 1)
        except OSError:
            pass

    d = os.path.abspath(exe_dir)
    for _ in range(5):
        plug = os.path.join(d, "Engine", "Plugins")
        if os.path.isdir(plug):
            walk(plug, 6)
            return fg, up, fsr
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return fg, up, fsr


def probe_dir(d):
    """浅层结构特征（深度 ≤2）"""
    feats = set()

    def walk(cur, depth):
        try:
            for f in sorted(os.listdir(cur)):
                p = os.path.join(cur, f)
                n = f.lower()
                if os.path.isfile(p):
                    if n in ("unityplayer.dll", "gameassembly.dll"):
                        feats.add("unity")
                    # Unity 数据目录签名（<游戏名>_Data\\globalgamemanagers）：原神这类把引擎
                    # 静态编进主程序、根目录没有 UnityPlayer.dll 的游戏靠它识别
                    elif n == "globalgamemanagers":
                        feats.add("unity")
                    elif n == "mhypbase.dll":
                        feats.add("hoyo")                      # 米哈游引擎基座
                    elif "hoyokprotect" in n or "mhyprot" in n:
                        feats.add("ac")                        # 米哈游内核反作弊
                    elif n.endswith(".pak") and os.path.getsize(p) >= 50 * 1024 * 1024 and n != "resources.pak":
                        feats.add("unreal")
                    elif n in ("steam_api64.dll", "steam_api.dll"):
                        feats.add("steam")
                    elif "easyanticheat" in n or "battleye" in n:
                        feats.add("ac")
                    elif n.endswith("shipping.exe") and "crash" not in n:
                        feats.add("shipping")
                elif os.path.isdir(p):
                    if n == "engine":
                        feats.add("unreal")
                    elif n in ("redist", "redistributable", "directx"):
                        continue
                    elif depth < 2:
                        walk(p, depth + 1)
        except OSError:
            pass

    walk(d, 0)
    return feats


def pick_exe(d):
    """返回 (exe名, 是否降级启动器)"""
    best, best_n, soft, soft_n = 0, None, 0, None
    try:
        files = os.listdir(d)
    except OSError:
        return None, False
    for f in files:
        if not f.lower().endswith(".exe"):
            continue
        n = f.lower()
        size = os.path.getsize(os.path.join(d, f))
        if any(k in n for k in HARD_SKIP):
            continue
        if any(k in n for k in SOFT_SKIP):
            if size > soft:
                soft, soft_n = size, f
            continue
        if size > best:
            best, best_n = size, f
    if best_n:
        return best_n, False
    return soft_n, True


def nice_title(d):
    cur = os.path.abspath(d)
    for _ in range(8):
        name = os.path.basename(cur)
        if name.lower() not in STRUCTURAL:
            return name
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    return os.path.basename(os.path.abspath(d))


def is_launcher_container(d, exe):
    """启动器根：选中的是 launcher 类 exe 且下挂 games\\ 子目录 → 不是游戏本体，禁止装代理"""
    if not exe or "launcher" not in exe.lower():
        return False
    return os.path.isdir(os.path.join(d, "games"))


def inspect(d):
    """返回 dict 或 None（对应 Dlssg.GameInspect）"""
    exe, soft = pick_exe(d)
    if not exe:
        return None
    low = os.path.basename(d).lower()
    if any(w in low for w in TOOL_WORDS):
        return None
    g = {"dir": d, "exe": exe, "title": os.path.basename(d), "soft": soft,
         "fg": False, "up": False, "fsr": False,
         "container": is_launcher_container(d, exe)}
    for names, key in ((ENTRY_FG, "fg"), (ENTRY_UP, "up"), (ENTRY_FSR, "fsr")):
        for name in names:
            if find_file(d, name, 3):
                g[key] = True
                break
    if not (g["fg"] and g["up"] and g["fsr"]):
        fg, up, fsr = probe_ue_plugins(d)
        g["fg"] = g["fg"] or fg
        g["up"] = g["up"] or up
        g["fsr"] = g["fsr"] or fsr
    feats = probe_dir(d)
    if not (g["fg"] or g["up"] or g["fsr"] or feats):
        return None
    if g["exe"].lower().endswith("shipping.exe"):
        g["shipping"] = True
    return g


def score(g):
    s = 0
    s += 4 if g["fg"] else 0
    s += 4 if g["fsr"] else 0
    s += 2 if g["up"] else 0
    s += 3 if g.get("shipping") else 0
    return s


def under(a, b):
    a, b = a.rstrip("\\"), b.rstrip("\\")
    return len(a) > len(b) and a.lower().startswith(b.lower()) and a[len(b)] == "\\"


def scan_launcher_root(root):
    cands = []

    def collect(d, depth):
        if depth <= 4:
            g = inspect(d)
            if g:
                g["depth"] = depth
                cands.append(g)
            if depth < 4:
                try:
                    for sub in sorted(os.listdir(d)):
                        p = os.path.join(d, sub)
                        if os.path.isdir(p) and sub.lower() not in ("redist", "redistributable", "directx", "engine"):
                            collect(p, depth + 1)
                except OSError:
                    pass

    collect(root, 0)
    cands.sort(key=lambda g: (-score(g), -g["depth"]))
    out = []
    for g in cands:
        if any(under(g["dir"], a["dir"]) or under(a["dir"], g["dir"]) for a in out):
            continue
        if os.path.basename(g["dir"]).lower() in STRUCTURAL:
            g["title"] = nice_title(g["dir"])
        out.append(g)
    return out


def describe(g):
    caps = []
    if g["fg"]:
        caps.append("DLSS 帧生成 √")
    if g["up"]:
        caps.append("DLSS 超分 √")
    if g["fsr"]:
        caps.append("FSR3 帧生成 √")
    if not caps:
        caps.append("无帧生成组件")
    soft = " [启动器降级]" if g.get("soft") else ""
    if g.get("container"):
        soft += " [启动器容器·禁止注入]"
    return "%-28s (%-28s) depth=%s score=%s  %s%s" % (
        g["title"], g["exe"], g.get("depth", "-"), score(g), " · ".join(caps), soft)


if __name__ == "__main__":
    roots = sys.argv[1:]
    if not roots:
        roots = [r"G:\Wuthering Waves", r"G:\miHoYo Launcher"]
        steam = r"G:\SteamLibrary\steamapps\common\Cyberpunk 2077"
        if os.path.isdir(steam):
            print("== Steam 根（Inspect 直扫）==")
            g = inspect(steam)
            print("  ", describe(g) if g else "  (未收录)")
            print()
    for r in roots:
        print("== 启动器根:", r)
        if not os.path.isdir(r):
            print("   (目录不存在)")
            continue
        for g in scan_launcher_root(r):
            print("  ", describe(g))
        print()

    # 收录结果自检：期望命中的目录必须在列表里
    expect = [r"G:\miHoYo Launcher\games\Genshin Impact Game",
              r"G:\miHoYo Launcher\games\ZenlessZoneZero Game",
              r"G:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64"]
    hits = []
    for root in [r"G:\miHoYo Launcher", r"G:\Wuthering Waves"]:
        if os.path.isdir(root):
            for g in scan_launcher_root(root):
                hits.append(g["dir"].lower())
    print("== 期望命中自检 ==")
    for e in expect:
        if not os.path.isdir(e):
            print("   [skip] 不存在:", e)
            continue
        print("   ", "OK  " if e.lower() in hits else "MISS", e)
    bad = [h for h in hits if os.path.basename(h).lower() in ("mihoyo launcher", "wuthering waves")]
    print("   ", "OK  " if not bad else "BAD ", "启动器根未被收录（应无）:", bad if bad else "无")
