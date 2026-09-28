using System;
using System.IO;
using System.Text;

namespace Fluxion
{
    // 2026-09-16：XeMfg 卸载/部署安全性回归（全部在 %TEMP% 沙盒里跑，绝不碰真实游戏目录）。
    //  断言 1：卸载时 OptiScaler.ini（用户调参载体）必须原地保留
    //  断言 2：无备份可还原的文件必须移入隔离区而不是删除（含 165MB NR 模型这类不可再生件）
    //  断言 3：隔离区里能按原相对路径找回来
    //  断言 4：部署时不覆盖已存在的 OptiScaler.ini（否则出厂值 Dxgi=auto/DlssNr=auto 会盖掉调参）
    static class UninstProbe
    {
        static int Fail = 0;
        static StringBuilder Out = new StringBuilder();

        static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        static void Chk(string what, bool ok, string got)
        {
            if (!ok) Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (got.Length > 0 ? "   (" + got + ")" : ""));
        }

        static void WriteBig(string p, int mb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[1024 * 1024];
                for (int i = 0; i < mb; i++) fs.Write(buf, 0, buf.Length);
            }
        }

        static string NewestQuarantine()
        {
            try
            {
                string root = Path.Combine(XeMfg.PackRoot, "_removed");
                if (!Directory.Exists(root)) return null;
                string best = null; DateTime bt = DateTime.MinValue;
                foreach (string d in Directory.GetDirectories(root))
                {
                    DateTime t = Directory.GetLastWriteTime(d);
                    if (t > bt) { bt = t; best = d; }
                }
                return best;
            }
            catch { return null; }
        }

        static int Main()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gb_safe_test");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            L("沙盒根 = " + tmp);
            L("PackRoot = " + XeMfg.PackRoot);
            L("");

            const string SENTINEL = "; USER-TUNED-SENTINEL-7f3a9c\r\n[Spoofing]\r\nDxgi=true\r\n";
            string ini = "OptiScaler.ini";
            string dxgi = "dxgi.dll";
            string nr = "nvngx_dlssnr.dll";
            string fwd = "nvngx.dll_dlssnr.dll";
            string relNr = Path.Combine("OptiScaler", "streamline", "nvngx_dlssnr.dll");

            // ============================================================ ① 卸载
            L("=== 1. 卸载：OptiScaler.ini 保留 + 其余移入隔离区 ===");
            string g1 = Path.Combine(tmp, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(g1);
            File.WriteAllText(Path.Combine(g1, "Client-Win64-Shipping.exe"), "stub");
            WriteBig(Path.Combine(g1, dxgi), 21);                 // >20MB 才被 CheckAt 认作已部署
            File.WriteAllText(Path.Combine(g1, ini), SENTINEL, Encoding.UTF8);
            WriteBig(Path.Combine(g1, nr), 3);
            File.WriteAllText(Path.Combine(g1, fwd), "fwd");
            WriteBig(Path.Combine(g1, relNr), 2);

            if (XeMfg.Detect(g1) != "wuwa") { L("!! Detect 认不出假鸣潮目录，测试无法继续"); return 1; }
            Chk("卸载前 IsInstalled = true", XeMfg.IsInstalled(g1), "");

            string r1 = XeMfg.Uninstall(g1);
            L("  回报：" + r1);
            L("");
            Chk("OptiScaler.ini 原地保留", File.Exists(Path.Combine(g1, ini)), "");
            Chk("OptiScaler.ini 内容未被改动",
                File.Exists(Path.Combine(g1, ini)) &&
                File.ReadAllText(Path.Combine(g1, ini), Encoding.UTF8).IndexOf("USER-TUNED-SENTINEL") >= 0, "");
            Chk("dxgi.dll 已不在游戏目录", !File.Exists(Path.Combine(g1, dxgi)), "");
            Chk("nvngx_dlssnr.dll 已不在游戏目录", !File.Exists(Path.Combine(g1, nr)), "");
            Chk("卸载后 IsInstalled = false", !XeMfg.IsInstalled(g1), "");

            string quar = NewestQuarantine();
            Chk("隔离区已创建", quar != null && Directory.Exists(quar), quar == null ? "null" : quar);
            if (quar != null)
            {
                Chk("隔离区里有 dxgi.dll", File.Exists(Path.Combine(quar, dxgi)), "");
                Chk("隔离区里有 nvngx_dlssnr.dll（不可再生件）", File.Exists(Path.Combine(quar, nr)), "");
                Chk("隔离区里保留了原相对路径 " + relNr, File.Exists(Path.Combine(quar, relNr)), "");
                Chk("隔离区里**没有** OptiScaler.ini（它是用户配置，原地留）",
                    !File.Exists(Path.Combine(quar, ini)), "");
            }
            L("");

            // ============================================================ ② 部署不覆盖 ini
            L("=== 2. 部署：不覆盖已存在的 OptiScaler.ini ===");
            string g2 = Path.Combine(tmp, "OneClick", "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(g2);
            File.WriteAllText(Path.Combine(g2, "Client-Win64-Shipping.exe"), "stub");
            File.WriteAllText(Path.Combine(g2, ini), SENTINEL, Encoding.UTF8);

            string before = File.ReadAllText(Path.Combine(g2, ini), Encoding.UTF8);
            string r2 = XeMfg.Install(g2);
            L("  回报（截断）：" + r2.Substring(0, Math.Min(220, r2.Length)));
            L("");
            Chk("部署成功（入口代理已落盘）", File.Exists(Path.Combine(g2, dxgi)), "");
            Chk("部署后 IsInstalled = true", XeMfg.IsInstalled(g2), "");
            string after = File.Exists(Path.Combine(g2, ini))
                ? File.ReadAllText(Path.Combine(g2, ini), Encoding.UTF8) : "";
            Chk("已存在的 OptiScaler.ini 未被出厂值覆盖", after == before, "len " + before.Length + " -> " + after.Length);
            Chk("回报里说明了保留 ini", r2.IndexOf("保留你已调好的") >= 0, "");
            L("");

            // ============================================================ ③ 全新目录仍会铺 ini
            L("=== 3. 全新目录：ini 不存在时应正常铺进去 ===");
            string g3 = Path.Combine(tmp, "Fresh", "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(g3);
            File.WriteAllText(Path.Combine(g3, "Client-Win64-Shipping.exe"), "stub");
            string r3 = XeMfg.Install(g3);
            Chk("全新目录部署成功", XeMfg.IsInstalled(g3), "");
            Chk("ini 已铺入", File.Exists(Path.Combine(g3, ini)), "");
            L("");

            // ============================================================ ④ 清理
            L("=== 4. 清理沙盒 ===");
            try { Directory.Delete(tmp, true); L("  沙盒已删"); } catch (Exception ex) { L("  沙盒清理失败：" + ex.Message); }
            if (quar != null) { try { Directory.Delete(quar, true); L("  测试隔离区已删"); } catch { } }

            L("");
            L(Fail == 0 ? "===== ALL PASS =====" : ("===== FAIL " + Fail + " ====="));
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "uninst_probe.txt"), Out.ToString(), new UTF8Encoding(false)); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
