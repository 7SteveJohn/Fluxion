# -*- coding: utf-8 -*-
"""v3.6.0 取值细调（把写死的优化目标值下放给用户）

背景：体检表只告诉用户"目标和现状一不一致"，目标值本身是程序写死的。
用户明确要求：像着色器缓存 12 档这种"能给出具体操作"的，就该把权限给用户。

做法：
  1) 驱动层的 CompSettings / AaaSettings 从 static readonly 数组改成按 Cfg 取值的方法；
  2) Config 新增 nv 段（驱动取值）+ network.throttlingIndex；
  3) 新增 OptKnob 表 + SetKnob / ApplyKnob，UI 侧新增「取值细调」对话框；
  4) 体检表给可调行挂 Key，双击直接改。

默认值 = 原硬编码值，行为零变化。
"""
import codecs
import re
import sys

ROOT = r"D:\youhua\GameBoost-DLSSG"


_NL = "\n"   # 由 load() 按文件实际行尾确定 —— 绝不能在 sub() 里现算：
             # 一旦某次替换往纯 LF 文件里注入了 CRLF，后续每次 sub 都会误判成 CRLF 文件，
             # 于是所有锚点都匹配不上（本脚本第一版就栽在这里，报错还指向"锚点不存在"）。


def load(p):
    global _NL
    raw = open(p, "rb").read()
    bom = raw.startswith(codecs.BOM_UTF8)
    t = raw.decode("utf-8-sig")
    _NL = "\r\n" if "\r\n" in t else "\n"
    return t, bom


def save(p, t, bom):
    out = t.encode("utf-8")
    if bom:
        out = codecs.BOM_UTF8 + out
    open(p, "wb").write(out)


def nl(s):
    return s.replace("\n", _NL)


def sub(t, old, new, cnt=1, label=""):
    o = nl(old)
    n = nl(new)
    c = t.count(o)
    if c != cnt:
        raise AssertionError("anchor count=%d want=%d [%s] :: %r" % (c, cnt, label, old[:90]))
    return t.replace(o, n)


def cut_block(t, start_marker, end_finder, label):
    i = t.find(start_marker)
    if i < 0:
        raise AssertionError("start not found [%s]" % label)
    j = t.find(end_finder, i)
    if j < 0:
        raise AssertionError("end not found [%s]" % label)
    return i, j + len(end_finder)


# ======================================================================
# 1. Core.cs
# ======================================================================
p_core = ROOT + r"\src\Core.cs"
cs, cs_bom = load(p_core)
for nm in ("Knobs", "OptKnob", "ApplyKnob", "SetKnob", "AttachKnobKeys"):
    if nm in cs:
        raise AssertionError("符号已存在，先确认: " + nm)

# ---- C1. Config 字段 ----
cs = sub(cs, """        public bool DlssOverride = false;
""", """        public bool DlssOverride = false;

        // ---- 驱动 / 系统「具体取值」细调（v3.6.0）----
        // 起因：体检表能显示「期望值」，但期望值是程序写死的 —— 用户看得见、改不了。
        // 而着色器缓存上限（12 档）、DLSS 强制预设（K/L/M）、垂直同步模式、量子长度这些，
        // 最优值本来就随游戏、随驱动版本、随个人偏好变化，锁死在程序里等于替用户做了选择。
        // 这里把每一项落成配置值，默认 = 原硬编码值（行为零变化），改由「取值细调」对话框写入。
        // ⚠️ 着色器缓存上限用 -1 表示 0xFFFFFFFF（无限制）：JSON 里没有 uint，直接写
        //    4294967295 会被 Convert.ToInt32 溢出、被 catch 吞掉后静默取默认值（本文件踩过多回）。
        public int NvShaderCacheOn = 1;                              // 0x00198FFF 0=关 1=开
        public int NvShaderCacheSize = -1;                           // 0x00AC8497 -1=无限制(0xFFFFFFFF)
        public int NvDlssPresetLetter = 0x00FFFFFF;                  // 0x0010E41DF3 0xFFFFFF=使用推荐值
        public int NvDlssPresetProfile = 1;                          // 0x00634291 0=N/A 1=推荐 2=自定义
        public int NvCompPreRender = 1;                              // 0x007BA09E 竞技档最大预渲染帧数
        public int NvCompPowerMode = 1;                              // 0x1057EB71 竞技档电源管理模式
        public int NvCompVsync = 0x08416747;                         // 0x00A879CF 竞技档垂直同步=强制关
        public int NvAaaVsync = 0x60925292;                          // 0x00A879CF 3A/MMO/二游档垂直同步
        public int NvCompTexQuality = 0x14;                          // 0x00CE2691 竞技档纹理过滤质量
        public int NvBgFpsLimit = 0;                                 // 0x10835005 后台帧率上限 0=不限
        public int NetThrottle = -1;                                 // NetworkThrottlingIndex -1=禁用节流
""", 1, "C1 Config fields")

# ---- C2. Config.Load 解析 ----
cs = sub(cs, """                if (dlss != null) c.DlssOverride = GetBool(dlss, "override", false);
""", """                if (dlss != null) c.DlssOverride = GetBool(dlss, "override", false);
                // ---- 取值细调（v3.6.0，对应「取值细调」对话框）----
                // 默认值与原硬编码完全一致，所以老安装（config.json 里没有 nv 段）行为不变。
                var nvk = GetDict(root, "nv");
                if (nvk != null)
                {
                    c.NvShaderCacheOn = GetInt(nvk, "shaderCacheOn", 1);
                    c.NvShaderCacheSize = GetInt(nvk, "shaderCacheSize", -1);
                    c.NvDlssPresetLetter = GetInt(nvk, "dlssForcedPreset", 0x00FFFFFF);
                    c.NvDlssPresetProfile = GetInt(nvk, "dlssPresetProfile", 1);
                    c.NvCompPreRender = GetInt(nvk, "compPrerenderedFrames", 1);
                    c.NvCompPowerMode = GetInt(nvk, "compPowerMode", 1);
                    c.NvCompVsync = GetInt(nvk, "compVsync", 0x08416747);
                    c.NvAaaVsync = GetInt(nvk, "aaaVsync", 0x60925292);
                    c.NvCompTexQuality = GetInt(nvk, "compTextureQuality", 0x14);
                    c.NvBgFpsLimit = GetInt(nvk, "backgroundFpsLimit", 0);
                }
""", 1, "C2 Config.Load nv")

