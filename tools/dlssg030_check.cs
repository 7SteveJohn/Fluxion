// DLSSG 0.3.0 整合自检：对真实游戏目录验证识别/状态逻辑（只读）
using System;
using Fluxion;

class Dlssg030Check
{
    static void Main()
    {
        Program.Cfg = Config.Load(Program.ConfigPath);
        string zzz = @"G:\miHoYo Launcher\games\ZenlessZoneZero Game";
        string wuwa = @"G:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64";

        Console.WriteLine("pack root      : " + Dlssg030.PackRoot);
        Console.WriteLine("pack exists    : " + System.IO.Directory.Exists(Dlssg030.PackRoot));
        Console.WriteLine();
        Console.WriteLine("== 绝区零 ==");
        Console.WriteLine("  Detect       : " + XeMfg.Detect(zzz));
        Console.WriteLine("  030 entry    : " + Dlssg030.EntryFor(XeMfg.Detect(zzz)));
        Console.WriteLine("  030 installed: " + Dlssg030.IsInstalled(zzz));
        Console.WriteLine("  030 status   : " + Dlssg030.Status(zzz));
        Console.WriteLine("  XeSS installed(应为 False): " + XeMfg.IsInstalled(zzz));
        Console.WriteLine();
        Console.WriteLine("== 鸣潮 ==");
        Console.WriteLine("  Detect       : " + XeMfg.Detect(wuwa));
        Console.WriteLine("  030 entry    : " + Dlssg030.EntryFor(XeMfg.Detect(wuwa)));
        Console.WriteLine("  030 installed: " + Dlssg030.IsInstalled(wuwa));
        Console.WriteLine("  030 status   : " + Dlssg030.Status(wuwa));
        Console.WriteLine("  XeSS installed(鸣潮当前应为 True): " + XeMfg.IsInstalled(wuwa));
        Console.WriteLine();
        // 互斥守卫验证：对已装 XeSS 的鸣潮执行 030 部署应被拒绝
        Console.WriteLine("== 互斥守卫（鸣潮已装 XeSS，030 部署应被拒绝）==");
        Console.WriteLine("  " + Dlssg030.Install(wuwa));
        Console.WriteLine();
        // 回滚安全性：卸载绝区零 0.3.0 后再装回（干跑不执行，只打印将做的事）
        Console.WriteLine("== 绝区零 0.3.0 卸载预览（未执行）==");
        Console.WriteLine("  Uninstall() 会恢复 _backup\\zzz 里的游戏自带 nvngx，并删除 d3d12.dll/dlssg_sm86.ini");
    }
}
