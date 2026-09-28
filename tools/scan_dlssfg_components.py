# -*- coding: utf-8 -*-
"""扫描本机所有游戏目录，按「目录里是否真的存在 DLSS 帧生成组件」判断游戏是否具备 DLSS FG。

判据（来自 NVIDIA Streamline / dlssg_for_sm86 的加载链）：
  nvngx_dlssg.dll                        -> NVIDIA 官方 DLSS 帧生成运行时（最硬的指标）
  nvngx_dlssg.dll 变体 / dlssg 相关      -> 兜底
  sl.dlss_g.dll                          -> Streamline 版 DLSS 帧生成插件
  sl.dlss.dll / nvngx_dlss.dll           -> 超分（非帧生成），只作旁证
  amd_fidelityfx_framegeneration_dx12.dll-> AMD FSR 帧生成
  mhypbase.dll / globalgamemanagers      -> 米哈游引擎基座 / Unity 数据目录（识别用）
  HoYoKProtect.sys / ACE-*.sys / mhyprot -> 内核反作弊（注入风险）
"""
import os
import sys
import json

DRIVES = ["C:\\", "D:\\", "E:\\", "F:\\", "G:\\"]

# 全盘扫描时跳过的重目录（避免浪费时间）
PRUNE = {
    "windows", "$recycle.bin", "system volume information", "program files",
    "program files (x86)", "programdata", "windowsapps", "wpsystem",
    "appdata", "node_modules", "__pycache__", ".git", "$winre_backup_partition.marker",
    "perflogs", "recovery", "boot", "documents and settings", "msys64",
    "temp", "tmp", "cache", "onedrive", "qqmusiccache", "config.msi",
    "wudownloadcache", "deliveryoptimization", "idm_temp", "recycler",
}

TARGETS = {
    "nvngx_dlssg.dll": ("DLSS_帧生成", "NVIDIA 官方 DLSS Frame Generation 运行时"),
    "sl.dlss_g.dll": ("DLSS_帧生成", "Streamline DLSS-G 插件"),
    "nvngx_dlss.dll": ("DLSS_超分", "DLSS Super Resolution"),
    "sl.dlss.dll": ("DLSS_超分", "Streamline DLSS 插件"),
    "nvngx_dlssd.dll": ("DLSS_光线重建", "DLSS Ray Reconstruction"),
    "sl.dlss_d.dll": ("DLSS_光线重建", "Streamline DLSS-D 插件"),
    "dlssg_to_fsr3_amd_is_better.dll": ("FSR3_MOD", "DLSS-FG→FSR3 转译 MOD"),
    "amd_fidelityfx_framegeneration_dx12.dll": ("FSR_帧生成", "AMD FSR Frame Generation"),
    "amd_fidelityfx_dx12.dll": ("FSR_超分", "AMD FSR 2/3"),
    "amd_fidelityfx_upscaler_dx12.dll": ("FSR_超分", "AMD FSR 3.1 Upscaler"),
    "mhypbase.dll": ("米哈游引擎", "miHoYo 引擎基座"),
    "HoYoKProtect.sys": ("内核反作弊", "米哈游 HoYoKProtect（注入高风险）"),
    "mhyprot2.sys": ("内核反作弊", "米哈游 mhyprot2"),
    "mhyprot3.sys": ("内核反作弊", "米哈游 mhyprot3"),
    "AntiCheatExpert.sys": ("内核反作弊", "腾讯 ACE"),
    "ACE-BASE.sys": ("内核反作弊", "腾讯 ACE-BASE"),
    "version.dll": ("代理入口", "已被 DLSSG 代理或游戏自身使用"),
    "dlssg_sm86.ini": ("本工具注入残留", "dlssg_for_sm86 配置"),
}

# 二游关键词（目录名/文件名命中即认为可能是二游）
GACHA_HINTS = [
    "genshin", "yuanshen", "原神", "zenless", "绝区零", "zzz", "starrail",
    "star rail", "崩坏", "honkai", "bh3", "impact", "wuthering", "鸣潮",
    "punishing", "战双", "snowbreak", "尘白", "gfl2", "少女前线", "追放",
    "endfield", "终末地", "arknights", "明日方舟", "toweroffantasy", "幻塔",
    "nikke", "胜利女神", "bluearchive", "蔚蓝档案", "azurlane", "碧蓝航线",
    "1999", "重返未来", "infinite nikki", "无限暖暖", "深空之眼", "无期迷途",
    "白夜极光", "千年之旅", "光与夜", "恋与深空", "鸣潮", "miHoYo", "mihoyo",
    "hy", "hypergryph", "kuro", "kurogames", "叠纸", "papergames",
]


def sz(p):
    try:
        return os.path.getsize(p)
    except Exception:
        return -1


def scan_root(root, maxdepth=8, collect_games=True):
    """遍历 root，收集目标组件文件 + 候选游戏目录。"""
    found = []          # (path, kind, desc)
    dirs_hit = {}       # 顶层游戏目录 -> 命中集合
    root = os.path.abspath(root)
    base_depth = root.rstrip("\\").count("\\")
    for dp, dn, fn in os.walk(root):
        dn[:] = [d for d in dn if d.lower() not in PRUNE and not d.startswith("$")]
        depth = dp.count("\\") - base_depth
        if depth > maxdepth:
            dn[:] = []
            continue
        for f in fn:
            lf = f.lower()
            if lf in TARGETS:
                kind, desc = TARGETS[lf]
                found.append((os.path.join(dp, f), kind, desc))
            # 被反作弊改名隔离的代理：version.dll.1234567890
            elif lf.startswith("version.dll.") and lf[12:].isdigit():
                found.append((os.path.join(dp, f), "代理残留(改名隔离)", "被反作弊隔离的 version.dll"))
        if collect_games:
            for d in dn:
                ld = d.lower()
                for h in GACHA_HINTS:
                    if h in ld:
                        rel = os.path.relpath(os.path.join(dp, d), root)
                        top = rel.split(os.sep)[0]
                        dirs_hit.setdefault(top, set()).add(d)
                        break
    return found, dirs_hit


def main():
    allfound = []
    gacha = {}
    for drv in DRIVES:
        if not os.path.isdir(drv):
            continue
        print("### scanning", drv, flush=True)
        try:
            f, d = scan_root(drv)
        except Exception as e:
            print("   ERR", drv, e)
            continue
        allfound += f
        for k, v in d.items():
            gacha.setdefault(drv + k, set()).update(v)

    print()
    print("=" * 78)
    print("目标组件命中（共 %d）" % len(allfound))
    print("=" * 78)
    bydir = {}
    for p, kind, desc in allfound:
        bydir.setdefault(os.path.dirname(p), []).append((os.path.basename(p), kind, desc, sz(p)))
    for d in sorted(bydir):
        print()
        print("[DIR]", d)
        for n, kind, desc, s in sorted(bydir[d]):
            print("     %-46s %-14s %8s B  %s" % (n, kind, s, desc))

    print()
    print("=" * 78)
    print("二游目录关键词命中")
    print("=" * 78)
    for k in sorted(gacha):
        print("  ", k, "  ->  ", ", ".join(sorted(gacha[k]))[:160])

    out = {
        "components": [
            {"path": p, "kind": k, "desc": d, "size": sz(p)} for p, k, d in allfound
        ],
        "gacha_dirs": {k: sorted(v) for k, v in gacha.items()},
    }
    dst = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_scan_dlssfg_result.json")
    with open(dst, "w", encoding="utf-8") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=1)
    print()
    print("已写出:", dst)


if __name__ == "__main__":
    main()