cs = sub(cs, """                if (net != null) { c.NetEnable = GetBool(net, "enable", true); c.NagleOff = GetBool(net, "nagleOff", true); c.SysResp = GetInt(net, "systemResponsiveness", 10); }
""", """                if (net != null)
                {
                    c.NetEnable = GetBool(net, "enable", true);
                    c.NagleOff = GetBool(net, "nagleOff", true);
                    c.SysResp = GetInt(net, "systemResponsiveness", 10);
                    c.NetThrottle = GetInt(net, "throttlingIndex", -1);   // v3.6.0：原来写死 -1，现在可调
                }
""", 1, "C2b Config.Load net")

# ---- C3. CompSettings / AaaSettings 改成方法 ----
i = cs.find("        static readonly uint[][] CompSettings")
if i < 0:
    raise AssertionError("CompSettings head not found")
# 从 AaaSettings 起找它的收尾
k = cs.find("        static readonly uint[][] AaaSettings", i)
e = cs.find("\n        };\n", k)
if e < 0:
    raise AssertionError("AaaSettings end not found")
e = e + len("\n        };\n")
NEW_DRV = """        // v3.6.0：这里原来是两个 static readonly 硬编码数组。改成按配置取值 ——
        // 体检表能显示"期望值"，但期望值是程序写死的，用户看得见、改不了；而着色器缓存上限
        // （12 档）、DLSS 强制预设字母、垂直同步模式这些，最优值本来就因游戏/因驱动版本而异。
        // 默认值刻意与原硬编码完全一致，所以行为零变化。
        public static uint[][] CompSettings()
        {
            var c = Program.Cfg;
            return new uint[][]
            {
                new uint[] { 0x1057EB71, (uint)c.NvCompPowerMode },                              // 电源管理模式
                new uint[] { 0x007BA09E, (uint)c.NvCompPreRender },                              // 最大预渲染帧数
                new uint[] { 0x00CE2691, unchecked((uint)c.NvCompTexQuality) },                  // 纹理过滤质量
                new uint[] { 0x00A879CF, unchecked((uint)c.NvCompVsync) },                       // 垂直同步
                new uint[] { NvDrsDb.IdShaderCacheEnable, unchecked((uint)c.NvShaderCacheOn) },  // 着色器缓存开关
                new uint[] { NvDrsDb.IdShaderCacheSize, unchecked((uint)c.NvShaderCacheSize) },  // 着色器缓存上限
            };
        }

        // 3A / MMO / 二游档（画质与帧率稳定优先）
        public static uint[][] AaaSettings()
        {
            var c = Program.Cfg;
            return new uint[][]
            {
                new uint[] { 0x1057EB71, (uint)c.NvCompPowerMode },
                new uint[] { NvDrsDb.IdShaderCacheEnable, unchecked((uint)c.NvShaderCacheOn) },
                new uint[] { NvDrsDb.IdShaderCacheSize, unchecked((uint)c.NvShaderCacheSize) },
                new uint[] { 0x00A879CF, unchecked((uint)c.NvAaaVsync) },
            };
        }
"""
cs = cs[:i] + nl(NEW_DRV) + cs[e:]

# ---- C4. 调用点 ----
cs = sub(cs, "WithDlss(CompSettings)", "WithDlss(CompSettings())", 1, "C4a")
cs = sub(cs, "WithDlss(AaaSettings)", "WithDlss(AaaSettings())", 3, "C4b")

# ---- C5. WithDlss 用配置值 ----
cs = sub(cs, """            list.Add(new uint[] { NvDrsDb.IdDlssDllOverride, 0x1 });                  // 启用 DLL 覆盖
            list.Add(new uint[] { NvDrsDb.IdDlssPresetProfile, 0x1 });                // 预设档 = 推荐
            list.Add(new uint[] { NvDrsDb.IdDlssPresetLetter, 0x00FFFFFF });          // 强制预设 = 使用推荐值
""", """            var c = Program.Cfg;
            list.Add(new uint[] { NvDrsDb.IdDlssDllOverride, 0x1 });                  // 启用 DLL 覆盖
            list.Add(new uint[] { NvDrsDb.IdDlssPresetProfile, unchecked((uint)c.NvDlssPresetProfile) });
            list.Add(new uint[] { NvDrsDb.IdDlssPresetLetter, unchecked((uint)c.NvDlssPresetLetter) });
""", 1, "C5 WithDlss")

# ---- C6. StatusItem 加 Key ----
cs = sub(cs, """    public class StatusItem
    {
        public string Item; public string Current; public string Expected; public string Status;
    }
""", """    public class StatusItem
    {
        public string Item; public string Current; public string Expected; public string Status;
        // v3.6.0：可调项挂上 OptKnob 的键 —— 体检表双击这一行就能直接改它的取值。
        // 空 = 只读核验项（HPET / GPU 中断亲和 / rBAR 全局强开这类，改错会让设备消失，不代改）。
        public string Key;
    }

    // ---- 「具体取值」旋钮（v3.6.0）----
    // 体检表只能回答"目标和现状一不一致"；这张表回答"目标值本身选哪一档"，并且让用户自己选。
    // 只开放「改坏了能改回来」的项：全部走 config.json + 原有备份机制。
    public class OptKnob
    {
        public string Key;        // 唯一键，也是体检表双击的定位键
        public string Sec;        // config.json 段名
        public string Name;       // config.json 键名
        public bool IsBool;       // true = 该键在 JSON 里是 true/false
        public string Group;      // 对话框分组标题
        public string Title;      // 显示名
        public string Hint;       // 这一档到底影响什么（必须写人话）
        public string[] Labels;
        public int[] Values;
        public string Apply;      // 保存后怎么落到系统：drv / hags / sched / proc / net
        public bool Restart;      // 需重启生效
        public int Value;         // 当前配置值

        public int Index
        {
            get
            {
                for (int i = 0; i < Values.Length; i++) if (Values[i] == Value) return i;
                return -1;
            }
        }
        public string CurrentText
        {
            get { int i = Index; return i < 0 ? ("当前值 " + Value) : Labels[i]; }
        }
        public string ValueText(int raw)
        {
            for (int i = 0; i < Values.Length; i++) if (Values[i] == raw) return Labels[i];
            return raw.ToString();
        }
    }
""", 1, "C6 StatusItem/Key + OptKnob")

