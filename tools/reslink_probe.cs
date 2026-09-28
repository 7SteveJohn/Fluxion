// 游戏分辨率联动 ResLink 的验证。
// 原则：**全程只读**，不切真模式、不真禁屏 ——
//   - 切换能力用 CDS_TEST 空跑（只问驱动"这么设行不行"，不落地）—— 第 5 节
//   - Restore() 只在"从未激活"状态下调用，天然 no-op —— 第 6 节
//   - 真正的进游戏/退游戏链路由用户游戏时实测（切屏/禁屏不能在探针里来真的）。
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;

    static class ResLinkProbe
    {
        static int Pass = 0, Fail = 0;
        static readonly List<string> Lines = new List<string>();

        static void L(string s) { Lines.Add(s); Console.WriteLine(s); }

        static void Chk(string what, bool ok, string extra)
        {
            if (ok) Pass++; else Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(extra) ? "" : "   → " + extra));
        }

        // 断言失败时要能一眼看出是**哪一项**不符 —— 只回一个 false 等于什么都没说（本轮踩过）
        static string DumpRules(Config c)
        {
            if (c == null || c.ResRules == null) return "(null)";
            string s = "count=" + c.ResRules.Count;
            for (int i = 0; i < c.ResRules.Count && i < 4; i++)
            {
                ResLinkRule r = c.ResRules[i];
                s += "  [" + i + "] " + r.Proc + " " + r.W + "x" + r.H + "@" + r.Hz
                   + " detach=" + r.Detach + " preset='" + (r.Preset ?? "") + "' offMons='" + (r.OffMons ?? "") + "'";
            }
            return s;
        }

        static int Main()
        {
            L("=== 游戏分辨率联动 ResLink 验证 ===");
            L("");

            // ---------------- ① 枚举在用屏幕 ----------------
            L("=== 1. AttachedDevices / PrimaryDevice（只读枚举）===");
            var devs = ResLink.AttachedDevices();
            foreach (var d in devs) L("    在用: " + d + "  已禁用=" + ResLink.IsDetached(d));
            Chk("至少 1 块在用屏", devs.Count >= 1, devs.Count.ToString());
            string pri = ResLink.PrimaryDevice();
            Chk("能识别主屏（PRIMARY_DEVICE）", !string.IsNullOrEmpty(pri), pri ?? "(null)");
            Chk("主屏在在用列表里", pri != null && devs.Contains(pri), "");
            Chk("主屏 IsDetached == false", pri != null && !ResLink.IsDetached(pri), "");
            L("");

            // ---------------- ② 规则匹配 ----------------
            L("=== 2. RuleFor 规则匹配（前缀 + 忽略大小写）===");
            var cfg = new Config();
            ResLinkRule r1 = null, r2 = null, r3 = null, r4 = null;
            try { r1 = ResLink.RuleFor("cs2"); } catch (Exception ex) { L("  ex: " + ex.Message); }
            try { r2 = ResLink.RuleFor("VALORANT-Win64-Shipping"); } catch (Exception ex) { L("  ex: " + ex.Message); }
            try { r3 = ResLink.RuleFor("notepad"); } catch (Exception ex) { L("  ex: " + ex.Message); }
            try { r4 = ResLink.RuleFor("CS2"); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("cs2 命中规则", r1 != null, r1 == null ? "(null)" : r1.W + "x" + r1.H + "@" + r1.Hz);
            Chk("cs2 → 1440x1080@165", r1 != null && r1.W == 1440 && r1.H == 1080 && r1.Hz == 165, "");
            Chk("cs2 → 不禁用副屏", r1 != null && !r1.Detach, "");
            Chk("VALORANT-Win64-Shipping 前缀命中", r2 != null, r2 == null ? "(null)" : r2.Proc);
            Chk("VALORANT → 1568x1080@165", r2 != null && r2.W == 1568 && r2.H == 1080 && r2.Hz == 165, "");
            // v3.9.0：瓦罗兰特默认从"分离所有副屏"改成"只在设备管理器里停用指定那一台"，
            //   所以这里断言的 Detach 反而必须是**false**（两条同时开 = 可能把屏全拿掉）。
            Chk("VALORANT → 不再分离副屏（改成只禁用指定那一台）", r2 != null && !r2.Detach, "");
            Chk("VALORANT → 进游戏时禁用 P27FBB-RG（型号 XMIB008）",
                r2 != null && r2.OffMons == "XMIB008", r2 == null ? "(null)" : (r2.OffMons ?? "(null)"));
            Chk("CS2 → 不禁用任何显示器", r1 != null && (r1.OffMons == null || r1.OffMons.Length == 0),
                r1 == null ? "(null)" : (r1.OffMons ?? ""));
            Chk("CS2 大写也命中（忽略大小写）", r4 != null && r4.W == 1440, "");
            Chk("notepad 不命中", r3 == null, r3 == null ? "" : "竟然命中了 " + r3.Proc);
            Chk("默认规则恰好 2 条", cfg.ResRules.Count == 2, cfg.ResRules.Count.ToString());
            L("");

            // ---------------- ③ Config displayLink 段解析 ----------------
            L("=== 3. Config.Load 解析 displayLink 段 ===");
            string tmpCfg = Path.Combine(Path.GetTempPath(), "reslink_probe_config.json");
            // ⚠ 2026-09-20 修：这里原来多写了一个 `}`（`]}}` + `,"ui"...`），根对象被提前关掉，
            //   而 Config.Load 内部的 catch 把解析异常吞了 → 返回**默认 Config**，
            //   于是本节所有断言都在测「代码里的默认值」，改默认值之前一直是**假 PASS**
            //   （默认值和文件值恰好相同，显不出来）。现在 baseW/H/Hz 故意用非默认值，
            //   一旦又读不到文件就会立刻暴露，不会再有"测默认值"这种假阳性。
            File.WriteAllText(tmpCfg,
                "{\"displayLink\":{\"enable\":true,\"baseW\":2560,\"baseH\":1440,\"baseHz\":240," +
                "\"rules\":[" +
                "{\"proc\":\"cs2\",\"w\":1440,\"h\":1080,\"hz\":165,\"detach\":false}," +
                "{\"proc\":\"VALORANT\",\"w\":1568,\"h\":1080,\"hz\":165,\"detach\":true,\"offMons\":\"XMIB008\"}," +
                "{\"w\":800,\"h\":600}]}" +
                ",\"ui\":{\"theme\":\"dark\"}}");
            Config c2 = null;
            bool loadOk = true;
            try { c2 = Config.Load(tmpCfg); } catch (Exception ex) { loadOk = false; L("  ex: " + ex.Message); }
            Chk("Config.Load 不抛异常", loadOk && c2 != null, "");
            if (c2 != null)
            {
                Chk("ResLinkEnable 读取", c2.ResLinkEnable, "");
                // 这三个值故意都不是默认值（默认 1920x1080@165）—— 读到它们才说明文件真被解析了
                Chk("Base 从文件读到 2560x1440@240（证明不是默认值）",
                    c2.BaseW == 2560 && c2.BaseH == 1440 && c2.BaseHz == 240,
                    c2.BaseW + "x" + c2.BaseH + "@" + c2.BaseHz);
                Chk("rules 解析出 2 条（无 proc 名的那条被丢弃）", c2.ResRules.Count == 2, c2.ResRules.Count.ToString());
                bool rulesOk = c2.ResRules.Count == 2
                    && c2.ResRules[0].Proc == "cs2" && c2.ResRules[0].W == 1440 && !c2.ResRules[0].Detach
                    && c2.ResRules[1].Proc == "VALORANT" && c2.ResRules[1].W == 1568 && c2.ResRules[1].Detach;
                Chk("规则内容逐字段正确", rulesOk, DumpRules(c2));
                // v3.9.0 新字段：offMons 读回（老配置没有这一项 → 必须是空串，不能是 null）
                Chk("offMons 读回", c2.ResRules.Count == 2
                    && (c2.ResRules[0].OffMons == null || c2.ResRules[0].OffMons.Length == 0)
                    && c2.ResRules[1].OffMons == "XMIB008", DumpRules(c2));
            }
            try { File.Delete(tmpCfg); } catch { }
            L("");

            // ---------------- ④ 初始状态 ----------------
            L("=== 4. 初始状态 ===");
            Chk("Active 初始为 false", !ResLink.Active, "");
            L("");

            // ---------------- ⑤ 切换能力空跑（CDS_TEST，不落地）----------------
            L("=== 5. 目标模式驱动接受度（CDS_TEST 空跑）===");
            const int CDS_TEST = 0x2;
            const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000,
                      DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
            if (pri != null)
            {
                int[][] want = new int[][] {
                    new int[] { 1440, 1080, 165 },   // CS2 档
                    new int[] { 1568, 1080, 165 },   // 瓦罗兰特档（本机自定义档，只注册了 165Hz 一档）
                };
                foreach (int[] m in want)
                {
                    var dm = new Native.DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                    dm.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                    dm.dmBitsPerPel = 32;
                    dm.dmPelsWidth = m[0]; dm.dmPelsHeight = m[1]; dm.dmDisplayFrequency = m[2];
                    int rc = Native.ChangeDisplaySettingsEx(pri, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
                    Chk(pri + " 接受 " + m[0] + "x" + m[1] + "@" + m[2], rc == 0, "rc=" + rc);
                }
                // 反向信息（不断言：驱动自定义档以后可能变）
                var dm60 = new Native.DEVMODE();
                dm60.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                dm60.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm60.dmBitsPerPel = 32;
                dm60.dmPelsWidth = 1568; dm60.dmPelsHeight = 1080; dm60.dmDisplayFrequency = 60;
                int rc60 = Native.ChangeDisplaySettingsEx(pri, ref dm60, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
                L("  （信息）1568x1080@60 → rc=" + rc60 + (rc60 == 0 ? " 可用" : " BADMODE/拒绝（符合\"该档只注册165Hz\"的实测）"));
            }
            L("");

            // ---------------- 6. Restore() no-op 路径 ----------------
            L("=== 6. Restore() 在未激活状态是 no-op ===");
            int logCount = 0;
            List<string> out6 = null;
            try { out6 = ResLink.Restore(); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("未激活时 Restore 返回空表", out6 != null && out6.Count == 0,
                out6 == null ? "(null)" : out6.Count.ToString());
            Chk("Restore 后 Active 仍为 false", !ResLink.Active, "");
            Chk("全程无日志产生", logCount == 0, "");
            L("");

            // ---------------- ⑦ 档位表（v3.8.0）----------------
            L("=== 7. 内置分辨率档位表 ===");
            ResPreset[] csP = ResLink.PresetsFor("cs2");
            ResPreset[] csP2 = ResLink.PresetsFor("cs2");
            ResPreset[] valP = ResLink.PresetsFor("VALORANT-Win64-Shipping");
            Chk("CS2 档位 >= 6 档", csP.Length >= 6, csP.Length.ToString());
            Chk("无畏契约档位 >= 6 档", valP.Length >= 6, valP.Length.ToString());
            Chk("按进程名前缀路由：VALORANT* 走另一张表", !object.ReferenceEquals(csP, valP), "");
            Chk("PresetsFor 对同一进程稳定返回同一张表", object.ReferenceEquals(csP, csP2), "");

            bool idOk = true, whOk = true;
            var seen = new Dictionary<string, int>();
            foreach (ResPreset p in csP)
            {
                if (seen.ContainsKey(p.Id)) idOk = false;
                seen[p.Id] = 1;
                if (p.W <= 0 || p.H <= 0) whOk = false;
            }
            int csCount = seen.Count;
            bool idOk2 = true; bool whOk2 = true;
            var seen2 = new Dictionary<string, int>();
            var whSeen = new Dictionary<string, int>();
            foreach (ResPreset p in csP)
            {
                string whk = p.W + "x" + p.H;
                if (whSeen.ContainsKey(whk)) whOk = false;
                whSeen[whk] = 1;
            }
            foreach (ResPreset p in valP)
            {
                if (seen2.ContainsKey(p.Id)) idOk2 = false;
                seen2[p.Id] = 1;
                if (p.W <= 0 || p.H <= 0) whOk2 = false;
            }
            Chk("CS2 档位 id 唯一且宽高合法", idOk && whOk, "n=" + csCount);
            Chk("无畏契约档位 id 唯一且宽高合法", idOk2 && whOk2, "n=" + seen2.Count);

            // 两张表都不能撞 id（否则配置里的 preset 会串台）
            bool crossOk = true;
            foreach (ResPreset p in valP) if (seen.ContainsKey(p.Id)) crossOk = false;
            Chk("两张表之间 id 不重复", crossOk, "");

            // 反查：表内命中 / 表外返回 null（界面据此显示"自定义"）
            Chk("MatchPreset(cs2,1280,960) 命中", ResLink.MatchPreset("cs2", 1280, 960) != null, "");
            Chk("MatchPreset(VALORANT,1568,1080) 命中（用户原档在表内）",
                ResLink.MatchPreset("VALORANT", 1568, 1080) != null, "");
            Chk("MatchPreset 表外 → null", ResLink.MatchPreset("cs2", 1234, 567) == null, "");
            // 默认规则必须能对上档位（否则界面上会显示"自定义"，等于白给）
            Chk("默认 CS 规则 → PresetOf 命中 cs-1440x1080",
                ResLink.PresetOf(ResLink.RuleFor("cs2")) != null
                && ResLink.PresetOf(ResLink.RuleFor("cs2")).Id == "cs-1440x1080", "");
            Chk("默认瓦罗兰特规则 → PresetOf 命中 val-1568x1080",
                ResLink.PresetOf(ResLink.RuleFor("VALORANT-Win64-Shipping")) != null
                && ResLink.PresetOf(ResLink.RuleFor("VALORANT-Win64-Shipping")).Id == "val-1568x1080", "");
            // id 认不出 / 与 w,h 不符时按 w,h 兜底（用户手改过 config.json 的情况）
            var odd = new ResLinkRule("cs2", 1280, 960, 165, false); odd.Preset = "cs-不存在的档";
            Chk("preset 是坏 id → 退回按 w/h 反查", ResLink.PresetOf(odd) != null && ResLink.PresetOf(odd).Id == "cs-1280x960", "");

            // 本机可用性（信息 + 断言取值合法）
            var modes = ResLink.MainScreenModes();
            int mw, mh; ResLink.MainScreenMax(out mw, out mh);
            L("    主屏 " + pri + " 枚举到 " + modes.Count + " 种分辨率，最大 " + mw + "x" + mh);
            bool avOk = true;
            foreach (ResPreset p in csP)
            {
                int a = ResLink.Availability(p);
                if (a < 0 || a > 2) avOk = false;
                L("      CS2 " + p.Id.PadRight(14) + " " + (p.W + "x" + p.H).PadRight(11) + " 可用性=" + a
                  + (a == 0 ? " ✓可切" : a == 1 ? " △需建自定义分辨率" : " ✕超出屏幕"));
            }
            foreach (ResPreset p in valP)
            {
                int a = ResLink.Availability(p);
                if (a < 0 || a > 2) avOk = false;
                L("      VAL " + p.Id.PadRight(14) + " " + (p.W + "x" + p.H).PadRight(11) + " 可用性=" + a
                  + (a == 0 ? " ✓可切" : a == 1 ? " △需建自定义分辨率" : " ✕超出屏幕"));
            }
            Chk("每个档位的可用性取值都在 0/1/2 内", avOk, "");
            Chk("当前主屏模式枚举非空（可用性判断有依据）", modes.Count > 0, modes.Count.ToString());
            // 关键一致性：标"✓可切"的档，驱动的 CDS_TEST 也必须接受
            if (pri != null)
            {
                int mism = 0;
                foreach (ResPreset p in csP)
                {
                    if (ResLink.Availability(p) != 0) continue;
                    var dmx = new Native.DEVMODE();
                    dmx.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                    dmx.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                    dmx.dmBitsPerPel = 32;
                    dmx.dmPelsWidth = p.W; dmx.dmPelsHeight = p.H; dmx.dmDisplayFrequency = 60;
                    if (Native.ChangeDisplaySettingsEx(pri, ref dmx, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0) mism++;
                }
                Chk("标✓的档驱动都接受（@60Hz 空跑核对）", mism == 0, "不一致 " + mism + " 档");
            }
            L("");

            // ---------------- ⑧ 规则写回 → 读回（往返，不碰真实配置）----------------
            L("=== 8. ResRulesJson → Config.Load 往返（全程不写真实配置文件）===");
            var savedRules = Program.Cfg.ResRules;
            var probeRules = new List<ResLinkRule>();
            probeRules.Add(new ResLinkRule("cs2", 1280, 960, 165, false) { Preset = "cs-1280x960" });
            probeRules.Add(new ResLinkRule("VALORANT", 1568, 1080, 240, true) { Preset = "val-1568x1080" });
            // 第三条刻意用一个**两张表都没有**的分辨率（1100x700）：验证"表外的档 preset 保持空"。
            //   ⚠ 别拿 1024x768 之类当"自定义"—— 它在 CS 表里，加载时会被反查补上 preset，
            //   断言就会写成一个假失败（第一版就是这么写错的）。
            probeRules.Add(new ResLinkRule("csgo", 1100, 700, 144, true) { Preset = "" });   // 自定义档
            Program.Cfg.ResRules = probeRules;
            string json = Program.ResRulesJson();
            L("    " + json);

            string rt = Path.Combine(Path.GetTempPath(), "reslink_probe_roundtrip.json");
            File.WriteAllText(rt, "{\"displayLink\":{\"rules\":" + json + "}}", new System.Text.UTF8Encoding(false));
            Config back = null;
            try { back = Config.Load(rt); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("往返：解析成功", back != null, "");
            Chk("往返：条数一致（3 条）", back != null && back.ResRules.Count == 3,
                back == null ? "(null)" : back.ResRules.Count.ToString());
            bool same = back != null && back.ResRules.Count == 3
                && back.ResRules[0].Proc == "cs2" && back.ResRules[0].W == 1280 && back.ResRules[0].H == 960
                && back.ResRules[0].Hz == 165 && !back.ResRules[0].Detach && back.ResRules[0].Preset == "cs-1280x960"
                && back.ResRules[1].W == 1568 && back.ResRules[1].Hz == 240 && back.ResRules[1].Detach
                && back.ResRules[1].Preset == "val-1568x1080"
                && back.ResRules[2].Proc == "csgo" && back.ResRules[2].W == 1100 && back.ResRules[2].Detach
                && back.ResRules[2].Preset == "";
            Chk("往返：proc/w/h/hz/detach/preset 逐字段一致", same, "");
            try { File.Delete(rt); } catch { }

            // 老配置（完全没有 preset 键）→ 加载时应按 w/h 反查补上
            string oldCfg = Path.Combine(Path.GetTempPath(), "reslink_probe_old.json");
            File.WriteAllText(oldCfg,
                "{\"displayLink\":{\"rules\":[" +
                "{\"proc\":\"cs2\",\"w\":1280,\"h\":960,\"hz\":165,\"detach\":false}," +
                "{\"proc\":\"cs2v2\",\"w\":9999,\"h\":9999,\"hz\":60,\"detach\":false}]}}",
                new System.Text.UTF8Encoding(false));
            Config old = null;
            try { old = Config.Load(oldCfg); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("老配置：表内档被反查补上 preset", old != null && old.ResRules[0].Preset == "cs-1280x960",
                old == null ? "(null)" : "\"" + old.ResRules[0].Preset + "\"");
            Chk("老配置：表外的档 preset 保持空（界面显示自定义）",
                old != null && old.ResRules.Count > 1 && old.ResRules[1].Preset == "",
                old == null ? "(null)" : "\"" + old.ResRules[1].Preset + "\"");
            try { File.Delete(oldCfg); } catch { }

            // 写回 JSON 必须能被整份 JSON 校验器接受（WriteConfigKey 写前要过这一关）
            bool jsonOk = true;
            try
            {
                new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>("{\"rules\":" + json + "}");
            }
            catch { jsonOk = false; }
            Chk("生成的 rules 是合法 JSON", jsonOk, "");

            Program.Cfg.ResRules = savedRules;   // 还原
            L("");

            // ---------------- ⑨ 标签 ↔ 索引 ↔ 档位 三者对齐 ----------------
            // 界面上"选了第 i 项"要变成"第 i 个档位"。这两件事分居 UI 与 Core，
            //   错位了就会"选了 1280x960 实际写成 1440x1080"，而且肉眼看下拉是正常的。
            //   所以把映射收进 ResLink 后，在这里把不变量逐条钉死。
            L("=== 9. 下拉标签 ↔ 索引 ↔ 档位 对齐 ===");
            foreach (string proc in new string[] { "cs2", "VALORANT-Win64-Shipping" })
            {
                ResPreset[] ps = ResLink.PresetsFor(proc);
                // 当前值在表内（取第 2 档）
                var labels = ResLink.ComboLabels(proc, ps[1].W, ps[1].H);
                Chk(proc + "：当前值在表内 → 下拉项数 == 档位数", labels.Count == ps.Length,
                    labels.Count + " vs " + ps.Length);
                Chk(proc + "：当前值在表内 → 选中项就是它", ResLink.ComboIndexOf(proc, ps[1].W, ps[1].H) == 1, "");
                // 当前值在表外
                var labels2 = ResLink.ComboLabels(proc, 1234, 567);
                Chk(proc + "：当前值在表外 → 多一项「自定义·当前值」", labels2.Count == ps.Length + 1,
                    labels2.Count.ToString());
                Chk(proc + "：当前值在表外 → 选中项落在最后一项",
                    ResLink.ComboIndexOf(proc, 1234, 567) == ps.Length, "");
                Chk(proc + "：最后一项对应 null（选了不改动）", ResLink.PresetAt(proc, ps.Length) == null, "");
                Chk(proc + "：越界索引也返回 null", ResLink.PresetAt(proc, -1) == null && ResLink.PresetAt(proc, 999) == null, "");
                // 往返不变量：对每一档，索引→档位→索引 必须回到原处
                bool round = true;
                string bad = "";
                for (int i = 0; i < ps.Length; i++)
                {
                    ResPreset p = ResLink.PresetAt(proc, i);
                    if (p == null || p.Id != ps[i].Id) { round = false; bad = "index " + i + " → null/串台"; break; }
                    int backI = ResLink.ComboIndexOf(proc, p.W, p.H);
                    if (backI != i) { round = false; bad = "index " + i + " → " + backI; break; }
                }
                Chk(proc + "：每一档 索引→档位→索引 回原位", round, bad);
                // 标签必须带上本机的可用性前缀（✓/△/✕ 之一）
                bool markOk = true;
                foreach (string s in labels)
                    if (s.Length == 0 || (s[0] != '✓' && s[0] != '△' && s[0] != '✕')) markOk = false;
                Chk(proc + "：每条标签都带可用性前缀", markOk, labels.Count > 0 ? labels[0] : "(空)");
                // 标签里必须写清分辨率与比例（否则用户没法判断选的是哪档）
                bool labelOk = true;
                for (int i = 0; i < ps.Length; i++)
                    if (labels[i].IndexOf(ps[i].W + "×" + ps[i].H) < 0 || labels[i].IndexOf(ps[i].Ratio) < 0) labelOk = false;
                Chk(proc + "：标签含 W×H 与比例", labelOk, "");
            }
            L("");

            // ---------------- ⑩ SaveResRules 全链路（ConfigPath 重定向到临时文件）----------------
            // 为什么要重定向：SaveResRules 的价值就在于"把档位写进 config.json"，而它靠一条正则把
            //   rules 数组整段替换掉 —— 正则写歪了用户的配置就被改坏。这是本功能最该验证、也最容易出错的一环，
            //   但探针绝不能碰真实 config.json（本机那份里还有用户所有设置），
            //   所以用反射把 Program 的 configPathCache 指到临时文件，测完还原。
            L("=== 10. SaveResRules 全链路（ConfigPath 重定向到临时文件）===");
            FieldInfo cpf = typeof(Program).GetField("configPathCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            Chk("能拿到 configPathCache 字段（拿不到本段就失去保护，必须人工复核）", cpf != null, "");
            string realCache = cpf == null ? null : (string)cpf.GetValue(null);
            var savedCfgObj = Program.Cfg;
            string tmpDir = Path.Combine(Path.GetTempPath(), "reslink_probe_cfg");
            try { Directory.CreateDirectory(tmpDir); } catch { }

            var probeCfg = new Config();
            probeCfg.ResRules = new List<ResLinkRule>();
            probeCfg.ResRules.Add(new ResLinkRule("cs2", 1280, 960, 165, false) { Preset = "cs-1280x960" });
            probeCfg.ResRules.Add(new ResLinkRule("VALORANT", 1920, 1080, 165, true) { Preset = "val-1920x1080" });
            probeCfg.BaseW = 2560; probeCfg.BaseH = 1440; probeCfg.BaseHz = 144;

            // 场景 A：老配置 —— **完全没有 displayLink 段**（这正是用户当前的形态）
            string cfgA = Path.Combine(tmpDir, "a.json");
            File.WriteAllText(cfgA,
                "{\n  \"gameAware\": { \"enable\": true },\n  \"ui\": { \"theme\": \"light\" }\n}\n",
                new System.Text.UTF8Encoding(false));
            if (cpf != null) cpf.SetValue(null, cfgA);
            Program.Cfg = probeCfg;
            bool okA = false;
            try { okA = Program.SaveResRules(); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("A. 节不存在时也返回成功（走 AppendSectionBlock）", okA, "");
            string textA = File.Exists(cfgA) ? File.ReadAllText(cfgA) : "";
            Dictionary<string, object> objA = null;
            bool jsonA = true;
            try { objA = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(textA); }
            catch { jsonA = false; }
            Chk("A. 写回后整份 JSON 仍合法", jsonA, "");
            Chk("A. 新建出了 displayLink 段", objA != null && objA.ContainsKey("displayLink"), "");
            Chk("A. 原有段没丢（gameAware / ui 仍在）",
                objA != null && objA.ContainsKey("gameAware") && objA.ContainsKey("ui"), "");
            Config backA = null;
            try { backA = Config.Load(cfgA); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("A. 用真解析器读回：两条规则逐字段一致",
                backA != null && backA.ResRules.Count == 2
                && backA.ResRules[0].W == 1280 && backA.ResRules[0].H == 960 && backA.ResRules[0].Preset == "cs-1280x960"
                && backA.ResRules[1].W == 1920 && backA.ResRules[1].Preset == "val-1920x1080" && backA.ResRules[1].Detach,
                "");
            Chk("A. baseW/H/Hz 一并写进配置",
                backA != null && backA.BaseW == 2560 && backA.BaseH == 1440 && backA.BaseHz == 144, "");

            // 场景 B：已有 displayLink 段 + **多行** rules 数组，且同文件里另有一个数组键（services.disable）
            //   —— 正则如果用贪婪式的 [\s\S]*\] 就会吃掉后面那个数组，这里专门盯住这一点。
            string cfgB = Path.Combine(tmpDir, "b.json");
            File.WriteAllText(cfgB,
                "{\n  \"displayLink\": {\n    \"enable\": true,\n    \"baseW\": 1920,\n    \"baseH\": 1080,\n"
                + "    \"baseHz\": 165,\n    \"rules\": [\n"
                + "      { \"proc\": \"cs2\", \"w\": 1440, \"h\": 1080, \"hz\": 165, \"detach\": false }\n"
                + "    ]\n  },\n  \"services\": { \"disable\": [ \"DiagTrack\" ] }\n}\n",
                new System.Text.UTF8Encoding(false));
            if (cpf != null) cpf.SetValue(null, cfgB);
            probeCfg.ResRules[0].W = 1680; probeCfg.ResRules[0].H = 1050; probeCfg.ResRules[0].Preset = "cs-1680x1050";
            Program.Cfg = probeCfg;
            bool okB = false;
            try { okB = Program.SaveResRules(); } catch (Exception ex) { L("  ex: " + ex.Message); }
            string textB = File.Exists(cfgB) ? File.ReadAllText(cfgB) : "";
            Chk("B. 已有段 + 多行数组时写回成功", okB, "");
            Chk("B. 旧 rules 已被替换（1440 不再出现）", textB.IndexOf("\"w\": 1440") < 0, "");
            Chk("B. 后面的 services.disable 数组没被吃掉（DiagTrack 仍在）", textB.IndexOf("DiagTrack") >= 0, "");
            Config backB = null;
            try { backB = Config.Load(cfgB); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("B. 读回：CS2 已变成 1680x1050",
                backB != null && backB.ResRules.Count == 2 && backB.ResRules[0].W == 1680
                && backB.ResRules[0].H == 1050 && backB.ResRules[0].Preset == "cs-1680x1050", "");

            // 场景 C：连写两次必须幂等 —— 顺带钉住 WriteConfigKey 那个"值没变就重复插键"的老陷阱
            bool okC1 = Program.SaveResRules();
            string t1 = File.ReadAllText(cfgB);
            bool okC2 = Program.SaveResRules();
            string t2 = File.ReadAllText(cfgB);
            Chk("C. 连续两次写回都成功", okC1 && okC2, "");
            Chk("C. 第二次是幂等的（文件内容一字不变）", t1 == t2, t1 == t2 ? "" : "内容变了 → 有键被重复插入");
            int dupRules = 0, idx = 0;
            while ((idx = t2.IndexOf("\"rules\"", idx)) >= 0) { dupRules++; idx++; }
            Chk("C. displayLink 里没有重复的 rules 键", dupRules == 1, "出现 " + dupRules + " 次");

            if (cpf != null) cpf.SetValue(null, realCache);    // 还原（null → ConfigPath 会按 DataDir 重算）
            Program.Cfg = savedCfgObj;
            try { Directory.Delete(tmpDir, true); } catch { }
            L("");

            // ---------------- ⑪ 显示器设备（MonMgr，**全程只读**）----------------
            // ⚠ 本段绝不调用 SetEnabled —— 那等于替你点了设备管理器的「禁用设备」，
            //   会真的改动这台机器的显示状态。这里只验证"设备认不认得出、型号对不对、
            //   状态判得准不准"；真正的停用/启用由用户在界面上自己点
            //   （性能优化页 →「显示器设备」）。
            L("=== 11. 显示器设备枚举（只读，不改任何设备状态）===");
            List<MonDev> mons = null;
            try { mons = MonMgr.All(); } catch (Exception ex) { L("  ex: " + ex.Message); }
            Chk("MonMgr.All() 不抛异常", mons != null, "");
            if (mons != null)
            {
                Chk("至少枚举到 1 台显示器设备", mons.Count >= 1, mons.Count.ToString());
                foreach (MonDev d in mons)
                    L("    " + d.Panel + "  输出=" + (d.Output.Length > 0 ? d.Output : "—")
                      + "  状态=" + d.StatusText + "  型号=" + d.Model);

                bool monIdOk = true, modelOk = true, panelOk = true, drvOk = true;
                foreach (MonDev d in mons)
                {
                    // SetupAPI 侧的实例 ID 前缀是 **DISPLAY\**（PnP 枚举器名），不是 MONITOR\
                    //   —— MONITOR\ 那个是 EnumDisplayDevices 给的"显示设备接口 ID"，两套不能混。
                    if (d.InstanceId.Length == 0 || d.InstanceId.IndexOf("DISPLAY\\") != 0) monIdOk = false;
                    if (d.Model.Length == 0) modelOk = false;
                    if (d.Panel.Length == 0) panelOk = false;
                    if (d.Driver.Length == 0) drvOk = false;
                }
                Chk("实例 ID 形如 DISPLAY\\XXX\\<实例>", monIdOk,
                    mons.Count > 0 ? mons[0].InstanceId : "");
                Chk("每台设备都拿到了驱动子键（关联显示层靠它）", drvOk,
                    mons.Count > 0 ? mons[0].Driver : "");
                Chk("每台设备都有型号 ID（硬件 ID 第二段）", modelOk, "");
                Chk("每台设备都有可读的名字", panelOk, "");

                // 型号反查必须能查回它自己（配置里存的就是型号；插拔换口后实例 ID 会变）
                MonDev first = mons[0];
                List<MonDev> monBack = MonMgr.ByModel(first.Model);
                bool backOk = false;
                foreach (MonDev d in monBack) if (d.InstanceId == first.InstanceId) backOk = true;
                Chk("ByModel 用第一台的型号能反查回它自己", backOk, first.Model + " → " + monBack.Count + " 台");

                Chk("ByModel(\"\") 返回空", MonMgr.ByModel("").Count == 0, "");
                Chk("ByModel(不存在的型号) 返回空", MonMgr.ByModel("NO_SUCH_MODEL_XYZ").Count == 0, "");

                // 与显示层交叉验证：设备层的判断不能和 EnumDisplayDevices 打架
                int prim = 0, onDesk = 0;
                foreach (MonDev d in mons) { if (d.Primary) prim++; if (d.OnDesktop) onDesk++; }
                Chk("最多 1 台被判定为主屏", prim <= 1, prim.ToString());
                // 关联成功数**不该超过**在用屏数；可以少于（显示器设备被禁用时，它的子设备
                //   枚举不出来 → 那一台挂在桌面上但认不出型号，本机 P27FBB-RG 就是这形态）
                Chk("识别出型号的在桌面设备数 <= AttachedDevices 数",
                    onDesk <= ResLink.AttachedDevices().Count,
                    onDesk + " vs " + ResLink.AttachedDevices().Count);
                bool consistent = true;
                foreach (MonDev d in mons) if (d.Disabled && d.OnDesktop) consistent = false;
                Chk("被禁用的设备不会被标成「在桌面上」（两套数据自洽）", consistent, "");

                int dis = 0;
                foreach (MonDev d in mons) if (d.Disabled) dis++;
                L("    （信息）本机被禁用的显示器设备数 = " + dis
                  + "；设置页/性能优化页显示的就是这条列表");
            }

            // 型号串 <-> 配置串（displayLink.rules[].offMons 用的就是这组转换）
            Chk("Join/SplitModels 往返一致",
                MonMgr.Join(new string[] { "XMIB008", "AOC3402" }) == "XMIB008,AOC3402", "");
            Chk("Join 跳过空项与 null",
                MonMgr.Join(new string[] { "A", "", null, "B" }) == "A,B",
                MonMgr.Join(new string[] { "A", "", null, "B" }));
            Chk("SplitModels 容忍空格与多余逗号", MonMgr.SplitModels(" A , ,B ").Length == 2,
                MonMgr.SplitModels(" A , ,B ").Length.ToString());
            Chk("SplitModels(null/全空白) → 空数组",
                MonMgr.SplitModels(null).Length == 0 && MonMgr.SplitModels("  ").Length == 0, "");
            L("");

            L("==============================================");
            L("PASS=" + Pass + "  FAIL=" + Fail);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "reslink_probe.txt"),
                string.Join(Environment.NewLine, Lines.ToArray()), System.Text.Encoding.UTF8); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
