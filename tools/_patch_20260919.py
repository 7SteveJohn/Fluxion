# -*- coding: utf-8 -*-
"""一次性补丁：把「驱动层只读核验(A类) + DLSS 模型覆盖(B1，默认关)」写进源码。

为什么用脚本而不是 Edit：同一轮里对同一文件发多个 Edit 会互相覆盖（2026-09-18 踩过），
本脚本按「读一次 → 全部替换 → 写一次」的原子方式落盘，且每处替换都断言锚点唯一。
"""
import codecs
import sys

ROOT = r"D:\youhua\GameBoost-DLSSG"


def load(p):
    raw = open(p, "rb").read()
    bom = raw.startswith(codecs.BOM_UTF8)
    return raw.decode("utf-8-sig"), bom


def save(p, txt, bom):
    out = txt.encode("utf-8")
    if bom:
        out = codecs.BOM_UTF8 + out
    open(p, "wb").write(out)


def rep(txt, old, new, tag):
    n = txt.count(old)
    if n != 1:
        print("  [FAIL] %-28s 锚点出现 %d 次（要求 1 次）" % (tag, n))
        return None
    print("  [ ok ] %-28s" % tag)
    return txt.replace(old, new)


# ============================================================ Core.cs
CORE = ROOT + r"\src\Core.cs"
core, core_bom = load(CORE)
core_orig = core

