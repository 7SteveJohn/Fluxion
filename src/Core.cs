using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Fluxion")]
[assembly: System.Reflection.AssemblyProduct("Fluxion")]
[assembly: System.Reflection.AssemblyVersion("3.3.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.3.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("3.3.2")]

namespace Fluxion
{
    public static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        // 同一个 API 的"取回一个 BOOL"重载 —— 走独立托管名 + EntryPoint，避免和上面 IntPtr 版重载打架
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        public static extern bool SystemParametersInfoRef(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

        // ★ 读"显示动画"开关（设置 → 辅助功能 → 视觉特效 → 动画效果；
        //   等同性能选项里的"为窗口内的控件和元素添加动画"）。
        //   SPI_GETCLIENTAREAANIMATION = 0x1042。动效引擎据此降级：关掉系统动画的用户，
        //   不该被我们自己的动效二次打扰（WCAG 减少动效 / Fluent accessible motion 的硬要求）。
        //   WinXP/2003 不认识这个常量（调用失败）→ 兜底当"允许"。
        public static bool ClientAreaAnimationEnabled()
        {
            try
            {
                int v = 1;
                if (!SystemParametersInfoRef(0x1042, 0, ref v, 0)) return true;
                return v != 0;
            }
            catch { return true; }
        }
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("ntdll.dll")]
        public static extern int NtSetTimerResolution(int desiredIn100ns, bool setResolution, out int currentRes);
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr hProcess);
        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr hProcess);
        [DllImport("psapi.dll")]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);
        // 深色标题栏（Win10 1809+）。属性号 20 是现行编号，19 是 1809~1909 的旧编号，
        // 两个都试一遍，失败直接忽略 —— 这只是观感问题，不值得抛异常。
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // 显示器模式（读刷新率用）。DEVMODE 的 dmOrientation..dmPrintQuality 与
        //  dmPosition+dmDisplayOrientation+dmDisplayFixedOutput 是 union，两者都占 16 字节，这里用后者。
        //  Unicode 版 sizeof(DEVMODE) = 220，dmSize 必须填这个值，否则 EnumDisplaySettings 返回失败。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion; public short dmDriverVersion;
            public short dmSize; public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX; public int dmPositionY;
            public int dmDisplayOrientation; public int dmDisplayFixedOutput;
            public short dmColor; public short dmDuplex; public short dmYResolution;
            public short dmTTOption; public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
            public int dmDisplayFlags; public int dmDisplayFrequency;
            public int dmICMMethod; public int dmICMIntent; public int dmMediaType;
            public int dmDitherType; public int dmReserved1; public int dmReserved2;
            public int dmPanningWidth; public int dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        // 写模式（显示模式守卫用）。CDS_UPDATEREGISTRY 会把结果一并存进注册表，重启后不回弹。
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);
        // 分离显示器专用：devMode 传 NULL = 把这块屏从桌面摘除（禁用）。
        // 与上面的 ref DEVMODE 版是同一个导出（ChangeDisplaySettingsExW）的两个重载，C# 侧必须起不同方法名。
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        public static extern int ChangeDisplaySettingsDetach(string deviceName, IntPtr devMode, IntPtr hwnd, int flags, IntPtr param);
        // 显示设备枚举（拿 StateFlags 判断某屏是否还挂在桌面上，ATTACHED_TO_DESKTOP=0x1）
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        public static extern bool EnumDisplayDevices(string device, int devNum, ref DISPLAY_DEVICE dd, int flags);

        // ============ 显示器设备：设备管理器层面的「启用 / 禁用」============
        // ⚠ 这和上面的 ChangeDisplaySettingsDetach 是**两件不同的事**，别混：
        //   · Detach（上面）  = 把这块屏从桌面上"摘掉"（显示配置层）—— 设备还在、EDID 还读得到，
        //                       设备管理器里显示"正常运行"，重启后自动回原生。
        //   · Disable（这里） = 设备管理器里右键「禁用设备」（PnP 层）—— 设备被停用、EDID 不再读，
        //                       设备管理器里显示"已禁用"，重启后**仍然是禁用**。
        // 2026-09-20 用户明确要的是后者（截图就是设备管理器「监视器」节点下的 P27FBB-RG）。
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_CLASSINSTALL_HEADER
        {
            public int cbSize;
            public int InstallFunction;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public int StateChange;
            public int Scope;
            public int HwProfile;
        }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            System.Text.StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            int Property, out int PropertyRegDataType, byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiSetClassInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            ref SP_PROPCHANGE_PARAMS ClassInstallParams, int ClassInstallParamsSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_DevNode_Status(out int status, out int problemNumber, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID(int devInst, System.Text.StringBuilder buffer, int bufferLen, int flags);
        // 设备管理器的「禁用/启用设备」在 cfgmgr32 里就是这两个（CR_SUCCESS = 0）。
        // 用它们当兜底：不经过类安装器，显示器这类"按硬件配置文件走"的设备有时只有它能停掉。
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Disable_DevNode(int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Enable_DevNode(int devInst, int flags);

        // ---- 送回收站（Junk 的删除通道）----
        //  为什么走 shell 而不是 File.Delete：备份/隔离区里的东西用户事后可能还想捞，
        //  硬删不留反悔余地 —— "清理"这种破坏性操作，可逆是底线。
        //  ⚠ pFrom 必须是**双 \0 结尾**，少一个 shell 直接返回 0x0002（文件找不到），
        //    这个坑在 MSDN 的签名注释里，不在报错里。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }
        public const uint FO_DELETE = 3;
        public const ushort FOF_SILENT = 0x0004, FOF_ALLOWUNDO = 0x0040,
                            FOF_NOCONFIRMATION = 0x0010, FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

        // 把一批路径送进回收站。返回 0 = 成功；非 0 是 shell 的错误码（原样给回执用）。
        public static int RecycleToBin(IntPtr owner, string path)
        {
            SHFILEOPSTRUCTW o = new SHFILEOPSTRUCTW();
            o.hwnd = owner;
            o.wFunc = FO_DELETE;
            o.pFrom = path + "\0\0";
            o.pTo = null;
            o.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
            return SHFileOperation(ref o);
        }
    }

    // ============ 显示模式守卫 ============
    // 起因（2026-09-20 实测）：远控整套进程被杀掉再拉起时，显卡会重新协商显示模式，
    //   主屏会被甩到远控自己注入的合成档上（本机实测 1920x1080@165 → 1568x1080@165）。
    // 做法：暂停远控前先记下每台在用屏的模式与位置，远控恢复后延时比对，被改了就装回去。
    //   ⚠ 这里只写"曾经存在且现在还在"的那几屏，**不负责**创建/删除屏幕：外接屏拔没拔由硬件说话。
    public static class DispGuard
    {
        public struct Mode { public string Dev; public int W, H, Hz, Bpp, X, Y; }

        const int ENUM_CURRENT = -1;
        const int DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000,
                  DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        const int CDS_UPDATEREGISTRY = 0x1;
        const int DISP_CHANGE_SUCCESSFUL = 0;

        // \\.\DISPLAY1..16 依次试探：设备不存在时 EnumDisplaySettings 直接返回 false，无需 EnumDisplayDevices。
        public static List<Mode> Capture()
        {
            var list = new List<Mode>();
            try
            {
                for (int i = 1; i <= 16; i++)
                {
                    var dm = new Native.DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                    string dev = "\\\\.\\DISPLAY" + i;
                    if (!Native.EnumDisplaySettings(dev, ENUM_CURRENT, ref dm)) continue;
                    if (dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) continue;
                    list.Add(new Mode
                    {
                        Dev = dev, W = dm.dmPelsWidth, H = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency,
                        Bpp = dm.dmBitsPerPel, X = dm.dmPositionX, Y = dm.dmPositionY
                    });
                }
            }
            catch { }
            return list;
        }

        // 远控主程序起来之后显卡还要缓几秒才稳定，太早装回去会被下一次重协商盖掉。
        public static void RestoreLater(List<Mode> want, int delayMs)
        {
            // Log 是 Program 的静态方法，DispGuard 是独立的顶层类 —— 必须写 Program.Log（裸写 Log 是 CS0103）
            if (want == null || want.Count == 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { System.Threading.Thread.Sleep(delayMs); foreach (var line in Restore(want)) Program.Log(line); }
                catch { }
            });
        }

        // 返回每条受影响的结果文案（没被改动的不产生文案）。
        public static List<string> Restore(List<Mode> want)
        {
            var logs = new List<string>();
            foreach (var m in Want(want))
            {
                var cur = Current(m.Dev);
                if (!cur.HasValue) continue;                       // 这屏已经拔了/不存在了
                var c = cur.Value;
                if (c.W == m.W && c.H == m.H && c.Hz == m.Hz && c.Bpp == m.Bpp) continue;
                var dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmPelsWidth = m.W; dm.dmPelsHeight = m.H; dm.dmDisplayFrequency = m.Hz;
                dm.dmBitsPerPel = m.Bpp; dm.dmPositionX = m.X; dm.dmPositionY = m.Y;
                int rc = Native.ChangeDisplaySettingsEx(m.Dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                logs.Add(string.Format("[显示守卫] {0}: 被改成 {1}x{2}@{3}，已装回 {4}x{5}@{6} —— {7}",
                    m.Dev, c.W, c.H, c.Hz, m.W, m.H, m.Hz,
                    rc == DISP_CHANGE_SUCCESSFUL ? "成功" : "失败(rc=" + rc + ")"));
            }
            return logs;
        }

        // 防御：want 若为 null 就给空表，避免 foreach 炸在后台线程里。
        static List<Mode> Want(List<Mode> want) { return want ?? new List<Mode>(); }

        public static Mode? Current(string dev)
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettings(dev, ENUM_CURRENT, ref dm)) return null;
            if (dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) return null;
            return new Mode
            {
                Dev = dev, W = dm.dmPelsWidth, H = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency,
                Bpp = dm.dmBitsPerPel, X = dm.dmPositionX, Y = dm.dmPositionY
            };
        }
    }

    // ============ 显示器设备（设备管理器「监视器」节点）============
    // 用户诉求（2026-09-20）：「我不是说要有启用和禁用的功能吗」—— 指设备管理器里那个
    //   「启用设备 / 禁用设备」。日常要启用，进游戏时按规则禁用指定那一台（他要禁 P27FBB-RG），
    //   退出游戏再启用回来。
    // 为什么必须两套 API 合起来看：
    //   · SetupAPI（这里）= 设备层的真身：实例 ID、型号、是不是"已禁用"。**含被禁用的设备**。
    //   · EnumDisplayDevices = 显示层的挂载情况：挂在桌面上吗、是 \\.\DISPLAYn 里的哪个、是不是主屏。
    //   只枚举被禁用的设备时，它的"显示器子设备"是**空的**（实测：主屏 P27FBB-RG 被禁用后
    //   EnumDisplayDevices(DISPLAY1,0) 直接失败），光靠第二套会以为"这台屏不存在"。
    // ❗ 显示层和 PnP 层不是一回事：实测**禁用主屏的显示器设备后画面照常输出**
    //   （因 EDID 不再读，Windows 只把它当 Generic Non-PnP Monitor）。所以"禁用设备"不等于
    //   "关掉这块屏"，别拿它当关屏开关宣传；用户要的效果是设备管理器里那个状态。
    public class MonDev
    {
        public string InstanceId = "";    // MONITOR\XMIB008\{4d36e96e-...}\0005（设备实例 ID）
        public string Model = "";         // 硬件 ID 型号段：XMIB008 / AOC3402 / DELD0E6（稳定，配置里存这个）
        public string Name = "";          // 显示名：Generic Monitor (P27FBB-RG)
        public string Panel = "";         // 好认的名字：P27FBB-RG（取显示名括号里的串，取不到退 Model）
        // 驱动子键，形如 {4d36e96e-e325-11ce-bfc1-08002be10318}\0006。
        // ★ 它是把"设备层"和"显示层"对上的唯一可靠交集：
        //   SetupAPI 给的实例 ID 是 DISPLAY\XMIB008\5&C86714F&4&UID4355（PnP 实例 ID），
        //   而 EnumDisplayDevices 给的 DeviceID 是 MONITOR\XMIB008\{guid}\0006（显示设备接口 ID）
        //   —— 两者字符串**不相等**，直接比会一台都匹配不上（2026-09-20 探针实测过）。
        //   但显示侧 DeviceID 的**尾部**恰好就是这里这个驱动子键，用 EndsWith 才能对上。
        public string Driver = "";
        public int DevInst;               // CM_* 直调用（cfgmgr32 的 DEVINST）
        public bool Disabled;             // 设备管理器显示「已禁用」
        public string Output = "";        // \\.\DISPLAYn（能关联上时）
        public bool OnDesktop;            // 挂在桌面上
        public bool Primary;              // 主屏

        public string StatusText
        {
            get
            {
                if (Disabled) return "已禁用";
                if (!OnDesktop) return "启用（未使用）";
                return Primary ? "启用 · 主屏" : "启用 · 扩展屏";
            }
        }
        // 面板上一行怎么显示：P27FBB-RG（\\.\DISPLAY1）· 启用 · 主屏
        public string Line
        {
            get
            {
                string o = Output.Length > 0 ? "（" + Output.Replace("\\\\.\\", "") + "）" : "";
                return Panel + o;
            }
        }
    }

    public static class MonMgr
    {
        static readonly Guid MonitorClass = new Guid("4d36e96e-e325-11ce-bfc1-08002be10318");
        const int DIGCF_PRESENT = 0x0002;
        const int SPDRP_FRIENDLYNAME = 0x000C;
        // ⚠ 0x09 才是 SPDRP_DRIVER；0x07 是 SPDRP_CLASS（返回的就是个 "Monitor" 字符串，
        //   拿它当驱动子键去比对会永远匹配不上 —— 2026-09-20 探针实测踩过）
        const int SPDRP_DRIVER = 0x0009;
        const int DIF_PROPERTYCHANGE = 0x00000002;
        const int DICS_ENABLE = 0x0001, DICS_DISABLE = 0x0002;
        const int DICS_FLAG_GLOBAL = 0x0001;
        const int DICS_FLAG_CONFIGSPECIFIC = 0x0002;
        const int DN_HAS_PROBLEM = 0x00000400, CM_PROB_DISABLED = 22;

        // 枚举当前**在场**的显示器设备（含被禁用的；不包含已经拔掉的幽灵实例）
        public static List<MonDev> All()
        {
            var list = new List<MonDev>();
            IntPtr set = IntPtr.Zero;
            try
            {
                // ⚠ 必须拷到局部变量再取地址：static readonly 字段不能作为 ref 实参（CS0199）
                Guid cls = MonitorClass;
                set = Native.SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
                var di = new Native.SP_DEVINFO_DATA();
                di.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref di); i++)
                {
                    var d = new MonDev();
                    d.InstanceId = InstanceIdOf(set, ref di);
                    d.Model = ModelOf(d.InstanceId);
                    d.Name = Str(set, ref di, SPDRP_FRIENDLYNAME);
                    d.Panel = PanelOf(d.Name, d.Model);
                    d.Driver = Str(set, ref di, SPDRP_DRIVER);
                    d.DevInst = di.DevInst;
                    int st, prob;
                    if (Native.CM_Get_DevNode_Status(out st, out prob, di.DevInst, 0) == 0)
                        d.Disabled = (st & DN_HAS_PROBLEM) != 0 && prob == CM_PROB_DISABLED;
                    list.Add(d);
                }
            }
            catch { }
            finally { Release(set); }

            // 合上显示层：谁挂在桌面上 / 是哪个 \\.\DISPLAYn / 是不是主屏
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & 0x1) == 0) continue;              // 没接桌面
                    var mon = new Native.DISPLAY_DEVICE();
                    mon.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0)) continue;   // 设备被禁用时就枚举不到
                    foreach (MonDev d in list)
                    {
                        // ⚠ 这里**不能**拿 d.InstanceId 直接和 mon.DeviceID 比：那两个字符串永远不相等
                        //   （PnP 实例 ID "DISPLAY\XMIB008\5&C86714F&4&UID4355" vs
                        //    显示设备接口 ID "MONITOR\XMIB008\{guid}\0006"）。
                        //   能对上的只有尾部那个驱动子键 —— 2026-09-20 探针实测：直接比 → 0 台关联成功，
                        //   改比驱动子键 → 全部正确关联。
                        if (d.Driver.Length == 0) continue;
                        if (mon.DeviceID == null || !mon.DeviceID.EndsWith(d.Driver, StringComparison.OrdinalIgnoreCase))
                            continue;
                        d.Output = dd.DeviceName;
                        d.OnDesktop = true;
                        d.Primary = (dd.StateFlags & 0x4) != 0;
                    }
                }
            }
            catch { }
            return list;
        }

        // 按型号找（配置里存的是型号，插拔换口后实例 ID 会变，型号不变）
        public static List<MonDev> ByModel(string model)
        {
            var r = new List<MonDev>();
            if (model == null || model.Length == 0) return r;
            foreach (MonDev d in All())
                if (string.Equals(d.Model, model, StringComparison.OrdinalIgnoreCase)) r.Add(d);
            return r;
        }

        // 设备管理器右键「禁用设备 / 启用设备」的等价调用。
        // 2026-09-23 用户机器上这条路返回了失败（日志：「禁用显示器 P27FBB-RG（DISPLAY1） 失败」），
        //   而且老的实现**把失败原因吞了**（只返回 false，不看 GetLastError）⇒ 只能盲猜。
        //   所以这里改成：三条路依次试，谁先成功用谁；全失败就把每一条的错误码摊在 LastError 里，
        //   由调用方写进日志。三条都是公开 API，不是野路子：
        //     ① SetupAPI + DICS_FLAG_GLOBAL        —— 设备管理器默认勾选"应用于所有硬件配置文件"
        //     ② SetupAPI + DICS_FLAG_CONFIGSPECIFIC —— 只作用于当前硬件配置文件（显示器这类按配置走的设备常常只认它）
        //     ③ CM_Disable_DevNode / CM_Enable_DevNode —— cfgmgr32 直调，绕开类安装器
        public static string LastError = "";

        // 成功只代表"调用被接受"；再问一次设备的真实状态，避免又出现
        //   「日志说已禁用、设备管理器里还启用着」这种假成功。问不到状态就不推翻结论。
        static bool StateIs(MonDev d, bool wantDisabled)
        {
            try
            {
                int st, prob;
                if (Native.CM_Get_DevNode_Status(out st, out prob, d.DevInst, 0) != 0) return true;
                bool dis = (st & DN_HAS_PROBLEM) != 0 && prob == CM_PROB_DISABLED;
                return dis == wantDisabled;
            }
            catch { return true; }
        }

        public static bool SetEnabled(MonDev d, bool on)
        {
            LastError = "";
            if (d == null || d.InstanceId.Length == 0) return false;
            bool want = !on;                  // 目标状态：禁用？
            var errs = new List<string>();

            // ①② SetupAPI
            IntPtr set = IntPtr.Zero;
            try
            {
                Guid cls = MonitorClass;      // 同上：static readonly 不能直接 ref（CS0199）
                set = Native.SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) errs.Add("SetupDiGetClassDevs 失败");
                else
                {
                    var di = new Native.SP_DEVINFO_DATA();
                    di.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                    for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref di); i++)
                    {
                        if (!string.Equals(InstanceIdOf(set, ref di), d.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                        int[] scopes = new int[] { DICS_FLAG_GLOBAL, DICS_FLAG_CONFIGSPECIFIC };
                        string[] names = new string[] { "GLOBAL", "CONFIGSPECIFIC" };
                        for (int s = 0; s < scopes.Length; s++)
                        {
                            var pcp = new Native.SP_PROPCHANGE_PARAMS();
                            pcp.ClassInstallHeader.cbSize = Marshal.SizeOf(typeof(Native.SP_CLASSINSTALL_HEADER));
                            pcp.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
                            pcp.StateChange = on ? DICS_ENABLE : DICS_DISABLE;
                            pcp.Scope = scopes[s];
                            pcp.HwProfile = 0;
                            bool ok = Native.SetupDiSetClassInstallParams(set, ref di, ref pcp,
                                          Marshal.SizeOf(typeof(Native.SP_PROPCHANGE_PARAMS)))
                                   && Native.SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref di);
                            if (ok && StateIs(d, want)) return true;
                            errs.Add("SetupAPI/" + names[s] + (ok ? "=调用成功但状态未变" : "=" + Marshal.GetLastWin32Error()));
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) { errs.Add("SetupAPI 异常:" + ex.GetType().Name); }
            finally { Release(set); }

            // ③ cfgmgr32 直调（返回 CONFIGRET，CR_SUCCESS = 0）
            try
            {
                int cr = on ? Native.CM_Enable_DevNode(d.DevInst, 0) : Native.CM_Disable_DevNode(d.DevInst, 0);
                if (cr == 0 && StateIs(d, want)) return true;
                errs.Add("CM_" + (on ? "Enable" : "Disable") + "_DevNode="
                       + (cr == 0 ? "调用成功但状态未变" : "CR" + cr));
            }
            catch (Exception ex) { errs.Add("cfgmgr32 异常:" + ex.GetType().Name); }

            LastError = string.Join(" / ", errs.ToArray());
            return false;
        }

        // 一台都不许留成禁用（兜底按钮用）：返回实际改动了几台
        public static int EnableAll()
        {
            int n = 0;
            try
            {
                foreach (MonDev d in All())
                    if (d.Disabled && SetEnabled(d, true)) n++;
            }
            catch { }
            return n;
        }

        // 按型号禁用一批（进游戏用）；返回成功停用的台数
        public static int DisableModels(string[] models)
        {
            int n = 0;
            if (models == null) return 0;
            try
            {
                foreach (string m in models)
                {
                    if (m == null || m.Trim().Length == 0) continue;
                    foreach (MonDev d in ByModel(m.Trim()))
                        if (!d.Disabled && SetEnabled(d, false)) n++;
                }
            }
            catch { }
            return n;
        }

        // 按型号启用一批（退出游戏用）
        public static int EnableModels(string[] models)
        {
            int n = 0;
            if (models == null) return 0;
            try
            {
                foreach (string m in models)
                {
                    if (m == null || m.Trim().Length == 0) continue;
                    foreach (MonDev d in ByModel(m.Trim()))
                        if (d.Disabled && SetEnabled(d, true)) n++;
                }
            }
            catch { }
            return n;
        }

        // 型号清单 <-> 配置串（逗号分隔）
        public static string Join(string[] models)
        {
            if (models == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (string m in models)
            {
                if (m == null || m.Trim().Length == 0) continue;
                if (sb.Length > 0) sb.Append(",");
                sb.Append(m.Trim());
            }
            return sb.ToString();
        }
        public static string[] SplitModels(string s)
        {
            if (s == null || s.Trim().Length == 0) return new string[0];
            var l = new List<string>();
            foreach (string x in s.Split(','))
            {
                string t = x.Trim();
                if (t.Length > 0) l.Add(t);
            }
            return l.ToArray();
        }

        static void Release(IntPtr set)
        {
            try { if (set != IntPtr.Zero && set != new IntPtr(-1)) Native.SetupDiDestroyDeviceInfoList(set); } catch { }
        }
        static string InstanceIdOf(IntPtr set, ref Native.SP_DEVINFO_DATA di)
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                int need;
                if (Native.SetupDiGetDeviceInstanceId(set, ref di, sb, sb.Capacity, out need)) return sb.ToString();
            }
            catch { }
            return "";
        }
        static string Str(IntPtr set, ref Native.SP_DEVINFO_DATA di, int prop)
        {
            try
            {
                var buf = new byte[1024];
                int type, need;
                if (Native.SetupDiGetDeviceRegistryProperty(set, ref di, prop, out type, buf, buf.Length, out need))
                    return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, need - 2)).TrimEnd('\0');
            }
            catch { }
            return "";
        }
        // MONITOR\XMIB008\{guid}\0005 → XMIB008
        static string ModelOf(string instanceId)
        {
            try
            {
                string[] p = instanceId.Split('\\');
                return p.Length >= 2 ? p[1] : "";
            }
            catch { return ""; }
        }
        // "Generic Monitor (P27FBB-RG)" → P27FBB-RG
        static string PanelOf(string name, string model)
        {
            try
            {
                int a = name.LastIndexOf('('), b = name.LastIndexOf(')');
                if (a >= 0 && b > a) return name.Substring(a + 1, b - a - 1).Trim();
            }
            catch { }
            return model;
        }
    }

    // ============ 游戏分辨率联动（v3.7.0） ============
    // 起因：用户主屏 27" 1080p@165，打 CS2 用 1440x1080（严格 4:3，纵向原生像素）、
    //   打瓦罗兰特用 1568x1080（瓦要先禁用副屏才切得动 —— 复制模式下该档不生效）。
    //   手动来回切太烦，要求：进游戏自动切、退出自动回 1920x1080@165。
    // 设计要点：
    //   ① 进游戏应用的是**临时模式**（ChangeDisplaySettingsEx flags=0，不写注册表）——
    //     好处：工具崩了 / 蓝屏了，重启系统自动回到注册表里的 1920x1080@165，绝不会"永远糊着"。
    //   ② 禁用副屏用 ChangeDisplaySettingsEx(dev, NULL) 分离；**只复原"是我们禁用的"那几块** ——
    //     分离前先记快照，用户本来就禁用着的屏（枚举里根本不存在）不会碰，退出后也不擅自启用。
    //   ③ 生效期间 DispGuard（显示守卫）让位：否则远控暂停的快照会把游戏分辨率记成"要保护的模式"，
    //     退出时两边抢着改分辨率（PauseAllRemote / ResumeAllRemote 里有对应让位逻辑）。
    public class ResLinkRule
    {
        public string Proc = "";
        public int W = 1920, H = 1080, Hz = 165;
        public bool Detach = false;
        // 选中的档位 id（见 ResLink.PresetsFor）。空串 = 自定义 —— 分辨率不在这份内置档位表里。
        // 为什么除了 W/H 还要单独记 id：用户换屏后同一个 w/h 可能已不在新屏支持列表里，
        //   记下 id 才能在界面上稳定回显"你选的是哪一档"，并据此提示"这一档在你现在的屏上不可用"。
        public string Preset = "";
        // 进游戏时要用**设备管理器**方式禁用的显示器（型号串，逗号分隔，如 "XMIB008"；
        //   型号取自硬件 ID 第二段，插拔换口后实例 ID 变、型号不变，所以配置里存型号）。
        // 为什么单独一个字段而不复用 Detach：Detach = 把屏从桌面摘掉（显示配置层，设备管理器里
        //   仍显示"正常运行"，重启即回原生）；这个是 PnP 层（设备管理器右键「禁用设备」）。
        //   2026-09-20 用户明确要的是后者，并且要按型号只禁指定的那一台。
        public string OffMons = "";
        public ResLinkRule() { }
        public ResLinkRule(string proc, int w, int h, int hz, bool detach)
        { Proc = proc; W = w; H = h; Hz = hz; Detach = detach; }
    }

    // ---- 分辨率档位表（v3.8.0）----
    // 起因（2026-09-20 用户诉求）：CS2 / 无畏契约的游戏分辨率原本是**写死的一条**，
    //   用户一旦换显示器（换尺寸 / 换分辨率 / 换刷新率），写死的那一条未必还合适，
    //   而程序里没有第二个选项 —— 只能手动改 config.json。
    // 做法："这两个游戏的玩家实际会选哪些分辨率"做成档位表，界面上可选、可回显，
    //   并按本机主屏**实际支持的模式**标注可用性（换屏后一眼能看出哪一档需要先建自定义分辨率）。
    // 数据来源（2026-09 联网核对，不是猜的）：
    //   · CS2：proconfig.net 跟踪的 131 名现役职业选手分辨率分布 —— 1280x960 占 60.3%、
    //     1920x1080 10.7%、1024x768 9.9%、1280x1024 9.2%、1680x1050 2.3%、1440x1080 1.5%、
    //     1350x1080 1.5%、1152x864 0.8%；take.skin（2026-08）复核一致，4:3 合计 >75%。
    //   · 无畏契约：proSettings 640+ 职业选手数据库（绝大多数用 1920x1080 原生）+
    //     Riot 官方支持分辨率列表（含 1440x1080 / 1280x960）+ setup.gg、tier1settings 的拉伸档位指南。
    //   ⚠ 1080p 屏上的 1280x960 / 1440x1080 / 1568x1080 这类多半不是 EDID 里的标准模式，
    //     需要先在显卡驱动里建好"自定义分辨率"才切得动（界面按本机屏实际支持的模式标注）。
    public class ResPreset
    {
        public string Id = "";      // 稳定标识，写回配置（displayLink.rules[].preset）
        public int W, H;
        public string Ratio = "";   // 比例与拉伸方式
        public string Note = "";    // 一句话说明（含量化依据）
        public ResPreset(string id, int w, int h, string ratio, string note)
        { Id = id; W = w; H = h; Ratio = ratio; Note = note; }
    }

    public static class ResLink
    {
        const int ENUM_REGISTRY = -2;
        const int DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000,
                  DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        const int CDS_UPDATEREGISTRY = 0x1;
        const int ATTACHED_TO_DESKTOP = 0x1, PRIMARY_DEVICE = 0x4;
        const int DISP_CHANGE_SUCCESSFUL = 0;

        // 联动是否生效中（LinkGameExit / 工具退出时据此还原）
        public static bool Active = false;
        // 分离前记下"当时还挂在桌面上"的副屏 —— 退出时**只**把这几块接回来
        static List<DispGuard.Mode> savedOthers = new List<DispGuard.Mode>();
        // 本次联动里**由本程序禁用**的显示器型号（设备管理器那一层）—— 退出时只把这几台开回来。
        // 用户自己手动禁用的屏不在此列，绝不擅自替他改（同 savedOthers 的道理）。
        static List<string> offedModels = new List<string>();
        static string curKey = "";

        // 规则匹配：进程名前缀（忽略大小写）—— 配置里写 "cs2" 命中 cs2，写 "VALORANT" 命中 VALORANT-Win64-Shipping
        public static ResLinkRule RuleFor(string proc)
        {
            if (proc == null || proc.Length == 0 || Program.Cfg == null) return null;
            foreach (ResLinkRule r in Program.Cfg.ResRules)
            {
                if (r == null || r.Proc == null || r.Proc.Length == 0) continue;
                if (proc.StartsWith(r.Proc, StringComparison.OrdinalIgnoreCase)) return r;
            }
            return null;
        }

        // ---- 档位表 ----
        // CS2：按职业选手使用率降序（proconfig.net, n=131）
        static readonly ResPreset[] CsPresets = new ResPreset[]
        {
            new ResPreset("cs-1280x960",  1280,  960, "4:3 拉伸", "职业最主流（60.3%）：敌人模型更宽，像素负载最轻"),
            new ResPreset("cs-1920x1080", 1920, 1080, "16:9 原生", "原生全高清（10.7%）：最清晰、横向视野最宽"),
            new ResPreset("cs-1024x768",  1024,  768, "4:3 拉伸", "经典低解（9.9%）：帧率最高，画面最糊"),
            new ResPreset("cs-1280x1024", 1280, 1024, "5:4",      "纵向最高（9.2%）：准星区更高，横向稍窄"),
            new ResPreset("cs-1680x1050", 1680, 1050, "16:10",    "轻度拉伸（2.3%）：介于 4:3 与原生之间"),
            new ResPreset("cs-1440x1080", 1440, 1080, "4:3 拉伸", "保住全部 1080 纵向像素（1.5%），比 1280x960 清晰"),
            new ResPreset("cs-1350x1080", 1350, 1080, "4:3 拉伸", "小众 4:3（1.5%）"),
            new ResPreset("cs-1152x864",  1152,  864, "4:3 拉伸", "更低负载的 4:3（0.8%）"),
        };

        // 无畏契约：职业主流是原生 1080p；4:3 拉伸为个人偏好（Riot 无原生拉伸，靠 GPU 全屏缩放）
        static readonly ResPreset[] ValPresets = new ResPreset[]
        {
            new ResPreset("val-1920x1080", 1920, 1080, "16:9 原生", "职业主流：640+ 选手数据库里绝大多数用原生 1080p"),
            new ResPreset("val-1440x1080", 1440, 1080, "4:3 拉伸", "最锐利的 4:3：保住全部 1080 纵向像素，Riot 官方支持"),
            new ResPreset("val-1280x960",  1280,  960, "4:3 拉伸", "最流行的拉伸档：准星与 HUD 看起来更宽，画面偏软"),
            // 1568x1080 是用户本机原设置（1568/1080≈1.452，最接近 16:11，不是标准比例）
            new ResPreset("val-1568x1080", 1568, 1080, "约 16:11",  "本机原设置档：横向比 1440x1080 宽一点，纵向仍是原生 1080"),
            new ResPreset("val-1680x1050", 1680, 1050, "16:10",     "轻度拉伸：不想画面太糊时的首选"),
            new ResPreset("val-1600x900",  1600,  900, "16:9",      "掉帧时的低成本方案：不拉伸，只降像素"),
            new ResPreset("val-1024x768",  1024,  768, "4:3 拉伸",  "极致帧率：低配机器才需要"),
        };

        // 一个游戏该看哪张表：按进程名前缀认（VALORANT* / cs2*）
        public static ResPreset[] PresetsFor(string proc)
        {
            if (proc == null) return CsPresets;
            if (proc.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase)) return ValPresets;
            return CsPresets;
        }

        // 按 id 找档位（找不到 / id 为空 → null = 自定义）
        public static ResPreset FindPreset(string proc, string id)
        {
            if (id == null || id.Length == 0) return null;
            foreach (ResPreset p in PresetsFor(proc)) if (p.Id == id) return p;
            return null;
        }

        // 按 W/H 反查档位（老配置没写 preset 时用；也用于"用户手改了 config.json"的兜底）
        public static ResPreset MatchPreset(string proc, int w, int h)
        {
            foreach (ResPreset p in PresetsFor(proc)) if (p.W == w && p.H == h) return p;
            return null;
        }

        // 规则当前对应的档位（null = 自定义）。preset 不认时退回按 w/h 反查 —— 换屏/手改都不会失配。
        public static ResPreset PresetOf(ResLinkRule r)
        {
            if (r == null) return null;
            ResPreset p = FindPreset(r.Proc, r.Preset);
            if (p != null && p.W == r.W && p.H == r.H) return p;
            return MatchPreset(r.Proc, r.W, r.H);
        }

        // ---- 下拉标签与索引 ----
        // 刻意放在 ResLink 而不是 UI 里：探针能直接验证"标签 ↔ 索引 ↔ 档位"三者对齐，
        //   而 UI 层只负责把标签填进下拉、把索引换算回档位（不再各写一套映射）。
        // 标签前缀 = 本机可用性：✓ 直接可切 / △ 需先建自定义分辨率 / ✕ 超出主屏
        public static string PresetLabel(ResPreset p)
        {
            if (p == null) return "";
            int a = Availability(p);
            return (a == 0 ? "✓" : (a == 1 ? "△" : "✕")) + " " + p.W + "×" + p.H + "（" + p.Ratio + "）";
        }

        // 下拉项 = 整张档位表；当前值不在表里时末尾补一项"自定义·当前值"
        //   （用户手改过 config.json 时不至于在下拉里找不到自己现在的值）
        public static List<string> ComboLabels(string proc, int curW, int curH)
        {
            var list = new List<string>();
            ResPreset[] ps = PresetsFor(proc);
            foreach (ResPreset p in ps) list.Add(PresetLabel(p));
            if (MatchPreset(proc, curW, curH) == null)
                list.Add("● " + curW + "×" + curH + "（自定义·当前值）");
            return list;
        }

        // 当前值对应下拉里的第几项
        public static int ComboIndexOf(string proc, int curW, int curH)
        {
            ResPreset[] ps = PresetsFor(proc);
            for (int i = 0; i < ps.Length; i++) if (ps[i].W == curW && ps[i].H == curH) return i;
            return ps.Length;          // 与 ComboLabels 的追加位置一致
        }

        // 下拉第 i 项对应的档位。返回 null = "自定义项"或越界 → 调用方不该改动任何东西
        public static ResPreset PresetAt(string proc, int i)
        {
            ResPreset[] ps = PresetsFor(proc);
            if (i < 0 || i >= ps.Length) return null;
            return ps[i];
        }

        // ---- 本机主屏能力 ----
        // 返回主屏 EDID/驱动报告的**全部模式**：界面据此标注"这一档现在切得动吗"。
        // 为什么要这道判断：1280x960 / 1440x1080 这类在 1080p 屏上多半不是标准模式，
        //   得先在显卡驱动里建自定义分辨率；换屏之后原来的档位也可能整档失效。
        //   不标出来，用户只能等到"进游戏没反应"才知道（apply 里只会留一条 rc!=0 的日志）。
        // 主屏模式列表缓存：界面一次要标注十几个档位，不缓存就要枚举十几遍（每遍几十次 P/Invoke）。
        // 换屏 / 改分辨率后由界面调 InvalidateModes() 失效。
        static List<DispGuard.Mode> modesCache = null;
        public static void InvalidateModes() { modesCache = null; }
        static List<DispGuard.Mode> Modes()
        {
            if (modesCache == null) modesCache = MainScreenModes();
            return modesCache;
        }

        public static List<DispGuard.Mode> MainScreenModes()
        {
            var list = new List<DispGuard.Mode>();
            string dev = PrimaryDevice();
            if (dev == null) return list;
            try
            {
                for (int i = 0; i < 512; i++)
                {
                    var dm = new Native.DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                    if (!Native.EnumDisplaySettings(dev, i, ref dm)) break;
                    if (dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) continue;
                    bool dup = false;
                    foreach (var m in list) if (m.W == dm.dmPelsWidth && m.H == dm.dmPelsHeight) { dup = true; break; }
                    if (dup) continue;
                    list.Add(new DispGuard.Mode { Dev = dev, W = dm.dmPelsWidth, H = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency });
                }
            }
            catch { }
            return list;
        }

        // 主屏是否支持该分辨率（枚举不到任何模式时返回 true —— 探测失败不该误报"不可用"）
        public static bool MainScreenSupports(int w, int h)
        {
            List<DispGuard.Mode> ms = Modes();
            if (ms.Count == 0) return true;
            foreach (var m in ms) if (m.W == w && m.H == h) return true;
            return false;
        }

        // 主屏的最大宽/高（用于区分"超出屏幕"与"需要建自定义分辨率"）
        public static void MainScreenMax(out int maxW, out int maxH)
        {
            maxW = 0; maxH = 0;
            foreach (var m in Modes())
            {
                if (m.W > maxW) maxW = m.W;
                if (m.H > maxH) maxH = m.H;
            }
        }

        // 档位在本机的可用性：0=可用  1=需先建自定义分辨率  2=超出主屏最大能力
        public static int Availability(ResPreset p)
        {
            if (p == null) return 0;
            int mw, mh; MainScreenMax(out mw, out mh);
            if (mw > 0 && (p.W > mw || p.H > mh)) return 2;
            return MainScreenSupports(p.W, p.H) ? 0 : 1;
        }

        // 枚举当前挂在桌面上的屏（\\.\DISPLAYn + StateFlags）
        public static List<string> AttachedDevices()
        {
            var list = new List<string>();
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & ATTACHED_TO_DESKTOP) != 0 && !list.Contains(dd.DeviceName))
                        list.Add(dd.DeviceName);
                }
            }
            catch { }
            return list;
        }

        public static string PrimaryDevice()
        {
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & ATTACHED_TO_DESKTOP) != 0 && (dd.StateFlags & PRIMARY_DEVICE) != 0)
                        return dd.DeviceName;
                }
            }
            catch { }
            var all = AttachedDevices();
            return all.Count > 0 ? all[0] : null;
        }

        public static bool IsDetached(string dev)
        {
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if (string.Equals(dd.DeviceName, dev, StringComparison.OrdinalIgnoreCase))
                        return (dd.StateFlags & ATTACHED_TO_DESKTOP) == 0;
                }
            }
            catch { }
            return false;
        }

        // 进游戏：切主屏到规则模式（+按规则分离其他屏）。返回日志文案（空串=无需动作）。
        public static string Apply(ResLinkRule r)
        {
            if (r == null || r.W <= 0 || r.H <= 0 || r.Hz <= 0) return "";
            string key = r.Proc + "|" + r.W + "x" + r.H + "@" + r.Hz + "|" + r.Detach + "|" + r.OffMons;
            if (Active && key == curKey) return "";   // 同一游戏重复触发（列表刷新）→ 不动
            if (Active) Restore();                     // 换了游戏（极少见）：先把上一局还原干净

            var logs = new List<string>();
            savedOthers = new List<DispGuard.Mode>();
            offedModels.Clear();

            // ① 分离其他屏（仅规则要求时）。已禁用的屏不在 AttachedDevices 里 → 天然"检测是否已禁用"，
            //    用户手动禁用着的副屏不会被记进 savedOthers，退出时也就不会被擅自启用。
            if (r.Detach)
            {
                string primary = PrimaryDevice();
                foreach (string dev in AttachedDevices())
                {
                    if (primary != null && dev == primary) continue;
                    DispGuard.Mode? before = DispGuard.Current(dev);
                    int rc = Native.ChangeDisplaySettingsDetach(dev, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    if (rc == DISP_CHANGE_SUCCESSFUL)
                    {
                        if (before.HasValue) savedOthers.Add(before.Value);
                        logs.Add("已禁用副屏 " + dev);
                    }
                    else logs.Add("禁用副屏 " + dev + " 失败(rc=" + rc + ")");
                }
            }

            // ①.5 按规则在**设备管理器**层面停用指定的显示器（2026-09-20 用户要的就是这个：
            //     「启动游戏时再禁用、只禁用指定那一台」）。
            //     必须排在切分辨率**之前**：停用设备会让 Windows 重排显示配置（主屏可能换人），
            //     等它排完再切，才不会把模式切到一块刚被拿掉的屏上。
            //     ⚠ 只记"本来启用的"——已经手动禁用着的屏不算我们干的，退出时不替用户开回来。
            if (r.OffMons != null && r.OffMons.Trim().Length > 0)
            {
                string[] want = MonMgr.SplitModels(r.OffMons);
                var seen = new List<string>();
                foreach (MonDev d in MonMgr.All())
                {
                    if (d.Disabled) continue;
                    bool hit = false;
                    foreach (string m in want)
                        if (string.Equals(d.Model, m, StringComparison.OrdinalIgnoreCase)) hit = true;
                    if (!hit) continue;
                    // 默认不禁用桌面上唯一在用的屏（停掉 = 黑屏）。但单屏拉伸恰恰要禁这一台 ——
                    //  用户在设置页开了「允许禁用唯一在用屏」才放行（退出游戏照样自动启用回来）。
                    if (d.OnDesktop && AttachedDevices().Count <= 1
                        && !(Program.Cfg != null && Program.Cfg.ResLinkSoloOff))
                    {
                        logs.Add("跳过禁用 " + d.Panel + "：它是当前唯一在用的屏，禁用会黑屏"
                               + "（要单屏拉伸请在设置页开「允许禁用唯一在用屏」）");
                        continue;
                    }
                    if (MonMgr.SetEnabled(d, false))
                    {
                        if (!seen.Contains(d.Model)) { seen.Add(d.Model); offedModels.Add(d.Model); }
                        logs.Add("已禁用显示器 " + d.Line
                                 + (d.Primary ? "（它原来是主屏，桌面已转到另一台屏）" : ""));
                    }
                    else logs.Add("禁用显示器 " + d.Line + " 失败（" + MonMgr.LastError + "）");
                }
                // 等 PnP 把显示配置重排完（同步调用返回后系统还要几百毫秒才稳定）
                if (seen.Count > 0) { try { System.Threading.Thread.Sleep(400); } catch { } }
            }

            // ② 主屏切目标模式。flags=0 = 临时模式：注册表默认仍是日常模式（1920x1080@165），
            //    工具崩了/系统重启都会自动回原生，不会把用户锁在 4:3 里。
            string main = PrimaryDevice();
            if (main == null) { Active = true; curKey = key; return Join(logs); }

            // 刷新率**跟随当前主屏正在用的 Hz**，而不是配置里写死的那一个（v3.8.0）：
            //   用户换 240Hz 屏后不用再改 config.json —— 写死的 165 在新屏上要么被驱动降档、
            //   要么直接 rc!=0 切不动。只有读不到当前模式时才回退到配置里的 Hz。
            int hz = r.Hz;
            DispGuard.Mode? curMain = DispGuard.Current(main);
            if (curMain.HasValue && curMain.Value.Hz > 0) hz = curMain.Value.Hz;

            Native.DEVMODE dm = new Native.DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            dm.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            dm.dmBitsPerPel = 32;
            dm.dmPelsWidth = r.W; dm.dmPelsHeight = r.H; dm.dmDisplayFrequency = hz;
            int rcMain = Native.ChangeDisplaySettingsEx(main, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (rcMain == DISP_CHANGE_SUCCESSFUL) logs.Add("主屏已切 " + r.W + "x" + r.H + "@" + hz);
            else
            {
                // 切不动时给一句"为什么"：这类非标准分辨率（1280x960 / 1440x1080 …）多半没在
                //   显卡驱动里建过自定义分辨率 —— 只丢一个 rc 号，用户不知道下一步该做什么。
                string why = MainScreenSupports(r.W, r.H) ? "" : "（本机主屏没有这一档，需先在显卡驱动里建自定义分辨率）";
                logs.Add("主屏切 " + r.W + "x" + r.H + "@" + hz + " 失败(rc=" + rcMain + ")，保持原模式" + why);
            }

            Active = true; curKey = key;
            return Join(logs);
        }

        // 退出游戏：主屏回 Cfg 的基础模式（默认 1920x1080@165），再把"我们禁用的"副屏接回来。
        public static List<string> Restore()
        {
            var logs = new List<string>();
            if (!Active && savedOthers.Count == 0 && offedModels.Count == 0) return logs;

            // ⓪ 先在设备管理器里启用"我们禁用的"显示器。必须排在所有模式操作之前 ——
            //    设备处于停用状态时给它设模式是设不进去的，得先让它回到桌面。
            if (offedModels.Count > 0)
            {
                var done = new List<string>();
                foreach (string m in offedModels)
                {
                    if (done.Contains(m)) continue;
                    done.Add(m);
                    int n = MonMgr.EnableModels(new string[] { m });
                    logs.Add("显示器 " + m + (n > 0 ? " 已启用" : " 已启用（本来就开着）"));
                }
                offedModels.Clear();
                try { System.Threading.Thread.Sleep(400); } catch { }   // 等 PnP 把屏接回桌面
            }

            // ① 接回副屏：优先用注册表记忆的模式（ENUM_REGISTRY），读不到就用分离前的快照
            foreach (DispGuard.Mode m in savedOthers)
            {
                Native.DEVMODE dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                bool got = false;
                try { got = Native.EnumDisplaySettings(m.Dev, ENUM_REGISTRY, ref dm); } catch { }
                if (!got || dm.dmPelsWidth <= 0)
                {
                    dm = new Native.DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                    dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                    dm.dmBitsPerPel = m.Bpp; dm.dmPelsWidth = m.W; dm.dmPelsHeight = m.H;
                    dm.dmDisplayFrequency = m.Hz; dm.dmPositionX = m.X; dm.dmPositionY = m.Y;
                }
                int rc = Native.ChangeDisplaySettingsEx(m.Dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                logs.Add("副屏 " + m.Dev + " 已恢复" + (rc == DISP_CHANGE_SUCCESSFUL ? "" : "(rc=" + rc + ")"));
            }
            savedOthers = new List<DispGuard.Mode>();

            // ② 主屏回基础模式（同样用 flags=0？不 —— 这里用 CDS_UPDATEREGISTRY：
            //    注册表里的日常模式本来就是它，写进去 = 幂等，顺带把"中途被谁改过注册表"也摆正）
            string main = PrimaryDevice();
            if (main != null)
            {
                Native.DEVMODE dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                dm.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmBitsPerPel = 32;
                dm.dmPelsWidth = Program.Cfg.BaseW; dm.dmPelsHeight = Program.Cfg.BaseH;
                dm.dmDisplayFrequency = Program.Cfg.BaseHz;
                DispGuard.Mode? cur = DispGuard.Current(main);
                if (cur.HasValue && cur.Value.W == dm.dmPelsWidth && cur.Value.H == dm.dmPelsHeight
                    && cur.Value.Hz == dm.dmDisplayFrequency)
                {
                    // 已经在基础模式（比如游戏里用户手动切回来了）→ 不再折腾
                }
                else
                {
                    int rc = Native.ChangeDisplaySettingsEx(main, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
                    logs.Add("主屏已还原 " + dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency
                             + (rc == DISP_CHANGE_SUCCESSFUL ? "" : "(rc=" + rc + ")"));
                }
            }
            Active = false; curKey = "";
            return logs;
        }

        static string Join(List<string> logs)
        {
            var kept = new List<string>();
            foreach (string s in logs) if (s != null && s.Length > 0) kept.Add(s);
            return kept.Count > 0 ? string.Join(" · ", kept.ToArray()) : "";
        }
    }

    public class RemoteApp
    {
        public string Name = "GameViewer";
        public List<string> Processes = new List<string>();
        public List<string> Services = new List<string>();
        public string Exe = "";
    }

    // ============ NVAPI 驱动配置（per-game 显卡设置自动化） ============
    // 实现参照开源项目 Ryujinx 的 NVThreadedOptimization（生产环境多年验证），
    // QueryInterface/DRS 函数 ID 与设置 ID 均核对自 NVIDIA 官方开源仓库 nvapi 的 NvApiDriverSettings.h
    public static unsafe class NvDrs
    {
        const uint NvAPI_Initialize_ID = 0x0150E828;
        const uint NvAPI_DRS_CreateSession_ID = 0x0694D52E;
        const uint NvAPI_DRS_LoadSettings_ID = 0x375DBD6B;
        const uint NvAPI_DRS_FindProfileByName_ID = 0x7E4A9A0B;
        const uint NvAPI_DRS_FindApplicationByName_ID = 0xEEE566B2;
        const uint NvAPI_DRS_CreateProfile_ID = 0x0CC176068;
        const uint NvAPI_DRS_CreateApplication_ID = 0x4347A9DE;
        const uint NvAPI_DRS_SetSetting_ID = 0x577DD202;
        const uint NvAPI_DRS_SaveSettings_ID = 0xFCBC7E14;
        const uint NvAPI_DRS_DestroySession_ID = 0x0DAD9CFF8;

        const string ProfileName = "GameBoost";

        // 每游戏写入的驱动设置（ID/取值全部核对自官方 NvApiDriverSettings.h）
        // 竞技档（最低输入延迟优先）
        // 2026-09-19 补着色器缓存两条：此前只有 3A 档写了，竞技档被漏掉。
        //   实测症状是「买枪菜单第一次打开卡一下」——首次出现的材质组合要现场编译
        //   DXBC→SASS，缓存命中后第二次就不卡；缓存上限太低则驱动淘汰后又复发。
        //   着色器缓存治的正是「首次」与「复发」，与输入延迟无关，两档都该有。
        // v3.6.0：这里原来是两个 static readonly 硬编码数组。改成按配置取值 ——
        // 体检表能显示"期望值"，但期望值是程序写死的，用户看得见、改不了；而着色器缓存上限
        // （12 档）、DLSS 强制预设字母、垂直同步模式这些，最优值本来就因游戏/因驱动版本而异。
        // 默认值刻意与原硬编码完全一致，所以行为零变化。
        public static uint[][] CompSettings()
        {
            var c = Program.Cfg;
            return new uint[][]
            {
                new uint[] { 0x1057EB71, (uint)c.NvCompPowerMode },                              // 电源管理模式
                new uint[] { 0x007BA09E, (uint)c.NvCompPreRender },                              // 最大预渲染帧数
                new uint[] { 0x00CE2691, unchecked((uint)c.NvCompTexQuality) },                  // 纹理过滤质量
                new uint[] { 0x00A879CF, unchecked((uint)c.NvCompVsync) },                       // 垂直同步
                new uint[] { NvDrsDb.IdShaderCacheEnable, unchecked((uint)c.NvShaderCacheOn) },  // 着色器缓存开关
                new uint[] { NvDrsDb.IdShaderCacheSize, unchecked((uint)c.NvShaderCacheSize) },  // 着色器缓存上限
            };
        }

        // 3A / MMO / 二游档（画质与帧率稳定优先）
        public static uint[][] AaaSettings()
        {
            var c = Program.Cfg;
            return new uint[][]
            {
                new uint[] { 0x1057EB71, (uint)c.NvCompPowerMode },
                new uint[] { NvDrsDb.IdShaderCacheEnable, unchecked((uint)c.NvShaderCacheOn) },
                new uint[] { NvDrsDb.IdShaderCacheSize, unchecked((uint)c.NvShaderCacheSize) },
                new uint[] { 0x00A879CF, unchecked((uint)c.NvAaaVsync) },
            };
        }

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
        static extern IntPtr QueryInterface(uint id);

        delegate int Dlg0();
        delegate int DlgSession(out IntPtr h);
        delegate int DlgVoid(IntPtr h);
        delegate int DlgFindName(IntPtr h, byte* name, out IntPtr profile);
        delegate int DlgFindApp(IntPtr h, byte* appName, out IntPtr profile, ref NvdrsApplicationV4 app);
        delegate int DlgCreateProfile(IntPtr h, ref NvdrsProfile info, out IntPtr profile);
        delegate int DlgCreateApp(IntPtr h, IntPtr profile, ref NvdrsApplicationV4 app);
        delegate int DlgSetSetting(IntPtr h, IntPtr profile, ref NvdrsSetting s);
        delegate int DlgErrMsg(int status, byte* desc);
        delegate int DlgDeleteProfile(IntPtr h, IntPtr profile);
        delegate int DlgRestoreProfile(IntPtr h, IntPtr profile);

        const uint NvAPI_GetErrorMessage_ID = 0x6C2D048C;

        public static string ErrText(int status)
        {
            try
            {
                var em = Get<DlgErrMsg>(NvAPI_GetErrorMessage_ID);
                if (em == null) return status.ToString();
                byte* buf = stackalloc byte[64];
                if (em(status, buf) == 0)
                    return status + "(" + Marshal.PtrToStringAnsi(new IntPtr(buf)) + ")";
            }
            catch { }
            return status.ToString();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct NvapiUnicodeString
        {
            public fixed byte Data[4096];
            public void Set(string text)
            {
                text += '\0';
                fixed (byte* d = Data)
                {
                    byte[] bytes = Encoding.Unicode.GetBytes(text);
                    int n = Math.Min(bytes.Length, 4096);
                    for (int i = 0; i < n; i++) d[i] = bytes[i];
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NvdrsProfile
        {
            public uint Version;
            public NvapiUnicodeString ProfileName;
            public uint GpuSupport;
            public uint IsPredefined;
            public uint NumOfApps;
            public uint NumOfSettings;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct NvdrsApplicationV4
        {
            public uint Version;
            public uint IsPredefined;
            public NvapiUnicodeString AppName;
            public NvapiUnicodeString UserFriendlyName;
            public NvapiUnicodeString Launcher;
            public NvapiUnicodeString FileInFolder;
            public uint Flags;
            public NvapiUnicodeString CommandLine;
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x3020)]
        public struct NvdrsSetting
        {
            [FieldOffset(0x0)] public uint Version;
            [FieldOffset(0x4)] public NvapiUnicodeString SettingName;
            [FieldOffset(0x1004)] public uint SettingId;
            [FieldOffset(0x1008)] public uint SettingType;
            [FieldOffset(0x100C)] public uint SettingLocation;
            [FieldOffset(0x1010)] public uint IsCurrentPredefined;
            [FieldOffset(0x1014)] public uint IsPredefinedValid;
            [FieldOffset(0x1018)] public uint PredefinedValue;
            [FieldOffset(0x201C)] public uint CurrentValue;
        }

        static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero) return null;
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        public static bool Available()
        {
            try
            {
                var init = Get<Dlg0>(NvAPI_Initialize_ID);
                return init != null && init() == 0;
            }
            catch { return false; }
        }

        // 驱动配置还原系统默认：预定义档恢复默认设置(RestoreProfileDefault)，自建档删除
        public static string ResetGameProfiles(List<string>[] gameLists)
        {
            var init = Get<Dlg0>(NvAPI_Initialize_ID);
            var createSession = Get<DlgSession>(NvAPI_DRS_CreateSession_ID);
            var load = Get<DlgVoid>(NvAPI_DRS_LoadSettings_ID);
            var find = Get<DlgFindName>(NvAPI_DRS_FindProfileByName_ID);
            var findApp = Get<DlgFindApp>(NvAPI_DRS_FindApplicationByName_ID);
            var del = Get<DlgDeleteProfile>(0x17093206);
            var restore = Get<DlgRestoreProfile>(0xFA5F6134);   // NvAPI_DRS_RestoreProfileDefault（开源 ID 表双源核对）
            var save = Get<DlgVoid>(NvAPI_DRS_SaveSettings_ID);
            var destroy = Get<DlgVoid>(NvAPI_DRS_DestroySession_ID);
            if (init == null || createSession == null || load == null || find == null || findApp == null
                || del == null || restore == null || save == null || destroy == null)
                return "NVAPI 接口不完整，跳过驱动配置还原";
            if (init() != 0) return "NVAPI 初始化失败";
            IntPtr h;
            if (createSession(out h) != 0) return "DRS 会话创建失败";
            try
            {
                if (load(h) != 0) return "DRS 配置读取失败";
                int restored = 0;
                foreach (var list in gameLists)
                {
                    if (list == null) continue;
                    foreach (var pn in list)
                    {
                        string exe = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                        NvapiUnicodeString an = new NvapiUnicodeString(); an.Set(exe);
                        NvdrsApplicationV4 info = new NvdrsApplicationV4();
                        info.Version = (uint)Marshal.SizeOf(typeof(NvdrsApplicationV4)) | (4u << 16);
                        IntPtr prof;
                        if (findApp(h, an.Data, out prof, ref info) == 0 && prof != IntPtr.Zero)
                        {
                            restore(h, prof);
                            restored++;
                        }
                    }
                }
                int deleted = 0;
                foreach (var ownName in new[] { ProfileName, ProfileName + "-MMO", ProfileName + "-AAA", ProfileName + "-Gacha" })
                {
                    IntPtr profile;
                    NvapiUnicodeString pn2 = new NvapiUnicodeString(); pn2.Set(ownName);
                    if (find(h, pn2.Data, out profile) == 0 && del(h, profile) == 0) deleted++;
                }
                int st = save(h);
                return st == 0
                    ? ("驱动配置已还原：预定义档恢复默认 " + restored + " 个，自建档删除 " + deleted + " 个")
                    : ("保存失败 " + ErrText(st));
            }
            finally { destroy(h); }
        }

        // 删除指定名称的驱动配置档（--nvclean 清理诊断遗留用）
        public static string DeleteProfiles(List<string> profileNames)
        {
            var init = Get<Dlg0>(NvAPI_Initialize_ID);
            var createSession = Get<DlgSession>(NvAPI_DRS_CreateSession_ID);
            var load = Get<DlgVoid>(NvAPI_DRS_LoadSettings_ID);
            var find = Get<DlgFindName>(NvAPI_DRS_FindProfileByName_ID);
            var del = Get<DlgDeleteProfile>(0x17093206);   // NvAPI_DRS_DeleteProfile（开源 ID 表双源核对）
            var save = Get<DlgVoid>(NvAPI_DRS_SaveSettings_ID);
            var destroy = Get<DlgVoid>(NvAPI_DRS_DestroySession_ID);
            if (init == null || createSession == null || load == null || find == null || del == null || save == null || destroy == null)
                return "NVAPI 接口不完整，跳过清理";
            if (init() != 0) return "NVAPI 初始化失败";
            IntPtr h;
            if (createSession(out h) != 0) return "DRS 会话创建失败";
            try
            {
                if (load(h) != 0) return "DRS 配置读取失败";
                int n = 0;
                foreach (var name in profileNames)
                {
                    IntPtr profile;
                    NvapiUnicodeString pn = new NvapiUnicodeString(); pn.Set(name);
                    if (find(h, pn.Data, out profile) == 0 && del(h, profile) == 0) n++;
                }
                int st = save(h);
                return st == 0 ? ("已删除 " + n + " 个配置档") : ("保存失败 " + ErrText(st) + "（已标记删除 " + n + " 个）");
            }
            finally { destroy(h); }
        }

        // --nvtest 专用：应用驱动配置并返回结果文本
        public static string NvTest(List<string> fps, List<string> mmo, List<string> aaa, List<string> gacha)
        {
            return ApplyGameProfiles(fps, mmo, aaa, gacha);
        }

        // 为给定游戏 exe 应用驱动配置：电源管理=最高性能优先 + 低延迟=超高（等价 NVIDIA 面板手动设置）。
        // 已有预定义档的游戏（cs2/VALORANT 等）直接在其档上写设置（面板同款行为——应用一次只能归属一个档，
        // 硬塞进自建档会导致 SaveSettings 被拒）；未收录的才进 "GameBoost" 自建档。
        public static string ApplyGameProfiles(List<string> fps, List<string> mmo, List<string> aaa, List<string> gacha)
        {
            var init = Get<Dlg0>(NvAPI_Initialize_ID);
            var createSession = Get<DlgSession>(NvAPI_DRS_CreateSession_ID);
            var load = Get<DlgVoid>(NvAPI_DRS_LoadSettings_ID);
            var find = Get<DlgFindName>(NvAPI_DRS_FindProfileByName_ID);
            var findApp = Get<DlgFindApp>(NvAPI_DRS_FindApplicationByName_ID);
            var createProfile = Get<DlgCreateProfile>(NvAPI_DRS_CreateProfile_ID);
            var createApp = Get<DlgCreateApp>(NvAPI_DRS_CreateApplication_ID);
            var setSetting = Get<DlgSetSetting>(NvAPI_DRS_SetSetting_ID);
            var save = Get<DlgVoid>(NvAPI_DRS_SaveSettings_ID);
            var destroy = Get<DlgVoid>(NvAPI_DRS_DestroySession_ID);
            if (init == null || createSession == null || load == null || find == null || findApp == null
                || createProfile == null || createApp == null || setSetting == null || save == null || destroy == null)
                return "NVAPI 接口不完整（驱动过旧？），已跳过";
            int st = init(); if (st != 0) return "NVAPI 初始化失败 " + st;

            IntPtr h;
            st = createSession(out h); if (st != 0) return "DRS 会话创建失败 " + st;
            try
            {
                st = load(h); if (st != 0) return "DRS 配置读取失败 " + st;
                int c1, o1, c2, o2, c3, o3, c4, o4; c1 = o1 = c2 = o2 = c3 = o3 = c4 = o4 = 0;
                if (fps != null && fps.Count > 0)
                    ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, fps, WithDlss(CompSettings()), ProfileName, out c1, out o1);
                // MMO 与 3A 共用画质稳定档驱动设置（电源管理=最高性能/着色器缓存无限制/垂直同步跟随游戏）；
                // MMO 的差异在系统层：单线程 CPU 瓶颈，靠 P 核绑定+High 优先级+后台挂起，见游戏联动
                if (mmo != null && mmo.Count > 0)
                    ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, mmo, WithDlss(AaaSettings()), ProfileName + "-MMO", out c2, out o2);
                if (aaa != null && aaa.Count > 0)
                    ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, aaa, WithDlss(AaaSettings()), ProfileName + "-AAA", out c3, out o3);
                // 二游档驱动设置同 3A：着色器缓存无限制是二游 PC 端卡顿主因（鸣潮 UE4 着色器编译、原神版本更新重编译）
                if (gacha != null && gacha.Count > 0)
                    ApplyCategory(h, find, findApp, createProfile, createApp, setSetting, gacha, WithDlss(AaaSettings()), ProfileName + "-Gacha", out c4, out o4);
                st = save(h); if (st != 0) return "DRS 保存失败 " + ErrText(st);
                return "NVIDIA 驱动配置已写入——FPS档 " + c1 + "/" + o1 + "（最低延迟）、MMO档 " + c2 + "/" + o2 + "、3A档 " + c3 + "/" + o3 + "、二游档 " + c4 + "/" + o4 + "（画质档：电源管理=最高性能/着色器缓存=无限制/垂直同步=跟随游戏）"
                       + (Program.Cfg.DlssOverride ? "；已附加 DLSS 模型覆盖（DLL 覆盖=开 / 预设=推荐值）" : "");
            }
            finally { destroy(h); }
        }

        // 配置开启 DLSS 模型覆盖时，把三件套附加到该档的写入列表上（不改动原数组）
        static uint[][] WithDlss(uint[][] baseSettings)
        {
            // NvDrs 是独立类，这里必须写 Program.Cfg —— 裸 Cfg 在 Program 之外解析不到（CS0103）
            if (!Program.Cfg.DlssOverride) return baseSettings;
            var list = new List<uint[]>(baseSettings);
            var c = Program.Cfg;
            list.Add(new uint[] { NvDrsDb.IdDlssDllOverride, 0x1 });                  // 启用 DLL 覆盖
            list.Add(new uint[] { NvDrsDb.IdDlssPresetProfile, unchecked((uint)c.NvDlssPresetProfile) });
            list.Add(new uint[] { NvDrsDb.IdDlssPresetLetter, unchecked((uint)c.NvDlssPresetLetter) });
            return list.ToArray();
        }

        // 一类游戏的写入：预定义档直写设置，未收录的进自建档（应用一次只能归属一个档）
        static void ApplyCategory(IntPtr h, DlgFindName find, DlgFindApp findApp, DlgCreateProfile createProfile,
            DlgCreateApp createApp, DlgSetSetting setSetting, List<string> games, uint[][] settings,
            string ownProfileName, out int onPredefined, out int own)
        {
            onPredefined = 0; own = 0;
            var ownApps = new List<string>();
            foreach (var pn in games)
            {
                string exe = pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn : pn + ".exe";
                NvapiUnicodeString an = new NvapiUnicodeString(); an.Set(exe);
                NvdrsApplicationV4 info = new NvdrsApplicationV4();
                info.Version = (uint)Marshal.SizeOf(typeof(NvdrsApplicationV4)) | (4u << 16);
                IntPtr prof;
                // 官方签名：(hSession, appName, OUT phProfile, INOUT pApplication) —— 句柄在前、结构体在后
                if (findApp(h, an.Data, out prof, ref info) == 0 && prof != IntPtr.Zero)
                {
                    foreach (var kv in settings) SetDword(h, setSetting, prof, kv[0], kv[1]);
                    onPredefined++;
                }
                else ownApps.Add(exe);
            }
            if (ownApps.Count == 0) return;
            IntPtr profile;
            NvapiUnicodeString pname = new NvapiUnicodeString(); pname.Set(ownProfileName);
            if (find(h, pname.Data, out profile) != 0)
            {
                NvdrsProfile pi = new NvdrsProfile();
                pi.Version = (uint)Marshal.SizeOf(typeof(NvdrsProfile)) | (1u << 16);
                pi.GpuSupport = uint.MaxValue;
                pi.ProfileName.Set(ownProfileName);
                if (createProfile(h, ref pi, out profile) != 0) return;
            }
            foreach (var exe in ownApps)
            {
                NvdrsApplicationV4 app = new NvdrsApplicationV4();
                app.Version = (uint)Marshal.SizeOf(typeof(NvdrsApplicationV4)) | (4u << 16);
                app.Flags = 3;
                app.AppName.Set(exe);
                app.UserFriendlyName.Set(exe);
                if (createApp(h, profile, ref app) == 0) own++;
            }
            foreach (var kv in settings) SetDword(h, setSetting, profile, kv[0], kv[1]);
        }

        static int SetDword(IntPtr h, DlgSetSetting setSetting, IntPtr profile, uint settingId, uint value)
        {
            NvdrsSetting s = new NvdrsSetting();
            s.Version = (uint)Marshal.SizeOf(typeof(NvdrsSetting)) | (1u << 16);
            s.SettingId = settingId;   // 与 Ryujinx 同款：仅数字 ID
            s.SettingType = 0;         // Dword
            s.SettingLocation = 0;     // CurrentProfile
            s.CurrentValue = value;
            s.PredefinedValue = value;
            return setSetting(h, profile, ref s);
        }
    }

    // ==================== NVIDIA 驱动配置只读核验（直读 nvdrsdb） ====================
    // 为什么不用 NVAPI 读：NvAPI_DRS_GetSetting(0x73BF8338) 在本机驱动上必然触发访问违例，
    //   改走 EnumSettings(0xAE3039DA) 返回 rc=-9 —— 两条路都试过，想读只能绕开 NVAPI。
    // 数据来源：%ProgramData%\NVIDIA Corporation\Drs\nvdrsdb0.bin / nvdrsdb1.bin。
    //   ⚠️ NVIDIA 控制面板的 3D 设置全在这两个二进制里，**注册表里什么都没有** ——
    //   2026-09-19 因为只查了注册表，把「全局着色器缓存被禁用」误判成"驱动默认"，追了两天。
    // 记录格式（逆向所得，已与 NVIDIA Profile Inspector 的 CustomSettingNames.xml 交叉验证）：
    //   每条设置 = 固定 16 字节：A4 00 10 00 | settingId(u32 LE) | 0x0010xx | value(u32 LE)
    //   第 3 个字段：0x1000 = 驱动内置默认值；低位非零（实测 0x1002）= 该档被显式设置过。
    public static class NvDrsDb
    {
        // 常用 settingId（逐个与 NPI 的自定义设置表核对过，不是猜的）
        public const uint IdShaderCacheEnable = 0x00198FFF;   // 着色器缓存开关      0=关 / 1=开
        public const uint IdShaderCacheSize = 0x00AC8497;     // 着色器缓存大小      0=禁用 / 0xFFFFFFFF=无限制 / 其余=MB
        public const uint IdDlssDllOverride = 0x10E41E01;     // DLSS - Enable DLL Override
        public const uint IdDlssPresetLetter = 0x10E41DF3;    // DLSS - Forced Preset Letter
        public const uint IdDlssPresetProfile = 0x00634291;   // DLSS - Forced Model Preset Profile
        public const uint IdRbarEnable = 0x000F00BA;          // rBAR - Enable
        public const uint IdFrameLimitBg = 0x10835005;        // Frame Rate Limiter - Background

        public static string Dir
        {
            get
            {
                try
                {
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                        "NVIDIA Corporation", "Drs");
                }
                catch { return null; }
            }
        }

        public static bool Available() { return PrimaryFile() != null; }

        // 只取 primary 一份。⚠️ nvdrsdb0.bin 与 nvdrsdb1.bin 是**主/备镜像**（实测两份内容完全一致），
        //   把两份都扫一遍会让所有条数**翻倍**（DLSS 覆盖 9→18、rBAR 73→116），
        //   而且两份在写入过程中可能短暂不一致 —— 只读 primary，仅在它缺失时才退回备份。
        public static string PrimaryFile()
        {
            try
            {
                if (Dir == null || !Directory.Exists(Dir)) return null;
                string p0 = Path.Combine(Dir, "nvdrsdb0.bin");
                if (File.Exists(p0)) return p0;
                var fs = Directory.GetFiles(Dir, "nvdrsdb*.bin");
                return fs.Length > 0 ? fs[0] : null;
            }
            catch { return null; }
        }

        // 记录 @+8 的低位非零 = 该档被显式设置过（用户或工具写的）；0x1000 = 驱动内置默认值
        public static bool IsUserSet(uint loc) { return (loc & 0xFu) != 0; }

        static string SizeText(uint v)
        {
            if (v == 0) return "★ 已禁用";
            if (v == 0xFFFFFFFFu) return "无限制";
            if (v >= 1024 && v % 1024 == 0) return (v / 1024) + " GB";
            return v + " MB";
        }

        // 按 settingId 统计记录条数（原始扫描，不做语义判断）。
        // 提供给探针做交叉验证：探针自己按同样的 16 字节格式再扫一遍，两边条数必须一致。
        public static int CountById(uint id)
        {
            int n = 0;
            try
            {
                string f = PrimaryFile();
                if (f == null) return 0;
                byte[] d = File.ReadAllBytes(f);
                for (int i = 0; i + 16 <= d.Length; i++)
                {
                    if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                    if (BitConverter.ToUInt32(d, i + 4) == id) n++;
                    i += 15;
                }
            }
            catch { }
            return n;
        }

        // 一趟扫完两份库，把体检要用的项一次取全（不去反复读 2.6 MB 的二进制）
        public static NvDrsSnapshot Snapshot()
        {
            var s = new NvDrsSnapshot();
            try
            {
                if (!Available()) return s;
                bool swFound = false, swOn = false;
                var sizes = new List<uint>();
                int dlssTotal = 0, dlssOn = 0, rbarOn = 0, rbarOff = 0;
                bool frlFound = false; uint frl = 0;
                {
                    byte[] d = File.ReadAllBytes(PrimaryFile());
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        uint id = BitConverter.ToUInt32(d, i + 4);
                        uint loc = BitConverter.ToUInt32(d, i + 8);
                        uint val = BitConverter.ToUInt32(d, i + 12);
                        i += 15;   // 记录独占 16 字节，跳过整条（避免 value 里恰好含签名字节时重复命中）
                        if (id == IdShaderCacheEnable) { swFound = true; if (val != 0) swOn = true; }
                        else if (id == IdShaderCacheSize && IsUserSet(loc)) sizes.Add(val);
                        else if (id == IdDlssDllOverride) { dlssTotal++; if (val != 0) dlssOn++; }
                        else if (id == IdRbarEnable) { if (val != 0) rbarOn++; else rbarOff++; }
                        else if (id == IdFrameLimitBg && !frlFound) { frlFound = true; frl = val; }
                    }
                }
                s.Ok = true;
                foreach (var v in sizes) if (v == 0) s.ShaderCacheDisabled = true;
                // 「着色器缓存大小」在控制面板里**只有全局设置、没有 per-game 覆盖**
                // → 全库里值为 0 的那一条必然是全局档，这是判定依据。
                string swTxt = swFound ? (swOn ? "开" : "★ 关") : "未记录(默认开)";
                var szSet = new List<string>();
                foreach (var v in sizes) { string t = SizeText(v); if (!szSet.Contains(t)) szSet.Add(t); }
                s.ShaderCache = "开关=" + swTxt + "，上限记录=" + (szSet.Count == 0 ? "无（继承驱动默认 16 GB）"
                              : (string.Join(" / ", szSet.ToArray()) + (sizes.Count > 1 ? "（" + sizes.Count + " 档）" : "")));
                s.DlssOverrideOn = dlssOn > 0;
                s.DlssOverride = dlssTotal == 0 ? "驱动库无记录"
                               : (dlssOn > 0 ? ("已开启 " + dlssOn + "/" + dlssTotal + " 档") : ("未开启（" + dlssTotal + " 档记录全为关）"));
                s.Rbar = (rbarOn + rbarOff == 0) ? "驱动库无记录"
                       : (rbarOn + " 个游戏开 / " + rbarOff + " 个关（均为驱动内置）");
                s.FrlBackground = !frlFound ? "无记录" : (frl == 0 ? "未限（0）" : ("值 " + frl));
            }
            catch { }
            return s;
        }
    }

    public class NvDrsSnapshot
    {
        public bool Ok;                    // 驱动配置库可读
        public bool ShaderCacheDisabled;   // 用户设置里出现 0 = 已禁用
        public string ShaderCache = "";
        public string DlssOverride = "";
        public bool DlssOverrideOn;
        public string Rbar = "";
        public string FrlBackground = "";
    }

    // DLSS 模型覆盖（NVIDIA 驱动层，per-game 档）
    // RTX 30 系能吃 DLSS 4 的 Transformer 超分模型（MFG 多帧生成才是 50 系专属）。
    // 本机实测：驱动库里 DLL 覆盖 9 条记录**值全是 0（关）** → 这项能力一直没启用。
    // 写法与既有 per-game 档同源（都是 NvDrs.SetDword）：
    //   0x10E41E01 = 1（启用 DLL 覆盖）、0x00634291 = 1（预设档=推荐）、
    //   0x10E41DF3 = 0x00FFFFFF（强制预设=使用推荐值）。
    // ⚠️ 预设**故意不写死 K/L/M**：NPI 2.4.0.31 说明写明 L 只在超性能档、M 只在性能档才有意义，
    //   其余档位仍用 K；而 RTX 30 系跑 Gen2 的 M 预设约有 20% 性能税 ——
    //   "越新越好"在 30 系上是错的，让驱动按游戏自己选（推荐值）才稳。
    // 默认关闭：与 0.3.x 帧生成代理存在潜在相互影响（两者都可能在 DXGI/DLL 层接管），先留 opt-in。

    public class Config
    {
        public bool PowerEnable = true; public string PowerScheme = "ultimate";
        public int MinProc = 100, MaxProc = 100;
        public bool UsbSuspendOff = true, PcieAspmOff = true, PowerGameSwitch = true;
        public bool GpuEnable = true, GameDvrOff = true, NvProfiles = true;
        public List<string> AaaGames = new List<string>();
        public List<string> MmoGames = new List<string>();
        public List<string> GachaGames = new List<string>();
        public List<string> FpsGames = null;   // null=沿用 gameAware.gameProcesses
        public bool CleanEnable = true, BoostPriority = true;
        public List<string> KillList = new List<string>();
        public List<string> GameProcesses = new List<string>();
        public bool NetEnable = true, NagleOff = true; public int SysResp = 10;
        public bool AwareEnable = true; public int PollSec = 3; public bool Notify = true;
        // 远控联动范围：true=只有「竞技(fps)」档暂停远控（二游/3A/网游不动用户的远控会话）。
        // 界面文案一直这么写，但 2026-09-15 之前的代码只看了总开关、没看档位 —— 启动鸣潮也杀远控。
        public bool AwareOnlyFps = true;
        // ---- 游戏分辨率联动（v3.7.0）----
        // 进游戏按规则切主屏分辨率、退出还原；瓦罗兰特还要求先禁用副屏（复制模式下 1568x1080 切不动）。
        // 应用的是**临时模式**（不写注册表），崩溃/重启自动回原生 —— 见 ResLink 类注释。
        public bool ResLinkEnable = true;
        // 桌面上只剩一台在用屏时，是否仍按规则禁用它（v3.9.2，默认关）。
        //   为什么要这个开关：无畏契约的「真拉伸」恰恰要禁掉**唯一这台**屏——让它读不到 EDID 里的
        //   原生宽高比，游戏内的 Fill 才会真拉伸。2026-09-23 本机只剩 P27 在用（AOC 副屏关着），
        //   而旧的"唯一屏禁用会黑屏"守卫一律跳过 ⇒ 用户怎么点都拿不到拉伸。
        //   默认关是因为它确实有代价：停用后 Windows 会重排桌面，可能黑一下（退出游戏自动启用回来）。
        public bool ResLinkSoloOff = false;
        public int BaseW = 1920, BaseH = 1080, BaseHz = 165;   // 退出游戏后的还原目标
        public List<ResLinkRule> ResRules = new List<ResLinkRule>
        {
            // 用户本机诉求（2026-09-20）：CS2 用 1440x1080（严格 4:3，纵向原生像素）；
            // 瓦罗兰特用 1568x1080 且要先禁用副屏。进程名按前缀匹配（忽略大小写）。
            // v3.8.0：这两条改成"档位表里的某一档"（Preset 记 id），用户可在设置页换成别的档
            //   —— 换显示器后不用再手改 config.json。两个 id 都在这份档位表里，不会认不出。
            // v3.9.0（2026-09-20 追加）：「启动游戏时再禁用，只禁用 p27 这一台就行」——
            //   比"分离所有副屏"更精确：直接在**设备管理器**层面停用指定的那一台，其余屏原样不动。
            //   型号取自硬件 ID 第二段（本机实测 P27FBB-RG = XMIB008）：插拔/换口后设备实例 ID 会变、
            //   型号不变，所以配置里记型号而不是实例 ID。
            //   ⚠ 本机 P27FBB-RG 是**主屏**（1920x1080@165）：停用之后桌面会转移到另一台屏上，
            //     这是用户明确要的效果；若哪天只剩一块屏在用，程序会拒绝停用（否则黑屏）。
            //   detach 同时改为 false：用户要的是"只禁用那一台"，而不是再叠一层"分离所有副屏"。
            new ResLinkRule("cs2", 1440, 1080, 165, false) { Preset = "cs-1440x1080" },
            new ResLinkRule("VALORANT", 1568, 1080, 165, false) { Preset = "val-1568x1080", OffMons = "XMIB008" },
        };
        public List<RemoteApp> RemoteApps = new List<RemoteApp>();
        public bool DragEnable = true, DragFullWindows = false, NoTransparency = true, NoAnim = true;
        public bool SchedEnable = true; public int SchedSep = 38;
        public bool HagsOn = true, MmGamesEnable = true;
        public bool NicEnable = true, NicGreenOff = true, NicPowerOff = true;
        public bool TimerEnable = true; public double TimerMs = 0.5;
        public bool SvcEnable = true; public List<string> DisableServices = new List<string>();
        public bool AlertEnable = true; public int AlertGpuC = 83, AlertCpuPct = 90, AlertMemPct = 90, AlertCooldownMin = 5;
        public bool GbEnable = true, GbTrim = true, GbPauseWU = true;
        public List<string> SuspendList = new List<string>();
        public bool GpuGameMode = true;
        public bool AdaptiveEnable = true;
        public bool AffinityEnable = false, AffinityPOnly = false;   // v2.0 默认关闭硬绑（改用异类线程调度策略，见 HeteroPolicyEnable）
        public bool HeteroPolicyEnable = true;                       // 异类线程调度策略：首选 P 核，交给 Thread Director 动态调度
        public bool InputEnable = true, MouseAccelOff = true;
        public bool MemEnable = true, NoPagingExec = true;
        public bool DefenderExclude = false;
        // ---- 系统虚拟化开关 ----
        // 关掉 hypervisor 层（Hyper-V / 虚拟机监控程序平台）。收益是减少帧生成时间抖动
        // （治的是帧时间尾部，不治平均帧上限）。
        // ⚠️ 默认 false —— 开启会让 WSL2 / Docker Desktop / Android 模拟器 / Windows 沙盒
        //    全部不可用。可逆：恢复时只写回 BCD 的一条值，Windows 功能不被动过，
        //    所以 WSL2/Docker 不用重装。
        public bool VirtDisableEnable = false;
        // ---- DLSS 模型覆盖（NVIDIA 驱动层写入，非只读）----
        // RTX 30 系可用 DLSS 4 的 Transformer 超分模型（只有 MFG 多帧生成锁 50 系）。
        // 默认 false：与 0.3.x 帧生成代理存在潜在相互影响，先留 opt-in，用户在「自定义优化项」里开。
        // 取值细节见 NvDrsDb 上方的注释（预设故意交给驱动的"推荐值"，避免 30 系踩 M 的 20% 性能税）。
        public bool DlssOverride = false;

        // ---- 驱动 / 系统「具体取值」细调（v3.6.0）----
        // 起因：体检表能显示「期望值」，但期望值是程序写死的 —— 用户看得见、改不了。
        // 而着色器缓存上限（12 档）、DLSS 强制预设（K/L/M）、垂直同步模式、量子长度这些，
        // 最优值本来就随游戏、随驱动版本、随个人偏好变化，锁死在程序里等于替用户做了选择。
        // 这里把每一项落成配置值，默认 = 原硬编码值（行为零变化），改由「取值细调」对话框写入。
        // ⚠️ 着色器缓存上限用 -1 表示 0xFFFFFFFF（无限制）：JSON 里没有 uint，直接写
        //    4294967295 会被 Convert.ToInt32 溢出、被 catch 吞掉后静默取默认值（本文件踩过多回）。
        public int NvShaderCacheOn = 1;                              // 0x00198FFF 0=关 1=开
        public int NvShaderCacheSize = -1;                           // 0x00AC8497 -1=无限制(0xFFFFFFFF)
        public int NvDlssPresetLetter = 0x00FFFFFF;                  // 0x0010E41DF3 0xFFFFFF=使用推荐值
        public int NvDlssPresetProfile = 1;                          // 0x00634291 0=N/A 1=推荐 2=自定义
        public int NvCompPreRender = 1;                              // 0x007BA09E 竞技档最大预渲染帧数
        public int NvCompPowerMode = 1;                              // 0x1057EB71 竞技档电源管理模式
        public int NvCompVsync = 0x08416747;                         // 0x00A879CF 竞技档垂直同步=强制关
        public int NvAaaVsync = 0x60925292;                          // 0x00A879CF 3A/MMO/二游档垂直同步
        public int NvCompTexQuality = 0x14;                          // 0x00CE2691 竞技档纹理过滤质量
        public int NvBgFpsLimit = 0;                                 // 0x10835005 后台帧率上限 0=不限
        public int NetThrottle = -1;                                 // NetworkThrottlingIndex -1=禁用节流
        // ---- 看门狗整合模块（v1.0.5）----
        public bool IncidentEnable = true;                       // 黑屏取证：nvlddmkm/Kernel-Power 41 → 快照
        public bool DiskGuardEnable = true; public int DiskWarnFreeGb = 20; public int DiskCleanLogDays = 0; // 0=不自动删日志
        public bool NetWatchEnable = false; public int NetWatchEveryMin = 5;   // 网络丢包哨兵
        public bool OverlayGuardEnable = false;                  // 游戏时挂起 NVIDIA 覆盖层
        public List<string> OverlayList = new List<string>();
        // ---- DLSSG 帧生成模块（v2.0）----
        public bool DlssgEnable = true;
        public string DlssgRepo = "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main";
        public bool DlssgUseProxy = true; public string DlssgProxy = "http://127.0.0.1:7890";
        public bool DlssgOnlineCovers = true;         // 联网补封面（Steam 官方 CDN；关掉则只用本地图标/渐变卡）
        public string DlssgRouter = "SM86";        // SM86(30系) / SM75(20系)
        public string DlssgKernel = "PTX";         // PTX(通用) / Auto / Cubin
        public int DlssgBilinear = 0;              // 0=精确 1=近似(省算力)
        // DLSS 5 神经渲染（NR）默认关：
        //  它每帧跑一遍 158 MB 模型、不改帧数只改画面。2026-09-16 实测 NR 常开 + 4X MFG
        //  → 鸣潮两次 GPUCrash（DXGI_ERROR_DEVICE_HUNG）。关闭不影响帧生成。
        //  （此前由 2.9.1 误固化 Enabled=true，导致每次启动都自动开）
        //  ⚠ 关于驱动门槛，2026-09-16 纠正了一处误读：上游的 ≥616.56 **只对 RTX 50 是硬门槛** ——
        //    DLSS5-Swapper 源码里 OPTI_DRIVER=61656，但 driverSupported() 判的是
        //    `blackwell(row) && 驱动≥61656`；其注释原话：模型随应用提供而非取自已安装驱动，
        //    所以 "an older driver still runs an OptiScaler install"。
        //    对 20/30/40 系它不是门槛，**别拿它劝用户升级驱动**。
        public bool DlssgNrEnabled = false;
        public int DlssgMaxFrames = 2;             // 1..5 → 2X/3X/4X/5X/6X（上限由运行库决定：310.9=5，310.1/0.2.x=3）
        public int DlssgLogLevel = 1;              // 0关 1错误 2诊断 3详细
        public bool DlssgAutoEntry = true;         // 已知游戏自动用专用入口（CP2077→winmm）
        public string DlssgGenericEntry = "version.dll";   // 3A 单机通用部署用的入口名（作者 alternatives 里的 6 选 1）
        public bool DlssgAntiCheatD3d12 = true;    // v2.1.0：反作弊游戏自动改走 d3d12.dll 入口
        public List<string> DlssgExtraRoots = new List<string>();
        // ---- UI 行为（v2.0.7）----
        public bool CloseAsk = true;               // 点 X 时询问「最小化到托盘 / 退出程序」
        // 界面主题：light=浅色（默认）/ dark=沿用「游戏启动器」的深色美术语言。
        // 必须在构造界面前读取（不少控件在构造函数里就把 Theme.X 抄进了字段）。
        public string UiTheme = "light";
        // 界面动效：auto=跟随系统（默认）/ on=始终开启 / off=关闭。
        //  为什么要留这一档：很多性能机上"为窗口内的控件和元素添加动画"本身就是关的
        //  （本机实测 SPI_GETCLIENTAREAANIMATION=0，即它在关闭状态），严格遵从系统就等于
        //  完全没有过渡 —— 用户会误以为"没做"。默认不动用户的选择（跟随系统），
        //  但把「始终开启」这个开关放到设置页明面上。
        public string UiMotion = "auto";
        public bool CloseToTray = true;            // 已记住的选择：true=最小化到托盘；false=退出

        // 上次 Load 的说明（由 Main 记入日志）
        public static string LoadNote = "";

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;
            Dictionary<string, object> root = null;
            string rawText = null;
            string firstErr = "";
            try
            {
                rawText = File.ReadAllText(path, Encoding.UTF8);
                root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(rawText);
            }
            catch (Exception ex) { firstErr = ex.Message; }

            // ① 解析失败先试自愈：构建期脚本可能把 $_ 占位符展开了，写出 $1/$10 这种非法行。
            //   2026-09-15 用户机实测：config.json 里三行 $10/$11/$12 让**整份配置静默回默认**
            //   —— 主题切了不生效、gameAware 游戏名单变空（打游戏不暂停远控）、键位全部重置。
            if (root == null && rawText != null)
            {
                try
                {
                    string fixedText = RepairJson(rawText);
                    if (fixedText != null && fixedText != rawText)
                        root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(fixedText);
                    if (root != null)
                    {
                        try
                        {
                            File.WriteAllText(path, fixedText, new UTF8Encoding(false));
                            LoadNote = "配置自愈：已剔除非法占位符行并回写 " + path;
                        }
                        catch (Exception wex)
                        {
                            LoadNote = "配置自愈：内容已修正但无法回写（" + wex.Message
                                     + "）—— 该目录只读，建议到「设置」里把数据目录改到可写位置";
                        }
                    }
                }
                catch { }
            }
            // ② 仍旧读不出来：备份坏文件 + 明说"本次用默认值"，不再无声无息
            if (root == null)
            {
                try { File.Copy(path, path + ".bad", true); } catch { }
                LoadNote = "配置解析失败（" + firstErr + "）→ 已备份为 config.json.bad，本次使用内置默认值";
                return c;
            }
            try
            {
                var power = GetDict(root, "power");
                if (power != null)
                {
                    c.PowerEnable = GetBool(power, "enable", true);
                    c.PowerScheme = GetStr(power, "scheme", "ultimate");
                    c.MinProc = GetInt(power, "minProcessorState", 100);
                    c.MaxProc = GetInt(power, "maxProcessorState", 100);
                    c.UsbSuspendOff = GetBool(power, "usbSuspendOff", true);
                    c.PcieAspmOff = GetBool(power, "pcieAspmOff", true);
                    c.PowerGameSwitch = GetBool(power, "gameAwareSwitch", true);
                }
                var gpu = GetDict(root, "gpu");
                if (gpu != null) { c.GpuEnable = GetBool(gpu, "enable", true); c.GameDvrOff = GetBool(gpu, "gameDvrOff", true); c.GpuGameMode = GetBool(gpu, "gameModeOn", true); c.NvProfiles = GetBool(gpu, "nvProfiles", true); }
                var gpf = GetDict(root, "gameProfiles");
                if (gpf != null)
                {
                    var ag = GetList(gpf, "aaaGames"); if (ag.Count > 0) c.AaaGames = ag;
                    var mmoL = GetList(gpf, "mmoGames"); if (mmoL.Count > 0) c.MmoGames = mmoL;
                    var ggL = GetList(gpf, "gachaGames"); if (ggL.Count > 0) c.GachaGames = ggL;
                    var fg = GetList(gpf, "fpsGames"); if (fg.Count > 0) c.FpsGames = fg;
                }
                var gbp = GetDict(root, "gameBoost");
                if (gbp != null)
                {
                    c.GbEnable = GetBool(gbp, "enable", true);
                    c.GbTrim = GetBool(gbp, "trimMemory", true);
                    c.GbPauseWU = GetBool(gbp, "pauseWindowsUpdate", true);
                    var sl = GetList(gbp, "suspendList"); if (sl.Count > 0) c.SuspendList = sl;
                }
                var clean = GetDict(root, "cleanup");
                if (clean != null)
                {
                    c.CleanEnable = GetBool(clean, "enable", true);
                    c.BoostPriority = GetBool(clean, "boostGamePriority", true);
                    var kl = GetList(clean, "killList"); if (kl.Count > 0) c.KillList = kl;
                    var gp = GetList(clean, "gameProcesses"); if (gp.Count > 0) c.GameProcesses = gp;
                }
                var net = GetDict(root, "network");
                if (net != null)
                {
                    c.NetEnable = GetBool(net, "enable", true);
                    c.NagleOff = GetBool(net, "nagleOff", true);
                    c.SysResp = GetInt(net, "systemResponsiveness", 10);
                    c.NetThrottle = GetInt(net, "throttlingIndex", -1);   // v3.6.0：原来写死 -1，现在可调
                }
                var aw = GetDict(root, "gameAware");
                if (aw != null)
                {
                    c.AwareEnable = GetBool(aw, "enable", true);
                    c.PollSec = GetInt(aw, "pollIntervalSec", 3);
                    c.Notify = GetBool(aw, "notify", true);
                    c.AwareOnlyFps = GetBool(aw, "onlyFps", true);
                    var gps = GetList(aw, "gameProcesses"); if (gps.Count > 0) c.GameProcesses = gps;
                    foreach (var a in GetListDict(aw, "remoteApps"))
                    {
                        var ra = new RemoteApp();
                        ra.Name = GetStr(a, "name", "GameViewer");
                        ra.Processes = GetList(a, "processes");
                        ra.Services = GetList(a, "services");
                        ra.Exe = GetStr(a, "exe", "");
                        c.RemoteApps.Add(ra);
                    }
                }
                var gd = GetDict(root, "guard");
                if (gd != null)
                {
                    var inc = GetDict(gd, "incidentWatch"); if (inc != null) c.IncidentEnable = GetBool(inc, "enable", true);
                    var dg = GetDict(gd, "diskGuard");
                    if (dg != null)
                    {
                        c.DiskGuardEnable = GetBool(dg, "enable", true);
                        c.DiskWarnFreeGb = GetInt(dg, "warnFreeGb", 20);
                        c.DiskCleanLogDays = GetInt(dg, "autoCleanLogDays", 0);
                    }
                    var nw = GetDict(gd, "netWatch");
                    if (nw != null) { c.NetWatchEnable = GetBool(nw, "enable", false); c.NetWatchEveryMin = GetInt(nw, "everyMinutes", 5); }
                    var og = GetDict(gd, "overlayGuard");
                    if (og != null)
                    {
                        c.OverlayGuardEnable = GetBool(og, "enable", false);
                        var ol = GetList(og, "processes"); if (ol.Count > 0) c.OverlayList = ol;
                    }
                }
                // ---- DLSSG 帧生成段 ----
                // 注意：变量名不能叫 dg —— 上面 diskGuard 的 dg 在子级作用域里用过，
                // 外层再声明同名会触发 CS0136（改变子级作用域里 dg 的含义）。
                var dlssgSec = GetDict(root, "dlssg");
                if (dlssgSec != null)
                {
                    c.DlssgEnable = GetBool(dlssgSec, "enable", true);
                    c.DlssgRepo = GetStr(dlssgSec, "repo", c.DlssgRepo);
                    c.DlssgUseProxy = GetBool(dlssgSec, "useProxy", true);
                    c.DlssgOnlineCovers = GetBool(dlssgSec, "onlineCovers", true);
                    c.DlssgProxy = GetStr(dlssgSec, "proxy", c.DlssgProxy);
                    c.DlssgRouter = GetStr(dlssgSec, "router", "SM86");
                    c.DlssgKernel = GetStr(dlssgSec, "kernelImage", "PTX");
                    c.DlssgBilinear = GetInt(dlssgSec, "hardwareBilinear", 0);
                    // 夹到 1..5：老配置里可能存着更大或非法的值，直接用会让下拉框索引越界
        c.DlssgMaxFrames = Math.Max(1, Math.Min(5, GetInt(dlssgSec, "maxGeneratedFrames", 2)));
                    c.DlssgLogLevel = GetInt(dlssgSec, "loggingLevel", 1);
                    c.DlssgAutoEntry = GetBool(dlssgSec, "autoEntrypoint", true);
                    c.DlssgGenericEntry = GetStr(dlssgSec, "genericEntry", "version.dll");
                    c.DlssgAntiCheatD3d12 = GetBool(dlssgSec, "antiCheatD3d12", true);
                    c.DlssgExtraRoots = GetList(dlssgSec, "extraRoots");
        c.DlssgNrEnabled = GetBool(dlssgSec, "nrEnabled", false);
                }
                // ---- 游戏分辨率联动段（displayLink）----
                // ⚠ 变量名不能用 dg/dlssgSec 等已有名（同一 try 块里重复声明 = CS0136）
                var resLinkSec = GetDict(root, "displayLink");
                if (resLinkSec != null)
                {
                    c.ResLinkEnable = GetBool(resLinkSec, "enable", true);
                    c.ResLinkSoloOff = GetBool(resLinkSec, "soloOff", false);
                    c.BaseW = GetInt(resLinkSec, "baseW", 1920);
                    c.BaseH = GetInt(resLinkSec, "baseH", 1080);
                    c.BaseHz = GetInt(resLinkSec, "baseHz", 165);
                    var rlList = GetListDict(resLinkSec, "rules");
                    if (rlList.Count > 0)
                    {
                        c.ResRules = new List<ResLinkRule>();
                        foreach (var a in rlList)
                        {
                            string proc = GetStr(a, "proc", "");
                            if (proc == null || proc.Length == 0) continue;   // 没进程名的规则无意义，直接丢
                            var rl = new ResLinkRule(proc,
                                GetInt(a, "w", 1920), GetInt(a, "h", 1080),
                                GetInt(a, "hz", 165), GetBool(a, "detach", false));
                            // preset 是 v3.8.0 新增字段：老配置里没有 → 按 w/h 反查内置档位，
                            //   反查不到就留空（界面显示"自定义"）。这样老配置不用迁移也不丢信息。
                            rl.Preset = GetStr(a, "preset", "");
                            // offMons 是 v3.9.0 新增：进游戏时要**在设备管理器层面禁用**的显示器型号
                            //   （逗号分隔，如 "XMIB008"）。老配置没有这一项 → 空串 = 不改任何设备。
                            rl.OffMons = GetStr(a, "offMons", "");
                            if (rl.OffMons == null) rl.OffMons = "";
                            if (rl.Preset == null || rl.Preset.Length == 0)
                            {
                                ResPreset mp = ResLink.MatchPreset(proc, rl.W, rl.H);
                                if (mp != null) rl.Preset = mp.Id;
                            }
                            c.ResRules.Add(rl);
                        }
                    }
                }
                var uiSec = GetDict(root, "ui");
                if (uiSec != null)
                {
                    c.CloseAsk = GetBool(uiSec, "closeAsk", true);
                    c.UiTheme = GetStr(uiSec, "theme", "light");
                    c.UiMotion = GetStr(uiSec, "motion", "auto");
                    if (c.UiMotion != "on" && c.UiMotion != "off") c.UiMotion = "auto";   // 认不出的值一律回落成"跟随系统"
                    c.CloseToTray = GetBool(uiSec, "closeToTray", true);
                }
                var df = GetDict(root, "dragFix");
                if (df != null)
                {
                    c.DragEnable = GetBool(df, "enable", true);
                    c.DragFullWindows = GetBool(df, "dragFullWindows", false);
                    c.NoTransparency = GetBool(df, "disableTransparency", true);
                    c.NoAnim = GetBool(df, "disableAnimations", true);
                }
                var sch = GetDict(root, "scheduler");
                if (sch != null)
                {
                    c.SchedEnable = GetBool(sch, "enable", true);
                    c.SchedSep = GetInt(sch, "win32PrioritySeparation", 38);
                    c.HeteroPolicyEnable = GetBool(sch, "heteroPolicy", true);
                }
                var hags = GetDict(root, "hags");
                if (hags != null) c.HagsOn = GetBool(hags, "enable", true);
                var mg = GetDict(root, "mmGames");
                if (mg != null) c.MmGamesEnable = GetBool(mg, "enable", true);
                var nic = GetDict(root, "nic");
                if (nic != null) { c.NicEnable = GetBool(nic, "enable", true); c.NicGreenOff = GetBool(nic, "greenEthernetOff", true); c.NicPowerOff = GetBool(nic, "powerManagementOff", true); }
                var tm = GetDict(root, "timer");
                if (tm != null) { c.TimerEnable = GetBool(tm, "enable", true); try { c.TimerMs = Convert.ToDouble(GetStr(tm, "resolutionMs", "0.5")); } catch { } }
                var sv = GetDict(root, "services");
                if (sv != null) { c.SvcEnable = GetBool(sv, "enable", true); var dl = GetList(sv, "disable"); if (dl.Count > 0) c.DisableServices = dl; }
                var al = GetDict(root, "alerts");
                if (al != null)
                {
                    c.AlertEnable = GetBool(al, "enable", true);
                    c.AlertGpuC = GetInt(al, "gpuTempC", 83); c.AlertCpuPct = GetInt(al, "cpuPct", 90);
                    c.AlertMemPct = GetInt(al, "memPct", 90); c.AlertCooldownMin = GetInt(al, "cooldownMin", 5);
                }
                var ad = GetDict(root, "adaptive");
                if (ad != null) c.AdaptiveEnable = GetBool(ad, "enable", true);
                var af = GetDict(root, "affinity");
                if (af != null) { c.AffinityEnable = GetBool(af, "enable", true); c.AffinityPOnly = GetBool(af, "pCoreOnly", true); }
                var inp = GetDict(root, "input");
                if (inp != null) { c.InputEnable = GetBool(inp, "enable", true); c.MouseAccelOff = GetBool(inp, "mouseAccelOff", true); }
                var mem = GetDict(root, "memory");
                if (mem != null) { c.MemEnable = GetBool(mem, "enable", true); c.NoPagingExec = GetBool(mem, "disablePagingExecutive", true); }
                var dfn = GetDict(root, "defender");
                if (dfn != null) c.DefenderExclude = GetBool(dfn, "enable", false);
                var virt = GetDict(root, "virtualization");
                if (virt != null) c.VirtDisableEnable = GetBool(virt, "enable", false);
                var dlss = GetDict(root, "dlss");
                if (dlss != null) c.DlssOverride = GetBool(dlss, "override", false);
                // ---- 取值细调（v3.6.0，对应「取值细调」对话框）----
                // 默认值与原硬编码完全一致，所以老安装（config.json 里没有 nv 段）行为不变。
                var nvk = GetDict(root, "nv");
                if (nvk != null)
                {
                    c.NvShaderCacheOn = GetInt(nvk, "shaderCacheOn", 1);
                    c.NvShaderCacheSize = GetInt(nvk, "shaderCacheSize", -1);
                    c.NvDlssPresetLetter = GetInt(nvk, "dlssForcedPreset", 0x00FFFFFF);
                    c.NvDlssPresetProfile = GetInt(nvk, "dlssPresetProfile", 1);
                    c.NvCompPreRender = GetInt(nvk, "compPrerenderedFrames", 1);
                    c.NvCompPowerMode = GetInt(nvk, "compPowerMode", 1);
                    c.NvCompVsync = GetInt(nvk, "compVsync", 0x08416747);
                    c.NvAaaVsync = GetInt(nvk, "aaaVsync", 0x60925292);
                    c.NvCompTexQuality = GetInt(nvk, "compTextureQuality", 0x14);
                    c.NvBgFpsLimit = GetInt(nvk, "backgroundFpsLimit", 0);
                }
            }
            catch { }
            // 联动游戏名单兜底：不管"配置缺失"还是"解析失败回默认"，识别名单都不该为空
            // —— 空名单 = 打游戏时不会暂停远控（2026-09-15 用户实测"打无畏契约还得手动退 UU远程"）。
            if (c.GameProcesses == null || c.GameProcesses.Count == 0)
                c.GameProcesses = new List<string>(DefaultGameProcesses);
            // 档位列表兜底：配置缺失/读取失败时如果不兜底，AutoCategory 会把**所有**游戏都判成
            // fps —— 于是"远控只对竞技档暂停"这道门禁形同虚设（鸣潮照样被当竞技档杀远控）。
            // 2026-09-15 实测：这就是"启动二游也杀 UU远程"的另一半原因。
            if (c.GachaGames == null || c.GachaGames.Count == 0)
                c.GachaGames = new List<string>(DefaultGachaGames);
            if (c.AaaGames == null || c.AaaGames.Count == 0)
                c.AaaGames = new List<string>(DefaultAaaGames);
            // MmoGames 没有内置名单，但**绝不能是 null**：AutoCategory / AllGameProcesses 会直接
            //  对它 .Contains。覆盖层 config.local.json 缺这个键时反序列化就给 null ——
            //   2026-09-16 探针实测撞上 NullReferenceException（真实运行时同样会炸）。
            if (c.MmoGames == null) c.MmoGames = new List<string>();
            return c;
        }

        // 内置默认游戏进程名单（不带 .exe）：仅在配置文件里没有名单时启用
        public static readonly string[] DefaultGameProcesses = new string[] {
            "cs2", "VALORANT", "RiotClientServices",
            "DeltaForceClient-Win64-Shipping", "DeltaForce-Win64-Shipping" };
        // 内置默认档位名单（与安装包里的 config.json 模板一致）：仅在配置里没有名单时启用
        public static readonly string[] DefaultGachaGames = new string[] {
            "YuanShen", "GenshinImpact", "Client-Win64-Shipping", "StarRail", "ZenlessZoneZero" };
        public static readonly string[] DefaultAaaGames = new string[] {
            "ForzaHorizon4", "ForzaHorizon5", "ForzaHorizon6", "Cyberpunk2077", "EldenRing" };

        // 修掉构建期残留的非法占位符行（$1 / $10 / $12 …）以及被它们带出来的悬空逗号
        public static string RepairJson(string text)
        {
            if (text == null) return null;
            var sb = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string t = raw.Trim();
                if (Regex.IsMatch(t, @"^\$[0-9]+\s*[,;]?$")) continue;   // 整行占位符
                if (t == "," || t == ";") continue;                       // 悬空分隔符行
                sb.Append(raw).Append('\n');
            }
            return Regex.Replace(sb.ToString(), @",(\s*[}\]])", "$1");   // 收尾多余逗号
        }

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o is Dictionary<string, object>) return (Dictionary<string, object>)o;
            return null;
        }
        static bool GetBool(Dictionary<string, object> d, string k, bool def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o))
            {
                if (o is bool) return (bool)o;
                if (o is string) { string s = (string)o; return s == "true" || s == "True" || s == "1"; }
            }
            return def;
        }
        static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o)) { try { return Convert.ToInt32(o); } catch { } }
            return def;
        }
        static string GetStr(Dictionary<string, object> d, string k, string def)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null) return o.ToString();
            return def;
        }
        static List<string> GetList(Dictionary<string, object> d, string k)
        {
            var r = new List<string>();
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                var arr = o as object[];
                if (arr != null) { foreach (var x in arr) r.Add(x.ToString()); return r; }
                var al = o as System.Collections.ArrayList;
                if (al != null) { foreach (var x in al) r.Add(x.ToString()); return r; }
            }
            return r;
        }
        static List<Dictionary<string, object>> GetListDict(Dictionary<string, object> d, string k)
        {
            var r = new List<Dictionary<string, object>>();
            object o;
            if (d != null && d.TryGetValue(k, out o) && o != null)
            {
                var arr = o as object[];
                if (arr != null) { foreach (var x in arr) if (x is Dictionary<string, object>) r.Add((Dictionary<string, object>)x); return r; }
                var al = o as System.Collections.ArrayList;
                if (al != null) { foreach (var x in al) if (x is Dictionary<string, object>) r.Add((Dictionary<string, object>)x); }
            }
            return r;
        }
    }

    // ==========================================================================================
    //  显卡名伪装（内置）—— 网游（绝区零 等）的 DLSS / 帧生成选项靠它才会出现。
    //
    //  作者教程的做法：改
    //    HKLM\SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_xxxx&SUBSYS_...\<实例>
    //  的 DeviceDesc / FriendlyName。以前要导出 .reg 再双击导入（还要自己找回滚路径），
    //  现在内置成两个按钮。
    //
    //  ⚠ 三条必须记住的：
    //    1) 只认 Service=nvlddmkm 的那个设备 —— VEN_10DE 下面还挂着一个 HD Audio 控制器，
    //       按厂商号盲改会改到声卡上。
    //    2) 还原时必须把 FriendlyName **整个删掉**：只改回 DeviceDesc 会残留伪装名
    //       （Windows 优先显示 FriendlyName）。
    //    3) 改完要重启电脑（或重启显卡设备）才生效；驱动升级后要重做。
    // ==========================================================================================
    public static class GpuSpoof
    {
        public const string Target = "NVIDIA GeForce RTX 5090";
        const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\PCI";

        public class St
        {
            public int Count;
            public string Key = "";            // VEN_10DE&DEV_xxxx\实例
            public string DeviceDesc = "";
            public string FriendlyName;
            public string InfForm = "";        // 原始形式（还原用）
            public bool Spoofed;
            public string Err = "";
        }

        static string BkFile { get { return Path.Combine(Program.DataDir, "gpu-spoof-original.txt"); } }

        // 找 NVIDIA 显示适配器（Service=nvlddmkm），读它的当前名字与"原始形式"
        public static St Detect()
        {
            var st = new St();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumRoot))
                {
                    if (k == null) { st.Err = "读不到注册表 " + EnumRoot; return st; }
                    foreach (string ven in k.GetSubKeyNames())
                    {
                        if (ven.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (RegistryKey sk = k.OpenSubKey(ven))
                        {
                            if (sk == null) continue;
                            foreach (string inst in sk.GetSubKeyNames())
                            {
                                string rel = ven + "\\" + inst;
                                try
                                {
                                    using (RegistryKey ik = Registry.LocalMachine.OpenSubKey(EnumRoot + "\\" + rel))
                                    {
                                        if (ik == null) continue;
                                        // ★ 只要显示适配器：VEN_10DE 下面那个 High Definition Audio 的 Service 是 HDAudBus
                                        if (!"nvlddmkm".Equals(Convert.ToString(ik.GetValue("Service")),
                                                               StringComparison.OrdinalIgnoreCase)) continue;
                                        st.Count++;
                                        if (st.Key.Length == 0) st.Key = rel;
                                        st.DeviceDesc = Convert.ToString(ik.GetValue("DeviceDesc"));
                                        st.FriendlyName = Convert.ToString(ik.GetValue("FriendlyName"));
                                        // 重建原始形式：@<InfPath>,%nvidia_dev.<dev>%;<友好名>
                                        string inf = Convert.ToString(ik.GetValue("InfPath"));
                                        // 本机实测：Enum 键里没有 InfPath，它在驱动键
                                        //   Class\{4d36e968-...}\000N 里；用 Enum 键的 Driver 值
                                        //   （形如 {guid} 加四位序号）找过去。少了这一步，重建出来的
                                        //   "原始名"会丢掉 @oem28.inf,%nvidia_dev.2486%; 前缀
                                        //   （探针 spoof_probe 抓到的，2026-09-16）。
                                        if (inf == null || inf.Length == 0)
                                        {
                                            string drv = Convert.ToString(ik.GetValue("Driver"));
                                            if (drv != null && drv.Length > 0)
                                                using (RegistryKey dk = Registry.LocalMachine.OpenSubKey(
                                                           "SYSTEM\\CurrentControlSet\\Control\\Class\\" + drv))
                                                    if (dk != null) inf = Convert.ToString(dk.GetValue("InfPath"));
                                        }
                                        Match mm = Regex.Match(ven, @"DEV_([0-9A-Fa-f]{4})");
                                        string dev = mm.Success ? mm.Groups[1].Value.ToLowerInvariant() : null;
                                        string dd = st.DeviceDesc == null ? "" : st.DeviceDesc;
                                        int sc = dd.LastIndexOf(';');
                                        string friendly = sc >= 0 ? dd.Substring(sc + 1) : dd;
                                        st.InfForm = (inf != null && inf.Length > 0 && dev != null)
                                            ? "@" + inf + ",%nvidia_dev." + dev + "%;" + friendly
                                            : friendly;
                                        string all = dd + "|" + (st.FriendlyName == null ? "" : st.FriendlyName);
                                        st.Spoofed = all.IndexOf("5090", StringComparison.Ordinal) >= 0;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { st.Err = ex.Message; }
            return st;
        }

        // 当前状态一行（给界面用）
        public static string StatusLine()
        {
            St st = Detect();
            if (st.Count == 0)
                return "未找到 NVIDIA 显卡设备" + (st.Err.Length > 0 ? "：" + st.Err : "") + "（伪装无法进行）";
            string cur = st.DeviceDesc == null ? "" : st.DeviceDesc;
            int sc = cur.LastIndexOf(';');
            if (sc >= 0) cur = cur.Substring(sc + 1);
            return "当前显卡名：" + cur
                 + (st.Spoofed ? "    ← 已伪装（网游才会显示帧生成选项）"
                               : "    ← 未伪装（网游不会显示帧生成选项）");
        }

        public static string Apply()
        {
            St st = Detect();
            if (st.Count == 0) return "没找到 NVIDIA 显卡设备" + (st.Err.Length > 0 ? "（" + st.Err + "）" : "");
            if (st.Spoofed) return "已经是伪装状态（" + st.DeviceDesc + "），无需重复开启";
            try
            {
                SaveOriginal(st.InfForm);      // 只在第一次保存，避免把伪装值当原始值存下来
                using (RegistryKey ik = Registry.LocalMachine.OpenSubKey(EnumRoot + "\\" + st.Key, true))
                {
                    if (ik == null) return "打不开设备键（本程序需要以管理员身份运行）";
                    ik.SetValue("DeviceDesc", Target, RegistryValueKind.String);
                    ik.SetValue("FriendlyName", Target, RegistryValueKind.String);
                }
            }
            catch (Exception ex) { return "写入失败：" + ex.Message; }
            return "已伪装为 " + Target + "（" + st.Key + "）";
        }

        public static string Restore()
        {
            St st = Detect();
            if (st.Count == 0) return "没找到 NVIDIA 显卡设备";
            string orig = LoadOriginal();
            if (orig == null || orig.Length == 0) orig = st.InfForm;
            if (orig == null || orig.Length == 0) return "找不到原始显卡名（备份文件丢失，且无法从设备键重建）";
            try
            {
                using (RegistryKey ik = Registry.LocalMachine.OpenSubKey(EnumRoot + "\\" + st.Key, true))
                {
                    if (ik == null) return "打不开设备键（本程序需要以管理员身份运行）";
                    ik.SetValue("DeviceDesc", orig, RegistryValueKind.String);
                    // ★ FriendlyName 必须整个删掉，否则伪装名还会被显示
                    try { ik.DeleteValue("FriendlyName", false); } catch { }
                }
            }
            catch (Exception ex) { return "写入失败：" + ex.Message; }
            return "已还原原始显卡名：" + orig;
        }

        // 不用重启电脑：重启显卡设备（等价于设备管理器里的"禁用→启用"）
        public static string RestartAdapter()
        {
            St st = Detect();
            if (st.Count == 0) return "没找到 NVIDIA 显卡设备";
            string o = Program.RunCmd("pnputil", "/restart-device \"PCI\\" + st.Key + "\"");
            o = (o == null ? "" : o.Trim());
            if (o.IndexOf("成功", StringComparison.Ordinal) >= 0
                || o.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                return "已重启显卡设备，伪装立即生效（屏幕可能闪一下）";
            return "自动重启显卡设备未成功" + (o.Length > 0 ? "：" + o : "")
                 + "\n请手动重启一次电脑（约 1 分钟）让伪装生效。";
        }

        // 主动把"原始显卡名"存成备份（未伪装时就调一次）。
        //  为什么没点"开启"也要存：万一用户是用别的方式改的（比如自己去导 .reg），
        //  我们也有一份精确原始值可还原 —— 而不是靠 InfPath 拼回来。
        public static string EnsureBackup()
        {
            try
            {
                St st = Detect();
                if (st.Spoofed || st.InfForm == null || st.InfForm.Length == 0) return "";
                SaveOriginal(st.InfForm);
                return BkFile;
            }
            catch { return ""; }
        }

        static void SaveOriginal(string v)
        {
            try
            {
                if (v == null || v.Length == 0) return;
                if (File.Exists(BkFile)) return;                  // 已有备份就不覆盖
                Directory.CreateDirectory(Path.GetDirectoryName(BkFile));
                File.WriteAllText(BkFile, v, new UTF8Encoding(false));
            }
            catch { }
        }

        static string LoadOriginal()
        {
            try { return File.Exists(BkFile) ? File.ReadAllText(BkFile).Trim() : null; }
            catch { return null; }
        }
    }

    public class StatusItem
    {
        public string Item; public string Current; public string Expected; public string Status;
        // v3.6.0：可调项挂上 OptKnob 的键 —— 体检表双击这一行就能直接改它的取值。
        // 空 = 只读核验项（HPET / GPU 中断亲和 / rBAR 全局强开这类，改错会让设备消失，不代改）。
        public string Key;
    }

    // ---- 「具体取值」旋钮（v3.6.0）----
    // 体检表只能回答"目标和现状一不一致"；这张表回答"目标值本身选哪一档"，并且让用户自己选。
    // 只开放「改坏了能改回来」的项：全部走 config.json + 原有备份机制。
    public class OptKnob
    {
        public string Key;        // 唯一键，也是体检表双击的定位键
        public string Sec;        // config.json 段名
        public string Name;       // config.json 键名
        public bool IsBool;       // true = 该键在 JSON 里是 true/false
        public string Group;      // 对话框分组标题
        public string Title;      // 显示名
        public string Hint;       // 这一档到底影响什么（必须写人话）
        public string[] Labels;
        public int[] Values;
        public string Apply;      // 保存后怎么落到系统：drv / hags / sched / proc / net
        public bool Restart;      // 需重启生效
        public int Value;         // 当前配置值

        public int Index
        {
            get
            {
                for (int i = 0; i < Values.Length; i++) if (Values[i] == Value) return i;
                return -1;
            }
        }
        public string CurrentText
        {
            get { int i = Index; return i < 0 ? ("当前值 " + Value) : Labels[i]; }
        }
        public string ValueText(int raw)
        {
            for (int i = 0; i < Values.Length; i++) if (Values[i] == raw) return Labels[i];
            return raw.ToString();
        }
    }

    public static class Program
    {
    public static string AppDir { get { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); } }
    // 数据目录分流：便携版(exe 同目录有 config.json) -> 就地读写；安装版(%ProgramData% 已存在) -> 集中到公共数据目录。
    // 这样装进 Program Files 后，配置/日志/备份不会落在只读的程序目录里，卸载也不会带走用户备份。
    static string dataDirCache;

    // ---- 数据目录指针（v2.9.1）----
    //  放在 %LOCALAPPDATA%\Fluxion\datadir.txt（永远可写的用户目录）。
    //  为什么需要它：默认的 %ProgramData%\Fluxion 在本机被 ACL 设成"只能新建、不能覆盖"
    //  —— config.json 写不回去，于是主题/开关改完重启就丢、坏掉的配置文件也永远修不回来
    //  （2026-09-15 用户实测：config.json 里三个 $10/$11/$12 非法占位符让它永久解析失败）。
    public static string DataDirPointerPath
    {
        get
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Fluxion", "datadir.txt");
            }
            catch { return null; }
        }
    }

    // 品牌迁移前的旧指针位置 —— 用户若自定义过数据目录，指针还写在旧品牌目录里，读不到会丢
    public static string LegacyDataDirPointerPath
    {
        get
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "GameBoost-DLSSG", "datadir.txt");
            }
            catch { return null; }
        }
    }

    static string ReadPointerFile(string f)
    {
        try
        {
            if (f == null || !File.Exists(f)) return null;
            string s = File.ReadAllText(f, Encoding.UTF8).Trim().Trim('"');
            if (s.Length > 2 && Directory.Exists(s)) return s;
        }
        catch { }
        return null;
    }

    public static string ReadDataDirPointer()
    {
        string s = ReadPointerFile(DataDirPointerPath);
        if (s == null) s = ReadPointerFile(LegacyDataDirPointerPath);   // 旧品牌指针兜底
        return s;
    }

    // ---- 品牌迁移（v1.0.0：GameBoost-DLSSG → Fluxion）----
    // 旧默认数据目录 %ProgramData%\GameBoost-DLSSG 里的 games.json / config.json / dlssg\source 等
    // **搬**到 %ProgramData%\Fluxion，搬完删除旧目录 —— 不留两份副本。
    // ⚠ 旧目录 ACL 特殊（只能新建、不能覆盖/改名/删除，2026-09-13 实测）：删除可能失败，
    //   失败就只记日志、留个空壳，绝不因此中断启动。
    // 幂等：目标已有同名文件 → 说明这份数据已经在用，删源即可（不覆盖、不回退）。
    //       install 包会先建 Fluxion 目录放 config.default.json，
    //       所以不能以"新目录已存在"为由跳过 —— 必须逐文件判断。
    public static void MigrateLegacyDataDir()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string oldDir = Path.Combine(root, "GameBoost-DLSSG");
            string newDir = Path.Combine(root, "Fluxion");
            if (!Directory.Exists(oldDir)) return;
            // 必须递归 —— dlssg\source\ 下是运行时代理源（version.dll 等，29MB），
            // 只搬顶层会让 SwitchEntry/InstallAt 找不到 0.3.x 源（plan_probe 第 9/12 节实测撞上）。
            int moved = 0, dropped = 0;
            MoveTree(oldDir, newDir, ref moved, ref dropped);
            if (moved > 0) Log("[迁移] 数据目录品牌迁移：已把 " + moved + " 个文件从 GameBoost-DLSSG 搬到 Fluxion");
            if (dropped > 0) Log("[迁移] 另有 " + dropped + " 个文件在新目录已存在，旧副本已删除");
            TryDeleteTree(oldDir);
        }
        catch (Exception ex) { try { Log("[迁移] 数据目录迁移失败: " + ex.Message); } catch { } }
    }

    // 递归搬移。目标已存在 → 删源（新目录里的才是正在用的那份）。返回后由调用方清目录。
    static void MoveTree(string src, string dst, ref int moved, ref int dropped)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src))
        {
            string t = Path.Combine(dst, Path.GetFileName(f));
            if (File.Exists(t))
            {
                // 新目录已有：旧的是过期副本，删掉（删不掉也无所谓，后面 TryDeleteTree 还会再试）
                try { File.Delete(f); dropped++; } catch { }
                continue;
            }
            try { File.Move(f, t); moved++; }
            catch
            {
                // 跨卷（理论上同在 %ProgramData% 不会）→ 退回 复制+删源
                try { File.Copy(f, t, false); File.Delete(f); moved++; } catch { }
            }
        }
        foreach (string d in Directory.GetDirectories(src))
            MoveTree(d, Path.Combine(dst, Path.GetFileName(d)), ref moved, ref dropped);
    }

    // 尽力删除目录树（先清文件再删空目录，自底向上）。ACL 不让删就静默留下，只回报成败。
    static bool TryDeleteTree(string dir)
    {
        bool ok = true;
        try
        {
            foreach (string d in Directory.GetDirectories(dir)) ok &= TryDeleteTree(d);
            foreach (string f in Directory.GetFiles(dir))
            {
                try { File.Delete(f); } catch { ok = false; }
            }
            try { Directory.Delete(dir, false); }
            catch { ok = false; }
        }
        catch { ok = false; }
        if (!ok) { try { Log("[迁移] 旧目录 " + dir + " 未能完全删除（ACL 限制），可手动删除，不影响使用"); } catch { } }
        else { try { Log("[迁移] 旧数据目录 GameBoost-DLSSG 已删除，无重复副本"); } catch { } }
        return ok;
    }

    public static string WriteDataDirPointer(string dir)
    {
        try
        {
            string f = DataDirPointerPath;
            if (f == null) return "无法定位 %LOCALAPPDATA%";
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, dir, new UTF8Encoding(false));
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string DataDir
    {
        get
        {
            if (dataDirCache != null) return dataDirCache;
            // 分享构建：不读数据目录指针文件（那是本机专属状态），
            // 便携（exe 目录可写）就地读写；装进 Program Files 这类只读目录则退回 %ProgramData%。
            if (ShareBuild)
            {
                dataDirCache = AppDir;
                try
                {
                    string probe = Path.Combine(AppDir, ".gb-write-test.tmp");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                }
                catch
                {
                    try
                    {
                        dataDirCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fluxion");
                    }
                    catch { }
                }
                return dataDirCache;
            }
            string custom = ReadDataDirPointer();          // 用户自选的数据目录优先（含旧品牌指针兜底）
            if (custom != null) { dataDirCache = custom; return dataDirCache; }
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string pd = Path.Combine(root, "Fluxion");
                string pdLegacy = Path.Combine(root, "GameBoost-DLSSG");
                if (File.Exists(Path.Combine(AppDir, "config.json"))) dataDirCache = AppDir;
                else if (Directory.Exists(pd)) dataDirCache = pd;
                else if (Directory.Exists(pdLegacy))
                { MigrateLegacyDataDir(); dataDirCache = pd; }   // 品牌迁移：把旧目录文件复制进新目录
                else dataDirCache = AppDir;
            }
            catch { dataDirCache = AppDir; }
            return dataDirCache;
        }
    }

    // ---- 数据目录迁移：**搬**而不是拷 ----
    //  同盘用 Move（瞬时、不落二次副本）；跨盘用 复制 → 校验字节数 → 删源。
    //  任何一项失败立刻中止并回报，绝不留"一半在新、一半在旧"的状态。
    public static string MigrateDataTo(string target)
    {
        string src = DataDir;
        if (target == null || target.Trim().Length == 0) return "目标目录为空";
        try
        {
            src = Path.GetFullPath(src).TrimEnd('\\');
            target = Path.GetFullPath(target.Trim()).TrimEnd('\\');
        }
        catch (Exception ex) { return "路径非法：" + ex.Message; }
        if (string.Equals(src, target, StringComparison.OrdinalIgnoreCase)) return "目标与当前数据目录相同";
        if (target.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) return "目标目录不能放在当前数据目录里面";

        string[] items = new string[] { "config.json", "library.json", "games.json",
            "gameProfiles.override.json", "ui.scale", "covers", "logs", "backup",
            "dlssg", "xess-pack", "dlssg030-pack" };

        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { return "无法创建目标目录：" + ex.Message; }

        // 目标里已有同名项 → 明确拒绝，避免覆盖用户既有文件
        foreach (string it in items)
        {
            string sp = Path.Combine(src, it), tp = Path.Combine(target, it);
            if (Directory.Exists(sp) && Directory.Exists(tp)) return "目标目录已存在 " + it + "\\，请换一个空目录";
            if (File.Exists(sp) && (File.Exists(tp) || Directory.Exists(tp))) return "目标目录已存在 " + it + "，请换一个空目录";
        }

        var moved = new List<string>();
        var leftovers = new List<string>();
        foreach (string it in items)
        {
            string sp = Path.Combine(src, it), tp = Path.Combine(target, it);
            long size = -1;
            if (File.Exists(sp)) size = new FileInfo(sp).Length;
            else if (Directory.Exists(sp)) size = DirSize(sp);
            else continue;                       // 本来就没有的项跳过
            try
            {
                // 同盘先试 Move（瞬时、不产生第二份数据）；被 ACL / 占用挡下就退回"复制+删源"
                bool movedOk = false;
                if (string.Equals(Path.GetPathRoot(sp), Path.GetPathRoot(tp), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(sp)) File.Move(sp, tp); else Directory.Move(sp, tp);
                        movedOk = true;
                    }
                    catch { movedOk = false; }
                }
                if (!movedOk)
                {
                    // 复制 → 逐项校验大小（不一致立即中止，源一律不删）→ 再删源
                    if (File.Exists(sp))
                    {
                        File.Copy(sp, tp, true);
                        if (new FileInfo(tp).Length != size) return "复制 " + it + " 后大小不一致，已中止（源文件未删）";
                    }
                    else
                    {
                        CopyDir(sp, tp);
                        if (DirSize(tp) != size) return "复制 " + it + " 后大小不一致，已中止（源目录未删）";
                    }
                    // 源删不掉不算失败：新目录已经是权威位置，指针一写就生效（残留只占空间）
                    try { if (File.Exists(sp)) File.Delete(sp); else Directory.Delete(sp, true); }
                    catch { leftovers.Add(it); }
                }
                moved.Add(it);
            }
            catch (Exception ex)
            {
                return "迁移 " + it + " 失败：" + ex.Message +
                       "（已成功搬走：" + (moved.Count > 0 ? string.Join("、", moved.ToArray()) : "无") + "，可手动搬回）";
            }
        }

        string werr = WriteDataDirPointer(target);
        if (werr.Length > 0)
            return "数据已搬到 " + target + "，但指针文件写入失败：" + werr + "（请手动把 datadir.txt 指向该目录）";
        dataDirCache = target;
        configPathCache = null;             // 让下一次取 ConfigPath 时按新目录重新判定
        if (leftovers.Count > 0)
            Log("数据迁移：以下源项因权限/占用删不掉，已保留在原目录（新目录已生效，可手动清理）："
                + string.Join("、", leftovers.ToArray()));
        return "";
    }

    static long DirSize(string d)
    {
        long n = 0;
        try { foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) { try { n += new FileInfo(f).Length; } catch { } } } catch { }
        return n;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
    }
    // 配置文件路径。**只读目录兜底**：主 config.json 写不进去时改用同目录的 config.local.json
    // 作覆盖层（存在即优先读），这样即便数据目录被 ACL 锁死，设置也能持久化。
    static string configPathCache;
    public static string ConfigPath
    {
        get
        {
            if (configPathCache != null) return configPathCache;
            try
            {
                string alt = Path.Combine(DataDir, "config.local.json");
                if (File.Exists(alt)) { configPathCache = alt; return configPathCache; }
            }
            catch { }
            configPathCache = Path.Combine(DataDir, "config.json");
            return configPathCache;
        }
    }
        static string LogFile;
        static string BackupFile;
        public static Config Cfg = new Config();
        // v1.0.0 = Fluxion 品牌首发（2026-09-20 由 GameBoost-DLSSG 全套改名而来）。
        // ⚠ 版本号在这里重开：改名换了安装包的 AppId，Windows 视作全新产品，
        //   旧的 3.6.x 线不再有升级关系（旧版需手动卸载）。两处版本号必须一起改（build.py --pkg 会校验）。
        public const string AppVersion = "1.0.9";
        public const string ShareVersion = "1.0.0";
        // ⚠ 必须用 #if 直接选常量（不能用运行时三元）：这样每个二进制里只留自己那条版本串，
        //   交付后可以直接在 exe 里搜 "SHARE-BUILD" 来证明「这份到底是不是分享构建」。
        //   （两个构建的代码只差一个 #define，体积会一模一样，光看大小分辨不出来。）
