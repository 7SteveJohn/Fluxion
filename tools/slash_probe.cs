using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Fluxion
{
    // 回归：所有会落进 DlssgGame.Dir 的路径都不许含正斜杠
    //  （混合斜杠的目录 explorer.exe 打不开 —— 会静默退到「文档」）。
    static class SlashProbe
    {
        static int Fail = 0;

        static void Chk(string src, List<string> dirs)
        {
            int bad = 0;
            foreach (string d in dirs)
                if (d != null && d.IndexOf('/') >= 0) { bad++; Console.WriteLine("       含正斜杠 -> " + d); }
            Console.WriteLine((bad == 0 ? "  [OK]   " : "  [FAIL] ") + src + "   (" + dirs.Count + " 个)");
            if (bad > 0) Fail++;
        }

        static int Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var a = new List<string>();
            foreach (SteamApp s in SteamLib.Scan()) a.Add(s.Dir);
            Chk("SteamLib.Scan() 的 Dir", a);

            var b = new List<string>();
            foreach (PlatformScan.RegEntry e in PlatformScan.RegistryEntries()) b.Add(e.Dir);
            Chk("RegistryEntries() 的 Dir", b);

            var c = new List<string>();
            foreach (DlssgGame g in Lib.ScanAll(delegate(string s) { })) c.Add(g.Dir);
            Chk("ScanAll() 最终列表的 Dir（端到端）", c);

            Console.WriteLine();
            Console.WriteLine(Fail == 0 ? "RESULT: PASS" : "RESULT: FAIL " + Fail + " 项");
            return Fail;
        }
    }
}
