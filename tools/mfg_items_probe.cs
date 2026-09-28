using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// v2.10.2 断言：倍率下拉真的有 5 项（只读检查，绝不触发任何写盘动作）。
//  故意**不**调用 DoApplyFgCfg()：那个方法会遍历游戏列表、把 ini 写进真实游戏目录，
//  拿用户的配置做实验是不可接受的。索引 → MaxGeneratedFrames 的映射由源码一行确定
//  （Cfg.DlssgMaxFrames = cbMaxFrames.SelectedIndex + 1），另有沙盒探针单独验证写入正确性。
namespace Fluxion
{
    static class MfgItemsProbe
    {
        static StringBuilder Out = new StringBuilder();
        static int Fail = 0;
        static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
        static void Chk(string n, bool ok, string d)
        {
            if (!ok) Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + n + (d.Length > 0 ? "  -> " + d : ""));
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                var cfg = Config.Load(Program.ConfigPath);
                Program.Cfg = cfg;
                L("配置里的 maxGeneratedFrames = " + cfg.DlssgMaxFrames);

                var f = new MainForm(cfg, false);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(-6000, -6000);
                f.Show();
                for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                FieldInfo fi = typeof(MainForm).GetField("cbMaxFrames",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Chk("找到 cbMaxFrames 字段", fi != null, fi == null ? "null" : fi.FieldType.Name);
                if (fi == null) { f.Close(); return 1; }

                object combo = fi.GetValue(f);
                Chk("实例非空", combo != null, combo == null ? "null" : combo.GetType().Name);

                // RoundCombo.Items 是 public readonly **字段**（类型是自定义的 ItemList），
                // 不是属性；ItemList 有 Count 属性 + this[int] 索引器，没有 IEnumerable。
                FieldInfo itemsF = combo.GetType().GetField("Items");
                Chk("找到 Items 字段", itemsF != null, itemsF == null ? "null" : itemsF.FieldType.Name);
                if (itemsF == null) { f.Close(); return 1; }

                object list = itemsF.GetValue(combo);
                PropertyInfo countP = list.GetType().GetProperty("Count");
                PropertyInfo idxP = list.GetType().GetProperty("Item", new Type[] { typeof(int) });
                Chk("ItemList 有 Count", countP != null, countP == null ? "null" : "ok");
                Chk("ItemList 有索引器", idxP != null, idxP == null ? "null" : "ok");
                if (countP == null || idxP == null) { f.Close(); return 1; }

                int n = Convert.ToInt32(countP.GetValue(list, null));
                string all = "";
                for (int i = 0; i < n; i++) all += "[" + Convert.ToString(idxP.GetValue(list, new object[] { i })) + "]";
                L("  实际项(" + n + "): " + all);
                Chk("恰好 5 项", n == 5, "count=" + n);
                Chk("第 1 项 = 2X", all.IndexOf("2X") >= 0, all);
                Chk("含 4 · 5X", all.IndexOf("5X") >= 0, all);
                Chk("含 5 · 6X（本轮修复点）", all.IndexOf("6X") >= 0, all);

                PropertyInfo sel = combo.GetType().GetProperty("SelectedIndex");
                object cur = sel == null ? null : sel.GetValue(combo, null);
                L("  当前选中索引 = " + cur + "（配置 maxGeneratedFrames = " + cfg.DlssgMaxFrames + "）");
                Chk("索引与配置一致（索引 = 值-1）",
                    cur != null && Convert.ToInt32(cur) == Math.Max(0, Math.Min(4, cfg.DlssgMaxFrames - 1)),
                    Convert.ToString(cur));

                // ---------- v3.3.0：DLSS 5 神经渲染（NR）开关下拉 ----------
                FieldInfo nrF = typeof(MainForm).GetField("cbNr", BindingFlags.NonPublic | BindingFlags.Instance);
                Chk("找到 cbNr 字段", nrF != null, nrF == null ? "null" : nrF.FieldType.Name);
                if (nrF != null)
                {
                    object nrCombo = nrF.GetValue(f);
                    Chk("cbNr 实例非空", nrCombo != null, nrCombo == null ? "null" : nrCombo.GetType().Name);
                    if (nrCombo != null)
                    {
                        FieldInfo nritemsF = nrCombo.GetType().GetField("Items");
                        object nrlist = nritemsF == null ? null : nritemsF.GetValue(nrCombo);
                        PropertyInfo np = nrlist == null ? null : nrlist.GetType().GetProperty("Count");
                        PropertyInfo ni = nrlist == null ? null : nrlist.GetType().GetProperty("Item", new Type[] { typeof(int) });
                        if (np != null && ni != null)
                        {
                            int nn = Convert.ToInt32(np.GetValue(nrlist, null));
                            string na = "";
                            for (int i = 0; i < nn; i++) na += "[" + Convert.ToString(ni.GetValue(nrlist, new object[] { i })) + "]";
                            L("  cbNr 实际项(" + nn + "): " + na);
                            Chk("恰好 2 项", nn == 2, "count=" + nn);
                            Chk("第 1 项是「关闭（推荐）」（默认不自动开）", na.IndexOf("关闭") >= 0, na);
                            Chk("第 2 项是「开启」", na.IndexOf("开启") >= 0, na);
                            PropertyInfo nsel = nrCombo.GetType().GetProperty("SelectedIndex");
                            object ncur = nsel == null ? null : nsel.GetValue(nrCombo, null);
                            L("  当前选中索引 = " + ncur + "（配置 nrEnabled = " + cfg.DlssgNrEnabled + "）");
                            Chk("索引与配置一致", ncur != null && Convert.ToInt32(ncur) == (cfg.DlssgNrEnabled ? 1 : 0),
                                Convert.ToString(ncur));
                        }
                        else Chk("ItemList 结构可用", false, "Count/索引器缺失");
                    }
                }

                // ---------- 「方案 A」区块正文是否已改为按游戏动态 ----------
                FieldInfo noteF = typeof(MainForm).GetField("lblXeNote", BindingFlags.NonPublic | BindingFlags.Instance);
                Chk("找到 lblXeNote 字段（方案A 动态正文）", noteF != null, noteF == null ? "null" : "ok");
                if (noteF != null)
                {
                    object lb = noteF.GetValue(f);
                    string txt = lb == null ? "" : Convert.ToString(lb.GetType().GetProperty("Text").GetValue(lb, null));
                    L("  当前正文: " + (txt == null ? "" : txt.Replace("\n", " / ")));
                    Chk("正文非空且已按游戏生成（未选中时提示去游戏库）",
                        txt != null && txt.Length > 0 && txt.IndexOf("游戏库") >= 0, txt == null ? "null" : "");
                }

                f.Close();
                L("");
                L("  注：索引 -> MaxGeneratedFrames 的映射只有一行（Cfg.DlssgMaxFrames = SelectedIndex + 1），");
                L("      所以 5 项 ⇒ 最大可写 5 ⇒ 6X。写入正确性由 tools/mfg_probe.cs 在沙盒里单独验证。");
            }
            catch (Exception ex)
            {
                Fail++;
                L("EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                L(ex.StackTrace == null ? "" : ex.StackTrace);
            }
            L("");
            L(Fail == 0 ? "RESULT: ALL PASS" : ("RESULT: " + Fail + " FAILED"));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "mfg_items.txt"), Out.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
