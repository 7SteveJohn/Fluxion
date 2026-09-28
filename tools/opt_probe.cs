// 优化项补全的只读验证（3.1.0）。
// 原则：能只读就只读；唯一会写盘的用例（驱动基线）在结束时**把原状恢复回去**。
namespace Fluxion
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    static class OptProbe
    {
        static int Pass = 0, Fail = 0;
        static readonly List<string> Log = new List<string>();

        static void L(string s) { Log.Add(s); Console.WriteLine(s); }

        static void Chk(string what, bool ok, string extra)
        {
            if (ok) Pass++; else Fail++;
            L((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(extra) ? "" : "   → " + extra));
        }

        static int Main()
        {
            L("=== 优化项补全验证（3.1.0）===");
            try { Program.Cfg = Config.Load(Program.ConfigPath); } catch { }
            L("");

            // ---------------- ① 刷新率读取 ----------------
            L("=== 1. 显示器刷新率（新增能力）===");
            int w, h, hz;
            bool okMode = Program.DisplayMode(out w, out h, out hz);
            L("  DisplayMode = " + okMode + "   " + w + "x" + h + " @" + hz + "Hz");
            int rhz = Program.RefreshHz();
            L("  RefreshHz() = " + rhz);
            Chk("DisplayMode 读到了分辨率", okMode && w > 0 && h > 0, w + "x" + h);
            Chk("RefreshHz() > 0（刷新率可读）", rhz > 0, rhz.ToString());
            Chk("RefreshHz() 与 DisplayMode 一致", rhz == hz, rhz + " vs " + hz);
            Chk("分辨率与刷新率都在合理范围", w >= 640 && w <= 16384 && h >= 480 && rhz >= 24 && rhz <= 1000,
                w + "x" + h + "@" + rhz);
            L("");

            // ---------------- ② 体检表 ----------------
            L("=== 2. 体检表（原 22 项 + 新增 9 项）===");
            List<StatusItem> items = null;
            try { items = Program.GetStatusItems(); }
            catch (Exception ex) { L("  GetStatusItems 抛异常: " + ex.GetType().Name + " " + ex.Message); }
            Chk("GetStatusItems 不抛异常", items != null, items == null ? "null" : "ok");
            if (items == null) { Dump(); return Fail; }

            L("  实际项数 = " + items.Count);
            Chk("项数 ≥ 30（22 原有 + 9 新增）", items.Count >= 30, items.Count.ToString());

            int empty = 0;
            foreach (StatusItem it in items)
            {
                if (string.IsNullOrEmpty(it.Item) || string.IsNullOrEmpty(it.Current)
                    || string.IsNullOrEmpty(it.Expected) || string.IsNullOrEmpty(it.Status)) empty++;
            }
            Chk("每项的 名称/当前/期望/状态 四列都非空", empty == 0, "空列 " + empty + " 项");

            string[] want = new string[] {
                "显示器刷新率", "鼠标加速", "禁止内核换页", "网卡节能", "已禁用服务数",
                "全屏优化", "进程电源节流", "优化模块", "NVIDIA 驱动版本"
            };
            foreach (string k in want)
            {
                bool found = false;
                string cur = "";
                foreach (StatusItem it in items) if (it.Item != null && it.Item.IndexOf(k) >= 0) { found = true; cur = it.Current; break; }
                Chk("新增行存在：" + k, found, found ? cur : "缺失");
            }
            L("");

            // 不变式：新增行的状态必须以 ✅ 或 ⚠️ 开头。
            //  原因：Ui.cs 把"非 ✅ 非 ⚠️"一律显示成红色「需处理」并计入「未生效」→
            //  用 🟡 表示"用户主动停用某个模块"会造成**永久红色误报 + 托盘告警**。
            //  这一轮我自己先踩了这个坑，所以把它固化成断言。
            L("=== 2b. 新增行的状态取值必须落在 ✅/⚠️（否则会变红色『需处理』）===");
            foreach (string k in want)
            {
                foreach (StatusItem it in items)
                {
                    if (it.Item != null && it.Item.IndexOf(k) >= 0)
                    {
                        string st = it.Status ?? "";
                        Chk("状态取值安全：" + k, st.StartsWith("✅") || st.StartsWith("⚠️"), st);
                        break;
                    }
                }
            }
            L("");

            // ---------------- ③ 倍率建议 ----------------
            L("=== 3. 按刷新率的倍率建议 ===");
            string hint = Dlssg.RefreshHint();
            L("  RefreshHint() = " + hint);
            Chk("提示非空（读到了刷新率）", hint.Length > 0, hint);
            Chk("提示里带本机刷新率数字", hint.IndexOf(rhz.ToString() + "Hz") >= 0, hint);
            Chk("提示里给出 6X 的基础帧门槛 = hz/6", hint.IndexOf("≤" + (rhz / 6)) >= 0, "期望 ≤" + (rhz / 6));
            Chk("提示里给出基础帧 40 的结论倍率 " + Math.Min(6, Math.Max(2, rhz / 40)) + "X",
                hint.IndexOf(Math.Min(6, Math.Max(2, rhz / 40)) + "X") >= 0, hint);
            L("");

            // ---------------- ④ 驱动版本基线（写盘用例，结束时恢复）----------------
            L("=== 4. NVIDIA 驱动版本比对 ===");
            string now = Program.DriverVerNow();
            L("  DriverVerNow() = " + now);
            Chk("能读到 NVIDIA 驱动版本号", now.Length > 0, now);

            // 沙盒：把基线文件指到 %TEMP%，**不碰数据目录里的真实基线**。
            //  两个理由：① 那是用户的真实数据；② 本机该目录对普通权限只允许新建、不允许覆盖
            //  （实测覆盖 Errno 13），探针写不动真实路径 —— 那是环境限制，不该记成 FAIL。
            string sbx = Path.Combine(Path.GetTempPath(), "gb_drv_test");
            try { if (Directory.Exists(sbx)) Directory.Delete(sbx, true); } catch { }
            Directory.CreateDirectory(sbx);
            string f = Path.Combine(sbx, "nv-driver.txt");
            string realF = Path.Combine(Program.DataDir, "nv-driver.txt");
            string realBefore = File.Exists(realF) ? File.ReadAllText(realF, Encoding.UTF8).Trim() : "(不存在)";
            Program.DriverFileOverride = f;
            L("  沙盒基线 = " + f);
            L("  真实基线 = " + realBefore + "（本次测试不会改动它）");
            try
            {
                string a = Program.MarkDriverBaseline();                 // 首次：无基线 → 不应报"更新"
                string content = File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8).Trim() : "";
                Chk("首次记录不误报『已更新』", a.Length == 0, a);
                Chk("基线文件已写入当前版本", content == now, content);

                string b = Program.MarkDriverBaseline();                 // 再调一次：版本没变 → 仍不报
                Chk("重复调用保持幂等（不误报）", b.Length == 0, b);

                File.WriteAllText(f, "1.00", new UTF8Encoding(false));   // 造"旧版本"
                string c = Program.MarkDriverBaseline();
                Chk("版本变化时给出提示", c.Length > 0, c);
                Chk("提示里含旧值 1.00 与新值 " + now, c.IndexOf("1.00") >= 0 && c.IndexOf(now) >= 0, c);
                Chk("提示里说明原因（着色器缓存）", c.IndexOf("着色器缓存") >= 0, c);

                string realAfter = File.Exists(realF) ? File.ReadAllText(realF, Encoding.UTF8).Trim() : "(不存在)";
                Chk("★ 真实数据目录里的基线未被本次测试改动", realAfter == realBefore,
                    realBefore + " → " + realAfter);
            }
            catch (Exception ex) { Chk("驱动基线往返测试", false, ex.Message); }
            finally
            {
                Program.DriverFileOverride = null;                       // 复位，避免影响后续断言
                try { Directory.Delete(sbx, true); } catch { }
                L("  沙盒已清理，override 已复位");
            }
            L("");

            // ---------------- ⑤ 新增写入项（只读现状，不做破坏性写）----------------
            L("=== 5. 新增的两项系统写入（只读检查当前值）===");
            L("  说明：这两项的真实写入要管理员权限且会改用户系统，探针**只读不写**。");
            foreach (string k in want)
                foreach (StatusItem it in items)
                    if (it.Item != null && it.Item.IndexOf(k) >= 0 && (k == "全屏优化" || k == "进程电源节流"))
                        L("    " + it.Item + " | 当前=" + it.Current + " | 期望=" + it.Expected + " | " + it.Status);

            Dump();
            return Fail;
        }

        static void Dump()
        {
            L("");
            L("=== 汇总：PASS " + Pass + " / FAIL " + Fail + " ===");
            L("=== 6. 驱动分档结论（NoteDriver）===");
            // 2026-09-16 纠正：上游的 ≥616.56 只对 RTX 50 是硬门槛；对 20/30/40 系不是。
            //  真正要避开的是 ≥616.64（NR 每次 evaluate 都在 NVIDIA 自家 NGX 运行时 fault）。
            //  这四档结论直接决定"要不要劝用户升级/回退驱动"，必须逐档断言。
            Chk("低于 591.86 → 判为不达标（提示先升级）",
                Dlssg030.NoteDriver(560.00).IndexOf("低于作者要求") >= 0, Dlssg030.NoteDriver(560.00));
            Chk("591.86~616.55 的正常区 → 明确说‘不必为了 NR 升级’",
                Dlssg030.NoteDriver(610.88).IndexOf("不必为了 NR 升级") >= 0, Dlssg030.NoteDriver(610.88));
            Chk("  且不带任何‘请升级/回退’的指令",
                Dlssg030.NoteDriver(610.88).IndexOf("先升级") < 0
                && Dlssg030.NoteDriver(610.88).IndexOf("回退") < 0, Dlssg030.NoteDriver(610.88));
            Chk("616.56 → 判为唯一达标且 NR 能跑完",
                Dlssg030.NoteDriver(616.56).IndexOf("616.56：目前唯一") >= 0, Dlssg030.NoteDriver(616.56));
            Chk("≥616.64（616.64）→ 警告 NR 会 fault 并建议回退 616.56",
                Dlssg030.NoteDriver(616.64).IndexOf("fault") >= 0
                && Dlssg030.NoteDriver(616.64).IndexOf("回退到 616.56") >= 0, Dlssg030.NoteDriver(616.64));
            Chk("≥616.64（616.92 最新）→ 同样警告",
                Dlssg030.NoteDriver(616.92).IndexOf("fault") >= 0, Dlssg030.NoteDriver(616.92));
            L("");

            L(Fail == 0 ? "ALL PASS" : (Fail + " 项失败"));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "opt_probe.txt"),
                    string.Join("\r\n", Log.ToArray()), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