# ---- C7. 插入旋钮基础设施（放在 ApplyAll 之前）----
KNOB_CODE = r"""        // ============ 取值细调：旋钮表（v3.6.0） ============
        // 设计约束（别在后续维护里破坏它）：
        //   ① 只放「改坏了能改回来」的项。HPET 强制、GPU 中断绑核、MSI 强制转换、rBAR 全局强开
        //      这类多来源明确警告"改错会让设备从系统消失/无法启动"的，一律只做只读核验，不进这张表。
        //   ② 每一项都必须写清 Hint（这个值到底影响什么、代价是什么）。只给选项不给代价，
        //      等于把"用户以为自己懂了"变成新的坑。
        //   ③ 驱动档位的取值与名称逐个核对过 NVIDIA Profile Inspector 的 CustomSettingNames.xml，
        //      不是猜的（着色器缓存实际是 12 档，不是网上常说的 10 档）。
        public static List<OptKnob> Knobs()
        {
            var c = Cfg;
            var l = new List<OptKnob>();

            // ---------- DLSS 模型覆盖 ----------
            l.Add(new OptKnob
            {
                Key = "dlss.override", Sec = "dlss", Name = "override", IsBool = true, Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "模型覆盖总开关",
                Hint = "开启后给各游戏档写入「DLL 覆盖=开 + 预设档 + 强制预设」。RTX 30 系能吃到 DLSS 4 的 Transformer 超分（只有多帧生成锁 50 系）。与 0.3.x 帧生成代理都可能在 DLL 层接管，先确认不打架再开。",
                Labels = new string[] { "关闭（默认）", "开启" }, Values = new int[] { 0, 1 },
                Value = c.DlssOverride ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "nv.dlssPresetLetter", Sec = "nv", Name = "dlssForcedPreset", Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "强制预设字母",
                Hint = "Transformer Gen1 = J/K，Gen2 = L/M。L 只在 Ultra Performance 档起效、M 只在 Performance 档起效，而且 Gen2 在 30 系有约 20% 性能税 —— 所以「使用推荐值」通常最好，别盲目追新。",
                Labels = new string[] { "使用推荐值（驱动决定，推荐）", "Preset K（Transformer Gen1，30 系稳妥）",
                                        "Preset L（Gen2，Ultra Performance 档生效）", "Preset M（Gen2，Performance 档生效，30 系约 -20% 帧）",
                                        "Preset J（Gen1 初版）", "不干预（N/A）" },
                Values = new int[] { 0x00FFFFFF, 0x0B, 0x0C, 0x0D, 0x0A, 0x00 },
                Value = c.NvDlssPresetLetter
            });
            l.Add(new OptKnob
            {
                Key = "nv.dlssPresetProfile", Sec = "nv", Name = "dlssPresetProfile", Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "预设档（Forced Model Preset Profile）",
                Hint = "全名是 DLSS - Forced Model Preset Profile。如果上面的「强制预设字母」不生效，驱动要求把这一项一起改（NVIDIA Profile Inspector 的说明原话）。默认「推荐」。",
                Labels = new string[] { "推荐（Recommended）", "自定义（Custom）", "不设置（N/A）" },
                Values = new int[] { 1, 2, 0 }, Value = c.NvDlssPresetProfile
            });

            // ---------- 着色器缓存 ----------
            l.Add(new OptKnob
            {
                Key = "nv.shaderCacheOn", Sec = "nv", Name = "shaderCacheOn", Apply = "drv",
                Group = "NVIDIA 驱动 · 着色器缓存（治「第一次遇到就卡」）",
                Title = "缓存开关",
                Hint = "关掉后驱动编译好的 shader 变体不落盘：每次遇到新材质组合都要现场编译，而且不会随游玩次数减少（不缓存就永远学不会）。",
                Labels = new string[] { "开启（推荐）", "关闭" }, Values = new int[] { 1, 0 },
                Value = c.NvShaderCacheOn
            });
            l.Add(new OptKnob
            {
                Key = "nv.shaderCacheSize", Sec = "nv", Name = "shaderCacheSize", Apply = "drv",
                Group = "NVIDIA 驱动 · 着色器缓存（治「第一次遇到就卡」）",
                Title = "缓存大小上限",
                Hint = "共 12 档。驱动按 LRU 淘汰，不是「设多大就占多大」 —— 本机 %LOCALAPPDATA%\\NVIDIA\\DXCache 目前约 1.25 GB。担心占盘位就选 4~16 GB，别选最后那档「关闭」（那不是省盘，等于禁用缓存）。",
                Labels = new string[] { "无限制（默认）", "100 GB", "16 GB", "10 GB", "8 GB", "5 GB", "4 GB",
                                        "1 GB", "512 MB", "256 MB", "128 MB", "关闭（=禁用缓存）" },
                Values = new int[] { -1, 0x19000, 0x4000, 0x2800, 0x2000, 0x1400, 0x1000,
                                     0x400, 0x200, 0x100, 0x80, 0x00 },
                Value = c.NvShaderCacheSize
            });

            // ---------- 竞技(FPS)档 ----------
            l.Add(new OptKnob
            {
                Key = "nv.compPrerenderedFrames", Sec = "nv", Name = "compPrerenderedFrames", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "最大预渲染帧数",
                Hint = "1 就是 NVIDIA 面板里的「低延迟·超高」。CPU 瓶颈时越低越跟手；GPU 瓶颈时设 1 会掉帧，那种情况选「跟随游戏设置」。",
                Labels = new string[] { "1（最低延迟，默认）", "2", "3", "4", "跟随游戏设置" },
                Values = new int[] { 1, 2, 3, 4, 0 }, Value = c.NvCompPreRender
            });
            l.Add(new OptKnob
            {
                Key = "nv.compPowerMode", Sec = "nv", Name = "compPowerMode", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "电源管理模式",
                Hint = "「最高性能优先」让 GPU 常驻高频：更跟手、更费电、温度更高。在意温度或笔记本上可以选「最佳功率」。",
                Labels = new string[] { "最高性能优先（默认）", "最佳功率", "自适应" },
                Values = new int[] { 1, 5, 0 }, Value = c.NvCompPowerMode
            });
            l.Add(new OptKnob
            {
                Key = "nv.compVsync", Sec = "nv", Name = "compVsync", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "垂直同步",
                Hint = "强制关闭 = 最低延迟但会撕裂。开了 G-Sync/FreeSync 的话，NVIDIA 官方建议：驱动这层保持关闭、游戏内 VSync 打开（配合 Reflex 由驱动自动限帧）。",
                Labels = new string[] { "强制关闭（默认）", "跟随游戏内设置", "强制开启", "快速同步（Fast Sync）" },
                Values = new int[] { 0x08416747, 0x60925292, 0x47814940, 0x18888888 }, Value = c.NvCompVsync
            });
            l.Add(new OptKnob
            {
                Key = "nv.compTextureQuality", Sec = "nv", Name = "compTextureQuality", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "纹理过滤质量",
                Hint = "降低过滤质量能省一点 GPU，代价是远处贴图发糊。竞技射击常用「高性能」。",
                Labels = new string[] { "高性能（默认）", "性能", "质量", "高质量" },
                Values = new int[] { 0x14, 0x0A, 0x00, -10 }, Value = c.NvCompTexQuality
            });

            // ---------- 3A / MMO / 二游档 ----------
            l.Add(new OptKnob
            {
                Key = "nv.aaaVsync", Sec = "nv", Name = "aaaVsync", Apply = "drv",
                Group = "NVIDIA 驱动 · 3A / MMO / 二游档",
                Title = "垂直同步",
                Hint = "画质档默认「跟随游戏内设置」—— 在没有 VRR 的屏幕上强制关闭必然撕裂，这类游戏帧率低，撕裂比延迟更碍眼。",
                Labels = new string[] { "跟随游戏内设置（默认）", "强制关闭", "强制开启", "快速同步（Fast Sync）" },
                Values = new int[] { 0x60925292, 0x08416747, 0x47814940, 0x18888888 }, Value = c.NvAaaVsync
            });

            // ---------- 驱动全局 ----------
            l.Add(new OptKnob
            {
                Key = "nv.backgroundFpsLimit", Sec = "nv", Name = "backgroundFpsLimit", Apply = "drv",
                Group = "NVIDIA 驱动 · 全局",
                Title = "后台应用帧率上限",
                Hint = "游戏切到后台时限制它的帧率，省电省热。注意这是写进各游戏档（per-profile）的，不是全局设置。",
                Labels = new string[] { "不限制（默认）", "60 FPS", "30 FPS", "20 FPS", "15 FPS", "10 FPS", "5 FPS" },
                Values = new int[] { 0, 60, 30, 20, 15, 10, 5 }, Value = c.NvBgFpsLimit
            });

            // ---------- Windows · GPU ----------
            l.Add(new OptKnob
            {
                Key = "hags.enable", Sec = "hags", Name = "enable", IsBool = true, Apply = "hags", Restart = true,
                Group = "Windows · GPU 与调度",
                Title = "硬件加速 GPU 计划（HAGS）",
                Hint = "开启可降低帧时间波动、让 GPU 自己管显存（NVIDIA 与多数指南推荐开）。少数高帧率竞技场景有「抖动反而变大」的反例报告 —— 那属于值得自己 A/B 一次的项。切换必须重启，不能游戏中途改。",
                Labels = new string[] { "开启（推荐，重启生效）", "关闭（重启生效）" }, Values = new int[] { 1, 0 },
                Value = c.HagsOn ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "scheduler.win32PrioritySeparation", Sec = "scheduler", Name = "win32PrioritySeparation",
                Apply = "sched", Restart = true,
                Group = "Windows · GPU 与调度",
                Title = "处理器计划（Win32PrioritySeparation）",
                Hint = "三个 2 位字段拼成：量子长短 / 可变或固定 / 前台加成倍数。38 = 前台量子 18、后台 6（Windows「程序」项的默认值，游戏推荐）；24 = 前后台都 36（Windows「后台服务」项）。数值与语义对照自微软官方文章《Master Your Quantum》。",
                Labels = new string[] { "38（0x26）短量子·可变·前台 3 倍 ← 默认/推荐",
                                        "36（0x24）短量子·可变·无前台加成",
                                        "40（0x28）短量子·固定·无加成",
                                        "22（0x16）长量子·可变·前台 3 倍",
                                        "20（0x14）长量子·可变·前台 2 倍",
                                        "24（0x18）长量子·固定·前后台同等",
                                        "2（0x02）系统默认（实测等价于 38）" },
                Values = new int[] { 38, 36, 40, 22, 20, 24, 2 }, Value = c.SchedSep
            });
            l.Add(new OptKnob
            {
                Key = "power.minProcessorState", Sec = "power", Name = "minProcessorState", Apply = "proc",
                Group = "Windows · GPU 与调度",
                Title = "处理器最小状态",
                Hint = "100% 让核心不降频（最跟手、最费电）；笔记本或夏天可降到 50%。注意工具的「硬件自适应」会自动把笔记本的 100% 压到 50%，那是防积热，不是没生效。",
                Labels = new string[] { "100%（默认，不降频）", "90%", "80%", "70%", "60%", "50%", "30%", "5%", "0%（允许深度降频）" },
                Values = new int[] { 100, 90, 80, 70, 60, 50, 30, 5, 0 }, Value = c.MinProc
            });
            l.Add(new OptKnob
            {
                Key = "power.maxProcessorState", Sec = "power", Name = "maxProcessorState", Apply = "proc",
                Group = "Windows · GPU 与调度",
                Title = "处理器最大状态",
                Hint = "降到 95% 以下会关掉睿频，几乎一定掉帧 —— 除非在做散热/功耗上限实验，否则保持 100%。",
                Labels = new string[] { "100%（默认）", "99%", "95%", "90%", "80%", "70%", "50%" },
                Values = new int[] { 100, 99, 95, 90, 80, 70, 50 }, Value = c.MaxProc
            });

            // ---------- Windows · 网络 ----------
            l.Add(new OptKnob
            {
                Key = "network.systemResponsiveness", Sec = "network", Name = "systemResponsiveness", Apply = "net",
                Group = "Windows · 网络",
                Title = "前台响应优先级（SystemResponsiveness）",
                Hint = "MMCSS 留给非多媒体任务的 CPU 百分比：越小越偏袒前台游戏。Windows 默认 20，游戏向常用 10，0 最激进（极端情况下可能卡音频）。",
                Labels = new string[] { "10（推荐）", "0（最激进）", "5", "15", "20（Windows 默认）", "30", "50（更保守）" },
                Values = new int[] { 10, 0, 5, 15, 20, 30, 50 }, Value = c.SysResp
            });
            l.Add(new OptKnob
            {
                Key = "network.nagleOff", Sec = "network", Name = "nagleOff", IsBool = true, Apply = "net",
                Group = "Windows · 网络",
                Title = "Nagle 算法（小包合并延迟）",
                Hint = "关闭 = 上传/动作包立即发，降低网络延迟感（TcpAckFrequency + TCPNoDelay = 1）。改成「保持默认」不会删除已经写进网卡的值，要用「恢复备份」才能还原。",
                Labels = new string[] { "关闭 Nagle（低延迟，推荐）", "保持系统默认" }, Values = new int[] { 1, 0 },
                Value = c.NagleOff ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "network.throttlingIndex", Sec = "network", Name = "throttlingIndex", Apply = "net",
                Group = "Windows · 网络",
                Title = "网络流量节流（NetworkThrottlingIndex）",
                Hint = "Windows 默认每处理 10 个数据包就打断一次多媒体流。禁用（-1 = 0xFFFFFFFF）让游戏流量不被节流；想恢复系统默认就选 10。",
                Labels = new string[] { "禁用节流（-1，推荐）", "10（Windows 默认）", "20", "30", "50" },
                Values = new int[] { -1, 10, 20, 30, 50 }, Value = c.NetThrottle
            });

            return l;
        }

        // 体检表行名 → 旋钮键。用显式映射而不是"逐行手写 Key"：条目顺序以后会变，
        // 手写的映射迟早漏掉一行，而漏掉的表现是"双击没反应"（不报错，很难发现）。
        static string KnobKeyOfItem(string item)
        {
            if (item == null) return null;
            switch (item)
            {
                case "驱动·着色器缓存开关": return "nv.shaderCacheOn";
                case "驱动·着色器缓存上限": return "nv.shaderCacheSize";
                case "驱动·DLSS 模型覆盖": return "dlss.override";
                case "驱动·DLSS 强制预设": return "nv.dlssPresetLetter";
                case "驱动·帧率上限(后台)": return "nv.backgroundFpsLimit";
                case "硬件加速GPU计划(HAGS)": return "hags.enable";
                case "CPU调度(Win32优先级分离)": return "scheduler.win32PrioritySeparation";
                case "处理器最小/最大状态": return "power.minProcessorState";
                case "前台响应优先级": return "network.systemResponsiveness";
                case "Nagle 算法(小包延迟)": return "network.nagleOff";
                case "网络流量节流": return "network.throttlingIndex";
            }
            return null;
        }

        static void AttachKnobKeys(List<StatusItem> list)
        {
            var map = new Dictionary<string, OptKnob>();
            foreach (var k in Knobs()) map[k.Key] = k;
            foreach (var it in list)
            {
                string kk = KnobKeyOfItem(it.Item);
                if (kk == null) continue;
                OptKnob k;
                if (!map.TryGetValue(kk, out k)) continue;
                it.Key = kk;
                if (k.Restart && it.Expected != null && it.Expected.IndexOf("重启") < 0)
                    it.Expected += "（改后需重启）";
            }
        }

        // 某个旋钮当前档位的显示文案（体检表用）
        public static string KnobText(string key)
        {
            foreach (var k in Knobs()) if (k.Key == key) return k.CurrentText;
            return "—";
        }

        // 写 config.json → 重载配置。之后所有读取（含一键优化）都以新值为准。
        public static bool SetKnob(OptKnob k, int rawValue)
        {
            bool ok = k.IsBool ? SetConfigBool(k.Sec, k.Name, rawValue != 0)
                               : SetConfigInt(k.Sec, k.Name, rawValue);
            if (!ok) return false;
            try { ReloadCfg(Config.Load(ConfigPath)); } catch { }
            return true;
        }

        // 把配置值落到系统。按 Apply 分派，同一类只跑一次（否则改 4 个驱动项就要写 4 遍 nvdrsdb）。
        public static string ApplyKnob(OptKnob k)
        {
            try
            {
                switch (k.Apply)
                {
                    case "drv":
                        return NvDrs.ApplyGameProfiles(FpsList(), Cfg.MmoGames, Cfg.AaaGames, Cfg.GachaGames);
                    case "hags":
                        {
                            string hp = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
                            BackupReg(hp, "HwSchMode");
                            WriteReg(hp, "HwSchMode", Cfg.HagsOn ? 2 : 1, RegistryValueKind.DWord);
                            return "HwSchMode=" + (Cfg.HagsOn ? 2 : 1) + "（重启生效）";
                        }
                    case "sched":
                        {
                            string sp = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl";
                            BackupReg(sp, "Win32PrioritySeparation");
                            WriteReg(sp, "Win32PrioritySeparation", Cfg.SchedSep, RegistryValueKind.DWord);
                            return "Win32PrioritySeparation=" + Cfg.SchedSep;
                        }
                    case "proc":
                        RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN " + Cfg.MinProc);
                        RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX " + Cfg.MaxProc);
                        RunCmd("powercfg", "/setactive SCHEME_CURRENT");
                        return "处理器状态 " + Cfg.MinProc + "% / " + Cfg.MaxProc + "%";
                    case "net":
                        {
                            string mm = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
                            BackupReg(mm, "NetworkThrottlingIndex");
                            BackupReg(mm, "SystemResponsiveness");
                            // 注意写 int：0xFFFFFFFF 是 uint 字面量，SetValue 的 DWord 只收 int，
                            // 传 uint 会抛 ArgumentException 并被吞掉（表现为"优化完仍显示未设置"，本文件踩过）
                            WriteReg(mm, "NetworkThrottlingIndex", Cfg.NetThrottle, RegistryValueKind.DWord);
                            WriteReg(mm, "SystemResponsiveness", Cfg.SysResp, RegistryValueKind.DWord);
                            int n = 0;
                            if (Cfg.NagleOff)
                            {
                                try
                                {
                                    using (var ifaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                                    {
                                        if (ifaces != null)
                                            foreach (var sub in ifaces.GetSubKeyNames())
                                            {
                                                string q = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
                                                WriteReg(q, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                                                WriteReg(q, "TCPNoDelay", 1, RegistryValueKind.DWord);
                                                n++;
                                            }
                                    }
                                }
                                catch { }
                            }
                            return "节流=" + Cfg.NetThrottle + " / 前台响应=" + Cfg.SysResp
                                 + " / Nagle=" + (Cfg.NagleOff ? ("已关闭(" + n + " 个网卡)") : "未改动");
                        }
                }
            }
            catch (Exception ex) { return "应用失败：" + ex.Message; }
            return "";
        }

"""
KNOB_CODE = nl(KNOB_CODE)
cs = sub(cs, "        public static void ApplyAll(Action<string> log)", KNOB_CODE + "        public static void ApplyAll(Action<string> log)", 1, "C7 knob infra")

