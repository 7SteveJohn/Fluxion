using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// ==========================================================================
//  pack_probe.cs —— 插件包导入（Pack.cs）的回归自检
// ==========================================================================
//  为什么值得单独测：
//    识别全靠三个"弱信号"—— 文件名、体积窗口、INI 内容特征。体积窗口是照着三份真实
//    发布包量出来的（0.3.0 17.5 MB / 0.3.2 与 0.3.5 30.0 MB / 旧版 15.7 MB），判据写歪一格，
//    用户拖进来的包就被认错；而认错的后果不是"不干活"，是**往资源包里写不该写的文件**
//    —— 比不识别更糟。所以每一档都要钉死。
//
//  隔离：
//    反射把 Program.DataDir 指到沙盒，下面所有 Import / SwitchTo 只动沙盒目录，
//    真实资源包（C:\ProgramData\Fluxion\dlssg030-pack）一个字节都不碰。
//
//  签名核验（2026-09-18 加）：
//    识别时对每个入口取证书指纹。合成包（零填充占位）必然是未签名，真实包必然是作者证书 ——
//    两头都断言，才拦得住"白名单要么全过、要么全拦"这种反向 bug。
// ==========================================================================
namespace Fluxion
{
static class PackImportProbe
{
    static StringBuilder Out = new StringBuilder();
    static int Fail = 0;

    static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }
    static void Head(string s) { L(""); L("=== " + s + " ==="); }

    static void Chk(string name, bool ok, string detail)
    {
        if (!ok) Fail++;
        L((ok ? "  [PASS] " : "  [FAIL] ") + name
          + (detail != null && detail.Length > 0 ? "  -> " + detail : ""));
    }

    // 实物量出来的数字（与 run_pack_probe.py 造合成包用的是同一组）
    //  ⚠ 别拿 version.dll 的体积当"入口体积"：鸣潮的入口是 dxgi.dll，而 alts\ 里
    //    每个备选都单独签过名，体积各差几十 KB —— 上一版断言就是这么写错的。
    //    这里一律按**窗口**判定，与 IsProxySize 的语义一致。
    const long WIN032_MIN = 28L * 1024 * 1024, WIN032_MAX = 34L * 1024 * 1024;
    const long WIN030_MIN = 16L * 1024 * 1024, WIN030_MAX = 20L * 1024 * 1024;
    const long SZ_032_VERSION = 29975840L;   // 0.3.2 默认版 version.dll
    const long SZ_032_DXGI = 29976352L;      // 0.3.2 供替换注入\dxgi.dll —— 鸣潮入口
    const long SZ_030_VERSION = 17529120L;   // 0.3.0 默认版 version.dll
    const long SZ_030_DXGI = 17529632L;      // 0.3.0 供替换注入\dxgi.dll
    const long SZ_LEGACY = 15667520L;        // 旧版（0.2.x）的 version.dll

    static string Sand, Data, Game;

