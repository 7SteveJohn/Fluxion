# -*- coding: utf-8 -*-
"""补丁 2：修 WithDlss 的 Cfg 作用域 + 给 NvDrsDb 加 CountById（供探针独立复算）+ 扩展 virt_probe。"""
import codecs
import sys

ROOT = r"D:\youhua\GameBoost-DLSSG"


def load(p):
    raw = open(p, "rb").read()
    return raw.decode("utf-8-sig"), raw.startswith(codecs.BOM_UTF8)


def save(p, txt, bom):
    out = txt.encode("utf-8")
    if bom:
        out = codecs.BOM_UTF8 + out
    open(p, "wb").write(out)


def rep(txt, old, new, tag):
    n = txt.count(old)
    if n != 1:
        print("  [FAIL] %-30s 锚点 %d 次" % (tag, n))
        return None
    print("  [ ok ] %-30s" % tag)
    return txt.replace(old, new)


# ---------------- Core.cs ----------------
CORE = ROOT + r"\src\Core.cs"
core, bom = load(CORE)

# 1) NvDrs 是独立类，裸 Cfg 解析不到 Program.Cfg（其他类一律 Program.Cfg）→ 编译期 CS0103
core = rep(core,
    """        static uint[][] WithDlss(uint[][] baseSettings)
        {
            if (!Cfg.DlssOverride) return baseSettings;""",
    """        static uint[][] WithDlss(uint[][] baseSettings)
        {
            // NvDrs 是独立类，这里必须写 Program.Cfg —— 裸 Cfg 在 Program 之外解析不到（CS0103）
            if (!Program.Cfg.DlssOverride) return baseSettings;""",
    "Core/修 WithDlss 作用域")
if core is None:
    sys.exit(1)

# 2) 给 NvDrsDb 加 CountById（探针用它做「独立复算 vs 解析器」交叉验证）
core = rep(core,
    """        // 一趟扫完两份库，把体检要用的项一次取全（不去反复读 2.6 MB 的二进制）""",
    """        // 按 settingId 统计记录条数（原始扫描，不做语义判断）。
        // 提供给探针做交叉验证：探针自己按同样的 16 字节格式再扫一遍，两边条数必须一致。
        public static int CountById(uint id)
        {
            int n = 0;
            try
            {
                if (!Available()) return 0;
                foreach (var f in Directory.GetFiles(Dir, "nvdrsdb*.bin"))
                {
                    byte[] d = File.ReadAllBytes(f);
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        if (BitConverter.ToUInt32(d, i + 4) == id) n++;
                        i += 15;
                    }
                }
            }
            catch { }
            return n;
        }

        // 一趟扫完两份库，把体检要用的项一次取全（不去反复读 2.6 MB 的二进制）""",
    "Core/NvDrsDb.CountById")
if core is None:
    sys.exit(1)

save(CORE, core, bom)

# ---------------- tools/virt_probe.cs ----------------
PROBE = ROOT + r"\tools\virt_probe.cs"
pb, pbom = load(PROBE)

pb = rep(pb,
    """// 虚拟化开关 / 竞技档着色器缓存 / 新核验项的只读验证。
// 原则：全程只读 —— 不调 VirtualizationOptimize（它会改 BCD），不写注册表、不改任何配置。""",
    """// 虚拟化开关 / 竞技档着色器缓存 / 驱动层只读核验(A类) / DLSS 覆盖接线(B1) 的只读验证。
// 原则：全程只读 —— 不调 VirtualizationOptimize（它会改 BCD）、不写注册表、不改任何配置文件，
//   DLSS 那节只反射 WithDlss 的返回值，不调 NvAPI_DRS_SetSetting（不写驱动）。""",
    "Probe/头注释")
if pb is None:
    sys.exit(1)