# ---- C8. NetworkOptimize 用可调值 ----
cs = sub(cs, """            WriteReg(mmPath, "NetworkThrottlingIndex", -1, RegistryValueKind.DWord);
            Log("已禁用网络流量节流");
""", """            WriteReg(mmPath, "NetworkThrottlingIndex", Cfg.NetThrottle, RegistryValueKind.DWord);
            Log(Cfg.NetThrottle == -1 ? "已禁用网络流量节流" : ("网络流量节流 = " + Cfg.NetThrottle));
""", 1, "C8 throttle")

# ---- C9. 体检表：改名 + 补两行 + 挂 Key ----
cs = sub(cs, '                    Item = "驱动·着色器缓存",', '                    Item = "驱动·着色器缓存开关",', 1, "C9a rename")

# 在「驱动·帧率上限(后台)」那一行之后插入两行新的可调项
anchor = '                    Item = "驱动·帧率上限(后台)",'
i = cs.find(anchor)
if i < 0:
    raise AssertionError("C9b anchor not found")
j = cs.find("});", i)
if j < 0:
    raise AssertionError("C9b end not found")
j += 3
NEWROWS = r"""

                // v3.6.0：把这两个值单独拉出来 —— 它们是「能给出具体操作」的典型，
                // 双击行即可在 12 档 / 6 个预设里改（此前只能看"期望值"）。
                list.Add(new StatusItem
                {
                    Item = "驱动·着色器缓存上限",
                    Current = KnobText("nv.shaderCacheSize"),
                    Expected = "双击本行从 12 档里选（驱动按 LRU 淘汰，不是「设多大就占多大」）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 强制预设",
                    Current = Cfg.DlssOverride ? KnobText("nv.dlssPresetLetter") : "未启用（先开上面的「模型覆盖总开关」）",
                    Expected = "双击本行选择；30 系建议「使用推荐值」，M 预设约 -20% 帧",
                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });"""
