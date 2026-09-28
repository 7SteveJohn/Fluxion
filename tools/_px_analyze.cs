// 一次性像素分析器：读 ui-snapshots 的 PNG，扫行列颜色，定位"多余空白 / 多出的色带"。
// 用法：pxa.exe <png> [mode]
//   mode=cols  从上往下扫若干列，输出颜色 Run（默认）
//   mode=rows  从左往右扫若干行
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

class PxAnalyze
{
    static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
    static bool Same(Color a, Color b)
    {
        return Math.Abs(a.R - b.R) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.B - b.B) <= 6;
    }

    static void ScanX(Bitmap b, int x, int y0, int y1)
    {
        Console.WriteLine("--- column x=" + x + "  y " + y0 + ".." + y1);
        int start = y0;
        Color prev = b.GetPixel(x, y0);
        for (int y = y0 + 1; y <= y1; y++)
        {
            Color c = b.GetPixel(x, y);
            if (!Same(c, prev))
            {
                if (y - start >= 2)
                    Console.WriteLine(string.Format("  y {0,4}..{1,-4} ({2,3}px)  {3}", start, y - 1, y - start, Hex(prev)));
                start = y; prev = c;
            }
            else if (y - start == 1) { }
        }
        if (y1 - start >= 1)
            Console.WriteLine(string.Format("  y {0,4}..{1,-4} ({2,3}px)  {3}", start, y1, y1 - start + 1, Hex(prev)));
    }

    static void ScanY(Bitmap b, int y, int x0, int x1)
    {
        Console.WriteLine("--- row y=" + y + "  x " + x0 + ".." + x1);
        int start = x0;
        Color prev = b.GetPixel(x0, y);
        for (int x = x0 + 1; x <= x1; x++)
        {
            Color c = b.GetPixel(x, y);
            if (!Same(c, prev))
            {
                if (x - start >= 2)
                    Console.WriteLine(string.Format("  x {0,4}..{1,-4} ({2,3}px)  {3}", start, x - 1, x - start, Hex(prev)));
                start = x; prev = c;
            }
        }
        if (x1 - start >= 1)
            Console.WriteLine(string.Format("  x {0,4}..{1,-4} ({2,3}px)  {3}", start, x1, x1 - start + 1, Hex(prev)));
    }

    // 找一张白色卡片的边界：从 (x, yStart) 向下找第一段连续"近白"区间
    static void FindCard(Bitmap b, int x, int yStart, int yEnd)
    {
        Console.WriteLine("--- card scan at x=" + x);
        int s = -1;
        for (int y = yStart; y <= yEnd; y++)
        {
            Color c = b.GetPixel(x, y);
            bool white = c.R > 246 && c.G > 246 && c.B > 246;
            if (white && s < 0) s = y;
            if (!white && s >= 0)
            {
                if (y - s >= 6) Console.WriteLine("  white run y " + s + ".." + (y - 1) + "  (" + (y - s) + "px)");
                s = -1;
            }
        }
        if (s >= 0) Console.WriteLine("  white run y " + s + ".." + yEnd);
    }

    // 沿一行取样求平均色（每 5 px 取一个，避开文字噪声）
    static Color AvgX(Bitmap b, int x0, int x1, int y)
    {
        if (y < 0 || y >= b.Height) return Color.Black;
        long r = 0, g = 0, bl = 0; int n = 0;
        for (int x = Math.Max(0, x0); x <= Math.Min(b.Width - 1, x1); x += 5)
        {
            Color c = b.GetPixel(x, y); r += c.R; g += c.G; bl += c.B; n++;
        }
        return n == 0 ? Color.Black : Color.FromArgb((int)(r / n), (int)(g / n), (int)(bl / n));
    }

    // 沿一列取样求平均色
    static Color AvgY(Bitmap b, int x, int y0, int y1)
    {
        if (x < 0 || x >= b.Width) return Color.Black;
        long r = 0, g = 0, bl = 0; int n = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(b.Height - 1, y1); y += 5)
        {
            Color c = b.GetPixel(x, y); r += c.R; g += c.G; bl += c.B; n++;
        }
        return n == 0 ? Color.Black : Color.FromArgb((int)(r / n), (int)(g / n), (int)(bl / n));
    }

    static void Main(string[] args)
    {
        string path = args[0];
        string mode = args.Length > 1 ? args[1] : "cols";
        using (var b = new Bitmap(path))
        {
            Console.WriteLine("image " + b.Width + "x" + b.Height + "  " + path);
            if (mode == "cols")
            {
                int[] xs = args.Length > 2 ? Array.ConvertAll(args[2].Split(','), int.Parse)
                                           : new int[] { 60, 120, 250, 400, 700 };
                foreach (int x in xs) ScanX(b, x, 0, Math.Min(b.Height - 1, args.Length > 4 ? int.Parse(args[4]) : 260));
            }
            else if (mode == "rows")
            {
                int[] ys = args.Length > 2 ? Array.ConvertAll(args[2].Split(','), int.Parse)
                                           : new int[] { 20, 40, 60, 120, 200 };
                foreach (int y in ys) ScanY(b, y, 0, Math.Min(b.Width - 1, args.Length > 4 ? int.Parse(args[4]) : 420));
            }
            else if (mode == "seams")
            {
                // seams y0 y1 x0 x1 [thr] —— 沿 y 扫，取 x0..x1 的平均色，列出横向硬边
                int y0 = int.Parse(args[2]), y1 = int.Parse(args[3]);
                int sx0 = int.Parse(args[4]), sx1 = int.Parse(args[5]);
                int thr = args.Length > 6 ? int.Parse(args[6]) : 3;
                Color prev = AvgX(b, sx0, sx1, y0);
                Console.WriteLine("--- horizontal seams (x " + sx0 + ".." + sx1 + ")");
                for (int y = y0 + 1; y <= Math.Min(y1, b.Height - 1); y++)
                {
                    Color c = AvgX(b, sx0, sx1, y);
                    int dr = c.R - prev.R, dg = c.G - prev.G, db = c.B - prev.B;
                    if (Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))) >= thr)
                        Console.WriteLine(string.Format("  y={0,4}  {1} -> {2}  d=({3,4},{4,4},{5,4})", y, Hex(prev), Hex(c), dr, dg, db));
                    prev = c;
                }
            }
            else if (mode == "vseams")
            {
                // vseams x0 x1 y0 y1 [thr] —— 沿 x 扫，取 y0..y1 的平均色，列出纵向硬边
                int x0 = int.Parse(args[2]), x1 = int.Parse(args[3]);
                int sy0 = int.Parse(args[4]), sy1 = int.Parse(args[5]);
                int thr = args.Length > 6 ? int.Parse(args[6]) : 3;
                Color prev = AvgY(b, x0, sy0, sy1);
                Console.WriteLine("--- vertical seams (y " + sy0 + ".." + sy1 + ")");
                for (int x = x0 + 1; x <= Math.Min(x1, b.Width - 1); x++)
                {
                    Color c = AvgY(b, x, sy0, sy1);
                    int dr = c.R - prev.R, dg = c.G - prev.G, db = c.B - prev.B;
                    if (Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))) >= thr)
                        Console.WriteLine(string.Format("  x={0,4}  {1} -> {2}  d=({3,4},{4,4},{5,4})", x, Hex(prev), Hex(c), dr, dg, db));
                    prev = c;
                }
            }
            else if (mode == "profile")
            {
                // profile x y0 y1 [step] —— 逐行输出精确颜色 + 与上一行的差值（看渐变有没有台阶）
                int px = int.Parse(args[2]);
                int y0 = int.Parse(args[3]), y1 = int.Parse(args[4]);
                int step = args.Length > 5 ? int.Parse(args[5]) : 1;
                Color prev = b.GetPixel(px, y0);
                for (int y = y0; y <= Math.Min(y1, b.Height - 1); y += step)
                {
                    Color c = b.GetPixel(px, y);
                    Console.WriteLine(string.Format("y={0,4}  {1}  d=({2,4},{3,4},{4,4})", y, Hex(c),
                        c.R - prev.R, c.G - prev.G, c.B - prev.B));
                    prev = c;
                }
            }
            else if (mode == "hprofile")
            {
                // hprofile y x0 x1 [step] —— 横向同理
                int py = int.Parse(args[2]);
                int x0 = int.Parse(args[3]), x1 = int.Parse(args[4]);
                int step = args.Length > 5 ? int.Parse(args[5]) : 1;
                Color prev = b.GetPixel(x0, py);
                for (int x = x0; x <= Math.Min(x1, b.Width - 1); x += step)
                {
                    Color c = b.GetPixel(x, py);
                    Console.WriteLine(string.Format("x={0,4}  {1}  d=({2,4},{3,4},{4,4})", x, Hex(c),
                        c.R - prev.R, c.G - prev.G, c.B - prev.B));
                    prev = c;
                }
            }
            else if (mode == "stack")
            {
                // stack <other.png> <out.png> [labelTop] [labelBottom]
                // 当前图在上（改前）、other 在下（改后），竖排拼成一张对比图
                string other = args[2], outFile = args[3];
                string la = args.Length > 4 ? args[4] : "";
                string lb = args.Length > 5 ? args[5] : "";
                using (var b2 = new Bitmap(other))
                {
                    int labH = 34;
                    int w = Math.Max(b.Width, b2.Width);
                    int h = b.Height + b2.Height + labH * 2 + 24;
                    using (var o = new Bitmap(w, h))
                    using (var g = Graphics.FromImage(o))
                    using (var f = new Font("Microsoft YaHei", 15f, FontStyle.Bold))
                    using (var bg = new SolidBrush(Color.FromArgb(40, 44, 52)))
                    {
                        g.Clear(Color.FromArgb(205, 210, 216));
                        g.FillRectangle(bg, 0, 0, w, labH);
                        g.DrawString(la, f, Brushes.White, 10, 4);
                        g.DrawImage(b, new Rectangle(0, labH, b.Width, b.Height));
                        g.FillRectangle(bg, 0, labH + b.Height + 12, w, labH);
                        g.DrawString(lb, f, Brushes.White, 10, labH + b.Height + 16);
                        g.DrawImage(b2, new Rectangle(0, labH * 2 + b.Height + 24, b2.Width, b2.Height));
                        o.Save(outFile, ImageFormat.Png);
                    }
                    Console.WriteLine("stacked -> " + outFile + "  " + w + "x" + h);
                }
            }
            else if (mode == "card")
            {
                FindCard(b, args.Length > 2 ? int.Parse(args[2]) : 300, 0, b.Height - 1);
            }
            else if (mode == "crop")
            {
                // crop x y w h scale outfile
                int cx = int.Parse(args[2]), cy = int.Parse(args[3]);
                int cw = int.Parse(args[4]), ch = int.Parse(args[5]);
                int sc = args.Length > 6 ? int.Parse(args[6]) : 2;
                string outFile = args.Length > 7 ? args[7] : "_crop.png";
                using (var o = new Bitmap(cw * sc, ch * sc))
                {
                    using (var gg = Graphics.FromImage(o))
                    {
                        gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        gg.DrawImage(b, new Rectangle(0, 0, cw * sc, ch * sc),
                                     new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
                    }
                    o.Save(outFile, ImageFormat.Png);
                }
                Console.WriteLine("cropped -> " + outFile + "  " + cw + "x" + ch + " x" + sc);
            }
        }
    }
}
