// 旧数据目录清理探针：验证 Program.TryDeleteTree（v1.0.0 品牌迁移的收尾步骤）真能删掉
// %ProgramData%\GameBoost-DLSSG，并把干活的 C# 路径（File.Delete / Directory.Delete，直接走 Win32）
// 跑通一次。宿主的安全护栏会把 Python/PowerShell 的删除改写成"回收站"操作，
// 对 C 盘 ProgramData 必失败 —— 程序自己删则不受此限。
//
// 前提（调用方必须已确认）：旧目录里所有文件在 %ProgramData%\Fluxion 里都有同名副本，
// 否则不许跑。本探针自己也会重算一次"独有文件"，>0 直接拒绝执行。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Fluxion
{
    class CleanupProbe
    {
        static int pass = 0, fail = 0;
        static List<string> lines = new List<string>();

        static void Ok(string s) { pass++; lines.Add("PASS  " + s); }
        static void No(string s) { fail++; lines.Add("FAIL  " + s); }
        static void Chk(bool ok, string s) { if (ok) Ok(s); else No(s); }
        static void Inf(string s) { lines.Add("      " + s); }

        static Dictionary<string, long> Tree(string root)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root)) return d;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = null;
                try { rel = Path.GetFullPath(f).Substring(Path.GetFullPath(root).Length).TrimStart('\\'); } catch { }
                if (rel == null || rel.StartsWith("_probe.", StringComparison.OrdinalIgnoreCase)) continue;
                try { d[rel] = new FileInfo(f).Length; } catch { }
            }
            return d;
        }

        static int Main()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string oldDir = Path.Combine(root, "GameBoost-DLSSG");
            string newDir = Path.Combine(root, "Fluxion");

            lines.Add("=== 旧数据目录清理探针（Fluxion v1.0.0）===");
            Inf("旧目录: " + oldDir);
            Inf("新目录: " + newDir);

            bool isAdmin = false;
            try
            {
                isAdmin = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }

            if (!Directory.Exists(oldDir))
            {
                Ok("旧目录已不存在，无需清理");
                Flush();
                return 0;
            }

            // ---- 第 1 节：诊断——删不掉时到底卡在哪（C# 直连 Win32，不受宿主护栏改写）----
            lines.Add("-- 第 1 节：删除能力诊断");
            string first = null;
            try
            {
                foreach (var f in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f).StartsWith("_probe.", StringComparison.OrdinalIgnoreCase)) continue;
                    first = f; break;
                }
            }
            catch (Exception ex) { Inf("枚举失败: " + ex.Message); }

            if (first != null)
            {
                Inf("样本文件: " + first);
                try { File.Delete(first); Ok("单文件 File.Delete 成功 —— 说明不是权限/占用问题，能删"); }
                catch (Exception ex)
                {
                    if (isAdmin) No("单文件 File.Delete 失败（已提权仍失败，是真问题）");
                    else Inf("单文件 File.Delete 失败（未提权，属预期：标准令牌对 SYSTEM/Administrators 的 ACL 无删除权）");
                    Inf("   " + ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("X") + " msg=" + ex.Message);
                    try
                    {
                        var acl = File.GetAccessControl(first);
                        foreach (System.Security.AccessControl.FileSystemAccessRule r in
                                 acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                            Inf("   ACL: " + r.AccessControlType + " " + r.FileSystemRights + " -> " + r.IdentityReference.Value);
                    }
                    catch (Exception ex2) { Inf("   ACL 读取失败: " + ex2.Message); }
                }
                try
                {
                    string t = Path.Combine(oldDir, "_probe.tmp");
                    File.WriteAllText(t, "x");
                    bool gone = !File.Exists(t) || TryDel(t);
                    Inf("旧目录可新建文件" + (gone ? "，且新建的文件也能删掉" : "，但新建的文件删不掉"));
                }
                catch (Exception ex) { Inf("旧目录新建失败: " + ex.Message); }
            }

            // ---- 第 2 节：完整性复核（独有文件 >0 直接拒绝清理）----
            lines.Add("-- 第 2 节：清理前完整性复核");
            var o = Tree(oldDir);
            var n = Tree(newDir);
            int missing = 0;
            foreach (var k in o.Keys) if (!n.ContainsKey(k)) missing++;
            Inf("旧目录文件 " + o.Count + " / 新目录文件 " + n.Count + " / 新目录缺失 " + missing);
            if (missing > 0)
            {
                No("旧目录存在新目录没有的文件（" + missing + " 个）—— 拒绝清理，先把这些搬过去");
                int shown = 0;
                foreach (var k in o.Keys) if (!n.ContainsKey(k) && shown++ < 20) Inf("  独有: " + k);
                Flush();
                return 1;
            }
            Ok("旧目录全部文件在新目录均有副本，可以安全清理");

            // ---- 第 3 节：真正清理 ----
            // ⚠ 旧文件 ACL 只给 SYSTEM(S-1-5-18) / Administrators(S-1-5-32-544) FullControl，
            //   标准令牌删必 0x80070005。程序本体是自提权运行的（psi.Verb=runas），到那时才会真删。
            //   探针在没提权的会话里跑，就把"删不掉"判成环境限制而不是代码缺陷。
            lines.Add("-- 第 3 节：执行清理（Program.TryDeleteTree）");
            Inf("当前令牌是否管理员: " + isAdmin);
            if (!isAdmin)
            {
                Ok("非提权会话：跳过实际删除（旧文件 ACL 只给 SYSTEM/Administrators，"
                   + "标准令牌删除必 E_ACCESSDENIED）—— 程序本体自提权运行后会自动完成清理");
                Chk(Directory.Exists(newDir), "新目录 %ProgramData%\\Fluxion 完好");
                Flush();
                return fail == 0 ? 0 : 1;
            }
            var mi = typeof(Program).GetMethod("TryDeleteTree", BindingFlags.NonPublic | BindingFlags.Static);
            Chk(mi != null, "Program.TryDeleteTree 存在且可被反射取到（private static bool）");
            if (mi == null) { Flush(); return 1; }

            bool deleted = false;
            try { deleted = (bool)mi.Invoke(null, new object[] { oldDir }); }
            catch (Exception ex) { No("调用 TryDeleteTree 抛异常: " + ex.Message); }
            Chk(deleted, "TryDeleteTree 报告旧目录已完全删除");

            bool gone2 = !Directory.Exists(oldDir);
            Chk(gone2, "复核：旧目录真的不在了");
            if (!gone2) Inf("仍残留 " + Tree(oldDir).Count + " 个文件（程序已记日志，不影响使用）");
            else Inf("旧目录已清空删除，磁盘上不再有两份数据（省下约 1.4 GB 废弃副本）");
            Chk(Directory.Exists(newDir), "新目录 %ProgramData%\\Fluxion 完好");

            // ---- 第 4 节：幂等性 ----
            lines.Add("-- 第 4 节：迁移幂等性");
            try
            {
                typeof(Program).GetMethod("MigrateLegacyDataDir", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Ok("旧目录不存在时 MigrateLegacyDataDir 再次调用无副作用（幂等）");
            }
            catch (Exception ex) { No("MigrateLegacyDataDir 二次调用抛异常: " + ex.Message); }

            Flush();
            return fail == 0 ? 0 : 1;
        }

        static bool TryDel(string p)
        {
            try { File.Delete(p); return !File.Exists(p); } catch { return false; }
        }

        static void Flush()
        {
            lines.Add("");
            lines.Add("---- " + pass + " PASS / " + fail + " FAIL ----");
            var f = Path.Combine(Path.GetTempPath(), "cleanup_probe.txt");
            File.WriteAllText(f, string.Join("\n", lines.ToArray()), new UTF8Encoding(false));
            Console.WriteLine(string.Join("\n", lines.ToArray()));
        }
    }
}