cs = cs[:j] + NEWROWS + cs[j:]

cs = sub(cs, """            return list;
        }

        // ============ 恢复 ============""", """            // 把可调旋钮挂到对应行上（体检表双击 → 直接改取值）
            AttachKnobKeys(list);

            return list;
        }

        // ============ 恢复 ============""", 1, "C9c attach")

save(p_core, cs, cs_bom)
print("[ok] Core.cs")

# ======================================================================
# 2. Ui.cs
# ======================================================================
p_ui = ROOT + r"\src\Ui.cs"
ui, ui_bom = load(p_ui)

# ---- U1. 体检表：双击 + 按钮 + 文案 ----
ui = sub(ui, """            var s2 = new Sec("优化项明细（当前系统实际设置）");
            lvOpt = NewList(new string[] { "优化项", "状态", "当前值", "期望值" }, new int[] { 260, 70, 250, 0 }, Theme.S(250));
            lvOpt.Resize += delegate { FillLastColumn(lvOpt, Theme.S(160)); };
            s2.Block(lvOpt, Theme.S(250));
            s2.Body("状态含义：「已生效」= 当前值与优化目标一致；「未生效」= 被系统更新或驱动重置；「需处理」= 读取失败或需要管理员权限。");
""", """            var s2 = new Sec("优化项明细（当前系统实际设置）");
            lvOpt = NewList(new string[] { "优化项", "状态", "当前值", "期望值" }, new int[] { 260, 70, 250, 0 }, Theme.S(250));
            lvOpt.Resize += delegate { FillLastColumn(lvOpt, Theme.S(160)); };
            // v3.6.0：体检表不再只是"看"。带 ⚙ 的行双击即可改这一项的具体取值
            //（着色器缓存 12 档、DLSS 预设字母、量子长度、前后台帧率上限…）。
            lvOpt.DoubleClick += delegate { OpenKnobForRow(); };
            var btnKnobSel = new FlatBtn(); btnKnobSel.Text = "调整选中项"; btnKnobSel.Width = Theme.S(100);
            btnKnobSel.Click += delegate { OpenKnobForRow(); };
            var btnKnobAll = new FlatBtn(); btnKnobAll.Text = "取值细调…"; btnKnobAll.Width = Theme.S(100);
            btnKnobAll.Click += delegate { ShowKnobDialog(null); };
            s2.Buttons(btnKnobSel, btnKnobAll);
            s2.Block(lvOpt, Theme.S(250));
            s2.Body("状态含义：「已生效」= 当前值与优化目标一致；「未生效」= 被系统更新或驱动重置；「需处理」= 读取失败或需要管理员权限。"
                  + "\\n带 ⚙ 的行可调：点选后按「调整选中项」（或直接双击该行）就能改这一项的具体档位；其余行是只读核验项。");
""", 1, "U1 s2")

