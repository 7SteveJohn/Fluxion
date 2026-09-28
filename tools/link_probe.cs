// ============================================================================
//  link_probe —— 「判定要对后续新添加的游戏生效」的回归探针
//
//  验证三件事：
//    ① 分类/方案推荐（Dlssg.RecommendCategory / LinkCategory）对新加的游戏即时生效，
//       不依赖用户去改配置名单；
//    ② 游戏联动（Program.AutoCategory / AllGameProcesses）读的是同一个判定 ——
//       新加的 3A 不再被误当"竞技网游"去暂停远控，对战平台根本不参与联动；
//    ③ 界面层：扫描/刷新重建列表（对象全换新）之后，选中项重新指向**新对象**，
//       否则高亮丢失、推荐块一直渲染旧字段。
//
//  全程在 %TEMP% 沙盒里造目录，不碰用户的真实游戏与配置。
// ============================================================================
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    static class LinkProbe
    {
        static int Fail = 0;
        static readonly StringBuilder Out = new StringBuilder();

        static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        static void Chk(string what, bool ok, string extra)
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (extra == null || extra.Length == 0 ? "" : "   -> " + extra));
            if (!ok) Fail++;
        }
        static void Pump(int loops) { for (int i = 0; i < loops; i++) { Application.DoEvents(); Thread.Sleep(40); } }

        static void Txt(string p, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
        }
        static void Bytes(string p, long len)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write))
            {
                byte[] b = new byte[65536];
                long left = len;
                while (left > 0) { int n = (int)Math.Min(left, b.Length); fs.Write(b, 0, n); left -= n; }
            }
        }

        // 造一个假游戏目录：主 exe + 可选的能力组件
        static string MK(string root, string dirName, string exeName, bool fg, bool up, bool fsr)
        {
            string d = Path.Combine(root, dirName);
            Directory.CreateDirectory(d);
            Bytes(Path.Combine(d, exeName), 200000);
            if (fg) Bytes(Path.Combine(d, "nvngx_dlssg.dll"), 7400000);
            if (up) Bytes(Path.Combine(d, "nvngx_dlss.dll"), 58000000);
            if (fsr) Bytes(Path.Combine(d, "amd_fidelityfx_framegeneration_dx12.dll"), 300000);
            return d;
        }

        static DlssgGame G(string dir, string exe, string title)
        {
            var g = new DlssgGame();
            g.Dir = dir; g.Exe = exe; g.Title = title;
            return g;
        }

        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "gb_link_test");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            Directory.CreateDirectory(root);

            try { Program.Cfg = Config.Load(Program.ConfigPath); } catch { }
            // 二游分类走的是"Cfg.GachaGames 名单匹配"这条路。把名单钉成内置默认值，
            //  免得断言被本机 config.local.json 的覆盖影响（探针要测的是逻辑，不是这台机器的配置）。
            if (Program.Cfg != null)
                Program.Cfg.GachaGames = new List<string>(Config.DefaultGachaGames);
            L("=== 判定对后续新添加的游戏生效 · 回归 ===");
            L("  配置来源 = " + Program.ConfigPath);
            L("");

            // ---------------------------------------------------------------- 造"新加的游戏"
            // ① 新装的 3A 单机：目录里有 DLSS 帧生成组件，名字不在任何名单里
            string d3a = MK(root, "BrandNew AAA Game", "BrandNewAAA.exe", true, true, false);
            // ② 新装的竞技网游：名字里带竞技关键词，但**不在配置名单**里
            string dfps = MK(root, "Turbo Arena", "TurboValorantClone.exe", true, false, false);
            // ③ 对战平台：不是游戏
            string dplat = MK(root, "5E平台", "5EClient.exe", false, false, false);
            // ④ 手动添加的条目：对象上的能力字段是"过期"的（false），磁盘上其实有 FG 组件
            string dstale = MK(root, "StaleEntry", "StaleEntry.exe", true, false, false);
            // ⑤ 完全没有帧生成组件的普通游戏
            string dplain = MK(root, "Indie Puzzler", "IndiePuzzler.exe", false, false, false);
            // ⑥ 二游：名字在二游名单里，但**工具没有它的专用入口**（XeMfg.Detect 认不出）
            string dgacha = MK(root, "Genshin Impact Game", "GenshinImpact.exe", true, false, false);
            // ⑦ 二游：有专用入口 —— 鸣潮的 Client-Win64-Shipping.exe 会让 Detect 返回 "wuwa"
            string dwuwa = MK(root, "Wuthering Waves", "Client-Win64-Shipping.exe", true, false, false);

            var raw = new List<DlssgGame>();
            raw.Add(G(d3a, "BrandNewAAA.exe", "全新 3A 单机"));
            raw.Add(G(dfps, "TurboValorantClone.exe", "全新竞技网游"));
            raw.Add(G(dplat, "5EClient.exe", "5E 对战平台"));
            raw.Add(G(dplain, "IndiePuzzler.exe", "小体量游戏"));
            raw.Add(G(dgacha, "GenshinImpact.exe", "二游·无专用入口"));
            raw.Add(G(dwuwa, "Client-Win64-Shipping.exe", "二游·有专用入口"));
            var stale = G(dstale, "StaleEntry.exe", "手动添加的条目");
            stale.Custom = true;                    // 标记成 games.json 里的自定义条目
            stale.HasFrameGen = false;              // 故意过期：磁盘上其实有 nvngx_dlssg.dll
            raw.Add(stale);

            // 走真实合并管线（这一步同时会重建 Dlssg.Library 并重算每条的分类）
            List<DlssgGame> igHits;
            var lib = Dlssg.MergeScan(raw, out igHits);

            // ---------------------------------------------------------------- ① 分类
            L("=== 1. 分类（Dlssg.RecommendCategory）：不看名单也能判对 ===");
            DlssgGame gAaa = Dlssg.FindByProcess("BrandNewAAA.exe");
            DlssgGame gFps = Dlssg.FindByProcess("TurboValorantClone");
            DlssgGame gPlat = Dlssg.FindByProcess("5EClient.exe");
            DlssgGame gStale = Dlssg.FindByProcess("StaleEntry.exe");
            DlssgGame gPlain = Dlssg.FindByProcess("IndiePuzzler.exe");

            Chk("库静态引用已被合并管线重建", Dlssg.Library.Count >= 5, "库条目 = " + Dlssg.Library.Count);
            Chk("新 3A 能按 exe 名找到（FindByProcess 忽略 .exe）", gAaa != null, gAaa == null ? "null" : gAaa.Title);
            Chk("新 3A -> 3A 单机（有帧生成组件兜底）",
                Dlssg.RecommendCategory(gAaa) == "aaa", Dlssg.RecommendCategory(gAaa));
            Chk("新竞技 -> 竞技网游（关键词兜底，配置名单里没有它）",
                Dlssg.RecommendCategory(gFps) == "fps", Dlssg.RecommendCategory(gFps));
            Chk("对战平台 -> platform（不是游戏）",
                Dlssg.RecommendCategory(gPlat) == "platform", Dlssg.RecommendCategory(gPlat));
            Chk("没有帧生成组件的普通游戏 -> other",
                Dlssg.RecommendCategory(gPlain) == "other", Dlssg.RecommendCategory(gPlain));
            Chk("手动添加的过期条目：能力字段被重新探测（false -> true）",
                gStale != null && gStale.HasFrameGen, gStale == null ? "null" : ("HasFrameGen=" + gStale.HasFrameGen));

            // ---------------------------------------------------------------- ①b 二游方案推荐
            // 判据是「工具有没有这款游戏的专用入口」，不是「是不是二游」。
            //  2026-09-18 前：所有二游一律推方案 A，而方案 A 的入口按 kind 取（非绝/鸣 → version.dll），
            //  带内核反作弊的二游按文件名就能拦掉它；同屏的 OptiNote 又写着"只对绝区零/鸣潮有意义"。
            L("=== 1b. 二游方案推荐：按「有无专用入口」分流，不按「是不是二游」===");
            DlssgGame gGacha = Dlssg.FindByProcess("GenshinImpact.exe");
            DlssgGame gWuwa = Dlssg.FindByProcess("Client-Win64-Shipping.exe");
            Chk("二游（无专用入口）能被找到", gGacha != null, gGacha == null ? "null" : gGacha.Title);
            Chk("二游（无专用入口）归类 = gacha",
                gGacha != null && Dlssg.RecommendCategory(gGacha) == "gacha",
                gGacha == null ? "null" : Dlssg.RecommendCategory(gGacha));
            Chk("★ 二游（无专用入口）不推方案 A，改推 0.3.x 通用",
                gGacha != null && Dlssg.BuildRec(gGacha).Action == "030",
                gGacha == null ? "null" : (Dlssg.BuildRec(gGacha).Head + " / Action=" + Dlssg.BuildRec(gGacha).Action));
            Chk("★ 二游（无专用入口）结论里不再出现「方案 A」（与 OptiNote 不再打架）",
                gGacha != null && Dlssg.BuildRec(gGacha).Head.IndexOf("方案 A") < 0,
                gGacha == null ? "null" : Dlssg.BuildRec(gGacha).Head);
            Chk("★ 二游（无专用入口）文案给出「换入口重试」的替代路径",
                gGacha != null && Dlssg.BuildRec(gGacha).Why.IndexOf("换入口重试") >= 0, "");
            Chk("★ 二游（无专用入口）文案点出两个前置（DX12 + Reflex）",
                gGacha != null && Dlssg.BuildRec(gGacha).Why.IndexOf("DX12") >= 0
                && Dlssg.BuildRec(gGacha).Why.IndexOf("Reflex") >= 0, "");
            Chk("二游（有专用入口 = 鸣潮）仍推方案 A",
                gWuwa != null && Dlssg.BuildRec(gWuwa).Action == "opti",
                gWuwa == null ? "null" : (Dlssg.BuildRec(gWuwa).Head + " / Action=" + Dlssg.BuildRec(gWuwa).Action));
            L("");

            // ---------------------------------------------------------------- ② 联动档位
            L("=== 2. 联动判定（Program.AutoCategory / AllGameProcesses）===");
            string cAaa = Program.AutoCategory("BrandNewAAA");
            string cFps = Program.AutoCategory("TurboValorantClone");
            string cPlat = Program.AutoCategory("5EClient");
            string cUnknown = Program.AutoCategory("TotallyUnknownExe");

            Chk("新 3A 的联动档位 = aaa（电源/加速包照做，但远控不碰）", cAaa == "aaa", cAaa);
            Chk("★ 新 3A 不再被判成 fps（旧代码 return \"fps\" 会让 UU远程 被暂停）", cAaa != "fps", cAaa);
            Chk("新竞技 -> fps（该暂停远控的就暂停）", cFps == "fps", cFps);
            Chk("对战平台 -> 空档位（不参与任何游戏联动）", cPlat.Length == 0, "([" + cPlat + "])");
            Chk("完全未知的进程 -> 不再默认 fps", cUnknown != "fps", cUnknown);

            var all = Program.AllGameProcesses();
            bool hasNewAaa = all.Contains("BrandNewAAA");
            bool hasNewFps = all.Contains("TurboValorantClone");
            bool hasStale = all.Contains("StaleEntry");
            bool hasPlat = all.Contains("5EClient");
            L("      AllGameProcesses() = " + string.Join(", ", all.ToArray()));
            Chk("★ 新加的 3A 已进入「游戏进程」名单（否则电源/加速包/亲和性全轮不到它）", hasNewAaa, "含 BrandNewAAA=" + hasNewAaa);
            Chk("新加的竞技也在名单里", hasNewFps, "含 TurboValorantClone=" + hasNewFps);
            Chk("手动添加的条目也在名单里", hasStale, "含 StaleEntry=" + hasStale);
            Chk("对战平台**不在**名单里（它不是游戏）", !hasPlat, "含 5EClient=" + hasPlat);
            Chk("新游戏的手动档位显示名取自库标题（不是原始进程名）",
                Program.GameDisplayName("BrandNewAAA") == "全新 3A 单机", Program.GameDisplayName("BrandNewAAA"));
            Chk("库里没有的进程仍原样返回（不瞎编名字）",
                Program.GameDisplayName("NoSuchProc") == "NoSuchProc", Program.GameDisplayName("NoSuchProc"));
            L("");

            // ---------------------------------------------------------------- ③ 扫描会重算
            L("=== 3. 再扫描一次：判定必须重算，而不是沿用旧结论 ===");
            var raw2 = new List<DlssgGame>();
            foreach (var g in lib)
            {
                var n = G(g.Dir, g.Exe, g.Title);       // 新对象，RecCat 为空
                n.Custom = g.Custom;
                raw2.Add(n);
            }
            // 新增一个刚下载的游戏
            string dnew = MK(root, "JustDownloaded", "JustDownloaded.exe", true, false, false);
            raw2.Add(G(dnew, "JustDownloaded.exe", "刚下载的游戏"));
            List<DlssgGame> ig2;
            var lib2 = Dlssg.MergeScan(raw2, out ig2);
            DlssgGame gNew = Dlssg.FindByProcess("JustDownloaded");
            Chk("第二次合并后新游戏能被找到", gNew != null, gNew == null ? "null" : gNew.Title);
            Chk("它的联动档位已算好（= aaa）", Dlssg.LinkCategory(gNew) == "aaa", Dlssg.LinkCategory(gNew));
            Chk("它已进入进程名单", Program.AllGameProcesses().Contains("JustDownloaded"), "");
            Chk("旧对象不受影响（判定是逐条重算，不是共享可变状态）",
                Dlssg.LinkCategory(gAaa) == "aaa", Dlssg.LinkCategory(gAaa));
            L("");

            // ---------------------------------------------------------------- ④ 界面层
            L("=== 4. 界面层：列表重建后选中项要指向新对象 ===");
            try
            {
                var f = new MainForm(Program.Cfg, false);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-6000, -6000);
                f.Size = new Size(1180, 1000);
                f.Show();
                Pump(4);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fg = typeof(MainForm).GetField("games", flags);
                FieldInfo fs = typeof(MainForm).GetField("selected", flags);
                MethodInfo mFill = typeof(MainForm).GetMethod("FillGameList", flags);
                MethodInfo mRec = typeof(MainForm).GetMethod("RefreshRec", flags);
                FieldInfo fBig = typeof(MainForm).GetField("lblRecBig", flags);
                Chk("反射拿到 games / selected / FillGameList / RefreshRec",
                    fg != null && fs != null && mFill != null && mRec != null, "");

                // 用沙盒库替换掉真实列表，并选中"刚下载的游戏"
                fg.SetValue(f, lib2);
                fs.SetValue(f, gNew);
                mFill.Invoke(f, null);
                mRec.Invoke(f, null);
                var sel1 = fs.GetValue(f) as DlssgGame;
                string head1 = (fBig == null || fBig.GetValue(f) == null) ? "(null)" : ((Label)fBig.GetValue(f)).Text;
                Chk("选中项就是新加的游戏", sel1 != null && sel1.Dir == dnew, sel1 == null ? "null" : sel1.Title);
                Chk("推荐结论已给出（不是「先选一个游戏」）",
                    head1.Length > 0 && head1.IndexOf("先选一个游戏") < 0, head1);
                Chk("推荐结论 = BuildRec 的结论（界面与判定同源）",
                    head1 == Dlssg.BuildRec(sel1).Head, head1 + " | " + Dlssg.BuildRec(sel1).Head);

                // 模拟"又来了一次扫描"：真实链路是 **扫描层产出全新对象**（只有 Dir/Exe/Title），
                // 再交给 MergeScan 重探能力 + 重算分类。这里照做——否则 MergeScan 会复用旧对象引用，
                // 测不出"列表重建后选中项是否跟着换新"。
                var scannedLike = new List<DlssgGame>();
                foreach (var g in lib2) scannedLike.Add(G(g.Dir, g.Exe, g.Title));
                List<DlssgGame> ig3;
                var lib3 = Dlssg.MergeScan(scannedLike, out ig3);
                fg.SetValue(f, lib3);
                mFill.Invoke(f, null);            // FillGameList 里会按目录重新定位 selected
                var sel2 = fs.GetValue(f) as DlssgGame;
                Chk("★ 列表重建后 selected 换成新对象（否则高亮丢失、推荐块渲染旧字段）",
                    sel2 != null && !object.ReferenceEquals(sel1, sel2), 
                    (sel2 == null ? "null" : ("新对象=" + object.ReferenceEquals(sel1, sel2))));
                Chk("换的是对象、不是换掉选择（目录不变）",
                    sel2 != null && sel2.Dir == sel1.Dir, sel2 == null ? "null" : sel2.Dir);
                mRec.Invoke(f, null);
                string head2 = (fBig == null || fBig.GetValue(f) == null) ? "(null)" : ((Label)fBig.GetValue(f)).Text;
                Chk("★ 重扫后结论与重扫前一致（判定重算而不是沿用旧对象）", head2 == head1, head1 + " | " + head2);
                Chk("界面结论 = BuildRec（界面与判定同源）", head2 == Dlssg.BuildRec(sel2).Head, head2);
                Chk("新对象上确实带上了能力字段（证明重探发生在合并管线里）",
                    sel2 != null && sel2.HasFrameGen, sel2 == null ? "null" : ("HasFrameGen=" + sel2.HasFrameGen));
                f.Close();
                Pump(3);
            }
            catch (Exception ex)
            {
                Chk("界面层验证未抛异常", false, ex.GetType().Name + ": " + ex.Message);
            }

            // ---------------------------------------------------------------- ⑤ 显卡代际门禁
            L("=== 5. 显卡代际门禁（分享给别人时，对方的卡各不相同）===");
            Chk("RTX 3060 Ti -> ampere", Dlssg.GpuTierFromName("NVIDIA GeForce RTX 3060 Ti") == "ampere",
                Dlssg.GpuTierFromName("NVIDIA GeForce RTX 3060 Ti"));
            Chk("RTX 5090 -> blackwell", Dlssg.GpuTierFromName("NVIDIA GeForce RTX 5090") == "blackwell",
                Dlssg.GpuTierFromName("NVIDIA GeForce RTX 5090"));
            Chk("RTX 4070 Ti -> ada", Dlssg.GpuTierFromName("NVIDIA GeForce RTX 4070 Ti") == "ada",
                Dlssg.GpuTierFromName("NVIDIA GeForce RTX 4070 Ti"));
            Chk("RTX 2080 SUPER -> turing", Dlssg.GpuTierFromName("NVIDIA GeForce RTX 2080 SUPER") == "turing",
                Dlssg.GpuTierFromName("NVIDIA GeForce RTX 2080 SUPER"));
            Chk("GTX 1660 SUPER -> gtx", Dlssg.GpuTierFromName("NVIDIA GeForce GTX 1660 SUPER") == "gtx",
                Dlssg.GpuTierFromName("NVIDIA GeForce GTX 1660 SUPER"));
            Chk("AMD RX 7900 XTX -> non-nvidia", Dlssg.GpuTierFromName("AMD Radeon RX 7900 XTX") == "non-nvidia",
                Dlssg.GpuTierFromName("AMD Radeon RX 7900 XTX"));
            Chk("Intel Arc -> non-nvidia", Dlssg.GpuTierFromName("Intel(R) Arc(TM) A770") == "non-nvidia",
                Dlssg.GpuTierFromName("Intel(R) Arc(TM) A770"));
            Chk("「(探测中)」-> unknown（WMI 没回来时不误判）",
                Dlssg.GpuTierFromName("(探测中)") == "unknown", Dlssg.GpuTierFromName("(探测中)"));

            Dlssg.Rec g40 = Dlssg.GpuGateRec("ada");   // Rec 是 Dlssg 的嵌套类型，探针里必须带外层限定
            Chk("40 系 -> 劝退（原生支持，按钮禁用）",
                g40 != null && g40.Action == "none" && g40.Head.IndexOf("原生支持") >= 0,
                g40 == null ? "null" : g40.Head);
            Chk("40 系文案点名「伪装是降级」",
                g40 != null && g40.Why.IndexOf("降级") >= 0, g40 == null ? "null" : "Why");
            Dlssg.Rec gg = Dlssg.GpuGateRec("gtx");
            Chk("GTX -> 明确不支持（红色告警）",
                gg != null && gg.Bad && gg.Action == "none" && gg.Head.IndexOf("不在") >= 0,
                gg == null ? "null" : gg.Head);
            Chk("AMD/Intel -> 指向游戏自带的 FSR3/XeSS",
                Dlssg.GpuGateRec("non-nvidia") != null
                    && Dlssg.GpuGateRec("non-nvidia").Why.IndexOf("FSR3") >= 0, "");
            Chk("30 系不被门禁拦（本机路线不受影响）", Dlssg.GpuGateRec("ampere") == null, "");
            Chk("20 系不被门禁拦", Dlssg.GpuGateRec("turing") == null, "");
            Chk("本机显卡代际 = ampere（RTX 3060 Ti）", Dlssg.GpuTier() == "ampere", Program.GpuName);
            Chk("本机 BuildRec 未被门禁改写（3A 游戏仍有可执行动作）",
                Dlssg.BuildRec(gAaa).Action != "none", Dlssg.BuildRec(gAaa).Head);
            L("");

            try { Directory.Delete(root, true); } catch { }
            L("");
            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "link_probe.txt"),
                    Out.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return Fail;
        }
    }
}