NVDRSDB = r'''
    // ==================== NVIDIA 驱动配置只读核验（直读 nvdrsdb） ====================
    // 为什么不用 NVAPI 读：NvAPI_DRS_GetSetting(0x73BF8338) 在本机驱动上必然触发访问违例，
    //   改走 EnumSettings(0xAE3039DA) 返回 rc=-9 —— 两条路都试过，想读只能绕开 NVAPI。
    // 数据来源：%ProgramData%\NVIDIA Corporation\Drs\nvdrsdb0.bin / nvdrsdb1.bin。
    //   ⚠️ NVIDIA 控制面板的 3D 设置全在这两个二进制里，**注册表里什么都没有** ——
    //   2026-09-19 因为只查了注册表，把「全局着色器缓存被禁用」误判成"驱动默认"，追了两天。
    // 记录格式（逆向所得，已与 NVIDIA Profile Inspector 的 CustomSettingNames.xml 交叉验证）：
    //   每条设置 = 固定 16 字节：A4 00 10 00 | settingId(u32 LE) | 0x0010xx | value(u32 LE)
    //   第 3 个字段：0x1000 = 驱动内置默认值；低位非零（实测 0x1002）= 该档被显式设置过。
    public static class NvDrsDb
    {
        // 常用 settingId（逐个与 NPI 的自定义设置表核对过，不是猜的）
        public const uint IdShaderCacheEnable = 0x00198FFF;   // 着色器缓存开关      0=关 / 1=开
        public const uint IdShaderCacheSize = 0x00AC8497;     // 着色器缓存大小      0=禁用 / 0xFFFFFFFF=无限制 / 其余=MB
        public const uint IdDlssDllOverride = 0x10E41E01;     // DLSS - Enable DLL Override
        public const uint IdDlssPresetLetter = 0x10E41DF3;    // DLSS - Forced Preset Letter
        public const uint IdDlssPresetProfile = 0x00634291;   // DLSS - Forced Model Preset Profile
        public const uint IdRbarEnable = 0x000F00BA;          // rBAR - Enable
        public const uint IdFrameLimitBg = 0x10835005;        // Frame Rate Limiter - Background

        public static string Dir
        {
            get
            {
                try
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                        "NVIDIA Corporation", "Drs");
                }
                catch { return null; }
            }
        }

        public static bool Available()
        {
            try { return Dir != null && Directory.Exists(Dir) && Directory.GetFiles(Dir, "nvdrsdb*.bin").Length > 0; }
            catch { return false; }
        }

        // 记录 @+8 的低位非零 = 该档被显式设置过（用户或工具写的）；0x1000 = 驱动内置默认值
        public static bool IsUserSet(uint loc) { return (loc & 0xFu) != 0; }

        static string SizeText(uint v)
        {
            if (v == 0) return "★ 已禁用";
            if (v == 0xFFFFFFFFu) return "无限制";
            if (v >= 1024 && v % 1024 == 0) return (v / 1024) + " GB";
            return v + " MB";
        }

        // 一趟扫完两份库，把体检要用的项一次取全（不去反复读 2.6 MB 的二进制）
        public static NvDrsSnapshot Snapshot()
        {
            var s = new NvDrsSnapshot();
            try
            {
                if (!Available()) return s;
                bool swFound = false, swOn = false;
                var sizes = new List<uint>();
                int dlssTotal = 0, dlssOn = 0, rbarOn = 0, rbarOff = 0;
                bool frlFound = false; uint frl = 0;
                foreach (var f in Directory.GetFiles(Dir, "nvdrsdb*.bin"))
                {
                    byte[] d = File.ReadAllBytes(f);
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        uint id = BitConverter.ToUInt32(d, i + 4);
                        uint loc = BitConverter.ToUInt32(d, i + 8);
                        uint val = BitConverter.ToUInt32(d, i + 12);
                        i += 15;   // 记录独占 16 字节，跳过整条（避免 value 里恰好含签名字节时重复命中）
                        if (id == IdShaderCacheEnable) { swFound = true; if (val != 0) swOn = true; }
                        else if (id == IdShaderCacheSize && IsUserSet(loc)) sizes.Add(val);
                        else if (id == IdDlssDllOverride) { dlssTotal++; if (val != 0) dlssOn++; }
                        else if (id == IdRbarEnable) { if (val != 0) rbarOn++; else rbarOff++; }
                        else if (id == IdFrameLimitBg && !frlFound) { frlFound = true; frl = val; }
                    }
                }
                s.Ok = true;
                foreach (var v in sizes) if (v == 0) s.ShaderCacheDisabled = true;
                // 「着色器缓存大小」在控制面板里**只有全局设置、没有 per-game 覆盖**
                // → 全库里值为 0 的那一条必然是全局档，这是判定依据。
                string swTxt = swFound ? (swOn ? "开" : "★ 关") : "未记录(默认开)";
                var szSet = new List<string>();
                foreach (var v in sizes) { string t = SizeText(v); if (!szSet.Contains(t)) szSet.Add(t); }
                s.ShaderCache = "开关=" + swTxt + "，上限=" + (szSet.Count == 0 ? "继承驱动默认(16 GB)" : string.Join(" / ", szSet.ToArray()));
                s.DlssOverrideOn = dlssOn > 0;
                s.DlssOverride = dlssTotal == 0 ? "驱动库无记录"
                               : (dlssOn > 0 ? ("已开启 " + dlssOn + "/" + dlssTotal + " 档") : ("未开启（" + dlssTotal + " 档记录全为关）"));
                s.Rbar = (rbarOn + rbarOff == 0) ? "驱动库无记录"
                       : (rbarOn + " 个游戏开 / " + rbarOff + " 个关（均为驱动内置）");
                s.FrlBackground = !frlFound ? "无记录" : (frl == 0 ? "未限（0）" : ("值 " + frl));
            }
            catch { }
            return s;
        }
    }

    public class NvDrsSnapshot
    {
        public bool Ok;                    // 驱动配置库可读
        public bool ShaderCacheDisabled;   // 用户设置里出现 0 = 已禁用
        public string ShaderCache = "";
        public string DlssOverride = "";
        public bool DlssOverrideOn;
        public string Rbar = "";
        public string FrlBackground = "";
    }

    // DLSS 模型覆盖（NVIDIA 驱动层，per-game 档）
    // RTX 30 系能吃 DLSS 4 的 Transformer 超分模型（MFG 多帧生成才是 50 系专属）。
    // 本机实测：驱动库里 DLL 覆盖 9 条记录**值全是 0（关）** → 这项能力一直没启用。
    // 写法与既有 per-game 档同源（都是 NvDrs.SetDword）：
    //   0x10E41E01 = 1（启用 DLL 覆盖）、0x00634291 = 1（预设档=推荐）、
    //   0x10E41DF3 = 0x00FFFFFF（强制预设=使用推荐值）。
    // ⚠️ 预设**故意不写死 K/L/M**：NPI 2.4.0.31 说明写明 L 只在超性能档、M 只在性能档才有意义，
    //   其余档位仍用 K；而 RTX 30 系跑 Gen2 的 M 预设约有 20% 性能税 ——
    //   "越新越好"在 30 系上是错的，让驱动按游戏自己选（推荐值）才稳。
    // 默认关闭：与 0.3.x 帧生成代理存在潜在相互影响（两者都可能在 DXGI/DLL 层接管），先留 opt-in。
'''