# ---- U2. FillList：标记可调 + 存 Tag ----
ui = sub(ui, """                    var lvi = new ListViewItem(it.Item);
                    lvi.SubItems.Add(st);""", """                    // ⚙ = 该项有可调档位（对应 Program.Knobs()）。用后缀而不是加一列：
                    // 列的可用宽度本来就紧，"能改"这件事只需要一个可识别的记号 + 双击。
                    var lvi = new ListViewItem(string.IsNullOrEmpty(it.Key) ? it.Item : (it.Item + "  ⚙"));
                    lvi.Tag = it.Key;
                    lvi.SubItems.Add(st);""", 1, "U2 FillList")

# ---- U3. 取值细调对话框 ----
DIALOG = r"""
        // ---------------------- 取值细调（v3.6.0） ----------------------
        // 这个对话框存在的理由：一键优化只能给出"程序认为的最优值"，而像着色器缓存上限（12 档）、
        // DLSS 强制预设字母、量子长度这些，最优值本来就因游戏、因驱动版本、因个人偏好而异。
        // 所以凡是「能给出具体操作」的项，就把档位摊开让用户自己选 —— 只读核验项不在这里
        //（HPET / GPU 中断绑核 / rBAR 全局强开 改错会让设备消失，工具只做核验、不代改）。
        void OpenKnobForRow()
        {
            if (lvOpt == null || lvOpt.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先在下面的列表里点选一行（带 ⚙ 的行可以调）。", "取值细调",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sel = lvOpt.SelectedItems[0];
            string key = sel.Tag as string;
            if (string.IsNullOrEmpty(key))
            {
                MessageBox.Show(this, "「" + sel.Text.Replace("  ⚙", "") + "」是只读核验项，本工具不代改。\n\n"
                    + "原因见「期望值」列的一句话说明：这一项要么没有「更优的档位」可选（默认即最优），\n"
                    + "要么改错的代价是设备消失 / 系统异常（HPET、GPU 中断绑核、MSI 强制转换、rBAR 全局强开）。",
                    "取值细调", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowKnobDialog(key);
        }

        void ShowKnobDialog(string focusKey)
        {
            var knobs = Program.Knobs();
            if (knobs.Count == 0) { MessageBox.Show(this, "没有可调项。", "取值细调"); return; }

            var dlg = new Form();
            dlg.Text = "取值细调（保存后立即写入系统）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(540), Theme.S(420));
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(640));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var combos = new List<RoundCombo>();
            string lastGroup = null;
            RoundCombo focusCb = null;
            foreach (var k in knobs)
            {
                Sec sec;
                if (k.Group != lastGroup)
                {
                    sec = new Sec(k.Group);
                    pg.Add(sec);
                    lastGroup = k.Group;
                }
                else sec = (Sec)pg.Controls[pg.Controls.Count - 1];

                var cb = new RoundCombo();
                cb.Tag = k;
                cb.Width = Theme.S(340);
                for (int i = 0; i < k.Labels.Length; i++) cb.Items.Add(k.Labels[i]);
                if (k.Index >= 0) cb.SelectedIndex = k.Index;
                else
                {
                    // 当前值不在档位表里（例如别处写过一个工具不提供的值）。
                    // 必须预选最后那条"不在档位表中"，否则用户只是打开对话框再点保存，
                    // 就会被静默改成第 0 档 —— 这是"我只想看看"变成"它自己改了"的经典事故。
                    cb.Items.Add("（当前值 " + k.Value + "，不在档位表中 —— 不动它）");
                    cb.SelectedIndex = k.Labels.Length;
                }

                string title = k.Title + (k.Restart ? "（需重启）" : "");
                sec.Row(title, cb);
                if (!string.IsNullOrEmpty(k.Hint)) sec.Body(k.Hint);
                combos.Add(cb);
                if (focusKey != null && k.Key == focusKey) focusCb = cb;
            }

            var sNote = new Sec("怎么用 / 边界");
            sNote.Body("· 每一项都对应 config.json 里的一个键：保存时先写回文件，再写入系统，所以重启程序后仍是你选的档位。\\n"
                     + "· 标「需重启」的是 Windows 注册表项（HAGS / 量子长度），改完当次不生效。\\n"
                     + "· 「NVIDIA 驱动 ·」开头的写在 nvdrsdb 里，重启游戏后生效；同一类只写一次，不会反复刷新驱动库。\\n"
                     + "· 这里只放「改坏了能改回来」的项。HPET 强制、GPU 中断绑核、MSI 强制转换、rBAR 全局强开\n"
                     + "   属于「改错会让设备消失 / 系统起不来」的类别，多来源明确警告，本工具只做只读核验。");
            pg.Add(sNote);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存并应用"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(104);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            pg.Reflow();
            if (focusCb != null)
            {
                // 双击体检表进来时，把焦点落在那一项上（用户点的就是它，不该让他自己找）
                try { dlg.Shown += delegate { focusCb.Focus(); }; } catch { }
            }

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var changed = new List<OptKnob>();
            int failed = 0;
            foreach (var cb in combos)
            {
                var k = (OptKnob)cb.Tag;
                int idx = cb.SelectedIndex;
                if (idx < 0 || idx >= k.Values.Length) continue;   // 选中"不在档位表中"那一项 = 不动
                int want = k.Values[idx];
                if (want == k.Value) continue;
                if (Program.SetKnob(k, want)) changed.Add(k);
                else failed++;
            }

            // 同一类只应用一次：改 4 个驱动项不该写 4 遍 nvdrsdb
            var kinds = new List<string>();
            foreach (var k in changed) if (!kinds.Contains(k.Apply)) kinds.Add(k.Apply);
            foreach (var kind in kinds)
            {
                OptKnob probe = changed.Find(delegate(OptKnob x) { return x.Apply == kind; });
                string r = Program.ApplyKnob(probe);
                if (!string.IsNullOrEmpty(r)) Log("[取值细调 · " + kind + "] " + r);
            }

            Cfg = Config.Load(Program.ConfigPath);
            Program.ReloadCfg(Cfg);
            Log("取值细调已保存：改动 " + changed.Count + " 项，应用到系统 " + kinds.Count + " 组"
                + (failed > 0 ? ("，写配置失败 " + failed + " 项") : ""));
            RefreshGridAsync();
        }
"""
DIALOG = nl(DIALOG)
ui = sub(ui, "        // ---------------------- 自定义优化项 ----------------------",
         DIALOG + nl("\n        // ---------------------- 自定义优化项 ----------------------"), 1, "U3 dialog")

