// ==========================================================================
//  Dlssg040.cs —— 2026-10-18 新增，三件事：
//   ① Dlssg040 ：0.4.x 新版结构（代理缩到 0.8 MB，运行库/后端/内核包搬到 dlssg_sm86\）
//   ② DlssgAsi ：0.3.x 的 ASI 变体（Version.asi + wininet.dll）—— 常规入口名被游戏屏蔽时用
//   ③ Upstream ：GitHub 下载源 —— 作者发新版时不必再手工下载、解压、拖给工具
// ==========================================================================
//  为什么 0.4.x 必须单独一条路径，而不是"0.3.x 换个资源包"：
//    代理体积从 30,021,920 B 掉到 853,280 B（0.81 MB）——运行库不再内嵌，改到 dlssg_sm86\
//    下按 backend / kernels / runtime / sr / xefg 分目录放。这意味着：
//      · 所有"按体积认代理"的旧判据全部失效（0.4.0 的代理比系统自带的 DLL 大不了多少），
//        认它只能靠**签名 + 配套目录**（见 Dlssg040.IsMine）；
//      · 只复制 DLL 等于什么都没做（作者 README 原话），部署必须带上整个 dlssg_sm86\；
//      · INI 布局换过（Optimized / Preset 搬到 [DLSSG]，新增 [XeSSFG]，[Runtime] Mode=Auto 删除），
//        旧版的键会被逐条告警 —— 所以这里不再复用 Dlssg030 的 INI 补齐逻辑。
//    版本号不再靠体积猜：包里带了 manifest.json，bundle.version 直接可读（2026-10-18 实测：
//    0.4.0 / runtime 310.9.16.0 / version.dll 853,280 B / INI 6,810 B）。
//
//  语法限制：本项目用 .NET Framework 的 csc.exe 编译，一律 C# 4 写法（无 $""、无 nameof、无 ?.）。
// ==========================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Fluxion
{
    // ==========================================================================
    //  ① DLSSG for SM86 · 0.4.x
    // ==========================================================================
    public static class Dlssg040
    {
        public const string DefVer = "0.4.1";
        public const string ResDirName = "dlssg_sm86";        // 游戏目录与资源包里同名的那个资源目录
        public const string IniName = "dlssg_sm86.ini";

        // 入口名单：0.4.0 根目录只发布 version.dll，其余五个在 alternatives\（与 0.3.x 同一套六个名字）
        static readonly string[] _entries = new string[] {
            "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" };
        public static string[] Entries { get { return _entries; } }

        // 资源目录里"缺一件就完全不生效"的三件 —— 作者在 README 已知问题里给过对应的日志名：
        //   后端缺失 → backend_install -100；内核包缺失 → kernel_pack_missing；运行库缺失 → 直接回退原厂路径。
        static readonly string[] _mustRes = new string[] {
            "backend\\sm86_backend.dll", "kernels\\fg_kernels.dll", "runtime\\nvngx_dlssg.dll" };
        // 用到才需要的可选件：xefg 只在 [FrameGeneration] Engine=XeSS 时读，sr 只在
        //  [SuperResolution] Mode=On 时读 —— 这两件合计 186 MB，出厂配置（Mode=Off）根本用不上。
        public static readonly string[] OptRes = new string[] {
            "xefg\\xefg_kernels.dll", "sr\\nvngx_dlss.dll", "sr\\sr_kernels.dll" };

        public static string PackRoot { get { return Path.Combine(Program.DataDir, "dlssg040-pack"); } }
        public static string AltDir { get { return Path.Combine(PackRoot, "alternatives"); } }
        public static string ResDir { get { return Path.Combine(PackRoot, ResDirName); } }

        // ---- 版本：读包里的 manifest.json，不再靠体积猜 ----

        static Dictionary<string, object> _bundle;
        static long _bundleTick;

        static Dictionary<string, object> Bundle()
        {
            string p = Path.Combine(PackRoot, "manifest.json");
            try
            {
                if (_bundle != null && File.Exists(p) && File.GetLastWriteTimeUtc(p).Ticks == _bundleTick)
                    return _bundle;
                if (!File.Exists(p)) { _bundle = null; return null; }
                object o = new JavaScriptSerializer().DeserializeObject(
                    File.ReadAllText(p, Encoding.UTF8));
                Dictionary<string, object> top = o as Dictionary<string, object>;
                object raw = null;
                Dictionary<string, object> b = (top != null && top.TryGetValue("bundle", out raw))
                                             ? raw as Dictionary<string, object> : null;
                _bundleTick = File.GetLastWriteTimeUtc(p).Ticks;
                _bundle = b;
                return b;
            }
            catch { return null; }
        }

        static string BundleStr(string key, string fallback)
        {
            Dictionary<string, object> b = Bundle();
            object v;
            if (b != null && b.TryGetValue(key, out v) && v != null)
            {
                string s = v.ToString();
                if (s.Length > 0) return s;
            }
            return fallback;
        }

        public static string Ver { get { return BundleStr("version", DefVer); } }
        public static string RuntimeVer { get { return BundleStr("runtime_version", ""); } }

        // ---- 判据：这个 DLL 是不是 0.4.x 的代理 ----
        //  0.4.0 六个入口实测 853,280 / 853,792 / 864,544 / 871,200 字节（各自的导出表不同），
        //  全部带作者的自签证书 85BA66762F851E49148D706915D09026281418E6。
        //  ⚠ 体积只能用来"决定要不要去取签名"，绝不能单独判 —— 这个尺寸区间里躺着的更多是别家文件。
        public static bool MaybeSize(long n) { return n >= 200L * 1024 && n < 14L * 1024 * 1024; }
        public static bool IsProxySize(long n) { return n >= 200L * 1024 && n <= 8L * 1024 * 1024; }

        // 作者签名 + 体积落在 0.4.x 窗口 → 本方案的代理（与文件名无关，Version.asi 走的是 0.3.x 窗口）
        public static bool IsMine(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                long n = new FileInfo(path).Length;
                if (!IsProxySize(n)) return false;
                return ProxySign.Of(path).Verdict == "upstream";
            }
            catch { return false; }
        }

        public static string EntrySrc(string entry)
        {
            if (string.Equals(entry, "version.dll", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(PackRoot, "version.dll");
            return Path.Combine(AltDir, entry);
        }

        // 部署时用哪个入口名。0.4.x 不像 0.3.x 那样按游戏类型挑槽位
        //  （作者不再给"绝区零用 d3d12、鸣潮用 dxgi"这种表），统一由这一处决定。
        public static string EntryPref()
        {
            string e = Program.Cfg != null ? Program.Cfg.DlssgEntry040 : null;
            if (e != null && Array.IndexOf(_entries, e) >= 0) return e;
            return "version.dll";
        }

        // 资源包能不能用（返回缺失清单）
        public static bool Ready(out string miss)
        {
            List<string> lack = new List<string>();
            if (!Directory.Exists(PackRoot)) { miss = "资源包目录不存在（" + PackRoot + "）"; return false; }
            if (!File.Exists(Path.Combine(PackRoot, "version.dll"))) lack.Add("version.dll");
            if (!File.Exists(Path.Combine(PackRoot, IniName))) lack.Add(IniName);
            foreach (string rel in _mustRes)
                if (!File.Exists(Path.Combine(ResDir, rel))) lack.Add(ResDirName + "\\" + rel);
            miss = string.Join("、", lack.ToArray());
            return lack.Count == 0;
        }

        // 这个游戏目录里 0.4.x 是否已在位、占的是哪个入口
        public static string InstalledEntry(string gameDir)
        {
            if (gameDir == null) return "";
            foreach (string e in _entries)
                if (IsMine(Path.Combine(gameDir, e))) return e;
            return "";
        }

        public static bool Installed(string gameDir)
        {
            string e = InstalledEntry(gameDir);
            if (e.Length == 0) return false;
            try { return File.Exists(Path.Combine(gameDir, IniName)) && File.Exists(KernelPath(gameDir)); }
            catch { return false; }
        }

        public static string KernelPath(string gameDir)
        {
            return Path.Combine(Path.Combine(gameDir, ResDirName), "kernels\\fg_kernels.dll");
        }

        // 部署目录里还缺什么（空串 = 齐）
        public static string DeployedLack(string gameDir)
        {
            List<string> lack = new List<string>();
            if (!File.Exists(Path.Combine(gameDir, IniName))) lack.Add(IniName);
            foreach (string rel in _mustRes)
            {
                int cut = rel.LastIndexOf('\\');
                string dir = Path.Combine(gameDir, ResDirName);
                if (cut > 0) dir = Path.Combine(dir, rel.Substring(0, cut));
                if (!File.Exists(Path.Combine(dir, rel.Substring(cut + 1)))) lack.Add(rel);
            }
            return string.Join("、", lack.ToArray());
        }

        // ---- 部署 ----

        public static string Install(string gameDir, string entry, bool force)
        {
            if (gameDir == null || !Directory.Exists(gameDir)) return "目录不存在：" + gameDir;
            if (entry == null || entry.Length == 0) entry = "version.dll";
            string miss;
            if (!Ready(out miss))
                return "0.4.x 资源包不完整（缺 " + miss + "）—— 先点「从 GitHub 取新版」，或把作者的发布包拖进窗口";
            string srcDll = EntrySrc(entry);
            if (!File.Exists(srcDll)) return "资源包里没有这个入口：" + srcDll;
            try
            {
                string dstDll = Path.Combine(gameDir, entry);
                // 覆盖前备份：游戏自带的同名 DLL 也在这个名字上（换入口名时最常见）
                int backed = BackupOne(dstDll);
                File.Copy(srcDll, dstDll, true);

                string iniNote = EnsureIni(gameDir, force);
                int copied = CopyRes(gameDir, force);
                string lack = DeployedLack(gameDir);

                //  换入口时把同方案的**其他入口**挪开（改名停放，与巡检体系同后缀）。
                //  不挪的后果：绝区零这类反作弊按文件名拦，旧 version.dll 留在目录里照样被拦
                //  （Error Code 11008，2026-11-01 实测）；多套同方案入口并存也会互抢渲染管线。
                //  IsMine 先确认是自家文件 —— 绝不碰游戏自带/别家的同名 DLL。
                string moved = "";
                foreach (string e in _entries)
                {
                    if (string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)) continue;
                    string p = Path.Combine(gameDir, e);
                    if (!File.Exists(p) || !IsMine(p)) continue;
                    try { File.Move(p, p + ".parked.040"); moved += (moved.Length > 0 ? "、" : "") + e; }
                    catch (Exception ex) { return "部署中止：旧入口 " + e + " 挪不动（" + ex.Message + "）—— 游戏还在运行？"; }
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format("已部署 {0}：入口 {1} + {2} + {3}\\（复制 {4} 个资源文件{5}）",
                    Ver, entry, IniName, ResDirName, copied,
                    backed > 0 ? "，覆盖前备份 " + backed + " 个同名原文件" : ""));
                if (moved.Length > 0) sb.AppendLine("  已停用旧入口：" + moved + "（反作弊会按文件名拦常规名；要换回去在入口下拉里重选再部署）");
                if (iniNote.Length > 0) sb.AppendLine("  " + iniNote);
                if (lack.Length > 0) sb.AppendLine("  ⚠ 部署后仍缺：" + lack);
                else sb.AppendLine("  三件必需资源齐全（后端 / 内核包 / 运行库）");
                sb.AppendLine("  出厂倍率上限 MaxGeneratedFrames=" + ReadMaxFrames(gameDir) + "（3 = 4X；要 6X 改成 5）");
                sb.AppendLine("  " + ApplyTuning(gameDir));
                //  驱动门槛（第 160 轮，对齐上游 0.4.1 的 release note：hard minimum R580）。
                //  只警告不拦：0.4.0 的包在旧驱动上仍可用，拦了反而把"继续用 0.4.0"的路堵死。
                string drv = Program.DriverVerNow();
                if (drv.Length > 0)
                {
                    double dv;
                    if (double.TryParse(drv, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out dv) && dv > 0 && dv < 580)
                        sb.AppendLine("  ⚠ 当前驱动 " + drv + "：0.4.1 起要求驱动 ≥ R580 —— 旧驱动请继续用 0.4.0 的包，或先升驱动");
                }
                sb.Append("  验证：完全退出游戏重启 → 画质菜单出现「DLSS 帧生成」；日志 <游戏目录>\\dlssg_sm86\\logs\\loader_*.jsonl");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "部署失败：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        // 彻底清掉（与"停放"不同：停放只改名，这里真删 —— 因为 dlssg_sm86\ 躺着 30~210 MB）
        public static string Purge(string gameDir)
        {
            if (gameDir == null || !Directory.Exists(gameDir)) return "目录不存在";
            int n = 0;
            List<string> failed = new List<string>();
            foreach (string e in _entries)
            {
                string p = Path.Combine(gameDir, e);
                try { if (IsMine(p)) { File.Delete(p); n++; } }
                catch (Exception ex) { failed.Add(e + "：" + ex.Message); }
            }
            try
            {
                string ini = Path.Combine(gameDir, IniName);
                if (File.Exists(ini)) { File.Delete(ini); n++; }
            }
            catch (Exception ex) { failed.Add(IniName + "：" + ex.Message); }
            try
            {
                string res = Path.Combine(gameDir, ResDirName);
                // 只删属于 0.4.x 的那份（0.3.x 的 dlssg_sm86 只有 logs，且通常已被停放改名）
                if (Directory.Exists(res) && File.Exists(KernelPath(gameDir)))
                {
                    Directory.Delete(res, true);
                    n++;
                }
            }
            catch (Exception ex) { failed.Add(ResDirName + "\\：" + ex.Message); }
            string r = "已清理 " + n + " 项（入口 + " + IniName + " + " + ResDirName + "\\），游戏回到原生状态";
            if (failed.Count > 0) r += "\n  ⚠ 其中 " + failed.Count + " 项没删掉：" + string.Join("；", failed.ToArray())
                                    + " —— 多半是游戏还在运行，完全退出后重试";
            return r;
        }

        static int BackupOne(string dst)
        {
            try
            {
                if (!File.Exists(dst)) return 0;
                if (IsMine(dst)) return 0;                  // 已经是我们自己的：不留第二份
                File.Copy(dst, dst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                return 1;
            }
            catch { return 0; }
        }

        // 出厂 INI：只在"没有 / 缺 0.4.x 才有的键 / force"时覆盖。
        //  不能照搬 0.3.x 的 EnsureFactoryIni：那个函数拿 0.3.x 那份出厂 INI 去比，
        //  会把 0.4.x 的新段（[DLSSG] / [XeSSFG]）当成"旧代"反复重写。
        public static string EnsureIni(string gameDir, bool force)
        {
            string src = Path.Combine(PackRoot, IniName);
            string dst = Path.Combine(gameDir, IniName);
            try
            {
                if (!File.Exists(src)) return "";
                if (!File.Exists(dst))
                {
                    File.Copy(src, dst, true);
                    return "已放入出厂 " + IniName + "（" + new FileInfo(src).Length + " B）";
                }
                string cur = File.ReadAllText(dst);
                // 0.4.x 的标志：Optimized / Preset 搬到 [DLSSG]、多出 [XeSSFG]
                bool is040 = cur.IndexOf("[DLSSG]", StringComparison.OrdinalIgnoreCase) >= 0
                          || cur.IndexOf("[XeSSFG]", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!is040 || force)
                {
                    File.Copy(dst, dst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    File.Copy(src, dst, true);
                    return is040 ? "已按 force 重抄出厂 INI"
                                 : "旧代 INI 已换成 0.4.x 出厂版（原文件留了 .bak-时间戳）";
                }
                return "";
            }
            catch (Exception ex) { return "INI 补齐失败：" + ex.Message; }
        }

        // 把资源目录拷进游戏目录。force=false 时只补**缺失**的件（切换/快速路径不重抄上百 MB）
        // 这次部署要带上的资源件（相对 dlssg_sm86\）：必需三件 + 可选的 sr 两件。
        //  清单只此一份 —— CopyRes 与"部署前签名核验"都用同一份，
        //  否则"提示里说会复制 5 个、实际复制 3 个"这种事迟早发生。
        internal static string[] PlannedRes()
        {
            List<string> all = new List<string>();
            all.AddRange(_mustRes);
            foreach (string rel in OptRes)
            {
                // sr 两件套默认不带（186 MB，出厂用不上）；要在设置里打开才带
                bool sr = rel.StartsWith("sr\\", StringComparison.Ordinal);
                if (sr && !WithSr) continue;
                all.Add(rel);
            }
            return all.ToArray();
        }

        // 这次部署会往游戏目录写的全部源文件（入口 + ini + 资源件）。
        public static List<string> PlannedSources(string gameDir, string entry)
        {
            List<string> r = new List<string>();
            string e = EntrySrc(entry);
            if (File.Exists(e)) r.Add(e);
            string ini = Path.Combine(PackRoot, IniName);
            if (File.Exists(ini)) r.Add(ini);
            foreach (string rel in PlannedRes())
            {
                string s = Path.Combine(ResDir, rel);
                if (File.Exists(s)) r.Add(s);
            }
            return r;
        }

        // 把资源目录拷进游戏目录。force=false 时只补**缺失**的件（切换/快速路径不重抄上百 MB）
        static int CopyRes(string gameDir, bool force)
        {
            int n = 0;
            List<string> all = new List<string>();
            all.AddRange(PlannedRes());
            foreach (string rel in all)
            {
                string s = Path.Combine(ResDir, rel);
                if (!File.Exists(s)) continue;                  // 资源包里本就没有：不报错
                string d = Path.Combine(Path.Combine(gameDir, ResDirName), rel);
                if (File.Exists(d) && !force) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(d));
                    File.Copy(s, d, true);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // 超分组件带不带（sv\ 106 MB + 74 MB，只有 [SuperResolution] Mode=On 用得上）
        static bool WithSr
        {
            get { return Program.Cfg == null ? false : Program.Cfg.DlssgSr; }
        }

        // ---- INI 读写 ----

        public static int ReadMaxFrames(string gameDir)
        {
            try
            {
                string p = Path.Combine(gameDir, IniName);
                if (!File.Exists(p)) return -1;
                foreach (string ln in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string t = ln.Trim();
                    if (!t.StartsWith("MaxGeneratedFrames", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    int v;
                    if (int.TryParse(t.Substring(eq + 1).Trim(), out v)) return v;
                }
            }
            catch { }
            return -1;
        }

        // 只改一个键，其余原样 —— 0.4.x 的 INI 是完整发布配置，整份重写会丢新段的键
        public static string SetIni(string gameDir, string section, string key, string val)
        {
            string p = Path.Combine(gameDir, IniName);
            if (!File.Exists(p)) return "找不到 " + IniName + "（0.4.x 未部署？）";
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

        // 1..5 → 2X..6X（与 0.3.x 同一套值：5 = 6X）
        public static string SetMaxFrames(string gameDir, int frames)
        {
            if (frames < 1) frames = 1;
            if (frames > 5) frames = 5;
            return SetIni(gameDir, "FrameGeneration", "MaxGeneratedFrames", frames.ToString());
        }

        // ---- 参数：0.4.x 的四个常用键 ----
        //  出厂 [DLSSG] Optimized=2 / Preset=Auto、[FrameGeneration] Engine=DLSS、
        //  [SuperResolution] Mode=Off。这四项决定"快多少 / 画质差多少"，
        //  不该逼用户去手改 6.8 KB 的 INI —— 但都只改那一行，其余键原样保留。
        public static string ApplyTuning(string gameDir)
        {
            return ApplyTuning(gameDir,
                Program.Cfg != null ? Program.Cfg.DlssgOptimized040 : 2,
                Program.Cfg != null ? Program.Cfg.DlssgPreset040 : "Auto");
        }

        //  显式版（第 160 轮）：每游戏参数预设用 —— 调用方解析好"本游戏生效值"再传进来
        //  （每游戏覆盖了就传覆盖值，没覆盖就传全局值）；Engine / SuperResolution 仍是全局量。
        public static string ApplyTuning(string gameDir, int opt, string preset)
        {
            string p = Path.Combine(gameDir, IniName);
            if (!File.Exists(p)) return "找不到 " + IniName + "（0.4.x 未部署？）";
            if (opt < 0) opt = 0;
            if (opt > 3) opt = 3;
            if (preset == null || preset.Length == 0) preset = "Auto";
            string engine = Program.Cfg != null ? Program.Cfg.DlssgEngine040 : "DLSS";
            if (engine == null || engine.Length == 0) engine = "DLSS";
            bool sr = Program.Cfg != null && Program.Cfg.DlssgSr040;

            SetIni(gameDir, "DLSSG", "Optimized", opt.ToString());
            SetIni(gameDir, "DLSSG", "Preset", preset);
            SetIni(gameDir, "FrameGeneration", "Engine", engine);
            SetIni(gameDir, "SuperResolution", "Mode", sr ? "On" : "Off");
            return "参数：Optimized=" + opt + " · Preset=" + preset + " · Engine=" + engine
                 + " · SuperResolution=" + (sr ? "On（需要 sr\\ 组件）" : "Off");
        }

        // ---- 生效检查：读游戏目录里的日志 ----
        //  只读：不改任何文件。作者 README 给的判据是
        //   `<游戏目录>\dlssg_sm86\logs\loader_<PID>.jsonl` 出现 = 代理被加载；
        //   backend_<PID>.jsonl 里 route active=true = 后端装上路由。
        //  这里不做严格 JSON 解析（字段随版本变），只统计文件名与几个关键串 ——
        //  宁可少说结论，也不给一个看起来精确但会误报的判定。
        public class LogCheck
        {
            public bool HasDir;
            public int LoaderFiles, BackendFiles;
            public string Newest = "";
            public int InstallHits, KernelMissing, ErrorLines, GateExit;
            public long Bytes;

            public bool Loaded { get { return LoaderFiles > 0 || BackendFiles > 0; } }

            public string Line()
            {
                if (!HasDir) return "还没有 dlssg_sm86\\logs —— 代理没被游戏加载过（刚装的话先进一次游戏再回来看）";
                if (!Loaded) return "日志目录是空的 —— 代理没被加载（换个入口名再试）";
                StringBuilder sb = new StringBuilder();
                sb.Append("日志 ").Append(LoaderFiles + BackendFiles).Append(" 个");
                if (Newest.Length > 0) sb.Append("（最新 ").Append(Newest).Append("）");
                if (LoaderFiles > 0) sb.Append("；loader 在（代理已被加载）");
                if (BackendFiles > 0) sb.Append("；backend 在（后端已启动）");
                if (InstallHits > 0) sb.Append("；install 事件 ").Append(InstallHits).Append(" 条");
                if (GateExit > 0) sb.Append("；fg_gate_exit 在（上次是正常退出）");
                if (KernelMissing > 0)
                    sb.Append("；⚠ kernel_pack_missing ").Append(KernelMissing)
                      .Append(" 条 → 内核包没进游戏（检查 dlssg_sm86\\kernels\\fg_kernels.dll）");
                if (ErrorLines > 0)
                    sb.Append("；⚠ level=error 行 ").Append(ErrorLines).Append(" 条（看 backend 日志）");
                return sb.ToString();
            }
        }

        public static LogCheck CheckLogs(string gameDir)
        {
            LogCheck r = new LogCheck();
            try
            {
                if (gameDir == null) return r;
                string dir = Path.Combine(Path.Combine(gameDir, ResDirName), "logs");
                r.HasDir = Directory.Exists(dir);
                if (!r.HasDir) return r;

                DateTime newest = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    bool isLoader = n.StartsWith("loader_");
                    bool isBackend = n.StartsWith("backend_");
                    if (!isLoader && !isBackend) continue;
                    if (isLoader) r.LoaderFiles++;
                    else r.BackendFiles++;
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        r.Bytes += fi.Length;
                        if (fi.LastWriteTime > newest) { newest = fi.LastWriteTime; r.Newest = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"); }
                    }
                    catch { }
                }

                // 最新一个 backend 日志的尾部：关键词只在这一个文件里数，避免把历史进程的旧结论混进来
                string target = null;
                DateTime hit = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(dir, "backend_*.jsonl"))
                {
                    DateTime t = File.GetLastWriteTime(f);
                    if (t > hit) { hit = t; target = f; }
                }
                if (target != null)
                {
                    string text = ReadTail(target, 256 * 1024);
                    r.InstallHits = CountOf(text, "\"install\"");
                    r.KernelMissing = CountOf(text, "kernel_pack_missing");
                    r.ErrorLines = CountOf(text, "\"level\":\"error\"");
                    r.GateExit = CountOf(text, "fg_gate_exit");
                }
            }
            catch { }
            return r;
        }

        static int CountOf(string text, string key)
        {
            if (text == null || text.Length == 0 || key.Length == 0) return 0;
            int n = 0, at = 0;
            while (true)
            {
                int i = text.IndexOf(key, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                n++;
                at = i + key.Length;
            }
            return n;
        }

        static string ReadTail(string path, int max)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    long skip = fs.Length > max ? fs.Length - max : 0;
                    fs.Seek(skip, SeekOrigin.Begin);
                    //  显式夹到 max：把"最多读这么多"的意图写进代码，也避开
                    //  `new byte[(int)(long)]` 这种一眼看不出会不会溢出的写法。
                    long tail = fs.Length - skip;
                    if (tail > max) tail = max;
                    if (tail < 0) tail = 0;
                    int tailLen = (int)tail;
                    byte[] buf = new byte[tailLen];
                    int n = fs.Read(buf, 0, tailLen);
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch { return ""; }
        }

        // ---- 界面用的一行/几行说明 ----

        public static string PackCaption()
        {
            string miss;
            if (!Ready(out miss))
                return "资源包未就绪（缺 " + miss + "）—— 点「从 GitHub 取新版」自动下载，或把发布包拖进窗口";
            string r = RuntimeVer;
            bool sr = File.Exists(Path.Combine(ResDir, "sr\\sr_kernels.dll"));
            //  2026-11-01 人话化：括号堆（字节/组件开关说明）撤掉，只留"哪一版、多大、带不带超分"。
            return "资源包 " + Ver + (r.Length > 0 ? " · 运行库 " + r : "")
                 + " · " + DescribeBytes(PackBytes())
                 + (sr ? "（含超分）" : "（不含超分——普通帧生成用不到）");
        }

        public static long PackBytes()
        {
            long n = 0;
            try
            {
                if (!Directory.Exists(PackRoot)) return 0;
                foreach (string f in Directory.GetFiles(PackRoot, "*.*", SearchOption.AllDirectories))
                {
                    try { n += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return n;
        }

        public static string DescribeBytes(long n)
        {
            return Math.Round(n / 1048576.0, 1).ToString() + " MB";
        }

        // 部署到某个游戏目录时的一句"占用多少"（用于部署前提示）
        public static string DeploySizeNote()
        {
            long n = 0;
            try
            {
                foreach (string rel in _mustRes)
                {
                    string f = Path.Combine(ResDir, rel);
                    if (File.Exists(f)) n += new FileInfo(f).Length;
                }
            }
            catch { }
            return DescribeBytes(n);
        }

        // ----------------------------------------------------------------------
        //  识别 + 归位（被 Pack.Inspect / Pack.Import 分流调用）
        // ----------------------------------------------------------------------

        // 判据为什么是"有没有这两个文件"而不是文件名/目录名：
        //   0.4.x 与 0.3.x 的发布件里都有一堆同名文件（version.dll、dlssg_sm86.ini、nvngx_*），
        //   只有 backend\sm86_backend.dll 与 kernels\fg_kernels.dll 是 0.4.x 才有 ——
        //   它们也是"0.4.x 能不能生效"的本体（作者：缺内核包时郑重记一条 kernel_pack_missing）。
        //   这样即使作者改了压缩包目录名、或者用户已经解压过，照样认得出。
        public const string Kind = "dlssg040";

        public static bool ClassifyInto(PackProbe p)
        {
            if (p == null || p.Items == null) return false;
            bool backend = false, kernels = false, ini = false, entry = false;
            foreach (PackItem it in p.Items)
            {
                if (it.Name == "sm86_backend.dll") backend = true;
                else if (it.Name == "fg_kernels.dll") kernels = true;
                else if (it.Name == IniName) ini = true;
                else if (Array.IndexOf(_entries, it.Name) >= 0) entry = true;
            }
            if (!(backend || kernels) || !ini || !entry) return false;

            p.Kind = Kind;
            int alt = 0, res = 0, nEntry = 0;
            foreach (PackItem it in p.Items)
            {
                string rel = TargetRel(it.Name);
                if (rel == null) { it.Role = "other"; continue; }
                if (rel.StartsWith("alternatives\\", StringComparison.OrdinalIgnoreCase))
                {
                    it.Role = "alt";
                    it.Cert = ProxySign.Of(it.Src);
                    alt++;
                }
                else if (rel.StartsWith(ResDirName + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    it.Role = "res040";
                    if (it.Name != "nvngx_dlss.dll" && it.Name != "nvngx_dlssg.dll")
                        it.Cert = ProxySign.Of(it.Src);
                    res++;
                }
                else if (it.Name == IniName) { it.Role = "ini"; }
                else if (rel == "manifest.json") { it.Role = "meta"; }
                else { it.Role = "entry"; it.Cert = ProxySign.Of(it.Src); nEntry++; }
            }
            p.Entry = "version.dll";
            p.Ver = Ver;                       // 归位后才会变；这里先用工具侧的当前版本号提示
            string vf = FirstFile(p, "manifest.json");
            if (vf != null) p.Ver = ManifestVer(vf);
            p.VerBasis = "包里有 " + (kernels ? "kernels\\fg_kernels.dll" : "backend\\sm86_backend.dll")
                       + " → 0.4.x 的新结构（代理只剩 0.8 MB，运行库不再内嵌）"
                       + (vf != null ? "；manifest.json 写的 bundle.version=" + ManifestVer(vf) : "");
            p.Notes.Add("入口 " + nEntry + " 个、备选入口 " + alt + " 个、资源文件 " + res + " 个");
            return true;
        }

        // 落盘位置：资源包里按作者发布包的原样布局放，部署时整份拷过去
        static string TargetRel(string name)
        {
            switch (name)
            {
                case "version.dll": return "version.dll";
                case "dlssg_sm86.ini": return "dlssg_sm86.ini";
                case "manifest.json": return "manifest.json";
                case "sm86_backend.dll": return "dlssg_sm86\\backend\\sm86_backend.dll";
                case "fg_kernels.dll": return "dlssg_sm86\\kernels\\fg_kernels.dll";
                case "nvngx_dlssg.dll": return "dlssg_sm86\\runtime\\nvngx_dlssg.dll";
                case "xefg_kernels.dll": return "dlssg_sm86\\xefg\\xefg_kernels.dll";
                case "nvngx_dlss.dll": return "dlssg_sm86\\sr\\nvngx_dlss.dll";
                case "sr_kernels.dll": return "dlssg_sm86\\sr\\sr_kernels.dll";
                default: break;
            }
            if (Array.IndexOf(_entries, name) >= 0) return "alternatives\\" + name;
            return null;
        }

        static string FirstFile(PackProbe p, string name)
        {
            foreach (PackItem it in p.Items)
                if (it.Name == name) return it.Src;
            return null;
        }

        static string ManifestVer(string path)
        {
            try
            {
                object o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                Dictionary<string, object> top = o as Dictionary<string, object>;
                object raw = null;
                Dictionary<string, object> b = (top != null && top.TryGetValue("bundle", out raw))
                                             ? raw as Dictionary<string, object> : null;
                object v = null;
                if (b != null && b.TryGetValue("version", out v) && v != null) return v.ToString();
            }
            catch { }
            return "";
        }

        public static string Import(PackProbe p, Action<string> log)
        {
            if (p == null || p.Error.Length > 0) return "识别失败：" + (p == null ? "空" : p.Error);
            string missBefore = "";
            bool had = Ready(out missBefore);
            string backup = null;
            try
            {
                Directory.CreateDirectory(PackRoot);
                Directory.CreateDirectory(AltDir);
                Directory.CreateDirectory(ResDir);
                if (had) backup = BackupRootFiles();

                int placed = 0;
                List<PackItem> done = new List<PackItem>();
                long bytes = 0;
                foreach (PackItem it in p.Items)
                {
                    if (it.Role == "other" || it.Role.Length == 0) continue;
                    string rel = TargetRel(it.Name);
                    if (rel == null) continue;
                    if (!WithSr && rel.StartsWith("dlssg_sm86\\sr\\", StringComparison.OrdinalIgnoreCase))
                        continue;                       // 186 MB 的超分件：默认不带
                    string dst = Path.Combine(PackRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(it.Src, dst, true);
                    it.Sha = Sha16(dst);
                    bytes += it.Size;
                    done.Add(it);
                    placed++;
                }
                Pack.WriteLedger(p, done, PackRoot);
                _bundle = null;                          // 版本可能变了，下次重读 manifest

                string miss;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("已更新 0.4.x 资源包 → " + PackRoot);
                sb.AppendLine("  落盘 " + placed + " 个文件（" + DescribeBytes(bytes) + "）"
                    + (backup != null ? "，旧件备份在 " + backup : ""));
                if (!Ready(out miss) && miss.Length > 0) sb.AppendLine("  ⚠ 归位后仍缺：" + miss);
                else sb.AppendLine("  资源包完整：version.dll + " + IniName + " + dlssg_sm86\\{backend,kernels,runtime}");
                if (!WithSr) sb.AppendLine("  未带超分组件（sr\\ 186 MB，出厂 Mode=Off 用不上）；"
                    + "要用 INT8 超分请到「设置」页打开「落盘超分组件」再导一次，届时右键谢谢你未来的自己。");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "归位失败：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        // 旧资源包的小件（根目录三个 + alternatives）备份即可；dlssg_sm86 动辄两百 MB，
        //  重复导同一版本时它的内容不会变，备份它只是白占双倍磁盘。
        static string BackupRootFiles()
        {
            try
            {
                int n = 0;
                string bk = Path.Combine(PackRoot, "_backup", "_pack-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                foreach (string f in Directory.GetFiles(PackRoot))
                {
                    Directory.CreateDirectory(bk);
                    File.Copy(f, Path.Combine(bk, Path.GetFileName(f)), true);
                    n++;
                }
                if (Directory.Exists(AltDir))
                    foreach (string f in Directory.GetFiles(AltDir))
                    {
                        Directory.CreateDirectory(Path.Combine(bk, "alternatives"));
                        File.Copy(f, Path.Combine(Path.Combine(bk, "alternatives"), Path.GetFileName(f)), true);
                        n++;
                    }
                return n > 0 ? bk : null;
            }
            catch { return null; }
        }

        static string Sha16(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

    // ==========================================================================
    //  ② ASI 变体：0.3.x 的代理换了个壳（Version.asi + Ultimate ASI Loader）
    // --------------------------------------------------------------------------
    //  什么时候用它：游戏屏蔽/占住了常规入口名（version / winmm / dxgi …），
    //  作者给的解法是把同一份代理改名成 Version.asi，靠 ASI 加载器（wininet.dll）在
    //  游戏加载系统 DLL 时把它带进进程。
    //  ⚠ 这只换**加载方式**，DLL 本体还是 0.3.x 那颗：实测 Version.asi = 30,021,920 B，
    //    落在 0.3.5 的体积窗口（30,021,408~30,039,840），签名也是作者那张 85BA6676…。
    //    所以它仍然是 Plan.D030 的件 —— 不需要新的方案 id，只是多了一个入口名与一个随行加载器。
    // ==========================================================================
    public static class DlssgAsi
    {
        public const string Proxy = "Version.asi";        // 代理本体（改名版）
        public const string Loader = "wininet.dll";       // ASI 加载器，随行必须一起放

        // 放 Dlssg030 的资源包里：它本来就是 0.3.x 的同一颗 DLL，共用 prerequisites 与 packet 逻辑
        public static string Dir { get { return Path.Combine(Dlssg030.PackRoot, "asi"); } }

        // 认游戏：异环（Neverness to Everness，HTGame.exe）。命中就用这套入口，不必先失败一次。
        static readonly string[] _words = new string[] { "异环", "neverness", "nte", "htgame" };

        public static bool IsGame(DlssgGame g)
        {
            if (g == null) return false;
            string exe = g.Exe == null ? "" : g.Exe.ToLowerInvariant();
            string blob = ((g.Title == null ? "" : g.Title) + " " + (g.Dir == null ? "" : g.Dir)).ToLowerInvariant();
            if (exe.Equals("htgame.exe", StringComparison.Ordinal)) return true;
            foreach (string w in _words)
                if (blob.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool Ready(out string miss)
        {
            List<string> lack = new List<string>();
            if (!Directory.Exists(Dir)) { miss = "资源目录不存在（" + Dir + "）"; return false; }
            if (!File.Exists(Path.Combine(Dir, Proxy))) lack.Add(Proxy);
            if (!File.Exists(Path.Combine(Dir, Loader))) lack.Add(Loader);
            miss = string.Join("、", lack.ToArray());
            return lack.Count == 0;
        }

        public static bool Installed(string gameDir)
        {
            try
            {
                if (gameDir == null) return false;
                string p = Path.Combine(gameDir, Proxy);
                return File.Exists(p) && Dlssg030.IsProxySize(new FileInfo(p).Length);
            }
            catch { return false; }
        }

        // 入口下拉里要不要显示这一项：资源齐了才显示（显示又部署不了是最差体验）
        public static bool Available()
        {
            string miss;
            return Ready(out miss);
        }

        public static string Install(string gameDir, bool force)
        {
            if (gameDir == null || !Directory.Exists(gameDir)) return "目录不存在：" + gameDir;
            string miss;
            if (!Ready(out miss)) return "ASI 变体资源不完整（缺 " + miss + "）—— 把作者的 ASI 版压缩包拖进窗口即可";
            try
            {
                int copied = 0, backed = 0;
                foreach (string nm in new string[] { Proxy, Loader })
                {
                    string s = Path.Combine(Dir, nm);
                    string d = Path.Combine(gameDir, nm);
                    if (!File.Exists(s)) continue;
                    if (File.Exists(d) && nm == Loader)
                    {
                        // wininet.dll 常常是别的 mod / 别的加载器在位：
                        //  一律先备份成 .bak-时间戳，覆盖前可随时还原
                        File.Copy(d, d + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                        backed++;
                    }
                    File.Copy(s, d, true);
                    copied++;
                }
                // 运行库：只在游戏目录里**没有**时才补。作者 README 的通用做法是不覆盖游戏自带的
                //  nvngx（实测 CP2077 / FH6 自带的都比包里那份新），缺了才用包里的垫上。
                int patched = 0;
                foreach (string nm in new string[] { "nvngx_dlss.dll", "nvngx_dlssg.dll" })
                {
                    string s = Path.Combine(Dir, nm);
                    string d = Path.Combine(gameDir, nm);
                    if (!File.Exists(s) || File.Exists(d)) continue;
                    File.Copy(s, d, true);
                    patched++;
                }
                string iniNote = EnsureIni(gameDir, force);
                return string.Format("已部署 ASI 变体：复制 {0} 个文件（{1} + {2}{3}）→ 重启游戏生效。{4}{5}",
                    copied, Proxy, Loader,
                    backed > 0 ? "，原来的 " + Loader + " 已备份成 .bak-时间戳" : "",
                    patched > 0 ? "缺的运行库补了 " + patched + " 个；" : "",
                    iniNote.Length > 0 ? "\n  " + iniNote : "");
            }
            catch (Exception ex)
            {
                return "部署失败：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        static string EnsureIni(string gameDir, bool force)
        {
            string dst = Path.Combine(gameDir, Dlssg.IniName);
            // 优先用 ASI 包里自带的出厂 INI，没有就退回 0.3.x 资源包那份（两者同代）
            string src = Path.Combine(Dir, Dlssg.IniName);
            if (!File.Exists(src)) src = Path.Combine(Dlssg030.CommonDir, Dlssg.IniName);
            try
            {
                if (!File.Exists(src)) return "";
                if (File.Exists(dst) && !force) return "";
                if (File.Exists(dst))
                    File.Copy(dst, dst + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                File.Copy(src, dst, true);
                return "已放入出厂 " + Dlssg.IniName;
            }
            catch (Exception ex) { return "INI 补齐失败：" + ex.Message; }
        }

        // ---- 识别 + 归位（拖入 / 下载命中这种包时）----

        public const string Kind = "dlssg030asi";

        // 判据：Version.asi 落在 0.3.x 的体积窗口（30,021,408~30,039,840）+ 同目录有 wininet.dll。
        //  光看名字会认错：任何 ASI mod 都可以把自己叫 Version.asi。
        public static bool ClassifyInto(PackProbe p)
        {
            if (p == null || p.Items == null) return false;
            PackItem asi = null, loader = null;
            foreach (PackItem it in p.Items)
            {
                if (it.Name == Proxy.ToLowerInvariant()) asi = it;
                else if (it.Name == Loader) loader = it;
            }
            if (asi == null || loader == null) return false;
            if (!Dlssg030.IsProxySize(asi.Size)) return false;

            p.Kind = Kind;
            p.Entry = Proxy;
            foreach (PackItem it in p.Items)
            {
                if (it.Name == Proxy.ToLowerInvariant()) { it.Role = "entry"; it.Cert = ProxySign.Of(it.Src); }
                else if (it.Name == Loader) { it.Role = "loader"; it.Cert = ProxySign.Of(it.Src); }
                else if (it.Name == Dlssg.IniName) { it.Role = "ini"; }
                else if (it.Name == "nvngx_dlss.dll" || it.Name == "nvngx_dlssg.dll")
                { it.Role = "runtime"; it.Cert = ProxySign.Of(it.Src); }
                else it.Role = "other";
            }
            p.Ver = Dlssg030.CodeOf(asi.Size);
            if (p.Ver.Length == 0) p.Ver = Dlssg030.Ver;
            p.VerBasis = Proxy + " = " + asi.Size.ToString("N0") + " B → 0.3.x 的 " + p.Ver
                       + " 档，只是改了加载方式（ASI），随行加载器 " + Loader + " " + loader.Size.ToString("N0") + " B"
                       + "（" + (loader.Cert == null ? "未核验" : ProxySign.Short(loader.Cert)) + "）";
            p.Notes.Add("⚠ " + Loader + " 是第三方的 Ultimate ASI Loader（签名通常不是作者那份），"
                      + "部署时会把它放进游戏目录；如果那里已经有别的 mod 用它，请先确认");
            return true;
        }

        public static string Import(PackProbe p, Action<string> log)
        {
            if (p == null || p.Error.Length > 0) return "识别失败：" + (p == null ? "空" : p.Error);
            try
            {
                Directory.CreateDirectory(Dir);
                List<PackItem> done = new List<PackItem>();
                int n = 0;
                foreach (PackItem it in p.Items)
                {
                    if (it.Role == "other" || it.Role.Length == 0) continue;
                    string dst = Path.Combine(Dir, it.Name);
                    File.Copy(it.Src, dst, true);
                    it.Sha = Sha16(dst);
                    done.Add(it);
                    n++;
                }
                Pack.WriteLedger(p, done, Dir);
                string miss;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("已更新 ASI 变体资源 → " + Dir);
                sb.AppendLine("  落盘 " + n + " 个文件（" + Proxy + " / " + Loader + " / INI / 运行库）");
                if (!Ready(out miss) && miss.Length > 0) sb.AppendLine("  ⚠ 归位后仍缺：" + miss);
                else sb.AppendLine("  资源齐全：适合常规入口名被游戏占住 / 屏蔽时（例如异环）用");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "归位失败：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        static string Sha16(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
            catch { return ""; }
        }

        public static string Uninstall(string gameDir)
        {
            int n = 0;
            try
            {
                foreach (string nm in new string[] { Proxy, Loader })
                {
                    string p = Path.Combine(gameDir, nm);
                    if (File.Exists(p)) { File.Delete(p); n++; }
                }
                string ini = Path.Combine(gameDir, Dlssg.IniName);
                if (File.Exists(ini)) { File.Delete(ini); n++; }
            }
            catch { }
            return "已删除 ASI 变体相关文件 " + n + " 个（" + Proxy + " / " + Loader + " / " + Dlssg.IniName + "）";
        }
    }

    // ==========================================================================
    //  ④ 二游规则表（2026-10-18）
    // --------------------------------------------------------------------------
    //  《异环》《绝区零》《鸣潮》这类二次元抽卡网游，作者在发布包里单独给过一张
    //  「几个网游注入.txt」：鸣潮 version/dxgi、绝区零 d3d12、燕云十六声 version 不行就 dxgi。
    //  这不是挑剔 —— 网游的启动器 / 反作弊常占住常见入口名，只能往它还空着的那几个名字里挑。
    //
    //  ⚠ 这张表只做**提示与警告**，不做锁死（用户选了别的一样能部署）：
    //    "游戏不加载这个入口名"是常态，锁死等于逼用户在失效的方案里反复重试。
    //    工具该做的是把作者给的答案摆出来，让手动改成为知情选择。
    // ==========================================================================
    public static class SecondGame
    {
        public const string Yh = "yh";         // 异环（Neverness to Everness）
        public const string Zzz = "zzz";       // 绝区零
        public const string Wuwa = "wuwa";     // 鸣潮

        // 认游戏。judgment 顺序：先认语录/路径关键词，再认 exe 名。
        //  Client-Win64-Shipping.exe 是 UE4/5 的通用名（一堆 Solid Snake 的游戏都叫这个），
        //  光靠它会把任何虚幻游戏误判成鸣潮，所以这一条只有在同样 mantis 关键词命中时才算。
        public static string Detect(DlssgGame g)
        {
            if (g == null) return "";
            string exe = (g.Exe == null ? "" : g.Exe).ToLowerInvariant();
            string blob = ((g.Title == null ? "" : g.Title) + " " + (g.Dir == null ? "" : g.Dir)).ToLowerInvariant();
            //  异环实测（2026-10-18 用户机截图）：G:\娱乐2\Neverness To Everness\
            //    NTELauncher\  ← 启动器（NTELauncher.exe / NTEGame.exe / libcef.dll / QmGUI.dll …）
            //    Client\WindowsNoEditor\HT\Binaries\Win64\  ← 真正渲染的 HTGame.exe
            //  扫描出来可能只看到 NTELauncher（那里的 exe 最大），所以两个名字都要认。
            if (exe == "ntegame.exe" || exe == "ntelauncher.exe" || exe == "htgame.exe"
                || blob.IndexOf("neverness", StringComparison.Ordinal) >= 0
                || blob.IndexOf("异环", StringComparison.Ordinal) >= 0) return Yh;
            if (exe == "zenlesszonezero.exe" || blob.IndexOf("zenless", StringComparison.Ordinal) >= 0
                || blob.IndexOf("绝区零", StringComparison.Ordinal) >= 0) return Zzz;
            if (blob.IndexOf("鸣潮", StringComparison.Ordinal) >= 0
                || blob.IndexOf("wuthering", StringComparison.Ordinal) >= 0
                || blob.IndexOf("kurogame", StringComparison.Ordinal) >= 0) return Wuwa;
            return "";
        }

        public static string Label(string k)
        {
            if (k == Yh) return "异环";
            if (k == Zzz) return "绝区零";
            if (k == Wuwa) return "鸣潮";
            return "";
        }

        // 作者那张网游注入表里给的答案（同时也解释了为什么需要"特定的插件"）
        public static string SuggestEntry(string k)
        {
            if (k == Yh) return DlssgAsi.Proxy;     // Version.asi（随行加载器 wininet.dll）
            if (k == Zzz) return "d3d12.dll";
            if (k == Wuwa) return "version.dll";
            return "";
        }

        public static string AltEntry(string k)
        {
            if (k == Yh) return "";
            if (k == Zzz) return "";
            if (k == Wuwa) return "dxgi.dll";       // 作者原文："version 不行就 dxgi"
            return "";
        }

        // 0.4.x 的入口建议：本质与 0.3.x 相同（同一颗代理换名字，作者 0.4.0 只是把六个名字
        //  都放进 alternatives\），所以绝区零仍是 d3d12、鸣潮仍是 version（不行换 dxgi）。
        //  唯一例外是异环：0.4.0 **没有**出 ASI 版，这条线停在 0.3.5，返回空串表示"没有专用装法"。
        public static string SuggestEntry040(string k)
        {
            if (k == Zzz) return "d3d12.dll";
            if (k == Wuwa) return "version.dll";
            return "";
        }

        public static bool WantsAsi(string k) { return k == Yh; }

        // 一句话说明，进确认框。
        //  带官方支持范围的依据（2026-10-18 复核）：
        //   绝区零 3.0 官方 FAQ Q7：DLSS 帧生成 = RTX 40 系及以上；FSR3 帧生成 = RTX 20 系及以上。
        //   鸣潮 2.2 版本内容说明：「当前版本支持 NVIDIA RTX 40 系列及以上显卡设备开启【补帧】功能」。
        //   异环：官方 DLSS 4.5 多帧生成只对 50 系（社区与官方宣传一致）。
        //  也就是说 20/30 系在鸣潮和异环上**没有**官方帧生成，绝区零有 FSR3 这条保底。
        public static string Why(string k)
        {
            if (k == Yh)
                return "异环：官方多帧生成只给 RTX 50 系，20/30 系只能靠这套。常规入口名又被它占住 / 屏蔽，"
                     + "所以只能用 ASI 加载器（Version.asi + wininet.dll）把代理带进进程。";
            if (k == Zzz)
                return "绝区零：官方 3.0 起给了 20 系以上 FSR3 帧生成（DLSS 帧生成仍只给 40 系以上）——"
                     + "只想更流畅可以先开官方那个，零风险；本方案是把它换成真 DLSS-G，画质/延迟更好，但属于注入。"
                     + "作者指定 d3d12.dll（其它入口名可能被反作弊隔离）。";
            if (k == Wuwa)
                return "鸣潮：官方「补帧」只给 RTX 40 系以上（2.2 版本说明原文），20/30 系只能靠本方案。"
                     + "作者给的是 version.dll，加载不到时换 dxgi.dll。";
            return "";
        }

        // ----------------------------------------------------------------------
        //  落地目录解析：启动器目录 ≠ 渲染目录
        // ----------------------------------------------------------------------
        //  代理 DLL 只有放在**渲染 EXE 所在目录**才会被进程加载。
        //  异环的启动器（NTELauncher\，里面还有 NTEGame.exe）与渲染目录（Client\…\Binaries\Win64\）
        //  是两层，扫描很可能只录到启动器那层 —— 直接部署到那里什么都不会发生。
        //  所以帧生成相关的部署/查询统一走这里解析后的目录。
        //  ⚠ 只影响帧生成，不动条目本身的 Dir/Exe（否则会改坏「启动游戏」）。
        public static string ResolveDir(string dir)
        {
            if (dir == null || dir.Length == 0) return dir;
            try
            {
                string d = dir.TrimEnd('\\');
                if (!Directory.Exists(d)) return dir;
                // 已经是 UE 的渲染层（…\Binaries\Win64）
                if (Path.GetFileName(d).Equals("Win64", StringComparison.OrdinalIgnoreCase)) return d;

                bool isLauncherDir = File.Exists(Path.Combine(d, "NTELauncher.exe"));
                bool hasLauncherSub = Directory.Exists(Path.Combine(d, "NTELauncher"));
                if (!isLauncherDir && !hasLauncherSub) return d;      // 其它游戏不碰

                string start = isLauncherDir ? Path.GetDirectoryName(d) : d;
                string cur = start;
                for (int up = 0; up < 3 && cur != null && cur.Length > 0; up++)
                {
                    string client = Path.Combine(cur, "Client");
                    if (Directory.Exists(client))
                    {
                        string hit = FindExeUnder(client, "HTGame.exe", 6);
                        if (hit != null) return hit;
                        hit = FindExeUnder(client, "NTEGame.exe", 6);
                        if (hit != null) return hit;
                    }
                    cur = Path.GetDirectoryName(cur);
                }
                // 找不到渲染层：保持原目录，由调用方在日志里如实提醒
                return d;
            }
            catch { return dir; }
        }

        // 在 root 下（深度 ≤maxDepth）找某个 exe，返回它所在目录
        static string FindExeUnder(string root, string name, int maxDepth)
        {
            try
            {
                foreach (string f in Directory.GetFiles(root, name, SearchOption.TopDirectoryOnly))
                    return Path.GetDirectoryName(f);
                if (maxDepth <= 0) return null;
                foreach (string sub in Directory.GetDirectories(root))
                {
                    string hit = FindExeUnder(sub, name, maxDepth - 1);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        // 这个目录是不是"看起来像启动器层"（用于给用户一句提醒）
        public static bool LooksLauncher(string dir)
        {
            try
            {
                if (dir == null || dir.Length == 0) return false;
                return File.Exists(Path.Combine(dir.TrimEnd('\\'), "NTELauncher.exe"));
            }
            catch { return false; }
        }

        // 二游专属的一句提醒（给推荐卡 / 状态行用）
        public static string Note(DlssgGame g)
        {
            string k = Detect(g);
            if (k.Length == 0) return "";
            string s = SuggestEntry(k);
            string alt = AltEntry(k);
            return "二游规则（作者网游注入表）：《" + Label(k) + "》用 " + s
                 + (alt.Length > 0 ? "，加载不到再换 " + alt : "")
                 + "。手动改别的名字也可以，但你得知道作者在表上给的不是那个。";
        }
    }

    // ==========================================================================
    //  ⑤ 作者的百度网盘下载源（GitHub 之外的备份通道）
    // --------------------------------------------------------------------------
    //  为什么单独列：有几个包**不在 GitHub 上**（异环专用、绝区零 XeSS 整合包），
    //  而国内直连 GitHub 又时通时不通 —— 网盘反而更稳。
    //  这里只存链接与用途，不做自动下载：百度网盘需要登录、且直链会过期，
    //  与其写一个"十次里成功两次"的抓取器，不如把链接一键复制给用户。
    // ==========================================================================
    public class NetDisk
    {
        public string Name = "";
        public string Url = "";
        public string Code = "";
        public string For = "";      // 对应哪套方案 / 哪个游戏
        public string Note = "";     // 怎么用
    }

    public static class NetDiskSources
    {
        public static readonly NetDisk[] All = new NetDisk[] {
            new NetDisk {
                Name = "0.4.1 for cuda（含数字签名）原生XeSS多帧生成（百度网盘）",
                Url  = "https://pan.baidu.com/s/1KJy1yaif8Wv6LgMg38C3Mg?pwd=9tbr",
                Code = "9tbr",
                For  = "全系 RTX（20~50系）· 0.4.1 正式版多帧生成",
                Note = "下载后直接拖入窗口（或解压为文件夹拖入），工具自动校验签名并归位"
            },
            new NetDisk {
                Name = "dlss4-4.5.zip（官方画质升级模型）",
                Url  = "https://pan.baidu.com/s/1oGgr0gri1E0OW1ooKo68jQ?pwd=3x3y",
                Code = "3x3y",
                For  = "画质升级 · DLSS 4 / 4.5 官方超分辨率核心（nvngx_dlss.dll）",
                Note = "解压后替换老游戏同名文件，消除模糊涂抹与拖影，画质大幅提升"
            },
            new NetDisk {
                Name = "RE框架（卡普空RE引擎专用）.zip",
                Url  = "https://pan.baidu.com/s/1yEzPb9zsKvgtOVEGqOYNdQ?pwd=jfae",
                Code = "jfae",
                For  = "卡普空专用 · REFramework 注入框架（dinput8.dll）",
                Note = "《生化危机》、《鬼武者》、《龙之信条2》、《怪猎》防崩溃与UI闪烁必备"
            },
            new NetDisk {
                Name = "0.4.1 for cuda（含数字签名）原生XeSS多帧生成（夸克网盘）",
                Url  = "https://pan.quark.cn/s/23b8c063803e",
                Code = "免密直达",
                For  = "全系 RTX（20~50系）· 0.4.1 正式版（含 0.4.0 / RE 框架）",
                Note = "下载后直接拖入窗口（或解压为文件夹拖入），工具自动校验签名并归位"
            },
            new NetDisk {
                Name = "绝区零 专用 XeSS 多帧生成.zip",
                Url  = "https://pan.baidu.com/s/1-22m05onTTCSt9QPxk8_5Q?pwd=23s7",
                Code = "23s7",
                For  = "绝区零 · 方案 A（OptiScaler + XeSS-FG）",
                Note = "解压后按方案 A 的方式放（工具不自动归位 OptiScaler 包，因为里面有你自己调好的参数）"
            },
            new NetDisk {
                Name = "0.4.0.zip",
                Url  = "https://pan.baidu.com/s/1JwCyxX4hyzktKRYHfmAoFw?pwd=5jm8",
                Code = "5jm8",
                For  = "任意游戏 · 0.4.x（与 GitHub 上那个是同一个包）",
                Note = "拖进窗口即可自动识别、归位，并问要不要重抄到已装的游戏"
            },
            new NetDisk {
                Name = "0.3.5 20系30系 异环专用 多帧生成.zip",
                Url  = "https://pan.baidu.com/s/1eVDfIKHCPgmUHS0J_tafbw?pwd=past",
                Code = "past",
                For  = "异环 · ASI 变体（Version.asi + wininet.dll）",
                Note = "拖进窗口即可自动识别归位；归位后异环卡片会推荐「ASI 变体」"
            },
        };

        public static string Block()
        {
            StringBuilder sb = new StringBuilder();
            foreach (NetDisk d in All)
            {
                sb.AppendLine(d.Name);
                sb.AppendLine("  链接：" + d.Url);
                sb.AppendLine("  提取码：" + d.Code);
                sb.AppendLine("  用途：" + d.For);
                sb.AppendLine("  用法：" + d.Note);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }

    // ==========================================================================
    //  ③ GitHub 下载源
    // --------------------------------------------------------------------------
    //  以前的流程：作者发新版 → 用户去 GitHub 找 asset → 下载 → 解压 → 找目录 → 拖进窗口。
    //  现在：点一下 → 查 releases/latest → 下 asset → 交给 Pack 识别归位。
    //
    //  为什么走 Releases API 而不是继续抓 raw 文件：
    //    0.4.0 起发布形态是一个 zip（80,551,324 B），几十个文件零散 raw 拉不全，
    //    而且 sr_kernels.dll 有 106 MB，raw 只能单文件、 manifiest 里那 24 个条目要拉 24 次。
    //  老 tag（0.3.5 及以前）**没有打包好的 asset**，只有源码 —— 这一层要说清楚，
    //    不能让用户以为"工具没联网成功"，所以没有 zip 的 release 会在列表里标注"仅源码"。
    // ==========================================================================
    public static class Upstream
    {
        public class Release
        {
            public string Tag = "";
            public string Name = "";
            public string Html = "";
            public string At = "";
            public string Asset = "";      // zip 资产名；空 = 该版本没打包二进制
            public string Url = "";
            public long Size;

            public bool HasZip { get { return Asset.Length > 0 && Url.Length > 0; } }

            public string Line()
            {
                string head = "  " + (Tag.Length > 0 ? Tag : "?");
                if (At.Length >= 10) head += "  (" + At.Substring(0, 10) + ")";
                if (HasZip)
                    head += "  " + Asset + "  " + Math.Round(Size / 1048576.0, 1) + " MB";
                else
                    head += "  仅源码，没有打包好的二进制（达不到的一律别列出来骗人）";
                return head;
            }
        }

        // 支持 https://github.com/a/b 与 a/b 两种写法；认不出就返回空串，由调用方兜默认
        static string NormRepo(string raw)
        {
            if (raw == null) raw = "";
            Match m = Regex.Match(raw, @"(?:github\.com/)?([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)");
            if (m.Success) return m.Groups[1].Value + "/" + m.Groups[2].Value;
            return "";
        }

        // 默认仓库：作者那个。
        public static string Repo()
        {
            string r = NormRepo(Program.Cfg != null ? Program.Cfg.DlssgGitHub : "");
            return r.Length > 0 ? r : "sdli1995/dlssg_for_sm86";
        }

        //  各模块的下载源分开：0.3.x / 0.4.x 走 dlssg_for_sm86，方案 B 走 nvidia_mfg_bridge。
        //  两套包的发布节奏与文件结构都不一样，共用一个地址只会把其中一个变成摆设。
        public static string RepoOf(string key)
        {
            if (string.Equals(key, "mfgbridge", StringComparison.OrdinalIgnoreCase))
            {
                string r = NormRepo(Program.Cfg != null ? Program.Cfg.MfgBridgeRepo : "");
                return r.Length > 0 ? r : "gt2333588/nvidia_mfg_bridge";
            }
            return Repo();
        }

        // GitHub 强制 TLS 1.2+，而 csc 编出来的 exe 跑在 .NET 4.0 兼容模式下，
        //  ServicePointManager 默认只开 SSL3/TLS1.0 → 不设这里必然报"未能创建 SSL/TLS 安全通道"。
        //  HttpWebRequest 版（续传要用它，WebClient 不支持 Range）
        static void Prep(HttpWebRequest rq)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            rq.UserAgent = "Fluxion";
            rq.Timeout = 30000;              // 建连 / 等响应头：卡住就抛，别让人干等 5 分钟
            rq.ReadWriteTimeout = 30000;     // 单次读；网络断在半路也能尽快走重试
            try { rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
        }

        static void Prep(WebClient wc)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            wc.Headers.Add("User-Agent", "Fluxion");
            if (Program.Cfg != null && Program.Cfg.DlssgUseProxy && Program.Cfg.DlssgProxy.Length > 0)
            {
                try { wc.Proxy = new WebProxy(Program.Cfg.DlssgProxy); } catch { }
            }
        }

        public static List<Release> Fetch(int count, out string err)
        {
            return Fetch(count, Repo(), out err);
        }

        public static List<Release> Fetch(int count, string repo, out string err)
        {
            err = "";
            List<Release> list = new List<Release>();
            try
            {
                string url = count <= 1
                    ? "https://api.github.com/repos/" + repo + "/releases/latest"
                    : "https://api.github.com/repos/" + repo + "/releases?per_page=" + count;
                string json = GetJson(url, out err);
                if (json == null) return list;
                //  ⚠ JavaScriptSerializer.DeserializeObject 对 JSON 数组返回的是 object[]（不是 ArrayList），
                //   只有 Skilled Comparable 版本才返回 ArrayList。2026-10-18 实测在本机 .NET 4 上是 object[]，
                //  只按 ArrayList 转型的话，会出现"JSON 拿到了、却认不出任何 release"，而且不报错。
                //   所以这里统一按 IEnumerable 走，两种形状都吃。
                CollectFrom(new JavaScriptSerializer().DeserializeObject(json), list);
                return list;
            }
            catch (Exception ex)
            {
                err = Explain(ex);
                return new List<Release>();
            }
        }

        // 两路取 JSON：先按配置（有代理就先代理，没配就交给系统默认），失败再**显式直连**。
        //  为什么要两路：国内直连 GitHub 时通时不通（2026-10-18 实测，同一条 API 几分钟前能通、
        //  几分钟后报"无法连接到远程服务器"），单路实现会把这个当成"用户没网"。
        //  显式直连 = wc.Proxy = null：绕开可能已经坏掉的系统代理设置。
        static bool HasProxy()
        {
            return Program.Cfg != null && Program.Cfg.DlssgUseProxy
                && Program.Cfg.DlssgProxy != null && Program.Cfg.DlssgProxy.Length > 0;
        }

        static string GetJson(string url, out string err)
        {
            err = "";
            string e1 = "", e2 = "", s;
            if (HasProxy())
            {
                s = GetJsonOnce(url, Program.Cfg.DlssgProxy, out e1);
                if (s != null) return s;
                s = GetJsonOnce(url, "", out e2);           // 代理挂了 → 显式直连
                if (s != null) return s;
                err = "代理：" + e1 + "；直连：" + e2;
                return null;
            }
            s = GetJsonOnce(url, null, out e1);             // null = 交给系统默认代理
            if (s != null) return s;
            s = GetJsonOnce(url, "", out e2);               // 系统代理坏 → 显式直连
            if (s != null) return s;
            err = e1.Length > 0 ? e1 : e2;
            return null;
        }

        static string GetJsonOnce(string url, string proxy, out string err)
        {
            err = "";
            try
            {
                using (WebClient wc = new WebClient())
                {
                    Prep(wc);
                    if (proxy != null) wc.Proxy = proxy.Length == 0 ? null : new WebProxy(proxy);
                    wc.Headers.Add("Accept", "application/vnd.github+json");
                    return wc.DownloadString(url);
                }
            }
            catch (Exception ex) { err = Explain(ex); return null; }
        }

        static void CollectFrom(object o, List<Release> list)
        {
            if (o == null) return;
            Dictionary<string, object> one = o as Dictionary<string, object>;
            if (one != null)
            {
                Release r = Parse(one);
                if (r != null) list.Add(r);
                return;
            }
            IEnumerable seq = o as IEnumerable;
            if (seq == null || seq is string) return;
            foreach (object it in seq)
            {
                Dictionary<string, object> d = it as Dictionary<string, object>;
                if (d == null) continue;
                Release r = Parse(d);
                if (r != null) list.Add(r);
            }
        }

        public static Release Latest(out string err)
        {
            return Latest(Repo(), out err);
        }

        public static Release Latest(string repo, out string err)
        {
            List<Release> l = Fetch(1, repo, out err);
            return l.Count > 0 ? l[0] : null;
        }

        static Release Parse(Dictionary<string, object> d)
        {
            Release r = new Release();
            r.Tag = S(d, "tag_name");
            r.Name = S(d, "name");
            r.Html = S(d, "html_url");
            r.At = S(d, "published_at");
            object raw = null;
            IEnumerable assets = (d.TryGetValue("assets", out raw)) ? raw as IEnumerable : null;
            if (assets != null && !(assets is string))
                foreach (object it in assets)
                {
                    Dictionary<string, object> a = it as Dictionary<string, object>;
                    if (a == null) continue;
                    string nm = S(a, "name");
                    if (nm.Length == 0) continue;
                    // 打包好的二进制资产：zip（dlssg_for_sm86 0.4.0 起）或 7z（MFG Bridge）。
                    //  源码包（Source code / tarball）与校验文件一律跳过 —— 那些装不成插件。
                    bool isZip = nm.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    bool is7z = nm.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
                    if (!isZip && !is7z) continue;
                    // 同一次 release 里可能还有别平台的包：跳过 x86 那份
                    if (nm.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    r.Asset = nm;
                    r.Url = S(a, "browser_download_url");
                    object sv = null;
                    if (a.TryGetValue("size", out sv) && sv != null)
                        try { r.Size = Convert.ToInt64(sv); } catch { }
                    break;
                }
            return r;
        }

        // 下到 <数据目录>\_inbox\<资产名>。返回本地路径（err 非空则失败）。
        public static string Download(Release r, Action<string> log, out string err)
        {
            err = "";
            if (r == null || !r.HasZip) { err = "该版本没有打包好的 zip 资产"; return null; }
            try
            {
                Directory.CreateDirectory(Pack.InboxRoot);
                string final = Path.Combine(Pack.InboxRoot, r.Asset);
                // 同名残留会让"下载完了没"的判断失真（旧的 0.3.x zip 也能叫这名字）
                if (File.Exists(final))
                {
                    File.Delete(final);
                    if (log != null) log("  旧的同名文件已删除：" + r.Asset);
                }
                string part = final + ".part";
                string e1 = "", e2 = "";
                bool ok;
                if (HasProxy())
                {
                    ok = DownloadOnce(r.Url, part, Program.Cfg.DlssgProxy, log, out e1);
                    if (!ok)
                    {
                        if (log != null) log("  代理下载失败（" + e1 + "），改直连重试…");
                        ok = DownloadOnce(r.Url, part, "", log, out e2);
                    }
                }
                else
                {
                    ok = DownloadOnce(r.Url, part, null, log, out e1);
                    if (!ok)
                    {
                        if (log != null) log("  直连失败（" + e1 + "），换一种方式重试…");
                        ok = DownloadOnce(r.Url, part, "", log, out e2);
                    }
                }
                if (!ok)
                {
                    err = e1.Length > 0 ? e1 : e2;
                    return null;
                }
                File.Move(part, final);
                long n = new FileInfo(final).Length;
                // ⚠ 体积对不上就是没下完（GitHub 会给出完整的 Content-Length，不会中途缩水）
                if (r.Size > 0 && n != r.Size)
                {
                    err = "文件大小不符（下载 " + n + " B，预期 " + r.Size + " B）——";
                    try { File.Delete(final); } catch { }
                    return null;
                }
                //  完整性：体积对不代表内容对（续传时远端换过包、或中间层塞了错误页都会踩到）。
                //  zip 能列出条目才算没坏；7z 走 tar 解压那一步会自然暴露，这里不重复校验。
                if (Pack.LooksLikeZipPublic(final))
                {
                    try
                    {
                        using (var zf = System.IO.Compression.ZipFile.OpenRead(final))
                            if (zf.Entries.Count == 0) throw new InvalidDataException("压缩包是空的");
                    }
                    catch (Exception exz)
                    {
                        //  ⚠ 只**告警**、不删文件：体积已经和服务器给的 Content-Length 对上了，
                        //    这里更可能是"压缩算法不被 .NET 认识"（比如将来换了 zstd），
                        //    而不是文件坏了。删掉会把人好不容易下完的 76 MB 扔掉；
                        //    真坏了，解压那一步会给出更准确的报错。
                        if (log != null) log("  ⚠ 压缩包自检未通过（" + exz.Message + "），仍交给解压环节判断");
                    }
                }
                return final;
            }
            catch (Exception ex)
            {
                err = Explain(ex);
                return null;
            }
        }

        //  体积显示：小于 1 MB 用 KB。原来一律除 1048576，小文件全程显示"0 MB / 0 MB"。
        static string Amt(long n)
        {
            if (n >= 1048576) return (n / 1048576.0).ToString("0.0") + " MB";
            return Math.Max(1, n / 1024) + " KB";
        }

        // 一次下载尝试（proxy == null 走系统默认；"" 显式直连）。
        //  ★ 支持**断点续传**：part 里已有的字节会被 Range 请求接上。
        //    为什么必须做：0.4.x 的包 76.8 MB，国内直连 GitHub 经常在 60~90% 断掉，
        //    原来一断就从 0 重来 —— 用户看到的就是"下载永远下不完"。
        //    调用方（Download）在代理失败后改直连重试，此时 part 还在，等于自动续传。
        static bool DownloadOnce(string url, string part, string proxy, Action<string> log, out string err)
        {
            err = "";
            try
            {
                long have = 0;
                try { if (File.Exists(part)) have = new FileInfo(part).Length; } catch { have = 0; }

                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
                Prep(rq);
                if (proxy != null) rq.Proxy = proxy.Length == 0 ? null : new WebProxy(proxy);
                if (have > 0) rq.AddRange(have);

                using (HttpWebResponse resp = (HttpWebResponse)rq.GetResponse())
                {
                    bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                    if (have > 0 && !partial)
                    {
                        //  服务器不认 Range（返回整包）：只能从头写，否则会把整包接在残包后面
                        if (log != null) log("  服务器不支持续传，从头下载…");
                        have = 0;
                    }
                    long total = have + Math.Max(0, resp.ContentLength);
                    if (log != null && have > 0)
                        log("  从 " + Amt(have) + " 处续传（共 " + Amt(total) + "）");

                    using (var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                                                   FileAccess.Write, FileShare.None, 81920))
                    using (Stream st = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[81920];
                        long got = have;
                        int last = -1, n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            got += n;
                            int pct = total > 0 ? (int)(got * 100 / total) : 0;
                            int step = pct / 5;
                            if (step != last)
                            {
                                last = step;
                                if (log != null)
                                    log(string.Format("  {0}%  {1} / {2}", pct, Amt(got), Amt(total)));
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception ex) { err = Explain(ex); return false; }
        }

        static string Explain(Exception ex)
        {
            string m = ex.Message;
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        return "GitHub API 拒绝了请求（403）：未登录的 API 每小时只给 60 次，"
                             + "也可能被网络中间层拦了。等一会儿再试，或在设置页把代理打开";
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return "找不到这个仓库或 release（404）：检查「设置」页里的 GitHub 仓库地址";
                    return "HTTP " + (int)resp.StatusCode + "：" + m;
                }
            }
            return m + "（国内网络建议开启设置页里的代理）";
        }

        static string S(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return v.ToString();
        }
    }
}