core = rep(core, """            return setSetting(h, profile, ref s);
        }
    }

    public class Config
""", """            return setSetting(h, profile, ref s);
        }
    }
""" + NVDRSDB + """
    public class Config
""", "Core/NvDrsDb 类")

# ---- 给 ApplyGameProfiles 附加 DLSS 设置 ----
APPLY_OLD = """        // 一类游戏的写入：预定义档直写设置，未收录的进自建档（应用一次只能归属一个档）"""
APPLY_NEW = """        // 配置开启 DLSS 模型覆盖时，把三件套附加到该档的写入列表上（不改动原数组）
        static uint[][] WithDlss(uint[][] baseSettings)
        {
            if (!Cfg.DlssOverride) return baseSettings;
            var list = new List<uint[]>(baseSettings);
            list.Add(new uint[] { NvDrsDb.IdDlssDllOverride, 0x1 });                  // 启用 DLL 覆盖
            list.Add(new uint[] { NvDrsDb.IdDlssPresetProfile, 0x1 });                // 预设档 = 推荐
            list.Add(new uint[] { NvDrsDb.IdDlssPresetLetter, 0x00FFFFFF });          // 强制预设 = 使用推荐值
            return list.ToArray();
        }

        // 一类游戏的写入：预定义档直写设置，未收录的进自建档（应用一次只能归属一个档）"""

if core is not None:
    core = rep(core, APPLY_OLD, APPLY_NEW, "Core/WithDlss 方法")

for tag, old, new in [
    ("Core/fps 档接 DLSS",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, fps, CompSettings, ProfileName, out c1, out o1);",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, fps, WithDlss(CompSettings), ProfileName, out c1, out o1);"),
    ("Core/MMO 档接 DLSS",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, mmo, AaaSettings, ProfileName + \"-MMO\", out c2, out o2);",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, mmo, WithDlss(AaaSettings), ProfileName + \"-MMO\", out c2, out o2);"),
    ("Core/3A 档接 DLSS",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, aaa, AaaSettings, ProfileName + \"-AAA\", out c3, out o3);",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, aaa, WithDlss(AaaSettings), ProfileName + \"-AAA\", out c3, out o3);"),
    ("Core/二游档接 DLSS",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, gacha, AaaSettings, ProfileName + \"-Gacha\", out c4, out o4);",
     "ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, gacha, WithDlss(AaaSettings), ProfileName + \"-Gacha\", out c4, out o4);"),
]:
    if core is None:
        break
    core = rep(core, old, new, tag)

if core is not None:
    core = rep(core,
        """（画质档：电源管理=最高性能/着色器缓存=无限制/垂直同步=跟随游戏）";""",
        """（画质档：电源管理=最高性能/着色器缓存=无限制/垂直同步=跟随游戏）"
                       + (Cfg.DlssOverride ? "；已附加 DLSS 模型覆盖（DLL 覆盖=开 / 预设=推荐值）" : "");""",
        "Core/结果文案带 DLSS")

# ---- Config 字段 ----
if core is not None:
    core = rep(core,
        """        public bool VirtDisableEnable = false;
""",
        """        public bool VirtDisableEnable = false;
        // ---- DLSS 模型覆盖（NVIDIA 驱动层写入，非只读）----
        // RTX 30 系可用 DLSS 4 的 Transformer 超分模型（只有 MFG 多帧生成锁 50 系）。
        // 默认 false：与 0.3.x 帧生成代理存在潜在相互影响，先留 opt-in，用户在「自定义优化项」里开。
        // 取值细节见 NvDrsDb 上方的注释（预设故意交给驱动的"推荐值"，避免 30 系踩 M 的 20% 性能税）。
        public bool DlssOverride = false;
""",
        "Core/Config 字段")

if core is not None:
    core = rep(core,
        """                var virt = GetDict(root, "virtualization");
                if (virt != null) c.VirtDisableEnable = GetBool(virt, "enable", false);
""",
        """                var virt = GetDict(root, "virtualization");
                if (virt != null) c.VirtDisableEnable = GetBool(virt, "enable", false);
                var dlss = GetDict(root, "dlss");
                if (dlss != null) c.DlssOverride = GetBool(dlss, "override", false);
""",
        "Core/Config.Load")

