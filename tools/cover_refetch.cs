// ============================================================================
//  cover_refetch —— 封面来源治理（官方竖版优先 → 联网竖版兜底）
//  背景：steam_<id>.jpg 里混着 library_hero / header 的【横版】图（如 Forza 的
//  风景照横幅），裁成 2:3 竖卡后下半被遮罩压平，看起来"上下两半"。
//  流程（每款有真封面的游戏）：
//    1) 当前封面 ar = w/h；> 0.80 视为横版/方图（目标 2:3 = 0.667）
//    2) Steam 游戏：先拉官方 library_600x900（DownloadSteam 本来就竖版优先），
//       拉到竖版 → 覆盖到 SafeName(Title).jpg（Card() 以命名文件最优先）
//    3) 仍横版 → CoverArt.SearchWebCover（Bing，比例 0.58~0.78）
//  ⚠ 判据是 ar 上限：0.67 的标准竖版绝不能当横版重抓（v1 探针就犯过这个错，
//    把 Cyberpunk 的官方 300x450 覆盖成了网络图）。
//  编译运行：python tools\cover_refetch.py
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Fluxion;

class CoverRefetch
{
    [STAThread]
    static void Main(string[] args)
    {
        Program.Cfg = Config.Load(Program.ConfigPath);
        List<DlssgGame> ig;
        var games = Dlssg.MergeScan(Lib.ScanAll(null), out ig);   // ScanAll = 目录+注册表官方名+appmanifest+WeGame 等全平台
        Console.WriteLine("games: " + games.Count);
        int okCnt = 0, officialCnt = 0, webCnt = 0, failCnt = 0;

        foreach (var g in games)
        {
            if (g == null || g.Ignored || g.Hidden) continue;
            // 用法：CoverRefetch <标题关键字>  → 无视比例，强制对匹配游戏重抓联网竖版
            bool force = args != null && args.Length > 0 && (g.Title ?? "").IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0;
            string src = FirstCover(g);
            if (src == null) continue;                       // 没有真封面（生成卡）→ 跳过
            double ar = Aspect(src);
            if (ar < 0) continue;
            if (!force && ar <= 0.80 && !(args != null && args.Length > 0))
            { okCnt++; continue; }

            // ---- 1) Steam 官方竖版：有 appid 就刷新一遍（官方永远优先于网络图）----
            string id = g.Id ?? "";
            if (!force && id.StartsWith("steam_") && id.Length > 6)
            {
                string appid = id.Substring(6);
                try
                {
                    if (CoverArt.DownloadSteam(appid, id))
                    {
                        double oar = Aspect(CoverArt.CardFile(id));
                        if (oar > 0 && oar <= 0.80)          // 官方竖版到手 → 覆盖命名文件
                        {
                            string dst = Path.Combine(CoverArt.CoversDir, CoverArt.SafeName(g.Title) + ".jpg");
                            File.Copy(CoverArt.CardFile(id), dst, true);
                            Console.WriteLine("OFFICIAL  {0}  <- {1}  ar={2:F2}", g.Title, Path.GetFileName(CoverArt.CardFile(id)), oar);
                            officialCnt++;
                            src = dst; ar = oar;
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("  official fail {0}: {1}", g.Title, ex.Message); }
            }

            // ---- 2) 仍横版/方图（或 force）→ 联网竖版 ----
            if (force || ar > 0.80)
            {
                Console.Write("LANDSCAPE {0}  ar={1:F2}  -> web... ", g.Title, ar);
                string r = CoverArt.SearchWebCover(g);
                string src2 = FirstCover(g);
                double ar2 = src2 == null ? -1 : Aspect(src2);
                Console.WriteLine(r + "   now: " + (src2 == null ? "-" : Path.GetFileName(src2) + " ar=" + ar2.ToString("F2")));
                if (ar2 > 0 && ar2 <= 0.80) webCnt++; else failCnt++;
            }
            else okCnt++;
        }
        Console.WriteLine();
        Console.WriteLine("SUMMARY: vertical=" + okCnt + " official=" + officialCnt + " web=" + webCnt + " fail=" + failCnt);
    }

    static double Aspect(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var im = Image.FromStream(fs))
                return (double)im.Width / im.Height;
        }
        catch { return -1; }
    }

    static string FirstCover(DlssgGame g)
    {
        string nm = CoverArt.SafeName(g.Title ?? "");
        var l = new List<string>();
        l.Add(Path.Combine(CoverArt.CoversDir, nm + ".png"));
        l.Add(Path.Combine(CoverArt.CoversDir, nm + ".jpg"));
        l.Add(Path.Combine(CoverArt.CoversDir, nm + ".jpeg"));
        string id = g.Id ?? "";
        if (id.Length > 0)
        {
            l.Add(CoverArt.CardFile(id));
            l.Add(CoverArt.CardFilePng(id));
        }
        foreach (string p in l) if (File.Exists(p)) return p;
        return null;
    }
}
