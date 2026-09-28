using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

// ==========================================================================
//  junk_probe.cs —— 备份 / 隔离区统计与可逆回收（Junk，P0-1）的回归自检
// ==========================================================================
//  盯的是两件事：
//   ① 统计要**如实** —— 归属解析错、体积算漏、时间认错，界面上那行"备份占用"就是假数据；
//   ② 保留策略必须在"删"之前生效 —— 每个归属里最新那一份是 Uninstall / Plan.SwitchTo
//      回滚时要拷回游戏目录的文件来源，一旦进了清理候选，用户点一次「清理」就把回滚路径断了。
//      所以既断言候选集，也断言"把保留项硬塞进回收接口会被拒"。
//
//  隔离：反射把 Program.DataDir 指到 %TEMP% 沙盒，真实的 1.48 GB 备份一个字节都不动。
//  造的文件都是 1 KB 级 —— 探针验的是判据，不是磁盘吞吐。
//  目录名一律照**实际出现过**的写法造（2026-09-21 在真机上量出来的形态）：
//     _pack-<ts> / cp2077-0.3.0-<ts> / zzz / wuwa-residual-ini-<ts> / zzz-opti-<ts> / 无时间戳的手工目录
// ==========================================================================
namespace Fluxion
{
static class JunkProbe
{
    static StringBuilder Out = new StringBuilder();
    static int Fail = 0;
    static string Sand, Data;

    static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
    static void Head(string s) { L(""); L("=== " + s + " ==="); }
    static void Chk(string name, bool ok, string detail)
    {
        if (!ok) Fail++;
        L((ok ? "  [PASS] " : "  [FAIL] ") + name
          + (detail != null && detail.Length > 0 ? "  -> " + detail : ""));
    }

    static string Stamp(int daysAgo) { return DateTime.Now.AddDays(-daysAgo).ToString("yyyyMMdd-HHmmss"); }

    // 造一个"备份目录"：files 个文件、每个 kb 千字节，体积完全可控
    static string Make(string owner, string bucket, string name, int files, int kb)
    {
        string d = Path.Combine(Data, owner);
        d = Path.Combine(d, bucket);
        d = Path.Combine(d, name);
        Directory.CreateDirectory(d);
        for (int i = 0; i < files; i++) File.WriteAllBytes(Path.Combine(d, "f" + i + ".dll"), new byte[kb * 1024]);
        return d;
    }

    static JunkItem At(List<JunkItem> all, string dir)
    {
        foreach (JunkItem it in all)
            if (string.Equals(it.Dir, dir, StringComparison.OrdinalIgnoreCase)) return it;
        return null;
    }
    static bool Has(List<JunkItem> list, string dir) { return At(list, dir) != null; }

