# -*- coding: utf-8 -*-
"""最小改动地给两份 config 加 nv 段 + network.throttlingIndex。

上一版用 json.dumps 整份重排，diff 无谓地变大（既有单行数组被展开）。
这里改回"按原文插入"：
  · 行尾/BOM 沿用原文
  · 只在 network 段头插一行 throttlingIndex
  · 只在对象末尾追加 nv 段（且不带尾部逗号）
  · 所有十进制一律 int(hex,16) 算，绝不手写（上一版手算 0x08416747 写错，被探针抓到）
"""
import codecs
import io
import json

ROOT = r"D:\youhua\GameBoost-DLSSG"
H = lambda s: int(s, 16)

NV_VALUES = [
    ("shaderCacheOn", 1),
    ("shaderCacheSize", -1),
    ("dlssForcedPreset", H("00FFFFFF")),
    ("dlssPresetProfile", 1),
    ("compPrerenderedFrames", 1),
    ("compPowerMode", 1),
    ("compVsync", H("08416747")),
    ("aaaVsync", H("60925292")),
    ("compTextureQuality", H("14")),
    ("backgroundFpsLimit", 0),
]

CACHE_STEPS = [(0, "关闭(禁用缓存)"), (H("80"), "128MB"), (H("100"), "256MB"), (H("200"), "512MB"),
               (H("400"), "1GB"), (H("1000"), "4GB"), (H("1400"), "5GB"), (H("2000"), "8GB"),
               (H("2800"), "10GB"), (H("4000"), "16GB"), (H("19000"), "100GB"), (-1, "无限制(0xFFFFFFFF)")]
VSYNC_STEPS = [(H("08416747"), "强制关"), (H("60925292"), "跟随游戏"),
               (H("47814940"), "强制开"), (H("18888888"), "快速同步")]
PRESET_STEPS = [(H("00FFFFFF"), "使用推荐值"), (H("0A"), "Preset J"), (H("0B"), "Preset K(Gen1)"),
                (H("0C"), "Preset L(Gen2)"), (H("0D"), "Preset M(Gen2)"), (0, "不干预")]

CMT_MAIN = ("【取值细调 · v3.6.0】这里每一条都对应体检表里带 ⚙ 的行（双击即可改），也对应「取值细调」对话框。"
            "默认值 = 旧的硬编码值，所以升级后行为不变。驱动层取值写入 nvdrsdb，重启游戏生效；"
            "改回默认不会自动撤销已写入系统的值，用「恢复备份」整体还原。")
CMT_SIZE = ("shaderCacheSize: 12 档。"
            + " / ".join("%d=%s" % (v, t) for v, t in CACHE_STEPS)
            + "。别写成 4294967295 —— JSON 读回时 Convert.ToInt32 会溢出并静默取默认值。")
CMT_PRESET = ("dlssForcedPreset: " + " / ".join("%d=%s" % (v, t) for v, t in PRESET_STEPS)
              + "。30 系建议保持「推荐值」：L 只在 Ultra Performance 档起效、M 只在 Performance 档起效，"
                "且 Gen2 在 30 系有约 20% 性能税。")
CMT_VSYNC = ("compVsync / aaaVsync: " + " / ".join("%d(0x%08X)=%s" % (v, v, t) for v, t in VSYNC_STEPS)
             + "。")
CMT_BG = "backgroundFpsLimit: 0=不限制 / 其余=目标 FPS。只写进各游戏档（per-profile）。"
CMT_THROTTLE = ("NetworkThrottlingIndex: -1=禁用节流(推荐) / 10=Windows 默认 / 20·30·50 更保守。"
                "改完立即生效；体检表里带 ⚙ 的行可双击改。")


def build_nv(nl):
    lines = [
        '  "nv": {',
        '    "_comment": "%s",' % CMT_MAIN,
        '    "_comment_size": "%s",' % CMT_SIZE,
        '    "_comment_preset": "%s",' % CMT_PRESET,
        '    "_comment_vsync": "%s",' % CMT_VSYNC,
        '    "_comment_bglimit": "%s",' % CMT_BG,
    ]
    for i, (k, v) in enumerate(NV_VALUES):
        tail = "," if i < len(NV_VALUES) - 1 else ""
        lines.append('    "%s": %d%s' % (k, v, tail))
    lines.append('  }')
    return nl.join(lines)


for rel in (r"\config.json", r"\installer\config.default.json"):
    p = ROOT + rel
    raw = open(p, "rb").read()
    bom = raw.startswith(codecs.BOM_UTF8)
    t = raw.decode("utf-8-sig")
    nl = "\r\n" if "\r\n" in t else "\n"

    # ① network 段头插 throttlingIndex
    if '"throttlingIndex"' not in t:
        key = '"network": {'
        i = t.index(key) + len(key)
        t = t[:i] + nl + '    "_comment_throttle": "%s",' % CMT_THROTTLE + nl + '    "throttlingIndex": -1,' + t[i:]

    # ② 末尾追加 nv 段
    if '"nv"' not in t:
        k = t.rstrip().rfind("}")
        t = t[:k].rstrip() + "," + nl + build_nv(nl) + nl + t[k:]

    d = json.loads(t)                       # 先验后写
    for kk, vv in NV_VALUES:
        assert d["nv"][kk] == vv, (rel, kk, d["nv"][kk], vv)
    assert d["network"]["throttlingIndex"] == -1
    assert d["nv"]["compVsync"] == H("08416747"), hex(d["nv"]["compVsync"])
    assert d["nv"]["aaaVsync"] == H("60925292"), hex(d["nv"]["aaaVsync"])
    assert d["nv"]["shaderCacheSize"] == -1
    out = codecs.BOM_UTF8 + t.encode("utf-8") if bom else t.encode("utf-8")
    open(p, "wb").write(out)
    print("[ok] %-32s lines=%d  nv=%d 项  throttle=%d  compVsync=0x%08X aaaVsync=0x%08X"
          % (rel, t.count(nl) + 1, len([x for x in d["nv"] if not x.startswith("_")]),
             d["network"]["throttlingIndex"], d["nv"]["compVsync"], d["nv"]["aaaVsync"]))
