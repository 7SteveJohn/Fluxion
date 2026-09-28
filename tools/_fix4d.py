# -*- coding: utf-8 -*-
"""修正 config 里 nv 段的十进制取值：手算 hex->dec 算错了几个。

被探针第 11 节抓到：compVsync 期望 0x08416747，实际读到 0x08418347（= 我手写的 138511175）。
这类错误人眼看不出来、工具不报错，但会把错的垂直同步模式写进驱动库。
所以这里一律用 int(hex, 16) 计算，绝不手写十进制。
"""
import codecs
import io
import json

ROOT = r"D:\youhua\GameBoost-DLSSG"

H = lambda s: int(s, 16)

NV = {
    # 键: (十进制值, 该值对应的十六进制说明)
    "shaderCacheOn": (1, None),
    "shaderCacheSize": (-1, "0xFFFFFFFF"),
    "dlssForcedPreset": (H("00FFFFFF"), "0x00FFFFFF"),
    "dlssPresetProfile": (1, None),
    "compPrerenderedFrames": (1, None),
    "compPowerMode": (1, None),
    "compVsync": (H("08416747"), "0x08416747"),
    "aaaVsync": (H("60925292"), "0x60925292"),
    "compTextureQuality": (H("14"), "0x14"),
    "backgroundFpsLimit": (0, None),
}

CACHE_STEPS = [
    (0, "关闭(禁用缓存)"),
    (H("80"), "128MB"),
    (H("100"), "256MB"),
    (H("200"), "512MB"),
    (H("400"), "1GB"),
    (H("1000"), "4GB"),
    (H("1400"), "5GB"),
    (H("2000"), "8GB"),
    (H("2800"), "10GB"),
    (H("4000"), "16GB"),
    (H("19000"), "100GB"),
    (-1, "无限制(0xFFFFFFFF)"),
]
VSYNC_STEPS = [
    (H("08416747"), "强制关"),
    (H("60925292"), "跟随游戏"),
    (H("47814940"), "强制开"),
    (H("18888888"), "快速同步"),
]
PRESET_STEPS = [
    (H("00FFFFFF"), "使用推荐值"),
    (H("0A"), "Preset J"),
    (H("0B"), "Preset K(Gen1)"),
    (H("0C"), "Preset L(Gen2)"),
    (H("0D"), "Preset M(Gen2)"),
    (0, "不干预"),
]


def comment_size():
    return ("shaderCacheSize: 12 档。0=关闭(禁用缓存) / "
            + " / ".join("%d=%s" % (v, t) for v, t in CACHE_STEPS[1:-1])
            + " / -1=无限制(0xFFFFFFFF)。别写成 4294967295 —— JSON 读回时 Convert.ToInt32 会溢出并静默取默认值。")


def comment_preset():
    return ("dlssForcedPreset: "
            + " / ".join("%d=%s" % (v, t) for v, t in PRESET_STEPS)
            + "。30 系建议保持「推荐值」：L 只在 Ultra Performance 档起效、M 只在 Performance 档起效，且 Gen2 在 30 系有约 20% 性能税。")


def comment_vsync():
    return ("compVsync / aaaVsync: "
            + " / ".join("%d(%s)=%s" % (v, hex(v), t) for v, t in VSYNC_STEPS)
            + "。")


# ---------- 1. 修两份 config ----------
for rel in (r"\config.json", r"\installer\config.default.json"):
    p = ROOT + rel
    raw = open(p, "rb").read()
    bom = raw.startswith(codecs.BOM_UTF8)
    t = raw.decode("utf-8-sig")
    d = json.loads(t)
    nv = d["nv"]
    nv["_comment_size"] = comment_size()
    nv["_comment_preset"] = comment_preset()
    nv["_comment_vsync"] = comment_vsync()
    nv["_comment_bglimit"] = comment_bglimit = "backgroundFpsLimit: 0=不限制 / 其余=目标 FPS。只写进各游戏档（per-profile）。"
    for k, (v, _) in NV.items():
        nv[k] = v
    out = json.dumps(d, ensure_ascii=False, indent=2)
    d2 = json.loads(out)          # 先验
    for k, (v, _) in NV.items():
        assert d2["nv"][k] == v, (rel, k)
    assert d2["nv"]["compVsync"] == H("08416747")
    assert d2["nv"]["aaaVsync"] == H("60925292")
    enc = codecs.BOM_UTF8 + out.encode("utf-8") if bom else out.encode("utf-8")
    open(p, "wb").write(enc)
    print("[ok] %-32s compVsync=%d(0x%08X)  aaaVsync=%d(0x%08X)"
          % (rel, d2["nv"]["compVsync"], d2["nv"]["compVsync"], d2["nv"]["aaaVsync"], d2["nv"]["aaaVsync"]))

# ---------- 2. 把修正并回补丁脚本，保证可复现 ----------
Q = ROOT + r"\tools\_patch4_20260919.py"
s = io.open(Q, encoding="utf-8").read()
NEW_CMT = ('    "_comment_size": "%s",\n    "_comment_preset": "%s",\n    "_comment_vsync": "%s",\n'
           % (comment_size(), comment_preset(), comment_vsync()))
# 就地替换三条注释行（按前缀定位）
lines = s.split("\n")
hit = 0
for i, ln in enumerate(lines):
    if ln.strip().startswith('"_comment_size":'):
        lines[i], hit = "    " + NEW_CMT.split("\n")[0], hit + 1
    elif ln.strip().startswith('"_comment_preset":'):
        lines[i] = "    " + NEW_CMT.split("\n")[1]
    elif ln.strip().startswith('"_comment_vsync":'):
        lines[i] = "    " + NEW_CMT.split("\n")[2]
assert hit == 1, hit
s = "\n".join(lines)
# 值行：把 compVsync / aaaVsync 的十进制改对
for key, (v, _) in NV.items():
    import re
    pat = re.compile(r'("' + key + r'":\s*)(-?\d+)')
    m = pat.search(s)
    assert m, key
    if int(m.group(2)) != v:
        s = s[:m.start(2)] + str(v) + s[m.end(2):]
        print("    修正脚本字面量 %-22s %s -> %s" % (key, m.group(2), v))
io.open(Q, "w", encoding="utf-8", newline="").write(s)
print("[ok] 补丁脚本已同步（可复现）")