# ---- U4. 自定义优化项对话框加一个入口按钮 ----
ui = sub(ui, """            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            pg.Reflow();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // 虚拟化是唯一「勾上就废掉一整类功能」的选项，落盘前单独确认一次。""",
         """            // 这个对话框管"开不开"，具体取值（档位）在另一个对话框里。两件事分开：
            // 开关是二选一，档位是一组取舍，混在一起用户会以为勾上就用的是他想要的档。
            var btnKnobs = new FlatBtn(); btnKnobs.Text = "取值细调…"; btnKnobs.Width = Theme.S(104);
            btnKnobs.Click += delegate { ShowKnobDialog(null); };
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            bar.Controls.Add(btnKnobs);
            dlg.Controls.Add(bar);

            pg.Reflow();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // 虚拟化是唯一「勾上就废掉一整类功能」的选项，落盘前单独确认一次。""", 1, "U4 entry")

save(p_ui, ui, ui_bom)
print("[ok] Ui.cs")

# ======================================================================
# 3. config.json / installer/config.default.json
# ======================================================================
NV_JSON = """  "nv": {
    "_comment": "【取值细调 · v3.6.0】这里每一条都对应体检表里带 ⚙ 的行（双击即可改），也对应「取值细调」对话框。默认值 = 旧的硬编码值，所以升级后行为不变。驱动层取值写入 nvdrsdb，重启游戏生效；改回默认不会自动撤销已写入系统的值，用「恢复备份」整体还原。",
        "_comment_size": "shaderCacheSize: 12 档。0=关闭(禁用缓存) / 128=128MB / 256=256MB / 512=512MB / 1024=1GB / 4096=4GB / 5120=5GB / 8192=8GB / 10240=10GB / 16384=16GB / 102400=100GB / -1=无限制(0xFFFFFFFF)。别写成 4294967295 —— JSON 读回时 Convert.ToInt32 会溢出并静默取默认值。",
        "_comment_preset": "dlssForcedPreset: 16777215=使用推荐值 / 10=Preset J / 11=Preset K(Gen1) / 12=Preset L(Gen2) / 13=Preset M(Gen2) / 0=不干预。30 系建议保持「推荐值」：L 只在 Ultra Performance 档起效、M 只在 Performance 档起效，且 Gen2 在 30 系有约 20% 性能税。",
        "_comment_vsync": "compVsync / aaaVsync: 138504007(0x8416747)=强制关 / 1620202130(0x60925292)=跟随游戏 / 1199655232(0x47814940)=强制开 / 411601032(0x18888888)=快速同步。",
    "_comment_bglimit": "backgroundFpsLimit: 0=不限制 / 其余=目标 FPS。只写进各游戏档（per-profile）。",
    "shaderCacheOn": 1,
    "shaderCacheSize": -1,
    "dlssForcedPreset": 16777215,
    "dlssPresetProfile": 1,
    "compPrerenderedFrames": 1,
    "compPowerMode": 1,
    "compVsync": 138504007,
    "aaaVsync": 1620202130,
    "compTextureQuality": 20,
    "backgroundFpsLimit": 0
  },
"""


