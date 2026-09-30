// ============================================================================
//  Fluxion · 界面层（简约浅色）
//  ---------------------------------------------------------------------------
//  第三版（2026-09-11）：换掉"全自绘深色 + 构建完后整体缩放"的路线。
//
//  为什么换：深色主题下系统原生控件（复选框、下拉框、滚动条、提示框）永远是浅色皮肤，
//  只能一个一个自绘去盖；盖不干净就表现为"色准不一致"，而自绘控件越多，越容易出现
//  文字压字、控件越界、缩放后错位。浅色主题反过来：原生控件本来就是正确外观。
//
//  新路线四条硬规则：
//    1. 只用原生控件：Label / ComboBox / TextBox / CheckBox / Button / ListView /
//       RichTextBox / FlowLayoutPanel / TableLayoutPanel。自绘只剩三个小组件
//       （扁平按钮、进度条、曲线），且全部只在自己边界内绘制。
//    2. 纵向位置一律由【上一个控件的实测高度】推导（Sec 里的光标式布局），
//       没有一个写死的 y 坐标 —— 改字号、换 DPI、改文案都不可能重叠或被裁切。
//    3. DPI 只换算一次：固定像素尺寸（行高/间距/列宽/列表高度）在创建时走 Theme.S()，
//       不做"构建完成后再整体放大"的后处理（那会让两套坐标系打架）。
//    4. 用系统标题栏，不做自绘标题栏 —— 省掉拖动、圆角 Region、最小化按钮三处高风险自绘。
//
//  功能与传统版完全一致：托盘、游戏联动、硬件告警、漂移检测、帧生成接入与诊断。
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Drawing.Text;

namespace Fluxion
{
    // ======================= 主题：浅色极简 =======================
    //  底色浅灰、卡片纯白、1px 浅描边、一处主色。层级靠"白卡浮在灰底上"表达，
    //  不靠明暗渐变 —— 这就是"简约"的实现方式，也顺带避免了深色下的对比度问题。
    static class Theme
    {
        // ====================================================================
        //  调色板（2026-09-15 v2.10.0 · 复刻 DLSS 5 Swapper 的界面语言）
        //  ------------------------------------------------------------------
        //  来源：D:\DLSS5\DLSS 5 Swapper 的 src/renderer/style.css，把它
        //  :root[data-theme=light] / [data-theme=dark] 两套变量逐项对到本类上。
        //  三条约定的原文：
        //   · 强调色 = 柠檬绿 #6CC10A（浅深共用同一色相，不是两套颜色）；
        //   · 深色底是"近黑 + 每层抬一级"：#05070A → #0C1016 → #12171F，
        //     原注释吐槽过"整屏压在一个灰蓝平面上，什么都看不出层级"；
        //   · 圆角 卡片/分区 18、按钮/输入 12（--radius / --radius-sm）。
        //  ⚠ 必须在构造界面前设定：部分控件（Bar/Spark 等）在构造函数里就把颜色
        //    抄进了自己的字段，之后再切主题它们不会跟着变。
        // ====================================================================
        public static int Mode = 0;                    // 0=浅色 1=深色
        public static void SetMode(string s)
        {
            Mode = (s != null && s.Trim().ToLowerInvariant() == "dark") ? 1 : 0;
        }
        public static bool Dark { get { return Mode == 1; } }
        static Color L(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static Color Pk(Color light, Color dark) { return Mode == 1 ? dark : light; }

        public static Color Bg { get { return Pk(L(245, 247, 250), L(11, 15, 23)); } }            // bg-1: 高阶曜石黑 #0B0F17
        public static Color Panel { get { return Pk(Color.White, L(19, 27, 38)); } }            // panel: 深空钛灰 #131B26
        public static Color Subtle { get { return Pk(L(248, 250, 252), L(14, 20, 29)); } }      // bg-3: #0E141D
        public static Color Line { get { return Pk(L(226, 232, 240), L(30, 41, 59)); } }        // stroke: #1E293B
        public static Color LineSoft { get { return Pk(L(241, 245, 249), L(26, 36, 52)); } }    // line: #1A2434
        public static Color Hover { get { return Pk(L(237, 242, 247), L(24, 34, 48)); } }       // panel-2: #182230
        public static Color Accent { get { return Pk(L(16, 185, 129), L(0, 220, 130)); } }     // #00DC82 (极光绿/翡翠绿)
        public static Color Accent2 { get { return Pk(L(5, 150, 105), L(56, 189, 248)); } }       // 天青蓝 #38BDF8
        public static Color Cyan { get { return Pk(L(14, 165, 233), L(56, 189, 248)); } }       // #38BDF8
        public static Color AccentSoft { get { return Pk(L(209, 250, 229), L(16, 42, 36)); } }  // accent-soft: 柔光翡翠
        // ---- 控件级派生色：以前散落在各绘制函数里的硬编码值，收进 Theme 以便换配色时一处生效。
        //      取值与原硬编码**逐位相同**（零视觉变化），只是不再散在外面。
        public static Color OkFill { get { return Pk(L(209, 250, 229), L(18, 32, 28)); } }    // 成功态按钮底：浅色=OkSoft，深色=更深的墨绿
        public static Color ErrFill { get { return Pk(L(255, 228, 230), L(36, 18, 24)); } }   // 危险态按钮底：浅色=ErrSoft，深色=更深的暗红
        public static Color CheckOn { get { return Pk(L(235, 252, 245), L(16, 42, 36)); } }  // 勾选圆底：浅色=近白极淡绿，深色=AccentSoft
        public static Color GridHead { get { return Pk(L(248, 250, 252), L(16, 22, 32)); } } // ListView 表头底
        public static Color GridAlt { get { return Pk(L(248, 250, 252), L(14, 20, 28)); } }  // ListView 斑马纹底
        public static Color Ok { get { return Pk(L(16, 185, 129), L(16, 185, 129)); } }         // #10B981
        public static Color Warn { get { return Pk(L(217, 119, 6), L(245, 158, 11)); } }        // #F59E0B
        public static Color Err { get { return Pk(L(225, 29, 72), L(244, 63, 94)); } }          // #F43F5E
        public static Color Text { get { return Pk(L(15, 23, 42), L(241, 245, 249)); } }        // #F1F5F9 (slate-100)
        public static Color TextDim { get { return Pk(L(71, 85, 105), L(148, 163, 184)); } }    // #94A3B8 (slate-400)
        public static Color TextFaint { get { return Pk(L(148, 163, 184), L(100, 116, 139)); } } // #64748B (slate-500)
        // 浅色下输入底用极浅灰
        public static Color Field { get { return Pk(L(241, 245, 249), L(14, 20, 29)); } }       // #0E141D
        public static Color FieldEdge { get { return Pk(L(203, 213, 225), L(34, 45, 62)); } }   // #222D3E
        public static Color Selected { get { return Pk(L(209, 250, 229), L(16, 42, 36)); } }
        public static Color OnAccent { get { return Color.White; } }
        public static Color ShadowC { get { return Pk(L(148, 163, 184), L(0, 0, 0)); } }
        public static Color Golden { get { return Pk(L(217, 119, 6), L(251, 191, 36)); } }     // #FBBF24
        // 分区色相（2026-09-20）：卡片标题左的图标胶囊不再清一色绿，
        //   按分区语义分配色相，一屏 5~6 张卡片才有颜色节奏（用户："界面还是很单调"）。
        public static Color Violet { get { return Pk(L(124, 92, 246), L(167, 139, 250)); } }
        public static Color Indigo { get { return Pk(L(79, 70, 229), L(129, 140, 248)); } }
        // 胶囊底 / 瓦片底：把色相以低透明度压在卡片底色上（浅色更淡、深色稍浓）
        public static Color ChipBg(Color hue) { return Color.FromArgb(Dark ? 46 : 26, hue); }
        public static Color TileBg(Color hue) { return Color.FromArgb(Dark ? 58 : 34, hue); }

        public static Color AccentDeep { get { return Pk(L(4, 120, 87), L(0, 180, 105)); } }
        public static Color AccentLite { get { return Pk(L(52, 211, 153), L(52, 235, 150)); } }
        public static Color OkSoft { get { return Pk(L(209, 250, 229), L(16, 42, 36)); } }     // rgba(16, 185, 129, 0.15)
        public static Color ErrSoft { get { return Pk(L(255, 228, 230), L(48, 20, 26)); } }    // rgba(244, 63, 94, 0.15)
        public static Color WarnSoft { get { return Pk(L(254, 243, 199), L(48, 36, 18)); } }   // rgba(245, 158, 11, 0.15)

        // ---- 圆角 token ----
        public static int RCard { get { return S(16); } }
        public static int RPanel { get { return S(16); } }
        public static int RBtn { get { return S(10); } }
        public static int RField { get { return S(10); } }
        public static int RChip { get { return S(8); } }
        public static int RPic { get { return S(14); } }


        // ---- 圆角绘制工具 ----
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var gp = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return gp;
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 2) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            using (var gp = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, gp);
        }
        public static void FillRound(Graphics g, Rectangle r, int rad, Brush b)
        {
            if (b == null || r.Width <= 0 || r.Height <= 0) return;
            using (var gp = Round(r, rad)) g.FillPath(b, gp);
        }
        public static void EdgeRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            if (c.A == 0) return;
            using (var gp = Round(r, rad)) using (var p = new Pen(c, w)) g.DrawPath(p, gp);
        }

        // ---- 高频绘制缓存（2026-09-20）----
        // 列表快速滚动时每行每格都走自绘回调，若每次都 new SolidBrush/Pen 再 Dispose，
        // 一帧就是几十次 GDI+ 对象分配 —— 背景铺完了文字还没画出来，表现就是
        // "文字不显示或显示过慢、很割裂"（用户截图反馈）。画刷/画笔按颜色缓存、只建一次、
        // 永不 Dispose（生命周期 = 进程生命周期；主题切换走"重建控件树"，缓存按 ARGB
        // 取值，旧主题的颜色条目留在表里无害）。
        static readonly Dictionary<int, SolidBrush> brushCache = new Dictionary<int, SolidBrush>();
        // ★ 缓存存活探测（2026-09-20 事故后加的保底）。
        //   背景：缓存交出的是共享实例。若有人误写 `using (var x = Theme.Solid(...))`，
        //   实例被释放、而字典里留下尸体 —— 此后每次绘制都在已销毁的 GDI+ 句柄上作图，
        //   抛 ArgumentException「参数无效」并**中断整页绘制**（用户侧：滚动/切页后内容整片消失、
        //   屏幕留下红色绘制失败标记），而且"延迟发作"：第一次用完全正常，之后全崩，极难排查。
        //   做法：反射读 GDI+ 对象内部的 native 句柄字段（Dispose 时会被置 null），
        //   尸体直接丢弃重建 —— 误用退化成"一次重建"，而不会让整页再也画不出来。
        //   ⚠ 试过并被否掉的两条路：
        //     ① 读 `SolidBrush.Color` 探测死活 —— 不行：它会缓存颜色值（托管字段），
        //        在尸体上读不抛异常，探测不出来（实测）。
        //     ② 继承 SolidBrush / Pen 覆盖 `Dispose(bool)` —— 不行：两个类型都是 sealed
        //        （CS0509，实测编译报错）。
        //   另配一道 static_check.py 的 CACHED-OBJ-USING 规则禁止这种写法。
        static readonly System.Reflection.FieldInfo brushNative =
            typeof(Brush).GetField("nativeBrush", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo penNative =
            typeof(Pen).GetField("nativePen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // ⚠ 该字段类型是 **IntPtr**（不是 SafeHandle）：Dispose 后它被置成 IntPtr.Zero，
        //   而不是 null —— 所以判据必须是 `!= IntPtr.Zero`；写成 `!= null` 会恒真、探测静默失效
        //   （2026-09-20 实测踩过：dump 出来 dispose 前后都"非 null"，实为零句柄）。
        static bool HandleAlive(object v)
        {
            if (v is IntPtr) return (IntPtr)v != IntPtr.Zero;
            return v != null;
        }
        static bool Alive(Brush b)
        {
            if (brushNative == null) return true;          // 拿不到内部字段 → 放弃检测（绝不误杀真对象）
            try { return HandleAlive(brushNative.GetValue(b)); } catch { return true; }
        }
        static bool Alive(Pen p)
        {
            if (penNative == null) return true;
            try { return HandleAlive(penNative.GetValue(p)); } catch { return true; }
        }

        public static SolidBrush Solid(Color c)
        {
            int k = c.ToArgb();
            SolidBrush b;
            if (!brushCache.TryGetValue(k, out b) || !Alive(b))
            {
                b = new SolidBrush(c);
                brushCache[k] = b;
            }
            return b;
        }
        static readonly Dictionary<int, Pen> penCache = new Dictionary<int, Pen>();
        public static Pen Hair(Color c, float w)
        {
            int k = c.ToArgb() ^ (int)(w * 16f);
            Pen p;
            if (!penCache.TryGetValue(k, out p) || !Alive(p))
            {
                p = new Pen(c, w);
                penCache[k] = p;
            }
            return p;
        }
        public static Pen Hair(Color c) { return Hair(c, 1f); }
        // 胶囊文字宽也按文案缓存（MeasureText 本身不便宜，滚动时每帧对同一文案反复测量纯属浪费）
        static readonly Dictionary<string, int> textWCache = new Dictionary<string, int>();
        public static int TextW(string txt, Font f)
        {
            int w;
            if (!textWCache.TryGetValue(f.FontFamily.Name + f.Size + "|" + txt, out w))
            {
                w = TextRenderer.MeasureText(txt, f, new Size(1000, 100), TextFormatFlags.NoPrefix).Width;
                textWCache[f.FontFamily.Name + f.Size + "|" + txt] = w;
            }
            return w;
        }
        // 柔和投影：用几层带 alpha 的圆角矩形由内向外叠加
        public static void SoftShadow(Graphics g, Rectangle r, int rad, int depth)
        {
            if (!Dark && depth <= 0) return;
            for (int i = depth; i >= 1; i--)
            {
                int a = (int)(9.0 * (depth - i + 1) / depth);      // 外圈最淡
                if (a <= 0) continue;
                var rr = new Rectangle(r.X - i, r.Y - i + S(2), r.Width + i * 2, r.Height + i * 2);
                FillRound(g, rr, rad + i, Color.FromArgb(a, ShadowC));
            }
        }
        // 强调色渐变（深色主题下用极光绿→天青蓝；浅色下用同色系保证一致观感）
        public static LinearGradientBrush AccentGrad(Rectangle r)
        {
            return new LinearGradientBrush(r, Accent, Dark ? Cyan : Accent2, 28f);
        }

        // ---- 两个色块之间的"消融"过渡（2026-09-20 二稿）----
        //  需求原话："色块颜色变化还是不够丝滑"。
        //  上一版是页头最后 28%（约 17px）做白→灰**线性**落下 —— 问题是：
        //   ① 17px 在 780px 高的窗口里太短，看着是"糊了一小段"而不是渐变；
        //   ② 线性渐变的**起点那一行**色阶突然开始变化，眼睛能抓到一条"起跑线"。
        //  改法：跨页头**整个高度**做 smoothstep（两端慢、中间快），并在 brush 里
        //  用 9 个采样停点近似这条曲线 —— 起点/终点都看不出"从哪一行开始变"。
        // 线性混色（**不做**缓动）—— 状态过渡专用：缓动交给 Anim 的曲线，这里只负责按比例叠色。
        //   与 Fade() 的唯一区别就是少了那个 smoothstep：Fade 是给"渐变带"用的，Mix 是给"状态之间"用的，
        //   两者混用会让补间的曲线被缓动两次（先 smoothstep 再 cubic-bezier ⇒ 手感发黏）。
        public static Color Mix(Color a, Color b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // 按比例缩放颜色的 alpha —— 用于淡入/淡出。⚠ 调用方必须**先铺不透明底色**：
        //   WinForms 子窗口之间没有合成器，半透明画在未初始化的 DC 上会花。
        public static Color Alpha(Color c, double t)
        {
            if (t <= 0) return Color.FromArgb(0, c);
            if (t >= 1) return c;
            return Color.FromArgb((int)Math.Round(c.A * t), c);
        }

        public static Color Fade(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            t = t * t * (3f - 2f * t);                       // smoothstep
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
        // 竖直缓动渐变（top→bottom，smoothstep 采样 stops 个停点）
        public static LinearGradientBrush VFade(Rectangle r, Color top, Color bot, int stops)
        {
            var br = new LinearGradientBrush(r, top, bot, 90f);
            if (stops >= 2)
            {
                var cb = new ColorBlend(stops);
                var cs = new Color[stops];
                var ps = new float[stops];
                for (int i = 0; i < stops; i++)
                {
                    float t = stops == 1 ? 0f : (float)i / (stops - 1);
                    ps[i] = t;
                    cs[i] = Fade(top, bot, t);
                }
                cb.Colors = cs;
                cb.Positions = ps;
                br.InterpolationColors = cb;
            }
            return br;
        }
        // ---- 矢量电竞级图标绘制工具 ----
        public static void DrawIconSpeedometer(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(c, 1.8f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                var arcR = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                g.DrawArc(p, arcR, 140, 260);
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f + 1f;
                float tx = cx + (r.Width * 0.32f), ty = cy - (r.Height * 0.32f);
                g.DrawLine(p, cx, cy, tx, ty);
            }
            using (var b = new SolidBrush(c))
            {
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f + 1f;
                g.FillEllipse(b, cx - 2f, cy - 2f, 4f, 4f);
            }
        }

        public static void DrawIconLightning(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            PointF[] pts = new PointF[] {
                new PointF(x + w * 0.55f, y + h * 0.05f),
                new PointF(x + w * 0.18f, y + h * 0.52f),
                new PointF(x + w * 0.48f, y + h * 0.52f),
                new PointF(x + w * 0.38f, y + h * 0.95f),
                new PointF(x + w * 0.82f, y + h * 0.44f),
                new PointF(x + w * 0.52f, y + h * 0.44f)
            };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        public static void DrawIconChip(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float pad = w * 0.22f;
            var core = new RectangleF(x + pad, y + pad, w - pad * 2, h - pad * 2);
            using (var p = new Pen(c, 1.5f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawRectangle(p, core.X, core.Y, core.Width, core.Height);
                float m1 = 0.38f, m2 = 0.62f;
                g.DrawLine(p, x + w * m1, y, x + w * m1, core.Top);
                g.DrawLine(p, x + w * m2, y, x + w * m2, core.Top);
                g.DrawLine(p, x + w * m1, core.Bottom, x + w * m1, y + h);
                g.DrawLine(p, x + w * m2, core.Bottom, x + w * m2, y + h);
                g.DrawLine(p, x, y + h * m1, core.Left, y + h * m1);
                g.DrawLine(p, x, y + h * m2, core.Left, y + h * m2);
                g.DrawLine(p, core.Right, y + h * m1, x + w, y + h * m1);
                g.DrawLine(p, core.Right, y + h * m2, x + w, y + h * m2);
            }
            using (var b = new SolidBrush(c))
            {
                float cw = core.Width * 0.36f;
                g.FillRectangle(b, core.X + (core.Width - cw) / 2f, core.Y + (core.Height - cw) / 2f, cw, cw);
            }
        }

        public static void DrawIconGamepad(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            var body = new RectangleF(x + 1, y + h * 0.2f, w - 2, h * 0.6f);
            using (var p = new Pen(c, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                using (var gp = Round(new Rectangle((int)body.X, (int)body.Y, (int)body.Width, (int)body.Height), (int)(body.Height * 0.45f)))
                    g.DrawPath(p, gp);
                float lx = x + w * 0.28f, ly = y + h * 0.5f;
                g.DrawLine(p, lx - 3, ly, lx + 3, ly);
                g.DrawLine(p, lx, ly - 3, lx, ly + 3);
            }
            using (var b = new SolidBrush(c))
            {
                float rx = x + w * 0.72f, ry = y + h * 0.5f;
                g.FillEllipse(b, rx - 1.5f, ry - 3.5f, 3f, 3f);
                g.FillEllipse(b, rx - 3.5f, ry - 0.5f, 3f, 3f);
                g.FillEllipse(b, rx + 1f, ry - 0.5f, 3f, 3f);
                g.FillEllipse(b, rx - 1.5f, ry + 2f, 3f, 3f);
            }
        }

        public static void DrawIconPulse(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float cy = y + h * 0.5f;
            PointF[] pts = new PointF[] {
                new PointF(x, cy),
                new PointF(x + w * 0.25f, cy),
                new PointF(x + w * 0.38f, y + h * 0.15f),
                new PointF(x + w * 0.55f, y + h * 0.85f),
                new PointF(x + w * 0.68f, cy - 2),
                new PointF(x + w * 0.78f, cy),
                new PointF(x + w, cy)
            };
            using (var p = new Pen(c, 1.7f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }
        }

        public static void DrawIconBook(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X + 1, y = r.Y + 2, w = r.Width - 2, h = r.Height - 4;
            using (var p = new Pen(c, 1.5f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float mx = x + w / 2f;
                g.DrawLine(p, mx, y + 2, mx, y + h);
                g.DrawArc(p, x, y, w / 2f, 4, 180, 180);
                g.DrawLine(p, x, y + 2, x, y + h);
                g.DrawLine(p, x, y + h, mx, y + h - 1);
                g.DrawArc(p, mx, y, w / 2f, 4, 180, 180);
                g.DrawLine(p, x + w, y + 2, x + w, y + h);
                g.DrawLine(p, mx, y + h - 1, x + w, y + h);
            }
        }

        public static void DrawIconGear(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float outR = Math.Min(r.Width, r.Height) * 0.42f;
            float inR = outR * 0.42f;
            using (var p = new Pen(c, 1.8f))
            {
                g.DrawEllipse(p, cx - outR, cy - outR, outR * 2, outR * 2);
                g.DrawEllipse(p, cx - inR, cy - inR, inR * 2, inR * 2);
                for (int deg = 0; deg < 180; deg += 45)
                {
                    double rad = deg * Math.PI / 180.0;
                    float dx = (float)(Math.Cos(rad) * (outR + 2.5f));
                    float dy = (float)(Math.Sin(rad) * (outR + 2.5f));
                    g.DrawLine(p, cx - dx, cy - dy, cx + dx, cy + dy);
                }
            }
        }

        public static void DrawBrandLogo(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            PointF[] hex = new PointF[] {
                new PointF(x + w * 0.5f, y + 1f),
                new PointF(x + w * 0.94f, y + h * 0.25f),
                new PointF(x + w * 0.94f, y + h * 0.75f),
                new PointF(x + w * 0.5f, y + h - 1f),
                new PointF(x + w * 0.06f, y + h * 0.75f),
                new PointF(x + w * 0.06f, y + h * 0.25f)
            };
            using (var bBg = new SolidBrush(Dark ? Color.FromArgb(28, Accent) : Color.FromArgb(16, Accent)))
                g.FillPolygon(bBg, hex);
            using (var br = AccentGrad(r))
            using (var p = new Pen(br, 1.8f))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawPolygon(p, hex);
            }
            PointF[] bolt = new PointF[] {
                new PointF(x + w * 0.54f, y + h * 0.18f),
                new PointF(x + w * 0.30f, y + h * 0.52f),
                new PointF(x + w * 0.48f, y + h * 0.52f),
                new PointF(x + w * 0.42f, y + h * 0.82f),
                new PointF(x + w * 0.70f, y + h * 0.46f),
                new PointF(x + w * 0.52f, y + h * 0.46f)
            };
            using (var br = AccentGrad(r))
            {
                g.FillPolygon(br, bolt);
            }
        }

        // ---- Windows 11 窗口外观（圆角 + 标题栏随主题）----
        //  DWMWA_WINDOW_CORNER_PREFERENCE=33 / BORDER_COLOR=34 / CAPTION_COLOR=35 / TEXT_COLOR=36
        //  DWMWA_USE_IMMERSIVE_DARK_MODE=20。均需 Win11 22000+，旧系统调用失败会被吞掉（外观回退为系统默认）。
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void ApplyChrome(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int round = 2;                                   // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                int dark = Dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                // ⚠ 标题栏色必须与**侧栏 Panel**（第一眼看到的窗口主体）一致，不能沿用 Bg(F5F7FA)：
                //   F5F7FA 是冷蓝白，和纯白侧栏之间有一条横贯窗口的分界（用户原话"两块颜色太割裂"）。
                //   浅色=纯白与侧栏无缝；深色 131B26 本来就等于 Panel，不动。
                int cap = Win32(Dark ? L(19, 27, 38) : L(255, 255, 255));
                DwmSetWindowAttribute(hwnd, 35, ref cap, 4);
                int txt = Win32(Dark ? L(241, 245, 249) : L(15, 23, 42));
                DwmSetWindowAttribute(hwnd, 36, ref txt, 4);
                int brd = Win32(Dark ? L(30, 41, 59) : L(226, 232, 240));
                DwmSetWindowAttribute(hwnd, 34, ref brd, 4);
            }
            catch { }
        }
        // ---- 深色模式下的原生滚动条 / ListView 表头 ----
        //  不做的话深色页面右缘会留一条白色滚动条（v2.7.1 深色设置页实测）。
        //  Win10 1809+ 的 "DarkMode_Explorer" 主题可把 Panel/ListView/RichTextBox
        //  的滚动条和 ListView 表头一起染黑；旧系统调用失败静默回退。
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ★ 强制"整棵子树立刻重画"的唯一可靠手段（2026-09-20）：
        //   WinForms 的 Control.Invalidate() 默认 **不重绘子控件**；Update() 只同步自己那个 HWND。
        //   而页面滚动是把分区/列表这些**真实窗口**搬来搬去（见 Pg.LayoutOnce），子控件的像素
        //   全靠 OS 搬窗口时顺手带过去 —— 只要有一次没带全，就留下"背景刷了、文字没画"的死区，
        //   而且会一直保留（用户截图：某行只剩名称列 / 整片空白）。RDW_ALLCHILDREN 把子孙一起算上，
        //   RDW_UPDATENOW 立刻上屏（不等 WM_PAINT 排队，滚动时轮询消息会把绘制饿死）。
        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRgn, uint flags);
        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ALLCHILDREN = 0x0080;
        const uint RDW_UPDATENOW = 0x0100;

        public static void HardRepaint(Control c)
        {
            if (c == null) return;
            try
            {
                if (!c.IsHandleCreated) return;
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch { }
        }

        const int LVM_GETHEADER = 0x101F;
        const int LVM_GETTOPINDEX = 0x102C;
        const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;
        const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        // ---- 列宽一键复原（v3.9.0）----
        // 用户 2026-09-20：「这个栏在还能自动调节，但为啥没有一键复原的选项」——
        //   说的就是表头列的宽度：能拖（也能双击分隔线自适应），拖乱了却没有回默认的口子。
        // 每个表建表时把"默认宽度"记进 WeakTable（不用句柄做键 —— NewList 时句柄往往还没创建；
        //   也不占 Control.Tag —— 那个位置随时可能被别处拿去用），右键菜单给两项复原入口。
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListView, int[]> listDefaults
            = new System.Runtime.CompilerServices.ConditionalWeakTable<ListView, int[]>();

        public static void RememberWidths(ListView lv, int[] w)
        {
            if (lv == null || w == null) return;
            try { listDefaults.Remove(lv); } catch { }
            try { listDefaults.Add(lv, w); } catch { }
        }

        public static void ResetListWidths(ListView lv)
        {
            if (lv == null || lv.IsDisposed) return;
            int[] w;
            if (!listDefaults.TryGetValue(lv, out w)) return;
            try
            {
                lv.BeginUpdate();
                for (int i = 0; i < lv.Columns.Count && i < w.Length; i++) lv.Columns[i].Width = w[i];
                lv.EndUpdate();
            }
            catch { }
            // 末列的"默认"不是那个建表名义值，而是"填满剩余宽度"（列表自己 Resize 时也是这么做的）——
            // 不补这一句，复原后末列会停在一个和用户建表时看到的不同的宽度上。
            if (w.Length > 0) FillLastColumn(lv, w[w.Length - 1]);
        }

        // 末列"填满剩余宽度"。minW 只是"尽量留够"的偏好，不能拿它撑破客户区：
        //   一旦出现横向滚动条，客户区高度会同时被压掉一行，而 Resize 又会再跑一次本函数。
        //   2026-09-20 实测：lvOpt 前三列 596 + minW 160 = 756 > 客户区 745 ——
        //   11px 的溢出既多出一条横向滚动条，又白丢一行可见行。
        // 放在 Theme（而不是 MainForm）里：ResetListWidths 也要用它 ——
        //   末列的"默认"就是"填满剩余"，两处复原入口必须和列表自己的 Resize 行为一致。
        public static void FillLastColumn(ListView lv, int minW)
        {
            try
            {
                if (lv == null || lv.Columns.Count == 0) return;
                int used = 0;
                for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
                int avail = lv.ClientSize.Width - used - Theme.S(4);
                int w = Math.Max(minW, avail);
                if (w > avail && avail >= Theme.S(80)) w = avail;
                if (w < Theme.S(40)) w = Theme.S(40);
                lv.Columns[lv.Columns.Count - 1].Width = w;
            }
            catch { }
        }

        // ★ ListView 是原生 SysListView32 —— `Buffered()`（反射设 Control.DoubleBuffered）对它**无效**：
        //   原生控件不走 WinForms 的 OnPaint 管线，那个属性只管托管自绘的控件。
        //   真正的开关是扩展样式 LVS_EX_DOUBLEBUFFER（Vista+）。
        //   不打它会怎样（用户 2026-09-19 反馈"滑动时有的色块变得更白、更割裂"）：
        //   滚动时列表先把整片底色擦成 BackColor（浅色主题 = 纯白 Panel），再逐行重绘斑马纹；
        //   擦完到画完之间那一帧就是一片"比斑马纹更白"的区域，滚动中反复闪，
        //   于是同一个列表里有的行白、有的行灰，看起来像被切碎了。
        static void DoubleBufferList(ListView lv)
        {
            if (lv == null) return;
            try
            {
                SendMessage(lv.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER);
            }
            catch { }
        }

        // ---- 滚动掉字补丁（2026-09-20）----
        // 现象（用户三张截图）：快速滑动时①某行只剩名称列、其余三列空白 ②整片列表空白（表头还在）。
        // 排查结论：LVS_EX_DOUBLEBUFFER 给 ListView 挂的是**分块脏区缓冲** —— 滚动时先在缓冲里搬像素、
        //   只重画新露出的窄条。自绘（OwnerDraw）下这条路径会留下"只铺了底色、文字从没写进缓冲"的
        //   死区，而且**会一直保留**（所以用户能静止截到，不是一闪而过的过程帧）。
        //   本机实测：整表重绘只要 10~13ms、滚动 0 掉字 —— 说明不是"画得慢"，就是缓冲脏区不同步。
        // 补丁：滚轮/滚动条一动，就补一次**整表**重绘，把这个窄条路径顶掉。
        //   为什么不用 WS_EX_COMPOSITED（网上常见的"整窗合成"解法）：挂到**子控件**上实测会让
        //   探针直接挂死（SIGTERM），那是给顶层窗口用的，不能套在 ListView 上。
        class ScrollRepaint : NativeWindow
        {
            readonly ListView lv;
            public ScrollRepaint(ListView lv) { this.lv = lv; AssignHandle(lv.Handle); }
            protected override void WndProc(ref Message m)
            {
                const int WM_PAINT = 0x000F;
                bool painting = m.Msg == WM_PAINT;
                if (painting) ClearCells(lv);          // 一帧开始：清掉上一帧的"画过了"记录
                base.WndProc(ref m);                   // 让 ListView 真正去画（自绘回调会写回记录）
                try
                {
                    if (m.Msg == 0x0115 || m.Msg == 0x0114 || m.Msg == 0x020A)   // WM_VSCROLL / WM_HSCROLL / WM_MOUSEWHEEL
                        if (lv != null && !lv.IsDisposed) lv.Invalidate(false);
                    if (painting) AuditAfterPaint(lv);  // 一帧结束：核这一帧有没有"画过却缺格"的行
                }
                catch { }
            }
        }
        static readonly System.Collections.Generic.Dictionary<IntPtr, ScrollRepaint> scrollPatched =
            new System.Collections.Generic.Dictionary<IntPtr, ScrollRepaint>();

        // ---- 表头右键菜单（v3.9.0）----
        // 用户 2026-09-20：「这个栏还能自动调节，但为啥没有一键复原的选项」——
        //   列宽操作的**习惯位置就是表头**，而表头是**独立的 SysHeader32 子窗口**：
        //   在它上面右键，消息发给表头自己，ListView 的 ContextMenuStrip **不会弹**（这是 Win32 行为，
        //   不是配置问题），于是用户视角就是"根本没有这个选项"。
        // 做法：把表头也用 NativeWindow 包一层，拦 WM_CONTEXTMENU 手动弹出同一个菜单。
        class HeaderMenu : NativeWindow
        {
            readonly ListView lv;
            public HeaderMenu(ListView lv) { this.lv = lv; }
            protected override void WndProc(ref Message m)
            {
                const int WM_CONTEXTMENU = 0x007B;
                if (m.Msg == WM_CONTEXTMENU && lv != null && !lv.IsDisposed && lv.ContextMenuStrip != null)
                {
                    long lp = m.LParam.ToInt64();
                    int x = unchecked((short)(lp & 0xFFFF)), y = unchecked((short)((lp >> 16) & 0xFFFF));
                    Point p = (x == -1 && y == -1) ? Cursor.Position : new Point(x, y);   // 键盘弹出菜单时 lParam=-1
                    try { lv.ContextMenuStrip.Show(p); } catch { }
                    return;
                }
                base.WndProc(ref m);
            }
        }
        static readonly System.Collections.Generic.Dictionary<IntPtr, HeaderMenu> headerMenus =
            new System.Collections.Generic.Dictionary<IntPtr, HeaderMenu>();

        static void PatchHeaderMenu(ListView lv)
        {
            if (lv == null) return;
            try
            {
                IntPtr hh = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (hh == IntPtr.Zero) return;
                if (!headerMenus.ContainsKey(hh))
                {
                    var hm = new HeaderMenu(lv);
                    hm.AssignHandle(hh);
                    headerMenus[hh] = hm;
                }
            }
            catch { }
        }

        // 兜底：**滚轮**是 ListView 收到 WM_MOUSEWHEEL 后自己内部滚的（不会给自己发 WM_VSCROLL），
        // 只劫消息会漏掉用户实际用的这条路（2026-09-20 确认："滚轮快滚"）。
        // 所以再挂一个 50ms 的轻量巡检：比较 LVM_GETTOPINDEX(0x102C)，变了就整表重绘一次。
        // 只用索引比较（常数级、无分配），没滚动时等于什么都不做。
        static readonly System.Collections.Generic.List<ListView> scrollWatched =
            new System.Collections.Generic.List<ListView>();
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> scrollTopIdx =
            new System.Collections.Generic.Dictionary<IntPtr, int>();
        static Timer scrollWatch;

        // ---- 逐格自检 + 自愈（2026-09-20）-----------------------------------------
        // 用户报"滑动过快时文字不显示 / 显示过慢"，截图里①某行只剩名称列 ②整片空白。
        // 本机用四种探针都复现不出来（列表自滚 / 拖滚动条 / 页面滚动 / 真滚轮），所以改成
        // **让它自己发现**：每画一格就在位图里记一位（掩码 bit0..3 = 名称/状态/当前值/期望值），
        // 一帧画完（WM_PAINT 处理完）立刻核对：
        //   某个**整行完全可见**的行，这一帧**画过**（掩码≠0）却偏偏没画到状态格（bit1=0）
        //   ⇒ 判定漏画 → 立刻整表重绘（自愈）+ 记一行日志（上限 12 条/进程）。
        // 判据为什么这么绕：只有"画过但缺格"才是**合法路径下不可能出现**的 ——
        //   · 掩码=0（这一帧没画它）：可能只是本帧脏区没覆盖到它，属正常，不能报；
        //   · 掩码≠0 却缺 bit1：说明 comctl32 已经把这一行交给自绘了，是**画的内容**丢了，
        //     而用户截图 1/2 正是这个形态（名称在、其余三列空）。
        // 为什么用第 1 列当"哨兵"：它既窄又靠左（x≈260..346），横向永远落在可视宽度内，
        //   不会因为列宽/横向滚动跑到视口外 ⇒ 它没被画就一定是漏画。
        // 开销：绘制侧一次数组位运算；每个 WM_PAINT 一次 Array.Clear + 十几行 GetItemRect。
        static readonly System.Collections.Generic.Dictionary<IntPtr, int[]> cellMask =
            new System.Collections.Generic.Dictionary<IntPtr, int[]>();
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> cellMiss =
            new System.Collections.Generic.Dictionary<IntPtr, int>();
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> cellFixAt =
            new System.Collections.Generic.Dictionary<IntPtr, int>();
        static int paintDiagLeft = 12;

        public static void PaintDiag(string where, string detail)
        {
            if (paintDiagLeft <= 0) return;
            paintDiagLeft--;
            try { Program.Log("[绘制自检] " + where + " " + detail); } catch { }
        }

        // 仅供探针做"注入式验证"：>=0 时把该行的状态格记成"没画过"。
        // 用来证明上面这套自检**真的抓得到**（否则"没报错"可能只是它从来不工作）。
        // 正常运行时恒为 -1，零影响。
        public static int FaultRow = -1;

        // 自绘回调入口调用：标记"这一行这一列画过了"
        public static void NoteCell(ListView lv, int row, int col)
        {
            if (lv == null || row < 0 || col < 0) return;
            if (row == FaultRow && col == 1) return;      // 注入故障（探针专用）
            try
            {
                IntPtr h = lv.Handle;
                if (h == IntPtr.Zero) return;
                int[] m;
                if (!cellMask.TryGetValue(h, out m)) { m = new int[1024]; cellMask[h] = m; }
                if (row < m.Length) m[row] |= 1 << col;
            }
            catch { }
        }

        // 一帧开始（WM_PAINT 进 WndProc 之前）清账：不清就只能证明"曾经画对过"
        static void ClearCells(ListView lv)
        {
            try
            {
                int[] m;
                if (lv != null && cellMask.TryGetValue(lv.Handle, out m)) Array.Clear(m, 0, m.Length);
            }
            catch { }
        }

        // 一帧结束：核对这一帧里有没有"画过、却缺了状态格"的整行
        static void AuditAfterPaint(ListView lv)
        {
            try
            {
                if (lv == null || lv.IsDisposed || lv.Columns.Count < 2) return;
                IntPtr h = lv.Handle;
                int[] m;
                if (!cellMask.TryGetValue(h, out m)) return;
                int badRow = -1, badMask = 0, badCount = 0, checkedRows = 0;
                int top = (int)SendMessage(h, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
                for (int i = top; i < lv.Items.Count && i < m.Length; i++)
                {
                    Rectangle r;
                    try { r = lv.GetItemRect(i, ItemBoundsPortion.Entire); } catch { break; }
                    if (r.Height <= 4) continue;
                    if (r.Top < 0) continue;                      // 顶部被裁一半 → 不算
                    if (r.Bottom > lv.ClientSize.Height) break;    // 到底了，只查"整行完全可见"的
                    checkedRows++;
                    if (m[i] == 0) continue;                       // 本帧没画它 → 正常
                    if ((m[i] & 2) != 0) continue;                  // 状态格画到了 → 正常
                    badCount++;
                    if (badRow < 0) { badRow = i; badMask = m[i]; }
                }
                if (badCount == 0 || badRow < 0) return;
                int last;
                cellFixAt.TryGetValue(h, out last);
                int now = Environment.TickCount;
                if (last != 0 && unchecked(now - last) < 300) return;   // 修复冷却：免得和重绘互相触发
                cellFixAt[h] = now;
                int n;
                cellMiss.TryGetValue(h, out n);
                cellMiss[h] = n + 1;
                PaintDiag("自绘漏画", "第 " + (n + 1) + " 次  行数=" + lv.Items.Count + " TopIndex=" + top
                          + " 查了 " + checkedRows + " 行 · 缺 " + badCount + " 行"
                          + " 首个缺失行=" + badRow + " 该行本帧已画列掩码=0x" + badMask.ToString("X")
                          + "（bit0..3 = 名称/状态/当前值/期望值）→ 已强制整表重绘");
                lv.Invalidate(false);
                lv.Update();          // 自愈：整表立刻重画
            }
            catch { }
        }

        static void PatchScrollRepaint(ListView lv)
        {
            if (lv == null) return;
            try
            {
                IntPtr h = lv.Handle;
                if (h == IntPtr.Zero) return;
                if (!scrollPatched.ContainsKey(h)) scrollPatched[h] = new ScrollRepaint(lv);
                if (!scrollWatched.Contains(lv)) scrollWatched.Add(lv);
                if (scrollWatch == null)
                {
                    scrollWatch = new Timer();
                    scrollWatch.Interval = 50;
                    scrollWatch.Tick += delegate { ScrollWatchTick(); };
                    scrollWatch.Start();
                }
                else if (!scrollWatch.Enabled) scrollWatch.Start();   // 空表自停后，下一个列表打补丁时重启
            }
            catch { }
        }

        // 巡检内容抽成方法：Tick 里顺带做周期修剪与空表自停（2026-09-30 体检：
        // scrollWatched/cellMask 等只增不减，换主题整树重建后旧引用与每句柄 4KB 掩码无限累积）
        static int watchTickN;
        static void ScrollWatchTick()
        {
            if (++watchTickN % 20 == 0) PruneScrollWatch();                 // ~1s 一次
            if (scrollWatched.Count == 0) { scrollWatch.Stop(); return; }   // 没有要管的列表就停表
            for (int i = 0; i < scrollWatched.Count; i++)
            {
                ListView l = scrollWatched[i];
                try
                {
                    if (l == null || l.IsDisposed) continue;
                    IntPtr hh = l.Handle;
                    int top = (int)SendMessage(hh, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
                    int old;
                    if (scrollTopIdx.TryGetValue(hh, out old) && old != top) l.Invalidate(false);
                    scrollTopIdx[hh] = top;
                }
                catch { }
            }
        }

        // 修剪已销毁列表的引用与句柄键（句柄可能被系统复用，陈旧掩码还会造成一次误判）
        static void PruneScrollWatch()
        {
            try
            {
                for (int i = scrollWatched.Count - 1; i >= 0; i--)
                {
                    ListView l = scrollWatched[i];
                    if (l == null || l.IsDisposed || !l.IsHandleCreated) scrollWatched.RemoveAt(i);
                }
                var live = new HashSet<IntPtr>();
                foreach (ListView l in scrollWatched) { try { live.Add(l.Handle); } catch { } }
                if (live.Count == 0) { scrollTopIdx.Clear(); cellMask.Clear(); cellFixAt.Clear(); return; }
                PruneHandleKeys(scrollTopIdx, live);
                PruneHandleKeys(cellFixAt, live);
                List<IntPtr> dead = new List<IntPtr>();
                foreach (KeyValuePair<IntPtr, int[]> kv in cellMask)
                    if (!live.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (IntPtr h in dead) cellMask.Remove(h);
            }
            catch { }
        }

        static void PruneHandleKeys(Dictionary<IntPtr, int> d, HashSet<IntPtr> live)
        {
            List<IntPtr> dead = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, int> kv in d)
                if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (IntPtr h in dead) d.Remove(h);
        }

        static void ApplyScroll(Control c)
        {
            if (!(c is Panel || c is ListView || c is System.Windows.Forms.TextBoxBase || c is ListBox)) return;
            try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
            // ★ ListView 的表头是**独立的 SysHeader32 子窗口**，只给 ListView 本身设主题它不跟着变
            //   —— 深色下就是一整条白表头（用户截图「优化项明细」）。必须单独把 header 的句柄也设一次。
            ListView lv = c as ListView;
            if (lv != null)
            {
                try
                {
                    IntPtr hh = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (hh != IntPtr.Zero) SetWindowTheme(hh, Dark ? "DarkMode_Explorer" : "Explorer", null);
                }
                catch { }
                DoubleBufferList(lv);      // 原生双缓冲（理由见上）
                PatchScrollRepaint(lv);    // 滚动后整表重绘（掉字补丁，理由见上）
                PatchHeaderMenu(lv);       // 表头右键也能出列宽菜单（理由见上）
            }
        }

        // 在窗体 HandleCreated 之后调用：给整棵控件树挂句柄创建回调，句柄一建就染色
        public static void ScrollThemeTree(Control root)
        {
            if (root == null) return;
            ApplyScroll(root);
            root.HandleCreated += delegate { ApplyScroll(root); };
            foreach (Control c in root.Controls) ScrollThemeTree(c);
        }

        static int Win32(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // 圆角控件"框外那四个角"的正确底色：沿父链找第一个**显式设过底**的容器。
        //   写死 Theme.Panel（纯白）放到浅灰页面上，就会在按钮/复选框四周留一圈白角 ——
        //   用户两次反馈的"选项周边一圈白框，像抠图没扣干净"就是这个（v2.9.1）。
        //   默认 BackColor(SystemColors.Control) 说明该层从未显式设色（WinForms 面板默认值），继续往上找。
        public static Color ParentSurface(Control c)
        {
            try
            {
                for (Control p = c == null ? null : c.Parent; p != null; p = p.Parent)
                {
                    Color b = p.BackColor;
                    if (b == Color.Transparent) continue;
                    if (b.ToArgb() == SystemColors.Control.ToArgb()) continue;
                    return b;
                }
            }
            catch { }
            return Bg;
        }

        // 实际 DPI / 96。进程是 DPI-aware 的（Core 里调了 SetProcessDPIAware），
        // 所以这里读到的是真实缩放；界面用它把"固定像素尺寸"换算一次。
        public static readonly float Scale;
        static Theme()
        {
            float k = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f; } catch { }
            // 允许用数据目录下的 ui.scale 覆盖（内容 1.0 / 1.25 …），用于多屏字体不一致时微调
            try
            {
                string f = Path.Combine(Program.DataDir, "ui.scale");
                if (File.Exists(f))
                {
                    float v;
                    if (float.TryParse(File.ReadAllText(f).Trim(), out v) && v >= 0.8f && v <= 3f) k = v;
                }
            }
            catch { }
            if (k < 1f) k = 1f;
            Scale = k;
        }
        public static int S(float v) { return (int)Math.Round(v * Scale); }
        public static string ScaleText() { return Scale.ToString("0.##") + "x / " + ((int)Math.Round(Scale * 96)) + " DPI"; }

        // 字体缓存：同一字号只创建一次，减少 GDI 句柄。
        // ★ 缓存里的 Font 绝不能放进 using —— 一旦被 Dispose，所有同字号的绘制都会在已销毁的
        //   句柄上操作，GDI+ 抛 ArgumentException「参数无效」，控件被填白打叉（血泪教训）。
        static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();
        static Font Get(string key, float pt, FontStyle style, string family)
        {
            Font f;
            if (Cache.TryGetValue(key, out f)) return f;
            try { f = new Font(family, pt, style); }
            catch { try { f = new Font(FontFamily.GenericSansSerif, pt, style); } catch { f = SystemFonts.DefaultFont; } }
            Cache[key] = f;
            return f;
        }
        public static Font F(float pt) { return Get("f" + pt, pt, FontStyle.Regular, "Microsoft YaHei UI"); }
        public static Font FB(float pt) { return Get("b" + pt, pt, FontStyle.Bold, "Microsoft YaHei UI"); }
        public static Font Mono(float pt) { return Get("m" + pt, pt, FontStyle.Regular, "Consolas"); }

        // 下拉框统一字号（外框与箭头由 RoundCombo 自绘，这里只统一字体）
        public static TextBox StyleText(TextBox t)
        {
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Field;
            t.ForeColor = Text;
            t.Font = F(9f);
            return t;
        }
        public static CheckBox StyleCheck(CheckBox c, string text)
        {
            c.Text = text;
            c.FlatStyle = FlatStyle.Flat;
            c.AutoSize = true;
            c.BackColor = Panel;        // 不用 Transparent：圆角分区的自绘底不会透传给它
            c.ForeColor = Text;
            c.Font = F(9f);
            c.UseMnemonic = false;      // 文案里的 & 不当快捷键
            return c;
        }
        public static RadioButton StyleRadio(RadioButton r, string text)
        {
            r.Text = text;
            r.FlatStyle = FlatStyle.Flat;
            r.AutoSize = true;
            r.BackColor = Panel;
            r.ForeColor = Text;
            r.Font = F(9f);
            r.UseMnemonic = false;
            return r;
        }
    }

    // ======================= 自绘：扁平按钮 =======================
    //  只在自己边界内绘制（FillRectangle + DrawRectangle + TextRenderer），
    //  不可能画到别的控件上。
    public enum BtnKind { Default, Primary, Success, Danger }

    // ═══════════════════ 动效引擎（2026-09-20，规范见 docs/motion-and-ui-research.md）═══════════════════
    //  为什么要有它：在此之前整个界面"零过渡"—— hover 是布尔量、数值是跳变、切页是硬切，
    //  用户的原话是"生硬 / 不够丝滑 / 很单调"。动效不是装饰，它是"这个控件对我的操作有反应"的信号。
    //
    //  取值依据（不拍脑袋，全部取自一手规范）：
    //    · 时长：M3 duration token 阶梯 + NN/g「界面动画 100–500ms，长动画是比短动画更常见的错误」
    //      ⇒ 微交互 80/110ms · 出现与淡变 200ms · 面板展开与数值补间 300ms · 大位移 450ms
    //    · 缓动：直接搬 M3 easing token 的 cubic-bezier 控制点；"进入用减速、退出用加速"
    //      是 M3 / Fluent / NN/g 三方一致的硬规律（也解释了为什么 exit 比 enter 短）。
    //
    //  三条工程约束：
    //    ① 用 Stopwatch 的**真实时间**驱动，不靠 Timer 固定步进 —— 掉帧时动画按真实时间推进，不会忽快忽慢。
    //    ② 没有动画在跑时立刻停表 —— 这是个性能工具，不能为了动效常驻占 CPU。
    //    ③ 遵从 Windows 的"显示动画"开关 —— 关掉时所有 To() 退化为 Snap()（瞬时到位），
    //       界面与我们改之前完全一致，不会因为动效把某些用户恶心到。
    static class Motion
    {
        // ── 时长阶梯（毫秒）────────────────────────────────────────────────
        public const int Press  = 80;    // 按下反馈：2~5 帧内完成，手感"即时"（Carbon fast-01=70 / Primer fast=80）
        public const int Fast   = 110;   // 悬停等微交互（Carbon 90–120 / Fluent fast 100 / Primer 80）
        public const int Normal = 200;   // 元素出现、淡变（M3 short4=200 / Fluent normal=200）
        public const int Medium = 300;   // 面板展开、数值补间（M3 medium2=300 / Fluent slow=300）
        public const int Slow   = 450;   // 大范围位移（M3 long1=450，仍在 500ms 上限之内）

        // ── 缓动：M3 easing token 的 cubic-bezier 控制点（x1,y1,x2,y2）──────
        public static readonly double[] Std     = { 0.20, 0.00, 0.00, 1.00 };  // standard
        public static readonly double[] Decel   = { 0.00, 0.00, 0.00, 1.00 };  // standard.decelerate  ← 进入
        public static readonly double[] Accel   = { 0.30, 0.00, 1.00, 1.00 };  // standard.accelerate  ← 退出
        public static readonly double[] EmphDec = { 0.05, 0.70, 0.10, 1.00 };  // emphasized.decelerate ← 重要的进入
        public static readonly double[] EmphAcc = { 0.30, 0.00, 0.80, 0.15 };  // emphasized.accelerate ← 重要的退出
        //  ⚠ M3 的 emphasized 本身是**两段式路径插值器**，一条 cubic-bezier 表达不了
        //    （官方在 CSS 列里标的是 "N/A (Use Standard as a fallback)"）。
        //    GDI+ 场景下用 emphasized.decelerate 近似它"先慢、然后极速到位"的手感就够了。

        static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        static Motion() { clock.Start(); }
        public static long Now { get { return clock.ElapsedMilliseconds; } }

        // 时间缩放（默认 1.0，正常运行时永远是 1.0）。
        //  只给离屏探针用：抓一帧画面要几十毫秒，200ms 的过渡根本来不及采样 ——
        //  把时长整体放大 N 倍再连拍，才能看清中间过程（前后段各自落在哪一帧，日志里有实测时刻）。
        public static double TimeScale = 1.0;

        static bool? osAnim;
        // 三档：auto=跟随系统 / on=始终开启 / off=关闭。由 MainForm 在构造时与设置页写入。
        public static string Mode = "auto";
        public static void RefreshOsFlag() { osAnim = null; }

        // 系统那一侧到底允不允许（设置页要把这个状态展示给用户，所以单独暴露）
        public static bool OsAllows
        {
            get
            {
                if (!osAnim.HasValue) osAnim = ReadOsAnim();
                return osAnim.Value;
            }
        }

        public static bool Enabled
        {
            get
            {
                if (Mode == "on") return true;
                if (Mode == "off") return false;
                return OsAllows;
            }
        }
        static bool ReadOsAnim()
        {
            try { return Native.ClientAreaAnimationEnabled(); } catch { return true; }
        }

        // CSS 的 cubic-bezier 求值：先二分出参数 t 使 Bx(t)=x，再取 By(t)。
        //   GDI+ 没有任何内置缓动，这 12 行就是全部家当。24 次二分 = 分辨率 6e-8，肉眼绝对够。
        public static double Ease(double[] p, double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            if (p == null) return x;
            double lo = 0, hi = 1, t = x;
            for (int i = 0; i < 24; i++)
            {
                t = (lo + hi) * 0.5;
                if (Bez(p[0], p[2], t) < x) lo = t; else hi = t;
            }
            return Bez(p[1], p[3], t);
        }
        static double Bez(double a1, double a2, double t)
        {
            double u = 1.0 - t;
            return 3.0 * u * u * t * a1 + 3.0 * u * t * t * a2 + t * t * t;
        }

        // ── 全局泵：只在有动画时运行（≈66fps，留一点余量给 60Hz 屏的掉帧）────────
        static readonly List<Anim> live = new List<Anim>();
        static Timer pump;
        internal static void Register(Anim a)
        {
            if (!live.Contains(a)) live.Add(a);
            if (pump == null)
            {
                pump = new Timer();
                pump.Interval = 15;
                pump.Tick += delegate { TickAll(); };
            }
            if (!pump.Enabled) pump.Start();
        }
        internal static void Unregister(Anim a)
        {
            live.Remove(a);
            if (live.Count == 0 && pump != null) pump.Stop();
        }
        static void TickAll()
        {
            long now = Now;
            for (int i = live.Count - 1; i >= 0; i--)
                if (!live[i].Step(now)) live.RemoveAt(i);
            if (live.Count == 0 && pump != null) pump.Stop();
        }
        public static int LiveCount { get { return live.Count; } }
    }

    // 标量补间：把某个属性从**当前实际值**滑到目标值。
    //  可中断是重点：每次 To() 都从 val（此刻屏幕上真实呈现的值）重新起步，而不是从上一段的起点重算 ——
    //  鼠标沿着一排按钮快速扫过时，颜色只会一路追过去，不会回跳。
    public sealed class Anim
    {
        readonly Control owner;
        readonly int dur;
        readonly double[] curve;
        double from, to, val;
        long t0;
        bool on;

        public Anim(Control owner, int durMs, double[] curve)
        {
            this.owner = owner; this.dur = durMs; this.curve = curve;
        }

        public double Value { get { return val; } }
        public bool Running { get { return on; } }

        /// 立即落到目标值（不动画）：初始化用，以及系统关闭动画时走这条路。
        public void Snap(double v)
        {
            from = to = val = v;
            if (on) { on = false; Motion.Unregister(this); }
            Touch();
        }

        public void To(double target)
        {
            if (on && target == to) return;                  // 已经在去往同一个目标，别重启动画
            if (!Motion.Enabled || dur <= 0) { Snap(target); return; }
            if (!on && target == val) return;                // 已经就在目标上，什么都不用做
            from = val; to = target; t0 = Motion.Now; on = true;
            Motion.Register(this);
        }

        internal bool Step(long now)
        {
            double span = dur * (Motion.TimeScale <= 0 ? 1.0 : Motion.TimeScale);
            double x = span <= 0 ? 1.0 : (now - t0) / span;
            if (x >= 1.0) { val = to; on = false; }
            else val = from + (to - from) * Motion.Ease(curve, x);
            Touch();
            return on;
        }

        void Touch()
        {
            if (owner != null && owner.IsHandleCreated && !owner.IsDisposed) owner.Invalidate();
        }
    }

    public class FlatBtn : Button
    {
        public BtnKind Kind = BtnKind.Default;
        // 状态过渡：hover 110ms / press 80ms。端点颜色与改前**逐字节一致**，只是中间多了几帧，
        // 观感只变顺、不变样（鼠标扫过一排按钮，从"啪啪闪"变成一条小尾巴追着走）。
        Anim aHov, aDown;

        public Color? Corner;      // 显式指定"框外角落"的底色；不指定则自动沿父链推断
        public FlatBtn()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Panel;          // 圆角外露出的底色（所在分区底色）
            Font = Theme.F(9f);
            Size = new Size(Theme.S(86), Theme.S(30));
            Cursor = Cursors.Hand;
            TabStop = true;
            aHov = new Anim(this, Motion.Fast, Motion.Decel);
            aDown = new Anim(this, Motion.Press, Motion.Decel);
        }
        protected override void OnMouseEnter(EventArgs e) { aHov.To(1.0); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { aHov.To(0.0); aDown.To(0.0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { aDown.To(1.0); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { aDown.To(0.0); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 先把自己整块矩形铺满（圆角之外的角落也要）：设置 UserPaint + AllPaintingInWmPaint 之后
            //   WinForms 不再擦本控件的背景，不铺满就会留下上一次绘制的残留（黑框 / 文字重影 / 像叠了好几层）。
            // 铺满整块矩形用的必须是"所在分区"的真实底色（写死白色会在灰页面上留白角）
            Color surf = Corner.HasValue ? Corner.Value : Theme.ParentSurface(this);
            using (var __bg = new SolidBrush(surf)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 两级混色：先按悬停进度在"常态 → 悬停"之间插值，再按按下进度往"按下态"拉。
            //  h=0,d=0 与 h=1,d=0 与 d=1 三个端点的结果和改前逐字节相同。
            double h = aHov.Value, dn = aDown.Value;
            Color fill, edge, txt;
            switch (Kind)
            {
                case BtnKind.Primary:
                    fill = Theme.Mix(Theme.Mix(Theme.Accent, Theme.AccentLite, h), Theme.AccentDeep, dn);
                    edge = fill; txt = Color.White; break;
                case BtnKind.Success:
                    fill = Theme.Mix(Theme.OkFill, Theme.OkSoft, Math.Max(h, dn));
                    edge = Theme.Ok; txt = Theme.Ok; break;
                case BtnKind.Danger:
                    fill = Theme.Mix(Theme.ErrFill, Theme.ErrSoft, Math.Max(h, dn));
                    edge = Theme.Err; txt = Theme.Err; break;
                default:
                    fill = Theme.Mix(Theme.Mix(Theme.Panel, Theme.Hover, h), Theme.Field, dn);
                    edge = Theme.Mix(Theme.Mix(Theme.FieldEdge, Theme.Accent, h), Theme.Accent, dn);
                    txt = Theme.Mix(Theme.Mix(Theme.TextDim, Theme.Text, h), Theme.Text, dn);
                    break;
            }
            if (!Enabled) { fill = Theme.Subtle; edge = Theme.Line; txt = Theme.TextFaint; }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Theme.RBtn;
            bool primary = (Kind == BtnKind.Primary) && Enabled;
            if (primary)
            {
                if (h > 0.004 && dn < 0.996)                      // 辉光随悬停进度淡入，不再闪现
                {
                    using (var glowP = new Pen(Color.FromArgb((int)Math.Round(50 * h * (1 - dn)), Theme.Accent), 3f))
                    using (var gpGlow = Theme.Round(r, rad))
                        g.DrawPath(glowP, gpGlow);
                }
                using (var br = Theme.AccentGrad(r)) using (var gp = Theme.Round(r, rad))
                    g.FillPath(br, gp);
            }
            else
            {
                Theme.FillRound(g, r, rad, fill);
                Theme.EdgeRound(g, r, rad, edge, 1f);
            }
            TextRenderer.DrawText(g, Text, primary ? Theme.FB(9f) : Font, r, txt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // ======================= 自绘：现代化硬件遥测条 =======================
    public class Bar : Control
    {
        int val;
        string cap = "";
        string right = "";
        public Color BarColor = Theme.Accent;
        // 瓦片模式（2026-09-20）：仪表盘"实时负载"从四条一模一样的横条
        //   改成 2×2 指标瓦片 —— 有自己的底色、图标，数值放大到 17pt 一眼能扫。
        public bool Grid;

        // ★ 数值补间（2026-09-20）：sysTimer 每 4s 喂一次新值，改之前条宽和数字都是**跳变**的。
        //   现在条宽和数字各走一条 300ms 的 decelerate 补间 —— 仪表盘从"读表格"变成"看仪表"。
        //   依据：M3 的数值变化用 medium（300ms）；NN/g 的 200–400ms 区间；且必须可中断（To 从当前值起步）。
        Anim aBar, aNum;
        bool numInit, hasNum, numDec;
        string numSuffix = "";

        public Bar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Height = Theme.S(42);
            aBar = new Anim(this, Motion.Medium, Motion.Decel);
            aNum = new Anim(this, Motion.Medium, Motion.Decel);
            aBar.Snap(0); aNum.Snap(0);
        }
        public int Value
        {
            get { return val; }
            set { int v = value < 0 ? 0 : (value > 100 ? 100 : value); if (v == val) return; val = v; aBar.To(v); }
        }
        public string Caption { get { return cap; } set { cap = value == null ? "" : value; Invalidate(); } }
        public string RightText
        {
            get { return right; }
            set { right = value == null ? "" : value; ParseNum(); Invalidate(); }
        }

        // "37%" / "61°C" / "3.2 GB" → 数字前缀参与补间，后缀原样拼回。
        //  ❗首次赋值走 Snap 而不是 To：否则启动瞬间会从 0% 滚上来，看起来像在报"CPU 0%"。
        void ParseNum()
        {
            numSuffix = ""; numDec = false; hasNum = false;
            var m = System.Text.RegularExpressions.Regex.Match(right, @"^\s*(-?\d+(?:\.\d+)?)\s*(.*)$");
            double n;
            if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                if (!numInit) { numInit = true; aNum.Snap(0); }
                return;
            }
            numDec = m.Groups[1].Value.IndexOf('.') >= 0;
            numSuffix = m.Groups[2].Value;
            hasNum = true;
            if (!numInit) { numInit = true; aNum.Snap(n); }
            else aNum.To(n);
        }

        // 补间中的条宽（给离屏探针取证用：证明是"滑"过去的而不是跳过去的）
        public double ShownValue { get { return aBar.Value; } }

        // 绘制用的数值文本（补间中的那一帧）
        string NumText()
        {
            if (!hasNum) return right;
            double v2 = aNum.Value;
            return (numDec ? v2.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                           : ((long)Math.Round(v2)).ToString(System.Globalization.CultureInfo.InvariantCulture)) + numSuffix;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (Grid) { PaintTile(g); return; }

            // 绘制左侧标题与硬件微图标
            int iconSz = Theme.S(13);
            var iconRect = new Rectangle(0, Theme.S(2), iconSz, iconSz);
            if (cap.Contains("CPU")) Theme.DrawIconChip(g, iconRect, BarColor);
            else if (cap.Contains("GPU") && !cap.Contains("温度")) Theme.DrawIconChip(g, iconRect, BarColor);
            else if (cap.Contains("内存")) Theme.DrawIconPulse(g, iconRect, BarColor);
            else if (cap.Contains("温度")) Theme.DrawIconLightning(g, iconRect, BarColor);

            int titleX = iconSz + Theme.S(6);
            var fc = Theme.FB(9.5f);
            var fv = Theme.FB(10.5f);
            int lh = Math.Max(fc.Height, fv.Height);

            TextRenderer.DrawText(g, cap, fc, new Rectangle(titleX, 0, Width / 2, lh), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 右侧数值
            TextRenderer.DrawText(g, NumText(), fv, new Rectangle(Width / 2, 0, Width - Width / 2, lh), BarColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 进度条轨道
            int ty = lh + Theme.S(6);
            int th = Theme.S(7);
            if (ty + th > Height) th = Math.Max(2, Height - ty);

            var track = new Rectangle(0, ty, Width - 1, th);
            Theme.FillRound(g, track, th / 2, Theme.Field);
            Theme.EdgeRound(g, track, th / 2, Theme.LineSoft, 1f);

            int w = (int)Math.Round((Width - 1) * aBar.Value / 100.0);       // 条宽跟着补间走
            if (w > 0)
            {
                int barW = Math.Max(th, w);
                var barRect = new Rectangle(0, ty, barW, th);
                Color endCol = Color.FromArgb(Math.Min(255, BarColor.R + 30), Math.Min(255, BarColor.G + 30), Math.Min(255, BarColor.B + 40));
                using (var br = new LinearGradientBrush(barRect, BarColor, endCol, 0f))
                {
                    Theme.FillRound(g, barRect, th / 2, br);
                }
                if (barW >= th)
                {
                    int beadSz = Math.Max(2, th - 2);
                    using (var wb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                        g.FillEllipse(wb, barRect.Right - beadSz - 1, ty + 1, beadSz, beadSz);
                }
            }
        }

        // 指标瓦片：色相底 + 图标 + 放大数值 + 底部细条
        void PaintTile(Graphics g)
        {
            int pad = Theme.S(13);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.S(12), Theme.TileBg(BarColor));
            Theme.EdgeRound(g, r, Theme.S(12), Color.FromArgb(Theme.Dark ? 66 : 44, BarColor), 1f);

            int iconSz = Theme.S(14);
            var iconRect = new Rectangle(pad, pad - Theme.S(1), iconSz, iconSz);
            if (cap.Contains("CPU")) Theme.DrawIconChip(g, iconRect, BarColor);
            else if (cap.Contains("内存")) Theme.DrawIconPulse(g, iconRect, BarColor);
            else if (cap.Contains("温度")) Theme.DrawIconLightning(g, iconRect, BarColor);
            else Theme.DrawIconChip(g, iconRect, BarColor);

            TextRenderer.DrawText(g, cap, Theme.F(9f),
                new Rectangle(pad + iconSz + Theme.S(6), pad - Theme.S(3), Width / 2, Theme.S(20)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 数值放大：瓦片模式的全部意义就在这一行
            TextRenderer.DrawText(g, NumText(), Theme.FB(17f),
                new Rectangle(Width / 2, pad - Theme.S(7), Width - pad - Width / 2, Theme.S(30)), BarColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int th = Theme.S(6);
            int ty = Height - pad - th;
            var track = new Rectangle(pad, ty, Width - pad * 2, th);
            Theme.FillRound(g, track, th / 2, Color.FromArgb(Theme.Dark ? 44 : 28, BarColor));
            int w = (int)Math.Round(track.Width * Math.Min(100, aBar.Value) / 100.0);   // 条宽跟着补间走
            if (w > 0) Theme.FillRound(g, new Rectangle(pad, ty, Math.Max(th, w), th), th / 2, BarColor);
        }
    }

    // ======================= 自绘：现代化遥测面积曲线 =======================
    public class Spark : Control
    {
        readonly List<float> data = new List<float>();
        public Color LineColor = Theme.Accent;
        public string Caption = "";
        public int MaxPoints = 90;

        public Spark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Height = Theme.S(118);
        }
        public void Push(float v) { data.Add(v); while (data.Count > MaxPoints) data.RemoveAt(0); Invalidate(); }
        public void Clear() { data.Clear(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var f = Theme.F(8.5f);
            var fv = Theme.FB(9.5f);

            // 标题
            TextRenderer.DrawText(g, Caption, f, new Rectangle(Theme.S(2), 0, Width / 2, f.Height + Theme.S(2)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            // 右侧最新值
            if (data.Count > 0)
            {
                float lastVal = data[data.Count - 1];
                TextRenderer.DrawText(g, ((int)Math.Round(lastVal)) + "%", fv,
                    new Rectangle(Width / 2, 0, Width / 2 - Theme.S(4), f.Height + Theme.S(2)), LineColor,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            int top = f.Height + Theme.S(8);
            int h = Height - top - Theme.S(4);
            if (h < Theme.S(10)) return;

            // 背景参考网格线
            using (var p = new Pen(Theme.LineSoft, 1f))
            {
                p.DashStyle = DashStyle.Dot;
                for (int i = 1; i <= 3; i++)
                {
                    int y = top + h * i / 4;
                    g.DrawLine(p, 0, y, Width - 1, y);
                }
            }

            if (data.Count < 2)
            {
                TextRenderer.DrawText(g, "等待采样…", f, new Rectangle(0, top, Width, h),
                    Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var pts = new PointF[data.Count];
            for (int i = 0; i < data.Count; i++)
            {
                float v = data[i]; if (v < 0) v = 0; if (v > 100) v = 100;
                pts[i] = new PointF((Width - 1) * (float)i / (MaxPoints - 1), top + h - h * v / 100f);
            }

            // 面积流光渐变填充
            using (var gp = new GraphicsPath())
            {
                gp.AddLines(pts);
                gp.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, top + h));
                gp.AddLine(new PointF(pts[pts.Length - 1].X, top + h), new PointF(pts[0].X, top + h));
                gp.CloseFigure();
                var fillRect = new Rectangle(0, top, Width, h);
                using (var br = new LinearGradientBrush(fillRect, Color.FromArgb(50, LineColor), Color.FromArgb(0, LineColor), 90f))
                {
                    g.FillPath(br, gp);
                }
            }

            // 曲线绘制
            using (var p = new Pen(LineColor, 1.8f))
            {
                p.LineJoin = LineJoin.Round;
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, pts);
            }

            // 最新采样点发光微珠
            PointF head = pts[pts.Length - 1];
            using (var b = new SolidBrush(Color.FromArgb(90, LineColor)))
                g.FillEllipse(b, head.X - 4f, head.Y - 4f, 8f, 8f);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, head.X - 2f, head.Y - 2f, 4f, 4f);
        }
    }

    // ======================= 内容分区（光标式纵向布局） =======================
    //  用法：var s = new Sec("标题"); s.Row("标签", 控件); s.Body("说明文字"); p.Add(s);
    //  ★ 所有 y 都由 items 的顺序 + 每个控件的实测高度推导，因此不存在"写死坐标被字撑破"
    //    这类问题；标签与控件在各自行内垂直居中，永远不会有"字压在框上"。
    // 双缓冲的 FlowLayoutPanel：封面网格滚动时不留残像。
    // Control.SetStyle 是 protected —— 只能靠派生类开，没法从外面调用（CS1540）。
    class BufferedFlow : FlowLayoutPanel
    {
        public BufferedFlow()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    // 封面网格：每次布局都按【当前宽度】算出应有的高度并设置（页面只留一条滚动条）。
    // 为什么不用 AutoSize + WrapContents：FlowLayoutPanel 的自动高度是按"不受约束的宽度"量的，
    // 换行后的行数算不准；也不能只在外部按旧宽度算一次 —— 首次布局 / 窗口变窄时算出的行数偏大，
    // 网格就会在页面里留出一大片空白（2026-09-14 用户截图："游戏太多会生成一大块白色区域"）。
    class CardGrid : BufferedFlow
    {
        public int CellW = 160, CellH = 266, PadPx = 6;
        bool busy, refitQueued;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            FitHeight();
        }

        // ★ 宽度一变就**同步**重算高度。
        //   父容器 Sec.DoLayout() 的写法是「先设宽度 → 再量高度」，而 WinForms 的子控件布局是
        //   延迟执行的：只靠 OnLayout，父容器量到的仍是旧高度 → 整页内容高度算错 →
        //   表现就是"上面或下面多出一大块白色区域"，而且只在窗口尺寸变化后偶发（用户截图）。
        //   放进 OnClientSizeChanged，父容器读到的就是新值。
        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            FitHeight();
        }

        void FitHeight()
        {
            if (busy) return;
            bool changed = false;
            busy = true;
            try
            {
                // ★ 宽度还没确定（窗口尚未布局 / 分区尚未给宽度）→ 直接不算。
                //   不挡这一下，启动初期 ClientSize.Width≈0 会算出"每行 1 张、共 16 行 = 3780px"的
                //   假高度，而 WinForms 会把这个巨大的滚动区**缓存**下来 —— 之后就算网格高度恢复正常，
                //   滚动条也不再重算，用户就能往下滚出一大片空白（2026-09-15 实测 DisplayRect=4031）。
                if (ClientSize.Width <= CellW) return;
                // 可用宽度按"去掉左右留白"算：直接拿 ClientSize 除会在居中缩进较大时多算一列
                // → 行数偏少 → 高度偏小 → 最后一行被裁
                int cw = ClientSize.Width - PadPx * 2;
                if (cw < CellW) cw = CellW;
                int perRow = Math.Max(1, cw / CellW);
                int leftover = cw - perRow * CellW;
                // 居中：每张卡的 Margin 已经含在 CellW 里，所以按子控件区宽度算
                int left = PadPx + Math.Max(0, leftover / 2);
                if (Padding.Left != left)
                {
                    Padding = new Padding(left, PadPx, PadPx, PadPx);
                    changed = true;
                }
                // 空列表 → 高度归零；否则会留一整屏空白（"多出一大块白色区域"的另一半来源）
                int rows = Controls.Count == 0 ? 0 : Math.Max(1, (Controls.Count + perRow - 1) / perRow);
                int want = rows == 0 ? 0 : rows * CellH + PadPx * 2;   // 空列表不留任何占位高度
                if (Height != want) { Height = want; changed = true; }
            }
            finally { busy = false; }

            // 竖滚动条的出现/消失会让可用宽度变化十几像素，行数可能因此变一 —— 等这一轮布局彻底
            // 落定后再收敛一次，否则会停在"按旧宽度算出的行数"上：多留一行空白或裁掉一行。
            if (changed && !refitQueued && IsHandleCreated)
            {
                refitQueued = true;
                try { BeginInvoke((Action)(delegate { refitQueued = false; FitHeight(); })); }
                catch { refitQueued = false; }
            }

            if (!changed) return;
            Sec s = Tag as Sec;
            if (s != null) { try { s.Resync(); } catch { } }
        }
    }

    //  对外暴露 Items / SelectedIndex / SelectedItem / SelectedIndexChanged + DropDownStyle 空属性，
    //  与原 ComboBox 用法兼容，调用点不用改逻辑。
    public class RoundCombo : Control
    {
        public class ItemList
        {
            readonly System.Collections.Generic.List<string> l = new System.Collections.Generic.List<string>();
            readonly RoundCombo o;
            public ItemList(RoundCombo owner) { o = owner; }
            public int Count { get { return l.Count; } }
            public string this[int i] { get { return (i >= 0 && i < l.Count) ? l[i] : ""; } }
            public void Add(string s) { l.Add(s == null ? "" : s); o.ItemsChanged(); }
            // 就地改一项的文案（索引不变）—— 例如「回收范围」各项后面回填条数/体积：
            //  重建整个列表会让 SelectedIndex 复位，用户刚选的档位会被悄悄改回去。
            public void Set(int i, string s)
            {
                if (i < 0 || i >= l.Count) return;
                string v = s == null ? "" : s;
                if (l[i] == v) return;
                l[i] = v; o.ItemsChanged();
            }
            public void AddRange(object[] a)
            {
                if (a != null) foreach (object x in a) l.Add(x == null ? "" : x.ToString());
                o.ItemsChanged();
            }
            public void Clear() { l.Clear(); o.ItemsChanged(); }
            internal string At(int i) { return (i >= 0 && i < l.Count) ? l[i] : ""; }
        }

        public readonly ItemList Items;
        readonly ToolStripDropDown drop = new ToolStripDropDown();
        int sel = -1;
        bool hover;

        public event EventHandler SelectedIndexChanged;

        public RoundCombo()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Items = new ItemList(this);
            Height = Theme.S(28);
            Width = Theme.S(120);
            BackColor = Theme.Panel;   // 工具条里经 SetCorner 改为页面底色（四角不露白）
            Font = Theme.F(9f);
            Cursor = Cursors.Hand;
            drop.AutoSize = false;
            drop.Padding = Padding.Empty;
        }

        // 圆角外四角颜色（工具条里改为页面底色，四角不露白）
        public void SetCorner(Color c) { BackColor = c; }

        // 与 ComboBox 调用点兼容（原来会写 DropDownStyle = DropDownList）
        public ComboBoxStyle DropDownStyle { get; set; }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value < -1 ? -1 : value;
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public object SelectedItem { get { return (sel >= 0 && sel < Items.Count) ? (object)Items.At(sel) : null; } }
        public override string Text
        {
            get { return SelectedItem == null ? "" : SelectedItem.ToString(); }
            set { }
        }
        internal void ItemsChanged()
        {
            if (sel >= Items.Count) sel = Items.Count > 0 ? 0 : -1;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        void ShowDrop()
        {
            if (!Enabled || Items.Count == 0) return;
            var m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            m.Font = Theme.F(9f);
            m.MinimumSize = new Size(Width, 0);
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Items.At(i));
                it.ForeColor = Theme.Text;
                it.Checked = (i == sel);
                it.Click += delegate { SelectedIndex = idx; };
                m.Items.Add(it);
            }
            m.Show(this, 0, Height + 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ShowDrop();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 先把自己整块矩形铺满（圆角之外的角落也要）：设置 UserPaint + AllPaintingInWmPaint 之后
            //   WinForms 不再擦本控件的背景，不铺满就会留下上一次绘制的残留（黑框 / 文字重影 / 像叠了好几层）。
            using (var __bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.RField, Theme.Field);
            Theme.EdgeRound(g, r, Theme.RField,
                (hover || drop.Visible) && Enabled ? Theme.Accent : Theme.FieldEdge, 1f);
            string tx = Text;
            if (tx.Length == 0) tx = "—";
            TextRenderer.DrawText(g, tx, Font, new Rectangle(Theme.S(10), 0, Width - Theme.S(30), Height),
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            // 自绘 chevron：原生箭头是系统色，深色主题下会突兀
            int cx = Width - Theme.S(14), cy = Height / 2;
            using (var p = new Pen(Enabled ? Theme.TextDim : Theme.TextFaint, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[] {
                    new Point(cx - Theme.S(4), cy - Theme.S(2)),
                    new Point(cx, cy + Theme.S(2)),
                    new Point(cx + Theme.S(4), cy - Theme.S(2)) });
            }
        }
    }

    // 圆角复选框：原生方框在深色主题下同样是系统白块，自绘为圆角方框 + 圆头对勾
    public class RoundCheck : CheckBox
    {
        public Color? Corner;      // 框外底色（由所在分区下发）；不设则沿父链推断
        bool hover;
        public RoundCheck()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size bs = base.GetPreferredSize(proposedSize);
            return new Size(bs.Width + Theme.S(14), Math.Max(bs.Height, Theme.S(20)));
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 先把自己整块矩形铺满（圆角之外的角落也要）：设置 UserPaint + AllPaintingInWmPaint 之后
            //   WinForms 不再擦本控件的背景，不铺满就会留下上一次绘制的残留（黑框 / 文字重影 / 像叠了好几层）。
            Color surf = Corner.HasValue ? Corner.Value : Theme.ParentSurface(this);
            using (var __bg = new SolidBrush(surf)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = Theme.S(14);
            int y = (Height - s) / 2;
            var box = new Rectangle(0, y, s, s);
            Theme.FillRound(g, box, Theme.S(4), Enabled ? (Checked ? Theme.Accent : Theme.Field) : Theme.LineSoft);
            Theme.EdgeRound(g, box, Theme.S(4),
                Checked ? Theme.Accent : ((hover && Enabled) ? Theme.Accent : Theme.FieldEdge), 1.2f);
            if (Checked)
            {
                using (var p = new Pen(Theme.OnAccent, 1.9f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new Point[] {
                        new Point(box.X + Theme.S(4), box.Y + s / 2),
                        new Point(box.X + s / 2 - Theme.S(1), box.Bottom - Theme.S(5)),
                        new Point(box.Right - Theme.S(4), box.Y + Theme.S(4)) });
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(20), 0, Width - Theme.S(20), Height),
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // 自绘单选框：原生单选框在深色主题下是刺眼的白色圆底高光，自绘为暗底圆环 + 翡翠绿中心实心点
    public class RoundRadio : RadioButton
    {
        public Color? Corner;      // 框外底色（由所在分区下发）；不设则沿父链推断
        bool hover;
        public RoundRadio()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size bs = base.GetPreferredSize(proposedSize);
            return new Size(bs.Width + Theme.S(14), Math.Max(bs.Height, Theme.S(20)));
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color surf = Corner.HasValue ? Corner.Value : Theme.ParentSurface(this);
            using (var __bg = new SolidBrush(surf)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Theme.S(14);
            int y = (Height - d) / 2;
            var circle = new Rectangle(0, y, d, d);

            Color bg = Enabled ? (Checked ? Theme.CheckOn : Theme.Field) : Theme.LineSoft;
            Color border = Checked ? Theme.Accent : ((hover && Enabled) ? Theme.Accent : Theme.FieldEdge);

            using (var b = new SolidBrush(bg))
                g.FillEllipse(b, circle);
            using (var p = new Pen(border, 1.2f))
                g.DrawEllipse(p, circle);

            if (Checked)
            {
                int dotD = Theme.S(6);
                int dotX = circle.X + (d - dotD) / 2;
                int dotY = circle.Y + (d - dotD) / 2;
                using (var bDot = new SolidBrush(Theme.Accent))
                    g.FillEllipse(bDot, dotX, dotY, dotD, dotD);
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(20), 0, Width - Theme.S(20), Height),
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // 菜单（右键菜单 / 下拉框 / 托盘菜单）在深色主题下也要跟着变，否则会弹出浅色面板
    class ThemedColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Theme.Line; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Field; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Field; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Field; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Field; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Line; } }
        public override Color CheckBackground { get { return Theme.AccentSoft; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
    }
    class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Field)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }
    }

    // ===================== 圆角控件层（v2.6.0） =====================
    //  统一圆角来源：Theme.RCard / RBtn / RField / RPic。
    //  为什么不用原生控件：WinForms 的 PictureBox / Button / TextBox 都只能画直角，
    //  而「边角太锋利」正是用户对 2.5.1 的直接反馈，所以这几个必须自绘。


    // 圆角侧栏导航项：选中态 = 强调色软底胶囊 + 左侧发光条 + 专属矢量几何图标
    public class NavBtn : Button
    {
        bool active;
        // Active 改成属性：切页时不再"药丸瞬间出现又瞬间消失"，而是 200ms 淡入 / 淡出。
        //  200ms + emphasized.decelerate 是 M3 给"屏内进入"的配对，也是 Fluent 的 enter 时长。
        public bool Active
        {
            get { return active; }
            set
            {
                if (active == value) return;
                active = value;
                if (aAct != null) aAct.To(value ? 1.0 : 0.0);
            }
        }
        // 把当前选中项的淡入重放一遍（切换动效档位时用来"当场证明生效了"）
        public void ReplayActive()
        {
            if (!active || aAct == null) return;
            aAct.Snap(0.18);
            aAct.To(1.0);
        }
        public int NavIndex = -1;
        Anim aHov, aAct;
        public NavBtn()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Panel;
            TabStop = false;
            Cursor = Cursors.Hand;
            aHov = new Anim(this, Motion.Fast, Motion.Decel);
            aAct = new Anim(this, Motion.Normal, Motion.EmphDec);
            aAct.Snap(0);
        }
        protected override void OnMouseEnter(EventArgs e) { aHov.To(1.0); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { aHov.To(0.0); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var __bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var r = new Rectangle(Theme.S(8), Theme.S(2), Width - Theme.S(16), Height - Theme.S(4));
            // 状态叠加：hv=悬停进度，ac=选中进度。两层都画，各自按自己的进度淡入淡出，
            //  所以"从一个条目滑到另一个条目"时不会出现某一帧两个都亮 / 都灭。
            double hv = aHov.Value, ac = aAct.Value;
            if (hv > 0.004)
                Theme.FillRound(g, r, Theme.RBtn, Theme.Mix(BackColor, Theme.Hover, hv * (1.0 - ac)));
            if (ac > 0.004)
            {
                Theme.FillRound(g, r, Theme.RBtn, Theme.Mix(BackColor, Theme.AccentSoft, ac));
                Theme.EdgeRound(g, r, Theme.RBtn, Color.FromArgb((int)Math.Round(50 * ac), Theme.Accent), 1f);
                // 左发光条：随选中进度长出来
                Theme.FillRound(g, new Rectangle(Theme.S(2), r.Y + Theme.S(6), Theme.S(3), r.Height - Theme.S(12)),
                    Theme.S(2), Theme.Alpha(Theme.Accent, ac));
            }

            Color itemColor = Theme.Mix(Theme.Mix(Theme.TextDim, Theme.Text, hv), Theme.Accent, ac);

            // 图标胶囊（2026-09-20）：选中项给图标一层同色浅底，与卡片标题胶囊呼应。
            //   以前只有一行浅绿药丸 + 裸图标，整条侧栏寡淡（用户："界面还是很单调"）。
            int chipSz2 = Theme.S(26);
            int chipX2 = Theme.S(12);
            int chipY2 = (Height - chipSz2) / 2;
            if (ac > 0.01)
                Theme.FillRound(g, new Rectangle(chipX2, chipY2, chipSz2, chipSz2), Theme.RChip,
                    Theme.Alpha(Theme.ChipBg(Theme.Accent), ac));

            // 绘制左侧专属矢量图标
            int iconSz = Theme.S(16);
            int iconX = chipX2 + (chipSz2 - iconSz) / 2;
            int iconY = chipY2 + (chipSz2 - iconSz) / 2;
            var iconRect = new Rectangle(iconX, iconY, iconSz, iconSz);

            switch (NavIndex)
            {
                case 0: Theme.DrawIconSpeedometer(g, iconRect, itemColor); break;
                case 1: Theme.DrawIconLightning(g, iconRect, itemColor); break;
                case 2: Theme.DrawIconChip(g, iconRect, itemColor); break;
                case 3: Theme.DrawIconGamepad(g, iconRect, itemColor); break;
                case 4: Theme.DrawIconPulse(g, iconRect, itemColor); break;
                case 5: Theme.DrawIconBook(g, iconRect, itemColor); break;
                case 6: Theme.DrawIconGear(g, iconRect, itemColor); break;
                default:
                    using (var b = new SolidBrush(itemColor))
                        g.FillEllipse(b, iconX + iconSz / 4f, iconY + iconSz / 4f, iconSz / 2f, iconSz / 2f);
                    break;
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(46), 0, Width - Theme.S(50), Height),
                itemColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // 圆角输入框宿主：TextBox 自身画不了圆角，套一层自绘外框
    class RoundField : Panel
    {
        public readonly TextBox Box;
        bool focus;
        public RoundField(int w, string hint)
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Width = w; Height = Theme.S(30);

            hintText = hint == null ? "" : hint;
            bool isSearch = hintText.Contains("搜索");
            int leftPad = isSearch ? Theme.S(28) : Theme.S(11);

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.F(9.5f);
            Box.Left = leftPad;
            Box.Top = Theme.S(7);
            Box.Width = w - leftPad - Theme.S(11);
            Box.TextChanged += delegate { Invalidate(); };
            Box.GotFocus += delegate { focus = true; Invalidate(); };
            Box.LostFocus += delegate { focus = false; Invalidate(); };
            Controls.Add(Box);

            if (hintText.Length > 0)
            {
                hintOn = true;
                Box.ForeColor = Theme.TextFaint;
                Box.Text = hintText;
                Box.GotFocus += delegate
                {
                    if (!hintOn) return;
                    hintOn = false;
                    Box.ForeColor = Theme.Text;
                    Box.Text = "";
                };
                Box.LostFocus += delegate
                {
                    if (hintOn || Box.Text.Trim().Length > 0) return;
                    hintOn = true;
                    Box.ForeColor = Theme.TextFaint;
                    Box.Text = hintText;
                };
            }
        }

        public void SetCorner(Color c) { BackColor = c; }

        string hintText = "";
        bool hintOn;
        public string Query { get { return hintOn ? "" : Box.Text.Trim(); } }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Box != null)
            {
                int leftPad = hintText.Contains("搜索") ? Theme.S(28) : Theme.S(11);
                Box.Left = leftPad;
                Box.Width = Math.Max(Theme.S(20), Width - leftPad - Theme.S(11));
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var __bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.RField, Theme.Field);
            Theme.EdgeRound(g, r, Theme.RField, focus ? Theme.Accent : Theme.FieldEdge, focus ? 1.6f : 1f);

            if (hintText.Contains("搜索"))
            {
                Color ic = focus ? Theme.Accent : Theme.TextFaint;
                using (var p = new Pen(ic, 1.5f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    int mgR = Theme.S(4);
                    int mgX = Theme.S(10), mgY = (Height - mgR * 2) / 2 - 1;
                    g.DrawEllipse(p, mgX, mgY, mgR * 2, mgR * 2);
                    g.DrawLine(p, mgX + mgR * 2 - 1, mgY + mgR * 2 - 1, mgX + mgR * 2 + Theme.S(4), mgY + mgR * 2 + Theme.S(4));
                }
            }
        }
    }

    // 页面标题头：让「标题」和「页面」成为一体
    class HeadBox : Panel
    {
        readonly string title;
        string sub;
        // ★ 消融长度（2026-09-20 二稿）：0 = 用自身高度（58px，每个分区页头都这样）。
        //   页面**最上面那一条**会被 Pg 设成 TopFadeLen（默认 110），此时渐变只在控件的
        //   前 58/110 段，剩下的部分由 Pg 自己的顶部渐变接着画 —— 两段共用同一条曲线，
        //   合起来就是一条 110px 长、两端都慢的"消融"，而不是两条短渐变拼起来。
        public int FadeLen;
        public HeadBox(string title, string sub)
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.title = title == null ? "" : title;
            this.sub = sub == null ? "" : sub;
            // ★ 底色与侧栏/标题栏同用 Panel（白）—— 顶部整条（DWM 标题栏 + 品牌头 + 页头）
            //   连成一个无缝横带，灰色页面从页头下沿才开始；以前用 Bg（灰），
            //   与左侧白色品牌头撞出一条竖向接缝（用户 2026-09-20 截图"设计很不美观"）。
            BackColor = Theme.Panel;
            Height = Theme.S(58);
        }
        public string Sub
        {
            get { return sub; }
            set { sub = value == null ? "" : value; Invalidate(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // ★ 顶条收口（2026-09-20 二稿）：白 → 页面灰，跨**整高**的 smoothstep 缓动。
            //   演进：① 纯白铺到底 + 硬边 → "太生硬"；② 最后 28%（≈17px）线性落下 →
            //   用户仍说"色块颜色变化还是不够丝滑"。17px 在这个窗口尺度上就是一个"糊掉的小段"，
            //   而且线性渐变的起点是一行"起跑线"，眼睛抓得到。
            //   现在：渐变覆盖整个页头高度（58px），并且走 smoothstep（两端慢、中间快），
            //   起止两侧都察觉不到"从哪一行开始变"。
            int fl = FadeLen > Height ? FadeLen : Height;
            using (var br = Theme.VFade(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, fl)),
                       Theme.Panel, Theme.Bg, 12))
            {
                g.FillRectangle(br, 0, 0, Width, Height);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rf = Theme.FB(15.5f);
            int th = Theme.S(26);
            // ★ 底铺满、文字缩进（2026-09-20）：底色铺满 Pg 全宽（见 Pg.LayoutOnce），
            //   文字仍按 GapSize 让出左边距，与下方卡片左边缘严格对齐。
            int padX = Theme.S(14);
            TextRenderer.DrawText(g, title, rf, new Rectangle(padX, Theme.S(1), Width - padX, th), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (sub.Length > 0)
                TextRenderer.DrawText(g, sub, Theme.F(8.5f),
                    new Rectangle(padX + Theme.S(1), Theme.S(2) + th, Width - padX, Theme.S(16)), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // 游戏卡片 —— 对齐《游戏启动器》的 .card 设计：
    //   封面铺满整张卡（object-fit: cover）→ 底部渐变遮罩 → 名称 / 平台 / 状态压在图上。
    // 为什么不再用"上方封面 + 下方两行文字"的分段式：两段之间天然有条接缝，
    //   整排看过去很割裂（用户原话"很多地方衔接不优美很割裂"）。
    // 顺带好处：整张卡只有一个绘制面（无子控件），少一类子控件重绘问题。
    class GameCard : Panel
    {
        public DlssgGame Game;
        public bool Picked;
        bool hover;
        readonly Image cover;        // CoverArt.Card 已裁切填满并烘焙好圆角（alpha 抗锯齿）

        public event EventHandler Chosen;
        public event EventHandler Launched;
        public event Action<GameCard, string> MenuAction;

        static Font nameFont, metaFont, starFont;
        static Font NameFont() { if (nameFont == null) nameFont = Theme.FB(9f); return nameFont; }
        static Font MetaFont() { if (metaFont == null) metaFont = Theme.F(7.5f); return metaFont; }
        static Font StarFont() { if (starFont == null) starFont = Theme.FB(10f); return starFont; }
        static ToolTip tip;
        static ToolTip Tip()
        {
            if (tip == null) { tip = new ToolTip(); tip.AutoPopDelay = 8000; tip.InitialDelay = 400; }
            return tip;
        }

        public GameCard(DlssgGame g, int w, int h)
        {
            Game = g;
            Size = new Size(w, h);
            Margin = new Padding(Theme.S(7));   // 卡片间距（CellW = 卡片宽 + 2×7）
            BackColor = Theme.Bg;             // 圆角之外露出的底色（= 网格/页面底色）
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            // 分享版不画封面：那份视觉是"游戏启动器"的样子，分享版要的是工具感（见 PaintInfoCard）
            cover = Program.ShareBuild ? null : CoverArt.Card(g, w, h);
            try
            {
                long played = Program.PlayTimeOf(Lib.IdOf(g));
                Tip().SetToolTip(this, (g.Ignored ? "[已忽略] " : "") + g.Title + "\r\n"
                    + StateText(g) + (played > 0 ? "\r\n累计时长：" + Program.FmtPlay(played) : "")
                    + (g.Dir != null && g.Dir.Length > 0 ? "\r\n" + g.Dir : ""));
            }
            catch { }

            Click += delegate { if (Chosen != null) Chosen(this, EventArgs.Empty); };
            DoubleClick += delegate { if (Launched != null) Launched(this, EventArgs.Empty); };
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };

            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Font = Theme.F(9f);
            menu.Opening += delegate { BuildMenu(menu); };
            ContextMenuStrip = menu;
        }

        void BuildMenu(ContextMenuStrip m)
        {
            m.Items.Clear();
            AddItem(m, "启动游戏", "launch");
            if (Game.Platform != "steam")   // steam:// 协议带不了参数
                AddItem(m, Lib.HasDx12Arg(Game) ? "DX12 模式启动 ✓（点击改回 DX11）" : "DX12 模式启动（-use-d3d12）", "dx12");
            AddItem(m, "检测运行模式（现在跑 DX11 还是 DX12）", "dxapi");
            AddItem(m, "打开目录", "opendir");
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, Game.Favorite ? "取消收藏" : "收藏", "fav");
            AddItem(m, "重命名…", "rename");
            AddItem(m, Game.Private ? "取消私密" : "私密", "priv");
            AddItem(m, Game.Hidden ? "取消隐藏" : "隐藏", "hid");
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, "帧生成（切到该页并选中它）", "fg");
            // 分享版不显示封面，封面相关操作一并收起（免得菜单里出现点了没用的项）
            if (!Program.ShareBuild)
            {
                m.Items.Add(new ToolStripSeparator());
                AddItem(m, "设置封面…", "setcover");
                AddItem(m, "联网搜寻封面", "webcover");
                AddItem(m, "重新下载官方封面", "dlcover");
                AddItem(m, "打开封面目录", "coverdir");
            }
            m.Items.Add(new ToolStripSeparator());
            AddItem(m, "从列表移除", "remove");
        }

        void AddItem(ContextMenuStrip m, string text, string act)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += delegate
            {
                if (Chosen != null) Chosen(this, EventArgs.Empty);   // 先把自己变成"选中游戏"
                if (MenuAction != null) MenuAction(this, act);
            };
            m.Items.Add(it);
        }

        // 状态全文（悬浮提示 / 帧生成页用）
        static string StateText(DlssgGame g)
        {
            if (g.Ignored) return "已忽略";
            string dl = Dlssg030.ShortTag(g);
            if (dl.Length > 0) return dl;
            string xe = XeMfg.ShortTag(g);
            if (xe.Length > 0) return xe;
            if (g.Installed) return "已开启（" + g.Entry + "）";
            if (g.HasFrameGen) return "可接入（内置 FG）";
            return "无帧生成组件";
        }

        // 卡面上的短标签：一行要放"平台 · 状态"，所以用短词，详情交给悬浮提示
        static string StateShort(DlssgGame g)
        {
            if (g.Ignored) return "已忽略";
            if (Dlssg030.ShortTag(g).Length > 0) return "DLSS MFG " + Dlssg030.Ver;
            if (XeMfg.ShortTag(g).Length > 0) return "方案 A 注入";
            if (g.Installed) return "已开启";
            if (g.HasFrameGen) return "可接入";
            return "无组件";
        }

        static Color StateColor(DlssgGame g)
        {
            if (g.Ignored) return Theme.Warn;
            if (Dlssg030.ShortTag(g).Length > 0) return Theme.Ok;
            if (XeMfg.ShortTag(g).Length > 0 || g.Installed) return Theme.Ok;
            if (g.HasFrameGen) return Theme.Accent;
            return Theme.TextFaint;
        }

        // 卡面状态按卡缓存：PaintCard 每次重绘都各调一遍 StateShort/StateColor，底下是
        // 十几回 File.Exists/FileInfo（通用卡 ~12 次、二游最坏 ~20 次），悬停/滚动重绘成倍放大
        //（2026-09-30 体检）。所有改状态的入口（开启/关闭/换入口/扫描/导入）都会整表重建卡片，
        // 缓存随卡生灭不会过期；换主题整树重建同理。
        string shortCache;
        Color? colorCache;
        string StateShortCached() { if (shortCache == null) shortCache = StateShort(Game); return shortCache; }
        Color StateColorCached() { if (colorCache == null) colorCache = StateColor(Game); return colorCache.Value; }

        // 绘制失败绝不允许把卡片留成"白块打叉"：.NET 对 OnPaint 抛异常的控件就是这个表现
        // （2026-09-15 用户截图实测 16 张白框+红叉）。任何异常都退化成一块纯底色，至少不难看。
        static bool paintErrLogged;      // 绘制异常只记一条，避免刷屏
        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintCard(e.Graphics); }
            catch (Exception ex)
            {
                if (!paintErrLogged)
                {
                    paintErrLogged = true;
                    try { Program.Log("卡片绘制异常（已兜底为纯底色）: " + ex.GetType().Name + ": " + ex.Message + " | 栈: " + (ex.StackTrace == null ? "" : ex.StackTrace.Replace("\r\n", " <- ").Substring(0, Math.Min(420, ex.StackTrace.Length)))); } catch { }
                }
                try { using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, 0, 0, Width, Height); }
                catch { }
            }
        }

        // 卡片自带一份封面副本（见 CoverArt.Own）：窗口销毁时一并释放，避免 GDI 句柄泄漏
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (cover != null) cover.Dispose(); } catch { }
                // 解除 static ToolTip 对本卡片的强引用：不清的话旧卡片整棵树永远不可回收，
                // 逐键/逐次重建在会话内无上限累积（2026-09-30 体检）。右键菜单一并释放。
                try { Tip().SetToolTip(this, null); } catch { }
                var m = ContextMenuStrip; ContextMenuStrip = null;
                try { if (m != null) m.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // 分享版卡片：不画封面，画成一条信息行（名称 / 平台·状态 / 目录）。
        // 为什么：封面网格 + 渐变压字是"游戏启动器"的视觉，分享版的目标是工具 ——
        // 使用者在这里只需要认出"哪个游戏、跑在哪、装没装、在哪个目录"。
        // 颜色全部走主题（深字浅底），不像封面卡那样强制白字。
        void PaintInfoCard(Graphics g)
        {
            using (var bgf = new SolidBrush(BackColor)) g.FillRectangle(bgf, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var full = new Rectangle(0, 0, Width, Height);
            var edgeRect = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, full, Theme.RCard, Picked ? Theme.AccentSoft : Theme.Panel);
            int pad = Theme.S(11);

            string nm = Game.Ignored ? "[已忽略] " + Game.Title : Game.Title;
            TextRenderer.DrawText(g, nm, Theme.FB(10f),
                new Rectangle(pad, Theme.S(7), Width - pad * 2 - Theme.S(18), Theme.S(18)),
                Game.Ignored ? Theme.TextFaint : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Game.Favorite)
                TextRenderer.DrawText(g, "★", StarFont(),
                    new Rectangle(Width - Theme.S(24), Theme.S(6), Theme.S(18), Theme.S(16)),
                    Theme.Golden,
                    TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);

            // 平台 · 状态（状态用颜色点示意）
            int dot = Theme.S(5);
            using (var b = new SolidBrush(StateColorCached()))
                g.FillEllipse(b, pad, Theme.S(32), dot, dot);
            TextRenderer.DrawText(g, Lib.PlatformLabel(Game.Platform) + "  ·  " + StateShortCached(), MetaFont(),
                new Rectangle(pad + dot + Theme.S(6), Theme.S(26), Width - pad * 2 - dot - Theme.S(6), Theme.S(15)),
                Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Game.Dir != null && Game.Dir.Length > 0)
                TextRenderer.DrawText(g, Game.Dir, MetaFont(),
                    new Rectangle(pad, Height - Theme.S(19), Width - pad * 2, Theme.S(14)),
                    Theme.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Picked)
            {
                using (var glowP = new Pen(Color.FromArgb(40, Theme.Accent), 4f))
                using (var gpE = Theme.Round(edgeRect, Theme.RCard))
                    g.DrawPath(glowP, gpE);
                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Accent, 2.2f);
            }
            else if (hover) Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Accent, 1.6f);
            else Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Line, 1f);
        }

        void PaintCard(Graphics g)
        {
            if (Program.ShareBuild) { PaintInfoCard(g); return; }   // 分享版：信息行卡片，不画封面
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var full = new Rectangle(0, 0, Width, Height);
            var edgeRect = new Rectangle(0, 0, Width - 1, Height - 1);

            // 封面（自带圆角 alpha，1:1 贴上去，不做任何裁剪）
            if (cover != null) g.DrawImage(cover, full);
            else Theme.FillRound(g, full, Theme.RCard, Theme.Panel);

            // 底部多阶渐变遮罩：让标题在任何封面上都具有顶级可读性
            using (var gp = Theme.Round(full, Theme.RCard))
            using (var br = new LinearGradientBrush(full, Color.Black, Color.Black, 90f))
            {
                var blend = new System.Drawing.Drawing2D.ColorBlend(5);
                blend.Colors = new Color[] {
                    Color.FromArgb(0, 8, 12, 18),
                    Color.FromArgb(0, 8, 12, 18),
                    Color.FromArgb(70, 8, 12, 18),
                    Color.FromArgb(170, 8, 12, 18),
                    Color.FromArgb(235, 8, 12, 18) };
                blend.Positions = new float[] { 0f, 0.45f, 0.65f, 0.82f, 1f };
                br.InterpolationColors = blend;
                g.FillPath(br, gp);
            }

            // 悬停轻提亮
            if (hover && !Picked)
            {
                using (var ov = new SolidBrush(Color.FromArgb(18, 255, 255, 255)))
                using (var gpH = Theme.Round(full, Theme.RCard))
                    g.FillPath(ov, gpH);
            }

            // 选中态 / 悬停态外框
            if (Picked)
            {
                using (var glowP = new Pen(Color.FromArgb(40, Theme.Accent), 4f))
                using (var gpE = Theme.Round(edgeRect, Theme.RCard))
                    g.DrawPath(glowP, gpE);

                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Accent, 2.2f);

                using (var bp = new Pen(Color.White, 2f))
                {
                    int bl = Theme.S(8);
                    g.DrawLine(bp, edgeRect.X + 2, edgeRect.Y + 2, edgeRect.X + 2 + bl, edgeRect.Y + 2);
                    g.DrawLine(bp, edgeRect.X + 2, edgeRect.Y + 2, edgeRect.X + 2, edgeRect.Y + 2 + bl);
                    g.DrawLine(bp, edgeRect.Right - 2, edgeRect.Y + 2, edgeRect.Right - 2 - bl, edgeRect.Y + 2);
                    g.DrawLine(bp, edgeRect.Right - 2, edgeRect.Y + 2, edgeRect.Right - 2, edgeRect.Y + 2 + bl);
                }
            }
            else if (hover)
            {
                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Accent, 1.6f);
            }
            else
            {
                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Line, 1f);
            }

            int pad = Theme.S(11);

            // 名称（最多两行，超出省略）
            string nm = Game.Ignored ? "[已忽略] " + Game.Title : Game.Title;
            TextRenderer.DrawText(g, nm, NameFont(),
                new Rectangle(pad, Height - Theme.S(56), Width - pad * 2, Theme.S(32)),
                Color.White,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.WordBreak
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 底部平台与状态微胶囊
            int my = Height - Theme.S(21);
            
            // 平台小胶囊
            string platLabel = Lib.PlatformLabel(Game.Platform);
            Color platBg = Color.FromArgb(160, 20, 28, 40);
            Color platTxt = Color.FromArgb(200, 215, 230);
            if (platLabel.Contains("Steam")) { platBg = Color.FromArgb(180, 23, 37, 84); platTxt = Color.FromArgb(147, 197, 253); }
            else if (platLabel.Contains("Epic")) { platBg = Color.FromArgb(180, 30, 41, 59); platTxt = Color.FromArgb(226, 232, 240); }
            else if (platLabel.Contains("WeGame")) { platBg = Color.FromArgb(180, 120, 53, 15); platTxt = Color.FromArgb(253, 230, 138); }
            else if (platLabel.Contains("本地")) { platBg = Color.FromArgb(180, 6, 78, 59); platTxt = Color.FromArgb(110, 231, 183); }

            var pSz = TextRenderer.MeasureText(platLabel, MetaFont());
            int pW = pSz.Width + Theme.S(6);
            int pH = Theme.S(14);
            var pRect = new Rectangle(pad, my + Theme.S(1), pW, pH);
            Theme.FillRound(g, pRect, Theme.S(4), platBg);
            TextRenderer.DrawText(g, platLabel, MetaFont(), pRect, platTxt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 状态文字与小圆点
            int stX = pad + pW + Theme.S(6);
            int stDotSz = Theme.S(5);
            int stDotY = my + (pH - stDotSz) / 2 + 1;
            Color sc = StateColorCached();
            using (var b = new SolidBrush(sc))
                g.FillEllipse(b, stX, stDotY, stDotSz, stDotSz);

            TextRenderer.DrawText(g, StateShortCached(),
                MetaFont(), new Rectangle(stX + stDotSz + Theme.S(4), my, Width - stX - stDotSz - pad, Theme.S(16)),
                Color.FromArgb(220, 226, 235),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 收藏角标
            if (Game.Favorite)
            {
                TextRenderer.DrawText(g, "★", StarFont(),
                    new Rectangle(Width - Theme.S(26), Theme.S(5), Theme.S(20), Theme.S(18)),
                    Theme.Golden,
                    TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }
        }

        public void SetPicked(bool on)
        {
            if (Picked == on) return;
            Picked = on;
            Invalidate();
        }
    }

    public class Sec : Panel
    {
        // 行类型
        const int KRow = 0;     // 标签 + 控件（支持一行两组）
        const int KText = 1;    // 整行说明文字（按宽度换行）
        const int KBlock = 2;   // 整行控件（拉伸到内宽：ListView / RichTextBox / 进度条）
        const int KFlow = 3;    // 按钮行（FlowLayoutPanel，自适应）
        const int KBar = 4;     // 工具条一行：左侧输入框（拉伸）+ 右侧控件组（两端对齐）
        const int KGrid2 = 5;   // 两列等宽区块（仪表盘指标瓦片：CPU/内存 · GPU/温度）

        class Item
        {
            public int Kind;
            public Control A;       // 标签1 / 文字 / null
            public Control B;       // 控件1 / 块控件 / 按钮流
            public Control C;       // 标签2
            public Control D;       // 控件2
            public int FixedH;      // >0 时该行用固定高度（文字变化不影响分区高度）
        }

        int padX, padY, gap, labelW, top;
        Color contentBg;   // 行内容底色：盒式分区=Panel，工具条（Plain）=Bg。写死 Panel 会在灰底上留白补丁

        // 把"框外底色"确定性地下发给整棵子树
        void ApplySurface(Control c)
        {
            if (c == null) return;
            FlatBtn fb = c as FlatBtn; if (fb != null) fb.Corner = contentBg;
            RoundCheck rc = c as RoundCheck; if (rc != null) rc.Corner = contentBg;
            RoundRadio rr = c as RoundRadio; if (rr != null) rr.Corner = contentBg;
            RoundField rf = c as RoundField; if (rf != null) rf.SetCorner(contentBg);
            RoundCombo rb = c as RoundCombo; if (rb != null) rb.SetCorner(contentBg);
            foreach (Control ch in c.Controls) ApplySurface(ch);
        }
        // 分区色相表。顺序即优先级（先判的赢）：
        //   "伪装" / "帧生成" 要排在"游戏"前面 —— 标题里两处关键词并存时，
        //   语义更专的那个应该赢（如"帧生成操作（对「目标游戏」生效）"）。
        //   只用 5 个色相，主轴仍是强调绿，避免整页变成调色盘。
        static Color SecHue(string t)
        {
            if (t == null || t.Length == 0) return Theme.Accent;
            if (t.Contains("联动")) return Theme.Accent;
            if (t.Contains("伪装")) return Theme.Golden;                       // 伪装显卡＝警告级动作
            if (t.Contains("帧生成") || t.Contains("方案")) return Theme.Violet;
            if (t.Contains("负载") || t.Contains("监控") || t.Contains("曲线")
                || t.Contains("日志") || t.Contains("运行时") || t.Contains("实时")) return Theme.Cyan;
            if (t.Contains("游戏")) return Theme.Accent;
            if (t.Contains("健康") || t.Contains("体检")) return Theme.Ok;
            if (t.Contains("漂移") || t.Contains("告警") || t.Contains("风险") || t.Contains("安全")) return Theme.Warn;
            if (t.Contains("主题") || t.Contains("界面")) return Theme.Violet;
            if (t.Contains("操作") || t.Contains("快捷") || t.Contains("说明") || t.Contains("插件")
                || t.Contains("隐私") || t.Contains("须知") || t.Contains("技术")) return Theme.Indigo;
            if (t.Contains("系统") || t.Contains("硬件") || t.Contains("下载") || t.Contains("封面")
                || t.Contains("窗口") || t.Contains("托盘") || t.Contains("目录")) return Theme.Cyan;
            return Theme.Accent;
        }

        // 页首卡片（2026-09-20）：顶边加一条两端渐隐的色相线。一页里的第一张卡
        //   因此有了"领头"的身份，其余卡片保持安静 —— 层级不靠阴影堆，靠这一笔。
        public bool Hero;

        public bool Plain;                 // 工具条：不画分区底与描边、不做标签列缩进
        int titleY, titleH;          // 标题位置（OnPaint 画强调条用）
        readonly List<Item> items = new List<Item>();
        string secTitle = "";

        public Sec(string title)
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Theme.Bg;
            padX = Theme.S(18); padY = Theme.S(14); gap = Theme.S(10); labelW = Theme.S(102);
            Width = Theme.S(600);
            secTitle = title == null ? "" : title;

            // 无标题 = 工具条模式
            if (secTitle.Length == 0)
            {
                Plain = true;
                contentBg = Theme.Bg;
                labelW = 0;
                padY = Theme.S(2);
                top = Theme.S(4);
                Height = top + padY;
                return;
            }

            int t = Theme.S(13);
            var lb = new Label();
            lb.AutoSize = true; lb.UseMnemonic = false; lb.BackColor = Theme.Panel;
            lb.Text = title; lb.Font = Theme.FB(10f); lb.ForeColor = Theme.Text;
            lb.Location = new Point(padX + Theme.S(34), t);   // 让位给 26px 图标胶囊
            Controls.Add(lb);
            titleY = t; titleH = lb.Height;
            top = t + lb.Height + Theme.S(12);
            Height = top + padY;
            contentBg = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var __bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Plain) return;

            // 柔和微投影
            Theme.SoftShadow(g, r, Theme.RPanel, Theme.S(3));

            // 卡片底板
            Theme.FillRound(g, r, Theme.RPanel, Theme.Panel);

            // 顶边微高光线
            if (Theme.Dark)
            {
                using (var pLight = new Pen(Color.FromArgb(14, 255, 255, 255), 1f))
                {
                    int arcD = Theme.RPanel * 2;
                    g.DrawArc(pLight, r.X, r.Y, arcD, arcD, 180, 90);
                    g.DrawLine(pLight, r.X + Theme.RPanel, r.Y, r.Right - Theme.RPanel, r.Y);
                    g.DrawArc(pLight, r.Right - arcD, r.Y, arcD, arcD, 270, 90);
                }
            }

            Theme.EdgeRound(g, r, Theme.RPanel, Theme.Line, 1f);

            if (Hero)
            {
                Color hc = SecHue(secTitle);
                int hx = Theme.RPanel, hw = Math.Max(1, Width - Theme.RPanel * 2);
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), 1),
                                                         Color.FromArgb(0, hc), hc, 0f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Color.FromArgb(0, hc), hc, Color.FromArgb(0, hc) };
                    cb.Positions = new float[] { 0f, 0.5f, 1f };
                    br.InterpolationColors = cb;
                    g.FillRectangle(br, hx, Theme.S(1), hw, Theme.S(2));
                }
            }

            // 标题左侧：图标胶囊（2026-09-20）。以前是一枚裸的绿色小图标，
            //   配上清一色的白卡，整页就是"排排坐"（用户："界面还是很单调"）。
            //   现在色相按分区语义分配，并垫一层低透明度同色底 —— 卡片之间有节奏了。
            Color markCol = SecHue(secTitle);
            int chipSz = Theme.S(26);
            int chipX = padX;
            int chipY = titleY + (titleH - chipSz) / 2;
            Theme.FillRound(g, new Rectangle(chipX, chipY, chipSz, chipSz), Theme.RChip, Theme.ChipBg(markCol));
            Theme.EdgeRound(g, new Rectangle(chipX, chipY, chipSz, chipSz), Theme.RChip,
                            Color.FromArgb(Theme.Dark ? 62 : 40, markCol), 1f);
            int iconSz = Theme.S(15);
            var iconRect = new Rectangle(chipX + (chipSz - iconSz) / 2, chipY + (chipSz - iconSz) / 2, iconSz, iconSz);

            if (secTitle.Contains("联动") || secTitle.Contains("游戏"))
                Theme.DrawIconGamepad(g, iconRect, markCol);
            else if (secTitle.Contains("负载"))
                Theme.DrawIconPulse(g, iconRect, markCol);
            else if (secTitle.Contains("操作"))
                Theme.DrawIconLightning(g, iconRect, markCol);
            else if (secTitle.Contains("帧生成") || secTitle.Contains("方案") || secTitle.Contains("运行时"))
                Theme.DrawIconChip(g, iconRect, markCol);
            else if (secTitle.Contains("健康"))
                Theme.DrawIconSpeedometer(g, iconRect, markCol);
            else if (secTitle.Contains("系统") || secTitle.Contains("硬件") || secTitle.Contains("监控")
                     || secTitle.Contains("曲线") || secTitle.Contains("日志"))
                Theme.DrawIconPulse(g, iconRect, markCol);
            else if (secTitle.Contains("设置") || secTitle.Contains("主题") || secTitle.Contains("界面")
                     || secTitle.Contains("窗口") || secTitle.Contains("托盘") || secTitle.Contains("目录"))
                Theme.DrawIconGear(g, iconRect, markCol);
            else if (secTitle.Contains("说明") || secTitle.Contains("技术") || secTitle.Contains("须知")
                     || secTitle.Contains("反作弊") || secTitle.Contains("隐私") || secTitle.Contains("数据"))
                Theme.DrawIconBook(g, iconRect, markCol);
            else
            {
                Theme.FillRound(g, new Rectangle(iconRect.X + iconSz / 4, iconRect.Y + iconSz / 4, iconSz / 2, iconSz / 2), Theme.S(3), markCol);
            }
        }

        Label MkLabel(string text, Font f, Color c)
        {
            var l = new Label();
            l.AutoSize = true; l.UseMnemonic = false; l.BackColor = contentBg;
            l.Text = text; l.Font = f; l.ForeColor = c; l.Margin = Padding.Empty;
            Controls.Add(l);
            return l;
        }

        // 工具条一行：左输入框拉伸、右控件组靠右（对应启动器 #topbar 的 space-between）
        public void Bar2(Control left, params Control[] right)
        {
            var fl = new FlowLayoutPanel();
            fl.FlowDirection = FlowDirection.LeftToRight;
            fl.WrapContents = false;
            fl.AutoSize = true;
            fl.BackColor = contentBg;
            fl.Margin = Padding.Empty;
            fl.Padding = Padding.Empty;
            if (right != null)
                foreach (Control c in right)
                {
                    c.Margin = new Padding(Theme.S(8), 0, 0, 0);
                    if (c is RoundField) ((RoundField)c).SetCorner(contentBg);   // 圆角外四角随工具条底色，不露白
                    if (c is RoundCombo) ((RoundCombo)c).SetCorner(contentBg);
                    fl.Controls.Add(c);
                }
            Controls.Add(fl);
            if (left != null)
            {
                if (left is RoundField) ((RoundField)left).SetCorner(contentBg);
                if (left is RoundCombo) ((RoundCombo)left).SetCorner(contentBg);
                Controls.Add(left);
            }
            var it = new Item(); it.Kind = KBar; it.B = left; it.D = fl;
            items.Add(it);
        }

        // 标签 + 控件（同一行，垂直居中）
        public Label Row(string caption, Control field)
        {
            return Pair(caption, field, 0, null, null, 0);
        }
        public Label Row(string caption, Control field, int fieldW)
        {
            return Pair(caption, field, fieldW, null, null, 0);
        }
        // 一行两组：标签1+控件1（左半）  标签2+控件2（右半）
        public Label Pair(string cap1, Control f1, int w1, string cap2, Control f2, int w2)
        {
            var it = new Item(); it.Kind = KRow;
            if (cap1 != null && cap1.Length > 0) it.A = MkLabel(cap1, Theme.F(9f), Theme.TextDim);
            if (f1 != null) { if (w1 > 0) f1.Width = w1; Controls.Add(f1); it.B = f1; }
            if (cap2 != null && cap2.Length > 0) it.C = MkLabel(cap2, Theme.F(9f), Theme.TextDim);
            if (f2 != null) { if (w2 > 0) f2.Width = w2; Controls.Add(f2); it.D = f2; }
            items.Add(it);
            return it.A as Label;
        }
        // 整行说明文字（自动换行，高度按当前宽度实测）
        public Label Body(string text)
        {
            var l = MkLabel(text, Theme.F(9f), Theme.TextDim);
            l.AutoSize = false;
            // ★ 回指所属分区：SetBody() 运行期改文案时靠它找到本分区并重排。
            //   忘了这一行，说明文字一变长就会被旧高度裁掉（而且不报错，只能靠肉眼发现）。
            l.Tag = this;
            var it = new Item(); it.Kind = KText; it.A = l;
            items.Add(it);
            return l;
        }
        // 固定高度的说明行：文字变化不改变分区高度 → 不触发整页重排。
        // 选中游戏状态行必须用它（否则每次点卡片都会改高度 → 整页重排 → 滚动位置被重算）。
        // ★ 刻意不设 Tag：SetBody 找不到所属分区就不会调 Resync。
        public Label BodyFixed(string text, int lines)
        {
            var l = MkLabel(text, Theme.F(9f), Theme.TextDim);
            l.AutoSize = false;
            l.AutoEllipsis = true;
            var it = new Item();
            it.Kind = KText;
            it.A = l;
            it.FixedH = Math.Max(1, lines) * (TextRenderer.MeasureText("测", l.Font).Height + Theme.S(2));
            items.Add(it);
            return l;
        }

        // 整行控件（拉伸到分区内宽）
        public T Block<T>(T c, int h) where T : Control
        {
            if (h > 0) c.Height = h;
            Controls.Add(c);
            var it = new Item(); it.Kind = KBlock; it.B = c;
            items.Add(it);
            return c;
        }
        // 两列等宽区块：把四条一模一样的横条换成 2×2 指标瓦片
        public void Block2(Control a, Control b, int h)
        {
            if (a == null || b == null) return;
            if (h > 0) { a.Height = h; b.Height = h; }
            Controls.Add(a); Controls.Add(b);
            var it = new Item(); it.Kind = KGrid2; it.B = a; it.D = b;
            items.Add(it);
        }

        // 按钮行
        public void Buttons(params Control[] btns)
        {
            var fl = new FlowLayoutPanel();
            fl.FlowDirection = FlowDirection.LeftToRight;
            fl.WrapContents = false;
            fl.AutoSize = true;
            fl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            fl.BackColor = contentBg;
            fl.Margin = Padding.Empty;
            fl.Padding = Padding.Empty;
            foreach (var b in btns)
            {
                if (b == null) continue;
                b.Margin = new Padding(0, 0, Theme.S(8), 0);
                fl.Controls.Add(b);
            }
            Controls.Add(fl);
            var it = new Item(); it.Kind = KFlow; it.B = fl;
            items.Add(it);
        }

        // 纵向排布：唯一算 y 的地方
        public void DoLayout()
        {
            int w = Width - padX * 2;
            if (w < Theme.S(200)) w = Theme.S(200);
            int y = top;
            int half = Width / 2;

            ApplySurface(this);      // 每次排版都把真实分区底色下发一遍（廉价且不会漏）

            foreach (Item it in items)
            {
                if (it.Kind == KText)
                {
                    var l = it.A;
                    if (l == null) continue;
                    if (it.FixedH > 0)
                    {
                        // 固定行高：文字只做省略，不参与高度推导
                        l.AutoSize = false;
                        l.Height = it.FixedH;
                        l.Width = w;
                        l.Location = new Point(padX, y);
                        y += l.Height + gap;
                        continue;
                    }
                    // 换行高度用 TextRenderer 自己量（含 \n 与自动折行），不依赖 Label.PreferredHeight
                    // 的内部实现 —— 量出来的高度和 Label 实际渲染用同一套 GDI 度量，必然一致。
                    var sz = TextRenderer.MeasureText(l.Text, l.Font, new Size(w, int.MaxValue),
                                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    l.AutoSize = false;
                    l.Height = sz.Height + Theme.S(2);
                    l.Width = w;
                    l.Location = new Point(padX, y);
                    y += l.Height + gap;
                    continue;
                }
                if (it.Kind == KBlock || it.Kind == KFlow)
                {
                    var b = it.B;
                    if (b == null) continue;
                    if (it.Kind == KBlock) b.Width = w;
                    b.Location = new Point(padX, y);
                    y += b.Height + gap;
                    continue;
                }
                if (it.Kind == KBar)
                {
                    var lc = it.B; var rc = it.D;
                    int hh = 0;
                    if (lc != null) hh = Math.Max(hh, lc.Height);
                    if (rc != null) hh = Math.Max(hh, rc.PreferredSize.Height);
                    if (hh == 0) continue;
                    if (rc != null)
                    {
                        // ⚠ 高度必须显式给：FlowLayoutPanel.AutoSize 的 PreferredSize 在首次排版时
                        //   可能还没算出来（Height=0），整组控件就会"隐形"（本轮实测踩到）。
                        rc.Width = rc.PreferredSize.Width;
                        rc.Height = Math.Max(hh, Theme.S(28));
                        rc.Left = Math.Max(padX, Width - padX - rc.Width);
                        rc.Top = y + (hh - rc.Height) / 2;
                    }
                    if (lc != null)
                    {
                        int lw = Width - padX * 2 - (rc != null ? rc.Width + Theme.S(20) : 0);
                        if (lw < Theme.S(140)) lw = Theme.S(140);
                        lc.Width = lw;
                        lc.Left = padX;
                        lc.Top = y + (hh - lc.Height) / 2;
                    }
                    y += hh + gap;
                    continue;
                }
                if (it.Kind == KGrid2)
                {
                    var g1 = it.B; var g2 = it.D;
                    int gh = 0;
                    if (g1 != null) gh = Math.Max(gh, g1.Height);
                    if (g2 != null) gh = Math.Max(gh, g2.Height);
                    if (gh == 0) continue;
                    int gc = Theme.S(12);
                    int cw = (w - gc) / 2;
                    if (g1 != null) { g1.Width = cw; g1.Location = new Point(padX, y); }
                    if (g2 != null) { g2.Width = w - cw - gc; g2.Location = new Point(padX + cw + gc, y); }
                    y += gh + gap;
                    continue;
                }
                // KRow
                int h = 0;
                if (it.A != null && it.A.Height > h) h = it.A.Height;
                if (it.B != null && it.B.Height > h) h = it.B.Height;
                if (it.C != null && it.C.Height > h) h = it.C.Height;
                if (it.D != null && it.D.Height > h) h = it.D.Height;
                if (h == 0) continue;
                if (it.A != null) { it.A.Left = padX; it.A.Top = y + (h - it.A.Height) / 2; }
                // ★ 标签宽度按真实文字量算，不能用固定值 labelW（102px）：
                //   标签是不透明底色（contentBg），比 labelW 长的标题会**直接盖住右侧控件** ——
                //   用户截图里那个只显示 "n.dll" 的下拉就是这么来的（"version.dll" 左半被标题糊掉）。
                int lwA = (it.A == null) ? 0 : Math.Max(labelW, it.A.Width + Theme.S(8));
                if (it.B != null)
                {
                    int avail = Width - padX * 2 - lwA;
                    if (avail > Theme.S(48) && it.B.Width > avail) it.B.Width = avail;   // 放不下就缩，宁可窄也不重叠
                    it.B.Left = (it.A == null) ? padX : padX + lwA;
                    it.B.Top = y + (h - it.B.Height) / 2;
                }
                if (it.C != null) { it.C.Left = half; it.C.Top = y + (h - it.C.Height) / 2; }
                if (it.D != null) { it.D.Left = half + labelW; it.D.Top = y + (h - it.D.Height) / 2; }
                y += h + gap;
            }
            int nh = y - gap + padY;
            if (nh != Height) Height = nh;
            Invalidate();
        }

        // 运行期文案变化后重排：先按新文字量出自己的新高度，再让所属页面把后面的分区一起下移。
        // 只做 DoLayout 而不通知 Pg，本分区变高了但后面分区的 y 不变 -> 会互相压住。
        public void Resync()
        {
            DoLayout();
            var p = Parent as Pg;
            if (p != null) p.Reflow();
        }
    }

    // ======================= 自绘细滚动条 =======================
    //  为什么不用原生 VScrollBar：它在深色下仍是**系统白皮** —— uxtheme 的 DarkMode_Explorer
    //  对独立滚动条控件不生效（用户截图：深色页面右缘一条纯白滑条）。
    //  既然滚动已经由 Pg 自己管，滚动条也就自绘：10px 细条 + 圆角滑块，
    //  取值对齐 DLSS 5 Swapper 的 --scroll（浅色 rgba(16,24,32,.20)、深色 rgba(255,255,255,.13)）。
    public class FlatScroll : Control
    {
        public int Minimum = 0;
        public int Maximum = 1;        // 约定同 VScrollBar：可达范围 [Minimum, Maximum-LargeChange+1]
        public int LargeChange = 1;
        int val;
        bool hover, drag;
        int dragY, dragVal;
        Anim aShow;                       // 滑块的"显形度"：常态压到很淡，鼠标进来 110ms 浮上来

        public event EventHandler ValueChanged;

        public FlatScroll()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Width = Theme.S(10);
            aShow = new Anim(this, Motion.Fast, Motion.Decel);
            aShow.Snap(0);
        }

        public int MaxValue { get { int m = Maximum - LargeChange + 1; return m < 0 ? 0 : m; } }

        public int Value
        {
            get { return val; }
            set
            {
                int nv = Math.Max(Minimum, Math.Min(value, MaxValue));
                if (nv == val) return;
                val = nv;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        int ThumbLen()
        {
            int track = Height - Theme.S(4);
            if (track <= 0) return 0;
            int total = Math.Max(1, Maximum + 1);
            int len = (int)((long)track * LargeChange / total);
            int min = Theme.S(28);
            return Math.Max(min, Math.Min(len, track));
        }

        int ThumbTop()
        {
            int track = Height - Theme.S(4);
            int len = ThumbLen();
            int room = track - len;
            if (room <= 0 || MaxValue <= 0) return Theme.S(2);
            return Theme.S(2) + (int)((long)room * (val - Minimum) / MaxValue);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; aShow.To(1.0); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; if (!drag) aShow.To(0.0); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int top = ThumbTop(), len = ThumbLen();
            if (e.Y >= top && e.Y <= top + len)          // 抓滑块
            {
                drag = true; dragY = e.Y; dragVal = val;
                aShow.To(1.0);
            }
            else                                          // 点轨道翻一页
            {
                Value = val + (e.Y < top ? -LargeChange : LargeChange);
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!drag) return;
            int track = Height - Theme.S(4) - ThumbLen();
            if (track <= 0 || MaxValue <= 0) return;
            double perPx = (double)MaxValue / track;
            Value = dragVal + (int)Math.Round((e.Y - dragY) * perPx);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            drag = false;
            aShow.To(hover ? 1.0 : 0.0);
        }

        // 滚轮落在滚动条上时交给所属页面处理（WinForms 不会自动冒泡）
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Pg pg = Parent as Pg;
            if (pg != null) { pg.ScrollByWheel(e.Delta); return; }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor))
                g.FillRectangle(bg, 0, 0, Width, Height);
            int len = ThumbLen();
            if (len <= 0) return;
            int top = ThumbTop();
            // 显形度：拖拽时永远满（用户正在操作它），否则跟着 hover 补间
            double sp = drag ? 1.0 : aShow.Value;
            int aLo = Theme.Dark ? 33 : 51, aHi = Theme.Dark ? 56 : 87;
            int aA = (int)Math.Round(aLo + (aHi - aLo) * sp);
            Color c = Theme.Dark
                ? Color.FromArgb(aA, 255, 255, 255)
                : Color.FromArgb(aA, 16, 24, 32);
            var r = new Rectangle(Theme.S(3), top, Math.Max(Theme.S(4), Width - Theme.S(6)), len);
            Theme.FillRound(g, r, r.Width / 2, c);
        }
    }

    // ======================= 页面容器（纵向堆叠 + 自适应宽度） =======================
    public class Pg : Panel
    {
        readonly List<Sec> secs = new List<Sec>();
        readonly List<HeadBox> heads = new List<HeadBox>();
        bool busy, pending;
        public int GapSize = Theme.S(14);
        // ★ 页面顶部"消融"总长（2026-09-20 二稿）：页头只有 58px 高，装不下一条够长的
        //   白→灰过渡；把剩下的一段交给 Pg 自己的顶部渐变接着画，两段同曲线。
        //   用户原话"色块颜色变化还是不够丝滑"—— 17px 的渐变在这个尺度上就是个"糊掉的小段"。
        public int TopFadeLen = Theme.S(110);

        // ── 滚动：自己管，不再用 WinForms 的 AutoScroll ────────────────────────────
        //  为什么弃用：AutoScroll 的滚动区是按"子控件并集"**缓存**出来的，
        //  DisplayRectangle / VerticalScroll.Maximum 经常停在旧值上，已经为它打了四轮补丁
        //  （多出空白、跳回顶部、点不动下面）。2026-09-15 实测残留：从全屏退回窗口后
        //  DisplayRect={1686×892} 而客户区只有 960×892 → 滚动条消失、AutoScrollPosition
        //  也设不动 → 下半页永远看不到也点不到（用户截图："切全屏后无法选下面的游戏"）。
        //  现在：AutoScroll=false + 一个真的 VScrollBar，LayoutOnce 里把子控件整体上移 ScrollY。
        //  子控件是被**真的移动**的，命中测试自然跟着走，不需要任何"缓存校正"。
        readonly FlatScroll sb = new FlatScroll();
        bool laying;                                  // LayoutOnce 期间禁止滚动事件再进来（否则递归）

        public int ScrollY { get { return sb.Value; } }
        public void SetScrollY(int v) { try { sb.Value = v; } catch { } }   // FlatScroll 自己夹紧

        // ---- 滚轮节流 + 停手补画（v3.9.0）-----------------------------------------
        // 用户 2026-09-20 再次反馈：「上下滑太快还是会有有的选项变成空白」。
        // 之前几轮已经把"漏画"该堵的都堵了（改真滚动条、HardRepaint 整棵子树、自绘逐格自检+自愈），
        // 但**根上的开销没降**：每个 WM_MOUSEWHEEL 都立刻"搬一遍所有子控件 + 整棵子树 RDW_UPDATENOW"。
        // 滚轮/触控板一秒能发上百个事件（高分辨率滚轮更密），绘制把消息泵吃满，
        // 来不及画的那几帧就留下"背景铺了、内容没画"的死区 —— 正是用户描述的现象。
        // 做法：事件只记账，15ms（≈60fps）统一应用一次；停手 120ms 后再补一次完整重绘兜底。
        //  15ms 的延迟人眼看不出来，但每一帧都是完整的。
        int wheelAccum;
        int lastWheelAt;
        static Timer wheelTimer;
        static readonly List<Pg> wheelPending = new List<Pg>();

        public void ScrollByWheel(int delta)
        {
            if (!sb.Visible) return;
            wheelAccum += delta;
            lastWheelAt = Environment.TickCount;
            if (!wheelPending.Contains(this)) wheelPending.Add(this);
            EnsureWheelTimer();
        }

        void ApplyWheel()
        {
            if (wheelAccum == 0) return;
            int d = wheelAccum; wheelAccum = 0;
            int step = Theme.S(56) * Math.Max(1, Math.Abs(d) / SystemInformation.MouseWheelScrollDelta);
            SetScrollY(sb.Value + (d > 0 ? -step : step));
        }

        static void EnsureWheelTimer()
        {
            if (wheelTimer != null) { if (!wheelTimer.Enabled) wheelTimer.Start(); return; }   // 空表自停后由滚轮重启
            wheelTimer = new Timer();
            wheelTimer.Interval = 15;
            wheelTimer.Tick += delegate
            {
                for (int i = wheelPending.Count - 1; i >= 0; i--)
                {
                    Pg p = wheelPending[i];
                    try
                    {
                        if (p == null || p.IsDisposed) { wheelPending.RemoveAt(i); continue; }
                        p.ApplyWheel();
                        // 停手 120ms 后补一次"整棵子树立刻重绘"：节流期间万一有帧被系统合并掉，
                        //   这一步保证留在屏幕上的是完整画面（TickCount 会回绕，差值必须 unchecked）
                        if (unchecked(Environment.TickCount - p.lastWheelAt) > 120)
                        {
                            p.lastWheelAt = 0;
                            wheelPending.RemoveAt(i);
                            Theme.HardRepaint(p);
                        }
                    }
                    catch { try { wheelPending.RemoveAt(i); } catch { } }
                }
                // 所有页面都停手收尾后停表（与动效泵"没有动画就立刻停表"同一约束，2026-09-30 体检：
                // 此前 15ms 定时器首滚后永久 66 次/秒空转）；下一次滚轮 EnsureWheelTimer 会重启
                if (wheelPending.Count == 0) wheelTimer.Stop();
            };
            wheelTimer.Start();
        }

        public Pg()
        {
            BackColor = Theme.Bg;
            AutoScroll = false;
            // 双缓冲：本容器与子控件都开，避免滚动后留空白带 / 残像
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint, true);
            sb.Dock = DockStyle.Right;
            sb.Minimum = 0;
            sb.ValueChanged += delegate { if (!laying) LayoutOnce(sb.Visible); };
            Controls.Add(sb);
        }
        bool heroAssigned;
        public void Add(Sec s)
        {
            // 每页的**第一张真卡片**（跳过工具条）自动成为"领头卡"
            if (!heroAssigned && s != null && !s.Plain) { s.Hero = true; heroAssigned = true; }
            secs.Add(s); Controls.Add(s);
        }
        public void AddHead(string title, string sub)
        {
            var h = new HeadBox(title, sub);
            h.Tag = heads.Count;
            // 第一条页头在页面顶端（内容坐标 y=0），与"窗口顶部的白带"连成一片，
            // 用长消融；其余页头（实时负载 / 快速操作 …）在灰色页面中间，用自身高度的消融。
            if (heads.Count == 0) h.FadeLen = TopFadeLen;
            heads.Add(h);
            Controls.Add(h);
            // 页头通栏后要和右侧滚动条抢 z-order：把它顶到最前面，
            // 否则竖滚动条会在顶带里露出一条，把通栏白带又切开
            h.BringToFront();
        }
        int lastW = -1, lastCount = -1, lastTotal = -1;
        static bool healLogged;

        // ★ 布局自检：确认"算出来的位置"真的落到控件上了。
        //   为什么必须有这道检查：早退闸原先只看三个缓存数（宽度 / 分区数 / 内容总高），
        //   而这三个数是在 LayoutOnce ***跑完之后*** 才写进去的 —— 万一某次布局在中途被打断
        //   （或某个分区的高度被算成 0），缓存可能已经写成"最终值"，控件却还停在坏位置上。
        //   此后每次 Reflow 都命中早退闸 → 坏布局**永久**留在屏幕上。
        //   用户截图佐证：游戏库页顶部横着一条被压到十几像素高的封面带（子控件会被裁到父容器
        //   范围内，所以看起来就是"封面顶端的一条"），全屏后出现且不消失。
        bool GeometryLooksRight()
        {
            try
            {
                if (ClientSize.Width <= 0) return true;
                int sy = sb.Value;
                int y = 0;
                foreach (HeadBox hb in heads)
                {
                    if (hb.Height < Theme.S(20)) return false;      // 高度塌了
                    if (hb.Top + sy != y) return false;              // 位置没落到算出来的地方
                    y += hb.Height + Theme.S(10);
                }
                foreach (Sec s in secs)
                {
                    if (s.Height < Theme.S(30)) return false;        // 分区被压扁（这条能抓住"封面带"）
                    if (s.Top + sy != y) return false;
                    y += s.Height + GapSize;
                }
                return true;
            }
            catch { return true; }
        }

        public void Reflow()
        {
            if (busy) { pending = true; return; }
            SyncScrollRange();
            // ★ 宽度 / 分区数量 / 内容总高都没变 → 通常不做任何重排。
            //   少了这道闸，每次改一行文字（如点卡片刷新状态行）都会整页重排并重设滚动位置，
            //   表现就是"点一下游戏图标强制跳回最上面"（2026-09-14 用户反馈）。
            //   但缓存一致 **不等于** 布局正确，所以还要过一遍几何自检；不一致就强制重排。
            if (lastCount == secs.Count && lastW == ClientSize.Width && lastTotal == ContentHeight())
            {
                if (GeometryLooksRight()) return;
                if (!healLogged)
                {
                    healLogged = true;
                    try
                    {
                        string detail = "";
                        foreach (Sec s in secs) detail += " [" + s.Height + "@" + s.Top + "]";
                        Program.Log("[布局自检] 缓存值一致但控件几何不对 → 已强制重排"
                                    + "（Pg " + ClientSize.Width + "x" + ClientSize.Height
                                    + " 内容高=" + ContentHeight() + " 分区数=" + secs.Count
                                    + " 分区高:位置 =" + detail + " ScrollY=" + sb.Value + "）");
                    }
                    catch { }
                }
            }
            busy = true;
            try
            {
                // 竖滚动条出现/消失会改可用宽度 → 行数可能变一，所以最多迭代 4 轮收敛
                bool assumedV = sb.Visible;
                for (int pass = 0; pass < 4; pass++)
                {
                    pending = false;
                    LayoutOnce(assumedV);
                    if (!pending && sb.Visible == assumedV) break;
                    assumedV = sb.Visible;
                }
                lastW = ClientSize.Width;
                lastCount = secs.Count;
                lastTotal = ContentHeight();
            }
            finally { busy = false; }
        }

        // 滚动条按"内容总高 vs 视口高"定价。VScrollBar 的可达范围是
        //  [Minimum, Maximum - LargeChange + 1]，所以 Maximum 给 want-1 时最大滚动量正好 = want-view。
        void SyncScrollRange()
        {
            try
            {
                int want = Math.Max(1, ContentHeight());
                int view = Math.Max(1, ClientSize.Height);
                sb.Visible = want > view;
                sb.Maximum = Math.Max(0, want - 1);
                sb.LargeChange = Math.Max(1, Math.Min(view, want));
                if (sb.Value > want - view) sb.Value = Math.Max(0, want - view);
            }
            catch { }
        }

        public int ContentHeightPublic() { return ContentHeight(); }

        // 内容总高 = 各分区已算好的高度之和（DoLayout 之后才有意义）
        int ContentHeight()
        {
            int h = 0;
            foreach (HeadBox b in heads) h += b.Height + Theme.S(10);
            foreach (Sec s in secs) h += s.Height + GapSize;
            return h;
        }

        void LayoutOnce(bool reserveVScroll)
        {
            // 预留竖向滚动条宽度：不预留时内容宽度刚好等于客户区宽度，竖条一出现就
            // 把内容挤出横向滚动条（用户截图底部那条多余的横条）
            int vw = reserveVScroll ? sb.Width : 0;
            int avail = ClientSize.Width - GapSize * 2 - vw;
            if (avail < Theme.S(360)) avail = Theme.S(360);

            laying = true;
            try
            {
            int sy = sb.Value;          // 自管滚动：子控件按 -sy 真的上移
            int y = 0;
            foreach (HeadBox hb in heads)
            {
                // ★ 页头通栏（2026-09-20）：以前这里和分区一样按 GapSize 缩进，于是页头白底
                //   左（右）边各让出 14px，露出 Pg 的灰底 —— 顶部白带被切出两条灰竖条。
                //   页头铺满 Pg 全宽后，白色从侧栏右边界一直连到窗口右边；
                //   文字的左缩进改到 HeadBox.OnPaint 里单独给（只缩文字不缩底）。
                hb.Width = ClientSize.Width;
                hb.Location = new Point(0, y - sy);
                y += hb.Height + Theme.S(10);
            }
            foreach (Sec s in secs)
            {
                s.Width = avail;
                s.DoLayout();              // 先给宽度再排版（换行文字要用宽度）
                // 分区高度不该小于一个空分区的最小值；真的塌了就按内容重算一次再定位，
                //  否则它会以"压扁"的姿态留在页面上（用户截图里的封面带就是这么来的）。
                if (s.Height < Theme.S(30))
                {
                    s.Height = Math.Max(s.Height, Theme.S(30));
                    s.DoLayout();
                }
                s.Location = new Point(GapSize, y - sy);
                y += s.Height + GapSize;
            }
            SyncScrollRange();            // 内容高定下来 → 滚动条定价（可能翻转 Visible，Reflow 会再跑一轮）
            // ★ 这里原来是 `foreach (Sec s in secs) s.Invalidate();` —— Invalidate(false) = **不重绘子控件**。
            //   而滚动是"真的搬窗口"（上面 s.Location = y - sy），子控件的像素全靠 OS 搬窗口时
            //   顺手带过去。搬得比画得快、或某一次没带全，就留下"背景铺了、文字没画"的死区，
            //   而且会一直保留 —— 用户两张截图（某行只剩名称列 / 整片空白）正是这个形态。
            //   改成整棵子树立刻重绘：RDW_ALLCHILDREN 把子孙一起算上，RDW_UPDATENOW 立刻上屏
            //   （滚动时消息泵忙着消化下一格滚轮，WM_PAINT 会被饿死 = 用户说的"文字显示过慢"）。
            Theme.HardRepaint(this);
            }
            finally { laying = false; }
        }

        // 任何子控件改尺寸都会进这里 → 顺手把滚动条重新定价（幂等，且避免分区内部改高度时漏更新）
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            SyncScrollRange();
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            Reflow();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollByWheel(e.Delta);
            base.OnMouseWheel(e);
        }

        // 页面底色：顶部 TopFadeLen 像素走"白→灰"的同一条曲线（接住页头那条消融的尾巴），
        // 再往下才是纯 Bg。
        // ⚠ 锚在**内容坐标**（减掉 ScrollY），不是视口坐标：页头本身随内容移动，
        //   渐变跟着内容一起滚走，两者永远同一条曲线；若锚视口，滚动后视口顶部会
        //   留一条"发亮的横带"（内容已经移走了，渐变还赖在顶上）。
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var r = ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            var g = e.Graphics;
            // ⚠ 这行**不能**包 using（2026-09-20 事故，1.0.1 就是它引入的）：
            //   Theme.Solid 返回的是**缓存字典里的共享刷子**，using 会在离开作用域时把它 Dispose，
            //   而字典里仍留着这个尸体 —— 此后每一次页面底色重绘都在已销毁的 GDI+ 句柄上作图，
            //   抛 ArgumentException「参数无效」并**中断整页绘制**。
            //   用户侧表现：滚动/切页后内容整片消失（只剩零星残影），屏幕上留下红色绘制失败标记；
            //   探针复现的堆栈正是 `Fluxion.Pg.OnPaintBackground` → `Graphics.FillRectangle`。
            //   共享缓存对象一律**只取用、不释放**。
            g.FillRectangle(Theme.Solid(Theme.Bg), r);
            int sy = sb.Value;
            if (sy >= TopFadeLen) return;                       // 已经滚过去了，整屏 Bg
            int vis = Math.Min(r.Height, TopFadeLen - sy);
            if (vis <= 1) return;
            using (var br = Theme.VFade(new Rectangle(0, -sy, r.Width, TopFadeLen), Theme.Panel, Theme.Bg, 12))
                g.FillRectangle(br, 0, 0, r.Width, vis);
        }
    }

    // ======================= 主窗口 =======================
    public class MainForm : Form
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_DROPFILES = 0x0233;
        const int NavW = 176;
        const int FootH = 30;

        Config Cfg;
        Control rootPanel;      // 外层骨架：换主题时要整棵摘掉重建
        Panel content;
        // 页面索引常量：加页时不用再改散落各处的裸数字（v2.5.0 从 5 页扩到 7 页）
        const int P_DASH = 0, P_OPT = 1, P_FG = 2, P_LIB = 3, P_MON = 4, P_HELP = 5, P_SET = 6;
        Pg[] pages = new Pg[7];
        Button[] navs = new Button[7];
        int page;

        // 仪表盘
        Label lblSysInfo, lblGameState, lblLink, lblLinkAct, lblHealth, lblHealthSub, lblFgRuntime, lblFgLast;
        Label lblXeNote;      // 「方案 A」区块正文：按选中的游戏变（不再把游戏名写死）
        Bar barCpu, barMem, barGpu, barTemp;
        // 监控
        Spark spCpu, spGpu, spMem;
        RichTextBox log;
        bool logVirgin = true;   // 日志占位：首条真日志到达前显示灰字提示
        long lastLogLen = 0;
        // 优化
        ListView lvOpt;
        Label lblDrift;
        // 帧生成
        Label lblRt, lblSel, lblD3d12, lblXe, lbl030;
        Label lblJunk;   // 设置页：备份/隔离区占用一行（P0-1）
        RoundCombo cbJunkAge;   // 「回收范围」档位；SetJunkLabel 往各项回填条数/体积
        static readonly int[] JunkAges = new int[] { 30, 14, 7, 0 };   // 0 = 不限时长
        Label lblPatrol; // 帧生成页：入口巡检一行（同目录里有几套代理并存）（P0-3）
        // 资源包版本结论上次报的是什么 —— 只在变化时写一行日志，避免每次刷新刷满日志
        string packVerdictSeen = "";
        FlatBtn btnXeOn, btnXeOff, btn030On, btn030Off;
        RoundCombo cbEntry;                                  // 3A 通用模式的入口名（6 选 1）
        // 方案推荐（自动识别 + 强调色 + 一键应用）
        Label lblRecBig, lblRecStars, lblRecWhy;
        FlatBtn btnRecApply;
        // 显卡名伪装（内置，以前要出去双击 .reg）
        Label lblSpoof;
        FlatBtn btnSpoofOn, btnSpoofOff, btnSpoofDev;
        // 游戏库（v2.5.0：平台扫描 + 封面卡片 + 库标记）
        FlowLayoutPanel flGames;
        Label lblLibStat, lblTarget;
        TextBox txtSearch;
        RoundField rfSearch;
        bool webCoverRunning;
        bool redownloadRunning;          // 「重新获取官方封面」进行中（下载链已放后台，防重入）
        RoundCombo cbPlat, cbSort, cbTarget, cbTheme;
        RoundCombo cbMotion;                                 // 设置页「界面动效」三态
        CheckBox chkFavOnly, chkShowHidden;
        FlatBtn btnCover, btnLaunchSel;
        bool syncTarget, coverRunning;
        int libCardW, libCardH;          // 游戏库卡片尺寸：BuildPageLibrary 里按构建类型定（分享版=信息行）
        static readonly object[] PlatNames = new object[] { "全部", "收藏", "私密", "二次元", "Steam", "Epic", "WeGame", "本地" };
        static readonly string[] PlatIds = new string[] { "all", "fav", "private", "genre", "steam", "epic", "wegame", "local" };
        RoundCombo cbRouter, cbKernel, cbBilinear, cbMaxFrames, cbLogLevel, cbNr;
        CheckBox cbFgAutoEntry, cbFgD3d12;
        FlatBtn btnDownload, btnScanGames, btnFgApply, btnOptimize;
        FlatBtn btnRestore;      // 「恢复备份」：执行期间要置灰+改文案，所以必须是字段（原先是局部变量）
        FlatBtn btnAddGame, btnIgnored;
        DlssgGame selected;
        List<DlssgGame> games = new List<DlssgGame>();
        // 设置
        RoundCombo cbScene, cbProfile, cbProfileGame;
        RoundCombo cbClose;      // 设置页「关闭窗口时」三态（v3.2.1 从「自定义优化项」搬来）
        CheckBox cbManual, cbAware, cbAutoStart;
        // 游戏分辨率档位（v3.8.0）：两个游戏各一个下拉。原来是写死在代码里的，
        //   换显示器后没有第二个选项 —— 现在可选、可按本机主屏标可用性。
        RoundCombo cbResCs, cbResVal;
        // 进游戏时要在**设备管理器**层面停用的显示器（v3.9.0）。值 = 显示器型号（硬件 ID 第二段）。
        RoundCombo cbOffCs, cbOffVal;
        FlatBtn btnResBase;          // 「还原目标 = 当前主屏」
        Label lblResInfo;            // 档位区的说明 + 实时状态
        bool resComboSyncing = false;   // 回显下拉时抑制事件（否则回显本身会把配置又写一遍）
        // 显示器设备（v3.9.0）：性能优化页的列表 + 兜底按钮
        ListView lvMon;
        FlatBtn btnMonOn, btnMonOff, btnMonAll;
        // 通用
        Label lblFoot, lblClock;
        Timer sysTimer, linkTimer, logTimer, clockTimer;
        NotifyIcon tray;
        ToolStripMenuItem miRemote, miGame;
        bool closing = false;
        bool StartHidden;
        bool trayTipShown = false;
        bool updatingProfile = false;
        bool driftWarned = false;
        int sysBusyFlag = 0;
        DateTime gameStartAt = DateTime.MinValue;
        string gameStartKey = "";      // 本次游戏在库里的稳定 Id（时长统计用）
        DateTime gpuWarnAt = DateTime.MinValue, cpuWarnAt = DateTime.MinValue, memWarnAt = DateTime.MinValue;
        bool lastGameState = false;
        string lastGame = null;          // 当前联动周期内的游戏进程名（换游戏也要重新触发联动）
        int gameMiss = 0;                // 连续"没检测到游戏"的轮询次数（退出防抖，见 UpdateLink）
        bool linkActive = false;         // 本次游戏会话是否真的执行过联动（决定退出时是否收尾）
        // 启动前预应用的显示联动（v3.9.1）：点「启动」时先按规则禁屏 / 切分辨率，再拉起游戏。
        // 记时刻是为了看门狗 —— 若之后一直没检测到游戏进程（启动失败 / 用户中途放弃），
        // 要能自动还原，否则桌面会一直停在游戏分辨率、显示器还禁着（用户会以为程序坏了）。
        DateTime resPreAt = DateTime.MinValue;
        bool scanRunning = false;        // 扫描进行中（按钮扫描与自动扫描互斥）
        DateTime lastScanAt = DateTime.MinValue;   // 上次扫描完成时刻（自动重扫的间隔判据）
        string lastAddDir = "";          // 「添加游戏…」上次选的目录（同一会话内记住）
        string logDir;

        public MainForm(Config cfg, bool startMinimized)
        {
            Cfg = cfg;
            // 动效档位必须在建控件树之前同步进引擎（设置页的说明文案要读 Motion.OsAllows）
            Motion.Mode = (cfg == null || cfg.UiMotion == "on" || cfg.UiMotion == "off") ? (cfg == null ? "auto" : cfg.UiMotion) : "auto";
            Motion.RefreshOsFlag();
            // ★ 必须在 BuildUi() 之前：Bar/Spark/卡片等在构造函数里就把 Theme.X 抄进了字段
            Theme.SetMode(cfg == null ? "light" : cfg.UiTheme);
            StartHidden = startMinimized;
            Program.EnsureLog();
            logDir = Path.Combine(Program.DataDir, "logs");
            BuildUi();
            Program.GameBoostCleanupOrphans();
            Program.GamePowerRecoverOrphan();
            Program.LoadProfileOverrides();
            try { ToolStripManager.Renderer = new ThemedMenuRenderer(); } catch { }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { Program.DetectSystem(); } catch { }
                try { Invoke((Action)(delegate { if (lblSysInfo != null) SetBody(lblSysInfo, Program.SystemSummary()); })); } catch { }
                Program.Log("系统探测: " + Program.SystemSummary());
            });
            Program.Log("GUI 已启动（管理员: " + IsAdmin() + (startMinimized ? "，最小化到托盘" : "") + "）");
            //  判据是从哪份来的必须留痕：catalog.json 读坏了会静默退回内置默认，
            //  那时"识别得对不对"的锅在数据文件上 —— 不说出来就是查不到的错。
            Program.Log(Catalog.Summary());
        }

        // Windows 11：窗口圆角 + 标题栏/边框随主题着色（旧系统调用会失败，自动回退系统默认）
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyChrome(Handle);
            Theme.ScrollThemeTree(this);
            // 拖入插件包（zip / 文件夹）。详见 OnPackDropped 上的注释。
            try { DragAcceptFiles(Handle, true); } catch { }
        }

        // 高 DPI 下窗口可能比屏幕工作区还大（例如 150% 缩放 + 1080p），按工作区收一下
        void ClampToScreen()
        {
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(Width, wa.Width);
                int h = Math.Min(Height, wa.Height);
                if (w != Width || h != Height)
                {
                    Size = new Size(w, h);
                    Program.Log("窗口比屏幕工作区大，已收缩到 " + w + "x" + h);
                }
            }
            catch { }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (StartHidden && value)
            {
                if (!IsHandleCreated) CreateHandle();
                StartHidden = false;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ---------------------- 界面搭建 ----------------------
        void BuildUi()
        {
            // ★ 窗口标题不能空（2026-09-20）：以前是 ""，而 ApplyChrome 又把标题栏染成纯白
            //   → 最上面那 31px 是一条**什么内容都没有的白条**，用户截图问"FLUXION 上面空一块是啥意思"。
            //   填上名字后标题栏有字，一眼能看出那是系统标题栏（也顺带让 Alt+Tab / 任务栏有正常标签）。
            Text = "Fluxion";
            // ★ ShowIcon 必须为 true（2026-09-29）：设 false 时 WinForms 不向窗口发 WM_SETICON，
            //   窗口最小化后任务栏改取窗口类图标（从未设置）→ 任务栏/悬停预览退回系统默认图标，
            //   看起来就是"最小化后 Fluxion 图标消失"。标题栏会多出一个小图标，属正常系统行为。
            ShowIcon = true;
            AutoScaleMode = AutoScaleMode.None;      // 缩放统一由 Theme.S() 负责，避免两套缩放叠加
            Font = Theme.F(9f);                      // 原生控件继承这个字体
            ForeColor = Theme.Text;
            BackColor = Theme.Bg;
            Icon = Program.GetAppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(Theme.S(860), Theme.S(560));
            ClientSize = new Size(Theme.S(1040), Theme.S(700));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            UpdateStyles();

            BuildChrome();

            ClampToScreen();
            BuildTray();

            sysTimer = new Timer(); sysTimer.Interval = 4000;
            sysTimer.Tick += delegate
            {
                try { UpdateSys(); } catch (Exception ex) { Program.Log("TickSys: " + ex.GetType().Name + " " + ex.Message); }
                // 看门狗（磁盘守护 / 覆盖层守护 / 服务与电源动作）只在完整版跑：分享版不动别人的机器
                // ❗ 必须走 GuardTickAsync（后台线程），不能在这儿直接调 Program.GuardTick() —— 详见方法上的注释
                if (!Program.ShareBuild) GuardTickAsync();
            };
            sysTimer.Start();

            // 游戏联动（进游戏暂停远控 / 切电源 / 加速包）只在完整版跑 —— 分享版没有远控模块
            if (!Program.ShareBuild)
            {
                linkTimer = new Timer(); linkTimer.Interval = Math.Max(5, Cfg.PollSec) * 1000;
                linkTimer.Tick += delegate { try { UpdateLink(); } catch (Exception ex) { Program.Log("TickLink: " + ex.GetType().Name + " " + ex.Message); } };
                linkTimer.Start();
            }

            logTimer = new Timer(); logTimer.Interval = 5000;
            logTimer.Tick += delegate { try { FollowLog(); } catch { } };
            logTimer.Start();

            clockTimer = new Timer(); clockTimer.Interval = 1000;
            clockTimer.Tick += delegate { try { if (lblClock != null) lblClock.Text = DateTime.Now.ToString("HH:mm:ss"); } catch { } };
            clockTimer.Start();

            Load += delegate
            {
                for (int i = 0; i < pages.Length; i++) pages[i].Reflow();
                ShowShareDisclaimer();
            };

            // 回到窗口时按需自动重扫（新装的游戏自动出现，见 MaybeAutoScan）
            Activated += delegate { MaybeAutoScan(); };

            Switch(Program.ShareBuild ? P_LIB : P_DASH);       // 分享版首屏直接给「游戏库」
            UpdateSys();
            if (!Program.ShareBuild) UpdateLink();
            RefreshGridAsync();
            SyncProfileEnabled();
            // 启动即加载自定义游戏（games.json），不用等扫描；忽略清单同时生效
            games = Dlssg.MergeScan(new List<DlssgGame>());
            FillGameList();
            RefreshFgSummary();
            RunScan(false);     // 起步即后台自动扫描一遍（静默、不阻塞界面）
            Log("界面已就绪（缩放 " + Theme.ScaleText() + "，窗口 " + Width + "x" + Height + "）");
            Log("Fluxion 已启动（管理员: " + IsAdmin() + "）");
        }

        // 外层骨架（侧栏 / 内容区 / 底栏）。抽出来是为了"换主题时原地重建"能复用同一段代码。
        void BuildChrome()
        {
            // 外层用 TableLayoutPanel 定骨架：行/列都是显式索引，
            // 不依赖 Dock 的 z-order 规则（那是"看起来对、换个顺序就错"的经典坑）。
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            Buffered(root);
            root.ColumnCount = 1; root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(FootH)));
            root.BackColor = Theme.Bg;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;

            var main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            Buffered(main);
            main.ColumnCount = 2; main.RowCount = 1;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(NavW)));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            main.BackColor = Theme.Bg;
            main.Margin = Padding.Empty;

            main.Controls.Add(BuildSide(), 0, 0);
            main.Controls.Add(BuildContent(), 1, 0);
            root.Controls.Add(main, 0, 0);
            root.Controls.Add(BuildFooter(), 0, 1);
            Controls.Add(root);
            rootPanel = root;
        }

        // 换主题：**原地重建整棵控件树**。
        //  为什么必须重建：本项目的配色有大量是在**构造期**就被控件抄进字段的（Label 的前后景、
        //  RoundCheck/Spark/Bar 的底色、ListView/RichTextBox 的原生色…），只重绘刷不干净 ——
        //  以前的办法是"提示重启"，体验上等于"深色根本没法用"（用户实测反馈）。
        //  重建只覆盖"骨架 + 7 个页面"，**绝不碰窗体级的东西**（托盘、4 个定时器、Load/Activated 事件），
        //  那些在 BuildUi 里只装一次，重复装会双份触发。
        void RebuildChrome()
        {
            int keepPage = page;
            DlssgGame keepSel = selected;
            Cursor = Cursors.WaitCursor;
            try
            {
                SuspendLayout();
                try
                {
                    if (rootPanel != null) { Controls.Remove(rootPanel); rootPanel.Dispose(); rootPanel = null; }
                }
                catch { }
                BackColor = Theme.Bg;
                ForeColor = Theme.Text;
                Font = Theme.F(9f);
                Icon = Program.GetAppIcon();
                BuildChrome();
                try { Theme.ApplyChrome(Handle); } catch { }
                try { Theme.ScrollThemeTree(this); } catch { }
                ResumeLayout(true);

                for (int i = 0; i < pages.Length; i++) if (pages[i] != null) pages[i].Reflow();
                Switch(keepPage);
                FillGameList();
                if (keepSel != null)
                    foreach (Control c in flGames.Controls)
                    {
                        GameCard gc = c as GameCard;
                        if (gc != null && gc.Game == keepSel) { SelectCard(gc); break; }
                    }
                try { RefreshFgSummary(); } catch { }
                try { UpdateSys(); } catch { }
                try { UpdateLink(); } catch { }
                // ★ 2026-09-20（1.0.2 用户反馈"该页时不时卡死、什么都不显示"）：
                //   重建控件树后，lvOpt 是**全新**的空表 —— 而整条重建路径里没有任何人回填它，
                //   于是每次换主题之后「优化项明细」都是 0 行（离屏探针 h 段实测 4 轮全部 = 0 行），
                //   同时 lblHealth / lblHealthSub / lblDrift 也停在初始值。
                //   用户截图里那张"只有表头、下面什么都没有"的性能优化页就是这个状态。
                //   ⚠ 别把它当成性能问题：整棵重建只要 ~250ms，问题纯粹是"没人回填"。
                try { RefreshGridAsync(); } catch { }
                Invalidate(true);
                Update();
            }
            catch (Exception ex)
            {
                Program.Log("换主题重建失败：" + ex.GetType().Name + " " + ex.Message + "（建议重启程序）");
            }
            finally { Cursor = Cursors.Default; }
        }

        // ---- 侧栏 ----
        Panel BuildSide()
        {
            var side = new Panel();
            side.Dock = DockStyle.Fill;
            side.Margin = Padding.Empty;
            side.BackColor = Theme.Panel;
            Buffered(side);
            // 侧栏与内容区之间不再画硬分隔线：底色本身已经有区分，硬线只会显得"割裂"

            // 侧栏内容用 FlowLayoutPanel 自上而下排，顺序 = 添加顺序，没有 Dock 次序歧义
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            // ★ 侧栏不该出现滚动条：FlowLayoutPanel 的 AutoScroll 默认为开，窗口偏矮时会在
            //   侧栏右侧冒出一条竖滚动条（用户截图："旁边为啥还有滑动条"）
            flow.AutoScroll = false;
            flow.HorizontalScroll.Visible = false;
            flow.VerticalScroll.Visible = false;
            flow.BackColor = Theme.Panel;
            flow.Margin = Padding.Empty;
            flow.Padding = Padding.Empty;

            int navBtnW = Theme.S(NavW) - 2;

            var brandHeader = new Panel();
            brandHeader.Width = navBtnW;
            // ★ 高度与右侧页头 HeadBox（58）严格一致 —— 以前是 64，顶条两块底边不齐，
            //   加上白色 vs 灰底撞色，就是用户截图"这两块颜色太割裂/设计很不美观"的主因。
            brandHeader.Height = Theme.S(58);
            brandHeader.BackColor = Theme.Panel;
            brandHeader.Margin = Padding.Empty;
            Buffered(brandHeader);
            brandHeader.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 绘制左侧六边形矢量 Logo（垂直居中）
                int logoSz = Theme.S(26);
                int logoX = Theme.S(14);
                int logoY = (brandHeader.Height - logoSz) / 2;
                Theme.DrawBrandLogo(g, new Rectangle(logoX, logoY, logoSz, logoSz));

                // 品牌主标题（v1.0.0 Fluxion 改名：这里此前硬编码 "GAMEBOOST"，漏网之鱼）
                // 两行作为整体在 58px 内垂直居中，与右侧页头「大标题在上、副标题在下」的节奏对齐
                int tx = Theme.S(48);
                TextRenderer.DrawText(g, "FLUXION", Theme.FB(10f),
                    new Rectangle(tx, Theme.S(8), brandHeader.Width - tx - Theme.S(6), Theme.S(20)), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                // 副标题降噪：亮极光绿 → 灰（TextDim）。顶条角落里那一点高饱和绿
                // 和右侧灰色页头撞色，是"很割裂"观感的另一半来源。
                TextRenderer.DrawText(g, "PERFORMANCE SUITE", Theme.FB(7f),
                    new Rectangle(tx, Theme.S(29), brandHeader.Width - tx - Theme.S(6), Theme.S(14)), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                // 底部分隔线：与 HeadBox 同高，但**改成两端渐隐的柔线**（2026-09-20）。
                //   原来是一条实心 1px 直线，只存在于左半边的品牌头里，右边页头却没有 ——
                //   顶部白带上"一条硬线只切左半边"，正是用户说的"上面棱角分明 / 割裂"。
                //   渐隐后与底部状态栏顶线（BuildFooter）用的是同一种过渡。
                Color lc = Theme.Line;
                using (var br = new LinearGradientBrush(
                           new Rectangle(0, 0, Math.Max(1, brandHeader.Width), 1),
                           Color.FromArgb(0, lc), lc, 0f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Color.FromArgb(0, lc), lc, Color.FromArgb(0, lc) };
                    cb.Positions = new float[] { 0f, 0.18f, 1f };
                    br.InterpolationColors = cb;
                    g.FillRectangle(br, 0, brandHeader.Height - 3, brandHeader.Width, 1);
                }
            };
            flow.Controls.Add(brandHeader);

            var sp = new Panel();
            sp.Width = navBtnW; sp.Height = Theme.S(8);
            sp.BackColor = Theme.Panel; sp.Margin = Padding.Empty;
            flow.Controls.Add(sp);

            string[] names = new string[] { "仪表盘", "性能优化", "帧生成", "游戏库", "实时监控", "说明", "设置" };
            // 分享构建：侧栏只留「帧生成 / 游戏库 / 说明」—— 其余页面是照作者这台机器配的系统调优，
            // 拿到别人机器上要么没用、要么有害，索性不给入口。
            // ⚠ 必须**全部创建** NavBtn、只把要显示的那些加进 flow —— navs[] 是 Switch() 的必填数组，
            //   留空会走 `navs[i].BackColor = …` 那条分支解引用 null，启动即崩（2026-09-17 实测：
            //   分享版双击毫无反应，就是这个 NullReferenceException）。
            bool[] navShow = Program.ShareBuild
                ? new bool[] { false, false, true, true, false, true, true }
                : new bool[] { true, true, true, true, true, true, true };
            for (int i = 0; i < names.Length; i++)
            {
                var nb = new NavBtn();
                nb.NavIndex = i;
                nb.Text = names[i];
                nb.Width = navBtnW;
                nb.Height = Theme.S(38);
                nb.Margin = Padding.Empty;
                nb.Font = Theme.F(9.5f);
                int idx = i;
                nb.Click += delegate { Switch(idx); };
                navs[i] = nb;                       // 无论显示与否都持有引用
                if (navShow[i]) flow.Controls.Add(nb);
            }

            // 版本与系统运行状态卡片钉在侧栏底部
            var verCard = new Panel();
            verCard.Dock = DockStyle.Bottom;
            verCard.Height = Theme.S(68);
            verCard.BackColor = Theme.Panel;
            Buffered(verCard);

            var tip = new ToolTip();
            tip.SetToolTip(verCard, "数据目录: " + Program.DataDir + (Program.IsAdmin() ? " (管理员权限)" : ""));

            verCard.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                // 顶部分隔线
                using (var p = new Pen(Theme.Line, 1f))
                    g.DrawLine(p, Theme.S(10), 0, verCard.Width - Theme.S(10), 0);

                // 柔和底卡
                var r = new Rectangle(Theme.S(8), Theme.S(8), verCard.Width - Theme.S(16), verCard.Height - Theme.S(16));
                Theme.FillRound(g, r, Theme.S(6), Theme.Subtle);
                Theme.EdgeRound(g, r, Theme.S(6), Theme.Line, 1f);

                // 呼吸绿微点
                int dotR = Theme.S(6);
                int dotX = r.X + Theme.S(10);
                int dotY = r.Y + Theme.S(11);
                using (var bGlow = new SolidBrush(Theme.OkSoft))
                    g.FillEllipse(bGlow, dotX - 2, dotY - 2, dotR + 4, dotR + 4);
                using (var bDot = new SolidBrush(Theme.Ok))
                    g.FillEllipse(bDot, dotX, dotY, dotR, dotR);

                // 守护状态文字
                Rectangle tr1 = new Rectangle(dotX + dotR + Theme.S(6), r.Y + Theme.S(6), r.Width - dotX - Theme.S(12), Theme.S(16));
                TextRenderer.DrawText(g, "系统守护 · 已就绪", Theme.FB(8.5f), tr1, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // 版本与模式
                Rectangle tr2 = new Rectangle(r.X + Theme.S(10), r.Y + Theme.S(24), r.Width - Theme.S(20), Theme.S(16));
                string modeStr = "v" + Program.DisplayVersion + (Program.ShareBuild ? " · 分享版" : "")
                    + " · " + (Program.DataDir == Program.AppDir ? "便携版" : "安装版") + (Program.IsAdmin() ? " · 管理员" : "");
                TextRenderer.DrawText(g, modeStr, Theme.Mono(7.5f), tr2, Theme.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };

            side.Controls.Add(verCard);
            side.Controls.Add(flow);
            flow.BringToFront();     // Fill 放最后 Dock，占满除底部以外的区域
            return side;
        }

        // Panel / FlowLayoutPanel 的 DoubleBuffered 是 protected，只能反射设置。
        // 窗体与几个 Dock 容器默认不开双缓冲 —— 拖动窗口、切页、缩放时会撕裂出残影。
        static void Buffered(Control c)
        {
            try
            {
                System.Reflection.PropertyInfo pi = typeof(Control).GetProperty(
                    "DoubleBuffered", System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(c, true, null);
            }
            catch { }
        }

        // ---- 底部状态栏 ----
        Panel BuildFooter()
        {
            var foot = new Panel();
            foot.Dock = DockStyle.Fill;
            foot.Margin = Padding.Empty;
            foot.BackColor = Theme.Panel;
            Buffered(foot);
            foot.Paint += delegate(object s, PaintEventArgs e)
            {
                // 顶边同样用淡出线，跟标题头的分隔线保持同一种过渡
                Color lc = Theme.Line;
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, foot.Width), 1),
                           Color.FromArgb(0, lc), lc, 0f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Color.FromArgb(0, lc), lc, Color.FromArgb(0, lc) };
                    cb.Positions = new float[] { 0f, 0.18f, 1f };
                    br.InterpolationColors = cb;
                    e.Graphics.FillRectangle(br, 0, 0, foot.Width, 1);
                }
            };

            lblFoot = new Label();
            lblFoot.Dock = DockStyle.Fill;
            lblFoot.Font = Theme.F(8.5f);
            lblFoot.ForeColor = Theme.TextDim;
            lblFoot.BackColor = Theme.Panel;
            lblFoot.TextAlign = ContentAlignment.MiddleLeft;
            lblFoot.Text = "就绪";

            var dotPanel = new Panel();
            dotPanel.Dock = DockStyle.Left;
            dotPanel.Width = Theme.S(28);
            dotPanel.BackColor = Theme.Panel;
            Buffered(dotPanel);
            dotPanel.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int dotR = Theme.S(6);
                int dotX = Theme.S(14);
                int dotY = (dotPanel.Height - dotR) / 2;
                using (var bGlow = new SolidBrush(Theme.OkSoft))
                    e.Graphics.FillEllipse(bGlow, dotX - 2, dotY - 2, dotR + 4, dotR + 4);
                using (var bDot = new SolidBrush(Theme.Ok))
                    e.Graphics.FillEllipse(bDot, dotX, dotY, dotR, dotR);
            };

            var statusBox = new Panel();
            statusBox.Dock = DockStyle.Fill;
            statusBox.BackColor = Theme.Panel;
            Buffered(statusBox);
            statusBox.Controls.Add(lblFoot);
            statusBox.Controls.Add(dotPanel);
            dotPanel.BringToFront();
            lblFoot.BringToFront();

            var clockBox = new Panel();
            clockBox.Dock = DockStyle.Right;
            clockBox.Width = Theme.S(90);
            clockBox.BackColor = Theme.Panel;
            clockBox.Padding = new Padding(0, Theme.S(4), Theme.S(12), Theme.S(4));
            Buffered(clockBox);

            lblClock = new Label();
            lblClock.Dock = DockStyle.Fill;
            lblClock.Font = Theme.Mono(8.5f);
            lblClock.ForeColor = Theme.Cyan;
            lblClock.BackColor = Theme.Field;
            lblClock.TextAlign = ContentAlignment.MiddleCenter;
            lblClock.Text = DateTime.Now.ToString("HH:mm:ss");
            clockBox.Controls.Add(lblClock);

            foot.Controls.Add(clockBox);
            foot.Controls.Add(statusBox);
            statusBox.BringToFront();
            return foot;
        }

        // ---- 内容区 ----
        Panel BuildContent()
        {
            content = new Panel();
            content.Dock = DockStyle.Fill;
            Buffered(content);
            content.Margin = Padding.Empty;
            content.BackColor = Theme.Bg;
            // ★ 顶部不留内边距（2026-09-20）：原来这里是 S(12)，会在标题栏与页头之间露出
            //   一条 12px 的灰带，页头因此比左侧品牌头低 12px —— 顶条白带断开 + 一个灰色直角，
            //   正是用户说的"色块颜色过渡太生硬"（截图里那个硬边灰块）。去掉后
            //   标题栏 → 页头（同高 58）连成一体，往下再渐隐到灰。
            // ★ 左右不留内边距（2026-09-20）：这里是 S(14)，而 Pg.LayoutOnce 里子控件又各让出
            //   GapSize=S(14) —— 两步叠加 = 内容区左（右）侧多出一条 28px 的灰竖条，
            //   在顶部把"白横带"切成 白|灰|白 三截（用户截图："上面棱角分明"）。
            //   留白职责收回给 LayoutOnce 一处（那里同时管卡片缩进和通栏页头），这里归零。
            content.Padding = new Padding(0, 0, 0, Theme.S(10));

            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = new Pg();
                pages[i].Dock = DockStyle.Fill;
                pages[i].Visible = false;
                content.Controls.Add(pages[i]);
            }

            BuildPageDashboard();
            BuildPageOptimize();
            BuildPageFrameGen();
            BuildPageLibrary();
            BuildPageMonitor();
            BuildPageHelp();
            BuildPageSettings();
            return content;
        }

        void Switch(int idx)
        {
            page = idx;
            for (int i = 0; i < pages.Length; i++)
            {
                bool on = (i == idx);
                pages[i].Visible = on;
                NavBtn nbb = navs[i] as NavBtn;
                // 不再切字体粗细：字重一跳，那行文字会瞬间重排一下（正是"生硬"的来源之一）。
                // 选中态改由药丸 + 图标胶囊 + 强调色三样一起表达，信息量足够，也不违反"别只靠颜色"。
                if (nbb != null) nbb.Active = on;
                else if (navs[i] != null) { navs[i].BackColor = on ? Theme.AccentSoft : Theme.Panel; navs[i].ForeColor = on ? Theme.Accent : Theme.TextDim; }
            }
            pages[idx].Reflow();
            if (idx == P_FG) RefreshFgSummary();
            if (idx == P_LIB) FillGameList();
            // 设置页：重算分辨率档位的"本机可用性"（用户可能刚换过显示器/改过分辨率，
            //   而档位标注是在构建页面那一刻算的 —— 不刷新就一直显示旧屏的判断）
            if (idx == P_SET) { try { RefreshResCombos(); } catch { } }
            // 性能优化页：重读显示器设备状态（用户可能刚在设备管理器里改过、或插拔过屏）
            if (idx == P_OPT) { try { FillMonList(); } catch { } }
        }

        // ---------------------- 页 1：仪表盘 ----------------------
        void BuildPageDashboard()
        {
            var p = pages[P_DASH];
            p.AddHead("仪表盘", "游戏联动 · 实时负载 · 系统健康度");

            // 版头顺序按使用频率：游戏联动（核心功能）> 实时负载 > 快捷操作 > 帧生成/健康度 > 静态系统信息
            var s3 = new Sec("游戏联动");
            lblGameState = s3.Body("等待状态…");
            lblGameState.Font = Theme.FB(14f);          // 仪表盘第一眼就该落在这行上
            lblGameState.ForeColor = Theme.Text;
            lblLink = s3.Body("");
            lblLinkAct = s3.Body("");
            p.Add(s3);

            var s2 = new Sec("实时负载");
            barCpu = new Bar(); barCpu.Caption = "CPU"; barCpu.BarColor = Theme.Accent;
            barMem = new Bar(); barMem.Caption = "内存"; barMem.BarColor = Theme.Cyan;
            barGpu = new Bar(); barGpu.Caption = "GPU"; barGpu.BarColor = Theme.Violet;
            barTemp = new Bar(); barTemp.Caption = "GPU 温度"; barTemp.BarColor = Theme.Warn;
            barCpu.Grid = barMem.Grid = barGpu.Grid = barTemp.Grid = true;
            s2.Block2(barCpu, barMem, Theme.S(66));
            s2.Block2(barGpu, barTemp, Theme.S(66));
            p.Add(s2);

            var s6 = new Sec("快捷操作");
            var q1 = new FlatBtn(); q1.Text = "一键优化"; q1.Kind = BtnKind.Primary; q1.Width = Theme.S(100);
            q1.Click += delegate { DoOptimize(); };
            var q2 = new FlatBtn(); q2.Text = "系统体检"; q2.Width = Theme.S(100);
            q2.Click += delegate { RefreshGridAsync(); Log("已刷新优化项状态（体检结果见「性能优化」页）"); Switch(P_OPT); };
            var q3 = new FlatBtn(); q3.Text = "恢复备份"; q3.Kind = BtnKind.Danger; q3.Width = Theme.S(100);
            q3.Click += delegate { DoRestore(); };
            s6.Buttons(q1, q2, q3);
            p.Add(s6);

            var s5 = new Sec("帧生成（DLSSG）");
            lblFgRuntime = s5.Body("—");
            // 生效自检：读代理自己写的 jsonl，直接回答"上次跑到了几倍"（不用进游戏看菜单）
            lblFgLast = s5.Body("");
            var btnGo = new FlatBtn(); btnGo.Text = "管理帧生成 →"; btnGo.Kind = BtnKind.Primary; btnGo.Width = Theme.S(124);
            btnGo.Click += delegate { Switch(P_FG); };
            s5.Buttons(btnGo);
            p.Add(s5);

            var s4 = new Sec("优化健康度");
            lblHealth = s4.Body("—");
            lblHealth.Font = Theme.FB(16f);
            lblHealth.ForeColor = Theme.Ok;
            lblHealthSub = s4.Body("");
            p.Add(s4);

            var s1 = new Sec("系统信息");
            lblSysInfo = s1.Body("正在探测硬件…");
            p.Add(s1);
        }

        // ---------------------- 页 2：性能优化 ----------------------
        void BuildPageOptimize()
        {
            var p = pages[P_OPT];
            p.AddHead("性能优化", "电源 / GPU 调度 / 进程优先级 · 一键优化与还原");

            var s1 = new Sec("操作");
            btnOptimize = new FlatBtn(); btnOptimize.Text = "一键优化"; btnOptimize.Kind = BtnKind.Primary; btnOptimize.Width = Theme.S(100);
            btnOptimize.Click += delegate { DoOptimize(); };
            var btnCustom = new FlatBtn(); btnCustom.Text = "自定义项"; btnCustom.Width = Theme.S(92);
            btnCustom.Click += delegate { ShowCustomDialog(); };
            btnRestore = new FlatBtn(); btnRestore.Text = "恢复备份"; btnRestore.Kind = BtnKind.Danger; btnRestore.Width = Theme.S(92);
            btnRestore.Click += delegate { DoRestore(); };
            var btnDefaults = new FlatBtn(); btnDefaults.Text = "恢复默认"; btnDefaults.Width = Theme.S(92);
            btnDefaults.Click += delegate
            {
                if (MessageBox.Show("将把电源计划重置为出厂默认，并把所有优化项恢复为 Windows 默认值（不是恢复备份）。\n\n确定继续吗？",
                    "恢复系统默认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                Log(">>> 开始恢复系统默认设置…");
                btnDefaults.Enabled = false;
                var gw = new BackgroundWorker();
                gw.DoWork += delegate { Program.RestoreSystemDefaults(); };
                gw.RunWorkerCompleted += delegate
                {
                    btnDefaults.Enabled = true; RefreshGridAsync();
                    Log(">>> 恢复系统默认完成");
                };
                gw.RunWorkerAsync();
            };
            var btnRefresh = new FlatBtn(); btnRefresh.Text = "刷新"; btnRefresh.Width = Theme.S(76);
            btnRefresh.Click += delegate { RefreshGridAsync(); UpdateLink(); Log("已刷新状态"); };
            s1.Buttons(btnOptimize, btnCustom, btnRestore, btnDefaults, btnRefresh);
            s1.Body("所有修改在执行前自动备份到数据目录的 backup\\，可随时还原。");
            p.Add(s1);

            var s2 = new Sec("优化项明细（当前系统实际设置）");
            // 状态列 86（不是 70）：胶囊里要放「已生效」这种 3 个汉字 + 圆点，
            // 70px 下胶囊的可用文字区只剩 ~37px，正好卡在省略号边缘（v3.6.1 离屏渲染实测）。
            lvOpt = NewList(new string[] { "优化项", "状态", "当前值", "期望值" }, new int[] { 260, 86, 250, 0 }, Theme.S(250));
            lvOpt.Resize += delegate { FillLastColumn(lvOpt, Theme.S(160)); };
            // v3.6.0：体检表不再只是"看"。带 ⚙ 的行双击即可改这一项的具体取值
            //（着色器缓存 12 档、DLSS 预设字母、量子长度、前后台帧率上限…）。
            lvOpt.DoubleClick += delegate { OpenKnobForRow(); };
            var btnKnobSel = new FlatBtn(); btnKnobSel.Text = "调整选中项"; btnKnobSel.Width = Theme.S(100);
            btnKnobSel.Click += delegate { OpenKnobForRow(); };
            var btnKnobAll = new FlatBtn(); btnKnobAll.Text = "取值细调…"; btnKnobAll.Width = Theme.S(100);
            btnKnobAll.Click += delegate { ShowKnobDialog(null); };
            s2.Buttons(btnKnobSel, btnKnobAll);
            s2.Block(lvOpt, Theme.S(250));
            s2.Body("状态含义：「已生效」= 当前值与优化目标一致；「未生效」= 被系统更新或驱动重置；"
                  + "灰色的「待确认」= 不是缺陷（可选 / 不适用 / 读不到，例如 HAGS 的 A/B 建议），看一眼就行；"
                  + "「需处理」= 读取失败或需要管理员权限。"
                  + "\n带 ⚙ 的行可调：点选后按「调整选中项」（或直接双击该行）就能改这一项的具体档位；"
                  + "其余行是只读核验项 —— 要么没有「更优的档位」可选（默认即最优），要么改错会让设备消失。"
                  + "\n列宽拖乱了：在表头（或列表任意处）右键 →「恢复默认列宽」；想单列按内容自适应，双击列分隔线即可。");
            p.Add(s2);

            var s3 = new Sec("漂移检测");
            lblDrift = s3.Body("刷新后自动检测：系统大版本更新常会静默重置优化项。");
            p.Add(s3);

            // ---- 显示器设备（v3.9.0）----
            // 用户 2026-09-20 诉求：「我不是说要有启用和禁用的功能吗」——
            //   指的就是设备管理器「监视器」节点下那个「启用设备 / 禁用设备」。
            //   日常要启用；进游戏时按「设置 → 场景与联动」里选定的那一台自动停用，退出游戏自动启用。
            // 放这一页的理由：语义与"改了什么 / 怎么还原"一致，用户发现"屏被禁用了"也会先来这里找。
            var s4 = new Sec("显示器设备（设备管理器里的「监视器」）");
            lvMon = NewList(new string[] { "显示器", "输出", "状态", "型号 ID" }, new int[] { 200, 100, 160, 0 }, Theme.S(150));
            lvMon.Resize += delegate { FillLastColumn(lvMon, Theme.S(110)); };
            btnMonOn = new FlatBtn(); btnMonOn.Text = "启用选中"; btnMonOn.Width = Theme.S(88);
            btnMonOn.Click += delegate { MonSetSel(true); };
            btnMonOff = new FlatBtn(); btnMonOff.Text = "禁用选中"; btnMonOff.Kind = BtnKind.Danger; btnMonOff.Width = Theme.S(88);
            btnMonOff.Click += delegate { MonSetSel(false); };
            btnMonAll = new FlatBtn(); btnMonAll.Text = "全部启用"; btnMonAll.Width = Theme.S(88);
            btnMonAll.Click += delegate
            {
                int n = MonMgr.EnableAll();
                Log(n > 0 ? ("已启用 " + n + " 台被禁用的显示器") : "当前没有被禁用的显示器（无需处理）");
                FillMonList();
            };
            var btnMonRef = new FlatBtn(); btnMonRef.Text = "刷新"; btnMonRef.Width = Theme.S(76);
            btnMonRef.Click += delegate { FillMonList(); Log("已刷新显示器设备列表"); };
            s4.Buttons(btnMonOn, btnMonOff, btnMonAll, btnMonRef);
            s4.Block(lvMon, Theme.S(150));
            s4.Body("这里的「禁用」= 设备管理器里右键「禁用设备」的等价操作（PnP 层，重启后仍保持禁用）；"
                  + "而游戏联动里的「分离副屏」只是把屏从桌面摘掉（设备管理器里仍显示正常运行）—— 两回事，别混。"
                  + "\n进游戏时按「设置 → 场景与联动」里选定的那一台自动停用，退出游戏自动启用回来；也可以在这里手动随时改。"
                  + "\n⚠ 停用主屏会让桌面转移到另一台屏上；桌面上只剩一块屏时程序会拒绝停用（否则直接黑屏）。"
                  + "误操作了就在本页点「全部启用」。");
            p.Add(s4);
            FillMonList();
        }

        // 显示器设备列表（含**已禁用**的：光靠 EnumDisplayDevices 看不到被禁用的设备，见 MonMgr 注释）
        void FillMonList()
        {
            if (lvMon == null || lvMon.IsDisposed) return;
            try
            {
                lvMon.BeginUpdate();
                lvMon.Items.Clear();
                foreach (MonDev d in MonMgr.All())
                {
                    var it = new ListViewItem(d.Panel);
                    it.SubItems.Add(d.Output.Length > 0 ? d.Output.Replace("\\\\.\\", "") : "—");
                    var st = it.SubItems.Add(d.StatusText);
                    it.SubItems.Add(d.Model);
                    it.Tag = d;
                    it.UseItemStyleForSubItems = false;
                    st.ForeColor = d.Disabled ? Theme.Err : (d.OnDesktop ? Theme.Ok : Theme.TextDim);
                    lvMon.Items.Add(it);
                }
            }
            catch { }
            finally { lvMon.EndUpdate(); }
        }

        void MonSetSel(bool on)
        {
            if (lvMon == null || lvMon.IsDisposed) return;
            if (lvMon.SelectedItems.Count == 0) { Log("请先在列表里选一台显示器"); return; }
            var d = lvMon.SelectedItems[0].Tag as MonDev;
            if (d == null) return;
            if (!on)
            {
                int onDesk = 0;
                try { foreach (MonDev x in MonMgr.All()) if (x.OnDesktop) onDesk++; } catch { }
                // 只剩一块屏时默认拒绝（停掉就没画面了）。但用户已经在设置页开过「允许禁用唯一在用屏」
                //   （单屏拉伸就要禁这一台）⇒ 不再拦，交给他自己的确认对话框。
                if (d.OnDesktop && onDesk <= 1 && !Cfg.ResLinkSoloOff)
                {
                    MessageBox.Show(this, "现在桌面上只有「" + d.Panel + "」这一块屏在输出画面。\n\n"
                        + "停用它等于把画面来源掐掉 —— 会黑屏，而且没有第二块屏可以把它开回来。已拒绝。\n\n"
                        + "（单屏拉伸需要禁掉它的话，先在设置页勾上「允许禁用唯一在用屏」。）",
                        "禁用显示器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string extra = d.Primary ? "\n\n⚠ 它是**当前主屏**：停用后桌面会转移到另一台屏上。" : "";
                if (MessageBox.Show(this, "确定停用「" + d.Panel + "」吗？\n\n"
                    + "效果 = 设备管理器里右键「禁用设备」（状态会变成「已禁用」）。" + extra
                    + "\n\n随时可以在本页点「全部启用」恢复。", "禁用显示器",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            bool ok = MonMgr.SetEnabled(d, on);
            Log((on ? "启用" : "禁用") + "显示器 " + d.Line
                + (ok ? " 成功" : " 失败（" + MonMgr.LastError + "）"));
            FillMonList();
        }

        // ---------------------- 页 3：帧生成（只看运行时与操作，长文在「说明」页） ----------------------
        void BuildPageFrameGen()
        {
            var p = pages[P_FG];
            p.AddHead("帧生成", "DLSS 帧生成 · 方案 A 注入（OptiScaler）");

            // XeSS 多帧生成：社区验证的两套部署（绝区零 d3d12 入口 / 鸣潮 dxgi 入口），2026-09-13 实机跑通
            // ---------- ① 方案推荐：自动识别 + 强调色 + 一键应用 ----------
            //  用户反馈"说法太乱、没有推荐度"：这里只给一条结论（大号强调色）+ 适用度星级 +
            //  一句话理由，剩下的全部交给「一键应用推荐方案」。
            var sRec = new Sec("① 方案推荐 · 自动识别");
            lblRecBig = sRec.Body("检查中…");
            lblRecBig.Font = Theme.FB(12f);
            lblRecBig.ForeColor = Theme.Accent;
            lblRecStars = sRec.Body("—");
            lblRecStars.Font = Theme.FB(9.5f);
            lblRecStars.ForeColor = Theme.Accent;
            lblRecWhy = sRec.BodyFixed("到「游戏库」页点一张卡片，这里会自动给出该装哪个方案。", 2);
            btnRecApply = new FlatBtn();
            btnRecApply.Text = "一键应用推荐方案";
            btnRecApply.Kind = BtnKind.Primary;
            btnRecApply.Width = Theme.S(160);
            btnRecApply.Click += delegate { DoApplyRecommend(); };
            var btnRecGoto = new FlatBtn();
            btnRecGoto.Text = "查看详解";
            btnRecGoto.Width = Theme.S(96);
            btnRecGoto.Click += delegate { Switch(P_HELP); };
            sRec.Buttons(btnRecApply, btnRecGoto);
            p.Add(sRec);

            // ---------- ② 显卡名伪装（内置：以前要导出 .reg 再双击导入） ----------
            var sSpoof = new Sec("② 显卡名伪装（网游的帧生成选项靠它出现）");
            lblSpoof = sSpoof.BodyFixed("检查中…", 2);
            // ⚠ 2026-09-16 实测现象（别把「重启显卡设备」当成等效手段）：
            //   21:26:03 写入伪装 → 21:27:06 重启显卡设备 → 21:27:44 启动绝区零 →
            //   21:28:10 游戏日志读到的仍是 "NVIDIA GeForce RTX 3060 Ti"。
            //   而更早那次「导入 .reg + 重启电脑」之后，游戏日志确实读到了 "RTX 5090"（03:46 / 10:50 两条）。
            //   结论：以「重启电脑」为准；「重启显卡设备」只算辅助（部件重新枚举可能把值写回去）。
            sSpoof.Body("改完请重启电脑才可靠（实测：只点「重启显卡设备」不足以让它生效）；驱动升级后要重做。"
                      + "副作用：全系统所有程序看到的都是 RTX 5090。随时可一键还原。");
            btnSpoofOn = new FlatBtn(); btnSpoofOn.Text = "开启伪装（改为 RTX 5090）";
            btnSpoofOn.Kind = BtnKind.Success; btnSpoofOn.Width = Theme.S(186);
            btnSpoofOn.Click += delegate { DoSpoof(true); };
            btnSpoofOff = new FlatBtn(); btnSpoofOff.Text = "还原真实显卡名"; btnSpoofOff.Width = Theme.S(136);
            btnSpoofOff.Click += delegate { DoSpoof(false); };
            btnSpoofDev = new FlatBtn(); btnSpoofDev.Text = "重启显卡设备"; btnSpoofDev.Width = Theme.S(120);
            btnSpoofDev.Click += delegate { DoSpoofRestartDevice(); };
            sSpoof.Buttons(btnSpoofOn, btnSpoofOff, btnSpoofDev);
            p.Add(sSpoof);

            var s1 = new Sec("运行时状态");
            lblRt = s1.BodyFixed("检查中…", 3);
            btnDownload = new FlatBtn(); btnDownload.Text = "下载运行时"; btnDownload.Width = Theme.S(112);
            btnDownload.Click += delegate { DoDownload(); };
            var btnResidue = new FlatBtn(); btnResidue.Text = "清理残留"; btnResidue.Width = Theme.S(96);
            btnResidue.Click += delegate { DoCleanResidue(); };
            s1.Buttons(btnDownload, btnResidue);
            p.Add(s1);

            // 目标游戏：与「游戏库」页的卡片选中双向同步（库页点卡片 → 这里跟着变；这里选 → 库页高亮跟着变）
            var s2 = new Sec("帧生成操作（对「目标游戏」生效）");
            cbTarget = new RoundCombo();
            cbTarget.DropDownStyle = ComboBoxStyle.DropDownList;
            cbTarget.Width = Theme.S(320);
            cbTarget.SelectedIndexChanged += delegate { OnTargetChanged(); };
            s2.Row("目标游戏", cbTarget, Theme.S(320));
            lblTarget = s2.BodyFixed("未选中 —— 到「游戏库」页点一张卡片选中游戏", 1);
            var btnOn = new FlatBtn(); btnOn.Text = "开启选中"; btnOn.Kind = BtnKind.Success; btnOn.Width = Theme.S(96);
            btnOn.Click += delegate { DoFgInstall(); };
            var btnOff = new FlatBtn(); btnOff.Text = "关闭选中"; btnOff.Kind = BtnKind.Danger; btnOff.Width = Theme.S(96);
            btnOff.Click += delegate { DoFgUninstall(); };
            var btnSwitch = new FlatBtn(); btnSwitch.Text = "换入口重试"; btnSwitch.Width = Theme.S(96);
            btnSwitch.Click += delegate { DoSwitchEntry(); };
            var btnDiag = new FlatBtn(); btnDiag.Text = "运行诊断"; btnDiag.Width = Theme.S(96);
            btnDiag.Click += delegate { DoFgDiagnose(); };
            btnIgnored = new FlatBtn(); btnIgnored.Text = "忽略清单"; btnIgnored.Width = Theme.S(96);
            btnIgnored.Click += delegate { ShowIgnoredDialog(); };
            //  「停放遗留项」不走部署路径 —— 它是一个独立的只读巡检动作：
            //   看见「两代代理并存」之后，要么忍着、要么只停掉多出来的那一代，不该被逼着点一次部署才能处理（P0-3）。
            var btnParkStray = new FlatBtn(); btnParkStray.Text = "停放遗留项"; btnParkStray.Width = Theme.S(112);
            btnParkStray.Click += delegate { DoParkStrays(); };
            s2.Buttons(btnOn, btnOff, btnSwitch, btnDiag, btnIgnored, btnParkStray);
            lblD3d12 = s2.BodyFixed("d3d12 入口：检查中…", 1);
            lblPatrol = s2.BodyFixed("入口巡检：检查中…", 2);
            p.Add(s2);


            // 标题不再写死某一个游戏名（用户反馈："为什么只写一个鸣潮"）；
            //  正文由 Dlssg.OptiNote(选中游戏) 动态生成，切游戏就变。
            var sXe = new Sec("帧生成方案 A · OptiScaler 注入");
            lblXe = sXe.BodyFixed("检查中…", 1);
            lblXeNote = sXe.BodyFixed(Dlssg.OptiNote(null), 2);
            sXe.Body("与 0.3.x 互斥（抢同一个入口 DLL），但可随时来回切 —— 只改文件名、不删文件、秒完成。"
                   + "详解见「说明」页。");
            btnXeOn = new FlatBtn(); btnXeOn.Text = "切换到本方案"; btnXeOn.Kind = BtnKind.Success; btnXeOn.Width = Theme.S(120);
            btnXeOn.Click += delegate { DoXeDeploy(); };
            btnXeOff = new FlatBtn(); btnXeOff.Text = "停放（回到原生）"; btnXeOff.Kind = BtnKind.Primary; btnXeOff.Width = Theme.S(132);
            btnXeOff.Click += delegate { DoXeUndeploy(); };
            sXe.Buttons(btnXeOn, btnXeOff);
            p.Add(sXe);
            var s030 = new Sec("DLSS MFG " + Dlssg030.Ver + " · 真 DLSS 多帧生成（代理模式）");
            lbl030 = s030.BodyFixed("检查中…", 1);
            s030.Body("代理 DLL 内嵌原厂 DLSS-G 运行库，只拦截 nvngx_dlssg.dll 的加载 → 跑的是真 DLSS 帧生成（2X-6X）。\n"
                      + "不限游戏类型：单机 3A 只放两个文件即可；网游多一步改显卡名。与方案 A 互斥，二选一。\n"
                      + "作者出新版时：把下载到的压缩包**直接拖到本窗口任意位置** —— 自动识别、归位资源包，"
                      + "并重抄到已装本方案的游戏。不用解压，也不用找目录。\n"
                      + "前置条件、6X 边界、各游戏实测清单都在「说明」页 → 帧生成方案详解。");
            // 通用游戏（3A 单机）用的入口名：作者 alternatives\README.md 的 6 选 1。
            //  默认 version.dll；少数游戏不加载它时，换成 winmm / dbghelp / dinput8 / dxgi / d3d12。
            cbEntry = new RoundCombo();
            cbEntry.Items.AddRange(Dlssg030.GenericEntries);
            int ei = 0;
            for (int i = 0; i < Dlssg030.GenericEntries.Length; i++)
                if (Dlssg030.GenericEntries[i].Equals(Cfg.DlssgGenericEntry, StringComparison.OrdinalIgnoreCase)) { ei = i; break; }
            cbEntry.SelectedIndex = ei;
            cbEntry.SelectedIndexChanged += delegate
            {
                if (cbEntry.SelectedIndex < 0 || cbEntry.SelectedIndex >= Dlssg030.GenericEntries.Length) return;
                Cfg.DlssgGenericEntry = Dlssg030.GenericEntries[cbEntry.SelectedIndex];
                Program.SetConfigStr("dlssg", "genericEntry", Cfg.DlssgGenericEntry);
                Log("通用入口已设为 " + Cfg.DlssgGenericEntry + "（部署时用）");
                RefreshFgSummary();
            };
            // 入口下拉单独占一行（整行铺满）：挤在标签右边时宽度不够，被标签盖住只剩尾巴
            s030.Body("通用游戏入口（3A 单机）—— 游戏不加载 version.dll 时换一个，6 个名字内嵌的运行库完全相同：");
            s030.Block(cbEntry, Theme.S(30));
            btn030On = new FlatBtn(); btn030On.Text = "切换到本方案"; btn030On.Kind = BtnKind.Success; btn030On.Width = Theme.S(120);
            btn030On.Click += delegate { Do030Deploy(); };
            btn030Off = new FlatBtn(); btn030Off.Text = "停放（回到原生）"; btn030Off.Kind = BtnKind.Primary; btn030Off.Width = Theme.S(132);
            btn030Off.Click += delegate { Do030Undeploy(); };
            var btn030Pre = new FlatBtn(); btn030Pre.Text = "前置条件检查"; btn030Pre.Width = Theme.S(116);
            btn030Pre.Click += delegate { Do030Precheck(); };
            s030.Buttons(btn030On, btn030Off, btn030Pre);
            p.Add(s030);

            var s3 = new Sec("帧生成参数");
            cbRouter = new RoundCombo(); cbRouter.Items.AddRange(new object[] { "SM86", "SM75" });
            cbKernel = new RoundCombo(); cbKernel.Items.AddRange(new object[] { "PTX", "Auto", "Cubin" });
            cbBilinear = new RoundCombo(); cbBilinear.Items.AddRange(new object[] { "0 · 精确", "1 · 近似" });
            // 上限由运行库决定，不是由游戏决定：
            //  310.9 运行库（本工具的 0.3.5 包，代理 version.dll 30,021,920 B = 28.63 MB）= 5 → 6X
            //  310.1 / 0.2.x 运行库 = 3 → 4X（再往上选也不会生效）
            //  作者 README 原话：「310.9 运行库新增 6X（MaxGeneratedFrames 上限由 3 提到 5）」；
            //  出厂 ini 注释：「5 = up to 6X, 3 = up to 4X … 6X on the 310.9 build, 4X on the 310.1 build」
            //  v2.10.2 之前这里只写到 3（4X），把 6X 挡在了 UI 外面。
            cbMaxFrames = new RoundCombo();
            cbMaxFrames.Items.AddRange(new object[] { "1 · 2X", "2 · 3X", "3 · 4X", "4 · 5X", "5 · 6X" });
            cbLogLevel = new RoundCombo(); cbLogLevel.Items.AddRange(new object[] { "0 · 关闭", "1 · 错误", "2 · 诊断", "3 · 详细" });
            // NR 的上游文档（wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass）明写其设计约束
            //  "是用设备挂死换来的"；2026-09-16 实测与 4X 帧生成同开 → 鸣潮两次 GPUCrash。
            //  关于驱动：上游的 ≥616.56 只对 RTX 50 是硬门槛（模型随应用提供，旧驱动照样能跑），
            //  真正要避开的是 ≥616.64（NR 每次 evaluate 都在 NVIDIA 自家 NGX 运行时 fault）。
            //  → 这次崩溃与驱动版本无关，升级驱动解决不了；所以默认「关闭」列在前面。
            cbNr = new RoundCombo(); cbNr.Items.AddRange(new object[] { "关闭（推荐）", "开启" });
            cbRouter.SelectedIndex = Cfg.DlssgRouter == "SM75" ? 1 : 0;
            cbKernel.SelectedIndex = Cfg.DlssgKernel == "Auto" ? 1 : (Cfg.DlssgKernel == "Cubin" ? 2 : 0);
            cbBilinear.SelectedIndex = Cfg.DlssgBilinear == 1 ? 1 : 0;
            cbMaxFrames.SelectedIndex = Math.Max(0, Math.Min(4, Cfg.DlssgMaxFrames - 1));
            cbLogLevel.SelectedIndex = Math.Max(0, Math.Min(3, Cfg.DlssgLogLevel));
            cbNr.SelectedIndex = Cfg.DlssgNrEnabled ? 1 : 0;
            s3.Pair("架构 (Router)", cbRouter, Theme.S(140), "最大生成帧", cbMaxFrames, Theme.S(140));
            s3.Pair("内核类型", cbKernel, Theme.S(140), "日志级别", cbLogLevel, Theme.S(140));
            s3.Row("光流精度", cbBilinear, Theme.S(140));
            s3.Row("DLSS 5 神经渲染 NR", cbNr, Theme.S(140));
            cbFgAutoEntry = new RoundCheck();
            Theme.StyleCheck(cbFgAutoEntry, "自动选择入口（推荐：已知游戏用专用入口，激活率更高）");
            cbFgAutoEntry.Checked = Cfg.DlssgAutoEntry;
            cbFgAutoEntry.CheckedChanged += delegate
            {
                Cfg.DlssgAutoEntry = cbFgAutoEntry.Checked;
                SaveCfgBool("dlssg", "autoEntrypoint", cbFgAutoEntry.Checked);
            };
            s3.Pair(null, cbFgAutoEntry, 0, null, null, 0);
            // v2.1.0：反作弊游戏自动改走 d3d12 入口（常规入口会被按文件名拦掉）
            cbFgD3d12 = new RoundCheck();
            Theme.StyleCheck(cbFgD3d12, "反作弊游戏自动改用 d3d12 入口（绝区零 / 鸣潮 等；常规入口会被拦）");
            cbFgD3d12.Checked = Cfg.DlssgAntiCheatD3d12;
            cbFgD3d12.CheckedChanged += delegate
            {
                Cfg.DlssgAntiCheatD3d12 = cbFgD3d12.Checked;
                SaveCfgBool("dlssg", "antiCheatD3d12", cbFgD3d12.Checked);
            };
            s3.Pair(null, cbFgD3d12, 0, null, null, 0);
            s3.BodyFixed("※ " + Dlssg030.Knowledge("nnr", "5X / 6X 只有 310.9 运行库才认")
                       + "（" + Dlssg030.PackCaption() + "）。"
                       + "\n※ 上限只是天花板：实际倍率由游戏自己的帧生成菜单决定，很多游戏只给到 4X。", 2);
            btnFgApply = new FlatBtn(); btnFgApply.Text = "应用参数"; btnFgApply.Kind = BtnKind.Primary; btnFgApply.Width = Theme.S(100);
            btnFgApply.Click += delegate { DoApplyFgCfg(); };
            s3.Buttons(btnFgApply);
            p.Add(s3);
        }

        // ---------------------- 页 4：游戏库（封面卡片网格 · 全页单一滚动条） ----------------------
        void BuildPageLibrary()
        {
            var p = pages[P_LIB];
            p.AddHead("游戏库", Program.ShareBuild
                ? "全平台游戏 · 右键管理 · 选中后可开 DX12 / 帧生成"
                : "全平台游戏 · 封面浏览 · 右键管理");

            // 顶栏（不套盒子，对齐启动器的 #topbar）：左搜索、右筛选
            var s1 = new Sec("");                       // 空标题 = 工具条模式
            rfSearch = new RoundField(Theme.S(320), "搜索游戏名 / 目录（按 / 聚焦）");  // 宽度由工具条拉伸
            txtSearch = rfSearch.Box;
            txtSearch.TextChanged += delegate { SearchDebounce(); };
            cbPlat = new RoundCombo();
            cbPlat.DropDownStyle = ComboBoxStyle.DropDownList;
            cbPlat.Items.AddRange(PlatNames);
            cbPlat.SelectedIndex = 0;
            cbPlat.SelectedIndexChanged += delegate { FillGameList(); };
            cbSort = new RoundCombo();
            cbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSort.Items.AddRange(new object[] { "最近游玩", "名称", "平台" });
            cbSort.SelectedIndex = 0;
            cbSort.SelectedIndexChanged += delegate
            {
                lastOrderValid = false;   // 用户主动换排序 → 作废"保持原顺序"，按新规则重排
                FillGameList();
            };
            chkFavOnly = new RoundCheck();
            Theme.StyleCheck(chkFavOnly, "仅收藏");
            chkFavOnly.CheckedChanged += delegate { FillGameList(); };
            chkShowHidden = new RoundCheck();
            Theme.StyleCheck(chkShowHidden, "显示已隐藏");
            chkShowHidden.CheckedChanged += delegate { FillGameList(); };
            cbPlat.Width = Theme.S(124);
            cbSort.Width = Theme.S(124);
            s1.Bar2(rfSearch, chkFavOnly, cbPlat, cbSort);

            btnScanGames = new FlatBtn(); btnScanGames.Text = "扫描游戏"; btnScanGames.Kind = BtnKind.Primary; btnScanGames.Width = Theme.S(112);
            btnScanGames.Click += delegate { RunScan(true); };
            var btnAddRoot = new FlatBtn(); btnAddRoot.Text = "添加目录…"; btnAddRoot.Width = Theme.S(100);
            btnAddRoot.Click += delegate { DoAddScanRoot(); };
            // 分享版没有封面这一套，按钮也不建（DoFetchCovers 内部另有 ShareBuild 兜底）
            if (!Program.ShareBuild)
            {
                btnCover = new FlatBtn(); btnCover.Text = "获取封面"; btnCover.Width = Theme.S(96);
                btnCover.Click += delegate { DoFetchCovers(); };
            }
            btnAddGame = new FlatBtn(); btnAddGame.Text = "添加游戏…"; btnAddGame.Width = Theme.S(96);
            btnAddGame.Click += delegate { DoAddGame(); };
            btnLaunchSel = new FlatBtn(); btnLaunchSel.Text = "启动选中"; btnLaunchSel.Kind = BtnKind.Success; btnLaunchSel.Width = Theme.S(100);
            btnLaunchSel.Click += delegate { DoLaunchGame(); };
            s1.Buttons(Program.ShareBuild
                ? new Control[] { btnScanGames, btnAddRoot, btnAddGame, btnLaunchSel }
                : new Control[] { btnScanGames, btnAddRoot, btnCover, btnAddGame, btnLaunchSel });
            s1.Pair(null, chkShowHidden, 0, null, null, 0);
            lblLibStat = s1.BodyFixed("尚未扫描 —— 点「扫描游戏」", 1);
            lblSel = s1.BodyFixed("未选中 —— 单击卡片选中，右键卡片可 启动 / 打开目录 / 收藏 / 重命名 / 私密 / 隐藏 / 移除", 1);
            p.Add(s1);

            // 卡片区也不套盒子：启动器就是「标题 + 裸网格」，套一层面板只会多一圈割裂的边
            var s2 = new Sec("");
            var grid = new CardGrid();
            grid.WrapContents = true;
            grid.FlowDirection = FlowDirection.LeftToRight;
            grid.BackColor = Theme.Bg;
            grid.Padding = new Padding(Theme.S(6));
            grid.AutoScroll = false;                    // 关掉内层滚动：整页只有一条滚动条
            // 分享版：无封面 → 卡片改成紧凑信息行（320×64），宽度小的窗口也能排下 1~2 列
            libCardW = Program.ShareBuild ? Theme.S(320) : Theme.S(148);
            libCardH = Program.ShareBuild ? Theme.S(64) : Theme.S(222);
            grid.CellW = libCardW + Theme.S(14);        // 卡片宽 + 左右间距
            grid.CellH = libCardH + Theme.S(14);        // 卡片高（分享版=信息行）+ 上下间距
            grid.PadPx = Theme.S(2);
            grid.Tag = s2;                              // 高度变化时通知所属分区重排
            flGames = grid;
            s2.Block(flGames, Program.ShareBuild ? Theme.S(96) : Theme.S(240));
            p.Add(s2);

        }

        // ---------------------- 页 6：说明（长文都挪到这一页） ----------------------
        void BuildPageHelp()
        {
            var p = pages[P_HELP];
            p.AddHead("说明", Program.ShareBuild
                ? "能做什么 · 插件怎么放 · 风险提示"
                : "方案来源 · 工作原理 · 使用与判据");

            if (Program.ShareBuild)
            {
                // 分享版说明页：只讲三件事，句子尽量短（用户要求：别高深、别字多）
                var sShare = new Sec("这个软件能做什么");
                sShare.Body("· 让 RTX 20 / 30 系也能开「帧生成」—— 游戏更流畅、操作更跟手\n"
                + "· 自动找出你电脑上的游戏，一键启动（可带 DX12 参数）\n"
                + "· 启动完直接告诉你是 DX11 还是 DX12，不用猜\n"
                + "· 到底有没有生效，「运行诊断」里一句话说清");
                p.Add(sShare);

                var sPlug = new Sec("插件怎么放 / 怎么更新");
                sPlug.Body("① 出新版了？把压缩包直接拖到窗口上就行\n"
                + "   程序自己认出是哪个版本、放好位置，再问你要不要更新到装过的游戏\n"
                + "② 手动放（左下角那行字就是实际位置，照着找）\n"
                + "   便携版：和 Fluxion.exe 同一个文件夹\n"
                + "   安装版：C:\\ProgramData\\Fluxion\n"
                + "③ 三个插件夹 —— 出新版就把里面的同名文件替换掉\n"
                + "   dlssg\\source\\        通用代理       整个文件夹覆盖\n"
                + "   dlssg030-pack\\        签名版 0.3.x   common 和 alts 都覆盖\n"
                + "   xess-pack\\zzz\\        绝区零专用     换 d3d12.dll 和 libxess*.dll\n"
                + "④ 换完必做一步：回「帧生成」页点一次「开启选中」，再点「运行诊断」\n"
                + "   （代理是点「开启选中」时才拷进游戏目录的，不点不生效）\n"
                + "   保险做法：换之前把旧文件改名留着，出问题改回来");
                p.Add(sPlug);

                var sRisk = new Sec("风险提示");
                sRisk.Body("· 给带反作弊的游戏（绝区零 / 鸣潮）注入第三方 DLL 可能封号，风险自负\n"
                + "· 「显卡名伪装」会改掉整机看到的显卡型号（可一键还原）\n"
                + "· 帧生成要先在 Windows 开「硬件加速 GPU 调度」，否则不生效\n"
                + "· 竞技射击（CS2 / Valorant / 三角洲）不建议开");
                p.Add(sRisk);
            }

            if (!Program.ShareBuild)
            {
                var s1 = new Sec("帧生成方案：技术栈与来源");
                s1.Body(Dlssg.HardwareFgNote());
                p.Add(s1);
            }

            if (!Program.ShareBuild)
            {
                var s2 = new Sec("怎么用 / 怎么确认生效");
                s2.Body("① 到「游戏库」页点一张卡片选中游戏（或在「帧生成」页的「目标游戏」下拉里直接选）"
                      + "\n② 「帧生成」页 → 先看「方案推荐」那两行 → 点对应区块的「切换到本方案」→ 装好后完全退出游戏再重启"
                      + "\n③ 游戏设置里出现「DLSS 帧生成」= 成功；只有 FSR 选项 = 入口没被调用，点「换入口重试」"
                      + "\n④ 回工具点「运行诊断」：日志里出现 evaluate 事件才算真的在补帧");
                p.Add(s2);
            }

            if (!Program.ShareBuild)
            {
                var s3 = new Sec("使用须知（实测结论）");
                s3.Body("· 帧生成需要 HAGS 开启（设置 → 系统 → 显示 → 图形 → 硬件加速 GPU 调度）\n" +
                        "· 基础帧低于 40 时开启会明显变糊、延迟增大\n" +
                        "· 竞技射击（CS2 / Valorant / 三角洲）不建议开启\n" +
                        // 旧文案「8GB 显存建议 2X」是错的：作者实测「帧生成的显存增量只随输出分辨率变化，
                        // 与倍率无关（2X 与 6X 占用相同）」。已按作者数据改正。
                        "· 倍率不额外吃显存（作者实测：显存增量只随输出分辨率变化，2X 与 6X 占用相同）\n" +
                        "· 游戏更新可能覆盖代理 DLL，更新后请重新开启\n" +
                        "· 判断代理是否真正生效，看「运行诊断」里的日志目录是否出现 jsonl\n" +
                        "· 带内核反作弊的游戏（绝区零 / 鸣潮）：用 d3d12 入口（代理 + 系统原件 + 载体三件套），\n" +
                        "  常规入口会被反作弊按文件名拦掉；封号风险由你自己承担\n" +
                        "· 反作弊若把代理改名隔离（version.dll.数字），文件不再被加载，点「清理残留」回收空间");
                p.Add(s3);
            }

            if (!Program.ShareBuild)
            {
                var s4 = new Sec("反作弊与入口选择");
                s4.Body("带内核反作弊的二游（绝区零 / HoYoKProtect，鸣潮 / 腾讯 ACE）会按【文件名】拦掉 version.dll、"
                      + "winmm.dll 这些常规入口（实测报错 11008），所以本工具对它们自动改用 d3d12.dll —— "
                      + "DX12 游戏必须加载的系统库名，拦不掉。"
                      + "\n\n但请明白：这只解决「装得进去」，不解决封号风险。注入第三方 DLL 属于明确的违约行为，"
                      + "社区也记录过反作弊直接拦截的案例；封的是你的账号，工具无法撤销。要不要冒险只能你自己定。");
                p.Add(s4);
            }

            // ---------- 方案详解（从「帧生成」页搬过来的长文案，放这里不挡视线）----------
            if (!Program.ShareBuild)
            {
                var sPlan = new Sec("帧生成方案详解（怎么选 / 前置条件 / 6X 边界）");
                sPlan.Body("【方案 A · OptiScaler 注入】绝区零 / 鸣潮专用"
                         + "\n· 绝区零 = d3d12.dll 入口 · 伪装 RTX 5090 · 帧生成走 DLSSG→XeFG 翻译（Intel XeSS 引擎执行插帧）"
                         + "\n· 鸣潮 = dxgi.dll 入口 · 帧生成走游戏原生 DLSSG（由 SM86 解锁让 30 系可用），同时带 DLSS 5 神经渲染 NR"
                         + "\n· 游戏内按 F10 呼出面板；用官方启动器启动若报文件缺失，直接双击游戏 exe"
                         + "\n· 与 0.3.x 互斥：两套代理抢同一个入口 DLL，只能装一套；切换只改入口 DLL 的名字（入口.parked.方案），不删任何文件"
                         + "\n· 特别注意（2026-09 起的事实）：绝区零 3.0+ 在 DX12 模式下游戏自带超分与帧生成 —— "
                         + "先在游戏内「设置 → 画面 → 高级 → 帧生成」确认；有 FSR 帧生成就直接用，不必注入。"
                         + "注入路线只在「要跑 DLSS 帧生成（官方限 40 系）」时才需要，且该选项要靠显卡名伪装才会出现。");
                sPlan.Body("【0.3.x · DLSSG for SM86】任意游戏可用"
                         + "\n· 代理 DLL 内嵌未修改的原厂 DLSS-G 运行库，只拦截 nvngx_dlssg.dll 的加载 → 游戏侧调用不变，跑的是真 DLSS 帧生成（2X-6X）"
                         + "\n· 入口：绝区零 d3d12.dll / 鸣潮 dxgi.dll；通用游戏默认 version.dll，不加载时换成 winmm / dbghelp / dinput8 / dxgi / d3d12"
                         + "\n· 适用面：不限游戏类型，只要游戏自己会去请求 DLSS 帧生成就能用。单机 3A 一般开箱即用"
                         + "（作者实测：黑神话悟空 / 赛博朋克 2077 / 地平线 6 可开 4X，Resonance 在 310.9 下默认 6X）；"
                         + "网游多两道门槛：多数需要先把显卡名伪装成 RTX 5090，并且游戏菜单里得先出现帧生成选项"
                         + "\n· 版本：本工具带的是 " + Dlssg030.Ver + "（20+30 系合并包）。相对我们上一份 0.3.2 修掉两个真问题："
                          + "0.3.5 修「游戏重建帧生成特性（切菜单 / 改分辨率画质 / 开关帧生成）后用错优化内核 → 生成帧花屏、玩久随机崩溃」，"
                          + "0.3.4 修 0.3.3 让 NVIDIA 自己的 DLSS 超分模型也吃到架构改写而挂 GPU；"
                          + "0.3.3 起架构改写提前到游戏启动并对外报 RTX 50，Streamline 2.8 的游戏不再一启动就卸掉帧生成插件；"
                         + "0.3.2 重写了 310.9 的 26 个推理内核，生成画面与官方 DLSS-G 逐位一致（作者实测 3080 Ti 对 5070、2080 Ti 对 3080 Ti 逐位相同）；"
                         + "20 系（SM75）与 30 系共用同一份出厂 INI，内核族按物理显卡自动选，不需要额外加键"
                         + "\n· " + Dlssg030.Knowledge("upgrade-path", "可升级路径只有 0.3.2 或 0.3.5+，0.3.3 / 0.3.4 要跳过")
                         + "\n· 出厂 INI 的 Optimized = 1（全部加速且逐位一致）仍是最优档，本工具不改它；"
                         + "0.3.2 起的 2 / 3 档是有损加速（对官方画面 PSNR 约 50 dB，更快），需要时手工改 ini"
                         + "\n· 前置条件：① 驱动至少 591.86；若要用 DLSS 5 NR，请停在 616.56（≥616.64 会让 NR 在 NGX 里 fault）② 网游常需把显卡名伪装成 RTX 5090 "
                         + "③ 装新版前要清掉旧版 version / sm86 残留（部署时自动清）④ 6X 需驱动支持 DLSS 4.5 且游戏原生支持"
                         + "\n· 6X 的边界：上限由游戏侧插件决定 —— 游戏自带较新的 Streamline 插件（支持 Dynamic MFG）时，"
                         + "装 310.9 + MaxGeneratedFrames=5 可跑到 6X；游戏只支持 4X 时本项目无法把它抬上去"
                         + "（日志 dlssg_sm86\\logs\\native_*.jsonl 里的 generated_count：3 = 4X，5 = 6X）。"
                         + "\n· 旧版代理（14.94 MB，内嵌 310.1）只认 MaxGeneratedFrames = 1..3 —— 写 5 会让它整份配置报错，"
                         + "所以本工具对旧版代理一律夹到 3，并在日志里提示升级。");
                p.Add(sPlan);
            }

            // 分享版没有封面（列表是信息行），这一节直接不出现
            if (!Program.ShareBuild)
            {
                var s5 = new Sec("封面库");
                s5.Body("游戏库的封面按这个顺序取：① 你自放的图片 → ② 缓存的 Steam 官方竖版封面 → ③ 按游戏名生成的颜色卡。"
                      + "\n自放：把图片按「卡片标题.png / .jpg」命名，放进下面的目录即自动生效（标题就是卡片上显示的名字）："
                      + "\n" + CoverArt.CoversDir
                      + "\n联网补齐：在「游戏库」页点「获取封面」。只访问 Steam 官方域名（steamstatic / steamcommunity），"
                      + "走「设置」页里配置的代理；关掉联网就只用本地图标与颜色卡。");
                p.Add(s5);
            }

            var s6 = new Sec("数据与隐私");
            s6.Body("游戏列表、收藏 / 私密 / 隐藏标记、最近游玩时间只写在本机：" + Program.DataDir
                  + "\n  · games.json —— 忽略清单 + 手动添加的游戏"
                  + "\n  · library.json —— 收藏 / 私密 / 隐藏 / 重命名 / 平台与 appid"
                  + (Program.ShareBuild ? "" : "\n  · covers\\\\ —— 封面图缓存")
                  + "\n「私密」的游戏不出现在「全部」列表（在「平台分类」里选「私密」才可见）；「隐藏」需要勾上「显示已隐藏」。");
            p.Add(s6);
        }

        // ---------------------- 页 5：实时监控 ----------------------
        void BuildPageMonitor()
        {
            var p = pages[P_MON];
            p.AddHead("实时监控", "CPU / GPU / 内存曲线与运行日志");

            var s1 = new Sec("实时曲线（最近 90 个采样）");
            var tlp = new TableLayoutPanel();
            tlp.ColumnCount = 3; tlp.RowCount = 1;
            tlp.BackColor = Theme.Panel;
            tlp.Margin = Padding.Empty;
            tlp.Padding = Padding.Empty;
            for (int i = 0; i < 3; i++) tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlp.Height = Theme.S(120);

            spCpu = new Spark(); spCpu.Caption = "CPU %"; spCpu.LineColor = Theme.Accent;
            spGpu = new Spark(); spGpu.Caption = "GPU %"; spGpu.LineColor = Theme.Ok;
            spMem = new Spark(); spMem.Caption = "内存 %"; spMem.LineColor = Theme.Cyan;
            spCpu.Dock = DockStyle.Fill; spCpu.Margin = new Padding(0, 0, Theme.S(12), 0);
            spGpu.Dock = DockStyle.Fill; spGpu.Margin = new Padding(0, 0, Theme.S(12), 0);
            spMem.Dock = DockStyle.Fill; spMem.Margin = Padding.Empty;
            tlp.Controls.Add(spCpu, 0, 0);
            tlp.Controls.Add(spGpu, 1, 0);
            tlp.Controls.Add(spMem, 2, 0);
            s1.Block(tlp, Theme.S(120));
            p.Add(s1);

            var s2 = new Sec("运行日志（与数据目录 logs\\ 同步）");
            log = new RichTextBox();
            log.ReadOnly = true;
            log.WordWrap = true;                        // 日志不需要横向滚动条（v2.7.1 审计：底部那条 hscroll 多余）
            log.BorderStyle = BorderStyle.None;
            log.ScrollBars = RichTextBoxScrollBars.Vertical;
            log.BackColor = Theme.Subtle;
            log.ForeColor = Theme.Text;
            log.Font = Theme.Mono(9f);
            try
            {
                typeof(RichTextBox).InvokeMember("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                    null, log, new object[] { true });
            }
            catch { }
            var logBox = new Panel();
            logBox.BackColor = Theme.Subtle;
            logBox.BorderStyle = BorderStyle.None;
            logBox.Padding = new Padding(Theme.S(6));
            log.Dock = DockStyle.Fill;
            logBox.Controls.Add(log);
            log.Text = "（暂无日志 —— 启动后日志会实时同步到这里）";
            log.ForeColor = Theme.TextFaint;
            s2.Block(logBox, Theme.S(268));
            p.Add(s2);
        }

        // ---------------------- 页 7：设置 ----------------------
        void BuildPageSettings()
        {
            var p = pages[P_SET];
            p.AddHead("设置", Program.ShareBuild
                ? "窗口与托盘 · 下载源 · 界面主题 · 数据目录"
                : "场景联动 · 窗口与托盘 · 下载源与联网封面 · 界面主题");

            var s0 = new Sec("界面主题");
            cbTheme = new RoundCombo();
            cbTheme.Items.AddRange(new object[] { "浅色", "深色 · 曜石黑 + 电竞极光绿（Obsidian Cyber）" });
            cbTheme.SelectedIndex = (Cfg != null && Cfg.UiTheme == "dark") ? 1 : 0;
            cbTheme.SelectedIndexChanged += delegate { DoSwitchTheme(cbTheme.SelectedIndex == 1); };
            s0.Row("界面主题", cbTheme, Theme.S(260));
            s0.Body("深色采用现代电竞控制台（Obsidian Cyber Studio）视觉系统：\n" +
                    "· 强调色为电竞极光绿 #00DC82 与科技天青 #38BDF8 双色流光渐变\n" +
                    "· 深色底走沉浸曜石黑 + 钛灰卡片：#0B0F17 → #131B26 → #182230\n" +
                    "· 核心控件均搭载高精度 GDI+ 矢量微图标与专属微光呼吸视觉\n" +
                    "· Windows 11 下窗口圆角、标题栏与边框会一并跟随主题深邃着色\n" +
                    "切换后立即生效：程序会原地重建控件树，不用重启、不弹询问框。");

            // 界面动效三档。说明文案里的系统状态是**现算的** —— 否则用户看到"什么都没变"
            // 只会以为是我们没做，而不是系统把动画关了。依据见 docs/motion-and-ui-research.md。
            cbMotion = new RoundCombo();
            cbMotion.Items.AddRange(new object[] { "跟随系统", "始终开启", "关闭" });
            cbMotion.SelectedIndex = Motion.Mode == "on" ? 1 : (Motion.Mode == "off" ? 2 : 0);
            cbMotion.SelectedIndexChanged += delegate { DoSwitchMotion(cbMotion.SelectedIndex); };
            s0.Row("界面动效", cbMotion, Theme.S(260));
            s0.Body(Motion.OsAllows
                ? "系统的「为窗口内的控件和元素添加动画」当前处于开启状态，「跟随系统」即启用全部过渡。\n" +
                  "动效一律服务反馈、不做装饰：悬停与按下 80~110ms、选中药丸淡入与数值滚动 200~300ms，\n" +
                  "没有任何一段超过 500ms，也不会为了动画拖延数值的显示。"
                : "⚠ 系统的「为窗口内的控件和元素添加动画」当前处于关闭状态\n" +
                  "   （设置 → 辅助功能 → 视觉特效 → 动画效果）。此时「跟随系统」等于完全没有过渡，\n" +
                  "   界面观感与旧版一致。想看到悬停过渡、数值滚动、切页淡入，请把这里选成「始终开启」。\n" +
                  "动效只服务反馈、不做装饰，单段最长 300ms，都不会拖延数值显示。");
            p.Add(s0);

            if (!Program.ShareBuild)
            {
                var s1 = new Sec("场景与联动");
                cbScene = new RoundCombo();
                cbScene.Items.AddRange(new object[] { "游戏", "办公" });
                cbScene.SelectedIndex = Program.SceneOffice ? 1 : 0;
                cbScene.SelectedIndexChanged += delegate
                {
                    bool office = cbScene.SelectedIndex == 1;
                    Program.SetSceneOffice(office);
                    SyncProfileEnabled();
                    Log(office ? "已切换办公场景：游戏联动整体停用（临时优化自动还原）" : "已切换游戏场景：档位体系恢复");
                    // 游戏开着时切到办公：立刻收尾本轮联动（还原远控/电源），不再等游戏退出
                    if (office && linkActive) LinkGameExit();
                    UpdateLink();
                };
                s1.Row("场景总开关", cbScene, Theme.S(110));

                cbProfileGame = new RoundCombo();
                var shown = new List<string>();
                foreach (var g in Program.AllGameProcesses())
                {
                    string dn = Program.GameDisplayName(g);
                    if (!shown.Contains(dn)) { shown.Add(dn); cbProfileGame.Items.Add(dn); }
                }
                if (cbProfileGame.Items.Count > 0) cbProfileGame.SelectedIndex = 0;
                cbProfileGame.SelectedIndexChanged += delegate { if (!updatingProfile) SyncProfileCombo(); };

                cbProfile = new RoundCombo();
                cbProfile.Items.AddRange(new object[] { "自动", "FPS竞技", "网游MMO", "3A大作", "二游" });
                cbProfile.SelectedIndexChanged += delegate
                {
                    if (updatingProfile) return;
                    string game = cbProfileGame.SelectedItem != null ? cbProfileGame.SelectedItem.ToString() : null;
                    if (game == null || game.Length == 0) { Log("请先选择要指定的游戏"); return; }
                    string[] cats = new string[] { "", "fps", "mmo", "aaa", "gacha" };
                    string[] catNames = new string[] { "自动", "FPS竞技", "网游MMO", "3A大作", "二游" };
                    int idx = cbProfile.SelectedIndex;
                    string cat = idx >= 0 && idx < cats.Length ? cats[idx] : "";
                    if (cat.Length == 0) { Program.SetProfileOverride(game, null); Log("档位[" + game + "]恢复自动判定"); }
                    else { Program.SetProfileOverride(game, cat); Log("档位[" + game + "]手动指定为 " + catNames[idx]); }
                    UpdateLink();
                };
                s1.Pair("游戏", cbProfileGame, Theme.S(180), "档位", cbProfile, Theme.S(130));

                cbManual = new RoundCheck();
                Theme.StyleCheck(cbManual, "手动指定档位（关闭 = 全部按列表自动判定）");
                cbManual.Checked = Program.ManualMode;
                cbManual.CheckedChanged += delegate
                {
                    Program.SetManualMode(cbManual.Checked);
                    SyncProfileEnabled();
                    Log(cbManual.Checked ? "已切换手动挡：可对单个游戏指定档位" : "已切换自动挡：全部按列表自动判定");
                    UpdateLink();
                };
                s1.Pair(null, cbManual, 0, null, null, 0);

                cbAware = new RoundCheck();
                Theme.StyleCheck(cbAware, "游戏联动（只对竞技网游生效：CS2 / 无畏契约 / 三角洲；二游与 3A 不动你的远控）");
                cbAware.Checked = Cfg.AwareEnable;
                cbAware.CheckedChanged += delegate
                {
                    Cfg.AwareEnable = cbAware.Checked;
                    SaveCfgBool("gameAware", "enable", cbAware.Checked);
                    Log("游戏联动已" + (cbAware.Checked ? "开启" : "关闭"));
                    // 游戏开着时关掉联动：立刻收尾本轮联动（还原远控/电源）
                    if (!cbAware.Checked && linkActive) LinkGameExit();
                    UpdateLink();
                };
                s1.Pair(null, cbAware, 0, null, null, 0);

                var cbAwareOnlyFps = new RoundCheck();
                Theme.StyleCheck(cbAwareOnlyFps, "远控只对「竞技」档暂停（二游 / 3A / 网游 不动你的远控）");
                cbAwareOnlyFps.Checked = Cfg.AwareOnlyFps;
                cbAwareOnlyFps.CheckedChanged += delegate
                {
                    Cfg.AwareOnlyFps = cbAwareOnlyFps.Checked;
                    SaveCfgBool("gameAware", "onlyFps", cbAwareOnlyFps.Checked);
                    Log("远控联动范围：" + (cbAwareOnlyFps.Checked
                        ? "仅竞技档（二游/3A/网游不暂停远控）" : "所有已识别游戏都会暂停远控"));
                };
                s1.Pair(null, cbAwareOnlyFps, 0, null, null, 0);

                cbAutoStart = new RoundCheck();
                Theme.StyleCheck(cbAutoStart, "开机自启（计划任务，以管理员权限运行）");
                cbAutoStart.Checked = Program.IsAutoStart();
                cbAutoStart.CheckedChanged += delegate
                {
                    bool want = cbAutoStart.Checked;
                    Log(want ? "正在设置开机自启…" : "正在取消开机自启…");
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        if (want) Program.SetAutoStart(); else Program.RemoveAutoStart();
                        Log(want ? "已设置开机自启" : "已取消开机自启");
                    });
                };
                s1.Pair(null, cbAutoStart, 0, null, null, 0);

                s1.Body("办公场景 = 整机不触发任何游戏联动；游戏场景 = 按档位体系联动（临时电源 / 加速包 / 远控暂停）。\n"
                      + "档位：fps(竞技 FPS) / mmo(网游) / aaa(3A) / gacha(二游)。默认只有 fps 档会暂停远控 —— "
                      + "二游与 3A 只做电源与加速包联动，绝不碰你的远控会话。");

                // ---- 游戏分辨率档位（v3.8.0）----
                // 为什么要有它：CS2 / 无畏契约的分辨率原来是**写死在代码里的一条**，用户换显示器
                //   （换尺寸 / 换分辨率 / 换刷新率）之后没有第二个选项，只能去手改 config.json。
                //   这里列的是"这两个游戏的玩家实际会选的分辨率"（职业选手统计数据，见 Core.cs 档位表注释），
                //   并按本机主屏**实际支持的模式**标注可用性 —— 换屏后一眼能看出哪一档得先建自定义分辨率。
                ResLink.InvalidateModes();   // 每次构建设置页都重读主屏模式（换屏后重建即生效）
                cbResCs = new RoundCombo();
                cbResCs.Items.AddRange(ResComboItems(ResKeyCs));
                cbResCs.SelectedIndexChanged += delegate { OnResPresetPicked(ResKeyCs, cbResCs); };
                s1.Row("CS2 分辨率", cbResCs, Theme.S(214));

                cbResVal = new RoundCombo();
                cbResVal.Items.AddRange(ResComboItems(ResKeyVal));
                cbResVal.SelectedIndexChanged += delegate { OnResPresetPicked(ResKeyVal, cbResVal); };
                s1.Row("无畏契约分辨率", cbResVal, Theme.S(214));

                // ---- 进游戏时禁用的显示器（v3.9.0）----
                // 用户 2026-09-20：「启动游戏时再禁用，只禁用 p27 这一台就行」。
                // 注意这是**设备管理器**（PnP）层面的停用，和上面写的"分离副屏"不是一回事：
                //   分离只是把屏从桌面摘掉（设备管理器里仍显示"正常运行"，重启即回原生），
                //   停用才是他截图里看到的那条"已禁用"。退出游戏会自动启用回来。
                cbOffCs = new RoundCombo();
                cbOffCs.Items.AddRange(OffMonItems(ResKeyCs));
                cbOffCs.SelectedIndexChanged += delegate { OnResOffMonPicked(ResKeyCs, cbOffCs); };
                s1.Row("CS2 游戏时禁用", cbOffCs, Theme.S(214));

                cbOffVal = new RoundCombo();
                cbOffVal.Items.AddRange(OffMonItems(ResKeyVal));
                cbOffVal.SelectedIndexChanged += delegate { OnResOffMonPicked(ResKeyVal, cbOffVal); };
                s1.Row("无畏契约游戏时禁用", cbOffVal, Theme.S(214));

                // ---- 单屏也禁用（v3.9.2）----
                // 默认那条守卫是"桌面上只剩一台屏就别禁，会黑屏"。但单屏玩家要真拉伸，禁的正是这一台 ——
                //   不禁它，游戏照样读到 EDID 里的原生 16:9，Fill 也不会真拉伸。所以把选择权交回给用户。
                var cbSoloOff = new RoundCheck();
                Theme.StyleCheck(cbSoloOff, "允许禁用唯一在用屏（单屏拉伸需要）");
                cbSoloOff.Checked = Cfg.ResLinkSoloOff;
                cbSoloOff.CheckedChanged += delegate
                {
                    Cfg.ResLinkSoloOff = cbSoloOff.Checked;
                    SaveCfgBool("displayLink", "soloOff", cbSoloOff.Checked);
                    Log("[分辨率联动] 单屏禁用："
                        + (cbSoloOff.Checked
                            ? "已允许 —— 桌面上只剩一台屏时也会按规则禁用它（这正是单屏拉伸要的；可能黑屏一下，退出游戏自动启用回来）"
                            : "已关闭 —— 只剩一台在用屏时跳过禁用，避免黑屏"));
                };
                s1.Pair(null, cbSoloOff, 0, null, null, 0);

                btnResBase = new FlatBtn();
                btnResBase.Text = "还原目标 = 当前主屏";
                btnResBase.Width = Theme.S(158);
                btnResBase.Click += delegate
                {
                    string msg = Program.SetBaseToCurrent();
                    Log("[分辨率联动] " + msg);
                    RefreshResInfo(msg);
                };
                s1.Buttons(btnResBase);

                lblResInfo = s1.Body("");
                ResSyncCombos();              // 下拉回显（必须在事件绑定之后，靠 syncing 抑制触发）
                RefreshResInfo("");

                p.Add(s1);
            }

            // ---- 窗口与托盘 ----
            // 为什么独立成区：这属于「程序自身行为」，不是「系统优化项」。
            // v3.2.1 之前它被塞在性能优化页的「自定义项」对话框里（第 17 项，与
            // 「电源计划」「Defender 排除」并列），结果用户在设置页根本找不到入口。
            var s1b = new Sec("窗口与托盘");
            cbClose = new RoundCombo();
            // 注意：行布局里控件实测可用宽约 228px，长句会被裁成「…（后台继续运...」。
            //      所以下拉项只放短标签，完整含义写在下面的说明里。
            // 三项文案必须与 CloseComboText() 的输出**逐字一致** ——
            //  两处各写一套就会出现「日志说 A、界面显示 B」（探针 close_probe 专门盯这条）。
            //  也别加「（推荐）」这类后缀：默认值就是询问，而用户已经改成别的之后，
            //  标在别的项上反而会让人以为自己设错了。
            cbClose.Items.AddRange(new object[] {
                "每次询问",
                "直接最小化到托盘",
                "直接退出程序" });
            cbClose.SelectedIndex = CloseComboIndex(Cfg.CloseAsk, Cfg.CloseToTray);
            cbClose.SelectedIndexChanged += delegate
            {
                int i = cbClose.SelectedIndex;
                if (i < 0) return;
                // 三态 ↔ 两个配置字段（ui.closeAsk / ui.closeToTray）：
                //   0 询问     → closeAsk=true；closeToTray 保持原值（作为弹框里的默认选中项）
                //   1 直接托盘 → closeAsk=false, closeToTray=true
                //   2 直接退出 → closeAsk=false, closeToTray=false
                Cfg.CloseAsk = (i == 0);
                if (i == 1) Cfg.CloseToTray = true;
                if (i == 2) Cfg.CloseToTray = false;
                SaveCfgBool("ui", "closeAsk", Cfg.CloseAsk);
                SaveCfgBool("ui", "closeToTray", Cfg.CloseToTray);
                Log("关闭窗口行为：" + CloseComboText(Cfg.CloseAsk, Cfg.CloseToTray));
            };
            s1b.Row("关闭窗口时", cbClose, Theme.S(300));
            s1b.Body(Program.ShareBuild
                ? "点右上角 X 的行为。托盘菜单里的「退出」永远直接退出，不受这里影响。\n"
                + "· 每次询问：弹框让你选「最小化到托盘 / 退出程序」；勾了「记住我的选择」就不再问\n"
                + "· 直接最小化到托盘：点 X 就收进托盘（帧生成诊断继续跑）；想退出用托盘右键菜单\n"
                + "· 直接退出程序：点 X 直接退出\n"
                + "想恢复询问：选回第一项即可。"
                : "点右上角 X 的行为。托盘菜单里的「退出」永远直接退出，不受这里影响。\n"
                   + "· 每次询问：弹框让你选「最小化到托盘 / 退出程序」；勾了「记住我的选择」就不再问\n"
                   + "· 直接最小化到托盘：点 X 就收进托盘，联动与告警继续跑；想退出用托盘右键菜单\n"
                   + "· 直接退出程序：点 X 直接退出（游戏联动、硬件告警、帧生成诊断一并停止）\n"
                   + "想恢复询问：选回第一项即可。");
            p.Add(s1b);

            var s2 = new Sec(Program.ShareBuild ? "下载源" : "下载源与联网封面");
            if (Program.ShareBuild)
                s2.Body("下载代理设置：帧生成运行时 / 插件包从这里下载时走它（直连失败会自动回退到代理）。失败原因写进下方日志。");
            else
            s2.Body("封面来源分三级，逐级回退：\n"
                  + "① Steam 官方 CDN（steamstatic）：按 appid 试 7 种官方尺寸，实测直连可用\n"
                  + "② 按名字搜 Steam appid（steamcommunity）：这一步需要代理\n"
                  + "③ 联网搜图兜底（Bing 图片搜索，只取竖版大图）：会访问任意 https 图片站，"
                  + "相关性不保证，挑错了可用右键「设置封面…」换成自己的图\n"
                  + "直连与代理两种方式自动互相回退；失败原因都会写进下方日志。");
            var inpProxy = new RoundField(Theme.S(240), "");   // 原生 TextBox 方角白框与整套圆角控件打架
            inpProxy.Box.Text = Cfg.DlssgProxy;
            s2.Row("代理地址", inpProxy, Theme.S(240));
            var cbUseProxy = new RoundCheck();
            Theme.StyleCheck(cbUseProxy, "使用代理下载");
            cbUseProxy.Checked = Cfg.DlssgUseProxy;
            s2.Pair(null, cbUseProxy, 0, null, null, 0);
            if (!Program.ShareBuild)
            {
                var cbCovers = new RoundCheck();
                Theme.StyleCheck(cbCovers, "联网获取游戏封面（Steam 官方优先，找不到时联网搜图兜底）");
                cbCovers.Checked = Cfg.DlssgOnlineCovers;
                cbCovers.CheckedChanged += delegate
                {
                    Cfg.DlssgOnlineCovers = cbCovers.Checked;
                    SaveCfgBool("dlssg", "onlineCovers", cbCovers.Checked);
                    Log("联网封面已" + (cbCovers.Checked ? "开启（游戏库点「获取封面」生效）" : "关闭（只用本地图标与颜色卡）"));
                };
                s2.Pair(null, cbCovers, 0, null, null, 0);
            }
            var btnSaveNet = new FlatBtn(); btnSaveNet.Text = "保存"; btnSaveNet.Kind = BtnKind.Primary; btnSaveNet.Width = Theme.S(84);
            btnSaveNet.Click += delegate
            {
                Cfg.DlssgProxy = inpProxy.Box.Text.Trim();
                Cfg.DlssgUseProxy = cbUseProxy.Checked;
                Program.SetConfigStr("dlssg", "proxy", Cfg.DlssgProxy);
                Program.SetConfigBool("dlssg", "useProxy", Cfg.DlssgUseProxy);
                Log("下载代理设置已保存：" + (Cfg.DlssgUseProxy ? Cfg.DlssgProxy : "直连"));
            };
            s2.Buttons(btnSaveNet);
            p.Add(s2);

            var sd = new Sec(Program.ShareBuild ? "数据目录（配置 / 日志 / 备份）" : "数据目录（配置 / 封面 / 日志 / 备份）");
            sd.Body("当前：" + Program.DataDir + "\n"
                  + "迁移是「搬移」而不是复制：先复制到新目录 → 逐项校验字节数 → 删除源，搬完原目录清空。\n"
                  + "为什么要改：默认的 %ProgramData%\\Fluxion 常被系统 ACL 锁成只读，"
                  + "配置文件写不回去 —— 主题、开关改完重启就丢，损坏的配置也永远修不回来。换到自己的目录彻底解决。");
            var btnMoveDir = new FlatBtn(); btnMoveDir.Text = "更改数据目录…"; btnMoveDir.Kind = BtnKind.Primary;
            btnMoveDir.Width = Theme.S(148);
            btnMoveDir.Click += delegate { DoChangeDataDir(); };
            var btnOpenDir = new FlatBtn(); btnOpenDir.Text = "打开"; btnOpenDir.Width = Theme.S(72);
            btnOpenDir.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Program.DataDir + "\""); }
                catch (Exception ex) { Log("打开目录失败: " + ex.Message); }
            };
            var btnExport = new FlatBtn(); btnExport.Text = "导出配置…"; btnExport.Width = Theme.S(104);
            btnExport.Click += delegate { DoExportConfig(); };
            var btnImport = new FlatBtn(); btnImport.Text = "导入配置…"; btnImport.Width = Theme.S(104);
            btnImport.Click += delegate { DoImportConfig(); };
            sd.Buttons(btnMoveDir, btnOpenDir, btnExport, btnImport);
            p.Add(sd);

            // 备份 / 隔离区的占用（P0-1）。这些东西每次部署、换包、卸载都在长，
            //  原来全工程没有一个入口看得见 —— 1.48 GB 躺在 C 盘上没人知道能不能删。
            var sj = new Sec("备份与回收");
            lblJunk = sj.BodyFixed("备份占用：统计中…", 2);
            sj.Body("· 「回收」是**送回收站**，不是硬删，随时能在回收站里还原。\n"
                  + "· 每个归属（整包一份 / 每个游戏一份）里最新的那一份永远留着 —— 卸载和换方案回滚要读它，"
                  + "删了游戏自带文件就恢复不了。选「不限时长」也不会动它们。\n"
                  + "· 想看具体是哪几个目录、各占多少，点「备份明细」，那里可以逐项回收。");
            //  时长做成可选项而不是写死 30 天：默认的 30 天挡的是"刚换完包就一键清空"这种手滑，
            //  但换包 / 卸载留下的备份常常几天内就想甩掉 —— 给档位，不偷偷放宽默认值。
            cbJunkAge = new RoundCombo();
            cbJunkAge.Items.AddRange(new object[] { "30 天前（推荐）", "14 天前", "7 天前", "不限时长" });
            cbJunkAge.DropDownStyle = ComboBoxStyle.DropDownList;
            cbJunkAge.SelectedIndex = 0;
            sj.Row("回收范围", cbJunkAge, Theme.S(272));   // 回填后是「30 天前（推荐）· 0 项 / 0.0 MB」，210 会被截断
            var btnJunkList = new FlatBtn(); btnJunkList.Text = "备份明细"; btnJunkList.Width = Theme.S(96);
            btnJunkList.Click += delegate { ShowJunkDialog(); };
            var btnJunkClean = new FlatBtn(); btnJunkClean.Text = "回收老备份";
            btnJunkClean.Kind = BtnKind.Danger; btnJunkClean.Width = Theme.S(112);
            btnJunkClean.Click += delegate { DoJunkClean(JunkDays()); };
            sj.Buttons(btnJunkList, btnJunkClean);
            p.Add(sj);
            SetJunkLabel();   // 建页时不扫盘（同步扫会让页面卡一下），挂好标签后再统计

            var s3 = new Sec("安全与还原");
            s3.Body(Program.ShareBuild
                ? "· 数据目录 " + Program.DataDir + "：配置、日志、备份都在此，卸载本工具也不会删除\n"
                + "· 帧生成代理只往游戏目录放 1~2 个文件，删除即还原，也可用 Steam「验证文件完整性」\n"
                + "· 本工具不修改游戏本体、不注入竞技游戏、不绕过反作弊"
                : "· 数据目录 " + Program.DataDir + "：配置、日志、备份都在此，卸载本工具也不会删除\n"
                + "· 所有注册表 / 电源 / 网卡修改在写入前自动备份到 backup\\，可用「恢复备份」完整还原\n"
                + "· 帧生成代理只往游戏目录放 1~2 个文件，删除即还原，也可用 Steam「验证文件完整性」\n"
                + "· 驱动配置（NVAPI）写入 NVIDIA 面板同名档位，可用「恢复默认」清除\n"
                + "· 本工具不修改游戏本体、不注入竞技游戏、不绕过反作弊");
            var btnOpen = new FlatBtn(); btnOpen.Text = "打开数据目录"; btnOpen.Width = Theme.S(112);
            btnOpen.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Program.DataDir + "\""); }
                catch (Exception ex) { Log("打开目录失败: " + ex.Message); }
            };
            var btnLogs = new FlatBtn(); btnLogs.Text = "打开发日志"; btnLogs.Width = Theme.S(96);
            btnLogs.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + Path.Combine(Program.DataDir, "logs") + "\""); }
                catch (Exception ex) { Log("打开目录失败: " + ex.Message); }
            };
            s3.Buttons(btnOpen, btnLogs);
            p.Add(s3);
        }

        // ---------------------- 小工具 ----------------------
        // 表格控件统一入口：列宽在创建时按 DPI 换算一次，最后一列在 Resize 时补齐
        ListView NewList(string[] cols, int[] widths, int height)
        {
            var lv = new ListView();
            lv.View = View.Details;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.GridLines = false;
            lv.BorderStyle = BorderStyle.None;      // FixedSingle 的黑框又硬又廉价；无框靠分区圆角收边
            lv.BackColor = Theme.Panel;
            lv.ForeColor = Theme.Text;
            lv.Font = Theme.F(9.5f);
            lv.Height = height;
            lv.OwnerDraw = true;
            Buffered(lv);

            // 记住"建表默认列宽"，供右键「恢复默认列宽」复原（用户 2026-09-20：拖乱了没退路）
            int[] defW = new int[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                int w = (i < widths.Length && widths[i] > 0) ? Theme.S(widths[i]) : Theme.S(140);
                defW[i] = w;
                lv.Columns.Add(cols[i], w);
            }
            Theme.RememberWidths(lv, defW);

            // 列宽复原入口（v3.9.0）：原生 ListView 本来就支持拖列宽、也支持双击分隔线按内容自适应，
            //   但**拖乱之后没有任何退路** —— 尤其是把某列拖到只剩十几像素时，靠自己拖回来很别扭。
            // 为什么**只**给「恢复默认列宽」、不再给"全部按内容自适应"：
            //   实测本表三列内容宽合计 833 > 客户区 773（「当前值」单列就要 514）——「按内容撑开」
            //   必然冒出横向滚动条，而横条会把客户区高度压掉一行，用户视角就是"表格少了一行"。
            //   用户自己也说了「这个栏还能自动调节」—— 逐列自适应（双击分隔线）本来就在，不重复提供。
            var cmW = new ContextMenuStrip();
            var lvRef = lv;
            var miResetW = new ToolStripMenuItem("恢复默认列宽");
            miResetW.Click += delegate { Theme.ResetListWidths(lvRef); };
            cmW.Items.Add(miResetW);
            lv.ContextMenuStrip = cmW;

            lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                var g = e.Graphics;

                // 深邃钛灰表头底色（画刷走 Theme.Solid 缓存，理由见缓存区注释）
                g.FillRectangle(Theme.Solid(Theme.GridHead), e.Bounds);

                // 底部分隔线
                g.DrawLine(Theme.Hair(Theme.Line), e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
                else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                else flags |= TextFormatFlags.Left;

                Rectangle tr = new Rectangle(e.Bounds.Left + Theme.S(10), e.Bounds.Top, Math.Max(0, e.Bounds.Width - Theme.S(20)), e.Bounds.Height);
                TextRenderer.DrawText(g, e.Header.Text, Theme.FB(8.5f), tr, Theme.TextDim, flags);
            };

            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e)
            {
                // 整行铺底只在这里做一次。以前把底色铺在 DrawSubItem 里、一格一格铺，
                // 问题有两个：格子与格子之间会留 1px 接缝，最右列右侧的空档也没人画 ——
                // 那些缝里露出的就是 ListView 的 BackColor（浅色 = 纯白），看起来"有的色块更白、更割裂"。
                // DrawSubItem 拿到的 Bounds 是**单元格**的，DrawItem 拿到的才是整行。
                //
                // 底色用**同一个颜色**，不再用斑马纹：
                //   浅色主题的交替色是 #FFFFFF / #F8FAFC，只差 3% —— 这点差异在屏幕上看不出
                //   "这是交替条纹"，只看得出"这些行颜色不一样"，再叠上滚动时的重绘，
                //   就是用户 2026-09-19 反馈的"滑动时有的色块变得更白、更割裂"。
                //   行的区分改由底部一条极浅的分隔线交代（LineSoft = #F1F5F9 / #1A2434）。
                //   ⚠ Theme.GridAlt（斑马纹色）因此不再被引用，别当成漏改。
                var g = e.Graphics;
                // 选中判据用**行自己的状态**（e.Item.Selected），不要用 e.State/e.ItemState。
                // 实测（tools\ui_snapshot.py light lst 1）：控件里没有任何一行被选中
                // （SelectedIndices=0、每项 Selected=False），可绘制回调里的 Selected 位却是**行行都有**
                // ——按那个位画，整个列表会被涂成"选中态"的 Hover 底色 + 每行一条绿色强调条，
                // 看起来就是"这些行怎么跟别的不一样"。（用户 2026-09-19 截图里的白块/绿条即由此而来。）
                // 画刷/画笔全走缓存：滚动时这里是每行必经之路，任何临时 GDI+ 对象都会放大成掉帧。
                bool isSel = e.Item != null && e.Item.Selected;
                g.FillRectangle(Theme.Solid(isSel ? Theme.Hover : Theme.Panel), e.Bounds);
                if (isSel)
                {
                    // 选中行左边缘的微光指示条（整行只画一次，所以不放 DrawSubItem 里）
                    g.FillRectangle(Theme.Solid(Theme.Accent), e.Bounds.Left, e.Bounds.Top + Theme.S(2),
                        Theme.S(3), Math.Max(0, e.Bounds.Height - Theme.S(4)));
                }
                else
                {
                    g.DrawLine(Theme.Hair(Theme.LineSoft), e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
            };

            lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                Theme.NoteCell(lv, e.ItemIndex, e.ColumnIndex);   // 逐格自检：这一格"画过了"
                var g = e.Graphics;
                // 普通文字格走 TextRenderer（GDI），Graphics 的 SmoothingMode/TextRenderingHint
                // 对它完全无效，白设还拖着 AntiAlias 状态 —— 只有胶囊几何要用，挪进分支里。

                // 选中判据与 DrawItem 保持一致：用行自己的状态，不用 e.ItemState
                // （后者在部分绘制路径下会把"选中"位给到每一行，文字就会整列变强调绿）。
                bool isSel = e.Item != null && e.Item.Selected;
                // 行背景（选中高亮 / 分隔线）已由 DrawItem 整行铺好，这里只画内容。

                string txt = e.SubItem != null ? e.SubItem.Text : "";
                if (string.IsNullOrEmpty(txt)) return;

                // 状态胶囊标签（已生效 / 未生效 / 可实测… / 需处理）
                // 判据优先用 FillList 打的状态格标记，文字白名单只作兜底 —— 以前只认
                // "已生效/未生效/需处理"三个词，于是 🟡 那一档（可实测 / 已跳过 / 可关闭 …）
                // 掉进 else 被当普通文字画，颜色跟着丢。
                if (IsBadgeCell(e.SubItem, txt))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Color c = (e.SubItem != null && e.SubItem.ForeColor != Color.Empty) ? e.SubItem.ForeColor : Theme.Text;
                    Color bg = c == Theme.Ok ? Theme.OkSoft
                             : c == Theme.Warn ? Theme.WarnSoft
                             : c == Theme.Err ? Theme.ErrSoft : Theme.Subtle;

                    // 胶囊宽度按文字实测：以前写死 68px，"已偏离默认"这类 4 字以上会被截掉。
                    // 左边距 6 + 圆点 5 + 间距 4 = 15，右边距 4，再加 8 的安全余量 ——
                    // 这个余量不能再省：算少了就会把「已生效」也省略成「已…」
                    //（第一版余量给成 4，离屏渲染出来的就是「• 已…」，见 ui-snapshots\real_light_p1*.png）
                    // 测量走 Theme.TextW 缓存：滚动时同一文案反复测量纯属浪费。
                    int dotR = Theme.S(5);
                    int tw = Theme.TextW(txt, Theme.FB(8.5f));
                    int avail = Math.Max(Theme.S(30), e.Bounds.Width - Theme.S(8));
                    int badgeW = Math.Min(avail, Math.Max(Theme.S(44), tw + Theme.S(27)));
                    int badgeH = Math.Min(Math.Max(14, e.Bounds.Height - 4), Theme.S(18));
                    int bx = e.Bounds.Left + Theme.S(6);
                    int by = e.Bounds.Top + (e.Bounds.Height - badgeH) / 2;
                    Rectangle badgeRect = new Rectangle(bx, by, badgeW, badgeH);

                    Theme.FillRound(g, badgeRect, Theme.S(4), Theme.Solid(bg));
                    Theme.EdgeRound(g, badgeRect, Theme.S(4), Color.FromArgb(70, c), 1f);

                    // 胶囊发光微点
                    int dotX = bx + Theme.S(6);
                    int dotY = by + (badgeH - dotR) / 2;
                    g.FillEllipse(Theme.Solid(c), dotX, dotY, dotR, dotR);

                    // 胶囊文字
                    int tx = dotX + dotR + Theme.S(4);
                    Rectangle tr = new Rectangle(tx, by, Math.Max(0, badgeRect.Right - Theme.S(4) - tx), badgeH);
                    TextRenderer.DrawText(g, txt, Theme.FB(8.5f), tr, c,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    g.SmoothingMode = SmoothingMode.Default;
                }
                else
                {
                    // 普通单元格文字排版
                    Color tc = isSel ? Theme.Accent : (e.SubItem != null && e.SubItem.ForeColor != lv.ForeColor && e.SubItem.ForeColor != Color.Empty ? e.SubItem.ForeColor : Theme.Text);
                    Rectangle tr = new Rectangle(e.Bounds.Left + Theme.S(10), e.Bounds.Top, Math.Max(0, e.Bounds.Width - Theme.S(16)), e.Bounds.Height);
                    TextRenderer.DrawText(g, txt, Theme.F(9f), tr, tc,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            };

            FillLastColumn(lv, Theme.S(140));
            return lv;
        }
        // 状态格标记 + 兜底白名单（两者取或）。为什么要两个：
        //   主判据是 FillList 给状态格打的 Name 标记 —— 新增一档状态时不用回来改这里；
        //   白名单只兜 ✅/⚠️/无前缀 这三个**永远不变**的固定词，为了防"标记没传过来"时
        //   把绝大多数行（已生效 / 未生效 / 需处理）的胶囊降级成裸文字。
        //   🟡 那一档的文案取自 Core.cs 的各条 Status，会随功能增删，所以**不进白名单**
        //   （它只依赖上面的标记；万一标记丢了也只是少一层胶囊，文字颜色仍是对的）。
        const string BadgeName = "badge";
        static readonly string[] BadgeLabels = new string[] { "已生效", "未生效", "需处理" };
        static bool IsBadgeCell(ListViewItem.ListViewSubItem sub, string txt)
        {
            if (string.IsNullOrEmpty(txt)) return false;
            if (sub != null && sub.Name == BadgeName) return true;
            for (int i = 0; i < BadgeLabels.Length; i++) if (BadgeLabels[i] == txt) return true;
            return false;
        }

        static void FillLastColumn(ListView lv, int minW) { Theme.FillLastColumn(lv, minW); }

        void SaveCfgBool(string sec, string key, bool val)
        {
            try { Program.SetConfigBool(sec, key, val); } catch { }
        }

        // ==================== 游戏分辨率档位（v3.8.0） ====================
        // 识别键：cs → 配置里 proc 以 "cs" 开头的规则；VALORANT → proc 以 "VALORANT" 开关的规则。
        //   刻意不直接传 "cs2" 去 ResLink.RuleFor：配置里那一项可能被用户改成了别的前缀。
        const string ResKeyCs = "cs", ResKeyVal = "VALORANT";

        ResLinkRule ResRule(string key)
        {
            try
            {
                foreach (ResLinkRule r in Cfg.ResRules)
                {
                    if (r == null || r.Proc == null || r.Proc.Length == 0) continue;
                    if (r.Proc.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return r;
                }
            }
            catch { }
            return null;
        }

        // 下拉项：档位表 + （当前值不在表里就补一项"自定义·当前值"）
        //   标签与索引的映射全部由 ResLink 提供（单一来源），UI 只负责填进去。
        object[] ResComboItems(string key)
        {
            ResLinkRule r = ResRule(key);
            string proc = r != null ? r.Proc : key;
            return ResLink.ComboLabels(proc, r != null ? r.W : 0, r != null ? r.H : 0).ToArray();
        }

        int ResComboIndex(string key)
        {
            ResLinkRule r = ResRule(key);
            if (r == null) return 0;
            return ResLink.ComboIndexOf(r.Proc, r.W, r.H);
        }

        // 切到设置页时重算：下拉项里的 ✓/△/✕ 是"按本机主屏"来的，
        //   而它是在构建页面那一刻算的 —— 用户刚换过显示器/改过分辨率时不重算就一直显示旧判断。
        // 重建下拉项会清掉选中项，所以整段都要在 syncing 里做，最后再回显一次。
        void RefreshResCombos()
        {
            if (cbResCs == null || cbResCs.IsDisposed) return;
            ResLink.InvalidateModes();
            resComboSyncing = true;
            try
            {
                cbResCs.Items.Clear(); cbResCs.Items.AddRange(ResComboItems(ResKeyCs));
                if (cbResVal != null && !cbResVal.IsDisposed)
                { cbResVal.Items.Clear(); cbResVal.Items.AddRange(ResComboItems(ResKeyVal)); }
                // 显示器列表也要重读：用户可能在别处插拔了屏 / 刚在性能优化页停用过一台
                if (cbOffCs != null && !cbOffCs.IsDisposed)
                { cbOffCs.Items.Clear(); cbOffCs.Items.AddRange(OffMonItems(ResKeyCs)); }
                if (cbOffVal != null && !cbOffVal.IsDisposed)
                { cbOffVal.Items.Clear(); cbOffVal.Items.AddRange(OffMonItems(ResKeyVal)); }
            }
            catch { }
            finally { resComboSyncing = false; }
            ResSyncCombos();
            RefreshResInfo("");
        }

        // 下拉回显。构建设置页 / 换主题重建后都要调 —— 置 syncing 抑制事件，
        //   否则"回显"会被 OnResPresetPicked 当成"用户改了档位"，把配置平白写一遍。
        void ResSyncCombos()
        {
            resComboSyncing = true;
            try
            {
                if (cbResCs != null && !cbResCs.IsDisposed) cbResCs.SelectedIndex = ResComboIndex(ResKeyCs);
                if (cbResVal != null && !cbResVal.IsDisposed) cbResVal.SelectedIndex = ResComboIndex(ResKeyVal);
                if (cbOffCs != null && !cbOffCs.IsDisposed) cbOffCs.SelectedIndex = OffMonIndex(ResKeyCs);
                if (cbOffVal != null && !cbOffVal.IsDisposed) cbOffVal.SelectedIndex = OffMonIndex(ResKeyVal);
            }
            catch { }
            finally { resComboSyncing = false; }
        }

        string ResDescr(string key)
        {
            ResLinkRule r = ResRule(key);
            if (r == null) return "（未配置）";
            ResPreset p = ResLink.PresetOf(r);
            return r.W + "×" + r.H + (p != null ? "（" + p.Ratio + "）" : "（自定义）");
        }

        void RefreshResInfo(string extra)
        {
            if (lblResInfo == null || lblResInfo.IsDisposed) return;
            int mw, mh; ResLink.MainScreenMax(out mw, out mh);
            string cur = "当前：CS2 " + ResDescr(ResKeyCs) + "　无畏契约 " + ResDescr(ResKeyVal)
                       + "　还原目标 " + Cfg.BaseW + "×" + Cfg.BaseH + "@" + Cfg.BaseHz;
            string off = "游戏时禁用显示器：CS2 " + OffMonDescr(ResKeyCs) + "　无畏契约 " + OffMonDescr(ResKeyVal);
            string legend = "可用性按本机主屏（" + (mw > 0 ? "最大 " + mw + "×" + mh : "探测不到，一律按可用处理")
                          + "）：✓ 直接可切 · △ 本机没有这一档，需先在显卡驱动里建自定义分辨率 · ✕ 超出屏幕。"
                          + "\n改完立即生效（进游戏时应用）；刷新率自动跟随你当前主屏，换 240Hz 屏不用改这里。"
                          + "\n「游戏时禁用」走的是**设备管理器**那一层（右键「禁用设备」的等价操作）：进游戏时停用、退出游戏自动启用。"
                          + "它和「分离副屏」不是一回事 —— 分离只是把屏从桌面摘掉，设备管理器里仍显示正常运行。"
                          + "\n从本程序点「启动」时会在**游戏启动之前**先应用（这是无畏契约「真拉伸」的必要条件："
                          + "它在启动那一刻读到显示器原生宽高比就把画面锁回 16:9）；从 WeGame / 官方启动器自己进游戏时，"
                          + "只能等检测到游戏进程后再应用，那一次真拉伸要重启一次游戏才吃得到。"
                          + "\n⚠ 停用**主屏**会让桌面转移到另一台屏上；桌面上只剩一块屏时默认拒绝停用（否则可能黑屏）。"
                          + "单屏玩家如果就是想要真拉伸，勾上面「允许禁用唯一在用屏」即可 —— 那正是单屏拉伸要禁的那一台。"
                          + "任何时候都可以在「性能优化」页底部把显示器一键全部启用回来。";
            SetBody(lblResInfo, (extra != null && extra.Length > 0 ? extra + "\n" : "") + cur + "\n" + off + "\n" + legend);
        }

        void OnResPresetPicked(string key, RoundCombo cb)
        {
            if (resComboSyncing) return;
            ResLinkRule r = ResRule(key);
            if (r == null || cb == null) return;
            // 索引 → 档位由 ResLink 换算（与下拉标签同一个来源，不会错位）
            ResPreset p = ResLink.PresetAt(r.Proc, cb.SelectedIndex);
            if (p == null) return;              // "自定义·当前值"或越界 → 不改动
            if (p.W == r.W && p.H == r.H && r.Preset == p.Id) return;   // 本来就是这个档 → 无事

            string name = key == ResKeyVal ? "无畏契约" : "CS2";
            string before = r.W + "×" + r.H;
            r.W = p.W; r.H = p.H; r.Preset = p.Id;
            bool ok = Program.SaveResRules();
            int a = ResLink.Availability(p);
            string av = a == 0 ? "✓ 本机主屏可直接切换"
                      : (a == 1 ? "△ 本机主屏没有这一档 —— 需先在显卡驱动里建自定义分辨率，否则进游戏时切不过去"
                                : "✕ 超出本机主屏能力，进游戏时会切不动（会保留原模式并记一条日志）");
            string msg = "[" + name + "] 分辨率 " + before + " → " + p.W + "×" + p.H + "（" + p.Ratio + "）：" + av
                       + (p.Note != null && p.Note.Length > 0 ? "　· " + p.Note : "")
                       + (ok ? "　已写入配置" : "　⚠ 配置写回失败，设置只在本次运行有效");
            Log("[分辨率联动] " + msg);
            RefreshResInfo(msg);
        }

        // ==================== 进游戏时禁用的显示器（v3.9.0） ====================
        // 下拉项 = 「不禁用」+ 本机在场的每台显示器（**值 = 显示器型号**，见 MonMgr）。
        // 为什么用型号而不是 \\.\DISPLAYn 或实例 ID：输出号会随插拔顺序变、实例 ID 随接口变，
        //   只有硬件 ID 里的型号是跟着这台屏走的（本机实测 P27FBB-RG = XMIB008）。
        object[] OffMonItems(string key)
        {
            var list = new List<object>();
            list.Add("不禁用（所有显示器保持启用）");
            ResLinkRule r = ResRule(key);
            string cur = (r != null && r.OffMons != null) ? r.OffMons.Trim() : "";
            bool curSeen = cur.Length == 0;
            var seen = new List<string>();
            try
            {
                foreach (MonDev d in MonMgr.All())
                {
                    if (seen.Contains(d.Model)) continue;
                    seen.Add(d.Model);
                    list.Add(d.Panel + "（" + d.StatusText + (d.Output.Length > 0 ? " · " + d.Output.Replace("\\\\.\\", "") : "") + "）");
                    if (string.Equals(d.Model, cur, StringComparison.OrdinalIgnoreCase)) curSeen = true;
                }
            }
            catch { }
            // 配置里记的型号当前不在场（拔了 / 换口了）→ 补一项显示出来。
            // 不补的话回显会掉到"不禁用"，用户看着就像"我明明配过却没了"，再点一下还会把配置清掉。
            if (!curSeen) list.Add(cur + "（当前未连接）");
            return list.ToArray();
        }

        int OffMonIndex(string key)
        {
            ResLinkRule r = ResRule(key);
            if (r == null || r.OffMons == null || r.OffMons.Trim().Length == 0) return 0;
            string cur = r.OffMons.Trim();
            int i = 1;
            var seen = new List<string>();
            try
            {
                foreach (MonDev d in MonMgr.All())
                {
                    if (seen.Contains(d.Model)) continue;
                    seen.Add(d.Model);
                    if (string.Equals(d.Model, cur, StringComparison.OrdinalIgnoreCase)) return i;
                    i++;
                }
            }
            catch { }
            return i;      // 末尾那条"当前未连接"
        }

        string OffMonModelAt(string key, int index)
        {
            if (index <= 0) return "";
            int i = 1;
            var seen = new List<string>();
            try
            {
                foreach (MonDev d in MonMgr.All())
                {
                    if (seen.Contains(d.Model)) continue;
                    seen.Add(d.Model);
                    if (i == index) return d.Model;
                    i++;
                }
            }
            catch { }
            ResLinkRule r = ResRule(key);
            return (r != null && r.OffMons != null) ? r.OffMons.Trim() : "";
        }

        string OffMonDescr(string key)
        {
            ResLinkRule r = ResRule(key);
            if (r == null || r.OffMons == null || r.OffMons.Trim().Length == 0) return "无";
            string m = r.OffMons.Trim();
            List<MonDev> ds = MonMgr.ByModel(m);
            if (ds.Count == 0) return m + "（当前未连接）";
            return ds[0].Panel + (ds[0].Primary ? "（主屏）" : "");
        }

        void OnResOffMonPicked(string key, RoundCombo cb)
        {
            if (resComboSyncing) return;
            ResLinkRule r = ResRule(key);
            if (r == null || cb == null) return;
            string before = r.OffMons == null ? "" : r.OffMons.Trim();
            string want = OffMonModelAt(key, cb.SelectedIndex);
            if (want == before) return;
            r.OffMons = want;
            bool ok = Program.SaveResRules();
            string name = key == ResKeyVal ? "无畏契约" : "CS2";
            string msg;
            if (want.Length == 0) msg = "[" + name + "] 进游戏时不再禁用任何显示器";
            else
            {
                List<MonDev> ds = MonMgr.ByModel(want);
                bool primary = false;
                foreach (MonDev d in ds) if (d.Primary) primary = true;
                msg = "[" + name + "] 进游戏时禁用显示器 " + (ds.Count > 0 ? ds[0].Panel : want)
                    + "（型号 " + want + "，" + ds.Count + " 个设备实例）"
                    + (primary ? "。⚠ 它是当前主屏：停用后桌面会转移到另一台屏上" : "");
            }
            Log("[分辨率联动] " + msg + (ok ? "　已写入配置" : "　⚠ 配置写回失败"));
            RefreshResInfo(msg);
        }

        // 「关闭窗口时」三态 ↔ 两个配置字段。设置页下拉、关闭询问框、日志文案共用这一处解释，
        //  避免三个地方各写一遍映射（写岔了就会出现"选了却按另一个执行"）。
        static int CloseComboIndex(bool ask, bool tray)
        {
            if (ask) return 0;
            return tray ? 1 : 2;
        }

        static string CloseComboText(bool ask, bool tray)
        {
            if (ask) return "每次询问";
            return tray ? "直接最小化到托盘" : "直接退出程序";
        }

        // 弹框里勾了「记住我的选择」后，把设置页那个下拉同步过来
        void RefreshCloseCombo()
        {
            try
            {
                if (cbClose == null || cbClose.IsDisposed) return;
                int want = CloseComboIndex(Cfg.CloseAsk, Cfg.CloseToTray);
                if (cbClose.SelectedIndex != want) cbClose.SelectedIndex = want;
            }
            catch { }
        }

        // 运行期更新说明文字的唯一入口：文字没变就什么都不做（高频 tick 下零开销），
        // 变了就重排所属分区 —— 这样任何长度的文案都不会被裁掉。
        static void SetBody(Label l, string text)
        {
            if (l == null) return;
            if (l.Text == text) return;
            l.Text = text;
            var s = l.Tag as Sec;
            if (s != null) s.Resync();
        }

        // ---------------------- 优化执行 ----------------------
        void DoOptimize()
        {
            var ans = MessageBox.Show("将执行一键优化：电源 / GPU / 调度 / 网络 / 内存 / 服务 / 输入等。\n所有修改执行前自动备份，可随时用「恢复备份」还原。\n\n确定继续吗？",
                "一键优化", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;
            Log(">>> 开始一键优化…");
            btnOptimize.Enabled = false; btnOptimize.Text = "优化中…";
            var gw = new BackgroundWorker();
            gw.DoWork += delegate { Program.ApplyAll(Log); };
            gw.RunWorkerCompleted += delegate
            {
                btnOptimize.Enabled = true; btnOptimize.Text = "一键优化";
                RefreshGridAsync();
                Log(">>> 一键优化完成");
            };
            gw.RunWorkerAsync();
            Switch(P_OPT);
        }

        void DoRestore()
        {
            if (MessageBox.Show("确定要恢复最近一次优化前的全部设置吗？", "恢复备份",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log(">>> 开始恢复备份…");
            // ❗ 2026-09-20（1.0.2 用户反馈"该页面时不时卡死、整窗点不动只能关掉重开"）：
            //   这里原先直接在 UI 线程调 Program.DoRestore(Log) —— 而它要逐条还原整份备份：
            //   几十上百次注册表写入 + `powercfg /setactive` + `bcdedit` + Defender 排除 + 服务启用，
            //   每次 RunCmd 都要起子进程并 WaitForExit(5000)，**合计几秒到几十秒**。
            //   这段时间主线程被钉死：窗口不重绘（看起来"什么都不显示"）、点哪儿都没反应
            //   （用户只能关掉重开 —— 而且中途掐掉会让系统停在"还原到一半"的危险状态）。
            //   同页的「一键优化」「恢复默认」早就走了 BackgroundWorker，只有它漏了，现在补齐。
            if (btnRestore != null) { btnRestore.Enabled = false; btnRestore.Text = "恢复中…"; }
            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = Program.DoRestore(Log); };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (btnRestore != null) { btnRestore.Enabled = true; btnRestore.Text = "恢复备份"; }
                RefreshGridAsync();
                if (e.Error != null) { Log(">>> 恢复失败：" + e.Error.Message); return; }
                Log(">>> 恢复完成（还原 " + (e.Result == null ? 0 : (int)e.Result) + " 项），状态已刷新");
            };
            gw.RunWorkerAsync();
        }

        // ---------------------- 状态刷新 ----------------------
        int gridBusyFlag = 0;

        // 读一次「当前系统实际设置」并回填「优化项明细」。
        // ★ 2026-09-20（1.0.2 用户反馈"该页面会时不时卡死不显示任何东西也无法动"）：
        //   数据没到之前这一页**什么都不显示** —— 表格 0 行、健康度还是 "—"，而读取要 0.5~3s
        //   （慢机器 / 事件日志大 / 注册表项多时更久）。用户说的"卡死了、什么都没有"，
        //   一半来自这个空白期，一半来自换主题后没人回填（见 RebuildChrome）。
        //   现在：先放一行占位「正在读取系统设置…」，数据到了整表替换；读取偏慢（>1.5s）
        //   当场记一条带耗时的日志 —— 下次再"卡"，日志里就有现场，不用再猜。
        void RefreshGridAsync()
        {
            ShowGridPlaceholder("正在读取系统设置…");
            // 连点「刷新」不再叠好几个后台任务（以前每点一次排一个，越点越慢、越点越多）
            if (System.Threading.Interlocked.CompareExchange(ref gridBusyFlag, 1, 0) != 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = Program.GetStatusItems(); };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                gridBusyFlag = 0;
                long ms = sw.ElapsedMilliseconds;
                if (e.Error != null)
                {
                    Log("状态读取失败: " + e.Error.Message);
                    ShowGridPlaceholder("读取失败：" + e.Error.Message + "（点「刷新」重试）");
                    return;
                }
                if (e.Result != null) FillList((List<StatusItem>)e.Result);
                if (ms > 1500)
                    Log("[体检] 状态读取耗时 " + ms + " ms（这段时间明细表是空白的）。界面若整个卡住不动，"
                        + "请把这条连同发生时间发给作者。");
            };
            gw.RunWorkerAsync();
        }

        // 明细表的占位行：让"正在读"和"读失败"都看得见，不再是一片空白
        void ShowGridPlaceholder(string msg)
        {
            if (lvOpt == null || lvOpt.IsDisposed) return;
            if (lvOpt.InvokeRequired)
            {
                try { lvOpt.Invoke((Action)(delegate { ShowGridPlaceholder(msg); })); } catch { }
                return;
            }
            try
            {
                lvOpt.BeginUpdate();
                lvOpt.Items.Clear();
                var it = new ListViewItem(msg);
                it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                it.ForeColor = Theme.TextDim;
                lvOpt.Items.Add(it);
                lvOpt.EndUpdate();
            }
            catch { }
        }

        void FillList(List<StatusItem> items)
        {
            if (lvOpt == null) return;
            if (lvOpt.InvokeRequired) { lvOpt.Invoke((Action)(delegate { FillList(items); })); return; }
            int ok = 0, warn = 0, bad = 0, info = 0;
            lvOpt.BeginUpdate();
            try
            {
                lvOpt.Items.Clear();
                foreach (var it in items)
                {
                    string cur = it.Current == null ? "" : it.Current.Trim();
                    string exp = it.Expected == null ? "" : it.Expected.Trim();
                    // 状态分级统一走 Program.BadgeKind/BadgeText（四个档次：已生效 / 未生效 / 待确认 🟡 / 需处理）。
                    // 以前这里只有 ✅ 与 ⚠️ 两支，其余一律算"需处理"并涂红 —— 于是「🟡 已跳过」
                    // 「🟡 可实测」这类**本来就不是缺陷**的行也顶着红点报错（2026-09-19 用户截图）。
                    int kind = Program.BadgeKind(it.Status);
                    string st = Program.BadgeText(it.Status);
                    Color c = kind == Program.BadgeOk ? Theme.Ok
                            : kind == Program.BadgeWarn ? Theme.Warn
                            : kind == Program.BadgeInfo ? Theme.TextDim : Theme.Err;
                    if (kind == Program.BadgeOk) ok++;
                    else if (kind == Program.BadgeWarn) warn++;
                    else if (kind == Program.BadgeInfo) info++;
                    else bad++;
                    // ⚙ = 该项有可调档位（对应 Program.Knobs()）。用后缀而不是加一列：
                    // 列的可用宽度本来就紧，"能改"这件事只需要一个可识别的记号 + 双击。
                    var lvi = new ListViewItem(string.IsNullOrEmpty(it.Key) ? it.Item : (it.Item + "  ⚙"));
                    lvi.Tag = it.Key;
                    var subSt = lvi.SubItems.Add(st);
                    lvi.SubItems.Add(cur);
                    lvi.SubItems.Add(exp);
                    lvi.UseItemStyleForSubItems = false;
                    subSt.ForeColor = c;
                    subSt.Name = BadgeName;      // 告诉 DrawSubItem"这一格画胶囊"，见 IsBadgeCell
                    lvOpt.Items.Add(lvi);
                }
            }
            finally { lvOpt.EndUpdate(); }

            if (lblHealth != null)
            {
                SetBody(lblHealth, ok + " / " + items.Count);
                lblHealth.ForeColor = (warn + bad == 0) ? Theme.Ok : (warn > 0 ? Theme.Warn : Theme.Err);
            }
            if (lblHealthSub != null)
                SetBody(lblHealthSub, "已生效 " + ok + " 项 · 未生效 " + warn + " 项"
                    + (info > 0 ? " · 待确认 " + info + " 项" : "")
                    + (bad > 0 ? " · 需处理 " + bad + " 项" : "") +
                    (warn + bad > 0 ? "\n建议在「性能优化」页点一次一键优化"
                                    : (info > 0 ? "\n其余「待确认」不是缺陷（可选 / 不适用 / 读不到），可逐行双击查看"
                                                : "\n全部优化已生效")));
            if (lblDrift != null)
            {
                // 「待确认」不计进漂移：它们多数是"可选 / 不适用 / 读不到"，
                // 把它们算成"被系统重置"会让漂移提示长期挂在那里，久了就没人看了。
                if (warn + bad == 0)
                {
                    SetBody(lblDrift, info > 0
                        ? "✅ 没有未生效项；另有 " + info + " 项待确认（不一定是缺陷，见明细）。"
                        : "✅ 全部 " + items.Count + " 项优化均已生效，无漂移。");
                    lblDrift.ForeColor = info > 0 ? Theme.TextDim : Theme.Ok;
                }
                else { SetBody(lblDrift, "⚠ 检测到 " + (warn + bad) + " 项未生效（系统更新 / 升级常静默重置优化项），点「一键优化」恢复。"); lblDrift.ForeColor = Theme.Warn; }
            }

            if (!driftWarned && tray != null)
            {
                driftWarned = true;
                if (warn + bad >= 3)
                {
                    string msg = "检测到 " + (warn + bad) + " 项优化未生效（可能被系统更新重置），建议点一次「一键优化」恢复。";
                    Log("[漂移检测] " + msg);
                    tray.ShowBalloonTip(4000, "Fluxion", msg, ToolTipIcon.Warning);
                }
            }
        }

        int guardBusyFlag = 0;

        // ❗ 看门狗**绝不能**在 UI 线程上跑（2026-09-20 排查"性能优化页时不时卡死"时确认）：
        //   Program.GuardTick() 里每一拍都跑网络哨兵 NetWatchTick（2 组 × 4 次 ping，单次超时 1500ms
        //   = 最坏 12 秒），每 5 拍（≈20s）跑 WatchDisplayEvents —— 那是 **WMI 查系统事件日志**
        //   （本机实测 15~51ms，但首次调用要激活 WMI 服务、事件日志大的机器上会到秒级）。
        //   这些活全是网络 / WMI / 文件，**不碰任何控件** → 整体扔线程池，再加一道重入闸防堆积
        //   （慢的一拍没跑完就跳过这一拍，宁可少测一次也不叠）。
        //   用户侧表现：主线程被同步 I/O 占住 → WM_PAINT 饿死（刚切页/换主题时被 invalidate
        //   的区域一直没画 = 整片空白）+ 点不动 = "卡死不显示任何东西也无法动"。
        //   ⚠ 这种卡**日志里什么都没有**（不是异常，是同步阻塞），别再去翻崩溃日志找它。
        //   回归防线：tools/static_check.py 的 UI-BLOCK-GUARDTICK 规则盯着这一条。
        void GuardTickAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref guardBusyFlag, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { Program.GuardTick(); }
                catch (Exception ex) { Program.Log("TickGuard: " + ex.GetType().Name + " " + ex.Message); }
                finally { guardBusyFlag = 0; }
            });
        }

        void UpdateSys()
        {
            if (System.Threading.Interlocked.CompareExchange(ref sysBusyFlag, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int c = Program.GetCpu(), m = Program.GetMem(), g = Program.GetGpuPct(), t = Program.GetGpuTemp();
                    if (!closing && barCpu != null)
                    {
                        // BeginInvoke：UI 线程被同步 IO 钉住时池线程不必陪着等（2026-09-30 体检）
                        barCpu.BeginInvoke((Action)(delegate
                        {
                            barCpu.Value = c; barCpu.RightText = c + "%";
                            barMem.Value = m; barMem.RightText = m + "%";
                            barGpu.Value = g; barGpu.RightText = g + "%";
                            barTemp.Value = Math.Min(100, t); barTemp.RightText = t + "°C";
                            barTemp.BarColor = t >= 83 ? Theme.Err : (t >= 70 ? Theme.Warn : Theme.Ok);
                            spCpu.Push(c); spGpu.Push(g); spMem.Push(m);
                        }));
                    }
                    CheckAlarms(c, m, t);
                }
                catch { }
                finally { sysBusyFlag = 0; }
            });
        }

        void CheckAlarms(int cpu, int mem, int gpuTemp)
        {
            if (!Cfg.AlertEnable || Cfg.AlertCooldownMin <= 0) return;
            DateTime now = DateTime.Now;
            var msgs = new List<string>();
            if (Cfg.AlertGpuC > 0 && gpuTemp >= Cfg.AlertGpuC && (now - gpuWarnAt).TotalMinutes >= Cfg.AlertCooldownMin)
            { gpuWarnAt = now; msgs.Add("GPU 温度 " + gpuTemp + "°C（阈值 " + Cfg.AlertGpuC + "°C），过热降频会掉帧，检查散热/风扇"); }
            if (Cfg.AlertCpuPct > 0 && cpu >= Cfg.AlertCpuPct && (now - cpuWarnAt).TotalMinutes >= Cfg.AlertCooldownMin)
            { cpuWarnAt = now; msgs.Add("CPU 占用 " + cpu + "%（阈值 " + Cfg.AlertCpuPct + "%），后台进程抢核"); }
            if (Cfg.AlertMemPct > 0 && mem >= Cfg.AlertMemPct && (now - memWarnAt).TotalMinutes >= Cfg.AlertCooldownMin)
            { memWarnAt = now; msgs.Add("内存占用 " + mem + "%（阈值 " + Cfg.AlertMemPct + "%），系统将换页卡顿"); }
            if (msgs.Count == 0) return;
            string s = "⚠ " + string.Join("；", msgs.ToArray());
            Log(s);
            if (tray != null && !closing)
                try { Invoke((Action)(delegate { tray.ShowBalloonTip(3000, "Fluxion 硬件告警", s, ToolTipIcon.Warning); })); } catch { }
        }

        void UpdateLink()
        {
            if (lblLink == null) return;
            try
            {
                string game = Program.GetRunningGame();
                // 退出防抖（2026-09-27 日志实测：一局 CS2 里"游戏已退出→又检测到"抖了 4 次，
                //   分辨率/远控/电源跟着来回切 —— 即用户报的"切一小会就自动切回去 / 切屏也会切回去 /
                //   联动有时断开"）。根因：进程枚举偶发漏掉在跑的游戏（或全屏游戏把 UI 消息泵饿死、
                //   tick 迟到叠加），而下面一次 null 就立即收尾。对策：
                //   ① 刚从"在玩"变"没检测到"时，绕过 2 秒快照缓存全新枚举复核一次，瞬时误报当场纠正；
                //   ② 仍查不到也要连续 miss 满 3 个轮询周期（约 9 秒）才宣布退出 —— 真退出的进程不会
                //      复活，被漏看一次的进程 3 秒内就会回来，用这个不对称性区分两者。
                if (game == null && lastGame != null && lastGame.Length > 0)
                {
                    gameMiss++;
                    if (gameMiss == 1 && Program.ProcExistsFresh(lastGame))
                    {
                        Log("进程枚举瞬时抖动：复核仍检测到 " + Program.GameDisplayName(lastGame) + "，联动保持不收尾");
                        game = lastGame;
                        gameMiss = 0;
                    }
                    else if (gameMiss < 3)
                    {
                        game = lastGame;   // 未达判决线：本周期先当作还在玩（标签照常显示游戏中）
                    }
                    else
                    {
                        gameMiss = 0;      // 连续 3 个周期都查不到 → 真退出了，放行走正常收尾
                    }
                }
                else
                {
                    gameMiss = 0;
                }
                string cat = Program.GetRunningCategory();
                string remote = Program.GetRemoteState();
                bool inGame = game != null && game.Length > 0;
                string state = inGame
                    ? ("游戏中：" + Program.GameDisplayName(game) + (cat.Length > 0 ? "  [" + cat + "]" : "  [自动]"))
                    : (Program.SceneOffice ? "办公场景（联动停用）" : "未检测到游戏");
                if (lblGameState != null)
                {
                    SetBody(lblGameState, state);
                    // 状态行同时承担颜色语义：在玩＝强调绿 / 办公＝灰 / 未检测＝常规
                    lblGameState.ForeColor = inGame ? Theme.Accent
                                           : (Program.SceneOffice ? Theme.TextDim : Theme.Text);
                }
                SetBody(lblLink, "远控状态：" + remote + "    联动：" + (Cfg.AwareEnable ? "已开启" : "已关闭") +
                               "    场景：" + (Program.SceneOffice ? "办公" : "游戏") +
                               (inGame && gameStartAt != DateTime.MinValue ? "    时长：" + FmtDur(DateTime.Now - gameStartAt) : ""));
                if (tray != null && miRemote != null) miRemote.Text = "远控：" + remote;
                if (tray != null && miGame != null) miGame.Text = state;

                // 联动触发：以「游戏进程名」为准（从无到有、或换了一个游戏，都重新触发一轮）
                if (game != lastGame)
                {
                    if (game != null)
                    {
                        gameStartAt = DateTime.Now;
                        gameStartKey = KeyOfProcess(game);
                        Log("检测到游戏启动: " + Program.GameDisplayName(game) + "（档位 " + (cat.Length > 0 ? cat : "自动") + "）");
                        LinkGameEnter(cat, game);
                    }
                    else
                    {
                        gameStartAt = DateTime.MinValue;
                        Log("游戏已退出，联动收尾");
                        LinkGameExit();
                    }
                    lastGame = game;
                    lastGameState = inGame;
                }

                // 启动前预应用的看门狗（v3.9.1）：点了「启动」却一直没等到游戏进程（启动失败 / 用户中途放弃），
                // 超时就还显示设置 —— 否则桌面会一直停在游戏分辨率、显示器还禁着，用户会以为程序把机器搞坏了。
                // 120 秒的取法：WeGame / Riot 登录器的登录+校验一般在 1 分钟内，留一倍余量；
                // 再长就等于让桌面长时间停在游戏分辨率上，比"误还原"更烦人。
                if (resPreAt != DateTime.MinValue && !inGame
                    && (DateTime.Now - resPreAt).TotalSeconds > 120)
                {
                    resPreAt = DateTime.MinValue;
                    if (ResLink.Active)
                    {
                        Log("启动后 120 秒仍未检测到游戏进程，正在还原显示设置（分辨率与显示器都恢复原状）");
                        // 显示器设备启停是秒级同步 IO，不能卡 UI tick（同 LinkGameExit 的收尾）
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate
                        {
                            try { foreach (string l in ResLink.Restore()) Program.Log("[分辨率联动] " + l); }
                            catch (Exception ex) { Program.Log("[分辨率联动] 后台还原异常: " + ex.Message); }
                        });
                    }
                }
            }
            catch (Exception ex) { Program.Log("UpdateLink异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // 进程名 → 游戏库条目 → 稳定 Id（时长统计的键）
        string KeyOfProcess(string proc)
        {
            if (games == null || proc == null) return "";
            foreach (DlssgGame g in games)
            {
                if (g == null || g.Exe == null) continue;
                string exe = g.Exe;
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
                if (exe.Equals(proc, StringComparison.OrdinalIgnoreCase)) return Lib.IdOf(g);
            }
            return "";
        }

        // ---- 游戏联动执行层（进游戏暂停远控/切电源/加速包，退出全部还原） ----
        void LinkGameEnter(string cat, string game)
        {
            if (!Cfg.AwareEnable || Program.SceneOffice)
            {
                linkActive = false;
                if (lblLinkAct != null)
                    SetBody(lblLinkAct, Cfg.AwareEnable ? "办公场景：游戏联动整体停用" : "游戏联动已关闭（设置页可开启）");
                return;
            }
            linkActive = true;
            resPreAt = DateTime.MinValue;   // 预应用转正：显示状态交给常规的退出收尾流程
            // ★ 分辨率联动（v3.7.0）：进游戏按规则切主屏（瓦罗兰特附带禁用副屏）。
            //   必须放在远控暂停**之前**：PauseAllRemote 侧对 ResLink.Active 有让位逻辑（不记快照、不抢装回）。
            ResLinkRule rr = Cfg.ResLinkEnable ? ResLink.RuleFor(game) : null;
            // ★ 远控只对「竞技」档暂停。
            //   用户实测反馈：启动鸣潮（二游）也把 UU远程 杀了，而界面文案一直写"只对竞技网游生效"
            //   —— 因为这里原先只看总开关、从不看档位，而鸣潮在 gachaGames 里同样会被识别为"游戏"。
            bool fpsCat = string.Equals(cat, "fps", StringComparison.OrdinalIgnoreCase);
            bool doPause = !(Cfg.AwareOnlyFps && !fpsCat);
            var parts = new List<string>();
            if (rr != null) parts.Add("切换分辨率");
            if (!doPause) parts.Add("档位「" + (cat.Length > 0 ? cat : "自动") + "」→ 按设置不动远控");
            else parts.Add("暂停远控");
            if (Cfg.PowerGameSwitch) parts.Add(Cfg.PowerScheme == "ultimate" ? "临时切卓越性能" : "临时切高性能");
            if (Cfg.GbEnable) parts.Add("加速包");
            // 慢活整条链投进**同一个**后台线程按原顺序串行执行：显示器 PnP 设备启停带 400ms 强制等待、
            // GamePowerOn 要起 2~4 个 powercfg 子进程（每个最长等 5s），同步跑在 UI 线程会让进游戏
            // 冻结 1~3 秒（2026-09-30 体检）。顺序不能乱：分辨率要先于远控暂停完成（见上让位逻辑）。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (rr != null)
                    {
                        string resLog = ResLink.Apply(rr);
                        if (resLog.Length > 0) Program.Log("[分辨率联动] " + resLog);
                    }
                    if (doPause) Program.PauseAllRemoteAsync();
                    if (Cfg.PowerGameSwitch) Program.GamePowerOn();
                    if (Cfg.GbEnable) Program.GameBoostStartAsync();
                }
                catch (Exception ex) { Program.Log("[联动] 后台执行异常: " + ex.Message); }
            });
            Program.ApplyGameTimer();
            if (Program.IsTimerApplied()) parts.Add("定时器 " + Cfg.TimerMs + "ms");
            Program.ApplyGameAffinity();
            string done = "已联动：" + string.Join(" · ", parts.ToArray()) + "（退出游戏自动还原）";
            if (lblLinkAct != null) SetBody(lblLinkAct, done);
            Log("[联动] " + done);
            if (Cfg.Notify && tray != null && !closing)
                try { tray.ShowBalloonTip(2500, "Fluxion",
                    "进入 " + Program.GameDisplayName(game) + "：已按档位联动，退出自动还原。", ToolTipIcon.Info); } catch { }
        }

        void LinkGameExit()
        {
            if (!linkActive)
            {
                if (lblLinkAct != null) SetBody(lblLinkAct, "");
                return;
            }
            linkActive = false;
            // 累计本次时长（卡片悬浮提示里能看到）
            if (gameStartAt != DateTime.MinValue && gameStartKey.Length > 0)
            {
                long secs = (long)(DateTime.Now - gameStartAt).TotalSeconds;
                if (secs > 5) Program.PlayTimeAdd(gameStartKey, secs);
            }
            // ★ 分辨率联动收尾：先把主屏还原、副屏接回来，**再**放行远控恢复
            //   （远控被拉起时显卡会重新协商，ResLink 先把模式摆正，远控那边的显示守卫也就无快照可抢）。
            //   设备启停 + powercfg 同步跑要 1~3 秒，整条链按原顺序投进同一个后台线程（同 LinkGameEnter）。
            bool resRestored = ResLink.Active;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (ResLink.Active)
                        foreach (string l in ResLink.Restore()) Program.Log("[分辨率联动] " + l);
                    Program.ResumeAllRemoteIfPausedAsync();
                    Program.GamePowerOff();
                    Program.GameBoostStopAsync();
                }
                catch (Exception ex) { Program.Log("[联动] 后台收尾异常: " + ex.Message); }
            });
            Program.RevertGameTimer();
            string done = "联动收尾：远控已恢复 · 电源/定时器/挂起进程已还原"
                        + (resRestored ? " · 分辨率已还原 " + Cfg.BaseW + "x" + Cfg.BaseH + "@" + Cfg.BaseHz : "");
            if (lblLinkAct != null) SetBody(lblLinkAct, done);
            Log("[联动] " + done);
            if (Cfg.Notify && tray != null && !closing)
                try { tray.ShowBalloonTip(2500, "Fluxion", "游戏已退出：远控恢复，临时优化全部还原。", ToolTipIcon.Info); } catch { }
        }

        static string FmtDur(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return Math.Max(1, (int)t.TotalSeconds) + " 秒";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " 分";
            return (int)t.TotalHours + " 时 " + ((int)t.TotalMinutes % 60) + " 分";
        }

        void FollowLog()
        {
            try
            {
                if (log == null || log.IsDisposed) return;
                string f = Program.LogToday();
                if (!File.Exists(f)) return;
                long len = new FileInfo(f).Length;
                if (len == lastLogLen) return;
                if (len < lastLogLen) lastLogLen = 0;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(lastLogLen, SeekOrigin.Begin);
                    int tail = (int)(len - lastLogLen);
                    var buf = new byte[tail];
                    fs.Read(buf, 0, buf.Length);
                    lastLogLen = len;
                    foreach (var l in Encoding.UTF8.GetString(buf).Split('\n'))
                    {
                        string s = l.TrimEnd('\r');
                        if (s.Length == 0) continue;
                        if (!s.Contains("GUI 已启动") && !s.Contains("已启动（管理员") && !s.Contains("已请求管理员"))
                            AppendLog(s);
                    }
                }
            }
            catch { }
        }

        // ---------------------- 帧生成页逻辑 ----------------------
        void RefreshFgSummary()
        {
            try
            {
                bool src = Dlssg.SourceReady();
                string hags = Dlssg.HagsText();
                int hagsWarn = Dlssg.HagsState() == 2 ? 0 : 1;
                if (lblRt != null)
                {
                    // 固定 3 行（BodyFixed）：文案变化不再改变分区高度，避免整页重排
                    Dlssg030.PackActualInfo pa = Dlssg030.PackActual();
                    string srcHint = Dlssg.SourceHint();
                    SetBody(lblRt, "运行时文件：" + (src ? "已就绪"
                                  : (srcHint.Length > 0 ? "未就绪 —— " + srcHint : "未下载（点「下载运行时」）")) +
                                 "        HAGS：" + hags + (hagsWarn == 1 ? "  ⚠ 需开启" : "  ✓") +
                                 "\n入口优先级 version → winmm → dinput8 → winhttp → dxgi；反作弊游戏自动改用 d3d12.dll" +
                                 "\n" + pa.Line());
                    lblRt.ForeColor = (src && !pa.Warn) ? Theme.TextDim : Theme.Warn;
                    //  srcHint 非空 = 源位上有份不对的件，本来就该用告警色，不需要额外条件
                    // 不一致只在**变化时**记一次日志 —— 每次刷新都写会把日志刷满
                    string vk = pa.Verdict + "|" + pa.Code + "|" + pa.Drift;
                    if (vk != packVerdictSeen)
                    {
                        packVerdictSeen = vk;
                        if (pa.Warn) Program.Log("[资源包] " + pa.Line());
                    }
                }
                if (lblD3d12 != null)
                {
                    string dh = Dlssg.D3d12Hint();
                    SetBody(lblD3d12, "d3d12 入口（反作弊游戏专用）："
                        + (dh.Length == 0 ? "可用（代理内嵌 + 系统原件 + 载体就绪）" : dh));
                    lblD3d12.ForeColor = dh.Length == 0 ? Theme.TextDim : Theme.Warn;
                }
                if (lblPatrol != null)
                {
                    //  每次进页面 / 换选中游戏都按目录里的真实文件重算一遍（只读）。
                    //  刻意不写日志：巡检只负责"看得见"，要不要处理由用户点按钮决定，
                    //  每次刷页面都记一行会把日志刷满。
                    string pt = selected == null ? "入口巡检：未选中游戏（先在列表里点一个）"
                                                 : Plan.PatrolLine(selected.Dir);
                    SetBody(lblPatrol, pt);
                    lblPatrol.ForeColor = pt.IndexOf('\u26a0') >= 0 ? Theme.Warn : Theme.TextDim;
                }
                if (lblXe != null)
                {
                    if (selected == null)
                    {
                        SetBody(lblXe, "当前状态：未选中游戏（先在列表里选中绝区零 / 鸣潮）");
                        lblXe.ForeColor = Theme.TextDim;
                    }
                    else if (XeMfg.Detect(selected.Dir).Length == 0)
                    {
                        SetBody(lblXe, "当前：这款不是绝区零 / 鸣潮 —— 方案 A 只对这两个游戏成立"
                            + "（靠专用入口 + 显卡名伪装）。\n      其他游戏（含 3A 单机）用下面的 0.3.x 通用模式。");
                        lblXe.ForeColor = Theme.TextDim;
                    }
                    else
                    {
                        // 统一用 Plan.Current 判「现在到底哪套在跑」，两套区块共用同一个真相：
                        //  opti=方案A在用 / 030=0.3.0在用 / legacy=旧版代理 / none=全部停放（原生）
                        string cur = Plan.Current(selected.Dir);
                        string slot = Plan.EntryName(XeMfg.Detect(selected.Dir));
                        bool mine = cur == Plan.Opti;
                        SetBody(lblXe, "当前：" + Plan.PlanName(cur) + "（" + slot + " 槽位）"
                            + (mine ? "  ← 本方案在用" :
                               (cur == Plan.None ? "  ← 代理已停放，游戏跑原生"
                                                 : "  ← 切到本方案会把它改名停放")));
                        lblXe.ForeColor = mine ? Theme.Ok : Theme.TextDim;
                    }
                }
                if (lbl030 != null)
                {
                    if (selected == null)
                    {
                        SetBody(lbl030, "当前状态：未选中游戏");
                        lbl030.ForeColor = Theme.TextDim;
                    }
                    else if (XeMfg.Detect(selected.Dir).Length == 0)
                    {
                        // 通用（3A 单机）：入口名由上面的下拉决定
                        string en = Cfg.DlssgGenericEntry;
                        string pf = Path.Combine(XeMfg.TargetDir(selected.Dir, ""), en);
                        bool inPlace = File.Exists(pf) && Plan.OwnerOf(pf) == Plan.D030;
                        bool legacyIn = false;
                        foreach (string e2 in Plan.AllEntryNames)
                        {
                            string q = Path.Combine(selected.Dir, e2);
                            if (File.Exists(q) && Plan.OwnerOf(q) == Plan.Legacy) legacyIn = true;
                        }
                        SetBody(lbl030, "当前：通用模式（3A 单机）· 入口 " + en + "  "
                            + (inPlace ? "← 本方案在用" : (legacyIn ? "← 装着旧版代理（上限 4X），切过来即可升到 6X"
                                                                  : "← 未接入")));
                        lbl030.ForeColor = inPlace ? Theme.Ok : Theme.TextDim;
                    }
                    else
                    {
                        string cur3 = Plan.Current(selected.Dir);
                        string slot3 = Plan.EntryName(XeMfg.Detect(selected.Dir));
                        bool mine3 = cur3 == Plan.D030;
                        SetBody(lbl030, "当前：" + Plan.PlanName(cur3) + "（" + slot3 + " 槽位）"
                            + (mine3 ? "  ← 本方案在用" :
                               (cur3 == Plan.None ? "  ← 代理已停放，游戏跑原生"
                                                  : "  ← 切到本方案会把它改名停放")));
                        lbl030.ForeColor = mine3 ? Theme.Ok : Theme.TextDim;
                    }
                }
                RefreshRec();
                RefreshSpoof();
                if (lblFgRuntime != null)
                {
                    int inst = 0, ign = 0, xe = 0, g30 = 0;
                    foreach (var g in games)
                    {
                        if (g.Ignored) { ign++; continue; }
                        if (g.Installed) inst++;
                        if (XeMfg.ShortTag(g).Length > 0) xe++;
                        if (Dlssg030.ShortTag(g).Length > 0) g30++;
                    }
                    // 「可接入 N 个」原来其实是"列表里一共几款游戏"，字面意思被读成
                    // "已经能用了" —— 改成按实际方案分列，不再有歧义。
                    // 运行模式直显（v3.3.4）：绝区零 3.0+ 的帧生成只在 DX12 下出现，
                    // 这一行就是"到底有没有生效"的答案，不用再靠猜。
                    string mode = "";
                    if (selected != null)
                    {
                        string api = Lib.RenderApi(selected);
                        if (api.Length > 0)
                            mode = "    " + selected.Title + " 运行模式：" + api
                                 + (api == "DX12" ? " ✅" : " ⚠ 未开 DX12（右键卡片 →「DX12 模式启动」）");
                    }
                    SetBody(lblFgRuntime, "运行时：" + (src ? "已就绪" : "未下载") + "    HAGS：" + hags +
                                        "    方案 A 注入：" + xe + " 个" +
                                        "    0.3.x 通用：" + g30 + " 个" +
                                        (ign > 0 ? "    被忽略 " + ign + " 个（列表里灰显）" : "") + mode);
                    if (mode.IndexOf("⚠") >= 0) lblFgRuntime.ForeColor = Theme.Warn;
                    else if (mode.Length > 0) lblFgRuntime.ForeColor = Theme.Ok;
                }
                if (btnDownload != null) btnDownload.Text = src ? "重新下载" : "下载运行时";

                // 生效自检：读每个游戏目录里代理的 jsonl，取最近一条给出"实际跑到几倍"。
                //  有效（读到真实 generated_count）用强调色，其余用弱化色 —— 一眼分清
                //  "真的跑起来了" vs "代理在但没调用/日志是空的"。
                if (lblFgLast != null)
                {
                    bool eff;
                    string sum = FgVerify.Summary(games, out eff);
                    SetBody(lblFgLast, sum);
                    lblFgLast.ForeColor = eff ? Theme.Ok : Theme.TextDim;
                }
                SyncTarget();      // 目标游戏下拉跟随选中
            }
            catch { }
        }

        void DoDownload()
        {
            if (btnDownload != null) btnDownload.Enabled = false;
            Log(">>> 开始下载帧生成运行时…");
            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                var msgs = new List<string>();
                msgs.Add(Dlssg.Download("version.dll", Log));
                foreach (var alt in new string[] { "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll" })
                    msgs.Add(alt + " → " + Dlssg.Download(alt, null));
                e.Result = msgs;
            };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (btnDownload != null) btnDownload.Enabled = true;
                var msgs = e.Result as List<string>;
                if (msgs != null) foreach (var m in msgs) Log("  " + m);
                RefreshFgSummary();
                Log(">>> 运行时下载流程结束（失败项通常是网络 / 代理问题，可在「设置」页改代理）");
            };
            gw.RunWorkerAsync();
        }

        // 添加自定义扫描目录（F:\黄油 这类第三方下载、无注册表记录的游戏）
        void DoAddScanRoot()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择要纳入扫描的游戏目录（例如 F:\\黄油）；下次扫描会自动包含它";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                if (Program.Cfg.DlssgExtraRoots.Contains(dir)) { Log("  该目录已在扫描列表：" + dir); return; }
                Program.Cfg.DlssgExtraRoots.Add(dir);
                Program.SetConfigList("dlssg", "extraRoots", Program.Cfg.DlssgExtraRoots);
                Log("  已加入扫描目录：" + dir + "（重新扫描中…）");
                RunScan(true);
            }
        }

        // 主题切换：**立即生效**（原地重建控件树），不提示、不重启、不弹窗
        void DoSwitchTheme(bool dark)
        {
            string v = dark ? "dark" : "light";
            if (Cfg != null && Cfg.UiTheme == v) return;
            bool wrote = Program.SetConfigStr("ui", "theme", v);
            if (Cfg != null) Cfg.UiTheme = v;
            if (!wrote)
                Log("⚠ 主题没能写进配置文件（" + Program.DataDir + "\\config.json 不可写）"
                    + "—— 本次运行内生效，重启后会退回原主题。请到下面「数据目录」换成可写目录。");
            Theme.SetMode(v);
            RebuildChrome();
            Log("界面主题已切换为「" + (dark ? "深色" : "浅色") + "」（原地重建控件树，无需重启）");
        }

        // 动效档位切换：立即生效 + 写配置（与主题切换同一套写法），并当场演示一次选中淡入，
        // 否则用户改成「始终开启」之后要等下一次切页才知道有没有生效。
        void DoSwitchMotion(int idx)
        {
            string v = idx == 1 ? "on" : (idx == 2 ? "off" : "auto");
            if (Cfg != null && Cfg.UiMotion == v)
            {
                for (int i = 0; i < navs.Length; i++) { NavBtn nb = navs[i] as NavBtn; if (nb != null) nb.ReplayActive(); }
                return;
            }
            bool wrote = Program.SetConfigStr("ui", "motion", v);
            if (Cfg != null) Cfg.UiMotion = v;
            Motion.Mode = v;
            Motion.RefreshOsFlag();
            if (!wrote)
                Log("⚠ 动效档位没能写进配置文件（" + Program.DataDir + "\\config.json 不可写）—— 本次运行内生效，重启后会退回原设置。");
            Log("界面动效已切换为「" + (idx == 1 ? "始终开启" : (idx == 2 ? "关闭" : "跟随系统")) + "」"
                + (idx == 0 ? "（系统当前" + (Motion.OsAllows ? "允许" : "禁止") + "动画）" : ""));
            for (int i = 0; i < navs.Length; i++) { NavBtn nb = navs[i] as NavBtn; if (nb != null) nb.ReplayActive(); }
        }

        // 配置导出：把"换机/重装后需要恢复的东西"整包拷到一个文件夹
        void DoExportConfig()
        {
            var dlg = new FolderBrowserDialog();
            dlg.Description = "选择导出到哪个文件夹（会在里面新建 GameBoost-配置备份）";
            dlg.ShowNewFolderButton = true;
            using (dlg)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dst = Path.Combine(dlg.SelectedPath, "GameBoost-配置备份");
                string[] files = new string[] { "config.json", "config.local.json", "library.json",
                    "games.json", "gameProfiles.override.json", "playtime.json", "ui.scale" };
                int n = 0;
                try
                {
                    Directory.CreateDirectory(dst);
                    foreach (string f in files)
                    {
                        string s = Path.Combine(Program.DataDir, f);
                        if (File.Exists(s)) { File.Copy(s, Path.Combine(dst, f), true); n++; }
                    }
                    string cov = Path.Combine(Program.DataDir, "covers");
                    if (Directory.Exists(cov)) { Program.CopyDirTree(cov, Path.Combine(dst, "covers")); n++; }
                }
                catch (Exception ex) { Log("⚠ 导出失败：" + ex.Message); return; }
                Log("✓ 配置已导出到 " + dst + "（含 " + n + " 项：设置 / 游戏库 / 标记 / 时长 / 封面）");
                try { Process.Start("explorer.exe", "\"" + dst + "\""); } catch { }
            }
        }

        // 配置导入：校验后再落盘（坏文件不许进）
        void DoImportConfig()
        {
            var dlg = new FolderBrowserDialog();
            dlg.Description = "选择之前导出的 GameBoost-配置备份 文件夹";
            using (dlg)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string src = dlg.SelectedPath;
                string cfg = Path.Combine(src, "config.json");
                if (!File.Exists(cfg)) { Log("⚠ 该文件夹里没有 config.json —— 请选 GameBoost-配置备份 那个目录"); return; }
                try
                {
                    new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(cfg, Encoding.UTF8));
                }
                catch (Exception ex) { Log("⚠ 导入中止：config.json 不是合法 JSON（" + ex.Message + "）"); return; }

                string[] files = new string[] { "config.json", "library.json", "games.json",
                    "gameProfiles.override.json", "playtime.json", "ui.scale" };
                int n = 0;
                try
                {
                    foreach (string f in files)
                    {
                        string s = Path.Combine(src, f);
                        if (File.Exists(s)) { File.Copy(s, Path.Combine(Program.DataDir, f), true); n++; }
                    }
                    string cov = Path.Combine(src, "covers");
                    if (Directory.Exists(cov)) { Program.CopyDirTree(cov, Path.Combine(Program.DataDir, "covers")); n++; }
                }
                catch (Exception ex) { Log("⚠ 导入部分失败：" + ex.Message); return; }
                Log("✓ 配置已导入（" + n + " 项）。请重启程序让设置与游戏库生效。");
            }
        }

        // 数据目录迁移：选目录 → 二次确认（明说"搬不是拷"）→ 搬移 → 提示重启
        void DoChangeDataDir()
        {
            var dlg = new FolderBrowserDialog();
            dlg.Description = "选择新的数据目录（配置 / 封面 / 日志 / 备份 / 帧生成资源包 会整体搬过去，原目录清空）";
            dlg.ShowNewFolderButton = true;
            try { string par = Path.GetDirectoryName(Program.DataDir); if (par != null) dlg.SelectedPath = par; }
            catch { }
            using (dlg)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string tgt = dlg.SelectedPath;
                if (string.IsNullOrEmpty(tgt)) return;
                if (MessageBox.Show(this,
                        "把数据目录从\n  " + Program.DataDir + "\n迁移到\n  " + tgt + "\n\n"
                      + "· 是搬移，不是复制：复制 → 校验大小 → 删除源文件，原目录会清空\n"
                      + "· 配置 / 游戏库 / 封面 / 日志 / 备份 / 帧生成资源包 一并搬走\n"
                      + "· 搬完需要重启程序",
                        "迁移数据目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

                Log(">>> 开始迁移数据目录 → " + tgt);
                string err = Program.MigrateDataTo(tgt);
                if (err.Length > 0)
                {
                    Log("⚠ 迁移中止：" + err);
                    MessageBox.Show(this, "迁移失败：\n" + err, "数据目录", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Log("✓ 数据目录已迁移到 " + tgt + "（原目录已清空）");
                if (MessageBox.Show(this, "数据目录已迁移到\n" + tgt + "\n\n现在重启程序让它生效吗？",
                        "数据目录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
                    Close();
                }
            }
        }

        // 扫描游戏目录。manual=true 走「扫描游戏」按钮（打进度日志、按钮置灰）；
        // manual=false 为后台静默自动扫描（启动时 + 窗口重新激活时，见 MaybeAutoScan）
        // —— 新装的游戏不必手点按钮就能出现（2026-09-13 用户反馈"识别不能实时更新"）。
        void RunScan(bool manual)
        {
            if (scanRunning) return;
            scanRunning = true;
            if (manual && btnScanGames != null) { btnScanGames.Enabled = false; btnScanGames.Text = "扫描中…"; }
            if (manual) Log(">>> 开始扫描游戏目录（Steam 库 + 常见路径 + 注册表）…");
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                // 先清掉失效的安装记录（文件已不在 / 已被别的东西替换）：
                // 不清的话卡片会把"别人的文件"当成本工具装的，还会让「关闭选中」删错东西
                int pruned = Dlssg.PruneStale();
                if (pruned > 0) Log("  已清理 " + pruned + " 条失效的安装记录");
                List<DlssgGame> scanned = Lib.ScanAll(manual ? (Action<string>)(delegate(string m) { Log("  " + m); }) : null);
                // 合并（含每条目 Inspect 重探，整条扫描链里最重的一步）也留在后台线程做完 ——
                // 此前它在 RunWorkerCompleted（UI 线程）同步跑，扫描完成后界面还要再冻一截
                //（2026-09-30 体检：MergeScan 全库重探压 UI 线程）。被合并的是新扫出的条目对象，
                // 此刻 games 还没被触碰，后台重探与界面读取互不相干。
                List<DlssgGame> igHits;
                e.Result = new object[] { Dlssg.MergeScan(scanned, out igHits), igHits };
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                scanRunning = false;
                lastScanAt = DateTime.Now;
                if (btnScanGames != null) { btnScanGames.Enabled = true; btnScanGames.Text = "扫描游戏"; }
                if (e.Error != null) { if (manual) Log("扫描失败: " + e.Error.Message); return; }
                var old = new List<string>();
                foreach (var o in games) old.Add(o.Dir.ToLowerInvariant());
                // 合并层会滤掉忽略清单 + 追加 games.json 里的自定义游戏（合并本身已在后台 DoWork 完成）
                object[] mr = e.Result as object[];
                List<DlssgGame> igHits = (mr != null && mr.Length > 1) ? mr[1] as List<DlssgGame> : null;
                games = (mr != null && mr.Length > 0) ? mr[0] as List<DlssgGame> : null;
                if (games == null) games = new List<DlssgGame>();
                if (igHits == null) igHits = new List<DlssgGame>();
                FillGameList();
                RebuildTarget();      // 目标游戏下拉跟随新列表
                int fg = 0, xe = 0, act = 0;
                string news = "";
                foreach (var g in games)
                {
                    if (g.Ignored) continue;
                    act++;
                    if (g.HasFrameGen) fg++;
                    if (XeMfg.ShortTag(g).Length > 0) xe++;
                    if (!old.Contains(g.Dir.ToLowerInvariant())) news += (news.Length > 0 ? "、" : "") + g.Title;
                }
                Log(">>> " + (manual ? "扫描完成" : "自动扫描完成") + "：游戏目录 " + games.Count
                    + " 个（有效 " + act + " 个）；已接入方案 A 注入 " + xe + " 个；含 DLSS 帧生成组件 " + fg + " 个"
                    + (news.Length > 0 ? "；新增 " + news : ""));
                // 被忽略的条目也照样进列表（灰显），这里同时把原因说出来 ——
                // 此前静默丢弃正是"装了却识别不出来"的来源（2026-09-13 原神即此）
                if (igHits.Count > 0)
                {
                    string ign = "";
                    foreach (var g in igHits) ign += (ign.Length > 0 ? "、" : "") + g.Title + "（" + g.Dir + "）";
                    Log(">>> 注意：有 " + igHits.Count + " 个目录在忽略清单里（列表中已灰显）：" + ign
                        + "  → 选中它点「开启选中」即可恢复收录");
                }
                RefreshFgSummary();
            };
            bw.RunWorkerAsync();
        }

        // 窗口重新激活时按需重扫：间隔未到不重复扫；游戏运行中不抢磁盘 IO
        void MaybeAutoScan()
        {
            if (scanRunning || closing) return;
            if (lastGameState || linkActive) return;
            if (DateTime.Now - lastScanAt < TimeSpan.FromMinutes(10)) return;
            RunScan(false);
        }

        // 列表填充：筛选 + 排序 + 生成卡片 + 按行数定高（整页单滚动条，无内层滚动）
        Timer searchDebounce;
        // 搜索框逐键触发 FillGameList：整页卡片重建（含封面解码与位图复制），连打会闪且 GC 压力大。
        // 300ms 防抖：停手才真正重建（2026-09-30 体检）。
        void SearchDebounce()
        {
            if (searchDebounce == null)
            {
                searchDebounce = new Timer();
                searchDebounce.Interval = 300;
                searchDebounce.Tick += delegate
                {
                    searchDebounce.Stop();
                    FillGameList();
                };
            }
            searchDebounce.Stop();
            searchDebounce.Start();
        }

        void FillGameList()
        {
            if (flGames == null) return;
            if (flGames.InvokeRequired) { flGames.Invoke((Action)(delegate { FillGameList(); })); return; }
            flGames.SuspendLayout();
            // 重建前记住页面滚动位置：不记的话每次自动扫描/后台刷新都会把用户弹回顶部
            int keepScroll = 0;
            if (pages != null && P_LIB < pages.Length && pages[P_LIB] != null)
            { keepScroll = pages[P_LIB].ScrollY; }
            try
            {
                // 先快照再 Dispose：Control.Dispose() 会把自己从父集合移除，边遍历边改集合
                // 会静默跳过约一半控件（2026-09-30 体检实测，不抛异常所以从未暴露）——
                // 漏掉的卡片连树带右键菜单都不释放
                Control[] olds = new Control[flGames.Controls.Count];
                flGames.Controls.CopyTo(olds, 0);
                for (int i = 0; i < olds.Length; i++) olds[i].Dispose();
                flGames.Controls.Clear();

                // 读 Query 而不是 Text：占位文案不能被当成搜索词
            string q = (rfSearch == null) ? "" : rfSearch.Query.ToLowerInvariant();
                string fid = PlatId();
                bool showHidden = chkShowHidden != null && chkShowHidden.Checked;
                var shown = new List<DlssgGame>();
                foreach (DlssgGame g in games)
                {
                    if (g.Hidden && !showHidden) continue;
                    if (g.Private && fid != "private") continue;              // 私密不进「全部」
                    if (fid == "fav" && !g.Favorite) continue;
                    if (fid == "private" && !g.Private) continue;
                    if (fid == "genre" && (g.Genre == null || g.Genre.Length == 0)) continue;
                    if ((fid == "steam" || fid == "epic" || fid == "wegame" || fid == "local") && g.Platform != fid) continue;
                    if (q.Length > 0 && !FuzzyHit(g, q)) continue;
                    shown.Add(g);
                }
                SortGames(shown);

                foreach (DlssgGame g in shown)
                {
                    var card = new GameCard(g, libCardW > 0 ? libCardW : Theme.S(148),
                                             libCardH > 0 ? libCardH : Theme.S(222));
                    card.Chosen += delegate(object s, EventArgs e) { SelectCard((GameCard)s); };
                    card.Launched += delegate(object s, EventArgs e)
                    {
                        var gc = (GameCard)s;
                        if (gc.Game != null) { SelectCard(gc); DoLaunchGame(); }
                    };
                    card.MenuAction += delegate(GameCard gc, string act) { OnCardMenu(gc, act); };
                    flGames.Controls.Add(card);
                }
                flGames.PerformLayout();     // 让网格按新行数定高（CardGrid 自己算）

                // ★ 扫描/刷新会重建整个列表（对象全部换新），而 selected 还指着**上一批的对象**：
                //   结果是——高亮丢失（下面按引用比较，永远不相等），而且「方案推荐」一直在渲染
                //   旧对象的字段（新扫描出来的能力/接入状态看不到）。按目录重新定位一次即可。
                if (selected != null)
                {
                    DlssgGame fresh = null;
                    foreach (DlssgGame g in games)
                        if (string.Equals(g.Dir, selected.Dir, StringComparison.OrdinalIgnoreCase)) { fresh = g; break; }
                    if (fresh != null) selected = fresh;
                }
                if (selected != null)
                    foreach (Control c in flGames.Controls)
                    {
                        var gc = c as GameCard;
                        if (gc != null) gc.SetPicked(gc.Game == selected);
                    }
                // 记下这次的实际顺序，供下一轮"集合未变则保持顺序"使用
                lastOrder.Clear();
                for (int i = 0; i < shown.Count; i++) lastOrder[OrderKey(shown[i])] = i;
                lastOrderValid = true;

                if (lblLibStat != null)
                    SetBody(lblLibStat, "共 " + games.Count + " 款，显示 " + shown.Count + " 款"
                        + (selected != null ? "    已选：" + selected.Title : "    （双击卡片启动）"));
            }
            finally
            {
                flGames.ResumeLayout(true);
                if (pages != null && P_LIB < pages.Length && pages[P_LIB] != null)
                { pages[P_LIB].SetScrollY(keepScroll); }
            }
        }

        string PlatId()
        {
            if (cbPlat == null || cbPlat.SelectedIndex < 0 || cbPlat.SelectedIndex >= PlatIds.Length) return "all";
            return PlatIds[cbPlat.SelectedIndex];
        }

        // 上一轮渲染出来的顺序（"游戏 Id|标题" → 位置）。
        //   ★ 2026-09-15 用户反馈"来回切全屏游戏排列会变"：默认的「最近游玩」排序会把刚玩过的
        //     游戏顶到第一位，而窗口 Activated 会触发自动扫描 → 重建卡片，于是 alt-tab 回来整个
        //     排列就变了。集合没变时沿用上次顺序，只有真的增删游戏、或用户主动换排序才重排。
        readonly Dictionary<string, int> lastOrder = new Dictionary<string, int>();
        bool lastOrderValid;

        static string OrderKey(DlssgGame g) { return Lib.IdOf(g) + "|" + (g.Title == null ? "" : g.Title); }

        // ---- 搜索：标题 / 目录 / exe / 平台 + 子序列模糊 + 拼音首字母 ----
        //  子序列：输入 "cby" 命中 "CybErpunk"（不要求连续）、"wwqy" 命中"无畏契约"。
        static bool SubSeq(string s, string q)
        {
            int j = 0;
            for (int i = 0; i < s.Length && j < q.Length; i++) if (s[i] == q[j]) j++;
            return j == q.Length;
        }

        // 常见游戏名用字的拼音首字母。只收"读音确定"的字 —— 宁可少收，不能收错
        // （收错会让搜索给出莫名其妙的命中，比搜不到更烦人）。要扩展就往 pairs 里加"字+首字母"。
        static readonly Dictionary<char, char> PY = BuildPy();
        static Dictionary<char, char> BuildPy()
        {
            var m = new Dictionary<char, char>();
            string pairs =
                "原y神s绝j区q零l鸣m潮c无w畏w契q约y卡k拉l彼b丘q三s角j洲z行x动d" +
                "崩b坏h星x穹q铁t道d极j限x竞j速s地d平p线x赛s博b朋p克k艾a尔e登d法f环h" +
                "尸s姬j之z梦m忠z诚c一y课k我w的d需x要y帮b助z友y新x生s也y疯f狂k猎l艳y" +
                "逐z影y低d语y宾b沉c寂j舞w手s艺y患h者z欲y望w国g斗d罗l魂h暗a黑h话h传z" +
                "说s奇q幻h荣r耀y前q夜y曙s光g钢g苍c战z争z觉j醒x命m运y交j错c女n异y闻w" +
                "录l学x院y杀s教j父f系x列l";
            for (int i = 0; i + 1 < pairs.Length; i += 2) m[pairs[i]] = pairs[i + 1];
            return m;
        }

        static string PyInitials(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
            {
                char v;
                if (PY.TryGetValue(ch, out v)) sb.Append(v);
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            }
            return sb.ToString();
        }

        static bool FuzzyHit(DlssgGame g, string q)
        {
            string title = (g.Title == null ? "" : g.Title).ToLowerInvariant();
            if (title.IndexOf(q) >= 0) return true;
            if (g.Dir != null && g.Dir.ToLowerInvariant().IndexOf(q) >= 0) return true;
            if (g.Exe != null && g.Exe.ToLowerInvariant().IndexOf(q) >= 0) return true;
            if (g.Platform != null && g.Platform.ToLowerInvariant().IndexOf(q) >= 0) return true;
            string py = PyInitials(g.Title);
            if (py.Length > 0 && py.IndexOf(q) >= 0) return true;
            return SubSeq(title, q) || (py.Length > 0 && SubSeq(py, q));
        }

        void SortGames(List<DlssgGame> list)
        {
            int mode = (cbSort == null || cbSort.SelectedIndex < 0) ? 0 : cbSort.SelectedIndex;
            if (mode == 0 && lastOrderValid)
            {
                // 集合没变（所有条目都在上次的序列里）→ 保持上次顺序
                bool allKnown = list.Count > 0;
                foreach (DlssgGame g in list) if (!lastOrder.ContainsKey(OrderKey(g))) { allKnown = false; break; }
                if (allKnown)
                {
                    list.Sort(delegate(DlssgGame a, DlssgGame b)
                    {
                        int ia, ib;
                        bool ha = lastOrder.TryGetValue(OrderKey(a), out ia);
                        bool hb = lastOrder.TryGetValue(OrderKey(b), out ib);
                        if (ha && hb) return ia.CompareTo(ib);
                        if (ha) return -1;
                        if (hb) return 1;
                        return string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
                    });
                    return;
                }
            }
            if (mode == 1)
                list.Sort(delegate(DlssgGame a, DlssgGame b) { return string.Compare(a.Title, b.Title, StringComparison.CurrentCulture); });
            else if (mode == 2)
                list.Sort(delegate(DlssgGame a, DlssgGame b)
                {
                    int c = string.Compare(a.Platform, b.Platform, StringComparison.Ordinal);
                    return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
                });
            else
                list.Sort(delegate(DlssgGame a, DlssgGame b)
                {
                    int c = b.LastPlayed.CompareTo(a.LastPlayed);      // 最近玩过的排前面
                    return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
                });
        }

        void SelectCard(GameCard k)
        {
            foreach (Control c in flGames.Controls)
            {
                var gc = c as GameCard;
                if (gc != null) gc.SetPicked(gc == k);
            }
            if (k == null || k.Game == null) return;
            selected = k.Game;
            if (lblSel != null)
                SetBody(lblSel, "已选：" + selected.Title + "    [" + Lib.PlatformLabel(selected.Platform) + "]    " + selected.Dir
                    + (Dlssg.EntryHint(selected).Length > 0 ? "    ⓘ " + Dlssg.EntryHint(selected) : ""));
            SyncTarget();
            RefreshFgSummary();
        }

        // 目标游戏下拉与卡片选中双向同步
        static string TargetLabel(DlssgGame g)
        {
            return (g.Installed ? "● " : "") + g.Title + "   [" + Lib.PlatformLabel(g.Platform) + "]";
        }

        void RebuildTarget()
        {
            if (cbTarget == null) return;
            syncTarget = true;
            try
            {
                cbTarget.Items.Clear();
                foreach (DlssgGame g in games) cbTarget.Items.Add(TargetLabel(g));
                int i = games.IndexOf(selected);
                cbTarget.SelectedIndex = (i >= 0 && i < cbTarget.Items.Count) ? i : -1;
            }
            finally { syncTarget = false; }
            SyncTarget();
        }

        void SyncTarget()
        {
            if (cbTarget == null) return;
            syncTarget = true;
            try
            {
                if (cbTarget.Items.Count != games.Count) RebuildTarget();
                else
                {
                    int i = games.IndexOf(selected);
                    cbTarget.SelectedIndex = (i >= 0 && i < cbTarget.Items.Count) ? i : -1;
                }
            }
            catch { }
            finally { syncTarget = false; }
            if (lblTarget != null)
                SetBody(lblTarget, selected == null
                    ? "未选中 —— 到「游戏库」页点一张卡片选中游戏"
                    : Lib.PlatformLabel(selected.Platform) + " · " + selected.Title
                      + Plan.StatusOf(selected.Dir)
                      + (selected.HasFrameGen ? "　有 DLSS 帧生成组件" : "　无 DLSS 帧生成组件")
                      + (selected.HasFsr3 ? "　自带 FSR3 帧生成" : "")
                      + (selected.HasUpscaler ? "　自带 DLSS 超分" : ""));
        }

        void OnTargetChanged()
        {
            if (syncTarget || cbTarget == null) return;
            int i = cbTarget.SelectedIndex;
            if (i < 0 || i >= games.Count) return;
            selected = games[i];
            foreach (Control c in flGames.Controls)
            {
                var gc = c as GameCard;
                if (gc != null) gc.SetPicked(gc.Game == selected);
            }
            if (lblSel != null)
                SetBody(lblSel, "已选：" + selected.Title + "    [" + Lib.PlatformLabel(selected.Platform) + "]    " + selected.Dir);
            SyncTarget();
            RefreshFgSummary();
        }

        // ---- 库标记操作 ----
        // 卡片右键菜单的动作分发（动作名见 GameCard.BuildMenu）
        void OnCardMenu(GameCard gc, string act)
        {
            if (gc == null || gc.Game == null) return;
            if (act == "launch") { DoLaunchGame(); return; }
            if (act == "dx12") { DoToggleDx12(); return; }
            if (act == "dxapi") { DoCheckRenderApi(); return; }
            if (act == "opendir") { DoOpenGameDir(); return; }
            if (act == "fav") { DoToggleFavorite(); return; }
            if (act == "rename") { DoRenameGame(); return; }
            if (act == "priv") { DoTogglePrivate(); return; }
            if (act == "hid") { DoToggleHidden(); return; }
            if (act == "setcover") { DoSetCover(); return; }
            if (act == "dlcover") { DoRedownloadCover(); return; }
            if (act == "webcover") { DoWebCover(); return; }
            if (act == "coverdir") { DoOpenCoversDir(); return; }
            if (act == "remove") { DoRemoveGame(); return; }
            if (act == "fg")
            {
                Switch(P_FG);
                Log("  已切到「帧生成」页，目标游戏已选中：" + selected.Title + " —— 点「开启选中」即可");
            }
        }

        // 手动指定封面：本地任意图片，拷成 <游戏名>.png（优先于官方封面）
        void DoSetCover()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "为「" + selected.Title + "」选择封面图片（建议竖版 2:3）";
                dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string err = CoverArt.SetManualCover(selected, dlg.FileName);
                if (err.Length == 0)
                {
                    Log("  已设置封面：" + selected.Title + " ← " + Path.GetFileName(dlg.FileName));
                    FillGameList();
                }
                else Log("  设置封面失败：" + err);
            }
        }

        // 联网按名字搜一张竖版图当封面（Bing 图片搜索；会访问任意 https 图片站）
        void DoWebCover()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (webCoverRunning) { Log("  联网搜图正在进行中…"); return; }
            if (!Cfg.DlssgOnlineCovers) { Log("  已关闭「联网获取游戏封面」，请先在设置页打开"); return; }
            webCoverRunning = true;
            DlssgGame g = selected;
            Log(">>> 联网搜寻封面：" + g.Title + "（只取竖版大图）");
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                string err = "未找到合适的封面";
                try { err = CoverArt.SearchWebCover(g); }
                catch (Exception ex) { err = ex.Message; }
                e.Result = err;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                webCoverRunning = false;
                string err = e.Result as string;
                if (err == null || err.Length == 0)
                {
                    Log("  已用联网搜到的图片作为封面：" + g.Title);
                    FillGameList();
                }
                else
                {
                    Log("  联网搜图未成功：" + err + " —— 可用右键「设置封面…」手动指定本地图片");
                }
            };
            bw.RunWorkerAsync();
        }

        // 重新下载官方封面（先删掉现有官方封面，绕开"已有就不重下"的判据）
        //  整条下载链放后台：Steam 全部尺寸 ×2 尝试 + 联网搜图几十个请求且未设超时，
        //  此前在 UI 线程同步跑，断网时要卡数分钟、期间游戏轮询联动停摆（2026-09-30 体检）。
        void DoRedownloadCover()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (redownloadRunning) { Log("  封面重取正在进行中…"); return; }
            if (selected.Id == null || selected.Id.Length == 0) selected.Id = Lib.IdOf(selected);
            try { if (File.Exists(CoverArt.CardFile(selected.Id))) File.Delete(CoverArt.CardFile(selected.Id)); } catch { }
            try { if (File.Exists(CoverArt.CardFilePng(selected.Id))) File.Delete(CoverArt.CardFilePng(selected.Id)); } catch { }
            DlssgGame g = selected;
            redownloadRunning = true;
            Log(">>> 重新获取官方封面：" + g.Title);
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                bool ok = false;
                try
                {
                    string appid = g.AppId == null ? "" : g.AppId;
                    ok = appid.Length > 0 && CoverArt.DownloadSteam(appid, g.Id);
                    if (!ok)
                    {
                        string alt = CoverArt.SearchAppId(g.Title);
                        if (alt.Length > 0 && alt != appid) { ok = CoverArt.DownloadSteam(alt, g.Id); if (ok) Lib.RememberAppId(g, alt); }
                    }
                    if (!ok) ok = CoverArt.SearchWebCover(g).Length == 0;      // 再退一步：联网搜图
                }
                catch (Exception ex) { Log("  封面重取异常：" + ex.Message); }
                e.Result = ok;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                redownloadRunning = false;
                CoverArt.ClearCache();
                bool ok = e.Result is bool && (bool)e.Result;
                Log(ok ? "  已获取封面：" + g.Title
                       : "  没找到合适封面（Steam 全部尺寸 + 联网搜图都试过）—— 可用右键「设置封面…」指定本地图片");
                FillGameList();
            };
            bw.RunWorkerAsync();
        }

        void DoOpenCoversDir()
        {
            try
            {
                Directory.CreateDirectory(CoverArt.CoversDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + CoverArt.CoversDir + "\"");
                Log("  已打开封面目录：" + CoverArt.CoversDir);
            }
            catch (Exception ex) { Log("  打开封面目录失败：" + ex.Message); }
        }

        void DoToggleFavorite()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            Lib.SetFlag(selected, "favorite", !selected.Favorite);
            Log("  " + (selected.Favorite ? "已收藏：" : "已取消收藏：") + selected.Title);
            FillGameList();
        }

        void DoTogglePrivate()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            Lib.SetFlag(selected, "private", !selected.Private);
            Log("  " + selected.Title + (selected.Private
                ? " 已标为私密（「全部」里不再出现；到「平台分类 → 私密」查看）"
                : " 已取消私密"));
            FillGameList();
        }

        void DoToggleHidden()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            Lib.SetFlag(selected, "hidden", !selected.Hidden);
            Log("  " + selected.Title + (selected.Hidden
                ? " 已隐藏（勾选上方「显示已隐藏」才看得到）"
                : " 已取消隐藏"));
            FillGameList();
        }

        void DoRenameGame()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            string name = Prompt("重命名", "只改启动器里显示的名字，不动游戏目录与文件。", selected.Title);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) { Log("  名字不能为空"); return; }
            Lib.Rename(selected, name);
            Log("  已重命名为：" + name);
            FillGameList();
            RebuildTarget();
        }

        string Prompt(string title, string hint, string def)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(Theme.S(430), Theme.S(146));
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.ShowIcon = false;
                f.BackColor = Theme.Panel;
                f.ForeColor = Theme.Text;
                f.Font = Theme.F(9f);
                try { Theme.ApplyChrome(f.Handle); } catch { }

                var lb = new Label();
                lb.Text = hint; lb.AutoSize = false;
                lb.Location = new Point(Theme.S(16), Theme.S(12)); lb.Size = new Size(Theme.S(398), Theme.S(34));
                lb.ForeColor = Theme.TextDim; lb.Font = Theme.F(9f);
                f.Controls.Add(lb);

                var tb = new TextBox();
                tb.Text = def;
                tb.Location = new Point(Theme.S(16), Theme.S(52)); tb.Width = Theme.S(398);
                tb.Font = Theme.F(10f);
                tb.BackColor = Theme.Field;
                tb.ForeColor = Theme.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                f.Controls.Add(tb);

                var ok = new FlatBtn(); ok.Text = "确定"; ok.Kind = BtnKind.Primary; ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(Theme.S(246), Theme.S(96)); ok.Width = Theme.S(80);
                var no = new FlatBtn(); no.Text = "取消"; no.DialogResult = DialogResult.Cancel;
                no.Location = new Point(Theme.S(334), Theme.S(96)); no.Width = Theme.S(80);
                f.Controls.Add(ok); f.Controls.Add(no);
                f.AcceptButton = ok; f.CancelButton = no;
                tb.SelectAll();
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // 联网补封面（Steam 官方 CDN，走设置里的代理）；后台跑，完成刷新列表
        void DoFetchCovers()
        {
            if (Program.ShareBuild) { Log("  分享版不含封面功能（游戏列表用信息行显示）"); return; }
            if (coverRunning) { Log("  封面获取正在进行中…"); return; }
            if (!Cfg.DlssgOnlineCovers) { Log("  已关闭联网封面 —— 当前使用本地图标与按名字生成的颜色卡"); return; }
            coverRunning = true;
            if (btnCover != null) { btnCover.Enabled = false; btnCover.Text = "获取中…"; }
            Log(">>> 开始获取封面（Steam 官方 CDN）…");
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                int ok = 0, tried = 0, web = 0;
                foreach (DlssgGame g in new List<DlssgGame>(games))
                {
                    if (CoverArt.HasRealCover(g)) continue;
                    if (g.Id == null || g.Id.Length == 0) g.Id = Lib.IdOf(g);
                    string appid = g.AppId == null ? "" : g.AppId;
                    tried++;
                    if (appid.Length > 0 && CoverArt.DownloadSteam(appid, g.Id)) { ok++; continue; }
                    // 库内 appid 拿不到封面（下架 / 地区限制 / 该 appid 在 CDN 无资源）
                    // → 退一步按名字搜一个替代 appid 再试
                    string alt = CoverArt.SearchAppId(g.Title);
                    if (alt.Length > 0 && alt != appid && CoverArt.DownloadSteam(alt, g.Id))
                    {
                        Lib.RememberAppId(g, alt);
                        ok++;
                        continue;
                    }
                    // 最后一步：联网按名字搜一张竖版图（会访问任意 https 图片站，见设置页说明）
                    if (CoverArt.SearchWebCover(g).Length == 0) web++;
                    if (tried >= 30) break;
                }
                e.Result = ok;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                coverRunning = false;
                if (btnCover != null) { btnCover.Enabled = true; btnCover.Text = "获取封面"; }
                int ok = (e.Result is int) ? (int)e.Result : 0;
                Log("  " + (ok > 0 ? "已获取 " + ok + " 张封面" : "没有新封面（离线 / 名称匹配不到 / 已全部就绪）"));
                CoverArt.ClearCache();
                FillGameList();
            };
            bw.RunWorkerAsync();
        }

        // MergeScan 放后台：它对库里每个条目重跑 Inspect（深度 3 目录递归找帧生成组件），
        // 游戏多/盘慢时在 UI 线程同步跑会冻数百毫秒到秒级（2026-09-30 体检）。
        // Dlssg.MergeScan 内部有串行锁，连点几次也按序合并；完成后回 UI 线程换列表并执行收尾。
        void MergeScanThen(List<DlssgGame> scanned, Action after)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<DlssgGame> merged = scanned;
                try { merged = Dlssg.MergeScan(scanned); }
                catch (Exception ex) { Program.Log("游戏库合并异常: " + ex.Message); }
                try { BeginInvoke((Action)(delegate { games = merged; if (after != null) after(); })); }
                catch { }
            });
        }

        void DoAddGame()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择游戏主程序（exe）";
                dlg.Filter = "程序 (*.exe)|*.exe";
                if (lastAddDir.Length > 0) dlg.InitialDirectory = lastAddDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { lastAddDir = Path.GetDirectoryName(dlg.FileName); } catch { }
                Log(">>> 添加自定义游戏：" + Dlssg.AddCustomGame(dlg.FileName));
            }
            MergeScanThen(games, delegate { FillGameList(); RefreshFgSummary(); });
        }

        // 移除：自定义条目 → 删记录；自动扫描条目 → 加入忽略清单（下次扫描不再出现）
        void DoRemoveGame()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            if (selected.Ignored) { Log("「" + selected.Title + "」本来就在忽略清单里（列表中灰显），无需再移除；要恢复收录请选中它点「开启选中」"); return; }
            // 二次确认：这是列表里唯一有持久副作用、且会让人误以为"游戏被删了"的动作。
            // 2026-09-13 原神就是被误点移除进了忽略清单，因为当时没有放行入口，表现为"装好了却怎么都识别不出来"。
            var okr = MessageBox.Show(this,
                "确定把「" + selected.Title + "」从列表移除？\r\n\r\n" +
                "不会删除任何游戏文件，只是把该目录记进忽略清单（列表里仍会显示，但灰显）。\r\n" +
                "要恢复收录：选中它点「开启选中」，或点「忽略清单」按钮放行。",
                "移除游戏", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (okr != DialogResult.OK) { Log("已取消移除：" + selected.Title); return; }
            Log(">>> 移除：" + Dlssg.RemoveGame(selected));
            MergeScanThen(games, delegate { FillGameList(); RefreshFgSummary(); });
        }

        // 忽略清单管理：点「移除选中」会把游戏目录记进 games.json 的 ignored（下次扫描不再收录），
        // 但此前没有任何入口能捞回来 —— 游戏重装回同一路径、或误点移除，就永久消失。
        // 2026-09-13 用户报「原神怎么都识别不出来」即此：扫描扫到了（日志"识别 11 个" → 列表 10 个），
        // 只因 G:\miHoYo Launcher\games\Genshin Impact Game 躺在 ignored 里被静默滤掉。
        void ShowIgnoredDialog()
        {
            var dlg = new Form();
            dlg.Text = "忽略清单（以下目录不会被扫描收录）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(520), Theme.S(380));
            dlg.ClientSize = new Size(Theme.S(560), Theme.S(440));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var sec = new Sec("被忽略的目录");
            var lv = NewList(new string[] { "目录", "磁盘现状" }, new int[] { 340, 0 }, Theme.S(200));
            lv.MultiSelect = true;
            lv.Resize += delegate { FillLastColumn(lv, Theme.S(140)); };
            sec.Block(lv, Theme.S(200));
            var lblCount = sec.Body("");
            sec.Body("「移除选中」只是把目录记到这里，不动任何游戏文件。\n" +
                     "游戏重装回同一路径、或误点移除时，选中它点「放行选中」即可恢复收录；「全部放行」清空整份清单。");

            var btnRestore = new FlatBtn(); btnRestore.Text = "放行选中"; btnRestore.Kind = BtnKind.Primary; btnRestore.Width = Theme.S(96);
            var btnAll = new FlatBtn(); btnAll.Text = "全部放行"; btnAll.Width = Theme.S(96);
            var btnClose = new FlatBtn(); btnClose.Text = "关闭"; btnClose.Width = Theme.S(84);
            sec.Buttons(btnRestore, btnAll, btnClose);
            pg.Add(sec);
            dlg.Controls.Add(pg);
            pg.BringToFront();

            Action updHint = delegate
            {
                string s = "共 " + lv.Items.Count + " 项";
                if (lv.SelectedItems.Count > 0) s += "，已选 " + lv.SelectedItems.Count + " 项";
                SetBody(lblCount, s);
            };
            Action refresh = null;
            refresh = delegate
            {
                lv.BeginUpdate();
                try
                {
                    lv.Items.Clear();
                    foreach (var d in Dlssg.LoadIgnoredDirs())
                    {
                        string st; Color c;
                        if (!Directory.Exists(d)) { st = "目录不存在（可放行清理）"; c = Theme.TextFaint; }
                        else
                        {
                            var g = Dlssg.Inspect(d, true);
                            if (g != null) { st = "可收录 · " + g.Exe; c = Theme.Ok; }
                            else { st = "目录在，但未通过游戏性判定"; c = Theme.TextDim; }
                        }
                        var it = new ListViewItem(d);
                        it.SubItems.Add(st);
                        it.Tag = d;
                        it.UseItemStyleForSubItems = false;
                        it.SubItems[1].ForeColor = c;
                        lv.Items.Add(it);
                    }
                }
                finally { lv.EndUpdate(); }
                updHint();
            };
            lv.SelectedIndexChanged += delegate { updHint(); };

            bool needRescan = false;
            btnRestore.Click += delegate
            {
                var sel = new List<string>();
                foreach (ListViewItem it in lv.SelectedItems) sel.Add(it.Tag as string);
                if (sel.Count == 0) { Log("请先在忽略清单里选中要放行的目录"); return; }
                string r = Dlssg.UnignoreDirs(sel);
                Log(">>> 忽略清单：" + r);
                if (r.StartsWith("已从忽略清单")) needRescan = true;
                refresh();
            };
            btnAll.Click += delegate
            {
                if (lv.Items.Count == 0) { Log("忽略清单本来就是空的"); return; }
                string r = Dlssg.UnignoreDirs(null);
                Log(">>> 忽略清单：" + r);
                if (r.StartsWith("已从忽略清单")) needRescan = true;
                refresh();
            };
            btnClose.Click += delegate { dlg.Close(); };

            refresh();
            dlg.ShowDialog(this);
            try { dlg.Dispose(); } catch { }
            if (needRescan)
            {
                Log(">>> 忽略清单已放行，正在重新扫描…");
                RunScan(false);
            }
        }

        // ---------------------- 备份与回收（P0-1） ----------------------
        //  明细对话框：路径 / 归属 / 时间 / 体积四列，可以逐项送回收站。
        //  按钮与文案一律写「回收」不写「删除」—— 走的是回收站，写"删除"会让人以为不可逆。
        void ShowJunkDialog()
        {
            var dlg = new Form();
            dlg.Text = "备份与隔离区明细";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(540), Theme.S(400));
            dlg.ClientSize = new Size(Theme.S(640), Theme.S(470));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var sec = new Sec("每一项都可以单独回收（标了「保留」的受保留策略保护）");
            var lv = NewList(new string[] { "目录", "归属", "时间", "体积" }, new int[] { 300, 168, 104, 0 }, Theme.S(230));
            lv.MultiSelect = true;
            lv.Resize += delegate { FillLastColumn(lv, Theme.S(120)); };
            sec.Block(lv, Theme.S(230));
            var lblSum = sec.Body("");
            sec.Body("带 * 的时间是目录修改时间（目录名里没有时间戳，多为早期手工留的），其余取自目录名。\n"
                   + "送进回收站的项可以从回收站「还原」；本工具不硬删任何备份文件。");
            var btnRecycle = new FlatBtn(); btnRecycle.Text = "回收选中"; btnRecycle.Kind = BtnKind.Danger;
            btnRecycle.Width = Theme.S(112);
            var btnRefresh = new FlatBtn(); btnRefresh.Text = "重新统计"; btnRefresh.Width = Theme.S(96);
            var btnClose = new FlatBtn(); btnClose.Text = "关闭"; btnClose.Width = Theme.S(84);
            sec.Buttons(btnRecycle, btnRefresh, btnClose);
            pg.Add(sec);
            dlg.Controls.Add(pg);
            pg.BringToFront();

            Action updHint = delegate
            {
                int n = lv.SelectedItems.Count;
                btnRecycle.Text = n > 0 ? "回收选中（" + n + "）" : "回收选中";
                btnRecycle.Enabled = n > 0;
            };
            lv.SelectedIndexChanged += delegate { updHint(); };

            List<JunkItem> items = new List<JunkItem>();
            Action refresh = delegate
            {
                items = Junk.Scan();
                lv.BeginUpdate();
                try
                {
                    lv.Items.Clear();
                    foreach (JunkItem it in items)
                    {
                        var row = new ListViewItem(it.Dir);
                        row.SubItems.Add(it.Owner + "\\" + it.Bucket + " · " + it.Game
                                         + (it.Keep ? "（保留）" : ""));
                        row.SubItems.Add(it.When.ToString("yyyy-MM-dd HH:mm") + (it.TimeFromName ? "" : " *"));
                        row.SubItems.Add(Junk.Mb(it.Bytes) + " · " + it.Files + " 个文件");
                        row.Tag = it;
                        row.UseItemStyleForSubItems = false;
                        if (it.Keep)
                        {
                            row.ForeColor = Theme.TextFaint;
                            row.SubItems[1].ForeColor = Theme.Warn;
                        }
                        lv.Items.Add(row);
                    }
                }
                finally { lv.EndUpdate(); }
                SetBody(lblSum, Junk.SummaryOf(items));
                updHint();
            };

            btnRecycle.Click += delegate
            {
                var sel = new List<JunkItem>();
                foreach (ListViewItem row in lv.SelectedItems)
                {
                    JunkItem j = row.Tag as JunkItem;
                    if (j != null) sel.Add(j);
                }
                if (sel.Count == 0) { Log("请先在明细里选中要回收的目录"); return; }
                int held = sel.FindAll(delegate(JunkItem j) { return j.Keep; }).Count;
                if (held > 0)
                {
                    var ans = MessageBox.Show(this,
                        "选中项里有 " + held + " 条受保留策略保护（回滚要读它们），不会被回收。\n\n"
                        + "跳过它们、回收其余 " + (sel.Count - held) + " 条？",
                        "含保留项", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (ans != DialogResult.OK) return;
                    sel.RemoveAll(delegate(JunkItem j) { return j.Keep; });
                    if (sel.Count == 0) return;
                }
                if (MessageBox.Show(this, Junk.ConfirmText(sel), "送回收站",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    Log(">>> 备份回收：用户取消，未做任何改动");
                    return;
                }
                DoRecycle(sel);
                refresh();
            };
            btnRefresh.Click += delegate { refresh(); Log(">>> 备份重新统计：" + Junk.SummaryOf(items)); };
            btnClose.Click += delegate { dlg.Close(); };

            refresh();
            dlg.ShowDialog(this);
            try { dlg.Dispose(); } catch { }
            SetJunkLabel();
        }

        // 真正动手的那一条：回收 + 如实写日志 + 刷新设置页那一行
        void DoRecycle(List<JunkItem> items)
        {
            string r;
            try { r = Junk.Recycle(items, this.Handle); }
            catch (Exception ex) { r = "回收异常：" + ex.GetType().Name + "：" + ex.Message; }
            Log(">>> 备份回收：" + r.Replace("\n", "\n      "));
            SetJunkLabel();
        }

        void SetJunkLabel()
        {
            if (lblJunk == null) return;
            try
            {
                List<JunkItem> all = Junk.Scan();
                // 下拉项按当前统计回填条数/体积 —— 默认的 30 天档在新装的机器上"一条都清不掉"
                //  （备份都还没满 30 天），不回填的话用户只会以为这功能没反应。
                if (cbJunkAge != null)
                {
                    int[] ages = JunkAges;
                    string[] head = new string[] { "30 天前（推荐）", "14 天前", "7 天前", "不限时长" };
                    for (int i = 0; i < ages.Length && i < cbJunkAge.Items.Count; i++)
                    {
                        List<JunkItem> a = Junk.Aged(all, ages[i]);
                        cbJunkAge.Items.Set(i, head[i] + " · " + a.Count + " 项 / " + Junk.Mb(Junk.BytesOf(a)));
                    }
                    if (cbJunkAge.SelectedIndex < 0) cbJunkAge.SelectedIndex = 0;
                }
                SetBody(lblJunk, Junk.SummaryOf(all));
                // 可回收的量超过 100 MB 才用告警色 —— 几十 MB 的正常备份不值得闪一行黄字
                lblJunk.ForeColor = Junk.BytesOf(Junk.Reclaimable(all)) > 100L * 1024 * 1024
                                  ? Theme.Warn : Theme.TextDim;
            }
            catch (Exception ex) { SetBody(lblJunk, "备份占用统计失败：" + ex.Message); }
        }

        // 「回收范围」下拉的天数换算：JunkAges 与 cbJunkAge 的项一一对应（0 = 不限时长）。
        int JunkDays()
        {
            if (cbJunkAge == null || cbJunkAge.SelectedIndex < 0) return 30;
            int i = cbJunkAge.SelectedIndex;
            return (i >= 0 && i < JunkAges.Length) ? JunkAges[i] : 30;
        }

        // 停放"不是当前这一套"的活入口代理（P0-3）。
        //  ⚠ 动手前必须把**具体文件名与所属那一代**摊出来给用户确认 ——
        //   这里停错一个就是游戏帧生成直接失效，不能只问一句"确定继续吗"。
        void DoParkStrays()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            string cur = Plan.Current(selected.Dir);
            if (cur == Plan.None)
            {
                Log(">>> 停放遗留项：" + selected.Title + " 目录里没有代理在位（游戏跑原生），无需处理");
                return;
            }
            var stray = new List<string>();
            foreach (Plan.EntryHit h in Plan.InspectEntries(selected.Dir))
                if (h.Owner != cur) stray.Add(h.Label());
            if (stray.Count == 0)
            {
                Log(">>> 停放遗留项：" + selected.Title + " 只有一套代理在位，没有遗留项可停");
                return;
            }
            string msg = "将在「" + selected.Title + "」的目录里改名停放下面 " + stray.Count
                       + " 个代理入口。当前在用的是 " + Plan.PlanName(cur) + "，不会动它：\n\n"
                       + string.Join("\n", stray.ToArray())
                       + "\n\n停放只是把文件改名成 <原名>.parked.<方案>，不删任何文件，随时可还原。\n"
                       + "目录：" + selected.Dir;
            if (MessageBox.Show(this, msg, "停放遗留代理",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                Log(">>> 停放遗留项：用户取消，未做任何改动");
                return;
            }
            string r;
            try { r = Plan.ParkForeignReport(selected.Dir); }
            catch (Exception ex) { r = "异常：" + ex.GetType().Name + "：" + ex.Message; }
            Log(">>> 停放遗留项：" + selected.Title + " —— " + r.Replace("\n", "\n      "));
            Log("  " + Plan.PatrolLine(selected.Dir));
            RefreshFgSummary();
        }

        void DoJunkClean(int days)
        {
            List<JunkItem> all;
            try { all = Junk.Scan(); }
            catch (Exception ex) { Log("备份统计失败: " + ex.Message); return; }
            List<JunkItem> aged = Junk.Aged(all, days);
            string span = days <= 0 ? "不限时长" : "满 " + days + " 天";
            if (aged.Count == 0)
            {
                Log(">>> 备份回收：" + Junk.SummaryOf(all) + " —— 没有「" + span + "」且不受保留策略保护的可回收项");
                MessageBox.Show(this,
                    "没有可回收的项：「" + span + "」的那些要么受保留策略保护（回滚要用），要么本来就没有。\n\n"
                    + Junk.SummaryOf(all),
                    "备份回收", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, Junk.ConfirmText(aged), "送回收站",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                Log(">>> 备份回收：用户取消，未做任何改动");
                return;
            }
            DoRecycle(aged);
        }

        void DoFgInstall()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            string selDir = selected.Dir;

            // 命中忽略清单 → 先放行再继续。这就是"识别不出来"的正解，不该让用户去翻 ProgramData 里的 json
            if (selected.Ignored)
            {
                Log(">>> 恢复收录：" + selected.Title);
                Log("  " + Dlssg.UnignoreDirs(new List<string> { selDir }));
                // 全库合并（含每条目 Inspect 重探）在后台跑，完成后再找回该条目并继续开启流程
                MergeScanThen(games, delegate
                {
                    selected = null;
                    if (games != null)
                        foreach (var g in games)
                            if (string.Equals(g.Dir, selDir, StringComparison.OrdinalIgnoreCase)) { selected = g; break; }
                    if (selected == null) { Log("  ⚠ 恢复后没能在列表里找回该条目，请点「扫描游戏」重试"); return; }
                    if (selected.Ignored)
                    {
                        Log("  ⚠ 忽略清单写入被拒绝（games.json 在 ProgramData，普通权限改不了）—— 请以管理员身份运行本工具后重试");
                        return;
                    }
                    Log("  已恢复收录，继续执行开启流程");
                    FinishFgInstall();
                });
                return;
            }

            FinishFgInstall();
        }

        // DoFgInstall 的开启执行段（"恢复收录"路径在后台合并完成后也走这里）。
        void FinishFgInstall()
        {
            if (selected == null) return;
            if (!selected.HasFrameGen)
            {
                Log("[" + selected.Title + "] 该游戏目录里没有 nvngx_dlssg.dll / sl.dlss_g.dll，代理没有可接管的调用" +
                    (selected.HasFsr3 ? "；但它自带 FSR3 帧生成，可在游戏内直接开" : ""));
                return;
            }
            Log(">>> 开启帧生成: " + selected.Title);
            // 二游/内核反作弊类：注入第三方代理有封号风险（社区已记录反作弊直接拦截代理加载的案例）
            string risk = Dlssg.InjectionRiskNote(selected);
            if (risk != null &&
                MessageBox.Show(risk, "注入风险提示 · " + selected.Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                Log("  已取消：「" + selected.Title + "」风险确认未通过");
                return;
            }
            // 走方案调度器，不再用旧版 Dlssg.Install —— 那条路径**零互斥**：装了 0.3.x 却把
            //  方案 A 的入口留在原地，两套代理同时进进程（2026-09-18 实测的 (0,11008) 就是它）。
            //  核验的源文件也跟着换成"真正会被写进游戏目录的那一份"。
            string fgSrc = Dlssg030.SourceEntryPath(selected.Dir);
            if (!TrustGateFile(fgSrc, selected.Title))
            {
                Log("  已取消：源代理未通过签名核验（" + fgSrc + "）");
                return;
            }
            string r = Plan.SwitchTo(selected.Dir, Plan.D030, true);
            Log("  " + r);
            FillGameList();
            RefreshFgSummary();
            if (string.Equals(Plan.EntryName(XeMfg.Detect(selected.Dir)), Dlssg.D3d12Entry, StringComparison.OrdinalIgnoreCase))
                Log("  验证：完全退出并重启游戏 → 设置里应出现「DLSS 帧生成」。d3d12 是反作弊游戏唯一进得去的名字；"
                    + "若游戏目录下始终不出现 " + Dlssg.LogFolderName + " 日志目录，说明反作弊连它一起拦了 —— 那是社区无解的边界。");
            else
                Log("  验证：完全退出并重启游戏 → 设置里应出现「DLSS 帧生成」；若只有 FSR 选项，点「换入口重试」换个入口再试。");
        }

        // 换入口重试：代理"已加载但从不激活"时的正解。
        // 游戏只加载、不调用某个入口 DLL 时代理永不初始化，游戏内自然只剩它自带的 FSR 帧生成。
        void DoSwitchEntry()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            if (selected.Ignored) { Log("「" + selected.Title + "」还在忽略清单里（列表灰显），先点「开启选中」恢复收录再换入口"); return; }
            Log(">>> 换入口重试: " + selected.Title);
            Log("  " + Dlssg.SwitchEntry(selected));
            FillGameList();
            RefreshFgSummary();
            Log("  下一步：完全退出游戏并重新启动 → 看设置里是否出现「DLSS 帧生成」→ 回工具点「运行诊断」确认日志有 evaluate 事件。");
        }

        void DoFgUninstall()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            Log(">>> 关闭帧生成: " + selected.Title);
            string r = Dlssg.Uninstall(selected);
            Log("  " + r);
            FillGameList();
            RefreshFgSummary();
        }

        void DoFgDiagnose()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            Log(">>> 运行诊断: " + selected.Title);
            Log("  支持情况：" + (selected.HasFrameGen ? "已集成 DLSS 帧生成" : "未集成 DLSS 帧生成") +
                (selected.HasFsr3 ? "；自带 FSR3 帧生成" : "") + (selected.HasUpscaler ? "；DLSS 超分" : ""));
            bool? hwFg = Dlssg.GpuSupportsDlssFg();
            Log("  显卡能力：" + (Dlssg.GpuName().Length > 0 ? Dlssg.GpuName() : "型号未知")
                + "；DLSS 帧生成 " + (hwFg == true ? "官方支持"
                    : (hwFg == false ? "官方不支持 → 走 dlssg_for_sm86 补 SM86/SM75 内核跑 NVIDIA 原生 DLSS-G（不是 FSR 转发）" : "无法判定")));
            Log("  游戏能力：" + (selected.HasFrameGen
                    ? "目录里有 nvngx_dlssg.dll / sl.dlss_g.dll → 代理有作用点"
                    : "没有 DLSS 帧生成组件 → 代理没有可接管的调用，装了也不生效")
                + (selected.HasFsr3 ? "；另自带 FSR3 帧生成（游戏内可直接开）" : "")
                + (selected.Ignored ? "；⚠ 当前在忽略清单里（点「开启选中」可恢复收录）" : ""));
            Log("  安装状态：" + (selected.Installed ? ("已开启（入口 " + selected.Entry + "）") : "未开启"));
            // v2.1.0：d3d12 复合入口的完整性校验（三件套缺一件 → 游戏启动就崩）
            if (string.Equals(selected.Entry, Dlssg.D3d12Entry, StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(selected.Dir, Dlssg.D3d12Entry)))
            {
                string dv = Dlssg.VerifyD3d12(selected.Dir);
                Log("  d3d12 三件套：" + (dv.Length == 0
                    ? "完整（代理 3,584 B + 系统原件 + 载体，导入链闭合）"
                    : "异常 → " + dv));
            }
            else
            {
                string dh = Dlssg.D3d12Hint();
                if (dh.Length > 0) Log("  d3d12 入口：" + dh);
            }
            var residue = Dlssg.ScanResidue(selected.Dir);
            if (residue.Count > 0)
                Log("  ⚠ 发现 " + residue.Count + " 个反作弊隔离残留（形如 version.dll.数字）→ 点「清理残留」删除");
            var v = Dlssg.CheckLogs(selected.Dir);
            Log("  运行日志：" + v.Message + (v.LastLog.Length > 0 ? "    (" + v.LastLog + ")" : ""));
            Log("  进程状态：" + Dlssg.ProcessEntryState(selected));
            Log("  HAGS：" + Dlssg.HagsText() + (Dlssg.HagsState() == 2 ? "" : "  ⚠ 帧生成必需项，未开启则不会生效"));
            if (v.HasLogDir && !v.Activated)
                Log("  ⚠ 代理已加载但未激活：该入口 DLL 只被加载、从未被调用 → 点「换入口重试」，再重启游戏验证");
            else if (v.HasLogDir && v.Activated && !v.FrameGenRunning)
                Log("  代理工作正常，等待游戏内开启帧生成选项");
            else if (!v.HasLogDir && selected.Installed)
                Log("  ⚠ 尚无日志目录：代理装好后还没被游戏加载过。启动一次游戏再看；若游戏内只有 FSR 选项，点「换入口重试」。");
        }

        // 清理反作弊隔离残留（v2.1.0）：反作弊把被拦的代理改名成 xxx.dll.<数字> —— 文件不会被加载，
        // 却白占 15 MB，用户还会误以为"装上了但没生效"。
        // 一键启动（非官方启动器：直接跑 exe，绕开官方启动器的文件完整性校验弹窗）
        void DoLaunchGame()
        {
            if (selected == null) { Log("  请先选中游戏（双击卡片也可启动）"); return; }
            PreApplyResLink(selected);          // 先摆好显示状态，再拉游戏（见方法头注释）
            string err = Lib.Launch(selected);
            // 自动定位不到启动文件：让用户指认一次（选择永久记住，之后点启动直接命中）
            if (err != null && err.StartsWith(Lib.NeedLocate, StringComparison.Ordinal))
            {
                Log("  没能自动定位「" + selected.Title + "」的启动文件，请在弹出的窗口里指认它的主程序（exe）");
                string picked = PickLaunchExe(selected);
                if (picked.Length > 0)
                {
                    Log("  已记住启动文件：" + picked);
                    err = Lib.Launch(selected);
                }
                else
                {
                    string dir = err.Substring(Lib.NeedLocate.Length);
                    try { if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
                    Log("  已取消指认：打开了游戏文件夹。下次点「启动游戏」可重新指认。");
                    return;
                }
            }
            if (err.Length > 0) { Log("  " + err); return; }
            string what = (selected.Platform == "steam" && selected.AppId != null && selected.AppId.Length > 0)
                ? "steam://rungameid/" + selected.AppId
                : Lib.LaunchTargetOf(selected);     // 与真正被启动的那条路径一致（exe 可能在子目录里）
            string largs = (selected.LaunchArgs == null ? "" : selected.LaunchArgs).Trim();
            Log("  已启动：" + selected.Title + "   (" + what + (largs.Length > 0 ? " " + largs : "") + ")");
            FillGameList();     // 刷新「最近游玩」排序
            // 带 DX12 参数启动时自动验证（用户要求：必须有反馈，别让人猜有没有生效）
            if (largs.IndexOf(Lib.ArgDx12, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("  正在确认运行模式（读游戏的 Player.log，最多 90 秒）…");
                StartDx12Verify(selected);
            }
        }

        // 启动指认对话框：自动解析失败时让用户挑出真正的启动 exe，选择经 Lib.SetLaunchExe
        // 持久化进 library.json —— 之后 LaunchTargetOf 最高优先级返回它（用户只指认这一次）。
        string PickLaunchExe(DlssgGame g)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "定位「" + (g.Title == null ? "游戏" : g.Title) + "」的启动程序（选择后自动记住）";
                dlg.Filter = "程序 (*.exe)|*.exe";
                try { if (g.Dir != null && Directory.Exists(g.Dir)) dlg.InitialDirectory = g.Dir; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return "";
                string r = MetaStore.SetLaunchExe(g, dlg.FileName);
                if (r.Length > 0) Log("  " + r);
                return dlg.FileName;
            }
        }

        // 选中库条目 → 分辨率联动规则的识别键（设置页那两个下拉用的就是 "cs" / "VALORANT" 这两个键）。
        // 为什么要桥接：规则里记的是**游戏进程名**，而库条目记的是**启动文件** ——
        //   WeGame 版无畏契约的启动文件是 aclos-launcher.exe，跟 "VALORANT" 对不上，只能用文件名/标题映射一次。
        string ResKeyOfGame(DlssgGame g)
        {
            if (g == null) return "";
            string exe = (g.Exe == null ? "" : g.Exe).ToLowerInvariant();
            if (exe.EndsWith(".exe")) exe = exe.Substring(0, exe.Length - 4);
            string title = (g.Title == null ? "" : g.Title).ToLowerInvariant();
            if (exe.StartsWith(ResKeyCs) || title.Contains("反恐精英") || title.Contains("counter-strike")) return ResKeyCs;
            if (exe.StartsWith("valorant") || exe.StartsWith("riot") || exe.Contains("aclos")
                || title.Contains("无畏契约") || title.Contains("valorant")) return ResKeyVal;
            return "";
        }

        // 启动游戏**之前**先把显示联动应用上（v3.9.1）。
        // 为什么顺序不能反（2026-09-23 用户报"进游戏没有真拉伸效果"）：
        //   无畏契约的「真拉伸」取决于**游戏启动那一刻**能否读到显示器的原生宽高比 —— 读到了它就把画面
        //   锁回 16:9（拉伸被吃掉）；在设备管理器里禁用监视器正是让它读不到（社区工具 StretchyVal 就是这么做的）。
        //   而进程轮询式的联动是"游戏已经起来之后"才禁屏 —— 对拉伸来说已经晚了，得重启游戏才生效。
        // 只在"确认能找到启动文件"时才动显示状态：启动找不到文件还留着禁屏是最糟的结果。
        void PreApplyResLink(DlssgGame g)
        {
            try
            {
                if (!Cfg.ResLinkEnable || g == null) return;
                string key = ResKeyOfGame(g);
                if (key.Length == 0) return;      // 认不出游戏 → 交给进程检测那条常规路径
                bool steam = g.Platform == "steam" && g.AppId != null && g.AppId.Length > 0;
                if (!steam && Lib.LaunchTargetOf(g).Length == 0) return;
                ResLinkRule rr = ResRule(key);
                if (rr == null) return;
                string did = ResLink.Apply(rr);
                if (!ResLink.Active) return;
                resPreAt = DateTime.Now;      // 看门狗起点（见 UpdateLink）
                Log("[分辨率联动] 启动前预应用" + (did.Length > 0 ? "：" + did : "（本次无变化）")
                    + "　· 先于游戏读取显示器配置，真拉伸才吃得到");
            }
            catch (Exception ex) { Log("启动前预应用显示联动失败：" + ex.GetType().Name + " " + ex.Message); }
        }

        // 分享版首次运行：免责声明摆在最前面（公开分享必须有）。写一个标记文件，之后不再弹。
        void ShowShareDisclaimer()
        {
            if (!Program.ShareBuild) return;
            try
            {
                string mark = Path.Combine(Program.DataDir, "share-ack.txt");
                if (File.Exists(mark)) return;
                string txt =
                    "这是 Fluxion 的分享版（只含帧生成相关功能，不做系统调优）。\n\n"
                  + "开箱前请读完这四条：\n"
                  + "· 工具会往游戏目录写入第三方 DLL（帧生成代理）并修改显卡驱动配置。带内核反作弊的游戏"
                  + "（绝区零 / 鸣潮 等）使用它属于明确违反用户协议，封号风险由你自己承担。\n"
                  + "· 「显卡名伪装」会把整机看到的显卡型号改成别的型号（随时可一键还原），显卡驱动升级后需要重做。\n"
                  + "· 工具需要管理员权限（写游戏目录 / 改驱动配置），运行时会有一次 UAC 提示。\n"
                  + "· 帧生成需要 Windows 的「硬件加速 GPU 调度（HAGS）」开启；基础帧低于 40 时开启会更糊、延迟更高。\n\n"
                  + "点「确定」表示你已知悉以上风险。";
                MessageBox.Show(txt, "使用前须知 · Fluxion 分享版",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                File.WriteAllText(mark, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // DX12 模式启动开关（v3.3.4）：绝区零 3.0+ 的「超分辨率 / 帧生成」设置项只在 DX12 模式下出现
        // （DX11 下整块面板不渲染——2026-09-17 本机实测，Player.log 显示 Direct3D 11.0）。
        // 参数用 -use-d3d12（社区实测有效）；v3.3.3 用的 -force-d3d12 无效，已废弃。
        void DoToggleDx12()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            bool on = !Lib.HasDx12Arg(selected);
            Lib.SetDx12(selected, on);
            if (on)
            {
                Log("  已开启 DX12 模式启动（" + Lib.ArgDx12 + "）：" + selected.Title);
                Log("  下一步：右键「启动游戏」→ 启动后本工具会自动确认是否真的跑在 DX12");
            }
            else Log("  已改回默认 DX11 模式（参数已移除）：" + selected.Title);
            FillGameList();
        }

        // 手动检测运行模式（右键菜单）：读游戏自己写的 Unity 日志，给一句明确结论。
        void DoCheckRenderApi()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            long logUnix;
            string api = Lib.RenderApiEx(selected, out logUnix);
            if (api.Length == 0)
            {
                Log("  未读到「" + selected.Title + "」的运行日志：请先用本工具启动一次游戏再看。");
                return;
            }
            string when = logUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(logUnix).ToLocalTime().ToString("MM-dd HH:mm") : "?";
            if (api == "DX12")
                Log("  运行模式：" + api + " ✅（" + when + " 的记录）—— 游戏内「设置 → 画面 → 高级」应有「超分辨率 / 帧生成」");
            else
                Log("  运行模式：" + api + " ⚠（" + when + " 的记录）—— DX12 未生效："
                    + (Lib.HasDx12Arg(selected) ? "参数已配好，用「启动游戏」重新进一次" : "先右键开「DX12 模式启动」再启动游戏"));
        }

        // 启动后自动验证：每 5 秒读一次该游戏的 Unity 日志，最多 90 秒，给出一句结论。
        void StartDx12Verify(DlssgGame g)
        {
            long launched = MetaStore.Now();
            int ticks = 0;
            var t = new System.Windows.Forms.Timer();
            t.Interval = 5000;
            t.Tick += delegate
            {
                ticks++;
                long logUnix;
                string api = Lib.RenderApiEx(g, out logUnix);
                bool fresh = logUnix >= launched - 5;
                if (api.Length > 0 && fresh)
                {
                    t.Stop(); t.Dispose();
                    if (api == "DX12")
                        Log("  ✅ 已确认：游戏跑在 Direct3D 12 —— 游戏内「设置 → 画面 → 高级」应出现「超分辨率 / 帧生成」");
                    else
                        Log("  ⚠ 仍是 Direct3D 11：-use-d3d12 没生效（显卡/驱动不支持，或被杀软/反作弊拦了参数）");
                    return;
                }
                if (ticks >= 18)
                {
                    t.Stop(); t.Dispose();
                    Log("  ⚠ 90 秒内没读到本次启动的日志：确认游戏真的起来了；起来后在卡片右键「检测运行模式」复查。");
                }
            };
            t.Start();
        }

        // 打开游戏目录（便于手动放文件 / 建快捷方式）
        void DoOpenGameDir()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            try
            {
                if (!Directory.Exists(selected.Dir)) { Log("  目录不存在：" + selected.Dir); return; }
                Process.Start("explorer.exe", "\"" + selected.Dir + "\"");
                Log("  已打开：" + selected.Dir);
            }
            catch (Exception ex) { Log("  打开失败：" + ex.Message); }
        }

        // ---------------------- 方案推荐：一键应用 ----------------------
        void RefreshRec()
        {
            if (lblRecBig == null) return;
            var rec = Dlssg.BuildRec(selected);
            if (lblXeNote != null) SetBody(lblXeNote, Dlssg.OptiNote(selected));
            SetBody(lblRecBig, rec.Head);
            SetBody(lblRecStars, rec.Stars.Length > 0 ? (rec.Stars + "   " + rec.Verdict + "   ·   " + rec.State)
                                                     : rec.State);
            SetBody(lblRecWhy, rec.Why);
            Color c = rec.Bad ? Theme.Err : (rec.Dim ? Theme.TextDim : Theme.Accent);
            lblRecBig.ForeColor = c;
            lblRecStars.ForeColor = c;
            btnRecApply.Text = rec.Btn;
            btnRecApply.Enabled = rec.Action != "none";
        }

        void DoApplyRecommend()
        {
            if (selected == null) { Log("  先在「游戏库」页点一张卡片选中游戏"); return; }
            var rec = Dlssg.BuildRec(selected);
            Log(">>> 一键应用推荐：" + rec.Head);
            if (rec.Action == "none")
            {
                MessageBox.Show(rec.Head + "\n\n" + rec.Why, "方案推荐",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (rec.Action == "opti")
            {
                DoXeDeploy();
            }
            else if (rec.Action == "dx12")
            {
                Lib.SetDx12(selected, true);
                Log("  已开启 DX12 模式启动（" + Lib.ArgDx12 + "）：" + selected.Title);
                Log("  下一步：点「启动游戏」；启动后本工具会自动确认是否真的跑在 DX12。");
                FillGameList();
            }
            else if (rec.Action == "030")
            {
                Do030Deploy();
            }
            else if (rec.Action == "030spoof")
            {
                Do030Deploy();
                var st = GpuSpoof.Detect();
                if (!st.Spoofed)
                {
                    var ans = MessageBox.Show(
                        "绝区零还差最后一步：把显卡名伪装成 " + GpuSpoof.Target + "。\n"
                        + "不做这一步，游戏看不到帧生成选项（这是作者对「网游」明确要求的前置条件）。\n\n"
                        + "现在一并开启吗？",
                        "还差一步", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ans == DialogResult.Yes) DoSpoof(true);
                }
            }
            RefreshRec();
            RefreshSpoof();
        }

        // ---------------------- 显卡名伪装（内置） ----------------------
        void RefreshSpoof()
        {
            if (lblSpoof == null) return;
            GpuSpoof.EnsureBackup();       // 未伪装时先把原始显卡名存下来，保证还原一定精确
            SetBody(lblSpoof, GpuSpoof.StatusLine());
        }

        void DoSpoof(bool on)
        {
            if (on)
            {
                var ans = MessageBox.Show(
                    "把显卡名改为 " + GpuSpoof.Target + "。\n\n"
                    + "· 这是作者对「网游」明确要求的前置条件：绝区零的 DLSS / 帧生成选项靠它才会出现。\n"
                    + "· 副作用：全系统所有程序看到的都是 RTX 5090；显卡驱动升级后要重做。\n"
                    + "· 改完需重启电脑（或点「重启显卡设备」，不用重启系统）。\n"
                    + "· 随时可点「还原真实显卡名」一键回退。\n\n"
                    + "确定开启吗？", "开启显卡名伪装", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
                Log(">>> 开启显卡名伪装：" + GpuSpoof.Apply());
            }
            else
            {
                var ans = MessageBox.Show(
                    "还原真实显卡名。\n\n"
                    + "绝区零的 DLSS / 帧生成选项会随之消失（那本来就是伪装的副产品）。\n"
                    + "同样需要重启电脑（或「重启显卡设备」）才生效。\n\n"
                    + "确定还原吗？", "还原显卡名", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans != DialogResult.Yes) return;
                Log(">>> 还原显卡名：" + GpuSpoof.Restore());
            }
            RefreshSpoof();
        }

        void DoSpoofRestartDevice()
        {
            Log(">>> 重启显卡设备：" + GpuSpoof.RestartAdapter());
            RefreshSpoof();
        }

        void DoXeDeploy()
        {
            if (selected == null) { Log("  请先在游戏列表里选中绝区零 / 鸣潮，再点部署"); return; }
            string kind = XeMfg.Detect(selected.Dir);
            if (kind.Length == 0) { Log("  该游戏不在支持列表（当前仅绝区零 / 鸣潮）"); return; }
            if (MessageBox.Show(
                "将往游戏目录部署 OptiScaler 注入组件。\n\n"
                + "· 绝区零：d3d12.dll 入口 · 伪装 RTX 5090 · 帧生成 DLSSG→XeFG（实测可绕反作弊按名拦截）\n"
                + "· 鸣潮：dxgi.dll 入口 · 帧生成走游戏原生 DLSSG（SM86 解锁）· 同时带 DLSS 5 神经渲染 NR\n"
                + "· 属于第三方组件注入，带内核反作弊的游戏理论上仍有封号风险，请自行判断\n"
                + "· 覆盖的游戏自带文件会自动备份；你已调好的 OptiScaler.ini 会被保留，不会被出厂值覆盖\n"
                + "· 本方案与「DLSS MFG 0.3.x」互斥（两套抢同一个入口 DLL）；切换会自动完成：\n"
                + "  本方案会把 0.3.x 的入口与配套组件改名停放，不删任何文件，随时可切回\n\n确定部署吗？",
                "切换到方案 A · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Log("切换到 " + Plan.PlanName(Plan.Opti) + "：" + selected.Dir);
            Log("  " + Plan.SwitchTo(selected.Dir, Plan.Opti));
            FillGameList();
            RefreshFgSummary();
        }

        void DoXeUndeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (MessageBox.Show(
                "把方案 A 的入口代理与配套组件改名停放（<文件>.parked.opti），游戏回到原生状态。\n\n"
                + "· 不删除任何文件：全部改名保留，OptiScaler.ini 里你调好的伪装 / NR 参数也一起存着\n"
                + "· 想切回来：回到本区块点「切换到本方案」即可，停放的文件会原样改回原名\n\n"
                + "停放期间会失去什么（切回来就恢复）：\n"
                + "· 绝区零 / 鸣潮：设置里的「帧生成」选项会消失（它是 OptiScaler 的显卡名伪装换来的）\n"
                + "· 鸣潮还会同时失去「DLSS 5 NR」\n\n确定停放吗？",
                "停放方案 A · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Log("停放方案 A（入口 + 配套组件，只改名不删文件）：" + selected.Dir);
            Log("  " + Plan.SwitchTo(selected.Dir, Plan.None));
            FillGameList();
            RefreshFgSummary();
        }

        // 前置条件检查：驱动版本 / 显卡名伪装状态 / 系统版本 / 6X 前提，逐条给结论
        void Do030Precheck()
        {
            Log(">>> " + Dlssg030.Ver + " 前置条件检查");
            foreach (string ln in Dlssg030.PrereqReport().Split('\n'))
            {
                string q = ln.TrimEnd();
                if (q.Length > 0) Log("  " + q);
            }
            Log("  提示：以上任一项不满足，游戏里可能只出现 FSR 或干脆没有「帧生成」选项。");
        }

        void Do030Deploy()
        {
            if (selected == null) { Log("  请先在游戏库选中绝区零 / 鸣潮，再点部署"); return; }
            string kind = XeMfg.Detect(selected.Dir);
            bool generic = kind.Length == 0;      // 通用（3A 单机）：作者文档只要求入口 + ini 两个文件
            // 绝区零实测：装 0.3.x 之后设置里依然没有帧生成选项 ——
            //  因为 0.3.x 走代理模式、**不做显卡名伪装**，而绝区零的 DLSS / 帧生成选项正是靠伪装才会出现。
            //  2026-09-16 两次实测都如此，而且 0.3.x 还会顶掉本来能用的方案 A（用户照提示点卸载导致）。
            //  所以这里在用户按下按钮的那一刻就把事实摆出来。
            // 2026-09-18 改正两处与实测相反的文案：
            //  ① "方案 A 在绝区零不可用、从未加载成功" 是错的 —— 绝区零现在跑的就是方案 A
            //     （d3d12.dll 入口，OptiScaler.log 20:33 还在写）；
            //  ② 教用户导入 .reg 改显卡名是危险的：DeviceFP 已把 MIHOYOSDK_GPU_NAME 记成 invalid，
            //     走注册表还是走 DXGI 伪装都会被判环境篡改 → (0,11008) 闪退。
            //  另：这六行原来把换行写成 `\\n`（字面反斜杠），弹窗里显示的就是一排 `\n` —— 一并修掉。
            string zzzWarn = kind == "zzz"
                ? "※ 绝区零请注意（2026-09-18 实测改正）：\n"
                + "  · 绝区零按显卡名决定是否显示 DLSS / 帧生成选项，3060 Ti 会被判不支持；\n"
                + "  · 但**不要**再导 D:\\youhua\\绝区零-显卡名伪装\\ 里的 .reg：\n"
                + "    DeviceFP 已经把你报的 GPU 名记成 invalid（MIHOYOSDK_GPU_NAME），\n"
                + "    伪装无论走注册表还是走 DXGI 都会被判环境篡改 → 进游戏后弹 (0,11008) 闪退。\n"
                + "  · " + Dlssg030.Knowledge("zzz-spoof", "版本相关的伪装结论请先看说明页，别照旧印象操作") + "。\n"
                + "    （这条是按当前代理版本 " + Dlssg030.Ver + " 从 catalog.json 的 knowledge 表挑的）\n"
                + "  · 绝区零当前唯一能让「帧生成」选项出现的是方案 A。要用方案 A 就关掉本对话框，\n"
                + "    在上方「方案 A · OptiScaler 注入」区块点「切换到本方案」。\n\n"
                : "";
            if (MessageBox.Show(
                zzzWarn
                + "将部署 DLSSG for SM86 " + Dlssg030.Ver + "（代理模式，真 DLSS 多帧生成）。\n\n"
                + "· 代理为自签名的第三方 DLL，带内核反作弊的游戏理论上仍有封号风险，请自行判断\n"
                + (generic
                    ? "· 通用模式（3A 单机）：只放入口 " + Cfg.DlssgGenericEntry + " + dlssg_sm86.ini 两个文件，\n"
                    + "  不覆盖游戏自带的 nvngx 运行库；单机通常不需要改显卡名。\n"
                    + "  若游戏不加载这个入口名，回到本区块把「通用游戏入口」换成别的名字再试。\n"
                    + "· 目录里已有的旧版代理（14.9 MB 那种）会被整套停放（只改名，不删文件）\n"
                    : "· 与方案 A 互斥（两套抢同一个入口 DLL），切换会自动完成：\n"
                    + "  本方案会把方案 A 的入口与配套组件改名停放（<文件>.parked.opti），不删任何文件；\n"
                    + "  想切回去，到上一个区块点「切换到本方案」即可，停放的文件会原样改回原名。\n"
                    + "· 覆盖的游戏自带 nvngx 运行库会自动备份\n")
                + "\n确定为它切换到本方案吗？",
                "切换到 DLSS MFG " + Dlssg030.Ver + " · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string srcEntry = Dlssg030.SourceEntryPath(selected.Dir);
            if (!TrustGateFile(srcEntry, selected.Title))
            {
                Log("  已取消：源代理未通过签名核验（" + srcEntry + "）");
                return;
            }
            Log("部署 DLSS MFG " + Dlssg030.Ver + "：" + selected.Dir + (generic ? "（通用模式，入口 " + Cfg.DlssgGenericEntry + "）" : ""));
            foreach (string ln in Dlssg030.PrereqReport().Split('\n'))
            {
                string q = ln.TrimEnd();
                if (q.Length > 0) Log("  " + q);
            }
            Log("  " + Plan.SwitchTo(selected.Dir, Plan.D030));
            FillGameList();
            RefreshFgSummary();
        }

        void Do030Undeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (MessageBox.Show(
                "将把 0.3.x 的入口代理、dlssg_sm86.ini 与日志目录改名停放（<文件>.parked.030），游戏回到原生状态。\n"
                + "· 不删除任何文件，想再切回来随时可以\n"
                + "· 仅影响本游戏目录，别处不受影响\n\n确定停放吗？",
                "停放 DLSS MFG " + Dlssg030.Ver + " · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log("停放 " + Plan.PlanName(Plan.D030) + "（入口 + 配套组件，只改名不删文件）：" + selected.Dir);
            Log("  " + Plan.SwitchTo(selected.Dir, Plan.None));
            FillGameList();
            RefreshFgSummary();
        }

        void DoCleanResidue()
        {
            if (selected == null) { Log("请先在列表里点选一个游戏"); return; }
            Log(">>> 扫描反作弊隔离残留: " + selected.Title);
            var r = Dlssg.ScanResidue(selected.Dir);
            if (r.Count == 0)
            {
                Log("  未发现残留（形如 version.dll.1271877294 的被隔离文件）");
                return;
            }
            double mb = 0;
            foreach (var f in r)
            {
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                mb += len / 1048576.0;
                Log("  发现 " + Path.GetFileName(f) + "    " + Math.Round(len / 1048576.0, 2) + " MB");
            }
            if (MessageBox.Show("发现 " + r.Count + " 个被反作弊改名隔离的代理文件（合计 " + Math.Round(mb, 2)
                    + " MB）。\n\n这些文件不会被游戏加载，删掉不影响游戏，只是回收空间。\n要删除吗？",
                    "清理残留 · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Log("  已取消");
                return;
            }
            int n = 0;
            foreach (var f in r)
            {
                try { File.Delete(f); n++; }
                catch (Exception ex) { Log("  删除失败 " + Path.GetFileName(f) + "：" + ex.Message); }
            }
            Log("  已删除 " + n + " / " + r.Count + " 个残留文件");
        }

        void DoApplyFgCfg()
        {
            Cfg.DlssgRouter = cbRouter.SelectedIndex == 1 ? "SM75" : "SM86";
            Cfg.DlssgKernel = cbKernel.SelectedItem != null ? cbKernel.SelectedItem.ToString() : "PTX";
            Cfg.DlssgBilinear = cbBilinear.SelectedIndex == 1 ? 1 : 0;
            Cfg.DlssgMaxFrames = cbMaxFrames.SelectedIndex + 1;
            Cfg.DlssgLogLevel = cbLogLevel.SelectedIndex;
            Cfg.DlssgNrEnabled = cbNr.SelectedIndex == 1;
            Program.SetConfigStr("dlssg", "router", Cfg.DlssgRouter);
            Program.SetConfigStr("dlssg", "kernelImage", Cfg.DlssgKernel);
            Program.SetConfigInt("dlssg", "hardwareBilinear", Cfg.DlssgBilinear);
            Program.SetConfigInt("dlssg", "maxGeneratedFrames", Cfg.DlssgMaxFrames);
            Program.SetConfigInt("dlssg", "loggingLevel", Cfg.DlssgLogLevel);
            Program.SetConfigBool("dlssg", "nrEnabled", Cfg.DlssgNrEnabled);

            // 逐游戏：先看这个目录里到底有没有我们的代理、是哪一代，再决定写什么。
            //  2026-09-16 事故：旧逻辑对所有游戏写同一份 261 字节模板 + Config 里的 maxGeneratedFrames(=5)，
            //  而旧版代理（14.9 MB / 310.1 运行库）只认 1..3 → 报 "Invalid INI integer" →
            //  FH6 直接卡死在加载界面。写之前必须先认代理，写的时候必须按"代理年代"夹上限。
            int n030 = 0, nOld = 0, nSkip = 0, nOpti = 0;
            foreach (var g in games)
            {
                if (g == null || g.Dir == null) continue;
                string gdir = XeMfg.TargetDir(g.Dir, XeMfg.Detect(g.Dir));
                string own = "";
                foreach (string e in Plan.AllEntryNames)
                {
                    string f = Path.Combine(gdir, e);
                    if (!File.Exists(f)) continue;
                    string o = Plan.OwnerOf(f);
                    if (o.Length > 0) { own = o; break; }
                }
                if (own.Length == 0) { nSkip++; continue; }        // 没接入本方案：绝不碰它的配置文件
                try
                {
                    if (own == Plan.D030)
                    {
                        // 0.3.x 的 INI 多了 Enabled / Optimized / Preset / Directory / Runtime，
                        //  不能整份覆盖（会丢键），只改 MaxGeneratedFrames 一行。
                        //  先修复被旧模板污染的 ini（261B 版含 Router=SM86，0.3.x 当整数解析直接报
                        //  configuration_error「Invalid INI integer」→ FH6 卡死在加载界面，2026-09-16 实测）
                        Dlssg030.EnsureFactoryIni(gdir);
                        string r = Dlssg030.SetMaxFrames(g.Dir, Cfg.DlssgMaxFrames);
                        if (r.IndexOf("已设为") >= 0) n030++;
                        Log("  0.3.x · " + g.Title + "：" + r);
                    }
                    else if (own == Plan.Legacy)
                    {
                        int m = Math.Min(Cfg.DlssgMaxFrames, 3);
                        File.WriteAllText(Path.Combine(gdir, Dlssg.IniName),
                                          Dlssg.BuildIniFor(m, Cfg.DlssgLogLevel), new UTF8Encoding(false));
                        nOld++;
                        if (m != Cfg.DlssgMaxFrames)
                            Log("  " + g.Title + "：装的是旧版代理（上限 4X），已按 MaxGeneratedFrames=3 写入"
                                + "；要 6X 请先点「切换到本方案」升级到 0.3.x");
                    }
                    else // Plan.Opti：方案A 在用。OptiScaler 的参数归 OptiScaler.ini / 游戏内 F10 菜单调，
                         // 它根本不读 dlssg_sm86.ini —— 绝不能把旧模板写进去（2026-09-16 02:40 事故根源）
                    {
                        // 唯一的例外：DLSS 5 神经渲染的总开关。它只改 [DlssNr] Enabled 一行，
                        //  且这是**用户明确要求可调**的一项（此前被 2.9.1 固化成 true，每次启动必开）。
                        string nr = XeMfg.SetNrEnabled(g.Dir, Cfg.DlssgNrEnabled);
                        if (nr.IndexOf("已") >= 0) nOpti++;
                        Log("  方案A · " + g.Title + "：" + nr);
                    }
                }
                catch { }
            }
            Log("帧生成参数已保存（Router=" + Cfg.DlssgRouter + " 内核=" + Cfg.DlssgKernel +
                " 光流=" + Cfg.DlssgBilinear + " 最大帧=" + Cfg.DlssgMaxFrames + " 日志=" + Cfg.DlssgLogLevel + "）");
            if (n030 > 0) Log("  已同步 " + n030 + " 个 0.3.x 部署游戏的倍率上限（需重启游戏生效）");
            if (nOld > 0) Log("  已更新 " + nOld + " 个旧版代理游戏的配置文件（上限 4X，需重启游戏生效）");
            if (nSkip > 0) Log("  跳过 " + nSkip + " 个未接入本方案的游戏（不写它们的配置文件）");
            if (nOpti > 0) Log("  跳过 " + nOpti + " 个方案A游戏（OptiScaler 的参数在游戏内 F10 菜单调，不写它的 ini）");
        }

        // ---------------------- 托盘 ----------------------
        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("打开主窗口");
            miOpen.Click += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            miGame = new ToolStripMenuItem("未检测到游戏");
            miGame.Enabled = false;
            miRemote = new ToolStripMenuItem("远控：—");
            miRemote.Enabled = false;
            var miOpt = new ToolStripMenuItem("一键优化");
            miOpt.Click += delegate { DoOptimize(); };
            var miRestore = new ToolStripMenuItem("恢复备份");
            miRestore.Click += delegate { DoRestore(); };
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate
            {
                closing = true;
                // 分享版没有加速包/远控这类常驻联动进程，不需要收尾清理
                if (!Program.ShareBuild) try { Program.GameBoostCleanupOrphans(); } catch { }
                tray.Visible = false;
                Application.Exit();
            };
            menu.Items.Add(miOpen);
            // 分享版托盘只留「打开主窗口 / 退出」：一键优化、恢复备份、远控状态都是本机专属功能
            if (!Program.ShareBuild)
            {
                menu.Items.Add(miGame);
                menu.Items.Add(miRemote);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(miOpt);
                menu.Items.Add(miRestore);
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            try { tray.Icon = Program.GetAppIcon(); } catch { }
            tray.Text = "Fluxion";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };

            // 关闭窗口 = 弹框询问「最小化到托盘 / 退出程序」（可记住选择），托盘菜单的「退出」永远直接退。
            // v2.0.7 之前是无声收进托盘 —— 用户点 X 以为退出了、程序其实还在后台跑，容易误会。
            // 关机 / 任务管理器结束时不能弹框（会卡住关机流程），一律按「收进托盘」处理。
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (closing) { if (tray != null) tray.Visible = false; return; }
                if (e.CloseReason == CloseReason.WindowsShutDown
                    || e.CloseReason == CloseReason.TaskManagerClosing) { e.Cancel = true; Hide(); return; }

                bool toTray = Cfg.CloseToTray;
                if (Cfg.CloseAsk)
                {
                    bool remember;
                    if (!AskCloseAction(out toTray, out remember)) { e.Cancel = true; return; }   // 取消 = 不关
                    if (remember)
                    {
                        Cfg.CloseAsk = false; Cfg.CloseToTray = toTray;
                        SaveCfgBool("ui", "closeAsk", false);
                        SaveCfgBool("ui", "closeToTray", toTray);
                        RefreshCloseCombo();     // 设置页那个下拉跟着换，避免两处显示不一致
                        Log("关闭行为已记住：" + (toTray ? "最小化到通知区域" : "退出程序")
                            + "（想恢复询问：设置 → 窗口与托盘）");
                    }
                }
                if (!toTray)
                {
                    closing = true;
                    try { Program.GameBoostCleanupOrphans(); } catch { }
                    if (tray != null) tray.Visible = false;
                    return;      // 不 Cancel：主窗口正常关闭，Application.Run 的消息循环随之退出
                }
                e.Cancel = true;
                Hide();
                if (!trayTipShown)
                {
                    trayTipShown = true;
                    if (tray != null)
                        tray.ShowBalloonTip(3000, "Fluxion",
                            "已收进托盘，游戏联动、硬件告警、帧生成诊断仍在后台运行。\r\n右键托盘图标可重新打开或退出。",
                            ToolTipIcon.Info);
                }
            };
        }

        // 关闭确认框：返回值 false = 用户取消（窗口不关）。
        // toTray / remember 通过 out 回传，正文里那句"稍后可在设置中修改此行为"对应的是
        // 自定义优化项里的「关闭窗口时询问」开关（ui.closeAsk）。
        bool AskCloseAction(out bool toTray, out bool remember)
        {
            toTray = Cfg.CloseToTray; remember = false;
            var dlg = new Form();
            dlg.Text = "关闭窗口";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.ShowIcon = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.ClientSize = new Size(Theme.S(420), Theme.S(268));
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var sec = new Sec("关闭窗口");
            sec.Body(Program.ShareBuild
                ? "帧生成诊断与运行模式确认需要程序保持后台运行。"
                : "游戏联动、硬件告警、帧生成诊断需要程序保持后台运行。");
            var rbTray = new RoundRadio(); Theme.StyleRadio(rbTray, "最小化到通知区域图标（后台继续运行）");
            var rbExit = new RoundRadio();
            Theme.StyleRadio(rbExit, Program.ShareBuild
                ? "退出程序"
                : "退出程序（联动与告警一并停止）");
            rbTray.Checked = toTray; rbExit.Checked = !toTray;
            sec.Pair(null, rbTray, 0, null, null, 0);
            sec.Pair(null, rbExit, 0, null, null, 0);
            var cbRemember = new RoundCheck();
            Theme.StyleCheck(cbRemember, "记住我的选择，不再询问");
            sec.Pair(null, cbRemember, 0, null, null, 0);
            sec.Body("不勾选则每次都问；勾选后不再询问，想恢复询问请到「设置 → 窗口与托盘」把「关闭窗口时」选回第一项。");
            pg.Add(sec);
            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnOk = new FlatBtn(); btnOk.Text = "确认"; btnOk.Kind = BtnKind.Primary; btnOk.Width = Theme.S(84);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnOk.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnOk);
            dlg.Controls.Add(bar);
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;

            pg.Reflow();
            var r = dlg.ShowDialog(this);
            bool ok = r == DialogResult.OK;
            if (ok) { toTray = rbTray.Checked; remember = cbRemember.Checked; }
            try { dlg.Dispose(); } catch { }
            return ok;
        }

        // ---------------------- 日志 ----------------------
        public void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            try
            {
                if (log != null && log.InvokeRequired) log.Invoke((Action)(delegate { AppendLog(line); }));
                else AppendLog(line);
            }
            catch { }
            // ⚠ lblFoot 是控件，而 Log() 会被**后台线程**调用（硬件告警 CheckAlarms、看门狗）。
            //   直接写 .Text 等于跨线程碰控件（会走 SendMessage 同步进 UI 线程）：UI 线程忙时
            //   反倒把后台线程一起拖住。统一编组到 UI 线程，且用 BeginInvoke（不阻塞调用方）。
            try
            {
                if (lblFoot != null && !lblFoot.IsDisposed)
                {
                    string t = msg.Length > 110 ? msg.Substring(0, 110) : msg;
                    if (lblFoot.InvokeRequired)
                        lblFoot.BeginInvoke((Action)(delegate { try { if (!lblFoot.IsDisposed) lblFoot.Text = t; } catch { } }));
                    else lblFoot.Text = t;
                }
            }
            catch { }
            Program.Log(msg);
        }

        void AppendLog(string line)
        {
            if (log == null || log.IsDisposed) return;
            if (logVirgin) { logVirgin = false; log.Clear(); log.ForeColor = Theme.Text; }
            log.AppendText(line + "\r\n");
            if (log.Lines.Length > 500)
            {
                int keepFrom = log.Lines.Length - 350;
                int firstChar = log.GetFirstCharIndexFromLine(keepFrom);
                if (firstChar > 0) { log.Select(0, firstChar); log.SelectedText = ""; }
            }
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }

        // ---------------------- 改尺寸后的整棵重绘 ----------------------
        //  用户截图：「全屏时」内容区顶部多出一条**被压扁的卡片行**。
        //  实测复现（探针 capture 模式，GetDC+BitBlt 读窗口表面）：1180x1000 → 1744x537 之后，
        //  卡片网格在被压扁的中间态位置画过一帧，**那一块之后再没被失效**，于是留在表面上。
        //  根因是这套自绘控件族（Pg / Sec / CardGrid / GameCard 全是 Opaque + UserPaint +
        //  AllPaintingInWmPaint）**父容器不擦背景**，孩子们移动时留下的旧像素没人负责擦掉；
        //  而 WinForms 的失效区在连续 resize / 最大化那一帧会被系统重绘吞掉。
        //  解法很土但准确：等这一轮布局彻底落定，整棵重绘一次并**立刻**刷出去。
        bool resizeRepaintQueued;

        // 当前可见的页面（布局自检与重绘都要用到）
        Pg ActivePg()
        {
            if (pages == null) return null;
            for (int i = 0; i < pages.Length; i++)
                if (pages[i] != null && pages[i].Visible) return pages[i];
            return null;
        }

        // 布局自检：只在几何异常时写日志（供"本机复现不出来"的问题在真机上留证）
        void CheckLayoutHealth(string why)
        {
            try
            {
                Pg pg = ActivePg();
                if (pg == null) return;
                CardGrid grid = null;
                int searchTop = int.MaxValue;
                foreach (Control c in DeepControls(pg))
                {
                    if (c is CardGrid && grid == null) grid = (CardGrid)c;
                    if (searchTop == int.MaxValue && c is RoundField)
                        searchTop = pg.PointToClient(c.PointToScreen(Point.Empty)).Y;
                }
                if (grid == null) return;
                Point gp = pg.PointToClient(grid.PointToScreen(Point.Empty));
                int above = 0;
                foreach (Control c in DeepControls(grid))
                {
                    if (!(c is GameCard)) continue;
                    if (pg.PointToClient(c.PointToScreen(Point.Empty)).Y < gp.Y - 2) above++;
                }
                if (gp.Y < Theme.S(150) || grid.Height < Theme.S(120) || above > 0)
                    Log("[布局自检] " + why + "：窗口 " + ClientSize + " Pg=" + pg.ClientSize
                        + " 网格 PgY=" + gp.Y + " 高=" + grid.Height + " 卡片=" + grid.Controls.Count
                        + " 越位卡片=" + above + " 搜索框Y=" + searchTop + " 内容高=" + pg.ContentHeightPublic()
                        + " ScrollY=" + pg.ScrollY);
            }
            catch { }
        }

        static List<Control> DeepControls(Control root)
        {
            List<Control> list = new List<Control>();
            foreach (Control c in root.Controls)
            {
                list.Add(c);
                list.AddRange(DeepControls(c));
            }
            return list;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (resizeRepaintQueued || !IsHandleCreated) return;
            resizeRepaintQueued = true;
            try
            {
                // 两遍：第一遍让布局收敛并立刻刷出去；第二遍排到下一轮消息之后再来一次 ——
                //  因为"布局落定"可能比这次重绘更晚（2.10.1 只做了一遍，所以在真机上仍会残留）。
                BeginInvoke((Action)(delegate
                {
                    try
                    {
                        Pg pg = ActivePg();
                        if (pg != null) pg.Reflow();
                        Invalidate(true); Update();
                    }
                    catch { }
                    try
                    {
                        BeginInvoke((Action)(delegate
                        {
                            resizeRepaintQueued = false;
                            try
                            {
                                Pg pg = ActivePg();
                                if (pg != null) pg.Reflow();
                                Invalidate(true); Update();
                                CheckLayoutHealth("尺寸变化后");
                            }
                            catch { }
                        }));
                    }
                    catch { resizeRepaintQueued = false; }
                }));
            }
            catch { resizeRepaintQueued = false; }
        }

        // ---------------------- 拖拽降载（拖窗口边框时的计时器让路） ----------------------
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ENTERSIZEMOVE)
            {
                if (sysTimer != null) sysTimer.Stop();
                if (linkTimer != null) linkTimer.Stop();
                if (logTimer != null) logTimer.Stop();
            }
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                if (sysTimer != null) sysTimer.Start();
                if (linkTimer != null) linkTimer.Start();
                if (logTimer != null) logTimer.Start();
            }
            else if (m.Msg == WM_DROPFILES)
            {
                // 自己 DragFinish 掉了，处理完就别再往下传（系统会再释放一次句柄）
                try { OnPackDropped(m.WParam); } catch (Exception ex) { Program.Log("Drop: " + ex.Message); }
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------------- 插件包拖入 ----------------------
        //  用 Win32 的 DragAcceptFiles 而不是 WinForms 的 AllowDrop：
        //  AllowDrop 是 OLE 拖放，drop 目标只挂在显式设了 AllowDrop=true 的那个控件上，
        //  子控件不会向上冒泡 —— 这个窗口有几十个 Panel / ListView，逐个挂既啰嗦，
        //  又会被「换主题时整棵重建」漏掉几个。DragAcceptFiles 设的是窗口的
        //  WS_EX_ACCEPTFILES 扩展样式，鼠标下方那个控件自己没注册时，系统会自动向上
        //  找到注册过的窗口 —— 在 Form 上注册一次，整窗生效，且重建子控件不受影响。
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void DragFinish(IntPtr hDrop);

        bool packBusy;

        void OnPackDropped(IntPtr hDrop)
        {
            List<string> paths = new List<string>();
            try
            {
                uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    StringBuilder sb = new StringBuilder((int)len + 2);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    if (sb.Length > 0) paths.Add(sb.ToString());
                }
            }
            finally { DragFinish(hDrop); }
            if (paths.Count == 0) return;
            StartPackImport(paths);
        }

        // 解压 + 识别放后台线程：一个 0.3.x 的包解压出来约 250 MB，扔在 UI 线程上会白屏两秒。
        void StartPackImport(List<string> paths)
        {
            if (packBusy) { Log("  上一次插件包导入还没结束，请稍候再拖"); return; }
            packBusy = true;
            Log(">>> 拖入 " + paths.Count + " 项，开始识别插件包");
            foreach (string p in paths) Log("    " + p);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<PackProbe> probes = new List<PackProbe>();
                foreach (string p in paths)
                {
                    try { probes.Add(Pack.Inspect(p, null)); }
                    catch (Exception ex)
                    {
                        PackProbe bad = new PackProbe();
                        bad.Input = p;
                        bad.Error = ex.GetType().Name + "：" + ex.Message;
                        probes.Add(bad);
                    }
                }
                try { Invoke((Action)(delegate { ShowPackDialog(probes); })); }
                catch { packBusy = false; }
            });
        }

        // 签名核验门（分级）：白名单命中 = 直接放行，不打扰；未签名 / 非白名单证书 = 单独弹一次确认。
        //  返回 false = 用户拒绝，调用方必须中止本次动作。
        //  ⚠ 弹框只在这一层做 —— 库层（Pack / Dlssg030）保持无交互，否则 headless 探针会卡死。
        bool TrustGate(List<string> bad, string what)
        {
            if (bad == null || bad.Count == 0) return true;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("这次要用的代理没通过签名核验：");
            foreach (string s in bad) sb.AppendLine("  " + s);
            sb.AppendLine();
            sb.AppendLine("上游作者发布的 0.3.x 代理带一张固定证书（85BA6676…）。上面这些都不是它 ——");
            sb.AppendLine("可能是你自己换过的入口、别人的修改版，或者来路不明的包。");
            sb.AppendLine("工具不替你判断来源，但装进游戏目录之后，它就是游戏进程的一部分。");
            sb.AppendLine();
            sb.AppendLine("确认继续吗？");
            return MessageBox.Show(sb.ToString(), "签名核验未通过 · " + what,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        // 部署前置：对"即将写进游戏目录的那个源文件"核验一次
        bool TrustGateFile(string path, string what)
        {
            if (path == null || path.Length == 0) return true;   // 取件算不出来 → 交给原有报错路径
            CertInfo c = ProxySign.Of(path);
            if (c.Ok) return true;
            List<string> bad = new List<string>();
            bad.Add(Path.GetFileName(path) + " — " + ProxySign.Short(c));
            bad.Add(Path.GetDirectoryName(path));
            return TrustGate(bad, what);
        }

        void ShowPackDialog(List<PackProbe> probes)
        {
            // 交互决策（识别报告/签名确认/要不要重抄）先在 UI 线程做完并收集成任务表，
            // 最重的归位+重抄整段投进后台执行：Import 要备份+复制约 250MB 再逐件 SHA256、
            // DeployTo 逐游戏重抄，此前同步压在 UI 线程，导入期间整窗白屏无响应（2026-09-30 体检）。
            var jobs = new List<object[]>();   // { PackProbe, int 模式(0=只归位 1=归位+重抄), List<DlssgGame> targets }
            foreach (PackProbe p in probes)
            {
                Log("--- 识别 ————————————————————————————");
                foreach (string ln in p.Report().Split('\n')) Log("  " + ln.TrimEnd());

                if (p.Error.Length > 0) { Log("  → 未做任何改动"); continue; }
                if (p.Kind == Pack.K_OPTI || p.Kind == Pack.K_LEGACY || p.Kind == Pack.K_UNK)
                {
                    Log("  → 这套不在拖入导入的范围内（见上），未做任何改动");
                    continue;
                }
                if (!p.Ready) { Log("  → 包里没有可归位的文件，未做任何改动"); continue; }

                List<PackItem> untrusted = Pack.NeedsTrustAsk(p);
                if (untrusted.Count > 0)
                {
                    List<string> badList = new List<string>();
                    foreach (PackItem it in untrusted)
                        badList.Add(it.Name + " — " + ProxySign.Short(it.Cert));
                    if (!TrustGate(badList, Path.GetFileName(p.Input)))
                    {
                        Log("  → 签名核验未通过，已取消，未做任何改动");
                        continue;
                    }
                    Log("  签名核验：确认了 " + untrusted.Count + " 个非白名单入口，继续导入");
                }

                List<DlssgGame> targets = Pack.Installed(games);
                int nCommon = p.Count("entry") + p.Count("ini") + p.Count("runtime");
                string ask =
                    "类型：" + p.KindLabel() + "\n"
                    + (p.VerBasis.Length > 0 ? "判据：" + p.VerBasis + "\n" : "")
                    + (p.Ver.Length > 0 ? "包名版本：" + p.Ver + "（仅推测，不作判据）\n" : "")
                    + "\n归位：" + nCommon + " 件进 common" + (p.Count("alt") > 0 ? "，" + p.Count("alt") + " 件进 alts" : "")
                    + "\n目标：" + Dlssg030.PackRoot
                    + "\n\n已装本方案的游戏（" + targets.Count + "）："
                    + (targets.Count > 0 ? GameNames(targets) : "无")
                    + "\n\n【是】更新资源包，并重抄到上面这些游戏\n"
                    + "【否】只更新资源包，游戏等你自己去点\n"
                    + "【取消】什么都不做";
                DialogResult r = MessageBox.Show(ask, "插件包导入 · " + Path.GetFileName(p.Input),
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { Log("  → 已取消"); continue; }
                jobs.Add(new object[] { p, r == DialogResult.Yes ? 1 : 0, targets });
            }
            if (jobs.Count == 0) { packBusy = false; return; }

            packBusy = true;   // 归位+重抄期间不许再拖新包（此前在弹框前就放开了）
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int imported = 0;
                foreach (object[] job in jobs)
                {
                    PackProbe p = (PackProbe)job[0];
                    int mode = (int)job[1];
                    List<DlssgGame> targets = (List<DlssgGame>)job[2];
                    try
                    {
                        Log(">>> 归位到资源包：" + Path.GetFileName(p.Input));
                        Log("  " + Pack.Import(p, delegate(string s) { Log(s); }));
                        imported++;
                        try
                        {
                            if (p.TempDir.Length > 0 && Directory.Exists(p.TempDir))
                            { Directory.Delete(p.TempDir, true); Log("  已清理解压临时目录"); }
                        }
                        catch { }

                        if (mode == 1)
                        {
                            Log(">>> 重新部署（强制重抄资源包里的新文件）");
                            Log("  " + Pack.DeployTo(targets, delegate(string s) { Log(s); }));
                        }
                    }
                    catch (Exception ex) { Log("  导入执行异常：" + ex.Message); }
                }
                bool any = imported > 0;
                try { Invoke((Action)(delegate { packBusy = false; if (any) { FillGameList(); RefreshFgSummary(); } })); }
                catch { packBusy = false; }
            });
        }

        static string GameNames(List<DlssgGame> gs)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DlssgGame g in gs)
            {
                if (sb.Length > 0) sb.Append("、");
                sb.Append(g.Title);
            }
            return sb.ToString();
        }

        // ---------------------- 档位下拉同步 ----------------------
        void SyncProfileCombo()
        {
            if (cbProfile == null || cbProfileGame == null) return;
            string game = cbProfileGame.SelectedItem != null ? cbProfileGame.SelectedItem.ToString() : null;
            updatingProfile = true;
            string ov = game != null ? Program.GetProfileOverride(game) : null;
            int sel = ov == "fps" ? 1 : ov == "mmo" ? 2 : ov == "aaa" ? 3 : ov == "gacha" ? 4 : 0;
            if (cbProfile.SelectedIndex != sel) cbProfile.SelectedIndex = sel;
            updatingProfile = false;
        }

        void SyncProfileEnabled()
        {
            if (cbProfile == null || cbProfileGame == null || cbManual == null) return;
            bool en = cbManual.Checked && !Program.SceneOffice;
            cbProfileGame.Enabled = en;
            cbProfile.Enabled = en;
            cbManual.Enabled = !Program.SceneOffice;
        }


        // ---------------------- 取值细调（v3.6.0） ----------------------
        // 这个对话框存在的理由：一键优化只能给出"程序认为的最优值"，而像着色器缓存上限（12 档）、
        // DLSS 强制预设字母、量子长度这些，最优值本来就因游戏、因驱动版本、因个人偏好而异。
        // 所以凡是「能给出具体操作」的项，就把档位摊开让用户自己选 —— 只读核验项不在这里
        //（HPET / GPU 中断绑核 / rBAR 全局强开 改错会让设备消失，工具只做核验、不代改）。
        void OpenKnobForRow()
        {
            if (lvOpt == null || lvOpt.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "请先在下面的列表里点选一行（带 ⚙ 的行可以调）。", "取值细调",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sel = lvOpt.SelectedItems[0];
            string key = sel.Tag as string;
            if (string.IsNullOrEmpty(key))
            {
                string exp = sel.SubItems.Count > 3 ? sel.SubItems[3].Text : "";
                MessageBox.Show(this, "「" + sel.Text.Replace("  ⚙", "") + "」没有可调档位，本工具不代改。\n\n"
                    + (string.IsNullOrEmpty(exp) ? "" : "这一项自己的说明：\n" + exp + "\n\n")
                    + "只有两类会这样：\n"
                    + "· 没有「更优的档位」可选 —— 默认即最优（如异类策略、核心拓扑）；\n"
                    + "· 改错的代价是设备消失 / 系统起不来 —— HPET 强制、GPU 中断绑核、MSI 强制转换、rBAR 全局强开。\n\n"
                    + "状态若是灰色的「待确认」，那不是缺陷（可选 / 不适用 / 读不到），不用处理。",
                    "取值细调", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowKnobDialog(key);
        }

        void ShowKnobDialog(string focusKey)
        {
            var knobs = Program.Knobs();
            if (knobs.Count == 0) { MessageBox.Show(this, "没有可调项。", "取值细调"); return; }

            var dlg = new Form();
            dlg.Text = "取值细调（保存后立即写入系统）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(540), Theme.S(420));
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(640));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var combos = new List<RoundCombo>();
            string lastGroup = null;
            RoundCombo focusCb = null;
            foreach (var k in knobs)
            {
                Sec sec;
                if (k.Group != lastGroup)
                {
                    sec = new Sec(k.Group);
                    pg.Add(sec);
                    lastGroup = k.Group;
                }
                else sec = (Sec)pg.Controls[pg.Controls.Count - 1];

                var cb = new RoundCombo();
                cb.Tag = k;
                cb.Width = Theme.S(340);
                for (int i = 0; i < k.Labels.Length; i++) cb.Items.Add(k.Labels[i]);
                if (k.Index >= 0) cb.SelectedIndex = k.Index;
                else
                {
                    // 当前值不在档位表里（例如别处写过一个工具不提供的值）。
                    // 必须预选最后那条"不在档位表中"，否则用户只是打开对话框再点保存，
                    // 就会被静默改成第 0 档 —— 这是"我只想看看"变成"它自己改了"的经典事故。
                    cb.Items.Add("（当前值 " + k.Value + "，不在档位表中 —— 不动它）");
                    cb.SelectedIndex = k.Labels.Length;
                }

                string title = k.Title + (k.Restart ? "（需重启）" : "");
                sec.Row(title, cb);
                if (!string.IsNullOrEmpty(k.Hint)) sec.Body(k.Hint);
                combos.Add(cb);
                if (focusKey != null && k.Key == focusKey) focusCb = cb;
            }

            var sNote = new Sec("怎么用 / 边界");
            sNote.Body("· 每一项都对应 config.json 里的一个键：保存时先写回文件，再写入系统，所以重启程序后仍是你选的档位。\\n"
                     + "· 标「需重启」的是 Windows 注册表项（HAGS / 量子长度），改完当次不生效。\\n"
                     + "· 「NVIDIA 驱动 ·」开头的写在 nvdrsdb 里，重启游戏后生效；同一类只写一次，不会反复刷新驱动库。\\n"
                     + "· 这里只放「改坏了能改回来」的项。HPET 强制、GPU 中断绑核、MSI 强制转换、rBAR 全局强开\n"
                     + "   属于「改错会让设备消失 / 系统起不来」的类别，多来源明确警告，本工具只做只读核验。");
            pg.Add(sNote);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存并应用"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(104);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            pg.Reflow();
            if (focusCb != null)
            {
                // 双击体检表进来时，把焦点落在那一项上（用户点的就是它，不该让他自己找）
                try { dlg.Shown += delegate { focusCb.Focus(); }; } catch { }
            }

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var changed = new List<OptKnob>();
            int failed = 0;
            foreach (var cb in combos)
            {
                var k = (OptKnob)cb.Tag;
                int idx = cb.SelectedIndex;
                if (idx < 0 || idx >= k.Values.Length) continue;   // 选中"不在档位表中"那一项 = 不动
                int want = k.Values[idx];
                if (want == k.Value) continue;
                if (Program.SetKnob(k, want)) changed.Add(k);
                else failed++;
            }

            // 同一类只应用一次：改 4 个驱动项不该写 4 遍 nvdrsdb
            var kinds = new List<string>();
            foreach (var k in changed) if (!kinds.Contains(k.Apply)) kinds.Add(k.Apply);
            foreach (var kind in kinds)
            {
                OptKnob probe = changed.Find(delegate(OptKnob x) { return x.Apply == kind; });
                string r = Program.ApplyKnob(probe);
                if (!string.IsNullOrEmpty(r)) Log("[取值细调 · " + kind + "] " + r);
            }

            Cfg = Config.Load(Program.ConfigPath);
            Program.ReloadCfg(Cfg);
            Log("取值细调已保存：改动 " + changed.Count + " 项，应用到系统 " + kinds.Count + " 组"
                + (failed > 0 ? ("，写配置失败 " + failed + " 项") : ""));
            RefreshGridAsync();
        }

        // ---------------------- 自定义优化项 ----------------------
        void ShowCustomDialog()
        {
            var dlg = new Form();
            dlg.Text = "自定义优化项（保存后立即生效）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(460), Theme.S(420));
            dlg.ClientSize = new Size(Theme.S(470), Theme.S(560));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var groups = new object[][]
            {
                new object[] { "电源计划优化", "power", "enable", Cfg.PowerEnable },
                new object[] { "游戏期电源联动（进游戏切高性能）", "power", "gameAwareSwitch", Cfg.PowerGameSwitch },
                new object[] { "GPU/系统（关闭 GameDVR）", "gpu", "enable", Cfg.GpuEnable },
                new object[] { "NVIDIA 驱动配置自动化", "gpu", "nvProfiles", Cfg.NvProfiles },
                new object[] { "Windows 游戏模式", "gpu", "gameModeOn", Cfg.GpuGameMode },
                new object[] { "CPU 调度（优先级分离 + 异类线程策略）", "scheduler", "enable", Cfg.SchedEnable },
                new object[] { "网络+网卡优化（Nagle / 节能）", "network", "enable", Cfg.NetEnable },
                new object[] { "服务优化（SysMain）", "services", "enable", Cfg.SvcEnable },
                new object[] { "输入（关闭鼠标加速）", "input", "enable", Cfg.InputEnable },
                new object[] { "内存（驱动不换页）", "memory", "enable", Cfg.MemEnable },
                new object[] { "Defender 游戏进程排除（安全权衡项）", "defender", "enable", Cfg.DefenderExclude },
                new object[] { "桌面拖拽流畅（关透明/动画）", "dragFix", "enable", Cfg.DragEnable },
                new object[] { "游戏加速包（挂后台/内存整理/停更新）", "gameBoost", "enable", Cfg.GbEnable },
                new object[] { "定时器联动（0.5ms，仅 FPS 档）", "timer", "enable", Cfg.TimerEnable },
                new object[] { "关闭系统虚拟化（WSL2 / Docker / 安卓模拟器将不可用）", "virtualization", "enable", Cfg.VirtDisableEnable },
                new object[] { "DLSS 模型覆盖（30 系可用 Transformer 超分，默认关）", "dlss", "override", Cfg.DlssOverride },
                new object[] { "内核隔离 VBS 检测（只检测不修改）", "vbs", "checkOnly", true },
                new object[] { "帧生成模块（DLSSG）", "dlssg", "enable", Cfg.DlssgEnable },
            };

            // 用同一个光标式分区承载，所以勾选框再多也不会互相压字（分区自己长高，外层滚动）
            var pg = new Pg();
            pg.Dock = DockStyle.Fill;
            var sec = new Sec("勾选需要生效的优化项");
            var boxes = new List<CheckBox>();
            foreach (var g in groups)
            {
                var cb = new RoundCheck();
                Theme.StyleCheck(cb, (string)g[0]);
                cb.Tag = g;
                cb.Checked = (bool)g[3];
                sec.Pair(null, cb, 0, null, null, 0);
                boxes.Add(cb);
            }
            sec.Body("未勾选的项不会被执行；已执行的项可在「恢复备份」里整体还原。");
            pg.Add(sec);
            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(84);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            // 这个对话框管"开不开"，具体取值（档位）在另一个对话框里。两件事分开：
            // 开关是二选一，档位是一组取舍，混在一起用户会以为勾上就用的是他想要的档。
            var btnKnobs = new FlatBtn(); btnKnobs.Text = "取值细调…"; btnKnobs.Width = Theme.S(104);
            btnKnobs.Click += delegate { ShowKnobDialog(null); };
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            bar.Controls.Add(btnKnobs);
            dlg.Controls.Add(bar);

            pg.Reflow();
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // 虚拟化是唯一「勾上就废掉一整类功能」的选项，落盘前单独确认一次。
                // 其余选项最坏的后果是"没效果"，只有这一项会让用户第二天发现模拟器打不开。
                foreach (var cb in boxes)
                {
                    var g0 = (object[])cb.Tag;
                    if ((string)g0[1] != "virtualization") continue;
                    if (cb.Checked && !(bool)g0[3])
                    {
                        string msg = "关闭系统虚拟化后，以下功能会全部不可用：\n\n"
                                   + "    WSL2（含里面已装的 Linux 发行版）\n"
                                   + "    Docker Desktop\n"
                                   + "    Android 模拟器（Android Studio / MuMu / 夜神等）\n"
                                   + "    Windows 沙盒\n\n"
                                   + "生效方式只改 BCD 里的一条 hypervisorlaunchtype，不动 Windows 功能，"
                                   + "所以用「恢复备份」还原后 WSL2 / Docker 不需要重装。\n\n"
                                   + "需要重启才生效。确定要关闭吗？";
                        if (MessageBox.Show(this, msg, "关闭系统虚拟化", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                            cb.Checked = false;
                    }
                }
                int changed = 0;
                foreach (var cb in boxes)
                {
                    var g = (object[])cb.Tag;
                    string secName = (string)g[1], key = (string)g[2];
                    bool want = cb.Checked, old = (bool)g[3];
                    if (want != old && Program.SetConfigBool(secName, key, want)) changed++;
                }
                Cfg = Config.Load(Program.ConfigPath);
                Program.ReloadCfg(Cfg);
                Log("自定义优化已保存：更新 " + changed + " 项并即时生效");
                RefreshGridAsync();
            }
        }
    }
}
