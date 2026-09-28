using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Fluxion;

// v2.9.2 回归自检：
//   ① 档位归类（鸣潮=gacha / cs2=fps）+ 远控只对竞技档的新默认
//   ② ResumeAllRemoteIfPausedAsync 的守卫：没暂停过就绝不去"恢复"（用替身远控 + 标记文件判别）
//   ③ CardGrid 宽度变化后高度**同步**重算（白块 bug 的正向断言）
class FixVerify2
{
    static string Out = "";
    static void L(string s) { Out += s + "\r\n"; Console.WriteLine(s); }

    static void Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "gb_fixverify2");
        try { Directory.CreateDirectory(tmp); } catch { }

        // 先按真实流程载入配置（含 v2.9.1 的自愈），否则 Cfg 是空默认值，档位测试没有意义
        try { Program.Cfg = Config.Load(Program.ConfigPath); } catch { }
        L("  配置来源 = " + Program.ConfigPath + "    LoadNote = " + Config.LoadNote);

        // ---------------- ① 档位归类 ----------------
        L("=== 1. 档位归类与联动范围 ===");
        try
        {
            L("  AwareOnlyFps 默认 = " + Program.Cfg.AwareOnlyFps + "   （true = 只有竞技档暂停远控）");
            string[] procs = { "Client-Win64-Shipping", "cs2", "VALORANT", "YuanShen", "ZenlessZoneZero", "Cyberpunk2077", "ForzaHorizon6" };
            foreach (string pr in procs)
            {
                string cat = Program.AutoCategory(pr);
                bool pause = !Program.Cfg.AwareOnlyFps || cat == "fps";
                L(string.Format("  {0,-28} → 档位 {1,-6} 暂停远控={2}", pr, cat, pause ? "是" : "否（不动远控）"));
            }
        }
        catch (Exception ex) { L("  FAIL: " + ex.Message); }

        // ---------------- ② 远控恢复守卫 ----------------
        L("");
        L("=== 2. 退出游戏时的远控恢复守卫（替身远控 + 标记文件）===");
        List<RemoteApp> saved = null;
        bool savedInstalled = Program.RemoteInstalled, savedPaused = Program.RemotePausedByLink;
        try
        {
            string marker = Path.Combine(tmp, "resume_called.txt");
            string probe = Path.Combine(tmp, "probe_remote.bat");
            if (File.Exists(marker)) File.Delete(marker);
            File.WriteAllText(probe, "@echo off\r\necho resumed > \"" + marker + "\"\r\n", new UTF8Encoding(false));

            saved = Program.Cfg.RemoteApps;
            Program.Cfg.RemoteApps = new List<RemoteApp>();
            var fake = new RemoteApp();
            fake.Name = "probe"; fake.Exe = probe;
            fake.Processes = new List<string>(); fake.Services = new List<string>();
            Program.Cfg.RemoteApps.Add(fake);
            Program.RemoteInstalled = true;

            // (a) 没暂停过 → 不该执行恢复
            Program.RemotePausedByLink = false;
            Program.ResumeAllRemoteIfPausedAsync();
            Thread.Sleep(1600);
            bool a = !File.Exists(marker);
            L("  未暂停过时调用 → 替身远控未被启动: " + a + (a ? "  ✔" : "  ✘ 守卫失效"));

            // (b) 确实暂停过 → 必须恢复（证明探针本身有效，不是"什么都没发生"）
            Program.RemotePausedByLink = true;
            Program.ResumeAllRemoteIfPausedAsync();
            Thread.Sleep(1600);
            bool b = File.Exists(marker);
            L("  已暂停过时调用 → 替身远控被启动: " + b + (b ? "  ✔ 探针有效" : "  ✘ 恢复没生效"));
            L("  结论: " + ((a && b) ? "守卫正确 —— 二游/3A 退出不会再无端拉起远控" : "需要复核"));
        }
        catch (Exception ex) { L("  FAIL: " + ex.Message); }
        finally
        {
            if (saved != null) Program.Cfg.RemoteApps = saved;
            Program.RemoteInstalled = savedInstalled;
            Program.RemotePausedByLink = savedPaused;
        }

        // ---------------- ③ 网格高度同步重算 ----------------
        L("");
        L("=== 3. 封面网格：改宽度后高度必须立刻正确（白块 bug 正向断言）===");
        try
        {
            var grid = new CardGrid();
            grid.CellW = 162; grid.CellH = 230; grid.PadPx = 6;   // 与库页一致（148+14 / 222+8）
            for (int i = 0; i < 16; i++)
            {
                var p = new Panel();
                p.Size = new Size(148, 222);
                p.Margin = new Padding(7);
                grid.Controls.Add(p);
            }
            int[] widths = { 1100, 700, 1500, 520 };
            foreach (int w in widths)
            {
                grid.Width = w;
                int h = grid.Height;                       // ★ 同步读，不给布局机会
                int avail = Math.Max(162, w - 12);
                int perRow = Math.Max(1, avail / 162);
                int rows = (16 + perRow - 1) / perRow;
                int expect = rows * 230 + 12;
                L(string.Format("  宽 {0,4} → 每行 {1} · {2} 行 · 期望高 {3} · 实测高 {4}  {5}",
                    w, perRow, rows, expect, h, h == expect ? "✔" : "✘"));
            }

            // 空列表必须归零（否则留一整屏空白）
            grid.Controls.Clear();
            grid.Width = 1100;
            L("  清空后高度 = " + grid.Height + (grid.Height == 0 ? "  ✔" : "  ✘ 应为 0"));
            grid.Dispose();
        }
        catch (Exception ex) { L("  FAIL: " + ex.Message); }

        try { File.WriteAllText(Path.Combine(tmp, "fix_verify2.txt"), Out, new UTF8Encoding(false)); } catch { }
    }
}