def add_nv(t):
    if '"nv"' in t:
        return t
    i = t.rstrip().rfind("}")
    head = t[:i].rstrip()
    tail = t[i:]
    # NV_JSON 自带结尾的 "},"（它是按"插在中间"写的）。追加到对象末尾时必须去掉那个逗号，
    # 否则得到 "}," + "}" = 尾部多余逗号 → JavaScriptSerializer 整份配置解析失败
    # → 全部配置静默回默认（本文件历史上出过两次同类事故，别再犯）。
    body = nl(NV_JSON).rstrip().rstrip(",")
    return head + "," + _NL + body + _NL + tail


for rel in (r"\config.json", r"\installer\config.default.json"):
    p = ROOT + rel
    t, bom = load(p)
    if '"throttlingIndex"' not in t:
        m = re.search(r'("network"\s*:\s*\{)', t)
        if not m:
            raise AssertionError("network section not found in " + rel)
        ins = m.end()
        add = (_NL + '    "_comment_throttle": "NetworkThrottlingIndex: -1=禁用节流(推荐) / 10=Windows 默认 / 20·30·50 更保守。改完立即生效；体检表里带 ⚙ 的行可双击改。",'
                   + _NL + '    "throttlingIndex": -1,')
        t = t[:ins] + add + t[ins:]
    t = add_nv(t)
    save(p, t, bom)
    print("[ok]", rel)
print("ALL DONE")
