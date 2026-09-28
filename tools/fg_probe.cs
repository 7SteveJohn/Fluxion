// ============================================================================
//  fg_probe —— 「帧生成生效自检」(FgVerify) 的回归验证
//  ---------------------------------------------------------------------------
//  验证目标：读代理 jsonl 能不能**准确**回答"实际跑到几倍"。
//  沙盒造四种形态（都按真实日志格式）：
//    A 有 evaluate      → 读出实际倍率（generated_count 的最大值 + 1）
//    B 只有 configuration → 读出上限倍率
//    C 0 字节日志        → 判为"代理已加载但未见调用"
//    D configuration_error → 把代理报的配置错误转成中文说明
//  外加：多份日志取"最近一份有内容的"、空目录返回无日志、真实游戏目录只读核对。
//
//  用法：python tools\run_fg_probe.py
// ============================================================================
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    static class FgProbe
    {
        static int Fail = 0;
        static string Tmp;
        static readonly StringBuilder SB = new StringBuilder();

        static void L(string s) { Console.WriteLine(s); SB.AppendLine(s); }
        static void Chk(string what, bool ok, string extra)
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(extra) ? "" : "   -> " + extra));
            if (!ok) Fail++;
        }

        static string MkGame(string name)
        {
            string d = Path.Combine(Tmp, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, name + ".exe"), "stub");
            return d;
        }

        // 目录：<game>\dlssg_sm86\logs\<file>（与真实代理一致）
        static void WriteLog(string gameDir, string file, params string[] lines)
        {
            string d = Path.Combine(gameDir, "dlssg_sm86", "logs");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, file), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
        static void TouchEmpty(string gameDir, string file)
        {
            string d = Path.Combine(gameDir, "dlssg_sm86", "logs");
            Directory.CreateDirectory(d);
            using (FileStream fs = File.Create(Path.Combine(d, file))) { }
        }

        // 真实日志里的两行（照抄 2077 的 native_*.jsonl）
        const string CFG = "{\"cuda_buffer_clear\":true,\"disabled_fusions\":0,\"event\":\"configuration\"," +
                           "\"hardware_bilinear\":false,\"image_patches\":true,\"kernel_image\":\"ptx\"," +
                           "\"max_generated\":3,\"optimized\":true,\"pid\":7728,\"router\":86," +
                           "\"runtime\":\"native_pipeline\",\"self_contained\":true}";
        static string Eval(int gen)
        {
            return "{\"evaluations\":1,\"event\":\"evaluate\",\"generated_count\":" + gen +
                   ",\"generated_index\":" + gen + ",\"handle\":1,\"pid\":7728}";
        }

        static int Main()
        {
            L("=== 帧生成生效自检（FgVerify）验证 ===");
            Tmp = Path.Combine(Path.GetTempPath(), "gb_fg_test");
            try { if (Directory.Exists(Tmp)) Directory.Delete(Tmp, true); } catch { }
            Directory.CreateDirectory(Tmp);
            L("  沙盒 = " + Tmp);
            L("");

            // ---------- A：有 evaluate → 实际倍率 ----------
            L("=== 1. 有 evaluate：应读出实际倍率 ===");
            string A = MkGame("GameA");
            WriteLog(A, "native_100.jsonl", CFG,
                     "{\"event\":\"ngx_driver_connected\",\"feature_id\":11,\"pid\":100}",
                     Eval(1), Eval(2), Eval(3));
            FgResult ra = FgVerify.Check(A);
            ra.Game = "GameA";
            L("   MaxGen=" + ra.MaxGen + "  GenCount=" + ra.GenCount + "  file=" + ra.File + "  bytes=" + ra.Bytes);
            Chk("读到 configuration 的 max_generated = 3", ra.MaxGen == 3, "" + ra.MaxGen);
            Chk("读到 evaluate 的最大 generated_count = 3", ra.GenCount == 3, "" + ra.GenCount);
            Chk("HasLog = true", ra.HasLog, "");
            var gl = new List<DlssgGame>();
            gl.Add(new DlssgGame { Title = "GameA", Dir = A });
            bool eff;
            string sumA = FgVerify.Summary(gl, out eff);
            L("   Summary = " + sumA);
            Chk("Summary 判为\"有效\"", eff, "");
            Chk("Summary 含 实际 4X（3+1）", sumA.Contains("实际 4X"), sumA);
            Chk("Summary 带游戏名", sumA.Contains("GameA"), sumA);
            L("");

            // ---------- B：只有 configuration → 上限 ----------
            L("=== 2. 只有 configuration：应读出上限、且不判为有效 ===");
            string B = MkGame("GameB");
            WriteLog(B, "backend_200.jsonl", CFG);
            FgResult rb = FgVerify.Check(B);
            L("   MaxGen=" + rb.MaxGen + "  GenCount=" + rb.GenCount);
            Chk("MaxGen = 3", rb.MaxGen == 3, "" + rb.MaxGen);
            Chk("GenCount 仍为 -1（没读到 evaluate）", rb.GenCount == -1, "" + rb.GenCount);
            var gl2 = new List<DlssgGame>();
            gl2.Add(new DlssgGame { Title = "GameB", Dir = B });
            string sumB = FgVerify.Summary(gl2, out eff);
            L("   Summary = " + sumB);
            Chk("不判为有效（没真的跑）", !eff, "");
            Chk("Summary 含 上限 4X", sumB.Contains("上限 4X"), sumB);
            L("");

            // ---------- C：0 字节 ----------
            L("=== 3. 0 字节日志：应判为\"已加载但未见调用\" ===");
            string C = MkGame("GameC");
            TouchEmpty(C, "backend_300.jsonl");
            FgResult rc = FgVerify.Check(C);
            L("   HasLog=" + rc.HasLog + "  bytes=" + rc.Bytes + "  file=" + rc.File);
            Chk("0 字节也算\"有日志\"（证明代理被加载过）", rc.HasLog, "");
            Chk("字节数 = 0", rc.Bytes == 0, "" + rc.Bytes);
            var gl3 = new List<DlssgGame>();
            gl3.Add(new DlssgGame { Title = "GameC", Dir = C });
            string sumC = FgVerify.Summary(gl3, out eff);
            L("   Summary = " + sumC);
            Chk("不判为有效", !eff, "");
            Chk("Summary 说明\"未见帧生成调用\"", sumC.Contains("未见帧生成调用"), sumC);
            L("");

            // ---------- D：配置错误 ----------
            L("=== 4. configuration_error：应转成中文说明 ===");
            string D = MkGame("GameD");
            WriteLog(D, "native_400.jsonl",
                "{\"event\":\"configuration_error\",\"message\":\"Invalid INI integer\",\"pid\":400}");
            FgResult rd = FgVerify.Check(D);
            L("   Note = " + rd.Note);
            Chk("识别出配置被拒", rd.Note.Length > 0, rd.Note);
            Chk("说明里点出 INI 问题", rd.Note.Contains("INI"), rd.Note);
            Chk("未误读出倍率", rd.GenCount == -1 && rd.MaxGen == -1, "");
            L("");

            // ---------- E：多份日志 → 以"最新那次活动"为准，并带出上次成功 ----------
            L("=== 5. 同目录多份日志：以最新那次为准 + 附上上次成功 ===");
            string E = MkGame("GameE");
            WriteLog(E, "native_500.jsonl", CFG, Eval(1));                 // 旧、有内容（成功 2X）
            System.Threading.Thread.Sleep(1100);
            TouchEmpty(E, "backend_600.jsonl");                            // 新、空（换代理后启动过）
            FgResult re = FgVerify.Check(E);
            L("   file=" + re.File + "  bytes=" + re.Bytes
              + "  HasPrev=" + re.HasPrev + "  PrevGen=" + re.PrevGenCount);
            Chk("取到的是最新那份（backend_600.jsonl）", re.File == "backend_600.jsonl", re.File);
            Chk("字节数 = 0（判定为\"加载了没跑\"）", re.Bytes == 0, "" + re.Bytes);
            Chk("带出上一份有内容的记录（PrevGenCount=1）", re.HasPrev && re.PrevGenCount == 1,
                "HasPrev=" + re.HasPrev + " prevGen=" + re.PrevGenCount);
            var glE = new List<DlssgGame>();
            glE.Add(new DlssgGame { Title = "GameE", Dir = E });
            string sumE = FgVerify.Summary(glE, out eff);
            L("   Summary = " + sumE);
            Chk("不判为有效（最近一次没跑到）", !eff, "");
            Chk("说明\"未见帧生成调用\"", sumE.Contains("未见帧生成调用"), sumE);
            Chk("并附上\"上次成功 … 实际 2X\"", sumE.Contains("上次成功") && sumE.Contains("实际 2X"), sumE);
            L("");

            // ---------- E2：最新那份有内容 → 直接报它（2077 的历史形态）----------
            L("=== 5b. 最新那份有内容时：直接报它，不受更早文件影响 ===");
            string E2 = MkGame("GameE2");
            WriteLog(E2, "native_700.jsonl", CFG, Eval(1));
            System.Threading.Thread.Sleep(1100);
            WriteLog(E2, "native_800.jsonl", CFG, Eval(3));                // 更新、更成功
            FgResult re2 = FgVerify.Check(E2);
            L("   file=" + re2.File + "  GenCount=" + re2.GenCount);
            Chk("取到最新那份 native_800.jsonl", re2.File == "native_800.jsonl", re2.File);
            Chk("读出实际 3 → 4X", re2.GenCount == 3, "" + re2.GenCount);
            var glE2 = new List<DlssgGame>();
            glE2.Add(new DlssgGame { Title = "GameE2", Dir = E2 });
            string sumE2 = FgVerify.Summary(glE2, out eff);
            L("   Summary = " + sumE2);
            Chk("判为有效并显示\"实际 4X\"", eff && sumE2.Contains("实际 4X"), sumE2);
            L("");

            // ---------- E3：跨游戏时必须说"最近成功"而不是"上次成功" ----------
            L("=== 5c. 最近活动与成功记录分属不同游戏：不能拼成同一款的历史 ===");
            string P = MkGame("GameP");
            WriteLog(P, "native_900.jsonl", CFG, Eval(3));                 // 成功 4X（较早）
            System.Threading.Thread.Sleep(1100);
            string Q = MkGame("GameQ");
            TouchEmpty(Q, "backend_950.jsonl");                            // 更晚、空
            var glQ = new List<DlssgGame>();
            glQ.Add(new DlssgGame { Title = "GameP", Dir = P });
            glQ.Add(new DlssgGame { Title = "GameQ", Dir = Q });
            string sumQ = FgVerify.Summary(glQ, out eff);
            L("   Summary = " + sumQ);
            Chk("以更晚的 GameQ 作为\"最近一次\"", sumQ.Contains("GameQ"), sumQ);
            Chk("不含\"上次成功\"（那会读成同一款的历史）", !sumQ.Contains("上次成功"), sumQ);
            Chk("改用\"最近成功\"并带上 GameP", sumQ.Contains("最近成功") && sumQ.Contains("GameP"), sumQ);
            L("");

            // ---------- F：空目录 ----------
            L("=== 6. 没有任何日志：HasLog = false ===");
            string F = MkGame("GameF");
            FgResult rf = FgVerify.Check(F);
            Chk("HasLog = false", !rf.HasLog, "");
            var gl6 = new List<DlssgGame>();
            gl6.Add(new DlssgGame { Title = "GameF", Dir = F });
            string sumF = FgVerify.Summary(gl6, out eff);
            L("   Summary = " + sumF);
            Chk("提示\"尚无代理日志\"", sumF.Contains("尚无代理日志"), sumF);
            L("");

            // ---------- G：真实环境只读核对 ----------
            L("=== 7. 真实游戏目录只读核对（不断言具体值，只报告）===");
            string[] real = new string[] {
                @"G:\miHoYo Launcher\games\ZenlessZoneZero Game",
                @"G:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64",
                @"G:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64",
                @"G:\SteamLibrary\steamapps\common\ForzaHorizon6",
            };
            foreach (string g in real)
            {
                if (!Directory.Exists(g)) { L("   (跳过，目录不存在) " + g); continue; }
                FgResult r = FgVerify.Check(g);
                string nm = Path.GetFileName(g.TrimEnd('\\'));
                L(string.Format("   {0,-24} file={1,-24} bytes={2,-8} gen={3,-3} empty={4,-5} 上次成功={5}",
                    nm, r.File, r.Bytes, r.GenCount, r.IsEmpty,
                    (r.HasPrev && r.PrevGenCount >= 0) ? (r.PrevGenCount + 1) + "X" : "-"));
            }
            L("");

            try { Directory.Delete(Tmp, true); L("沙盒已清理"); }
            catch (Exception ex) { L("沙盒清理失败：" + ex.Message); }

            L("");
            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));

            // 控制台是 GBK，中文会花屏 —— 同时落一份 UTF-8 供阅读
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "fg_probe.txt"),
                    SB.ToString(), new UTF8Encoding(false));
            }
            catch { }

            return Fail == 0 ? 0 : 1;
        }
    }
}
