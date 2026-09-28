// 常备诊断：Windows 的"显示动画"开关到底开没开（决定动效引擎走完整动画还是瞬时 Snap）。
//   排查"动效看不到 / 加了动画没反应"时第一个该跑的东西。SPI_GETCLIENTAREAANIMATION = 0x1042；
//   顺带打出相关的几个开关和注册表值做交叉印证（注意 MinAnimate 是另一个开关，别混淆）。
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class AnimCheck
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    static extern bool SPI(uint act, uint p, ref int v, uint ini);

    static void One(string name, uint act)
    {
        int v = -1;
        bool ok = false;
        try { ok = SPI(act, 0, ref v, 0); } catch (Exception e) { Console.WriteLine(name + " EX " + e.Message); return; }
        Console.WriteLine(string.Format("{0,-34} act=0x{1:X4} ok={2} value={3}", name, act, ok, v));
    }

    static void Main()
    {
        One("SPI_GETCLIENTAREAANIMATION", 0x1042);
        One("SPI_GETUIEFFECTS", 0x103E);
        One("SPI_GETANIMATION", 0x0048);
        One("SPI_GETDRAGFULLWINDOWS", 0x0026);
        One("SPI_GETSCREENSAVERRUNNING", 0x0072);

        Show(@"Control Panel\Desktop", "UserPreferencesMask");
        Show(@"Control Panel\Desktop\WindowMetrics", "MinAnimate");
        Show(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting");
        Show(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");

        Console.WriteLine("OS = " + Environment.OSVersion.VersionString);
    }

    static void Show(string key, string name)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key))
            {
                object o = k == null ? null : k.GetValue(name);
                string s;
                if (o is byte[]) { var b = (byte[])o; s = "bytes[" + b.Length + "] " + BitConverter.ToString(b); }
                else s = o == null ? "(null)" : o.ToString();
                Console.WriteLine(string.Format("{0}\\{1,-20} = {2}", key, name, s));
            }
        }
        catch (Exception e) { Console.WriteLine(key + "\\" + name + " ERR " + e.Message); }
    }
}
