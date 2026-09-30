using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Fluxion
{
    // ============================================================================
    //  游戏库 v2.5.0：平台扫描 + 官方游戏名 + 库标记
    //  --------------------------------------------------------------------------
    //  移植自用户自研启动器 D:\杂项\GameLauncher（Python），逐函数对照改写：
    //    scanner.py → SteamLib（appmanifest 官方名）/ RegistryEntries（平台分类）
    //                 / ScanEpic / ScanUbisoft / ScanEA / Helper / FindExe / Genre2D
    //    images.py  → CoverArt（见文件下半部分）
    //  保持 C# 5 语法（csc 4.0.30319 编译），不引入任何外部依赖。
    // ============================================================================

    // ---- 库标记（收藏/私密/隐藏/重命名/最近游玩）----
    //  刻意与 games.json 分开存（library.json），避免动到现有的忽略清单/自定义游戏逻辑。
    public class GameMeta
    {
        public string Key = "";          // 规范化主程序路径（无 Exe 时用目录）
        public string Name = "";         // 重命名（空 = 用扫描到的官方名）
        public string Platform = "";
        public string AppId = "";
        public bool Favorite;
        public bool Private;
        public bool Hidden;
        public long LastPlayed;          // unix 秒
        public string LaunchArgs = "";   // 自定义启动参数（如 -force-d3d12，见 SetDx12）
        public string LaunchExe = "";    // 手动指认的启动 exe（自动定位不到时选一次，之后永久生效）
    }

    public static class MetaStore
    {
        public static string MetaPath { get { return Path.Combine(Program.DataDir, "library.json"); } }

        // 键 = 主程序完整路径（小写规范化）；没有 Exe 时退回目录
        public static string KeyOf(DlssgGame g)
        {
            if (g == null) return "";
            string exe = (g.Exe == null ? "" : g.Exe);
            string p = exe.Length > 0 ? Path.Combine(g.Dir == null ? "" : g.Dir, exe) : (g.Dir == null ? "" : g.Dir);
            return Norm(p);
        }

        public static string Norm(string p)
        {
            if (p == null) return "";
            try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
        }

        // 最近一次成功解析的全量数据：library.json 一旦损坏（截断/写坏），直接拿空表去 Save
        // 会把整库静默重写成单条 —— 收藏/隐藏/重命名/指认的启动 exe 全部丢失（2026-09-30 体检 high）。
        // 规则：解析失败 → 先试 .bak（SaveAll 的 File.Replace 留下的上一版），再退这里，绝不用空表覆盖旧库。
        static Dictionary<string, GameMeta> lastGood;
        // library.json 的读-改-写有 UI 线程（收藏/启动）与后台线程（扫描/批量封面）并发，
        // 无锁时后写者会用旧快照覆盖先写者（2026-09-30 体检）。lock 可重入，Save 里套 Load/SaveAll 没问题。
        static readonly object ioLock = new object();

        public static Dictionary<string, GameMeta> Load()
        {
            lock (ioLock)
            {
                var map = TryRead(MetaPath);
                if (map == null && File.Exists(MetaPath))
                {
                    Program.Log("library.json 解析失败，尝试上一版备份 library.json.bak");
                    map = TryRead(MetaPath + ".bak");
                }
                if (map != null) lastGood = map;
                else map = lastGood;
                return map ?? new Dictionary<string, GameMeta>();
            }
        }

        // 读一个 json 并反序列化。文件不存在返回 null（首次运行是常态，不算错）；坏了/被占用也返回 null（留日志）。
        static Dictionary<string, GameMeta> TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return null;
                var map = new Dictionary<string, GameMeta>();
                foreach (var kv in root)
                {
                    var d = kv.Value as Dictionary<string, object>;   // 注意：JavaScriptSerializer 的嵌套对象是 Dictionary
                    if (d == null) continue;
                    var m = new GameMeta();
                    m.Key = kv.Key;
                    m.Name = S(d, "name");
                    m.Platform = S(d, "platform");
                    m.AppId = S(d, "appid");
                    m.Favorite = B(d, "favorite");
                    m.Private = B(d, "private");
                    m.Hidden = B(d, "hidden");
                    m.LastPlayed = L(d, "lastPlayed");
                    m.LaunchArgs = S(d, "launchArgs");
                    m.LaunchExe = S(d, "launchExe");
                    map[kv.Key] = m;
                }
                return map;
            }
            catch (Exception ex) { Program.Log("library.json 读取失败(" + Path.GetFileName(path) + "): " + ex.Message); return null; }
        }

        public static void SaveAll(Dictionary<string, GameMeta> map)
        {
            lock (ioLock)
            {
                try
                {
                    var root = new Dictionary<string, object>();
                    foreach (var kv in map)
                    {
                        var m = kv.Value;
                        var d = new Dictionary<string, object>();
                        d["name"] = m.Name; d["platform"] = m.Platform; d["appid"] = m.AppId;
                        d["favorite"] = m.Favorite; d["private"] = m.Private; d["hidden"] = m.Hidden;
                        d["lastPlayed"] = m.LastPlayed;
                        d["launchArgs"] = m.LaunchArgs;
                        d["launchExe"] = m.LaunchExe;
                        root[kv.Key] = d;
                    }
                    Directory.CreateDirectory(Program.DataDir);
                    // 原子写：先写临时文件再 File.Replace（原子替换并自动留下 .bak 上一版）。
                    // 直接 WriteAllText 在崩溃/断电/磁盘满时会留下截断 JSON，并发读方也可能读到坏文件
                    //（2026-09-30 体检 high）。失败时原文件原样保留，不存在"写了一半"的中间态。
                    string tmp = MetaPath + ".tmp";
                    File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
                    if (File.Exists(MetaPath)) File.Replace(tmp, MetaPath, MetaPath + ".bak");
                    else File.Move(tmp, MetaPath);
                }
                catch (Exception ex) { Program.Log("library.json 写入失败: " + ex.Message); }
            }
        }

        public static GameMeta GetOrCreate(string key)
        {
            var map = Load();
            GameMeta m;
            if (map.TryGetValue(key, out m)) return m;
            m = new GameMeta();
            m.Key = key;
            return m;
        }

        public static void Save(GameMeta m)
        {
            if (m == null || m.Key.Length == 0) return;
            lock (ioLock)
            {
                var map = Load();
                map[m.Key] = m;
                SaveAll(map);
            }
        }

        public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // 手动指认的启动 exe（LaunchTargetOf 的最高优先级来源）。文件已不存在视为没指认过。
        public static string LaunchExeOf(DlssgGame g)
        {
            try
            {
                if (g == null) return "";
                GameMeta m;
                if (!Load().TryGetValue(KeyOf(g), out m)) return "";
                string p = m.LaunchExe == null ? "" : m.LaunchExe.Trim();
                return (p.Length > 0 && File.Exists(p)) ? p : "";
            }
            catch { return ""; }
        }
        // 记住用户指认的启动文件。返回 "" = 成功，其他 = 失败原因（界面直接打日志）。
        public static string SetLaunchExe(DlssgGame g, string exePath)
        {
            try
            {
                if (g == null || exePath == null || !File.Exists(exePath)) return "无效的启动文件";
                GameMeta m = GetOrCreate(KeyOf(g));
                m.LaunchExe = exePath;
                Save(m);
                return "";
            }
            catch (Exception ex) { return "启动文件保存失败: " + ex.Message; }
        }

        static string S(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return "";
            return d[k].ToString();
        }
        static bool B(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return false;
            object v = d[k];
            if (v is bool) return (bool)v;
            string s = v.ToString();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        static long L(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return 0;
            object v = d[k];
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
    }

    // ---- Steam 库（appmanifest = 官方游戏名 + 全部库路径）----
    public class SteamApp
    {
        public string AppId = "";
        public string Name = "";
        public string InstallDir = "";
        public string Dir = "";          // 游戏实际目录（可能为空）
    }

    public static class SteamLib
    {
        public static List<SteamApp> Scan()
        {
            var apps = new List<SteamApp>();
            try
            {
                string steam = RegS(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                if (steam.Length == 0 || !Directory.Exists(steam)) steam = @"C:\Program Files (x86)\Steam";
                // SteamPath 可能是正斜杠写法，归一后再拼路径（理由见 Lib.BackSlashes）。
                //  其它库的路径来自 libraryfolders.vdf，本来就是反斜杠 ——
                //  所以只有**主库**（Steam 安装目录）里的游戏中招。
                steam = Lib.BackSlashes(steam);
                if (!Directory.Exists(steam)) return apps;

                var libs = new List<string>();
                libs.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(ReadText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (p.Length > 0 && !libs.Contains(p)) libs.Add(p);
                    }

                var seenApp = new List<string>();
                foreach (string lib in libs)
                {
                    string sa = Path.Combine(lib.TrimEnd('\\', '/'), "steamapps");
                    if (!Directory.Exists(sa)) continue;
                    string[] acfs;
                    try { acfs = Directory.GetFiles(sa, "appmanifest_*.acf"); }
                    catch { continue; }
                    foreach (string f in acfs)
                    {
                        try
                        {
                            string t = ReadText(f);
                            string appid = Vdf(t, "appid");
                            string name = Vdf(t, "name");
                            string idir = Vdf(t, "installdir");
                            if (appid.Length == 0 || name.Length == 0 || appid == "228983") continue;
                            if (PlatformScan.NonGame(name)) continue;
                            if (seenApp.Contains(appid)) continue;
                            seenApp.Add(appid);
                            var a = new SteamApp();
                            a.AppId = appid; a.Name = name; a.InstallDir = idir;
                            string d = idir.Length > 0 ? Path.Combine(sa, "common", idir) : lib;
                            a.Dir = Directory.Exists(d) ? d : "";
                            apps.Add(a);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return apps;
        }

        // VDF/ACF 里取 "key" "value"
        static string Vdf(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static string ReadText(string p)
        {
            try { return File.ReadAllText(p, Encoding.UTF8); } catch { return ""; }
        }

        public static string RegS(RegistryKey hive, string path, string name)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(path))
                {
                    if (k == null) return "";
                    object v = k.GetValue(name);
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }
    }

    // ---- 各平台扫描 + 分类（对照 scanner.py）----
    public static class PlatformScan
    {
        // 辅助程序（卸载器 / 崩溃上报 / 运行库 / 编辑器自带工具），不作为游戏主程序
        // 末尾一批是实测踩到的：CS2 目录里有 18.9MB 的 source1import.exe 比正主 cs2.exe(2.9MB) 还大，
        // 无畏契约目录里 ffmpeg.exe 同理 —— 纯按体积挑必然挑错。
        static readonly Regex HelperPat = new Regex(
            @"(unins|crash|redist|vcredist|dxsetup|dotnet|battleye|easyanticheat|^eac|" +
            @"setup$|^setup|install$|_install|update$|_updater$|^updater|patcher|" +
            @"language|speech|sentry|report|helper|prereq|physx|xinput|openal|" +
            @"^launcher$|prelauncher|safemode|卸载|登录器|" +
            @"source1import|cs_mdl_import|vconsole|resourceinfo|resourcecopy|import_map|" +
            @"ffmpeg|ffplay|ffprobe|tinydl|msdkweblauncher|^assistant$|crashpad|elevate|touchup|" +
            @"^obs|streamlabs|^7z|winrar|^vlc|mediainfo)", RegexOptions.IgnoreCase);

        // 非游戏条目（注册表卸载项 / Steam appmanifest 名称过滤）
        static readonly Regex NonGamePat = new Regex(
            @"(visual c\+\+|vcredist|directx|\.net|runtime|redistributable|redist|运行库|" +
            @"\bsdk\b|\bdriver\b|驱动|更新|update|upgrade|hotfix|\bkb\d+|" +
            @"microsoft|windows|internet explorer|microsoft edge|office|vs code|visual studio|" +
            @"python|java\B|jdk|node\.js|git\b|git for windows|tortoise|" +
            @"nvidia|geforce|amdgpu|radeon|\bamd\b|intel\b|realtek|qualcomm|bluetooth|audio|chipset|" +
            @"\bqq\b|微信|wechat|weixin|腾讯会议|百度网盘|网易云音乐|腾讯视频|爱奇艺|优酷|哔哩|bilibili|" +
            @"wps|7-?zip|winrar|bandizip|everything|potplayer|\bvlc\b|火绒|360|压缩|鲁大师|" +
            @"adobe|photoshop|discord|spotify|telegram|" +
            @"^steam$|steamworks|proton|steam client|epic games launcher|epic online services|" +
            @"^wegame$|腾讯 weg? ?ame|wegame助手|gog galaxy|battle\.net$|blizzard battle|" +
            @"ubisoft connect|ubisoft game launcher|ea app|^origin$|ea app installer|" +
            @"launcher$|启动器$|dxweb|physx|openal|xlive|games for windows|" +
            @"uninstall|卸载|docs?\.|readme|" +
            @"clash|\bverge\b|notepad|chrome|firefox|thunder|迅雷|internet download|\bidm\b|" +
            @"obsidian|jetbrains|pycharm|intellij|android studio|ollama|powertoys|power toys|" +
            @"网盘|夸克|quark|迅游|加速器|\bboost\b|kimi\b|gemini\b|copilot|cherry studio|" +
            @"antigravity|trae\b|codebuddy|workbuddy|\bbuddy\b|akko|图吧|\biot\b|iot_|" +
            @"inno setup|compil|package cache|context menu|winhance|snap\.hutao|hutao|" +
            @"watt toolkit|steam\+\+|oopz|tiez|自考|工作台|创作|荣耀互联|hihonor|gameviewer|" +
            @"uu远程|qqmail|qq音乐|qqmusic|anticheat|anti-cheat|plugin|助手|" +
            // 对战平台 / 匹配平台（用户实测点名：5EClient、perfectworldarena = CS2 的对战平台，
            //  不是游戏本身；以前它们会以"电竞平台"的名义混进游戏库，2026-09-16）
            @"\b5e(client|box|对战|平台)|perfectworld|完美世界|对战平台|电竞平台|" +
            // 桌面美化 / 壁纸类软件：Steam 上架了但不是游戏（Wallpaper Engine / MyDockFinder），
            //   以前会混进游戏库 → wallpaper64.exe 被"检测到游戏启动"→ 桌面上空跑加速包+卓越性能+0.5ms
            //   定时器（2026-09-27/28 日志实测连开一整天）
            @"wallpaper\s*engine|wallpaper32|wallpaper64|mydockfinder|mydock\b|" +
            @"\bdlss\b|gameboost|gamecenter)", RegexOptions.IgnoreCase);

        // 二次元关键词（匹配 名称 / 路径 / exe）
        static readonly string[] Genre2D = new string[] {
            "原神", "genshin", "yuanshen", "星穹铁道", "starrail", "崩坏", "honkai", "bh3",
            "绝区零", "zenless", "zzz", "mihoyo", "hoyoplay", "hoyoverse", "米哈游",
            "鸣潮", "wuthering", "kuro", "库洛", "战双", "punishing",
            "明日方舟", "终末地", "endfield", "深空之眼", "尘白禁区", "snowbreak",
            "重返未来", "reverse:1999", "reverse1999", "无期迷途", "白夜极光",
            "蔚蓝档案", "bluearchive", "nikke", "胜利女神", "少女前线", "少前",
            "碧蓝航线", "azurlane", "未定事件簿", "光与夜之恋", "恋与深空",
            "loveanddeepspace", "世界之外", "如鸢", "物华弥新", "千年之旅",
            "新月同行", "异环", "蓝色星原", "二重螺旋", "宿命回响",
            "尸姬", "galgame", "黄油"
        };

        // 战网 uid -> 名称
        static readonly string[][] BattleNames = new string[][] {
            new string[] { "pro", "暗黑破坏神 IV" }, new string[] { "d3", "暗黑破坏神 III" },
            new string[] { "d2os", "暗黑破坏神 II：重制版" }, new string[] { "wow", "魔兽世界" },
            new string[] { "w3", "魔兽争霸 III：重制版" }, new string[] { "osi", "炉石传说" },
            new string[] { "s2", "星际争霸 II" }, new string[] { "hero", "风暴英雄" },
            new string[] { "anbs", "暗黑破坏神：不朽" }
        };

        // 米哈游启动器注册表键名 -> 主程序名提示（用于从启动器根目录定位游戏本体）
        static readonly string[][] MiHoYoHints = new string[][] {
            new string[] { "hk4e", "yuanshen" }, new string[] { "hkrpg", "starrail" },
            new string[] { "nap_cn", "zenlesszonezero" }, new string[] { "bh3", "bh3" }
        };

        public static bool NonGame(string name) { try { return NonGamePat.IsMatch(name == null ? "" : name); } catch { return false; } }
        public static bool IsHelper(string stem) { try { return HelperPat.IsMatch(stem == null ? "" : stem); } catch { return false; } }

        public static bool Genre2DHit(string name, string path)
        {
            string blob = ((name == null ? "" : name) + " " + (path == null ? "" : path)).ToLowerInvariant();
            foreach (string k in Genre2D) if (blob.Contains(k)) return true;
            return false;
        }

        public static string NormName(string n) { return Regex.Replace((n == null ? "" : n).ToLowerInvariant(), @"[\s:：·\-_—]+", ""); }

        public static bool IsHelperExe(string path)
        {
            try { return IsHelper(Path.GetFileNameWithoutExtension(path)); } catch { return false; }
        }

        // 从 DisplayIcon 取 exe（"C:\a\b.exe,0" 形式）
        public static string ExeFromIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return "";
            string p = icon.Split(',')[0].Trim().Trim('"');
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
            if (p.ToLowerInvariant().EndsWith(".exe") && File.Exists(p)) return p;
            return "";
        }

        // 找主程序时要跳过的目录（redist / directx / uninstall 这类，里面全是自带组件与卸载器）。
        // internal 而非 private：Lib.LaunchTargetOf 的浅层查找要用同一份表 —— 两处各写一份，
        //   将来加了一条就会"扫描时跳过、启动时命中"，出问题极难查。
        internal static readonly string[] SkipDirs = new string[] { "redist", "_commonredist", "commonredist", "directx", "dotnet", "uninstall", "extras", "__macosx", "plugins" };

        // 目录内挑主程序：名称匹配(3) > 提示名(2) > UE Shipping(1) > 体积最大
        public static string FindExe(string installDir, string hint, string name)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return "";
            var cands = new List<string>();
            var launchers = new List<string>();
            try
            {
                var stack = new List<string[]>();
                stack.Add(new string[] { installDir, "0" });
                while (stack.Count > 0)
                {
                    string[] cur = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    string root = cur[0];
                    int depth = int.Parse(cur[1]);
                    if (root != installDir && depth >= 4) continue;
                    string[] subs;
                    try { subs = Directory.GetDirectories(root); } catch { subs = new string[0]; }
                    foreach (string s in subs)
                    {
                        string bn = Path.GetFileName(s).ToLowerInvariant();
                        bool skip = false;
                        foreach (string sk in SkipDirs) if (bn == sk) { skip = true; break; }
                        if (!skip) stack.Add(new string[] { s, (depth + 1).ToString() });
                    }
                    string[] fs;
                    try { fs = Directory.GetFiles(root, "*.exe"); } catch { fs = new string[0]; }
                    foreach (string f in fs)
                    {
                        string stem = Path.GetFileNameWithoutExtension(f);
                        if (Regex.IsMatch(stem, "^(pre)?launcher$", RegexOptions.IgnoreCase)) launchers.Add(f);
                        else cands.Add(f);
                    }
                }
            }
            catch { }

            var good = new List<string>();
            foreach (string p in cands) if (!IsHelperExe(p)) good.Add(p);
            if (good.Count == 0) good = launchers;
            if (good.Count == 0) return "";

            string token = NormName(name);
            string hintL = (hint == null ? "" : hint).ToLowerInvariant();
            string best = "";
            int bestTier = -1;
            long bestSize = -1;
            int bestDepth = 999;
            foreach (string p in good)
            {
                string stem = NormName(Path.GetFileNameWithoutExtension(p));
                int tier = 0;
                if (stem.Length > 2 && token.Length > 0 && (stem.Contains(token) || token.Contains(stem))) tier = 3;
                else if (hintL.Length > 0 && stem.Contains(hintL)) tier = 2;
                else if (stem.Contains("win64shipping") || stem.EndsWith("shipping")) tier = 1;
                long sz = 0;
                try { sz = new FileInfo(p).Length; } catch { }
                int dep = p.Split('\\').Length;
                if (tier > bestTier || (tier == bestTier && sz > bestSize)
                    || (tier == bestTier && sz == bestSize && dep < bestDepth))
                { best = p; bestTier = tier; bestSize = sz; bestDepth = dep; }
            }
            if (hintL.Length > 0 && bestTier == 0) return "";   // 有提示名但没命中 → 宁缺勿错
            return best;
        }

        // ---------- Epic ----------
        public static List<DlssgGame> Epic()
        {
            var outl = new List<DlssgGame>();
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                           @"Epic\EpicGamesLauncher\Data\Manifests");
                if (!Directory.Exists(root)) return outl;
                var ser = new JavaScriptSerializer();
                foreach (string f in Directory.GetFiles(root, "*.item"))
                {
                    try
                    {
                        var d = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8));
                        if (d == null) continue;
                        string name = K(d, "DisplayName");
                        string loc = K(d, "InstallLocation");
                        string rel = K(d, "LaunchExecutable");
                        if (name.Length == 0 || loc.Length == 0 || !Directory.Exists(loc)) continue;
                        if (NonGame(name)) continue;
                        var g = new DlssgGame();
                        g.Dir = loc;
                        g.Title = name;
                        g.Platform = "epic";
                        if (rel.Length > 0)
                        {
                            string exe = Path.Combine(loc, rel.Replace('/', '\\'));
                            if (File.Exists(exe)) g.Exe = Path.GetFileName(exe);
                        }
                        if (g.Exe.Length == 0) g.Exe = Path.GetFileName(FindExe(loc, null, name));
                        outl.Add(g);
                    }
                    catch { }
                }
            }
            catch { }
            return outl;
        }

        // ---------- 育碧 / EA ----------
        public static List<DlssgGame> Ubisoft()
        {
            var outl = new List<DlssgGame>();
            foreach (string basePath in new string[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
                foreach (string sub in SubKeys(Registry.LocalMachine, basePath))
                {
                    var vals = Values(Registry.LocalMachine, basePath + "\\" + sub);
                    string d = Val(vals, "installdir");
                    if (d.Length == 0) d = Val(vals, "installdirwow64");
                    if (d.Length == 0 || !Directory.Exists(d)) continue;
                    string name = Path.GetFileName(d.TrimEnd('\\', '/'));
                    if (NonGame(name)) continue;
                    var g = new DlssgGame();
                    g.Dir = d; g.Title = name; g.Platform = "ubisoft";
                    g.Exe = Path.GetFileName(FindExe(d, null, name));
                    outl.Add(g);
                }
            return outl;
        }

        public static List<DlssgGame> EA()
        {
            var outl = new List<DlssgGame>();
            foreach (string basePath in new string[] { @"SOFTWARE\EA Games", @"SOFTWARE\WOW6432Node\EA Games" })
                foreach (string sub in SubKeys(Registry.LocalMachine, basePath))
                {
                    var vals = Values(Registry.LocalMachine, basePath + "\\" + sub);
                    string d = Val(vals, "install dir");
                    if (d.Length == 0 || !Directory.Exists(d)) continue;
                    if (NonGame(sub)) continue;
                    var g = new DlssgGame();
                    g.Dir = d; g.Title = sub; g.Platform = "ea";
                    g.Exe = Path.GetFileName(FindExe(d, null, sub));
                    outl.Add(g);
                }
            return outl;
        }

        // ---------- 注册表卸载项（WeGame / GOG / 战网 / 第三方 / 二游） ----------
        public class RegEntry
        {
            public string Name = "";
            public string Loc = "";
            public string Icon = "";
            public string Uninst = "";
            public string Platform = "local";
            public string Dir = "";          // 推断出的游戏目录
            public string Hint = "";         // 米哈游主程序名提示
            public string SubKey = "";
        }

        public static List<RegEntry> RegistryEntries()
        {
            var outl = new List<RegEntry>();
            var paths = new object[][] {
                new object[] { Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" },
                new object[] { Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" },
                new object[] { Registry.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\Uninstall" }
            };
            foreach (object[] row in paths)
            {
                RegistryKey hive = (RegistryKey)row[0];
                string upath = (string)row[1];
                foreach (string sub in SubKeys(hive, upath))
                {
                    var vals = Values(hive, upath + "\\" + sub);
                    string name = Val(vals, "displayname").Trim();
                    if (name.Length == 0 || NonGame(name)) continue;
                    string icon = Val(vals, "displayicon").Trim().Trim('"');
                    string loc = Val(vals, "installlocation").Trim().Trim('"');
                    string uninst = Val(vals, "uninstallstring").Trim().Trim('"');
                    string scv = Val(vals, "systemcomponent");
                    string iconExe = ExeFromIcon(icon);
                    if (scv == "1" && icon.Length == 0) continue;
                    string probe = (loc.Length > 0 ? loc : (iconExe.Length > 0 ? Path.GetDirectoryName(iconExe) : ""))
                                   + " " + uninst;
                    probe = probe.ToLowerInvariant();
                    if (probe.Contains("windowsapps") || probe.Contains("steamapps")) continue;   // UWP 不能直接启动；Steam 由 acf 覆盖
                    string plat = Classify(vals, name, loc, icon, uninst);
                    if (plat == "steam") continue;
                    var e = new RegEntry();
                    e.Name = name; e.Loc = loc; e.Icon = icon; e.Uninst = uninst;
                    e.Platform = plat; e.SubKey = sub;
                    foreach (string[] h in MiHoYoHints)
                        if (sub.ToLowerInvariant().Contains(h[0])) { e.Hint = h[1]; break; }
                    // 目录推断：安装目录 > 图标所在目录 > 卸载串里的 exe 目录。
                    //  Directory.Exists 用原值判（它两种斜杠都认），但落进 e.Dir 前一律归一 ——
                    //  注册表里的路径可能带正斜杠，理由见 Lib.BackSlashes。
                    if (loc.Length > 0 && Directory.Exists(loc)) e.Dir = Lib.BackSlashes(loc);
                    else if (iconExe.Length > 0 && Directory.Exists(Path.GetDirectoryName(iconExe)))
                        e.Dir = Lib.BackSlashes(Path.GetDirectoryName(iconExe));
                    else
                    {
                        Match m = Regex.Match(uninst, @"[A-Za-z]:\\[^""]*?\.exe");
                        if (m.Success)
                        {
                            string dd = Path.GetDirectoryName(m.Value);
                            if (Directory.Exists(dd) && !IsSystemDir(dd)) e.Dir = Lib.BackSlashes(dd);
                        }
                    }
                    if (e.Dir.Length == 0) continue;
                    outl.Add(e);
                }
            }
            return outl;
        }

        static bool IsSystemDir(string p)
        {
            string s = (p == null ? "" : p).ToLowerInvariant();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            return s.StartsWith(win) || s.Contains("\\appdata\\");
        }

        // 常规软件安装区：需有明确"游戏信号"才算游戏
        public static bool PositiveGameSignal(string name, string blob)
        {
            string b = ((name == null ? "" : name) + " " + (blob == null ? "" : blob)).ToLowerInvariant();
            string[] keys = new string[] { "game", "游戏", "galgame", "steamapps", "rail_apps", "wegameapps" };
            foreach (string k in keys) if (b.Contains(k)) return true;
            return Genre2DHit(name, blob);
        }

        public static string Classify(Dictionary<string, string> vals, string name, string loc, string icon, string uninst)
        {
            if (vals.ContainsKey("gogproductid") || (uninst != null && uninst.ToLowerInvariant().Contains("goggalaxy"))) return "gog";
            string locL = (loc == null ? "" : loc).ToLowerInvariant();
            string unL = (uninst == null ? "" : uninst).ToLowerInvariant();
            if (locL.Contains("wegame") || unL.Contains("wegame") || locL.Contains("rail_apps") || locL.Contains("wegameapps")) return "wegame";
            string blob = ((name == null ? "" : name) + " " + (icon == null ? "" : icon) + " " + (uninst == null ? "" : uninst) + " " + Val(vals, "publisher")).ToLowerInvariant();
            if (blob.Contains("battle.net") || blob.Contains("blizzard") || blob.Contains("暴雪")) return "battle";
            if (blob.Contains("ubisoft")) return "ubisoft";
            if (blob.Contains("epic games")) return "epic";
            if (blob.Contains("steamapps")) return "steam";
            if (blob.Contains("ea games") || blob.Contains("electronic arts")) return "ea";
            return "local";
        }

        // ---------- 注册表小工具 ----------
        public static List<string> SubKeys(RegistryKey hive, string path)
        {
            var outl = new List<string>();
            try
            {
                using (RegistryKey k = hive.OpenSubKey(path))
                {
                    if (k == null) return outl;
                    outl.AddRange(k.GetSubKeyNames());
                }
            }
            catch { }
            return outl;
        }

        public static Dictionary<string, string> Values(RegistryKey hive, string path)
        {
            var d = new Dictionary<string, string>();
            try
            {
                using (RegistryKey k = hive.OpenSubKey(path))
                {
                    if (k == null) return d;
                    foreach (string n in k.GetValueNames())
                    {
                        object v = null;
                        try { v = k.GetValue(n); } catch { }
                        d[n.ToLowerInvariant()] = v == null ? "" : v.ToString();
                    }
                }
            }
            catch { }
            return d;
        }

        public static string Val(Dictionary<string, string> d, string k)
        {
            string v;
            if (d != null && d.TryGetValue(k, out v)) return v == null ? "" : v;
            return "";
        }

        static string K(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null) return d[k].ToString();
            return "";
        }
    }

    // ============================================================================
    //  封面管线（对照 images.py）
    //  --------------------------------------------------------------------------
    //  取图顺序：用户自定义封面（covers\游戏名.png）→ 缓存的官方竖版 → 现画渐变卡。
    //  网络请求：仅 https + Steam 官方域名白名单 + 大小上限，走工具现有的代理配置。
    // ============================================================================
    public static class CoverArt
    {
        public static string CoversDir { get { return Path.Combine(Program.DataDir, "covers"); } }

        static readonly Dictionary<string, Image> mem = new Dictionary<string, Image>();
        // mem 在 UI 线程（卡片绘制）与后台线程（批量封面 ClearCache）并发访问，无锁时
        // Dictionary 内部桶可能被并发写坏（2026-09-15 白框事故的近亲，2026-09-30 体检）。
        // Own() 的位图复制也一并锁内完成：保证复制期间源图绝不会被 ClearCache Dispose 掉。
        static readonly object memLock = new object();

        public static void ClearCache()
        {
            lock (memLock)
            {
                foreach (KeyValuePair<string, Image> kv in mem) { try { kv.Value.Dispose(); } catch { } }
                mem.Clear();
            }
        }

        public static bool Allowed(string url)
        {
            try
            {
                Uri u = new Uri(url);
                if (u.Scheme != "https") return false;
                string[] hosts = new string[] { "cdn.cloudflare.steamstatic.com", "steampowered.com",
                    "api.steampowered.com", "media.steampowered.com", "steamcommunity.com" };
                string h = u.Host.ToLowerInvariant();
                foreach (string a in hosts) if (h == a || h.EndsWith("." + a)) return true;
                return false;
            }
            catch { return false; }
        }

        public static byte[] Download(string url, int maxSize)
        {
            return Download(url, maxSize, 500);
        }

        // 网络策略：先按设置页选的方式试，失败再换另一种。
        // 实测（2026-09-14）：Steam CDN 直连可用（0.3s），而 steamcommunity 的名称搜索需要代理；
        // 用户代理又常常没开 —— 只走一条路就会"全军覆没"，所以这里必须双路回退。
        // 同时把失败原因写进日志：以前是静默 catch，界面上完全看不出为什么没封面。
        public static byte[] Download(string url, int maxSize, int minSize)
        {
            if (!Allowed(url)) { Program.Log("  封面下载被拒绝（域名不在白名单）：" + url); return null; }
            bool preferProxy = Program.Cfg.DlssgUseProxy && Program.Cfg.DlssgProxy.Length > 0;
            byte[] d = Fetch(url, maxSize, minSize, preferProxy);
            if (d == null) d = Fetch(url, maxSize, minSize, !preferProxy);
            return d;
        }

        static byte[] Fetch(string url, int maxSize, int minSize, bool useProxy)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 Fluxion";
                    wc.Encoding = Encoding.UTF8;
                    if (useProxy) wc.Proxy = new WebProxy(Program.Cfg.DlssgProxy);
                    else wc.Proxy = null;          // null = 明确不走代理（默认会取 IE/系统代理设置）
                    byte[] d = wc.DownloadData(url);
                    if (d == null || d.Length < minSize || d.Length > maxSize) return null;
                    return d;
                }
            }
            catch (Exception ex)
            {
                Program.Log("  下载失败（" + (useProxy ? "代理 " + Program.Cfg.DlssgProxy : "直连") + "）"
                            + url.Substring(0, Math.Min(64, url.Length)) + " → " + ex.Message);
                return null;
            }
        }

        public static string SafeName(string s)
        {
            string t = s == null ? "" : s;
            foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c, '_');
            return t.Trim();
        }

        public static string CardFile(string id) { return Path.Combine(CoversDir, id + ".jpg"); }
        public static string CardFilePng(string id) { return Path.Combine(CoversDir, id + ".png"); }
        // v4 生成卡（构图改过：磁贴上移、去掉自带底部压暗）→ 换文件名作废旧缓存
        public static string GenFile(string id) { return Path.Combine(CoversDir, id + "_gen4.png"); }

        // Steam 官方封面候选路径，按「竖版优先」排列。
        //  实测（2026-09-14，用户反馈"怎么有的没有封面"）：
        //  部分游戏**没有** library_600x900 也**没有** header，但library_hero 是有的
        //  （appid 2483190 实测：前两条 404、library_hero 200/250KB）。
        //  只试一条路径 → 那批游戏永远没有封面。所以这里把官方所有尺寸都试一遍。
        static readonly string[] CoverPaths = new string[] {
            "library_600x900.jpg",      // 竖版库封面（最理想，正好 2:3）
            "library_600x900_2x.jpg",
            "header.jpg",               // 横版头图
            "capsule_616x353.jpg",
            "library_hero.jpg",         // 宽幅 hero
            "capsule_231x87.jpg",
            "logo.png" };

        public static bool DownloadSteam(string appid, string id)
        {
            if (!Regex.IsMatch(appid == null ? "" : appid, @"^\d{1,12}$")) return false;
            if (id == null || id.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(CoversDir);
                foreach (string path in CoverPaths)
                {
                    byte[] d = Download(string.Format(
                        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/{1}", appid, path), 4 << 20);
                    if (d == null) continue;
                    File.WriteAllBytes(CardFile(id), d);
                    Program.Log("  封面已下载：" + id + " ← " + path);
                    return true;
                }
                Program.Log("  " + id + "（appid " + appid + "）在 Steam CDN 无任何可用封面（已试 "
                            + CoverPaths.Length + " 种路径）—— 保留按名字生成的卡片");
            }
            catch { }
            return false;
        }

        // 手动指定封面：把用户选的图片拷成 <游戏名>.png（Card() 优先读它）
        public static string SetManualCover(DlssgGame g, string srcFile)
        {
            try
            {
                if (g == null || srcFile == null || !File.Exists(srcFile)) return "文件不存在";
                Directory.CreateDirectory(CoversDir);
                string dst = Path.Combine(CoversDir, SafeName(g.Title) + Path.GetExtension(srcFile).ToLowerInvariant());
                using (Image probe = Image.FromFile(srcFile)) { }        // 先验证是合法图片
                File.Copy(srcFile, dst, true);
                // 清掉同名的其它扩展，避免旧封面抢先
                foreach (string ext in new string[] { ".png", ".jpg", ".jpeg" })
                {
                    string p = Path.Combine(CoversDir, SafeName(g.Title) + ext);
                    if (File.Exists(p) && !Eq(p, dst)) { try { File.Delete(p); } catch { } }
                }
                ClearCache();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // 同上的字节版（联网下载后直接落盘）
        static string SaveCoverBytes(DlssgGame g, byte[] data)
        {
            try
            {
                if (g == null || data == null || data.Length < 1000) return "数据无效";
                // 竖版门禁：任何写入 named 封面的字节都必须是接近 2:3 的竖图。
                // 没有这道闸，512x512 方图/横图会混进来把好封面顶掉（2026-09-15 实测事故）。
                try
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image img = Image.FromStream(ms))
                    {
                        double ar = (double)img.Width / img.Height;
                        if (img.Width < 400 || img.Height < 560 || ar < 0.50 || ar > 0.85)
                            return string.Format("拒绝非竖版封面（{0}x{1}，比例 {2:F2}）", img.Width, img.Height, ar);
                    }
                }
                catch { return "封面数据无法解码"; }
                Directory.CreateDirectory(CoversDir);
                string dst = Path.Combine(CoversDir, SafeName(g.Title) + ".jpg");
                File.WriteAllBytes(dst, data);
                foreach (string ext in new string[] { ".png", ".jpeg" })
                {
                    string p = Path.Combine(CoversDir, SafeName(g.Title) + ext);
                    if (File.Exists(p) && !Eq(p, dst)) { try { File.Delete(p); } catch { } }
                }
                ClearCache();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------------- 无封面游戏的联网兜底（best-effort） ----------------
        //  实测（2026-09-14）：Bing 图片搜索能拿到候选，但**相关性不稳** —— "无畏契约"会返回
        //  书法字帖、"卡拉彼丘"会返回毕业证照片。所以这里做三重过滤：域名黑名单 + 最小尺寸 +
        //  竖版比例 + 必须能解码成图片。仍可能挑错，界面保留「设置封面…」手动兜底。
        //  ⚠ 这一步会访问任意 https 图片站（不再是 Steam 专属白名单），设置页文案已同步说明。
        static readonly string[] ImgBadHosts = new string[] {
            "hanyuguoxue", "hao86", "hgcha", "39017", "zitupic", "calligraphy", "shufazidian",
            "diyifanwen", "qiuwenzi", "bapiw.com", "diploma", "baike.baidu", "so.com",
            "tgl.qq.com/cover" };

        static bool BadImageHost(string url)
        {
            string h = (url == null ? "" : url).ToLowerInvariant();
            foreach (string s in ImgBadHosts) if (h.Contains(s)) return true;
            return false;
        }

        // 不做域名白名单的取回（仅用于联网搜图这条路径）
        static byte[] FetchAny(string url, int maxSize, int minSize)
        {
            if (url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            bool preferProxy = Program.Cfg.DlssgUseProxy && Program.Cfg.DlssgProxy.Length > 0;
            byte[] d = Fetch(url, maxSize, minSize, preferProxy);
            if (d == null) d = Fetch(url, maxSize, minSize, !preferProxy);
            return d;
        }

        // 成功返回 ""，失败/找不到返回原因
        public static string SearchWebCover(DlssgGame g)
        {
            if (g == null || g.Title == null || g.Title.Trim().Length < 2) return "名字太短，无法检索";
            string q = g.Title.Trim();
            int tried = 0;
            string[] tails = new string[] { " 游戏 封面 竖版", " 官方 主视觉 壁纸 竖" };
            foreach (string tail in tails)
            {
                string u = "https://www.bing.com/images/search?q=" + Uri.EscapeDataString(q + tail) + "&form=HDRSC2";
                byte[] raw = FetchAny(u, 6 << 20, 2000);
                if (raw == null)
                {
                    Program.Log("  联网搜图失败（搜索引擎不可达）：" + q);
                    continue;
                }
                string html = Encoding.UTF8.GetString(raw);
                var seen = new Dictionary<string, int>();
                foreach (string cand0 in BingImageUrls(html, 30))
                {
                    string cand = cand0;
                    if (seen.ContainsKey(cand)) continue;
                    seen[cand] = 1;
                    if (BadImageHost(cand)) continue;
                    if (++tried > 24) break;
                    byte[] d = FetchAny(cand, 12 << 20, 8000);
                    if (d == null) continue;
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(d))
                        using (Image img = Image.FromStream(ms))
                        {
                            if (img.Width < 500 || img.Height < 640) continue;      // 太小
                            double ar = (double)img.Width / img.Height;
                            // 只收接近 2:3 的竖版（0.58~0.78）。放宽到 0.95 会收到
                            // 「三格拼图 / 堆叠宣传图」这类东西 —— 实测无畏契约就中过招。
                            if (ar < 0.58 || ar > 0.78) continue;
                        }
                    }
                    catch { continue; }
                    string err = SaveCoverBytes(g, d);
                    if (err.Length == 0)
                    {
                        Program.Log("  联网封面命中：" + g.Title + " ← " + ShortUrl(cand));
                        return "";
                    }
                }
            }
            return "未找到合适的竖版图（已试 " + tried + " 个候选）";
        }

        // Bing 图片搜索结果里图片直链的形式实测为：
        //     &quot;murl&quot;:&quot;https://…&quot;
        //  也就是 JSON 被 HTML 转义过了（旧版页面是未转义的 "murl":"…"，这里两种都兼容）。
        //  用 HTML 实体当定界符的额外好处：**源码里一个转义双引号都不用写** ——
        //  v2.6.1 就是在这里写坏了正则，编译器报 CS1010「常量中有换行符」。
        static List<string> BingImageUrls(string html, int max)
        {
            var res = new List<string>();
            if (html == null) return res;
            string[] tags = new string[] { "&quot;murl&quot;:&quot;", "murl" + '"' + ':' + '"' };
            foreach (string tag in tags)
            {
                string endTag = (tag[0] == '&') ? "&quot;" : "\"";   // 对应两种形态的结束符
                int p = 0;
                while (res.Count < max)
                {
                    p = html.IndexOf(tag, p);
                    if (p < 0) break;
                    p += tag.Length;
                    int e = html.IndexOf(endTag, p);
                    if (e < 0) break;
                    string u = html.Substring(p, e - p).Replace("&amp;", "&").Replace("\\/", "/");
                    if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !res.Contains(u)) res.Add(u);
                }
                if (res.Count > 0) break;      // 命中一种形态就够了
            }
            return res;
        }

        static string ShortUrl(string u)
        {
            if (u == null) return "";
            return u.Length <= 72 ? u : u.Substring(0, 72) + "…";
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // 按名称在 Steam 搜索 appid（对照 images.search_steam_appid）
        public static string SearchAppId(string name)
        {
            string n = Regex.Replace((name == null ? "" : name).ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+", "");
            if (n.Length < 2) return "";
            try
            {
                byte[] raw = Download("https://steamcommunity.com/actions/SearchApps/" + Uri.EscapeDataString((name == null ? "" : name).Trim()), 1 << 20, 20);
                if (raw == null) return "";
                string txt = Encoding.UTF8.GetString(raw);
                var ser = new JavaScriptSerializer();
                var arr = ser.DeserializeObject(txt) as System.Collections.IEnumerable;
                if (arr == null) return "";
                string first = "";
                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("appid") || d["appid"] == null) continue;
                    string id = d["appid"].ToString();
                    if (first.Length == 0) first = id;
                    string rn = Regex.Replace((d.ContainsKey("name") && d["name"] != null ? d["name"].ToString() : "").ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+", "");
                    if (rn == n) return id;      // 精确同名优先
                }
                return first;
            }
            catch { return ""; }
        }

        // 按目标比例裁切后再缩放（"填满"）：竖版封面正好是 2:3 → 裁切为 0；横版头图会被裁到中间，
        // 但从 Zoom（留黑边/留灰边）改成填满后，卡片看起来干净得多，也解决了"封面大小不匹配"的违和感。
        // 封面适配：object-fit: cover 语义 —— 等比铺满卡片、多出来的部分裁掉。
        //  为什么不再用"模糊铺满 + 完整图居中"（v2.6.0/2.6.1 的做法）：
        //  横版图缩进竖版卡里，上下两条模糊带非常显眼 —— 用户原话"这个明显不适合做封面"。
        //  现在对齐游戏启动器的 .card-img { object-fit: cover }：一律裁切填满；
        //  横版图纵向取景 42%（关键视觉通常偏上，取正中容易切掉角色头）。
        public static Bitmap Fit(Image src, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                double tr = (double)w / h, sr = (double)src.Width / src.Height;
                int cw, ch;
                if (sr > tr) { ch = src.Height; cw = (int)Math.Round(src.Height * tr); }
                else { cw = src.Width; ch = (int)Math.Round(src.Width / tr); }
                int cx = (src.Width - cw) / 2;
                int cy = (int)Math.Round((src.Height - ch) * (sr > tr ? 0.42 : 0.5));
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), cx, cy, cw, ch, GraphicsUnit.Pixel, ia);
                }
            }
            return bmp;
        }

        // 圆角必须"烘焙"进位图，而不是绘制时 SetClip。
        //  原因：GDI+ 的 SetClip(path) 是**区域裁剪**，不做抗锯齿 —— 圆角边缘就是硬锯齿
        //  （用户截图："封面边缘锯齿过于严重，整个程序都有这个感觉"）。
        //  FillPath + TextureBrush 是"按路径覆盖填充"，边缘带 alpha 过渡，天然平滑。
        public static Bitmap RoundAlpha(Bitmap bmp, int radius)
        {
            if (bmp == null || radius <= 0) return bmp;
            try
            {
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                Bitmap outb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(outb))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    using (TextureBrush tb = new TextureBrush(bmp, WrapMode.Clamp))
                    using (GraphicsPath gp = Theme.Round(rect, radius))
                        g.FillPath(tb, gp);
                }
                bmp.Dispose();
                return outb;
            }
            catch { return bmp; }
        }

        // 裁掉封面底部"纯色空带"：Steam 官方 library_600x900 本来就给客户端留了下半空底
        // （用来叠游戏名），直接全幅贴卡会显成"上图案 + 下空底"两截（用户截图）。
        // 从底部往上数连续的纯色行（64 列采样的亮度极差 <= 10），最多裁 40% 高。
        static int CountFlatBottom(Image src, double maxCutFrac)
        {
            try
            {
                int sw = 64;
                int sh = (int)((double)src.Height / src.Width * sw);
                if (sh < 8) return 0;
                using (Bitmap small = new Bitmap(src, sw, sh))
                {
                    int cut = 0;
                    int maxCut = (int)(sh * maxCutFrac);
                    for (int y = sh - 1; y > sh / 2; y--)
                    {
                        int mn = 255, mx = 0;
                        for (int x = 0; x < sw; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                            if (lum < mn) mn = lum;
                            if (lum > mx) mx = lum;
                        }
                        if (mx - mn <= 10 && cut < maxCut) cut = sh - y;
                        else break;
                    }
                    return cut > 0 ? (int)((double)cut / sh * src.Height) : 0;
                }
            }
            catch { return 0; }
        }

        // 封面专用加载：先裁空底再 cover-crop（生成磁贴不走这里，它们本来就是成品构图）
        static Image LoadCoverScaled(string path, int w, int h)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image tmp = Image.FromStream(fs))
                {
                    int cut = CountFlatBottom(tmp, 0.40);
                    if (cut > 0 && cut < tmp.Height - 40)
                    using (Image trimmed = ((Bitmap)tmp).Clone(new Rectangle(0, 0, tmp.Width, tmp.Height - cut), tmp.PixelFormat))
                        return Fit(trimmed, w, h);
                    return Fit(tmp, w, h);
                }
            }
            catch { return null; }
        }

        static Image LoadScaled(string path, int w, int h)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image tmp = Image.FromStream(fs))
                    return Fit(tmp, w, h);
            }
            catch { return null; }
        }

        static void HsvToRgb(double h, double s, double v, out int r, out int g, out int b)
        {
            h = h - Math.Floor(h);
            double i = Math.Floor(h * 6);
            double f = h * 6 - i;
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double rr, gg, bb;
            switch (((int)i) % 6)
            {
                case 0: rr = v; gg = t; bb = p; break;
                case 1: rr = q; gg = v; bb = p; break;
                case 2: rr = p; gg = v; bb = t; break;
                case 3: rr = p; gg = q; bb = v; break;
                case 4: rr = t; gg = p; bb = v; break;
                default: rr = v; gg = p; bb = q; break;
            }
            r = (int)(rr * 255); g = (int)(gg * 255); b = (int)(bb * 255);
        }

        // 无封面游戏的生成卡配色：一族**深色**渐变（取自游戏启动器的紫→蓝→青，但压到低明度）。
        //  为什么不用亮色（v2.6.0 的错）：真实盒装封面基本都是暗调，亮青色拼在一起会"参差不齐"，
        //  而且图标浮在亮底上像贴纸。压暗之后整排卡片观感一致。
        static readonly int[][] GenPairs = new int[][] {
            new int[] { 0x3A2E7A, 0x1B2E6B },   // 深靛 → 深蓝
            new int[] { 0x24406E, 0x123A55 },   // 深蓝 → 深青
            new int[] { 0x3B2A63, 0x532E7A },   // 深紫 → 紫
            new int[] { 0x1E4A57, 0x123C4E },   // 深青 → 墨青
            new int[] { 0x4A2A55, 0x2A2E6B },   // 深梅 → 靛
            new int[] { 0x2B3A6B, 0x14405E } }; // 藏蓝 → 深湖

        static Color FromRgb(int v)
        {
            return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        // 生成卡 v3：「设计感封面」而不是「渐变 + 小图标」
        //  深色斜渐变 → 中央品牌色辉光 → 图标磁贴（大、带投影与细描边）→ 底部压暗 → 斜向高光
        public static Bitmap GradientCard(string name, int w, int h, Image icon)
        {
            byte[] dig;
            using (SHA256 sha = SHA256.Create()) dig = sha.ComputeHash(Encoding.UTF8.GetBytes(name == null ? "" : name));
            int[] pair = GenPairs[dig[0] % GenPairs.Length];
            Color c1 = FromRgb(pair[0]), c2 = FromRgb(pair[1]);

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var rect = new Rectangle(0, 0, w, h);

                using (LinearGradientBrush br = new LinearGradientBrush(rect, c1, c2, 42f))
                    g.FillRectangle(br, rect);

                // 中央辉光：把图标"托"起来（比 v2 更大更柔）
                using (GraphicsPath gp = new GraphicsPath())
                {
                    int gw = (int)(w * 1.35), gh = (int)(h * 1.05);
                    gp.AddEllipse((w - gw) / 2, (int)(h * 0.06), gw, gh);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(96, Lighten(c2, 0.55));
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, c2) };
                        g.FillPath(pb, gp);
                    }
                }

                // 图标磁贴：尺寸更大、圆角、细描边、投影 —— 像一个真正的应用磁贴，而不是浮着的小图
                // ⚠ 磁贴必须整体落在卡片遮罩区（底部 40%）之上：压着遮罩中线就会被切成"上下两半"
                var tile = new Rectangle((int)(w * 0.19), (int)(h * 0.13), (int)(w * 0.62), (int)(w * 0.62));
                using (var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    var sr = new Rectangle(tile.X + Theme.S(2), tile.Y + Theme.S(5), tile.Width, tile.Height);
                    using (var path = Theme.Round(sr, Theme.S(18)))
                        g.FillPath(sh, path);
                }
                if (icon != null)
                {
                    using (var path = Theme.Round(tile, Theme.S(18)))
                    {
                        var old = g.Clip;
                        g.SetClip(path, CombineMode.Replace);
                        g.DrawImage(icon, tile);
                        g.Clip = old;
                        old.Dispose();
                    }
                    using (var path = Theme.Round(tile, Theme.S(18)))
                    using (var pen = new Pen(Color.FromArgb(64, 255, 255, 255), 1f))
                        g.DrawPath(pen, path);
                }
                else
                {
                    using (var path = Theme.Round(tile, Theme.S(18)))
                    using (var b = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                        g.FillPath(b, path);
                    string ch = (name == null || name.Length == 0) ? "?" : name.Substring(0, 1);
                    using (Font f = new Font("Microsoft YaHei UI", tile.Height * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(ch, f, b, tile, sf);
                    }
                }

                // 斜向高光（收边提质感）。
                // ⚠ 画刷矩形必须与填充矩形一致：填的面积比画刷大时，默认 WrapMode.Tile
                //   会平铺出条纹（与 GameCard 遮罩"两道杠"同源）。
                var hlRect = new Rectangle(-w, -h, w * 2, h * 3 / 2);
                using (LinearGradientBrush hl = new LinearGradientBrush(hlRect,
                           Color.FromArgb(28, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 118f))
                    g.FillRectangle(hl, hlRect);
            }
            return bmp;
        }

        static Color Lighten(Color c, double k)
        {
            return Color.FromArgb(
                Math.Min(255, (int)(c.R + (255 - c.R) * k)),
                Math.Min(255, (int)(c.G + (255 - c.G) * k)),
                Math.Min(255, (int)(c.B + (255 - c.B) * k)));
        }

        // 卡片图：内存缓存 + 磁盘缓存（避免每次刷新重算）
        public static Image Card(DlssgGame g, int w, int h)
        {
            if (g == null) return null;
            string id = g.Id == null ? "" : g.Id;
            // 缓存键必须包含标题：Id 为空（未入平台库的自定义条目）时若只用尺寸做键，
            // 两个不同游戏会共用同一张图（2026-09-14 探针实测踩到）
            string key = id + "|" + SafeName(g.Title) + "|" + w + "x" + h;
            Image cached;
            lock (memLock) { if (mem.TryGetValue(key, out cached)) return Own(cached); }

            Image img = null;
            try
            {
                Directory.CreateDirectory(CoversDir);
                string nm = SafeName(g.Title);
                string[] cand = new string[] {
                    Path.Combine(CoversDir, nm + ".png"),
                    Path.Combine(CoversDir, nm + ".jpg"),
                    Path.Combine(CoversDir, nm + ".jpeg") };
                if (id.Length > 0)
                    cand = Append(cand, CardFile(id), CardFilePng(id), GenFile(id));
                foreach (string p in cand)
                {
                    if (!File.Exists(p)) continue;
                    Image bi = (p.IndexOf("_gen", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? LoadScaled(p, w, h)   // 生成磁贴是成品构图，不裁
                        : LoadCoverScaled(p, w, h);
                    if (bi == null) continue;
                    img = RoundAlpha((Bitmap)bi, Theme.RPic);
                    break;
                }
                if (img == null)
                {
                    Image icon = null;
                    try
                    {
                        if (g.Exe != null && g.Exe.Length > 0)
                        {
                            string exe = Path.Combine(g.Dir, g.Exe);
                            if (File.Exists(exe)) icon = GameIcon.Extract(exe, 256);
                        }
                    }
                    catch { }
                    Bitmap bmp = RoundAlpha(GradientCard(g.Title, w, h, icon), Theme.RPic);
                    if (icon != null) icon.Dispose();
                    img = bmp;
                    try { if (id.Length > 0) bmp.Save(GenFile(id), ImageFormat.Png); } catch { }
                }
            }
            catch { }
            // 加进缓存后当场在锁内复制出调用方的独占副本：Own 若放到锁外，
            // 中间可能被并发 ClearCache 把源图 Dispose 掉（0915 白框事故同款）
            if (img != null) lock (memLock) { mem[key] = img; return Own(img); }
            return null;
        }

        // 交给调用方一份**独占副本**。
        //   ★ 2026-09-15 事故根因：Card() 原来直接把缓存里的 Image 交出去，GameCard 会长久持有它，
        //     而 ClearCache()（获取封面 / 换封面 / 扫描后）却把同一批 Image Dispose 掉。之后任何一次
        //     重绘都是在已销毁的 GDI+ 句柄上作图 → ArgumentException「参数无效」，.NET 会把绘制失败的
        //     控件**填白打叉** —— 用户截图里 16 张"白框 + 红叉"就是这个（日志里 16 条 UNCAUGHT_THREAD）。
        //     规则：缓存自己持有并负责释放；调用方拿到的必须是副本。
        static Image Own(Image src)
        {
            try
            {
                if (src == null) return null;
                Bitmap b = src as Bitmap;
                return b != null ? new Bitmap(b) : new Bitmap(src);
            }
            catch { return null; }
        }

        static string[] Append(string[] a, params string[] more)
        {
            var l = new List<string>(a);
            l.AddRange(more);
            return l.ToArray();
        }

        // 该游戏是否已有"真封面"（自定义 or 下载的官方封面）——决定要不要联网补
        public static bool HasRealCover(DlssgGame g)
        {
            try
            {
                string nm = SafeName(g == null ? "" : g.Title);
                if (File.Exists(Path.Combine(CoversDir, nm + ".png"))) return true;
                if (File.Exists(Path.Combine(CoversDir, nm + ".jpg"))) return true;
                string id = g == null ? "" : (g.Id == null ? "" : g.Id);
                if (id.Length == 0) return false;
                return File.Exists(CardFile(id)) || File.Exists(CardFilePng(id));
            }
            catch { return false; }
        }
    }

    // ============================================================================
    //  库汇总：把平台扫描结果合并成最终游戏列表（官方名 + 平台 + 标记）
    // ============================================================================
    public static class Lib
    {
        // 注册表里的安装路径可能是正斜杠写法 —— 实测 Steam 的 SteamPath 就是
        //  "c:/program files (x86)/steam"。Path.Combine 一拼就混成 "c:/.../steam\steamapps"，
        //  这种混合斜杠 explorer.exe 解析不了，会**静默退到「文档」**（不报错，Process.Start 也不抛）；
        //  游戏 exe 同样可能起不来。⚠ Directory.Exists 对混合斜杠返回 true，常规守卫拦不住。
        //  凡是要落进 DlssgGame.Dir 的外部路径都过这一道（2026-09-19 用户实测即此）。
        public static string BackSlashes(string p)
        {
            return string.IsNullOrEmpty(p) ? "" : p.Replace('/', '\\');
        }

        public static string IdOf(DlssgGame g)
        {
            if (g == null) return "";
            if (g.Platform == "steam" && g.AppId != null && Regex.IsMatch(g.AppId, @"^\d{1,12}$")) return "steam_" + g.AppId;
            string seed = MetaStore.Norm(g.Exe != null && g.Exe.Length > 0 ? Path.Combine(g.Dir, g.Exe) : g.Dir);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return (g.Platform != null && g.Platform.Length > 0 ? g.Platform : "local") + "_" + sb.ToString();
            }
        }

        static List<DlssgGame> Safe(Func<List<DlssgGame>> f)
        {
            try { return f(); } catch { return new List<DlssgGame>(); }
        }

        // 把扫描到的目录对上已有条目：精确命中 > 米哈游提示名命中 > 唯一前缀命中
        static DlssgGame MatchUnique(List<DlssgGame> list, string dir, string hint)
        {
            string d = string.IsNullOrEmpty(dir) ? "" : MetaStore.Norm(dir);
            string hl = (hint == null ? "" : hint).ToLowerInvariant();
            var exact = new List<DlssgGame>();
            var pref = new List<DlssgGame>();
            var hintHit = new List<DlssgGame>();
            foreach (DlssgGame g in list)
            {
                if (g.Dir == null || g.Dir.Length == 0) continue;
                string gd = MetaStore.Norm(g.Dir);
                if (d.Length > 0 && gd == d) exact.Add(g);
                else if (d.Length > 0 && (gd.StartsWith(d + "\\") || d.StartsWith(gd + "\\"))) pref.Add(g);
                if (hl.Length > 0 && (gd.Contains(hl) || (g.Exe != null && g.Exe.ToLowerInvariant().Contains(hl)))) hintHit.Add(g);
            }
            if (exact.Count > 0) return exact[0];
            if (hintHit.Count == 1) return hintHit[0];
            if (pref.Count == 1) return pref[0];
            return null;
        }

        // 能力探测（帧生成组件等），但保留平台给的标题/平台字段
        static void Fill(DlssgGame g)
        {
            try
            {
                DlssgGame p = Dlssg.Inspect(g.Dir, false);
                if (p != null)
                {
                    g.HasFrameGen = p.HasFrameGen;
                    g.HasUpscaler = p.HasUpscaler;
                    g.HasFsr3 = p.HasFsr3;
                    g.Installed = p.Installed;
                    g.Entry = p.Entry;
                    if (g.Exe.Length == 0) g.Exe = p.Exe;
                }
            }
            catch { }
        }

        public static List<DlssgGame> ScanAll(Action<string> log)
        {
            Action<string> L = log == null ? (Action<string>)delegate(string s) { } : log;

            // ① 现有扫描（常见目录 / Steam 库目录 / 注册表卸载项 + 帧生成能力探测）
            List<DlssgGame> list;
            try { list = Dlssg.Scan(log); }
            catch { list = new List<DlssgGame>(); }

            // ② 注册表卸载项：给已有条目换成官方名（原神 / 绝区零 / 鸣潮 这类中文名）
            List<PlatformScan.RegEntry> regs = new List<PlatformScan.RegEntry>();
            try { regs = PlatformScan.RegistryEntries(); } catch { }
            int renamed = 0;
            foreach (PlatformScan.RegEntry e in regs)
            {
                DlssgGame hit = MatchUnique(list, e.Dir, e.Hint);
                if (hit == null) continue;
                if (hit.Title != e.Name && e.Name.Length > 0) { hit.Title = e.Name; renamed++; }
                if (e.Platform != "local" && hit.Platform == "local") hit.Platform = e.Platform;
            }

            // ③ Steam appmanifest：官方名（含中文）+ 全部库（补回第二个 Steam 库里的游戏）
            int added = 0;
            var steamApps = new List<SteamApp>();
            try { steamApps = SteamLib.Scan(); } catch { }
            foreach (SteamApp a in steamApps)
            {
                DlssgGame hit = MatchUnique(list, a.Dir, null);
                if (hit != null)
                {
                    if (a.Name.Length > 0 && hit.Title != a.Name) { hit.Title = a.Name; renamed++; }
                    hit.Platform = "steam";
                    if (a.AppId.Length > 0) hit.AppId = a.AppId;
                    continue;
                }
                if (a.Dir == null || a.Dir.Length == 0) continue;
                DlssgGame g = Dlssg.Inspect(a.Dir, false);
                if (g == null) { g = new DlssgGame(); g.Dir = a.Dir; }
                g.Dir = a.Dir; g.Title = a.Name; g.Platform = "steam"; g.AppId = a.AppId;
                if (g.Exe.Length == 0)
                {
                    string e2 = PlatformScan.FindExe(a.Dir, null, a.Name);
                    if (e2.Length > 0) g.Exe = Path.GetFileName(e2);
                }
                list.Add(g); added++;
            }

            // ④ Epic / 育碧 / EA
            var groups = new List<DlssgGame>[] { Safe(PlatformScan.Epic), Safe(PlatformScan.Ubisoft), Safe(PlatformScan.EA) };
            foreach (List<DlssgGame> res in groups)
                foreach (DlssgGame g in res)
                {
                    if (MatchUnique(list, g.Dir, null) != null) continue;
                    Fill(g);
                    list.Add(g); added++;
                }

            // ⑤ 启动器平台（WeGame / 暴雪 / 育碧 / EA / GOG）的注册表条目
            foreach (PlatformScan.RegEntry e in regs)
            {
                if (e.Platform == "local" || e.Platform == "steam" || e.Platform == "epic") continue;
                if (MatchUnique(list, e.Dir, e.Hint) != null) continue;
                DlssgGame g = new DlssgGame();
                g.Dir = e.Dir; g.Title = e.Name; g.Platform = e.Platform;
                string exe = PlatformScan.FindExe(e.Dir, e.Hint, e.Name);
                if (exe.Length > 0) g.Exe = Path.GetFileName(exe);
                Fill(g);
                list.Add(g); added++;
            }

            // ⑥ 去重 + 库标记（收藏/私密/隐藏/重命名/最近游玩）+ 二次元分类
            Dictionary<string, GameMeta> map = MetaStore.Load();
            var final = new List<DlssgGame>();
            var seenName = new List<string>();
            foreach (DlssgGame g in list)
            {
                if (g.Dir == null || g.Dir.Length == 0) continue;
                string nm = PlatformScan.NormName(g.Title);
                if (nm.Length > 0 && seenName.Contains(nm)) continue;
                if (nm.Length > 0) seenName.Add(nm);

                g.Id = IdOf(g);
                GameMeta m;
                if (map.TryGetValue(MetaStore.KeyOf(g), out m))
                {
                    if (m.Name != null && m.Name.Length > 0) g.Title = m.Name;
                    g.Favorite = m.Favorite; g.Private = m.Private; g.Hidden = m.Hidden;
                    g.LastPlayed = m.LastPlayed;
                    g.LaunchArgs = m.LaunchArgs;
                    if ((g.AppId == null || g.AppId.Length == 0) && m.AppId.Length > 0) g.AppId = m.AppId;
                }
                if (g.Platform == null || g.Platform.Length == 0) g.Platform = "local";
                if (g.Genre == null || g.Genre.Length == 0)
                    if (PlatformScan.Genre2DHit(g.Title, g.Dir + " " + g.Exe)) g.Genre = "二次元";
                final.Add(g);
            }

            L("平台识别：修正官方名 " + renamed + " 款，新增 " + added + " 款，合计 " + final.Count + " 款");
            return final;
        }

        // 启动目标：把"只记了文件名"的 Exe 解析成真实全路径。
        // 为什么单独一步：扫描端存的是 Path.GetFileName(exe)（见 Fill / 各平台分支）—— 这个格式是
        //   元数据 key（收藏/隐藏/最近游玩）的一部分，不能为了修路径就改；而游戏的 exe 未必在
        //   安装目录根下：实测 WeGame 版无畏契约的 aclos-launcher.exe 在
        //   "...\无畏契约(2001715)\ACLOS\" 里，Dir + 文件名 拼出来是不存在的路径，
        //   于是启动动作静默退化成"打开文件夹"（2026-09-23 用户报的正是这个）。
        // 解析顺序：⓪ 手动指认（指认一次永久记住）→ ① 目录直拼 → ② 2 层浅搜 →
        //           ③ 平台感知深找。② 的 2 层够不着 WeGame 三角洲的主程序
        //   （DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe，3 层深），
        //   2026-09-27 用户报"启动三角洲只弹出文件夹"即此 —— ③ 补上扫描期同款的深度引擎。
        //   命中结果按"目录+文件名"缓存 —— 运行期目录内容不会变，不必每次启动都去遍历。
        static readonly Dictionary<string, string> ExePathCache = new Dictionary<string, string>();

        public static string LaunchTargetOf(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Dir.Length == 0) return "";
            // ⓪ 手动指认过的启动文件最优先（见 DoLaunchGame 的指认对话框）
            string pinned = MetaStore.LaunchExeOf(g);
            if (pinned.Length > 0) return pinned;
            string exe = g.Exe == null ? "" : g.Exe.Trim();
            if (exe.Length == 0) return FindExeDeep(g);   // 条目没记 exe（WeGame 扫描常见）→ 直接深找
            string direct = Path.Combine(g.Dir, exe);
            if (File.Exists(direct)) return direct;
            // 已经带目录层级的写法（相对/绝对路径）→ 拼出来不在了就是不在了，不再瞎找
            if (exe.IndexOf('\\') >= 0 || exe.IndexOf('/') >= 0) return "";
            string key = MetaStore.Norm(g.Dir) + "|" + exe.ToLowerInvariant();
            string hit;
            lock (ExePathCache) if (ExePathCache.TryGetValue(key, out hit)) return hit;
            hit = FindExeShallow(g.Dir, Path.GetFileName(exe), 2);
            if (hit.Length == 0) hit = FindExeDeep(g);
            lock (ExePathCache) ExePathCache[key] = hit;
            return hit;
        }

        // 平台感知的深找：WeGame 优先用**安装根目录的 rail 启动器**（DeltaForceClient.exe /
        //   aclos-launcher.exe 这类，官方快捷方式同款）—— 直接拉 Shipping 主程序会缺 WeGame 的
        //   登录票据上下文，登录/更新/TCLS 引导都归启动器管；根目录没有可用的再退通用深找。
        // 其余平台直接用扫描期同款引擎（名称匹配 > UE Shipping > 体积打分）。
        static string FindExeDeep(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Dir.Length == 0) return "";
            if (string.Equals(g.Platform, "wegame", StringComparison.OrdinalIgnoreCase))
            {
                string root = WeGameRootLauncher(g.Dir);
                if (root.Length > 0) return root;
            }
            return PlatformScan.FindExe(g.Dir, null, g.Title);
        }

        // WeGame 安装根目录的 rail 启动器：根下"非卸载器/非助手"里最大的 exe
        static string WeGameRootLauncher(string dir)
        {
            try
            {
                string best = ""; long bestSize = 0;
                foreach (string f in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string nm = Path.GetFileName(f).ToLowerInvariant();
                        if (nm.Contains("unins") || nm.Contains("卸载") || nm.Contains("setup")
                            || nm.Contains("install") || nm.Contains("crash") || nm.Contains("redist")
                            || nm.Contains("safemode") || nm.Contains("bootstrap")) continue;
                        if (PlatformScan.IsHelperExe(f)) continue;
                        long len = new FileInfo(f).Length;
                        if (len > bestSize) { bestSize = len; best = f; }
                    }
                    catch { }
                }
                return best;
            }
            catch { return ""; }
        }

        // 在 root 下 1~maxDepth 层子目录里找一个叫 name 的 exe（逐层广搜，越浅越优先）
        static string FindExeShallow(string root, string name, int maxDepth)
        {
            try
            {
                var lvl = new List<string>();
                lvl.Add(root);
                for (int d = 1; d <= maxDepth && lvl.Count > 0; d++)
                {
                    var nxt = new List<string>();
                    foreach (string dir in lvl)
                    {
                        string[] subs;
                        try { subs = Directory.GetDirectories(dir); } catch { subs = new string[0]; }
                        foreach (string s in subs)
                        {
                            string bn = Path.GetFileName(s).ToLowerInvariant();
                            bool skip = false;
                            foreach (string sk in PlatformScan.SkipDirs) if (bn == sk) { skip = true; break; }
                            if (skip) continue;
                            string p = Path.Combine(s, name);
                            if (File.Exists(p)) return p;
                            nxt.Add(s);
                        }
                    }
                    lvl = nxt;
                }
            }
            catch { }
            return "";
        }

        // 启动：Steam 走 steam:// 协议（DRM/更新更稳），其余直接跑 exe（绕开官方启动器的校验弹窗）。
        // 返回值："" = 已启动；NeedLocate|目录 = 自动定位不到启动文件，界面弹指认对话框；
        //         其他非空 = 失败原因。
        public const string NeedLocate = "LOCATE|";
        public static string Launch(DlssgGame g)
        {
            if (g == null) return "无效的游戏条目";
            try
            {
                if (g.Platform == "steam" && g.AppId != null && Regex.IsMatch(g.AppId, @"^\d{1,12}$"))
                    Process.Start("steam://rungameid/" + g.AppId);
                else
                {
                    string exe = LaunchTargetOf(g);
                    if (exe.Length > 0)
                    {
                        string args = (g.LaunchArgs == null ? "" : g.LaunchArgs).Trim();
                        if (args.Length > 0) Process.Start(exe, args);
                        else Process.Start(exe);
                    }
                    else if (Directory.Exists(g.Dir))
                    {
                        // 说清"这不是启动成功"（2026-09-23）：以前这里静默开文件夹还照打"已启动"。
                        // 2026-09-28 起改为返回 NeedLocate 标记：界面弹"指认启动文件"对话框，
                        //   选择持久化进 library.json —— 下次点启动直接命中，不再每次都开文件夹。
                        return NeedLocate + g.Dir;
                    }
                    else return "找不到可执行文件";
                }
            }
            catch (Exception ex) { return "启动失败：" + ex.Message; }
            try
            {
                GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
                m.LastPlayed = MetaStore.Now();
                m.Platform = g.Platform;
                if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
                MetaStore.Save(m);
                g.LastPlayed = m.LastPlayed;
            }
            catch { }
            return "";
        }

        public static void SetFlag(DlssgGame g, string which, bool on)
        {
            if (g == null) return;
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            if (which == "favorite") { m.Favorite = on; g.Favorite = on; }
            else if (which == "private") { m.Private = on; g.Private = on; }
            else if (which == "hidden") { m.Hidden = on; g.Hidden = on; }
            m.Platform = g.Platform;
            if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
            MetaStore.Save(m);
        }

        // ---- DX12 模式启动（v3.3.4）----
        // 绝区零 3.0+ 的「超分辨率 / 帧生成」设置项只在 DX12 模式下出现（DX11 下整块面板不渲染）。
        // 参数名是 -use-d3d12（社区实测：NGA「避开启动器并保留 DX12 设置直接启动游戏」2026-09-04）。
        // v3.3.3 用的通用 Unity 参数 -force-d3d12 无效（本机实测仍是 Direct3D 11.0），
        // 留在 LegacyDx12Args 里只为切换时把旧值清掉。
        public const string ArgDx12 = "-use-d3d12";
        static readonly string[] LegacyDx12Args = new string[] { "-force-d3d12" };

        static Regex ArgPat(string arg)
        {
            return new Regex(@"(?:^|\s)" + Regex.Escape(arg) + @"(?:\s|$)", RegexOptions.IgnoreCase);
        }

        public static bool HasDx12Arg(DlssgGame g)
        {
            return g != null && g.LaunchArgs != null && ArgPat(ArgDx12).IsMatch(g.LaunchArgs);
        }

        public static void SetDx12(DlssgGame g, bool on)
        {
            if (g == null) return;
            string cur = g.LaunchArgs == null ? "" : g.LaunchArgs;
            string rest = ArgPat(ArgDx12).Replace(cur, " ").Trim();
            foreach (string old in LegacyDx12Args) rest = ArgPat(old).Replace(rest, " ").Trim();
            string args = on ? (rest.Length > 0 ? rest + " " + ArgDx12 : ArgDx12) : rest;
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            m.LaunchArgs = args;
            m.Platform = g.Platform;
            if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
            MetaStore.Save(m);
            g.LaunchArgs = args;
        }

        // ---- 运行模式检测（v3.3.4）----
        // 用户反馈「开了也看不出有没有生效」——唯一可信的真话来源是游戏自己的 Unity 日志：
        //   %USERPROFILE%\AppData\LocalLow\<厂商>\<产品>\Player.log 里的
        //     "Version:  Direct3D 12" 或 "Direct3D 11.0"
        // 哪个日志属于哪款游戏：日志正文里有该游戏的数据目录路径（第 3 行就是），按路径匹配。
        static readonly Dictionary<string, string> PlayerLogCache = new Dictionary<string, string>();

        public static string RenderApi(DlssgGame g) { long t; return RenderApiEx(g, out t); }

        public static string RenderApiEx(DlssgGame g, out long logUnix)
        {
            logUnix = 0;
            if (g == null || g.Dir == null || g.Dir.Length == 0) return "";
            string log = FindPlayerLog(g.Dir);
            if (log.Length == 0) return "";
            try
            {
                logUnix = (long)(File.GetLastWriteTimeUtc(log) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                Match m = Regex.Match(File.ReadAllText(log), @"Version:\s+Direct3D\s+(\d+)");
                if (!m.Success) return "";
                string v = m.Groups[1].Value;
                return v == "12" ? "DX12" : (v.StartsWith("11") ? "DX11" : "");
            }
            catch { return ""; }
        }

        static string FindPlayerLog(string gameDir)
        {
            string key = gameDir.ToLowerInvariant();
            string hit;
            if (PlayerLogCache.TryGetValue(key, out hit)) return hit;
            string found = "";
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
                string needle = gameDir.Replace('\\', '/').TrimEnd('/');
                if (Directory.Exists(root))
                    foreach (string comp in Directory.GetDirectories(root))
                        foreach (string prod in Directory.GetDirectories(comp))
                        {
                            string log = Path.Combine(prod, "Player.log");
                            if (!File.Exists(log)) continue;
                            if (File.ReadAllText(log).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { found = log; break; }
                        }
            }
            catch { }
            PlayerLogCache[key] = found;
            return found;
        }

        public static void Rename(DlssgGame g, string name)
        {
            if (g == null) return;
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            m.Name = name == null ? "" : name.Trim();
            m.Platform = g.Platform;
            if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
            MetaStore.Save(m);
            if (m.Name.Length > 0) g.Title = m.Name;
        }

        public static void RememberAppId(DlssgGame g, string appid)
        {
            if (g == null) return;
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            m.AppId = appid == null ? "" : appid;
            MetaStore.Save(m);
            if (appid != null && appid.Length > 0 && appid != "-") g.AppId = appid;
        }

        public static string PlatformLabel(string p)
        {
            if (p == "steam") return "Steam";
            if (p == "epic") return "Epic";
            if (p == "wegame") return "WeGame";
            if (p == "battle") return "战网";
            if (p == "ubisoft") return "育碧";
            if (p == "ea") return "EA";
            if (p == "gog") return "GOG";
            return "本地";
        }
    }
}
