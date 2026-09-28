// 虚拟化开关 / 竞技档着色器缓存 / 驱动层只读核验(A类) / DLSS 覆盖接线(B1) 的只读验证。
// 原则：全程只读 —— 不调 VirtualizationOptimize（它会改 BCD）、不写注册表、不改任何配置文件，
//   DLSS 那节只反射 WithDlss 的返回值，不调 NvAPI_DRS_SetSetting（不写驱动）。
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    static class VirtProbe
    {
        static int Pass = 0, Fail = 0;

        static void L(string s) { Console.WriteLine(s); }

        static void Chk(string what, bool ok, string extra)
        {
            if (ok) Pass++; else Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(extra) ? "" : "   -> " + extra));
        }

        static int Main()
        {
            L("=== 虚拟化开关 + 竞技档着色器缓存 + 新核验项（只读）===");
            L("");

            // ---------- 1. 竞技档驱动设置里是否真多了两条着色器缓存 ----------
            L("=== 1. 竞技档驱动设置（CompSettings）===");
            // v3.6.0：CompSettings 从 static readonly 数组改成了按配置取值的方法
            //   （取值细调要能改「缓存上限 12 档」这类值），所以这里不再反射字段，直接调方法。
            uint[][] comp = null;
            try { comp = NvDrs.CompSettings(); }
            catch (Exception ex) { L("  取值失败: " + ex.Message); }
            Chk("CompSettings() 可调用", comp != null, comp == null ? "失败" : comp.Length + " 条");
            if (comp != null)
            {
                bool hasCache = false, hasSize = false;
                foreach (uint[] s in comp)
                {
                    if (s.Length >= 2 && s[0] == 0x00198FFF && s[1] == 0x1) hasCache = true;
                    if (s.Length >= 2 && s[0] == 0x00AC8497 && s[1] == 0xFFFFFFFF) hasSize = true;
                }
                Chk("含「着色器缓存=开」(0x00198FFF)", hasCache, "");
                Chk("含「缓存上限=无限制」(0x00AC8497)", hasSize, "");
                Chk("条数 = 6（原 4 + 新 2）", comp.Length == 6, comp.Length.ToString());
                // 原 4 条不能被挤掉
                bool keepPwr = false, keepPre = false, keepVsync = false;
                foreach (uint[] s in comp)
                {
                    if (s.Length >= 2 && s[0] == 0x1057EB71) keepPwr = true;
                    if (s.Length >= 2 && s[0] == 0x007BA09E) keepPre = true;
                    if (s.Length >= 2 && s[0] == 0x00A879CF) keepVsync = true;
                }
                Chk("原有 4 条仍在（电源/预渲染/垂直同步）", keepPwr && keepPre && keepVsync, "");
            }
            L("");

            // ---------- 2. 核心拓扑 ----------
            L("=== 2. 核心拓扑（P/E 核）===");
            // 先把硬件探测跑一遍：GpuName 默认是"(探测中)"，不探测的话第 6 节那条
            // 「HAGS × 竞技游戏」提示项的显卡判据永远为假，会误判成"提示项没接线"
            try { Program.DetectSystem(); } catch (Exception ex) { L("  DetectSystem 异常: " + ex.Message); }
            L("  CPU = " + Program.CpuName);
            L("  GPU = " + Program.GpuName);
            try { Program.DetectTopology(); } catch (Exception ex) { L("  DetectTopology 异常: " + ex.Message); }
            L("  HybridCpu = " + Program.HybridCpu + "，P 核 = " + Program.PPhysical + "，E 核 = " + Program.EPhysical);
            L("  逻辑处理器数 = " + Environment.ProcessorCount);
            if (Program.HybridCpu)
            {
                Chk("P 核物理数 > 0", Program.PPhysical > 0, Program.PPhysical.ToString());
                Chk("E 核物理数 > 0", Program.EPhysical > 0, Program.EPhysical.ToString());
                Chk("P+E 物理核 <= 逻辑处理器数", Program.PPhysical + Program.EPhysical <= Environment.ProcessorCount,
                    (Program.PPhysical + Program.EPhysical) + " <= " + Environment.ProcessorCount);
            }
            L("");

            // ---------- 3. 虚拟化状态（只读） ----------
            L("=== 3. 虚拟化状态（只读，不碰 BCD）===");
            string err = null;
            bool hv = false;
            try { hv = Program.HypervisorRunning(); } catch (Exception ex) { err = ex.Message; }
            Chk("HypervisorRunning() 不抛异常", err == null, err ?? "");
            L("  HypervisorPresent = " + hv);

            err = null;
            string hlt = null;
            try { hlt = Program.HypervisorLaunchType(); } catch (Exception ex) { err = ex.Message; }
            Chk("HypervisorLaunchType() 不抛异常", err == null, err ?? "");
            L("  BCD hypervisorlaunchtype = " + (hlt == null ? "(未设置 = 出厂 Auto)" : hlt));
            L("");

            // ---------- 4. CS2 显示模式 ----------
            L("=== 4. CS2 显示模式（读 Steam userdata 的 cs2_video.txt）===");
            err = null;
            string mode = null;
            try { mode = Program.Cs2DisplayMode(); } catch (Exception ex) { err = ex.Message; }
            Chk("Cs2DisplayMode() 不抛异常", err == null, err ?? "");
            L("  读到 = " + (mode == null ? "(未找到 CS2 配置)" : mode));
            if (mode != null)
                Chk("返回值是已知模式之一", mode == "独占全屏" || mode == "无边框窗口" || mode == "窗口化", mode);
            L("");

            // ---------- 5. 配置解析 ----------
            L("=== 5. config.json 的 virtualization 段 ===");
            Config c = null;
            err = null;
            try { c = Config.Load(@"D:\youhua\Fluxion\config.json"); } catch (Exception ex) { err = ex.Message; }
            Chk("Config.Load 不抛异常", c != null && err == null, err ?? "");
            if (c != null)
                Chk("virtualization.enable 解析为 false（默认关）", !c.VirtDisableEnable, c.VirtDisableEnable.ToString());
            L("");

            // ---------- 6. 新核验项 ----------
            L("=== 6. 核验清单新增项 ===");
            if (c != null) Program.Cfg = c;
            List<StatusItem> items = null;
            err = null;
            try { items = Program.GetStatusItems(); } catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            Chk("GetStatusItems 不抛异常", items != null, items == null ? err : items.Count + " 项");
            if (items != null)
            {
                string[] want = { "系统虚拟化", "异类策略", "核心拓扑", "CS2 显示模式" };
                foreach (string k in want)
                {
                    string cur = null, st = null;
                    foreach (StatusItem it in items)
                        if (it.Item != null && it.Item.IndexOf(k) >= 0) { cur = it.Current; st = it.Status; break; }
                    Chk("有「" + k + "」项", cur != null, cur == null ? "缺失" : cur + "  [" + st + "]");
                }
                bool hasHagsTip = false;
                foreach (StatusItem it in items)
                    if (it.Item != null && it.Item.IndexOf("HAGS ×") >= 0) hasHagsTip = true;
                L("  (HAGS 竞技游戏 A/B 提示项 = " + (hasHagsTip ? "已出现" : "未出现") + "，视显卡与游戏名单而定)");

                int empty = 0;
                foreach (StatusItem it in items)
                    if (string.IsNullOrEmpty(it.Item) || string.IsNullOrEmpty(it.Current)
                        || string.IsNullOrEmpty(it.Expected) || string.IsNullOrEmpty(it.Status)) empty++;
                Chk("全部项四列非空", empty == 0, "空列 " + empty + " 项");
            }
            // ---------- 7. 驱动配置库只读解析（NvDrsDb）----------
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
                        // 只扫 primary（nvdrsdb*.bin 通配会把主/备镜像算两遍 → 条数翻倍）
                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                                     + @"\NVIDIA Corporation\Drs";
                        string pf = System.IO.Path.Combine(dir, "nvdrsdb0.bin");
                        if (!System.IO.File.Exists(pf))
                        {
                            string[] cand = System.IO.Directory.GetFiles(dir, "nvdrsdb*.bin");
                            pf = cand.Length > 0 ? cand[0] : null;
                        }
                        if (pf != null)
                        {
                            byte[] dd = System.IO.File.ReadAllBytes(pf);
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
                    Chk("DLSS 覆盖记录数：独立复算(仅 primary) == NvDrsDb（解析器自洽）",
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
            // ---------- 11. 取值细调旋钮表（v3.6.0）----------
            L("=== 11. 取值细调旋钮表 ===");
            List<OptKnob> knobs = null;
            try { knobs = Program.Knobs(); }
            catch (Exception ex) { L("  Knobs() 抛异常: " + ex.GetType().Name + " " + ex.Message); }
            Chk("Knobs() 不抛异常且非空", knobs != null && knobs.Count > 0, knobs == null ? "null" : knobs.Count + " 项");
            if (knobs != null)
            {
                // 每一项都必须：档位表与文案表等长、当前值落在档位表里（或明确记录为"不在表中"）、
                // 有 Hint（只给选项不给代价 = 新的坑）、Apply 是已知值（否则保存后什么都不会发生）
                bool lenOk = true, hintOk = true, applyOk = true, dupKey = false;
                var seen = new Dictionary<string, bool>();
                foreach (OptKnob k in knobs)
                {
                    if (k.Labels == null || k.Values == null || k.Labels.Length != k.Values.Length) lenOk = false;
                    if (string.IsNullOrEmpty(k.Hint)) hintOk = false;
                    if (k.Apply != "drv" && k.Apply != "hags" && k.Apply != "sched" && k.Apply != "proc" && k.Apply != "net") applyOk = false;
                    if (seen.ContainsKey(k.Key)) dupKey = true; else seen[k.Key] = true;
                }
                Chk("每项 Labels.Length == Values.Length", lenOk, "");
                Chk("每项都有 Hint（代价说明）", hintOk, "");
                Chk("每项 Apply 都是已知动作", applyOk, "");
                Chk("Key 无重复", !dupKey, "");

                // 关键：默认值必须等于改造前的硬编码值，否则"升级后行为不变"是假的
                OptKnob kCache = null, kSize = null, kPre = null, kPwr = null, kVs = null, kPreset = null;
                foreach (OptKnob k in knobs)
                {
                    if (k.Key == "nv.shaderCacheOn") kCache = k;
                    if (k.Key == "nv.shaderCacheSize") kSize = k;
                    if (k.Key == "nv.compPrerenderedFrames") kPre = k;
                    if (k.Key == "nv.compPowerMode") kPwr = k;
                    if (k.Key == "nv.compVsync") kVs = k;
                    if (k.Key == "nv.dlssPresetLetter") kPreset = k;
                }
                Chk("有着色器缓存开关/大小两个旋钮", kCache != null && kSize != null, "");
                Chk("缓存大小共 12 档（不是网上常说的 10 档）", kSize != null && kSize.Values.Length == 12,
                    kSize == null ? "缺" : kSize.Values.Length.ToString());
                Chk("缓存默认 = 无限制(-1 -> 0xFFFFFFFF)", kSize != null && kSize.Value == -1 && (uint)kSize.Value == 0xFFFFFFFFu,
                    kSize == null ? "缺" : kSize.Value.ToString());
                Chk("竞技档预渲染帧数默认 = 1", kPre != null && kPre.Value == 1, kPre == null ? "缺" : kPre.Value.ToString());
                Chk("竞技档电源模式默认 = 最高性能(1)", kPwr != null && kPwr.Value == 1, kPwr == null ? "缺" : kPwr.Value.ToString());
                Chk("竞技档垂直同步默认 = 强制关(0x08416747)", kVs != null && kVs.Value == 0x08416747,
                    kVs == null ? "缺" : "0x" + kVs.Value.ToString("X8"));
                // 30 系不能写死 Gen2 预设：这条在上一轮就定过，别在可调化之后被改回去
                Chk("DLSS 预设默认 = 使用推荐值(0x00FFFFFF)", kPreset != null && kPreset.Value == 0x00FFFFFF,
                    kPreset == null ? "缺" : "0x" + kPreset.Value.ToString("X8"));
            }
            L("");

            // ---------- 12. 体检表可调行已挂上旋钮键 ----------
            L("=== 12. 体检表可调行（Key 接线）===");
            if (items != null)
            {
                int withKey = 0; var keys = new List<string>();
                foreach (StatusItem it in items) if (!string.IsNullOrEmpty(it.Key)) { withKey++; keys.Add(it.Item); }
                Chk("有行挂上了可调键", withKey > 0, withKey + " 行");
                foreach (string need in new string[] { "nv.shaderCacheOn", "nv.shaderCacheSize", "dlss.override",
                                                       "hags.enable", "scheduler.win32PrioritySeparation",
                                                       "network.systemResponsiveness", "network.nagleOff" })
                {
                    bool hit = false;
                    foreach (StatusItem it in items) if (it.Key == need) hit = true;
                    Chk("接线存在: " + need, hit, "");
                }
                // 只读核验项必须仍然没有 Key（它们不该被误挂上可调入口）
                bool readonlyClean = true;
                foreach (StatusItem it in items)
                    if (it.Item != null && (it.Item.IndexOf("Resizable BAR") >= 0 || it.Item.IndexOf("DPC") >= 0))
                        if (!string.IsNullOrEmpty(it.Key)) readonlyClean = false;
                Chk("只读核验项(rBAR/DPC)没有被挂上可调键", readonlyClean, "");
            }
            L("");

            // ---------- 13. 状态分级：🟡 不再被当成"需处理" ----------
            // 用户 2026-09-19 的截图：「HAGS × 竞技游戏(A/B 建议)」自己写着"HAGS 已开"，
            // 状态却是红色「需处理」，而且双击没反应。两件事都在这一节钉住。
            L("=== 13. 状态分级与「可实测」行 ===");
            if (items != null)
            {
                int ok2 = 0, warn2 = 0, info = 0, bad2 = 0;
                bool textOk = true, lenOk = true, noRed = true;
                var seen = new List<string>();
                foreach (StatusItem it in items)
                {
                    int k = Program.BadgeKind(it.Status);
                    string t = Program.BadgeText(it.Status);
                    if (k == Program.BadgeOk) ok2++;
                    else if (k == Program.BadgeWarn) warn2++;
                    else if (k == Program.BadgeInfo) info++;
                    else bad2++;
                    if (string.IsNullOrEmpty(t)) textOk = false;
                    if (k == Program.BadgeInfo)
                    {
                        if (t != null && t.Length > 3) lenOk = false;   // 状态列宽 70px，只放得下 3 个汉字
                        if (t == "需处理") noRed = false;
                        if (t != null && seen.IndexOf(t) < 0) seen.Add(t);
                    }
                }
                Chk("每行都能归到一个状态档次（无空文案）", textOk, "已生效 " + ok2 + " / 未生效 " + warn2 + " / 待确认 " + info + " / 需处理 " + bad2);
                Chk("🟡 行的胶囊不超过 3 个汉字", lenOk, string.Join("、", seen.ToArray()));
                Chk("🟡 行不会被渲染成「需处理」（以前全部涂红）", noRed, "");
                bool foundAba = false;
                foreach (StatusItem it in items)
                    if (it.Item != null && it.Item.IndexOf("A/B 建议") >= 0)
                    {
                        foundAba = true;
                        Chk("HAGS A/B 行挂上了 hags.enable（双击可切）", it.Key == "hags.enable",
                            it.Key == null ? "Key 为空 —— 双击会没反应" : it.Key);
                        Chk("HAGS A/B 行归入 🟡（它自己写着「已开」，不该报错）",
                            Program.BadgeKind(it.Status) == Program.BadgeInfo, Program.BadgeText(it.Status));
                    }
                if (!foundAba)
                    L("  （本机没有「HAGS × 竞技游戏(A/B 建议)」这一行 —— 需 RTX 30/20 系 + HAGS 已开 + 有竞技档游戏）");
            }
            L("");

            L("=== 结果: PASS " + Pass + " / FAIL " + Fail + " ===");
            return Fail;
        }
    }
}
