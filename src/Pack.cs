// ==========================================================================
//  Pack.cs —— 插件包导入：把作者发布的压缩包拖进窗口，自动识别 → 校验 → 归位
// ==========================================================================
//  为什么需要这层：
//    作者每发一版，用户要做的都是同一套手工活 —— 下载 zip、自己解压、在
//    "0.3.5 DLSS多帧生成 支持20和30系 默认version注入" 这种目录里找出该复制哪几个
//    文件、再放进 <数据目录>\dlssg030-pack\{common,alts}。换台机器还要再来一遍。
//    这里把它变成一次拖拽。
//
//  核心原则：**只认内容**，不认文件名，也不认目录名。
//    作者每版的解压目录名都不一样（同一次发布里还有 "供替换注入" 这种子目录），
//    所以这里递归收集全部文件，再用 文件名 + 体积 + INI 内容特征 判定归属。
//
//  判据来源：Catalog（数据目录 catalog.json，缺失时用代码里的内置默认）。
//    内置默认是 2026-09-18 实测三份真实发布包、2026-09-21 复核 0.3.5 量出来的，不是估的：
//      0.3.0 档 入口 17,528,608~17,547,040 · INI 2099
//      0.3.2 档 入口 29,974,816~29,993,760 · INI 3548（含 SM75/Turing）
//      0.3.5 档 入口 30,021,408~30,039,840 · INI 3571（与 0.3.2 后段逐字节相同）
//      旧版     入口 15,667,520 · 没有 dlssg_sm86.ini
//    要看"现在生效的是哪一版"：python tools\catalog_probe.py 或界面「运行时状态」那一行。
//    注意 alts\ 里 5 个备选入口的体积**各不相同**（每个都单独签过名），所以判据是区间而不是等值。
//
//  归位规则（与 Dlssg030 的 pack 布局一一对应）：
//    common\  ← 入口代理 version.dll + dlssg_sm86.ini + nvngx_dlss.dll + nvngx_dlssg.dll
//    alts\    ← 其余入口名（winmm / dxgi / d3d12 / dbghelp / dinput8 / winhttp）
//    pack.json ← 导入账本：来源、版本、每个文件的体积、sha256 与**签名指纹**
//  包里多余的 .md / .txt 只做提示，不落盘。
//
//  签名核验（ProxySign，2026-09-18 加）：
//    识别时对每个入口 / 运行库取签名证书指纹，与白名单比对；结论进报告与账本。
//    白名单外的不硬拦，由 UI 弹一次二次确认（分级，见 NeedsTrustAsk）。
//    判据是指纹而不是"签名是否有效"—— 作者的代理是自签名的，链校验必然不过。
//
//  与旧版代码的关系：
//    这是"作者换了内核版本"这类升级的**唯一入口**。导入完成后调用 Plan.SwitchTo(..., force)
//    把新资源重抄到每个已装游戏（force 是必需的：入口已在我们这套时 SwitchTo 默认会短路）。
// ==========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Fluxion
{
    // 包里的一个文件（识别后带上归属角色）
    public class PackItem
    {
        public string Role = "";     // entry / alt / ini / runtime / opti / other
        public string Name = "";     // 文件基名（小写）
        public string Src = "";      // 源绝对路径
        public long Size;
        public string Sha = "";      // sha256 前 16 位
        public CertInfo Cert;        // 签名核验结论（只对 entry / alt / runtime 填）
    }

    // 一个 DLL 的签名核验结论
    public class CertInfo
    {
        public string Thumb = "";     // 签名证书指纹（大写十六进制）；空 = 取不到
        public string Subject = "";   // 证书主题（仅供排查，不参与判定）
        public string Verdict = "";   // upstream / nvidia / other / unsigned / missing

        public bool Ok { get { return Verdict == "upstream" || Verdict == "nvidia"; } }
    }

    // 代理入口的签名核验。
    //
    //  为什么判的是"证书指纹"而不是"签名是否有效"：
    //    上游作者的代理是**自签名**的 —— Authenticode 的链校验对它们一律不通过。
    //    2026-09-18 实测：资源包里 6 个入口（version + alts 五个）的状态全是 UnknownError。
    //    所以按 Status == Valid 硬判会把**全部合法代理**判成非法。
    //    指纹只认"这张证书"，与链是否受信无关 —— 这正是 FrameGen-Manager 用的那一层。
    //
    //  指纹值来自本机 0.3.5 资源包实测（2026-09-21 换包前后各验一次，指纹相同）：
    //    6 个入口共用 85BA6676…（作者证书），nvngx 运行库是 7B7B0B66…（NVIDIA 官方）。
    //    方案 A 的 OptiScaler 入口（dxgi.dll）实测**未签名** —— 它走不了验签，也不该走。
    public static class ProxySign
    {
        // 白名单指纹读 Catalog（内置默认就是 Catalog.DefThumbUp / DefThumbNv）。
        //  作者换证书时只改 catalog.json，不用重新编译 —— 这条与体积档属同一类"换包要改的事实"。
        public static string ThumbUpstream { get { return Catalog.ThumbUpstream; } }
        public static string ThumbNvidia { get { return Catalog.ThumbNvidia; } }

        static readonly System.Collections.Generic.Dictionary<string, CertInfo> Cache =
            new System.Collections.Generic.Dictionary<string, CertInfo>(StringComparer.OrdinalIgnoreCase);
        const int CacheMax = 256;
        // Cache 在识别线程池（Classify 写缓存）与 UI 线程（PackActual/OwnerOf/TrustGateFile 读）并发访问：
        // .NET Framework 的 Dictionary 并发写可致内部桶损坏（死循环 = 整程序挂死），全部读写都要过这把锁。
        static readonly object cacheLock = new object();

        // 只从 PE 的签名表里取证书，不做链校验（自签名本来就过不了链校验）。
        //  未签名的文件 CreateFromSignedFile 会抛 CryptographicException —— 那就是 unsigned。
        public static CertInfo Of(string path)
        {
            //  缓存键带上体积与修改时间：换包 / 停放改名都会动到其中之一，
            //  不会出现"文件换了、结论还是旧的"。
            string key = null;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return OfUncached(path);        // 不存在：直接走原路径报 missing
                key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTime.Ticks;
                CertInfo hit;
                lock (cacheLock) { if (Cache.TryGetValue(key, out hit)) return hit; }
            }
            catch { return OfUncached(path); }

            CertInfo c = OfUncached(path);
            try
            {
                lock (cacheLock)
                {
                    if (Cache.Count >= CacheMax) Cache.Clear();     // 换包是一次性的，不需要 LRU
                    Cache[key] = c;
                }
            }
            catch { }
            return c;
        }

        static CertInfo OfUncached(string path)
        {
            CertInfo c = new CertInfo();
            if (path == null || path.Length == 0 || !File.Exists(path)) { c.Verdict = "missing"; return c; }
            try
            {
                X509Certificate x = X509Certificate.CreateFromSignedFile(path);
                c.Thumb = (x.GetCertHashString() ?? "").ToUpperInvariant();
                try { c.Subject = x.Subject ?? ""; } catch { }
            }
            catch { c.Verdict = "unsigned"; return c; }

            if (c.Thumb.Length == 0) c.Verdict = "unsigned";
            else if (c.Thumb == Catalog.ThumbUpstream) c.Verdict = "upstream";
            else if (c.Thumb == Catalog.ThumbNvidia) c.Verdict = "nvidia";
            else c.Verdict = "other";
            return c;
        }

        // 一行结论，直接进日志 / 对话框
        public static string Short(CertInfo c)
        {
            if (c == null) return "未核验";
            string head = c.Thumb.Length >= 8 ? c.Thumb.Substring(0, 8) + "…" : c.Thumb;
            if (c.Verdict == "upstream") return "上游作者签名（白名单命中 " + head + "）";
            if (c.Verdict == "nvidia") return "NVIDIA 官方签名（白名单命中 " + head + "）";
            if (c.Verdict == "unsigned") return "未签名";
            if (c.Verdict == "missing") return "文件不存在";
            return "非白名单签名（" + head + "）";
        }
    }

    // 一次识别（一个 zip 或一个目录）的结果
    public class PackProbe
    {
        public string Input = "";       // 用户拖进来的原始路径
        public string TempDir = "";     // zip 解压出的临时目录（空 = 直接用的目录）
        public string Kind = "";        // Pack.K_* 之一
        public string Ver = "";         // 版本串（从路径名推测，仅供显示）
        public string VerBasis = "";    // 实测判据（这才是结论依据）
        public string Entry = "";       // 主入口名
        public List<PackItem> Items = new List<PackItem>();
        public List<string> Notes = new List<string>();     // 识别过程中的提示
        public string Error = "";       // 致命错误（解压失败等）

        public bool Ready    // 有没有可归位的东西
        {
            get
            {
                foreach (var it in Items)
                    if (it.Role == "entry" || it.Role == "alt" || it.Role == "ini") return true;
                return false;
            }
        }

        public string KindLabel()
        {
            if (Kind == Pack.K_030) return "DLSS MFG 0.3.x（真 DLSS 多帧生成 · 代理模式）";
            if (Kind == Pack.K_LEGACY) return "dlssg_for_sm86 旧版（0.2.x）";
            if (Kind == Pack.K_OPTI) return "OptiScaler / XeMfg（方案 A）";
            return "未识别";
        }

        public int Count(string role)
        {
            int n = 0;
            foreach (var it in Items) if (it.Role == role) n++;
            return n;
        }

        // 完整的多行报告，直接进日志
        public string Report()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("包      " + Input);
            if (TempDir.Length > 0) sb.AppendLine("        已解压到 " + TempDir);
            if (Error.Length > 0) { sb.AppendLine("错误    " + Error); return sb.ToString().TrimEnd(); }
            sb.AppendLine("类型    " + KindLabel());
            if (Ver.Length > 0) sb.AppendLine("包名版本 " + Ver + "（仅从路径推测，不作判据）");
            if (VerBasis.Length > 0) sb.AppendLine("实测判据 " + VerBasis);
            if (Entry.Length > 0) sb.AppendLine("主入口  " + Entry + "（另有 " + (Count("alt")) + " 个备选入口）");
            sb.AppendLine("归位清单");
            foreach (var it in Items)
            {
                string where = it.Role == "entry" ? "common" : it.Role == "alt" ? "alts  "
                             : it.Role == "ini" ? "common" : it.Role == "runtime" ? "common" : "(跳过)";
                sb.AppendLine(string.Format("  {0}  {1,-24} {2,12:N0} B", where, it.Name, it.Size));
            }
            bool anySign = false;
            foreach (var it in Items) if (it.Cert != null) { anySign = true; break; }
            if (anySign)
            {
                string up = Catalog.ThumbUpstream;
                sb.AppendLine("签名核验（上游作者证书 " + (up.Length > 8 ? up.Substring(0, 8) + "…" : up) + "）");
                foreach (var it in Items)
                    if (it.Cert != null) sb.AppendLine("  " + it.Name.PadRight(18) + ProxySign.Short(it.Cert));
            }
            foreach (string s in Notes) sb.AppendLine("  注: " + s);
            return sb.ToString().TrimEnd();
        }
    }

    // 导入账本里一条文件记录（只读回来的形状）
    public class LedgerFile
    {
        public long Size;
        public string Sha = "", Role = "", Signer = "", SignVerdict = "";
    }

    // pack.json 读回来的形状
    public class PackLedger
    {
        public string ImportedAt = "", Source = "", Ver = "", VerBasis = "", Kind = "";
        public Dictionary<string, LedgerFile> Files =
            new Dictionary<string, LedgerFile>(StringComparer.OrdinalIgnoreCase);
    }

    public static class Pack
    {
        public const string K_030 = "dlssg030";
        public const string K_LEGACY = "legacy";
        public const string K_OPTI = "optiscaler";
        public const string K_UNK = "unknown";

        // 旧版（0.2.x）代理的体积窗口与 0.3.x 的粗窗口都在 Catalog —— 判据不再散在代码里

        public static string InboxRoot { get { return Path.Combine(Program.DataDir, "_inbox"); } }

        // ----------------------------------------------------------------------
        //  识别
        // ----------------------------------------------------------------------

        // input 可以是 .zip，也可以是一个已解压的目录（作者有时直接给文件夹）
        public static PackProbe Inspect(string input, Action<string> log)
        {
            PackProbe p = new PackProbe();
            p.Input = input == null ? "" : input;
            if (p.Input.Length == 0) { p.Error = "空路径"; return p; }

            string root = p.Input;
            try
            {
                if (File.Exists(p.Input))
                {
                if (!LooksLikeZip(p.Input)) { p.Error = "只认 .zip 压缩包（这个文件不像 zip）"; return p; }
                p.TempDir = NewInboxDir();
                Unzip(p.Input, p.TempDir);
                root = p.TempDir;
                }
                else if (!Directory.Exists(p.Input)) { p.Error = "路径不存在"; return p; }

                Collect(root, p);
                Classify(p);
            }
            catch (Exception ex)
            {
                p.Error = ex.GetType().Name + "：" + ex.Message;
            }
            return p;
        }

        // 解压目标目录。必须**唯一**：一次能拖进好几个 zip（窗口支持多选），
    //  同一秒里建两个同名目录时 Directory.CreateDirectory 不报错，于是两个包的文件混在
    //  一个目录里 → 识别直接串档（2026-09-18 探针抓到：0.3.0 的包被判成 0.3.2 档）。
    static string NewInboxDir()
    {
        string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string d = Path.Combine(InboxRoot, ts);
        int i = 1;
        while (Directory.Exists(d)) d = Path.Combine(InboxRoot, ts + "-" + (i++).ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    // zip 头是 "PK\x03\x04"。后缀也看一眼 —— 有些下载站给的是 .zip 但内容是别的。
        static bool LooksLikeZip(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Length < 4) return false;
                    int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                    return b0 == 0x50 && b1 == 0x4B;
                }
            }
            catch { return false; }
        }

        // 解压。自己走 ZipArchive 而不是 ZipFile.ExtractToDirectory，两个原因：
        //  ① 要挡 zip-slip（压缩包里的 ../../ 能把文件写到包外）；
        //  ② 要自己建目录 —— 有些包不写目录条目，靠条目名里的路径。
        static void Unzip(string zipPath, string dst)
        {
            string full = Path.GetFullPath(dst);
            if (!full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                full += Path.DirectorySeparatorChar;
            using (FileStream fs = File.OpenRead(zipPath))
            using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in za.Entries)
                {
                    if (e.Name.Length == 0) continue;                        // 目录条目
                    string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string outPath = Path.GetFullPath(Path.Combine(dst, rel));
                    if (!outPath.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("压缩包里有越界路径（zip-slip），已中止：" + e.FullName);
                    string dir = Path.GetDirectoryName(outPath);
                    if (dir != null && dir.Length > 0) Directory.CreateDirectory(dir);
                    using (Stream src = e.Open())
                    using (FileStream outFs = File.Create(outPath))
                        src.CopyTo(outFs);
                }
            }
        }

        // 递归收集。不用 SearchOption.AllDirectories —— 遇到没权限的子目录会整棵树抛异常。
        static void Collect(string dir, PackProbe p)
        {
            List<string> files;
            try { files = new List<string>(Directory.GetFiles(dir)); }
            catch { return; }
            foreach (string f in files)
            {
                PackItem it = new PackItem();
                it.Src = f;
                it.Name = Path.GetFileName(f).ToLowerInvariant();
                try { it.Size = new FileInfo(f).Length; } catch { continue; }
                p.Items.Add(it);
            }
            List<string> subs;
            try { subs = new List<string>(Directory.GetDirectories(dir)); }
            catch { return; }
            foreach (string s in subs)
            {
                // 不要把上次解压的临时产物再吃一遍
                if (p.TempDir.Length == 0 && Path.GetFullPath(s).StartsWith(Path.GetFullPath(InboxRoot), StringComparison.OrdinalIgnoreCase))
                    continue;
                Collect(s, p);
            }
        }

        // 角色 + 套别 + 版本档
        static void Classify(PackProbe p)
        {
            PackItem biggest = null;      // 体积最大的入口代理 —— 版本的判据
            foreach (PackItem it in p.Items)
            {
                if (it.Name == "dlssg_sm86.ini") { it.Role = "ini"; continue; }
                if (it.Name == "nvngx_dlss.dll" || it.Name == "nvngx_dlssg.dll")
                {
                    it.Role = "runtime";
                    it.Cert = ProxySign.Of(it.Src);
                    continue;
                }
                if (IsOptiName(it.Name)) { it.Role = "opti"; continue; }
                if (IsEntryName(it.Name) && LooksLikeProxy(it.Size))
                {
                    it.Role = it.Name == "version.dll" ? "entry" : "alt";
                    it.Cert = ProxySign.Of(it.Src);
                    if (biggest == null || it.Size > biggest.Size) biggest = it;
                    continue;
                }
                it.Role = "other";
            }

            bool hasIni = p.Count("ini") > 0;
            bool hasRunt = p.Count("runtime") > 0;
            bool hasOpti = p.Count("opti") > 0;
            int nEntry = p.Count("entry") + p.Count("alt");

            // 套别判定。顺序有讲究：
            //  ① dlssg_sm86.ini 是 0.3.x 的唯一标志，有它就是 0.3.x（哪怕同捆了别的东西）；
            //  ② 没有 ini 但有 OptiScaler 特征 → 方案 A；
            //  ③ 只有代理、体积落在旧版窗口 → 旧版；
            //  ④ 有代理没 ini 但体积是 0.3.x 档 → 0.3.x 的不完整包（只更新代理的场景）。
            if (hasIni) p.Kind = K_030;
            else if (hasOpti) p.Kind = K_OPTI;
            else if (biggest != null && Catalog.IsLegacySize(biggest.Size)) p.Kind = K_LEGACY;
            else if (nEntry > 0 && biggest != null) p.Kind = K_030;
            else p.Kind = K_UNK;

            if (nEntry == 0 && p.Kind != K_OPTI)
                p.Notes.Add("没找到入口代理（体积应落在 16–20 MB 或 28–34 MB 区间）；体积对不上的一律不当代理，避免误判游戏自带的 dll");

            // 版本档：一律按实测体积说，包名里的版本只作提示
            if (p.Kind == K_030)
            {
                if (biggest != null)
                {
                    //  标签由档表算出来：细档认得到就报细档，认不到只报粗档，
                    //  两个都不在就明说"不在任何已知档" —— 过去这里是 ternary 里写死的两句话，
                    //  换一版就要回来改一次（并且和 Dlssg030 的窗口各写一套）。
                    double mb = Math.Round(biggest.Size / 1048576.0, 2);
                    string band = Dlssg030.CodeOf(biggest.Size);
                    string coarse = band.Length > 0 ? "" : Dlssg030.CoarseCodeOf(biggest.Size);
                    string label = band.Length > 0 ? band + " 档"
                               : (coarse.Length > 0 ? coarse : "不在任何已知档");
                    p.VerBasis = "入口代理 " + biggest.Name + " = " + mb + " MB → " + label;
                }
                string iniPath = FirstSrc(p, "ini");
                if (iniPath != null)
                {
                    string ini = ReadHead(iniPath, 8192);
                    if (ini.IndexOf("SM75", StringComparison.OrdinalIgnoreCase) >= 0
                        || ini.IndexOf("Turing", StringComparison.OrdinalIgnoreCase) >= 0)
                        p.VerBasis += " · INI 含 SM75/Turing（20 系支持，0.3.2 起）";
                    else if (ini.IndexOf("Optimized", StringComparison.OrdinalIgnoreCase) >= 0)
                        p.VerBasis += " · INI " + new FileInfo(iniPath).Length + " B（各档出厂 INI：" + Catalog.IniSizesText() + "）";
                }
            }
            else if (p.Kind == K_LEGACY && biggest != null)
            {
                p.VerBasis = "入口代理 " + biggest.Name + " = " + Math.Round(biggest.Size / 1048576.0, 2) + " MB → 旧版 0.2.x 档";
            }

            // 主入口：version.dll 优先（作者默认），否则挑体积最大的那个
            foreach (PackItem it in p.Items)
                if (it.Role == "entry") { p.Entry = it.Name; break; }
            if (p.Entry.Length == 0 && biggest != null) p.Entry = biggest.Name;

            p.Ver = GuessVer(p.Input);
        }

        // 入口名单只此一份（Plan.EntryNames，7 个含历史遗留的 winhttp）。
        //  漏掉一个名字，那个残留就会被当成游戏自带文件放过。
        static bool IsEntryName(string name)
        {
            foreach (string n in Plan.AllEntryNames)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool IsOptiName(string name)
        {
            return name == "optiscaler.ini" || name == "optiscaler.dll"
                || name == "fakenvapi.dll" || name == "fakenvapi.ini"
                || name.StartsWith("libxess", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("dlssg_to_fsr3", StringComparison.OrdinalIgnoreCase);
        }

        // 0.3.x 的两个窗口 + 旧版窗口
        static bool LooksLikeProxy(long n)
        {
            return Dlssg030.IsProxySize(n) || Catalog.IsLegacySize(n);
        }

        static string FirstSrc(PackProbe p, string role)
        {
            foreach (PackItem it in p.Items) if (it.Role == role) return it.Src;
            return null;
        }

        static string ReadHead(string path, int max)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    int want = (int)Math.Min((long)max, fs.Length);
                    byte[] buf = new byte[want];
                    int n = fs.Read(buf, 0, want);
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch { return ""; }
        }

        // 从路径里揪一个 x.y.z 当版本号显示。只是显示用 —— 判据永远是体积。
        static string GuessVer(string input)
        {
            try
            {
                Match m = Regex.Match(input, @"(\d+\.\d+\.\d+)");
                return m.Success ? m.Value : "";
            }
            catch { return ""; }
        }

        // ----------------------------------------------------------------------
        //  签名核验：分级，不是硬拦
        // ----------------------------------------------------------------------
        //  白名单命中（作者证书 / NVIDIA 官方）→ 直接放行，不打扰。
        //  未签名 / 非白名单证书 → 这里返回非空，**调用方（UI）必须先弹一次二次确认**。
        //  为什么不硬拦：用户有权换入口、用别人的修改版 —— 工具的职责是把"这不是作者那份"
        //  说清楚并留痕，不是替用户禁止。
        public static List<PackItem> NeedsTrustAsk(PackProbe p)
        {
            List<PackItem> bad = new List<PackItem>();
            if (p == null) return bad;
            foreach (PackItem it in p.Items)
            {
                if (it.Role != "entry" && it.Role != "alt") continue;
                if (it.Cert != null && it.Cert.Ok) continue;
                bad.Add(it);
            }
            return bad;
        }

        // ----------------------------------------------------------------------
        //  归位
        // ----------------------------------------------------------------------

        public static string Import(PackProbe p, Action<string> log)
        {
            if (p.Error.Length > 0) return "识别失败：" + p.Error;
            if (p.Kind == K_OPTI)
                return "OptiScaler（方案 A）的包不支持拖入导入：它的目录里有 OptiScaler\\ 子目录与你自己的调参，"
                     + "而方案 A 的部署逻辑按绝区零 / 鸣潮分别取件，自动归位反而更容易搞错。请继续手工替换。";
            if (p.Kind == K_LEGACY)
                return "这是旧版 dlssg_for_sm86（0.2.x）的包，本工具已不带这条路径，未做任何改动。";
            if (p.Kind != K_030)
                return "认不出这个包属于哪套方案（既没有 dlssg_sm86.ini，入口体积也不在已知窗口内），未做任何改动。";
            if (!p.Ready)
                return "包里没有可归位的文件（需要 入口代理 或 dlssg_sm86.ini），未做任何改动。";

            string backup = null;
            try
            {
                backup = BackupPack();
                if (backup != null && log != null) log("  旧资源包已备份 → " + backup);

                int placed = 0;
                List<PackItem> done = new List<PackItem>();
                foreach (PackItem it in p.Items)
                {
                    string sub = null;
                    if (it.Role == "entry" || it.Role == "ini" || it.Role == "runtime") sub = "common";
                    else if (it.Role == "alt") sub = "alts";
                    if (sub == null) continue;
                    string d = Path.Combine(Dlssg030.PackRoot, sub);
                    Directory.CreateDirectory(d);
                    string dst = Path.Combine(d, it.Name);
                    File.Copy(it.Src, dst, true);
                    it.Sha = Sha16(dst);
                    done.Add(it);
                    placed++;
                }

                string lack = PackLack();
                WriteLedger(p, done);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("已更新资源包 → " + Dlssg030.PackRoot);
                sb.AppendLine("  落盘 " + placed + " 个文件"
                    + (backup == null ? "（原资源包本来就是空的，没有可备份的旧件）" : "，旧件在 " + backup));
                if (lack.Length > 0) sb.AppendLine("  ⚠ 归位后仍缺：" + lack);
                else sb.AppendLine("  资源包完整（common 4 件 + alts 有备选入口）");
                int skipped = p.Count("other");
                if (skipped > 0) sb.AppendLine("  跳过 " + skipped + " 个非插件文件（README / 说明文本等）");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "归位失败（未破坏原资源包）：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        // 旧资源包整包备份到 _backup\_pack-<时间戳>\
        static string BackupPack()
        {
            string bk = Path.Combine(Dlssg030.BackupRoot, "_pack-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int n = 0;
            foreach (string sub in new string[] { "common", "alts" })
            {
                string src = Path.Combine(Dlssg030.PackRoot, sub);
                if (!Directory.Exists(src)) continue;
                foreach (string f in Directory.GetFiles(src))
                {
                    Directory.CreateDirectory(bk);
                    File.Copy(f, Path.Combine(bk, sub + "_" + Path.GetFileName(f)), true);
                    n++;
                }
            }
            return n > 0 ? bk : null;
        }

        // 归位后还缺什么（空串 = 齐）
        static string PackLack()
        {
            List<string> lack = new List<string>();
            foreach (string n in new string[] { "version.dll", "dlssg_sm86.ini", "nvngx_dlss.dll", "nvngx_dlssg.dll" })
                if (!File.Exists(Path.Combine(Dlssg030.CommonDir, n))) lack.Add(n);
            int alts = 0;
            try { alts = Directory.GetFiles(Dlssg030.AltsDir, "*.dll").Length; } catch { }
            if (alts == 0) lack.Add("alts\\*.dll");
            return string.Join("、", lack.ToArray());
        }

        // 导入账本。原样记下"这批文件是什么时候从哪来的、每个体积多少、sha 多少"，
        //  下次再拖同一个包能一眼看出没有变化。写完立刻读回校验 ——
        //  config.json 被写坏的教训（2026-09-11）在前，宁可多一步。
        static void WriteLedger(PackProbe p, List<PackItem> placed)
        {
            Dictionary<string, object> rec = new Dictionary<string, object>();
            rec["source"] = p.Input;
            rec["importedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            rec["kind"] = p.Kind;
            rec["ver"] = p.Ver;
            rec["verBasis"] = p.VerBasis;
            Dictionary<string, object> files = new Dictionary<string, object>();
            foreach (PackItem it in placed)
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["size"] = it.Size;
                o["sha256"] = it.Sha;
                o["role"] = it.Role;
                o["signer"] = it.Cert == null ? "" : it.Cert.Thumb;
                o["signVerdict"] = it.Cert == null ? "" : it.Cert.Verdict;
                files[it.Name] = o;
            }
            rec["files"] = files;
            string text = new JavaScriptSerializer().Serialize(rec);
            string path = Path.Combine(Dlssg030.PackRoot, "pack.json");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));   // 读回校验
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

        // 账本的只读入口。原来只有 WriteLedger 没有读者 —— 记了就没人看，等于没记
        //  （2026-09-21：本机 pack 是手工换的，根本没有 pack.json，界面却显示得像有依据）。
        //  缺失 / 形状不对 / 读不动一律返回 null，由调用方如实说"没有账本"，不猜。
        public static PackLedger ReadLedger()
        {
            try
            {
                string path = Path.Combine(Dlssg030.PackRoot, "pack.json");
                if (!File.Exists(path)) return null;
                object raw = new JavaScriptSerializer().DeserializeObject(
                    File.ReadAllText(path, Encoding.UTF8));
                Dictionary<string, object> d = raw as Dictionary<string, object>;
                if (d == null) return null;
                PackLedger L = new PackLedger();
                L.ImportedAt = LedgerStr(d, "importedAt");
                L.Source = LedgerStr(d, "source");
                L.Ver = LedgerStr(d, "ver");
                L.VerBasis = LedgerStr(d, "verBasis");
                L.Kind = LedgerStr(d, "kind");
                object filesRaw;
                Dictionary<string, object> files =
                    d.TryGetValue("files", out filesRaw) ? filesRaw as Dictionary<string, object> : null;
                if (files != null)
                    foreach (var kv in files)
                    {
                        Dictionary<string, object> fo = kv.Value as Dictionary<string, object>;
                        if (fo == null) continue;
                        LedgerFile lf = new LedgerFile();
                        lf.Size = LedgerLong(fo, "size");
                        lf.Sha = LedgerStr(fo, "sha256");
                        lf.Role = LedgerStr(fo, "role");
                        lf.Signer = LedgerStr(fo, "signer");
                        lf.SignVerdict = LedgerStr(fo, "signVerdict");
                        L.Files[kv.Key.ToLowerInvariant()] = lf;
                    }
                return L;
            }
            catch { return null; }
        }

        static string LedgerStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return v.ToString();
        }

        // JavaScriptSerializer 把 JSON 数字解成 int 或 long，体积必须按 long 取
        static long LedgerLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }

        // ----------------------------------------------------------------------
        //  更新部署
        // ----------------------------------------------------------------------

        // 当前装了本方案的游戏（更新部署的默认目标）
        public static List<DlssgGame> Installed(List<DlssgGame> games)
        {
            List<DlssgGame> r = new List<DlssgGame>();
            if (games == null) return r;
            foreach (DlssgGame g in games)
            {
                if (g == null || g.Dir == null || g.Dir.Length == 0) continue;
                try
                {
                    if (Dlssg030.IsInstalled(g.Dir) || Dlssg030.GenericInstalled(g.Dir)) r.Add(g);
                }
                catch { }
            }
            return r;
        }

        // 把新资源重抄到每个目标。必须走 force —— 入口已在我们这套时 SwitchTo 默认会短路成
        //  "无需切换"，那样资源包更新了游戏里还是旧的。
        public static string DeployTo(List<DlssgGame> targets, Action<string> log)
        {
            if (targets == null || targets.Count == 0) return "没有需要重新部署的游戏（都还是原生状态）";
            int ok = 0, bad = 0;
            foreach (DlssgGame g in targets)
            {
                string r;
                try { r = Plan.SwitchTo(g.Dir, Plan.D030, true); }
                catch (Exception ex) { r = "异常：" + ex.GetType().Name + "：" + ex.Message; }
                if (r.IndexOf("已切换到", StringComparison.Ordinal) >= 0) ok++; else bad++;
                if (log != null) log("  " + g.Title + "：" + r.Replace("\n", "\n      "));
            }
            return "重新部署 " + targets.Count + " 个游戏（成功 " + ok + (bad > 0 ? "，未成功 " + bad : "") + "），重启游戏生效";
        }
    }

    // ==========================================================================
    //  Catalog —— 判据数据集中一处（代理版本档 / 体积窗口 / 入口名单 / 签名白名单）
    // ==========================================================================
    //  为什么外置：0.3.2 → 0.3.5 换一次包，同一个"代理版本"事实被抄在 6 个地方
    //  （Dlssg.cs 的常量与体积表、Ui.cs 的文案、本文件的版本档标签、三份探针、探针 runner）。
    //  抄得多必然对不上 —— 换包的真正成本从来不是量体积，是改这一堆还怕漏。
    //  现在判据集中在数据目录的 catalog.json；缺失或读坏时退回内置默认
    //  （默认值与换包前的常量逐值相同，所以不放文件时行为一字不变）。
    //
    //  ⚠ 这里只放"判据"，不放"结论"：Ver 说的是"这次随工具发的是哪一版"，
    //    磁盘上实际是哪一版由 Dlssg030.PackActual() 量出来。两者不一致必须报警 ——
    //    绝不能因为 catalog 可改就拿它当磁盘事实。
    // ==========================================================================
    public class ProxyBand
    {
        public string Id = "";
        public long Min, Max;         // 体积判据：[Min, Max)
        public long IniSize;          // 出厂 INI 体积，0 = 该版未记录
        public string Note = "";
        public ProxyBand() { }
        public ProxyBand(string id, long min, long max, long ini, string note)
        { Id = id; Min = min; Max = max; IniSize = ini; Note = note; }
    }

    public class SizeWin
    {
        public long Min, Max;         // [Min, Max)
        public SizeWin() { }
        public SizeWin(long min, long max) { Min = min; Max = max; }
    }

    // 一条"只在某些版本成立"的上游实测结论（见 Dlssg030.Knowledge 的用法）
    public class KnowNote
    {
        public string Id = "";
        public string Since = "";     // 从哪个代理版本起成立（含）；空 = 一直成立
        public string Until = "";     // 到哪个版本为止（含）；空 = 至今
        public string Text = "";
        public string Topic = "";
    }

    public static class Catalog
    {
        public const string FileHint = "catalog.json";

        // ---- 内置默认（= 换包前散在各处的常量，逐值照抄，不是重新估的）----
        const string DefVer = "0.3.5";
        const long MB = 1024L * 1024L;
        //  方案 A（OptiScaler）入口的体积下沿。0.3.x 的粗窗口已经覆盖了 28–34 MB，
        //  所以"大于这个数且不在本方案窗口里"才是方案 A —— 与 OwnerOf 的判据顺序一致。
        const long DefOptiMin = 20 * MB;
        static string[] DefEntries() { return new string[] {
            "dxgi.dll", "d3d12.dll", "version.dll", "dinput8.dll", "winmm.dll", "winhttp.dll", "dbghelp.dll" }; }
        static SizeWin DefLegacy() { return new SizeWin(14 * MB, 16 * MB); }        // 旧版 0.2.x 入口
        static SizeWin DefSource() { return new SizeWin(15500000L, 15800000L); }    // 0.2.x 下载源件
        static SizeWin[] DefWins() { return new SizeWin[] {
            new SizeWin(16 * MB, 20 * MB), new SizeWin(28 * MB, 34 * MB) }; }
        static ProxyBand[] DefBands() { return new ProxyBand[] {
            new ProxyBand("0.3.0 / 0.3.1", 16 * MB, 20 * MB, 2099, "首个公开版"),
            new ProxyBand("0.3.2", 29974816L, 29993761L, 3548, "20+30 系合并包；重写 310.9 的 26 个推理内核"),
            new ProxyBand("0.3.5", 30021408L, 30039841L, 3571, "修 0.3.0 起「重建帧生成特性后用错优化内核」（#561）"),
        }; }
        // ---- 上游实测结论（"只在某些代理版本成立"的事实），散在 UI 与文档里必然过期 ----
        //  每条写清适用区间；Knowledge(id) 按当前 Ver 挑那一条成立的。
        //  搬进来的动因（2026-09-21）：绝区零那句从"0.3.x 不做伪装"改成"0.3.2 及以前不做伪装"
        //  是手工改 UI 文案改的 —— 同一个结论在功能缺口与路线图.md 里还有四处，上游一改就要漏。
        static KnowNote[] DefKnow()
        {
            return new KnowNote[] {
                new KnowNote { Id = "zzz-spoof", Topic = "绝区零帧生成菜单与显卡名伪装", Until = "0.3.2",
                    Text = "0.3.2 及以前不做显卡名伪装（2026-09-16 实测）：撤掉方案 A 之后，设置里那个「帧生成」"
                         + "选项很可能一起消失，属于预期行为，不是装坏了" },
                new KnowNote { Id = "zzz-spoof", Topic = "绝区零帧生成菜单与显卡名伪装", Since = "0.3.3",
                    Text = "0.3.3 起代理会自己改写游戏 / Streamline 看到的显卡架构（对外报 RTX 50），"
                         + "理论上可能让菜单里出现帧生成选项 —— 本工具未实测，仍不要导 .reg" },
                new KnowNote { Id = "sm75", Topic = "20 系支持", Since = "0.3.2",
                    Text = "0.3.2 起把 20 系（SM75 / Turing）并进同一个包，与 30 系共用同一份出厂 INI" },
                new KnowNote { Id = "nnr", Topic = "5X / 6X 的运行库门槛",
                    Text = "5X / 6X 只有 310.9 运行库才认；旧版 0.2.x / 310.1 的运行库上限是 4X，选了不生效" },
                new KnowNote { Id = "upgrade-path", Topic = "别按版本号顺序升级",
                    Text = "别按版本号顺序升级：0.3.3 / 0.3.4 本身就是坑 —— 0.3.3 让 NVIDIA 自家 NGX 模型"
                         + "也吃到架构改写 → GPU 挂起（上游 #535 / #538 / #540 / #542），0.3.4 才修；"
                         + "0.3.5 修的是 0.3.0 起的内核重建缺陷（#561）。可升级路径只有 0.3.2 或 0.3.5+，"
                         + "中间两版要跳过" },
            };
        }

        const string DefThumbUp = "85BA66762F851E49148D706915D09026281418E6";
        const string DefThumbNv = "7B7B0B6697AFB438CF6F65A155F00E86676FB186";

        static string _ver;
        static ProxyBand[] _bands;
        static SizeWin[] _wins;
        static SizeWin _legacy, _srcWin;
        static string[] _entries;
        static string _up, _nv;
        static KnowNote[] _know;
        static long _optiMin = DefOptiMin;
        static string _origin = "内置默认";
        static string _err = "";

        public static string FilePath { get { return Path.Combine(Program.DataDir, FileHint); } }

        public static void Reload() { lock (typeof(Catalog)) { _ver = null; } }

        static void Ensure()
        {
            if (_ver != null) return;
            lock (typeof(Catalog))
            {
                if (_ver != null) return;
                Load();
            }
        }

        // 先全部落到局部量，成功后一次性提交 —— 半途而废的 JSON 绝不能留下"一半新一半旧"的判据
        static void Load()
        {
            string ver = DefVer;
            ProxyBand[] bands = DefBands();
            SizeWin[] wins = DefWins();
            SizeWin legacy = DefLegacy(), srcw = DefSource();
            string[] entries = DefEntries();
            string up = DefThumbUp, nv = DefThumbNv;
            long optiMin = DefOptiMin;
            KnowNote[] know = DefKnow();
            string origin = "内置默认", err = "";
            try
            {
                string p = FilePath;
                if (File.Exists(p))
                {
                    object raw = new JavaScriptSerializer().DeserializeObject(
                        File.ReadAllText(p, Encoding.UTF8));
                    Dictionary<string, object> d = raw as Dictionary<string, object>;
                    if (d == null) { err = "顶层不是 JSON 对象"; }
                    else
                    {
                        Dictionary<string, object> pr = Obj(d, "proxy") as Dictionary<string, object>;
                        if (pr != null)
                        {
                            string v = Str(pr, "ver");
                            if (v.Length > 0) ver = v;
                            ProxyBand[] pb = ParseBands(Obj(pr, "bands"));
                            if (pb != null) bands = pb;
                            SizeWin[] pw = ParseWins(Obj(pr, "windows"));
                            if (pw != null) wins = pw;
                            SizeWin l = ParseWin(Obj(pr, "legacy"));
                            if (l != null) legacy = l;
                            SizeWin s = ParseWin(Obj(pr, "sourceSize"));
                            if (s != null) srcw = s;
                            string[] en = ParseStrs(Obj(pr, "entries"));
                            if (en != null && en.Length > 0) entries = en;
                            long om = Long(pr, "optiMin", 0);
                            if (om > 0) optiMin = om;
                            Dictionary<string, object> sg = Obj(pr, "signs") as Dictionary<string, object>;
                            if (sg != null)
                            {
                                if (Str(sg, "upstream").Length > 0) up = Str(sg, "upstream").ToUpperInvariant();
                                if (Str(sg, "nvidia").Length > 0) nv = Str(sg, "nvidia").ToUpperInvariant();
                            }
                        }
                        KnowNote[] kn = ParseKnow(Obj(d, "knowledge"));
                        if (kn != null) know = kn;
                        origin = p;
                    }
                }
            }
            catch (Exception ex) { err = ex.GetType().Name + "：" + ex.Message; origin = "内置默认（判据文件读坏了）"; }

            // 读坏时退回默认：但 err 必须留着，让界面/日志能说出来 —— 静默回落是最难查的那种错
            if (err.Length > 0)
            {
                ver = DefVer; bands = DefBands(); wins = DefWins();
                legacy = DefLegacy(); srcw = DefSource(); entries = DefEntries();
                up = DefThumbUp; nv = DefThumbNv; know = DefKnow(); optiMin = DefOptiMin;
            }
            _ver = ver; _bands = bands; _wins = wins; _legacy = legacy; _srcWin = srcw;
            _entries = entries; _up = up; _nv = nv; _know = know; _origin = origin; _err = err;
            _optiMin = optiMin;
        }

        //  ⚠ 一律走 TryGetValue：.NET 4 的 Dictionary 索引器缺键是**抛异常**，不是给 null。
        //    用索引器的话 JSON 少写一个键就整段抛，被外层 catch 兜成"退回内置默认" ——
        //    判据文件明明放了却说没放，是查不出来的那种错（2026-09-21 catalog_probe 抓到）。
        static object Obj(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v)) return null;
            return v;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v = Obj(d, k);
            if (v == null) return "";
            return v.ToString();
        }
        static long Long(Dictionary<string, object> d, string k, long def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt64(v); } catch { return def; }
        }
        static string[] ParseStrs(object o)
        {
            var a = o as Array;
            if (a == null) return null;
            var r = new List<string>();
            foreach (object x in a) if (x != null) r.Add(x.ToString());
            return r.ToArray();
        }
        static SizeWin ParseWin(object o)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;
            long mn = Long(d, "min", 0), mx = Long(d, "max", 0);
            if (mx <= mn) return null;
            return new SizeWin(mn, mx);
        }
        static SizeWin[] ParseWins(object o)
        {
            var a = o as Array;
            if (a == null) return null;
            var r = new List<SizeWin>();
            foreach (object x in a) { SizeWin w = ParseWin(x); if (w != null) r.Add(w); }
            return r.Count > 0 ? r.ToArray() : null;
        }
        static ProxyBand[] ParseBands(object o)
        {
            var a = o as Array;
            if (a == null) return null;
            var r = new List<ProxyBand>();
            foreach (object x in a)
            {
                Dictionary<string, object> d = x as Dictionary<string, object>;
                if (d == null) continue;
                long mn = Long(d, "min", 0), mx = Long(d, "max", 0);
                if (mx <= mn) continue;
                r.Add(new ProxyBand(Str(d, "id"), mn, mx, Long(d, "ini", 0), Str(d, "note")));
            }
            return r.Count > 0 ? r.ToArray() : null;
        }
        static KnowNote[] ParseKnow(object o)
        {
            var a = o as Array;
            if (a == null) return null;
            var r = new List<KnowNote>();
            foreach (object x in a)
            {
                Dictionary<string, object> d = x as Dictionary<string, object>;
                if (d == null) continue;
                KnowNote n = new KnowNote();
                n.Id = Str(d, "id"); n.Since = Str(d, "since"); n.Until = Str(d, "until");
                n.Text = Str(d, "text"); n.Topic = Str(d, "topic");
                if (n.Id.Length > 0 && n.Text.Length > 0) r.Add(n);
            }
            return r.ToArray();
        }

        // ---- 对外 ----
        public static string Ver { get { Ensure(); return _ver; } }
        public static ProxyBand[] Bands { get { Ensure(); return _bands; } }
        public static string[] EntryNames { get { Ensure(); return _entries; } }
        public static string ThumbUpstream { get { Ensure(); return _up; } }
        public static string ThumbNvidia { get { Ensure(); return _nv; } }
        public static SizeWin LegacyWin { get { Ensure(); return _legacy; } }
        public static SizeWin SourceWin { get { Ensure(); return _srcWin; } }
        //  体积大于它、又不在本方案窗口里 → 判成方案 A（OptiScaler）。Plan.OwnerOfCore 用。
        public static long OptiMin { get { Ensure(); return _optiMin; } }
        public static KnowNote[] KnowledgeNotes { get { Ensure(); return _know; } }
        public static string Origin { get { Ensure(); return _origin; } }
        public static string Error { get { Ensure(); return _err; } }

        // 是不是本方案的入口代理 —— 只看粗窗口，且两端都是开区间（照搬搬之前的
        //  n > ProxyMin030 && n < ProxyMax030，搬判据不顺手改语义）。
        //  ⚠ 细档（bands）不参与这里的判定：它是"是哪一版"的第二层。
        //    给新版本加档时必须同时把粗窗口（windows）拉开，否则体积这层认不下、代号那层白记。
        public static bool IsProxySize(long n)
        {
            Ensure();
            foreach (SizeWin w in _wins) if (n > w.Min && n < w.Max) return true;
            return false;
        }
        public static bool IsLegacySize(long n)
        {
            Ensure();
            return n >= _legacy.Min && n < _legacy.Max;
        }
        public static bool IsSourceSize(long n)
        {
            Ensure();
            return n >= _srcWin.Min && n < _srcWin.Max;
        }
        public static string CodeOf(long n)
        {
            Ensure();
            foreach (ProxyBand b in _bands) if (n >= b.Min && n < b.Max) return b.Id;
            return "";
        }
        public static string CoarseCodeOf(long n)
        {
            Ensure();
            if (!IsProxySize(n)) return "";
            // 粗档边界就是粗窗口第二段的上沿 —— 与内置默认里"28 MB 档"同义
            return n >= _wins[Math.Max(0, _wins.Length - 1)].Min ? "0.3.2 及以后（定不到小版本）" : "0.3.0 / 0.3.1";
        }
        // 各档出厂 INI 体积一句话清单 —— 给识别报告的提示用。
        //  原来这句话抄在 Pack.Classify 里（"0.3.0 是 2099；0.3.2 后段与 0.3.5 是 3571"），
        //  换一版就要回来改一次；现在它从档表算出来。
        public static string IniSizesText()
        {
            Ensure();
            var sb = new StringBuilder();
            foreach (ProxyBand b in _bands)
            {
                if (b.IniSize <= 0) continue;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(b.Id).Append(" 是 ").Append(b.IniSize);
            }
            return sb.ToString();
        }
        // 版本号比较（0.3.10 要大于 0.3.9 —— 按字符串比会反过来）
        public static int VerCmp(string a, string b)
        {
            string[] xa = (a ?? "").Split('.'), xb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(xa.Length, xb.Length); i++)
            {
                long va = 0, vb = 0;
                if (i < xa.Length) long.TryParse(xa[i], out va);
                if (i < xb.Length) long.TryParse(xb[i], out vb);
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }

        // 挑出对**当前代理版本**成立的那一条结论；没有适用的（版本落在两条的缝里 / 判据被改坏）
        // 就返回空串，由调用方决定退路 —— 宁可少说一句，也不要说错一句。
        public static string Knowledge(string id)
        {
            Ensure();
            string ver = _ver;
            KnowNote fallback = null;
            foreach (KnowNote n in _know)
            {
                if (!string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Since.Length == 0 && n.Until.Length == 0) fallback = n;   // 无条件成立
                bool ok = true;
                if (n.Since.Length > 0 && VerCmp(ver, n.Since) < 0) ok = false;
                if (n.Until.Length > 0 && VerCmp(ver, n.Until) > 0) ok = false;
                if (ok) return n.Text;
            }
            return fallback == null ? "" : fallback.Text;
        }

        public static string NewestBandId()
        {
            Ensure();
            string best = "";
            long at = long.MinValue;
            foreach (ProxyBand b in _bands) if (b.Min > at) { at = b.Min; best = b.Id; }
            return best;
        }

        // 把内置默认原样吐成一份 JSON —— 用户要"改判据"时拿它当模板，不用猜字段。
        //  写完 Reload 一次就能自证形状对得上（pack_probe 里断言这一点）。
        public static string DefaultJson()
        {
            var wins = new List<object>();
            foreach (SizeWin w in DefWins()) wins.Add(WinObj(w.Min, w.Max));
            var bands = new List<object>();
            foreach (ProxyBand b in DefBands()) bands.Add(BandObj(b.Id, b.Min, b.Max, b.IniSize, b.Note));
            var pr = new Dictionary<string, object>();
            pr["ver"] = DefVer;
            pr["optiMin"] = DefOptiMin;
            pr["windows"] = wins.ToArray();
            pr["bands"] = bands.ToArray();
            pr["legacy"] = WinObj(DefLegacy().Min, DefLegacy().Max);
            pr["sourceSize"] = WinObj(DefSource().Min, DefSource().Max);
            pr["entries"] = DefEntries();
            var signs = new Dictionary<string, object>();
            signs["upstream"] = DefThumbUp;
            signs["nvidia"] = DefThumbNv;
            pr["signs"] = signs;
            var top = new Dictionary<string, object>();
            top["schema"] = 1;
            top["_说明"] = "代理判据：换一次包只改这一份。bands 是「体积 → 哪一版」的细档（闭区间），"
                        + "windows 是「是不是本方案」的粗窗口（开区间），legacy/sourceSize 是旧版 0.2.x 的"
                        + "入口窗口与下载源件窗口，signs 是签名白名单指纹。删除本文件即回到内置默认。";
            top["proxy"] = pr;
            return new JavaScriptSerializer().Serialize(top);
        }

        static Dictionary<string, object> WinObj(long min, long max)
        {
            var d = new Dictionary<string, object>();
            d["min"] = min; d["max"] = max;
            return d;
        }

        static Dictionary<string, object> BandObj(string id, long min, long max, long ini, string note)
        {
            var d = new Dictionary<string, object>();
            d["id"] = id; d["min"] = min; d["max"] = max; d["ini"] = ini; d["note"] = note;
            return d;
        }

        // 判据来源一行 —— 启动时进日志。"读坏了默默用默认值"是最难查的那类故障，必须留痕。
        public static string Summary()
        {
            Ensure();
            return "判据来源：" + _origin + "（ver=" + _ver + "，版本档 " + _bands.Length
                 + " 条，粗窗口 " + _wins.Length + " 段，入口 " + _entries.Length + " 个，知识条目 "
                 + _know.Length + " 条）" + (_err.Length > 0 ? " ⚠ 读取失败已退回内置默认：" + _err : "");
        }
    }


    //  为什么要这一层：部署、换包、卸载每次都往 _backup / _removed 里堆东西，而工程里
    //  **没有任何入口**看得见这些占用（2026-09-21 实测：两个资源包合计 1.48 GB / 15 个目录，
    //  最大单份 724.9 MB）。用户想自己清也无从下手 —— 哪个目录能删、删了会不会断了回滚，
    //  全都没处问。
    //
    //  三条底线：
    //   ① 统计只读 —— 扫盘不写盘；
    //   ② 删除一律送回收站（Native.RecycleToBin），不 File.Delete；
    //   ③ 保留策略先于删除 —— 每个归属（整包 / 每个游戏）里最新那一份永不进清理候选：
    //      那一份正是 Uninstall / Plan.SwitchTo 回滚时要读回去的游戏自带件。
    // ==========================================================================
    public class JunkItem
    {
        public string Dir = "";        // 绝对路径
        public string Owner = "";      // dlssg030-pack / xess-pack
        public string Bucket = "";     // _backup / _removed
        public string Game = "";       // 目录名解析出的归属：_pack / zzz / wuwa / cp2077 …
        public DateTime When;
        public bool TimeFromName;      // 时间来自目录名（准）；否则是目录修改时间（近似）
        public long Bytes;
        public int Files;
        public bool Keep;
        public string KeepWhy = "";
    }

    public static class Junk
    {
        public const int DefaultAgeDays = 30;

        // 只读扫描。返回按体积从大到小排好、并标好保留策略的全部条目。
        public static List<JunkItem> Scan()
        {
            var all = new List<JunkItem>();
            // 路径从各自的公开属性取 —— 在这里再写一遍 "_backup" 就是第四份判据了
            ScanBucket(all, "dlssg030-pack", Dlssg030.BackupRoot);
            ScanBucket(all, "xess-pack", XeMfg.BackupRoot);
            ScanBucket(all, "xess-pack", XeMfg.RemovedRoot);
            Mark(all);
            all.Sort(delegate(JunkItem x, JunkItem y) { return y.Bytes.CompareTo(x.Bytes); });
            return all;
        }

        static void ScanBucket(List<JunkItem> all, string owner, string bucketDir)
        {
            if (bucketDir == null || !Directory.Exists(bucketDir)) return;
            string bucket = Path.GetFileName(bucketDir);
            string[] subs;
            try { subs = Directory.GetDirectories(bucketDir); } catch { return; }
            foreach (string s in subs)
            {
                JunkItem it = new JunkItem();
                it.Dir = s;
                it.Owner = owner;
                it.Bucket = bucket;
                string nm = Path.GetFileName(s);
                it.Game = GameOf(nm);
                DateTime t;
                it.TimeFromName = TryStamp(nm, out t);
                if (it.TimeFromName) it.When = t;
                else { try { it.When = Directory.GetLastWriteTime(s); } catch { it.When = DateTime.MinValue; } }
                CountTree(s, it);
                all.Add(it);
            }
        }

        // 目录名第一段就是归属：_pack-<ts> / zzz / wuwa / cp2077-0.3.0-<ts> / zzz-opti-<ts>
        static string GameOf(string name)
        {
            if (name == null || name.Length == 0) return "?";
            int i = name.IndexOf('-');
            return (i <= 0 ? name : name.Substring(0, i)).ToLowerInvariant();
        }

        // 部署/卸载/换包写出来的目录名一律带 yyyyMMdd-HHmmss；老的手工目录可能写法不同，
        //  所以两种分隔符都认，认不出来才退到目录时间。
        static readonly Regex Stamp = new Regex(@"(\d{8})[-_]?(\d{6})", RegexOptions.Compiled);
        static bool TryStamp(string name, out DateTime t)
        {
            t = DateTime.MinValue;
            try
            {
                Match m = Stamp.Match(name ?? "");
                if (!m.Success) return false;
                t = DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
                                        System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch { t = DateTime.MinValue; return false; }
        }

        static void CountTree(string dir, JunkItem it)
        {
            // 不用 SearchOption.AllDirectories —— 备份区里可能有没权限的子目录，
            //  那会让整棵树抛异常、这一项被整体跳过（体积统计凭空少一块还不报错）。
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { it.Bytes += new FileInfo(f).Length; it.Files++; } catch { }
                }
                foreach (string s in Directory.GetDirectories(dir)) CountTree(s, it);
            }
            catch { }
        }

        // 保留策略：每个 (资源包, 桶, 归属) 里最新的一份标成 Keep。
        static void Mark(List<JunkItem> all)
        {
            var newest = new Dictionary<string, JunkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (JunkItem it in all)
            {
                string key = it.Owner + "|" + it.Bucket + "|" + it.Game;
                JunkItem cur;
                if (!newest.TryGetValue(key, out cur) || it.When > cur.When) newest[key] = it;
            }
            foreach (var kv in newest)
            {
                kv.Value.Keep = true;
                kv.Value.KeepWhy = kv.Value.Owner + " 里 " + kv.Value.Game + " 最近的一份 —— 回滚要读它";
            }
        }

        public static long BytesOf(IEnumerable<JunkItem> items)
        {
            long n = 0;
            if (items == null) return 0;
            foreach (JunkItem it in items) if (it != null) n += it.Bytes;
            return n;
        }

        public static string Mb(long n) { return (n / 1048576.0).ToString("0.0") + " MB"; }

        // 不在保留策略里的条目 —— 只有这些才可能被回收（与天数无关）。
        public static List<JunkItem> Reclaimable(List<JunkItem> all)
        {
            var r = new List<JunkItem>();
            if (all == null) return r;
            foreach (JunkItem it in all) if (!it.Keep) r.Add(it);
            return r;
        }

        // 够老、且不在保留策略里的条目 —— 这才是「清理 N 天前的备份」的候选集。
        public static List<JunkItem> Aged(List<JunkItem> all, int days)
        {
            var r = new List<JunkItem>();
            if (all == null) return r;
            DateTime cut = DateTime.Now.AddDays(-days);
            foreach (JunkItem it in all) if (!it.Keep && it.When <= cut) r.Add(it);
            return r;
        }

        // 一行结论，给设置页与日志共用
        public static string Summary()
        {
            List<JunkItem> all = Scan();
            return SummaryOf(all);
        }

        public static string SummaryOf(List<JunkItem> all)
        {
            if (all == null || all.Count == 0) return "备份区没有残留（_backup / _removed 都是空的）";
            long total = BytesOf(all);
            List<JunkItem> rec = Reclaimable(all);
            List<JunkItem> aged = Aged(all, DefaultAgeDays);
            // 两个口径都得报：只报"满 30 天"会让人以为刚留下的备份永远清不掉，
            //  只报"可回收"又会让「清理 30 天前」这个按钮的射程看不出来。
            return "备份占用 " + Mb(total) + "（" + all.Count + " 项）｜ 保留策略外可回收 "
                 + Mb(BytesOf(rec)) + "（" + rec.Count + " 项），其中满 " + DefaultAgeDays + " 天 "
                 + Mb(BytesOf(aged)) + "（" + aged.Count + " 项）｜ 留着 " + Mb(total - BytesOf(rec))
                 + " 供回滚";
        }

        // 送回收站。**逐条**调 shell：一批里只要有一条失败（文件被占用 / 权限 / 回收站被关），
        //  整批一个返回码就说不清是谁的错 —— 回执必须一条条对得上目录。
        public static string Recycle(IEnumerable<JunkItem> items, IntPtr owner)
        {
            if (items == null) return "没有要清理的项";
            var body = new StringBuilder();
            int okn = 0, bad = 0;
            long freed = 0;
            foreach (JunkItem it in items)
            {
                if (it == null) continue;
                if (it.Keep)
                {
                    bad++;
                    body.AppendLine("  ✘ 已跳过（保留策略）：" + it.Dir + " —— " + it.KeepWhy);
                    continue;
                }
                if (!Directory.Exists(it.Dir))
                {
                    body.AppendLine("  · 已经不在了：" + it.Dir);
                    continue;
                }
                long sz = it.Bytes;
                int rc = Native.RecycleToBin(owner, it.Dir);
                bool gone = !Directory.Exists(it.Dir);
                if (rc == 0 && gone)
                {
                    okn++; freed += sz;
                    body.AppendLine("  ✔ 已送回收站 " + Mb(sz) + "：" + it.Dir);
                }
                else if (rc == 0)
                {
                    bad++;
                    body.AppendLine("  ? shell 说成功但目录还在（这台机器的回收站可能被禁用）：" + it.Dir);
                }
                else
                {
                    bad++;
                    body.AppendLine("  ✘ 回收失败 0x" + rc.ToString("X8") + "：" + it.Dir);
                }
            }
            var head = "清理 " + okn + " 项，释放 " + Mb(freed) + "（都在回收站，可随时还原）"
                     + (bad > 0 ? "；未成功 " + bad + " 项" : "");
            return head + "\n" + body.ToString().TrimEnd();
        }

        // 清理前的确认文本：把**具体路径**摊开给用户看，不写"确定要删除吗"这种没信息量的话。
        public static string ConfirmText(List<JunkItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("将把下面 ").Append(items.Count).Append(" 项送进回收站（不是硬删，可从回收站还原），合计 ")
              .Append(Mb(BytesOf(items))).Append("：\n\n");
            foreach (JunkItem it in items)
                sb.Append("  ").Append(Mb(it.Bytes)).Append("  ").Append(it.Dir)
                  .Append("（").Append(it.When.ToString("yyyy-MM-dd")).Append("）\n");
            sb.Append("\n保留策略内的最新一份不会出现在这里。卸载 / 换方案要回滚时读的就是那一份。");
            return sb.ToString();
        }
    }
}