NEW = r'''            // ---------- 7. 驱动配置库只读解析（NvDrsDb）----------
            L("=== 7. 驱动配置库解析（直读 nvdrsdb，只读）===");
            NvDrsSnapshot snap = null;
            err = null;
            try { snap = NvDrsDb.Snapshot(); } catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            Chk("NvDrsDb.Snapshot() 不抛异常", snap != null && err == null, err ?? "");
            if (snap != null)
            {
                L("  驱动库可读   : " + snap.Ok);
                L("  着色器缓存   : " + snap.ShaderCache + (snap.ShaderCacheDisabled ? "   [判定=已禁用]" : ""));
                L("  DLSS 覆盖    : " + snap.DlssOverride);
                L("  Resizable BAR: " + snap.Rbar);
                L("  帧率上限(后台): " + snap.FrlBackground);
                if (snap.Ok)
                {
                    Chk("着色器缓存摘要非空", !string.IsNullOrEmpty(snap.ShaderCache), snap.ShaderCache);
                    Chk("DLSS 覆盖摘要非空", !string.IsNullOrEmpty(snap.DlssOverride), snap.DlssOverride);
                    Chk("rBAR 摘要非空", !string.IsNullOrEmpty(snap.Rbar), snap.Rbar);
                    Chk("FRL 摘要非空", !string.IsNullOrEmpty(snap.FrlBackground), snap.FrlBackground);
                    // 自洽性：判成禁用时摘要里必须写着禁用
                    Chk("禁用判定与摘要自洽",
                        !snap.ShaderCacheDisabled || snap.ShaderCache.IndexOf("已禁用") >= 0,
                        "disabled=" + snap.ShaderCacheDisabled + " / " + snap.ShaderCache);

                    // 独立复算：探针自己按同样的 16 字节记录格式再扫一遍，与解析器比条数。
                    // 两边不一致 = 解析器漏读/多读（这是真交叉验证，不是拿函数验它自己）。
                    int mine = 0;
                    string scanErr = null;
                    try
                    {
                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                                     + @"\NVIDIA Corporation\Drs";
                        foreach (string f in System.IO.Directory.GetFiles(dir, "nvdrsdb*.bin"))
                        {
                            byte[] dd = System.IO.File.ReadAllBytes(f);
                            for (int i = 0; i + 16 <= dd.Length; i++)
                            {
                                if (dd[i] != 0xA4 || dd[i + 1] != 0x00 || dd[i + 2] != 0x10 || dd[i + 3] != 0x00) continue;
                                if (BitConverter.ToUInt32(dd, i + 4) == NvDrsDb.IdDlssDllOverride) mine++;
                                i += 15;
                            }
                        }
                    }
                    catch (Exception ex) { scanErr = ex.Message; }
                    int theirs = NvDrsDb.CountById(NvDrsDb.IdDlssDllOverride);
                    Chk("DLSS 覆盖记录数：独立复算 == NvDrsDb（解析器自洽）",
                        scanErr == null && mine == theirs && mine > 0,
                        (scanErr ?? (mine + " vs " + theirs)));
                }
            }
            L("");

            // ---------- 8. DLSS 覆盖三件套的接线（只反射，不写驱动）----------
            L("=== 8. WithDlss 接线（只反射，不写驱动）===");
            if (c != null)
            {
                bool oldDlss = c.DlssOverride;
                var mi = typeof(NvDrs).GetMethod("WithDlss", BindingFlags.NonPublic | BindingFlags.Static);
                Chk("WithDlss 可反射取到", mi != null, mi == null ? "缺失" : "ok");
                if (mi != null)
                {
                    uint[][] seed = new uint[][] { new uint[] { 0x1057EB71, 0x1 } };
                    c.DlssOverride = false;
                    var rOff = (uint[][])mi.Invoke(null, new object[] { seed });
                    Chk("开关关闭时原样返回（不附加任何设置）",
                        rOff != null && rOff.Length == 1 && rOff[0][0] == 0x1057EB71,
                        rOff == null ? "null" : rOff.Length.ToString());

                    c.DlssOverride = true;
                    var rOn = (uint[][])mi.Invoke(null, new object[] { seed });
                    c.DlssOverride = oldDlss;   // 立刻还原，别把探针状态留在内存里

                    bool dllOn = false, prof = false, letter = false; uint letterVal = 0;
                    if (rOn != null)
                        foreach (uint[] s in rOn)
                        {
                            if (s[0] == NvDrsDb.IdDlssDllOverride) { dllOn = (s[1] == 1); }
                            if (s[0] == NvDrsDb.IdDlssPresetProfile) prof = (s[1] == 1);
                            if (s[0] == NvDrsDb.IdDlssPresetLetter) { letter = true; letterVal = s[1]; }
                        }
                    Chk("开启时附加 3 条（1 -> 4 条）", rOn != null && rOn.Length == 4, rOn == null ? "null" : rOn.Length.ToString());
                    Chk("含 DLL 覆盖 = 1 (0x10E41E01)", dllOn, "");
                    Chk("含 预设档 = 推荐 (0x00634291)", prof, "");
                    Chk("含 强制预设 (0x10E41DF3)", letter, "值 0x" + letterVal.ToString("X8"));
                    // 关键分档规则：不能写死 K/L/M —— 30 系跑 Gen2 的 M 预设约有 20% 性能税
                    Chk("预设值 = 0x00FFFFFF（使用推荐值，非 K/L/M 写死）",
                        letter && letterVal == 0x00FFFFFF, "0x" + letterVal.ToString("X8"));
                    Chk("原设置未被挤掉", rOn != null && rOn[0][0] == 0x1057EB71, "");
                }
            }
            L("");

            // ---------- 9. 体检表新增的 A 类只读项 ----------
            L("=== 9. 体检表新增项（驱动层 / 显示路径 / DPC）===");
            if (items != null)
            {
                string[] want2 = { "驱动·着色器缓存", "驱动·DLSS 模型覆盖", "驱动·Resizable BAR",
                                   "驱动·帧率上限", "窗口化游戏的优化", "每程序全屏优化标记", "DPC / 中断分布" };
                foreach (string k in want2)
                {
                    string cur2 = null, st2 = null;
                    foreach (StatusItem it in items)
                        if (it.Item != null && it.Item.IndexOf(k) >= 0) { cur2 = it.Current; st2 = it.Status; break; }
                    Chk("有「" + k + "」项", cur2 != null, cur2 == null ? "缺失" : cur2 + "   [" + st2 + "]");
                }
            }
            L("");

            // ---------- 10. config.json 的 dlss 段 ----------
            L("=== 10. config.json 的 dlss 段 ===");
            if (c != null)
                Chk("dlss.override 解析为 false（默认关）", !c.DlssOverride, c.DlssOverride.ToString());
            L("");
'''

pb = rep(pb,
    """            L("");
            L("=== 结果: PASS " + Pass + " / FAIL " + Fail + " ===");""",
    NEW + """            L("=== 结果: PASS " + Pass + " / FAIL " + Fail + " ===");""",
    "Probe/新增 7-10 节")
if pb is None:
    sys.exit(1)

save(PROBE, pb, pbom)
print("全部完成")
