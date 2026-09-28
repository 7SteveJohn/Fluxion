// ============================================================================
//  ui_snapshot —— 离屏渲染视觉验收探针
//  ---------------------------------------------------------------------------
//  为什么需要它：静态校验 + 编译通过 ≠ 观感正确。2.6.0 就是靠它才发现
//  「复选框文字被省略号截断」「浅色下输入框与白卡片分不清」这类只有看图才知道的问题。
//
//  用法（必须与源码一起编译，否则访问不到 internal 的 Theme / HeadBox / CardGrid 等）：
//    1) 编译：python tools\ui_snapshot.py build
//    2) 渲染：python tools\ui_snapshot.py light real 3      # 浅色 · 游戏库页
//             python tools\ui_snapshot.py dark  real 0      # 深色 · 仪表盘
//             python tools\ui_snapshot.py light showcase    # 样式表（所有改动过的控件）
//             python tools\ui_snapshot.py light covers      # 封面多路径下载实测
//  输出：Fluxion\ui-snapshots\*.png
//
//  页面索引：0 仪表盘 1 性能优化 2 帧生成 3 游戏库 4 实时监控 5 说明 6 设置
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Fluxion;

class StyleProbe
{
    static string OUT = @"D:\youhua\Fluxion\ui-snapshots";

    static DlssgGame G(string title, string id)
    {
        var g = new DlssgGame();
        g.Title = title;
        g.Id = id == null ? "" : id;
        g.Platform = "steam";
        g.Dir = @"G:\nonexistent";
        g.Exe = "";
        return g;
    }

