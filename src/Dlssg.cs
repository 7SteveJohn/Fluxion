// ============================================================================
//  Fluxion · DLSSG 帧生成子系统
//  ---------------------------------------------------------------------------
//  作用：为 RTX 20/30 系（SM86/SM75）游戏一键接入 dlssg_for_sm86 帧生成运行时。
//  原理：该 Mod 以 DLL 代理方式注入（游戏 EXE 同目录放代理 DLL + dlssg_sm86.ini），
//        接管游戏对 nvngx_dlssg.dll 的加载请求，换成补齐 SM86/SM75 内核的运行时。
//
//  关键工程结论（实测，2026-09-11）：
//    · Cyberpunk 2077 (2.31) 导入 version.dll 但运行中从不调用其函数，
//      代理的延迟初始化永不触发（现象：DLL 在进程里、零日志、FG 无效且掉帧）。
//      换 winmm.dll 入口后立即激活，实测基准 135 → 190 帧。
//      → 因此本模块内置「已知游戏专用入口」表。
//    · 判定代理是否真正工作的唯一可靠依据是日志目录 dlssg_sm86\logs\*.jsonl，
//      日志出现 = 代理已激活（含 runtime_redirect / feature_created / evaluate 事件）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Fluxion
{
    // ---- 数据模型 ----
    public class DlssgGame
    {
        public string Title = "";           // 显示名
        public string Dir = "";             // 游戏 EXE 所在目录
        public string Exe = "";             // 主 EXE 名
        public string Category = "";        // aaa / gacha / fps / mmo / ""
        public bool HasFrameGen;            // 集成 DLSS 帧生成（nvngx_dlssg.dll / sl.dlss_g.dll）
        public bool HasUpscaler;            // 集成 DLSS 超分（nvngx_dlss.dll）
        public bool HasFsr3;                // 集成 FSR3 帧生成（免 Mod 备选路线）
        public bool Installed;              // 当前已装本工具代理
        public string Entry = "";           // 已装/将用入口 DLL 名
        public string Note = "";            // 备注（已知问题等）
        public bool Preset;                 // 未安装的预置条目
        public bool Custom;                 // 用户手动添加（不走游戏性判定，列表里标注）
        public bool Ignored;                // 在忽略清单里（仍会显示在列表中，可一键恢复收录）
        public int ScanDepth;               // 扫描候选深度（启动器根=0）：同分时更深者优先
        // ---- 游戏库（v2.5.0，见 Lib.cs）----
        public string Platform = "local";   // steam / epic / wegame / battle / ubisoft / ea / gog / local
        public string AppId = "";           // Steam appid（用于 steam:// 启动与封面下载）
        public string Genre = "";           // 题材标签（"二次元"）
        public string Id = "";              // 稳定 ID（平台_哈希）：封面文件名与库标记用
        public bool Favorite;               // 收藏
        public bool Private;                // 私密（不在"全部"里出现）
        public bool Hidden;                 // 隐藏（除非开"显示已隐藏"）
        public long LastPlayed;             // 最近游玩（unix 秒）
        public string LaunchArgs = "";      // 自定义启动参数（持久化在 library.json，见 GameMeta/Lib.SetDx12）
        // ---- 判定缓存（每次 MergeScan 重算，不落盘）----
        //   RecCat = RecommendCategory 的原始分类（platform / gacha / aaa / fps / other），
        //   null 表示"还没算过"。缓存它的两个理由：
        //     ① AllGameProcesses() 在每次进程快照里会被调用十几次，绝不能在它里面做文件系统探测
        //        （RecommendCategory 会调 XeMfg.Detect 去列目录）；
        //     ② 判定必须**每次扫描都重算**，否则后续新加的游戏会一直沿用旧结论。
        public string RecCat;
    }

    public class DlssgRec
    {
        public string Dir = "";
        public string Proxy = "";
        public string Sha256 = "";
        public string Backup = "";
        public string InstalledAt = "";
    }

    public static class Dlssg
    {
        // 入口优先级：version 最通用，winmm 激活率最高（音频库必被调用）
        public static readonly string[] EntryNames = new string[] { "version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll" };

        // ---- d3d12 复合入口（v2.1.0 新增）----
        // 为什么必须另开一条路：带内核反作弊的游戏（绝区零 HoYoKProtect / 鸣潮腾讯 ACE）会按
        // 【文件名】识别并拦截上面那 5 个入口。上游 issue #111 实测原话：「绝区零的反作弊会识别到
        // version.dll 等的名词，从而使其失效」；#387（明日方舟：终末地）五个入口全军覆没，症状逐条
        // 对应。而 d3d12.dll 是 DX12 游戏【必须加载】的系统库，反作弊无法按名拉黑。
        // 但上游包（dlssg_for_sm86）只发 5 个入口，没有 d3d12.dll（README：「五种已签名代理入口
        // 保持不变」）—— 所以本工具自己造一个纯 PE 转发代理：
        //   · 导出系统 d3d12.dll 的全部 18 个函数 → 转发给同目录 d3d12_orig.dll（系统原件副本）
        //   · 导入表只依赖 dinput8.dll!DirectInput8Create → 逼 Windows 在加载代理时把上游的
        //     dinput8.dll（真正的 dlssg 运行时，15.6 MB）拉进进程
        // 即：反作弊拦不住 d3d12.dll，而 d3d12.dll 自己会把被拦的那份运行时拽进来。
        // 详见 docs\dlssg_for_sm86_研究笔记.md 第 12 节 + tools\make_d3d12_proxy.py。
        public const string D3d12Entry = "d3d12.dll";        // 代理（本工具内嵌字节，3,584 B）
        public const string D3d12Orig = "d3d12_orig.dll";    // 转发目标（系统 System32\d3d12.dll 的副本）
        public const string D3d12Carrier = "dinput8.dll";    // 载体（上游 altnative\dinput8.dll）

        // 反作弊是否自动改走 d3d12 入口（默认开；关掉后本工具对反作弊游戏退回常规入口）
        static bool PreferD3d12 { get { return Program.Cfg.DlssgAntiCheatD3d12; } }

        // 已知游戏专用入口（游戏标题关键字小写 → 入口名）
        // Cyberpunk 2077：对 version.dll 只加载不调用 → 必须 winmm（实测结论）
        static readonly string[][] EntryOverrides = new string[][]
        {
            new string[] { "cyberpunk 2077", "winmm.dll" }
        };

        // 本 Mod 各入口 DLL 的体积区间（0.2.3/0.2.4 实测）：用于"这个文件是不是本 Mod 的"粗判据
        //  旧版（0.2.x）源件的体积窗口 —— 判据在 Catalog（内置默认 15,500,000~15,800,000）
        static long DllSizeMin { get { return Catalog.SourceWin.Min; } }
        static long DllSizeMax { get { return Catalog.SourceWin.Max; } }

        public static string BaseDir { get { return Program.DataDir; } }
        public static string SourceDir { get { return Path.Combine(BaseDir, "dlssg", "source"); } }
        public static string AltDir { get { return Path.Combine(SourceDir, "altnative"); } }
        public static string BackupDir { get { return Path.Combine(BaseDir, "dlssg", "backup"); } }
        public static string StatePath { get { return Path.Combine(BaseDir, "dlssg", "state.json"); } }
        public static string IniName { get { return "dlssg_sm86.ini"; } }
        public static string LogFolderName { get { return "dlssg_sm86"; } }

        // ==================== 状态记录 ====================

        public static List<DlssgRec> LoadState()
        {
            var list = new List<DlssgRec>();
            try
            {
                if (!File.Exists(StatePath)) return list;
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatePath, Encoding.UTF8));
                if (root == null || !root.ContainsKey("installs")) return list;
                var arr = root["installs"] as System.Collections.IEnumerable;
                if (arr == null) return list;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var r = new DlssgRec();
                    r.Dir = GetS(d, "dir");
                    r.Proxy = GetS(d, "proxy");
                    r.Sha256 = GetS(d, "sha256");
                    r.Backup = GetS(d, "backup");
                    r.InstalledAt = GetS(d, "installedAt");
                    list.Add(r);
                }
            }
            catch { }
            return list;
        }

        static void SaveState(List<DlssgRec> recs)
        {
            try
            {
                var arr = new List<Dictionary<string, object>>();
                foreach (var r in recs)
                {
                    var d = new Dictionary<string, object>();
                    d["dir"] = r.Dir; d["proxy"] = r.Proxy; d["sha256"] = r.Sha256;
                    d["backup"] = r.Backup; d["installedAt"] = r.InstalledAt;
                    arr.Add(d);
                }
                var root = new Dictionary<string, object>();
                root["installs"] = arr;
                var ser = new JavaScriptSerializer();
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, ser.Serialize(root), new UTF8Encoding(false));
            }
            catch { }
        }

        static void SetRec(string dir, string proxy, string sha, string backup)
        {
            var recs = LoadState();
            var keep = new List<DlssgRec>();
            foreach (var r in recs) if (!Eq(r.Dir, dir)) keep.Add(r);
            var n = new DlssgRec();
            n.Dir = dir; n.Proxy = proxy; n.Sha256 = sha; n.Backup = backup;
            n.InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            keep.Add(n);
            SaveState(keep);
        }

        static void DelRec(string dir)
        {
            var recs = LoadState();
            var keep = new List<DlssgRec>();
            foreach (var r in recs) if (!Eq(r.Dir, dir)) keep.Add(r);
            SaveState(keep);
        }

        // 清理失效记录：记录指向的代理文件已不存在，或体积已不是本工具那份。
        // 实测事故（2026-09-13）：绝区零换装 XeSS 套件后目录里的 d3d12.dll 变成 25.6 MB 的
        // OptiScaler 本体，而 state.json 里那条 15:34 的旧记录还在 —— 于是卡片误报
        // 「已开启（d3d12.dll）」，而且「关闭选中」会顺着这条记录去删别人的文件。
        // 判据用体积而不是"文件是否存在"：文件在、但不是我们的，同样要清掉记录。
        public static int PruneStale()
        {
            int dropped = 0;
            try
            {
                var recs = LoadState();
                var keep = new List<DlssgRec>();
                foreach (var r in recs)
                {
                    bool ok = false;
                    try
                    {
                        string p = Path.Combine(r.Dir == null ? "" : r.Dir, r.Proxy == null ? "" : r.Proxy);
                        if (File.Exists(p))
                        {
                            long len = new FileInfo(p).Length;
                            ok = Eq(r.Proxy, D3d12Entry)
                                 ? (len == D3d12ProxyBlob.Size)                 // d3d12 代理：3,584 B
                                 : (len >= DllSizeMin && len <= DllSizeMax);    // 其余入口：dlssg 运行时
                        }
                    }
                    catch { }
                    if (ok) keep.Add(r); else dropped++;
                }
                if (dropped > 0) SaveState(keep);
            }
            catch { }
            return dropped;
        }

        static string GetS(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null) return d[k].ToString();
            return "";
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ==================== 扫描 ====================

        // 扫描已安装游戏：Steam 库清单 + 常见目录 + 注册表卸载项
        public static List<DlssgGame> Scan(Action<string> progress)
        {
            var found = new List<DlssgGame>();
            var seen = new List<string>();
            var roots = new List<string>();

            // 1) Steam 库（libraryfolders.vdf 里所有 path）
            foreach (var steam in new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                @"C:\Program Files\Steam", @"D:\Steam", @"E:\Steam" })
            {
                try
                {
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                        {
                            string p = m.Groups[1].Value.Replace(@"\\", @"\");
                            string common = Path.Combine(p, "steamapps", "common");
                            if (Directory.Exists(common) && !Contain(roots, common)) roots.Add(common);
                        }
                    }
                    string c = Path.Combine(steam, "steamapps", "common");
                    if (Directory.Exists(c) && !Contain(roots, c)) roots.Add(c);
                }
                catch { }
            }

            // 2) 常见游戏根目录
            foreach (var d in new string[] {
                @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Games", @"D:\Games",
                @"D:\SteamLibrary\steamapps\common", @"E:\SteamLibrary\steamapps\common",
                @"D:\Epic Games", @"E:\Epic Games", @"C:\Program Files\Epic Games",
                @"D:\GOG Games", @"E:\GOG Games", @"F:\SteamLibrary\steamapps\common",
                @"G:\SteamLibrary\steamapps\common", @"C:\XboxGames", @"D:\XboxGames" })
            {
                if (Directory.Exists(d) && !Contain(roots, d)) roots.Add(d);
            }

            // 3) 用户配置的额外根目录 + 已知中文游戏目录关键字
            var customRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Program.Cfg.DlssgExtraRoots)
            {
                if (Directory.Exists(d) && !Contain(roots, d)) { roots.Add(d); customRoots.Add(d); }
            }

            int n = 0;
            foreach (var root in roots)
            {
                n++;
                if (progress != null) progress("扫描 (" + n + "/" + roots.Count + "): " + root);
                List<string> dirs;
                try { dirs = new List<string>(Directory.GetDirectories(root)); }
                catch { continue; }
                foreach (var dir in dirs)
                {
                    if (Contain(seen, dir)) continue;
                    var g = Inspect(dir, true, customRoots.Contains(root));   // 自定义目录放宽游戏性判定
                    if (g != null) { found.Add(g); seen.Add(dir); }
                }
            }

            // 4) 注册表卸载项：启动器类游戏（米哈游 HoYoPlay / 库洛等）装在自定义路径，
            //    前三类根目录覆盖不到（实测：绝区零在 G:\miHoYo Launcher、鸣潮在 G:\Wuthering Waves，
            //    2026-09-12 用户反馈"扫描读不出二游"即此根因）。只对命中启动器关键词的条目做探测。
            ScanRegistryUninstalls(progress, found, seen);

            if (progress != null) progress("扫描完成：识别 " + found.Count + " 个游戏目录");
            return found;
        }

        // 启动器/二游关键词：注册表 DisplayName / 安装路径命中才值得深挖（避免把每个已安装程序都翻一遍）
        static readonly string[] LauncherWords = new string[] {
            "mihoyo", "hoyo", "cognosphere", "kuro", "wuthering", "yuanshen", "genshin",
            "starrail", "zenless", "honkai", "endfield", "snowbreak", "tower of fantasy",
            "崩坏", "原神", "鸣潮", "绝区零", "米哈游", "库洛", "幻塔", "尘白"
        };

        // 枚举注册表卸载项（HKLM 64/32 位视图 + HKCU），命中启动器关键词的条目取安装根目录，
        // 向下探测（深度 ≤4）找真正通过游戏性判定的目录。鸣潮主 exe 在根下 5 层：
        // Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe
        static void ScanRegistryUninstalls(Action<string> progress, List<DlssgGame> found, List<string> seen)
        {
            string[] uninstPaths = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            var hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            var probeRoots = new List<string>();
            foreach (var hive in hives)
            {
                foreach (var up in uninstPaths)
                {
                    RegistryKey k = null;
                    try { k = hive.OpenSubKey(up); } catch { }
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        RegistryKey sk = null;
                        try { sk = k.OpenSubKey(sub); } catch { }
                        if (sk == null) continue;
                        try
                        {
                            string name = Conv(sk.GetValue("DisplayName"));
                            string loc = Conv(sk.GetValue("InstallLocation"));
                            string icon = Conv(sk.GetValue("DisplayIcon"));
                            string uninst = Conv(sk.GetValue("UninstallString"));
                            string hay = (name + " " + loc + " " + icon + " " + uninst).ToLowerInvariant();
                            bool hit = false;
                            foreach (var w in LauncherWords) if (hay.Contains(w)) { hit = true; break; }
                            if (!hit) continue;
                            string baseDir = loc.Trim().Length > 0 ? loc.Trim() : DirOfCmd(uninst);
                            if (baseDir == null || baseDir.Trim().Length == 0) baseDir = DirOfCmd(icon);
                            if (baseDir == null || !Directory.Exists(baseDir)) continue;
                            if (Contain(probeRoots, baseDir)) continue;
                            probeRoots.Add(baseDir);
                        }
                        catch { }
                        finally { try { sk.Close(); } catch { } }
                    }
                    try { k.Close(); } catch { }
                }
            }

            int rn = 0;
            foreach (var root in probeRoots)
            {
                rn++;
                if (progress != null) progress("注册表扫描 (" + rn + "/" + probeRoots.Count + "): " + root);
                ScanLauncherRoot(root, found, seen);
            }
        }

        // 启动器根 → 真正的游戏目录可能藏在多层下（鸣潮：根\Game\Client\Binaries\Win64，
        // 且中途的 "Wuthering Waves Game" 存根目录会靠 Engine 目录误判先过门）。
        // 策略：整棵子树（深度 ≤4）收集全部过门候选 → 打分（帧生成组件/FSR3/shipping exe）→
        // 按分贪心收录，祖先/后代目录互相排斥（存根目录让位给深层真身）。
        static void ScanLauncherRoot(string root, List<DlssgGame> found, List<string> seen)
        {
            var cands = new List<DlssgGame>();
            CollectCandidates(root, 0, cands, seen);
            // 排序：分数降序；同分则目录更深者优先（启动器根 vs 真游戏目录同分时，更深的那个才是游戏）
            cands.Sort(delegate(DlssgGame a, DlssgGame b)
            {
                int sa = ScoreGame(a), sb = ScoreGame(b);
                if (sa != sb) return sb - sa;
                return b.ScanDepth - a.ScanDepth;
            });
            foreach (var g in cands)
            {
                if (Contain(seen, g.Dir)) continue;
                bool clash = false;
                foreach (var acc in found)
                    if (PathUnder(g.Dir, acc.Dir) || PathUnder(acc.Dir, g.Dir)) { clash = true; break; }
                if (clash) continue;
                // Binaries\Win64 这类结构目录名没有可读性 → 沿父链向上找第一个非结构名
                if (IsStructuralName(Path.GetFileName(g.Dir))) g.Title = NiceTitle(g.Dir);
                found.Add(g); seen.Add(g.Dir);
            }
        }

        static void CollectCandidates(string dir, int depth, List<DlssgGame> cands, List<string> seen)
        {
            if (!Contain(seen, dir))
            {
                var g = Inspect(dir, true);
                if (g != null) { g.ScanDepth = depth; cands.Add(g); }
            }
            if (depth >= 4) return;
            foreach (var sub in SafeDirs(dir))
            {
                string bn = Path.GetFileName(sub).ToLowerInvariant();
                if (bn == "redist" || bn == "redistributable" || bn == "directx" || bn == "engine") continue;
                CollectCandidates(sub, depth + 1, cands, seen);
            }
        }

        // 候选打分：组件越真分越高——原生帧生成/FSR3 是最强信号，UE shipping 命名次之
        static int ScoreGame(DlssgGame g)
        {
            int s = 0;
            if (g.HasFrameGen) s += 4;
            if (g.HasFsr3) s += 4;
            if (g.HasUpscaler) s += 2;
            string e = (g.Exe ?? "").ToLowerInvariant();
            if (e.EndsWith("shipping.exe")) s += 3;
            return s;
        }

        static bool PathUnder(string dir, string ancestor)
        {
            string a = dir.TrimEnd('\\');
            string b = ancestor.TrimEnd('\\');
            return a.Length > b.Length
                && a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                && a[b.Length] == '\\';
        }

        static bool IsStructuralName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n == "win64" || n == "win32" || n == "binaries" || n == "client"
                || n == "x64" || n == "x86" || n == "bin" || n == "game";
        }

        // Binaries\Win64\... 这类结构目录名不能当标题 → 沿父链向上找第一个非结构名。
        // ⚠️ 旧实现循环只走 3 跳（Win64→Binaries→Client 就耗尽了），且跳出后把原名又返回，
        //    结果鸣潮在列表里一直显示成 "Win64"（2026-09-12 用户反馈）。
        static string NiceTitle(string dir)
        {
            try
            {
                var d = new DirectoryInfo(dir);
                for (int i = 0; i < 8 && d != null; i++)
                {
                    if (!IsStructuralName(d.Name)) return d.Name;
                    d = d.Parent;
                }
                return new DirectoryInfo(dir).Name;
            }
            catch { return Path.GetFileName(dir); }
        }

        // UE 打包游戏的 NVIDIA 插件位于 <游戏根>\Engine\Plugins\...，不像 Unity 那样放在 exe 旁边。
        // 从 exe 目录向上最多 5 级找到游戏根（含 Engine 目录者），再在 Engine\Plugins 里
        // 定点搜索 DLSS / FSR3 组件（跳过 Content/Paks 等资源目录，只走插件目录，开销很小）。
        static void ProbeUePlugins(string exeDir, DlssgGame g)
        {
            try
            {
                var d = new DirectoryInfo(exeDir);
                for (int i = 0; i < 5 && d != null; i++)
                {
                    string plug = Path.Combine(Path.Combine(d.FullName, "Engine"), "Plugins");
                    if (Directory.Exists(plug)) { FindEngineDlls(plug, 6, g); return; }
                    d = d.Parent;
                }
            }
            catch { }
        }

        static void FindEngineDlls(string dir, int depth, DlssgGame g)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (InName(FgDllNames, n)) g.HasFrameGen = true;
                    else if (InName(UpDllNames, n)) g.HasUpscaler = true;
                    else if (InName(FsrDllNames, n)) g.HasFsr3 = true;
                }
                if (depth <= 0) return;
                foreach (var sub in SafeDirs(dir))
                {
                    string bn = Path.GetFileName(sub).ToLowerInvariant();
                    if (bn == "content" || bn == "paks" || bn == "saved" || bn == "movies" || bn == "audio") continue;
                    FindEngineDlls(sub, depth - 1, g);
                }
            }
            catch { }
        }

        // ==================== 显卡硬件能力 ====================
        // 事实分两层，别混（2026-09-13 本文件曾把两层混为一谈、被用户当场纠正）：
        //  · 官方口径：DLSS 帧生成是 RTX 40 系（Ada）起的硬件能力，20/30 系不在 NVIDIA 支持列表里。
        //    GpuSupportsDlssFg() 判断的是这一层，含义是"官方支持与否"。
        //  · 本工具接的是社区项目 dlssg_for_sm86（github.com/sdli1995/dlssg_for_sm86）：
        //    它给 Ampere/Turing 补一份 SM86/SM75 内核，并内嵌 NVIDIA 自己的 DLSS-G 运行时，
        //    接管游戏对 nvngx_dlssg.dll 的加载请求 —— 跑的是【NVIDIA 原生 DLSS 帧生成管线】，
        //    不是把调用转成 FSR3。ini 里的 Router=SM86 指的是"路由到 SM86 内核"，不是 FSR。
        // 所以「官方不支持」≠「用不了」：装了代理后，游戏内应该出现【DLSS 帧生成】选项。
        // 真正要排查的是【代理有没有被激活】—— 入口 DLL 只被加载、从不被调用时（CP2077 对
        // version.dll 正是如此），代理永不初始化，游戏内自然只剩它自带的 FSR 帧生成。
        // （2026-09-13 用户反馈"注入后还是 FSR"，属于后者：入口没被调用，而不是硬件到顶。）
        // 判据唯一可靠来源：<游戏目录>\dlssg_sm86\logs\*.jsonl 出现 runtime_redirect /
        // feature_created / evaluate（见 CheckLogs + UI「运行诊断」）。
        static string gpuNameCache;
        public static string GpuName()
        {
            if (gpuNameCache != null) return gpuNameCache;
            gpuNameCache = "";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (k != null)
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            using (var sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                if (Conv(sk.GetValue("DriverDesc")).Trim().Length == 0) continue;
                                string d = Conv(sk.GetValue("DriverDesc")).Trim();
                                // 跳过虚拟显示适配器（GameViewer / 各家虚拟屏），只要真实显卡
                                if (d.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                gpuNameCache = d; break;
                            }
                        }
                }
            }
            catch { }
            return gpuNameCache;
        }

        // 本机能否使用真正的 DLSS 帧生成：true = 能 / false = 不能 / null = 型号读不到（不确定）
        public static bool? GpuSupportsDlssFg()
        {
            string n = GpuName().ToUpperInvariant();
            if (n.Length == 0) return null;
            if (n.Contains("RTX 50") || n.Contains("RTX 40")) return true;      // Ada / Blackwell
            if (n.Contains("RTX") || n.Contains("GTX") || n.Contains("MX")) return false;
            return null;
        }

        // 帧生成页的常驻说明：把「官方不支持 ≠ 用不了」和「怎么确认代理真的生效」写在界面上
        // ---------- 方案推荐（用户要求"插件太多，直接告诉我该装哪个"）----------
        //  档位取自配置里的三张名单（二游 / 3A / 竞技），不靠猜。
        // 归一化：去掉空格 / 连字符 / 下划线并转小写。
        //  必须做这一步 —— 目录名是「Cyberpunk 2077」，而名单里写的是「Cyberpunk2077」，
        //  直接比字符串会让 3A 游戏整类掉进"未归类"（2026-09-16 实测）。
        static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
                if (c != ' ' && c != '-' && c != '_' && c != '.') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // 竞技网游的内置关键词（归一化后比较）：cs2 / 无畏契约 / 三角洲 本身。
        //  ⚠ 别把「完美世界」「5EClient」放进来 —— 它们是 CS2 的对战平台，不是游戏
        //    （用户 2026-09-16 点名指出）。对战平台走 PlatformKeywords。
        static readonly string[] FpsKeywords = new string[] {
            "cs2", "csgo", "valorant", "riotclient", "deltaforce", "三角洲",
            // 国服名（条目 exe 常是平台自己的启动器名，只能靠标题认出来）
            "无畏契约", "卡拉彼丘", "calabiyau", "strinova",
            // PUBG（库里的条目名是 TslGame.exe / PUBG: BATTLEGROUNDS，两个都得认）
            "pubg", "tslgame", "绝地求生" };

        // 对战平台 / 匹配平台：**不是游戏**，帧生成对它没有意义。
        //  必须"完全等于"才算的（名字太通用，用包含匹配会误伤真游戏：
        //    SteamWorld Dig 含 steam、刺客信条：起源/Origins 含 origin、装机模拟器 含 模拟器）
        static readonly string[] PlatformExact = new string[] {
            "steam", "wegame", "origin", "mumu", "mumuplayer", "模拟器" };

        // 足够独特，可以"包含"匹配
        static readonly string[] PlatformPart = new string[] {
            "5eclient", "5ebox", "5e对战", "perfectworld", "完美世界", "对战平台", "电竞平台",
            "epicgames", "goggalaxy", "battlenet", "ubisoftconnect", "eadesktop", "eaapp",
            "gameviewer", "steamclient", "wegameplatform", "平台",
            // 桌面美化 / 壁纸软件（Steam 上架但不是游戏）：拦在分类这层，联动档位判成"不是游戏"，
            //   否则 wallpaper64.exe 会以"aaa 档"触发全套游戏联动（2026-09-28 实测）
            "wallpaperengine", "wallpaper32", "wallpaper64", "mydockfinder", "mydock" };

        // 名字像不像"对战平台"。命中即认为不是游戏。
        //  匹配的是"归一化后的 exe 名 / 目录名 / 标题"，不含完整路径 ——
        //  否则 G:\wegameapps\rail_apps\卡拉彼丘 这种正常游戏会被误判。
        //  ⚠ 曾经这里还有一条 EndsWith("launcher")：结果把「无畏契约」（条目 exe 是
        //    aclos-launcher.exe）判成了对战平台，还给了"平台负责匹配与反作弊"的错理由。
        //    游戏自己的启动器不是平台 —— 那条已删（2026-09-16 渲染实测抓到）。
        public static bool IsPlatformName(string s)
        {
            string n = Norm(s);
            if (n.Length == 0) return false;
            foreach (string k in PlatformExact) if (n == k) return true;
            foreach (string k in PlatformPart) if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static bool InList(List<string> list, string norm)
        {
            if (list == null || norm.Length == 0) return false;
            foreach (string x in list) if (Norm(x) == norm) return true;
            return false;
        }

        // 最近一次合并出的游戏库（MergeScan 是唯一写入点 —— 单一来源，避免多处各维护一份走样）。
        //  链接/联动判定要用它，才能对"后续新加的游戏"同样生效（2026-09-16 用户要求）。
        public static List<DlssgGame> Library = new List<DlssgGame>();

        // 按进程名（带不带 .exe 都行）在库里找条目 —— 供 Core 的联动判定用。
        //  先按 exe 名比，再退一步按目录末段比（扫出来的条目有时 Exe 为空）。
        public static DlssgGame FindByProcess(string processName)
        {
            if (processName == null || processName.Length == 0) return null;
            List<DlssgGame> lib = Library;
            if (lib == null || lib.Count == 0) return null;
            string want = Norm(Path.GetFileNameWithoutExtension(processName));
            if (want.Length == 0) return null;
            for (int i = 0; i < lib.Count; i++)
            {
                DlssgGame g = lib[i];
                if (g == null || g.Exe == null || g.Exe.Length == 0) continue;
                if (Norm(Path.GetFileNameWithoutExtension(g.Exe)) == want) return g;
            }
            for (int i = 0; i < lib.Count; i++)
            {
                DlssgGame g = lib[i];
                if (g == null || g.Dir == null || g.Dir.Length == 0) continue;
                string d = g.Dir.TrimEnd('\\', '/');
                int k = d.LastIndexOf('\\');
                if (k >= 0 && k + 1 < d.Length && Norm(d.Substring(k + 1)) == want) return g;
            }
            return null;
        }

        // 把「方案推荐」的分类映射成**联动档位**（Core.AutoCategory 用）。
        //  返回 "" = 不是游戏（对战平台 / 启动器）→ 不参与任何游戏联动（电源、加速包、远控都不碰）。
        //  返回 "aaa" / "gacha" / "fps" / "mmo" = 是游戏，按档位联动。
        //  ⚠ 关键：以前 Core.AutoCategory 对新游戏一律 `return "fps"`，于是"新装的游戏"会被
        //    当成竞技网游去暂停 UU远程。现在分类与推荐共用同一套规则，且**每次扫描重算**。
        public static string LinkCategory(DlssgGame g)
        {
            if (g == null) return "";
            string cat = g.RecCat;
            if (cat == null)
            {
                try { cat = RecommendCategory(g); } catch { cat = "other"; }
                g.RecCat = cat;                       // 缓存：AllGameProcesses 会被高频调用
            }
            if (cat == "platform") return "";         // 不是游戏
            if (cat == "fps") return "fps";
            if (cat == "gacha") return "gacha";
            return "aaa";                             // aaa / other：是游戏但非竞技 → 只做电源/加速包，不动远控
        }

        // 判定档位：优先看工具认得的游戏（绝区零 / 鸣潮），其次按 exe 名 / 目录名 / 标题
        //  三个候选去匹配名单（都是归一化比较），最后用"游戏有没有 DLSS 帧生成组件"兜底 ——
        //  比直接甩一个"未归类"有用得多。
        public static string RecommendCategory(DlssgGame g)
        {
            if (g == null) return "other";
            try
            {
                string kind = XeMfg.Detect(g.Dir);
                if (kind == "zzz" || kind == "wuwa") return "gacha";

                string[] cand = new string[] {
                    g.Exe == null ? "" : Path.GetFileNameWithoutExtension(g.Exe),
                    g.Dir == null ? "" : Path.GetFileName(g.Dir.TrimEnd('\\', '/')),
                    g.Title };
                // 对战平台 / 启动器优先判定：它们既不是二游也不是竞技网游，
                //  以前会把「完美世界竞技平台」「5EClient」当成竞技网游、甩一段封号警告（2026-09-16 用户指出）。
                foreach (string c in cand)
                    if (IsPlatformName(c)) return "platform";
                foreach (string c in cand)
                {
                    string n = Norm(c);
                    if (n.Length == 0) continue;
                    if (InList(Program.Cfg.GachaGames, n)) return "gacha";
                    if (InList(Program.Cfg.AaaGames, n)) return "aaa";
                    if (InList(Program.Cfg.FpsGames, n)) return "fps";
                    if (InList(Program.Cfg.GameProcesses, n)) return "fps";
                }
                // 兜底一：竞技网游的内置关键词（配置里的 fpsGames 是空的，但库里叫
                //  「完美世界竞技平台」「5EClient」这种 —— 不认出来就只会甩一句"未归类"）
                foreach (string c in cand)
                {
                    string n = Norm(c);
                    if (n.Length == 0) continue;
                    foreach (string k in FpsKeywords)
                        if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return "fps";
                }
                // 兜底二：集成了 DLSS 帧生成组件的（nvngx_dlssg.dll / sl.dlss_g.dll）按 3A 单机对待
                if (g.HasFrameGen) return "aaa";
            }
            catch { }
            return "other";
        }

        public static string CategoryName(string cat)
        {
            if (cat == "gacha") return "二游";
            if (cat == "aaa") return "3A 单机";
            if (cat == "fps") return "竞技网游";
            if (cat == "platform") return "对战平台 / 启动器（不是游戏）";
            return "未归类";
        }

        // ---------- 方案推荐：自动识别 → 一句话结论 + 适用度 + 可一键执行 ----------
        //  用户要求"强调色显示 + 自动识别自动选择"：Head 用强调色大号字、Stars 给适用度、
        //  Action 告诉界面该执行哪个动作（"一键应用推荐方案"直接照它做，不用用户去下面找按钮）。
        public class Rec
        {
            public string Head = "";          // 强调色标题
            public string Stars = "☆☆☆☆☆";   // 适用度
            public string Verdict = "";       // 推荐 / 可选 / 不推荐
            public string State = "";         // 当前状态
            public string Why = "";           // 一句话理由
            public string Btn = "一键应用推荐方案";
            public string Action = "none";    // opti | 030 | 030spoof | none
            public bool Bad;                  // true = 用告警色（红）而不是强调色（绿）
            public bool Dim;                  // true = 用中性灰（"这不是游戏"这类中性结论）
        }

        // NR 状态一句话（给推荐文案与区块正文共用）
        static string NrHint(string dir)
        {
            int nr = XeMfg.NrEnabled(dir);
            if (nr == 0) return "；DLSS 5 神经渲染已关闭（帧生成不受影响）。";
            if (nr == 1) return "；⚠ DLSS 5 神经渲染开着 —— 它每帧跑一遍 158 MB 模型，"
                              + "上游文档写明其设计约束「是用设备挂死换来的」；"
                              + "2026-09-16 实测与 4X 帧生成同开 → 鸣潮两次 GPUCrash（设备挂死）。"
                              + "若出现黑屏/崩溃先把这一项关掉再试（与驱动版本无关，升级驱动也解决不了）。";
            return "";
        }

        // 「方案 A」区块正文：按选中的游戏给不同的话，不再把某一个游戏名写死在标题里。
        public static string OptiNote(DlssgGame g)
        {
            if (g == null)
                return "先在「游戏库」页选中一个游戏，这里会给出它用本方案的结果。\n"
                     + "本方案 = OptiScaler 注入（dxgi / d3d12 入口）—— 同时提供帧生成与 DLSS 5 神经渲染。";
            string kind = XeMfg.Detect(g.Dir);
            if (kind == "zzz")
                return "绝区零：先确认游戏跑在 DX12（右键卡片 →「DX12 模式启动」）—— 3.0+ 的 DX12 模式自带超分与帧生成，"
                     + "多数情况游戏内直接开就够了，不必注入。只有要跑 DLSS 帧生成（官方限 40 系）才用本方案，"
                     + "且该选项要靠「② 显卡名伪装」才会出现。";
            if (kind == "wuwa")
                return "鸣潮：本方案在用。dxgi.dll 入口 · 帧生成走游戏原生 DLSSG（SM86 解锁）"
                     + NrHint(g.Dir);
            return "本方案只对绝区零 / 鸣潮有意义（依赖专用入口名与显卡名伪装）。"
                 + "其它游戏请用下方 0.3.x 通用模式。";
        }

        // ---------- 显卡代际门禁（分享给别人时，对方的卡各不相同）----------
        //  本工具的帧生成路线只服务 RTX 20/30 系（SM75/SM86 内核补齐）：
        //  · RTX 40/50 官方驱动原生就有 DLSS 帧生成（40 系还有原生多帧生成）—— 不需要任何代理，
        //    而且把 4090 伪装成 "RTX 5090" 是**降级**；
        //  · GTX 10/16 系没有对应的帧生成运行库（作者只做了 20/30 系内核）；
        //  · AMD / Intel 走游戏自带的 FSR3 / XeSS。
        //  之前的推荐只看"游戏类型"，不看对方的卡 —— 给 40 系用户推"一键应用 0.3.x + 伪装"是错的。
        public static string GpuTierFromName(string name)
        {
            if (name == null) return "unknown";
            string u = name.ToUpperInvariant();
            if (u.IndexOf("(探测中)", StringComparison.Ordinal) >= 0) return "unknown";
            if (u.IndexOf("RTX 50", StringComparison.Ordinal) >= 0) return "blackwell";
            if (u.IndexOf("RTX 40", StringComparison.Ordinal) >= 0) return "ada";
            if (u.IndexOf("RTX 30", StringComparison.Ordinal) >= 0) return "ampere";
            if (u.IndexOf("RTX 20", StringComparison.Ordinal) >= 0) return "turing";
            if (u.IndexOf("RTX", StringComparison.Ordinal) >= 0) return "rtx-other";
            if (u.IndexOf("GTX", StringComparison.Ordinal) >= 0) return "gtx";
            if (u.IndexOf("NVIDIA", StringComparison.Ordinal) >= 0) return "nvidia-other";
            if (u.IndexOf("RADEON", StringComparison.Ordinal) >= 0 || u.IndexOf("AMD", StringComparison.Ordinal) >= 0) return "non-nvidia";
            if (u.IndexOf("ARC", StringComparison.Ordinal) >= 0 || u.IndexOf("INTEL", StringComparison.Ordinal) >= 0) return "non-nvidia";
            return "unknown";
        }

        static string _gpuTier;
        public static string GpuTier()
        {
            string t = _gpuTier;
            if (t != null) return t;
            t = GpuTierFromName(Program.GpuDisplayName);
            if (t != "unknown") _gpuTier = t;   // unknown 不缓存：WMI 探测可能还没完成，下次重试
            return t;
        }

        // 命中门禁返回一条完整推荐（调用方直接 return），null = 该卡不在门禁范围（照常走分类推荐）
        public static Rec GpuGateRec(string tier)
        {
            if (tier == "blackwell" || tier == "ada")
            {
                var r = new Rec();
                r.Head = "不用装：这张卡原生支持 DLSS 帧生成";
                r.Stars = "";
                r.Verdict = "不适用";
                r.Dim = true;
                r.Why = "RTX 40 / 50 系在官方驱动里就有 DLSS 帧生成（40 系还有原生多帧生成），"
                      + "直接在游戏画质菜单开即可 —— 不需要任何代理补丁，本工具帮不上忙也不该插手。"
                      + "\n特别注意：本工具的「显卡名伪装」会把卡名改成 RTX 5090，对 40 系那是降级，别用。";
                r.Btn = "无需安装";
                r.Action = "none";
                return r;
            }
            if (tier == "gtx" || tier == "nvidia-other" || tier == "non-nvidia")
            {
                var r = new Rec();
                r.Head = "用不了：这张显卡不在 DLSS 帧生成的支持范围";
                r.Stars = "★☆☆☆☆";
                r.Verdict = "不支持";
                r.Bad = true;
                r.Why = "本工具补的是 NVIDIA RTX 20/30 系缺失的帧生成内核（SM75 / SM86）。"
                      + (tier == "gtx" || tier == "nvidia-other"
                          ? "GTX 系列没有对应的帧生成运行库，装了也不会在游戏里出现选项。"
                          : "AMD / Intel 显卡走游戏自带的 FSR3 / XeSS 帧生成，DLSS 运行库在它们上无法加载。")
                      + "游戏画质菜单里能看到 FSR / XeSS 的话，直接用游戏自带的即可。";
                r.Btn = "无需安装";
                r.Action = "none";
                return r;
            }
            return null;   // ampere / turing / rtx-other / unknown → 照常分类推荐
        }

        public static Rec BuildRec(DlssgGame g)
        {
            var r = new Rec();
            if (g == null)
            {
                r.Head = "先选一个游戏";
                r.Stars = "";
                r.State = "未选中";
                r.Why = "到「游戏库」页点一张卡片，这里会自动给出该装哪个方案，并可直接一键应用。";
                return r;
            }
            string cat = RecommendCategory(g);
            string kind = XeMfg.Detect(g.Dir);
            string cur = Plan.Current(g.Dir);
            r.State = cur == Plan.None ? "当前未接入（游戏跑原生）" : ("当前 " + Plan.PlanName(cur));

            if (cat == "platform")
            {
                r.Head = "不用装：这不是游戏，是对战平台 / 启动器";
                r.Stars = "";
                r.Verdict = "不适用";
                r.Dim = true;
                r.Why = "对战平台（5E / 完美世界竞技平台这类）负责匹配与反作弊，本身没有帧生成 —— "
                      + "注入进去既没收益、还可能被它的反作弊盯上。要装的是它启动的那个游戏（例如 cs2.exe）。"
                      + (g.Ignored ? "\n它已经在忽略清单里，保持忽略即可。" : "");
                r.Btn = "无需安装";
                return r;
            }
            if (cat == "fps")
            {
                r.Head = "不推荐安装（保持原生）";
                r.Stars = "★☆☆☆☆";
                r.Verdict = "不推荐";
                r.Bad = true;
                r.Why = "竞技网游带内核反作弊，注入类工具封号风险自负，收益也不值当 —— 本工具只给它做电源 / 加速包联动。";
                r.Btn = "无需安装";
                return r;
            }
            // 显卡代际门禁：分享给别人时对方的卡各不相同（40/50 原生有、GTX/他牌用不了）。
            //  放在 platform/fps 之后：那两类本来就"不推荐安装"，结论与显卡无关，保持不变。
            Rec gate = GpuGateRec(GpuTier());
            if (gate != null) return gate;

            // DX12 前置门禁（v3.3.4）：绝区零 3.0+ 这类游戏的「超分辨率 / 帧生成」只在 DX12 模式下
            // 出现，DX11 下整块面板不渲染 —— 用户会以为"注入失败 / 工具没用"。
            // 2026-09-17 本机实测：Player.log = Direct3D 11.0，游戏内既没有 DLSS 也没有 FSR 帧生成。
            // 判据：目录里有 amd_fidelityfx_framegeneration_dx12.dll（帧生成是 DX12 组件）+ 实测跑 DX11。
            bool dx12Game = File.Exists(Path.Combine(g.Dir, "amd_fidelityfx_framegeneration_dx12.dll"));
            string api = dx12Game ? Lib.RenderApi(g) : "";
            if (dx12Game && api == "DX11")
            {
                r.Head = "先解决运行模式：游戏现在跑 DX11";
                r.Stars = "";
                r.Verdict = "前置条件";
                r.State = "当前 Direct3D 11 · 帧生成面板不会被渲染出来";
                r.Why = "这款游戏的「超分辨率 / 帧生成」只在 DX12 模式下出现，DX11 下整块面板不渲染 —— "
                      + "所以任何注入都不会有效果。\n"
                      + "下一步只有一件事：右键它的卡片 →「DX12 模式启动（-use-d3d12）」→ 点「启动游戏」。"
                      + "启动后本工具会自动确认是否真的跑在 DX12；跑起来后 FSR 帧生成本机原生可用，不需要注入。";
                r.Btn = "一键开启 DX12 启动";
                r.Action = "dx12";
                return r;
            }

            if (cat == "gacha" && kind == "zzz")
            {
                if (api == "DX12")
                {
                    // 3.0+ 的 DX12 模式原生带 DLSS / FSR 超分与帧生成：20/30 系直接开 FSR 帧生成即可，
                    // 不需要任何注入。要 DLSS 帧生成（官方限 40 系）才轮到 0.3.x + 显卡名伪装。
                    r.Head = "推荐：游戏内直接开 FSR 帧生成（原生，无需注入）";
                    r.Stars = "★★★★★";
                    r.Verdict = "推荐";
                    r.State = "当前 Direct3D 12 · 游戏自带 DLSS / FSR 超分与帧生成";
                    r.Why = "DX12 模式已生效 → 游戏内「设置 → 画面 → 高级 → 帧生成」选【FSR 帧生成】即可"
                          + "（官方说明：FSR 帧生成支持 NVIDIA 20 系及以上含 RT Core）。"
                          + "要 DLSS 帧生成（官方限 40 系）才需要下面「0.3.x + 显卡名伪装」的注入路线，属进阶可选。";
                    r.Btn = "无需安装";
                    r.Action = "none";
                    return r;
                }
                r.Head = "推荐：DLSS MFG 0.3.x（作者签名版）+ 显卡名伪装";
                r.Stars = "★★★★☆";
                r.Verdict = "推荐";
                r.Why = "目录里有 DLSS / FSR 帧生成组件。先确认游戏跑在 DX12（右键卡片 →「DX12 模式启动」→ 启动游戏）："
                      + "多数情况开了 DX12 后在游戏内直接开 FSR 帧生成就够了；"
                      + "要 DLSS MFG（30 系补 SM86 内核）才走这条注入路线 —— 绝区零会拦未签名 DLL，必须用作者签名版，"
                      + "另外它的 DLSS 帧生成选项还得靠显卡名伪装才会出现。";
                r.Btn = "一键应用（0.3.x + 伪装）";
                r.Action = "030spoof";
                return r;
            }
            if (cat == "gacha")
            {
                if (!g.HasFrameGen)
                {
                    // 二游里没有 DLSS 帧生成组件的（原神 / 星铁这类）装了也没选项可开
                    r.Head = g.HasFsr3 ? "可选：游戏自带 FSR3 帧生成，不必装" : "不用装：游戏没有 DLSS 帧生成";
                    r.Stars = g.HasFsr3 ? "★★☆☆☆" : "★☆☆☆☆";
                    r.Verdict = g.HasFsr3 ? "可选" : "不适用";
                    r.Dim = !g.HasFsr3;
                    r.Why = g.HasFsr3
                        ? "游戏目录里没有 nvngx_dlssg.dll，但有 FSR3 帧生成 —— 直接在游戏内开 FSR3 即可。"
                        : "游戏目录里没有 nvngx_dlssg.dll / sl.dlss_g.dll，代理没有可接管的调用，装了也不会出现选项。";
                    r.Btn = "无需安装";
                    return r;
                }
                // 方案 A 的成立前提是**有专用入口名**（工具认得出这款游戏），不是"这是二游"。
                //  2026-09-18 修：此前所有二游一律推方案 A，与本区块正文
                //  「本方案只对绝区零 / 鸣潮有意义」直接打架；而且非绝/鸣的入口会落成
                //  version.dll，带内核反作弊的二游按【文件名】就能拦掉（即本文件自己记的报错 11008）——
                //  推荐了不能用的方案 + 入口必然是错的 + 同屏文案自打脸，三重错。
                if (kind.Length > 0)
                {
                    r.Head = "推荐：" + Plan.PlanName(Plan.Opti);
                    r.Stars = "★★★★★";
                    r.Verdict = "推荐";
                    r.Why = "帧生成走游戏原生 DLSSG（SM86 解锁，实测 6X）+ 可另开 DLSS 5 神经渲染。"
                          + NrHint(g.Dir) + RefreshHint();
                    r.Btn = "一键应用（方案 A）";
                    r.Action = "opti";
                    return r;
                }
                // 没有专用入口的二游 → 通用 0.3.x。
                //  这类游戏原生集成了 DLSS 帧生成（否则上面那条 !g.HasFrameGen 已经拦掉了），
                //  被挡住的只是「驱动不给 20/30 系开放」这一层，0.3.x 补上 SM86 内核即可。
                r.Head = "推荐：DLSS MFG 0.3.x 通用模式";
                r.Stars = "★★★☆☆";
                r.Verdict = "可试";
                r.Why = "游戏自己集成了 DLSS 帧生成，挡住的只是「驱动不给 20/30 系开放」这一层 —— 0.3.x 补 SM86 内核即可。"
                      + "通用模式默认用 version.dll 入口；二游常带内核反作弊，可能按【文件名】拦掉它，"
                      + "那就到下方 0.3.x 区块点「换入口重试」，优先试 dxgi.dll / d3d12.dll。"
                      + "\n两个前置（二游实测都躲不掉）：游戏要以 DX12 模式启动；游戏内要同时打开 NVIDIA Reflex，帧生成开关才会出现。"
                      + RefreshHint();
                r.Btn = "一键应用（0.3.x）";
                r.Action = "030";
                return r;
            }
            if (cat == "aaa")
            {
                if (!g.HasFrameGen)
                {
                    // 没有帧生成组件的游戏装了也不会出现选项 —— 这一条比"推荐/不推荐"有用
                    r.Head = g.HasFsr3 ? "可选：游戏自带 FSR3 帧生成，不必装" : "不推荐：该游戏没有 DLSS 帧生成组件";
                    r.Stars = g.HasFsr3 ? "★★☆☆☆" : "★☆☆☆☆";
                    r.Verdict = g.HasFsr3 ? "可选" : "不推荐";
                    r.Bad = !g.HasFsr3;
                    r.Why = g.HasFsr3
                        ? "目录里没有 nvngx_dlssg.dll，但有 FSR3 帧生成组件 —— 直接在游戏内开 FSR3 更省事；"
                          + "0.3.x 需要游戏自己请求 DLSS 帧生成，此款没有可接管的调用。"
                        : "目录里既没有 nvngx_dlssg.dll 也没有 FSR3 组件，代理没有可接管的调用，装了也不会出现选项。";
                    r.Btn = "无需安装";
                    return r;
                }
                r.Head = "推荐：DLSS MFG 0.3.x 通用模式";
                r.Stars = "★★★★★";
                r.Verdict = "推荐";
                r.Why = "游戏自己集成了 DLSS 帧生成 → 单机只放两个文件、不用改显卡名即可。"
                      + "倍率由游戏菜单决定（2077 的菜单上限是 4X，300 帧就是 4X 在正常工作，代理抬不上去）。"
                      + RefreshHint();
                r.Btn = "一键应用（0.3.x）";
                r.Action = "030";
                return r;
            }
            if (!g.HasFrameGen && !g.HasFsr3)
            {
                // 连帧生成组件都没有的游戏（库里实测：P5R / PEAK / 以撒 / 尸姬之梦 / Esports Manager…）
                //  推荐安装毫无意义 —— 代理没有可接管的调用（2026-09-16 库诊断表抓到的）。
                r.Head = "不用装：没检测到帧生成组件";
                r.Stars = "★☆☆☆☆";
                r.Verdict = "不适用";
                r.Dim = true;
                r.Why = "游戏目录里没有 nvngx_dlssg.dll / sl.dlss_g.dll，代理没有可接管的调用，装了也不会出现选项。"
                      + "若你确认游戏内本来就有「DLSS 帧生成」开关，可到下方 0.3.x 区块手动装一次试试。";
                r.Btn = "无需安装";
                return r;
            }
            r.Head = "推荐：DLSS MFG 0.3.x 通用模式（可试）";
            r.Stars = g.HasFrameGen ? "★★★★☆" : "★★☆☆☆";
            r.Verdict = g.HasFrameGen ? "可选" : "低把握";
            r.Why = g.HasFrameGen
                ? "未归类，但游戏集成了 DLSS 帧生成组件 → 0.3.x 有机会生效，进游戏看菜单便知。"
                : "未归类；检测到 FSR3 帧生成但没看到 DLSS 帧生成组件 → 先在游戏内试 FSR3，再考虑 0.3.x。";
            r.Btn = "一键应用（0.3.x）";
            r.Action = "030";
            return r;
        }

        // 按显示器刷新率给"倍率甜点"提示。
        //  依据：生成帧超过刷新率就是无效帧 → 可用倍率上限 = 刷新率 ÷ 基础帧。
        //  只读刷新率、不猜帧率（本工具不采集帧率），所以按常见基础帧档位给结论。
        public static string RefreshHint()
        {
            int hz = Program.RefreshHz();
            if (hz <= 0) return "";
            int f = hz / 40;                       // 基础帧 40 时能用的最大倍率
            if (f < 2) f = 2;
            if (f > 6) f = 6;
            return "（显示器 " + hz + "Hz：生成帧超过 " + hz + " 就是无效帧 —— 基础帧 40 时 " + f
                 + "X 就够，6X 要基础帧 ≤" + (hz / 6) + " 才不浪费。）";
        }

        // 给它一句话结论 + 当前状态（供「方案推荐」区块那两行动态文字用）
        public static string RecommendLine(DlssgGame g)
        {
            if (g == null) return "目标：未选中 —— 到「游戏库」页点一张卡片，这里会给出一句话结论。";
            string cat = RecommendCategory(g);
            string kind = XeMfg.Detect(g.Dir);
            string cur = Plan.Current(g.Dir);
            string plan, why;
            if (cat == "gacha")
            {
                if (kind == "zzz")
                {
                    plan = "0.3.x（作者签名版）+ 注册表显卡名伪装";
                    why = "方案A 的代理从未被绝区零加载过（未签名 DLL 被反作弊拦掉，2026-09-16 实测两次）——"
                        + "用签名版 0.3.x；伪装文件在 D:\\youhua\\绝区零-显卡名伪装\\，双击导入后重启电脑。";
                }
                else
                {
                    plan = Plan.PlanName(Plan.Opti);
                    why = "帧生成走游戏原生 DLSSG（SM86 解锁，实测 6X）+ 可另开 DLSS 5 神经渲染"
                        + NrHint(g.Dir);
                }
            }
            else if (cat == "aaa")
            {
                plan = "0.3.x 通用模式";
                why = "单机：只放两个文件，不需要改显卡名。要 6X 就用本工具自带的 310.9 版，并把「最大生成帧」设成 5。";
            }
            else if (cat == "fps")
            {
                plan = "不建议装（只做电源 / 加速包联动）";
                why = "竞技网游带内核反作弊，注入类工具封号风险自负，收益也不值当。";
            }
            else if (kind.Length > 0)
            {
                plan = Plan.PlanName(Plan.Opti);
                why = "已识别为绝区零 / 鸣潮 → 走方案 A（它靠专用入口 + 显卡名伪装）。";
            }
            else
            {
                plan = "0.3.x 通用模式（可试）";
                why = "未归类：0.3.x 只拦截 nvngx_dlssg.dll 的加载，装错最多不生效，进游戏一看便知。";
            }
            string state = cur == Plan.None ? "当前未接入（游戏跑原生）"
                         : ("当前：" + Plan.PlanName(cur));
            return "目标：" + g.Title + "（" + CategoryName(cat) + "）  →  推荐：" + plan
                 + "\n" + state + "。" + why;
        }

        public static string HardwareFgNote()
        {
            bool? fg = GpuSupportsDlssFg();
            string gpu = GpuName();
            if (fg == true)
                return "显卡：" + gpu + " —— 官方支持 DLSS 帧生成，游戏内直接开原生选项即可（本工具的代理方案主要给 RTX 20/30 系用）。";

            return "显卡：" + gpu + " —— NVIDIA 官方未给这一代开放 DLSS 帧生成。本工具用的是社区开源方案（GitHub）："
                 + "\n  · OptiScaler（github.com/optiscaler/OptiScaler）—— 拦截并接管上采样 / 帧生成调用"
                 + "\n  · dlss-unlocked（ShyVortex，含 SM86 MFG 解锁）+ dlssg_for_sm86（sdli1995）—— 帧生成运行时"
                 + "\n  · fakenvapi + Intel XeSS-FG 运行库（libxess / libxess_fg）—— 实际执行插帧的引擎"
                 + "\n\n工作链路（三层，这解释了游戏菜单为什么会冒出开关）："
                 + "\n  ① OptiScaler 把本机显卡伪装成 RTX 5090 → 游戏菜单才肯显示「DLSS 帧生成」"
                 + "\n  ② 游戏发出的原生 DLSS 帧生成调用被 OptiScaler 拦下（FGInput=DLSSG）"
                 + "\n  ③ 插帧由谁执行，两个游戏不同："
                 + "\n     绝区零 → 交给 Intel XeSS 帧生成引擎（FGOutput=XeFG，2-6 倍）；"
                 + "\n     鸣潮   → 仍走游戏原生 DLSSG，由 SM86 解锁让它在 30 系上能跑（FGOutput=DLSSG，实测可 6X）"
                 + "\n\n带内核反作弊的二游（绝区零 / 鸣潮）：常规 version.dll 入口会被按【文件名】拦掉（实测报错 11008），"
                 + "\n改用 d3d12.dll（绝区零）/ dxgi.dll（鸣潮）这两个 DX12 必加载的库名做入口 —— 拦不掉。"
                 + "\n\n用法：下方「方案 A · OptiScaler 注入」区块 → 选中游戏 → 点「切换到本方案」；游戏内按 F10 呼出 OptiScaler 面板。"
                 + "\n生效判据：① 游戏菜单出现「DLSS 帧生成」；② F10 面板显示 FG 已启用；③ 帧数明显翻倍。"
                 + "\n\n提醒：帧生成只提高【显示帧率】，不会降低延迟（插帧必然增加延迟）。基础帧顶到 50-60 以上再开效果最好。"
                 + "\n\n【本轮联网核实】本轮把 NR 段的上游项目找齐了：它出自 "
                 + "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass（基于 OptiScaler + Dagherbou 的 NR fork，"
                 + "配色合成取自 RenoDX）。该文档给出三条硬前提："
                 + "\n  · 驱动：上游的 ≥616.56 只对 RTX 50 是硬门槛（见下方纠正）；"
                 + "20/30/40 系用 ShortFuse 跨代 310.8 运行库（哈希 E67DEE20…），旧驱动也能跑；"
                 + "\n  · 起步姿势是「先关 NR + 2X 帧生成」，再逐层加 —— 别一上来就堆满；"
                 + "\n  · 它的若干设计约束原文写着「were paid for with device hangs」→ NR 与帧生成同开有挂死风险。"
                 + "\n  另外：Reflex 的 FPS 上限会连带限制生成帧（150 的限制在 4X 下会把渲染压到约 37.5fps），"
                 + "提倍率并不会抬高这个上限 —— 测帧数前先把 VSync 关掉、把帧率上限设为 0。"
                 + "\n\n【2026-09-16 纠正：驱动门槛被我读错了】上一版写「驱动 ≥616.56 是硬前提」，那是把 DLSS5-Swapper 源码里的 OPTI_DRIVER=61656 单独摘出来看了。"
                 + "完整判据是 driverSupported = blackwell(卡) && 驱动≥61656 —— 只对 RTX 50 生效；其注释原话：模型文件随应用提供、不取自已安装驱动，所以旧驱动照样能跑 OptiScaler 安装。"
                 + "对 RTX 20/30/40 它不是门槛。真正要避开的是 ≥616.64：NEURAL_FAULT_DRIVER=61664，且该检查对任何 N 卡生效 —— 616.64 与 616.86 上每次 neural evaluate 都在 NVIDIA 自家 NGX 运行时 fault，只有 616.56 能跑完（DLSS5-Feeder issue #54）。"
                 + "版本序列：610.88（本机）→ 616.56（08-26）→ 616.64（09-03）→ 616.86（09-04 hotfix）→ 616.92（09-09 最新）。所以既≥616.56、NR 又能跑完的只有 616.56 一个版本。"
                 + "而 616.56 自己带着两个已确认问题（升级后远程桌面 RDP 黑屏、无法创建虚拟显示器，均到 616.86 hotfix 才修；另有「优先最高性能」电源模式可能不生效）。"
                 + "结论：常驻远程控制（UU远程 / GameViewer）的机器，升 616.56 风险大于收益 —— 而且这次鸣潮崩溃不是驱动版本造成的，升级解决不了。";
        }

        // ==================== 注入前的收益 / 风险评估 ====================
        // 判据按「硬事实优先」排序，而不是只靠游戏名名单（名单必然漏，漏一次用户就踩一次坑）：
        //   ① 反作弊：名单命中（二游 / 内核反作弊）→ 封号风险，最高优先级，必须弹
        //   ② 能力：游戏目录里没有 nvngx_dlssg.dll / sl.dlss_g.dll → 根本没有可接管的调用，装了不生效
        //   ③ 非官方路径：本机不在 NVIDIA 官方支持列表 → 能生效但可能不出现选项 / 崩溃 / 画面异常
        // 三者全不命中才返回 null（不打扰用户）。
        // 返回 null = 无风险提示；非 null = 给 UI 弹确认框的文案。
        // ⚠ 不要把「本机显卡官方不支持」写成「注入也没用」—— 本工具存在的意义就是绕过这一条
        //   （dlssg_for_sm86 在 Ampere 上跑原生 DLSS-G）。（2026-09-13 曾犯此错，被用户纠正。）
        // 反作弊 / 二游名单：**唯一来源**。判定入口只有 IsAntiCheat()，
        // 不要在别处再抄一份（2026-09-13 FSR 组件名写在两处漏一处的教训）。
        static readonly string[] GachaWords = new string[] {
            "zenless", "yuanshen", "genshin", "starrail", "honkai", "mihoyo", "hoyoplay", "cognosphere",
            "wuthering", "kuro", "endfield", "snowbreak", "blue archive", "arknights", "nikke",
            "崩坏", "原神", "星穹", "绝区零", "鸣潮", "库洛", "米哈游", "尘白", "幻塔", "明日方舟"
        };

        public static string InjectionRiskNote(DlssgGame g)
        {
            if (g == null) return null;
            bool gacha = IsAntiCheat(g);
            bool? fg = GpuSupportsDlssFg();

            var sb = new StringBuilder();
            if (gacha)
            {
                sb.Append("【内核反作弊 / 封号风险 —— 请先看这段】\n\n");
                sb.Append("· 这类游戏带内核级反作弊（mhyprot / HoYoKProtect / ACE 等），注入第三方 DLL\n");
                sb.Append("  属于明确的封号高危行为；社区已记录过反作弊直接拦截代理 DLL 加载的案例。\n");
                sb.Append("· 被封的是你的账号，工具无法撤销，也帮不上忙。要不要冒这个险只能你自己定。\n");
                if (PreferD3d12 && D3d12Feasible(g))
                    sb.Append("· 技术上：这类游戏会按【文件名】拦掉 version.dll / winmm.dll 等常规入口\n"
                            + "  （上游 issue #111 实测原话），本工具已自动改用 d3d12.dll 入口 —— 它是 DX12\n"
                            + "  游戏必须加载的系统库，拦不掉。这只解决「装得进去」，不解决封号风险。\n");
                else if (PreferD3d12)
                    sb.Append("· 提示：" + D3d12Hint() + "\n");
                sb.Append("\n");
            }
            if (!g.HasFrameGen)
            {
                sb.Append("【本游戏不支持 DLSS 帧生成 —— 装了也不会生效】\n\n");
                sb.Append("· 目录里没有 nvngx_dlssg.dll / sl.dlss_g.dll，代理没有可接管的调用，等于空转。\n");
                sb.Append("· 只有游戏自带的 FSR 帧生成这条路（如果它有）。\n\n");
            }
            else if (fg == false)
            {
                sb.Append("【这是社区补内核方案，非 NVIDIA 官方支持】\n\n");
                sb.Append("· 本机 " + GpuName() + " 不在官方 DLSS 帧生成支持列表里。本工具靠 dlssg_for_sm86 给\n");
                sb.Append("  Ampere 补 SM86 内核 + 内嵌 DLSS-G 运行时，跑的是 NVIDIA 原生帧生成管线（不是 FSR）。\n");
                sb.Append("· 能生效，但属于非官方路径：个别游戏会出现选项不出现 / 崩溃 / 画面异常；\n");
                sb.Append("  NVIDIA 更新驱动或运行时后也可能失效。\n\n");
            }
            if (sb.Length == 0) return null;

            sb.Append("装完怎么确认真的生效：重启游戏 → 设置里有没有「DLSS 帧生成」选项；\n");
            sb.Append("或回工具点「运行诊断」—— 日志出现 evaluate 事件才算真的在补帧；\n");
            sb.Append("若只有 FSR 选项，说明入口 DLL 没被调用，点「换入口重试」。\n\n");
            sb.Append("仍要为「" + g.Title + "」开启注入吗？");
            return sb.ToString();
        }

        static string Conv(object o) { return o == null ? "" : o.ToString(); }

        // 从命令行字符串（UninstallString / DisplayIcon）提取所在目录，容忍带引号与参数
        static string DirOfCmd(string cmd)
        {
            if (cmd == null) return null;
            cmd = cmd.Trim();
            if (cmd.Length == 0) return null;
            string exe = null;
            if (cmd.StartsWith("\""))
            {
                int e = cmd.IndexOf('"', 1);
                if (e > 1) exe = cmd.Substring(1, e - 1);
            }
            else
            {
                int e = cmd.ToLowerInvariant().IndexOf(".exe", StringComparison.Ordinal);
                if (e >= 0) exe = cmd.Substring(0, e + 4);
                else { int s = cmd.IndexOf(' '); exe = s > 0 ? cmd.Substring(0, s) : cmd; }
            }
            if (exe == null || exe.Trim().Length == 0) return null;
            try { return Path.GetDirectoryName(exe); } catch { return null; }
        }

        // ==================== 游戏 / 非游戏判定 ====================
        // 旧版规则 = "目录里有 exe 就算游戏"，而扫描根又包含 C:\Program Files，
        // 结果 Git / dotnet / Clash Verge / FlClash / GitHub CLI 全进了列表（2026-09-12 用户反馈）。
        // 现在两层判定：
        //   ① 工具名黑名单一票否决（目录名 / exe 名）；
        //   ② 引擎与大文件特征积分 < 2 分不算游戏（git-bash / gh / dotnet 这类全是 0 分）。
        // 手动添加的路径不走该判定（用户指定 = 信任）。

        // 子串匹配即可的词（选词时避免误伤游戏名；"clash" 连 FlClash 一起覆盖）
        static readonly string[] ToolWords = new string[] {
            "clash", "mihomo", "v2ray", "xray", "singbox", "sing-box", "hiddify", "nekoray",
            "dotnet", "powershell", "pwsh", "steamcmd", "github cli", "gitbash", "git-bash",
            "discord", "telegram", "wechat", "weixin", "dingtalk", "feishu",
            "geforce", "nvidia", "radeon", "realtek", "bandizip", "winrar", "7-zip",
            "powertoys", "listary", "everything", "context menu", "rightmenu",
            "windows terminal", "openjdk", "nodejs",
            "steam++", "watt toolkit", "igamecenter", "thunder network"
        };
        // 短词必须带边界匹配："git" 会子串命中 "Digital"、"gh" 会命中一堆随便的名字
        static readonly Regex ToolShortRe = new Regex(@"(^|[\s\-\._\d])(git|gh|bash|cmd|obs|jdk|java|adb)([\s\-\._\d]|$)");

        static bool ToolNamed(string name)
        {
            if (name == null || name.Length == 0) return false;
            string n = name.ToLowerInvariant();
            if (ToolShortRe.IsMatch(n)) return true;
            foreach (var w in ToolWords) if (n.Contains(w)) return true;
            return false;
        }

        class DirProbe
        {
            public bool Unity, Unreal, Pck, DataWin, SteamApi, AntiCheat, ShippingExe, RpgMaker, RenPy, Hoyo;
        }

        // 浅层探一遍目录（深度 ≤2 层、至多 ~5000 个文件），收集"这是不是游戏"的特征。
        // 跳过 redist/directx 这类纯运行库目录，避免安装包干扰计数。
        static DirProbe ProbeDir(string dir)
        {
            var pr = new DirProbe();
            int seen = 0;
            try { WalkProbe(dir, 0, pr, ref seen); } catch { }
            return pr;
        }

        static void WalkProbe(string dir, int depth, DirProbe pr, ref int seen)
        {
            if (depth > 2 || seen > 5000) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    seen++;
                    if (seen > 5000) return;
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n == "unityplayer.dll" || n == "gameassembly.dll") pr.Unity = true;
                    // Unity 数据目录签名：<游戏名>_Data\globalgamemanagers 是 Unity 独有文件。
                    // 部分 Unity 游戏（实测原神 YuanShen.exe 411MB）把引擎静态编进主程序，
                    // 根目录根本没有 UnityPlayer.dll —— 只认那个文件名就会漏掉整款游戏（2026-09-13 反馈）。
                    else if (n == "globalgamemanagers") pr.Unity = true;
                    // 米哈游自研引擎基座（原神 / 星铁 / 绝区零 通用），与 Unity 并列的游戏性特征
                    else if (n == "mhypbase.dll") pr.Hoyo = true;
                    // 米哈游内核级反作弊驱动：既是游戏特征，也是注入风险的重要信号
                    else if (n.IndexOf("hoyokprotect") >= 0 || n.IndexOf("mhyprot") >= 0) pr.AntiCheat = true;
                    // .pak 要够大且排除 Chromium 资源包命名 —— Edge 的 resources.pak 有 38MB，
                    // 不加这两条会把 Program Files (x86)\Microsoft 误判成游戏（实测踩过）
                    else if (n.EndsWith(".pak") && n.IndexOf("_percent.pak") < 0 && n != "resources.pak")
                    {
                        try { if (new FileInfo(f).Length >= 50L * 1024 * 1024) pr.Unreal = true; } catch { }
                    }
                    else if (n.EndsWith(".pck"))
                    {
                        try { if (new FileInfo(f).Length >= 1024 * 1024) pr.Pck = true; } catch { }
                    }
                    else if (n == "data.win") pr.DataWin = true;
                    else if (n == "steam_api64.dll" || n == "steam_api.dll") pr.SteamApi = true;
                    else if (n.IndexOf("easyanticheat") >= 0 || n.IndexOf("battleye") >= 0) pr.AntiCheat = true;
                    else if (n.EndsWith("shipping.exe") && n.IndexOf("crash") < 0) pr.ShippingExe = true;
                    else if (n == "game.js" || n.EndsWith(".rpgsave")) pr.RpgMaker = true;
                    else if (n.EndsWith(".rpa")) pr.RenPy = true;
                }
                if (depth < 2)
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        string bn = Path.GetFileName(d).ToLowerInvariant();
                        if (bn == "engine") { pr.Unreal = true; continue; }
                        if (bn == "www") { pr.RpgMaker = true; continue; }        // RPG Maker MV/MZ 的 www 目录
                        if (bn == "renpy") { pr.RenPy = true; continue; }         // Ren'Py 引擎目录
                        if (bn == "redist" || bn == "redistributable" || bn == "directx" || bn == "_commonredist") continue;
                        WalkProbe(d, depth + 1, pr, ref seen);
                    }
            }
            catch { }
        }

        static bool LooksLikeGame(DlssgGame g, DirProbe pr)
        {
            // ★ 主程序体积不参与判定：Office / 迅雷 / 键盘驱动的主程序同样是几十上百 MB，
            //   单凭大小必然误收（实测 8 个工具全是这样混进来的）。只认结构性特征：
            //   游戏引擎的运行库 / 数据包 / 反作弊，是工具类目录造不出来的。
            if (g.HasFrameGen || g.HasUpscaler || g.HasFsr3) return true;   // 本工具的直接目标
            if (pr.Unity) return true;                                      // UnityPlayer.dll / GameAssembly.dll / <游戏名>_Data\globalgamemanagers
            if (pr.Hoyo) return true;                                       // 米哈游引擎基座 mhypbase.dll
            if (pr.Unreal) return true;                                     // ≥50MB 的 .pak / Engine 目录
            if (pr.Pck || pr.DataWin) return true;                          // Godot .pck / GameMaker data.win
            if (pr.RpgMaker || pr.RenPy) return true;                       // RPG Maker www/game.js、Ren'Py .rpa
            if (pr.SteamApi || pr.AntiCheat || pr.ShippingExe) return true; // steam_api / EAC·BattlEye / UE shipping
            return false;
        }

        // ==================== 自定义游戏 / 忽略清单 ====================
        // 存独立文件 games.json（不改 config.json：那边是文本手术式改键，数组不好维护）。
        //   custom  = 用户手动添加的游戏（信任，不走判定）
        //   ignored = 用户手动移除的自动扫描目录（下次扫描不再收录）

        static string GamesPath { get { return Path.Combine(BaseDir, "games.json"); } }

        public static List<string> LoadIgnoredDirs()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(GamesPath)) return list;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(GamesPath, Encoding.UTF8));
                if (root == null || !root.ContainsKey("ignored")) return list;
                var arr = root["ignored"] as System.Collections.IEnumerable;
                if (arr == null) return list;
                foreach (var o in arr) { string s = o as string; if (!string.IsNullOrEmpty(s)) list.Add(s); }
            }
            catch { }
            return list;
        }

        public static List<DlssgGame> LoadCustomGames()
        {
            var list = new List<DlssgGame>();
            try
            {
                if (!File.Exists(GamesPath)) return list;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(GamesPath, Encoding.UTF8));
                if (root == null || !root.ContainsKey("custom")) return list;
                var arr = root["custom"] as System.Collections.IEnumerable;
                if (arr == null) return list;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string exe = GetS(d, "exe");
                    if (exe.Length == 0) continue;
                    var g = CustomFromExe(exe);
                    if (g != null) list.Add(g);
                }
            }
            catch { }
            return list;
        }

        // 返回是否真的落盘。ProgramData 目录由提权进程创建，普通权限写入会被 ACL 拒绝 ——
        // 这里必须把失败如实报出去，否则 UI 会显示"已恢复"而文件其实没变（2026-09-13 踩过）。
        static bool SaveGamesStore(List<DlssgGame> customs, List<string> ignored)
        {
            try
            {
                var arr = new List<Dictionary<string, object>>();
                foreach (var g in customs)
                {
                    var d = new Dictionary<string, object>();
                    d["exe"] = g.Exe.Length > 0 ? Path.Combine(g.Dir, g.Exe) : g.Dir;
                    arr.Add(d);
                }
                var root = new Dictionary<string, object>();
                root["custom"] = arr;
                root["ignored"] = ignored;
                Directory.CreateDirectory(Path.GetDirectoryName(GamesPath));
                File.WriteAllText(GamesPath, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Program.Log("games.json 写入失败: " + ex.Message); return false; }
        }

        // 由用户选的 exe 构造条目：能力字段现探，且向上再探两级（用户可能选到 bin\x64 里的真主程序）
        static DlssgGame CustomFromExe(string exePath)
        {
            string dir = Path.GetDirectoryName(exePath);
            var g = Inspect(dir, false);
            if (g == null)
            {
                g = new DlssgGame();
                g.Dir = dir;
                g.Title = Path.GetFileName(dir);
            }
            g.Exe = Path.GetFileName(exePath);
            string up = dir;
            for (int i = 0; i < 2 && Path.GetDirectoryName(up) != up; i++)
            {
                up = Path.GetDirectoryName(up);
                var gu = Inspect(up, false);
                if (gu == null) continue;
                g.HasFrameGen |= gu.HasFrameGen;
                g.HasUpscaler |= gu.HasUpscaler;
                g.HasFsr3 |= gu.HasFsr3;
                if (gu.Installed && !g.Installed) { g.Installed = true; g.Entry = gu.Entry; }
            }
            g.Custom = true;
            return g;
        }

        public static string AddCustomGame(string exePath)
        {
            if (!File.Exists(exePath)) return "文件不存在: " + exePath;
            var customs = LoadCustomGames();
            foreach (var c in customs)
                if (Eq(c.Dir, Path.GetDirectoryName(exePath)))
                    return c.Title + " 已在列表里";
            var g = CustomFromExe(exePath);
            customs.Add(g);
            var ig = LoadIgnoredDirs();
            for (int i = ig.Count - 1; i >= 0; i--) if (Eq(ig[i], g.Dir)) ig.RemoveAt(i);  // 曾手动移除过的目录，加回来即解除忽略
            SaveGamesStore(customs, ig);
            return g.Title + "  (" + g.Exe + ")" + (g.HasFrameGen ? "，集成 DLSS 帧生成" : "");
        }

        public static string RemoveGame(DlssgGame g)
        {
            var customs = LoadCustomGames();
            DlssgGame hit = null;
            foreach (var c in customs) if (Eq(c.Dir, g.Dir) && Eq(c.Exe, g.Exe)) { hit = c; break; }
            if (hit == null) foreach (var c in customs) if (Eq(c.Dir, g.Dir)) { hit = c; break; }
            if (hit != null)
            {
                customs.Remove(hit);
                SaveGamesStore(customs, LoadIgnoredDirs());
                return hit.Title + "（自定义条目，已删除）";
            }
            var ig = LoadIgnoredDirs();
            if (!Contain(ig, g.Dir)) ig.Add(g.Dir);
            SaveGamesStore(customs, ig);
            return g.Title + "（已加入忽略清单，下次扫描不再出现；要放行点「忽略清单」按钮）";
        }

        public static List<DlssgGame> MergeScan(List<DlssgGame> scanned)
        {
            List<DlssgGame> dummy;
            return MergeScan(scanned, out dummy);
        }

        // 扫描结果合并：标注忽略清单 + 追加自定义游戏（按目录去重）
        // 被忽略的条目【不再从列表里消失】，只是打上 Ignored 标记（UI 灰显 + 可一键恢复）——
        // 隐藏状态正是"明明装了却识别不出来"的来源。ignoredHits 仍照旧回传，供日志明说。
        // 2026-09-13 用户报「新下载的原神怎么都识别不出来」：扫描其实扫到了
        // （日志「识别 11 个游戏目录」→ 列表 10 个），只因为 G:\miHoYo Launcher\games\Genshin Impact Game
        // 躺在 ignored 里就静默消失，而清单既没有 UI 入口、文件又在 ProgramData（普通权限改不了）。
        public static List<DlssgGame> MergeScan(List<DlssgGame> scanned, out List<DlssgGame> ignoredHits)
        {
            var ignored = LoadIgnoredDirs();
            var customs = LoadCustomGames();
            var res = new List<DlssgGame>();
            ignoredHits = new List<DlssgGame>();
            var seen = new List<string>();
            if (scanned != null)
                foreach (var g in scanned)
                {
                    bool skip = false;
                    foreach (var ig in ignored) if (Eq(ig, g.Dir)) { skip = true; break; }
                    g.Ignored = skip;
                    if (skip) ignoredHits.Add(g);
                    if (!Contain(seen, g.Dir)) { res.Add(g); seen.Add(g.Dir); }
                }
            foreach (var c in customs)
                if (!Contain(seen, c.Dir)) { res.Add(c); seen.Add(c.Dir); }

            // ★ 判定统一在这里算一遍（每次扫描都重算 → 后续新加的游戏自动生效）
            //   ① 每一条都重新探测能力字段（不再只探"手动添加"的条目）：
            //      之前只探 Custom 的那些，等于"信任上游探过"——于是任何绕过扫描进来的条目
            //      （游戏更新后才出现帧生成组件、目录当时临时读不到、探针/测试直接构造的条目）
            //      都会带着过期的 HasFrameGen，判定就跟着错（2026-09-16 探针实测抓到）。
            //      统一重探的代价是每次扫描多走一遍目录（约几十毫秒，扫描本来就在后台线程）。
            //   ② 算并缓存 RecCat：链接/联动（Core.AutoCategory）与「方案推荐」（BuildRec）
            //      此后读同一个值，不会出现"两个地方各判一套"。
            try
            {
                foreach (DlssgGame g in res)
                {
                    if (g == null) continue;
                    // 目录是盘根（"G:\" 这种）就不重探 —— Inspect 会去递归整块盘，代价不可接受
                    string probeDir = (g.Dir == null) ? "" : g.Dir.TrimEnd('\\', '/');
                    bool tooShallow = probeDir.Length == 0 || Path.GetDirectoryName(probeDir) == null;
                    if (!tooShallow)
                    try
                    {
                        DlssgGame p = Inspect(g.Dir, false);
                        if (p != null)
                        {
                            g.HasFrameGen = p.HasFrameGen;
                            g.HasUpscaler = p.HasUpscaler;
                            g.HasFsr3 = p.HasFsr3;
                            g.Installed = p.Installed;
                            if (p.Entry.Length > 0) g.Entry = p.Entry;
                            if (g.Exe == null || g.Exe.Length == 0) g.Exe = p.Exe;
                            if (g.Title == null || g.Title.Length == 0) g.Title = p.Title;
                        }
                    }
                    catch { }
                    try { g.RecCat = RecommendCategory(g); } catch { }
                }
            }
            catch { }

            // 库的静态引用：供 Core（联动判定）与界面读取。
            //  真正扫描出来的结果会替换它；只带自定义条目（开机那一刻）的调用不缩容。
            if (scanned != null && scanned.Count > 0) Library = res;
            else if (Library.Count == 0) Library = res;
            return res;
        }

        // 解除忽略：把目录从 games.json 的 ignored 数组里摘掉（dirs 传 null/空 = 全部清空）
        public static string UnignoreDirs(List<string> dirs)
        {
            var ig = LoadIgnoredDirs();
            if (ig.Count == 0) return "忽略清单本来就是空的";
            int n = 0;
            for (int i = ig.Count - 1; i >= 0; i--)
            {
                bool hit = (dirs == null || dirs.Count == 0);
                if (!hit) foreach (var d in dirs) if (Eq(d, ig[i])) { hit = true; break; }
                if (hit) { ig.RemoveAt(i); n++; }
            }
            if (n == 0) return "忽略清单无需改动";
            if (!SaveGamesStore(LoadCustomGames(), ig))
                return "解除忽略失败：games.json 写入被拒绝（该文件在 ProgramData，需要管理员权限）";
            return "已从忽略清单移除 " + n + " 项（重新扫描即可收录）";
        }

        // 检查单个游戏目录是否具备帧生成接入条件
        // gate=true 时做游戏性判定（扫描用）；手动添加走 gate=false（信任用户）
        public static DlssgGame Inspect(string gameDir, bool gate, bool skipGameCheck = false)
        {
            try
            {
                var exeFiles = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exeFiles.Length == 0) return null;
                string pick = null;
                long best = 0;
                string pickSoft = null;          // 启动器类 exe：仅在目录里没有其它候选时采用
                long bestSoft = 0;
                foreach (var f in exeFiles)
                {
                    try
                    {
                        long len = new FileInfo(f).Length;
                        string nm = Path.GetFileName(f).ToLowerInvariant();
                        // 永不采纳：卸载器 / 安装器 / 运行库安装器（这些出现在启动器根目录）
                        if (nm.Contains("unins") || nm.Contains("setup") || nm.Contains("crash")
                            || nm.Contains("redist") || nm.Contains("safemode")
                            || nm.Contains("bootstrap") || nm.Contains("install")) continue;
                        // 启动器降级为备选：多数游戏的真主程序在 bin\x64 等子目录里，
                        // 但像 CP2077 根目录只有 REDprelauncher.exe —— 一旦整个跳过，条目会凭空消失
                        // （2026-09-12 实测：加了无条件跳过 launcher 后 2077 从列表里没了，而它的代理还装着）
                        if (nm.Contains("launcher"))
                        {
                            if (len > bestSoft) { bestSoft = len; pickSoft = f; }
                            continue;
                        }
                        if (len > best) { best = len; pick = f; }
                    }
                    catch { }
                }
                if (pick == null) pick = pickSoft;
                if (pick == null) return null;

                // ① 工具名黑名单：目录名或主程序名命中直接排除（放在最前面，省掉后续的深度探测）
                if (gate && (ToolNamed(Path.GetFileName(gameDir)) || ToolNamed(Path.GetFileName(pick))))
                    return null;

                var g = new DlssgGame();
                g.Dir = gameDir;
                g.Exe = Path.GetFileName(pick);
                g.Title = Path.GetFileName(gameDir);

                // 递归找补帧/超分组件（深度 3，只看文件名，快）
                g.HasFrameGen = FindAnyFile(gameDir, FgDllNames, 3);
                g.HasUpscaler = FindAnyFile(gameDir, UpDllNames, 3);
                g.HasFsr3 = FindAnyFile(gameDir, FsrDllNames, 3);

                // ② UE 打包游戏的 NVIDIA 插件在 Engine\Plugins 下，不在 exe 目录旁边。
                //    鸣潮实测：Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64\
                //    {nvngx_dlssg.dll, sl.dlss_g.dll} 是它原生的 DLSS 帧生成 —— 不查这里就会把
                //    "支持 DLSS 帧生成" 误报成 "无 DLSS 帧生成"（2026-09-12 用户反馈）。
                //    必须在游戏性判定之前跑：帧生成组件本身就是判定通过的强特征。
                if (!g.HasFrameGen || !g.HasUpscaler || !g.HasFsr3)
                    ProbeUePlugins(gameDir, g);

                // ③ 引擎/大文件特征积分：不达标不算游戏。
                //    没有这层时 "C:\Program Files\xxx" 下任何一个带 exe 的工具目录都会被收录。
                // skipGameCheck：用户自定义扫描目录里的条目跳过游戏性判定
                // （RPG Maker / Ren'Py 这类引擎特征不在名单里，但用户指定了目录就是要它进库）
                if (gate && !skipGameCheck && !LooksLikeGame(g, ProbeDir(gameDir)))
                    return null;

                // 已是本工具/同类 Mod 已装状态（d3d12 复合入口优先：它能出现就说明走的是反作弊路线）
                var probe = new List<string>();
                probe.Add(D3d12Entry);
                probe.AddRange(EntryNames);
                foreach (var en in probe)
                {
                    string p = Path.Combine(gameDir, en);
                    if (File.Exists(p) && IsOurs(p)) { g.Installed = true; g.Entry = en; break; }
                }
                // 代理可能装在 EXE 的子目录（如 bin\x64）
                if (!g.Installed)
                {
                    foreach (var sub in SafeDirs(gameDir))
                    {
                        foreach (var en in probe)
                        {
                            string p = Path.Combine(sub, en);
                            if (File.Exists(p) && IsOurs(p) && File.Exists(Path.Combine(sub, IniName)))
                            { g.Installed = true; g.Entry = en; g.Dir = sub; break; }
                        }
                        if (g.Installed) break;
                    }
                }
                return g;
            }
            catch { return null; }
        }

        static List<string> SafeDirs(string dir)
        {
            var r = new List<string>();
            try { r.AddRange(Directory.GetDirectories(dir)); } catch { }
            return r;
        }

        // 帧生成 / 超分组件文件名：唯一来源，普通目录探测与 UE 插件探测共用
        // （此前两处各写一份名单，导致绝区零用的 amd_fidelityfx_framegeneration_dx12.dll 两边都漏，2026-09-13 修）
        static readonly string[] FgDllNames = new string[] { "nvngx_dlssg.dll", "sl.dlss_g.dll" };
        static readonly string[] UpDllNames = new string[] { "nvngx_dlss.dll" };
        static readonly string[] FsrDllNames = new string[] {
            "ffx_frameinterpolation_x64.dll",             // FSR3 帧生成（SDK 直链）
            "amd_fidelityfx_dx12.dll",                    // FSR3 帧生成（统一 loader）
            "amd_fidelityfx_framegeneration_dx12.dll"     // FSR3 帧生成（实测绝区零用此名）
        };

        static bool InName(string[] set, string name)
        {
            foreach (var s in set) if (s == name) return true;
            return false;
        }

        static bool FindAnyFile(string dir, string[] names, int depth)
        {
            foreach (var n in names) if (FindFile(dir, n, depth)) return true;
            return false;
        }

        static bool FindFile(string dir, string name, int depth)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return true;
                if (depth <= 0) return false;
                foreach (var sub in SafeDirs(dir))
                {
                    string bn = Path.GetFileName(sub).ToLowerInvariant();
                    if (bn == "redist" || bn == "redistributable" || bn == "directx") continue;
                    if (FindFile(sub, name, depth - 1)) return true;
                }
            }
            catch { }
            return false;
        }

        // 代理 DLL 体积判据（配合 state.json 的 SHA 做严格校验）
        public static bool IsOurs(string path)
        {
            try
            {
                string nm = Path.GetFileName(path);
                string dir = Path.GetDirectoryName(path);
                // d3d12 复合入口的两个小文件（代理 3.5 KB / 系统副本 146 KB）不落在 dlssg 运行时
                // 的体积区间里，改用「配置文件标记 + state 记录」判定
                if (Eq(nm, D3d12Entry) || Eq(nm, D3d12Orig))
                    return HasOurIni(dir) || HasOurRec(dir, nm);
                long len = new FileInfo(path).Length;
                if (len < DllSizeMin || len > DllSizeMax) return false;
                if (HasOurIni(dir)) return true;
                return HasOurRec(dir, nm);
            }
            catch { return false; }
        }

        // 该目录里的 dlssg_sm86.ini 是不是本工具写的（早期版本可能没写 managed 注释，故两个判据都认）
        static bool HasOurIni(string dir)
        {
            try
            {
                string ini = Path.Combine(dir, IniName);
                if (!File.Exists(ini)) return false;
                string txt = File.ReadAllText(ini);
                return txt.Contains("DLSSG-Tool managed") || txt.Contains("[Compatibility]");
            }
            catch { return false; }
        }

        static bool HasOurRec(string dir, string proxy)
        {
            try
            {
                foreach (var r in LoadState())
                    if (Eq(r.Dir, dir) && Eq(r.Proxy, proxy)) return true;
            }
            catch { }
            return false;
        }

        // ==================== 入口选择 ====================

        // 候选入口顺序：反作弊游戏优先 d3d12，已知游戏专用入口其次，其余按 EntryNames 顺序
        static List<string> EntryOrder(DlssgGame g)
        {
            string prefer = null;
            string title = (g.Title + " " + g.Dir).ToLowerInvariant();
            foreach (var ov in EntryOverrides)
                if (title.Contains(ov[0])) { prefer = ov[1]; break; }

            var order = new List<string>();
            // 反作弊游戏：5 个常规入口会被按名拦截，d3d12 是唯一进得去的名字
            if (PreferD3d12 && IsAntiCheat(g)) order.Add(D3d12Entry);
            if (prefer != null && prefer != D3d12Entry) order.Add(prefer);
            foreach (var e in EntryNames) if (e != prefer) order.Add(e);
            return order;
        }

        // 该入口名在当前目录里可用吗：文件不存在（可新建）或已存在但确实是本工具的代理
        static bool EntryUsable(DlssgGame g, string e)
        {
            if (Eq(e, D3d12Entry)) return D3d12Feasible(g);
            string p = Path.Combine(g.Dir, e);
            if (!File.Exists(p)) return true;
            return IsOurs(p);
        }

        // 下一个可用入口（在当前入口之后轮换）—— 给"代理被加载但从不激活"的游戏换入口用
        public static string NextEntry(DlssgGame g)
        {
            if (g == null) return null;
            var order = EntryOrder(g);
            int start = order.IndexOf(g.Entry == null ? "" : g.Entry);
            for (int k = 1; k <= order.Count; k++)
            {
                string e = order[((start < 0 ? -1 : start) + k + order.Count) % order.Count];
                if (!EntryUsable(g, e)) continue;
                if (!File.Exists(SourceFile(e))) continue;   // 运行时/源文件没下载的跳过
                return e;
            }
            return null;
        }

        // 说明该游戏为何要用专用入口（供界面提示）
        public static string EntryHint(DlssgGame g)
        {
            if (g == null) return "";
            if (PreferD3d12 && IsAntiCheat(g))
                return "该游戏带内核反作弊，常规入口（version/winmm…）会被按文件名拦截 → 自动改用 d3d12.dll 入口";
            string title = (g.Title + " " + g.Dir).ToLowerInvariant();
            foreach (var ov in EntryOverrides)
                if (title.Contains(ov[0]))
                    return "该游戏对 version.dll 只加载不调用（代理不会激活），已自动改用 " + ov[1];
            return "";
        }

        // ==================== 安装 / 卸载 ====================

        // 启动器根目录判定：目录里挂着 games 子目录，且根下有 launcher 类 exe
        // —— 这是"装游戏的容器"，不是游戏本体。代理装进这里会同时作用于容器下的所有游戏
        // （实测 2026-09-12：G:\miHoYo Launcher 被注入，该目录含原神/绝区零/星铁，且都是内核反作弊，风险面被放大）。
        //  2026-09-18 改成**按目录**判（原来是吃 DlssgGame.Exe），因为护栏搬进了 Plan.SwitchTo
        //  —— 那条路只拿到目录字符串。判据要两个条件同时成立，避免把普通游戏目录误判成容器：
        //  G:\Wuthering Waves 也有 launcher.exe，但它没有 games\ 子目录。
        public static bool IsLauncherDir(string dir)
        {
            if (dir == null || dir.Length == 0) return false;
            try
            {
                if (!Directory.Exists(Path.Combine(dir, "games"))) return false;
                foreach (string f in Directory.GetFiles(dir, "*.exe"))
                    if (Path.GetFileName(f).ToLowerInvariant().IndexOf("launcher") >= 0) return true;
                return false;
            }
            catch { return false; }
        }

        // ==================== d3d12 复合入口（v2.1.0） ====================

        // 反作弊游戏判定：唯一来源（EntryOrder / EntryHint / InjectionRiskNote 共用同一份名单）。
        // 教训来自 2026-09-13 的 FSR 组件名事故：同一份清单写在两处，必然漏一处。
        public static bool IsAntiCheat(DlssgGame g)
        {
            if (g == null) return false;
            string hay = ((g.Title == null ? "" : g.Title) + " " + (g.Exe == null ? "" : g.Exe) + " "
                          + (g.Dir == null ? "" : g.Dir)).ToLowerInvariant();
            foreach (var w in GachaWords) if (hay.Contains(w)) return true;
            return false;
        }

        // 该游戏能不能走 d3d12 路线：① 本机有系统 d3d12.dll（转发目标）
        // ② 载体 dinput8.dll 已下载 ③ 三个目标文件名没被非本工具的文件占用
        public static bool D3d12Feasible(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Dir.Length == 0) return false;
            try
            {
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "d3d12.dll"))) return false;
                if (!File.Exists(Path.Combine(AltDir, D3d12Carrier))) return false;
                string[] mine = new string[] { D3d12Entry, D3d12Orig, D3d12Carrier };
                foreach (var fn in mine)
                {
                    string p = Path.Combine(g.Dir, fn);
                    if (!File.Exists(p)) continue;
                    if (!IsOurs(p)) return false;   // 目录里已有别人的同名文件 → 不硬碰
                }
                return true;
            }
            catch { return false; }
        }

        // d3d12 入口不可用时给出具体原因（界面用）
        public static string D3d12Hint()
        {
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "d3d12.dll")))
                return "本机缺少系统 d3d12.dll（DirectX 12 运行库）→ d3d12 入口不可用";
            if (!File.Exists(Path.Combine(AltDir, D3d12Carrier)))
                return "载体 " + D3d12Carrier + " 未下载 → 点「下载运行时」后才能用 d3d12 入口";
            return "";
        }

        // 安装 d3d12 复合入口：代理（内嵌字节）+ 系统原件副本 + 载体 + 配置
        static string InstallD3d12(DlssgGame g)
        {
            string dir = g.Dir;
            string sys = Path.Combine(Environment.SystemDirectory, "d3d12.dll");
            if (!File.Exists(sys)) return "本机缺少系统 d3d12.dll（DirectX 12 运行库），无法建立转发目标";

            string carrier = Path.Combine(AltDir, D3d12Carrier);
            if (!File.Exists(carrier)) return "缺少载体 " + D3d12Carrier + "，请先点「下载运行时」";

            byte[] proxy;
            try { proxy = D3d12ProxyBlob.Bytes(); }
            catch (Exception ex) { return "内嵌代理解包失败：" + ex.Message; }
            if (proxy.Length != D3d12ProxyBlob.Size)
                return "内嵌代理体积异常（" + proxy.Length + " 字节，应为 " + D3d12ProxyBlob.Size + "）";

            // 目标名被非本工具的文件占用 → 先整份备份（dinput8.dll 尤其常见：有些游戏自带真品）
            string backup = "";
            string[] targets = new string[] { D3d12Entry, D3d12Orig, D3d12Carrier };
            var toBackup = new List<string>();
            foreach (var fn in targets)
            {
                string p = Path.Combine(dir, fn);
                if (File.Exists(p) && !IsOurs(p)) toBackup.Add(fn);
            }
            if (toBackup.Count > 0)
            {
                try
                {
                    string bdir = Path.Combine(BackupDir, Safe(g.Title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(bdir);
                    foreach (var fn in toBackup) File.Copy(Path.Combine(dir, fn), Path.Combine(bdir, fn), true);
                    backup = bdir;
                    foreach (var fn in toBackup) File.Delete(Path.Combine(dir, fn));
                }
                catch (Exception ex) { return "备份原文件失败：" + ex.Message; }
            }

            try
            {
                File.WriteAllBytes(Path.Combine(dir, D3d12Entry), proxy);
                File.Copy(sys, Path.Combine(dir, D3d12Orig), true);
                File.Copy(carrier, Path.Combine(dir, D3d12Carrier), true);
                File.WriteAllText(Path.Combine(dir, IniName), BuildIni(), new UTF8Encoding(false));
            }
            catch (Exception ex) { return "写入 d3d12 入口失败：" + ex.Message; }

            // 假成功校验：不确认"导入链真的闭合"就不算装好 —— 三件套缺一件，游戏一启动就崩
            string v = VerifyD3d12(dir);
            if (v.Length > 0) return "d3d12 入口校验未通过：" + v + "（请点「关闭选中」清理后重试）";

            string sha = "";
            try { sha = Sha256(Path.Combine(dir, D3d12Entry)); } catch { }
            SetRec(dir, D3d12Entry, sha, backup);
            g.Installed = true; g.Entry = D3d12Entry;
            return "已开启（d3d12 入口：代理 + 系统原件 + 载体三件套）" + (backup.Length > 0 ? "；原同名文件已备份" : "");
        }

        // 校验 d3d12 三件套是否真的可用 —— 空串 = 通过，非空 = 具体问题
        public static string VerifyD3d12(string dir)
        {
            try
            {
                string p = Path.Combine(dir, D3d12Entry);
                if (!File.Exists(p)) return D3d12Entry + " 不存在";
                long len = new FileInfo(p).Length;
                if (len != D3d12ProxyBlob.Size) return D3d12Entry + " 体积 " + len + "，应为 " + D3d12ProxyBlob.Size;

                // 最关键的一条：代理的导入表必须真的指向载体，否则三件套不闭合
                var imports = PeImports(p);
                if (imports.Count == 0) return D3d12Entry + " 导入表解析为空（文件损坏）";
                bool hasCarrier = false;
                foreach (var im in imports) if (Eq(im, D3d12Carrier)) { hasCarrier = true; break; }
                if (!hasCarrier) return D3d12Entry + " 的导入表里没有 " + D3d12Carrier + "（代理与载体没接上）";

                if (!File.Exists(Path.Combine(dir, D3d12Orig))) return D3d12Orig + " 不存在（转发目标缺失，游戏会崩溃）";
                string car = Path.Combine(dir, D3d12Carrier);
                if (!File.Exists(car)) return D3d12Carrier + " 不存在（载体缺失，帧生成运行时不会被拉起）";
                long cl = new FileInfo(car).Length;
                if (cl < DllSizeMin || cl > DllSizeMax)
                    return D3d12Carrier + " 体积 " + cl + " 不在 dlssg 运行时区间，可能不是真品";
                if (!File.Exists(Path.Combine(dir, IniName))) return IniName + " 不存在";
                return "";
            }
            catch (Exception ex) { return "校验异常：" + ex.Message; }
        }

        // 读 PE 导入表的 DLL 名列表（不依赖外部工具：直接解析 IMAGE_IMPORT_DESCRIPTOR）
        public static List<string> PeImports(string path)
        {
            var r = new List<string>();
            try
            {
                byte[] d = File.ReadAllBytes(path);
                int pe = BitConverter.ToInt32(d, 0x3C);
                int nsec = BitConverter.ToInt16(d, pe + 6);
                int optsz = BitConverter.ToInt16(d, pe + 20);
                int opt = pe + 24;
                int magic = BitConverter.ToInt16(d, opt);
                int ddoff = opt + (magic == 0x20B ? 112 : 96);
                int impRva = BitConverter.ToInt32(d, ddoff + 8);
                if (impRva == 0) return r;

                var secs = new List<int[]>();
                int so = opt + optsz;
                for (int i = 0; i < nsec; i++)
                {
                    int s = so + 40 * i;
                    int vsz = BitConverter.ToInt32(d, s + 8), va = BitConverter.ToInt32(d, s + 12);
                    int rsz = BitConverter.ToInt32(d, s + 16), ra = BitConverter.ToInt32(d, s + 20);
                    secs.Add(new int[] { va, vsz, ra, rsz });
                }
                int o = Rva2Off(secs, impRva);
                if (o < 0) return r;
                while (o + 20 <= d.Length)
                {
                    int dn = BitConverter.ToInt32(d, o + 12);
                    int iat = BitConverter.ToInt32(d, o + 16);
                    if (dn == 0 && iat == 0) break;
                    int dof = Rva2Off(secs, dn);
                    if (dof < 0) break;
                    int end = dof;
                    while (end < d.Length && d[end] != 0) end++;
                    r.Add(Encoding.ASCII.GetString(d, dof, end - dof));
                    o += 20;
                }
            }
            catch { }
            return r;
        }

        static int Rva2Off(List<int[]> secs, int rva)
        {
            foreach (var s in secs)
            {
                int span = Math.Max(s[1], s[3]);
                if (rva >= s[0] && rva < s[0] + span) return s[2] + (rva - s[0]);
            }
            return -1;
        }

        // 反作弊把被拦的代理改名隔离（实测 HoYoKProtect 产出 version.dll.1271877294）——
        // 这些文件不再被加载，却白占 15 MB，且让用户误判"还装着"。
        public static List<string> ScanResidue(string dir)
        {
            var r = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    string nm = Path.GetFileName(f);
                    var m = Regex.Match(nm, @"^(.+\.dll)\.(\d+)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string baseDll = m.Groups[1].Value;
                    bool known = Eq(baseDll, D3d12Entry);
                    if (!known)
                        foreach (var en in EntryNames) if (Eq(en, baseDll)) { known = true; break; }
                    if (!known) continue;
                    try
                    {
                        long len = new FileInfo(f).Length;
                        if (len < DllSizeMin || len > DllSizeMax) continue;   // 体积不符，不是本工具那份，别动
                    }
                    catch { continue; }
                    r.Add(f);
                }
            }
            catch { }
            return r;
        }

        // ⚠ 2026-09-18 删除：这里原来是 `Install(DlssgGame, bool ensureSource)` —— 一套**自己做入口
        //   选择、自己抄文件、零互斥**的部署实现，与 Plan.SwitchTo 并存。谁被调用取决于界面点了
        //   哪个按钮（「开启帧生成」调它、「切换到本方案」调 SwitchTo），于是出现"装了两套、
        //   两个插件打架"→ 绝区零 (0,11008)。它当时已经零调用点，直接删掉，不留第二套部署路径；
        //   "启动器根目录"那条护栏随之上移到 Plan.SwitchTo（判据见 IsLauncherDir）。
        //   要装/换方案，唯一入口是 Plan.SwitchTo。

        // 按指定入口安装（跳过自动选择）。SwitchEntry 用它轮换入口。
        static string InstallAt(DlssgGame g, string chosen)
        {
            bool isD3d12 = Eq(chosen, D3d12Entry);
            bool known = isD3d12;
            foreach (var e in EntryNames) if (e == chosen) { known = true; break; }
            if (!known) return "未知入口名：" + chosen;

            string src = isD3d12 ? "" : SourceFile(chosen);
            if (!isD3d12 && !File.Exists(src)) return "缺少源文件 " + chosen + "，请先点「下载运行时」";

            // 互斥前置（2026-09-18）：这条路径同样是"往游戏目录写入口 DLL"，必须先把别家方案整套停放。
            //  以前 InstallAt 不做互斥、界面又从「开启帧生成」直接调它 → 两套代理同时进进程
            //  （绝区零 (0,11008) 的成因之一）。校验全部走完再动手停放，避免"停了却没装成"。
            //  keep = 要装进去的那份文件属于哪套方案；d3d12 复合入口是本工具内嵌代理（legacy 线）。
            string own = isD3d12 ? Plan.Legacy : Plan.OwnerOf(src);
            List<string> failed = new List<string>();
            // ⚠ ParkOthers 是**返回**停放清单的（自己 new 一个 List 传出去的是 failed）。
            //   漏接返回值 → 回执永远是空的，用户看不到"已经帮你把另一套关了"（本行踩过一次）。
            List<string> parked = Plan.ParkOthers(g.Dir, XeMfg.Detect(g.Dir), own, failed);

            // d3d12 是复合入口（代理 + 系统原件 + 载体三件套），走独立安装路径
            if (isD3d12) return InstallD3d12(g) + Plan.Note(parked, failed);

            string tgt = Path.Combine(g.Dir, chosen);
            string backup = "";

            // 目标位置被非本工具的 DLL 占用 → 先整份备份再替换
            if (File.Exists(tgt) && !IsOurs(tgt))
            {
                try
                {
                    string bdir = Path.Combine(BackupDir, Safe(g.Title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(bdir);
                    File.Copy(tgt, Path.Combine(bdir, chosen), true);
                    backup = bdir;
                    File.Delete(tgt);
                }
                catch (Exception ex) { return "备份原文件失败：" + ex.Message; }
            }

            try { File.Copy(src, tgt, true); }
            catch (Exception ex) { return "复制失败：" + ex.Message; }

            try { File.WriteAllText(Path.Combine(g.Dir, IniName), BuildIni(), new UTF8Encoding(false)); }
            catch (Exception ex) { return "写入配置失败：" + ex.Message; }

            // INI 的年代必须跟刚抄进去的那份代理对上：旧版代理只认 1..3（BuildIniFor 硬夹到 3），
            //  0.3.x 要完整格式（[General] Enabled=1 / [Runtime] Mode=Bundled / [Compatibility] Preset…）
            //  才能拿到 MaxGeneratedFrames=5 → 6X。以前这条路径一律写 261 B 旧模板，
            //  却抄进去一份 0.3.x 的代理 → 上限白丢两档（2026-09-18 记的"261 B 模板"就是这条）。
            if (own == Plan.D030) Dlssg030.EnsureFactoryIni(g.Dir);

            string sha = "";
            try { sha = Sha256(tgt); } catch { }
            SetRec(g.Dir, chosen, sha, backup);

            g.Installed = true; g.Entry = chosen;
            return "已开启（入口 " + chosen + "）" + (backup.Length > 0 ? "；原同名文件已备份" : "")
                 + Plan.Note(parked, failed);
        }

        // 换入口重试：卸掉当前入口 → 改用下一个可用入口重装。
        // 用于「代理 DLL 已进进程但从不激活」—— 游戏只加载、不调用该入口时代理永不初始化，
        // 游戏内自然只剩它自带的 FSR 帧生成（CP2077 对 version.dll 正是如此，winmm 才可用）。
        // 保留日志目录，便于对比换入口前后的诊断结果。
        public static string SwitchEntry(DlssgGame g)
        {
            if (g == null) return "无效的游戏条目";
            if (!Directory.Exists(g.Dir)) return "目录不存在：" + g.Dir;
            if (IsDirBusy(g.Dir)) return "游戏正在运行，请先完全退出游戏";

            string next = NextEntry(g);
            if (next == null)
                return "没有其它可用入口（运行时未下载，或 5 个入口名都被非本工具的 DLL 占用）";

            string old = g.Entry == null ? "" : g.Entry;
            // Uninstall 的回执不能扔：它会说出"方案 A 已被自动停放"这件事 ——
            //  这条以前被丢掉，用户只看到"入口换了"，不知道目录里另一套被动过（2026-09-18）。
            string ur = Uninstall(g, true);
            string r = InstallAt(g, next);
            string tail = (ur != null && ur.IndexOf("已停放") >= 0) ? "\n  " + ur.Replace("\n", "\n  ") : "";
            return "入口 " + (old.Length > 0 ? old : "（未安装）") + " → " + next + "：" + r + tail;
        }

        public static string Uninstall(DlssgGame g) { return Uninstall(g, false); }

        public static string Uninstall(DlssgGame g, bool keepLogs)
        {
            if (g == null) return "无效的游戏条目";
            string dir = g.Dir;

            // 护栏：这两个游戏当前若装的是「方案 A · OptiScaler 注入」，入口文件（d3d12.dll / dxgi.dll）
            // 与常规 dlssg 入口同名。混着删只会删掉其中一两个文件，留下 fakenvapi /
            // libxess / OptiScaler 子目录 → 装了一半，游戏可能起不来。
            // 所以这里先把方案 A **整套停放**（只改名），再照常清理旧版残留 —— 原实现是直接
            // return 让用户"先去点停放再回来"，2026-09-18 用户反馈那一步很多余。
            string parkNote = "";
            if (XeMfg.IsInstalled(g.Dir))
            {
                string pr = Plan.SwitchTo(g.Dir, Plan.None);
                if (Plan.Current(g.Dir) != Plan.None)
                    return "关闭失败：方案 A 没能停放（多半是游戏或启动器还在运行）。\n" + pr;
                parkNote = "已停放方案 A（只改名，去方案 A 区块点「切换到本方案」即可切回）";
            }

            var recs = LoadState();
            DlssgRec rec = null;
            foreach (var r in recs) if (Eq(r.Dir, dir)) { rec = r; break; }

            var actions = new List<string>();
            bool removed = false;
            if (parkNote.Length > 0) actions.Add(parkNote);

            // 0) d3d12 复合入口：代理 + 系统原件副本
            //    （载体 dinput8.dll 由下面的入口循环处理，它同样落在 dlssg 运行时体积区间里）
            bool d3d12Rec = (rec != null && Eq(rec.Proxy, D3d12Entry));
            string d3p = Path.Combine(dir, D3d12Entry);
            if (File.Exists(d3p) && (d3d12Rec || IsOurs(d3p)))
            {
                try { File.Delete(d3p); removed = true; actions.Add("已移出 " + D3d12Entry); }
                catch (Exception ex) { actions.Add("删除 " + D3d12Entry + " 失败：" + ex.Message); }
            }
            string d3o = Path.Combine(dir, D3d12Orig);
            if (File.Exists(d3o) && d3d12Rec)
            {
                // 只删「我们拷进去的那份系统副本」：长度得和本机 System32\d3d12.dll 对得上
                bool sysMatch = false;
                try
                {
                    string sys = Path.Combine(Environment.SystemDirectory, "d3d12.dll");
                    if (File.Exists(sys)) sysMatch = (new FileInfo(sys).Length == new FileInfo(d3o).Length);
                }
                catch { }
                if (sysMatch)
                {
                    try { File.Delete(d3o); removed = true; actions.Add("已移出 " + D3d12Orig); }
                    catch (Exception ex) { actions.Add("删除 " + D3d12Orig + " 失败：" + ex.Message); }
                }
                else actions.Add("保留 " + D3d12Orig + "（与本机系统 d3d12.dll 不一致，判定为你自己的文件）");
            }

            // 1) 移出代理 DLL（三重校验：入口名白名单 + 体积 + SHA）
            foreach (var en in EntryNames)
            {
                string p = Path.Combine(dir, en);
                if (!File.Exists(p)) continue;
                if (!IsOurs(p)) { actions.Add("跳过非本工具文件 " + en); continue; }
                bool shaOk = true;
                if (rec != null && Eq(rec.Proxy, en) && rec.Sha256.Length > 0)
                {
                    try { if (!Eq(Sha256(p), rec.Sha256)) shaOk = false; } catch { }
                }
                if (!shaOk) { actions.Add("SHA 不符，保留 " + en + "（可能已更新或被覆盖）"); continue; }
                try { File.Delete(p); removed = true; actions.Add("已移出 " + en); }
                catch (Exception ex) { actions.Add("删除 " + en + " 失败：" + ex.Message); }
            }

            // 2) 删配置（必须含本工具标记或记录在案）
            string ini = Path.Combine(dir, IniName);
            if (File.Exists(ini))
            {
                bool mine = rec != null;
                try { if (File.ReadAllText(ini).Contains("DLSSG-Tool managed")) mine = true; } catch { }
                if (mine) { try { File.Delete(ini); actions.Add("已移除 " + IniName); } catch { } }
                else actions.Add("配置非本工具生成，保留");
            }

            // 3) 清日志目录（keepLogs=true 时保留 —— 换入口前后要能对比诊断结果）
            string lg = Path.Combine(dir, LogFolderName);
            if (Directory.Exists(lg) && !keepLogs)
            {
                try { Directory.Delete(lg, true); actions.Add("已清理日志目录"); } catch { }
            }

            // 4) 还原备份
            if (rec != null && rec.Backup != null && rec.Backup.Length > 0 && Directory.Exists(rec.Backup))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(rec.Backup))
                    {
                        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                        actions.Add("已还原 " + Path.GetFileName(f));
                        removed = true;
                    }
                }
                catch { }
            }

            // 5) 反作弊隔离残留：被改名成 xxx.dll.<数字> 的代理（HoYoKProtect 实测就是这么干的），
            //    不再被加载、但会在游戏目录里白占 15 MB，并让用户误判"还装着"。
            foreach (var rp in ScanResidue(dir))
            {
                try { File.Delete(rp); removed = true; actions.Add("已清理残留 " + Path.GetFileName(rp)); }
                catch { }
            }

            DelRec(dir);
            g.Installed = false; g.Entry = "";
            if (!removed && actions.Count == 0) return "未发现本工具安装痕迹";
            return string.Join("；", actions.ToArray());
        }

        // ==================== 运行状态诊断 ====================

        public class LogVerdict
        {
            public bool HasLogDir;
            public bool Activated;          // 日志里出现 runtime_redirect / feature_created
            public bool FrameGenRunning;    // 出现 evaluate 且计数增长
            public string LastEvent = "";
            public int Evaluations;
            public int ActualSm;
            public string LastLog = "";
            public string Message = "";
        }

        // 读 Mod 日志判断代理是否真正激活（本模块最有价值的诊断能力）
        public static LogVerdict CheckLogs(string gameExeDir)
        {
            var v = new LogVerdict();
            try
            {
                string lg = Path.Combine(gameExeDir, LogFolderName);
                if (!Directory.Exists(lg)) { v.Message = "尚无日志目录（代理未激活或从未运行过）"; return v; }
                v.HasLogDir = true;
                var files = new List<string>(Directory.GetFiles(lg, "*.jsonl", SearchOption.AllDirectories));
                if (files.Count == 0) { v.Message = "日志目录存在但没有日志文件"; return v; }
                files.Sort();
                string newest = files[files.Count - 1];
                v.LastLog = Path.GetFileName(newest);
                long len = new FileInfo(newest).Length;
                if (len > 4000000) {   // 超大日志只读尾部，避免卡界面
                    using (var fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Seek(len - 4000000, SeekOrigin.Begin);
                        int tail = (int)(fs.Length - fs.Position);
                        var buf = new byte[tail];
                        fs.Read(buf, 0, tail);
                        v.LastEvent = Encoding.UTF8.GetString(buf);
                    }
                }
                else v.LastEvent = File.ReadAllText(newest, Encoding.UTF8);
                foreach (var line in v.LastEvent.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length == 0) continue;
                    if (s.Contains("\"runtime_redirect\"") || s.Contains("\"feature_created\"") || s.Contains("\"model_ready\"")) v.Activated = true;
                    if (s.Contains("\"actual_sm\""))
                    {
                        var m = Regex.Match(s, "\"actual_sm\"\\s*:\\s*(\\d+)");
                        if (m.Success) { int t; if (int.TryParse(m.Groups[1].Value, out t)) v.ActualSm = t; }
                    }
                    if (s.Contains("\"evaluate\""))
                    {
                        var m = Regex.Match(s, "\"evaluations\"\\s*:\\s*(\\d+)");
                        if (m.Success) { int t; if (int.TryParse(m.Groups[1].Value, out t)) v.Evaluations = t; }
                        v.FrameGenRunning = true;
                    }
                }
                if (v.FrameGenRunning && v.Evaluations > 0)
                    v.Message = "帧生成正在运行（已补帧 " + v.Evaluations + " 次" + (v.ActualSm > 0 ? "，内核 SM" + v.ActualSm : "") + "）";
                else if (v.Activated) v.Message = "代理已激活，等待游戏请求帧生成";
                else v.Message = "日志存在但没有激活事件（异常）";
            }
            catch (Exception ex) { v.Message = "读取日志失败：" + ex.Message; }
            return v;
        }

        // 代理 DLL 是否已进入游戏进程（配合 CheckLogs 判定"加载未激活"）
        public static string ProcessEntryState(DlssgGame g)
        {
            try
            {
                var ps = Process.GetProcesses();
                foreach (var p in ps)
                {
                    try
                    {
                        if (p.ProcessName.ToLowerInvariant() + ".exe" != g.Exe.ToLowerInvariant()) continue;
                        foreach (ProcessModule m in p.Modules)
                        {
                            if (m.FileName == null) continue;
                            if (Eq(m.FileName, Path.Combine(g.Dir, g.Entry.Length > 0 ? g.Entry : "version.dll")))
                                return "代理已在游戏进程中（已加载）";
                        }
                        return "游戏在运行，但代理未加载";
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return "游戏未运行";
        }

        // 前置条件：HAGS 是 DLSS 帧生成的硬性要求
        public static int HagsState()
        {
            try
            {
                var v = Program.ReadReg(@"HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
                if (v == null) return 0;
                return Convert.ToInt32(v);
            }
            catch { return -1; }
        }

        public static string HagsText()
        {
            int s = HagsState();
            if (s == 2) return "已开启";
            if (s == 1) return "已开启(1)";
            if (s == 0) return "未开启";
            return "读取失败";
        }

        // ==================== INI 参数 ====================

        static string BuildIni() { return BuildIniFor(Program.Cfg.DlssgMaxFrames, Program.Cfg.DlssgLogLevel); }

        // 旧版代理（14.9 MB，内嵌 310.1 运行库）只认 MaxGeneratedFrames = 1..3。
        //  写 5 会让它抛 "Invalid INI integer" 并**整份配置作废** ——
        //  2026-09-16 实测：这一步把 FH6 卡死在加载界面（native_*.jsonl 只有一行 configuration_error）。
        //  所以上限一律按"代理年代"夹紧，0.3.x（310.9）才允许 5。
        public static string BuildIniFor(int maxFrames, int logLevel)
        {
            if (maxFrames < 1) maxFrames = 1;
            if (maxFrames > 3) maxFrames = 3;
            var sb = new StringBuilder();
            sb.AppendLine("; dlssg_sm86.ini - Fluxion managed");
            sb.AppendLine("; 修改后需重启游戏生效。删掉本文件也可以（使用默认值）。");
            sb.AppendLine("[Compatibility]");
            sb.AppendLine("Router=" + (Program.Cfg.DlssgRouter.Length > 0 ? Program.Cfg.DlssgRouter : "SM86"));
            sb.AppendLine("KernelImage=" + (Program.Cfg.DlssgKernel.Length > 0 ? Program.Cfg.DlssgKernel : "PTX"));
            sb.AppendLine("HardwareBilinear=" + Program.Cfg.DlssgBilinear);
            sb.AppendLine();
            sb.AppendLine("[FrameGeneration]");
            sb.AppendLine("MaxGeneratedFrames=" + maxFrames);
            sb.AppendLine();
            sb.AppendLine("[Logging]");
            sb.AppendLine("Level=" + logLevel);
            return sb.ToString();
        }

        // ==================== 源文件下载 ====================

        //  判据不能只看"文件在不在"。这个源位属于旧版 0.2.x 路线（配套 altnative\），
        //  体积应当在 Catalog.SourceWin（内置默认 15,500,000~15,800,000）里；
        //  2026-09-21 实测这里躺着一份 29,975,840 B 的 0.3.2 代理 —— 那是 pack 路线的件，
        //  部署 0.3.x 时根本不读这个目录，但 File.Exists 为真，于是界面长期误报"已下载"。
        public static bool SourceReady()
        {
            long n;
            return SourceSize(out n) && Catalog.IsSourceSize(n);
        }

        // 源件在不在 + 多大。拆出来是为了"不在"与"在但不对"能分开说。
        public static bool SourceSize(out long bytes)
        {
            bytes = 0;
            try
            {
                string p = SourceFile("version.dll");
                if (!File.Exists(p)) return false;
                bytes = new FileInfo(p).Length;
                return true;
            }
            catch { bytes = 0; return false; }
        }

        // 一行说明：只在"文件在、体积却不是 0.2.x 那份"时才有内容（其余情况返回空串）。
        public static string SourceHint()
        {
            long n;
            if (!SourceSize(out n)) return "";
            if (Catalog.IsSourceSize(n)) return "";
            return "source 里那份 version.dll = " + n.ToString("N0")
                 + " B，不在旧版 0.2.x 的体积窗口（" + Catalog.SourceWin.Min + "~" + Catalog.SourceWin.Max
                 + "）—— 那是 0.3.x 的件，放错了源位；0.2.x 路线请点「下载运行时」重新取一份";
        }

        public static string SourceFile(string proxy)
        {
            if (Eq(proxy, "version.dll")) return Path.Combine(SourceDir, "version.dll");
            // d3d12 入口的"源文件"就是它的载体：代理和系统副本都不需要下载
            // （代理是本工具内嵌的字节，副本从本机 System32 现拷）
            if (Eq(proxy, D3d12Entry)) return Path.Combine(AltDir, D3d12Carrier);
            return Path.Combine(AltDir, proxy);
        }

        public static string Download(string proxy, Action<string> progress)
        {
            try
            {
                Directory.CreateDirectory(SourceDir);
                Directory.CreateDirectory(AltDir);
                string repo = Program.Cfg.DlssgRepo.Length > 0
                    ? Program.Cfg.DlssgRepo
                    : "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main";
                string rel = Eq(proxy, "version.dll") ? "version.dll" : "altnative/" + proxy;
                string url = repo.TrimEnd('/') + "/" + rel;
                string outFile = SourceFile(proxy);
                if (progress != null) progress("下载 " + proxy + " ...");

                // GitHub 强制 TLS 1.2+。csc.exe 编译时没有 TargetFrameworkAttribute，
                // exe 运行在 .NET 4.0 兼容模式下：ServicePointManager 默认只开
                // SSL3/TLS1.0，直接连 https://raw.githubusercontent.com 必报
                // "未能创建 SSL/TLS 安全通道"。3072=Tls12, 12288=Tls13（系统不支持则忽略）。
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }

                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Fluxion");
                    if (Program.Cfg.DlssgUseProxy && Program.Cfg.DlssgProxy.Length > 0)
                    {
                        wc.Proxy = new WebProxy(Program.Cfg.DlssgProxy);
                        if (progress != null) progress("经代理 " + Program.Cfg.DlssgProxy);
                    }
                    byte[] data = wc.DownloadData(url);
                    if (data == null || data.Length < DllSizeMin)
                        return "下载内容异常（" + (data == null ? 0 : data.Length) + " 字节），请检查网络或代理";
                    File.WriteAllBytes(outFile, data);
                }
                long len = new FileInfo(outFile).Length;
                return "OK " + Math.Round(len / 1048576.0, 2) + " MB  SHA256 " + Sha256(outFile);
            }
            catch (Exception ex)
            {
                return "下载失败：" + ex.Message + "（国内网络建议开启 config.json 里的代理）";
            }
        }

        // ==================== 工具函数 ====================

        // 入口 DLL 是不是正被别的进程占着（实测：试着独占打开一次，比看进程列表准）。
        //  Plan.SwitchTo 也用它 —— 占用时改名必失败，宁可在动手前就说清楚。
        public static bool IsDirBusy(string dir)
        {
            try
            {
                // d3d12 组的两个小文件也要试锁：游戏加载 d3d12.dll 后覆盖必然失败
                var names = new List<string>(EntryNames);
                names.Add(D3d12Entry);
                names.Add(D3d12Orig);
                foreach (var en in names)
                {
                    string p = Path.Combine(dir, en);
                    if (!File.Exists(p)) continue;
                    try { using (var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                    catch { return true; }
                }
            }
            catch { }
            return false;
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            string r = sb.ToString();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }

        static bool Contain(List<string> l, string s)
        {
            foreach (var x in l) if (Eq(x, s)) return true;
            return false;
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }
    }

    // ==========================================================================
    //  d3d12 复合入口的代理字节（v2.1.0）
    //  --------------------------------------------------------------------------
    //  这是一段 3,584 字节的 PE 文件，由项目内 tools\make_d3d12_proxy.py 逐字节构造
    //  （纯 PE，不含任何机器码）：
    //    · 导出表把系统 d3d12.dll 的全部 18 个函数转发到同目录 d3d12_orig.dll 的同名函数
    //    · 导入表只依赖 dinput8.dll!DirectInput8Create
    //  Windows 加载本代理时必须先解析这个导入，于是把同目录的 dlssg dinput8.dll（真正的
    //  帧生成运行时）自动拉进进程 —— 反作弊拦不住 d3d12.dll 这个名字，而它会自己把被拦的
    //  那份运行时拽进来。原因与实证见 docs\dlssg_for_sm86_研究笔记.md 第 12 节。
    //  ⚠ 不要手改下面的字节：先改 tools\make_d3d12_proxy.py，重跑 tools\make_deploy_pkg.py
    //    自检通过后，再用产出的 d3d12.dll 重新编码本常量。
    // ==========================================================================
    // 游戏图标提取（列表显示用）：读 exe 关联图标——解决"英文名看不懂是哪款"
    // 失败返回 null，调用方用占位；带缓存由调用方负责
    // 图标 / 封面取图
    //   ★ 2026-09-13 用户反馈"封面好糊"的根因：原来用 Icon.ExtractAssociatedIcon()，
    //     它只返回 32×32（系统小图标尺寸），拉到 132 的卡片上就是一团糊。
    //     现在改成 PrivateExtractIcons 主动要 256×256（现代游戏 exe 里都带），
    //     并在多个候选尺寸里取【实际像素最大】的那张，最后用高质量缩放落到卡片尺寸。
    // ============================================================================
    //  帧生成生效自检
    //  ---------------------------------------------------------------------------
    //  为什么需要：最常被问的一句是"帧生成到底生效没"。以前只能进游戏看菜单，或者手动点
    //  「运行诊断」再自己解读。其实代理会把每一帧的生成情况写进 jsonl —— 读它就有确定答案。
    //
    //  日志格式（2026-09-16 实测，UTF-8 + CRLF）：
    //    {"event":"configuration", …, "max_generated":3, "router":86, "runtime":"native_pipeline"}
    //        ↑ 每次加载写一次，给出"最多能生成几帧"（3 → 4X）
    //    {"event":"evaluate", "generated_count":1, "evaluations":1, …}
    //        ↑ 每生成一帧写一行，generated_count = 本次生成数；取最大值即"实际跑到几倍"
    //  文件名：<游戏目录>\dlssg_sm86\logs\native_<pid>.jsonl    旧版 dlssg_for_sm86
    //          <游戏目录>\dlssg_sm86\logs\backend_<pid>.jsonl   0.3.x（无事件时不写内容 → 0 字节）
    //          <游戏目录>\OptiScaler\dlssg_sm86\logs\           OptiScaler 内置那份
    //
    //  0 字节的日志也是有价值的信息：说明**代理被加载过、但没产生帧生成调用**。
    //  纯只读：只读文件，不写不改。
    // ============================================================================
    public class FgResult
    {
        public string Game = "";            // 游戏标题（由调用方填）
        public string Dir = "";
        public string File = "";            // 日志文件名（不含路径）
        public DateTime When = DateTime.MinValue;
        public long Bytes;
        public int MaxGen = -1;             // configuration.max_generated（-1 = 未见该事件）
        public int GenCount = -1;           // evaluate.generated_count 的最大值（-1 = 未见）
        public string Note = "";            // 异常说明（如代理回报的配置错误）

        // 当**最近一份**日志是空的（= 代理加载过但没跑帧生成）时，附带"上一份有内容日志"的信息，
        // 这样结论不会把早先的一次成功记录顶掉（2026-09-16：2077 就是 01:35 成功过、02:07 又启动
        // 过但没跑；旧实现只报"有内容那份"，于是把 02:07 那次完全忽略了）。
        public bool HasPrev;
        public DateTime PrevWhen = DateTime.MinValue;
        public int PrevGenCount = -1;
        public int PrevMaxGen = -1;
        public string PrevNote = "";

        public bool HasLog { get { return When != DateTime.MinValue; } }
        public bool IsEmpty { get { return HasLog && Bytes == 0; } }
        public string RateMax { get { return MaxGen >= 0 ? (MaxGen + 1) + "X" : ""; } }
    }

    public static class FgVerify
    {
        // 日志候选目录（相对游戏目录）
        static readonly string[] LogDirs = new string[] {
            @"dlssg_sm86\logs",
            @"OptiScaler\dlssg_sm86\logs",
        };

        // 扫一个游戏目录，返回**最近一次代理活动**的快照：
        //  · 最近那份日志有内容 → 直接解析它（可能是有效记录 / 上限 / 配置错误）
        //  · 最近那份是 0 字节 → 判为"加载了但没跑"，并把上一份有内容的记录附在 Prev* 里
        public static FgResult Check(string gameDir)
        {
            var none = new FgResult();
            none.Dir = gameDir == null ? "" : gameDir;
            if (none.Dir.Length == 0 || !Directory.Exists(none.Dir)) return none;

            FgResult latest = null, prev = null;
            string latestPath = null;
            try
            {
                foreach (string rel in LogDirs)
                {
                    string d = Path.Combine(none.Dir, rel);
                    if (!Directory.Exists(d)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(d, "*.jsonl"); }
                    catch { continue; }

                    foreach (string f in files)
                    {
                        long len;
                        DateTime t;
                        try { len = new FileInfo(f).Length; t = File.GetLastWriteTime(f); }
                        catch { continue; }

                        if (latest == null || t > latest.When)
                        {
                            latest = new FgResult();
                            latest.Dir = none.Dir;
                            latest.File = Path.GetFileName(f);
                            latest.When = t;
                            latest.Bytes = len;
                            latestPath = f;
                        }
                        if (len > 0 && (prev == null || t > prev.When))
                        {
                            var p = new FgResult();
                            p.Dir = none.Dir;
                            p.File = Path.GetFileName(f);
                            p.When = t;
                            p.Bytes = len;
                            ParseInto(f, p);
                            prev = p;
                        }
                    }
                }
            }
            catch { }

            if (latest == null) return none;

            if (latest.Bytes > 0)
            {
                if (latestPath != null) ParseInto(latestPath, latest);
                return latest;
            }

            // 最近一份是空的：附上"上一份有内容"的记录（供 Summary 说明"上次成功是什么时候"）
            if (prev != null)
            {
                latest.HasPrev = true;
                latest.PrevWhen = prev.When;
                latest.PrevGenCount = prev.GenCount;
                latest.PrevMaxGen = prev.MaxGen;
                latest.PrevNote = prev.Note;
            }
            return latest;
        }

        // 逐行扫 jsonl。只做子串定位 + 手写取整，避免引入 JSON 解析开销（这函数会被频繁调用）。
        static void ParseInto(string path, FgResult r)
        {
            try
            {
                using (var sr = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    int guard = 0;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (++guard > 20000) break;      // 防御：异常大的日志不至于拖住界面

                        int i = line.IndexOf("\"max_generated\":", StringComparison.Ordinal);
                        if (i >= 0) { int v = IntAfter(line, i + 16); if (v >= 0) r.MaxGen = v; }

                        i = line.IndexOf("\"generated_count\":", StringComparison.Ordinal);
                        if (i >= 0) { int v = IntAfter(line, i + 18); if (v >= 0 && v > r.GenCount) r.GenCount = v; }

                        if (r.Note.Length == 0 && line.IndexOf("configuration_error", StringComparison.Ordinal) >= 0)
                            r.Note = line.IndexOf("Invalid INI integer", StringComparison.Ordinal) >= 0
                                ? "配置被拒（INI 里有代理不认的值，游戏可能卡在加载界面）"
                                : "配置被拒（代理拒绝了 INI）";
                    }
                }
            }
            catch { }
        }

        static int IntAfter(string s, int at)
        {
            int i = at;
            while (i < s.Length && (s[i] == ' ' || s[i] == '"')) i++;
            int st = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == st) return -1;
            int v;
            return int.TryParse(s.Substring(st, i - st), out v) ? v : -1;
        }

        // 扫全部游戏，取"最近一次代理活动"给出一句话结论（仪表盘用）。
        //  effective = 最近一次就是成功的（读到真实 generated_count），调用方据此决定用什么颜色。
        public static string Summary(List<DlssgGame> games, out bool effective)
        {
            effective = false;
            if (games == null || games.Count == 0) return "";

            FgResult best = null;      // 最近一次代理活动（不管有没有跑到）
            FgResult lastOk = null;    // 最近一次"真的跑起来"的记录
            try
            {
                foreach (var g in games)
                {
                    if (g == null || g.Ignored || g.Dir == null || g.Dir.Length == 0) continue;
                    FgResult r = Check(g.Dir);
                    if (!r.HasLog) continue;
                    r.Game = g.Title;
                    if (best == null || r.When > best.When) best = r;

                    if (r.GenCount >= 0)
                    {
                        if (lastOk == null || r.When > lastOk.When) lastOk = r;
                    }
                    else if (r.IsEmpty && r.HasPrev && r.PrevGenCount >= 0)
                    {
                        // 最近那份是空的，但它之前有一次成功 —— 也算一次"成功记录"
                        if (lastOk == null || r.PrevWhen > lastOk.When)
                        {
                            var p = new FgResult();
                            p.Game = g.Title;
                            p.When = r.PrevWhen;
                            p.GenCount = r.PrevGenCount;
                            lastOk = p;
                        }
                    }
                }
            }
            catch { }

            if (best == null)
                return "尚无代理日志 —— 进游戏跑一局后回来看（代理会自己写记录）";

            string t = best.When.ToString("MM-dd HH:mm");

            if (best.GenCount >= 0)
            {
                effective = true;
                return "最近生效：" + best.Game + " · 实际 " + (best.GenCount + 1) + "X（" + t + "）";
            }
            if (best.IsEmpty)
            {
                // 0 字节 = 代理被加载过、但没产生帧生成调用（没进游戏跑 / 游戏没请求 / 被拦）
                //  · 同一游戏自己更早有过成功 → 说"上次成功"
                //  · 成功记录属于**别的**游戏 → 说"｜最近成功：…"（用"上次"会读成同一款的历史）
                if (best.HasPrev && best.PrevGenCount >= 0)
                    return "最近一次：" + best.Game + " · 未见帧生成调用（" + t + "）；上次成功：实际 "
                         + (best.PrevGenCount + 1) + "X（" + best.PrevWhen.ToString("MM-dd HH:mm") + "）";
                if (lastOk != null && lastOk.Game != best.Game && lastOk.GenCount >= 0)
                    return "最近一次：" + best.Game + " · 未见帧生成调用（" + t + "）｜最近成功："
                         + lastOk.Game + " · 实际 " + (lastOk.GenCount + 1)
                         + "X（" + lastOk.When.ToString("MM-dd HH:mm") + "）";
                return "最近一次：" + best.Game + " · 代理已加载但未见帧生成调用（" + t + "）";
            }
            if (best.Note.Length > 0)
                return "最近一次：" + best.Game + " · " + best.Note + "（" + t + "）";
            if (best.MaxGen >= 0)
                return "最近一次：" + best.Game + " · 上限 " + (best.MaxGen + 1)
                     + "X，但没读到生成记录（" + t + "）";
            return "最近一次：" + best.Game + " · 代理已加载但未见帧生成调用（" + t + "）";
        }

    }

    public static class GameIcon
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
                                              IntPtr[] phicon, int[] piconid, int nIcons, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        public static System.Drawing.Bitmap Extract(string exePath, int size)
        {
            System.Drawing.Bitmap best = null;
            // 从大到小试；只要拿到一张就够，但若前面拿到的很小、后面要到大图就换（取实际最大）
            foreach (int want in new int[] { 256, 128, 64, 48, 32 })
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    var hs = new IntPtr[1];
                    var ids = new int[1];
                    if (PrivateExtractIcons(exePath, 0, want, want, hs, ids, 1, 0) <= 0) continue;
                    h = hs[0];
                    if (h == IntPtr.Zero) continue;
                    System.Drawing.Bitmap b;
                    using (var ico = System.Drawing.Icon.FromHandle(h))
                        b = ico.ToBitmap();
                    if (b == null) continue;
                    if (best == null || b.Width > best.Width)
                    {
                        if (best != null) best.Dispose();
                        best = b;
                    }
                    else b.Dispose();
                    if (best.Width >= 256) break;
                }
                catch { }
                finally { if (h != IntPtr.Zero) { try { DestroyIcon(h); } catch { } } }
            }

            if (best == null)
            {
                // 兜底：老 API（某些自解压 exe / 资源异常的 exe 只有这条路）
                try
                {
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                        if (ico != null) best = ico.ToBitmap();
                }
                catch { return null; }
            }
            if (best == null) return null;
            try { return Scale(best, size, size); }
            finally { best.Dispose(); }
        }

        // 高质量缩放：new Bitmap(img, w, h) 走的是低质量插值，缩出来发虚。
        // TileFlipXY 是 GDI+ 缩放的标准修法（否则边缘一圈发白/发黑）。
        public static System.Drawing.Bitmap Scale(System.Drawing.Image src, int w, int h)
        {
            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h),
                                0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                }
            }
            return bmp;
        }
    }

    // ==========================================================================
    //  XeSS 多帧生成（v2.2.0）—— 两套社区验证可用的部署
    //  --------------------------------------------------------------------------
    //  绝区零（zzz）：OptiScaler 挂 d3d12.dll（DX12 必加载的系统库名，反作弊按名拦不掉）
    //                + fakenvapi + libxess/libxess_fg + 作者预配 ini
    //                （SpoofedGPUName=RTX 5090 → 游戏菜单露 DLSS+帧生成；
    //                  FGInput=DLSSG → FGOutput=XeFG，即"Xe 多帧生成 2-6 倍"）
    //  鸣潮（wuwa）：OptiScaler 0.7.7 全套挂 dxgi.dll + OptiScaler\ 子目录（dlssg_sm86 + streamline）
    //                + nvngx_dlssnr；ini 预配 dlss 上采样 + DLSSG 进出
    //  资源放数据目录（合计约 850 MB，不内嵌 exe）：
    //    %ProgramData%\Fluxion\xess-pack\{zzz,wuwa}\
    //  安装时对被覆盖的游戏自带件做备份，卸载时恢复（绝区零的 libxess* 是游戏自带件）。
    //  ⚠ 反作弊风险：绝区零的 d3d12 名实测可绕过（2026-09-13 验证），但仍属注入行为。
    // ==========================================================================
    // ==========================================================================
    //  DLSSG for SM86 · 0.3.5（代理模式）—— 真 DLSS 多帧生成
    //  --------------------------------------------------------------------------
    //  与 XeMfg（OptiScaler + XeFG 转译）不同：0.3.x 内嵌未修改的原厂 DLSS-G
    //  运行库，只拦截 nvngx_dlssg.dll 的加载，游戏侧 NGX 调用不变 → 真 DLSS MFG
    //  （2X-6X，310.9 版），延迟更低。作者建议入口：绝区零 d3d12 / 鸣潮 version|dxgi。
    //  0.3.2 相对 0.3.0：重写 310.9 的 26 个推理内核（输出与官方逐位一致，3080 Ti 快 0~8%），
    //  `Optimized` 扩到 0-3 四级，并把 20 系（SM75）并进同一个包。
    //  资源包：<数据目录>\dlssg030-pack\{common,alts}（common 含 version.dll 与公共文件）
    //  代理签名：CN=DLSSG for SM86 (self-signed)，指纹 85BA6676…（0.2.4 起未换过，换包时要复核）
    // ==========================================================================
    public static class Dlssg030
    {
        static readonly string[] CommonFiles = new string[] { "dlssg_sm86.ini", "nvngx_dlss.dll", "nvngx_dlssg.dll" };
        // 当前随工具发布的代理版本 —— 界面标题 / 卡片标签 / 日志一律走它，换包只改这一行。
        //  2026-09-21 由 0.3.2 换到 0.3.5（出厂 INI 逐字节相同、签名指纹相同，只有代理二进制换掉）。
        //  0.3.5 修：0.3.0 起「游戏重建帧生成特性后用错优化内核 → 生成帧花屏 / 玩久随机崩溃」（#561）。
        //  0.3.4 修：0.3.3 把 NVIDIA 自家 NGX 模型也一起改写架构导致的 GPU 挂起（#535/#538/#540/#542）。
        //  0.3.3 起：架构改写提前到游戏启动并对外报 RTX 50；Optimized=1 不再默认跳过重复真实帧拷贝（#532 闪屏）。
        // 当前随工具发布的代理版本 —— 界面标题 / 卡片标签 / 日志一律走它。
        //  判据本体在 Catalog（数据目录 catalog.json 可覆盖；没有该文件时用内置默认 0.3.5）。
        //  ⚠ 这说的是"工具打算发哪一版"，不是"磁盘上躺着哪一版" —— 后者见 PackActual()。
        public static string Ver { get { return Catalog.Ver; } }
        // 入口代理内嵌整套运行库，体积跟着版本走。粗窗口与版本档全部读 Catalog：
        //  内置默认 0.3.0/0.3.1 = 16–20 MB；0.3.2 = 29,974,816~29,993,760；
        //  0.3.5 = 30,021,408~30,039,840。28 MB 档必须单列一段 —— 它比 OptiScaler 的入口
        //  （25.6 / 26.1 MB）还大，只按"大于 20 MB 就是方案 A"的老阈值会整类认错（2026-09-17）。
        public static bool IsProxySize(long n) { return Catalog.IsProxySize(n); }

        // ---- 版本档表（体积 → 具体哪一版），数据在 Catalog ----
        //  代理 DLL 里没有版本资源可读、作者也不在文件名里标版本，实测只有体积能分代。
        //  对不上任何细档时 Catalog 只给粗标签（"定不到小版本"），不拿 Ver 顶着。
        public static ProxyBand[] VersionBands { get { return Catalog.Bands; } }

        // 体积 → 细档代号；对不上返回空串（调用方自己决定是退到粗窗口还是报未知）
        public static string CodeOf(long n) { return Catalog.CodeOf(n); }

        // 只在"落在粗窗口但对不上任何细档"时给出的粗标签
        public static string CoarseCodeOf(long n) { return Catalog.CoarseCodeOf(n); }

        // 界面文案用的一行"这次发的哪一版 + 磁盘那份多大"。
        //  体积一律现场量，不抄进句子 —— 抄一次就多一处会过期（30,021,920 那个例子）。
        public static string PackCaption()
        {
            PackActualInfo a = PackActual();
            if (!a.Found) return "本工具的 " + Ver + " 包（资源包还没落盘）";
            string code = a.Code.Length > 0 ? a.Code : Ver;
            return "本工具资源包里那份 = " + code + "（version.dll " + a.EntrySize.ToString("N0") + " 字节）";
        }

        // 一条"只在某些代理版本成立"的上游实测结论；挑不出来时用调用方给的中性说法。
        public static string Knowledge(string id, string fallback)
        {
            string t = Catalog.Knowledge(id);
            return t.Length > 0 ? t : fallback;
        }

        public static string PackRoot { get { return Path.Combine(Program.DataDir, "dlssg030-pack"); } }
        // public：Pack.cs（拖入导入）要往这两个目录归位文件并按它们判完整度
        public static string CommonDir { get { return Path.Combine(PackRoot, "common"); } }
        public static string AltsDir { get { return Path.Combine(PackRoot, "alts"); } }
        static string BackupDir(string key) { return Path.Combine(BackupRoot, key); }
        // public：Junk（备份统计与回收）要按同一套路径去扫，别再抄一份 "_backup" 字面量
        public static string BackupRoot { get { return Path.Combine(PackRoot, "_backup"); } }


        // 每个游戏用哪个入口名（作者在包内"几个网游注入.txt"里给的推荐）
        public static string EntryFor(string kind)
        {
            if (kind == "zzz") return "d3d12.dll";
            if (kind == "wuwa") return "dxgi.dll";
            // 通用游戏（3A 单机）：默认 version.dll（绝大多数游戏都会加载它），
            //  加载不到时在界面上从 6 个备选名里换一个（作者 alternatives/README.md 的用法）
            string g = Program.Cfg != null ? Program.Cfg.DlssgGenericEntry : null;
            return (g != null && g.Length > 0) ? g : "version.dll";
        }
        static bool EqHead(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        public static bool IsInstalled(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return false;
            return CheckAt(kind, XeMfg.TargetDir(gameDir, kind));
        }
        public static bool IsInstalledQuick(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Exe == null) return false;
            string kind;
            if (EqHead(g.Exe, "ZenlessZoneZero.exe")) kind = "zzz";
            else if (EqHead(g.Exe, "Client-Win64-Shipping.exe")) kind = "wuwa";
            else return false;
            return CheckAt(kind, g.Dir);
        }
        // 判据：入口代理体积落在 0.3.x 区间 + 同目录有 dlssg_sm86.ini
        static bool CheckAt(string kind, string target)
        {
            try
            {
                string p = Path.Combine(target, EntryFor(kind));
                if (!File.Exists(p) || !File.Exists(Path.Combine(target, "dlssg_sm86.ini"))) return false;
                return IsProxySize(new FileInfo(p).Length);
            }
            catch { return false; }
        }
        public static string ShortTag(DlssgGame g)
        {
            if (g == null || g.Exe == null) return "";
            if (EqHead(g.Exe, "ZenlessZoneZero.exe") && CheckAt("zzz", g.Dir)) return "DLSS MFG " + Ver + "（d3d12）";
            if (EqHead(g.Exe, "Client-Win64-Shipping.exe") && CheckAt("wuwa", g.Dir)) return "DLSS MFG " + Ver + "（dxgi）";
            // 通用（3A 单机）：按目录里有没有我们的代理判断。卡片也要显示出来，
            //  否则"装没装成功"在列表里看不出来（这是反复重装的动机之一）
            if (GenericInstalled(g.Dir)) return "DLSS MFG 0.3.x（通用）";
            return "";
        }
        public static string Status(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（绝区零 / 鸣潮）";
            string name = kind == "zzz" ? "绝区零" : "鸣潮";
            if (IsInstalled(gameDir))
                return "已部署（" + name + " · " + EntryFor(kind) + " 入口 · 真 DLSS 帧生成，上限 6X）";
            if (!File.Exists(Path.Combine(CommonDir, "version.dll"))) return "资源包缺失（应位于 " + PackRoot + "）";
            return "未部署（" + name + "）";
        }

        // 驱动结论。分档给不同建议，因为「越新越好」在这件事上恰恰是错的：
        //  · < 591.86：低于作者给的下限。
        //  · 591.86 ~ 616.55：正常区。NR 的模型随应用提供、不取自已安装驱动，
        //    所以这个区间照样能跑 OptiScaler 安装 —— 不必为了 NR 升级。
        //  · 616.56：目前唯一既"达到上游门槛"、NR 又能跑完的版本。
        //  · ≥ 616.64：NR 每次 evaluate 都会在 NVIDIA 自家 NGX 运行时 fault
        //    （DLSS5-Feeder 作者三台机器实测：616.64 / 616.86 fault，616.56 completes；issue #54）。
        //    注意这条对任何 N 卡生效，不限于 50 系。
        // public：tools/opt_probe.cs 要对四档结论分别断言（分档错了会误导用户升级/回退）
        public static string NoteDriver(double drv)
        {
            if (drv < 591.86) return "  ✘ 低于作者要求的 591.86，请先升级驱动";
            if (drv >= 616.64)
                return "  ⚠ ≥616.64：NR 每次 evaluate 都会在 NVIDIA 自家 NGX 运行时 fault"
                     + "（616.64 / 616.86 实测 fault，只有 616.56 能跑完）→ 要用 NR 请回退到 616.56";
            if (drv >= 616.56)
                return "  ✔ 616.56：目前唯一既达到上游门槛、NR 又能跑完的版本";
            return "  ✔ 满足作者要求（≥591.86）。NR 的模型随应用提供、不取自已安装驱动，"
                 + "所以这个版本照样能跑 —— 不必为了 NR 升级";
        }

        // ---- 前置条件检查 ----
        //  逐条对照作者两个视频的简介：
        //   ① 驱动至少要 591.86；这不是越高越好 —— 见下方 NoteDriver()，
        //      ≥616.64 会让 NR 在 NVIDIA 自家 NGX 运行时 fault（2026-09-16 核实）
        //   ② 改显卡名（一般网游才需要）：适配器名改成 NVIDIA GeForce RTX 5090 且不留乱码；
        //      每次升级驱动后都要重改
        //   ③ 装新版前要删掉旧版的 version 与 sm86 文件（本工具部署时自动清）
        //   ④ 开启 6X 需要驱动支持 DLSS 4.5，且游戏本身原生支持 6 倍生成
        public static string PrereqReport()
        {
            var sb = new StringBuilder();
            string gpu; double drv;
            Program.NvDriver(out gpu, out drv);
            if (drv <= 0)
                sb.AppendLine("· 显卡驱动：未识别（非 NVIDIA 卡或读取失败）—— 本方案只对 N 卡有意义");
            else
                sb.AppendLine("· 显卡驱动：" + drv.ToString("0.##") + NoteDriver(drv));
            sb.AppendLine("· 显卡名称：" + (gpu.Length > 0 ? gpu : "未识别")
                + (gpu.IndexOf("5090", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "  ✔ 已是 50 系名称（已伪装）"
                    : "  —— 网游（绝区零等）常需要伪装成「NVIDIA GeForce RTX 5090」才放行"));
            string gateTier = Dlssg.GpuTier();
            sb.AppendLine("· 显卡代际：" + gateTier
                + (Dlssg.GpuGateRec(gateTier) != null
                    ? "（不在 RTX 20/30 系支持范围 —— 推荐页会直接劝退，勿装）"
                    : "（在支持范围内）"));
            sb.AppendLine("· 系统版本：" + Program.WindowsBuild() + "（作者提示：系统版本太老也可能不生效）");
            sb.AppendLine("· 绝区零专属结论（2026-09-16 深度实测）：方案A 的代理从未被绝区零加载过 ——"
                        + "未签名 DLL 被反作弊拦掉（隔离实验证明 dll 本身可加载，游戏进程内从未加载）。"
                        + "绝区零唯一可行的注入路径是作者的自带签名版 d3d12.dll（0.3.x），"
                        + "且必须先做注册表显卡名伪装（0.3.x 不做伪装，游戏看到 3060 Ti 就不会显示帧生成选项）。");
            sb.AppendLine("· 6X 前提：驱动需支持 DLSS 4.5，且游戏原生支持 6 倍生成；否则最高只能选到 2X~4X");
            sb.AppendLine("· 升级注意：装新版前必须清掉旧版 version.dll / sm86 残留（本工具部署时会自动清理）");
            sb.AppendLine("· 改显卡名的注册表位置（改完重启；每次升级驱动后都要重改，值里不要留任何乱码）：");
            sb.AppendLine("    HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_10DE&DEV_xxx\\<实例>\\DeviceDesc");
            sb.AppendLine("    → 改成 NVIDIA GeForce RTX 5090");
            return sb.ToString();
        }

        // 清掉"上一次选过、这次不用"的旧代理入口残留（作者要求的升级卫生）。
        //  只删名字在已知入口表里、且体积落在 0.3.x 区间内的文件 ——
        //  游戏自带同名 dll 的体积不在这个区间，绝不会被误删。
        static int CleanOldEntries(string dst, string keep)
        {
            int n = 0;
            foreach (var f in Plan.AllEntryNames)      // 全表（含 winhttp），单点维护见 Plan.EntryNames
            {
                if (EqHead(f, keep)) continue;
                try
                {
                    string p = Path.Combine(dst, f);
                    if (!File.Exists(p)) continue;
                    if (!IsProxySize(new FileInfo(p).Length)) continue;
                    File.Delete(p);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // force：资源包换过版本后的强制重抄。不加这个开关，「更新部署」会被下面那条
        //  "已在位"短路挡住 —— 资源包是新的、游戏目录里还是旧的，界面却显示成功。
        // 资源包里这次要用的入口代理。Install 与「部署前签名核验」共用同一份取件规则 ——
        //  写成两处必然对不上（核验的是一份、装进去的是另一份）。
        public static string PackProxyPath(string kind)
        {
            string entry = EntryFor(kind);
            return entry == "version.dll" ? Path.Combine(CommonDir, entry) : Path.Combine(AltsDir, entry);
        }

        // 这次部署会把哪个文件写进游戏目录（找不到 = 返回空，交给原有逻辑去报错）。
        //  专用游戏走 PackProxyPath；通用游戏只放一个入口，与 InstallGeneric 的取件一致。
        public static string SourceEntryPath(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length > 0) return PackProxyPath(kind);
            string e = Program.Cfg.DlssgGenericEntry;
            if (e == null || e.Length == 0) e = "version.dll";
            return e.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                 ? Path.Combine(CommonDir, "version.dll")
                 : Path.Combine(AltsDir, e);
        }

        // ---- 磁盘上资源包"实际是谁"（只读）----
        //  为什么需要这一层：Ver 是代码常量，只说明"工具打算发哪一版"；pack 目录里躺着的是
        //  哪一版取决于用户（或作者自己）最后一次往 common\alts 写了什么。2026-09-21 换包时
        //  就是靠手工量 version.dll 体积才确认磁盘上是哪代 —— 而"代码说 0.3.5、磁盘还是 0.3.2"
        //  这类不一致以前没有任何地方能发现，偏偏两代的 bug 面完全不同。
        //  这个方法只量不写、不修，任何异常都退化成"报不出来"而不是抛给用户。
        public class PackActualInfo
        {
            public bool Found;                // common\version.dll 在不在
            public long EntrySize;
            public string EntryTime = "";     // 文件最后写入时间 —— 没有导入账本时唯一的来源线索
            public string Code = "";          // 体积反查到的代号（细档优先，退到粗档）
            public string Verdict = "";       // ok / mismatch / coarse / unknown / missing
            public long AltsCount, AltsMin, AltsMax;
            public long IniSize;
            public string SignVerdict = "";         // 入口签名的判定（upstream/unsigned/…），界面上缩成一个短词
            public bool HasLedger;            // pack.json 在不在
            public string ImportedAt = "", Source = "", LedgerVer = "";
            public string Drift = "";         // 非空 = 导入账本记录的体积与磁盘现状不符（被人手工换过）
            public string Note = "";          // 其它需要如实说明的情况

            public bool MatchesVer { get { return Verdict == "ok"; } }

            // 界面上要不要用告警色。⚠ 光看 MatchesVer 不够：磁盘被人手工换过、
            //  但换进来的那一版恰好又等于 Ver 时，体积对得上、账本对不上 —— 那也必须报。
            public bool Warn { get { return !Found || !MatchesVer || Drift.Length > 0; } }

            // 一行结论，给帧生成页「运行时状态」与日志共用。
            //  不写死体积数字 —— 这里输出的就是实测值（P1-7 要的效果）。
            //  刻意压到一行能放下：这一格是 BodyFixed + AutoEllipsis，长了不会被折行，直接被裁掉。
            public string Line()
            {
                if (!Found) return "资源包：未落盘（" + Path.Combine(CommonDir, "version.dll") + " 不存在）";
                StringBuilder sb = new StringBuilder();
                if (Verdict == "mismatch")
                    sb.Append("⚠ 资源包：磁盘是 ").Append(Code).Append("，工具按 ").Append(Ver).Append(" 发布");
                else if (Verdict == "unknown")
                    sb.Append("⚠ 资源包：不在任何已知版本档，工具按 ").Append(Ver).Append(" 发布");
                else
                    sb.Append("资源包：").Append(Code);
                sb.Append("（version.dll ").Append(EntrySize.ToString("N0")).Append(" B");
                if (IniSize > 0) sb.Append(" · INI ").Append(IniSize.ToString("N0")).Append(" B");
                if (AltsCount > 0) sb.Append(" · alts ").Append(AltsCount).Append(" 个");
                string at = LedgerMinute(ImportedAt);
                if (HasLedger && at.Length > 0) sb.Append(" · 导入 ").Append(at);
                else if (EntryTime.Length > 0) sb.Append(" · 文件 ").Append(EntryTime);
                string sg = SignWord();
                if (sg.Length > 0) sb.Append(" · ").Append(sg);
                sb.Append("）");
                if (Drift.Length > 0) sb.Append(" ⚠ ").Append(Drift);
                if (Note.Length > 0) sb.Append(" ｜ ").Append(Note);
                return sb.ToString();
            }

            // 签名只留一个短词 —— ProxySign.Short() 那句 20 多字，放这行里放不下（完整结论在导入报告里）
            string SignWord()
            {
                if (SignVerdict == "upstream") return "作者签名";
                if (SignVerdict == "nvidia") return "NVIDIA 签名";
                if (SignVerdict == "unsigned") return "未签名";
                if (SignVerdict == "other") return "非白名单签名";
                if (SignVerdict == "missing") return "签名读不到";
                return "";
            }

            // 账本记到秒，显示到分就够（多出来的 :ss 不值得占掉体积数字的位置）
            static string LedgerMinute(string s)
            {
                if (s == null) return "";
                return s.Length > 16 ? s.Substring(0, 16) : s;
            }
        }

        // public：tools/pack_probe.cs 要对四种现状分别断言
        public static PackActualInfo PackActual()
        {
            PackActualInfo a = new PackActualInfo();
            string ep = Path.Combine(CommonDir, "version.dll");
            try
            {
                FileInfo fi = new FileInfo(ep);
                if (fi.Exists)
                {
                    a.Found = true;
                    a.EntrySize = fi.Length;
                    a.EntryTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { }
            try
            {
                if (Directory.Exists(AltsDir))
                    foreach (string f in Directory.GetFiles(AltsDir, "*.dll"))
                    {
                        long n = new FileInfo(f).Length;
                        a.AltsCount++;
                        if (a.AltsMin == 0 || n < a.AltsMin) a.AltsMin = n;
                        if (n > a.AltsMax) a.AltsMax = n;
                    }
            }
            catch { }
            try
            {
                string ip = Path.Combine(CommonDir, "dlssg_sm86.ini");
                if (File.Exists(ip)) a.IniSize = new FileInfo(ip).Length;
            }
            catch { }
            if (a.Found) a.SignVerdict = ProxySign.Of(ep).Verdict;

            if (!a.Found) a.Verdict = "missing";
            else
            {
                string fine = CodeOf(a.EntrySize);
                bool exact = fine.Length > 0;
                a.Code = exact ? fine : CoarseCodeOf(a.EntrySize);
                if (!IsProxySize(a.EntrySize)) a.Verdict = "unknown";
                else if (a.Code == Ver) a.Verdict = "ok";
                else if (!exact) a.Verdict = "coarse";     // 认得出是哪一代、认不出小版本 —— 别拿 Ver 顶着
                else a.Verdict = "mismatch";
            }

            PackLedger L = Pack.ReadLedger();
            if (L == null) { if (a.Found) a.Note = "没有导入账本（手工换的包）"; }
            else
            {
                a.HasLedger = true;
                a.ImportedAt = L.ImportedAt;
                a.Source = L.Source;
                a.LedgerVer = L.Ver;
                LedgerFile lf;
                if (a.Found && L.Files.TryGetValue("version.dll", out lf) && lf.Size > 0 && lf.Size != a.EntrySize)
                    a.Drift = "导入账本记 " + lf.Size.ToString("N0") + " B ≠ 现状 " + a.EntrySize.ToString("N0") + " B";
                if (L.ImportedAt.Length == 0 && a.EntryTime.Length > 0) a.Note = "账本没记导入时间，按文件时间看";
            }
            return a;
        }

        public static string Install(string gameDir, bool force = false)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（绝区零 / 鸣潮）";
            if (!force && IsInstalled(gameDir)) return "已处于部署状态，无需重复部署";
            // 方案 A 在位 → 直接整套停放，不再把用户打发去手动点「停放」。
            //  原实现这里 return 一段"请先在上一区块点停放，再回来点本方案"——用户照做之前，
            //  两套方案就同时在游戏目录里生效（2026-09-18 的投诉正是这个）。
            //  仍然不建议换（0.3.x 没有 NR），但那是"提示"，不该变成"拦路"。
            List<string> parked = new List<string>(), failed = new List<string>();
            parked.AddRange(Plan.ParkOthers(gameDir, kind, Plan.D030, failed));
            string entry = EntryFor(kind);
            string proxy = PackProxyPath(kind);
            if (!File.Exists(proxy)) return "资源包缺代理：" + proxy;
            string dst = XeMfg.TargetDir(gameDir, kind);
            string bk = BackupDir(kind);
            int cleaned = CleanOldEntries(dst, entry);        // 升级卫生：先清旧版入口残留
            var list = new List<string>();
            list.Add(entry);
            list.AddRange(CommonFiles);
            int copied = 0, backed = 0;
            try
            {
                foreach (var f in list)
                {
                    string s = f == entry ? proxy : Path.Combine(CommonDir, f);
                    if (!File.Exists(s)) return "资源包缺文件：" + f;
                    string d = Path.Combine(dst, f);
                    if (File.Exists(d))                       // 覆盖前备份（游戏自带 nvngx 也在内）
                    {
                        Directory.CreateDirectory(bk);
                        File.Copy(d, Path.Combine(bk, f), true);
                        backed++;
                    }
                    File.Copy(s, d, true);
                    copied++;
                }
            }
            catch (Exception ex) { return "部署失败：" + ex.Message; }
            if (!IsInstalled(gameDir)) return "部署异常：代理未正确落盘（检查杀软拦截 / 磁盘空间）";
            EnsureFactoryIni(dst);
            return "已部署 " + copied + " 个文件（覆盖前备份 " + backed + " 个"
                 + (cleaned > 0 ? "，清理旧入口残留 " + cleaned + " 个" : "") + "）→ " + (kind == "zzz" ? "绝区零" : "鸣潮")
                 + " 已启用真 DLSS 多帧生成（" + entry + " 入口 · INI MaxGeneratedFrames=5 → 上限 6X）"
                 + "\n  验证：完全退出并重启游戏 → 画质菜单应出现「DLSS 帧生成」；日志 <游戏目录>\\dlssg_sm86\\logs\\loader_*.jsonl"
                 + Plan.Note(parked, failed);
        }

        // ---------- 通用（3A 单机）部署 ----------
        //  作者 README 的安装步骤：只需把 version.dll（或 alternatives\ 里某个备选名）与
        //  dlssg_sm86.ini 复制进"渲染 EXE 目录"。**不覆盖游戏自带的 nvngx** ——
        //  实测 CP2077 的 nvngx_dlssg.dll(7,607,336) 与 FH6 的(7,499,376) 都比包内那份新。
        public static readonly string[] GenericEntries = new string[] {
            "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" };

        public static bool HasGeneral(string iniPath)
        {
            try { return File.Exists(iniPath) && File.ReadAllText(iniPath).IndexOf("[General]", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        // 是否已有本方案的代理在位（按体积认，14.5MB 的旧版不算）
        public static bool GenericInstalled(string dir)
        {
            try
            {
                foreach (string e in GenericEntries)
                {
                    string p = Path.Combine(dir, e);
                    if (!File.Exists(p)) continue;
                    if (IsProxySize(new FileInfo(p).Length)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // 通用部署/切换：先停放别家代理（含 14.9MB 旧版），再放入口 + 出厂 INI
        public static string InstallGeneric(string gameDir, string entry, int maxFrames, bool force = false)
        {
            if (entry == null || entry.Length == 0) entry = "version.dll";
            try
            {
                // keep 要传**方案 id**，不是入口文件名 —— 原实现把 `entry` 传了进来，
                //  而函数里是拿它跟 OwnerOf 的结果（opti/030/legacy）比，恒不相等 →
                //  通用游戏每次部署都会把目录里所有代理无差别停放。
                List<string> failed = new List<string>();
                List<string> parked = Plan.ParkOthers(gameDir, "", Plan.D030, failed);
                string src = entry.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                           ? Path.Combine(CommonDir, "version.dll")
                           : Path.Combine(AltsDir, entry);
                if (!File.Exists(src)) return "资源包里没有这个入口：" + entry;
                string dst = Path.Combine(gameDir, entry);
                if (File.Exists(dst))
                {
                    if (!force && IsProxySize(new FileInfo(dst).Length)) return "已经就是本方案了（" + entry + " 在位）。";
                    File.Copy(dst, dst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                }
                File.Copy(src, dst, true);

                string ini = Path.Combine(CommonDir, "dlssg_sm86.ini");
                string idst = Path.Combine(gameDir, "dlssg_sm86.ini");
                // force 时连出厂 INI 一起重抄 —— 0.3.0 → 0.3.2 的 INI 本身就换过（2099 → 3548 B；0.3.2 后段起 3571 B），
                //  只换代理不换 INI 会留一份和内核不匹配的配置。
                if (File.Exists(ini) && (force || !HasGeneral(idst)))
                {
                    if (File.Exists(idst)) File.Copy(idst, idst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    File.Copy(ini, idst, true);
                }
                string r = SetMaxFrames(gameDir, maxFrames);
                return "已切换到本方案：入口 " + entry + " + dlssg_sm86.ini。\n  " + r + "。重启游戏生效。"
                     + Plan.Note(parked, failed);
            }
            catch (Exception ex) { return "部署失败：" + ex.Message; }
        }

        // 0.3.x 的出厂 INI 是完整发布配置：0.3.0 是 2099 字节，0.3.2 后段与 0.3.5 都是 3571 字节（多一档
        //  Optimized 说明），都带 [General] Enabled=1 / [Runtime] Mode=Bundled /
        //  [Compatibility] Preset / [Logging] Directory。旧版路径的 BuildIni() 只写 261 字节的模板，
        //  会把上面这些键全冲掉（2026-09-16 实测：绝区零的出厂 INI 就这样被覆盖过）。
        //  这里在部署完成后检查一次：只要缺 [General]，就用资源包里的出厂版换回去。
        public static void EnsureFactoryIni(string dst)
        {
            try
            {
                string ini = Path.Combine(dst, "dlssg_sm86.ini");
                // 文件整个丢了也要补回来。2026-09-16 实测事故：用户在绝区零点过「关闭帧生成」，
                //  该 ini 被删除；此后两次「切换到本方案」都命中 Plan.SwitchTo 的"只改名"快速路径
                //  （入口 DLL 还在、只是被停放），谁也没重抄配套文件 → ini 永久缺失 →
                //  代理读不到配置、连 dlssg_sm86\\logs 都不建 → 游戏里始终没有帧生成选项。
                if (!File.Exists(ini))
                {
                    string fac0 = Path.Combine(CommonDir, "dlssg_sm86.ini");
                    if (File.Exists(fac0)) File.Copy(fac0, ini, true);
                    return;
                }
                string cur = File.ReadAllText(ini);
                if (cur.IndexOf("[General]", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // 旧版 BuildIni() 写的 261 B 模板 —— 整份换掉，备份留档。
                    string fac0 = Path.Combine(CommonDir, "dlssg_sm86.ini");
                    if (!File.Exists(fac0)) return;
                    File.Copy(ini, ini + ".bak-oldtemplate", true);
                    File.Copy(fac0, ini, true);
                    return;
                }
                // 已是 0.3.x 格式，还要看**是不是同一代** —— 光有 [General] 不够。
                //  0.3.2 后段出厂 INI 从 3548 B 长到 3571 B（[FrameGeneration] 段首多一行；0.3.5 与我们手上这份逐字节相同）
                //  SkipRepeatedRealCopy），将来还可能再变。这里按"内容有无出厂包里的键"判：
                //  出厂包里出现的、而玩家这份缺任一个，就说明玩家那份是上几代的，补一次。
                //  ⚠ 不能按尺寸硬判：玩家可能自己改过 MaxGeneratedFrames 等键，尺寸会漂。
                string facSrc = Path.Combine(CommonDir, "dlssg_sm86.ini");
                if (!File.Exists(facSrc)) return;
                if (!FactoryIniNewerThan(cur, File.ReadAllText(facSrc))) return;
                File.Copy(ini, ini + ".bak-oldgen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                File.Copy(facSrc, ini, true);
            }
            catch { }
        }

        // 出厂 INI 是否比玩家手上那份"新"（多出出厂包里有、玩家那份没有的键）。
        //  判据取 [段] 下的 key= 行集合差集，而不是整文件比字节：
        //  玩家改过 MaxGeneratedFrames / Optimized 之类的值是常态，那不算"旧"。
        //  只看"出厂有、玩家没有"这一个方向 —— 玩家自己加的键不算数。
        //  段名与键名都按不区分大小写比（INI 语义）。
        internal static bool FactoryIniNewerThan(string playerIni, string factoryIni)
        {
            try
            {
                HashSet<string> have = IniKeysOf(playerIni);
                foreach (string k in IniKeysOf(factoryIni))
                    if (!have.Contains(k)) return true;
            }
            catch { }
            return false;
        }

        // 把 INI 摊平成 "段/键" 的小写集合（值忽略）。注释行与纯值行都不算。
        static HashSet<string> IniKeysOf(string text)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sec = "";
            foreach (string raw in text.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln[0] == ';' || ln[0] == '#') continue;
                if (ln[0] == '[')
                {
                    int e = ln.IndexOf(']');
                    if (e > 1) sec = ln.Substring(1, e - 1).Trim();
                    continue;
                }
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                set.Add(sec + "/" + ln.Substring(0, eq).Trim());
            }
            return set;
        }

        // 改写 0.3.x 部署目录里的倍率上限。
        //  0.3.x 的 dlssg_sm86.ini 与旧版**格式不同**（多了 Enabled / Optimized / Preset /
        //  Directory / Runtime 这些键），所以不能用旧版的 BuildIni() 覆盖 —— 会把这些键全丢掉。
        //  这里只动 MaxGeneratedFrames 那一行，其余原样保留。
        //  值对照（作者 README）：5 = 6X，4 = 5X，3 = 4X，2 = 3X，1 = 2X；实际生成帧数由游戏请求、再钳到上限。
        //  ⚠ 0.3.2 改了出厂默认：出厂 MaxGeneratedFrames=5（0.3.1 起作者把出厂值写成 3=4X），
        //    本工具仍按 Cfg.DlssgMaxFrames 显式写入，不受出厂值影响。
        public static string SetMaxFrames(string gameDir, int frames)
        {
            if (frames < 1) frames = 1;
            if (frames > 5) frames = 5;
            try
            {
                // 不再要求"必须是绝区零/鸣潮" —— 通用游戏（3A）同样要能改上限
                string dst = XeMfg.TargetDir(gameDir, XeMfg.Detect(gameDir));
                string p = Path.Combine(dst, "dlssg_sm86.ini");
                if (!File.Exists(p)) return "找不到 dlssg_sm86.ini（0.3.x 未部署？）";

                List<string> lines = new List<string>(File.ReadAllLines(p, Encoding.UTF8));
                bool hit = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].TrimStart().StartsWith("MaxGeneratedFrames", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "MaxGeneratedFrames=" + frames;
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                {
                    int at = -1;
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].Trim().Equals("[FrameGeneration]", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                    if (at >= 0) lines.Insert(at + 1, "MaxGeneratedFrames=" + frames);
                    else { lines.Add("[FrameGeneration]"); lines.Add("MaxGeneratedFrames=" + frames); }
                }
                File.WriteAllLines(p, lines.ToArray(), new UTF8Encoding(false));
                return "上限已设为 " + (frames + 1) + "X（MaxGeneratedFrames=" + frames + "）";
            }
            catch (Exception ex) { return "改写失败：" + ex.Message; }
        }

        // 反向互斥：XeSS 方案部署完成后，把 0.3.x 独有的痕迹清掉。
        //  之前只有"部署 0.3.x 时拒绝（因为 XeSS 在装）"这一个方向，反方向没人管 ——
        //  结果切回 XeSS 后 dlssg_sm86.ini 还留在游戏目录里（2026-09-15 实测），
        //  既是垃圾又让"到底装的哪套"变得含糊。只删这个 0.3.x 独有的 ini；
        //  dlssg_sm86\\logs 保留（诊断价值），入口代理由覆盖安装处理。
        public static int CleanLeftovers(string gameDir)
        {
            int n = 0;
            try
            {
                string kind = XeMfg.Detect(gameDir);
                if (kind.Length == 0) return 0;
                string dst = XeMfg.TargetDir(gameDir, kind);
                string ini = Path.Combine(dst, "dlssg_sm86.ini");
                if (File.Exists(ini)) { File.Delete(ini); n++; }
            }
            catch { }
            return n;
        }

        public static string Uninstall(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（绝区零 / 鸣潮）";
            string dst = XeMfg.TargetDir(gameDir, kind);
            string bk = BackupDir(kind);
            var list = new List<string>();
            list.Add(EntryFor(kind));
            list.AddRange(CommonFiles);
            int removed = 0, restored = 0;
            foreach (var f in list)
            {
                string d = Path.Combine(dst, f);
                string b = Path.Combine(bk, f);
                try
                {
                    if (File.Exists(b)) { File.Copy(b, d, true); restored++; }
                    else if (File.Exists(d)) { File.Delete(d); removed++; }
                }
                catch { }
            }
            return "已停放：恢复 " + restored + " 个游戏自带件、清掉 " + removed + " 个本方案新增文件（dlssg_sm86\\logs 保留作诊断）";
        }
    }

    // ============================================================================
    //  两套帧生成方案的「切换」（取代原来的 部署 / 卸载）
    //
    //  为什么能秒级来回切：两套方案的入口 DLL **同名**（绝区零 d3d12.dll / 鸣潮 dxgi.dll），
    //  一个游戏目录只有一个槽位；而非入口文件（OptiScaler\ 目录、NR 模型、libxess*、
    //  nvngx_dlssg.dll…）在没有入口代理时根本不会被加载，**可以一直留在原地**。
    //  所以切换 = 把当前入口改名成 <名>.parked.<方案>，把目标方案停放中那份改回原名。
    //  全程 File.Move，**不删除任何文件**，也不重抄几百 MB。2026-09-16 起。
    // ============================================================================
    public static class Plan
    {
        public const string Opti = "opti";      // 方案 A · OptiScaler 注入
        public const string D030 = "030";       // DLSS MFG 0.3.x 代理
        public const string Legacy = "legacy";  // 早期 dlssg_for_sm86 的 version/dinput8 等
        public const string None = "none";      // 原生（所有代理都停放）

        // 入口名单读 Catalog（内置默认与这份常量同值）。漏掉一个名字，
        //  那个残留就会被当成游戏自带文件放过 —— 所以它和体积档一起进判据文件。
        static string[] EntryNames { get { return Catalog.EntryNames; } }

        public static string EntryName(string kind) { return Dlssg030.EntryFor(kind); }

        // 给 UI 用：所有入口名（判断一个目录里有没有我们的代理）
        public static string[] AllEntryNames { get { return EntryNames; } }

        public static bool IsEntryName(string name)
        {
            foreach (string nm in EntryNames)
                if (nm.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 体积下限取 14 MB：旧版 dlssg_for_sm86 的入口是 15,667,520 / 15,678,272 字节（14.94 MB），
        //  按 15 MB 划界会把它整类漏掉 —— 于是工具既认不出它、也拦不住"两个代理同时生效"。
        static bool LooksProxy(long n) { return n >= 14L * 1024 * 1024 && n <= 400L * 1024 * 1024; }

        // 判断一个文件属于哪套方案（含"停放中"的）。不是代理就返回空串 —— 绝不碰游戏自带文件。
        //
        //  体积 + 签名双判（2026-09-21，P1-5）：28 MB 档的下沿与 OptiScaler 入口
        //  （d3d12.dll 26,116,608 / dxgi.dll 25,656,288）只差约 1.9 MB，上游任一侧涨一点就会串档。
        //  本方案的入口一定带作者的自签名（85BA6676…），所以签名是比体积更硬的证据：
        //   · 作者签名 → 就是本方案，与它体积落在哪一档无关；
        //   · 明确是别人签的（NVIDIA 官方 / 非白名单）→ 不是我们的代理，也不算别家方案，
        //     整条判据退回"不是代理"（宁可不碰，也不能把别人签的文件改名停放）；
        //   · 未签名 / 取不到签名 → 退回纯体积窗口（OptiScaler 入口本身也未签名，靠窗口分）。
        //  ⚠ 造不出"窗口内但错签"的真实文件：给签名 DLL 追加 0 撑体积会让证书目录读不到
        //    （实测 CryptographicException），所以判据拆成纯函数 OwnerOfCore 让探针逐格断言。
        public static string OwnerOf(string path)
        {
            try
            {
                string n = Path.GetFileName(path);
                if (n.EndsWith(".parked." + Opti, StringComparison.OrdinalIgnoreCase)) return Opti;
                if (n.EndsWith(".parked." + D030, StringComparison.OrdinalIgnoreCase)) return D030;
                if (n.EndsWith(".parked." + Legacy, StringComparison.OrdinalIgnoreCase)) return Legacy;
                long len = new FileInfo(path).Length;
                //  只在体积可能判成"本方案"或"方案 A"时才去取签名 —— 游戏自带的同名小 dll
                //  不必为此多开一次文件（一次全库扫描是 游戏数 × 7 个入口名）。
                string sv = (Dlssg030.IsProxySize(len) || len > Catalog.OptiMin)
                          ? ProxySign.Of(path).Verdict : "";
                return OwnerOfCore(len, sv);
            }
            catch { return ""; }
        }

        // 纯函数：体积 + 签名结论 → 归属。判据只这一处，探针可以任意组合喂进去。
        public static string OwnerOfCore(long len, string signVerdict)
        {
            if (!LooksProxy(len)) return "";
            if (signVerdict == "upstream") return D030;
            if (signVerdict == "nvidia" || signVerdict == "other") return "";
            if (Dlssg030.IsProxySize(len)) return D030;    // 0.3.0 17.5 MB / 0.3.2-0.3.5 30.0 MB
            if (len > Catalog.OptiMin) return Opti;        // OptiScaler 入口约 25.6 / 26.1 MB
            return Legacy;                                 // 旧版 15,667,520 B = 14.94 MB
            // ⚠ 阈值别写 17 MB：0.3.0 的入口只有 16.72 MB，会整类掉进 legacy（2026-09-16 探针抓到）
        }

        public static string Current(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            string dir = XeMfg.TargetDir(gameDir, kind);   // 未知类型 = 通用游戏，落地目录就是 gameDir
            // 先看主入口，主入口不在（被停放）就扫一遍其它入口名 ——
            //  早期 dlssg_for_sm86 用的是 version.dll / dinput8.dll，只认主入口会误报"原生"。
            string entry = EntryName(kind);
            string p = Path.Combine(dir, entry);
            if (File.Exists(p))
            {
                string o = OwnerOf(p);
                if (o.Length > 0) return o;
            }
            foreach (string nm in EntryNames)
            {
                if (nm.Equals(entry, StringComparison.OrdinalIgnoreCase)) continue;
                string f = Path.Combine(dir, nm);
                if (!File.Exists(f)) continue;
                string o = OwnerOf(f);
                if (o.Length > 0) return o;
            }
            return None;
        }

        // 目录里还有哪些"活的"入口代理（不含 keep 那套）。
        //  切换后必须用它复核，只看 Current() 会漏 —— Current 先看主入口，主入口对了就返回，
        //  而别家方案完全可能躺在**另一个入口名**上：2026-09-18 那台机器就是
        //  方案 A 占 d3d12.dll、0.3.x 占 version.dll，两个槽各一个，Current 只看得出其中一个。
        public static List<string> LiveOthers(string gameDir, string keep)
        {
            List<string> hit = new List<string>();
            try
            {
                string kind = XeMfg.Detect(gameDir);
                string dir = XeMfg.TargetDir(gameDir, kind);
                foreach (string nm in EntryNames)
                {
                    string f = Path.Combine(dir, nm);
                    if (!File.Exists(f)) continue;
                    string owner = OwnerOf(f);
                    if (owner.Length == 0 || owner == keep) continue;
                    hit.Add(nm + "（" + PlanName(owner) + "）");
                }
            }
            catch { }
            return hit;
        }

        // ---- 只读巡检：这个目录里同时躺着几套代理（P0-3） ----
        //  为什么要单独有这条路：Plan.ParkOthers 只在部署 / 切换路径里跑，所以"两代代理已经在
        //  同一个目录里并存"这种状态（2026-09-18 投诉的那个家族）平时**没人检查** ——
        //  而 CP2077 实测就是 0.3.5 的 version.dll 与 0.2.x 的 winmm.dll 同时在位。
        //  不点部署就永远看不见；一按部署又怕它把真正在生效的那个入口给停了。
        //  所以这里把"看"和"动"彻底分开：InspectEntries 只读，ParkForeign 只动**不是当前方案**的那些。
        public class EntryHit
        {
            public string Name = "";          // 目录里的实际文件名
            public string Owner = "";         // opti / 030 / legacy
            public long Size;
            public bool Main;                 // 是不是当前方案占着的那个入口
            public string ActualCode = "";    // 030 那一行按体积反查到的实际版本（其它方案为空）
            public string SignVerdict = "";

            public string Label()
            {
                if (Owner == D030 && ActualCode.Length > 0 && ActualCode != Dlssg030.Ver)
                    return Name + "（DLSS MFG " + ActualCode + "，工具按 " + Dlssg030.Ver + " 发布）";
                return Name + "（" + PlanName(Owner) + "）";
            }
        }

        // 只读。返回这个目录里所有**活的**入口代理（不含 .parked.* 停放件）。
        public static List<EntryHit> InspectEntries(string gameDir)
        {
            var hit = new List<EntryHit>();
            try
            {
                string kind = XeMfg.Detect(gameDir);
                string dir = XeMfg.TargetDir(gameDir, kind);
                string cur = Current(gameDir);
                string entry = EntryName(kind);
                foreach (string nm in EntryNames)
                {
                    string f = Path.Combine(dir, nm);
                    if (!File.Exists(f)) continue;
                    string owner = OwnerOf(f);
                    if (owner.Length == 0) continue;          // 游戏自带同名 dll，体积不在任何窗口里
                    EntryHit h = new EntryHit();
                    h.Name = nm;
                    h.Owner = owner;
                    h.Main = owner == cur && nm == entry;
                    try { h.Size = new FileInfo(f).Length; } catch { }
                    if (owner == D030)
                    {
                        h.ActualCode = Dlssg030.CodeOf(h.Size);
                        if (h.ActualCode.Length == 0) h.ActualCode = Dlssg030.CoarseCodeOf(h.Size);
                        h.SignVerdict = ProxySign.Of(f).Verdict;
                    }
                    hit.Add(h);
                }
            }
            catch { }
            return hit;
        }

        // 目录里已停放的件数（只报数，不列清单 —— 清单太长会把状态行挤掉）
        public static int ParkedCount(string gameDir)
        {
            int n = 0;
            try
            {
                string kind = XeMfg.Detect(gameDir);
                string dir = XeMfg.TargetDir(gameDir, kind);
                foreach (string f in Directory.GetFiles(dir))
                {
                    string nm = Path.GetFileName(f);
                    if (nm.EndsWith(".parked." + Opti, StringComparison.OrdinalIgnoreCase)
                        || nm.EndsWith(".parked." + D030, StringComparison.OrdinalIgnoreCase)
                        || nm.EndsWith(".parked." + Legacy, StringComparison.OrdinalIgnoreCase)) n++;
                }
            }
            catch { }
            return n;
        }

        // 一行巡检结论，给帧生成页与日志共用。
        //  两种要告警的情形，都要说清"该点哪个按钮"，不能只喊"有问题"：
        //   ① 两套以上代理并存 → 抢同一个入口（帧生成会互相打架）→ 停放遗留项；
        //   ② 只有一套、但游戏里那份的版本比资源包旧 → 换了包没更新部署 → 更新部署。
        public static string PatrolLine(string gameDir)
        {
            List<EntryHit> live = InspectEntries(gameDir);
            int parkedN = ParkedCount(gameDir);
            string tail = parkedN > 0 ? "｜ 另有 " + parkedN + " 项已停放（可逆）" : "";
            if (live.Count == 0) return "入口巡检：没有代理在位（游戏跑原生）" + tail;
            var owners = new List<string>();
            foreach (EntryHit h in live)
                if (owners.IndexOf(h.Owner) < 0) owners.Add(h.Owner);
            if (owners.Count >= 2)
            {
                var names = new List<string>();
                foreach (EntryHit h in live) names.Add(h.Label());
                return "⚠ 入口巡检：本目录有 " + live.Count + " 个代理并存 —— "
                     + string.Join(" / ", names.ToArray())
                     + " —— 两套在抢同一个入口，点「停放遗留项」（改名停放，不删文件）" + tail;
            }
            foreach (EntryHit h in live)
                if (h.Owner == D030 && h.ActualCode.Length > 0 && h.ActualCode != Dlssg030.Ver)
                    return "⚠ 入口巡检：" + h.Label() + " —— 游戏里这份和资源包对不上，点「更新部署」重抄" + tail;
            return "入口巡检：" + live[0].Label() + " 单套在位" + tail;
        }

        // 停放"不是当前这套"的活入口代理。keep 传方案 id（一般就是 Current(gameDir) 的结果）。
        //  ⚠ 与 ParkOthers 的区别：这里**不碰**当前生效的那一套的组件（ini / 日志目录），
        //    也不碰游戏自带文件 —— 巡检要的是"只清掉多出来的那一代"。
        public static List<string> ParkForeign(string gameDir, string keep, List<string> failed)
        {
            var parked = new List<string>();
            try
            {
                if (keep == None) return parked;      // 原生状态：没有"该留的那套"，一个都不动
                if (Dlssg.IsLauncherDir(gameDir)) return parked;
                string kind = XeMfg.Detect(gameDir);
                string dir = XeMfg.TargetDir(gameDir, kind);
                foreach (string nm in EntryNames)
                {
                    string f = Path.Combine(dir, nm);
                    if (!File.Exists(f)) continue;
                    string owner = OwnerOf(f);
                    if (owner.Length == 0 || owner == keep) continue;
                    ParkOne(f, owner, false, parked, failed);
                }
            }
            catch { }
            return parked;
        }

        public static string ParkForeignReport(string gameDir)
        {
            List<string> failed = new List<string>();
            List<string> parked = ParkForeign(gameDir, Current(gameDir), failed);
            if (parked.Count == 0 && failed.Count == 0)
                return "没有需要停放的遗留代理（只有当前这一套在位，或本来就是原生状态）";
            return (parked.Count > 0 ? "已停放 " + parked.Count + " 项：" + string.Join("、", parked.ToArray())
                                    : "没有可停放的项")
                 + (failed.Count > 0 ? "\n  ⚠ " + failed.Count + " 项没停成：" + string.Join("、", failed.ToArray()) : "")
                 + "\n  停放是改名（.parked.<方案>），不删文件，随时可还原。";
        }

        // 界面用：按目录里的**真实文件**说清"当前装的哪套、入口叫什么"。
        //  不要用 games.json 里的 Entry 字段 —— 它是上次扫描时的快照，
        //  我们在外面换过文件之后就过期了（2026-09-16 实测：显示"入口 winmm.dll"，
        //  实际生效的是 version.dll，用户看着矛盾）。
        public static string StatusOf(string gameDir)
        {
            try
            {
                string cur = Current(gameDir);
                if (cur == None) return "　未装代理（游戏跑原生）";
                string kind = XeMfg.Detect(gameDir);
                string dir = XeMfg.TargetDir(gameDir, kind);
                foreach (string nm in EntryNames)
                {
                    string f = Path.Combine(dir, nm);
                    if (File.Exists(f) && OwnerOf(f) == cur)
                        return "　已装代理：" + PlanName(cur) + "（入口 " + nm + "）";
                }
                return "　已装代理：" + PlanName(cur);
            }
            catch { return ""; }
        }

        public static string PlanName(string plan)
        {
            if (plan == Opti) return "方案 A · OptiScaler 注入";
            if (plan == D030) return "DLSS MFG " + Dlssg030.Ver;
            //  legacy 必须单列一条：原来只有 Opti / D030 两个分支，legacy 走到最后一条 return，
            //  于是"装着 0.2.x 代理"被说成"原生（所有代理都已停放）"—— 巡检把它并列出来就露馅了
            //  （2026-09-21 plan_probe 第 13 节抓到）。
            if (plan == Legacy) return "旧版 dlssg_for_sm86（0.2.x，上限 4X）";
            return "原生（所有代理都已停放）";
        }

        // 各方案在游戏目录里的"专属组件"（入口 DLL 以外）。
        //  · 方案 A：OptiScaler.ini / fakenvapi.* / libxess*.dll / D3D12_Optiscaler\ 这类，
        //    清单直接取 XeMfg 里部署时用的那两份，不另立一份（两处必然对不上）。
        //  · 0.3.x：只有 ini 与日志目录。nvngx_dlss / nvngx_dlssg 是**游戏自带的运行库**（_backup
        //    里就有覆盖前的备份为证），不属于任何一套方案，绝不停放。
        static void ComponentsOf(string plan, string kind, out string[] files, out string[] dirs)
        {
            if (plan == Opti) { files = XeMfg.ComponentsOf(kind, true); dirs = XeMfg.ComponentsOf(kind, false); }
            else if (plan == D030) { files = new string[] { "dlssg_sm86.ini" }; dirs = new string[] { "dlssg_sm86" }; }
            else { files = new string[0]; dirs = new string[0]; }
        }

        // 改名停放一件（文件或目录）。失败**不吞**：记进 failed 由调用方如实报出来。
        static void ParkOne(string path, string owner, bool isDir, List<string> parked, List<string> failed)
        {
            string dst = path + ".parked." + owner;
            string nm = Path.GetFileName(path);
            try
            {
                if (isDir)
                {
                    if (Directory.Exists(dst)) return;              // 已经停放过了
                    Directory.Move(path, dst);
                }
                else
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(path, dst);
                }
                if (parked != null) parked.Add(nm + " → " + Path.GetFileName(dst));
            }
            catch (Exception ex)
            {
                if (failed != null) failed.Add(nm + "：" + ex.Message);
            }
        }

        // 统一的"停放 / 失败"回执尾巴。四处安装路径共用 —— 各写一遍必然有一处漏报。
        public static string Note(List<string> parked, List<string> failed)
        {
            StringBuilder sb = new StringBuilder();
            if (parked != null && parked.Count > 0)
                sb.Append("\n  已停放另一套方案（只改名，随时可切回）：" + string.Join("；", parked.ToArray()));
            if (failed != null && failed.Count > 0)
                sb.Append("\n  ⚠ 有 " + failed.Count + " 项没停放成：" + string.Join("；", failed.ToArray())
                        + " —— 那套现在仍在生效，完全退出游戏（含启动器）后重试。");
            return sb.ToString();
        }

        // 把所有"不是 keep 方案"的入口代理**与配套组件**改名停放。
        //  failed：收集失败项。以前这里是 `catch { }` 一把吞掉，界面照样报"已切换"，
        //   用户却看着两套插件打架、还得手动再点一次停放 —— 2026-09-18 的投诉就是这个。
        public static List<string> ParkOthers(string gameDir, string kind, string keep, List<string> failed)
        {
            List<string> parked = new List<string>();
            string dir = XeMfg.TargetDir(gameDir, kind);   // kind 可为空（通用游戏）
            string entry = EntryName(kind);
            foreach (string nm in EntryNames)
            {
                string f = Path.Combine(dir, nm);
                if (!File.Exists(f)) continue;
                string owner = OwnerOf(f);
                if (owner.Length == 0) continue;                        // 非代理：不碰
                if (nm.Equals(entry, StringComparison.OrdinalIgnoreCase) && owner == keep) continue;
                ParkOne(f, owner, false, parked, failed);
            }
            // 配套组件：只停"别家"那套的；keep 那套的留原位，切回来时由 UnparkComponents 复原。
            foreach (string plan in new string[] { Opti, D030 })
            {
                if (plan == keep) continue;
                string[] cfs, cds;
                ComponentsOf(plan, kind, out cfs, out cds);
                foreach (string nm in cfs)
                {
                    if (IsEntryName(nm)) continue;                      // 入口由上面那一轮处理
                    string p = Path.Combine(dir, nm);
                    if (File.Exists(p)) ParkOne(p, plan, false, parked, failed);
                }
                foreach (string nm in cds)
                {
                    string p = Path.Combine(dir, nm);
                    if (Directory.Exists(p)) ParkOne(p, plan, true, parked, failed);
                }
            }
            return parked;
        }

        // 把目标方案上次被停放的组件改回原名。与 ParkOthers 严格对称 ——
        //  少了这一半就会"切过去能用、切回来残废"（入口回来了，ini 与运行库还躺在 .parked 里）。
        static List<string> UnparkComponents(string dir, string plan, string kind, List<string> failed)
        {
            List<string> back = new List<string>();
            string[] cfs, cds;
            ComponentsOf(plan, kind, out cfs, out cds);
            foreach (string nm in cfs)
            {
                if (IsEntryName(nm)) continue;                          // 入口由调用方自己复原
                string live = Path.Combine(dir, nm), pk = live + ".parked." + plan;
                if (File.Exists(live) || !File.Exists(pk)) continue;
                try { File.Move(pk, live); back.Add(nm); }
                catch (Exception ex) { if (failed != null) failed.Add(nm + " 复原失败：" + ex.Message); }
            }
            foreach (string nm in cds)
            {
                string live = Path.Combine(dir, nm), pk = live + ".parked." + plan;
                if (Directory.Exists(live) || !Directory.Exists(pk)) continue;
                try { Directory.Move(pk, live); back.Add(nm + "\\"); }
                catch (Exception ex) { if (failed != null) failed.Add(nm + "\\ 复原失败：" + ex.Message); }
            }
            return back;
        }

        // 目标方案的文件是不是都已经在目录里了（齐了就不用重抄，直接改回名字）
        static bool FilesPresent(string dir, string entry, string target)
        {
            try
            {
                string cur = Path.Combine(dir, entry);
                if (target == Opti)
                {
                    bool ent = (File.Exists(cur) && OwnerOf(cur) == Opti) || File.Exists(cur + ".parked." + Opti);
                    return ent && File.Exists(Path.Combine(dir, "fakenvapi.dll"))
                               && File.Exists(Path.Combine(dir, "OptiScaler.ini"));
                }
                bool e2 = (File.Exists(cur) && OwnerOf(cur) == D030) || File.Exists(cur + ".parked." + D030);
                // 通用游戏不覆盖 nvngx（作者文档只要求入口 + ini），所以这里不能强求 nvngx 存在
                bool needNvngx = entry != "version.dll" || File.Exists(Path.Combine(dir, "nvngx_dlssg.dll"));
                return e2 && File.Exists(Path.Combine(dir, "dlssg_sm86.ini")) && needNvngx;
            }
            catch { return false; }
        }

        // 切换到 target（Opti / D030 / None）。只改名，不删除。
        // force：跳过全部"已经在位就不用动"的快速路径，把资源包里的文件**重抄**一遍。
        //  这是"插件包换了新版本"之后必须走的路 —— 否则入口 DLL 还在位，
        //  SwitchTo 会认为无事可做，游戏里跑的还是旧内核。
        public static string SwitchTo(string gameDir, string target)
        {
            return SwitchTo(gameDir, target, false);
        }

        public static string SwitchTo(string gameDir, string target, bool force)
        {
            string kind = XeMfg.Detect(gameDir);
            string dir = XeMfg.TargetDir(gameDir, kind);
            string entry = EntryName(kind);
            string before = Current(gameDir);
            // 启动器根目录护栏（2026-09-18 从被删掉的 Dlssg.Install 搬到这里 —— 现在只有这一条部署路径，
            //  护栏也必须落在这条路上）。装进"容器"目录会同时作用于它下面的所有游戏
            //  （G:\miHoYo Launcher 里躺着原神/绝区零/星铁，且都是内核反作弊，风险面被放大）。
            if (Dlssg.IsLauncherDir(dir))
                return "切换中止：这是「启动器根目录」而不是游戏本体 —— " + dir + "\n"
                     + "  它下面挂着 games\\ 子目录，装进去会同时作用于其中所有游戏，请选中游戏本体那一层。";
            // 游戏还在跑 → 入口 DLL 被占用，改名/覆盖必然失败，而用户只会看到一句"没关掉原来那套"。
            //  这里动手前实测一次占用，比事后报错更早也更准（2026-09-18）。
            if (Dlssg.IsDirBusy(dir))
                return "切换中止：入口 DLL 正被占用（游戏或启动器还在运行？），此刻改名会失败。\n"
                     + "  请完全退出游戏（含启动器）后重试。";
            // 方案 A 只对绝区零 / 鸣潮成立（依赖伪装与专用入口），通用游戏只能走 0.3.x
            if (target == Opti && kind.Length == 0)
                return "方案 A 只支持绝区零 / 鸣潮；其他游戏请用 0.3.x（通用模式）。";

            if (!force && before == target && target != None)
                return "已经就是" + PlanName(target) + "了，无需切换（入口 " + entry + " 未改动）。";

            List<string> failed = new List<string>();
            List<string> parked = ParkOthers(gameDir, kind, target, failed);
            List<string> notes = new List<string>();
            List<string> restored = new List<string>();

            if (target == Opti || target == D030)
            {
                // 先把目标方案的配套件复原，再判断"是否已在位"。顺序不能反：
                //   XeMfg.Install 对已存在的 OptiScaler.ini 是"永不覆盖"（要保住用户调好的伪装与 NR 参数），
                //   晚于它复原就会把出厂值抄进去，把配置冲掉（2026-09-16 那个"帧生成选项消失"就是这条）。
                restored = UnparkComponents(dir, target, kind, failed);

                bool ok = !force && FilesPresent(dir, entry, target);
                string cur = Path.Combine(dir, entry);
                string pk = cur + ".parked." + target;
                if (!force && !File.Exists(cur) && File.Exists(pk))
                {
                    try { File.Move(pk, cur); notes.Add("把停放中的 " + entry + " 改回原名（未重抄，秒完成）"); ok = true; }
                    catch (Exception ex) { failed.Add(entry + " 复原失败：" + ex.Message); }
                }
                if (!ok)
                {
                    string r;
                    if (target == Opti) r = XeMfg.Install(gameDir);
                    else if (kind.Length == 0)
                        r = Dlssg030.InstallGeneric(dir, entry, Program.Cfg.DlssgMaxFrames, force);   // 通用：只放两个文件
                    else r = Dlssg030.Install(gameDir, force);
                    notes.Add(r);
                }
            }

            // ★ 配套文件补齐：入口 DLL 到齐 ≠ 目录完整。
            //   「关闭帧生成」会删掉 dlssg_sm86.ini，而上面那条"把停放的改回原名、秒完成"的快速路径
            //   不重抄任何文件 —— 不在这里补一刀，ini 就永远回不来（2026-09-16 实测的绝区零）。
            if (target == D030) Dlssg030.EnsureFactoryIni(dir);

            string now = Current(gameDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("已切换到：" + PlanName(now) + "（" + entry + " 槽位）");
            sb.Append(Note(parked, failed));
            if (restored.Count > 0)
                sb.AppendLine("  复原（上一轮停放时改过名的配套件）：" + string.Join("；", restored.ToArray()));
            foreach (string s in notes) sb.AppendLine("  " + s.Replace("\n", "\n  "));
            // 结果复核：以目录**现状**为准，不听信中间步骤的自述。
            //  ⚠ 分两个方向查，缺一个就会漏 ——
            //    ① "目标没装上"：Current 不等于目标方案；
            //    ② "别家没关掉"：别家方案的活入口还躺在**其它入口名**上（Current 只看主入口，查不出来）。
            if (now != target)
                sb.AppendLine("  ⚠ 切换未完成：目录现状是「" + PlanName(now) + "」，不是「" + PlanName(target) + "」。"
                            + "多半是游戏或启动器还在运行、DLL 被占用 —— 完全退出后重试。");
            List<string> leftover = LiveOthers(gameDir, target);
            if (leftover.Count > 0)
                sb.AppendLine("  ⚠ 目录里仍有别家方案的活入口：" + string.Join("、", leftover.ToArray())
                            + " —— 两套会一起进进程抢渲染管线。完全退出游戏（含启动器）后重试本操作。");
            sb.AppendLine("  ※ 想切回来：到另一个区块点「切换到本方案」即可，停放的文件会原样改回原名。");
            return sb.ToString().TrimEnd();
        }
    }

    public static class XeMfg
    {
        static readonly string[] ZzzFiles = new string[] {
            "d3d12.dll", "OptiScaler.ini", "fakenvapi.dll", "fakenvapi.ini",
            "libxell.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "Remove_OptiScaler.bat" };
        static readonly string[] ZzzDirs = new string[] { "D3D12_Optiscaler", "Licenses" };
        static readonly string[] WuwaFiles = new string[] {
            "dxgi.dll", "OptiScaler.ini", "nvngx_dlssnr.dll", "nvngx.dll_dlssnr.dll" };
        static readonly string[] WuwaDirs = new string[] { "OptiScaler", "Licenses" };

        // 给 Plan 的"整套停放"用：这套方案在某类游戏目录里放了哪些文件 / 目录。
        //  files=true 取文件清单，false 取目录清单；kind 不是 zzz/wuwa 时返回空（方案 A 只支持这两款）。
        public static string[] ComponentsOf(string kind, bool files)
        {
            if (kind == "zzz") return files ? ZzzFiles : ZzzDirs;
            if (kind == "wuwa") return files ? WuwaFiles : WuwaDirs;
            return new string[0];
        }

        public static string PackRoot { get { return Path.Combine(Program.DataDir, "xess-pack"); } }
        static string PackDir(string kind) { return Path.Combine(PackRoot, kind); }
        static string BackupDir(string kind) { return Path.Combine(BackupRoot, kind); }
        // public：见 Dlssg030.BackupRoot 的说明 —— Junk 扫描要复用，不留第二份 "_backup"
        public static string BackupRoot { get { return Path.Combine(PackRoot, "_backup"); } }
        public static string RemovedRoot { get { return Path.Combine(PackRoot, "_removed"); } }

        // ---- DLSS 5 神经渲染（NR）开关：只改 OptiScaler.ini 里 [DlssNr] Enabled 这一行 ----
        //  为什么值得做成开关：
        //  · NR 每帧都要跑一遍 158 MB 的模型，RTX 20/30 走更重的 FP16 路径；
        //  · 上游文档（wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass，即本 [DlssNr] 段的出处）
        //    自己写明它的若干设计约束「were paid for with device hangs」，且要求驱动 ≥ 616.56；
        //  · 2026-09-16 实测：NR 常开 + 4X MFG 同跑 → 鸣潮两次 GPUCrash
        //    （DXGI_ERROR_DEVICE_REMOVED / DEVICE_HUNG，D3D12Resources.cpp:596）。
        //  关闭它不影响帧生成 —— 只是不跑神经渲染。
        public static int NrEnabled(string gameDir)
        {
            try
            {
                string kind = Detect(gameDir);
                if (kind.Length == 0) return -1;
                string p = Path.Combine(TargetDir(gameDir, kind), "OptiScaler.ini");
                if (!File.Exists(p)) return -1;
                bool ins = false;
                foreach (string l in File.ReadAllLines(p))
                {
                    string s = l.Trim();
                    if (s.StartsWith("[", StringComparison.Ordinal))
                    { ins = string.Equals(s, "[DlssNr]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (ins && s.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) && s.IndexOf('=') >= 0)
                    {
                        string v = s.Substring(s.IndexOf('=') + 1).Trim();
                        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return 1;
                        return 0;   // false / auto / 其它值都按"不主动开"
                    }
                }
            }
            catch { }
            return -1;
        }

        public static string SetNrEnabled(string gameDir, bool on)
        {
            try
            {
                string kind = Detect(gameDir);
                if (kind.Length == 0) return "该游戏不在支持列表（绝区零 / 鸣潮）";
                string p = Path.Combine(TargetDir(gameDir, kind), "OptiScaler.ini");
                if (!File.Exists(p)) return "找不到 OptiScaler.ini（方案 A 未部署？）";
                List<string> lines = new List<string>(File.ReadAllLines(p));
                bool ins = false, hit = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string s = lines[i].Trim();
                    if (s.StartsWith("[", StringComparison.Ordinal))
                    { ins = string.Equals(s, "[DlssNr]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (ins && s.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) && s.IndexOf('=') >= 0)
                    { lines[i] = "Enabled=" + (on ? "true" : "false"); hit = true; break; }
                }
                if (!hit)
                {
                    int at = -1;
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].Trim().Equals("[DlssNr]", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                    if (at < 0) { lines.Add("[DlssNr]"); at = lines.Count - 1; }
                    lines.Insert(at + 1, "Enabled=" + (on ? "true" : "false"));
                }
                File.WriteAllLines(p, lines.ToArray(), new UTF8Encoding(false));
                return on
                    ? "DLSS 5 神经渲染已开启（下一局生效；开着吃性能，且与帧生成同开有设备挂死风险）"
                    : "DLSS 5 神经渲染已关闭（帧生成不受影响）";
            }
            catch (Exception ex) { return "改写失败：" + ex.Message; }
        }


        // 把文件移入隔离区（而不是删除）—— 无备份可还原时的唯一安全做法。
        //  实测证据：鸣潮部署时游戏里本来没有这些文件，所以 `_backup\wuwa` 根本不存在，
        //  原实现走 File.Delete 分支，一次误点就删掉两份 165 MB 的跨代 NR 模型、SM86 运行库
        //  与入口代理，且没有任何还原副本（2026-09-16 核实）。移入隔离区后可原样拷回。
        static void Retire(string srcFile, string quarDir, string relKey, ref int moved)
        {
            try
            {
                if (!File.Exists(srcFile)) return;
                string d = Path.Combine(quarDir, relKey);
                string pd = Path.GetDirectoryName(d);
                if (pd != null && pd.Length > 0) Directory.CreateDirectory(pd);
                if (File.Exists(d)) File.Delete(d);
                File.Move(srcFile, d);
                moved++;
            }
            catch { }
        }


        // 在 gameDir 及其下 ≤depth 层里找 exe，返回其所在目录（鸣潮的 exe 在 3 层深：Client\Binaries\Win64）
        static string FindExeDir(string dir, string exeName, int depth)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exeName))) return dir;
                if (depth <= 0) return null;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string r = FindExeDir(sub, exeName, depth - 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // 识别游戏类型
        public static string Detect(string gameDir)
        {
            if (FindExeDir(gameDir, "ZenlessZoneZero.exe", 3) != null) return "zzz";
            if (FindExeDir(gameDir, "Client-Win64-Shipping.exe", 3) != null) return "wuwa";
            return "";
        }

        // 实际落地目录（exe 所在处）
        public static string TargetDir(string gameDir, string kind)
        {
            // 通用游戏（3A 等）：落地目录就是扫描时认定的 exe 所在目录
            if (kind == null || kind.Length == 0 || (kind != "zzz" && kind != "wuwa")) return gameDir;
            string exe = kind == "zzz" ? "ZenlessZoneZero.exe" : "Client-Win64-Shipping.exe";
            string r = FindExeDir(gameDir, exe, 3);
            return r != null ? r : gameDir;
        }

        static bool EqHead(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        public static bool IsInstalled(string gameDir)
        {
            string kind = Detect(gameDir);
            if (kind.Length == 0) return false;
            return CheckAt(kind, TargetDir(gameDir, kind));
        }

        // 卡片刷新用：直接吃扫描结果里的 Dir/Exe，不再递归找 exe（列表里有十几款游戏，
        // 每张卡片都做一次深度 3 的目录递归会把刷新拖慢到肉眼可见）。
        public static bool IsInstalledQuick(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Exe == null) return false;
            string kind;
            if (EqHead(g.Exe, "ZenlessZoneZero.exe")) kind = "zzz";
            else if (EqHead(g.Exe, "Client-Win64-Shipping.exe")) kind = "wuwa";
            else return false;
            return CheckAt(kind, g.Dir);
        }

        // 判据：入口代理存在（>20 MB，OptiScaler 本体体积）+ 同目录有 OptiScaler.ini
        static bool CheckAt(string kind, string target)
        {
            try
            {
                string probe = kind == "zzz" ? "d3d12.dll" : "dxgi.dll";
                string p = Path.Combine(target, probe);
                if (!File.Exists(p) || !File.Exists(Path.Combine(target, "OptiScaler.ini"))) return false;
                return new FileInfo(p).Length > 20 * 1024 * 1024;
            }
            catch { return false; }
        }

        // 卡片上的短标签：说清是哪个游戏、走哪个入口（用户问过"可接入指的是什么"，
        // 根因就是状态栏只写了泛化词，没写实际方案）
        public static string ShortTag(DlssgGame g)
        {
            if (g == null || g.Exe == null) return "";
            if (EqHead(g.Exe, "ZenlessZoneZero.exe") && CheckAt("zzz", g.Dir))
                return "方案 A 注入（d3d12）";
            if (EqHead(g.Exe, "Client-Win64-Shipping.exe") && CheckAt("wuwa", g.Dir))
                return "方案 A 注入（dxgi）";
            return "";
        }

        public static string Status(string gameDir)
        {
            string kind = Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（仅绝区零 / 鸣潮）";
            string name = kind == "zzz" ? "绝区零" : "鸣潮";
            if (IsInstalled(gameDir)) return "已部署（" + name + " · " + (kind == "zzz" ? "d3d12.dll 入口，DLSSG→XeFG" : "dxgi.dll 入口，DLSSG 进出") + "）";
            if (!Directory.Exists(PackDir(kind))) return "资源包缺失（应位于 " + PackDir(kind) + "）";
            return "未部署（" + name + "）";
        }

        public static string Install(string gameDir)
        {
            string kind = Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（仅绝区零 / 鸣潮）";
            if (IsInstalled(gameDir)) return "已处于部署状态，无需重复部署";
            string src = PackDir(kind);
            if (!Directory.Exists(src)) return "资源包缺失：" + src + "（请把 xess-pack 放入数据目录）";
            List<string> failed = new List<string>();
            List<string> parked = Plan.ParkOthers(gameDir, kind, Plan.Opti, failed);   // 先把别家方案整套停放
            string dst = TargetDir(gameDir, kind);
            string[] files = kind == "zzz" ? ZzzFiles : WuwaFiles;
            string[] dirs = kind == "zzz" ? ZzzDirs : WuwaDirs;
            string bk = BackupDir(kind);
            int copied = 0, backed = 0, kept = 0;
            try
            {
                foreach (var f in files)
                {
                    string s = Path.Combine(src, f);
                    if (!File.Exists(s)) return "资源包缺文件：" + f;
                    string d = Path.Combine(dst, f);
                    // OptiScaler.ini 永不覆盖：里面装着用户调好的 Spoofing/Dxgi 与 [DlssNr] 参数，
                    //  资源包里那份是出厂值（Dxgi=auto / DlssNr=auto），盖上去 =
                    //  帧生成选项消失 + NR 回到未启用。要恢复出厂值请手动删掉该文件。2026-09-16。
                    if (f == "OptiScaler.ini" && File.Exists(d)) { kept++; continue; }
                    if (File.Exists(d))                      // 覆盖前备份（游戏自带件保护）
                    {
                        Directory.CreateDirectory(bk);
                        File.Copy(d, Path.Combine(bk, f), true);
                        backed++;
                    }
                    File.Copy(s, d, true);
                    copied++;
                }
                foreach (var dir in dirs)
                {
                    string s = Path.Combine(src, dir);
                    if (!Directory.Exists(s)) continue;
                    foreach (var sf in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
                    {
                        string rel = sf.Substring(s.Length).TrimStart('\\');
                        string d = Path.Combine(dst, dir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(d));
                        if (File.Exists(d))
                        {
                            string b = Path.Combine(bk, dir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(b));
                            File.Copy(d, b, true);
                            backed++;
                        }
                        File.Copy(sf, d, true);
                        copied++;
                    }
                }
            }
            catch (Exception ex) { return "部署失败：" + ex.Message; }
            // 假成功校验：代理本体必须落盘且体积正确
            if (!IsInstalled(gameDir)) return "部署异常：代理文件未正确落盘，请检查磁盘空间与杀软拦截";
            int leftovers = Dlssg030.CleanLeftovers(gameDir);   // 双向互斥：顺手清掉 0.3.x 的残留
            return "已部署 " + copied + " 个文件（覆盖前备份 " + backed + " 个游戏自带件"
                 + (kept > 0 ? "，保留你已调好的 OptiScaler.ini" : "")
                 + (leftovers > 0 ? "，清理 0.3.x 残留 " + leftovers + " 个" : "") + "）→ "
                 + (kind == "zzz" ? "绝区零已启用 方案 A 注入（伪装 5090 · DLSSG→XeFG）"
                                  : "鸣潮已启用 方案 A 注入（dxgi 入口 · 原生 DLSSG + DLSS 5 NR）")
                 + "\n重启游戏生效；游戏内按 F10 呼出 OptiScaler 菜单（若无效请改用启动器直连 exe 启动）。"
                 + Plan.Note(parked, failed);
        }

        // v2.10.7 起「卸载」改名为「停放」：只做改名，**不删除任何文件**，
        //  所以点错了也只是回到原生状态，去另一个区块点「切换到本方案」就能立刻回来。
        public static string Park(string gameDir) { return Plan.SwitchTo(gameDir, Plan.None); }

        // 旧实现（会移走文件）保留在此仅供追溯，已无调用点。
        static string Park_old_not_used(string gameDir)
        {
            string kind = Detect(gameDir);
            if (kind.Length == 0) return "该游戏不在支持列表（仅绝区零 / 鸣潮）";
            string dst = TargetDir(gameDir, kind);
            string bk = BackupDir(kind);
            string[] files = kind == "zzz" ? ZzzFiles : WuwaFiles;
            string[] dirs = kind == "zzz" ? ZzzDirs : WuwaDirs;
            int removed = 0, restored = 0, kept = 0;
            string quar = Path.Combine(RemovedRoot,
                                       kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try
            {
                foreach (var f in files)
                {
                    // OptiScaler.ini 是**用户调参的载体**（Spoofing/Dxgi 开关、[DlssNr] 参数都在里面）。
                    //  卸载时原位保留：没有入口代理时它不会被读取，留着是为了下次重装能接着用 ——
                    //  否则重装会用资源包里的出厂值（Dxgi=auto / DlssNr=auto）覆盖，
                    //  帧生成选项会再次消失，NR 也不再明确启用。2026-09-16 实测踩到。
                    if (f == "OptiScaler.ini") { kept++; continue; }
                    string d = Path.Combine(dst, f);
                    string b = Path.Combine(bk, f);
                    if (File.Exists(b)) { File.Copy(b, d, true); restored++; }
                    else Retire(d, quar, f, ref removed);
                }
                foreach (var dir in dirs)
                {
                    string s = Path.Combine(PackDir(kind), dir);
                    if (!Directory.Exists(s)) continue;
                    foreach (var sf in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
                    {
                        string rel = sf.Substring(s.Length).TrimStart('\\');
                        string d = Path.Combine(dst, dir, rel);
                        string b = Path.Combine(bk, dir, rel);
                        if (File.Exists(b)) { File.Copy(b, d, true); restored++; }
                        else Retire(d, quar, Path.Combine(dir, rel), ref removed);
                    }
                }
                // 清掉运行日志与残留（反作弊改名残留 version.dll.<数字> 一并清）
                try { File.Delete(Path.Combine(dst, "OptiScaler.log")); } catch { }
                try
                {
                    foreach (var p in Directory.GetFiles(dst, "version.dll.*"))
                        try { File.Delete(p); } catch { }
                }
                catch { }
            }
            catch (Exception ex) { return "停放失败：" + ex.Message; }
            return "已停放：移入隔离区 " + removed + " 个文件，恢复游戏自带件 " + restored + " 个，保留你的配置 " + kept + " 个"
                 + " → 重启游戏即回到原生状态"
                 + (removed > 0 ? "\n  隔离区（需要时把里面文件拷回游戏目录即可完全还原）：" + quar : "");
        }
    }

    internal static class D3d12ProxyBlob
    {
        public const int Size = 3584;

        const string B64 =
            "TVqQAAMAAAAEAAAA//8AALgAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAFRoaXMgcHJv" +
            "Z3JhbSBjYW5ub3QgYmUgcnVuIGluIERPUyBtb2RlLg0KJAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQRQAAZIYDAAAAAAAAAAAA" +
            "AAAAAPAAIiALAg4AAAAAAAAMAAAAAAAAAAAAAAAAAAAAAACAAQAAAAAQAAAAAgAABgAAAAAAAAAGAAAAAAAAAABAAAAAAgAA" +
            "AAAAAAIAQAEAABAAAAAAAAAQAAAAAAAAAAAQAAAAAAAAEAAAAAAAAAAAAAAQAAAAABAAAN4FAADeFQAAWQAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAwAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "ACAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAucmRhdGEAADcGAAAAEAAAAAgAAAACAAAAAAAAAAAAAAAAAABAAABA" +
            "LmRhdGEAAAAQAAAAACAAAAACAAAACgAAAAAAAAAAAAAAAAAAQAAAwC5yZWxvYwAADAAAAAAwAAAAAgAAAAwAAAAAAAAAAAAA" +
            "AAAAAEAAAEIAAAAAAAAAAAAAAADcEAAAAQAAABIAAAASAAAAKBAAAHAQAAC4EAAA2xIAAAcTAAA0EwAAWxMAAHwTAACwEwAA" +
            "7RMAABsUAABKFAAAcBQAAJEUAAC7FAAA4BQAABEVAAA2FQAAYRUAAJUVAAC1FQAA5hAAAAMRAAAhEQAAOREAAEsRAABwEQAA" +
            "nhEAAL0RAADdEQAA9BEAAAYSAAAhEgAANxIAAFkSAABvEgAAixIAALASAADBEgAAAAABAAIAAwAEAAUABgAHAAgACQAKAAsA" +
            "DAANAA4ADwAQABEAZDNkMTIuZGxsAEQzRDEyQ29yZUNyZWF0ZUxheWVyZWREZXZpY2UARDNEMTJDb3JlR2V0TGF5ZXJlZERl" +
            "dmljZVNpemUARDNEMTJDb3JlUmVnaXN0ZXJMYXllcnMARDNEMTJDcmVhdGVEZXZpY2UARDNEMTJDcmVhdGVSb290U2lnbmF0" +
            "dXJlRGVzZXJpYWxpemVyAEQzRDEyQ3JlYXRlVmVyc2lvbmVkUm9vdFNpZ25hdHVyZURlc2VyaWFsaXplcgBEM0QxMkRldmlj" +
            "ZVJlbW92ZWRFeHRlbmRlZERhdGEARDNEMTJFbmFibGVFeHBlcmltZW50YWxGZWF0dXJlcwBEM0QxMkdldERlYnVnSW50ZXJm" +
            "YWNlAEQzRDEyR2V0SW50ZXJmYWNlAEQzRDEyUElYRXZlbnRzUmVwbGFjZUJsb2NrAEQzRDEyUElYR2V0VGhyZWFkSW5mbwBE" +
            "M0QxMlBJWE5vdGlmeVdha2VGcm9tRmVuY2VTaWduYWwARDNEMTJQSVhSZXBvcnRDb3VudGVyAEQzRDEyU2VyaWFsaXplUm9v" +
            "dFNpZ25hdHVyZQBEM0QxMlNlcmlhbGl6ZVZlcnNpb25lZFJvb3RTaWduYXR1cmUAR2V0QmVoYXZpb3JWYWx1ZQBTZXRBcHBD" +
            "b21wYXRTdHJpbmdQb2ludGVyAGQzZDEyX29yaWcuZGxsLkQzRDEyQ29yZUNyZWF0ZUxheWVyZWREZXZpY2UAZDNkMTJfb3Jp" +
            "Zy5kbGwuRDNEMTJDb3JlR2V0TGF5ZXJlZERldmljZVNpemUAZDNkMTJfb3JpZy5kbGwuRDNEMTJDb3JlUmVnaXN0ZXJMYXll" +
            "cnMAZDNkMTJfb3JpZy5kbGwuRDNEMTJDcmVhdGVEZXZpY2UAZDNkMTJfb3JpZy5kbGwuRDNEMTJDcmVhdGVSb290U2lnbmF0" +
            "dXJlRGVzZXJpYWxpemVyAGQzZDEyX29yaWcuZGxsLkQzRDEyQ3JlYXRlVmVyc2lvbmVkUm9vdFNpZ25hdHVyZURlc2VyaWFs" +
            "aXplcgBkM2QxMl9vcmlnLmRsbC5EM0QxMkRldmljZVJlbW92ZWRFeHRlbmRlZERhdGEAZDNkMTJfb3JpZy5kbGwuRDNEMTJF" +
            "bmFibGVFeHBlcmltZW50YWxGZWF0dXJlcwBkM2QxMl9vcmlnLmRsbC5EM0QxMkdldERlYnVnSW50ZXJmYWNlAGQzZDEyX29y" +
            "aWcuZGxsLkQzRDEyR2V0SW50ZXJmYWNlAGQzZDEyX29yaWcuZGxsLkQzRDEyUElYRXZlbnRzUmVwbGFjZUJsb2NrAGQzZDEy" +
            "X29yaWcuZGxsLkQzRDEyUElYR2V0VGhyZWFkSW5mbwBkM2QxMl9vcmlnLmRsbC5EM0QxMlBJWE5vdGlmeVdha2VGcm9tRmVu" +
            "Y2VTaWduYWwAZDNkMTJfb3JpZy5kbGwuRDNEMTJQSVhSZXBvcnRDb3VudGVyAGQzZDEyX29yaWcuZGxsLkQzRDEyU2VyaWFs" +
            "aXplUm9vdFNpZ25hdHVyZQBkM2QxMl9vcmlnLmRsbC5EM0QxMlNlcmlhbGl6ZVZlcnNpb25lZFJvb3RTaWduYXR1cmUAZDNk" +
            "MTJfb3JpZy5kbGwuR2V0QmVoYXZpb3JWYWx1ZQBkM2QxMl9vcmlnLmRsbC5TZXRBcHBDb21wYXRTdHJpbmdQb2ludGVyABsW" +
            "AAAAAAAAAAAAACsWAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAERpcmVjdElucHV0OENyZWF0ZQAGFgAAAAAAAAAAAAAA" +
            "AAAAZGlucHV0OC5kbGwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAYWAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAwAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        public static byte[] Bytes()
        {
            return Convert.FromBase64String(B64);
        }
    }
}
