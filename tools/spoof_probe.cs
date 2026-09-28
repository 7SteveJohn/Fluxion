// 显卡名伪装：只读验证（绝不写注册表 —— 写 HKLM 要管理员，也不该在测试里动用户的系统）
namespace Fluxion
{
    using System;
    using System.IO;

    static class SpoofProbe
    {
        static int Fail = 0;
        static void L(string s) { Console.WriteLine(s); }
        static void Chk(string what, bool ok, string extra)
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (extra == null || extra.Length == 0 ? "" : "   -> " + extra));
            if (!ok) Fail++;
        }

        static int Main()
        {
            L("=== 显卡名伪装 · 只读验证 ===");
            GpuSpoof.St st = GpuSpoof.Detect();
            L("  设备实例数     = " + st.Count);
            L("  设备键         = " + st.Key);
            L("  DeviceDesc     = " + st.DeviceDesc);
            L("  FriendlyName   = " + (st.FriendlyName == null ? "(无)" : st.FriendlyName));
            L("  重建原始形式   = " + st.InfForm);
            L("  是否已伪装     = " + st.Spoofed);
            L("");

            Chk("恰好找到 1 个 NVIDIA 显示适配器（Service=nvlddmkm）", st.Count == 1, "count=" + st.Count);
            Chk("设备键是 VEN_10DE&DEV_xxxx 形式", st.Key.StartsWith("VEN_10DE&DEV_"), st.Key);
            Chk("原始形式保留 inf 前缀（还原后才显示正常）",
                st.InfForm.StartsWith("@") && st.InfForm.IndexOf(';') > 0, st.InfForm);
            Chk("原始形式里的友好名以 NVIDIA 开头", st.InfForm.IndexOf("NVIDIA ") >= 0, st.InfForm);
            Chk("当前未伪装（本机现状）", !st.Spoofed, st.DeviceDesc);
            Chk("StatusLine 读出当前显卡名", GpuSpoof.StatusLine().IndexOf("当前显卡名") >= 0, GpuSpoof.StatusLine());

            string bk = GpuSpoof.EnsureBackup();
            L("  备份文件       = " + (bk.Length == 0 ? "(未生成 —— 已伪装状态下正常)" : bk));
            if (bk.Length > 0)
            {
                Chk("备份文件已落盘", File.Exists(bk), bk);
                string c = File.ReadAllText(bk).Trim();
                Chk("备份内容 = 重建的原始形式", c == st.InfForm.Trim(), c);
                Chk("备份内容不是伪装值", c.IndexOf("5090") < 0, "");
            }
            else
            {
                Chk("已处于伪装状态 -> 不需要备份", true, "");
            }

            L("");
            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            return Fail;
        }
    }
}
