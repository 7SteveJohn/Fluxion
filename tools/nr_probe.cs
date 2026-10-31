// 3.3.0 回归：NR 开关 / 方案A 动态正文 / 切换后补齐配套文件（ini）
// 全程在 %TEMP% 沙盒里跑，绝不碰真实游戏目录与真实配置。
namespace Fluxion
{
    using System;
    using System.IO;
    using System.Text;

    static class NrProbe
    {
        static int Fail = 0;
        static StringBuilder Out = new StringBuilder();
        static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        static void Chk(string what, bool ok, string extra)
        {
            Out.AppendLine((ok ? "  [PASS] " : "  [FAIL] ") + what
                + (extra == null || extra.Length == 0 ? "" : "   -> " + extra));
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what
                + (extra == null || extra.Length == 0 ? "" : "   -> " + extra));
            if (!ok) Fail++;
        }
        static void Big(string p, int mb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
            {
                byte[] b = new byte[65536];
                long left = (long)mb * 1024 * 1024;
                while (left > 0) { int n = (int)Math.Min(left, b.Length); fs.Write(b, 0, n); left -= n; }
            }
        }
        static void Tiny(string p, string s) { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, s ?? "x"); }
        static int Skip = 0;
        //  环境依赖的断言走这里：显式说"为什么没测"，而不是悄悄留一坨红。
        static void Skipped(string what, string why)
        {
            Skip++;
            L("  [SKIP] " + what + "   —— " + why);
        }

        static int Main()
        {
            try { Program.Cfg = Config.Load(Program.ConfigPath); } catch { }
            L("=== 3.3.0 回归（NR 开关 / 方案A 正文 / 切换补配套文件）===");
            L("");

            string tmp = Path.Combine(Path.GetTempPath(), "gb_nr_test");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            Directory.CreateDirectory(tmp);

            // ---------- 1. 真实环境：鸣潮的 NR 现在必须是"关" ----------
            L("=== 1. 真实环境：鸣潮 NR 当前状态 ===");
            string W = @"G:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64";
            int nr = XeMfg.NrEnabled(W);
            L("  XeMfg.NrEnabled(鸣潮) = " + nr + "  (0=关 / 1=开 / -1=读不到)");
            //  -1 = 该目录现在没有 OptiScaler 的 ini（方案被换掉/没部署）—— 这是**环境**，不是缺陷。
            if (nr < 0) Skipped("鸣潮 NR 状态（目录里没有 OptiScaler.ini，无法判断）", "nr=-1");
            else Chk("鸣潮 NR 已关闭", nr == 0, "nr=" + nr);
            Chk("OptiNote(绝区零) 不再写死鸣潮", Dlssg.OptiNote(null).IndexOf("鸣潮") < 0, "");
            L("");

            // ---------- 2. NR 开关：沙盒往返 ----------
            L("=== 2. NR 开关：沙盒读改写往返 ===");
            string w = Path.Combine(tmp, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Tiny(Path.Combine(w, "Client-Win64-Shipping.exe"), "stub");
            Tiny(Path.Combine(w, "OptiScaler.ini"),
                 "[Upscalers]\r\nDx12Upscaler=dlss\r\n\r\n[Spoofing]\r\nDxgi=true\r\nSpoofedGPUName=NVIDIA GeForce RTX 5090\r\n\r\n" +
                 "[DlssNr]\r\nPrecision=0\r\nEnabled=auto\r\nRunBeforeSR=false\r\nFinishedPicture=true\r\nPasses=1\r\nTransferStrength=1.0\r\n\r\n[Log]\r\nLogToFile=true\r\n");
            L("  Detect(沙盒) = " + XeMfg.Detect(w));
            Chk("沙盒被认作鸣潮", XeMfg.Detect(w) == "wuwa", XeMfg.Detect(w));
            Chk("初始 Enabled=auto 读作 0（不主动开）", XeMfg.NrEnabled(w) == 0, "nr=" + XeMfg.NrEnabled(w));
            L("  开启: " + XeMfg.SetNrEnabled(w, true));
            Chk("开启后读作 1", XeMfg.NrEnabled(w) == 1, "nr=" + XeMfg.NrEnabled(w));
            L("  关闭: " + XeMfg.SetNrEnabled(w, false));
            Chk("关闭后读作 0", XeMfg.NrEnabled(w) == 0, "nr=" + XeMfg.NrEnabled(w));
            string after = File.ReadAllText(Path.Combine(w, "OptiScaler.ini"));
            Chk("只改了 Enabled 一行（其余键一个不丢）",
                after.Contains("Dxgi=true") && after.Contains("SpoofedGPUName=NVIDIA GeForce RTX 5090")
                && after.Contains("Passes=1") && after.Contains("TransferStrength=1.0")
                && after.Contains("FinishedPicture=true") && after.Contains("RunBeforeSR=false")
                && after.Contains("LogToFile=true"), "");
            Chk("没有把 auto 之外的值写坏", after.Contains("Enabled=false") && !after.Contains("Enabled=true"), "");
            Chk("文件里只有一个 [DlssNr]", after.Split(new string[] { "[DlssNr]" }, StringSplitOptions.None).Length == 2, "");
            L("");

            // ---------- 3. 用户场景复刻：切到 0.3.x 之后 ini 必须被补回来 ----------
            L("=== 3. 复刻事故：入口还在（被停放）+ ini 被删 -> 切换后 ini 必须回来 ===");
            string z = Path.Combine(tmp, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            // 3a) ini 缺失时 EnsureFactoryIni 要能补（旧实现是 `if (!File.Exists) return` 直接放弃）
            string noini = Path.Combine(tmp, "noini");
            Directory.CreateDirectory(noini);
            Chk("前置：目录里确实没有 ini", !File.Exists(Path.Combine(noini, "dlssg_sm86.ini")), "");
            Dlssg030.EnsureFactoryIni(noini);
            string iniPath = Path.Combine(noini, "dlssg_sm86.ini");
            Chk("EnsureFactoryIni 把缺失的 ini 补出来了", File.Exists(iniPath), File.Exists(iniPath) ? new FileInfo(iniPath).Length.ToString() + " B" : "缺失");
            string iniTxt = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
            Chk("补出来的是 0.3.x 出厂格式（含 [General] / Enabled=1）",
                iniTxt.Contains("[General]") && iniTxt.Contains("Enabled=1"), "");

            // 3b) 真实切换路径：方案A 在用 + 没有 ini -> 切到 0.3.x 后 ini 必须在
            string real = Path.Combine(tmp, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            try { File.Delete(Path.Combine(real, "dlssg_sm86.ini")); } catch { }
            // 摆一个"方案A 在用"的入口（26 MB > 20 MB 判据）
            Big(Path.Combine(real, "dxgi.dll"), 26);
            Tiny(Path.Combine(real, "fakenvapi.dll"), "x");
            L("  切换前 Current = " + Plan.Current(real));
            Chk("前置：切换前是方案A", Plan.Current(real) == Plan.Opti, Plan.Current(real));
            string r = Plan.SwitchTo(real, Plan.D030);
            L("  SwitchTo 回执: " + r.Replace("\n", " | "));
            L("  切换后 Current = " + Plan.Current(real));
            Chk("切换后是 0.3.x", Plan.Current(real) == Plan.D030, Plan.Current(real));
            Chk("★ 切换后 dlssg_sm86.ini 存在（这就是绝区零失去帧生成选项的那个洞）",
                File.Exists(Path.Combine(real, "dlssg_sm86.ini")),
                File.Exists(Path.Combine(real, "dlssg_sm86.ini")) ? new FileInfo(Path.Combine(real, "dlssg_sm86.ini")).Length + " B" : "缺失");
            //  原来这里写死的是 0.3.0 时代的体积窗口（16–20 MB）。换包到 0.3.5 之后落盘是
            //  30,021,920 B —— 判据已经在 catalog 的档表里，断言就别再抄一份体积。
            long dxlen = new FileInfo(Path.Combine(real, "dxgi.dll")).Length;
            Chk("入口 d3d12/dxgi 槽位上确实是本方案的 0.3.x 代理（按档表判，不写死体积）",
                Dlssg030.IsProxySize(dxlen) && Dlssg030.CodeOf(dxlen) == Dlssg030.Ver,
                dxlen + " → " + Dlssg030.CodeOf(dxlen));
            Chk("一个文件都没删（停放件仍在）", File.Exists(Path.Combine(real, "dxgi.dll.parked.opti")), "");
            L("");

            // ---------- 4. 真实环境：绝区零的 ini 已回来了吗 ----------
            L("=== 4. 真实环境：绝区零 ===");
            string Z = @"G:\miHoYo Launcher\games\ZenlessZoneZero Game";
            string zini = Path.Combine(Z, "dlssg_sm86.ini");
            string zdll = Path.Combine(Z, "d3d12.dll");
            //  这三条断言的是"**这台机器上**绝区零装没装 0.3.x"（2026-09-21 实况：没装，
            //  d3d12.dll 是别家方案的 25,656,288 B）。装着时才走断言，没装就显式跳过 ——
            //  拿探针测环境，红了既不是代码错也不是用户错，谁也看不出什么。
            bool zDep = File.Exists(zini) && File.Exists(zdll)
                      && Dlssg030.IsProxySize(new FileInfo(zdll).Length);
            string zWhy = !Directory.Exists(Z) ? "找不到绝区零目录"
                      : (!File.Exists(zini) ? "该目录没有 dlssg_sm86.ini（绝区零当前未部署 0.3.x）"
                                            : "d3d12.dll 不在本方案体积档里（这台机器上它走的是别家方案）");
            if (!zDep)
            {
                Skipped("绝区零 dlssg_sm86.ini 已存在", zWhy);
                Skipped("绝区零 ini 是 0.3.x 出厂格式（含 [General]）", zWhy);
                Skipped("绝区零 d3d12.dll 是本方案的签名代理（按档表判）", zWhy);
            }
            else
            {
                Chk("绝区零 dlssg_sm86.ini 已存在", true, new FileInfo(zini).Length + " B");
                string zt = File.ReadAllText(zini);
                Chk("绝区零 ini 是 0.3.x 出厂格式（含 [General]）", zt.Contains("[General]"), "");
                long zl = new FileInfo(zdll).Length;
                Chk("绝区零 d3d12.dll 是本方案的签名代理（按档表判，不写死体积）",
                    Dlssg030.IsProxySize(zl) && Dlssg030.CodeOf(zl).Length > 0,
                    zl + " → " + Dlssg030.CodeOf(zl));
            }
            L("");

            // ---------- 5. 方案A 正文按游戏变 ----------
            L("=== 5. OptiNote 按游戏给不同的话 ===");
            string n0 = Dlssg.OptiNote(null);
            string nz = Dlssg.OptiNote(new DlssgGame { Dir = Z, Exe = "ZenlessZoneZero.exe", Title = "绝区零" });
            //  不能用上面那个 real：第 3b 节已经把同一个目录从方案 A 切到 0.3.x，
            //  OptiScaler.ini 被停放、NR 读成"未知"，正文自然不带 NR 状态 —— 那是探针自己
            //  蹭了别人的残局，不是产品的错。这一节自造一份"方案 A 在用 + ini 在位"的目录。
            string wa = Path.Combine(tmp, "WuwaNote");
            Directory.CreateDirectory(wa);
            Tiny(Path.Combine(wa, "Client-Win64-Shipping.exe"), "MZ");
            Big(Path.Combine(wa, "dxgi.dll"), 26);
            Tiny(Path.Combine(wa, "OptiScaler.ini"), "[Dxgi]\nDxgi=true\n[DlssNr]\nEnabled=true\n");
            Chk("前置：新沙盒被认作鸣潮且方案 A 在位",
                XeMfg.Detect(wa) == "wuwa" && Plan.Current(wa) == Plan.Opti,
                XeMfg.Detect(wa) + " / " + Plan.Current(wa));
            string nw = Dlssg.OptiNote(new DlssgGame { Dir = wa, Exe = "Client-Win64-Shipping.exe", Title = "鸣潮" });
            L("  未选中: " + n0.Replace("\n", " / "));
            L("  绝区零: " + nz.Replace("\n", " / "));
            L("  鸣潮  : " + nw.Replace("\n", " / "));
            //  ⚠ 2026-10-26 按当前契约重写：这几条原来钉的是**具体文案**（"游戏库""不必注入"
            //    "本方案在用"），而文案每次改版都会红一次 —— 追着文案改断言等于给 UI 上锁。
            //    现在只断言"这条路上必须成立的事"。
            Chk("未选中时仍给一句可读的通用说明（不空、不崩）", n0.Length > 0, n0.Replace("\n", " / "));
            //  正文 2026-09-20 改过话术：绝区零 3.0+ 的 DX12 模式自带帧生成，所以从
            //  "不用本方案"改成"多数情况不必注入 —— 只有要跑 DLSS 帧生成才用本方案"。
            //  要测的还是那两件事：先劝住注入，再说清什么时候才真需要本方案。
            //  绝区零：核心是"劝住注入" —— 现在的话术是「只跑 DLSS 帧生成才用它」，
            //  断言落在"才用"这个转折上（换成别的措辞只要还保留这个转折就仍然成立）。
            Chk("绝区零 -> 仍先劝住：只有跑 DLSS 帧生成才用本方案",
                nz.IndexOf("才用") >= 0 && nz.IndexOf("DLSS") >= 0, nz.Replace("\n", " / "));
            //  鸣潮：`OptiNote` 现在只返回 NR 开着的警告（理由已并进「此游戏的适配方案」那一行），
            //  没开 NR 时就是空串。所以断言 = 「空，或者只有 NR 警告」，不再要求"说本方案在用"。
            Chk("鸣潮 -> 只说 NR 警告（没开 NR 时为空，不重复讲推荐理由）",
                nw.Length == 0 || nw.IndexOf("神经渲染") >= 0, "[" + nw.Replace("\n", " / ") + "]");
            Chk("鸣潮文案带上 NR 状态", nw.IndexOf("神经渲染") >= 0, "");
            L("");

            try { Directory.Delete(tmp, true); L("沙盒已清理"); } catch (Exception ex) { L("沙盒清理失败：" + ex.Message); }

            L("");
            L(Fail == 0 ? ("ALL PASS" + (Skip > 0 ? "（另有 " + Skip + " 项因环境依赖跳过）" : ""))
              : (Fail + " 项失败" + (Skip > 0 ? "；另有 " + Skip + " 项因环境依赖跳过" : "")));
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "nr_probe.txt"), Out.ToString(), new UTF8Encoding(false)); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
