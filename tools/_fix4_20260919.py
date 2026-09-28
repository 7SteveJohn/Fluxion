# -*- coding: utf-8 -*-
"""修正 _patch4 脚本里的 C# 字面量（未转义引号 / 单反斜杠），并统一把 Program.OptKnob 写成 OptKnob。

为什么单独写一个修正脚本：Edit 工具对这个文件的两处替换回执成功但内容没变
（已在 Core.cs 编译期暴露：CS1009 无法识别的转义序列 + 字符串被引号截断）。
所以这里改成"改完立刻回读断言"，不依赖编辑工具的回执。
"""
import codecs
import io

P = r"D:\youhua\GameBoost-DLSSG\tools\_patch4_20260919.py"


def rd():
    with io.open(P, encoding="utf-8") as f:
        return f.read()


def wr(s):
    with io.open(P, "w", encoding="utf-8", newline="") as f:
        f.write(s)


s = rd()
BS = chr(92)      # 反斜杠
Q = chr(34)       # 双引号
LQ = chr(8220)    # 左花引号
RQ = chr(8221)    # 右花引号

fixes = []

# --- 1. 着色器缓存大小那条 Hint：裸引号 + 单反斜杠路径 ---
old = ("                Hint = " + Q + "共 12 档。驱动按 LRU 淘汰，不是" + Q + "设多大就占多大" + Q
       + " —— 本机 %LOCALAPPDATA%" + BS + "NVIDIA" + BS + "DXCache 目前约 1.25 GB。"
       + "担心占盘位就选 4~16 GB，别选「关闭」（等于禁用缓存）。" + Q + ",")
new = ("                Hint = " + Q + "共 12 档。驱动按 LRU 淘汰，不是「设多大就占多大」 —— 本机 "
       + "%LOCALAPPDATA%" + BS + BS + "NVIDIA" + BS + BS + "DXCache 目前约 1.25 GB。"
       + "担心占盘位就选 4~16 GB，别选最后那档「关闭」（那不是省盘，等于禁用缓存）。" + Q + ",")
if s.count(old) == 1:
    s = s.replace(old, new)
    fixes.append("shaderCacheSize Hint")
elif s.count(new) == 1:
    fixes.append("shaderCacheSize Hint (已正确)")
else:
    raise AssertionError("shaderCacheSize Hint 锚点不唯一: %d / %d" % (s.count(old), s.count(new)))

# --- 2. HAGS 那条 Hint：裸引号 ---
old = ("少数高帧率竞技场景有" + Q + "抖动反而变大" + Q + "的反例报告。")
new = ("少数高帧率竞技场景有「抖动反而变大」的反例报告 —— 那属于值得自己 A/B 一次的项。")
if s.count(old) == 1:
    s = s.replace(old, new)
    fixes.append("hags Hint")
elif s.count(new) == 1:
    fixes.append("hags Hint (已正确)")
else:
    # 可能是中间态（既无旧的也无新的）：打印现场，人工判断
    i = s.find("硬件加速 GPU 计划")
    raise AssertionError("hags Hint 锚点不唯一；现场: " + repr(s[i - 200:i + 400]))

# --- 3. 只读提示里的裸引号 ---
old = ("这一项要么没有" + Q + "更优的档位" + Q + "可选（默认即最优），")
new = ("这一项要么没有「更优的档位」可选（默认即最优），")
if s.count(old) == 1:
    s = s.replace(old, new)
    fixes.append("readonly tip")
elif s.count(new) == 1:
    fixes.append("readonly tip (已正确)")
else:
    raise AssertionError("readonly tip 锚点不唯一: %d / %d" % (s.count(old), s.count(new)))

# --- 4. Program.OptKnob -> OptKnob（OptKnob 是命名空间级类型，不是 Program 的嵌套类型）---
n = s.count("Program.OptKnob")
if n:
    s = s.replace("Program.OptKnob", "OptKnob")
    fixes.append("Program.OptKnob x%d" % n)

wr(s)

# ---- 回读断言（不信编辑回执，只信落盘内容）----
t = rd()
checks = [
    ("不是「设多大就占多大」", "着色器缓存 Hint 已换成书名号", True),
    ("%LOCALAPPDATA%" + BS + BS + "NVIDIA", "路径已是 C# 可用的双反斜杠", True),
    ("抖动反而变大」", "HAGS Hint 已换成书名号", True),
    ("更优的档位」", "只读提示已换成书名号", True),
    ("Program.OptKnob", "不再引用 Program.OptKnob", False),
]
for pat, label, want in checks:
    got = t.count(pat) > 0
    if got != want:
        raise AssertionError("回读断言失败: %s (count=%d)" % (label, t.count(pat)))

print("[ok] 修正项:", ", ".join(fixes) if fixes else "无")
print("[ok] 回读断言全部通过")
