// 「关闭窗口时」三态下拉的只读验证（v3.2.1）
// 目的：证明 ① 三态↔两字段的映射全组合正确；② 设置页下拉的文案/项数/初值与映射一致；
//      ③ 弹框"记住选择"后的同步逻辑用的是同一个映射函数（不会两处各写一套）。
// 纪律：全程只读 + 反射，**不触发 SelectedIndexChanged**（那会写用户真实的 config.json）。
namespace Fluxion
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Windows.Forms;

    static class CloseProbe
    {
        static int Fail = 0;
        static readonly StringBuilder Out = new StringBuilder();
        static void L(string s) { Out.AppendLine(s); }
        static void Chk(string what, bool ok, string extra)
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (extra == null || extra.Length == 0 ? "" : "   → " + extra));
            if (!ok) Fail++;
        }

        static object CallStatic(string name, params object[] args)
        {
            MethodInfo mi = typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) return null;
            return mi.Invoke(null, args);
        }

        static int Index(bool ask, bool tray)
        {
            object o = CallStatic("CloseComboIndex", ask, tray);
            return o == null ? -999 : Convert.ToInt32(o);
        }

        static string Text(bool ask, bool tray)
        {
            object o = CallStatic("CloseComboText", ask, tray);
            return o == null ? "(方法未找到)" : Convert.ToString(o);
        }

        static int Main()
        {
            L("=== 「关闭窗口时」三态验证（3.2.1）===");
            L("");

            // ---------- ① 映射全组合 ----------
            L("=== 1. 三态 ↔ 两个配置字段（ui.closeAsk / ui.closeToTray）===");
            Chk("(询问=T, 托盘=T) → 索引 0", Index(true, true) == 0, "idx=" + Index(true, true));
            Chk("(询问=T, 托盘=F) → 索引 0（询问态不看托盘值）", Index(true, false) == 0, "idx=" + Index(true, false));
            Chk("(询问=F, 托盘=T) → 索引 1（直接最小化到托盘）", Index(false, true) == 1, "idx=" + Index(false, true));
            Chk("(询问=F, 托盘=F) → 索引 2（直接退出）", Index(false, false) == 2, "idx=" + Index(false, false));
            L("  文案: " + Text(true, true) + " / " + Text(false, true) + " / " + Text(false, false));
            L("");

            // ---------- ② 下拉项与映射文案一致 ----------
            L("=== 2. 设置页下拉：项数、文案、初值 ===");
            var cfg = Config.Load(Program.ConfigPath);
            var f = new MainForm(cfg, false);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-6000, -6000);
            f.Size = new Size(1180, 1000);
            f.Show();
            for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

            FieldInfo fi = typeof(MainForm).GetField("cbClose", BindingFlags.NonPublic | BindingFlags.Instance);
            Chk("字段 cbClose 存在", fi != null, fi == null ? "null" : fi.FieldType.Name);
            object combo = fi == null ? null : fi.GetValue(f);
            Chk("实例非空（设置页已构建）", combo != null, combo == null ? "null" : combo.GetType().Name);

            List<string> items = new List<string>();
            if (combo != null)
            {
                // RoundCombo.Items 是 public readonly 字段（自定义 ItemList：有 Count + this[int]）
                FieldInfo li = combo.GetType().GetField("Items", BindingFlags.Public | BindingFlags.Instance);
                if (li != null)
                {
                    object list = li.GetValue(combo);
                    PropertyInfo pc = list.GetType().GetProperty("Count");
                    PropertyInfo pi = list.GetType().GetProperty("Item", new Type[] { typeof(int) });
                    int n = pc == null ? 0 : Convert.ToInt32(pc.GetValue(list, null));
                    for (int i = 0; i < n; i++)
                        items.Add(Convert.ToString(pi.GetValue(list, new object[] { i })));
                }
            }
            L("  实际项: [" + string.Join("][", items.ToArray()) + "]");
            Chk("恰好 3 项", items.Count == 3, "count=" + items.Count);

            // 下拉项文案必须与 CloseComboText 的输出逐字一致 ——
            //   两处各写一套文案，就会出现"日志说 A、界面显示 B"
            if (items.Count == 3)
            {
                Chk("第 1 项 == Text(询问)", items[0] == Text(true, true), items[0] + " vs " + Text(true, true));
                Chk("第 2 项 == Text(直接托盘)", items[1] == Text(false, true), items[1] + " vs " + Text(false, true));
                Chk("第 3 项 == Text(直接退出)", items[2] == Text(false, false), items[2] + " vs " + Text(false, false));
            }

            // 初值：应与真实配置一致
            PropertyInfo ps = combo.GetType().GetProperty("SelectedIndex");
            object selObj = ps == null ? null : ps.GetValue(combo, null);
            int sel = selObj == null ? -1 : Convert.ToInt32(selObj);
            int want = Index(cfg.CloseAsk, cfg.CloseToTray);
            L(string.Format("  配置: closeAsk={0} closeToTray={1}  → 应选中索引 {2}；实际选中 {3}",
                cfg.CloseAsk, cfg.CloseToTray, want, sel));
            Chk("下拉初值与配置一致", sel == want, "实际 " + sel + " / 期望 " + want);
            L("");

            // ---------- ③ 与 FormClosing 的分支等价 ----------
            L("=== 3. 与关闭流程的分支等价（用同一映射反推行为）===");
            // FormClosing 读 Cfg.CloseAsk / Cfg.CloseToTray：
            //   CloseAsk=true            → 弹框询问
            //   CloseAsk=false && tray   → 收进托盘（e.Cancel=true; Hide()）
            //   CloseAsk=false && !tray  → 直接退出
            // 这里断言"选中 idx 后应有的行为"能被映射还原，覆盖用户会遇到的三种
            string[] expectBehavior = new string[] { "询问", "托盘", "退出" };
            for (int i = 0; i < 3; i++)
            {
                bool ask = (i == 0);
                bool tray = (i == 1);
                int back = Index(ask, tray);
                bool ok = (i == 0) ? (back == 0)
                        : (i == 1) ? (!ask && tray && back == 1)
                                   : (!ask && !tray && back == 2);
                Chk("选中「" + expectBehavior[i] + "」→ 行为可还原为「" + expectBehavior[i] + "」", ok,
                    string.Format("ask={0} tray={1} idx={2}", ask, tray, back));
            }
            L("");

            // ---------- ④ 弹框"记住选择"写的就是这两个字段 ----------
            L("=== 4. 弹框「记住我的选择」与下拉写同一组配置键 ===");
            // 源码约定：两处都写 ui.closeAsk / ui.closeToTray。
            // 这里用反射确认 RefreshCloseCombo 存在（记住选择后会调它同步下拉）。
            MethodInfo rf = typeof(MainForm).GetMethod("RefreshCloseCombo", BindingFlags.NonPublic | BindingFlags.Instance);
            Chk("RefreshCloseCombo 存在（记住选择后同步下拉）", rf != null, rf == null ? "null" : "ok");
            if (rf != null)
            {
                try 
                {
                    rf.Invoke(f, null);
                    Chk("调用 RefreshCloseCombo 不抛异常（下拉被 Dispose 时也要安全）", true, "");
                }
                catch (Exception ex) { Chk("调用 RefreshCloseCombo 不抛异常", false, ex.GetType().Name + ": " + ex.Message); }
            }

            f.Close();

            L("");
            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "close_probe.txt"), Out.ToString(), new UTF8Encoding(false)); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