# ---- DPC 快照方法 ----
DPC = r'''        // DPC / 中断分布快照（只读）。只取一次样 —— 目的是"下次再出现帧时间尖峰时有依据"，
        // 不是做持续监控（那是 LatencyMon 的活）。排除 _Total，只看具体核心。
        static StatusItem DpcSnapshotItem()
        {
            try
            {
                int maxDpc = -1, maxInt = -1; string dpcCore = null, intCore = null;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name,PercentDPCTime,InterruptsPersec FROM Win32_PerfFormattedData_PerfOS_Processor"))
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string nm = Convert.ToString(mo["Name"]);
                        if (nm == "_Total") continue;
                        int dpc = 0, itr = 0;
                        int.TryParse(Convert.ToString(mo["PercentDPCTime"]), out dpc);
                        int.TryParse(Convert.ToString(mo["InterruptsPersec"]), out itr);
                        if (dpc > maxDpc) { maxDpc = dpc; dpcCore = nm; }
                        if (itr > maxInt) { maxInt = itr; intCore = nm; }
                    }
                if (maxDpc < 0)
                    return new StatusItem { Item = "DPC / 中断分布", Current = "计数器不可用", Expected = "只读快照", Status = "🟡 已跳过" };
                return new StatusItem
                {
                    Item = "DPC / 中断分布",
                    Current = "DPC 峰值 " + maxDpc + "%(核 " + dpcCore + ")，中断峰值 " + maxInt + "/s(核 " + intCore + ")",
                    Expected = "DPC 峰值 <5%（持续偏高则查驱动/网卡中断）",
                    Status = maxDpc < 5 ? "✅ 正常" : "🟡 偏高"
                };
            }
            catch (Exception ex)
            {
                return new StatusItem { Item = "DPC / 中断分布", Current = "读取失败: " + ex.Message, Expected = "只读快照", Status = "🟡 已跳过" };
            }
        }

'''

if core is not None:
    core = rep(core,
        """        public static List<StatusItem> GetStatusItems()""",
        DPC + """        public static List<StatusItem> GetStatusItems()""",
        "Core/DpcSnapshotItem")

# ---- 体检表新增 A 类项 ----
STATUS_NEW = r'''
            // ---- 2026-09-19 新增：驱动层只读核验（直读 nvdrsdb）----
            // 起因：本机「全局着色器缓存被禁用」这件事工具看不见，只能靠外部脚本读，
            //   结果追了两天才定位。驱动层是"改完就忘"的重灾区，所以把可见性做进体检表。
            // 本段全部只读，不写任何值。
            var nvdb = NvDrsDb.Snapshot();
            if (nvdb.Ok)
            {
                list.Add(new StatusItem
                {
                    Item = "驱动·着色器缓存",
                    Current = nvdb.ShaderCache,
                    Expected = "开关=开，上限 ≥16 GB",
                    Status = nvdb.ShaderCacheDisabled ? "🔴 已禁用" : "✅ 正常"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 模型覆盖",
                    Current = nvdb.DlssOverride,
                    Expected = Cfg.DlssOverride ? "本工具已开启（配置：dlss.override=true）" : "可用 Transformer 超分（3060 Ti 支持；在自定义优化项里开）",
                    Status = nvdb.DlssOverrideOn ? "✅ 已开启" : "🟡 未开启"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·Resizable BAR",
                    Current = nvdb.Rbar,
                    Expected = "只读核验（驱动按游戏预设，勿全局强开）",
                    Status = "✅ 已读取"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·帧率上限(后台)",
                    Current = nvdb.FrlBackground,
                    Expected = "无记录或 0（不限制）",
                    Status = "✅ 已读取"
                });
            }
            else
            {
                list.Add(new StatusItem
                {
                    Item = "驱动层设置(直读 nvdrsdb)",
                    Current = "驱动配置库不可读",
                    Expected = "需已安装 NVIDIA 驱动",
                    Status = "🟡 已跳过"
                });
            }

            // 窗口化游戏的优化（SwapEffectUpgradeEnable）——微软口径：**只对窗口/无边框的 DX10/11 生效**，
            // 独占全屏基本无感。它的价值在反向：某游戏进了无边框模式却关着它 → 白丢一截延迟。
            string dxgs = Convert.ToString(ReadReg("HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences", "DirectXUserGlobalSettings"));
            bool swapUp = dxgs != null && dxgs.IndexOf("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase) >= 0;
            list.Add(new StatusItem
            {
                Item = "窗口化游戏的优化",
                Current = dxgs == null ? "未设置" : (swapUp ? "已开启" : "未开启"),
                Expected = "开启（仅影响窗口/无边框的 DX10/11 游戏）",
                Status = swapUp ? "✅ 已应用" : "🟡 未开启"
            });

            // 每程序「全屏优化」兼容标记（AppCompatFlags\Layers）——与工具写的**全局** FSE 键是两套存储。
            // 用户若手动给某个 exe 勾过「禁用全屏优化」，工具此前完全看不见。
            int compatCnt = 0;
            try
            {
                using (var lk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                {
                    if (lk != null)
                        foreach (var vn in lk.GetValueNames())
                        {
                            string vv = Convert.ToString(lk.GetValue(vn));
                            if (vv != null && vv.IndexOf("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) >= 0) compatCnt++;
                        }
                }
            }
            catch { }
            list.Add(new StatusItem
            {
                Item = "每程序全屏优化标记",
                Current = compatCnt == 0 ? "无（都用系统默认）" : (compatCnt + " 个程序被标记「禁用全屏优化」"),
                Expected = "无特殊需要时保持默认",
                Status = compatCnt == 0 ? "✅ 默认" : "🟡 见说明"
            });

            list.Add(DpcSnapshotItem());

'''