#if SHARE
        public const string DisplayVersion = ShareVersion;
        public const string BuildTag = "SHARE-BUILD";
#else
        public const string DisplayVersion = AppVersion;
        public const string BuildTag = "FULL-BUILD";
#endif
        // 分享构建（编译时 /define:SHARE）：
        //   只保留帧生成相关功能（游戏库 / 帧生成 / 说明），不跑任何"改本机"的子系统
        //   （看门狗·磁盘守护·覆盖层守护、游戏联动·远控、电源计划、服务、调度器、模拟器、FPS 档），
        //   也不读不写数据目录指针文件 —— 别人的机器上不该继承原作者的任何配置。
        public static readonly bool ShareBuild =
#if SHARE
            true;
#else
            false;
#endif
        static System.Threading.Mutex singleMutex;   // 静态根引用：进程存活期间不可被 GC 回收
        // ---- 看门狗引擎状态 ----
        static DateTime evtLast = DateTime.Now.AddSeconds(-15);   // 事件日志增量扫描游标
        static DateTime lastIncident = DateTime.MinValue;         // 取证快照 60s 冷却
        static DateTime lastDiskWarn = DateTime.MinValue;         // C 盘告警 6h 冷却
        static DateTime netLast = DateTime.MinValue;              // 网络哨兵计时
        static int guardTickN = 0;
        static List<Process> overlaySuspended = new List<Process>();

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            try { Directory.CreateDirectory(DataDir); } catch { }
            Cfg = Config.Load(ConfigPath);
            if (Config.LoadNote.Length > 0) { try { Log(Config.LoadNote); } catch { } }
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest(); return; }
            if (args.Length > 0 && args[0] == "--uiprobe")
            {
                // 真实启动自检：构造 MainForm —— 构造函数里就会跑 Switch()，UI 构建期的崩溃全在这里暴露
                // （2026-09-17：分享版裁侧栏后 navs[] 留空，Switch 解引用 null → 双击毫无反应，就是靠它抓出来的）。
                // 不动消息循环 ⇒ 定时器不会 tick，验证过程不会碰用户的机器。
                var sbp = new StringBuilder();
                sbp.AppendLine("BUILD: " + BuildTag + " share=" + ShareBuild + " version=" + DisplayVersion + " dataDir=" + DataDir);
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    using (var f = new MainForm(Cfg, true))
                        sbp.AppendLine(f != null ? "MAINFORM_OK" : "MAINFORM_NULL");
                }
                catch (Exception ex)
                {
                    sbp.AppendLine("MAINFORM_FAIL: " + ex.GetType().Name + ": " + ex.Message);
                    sbp.AppendLine(ex.StackTrace);
                }
                File.WriteAllText(Path.Combine(DataDir, "uiprobe.txt"), sbp.ToString(), Encoding.UTF8);
                return;
            }
            if (args.Length > 0 && args[0] == "--nvtest")
            {
                var sb2 = new StringBuilder();
                DetectSystem();
                sb2.AppendLine("SYSTEM: " + SystemSummary());
                sb2.AppendLine("NVAPI_AVAILABLE: " + NvDrs.Available());
                try { sb2.AppendLine("NVAPPLY: " + NvDrs.NvTest(FpsList(), Cfg.MmoGames, Cfg.AaaGames, Cfg.GachaGames)); }
                catch (Exception ex) { sb2.AppendLine("NVAPPLY_FAIL: " + ex.Message); }
                File.WriteAllText(Path.Combine(DataDir, "nvtest.txt"), sb2.ToString(), Encoding.UTF8);
                return;
            }
            if (args.Length > 0 && args[0] == "--nvclean")
            {
                // 清理诊断遗留的测试配置档
                var junk = new List<string> { "GBTestA", "GBTestB", "GBTestC", "GBTestD", "GBTestF", "GBTestG1", "GBTestG2", "GBTestG3", "GBTestG4" };
                File.WriteAllText(Path.Combine(DataDir, "nvtest.txt"), "NVCLEAN: " + NvDrs.DeleteProfiles(junk) + "\r\n", Encoding.UTF8);
                return;
            }
            bool startMinimized = Array.IndexOf(args, "--minimized") >= 0;
            // 管理员提权（杀 GameViewerService / 写 HKLM 需要）；GB_ELEV 防止重复弹窗
            if (!IsAdmin() && Environment.GetEnvironmentVariable("GB_ELEV") != "1")
            {
                Environment.SetEnvironmentVariable("GB_ELEV", "1");
                try
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                    psi.Arguments = startMinimized ? "--minimized" : "";
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return;
                }
                catch { /* 用户拒绝 UAC，降级普通权限运行 */ }
            }
            // 单实例锁第一道（提权之前）：探测已有实例，直接拦下，不再白弹一次 UAC。
            // OpenExisting 对提权实例持有的锁会抛 UnauthorizedAccessException——同样证明它存在
            bool alreadyRunning = false;
            try { var probe = System.Threading.Mutex.OpenExisting("Fluxion_SingleInstance"); probe.Close(); alreadyRunning = true; }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { alreadyRunning = true; }
            if (alreadyRunning)
            {
                if (!startMinimized)
                    MessageBox.Show("Fluxion 已在运行（图标在系统托盘，可能收在 ^ 折叠区）。", "Fluxion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 单实例锁第二道（提权之后正式持有）：
            // 必须用 static 字段根引用——局部 Mutex 在"最后一次使用后"即被 GC 回收，锁会悄悄失效
            //（The Misunderstood Mutex / GC.KeepAlive 官方文档记载的经典坑），此前多实例正是这么来的
            bool createdNew;
            singleMutex = new System.Threading.Mutex(true, "Fluxion_SingleInstance", out createdNew);
            if (!createdNew)
            {
                if (!startMinimized)
                    MessageBox.Show("Fluxion 已在运行（图标在系统托盘，可能收在 ^ 折叠区）。", "Fluxion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 全局未捕获异常 → 写日志（下次"未响应"时能定位根因，不再静默死）
            // ⚠ 必须连**堆栈首帧**一起记：只写 Message 的话，"ArgumentException: 参数无效" 这种
            //   在自绘程序里能来自几十处绘图调用，等于什么都没记 —— 2026-09-20 的整页绘制中断
            //   就是这么白耗掉一轮排查的（最后靠探针复现才拿到堆栈）。
            Application.ThreadException += (s, e) => Program.Log("UNCAUGHT_THREAD: " + (e.Exception != null
                ? e.Exception.GetType().Name + ": " + e.Exception.Message + "  @ " + Program.FirstFrame(e.Exception)
                : "?"));
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { var ex = e.ExceptionObject as Exception; Program.Log("UNCAUGHT_DOMAIN: " + (ex != null ? ex.GetType().Name + ": " + ex.Message + "  @ " + Program.FirstFrame(ex) : "?")); };
            // 主窗体**构造期**的异常发生在消息循环之外（Application.Run 的参数求值里），
            // ThreadException 抓不到 —— 表现就是"双击毫无反应"，用户完全无从下手
            // （2026-09-17 分享版实测：裁侧栏留下 null 按钮 → Switch() 解引用 null → 就是这个症状）。
            // 这里兜住并弹框 + 落盘，让任何启动期崩溃都变成一条能读的报错。
            try
            {
                Application.Run(new MainForm(Cfg, startMinimized));
            }
            catch (Exception ex)
            {
                string crash = "启动失败（主窗体构造期异常）\r\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                             + "\r\n\r\n" + ex.GetType().FullName + ": " + ex.Message
                             + "\r\n\r\n" + ex.StackTrace
                             + "\r\n\r\n版本：" + DisplayVersion + "（" + BuildTag + "）"
                             + "\r\n数据目录：" + DataDir;
                try { File.WriteAllText(Path.Combine(DataDir, "crash_startup.txt"), crash, Encoding.UTF8); } catch { }
                try { Log("STARTUP_CRASH: " + ex.GetType().Name + ": " + ex.Message); } catch { }
                MessageBox.Show(crash + "\r\n\r\n（已写入 crash_startup.txt，请把这段发给开发者）",
                                "Fluxion 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // 有序退出时把联动状态一并还原：挂起进程唤醒、临时电源/定时器还原；
            // 远控只在「当前没在游戏」时恢复——游戏还开着就恢复 UU远程 等于把掉帧元凶请回来
            try { GameBoostCleanupOrphans(); } catch { }
            try { GamePowerRecoverOrphan(); } catch { }
            try { RevertGameTimer(); } catch { }
            try { if (ResLink.Active) foreach (string l in ResLink.Restore()) Log("[分辨率联动] " + l); } catch { }
            try { if (RemotePausedByLink && !IsGameRunning()) ResumeAllRemote(); } catch { }
            // 退出留痕：下次窗口无声消失时，能区分"用户点了退出"还是"被外部终止"
            //（正常走这条写入 = 有序退出；日志里没有这一行 = 进程是被杀掉的）
            Log("GUI 已退出（有序关闭，游戏联动停止）");
        }

        // 本机 NVIDIA 显卡名 + 真实驱动版本号（如 610.88）。
        //  注册表里那串 DriverVersion 形如 32.0.16.1088 —— 把**最后两段拼起来取末 5 位**
        //  才是官方版本号（161088 → 610.88；591.86 对应 32.0.15.9186）。
        public static void NvDriver(out string name, out double ver)
        {
            name = ""; ver = 0;
            try
            {
                string baseKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (var k = Registry.LocalMachine.OpenSubKey(baseKey))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;              // 只要 0000/0001 这种实例键
                        using (var sk = k.OpenSubKey(sub))
                        {
                            if (sk == null) continue;
                            string desc = sk.GetValue("DriverDesc") as string;
                            string dv = sk.GetValue("DriverVersion") as string;
                            if (desc == null || dv == null) continue;
                            if (desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0) continue;   // 跳过虚拟屏
                            name = desc;
                            string[] parts = dv.Split('.');
                            if (parts.Length >= 2)
                            {
                                string tail = parts[parts.Length - 2] + parts[parts.Length - 1];
                                if (tail.Length > 5) tail = tail.Substring(tail.Length - 5);
                                int n;
                                if (int.TryParse(tail, out n)) ver = n / 100.0;
                            }
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        // 主显示器当前模式（只读）。为什么需要：帧生成的**倍率上限由显示器刷新率决定**
        //  —— 生成帧超过刷新率就是无效帧。本工具此前完全没有刷新率读数，用户问
        //  "6X 值不值得开"时无法回答（2026-09-16）。
        public static bool DisplayMode(out int w, out int h, out int hz)
        {
            w = h = hz = 0;
            try
            {
                Native.DEVMODE dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                if (!Native.EnumDisplaySettings(null, -1, ref dm)) return false;   // -1 = ENUM_CURRENT_SETTINGS
                w = dm.dmPelsWidth; h = dm.dmPelsHeight; hz = dm.dmDisplayFrequency;
                return w > 0 && h > 0;
            }
            catch { return false; }
        }

        public static int RefreshHz()
        {
            int w, h, hz;
            return DisplayMode(out w, out h, out hz) ? hz : 0;
        }

        // ---- NVIDIA 驱动版本基线（只记录 + 提示，不自动执行任何动作）----
        //  治的是"更新驱动后第一次进游戏特别卡"：那是着色器缓存被重建，容易被当成工具出问题。
        //  基线在「一键优化」时记录，体检表只比对、不写入（读函数不产生副作用）。
        // 驱动基线文件路径。正常走数据目录。
        //  DriverFileOverride 是**测试沙盒出口**：%ProgramData%\Fluxion 在本机对普通权限
        //  只允许「新建」、不允许「覆盖」（实测覆盖直接 Errno 13 / WinError 5），所以探针在非管理员下
        //  没法在真实路径上做「写入 → 比对 → 还原」的往返测试，只能把路径指到临时目录。
        //  用 internal 而非 private：探针与主程序一起编译（同一程序集），可直接赋值，无需反射。
        internal static string DriverFileOverride;
        static string DriverFile
        {
            get
            {
                if (DriverFileOverride != null && DriverFileOverride.Length > 0) return DriverFileOverride;
                return Path.Combine(DataDir, "nv-driver.txt");
            }
        }

        public static string DriverVerNow()
        {
            string nm; double v;
            NvDriver(out nm, out v);
            return v > 0 ? v.ToString("F2") : "";
        }

        // 把当前驱动版本记为基线；返回提示串（版本有变化时非空，否则空串）
        public static string MarkDriverBaseline()
        {
            try
            {
                string cur = DriverVerNow();
                if (cur.Length == 0) return "";
                string old = File.Exists(DriverFile) ? File.ReadAllText(DriverFile, Encoding.UTF8).Trim() : "";
                File.WriteAllText(DriverFile, cur, new UTF8Encoding(false));
                if (old.Length == 0 || old == cur) return "";
                return "NVIDIA 驱动已从 " + old + " 更新到 " + cur
                     + "：更新后第一次进游戏会重建着色器缓存、比较卡，属正常现象（着色器缓存已设为无限制）。";
            }
            catch { return ""; }
        }

        // 真实 Windows build（26200.9168 这种）。
        //  Environment.OSVersion 在 .NET Framework 下被兼容层压成 6.2/9200，
        //  读注册表 CurrentBuildNumber + UBR 才拿得到真值。
        // ---- 游戏时长统计（playtime.json，键 = Lib.IdOf(game)）----
        //  为什么单独一份文件：library.json 的读写是"文本手术"式的，往里加字段风险大；
        //  时长是纯附加数据，独立小文件最稳、也不影响其它读取方。
        static Dictionary<string, long> playTime;
        public static string PlayTimePath { get { return Path.Combine(DataDir, "playtime.json"); } }

        static void PlayTimeLoad()
        {
            if (playTime != null) return;
            playTime = new Dictionary<string, long>();
            try
            {
                if (File.Exists(PlayTimePath))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(PlayTimePath, Encoding.UTF8));
                    foreach (var kv in d) { try { playTime[kv.Key] = Convert.ToInt64(kv.Value); } catch { } }
                }
            }
            catch { }
        }

        public static long PlayTimeOf(string key)
        {
            if (key == null || key.Length == 0) return 0;
            PlayTimeLoad();
            long v;
            return playTime.TryGetValue(key, out v) ? v : 0;
        }

        public static void PlayTimeAdd(string key, long secs)
        {
            if (key == null || key.Length == 0 || secs <= 0) return;
            PlayTimeLoad();
            long v;
            playTime.TryGetValue(key, out v);
            playTime[key] = v + secs;
            try { File.WriteAllText(PlayTimePath, new JavaScriptSerializer().Serialize(playTime), new UTF8Encoding(false)); }
            catch { }
        }

        public static string FmtPlay(long secs)
        {
            if (secs < 60) return secs + " 秒";
            if (secs < 3600) return (secs / 60) + " 分钟";
            return (secs / 3600) + " 小时 " + ((secs % 3600) / 60) + " 分";
        }

        // 配置导出/导入用（原来的 CopyDir 是私有的）
        public static void CopyDirTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string d in Directory.GetDirectories(from)) CopyDirTree(d, Path.Combine(to, Path.GetFileName(d)));
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }

        public static string WindowsBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object cb = k.GetValue("CurrentBuildNumber");
                        object ubr = k.GetValue("UBR");
                        if (cb != null)
                            return cb.ToString() + (ubr != null ? "." + ubr.ToString() : "");
                    }
                }
            }
            catch { }
            try { return Environment.OSVersion.Version.Build.ToString(); } catch { }
            return "未知";
        }

        public static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // exe 内嵌图标（compile.ps1 经 csc /win32icon 嵌入），托盘/窗体共用；失败回退系统图标
        public static Icon GetAppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); }
            catch { return SystemIcons.Application; }
        }

        // ============ 工具函数 ============
        public static void EnsureLog()
        {
            if (LogFile != null) return;
            string dir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(dir);
            // 日志自清理：超 14 天的旧日志删除（防无限膨胀）
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles())
                    if ((DateTime.Now - f.LastWriteTime).TotalDays > 14)
                        try { f.Delete(); } catch { }
            }
            catch { }
            LogFile = Path.Combine(dir, "fluxion_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        }
        public static void Log(string msg)
        {
            EnsureLog();
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            try { File.AppendAllText(LogFile, line + "\r\n", Encoding.UTF8); } catch { }
        }

        // 取异常堆栈里第一帧属于**本程序**的方法（跳过 System.Drawing / System.Windows.Forms 的包装帧）。
        //   自绘程序里"参数无效"这类 GDI+ 异常，光看类型和消息完全无法定位；
        //   有了这一帧，日志里就能直接读出是哪个控件、哪个方法在已销毁的句柄上作图。
        public static string FirstFrame(Exception ex)
        {
            try
            {
                if (ex == null) return "";
                string st = ex.StackTrace ?? "";
                if (st.Length == 0) return "";
                string[] lines = st.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.IndexOf("Fluxion.", StringComparison.Ordinal) >= 0)
                        return t.Replace("在 ", "").Replace("at ", "");
                }
                return lines[0].Trim();
            }
            catch { return ""; }
        }

        // ==================== 看门狗整合模块（v1.0.5）====================
        // 由 GUI sysTimer 每 4s 调一次；每个子模块自带节流，开销极低
        public static void GuardTick()
        {
            guardTickN++;
            if (Cfg.IncidentEnable && guardTickN % 5 == 0) WatchDisplayEvents();   // 每 20s 扫一次事件日志
            if (Cfg.DiskGuardEnable && guardTickN % 150 == 0) DiskGuardTick();     // 每 10 分钟查一次 C 盘
            if (Cfg.NetWatchEnable) NetWatchTick();
        }

        // 黑屏取证：System 日志增量扫 nvlddmkm（全级别）+ Kernel-Power 41，命中即写快照
        static void WatchDisplayEvents()
        {
            try
            {
                // WMI DMTF 时间（UTC），WMI 自行换算时区
                string since = evtLast.ToUniversalTime().ToString("yyyyMMddHHmmss.000000+000");
                evtLast = DateTime.Now;
                var wql = "SELECT RecordNumber, TimeWritten, SourceName, EventCode, Message FROM Win32_NTLogEvent " +
                          "WHERE LogFile='System' AND TimeWritten > '" + since + "' AND " +
                          "(SourceName='nvlddmkm' OR (SourceName='Microsoft-Windows-Kernel-Power' AND EventCode=41))";
                foreach (var o in new ManagementObjectSearcher("root\\CIMV2", wql).Get())
                {
                    string src = "" + o["SourceName"], code = "" + o["EventCode"], tw = "" + o["TimeWritten"];
                    string msg = o["Message"] as string;
                    WriteIncident(src, code, tw, msg);
                }
            }
            catch { }   // 事件日志读取失败静默跳过，不能影响主流程
        }

        static void WriteIncident(string src, string code, string timeWmi, string msg)
        {
            bool full = (DateTime.Now - lastIncident).TotalSeconds > 60;   // 一次黑屏常连爆多条：首条全量，后续只留一行
            if (full) lastIncident = DateTime.Now;
            try
            {
                string dir = Path.Combine(DataDir, "logs"); Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, "incident_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (full ? "" : "_b.txt"));
                var sb = new StringBuilder();
                sb.AppendLine("=== GameBoost 黑屏取证快照 ===");
                sb.AppendLine("快照时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("触发事件: " + src + "  事件ID: " + code + "  系统记录时间: " + WmiToDate(timeWmi));
                if (!full)
                {
                    sb.AppendLine("（冷却期内续发事件，详情见同秒前一份快照）");
                }
                else
                {
                    if (!string.IsNullOrEmpty(msg)) sb.AppendLine("事件消息: " + msg.Trim().Replace("\r", " ").Replace("\n", " "));
                    sb.AppendLine();
                    sb.AppendLine("--- 显示适配器 ---");
                    try
                    {
                        foreach (var v in new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, DriverVersion, Status FROM Win32_VideoController").Get())
                            sb.AppendLine("" + v["Name"] + " | 驱动 " + v["DriverVersion"] + " | 状态 " + v["Status"]);
                    }
                    catch { }
                    try { var d = new DriveInfo("C"); sb.AppendLine("C 盘剩余: " + (d.AvailableFreeSpace / 1073741824.0).ToString("F1") + " GB"); } catch { }
                    sb.AppendLine();
                    sb.AppendLine("--- 进程快照（累计 CPU 时间前 10）---");
                    var ps = Process.GetProcesses();
                    Array.Sort(ps, delegate (Process a, Process b) { return SafeCpu(b).CompareTo(SafeCpu(a)); });
                    int n = 0;
                    foreach (var p in ps)
                    {
                        if (n >= 10) break;
                        try
                        {
                            string path = "";
                            try { path = p.MainModule.FileName; } catch { }
                            sb.AppendLine(p.ProcessName + " (PID " + p.Id + ") CPU=" + p.TotalProcessorTime.ToString(@"hh\:mm\:ss") + " 内存=" + (p.WorkingSet64 / 1048576.0).ToString("F0") + "MB" + (path != "" ? "  " + path : ""));
                            n++;
                        }
                        catch { }
                    }
                    foreach (var p in ps) try { p.Dispose(); } catch { }
                }
                File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
                Log("⚠ 检测到 GPU/电源事件 " + src + " id=" + code + " → 已写取证快照 " + Path.GetFileName(f));
            }
            catch { }
        }

        static string WmiToDate(string dmtf)
        {
            try
            {
                // yyyyMMddHHmmss.ffffff(+/-)MMM → 本地时间
                int offMin = int.Parse(dmtf.Substring(21).Replace(".", ""));
                DateTime utc = DateTime.ParseExact(dmtf.Substring(0, 14), "yyyyMMddHHmmss", null);
                return utc.AddMinutes(-offMin).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { return dmtf; }
        }

        static TimeSpan SafeCpu(Process p) { try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; } }

        // C 盘守护：低于阈值告警（6h 冷却）+ 可选自动清理过期日志
        static void DiskGuardTick()
        {
            try
            {
                var d = new DriveInfo("C");
                double freeGb = d.AvailableFreeSpace / 1073741824.0;
                if (freeGb < Cfg.DiskWarnFreeGb && (DateTime.Now - lastDiskWarn).TotalMinutes > 360)
                {
                    lastDiskWarn = DateTime.Now;
                    Log("⚠ C 盘剩余空间不足: " + freeGb.ToString("F1") + " GB（阈值 " + Cfg.DiskWarnFreeGb + " GB），建议清理系统还原点/WU 缓存");
                }
                if (Cfg.DiskCleanLogDays > 0)
                {
                    string dir = Path.Combine(DataDir, "logs");
                    if (Directory.Exists(dir))
                    {
                        var cut = DateTime.Now.AddDays(-Cfg.DiskCleanLogDays);
                        foreach (var f in Directory.GetFiles(dir))
                            try { if (File.GetLastWriteTime(f) < cut) File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        // 网络丢包哨兵：周期 ping 网关 + 223.5.5.5，结果追加 netwatch.csv；丢包才写主日志
        static void NetWatchTick()
        {
            if ((DateTime.Now - netLast).TotalMinutes < Cfg.NetWatchEveryMin) return;
            netLast = DateTime.Now;
            try
            {
                string gw = null;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var g in ni.GetIPProperties().GatewayAddresses)
                        if (g != null && g.Address.ToString() != "0.0.0.0") { gw = g.Address.ToString(); break; }
                    if (gw != null) break;
                }
                string dir = Path.Combine(DataDir, "logs"); Directory.CreateDirectory(dir);
                string csv = Path.Combine(dir, "netwatch.csv");
                if (!File.Exists(csv)) File.AppendAllText(csv, "time,target,sent,loss,latencyMs\r\n", Encoding.UTF8);
                int sent, loss; double avg;
                PingTarget(gw ?? "223.5.5.5", out sent, out loss, out avg);
                File.AppendAllText(csv, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ",gateway(" + (gw ?? "n/a") + ")," + sent + "," + loss + "," + avg.ToString("F1") + "\r\n", Encoding.UTF8);
                if (loss > 0) Log("⚠ 网关 " + gw + " 丢包 " + loss + "/" + sent + "（平均 " + avg.ToString("F0") + " ms）——帧率卡顿时可对照此时间戳区分网络/本机因素");
                PingTarget("223.5.5.5", out sent, out loss, out avg);
                File.AppendAllText(csv, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ",223.5.5.5," + sent + "," + loss + "," + avg.ToString("F1") + "\r\n", Encoding.UTF8);
                if (loss > 0) Log("⚠ 外网 223.5.5.5 丢包 " + loss + "/" + sent + "（平均 " + avg.ToString("F0") + " ms）");
            }
            catch { }
        }

        static void PingTarget(string host, out int sent, out int loss, out double avg)
        {
            sent = 4; loss = 0; avg = 0; int ok = 0;
            using (var ping = new System.Net.NetworkInformation.Ping())
            {
                for (int i = 0; i < sent; i++)
                {
                    try
                    {
                        var r = ping.Send(host, 1500);
                        if (r.Status == System.Net.NetworkInformation.IPStatus.Success) { avg += r.RoundtripTime; ok++; }
                        else loss++;
                    }
                    catch { loss++; }
                }
            }
            avg = ok > 0 ? avg / ok : -1;
        }

        // NVIDIA 覆盖层守护：进游戏挂起（不是杀，可恢复），退出还原
        public static void OverlaySync(bool game)
        {
            try
            {
                if (!Cfg.OverlayGuardEnable || Cfg.OverlayList.Count == 0) return;
                if (game)
                {
                    if (overlaySuspended.Count > 0) return;
                    foreach (string name in Cfg.OverlayList)
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try
                            {
                                if (Native.NtSuspendProcess(p.Handle) == 0)
                                {
                                    overlaySuspended.Add(p);
                                    Log("已挂起覆盖层进程: " + p.ProcessName + " (PID " + p.Id + ")");
                                }
                            }
                            catch (Exception ex) { Log("挂起 " + p.ProcessName + " 失败: " + ex.Message); try { p.Dispose(); } catch { } }
                        }
                }
                else if (overlaySuspended.Count > 0)
                {
                    int resumed = 0;
                    foreach (var p in overlaySuspended)
                    {
                        try { Native.NtResumeProcess(p.Handle); resumed++; } catch { }
                        try { p.Dispose(); } catch { }
                    }
                    Log("已恢复覆盖层进程 x" + resumed);
                    overlaySuspended.Clear();
                }
            }
            catch (Exception ex) { Log("OverlaySync 异常: " + ex.Message); }
        }

        static string GetBackupFile()
        {
            if (BackupFile == null)
            {
                string dir = Path.Combine(DataDir, "backup");
                Directory.CreateDirectory(dir);
                BackupFile = Path.Combine(dir, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            }
            return BackupFile;
        }

        public static void AddBackupEntry(Dictionary<string, object> entry)
        {
            string bf = GetBackupFile();
            var list = new List<Dictionary<string, object>>();
            if (File.Exists(bf))
            {
                try
                {
                    var ex = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(bf, Encoding.UTF8));
                    if (ex != null) list = ex;
                }
                catch { }
            }
            foreach (var e in list)
                if (e.ContainsKey("Type") && e["Type"].ToString() == entry["Type"].ToString() &&
                    e.ContainsKey("Path") && e["Path"].ToString() == entry["Path"].ToString() &&
                    e.ContainsKey("Name") && e["Name"].ToString() == entry["Name"].ToString())
                    return; // 幂等
            list.Add(entry);
            try { File.WriteAllText(bf, new JavaScriptSerializer().Serialize(list), Encoding.UTF8); } catch { }
        }

        public static void BackupReg(string path, string name)
        {
            var entry = new Dictionary<string, object>();
            entry["Type"] = "registry"; entry["Path"] = path; entry["Name"] = name;
            try
            {
                var key = RegKey(path, true);
                if (key != null && key.GetValue(name) != null)
                {
                    entry["Value"] = key.GetValue(name);
                    entry["ValueType"] = key.GetValueKind(name).ToString();
                    entry["Exists"] = true;
                    key.Close();
                }
                else { entry["Exists"] = false; if (key != null) key.Close(); }
            }
            catch { entry["Exists"] = false; }
            AddBackupEntry(entry);
        }

        public static void BackupPowerScheme()
        {
            string guid = GetActiveSchemeGuid();
            if (guid != "")
            {
                var entry = new Dictionary<string, object>();
                entry["Type"] = "power_scheme"; entry["Value"] = guid;
                AddBackupEntry(entry);
            }
        }

        public static string GetActiveSchemeGuid()
        {
            var m = Regex.Match(RunCmd("powercfg", "/getactivescheme"), @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ---- 游戏期临时电源计划联动：进游戏切高性能档，退出还原原计划（崩溃/强杀后下次启动自恢复） ----
        static string GamePowerFile { get { return Path.Combine(DataDir, "backup", "game_power_state.txt"); } }
        public static void GamePowerOn()
        {
            if (!Cfg.PowerGameSwitch) return;
            try
            {
                string cur = GetActiveSchemeGuid();
                if (cur == "") return;
                string target = Cfg.PowerScheme == "ultimate" ? "e9a42b02-d5df-448d-aa00-03f14749eb61" : "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
                if (cur.Equals(target, StringComparison.OrdinalIgnoreCase)) return;   // 已是目标计划，无需切换
                Directory.CreateDirectory(Path.GetDirectoryName(GamePowerFile));
                File.WriteAllText(GamePowerFile, cur);
                if (!RunCmd("powercfg", "/list").Contains(target))
                    RunCmd("powercfg", "/duplicatescheme " + target);
                RunCmd("powercfg", "/setactive " + target);
                Log("[游戏电源联动] 已临时切换至" + (Cfg.PowerScheme == "ultimate" ? "卓越性能" : "高性能") + "（原计划已记录，退出游戏自动还原）");
            }
            catch (Exception ex) { Log("[游戏电源联动] 切换失败: " + ex.Message); }
        }
        public static void GamePowerOff()
        {
            try
            {
                if (!File.Exists(GamePowerFile)) return;
                string guid = File.ReadAllText(GamePowerFile, Encoding.UTF8).Trim();
                if (guid != "") RunCmd("powercfg", "/setactive " + guid);
                File.Delete(GamePowerFile);
                Log("[游戏电源联动] 已还原原电源计划 " + guid);
            }
            catch (Exception ex) { Log("[游戏电源联动] 还原失败: " + ex.Message); }
        }
        public static void GamePowerRecoverOrphan()
        {
            try
            {
                if (!File.Exists(GamePowerFile)) return;
                string guid = File.ReadAllText(GamePowerFile, Encoding.UTF8).Trim();
                if (guid != "") RunCmd("powercfg", "/setactive " + guid);
                File.Delete(GamePowerFile);
                Log("[游戏电源联动] 检测到上次游戏会话未还原的电源计划，已自动恢复为 " + guid);
            }
            catch { }
        }

        static RegistryKey RegKey(string path, bool writable)
        {
            try
            {
                string sub = path.Substring(5).TrimStart('\\');
                if (path.StartsWith("HKCU:")) return Registry.CurrentUser.OpenSubKey(sub, writable);
                if (path.StartsWith("HKLM:")) return Registry.LocalMachine.OpenSubKey(sub, writable);
            }
            catch { }
            return null;
        }

        public static object ReadReg(string path, string name)
        {
            try
            {
                var key = RegKey(path, false);
                if (key != null) { object v = key.GetValue(name); key.Close(); return v; }
            }
            catch { }
            return null;
        }

        public static void WriteReg(string path, string name, object value, RegistryValueKind kind)
        {
            try
            {
                string sub = path.Substring(5).TrimStart('\\');
                RegistryKey hive = path.StartsWith("HKCU:") ? Registry.CurrentUser : Registry.LocalMachine;
                using (var key = hive.CreateSubKey(sub))
                    if (key != null)
                    {
                        // 旧值类型不对时（如 REG_SZ 残留）SetValue 直接抛"类型与 RegistryValueKind 不匹配"；
                        // 先删同名值再按指定类型写入，避免 SystemProfile 那几个键每次优化都报一行错
                        key.DeleteValue(name, false);
                        key.SetValue(name, value, kind);
                    }
            }
            catch (Exception ex) { Log("注册表写入失败 " + path + "\\" + name + ": " + ex.Message); }
        }

        public static string RunCmd(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }
                using (var p = Process.Start(psi))
                {
                    // 先两路异步读、再限时等退出：同步 ReadToEnd 一路时，另一路管道(4KB)写满会互锁；
                    // 常驻子进程（-Monitor）则 ReadToEnd 永远等不到 EOF——两者都会把调用线程永久挂死
                    var tOut = p.StandardOutput.ReadToEndAsync();
                    var tErr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000))
                    { try { p.Kill(); } catch { } }
                    try { tOut.Wait(3000); } catch { }
                    return tOut.IsCompleted ? tOut.Result : "";
                }
            }
            catch { return ""; }
        }

        static string GetPowerCfgSetting(string sub, string setting)
        {
            string q = RunCmd("powercfg", "/q SCHEME_CURRENT " + sub + " " + setting);
            foreach (string rawLine in q.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Contains("当前交流电源设置索引") || line.Contains("AC Power Setting Index"))
                {
                    var m = Regex.Match(line, @"\((\d+)\)");
                    if (m.Success) return m.Groups[1].Value;
                    m = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                    if (m.Success) { try { return Convert.ToInt32(m.Groups[1].Value, 16).ToString(); } catch { } }
                    return line;
                }
            }
            return "?";
        }

        public static void KillProcess(string name)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                { try { p.Kill(); } catch { } }
            }
            catch { }
        }

        public static void StopService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    { sc.Stop(); try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8)); } catch { } }
                }
            }
            catch { }
        }
        public static void StartService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            }
            catch { }
        }

        public static bool IsGameRunning()
        {
            foreach (var gp in AllGameProcesses())
                if (ProcExists(gp)) return true;
            return false;
        }

        // 三档游戏档案：FPS(竞技) / MMO(网游) / AAA(3A)。fps 列表为空时沿用 gameAware.gameProcesses（兼容旧配置）
        public static List<string> FpsList() { return Cfg.FpsGames ?? Cfg.GameProcesses; }
        public static List<string> AllGameProcesses()
        {
            var all = new List<string>();
            if (Cfg == null) return all;
            // 名单一律 null 安全（config.local.json 缺键时会是 null —— 见 AutoCategory 的注释）
            AddNames(all, FpsList());
            AddNames(all, Cfg.GameProcesses);
            AddNames(all, Cfg.MmoGames);
            AddNames(all, Cfg.AaaGames);
            AddNames(all, Cfg.GachaGames);
            // ★ 再把游戏库里"是游戏"的条目一并纳入（去 .exe）——
            //   否则**后续新加的游戏**不在任何静态名单里，就根本不被认作"游戏"：
            //   电源联动 / 加速包 / 定时器 / 亲和性与 P 核绑定全都轮不到它，
            //   手动档位下拉（Ui 里遍历本方法）也看不到它。2026-09-16 用户要求"判定对新加的游戏生效"。
            //   对战平台（LinkCategory 返回空）明确排除 —— 它们不是游戏。
            try
            {
                foreach (var g in Dlssg.Library)
                {
                    if (g == null || g.Ignored) continue;
                    if (Dlssg.LinkCategory(g).Length == 0) continue;      // 不是游戏
                    string exe = (g.Exe == null) ? "" : Path.GetFileNameWithoutExtension(g.Exe);
                    if (exe.Length == 0) continue;
                    if (!all.Contains(exe)) all.Add(exe);
                }
            }
            catch { }
            return all;
        }

        // ---- 档位判定：自动（按列表）+ 手动挡覆盖（gameProfiles.override.json，GUI 写入） ----
        // 进程名 → 游戏中文名（同一游戏的不同版本进程归并到同一名字，手动档位按游戏生效）
        static readonly string[,] GameDisplay = {
            {"cs2", "CS2"}, {"VALORANT", "无畏契约"}, {"RiotClientServices", "无畏契约启动器"},
            {"DeltaForceClient-Win64-Shipping", "三角洲行动"}, {"DeltaForce-Win64-Shipping", "三角洲行动"},
            {"YuanShen", "原神"}, {"GenshinImpact", "原神"},
            {"Client-Win64-Shipping", "鸣潮"},
            {"StarRail", "崩坏：星穹铁道"}, {"ZenlessZoneZero", "绝区零"},
            {"ForzaHorizon4", "极限竞速：地平线4"}, {"ForzaHorizon5", "极限竞速：地平线5"}, {"ForzaHorizon6", "极限竞速：地平线6"},
            {"Cyberpunk2077", "赛博朋克2077"}, {"EldenRing", "艾尔登法环"},
        };
        public static string GameDisplayName(string processName)
        {
            for (int i = 0; i < GameDisplay.GetLength(0); i++)
                if (GameDisplay[i, 0].Equals(processName, StringComparison.OrdinalIgnoreCase)) return GameDisplay[i, 1];
            // 内置表里没有的（后续新加的游戏）→ 从游戏库取显示名。
            //  否则手动档位下拉里会出现原始进程名（例如 2077 的 REDprelauncher、刚下载的 xxx.exe）。
            try
            {
                DlssgGame g = Dlssg.FindByProcess(processName);
                if (g != null && g.Title != null && g.Title.Length > 0) return g.Title;
            }
            catch { }
            return processName;
        }
        // 手动挡总开关（存 override 文件的 "_mode" 键；自动挡=忽略一切手动覆盖）
        public static bool ManualMode = false;
        // 总场景：false=游戏（正常档位体系） true=办公（整机不触发任何游戏联动，存 "_scene" 键）
        public static bool SceneOffice = false;
        static Dictionary<string, string> ProfileOverrides = new Dictionary<string, string>();
        static string OverrideFile { get { return Path.Combine(DataDir, "gameProfiles.override.json"); } }
        public static void LoadProfileOverrides()
        {
            try
            {
                if (!File.Exists(OverrideFile)) return;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(OverrideFile, Encoding.UTF8));
                if (d == null) return;
                foreach (var kv in d)
                {
                    if (kv.Key == "_mode") { ManualMode = kv.Value.ToString() == "manual"; continue; }
                    if (kv.Key == "_scene") { SceneOffice = kv.Value.ToString() == "office"; continue; }
                    ProfileOverrides[kv.Key] = kv.Value.ToString();
                }
            }
            catch { }
        }
        public static string GetProfileOverride(string processName)
        {
            string o;
            return ProfileOverrides.TryGetValue(processName, out o) ? o : null;
        }
        public static void SetProfileOverride(string gameDisplayName, string category)
        {
            if (category == null) ProfileOverrides.Remove(gameDisplayName);
            else ProfileOverrides[gameDisplayName] = category;
            SaveProfileState();
        }
        public static void SetManualMode(bool manual)
        {
            ManualMode = manual;
            SaveProfileState();
        }
        public static void SetSceneOffice(bool office)
        {
            SceneOffice = office;
            SaveProfileState();
        }
        static void SaveProfileState()
        {
            try
            {
                var full = new Dictionary<string, string>(ProfileOverrides);
                full["_mode"] = ManualMode ? "manual" : "auto";
                full["_scene"] = SceneOffice ? "office" : "game";
                File.WriteAllText(OverrideFile, new JavaScriptSerializer().Serialize(full), Encoding.UTF8);
            }
            catch (Exception ex) { Log("档位状态保存失败: " + ex.Message); }
        }

        // 自动归类：按列表归属
        public static string AutoCategory(string processName)
        {
            // ① 先问游戏库：分类与「方案推荐」共用同一套规则（Dlssg.RecommendCategory），
            //    所以**后续新加的游戏**（新装的二游 / 3A、手动添加的条目）不用改名单就能被正确归类。
            //    返回 "" 表示"不是游戏"（对战平台 / 启动器）→ 不参与任何游戏联动。
            try
            {
                DlssgGame g = Dlssg.FindByProcess(processName);
                if (g != null) return Dlssg.LinkCategory(g);
            }
            catch { }
            // ② 静态名单兜底（库还没扫出来时，例如刚开机那一小段）
            //    ⚠ 一律走 null 安全判定：config.local.json 缺键时名单会是 null，
            //      旧代码 `Cfg.MmoGames.Contains(...)` 会直接抛 NullReferenceException
            //      （2026-09-16 探针实测撞上；同样的问题也会发生在真实运行时）。
            if (Cfg == null) return "aaa";
            if (NameHas(Cfg.MmoGames, processName)) return "mmo";
            if (NameHas(Cfg.AaaGames, processName)) return "aaa";
            if (NameHas(Cfg.GachaGames, processName)) return "gacha";
            if (NameHas(Cfg.FpsGames, processName) || NameHas(Cfg.GameProcesses, processName)) return "fps";
            // ③ 都不认识 → 不再默认成 "fps"
            //    旧代码这里 `return "fps"`，后果是：名单之外的游戏（新装的）被判成竞技网游 →
            //    玩它的时候 UU远程 被暂停。用户实测的"启动二游也杀远控"同源。
            //    未知就当"是游戏但不竞技"：电源 / 加速包 / 亲和性照做，远控不碰。
            return "aaa";
        }

        // 名单判定（null 安全）
        static bool NameHas(List<string> list, string name)
        {
            if (list == null || name == null || name.Length == 0) return false;
            return list.Contains(name);
        }

        // 把名单里的项并入清单（去重、null 安全）
        static void AddNames(List<string> all, List<string> list)
        {
            if (all == null || list == null) return;
            foreach (var x in list)
                if (x != null && x.Length > 0 && !all.Contains(x)) all.Add(x);
        }

        // 当前在跑的游戏进程名（无则 null）
        public static string GetRunningGame()
        {
            foreach (var g in AllGameProcesses()) if (ProcExists(g)) return g;
            return null;
        }

        // 当前档位：手动挡时覆盖优先（按游戏中文名），其次自动归类。"office"=办公档（不触发游戏联动）
        public static string GetRunningCategory()
        {
            string g = GetRunningGame();
            if (g == null) return null;
            if (SceneOffice) return "office";   // 办公场景：整机不触发游戏联动
            if (ManualMode)
            {
                string o;
                if (ProfileOverrides.TryGetValue(GameDisplayName(g), out o)) return o;
            }
            return AutoCategory(g);
        }

        // ============ 硬件/系统探测（自适应优化依据；后台线程填充，失败取保守默认） ============
        public static string CpuName = "(探测中)", GpuName = "(探测中)", MachineType = "未知";
        public static bool IsLaptop = false, HagsCapable = true, SystemDiskSsd = true, HasNvidia = false, RemoteInstalled = true;
        public static int OsBuild = 0;
        // 单声明访问器：GpuName 在上面的多变量声明里，自研静态检查器只登记首个变量名，
        // 跨类引用 Program.GpuName 会被误报 UNRESOLVED（2026-09-17，加门禁时踩到）。
        public static string GpuDisplayName { get { return GpuName; } }

        public static void DetectSystem()
        {
            // Environment.OSVersion 在无 manifest 的程序里返回兼容视图(Win8=9200)，真实 build 从注册表取
            try
            {
                object cb = ReadReg("HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "CurrentBuildNumber");
                if (cb != null) OsBuild = int.Parse(cb.ToString());
            }
            catch { }
            if (OsBuild == 0) { try { OsBuild = Environment.OSVersion.Version.Build; } catch { } }
            try
            {
                using (var mo = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (var o in mo.Get()) { CpuName = Convert.ToString(o["Name"]).Trim(); break; }
            }
            catch { CpuName = "(未知)"; }
            // 笔记本判定：机箱类型为便携类(8-14,18,21,30-32)即算
            try
            {
                var portable = new[] { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
                using (var mo = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                    foreach (var o in mo.Get())
                    {
                        var arr = o["ChassisTypes"] as ushort[];
                        if (arr != null && arr.Length > 0 && Array.IndexOf(portable, (int)arr[0]) >= 0) IsLaptop = true;
                        break;
                    }
                MachineType = IsLaptop ? "笔记本" : "桌面机";
            }
            catch { }
            // GPU：取第一块非虚拟显卡
            try
            {
                using (var mo = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (var o in mo.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (n.Contains("Virtual") || n.Contains("MuMu") || n.Contains("GameViewer")) continue;
                        GpuName = n; break;
                    }
                HasNvidia = GpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                // HAGS 官方支持矩阵：NVIDIA GTX 10系+/RTX、AMD RX 5000+、Intel Arc/Iris Xe，且系统 >= Win10 2004(19041)
                HagsCapable = OsBuild >= 19041;
                var mGtx = Regex.Match(GpuName, @"GTX\s*(\d+)");
                var mAmd = Regex.Match(GpuName, @"RX\s*(\d+)");
                if (GpuName.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0) { }
                else if (mGtx.Success && int.Parse(mGtx.Groups[1].Value) >= 1000) { }
                else if (mAmd.Success && int.Parse(mAmd.Groups[1].Value) >= 5000) { }
                else if (GpuName.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0 || GpuName.IndexOf("Iris Xe", StringComparison.OrdinalIgnoreCase) >= 0) { }
                else HagsCapable = false;
            }
            catch { }
            // 系统盘介质：root\Microsoft\Windows\Storage 的 MSFT_PhysicalDisk(4=SSD,3=HDD)，按 C: 所在磁盘匹配
            try
            {
                uint systemDisk = 0; bool foundPart = false;
                var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
                {
                    using (var ps = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='C:'")))
                        foreach (var o in ps.Get()) { systemDisk = Convert.ToUInt32(o["DiskNumber"]); foundPart = true; break; }
                    if (foundPart)
                        using (var ds = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, MediaType FROM MSFT_PhysicalDisk")))
                            foreach (var o in ds.Get())
                                if (Convert.ToUInt32(o["Number"]) == systemDisk)
                                { SystemDiskSsd = Convert.ToUInt32(o["MediaType"]) != 3; break; }   // 3=HDD
                }
            }
            catch { SystemDiskSsd = true; }   // 探测失败保守视为 SSD（保持原行为）
            // 配置的远控是否真的装了（没装则联动静默失效）
            try
            {
                bool any = false;
                foreach (var app in Cfg.RemoteApps)
                {
                    if (app.Exe.Length > 0 && File.Exists(app.Exe)) { any = true; break; }
                    foreach (var sn in app.Services)
                        try { using (new ServiceController(sn)) { any = true; break; } } catch { }
                    if (any) break;
                }
                RemoteInstalled = any;
            }
            catch { RemoteInstalled = true; }
            // 分享场景：配置的远控未安装时，按已知远控注册表探测（进程或服务任一命中即算），
            // 自动纳入游戏联动。每次启动重新探测，不改写用户的 config.json。
            // 名单来源：UU远程=本机实测(GameViewer 系列)；向日葵 SunloginClient/sunloginclient、
            // ToDesk ToDesk_Service/ToDesk_Desktop、TeamViewer TeamViewer/TeamViewer_Service、
            // RustDesk/AnyDesk 同名进程——均官网或权威资料核实
            if (!RemoteInstalled)
            {
                var knownRemotes = new object[][]
                {
                    new object[] { "UU远程", new[] { "GameViewer", "GameViewerServer", "GameViewerHealthd", "GameViewerService" }, new[] { "GameViewerService" }, "GameViewer" },
                    new object[] { "向日葵", new[] { "SunloginClient" }, new[] { "sunloginclient" }, "SunloginClient" },
                    new object[] { "ToDesk", new[] { "ToDesk_Service", "ToDesk_Desktop" }, new string[0], "" },
                    new object[] { "RustDesk", new[] { "rustdesk" }, new string[0], "rustdesk" },
                    new object[] { "TeamViewer", new[] { "TeamViewer", "TeamViewer_Service" }, new[] { "TeamViewer" }, "TeamViewer" },
                    new object[] { "AnyDesk", new[] { "AnyDesk" }, new string[0], "AnyDesk" },
                };
                foreach (var kr in knownRemotes)
                {
                    string disp = (string)kr[0];
                    string[] procNames = (string[])kr[1];
                    string[] svcNames = (string[])kr[2];
                    string mainProc = (string)kr[3];
                    string exePath = ""; bool found = false;
                    foreach (var pn in procNames)
                    {
                        try
                        {
                            var procs = Process.GetProcessesByName(pn);
                            if (procs.Length == 0) continue;
                            found = true;
                            if (mainProc.Length > 0 && pn.Equals(mainProc, StringComparison.OrdinalIgnoreCase))
                            {
                                try { exePath = procs[0].MainModule.FileName; } catch { }
                                // UU远程主程序本体在 bin\ 下，被外部拉起会单实例自退；
                                // 恢复必须用根目录启动器（官方快捷方式同款）
                                if (exePath.Length > 0 && pn.StartsWith("GameViewer"))
                                {
                                    string root = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
                                    if (root != null)
                                    {
                                        string launcher = Path.Combine(root, "GameViewer.exe");
                                        if (File.Exists(launcher)) exePath = launcher;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    foreach (var sn in svcNames)
                        try { using (new ServiceController(sn)) { found = true; } } catch { }
                    if (!found) continue;
                    var app = new RemoteApp();
                    app.Name = disp;
                    app.Processes = new List<string>(procNames);
                    app.Services = new List<string>(svcNames);
                    app.Exe = exePath;
                    Cfg.RemoteApps.Add(app);
                    RemoteInstalled = true;
                    Log("[自适应] 检测到远控 " + disp + "，已自动加入游戏联动（开游戏暂停/退出恢复）");
                    break;
                }
            }
            DetectTopology();
        }

        public static string SystemSummary()
        {
            return MachineType + " | " + CpuName + " | " + GpuName + " | 系统盘" + (SystemDiskSsd ? "SSD" : "HDD")
                + " | Win build " + OsBuild + " | HAGS" + (HagsCapable ? "支持" : "不支持") + (RemoteInstalled ? "" : " | 远控未安装");
        }

        // ---- CPU 拓扑：混合架构(12代+) P/E 核识别与游戏 P 核亲和性 ----
        // 用固定长度的 GetLogicalProcessorInformation 解析：P 核带超线程(mask 位数为 2)，E 核单 LP(1 位)。
        // 全部核心同位数视为非混合 CPU（如无 SMT 处理器），不启用绑定。
        public static bool HybridCpu = false;
        public static ulong PMask = 0, EMask = 0;
        // 物理核数（P 核按「每核 LP≥2」计、E 核 1 LP 1 核）。核验页用它判「小核休眠」策略是否适用：
        // 该策略只对 P 核多到用不完的平台有意义，社区给的阈值是 8 个 P 核 —— 6 P 核属于不该限制那一档。
        public static int PPhysical = 0, EPhysical = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SLPI
        {
            public UIntPtr ProcessorMask;
            public int Relationship;   // 0 = RelationProcessorCore
        }

        public static void DetectTopology()
        {
            try
            {
                uint len = 0;
                Native.GetLogicalProcessorInformation(IntPtr.Zero, ref len);
                if (len == 0) return;
                var buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!Native.GetLogicalProcessorInformation(buf, ref len)) return;
                    int expected = Environment.ProcessorCount;
                    // 原生结构体大小随位数/版本不同（x64 有 24/32/40 等可能），用自校验挑出正确步长：
                    // 每条记录 Relationship 必须是 0-3，且 ProcessorCore 掩码位总数 == 逻辑处理器数
                    foreach (int recordSize in new int[] { 24, 32, 40, 48 })
                    {
                        if (len % recordSize != 0) continue;
                        ulong p = 0, e = 0; int total = 0; bool valid = true; int pCores = 0, eCores = 0;
                        for (int off = 0; off + recordSize <= (int)len; off += recordSize)
                        {
                            var rec = (SLPI)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + off), typeof(SLPI));
                            if (rec.Relationship < 0 || rec.Relationship > 3) { valid = false; break; }
                            if (rec.Relationship != 0) continue;   // 只要 ProcessorCore 记录
                            ulong mask = rec.ProcessorMask.ToUInt64();
                            for (ulong m = mask; m != 0; m &= m - 1) total++;
                            if (mask != 0)
                            {
                                int bits = 0; for (ulong m = mask; m != 0; m &= m - 1) bits++;
                                if (bits >= 2) { p |= mask; pCores++; } else { e |= mask; eCores++; }
                            }
                        }
                        if (!valid || total != expected) continue;
                        // 只有同时存在两类核心才认定混合架构（否则是无 SMT 的同构 CPU，绑定无意义）
                        if (p != 0 && e != 0) { HybridCpu = true; PMask = p; EMask = e; PPhysical = pCores; EPhysical = eCores; }
                        break;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
        }

        // 游戏进程绑定 P 核（v2.0 默认关闭：Intel Thread Director 官方不建议硬绑，
        // 会破坏动态调度决策；改用 HeteroPolicyOptimize 的异类线程调度策略。
        // 仍保留此能力：config.json affinity.enable=true 可强制开启做对比测试）
        public static void ApplyGameAffinity()
        {
            if (!Cfg.AffinityEnable || !Cfg.AffinityPOnly || !HybridCpu || PMask == 0)
            {
                if (!Cfg.AffinityEnable && Cfg.HeteroPolicyEnable && HybridCpu)
                    Log("[调度] 未做 P 核硬绑：已改用异类线程调度策略（Thread Director 动态调度，官方推荐做法）");
                return;
            }
            int n = 0;
            foreach (var gp in AllGameProcesses())
            {
                try
                {
                    foreach (var pr in Process.GetProcessesByName(gp))
                    {
                        try { pr.ProcessorAffinity = (IntPtr)PMask; n++; } catch { }
                    }
                }
                catch { }
            }
            if (n > 0)
            {
                int pBits = 0; for (ulong m = PMask; m != 0; m &= m - 1) pBits++;
                int eBits = 0; for (ulong m = EMask; m != 0; m &= m - 1) eBits++;
                Log("[P核亲和] 已把 " + n + " 个游戏进程绑定到 " + pBits + " 个 P 核逻辑处理器（隔离 " + eBits + " 个 E 核）");
            }
        }

        public static string GetRemoteState()
        {
            if (!RemoteInstalled) return "未安装";
            foreach (var app in Cfg.RemoteApps)
            {
                foreach (var pn in app.Processes)
                    if (ProcExists(pn)) return "运行中";
                foreach (var sn in app.Services)
                    try { using (var sc = new ServiceController(sn)) if (sc.Status != ServiceControllerStatus.Stopped) return "运行中"; } catch { }
            }
            return "已暂停";
        }

        // 游戏联动与托盘手动开关共用的远控操作
        // 杀进程顺序必须倒序：UU远程带 Healthd 健康监护进程，先杀主程序会被它重新拉起
        // RemotePausedByLink：本次运行期间联动是否暂停过远控（决定退出时要不要恢复，防止误弹远控窗口）
        public static bool RemotePausedByLink = false;
        // 暂停远控前的显示模式快照 —— 远控被拉起时显卡会把链路抖一遍，恢复后要用它把分辨率装回去
        static List<DispGuard.Mode> dispBeforeRemote = new List<DispGuard.Mode>();
        public static void PauseAllRemote()
        {
            if (!RemoteInstalled) return;
            // 分辨率联动生效期间显示模式归 ResLink 管：这里若记快照，会把游戏分辨率（如 1568x1080）
            // 记成"要保护的模式"，退出时两边抢着改分辨率。快照跳过，ResumeAllRemote 侧同样跳过装回。
            if (ResLink.Active)
                Log("[显示守卫] 游戏分辨率联动生效中，本次不记快照（模式由分辨率联动负责）");
            else
            {
                dispBeforeRemote = DispGuard.Capture();
                Log("[显示守卫] 已记录 " + dispBeforeRemote.Count + " 台在用屏幕的模式，远控恢复后若被改动会自动装回");
            }
            RemotePausedByLink = true;
            foreach (var app in Cfg.RemoteApps)
            {
                foreach (var sn in app.Services) StopService(sn);
                for (int i = app.Processes.Count - 1; i >= 0; i--)
                    KillProcess(app.Processes[i]);
            }
        }
        public static void ResumeAllRemote()
        {
            if (!RemoteInstalled) return;
            RemotePausedByLink = false;
            foreach (var app in Cfg.RemoteApps)
            {
                foreach (var sn in app.Services) StartService(sn);
                if (app.Exe.Length > 0 && File.Exists(app.Exe))
                    try { Process.Start(app.Exe); } catch { }
            }
            // 远控主程序起来后显卡还要缓几秒，延时比对：只装回模式，不动拓扑（拔了的外接屏不找回）
            // 分辨率联动生效期间跳过：退出游戏时 LinkGameExit 会先把 ResLink 还原，再把这里放出来
            if (ResLink.Active)
                Log("[显示守卫] 游戏分辨率联动生效中，跳过装回（避免与还原 1920x1080 抢写）");
            else
                DispGuard.RestoreLater(dispBeforeRemote, 8000);
        }
        // 只在"确实被联动暂停过"时才恢复。
        //   ★ 退出游戏时不能无条件调 ResumeAllRemote：它会把远控服务和 UU远程 主程序都拉起来，
        //     于是"没暂停过远控"的档位（二游/3A）一退出游戏反倒弹出一个远控窗口（2026-09-15 实测）。
        public static void ResumeAllRemoteIfPausedAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { if (RemotePausedByLink) ResumeAllRemote(); } catch { }
            });
        }

        // 服务停止/进程击杀可耗时 20s+，联动 tick 里绝不能在 UI 线程跑（同 GameBoostStartAsync）
        public static void PauseAllRemoteAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { PauseAllRemote(); } catch { } });
        }
        public static void ResumeAllRemoteAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { ResumeAllRemote(); } catch { } });
        }

        // ============ 优化执行 ============
        public static void PowerOptimize()
        {
            Log("—— [1/9] 电源计划 ——");
            string guid = Cfg.PowerScheme == "ultimate" ? "e9a42b02-d5df-448d-aa00-03f14749eb61" : "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
            string name = Cfg.PowerScheme == "ultimate" ? "卓越性能" : "高性能";
            if (!RunCmd("powercfg", "/list").Contains(guid))
                RunCmd("powercfg", "/duplicatescheme " + guid);
            RunCmd("powercfg", "/setactive " + guid);
            Log("已激活电源计划: " + name);
            if (Cfg.MinProc > 0)
            {
                int minState = Cfg.MinProc;
                if (Cfg.AdaptiveEnable && IsLaptop && minState >= 100)
                { minState = 50; Log("自适应(笔记本)：最小处理器状态降为 50%（防积热耗电），最大仍 100%"); }
                RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN " + minState);
                RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX " + Cfg.MaxProc);
                Log("处理器最小/最大状态 " + minState + "%/" + Cfg.MaxProc + "%");
            }
            if (Cfg.UsbSuspendOff)
            {
                RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
                Log("已禁用 USB 选择性暂停");
            }
            if (Cfg.PcieAspmOff)
            {
                RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0");
                Log("已关闭 PCIe ASPM 节能");
            }
            RunCmd("powercfg", "/change standby-timeout-ac 0");
            RunCmd("powercfg", "/change hibernate-timeout-ac 0");
            RunCmd("powercfg", "/change disk-timeout-ac 0");
            RunCmd("powercfg", "/setactive " + guid);
            Log("已关闭睡眠/休眠/硬盘超时");
        }

        public static void GpuOptimize()
        {
            Log("—— [2/9] GPU/系统 ——");
            if (Cfg.GameDvrOff)
            {
                WriteReg("HKCU:\\System\\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                WriteReg("HKCU:\\System\\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
                // 只写 FSEBehaviorMode 时 Windows 可能忽略它 —— 微软另有一个"尊重用户设置"开关，
                //  不置 1 系统会按自己的启发式决定是否应用（2026-09-16 联网核实）。
                WriteReg("HKCU:\\System\\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, RegistryValueKind.DWord);
                WriteReg("HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                WriteReg("HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                Log("已关闭 GameDVR 游戏录制");
            }
            if (Cfg.GpuGameMode)
            {
                // Windows 游戏模式：前台游戏获得调度/资源优先（官方功能，保持开启）
                string gbPath = "HKCU:\\Software\\Microsoft\\GameBar";
                BackupReg(gbPath, "AutoGameModeEnabled");
                WriteReg(gbPath, "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                Log("已确保 Windows 游戏模式开启 (AutoGameModeEnabled=1)");
            }
            if (Cfg.NvProfiles)
            {
                // NVIDIA 驱动 per-game 配置（等价控制面板手动改，但全自动）：电源管理=最高性能优先 + 低延迟=超高
                if (HasNvidia || File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll")))
                {
                    try { Log("[NVAPI] " + NvDrs.ApplyGameProfiles(FpsList(), Cfg.MmoGames, Cfg.AaaGames, Cfg.GachaGames)); }
                    catch (Exception ex) { Log("[NVAPI] 应用失败: " + ex.Message); }
                }
                else Log("[NVAPI] 未检测到 NVIDIA 显卡（" + GpuName + "），跳过驱动配置");
            }
        }

        public static void CleanOptimize()
        {
            Log("—— [5/9] 后台清理 ——");
            if (Cfg.KillList.Count > 0)
            {
                int killed = 0;
                foreach (var raw in Cfg.KillList)
                {
                    string pn = raw.Replace(".exe", "");
                    var procs = Process.GetProcessesByName(pn);
                    if (procs.Length > 0)
                    {
                        foreach (var p in procs) { try { p.Kill(); killed++; } catch { } }
                        Log("已清理: " + pn);
                    }
                }
                if (killed == 0) Log("无白名单进程需要清理");
            }
            if (Cfg.BoostPriority)
            {
                bool found = false;
                foreach (var gp in AllGameProcesses())
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(gp))
                        { try { p.PriorityClass = ProcessPriorityClass.High; found = true; } catch { } }
                    }
                    catch { }
                }
                Log(found ? "游戏进程已提升为 High 优先级" : "未检测到运行中的游戏进程");
            }
            ApplyGameAffinity();   // 若游戏在跑且为混合 CPU，同时绑定 P 核
        }

        public static void NetworkOptimize()
        {
            Log("—— [6/9] 网络参数 ——");
            string mmPath = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
            // ⚠️ 0xFFFFFFFF 是 uint 字面量，而 RegistryKey.SetValue 的 DWord 只收 int ——
            //    传 uint 会在 SetValue 里抛 ArgumentException 且被 catch 吞掉，
            //    表现为"优化完检查仍显示未设置"（实测踩过）。写 -1（int）落盘就是 0xFFFFFFFF。
            WriteReg(mmPath, "NetworkThrottlingIndex", Cfg.NetThrottle, RegistryValueKind.DWord);
            Log(Cfg.NetThrottle == -1 ? "已禁用网络流量节流" : ("网络流量节流 = " + Cfg.NetThrottle));
            WriteReg(mmPath, "SystemResponsiveness", Cfg.SysResp, RegistryValueKind.DWord);
            Log("SystemResponsiveness=" + Cfg.SysResp);
            if (Cfg.NagleOff)
            {
                int n = 0;
                try
                {
                    using (var ifaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                    {
                        if (ifaces != null)
                            foreach (var sub in ifaces.GetSubKeyNames())
                            {
                                string p = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
                                WriteReg(p, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                                WriteReg(p, "TCPNoDelay", 1, RegistryValueKind.DWord);
                                n++;
                            }
                    }
                }
                catch { }
                Log("已对 " + n + " 个网卡禁用 Nagle 算法");
            }
            NicOptimize();
        }

        public static void DragFixOptimize()
        {
            Log("—— [9/9] 拖拽流畅 ——");
            if (Cfg.DragFullWindows == false)
            {
                WriteReg("HKCU:\\Control Panel\\Desktop", "DragFullWindows", "0", RegistryValueKind.String);
                try { Native.SystemParametersInfo(0x25, 0, IntPtr.Zero, 1); } catch { }
                Log("拖拽切换为仅边框模式（即时生效）");
            }
            if (Cfg.NoTransparency)
            {
                WriteReg("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord);
                Log("已关闭窗口透明效果");
            }
            if (Cfg.NoAnim)
            {
                WriteReg("HKCU:\\Control Panel\\Desktop\\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);
                Log("已关闭窗口动画");
            }
        }

        // ============ 输入 / 内存 / Defender（规格书 v2 增量） ============
        public static void InputOptimize()
        {
            if (!Cfg.InputEnable) return;
            if (Cfg.MouseAccelOff)
            {
                // 关闭"提高指针精确度"（系统鼠标加速）：原始输入游戏不受影响，读系统鼠标的游戏更跟手
                string p = "HKCU:\\Control Panel\\Mouse";
                BackupReg(p, "MouseSpeed"); BackupReg(p, "MouseThreshold1"); BackupReg(p, "MouseThreshold2");
                WriteReg(p, "MouseSpeed", "0", RegistryValueKind.String);
                WriteReg(p, "MouseThreshold1", "0", RegistryValueKind.String);
                WriteReg(p, "MouseThreshold2", "0", RegistryValueKind.String);
                try
                {
                    int[] vals = new int[] { 0, 0, 0 };   // SPI_SETMOUSE：加速度关/阈值 0/0，即时生效并广播
                    var h = System.Runtime.InteropServices.GCHandle.Alloc(vals, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try { Native.SystemParametersInfo(0x0004, 0, h.AddrOfPinnedObject(), 0x03); }
                    finally { h.Free(); }
                }
                catch { }
                Log("已关闭系统鼠标加速（提高指针精确度）");
            }
        }

        public static void MemoryOptimize()
        {
            if (!Cfg.MemEnable) return;
            if (Cfg.NoPagingExec)
            {
                string p = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management";
                BackupReg(p, "DisablePagingExecutive");
                WriteReg(p, "DisablePagingExecutive", 1, RegistryValueKind.DWord);
                Log("已禁止内核/驱动换页出内存（DisablePagingExecutive=1，重启生效）");
            }
        }

        // Defender 排除走 WMI（root\Microsoft\Windows\Defender 的 MSFT_MpPreference.Add/Remove，
        // 参数名与 cmdlet 相同）——不依赖 PowerShell，精简系统/被安全软件拦 PS 的机器也能用
        public static void DefenderExclusion(string method, string processName)
        {
            try
            {
                var scope = new ManagementScope(@"root\Microsoft\Windows\Defender");
                using (var mc = new ManagementClass(scope, new ManagementPath("MSFT_MpPreference"), null))
                using (var inP = mc.GetMethodParameters(method))
                {
                    inP["ExclusionProcess"] = new string[] { processName };
                    using (mc.InvokeMethod(method, inP, null)) { }
                }
            }
            catch (Exception ex) { Log("Defender " + method + "(" + processName + ") 失败: " + ex.Message); }
        }

        public static void DefenderOptimize()
        {
            if (!Cfg.DefenderExclude) { Log("Defender 游戏进程排除：默认关闭（安全权衡项，config.json defender.enable 开启）"); return; }
            // 进程级排除（只排除游戏 exe 本体，不开放整个目录），备份以便 -Restore 移除
            var names = new List<string>();
            foreach (var gp in Cfg.GameProcesses)
                names.Add(gp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? gp : gp + ".exe");
            int ok = 0;
            foreach (var n in names)
            {
                DefenderExclusion("Add", n);
                ok++;
            }
            var entry = new Dictionary<string, object>();
            entry["Type"] = "defender_excl"; entry["Processes"] = string.Join(",", names.ToArray());
            AddBackupEntry(entry);
            Log("已添加 " + ok + " 个游戏进程到 Defender 实时扫描排除（进程级，-Restore 可移除）");
        }

        // ============ 新增优化（调度 / HAGS / 多媒体任务 / 网卡节能 / 服务 / 定时器） ============
        public static void SchedulerOptimize()
        {
            if (!Cfg.SchedEnable) { Log("—— [3/9] CPU 调度（已在配置中禁用，跳过）——"); return; }
            Log("—— [3/9] CPU 调度 ——");
            string p = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl";
            BackupReg(p, "Win32PrioritySeparation");
            WriteReg(p, "Win32PrioritySeparation", Cfg.SchedSep, RegistryValueKind.DWord);
            Log("Win32PrioritySeparation=" + Cfg.SchedSep + "（0x" + Cfg.SchedSep.ToString("X") + " 短量子+可变+前台增强，重启生效）");

            // 进程电源节流（Power Throttling，Win10 1709+ 默认开启）：系统会把"判定为后台"的进程降频。
            //  游戏是多进程架构（启动器 / 反作弊 / 音频线程），子进程容易被判成后台而降频。
            //  这一项是 HKLM 级（对所有进程生效），故随"CPU 调度"一起做。
            string pp = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power";
            BackupReg(pp, "PowerThrottlingOff");
            WriteReg(pp, "PowerThrottlingOff", 1, RegistryValueKind.DWord);
            Log("已关闭进程电源节流（PowerThrottlingOff=1，重启生效）");
        }

        // 异类线程调度策略（混合架构的正确做法，替代 v1.x 的 P 核硬绑）
        // 依据：Intel Thread Director 白皮书明确不建议在混合架构上手动钉核——
        //       硬绑会破坏 Thread Director 的调度决策，可能把后台线程留在 P 核、游戏线程挤到 E 核。
        //       正确做法是交给硬件+系统的动态调度，只调整"偏好策略"。
        // ⚠️ GUID 以本机实测为准（powercfg /q SUB_PROCESSOR 实测，12 代酷睿台式机 / Win11 25H2）：
        //    bae08b81-2d5e-4688-ad6a-13243356654b = 异类短运行线程调度策略（别名 SHORTSCHEDPOLICY）——
        //    游戏线程正是短运行线程，这是真正起作用的那个键。
        //    可选值：0 所有处理器 / 1 高性能处理器(P核) / 2 首选高性能处理器 / 3 高效处理器 / 4 首选高效处理器 / 5 自动
        //    93b8b6dc-...（"异类线程调度策略"总开关）在本机不存在（powercfg 报"设置不存在"），
        //    旧版把它当"短运行线程"去 apply+check，结果 apply 静默失败、check 永远返回"?"（实测踩过）。
        public static void HeteroPolicyOptimize()
        {
            if (!Cfg.HeteroPolicyEnable) { Log("—— 异类线程调度策略（配置中禁用，跳过）——"); return; }
            if (!HybridCpu) { Log("—— 异类线程调度策略（非混合架构 CPU，跳过）——"); return; }
            Log("—— 异类线程调度策略（混合架构 P/E 核调度） ——");
            try
            {
                string sub = "54533251-82be-4824-96c1-47b60b740d00";
                string shortTh = "bae08b81-2d5e-4688-ad6a-13243356654b";   // 异类短运行线程调度策略（本机实测存在）
                UnlockPowerSetting(sub, shortTh);
                BackupReg(@"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" + sub + "\\" + shortTh, "Attributes");
                RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT " + sub + " " + shortTh + " 1");
                RunCmd("powercfg", "/setdcvalueindex SCHEME_CURRENT " + sub + " " + shortTh + " 1");
                RunCmd("powercfg", "/setactive SCHEME_CURRENT");
                // 当场回读：powercfg /setxxx 失败不报错（旧版 93b8b6dc 就是设置不存在却静默失败），
                // 不回读就只能在检查页看到"未应用"，用户会以为点了优化没用
                string now = GetPowerCfgSetting(sub, shortTh);
                Log(now == "1"
                    ? "异类短运行线程调度策略 = 高性能处理器(P核)，回读确认已生效"
                    : "⚠️ 已下发，但回读 AC=" + now + "（未确认为 1）—— 该设置可能不支持当前电源方案");
            }
            catch (Exception ex) { Log("异类线程策略设置失败: " + ex.Message); }
        }

        // 解锁电源方案里的隐藏选项（Attributes=2 表示"显示在电源选项中"）
        static void UnlockPowerSetting(string sub, string setting)
        {
            try
            {
                string p = @"HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" + sub + "\\" + setting;
                WriteReg(p, "Attributes", 2, RegistryValueKind.DWord);
            }
            catch { }
        }

        // 内核隔离 / VBS 状态检测（只读展示，不修改——调研结论：收益不确定且明显降低安全性）
        public static string VbsState()
        {
            try
            {
                object devGuard = ReadReg(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
                object hvci = ReadReg(@"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
                int dg = devGuard == null ? 0 : Convert.ToInt32(devGuard);
                int hi = hvci == null ? 0 : Convert.ToInt32(hvci);
                if (dg == 0 && hi == 0) return "未启用";
                string s = "";
                if (dg != 0) s += "VBS 已启用";
                if (hi != 0) s += (s.Length > 0 ? " / " : "") + "内存完整性已启用";
                return s + "（如需关闭请自行在「核心隔离」设置操作，本工具不代改）";
            }
            catch { return "读取失败"; }
        }

        public static void HagsMmOptimize()
        {
            Log("—— [4/9] HAGS / 多媒体任务 ——");
            if (Cfg.HagsOn)
            {
                if (Cfg.AdaptiveEnable && !HagsCapable)
                { Log("自适应(显卡/系统)：跳过 HAGS——" + GpuName + (OsBuild < 19041 ? " 或系统低于 Win10 2004" : "") + " 不在官方支持矩阵内（NVIDIA GTX10系+/AMD RX5000+/Intel Arc + Win10 2004+）"); }
                else
                {
                    string p = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
                    BackupReg(p, "HwSchMode");
                    WriteReg(p, "HwSchMode", 2, RegistryValueKind.DWord);
                    Log("已开启硬件加速 GPU 计划 HwSchMode=2（重启生效）");
                }
            }
            if (Cfg.MmGamesEnable)
            {
                string p = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games";
                BackupReg(p, "GPU Priority"); BackupReg(p, "Priority"); BackupReg(p, "Scheduling Category"); BackupReg(p, "SFIO Priority");
                WriteReg(p, "GPU Priority", 8, RegistryValueKind.DWord);
                WriteReg(p, "Priority", 6, RegistryValueKind.DWord);
                WriteReg(p, "Scheduling Category", "High", RegistryValueKind.String);
                WriteReg(p, "SFIO Priority", "High", RegistryValueKind.String);
                Log("多媒体 Games 任务已提升（GPU Priority=8 / Priority=6 / High）");
            }
        }

        static void NicWriteValue(string keyPath, string name)
        {
            BackupReg(keyPath, name);
            WriteReg(keyPath, name, 0, RegistryValueKind.DWord);
        }
        public static void NicOptimize()
        {
            if (!Cfg.NicEnable) return;
            int n = 0;
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                        using (var k = cls.OpenSubKey(sub, false))
                        {
                            // 只处理真实网卡键（有 DriverDesc 描述），跳过子键 0000 下的 Continued 等
                            if (k == null || k.GetValue("DriverDesc") == null) continue;
                        }
                        string p = @"HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\" + sub;
                        if (Cfg.NicGreenOff)
                        {
                            // 节能以太网系列：驱动不支持的项写入后会被忽略，无副作用
                            NicWriteValue(p, "*EEE"); NicWriteValue(p, "EEE"); NicWriteValue(p, "AdvancedEEE");
                            NicWriteValue(p, "EnableGreenEthernet"); NicWriteValue(p, "GigaLite"); NicWriteValue(p, "AutoDisableGigabit");
                        }
                        if (Cfg.NicPowerOff)
                        {
                            // 24 = 同时取消"允许计算机关闭此设备"两个勾，防止系统挂起网卡造成网络毛刺
                            BackupReg(p, "PnPCapabilities");
                            WriteReg(p, "PnPCapabilities", 24, RegistryValueKind.DWord);
                        }
                        n++;
                    }
                }
            }
            catch { }
            Log("已对 " + n + " 个网卡关闭节能特性（EEE/GreenEthernet/系统挂起，重插或重启网卡后生效）");
        }

        // ============ 系统虚拟化开关（Hyper-V 层）============
        // 依据：社区视频《自己的配置明明不错，但还是帧数上不去》第 1 步——原视频走的是
        //   「启用或关闭 Windows 功能」里取消勾选 Hyper-V / Windows 虚拟机监控程序平台 / 虚拟机平台。
        //   作者自述收益是「降低帧数波动、减少帧生成时间抖动」——注意它治的是**帧时间尾部**，
        //   不治平均帧上限（视频里 GPU 只用 48% 却卡在 510 帧，就是药方与症状错配的现场）。
        //
        // 实现上只改 BCD 的 hypervisorlaunchtype，不去动 Windows 功能：
        //   · 生效面等价——hypervisor 不随开机加载，那三层虚拟化特性就都跑不起来，
        //     这正是性能收益的来源；
        //   · 可逆性最好——恢复只是一条 bcdedit，WSL2 的发行版、Docker 的镜像毫发无损，
        //     不用重装；而 DISM 禁功能再启用要重启两轮；
        //   · 不跑 DISM——那条路在「功能本就没启用」的机器上要空跑几十秒并报无意义错误。
        //   · 代价（必须让用户知道）：WSL2 / Docker Desktop / Android 模拟器 / Windows 沙盒
        //     全部不可用。所以配置项默认关闭，且写入前一定先备份原值。
        public static bool HypervisorRunning()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object v = mo["HypervisorPresent"];
                        return v != null && Convert.ToBoolean(v);
                    }
            }
            catch { }
            return false;
        }

        // BCD 里 hypervisorlaunchtype 的当前值；null = 未显式设置（等价出厂态 Auto）
        public static string HypervisorLaunchType()
        {
            string txt = RunCmd("bcdedit", "/enum {current}");
            var m = Regex.Match(txt, @"hypervisorlaunchtype\s+(\w+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string VirtualizationOptimize()
        {
            if (!Cfg.VirtDisableEnable) { Log("—— 系统虚拟化（配置中禁用，跳过）——"); return "已停用，跳过"; }
            Log("—— 系统虚拟化（关闭 Hyper-V 层）——");
            try
            {
                string cur = HypervisorLaunchType();
                var entry = new Dictionary<string, object>();
                entry["Type"] = "bcd"; entry["Path"] = "current";
                entry["Name"] = "hypervisorlaunchtype";
                if (cur != null) entry["Value"] = cur;      // 有值才记，恢复时按"有没有 Value"判断
                AddBackupEntry(entry);

                RunCmd("bcdedit", "/set hypervisorlaunchtype off");
                // 回读：bcdedit 失败不报错（同 powercfg 的坑），不回读就会"看起来做了其实没做"
                string now = HypervisorLaunchType();
                if (string.Equals(now, "off", StringComparison.OrdinalIgnoreCase))
                {
                    Log("已关闭 hypervisor 加载（hypervisorlaunchtype=off，重启生效）");
                    Log("· 重启后 WSL2 / Docker Desktop / Android 模拟器 / Windows 沙盒将不可用");
                    if (HypervisorRunning()) Log("· 当前 hypervisor 仍在运行，重启后才会真正卸下");
                    return "已关闭（重启生效）";
                }
                Log("⚠️ 已下发 bcdedit，但回读为 " + (now == null ? "未设置" : now) + " —— 未确认生效（需要管理员权限）");
                return "下发未确认";
            }
            catch (Exception ex) { Log("虚拟化关闭失败: " + ex.Message); return "失败: " + ex.Message; }
        }

        // 恢复出厂态：BCD 里不留 hypervisorlaunchtype（等价 Auto，hypervisor 按需加载）。
        // 「恢复备份」走 DoRestore 的 bcd 分支按备份原值精确还原，这里只服务「恢复系统默认」。
        // 只在备份里确实有本工具改过的记录时才动 —— 否则会抹掉用户自己设的虚拟化偏好。
        public static void VirtualizationToFactory()
        {
            bool changed = false;
            try
            {
                string dir = Path.Combine(DataDir, "backup");
                if (Directory.Exists(dir))
                {
                    string latest = null;
                    foreach (var f in Directory.GetFiles(dir, "backup_*.json"))
                        if (latest == null || File.GetLastWriteTime(f) > File.GetLastWriteTime(latest)) latest = f;
                    if (latest != null)
                    {
                        var list = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(latest, Encoding.UTF8));
                        if (list != null)
                            foreach (var e in list)
                                if (e.ContainsKey("Type") && e["Type"].ToString() == "bcd"
                                    && e.ContainsKey("Name") && e["Name"].ToString() == "hypervisorlaunchtype")
                                    changed = true;
                    }
                }
            }
            catch { }
            if (!changed) return;
            RunCmd("bcdedit", "/deletevalue hypervisorlaunchtype");
            Log("虚拟化：hypervisorlaunchtype 已删除（恢复出厂态 Auto，重启后 WSL2 / Docker / 模拟器可用）");
        }

        // CS2 的视频设置（显示模式）。买枪菜单是 2D UI 层：打开时整幅画面要从「纯 3D」
        //   变成「3D + UI 合成」，无边框窗口下这一步要多过一层 DWM —— 是帧时间尖峰的常见来源。
        // ⚠️ SteamPath 注册表值带正斜杠（c:/program files (x86)/steam）且是小写，
        //    必须先归一化，否则 Path.Combine 会混出半正半反的路径（2026-09-19 实测踩过）。
        public static string Cs2DisplayMode()
        {
            try
            {
                string steam = Convert.ToString(ReadReg(@"HKCU:\Software\Valve\Steam", "SteamPath"));
                if (string.IsNullOrEmpty(steam)) return null;
                steam = steam.Replace('/', '\\');
                string ud = Path.Combine(steam, "userdata");
                if (!Directory.Exists(ud)) return null;
                foreach (var acc in Directory.GetDirectories(ud))
                {
                    string f = Path.Combine(acc, "730", "local", "cfg", "cs2_video.txt");
                    if (!File.Exists(f)) continue;
                    string full = null, border = null;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var m = Regex.Match(line, @"""setting\.(fullscreen|nowindowborder)""\s+""(\d)""");
                        if (!m.Success) continue;
                        if (m.Groups[1].Value == "fullscreen") full = m.Groups[2].Value;
                        else border = m.Groups[2].Value;
                    }
                    if (full == "1") return "独占全屏";
                    if (full == "0") return (border == "1") ? "无边框窗口" : "窗口化";
                }
            }
            catch { }
            return null;
        }

        public static void ServiceOptimize()
        {
            if (!Cfg.SvcEnable || Cfg.DisableServices.Count == 0) { Log("—— [7/9] 服务优化（无项目，跳过）——"); return; }
            Log("—— [7/9] 服务优化 ——");
            foreach (var sn in Cfg.DisableServices)
            {
                if (sn.Equals("SysMain", StringComparison.OrdinalIgnoreCase) && Cfg.AdaptiveEnable && !SystemDiskSsd)
                { Log("自适应(机械硬盘)：保留 SysMain(超级预取)——HDD 上它对加载性能有益，不禁用"); continue; }
                // 备份启动类型（Services\<名> 的 Start 值），-Restore 按注册表还原
                BackupReg("HKLM:\\SYSTEM\\CurrentControlSet\\Services\\" + sn, "Start");
                StopService(sn);
                RunCmd("sc", "config " + sn + " start= disabled");
                Log("已禁用服务: " + sn);
            }
        }

        // ---- 游戏期间定时器分辨率联动（ISLC/TimerResolution 同款 NtSetTimerResolution 方案） ----
        static bool timerApplied = false;
        static int timerWant100ns = 5000;
        public static void ApplyGameTimer()
        {
            if (timerApplied || !Cfg.TimerEnable) return;
            timerWant100ns = Math.Max(1000, (int)Math.Round(Cfg.TimerMs * 10000));
            int cur;
            if (Native.NtSetTimerResolution(timerWant100ns, true, out cur) == 0)
            {
                timerApplied = true;
                Log("[联动] 游戏运行中：定时器分辨率 " + (cur / 10000.0).ToString("0.###") + "ms → " + Cfg.TimerMs + "ms（退出游戏自动还原）");
            }
        }
        public static void RevertGameTimer()
        {
            if (!timerApplied) return;
            int cur;
            Native.NtSetTimerResolution(timerWant100ns, false, out cur);
            timerApplied = false;
            Log("游戏退出：定时器分辨率已还原系统默认");
        }
        public static bool IsTimerApplied() { return timerApplied; }

        // Win10 2004+ 定时器分辨率改为按进程生效，外部工具的请求可能被忽略；
        // GlobalTimerResolutionRequests=1 恢复全局行为（Win11 24H2 社区验证方案，重启生效）
        public static void TimerGlobalFix()
        {
            if (!Cfg.TimerEnable) return;
            string p = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel";
            BackupReg(p, "GlobalTimerResolutionRequests");
            WriteReg(p, "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
            Log("已启用全局定时器请求 GlobalTimerResolutionRequests=1（否则 0.5ms 只对 GameBoost 自身生效，游戏不受益；重启生效）");
        }

        // ---- 游戏加速包（Razer Cortex 挂起进程 / 游戏加加内存整理 / 社区验证的游戏期停 Windows 更新） ----
        static List<Process> suspendedProcs = new List<Process>();
        static List<string> wuWasRunning = new List<string>();
        static readonly string[] wuServices = { "wuauserv", "bits", "dosvc" };   // dosvc=传递优化(P2P 分发)，社区验证的 ping 尖峰元凶之一

        public static void GameBoostStart()
        {
            if (!Cfg.GbEnable) return;
            int suspended = 0;
            foreach (var name in Cfg.SuspendList)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        // 挂起而非杀：游戏退出可完整恢复（Cortex 同款做法），失败(权限/已退出)静默跳过
                        try
                        {
                            if (Native.NtSuspendProcess(p.Handle) == 0) { suspendedProcs.Add(p); suspended++; }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            long freedMB = 0; int trimmed = 0;
            if (Cfg.GbTrim)
            {
                // 内存整理：只修剪后台大进程(≥200MB)的工作集，游戏进程与自身不动
                var gameNames = new List<string>(Cfg.GameProcesses);
                gameNames.Add("GameBoost");
                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            if (gameNames.Contains(p.ProcessName)) continue;
                            if (p.WorkingSet64 < 200L * 1024 * 1024) continue;
                            long before = p.WorkingSet64;
                            if (Native.EmptyWorkingSet(p.Handle)) { freedMB += before / (1024 * 1024); trimmed++; }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            wuWasRunning.Clear();
            if (Cfg.GbPauseWU)
            {
                foreach (var sn in wuServices)
                {
                    try
                    {
                        using (var sc = new ServiceController(sn))
                            if (sc.Status == ServiceControllerStatus.Running) { wuWasRunning.Add(sn); StopService(sn); }
                    }
                    catch { }
                }
            }
            Log("[游戏加速包] 挂起进程 " + suspended + " 个 / 内存整理 " + trimmed + " 个(约 " + freedMB + " MB) / 已暂停更新服务 " + wuWasRunning.Count + " 个（游戏退出自动恢复）");
        }

        public static void GameBoostStop()
        {
            if (!Cfg.GbEnable) return;
            int resumed = 0;
            foreach (var p in suspendedProcs)
            {
                try { if (Native.NtResumeProcess(p.Handle) == 0) resumed++; }
                catch { }
                try { p.Dispose(); } catch { }
            }
            suspendedProcs.Clear();
            int wuRestarted = wuWasRunning.Count;
            foreach (var sn in wuWasRunning) StartService(sn);
            wuWasRunning.Clear();
            Log("[游戏加速包] 已恢复挂起进程 " + resumed + " 个 / 重启更新服务 " + wuRestarted + " 个");
        }

        // 启动时兜底：把上次异常退出可能残留的挂起进程唤醒（对正常运行进程无副作用）
        public static void GameBoostCleanupOrphans()
        {
            foreach (var name in Cfg.SuspendList)
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                        try { Native.NtResumeProcess(p.Handle); } catch { }
                }
                catch { }
        }

        // 服务停止/内存整理最长可耗时 20s+，绝不能在 UI 线程跑（联动的 tick 里只投后台并立即返回）
        static object gbLock = new object();
        public static void GameBoostStartAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { lock (gbLock) GameBoostStart(); });
        }
        public static void GameBoostStopAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { lock (gbLock) GameBoostStop(); });
        }

        public static void ReloadCfg(Config c) { Cfg = c; }

        // ============ config 定位/写回（按节-键做文本手术，保留 JSON 注释与其他字段） ============

        // 花括号深度（跳过字符串字面量；\" 转义按两字符处理）
        static int BraceDepthAt(string text, int index)
        {
            int depth = 0; bool inStr = false;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                char ch = text[i];
                if (inStr)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') inStr = false;
                }
                else if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            return depth;
        }

        static int MatchBrace(string text, int brace)
        {
            int depth = 0; bool inStr = false;
            for (int i = brace; i < text.Length; i++)
            {
                char ch = text[i];
                if (inStr)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') inStr = false;
                }
                else if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // 定位顶层节块（返回 '{' 与其配对 '}' 的下标）。
        // ❗不能再用 IndexOf("\"services\"") —— gameAware.remoteApps 里就有 "services": ["GameViewerService"]，
        //   于是「服务优化」的开关会被写到它后面第一个 { 命中的「CPU 调度」节上（2026-09-13 发现的静默错改）。
        //   判据：该键所在位置的花括号深度必须是 1（顶层），且键与 { 之间只有冒号/空白。
        static bool FindSectionBlock(string text, string section, out int brace, out int end)
        {
            brace = -1; end = -1;
            string needle = "\"" + section + "\"";
            int from = 0;
            while (true)
            {
                int si = text.IndexOf(needle, from, StringComparison.Ordinal);
                if (si < 0) return false;
                from = si + needle.Length;
                if (BraceDepthAt(text, si) != 1) continue;
                int b = text.IndexOf('{', si);
                if (b < 0) return false;
                bool clean = true;
                for (int i = si + needle.Length; i < b; i++)
                {
                    char ch = text[i];
                    if (ch != ':' && !char.IsWhiteSpace(ch)) { clean = false; break; }
                }
                if (!clean) continue;
                int e = MatchBrace(text, b);
                if (e < 0) return false;
                brace = b; end = e;
                return true;
            }
        }

        // 把键补进某个节块（block 形如 "{\r\n    \"a\": true\r\n  "，含首花括号、不含尾花括号）
        static string InsertKeyBlock(string block, string key, string rawValue)
        {
            try
            {
                if (block.Length < 1 || block[0] != '{') return null;
                string inner = block.Substring(1).TrimEnd();
                string sep = inner.Length == 0 ? "" : (inner.EndsWith(",") ? "" : ",");
                string nb = "{" + inner + sep + "\r\n    \"" + key + "\": " + rawValue + "\r\n  ";
                // 注意：返回值不含结尾的 '}'（调用方用 text.Substring(end) 接回去），
                // 所以校验时必须自己补上 —— 忘了补就是"块本身非法、永远验不过、补键静默失效"。
                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(nb + "}");
                return nb;
            }
            catch { return null; }
        }

        // 整个节不存在时（老 config.json 里没有新加的节）追加一个新节
        static string AppendSectionBlock(string text, string section, string key, string rawValue)
        {
            try
            {
                int b = text.IndexOf('{');
                if (b < 0) return null;
                int e = MatchBrace(text, b);
                if (e < 0) return null;
                string inner = text.Substring(b + 1, e - b - 1).TrimEnd();
                string sep = inner.Length == 0 ? "" : (inner.EndsWith(",") ? "" : ",");
                string nb = text.Substring(0, b + 1) + inner + sep
                          + "\r\n\r\n  \"" + section + "\": {\r\n    \"" + key + "\": " + rawValue + "\r\n  }\r\n"
                          + text.Substring(e);
                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(nb);   // 写坏宁可拒绝
                return nb;
            }
            catch { return null; }
        }

        // 统一写回：定位节 → 正则替换键值；键/节缺失就补写。写前整份文件校验一遍
        // （2026-09 曾因配置文件被写入非法占位符导致整体解析失败、全部配置静默回默认 —— 这类事故必须挡在写入前）
        static bool WriteConfigKey(string section, string key, string pat, string rawValue)
        {
            try
            {
                string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                int brace, end;
                if (!FindSectionBlock(text, section, out brace, out end))
                {
                    string full = AppendSectionBlock(text, section, key, rawValue);
                    if (full == null) return false;
                    File.WriteAllText(ConfigPath, full, Encoding.UTF8);
                    return true;
                }
                string block = text.Substring(brace, end - brace);
                // ⚠ 判据用 Regex.IsMatch（"键在不在"），**不能**用"替换前后文本有没有变化"。
                //   旧写法 `nb == block` 有个静默陷阱：当写入值与现有值**完全相同**时，替换成功但结果
                //   与原文一字不差 → 被误判成"键不在本节" → InsertKeyBlock 又插一个同名键。
                //   后果是配置文件里同一个键出现两次（JavaScriptSerializer 不报错、静默取其一），
                //   每保存一次就多一条，越积越脏。任何"值没变也照写"的路径（连续保存、换主题重建后的回填）
                //   都会踩到 —— 2026-09-20 加分辨率档位时发现。
                string nb;
                if (!System.Text.RegularExpressions.Regex.IsMatch(block, pat))
                {
                    // 键不在本节 → 补写一个。老安装的 config.json 不会有新增的键，而安装包对该文件用的是
                    // onlyifdoesntexist（升级不覆盖用户那份），所以"新键写不进去"是必然会发生的事。
                    nb = InsertKeyBlock(block, key, rawValue);
                    if (nb == null) return false;
                }
                else
                {
                    // 必须用 ${1} 而不是 $1：替换串 "$1"+rawValue 在 rawValue 是数字时会拼成 "$10"/"$11"/"$12"，
                    // .NET 会把它当成"第 10/11/12 号捕获组"，而本模式只有 1 个组 —— 无效组号会原样写出字面量 $10。
                    // 结果就是整数键被写成 "$10,"，整份 JSON 解析失败、全部配置静默回默认（2026-09 两次事故的根因）。
                    nb = System.Text.RegularExpressions.Regex.Replace(block, pat, "${1}" + rawValue.Replace("$", "$$"));
                }
                if (nb == block) return true;      // 值本来就是它：不必重写文件（幂等，且避免无谓的整份重写）
                string outText = text.Substring(0, brace) + nb + text.Substring(end);
                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(outText);
                File.WriteAllText(ConfigPath, outText, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log("config 写回失败 [" + section + "." + key + "]: " + ex.Message);
                // ★ 只读目录兜底：主 config.json 被 ACL 锁成只读（本机的 %ProgramData% 就是）
                //   → 落一份 config.local.json 当覆盖层，之后所有写入都走它；下次启动优先读它。
                //   不加这一层，"主题/开关改完重启就丢"永远无解。
                bool onOverlay = configPathCache != null &&
                                 configPathCache.EndsWith("config.local.json", StringComparison.OrdinalIgnoreCase);
                if (!onOverlay)
                {
                    try
                    {
                        string alt = Path.Combine(DataDir, "config.local.json");
                        string cur = File.ReadAllText(ConfigPath, Encoding.UTF8);
                        File.WriteAllText(alt, Config.RepairJson(cur), new UTF8Encoding(false));   // 顺手修掉历史坏写
                        configPathCache = alt;
                        Log("已改用可写的覆盖配置：" + alt + "（设置从现在起不会丢）");
                        return WriteConfigKey(section, key, pat, rawValue);                  // 重试一次
                    }
                    catch (Exception ex2) { Log("覆盖配置也写不了：" + ex2.Message + "（建议在「设置」里换数据目录）"); }
                }
                return false;
            }
        }

        public static bool SetConfigBool(string section, string key, bool value)
        {
            return WriteConfigKey(section, key, "(\"" + key + "\"\\s*:\\s*)(true|false)", value ? "true" : "false");
        }

        public static bool SetConfigStr(string section, string key, string value)
        {
            return WriteConfigKey(section, key, "(\"" + key + "\"\\s*:\\s*)\"[^\"]*\"",
                "\"" + value.Replace("\\", "\\\\") + "\"");
        }

        public static bool SetConfigInt(string section, string key, int value)
        {
            return WriteConfigKey(section, key, "(\"" + key + "\"\\s*:\\s*)(-?\\d+)", value.ToString());
        }

        // 字符串数组（如 dlssg.extraRoots）：生成 JSON 数组字面量写入
        public static bool SetConfigList(string section, string key, System.Collections.Generic.List<string> values)
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"").Append(values[i].Replace("\\", "\\\\")).Append("\"");
            }
            sb.Append("]");
            return WriteConfigKey(section, key, "(\"" + key + "\"\\s*:\\s*)\\[[^\\]]*\\]", sb.ToString());
        }

        // ---- 写回游戏分辨率规则（v3.8.0）----
        // 为什么单独写一个：rules 是**对象数组**，而 SetConfigList 只支持字符串数组；
        //   WriteConfigKey 又只会替换"节里某个键"的值 —— 所以这里把整个数组重新序列化后整段替换。
        //   （数组里没有嵌套的 [ ]，所以非贪婪匹配到第一个 ] 就是数组结尾。）
        // 写入前照样过 WriteConfigKey 的整份 JSON 校验：写坏宁可拒绝。
        // rules 数组的 JSON 字面量。**单独抽出来**：探针可以拿它做"写出去 → 读回来"的往返验证，
        //   而不必真的去写配置文件（探针绝不能碰用户的 config.json）。
        public static string ResRulesJson()
        {
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            if (Cfg != null)
            {
                foreach (ResLinkRule r in Cfg.ResRules)
                {
                    if (r == null || r.Proc == null || r.Proc.Length == 0) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append("{ \"proc\": \"").Append(r.Proc.Replace("\\", "\\\\")).Append("\", ");
                    sb.Append("\"w\": ").Append(r.W).Append(", ");
                    sb.Append("\"h\": ").Append(r.H).Append(", ");
                    sb.Append("\"hz\": ").Append(r.Hz).Append(", ");
                    sb.Append("\"detach\": ").Append(r.Detach ? "true" : "false");
                    if (r.Preset != null && r.Preset.Length > 0)
                        sb.Append(", \"preset\": \"").Append(r.Preset.Replace("\\", "\\\\")).Append("\"");
                    // 进游戏时要在设备管理器层面禁用的显示器型号（v3.9.0）。空 = 不写这一项，
                    //   老配置读回来是空 → 行为与升级前完全一致。
                    if (r.OffMons != null && r.OffMons.Trim().Length > 0)
                        sb.Append(", \"offMons\": \"").Append(r.OffMons.Trim().Replace("\\", "\\\\")).Append("\"");
                    sb.Append(" }");
                }
            }
            sb.Append("]");
            return sb.ToString();
        }

        public static bool SaveResRules()
        {
            try
            {
                if (Cfg == null) return false;
                bool okRules = WriteConfigKey("displayLink", "rules", "(\"rules\"\\s*:\\s*)\\[[^\\]]*\\]", ResRulesJson());
                // 顺带把整节补齐：老安装的配置里根本没有 displayLink 段（这功能上线后没被改过），
                //   只写 rules 会让用户看到一份"缺一半"的配置，下次也不好手改。
                SetConfigBool("displayLink", "enable", Cfg.ResLinkEnable);
                SetConfigInt("displayLink", "baseW", Cfg.BaseW);
                SetConfigInt("displayLink", "baseH", Cfg.BaseH);
                SetConfigInt("displayLink", "baseHz", Cfg.BaseHz);
                return okRules;
            }
            catch (Exception ex) { Log("分辨率规则写回失败: " + ex.Message); return false; }
        }

        // 把"退出游戏后的还原目标"设成主屏当前模式。
        // 用途：用户换了显示器 / 换了日常分辨率之后，写死的 1920x1080@165 已经不对了 ——
        //   点一下即可对齐，不用去手改 config.json。
        // ⚠ 联动生效中（主屏正停在游戏分辨率）必须拒绝：否则会把游戏分辨率设成"还原目标"，
        //   退出游戏时等于什么都没还原。
        public static string SetBaseToCurrent()
        {
            if (ResLink.Active)
                return "游戏联动正生效（主屏现在是游戏分辨率），先退出游戏再设";
            DispGuard.Mode? m = DispGuard.Current(ResLink.PrimaryDevice());
            if (!m.HasValue || m.Value.W <= 0) return "读不到当前主屏模式，未改动";
            string before = Cfg.BaseW + "x" + Cfg.BaseH + "@" + Cfg.BaseHz;
            Cfg.BaseW = m.Value.W; Cfg.BaseH = m.Value.H; Cfg.BaseHz = m.Value.Hz;
            SetConfigInt("displayLink", "baseW", Cfg.BaseW);
            SetConfigInt("displayLink", "baseH", Cfg.BaseH);
            SetConfigInt("displayLink", "baseHz", Cfg.BaseHz);
            string after = Cfg.BaseW + "x" + Cfg.BaseH + "@" + Cfg.BaseHz;
            return before == after ? "还原目标本来就是 " + after : "还原目标：" + before + " → " + after;
        }

        // 当日日志文件路径（UI 跟随日志用）
        public static string LogToday() { return LogFile; }

        // ============ 一键优化编排（v2.0：从 UI 层下沉到此处，避免序列分散在两处） ============
        // 顺序：备份 → 电源 → 调度 → GPU → 定时器 → 网络/网卡 → 服务 → 输入 → 内存 → 安全 → 桌面
        // ============ 取值细调：旋钮表（v3.6.0） ============
        // 设计约束（别在后续维护里破坏它）：
        //   ① 只放「改坏了能改回来」的项。HPET 强制、GPU 中断绑核、MSI 强制转换、rBAR 全局强开
        //      这类多来源明确警告"改错会让设备从系统消失/无法启动"的，一律只做只读核验，不进这张表。
        //   ② 每一项都必须写清 Hint（这个值到底影响什么、代价是什么）。只给选项不给代价，
        //      等于把"用户以为自己懂了"变成新的坑。
        //   ③ 驱动档位的取值与名称逐个核对过 NVIDIA Profile Inspector 的 CustomSettingNames.xml，
        //      不是猜的（着色器缓存实际是 12 档，不是网上常说的 10 档）。
        public static List<OptKnob> Knobs()
        {
            var c = Cfg;
            var l = new List<OptKnob>();

            // ---------- DLSS 模型覆盖 ----------
            l.Add(new OptKnob
            {
                Key = "dlss.override", Sec = "dlss", Name = "override", IsBool = true, Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "模型覆盖总开关",
                Hint = "开启后给各游戏档写入「DLL 覆盖=开 + 预设档 + 强制预设」。RTX 30 系能吃到 DLSS 4 的 Transformer 超分（只有多帧生成锁 50 系）。与 0.3.x 帧生成代理都可能在 DLL 层接管，先确认不打架再开。",
                Labels = new string[] { "关闭（默认）", "开启" }, Values = new int[] { 0, 1 },
                Value = c.DlssOverride ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "nv.dlssPresetLetter", Sec = "nv", Name = "dlssForcedPreset", Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "强制预设字母",
                Hint = "Transformer Gen1 = J/K，Gen2 = L/M。L 只在 Ultra Performance 档起效、M 只在 Performance 档起效，而且 Gen2 在 30 系有约 20% 性能税 —— 所以「使用推荐值」通常最好，别盲目追新。",
                Labels = new string[] { "使用推荐值（驱动决定，推荐）", "Preset K（Transformer Gen1，30 系稳妥）",
                                        "Preset L（Gen2，Ultra Performance 档生效）", "Preset M（Gen2，Performance 档生效，30 系约 -20% 帧）",
                                        "Preset J（Gen1 初版）", "不干预（N/A）" },
                Values = new int[] { 0x00FFFFFF, 0x0B, 0x0C, 0x0D, 0x0A, 0x00 },
                Value = c.NvDlssPresetLetter
            });
            l.Add(new OptKnob
            {
                Key = "nv.dlssPresetProfile", Sec = "nv", Name = "dlssPresetProfile", Apply = "drv",
                Group = "NVIDIA 驱动 · DLSS 模型覆盖",
                Title = "预设档（Forced Model Preset Profile）",
                Hint = "全名是 DLSS - Forced Model Preset Profile。如果上面的「强制预设字母」不生效，驱动要求把这一项一起改（NVIDIA Profile Inspector 的说明原话）。默认「推荐」。",
                Labels = new string[] { "推荐（Recommended）", "自定义（Custom）", "不设置（N/A）" },
                Values = new int[] { 1, 2, 0 }, Value = c.NvDlssPresetProfile
            });

            // ---------- 着色器缓存 ----------
            l.Add(new OptKnob
            {
                Key = "nv.shaderCacheOn", Sec = "nv", Name = "shaderCacheOn", Apply = "drv",
                Group = "NVIDIA 驱动 · 着色器缓存（治「第一次遇到就卡」）",
                Title = "缓存开关",
                Hint = "关掉后驱动编译好的 shader 变体不落盘：每次遇到新材质组合都要现场编译，而且不会随游玩次数减少（不缓存就永远学不会）。",
                Labels = new string[] { "开启（推荐）", "关闭" }, Values = new int[] { 1, 0 },
                Value = c.NvShaderCacheOn
            });
            l.Add(new OptKnob
            {
                Key = "nv.shaderCacheSize", Sec = "nv", Name = "shaderCacheSize", Apply = "drv",
                Group = "NVIDIA 驱动 · 着色器缓存（治「第一次遇到就卡」）",
                Title = "缓存大小上限",
                Hint = "共 12 档。驱动按 LRU 淘汰，不是「设多大就占多大」 —— 本机 %LOCALAPPDATA%\\NVIDIA\\DXCache 目前约 1.25 GB。担心占盘位就选 4~16 GB，别选最后那档「关闭」（那不是省盘，等于禁用缓存）。",
                Labels = new string[] { "无限制（默认）", "100 GB", "16 GB", "10 GB", "8 GB", "5 GB", "4 GB",
                                        "1 GB", "512 MB", "256 MB", "128 MB", "关闭（=禁用缓存）" },
                Values = new int[] { -1, 0x19000, 0x4000, 0x2800, 0x2000, 0x1400, 0x1000,
                                     0x400, 0x200, 0x100, 0x80, 0x00 },
                Value = c.NvShaderCacheSize
            });

            // ---------- 竞技(FPS)档 ----------
            l.Add(new OptKnob
            {
                Key = "nv.compPrerenderedFrames", Sec = "nv", Name = "compPrerenderedFrames", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "最大预渲染帧数",
                Hint = "1 就是 NVIDIA 面板里的「低延迟·超高」。CPU 瓶颈时越低越跟手；GPU 瓶颈时设 1 会掉帧，那种情况选「跟随游戏设置」。",
                Labels = new string[] { "1（最低延迟，默认）", "2", "3", "4", "跟随游戏设置" },
                Values = new int[] { 1, 2, 3, 4, 0 }, Value = c.NvCompPreRender
            });
            l.Add(new OptKnob
            {
                Key = "nv.compPowerMode", Sec = "nv", Name = "compPowerMode", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "电源管理模式",
                Hint = "「最高性能优先」让 GPU 常驻高频：更跟手、更费电、温度更高。在意温度或笔记本上可以选「最佳功率」。",
                Labels = new string[] { "最高性能优先（默认）", "最佳功率", "自适应" },
                Values = new int[] { 1, 5, 0 }, Value = c.NvCompPowerMode
            });
            l.Add(new OptKnob
            {
                Key = "nv.compVsync", Sec = "nv", Name = "compVsync", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "垂直同步",
                Hint = "强制关闭 = 最低延迟但会撕裂。开了 G-Sync/FreeSync 的话，NVIDIA 官方建议：驱动这层保持关闭、游戏内 VSync 打开（配合 Reflex 由驱动自动限帧）。",
                Labels = new string[] { "强制关闭（默认）", "跟随游戏内设置", "强制开启", "快速同步（Fast Sync）" },
                Values = new int[] { 0x08416747, 0x60925292, 0x47814940, 0x18888888 }, Value = c.NvCompVsync
            });
            l.Add(new OptKnob
            {
                Key = "nv.compTextureQuality", Sec = "nv", Name = "compTextureQuality", Apply = "drv",
                Group = "NVIDIA 驱动 · 竞技(FPS)档专用",
                Title = "纹理过滤质量",
                Hint = "降低过滤质量能省一点 GPU，代价是远处贴图发糊。竞技射击常用「高性能」。",
                Labels = new string[] { "高性能（默认）", "性能", "质量", "高质量" },
                Values = new int[] { 0x14, 0x0A, 0x00, -10 }, Value = c.NvCompTexQuality
            });

            // ---------- 3A / MMO / 二游档 ----------
            l.Add(new OptKnob
            {
                Key = "nv.aaaVsync", Sec = "nv", Name = "aaaVsync", Apply = "drv",
                Group = "NVIDIA 驱动 · 3A / MMO / 二游档",
                Title = "垂直同步",
                Hint = "画质档默认「跟随游戏内设置」—— 在没有 VRR 的屏幕上强制关闭必然撕裂，这类游戏帧率低，撕裂比延迟更碍眼。",
                Labels = new string[] { "跟随游戏内设置（默认）", "强制关闭", "强制开启", "快速同步（Fast Sync）" },
                Values = new int[] { 0x60925292, 0x08416747, 0x47814940, 0x18888888 }, Value = c.NvAaaVsync
            });

            // ---------- 驱动全局 ----------
            l.Add(new OptKnob
            {
                Key = "nv.backgroundFpsLimit", Sec = "nv", Name = "backgroundFpsLimit", Apply = "drv",
                Group = "NVIDIA 驱动 · 全局",
                Title = "后台应用帧率上限",
                Hint = "游戏切到后台时限制它的帧率，省电省热。注意这是写进各游戏档（per-profile）的，不是全局设置。",
                Labels = new string[] { "不限制（默认）", "60 FPS", "30 FPS", "20 FPS", "15 FPS", "10 FPS", "5 FPS" },
                Values = new int[] { 0, 60, 30, 20, 15, 10, 5 }, Value = c.NvBgFpsLimit
            });

            // ---------- Windows · GPU ----------
            l.Add(new OptKnob
            {
                Key = "hags.enable", Sec = "hags", Name = "enable", IsBool = true, Apply = "hags", Restart = true,
                Group = "Windows · GPU 与调度",
                Title = "硬件加速 GPU 计划（HAGS）",
                Hint = "开启可降低帧时间波动、让 GPU 自己管显存（NVIDIA 与多数指南推荐开）。少数高帧率竞技场景有「抖动反而变大」的反例报告 —— 那属于值得自己 A/B 一次的项。切换必须重启，不能游戏中途改。",
                Labels = new string[] { "开启（推荐，重启生效）", "关闭（重启生效）" }, Values = new int[] { 1, 0 },
                Value = c.HagsOn ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "scheduler.win32PrioritySeparation", Sec = "scheduler", Name = "win32PrioritySeparation",
                Apply = "sched", Restart = true,
                Group = "Windows · GPU 与调度",
                Title = "处理器计划（Win32PrioritySeparation）",
                Hint = "三个 2 位字段拼成：量子长短 / 可变或固定 / 前台加成倍数。38 = 前台量子 18、后台 6（Windows「程序」项的默认值，游戏推荐）；24 = 前后台都 36（Windows「后台服务」项）。数值与语义对照自微软官方文章《Master Your Quantum》。",
                Labels = new string[] { "38（0x26）短量子·可变·前台 3 倍 ← 默认/推荐",
                                        "36（0x24）短量子·可变·无前台加成",
                                        "40（0x28）短量子·固定·无加成",
                                        "22（0x16）长量子·可变·前台 3 倍",
                                        "20（0x14）长量子·可变·前台 2 倍",
                                        "24（0x18）长量子·固定·前后台同等",
                                        "2（0x02）系统默认（实测等价于 38）" },
                Values = new int[] { 38, 36, 40, 22, 20, 24, 2 }, Value = c.SchedSep
            });
            l.Add(new OptKnob
            {
                Key = "power.minProcessorState", Sec = "power", Name = "minProcessorState", Apply = "proc",
                Group = "Windows · GPU 与调度",
                Title = "处理器最小状态",
                Hint = "100% 让核心不降频（最跟手、最费电）；笔记本或夏天可降到 50%。注意工具的「硬件自适应」会自动把笔记本的 100% 压到 50%，那是防积热，不是没生效。",
                Labels = new string[] { "100%（默认，不降频）", "90%", "80%", "70%", "60%", "50%", "30%", "5%", "0%（允许深度降频）" },
                Values = new int[] { 100, 90, 80, 70, 60, 50, 30, 5, 0 }, Value = c.MinProc
            });
            l.Add(new OptKnob
            {
                Key = "power.maxProcessorState", Sec = "power", Name = "maxProcessorState", Apply = "proc",
                Group = "Windows · GPU 与调度",
                Title = "处理器最大状态",
                Hint = "降到 95% 以下会关掉睿频，几乎一定掉帧 —— 除非在做散热/功耗上限实验，否则保持 100%。",
                Labels = new string[] { "100%（默认）", "99%", "95%", "90%", "80%", "70%", "50%" },
                Values = new int[] { 100, 99, 95, 90, 80, 70, 50 }, Value = c.MaxProc
            });

            // ---------- Windows · 网络 ----------
            l.Add(new OptKnob
            {
                Key = "network.systemResponsiveness", Sec = "network", Name = "systemResponsiveness", Apply = "net",
                Group = "Windows · 网络",
                Title = "前台响应优先级（SystemResponsiveness）",
                Hint = "MMCSS 留给非多媒体任务的 CPU 百分比：越小越偏袒前台游戏。Windows 默认 20，游戏向常用 10，0 最激进（极端情况下可能卡音频）。",
                Labels = new string[] { "10（推荐）", "0（最激进）", "5", "15", "20（Windows 默认）", "30", "50（更保守）" },
                Values = new int[] { 10, 0, 5, 15, 20, 30, 50 }, Value = c.SysResp
            });
            l.Add(new OptKnob
            {
                Key = "network.nagleOff", Sec = "network", Name = "nagleOff", IsBool = true, Apply = "net",
                Group = "Windows · 网络",
                Title = "Nagle 算法（小包合并延迟）",
                Hint = "关闭 = 上传/动作包立即发，降低网络延迟感（TcpAckFrequency + TCPNoDelay = 1）。改成「保持默认」不会删除已经写进网卡的值，要用「恢复备份」才能还原。",
                Labels = new string[] { "关闭 Nagle（低延迟，推荐）", "保持系统默认" }, Values = new int[] { 1, 0 },
                Value = c.NagleOff ? 1 : 0
            });
            l.Add(new OptKnob
            {
                Key = "network.throttlingIndex", Sec = "network", Name = "throttlingIndex", Apply = "net",
                Group = "Windows · 网络",
                Title = "网络流量节流（NetworkThrottlingIndex）",
                Hint = "Windows 默认每处理 10 个数据包就打断一次多媒体流。禁用（-1 = 0xFFFFFFFF）让游戏流量不被节流；想恢复系统默认就选 10。",
                Labels = new string[] { "禁用节流（-1，推荐）", "10（Windows 默认）", "20", "30", "50" },
                Values = new int[] { -1, 10, 20, 30, 50 }, Value = c.NetThrottle
            });

            return l;
        }

        // 体检表行名 → 旋钮键。用显式映射而不是"逐行手写 Key"：条目顺序以后会变，
        // 手写的映射迟早漏掉一行，而漏掉的表现是"双击没反应"（不报错，很难发现）。
        static string KnobKeyOfItem(string item)
        {
            if (item == null) return null;
            switch (item)
            {
                case "驱动·着色器缓存开关": return "nv.shaderCacheOn";
                case "驱动·着色器缓存上限": return "nv.shaderCacheSize";
                case "驱动·DLSS 模型覆盖": return "dlss.override";
                case "驱动·DLSS 强制预设": return "nv.dlssPresetLetter";
                case "驱动·DLSS 预设档": return "nv.dlssPresetProfile";
                // 竞技档四项各自成行（v3.6.0 补全）：以前合成一行、只挂"预渲染帧数"，
                // 另外三项虽在同一个对话框里，体检表里却看不见也点不到。
                case "驱动·竞技档·预渲染帧数": return "nv.compPrerenderedFrames";
                case "驱动·竞技档·电源管理": return "nv.compPowerMode";
                case "驱动·竞技档·垂直同步": return "nv.compVsync";
                case "驱动·竞技档·纹理过滤质量": return "nv.compTextureQuality";
                case "驱动·3A/MMO/二游档取值": return "nv.aaaVsync";
                case "驱动·帧率上限(后台)": return "nv.backgroundFpsLimit";
                case "硬件加速GPU计划(HAGS)": return "hags.enable";
                // 这一行是「HAGS × 竞技游戏(A/B 建议)」—— 只在 30/20 系 + 有竞技档游戏时出现。
                // 也挂 hags.enable：用户在这里看到"可以 A/B"之后，最自然的下一步就是当场切一下，
                // 不该再让他回去找上面那行（2026-09-19 用户："这一项是什么，为什么改不了"）。
                case "HAGS × 竞技游戏(A/B 建议)": return "hags.enable";
                case "CPU调度(Win32优先级分离)": return "scheduler.win32PrioritySeparation";
                case "处理器最小/最大状态": return "power.minProcessorState";
                case "处理器最大状态(睿频上限)": return "power.maxProcessorState";
                case "前台响应优先级": return "network.systemResponsiveness";
                case "Nagle 算法(小包延迟)": return "network.nagleOff";
                case "网络流量节流": return "network.throttlingIndex";
            }
            return null;
        }

        static void AttachKnobKeys(List<StatusItem> list)
        {
            var map = new Dictionary<string, OptKnob>();
            foreach (var k in Knobs()) map[k.Key] = k;
            foreach (var it in list)
            {
                string kk = KnobKeyOfItem(it.Item);
                if (kk == null) continue;
                OptKnob k;
                if (!map.TryGetValue(kk, out k)) continue;
                it.Key = kk;
                if (k.Restart && it.Expected != null && it.Expected.IndexOf("重启") < 0)
                    it.Expected += "（改后需重启）";
            }
        }

        // 某个旋钮当前档位的显示文案（体检表用）
        public static string KnobText(string key)
        {
            foreach (var k in Knobs()) if (k.Key == key) return k.CurrentText;
            return "—";
        }

        // ==================== 体检表状态分级（v3.6.1） ====================
        // 四档：已生效 / 未生效 / 待确认(🟡) / 需处理。
        // 为什么补第四档：界面以前只有两支 —— 状态开头不是 ✅/⚠️ 的**一律**算"需处理"并涂红。
        // 于是「🟡 已跳过（DPC 计数器不可用）」「🟡 可实测（HAGS 的 A/B 建议）」这些
        // **本来就不是缺陷**的行，也在屏幕上顶着红点报错（2026-09-19 用户截图问"这一项是什么"）。
        // 🟡 的语义就是"不是缺陷"：可选 / 不适用 / 读不到 —— 需要你看一眼，但不必处理。
        // ⚠ 四个常量**分开写**，不要合并成 `public const int A = 0, B = 1, ...`：
        //   tools\static_check.py 的成员扫描按"一行一个名字"解析，逗号声明只认第一个，
        //   后面三个会被判成 UNRESOLVED（在校验器里是假红，但会掩盖真红，不值得为省三行冒这个险）。
        public const int BadgeOk = 0;
        public const int BadgeWarn = 1;
        public const int BadgeInfo = 2;
        public const int BadgeBad = 3;

        public static int BadgeKind(string status)
        {
            if (status == null) return BadgeBad;
            string s = status.Trim();
            if (s.StartsWith("✅")) return BadgeOk;
            if (s.StartsWith("⚠️")) return BadgeWarn;
            if (s.StartsWith("🟡")) return BadgeInfo;
            return BadgeBad;
        }

        // 胶囊里显示的字：✅/⚠️ 用固定词（一行里就要说清"生效了 / 没生效"），
        // 🟡 保留原文（"可实测 / 已跳过 / 可关闭"各不相同，换成固定词反而把信息丢了）。
        // ⚠ 返回的文字必须 ≤ 3 个汉字：状态列宽 86px（v3.6.1 由 70 加宽而来），
        //   胶囊左右各留 ~13px 内边距，放不下第 4 个字。
        //   （探针 virt_probe 第 13 节有断言守着，把文案加长会当场报红）。
        public static string BadgeText(string status)
        {
            int k = BadgeKind(status);
            if (k == BadgeOk) return "已生效";
            if (k == BadgeWarn) return "未生效";
            if (k == BadgeInfo)
            {
                // 🟡 是代理对（U+1F7E1），长度按 "🟡".Length 取，不要写死 1/2
                string s = status.Trim();
                return s.Substring("🟡".Length).Trim();
            }
            return "需处理";
        }

        // 写 config.json → 重载配置。之后所有读取（含一键优化）都以新值为准。
        public static bool SetKnob(OptKnob k, int rawValue)
        {
            bool ok = k.IsBool ? SetConfigBool(k.Sec, k.Name, rawValue != 0)
                               : SetConfigInt(k.Sec, k.Name, rawValue);
            if (!ok) return false;
            try { ReloadCfg(Config.Load(ConfigPath)); } catch { }
            return true;
        }

        // 把配置值落到系统。按 Apply 分派，同一类只跑一次（否则改 4 个驱动项就要写 4 遍 nvdrsdb）。
        public static string ApplyKnob(OptKnob k)
        {
            try
            {
                switch (k.Apply)
                {
                    case "drv":
                        return NvDrs.ApplyGameProfiles(FpsList(), Cfg.MmoGames, Cfg.AaaGames, Cfg.GachaGames);
                    case "hags":
                        {
                            string hp = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
                            BackupReg(hp, "HwSchMode");
                            WriteReg(hp, "HwSchMode", Cfg.HagsOn ? 2 : 1, RegistryValueKind.DWord);
                            return "HwSchMode=" + (Cfg.HagsOn ? 2 : 1) + "（重启生效）";
                        }
                    case "sched":
                        {
                            string sp = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl";
                            BackupReg(sp, "Win32PrioritySeparation");
                            WriteReg(sp, "Win32PrioritySeparation", Cfg.SchedSep, RegistryValueKind.DWord);
                            return "Win32PrioritySeparation=" + Cfg.SchedSep;
                        }
                    case "proc":
                        RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN " + Cfg.MinProc);
                        RunCmd("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX " + Cfg.MaxProc);
                        RunCmd("powercfg", "/setactive SCHEME_CURRENT");
                        return "处理器状态 " + Cfg.MinProc + "% / " + Cfg.MaxProc + "%";
                    case "net":
                        {
                            string mm = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
                            BackupReg(mm, "NetworkThrottlingIndex");
                            BackupReg(mm, "SystemResponsiveness");
                            // 注意写 int：0xFFFFFFFF 是 uint 字面量，SetValue 的 DWord 只收 int，
                            // 传 uint 会抛 ArgumentException 并被吞掉（表现为"优化完仍显示未设置"，本文件踩过）
                            WriteReg(mm, "NetworkThrottlingIndex", Cfg.NetThrottle, RegistryValueKind.DWord);
                            WriteReg(mm, "SystemResponsiveness", Cfg.SysResp, RegistryValueKind.DWord);
                            int n = 0;
                            if (Cfg.NagleOff)
                            {
                                try
                                {
                                    using (var ifaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                                    {
                                        if (ifaces != null)
                                            foreach (var sub in ifaces.GetSubKeyNames())
                                            {
                                                string q = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
                                                WriteReg(q, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                                                WriteReg(q, "TCPNoDelay", 1, RegistryValueKind.DWord);
                                                n++;
                                            }
                                    }
                                }
                                catch { }
                            }
                            return "节流=" + Cfg.NetThrottle + " / 前台响应=" + Cfg.SysResp
                                 + " / Nagle=" + (Cfg.NagleOff ? ("已关闭(" + n + " 个网卡)") : "未改动");
                        }
                }
            }
            catch (Exception ex) { return "应用失败：" + ex.Message; }
            return "";
        }

        public static void ApplyAll(Action<string> log)
        {
            Action<string> L = log != null ? log : new Action<string>(delegate(string s) { Log(s); });

            // 备份（「恢复备份」依赖这些条目，缺一不可）
            BackupPowerScheme();
            BackupReg(@"HKCU:\Control Panel\Desktop", "DragFullWindows");
            BackupReg(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
            BackupReg(@"HKCU:\Control Panel\Desktop\WindowMetrics", "MinAnimate");
            BackupReg(@"HKCU:\System\GameConfigStore", "GameDVR_Enabled");
            BackupReg(@"HKCU:\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode");
            BackupReg(@"HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "PowerThrottlingOff");
            BackupReg(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");
            BackupReg(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness");

            try { PowerOptimize(); } catch (Exception ex) { L("电源优化异常: " + ex.Message); }
            try { SchedulerOptimize(); } catch (Exception ex) { L("调度优化异常: " + ex.Message); }
            try { HeteroPolicyOptimize(); } catch (Exception ex) { L("异类线程策略异常: " + ex.Message); }
            try { GpuOptimize(); } catch (Exception ex) { L("GPU 优化异常: " + ex.Message); }
            try { HagsMmOptimize(); } catch (Exception ex) { L("HAGS/多媒体异常: " + ex.Message); }
            try { TimerGlobalFix(); } catch (Exception ex) { L("定时器异常: " + ex.Message); }
            try { NetworkOptimize(); } catch (Exception ex) { L("网络优化异常: " + ex.Message); }
            try { NicOptimize(); } catch (Exception ex) { L("网卡优化异常: " + ex.Message); }
            try { VirtualizationOptimize(); } catch (Exception ex) { L("虚拟化异常: " + ex.Message); }
            try { ServiceOptimize(); } catch (Exception ex) { L("服务优化异常: " + ex.Message); }
            try { InputOptimize(); } catch (Exception ex) { L("输入优化异常: " + ex.Message); }
            try { MemoryOptimize(); } catch (Exception ex) { L("内存优化异常: " + ex.Message); }
            try { DefenderOptimize(); } catch (Exception ex) { L("Defender 排除异常: " + ex.Message); }
            try { DragFixOptimize(); } catch (Exception ex) { L("桌面流畅异常: " + ex.Message); }
            try { string dh = MarkDriverBaseline(); if (dh.Length > 0) L("· " + dh); } catch { }
            L("全部优化执行完毕（明细见「性能优化」页）");
        }

        // ============ 恢复系统默认（出厂值，区别于"恢复备份"） ============
        public static void RestoreSystemDefaults()
        {
            Log("—— 开始恢复系统默认设置（出厂值）——");
            // 电源：官方命令重置全部电源计划为出厂默认（含删除自定义计划）
            RunCmd("powercfg", "-restoredefaultschemes");
            Log("电源计划已全部重置为出厂默认");
            // 注册表：各项写回 Windows 出厂值
            WriteReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation", 2, RegistryValueKind.DWord);
            WriteReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", "HwSchMode", 1, RegistryValueKind.DWord);
            try { VirtualizationToFactory(); } catch (Exception ex) { Log("虚拟化还原异常: " + ex.Message); }
            WriteReg("HKCU:\\System\\GameConfigStore", "GameDVR_Enabled", 1, RegistryValueKind.DWord);
            DeleteRegValue("HKCU:\\System\\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode");
            DeleteRegValue("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", "PowerThrottlingOff");
            WriteReg("HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", 1, RegistryValueKind.DWord);
            WriteReg("HKCU:\\Software\\Microsoft\\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
            string mmPath = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
            WriteReg(mmPath, "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
            WriteReg(mmPath, "SystemResponsiveness", 20, RegistryValueKind.DWord);
            DeleteRegValue("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel", "GlobalTimerResolutionRequests");
            DeleteRegValue("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "DisablePagingExecutive");
            WriteReg("HKLM:\\SYSTEM\\CurrentControlSet\\Services\\SysMain", "Start", 2, RegistryValueKind.DWord);
            WriteReg("HKCU:\\Control Panel\\Mouse", "MouseSpeed", "1", RegistryValueKind.String);
            WriteReg("HKCU:\\Control Panel\\Mouse", "MouseThreshold1", "6", RegistryValueKind.String);
            WriteReg("HKCU:\\Control Panel\\Mouse", "MouseThreshold2", "10", RegistryValueKind.String);
            try
            {
                int[] vals = new int[] { 1, 6, 10 };   // 默认：提高指针精确度开
                var h = System.Runtime.InteropServices.GCHandle.Alloc(vals, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { Native.SystemParametersInfo(0x0004, 0, h.AddrOfPinnedObject(), 0x03); }
                finally { h.Free(); }
            }
            catch { }
            WriteReg("HKCU:\\Control Panel\\Desktop", "DragFullWindows", "1", RegistryValueKind.String);
            WriteReg("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "EnableTransparency", 1, RegistryValueKind.DWord);
            WriteReg("HKCU:\\Control Panel\\Desktop\\WindowMetrics", "MinAnimate", "1", RegistryValueKind.String);
            try { Native.SystemParametersInfo(0x25, 1, IntPtr.Zero, 1); } catch { }   // 拖拽恢复全窗口渲染
            // 策略项与多媒体 Games 任务、网卡节能、Nagle：删除我们写入的值（不存在=默认）
            try
            {
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\Policies\\Microsoft\\Windows\\GameDVR", true);
                if (key != null) { key.DeleteValue("AllowGameDVR", false); key.Close(); }
            }
            catch { }
            try
            {
                string gp = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games";
                var k2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(gp, true);
                if (k2 != null)
                {
                    foreach (var v in new[] { "GPU Priority", "Priority", "Scheduling Category", "SFIO Priority" }) k2.DeleteValue(v, false);
                    k2.Close();
                }
            }
            catch { }
            try
            {
                using (var ifaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                    if (ifaces != null)
                        foreach (var sub in ifaces.GetSubKeyNames())
                        {
                            string pp = @"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
                            DeleteRegValue(pp, "TcpAckFrequency");
                            DeleteRegValue(pp, "TCPNoDelay");
                        }
            }
            catch { }
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var sub in cls.GetSubKeyNames())
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(sub, @"^\d{4}$")) continue;
                            string pp = @"HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\" + sub;
                            foreach (var v in new[] { "*EEE", "EEE", "AdvancedEEE", "EnableGreenEthernet", "GigaLite", "AutoDisableGigabit", "PnPCapabilities" })
                                DeleteRegValue(pp, v);
                        }
            }
            catch { }
            // Defender 排除移除（按备份记录）
            try
            {
                string dir = Path.Combine(DataDir, "backup");
                if (Directory.Exists(dir))
                {
                    string latest = null;
                    foreach (var f in Directory.GetFiles(dir, "backup_*.json"))
                        if (latest == null || File.GetLastWriteTime(f) > File.GetLastWriteTime(latest)) latest = f;
                    if (latest != null)
                    {
                        var list = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(latest, Encoding.UTF8));
                        if (list != null)
                            foreach (var e in list)
                                if (e.ContainsKey("Type") && e["Type"].ToString() == "defender_excl")
                                    foreach (var pn in e["Processes"].ToString().Split(','))
                                        if (pn.Trim().Length > 0) DefenderExclusion("Remove", pn.Trim());
                    }
                }
            }
            catch { }
            try { if (File.Exists(GamePowerFile)) File.Delete(GamePowerFile); } catch { }
            // NVIDIA 驱动配置还原
            if (HasNvidia || File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll")))
            {
                try { Log("[NVAPI] " + NvDrs.ResetGameProfiles(new List<string>[] { FpsList(), Cfg.MmoGames, Cfg.AaaGames, Cfg.GachaGames })); }
                catch (Exception ex) { Log("[NVAPI] 还原失败: " + ex.Message); }
            }
            Log("—— 恢复系统默认完成（多数项即时生效，HAGS/电源计划建议重启）——");
        }

        static void DeleteRegValue(string path, string name)
        {
            try
            {
                var key = RegKey(path, true);
                if (key != null) { key.DeleteValue(name, false); key.Close(); }
            }
            catch { }
        }

        // ============ 状态审计 ============
        // DPC / 中断分布快照（只读）。只取一次样 —— 目的是"下次再出现帧时间尖峰时有依据"，
        // 不是做持续监控（那是 LatencyMon 的活）。排除 _Total，只看具体核心。
        static StatusItem DpcSnapshotItem()
        {
            try
            {
                int maxDpc = -1, maxInt = -1; string dpcCore = null, intCore = null;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name,PercentDPCTime,InterruptsPersec FROM Win32_PerfFormattedData_PerfOS_Processor"))
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string nm = Convert.ToString(mo["Name"]);
                        if (nm == "_Total") continue;
                        int dpc = 0, itr = 0;
                        int.TryParse(Convert.ToString(mo["PercentDPCTime"]), out dpc);
                        int.TryParse(Convert.ToString(mo["InterruptsPersec"]), out itr);
                        if (dpc > maxDpc) { maxDpc = dpc; dpcCore = nm; }
                        if (itr > maxInt) { maxInt = itr; intCore = nm; }
                    }
                if (maxDpc < 0)
                    return new StatusItem { Item = "DPC / 中断分布", Current = "计数器不可用", Expected = "只读快照", Status = "🟡 已跳过" };
                return new StatusItem
                {
                    Item = "DPC / 中断分布",
                    Current = "DPC 峰值 " + maxDpc + "%(核 " + dpcCore + "，单次取样)，中断峰值 " + maxInt + "/s(核 " + intCore + ")",
                    Expected = "单次取样噪声大：<25% 视为正常；持续偏高才需查驱动/网卡中断",
                    Status = maxDpc < 25 ? "✅ 正常" : "🟡 偏高"
                };
            }
            catch (Exception ex)
            {
                return new StatusItem { Item = "DPC / 中断分布", Current = "读取失败: " + ex.Message, Expected = "只读快照", Status = "🟡 已跳过" };
            }
        }

        public static List<StatusItem> GetStatusItems()
        {
            var list = new List<StatusItem>();
            string schemeLine = RunCmd("powercfg", "/getactivescheme");
            string curScheme = "未知";
            var m = Regex.Match(schemeLine, @"\((.*?)\)");
            if (m.Success) curScheme = m.Groups[1].Value;
            list.Add(new StatusItem { Item = "电源计划", Current = curScheme, Expected = Cfg.PowerScheme == "ultimate" ? "卓越性能" : "高性能",
                Status = (curScheme.Contains("卓越") || curScheme.Contains("高性能")) ? "✅ 已应用" : "⚠️ 未应用" });

            string min = GetPowerCfgSetting("SUB_PROCESSOR", "PROCTHROTTLEMIN");
            string max = GetPowerCfgSetting("SUB_PROCESSOR", "PROCTHROTTLEMAX");
            bool procOk = min == Cfg.MinProc.ToString() && max == Cfg.MaxProc.ToString();
            list.Add(new StatusItem { Item = "处理器最小/最大状态", Current = min + "% / " + max + "%", Expected = Cfg.MinProc + "% / " + Cfg.MaxProc + "%",
                Status = procOk ? "✅ 已应用" : "⚠️ 未应用" });

            // 最大状态单独再给一行（v3.6.0 补全）：上面那行双击只会落到"最小状态"（那是另一个旋钮），
            // 不给行 = 体检表里看不到、点不到，而它本身是有档位的（100% 保睿频 / 95% 以下关睿频）。
            list.Add(new StatusItem { Item = "处理器最大状态(睿频上限)", Current = max + "%", Expected = Cfg.MaxProc + "%",
                Status = max == Cfg.MaxProc.ToString() ? "✅ 已应用" : "⚠️ 未应用" });

            string usb = GetPowerCfgSetting("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
            list.Add(new StatusItem { Item = "USB 选择性暂停", Current = usb == "0" ? "已禁用" : (usb == "?" ? "?" : "启用(" + usb + ")"), Expected = "禁用(0)",
                Status = usb == "0" ? "✅ 已应用" : "⚠️ 未应用" });

            string aspm = GetPowerCfgSetting("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5");
            list.Add(new StatusItem { Item = "PCIe ASPM 节能", Current = aspm == "0" ? "已关闭" : (aspm == "?" ? "?" : "开启(" + aspm + ")"), Expected = "关闭(0)",
                Status = aspm == "0" ? "✅ 已应用" : "⚠️ 未应用" });

            string stby = GetPowerCfgSetting("SUB_SLEEP", "STANDBYIDLE");
            list.Add(new StatusItem { Item = "自动睡眠", Current = stby == "0" ? "从不" : stby + " 秒", Expected = "从不(0)",
                Status = (stby == "0" || stby == "2147483648") ? "✅ 已应用" : "⚠️ 未应用" });

            object gdvr = ReadReg("HKCU:\\System\\GameConfigStore", "GameDVR_Enabled");
            bool gdvrOk = (gdvr is int && (int)gdvr == 0);
            list.Add(new StatusItem { Item = "GameDVR 游戏录制", Current = gdvrOk ? "已关闭" : (gdvr == null ? "未设置" : "开启"), Expected = "关闭(0)",
                Status = gdvrOk ? "✅ 已应用" : (gdvr == null ? "🟡 未设置" : "⚠️ 未应用") });

            string mmPath = "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";
            object nti = ReadReg(mmPath, "NetworkThrottlingIndex");
            bool ntiOk = (nti is int && (int)nti == -1);
            list.Add(new StatusItem { Item = "网络流量节流", Current = ntiOk ? "已禁用" : (nti == null ? "未设置" : "值 " + nti), Expected = "禁用(0xFFFFFFFF)",
                Status = ntiOk ? "✅ 已应用" : (nti == null ? "🟡 未设置" : "⚠️ 未应用") });

            object sr = ReadReg(mmPath, "SystemResponsiveness");
            bool srOk = (sr is int && (int)sr == Cfg.SysResp);
            list.Add(new StatusItem { Item = "前台响应优先级", Current = sr == null ? "未设置" : "值 " + sr, Expected = "值 " + Cfg.SysResp,
                Status = srOk ? "✅ 已应用" : (sr == null ? "🟡 未设置" : "⚠️ 未应用") });

            string nagle = "未开启";
            try
            {
                using (var ifaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                {
                    if (ifaces != null)
                        foreach (var sub in ifaces.GetSubKeyNames())
                        {
                            using (var k = ifaces.OpenSubKey(sub))
                            {
                                if (k != null)
                                {
                                    object f = k.GetValue("TcpAckFrequency");
                                    if (f is int && (int)f == 1) { nagle = "已关闭"; break; }
                                }
                            }
                        }
                }
            }
            catch { }
            list.Add(new StatusItem { Item = "Nagle 算法(小包延迟)", Current = nagle, Expected = "关闭(1)",
                Status = nagle == "已关闭" ? "✅ 已应用" : "⚠️ 未应用" });

            list.Add(new StatusItem { Item = "游戏联动(自动切远控)", Current = Cfg.AwareEnable ? "开启" : "关闭", Expected = "开启",
                Status = Cfg.AwareEnable ? "✅ 已启用" : "🟡 已停用" });

            object wps = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation");
            bool wpsOk = (wps is int && (int)wps == Cfg.SchedSep);
            list.Add(new StatusItem { Item = "CPU调度(Win32优先级分离)", Current = wps == null ? "未设置" : "0x" + ((int)wps).ToString("X") + "(" + (int)wps + ")",
                Expected = "0x" + Cfg.SchedSep.ToString("X") + " 短量子+前台增强",
                Status = wpsOk ? "✅ 已应用" : (wps == null ? "🟡 未设置" : "⚠️ 未应用") });

            object hw = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", "HwSchMode");
            bool hwOk = (hw is int && (int)hw == 2);
            list.Add(new StatusItem { Item = "硬件加速GPU计划(HAGS)", Current = hwOk ? "开启" : (hw == null ? "未设置(默认关)" : "关闭"), Expected = "开启(2，重启生效)",
                Status = hwOk ? "✅ 已应用" : "⚠️ 未应用" });

            // ---- 2026-09-19 补：两个社区视频的建议纳入核验 ----
            // ① 系统虚拟化（视频《自己的配置明明不错，但还是帧数上不去》第 1 步）。
            //    hypervisor 不加载 = 少一层虚拟化，作者自述收益是"减少帧生成时间抖动"。
            bool hvRun = HypervisorRunning();
            string hlt = HypervisorLaunchType();
            list.Add(new StatusItem
            {
                Item = "系统虚拟化(Hyper-V 层)",
                Current = hvRun ? "hypervisor 运行中" : ("未运行" + (hlt == null ? "，BCD 未设置(=出厂 Auto)" : "，BCD " + hlt)),
                Expected = "未运行更稳（代价：WSL2 / Docker / 安卓模拟器 / 沙盒不可用）",
                Status = hvRun ? (Cfg.VirtDisableEnable ? "⚠️ 未生效" : "🟡 可关闭") : "✅ 未运行"
            });

            // ② 异类策略 HeteroPolicy（视频《大小核平台 Win11 比 Win10 更好用》）。
            //    视频推荐的 0（所有核可调度）**正是 Win11 默认档**——所以"不动"就是最优解；
            //    只有 8 个 P 核以上的平台才值得考虑 3（小核休眠）。这里只核验，不写入。
            if (HybridCpu)
            {
                string hp = GetPowerCfgSetting("54533251-82be-4824-96c1-47b60b740d00", "93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
                string hpName = hp == "0" ? "所有处理器(默认/推荐)" : hp == "2" ? "大核休眠(≈优先 P 核)"
                              : hp == "3" ? "小核休眠(≈BIOS 关 E 核)" : hp == "4" ? "按利用率随机停放(旧默认)" : null;
                bool hpDefault = (hp == "?" || hp == null || hp == "0");
                list.Add(new StatusItem
                {
                    Item = "异类策略 HeteroPolicy",
                    Current = hpDefault ? "未设置(=Win11 默认 0)" : (hpName ?? ("值 " + hp)),
                    Expected = "所有处理器(0) —— 默认即最优，不必改",
                    // 🟡 的文案要 ≤3 个汉字：状态列宽 86px，胶囊塞不下更长
                    // （见 Program.BadgeText 的注释）
                    Status = hpDefault ? "✅ 无需处理" : "🟡 非默认"
                });
                list.Add(new StatusItem
                {
                    Item = "核心拓扑(P/E 核)",
                    Current = "P " + PPhysical + " 核 / E " + EPhysical + " 核",
                    Expected = PPhysical >= 8 ? "P 核充裕，可试「小核休眠」策略(3)" : "P " + PPhysical + " 核属「不该限制」档，保持默认",
                    Status = "✅ 已读取"
                });
            }

            // ③ HAGS 在 30/20 系竞技射击上是长期争论点：NVIDIA 与多数指南推荐开，但高帧率区间的
            //    帧时间抖动有反例报告。本工具**不替你改默认**，只在"已是 30/20 系 + 有竞技档游戏名单"
            //    时提示可以 A/B 实测（HAGS 切换必须重启，不能游戏中途切）。
            //    v3.6.1：这一行挂上 hags.enable —— 看到"可以 A/B"就能当场切，不必回去找上面那行。
            bool ampere = !string.IsNullOrEmpty(GpuName)
                          && (GpuName.IndexOf("RTX 30", StringComparison.OrdinalIgnoreCase) >= 0
                              || GpuName.IndexOf("RTX 20", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hwOk && ampere && FpsList().Count > 0)
                list.Add(new StatusItem
                {
                    Item = "HAGS × 竞技游戏(A/B 建议)",
                    Current = GpuName + "，HAGS 已开",
                    Expected = "已是推荐档（保持开）；双击本行可关掉做 A/B —— 切换要重启系统",
                    // 🟡 = 不是缺陷：现状已是推荐值，只是"值得你自己验一次"。
                    // 以前这行被归进"需处理"涂红，和自己的"已开"自相矛盾。
                    Status = "🟡 可实测"
                });

            // ④ CS2 显示模式：独占全屏省掉一层 DWM 合成，买枪菜单（2D UI 层）打开时收益最直接
            string cs2Mode = Cs2DisplayMode();
            if (cs2Mode != null)
                list.Add(new StatusItem
                {
                    Item = "CS2 显示模式",
                    Current = cs2Mode,
                    Expected = "独占全屏（窗口化全屏要走 DWM 合成）",
                    Status = cs2Mode == "独占全屏" ? "✅ 已应用" : "🟡 可改"
                });

            // ---- 2026-09-19 新增：驱动层只读核验（直读 nvdrsdb）----
            // 起因：本机「全局着色器缓存被禁用」这件事工具看不见，只能靠外部脚本读，
            //   结果追了两天才定位。驱动层是"改完就忘"的重灾区，所以把可见性做进体检表。
            // 本段全部只读，不写任何值。
            var nvdb = NvDrsDb.Snapshot();
            if (nvdb.Ok)
            {
                list.Add(new StatusItem
                {
                    Item = "驱动·着色器缓存开关",
                    Current = nvdb.ShaderCache,
                    Expected = "开关=开，且无「0=禁用」记录",
                    Status = nvdb.ShaderCacheDisabled ? "🔴 已禁用" : "✅ 正常"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 模型覆盖",
                    Current = nvdb.DlssOverride,
                    Expected = Cfg.DlssOverride ? "本工具已开启（配置：dlss.override=true）" : "可用 Transformer 超分（3060 Ti 支持；在自定义优化项里开）",
                    Status = nvdb.DlssOverrideOn ? "✅ 已开启" : "🟡 未开启"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·Resizable BAR",
                    Current = nvdb.Rbar,
                    Expected = "只读核验（驱动按游戏预设，勿全局强开）",
                    Status = "✅ 已读取"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·帧率上限(后台)",
                    Current = nvdb.FrlBackground,
                    Expected = "无记录或 0（不限制）",
                    Status = "✅ 已读取"
                });

                // v3.6.0：把这两个值单独拉出来 —— 它们是「能给出具体操作」的典型，
                // 双击行即可在 12 档 / 6 个预设里改（此前只能看"期望值"）。
                list.Add(new StatusItem
                {
                    Item = "驱动·着色器缓存上限",
                    Current = KnobText("nv.shaderCacheSize"),
                    Expected = "双击本行从 12 档里选（驱动按 LRU 淘汰，不是「设多大就占多大」）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 强制预设",
                    Current = Cfg.DlssOverride ? KnobText("nv.dlssPresetLetter") : "未启用（先开上面的「模型覆盖总开关」）",
                    Expected = "双击本行选择；30 系建议「使用推荐值」，M 预设约 -20% 帧",
                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·DLSS 预设档",
                    Current = KnobText("nv.dlssPresetProfile"),
                    Expected = "双击本行选择；「强制预设字母」不生效时驱动要求一起改这一项",
                    Status = Cfg.DlssOverride ? "✅ 可调" : "🟡 未启用"
                });

                // 竞技档的四个值各自成行（v3.6.0 补全）。以前合成一行、双击只落在"预渲染帧数"上，
                // 另外三项虽然也在同一个对话框里，但体检表里既看不见也点不到 —— 用户的要求是
                // "能给出具体操作的就该把权限给用户"，所以一项一行、各自可双击。
                list.Add(new StatusItem
                {
                    Item = "驱动·竞技档·预渲染帧数",
                    Current = KnobText("nv.compPrerenderedFrames"),
                    Expected = "双击可调（1=最低延迟；CPU 瓶颈调低更跟手，GPU 瓶颈反而掉帧）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·竞技档·电源管理",
                    Current = KnobText("nv.compPowerMode"),
                    Expected = "双击可调（最高性能更跟手、更费电更热；笔记本可退到最佳功率）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·竞技档·垂直同步",
                    Current = KnobText("nv.compVsync"),
                    Expected = "双击可调（强制关=最低延迟但撕裂；有 G-Sync/FreeSync 建议跟随游戏内设置）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·竞技档·纹理过滤质量",
                    Current = KnobText("nv.compTextureQuality"),
                    Expected = "双击可调（高性能省一点 GPU，代价是远处贴图发糊）",
                    Status = "✅ 可调"
                });
                list.Add(new StatusItem
                {
                    Item = "驱动·3A/MMO/二游档取值",
                    Current = "垂直同步 " + KnobText("nv.aaaVsync")
                            + " · 电源 " + KnobText("nv.compPowerMode")
                            + " · 缓存 " + KnobText("nv.shaderCacheSize"),
                    Expected = "双击本行可一并调整；画质档默认不强制关垂直同步（无 VRR 屏会撕裂）",
                    Status = "✅ 可调"
                });
            }
            else
            {
                list.Add(new StatusItem
                {
                    Item = "驱动层设置(直读 nvdrsdb)",
                    Current = "驱动配置库不可读",
                    Expected = "需已安装 NVIDIA 驱动",
                    Status = "🟡 已跳过"
                });
            }

            // 窗口化游戏的优化（SwapEffectUpgradeEnable）——微软口径：**只对窗口/无边框的 DX10/11 生效**，
            // 独占全屏基本无感。它的价值在反向：某游戏进了无边框模式却关着它 → 白丢一截延迟。
            string dxgs = Convert.ToString(ReadReg("HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences", "DirectXUserGlobalSettings"));
            bool swapUp = dxgs != null && dxgs.IndexOf("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase) >= 0;
            list.Add(new StatusItem
            {
                Item = "窗口化游戏的优化",
                Current = dxgs == null ? "未设置" : (swapUp ? "已开启" : "未开启"),
                Expected = "开启（仅影响窗口/无边框的 DX10/11 游戏）",
                Status = swapUp ? "✅ 已应用" : "🟡 未开启"
            });

            // 每程序「全屏优化」兼容标记（AppCompatFlags\Layers）——与工具写的**全局** FSE 键是两套存储。
            // 用户若手动给某个 exe 勾过「禁用全屏优化」，工具此前完全看不见。
            int compatCnt = 0;
            try
            {
                using (var lk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                {
                    if (lk != null)
                        foreach (var vn in lk.GetValueNames())
                        {
                            string vv = Convert.ToString(lk.GetValue(vn));
                            if (vv != null && vv.IndexOf("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) >= 0) compatCnt++;
                        }
                }
            }
            catch { }
            list.Add(new StatusItem
            {
                Item = "每程序全屏优化标记",
                Current = compatCnt == 0 ? "无（都用系统默认）" : (compatCnt + " 个程序被标记「禁用全屏优化」"),
                Expected = "无特殊需要时保持默认",
                Status = compatCnt == 0 ? "✅ 默认" : "🟡 见说明"
            });

            list.Add(DpcSnapshotItem());


            object gp8 = ReadReg("HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games", "GPU Priority");
            list.Add(new StatusItem { Item = "多媒体Games任务优先级", Current = gp8 == null ? "未设置" : "值 " + gp8, Expected = "GPU Priority=8",
                Status = (gp8 is int && (int)gp8 == 8) ? "✅ 已应用" : (gp8 == null ? "🟡 未设置" : "⚠️ 未应用") });

            list.Add(new StatusItem { Item = "游戏定时器联动(0.5ms)", Current = Cfg.TimerEnable ? (IsTimerApplied() ? "生效中(游戏中)" : "待机") : "关闭", Expected = Cfg.TimerEnable ? "开启" : "关闭",
                Status = Cfg.TimerEnable ? "✅ 已启用" : "🟡 已停用" });

            list.Add(new StatusItem { Item = "游戏加速包(挂起/整理/停更新)", Current = Cfg.GbEnable ? "开启" : "关闭", Expected = "开启",
                Status = Cfg.GbEnable ? "✅ 已启用" : "🟡 已停用" });

            object agm = ReadReg("HKCU:\\Software\\Microsoft\\GameBar", "AutoGameModeEnabled");
            list.Add(new StatusItem { Item = "Windows 游戏模式", Current = (agm is int && (int)agm == 1) ? "开启" : (agm == null ? "未设置(默认开)" : "关闭"), Expected = "开启(1)",
                Status = (agm is int && (int)agm == 1) ? "✅ 已应用" : "🟡 未确认" });

            object gtr = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel", "GlobalTimerResolutionRequests");
            list.Add(new StatusItem { Item = "全局定时器请求(Win10 2004+)", Current = (gtr is int && (int)gtr == 1) ? "启用" : (gtr == null ? "未设置" : "关闭"), Expected = "启用(1，重启生效)",
                Status = (gtr is int && (int)gtr == 1) ? "✅ 已应用" : "⚠️ 未应用" });

            object df = ReadReg("HKCU:\\Control Panel\\Desktop", "DragFullWindows");
            object tr = ReadReg("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "EnableTransparency");
            bool dragOn = (df is string && (string)df == "0") && (tr is int && (int)tr == 0);
            list.Add(new StatusItem { Item = "拖拽流畅(边框/透明)", Current = dragOn ? "已开启" : "未开启", Expected = "开启",
                Status = dragOn ? "✅ 已应用" : "⚠️ 未应用" });

            // ---- v2.0 新增项 ----
            if (HybridCpu)
            {
                // 查"异类短运行线程调度策略"（bae08b81，游戏线程正是短运行线程）。
                // 旧版查 93b8b6dc（"异类线程调度策略"总开关），本机不存在该设置，
                // powercfg 返回"设置不存在"→ 解析出"?" → 永远显示未应用（实测踩过）。
                string het = GetPowerCfgSetting("54533251-82be-4824-96c1-47b60b740d00", "bae08b81-2d5e-4688-ad6a-13243356654b");
                string hetName = het == "0" ? "所有处理器" : het == "1" ? "高性能处理器(P核)"
                               : het == "2" ? "首选高性能处理器" : het == "3" ? "高效处理器"
                               : het == "4" ? "首选高效处理器" : het == "5" ? "自动" : null;
                bool hetOk = het == "1";
                list.Add(new StatusItem
                {
                    Item = "异类线程调度策略(混合架构)",
                    Current = het == "?" ? "?" : (hetName ?? ("值 " + het)),
                    Expected = "高性能处理器(P核)(1)",
                    Status = hetOk ? "✅ 已应用" : "⚠️ 未应用"
                });
            }
            else
            {
                list.Add(new StatusItem { Item = "异类线程调度策略(混合架构)", Current = "非混合架构 CPU", Expected = "不适用", Status = "✅ 无需设置" });
            }

            string vbs = VbsState();
            list.Add(new StatusItem
            {
                Item = "内核隔离 / VBS（仅检测）",
                Current = vbs.IndexOf("未启用") >= 0 ? "未启用(性能最优)" : (vbs.IndexOf("读取失败") >= 0 ? "?" : "已启用(可能有帧损)"),
                Expected = "自行权衡（本工具不代改）",
                Status = vbs.IndexOf("未启用") >= 0 ? "✅ 无需处理" : "🟡 见说明"
            });

            bool srcOk = Dlssg.SourceReady();
            list.Add(new StatusItem
            {
                Item = "帧生成运行时(DLSSG)",
                Current = srcOk ? "已就绪" : "未下载",
                Expected = "已就绪（不玩 3A 可忽略）",
                Status = srcOk ? "✅ 已就绪" : "🟡 未下载"
            });
            int fgInst = Dlssg.LoadState().Count;
            list.Add(new StatusItem
            {
                Item = "帧生成已接入游戏",
                Current = fgInst + " 个",
                Expected = "按需（仅单机 3A）",
                Status = fgInst > 0 ? "✅ 已接入" : "🟡 未接入"
            });

            // ---- 2026-09-16 补：刷新率 + 此前"已实现但看不到"的项 ----
            //  用户的痛点很具体：「点完一键优化看不到结果」→ 怀疑没生效 → 反复重装。
            //  能读回系统实际值的就真去读；纯行为/监控型模块没有常驻设置可读，就如实只报开关状态。

            int sw, sh, shz;
            bool hasMode = DisplayMode(out sw, out sh, out shz);
            list.Add(new StatusItem
            {
                Item = "显示器刷新率（帧生成上限）",
                Current = hasMode ? (sw + "×" + sh + " @" + shz + "Hz") : "读取失败",
                Expected = hasMode ? ("生成帧超过 " + shz + " 就是无效帧") : "?",
                Status = hasMode ? "✅ 已读取" : "🟡 未读到"
            });

            object ms = ReadReg("HKCU:\\Control Panel\\Mouse", "MouseSpeed");
            object mt1 = ReadReg("HKCU:\\Control Panel\\Mouse", "MouseThreshold1");
            bool mAccOff = Convert.ToString(ms) == "0" && Convert.ToString(mt1) == "0";
            list.Add(new StatusItem { Item = "鼠标加速(提高指针精确度)", Current = mAccOff ? "已关闭" : "开启", Expected = "关闭(0/0)",
                // 模块被配置关掉 = 符合期望，不能报"未生效"（否则永久红色"需处理"+ 托盘告警）
                Status = mAccOff ? "✅ 已应用" : ((Cfg.InputEnable && Cfg.MouseAccelOff) ? "⚠️ 未应用" : "✅ 已停用") });

            object dpe = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "DisablePagingExecutive");
            bool dpeOk = (dpe is int && (int)dpe == 1);
            list.Add(new StatusItem { Item = "禁止内核换页出内存", Current = dpeOk ? "已开启" : (dpe == null ? "未设置" : "关闭"), Expected = "开启(1，重启生效)",
                Status = dpeOk ? "✅ 已应用" : ((Cfg.MemEnable && Cfg.NoPagingExec) ? "⚠️ 未应用" : "✅ 已停用") });

            // 网卡：PnPCapabilities=24 = 已取消"允许计算机关闭此设备"的两项（与 NicOptimize 写入值一致）
            int nicTotal = 0, nicOk = 0;
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls != null)
                        foreach (string sub in cls.GetSubKeyNames())
                        {
                            if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                            using (var nk = cls.OpenSubKey(sub))
                            {
                                if (nk == null || nk.GetValue("DriverDesc") == null) continue;
                                nicTotal++;
                                object pnc = nk.GetValue("PnPCapabilities");
                                if (pnc is int && (int)pnc == 24) nicOk++;
                            }
                        }
                }
            }
            catch { }
            list.Add(new StatusItem { Item = "网卡节能(挂起/EEE)", Current = nicOk + " / " + nicTotal + " 个已处理", Expected = nicTotal == 0 ? "无网卡" : "全部(" + nicTotal + ")",
                Status = !Cfg.NicEnable ? "✅ 已停用" : (nicTotal == 0 ? "✅ 无需设置" : (nicOk == nicTotal ? "✅ 已应用" : "⚠️ 未应用")) });

            // 服务：Start=4 = 已禁用（与 ServiceOptimize 写入方式一致）
            int svcDone = 0;
            foreach (string sn in Cfg.DisableServices)
            {
                object stv = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Services\\" + sn, "Start");
                if (stv is int && (int)stv == 4) svcDone++;
            }
            list.Add(new StatusItem { Item = "已禁用服务数", Current = svcDone + " / " + Cfg.DisableServices.Count, Expected = Cfg.SvcEnable ? "全部禁用" : "不启用",
                Status = (!Cfg.SvcEnable || Cfg.DisableServices.Count == 0) ? "✅ 无需设置" : (svcDone == Cfg.DisableServices.Count ? "✅ 已应用" : "⚠️ 未应用") });

            object fseB = ReadReg("HKCU:\\System\\GameConfigStore", "GameDVR_FSEBehaviorMode");
            object fseH = ReadReg("HKCU:\\System\\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode");
            bool fseOk = (fseB is int && (int)fseB == 2) && (fseH is int && (int)fseH == 1);
            list.Add(new StatusItem
            {
                Item = "全屏优化(独占全屏)",
                Current = (fseB == null ? "未设置" : "模式 " + fseB) + " / " + (fseH == null ? "未声明尊重" : "尊重用户设置"),
                Expected = "模式 2 + 尊重用户设置(1)",
                Status = !Cfg.GameDvrOff ? "✅ 已停用" : (fseOk ? "✅ 已应用" : "⚠️ 未应用")
            });

            object pt = ReadReg("HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", "PowerThrottlingOff");
            bool ptOk = (pt is int && (int)pt == 1);
            list.Add(new StatusItem { Item = "进程电源节流", Current = ptOk ? "已关闭" : (pt == null ? "未设置(默认开)" : "开启"), Expected = "关闭(1，重启生效)",
                Status = !Cfg.SchedEnable ? "✅ 已停用" : (ptOk ? "✅ 已应用" : "⚠️ 未应用") });

            // 行为型 / 监控型模块：没有可读回的常驻系统设置，只能如实报开关状态
            var mods = new List<string>();
            if (Cfg.CleanEnable) mods.Add("后台清理");
            if (Cfg.DiskGuardEnable) mods.Add("磁盘守护");
            if (Cfg.AdaptiveEnable) mods.Add("自适应策略");
            if (Cfg.OverlayGuardEnable) mods.Add("覆盖层守护");
            if (Cfg.NetWatchEnable) mods.Add("网络哨兵");
            if (Cfg.DefenderExclude) mods.Add("Defender排除");
            list.Add(new StatusItem
            {
                Item = "优化模块(行为/监控型)",
                Current = mods.Count > 0 ? string.Join("、", mods.ToArray()) : "全部关闭",
                Expected = "按需（无常驻设置可回读）",
                Status = mods.Count > 0 ? ("✅ 已启用 " + mods.Count + " 项") : "✅ 全部关闭"
            });

            string dvNow = DriverVerNow();
            string dvBase = "";
            try { if (File.Exists(DriverFile)) dvBase = File.ReadAllText(DriverFile, Encoding.UTF8).Trim(); } catch { }
            bool dvChanged = dvNow.Length > 0 && dvBase.Length > 0 && dvBase != dvNow;
            list.Add(new StatusItem
            {
                Item = "NVIDIA 驱动版本",
                Current = dvNow.Length > 0 ? dvNow : "未检测到",
                Expected = dvBase.Length > 0 ? ("基线 " + dvBase) : "点「一键优化」记录基线",
                // "没建基线"不是漂移（点一次一键优化即建），只有"驱动已换版本"才值得提醒
                Status = dvChanged ? "⚠️ 驱动已更新(见说明)" : (dvBase.Length > 0 ? "✅ 已记录" : "✅ 未建基线")
            });

            // 把可调旋钮挂到对应行上（体检表双击 → 直接改取值）
            AttachKnobKeys(list);

            return list;
        }

        // ============ 恢复 ============
        public static int DoRestore(Action<string> log)
        {
            string dir = Path.Combine(DataDir, "backup");
            if (!Directory.Exists(dir)) { log("未找到备份目录，无需恢复"); return 0; }
            string latest = null;
            foreach (var f in Directory.GetFiles(dir, "backup_*.json"))
                if (latest == null || File.GetLastWriteTime(f) > File.GetLastWriteTime(latest)) latest = f;
            if (latest == null) { log("未找到备份文件，无需恢复"); return 0; }
            log("使用备份: " + Path.GetFileName(latest));
            var list = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(latest, Encoding.UTF8));
            int ok = 0, fail = 0;
            foreach (var e in list)
            {
                string type = e.ContainsKey("Type") ? e["Type"].ToString() : "";
                try
                {
                    if (type == "registry")
                    {
                        string path = e["Path"].ToString(), name = e["Name"].ToString();
                        bool exists = e.ContainsKey("Exists") && (bool)e["Exists"];
                        if (exists)
                        {
                            object val = null;
                            string vt = e.ContainsKey("ValueType") ? e["ValueType"].ToString() : "String";
                            if (vt == "DWord") val = Convert.ToInt32(e["Value"]);
                            else if (vt == "QWord") val = Convert.ToInt64(e["Value"]);
                            else if (vt == "Binary") { var arr = (object[])e["Value"]; var by = new byte[arr.Length]; for (int i = 0; i < arr.Length; i++) by[i] = Convert.ToByte(arr[i]); val = by; }
                            else if (vt == "MultiString") { var arr = (object[])e["Value"]; val = Array.ConvertAll(arr, x => x.ToString()); }
                            else val = e["Value"].ToString();
                            RegistryValueKind kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), vt);
                            WriteReg(path, name, val, kind);
                        }
                        else
                        {
                            try
                            {
                                var key = RegKey(path, true);
                                if (key != null) { key.DeleteValue(name, false); key.Close(); }
                            }
                            catch { }
                        }
                        ok++;
                    }
                    else if (type == "power_scheme")
                    {
                        RunCmd("powercfg", "/setactive " + e["Value"]);
                        log("已恢复电源计划");
                        ok++;
                    }
                    else if (type == "defender_excl")
                    {
                        foreach (var pn in e["Processes"].ToString().Split(','))
                            if (pn.Trim().Length > 0)
                                DefenderExclusion("Remove", pn.Trim());
                        log("已移除 Defender 游戏进程排除");
                        ok++;
                    }
                    else if (type == "bcd")
                    {
                        // 系统虚拟化：按备份的原值精确还原。备份里没存 Value = 当时本是出厂态，
                        // 那就删掉这个值回到出厂（而不是瞎写一个 Auto）。
                        // 回读一次：bcdedit 失败不报错（同 powercfg 的坑），不回读就会"看起来还原了其实没有"。
                        string want = e.ContainsKey("Value") ? e["Value"].ToString() : null;
                        if (want != null) RunCmd("bcdedit", "/set hypervisorlaunchtype " + want);
                        else RunCmd("bcdedit", "/deletevalue hypervisorlaunchtype");
                        string got = HypervisorLaunchType();
                        bool rok = (want == null) ? (got == null) : string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
                        log((want == null ? "已还原虚拟化为出厂态" : "已还原虚拟化为 " + want)
                            + (rok ? "（重启后 WSL2 / Docker / 模拟器恢复可用）"
                                   : " —— ⚠️ 回读为 " + (got == null ? "未设置" : got) + "，未确认"));
                        ok++;
                    }
                }
                catch { fail++; }
            }
            log("恢复完成：成功 " + ok + " 项，失败 " + fail + " 项");
            return ok;
        }

        // ============ 开机自启 ============
        public static bool IsAutoStart()
        {
            // ⚠ v1.0.0 改名修复：任务名已从 GameBoost 改为 Fluxion，判断串却还找 "GameBoost"
            //   —— schtasks 的 LIST 输出里只有新任务名，Contains 永远 false，
            //   表现为"开了开机自启、设置页却显示未开启"（重开一次倒也能覆盖，但状态是假的）。
            return RunCmd("schtasks", "/Query /TN Fluxion /FO LIST").Contains("Fluxion");
        }
        public static void SetAutoStart()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            RunCmd("schtasks", "/Create /TN Fluxion /TR \"\\\"" + exe + "\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F");
        }
        public static void RemoveAutoStart()
        {
            RunCmd("schtasks", "/Delete /TN Fluxion /F");
        }

        // ============ 系统实时状态 ============
        public static int GetCpu()
        {
            try
            {
                using (var mo = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
                {
                    foreach (var o in mo.Get())
                        if (o["LoadPercentage"] != null) return Convert.ToInt32(o["LoadPercentage"]);
                }
            }
            catch { }
            return -1;
        }
        public static int GetMem()
        {
            try
            {
                using (var mo = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"))
                {
                    foreach (var o in mo.Get())
                    {
                        double total = Convert.ToDouble(o["TotalVisibleMemorySize"]);
                        double free = Convert.ToDouble(o["FreePhysicalMemory"]);
                        return (int)Math.Round((total - free) / total * 100);
                    }
                }
            }
            catch { }
            return -1;
        }
        // GPU 信息缓存（2 秒）：避免 UpdateSys 里 GetGpuPct/GetGpuTemp 各启动一次 nvidia-smi.exe 子进程阻塞 UI 线程
        static string[] gpuInfoCache = null;
        static DateTime gpuInfoTime = DateTime.MinValue;
        static string[] GetGpuInfo()
        {
            if (gpuInfoCache != null && (DateTime.Now - gpuInfoTime).TotalSeconds < 2)
                return gpuInfoCache;
            gpuInfoCache = new string[] { "-1", "-1" };
            if (!HasNvidia) { gpuInfoTime = DateTime.Now; return gpuInfoCache; }   // 非 N 卡不反复拉起 nvidia-smi
            try
            {
                string o = RunCmd("nvidia-smi", "--query-gpu=utilization.gpu,temperature.gpu,memory.used --format=csv,noheader,nounits");
                var parts = o.Split(',');
                if (parts.Length >= 2) gpuInfoCache = new string[] { parts[0].Trim(), parts[1].Trim() };
            }
            catch { }
            gpuInfoTime = DateTime.Now;
            return gpuInfoCache;
        }
        public static int GetGpuPct() { try { return Convert.ToInt32(GetGpuInfo()[0]); } catch { return -1; } }
        public static int GetGpuTemp() { try { return Convert.ToInt32(GetGpuInfo()[1]); } catch { return -1; } }

        // 进程快照缓存（2 秒）：IsGameRunning/GetRemoteState 共 13 次 GetProcessesByName → 改为一次 GetProcesses + 内存过滤
        static List<Process> procSnapshot = null;
        static DateTime procSnapshotTime = DateTime.MinValue;
        static List<Process> GetProcSnapshot()
        {
            if (procSnapshot != null && (DateTime.Now - procSnapshotTime).TotalSeconds < 2)
                return procSnapshot;
            try
            {
                var fresh = new List<Process>(Process.GetProcesses());
                if (fresh.Count > 0)
                {
                    procSnapshot = fresh;
                    procSnapshotTime = DateTime.Now;
                    return fresh;
                }
                // 空结果 = 枚举失败而不是真没进程：**不缓存也不交给调用方**。
                //   缓存它会让之后 2 秒内所有判定"看不见任何进程"→ 游戏联动误判"游戏已退出"
                //   → 分辨率/远控/电源全部还原，3 秒后又重新触发一轮（2026-09-27 日志实测一局抖 4 次）。
                return procSnapshot != null ? procSnapshot : fresh;
            }
            catch
            {
                // 枚举异常同理：退回上一次快照（顶多旧 2 秒），绝不返回空表
                return procSnapshot != null ? procSnapshot : new List<Process>();
            }
        }
        public static bool ProcExists(string name)
        {
            foreach (var p in GetProcSnapshot())
                try { if (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            return false;
        }
        // 绕过快照缓存的全新枚举：联动退出防抖用。快照链路偶发"看不见在跑的游戏"
        //（枚举瞬时失败 / 全屏游戏把 UI 消息泵饿死导致 tick 迟到），复核一次就能纠正误报。
        public static bool ProcExistsFresh(string name)
        {
            if (name == null || name.Length == 0) return false;
            try
            {
                foreach (var p in Process.GetProcesses())
                    try { if (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            }
            catch { }
            return false;
        }

        // ============ 自检 ============
        static void SelfTest()
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("BUILD: " + BuildTag + " share=" + ShareBuild + " version=" + DisplayVersion + " dataDir=" + DataDir);
                sb.AppendLine("CONFIG: scheme=" + Cfg.PowerScheme + " killList=" + Cfg.KillList.Count + " games=" + Cfg.GameProcesses.Count + " remoteApps=" + Cfg.RemoteApps.Count);
                DetectSystem();
                sb.AppendLine("SYSTEM: " + SystemSummary());
                sb.AppendLine("TOPOLOGY: hybrid=" + HybridCpu + " PMask=0x" + PMask.ToString("X") + " EMask=0x" + EMask.ToString("X"));
                sb.AppendLine("NVAPI_AVAILABLE: " + NvDrs.Available());
                var items = GetStatusItems();
                sb.AppendLine("STATUS_ITEMS=" + items.Count);
                foreach (var it in items) sb.AppendLine(it.Item + " | " + it.Current + " | " + it.Expected + " | " + it.Status);
                sb.AppendLine("GAME_RUNNING=" + IsGameRunning());
                sb.AppendLine("REMOTE_STATE=" + GetRemoteState());
                sb.AppendLine("EXE_OK");
            }
            catch (Exception ex) { sb.AppendLine("FAIL: " + ex.ToString()); }
            File.WriteAllText(Path.Combine(DataDir, "selftest.txt"), sb.ToString(), Encoding.UTF8);
        }
    }
}
