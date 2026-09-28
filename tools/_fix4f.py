# -*- coding: utf-8 -*-
"""把 _patch4 脚本里 NV_JSON 的十进制字面量改成程序算出来的值（可复现）。"""
import io
import re

Q = r"D:\youhua\GameBoost-DLSSG\tools\_patch4_20260919.py"
s = io.open(Q, encoding="utf-8").read()
H = lambda x: int(x, 16)

FIX = {
    "compVsync": H("08416747"),
    "aaaVsync": H("60925292"),
}
n = 0
for k, v in FIX.items():
    pat = re.compile(r'("' + k + r'":\s*)(-?\d+)')
    m = pat.search(s)
    assert m, k
    if int(m.group(2)) != v:
        print("  %-16s %s -> %d" % (k, m.group(2), v))
        s = s[:m.start(2)] + str(v) + s[m.end(2):]
        n += 1

# 注释里的 65536(=64GB，写错了) -> 102400(0x19000=100GB)
if "65536=100GB" in s:
    s = s.replace("65536=100GB", "102400=100GB")
    print("  _comment_size 65536 -> 102400")
    n += 1

# _comment_vsync 里的十进制同步修正
old_v = "138511175(0x08416747)=强制关 / 1619595938(0x60925292)=跟随游戏 / 1200879936(0x47814940)=强制开 / 412316872(0x18888888)=快速同步"
new_v = "%d(0x08416747)=强制关 / %d(0x60925292)=跟随游戏 / %d(0x47814940)=强制开 / %d(0x18888888)=快速同步" % (
    H("08416747"), H("60925292"), H("47814940"), H("18888888"))
if old_v in s:
    s = s.replace(old_v, new_v)
    print("  _comment_vsync 四个十进制已修正")
    n += 1

io.open(Q, "w", encoding="utf-8", newline="").write(s)
t = io.open(Q, encoding="utf-8").read()
assert "138511175" not in t and "1619595938" not in t and "65536=100GB" not in t, "回读失败"
print("[ok] 补丁脚本已同步（%d 处）" % n)
