// ============================================================================
//  ui_stall_probe —— 「UI 线程被周期性阻塞」量化探针
//  ---------------------------------------------------------------------------
//  背景（2026-09-20 用户反馈）：性能优化页"时不时卡死，不显示任何东西也无法动"。
//  程序日志 17:08 那一场**没有任何异常** —— 不是崩溃，是主线程被卡住。
//  嫌疑：MainForm.sysTimer(4s) 的 Tick 在 **UI 线程**上直接调 Program.GuardTick()，
//        其中每一拍都跑 NetWatchTick（2 组 × 4 次 ping，各 1500ms 超时 = 最坏 12s），
//        每 5 拍（≈20s）跑 WatchDisplayEvents（**WMI 查系统事件日志**）。
//
//  用法：python tools\run_stall_probe.py [a|b|c|all]
//        a = GuardTick 逐次计时（默认）
//        b = 再叠加网络哨兵
//        c = 真机消息循环 + 后台看门狗测 UI 往返延迟（70s）
//  输出：Fluxion\ui-snapshots\stall_report.txt（winexe 拿不到 stdout，结论一律落盘）
//        ⚠ 每写一行就整份重写一次 —— 探针万一中途崩了，也要留下现场。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Fluxion;

class StallProbe
{
    static readonly StringBuilder R = new StringBuilder();
    static readonly string Dir = @"D:\youhua\Fluxion\ui-snapshots";
    static readonly string Rpt = Path.Combine(Dir, "stall_report.txt");