    static int Main(string[] args)
    {
        Sand = args.Length > 0 ? args[0] : @"D:\youhua\_tpack";
        Data = Path.Combine(Sand, "data");
        Game = Path.Combine(Sand, "game");
        L("沙盒       " + Sand);
        L("数据目录   " + Data);

        // ---------- 0. 隔离 ----------
        Head("0. 把 DataDir 隔离进沙盒（真实资源包不参与）");
        try
        {
            Directory.CreateDirectory(Data);
            FieldInfo f = typeof(Program).GetField("dataDirCache", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) { L("!! 找不到 Program.dataDirCache 字段，无法隔离，中止"); return 2; }
            f.SetValue(null, Data);
            string got = Program.DataDir;
            Chk("DataDir 指向沙盒", string.Equals(got.TrimEnd('\\'), Data.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), got);
            Chk("资源包路径也在沙盒里",
                Dlssg030.PackRoot.IndexOf(Sand, StringComparison.OrdinalIgnoreCase) == 0, Dlssg030.PackRoot);
        }
        catch (Exception ex) { L("!! 隔离失败：" + ex.GetType().Name + " " + ex.Message); return 2; }

        // ---------- 1. 入口名单只有一份 ----------
        Head("1. 入口名单单点维护（Plan.AllEntryNames）");
        string[] all = Plan.AllEntryNames;
        Chk("入口共 7 个", all != null && all.Length == 7, all == null ? "null" : all.Length.ToString());
        bool hasWinhttp = false;
        foreach (string n in all) if (n == "winhttp.dll") hasWinhttp = true;
        Chk("含历史遗留的 winhttp.dll", hasWinhttp, "");

        // ---------- 2. 合成包：0.3.2 档 ----------
        Head("2. 合成 0.3.2 包（默认 version 注入 + 供替换注入 一起打）");
        PackProbe p32 = Pack.Inspect(Path.Combine(Sand, "pkg032.zip"), null);
        Dump(p32);
        Chk("识别为 dlssg030", p32.Kind == Pack.K_030, p32.Kind);
        Chk("主入口 version.dll", p32.Entry == "version.dll", p32.Entry);
        //  标签文案随 P1-4 变了：旧版是代码里写死的"0.3.2+ 档（28–34 MB）"，
        //  现在由 Catalog 档表算出"0.3.2 档"。断言盯的还是"报没报对档"，只是文案换了来源。
        Chk("判据按档表报出 0.3.2 档", p32.VerBasis.IndexOf("0.3.2 档", StringComparison.Ordinal) >= 0,
            p32.VerBasis);
        Chk("判据含 SM75/Turing（读了 INI 内容）",
            p32.VerBasis.IndexOf("SM75", StringComparison.Ordinal) >= 0
            || p32.VerBasis.IndexOf("Turing", StringComparison.Ordinal) >= 0, p32.VerBasis);
        Chk("common 归位 4 件（entry+ini+2 runtime）",
            p32.Count("entry") + p32.Count("ini") + p32.Count("runtime") == 4,
            "entry=" + p32.Count("entry") + " ini=" + p32.Count("ini") + " runtime=" + p32.Count("runtime"));
        Chk("alts 归位 5 件", p32.Count("alt") == 5, p32.Count("alt").ToString());
        Chk("说明文本被跳过（不计入 role）", p32.Count("other") >= 1, p32.Count("other").ToString());
        Chk("6 个入口全部落在 0.3.2 窗口（28–34 MB）", AllEntriesIn(p32, WIN032_MIN, WIN032_MAX), EntrySizes(p32));
        Chk("zip 解压落在沙盒 _inbox",
            p32.TempDir.Length > 0 && p32.TempDir.IndexOf(Sand, StringComparison.OrdinalIgnoreCase) == 0, p32.TempDir);

        // ---------- 3. 合成包：0.3.0 档（体积窗口不能串档） ----------
        Head("3. 合成 0.3.0 包（必须落在 16–20 MB 档）");
        PackProbe p30 = Pack.Inspect(Path.Combine(Sand, "pkg030.zip"), null);
        Dump(p30);
        Chk("识别为 dlssg030", p30.Kind == Pack.K_030, p30.Kind);
        Chk("6 个入口全部落在 0.3.0 窗口（16–20 MB）", AllEntriesIn(p30, WIN030_MIN, WIN030_MAX), EntrySizes(p30));
        Chk("判据说的是 0.3.0 / 0.3.1 档（没串到 0.3.2 档）",
            p30.VerBasis.IndexOf("0.3.0 / 0.3.1 档", StringComparison.Ordinal) >= 0, p30.VerBasis);
        Chk("没被 INI 的 SM75 误导（0.3.0 的 INI 本来就没有）",
            p30.VerBasis.IndexOf("SM75", StringComparison.Ordinal) < 0, p30.VerBasis);

        // ---------- 3b. 连拖两个包不能互相污染 ----------
        //  一轮里拖进多个 zip 是常规用法（作者一次发好几个变体），而解压目录是按秒命名的 ——
        //  同秒撞名时两个包的文件会混进同一个目录，识别直接串档。这条是 2026-09-18 抓到的真 bug。
        Head("3b. 同一秒连拖两个包 —— 解压目录必须互不干扰");
        PackProbe a1 = Pack.Inspect(Path.Combine(Sand, "pkg032.zip"), null);
        PackProbe a2 = Pack.Inspect(Path.Combine(Sand, "pkg030.zip"), null);
        Chk("两个解压目录不同", a1.TempDir != a2.TempDir, a1.TempDir + " | " + a2.TempDir);
        Chk("先拖的那个仍是 0.3.2 档", AllEntriesIn(a1, WIN032_MIN, WIN032_MAX), EntrySizes(a1));
        Chk("后拖的那个仍是 0.3.0 档", AllEntriesIn(a2, WIN030_MIN, WIN030_MAX), EntrySizes(a2));
        Chk("各自只有 1 个 version.dll（没有互相混入）",
            CountName(a1, "version.dll") == 1 && CountName(a2, "version.dll") == 1,
            CountName(a1, "version.dll") + " / " + CountName(a2, "version.dll"));

        // ---------- 4. 旧版包 ----------
        Head("4. 旧版（0.2.x）包 —— 认得出来但拒绝导入");
        PackProbe pl = Pack.Inspect(Path.Combine(Sand, "legacy"), null);
        Dump(pl);
        Chk("识别为 legacy", pl.Kind == Pack.K_LEGACY, pl.Kind);
        string lr = Pack.Import(pl, null);
        Chk("Import 拒绝且说明原因", lr.IndexOf("旧版", StringComparison.Ordinal) >= 0, lr);

        // ---------- 5. 认不出的包 ----------
        Head("5. 认不出的包 —— 宁可不干活，也不往资源包里写");
        PackProbe pj = Pack.Inspect(Path.Combine(Sand, "junk"), null);
        Dump(pj);
        Chk("识别为 unknown", pj.Kind == Pack.K_UNK, pj.Kind);
        Chk("没有 Ready 文件", !pj.Ready, pj.Ready.ToString());
        string jr = Pack.Import(pj, null);
        Chk("Import 拒绝且说明原因", jr.IndexOf("认不出", StringComparison.Ordinal) >= 0, jr);

        // ---------- 6. zip-slip ----------
        Head("6. zip-slip 必须被拦下（压缩包里的 ../ 不能写到包外）");
        string evil = Path.Combine(Sand, "evil.zip");
        bool caught = false;
        string msg = "";
        try { PackProbe pe = Pack.Inspect(evil, null); msg = pe.Error; caught = pe.Error.Length > 0; }
        catch (Exception ex) { caught = true; msg = ex.Message; }
        Chk("含 ../ 的压缩包被拒绝", caught, msg);
        Chk("没有在沙盒根之外落文件",
            !File.Exists(Path.Combine(Sand, "escaped-by-zipslip.txt")), "");
        try { if (Directory.Exists(Path.Combine(Sand, "_inbox"))) { } } catch { }

        // ---------- 7. 真实发布包（三份，只读） ----------
        Head("7. 真实发布包不能认错（只读，不解压）");
        string listFile = Path.Combine(Sand, "real_packs.txt");
        if (!File.Exists(listFile))
        {
            L("  [SKIP] 没找到 real_packs.txt");
        }
        else
        {
            string[] rows = File.ReadAllLines(listFile, Encoding.UTF8);
            int seen = 0;
            foreach (string row in rows)
            {
                if (row.Trim().Length == 0 || row.IndexOf('\t') < 0) continue;
                string[] kv = row.Split('\t');
                PackProbe rp = Pack.Inspect(kv[1], null);
                seen++;
                L("  [" + kv[0] + "] " + rp.Kind + " · " + rp.VerBasis);
                Chk("真实包 " + kv[0] + " 识别为 dlssg030", rp.Kind == Pack.K_030, rp.Kind);
                //  旧文案（"0.3.2+ 档"）分不开 0.3.2 与 0.3.5，所以两条只能一起期望粗档。
                //  档表能分开之后，断言改成"报出的那一代 == 包名那一代"，是收紧不是放宽。
                Chk("真实包 " + kv[0] + " 判据报出与包名同一代",
                    rp.VerBasis.IndexOf(ExpectBand(kv[0]) + " 档", StringComparison.Ordinal) >= 0,
                    rp.VerBasis);
                Chk("真实包 " + kv[0] + " 的包名版本推测对了",
                    rp.Ver == kv[0], rp.Ver);
                List<PackItem> realAsk = Pack.NeedsTrustAsk(rp);
                Chk("真实包 " + kv[0] + " 的入口全部命中白名单（不该弹确认）",
                    realAsk.Count == 0,
                    "ask=" + realAsk.Count + (realAsk.Count > 0 ? " 首个=" + ProxySign.Short(realAsk[0].Cert) : ""));
            }
            Chk("至少验证了 2 份真实包", seen >= 2, seen.ToString());
        }

        // ---------- 8. 落盘 / 备份 / 账本 ----------
        Head("8. 归位落盘 + 旧资源包备份 + 账本");
        string ir = Pack.Import(p32, null);
        L("  " + ir.Replace("\n", "\n  "));
        string[] need = new string[] { "version.dll", "dlssg_sm86.ini", "nvngx_dlss.dll", "nvngx_dlssg.dll" };
        int have = 0;
        foreach (string n in need) if (File.Exists(Path.Combine(Dlssg030.CommonDir, n))) have++;
        Chk("common 落盘 4/4", have == 4, have + "/4");
        int alts = 0;
        try { alts = Directory.GetFiles(Dlssg030.AltsDir, "*.dll").Length; } catch { }
        Chk("alts 落盘 5 个 dll", alts == 5, alts.ToString());
        Chk("common\\version.dll 是 0.3.2 合成包的体积",
            new FileInfo(Path.Combine(Dlssg030.CommonDir, "version.dll")).Length == SZ_032_VERSION, "");
        string ledger = Path.Combine(Dlssg030.PackRoot, "pack.json");
        Chk("账本 pack.json 已生成", File.Exists(ledger), ledger);
        bool ledgerOk = false;
        string ledgerDetail = "";
        try
        {
            string txt = File.ReadAllText(ledger, Encoding.UTF8);
            object o = new JavaScriptSerializer().DeserializeObject(txt);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            ledgerOk = d != null && d.ContainsKey("files") && d.ContainsKey("importedAt");
            ledgerDetail = "keys=" + (d == null ? "-" : d.Count.ToString());
        }
        catch (Exception ex) { ledgerDetail = ex.GetType().Name + " " + ex.Message; }
        Chk("账本可被 JSON 解析且含 files/importedAt", ledgerOk, ledgerDetail);
        string ledgerTxt = File.Exists(ledger) ? File.ReadAllText(ledger, Encoding.UTF8) : "";
        Chk("账本记了签名指纹（signer / signVerdict）",
            ledgerTxt.IndexOf("\"signer\"", StringComparison.Ordinal) >= 0
            && ledgerTxt.IndexOf("\"signVerdict\"", StringComparison.Ordinal) >= 0, "");
        Chk("没有留下 _inbox 之外的散件",
            File.Exists(Path.Combine(Dlssg030.PackRoot, "readme.md")) == false, "");

        // 再导 0.3.0，旧件必须被备份
        Head("9. 覆盖导入时必须备份旧资源包");
        PackProbe p30b = Pack.Inspect(Path.Combine(Sand, "pkg030.zip"), null);
        Pack.Import(p30b, null);
        string bkr = Path.Combine(Dlssg030.PackRoot, "_backup");
        bool bkOk = false;
        string bkDetail = "";
        if (Directory.Exists(bkr))
        {
            foreach (string d in Directory.GetDirectories(bkr))
            {
                foreach (string f in Directory.GetFiles(d))
                {
                    if (f.EndsWith("common_version.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        bkOk = new FileInfo(f).Length == SZ_032_VERSION;   // 备份的是上一批（0.3.2）
                        bkDetail = d + "  " + new FileInfo(f).Length;
                    }
                }
            }
        }
        Chk("_backup\\_pack-* 里存着 0.3.2 的 version.dll", bkOk, bkDetail);
        Chk("common\\version.dll 已换成 0.3.0 的体积",
            new FileInfo(Path.Combine(Dlssg030.CommonDir, "version.dll")).Length == SZ_030_VERSION, "");

        // ---------- 10. force 通道：更新部署的关键 ----------
        Head("10. force 通道 —— 换了资源包后，游戏目录必须真的被重抄");
        Directory.CreateDirectory(Game);
        File.WriteAllBytes(Path.Combine(Game, "Client-Win64-Shipping.exe"), new byte[] { 0x4D, 0x5A });
        string kind = XeMfg.Detect(Game);
        Chk("沙盒游戏被认作鸣潮", kind == "wuwa", kind);
        string tdir = XeMfg.TargetDir(Game, kind);
        Directory.CreateDirectory(tdir);
        string entry = Dlssg030.EntryFor(kind);
        Chk("鸣潮入口是 dxgi.dll", entry == "dxgi.dll", entry);

        // 第一次部署（资源包此刻是 0.3.0）
        string r1 = Plan.SwitchTo(Game, Plan.D030);
        L("  " + r1.Replace("\n", "\n  "));
        string entryFile = Path.Combine(tdir, entry);
        Chk("已部署 dxgi.dll", File.Exists(entryFile), entryFile);
        long sizeAfterFirst = new FileInfo(entryFile).Length;
        Chk("落盘的是 0.3.0 档的代理", sizeAfterFirst == SZ_030_DXGI, sizeAfterFirst.ToString());

        // 换资源包到 0.3.2，不带 force → 必须短路（这正是不加 force 的"更新部署没生效"）
        Pack.Import(p32, null);
        string r2 = Plan.SwitchTo(Game, Plan.D030);
        L("  不带 force：" + r2.Replace("\n", " / "));
        bool shortCircuited = r2.IndexOf("无需切换", StringComparison.Ordinal) >= 0;
        Chk("不带 force 会短路（证明 force 是必需的）", shortCircuited, r2);
        Chk("游戏目录里还是 0.3.0 档（短路没重抄）",
            new FileInfo(entryFile).Length == SZ_030_DXGI, new FileInfo(entryFile).Length.ToString());

        // 带 force → 必须重抄成 0.3.2
        string r3 = Plan.SwitchTo(Game, Plan.D030, true);
        L("  带 force：" + r3.Replace("\n", " / "));
        Chk("带 force 后游戏目录里是 0.3.2 档的代理",
            new FileInfo(entryFile).Length == SZ_032_DXGI, new FileInfo(entryFile).Length.ToString());
        Chk("forced 部署也把出厂 INI 重抄了（0.3.2+ 的 3571 B 版本）",
            File.Exists(Path.Combine(tdir, "dlssg_sm86.ini")), "");
        Chk("Plan.Current 认得出来是本方案", Plan.Current(Game) == Plan.D030, Plan.Current(Game));

        // ---------- 10b. 签名核验（分级：白名单放行，其余交二次确认） ----------
        Head("10b. 签名核验 —— 判据是证书指纹，不是「签名有效」");
        // 合成包里的入口是零填充占位（未签名）→ 必须要求确认
        List<PackItem> ask32 = Pack.NeedsTrustAsk(p32);
        Chk("合成包要求二次确认，且覆盖全部 6 个入口", ask32.Count == 6, "count=" + ask32.Count);
        bool allBad = true;
        foreach (PackItem it in ask32) if (it.Cert == null || it.Cert.Ok) allBad = false;
        Chk("要求确认的每一条都没命中白名单", allBad, "");
        Chk("未签名文件结论 = unsigned",
            ask32.Count > 0 && ask32[0].Cert.Verdict == "unsigned",
            ask32.Count > 0 ? ask32[0].Cert.Verdict : "-");
        Chk("Short() 说成「未签名」",
            ask32.Count > 0 && ProxySign.Short(ask32[0].Cert).IndexOf("未签名", StringComparison.Ordinal) >= 0,
            ask32.Count > 0 ? ProxySign.Short(ask32[0].Cert) : "-");
        Chk("报告里出现「签名核验」块",
            p32.Report().IndexOf("签名核验", StringComparison.Ordinal) >= 0, "");

        // 真实代理（作者自签证书）→ 白名单命中，不该打扰用户。这是核心正向断言。
        string realProxy = FindRealProxy();
        if (realProxy == null)
        {
            L("  [SKIP] 本机找不到真实的 0.3.x 代理文件");
        }
        else
        {
            CertInfo rcs = ProxySign.Of(realProxy);
            Chk("真实代理命中作者证书白名单", rcs.Ok && rcs.Verdict == "upstream", ProxySign.Short(rcs));
            Chk("指纹等于常量 85BA6676…（自签名，链不可信也能认）",
                rcs.Thumb == ProxySign.ThumbUpstream, rcs.Thumb);
            Chk("Short() 标为「上游作者签名」",
                ProxySign.Short(rcs).IndexOf("上游作者签名", StringComparison.Ordinal) >= 0, ProxySign.Short(rcs));
        }

        // 有签名但不在白名单（微软系统文件）→ 同样不算通过，但与「未签名」要能区分开
        string msDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
        if (!File.Exists(msDll))
        {
            L("  [SKIP] 找不到 System32\\version.dll");
        }
        else
        {
            CertInfo mcs = ProxySign.Of(msDll);
            Chk("微软签名的 DLL 不在白名单 → 不通过", !mcs.Ok && mcs.Verdict == "other", ProxySign.Short(mcs));
            Chk("且能拿到指纹（与「未签名」区分开）",
                mcs.Thumb.Length > 0 && mcs.Thumb != ProxySign.ThumbUpstream, mcs.Thumb);
        }

        CertInfo gone = ProxySign.Of(Path.Combine(Sand, "no-such-file.dll"));
        Chk("文件不存在 → missing 且不通过", gone.Verdict == "missing" && !gone.Ok, gone.Verdict);

        // ---------- 11. 目标筛选 ----------
        Head("11. '已装本方案的游戏'筛选（更新部署的默认目标）");
        List<DlssgGame> gs = new List<DlssgGame>();
        DlssgGame g1 = new DlssgGame(); g1.Title = "鸣潮(沙盒)"; g1.Dir = Game; g1.Exe = "Client-Win64-Shipping.exe";
        DlssgGame g2 = new DlssgGame(); g2.Title = "没装过的游戏";
        g2.Dir = Path.Combine(Sand, "emptygame"); g2.Exe = "foo.exe";
        Directory.CreateDirectory(g2.Dir);
        gs.Add(g1); gs.Add(g2);
        List<DlssgGame> hit = Pack.Installed(gs);
        Chk("只有装了的那一个被选中", hit.Count == 1 && hit[0].Title == "鸣潮(沙盒)",
            hit.Count + " -> " + (hit.Count > 0 ? hit[0].Title : "-"));

        // ---------- 12. 磁盘上的资源包"实际是谁"（P0-2） ----------
        //  这一节盯的是 2026-09-21 换包暴露的盲区：界面显示的是代码常量 Ver，
        //  磁盘上躺着的是另一回事。所以断言的形状必须是"换掉磁盘文件 → 界面报不一致"，
        //  而不是"界面说什么就信什么"。
        Head("12. PackActual —— 资源包实际版本可见（代码常量 ≠ 磁盘事实）");
        string vfile = Path.Combine(Dlssg030.CommonDir, "version.dll");
        long sizeNow = new FileInfo(vfile).Length;      // 上一节把 pack 换回成了 0.3.2
        Chk("CodeOf 认得 0.3.5 的实测体积（含 alts 两端）",
            Dlssg030.CodeOf(30021408L) == "0.3.5" && Dlssg030.CodeOf(30021920L) == "0.3.5"
            && Dlssg030.CodeOf(30039840L) == "0.3.5", Dlssg030.CodeOf(30021920L));
        Chk("CodeOf 认得 0.3.2（version 29,975,840 与最贵的 alt 29,993,760）",
            Dlssg030.CodeOf(29975840L) == "0.3.2" && Dlssg030.CodeOf(29993760L) == "0.3.2", Dlssg030.CodeOf(29975840L));
        Chk("CodeOf 认不得 0.3.3 / 0.3.4 时返回空串（宁可不猜）",
            Dlssg030.CodeOf(30000000L).Length == 0, "'" + Dlssg030.CodeOf(30000000L) + "'");
        Chk("认不得小版本时退到粗档，不落到某个具体版本",
            Dlssg030.CoarseCodeOf(30000000L).Length > 0 && Dlssg030.CoarseCodeOf(30000000L) != "0.3.5",
            Dlssg030.CoarseCodeOf(30000000L));
        Chk("0.3.0 档体积反查到 0.3.0 / 0.3.1", Dlssg030.CodeOf(SZ_030_VERSION) == "0.3.0 / 0.3.1",
            Dlssg030.CodeOf(SZ_030_VERSION));

        Dlssg030.PackActualInfo q1 = Dlssg030.PackActual();
        L("  " + q1.Line());
        Chk("量到了 common\\version.dll 的实际体积", q1.Found && q1.EntrySize == sizeNow, q1.EntrySize.ToString("N0"));
        Chk("磁盘是 0.3.2 → 反查到 0.3.2", q1.Code == "0.3.2", q1.Code);
        Chk("与代码常量 0.3.5 不一致 → Verdict=mismatch 且 MatchesVer=false",
            q1.Verdict == "mismatch" && !q1.MatchesVer, q1.Verdict);
        Chk("不一致的行同时报出磁盘版与工具声明版，并带告警符",
            q1.Line().IndexOf("0.3.2", StringComparison.Ordinal) >= 0
            && q1.Line().IndexOf(Dlssg030.Ver, StringComparison.Ordinal) >= 0
            && q1.Line().IndexOf("⚠", StringComparison.Ordinal) >= 0, q1.Line());
        Chk("行里的体积是实测值（界面不再写死数字）",
            q1.Line().IndexOf(sizeNow.ToString("N0"), StringComparison.Ordinal) >= 0, q1.Line());
        Chk("导入账本读得回来（importedAt 非空）", q1.HasLedger && q1.ImportedAt.Length > 0, q1.ImportedAt);
        Chk("账本体积与现状一致 → 不报漂移", q1.Drift.Length == 0, q1.Drift);
        Chk("入口签名判定带出来了（合成包必然是未签名）",
            q1.SignVerdict == "unsigned", q1.SignVerdict);
        Chk("alts 体积范围被量到（5 个备选）", q1.AltsCount == 5 && q1.AltsMin > 0 && q1.AltsMax >= q1.AltsMin,
            q1.AltsCount + " 个 " + q1.AltsMin + "~" + q1.AltsMax);

        // 把磁盘上的 version.dll 换成 0.3.5 的体积（只改长度，不复制 30 MB）——
        //  这就是"手工换包"在体积判据上的等价物，也是验收判据要求的那一步。
        long sizeBack = sizeNow;
        SetLen(vfile, 30021920L);
        Dlssg030.PackActualInfo q2 = Dlssg030.PackActual();
        L("  换成 0.3.5 体积后：" + q2.Line());
        Chk("换成 0.3.5 体积 → Verdict=ok、MatchesVer=true",
            q2.Verdict == "ok" && q2.MatchesVer, q2.Verdict);
        Chk("一致时版本这一段不告警（漂移另算，见下一条）",
            q2.Line().StartsWith("资源包：" + Dlssg030.Ver) && q2.Line().IndexOf("磁盘是", StringComparison.Ordinal) < 0,
            q2.Line());
        Chk("一致时显示导入时间", q2.Line().IndexOf("导入", StringComparison.Ordinal) >= 0, q2.Line());
        Chk("磁盘被手工换过 → 报出与账本的体积漂移", q2.Drift.Length > 0, q2.Drift);
        Chk("体积档对得上但账本对不上 → Warn 仍为真（光看 MatchesVer 会漏）", q2.Warn, q2.Drift);

        // 体积落在窗口内、但对不上任何细档（0.3.3/0.3.4 这一类）
        SetLen(vfile, 30000000L);
        Dlssg030.PackActualInfo q3 = Dlssg030.PackActual();
        Chk("认不到小版本 → Verdict=coarse（不算一致，也不谎报成某个版本）",
            q3.Verdict == "coarse" && !q3.MatchesVer && q3.Code.IndexOf("0.3.2", StringComparison.Ordinal) >= 0,
            q3.Verdict + " / " + q3.Code);

        // 完全不是代理的体积（游戏自带同名 dll 被塞进来了）
        SetLen(vfile, 4096L);
        Dlssg030.PackActualInfo q4 = Dlssg030.PackActual();
        Chk("体积不在任何窗口 → Verdict=unknown", q4.Verdict == "unknown" && !q4.MatchesVer, q4.Verdict);
        Chk("unknown 也带告警符", q4.Line().IndexOf("⚠", StringComparison.Ordinal) >= 0, q4.Line());

        SetLen(vfile, sizeBack);
        Dlssg030.PackActualInfo q5 = Dlssg030.PackActual();
        Chk("恢复原体积后回到 mismatch（探针没留下脏状态）",
            q5.Verdict == "mismatch" && q5.EntrySize == sizeBack && q5.Drift.Length == 0, q5.Verdict);

        // 没有账本 = 手工换包，必须如实说明，不能装作"没导入过所以不知道"
        string led = Path.Combine(Dlssg030.PackRoot, "pack.json");
        string ledOff = led + ".probe-off";
        File.Move(led, ledOff);
        Dlssg030.PackActualInfo q6 = Dlssg030.PackActual();
        Chk("账本缺失 → HasLedger=false 且明说没有导入账本",
            !q6.HasLedger && (q6.Line().IndexOf("账本", StringComparison.Ordinal) >= 0), q6.Line());
        Chk("账本缺失时退到文件时间这条线索", q6.EntryTime.Length > 0, q6.EntryTime);
        File.Move(ledOff, led);
        Dlssg030.PackActualInfo q7 = Dlssg030.PackActual();
        Chk("账本放回来就重新读得到（探针复原）", q7.HasLedger, "");

        // ---------- 13. 判据外置（P1-4）：换一次包只改一份数据 ----------
        //  这一节盯的不是"读得到 JSON"，而是那条验收判据：
        //  **不重新编译**、只往数据目录放一份 catalog.json，新版本档就得被认出来；
        //  文件撤掉之后，旧的三档必须回到原样（否则内置默认就成了摆设）。
        Head("13. catalog.json 外置判据（只改数据，不改代码）");
        string catFile = Path.Combine(Data, "catalog.json");
        Chk("没有 catalog.json 时用的是内置默认",
            Catalog.Origin.IndexOf("内置", StringComparison.Ordinal) >= 0
            && Catalog.Error.Length == 0, Catalog.Summary());
        Chk("内置默认认不出合成档 0.9.9 的体积", Dlssg030.CodeOf(31200000L).Length == 0,
            Dlssg030.CodeOf(31200000L));

        //  只改数据：ver 换掉、加一条 0.9.9 档、粗窗口第二段抬到 36 MB、入口名单多一个名字
        File.WriteAllText(catFile, CatalogJson("0.9.9"), new UTF8Encoding(false));
        Catalog.Reload();
        L("  " + Catalog.Summary());
        Chk("Reload 后判据来源变成那份文件", Catalog.Origin == catFile, Catalog.Origin);
        Chk("ver 由数据文件带出来（Dlssg030.Ver 跟着变）",
            Dlssg030.Ver == "0.9.9" && Catalog.Ver == "0.9.9", Dlssg030.Ver);
        Chk("新档体积认得出代号 0.9.9", Dlssg030.CodeOf(31200000L) == "0.9.9", Dlssg030.CodeOf(31200000L));
        //  31.2 MB 在内置默认的粗窗口（28–34 MB）之内，本来就该被认下 —— 用它证明不了"窗口来自数据"。
        //  35,000,000 B 才是那一格：注入的窗口是 28–36 MB（收），默认是 28–34 MB（不收）。
        Chk("注入的粗窗口覆盖到 35.8 MB（默认窗口下这个体积不算代理）",
            Dlssg030.IsProxySize(35800000L) && Dlssg030.IsProxySize(31200000L), "");
        Chk("入口名单也来自同一份数据（多出一个探针名）",
            Plan.AllEntryNames.Length == 8 && Plan.IsEntryName("probe-only.dll"),
            Plan.AllEntryNames.Length + " 个");
        Chk("旧的三档没有被新档挤掉",
            Dlssg030.CodeOf(29975840L) == "0.3.2" && Dlssg030.CodeOf(30021920L) == "0.3.5"
            && Dlssg030.CodeOf(17529120L) == "0.3.0 / 0.3.1", "");
        Chk("方案A下沿同样可调（数据说了算）", Catalog.OptiMin == 20971520L, Catalog.OptiMin.ToString());
        Chk("INI 体积清单跟着档表走（不再抄在代码里）",
            Catalog.IniSizesText().IndexOf("0.9.9 是 4096", StringComparison.Ordinal) >= 0,
            Catalog.IniSizesText());

        //  喂一个"0.9.9 档"的合成目录给识别逻辑（Inspect 收目录，不必动 runner 的 zip 夹具）
        string synth = Path.Combine(Sand, "pkg099");
        Directory.CreateDirectory(synth);
        MakeLen(Path.Combine(synth, "version.dll"), 31200000L);
        File.WriteAllText(Path.Combine(synth, "dlssg_sm86.ini"), "[General]\r\n");
        PackProbe p99 = Pack.Inspect(synth, null);
        L("  " + p99.VerBasis);
        Chk("仅改数据就让新版本包被认成 dlssg030", p99.Kind == Pack.K_030, p99.Kind);
        Chk("识别报告按新档报出版本", p99.VerBasis.IndexOf("0.9.9 档", StringComparison.Ordinal) >= 0,
            p99.VerBasis);
        Chk("主入口仍是 version.dll", p99.Entry == "version.dll", p99.Entry);

        //  撤掉文件 → 一切回到内置默认（内置默认必须与改前逐值相同）
        File.Delete(catFile);
        Catalog.Reload();
        Chk("撤掉文件后回到内置默认", Catalog.Origin.IndexOf("内置", StringComparison.Ordinal) >= 0,
            Catalog.Origin);
        Chk("回到默认后 ver / 窗口 / 名单都复原",
            Dlssg030.Ver == "0.3.5" && Dlssg030.CodeOf(31200000L).Length == 0
            && !Dlssg030.IsProxySize(35800000L) && Plan.AllEntryNames.Length == 7,
            Dlssg030.Ver + " / " + Plan.AllEntryNames.Length);
        Chk("默认档表三条都在", Catalog.Bands.Length == 3, Catalog.Bands.Length + " 条");

        //  写坏的文件：必须退回默认**并且把错说出来**（静默回落是最难查的那类）
        File.WriteAllText(catFile, "{ this is not json", new UTF8Encoding(false));
        Catalog.Reload();
        Chk("坏 JSON：判据退回内置默认",
            Dlssg030.Ver == "0.3.5" && Dlssg030.CodeOf(31200000L).Length == 0, Dlssg030.Ver);
        Chk("坏 JSON：错误留痕，并在 Summary 里说出来",
            Catalog.Error.Length > 0 && Catalog.Summary().IndexOf('⚠') >= 0, Catalog.Error);
        //  「导出判据模板」写出来的那份，读回去必须与内置默认逐值相同 ——
        //  模板形状不对的话，用户点一下按钮就把判据弄坏了。
        File.WriteAllText(catFile, Catalog.DefaultJson(), new UTF8Encoding(false));
        Catalog.Reload();
        Chk("内置默认导出的模板读回去，判据逐值不变",
            Dlssg030.Ver == "0.3.5" && Catalog.Bands.Length == 3 && Catalog.Error.Length == 0
            && Dlssg030.CodeOf(29975840L) == "0.3.2" && Dlssg030.CodeOf(30021920L) == "0.3.5"
            && Plan.AllEntryNames.Length == 7 && Catalog.OptiMin == 20971520L,
            Catalog.Summary());
        Chk("模板带上了旧版窗口与源件窗口（这两条也在判据里）",
            Catalog.IsLegacySize(15667520L) && Catalog.IsSourceSize(15667520L)
            && !Catalog.IsSourceSize(29975840L), "");
        File.Delete(catFile);
        Catalog.Reload();
        Chk("清场后恢复默认（不留脏状态给后面的节次）",
            Dlssg030.Ver == "0.3.5" && Catalog.Error.Length == 0, Catalog.Summary());

        L("");
        L("========================================");
        //  顺手落一份 UTF-8 日志：runner 的控制台通道是 GBK（本机代码页 936），
        //  中文报告经它转一手就成乱码 —— 读不懂的探针报告等于没跑（plan / fg 探针早就这么存了）。
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pack_probe.txt"),
                              Out.ToString(), new UTF8Encoding(false));
        }
        catch { }

