// ==========================================================================
//  MfgBridge.cs —— 方案 B：nvidia_mfg_bridge
// ==========================================================================
//  这是什么：
//    另一个团队做的 20/30 系帧生成扩展 Mod，基于 Nukem9 的 dlssg-to-fsr3（GPLv3）
//    二次开发。发布形态是**一个 nvngx.dll**，自己改名成入口名即可，运行时按需把
//    provider 解包到 %TEMP%\nvidia_mfg_bridge\。
//    仓库 gt2333588/nvidia_mfg_bridge（最新 v0.507，2026-10-15）。
//
//  为什么值得单独做成一套方案（对照 dlssg_for_sm86 0.4.0）：
//    · 入口名里有 IPHLPAPI.dll、XINPUT9_1_0.dll、cryptsp.dll 这些**冷门名** ——
//      二游的启动器 / 反作弊常把 version / winmm / dxgi 这类常见名占住，冷门名冗余更大；
//    · Home 键（或 ForceFrameGenOverride）能**直接覆盖倍率**，不受游戏菜单里能选的档位限制
//      （0.4.0 那边只能改 ini 的 MaxGeneratedFrames，且上限仍由游戏侧插件决定）；
//    · 三套插帧实现可切：官方 DLSS-G（默认）/ FSR3.1 / XeSS；
//    · 一个文件，部署与卸载都干净。
//
//  ⚠ 必须说清的代价（这决定了它在工具里的定位是"可选"而不是"默认"）：
//    · 该仓库**只发二进制、不含源码**，信任只能靠签名核验与社区口碑；
//    · 作者自己 README 写明"联机游戏、竞技游戏、带反作弊的不建议使用" ——
//      所以它对"二游"的完善是**功能层面**的，不是**账号安全层面**的。
//      账号安全性上它与 0.4.0 是同一级别：都是往游戏进程里注入第三方 DLL。
//
//  与 0.3.x / 0.4.x / 方案 A 的关系：互斥。它们抢的是同一批入口名，
//   同一时刻只能有一套在生效（切换靠改名，见 Plan.SwitchTo）。
//
//  语法限制：csc.exe 编译，一律 C# 4 写法（无 $""、无 nameof、无 ?.）。
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Fluxion
{
    public static class MfgBridge
    {
        // 发布包里就这一个文件，改名后放到游戏目录
        public const string DllName = "nvngx.dll";
        public const string IniName = "dlssg_to_fsr3.ini";
        public const string Kind = "mfgbridge";
        public const string DefVer = "0.507";

        // 作者 README 给的六个可改名目标（顺序即推荐顺序）
        public static readonly string[] Entries = new string[] {
            "version.dll", "winhttp.dll", "dbghelp.dll", "cryptsp.dll", "xinput9_1_0.dll", "iphlpapi.dll" };

        public static string PackRoot { get { return Path.Combine(Program.DataDir, "mfgbridge-pack"); } }
        public static string DllPath { get { return Path.Combine(PackRoot, DllName); } }
        public static string IniPath { get { return Path.Combine(PackRoot, IniName); } }

        // 资源包在不在：只认 nvngx.dll；ini 是可选的（没有就按出厂默认跑）
        public static bool Ready(out string miss)
        {
            miss = "";
            if (!Directory.Exists(PackRoot)) { miss = "资源包目录不存在（" + PackRoot + "）"; return false; }
            if (!File.Exists(DllPath)) { miss = DllName; return false; }
            return true;
        }

        public static long PackBytes()
        {
            long n = 0;
            try
            {
                if (!Directory.Exists(PackRoot)) return 0;
                foreach (string f in Directory.GetFiles(PackRoot))
                    try { n += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return n;
        }

        public static string Mb(long n)
        {
            return Math.Round(n / 1048576.0, 1).ToString() + " MB";
        }

        // 界面用的一行说明
        public static string Caption()
        {
            string miss;
            if (!Ready(out miss))
                return "方案 B 资源包未就绪（缺 " + miss + "）—— 点「从 GitHub 取新版」自动下载，或把发布包拖进窗口";
            long n = 0;
            try { n = new FileInfo(DllPath).Length; } catch { }
            return "资源包 nvngx.dll（" + Mb(n) + "）· 部署时按选中的入口名改名放入游戏目录";
        }

        // ----------------------------------------------------------------------
        //  认：这个 DLL 是方案 B 的件吗
        // ----------------------------------------------------------------------
        //  不用体积窗口判 —— 0.4.0 已经证明"按体积认代"在新结构下不可靠。
        //  这里用**资源包账本里的 sha256** 比对：工具自己落过盘的件才知道，外来同名文件不认。
        public static bool IsMine(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                List<string> shas = KnownShas();
                if (shas.Count == 0) return false;
                string h = Sha16(path);
                foreach (string s in shas)
                    if (string.Equals(s, h, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }

        // 账本里记的这批件的 sha256（前 16 位）
        static List<string> KnownShas()
        {
            try
            {
                PackLedger L = Pack.ReadLedger(PackRoot);
                List<string> r = new List<string>();
                if (L == null) return r;
                foreach (var kv in L.Files)
                    if (kv.Value != null && kv.Value.Sha.Length > 0) r.Add(kv.Value.Sha);
                return r;
            }
            catch { return new List<string>(); }
        }

        // 这个目录里方案 B 有没有在位、占的是哪个入口
        public static string InstalledEntry(string gameDir)
        {
            if (gameDir == null) return "";
            foreach (string e in Entries)
            {
                string p = Path.Combine(gameDir, e);
                if (File.Exists(p) && IsMine(p)) return e;
            }
            return "";
        }

        public static bool Installed(string gameDir)
        {
            return InstalledEntry(gameDir).Length > 0;
        }

        // ----------------------------------------------------------------------
        //  部署
        // ----------------------------------------------------------------------

        // 与 Deploy 路径共用的一句"计划清单"（也给界面/日志用）
        public static List<string> PlannedSources()
        {
            List<string> r = new List<string>();
            if (File.Exists(DllPath)) r.Add(DllPath);
            if (File.Exists(IniPath)) r.Add(IniPath);
            return r;
        }

        public static string Install(string gameDir, string entry, bool force)
        {
            if (gameDir == null || !Directory.Exists(gameDir)) return "目录不存在：" + gameDir;
            if (entry == null || entry.Length == 0) entry = "version.dll";
            string miss;
            if (!Ready(out miss)) return "方案 B 资源包不完整（缺 " + miss + "）—— 先点「从 GitHub 取新版」";

            string src = DllPath;
            string dst = Path.Combine(gameDir, entry);
            int backed = 0;
            try
            {
                // 覆盖前备份：游戏自带的同名 DLL 也顶着这个名字，换入口名时最常见
                if (File.Exists(dst) && !IsMine(dst))
                {
                    File.Copy(dst, dst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    backed++;
                }
                File.Copy(src, dst, true);
            }
            catch (Exception ex)
            {
                return "部署失败：" + ex.GetType().Name + "：" + ex.Message;
            }

            // 出厂 ini：没有就补一份，有了不动（用户调过参数不该被冲掉）
            string iniNote = "";
            try
            {
                if (File.Exists(IniPath))
                {
                    string idst = Path.Combine(gameDir, IniName);
                    if (!File.Exists(idst) || force)
                    {
                        if (File.Exists(idst))
                            File.Copy(idst, idst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                        File.Copy(IniPath, idst, true);
                        iniNote = "已放入出厂 " + IniName;
                    }
                }
            }
            catch { }

            return "已部署方案 B：nvngx.dll → 入口 " + entry
                 + (backed > 0 ? "（原同名文件已备份成 .bak-时间戳）" : "")
                 + (iniNote.Length > 0 ? "；" + iniNote : "")
                 + "。运行时会把 provider 解包到 %TEMP%\\nvidia_mfg_bridge\\；游戏内按 Home 键调倍率，Insert 键切 NR。"
                 + "\n  卸载：删掉游戏目录里那个改名后的文件即可。";
        }

        // 停放（改名，不删）。返回停放了几项 —— 由 Plan.ParkOthers 统一调用。
        public static int Park(string gameDir, List<string> parked, List<string> failed)
        {
            int n = 0;
            foreach (string e in Entries)
            {
                string p = Path.Combine(gameDir, e);
                if (!File.Exists(p) || !IsMine(p)) continue;
                string dst = p + ".parked." + Kind;
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(p, dst);
                    if (parked != null) parked.Add(e + " → " + Path.GetFileName(dst));
                    n++;
                }
                catch (Exception ex)
                {
                    if (failed != null) failed.Add(e + "：" + ex.Message);
                }
            }
            return n;
        }

        // 彻底卸载（真删）
        public static string Uninstall(string gameDir)
        {
            int n = 0;
            List<string> failed = new List<string>();
            foreach (string e in Entries)
            {
                string p = Path.Combine(gameDir, e);
                try { if (File.Exists(p) && IsMine(p)) { File.Delete(p); n++; } }
                catch (Exception ex) { failed.Add(e + "：" + ex.Message); }
            }
            string ini = Path.Combine(gameDir, IniName);
            try { if (File.Exists(ini)) { File.Delete(ini); n++; } }
            catch (Exception ex) { failed.Add(IniName + "：" + ex.Message); }

            string r = "已删除方案 B 的 " + n + " 个文件（含 " + IniName + "）";
            if (failed.Count > 0)
                r += "\n  ⚠ 有 " + failed.Count + " 项没删掉：" + string.Join("；", failed.ToArray())
                   + " —— 多半是游戏还在运行，完全退出后重试";
            return r;
        }

        // ----------------------------------------------------------------------
        //  识别 + 归位（拖入 / 下载命中这种包时）
        // ----------------------------------------------------------------------

        // 判据：包里有 nvngx.dll，且体积**明显大于**一个普通入口 stub。
        //  名字本身不能当判据（OptiScaler 生态里也有 nvngx.dll），所以两条都要。
        //  体积门槛取 20 MB：199 MB 的实测值离它很远，不会误判；
        //  而普通 stub 只有几百 KB，也进不来。
        public static bool ClassifyInto(PackProbe p)
        {
            if (p == null || p.Items == null) return false;
            PackItem dll = null;
            foreach (PackItem it in p.Items)
                if (it.Name == DllName) { dll = it; break; }
            if (dll == null || dll.Size < 20L * 1024 * 1024) return false;

            p.Kind = Kind;
            p.Entry = DllName;
            p.Ver = DefVer;
            p.VerBasis = DllName + " = " + dll.Size.ToString("N0") + " B（"
                       + Mb(dll.Size) + "）→ 单文件自带 provider 的 MFG Bridge 包";
            foreach (PackItem it in p.Items)
            {
                if (it.Name == DllName) { it.Role = "entry"; it.Cert = ProxySign.Of(it.Src); }
                else if (it.Name == IniName) { it.Role = "ini"; }
                else it.Role = "other";
            }
            p.Notes.Add("部署时这个文件会被**改名**成你选的入口名（默认 version.dll）再放进游戏目录");
            return true;
        }

        public static string Import(PackProbe p, Action<string> log)
        {
            if (p == null || p.Error.Length > 0) return "识别失败：" + (p == null ? "空" : p.Error);
            try
            {
                Directory.CreateDirectory(PackRoot);
                List<PackItem> done = new List<PackItem>();
                int n = 0;
                long bytes = 0;
                foreach (PackItem it in p.Items)
                {
                    if (it.Role != "entry" && it.Role != "ini") continue;
                    string dst = Path.Combine(PackRoot, it.Name);
                    File.Copy(it.Src, dst, true);
                    it.Sha = Sha16(dst);
                    bytes += it.Size;
                    done.Add(it);
                    n++;
                }
                if (done.Count == 0) return "包里没有可归位的文件（需要 " + DllName + "）";
                Pack.WriteLedger(p, done, PackRoot);
                return "已更新方案 B 资源 → " + PackRoot + "\n  落盘 " + n + " 个文件（" + Mb(bytes) + "）"
                     + "\n  部署方式：把 " + DllName + " 改名成选中的入口名后放进游戏目录";
            }
            catch (Exception ex)
            {
                return "归位失败：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        // ----------------------------------------------------------------------
        //  配置改写
        // ----------------------------------------------------------------------

        // 只改一个键，其余原样 —— 出厂 ini 是完整发布配置（7 KB），整份重写会丢一堆键
        public static string SetIni(string gameDir, string section, string key, string val)
        {
            string p = Path.Combine(gameDir, IniName);
            if (!File.Exists(p)) return "找不到 " + IniName + "（方案 B 未部署？）";
            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(p, Encoding.UTF8));
                int secAt = -1, secEnd = lines.Count;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (!t.StartsWith("[", StringComparison.Ordinal)) continue;
                    if (secAt >= 0) { secEnd = i; break; }
                    int e = t.IndexOf(']');
                    if (e > 1 && t.Substring(1, e - 1).Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                        secAt = i;
                }
                if (secAt < 0)
                {
                    lines.Add("[" + section + "]");
                    lines.Add(key + "=" + val);
                }
                else
                {
                    bool hit = false;
                    for (int i = secAt + 1; i < secEnd; i++)
                    {
                        if (lines[i].TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = key + "=" + val;
                            hit = true;
                            break;
                        }
                    }
                    if (!hit) lines.Insert(secAt + 1, key + "=" + val);
                }
                File.WriteAllLines(p, lines.ToArray(), new UTF8Encoding(false));
                return "已写入 [" + section + "] " + key + "=" + val;
            }
            catch (Exception ex) { return "改写失败：" + ex.Message; }
        }

        static string Sha16(string path)
        {
            try
            {
                string h = Dlssg.Sha256(path);
                return h.Length > 16 ? h.Substring(0, 16) : h;
            }
            catch { return ""; }
        }
    }
}
