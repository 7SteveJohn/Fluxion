// 取值细调旋钮表：把 Program.Knobs() 的实际内容原样导出成 Markdown。
// 为什么要探针而不是手写文档：档位名称/取值/默认值散在代码里，手抄一定会漂移
// （本机 2026-09-19 就手算错了 0x08416747 的十进制）。这里直接读内存里的表，导出即事实。
// 全程只读：不写注册表、不调 ApplyKnob、不改配置。
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    static class KnobsProbe
    {
        static void Main(string[] args)
        {
            string outPath = (args != null && args.Length > 0) ? args[0] : "knobs.md";
            var sb = new StringBuilder();
            List<OptKnob> knobs = Program.Knobs();

            sb.AppendLine("# Fluxion 取值细调 · 全量档位表");
            sb.AppendLine();
            sb.AppendLine("> 本文件由 `tools/knobs_probe.cs` 直接读 `Program.Knobs()` 导出（不是手写的），");
            sb.AppendLine("> 所以和程序实际提供的档位永远一致。共 " + knobs.Count + " 项。");
            sb.AppendLine();

            string last = null;
            foreach (OptKnob k in knobs)
            {
                if (k.Group != last)
                {
                    sb.AppendLine("## " + k.Group);
                    sb.AppendLine();
                    sb.AppendLine("| 项目 | 当前 | 可选档位 | 落在哪 | 生效 |");
                    sb.AppendLine("|---|---|---|---|---|");
                    last = k.Group;
                }
                var opts = new List<string>();
                for (int i = 0; i < k.Labels.Length; i++)
                {
                    string mark = (k.Values[i] == k.Value) ? "**" : "";
                    opts.Add(mark + k.Labels[i] + mark + " `" + Raw(k.Values[i]) + "`");
                }
                sb.AppendLine("| " + k.Title + " | " + k.CurrentText + " | " + string.Join("<br>", opts.ToArray())
                              + " | `" + k.Sec + "." + k.Name + "` | " + Where(k.Apply) + (k.Restart ? "，需重启" : "") + " |");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(k.Hint))
                {
                    sb.AppendLine("> " + k.Hint);
                    sb.AppendLine();
                }
            }

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine("已导出 " + knobs.Count + " 项 -> " + outPath);

            // 顺带把「只读核验项」也列出来：用户最常问"为什么这个不能改"
            var items = Program.GetStatusItems();
            int readonlyCount = 0;
            Console.WriteLine("");
            Console.WriteLine("=== 只读核验项（没有 ⚙，不代改）===");
            foreach (StatusItem it in items)
            {
                if (!string.IsNullOrEmpty(it.Key)) continue;
                readonlyCount++;
                Console.WriteLine("  · " + it.Item);
            }
            Console.WriteLine("可调 " + (items.Count - readonlyCount) + " 项 / 只读 " + readonlyCount + " 项 / 共 " + items.Count + " 项");
        }

        static string Raw(int v)
        {
            if (v == -1) return "0xFFFFFFFF";
            if (v < 0) return "0x" + ((uint)v).ToString("X8");
            if (v > 0xFFFF) return "0x" + v.ToString("X8");
            return v.ToString();
        }

        static string Where(string apply)
        {
            switch (apply)
            {
                case "drv": return "NVIDIA 驱动库 nvdrsdb（重启游戏生效）";
                case "hags": return "注册表 HwSchMode";
                case "sched": return "注册表 Win32PrioritySeparation";
                case "proc": return "powercfg 处理器状态";
                case "net": return "注册表 MMCSS 网络参数";
            }
            return apply;
        }
    }
}