    static void W(string s)
    {
        R.AppendLine(s);
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Rpt, R.ToString(), Encoding.UTF8); } catch { }
        Console.WriteLine(s);
    }

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "a";
        try
        {
            W("========== Fluxion UI 卡顿探针 ==========");
            W("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "   模式: " + mode);
            W("");

            var cfg = Config.Load(Program.ConfigPath);
            Program.Cfg = cfg;
            try { Program.EnsureLog(); } catch { }
            W("配置已载入: IncidentEnable=" + cfg.IncidentEnable + " DiskGuardEnable=" + cfg.DiskGuardEnable
              + " NetWatchEnable=" + cfg.NetWatchEnable + " NetWatchEveryMin=" + cfg.NetWatchEveryMin);
            W("");

            if (mode == "a" || mode == "all")
            {
                cfg.IncidentEnable = true;      // 默认就是 true（黑屏取证）
                cfg.DiskGuardEnable = false;    // 每 150 拍才跑，先排除
                cfg.NetWatchEnable = false;     // 默认 false，先排除
                W("— A. GuardTick() 直接计时（IncidentEnable=true，无网络哨兵）—");
                for (int i = 1; i <= 11; i++)
                {
                    var sw = Stopwatch.StartNew();
                    string err = "";
                    try { Program.GuardTick(); } catch (Exception ex) { err = " 抛异常 " + ex.GetType().Name + ": " + ex.Message; }
                    sw.Stop();
                    W("   #" + i + "  " + sw.ElapsedMilliseconds + " ms"
                      + (i % 5 == 0 ? "   ← 含 WMI 事件日志扫描" : "") + err);
                    Application.DoEvents();
                }
                W("");
            }

            if (mode == "b" || mode == "all")
            {
                cfg.IncidentEnable = false;
                cfg.NetWatchEnable = true;
                cfg.NetWatchEveryMin = 0;       // 强制每拍都跑
                W("— B. 打开网络哨兵（2 组 × 4 次 ping，单次超时 1500ms）—");
                for (int i = 1; i <= 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    try { Program.GuardTick(); } catch { }
                    sw.Stop();
                    W("   #" + i + "  " + sw.ElapsedMilliseconds + " ms");
                }
                cfg.NetWatchEnable = false;
                W("");
            }

            if (mode == "c" || mode == "all")
            {
                cfg.IncidentEnable = true;      // 恢复默认（黑屏取证 20s 一次 WMI），给 C 段用
                W("— C. 真机 UI 往返延迟实测（默认配置，跑 75s；停顿 >300ms 全部记录）—");
                RunStallTest(cfg);
            }

            if (mode == "d" || mode == "all")
            {
                W("— D. GetStatusItems()（优化项明细的数据源，跑在后台线程）逐次计时 —");
                for (int i = 1; i <= 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    List<StatusItem> items = null;
                    string err = "";
                    try { items = Program.GetStatusItems(); }
                    catch (Exception ex) { err = " 抛异常 " + ex.GetType().Name + ": " + ex.Message; }
                    sw.Stop();
                    W("   #" + i + "  " + sw.ElapsedMilliseconds + " ms   行数=" + (items == null ? -1 : items.Count) + err);
                }
                W("");
            }

            if (mode == "e" || mode == "all")
            {
                W("— E. 真机：点「刷新」后，明细表到底多久才填上（最多等 30s）—");
                RunGridFillTest(cfg);
            }

            if (mode == "g" || mode == "all")
            {
                W("— G. 宽度扫描：lvOpt.Resize → FillLastColumn 是否与滚动条形成震荡死循环 —");
                RunResizeSweep(cfg);
            }

            if (mode == "h" || mode == "all")
            {
                W("— H. 换主题 = 整棵控件树原地重建，逐次计时（用户卡死前刚做过这件事）—");
                RunRebuildTiming(cfg);
            }

            W("");
            W("========== 探针结束 ==========");
        }
        catch (Exception ex)
        {
            W("!! 探针自身异常: " + ex.GetType().Name + ": " + ex.Message);
            W(ex.StackTrace);
        }
    }

    // ⚠ volatile 不能修饰局部变量（CS0106）—— 看门狗线程要读，所以提到类字段
    static volatile bool stopWatch;

    // ⚠ 不要用 Application.Run(f) 收尾：本程序「关闭窗口时」默认是**最小化到托盘**，
    //   f.Close() 只会把窗口藏起来、消息循环照转 → 探针永远不结束
    //   （2026-09-20 踩到：前一版探针就这样挂在那儿 10 分钟，报告一个字没落盘）。
    //   改用 ui_snapshot 一直在用的 DoEvents 泵：主线程自己泵消息，循环次数有界，必停。
    static void RunStallTest(Config cfg)
    {
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1180, 760);
        f.Show();
        Pump(20);

        var stalls = new List<long>();      // 停顿毫秒
        var atSec = new List<double>();     // 停顿发生的相对秒
        stopWatch = false;
        var sw = Stopwatch.StartNew();

        // 看门狗：后台线程往 UI 线程投一个空操作，量它多久才被执行。
        // UI 线程若正卡在同步调用里，延迟就直接等于那次卡住的时长。
        var wd = new Thread(delegate()
        {
            while (!stopWatch)
            {
                long t0 = sw.ElapsedMilliseconds;
                var ev = new ManualResetEventSlim(false);
                try { f.BeginInvoke((Action)(delegate { try { ev.Set(); } catch { } })); }
                catch { Thread.Sleep(200); continue; }
                ev.Wait(30000);
                long lag = sw.ElapsedMilliseconds - t0;
                if (lag > 300) { lock (stalls) { stalls.Add(lag); atSec.Add(t0 / 1000.0); } }
                Thread.Sleep(150);
            }
        });
        wd.IsBackground = true;

        W("   看门狗已启动，负载 = 切页 + 换主题 + 滚动（复刻用户当时操作）");
        wd.Start();

        // 负载：复刻用户 17:08 那一场（切页 → 换主题深/浅 → 动效开 → 停在性能优化页）
        Mark("切到性能优化页 + 刷新明细", sw);
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        MethodInfoReflect(f, "RefreshGridAsync", null);
        Pump(30);

        Mark("换主题 → 深色", sw);
        MethodInfoReflect(f, "DoSwitchTheme", new object[] { true });
        Pump(30);

        Mark("换主题 → 浅色", sw);
        MethodInfoReflect(f, "DoSwitchTheme", new object[] { false });
        Pump(30);

        Mark("循环切页 x8（含性能优化页）", sw);
        for (int r = 0; r < 8; r++)
        {
            MethodInfoReflect(f, "Switch", new object[] { r % 7 });
            Pump(6);
        }

        Mark("停在性能优化页，滚动明细表", sw);
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        var lv = GetField(f, "lvOpt") as ListView;
        for (int r = 0; r < 3 && lv != null && lv.Items.Count > 0; r++)
        {
            lv.EnsureVisible(lv.Items.Count - 1); lv.Refresh(); Pump(4);
            lv.EnsureVisible(0); lv.Refresh(); Pump(4);
        }

        Mark("静置观察 30s（看门狗/定时器是否周期性卡）", sw);
        for (int i = 0; i < 300; i++) { Application.DoEvents(); Thread.Sleep(100); }

        stopWatch = true;
        Thread.Sleep(400);
        f.Hide();

        lock (stalls)
        {
            long max = 0; foreach (long v in stalls) if (v > max) max = v;
            W("   共测到 " + stalls.Count + " 次 >300ms 停顿；最长 " + max + " ms");
            for (int i = 0; i < stalls.Count; i++)
                W("     [" + atSec[i].ToString("F1") + "s] " + stalls[i] + " ms");
            W("   时间轴标记:");
            foreach (string m in MarkBuffer) W("     " + m);
        }
    }

    // 真机端到端：切到性能优化页 → 触发刷新 → 轮询明细表行数，看它到底会不会填上、要多久
    static void RunGridFillTest(Config cfg)
    {
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(20);
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        Pump(10);
        var lv = GetField(f, "lvOpt") as ListView;
        W("   明细表控件: " + (lv == null ? "找不到!" : "已就绪"));
        W("   切页后行数 = " + (lv == null ? -1 : lv.Items.Count));

        // ① 首次刷新
        var sw = Stopwatch.StartNew();
        MethodInfoReflect(f, "RefreshGridAsync", null);
        long filledAt = -1;
        for (int i = 0; i < 600; i++)          // 最多 30s
        {
            Application.DoEvents(); Thread.Sleep(50);
            if (lv != null && lv.Items.Count > 0) { filledAt = sw.ElapsedMilliseconds; break; }
        }
        W("   ① 触发刷新 → " + (filledAt < 0 ? "30s 内**始终是 0 行**（界面看着就是空的）"
                                              : filledAt + " ms 后填上 " + lv.Items.Count + " 行"));

        // ② 换主题（原地重建控件树）后再看：重建后有没有人回填？
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        MethodInfoReflect(f, "DoSwitchTheme", new object[] { !Theme.Dark });
        Pump(30);
        var lv2 = GetField(f, "lvOpt") as ListView;
        W("   ② 换主题后行数 = " + (lv2 == null ? -1 : lv2.Items.Count)
          + "（控件是否换新: " + (!object.ReferenceEquals(lv, lv2)) + "）");
        var sw2 = Stopwatch.StartNew();
        long filledAt2 = -1;
        for (int i = 0; i < 400; i++)
        {
            Application.DoEvents(); Thread.Sleep(50);
            if (lv2 != null && lv2.Items.Count > 0) { filledAt2 = sw2.ElapsedMilliseconds; break; }
        }
        W("   ② 换主题后等 20s → " + (filledAt2 < 0 ? "**仍是 0 行，且没有任何机制会去回填**"
                                                     : filledAt2 + " ms 后自行填上 " + lv2.Items.Count + " 行"));

        f.Hide();
        W("");
    }

    // 宽度逐像素扫描：如果 FillLastColumn 与滚动条互相触发，某个宽度上 Resize 次数会爆掉
    static void RunResizeSweep(Config cfg)
    {
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(20);
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        MethodInfoReflect(f, "RefreshGridAsync", null);
        for (int i = 0; i < 200; i++) { Application.DoEvents(); Thread.Sleep(25); }

        var lv = GetField(f, "lvOpt") as ListView;
        if (lv == null) { W("   找不到 lvOpt"); return; }
        W("   行数=" + lv.Items.Count + "  列数=" + lv.Columns.Count
          + "  列宽=" + ColWidths(lv) + "  客户区=" + lv.ClientSize.Width + "x" + lv.ClientSize.Height);

        int hits = 0;
        lv.Resize += delegate { hits++; };

        // 行数多时竖滚动条必然出现；宽度变化会让竖/横滚动条交替开关 —— 正是震荡的温床
        int worstW = -1, worstN = 0;
        long worstMs = 0;
        for (int w = 960; w <= 1360; w += 1)
        {
            hits = 0;
            var sw = Stopwatch.StartNew();
            f.Width = w;
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(6); }
            sw.Stop();
            if (hits > worstN) { worstN = hits; worstW = w; worstMs = sw.ElapsedMilliseconds; }
            if (hits > 12 || sw.ElapsedMilliseconds > 400)
                W("   ⚠ 宽度 " + w + "：Resize 触发 " + hits + " 次，耗时 " + sw.ElapsedMilliseconds
                  + " ms（列宽=" + ColWidths(lv) + " 客户区=" + lv.ClientSize.Width + "x" + lv.ClientSize.Height
                  + " HScroll=" + HasHScroll(lv) + "）");
        }
        W("   最坏点：宽度 " + worstW + " → Resize " + worstN + " 次 / " + worstMs + " ms"
          + (worstN > 12 ? "   ⇒ **存在震荡**" : "   ⇒ 未见震荡（正常应 ≤ 5 次）"));
        f.Hide();
        W("");
    }

    const int LVM_FIRST = 0x1000;
    const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    const int WS_HSCROLL = 0x00100000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    static string ColWidths(ListView lv)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lv.Columns.Count; i++) sb.Append(lv.Columns[i].Width).Append(i == lv.Columns.Count - 1 ? "" : "|");
        return sb.ToString();
    }

    static bool HasHScroll(ListView lv)
    {
        try { return (GetWindowLong(lv.Handle, -16 /*GWL_STYLE*/) & WS_HSCROLL) != 0; }
        catch { return false; }
    }

    static void RunRebuildTiming(Config cfg)
    {
        var f = new MainForm(cfg, false);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.Size = new Size(1056, 739);
        f.Show();
        Pump(20);
        MethodInfoReflect(f, "Switch", new object[] { 1 });
        Pump(20);

        // 看门狗：重建期间 UI 线程到底被占住多久
        var worst = 0L;
        var worstAt = "";
        for (int round = 1; round <= 4; round++)
        {
            bool toDark = !Theme.Dark;
            var sw = Stopwatch.StartNew();
            var ev = new ManualResetEventSlim(false);
            Thread t = new Thread(delegate()
            {
                long t0 = sw.ElapsedMilliseconds;
                try { f.BeginInvoke((Action)(delegate { ev.Set(); })); } catch { }
                ev.Wait(60000);
                long lag = sw.ElapsedMilliseconds - t0;
                if (lag > worst) { worst = lag; worstAt = "第 " + 0 + " 轮"; }
            });
            t.IsBackground = true; t.Start();

            var sw2 = Stopwatch.StartNew();
            MethodInfoReflect(f, "DoSwitchTheme", new object[] { toDark });
            sw2.Stop();                       // ← 这一行返回时，整棵树已经重建完
            long buildMs = sw2.ElapsedMilliseconds;

            var lv = GetField(f, "lvOpt") as ListView;
            int atOnce = lv == null ? -1 : lv.Items.Count;   // 重建刚结束时：应是占位行 1 行
            string placeholder = (lv != null && lv.Items.Count == 1) ? lv.Items[0].Text : "";
            long filledAt = -1; int final = atOnce;
            var sw3 = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)                 // 最多 10s
            {
                Application.DoEvents(); Thread.Sleep(50);
                if (lv != null && lv.Items.Count > 1) { filledAt = sw3.ElapsedMilliseconds; final = lv.Items.Count; break; }
            }
            W("   第 " + round + " 轮 → " + (toDark ? "深色" : "浅色")
              + "：重建耗时 " + buildMs + " ms"
              + "，重建瞬时有 " + atOnce + " 行" + (placeholder.Length > 0 ? "（「" + placeholder + "」占位）" : "")
              + "，回填 " + (filledAt < 0 ? "**失败：10s 内始终 ≤1 行**" : filledAt + " ms 后 " + final + " 行")
              + (buildMs > 800 ? "   ⚠ 重建超过 800ms" : ""));
        }
        W("   重建后 1s 内 UI 线程最长无响应 = " + Math.Max(worst, 0) + " ms");
        f.Hide();
        W("");
    }

    static void Mark(string what, Stopwatch sw)
    {
        // 即时落盘（W 每次整份重写）：探针若被中途打死，也知道最后走到哪一步
        W("   >> " + (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + "s  " + what);
    }

    static readonly List<string> MarkBuffer = new List<string>();

    static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(40); } }

    static object GetField(object o, string name)
    {
        var fi = o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return fi == null ? null : fi.GetValue(o);
    }

    static void MethodInfoReflect(object o, string name, object[] arg)
    {
        var mi = o.GetType().GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (mi != null) mi.Invoke(o, arg);
    }
}