    static void Shot(Form f, string file)
    {
        f.Show();
        for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(Path.Combine(OUT, file), ImageFormat.Png);
        }
        Console.WriteLine("saved " + file);
    }

    // 合成「样式表」：把改动过的每一种控件都摆出来，肉眼核对圆角与配色
    static void Showcase(string mode)
    {
        var f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.BackColor = Theme.Bg;
        f.ClientSize = new Size(1060, 740);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);

        // 侧栏 + 圆角胶囊导航
        var side = new Panel();
        side.SetBounds(0, 0, 190, 740);
        side.BackColor = Theme.Panel;
        f.Controls.Add(side);

        var brand = new Label();
        brand.Text = "Fluxion";
        brand.Font = Theme.FB(10.5f);
        brand.ForeColor = Theme.Text;
        brand.AutoSize = true;
        brand.BackColor = Theme.Panel;
        brand.Location = new Point(Theme.S(18), Theme.S(18));
        side.Controls.Add(brand);

        var brand2 = new Label();
        brand2.Text = "v" + Program.AppVersion + "  ·  " + mode;
        brand2.Font = Theme.F(8f);
        brand2.ForeColor = Theme.TextFaint;
        brand2.AutoSize = true;
        brand2.BackColor = Theme.Panel;
        brand2.Location = new Point(Theme.S(18), Theme.S(42));
        side.Controls.Add(brand2);

        string[] nav = new string[] { "仪表盘", "性能优化", "帧生成", "游戏库", "实时监控", "说明", "设置" };
        for (int i = 0; i < nav.Length; i++)
        {
            var nb = new NavBtn();
            nb.Text = nav[i];
            nb.SetBounds(0, Theme.S(74) + i * Theme.S(40), 188, Theme.S(38));
            nb.Active = (i == 3);
            nb.Font = nb.Active ? Theme.FB(9.5f) : Theme.F(9.5f);
            side.Controls.Add(nb);
        }

        int X = Theme.S(214);
        int W = 1060 - X - Theme.S(24);

        var head = new HeadBox("游戏库", "全平台游戏 · 封面浏览 · 右键管理");
        head.SetBounds(X, Theme.S(16), W, Theme.S(56));
        f.Controls.Add(head);

        // 分区（圆角面板 + 强调条 + 圆角按钮 + 圆角输入框）
        var s = new Sec("界面与圆角");
        var rf = new RoundField(Theme.S(200), "");
        rf.Box.Text = "";
        var ck = new RoundCheck();
        Theme.StyleCheck(ck, "开关");
        ck.Checked = true;
        s.Pair("搜索", rf, Theme.S(200), "开关", ck, 0);
        var cb = new RoundCombo();
        cb.Items.AddRange(new object[] { "浅色", "深色 · 游戏启动器风格" });
        cb.SelectedIndex = mode == "dark" ? 1 : 0;
        s.Row("界面主题", cb, Theme.S(230));

        var b1 = new FlatBtn(); b1.Text = "主按钮"; b1.Kind = BtnKind.Primary; b1.Width = Theme.S(96);
        var b2 = new FlatBtn(); b2.Text = "次按钮"; b2.Width = Theme.S(96);
        var b3 = new FlatBtn(); b3.Text = "成功"; b3.Kind = BtnKind.Success; b3.Width = Theme.S(96);
        var b4 = new FlatBtn(); b4.Text = "危险"; b4.Kind = BtnKind.Danger; b4.Width = Theme.S(96);
        s.Buttons(b1, b2, b3, b4);

        var bar = new Bar(); bar.Caption = "GPU"; bar.RightText = "62%"; bar.Value = 62;
        bar.BarColor = Theme.Accent;
        s.Block(bar, Theme.S(38));
        s.Body("圆角 token：卡片 / 分区 14px、按钮 / 输入框 10px、徽章 999（胶囊）。深色主题沿用《游戏启动器》的紫→蓝→青。");
        s.Width = W;
        s.DoLayout();
        s.Location = new Point(X, Theme.S(84));
        f.Controls.Add(s);

        // 卡片网格：真封面 + 生成卡
        int ch = s.Height + Theme.S(84) + Theme.S(18);
        var cards = new DlssgGame[] {
            G("Cyberpunk 2077", ""),
            G("尸姬之梦", ""),
            G("Wannabe Galgame", "steam_4262610") };
        for (int i = 0; i < cards.Length; i++)
        {
            var c = new GameCard(cards[i], Theme.S(132), Theme.S(198));
            c.Location = new Point(X + i * (Theme.S(132) + Theme.S(28)), ch);
            f.Controls.Add(c);
        }
        // 选中态
        Console.WriteLine("card height = " + ch + " + 266 = " + (ch + Theme.S(266)));

        f.Height = Math.Min(740, ch + Theme.S(280));
        f.ClientSize = new Size(f.ClientSize.Width, f.Height);
        Shot(f, "style_" + mode + ".png");
        f.Close();
    }

    // 真实主窗体：渲染指定页（验证标题头/卡片没把布局搞坏）
    static void RealPage(string mode, int page, string file, int hgt, int scrollTo)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, hgt);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        try
        {
            MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi != null) mi.Invoke(f, new object[] { page });
        }
        catch (Exception ex) { Console.WriteLine("switch err: " + ex.Message); }
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }
        // 可选：把体检表滚到第 N 行再截图 —— 否则第 20 行之后的问题（比如 HAGS 那一行）
        // 永远落在可视区外，只能靠探针断言看，看不到实际长相。
        if (scrollTo >= 0)
        {
            FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
            ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
            if (lv == null) Console.WriteLine("找不到 lvOpt（滚动被跳过）");
            else if (scrollTo >= lv.Items.Count) Console.WriteLine("scrollTo 超出范围（共 " + lv.Items.Count + " 行）");
            else { lv.EnsureVisible(scrollTo); lv.Refresh(); Console.WriteLine("scroll -> " + lv.Items[scrollTo].Text); }
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(30); }
        }
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(Path.Combine(OUT, file), ImageFormat.Png);
        }
        Console.WriteLine("saved " + file);
        f.Close();
    }

    // 封面多路径回退实测：对之前下不到封面的 appid 逐个重试
    static void Covers()
    {
        string[][] t = new string[][] {
            new string[] { "2483190", "steam_2483190" },
            new string[] { "3527290", "steam_3527290" },
            new string[] { "3548580", "steam_3548580" },
            new string[] { "4262610", "steam_4262610" } };
        foreach (string[] a in t)
        {
            string f = CoverArt.CardFile(a[1]);
            if (File.Exists(f)) File.Delete(f);
            bool ok = CoverArt.DownloadSteam(a[0], a[1]);
            long sz = File.Exists(f) ? new FileInfo(f).Length : 0;
            Console.WriteLine((ok ? "OK  " : "FAIL") + "  appid " + a[0] + "  ->  " + sz + " bytes");
        }
    }

    // 无封面游戏：联网取图实测（会真实写入 covers 目录）
    static void WebCovers()
    {
        string[] names = new string[] { "原神", "绝区零", "鸣潮", "卡拉彼丘", "无畏契约" };
        foreach (string n in names)
        {
            var g = new DlssgGame();
            g.Title = n; g.Id = ""; g.Platform = "local";
            string err = CoverArt.SearchWebCover(g);
            Console.WriteLine((err.Length == 0 ? "OK   " : "FAIL ") + n + "   " + err);
        }
    }

    // 打印卡片网格真实几何：用于核对"行内居中"到底有没有生效（比看截图靠谱）
    static void Geom(string mode, int page)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1000);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }

        CardGrid grid = FindGrid(f);
        if (grid == null) { Console.WriteLine("找不到 CardGrid"); f.Close(); return; }
        Console.WriteLine("form        W=" + f.ClientSize.Width);
        Console.WriteLine("grid  Left=" + grid.Left + "  Width=" + grid.Width
                          + "  ClientW=" + grid.ClientSize.Width + "  DisplayW=" + grid.DisplayRectangle.Width);
        Console.WriteLine("grid  Padding=" + grid.Padding + "  CellW=" + grid.CellW + "  CellH=" + grid.CellH
                          + "  Count=" + grid.Controls.Count + "  Height=" + grid.Height);
        int perRow = Math.Max(1, grid.ClientSize.Width / grid.CellW);
        Console.WriteLine("perRow=" + perRow + "  leftover=" + (grid.ClientSize.Width - perRow * grid.CellW)
                          + "  期望 Padding.Left=" + Math.Max(0, (grid.ClientSize.Width - perRow * grid.CellW) / 2));
        for (int i = 0; i < Math.Min(6, grid.Controls.Count); i++)
        {
            Control c = grid.Controls[i];
            Console.WriteLine("  card[" + i + "] Left=" + c.Left + " Top=" + c.Top
                              + " W=" + c.Width + " H=" + c.Height + " Margin=" + c.Margin);
        }
        f.Close();
    }

    // 原地换主题实测：浅色起 → 调 DoSwitchTheme(true) → 再渲染同样的页面
    static void ThemeSwap(int page)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = "light";
        Theme.SetMode("light");
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1056, 739);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        MethodInfo sw = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (sw != null) sw.Invoke(f, new object[] { page });
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }

        string before = Path.Combine(OUT, "swap_before_p" + page + ".png");
        using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(before, ImageFormat.Png); }

        MethodInfo dst = typeof(MainForm).GetMethod("DoSwitchTheme", BindingFlags.NonPublic | BindingFlags.Instance);
        Console.WriteLine("DoSwitchTheme 找到: " + (dst != null));
        if (dst != null) dst.Invoke(f, new object[] { true });
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }

        string after = Path.Combine(OUT, "swap_after_p" + page + ".png");
        using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(after, ImageFormat.Png); }
        Console.WriteLine("Theme.Dark = " + Theme.Dark + "   页面数=" + f.Controls.Count);
        Console.WriteLine("saved " + before);
        Console.WriteLine("saved " + after);
        f.Close();
    }

    // 命中测试：复现"切全屏后点不动下面那排游戏"。
    // 关键在"先按窗口尺寸布局、再改成全屏尺寸"——这才是用户的操作顺序；
    // 一开始就用全屏尺寸启动，走的不是同一条布局路径。
    // 每张卡只在**可见部分**的中心探针（整张卡都在视口外时不算失败）。
    static void Hit(int page, int w0, int h0, int w1, int h1)
    {
        var cfg = Config.Load(Program.ConfigPath);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(w0, h0);
        f.Show();
        Pump(30);
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        Pump(50);

        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        if (arr == null || page >= arr.Length) { Console.WriteLine("取 pages 失败"); f.Close(); return; }
        Pg pg = arr[page];
        CardGrid grid = FindGrid(pg);
        if (grid == null) { Console.WriteLine("找不到 CardGrid"); f.Close(); return; }

        Report("① 窗口 " + w0 + "x" + h0, f, pg, grid);

        f.Size = new Size(w1, h1);                       // ← 用户"切全屏"
        Pump(60);
        Report("② 改成 " + w1 + "x" + h1 + "（切全屏）", f, pg, grid);

        ScrollTo(pg, int.MaxValue);
        Report("③ 全屏下滚到底", f, pg, grid);

        f.Size = new Size(w0, h0);                       // 退回窗口尺寸
        Pump(60);
        Report("④ 退回 " + w0 + "x" + h0, f, pg, grid);

        ScrollTo(pg, int.MaxValue);
        Report("⑤ 窗口下滚到底", f, pg, grid);
        f.Close();
    }

    static void Pump(int loops) { for (int i = 0; i < loops; i++) { Application.DoEvents(); Thread.Sleep(40); } }

    static bool ScrollVisible(Pg pg)
    {
        foreach (Control c in pg.Controls) if (c is VScrollBar) return c.Visible;
        return false;
    }

    static string SbState(Pg pg)
    {
        foreach (Control c in pg.Controls)
        {
            VScrollBar b = c as VScrollBar;
            if (b != null) return "VScrollBar Min=" + b.Minimum + " Max=" + b.Maximum
                                + " Large=" + b.LargeChange + " Value=" + b.Value + " Vis=" + b.Visible;
        }
        return "无 VScrollBar";
    }

    static void ScrollTo(Pg pg, int v) { pg.SetScrollY(v); Pump(25); }

    static void Report(string label, Form f, Pg pg, CardGrid grid)
    {
        int content = 0;
        foreach (Control c in pg.Controls) if (c.Bottom > content) content = c.Bottom;
        Console.WriteLine("== " + label);
        Console.WriteLine("   Pg " + pg.ClientSize + "  子控件最大Bottom=" + content
            + "  ScrollY=" + pg.ScrollY + "  AutoScroll=" + pg.AutoScroll);
        Console.WriteLine("   可见滚动条=" + ScrollVisible(pg) + "  " + SbState(pg));
        Console.WriteLine("   CardGrid " + grid.Bounds + "  Count=" + grid.Controls.Count);
        int miss = 0, inv = 0, shown = 0;
        foreach (Control c in grid.Controls)
        {
            GameCard gc = c as GameCard;
            if (gc == null) continue;
            if (shown++ >= 16) break;
            string r = ProbeCard(f, pg, gc);
            if (r == "不可见") { inv++; continue; }
            if (r != "OK") { miss++; Console.WriteLine("      " + Pad(gc.Game.Title) + " → " + r); }
        }
        Console.WriteLine("   命中失败 " + miss + " 张 · 视口外 " + inv + " 张");
    }

    static string Pad(string s) { s = s == null ? "?" : s; return s.Length >= 22 ? s.Substring(0, 22) : s + new string(' ', 22 - s.Length); }

    // 只探"卡片与视口的交集"的中心：整张卡在视口外时返回"不可见"，不算命中失败
    static string ProbeCard(Form f, Pg pg, GameCard gc)
    {
        Rectangle scr = gc.RectangleToScreen(gc.ClientRectangle);
        Rectangle pgScr = pg.RectangleToScreen(pg.ClientRectangle);
        Rectangle vis = Rectangle.Intersect(scr, pgScr);
        if (vis.Width <= 3 || vis.Height <= 3) return "不可见";
        Point pt = new Point(vis.Left + vis.Width / 2, vis.Top + Math.Min(24, vis.Height / 2));
        Control hit = Deepest(f, pt);
        if (hit == gc) return "OK";
        return "MISS → " + (hit == null ? "null（没控件）" : hit.GetType().Name
               + (hit is GameCard ? "「" + ((GameCard)hit).Game.Title + "」" : ""));
    }

    // 从窗体逐层往下问"这个点最深处是谁"，与 WinForms/WM_NCHITTEST 的判定一致
    static Control Deepest(Control root, Point screenPt)
    {
        Control cur = root;
        for (int i = 0; i < 40; i++)
        {
            Point p = cur.PointToClient(screenPt);
            Control next = cur.GetChildAtPoint(p, GetChildAtPointSkip.Invisible);
            if (next == null) return cur == root ? null : cur;
            cur = next;
        }
        return cur;
    }

    // 真实窗口表面捕获：DrawToBitmap 会强制整棵树重绘，看不见"没被重绘的残影"，
    // 所以这里直接 GetDC + BitBlt 读窗口表面（残影会原样出现），用来复现"全屏后顶部一条卡片残影"。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);

    static void Capture(string mode, int page, int w0, int h0, int w1, int h1, string tag)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);              // 必须真的在桌面上，GetDC 才拿得到表面
        f.Size = new Size(w0, h0);
        f.Show();
        Pump(30);
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        Pump(40);
        Grab(f, "cap_" + mode + "_" + tag + "_a.png");

        f.Size = new Size(w1, h1);                  // ← 模拟"切全屏"
        Pump(40);
        Grab(f, "cap_" + mode + "_" + tag + "_b.png");

        f.WindowState = FormWindowState.Maximized;
        Pump(40);
        Grab(f, "cap_" + mode + "_" + tag + "_c.png");

        f.WindowState = FormWindowState.Normal;
        Pump(40);
        Grab(f, "cap_" + mode + "_" + tag + "_d.png");
        f.Close();
    }

    static void Grab(Form f, string file)
    {
        Rectangle cr = f.RectangleToScreen(f.ClientRectangle);
        using (var bmp = new Bitmap(cr.Width, cr.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr src = GetDC(f.Handle);
                IntPtr dst = g.GetHdc();
                BitBlt(dst, 0, 0, cr.Width, cr.Height, src, 0, 0, 0x00CC0020);
                g.ReleaseHdc(dst);
                ReleaseDC(f.Handle, src);
            }
            bmp.Save(Path.Combine(OUT, file), ImageFormat.Png);
        }
        Console.WriteLine("grab " + file + "  client=" + f.ClientSize + "  state=" + f.WindowState);
    }

    // 残影扫描：把「切全屏」的各种真实时序都跑一遍，每次都读**真实窗口表面**，
    //  统计"顶部区域出现一整行高饱和像素"的可疑行数（= 被压扁的卡片带）。
    //  为什么不能用 DrawToBitmap 验收：它会把整棵树重绘到一张**新**位图上，残影类问题在它面前完全隐形。
    static void GhostScan()
    {
        Console.WriteLine("=== 残影差分测试（抓两次表面，中间强制整树重绘；差异像素 >3000 判为残影）===");
        Console.WriteLine("  页 3 = 游戏库（卡片网格所在页）；「差异像素」是采样后的计数（每 2 像素采一次）");

        RunSeq("⓪ 基线：什么都不做", delegate(MainForm f)
        {
            Pump(20);
        });

        RunSeq("① 窗口 1180x1000 → 最大化", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 3); Pump(40);
            f.WindowState = FormWindowState.Maximized; Pump(30);
        });

        RunSeq("② 最大化 → 还原 → 再最大化", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 3); Pump(30);
            f.WindowState = FormWindowState.Maximized; Pump(30);
            f.WindowState = FormWindowState.Normal; Pump(30);
            f.WindowState = FormWindowState.Maximized; Pump(30);
        });

        RunSeq("③ 最大化 → 最小化 → 还原", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 3); Pump(30);
            f.WindowState = FormWindowState.Maximized; Pump(30);
            f.WindowState = FormWindowState.Minimized; Pump(20);
            f.WindowState = FormWindowState.Maximized; Pump(30);
        });

        RunSeq("④ 先最大化 → 再切到游戏库页", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30);
            f.WindowState = FormWindowState.Maximized; Pump(30);
            Switch(f, 3); Pump(40);
        });

        RunSeq("⑤ 启动即最大化 → 切到游戏库页", delegate(MainForm f)
        {
            f.WindowState = FormWindowState.Maximized; Pump(40);
            Switch(f, 3); Pump(40);
        });

        RunSeq("⑥ 一步跳到全屏尺寸（不经过 WindowState）", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 3); Pump(30);
            SetSize(f, 1920, 1009); Pump(30);
        });

        RunSeq("⑦ 先压成 400x300 → 再跳全屏", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 3); Pump(30);
            SetSize(f, 400, 300); Pump(25);
            SetSize(f, 1920, 1009); Pump(30);
        });

        RunSeq("⑧ 在别的页上切全屏 → 再切回游戏库页", delegate(MainForm f)
        {
            SetSize(f, 1180, 1000); Pump(30); Switch(f, 0); Pump(30);
            f.WindowState = FormWindowState.Maximized; Pump(30);
            Switch(f, 3); Pump(40);
        });
    }

    static void SetSize(Form f, int w, int h)
    {
        if (f.WindowState != FormWindowState.Normal) f.WindowState = FormWindowState.Normal;
        f.Size = new Size(w, h);
    }

    static void Switch(MainForm f, int page)
    {
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
    }

    static void RunSeq(string title, Action<MainForm> steps)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = "light";
        Theme.SetMode("light");
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1180, 1000);
        f.Show();
        Pump(20);
        try { steps(f); }
        catch (Exception ex) { Console.WriteLine("  " + title + "  步骤异常: " + ex.Message); }

        // ★ 差分测试：同一状态先抓真实表面，再强制整树重绘，然后**再抓一次**。
        //   应用正确时两张图应当一致；有残影时 A 会带着"没被擦掉的旧像素"。
        //   这比"数高饱和行"可靠得多 —— 不需要事先知道残影长什么样、出现在哪。
        using (Bitmap a = GrabBmp(f))
        {
            try { f.Invalidate(true); f.Update(); } catch { }
            Pump(6);
            using (Bitmap b = GrabBmp(f))
            {
                int px, firstY, lastY, badRows, worst, worstY;
                DiffRows(a, b, out px, out firstY, out lastY, out badRows, out worst, out worstY);
                string verdict = (px > 3000) ? "★ 有残影" : (px > 400 ? "（轻微差异）" : "干净");
                Console.WriteLine(string.Format(
                    "  {0,-32} client={1,-12} 差异像素={2,-7} 可疑行={3,-4} 峰值行={4}({5} px) y={6}..{7}   {8}",
                    title, f.ClientSize, px, badRows, worstY, worst, firstY, lastY, verdict));
                if (px > 3000)
                {
                    string tag = "ghost_" + Math.Abs(title.GetHashCode()) % 10000 + "_";
                    a.Save(Path.Combine(OUT, tag + "A.png"), ImageFormat.Png);
                    b.Save(Path.Combine(OUT, tag + "B.png"), ImageFormat.Png);
                    Console.WriteLine("     已存证: " + tag + "A.png / " + tag + "B.png");
                }
            }
        }
        f.Close();
        Pump(10);
    }

    static Bitmap GrabBmp(Form f)
    {
        Rectangle cr = f.RectangleToScreen(f.ClientRectangle);
        Bitmap bmp = new Bitmap(Math.Max(1, cr.Width), Math.Max(1, cr.Height));
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr src = GetDC(f.Handle);
            IntPtr dst = g.GetHdc();
            BitBlt(dst, 0, 0, cr.Width, cr.Height, src, 0, 0, 0x00CC0020);
            g.ReleaseHdc(dst);
            ReleaseDC(f.Handle, src);
        }
        return bmp;
    }

    // 逐行比对两张同尺寸表面（只比内容区，避开左侧导航与右缘），输出差异统计
    static void DiffRows(Bitmap a, Bitmap b,
        out int pixels, out int firstY, out int lastY, out int badRows, out int worst, out int worstY)
    {
        pixels = 0; firstY = -1; lastY = -1; badRows = 0; worst = 0; worstY = -1;
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        for (int y = 0; y < h; y += 2)
        {
            int n = 0;
            for (int x = 200; x < w - 40; x += 2)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.R - q.R) > 24 || Math.Abs(p.G - q.G) > 24 || Math.Abs(p.B - q.B) > 24) n++;
            }
            if (n > 3)
            {
                badRows++;
                pixels += n;
                if (firstY < 0) firstY = y;
                lastY = y;
            }
            if (n > worst) { worst = n; worstY = y; }
        }
    }

    // 卡片摆放检查：用户截图里那条"被压到 11px 高的封面带"横在标题与搜索框之间。
    //  它不是残影（差分测试抓不到），而更像**真实控件被摆到错误的 Y / 高度塌陷**。
    //  所以这里直接量：网格在页面里的 Y 与高度、卡片有没有跑到搜索框上方。
    static void GhostMatrix()
    {
        Console.WriteLine("=== 卡片摆放矩阵（每种尺寸：窗口 → 切游戏库页 → 最大化，然后量几何）===");
        int[,] sizes = new int[,] {
            {1752, 566}, {1744, 537}, {1180, 1000}, {1056, 739}, {1500, 600}, {1900, 560},
        };
        for (int i = 0; i < sizes.GetLength(0); i++)
            ProbeSize(sizes[i, 0], sizes[i, 1]);
    }

    static void ProbeSize(int w0, int h0)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = "light";
        Theme.SetMode("light");
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(w0, h0);
        f.Show();
        Pump(25);
        Switch(f, 3);
        Pump(45);
        f.WindowState = FormWindowState.Maximized;
        Pump(45);

        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        Pg pg = (arr != null && arr.Length > 3) ? arr[3] : null;
        Console.WriteLine();
        Console.WriteLine("--- 窗口 " + w0 + "x" + h0 + "  →  最大化后 client=" + f.ClientSize);
        if (pg == null) { Console.WriteLine("  取不到 Pg[3]"); f.Close(); return; }

        CardGrid grid = FindGrid(pg);
        if (grid == null) { Console.WriteLine("  找不到 CardGrid"); f.Close(); return; }
        Point gp = pg.PointToClient(grid.PointToScreen(Point.Empty));
        Console.WriteLine("  Pg      : " + pg.ClientSize + "  ScrollY=" + pg.ScrollY);
        Console.WriteLine("  CardGrid: Bounds=" + grid.Bounds + "  → Pg 坐标 Y=" + gp.Y + " H=" + grid.Height
                          + "  卡片数=" + grid.Controls.Count);

        // 找出搜索框（工具条里的输入框）的 Y，任何卡片跑到它上面就是异常
        int searchTop = int.MaxValue;
        foreach (Control c in AllControls(pg))
            if (c is RoundField || c.GetType().Name == "RoundField")
                searchTop = Math.Min(searchTop, pg.PointToClient(c.PointToScreen(Point.Empty)).Y);
        Console.WriteLine("  搜索框  : Pg 坐标 Y=" + searchTop);

        int above = 0, aboveGrid = 0;
        foreach (Control c in AllControls(pg))
        {
            GameCard gc = c as GameCard;
            if (gc == null) continue;
            int y = pg.PointToClient(gc.PointToScreen(Point.Empty)).Y;
            if (y < searchTop) above++;
            Rectangle gsr = pg.RectangleToClient(gc.RectangleToScreen(gc.ClientRectangle));
            if (gsr.Bottom <= gp.Y + 2 || gsr.Top >= gp.Y + grid.Height - 2) aboveGrid++;
        }
        Console.WriteLine("  卡片跑到搜索框上方: " + above + " 张   卡片落在网格矩形之外: " + aboveGrid + " 张"
                          + (above > 0 || aboveGrid > 0 ? "   ★ 异常" : "   正常"));
        f.Close();
        Pump(10);
    }

    static List<Control> AllControls(Control root)
    {
        List<Control> list = new List<Control>();
        CollectAll(root, list);
        return list;
    }

    static void CollectAll(Control root, List<Control> into)
    {
        foreach (Control c in root.Controls)
        {
            into.Add(c);
            CollectAll(c, into);
        }
    }

    // 自愈回归测试：把用户截图里那条"封面带"的状态**人为造出来**，验证能自己纠回来。
    //  两种造法分别对应两条修复路径：
    //   A) 只把分区搬走（高度不变 → 三个缓存数全都一致）→ 旧代码会命中早退闸，永久卡住；
    //      新代码的几何自检会发现"位置没落到算出来的地方"并强制重排。
    //   B) 把分区高度压成 11px（高度变了 → 缓存不一致，本来就会重排）→ 验证 LayoutOnce 的塌陷纠正。
    static void SelfHeal()
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = "light";
        Theme.SetMode("light");
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1180, 1000);
        f.Show();
        Pump(25);
        Switch(f, 3);
        Pump(45);
        f.WindowState = FormWindowState.Maximized;
        Pump(45);

        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        Pg pg = (arr != null && arr.Length > 3) ? arr[3] : null;
        if (pg == null) { Console.WriteLine("取不到 Pg[3]"); f.Close(); return; }

        // 找到"装着卡片网格的那个分区"
        Sec host = null; CardGrid grid = null;
        foreach (Control c in AllControls(pg))
        {
            Sec s = c as Sec;
            if (s == null) continue;
            foreach (Control k in s.Controls)
                if (k is CardGrid) { host = s; grid = (CardGrid)k; break; }
            if (host != null) break;
        }
        Console.WriteLine("=== 自愈回归测试 ===");
        if (host == null || grid == null) { Console.WriteLine("  找不到卡片网格所属分区"); f.Close(); return; }
        Console.WriteLine("  起始：分区 Top=" + host.Top + " H=" + host.Height
                          + "  网格 Top=" + grid.Top + " H=" + grid.Height);

        // ---- A) 只搬位置，不动高度（缓存三数保持一致）----
        int expTop = host.Top;
        int expGridTop = grid.Top;
        host.Location = new Point(host.Left, Theme.S(18));
        grid.Location = new Point(grid.Left, Theme.S(4));
        Pump(6);
        int brokenTop = host.Top;
        pg.Reflow();
        Pump(6);
        Console.WriteLine("  A) 只搬位置：破坏后 分区Top=" + brokenTop + " → Reflow 后 Top=" + host.Top
                          + "（期望 " + expTop + "）  网格 Top=" + grid.Top + "（期望 " + expGridTop + "）");
        Console.WriteLine((host.Top == expTop && grid.Top == expGridTop)
            ? "     [PASS] 位置型坏布局已自愈" : "     [FAIL] 没有自愈");

        // ---- B) 把分区高度压成 11px ----
        int expH = host.Height;
        host.Height = 11;
        Pump(6);
        pg.Reflow();
        Pump(6);
        Console.WriteLine("  B) 压扁到 11px：Reflow 后 H=" + host.Height + "（期望 >= " + Theme.S(30) + "）");
        Console.WriteLine((host.Height >= Theme.S(30) && Math.Abs(host.Height - expH) <= 2)
            ? "     [PASS] 塌陷高度已自愈" : "     [FAIL] 高度没有恢复");

        // ---- C) 再整体校验一次：卡片有没有在网格之外 ----
        int above = 0;
        foreach (Control c in AllControls(grid))
        {
            if (!(c is GameCard)) continue;
            Point p = pg.PointToClient(c.PointToScreen(Point.Empty));
            Point gp = pg.PointToClient(grid.PointToScreen(Point.Empty));
            if (p.Y < gp.Y - 2 || p.Y > gp.Y + grid.Height) above++;
        }
        Console.WriteLine("  C) 越位卡片 " + above + " 张" + (above == 0 ? "   [PASS]" : "   [FAIL]"));
        f.Close();
        Pump(10);
    }

    // 方案推荐验证：选中指定游戏 → 刷新帧生成页 → 渲染
    //  用法：StyleProbe.exe light recgame 2 绝区零
    static void RecGame(string mode, int page, string needle)
    {
        var cfg = Config.Load(Program.ConfigPath);
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1000);
        f.Show();
        Pump(30);

        FieldInfo gf = typeof(MainForm).GetField("games", BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo sf = typeof(MainForm).GetField("selected", BindingFlags.NonPublic | BindingFlags.Instance);
        List<DlssgGame> list = gf == null ? null : gf.GetValue(f) as List<DlssgGame>;
        DlssgGame hit = null;
        if (list != null)
            foreach (DlssgGame g in list)
                if (g != null && g.Title != null
                    && g.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { hit = g; break; }
        Console.WriteLine("选中的游戏: " + (hit == null ? ("未找到含「" + needle + "」的（列表 " + (list == null ? 0 : list.Count) + " 款）") : hit.Title));
        if (hit != null) sf.SetValue(f, hit);

        MethodInfo rf = typeof(MainForm).GetMethod("RefreshFgSummary", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo sw = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (sw != null) sw.Invoke(f, new object[] { page });
        Pump(30);
        if (rf != null) rf.Invoke(f, null);
        Pump(15);

        string outp = Path.Combine(OUT, "rec_" + mode + "_" + needle + ".png");
        using (var b = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
            b.Save(outp, ImageFormat.Png);
        }
        Console.WriteLine("saved " + outp);
        f.Close();
    }

    // 诊断：把库里每条游戏的字段与推荐分类都打出来
    //  用法：StyleProbe.exe light recdump 2
    static void RecDump(string mode)
    {
        var cfg = Config.Load(Program.ConfigPath);
        Program.Cfg = cfg;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1000);
        f.Show();
        Pump(30);

        FieldInfo gf = typeof(MainForm).GetField("games", BindingFlags.NonPublic | BindingFlags.Instance);
        List<DlssgGame> list = gf == null ? null : gf.GetValue(f) as List<DlssgGame>;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("cat           \tExe\tTitle\tDir\tFG\tFSR3\tUp\tPlan");
        if (list != null)
        {
            foreach (DlssgGame g in list)
            {
                string cat = Dlssg.RecommendCategory(g);
                Dlssg.Rec rec = Dlssg.BuildRec(g);
                sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}",
                    cat, g.Exe, g.Title, g.Dir,
                    g.HasFrameGen ? "Y" : "-", g.HasFsr3 ? "Y" : "-", g.HasUpscaler ? "Y" : "-",
                    rec.Head));
            }
        }
        sb.AppendLine("共 " + (list == null ? 0 : list.Count) + " 条");
        string outp = Path.Combine(OUT, "recdump.txt");
        File.WriteAllText(outp, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine("saved " + outp);
        f.Close();
    }

    // 接线审计：把每个页面里所有可交互控件的"是否挂了事件"打出来。
    //  为什么需要：界面改完之后最容易出现"按钮画出来了、点了没反应"（事件没接上），
    //  编译和渲染都看不出来。这个模式一次列全。
    //  用法：StyleProbe.exe light wiredump
    // 事件是否挂了。两种形态都要查：
    //  ① 框架内置事件（Click / CheckedChanged…）：在基类里有 `private static readonly object Event<Name>`，
    //     真实订阅存在 Component.Events(EventHandlerList) 里。
    //  ② 自定义控件显式声明的事件（RoundCombo.SelectedIndexChanged）：编译成**同名的实例字段**，
    //     没有 Event<Name> 这种键 —— 上一版只查①，于是 RoundCombo 全部误报成"未接线"。
    static bool WiredAny(object ctrl, params string[] names)
    {
        if (ctrl == null) return false;
        PropertyInfo pi = typeof(System.ComponentModel.Component).GetProperty("Events",
            BindingFlags.NonPublic | BindingFlags.Instance);
        System.ComponentModel.EventHandlerList list =
            pi == null ? null : pi.GetValue(ctrl, null) as System.ComponentModel.EventHandlerList;

        foreach (string nm in names)
        {
            for (Type t = ctrl.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                // ① 框架内置
                FieldInfo key = t.GetField("Event" + nm, BindingFlags.NonPublic | BindingFlags.Static);
                if (key != null && list != null)
                {
                    object k = key.GetValue(null);
                    if (list[k] != null) return true;
                }
                // ② 显式事件（实例字段，字段名 = 事件名）
                FieldInfo inst = t.GetField(nm,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (inst != null && inst.GetValue(ctrl) != null) return true;
            }
        }
        return false;
    }

    static void WireDump(string mode)
    {
        var cfg = Config.Load(Program.ConfigPath);
        Program.Cfg = cfg;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1000);
        f.Show();
        Pump(30);

        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        string[] names = { "仪表盘", "性能优化", "帧生成", "游戏库", "实时监控", "说明", "设置" };

        var sb = new System.Text.StringBuilder();
        int dead = 0, total = 0;

        if (arr != null)
        {
            for (int pi = 0; pi < arr.Length; pi++)
            {
                Pg pg = arr[pi];
                if (pg == null) continue;
                sb.AppendLine("=== [" + pi + "] " + (pi < names.Length ? names[pi] : "?") + " ===");
                int n = 0;
                foreach (Control c in AllControls(pg))
                {
                    bool isBtn = c is FlatBtn;
                    bool isChk = c is RoundCheck;
                    bool isCmb = c is RoundCombo;
                    bool isFld = c is RoundField;
                    bool isLv = c is ListView;
                    if (!isBtn && !isChk && !isCmb && !isFld && !isLv) continue;

                    string txt = "";
                    try { txt = c.Text ?? ""; } catch { }
                    if (txt.Length > 40) txt = txt.Substring(0, 40) + "…";

                    string flags = "";
                    if (isBtn) { bool w = WiredAny(c, "Click"); flags = w ? "Click=Y" : "Click=**N**"; if (!w) dead++; }
                    else if (isChk) { bool w = WiredAny(c, "CheckedChanged", "CheckStateChanged", "Click"); flags = w ? "Chk=Y" : "Chk=**N**"; if (!w) dead++; }
                    else if (isCmb) { bool w = WiredAny(c, "SelectedIndexChanged", "Click"); flags = w ? "Sel=Y" : "Sel=**N**"; if (!w) dead++; }
                    else flags = "-";

                    total++;
                    n++;
                    sb.AppendLine(string.Format("  {0,-11} {1,-34} {2,-11}{3}{4}  @({5},{6}) {7}x{8}",
                        c.GetType().Name, txt, flags,
                        c.Visible ? "" : " [隐藏]", c.Enabled ? "" : " [禁用]",
                        c.Left, c.Top, c.Width, c.Height));
                }
                if (n == 0) sb.AppendLine("  （无可交互控件）");
                sb.AppendLine();
            }
        }
        sb.AppendLine("控件总数 " + total + "，其中疑似未接线 " + dead);
        string outp = Path.Combine(OUT, "wiredump.txt");
        File.WriteAllText(outp, sb.ToString(), new System.Text.UTF8Encoding(false));
        Console.WriteLine("saved " + outp + "  控件 " + total + " / 疑似死控件 " + dead);
        f.Close();
    }

    // 页面布局链 dump：精确定位"上下多出一大块"到底出在哪一层
    static void GeomPage(string mode, int page)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1056, 739);          // 用户窗口的实际尺寸
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        for (int i = 0; i < 50; i++) { Application.DoEvents(); Thread.Sleep(50); }

        Pg pg = null;
        try
        {
            FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
            if (pf != null)
            {
                Pg[] arr = pf.GetValue(f) as Pg[];
                if (arr != null && page >= 0 && page < arr.Length) pg = arr[page];
            }
        }
        catch (Exception ex) { Console.WriteLine("取 pages 失败: " + ex.Message); }
        if (pg == null) { Console.WriteLine("找不到 Pg（pages[" + page + "]）"); f.Close(); return; }

        Console.WriteLine("== Pg  ClientSize=" + pg.ClientSize + "  DisplayRect=" + pg.DisplayRectangle);
        Console.WriteLine("   AutoScroll=" + pg.AutoScroll + "  VScroll.Visible=" + pg.VerticalScroll.Visible
                          + "  VScroll.Maximum=" + pg.VerticalScroll.Maximum + "  LargeChange=" + pg.VerticalScroll.LargeChange
                          + "  AutoScrollMinSize=" + pg.AutoScrollMinSize + "  Padding=" + pg.Padding);
        int contentBottom = 0;
        foreach (Control c in pg.Controls)
        {
            Console.WriteLine(string.Format("   child {0,-12} Loc={1,-14} Size={2,-12} Bottom={3}",
                c.GetType().Name, c.Location, c.Size, c.Bottom));
            if (c.Bottom > contentBottom) contentBottom = c.Bottom;
        }
        Console.WriteLine("   子控件最大 Bottom = " + contentBottom + "  （Pg 客户区高 " + pg.ClientSize.Height + "）");
        Console.WriteLine("   → 可滚动总量 = " + (contentBottom - pg.ClientSize.Height) + " px");

        // 关键：找出"分区的实际高度"与"分区内容底边"的差 —— 差就是分区内部的空白
        foreach (Control c in pg.Controls)
        {
            Sec s = c as Sec;
            if (s == null) continue;
            int innerBottom = 0;
            foreach (Control ch in s.Controls) if (ch.Bottom > innerBottom) innerBottom = ch.Bottom;
            Console.WriteLine(string.Format("   Sec 「{0}」 Height={1}  内容底={2}  内部空白={3}",
                SecTitle(s), s.Height, innerBottom, s.Height - innerBottom));
            CardGrid cg = FindGrid(s);
            if (cg != null)
                Console.WriteLine("       └ CardGrid Height=" + cg.Height + " Count=" + cg.Controls.Count
                                  + " 期望=" + (((cg.Controls.Count + Math.Max(1, (cg.ClientSize.Width - cg.PadPx * 2) / cg.CellW) - 1)
                                    / Math.Max(1, (cg.ClientSize.Width - cg.PadPx * 2) / cg.CellW)) * cg.CellH + cg.PadPx * 2));
        }
        // 递归找"谁把滚动区撑大了"：打印所有后代里底边超过内容底的控件
        Console.WriteLine("   -- 可疑后代（在 Pg 坐标系里的底边 > " + contentBottom + "）--");
        WalkDeep(pg, pg, contentBottom, 0);
        Console.WriteLine("   再调一次 Pg.Reflow() 之后 DisplayRect=" + AfterReflow(pg));
        Console.WriteLine("   PerformLayout 之后 DisplayRect=" + AfterPL(pg));
        f.Close();
    }

    static Rectangle AfterPL(Pg pg) { pg.PerformLayout(); Application.DoEvents(); return pg.DisplayRectangle; }
    static Rectangle AfterReflow(Pg pg)
    {
        MethodInfo mi = typeof(Pg).GetMethod("Reflow", BindingFlags.Public | BindingFlags.Instance);
        if (mi != null) mi.Invoke(pg, null);
        Application.DoEvents(); Thread.Sleep(120); Application.DoEvents();
        return pg.DisplayRectangle;
    }

    static void WalkDeep(Control root, Control pg, int limit, int depth)
    {
        foreach (Control c in root.Controls)
        {
            Point abs = pg.PointToClient(c.PointToScreen(Point.Empty));
            int bottom = abs.Y + c.Height;
            if (bottom > limit)
                Console.WriteLine("      " + new string(' ', depth * 2) + c.GetType().Name
                                  + "  Y=" + abs.Y + "  H=" + c.Height + "  Bottom=" + bottom
                                  + "  Dock=" + c.Dock + "  Visible=" + c.Visible);
            WalkDeep(c, pg, limit, depth + 1);
        }
    }

    static string SecTitle(Sec s)
    {
        try
        {
            FieldInfo fi = typeof(Sec).GetField("title", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) { object v = fi.GetValue(s); if (v != null) return v.ToString(); }
        }
        catch { }
        return "?";
    }

    static Pg FindPg(Control root)
    {
        foreach (Control c in root.Controls)
        {
            Pg p = c as Pg;
            if (p != null) return p;
            Pg sub = FindPg(c);
            if (sub != null) return sub;
        }
        return null;
    }

    static CardGrid FindGrid(Control root)
    {
        foreach (Control c in root.Controls)
        {
            CardGrid g = c as CardGrid;
            if (g != null) return g;
            CardGrid sub = FindGrid(c);
            if (sub != null) return sub;
        }
        return null;
    }

    static void CloseDlg(string mode)
    {
        var dlg = new Form();
        dlg.Text = "关闭窗口";
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MaximizeBox = false; dlg.MinimizeBox = false;
        dlg.ShowInTaskbar = false;
        dlg.ShowIcon = false;
        dlg.BackColor = Theme.Bg;
        dlg.ForeColor = Theme.Text;
        dlg.Font = Theme.F(9f);
        dlg.AutoScaleMode = AutoScaleMode.None;
        dlg.ClientSize = new Size(Theme.S(420), Theme.S(268));
        try { Theme.ApplyChrome(dlg.Handle); } catch { }

        var pg = new Pg();
        pg.Dock = DockStyle.Fill;
        var sec = new Sec("关闭窗口");
        sec.Body("游戏联动、硬件告警、帧生成诊断需要程序保持后台运行。");
        var rbTray = new RoundRadio(); Theme.StyleRadio(rbTray, "最小化到通知区域图标（后台继续运行）");
        var rbExit = new RoundRadio(); Theme.StyleRadio(rbExit, "退出程序（联动与告警一并停止）");
        rbTray.Checked = true; rbExit.Checked = false;
        sec.Pair(null, rbTray, 0, null, null, 0);
        sec.Pair(null, rbExit, 0, null, null, 0);
        var cbRemember = new RoundCheck();
        Theme.StyleCheck(cbRemember, "记住我的选择，不再询问");
        sec.Pair(null, cbRemember, 0, null, null, 0);
        sec.Body("不勾选则每次都问；勾选后想恢复询问，到「自定义优化项」里重新勾上「关闭窗口时询问」。");
        pg.Add(sec);
        dlg.Controls.Add(pg);
        pg.BringToFront();

        var bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Bottom;
        bar.Height = Theme.S(52);
        bar.FlowDirection = FlowDirection.RightToLeft;
        bar.WrapContents = false;
        bar.BackColor = Theme.Panel;
        bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
        var btnOk = new FlatBtn(); btnOk.Text = "确认"; btnOk.Kind = BtnKind.Primary; btnOk.Width = Theme.S(84);
        var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
        bar.Controls.Add(btnCancel);
        bar.Controls.Add(btnOk);
        dlg.Controls.Add(bar);

        pg.Reflow();
        Shot(dlg, "closedlg_" + mode + ".png");
    }

    // ==================== 滚动掉字实测（2026-09-20）====================
    // 用户反馈：滑动过快时"文字不显示或显示过慢"，截图里出现①整片空白列表 ②某行只剩名称列。
    // 为什么必须专门做一个模式：
    //   · DrawToBitmap 会**强制整树重绘**，这类瞬时中间态在它面前完全隐形；
    //   · 只有"发滚动消息 → 立刻 BitBlt 读真实窗口表面"才抓得到（与 ghostscan 同一思路）。
    // 判据：某行如果"名称列有墨、状态/当前值/期望值列一点墨都没有" = 掉字。
    // 指标：滚一屏后，需要多少次消息泵迭代才把文字补齐（迭代数 × 间隔 ms ≈ 用户感知的延迟）。
    static Bitmap GrabCtl(Control c)
    {
        Rectangle cr = c.ClientRectangle;
        Bitmap bmp = new Bitmap(Math.Max(1, cr.Width), Math.Max(1, cr.Height));
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr src = GetDC(c.Handle), dst = g.GetHdc();
            BitBlt(dst, 0, 0, cr.Width, cr.Height, src, 0, 0, 0x00CC0020);
            g.ReleaseHdc(dst);
            ReleaseDC(c.Handle, src);
        }
        return bmp;
    }

    // 逐列数"墨"（亮度 < 170 的像素）。白底、LineSoft 分隔线(244)、Grid 竖线(231) 都不会被计入。
    static List<int> BlankRows(ListView lv, Bitmap shot, out int visible)
    {
        var bad = new List<int>();
        visible = 0;
        int nc = lv.Columns.Count;
        if (nc == 0) return bad;
        int[] edge = new int[nc + 1];
        for (int i = 0; i < nc; i++) edge[i + 1] = edge[i] + lv.Columns[i].Width;
        for (int i = 0; i < lv.Items.Count; i++)
        {
            Rectangle r;
            try { r = lv.GetItemRect(i, ItemBoundsPortion.Entire); }
            catch { continue; }
            if (r.Height <= 4 || r.Bottom < 2 || r.Top > lv.ClientSize.Height - 2) continue;  // 整行在视口外/只露一丝
            visible++;
            int y0 = Math.Max(0, r.Top), y1 = Math.Min(shot.Height - 1, r.Bottom - 1);
            int[] ink = new int[nc];
            for (int y = y0; y <= y1; y++)
                for (int x = 0; x < shot.Width; x++)
                {
                    Color c = shot.GetPixel(x, y);
                    if ((c.R * 30 + c.G * 59 + c.B * 11) / 100 >= 170) continue;
                    int k = 0; while (k < nc && x >= edge[k + 1]) k++;
                    if (k < nc) ink[k]++;
                }
            if (ink[0] < 12) continue;                       // 名称列本来就没墨（行还没开始画）→ 不算
            bool rest = false;
            for (int k = 1; k < nc; k++) if (ink[k] > 2) { rest = true; break; }
            if (!rest) bad.Add(i);
        }
        return bad;
    }

    // 探针编成 winexe（没控制台），Console 输出经常拿不到 —— 所以抓一份到文件。
    static TextWriter origOut;
    static StringWriter capLog;
    static void CapStart() { origOut = Console.Out; capLog = new StringWriter(); Console.SetOut(capLog); }
    static void CapEnd(string file)
    {
        Console.SetOut(origOut == null ? TextWriter.Null : origOut);
        File.WriteAllText(Path.Combine(OUT, file), capLog == null ? "" : capLog.ToString(), new System.Text.UTF8Encoding(false));
    }

    static void ScrollStress(string mode, int page, int bursts)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);          // 必须真的在桌面上，GetDC 才拿得到表面
        f.Size = new Size(1056, 739);          // 用户窗口的实际尺寸
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) { Console.WriteLine("找不到 lvOpt"); f.Close(); return; }
        //  lvOpt 属于「性能优化」页（page 1）。传别的页码时它**不在被绘制的那一页上**，
        //  "值列一点墨都没有"必然成立 —— 于是基线就报"掉字 4"、12 步报"掉字 48"，
        //  看着像渲染缺陷，其实是探针用错页码（2026-09-21 实测：page=1 掉字 0）。
        //  判据本身要拦住这种误用，而不是留一份读不懂的红给下一个人。
        if (page != 1)
        {
            Console.WriteLine("[用法错误] realwheel 的页码必须是 1（性能优化页）—— lvOpt 只在那一页被绘制。");
            Console.WriteLine("          传 page=" + page + " 会得到假的「掉字」，所以这里直接拒绝。");
            f.Close();
            return;
        }

        Console.WriteLine("=== 滚动掉字实测  mode=" + mode + "  page=" + page
                          + "  lv=" + lv.ClientSize + "  Items=" + lv.Items.Count
                          + "  OwnerDraw=" + lv.OwnerDraw + "  GridLines=" + lv.GridLines);
        // ① 数据自检：每一行到底有没有 4 列内容（排除"数据本来就是空的"这种误判）
        int few = 0;
        foreach (ListViewItem it in lv.Items)
            if (it.SubItems.Count < 4) few++;
        Console.WriteLine("  行数=" + lv.Items.Count + "  列数=" + lv.Columns.Count
                          + "  子项不足 4 的行=" + few);
        for (int i = 0; i < Math.Min(3, lv.Items.Count); i++)
        {
            ListViewItem it = lv.Items[i];
            Console.WriteLine("   item[" + i + "] 「" + it.Text + "」 sub=" + it.SubItems.Count
                              + " → [" + (it.SubItems.Count > 1 ? it.SubItems[1].Text : "-") + "] ["
                              + (it.SubItems.Count > 2 ? it.SubItems[2].Text : "-") + "] ["
                              + (it.SubItems.Count > 3 ? it.SubItems[3].Text : "-") + "]");
        }

        // ② 基准：静置状态下的完整表面（此时若已有掉字行，说明问题与滚动无关）
        int vis;
        using (Bitmap clean = GrabCtl(lv))
        {
            var b0 = BlankRows(lv, clean, out vis);
            Console.WriteLine("  静置基准：可见行 " + vis + "  掉字行 = " + b0.Count
                              + (b0.Count > 0 ? "  → " + string.Join(",", b0.ConvertAll(x => x.ToString()).ToArray()) : ""));
        }

        // ③ 爆发式滚动：连续下翻，每步之后立刻抓表面（不给它慢慢补画的机会）
        int totalBlank = 0, worstStep = -1, worstCount = 0;
        for (int step = 0; step < bursts; step++)
        {
            for (int k = 0; k < 5; k++) SendMsg(lv.Handle, WM_VSCROLL, (IntPtr)1, IntPtr.Zero);  // SB_LINEDOWN ×5
            using (Bitmap shot = GrabCtl(lv))
            {
                var bad = BlankRows(lv, shot, out vis);
                if (bad.Count > 0)
                {
                    totalBlank += bad.Count;
                    if (bad.Count > worstCount)
                    {
                        worstCount = bad.Count; worstStep = step;
                        shot.Save(Path.Combine(OUT, "scroll_bad_" + mode + "_step" + step + ".png"), ImageFormat.Png);
                    }
                }
            }
            Application.DoEvents();
            Thread.Sleep(2);
            // 回到顶部，保持每次都是同一批行参与
            SendMsg(lv.Handle, WM_VSCROLL, (IntPtr)7, IntPtr.Zero);   // SB_TOP
            Application.DoEvents();
        }
        Console.WriteLine("  滚动 " + bursts + " 次爆发：累计掉字行 " + totalBlank
                          + (worstStep >= 0 ? "  最差一次 step=" + worstStep + " 掉 " + worstCount + " 行（已存图）" : ""));

        // ④ 时序指标：滚一屏后要多少次泵迭代才补齐（迭代 ≈ 一帧）
        int[] need = new int[6];
        for (int t = 0; t < need.Length; t++)
        {
            SendMsg(lv.Handle, WM_VSCROLL, (IntPtr)7, IntPtr.Zero);   // SB_TOP
            Pump(10);
            for (int k = 0; k < 8; k++) SendMsg(lv.Handle, WM_VSCROLL, (IntPtr)1, IntPtr.Zero);
            int it = 0;
            while (it < 60)
            {
                it++;
                Application.DoEvents();
                using (Bitmap shot = GrabCtl(lv))
                {
                    if (BlankRows(lv, shot, out vis).Count == 0) break;
                }
                Thread.Sleep(2);
            }
            need[t] = it;
        }
        int max = 0; double avg = 0;
        for (int t = 0; t < need.Length; t++) { if (need[t] > max) max = need[t]; avg += need[t]; }
        Console.WriteLine("  补齐耗时（泵迭代数，越小越好）：[" + string.Join(",", Array.ConvertAll(need, x => x.ToString()))
                          + "]  平均 " + (avg / need.Length).ToString("F1") + "  最差 " + max + " 次");
        Console.WriteLine("  判读：平均 <= 2 次且累计掉字 0 → 滚动跟手；否则就是用户说的\"文字显示过慢\"");
        f.Close();
        CapEnd("scroll_report.txt");
    }

    // 拖动式滚动 + 重绘耗时（2026-09-20 第二版实测）
    //   为什么要第二版：第一版用 SB_LINEDOWN（滚轮/方向键那条路）——0 掉字、1 次泵迭代就补齐，
    //   复现不出用户看到的现象，说明用户的滚动路径不同。这里补齐两种更接近"手动拖滚动条"的方式：
    //     ① SB_THUMBTRACK：按住滑块拖 —— ListView 会走"原地重定位 + 局部重绘"路径；
    //     ② 不泵消息的连发：模拟渲染线程被占住时用户还在滚（消息堆积）。
    //   同时量一个客观指标：ListView 全量重绘耗时（ms）——这才是"文字显示过慢"的根。
    static void ScrollDrag(string mode, int page, int bursts)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) { Console.WriteLine("找不到 lvOpt"); CapEnd("scroll2_report.txt"); f.Close(); return; }

        Console.WriteLine("=== 拖动式滚动实测  mode=" + mode + "  Items=" + lv.Items.Count
                          + "  client=" + lv.ClientSize + "  首行高=" + lv.GetItemRect(0, ItemBoundsPortion.Entire).Height);

        // ① 全量重绘耗时：直接量"把整表重画一遍"要多久（用户感知的文字延迟上限就是它）
        var sw = new System.Diagnostics.Stopwatch();
        double best = 1e9, sum = 0, worst = 0;
        for (int t = 0; t < 8; t++)
        {
            Application.DoEvents();
            sw.Restart();
            lv.Invalidate();
            lv.Update();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms < best) best = ms;
            if (ms > worst) worst = ms;
            sum += ms;
        }
        Console.WriteLine("  全量重绘耗时 ms：最好 " + best.ToString("F1") + "  平均 " + (sum / 8).ToString("F1")
                          + "  最差 " + worst.ToString("F1"));

        // ② SB_THUMBTRACK 拖动：位置大步跳，跳完立刻抓（不泵消息）→ 再泵 1 帧抓
        int n = lv.Items.Count;
        int totalRaw = 0, totalPumped = 0, worstRaw = 0;
        for (int step = 0; step < bursts; step++)
        {
            int pos = (int)((double)n * ((step % 5) + 1) / 5.0);
            SendMsg(lv.Handle, WM_VSCROLL, (IntPtr)((pos << 16) | 5), IntPtr.Zero);   // 5 = SB_THUMBTRACK
            int vis;
            using (Bitmap raw = GrabCtl(lv))
            {
                int b = BlankRows(lv, raw, out vis).Count;
                totalRaw += b;
                if (b > worstRaw) { worstRaw = b; raw.Save(Path.Combine(OUT, "drag_raw_" + mode + "_step" + step + ".png"), ImageFormat.Png); }
            }
            Application.DoEvents();
            using (Bitmap pmp = GrabCtl(lv))
                totalPumped += BlankRows(lv, pmp, out vis).Count;
            Thread.Sleep(2);
        }
        Console.WriteLine("  拖动 " + bursts + " 次：立刻抓的掉字行合计 " + totalRaw + "（最差 " + worstRaw + " 行，已存图）"
                          + "；泵 1 帧后 " + totalPumped);

        // ③ 滚轮快滚（用户实际报的路径 2026-09-20）：ListView 收到 WM_MOUSEWHEEL 后**自己内部滚**，
        //    不给自己发 WM_VSCROLL —— 所以必须单独测这一条，否则"修好了"是假的。
        int wheelTop0 = (int)SendMsg(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
        int wheelBlank = 0, wheelMoved = 0;
        for (int step = 0; step < bursts * 2; step++)
        {
            Point sp = lv.PointToScreen(new Point(lv.ClientSize.Width / 2, lv.ClientSize.Height / 2));
            SendMsg(lv.Handle, WM_MOUSEWHEEL, (IntPtr)(-7864320), (IntPtr)((sp.Y << 16) | (sp.X & 0xFFFF)));  // delta=-120 → 向下滚
            int vis;
            using (Bitmap w = GrabCtl(lv))
            {
                var bad = BlankRows(lv, w, out vis);
                wheelBlank += bad.Count;
                if (bad.Count > 0 && wheelMoved < 3)
                    w.Save(Path.Combine(OUT, "wheel_bad_" + mode + "_step" + step + ".png"), ImageFormat.Png);
            }
            wheelMoved++;
            Application.DoEvents();
            Thread.Sleep(2);
        }
        int wheelTop1 = (int)SendMsg(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine("  滚轮 " + (bursts * 2) + " 格：topIndex " + wheelTop0 + " → " + wheelTop1
                          + "（说明滚轮真的滚动了列表，否则这条测试是空跑）  掉字行合计 " + wheelBlank);
        Console.WriteLine("  判读：立刻抓有掉字、泵 1 帧后为 0 → 属「一帧内没画完」，观感上就是文字迟到");
        f.Close();
        CapEnd("scroll2_report.txt");
    }

    // 把"掉字行"的真实数据打出来：到底是**没画**还是**本来就空**。
    //   FillList 给每行填 4 格（名称/状态/当前值/期望值），而 DrawSubItem 遇到空串会直接 return
    //   —— 所以"某行只剩名称列"既可能是绘制坏了，也可能只是那三个字段本来就是空。
    //   不打印数据就没法区分，前面几轮全卡在这里。
    static void DumpRow(ListView lv, int i, Bitmap shot, string tag)
    {
        if (i < 0 || i >= lv.Items.Count) return;
        ListViewItem it = lv.Items[i];
        Console.WriteLine("    [" + tag + "] 行 " + i + " 文本=" + Show(it.Text));
        for (int k = 1; k < it.SubItems.Count; k++)
        {
            ListViewItem.ListViewSubItem sub = it.SubItems[k];
            Console.WriteLine("       col" + k + " 文本=" + Show(sub.Text)
                              + "  ForeColor=" + (sub.ForeColor == Color.Empty ? "(空)" : sub.ForeColor.ToString())
                              + " Name=" + (sub.Name == null ? "(null)" : sub.Name));
        }
        // 逐列数墨（与 BlankRows 同一套阈值）
        int nc = lv.Columns.Count;
        int[] edge = new int[nc + 1];
        for (int c = 0; c < nc; c++) edge[c + 1] = edge[c] + lv.Columns[c].Width;
        Rectangle r; try { r = lv.GetItemRect(i, ItemBoundsPortion.Entire); } catch { return; }
        int y0 = Math.Max(0, r.Top), y1 = Math.Min(shot.Height - 1, r.Bottom - 1);
        int[] ink = new int[nc];
        for (int y = y0; y <= y1; y++)
            for (int x = 0; x < shot.Width; x++)
            {
                Color c = shot.GetPixel(x, y);
                if ((c.R * 30 + c.G * 59 + c.B * 11) / 100 >= 170) continue;
                int k = 0; while (k < nc && x >= edge[k + 1]) k++;
                if (k < nc) ink[k]++;
            }
        Console.WriteLine("       墨量/列=" + string.Join(",", Array.ConvertAll(ink, x => x.ToString()))
                          + "  行矩形=" + r);
    }

    static string Show(string s)
    {
        if (s == null) return "(null)";
        if (s.Length == 0) return "\"\"";        // 空串要一眼看出来
        return "\"" + s + "\"";
    }

    // ==================== 自检/自愈的注入式验证（2026-09-20）====================
    // 目的：证明"逐格自检"真的能抓到"某行只剩名称列"这种漏画，而不是永远静默。
    // 做法：打开 Theme.FaultRow，让第 N 行的状态格被记成"没画过"（模拟实际漏画），
    //   然后看日志里有没有出现「[绘制自检] 自绘漏画」，以及修复后掩码是否恢复完整。
    static void FaultInject(string mode, int page, int row)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) { Console.WriteLine("找不到 lvOpt"); CapEnd("fault_report.txt"); f.Close(); return; }

        Console.WriteLine("=== 自检注入验证  mode=" + mode + "  page=" + page
                          + "  lv=" + lv.ClientSize + "  Items=" + lv.Items.Count
                          + "  注入行=" + row + "（该行状态格会被记成没画过）");

        Theme.FaultRow = row;
        // 关键：自检是在 WM_PAINT 里跑的，列表闲着就不会有 WM_PAINT —— 必须主动触发重画
        for (int k = 0; k < 5; k++)
        {
            try { lv.Invalidate(false); lv.Update(); } catch { }
            Pump(3);
        }
        int vis;
        using (Bitmap shot = GrabCtl(lv))
        {
            var bad = BlankRows(lv, shot, out vis);
            Console.WriteLine("  注入期间：可见 " + vis + " 行 · 掉字行 [" + string.Join(",", bad.ConvertAll(x => x.ToString()).ToArray()) + "]");
            Console.WriteLine("  （自检会在 300ms 冷却内反复修，所以屏幕上不该出现掉字：掉字 0 = 自愈有效）");
        }
        Theme.FaultRow = -1;
        Pump(10);
        Console.WriteLine("  已撤掉注入。日志里应出现「[绘制自检] 自绘漏画 第 N 次 … 首个缺失行=" + row + "」");
        f.Close();
        CapEnd("fault_report.txt");
    }

    // ==================== 表头右键菜单 + 列宽一键复原（2026-09-20）====================
    // 用户诉求：「这个栏还能自动调节，但为啥没有一键复原的选项」。
    // 两个要点各验一条**确定性**判据（不靠输入注入 —— 无前台焦点时注入送不到窗口）：
    //   ① 表头是独立 SysHeader32 子窗口，右键它必须也能弹出同一张菜单；
    //      直接给表头窗口发 WM_CONTEXTMENU，看 ContextMenuStrip.Visible 是否变 true。
    //   ② 「恢复默认列宽」真的把被拖乱的宽度还原回建表值。
    const int WM_CONTEXTMENU = 0x007B;
    const int LVM_GETHEADER_P = 0x101F;

    static void HeaderMenuProbe(string mode, int page)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) { Console.WriteLine("找不到 lvOpt"); CapEnd("headermenu_report.txt"); f.Close(); return; }

        Console.WriteLine("=== 表头右键菜单 + 列宽复原  mode=" + mode + "  page=" + page
                          + "  lv=" + lv.ClientSize + "  列数=" + lv.Columns.Count);

        // ① 建表后应有菜单 + 表头句柄
        Console.WriteLine("  " + (lv.ContextMenuStrip != null ? "[PASS]" : "[FAIL]")
                          + " ListView 上挂了右键菜单"
                          + (lv.ContextMenuStrip != null ? "（" + lv.ContextMenuStrip.Items.Count + " 项）" : ""));
        IntPtr hHeader = SendMsg(lv.Handle, LVM_GETHEADER_P, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine("  " + (hHeader != IntPtr.Zero ? "[PASS]" : "[FAIL]")
                          + " 取到表头句柄（SysHeader32）  → " + hHeader);

        // ② 给表头发 WM_CONTEXTMENU，菜单应弹出
        if (hHeader != IntPtr.Zero && lv.ContextMenuStrip != null)
        {
            Point pt = lv.PointToScreen(new Point(Theme.S(40), 8));
            IntPtr lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            SendMsg(hHeader, WM_CONTEXTMENU, hHeader, lp);
            Pump(20);
            bool shown = lv.ContextMenuStrip.Visible;
            Console.WriteLine("  " + (shown ? "[PASS]" : "[FAIL]")
                              + " 右键表头 → 菜单弹出（修前这里恒为 false：表头是自己的窗口，不继承父菜单）");
            try { lv.ContextMenuStrip.Close(); } catch { }
            Pump(10);
        }

        // ③ 菜单项齐全
        if (lv.ContextMenuStrip != null)
        {
            string names = "";
            foreach (ToolStripItem it in lv.ContextMenuStrip.Items) names += "|" + it.Text;
            bool hasReset = names.Contains("恢复默认列宽");
            Console.WriteLine("  " + (hasReset ? "[PASS]" : "[FAIL]") + " 有「恢复默认列宽」项   → " + names);
        }

        // ④ 复原逻辑：拖乱 → 复原 → 应等于建表值
        int n = lv.Columns.Count;
        int[] before = new int[n];
        for (int i = 0; i < n; i++) before[i] = lv.Columns[i].Width;
        for (int i = 0; i < n; i++) lv.Columns[i].Width = 23;     // 模拟把每列都拖到只剩一点
        Pump(4);
        Theme.ResetListWidths(lv);
        Pump(4);
        bool same = true;
        string after = "";
        for (int i = 0; i < n; i++)
        {
            after += "|" + lv.Columns[i].Width;
            if (lv.Columns[i].Width != before[i]) same = false;
        }
        Console.WriteLine("  " + (same ? "[PASS]" : "[FAIL]") + " 拖乱后复原 = 建表值   → 建表" + string.Join("|", Array.ConvertAll(before, x => x.ToString()))
                          + "  复原后" + after);

        // ⑤ 复原到默认后不应冒出横向滚动条（末列被 FillLastColumn 收敛到剩余宽度）
        try
        {
            Theme.ResetListWidths(lv);
            Pump(6);
            int total = 0;
            string colsW = "";
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                total += lv.Columns[i].Width;
                colsW += "|" + lv.Columns[i].Text + "=" + lv.Columns[i].Width;
            }
            const int WS_HSCROLL_P = 0x00100000;
            bool hscroll = (GetWindowLongProbe(lv.Handle, GWL_STYLE_PROBE) & WS_HSCROLL_P) != 0;
            Console.WriteLine("  复原后逐列：" + colsW);
            Console.WriteLine("  " + (hscroll ? "[FAIL]" : "[PASS]") + " 复原后无横向滚动条（不白丢一行）"
                              + "  → 总宽 " + total + " vs 客户区 " + lv.ClientSize.Width + "  有横条=" + hscroll);
        }
        catch (Exception ex) { Console.WriteLine("  [FAIL] ResetListWidths 抛异常：" + ex.Message); }

        Console.WriteLine("  判读：②必须 PASS —— 它证明「表头右键」这条路真的通（用户视角的「没有选项」就死在这里）。");
        f.Close();
        CapEnd("headermenu_report.txt");
    }

    // ==================== 滚动时列表是否被反复改尺寸/改列宽（2026-09-20）============
    // 线索：`lvOpt.Resize += { FillLastColumn(lvOpt, S(160)); }` —— 末列宽度是**跟着控件宽度**
    //   算出来的；而 Pg.LayoutOnce 每滚一格都会 `s.Width = avail; s.DoLayout();`。
    //   如果这两件事真的每次滚动都跑，那就是"滚动中途不断改列宽" → 自绘时的 e.Bounds 会撞上
    //   改列宽的那一瞬（画到一半布局变了）→ 正好是"某行只剩名称列"。
    //   这一版专门验它：数 Resize 次数、记末列宽度轨迹、看有没有 VScrollBar。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLongProbe(IntPtr hWnd, int index);
    const int GWL_STYLE_PROBE = -16;
    const int WS_VSCROLL_PROBE = 0x00200000;

    static void ColWatch(string mode, int page, int steps, int stepPx)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        if (lv == null || arr == null || page >= arr.Length)
        { Console.WriteLine("取 lvOpt/pages 失败"); CapEnd("colwatch_report.txt"); f.Close(); return; }
        Pg pg = arr[page];

        int resizes = 0;
        lv.Resize += delegate { resizes++; };

        Rectangle inPg = new Rectangle(pg.PointToClient(lv.PointToScreen(Point.Empty)), lv.Size);
        int baseY = Math.Max(0, inPg.Top + pg.ScrollY - Theme.S(20));

        Console.WriteLine("=== 滚动时列宽/尺寸轨迹  mode=" + mode + "  page=" + page);
        Console.WriteLine("  初始 lv=" + lv.ClientSize + "  列宽=[" + Cols(lv) + "]  合计=" + ColsSum(lv)
                          + "  VScroll=" + HasVScroll(lv));

        pg.SetScrollY(baseY); Pump(20);
        Console.WriteLine("  滚到 baseY=" + baseY + " 后 lv=" + lv.ClientSize + "  列宽=[" + Cols(lv) + "]"
                          + "  合计=" + ColsSum(lv) + "  Resize累计=" + resizes + "  VScroll=" + HasVScroll(lv));

        int before = resizes;
        for (int s = 0; s < steps; s++)
        {
            for (int k = 1; k <= 6; k++) pg.SetScrollY(baseY + stepPx * k);
            Application.DoEvents();
            Console.WriteLine("  step " + s + " ScrollY=" + pg.ScrollY + "  lv=" + lv.ClientSize
                              + "  末列=" + lv.Columns[lv.Columns.Count - 1].Width
                              + "  列合计=" + ColsSum(lv) + "  Resize累计=" + resizes + "  VScroll=" + HasVScroll(lv));
            pg.SetScrollY(baseY);
            Application.DoEvents();
            Thread.Sleep(2);
        }
        Console.WriteLine("  页面滚动 " + steps + " 轮（每轮 6 格）→ ListView.Resize 触发 " + (resizes - before) + " 次");
        Console.WriteLine("  判读：Resize 次数 ≈ 0 → 列宽稳定，本条排除；次数与格数同量级 → 就是它。");
        f.Close();
        CapEnd("colwatch_report.txt");
    }

    static string Cols(ListView lv)
    {
        var a = new string[lv.Columns.Count];
        for (int i = 0; i < lv.Columns.Count; i++) a[i] = lv.Columns[i].Width.ToString();
        return string.Join(",", a);
    }
    static int ColsSum(ListView lv)
    {
        int s = 0;
        foreach (ColumnHeader c in lv.Columns) s += c.Width;
        return s;
    }
    static string HasVScroll(ListView lv)
    {
        try { return ((GetWindowLongProbe(lv.Handle, GWL_STYLE_PROBE) & WS_VSCROLL_PROBE) != 0) ? "有" : "无"; }
        catch { return "?"; }
    }

    // ==================== 静态整表数据+墨量对照（2026-09-20）====================
    // 目的：把"某行只剩名称列"这件事一次问到底 —— 逐行打印 4 格文本、前景色、逐列墨量。
    //   空串 = 数据本来就是空（不是绘制问题）；有文本却 0 墨 = 真的没画出来（才是 bug）。
    static void DumpRows(string mode, int page)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) { Console.WriteLine("找不到 lvOpt"); CapEnd("dumprows_report.txt"); f.Close(); return; }

        int vis;
        using (Bitmap shot = GrabCtl(lv))
        {
            Console.WriteLine("=== 静态整表对照  mode=" + mode + "  page=" + page
                              + "  lv=" + lv.ClientSize + "  Items=" + lv.Items.Count);
            var bad = BlankRows(lv, shot, out vis);
            Console.WriteLine("  可见 " + vis + " 行（视口内）· 掉字行索引 [" + string.Join(",", bad.ConvertAll(x => x.ToString()).ToArray()) + "]");
            for (int i = 0; i < lv.Items.Count; i++)
            {
                Rectangle r;
                try { r = lv.GetItemRect(i, ItemBoundsPortion.Entire); } catch { continue; }
                bool inView = !(r.Height <= 4 || r.Bottom < 2 || r.Top > lv.ClientSize.Height - 2);
                ListViewItem it = lv.Items[i];
                string cells = "";
                for (int k = 0; k < it.SubItems.Count; k++) cells += "|" + Show(it.SubItems[k].Text);
                Console.WriteLine("  #" + Pad2(i) + (inView ? " *" : "  ") + " " + cells);
            }
            if (bad.Count > 0) foreach (int i in bad) DumpRow(lv, i, shot, "掉字");
        }
        f.Close();
        CapEnd("dumprows_report.txt");
    }

    static string Pad2(int i) { return i < 10 ? " " + i : "" + i; }

    // ==================== 真实滚轮实测（第四版，2026-09-20）====================
    // 为什么非要用真输入：前三版都是 SendMessage 造消息。实测发现给 ListView 直接投
    //   WM_MOUSEWHEEL 它**根本不滚**（topIndex 0→0）—— 也就是说造出来的消息走的不是
    //   用户那条路（真实的 WM_MOUSEWHEEL 由系统按"光标下那个窗口"投递，还牵扯焦点）。
    //   所以这一版：把光标真的移到列表上 → mouse_event 发真滚轮 → 逐格读真实表面。
    // 同时记录 ListView.topIndex 与 Pg.ScrollY，第一次把"滚轮到底滚的是谁"这个问题钉死。
    // 用完把光标还回原位（不要动用户的东西）。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    const uint MOUSEEVENTF_WHEEL = 0x0800;

    static void RealWheel(string mode, int page, int notches)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        if (lv == null || arr == null || page >= arr.Length)
        { Console.WriteLine("取 lvOpt/pages 失败"); CapEnd("realwheel_report.txt"); f.Close(); return; }
        Pg pg = arr[page];

        Point cursorOld; GetCursorPos(out cursorOld);
        Point hit = lv.PointToScreen(new Point(lv.ClientSize.Width / 2, lv.ClientSize.Height / 2));
        // 统一用**物理像素**坐标（GetCursorPos/SetCursorPos 都是物理像素）
        SetCursorPos(hit.X, hit.Y);
        Pump(20);

        int vis;
        int top0 = (int)SendMsg(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
        int scr0 = pg.ScrollY;
        using (Bitmap clean = GrabCtl(lv))
        {
            var bad = BlankRows(lv, clean, out vis);
            Console.WriteLine("=== 真实滚轮实测  mode=" + mode + "  page=" + page
                              + "  lv=" + lv.ClientSize + "  Items=" + lv.Items.Count);
            Console.WriteLine("  光标目标=" + hit + "  topIndex=" + top0 + "  Pg.ScrollY=" + scr0
                              + "  基线可见 " + vis + " 行 / 掉字 " + bad.Count);
        }

        int blanks = 0, minVis = 9999, movedSteps = 0;
        for (int i = 0; i < notches; i++)
        {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -120, UIntPtr.Zero);   // -120 = 向下一格
            int vRaw = 0;
            using (Bitmap raw = GrabCtl(lv))
            {
                var bad = BlankRows(lv, raw, out vRaw);
                blanks += bad.Count;
                if (vRaw < minVis) minVis = vRaw;
                if (bad.Count > 0)
                    raw.Save(Path.Combine(OUT, "realwheel_bad_" + mode + "_n" + i + ".png"), ImageFormat.Png);
            }
            int t = (int)SendMsg(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
            if (t != top0) movedSteps++;
            Console.WriteLine("  n" + i + "  topIndex=" + t + "  ScrollY=" + pg.ScrollY + "  可见 " + vRaw + " 行");
            Application.DoEvents();
            Thread.Sleep(8);      // 近似"快滚"的节奏
        }
        int top1 = (int)SendMsg(lv.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine("  合计：列表 topIndex " + top0 + " → " + top1 + "（动了 " + movedSteps + " 格）"
                          + "  Pg.ScrollY " + scr0 + " → " + pg.ScrollY);
        Console.WriteLine("  掉字合计 " + blanks + "  列表最少可见行 " + minVis);
        Console.WriteLine("  判读：ScrollY 变了 = 滚轮滚的是**页面**（列表被搬窗口）；topIndex 变了 = 滚的是**列表自己**。");
        //  一步都没动 = 这 N 帧其实是同一帧，"掉字合计"只是基线值 ×N。不写出来的话，
        //  读报告的人会以为真滚过 N 个状态（合成滚轮在离屏 / 非前台下常常根本送不到）。
        if (top1 == top0 && movedSteps == 0 && pg.ScrollY == scr0)
            Console.WriteLine("  ⚠ 滚轮没让列表或页面动过（topIndex 与 ScrollY 全程不变）—— 上面这些帧其实是同一帧，"
                              + "\"掉字合计\"只是基线×步数；要测真实滚动得先让滚轮真的生效"
                              + "（这条本轮没查清：是合成输入没送到，还是控件不响应滚轮）。");
        f.Close();
        CapEnd("realwheel_report.txt");
        SetCursorPos(cursorOld.X, cursorOld.Y);
    }

    // ==================== 页面滚动实测（第三版，2026-09-20）====================
    // 为什么还要第三版：前两版测的都是**列表自己滚**（SB_LINEDOWN / SB_THUMBTRACK / 列表自己的滚轮）。
    //   但这一页的滚动其实是 Pg **自管**的：LayoutOnce 把每个分区**真的移动**（s.Location = y - ScrollY）。
    //   用户滚轮滚的多半就是整个页面 —— 列表窗口只是被父容器搬来搬去的那个。
    //   而 LayoutOnce 里写的是 `s.Invalidate()`，即 Invalidate(false) = **不重绘子控件**：
    //   列表的像素全靠 OS 搬窗口时顺手带过去。搬得比画得快，就会留下"半张表"
    //   —— 正好对上用户三张截图（某行只剩名称列 / 整片空白）。
    // 判据同前：某行名称列有墨、其余三列无墨 = 掉字。
    // 额外记一项"列表可见行数"：远小于基线 = 整片空白那种形态。
    // 把某个长页面滚到指定位置再截一张（设置页 / 说明页的下半截以前完全看不到）。
    //   用法：python tools\ui_snapshot.py light view 6 900
    static void ViewAt(string mode, int page, int y)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);
        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        if (arr == null || page >= arr.Length)
        { Console.WriteLine("取 pages 失败"); f.Close(); return; }
        arr[page].SetScrollY(y);
        Pump(80);
        Console.WriteLine("page=" + page + "  请求 ScrollY=" + y + "  实际=" + arr[page].ScrollY);
        using (Bitmap b = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
            string outPath = Path.Combine(OUT, "view_" + mode + "_p" + page + "_y" + y + ".png");
            b.Save(outPath, ImageFormat.Png);
            Console.WriteLine("已保存 " + outPath);
        }
        f.Close();
    }

    static void PageScrollStress(string mode, int page, int steps, int stepPx)
    {
        CapStart();
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        Theme.SetMode(mode);
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);          // 必须真的在桌面上，GetDC 才拿得到表面
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(30);
        Switch(f, page);
        Pump(60);

        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        FieldInfo pf = typeof(MainForm).GetField("pages", BindingFlags.NonPublic | BindingFlags.Instance);
        Pg[] arr = pf == null ? null : (Pg[])pf.GetValue(f);
        if (lv == null || arr == null || page >= arr.Length)
        { Console.WriteLine("取 lvOpt/pages 失败"); CapEnd("pagescroll_report.txt"); f.Close(); return; }
        Pg pg = arr[page];

        // 列表在"页面内容坐标"里的位置 = 当前页内位置 + 已滚量（不受滚动影响的那个值）
        Rectangle inPg = new Rectangle(pg.PointToClient(lv.PointToScreen(Point.Empty)), lv.Size);
        int contentY = inPg.Top + pg.ScrollY;
        int baseY = Math.Max(0, contentY - Theme.S(20));   // 视口顶端对齐到列表上方 20px → 列表整体可见

        //  lvOpt 属于「性能优化」页（page 1）。传别的页码时它**不在被绘制的那一页上**，
        //  对它做 BlankRows 量的是"一个不可见控件"，基线必然报假掉字、还会落一堆
        //  pagescroll_bad_*.png（2026-09-21 实测：page=2 基线掉字 4，page=1 掉字 0）。
        //  → 掉字这一步只在列表真的可见时做；页面滚动本身（ScrollY / 每格耗时）在任何页码都测。
        bool lvOnPage = lv.Visible;

        Console.WriteLine("=== 页面滚动掉字实测  mode=" + mode + "  page=" + page
                          + (lvOnPage ? "" : "（lvOpt 不在此页 → 只测页面滚动，跳过掉字判据）"));
        Console.WriteLine("  Pg=" + pg.ClientSize + "  ScrollY=" + pg.ScrollY + "  lv 在页内=" + inPg
                          + "  内容Y=" + contentY + "  baseY=" + baseY + "  Items=" + lv.Items.Count
                          + "  lv.Visible=" + lvOnPage);

        int baseVis = 0;
        if (lvOnPage)
        {
            using (Bitmap clean = GrabCtl(lv))
            {
                var bad = BlankRows(lv, clean, out baseVis);
                Console.WriteLine("  ① 静止基线：列表可见 " + baseVis + " 行，掉字 " + bad.Count
                                  + "（>0 说明基线本身就坏，后面数字要打折看）");
            }
        }
        else
        {
            Console.WriteLine("  ① 跳过：本页没有 lvOpt（那是「性能优化」页的列表），"
                              + "对它量掉字只会得到假红。要测掉字请传 page=1。");
        }

        pg.SetScrollY(baseY); Pump(20);

        int totalRaw = 0, totalPumped = 0, worst = 0, worstStep = -1;
        int minVisRaw = 9999, minVisPumped = 9999;
        var swBurst = new System.Diagnostics.Stopwatch();
        double burstBest = 1e9, burstWorst = 0, burstSum = 0;
        for (int s = 0; s < steps; s++)
        {
            // 快滚：连发 6 次，中间**不泵消息** —— 模拟 WM_PAINT 被滚轮消息饿死
            swBurst.Restart();
            for (int k = 1; k <= 6; k++) pg.SetScrollY(baseY + stepPx * k);
            swBurst.Stop();
            double ms = swBurst.Elapsed.TotalMilliseconds;
            if (ms < burstBest) burstBest = ms;
            if (ms > burstWorst) burstWorst = ms;
            burstSum += ms;
            int vRaw = 0, vPump = 0, bRaw = 0, bPump = 0;
            if (lvOnPage)
            {
                using (Bitmap raw = GrabCtl(lv))
                {
                    var bad = BlankRows(lv, raw, out vRaw);
                    bRaw = bad.Count; totalRaw += bRaw;
                    if (vRaw < minVisRaw) minVisRaw = vRaw;
                    if (bRaw > worst)
                    {
                        worst = bRaw; worstStep = s;
                        raw.Save(Path.Combine(OUT, "pagescroll_bad_" + mode + "_s" + s + ".png"), ImageFormat.Png);
                    }
                }
                Application.DoEvents();
                using (Bitmap pmp = GrabCtl(lv))
                {
                    bPump = BlankRows(lv, pmp, out vPump).Count;
                    totalPumped += bPump;
                    if (vPump < minVisPumped) minVisPumped = vPump;
                }
            }
            else
            {
                Application.DoEvents();
                // 本页没有 lvOpt 时掉字判据用不了，但**页面滚到哪了**正是我们要看的 ——
                //  长页面（设置页 / 说明页）以前没有任何办法看下半截。这里存一张滚后的视口图。
                if (s == 0)
                {
                    try
                    {
                        using (Bitmap view = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(view, new Rectangle(0, 0, f.Width, f.Height));
                            view.Save(Path.Combine(OUT, "pagescroll_view_" + mode + "_p" + page + ".png"),
                                      ImageFormat.Png);
                        }
                    }
                    catch { }
                }
            }
            if (lvOnPage && (bRaw > 0 || bPump > 0 || vRaw < baseVis))
                Console.WriteLine("  step " + s + "  ScrollY=" + pg.ScrollY
                                  + "  立刻抓: 可见 " + vRaw + " 行 / 掉字 " + bRaw
                                  + "   泵1帧后: 可见 " + vPump + " 行 / 掉字 " + bPump);
            else if (!lvOnPage)
                Console.WriteLine("  step " + s + "  ScrollY=" + pg.ScrollY + "  每轮 6 格 " + ms.ToString("F1") + " ms");
            pg.SetScrollY(baseY);
            Application.DoEvents();
            Thread.Sleep(2);
        }
        if (lvOnPage)
        {
            Console.WriteLine("  ② 页面快滚 " + steps + " 次：立刻抓掉字合计 " + totalRaw
                              + "（最差一次 " + worst + " 行" + (worstStep >= 0 ? " @step" + worstStep + "，已存图" : "") + "）"
                              + "；泵 1 帧后合计 " + totalPumped);
            Console.WriteLine("  ③ 列表可见行数：基线 " + baseVis + "  立刻抓最少 " + minVisRaw + "  泵后最少 " + minVisPumped);
        }
        else
        {
            Console.WriteLine("  ② 页面快滚 " + steps + " 次：已跳过掉字统计（lvOpt 不在本页）");
            Console.WriteLine("  ③ 已跳过：可见行数只在 lvOpt 所在页有意义");
        }
        Console.WriteLine("  ④ 每轮 6 格整页滚动耗时 ms（含同步整树重绘）：最好 " + burstBest.ToString("F1")
                          + "  平均 " + (burstSum / Math.Max(1, steps)).ToString("F1")
                          + "  最差 " + burstWorst.ToString("F1")
                          + "  → 单格 ≈ " + (burstSum / Math.Max(1, steps * 6)).ToString("F1") + " ms"
                          + (pg.ScrollY > 0 ? "（ScrollY 最终 " + pg.ScrollY + " = 页面确实滚了）"
                                            : "（⚠ ScrollY 没变 = 页面压根没滚）"));
        Console.WriteLine(lvOnPage
            ? "  判读：掉字 0 且可见行数不低于基线 → 页面滚动这条路是干净的；否则就是它。"
            : "  判读：本页没有 lvOpt，只有「ScrollY 有没有动 / 每格耗时」有效；掉字判据请在 page=1 上跑。");
        f.Close();
        CapEnd("pagescroll_report.txt");
    }

    [STAThread]
    // 扫描线取色：回答"这一小块到底是什么颜色"这类 1~2 像素的问题。
    //   为什么不用放大截图：这个环境里没装 PIL，PowerShell 的 Add-Type 被安全策略拦住
    //   （"compiles arbitrary .NET code at runtime"），而"某行左边那根绿条是谁画的"
    //   用一串色值回答比用图更准。行尾用 run-length 压缩，免得刷屏。
    //   用法：pix <light|dark> <页> <h|v> <固定的那条坐标> <起> <止>
    //        例：pix light 1 h 371 190 560     ← 第 371 行、x 从 190 扫到 560
    static void Pixels(string mode, int page, char axis, int fixedCoord, int from, int to)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1500);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            int a0 = Math.Min(from, to), a1 = Math.Max(from, to);
            Color last = Color.Empty; int runStart = a0;
            // 直接写文件：本探针是用 /target:winexe 编的（没有控制台），Console 输出经常拿不到。
            var sb = new System.Text.StringBuilder();
            for (int a = a0; a <= a1; a++)
            {
                Color c = axis == 'h' ? bmp.GetPixel(a, fixedCoord) : bmp.GetPixel(fixedCoord, a);
                if (a == a0) { last = c; runStart = a; continue; }
                if (c.ToArgb() != last.ToArgb())
                {
                    sb.AppendLine(string.Format("{0,5}-{1,5}  #{2:X6}", runStart, a - 1, last.ToArgb() & 0xFFFFFF));
                    last = c; runStart = a;
                }
            }
            sb.AppendLine(string.Format("{0,5}-{1,5}  #{2:X6}", runStart, a1, last.ToArgb() & 0xFFFFFF));
            string outFile = Path.Combine(OUT, "pix_" + mode + "_p" + page + "_" + axis + fixedCoord
                                               + "_" + a0 + "_" + a1 + ".txt");
            File.WriteAllText(outFile, sb.ToString());
            Console.WriteLine(sb.ToString());
            Console.WriteLine("wrote " + outFile);
        }
        f.Close();
    }

    // 读回 ListView 的扩展样式：LVS_EX_DOUBLEBUFFER(0x00010000) 是"滚动不闪"的关键，
    //   而它在离屏截图里**看不出来**，只能这样确认它真的被打进原生控件了。
    // ⚠ DllImport 默认拿**方法名**当入口点：这里方法叫 SendMsg，user32 里没有这个名字，
    //   必须显式写 EntryPoint = "SendMessageW"，否则 EntryPointNotFoundException（已踩一次）。
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        EntryPoint = "SendMessageW")]
    static extern IntPtr SendMsg(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int LVM_GETEXTENDEDLISTVIEWSTYLE = 0x1037;
    const int WM_VSCROLL = 0x0115;
    const int WM_MOUSEWHEEL = 0x020A;
    const int LVM_GETTOPINDEX = 0x102C;

    // 体检表 ListView 的状态快照：回答"为什么每一行都画了选中条 / 行底都是一个颜色"这类问题。
    //   自绘里唯一决定行底色的就是 DrawItem 里的 Selected 判断，所以先把 Selected/Focused 打出来。
    static void ListState(string mode, int page)
    {
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 1500);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
        if (mi != null) mi.Invoke(f, new object[] { page });
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }

        var sb = new System.Text.StringBuilder();
        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv == null) sb.AppendLine("找不到 lvOpt");
        else
        {
            sb.AppendLine("ListView: Items=" + lv.Items.Count + " SelectedIndices=" + lv.SelectedIndices.Count
                          + " Focused=" + lv.Focused + " FocusedItem=" + (lv.FocusedItem == null ? "null" : lv.FocusedItem.Index.ToString())
                          + " MultiSelect=" + lv.MultiSelect + " HideSelection=" + lv.HideSelection
                          + " OwnerDraw=" + lv.OwnerDraw + " ClientSize=" + lv.ClientSize);
            var cols = new System.Text.StringBuilder("  Columns: ");
            foreach (ColumnHeader c in lv.Columns) cols.Append(c.Text).Append('=').Append(c.Width).Append(" | ");
            sb.AppendLine(cols.ToString());
            long ext = SendMsg(lv.Handle, LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero).ToInt64();
            sb.AppendLine("  扩展样式 = 0x" + ext.ToString("X8") + "   LVS_EX_DOUBLEBUFFER(0x00010000) = "
                          + ((ext & 0x00010000L) != 0 ? "已开启（滚动不闪）" : "**没开**（滚动会闪白）"));
            int n = lv.Items.Count;
            for (int i = 0; i < n; i++)
            {
                ListViewItem it = lv.Items[i];
                sb.AppendLine(string.Format("  item {0,2}: Selected={1,-5} Focused={2,-5} Bounds={3} Text={4}",
                    i, it.Selected, it.Focused, it.Bounds, it.Text));
                if (it.SubItems.Count > 1)
                    sb.AppendLine(string.Format("           sub1 Bounds={0} Text={1} Name={2} Fore={3}",
                        it.SubItems[1].Bounds, it.SubItems[1].Text, it.SubItems[1].Name, it.SubItems[1].ForeColor));
            }
        }
        string outFile = Path.Combine(OUT, "lst_" + mode + "_p" + page + ".txt");
        File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine("wrote " + outFile);
        f.Close();
    }

    // ═════════════════════ 动效抓帧（2026-09-20）═════════════════════
    //  静态截图永远证明不了"过渡发生过"。所以这里在**真实窗口**上触发一次状态变化，
    //  按 ~25ms 的节奏连拍，裁出关心的区域横向拼成一条"电影条"；同时把每一帧的
    //  关键像素颜色打印出来 —— 图给眼睛看，数字给结论用（防止"看起来好像动了"）。
    //  用法：python tools/ui_snapshot.py light anim nav 0 780
    //        python tools/ui_snapshot.py light anim value 0 780
    static void Collect<T>(Control root, List<T> outl) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            T t = c as T;
            if (t != null) outl.Add(t);
            Collect<T>(c, outl);
        }
    }

    static void SwitchPage(MainForm f, int page)
    {
        try
        {
            MethodInfo mi = typeof(MainForm).GetMethod("Switch", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi != null) mi.Invoke(f, new object[] { page });
        }
        catch (Exception ex) { Console.WriteLine("switch err: " + ex.Message); }
    }

    static string HexC(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    // 控件客户区矩形 → 窗口坐标（= DrawToBitmap 位图坐标）
    static Rectangle WinRect(Form f, Control c, int pad)
    {
        Rectangle r = f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
        Point cl = f.PointToScreen(System.Drawing.Point.Empty);
        return new Rectangle(r.X + cl.X - f.Left - pad, r.Y + cl.Y - f.Top - pad,
                             r.Width + pad * 2, r.Height + pad * 2);
    }

    // 场景 → (被观察的补间、时长、曲线、拍照区域、逐格设置进度的动作)
    //  进度 p 是**缓动后**的进度；等效时刻由曲线反解（曲线单调递增，二分即可）。
    static double InvEase(double[] curve, double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        double lo = 0, hi = 1;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) * 0.5;
            if (Motion.Ease(curve, mid) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5;
    }

    static Anim GetAnim(object owner, string field)
    {
        FieldInfo fi = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
        return fi == null ? null : fi.GetValue(owner) as Anim;
    }

    static void AnimStrip(string mode, string scene, int page, int hgt, double scale)
    {
        Motion.TimeScale = 1.0;      // storyboard 不跑实时，缩放无意义
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        cfg.UiMotion = "on";         // 本机系统开关是关的，这里走"始终开启"档（顺带验配置链路）
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, hgt);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }

        // ── 先让"变化前"的状态稳定下来 ──────────────────────────────────
        int fromPage = (page == 0) ? 1 : 0;
        Rectangle cap;
        Anim aOut = null, aIn = null, aBar = null, aNum = null;
        Bar bar = null;
        var samples = new List<Point>();
        var names = new List<string>();
        double[] curve;
        int durMs;
        string title, tip;

        if (scene == "value")
        {
            SwitchPage(f, page);
            for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(25); }
            FieldInfo bf = typeof(MainForm).GetField("barCpu", BindingFlags.NonPublic | BindingFlags.Instance);
            bar = bf == null ? null : bf.GetValue(f) as Bar;
            if (bar == null) { File.WriteAllText(Path.Combine(OUT, "anim_err.txt"), "找不到 barCpu"); f.Close(); return; }
            bar.RightText = "91%";                       // 先让数字有意义，再单方面钉住进度
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
            aBar = GetAnim(bar, "aBar");
            aNum = GetAnim(bar, "aNum");
            cap = WinRect(f, bar, 6);
            int trackY = 6 + bar.Height - 13 - 6 + 3;
            samples.Add(new Point(cap.X + 6 + Theme.S(13) + 60, cap.Y + trackY));
            names.Add("轨道@+60px");
            curve = Motion.Decel; durMs = Motion.Medium;
            title = mode + " · 指标瓦片数值补间 storyboard（CPU 顶到 91%，总时长 " + durMs + "ms · standard.decelerate）";
            tip = "每格 = 把补间钉在该进度上拍的静态画面；进度→时刻由曲线反解，所以标签里的 t 是该进度真正发生的时刻";
        }
        else
        {
            SwitchPage(f, fromPage);
            for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(25); }
            var navs = new List<NavBtn>();
            Collect<NavBtn>(f, navs);
            NavBtn nOut = null, nIn = null;
            foreach (NavBtn n in navs)
            {
                if (n.NavIndex == fromPage) nOut = n;
                if (n.NavIndex == page) nIn = n;
            }
            if (nOut == null || nIn == null) { File.WriteAllText(Path.Combine(OUT, "anim_err.txt"), "找不到 NavBtn"); f.Close(); return; }
            aOut = GetAnim(nOut, "aAct");
            aIn = GetAnim(nIn, "aAct");
            Rectangle r1 = WinRect(f, nOut, 0), r2 = WinRect(f, nIn, 0);
            cap = Rectangle.Union(r1, r2);
            cap.Inflate(6, 6);
            // 采样点取药丸**右端**（避开文字字形与图标胶囊，否则读到的是抗锯齿边缘色）
            samples.Add(new Point(r1.X + r1.Width - Theme.S(16), r1.Y + r1.Height / 2));
            names.Add("第" + fromPage + "项");
            samples.Add(new Point(r2.X + r2.Width - Theme.S(16), r2.Y + r2.Height / 2));
            names.Add("第" + page + "项");
            curve = Motion.EmphDec; durMs = Motion.Normal;
            title = mode + " · 侧栏选中药丸淡入淡出 storyboard（第" + fromPage + "项 -> 第" + page + "项，总时长 " + durMs + "ms · emphasized.decelerate）";
            tip = "每格 = 把补间钉在该进度上拍的静态画面；进度→时刻由曲线反解，标签里的 t 是该进度真正发生的时刻";
        }

        // ── 逐格：钉住进度 → 拍照 ────────────────────────────────────────
        //  按**时间**均分取样（而不是按进度）：decelerate 曲线前段极快，
        //  按进度取点会让 10 格里 8 格挤在前 15ms 内，看不出过程。
        //  每格把补间钉在 x 时刻对应的进度上（Snap），所以抓帧耗时不再影响画面内容。
        double[] xs = new double[] { 0.0, 0.04, 0.08, 0.15, 0.25, 0.4, 0.6, 0.8, 1.0 };
        var shots = new List<Bitmap>();
        var log = new List<string>();
        log.Add("场景=" + title);
        log.Add("说明=每格把补间钉在指定时刻对应的进度上（Anim.Snap），故抓帧耗时不影响画面；活动补间应恒为 0");
        log.Add("时刻ms  进度(缓动后)  活动补间数  采样点颜色");
        for (int i = 0; i < xs.Length; i++)
        {
            double x = xs[i];
            double p = Motion.Ease(curve, x);
            if (aOut != null) aOut.Snap(1.0 - p);
            if (aIn != null) aIn.Snap(p);
            if (aBar != null) aBar.Snap(p * 91.0);
            if (aNum != null) aNum.Snap(p * 91.0);
            Application.DoEvents();
            Thread.Sleep(12);
            Application.DoEvents();

            var full = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(full, new Rectangle(0, 0, f.Width, f.Height));
            var cell = new Bitmap(cap.Width, cap.Height);
            using (var g = Graphics.FromImage(cell))
                g.DrawImage(full, new Rectangle(0, 0, cap.Width, cap.Height), cap, GraphicsUnit.Pixel);
            full.Dispose();
            shots.Add(cell);

            int tMs = (int)Math.Round(x * durMs);
            string line = string.Format("{0,5}  {1,10}  {2,10}  ", tMs, p.ToString("0.0000"), Motion.LiveCount);
            for (int s = 0; s < samples.Count; s++)
            {
                int px = samples[s].X - cap.X, py = samples[s].Y - cap.Y;
                Color c = (px >= 0 && py >= 0 && px < cell.Width && py < cell.Height) ? cell.GetPixel(px, py) : Color.Black;
                line += names[s] + "=" + HexC(c) + "  ";
            }
            if (bar != null) line += "条宽补间值=" + bar.ShownValue.ToString("0.0");
            log.Add(line);
        }

        // ── 拼片 ────────────────────────────────────────────────────────
        int cellH = cap.Height + 24, cellW = cap.Width;
        int W = (cellW + 10) * shots.Count + 10, H = cellH + 48;
        using (var sheet = new Bitmap(W, H))
        using (var g = Graphics.FromImage(sheet))
        using (var fT = new Font("Segoe UI", 8.5f))
        using (var fH = new Font("Segoe UI", 11f, FontStyle.Bold))
        using (var wb = new SolidBrush(Color.White))
        using (var tb = new SolidBrush(Color.FromArgb(30, 34, 40)))
        using (var pb = new SolidBrush(Color.FromArgb(110, 118, 130)))
        {
            g.FillRectangle(wb, 0, 0, W, H);
            g.DrawString(title, fH, tb, 10, 8);
            g.DrawString(tip, fT, pb, 10, 28);
            for (int i = 0; i < shots.Count; i++)
            {
                int x = 10 + i * (cellW + 10), y = 50;
                g.DrawImage(shots[i], x, y, cellW, cap.Height);
                using (var pen = new Pen(Color.FromArgb(202, 208, 216)))
                    g.DrawRectangle(pen, x, y, cellW - 1, cap.Height - 1);
                string p0 = Motion.Ease(curve, xs[i]).ToString("0.00");
                string t0 = ((int)Math.Round(xs[i] * durMs)).ToString();
                g.DrawString("t=" + t0 + "ms", fT, tb, x, y + cap.Height + 2);
                g.DrawString("p=" + p0, fT, pb, x, y + cap.Height + 13);
            }
            sheet.Save(Path.Combine(OUT, "anim_" + scene + "_" + mode + ".png"), ImageFormat.Png);
        }
        foreach (var b in shots) b.Dispose();
        File.WriteAllLines(Path.Combine(OUT, "anim_" + scene + "_" + mode + ".txt"), log.ToArray());
        f.Close();

        // ── ② 时序采样：证明"按真实时间连续推进 + 空闲后泵自停" ────────────
        var cfg2 = Config.Load(Program.ConfigPath);
        cfg2.UiTheme = mode;
        cfg2.UiMotion = "on";
        var f2 = new MainForm(cfg2, false);
        f2.StartPosition = FormStartPosition.Manual;
        f2.Location = new Point(-6000, -6000);
        f2.Size = new Size(1180, hgt);
        f2.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        var own = new List<NavBtn>();
        Collect<NavBtn>(f2, own);
        foreach (NavBtn n in own) n.Active = (n.NavIndex == 1);
        for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(25); }
        NavBtn w = null;
        foreach (NavBtn n in own) if (n.NavIndex == 0) w = n;
        Anim aw = w == null ? null : GetAnim(w, "aAct");
        if (w != null) w.Active = true;                       // 触发第 0 项的淡入
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        var cad = new List<string>();
        cad.Add("场景 = 第0项药丸淡入，时长 " + Motion.Normal + "ms（emphasized.decelerate）；每 ~20ms 采一次");
        cad.Add("实测ms  补间值(0..1)  活动补间数");
        while (sw.ElapsedMilliseconds < Motion.Normal * 2 + 120)
        {
            cad.Add(string.Format("{0,6}  {1,12}  {2,12}", sw.ElapsedMilliseconds,
                aw == null ? "-" : aw.Value.ToString("0.0000"), Motion.LiveCount));
            Thread.Sleep(20);
            Application.DoEvents();
        }
        File.WriteAllLines(Path.Combine(OUT, "anim_cadence_" + mode + ".txt"), cad.ToArray());
        Console.WriteLine("done");
        f2.Close();
    }

    // 复现「共享缓存 GDI+ 对象被 Dispose 后，第二次重绘在已销毁句柄上作图」。
    //   源码里任何 `using (var x = Theme.Solid/Hair(...))` 都会把**缓存字典里那个实例**销毁，
    //   之后每次重绘都在尸体上作图 → ArgumentException「参数无效」→ WinForms 只中断这一次绘制，
    //   用户看到的就是「滚动后文字整片消失」/偶发红色绘制失败标记。
    //   本模式先正常画一帧（等价于那次 using 已经跑过），再强制重绘，拍第二帧的长相。
    static void PaintFail(string mode, int page, int hgt)
    {
        // 异常猎手：把「启动 → 逐页切换 → 明细表滚到底再滚回 → 整窗强制重绘」跑一遍，
        // 接管 Application.ThreadException 并**连堆栈一起落盘**。
        // 为什么要这样：源码里"在已销毁的 GDI+ 句柄上作图"这类路径（ArgumentException 参数无效）
        //   只在真实重绘时序下才暴露，光读代码看不出来；有了堆栈就能直接指到行。
        //   ⚠ SetUnhandledExceptionMode 只能在**创建任何控件之前**调用。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        var errs = new List<string>();
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            errs.Add("【" + (errs.Count + 1) + "】" + e.Exception.GetType().Name + ": " + e.Exception.Message
                     + "\r\n" + e.Exception.StackTrace);
        };
        // ① 机制自检：共享缓存刷子被 Dispose 之后再复用，到底会不会抛；
        //    顺带 dump Brush/Pen 的私有字段名 —— 存活探测靠反射读它，字段名猜错探测就静默失效。
        try
        {
            var names = new List<string>();
            foreach (FieldInfo ff in typeof(Brush).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                names.Add(ff.Name + ":" + ff.FieldType.Name);
            errs.Add("Brush 私有实例字段 = " + string.Join(" | ", names.ToArray()));
            names.Clear();
            foreach (FieldInfo ff in typeof(Pen).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                names.Add(ff.Name + ":" + ff.FieldType.Name);
            errs.Add("Pen 私有实例字段 = " + string.Join(" | ", names.ToArray()));
        }
        catch (Exception ex) { errs.Add("字段 dump 失败: " + ex.Message); }
        try
        {
            var b0 = Theme.Solid(Theme.Bg);
            FieldInfo fi = typeof(Brush).GetField("nativeBrush", BindingFlags.NonPublic | BindingFlags.Instance);
            errs.Add("dispose 前 nativeBrush = " + (fi == null ? "字段不存在" : (fi.GetValue(b0) == null ? "null" : "非 null")));
            ((IDisposable)b0).Dispose();
            errs.Add("dispose 后 nativeBrush = " + (fi == null ? "字段不存在" : (fi.GetValue(b0) == null ? "null" : "非 null")));
            var b1 = Theme.Solid(Theme.Bg);
            using (var g = Graphics.FromImage(new Bitmap(8, 8))) g.FillRectangle(b1, 0, 0, 4, 4);
            errs.Add("机制自检：缓存刷子被 Dispose 后复用 -> 未抛异常");
        }
        catch (Exception ex)
        {
            errs.Add("机制自检：缓存刷子被 Dispose 后复用 -> 抛 " + ex.GetType().Name + ": " + ex.Message);
        }
        var cfg = Config.Load(Program.ConfigPath);
        cfg.UiTheme = mode;
        cfg.UiMotion = "on";
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, hgt);
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
        // ② 逐页切换 + 每次强制整窗重绘（复现"切页后重绘"路径）
        for (int p = 0; p < 7; p++)
        {
            SwitchPage(f, p);
            for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(30); }
            f.Invalidate(true); f.Update();
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(30); }
        }
        // ③ 性能优化页：明细表来回滚（用户说的"快速滑动"）
        SwitchPage(f, 1);
        for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(30); }
        FieldInfo lf = typeof(MainForm).GetField("lvOpt", BindingFlags.NonPublic | BindingFlags.Instance);
        ListView lv = lf == null ? null : lf.GetValue(f) as ListView;
        if (lv != null)
        {
            for (int k = 0; k < 3; k++)
                for (int r = 0; r < lv.Items.Count; r++) { lv.EnsureVisible(r); Application.DoEvents(); }
            errs.Add("滚动完成，明细表 " + lv.Items.Count + " 行");
        }
        else errs.Add("找不到 lvOpt（滚动被跳过）");
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(30); }
        f.Invalidate(true); f.Update();
        for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
        var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(OUT, "paintfail_" + mode + ".png"), ImageFormat.Png);
        bmp.Dispose();
        File.WriteAllLines(Path.Combine(OUT, "paintfail_" + mode + ".txt"), errs.ToArray());
        f.Close();
    }

    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "light";
        string what = args.Length > 1 ? args[1] : "showcase";
        Theme.SetMode(mode);
        Directory.CreateDirectory(OUT);
        try
        {
            if (what == "showcase") Showcase(mode);
            else if (what == "closedlg") CloseDlg(mode);
            else if (what == "covers") Covers();
            else if (what == "webcovers") WebCovers();
            else if (what == "geom") Geom(mode, int.Parse(args.Length > 2 ? args[2] : "3"));
            else if (what == "geompage") GeomPage(mode, int.Parse(args.Length > 2 ? args[2] : "3"));
            else if (what == "themeswap") ThemeSwap(int.Parse(args.Length > 2 ? args[2] : "6"));
            else if (what == "selfheal") SelfHeal();
            else if (what == "ghostmatrix") GhostMatrix();
            else if (what == "ghostscan") GhostScan();
            else if (what == "recdump") RecDump(mode);
            else if (what == "wiredump") WireDump(mode);
            else if (what == "recgame") RecGame(mode, int.Parse(args.Length > 2 ? args[2] : "2"), args.Length > 3 ? args[3] : "鸣潮");
            else if (what == "capture") Capture(mode, int.Parse(args.Length > 2 ? args[2] : "3"),
                                                int.Parse(args.Length > 3 ? args[3] : "1180"),
                                                int.Parse(args.Length > 4 ? args[4] : "1000"),
                                                int.Parse(args.Length > 5 ? args[5] : "1744"),
                                                int.Parse(args.Length > 6 ? args[6] : "537"),
                                                args.Length > 7 ? args[7] : "g");
            else if (what == "hit") Hit(int.Parse(args.Length > 2 ? args[2] : "3"),
                                        int.Parse(args.Length > 3 ? args[3] : "1180"),
                                        int.Parse(args.Length > 4 ? args[4] : "1000"),
                                        int.Parse(args.Length > 5 ? args[5] : "1920"),
                                        int.Parse(args.Length > 6 ? args[6] : "1080"));
            else if (what == "lst") ListState(mode, int.Parse(args.Length > 2 ? args[2] : "1"));
            else if (what == "scroll") ScrollStress(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                    int.Parse(args.Length > 3 ? args[3] : "12"));
            else if (what == "scrolldrag") ScrollDrag(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                      int.Parse(args.Length > 3 ? args[3] : "15"));
            else if (what == "view") ViewAt(mode, int.Parse(args.Length > 2 ? args[2] : "6"),
                                       int.Parse(args.Length > 3 ? args[3] : "0"));
        else if (what == "pagescroll") PageScrollStress(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                           int.Parse(args.Length > 3 ? args[3] : "15"),
                                                           int.Parse(args.Length > 4 ? args[4] : "7"));
            else if (what == "realwheel") RealWheel(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                    int.Parse(args.Length > 3 ? args[3] : "8"));
            else if (what == "dumprows") DumpRows(mode, int.Parse(args.Length > 2 ? args[2] : "1"));
            else if (what == "faultinject") FaultInject(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                       int.Parse(args.Length > 3 ? args[3] : "3"));
            else if (what == "headermenu") HeaderMenuProbe(mode, int.Parse(args.Length > 2 ? args[2] : "1"));
            else if (what == "colwatch") ColWatch(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                                  int.Parse(args.Length > 3 ? args[3] : "10"),
                                                  int.Parse(args.Length > 4 ? args[4] : "7"));
            else if (what == "anim")
                AnimStrip(mode, args.Length > 2 ? args[2] : "nav",
                          int.Parse(args.Length > 3 ? args[3] : "0"),
                          int.Parse(args.Length > 4 ? args[4] : "780"),
                          args.Length > 5 ? double.Parse(args[5]) : 8.0);
            else if (what == "paintfail")
            {
                // winexe 丢了 Console，异常必须落盘才看得到
                try { PaintFail(mode, int.Parse(args.Length > 2 ? args[2] : "0"),
                                int.Parse(args.Length > 3 ? args[3] : "780")); }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(OUT, "paintfail_err.txt"),
                                      ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.ToString());
                }
            }
            else if (what == "pix") Pixels(mode, int.Parse(args.Length > 2 ? args[2] : "1"),
                                           (args.Length > 3 ? args[3] : "h")[0],
                                           int.Parse(args.Length > 4 ? args[4] : "0"),
                                           int.Parse(args.Length > 5 ? args[5] : "0"),
                                           int.Parse(args.Length > 6 ? args[6] : "100"));
            else RealPage(mode, int.Parse(args.Length > 2 ? args[2] : "3"),
                          "real_" + mode + "_p" + (args.Length > 2 ? args[2] : "3")
                          + (args.Length > 3 ? "_h" + args[3] : "") + (args.Length > 4 ? "_s" + args[4] : "") + ".png",
                          args.Length > 3 ? int.Parse(args[3]) : 800,
                          args.Length > 4 ? int.Parse(args[4]) : -1);
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE ERROR: " + ex);
            if (capLog != null)
            {
                capLog.WriteLine("PROBE ERROR: " + ex.Message);
                capLog.WriteLine(ex.ToString());
                CapEnd("scroll_report.txt");
            }
        }
    }
}
