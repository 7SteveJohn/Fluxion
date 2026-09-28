# -*- coding: utf-8 -*-
"""版本号 3.5.5 -> 3.6.0。

⚠️ .iss 不能用文本模式读写：2026-09-19 上一轮就是这么把 BOM 和 CRLF 一起写没的，
iss_check 直接 FAIL。这里全程按字节操作，改完立刻回读断言（BOM/CRLF/size 自洽）。
"""
import codecs
import io

ROOT = r"D:\youhua\GameBoost-DLSSG"
OLD, NEW = "3.5.5", "3.6.0"

# ---- Core.cs：AppVersion ----
p = ROOT + r"\src\Core.cs"
raw = open(p, "rb").read()
bom = raw.startswith(codecs.BOM_UTF8)
t = raw.decode("utf-8-sig")
old = 'AppVersion = "%s"' % OLD
assert t.count(old) == 1, t.count(old)
t = t.replace(old, 'AppVersion = "%s"' % NEW)
open(p, "wb").write(codecs.BOM_UTF8 + t.encode("utf-8") if bom else t.encode("utf-8"))
print("[ok] Core.cs  AppVersion %s -> %s (BOM=%s)" % (OLD, NEW, bom))

# ---- installer/*.iss：按字节替换，绝不改行尾与 BOM ----
for rel in (r"\installer\GameBoost-DLSSG.iss", r"\installer\GameBoost-DLSSG-Share.iss"):
    q = ROOT + rel
    try:
        raw = open(q, "rb").read()
    except IOError:
        print("[skip] %s 不存在" % rel)
        continue
    before = (raw.startswith(codecs.BOM_UTF8), raw.count(b"\r\n"), raw.count(b"\n"), len(raw))
    pat = OLD.encode("utf-8")
    n = raw.count(pat)
    if n == 0:
        print("[skip] %s 无 %s" % (rel, OLD))
        continue
    raw2 = raw.replace(pat, NEW.encode("utf-8"))
    open(q, "wb").write(raw2)
    after = (len(raw2),)
    raw3 = open(q, "rb").read()
    assert raw3.startswith(codecs.BOM_UTF8) == before[0], "BOM 被改了"
    assert raw3.count(b"\r\n") == before[1], "CRLF 数变了"
    assert raw3.count(b"\n") == before[2], "LF 数变了"
    assert OLD.encode("utf-8") not in raw3
    assert NEW.encode("utf-8") in raw3
    print("[ok] %-38s %d 处替换；BOM=%s CRLF=%d 字节 %d -> %d"
          % (rel, n, before[0], before[1], before[3], len(raw3)))