    [STAThread]
    static int Main(string[] args)
    {
        // 控制台默认按 OEM 代码页（本机 936）出字节，runner 用 UTF-8 解码就会把中文全变成乱码 ——
        //  探针报告读不懂就等于没跑。开局就把输出钉成 UTF-8。
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Sand = args.Length > 0 ? args[0]
             : Path.Combine(Path.GetTempPath(), @"gb_junk_probe\" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Data = Path.Combine(Sand, "data");
        L("沙盒     " + Sand);
        L("数据目录 " + Data);
        Directory.CreateDirectory(Data);

        Head("0. 把 DataDir 隔离进沙盒（真实备份不参与）");
        try
        {
            FieldInfo f = typeof(Program).GetField("dataDirCache", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) { L("!! 找不到 Program.dataDirCache，无法隔离，中止"); return 2; }
            f.SetValue(null, Data);
            Chk("DataDir 指向沙盒",
                string.Equals(Program.DataDir.TrimEnd('\\'), Data.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                Program.DataDir);
            Chk("三个扫描根全在沙盒里",
                Dlssg030.BackupRoot.StartsWith(Sand, StringComparison.OrdinalIgnoreCase)
                && XeMfg.BackupRoot.StartsWith(Sand, StringComparison.OrdinalIgnoreCase)
                && XeMfg.RemovedRoot.StartsWith(Sand, StringComparison.OrdinalIgnoreCase),
                Dlssg030.BackupRoot);
        }
        catch (Exception ex) { L("!! 隔离失败：" + ex.GetType().Name + " " + ex.Message); return 2; }

        // ---------- 1. 统计 ----------
        Head("1. 统计：归属解析、体积、文件数、时间来源");
        // dlssg030-pack\_backup —— _pack 三份（90/45/1 天前）+ cp2077 两份（60/5）+ zzz 一份（无时间戳）
        string A1 = Make("dlssg030-pack", "_backup", "_pack-" + Stamp(90), 6, 200);
        string A2 = Make("dlssg030-pack", "_backup", "_pack-" + Stamp(45), 6, 200);
        string A3 = Make("dlssg030-pack", "_backup", "_pack-" + Stamp(1), 6, 300);
        string B1 = Make("dlssg030-pack", "_backup", "cp2077-0.3.0-" + Stamp(60), 3, 100);
        string B2 = Make("dlssg030-pack", "_backup", "cp2077-0.3.0-" + Stamp(5), 3, 100);
        string C1 = Make("dlssg030-pack", "_backup", "zzz", 4, 100);
        // xess-pack\_removed —— wuwa 两份（120 与 residual-ini 20）+ zzz 三份（120/opti 10/signed030 3）+ 无时间戳一份
        string D1 = Make("xess-pack", "_removed", "wuwa-" + Stamp(120), 5, 200);
        string D2 = Make("xess-pack", "_removed", "wuwa-residual-ini-" + Stamp(20), 2, 1);
        string E1 = Make("xess-pack", "_removed", "zzz-" + Stamp(120), 4, 1);
        string E2 = Make("xess-pack", "_removed", "zzz-opti-" + Stamp(10), 2, 1);
        string E3 = Make("xess-pack", "_removed", "zzz-signed030-" + Stamp(3), 2, 1);
        string F1 = Make("xess-pack", "_removed", "handmade-dir", 1, 1);
        // xess-pack\_backup —— zzz 一份（与 dlssg030-pack 里那个同名、不同资源包）
        string G1 = Make("xess-pack", "_backup", "zzz", 3, 1);

        List<JunkItem> all = Junk.Scan();
        Chk("两个资源包的 _backup / _removed 全扫到（13 项）", all.Count == 13, all.Count + " 项");
        JunkItem a = At(all, A1);
        Chk("整包备份归属解析为 _pack", a != null && a.Game == "_pack", a == null ? "null" : a.Game);
        Chk("体积与文件数对得上（6 × 200 KB）",
            a != null && a.Files == 6 && a.Bytes == 6 * 200 * 1024,
            a == null ? "null" : a.Files + " 个 / " + a.Bytes + " B");
        Chk("时间取自目录名（90 天前）",
            a != null && a.TimeFromName && Math.Abs((a.When - DateTime.Now.AddDays(-90)).TotalDays) < 1,
            a == null ? "null" : a.When.ToString("yyyy-MM-dd"));
        Chk("归属者与桶分开记（owner / bucket）",
            a != null && a.Owner == "dlssg030-pack" && a.Bucket == "_backup",
            a == null ? "null" : a.Owner + "/" + a.Bucket);
        a = At(all, B1);
        Chk("cp2077-0.3.0-<ts> 归到 cp2077（不被 0.3.0 那段带跑）",
            a != null && a.Game == "cp2077", a == null ? "null" : a.Game);
        a = At(all, D2);
        Chk("wuwa-residual-ini-<ts> 归到 wuwa", a != null && a.Game == "wuwa", a == null ? "null" : a.Game);
        a = At(all, E2);
        Chk("zzz-opti-<ts> 与 zzz-<ts> 算同一个归属（同款游戏的不同来源）",
            a != null && a.Game == "zzz" && At(all, E1) != null && At(all, E1).Game == "zzz",
            a == null ? "null" : a.Game);
        a = At(all, F1);
        Chk("目录名里没时间戳 → TimeFromName=false，退到目录修改时间",
            a != null && !a.TimeFromName && a.When != DateTime.MinValue,
            a == null ? "null" : a.When.ToString("yyyy-MM-dd"));
        Chk("归属 = 目录名第一段这条规则对无时间戳的手工目录同样成立（handmade-dir → handmade）",
            a != null && a.Game == "handmade", a == null ? "null" : a.Game);
        string H1 = Make("xess-pack", "_backup", "wuwa", 0, 0);
        Chk("空目录不报错、按 0 计入", At(Junk.Scan(), H1).Bytes == 0 && At(Junk.Scan(), H1).Files == 0, "");
        all = Junk.Scan();
        Chk("重扫一次多了一项（13 → 14）", all.Count == 14, all.Count + " 项");

        // ---------- 2. 保留策略 ----------
        Head("2. 保留策略：每个归属最新的一份必须留着（回滚要读它）");
        Chk("同归属里最新的 _pack 被保留（1 天前那份）", At(all, A3).Keep, At(all, A3).KeepWhy);
        Chk("同归属里较旧的两份 _pack 可回收", !At(all, A1).Keep && !At(all, A2).Keep, "");
        Chk("KeepWhy 说清了「为什么留」",
            At(all, A3).KeepWhy.IndexOf("回滚", StringComparison.Ordinal) >= 0, At(all, A3).KeepWhy);
        Chk("cp2077 只留最新的一份", At(all, B2).Keep && !At(all, B1).Keep, "");
        Chk("同名归属跨资源包不互相顶掉（两个 zzz 都留）",
            At(all, C1).Keep && At(all, G1).Keep, "");
        Chk("某归属只有一份时照样保留（没有更新的来顶它）", At(all, F1).Keep, At(all, F1).KeepWhy);
        Chk("wuwa 归属里 120 天那份被 20 天那份顶掉了",
            !At(all, D1).Keep && At(all, D2).Keep, "");
        Chk("_removed 里 zzz 只留最新的一份（signed030 3 天前）",
            At(all, E3).Keep && !At(all, E1).Keep && !At(all, E2).Keep, "");
        Chk("回收候选共 6 项（A1 A2 B1 D1 E1 E2）", Junk.Reclaimable(all).Count == 6,
            Junk.Reclaimable(all).Count + " 项");
        Chk("受保留保护的共 8 项", all.FindAll(delegate(JunkItem j) { return j.Keep; }).Count == 8, "");

        // ---------- 3. 天数门槛 ----------
        Head("3. 「清理 N 天前的备份」的候选集");
        List<JunkItem> aged = Junk.Aged(all, Junk.DefaultAgeDays);
        Chk("满 30 天且不受保留的才进候选（5 项）", aged.Count == 5, aged.Count + " 项");
        Chk("90 天前的整包备份在候选里", Has(aged, A1), "");
        Chk("45 天前的整包备份也在候选里", Has(aged, A2), "");
        Chk("10 天前那份虽可回收但没到期 → 不在候选", !Has(aged, E2) && !At(all, E2).Keep, "");
        Chk("1 天前的最新份既保留又没到期 → 不在候选", !Has(aged, A3), "");
        Chk("门槛本身生效：满 100 天的只剩 2 项", Junk.Aged(all, 100).Count == 2,
            Junk.Aged(all, 100).Count + " 项");
        Chk("days=0 时与 Reclaimable 等价", Junk.Aged(all, 0).Count == Junk.Reclaimable(all).Count,
            Junk.Aged(all, 0).Count + " vs " + Junk.Reclaimable(all).Count);
        Chk("候选里绝不混进保留项",
            Junk.Aged(all, 3650).FindAll(delegate(JunkItem j) { return j.Keep; }).Count == 0, "");

        // ---------- 4. 汇总与确认文本 ----------
        Head("4. 一行汇总与删前确认");
        string sum = Junk.SummaryOf(all);
        L("  " + sum);
        long total = Junk.BytesOf(all), freeable = Junk.BytesOf(aged);
        long recAll = Junk.BytesOf(Junk.Reclaimable(all));
        Chk("汇总里有总占用", sum.Contains(Junk.Mb(total)), Junk.Mb(total));
        Chk("汇总分三个口径：保留外可回收 / 其中满 30 天 / 留着供回滚",
            sum.Contains(Junk.Mb(recAll)) && sum.Contains(Junk.Mb(freeable))
            && sum.Contains(Junk.Mb(total - recAll)), sum);
        Chk("汇总里的项数与实际一致", sum.Contains(all.Count + " 项"), all.Count + " 项");
        Chk("空备份区不谎报占用",
            Junk.SummaryOf(new List<JunkItem>()).IndexOf("空的", StringComparison.Ordinal) >= 0,
            Junk.SummaryOf(new List<JunkItem>()));
        string confirm = Junk.ConfirmText(aged);
        Chk("确认文本摊开了具体路径（不是「确定要删除吗」）", confirm.Contains(A1) && confirm.Contains(D1), "");
        Chk("确认文本里没混进保留项", !confirm.Contains(A3) && !confirm.Contains(C1), "");
        Chk("确认文本写明送回收站、可还原，并点出保留策略",
            confirm.IndexOf("回收站", StringComparison.Ordinal) >= 0
            && confirm.IndexOf("还原", StringComparison.Ordinal) >= 0
            && confirm.IndexOf("保留", StringComparison.Ordinal) >= 0, "");

        // ---------- 5. 回收 ----------
        Head("5. 回收：只动候选，保留项一条都不碰");
        string rc = Junk.Recycle(aged, IntPtr.Zero);
        L("  " + rc.Replace("\n", "\n  "));
        Chk("候选 5 项都从磁盘上消失了",
            !Directory.Exists(A1) && !Directory.Exists(A2) && !Directory.Exists(B1)
            && !Directory.Exists(D1) && !Directory.Exists(E1), "");
        Chk("未进候选的 9 项全在原地（含可回收但没到期那份）",
            Directory.Exists(A3) && Directory.Exists(B2) && Directory.Exists(C1)
            && Directory.Exists(D2) && Directory.Exists(E2) && Directory.Exists(E3)
            && Directory.Exists(F1) && Directory.Exists(G1) && Directory.Exists(H1), "");
        Chk("抬头说清理了几项", rc.IndexOf("清理 5 项", StringComparison.Ordinal) >= 0, rc.Split('\n')[0]);
        Chk("回执如实报了释放体积", rc.Contains(Junk.Mb(freeable)), Junk.Mb(freeable));
        Chk("回执逐条列了回收掉的路径", rc.Contains(A1) && rc.Contains(D1), "");
        Chk("回执写明在回收站、可随时还原",
            rc.IndexOf("回收站", StringComparison.Ordinal) >= 0
            && rc.IndexOf("可随时还原", StringComparison.Ordinal) >= 0, "");
        long after = Junk.BytesOf(Junk.Scan());
        Chk("重新统计后总占用正好少了那么多", total - after == freeable, Junk.Mb(total - after));
        Chk("回收站这条路走完之后没留下半截目录（不是硬删的残留）",
            !Directory.Exists(A1) && !File.Exists(Path.Combine(A1, "f0.dll")), A1);

        Head("6. 把保留项硬塞进回收接口 → 必须拒绝");
        string r2 = Junk.Recycle(new List<JunkItem> { At(Junk.Scan(), A3) }, IntPtr.Zero);
        L("  " + r2.Replace("\n", "\n  "));
        Chk("保留项没被删", Directory.Exists(A3), "");
        Chk("拒绝要如实写进回执，不能装作成功",
            r2.IndexOf("保留策略", StringComparison.Ordinal) >= 0 && r2.IndexOf("✘", StringComparison.Ordinal) >= 0
            && r2.IndexOf("未成功 1 项", StringComparison.Ordinal) >= 0, "");

        Head("7. 已经不存在的项 / 空清单");
        JunkItem gone = new JunkItem();
        gone.Dir = Path.Combine(Data, @"xess-pack\_removed\wuwa-" + Stamp(400));
        gone.Game = "wuwa";
        string r3 = Junk.Recycle(new List<JunkItem> { gone }, IntPtr.Zero);
        Chk("目录本来就不在 → 报「已经不在了」，既不计成功也不计失败",
            r3.IndexOf("已经不在了", StringComparison.Ordinal) >= 0
            && r3.IndexOf("未成功", StringComparison.Ordinal) < 0
            && r3.IndexOf("清理 0 项", StringComparison.Ordinal) >= 0, r3.Replace("\n", " / "));
        string r4 = Junk.Recycle(null, IntPtr.Zero);
        Chk("传 null 不抛异常", r4.Length > 0, r4);

        Head("8. 验收判据：清理之后回滚路径仍然完好");
        //  模拟一次卸载回滚：从**保留**的那份备份里把游戏自带件拷回游戏目录。
        //  清理只吃候选，所以这一步必须照常成立 —— 这正是 P0-1 的验收判据。
        string game = Path.Combine(Sand, "game");
        Directory.CreateDirectory(game);
        string target = Path.Combine(game, "nvngx_dlssg.dll");
        File.WriteAllBytes(target, new byte[1024]);
        File.Copy(target, Path.Combine(G1, "nvngx_dlssg.dll"), true);
        File.Delete(target);
        Chk("前置：游戏目录里那个文件已经不在了", !File.Exists(target), "");
        all = Junk.Scan();
        Chk("清理过的盘上，回滚要用的那份备份仍是保留项", At(all, G1) != null && At(all, G1).Keep, "");
        string src = Path.Combine(G1, "nvngx_dlssg.dll");
        Chk("保留的那份里还留着待还原的文件", File.Exists(src), src);
        File.Copy(src, target, true);
        Chk("回滚拷贝成功 —— 「清理 30 天前的备份」不会断掉回滚", File.Exists(target), target);
        Chk("Plan.SwitchTo 用的路径没被统计/回收改动过（仍在原位）",
            Directory.Exists(Dlssg030.BackupRoot) && Directory.Exists(XeMfg.BackupRoot), "");

        L("");
        L("========================================");
        L(Fail == 0 ? "JUNK PROBE: ALL PASS" : ("JUNK PROBE: " + Fail + " FAILED"));
        L("========================================");
        L("沙盒留在 " + Sand + "（探针不硬删任何东西，回收掉的项在回收站里）");
        return Fail == 0 ? 0 : 1;
    }
}
}
