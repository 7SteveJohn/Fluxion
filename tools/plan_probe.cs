using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;

namespace Fluxion
{
    // v2.10.7 回归：方案切换（Plan.SwitchTo）必须是"只改名、不删除"，且能无限来回。
    //  全程在 %TEMP% 沙盒里造假游戏目录，绝不碰真实游戏目录。
    static class PlanProbe
    {
        static int Fail = 0;
        static StringBuilder Out = new StringBuilder();
        static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        static void Chk(string what, bool ok, string got)
        {
            if (!ok) Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (got.Length > 0 ? "   (" + got + ")" : ""));
        }
        static void Big(string p, int mb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
            {
                byte[] b = new byte[1024 * 1024];
                for (int i = 0; i < mb; i++) fs.Write(b, 0, b.Length);
            }
        }
        static void Tiny(string p) { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "x"); }
        // 按精确字节数造文件（旧版代理是 15,667,520 B，用 MB 级 Big() 造不出这个值）
        static void Exact(string p, long bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
            {
                byte[] b = new byte[65536];
                long left = bytes;
                while (left > 0) { int n = (int)Math.Min(left, b.Length); fs.Write(b, 0, n); left -= n; }
            }
        }

        static string[] Snap(string dir)
        {
            List<string> l = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) l.Add(Path.GetFileName(f));
            l.Sort();
            return l.ToArray();
        }

        static int Main()
        {
            try { Program.Cfg = Config.Load(Program.ConfigPath); } catch { }

            string tmp = Path.Combine(Path.GetTempPath(), "gb_plan_test");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            L("沙盒根 = " + tmp);
            L("");

            // ---------- 造一个"方案 A 在用"的假鸣潮目录 ----------
            string W = Path.Combine(tmp, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(W);
            Tiny(Path.Combine(W, "Client-Win64-Shipping.exe"));
            Big(Path.Combine(W, "dxgi.dll"), 21);                    // >20MB → 认作 OptiScaler
            Tiny(Path.Combine(W, "fakenvapi.dll"));
            Tiny(Path.Combine(W, "OptiScaler.ini"));
            // 预置 0.3.0 的停放件（真实体积 17.5MB，改回原名后仍要能被认出来）与配套文件
            Big(Path.Combine(W, "dxgi.dll.parked.030"), 18);
            Tiny(Path.Combine(W, "nvngx_dlssg.dll"));
            Tiny(Path.Combine(W, "dlssg_sm86.ini"));

            L("=== 1. 当前方案识别 ===");
            Chk("方案 A 在用 → Current = opti", Plan.Current(W) == Plan.Opti, Plan.Current(W));
            L("");

            string[] before = Snap(W);
            L("=== 2. 切到 0.3.0 ===");
            string r1 = Plan.SwitchTo(W, Plan.D030);
            L("  回报：" + r1.Replace("\n", "\n  "));
            try
            {
                FileInfo fi = new FileInfo(Path.Combine(W, "dxgi.dll"));
                L("    [调试] dxgi.dll 存在=" + fi.Exists + "  大小=" + (fi.Exists ? fi.Length : -1)
                  + "  OwnerOf=" + (fi.Exists ? "[" + Plan.OwnerOf(Path.Combine(W, "dxgi.dll")) + "]" : "-"));
            }
            catch (Exception ex) { L("    [调试] 异常 " + ex.Message); }
            Chk("切完 Current = 030", Plan.Current(W) == Plan.D030, Plan.Current(W));
            Chk("方案 A 的入口被改名停放（文件还在）", File.Exists(Path.Combine(W, "dxgi.dll.parked.opti")), "");
            Chk("dxgi.dll 位置上是 0.3.0 的入口", File.Exists(Path.Combine(W, "dxgi.dll")), "");
            Chk("停放的是 0.3.0 那份（.parked.030 已改回原名）",
                !File.Exists(Path.Combine(W, "dxgi.dll.parked.030")), "");
            L("");

            L("=== 3. 停放全部（回到原生） ===");
            string r2 = Plan.SwitchTo(W, Plan.None);
            L("  回报：" + r2.Replace("\n", "\n  "));
            Chk("停放后 Current = none", Plan.Current(W) == Plan.None, Plan.Current(W));
            Chk("入口 dxgi.dll 已改名", !File.Exists(Path.Combine(W, "dxgi.dll")), "");
            Chk("0.3.0 那份停放为 .parked.030", File.Exists(Path.Combine(W, "dxgi.dll.parked.030")), "");
            L("");

            L("=== 4. 再切回方案 A（来回切） ===");
            string r3 = Plan.SwitchTo(W, Plan.Opti);
            L("  回报：" + r3.Replace("\n", "\n  "));
            Chk("切回后 Current = opti", Plan.Current(W) == Plan.Opti, Plan.Current(W));
            Chk("dxgi.dll 就位", File.Exists(Path.Combine(W, "dxgi.dll")), "");
            Chk(".parked.opti 已改回原名", !File.Exists(Path.Combine(W, "dxgi.dll.parked.opti")), "");
            L("");

            L("=== 5. 关键：全程没有任何文件被删除 ===");
            string[] after = Snap(W);
            // 只保证"不减少"：新增文件是允许的 —— SwitchTo(0.3.x) 会顺手把被旧模板污染的
            //  dlssg_sm86.ini 备份成 .bak-oldtemplate（这是 2026-09-16 绝区零 ini 缺失事故的修复配套）。
            Chk("文件总数不减少（切换只增不删）", after.Length >= before.Length, before.Length + " → " + after.Length);
            List<string> missing = new List<string>();
            foreach (string f in before)
            {
                string bare = f;
                int k = bare.IndexOf(".parked.");
                if (k >= 0) bare = bare.Substring(0, k);
                bool found = false;
                foreach (string g in after)
                {
                    string gb = g; int k2 = gb.IndexOf(".parked.");
                    if (k2 >= 0) gb = gb.Substring(0, k2);
                    if (gb == bare) { found = true; break; }
                }
                if (!found) missing.Add(f);
            }
            Chk("没有任何文件消失", missing.Count == 0,
                missing.Count == 0 ? "" : string.Join("、", missing.ToArray()));
            L("");

            L("=== 6. 旧版入口（version/dinput8）也能识别 ===");
            string W2 = Path.Combine(tmp, "Legacy", "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(W2);
            Tiny(Path.Combine(W2, "Client-Win64-Shipping.exe"));
            Big(Path.Combine(W2, "dinput8.dll"), 15);                 // 15MB → 旧版 dlssg_for_sm86
            Chk("只装旧版代理 → Current = legacy", Plan.Current(W2) == Plan.Legacy, Plan.Current(W2));
            L("");

            L("=== 7. 绝区零槽位（d3d12.dll） ===");
            string Z = Path.Combine(tmp, "ZZZ", "ZenlessZoneZero Game");
            Directory.CreateDirectory(Z);
            Tiny(Path.Combine(Z, "ZenlessZoneZero.exe"));
            Big(Path.Combine(Z, "d3d12.dll"), 26);                    // 26MB → OptiScaler
            Chk("绝区零 + 26MB d3d12 → opti", Plan.Current(Z) == Plan.Opti, Plan.Current(Z));
            Big(Path.Combine(Z, "d3d12.dll.parked.030"), 18);
            Tiny(Path.Combine(Z, "nvngx_dlssg.dll"));
            Tiny(Path.Combine(Z, "dlssg_sm86.ini"));
            string rz = Plan.SwitchTo(Z, Plan.D030);
            L("  切到 0.3.0：" + rz.Split('\n')[0]);
            Chk("绝区零切完 → 030", Plan.Current(Z) == Plan.D030, Plan.Current(Z));
            Chk("绝区零也能切回",
                Plan.SwitchTo(Z, Plan.Opti).IndexOf("已切换到") >= 0 && Plan.Current(Z) == Plan.Opti, "");
            L("");

            L("=== 7b. 入口体积分档表（0.3.2/0.3.5 的 28.6MB 曾被误判成 OptiScaler）===");
            //  2026-09-17：资源包换成 0.3.2 之后，它的入口是 29,975,840 B（28.59 MB）——
            //  比 OptiScaler 的入口（24.5 / 24.9 MB）**还大**，OwnerOf 原来的顺序
            //  （先判 >20MB → Opti）会把它整类认成方案 A。这里把每一档钉死。
            string C1 = Path.Combine(tmp, "SizeProbe");
            Directory.CreateDirectory(C1);
            Tiny(Path.Combine(C1, "x.exe"));
            object[][] sizes = new object[][]
            {
                new object[] { "0.3.0 入口 17,529,120 B", 17529120L, Plan.D030 },
                new object[] { "0.3.2 入口 version 29,975,840 B", 29975840L, Plan.D030 },
                new object[] { "0.3.2 dbghelp 29,993,760 B", 29993760L, Plan.D030 },
                new object[] { "0.3.5 入口 version 30,021,920 B", 30021920L, Plan.D030 },
                new object[] { "0.3.5 dbghelp 30,039,840 B", 30039840L, Plan.D030 },
                new object[] { "OptiScaler d3d12 25,656,288 B", 25656288L, Plan.Opti },
                new object[] { "OptiScaler dxgi 26,116,608 B", 26116608L, Plan.Opti },
                new object[] { "旧版 15,667,520 B", 15667520L, Plan.Legacy },
            };
            foreach (object[] s in sizes)
            {
                string f = Path.Combine(C1, "probe_entry.dll");
                Exact(f, (long)s[1]);
                Chk((string)s[0] + " → " + (string)s[2], Plan.OwnerOf(f) == (string)s[2],
                    "[" + Plan.OwnerOf(f) + "]");
                File.Delete(f);
            }
            L("");

            L("=== 8. 旧版代理（14.94 MB）必须能被认出来 ===");
            string L2 = Path.Combine(tmp, "Legacy2", "Cyberpunk 2077");
            Directory.CreateDirectory(L2);
            Tiny(Path.Combine(L2, "Cyberpunk2077.exe"));
            Exact(Path.Combine(L2, "winmm.dll"), 15667520L);            // 精确的旧版体积
            Exact(Path.Combine(L2, "dlssg_sm86.ini.bak"), 236L);
            Chk("14.94MB 的旧版代理 → Current = legacy", Plan.Current(L2) == Plan.Legacy, Plan.Current(L2));
            Chk("  OwnerOf 也认它", Plan.OwnerOf(Path.Combine(L2, "winmm.dll")) == Plan.Legacy,
                Plan.OwnerOf(Path.Combine(L2, "winmm.dll")));
            L("");

            L("=== 9. 通用游戏（3A 单机）切到 0.3.x ===");
            string G3 = Path.Combine(tmp, "ForzaHorizon6");
            Directory.CreateDirectory(G3);
            Tiny(Path.Combine(G3, "ForzaHorizon6.exe"));
            Exact(Path.Combine(G3, "version.dll"), 15667520L);          // 先摆一个旧版代理（= 修复前的现状）
            Tiny(Path.Combine(G3, "nvngx_dlssg.dll"));                  // 游戏自带，不能被我们覆盖
            // ★ 沙盒纪律：通用入口名必须**显式固定**，不能跟着探针所在机器的配置走 ——
            //   2026-09-17 实测：用户真实 config.local.json 里 genericEntry=d3d12.dll（他在 UI 下拉里选过），
            //   EntryFor("") 读到它 → 装的是 d3d12.dll → 断言 version.dll 必然 FAIL。这不是代码回归。
            string genericEntrySaved = Program.Cfg != null ? Program.Cfg.DlssgGenericEntry : null;
            if (Program.Cfg != null) Program.Cfg.DlssgGenericEntry = "version.dll";
            L("  （沙盒固定通用入口 = version.dll；探针机器的真实配置是 " + (genericEntrySaved ?? "(null)") + "）");
            string nvBefore = "";
            try { nvBefore = new FileInfo(Path.Combine(G3, "nvngx_dlssg.dll")).Length.ToString(); } catch { }
            L("  切到 0.3.x：" + Plan.SwitchTo(G3, Plan.D030).Replace("\n", " | "));
            Chk("通用游戏切完 → Current = 030", Plan.Current(G3) == Plan.D030, Plan.Current(G3));
            Chk("旧版代理被改名停放（文件还在）", File.Exists(Path.Combine(G3, "version.dll.parked.legacy")), "");
            // 断言跟着资源包走：0.3.0/0.3.1 = 17,529,120 B，0.3.2 = 29,975,840 B。
            //  写死某一个数字，下次作者换包探针就会假红（2026-09-17 换 0.3.2 时就踩了）。
            long entLen = File.Exists(Path.Combine(G3, "version.dll"))
                       ? new FileInfo(Path.Combine(G3, "version.dll")).Length : 0;
            Chk("入口位置上是 0.3.x 的代理（体积落在 0.3.x 区间）",
                entLen > 0 && Dlssg030.IsProxySize(entLen), entLen.ToString());
            Chk("ini 换成了 0.3.x 出厂版（含 [General]）",
                Dlssg030.HasGeneral(Path.Combine(G3, "dlssg_sm86.ini")), "");
            Chk("游戏自带的 nvngx_dlssg.dll 未被覆盖（仍是 " + nvBefore + " B）",
                new FileInfo(Path.Combine(G3, "nvngx_dlssg.dll")).Length.ToString() == nvBefore, "");
            L("  停放：" + Plan.SwitchTo(G3, Plan.None).Split('\n')[0]);
            Chk("通用游戏也能停放回原生", Plan.Current(G3) == Plan.None, Plan.Current(G3));
            L("");

            L("=== 10. 旧版代理的 INI 上限必须夹到 3（写 5 会卡死游戏）===");
            string ini5 = Dlssg.BuildIniFor(5, 1);
            Chk("BuildIniFor(5) 实际写 3", ini5.IndexOf("MaxGeneratedFrames=3") >= 0
                && ini5.IndexOf("MaxGeneratedFrames=5") < 0, "");
            Chk("BuildIniFor(1) 保持 1", Dlssg.BuildIniFor(1, 1).IndexOf("MaxGeneratedFrames=1") >= 0, "");
            Chk("BuildIniFor(0) 兜到 1", Dlssg.BuildIniFor(0, 1).IndexOf("MaxGeneratedFrames=1") >= 0, "");

            L("=== 11. 互斥缺口回归（2026-09-18 用户实测的「两个插件打架」）===");
            //  症状：点了「切换到本方案」，原来那套仍在目录里生效，用户还得手动再点一次「停放」。
            //  三条根因逐条钉住：
            //   ① 配套组件（OptiScaler.ini / fakenvapi.dll 这类）以前根本不停放 → 目录里两套长期并存；
            //   ② 停放失败被 `catch { }` 吞掉，界面照样报"已切换" → 明明没关掉却说关掉了；
            //   ③ 护栏（Dlssg030.Install / Dlssg.Uninstall）把用户打发去"先手动停放再回来"。
            string M = Path.Combine(tmp, "MutexZZZ", "ZenlessZoneZero Game");
            Directory.CreateDirectory(M);
            Tiny(Path.Combine(M, "ZenlessZoneZero.exe"));
            Big(Path.Combine(M, "d3d12.dll"), 26);                     // 方案 A 占着绝区零的入口槽
            Tiny(Path.Combine(M, "OptiScaler.ini"));                   // 方案 A 的配套组件
            Tiny(Path.Combine(M, "fakenvapi.dll"));
            Chk("前置：方案 A 在位", Plan.Current(M) == Plan.Opti, Plan.Current(M));

            string mr = Plan.SwitchTo(M, Plan.D030);
            L("  切到 0.3.x：" + mr.Split('\n')[0]);
            Chk("① 入口被停放", File.Exists(Path.Combine(M, "d3d12.dll.parked.opti")), "");
            Chk("① 配套组件 OptiScaler.ini 一并停放（旧实现在这里会漏）",
                File.Exists(Path.Combine(M, "OptiScaler.ini.parked.opti"))
                && !File.Exists(Path.Combine(M, "OptiScaler.ini")), "");
            Chk("① 配套组件 fakenvapi.dll 一并停放",
                File.Exists(Path.Combine(M, "fakenvapi.dll.parked.opti")), "");
            Chk("③ 回执不再打发用户去手动停放（旧实现回一段「请先…停放」）",
                mr.IndexOf("请先") < 0, mr.Split('\n')[0]);
            Chk("① LiveOthers 查得出没有别家活入口", Plan.LiveOthers(M, Plan.D030).Count == 0,
                string.Join("、", Plan.LiveOthers(M, Plan.D030).ToArray()));

            Plan.SwitchTo(M, Plan.Opti);
            Chk("② 切回：入口复原", File.Exists(Path.Combine(M, "d3d12.dll")), "");
            Chk("② 切回：OptiScaler.ini 复原（不能只有入口回来、组件还躺在 .parked 里）",
                File.Exists(Path.Combine(M, "OptiScaler.ini")), "");
            Chk("② 切回：fakenvapi.dll 复原", File.Exists(Path.Combine(M, "fakenvapi.dll")), "");

            L("  — 别家的活入口缩在「另一个入口名」上：Current 看不出，LiveOthers 必须看得出 —");
            Big(Path.Combine(M, "version.dll"), 29);                   // 0.3.2 缩在 version.dll 上
            List<string> lo = Plan.LiveOthers(M, Plan.Opti);
            Chk("② LiveOthers 查得出来", lo.Count == 1 && lo[0].StartsWith("version.dll"),
                string.Join("、", lo.ToArray()));
            Chk("② 这正是 Current 会漏掉的场景（它先看主入口，主入口对了就返回）",
                Plan.Current(M) == Plan.Opti, Plan.Current(M));

            L("  — 「关闭帧生成」不再要求用户先去点一次停放 —");
            DlssgGame mg = new DlssgGame();
            mg.Title = "绝区零"; mg.Dir = M; mg.Exe = "ZenlessZoneZero.exe";
            string ur = Dlssg.Uninstall(mg, false);
            L("    Uninstall 回执：" + ur.Replace("\n", " | "));
            Chk("③ 不再出现「请先用停放」这类打发人的话", ur.IndexOf("请先用") < 0, ur);
            Chk("③ 关完目录里没有任何活入口", Plan.Current(M) == Plan.None, Plan.Current(M));
            Chk("③ 回执里说明了方案 A 是被自动停放的", ur.IndexOf("已停放方案 A") >= 0, ur);

            L("  — 回执工具 Plan.Note：停放与失败都要说出来 —");
            string note = Plan.Note(new List<string> { "a.dll → a.dll.parked.opti" }, new List<string> { "b.dll：拒绝访问" });
            Chk("Note 报出已停放项", note.IndexOf("已停放另一套方案") >= 0, note);
            Chk("Note 报出没停放成的项（旧实现是 catch 吞掉、界面报成功）",
                note.IndexOf("没停放成") >= 0 && note.IndexOf("b.dll") >= 0, note);
            L("");

            L("=== 12. 收口回归（2026-09-18：只剩一条部署路径 / INI 年代跟随代理 / 启动器护栏搬位置）===");
            //  ① 以前除 Plan.SwitchTo 外还有 `Dlssg.Install` 那套零互斥的部署实现（「开启帧生成」调它）。
            //     它已删除；剩下的 InstallAt（SwitchEntry 用）现在自己也会停放别家方案。
            //  ② 这条路径以前一律写 261 B 旧模板 INI，却抄进去一份 0.3.x 代理 → 上限白丢两档（4X 而非 6X）。
            //  ③ 「启动器根目录」护栏随 Dlssg.Install 一起没了，现在落在 Plan.SwitchTo 上（改成按目录判）。

            // ③ 启动器容器：games\ 子目录 + launcher 类 exe 同时成立才拦
            string LC = Path.Combine(tmp, "FakeLauncherRoot");
            Directory.CreateDirectory(Path.Combine(LC, "games", "SomeGame"));
            Tiny(Path.Combine(LC, "launcher.exe"));
            string lr = Plan.SwitchTo(LC, Plan.D030, true);
            Chk("③ 启动器根目录被拒（装进去会同时作用于容器下所有游戏）",
                lr.IndexOf("启动器") >= 0, lr.Split('\n')[0]);
            Chk("③ 拒绝时一个文件都没写进去", !File.Exists(Path.Combine(LC, "version.dll")), "");
            string PL = Path.Combine(tmp, "PlainLauncherDir");
            Directory.CreateDirectory(PL);
            Tiny(Path.Combine(PL, "launcher.exe"));
            Chk("③ 判据要两个条件同时成立 —— 只有 launcher.exe、没有 games\\ 的不算容器"
                + "（鸣潮根目录就是这样）", !Dlssg.IsLauncherDir(PL), "");

            // ① 先单独直测 ParkOthers，免得把它的行为与 SwitchEntry 的编排混在一起
            string Patrol = Path.Combine(tmp, "ParkDirect");
            Directory.CreateDirectory(Patrol);
            Big(Path.Combine(Patrol, "d3d12.dll"), 26);
            Tiny(Path.Combine(Patrol, "OptiScaler.ini"));
            List<string> df = new List<string>();
            List<string> dp = Plan.ParkOthers(Patrol, "", Plan.D030, df);
            L("    ParkOthers 直测：parked=" + dp.Count + "、failed=" + df.Count
              + " → " + string.Join("；", dp.ToArray()));
            Chk("① ParkOthers 直测：26MB 的 d3d12.dll 被判成别家方案并停放", dp.Count >= 1,
                string.Join("；", dp.ToArray()));

            // ①② 第二套部署路径：SwitchEntry → InstallAt
            //  这一节以前直接吃真机 %ProgramData%\Fluxion\dlssg\source 里的文件 ——
            //  断言"抄进去的是 0.3.x 代理"能成立，纯粹因为那份文件放错了源位（是 0.3.2）。
            //  现在把 DataDir 临时指向沙盒、自己造源件，跑完还原：测的才是判据本身。
            string seedDir = Path.Combine(tmp, "dd");
            string seedSrc = Path.Combine(Path.Combine(seedDir, "dlssg"), "source");
            Directory.CreateDirectory(seedSrc);
            FieldInfo ddf = typeof(Program).GetField("dataDirCache", BindingFlags.NonPublic | BindingFlags.Static);
            object dataDirBack = ddf == null ? null : ddf.GetValue(null);
            if (ddf != null) ddf.SetValue(null, seedDir);

            // P1-6 回归：源位上躺一份 0.3.x 体积的件，SourceReady 不能再报"已下载"
            Exact(Path.Combine(seedSrc, "version.dll"), 29975840);
            Chk("②-0 源位上是 0.3.x 体积 → SourceReady 为假（旧判据只看 File.Exists，会误报已下载）",
                !Dlssg.SourceReady(), Dlssg.SourceHint());
            Chk("②-0 还要说清为什么不算（不能只丢一句「未下载」）",
                Dlssg.SourceHint().IndexOf("29,975,840", StringComparison.Ordinal) >= 0
                && Dlssg.SourceHint().IndexOf("0.2.x", StringComparison.Ordinal) >= 0,
                Dlssg.SourceHint());
            Exact(Path.Combine(seedSrc, "version.dll"), 15667520);
            Chk("②-0 换成正确的 0.2.x 源件 → SourceReady 为真", Dlssg.SourceReady(), "");

            string G4 = Path.Combine(tmp, "SwitchEntryGame");
            Directory.CreateDirectory(G4);
            Tiny(Path.Combine(G4, "game.exe"));
            Exact(Path.Combine(G4, "winmm.dll"), 15667520);            // 当前入口：旧版代理
            Big(Path.Combine(G4, "d3d12.dll"), 26);                     // 方案 A 缩在另一个入口名上
            Tiny(Path.Combine(G4, "OptiScaler.ini"));
            DlssgGame g4 = new DlssgGame();
            g4.Title = "测试游戏"; g4.Dir = G4; g4.Exe = "game.exe"; g4.Entry = "winmm.dll";
            string sr = Dlssg.SwitchEntry(g4);
            L("    SwitchEntry 回执：" + sr.Replace("\n", " | "));
            Chk("① 换入口时把别家方案也停放掉（旧实现只认主入口，会留它活着）",
                File.Exists(Path.Combine(G4, "d3d12.dll.parked.opti")), "");
            Chk("① 回执说明了这次停放（旧实现把 Uninstall 的回执整段丢掉）",
                sr.IndexOf("已停放") >= 0, sr);
            Chk("① 换完没有别家活入口", Plan.LiveOthers(G4, Plan.Current(G4)).Count == 0,
                string.Join("、", Plan.LiveOthers(G4, Plan.Current(G4)).ToArray()));
            Chk("② 抄进去的是源位上那一份（源件决定代际，旧版路线装的就是 0.2.x）",
                File.Exists(Path.Combine(G4, "version.dll"))
                && new FileInfo(Path.Combine(G4, "version.dll")).Length == 15667520,
                Plan.OwnerOf(Path.Combine(G4, "version.dll")));
            Chk("② 这条路线的归属判出来是 legacy（不是 0.3.x —— 那需要 pack 路线）",
                Plan.OwnerOf(Path.Combine(G4, "version.dll")) == Plan.Legacy,
                Plan.OwnerOf(Path.Combine(G4, "version.dll")));
            if (ddf != null) ddf.SetValue(null, dataDirBack);
            Chk("② DataDir 已还原（本节不污染后面的节次）",
                string.Equals(Program.DataDir, Convert.ToString(dataDirBack), StringComparison.OrdinalIgnoreCase),
                Program.DataDir);
            L("");
            L("");

            // ---------- 13. 跨代代理并存的只读巡检（P0-3） ----------
            //  照 CP2077 根目录的实测形态造：0.3.5 的 version.dll（30,021,920）
            //  与 0.2.x 的 winmm.dll（15,678,272）同时在位，两代抢同一个入口。
            L("");
            L("=== 13. 入口巡检：跨代代理并存要看得见（P0-3） ===");
            string Jp5 = Path.Combine(tmp, "patrol");
            Directory.CreateDirectory(Jp5);
            Tiny(Path.Combine(Jp5, "game.exe"));
            Exact(Path.Combine(Jp5, "version.dll"), 30021920);            // 0.3.5
            Exact(Path.Combine(Jp5, "winmm.dll"), 15678272);              // 0.2.x 遗留
            Tiny(Path.Combine(Jp5, "dlssg_sm86.ini"));
            string pl = Plan.PatrolLine(Jp5);
            L("    " + pl);
            Chk("① 巡检报出两个代理（不是只看到主入口那个）",
                pl.IndexOf("2 个代理并存") >= 0, pl);
            Chk("① 两个名字都点出来：version.dll 与 winmm.dll",
                pl.IndexOf("version.dll") >= 0 && pl.IndexOf("winmm.dll") >= 0, pl);
            Chk("① 遗留那代按实际体积报成 0.2.x（旧版），不当成在跑的 0.3.5",
                pl.IndexOf("旧版") >= 0 || pl.IndexOf("0.2.x") >= 0, pl);
            Chk("① 告警行给出可点的动作（不是只喊有问题）",
                pl.IndexOf("停放遗留项") >= 0 && pl.IndexOf("⚠") >= 0, pl);
            Chk("① 巡检本身是只读的：两个文件都还在原地",
                File.Exists(Path.Combine(Jp5, "version.dll")) && File.Exists(Path.Combine(Jp5, "winmm.dll")), "");
            Chk("① InspectEntries 按体积认出了两代各自的代号",
                Plan.InspectEntries(Jp5).Count == 2
                && Plan.InspectEntries(Jp5)[0].Owner == Plan.D030
                && Plan.InspectEntries(Jp5)[1].Owner == Plan.Legacy,
                Plan.InspectEntries(Jp5).Count + " 项");
            Chk("① 主入口标出来（当前生效的是哪一个）",
                Plan.InspectEntries(Jp5).Exists(delegate(Plan.EntryHit h) { return h.Main; }), "");

            string pr = Plan.ParkForeignReport(Jp5);
            L("    " + pr.Replace("\n", " | "));
            Chk("② 停放后目录里只剩 version.dll 与 winmm.dll.parked.legacy",
                File.Exists(Path.Combine(Jp5, "version.dll"))
                && !File.Exists(Path.Combine(Jp5, "winmm.dll"))
                && File.Exists(Path.Combine(Jp5, "winmm.dll.parked.legacy")), pr);
            Chk("② Plan.Current 仍是 030（没把真正在生效的入口停掉）",
                Plan.Current(Jp5) == Plan.D030, Plan.Current(Jp5));
            Chk("② 停放后巡检不再告警", Plan.PatrolLine(Jp5).IndexOf("⚠") < 0, Plan.PatrolLine(Jp5));
            Chk("② 已停放件被计入（看得见历史，但不算并存）",
                Plan.PatrolLine(Jp5).IndexOf("另有 1 项已停放") >= 0, Plan.PatrolLine(Jp5));
            Chk("② 再点一次是幂等的（没有遗留项可停）",
                Plan.ParkForeignReport(Jp5).IndexOf("没有需要停放的遗留代理") >= 0, Plan.ParkForeignReport(Jp5));
            Chk("② 0.3.x 的 ini 没被一起停掉（它是当前方案的组件）",
                File.Exists(Path.Combine(Jp5, "dlssg_sm86.ini")), "");

            // ③ 游戏自带同名文件绝不进候选：体积不在任何窗口的 winmm.dll
            string Jp6 = Path.Combine(tmp, "native");
            Directory.CreateDirectory(Jp6);
            Tiny(Path.Combine(Jp6, "game.exe"));
            Exact(Path.Combine(Jp6, "version.dll"), 30021920);
            Exact(Path.Combine(Jp6, "winmm.dll"), 1868736);               // CP2077 自带 dbghelp 量级，不是代理
            Chk("③ 游戏自带同名 dll 不被认成代理（体积不在窗口内）",
                Plan.InspectEntries(Jp6).Count == 1
                && Plan.InspectEntries(Jp6)[0].Name == "version.dll",
                Plan.InspectEntries(Jp6).Count + " 项");
            Chk("③ 单套在位 → 巡检不告警", Plan.PatrolLine(Jp6).IndexOf("⚠") < 0, Plan.PatrolLine(Jp6));
            Plan.ParkForeignReport(Jp6);
            Chk("③ 停放操作也没碰游戏自带文件", File.Exists(Path.Combine(Jp6, "winmm.dll")), "");

            // ④ 游戏里那份比资源包旧（换了包没更新部署）—— 也要被巡检抓到
            string Jp7 = Path.Combine(tmp, "stale");
            Directory.CreateDirectory(Jp7);
            Tiny(Path.Combine(Jp7, "game.exe"));
            Exact(Path.Combine(Jp7, "version.dll"), 29975840);            // 0.3.2，资源包已是 0.3.5
            Tiny(Path.Combine(Jp7, "dlssg_sm86.ini"));
            string sl = Plan.PatrolLine(Jp7);
            Chk("④ 只有一套、但版本比工具旧 → 仍然告警，并指向「更新部署」",
                sl.IndexOf("⚠") >= 0 && sl.IndexOf("0.3.2") >= 0 && sl.IndexOf("更新部署") >= 0, sl);
            Chk("④ 这套不会被「停放遗留项」误停（它是当前方案）",
                Plan.ParkForeignReport(Jp7).IndexOf("没有需要停放的遗留代理") >= 0
                && File.Exists(Path.Combine(Jp7, "version.dll")), Plan.ParkForeignReport(Jp7));

            // ⑤ 原生状态（全停放）时 ParkForeign 不动任何东西
            string Jp8 = Path.Combine(tmp, "native-none");
            Directory.CreateDirectory(Jp8);
            Tiny(Path.Combine(Jp8, "game.exe"));
            Exact(Path.Combine(Jp8, "winmm.dll"), 15678272);
            File.Move(Path.Combine(Jp8, "winmm.dll"), Path.Combine(Jp8, "winmm.dll.parked.legacy"));
            Chk("⑤ Current=none 时巡检说清是原生", Plan.PatrolLine(Jp8).IndexOf("没有代理在位") >= 0, Plan.PatrolLine(Jp8));
            Chk("⑤ 原生状态下 ParkForeign 一个都不动",
                Plan.ParkForeign(Jp8, Plan.Current(Jp8), new List<string>()).Count == 0
                && File.Exists(Path.Combine(Jp8, "winmm.dll.parked.legacy")), "");

            L("");

            // ---------- 14. 体积 + 签名双判（P1-5） ----------
            //  OwnerOfCore 是纯函数：真签名文件造不出"窗口内但错签"那一格
            //  （给签名 DLL 追加 0 撑体积会让证书目录读不到，实测 CryptographicException），
            //  所以判据矩阵逐格喂，比拿文件测更严。
            L("");
            L("=== 14. OwnerOfCore：体积窗口 + 签名指纹双判（P1-5） ===");
            // ① 五档体积各自的归属（不带签名信息 = 旧行为，必须一字不变）
            Chk("① 旧版 15,667,520 → legacy",
                Plan.OwnerOfCore(15667520L, "") == Plan.Legacy, Plan.OwnerOfCore(15667520L, ""));
            Chk("① 0.3.0 17,529,120 → 030（阈值写 17 MB 就会掉进 legacy）",
                Plan.OwnerOfCore(17529120L, "") == Plan.D030, Plan.OwnerOfCore(17529120L, ""));
            Chk("① 0.3.2 29,975,840 → 030", Plan.OwnerOfCore(29975840L, "") == Plan.D030, "");
            Chk("① 0.3.5 30,021,920 → 030", Plan.OwnerOfCore(30021920L, "") == Plan.D030, "");
            Chk("① OptiScaler d3d12 26,116,608 → opti（夹在两个窗口中间，没串档）",
                Plan.OwnerOfCore(26116608L, "") == Plan.Opti, Plan.OwnerOfCore(26116608L, ""));
            Chk("① OptiScaler dxgi 25,656,288 → opti",
                Plan.OwnerOfCore(25656288L, "") == Plan.Opti, "");
            Chk("① 五档互不串档（各归各的）",
                Plan.OwnerOfCore(15667520L, "") != Plan.OwnerOfCore(17529120L, "")
                && Plan.OwnerOfCore(26116608L, "") != Plan.OwnerOfCore(29975840L, ""), "");
            // ② 窗口内但错签 —— 这一格正是 1.9 MB 余量带来的风险
            Chk("② 窗口内但非白名单签名 → 不判为本方案",
                Plan.OwnerOfCore(30021920L, "other") == "", Plan.OwnerOfCore(30021920L, "other"));
            Chk("② 窗口内但是 NVIDIA 官方签名 → 不判为本方案",
                Plan.OwnerOfCore(30021920L, "nvidia") == "", Plan.OwnerOfCore(30021920L, "nvidia"));
            Chk("② 错签也不判成方案 A（宁可说「不是代理」，也不误停别人签的文件）",
                Plan.OwnerOfCore(26116608L, "other") == "", Plan.OwnerOfCore(26116608L, "other"));
            // ③ 签名是正向证据：体积跑出窗口也认得是本方案（上游改一次构建就会这样）
            Chk("③ 作者签名 + 窗口外(36 MB) → 仍判 030",
                Plan.OwnerOfCore(36000000L, "upstream") == Plan.D030, Plan.OwnerOfCore(36000000L, "upstream"));
            Chk("③ 作者签名 + 两窗口之间(22 MB) → 仍判 030",
                Plan.OwnerOfCore(22000000L, "upstream") == Plan.D030, "");
            Chk("③ 作者签名也救不了 LooksProxy 的两端（1 MB / 500 MB 不是代理）",
                Plan.OwnerOfCore(1048576L, "upstream") == ""
                && Plan.OwnerOfCore(500L * 1024 * 1024, "upstream") == "", "");
            Chk("③ 签名读不到（missing）不否决，退回体积判据",
                Plan.OwnerOfCore(30021920L, "missing") == Plan.D030, "");
            // ④ 真签名文件回归（只读；换机器找不到就跳过，不拿它凑数）
            string realUp = @"C:\ProgramData\Fluxion\dlssg030-pack\common\version.dll";
            string realNv = @"C:\ProgramData\Fluxion\dlssg030-pack\common\nvngx_dlss.dll";
            if (!File.Exists(realUp)) L("  [SKIP] 找不到真实上游签名代理，跳过真文件回归：" + realUp);
            else
            {
                Chk("④ 真代理：签名是 upstream 且 OwnerOf=030",
                    ProxySign.Of(realUp).Verdict == "upstream"
                    && Plan.OwnerOf(realUp) == Plan.D030,
                    ProxySign.Of(realUp).Verdict + " / " + Plan.OwnerOf(realUp));
                Chk("④ 同一文件取两次签名结论走缓存（同一个对象，不再开文件）",
                    ReferenceEquals(ProxySign.Of(realUp), ProxySign.Of(realUp)), "");
            }
            if (!File.Exists(realNv)) L("  [SKIP] 找不到 NVIDIA 签名的运行库，跳过否决回归：" + realNv);
            else
            {
                //  59 MB 的 NVIDIA 签名运行库：旧判据会说成"方案 A"（大于 20 MB），
                //  双判之后必须说"不是代理" —— 它压根不是任何一套方案的入口。
                Chk("④ NVIDIA 签名的运行库不再被误判成方案 A",
                    ProxySign.Of(realNv).Verdict == "nvidia" && Plan.OwnerOf(realNv) == "",
                    ProxySign.Of(realNv).Verdict + " / '" + Plan.OwnerOf(realNv) + "'");
            }
            // ⑤ 判据矩阵的边界：窗口上下沿本身是开区间，别把边界值判错
            //  开区间不等于"不是代理"：legacy 是兜底档 —— 过了 LooksProxy 的 14 MB 下限、
            //  既不在 0.3.x 窗口又不大于方案A下沿的，都算旧版 0.2.x（2026-09-16 定下的形状）。
            Chk("⑤ 粗窗口两端是开区间：16 MB / 20 MB 都不算 0.3.x，落到 legacy 兜底档",
                Plan.OwnerOfCore(16L * 1024 * 1024, "") == Plan.Legacy
                && Plan.OwnerOfCore(20L * 1024 * 1024, "") == Plan.Legacy
                && Plan.OwnerOfCore(17529120L, "") == Plan.D030,
                Plan.OwnerOfCore(16L * 1024 * 1024, "") + " / " + Plan.OwnerOfCore(20L * 1024 * 1024, ""));
            Chk("⑤ 真正的「不是代理」边界是 LooksProxy 的 14 MB 下沿",
                Plan.OwnerOfCore(13900000L, "") == "" && Plan.OwnerOfCore(14L * 1024 * 1024, "") != "",
                Plan.OwnerOfCore(13900000L, "") + " / " + Plan.OwnerOfCore(14L * 1024 * 1024, ""));

            try { Directory.Delete(tmp, true); L("沙盒已清理"); } catch (Exception ex) { L("沙盒清理失败：" + ex.Message); }
            L("");
            L(Fail == 0 ? "===== ALL PASS =====" : ("===== FAIL " + Fail + " ====="));
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "plan_probe.txt"), Out.ToString(), new UTF8Encoding(false)); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
