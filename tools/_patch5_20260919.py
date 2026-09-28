# -*- coding: utf-8 -*-
"""补三行体检表条目：把「按游戏档位的驱动取值」也做成可见可双击的行。

为什么要补：18 个旋钮里只有 11 个能通过体检表双击直达，剩下 7 个
（竞技档的预渲染帧数/电源模式/垂直同步/纹理质量、3A 档垂直同步、DLSS 预设档）
只能在「取值细调…」对话框里翻找 —— 而"看得见才想得起去改"，这正是用户这次的诉求。
"""
import io

ROOT = r"D:\youhua\GameBoost-DLSSG"
P = ROOT + r"\src\Core.cs"
t = io.open(P, encoding="utf-8-sig").read()
NL = "\n"

# ---- 1. 在「驱动·DLSS 强制预设」之后插三行 ----
anchor = """                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });
"""
assert t.count(anchor) == 1, t.count(anchor)

NEWROWS = """                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 预设档",
                    Current = KnobText("nv.dlssPresetProfile"),
                    Expected = "双击本行选择；「强制预设字母」不生效时驱动要求一起改这一项",
                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });

                // 按游戏档位的驱动取值：一个档位有 4 个值，不可能一项一行，
                // 所以合成一行显示"当前四件套"，双击进入后可以一并改。
                list.Add(new StatusItem
                {
                    Item = "驱动·竞技(FPS)档取值",
                    Current = "预渲染 " + KnobText("nv.compPrerenderedFrames")
                            + " · 电源 " + KnobText("nv.compPowerMode")
                            + " · 垂直同步 " + KnobText("nv.compVsync")
                            + " · 纹理 " + KnobText("nv.compTextureQuality"),
                    Expected = "双击本行可一并调整这 4 项（CPU 瓶颈调低预渲染更跟手；GPU 瓶颈反而掉帧）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·3A/MMO/二游档取值",
                    Current = "垂直同步 " + KnobText("nv.aaaVsync")
                            + " · 电源 " + KnobText("nv.compPowerMode")
                            + " · 缓存 " + KnobText("nv.shaderCacheSize"),
                    Expected = "双击本行可一并调整；画质档默认不强制关垂直同步（无 VRR 屏会撕裂）",
                    Status = "✅ 可调"
                });
"""
t = t.replace(anchor, NEWROWS)

# ---- 2. KnobKeyOfItem 增加映射 ----
old_map = """                case "驱动·DLSS 强制预设": return "nv.dlssPresetLetter";
"""
new_map = """                case "驱动·DLSS 强制预设": return "nv.dlssPresetLetter";
                case "驱动·DLSS 预设档": return "nv.dlssPresetProfile";
                case "驱动·竞技(FPS)档取值": return "nv.compPrerenderedFrames";
                case "驱动·3A/MMO/二游档取值": return "nv.aaaVsync";
"""
assert t.count(old_map) == 1
t = t.replace(old_map, new_map)

io.open(P, "w", encoding="utf-8", newline="").write(t)

# ---- 回读断言 ----
u = io.open(P, encoding="utf-8-sig").read()
for s in ('Item = "驱动·DLSS 预设档"', 'Item = "驱动·竞技(FPS)档取值"',
          'Item = "驱动·3A/MMO/二游档取值"', 'case "驱动·竞技(FPS)档取值": return "nv.compPrerenderedFrames";'):
    assert u.count(s) == 1, (s, u.count(s))
import codecs
raw = open(P, "rb").read()
print("[ok] 已补 3 行；Core.cs BOM=%s CRLF=%d LF=%d"
      % (raw.startswith(codecs.BOM_UTF8), raw.count(b"\r\n"), raw.count(b"\n")))
