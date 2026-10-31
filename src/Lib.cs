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
        //  每游戏环境变量（2026-10-26 第 148 轮）：一行一条 `KEY=VALUE`。
        //  为什么用字符串而不是 Dictionary：多行文本框直接编辑、顺序稳定、写进 JSON 就是一个串；
        //  解析只在**启动那一刻**做一次，不值得为它引入一层对象。
        public string LaunchEnv = "";
        //  每游戏帧生成参数（2026-10-29 第 160 轮）。覆盖语义：0 / "" / -1 = 跟随全局，
        //  与 DlssgGame 的同名字段一一对应（见 Dlssg.cs 的注释）。
        public int FgMax;
        public string FgPreset = "";
        public int FgOpt = -1;
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
                    m.LaunchEnv = S(d, "launchEnv");
                    m.FgMax = (int)L(d, "fgMax");
                    m.FgPreset = S(d, "fgPreset");
                    //  fgOpt 缺键 = 跟随全局（-1），不能用 L() 的缺省 0 —— 0 是合法档位
                    m.FgOpt = d.ContainsKey("fgOpt") && d["fgOpt"] != null ? (int)L(d, "fgOpt") : -1;
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
                        d["launchEnv"] = m.LaunchEnv;
                        if (m.FgMax > 0) d["fgMax"] = m.FgMax;                  // 缺省值不落盘，JSON 保持瘦
                        if (m.FgPreset != null && m.FgPreset.Length > 0) d["fgPreset"] = m.FgPreset;
                        if (m.FgOpt >= 0) d["fgOpt"] = m.FgOpt;
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
                            //  ★ Steam 条目一律收录（2026-10-23 用户："Steam下载的一律收录就行"）：
                            //    Valve 自己的清单里写的是"这个 app 装在哪"，名字是官方名，
                            //    拿它去比对我们的"非游戏"词表是拿猜测覆盖事实 —— 因此不再按名字过滤。
                            //    只留一条例外：**Steamworks 公共运行库**（appid 228980）是 VC++/DirectX
                            //    运行库包，既不是游戏也不是软件，收进来只会多一条噪音条目。
                            //    （旧代码里写的是 228983，那是另一个 id，228980 靠 steamworks 词条挡住的）
                            if (appid.Length == 0 || name.Length == 0) continue;
                            if (appid == "228980" || appid == "228983") continue;
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
            @"trae\b|codebuddy|\bbuddy\b|akko|图吧|\biot\b|iot_|" +
            @"inno setup|compil|package cache|context menu|winhance|snap\.hutao|hutao|" +
            @"watt toolkit|steam\+\+|oopz|tiez|自考|工作台|创作|荣耀互联|hihonor|gameviewer|" +
            @"uu远程|qqmail|qq音乐|qqmusic|anticheat|anti-cheat|plugin|助手|" +
            // 对战平台 / 匹配平台（用户实测点名：5EClient、perfectworldarena = CS2 的对战平台，
            //  不是游戏本身；以前它们会以"电竞平台"的名义混进游戏库，2026-09-16）
            @"\b5e(client|box|对战|平台)|perfectworld|完美世界|对战平台|电竞平台|" +
            // 桌面美化 / Dock 类软件：Steam 上架了但不是游戏（MyDockFinder）。
            //  ⚠ Wallpaper Engine 已从这条里**移出**（2026-10-23 用户："wallpaper也算进游戏里"）：
            //    它在 Steam 里有正式条目（appid 431960），按游戏收录后能拿到官方名和封面。
            //    但"别让它触发游戏联动"这件事仍然要做 —— 那是另一层（Dlssg.ToolPart/
            //    PlatformPart 仍把它判成常驻工具，启动时不跑加速包）。两层职责分开，互不影响。
            @"mydockfinder|mydock\b|" +
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
        //  最近使用顺序（末尾=最新）。配合 MemCap 做 LRU 淘汰（2026-10-24 第 134 轮）。
        static readonly List<string> memOrder = new List<string>();
        // mem 在 UI 线程（卡片绘制）与后台线程（批量封面 ClearCache）并发访问，无锁时
        // Dictionary 内部桶可能被并发写坏（2026-09-15 白框事故的近亲，2026-09-30 体检）。
        // Own() 的位图复制也一并锁内完成：保证复制期间源图绝不会被 ClearCache Dispose 掉。
        static readonly object memLock = new object();

        public static void ClearCache()
        {
            //  这里 Dispose 是**对的**：mem 里存的每一张都是缓存自己 new 出来的原件
            //  （对外一律经 Own() 复制副本，见 Hit / Remember）。所以清表时释放，不会伤到
            //  任何控件手里的图 —— 控件拿到的都是副本，所有权在调用方。
            lock (memLock)
            {
                foreach (KeyValuePair<string, Image> kv in mem) { try { kv.Value.Dispose(); } catch { } }
                mem.Clear();
                memOrder.Clear();
            }
        }

        //  命中时把 key 挪到队尾（真正的 LRU：常用的不会被误淘汰）
        static Image Hit(string key)
        {
            Image cached;
            if (!mem.TryGetValue(key, out cached)) return null;
            memOrder.Remove(key);
            memOrder.Add(key);
            return Own(cached);
        }

        //  缓存上限：位图缓存原来无上限，常驻内存随「库大小 × 尺寸档」线性涨
        //  （13 款游戏全尺寸 40~60 张、每张 1~3 MB —— 是进程里最大的一块浮动项）。
        //  ★ 2026-10-24 第 134 轮：超限不再**整表清空**，改成 LRU 淘汰最旧的 1/4。
        //  整表清空的问题是"正在被看的那 8 张也一起被 Dispose 掉"，
        //  下次重绘要全部重新解码 —— 手感上就是滚动到某个位置突然卡一下。
        const int MemCap = 64;

        //  插入缓存（重复键先释放旧图）。必须在锁内完成插入，调用方随后在锁外 Own()。
        static void Remember(string key, Image img)
        {
            if (img == null) return;
            lock (memLock)
            {
                Image old;
                if (mem.TryGetValue(key, out old) && !ReferenceEquals(old, img))
                { try { old.Dispose(); } catch { } }
                if (mem.Count >= MemCap && !mem.ContainsKey(key))
                {
                    int drop = Math.Max(1, MemCap / 4);
                    int n = Math.Min(drop, memOrder.Count);
                    for (int i = 0; i < n; i++)
                    {
                        string k = memOrder[0];
                        memOrder.RemoveAt(0);
                        Image v;
                        if (mem.TryGetValue(k, out v))
                        {
                            if (!ReferenceEquals(v, img)) { try { v.Dispose(); } catch { } }
                            mem.Remove(k);
                        }
                    }
                }
                mem[key] = img;       // 当前这张最后放回去，保证调用方紧接着的 Own() 源图有效
                memOrder.Remove(key);
                memOrder.Add(key);
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

        class TimeoutWebClient : WebClient
        {
            public int TimeoutMs { get; set; }
            public TimeoutWebClient(int timeoutMs = 4000) { TimeoutMs = timeoutMs; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest req = base.GetWebRequest(address);
                if (req != null)
                {
                    req.Timeout = TimeoutMs;
                    var hr = req as HttpWebRequest;
                    if (hr != null) hr.ReadWriteTimeout = TimeoutMs;
                }
                return req;
            }
        }

        static byte[] Fetch(string url, int maxSize, int minSize, bool useProxy)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try
            {
                using (var wc = new TimeoutWebClient(4000))
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

        // 生成多别名候选，解决冒号、空格、中英文别名导致的封面无法匹配问题
        public static List<string> GetTitleCandidates(string title)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(title)) return res;
            string s = title.Trim();
            Action<string> Add = delegate(string v)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    string safe = SafeName(v);
                    if (!string.IsNullOrEmpty(safe) && !res.Contains(safe)) res.Add(safe);
                }
            };
            Add(s);
            if (s.Contains("：")) Add(s.Replace('：', '_'));
            if (s.Contains("：")) Add(s.Replace("：", ""));
            if (s.Contains(":")) Add(s.Replace(':', '_'));
            if (s.Contains(":")) Add(s.Replace(":", ""));
            if (s.Contains(" ")) Add(s.Replace(" ", ""));

            string lower = s.ToLowerInvariant();
            if (lower.Contains("星穹铁道") || lower.Contains("starrail"))
            { Add("崩坏：星穹铁道"); Add("崩坏_星穹铁道"); Add("StarRail"); Add("崩坏星穹铁道"); }
            if (lower.Contains("绝区零") || lower.Contains("zenless"))
            { Add("绝区零"); Add("ZenlessZoneZero"); }
            if (lower.Contains("原神") || lower.Contains("genshin") || lower.Contains("yuanshen"))
            { Add("原神"); Add("GenshinImpact"); }
            if (lower.Contains("鸣潮") || lower.Contains("wuthering"))
            { Add("鸣潮"); Add("WutheringWaves"); }
            if (lower.Contains("卡拉彼丘") || lower.Contains("calabiyau"))
            { Add("卡拉彼丘"); Add("Calabiyau"); }
            if (lower.Contains("无畏契约") || lower.Contains("valorant"))
            { Add("无畏契约"); Add("VALORANT"); }
            if (lower.Contains("三角洲") || lower.Contains("deltaforce"))
            { Add("三角洲行动"); Add("DeltaForce"); }
            if (lower.Contains("cs2") || lower.Contains("counter-strike"))
            { Add("Counter-Strike 2"); Add("cs2"); }

            return res;
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
        //  gen4 → gen5：改成 2 倍烘焙之后，旧的 1x 文件必须失效（否则照旧被放大）。
        //  版本号进文件名 = 生成规则一改，旧磁贴自动失效（不用手动清缓存）。
        //  gen5 → gen6（2026-10-22）：磁贴配色从"按标题 hash"改成"取图标主色"。
        //  gen6 → gen7（2026-10-22）：顶部提亮从 118° 斜向改成垂直渐变 ——
        //        斜向那条在卡片 50% 高度处留一道水平切缝（用户截图的"磁贴瑕疵"）。
        public static string GenFile(string id) { return Path.Combine(CoversDir, id + "_gen7.png"); }

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
                using (Image src = Image.FromFile(srcFile))
                    return SetManualCoverImage(g, src);
            }
            catch (Exception ex) { return ex.Message; }
        }

        //  内存图直接落盘（2026-10-23 自定义封面入口之一：剪贴板粘贴）。
        //  以前只能"选文件"，用户截完图还得先存盘 —— 多两步就没人用了。
        //  落盘逻辑与上面那条完全同一套（同一文件名、同一清缓存），不另起一条分支。
        public static string SetManualCoverImage(DlssgGame g, Image img)
        {
            try
            {
                if (g == null || img == null) return "没有图片";
                if (img.Width < 100 || img.Height < 100) return "图片太小（至少 100×100）";
                Directory.CreateDirectory(CoversDir);
                //  ⚠ 一律转存成 .png（2026-10-22）：以前按源文件扩展名原样复制，
                //  选 .bmp / .webp 时存成 `<标题>.bmp`，而 Card() 的候选链只找 png/jpg/jpeg ——
                //  于是"设置成功"但卡片照旧，用户以为没生效。
                string dst = Path.Combine(CoversDir, SafeName(g.Title) + ".png");
                using (Bitmap bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics gg = Graphics.FromImage(bmp))
                    {
                        gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        gg.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                    }
                    bmp.Save(dst, ImageFormat.Png);
                }
                // 清掉同名的其它扩展，避免旧封面抢先
                foreach (string ext in new string[] { ".jpg", ".jpeg" })
                {
                    string p = Path.Combine(CoversDir, SafeName(g.Title) + ext);
                    if (File.Exists(p) && !Eq(p, dst)) { try { File.Delete(p); } catch { } }
                }
                ClearCache();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        //  移除封面（2026-10-22）：删掉这个条目**所有**可能的封面文件，让它回到
        //  "exe 图标磁贴 / Steam 官方封面"的自动状态。以前没有这个入口 ——
        //  手动设的、搜图存的、CDN 下的一律叫 `<标题>.*` 或 `<id>.*`，用户想撤销只能自己去翻目录。
        public static void ClearCover(DlssgGame g)
        {
            if (g == null) return;
            try
            {
                string nm = SafeName(g.Title);
                var kill = new List<string>();
                foreach (string ext in new string[] { ".png", ".jpg", ".jpeg" })
                    kill.Add(Path.Combine(CoversDir, nm + ext));
                string id = g.Id == null ? "" : g.Id;
                if (id.Length > 0)
                {
                    kill.Add(CardFile(id)); kill.Add(CardFilePng(id)); kill.Add(GenFile(id));
                    kill.Add(HeroFile(id));                       // 详情页的横幅也要一起退回自动
                }
                foreach (string p in kill)
                {
                    try { if (p != null && p.Length > 0 && File.Exists(p)) File.Delete(p); } catch { }
                }
                ClearCache();
                Program.Log("  已移除封面：" + (g.Title == null ? "" : g.Title));
            }
            catch { }
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
                string norm = NormalizeGameTitle(g.Title);
                string nm = SafeName(norm);
                string dst = Path.Combine(CoversDir, nm + ".jpg");
                File.WriteAllBytes(dst, data);
                if (g.Id != null && g.Id.Length > 0)
                {
                    try { File.WriteAllBytes(CardFile(g.Id), data); } catch { }
                    try { if (File.Exists(GenFile(g.Id))) File.Delete(GenFile(g.Id)); } catch { }
                }
                foreach (string ext in new string[] { ".png", ".jpeg" })
                {
                    string p = Path.Combine(CoversDir, nm + ext);
                    if (File.Exists(p) && !Eq(p, dst)) { try { File.Delete(p); } catch { } }
                }
                ClearCache();
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        //  官网图专用落盘：与 SaveCoverBytes 唯一的区别是**没有竖版门禁**。
        //  平台客户端（5E / 完美世界）根本不存在竖版海报，官网给的多是横版主视觉；
        //  卡片渲染是 cover 裁切，横图裁两侧即可 —— 总比"配一张别家的竖版海报"强。
        static string SaveCoverBytesLoose(DlssgGame g, byte[] data)
        {
            try
            {
                if (g == null || data == null || data.Length < 1000) return "数据无效";
                try
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image img = Image.FromStream(ms))
                    {
                        if (img.Width < 400 || img.Height < 300) return string.Format("图片太小（{0}x{1}）", img.Width, img.Height);
                    }
                }
                catch { return "封面数据无法解码"; }
                Directory.CreateDirectory(CoversDir);
                string dst = Path.Combine(CoversDir, SafeName(g.Title) + ".jpg");
                File.WriteAllBytes(dst, data);
                foreach (string ext in new string[] { ".png", ".jpeg" })
                {
                    string p2 = Path.Combine(CoversDir, SafeName(g.Title) + ext);
                    if (File.Exists(p2) && !Eq(p2, dst)) { try { File.Delete(p2); } catch { } }
                }
                if (g.Id != null && g.Id.Length > 0)
                {
                    try { if (File.Exists(GenFile(g.Id))) File.Delete(GenFile(g.Id)); } catch { }
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

        //  从网页里抠 og:image / twitter:image（属性顺序两种都试）
        static string MetaImage(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string[] pats = new string[] {
                "<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"']([^\"']+)",
                "<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+property=[\"']og:image[\"']",
                "<meta[^>]+name=[\"']twitter:image[\"'][^>]+content=[\"']([^\"']+)" };
            foreach (string pat in pats)
            {
                Match m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return m.Groups[1].Value.Replace("&amp;", "&");
            }
            return "";
        }

        //  「官网 og:image」优先（2026-10-22）：对 5EClient / 完美世界竞技平台这类**平台客户端**，
        //  名字搜图必然混入同名内容（旅游海报、电影剧照）—— 官网首页的主视觉才是官方那张。
        //  比例放宽到 0.45~2.4：官网图多是横版，卡片会 cover 裁切，总比配错图强。
        static string TryOfficialCover(DlssgGame g, string q)
        {
            try
            {
                byte[] raw = FetchAny("https://www.bing.com/search?q=" + Uri.EscapeDataString(q + " 官网"), 2 << 20, 2000);
                if (raw == null) return "官网检索失败";
                string html = Encoding.UTF8.GetString(raw);
                int p = 0, tried = 0;
                while (tried < 4)
                {
                    int h2 = html.IndexOf("<h2><a ", p, StringComparison.OrdinalIgnoreCase);
                    if (h2 < 0) break;
                    int hs = html.IndexOf("href=\"", h2, StringComparison.OrdinalIgnoreCase);
                    if (hs < 0) break;
                    hs += 6;
                    int he = html.IndexOf('"', hs);
                    if (he <= hs) break;
                    string url = html.Substring(hs, he - hs);
                    p = he;
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (url.IndexOf("bing.com", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (url.IndexOf("microsoft.com", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    tried++;
                    byte[] page = FetchAny(url, 3 << 20, 1000);
                    if (page == null) continue;
                    string img = MetaImage(Encoding.UTF8.GetString(page));
                    if (img.Length == 0) continue;
                    try { img = new Uri(new Uri(url), img).ToString(); } catch { }
                    byte[] d = FetchAny(img, 12 << 20, 6000);
                    if (d == null) continue;
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(d))
                        using (Image im = Image.FromStream(ms))
                        {
                            if (im.Width < 400 || im.Height < 400) continue;
                            double ar = (double)im.Width / im.Height;
                            if (ar < 0.45 || ar > 2.4) continue;
                        }
                    }
                    catch { continue; }
                    if (SaveCoverBytesLoose(g, d).Length == 0)
                    {
                        Program.Log("  官网封面命中：" + g.Title + " ← " + ShortUrl(img));
                        return "";
                    }
                }
            }
            catch { }
            return "官网没找到可用图";
        }

        public static string NormalizeGameTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (s.EndsWith(" Game", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 5).Trim();
            if (s.EndsWith(" Client", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 7).Trim();
            string low = s.ToLowerInvariant().Replace(" ", "").Replace(":", "").Replace("-", "").Replace("_", "");
            if (low.Contains("starrail") || low.Contains("honkai") || low.Contains("星穹铁道")) return "崩坏：星穹铁道";
            if (low.Contains("genshin") || low.Contains("yuanshen") || low.Contains("原神")) return "原神";
            if (low.Contains("zenless") || low.Contains("zzz") || low.Contains("绝区零")) return "绝区零";
            if (low.Contains("wutheringwaves") || low.Contains("鸣潮")) return "鸣潮";
            if (low.Contains("valorant") || low.Contains("无畏契约")) return "无畏契约";
            if (low.Contains("blackmyth") || low.Contains("wukong") || low.Contains("黑神话")) return "黑神话：悟空";
            if (low.Contains("deltaforce") || low.Contains("三角洲")) return "三角洲行动";
            if (low.Contains("naraka") || low.Contains("永劫无间")) return "永劫无间";
            return s;
        }

        // 成功返回 ""，失败/找不到返回原因
        public static string SearchWebCover(DlssgGame g)
        {
            if (g == null || g.Title == null || g.Title.Trim().Length < 2) return "名字太短，无法检索";
            string q = NormalizeGameTitle(g.Title);
            //  ① 先试官网主视觉（最准）
            if (TryOfficialCover(g, q).Length == 0) return "";
            //  ② 再走图片搜索，但**按标题相关性排序**：对得上的先用，对不上的只做兜底
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
                //  相关性排序：标题对得上的排前面，对不上的留在后面当兜底（一张都不匹配时才用）
                var strict = new List<string>();
                var loose = new List<string>();
                foreach (string[] hit in BingImageHits(html, 40))
                {
                    string u0 = hit[1];
                    if (BadImageHost(u0)) continue;
                    (TitleMatch(hit[0], q) ? strict : loose).Add(u0);
                }
                if (strict.Count == 0)
                {
                    //  ★ 没有任何一张图的标题跟游戏名对得上 —— 这时候"兜底"就是"配错图"
                    //  （5EClient 搜出自媒体教程封面就是这么来的）。宁可明说失败，让用户手动给图。
                    Program.Log("  搜图放弃：候选里没有一张的标题与「" + q + "」相关（loose=" + loose.Count + "）");
                    return "未找到与名字相关的封面（建议手动指定图片）";
                }
                var ordered = new List<string>(strict);
                ordered.AddRange(loose);
                Program.Log("  搜图相关性：匹配 " + strict.Count + " 张 / 其余 " + loose.Count + " 张（兜底）");
                foreach (string cand0 in ordered)
                {
                    string cand = cand0;
                    if (seen.ContainsKey(cand)) continue;
                    seen[cand] = 1;
                    if (++tried > 24) break;
                    byte[] d = FetchAny(cand, 12 << 20, 8000);
                    if (d == null) continue;
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(d))
                        using (Image img = Image.FromStream(ms))
                        {
                            if (img.Width < 380 || img.Height < 520) continue;      // 太小
                            double ar = (double)img.Width / img.Height;
                            // 只收接近 2:3 的竖版（0.58~0.78）。放宽到 0.95 会收到
                            // 「三格拼图 / 堆叠宣传图」这类东西 —— 实测无畏契约就中过招。
                            if (ar < 0.52 || ar > 0.82) continue;
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

        //  图片结果里每条都带一个 `t`（页面标题）—— 这是判断"这张图是不是这个游戏的"唯一线索。
        //  以前只抠 murl，于是一张"5E 相关"的旅游海报也能凭尺寸过关（2026-10-22 用户报的正是这个）。
        static List<string[]> BingImageHits(string html, int max)
        {
            var res = new List<string[]>();
            if (html == null) return res;
            string[] tags = new string[] { "&quot;murl&quot;:&quot;", "murl" + '"' + ':' + '"' };
            foreach (string tag in tags)
            {
                bool esc = tag[0] == '&';
                string endTag = esc ? "&quot;" : "\"";
                string tTag = esc ? "&quot;t&quot;:&quot;" : "t" + '"' + ':' + '"';
                int p = 0;
                while (res.Count < max)
                {
                    p = html.IndexOf(tag, p);
                    if (p < 0) break;
                    int us = p + tag.Length;
                    int e = html.IndexOf(endTag, us);
                    if (e < 0) break;
                    string u = html.Substring(us, e - us).Replace("&amp;", "&").Replace("\\/", "/");
                    p = e;
                    if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    string title = "";
                    int w0 = Math.Max(0, e - 1400), w1 = Math.Min(html.Length, e + 1400);
                    string win = html.Substring(w0, w1 - w0);
                    int tp = win.IndexOf(tTag);
                    if (tp >= 0)
                    {
                        int ts = tp + tTag.Length;
                        int te = win.IndexOf(endTag, ts);
                        if (te > ts) title = win.Substring(ts, te - ts).Replace("&amp;", "&");
                    }
                    res.Add(new string[] { title, u });
                }
                if (res.Count > 0) break;
            }
            return res;
        }

        //  结果标题与游戏名是否"像同一个东西"：去符号小写后互相包含，或最长公共子串 ≥ min(3, 名字长度)。
        //  目的是把"同名的旅游照 / 电影海报"排到后面，而不是一刀切掉（否则容易一张都搜不到）。
        static bool TitleMatch(string pageTitle, string game)
        {
            string a = Regex.Replace((pageTitle == null ? "" : pageTitle).ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+", "");
            string b = Regex.Replace((game == null ? "" : game).ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+", "");
            if (a.Length == 0 || b.Length == 0) return false;
            if (a.Contains(b) || b.Contains(a)) return true;
            int need = Math.Min(3, b.Length);
            if (need < 2) return false;
            int best = 0;
            for (int i = 0; i < b.Length; i++)
                for (int j = 0; j < a.Length; j++)
                {
                    int k = 0;
                    while (i + k < b.Length && j + k < a.Length && b[i + k] == a[j + k]) k++;
                    if (k > best) best = k;
                }
            return best >= need;
        }

        //  Bing 图片搜索结果里图片直链的形式实测为：
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
                //  ⚠ 不再 return first（2026-10-22 严重问题）：
                //  Steam 的 SearchApps 是**模糊搜索**，第一个结果经常是别家游戏 ——
                //  用户库里 5EClient / 完美世界竞技平台 被配上了完全无关的封面，
                //  就是"名字没搜到 → 拿第一个 appid 顶替"造成的。
                //  现在只认"精确同名"，或"一方包含另一方且都不算短串"；否则宁可返回空。
                string best = "";
                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("appid") || d["appid"] == null) continue;
                    string id = d["appid"].ToString();
                    string rn = Regex.Replace((d.ContainsKey("name") && d["name"] != null ? d["name"].ToString() : "").ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+", "");
                    if (rn.Length == 0) continue;
                    if (rn == n) return id;      // 精确同名优先
                    if (best.Length == 0 && rn.Length >= 4 && n.Length >= 4
                        && (rn.Contains(n) || n.Contains(rn))) best = id;
                }
                return best;
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
        //  GDI+ 单步缩小超过约 4 倍会出条带/接缝（7000px 的壁纸直接缩到 800 宽，
        //  画面上就会多出一条贯穿的暗线）。先逐级减半到目标的 4 倍以内，再精确缩放。
        //  Fit（裁切填满）与 ResizeTo（等比不裁）共用这一步 —— 别各写一份。
        static Image StepHalve(Image src, int w, int h, ref Bitmap scratch)
        {
            Image work = src;
            while (work.Width > w * 4 || work.Height > h * 4)
            {
                var next = new Bitmap(Math.Max(1, work.Width / 2), Math.Max(1, work.Height / 2));
                using (var g2 = Graphics.FromImage(next))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g2.CompositingQuality = CompositingQuality.HighQuality;
                    g2.DrawImage(work, 0, 0, next.Width, next.Height);
                }
                if (scratch != null) scratch.Dispose();
                scratch = next;
                work = scratch;
                if (work.Width <= 4 || work.Height <= 4) break;
            }
            return work;
        }

        public static Bitmap Fit(Image src, int w, int h)
        {
            Bitmap mid = null;
            try
            {
                Image work = StepHalve(src, w, h, ref mid);

                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    double tr = (double)w / h, sr = (double)work.Width / work.Height;
                    int cw, ch;
                    if (sr > tr) { ch = work.Height; cw = (int)Math.Round(work.Height * tr); }
                    else { cw = work.Width; ch = (int)Math.Round(work.Width / tr); }
                    int cx = (work.Width - cw) / 2;
                    int cy = (int)Math.Round((work.Height - ch) * (sr > tr ? 0.42 : 0.5));
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(work, new Rectangle(0, 0, w, h), cx, cy, cw, ch, GraphicsUnit.Pixel, ia);
                    }
                }
                return bmp;
            }
            finally { if (mid != null) mid.Dispose(); }
        }

        //  等比缩放（**不裁切**）—— 横幅专用。
        //  为什么需要它：横幅最终要按"实际横幅比例"取景，那一刀由 HeadBox.Compose 裁。
        //  在这里再裁一次就是两刀，取景比素材明显"往里怼"（见 Hero 的注释）。
        public static Bitmap ResizeTo(Image src, int w, int h)
        {
            Bitmap mid = null;
            try
            {
                Image work = StepHalve(src, w, h, ref mid);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(work, new Rectangle(0, 0, w, h), 0, 0, work.Width, work.Height,
                                    GraphicsUnit.Pixel, ia);
                    }
                }
                return bmp;
            }
            catch { return null; }
            finally { if (mid != null) mid.Dispose(); }
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

        //  顶部平坦边（与底部同款逻辑）。只认"近黑 / 近白"的整行 —— 画面里那种暗色天空、
        //  纯色渐变背景不会被误裁，否则会切掉真画面（Steam 的 library 图经常上下各带一条黑边）。
        static int CountFlatTop(Image src, double maxCutFrac)
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
                    for (int y = 0; y < sh / 2; y++)
                    {
                        int mn = 255, mx = 0, sum = 0;
                        for (int x = 0; x < sw; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                            if (lum < mn) mn = lum;
                            if (lum > mx) mx = lum;
                            sum += lum;
                        }
                        int avg = sum / sw;
                        bool flat = (mx - mn) <= 10;
                        bool edge = avg < 45 || avg > 210;
                        if (flat && edge && cut < maxCut) cut = y + 1;
                        else break;
                    }
                    return cut > 0 ? (int)((double)cut / sh * src.Height) : 0;
                }
            }
            catch { return 0; }
        }

        // 封面专用加载：先裁空底/空边再 cover-crop（生成磁贴不走这里，它们本来就是成品构图）
        static Image LoadCoverScaled(string path, int w, int h)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image tmp = Image.FromStream(fs))
                {
                    int cutB = CountFlatBottom(tmp, 0.40);
                    int cutT = CountFlatTop(tmp, 0.40);
                    //  左右也照裁（2026-10-22 用户"封面角落有瑕疵"）：只处理上下两条的话，
                    //  带**侧边黑边/白边**的素材（国产客户端横图、截图类封面常见）裁完
                    //  角上还剩一块黑/白 —— cover-crop 之后正好怼在卡片左上角上。
                    int cutL = CountFlatLeft(tmp, 0.30);
                    int cutR = CountFlatRight(tmp, 0.30);
                    int nx = tmp.Width - cutL - cutR, nh = tmp.Height - cutT - cutB;
                    bool any = cutT > 0 || cutB > 0 || cutL > 0 || cutR > 0;
                    if (any && nx > 40 && nh > 40 && nx < tmp.Width - 4 && nh < tmp.Height - 4)
                    using (Image trimmed = ((Bitmap)tmp).Clone(new Rectangle(cutL, cutT, nx, nh), tmp.PixelFormat))
                        return Fit(trimmed, w, h);
                    return Fit(tmp, w, h);
                }
            }
            catch { return null; }
        }

        //  与 CountFlatTop 同款判据的左右版本：只认"近黑(<45)/近白(>210) 的整列"，
        //  防止把画面里真正的暗色/亮色竖条（柱子、墙角、海报边框设计）误裁掉。
        static int CountFlatLeft(Image src, double maxCutFrac)
        {
            try
            {
                int sh = 64;
                int sw = (int)((double)src.Width / src.Height * sh);
                if (sw < 8) return 0;
                using (Bitmap small = new Bitmap(src, sw, sh))
                {
                    int cut = 0;
                    int maxCut = (int)(sw * maxCutFrac);
                    for (int x = 0; x < sw / 2; x++)
                    {
                        int mn = 255, mx = 0, sum = 0;
                        for (int y = 0; y < sh; y++)
                        {
                            Color c = small.GetPixel(x, y);
                            int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                            if (lum < mn) mn = lum;
                            if (lum > mx) mx = lum;
                            sum += lum;
                        }
                        int avg = sum / sh;
                        bool flat = (mx - mn) <= 10;
                        bool edge = avg < 45 || avg > 210;
                        if (flat && edge && cut < maxCut) cut = x + 1;
                        else break;
                    }
                    return cut > 0 ? (int)((double)cut / sw * src.Width) : 0;
                }
            }
            catch { return 0; }
        }

        static int CountFlatRight(Image src, double maxCutFrac)
        {
            try
            {
                int sh = 64;
                int sw = (int)((double)src.Width / src.Height * sh);
                if (sw < 8) return 0;
                using (Bitmap small = new Bitmap(src, sw, sh))
                {
                    int cut = 0;
                    int maxCut = (int)(sw * maxCutFrac);
                    for (int x = sw - 1; x > sw / 2; x--)
                    {
                        int mn = 255, mx = 0;
                        for (int y = 0; y < sh; y++)
                        {
                            Color c = small.GetPixel(x, y);
                            int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                            if (lum < mn) mn = lum;
                            if (lum > mx) mx = lum;
                        }
                        if (mx - mn <= 10 && cut < maxCut) cut = sw - x;
                        else break;
                    }
                    return cut > 0 ? (int)((double)cut / sw * src.Width) : 0;
                }
            }
            catch { return 0; }
        }

        //  等比缩放到"不超过 maxW"（不裁切、不放大）—— 横幅用。
        //  不放大是有意的：小图放大只会糊，不如按原样用，让 Compose 去 zoom。
        static Image LoadWide(string path, int maxW)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image tmp = Image.FromStream(fs))
                {
                    int w = Math.Min(maxW, tmp.Width);
                    if (w < 32) return null;
                    int h = Math.Max(1, (int)Math.Round((double)tmp.Height * w / tmp.Width));
                    return ResizeTo(tmp, w, h);
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
        //  图标主色：跳过透明 / 近黑 / 近白 / 低饱和像素，按"饱和度 × 亮度"加权投票。
        //  纯黑白图标（比如很多工具类）取不到彩色 → 返回 Empty，调用方回落到按标题 hash 的色板。
        static Color DominantColor(Image icon, Color fallback)
        {
            try
            {
                using (var small = new Bitmap(icon, 32, 32))
                {
                    double sr = 0, sg = 0, sb = 0, sw = 0;
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            if (c.A < 200) continue;
                            int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                            int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                            float sat = mx == 0 ? 0f : (float)(mx - mn) / mx;
                            float val = mx / 255f;
                            if (sat < 0.22f || val < 0.18f || val > 0.97f) continue;
                            double wgt = sat * val;
                            sr += c.R * wgt; sg += c.G * wgt; sb += c.B * wgt; sw += wgt;
                        }
                    if (sw < 1) return fallback;
                    return Color.FromArgb((int)(sr / sw), (int)(sg / sw), (int)(sb / sw));
                }
            }
            catch { return fallback; }
        }

        public static Bitmap GradientCard(string name, int w, int h, Image icon)
        {
            byte[] dig;
            using (SHA256 sha = SHA256.Create()) dig = sha.ComputeHash(Encoding.UTF8.GetBytes(name == null ? "" : name));
            int[] pair = GenPairs[dig[0] % GenPairs.Length];
            Color c1 = FromRgb(pair[0]), c2 = FromRgb(pair[1]);

            //  ★ 有图标就用**图标自己的主色**（2026-10-22）：
            //  以前配色是"按标题 hash 从色板里抽一组"，于是 5E 顶着橙绿渐变、完美世界顶着粉紫 ——
            //  跟品牌毫无关系，用户一眼就觉得"这不是它的封面"。
            //  主色来自官方图标，磁贴就有了品牌感（黑蓝的 5E、金红的完美）。
            if (icon != null)
            {
                Color dom = DominantColor(icon, Color.Empty);
                if (dom != Color.Empty)
                {
                    c1 = Darken(dom, 0.55);
                    c2 = Lighten(dom, 0.18);
                }
            }

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

                // 顶部提亮（收边提质感）。
                //  ⚠ 必须是"alpha 在填充矩形内部就降到 0"的渐变：以前是 118° 斜向高光，
                //    画刷矩形 (-w,-h,2w,1.5h) 的下边缘正好落在卡片 50% 高度处，而 118° 的
                //    梯度沿那条边还没走到 0 → 一整条水平切缝（像素实测：y=h/2 处 RGB 跳变 9~16），
                //    用户截图里磁贴中部的"横线瑕疵"就是它。垂直渐变 + 底边 alpha=0，物理上无缝。
                var hlRect = new Rectangle(0, 0, w, h * 2 / 5);
                using (LinearGradientBrush hl = new LinearGradientBrush(hlRect,
                           Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(hl, hlRect);
            }
            return bmp;
        }

        //  与 Lighten 配套：往黑色压，k=0.55 大约是"同一个色相、暗 55%"
        static Color Darken(Color c, double k)
        {
            return Color.FromArgb(
                Math.Max(0, (int)(c.R * (1 - k))),
                Math.Max(0, (int)(c.G * (1 - k))),
                Math.Max(0, (int)(c.B * (1 - k))));
        }

        static Color Lighten(Color c, double k)
        {
            return Color.FromArgb(
                Math.Min(255, (int)(c.R + (255 - c.R) * k)),
                Math.Min(255, (int)(c.G + (255 - c.G) * k)),
                Math.Min(255, (int)(c.B + (255 - c.B) * k)));
        }

        // 卡片图：内存缓存 + 磁盘缓存（避免每次刷新重算）
        // ---- hero 横幅（2026-10-18）----
        //  以前直接拿**竖版**封面塞进 800×196 的横条：600×900 放大到 800 宽再裁掉上下 3/4，
        //  又糊又只剩中间一条。Steam 库页的横幅用的是横版 library_hero（1920×620）。
        //  这里优先取横版，取不到才退回竖版（会糊，但至少不是空白）。
        public static string HeroFile(string id) { return Path.Combine(CoversDir, id + "_hero.jpg"); }

        //  ⚠ 参数是"最大宽度"，**不传目标比例**（2026-10-20 核对 Steam 素材后改）。
        //  原来传 1200×540（2.22:1）让 Fit 先裁成那个比例，可素材比例根本不统一：
        //  实测本机 hero 全是 16:9（1.78，Bing 搜的壁纸），Steam 官方 library_hero 是 3.10。
        //  固定一个请求比例 ⇒ 总有一方被白白裁掉，再被横幅裁第二刀，取景比素材明显"往里怼"。
        //  现在只按宽度等比缩小，取景那一刀交给 HeadBox.Compose（按实际横幅比例裁一次）。
        public static Image Hero(DlssgGame g, int maxW)
        {
            if (g == null) return null;
            string id = g.Id == null ? "" : g.Id;
            string key = id + "|hero|" + maxW;
            Image cached;
            //  ★ 命中缓存也要交**副本**（2026-10-26 第 149 轮）：原来这里直接 return cached，
            //  于是 Card() 的语义是"第一次给副本、之后给缓存本体"—— 调用方一律按"自己持有、自己释放"
            //  写（CoverTile / LibHero / LibActionBar 都在 Dispose 时释放 cover），
            //  第二次取同一个 key 就会把缓存本体销毁掉，之后任何一次绘制都是在已销毁的
            //  GDI+ 句柄上作图 → ArgumentException「参数无效」→ 控件被填白打叉。
            //  这与 0915 那次"16 张白框红叉"是同一个根因的残留分支：缓存自己持有，调用方只拿副本。
            lock (memLock) { cached = Hit(key); if (cached != null) return Own(cached); }
            try
            {
                // 1. 优先在 covers/ 查找根据游戏标题命名的 _hero 横幅或通用高清封面
                foreach (string cName in GetTitleCandidates(g.Title))
                {
                    string hf = Path.Combine(CoversDir, cName + "_hero.jpg");
                    if (!File.Exists(hf)) hf = Path.Combine(CoversDir, cName + "_hero.png");
                    if (!File.Exists(hf)) hf = Path.Combine(CoversDir, cName + ".jpg");
                    if (File.Exists(hf))
                    {
                        Image img = LoadWide(hf, maxW);
                        if (img != null)
                        {
                            Remember(key, img);
                            return Own(img);
                        }
                    }
                }

                if (id.Length > 0)
                {
                    string cache = HeroFile(id);
                    string hid = AppIdOf(g);
                    if (!File.Exists(cache) && hid.Length > 0)
                        CopyLocalHero(hid, cache);
                    if (File.Exists(cache))
                    {
                        Image img = LoadWide(cache, maxW);
                        if (img != null)
                        {
                            Remember(key, img);
                            return Own(img);
                        }
                    }
                }
            }
            catch { }
            //  没有横版就后台搜一张（只试一次）：第一次显示纯色底，搜到后自动换成整幅横图
            if (id.Length > 0 && !HeroTried(id) && Program.Cfg.DlssgOnlineCovers)
            {
                MarkHeroTried(id);
                DlssgGame gg = g;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        if (SearchHeroCover(gg).Length == 0)
                        {
                            ClearCache();
                            if (HeroReady != null) HeroReady();
                        }
                    }
                    catch { }
                });
            }
            //  没有横版素材：不硬裁竖版（600×900 塞进 4:1 只截中间一条，残缺且糊），
            //  改成「竖版封面缩到极小再放大」的廉价模糊底 —— 有图、看不出裁切，标题压在上面也清楚。
            //  这里的画布按 16:9 算（横幅实际比例由 Compose 再裁，这层只是兜底底图）。
            int fw = maxW, fh = Math.Max(1, maxW * 9 / 16);
            Image fallback = Card(g, fw, fh);
            if (fallback == null) return null;
            return AmbientWithCover(g, fallback, fw, fh);
        }

        //  无横版时的氛围底：模糊背景 + **右下角放完整竖版封面**（等比缩小，不裁切）。
        //  模糊底只是氛围，真正"看得全"的是右下那张小图。
        static Image AmbientWithCover(DlssgGame g, Image cardFit, int w, int h)
        {
            //  底色不模糊：取封面主色压暗当纯色背景（用户原话："别加模糊感，要 CS2 那种干净整幅"）
            Image canvas = SolidBackdrop(cardFit, w, h);
            if (canvas == null) return null;
            try
            {
                byte[] raw = RawCoverBytes(g);
                if (raw != null)
                {
                    using (var ms = new MemoryStream(raw))
                    using (Image full = Image.FromStream(ms))
                    {
                        int ch = h - Theme.S(36);
                        int cw = (int)Math.Round((double)full.Width * ch / full.Height);
                        if (cw > 0 && cw <= w / 2)
                        {
                            using (Bitmap scaled = GameIcon.Scale(full, cw, ch))
                            using (Bitmap rounded = RoundAlpha(scaled, Theme.RCard))
                            using (var gr = Graphics.FromImage(canvas))
                            {
                                gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                gr.DrawImage(rounded, w - cw - Theme.S(18), Theme.S(18));
                            }
                        }
                    }
                }
            }
            catch { }
            return canvas;
        }

        //  原始封面字节（不做任何裁切）：与 Card() 同一套候选，但直接返回文件内容
        static byte[] RawCoverBytes(DlssgGame g)
        {
            try
            {
                string nm = SafeName(g.Title);
                string id = g.Id == null ? "" : g.Id;
                List<string> cand = new List<string>();
                cand.Add(Path.Combine(CoversDir, nm + ".png"));
                cand.Add(Path.Combine(CoversDir, nm + ".jpg"));
                cand.Add(Path.Combine(CoversDir, nm + ".jpeg"));
                if (id.Length > 0) { cand.Add(CardFile(id)); cand.Add(CardFilePng(id)); }
                foreach (string p in cand)
                {
                    if (!File.Exists(p)) continue;
                    byte[] d = File.ReadAllBytes(p);
                    if (d.Length > 1000) return d;
                }
            }
            catch { }
            return null;
        }

        //  纯色底：把封面缩成 1×1 取主色，压暗后铺满 + 上下加深，替代模糊层
        static Image SolidBackdrop(Image src, int w, int h)
        {
            try
            {
                Color c = Color.FromArgb(40, 46, 58);
                try
                {
                    using (var one = new Bitmap(1, 1))
                    {
                        using (var g1 = Graphics.FromImage(one))
                        {
                            g1.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g1.DrawImage(src, 0, 0, 1, 1);
                        }
                        Color s0 = one.GetPixel(0, 0);
                        c = Color.FromArgb(s0.R * 45 / 100 + 10, s0.G * 45 / 100 + 10, s0.B * 45 / 100 + 14);
                    }
                }
                catch { }
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var b = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                               Color.FromArgb(Math.Min(255, c.R + 18), Math.Min(255, c.G + 18), Math.Min(255, c.B + 22)), c, 25f))
                        g.FillRectangle(b, 0, 0, w, h);
                }
                return bmp;
            }
            catch { return null; }
        }

        //  廉价模糊：缩到 1/12 再双三次放大。GDI+ 没有高斯模糊，这两步足够糊掉细节、又几乎不耗时。
        static Image BlurFill(Image src, int w, int h)
        {
            try
            {
                int sw = Math.Max(6, w / 16), sh = Math.Max(4, h / 16);
                using (var small = new Bitmap(sw, sh))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, sw, sh);
                    }
                    var big = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(small, 0, 0, w, h);
                        //  压暗一档，保证白色标题与副标题在任何封面上都读得清
                        using (var b = new SolidBrush(Color.FromArgb(120, 8, 12, 18)))
                            g.FillRectangle(b, 0, 0, w, h);
                    }
                    return big;
                }
            }
            catch { return src; }
        }

        // ---- 横版主视觉搜索（2026-10-18）----
        //  Steam 有 library_hero，非 Steam 没有。这里按名字搜一张 16:9 的壁纸当横幅，
        //  存 <id>_hero.jpg 后就不再联网（跟封面一样只补一次）。
        static readonly HashSet<string> heroTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static Action HeroReady;      // 由 MainForm 注册：搜到了就刷新当前横幅

        static bool HeroTried(string id) { lock (heroTried) return heroTried.Contains(id); }
        static void MarkHeroTried(string id) { lock (heroTried) heroTried.Add(id); }

        public static string SearchHeroCover(DlssgGame g)
        {
            if (g == null || g.Title == null || g.Title.Trim().Length < 2) return "名字太短";
            string id = g.Id == null ? "" : g.Id;
            if (id.Length == 0) return "无 id";
            try
            {
                string[] tails = new string[] { " 游戏 壁纸 1920x1080", " 官方 主视觉 横版" };
                foreach (string tail in tails)
                {
                    string u = "https://www.bing.com/images/search?q="
                             + Uri.EscapeDataString(g.Title.Trim() + tail) + "&form=HDRSC2";
                    byte[] raw = FetchAny(u, 6 << 20, 2000);
                    if (raw == null) continue;
                    string html = Encoding.UTF8.GetString(raw);
                    int tried = 0;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string cand in BingImageUrls(html, 30))
                    {
                        if (!seen.Add(cand)) continue;
                        if (BadImageHost(cand)) continue;
                        if (++tried > 20) break;
                        byte[] d = FetchAny(cand, 12 << 20, 30000);
                        if (d == null) continue;
                        try
                        {
                            using (var ms = new MemoryStream(d))
                            using (Image img = Image.FromStream(ms))
                            {
                                double ar = (double)img.Width / img.Height;
                                //  只要接近 16:9 的大图：横幅要横、要够大，竖版/方图一律不要
                                if (img.Width < 1280 || ar < 1.5 || ar > 2.1) continue;
                            }
                        }
                        catch { continue; }
                        Directory.CreateDirectory(CoversDir);
                        File.WriteAllBytes(HeroFile(id), d);
                        Program.Log("  横幅命中（按名字搜）：" + g.Title + " ← " + ShortUrl(cand));
                        return "";
                    }
                }
            }
            catch { }
            return "未找到横版";
        }

        //  Steam 本地缓存的横版主视觉：library_hero*.jpg（要排除 *_blur_* —— 那是糊的底图）
        static bool CopyLocalHero(string appid, string dst)
        {
            try
            {
                if (!Regex.IsMatch(appid == null ? "" : appid.Trim(), @"^\d{1,12}$")) return false;
                string root = SteamRoot();
                if (root.Length == 0) return false;
                string dir = Path.Combine(root, "appcache", "librarycache", appid.Trim());
                if (!Directory.Exists(dir)) return false;
                string[] hits = Directory.GetFiles(dir, "library_hero*.jpg");
                string pick = "";
                foreach (string p in hits)
                {
                    if (p.IndexOf("blur", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (p.IndexOf("schinese", StringComparison.OrdinalIgnoreCase) >= 0) { pick = p; break; }
                    if (pick.Length == 0) pick = p;
                }
                if (pick.Length == 0) return false;
                Directory.CreateDirectory(CoversDir);
                File.Copy(pick, dst, true);
                Program.Log("  横幅取自 Steam 本地缓存：" + appid + " ← " + pick);
                return true;
            }
            catch { return false; }
        }

        //  条目对应的 Steam appid：优先用扫描结果，其次查元数据。
        //  为什么需要兜底：从磁盘扫进来的 Steam 库游戏（platform 为空）DlssgGame.AppId 是空的，
        //  但 library.json 里往往存着正确的 appid —— 不查这一下，封面就永远取不到。
        //  安装目录里找官方主视觉：递归 ≤3 层，只收 60KB~8MB 的 png/jpg，
        //  跳过 cache / screenshot / locales 这类目录和 icon/unins 这类文件。
        //  取"文件最大"的几张再验尺寸 —— 启动横幅通常是目录里最大的那张图。
        static string LocalArt(DlssgGame g)
        {
            try
            {
                if (g == null || g.Dir == null || g.Dir.Length == 0 || !Directory.Exists(g.Dir)) return "";
                var cands = new List<KeyValuePair<long, string>>();
                CollectArt(g.Dir, 0, cands);
                if (cands.Count == 0) return "";
                cands.Sort(delegate(KeyValuePair<long, string> a, KeyValuePair<long, string> b) { return b.Key.CompareTo(a.Key); });
                int tried = 0;
                foreach (var kv in cands)
                {
                    if (++tried > 5) break;
                    try
                    {
                        using (Image im = Image.FromFile(kv.Value))
                        {
                            if (im.Width < 600 || im.Height < 400) continue;
                            double ar = (double)im.Width / im.Height;
                            if (ar < 0.5 || ar > 3.0) continue;
                            Directory.CreateDirectory(CoversDir);
                            string dst = Path.Combine(CoversDir, SafeName(g.Title) + ".jpg");
                            using (var bmp = new Bitmap(im)) bmp.Save(dst, ImageFormat.Jpeg);
                            ClearCache();
                            Program.Log("  封面取自安装目录：" + g.Title + " ← " + kv.Value);
                            return dst;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        static void CollectArt(string dir, int depth, List<KeyValuePair<long, string>> outList)
        {
            if (depth > 2 || outList.Count > 40) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string nm = Path.GetFileName(f).ToLowerInvariant();
                    if (!(nm.EndsWith(".png") || nm.EndsWith(".jpg") || nm.EndsWith(".jpeg"))) continue;
                    if (nm.IndexOf("icon") >= 0 || nm.IndexOf("unins") >= 0 || nm.IndexOf("thumb") >= 0) continue;
                    try
                    {
                        long len = new FileInfo(f).Length;
                        if (len < 60L * 1024 || len > 8L * 1024 * 1024) continue;
                        outList.Add(new KeyValuePair<long, string>(len, f));
                    }
                    catch { }
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string dn = Path.GetFileName(d).ToLowerInvariant();
                    if (dn.IndexOf("cache") >= 0 || dn.IndexOf("screenshot") >= 0 || dn == "locales" || dn == "locales") continue;
                    CollectArt(d, depth + 1, outList);
                    if (outList.Count > 40) return;
                }
            }
            catch { }
        }

        public static string AppIdOf(DlssgGame g)
        {
            if (g == null) return "";
            try
            {
                if (g.AppId != null && Regex.IsMatch(g.AppId.Trim(), @"^\d{1,12}$")) return g.AppId.Trim();
            }
            catch { }
            try
            {
                GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
                if (m != null && m.AppId != null && Regex.IsMatch(m.AppId.Trim(), @"^\d{1,12}$")) return m.AppId.Trim();
            }
            catch { }
            return "";
        }

        public static Image Card(DlssgGame g, int w, int h)
        {
            if (g == null) return null;
            string id = g.Id == null ? "" : g.Id;
            // 缓存键必须包含标题：Id 为空（未入平台库的自定义条目）时若只用尺寸做键，
            // 两个不同游戏会共用同一张图（2026-09-14 探针实测踩到）
            string key = id + "|" + SafeName(g.Title) + "|" + w + "x" + h;
            Image cached;
            //  ★ 命中缓存也要交**副本**（2026-10-26 第 149 轮）：原来这里直接 return cached，
            //  于是 Card() 的语义是"第一次给副本、之后给缓存本体"—— 调用方一律按"自己持有、自己释放"
            //  写（CoverTile / LibHero / LibActionBar 都在 Dispose 时释放 cover），
            //  第二次取同一个 key 就会把缓存本体销毁掉，之后任何一次绘制都是在已销毁的
            //  GDI+ 句柄上作图 → ArgumentException「参数无效」→ 控件被填白打叉。
            //  这与 0915 那次"16 张白框红叉"是同一个根因的残留分支：缓存自己持有，调用方只拿副本。
            lock (memLock) { cached = Hit(key); if (cached != null) return Own(cached); }

            Image img = null;
            try
            {
                Directory.CreateDirectory(CoversDir);
                var candList = new List<string>();
                foreach (string cName in GetTitleCandidates(g.Title))
                {
                    candList.Add(Path.Combine(CoversDir, cName + ".png"));
                    candList.Add(Path.Combine(CoversDir, cName + ".jpg"));
                    candList.Add(Path.Combine(CoversDir, cName + ".jpeg"));
                }
                if (id.Length > 0)
                {
                    candList.Add(CardFile(id));
                    candList.Add(CardFilePng(id));
                }
                string[] cand = candList.ToArray();
                //  ★ AppId 有效就再找一套 `steam_<appid>`（2026-10-22 "封面识别差劲"）：
                //  扫描器把一部分 Steam 库游戏标成了空 platform（如 wallpaper_engine / MyDockFinder），
                //  它们的 Id 是 local_<hash>，永远命不中 steam_<appid> 那套封面文件 ——
                //  于是明明有 appid、本地 Steam 缓存里也有图，卡片却只显示生成磁贴。
                string appid = AppIdOf(g);
                string sid = appid.Length > 0 ? "steam_" + appid : "";
                if (sid.Length > 0 && sid != id)
                    cand = Append(cand, CardFile(sid), CardFilePng(sid));
                //  ⚠ 生成磁贴**不在候选链里**（2026-10-22）：`local_<hash>_gen6.png` 一旦存在，
                //  就会抢在"安装目录官方主视觉"前面（完美世界就是被自己的旧磁贴顶掉的）。
                //  磁贴只在最后作为"复用上次结果"来用，见下面 img == null 的分支。
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
                //  ★ 再试一步：**安装目录自带的官方主视觉**（2026-10-22）。
                //  完美世界竞技平台这类客户端会把启动横幅放在 `pvp_static\xxx.png`（实测 1212×712），
                //  那是发行方自己的素材 —— 比任何"按名字搜图"都准，也不需要联网。
                if (img == null && g.Dir != null && g.Dir.Length > 0)
                {
                    string art = LocalArt(g);
                    if (art.Length > 0)
                    {
                        Image bi = LoadCoverScaled(art, w, h);
                        if (bi != null) img = RoundAlpha((Bitmap)bi, Theme.RPic);
                    }
                }

                //  已经生成过磁贴就复用（磁贴跨 DPI 存了 2 倍图，重算没必要）
                if (img == null)
                {
                    foreach (string gf in new string[] { id.Length > 0 ? GenFile(id) : "", sid.Length > 0 ? GenFile(sid) : "" })
                    {
                        if (gf.Length == 0 || !File.Exists(gf)) continue;
                        Image bi = LoadScaled(gf, w, h);
                        if (bi != null) { img = RoundAlpha((Bitmap)bi, Theme.RPic); break; }
                    }
                }

                //  ★ 兜底前先"趁手"从本地 Steam 客户端缓存复制一张（毫秒级、不联网）：
                //  有 appid 但因为 platform 识别不到而没有封面文件的条目，就靠这一步救回来 ——
                //  以前必须先手动点一次「获取封面」才会有图，用户看到的就是一堆生成磁贴。
                if (img == null && sid.Length > 0)
                {
                    string sp = CardFile(sid);
                    if (!File.Exists(sp) && CopyLocalSteam(appid, sid)) sp = CardFile(sid);
                    if (File.Exists(sp))
                    {
                        Image bi = LoadCoverScaled(sp, w, h);
                        if (bi != null) img = RoundAlpha((Bitmap)bi, Theme.RPic);
                    }
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
                    //  ★ 按 **2 倍**烘焙、并且**不圆角**存盘（2026-10-20）：
                    //    · 2 倍：磁盘缓存文件名只有 id，是跨 DPI 复用的 —— 按 1x 存进去，
                    //      150% 缩放下再读出来就是"放大"，渐变色带和首字母一起糊。
                    //    · 不圆角：读回来时那条路本来就会 RoundAlpha，存盘再圆一次等于圆两遍。
                    int gw = w * 2, gh = h * 2;
                    Bitmap big = GradientCard(g.Title, gw, gh, icon);
                    if (icon != null) { icon.Dispose(); icon = null; }
                    if (big != null && id.Length > 0)
                    {
                        try { big.Save(GenFile(id), ImageFormat.Png); } catch { }
                    }
                    img = big == null ? null : RoundAlpha(Fit(big, w, h), Theme.RPic);
                    if (big != null) big.Dispose();
                }
            }
            catch { }
            // 加进缓存后当场在锁内复制出调用方的独占副本：Own 若放到锁外，
            // 中间可能被并发 ClearCache 把源图 Dispose 掉（0915 白框事故同款）
            if (img != null) { Remember(key, img); return Own(img); }
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
        // ---- Steam 本地库缓存（2026-10-18）----
        //  Steam 客户端本身就把官方竖版封面放在 appcache\librarycache 里，直接复制一份
        //  是毫秒级、离线可用，比走 CDN 快一个数量级，也不吃用户的代理设置。
        public static string SteamRoot()
        {
            try
            {
                string p = SteamLib.RegS(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                if (p.Length == 0) p = @"C:\Program Files (x86)\Steam";
                p = Lib.BackSlashes(p);
                return Directory.Exists(p) ? p : "";
            }
            catch { return ""; }
        }

        public static bool CopyLocalSteam(string appid, string id)
        {
            if (!Regex.IsMatch(appid == null ? "" : appid.Trim(), @"^\d{1,12}$")) return false;
            if (id == null || id.Length == 0) return false;
            string root = SteamRoot();
            if (root.Length == 0) return false;
            string cache = Path.Combine(root, "appcache", "librarycache");
            if (!Directory.Exists(cache)) return false;
            string dst = CardFile(id);
            //  已有就别覆盖：那可能是用户手动换的、或之前下过的高清版
            //  （返回 false = "这次没有从本地补图"，调用方据此计数）
            if (File.Exists(dst) && new FileInfo(dst).Length > 0) return false;
            //  ⚠ 本地缓存的文件名带**语言后缀**（实测：「Steam\appcache\librarycache\730」里是
            //    library_600x900_schinese.jpg / header_schinese.jpg），所以不能拿 CDN 那套精确文件名去拼，
            //    必须用通配符枚举。顺序 = 竖版优先（600x900），最后才是横版与 logo。
            //  ⚠ 不要把 logo*.png 算进来：Steam 的 logo 是**透明背景的小标识**，
            //    贴上卡片几乎什么都看不见（实测 Blender 变成一块灰白）。宁可继续用生成卡。
            string[] pats = new string[] {
                "library_600x900*.jpg", "library_600x900*.png",
                "header*.jpg", "library_hero*.jpg", "capsule_616x353*.jpg" };
            string dir = Path.Combine(cache, appid);
            foreach (string pat in pats)
            {
                string hit = First(Directory.Exists(dir) ? Directory.GetFiles(dir, pat) : null);
                if (hit.Length > 0 && TryCopy(hit, dst, id)) return true;
            }
            foreach (string pat in pats)
            {
                string hit = First(Directory.GetFiles(cache, appid + "_" + pat));
                if (hit.Length > 0 && TryCopy(hit, dst, id)) return true;
            }
            return false;
        }

        // ---- 自定义封面来源（2026-10-18）----
        //  非 Steam 游戏的封面没有稳定公开图源：官方图在游戏包/DLL 里（鸣潮、米哈游启动器都如此），
        //  商店图要平台 API。所以这里给一张**用户可编辑**的表（DataDir\covers.json）：
        //      { "鸣潮": "https://xxx/cover.jpg", "绝区零": "D:\\pics\\zzz.png" }
        //  键 = 游戏名关键词（包含匹配，大小写不敏感），值 = 网络图片 URL 或本地图片路径。
        //  命中后优先于「Steam 本地缓存 / CDN / 按名字搜图」，填一次永久生效、离线也能用。
        public static string CustomFile { get { return Path.Combine(Program.DataDir, "covers.json"); } }

        //  返回值：true = 已用上自定义图；false + err 非空 = 命中但失败；false + err 空 = 没配。
        public static bool ApplyCustom(DlssgGame g, out string err)
        {
            err = "";
            if (g == null || g.Title == null || g.Title.Length == 0) return false;
            if (!File.Exists(CustomFile)) return false;
            try
            {
                var map = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(CustomFile, Encoding.UTF8));
                if (map == null) return false;
                string hit = "";
                foreach (KeyValuePair<string, string> kv in map)
                {
                    if (kv.Key == null || kv.Key.Length == 0 || kv.Key[0] == '_') continue;   // 下划线开头 = 说明行
                    if (kv.Value == null || kv.Value.Trim().Length == 0) continue;
                    if (g.Title.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) { hit = kv.Value.Trim(); break; }
                }
                if (hit.Length == 0) return false;
                //  已经用上了就不再重复下载/复制：图比 covers.json 新 = 上次已经应用过
                string card = Path.Combine(CoversDir, SafeName(g.Title) + ".jpg");
                if (File.Exists(card) && File.GetLastWriteTimeUtc(card) > File.GetLastWriteTimeUtc(CustomFile))
                    return true;

                byte[] data;
                if (hit.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || hit.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    //  用户明确指定的地址：不走域名白名单（那条闸是给自动搜图用的）
                    data = Fetch(hit, 20 << 20, 1000, false);
                    if (data == null) data = Fetch(hit, 20 << 20, 1000, true);
                    if (data == null) { err = "下载失败：" + hit; return false; }
                }
                else
                {
                    if (!File.Exists(hit)) { err = "本地图片不存在：" + hit; return false; }
                    data = File.ReadAllBytes(hit);
                }
                err = SaveCoverBytes(g, data);
                if (err.Length > 0) return false;
                Program.Log("  封面取自 covers.json：" + g.Title);
                return true;
            }
            catch (Exception ex)
            {
                err = "covers.json 读取失败：" + ex.Message;
                return false;
            }
        }

        //  同一游戏常有多语言封面：中文优先（用户是中文环境），否则取文件名最短的那张（= 无语言后缀）
        static string First(string[] hits)
        {
            if (hits == null || hits.Length == 0) return "";
            foreach (string h in hits)
                if (h.IndexOf("schinese", StringComparison.OrdinalIgnoreCase) >= 0) return h;
            string best = hits[0];
            foreach (string h in hits)
                if (Path.GetFileName(h).Length < Path.GetFileName(best).Length) best = h;
            return best;
        }

        static bool TryCopy(string src, string dst, string id)
        {
            try
            {
                using (Image probe = Image.FromFile(src)) { }     // 先验证是合法图片（缓存里可能有半截文件）
                Directory.CreateDirectory(CoversDir);
                File.Copy(src, dst, true);
                Program.Log("  封面取自 Steam 本地缓存：" + id + " ← " + src);
                return true;
            }
            catch { return false; }
        }

        //  批量补齐：只碰本地文件，零网络。扫描合并后直接调，用户不用再点「获取封面」。
        public static int FillLocalCovers(IEnumerable<DlssgGame> list)
        {
            int n = 0;
            foreach (DlssgGame g in list)
            {
                if (g == null) continue;
                if (g.Id == null || g.Id.Length == 0) g.Id = Lib.IdOf(g);
                //  ① covers.json 里用户自己配的（对所有平台生效，且**覆盖**旧图 —— 填了就是想换）
                string cerr;
                if (ApplyCustom(g, out cerr)) { n++; continue; }
                if (cerr.Length > 0) Program.Log("  covers.json 未生效（" + g.Title + "）：" + cerr);
                //  ② Steam 本地缓存（只对 Steam 条目）
                if (HasRealCover(g)) continue;
                if (g.Platform != "steam") continue;
                string appid = g.AppId == null ? "" : g.AppId.Trim();
                if (appid.Length == 0) continue;
                if (CopyLocalSteam(appid, g.Id)) n++;
            }
            return n;
        }

        //  ★ 扫描后自动补齐 Steam 官方封面（2026-10-23，P3）。
        //  为什么值得自动做：缺封面时落的是"渐变磁贴 + exe 图标"，一面墙里混着两种画风 ——
        //  官方 2:3 库封面的比例与其它卡片一致，补齐之后墙面才是同一套语言。
        //  边界（刻意收窄）：
        //    · 只走 Steam 官方域名（DownloadSteam 拼的是 cdn.cloudflare.steamstatic.com / steampowered），
        //      **不做"按名字搜图"** —— 那条路会把 5EClient 配成"春天就要上春山"（2026-10-22 教训）；
        //    · 只补**有 appid 且没有真封面**的条目，最多 max 张，失败静默；
        //    · 开关是设置页的「联网获取封面」，关掉就只读本地缓存。
        public static int FillSteamCovers(IEnumerable<DlssgGame> list, bool online, int max)
        {
            int got = 0;
            if (list == null) return 0;
            foreach (DlssgGame g in list)
            {
                if (g == null) continue;
                if (got >= max) break;
                if (g.Id == null || g.Id.Length == 0) g.Id = Lib.IdOf(g);
                if (HasRealCover(g)) continue;
                string appid = g.AppId == null ? "" : g.AppId.Trim();
                if (appid.Length == 0) continue;
                if (!online) continue;
                bool okOne = false;
                try { okOne = DownloadSteam(appid, g.Id); } catch { }
                if (okOne) got++;
            }
            return got;
        }

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
                    g.LaunchEnv = m.LaunchEnv;
                    g.FgMax = m.FgMax; g.FgPreset = m.FgPreset ?? ""; g.FgOpt = m.FgOpt;
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
            //  ★ 第 149 轮：深找先用**条目里记的 exe 名**当提示，再退回打分。
            //  原来直接 `FindExe(dir, null, title)` —— 完全不看 exe 名，只按"名字含标题 / UE Shipping /
            //  体积最大"打分。于是条目里写 `nope.exe` 时，它会挑一个**别的** exe 启动：
            //  用户点的 A 程序、跑起来是 B，而且不报错。同名优先之后，"exe 改名/挪位"仍能救回来
            //  （同名那条路会命中），只是不再替用户瞎选。
            string nm = g.Exe == null ? "" : g.Exe.Trim();
            if (nm.IndexOf('\\') >= 0 || nm.IndexOf('/') >= 0) nm = "";   // 带路径的不能当文件名用
            string stem = nm.Length > 0 ? Path.GetFileNameWithoutExtension(nm) : "";
            if (stem.Length > 0)
            {
                string byName = PlatformScan.FindExe(g.Dir, stem, g.Title);
                if (byName.Length > 0) return byName;
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

        // 环境变量文本 → 键值对（一行一条 `KEY=VALUE`；空行和 # 注释行跳过）。
        //  只按**第一个** '=' 切 —— 值里可能还有 '='（token / base64 / 命令行片段）。
        //  非法行（没有 '=' 或键为空）静默跳过：校验在编辑对话框里做，启动路径不该再弹框。
        public static List<KeyValuePair<string, string>> ParseEnv(string text)
        {
            var r = new List<KeyValuePair<string, string>>();
            if (text == null || text.Length == 0) return r;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k.Length == 0) continue;
                r.Add(new KeyValuePair<string, string>(k, v));
            }
            return r;
        }

        // 写入「启动参数 + 环境变量」（一次落盘，两个字段同源，避免半更新）
        public static void SetLaunchConfig(DlssgGame g, string args, string env)
        {
            if (g == null) return;
            args = args == null ? "" : args.Trim();
            env = env == null ? "" : env.Trim().Replace("\r\n", "\n");
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            m.LaunchArgs = args;
            m.LaunchEnv = env;
            m.Platform = g.Platform;
            if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
            MetaStore.Save(m);
            g.LaunchArgs = args;
            g.LaunchEnv = env;
        }

        // 写入「每游戏帧生成参数」（第 160 轮）：一次落盘 + 同步到运行时对象。
        //  覆盖语义见 DlssgGame 的字段注释（0/""/-1 = 跟随全局）；应用（写 INI）由调用方做 ——
        //  这里只管元数据，因为"装没装、装的是哪一代"只有 UI 层知道。
        public static void SetFgConfig(DlssgGame g, int max, string preset, int opt)
        {
            if (g == null) return;
            if (max < 0) max = 0;
            if (max > 5) max = 5;
            if (opt < -1) opt = -1;
            if (opt > 3) opt = 3;
            preset = preset == null ? "" : preset.Trim();
            GameMeta m = MetaStore.GetOrCreate(MetaStore.KeyOf(g));
            m.FgMax = max;
            m.FgPreset = preset;
            m.FgOpt = opt;
            m.Platform = g.Platform;
            if (g.AppId != null && g.AppId.Length > 0) m.AppId = g.AppId;
            MetaStore.Save(m);
            g.FgMax = max;
            g.FgPreset = preset;
            g.FgOpt = opt;
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
                        List<KeyValuePair<string, string>> env = ParseEnv(g.LaunchEnv);
                        if (env.Count == 0)
                        {
                            // 没有环境变量：沿用原来的启动方式。
                            // 刻意不统一改成 ProcessStartInfo —— UseShellExecute=false 会改变
                            // 工作目录与 shell 行为，这条路径跑了很久没出问题，不为"整齐"去动它。
                            if (args.Length > 0) Process.Start(exe, args);
                            else Process.Start(exe);
                        }
                        else
                        {
                            // 有环境变量就只能走 ProcessStartInfo（Process.Start(文件, 参数) 没有环境变量入口）。
                            // ⚠ 两个必须显式设的：
                            //   ① UseShellExecute=false —— 为 true 时 EnvironmentVariables 被静默忽略；
                            //   ② WorkingDirectory=exe 所在目录 —— 缺省是 Fluxion 自己的目录，
                            //      游戏按相对路径找资源会失败，症状是"闪退/黑屏"，极难查。
                            var psi = new ProcessStartInfo(exe);
                            if (args.Length > 0) psi.Arguments = args;
                            psi.UseShellExecute = false;
                            psi.WorkingDirectory = Path.GetDirectoryName(exe);
                            foreach (KeyValuePair<string, string> kv in env)
                            {
                                try { psi.EnvironmentVariables[kv.Key] = kv.Value; } catch { }
                            }
                            try
                            {
                                Program.Log("  启动环境变量 " + env.Count + " 条："
                                    + string.Join(", ", Array.ConvertAll(env.ToArray(),
                                        delegate(KeyValuePair<string, string> kv) { return kv.Key; })));
                            }
                            catch { }
                            Process.Start(psi);
                            g.LastLaunchedWithEnv = env.Count;      // 供诊断/探针判断"这次真带上了"
                        }
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