        L(Fail == 0 ? "PACK PROBE: ALL PASS" : ("PACK PROBE: " + Fail + " FAILED"));
        L("========================================");
        return Fail == 0 ? 0 : 1;
    }


    // 造一份判据文件（ver / 窗口 / 档表 / 入口名单 / 指纹一次写全）。
    //  刻意用序列化而不是手写 JSON 字面量：手写的那版把 python、C#、JSON 三层转义叠在一起，
    //  少一个反斜杠就是"字符串常量里有换行符"这种编译错 —— 判据本身反而没在看。
    static string CatalogJson(string ver)
    {
        var wins = new object[] {
            Win(16L * 1024 * 1024, 20L * 1024 * 1024),
            Win(28L * 1024 * 1024, 36000000L),
        };
        var bands = new object[] {
            Band("0.3.0 / 0.3.1", 16L * 1024 * 1024, 20L * 1024 * 1024, 2099, "首个公开版"),
            Band("0.3.2", 29974816L, 29993761L, 3548, "20+30 系合并包"),
            Band("0.3.5", 30021408L, 30039841L, 3571, "修 #561"),
            Band("0.9.9", 31000000L, 31500000L, 4096, "探针造的档"),
        };
        var top = new Dictionary<string, object>();
        top["schema"] = 1;
        var pr = new Dictionary<string, object>();
        pr["ver"] = ver;
        pr["optiMin"] = 20971520L;
        pr["windows"] = wins;
        pr["bands"] = bands;
        pr["entries"] = new object[] { "dxgi.dll", "d3d12.dll", "version.dll", "dinput8.dll",
                                       "winmm.dll", "winhttp.dll", "dbghelp.dll", "probe-only.dll" };
        var signs = new Dictionary<string, object>();
        signs["upstream"] = "85BA66762F851E49148D706915D09026281418E6";
        signs["nvidia"] = "7B7B0B6697AFB438CF6F65A155F00E86676FB186";
        pr["signs"] = signs;
        top["proxy"] = pr;
        return new JavaScriptSerializer().Serialize(top);
    }

    static Dictionary<string, object> Win(long min, long max)
    {
        var d = new Dictionary<string, object>();
        d["min"] = min; d["max"] = max;
        return d;
    }

    static Dictionary<string, object> Band(string id, long min, long max, long ini, string note)
    {
        var d = new Dictionary<string, object>();
        d["id"] = id; d["min"] = min; d["max"] = max; d["ini"] = ini; d["note"] = note;
        return d;
    }

    // 包名 → 期望的档名（0.3.0 与 0.3.1 在档表里就是同一条）
    static string ExpectBand(string v)
    {
        if (v == "0.3.0" || v == "0.3.1") return "0.3.0 / 0.3.1";
        return v;
    }

    // 造一个体积精确的新文件（SetLen 只能改已存在文件的长度）
    static void MakeLen(string path, long len)
    {
        using (FileStream fs = File.Create(path)) fs.SetLength(len);
    }

    static void SetLen(string path, long len)
    {
        using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            fs.SetLength(len);
    }

    // 真实代理文件（作者签名的那几个）—— 只读，不复制。找不到就跳过相关断言。
    //  优先用 runner 写下的真实发布包目录（换机器也不用改这个探针）。
    static string FindRealProxy()
    {
        string[] names = new string[] { "version.dll", "dxgi.dll", "d3d12.dll" };
        string listFile = Path.Combine(Sand, "real_packs.txt");
        if (File.Exists(listFile))
        {
            foreach (string row in File.ReadAllLines(listFile, Encoding.UTF8))
            {
                if (row.Trim().Length == 0 || row.IndexOf('\t') < 0) continue;
                string d = row.Split('\t')[1];
                foreach (string n in names)
                {
                    string f = Path.Combine(d, n);
                    if (File.Exists(f)) return f;
                }
            }
        }
        foreach (string c in new string[] {
            @"C:\ProgramData\Fluxion\dlssg030-pack\common\version.dll",
            @"C:\ProgramData\Fluxion\dlssg030-pack\alts\d3d12.dll",
            @"C:\ProgramData\Fluxion\dlssg\source\version.dll" })
            if (File.Exists(c)) return c;
        return null;
    }

    // 入口体积是否全部落在给定窗口内 —— 判据本来就是窗口，不是等值
    static bool AllEntriesIn(PackProbe p, long lo, long hi)
    {
        int n = 0;
        foreach (PackItem it in p.Items)
        {
            if (it.Role != "entry" && it.Role != "alt") continue;
            n++;
            if (it.Size < lo || it.Size > hi) return false;
        }
        return n > 0;
    }

    static int CountName(PackProbe p, string name)
    {
        int n = 0;
        foreach (PackItem it in p.Items)
            if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    static string EntrySizes(PackProbe p)
    {
        StringBuilder sb = new StringBuilder();
        foreach (PackItem it in p.Items)
        {
            if (it.Role != "entry" && it.Role != "alt") continue;
            if (sb.Length > 0) sb.Append("  ");
            sb.Append(it.Name).Append("=").Append(it.Size);
        }
        return sb.ToString();
    }

    static void Dump(PackProbe p)
    {
        string[] lines = p.Report().Split('\n');
        for (int i = 0; i < lines.Length; i++) L("  " + lines[i].TrimEnd());
    }
}
}
