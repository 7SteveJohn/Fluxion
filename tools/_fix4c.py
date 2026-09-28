# -*- coding: utf-8 -*-
"""把「WithDlss 缺 var c」并回 _patch4 脚本：按 old/new 分界线 (\"\"\", \"\"\") 精确定位，只改 new 侧。"""
import io

Q = r"D:\youhua\GameBoost-DLSSG\tools\_patch4_20260919.py"
s = io.open(Q, encoding="utf-8").read()
NL = chr(10)
Q3 = chr(34) * 3
LINE = "list.Add(new uint[] { NvDrsDb.IdDlssDllOverride, 0x1 });                  // 启用 DLL 覆盖"

old = Q3 + ", " + Q3 + "            " + LINE
new = Q3 + ", " + Q3 + "            var c = Program.Cfg;" + NL + "            " + LINE

c1, c2 = s.count(old), s.count(new)
print("old=%d new=%d" % (c1, c2))
if c1 == 1 and c2 == 0:
    s = s.replace(old, new)
    io.open(Q, "w", encoding="utf-8", newline="").write(s)
    print("[ok] C5 的 new 侧已补上 var c = Program.Cfg;")
elif c2 == 1:
    print("[skip] 已包含")
else:
    raise AssertionError("锚点计数异常")

t = io.open(Q, encoding="utf-8").read()
assert t.count("var c = Program.Cfg;") == 1, t.count("var c = Program.Cfg;")
i = t.index("C5 WithDlss")
print("--- C5 现场 ---")
for ln in t[i - 780:i + 40].split(NL):
    print("   ", ln)
