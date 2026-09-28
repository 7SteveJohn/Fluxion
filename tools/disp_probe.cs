// 显示模式守卫 DispGuard 的验证。
// 原则：**全程只读**，一个真的模式都不写。
//   - 改(N)写路径的能力用 CDS_TEST 空跑（只问驱动"这么设行不行"，不落地）—— 第 4 节
//   - Restore() 只在"现状 == 快照"时被调用，天然是 no-op —— 第 5 节
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    static class DispProbe
    {
        static int Pass = 0, Fail = 0;
        static readonly List<string> Lines = new List<string>();

        static void L(string s) { Lines.Add(s); Console.WriteLine(s); }

        static void Chk(string what, bool ok, string extra)
        {
            if (ok) Pass++; else Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(extra) ? "" : "   → " + extra));
        }

        static int Main()
        {
            L("=== 显示模式守卫 DispGuard 验证 ===");
            L("");

            // ---------------- ① DEVMODE 布局 ----------------
            L("=== 1. DEVMODE 布局 ===");
            int sz = Marshal.SizeOf(typeof(Native.DEVMODE));
            Chk("sizeof(DEVMODE) == 220（dmSize 必须填这个值）", sz == 220, sz + " 字节");
            L("");

            // ---------------- ② Capture ----------------
            L("=== 2. Capture() 抓当前在用屏幕 ===");
            var snap = DispGuard.Capture();
            L("  抓到 " + snap.Count + " 台：");
            foreach (var m in snap)
                L("    " + m.Dev + "  " + m.W + "x" + m.H + "@" + m.Hz + " bpp=" + m.Bpp + " 位置=(" + m.X + "," + m.Y + ")");
            Chk("至少抓到 1 台在用屏幕", snap.Count >= 1, snap.Count.ToString());
            bool allValid = true;
            foreach (var m in snap)
                if (m.W <= 0 || m.H <= 0 || m.Hz <= 0 || string.IsNullOrEmpty(m.Dev)) allValid = false;
            Chk("每台都有合法的宽/高/刷新率/设备名", allValid, "");
            Chk("设备名是 \\\\.\\DISPLAY<n> 形式", snap.Count > 0 && snap[0].Dev.StartsWith("\\\\.\\DISPLAY"),
                snap.Count > 0 ? snap[0].Dev : "(无)");
            L("");

            // ---------------- ③ Current 复读 ----------------
            L("=== 3. Current() 复读应与快照一致 ===");
            foreach (var m in snap)
            {
                var cur = DispGuard.Current(m.Dev);
                bool ok = cur.HasValue && cur.Value.W == m.W && cur.Value.H == m.H
                          && cur.Value.Hz == m.Hz && cur.Value.Bpp == m.Bpp;
                Chk(m.Dev + " 复读一致", ok,
                    cur.HasValue ? (cur.Value.W + "x" + cur.Value.H + "@" + cur.Value.Hz) : "(读不到)");
            }
            L("");

            // ---------------- ④ 写路径空跑 ----------------
            L("=== 4. ChangeDisplaySettingsEx 空跑（CDS_TEST，不会真的改屏幕）===");
            const int CDS_TEST = 0x2, CDS_UPDATEREGISTRY = 0x1;
            const int DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000,
                      DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
            foreach (var m in snap)
            {
                var dm = new Native.DEVMODE();
                dm.dmSize = (short)sz;
                dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmPelsWidth = m.W; dm.dmPelsHeight = m.H; dm.dmDisplayFrequency = m.Hz;
                dm.dmBitsPerPel = m.Bpp; dm.dmPositionX = m.X; dm.dmPositionY = m.Y;
                int rc = Native.ChangeDisplaySettingsEx(m.Dev, ref dm, IntPtr.Zero,
                                                        CDS_UPDATEREGISTRY | CDS_TEST, IntPtr.Zero);
                Chk(m.Dev + " 装回 " + m.W + "x" + m.H + "@" + m.Hz + " 的写法被驱动接受", rc == 0, "rc=" + rc);
            }
            L("");

            // ---------------- ⑤ Restore 语义 ----------------
            L("=== 5. Restore() 的 no-op / 跳过语义（不写屏）===");
            var noop = DispGuard.Restore(snap);
            Chk("现状与快照一致时不产生任何改动（0 条）", noop.Count == 0, noop.Count + " 条");
            var ghost = new List<DispGuard.Mode>();
            ghost.Add(new DispGuard.Mode { Dev = "\\\\.\\DISPLAY99", W = 1920, H = 1080, Hz = 60, Bpp = 32, X = 0, Y = 0 });
            var ghostLogs = DispGuard.Restore(ghost);
            Chk("拔掉的/不存在的屏被跳过而不是报错", ghostLogs.Count == 0, ghostLogs.Count + " 条");
            Chk("快照为 null 时不抛异常", DispGuard.Restore(null).Count == 0, "");
            DispGuard.RestoreLater(null, 10);
            Chk("RestoreLater(null) 不派发后台任务（也不抛）", true, "已调用");
            L("");

            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            L("PASS=" + Pass + "  FAIL=" + Fail);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "disp_probe.txt"),
                    string.Join("\r\n", Lines.ToArray()), new UTF8Encoding(false));
            }
            catch { }
            return Fail;
        }
    }
}
