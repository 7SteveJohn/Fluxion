using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Fluxion;

// v2.9.1 回归自检：① 配置自愈 ② 封面缓存清空后卡片仍可绘制 ③ 联动名单兜底
class FixVerify
{
    static string Out = "";
    static void L(string s) { Out += s + "\r\n"; }

    static bool JsonOk(string t)
    {
        try { new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(t); return true; }
        catch { return false; }
    }

    static void Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "gb_fixverify");
        try { Directory.CreateDirectory(tmp); } catch { }

        // ---------------- ① 配置自愈 ----------------
        L("=== 1. 配置自愈（拿本机那份坏 config.json 实测）===");
        string srcCfg = @"C:\ProgramData\Fluxion\config.json";
        string bad = Path.Combine(tmp, "config.json");
        try
        {
            File.Copy(srcCfg, bad, true);
            string before = File.ReadAllText(bad, Encoding.UTF8);
            L("  输入含 $10 占位符: " + before.Contains("$10"));
            L("  输入 JSON 合法: " + JsonOk(before));
            Config.LoadNote = "";
            Config c = Config.Load(bad);
            string after = File.ReadAllText(bad, Encoding.UTF8);
            L("  输出 JSON 合法: " + JsonOk(after) + "   （含 $ 占位符: " + after.Contains("$1") + "）");
            L("  LoadNote: " + Config.LoadNote);
            L("  → scheme=" + c.PowerScheme + "  游戏名单=" + c.GameProcesses.Count
              + "  远控名单=" + c.RemoteApps.Count + "  主题=" + c.UiTheme
              + "  档位(二游)=" + c.GachaGames.Count + "  档位(3A)=" + c.AaaGames.Count);
        }
        catch (Exception ex) { L("  FAIL: " + ex); }

        // ---------------- ③ 名单兜底 ----------------
        L("");
        L("=== 2. 联动名单兜底（配置里没有 gameAware 时）===");
        try
        {
            string e = Path.Combine(tmp, "empty.json");
            File.WriteAllText(e, "{\"power\":{\"enable\":true}}", new UTF8Encoding(false));
            Config c2 = Config.Load(e);
            L("  游戏名单数=" + c2.GameProcesses.Count + " → " + string.Join(",", c2.GameProcesses.ToArray()));
        }
        catch (Exception ex) { L("  FAIL: " + ex); }

        // ---------------- ② 封面所有权 ----------------
        L("");
        L("=== 3. 封面：CleanCache 之后卡片图还能不能画（旧代码这里必炸）===");
        try
        {
            var g = new DlssgGame();
            g.Title = "绝区零";
            g.Platform = "local";
            g.Dir = @"G:\x";
            g.Exe = "a.exe";

            Image img = CoverArt.Card(g, 148, 222);
            L("  Card() 返回: " + (img == null ? "null" : img.Width + "x" + img.Height));

            CoverArt.ClearCache();          // ← 旧代码就是在这里把卡片正持有的同一张图 Dispose 掉

            bool usable = false; string err = "";
            try
            {
                using (var b = new Bitmap(148, 222))
                using (var gr = Graphics.FromImage(b))
                {
                    gr.Clear(Color.White);
                    gr.DrawImage(img, 0, 0);        // 旧代码：ArgumentException 参数无效
                    b.Save(Path.Combine(tmp, "cover_after_clear.png"), ImageFormat.Png);
                }
                usable = true;
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            L("  ClearCache() 后仍可绘制: " + usable + (usable ? "  ✔ 修复生效" : "  ✘ " + err));
        }
        catch (Exception ex) { L("  FAIL: " + ex); }

        // ---------------- ④ 数据目录指针 ----------------
        L("");
        L("=== 4. 数据目录指针 ===");
        L("  指针路径 = " + Program.DataDirPointerPath);
        L("  当前数据目录 = " + Program.DataDir);
        L("  配置文件路径 = " + Program.ConfigPath);

        // ---------------- ⑤ 0.3.0 前置条件 ----------------
        L("");
        L("=== 5. 0.3.0 前置条件检查（对照视频简介）===");
        try { L(Dlssg030.PrereqReport()); }
        catch (Exception ex) { L("  FAIL: " + ex); }

        try { File.WriteAllText(Path.Combine(tmp, "fix_verify.txt"), Out, new UTF8Encoding(false)); } catch { }
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write(Out);
    }
}