if core is not None:
    core = rep(core,
        """                    Status = cs2Mode == "独占全屏" ? "✅ 已应用" : "🟡 可改"
                });
""",
        """                    Status = cs2Mode == "独占全屏" ? "✅ 已应用" : "🟡 可改"
                });
""" + STATUS_NEW,
        "Core/体检表 A 类项")

if core is None or core == core_orig:
    print("Core.cs 未改动或中途失败，已放弃写入")
    sys.exit(1)
save(CORE, core, core_bom)
print("  -> Core.cs 已写入（%d -> %d 字节）" % (len(core_orig), len(core)))

# ============================================================ Ui.cs
UI = ROOT + r"\src\Ui.cs"
ui, ui_bom = load(UI)
ui_orig = ui
ui = rep(ui,
    """                new object[] { "关闭系统虚拟化（WSL2 / Docker / 安卓模拟器将不可用）", "virtualization", "enable", Cfg.VirtDisableEnable },
""",
    """                new object[] { "关闭系统虚拟化（WSL2 / Docker / 安卓模拟器将不可用）", "virtualization", "enable", Cfg.VirtDisableEnable },
                new object[] { "DLSS 模型覆盖（30 系可用 Transformer 超分，默认关）", "dlss", "override", Cfg.DlssOverride },
""",
    "Ui/配置表 DLSS 行")
if ui is None:
    sys.exit(1)
save(UI, ui, ui_bom)
print("  -> Ui.cs 已写入（%d -> %d 字节）" % (len(ui_orig), len(ui)))

# ============================================================ config.json / config.default.json
DLSS_JSON = '''  "dlss": {
    "_comment": "【默认关闭】DLSS 模型覆盖（NVIDIA 驱动层 per-game 写入）：启用驱动的 DLL 覆盖 + 预设档=推荐 + 强制预设=使用推荐值，让 RTX 20/30 系也能用上 DLSS 4 的 Transformer 超分模型（MFG 多帧生成才是 50 系专属）。预设故意交给驱动的「推荐值」而不写死 K/L/M——L 只在超性能档、M 只在性能档才有意义，且 30 系跑 M 约有 20% 性能税。默认关闭的原因：与 0.3.x 帧生成代理存在潜在相互影响（两者都可能在 DXGI/DLL 层接管），确认不打架再开。还原：「恢复备份」或把本项改回 false 后重新优化",
    "override": false
  },

'''

for p, bom, anchor in [
    (ROOT + r"\config.json", True,
     '''  "timer": {
    "_comment": "游戏运行期间自动把系统定时器分辨率压到 0.5ms'''),
    (ROOT + r"\installer\config.default.json", False,
     '''  "timer": {
    "_comment": "游戏运行期间自动把系统定时器分辨率压到 0.5ms'''),
]:
    txt, b = load(p)
    t2 = rep(txt, anchor, DLSS_JSON + anchor, "config/" + p.split("\\")[-1])
    if t2 is None:
        sys.exit(1)
    save(p, t2, b)

print("全部完成")
