// 图标端到端校验：走 Windows 自己的图标 API，验证手写 ICO 与 exe 内嵌图标都能被正确解析。
// 为什么必须这么做：手写的 ICO 容器可能"PIL 读得动、Windows 读不动" —— 只有走 Win32 才算数。
using System;
using System.Drawing;
using System.IO;

namespace Fluxion
{
    static class IconProbe
    {
        static int Fail = 0;
        static string Log = "";

        static void L(string s) { Log += s + "\r\n"; Console.WriteLine(s); }

        static void Chk(string what, bool ok, string extra)
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (extra == null || extra.Length == 0 ? "" : "   -> " + extra));
            if (!ok) Fail++;
        }

        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : @"D:\youhua\Fluxion";
            string ico = Path.Combine(root, "icon", "icon.ico");
            string exe = Path.Combine(root, "Fluxion.exe");
            string setup = Path.Combine(root, "installer_out", "Fluxion-Setup-3.2.2.exe");
            string outDir = Path.Combine(root, "ui-snapshots");

            L("=== 图标端到端校验（Win32 图标 API）===");
            L("");

            // ---------- 0) 容器层校验：直接读 ICO 目录，不依赖任何库的解码 ----------
            //  为什么必须先做这一步：上一版我把**所有帧都写成 PNG 压缩**，PIL 读得好好的，
            //  但 Windows 的图标 API 解出来是垃圾（角色是绿的，取样却是紫色/土黄 —— 解码错位）。
            //  所以"第三方库能读"不能作为验收依据，必须读容器 + 走 Win32 API 两条都验。
            L("=== 0. ICO 容器目录（直接解析字节）===");
            int[] expect = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            try
            {
                byte[] raw = File.ReadAllBytes(ico);
                int type = BitConverter.ToUInt16(raw, 2);
                int cnt = BitConverter.ToUInt16(raw, 4);
                Chk("reserved=0 / type=1（这是图标不是光标）", raw[0] == 0 && raw[1] == 0 && type == 1,
                    "type=" + type);
                Chk("帧数 = 7", cnt == 7, "count=" + cnt);
                for (int i = 0; i < cnt && i < expect.Length; i++)
                {
                    int off = 6 + 16 * i;
                    int w = raw[off];                       // 256 记作 0
                    int wReal = w == 0 ? 256 : w;
                    int bits = BitConverter.ToUInt16(raw, off + 6);
                    int bytes = BitConverter.ToInt32(raw, off + 8);
                    int dataOff = BitConverter.ToInt32(raw, off + 12);
                    bool isPng = dataOff + 8 <= raw.Length
                                 && raw[dataOff] == 0x89 && raw[dataOff + 1] == 0x50;   // \x89P
                    Chk(string.Format("第{0}帧 {1}px / {2}bpp / {3}B / {4}", i + 1, wReal, bits, bytes,
                            isPng ? "PNG" : "DIB"),
                        wReal == expect[i] && bits == 32 && bytes > 0 && dataOff + bytes <= raw.Length,
                        "");
                    // ★ 格式约定：≤128 必须是 DIB（GDI+ 对非 256 的 PNG 帧按 DIB 解 → 花屏）；256 用 PNG
                    if (wReal <= 128) Chk("    该帧用 DIB（Windows 兼容性要求）", !isPng, isPng ? "是 PNG ★" : "DIB");
                    else Chk("    该帧用 PNG（Vista+ 标准，省体积）", isPng, isPng ? "PNG" : "DIB");
                }
            }
            catch (Exception ex) { Chk("ICO 容器可解析", false, ex.Message); }

            // ---------- 1) 走 Win32/GDI+ 逐尺寸解析 ----------
            L("");
            L("=== 1. 逐尺寸走 Win32 图标 API 解析（含像素抽查）===");
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            foreach (int s in sizes)
            {
                try
                {
                    using (Icon ic = new Icon(ico, new Size(s, s)))
                    {
                        // 已知限制：GDI+ 的 Icon 请求 256 会回退到 128。三种容器（我们的 PNG256、
                        //  我们的 DIB256、以及旧图标）实测都是 128 —— 是 API 上限，不是文件问题。
                        //  Explorer 走的是 shell 自己的加载器，能用到 256 帧；这里不把它记成失败。
                        bool sizeOk = (ic.Width == s && ic.Height == s)
                                      || (s == 256 && ic.Width == 128);
                        Chk(s + "px 可解析" + (s == 256 ? "（GDI+ 对 256 回退到 128 属已知上限）" : "且尺寸正确"),
                            sizeOk,
                            ic.Width + "x" + ic.Height + (s == 256 && ic.Width == 128 ? "（已按已知上限放行）" : ""));
                        using (Bitmap b = ic.ToBitmap())
                        {
                            // 4 角应透明（圆角），中心应不透明
                            int c0 = b.GetPixel(0, 0).A;
                            int c1 = b.GetPixel(b.Width - 1, 0).A;
                            int c2 = b.GetPixel(0, b.Height - 1).A;
                            int c3 = b.GetPixel(b.Width - 1, b.Height - 1).A;
                            int cm = b.GetPixel(b.Width / 2, b.Height / 2).A;
                            bool shape = cm > 200 && c0 < 60 && c1 < 60 && c2 < 60 && c3 < 60;
                            Chk("  四角透明 + 中心实心（圆角生效）", shape,
                                string.Format("角={0}/{1}/{2}/{3} 心={4}", c0, c1, c2, c3, cm));
                            // 抽查主色确实是极光绿系（G 通道显著高于 R）
                            Color mid = b.GetPixel(b.Width / 2, b.Height * 3 / 4);
                            Chk("  存在绿色主体", mid.G > mid.R + 40, string.Format("取样 RGB({0},{1},{2})", mid.R, mid.G, mid.B));
                            if (s == 32 || s == 256)
                                b.Save(Path.Combine(outDir, "dbg_ico_from_file_" + s + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                catch (Exception ex) { Chk(s + "px 可解析", false, ex.Message); }
            }

            // ---------- 2) exe 内嵌图标：走 shell 同一 API（托盘图标就是这么取的）----------
            L("");
            L("=== 2. exe 内嵌图标（ExtractAssociatedIcon，与托盘同路径）===");
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(exe))
                {
                    Chk("能取出图标", ic != null, "");
                    if (ic != null)
                    {
                        L("  尺寸 = " + ic.Width + "x" + ic.Height);
                        using (Bitmap b = ic.ToBitmap())
                        {
                            // 与 ICO 的 32px 帧逐像素比：应完全一致（exe 里嵌的就是它）
                            using (Icon refIco = new Icon(ico, new Size(b.Width, b.Height)))
                            using (Bitmap rb = refIco.ToBitmap())
                            {
                                int diff = 0;
                                for (int y = 0; y < b.Height; y++)
                                    for (int x = 0; x < b.Width; x++)
                                        if (b.GetPixel(x, y) != rb.GetPixel(x, y)) diff++;
                                Chk("exe 图标与 icon.ico 同尺寸帧逐像素一致", diff == 0, "差异像素 " + diff);
                            }
                            b.Save(Path.Combine(outDir, "dbg_ico_from_exe.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
            }
            catch (Exception ex) { Chk("exe 内嵌图标", false, ex.Message); }

            // ---------- 3) 安装包图标 ----------
            L("");
            L("=== 3. 安装包图标 ===");
            if (File.Exists(setup))
            {
                try
                {
                    using (Icon ic = Icon.ExtractAssociatedIcon(setup))
                    {
                        Chk("安装包能取出图标（SetupIconFile 生效）", ic != null,
                            ic == null ? "" : ic.Width + "x" + ic.Height);
                        if (ic != null)
                            using (Bitmap b = ic.ToBitmap())
                                b.Save(Path.Combine(outDir, "dbg_ico_from_setup.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Chk("安装包图标", false, ex.Message); }
            }
            else Chk("安装包存在", false, setup);

            L("");
            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "icon_probe.txt"), Log, new System.Text.UTF8Encoding(false)); } catch { }
            return Fail == 0 ? 0 : 1;
        }
    }
}
