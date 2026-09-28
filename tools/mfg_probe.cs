using System;
using System.IO;
using System.Text;

// v2.10.2 回归自检：0.3.0 的倍率上限改写（Dlssg030.SetMaxFrames）
//  为什么要单独测：0.3.0 的 dlssg_sm86.ini 与旧版格式不同，用旧版 BuildIni() 覆盖会丢键。
//  这里在一个**沙盒目录**里真跑（放一个假的 Client-Win64-Shipping.exe 就能让 XeMfg.Detect 认作"鸣潮"），
//  断言：只动 MaxGeneratedFrames 那一行，其余键一个不少。
namespace Fluxion
{
static class MfgProbe
{
    static StringBuilder Out = new StringBuilder();
    static int Fail = 0;

    static void L(string s) { Out.AppendLine(s); Console.WriteLine(s); }

    static void Chk(string name, bool ok, string detail)
    {
        if (!ok) Fail++;
        L((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    // 0.3.0 出厂 ini 的独有键（一个都不能丢）
    static readonly string[] Keys030 = new string[] {
        "Enabled=1", "Optimized=1", "Preset=Auto", "Level=1",
        @"Directory=dlssg_sm86\logs", "Mode=Bundled", "CacheDirectory=" };

    const string Ini030 = @"; DLSSG SM86 - frame generation for Ampere (RTX 30) and newer.
[General]
Enabled=1

[FrameGeneration]
Optimized=1
MaxGeneratedFrames=5

[Compatibility]
Preset=Auto

[Logging]
Level=1
Directory=dlssg_sm86\logs

[Runtime]
Mode=Bundled
CacheDirectory=
";

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : @"D:\youhua\_t030";
        string sand = Path.Combine(root, Path.Combine("Wuthering Waves Game", Path.Combine("Client", Path.Combine("Binaries", "Win64"))));
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(sand);
            File.WriteAllBytes(Path.Combine(sand, "Client-Win64-Shipping.exe"), new byte[] { 0x4D, 0x5A });
            string ini = Path.Combine(sand, "dlssg_sm86.ini");

            L("=== 0. 沙盒与识别 ===");
            Chk("XeMfg.Detect 认作鸣潮", XeMfg.Detect(sand) == "wuwa", XeMfg.Detect(sand));
            Chk("TargetDir 指向沙盒", XeMfg.TargetDir(sand, "wuwa") == sand, XeMfg.TargetDir(sand, "wuwa"));

            L("");
            L("=== 1. 改到 2X（5 -> 2），其余键必须原样 ===");
            File.WriteAllText(ini, Ini030, new UTF8Encoding(false));
            string r = Dlssg030.SetMaxFrames(sand, 2);
            L("  返回: " + r);
            string t = File.ReadAllText(ini);
            Chk("MaxGeneratedFrames=2", t.Contains("MaxGeneratedFrames=2"), "");
            Chk("未残留 =5", !t.Contains("MaxGeneratedFrames=5"), "");
            foreach (string k in Keys030)
                Chk("保留键 " + k, t.Contains(k), "");

            L("");
            L("=== 2. 改到 6X（2 -> 5）===");
            r = Dlssg030.SetMaxFrames(sand, 5);
            L("  返回: " + r);
            t = File.ReadAllText(ini);
            Chk("MaxGeneratedFrames=5", t.Contains("MaxGeneratedFrames=5"), "");
            Chk("回执含 6X", r.Contains("6X"), r);
            foreach (string k in Keys030)
                Chk("保留键 " + k, t.Contains(k), "");

            L("");
            L("=== 3. 越界值要被夹住（99 -> 5，0 -> 1）===");
            r = Dlssg030.SetMaxFrames(sand, 99);
            Chk("99 夹到 5", File.ReadAllText(ini).Contains("MaxGeneratedFrames=5"), r);
            r = Dlssg030.SetMaxFrames(sand, 0);
            Chk("0 夹到 1", File.ReadAllText(ini).Contains("MaxGeneratedFrames=1"), r);

            L("");
            L("=== 4. 键整行缺失时，插入到 [FrameGeneration] 之后 ===");
            string noKv = Ini030.Replace("MaxGeneratedFrames=5" + "\n", "");
            File.WriteAllText(ini, noKv, new UTF8Encoding(false));
            r = Dlssg030.SetMaxFrames(sand, 4);
            L("  返回: " + r);
            string[] lines = File.ReadAllLines(ini);
            int iFg = Array.FindIndex(lines, x => x.Trim() == "[FrameGeneration]");
            Chk("找到 [FrameGeneration]", iFg >= 0, iFg.ToString());
            Chk("紧跟在它后面插入",
                iFg >= 0 && iFg + 1 < lines.Length && lines[iFg + 1].Trim() == "MaxGeneratedFrames=4",
                (iFg >= 0 && iFg + 1 < lines.Length) ? lines[iFg + 1] : "(越界)");
            Chk("Optimized 仍在", File.ReadAllText(ini).Contains("Optimized=1"), "");

            L("");
            L("=== 5. ini 不存在时给友好错误，不抛异常 ===");
            File.Delete(ini);
            r = Dlssg030.SetMaxFrames(sand, 5);
            L("  返回: " + r);
            Chk("回执是提示而非异常", r.Contains("找不到") || r.Contains("改写失败"), r);

            L("");
            L("=== 6. 目录里没有 ini 时也要给提示（不抛异常） ===");
            //  2026-09-16 起 SetMaxFrames 不再要求"必须是绝区零 / 鸣潮"—— 通用游戏（3A 单机）
            //  同样要能改上限，所以判据从"游戏类型"改成"目录里有没有 dlssg_sm86.ini"。
            //  这条断言原来写的是"拒绝非支持目录（含'不在支持列表'）"，改判据那天起就已失效
            //  （2026-09-17 换 0.3.2 时发现是陈旧的假红），现在按真实契约重写。
            string other = Path.Combine(root, "NotAGame");
            Directory.CreateDirectory(other);
            r = Dlssg030.SetMaxFrames(other, 5);
            L("  返回: " + r);
            Chk("没 ini 的目录 → 提示找不到 ini（不抛异常）",
                r.Contains("找不到") && r.Contains("dlssg_sm86.ini"), r);
        }
        catch (Exception ex)
        {
            Fail++;
            L("EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            L(ex.StackTrace == null ? "" : ex.StackTrace);
        }

        L("");
        L(Fail == 0 ? "RESULT: ALL PASS" : ("RESULT: " + Fail + " FAILED"));
        try
        {
            string o = Path.Combine(Path.GetTempPath(), "mfg_probe.txt");
            File.WriteAllText(o, Out.ToString(), new UTF8Encoding(false));
            Console.WriteLine("saved " + o);
        }
        catch { }
        try { Directory.Delete(root, true); } catch { }
        return Fail == 0 ? 0 : 1;
    }
}
}
