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
            try { brushCache.Clear(); } catch { }
            try { penCache.Clear(); } catch { }
        }
        public static bool Dark { get { return Mode == 1; } }
        static Color L(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static Color Pk(Color light, Color dark) { return Mode == 1 ? dark : light; }

        //  ==================================================================
        //  2026-10-21 全局改版：参考 Apple 的设计语言（macOS 克制版）
        //  ------------------------------------------------------------------
        //  取色的来源是 Apple 的系统语义色，不是"看起来像苹果"的随手近似：
        //   · 底色 systemGroupedBackground  #F2F2F7 / 卡片 secondarySystemGroupedBackground #FFFFFF
        //   · 正文 label #1D1D1F、次级 secondaryLabel #6E6E73、三级 tertiaryLabel #8E8E93
        //   · 分隔线 rgba(0,0,0,0.12)（浅）/ rgba(255,255,255,0.12)（深）
        //   · 强调 / 交互色 systemBlue #007AFF · #0A84FF
        //   · 语义色 systemGreen #34C759 · #30D158、systemOrange #FF9500 · #FF9F0A、
        //     systemRed #FF3B30 · #FF453A
        //  改版的**核心决定**是把"交互色"和"状态色"拆开：
        //   以前强调绿 #10B981 同时承担按钮、勾选、选中行和"已生效"状态点 ——
        //   同一个绿在一屏里有两个意思，用户看到绿色不知道该"点"还是该"读"。
        //   Apple 的规矩正好相反：蓝色只给可点的东西，绿色只给成功/生效，
        //   语义色「used sparingly and exclusively for system feedback」。
        //  实测数据也支持这一步：白字压绿（#34C759）对比度只有 2.2:1，压蓝（#007AFF）是 4.0:1，
        //   主按钮（白字）放在蓝色上是唯一两个都过关的选择。
        //   品牌标识（DrawBrandLogo）跟着 Accent 走，所以徽标会一起变蓝 —— 单强调色才是 Apple。
        //  ⚠ 这一版把所有偏蓝的 slate 灰（#0F172A / #475569 / #94A3B8）换成了中性灰。
        //    tailwind 的 slate 是"冷蓝灰"，和蓝强调色叠在一起整屏发青；Apple 的灰是中性的。
        //  ==================================================================
        //  浅色底从 #F5F7FA 压到 #EDF1F6：卡片仍是纯白，但**对比拉开了**，
        //  白卡才真的"浮"在底上（原来两者只差 4 级，整页糊成一片白）。
        //  白卡与底色保持 8~10% 明度差（规范给的是 10~15%）。
        public static Color Bg { get { return Pk(L(242, 242, 247), L(20, 20, 22)); } }           // systemGroupedBackground #F2F2F7 / #141416
        public static Color Panel { get { return Pk(Color.White, L(31, 31, 34)); } }            // 卡片：#FFFFFF / #1F1F22（深色下"抬一级"= 更亮，不是更暗）
        public static Color Subtle { get { return Pk(L(250, 250, 252), L(26, 26, 29)); } }      // 次级底：#FAFAFC / #1A1A1D
        //  侧栏材质（2026-10-21）：macOS 的左栏是比内容区**再深一档**的"铁道"，
        //  不是和内容区同色的白板。三层明度关系：侧栏 #F0F0F3 < 内容底 #F2F2F7 < 卡片 #FFFFFF。
        //  深色反过来是"抬升"：侧栏 #1A1A1D 比底 #141416 亮，比卡片 #1F1F22 暗。
        public static Color Sidebar { get { return Pk(L(235, 235, 239), L(26, 26, 29)); } }
        public static Color Line { get { return Pk(L(229, 229, 234), L(53, 53, 58)); } }        // separator：systemGray5 #E5E5EA / #35353A
        public static Color LineSoft { get { return Pk(L(240, 240, 243), L(42, 42, 46)); } }    // 更淡的分隔线 #F0F0F3 / #2A2A2E
        //  悬停底（第 161 轮修语义）：浅色原来是 #F5F5F7 —— 比 Bg(#F2F2F7) 还**亮 3 个点**，
        //  画在 Bg 底上的 hover（侧栏导航 / 分段滑块 / 侧栏游戏行）全部隐形，鼠标扫过去毫无反馈。
        //  对齐第 153 轮 WinBtn 的做法：比底色明显深一档（≈7% 墨，#E3E3E8 附近）；画在白 Panel 上
        //  也顺带从"几乎看不见"变成正常浅灰。深色原本就可见，保持不动。
        public static Color Hover { get { return Pk(Mix(Bg, Text, 0.07), L(42, 42, 46)); } }
        public static Color Accent { get { return Pk(L(0, 122, 255), L(10, 132, 255)); } }      // systemBlue #007AFF / #0A84FF
        public static Color Accent2 { get { return Pk(L(88, 86, 214), L(94, 92, 230)); } }      // systemIndigo #5856D6 / #5E5CE6（渐变副色）
        public static Color Cyan { get { return Pk(L(50, 173, 230), L(100, 210, 255)); } }      // systemTeal #32ADE6 / #64D2FF
        public static Color AccentSoft { get { return Pk(L(224, 239, 255), L(26, 43, 66)); } }  // accent-soft：12% systemBlue 压在卡片底色上
        // ---- 控件级派生色：以前散落在各绘制函数里的硬编码值，收进 Theme 以便换配色时一处生效。
        //      取值与原硬编码**逐位相同**（零视觉变化），只是不再散在外面。
        public static Color OkFill { get { return Pk(L(231, 249, 238), L(22, 48, 31)); } }    // 成功态按钮底：浅色=12% 绿压白，深色=更深的墨绿
        public static Color ErrFill { get { return Pk(L(255, 231, 230), L(53, 25, 26)); } }   // 危险态按钮底
        public static Color CheckOn { get { return Pk(L(234, 243, 255), L(20, 40, 63)); } }  // 勾选圆底：浅色=极淡蓝
        public static Color GridHead { get { return Pk(L(247, 247, 250), L(35, 35, 39)); } } // ListView 表头底
        public static Color GridAlt { get { return Pk(L(250, 250, 252), L(27, 27, 30)); } }  // ListView 斑马纹底
        public static Color Ok { get { return Pk(L(52, 199, 89), L(48, 209, 88)); } }          // systemGreen #34C759 / #30D158
        public static Color Warn { get { return Pk(L(255, 149, 0), L(255, 159, 10)); } }        // systemOrange #FF9500 / #FF9F0A
        public static Color Err { get { return Pk(L(255, 59, 48), L(255, 69, 58)); } }          // systemRed #FF3B30 / #FF453A
        public static Color Text { get { return Pk(L(29, 29, 31), L(245, 245, 247)); } }        // label #1D1D1F / #F5F5F7
        public static Color TextDim { get { return Pk(L(110, 110, 115), L(161, 161, 166)); } }  // secondaryLabel #6E6E73 / #A1A1A6
        public static Color TextFaint { get { return Pk(L(118, 118, 123), L(142, 142, 147)); } } // tertiary 图标/装饰用（浅色 #76767B：Bg 上 4.05:1 ≥ 图形 3:1；深色同值）
        public static Color TextDisabled { get { return Pk(L(142, 142, 147), L(110, 110, 115)); } } // 禁用态文字（WCAG 豁免，保持弱化观感）
        // 浅色下输入底用极浅灰
        public static Color Field { get { return Pk(L(242, 242, 247), L(26, 26, 29)); } }       // #F2F2F7 / #1A1A1D
        //  输入类控件的填充色要跟着**周围底色**翻转（2026-10-22 统一底色后）：
        //  卡片（白）里用 Field（浅灰）划出输入区；灰底/侧栏上用 Panel（白）才看得见 ——
        //  否则 Field 与页面底色完全相同，搜索框直接"蒸发"。
        public static Color FieldOn(Color surround)
        {
            //  卡片白里：用 Field（浅灰）划出输入区；
            //  灰底上：**不要白块**（白框贴在灰底上，边缘就是一条硬边 —— 用户说的"周边有瑕疵"），
            //  改成比背景深 4 级，几乎融进底色，靠图标和占位文字说明它是个输入框。
            if (surround == Panel) return Field;
            return Mix(surround, Text, Dark ? 0.05 : 0.035);
        }
        public static Color FieldEdge { get { return Pk(L(210, 210, 215), L(58, 58, 63)); } }   // #D2D2D7 / #3A3A3F
        public static Color Selected { get { return Pk(L(224, 239, 255), L(20, 40, 63)); } }
        public static Color OnAccent { get { return Color.White; } }
        public static Color ShadowC { get { return Pk(L(0, 0, 0), L(0, 0, 0)); } }              // Apple 的投影是中性黑，不是蓝灰
        public static Color Golden { get { return Pk(L(255, 149, 0), L(255, 214, 10)); } }     // #FF9500 / #FFD60A
        //  ★ 语义色有两副面孔（2026-10-21）：填充用鲜艳版，文字用加深版。
        //  Apple 自己就是这么分的（浅色下绿色文字是 #248A3D，不是 #34C759）：systemGreen 在白底上
        //  只有 2.2:1、systemOrange 只有 2.0:1 —— 当色块很好看，当文字就是"发虚的花色"，读不清。
        //  深色主题反过来了：底色够深，鲜艳版本身就有 7:1 以上，不需要换。
        public static Color OkText { get { return Pk(L(36, 138, 61), L(48, 209, 88)); } }       // #248A3D / #30D158
        public static Color WarnText { get { return Pk(L(178, 80, 0), L(255, 159, 10)); } }     // #B25000 / #FF9F0A
        public static Color ErrText { get { return Pk(L(215, 0, 21), L(255, 69, 58)); } }       // #D70015 / #FF453A
        public static Color AccentText { get { return Pk(L(0, 90, 204), L(10, 132, 255)); } }   // #005ACC / #0A84FF
        //  调用点统一走这里：传进去的仍是 Theme.Ok 这种"填充色"，返回当前主题下该用于文字的那一档。
        public static Color TextSafe(Color c)
        {
            if (Dark) return c;
            if (c == Ok) return OkText;
            if (c == Warn) return WarnText;
            if (c == Err) return ErrText;
            if (c == Accent) return AccentText;
            return c;
        }
        // 卡片描边（2026-10-21）：Apple 的"Level 1 浮起"= 柔和投影 + 1px 发丝边。
        //   以前浅色刻意不画边（怕"描边+投影=廉价卡片"），那是冷灰蓝配色下的结论；
        //   换成中性灰之后，8% 的黑边几乎看不见，却能把卡片边界"钉"住，投影可以画得更淡。
        public static Color CardEdge { get { return Pk(Color.FromArgb(20, 0, 0, 0), Color.FromArgb(36, 255, 255, 255)); } }
        // 分区色相（2026-09-20）：卡片标题左的图标胶囊不再清一色绿，
        //   按分区语义分配色相，一屏 5~6 张卡片才有颜色节奏（用户："界面还是很单调"）。
        public static Color Violet { get { return Pk(L(175, 82, 222), L(191, 90, 242)); } }    // systemPurple #AF52DE / #BF5AF2
        public static Color Indigo { get { return Pk(L(88, 86, 214), L(94, 92, 230)); } }       // systemIndigo #5856D6 / #5E5CE6
        // 胶囊底 / 瓦片底：把色相以低透明度压在卡片底色上（浅色更淡、深色稍浓）
        //  胶囊底再压淡一档：浅色 26→18、深色 46→42。
        //  原来那层彩色底比标题还抢眼，整页像"图标演示"；压淡之后色相还在，但主角回到文字。
        //  图标胶囊底（2026-10-20 收色）：原来按分区色相染色，一页 4~6 张卡就是 4~6 个色块，
        //  眼睛先看到的是"彩色贴纸"而不是内容。现在胶囊底一律中性，色相只留给图标本身 ——
        //  同一屏里只剩"强调绿 + 一处语义色"，层次立刻干净。
        public static Color ChipBg(Color hue) { return Dark ? Subtle : Field; }
        public static Color TileBg(Color hue) { return Color.FromArgb(Dark ? 58 : 34, hue); }

        public static Color AccentDeep { get { return Pk(L(0, 98, 204), L(10, 111, 216)); } }  // 按下/悬停加深
        public static Color AccentLite { get { return Pk(L(77, 163, 255), L(94, 169, 255)); } }
        public static Color OkSoft { get { return Pk(L(231, 249, 238), L(22, 48, 31)); } }     // rgba(52,199,89,0.15)
        public static Color ErrSoft { get { return Pk(L(255, 231, 230), L(53, 23, 25)); } }    // rgba(255,59,48,0.15)
        public static Color WarnSoft { get { return Pk(L(255, 243, 224), L(51, 40, 15)); } }   // rgba(255,149,0,0.15)

        // ---- 圆角 token（2026-10-29 对齐「自考工作台」D:\study：卡片 16 / 控件 10）----
        //  2026-10-21 曾收到 12/8（"macOS 比 Tailwind 收"）；用户拍板以 study 为布局基准
        //  （实测 ：root --r-card:16px / --r-btn:10px），且 16 配大留白才出"纸面"感。
        //  胶囊 RChip 与封面 RPic 维持原档 —— 胶囊再圆就成药丸、封面圆角跟网格走。
        public static int RCard { get { return S(16); } }
        public static int RPanel { get { return S(16); } }
        public static int RBtn { get { return S(5); } }
        public static int RField { get { return S(6); } }
        public static int RChip { get { return S(6); } }
        public static int RPic { get { return S(10); } }


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
            using (var gp = Round(r, rad)) g.FillPath(Solid(c), gp);
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

        //  键盘焦点环（2026-10-29 第 166 轮）：2px Accent 圆角描边，画在控件内侧边缘
        //  （inset 1px，避免被控件边界裁掉）。调用判据统一是 `Focused && ShowFocusCues` ——
        //  ShowFocusCues 让鼠标点击后不显示环（只对键盘导航者有意义），与 WinForms 语义一致。
        public static void DrawFocusRing(Graphics g, Rectangle r, int rad)
        {
            var rr = new Rectangle(r.X + S(1), r.Y + S(1), r.Width - S(2), r.Height - S(2));
            using (var gp = Round(rr, Math.Max(2, rad - 1)))
            using (var p = new Pen(Accent, S(2)))
            {
                p.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                g.DrawPath(p, gp);
            }
        }

        //  ★ 压在**照片/封面**上的文字：一律灰度抗锯齿（2026-10-23，第 101 轮）
        //  ---------------------------------------------------------------------------
        //  症状：卡片标题放大 4 倍，每个字母左缘发蓝、右缘发橙 —— ClearType 是按"已知底色"
        //  做的子像素渲染，压在照片上那圈彩边就是"脏"和"廉价"的直接来源；系统默认开 ClearType
        //  是因为应用里绝大多数文字都在纯色底上（那里它是对的）。
        //  代价：GDI+ 的 DrawString 与 GDI 的 DrawText 字宽差 1px 级；这里用矩形 + 居中对齐，
        //  实测无可见位移。macOS 整个系统就是灰度抗锯齿，所以"字压图"看着干净。
        public static void DrawPhotoText(Graphics g, string s, Font f, Rectangle r, Color c,
                                         StringAlignment align = StringAlignment.Near)
        {
            if (s == null || s.Length == 0 || r.Width <= 0 || r.Height <= 0) return;
            TextRenderingHint oldHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            try
            {
                using (var sf = new StringFormat(StringFormatFlags.NoWrap))
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.Alignment = align;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags |= StringFormatFlags.NoClip;
                    using (var b = new SolidBrush(c))
                        g.DrawString(s, f, b, r, sf);
                }
            }
            finally { g.TextRenderingHint = oldHint; }
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
            //  浅色主题原来 alpha 封顶只有 9/255（≈3.5%），等于没画 —— 卡片整页"贴在纸上"，
            //  这是"廉价感"最直接的来源。浅色给到 16，深色保持 9（深色本身靠底色分层，不需要重影）。
            //  2026-10-21（Apple 化）：浅色从 16 收到 12、下移量 3 → 2 —— Apple 的 Level 1 是
            //  `0 2px 8px rgba(0,0,0,.08)` 那种"贴着纸面的一层薄雾"，重投影反而显脏。
            //  ★ 2026-10-24 第 137 轮：12 在 #F2F2F7 上几乎看不见，卡片像"印"在纸面上而不是浮着。
            //  收到 12 的前提是"卡片之间有足够留白"，那时留白不够、阴影再重就糊成一片。
            //  现在内边距/间距都放大了（24/20、卡片间距 24），阴影可以回到 17 而不显脏。
            //  浅色投影收敛为干净柔和的微投影（4/255 ≈ 1.5% 起步，累积约 5%），避免白底显脏显重
            double peak = Dark ? 6.0 : 4.0;
            int yOffset = S(Dark ? 2 : 1);
            for (int i = depth; i >= 1; i--)
            {
                int a = (int)(peak * (depth - i + 1) / depth);     // 外圈最淡
                if (a <= 0) continue;
                var rr = new Rectangle(r.X - i, r.Y - i + yOffset, r.Width + i * 2, r.Height + i * 2);
                FillRound(g, rr, rad + i, Solid(Color.FromArgb(a, ShadowC)));
            }
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
        // ---- 矢量图标绘制工具 ----
        //  ★ 2026-10-21 统一成一套「单线图标」（对齐 SF Symbols 的默认形态）：
        //    · **同一根笔画** —— 以前 1.4/1.5/1.6/1.7/1.8 各写各的，同屏并排时粗细肉眼可辨；
        //    · **默认描边、默认不填充** —— SF Symbols 的常态是 outline，填充只用于"选中态"。
        //      以前闪电是实心多边形、其余是描边，它一张卡就比旁边重一倍；
        //    · 圆头圆角（LineCap.Round + LineJoin.Round），端点不再出现方角；
        //    · 16px 光学栅格：四周留 1px，笔画的视觉包围盒尽量一致。
        //  尺寸仍由调用方给（iconRect 16px），这里只负责笔画的"同一种手感"。
        static float IconPen(Rectangle r)
        {
            float w = Math.Min(r.Width, r.Height);
            return Math.Max(1.0f, w * 0.075f);      // 16px → 1.2（161 轮实验：1.52 在 1x 下偏糊）
        }
        static Pen IconPen(Rectangle r, Color c)
        {
            var p = new Pen(c, IconPen(r));
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            return p;
        }

        public static void DrawIconSpeedometer(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f + Theme.S(1);
            using (var p = IconPen(r, c))
            {
                var arcR = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                g.DrawArc(p, arcR, 140, 260);
                g.DrawLine(p, cx, cy, cx + r.Width * 0.3f, cy - r.Height * 0.3f);
            }
            using (var b = new SolidBrush(c))
            {
                float d = IconPen(r) * 1.5f;
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            }
        }

        //  闪电：这一枚**保留实心**（其余全部改为描边），理由是 16px 下的实测：
        //   描边版的内洞只有 4px，圆角连接在尖锐顶点处堆成墨团，渲染出来像"两个菱形叠在一起"，
        //   认不出是闪电（见 ui-snapshots/_icon_sheet.png 的 16px 列）。
        //   SF Symbols 在小尺寸下也是切到 .fill 变体来保可读性的 —— 关键不是"都不填充"，
        //   而是**不用填充去抢视觉重量**：所以这里把整枚缩到 0.82，让它的墨量和其他描边图标持平。
        public static void DrawIconLightning(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = r.Width * 0.82f, h = r.Height * 0.82f;
            float x = r.X + (r.Width - w) / 2f, y = r.Y + (r.Height - h) / 2f;
            PointF[] pts = new PointF[] {
                new PointF(x + w * 0.58f, y + h * 0.02f),
                new PointF(x + w * 0.16f, y + h * 0.52f),
                new PointF(x + w * 0.46f, y + h * 0.52f),
                new PointF(x + w * 0.38f, y + h * 0.98f),
                new PointF(x + w * 0.86f, y + h * 0.44f),
                new PointF(x + w * 0.54f, y + h * 0.44f)
            };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        //  温度计（单线：管 + 底部球，球心一点液柱）。
        //  ⚠ 温度**不能**再用闪电：闪电在本项目里是"加速/超频"，而且是全套图标里唯一
        //    保留实心的那一个 —— 拿它当温度计是语义错位（2026-10-25 仪表盘截图里
        //    "GPU 温度 40°C" 左边挂着一道闪电，看起来像"在超频"）。
        public static void DrawIconThermo(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float cx = x + w * 0.5f;
            float bulbR = w * 0.21f;
            float bulbCy = y + h - bulbR - h * 0.02f;
            using (var p = IconPen(r, c))
            {
                g.DrawLine(p, cx, y + h * 0.07f, cx, bulbCy);
                g.DrawEllipse(p, cx - bulbR, bulbCy - bulbR, bulbR * 2, bulbR * 2);
            }
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx - bulbR * 0.42f, bulbCy - bulbR * 0.42f, bulbR * 0.84f, bulbR * 0.84f);
        }

        public static void DrawIconChip(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float pad = w * 0.22f;
            var core = new RectangleF(x + pad, y + pad, w - pad * 2, h - pad * 2);
            using (var p = IconPen(r, c))
            {
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
            //  中心那个小方块原来是**实心**的：16px 下它就是一块墨点，和"描边"的其余笔画对不上。
            //  SF Symbols 的 cpu 也没有内核方块 —— 直接去掉，只留封装体 + 引脚。
        }

        public static void DrawIconGamepad(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            var body = new RectangleF(x + 1, y + h * 0.2f, w - 2, h * 0.6f);
            using (var p = IconPen(r, c))
            {
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
            using (var p = IconPen(r, c))
            {
                g.DrawLines(p, pts);
            }
        }

        public static void DrawIconBook(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X + 1, y = r.Y + 2, w = r.Width - 2, h = r.Height - 4;
            using (var p = IconPen(r, c))
            {
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

        //  勾选清单（2026-10-21）：「勾选需要生效的优化项」这类"一堆开关"的分区。
        //  三条滑块而不是三个勾选框 —— 16px 的方框画 tick 会糊成一团墨点，滑块在这个尺寸下更清爽。
        public static void DrawIconSliders(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            //  行距 0.18/0.32/0.46 时 16px 上三颗钮只有 1px 间隙，糊成一条；
            //  钮也从 0.13 收到 0.105 —— 一屏里这个图标出现的次数最多（勾选清单），不能是噪点。
            float knobR = Math.Max(1.5f, h * 0.105f);
            using (var p = IconPen(r, c))
            using (var b = new SolidBrush(c))
            {
                for (int i = 0; i < 3; i++)
                {
                    float ly = y + h * (0.16f + 0.34f * i);
                    g.DrawLine(p, x, ly, x + w, ly);
                    float kx = x + w * (i == 0 ? 0.32f : (i == 1 ? 0.68f : 0.46f));
                    g.FillEllipse(b, kx - knobR, ly - knobR, knobR * 2, knobR * 2);
                }
            }
        }

        //  迁移 / 导入导出：两条反向箭头。比"文件图标"更能表达"搬来搬去"，也不依赖任何具体格式。
        public static void DrawIconSwap(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X + 1, y = r.Y + 1, w = r.Width - 2, h = r.Height - 2;
            float y1 = y + h * 0.30f, y2 = y + h * 0.70f;
            float ah = Math.Max(2.0f, h * 0.145f), aw = Math.Max(2.4f, w * 0.22f);
            using (var p = IconPen(r, c))
            {
                g.DrawLine(p, x, y1, x + w, y1);
                g.DrawLine(p, x, y2, x + w, y2);
                g.DrawLine(p, x + w - aw, y1 - ah, x + w, y1);      // 上排：指右
                g.DrawLine(p, x + w - aw, y1 + ah, x + w, y1);
                g.DrawLine(p, x + aw, y2 - ah, x, y2);              // 下排：指左
                g.DrawLine(p, x + aw, y2 + ah, x, y2);
            }
        }

        //  安全 / 备份 / 风险（2026-10-21）：盾牌轮廓。以前这一类标题（安全与还原 / 备份与回收 /
        //  风险提示 / 漂移检测）全部退化成"小方点"，和别的卡片站在一起就像漏画了。
        public static void DrawIconShield(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float ix = w * 0.08f, iy = h * 0.06f;
            PointF[] pts = new PointF[] {
                new PointF(x + w * 0.5f, y + iy),                    // 顶点
                new PointF(x + w - ix, y + h * 0.2f),
                new PointF(x + w - ix, y + h * 0.48f),
                new PointF(x + w * 0.5f, y + h - iy),                // 底尖
                new PointF(x + ix, y + h * 0.48f),
                new PointF(x + ix, y + h * 0.2f)
            };
            using (var p = IconPen(r, c)) g.DrawPolygon(p, pts);
        }

        //  下载：箭头 + 底部托盘。
        public static void DrawIconDownload(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var p = IconPen(r, c))
            {
                float mx = x + w * 0.5f, ah = h * 0.16f, aw = w * 0.16f;
                g.DrawLine(p, mx, y + h * 0.12f, mx, y + h * 0.58f);
                g.DrawLine(p, mx - aw, y + h * 0.42f, mx, y + h * 0.58f);
                g.DrawLine(p, mx + aw, y + h * 0.42f, mx, y + h * 0.58f);
                g.DrawLine(p, x + w * 0.14f, y + h * 0.8f, x + w * 0.86f, y + h * 0.8f);
                g.DrawLine(p, x + w * 0.14f, y + h * 0.8f, x + w * 0.14f, y + h * 0.66f);
                g.DrawLine(p, x + w * 0.86f, y + h * 0.8f, x + w * 0.86f, y + h * 0.66f);
            }
        }

        //  显示器：屏幕框 + 支架。
        public static void DrawIconDisplay(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var p = IconPen(r, c))
            {
                var scr = new RectangleF(x + w * 0.07f, y + h * 0.12f, w * 0.86f, h * 0.56f);
                using (var gp = Round(new Rectangle((int)scr.X, (int)scr.Y, (int)scr.Width, (int)scr.Height), Theme.S(2)))
                    g.DrawPath(p, gp);
                g.DrawLine(p, x + w * 0.5f, y + h * 0.68f, x + w * 0.5f, y + h * 0.84f);
                g.DrawLine(p, x + w * 0.3f, y + h * 0.88f, x + w * 0.7f, y + h * 0.88f);
            }
        }

        //  封面库：相框 + 山形。以前"封面库"也是方点。
        public static void DrawIconImage(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            var frame = new RectangleF(x + w * 0.07f, y + h * 0.12f, w * 0.86f, h * 0.76f);
            using (var p = IconPen(r, c))
            {
                using (var gp = Round(new Rectangle((int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height), Theme.S(2)))
                    g.DrawPath(p, gp);
                g.DrawLine(p, frame.Left + frame.Width * 0.12f, frame.Bottom - frame.Height * 0.16f,
                                frame.Left + frame.Width * 0.42f, frame.Top + frame.Height * 0.36f);
                g.DrawLine(p, frame.Left + frame.Width * 0.42f, frame.Top + frame.Height * 0.36f,
                                frame.Left + frame.Width * 0.68f, frame.Bottom - frame.Height * 0.16f);
                g.DrawLine(p, frame.Left + frame.Width * 0.60f, frame.Top + frame.Height * 0.46f,
                                frame.Left + frame.Width * 0.76f, frame.Top + frame.Height * 0.34f);
                g.DrawLine(p, frame.Left + frame.Width * 0.76f, frame.Top + frame.Height * 0.34f,
                                frame.Right - frame.Width * 0.08f, frame.Bottom - frame.Height * 0.16f);
                float d = IconPen(r) * 1.4f;
                g.FillEllipse(new SolidBrush(c), frame.Left + frame.Width * 0.2f,
                              frame.Top + frame.Height * 0.14f, d, d);
            }
        }

        public static void DrawIconGear(Graphics g, Rectangle r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            //  半径从 0.42 收到 0.38：齿轮的齿是**往外画**的（+2.5px），0.42×16=6.7 + 2.5 = 9.2
            //  已经顶出 8px 半径的框 —— 上下两颗齿其实一直是贴着边被裁掉的。
            //  ★ 同时去掉内圈：16px 上"外圆 + 内圆 + 8 颗齿"三层叠起来是一坨墨点（对照表里能看得很清楚），
            //  内圈在这个尺寸下不提供任何信息量，只贡献噪点。现在只剩一个环 + 8 根短齿。
            float outR = Math.Min(r.Width, r.Height) * 0.36f;
            using (var p = IconPen(r, c))
            {
                g.DrawEllipse(p, cx - outR, cy - outR, outR * 2, outR * 2);
                float tooth = IconPen(r) * 1.2f;
                for (int deg = 0; deg < 180; deg += 45)
                {
                    double rad = deg * Math.PI / 180.0;
                    float dx = (float)(Math.Cos(rad) * (outR + tooth));
                    float dy = (float)(Math.Sin(rad) * (outR + tooth));
                    g.DrawLine(p, cx - dx, cy - dy, cx + dx, cy + dy);
                }
            }
        }

        //  品牌标识（2026-10-21 并入图标体系）：外六边形**描边** + 内部**实心**闪电，
        //  和分区标题那套图标是同一套语言（闪电同样是"描边里唯一保留实心"的那一枚）。
        //  去掉渐变：全屏唯一还剩渐变的地方就是这里，而按钮的渐变已经撤掉了 —— 一个界面里
        //  "只有 Logo 有渐变"不是层次，是不统一。笔画也换成 IconPen 的同一根。
        public static void DrawBrandLogo(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            //  ★ 2026-10-24 第 138 轮：去掉底板。
            //  「彩色圆角方 + 反白符号」是**桌面 App 图标**的画法（48px 那种），
            //  放到应用内侧栏 26px 的位置，就是"说不出来的廉价感"的头号来源 ——
            //  它在读"我是个应用图标"，而这一屏要读的是"我是个品牌符号"。
            //  Linear / Raycast / Apple Music 的侧栏品牌都是**单色符号、不带底板**。
            //  （2026-10-22 从"六边形描边"改成"圆角方 + 反白"，也同样是徽章画法，两次都错在同一个地方：
            //  在应用内部用"图标"的语言。）
            //  底板去掉后笔画改粗一档：同一根笔画在有底板时靠反白托住，去底板后要自己站住。
            //  反白闪电：占图标 64% 高、水平居中，往右偏 2% 修正视觉重心（闪电形心偏左）
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            PointF[] bolt = new PointF[] {
                new PointF(x + w * 0.565f, y + h * 0.21f),
                new PointF(x + w * 0.335f, y + h * 0.53f),
                new PointF(x + w * 0.50f,  y + h * 0.53f),
                new PointF(x + w * 0.445f, y + h * 0.79f),
                new PointF(x + w * 0.675f, y + h * 0.47f),
                new PointF(x + w * 0.53f,  y + h * 0.47f)
            };
            using (var b = new SolidBrush(Accent))
                g.FillPolygon(b, bolt);
            using (var p = new Pen(Accent, 1.2f))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawPolygon(p, bolt);
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
                //  标题栏色 = 窗口主体色（2026-10-22 统一底色后主体是 Bg 不是 Panel：
                //  再跟着 Panel 白走，窗口顶部又会横贯一条白条）。
                int cap = Win32(Dark ? L(20, 20, 22) : L(242, 242, 247));
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
        [DllImport("user32.dll")]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);   // flags 2 = PW_RENDERFULLCONTENT（含 DWM 合成内容）
        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ERASE = 0x0004;
        const uint RDW_ALLCHILDREN = 0x0080;
        const uint RDW_UPDATENOW = 0x0100;

        public static void HardRepaint(Control c)
        {
            if (c == null) return;
            try
            {
                if (!c.IsHandleCreated) return;
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
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

        //  列宽随宽度自适应（2026-10-20）。
        //  背景：列宽以前只在**建表那一刻**分配一次（NewList 里调一次 FillLastColumn），此后窗口
        //  怎么变都不动 —— 缩到窗口最小尺寸 860 时，前三个固定列 596 + 末列最少 250 = 846 直接
        //  顶出横向滚动条，第 4 列整列看不见；放大时又留着右边一大片空白。
        //  两条原则，缺一条都会变成"和用户抢"：
        //    ① 装得下 → 只补末列（与原来一致），富余归最后一列；
        //    ② 装不下 → 先压末列；还不够才按比例压固定列，**且只在固定列仍等于默认值时**
        //       （= 用户没手拖过）。用户自己拖过的宽度一律不抢。
        public static void FitColumns(ListView lv, int minLastW, int minCol)
        {
            try
            {
                if (lv == null || lv.IsDisposed || lv.Columns.Count == 0) return;
                int n = lv.Columns.Count;
                if (n == 1) { FillLastColumn(lv, minLastW); return; }
                //  ⚠ 留 4px 余量：ListView 在"列宽合计 == 客户区"这种临界状态下**仍会画横向滚动条**
                //    （实测 513 vs 517 就有），但 16px 会在右侧留出一条被主题色刷灰的空区。
                //    4px 既能避开滚动条，又窄到看不出来。
                int avail = lv.ClientSize.Width - Theme.S(4);
                if (avail <= Theme.S(120)) return;
                int fixedSum = 0;
                for (int i = 0; i < n - 1; i++) fixedSum += lv.Columns[i].Width;
                int last = avail - fixedSum;
                if (last >= minLastW) { SetColWidth(lv, n - 1, last); return; }

                int[] def;
                bool hasDef = listDefaults.TryGetValue(lv, out def) && def != null && def.Length >= n;
                int defSum = 0;
                if (hasDef) for (int i = 0; i < n - 1; i++) defSum += def[i];
                if (!hasDef || defSum <= 0 || fixedSum != defSum)
                {
                    //  用户拖过列宽：不动固定列，只把末列压到能不撑破的最小值
                    SetColWidth(lv, n - 1, Math.Max(Theme.S(48), last));
                    return;
                }

                //  按比例压固定列：先给每列地板，再把余量按"超出地板的部分"分摊 —— 这样
                //  「优化项」这种宽列承担更多收缩，而 86px 的「状态」不会先被压成一条缝。
                int nf = n - 1;
                int room = Math.Max(Theme.S(48), avail - minLastW);
                int baseSum = nf * minCol;
                int[] w = new int[nf];
                if (baseSum >= room)
                {
                    for (int i = 0; i < nf; i++) w[i] = Math.Max(Theme.S(40), room / nf);
                }
                else
                {
                    int extra = room - baseSum, weight = 0;
                    for (int i = 0; i < nf; i++) weight += Math.Max(0, def[i] - minCol);
                    for (int i = 0; i < nf; i++)
                    {
                        int share = weight > 0
                            ? (int)((long)extra * Math.Max(0, def[i] - minCol) / weight)
                            : extra / nf;
                        w[i] = minCol + share;
                    }
                }
                lv.BeginUpdate();
                int got = 0;
                for (int i = 0; i < nf; i++) { SetColWidth(lv, i, w[i]); got += w[i]; }
                SetColWidth(lv, n - 1, Math.Max(Theme.S(48), avail - got));
                lv.EndUpdate();
            }
            catch { }
        }

        //  只在真的变了才写：列宽赋值会触发 ListView 重绘与 ClientSize 变化，
        //  在 Resize 里无条件写就是自激（横条出现/消失来回抖）。
        static void SetColWidth(ListView lv, int col, int w)
        {
            if (w <= 0 || col < 0 || col >= lv.Columns.Count) return;
            if (lv.Columns[col].Width != w) lv.Columns[col].Width = w;
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
                    Sec sec = p as Sec;
                    if (sec != null) return sec.Plain ? Bg : Panel;
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
            //  ★ 自带字体注册放在静态构造里（2026-10-20）：探针 StyleProbe 是直接 new MainForm 的，
            //    不走 Program.Main —— 只在 Main 里调会出现"资源编进去了但没人注册、字体还是 Noto"。
            try { InitFonts(); } catch { }
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

        //  实际解析到的字族（2026-10-20）：GDI+ 找不到族名时**不报错、静默回退**，
        //  而 Noto Sans SC 在 Windows 上常以**可变字体**安装，GDI+ 对 VF 支持不完整 ——
        //  于是"以为在用 Noto，其实在渲染雅黑/宋体"这种情况完全可能发生。
        //  启动日志里打一次，一眼看得出到底用的哪个族。
        //  全界面共用一个自绘提示实例（见 FlatTip）：系统 ToolTip 是淡黄底 + 灰边，和界面不搭。
        //  放在 Theme 而不是某个控件类里，是因为游戏卡、侧栏、主窗都要用。
        static FlatTip sharedTip;
        public static FlatTip Tip
        {
            get
            {
                if (sharedTip == null) { sharedTip = new FlatTip(); sharedTip.AutoPopDelay = 8000; sharedTip.InitialDelay = 400; }
                return sharedTip;
            }
        }

        public static string FaceInfo()
        {
            try
            {
                return "正文=" + F(9.5f).FontFamily.Name
                     + " 标题=" + FM(13f).FontFamily.Name
                     + " 数字=" + NumLight(26f).FontFamily.Name
                     + " 等宽=" + Mono(9f).FontFamily.Name;
            }
            catch { return "?"; }
        }

        // 字体缓存：同一字号只创建一次，减少 GDI 句柄。
        // ★ 缓存里的 Font 绝不能放进 using —— 一旦被 Dispose，所有同字号的绘制都会在已销毁的
        //   句柄上操作，GDI+ 抛 ArgumentException「参数无效」，控件被填白打叉（血泪教训）。
        static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();
        static Font Get(string key, float pt, FontStyle style, string family)
        {
            Font f;
            if (Cache.TryGetValue(key, out f)) return f;
            f = null;
            FontFamily pf;
            if (privateFams.TryGetValue(family, out pf))
            {
                try { f = new Font(pf, pt, style); } catch { f = null; }
            }
            if (f == null)
            {
                try { f = new Font(family, pt, style); }
                catch { try { f = new Font(FontFamily.GenericSansSerif, pt, style); } catch { f = SystemFonts.DefaultFont; } }
            }
            Cache[key] = f;
            return f;
        }
        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        //  界面字体族：默认 Noto Sans SC，自带 MiSans 注册成功后切过去（见 InitFonts）。
        //  主题缓存 key 里带族名，所以两族不会互相命中脏缓存。
        public static string FontFamilyName = "Noto Sans SC";
        public static string MediumFamilyName = "Noto Sans SC Medium";

        //  自带界面字体（2026-10-20）：MiSans 子集编进 exe。
        //  为什么必须自带：MiSans 不是 Windows 自带字，用户机器上基本没有；
        //  而 GDI 的 TextRenderer 只认**进程内注册过的族名** —— 放进 PrivateFontCollection
        //  它会找不到、静默回退成默认字体。AddFontMemResourceEx 是让 GDI 认得的正路。
        //  ⚠ 缓冲必须一直留在内存里（fontBlocks 保存指针），注册后不能释放。
        static bool fontsReady, fontsOk;
        static readonly List<IntPtr> fontBlocks = new List<IntPtr>();
        //  ⚠ GDI 和 GDI+ 是**两套**字体注册：
        //    · AddFontMemResourceEx  → 只有 GDI（TextRenderer.DrawText）认这个族名；
        //    · PrivateFontCollection → 只有 GDI+（new Font / Graphics.DrawString）认。
        //    只用前者：`new Font("MiSans", …)` 会**静默回退**成 Microsoft Sans Serif（实测）。
        //    所以两份都要注册，各取所需。
        static readonly System.Drawing.Text.PrivateFontCollection pfc = new System.Drawing.Text.PrivateFontCollection();
        static readonly Dictionary<string, FontFamily> privateFams = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        public static bool InitFonts()
        {
            if (fontsReady) return fontsOk;
            fontsReady = true;
            try
            {
                var asm = typeof(Theme).Assembly;
                int ok = 0;
                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.IndexOf("Font.MiSans", StringComparison.Ordinal) < 0) continue;
                    using (var st = asm.GetManifestResourceStream(res))
                    {
                        if (st == null) continue;
                        var buf = new byte[st.Length];
                        int read = 0;
                        while (read < buf.Length)
                        {
                            int n = st.Read(buf, read, buf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        IntPtr p = Marshal.AllocHGlobal(buf.Length);
                        Marshal.Copy(buf, 0, p, buf.Length);
                        uint cnt = 0;
                        IntPtr hh = AddFontMemResourceEx(p, (uint)buf.Length, IntPtr.Zero, out cnt);
                        fontBlocks.Add(p);
                        if (cnt > 0) ok++;
                        try { pfc.AddMemoryFont(p, buf.Length); } catch { }
                        try { Program.Log("[字体] " + res + " len=" + buf.Length + " cnt=" + cnt); } catch { }
                    }
                }
                foreach (FontFamily fam in pfc.Families)
                    if (!string.IsNullOrEmpty(fam.Name)) privateFams[fam.Name] = fam;
                if (ok > 0 && privateFams.ContainsKey("MiSans"))
                {
                    FontFamilyName = "MiSans";
                    if (privateFams.ContainsKey("MiSans Medium")) MediumFamilyName = "MiSans Medium";
                    fontsOk = true;
                }
            }
            catch { }
            return fontsOk;
        }
        public static Font F(float pt) { return Get("f" + pt + FontFamilyName, pt, FontStyle.Regular, FontFamilyName); }
        public static Font FB(float pt) { return Get("b" + pt + FontFamilyName, pt, FontStyle.Bold, FontFamilyName); }
        public static Font Mono(float pt) { return Get("m" + pt, pt, FontStyle.Regular, "Consolas"); }

        //  三档字重里的"中档"（2026-10-20 字体方向）：中文标题长期以来只有 Regular / Bold 两档，
        //  Bold 又被到处用（标题、表头、行标签），等于没有重点。Medium 一档让标题"细而有力"，
        //  Bold 只留给页面大标题。Noto Sans SC 的命名实例在 GDI+ 里是独立族名，得先问字体集，
        //  直接 new Font 会**静默回退**成默认宋体 —— 那比现在的 Bold 难看十倍。
        static bool? numOk;
        static bool FamilyInstalled(string name)
        {
            try
            {
                using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                    foreach (var fam in ifc.Families)
                        if (string.Equals(fam.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }
        public static Font FM(float pt)
        {
            //  字族名由 InitFonts 决定（MiSans Medium / Noto Sans SC Medium），不再写死
            return Get("M" + pt + MediumFamilyName, pt, FontStyle.Regular, MediumFamilyName);
        }
        //  ⚠ 用**常规宽度**的 Bahnschrift，不用 SemiBold Condensed（2026-10-20）。
        //    Condensed 是"省横向空间"的选择，配在中文里会显得挤、像报表；同一族里
        //    KPI 用的是 Light，这里回到常规宽度才连贯。Bahnschrift 的数字本身等宽，列仍对齐。
        //  数字字型（DIN 风）：仪表盘的百分比 / 时钟 / 版本号。Bahnschrift Win10 1809+ 自带，
        //  SemiBold Condensed 是 NVIDIA App / 性能页那种"仪表数字"。中文混排时只用于纯数字段。
        public static Font Num(float pt)
        {
            if (numOk == null) numOk = FamilyInstalled("Bahnschrift");
            if (numOk.Value) return Get("N" + pt + "Bahns", pt, FontStyle.Regular, "Bahnschrift");
            return FM(pt);
        }

        //  大号数字用 Light 而不是 SemiBold Condensed（2026-10-20，设计调研结论）：
        //  KPI 主数值的通行规格是 28–36px，且"大而细"比"小而粗"更从容 —— SemiBold Condensed
        //  本来就是给密集表格省横向空间的，放在卡片主数值上挤、像报表。Bahnschrift 是同一族，
        //  数字仍等宽，换字重不会让列对不齐。
        static bool? numLightOk;
        public static Font NumLight(float pt)
        {
            if (numLightOk == null) numLightOk = FamilyInstalled("Bahnschrift Light");
            if (numLightOk.Value) return Get("N" + pt + "BahnsL", pt, FontStyle.Regular, "Bahnschrift Light");
            return F(pt);
        }

        // ---- 文字灰度化（2026-10-29 第 157 轮）----
        //  GDI（TextRenderer / Label）的文字渲染跟系统 ClearType 走，浅色底上文字边缘是
        //  红/蓝子像素彩边 —— 第 153 轮实测按钮文字区 22.7% 像素 chroma>40，用户说的"毛边"
        //  大头就是它。已验证字体层换 quality 走不通（FromHfont 丢 ANTIALIASED_QUALITY，
        //  tools/grey_probe.cs：Label 两种字体彩边同为 1351），所以绘制端统一改 GDI+ 灰度。
        //  ⚠ DrawTxt 与 TextRenderer.DrawText **同签名** —— 53 个调用点只换方法名不改参数。
        //  量宽仍大多走 MeasureText（GDI），两端 advance 有差，但同一段文字内部只跟自己比，
        //  跨引擎对比只发生在"量完再画"的像素级接缝 —— 那几处已切 MeasureTxt（MetaLine/PaintTile）。
        public static void DrawTxt(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags tf)
        {
            if (string.IsNullOrEmpty(s) || f == null || r.Width <= 0 || r.Height <= 0) return;
            var sf = FmtOf(tf);
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            try { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, (RectangleF)r, sf); }
            finally { g.TextRenderingHint = hint; }
        }

        //  与 DrawTxt 同一套 StringFormat 的量宽 —— 需要"量完再画"精确接缝的地方用它。
        public static SizeF MeasureTxt(Graphics g, string s, Font f, TextFormatFlags tf)
        {
            var sf = FmtOf(tf);
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return g.MeasureString(s ?? "", f, new SizeF(int.MaxValue, int.MaxValue), sf);
        }

        static StringFormat FmtOf(TextFormatFlags tf)
        {
            //  GenericTypographic 起底：去掉 GDI+ 默认在行盒顶部加的 internal leading ——
            //  那段下沉随字号变大（26pt 数字比 13pt 单位沉得多），混排基线就散了
            //  （实测 "5 %" 的单位掉到基线下方）。Typographic 的"顶 = cellAscent"与
            //  GDI DrawText 的 DT_TOP 语义一致；水平方向也去掉两侧 overhang，
            //  与 NoPadding 调用点的"真左缘"预期一致。
            var sf = new StringFormat(StringFormat.GenericTypographic);
            if ((tf & TextFormatFlags.Right) != 0) sf.Alignment = StringAlignment.Far;
            else if ((tf & TextFormatFlags.HorizontalCenter) != 0) sf.Alignment = StringAlignment.Center;
            else sf.Alignment = StringAlignment.Near;
            if ((tf & TextFormatFlags.Bottom) != 0) sf.LineAlignment = StringAlignment.Far;
            else if ((tf & TextFormatFlags.VerticalCenter) != 0) sf.LineAlignment = StringAlignment.Center;
            else sf.LineAlignment = StringAlignment.Near;
            var ff = sf.FormatFlags | StringFormatFlags.LineLimit;   // 不画半行（GDI 的行为）
            if ((tf & TextFormatFlags.SingleLine) != 0) ff |= StringFormatFlags.NoWrap;
            sf.FormatFlags = ff;
            //  EndEllipsis 是单行语义：DrawString 默认会折行，折行后省略号就没了 —— 强制单行再截
            if ((tf & TextFormatFlags.EndEllipsis) != 0)
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
            }
            //  NoPrefix 在 GDI+ 里天然成立（& 不做助记符处理），NoPadding 是 GDI 的字格内边距
            //  概念，DrawString 没有 —— 两者映射为空操作。
            return sf;
        }

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
        //  Mac 那种"先冲过一点再收回"的手感（CSS 里的 easeOutBack）。
        //  ⚠ 只给**几何量**（位移 / 宽度）用：颜色量过冲会被 Mix 夹回端点，加了等于没加。
        public static readonly double[] Spring = { 0.22, 1.28, 0.36, 1.00 };
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
            Font = Theme.F(10f);             // 按钮 13.3px（第 158 轮 9→10：与 14px 正文同层差半档）
            //  高度 30 → 34（2026-10-20）：调研结论是"按钮越大，内边距越要宽松，
            //  不能按字号等比"。30px 配 9pt 字，上下只剩 9px，按下去像个小方块；
            //  34px 让按钮自己有一点分量，也不至于把按钮行撑开（宽度没动）。
            Size = new Size(Theme.S(86), Theme.S(34));
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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

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
                    edge = Theme.Ok; txt = Theme.TextSafe(Theme.Ok); break;
                case BtnKind.Danger:
                    fill = Theme.Mix(Theme.ErrFill, Theme.ErrSoft, Math.Max(h, dn));
                    edge = Theme.Err; txt = Theme.TextSafe(Theme.Err); break;
                default:
                    //  Apple 的普通按钮 = 白底 + 1px 灰边 + 正文色标签，**悬停不变色相**，
                    //  只把底压深一档。原来悬停会把描边染成强调色、文字从灰变黑 —— 那是"网页按钮"
                    //  的语言：鼠标还没点，颜色先来抢注意力。
                    fill = Theme.Mix(Theme.Mix(Theme.Panel, Theme.Hover, h), Theme.Field, dn);
                    edge = Theme.Mix(Theme.FieldEdge, Theme.TextFaint, Math.Max(h, dn) * 0.55);
                    txt = Theme.Text;
                    break;
            }
            if (!Enabled) { fill = Theme.Subtle; edge = Theme.Line; txt = Theme.TextDisabled; }

            //  按压缩进 1px：鼠标按下时按钮"陷"一下，配合颜色加深就是 Mac 的点击手感
            int pad = Theme.S(1);
            int press = (int)Math.Round(Math.Min(1.0, dn) * Theme.S(1));
            var r = new Rectangle(pad + press, pad + press, Width - 1 - (pad + press) * 2, Height - 1 - (pad + press) * 2);
            int rad = Theme.RBtn;
            bool primary = (Kind == BtnKind.Primary) && Enabled;
            if (primary)
            {
                // 悬停外发光微光晕 (Ambient Glow)
                if (h > 0.02 && dn < 0.8)
                {
                    int glowAlpha = (int)Math.Round(45 * h);
                    if (glowAlpha > 0)
                    {
                        var glowRect = new Rectangle(press, press, Width - 1 - press * 2, Height - 1 - press * 2);
                        using (var glowPen = new Pen(Color.FromArgb(glowAlpha, Theme.Accent), Theme.S(2)))
                        using (var gpG = Theme.Round(glowRect, rad + Theme.S(1)))
                        {
                            g.DrawPath(glowPen, gpG);
                        }
                    }
                }
                double down = Math.Max(h * 0.12, dn);
                Theme.FillRound(g, r, rad, Theme.Mix(Theme.Accent, Theme.AccentDeep, down));
                // 顶边微光漫反射高亮
                using (var hlPen = new Pen(Color.FromArgb((int)Math.Round(35 + 25 * h), 255, 255, 255), 1f))
                using (var gpHl = Theme.Round(r, rad))
                {
                    g.DrawPath(hlPen, gpHl);
                }
            }
            else
            {
                Theme.FillRound(g, r, rad, fill);
                Theme.EdgeRound(g, r, rad, edge, 1f);
            }
            //  填充按钮（强调色底 + 白字）的标签走灰度抗锯齿：ClearType 是按"接近中性灰的底"
            //  设计的，压在饱和蓝底上，每个笔画边缘都会带一圈橙/蓝彩边（截图放大可见）。
            //  描边/浅底按钮仍然走 GDI（那里 ClearType 更清晰），只有填充按钮换。
            if (primary)
                Theme.DrawPhotoText(g, Text, Theme.FB(9f), r, txt, StringAlignment.Center);
            else
                Theme.DrawTxt(g, Text, Font, r, txt,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) Theme.DrawFocusRing(g, r, rad);
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

        //  ★ 瓦片字体缓存（2026-10-29 第 155 轮）：PaintTile 原来每帧构造 3 个 Font
        //    （26pt 数值 / 13pt 单位 / 9pt 标签）—— 四个瓦片 × 每秒数帧的纯分配。
        //    换主题 = RebuildChrome() 重建控件树，字段跟着重新初始化，不会拿到旧主题的字号。
        readonly Font tileCapF = Theme.F(9f);
        readonly Font tileNumF = Theme.NumLight(26f);
        readonly Font tileUnitF = Theme.F(13f);

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
            //  第 148 轮：42 → 62。瓦片改成"标签 / 大数值 / 条"三行后，
            //  42 会把数值和条挤在一起（或把条顶出瓦片）。
            Height = Theme.S(62);
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

        //  只要数字本体（单位单独画，见 PaintTile）
        string NumBody()
        {
            if (!hasNum) return right;
            double v2 = aNum.Value;
            return numDec ? v2.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                          : ((long)Math.Round(v2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        //  字体基线（像素）。TextRenderer 的 VerticalCenter 居中的是"行盒"(ascent+descent)，
        //  要两段不同字号的文字共用一条基线，就必须自己把行盒拆成 ascent / descent。
        //  用设计单位比例 × Font.Height，避免把 DPI 换算写死在代码里。
        internal static float AscentOf(Font f)
        {
            var ff = f.FontFamily;
            int em = ff.GetEmHeight(f.Style);
            int asc = ff.GetCellAscent(f.Style);
            int desc = ff.GetCellDescent(f.Style);
            if (em <= 0 || asc + desc <= 0) return f.Height * 0.8f;
            return f.Height * (float)asc / (asc + desc);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (Grid) { PaintTile(g); return; }

            // 绘制左侧标题与硬件微图标
            int iconSz = Theme.S(13);
            var iconRect = new Rectangle(0, Theme.S(2), iconSz, iconSz);
            if (cap.Contains("CPU")) Theme.DrawIconChip(g, iconRect, BarColor);
            else if (cap.Contains("GPU") && !cap.Contains("温度")) Theme.DrawIconChip(g, iconRect, BarColor);
            else if (cap.Contains("内存")) Theme.DrawIconPulse(g, iconRect, BarColor);
            else if (cap.Contains("温度")) Theme.DrawIconThermo(g, iconRect, BarColor);

            int titleX = iconSz + Theme.S(6);
            var fc = Theme.FM(9.5f);
            var fv = Theme.Num(11f);
            int lh = Math.Max(fc.Height, fv.Height);

            Theme.DrawTxt(g, cap, fc, new Rectangle(titleX, 0, Width / 2, lh), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 右侧数值
            Theme.DrawTxt(g, NumText(), fv, new Rectangle(Width / 2, 0, Width - Width / 2, lh), BarColor,
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

        // 指标瓦片：图标 + 标签 + 放大数值 + 底部细条
        void PaintTile(Graphics g)
        {
            //  ★ 内边距 13 → 0（2026-10-20 对齐审计）：瓦片原来自带 13px 内边距，和卡片的 padX 叠在一起，
            //    结果"卡片标题/正文"在 padX，"瓦片里的图标与进度条"在 padX+13 —— 一屏两套左基准，
            //    肉眼就是"没对齐"。瓦片没有底色、不需要自己的内边距，对齐到卡片的内容网格即可。
            int pad = 0;
            //  ★ 不再画"框"（2026-10-20）：白卡里再嵌四个带边框的浅灰块 = 卡片套卡片，
            //    这是"仪表盘模板"最典型的拼贴感。现在整块透明，靠留白分组，
            //    一屏少掉四个方块、四圈描边，卡片本身重新成为唯一的"面"。
            int iconSz = Theme.S(14);
            var iconRect = new Rectangle(pad, pad, iconSz, iconSz);
            //  图标一律墨色：颜色只留给状态（进度条），和卡片标题上的图标同一条规矩。
            Color ic = Theme.TextDim;
            if (cap.Contains("CPU")) Theme.DrawIconChip(g, iconRect, ic);
            else if (cap.Contains("内存")) Theme.DrawIconPulse(g, iconRect, ic);
            else if (cap.Contains("温度")) Theme.DrawIconThermo(g, iconRect, ic);
            else Theme.DrawIconChip(g, iconRect, ic);

            //  ★ 2026-10-25 第 148 轮：瓦片从「标签左 / 数值右」改成**上下两行、左对齐**。
            //    原来一行两端对齐，瓦片中间空出一条贯穿全宽的空白 —— 四个并排就是四面空墙，
            //    再配合 2×2 等分布局，典型"仪表盘模板"的拼贴感。
            //    现在标签在上、数值紧跟其下、条压在最底，四个瓦片形成同一条左基准。
            int tx = pad + iconSz + Theme.S(6);
            Theme.DrawTxt(g, cap, tileCapF,
                new Rectangle(tx, pad, Width - tx, Theme.S(18)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.NoPadding);       // 与下面的数值共用同一条真左缘（见数值注释）

            //  数值：18pt SemiBold Condensed → **26pt Light**。
            //  调研结论：KPI 主数值 28–36px，"大而细"比"小而粗"从容；颜色仍用正文色，
            //  饱和度留给进度条（颜色=状态，不是装饰）。
            //  ⚠ NoPadding 必须加（2026-10-20 像素审计）：GDI 绘制默认在文字四周留一段内边距，
            //    右对齐时这段也吃在右边 —— 实测数值右缘比进度条右端短 **12px**，两列都短，
            //    所以不是"某一列算错了"，而是对齐方式本身留了缝。NoPadding 才是真贴边。
            //  数值与**标签文字**同一条左基准（不是与图标对齐）—— 文字块整体成一个左边缘。
            //
            //  ★ 2026-10-26（第 154 轮）：数值与单位**分级**。
            //    「27%」三个字符同样大，是"数据表格"的排法；真正像仪表的（iOS 电量、macOS 存储、
            //    Linear 的 KPI 卡）一律是「数字大一号、单位小一档、两者共一条基线」。
            //    这一笔不动任何布局，但一屏四个读数会从"数字"变成"仪表"。
            //    单位取 TextDim：它是量纲，不是结论，颜色不该和数值争。
            var fn = tileNumF;
            var fu = tileUnitF;
            //  ★ 数值贴**瓦片左缘**（pad），不跟标签一起缩进到 tx。
            //    原来数值和标签同一条 x，于是瓦片里出现两条左基准：图标与进度条在 0，
            //    大数字在 0+图标宽+6 —— 一屏四个大数字全都往右缩 20px，而条子从 0 起，
            //    两者错开的那 20px 就是"没对齐"的来源（放大看最明显的就是数字悬在半空）。
            //    现在「图标 / 大数字 / 进度条」共用同一条左缘，标签是有意挂在图标后面的上标。
            //  ★ 2026-10-30（第 169 轮）：数字区 11 → **20**。原布局标签行画到 y=14、
            //    数字区从 y=11 起 —— 两段重叠 3px，26pt 字形的顶部直接顶进标签
            //    （用户 1.3.0 深色截图："内存"两个字压在 49 的顶上，"全挤在一堆"）。
            //    72px 瓦片的正确节奏：标签 [0,18) / 数字 [20,54) / 条 [67,72)。
            var numArea = new Rectangle(pad, Theme.S(20), Width - pad * 2, Theme.S(34));
            string body = NumBody();
            string unitText = hasNum ? numSuffix : "";
            float baseY = numArea.Top + (numArea.Height - fn.Height) / 2f + AscentOf(fn);
            Theme.DrawTxt(g, body, fn,
                new Rectangle(numArea.Left, (int)Math.Round(baseY - AscentOf(fn)), numArea.Width, fn.Height + Theme.S(2)),
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (unitText.Length > 0)
            {
                int bw = (int)Math.Ceiling(Theme.MeasureTxt(g, body, fn,
                             TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
                int ux = numArea.Left + bw + Theme.S(1);
                Theme.DrawTxt(g, unitText, fu,
                    new Rectangle(ux, (int)Math.Round(baseY - AscentOf(fu)), Math.Max(Theme.S(8), numArea.Right - ux), fu.Height + Theme.S(2)),
                    Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            int th = Theme.S(5);
            int ty = Height - pad - th;
            var track = new Rectangle(pad, ty, Width - pad * 2, th);
            Theme.FillRound(g, track, th / 2, Color.FromArgb(Theme.Dark ? 44 : 28, BarColor));
            int w = (int)Math.Round(track.Width * Math.Min(100, aBar.Value) / 100.0);   // 条宽跟着补间走
            if (w > 0)
            {
                int barW = Math.Max(th, w);
                var barRect = new Rectangle(pad, ty, barW, th);
                Color endCol = Color.FromArgb(Math.Min(255, BarColor.R + 25), Math.Min(255, BarColor.G + 25), Math.Min(255, BarColor.B + 35));
                using (var br = new LinearGradientBrush(barRect, BarColor, endCol, 0f))
                {
                    Theme.FillRound(g, barRect, th / 2, br);
                }
                // 端点高亮发光微球 (Glow Cap Highlight)
                if (barW >= th)
                {
                    int capSz = Math.Max(2, th - Theme.S(1));
                    int cx = barRect.Right - capSz / 2 - Theme.S(1);
                    int cy = ty + th / 2;
                    using (var gb = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                        g.FillEllipse(gb, cx - capSz / 2 - 1, cy - capSz / 2 - 1, capSz + 2, capSz + 2);
                    using (var wb = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                        g.FillEllipse(wb, cx - capSz / 2, cy - capSz / 2, capSz, capSz);
                }
            }
        }
    }

    // ======================= 灰度文字的 Label（第 157 轮） =======================
    //  WinForms Label 的文字走 GDI ClearType（实测面包屑一串文字 300+ 彩边像素）。
    //  UserPaint 接管绘制后统一走 GDI+ AntiAliasGridFit，与自绘控件同一套字形渲染。
    //  布局（AutoSize / TextAlign / AutoEllipsis）行为保持 Label 的；
    //  本工程所有 Label 都是不透明底（BackColor 明确给色），这里按"铺满底色"画。
    public class TxtLbl : Label
    {
        public TxtLbl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, 0, 0, Width, Height);
            if (Text == null || Text.Length == 0 || !Enabled) { base.OnPaint(e); return; }
            var sf = new StringFormat();
            switch (TextAlign)
            {
                case ContentAlignment.TopRight:
                case ContentAlignment.MiddleRight:
                case ContentAlignment.BottomRight:
                    sf.Alignment = StringAlignment.Far; break;
                case ContentAlignment.TopCenter:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.BottomCenter:
                    sf.Alignment = StringAlignment.Center; break;
            }
            switch (TextAlign)
            {
                case ContentAlignment.BottomLeft:
                case ContentAlignment.BottomCenter:
                case ContentAlignment.BottomRight:
                    sf.LineAlignment = StringAlignment.Far; break;
                case ContentAlignment.MiddleLeft:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.MiddleRight:
                    sf.LineAlignment = StringAlignment.Center; break;
            }
            if (AutoEllipsis)
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;      // 省略号是单行语义
                sf.Trimming = StringTrimming.EllipsisCharacter;
            }
            using (var br = new SolidBrush(ForeColor))
                g.DrawString(Text, Font, br, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    // ======================= 自绘：环形健康度仪表盘 (方案C) =======================
    public class RadialGauge : Control
    {
        int val;
        string score = "—";
        string sub = "优化度";
        Color arcColor = Theme.Ok;
        readonly Anim aProgress;
        public Color? Corner;

        public RadialGauge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Size = new Size(Theme.S(96), Theme.S(96));
            aProgress = new Anim(this, Motion.Medium, Motion.Decel);
            aProgress.Snap(0);
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = value < 0 ? 0 : (value > 100 ? 100 : value);
                if (v == val) return;
                val = v;
                aProgress.To(v);
            }
        }

        public string ScoreText
        {
            get { return score; }
            set { score = value == null ? "" : value; Invalidate(); }
        }

        public string SubText
        {
            get { return sub; }
            set { sub = value == null ? "" : value; Invalidate(); }
        }

        public Color ArcColor
        {
            get { return arcColor; }
            set { arcColor = value; Invalidate(); }
        }

        public void SetHealth(int ok, int total, Color col)
        {
            arcColor = col;
            int pct = total > 0 ? (int)Math.Round(100.0 * ok / total) : 0;
            score = pct + "%";
            sub = ok + "/" + total;
            Value = pct;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surf = Corner.HasValue ? Corner.Value : Theme.ParentSurface(this);
            using (var bgBr = new SolidBrush(surf))
                g.FillRectangle(bgBr, 0, 0, Width, Height);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            int sz = Math.Min(Width, Height);
            int pad = Theme.S(5);
            int penW = Math.Max(Theme.S(5), sz / 13);
            int diam = Math.Max(1, sz - pad * 2 - penW);
            var rect = new Rectangle((Width - diam) / 2, (Height - diam) / 2, diam, diam);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // 1. 底轨 (Track): 柔和半透明弧圈
            int trackAlpha = Theme.Dark ? 40 : 25;
            using (var trackPen = new Pen(Color.FromArgb(trackAlpha, arcColor), penW))
            {
                g.DrawEllipse(trackPen, rect);
            }

            // 2. 动态活动环 (Active Arc)
            double curVal = Math.Max(0.0, Math.Min(100.0, aProgress.Value));
            float sweep = (float)(360.0 * curVal / 100.0);
            if (sweep > 0.5f)
            {
                if (sweep > 2f)
                {
                    using (var glowPen = new Pen(Color.FromArgb(Theme.Dark ? 36 : 22, arcColor), penW + Theme.S(3)))
                    {
                        glowPen.StartCap = LineCap.Round; glowPen.EndCap = LineCap.Round;
                        if (sweep >= 359.5f) g.DrawEllipse(glowPen, rect);
                        else g.DrawArc(glowPen, rect, -90f, sweep);
                    }
                }
                using (var activePen = new Pen(arcColor, penW))
                {
                    activePen.StartCap = LineCap.Round;
                    activePen.EndCap = LineCap.Round;
                    if (sweep >= 359.5f)
                    {
                        g.DrawEllipse(activePen, rect);
                    }
                    else
                    {
                        g.DrawArc(activePen, rect, -90f, sweep);
                    }
                }
            }

            // 3. 居中主数值与标注
            float numPt = sz < Theme.S(90) ? 15.5f : 19f;
            float subPt = sz < Theme.S(90) ? 7.5f : 8.5f;
            Font fNum = Theme.NumLight(numPt);
            Font fSub = Theme.F(subPt);

            int cx = rect.X + rect.Width / 2;
            int cy = rect.Y + rect.Height / 2;

            Size szNum = TextRenderer.MeasureText(score, fNum);
            Size szSub = TextRenderer.MeasureText(sub, fSub);

            int totalH = szNum.Height + szSub.Height - Theme.S(4);
            int startY = cy - totalH / 2;

            var rNum = new Rectangle(cx - szNum.Width / 2, startY, szNum.Width, szNum.Height);
            Theme.DrawTxt(g, score, fNum, rNum, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            var rSub = new Rectangle(cx - szSub.Width / 2, startY + szNum.Height - Theme.S(4), szSub.Width, szSub.Height);
            Theme.DrawTxt(g, sub, fSub, rSub, Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    // ======================= 自绘：带动态呼吸状态指示灯的 Label (方案C) =======================
    public class BreathingLabel : Label
    {
        Timer timer;
        float phase;
        bool inGame;
        bool inOffice;
        public Color? Corner;

        public BreathingLabel(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            timer = new Timer();
            timer.Interval = 40;
            timer.Tick += delegate
            {
                if (IsDisposed) return;
                Form f = FindForm();
                if (!Visible || (f != null && f.WindowState == FormWindowState.Minimized))
                {
                    if (timer != null && timer.Enabled) timer.Stop();
                    return;
                }
                phase += inGame ? 0.16f : 0.06f;
                if (phase > (float)(Math.PI * 2)) phase -= (float)(Math.PI * 2);
                Invalidate();
            };
            timer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncTimer();
        }

        void SyncTimer()
        {
            if (timer == null || IsDisposed) return;
            Form f = FindForm();
            bool running = Visible && (f == null || f.WindowState != FormWindowState.Minimized);
            if (running && !timer.Enabled) timer.Start();
            else if (!running && timer.Enabled) timer.Stop();
        }

        public void SetMode(bool inGame, bool office)
        {
            this.inGame = inGame;
            this.inOffice = office;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null)
            {
                timer.Stop();
                timer.Dispose();
                timer = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Color surf = Corner.HasValue ? Corner.Value : Theme.ParentSurface(this);
            using (var bgBr = new SolidBrush(surf))
                g.FillRectangle(bgBr, 0, 0, Width, Height);

            if (Text == null || Text.Length == 0) return;

            Color dotCol = inGame ? Theme.Accent : (inOffice ? Theme.Golden : Theme.Ok);
            float sinVal = (float)((Math.Sin(phase) + 1.0) * 0.5);

            int dotDia = Theme.S(8);
            int cy = Height / 2;
            int cx = Theme.S(6);

            // Halo pulse
            int haloMax = Theme.S(inGame ? 7 : 4);
            int haloR = dotDia / 2 + (int)Math.Round(haloMax * sinVal);
            int haloAlpha = (int)Math.Round((inGame ? 85 : 45) * (1.0f - sinVal * 0.6f));
            if (haloAlpha > 0)
            {
                using (var haloBr = new SolidBrush(Color.FromArgb(haloAlpha, dotCol)))
                    g.FillEllipse(haloBr, cx - haloR, cy - haloR, haloR * 2, haloR * 2);
            }

            // Core dot
            using (var dotBr = new SolidBrush(dotCol))
                g.FillEllipse(dotBr, cx - dotDia / 2, cy - dotDia / 2, dotDia, dotDia);

            // Center highlight
            using (var hiBr = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                g.FillEllipse(hiBr, cx - Theme.S(1), cy - Theme.S(2), Theme.S(2), Theme.S(2));

            // Text
            int textX = cx + dotDia / 2 + Theme.S(7);
            var textRect = new Rectangle(textX, 0, Math.Max(0, Width - textX), Height);
            Theme.DrawTxt(g, Text, Font, textRect, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // ======================= 自绘：工具条右端的元信息行 =======================
    //  一句话里两段颜色，Label 做不到（ForeColor 只有一个）。
    //  为什么非要两段：整句染成告警橙会把「库 12 款 · 14:37 扫描」这种中性信息一起喊起来，
    //  于是卡片里最响的东西从主按钮变成了注脚 —— 层级被自己的配色推翻。
    //  现在只有结论段跟状态走色，量纲（库规模 / 上次扫描）恒为次级灰。
    public class MetaLine : Control
    {
        public string Head = "", Tail = "";
        public Color HeadColor = Theme.TextDim;
        public bool RightAlign = false;

        public MetaLine()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Font = Theme.F(9.5f);
        }

        static int Tw(Graphics g, string s, Font f)
        {
            if (s == null || s.Length == 0) return 0;
            //  第 157 轮：Head/Tail 两段接缝要求"量的宽 = 画的宽"，量画必须同一引擎（GDI+）
            return (int)Math.Ceiling(Theme.MeasureTxt(g, s, f,
                       TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            int wh = Tw(g, Head, Font), wt = Tw(g, Tail, Font);
            //  窄窗口兜底：整句放不下时**先丢量纲段（库规模 / 扫描时间）、保住结论段**。
            //  反过来丢结论就正好把「有几项没生效」这条唯一有动作价值的信息藏了；
            //  而硬裁左边会剩半句「…1 项优化未生」，比不显示更难看。
            if (wh + wt > Width) wt = 0;
            if (wh + wt <= 0) return;
            int x = RightAlign ? Width - (wh + wt) : 0;
            var rf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                     | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (wh > 0) Theme.DrawTxt(g, Head, Font, new Rectangle(x, 0, wh + Theme.S(2), Height), HeadColor, rf);
            if (wt > 0) Theme.DrawTxt(g, Tail, Font, new Rectangle(x + wh, 0, wt + Theme.S(2), Height), Theme.TextDim, rf);
        }
    }

    // ======================= 自绘：现代化遥测面积曲线 =======================
    public class Spark : Control
    {
        readonly List<float> data = new List<float>();
        public Color LineColor = Theme.Accent;
        public string Caption = "";
        public string Unit = "%";
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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var f = Theme.F(8.5f);
            var fv = Theme.FB(9.5f);

            // 标题
            Theme.DrawTxt(g, Caption, f, new Rectangle(Theme.S(2), 0, Width / 2, f.Height + Theme.S(2)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            // 右侧最新值（没数据时给"—"，位置与有数据时一致 —— 空/非空两态不要在布局上跳）
            if (data.Count > 0)
            {
                float lastVal = data[data.Count - 1];
                Theme.DrawTxt(g, ((int)Math.Round(lastVal)) + (Unit ?? "%"), fv,
                    new Rectangle(Width / 2, 0, Width / 2 - Theme.S(4), f.Height + Theme.S(2)), LineColor,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
            else
            {
                Theme.DrawTxt(g, "—", fv,
                    new Rectangle(Width / 2, 0, Width / 2 - Theme.S(4), f.Height + Theme.S(2)), Theme.TextFaint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            int top = f.Height + Theme.S(8);
            int h = Height - top - Theme.S(4);
            if (Width <= Theme.S(10) || h < Theme.S(10)) return;

            // 背景参考网格线（第 147 轮：从 LineSoft 加深到 Line —— 原来那档太淡，
            //  空态下整片看着像"图表坏了"，其实网格一直在，只是没人看得见）
            using (var p = new Pen(Theme.Line, 1f))
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
                //  空态提示压在**底部**，不与网格线交叠（原来居中，文字穿过虚线，看着像乱码）
                Theme.DrawTxt(g, "等待采样…", f, new Rectangle(0, top + h - f.Height - Theme.S(4), Width, f.Height + Theme.S(2)),
                    Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
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
        ToolStripDropDown openMenu;                 // 当前实际弹出的下拉（每次 ShowDrop 新建）
        static RoundCombo openOwner;                // 全局唯一弹出者：滚动发生时要能找到并收掉它
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
            Font = Theme.F(10f);             // 下拉值文字（第 158 轮 9→10，与输入框同层）
            Cursor = Cursors.Hand;
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
            var m = MenuFactory.New();
            //  显式指定渲染器：ToolStripManager.Renderer 对 ContextMenuStrip 不总是生效，
            //  之前就退回了系统默认的**白底直角**列表（和整套自绘 UI 完全两张皮）。
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false;
            m.Font = Theme.F(9f);
            m.BackColor = Theme.Field;
            m.DropShadowEnabled = true;
            //  限宽限高：原来 MinimumSize 直接取控件宽度，Block 拉伸后下拉和整行一样宽（一张大白板）；
            //  高度不限时窗口底部还会被 ToolStrip 自动翻到控件上方，挡住下面的按钮。
            m.MinimumSize = new Size(Theme.S(140), 0);
            m.MaximumSize = new Size(Math.Min(Width, Theme.S(360)), Theme.S(340));
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Items.At(i));
                it.ForeColor = Theme.Text;
                it.Checked = (i == sel);
                it.Click += delegate { SelectedIndex = idx; };
                m.Items.Add(it);
            }
            //  用**屏幕坐标**定位：原来传的是控件客户区坐标，控件处在滚动容器里时会被算到别的控件旁边
            //  （截图里 SM86 列表飘到"架构(Router)"下拉右上、压住按钮，就是这么来的）。
            //  ⚠ 顶层窗口 + 一次性坐标 = 只在**打开那一刻**是对的：页面一滚，锚点跟着内容搬走，
            //    菜单还钉在旧屏幕位置上（2026-10-31 用户："滑动页面时菜单到处乱晃"）。
            //    修法不是让它跟着挪（补间期间每帧重定位只会更乱），是**内容一动就收**——
            //    Pg.SetScrollY 值变化时调 CloseOpenPopup()，和浏览器/系统菜单的直觉一致。
            CloseOpenPopup();                        // 互斥保险：理论上 AutoClose 已收掉旧弹层
            openMenu = m; openOwner = this;
            m.Closed += delegate
            {
                if (openMenu == m) openMenu = null;
                if (openOwner == this) openOwner = null;
                Invalidate();                        // 弹出态边框高亮随关闭恢复
            };
            m.Show(PointToScreen(new Point(0, Height + 2)));
        }

        //  内容滚动时的统一收口（Pg.SetScrollY 调用）：收掉还钉在旧屏幕坐标上的下拉。
        //  菜单实例是私有字段，外层摸不到，所以做成静态方法。
        public static void CloseOpenPopup()
        {
            RoundCombo c = openOwner;
            if (c == null) return;
            openOwner = null;
            ToolStripDropDown m = c.openMenu;
            c.openMenu = null;
            //  换主题会重建整棵控件树：登记链上可能残留已 Dispose 的组合框，只清登记不动它
            if (m != null && !m.IsDisposed && m.Visible) { try { m.Close(); } catch { } }
            if (!c.IsDisposed) c.Invalidate();
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
            Theme.FillRound(g, r, Theme.RField, Theme.FieldOn(BackColor));
            Theme.EdgeRound(g, r, Theme.RField,
                (hover || openMenu != null) && Enabled ? Theme.Accent : Theme.FieldEdge, 1f);
            string tx = Text;
            if (tx.Length == 0) tx = "—";
            Theme.DrawTxt(g, tx, Font, new Rectangle(Theme.S(10), 0, Width - Theme.S(30), Height),
                Enabled ? Theme.Text : Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            // 自绘 chevron：原生箭头是系统色，深色主题下会突兀
            int cx = Width - Theme.S(14), cy = Height / 2;
            using (var p = new Pen(Enabled ? Theme.TextDim : Theme.TextDisabled, 1.9f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[] {
                    new Point(cx - Theme.S(4), cy - Theme.S(2)),
                    new Point(cx, cy + Theme.S(2)),
                    new Point(cx + Theme.S(4), cy - Theme.S(2)) });
            }
            if (Focused && ShowFocusCues) Theme.DrawFocusRing(g, r, Theme.RField);
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
            //  ★ 2026-10-24 第 138 轮：14px 在 32px 行高里占比太小，看着"弱"；
            //  未勾选用 Field（浅灰）填充，在白卡上和底色几乎没差别，看不清是个框。
            //  改成 16px + 白底 + 1.2px 灰边，圆角收到 3（macOS checkbox 是 3/14 ≈ 21%）。
            int s = Theme.S(16);
            int y = (Height - s) / 2;
            var box = new Rectangle(0, y, s, s);
            Theme.FillRound(g, box, Theme.S(3), Enabled ? (Checked ? Theme.Accent : Theme.Panel) : Theme.LineSoft);
            Theme.EdgeRound(g, box, Theme.S(3),
                Checked ? Theme.Accent : ((hover && Enabled) ? Theme.Accent : Theme.Line), 1.2f);
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
            Theme.DrawTxt(g, Text, Font, new Rectangle(Theme.S(20), 0, Width - Theme.S(20), Height),
                Enabled ? Theme.Text : Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            //  焦点环跟着勾选框本体（外扩 3px），不画整行 —— 行高 20px 画整行会挤到相邻行
            if (Focused && ShowFocusCues)
                Theme.DrawFocusRing(g, new Rectangle(box.X - Theme.S(3), box.Y - Theme.S(3),
                    box.Width + Theme.S(6), box.Height + Theme.S(6)), Theme.S(7));
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

            Theme.DrawTxt(g, Text, Font, new Rectangle(Theme.S(20), 0, Width - Theme.S(20), Height),
                Enabled ? Theme.Text : Theme.TextDisabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                Theme.DrawFocusRing(g, new Rectangle(circle.X - Theme.S(3), circle.Y - Theme.S(3),
                    circle.Width + Theme.S(6), circle.Height + Theme.S(6)), Theme.S(9));
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
    //  现代 Fluent / Apple 风格菜单渲染：
    //  圆角白卡 + 发丝边 + 柔和半透明胶囊高亮 + 舒适的行距呼吸感。
    class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        Size lastRegion;
        public ThemedMenuRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (item is ToolStripMenuItem)
            {
                item.Padding = new Padding(Theme.S(12), Theme.S(5), Theme.S(12), Theme.S(5));
                item.Margin = new Padding(0, 1, 0, 1);
            }
            else if (item is ToolStripSeparator)
            {
                item.Margin = new Padding(Theme.S(8), Theme.S(3), Theme.S(8), Theme.S(3));
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color tc;
            bool isStatus = e.Item.Tag != null && (string)e.Item.Tag == "status";

            if (isStatus)
            {
                // 顶部状态展示：有游戏运行时鲜绿，待命时典雅灰
                tc = (e.Text.IndexOf("游戏中") >= 0 || e.Text.IndexOf("⚡") >= 0)
                    ? Theme.TextSafe(Theme.Ok)
                    : Theme.TextDim;
            }
            else
            {
                tc = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
                if ((e.Item.Selected || e.Item.Pressed) && e.Item.Enabled)
                {
                    tc = Theme.Dark ? Color.FromArgb(255, 255, 255) : Theme.Text;
                }
            }

            var g = e.Graphics;
            var oldHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            try
            {
                var rect = e.TextRectangle;
                rect.X += Theme.S(2);
                Theme.DrawTxt(g, e.Text, e.TextFont, rect, tc, e.TextFormat);
            }
            finally
            {
                g.TextRenderingHint = oldHint;
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var ts = e.ToolStrip;
            int w = Math.Max(1, ts.Width - 1);
            int h = Math.Max(1, ts.Height - 1);
            var r = new Rectangle(0, 0, w, h);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Theme.FillRound(g, r, Theme.S(8), Theme.Panel);
            Theme.EdgeRound(g, r, Theme.S(8), Theme.CardEdge, 1f);
            if (ts.Region == null || lastRegion != ts.Size)
            {
                try
                {
                    using (var gp = Theme.Round(new Rectangle(0, 0, ts.Width, ts.Height), Theme.S(8))) ts.Region = new Region(gp);
                    lastRegion = ts.Size;
                }
                catch { }
            }
        }

        // 高亮：Win11 风格柔和半透明微圆角胶囊 (R=5)，状态项不高亮
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            bool isStatus = e.Item.Tag != null && (string)e.Item.Tag == "status";
            if (isStatus || (!e.Item.Selected && !e.Item.Pressed) || !e.Item.Enabled) return;
            var r = new Rectangle(Theme.S(4), 1, Math.Max(1, e.Item.Width - Theme.S(8)), Math.Max(1, e.Item.Height - 2));
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color hov = Theme.Dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);
            Theme.FillRound(g, r, Theme.S(5), hov);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            Color lineCol = Theme.Dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
            using (var p = new Pen(lineCol))
                e.Graphics.DrawLine(p, Theme.S(8), y, Math.Max(Theme.S(8), e.Item.Width - Theme.S(8)), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    }

    //  统一的右键菜单外观：圆角白卡 + 自绘高亮 + 原生 DWM 阴影与圆角。
    static class MenuFactory
    {
        public static ContextMenuStrip New()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = false;
            m.Font = Theme.F(9.5f);
            m.BackColor = Theme.Panel;
            m.ForeColor = Theme.Text;
            m.MinimumSize = new Size(Theme.S(140), 0);
            m.Padding = new Padding(Theme.S(4), Theme.S(5), Theme.S(4), Theme.S(5));
            m.DropShadowEnabled = true;
            m.HandleCreated += delegate {
                try { Theme.ApplyChrome(m.Handle); } catch { }
            };
            Fx.FadeInMenu(m);
            return m;
        }
    }

    //  弹层动效统一入口（2026-10-29 第 165 轮，对应审计批 2）：
    //   · FadeOnClose —— 对话框关闭淡出：FormClosing 拦截，覆盖按钮/ESC/Alt+F4 全部关闭路径
    //   · FadeInMenu —— 右键菜单淡入：挂在 MenuFactory，6 处一次生效
    //  都判 Motion.Enabled（系统关动画 / 设置 off 时零行为）。用 Timer 步进而不用 Anim：
    //  Anim 没有完成回调，而"淡出结束必须真的 Close"需要回调语义。
    static class Fx
    {
        // ---- 对话框关闭淡出（8×25 = 200ms，Accel = 退出用加速曲线）----
        public static void FadeOnClose(Form dlg)
        {
            bool fading = false;
            dlg.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (fading || !Motion.Enabled || !dlg.IsHandleCreated || dlg.IsDisposed) return;
                e.Cancel = true;                  // 先拦住这次 Close，淡完再真关
                fading = true;
                //  ★ 2026-10-31 修复（save_dialog_probe §1 复现的保存丢失 bug）：
                //    保存按钮刚把 DialogResult 设为 OK，而 WinForms 对**模态窗**的 Close()
                //    会把它改写成 Cancel —— 淡出收尾若直接 dlg.Close()，ShowDialog 就返回
                //    Cancel，调用方 `!= OK 则 return` → 保存分支被静默跳过（不落盘、无日志），
                //    用户看到的就是"填了参数，它自己会擦掉"。
                //    记住按钮结果，收尾用它关窗；点 X（此刻仍是 None）按取消处理。
                DialogResult keep = dlg.DialogResult;
                var t = new Timer { Interval = 25 };
                int step = 0;
                t.Tick += delegate
                {
                    if (dlg.IsDisposed) { t.Stop(); t.Dispose(); return; }
                    step++;
                    double x = step / 8.0;
                    if (x >= 1.0)
                    {
                        t.Stop(); t.Dispose();
                        if (dlg.Modal)
                        {
                            // 模态窗：设置 DialogResult 即以此结果关窗，避开会改写结果的 Close()
                            dlg.DialogResult = keep == DialogResult.None ? DialogResult.Cancel : keep;
                        }
                        else dlg.Close();   // 非模态：DialogResult 没有关窗语义，维持原路径
                        return;
                    }
                    dlg.Opacity = 1.0 - 0.98 * Motion.Ease(Motion.Accel, x);
                };
                t.Start();
            };
        }

        // ---- 菜单淡入（9×12 ≈ 108ms，Decel = 进入用减速曲线）----
        public static void FadeInMenu(ContextMenuStrip m)
        {
            m.Opening += delegate { if (Motion.Enabled && !m.IsDisposed) m.Opacity = 0; };
            m.Opened += delegate
            {
                if (!Motion.Enabled) return;
                var t = new Timer { Interval = 12 };
                int step = 0;
                t.Tick += delegate
                {
                    if (m.IsDisposed) { t.Stop(); t.Dispose(); return; }
                    step++;
                    double x = step / 9.0;
                    if (x >= 1.0) { t.Stop(); t.Dispose(); m.Opacity = 1.0; return; }
                    m.Opacity = Motion.Ease(Motion.Decel, x);
                };
                t.Start();
            };
        }
    }

    // ===================== 圆角控件层（v2.6.0） =====================
    //  统一圆角来源：Theme.RCard / RBtn / RField / RPic。
    //  为什么不用原生控件：WinForms 的 PictureBox / Button / TextBox 都只能画直角，
    //  而「边角太锋利」正是用户对 2.5.1 的直接反馈，所以这几个必须自绘。


    // 圆角侧栏导航项：选中态 = 强调色软底胶囊 + 左侧发光条 + 专属矢量几何图标

    // 圆角输入框宿主：TextBox 自身画不了圆角，套一层自绘外框
    class RoundField : Panel
    {
        public readonly TextBox Box;
        bool focus;

        //  占位提示是**写进 TextBox 的真文本**（见构造函数），所以读用户输入不能用 Box.Text——
        //  否则"搜索游戏…"会被当成搜索词，把列表过滤成空（2026-10-18 实测）。
        public string RealText { get { return hintOn ? "" : (Box.Text ?? ""); } }

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
            //  TextBox 是矩形子控件，会整块盖在圆角框上 —— 底色必须与外框填充同一档，
            //  否则白框里会露出一块灰矩形（2026-10-22 用户截图"搜索框周边有瑕疵"）。
            Box.BackColor = Theme.FieldOn(BackColor);
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.F(10f);         // 输入值 13.3px（第 158 轮 9.5→10，与 14px 正文差半档）
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
                Box.ForeColor = Theme.TextDim;
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
                    Box.ForeColor = Theme.TextDim;
                    Box.Text = hintText;
                };
            }
        }

        public void SetCorner(Color c)
        {
            BackColor = c;
            if (Box != null) Box.BackColor = Theme.FieldOn(c);   // 外框换了底色，内嵌 TextBox 要跟着换
            Invalidate();
        }

        string hintText = "";
        bool hintOn;
        public string Query { get { return hintOn ? "" : Box.Text.Trim(); } }

        //  从外部灌值。**必须走这里，不要直接改 Box.Text**：
        //  占位提示是"写进 TextBox 的真文本 + hintOn 标记"，直接赋 Box.Text 会让 hintOn 停在 true，
        //  于是 RealText/Query 永远返回空 —— 用户看到框里有字、程序读到的却是空（搜索框踩过这个坑）。
        public void SetText(string s)
        {
            s = s == null ? "" : s;
            //  传空串 = 回到占位提示态（不是"清空后留个空框"）：调用方多半是"把已有值灌进来"，
            //  没有值时应该还能看到提示该怎么填。
            if (s.Length == 0 && hintText.Length > 0)
            {
                hintOn = true;
                Box.ForeColor = Theme.TextDim;
                Box.Text = hintText;
            }
            else
            {
                hintOn = false;
                Box.ForeColor = Theme.Text;
                Box.Text = s;
            }
            Invalidate();
        }

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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.RField, Theme.FieldOn(BackColor));
            //  未聚焦不描边（2026-10-20）：填充色本身已经划出边界，再套一圈灰线就成了
            //  "白底上贴了个控件"。聚焦时才画环。
            //  ★ 2026-10-21：单根 1.6px 实线 → Apple 的"柔光圈"：外圈 3px 低透明度打底、
            //   内圈 1px 实线收边。控件的矩形会裁掉描边外侧的那一半，所以两圈都画在边界内，
            //   合起来的观感是 2~3px 的柔和光圈，而不是一条会"咬"住输入框的硬线。
            if (focus)
            {
                var rGlow = new Rectangle(2, 2, Width - 5, Height - 5);
                Theme.EdgeRound(g, rGlow, Math.Max(2, Theme.RField - 2), Theme.Alpha(Theme.Accent, 0.32), 3f);
                Theme.EdgeRound(g, r, Theme.RField, Theme.Accent, 1.2f);
            }

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
    //  public 是因为 Pg.AddHead 现在返回它（hero 需要页面拿到页头去更新封面/标题），
    //  而 Pg 是 public —— internal 的返回类型会直接编译不过（CS0050）。
    public class HeadBox : Panel
    {
        string title;
        string sub;

        // ---- Steam 式大横幅（hero）----
        //  普通页头是 58px 的白条 + 标题；Hero 模式是 168px 的封面横幅：
        //  封面铺满 + 底部压暗 + 大号标题压在左下角。这是"像不像 Steam"最关键的一块。
        //  默认 false —— 只有需要"当前游戏"语义的页面才开，其它页面完全不受影响。
        public bool Hero;
        public Image HeroCover;
        //  设计高度（构造/构建时定下的那条基线）。Pg 的"内容装不满就撑高页头"从它算起 ——
        //  直接读 Height 会累加，切一次页签页头就长一截。
        public int BaseH;
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
            //   连成一个无缝横带。2026-10-22 统一底色后整片都是 Bg，页头也不再是白条。
            BackColor = Theme.Bg;
            //  58 → 66（2026-10-20）：20pt 标题要 40px、9.5pt 副题要 19px，两者合计 59px
            //  硬塞进 58px 的页头 = 标题顶着导航的绿线、副题贴着卡片，就是用户说的"突兀"。
            //  加 8px 让整块有上下呼吸，左栏标题行同步（见 BuildGameSide 的 RowStyles）。
            Height = Theme.S(66);
            BaseH = Height;
        }
        public string Sub
        {
            get { return sub; }
            set { sub = value == null ? "" : value; Invalidate(); }
        }
        // hero 内容更新（页面切换选中游戏时调用）
        public void SetHero(string t, string s, Image cover)
        {
            title = t == null ? "" : t;
            sub = s == null ? "" : s;
            HeroCover = cover;
            Invalidate();
        }

        //  hero 下方的模糊渐隐带（Steam 详情页的做法）：同一张图的底部区域模糊后向下淡出，
//  把"被裁断的横图"和下面的内容区接起来，看起来像画面在往下延伸。
        public int FogH;
        //  整幅（横幅 + 底部渐隐带）合成缓存。
        //  为什么必须"一次合成"：以前横幅画一遍、渐隐带再用另一次缩放的副本画一遍，
        //  两边的裁切窗不一致 → 交界处同一张画换了个位置又出现一遍。用户 2026-10-19 截图
        //  原话「你这是啥啊，咋还重复显示」就是它。从同一张位图里取像素，几何天然连续。
        Bitmap heroCache;
        Image heroSrc;
        int heroW, heroTotal;
        //  hero 底部操作条（Steam：按钮压在图上，不是单独一条白色区域）
        public string Meta = "";       // 按钮右侧那行小字（平台 · 推荐方案）
        public Action Play;            // 点「开始游戏」
        //  右端状态（"当前未接入（游戏跑原生）"之类）：用来填满按钮那一行的右半边，
        //  否则一条横幅的左下角挤着三样东西、右边两米宽全是空的。
        public string Note = "";
        public Color NoteColor = Theme.Ok;
        //  标题字号：Steam 的库横幅标题很"重"，30pt 在 900px 宽的横幅里偏小、显空。
        //  34pt 配 134×34 的按钮，"开始游戏"大约是标题的三分之一宽，比例才对。
        public float TitlePt = 34f;
        bool btnHot, btnDown;
        //  入场补间：整页切换时页头文字淡入 + 微微上移（M3 enter = 200ms / emphasized.decelerate）。
        //  为什么只做页头：页面里其它内容都是**子控件**，WinForms 的子控件没有"整体透明度"
        //  （BackColor 的 alpha 会被直接忽略），做不了真淡入；页头是自己画的文字，颜色能带 alpha。
        //  规范见 docs/motion-and-ui-research.md 5.1「页面/顶层切换：快速 fade，不要滑入」——
        //  这里的 7px 位移只是给淡入配一点方向感，不是"滑入"。
        Anim aEnter;
        public void PlayEnter()
        {
            if (aEnter == null) aEnter = new Anim(this, Motion.Normal, Motion.EmphDec);
            aEnter.Snap(0.0);
            aEnter.To(1.0);
        }

        int HeroH { get { return Math.Max(Theme.S(80), Height - FogH); } }
        //  底部内容块的位置只在这一处算，绘制与命中测试共用（改过一版按钮点不动，
        //  就是因为 PlayRect 和 OnPaint 各算各的，差了几像素）。
        int PadX { get { return Theme.S(22); } }
        int BtnW { get { return Theme.S(134); } }
        int BtnH { get { return Theme.S(34); } }
        Rectangle PlayRect()
        {
            //  下沿距图片底 24px：再往下压到渐隐带上（用户原话"开始游戏别靠下"），
            //  再往上抬，按钮和渐隐带之间就会空出一大片
            return new Rectangle(PadX, HeroH - Theme.S(24) - BtnH, BtnW, BtnH);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = Meta.Length > 0 && PlayRect().Contains(e.Location);
            if (h != btnHot) { btnHot = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (btnHot) { btnHot = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Meta.Length > 0 && PlayRect().Contains(e.Location)) { btnDown = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (btnDown)
            {
                btnDown = false;
                Invalidate();
                if (Meta.Length > 0 && PlayRect().Contains(e.Location) && Play != null) Play();
            }
            base.OnMouseUp(e);
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (reflowTimer != null) { try { reflowTimer.Stop(); reflowTimer.Dispose(); } catch { } reflowTimer = null; }
                if (heroCache != null) { try { heroCache.Dispose(); } catch { } heroCache = null; }
            }
            base.Dispose(disposing);
        }

        // ===================== hero 合成（一次画完）=====================
        //
        //  这一块重写过三次，把踩过的坑钉在这里：
        //   ① 第一版"清晰图 + 另一张缩小的模糊图"两层叠 —— 模糊度是阶跃的，交界一眼看出。
        //   ② 第二版"横幅画一遍、渐隐带再用另一个缩放的副本画一遍" —— 两次的裁切窗不同，
        //      带子里出现的是画面**另一个位置**的同一段内容，看起来就是"画面重复了一遍"。
        //   ③ 第三版"整块一次合成" —— 横幅和带子取自同一张位图，几何天然连续；
        //      带子里做「清晰 → 模糊 → 页面底色」的逐行过渡，两端斜率都归零，看不见起止。
        //
        //  还有一个必须记住的收尾：带子的终点是 Theme.Bg（页面底色），不是 Theme.Panel（白）。
        //  取错的话横幅下面会先白后灰，多出一道硬边（2026-10-19 取样实测：254,254,254 → 233,237,243）。

        /// <summary>把「横幅 + 渐隐带」整块渲染成一张位图。失败返回 null（调用方走纯色兜底）。</summary>
        static Bitmap Compose(Image src, int w, int heroH, int fogH)
        {
            int H = heroH + fogH;
            if (src == null || w <= 0 || H <= 0) return null;
            Bitmap bmp;
            try { bmp = new Bitmap(w, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
            catch { return null; }
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Theme.Bg);                        // 底边收到它，接缝自然消失
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    //  cover 裁切：按"横幅 + 渐隐带"整块算缩放，保证带子里是真画面而不是空白。
                    //  焦点保护：角色头部与核心焦点位于上部 12%~35%，多出高度适度顶对齐，绝不削头。
                    float sc = Math.Max((float)w / src.Width, (float)H / src.Height);
                    int dw = (int)Math.Round(src.Width * sc), dh = (int)Math.Round(src.Height * sc);
                    int extraH = Math.Max(0, dh - H);
                    int top = Math.Min(Theme.S(80), (int)Math.Round(extraH * 0.12));
                    g.DrawImage(src, new Rectangle((w - dw) / 2, -top, dw, dh));

                    //  压暗：**一条**曲线跨整块。分成两段画的话，两条的起点/终点对不上，
                    //  交界处必留一道色阶（用户截图里那道横线就是这么来的）。
                    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, H), Color.Black, Color.Black, 90f))
                    {
                        var cb = new ColorBlend(5);
                        cb.Colors = new Color[] {
                            Color.FromArgb(0, 8, 12, 18),          // 顶部：完全不压
                            Color.FromArgb(0, 8, 12, 18),
                            Color.FromArgb(150, 8, 12, 18),        // 标题上沿
                            Color.FromArgb(196, 8, 12, 18),        // 按钮下沿
                            Color.FromArgb(196, 8, 12, 18) };      // 带子里一直压到底
                        cb.Positions = new float[] {
                            0f,
                            (float)(heroH * 0.30) / H,
                            (float)Math.Max(1, heroH - Theme.S(136)) / H,
                            (float)Math.Max(2, heroH - Theme.S(30)) / H,
                            1f };
                        lg.InterpolationColors = cb;
                        g.FillRectangle(lg, 0, 0, w, H);
                    }
                }
                if (fogH > 0) SoftenTail(bmp, w, heroH, fogH);
                return bmp;
            }
            catch
            {
                try { bmp.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>把位图底部 fogH 行做成「清晰 → 模糊 → 页面底色」的渐进过渡（就地修改）。</summary>
        static void SoftenTail(Bitmap src, int w, int heroH, int fogH)
        {
            int H = heroH + fogH;
            Bitmap blur = null, band = null;
            try
            {
                //  便宜的高斯近似：整块缩到 1/10 再双三次放回来。整块一起缩，
                //  带子顶部才有正确的上下文，不会"糊得跟上面不一样"。
                int sw = Math.Max(8, w / 10), sh = Math.Max(4, H / 10);
                blur = new Bitmap(w, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var small = new Bitmap(sw, sh))
                {
                    using (var g1 = Graphics.FromImage(small))
                    {
                        g1.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g1.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g1.DrawImage(src, new Rectangle(0, 0, sw, sh));
                    }
                    using (var g2 = Graphics.FromImage(blur))
                    {
                        //  这里是"把 1/10 的小图放大回去"，双线性与双三次肉眼无差，
                        //  但双三次要慢一倍多（实测 905 宽 8.6ms → 3.1ms）。
                        g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.DrawImage(small, new Rectangle(0, 0, w, H));
                    }
                }

                band = new Bitmap(w, fogH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(band))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;

                    //  ① 清晰 → 模糊：只有带子前 55% 需要逐行混合，后面整段一次贴模糊版。
                    //     性能账：逐行混 92×2=184 次 DrawImage ≈ 12ms，砍到 50×2+1 ≈ 7ms。
                    //  模糊在 70% 处就完成（原来是 55%），给"模糊 → 底色"留出更长的下坡
                    int rampEnd = (int)Math.Round((fogH - 1) * 0.70);
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        for (int i = 0; i <= rampEnd; i++)
                        {
                            double t = (double)i / Math.Max(1, fogH - 1);
                            double m = Math.Min(1.0, t / 0.70); m = m * m * (3 - 2 * m);
                            int y = heroH + i;

                            var cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix33 = (float)m;
                            ia.SetColorMatrix(cm);
                            g.DrawImage(blur, new Rectangle(0, i, w, 1), 0, y, w, 1, GraphicsUnit.Pixel, ia);

                            cm = new System.Drawing.Imaging.ColorMatrix();
                            cm.Matrix33 = (float)(1 - m);
                            ia.SetColorMatrix(cm);
                            g.DrawImage(src, new Rectangle(0, i, w, 1), 0, y, w, 1, GraphicsUnit.Pixel, ia);
                        }
                    }
                    int restY = rampEnd + 1, restH = fogH - restY;
                    if (restH > 0)
                        g.DrawImage(blur, new Rectangle(0, restY, w, restH),
                                    0, heroH + restY, w, restH, GraphicsUnit.Pixel);

                    //  ② 往页面底色收：**一次** alpha 渐变填充，而不是逐行算 alpha。
                    //     smoothstep 用 9 个节点逼近（两点之间线性插值），所以既快又不起色带。
                    Color bg = Theme.Bg;
                    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, fogH)),
                                                           bg, bg, 90f))
                    {
                        var cb = new ColorBlend(9);
                        cb.Positions = new float[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f };
                        int[] a9 = new int[] { 0, 14, 40, 81, 128, 174, 215, 241, 255 };
                        var cols = new Color[9];
                        for (int i = 0; i < 9; i++) cols[i] = Color.FromArgb(a9[i], bg);
                        cb.Colors = cols;
                        lg.InterpolationColors = cb;
                        g.FillRectangle(lg, 0, 0, w, fogH);
                    }
                }
                using (var g = Graphics.FromImage(src))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(band, new Rectangle(0, heroH, w, fogH), 0, 0, w, fogH, GraphicsUnit.Pixel);
                }
            }
            catch { }
            finally
            {
                if (blur != null) blur.Dispose();
                if (band != null) band.Dispose();
            }
        }

        //  改宽度时重建一次要 ~18ms（缩图 + 模糊 + 压暗）。拖窗口边框时系统一秒能发几十次
        //  尺寸事件，逐次重建会把 UI 线程吃掉、画面一顿一顿。做法：拖动期间照旧用**旧缓存**
        //  拉伸顶着，停手 70ms 后再重建一张清晰的 —— 拖动中那点轻微拉伸肉眼基本看不出来。
        Timer reflowTimer;
        bool cacheStale;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!Hero) return;
            cacheStale = true;
            if (reflowTimer == null)
            {
                reflowTimer = new Timer();
                reflowTimer.Interval = 70;
                reflowTimer.Tick += delegate
                {
                    reflowTimer.Stop();
                    cacheStale = false;
                    Invalidate();
                };
            }
            reflowTimer.Stop();
            reflowTimer.Start();
        }

        void EnsureCache(int heroH, int fogH)
        {
            int H = heroH + fogH;
            bool ok = heroCache != null && heroW == Width && heroTotal == H && object.ReferenceEquals(heroSrc, HeroCover);
            if (ok) return;
            //  拖动中且手上已有一张 → 先不重建（PaintHero 会拉伸旧图顶上）
            if (cacheStale && heroCache != null) return;
            if (heroCache != null) { try { heroCache.Dispose(); } catch { } heroCache = null; }
            heroSrc = HeroCover; heroW = Width; heroTotal = H;
            if (HeroCover != null) heroCache = Compose(HeroCover, Width, heroH, fogH);
        }

        void PaintHero(Graphics g)
        {
            //  画风一致（2026-10-22 用户："上面棱角分明，下面圆滑"）：
            //  hero 自己也是一张卡片 —— 四角 RCard 圆角，由 Pg 布局把它内缩出一圈边距。
            var clipOld = g.Clip;
            using (var clipPath = Theme.Round(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Theme.RCard))
                g.SetClip(clipPath);
            try { PaintHeroCore(g); }
            finally { g.Clip = clipOld; clipOld.Dispose(); }
        }

        void PaintHeroCore(Graphics g)
        {
            if (Width <= 0 || Height <= 0) return;
            int heroH = Math.Max(Theme.S(80), Height - FogH);
            int fogH = Math.Max(0, Height - heroH);
            int H = heroH + fogH;
            EnsureCache(heroH, fogH);

            if (heroCache != null)
            {
                //  ⚠ 左移 1px（2026-10-22）：hero 贴在最左，与侧栏之间有一条 1px 缝，
                //  深色图上会显成一条亮竖线（用户截图"页头那条竖线"）。画的时候往左溢出 1px 盖住它。
                if (heroCache.Width == Width && heroCache.Height == H) g.DrawImageUnscaled(heroCache, -1, 0);
                else
                {
                    var old2 = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(heroCache, new Rectangle(0, 0, Width, H));
                    g.InterpolationMode = old2;
                }
            }
            else
            {
                //  没封面（还没选游戏 / 分享版）：从页面白接到页面底色，
                //  上沿贴顶栏、下沿贴页面，两头都不留边
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, H)),
                                                       Theme.Panel, Theme.Bg, 90f))
                    g.FillRectangle(b, 0, 0, Width, H);
            }

            // ★ 电影级双层保护遮罩（Dark Scrim · 2026-10-30 重构）：
            //   彻底解决角色立绘被 30pt 大字暴力遮挡面部的问题。
            //   ① 底部垂直渐变：从卡片中下段平滑过渡到深色，保护按钮与底部状态；
            //   ② 左侧水平渐变：在标题与行动区铺垫一层非线性暗色，字符边缘清晰可读，
            //      而右侧角色立绘主体（如人物脸部与身姿）保持完全透亮。
            if (heroCache != null)
            {
                // ① 底部暗度渐变 (Vertical Scrim): 38% -> 100%
                // ⚠ GDI+ 默认 WrapMode.Tile 在渐变边缘由于浮点取样会把底部 230 深色误卷回顶部边缘形成 1px 黑线，
                //    这里外扩画刷矩形并设置 WrapMode.TileFlipY，彻底根治封面 38% 高度处出现黑线的瑕疵。
                int scrimTop = (int)(H * 0.38f);
                int scrimH = H - scrimTop;
                if (scrimH > 0 && Width > 0)
                {
                    using (var vBrush = new LinearGradientBrush(
                        new Rectangle(0, Math.Max(0, scrimTop - 2), Width, scrimH + 4),
                        Color.Transparent, Color.FromArgb(220, 12, 13, 16), 90f))
                    {
                        vBrush.WrapMode = WrapMode.TileFlipY;
                        var cb = new ColorBlend(4);
                        cb.Positions = new float[] { 0f, 0.35f, 0.75f, 1f };
                        cb.Colors = new Color[] {
                            Color.FromArgb(0, 12, 13, 16),
                            Color.FromArgb(60, 12, 13, 16),
                            Color.FromArgb(160, 12, 13, 16),
                            Color.FromArgb(230, 12, 13, 16)
                        };
                        vBrush.InterpolationColors = cb;
                        g.FillRectangle(vBrush, 0, scrimTop, Width, scrimH);
                    }
                }

                // ② 左侧暗角渐变 (Horizontal Scrim): x: 0 -> Width * 0.62
                int leftScrimW = Math.Min(Width, Math.Max(Theme.S(360), (int)(Width * 0.62f)));
                if (leftScrimW > 0 && H > 0)
                {
                    using (var hBrush = new LinearGradientBrush(
                        new Rectangle(-2, 0, leftScrimW + 4, H),
                        Color.FromArgb(180, 10, 11, 14), Color.Transparent, 0f))
                    {
                        hBrush.WrapMode = WrapMode.TileFlipX;
                        var cbH = new ColorBlend(4);
                        cbH.Positions = new float[] { 0f, 0.40f, 0.75f, 1f };
                        cbH.Colors = new Color[] {
                            Color.FromArgb(180, 10, 11, 14),
                            Color.FromArgb(130, 10, 11, 14),
                            Color.FromArgb(50, 10, 11, 14),
                            Color.FromArgb(0, 10, 11, 14)
                        };
                        hBrush.InterpolationColors = cbH;
                        g.FillRectangle(hBrush, 0, 0, leftScrimW, H);
                    }
                }
            }

            //  ---- 底部内容块：标题压在按钮上方，整块贴着图片下沿往上排 ----
            //  ⚠ 标题以前写死 `Theme.S(46)` 的框 + `TextFormatFlags.Bottom` —— 30pt 的
            //    Noto Sans SC 行高是 58px，46px 的框直接**把字的下半截裁掉**（用户截图
            //    "游戏标题字体显示不全"）。现在一律用 Font.Height 当框高，从根上不可能裁。
            var pr = PlayRect();
            int padX = PadX;

            Font fTitle = Theme.FB(TitlePt);
            int tH = fTitle.Height;
            int tY = pr.Top - Theme.S(6) - tH;
            int maxTitleW = Math.Max(Theme.S(10), Math.Min(Width - padX * 2, Math.Max(Theme.S(280), (int)(Width * 0.58f))));
            var tRect = new Rectangle(padX, tY, maxTitleW, tH);

            //  白字压在高饱和封面上会"糊进去"——保留 1px 暗投影（这是**可读性**保障：
            //  实测这版封面标题区背景亮度中位 26（8.5:1），但局部亮块只有 2.5:1），
            //  但投影和正文都走灰度抗锯齿（2026-10-23）：以前两条都走 ClearType，
            //  在照片上会各带一圈彩边，放大看像"字被描了彩边"。
            Theme.DrawPhotoText(g, title, fTitle,
                new Rectangle(tRect.X + 1, tRect.Y + 1, tRect.Width, tRect.Height),
                Color.FromArgb(140, 0, 0, 0));
            Theme.DrawPhotoText(g, title, fTitle, tRect, Color.White);

            if (sub.Length > 0 && Meta.Length == 0)
                Theme.DrawTxt(g, sub, Theme.F(9.5f),
                    new Rectangle(padX + Theme.S(1), tY + tH, Math.Max(1, Width - padX * 2), Theme.S(20)),
                    Color.FromArgb(198, 226, 232, 240),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (Meta.Length > 0)
            {
                //  ① 开始游戏（叠在图上，不是单独一条白块）
                Color bg = btnDown ? Theme.AccentDeep : (btnHot ? Theme.Ok : Theme.Accent);
                using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                    Theme.FillRound(g, new Rectangle(pr.X + 1, pr.Y + 2, pr.Width, pr.Height), Theme.S(6), b);
                Theme.FillRound(g, pr, Theme.S(6), bg);
                int tx = pr.X + Theme.S(16);
                using (var b = new SolidBrush(Color.White))
                    g.FillPolygon(b, new Point[] {
                        new Point(tx, pr.Y + Theme.S(11)),
                        new Point(tx, pr.Y + Theme.S(23)),
                        new Point(tx + Theme.S(9), pr.Y + Theme.S(17)) });
                //  按钮标签同样走灰度抗锯齿：ClearType 的字边彩边在饱和蓝底上最刺眼
                Theme.DrawPhotoText(g, "开始游戏", Theme.FB(9.5f),
                    new Rectangle(tx + Theme.S(15), pr.Y, pr.Width - Theme.S(28), pr.Height), Color.White);

                //  ② 右端状态：把这一行的右半边填上（以前整条右半边是空的）
                Font fNote = Theme.F(9f);
                int noteW = 0;
                if (Note.Length > 0)
                {
                    noteW = TextRenderer.MeasureText(g, Note, fNote, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width
                        //  +10 而不是 +4：NoPadding 量出来的宽和实际排版差几像素，
                        //  框子刚好等宽时 GDI 会判定"放不下"直接打省略号（实测踩过）
                        + Theme.S(10);
                    //  状态文案最长的一句有 14 个字：窄窗口下不给上限的话会把中间的
                    //  「平台 · 推荐方案」整段挤没，所以最多占 42% 宽，超出就省略
                    int cap = (int)Math.Round(Width * 0.42);
                    if (noteW > cap) noteW = cap;
                }
                int noteX = Width - padX - noteW;

                //  ③ 按钮与说明之间的细分隔线 —— 没有它，左边一小撮、右边一小撮，
                //    中间那片空就真的只是"空"；有了它就是"排过的三段式"。
                int divX = pr.Right + Theme.S(14);
                int divH = (int)Math.Round(pr.Height * 0.52);
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                    g.DrawLine(pen, divX, pr.Y + (pr.Height - divH) / 2, divX, pr.Y + (pr.Height + divH) / 2);

                //  ④ 中间：平台 · 推荐方案（放不下就省略，绝不压到右边状态上）
                int metaX = divX + Theme.S(14);
                int metaW = Math.Max(1, (noteW > 0 ? noteX - Theme.S(16) : Width - padX) - metaX);
                Theme.DrawTxt(g, Meta, Theme.F(9f),
                    new Rectangle(metaX, pr.Y, metaW, pr.Height),
                    Color.FromArgb(215, 226, 232, 240),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                if (Note.Length > 0)
                {
                    int dot = Theme.S(6);
                    using (var b = new SolidBrush(NoteColor))
                        g.FillEllipse(b, noteX - Theme.S(11), pr.Y + (pr.Height - dot) / 2, dot, dot);
                    Theme.DrawTxt(g, Note, fNote,
                        new Rectangle(noteX, pr.Y, noteW, pr.Height),
                        Color.FromArgb(225, 235, 242, 250),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                        | TextFormatFlags.EndEllipsis);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (Hero) { PaintHero(g); return; }

            // ★ 顶条收口（2026-09-20 二稿）：白 → 页面灰，跨**整高**的 smoothstep 缓动。
            //   演进：① 纯白铺到底 + 硬边 → "太生硬"；② 最后 28%（≈17px）线性落下 →
            //   用户仍说"色块颜色变化还是不够丝滑"。17px 在这个窗口尺度上就是一个"糊掉的小段"，
            //   而且线性渐变的起点是一行"起跑线"，眼睛抓得到。
            //   现在：渐变覆盖整个页头高度（58px），并且走 smoothstep（两端慢、中间快），
            //   起止两侧都察觉不到"从哪一行开始变"。
            //  统一底色（2026-10-22）：页头与内容区同色，原来的"白→灰整高渐变"变成
            //  从灰渐到灰 —— 没有两端也就没有过渡，直接铺满底色，不留一道悬空的灰带。
            using (var bgb = new SolidBrush(BackColor)) g.FillRectangle(bgb, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            //  页面标题 15.5f → 18.5f：原来和卡片标题（10f）拉不开层级，整页"没有主心骨"。
            //  字重 Medium → Regular、20 → 19（2026-10-20）：Medium 在 26px 上笔画齐粗，
            //  压得很"鼓"，配导航的绿线就成了"怼在一起"。大标题用常规字重反而从容
            //  （层级靠尺寸，不靠加粗）；字号略收一档，配合加高的页头把上下留出来。
            var rf = Theme.F(19f);
            //  ★ 框高必须用 Font.Height（2026-10-20）：Noto Sans SC 的行高约 1.48em，
            //    20pt 就是 ~40px —— 原来写死 S(30)，每页标题的下半截都被裁掉，裁剩的笔画
            //    远看像一排标点（快照里「性能优化.」的句点就是"化"的残腿）。
            //    hero 标题早就为同一个病改过 Font.Height（见 PaintHero 的注释），这里补齐。
            int th = rf.Height;
            //  副题同一规矩：9.5pt 的 Noto 要 19px，写死 16px 会把字头切平
            //  （6 倍放大下「电源 / GPU 调度」的上沿是一条直线切口）。
            var sf = Theme.F(9.5f);
            int sh = sf.Height;
            //  标题 + 副题整块在页头里垂直居中，不再从 S(1) 硬排
            int y0 = Math.Max(0, (Height - th - sh) / 2);
            // ★ 底铺满、文字缩进（2026-09-20）：底色铺满 Pg 全宽（见 Pg.LayoutOnce），
            //   文字仍按 GapSize 让出左边距，与下方卡片左边缘严格对齐。
            int padX = Theme.S(14);
            double ep = aEnter == null ? 1.0 : aEnter.Value;
            int rise = (int)Math.Round(Theme.S(7) * (1.0 - ep));
            Theme.DrawTxt(g, title, rf, new Rectangle(padX, y0 + rise, Width - padX, th),
                Theme.Alpha(Theme.Text, ep),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (sub.Length > 0)
                Theme.DrawTxt(g, sub, sf,
                    new Rectangle(padX + Theme.S(1), y0 + th - Theme.S(4) + rise, Width - padX, sh),
                    Theme.Alpha(Theme.TextDim, ep),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // Steam 库那种标签栏：纯文字 + 选中项下面一条 2px 强调色横条。
    //  为什么不用按钮：一排圆角按钮看起来是"筛选器/工具栏"，而 Steam 的标签表达的是"页面分区"。
    //  Apple 分段控件（2026-10-21）：主导航和帧生成页子页签共用这一套形态。
    //  以前是"纯文字 + 2px 强调色下划线"—— 那是浏览器页签的语言；
    //  Apple 的 segmented control 是：一条灰底胶囊轨道 + 一枚会滑动的白色浮块，
    //  选中段文字用正文色 —— **强调色不参与**：颜色只表达"容器"，不表达"选中"。
    //  滑块走 Anim（Normal + EmphDec，200ms），与切页的 PlayHeadEnter 同一节奏。
    //  库分类筛选（2026-10-23）：照 Steam 的库左栏 —— 左边是**分类**（全部 / 收藏 / 各平台 + 数量），
    //  右边才是游戏墙。以前左栏列的是游戏本身，和右边的墙重复一遍，
    //  用户的原话是"有了游戏库为什么下面还会有库主页"。
    public class LibFilter : Control
    {
        public string[] Labels = new string[0];
        public string[] Keys = new string[0];      // all / fav / steam / local …
        public int[] Counts = new int[0];
        public int Selected;
        int hot = -1;

        public LibFilter()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }

        public event EventHandler Picked;
        int RowH { get { return Theme.S(32); } }

        public void SetData(string[] labels, string[] keys, int[] counts)
        {
            Labels = labels; Keys = keys; Counts = counts;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int r = e.Y < 0 ? -1 : e.Y / RowH;
            if (r >= Labels.Length) r = -1;
            if (r != hot) { hot = r; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int r = e.Y < 0 ? -1 : e.Y / RowH;
            if (r >= 0 && r < Labels.Length && r != Selected)
            {
                Selected = r; Invalidate();
                if (Picked != null) Picked(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < Labels.Length; i++)
            {
                var row = new Rectangle(Theme.S(4), i * RowH + Theme.S(2), Width - Theme.S(8), RowH - Theme.S(4));
                bool sel = i == Selected;
                if (sel) Theme.FillRound(g, row, Theme.S(6), Theme.Alpha(Theme.AccentSoft, 1.0));
                else if (i == hot) Theme.FillRound(g, row, Theme.S(6), Theme.Hover);
                Color fg = sel ? Theme.Accent : Theme.Text;
                Theme.DrawTxt(g, Labels[i], Theme.F(12f),
                    new Rectangle(row.X + Theme.S(10), row.Y, row.Width - Theme.S(46), row.Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (Counts != null && i < Counts.Length)
                    //  ⚠ 计数用界面字体，不用 Theme.Num（Bahnschrift）：
                    //  实测在这个尺寸下 Bahnschrift 的数字**不落笔**（同一行中文正常、数字区整片空白）。
                    Theme.DrawTxt(g, Counts[i].ToString(), Theme.F(9f),
                        new Rectangle(row.Right - Theme.S(40), row.Y, Theme.S(32), row.Height),
                        sel ? Theme.Accent : Theme.TextDim,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    //  侧栏竖向菜单（2026-10-23）：照 FileButler 的 n-menu 造型 ——
    //  行高 36、圆角 8、图标 18 + 文字 13px、选中态用**淡填充 + 强调色文字**（不是滑块）。
    //  为什么从横向分段改成竖向：FileButler 的导航就在侧栏里，横向那一行是"网页 tab"的语言；
    //  改成竖向之后顶部那 52px 整行消失，页面直接从标题栏下方开始。
    public class NavMenu : Control
    {
        string[] items = new string[0];
        bool[] show;
        int selected;
        int hot = -1;

        public NavMenu()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }

        public int Selected { get { return selected; } }
        public event EventHandler SelectedChanged;

        public void SetItems(string[] names, bool[] showMask)
        {
            items = names ?? new string[0];
            show = showMask;
            Reflow();
        }

        //  行高 40（2026-10-29 对齐「自考工作台」实测：导航项 py-2.5 = 40px 高）。
        //  行与行之间不留缝（study 的导航项是连续堆叠的 hover 块）——原来 RowH 36 里每行只画 34，
        //  2px 的缝在浅色下把导航切成了"一条条"。
        int RowH { get { return Theme.S(40); } }

        public void Reflow()
        {
            int n = 0;
            for (int i = 0; i < items.Length; i++) if (show == null || show[i]) n++;
            Height = n * RowH + Theme.S(4);
            Invalidate();
        }

        public void SetSelected(int page)
        {
            if (page == selected) return;
            selected = page;
            Invalidate();
        }

        int PageAt(int y)
        {
            int slot = y < 0 ? -1 : y / RowH;
            int k = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (show != null && !show[i]) continue;
                if (k == slot) return i;
                k++;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int p = PageAt(e.Y);
            if (p != hot) { hot = p; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot != -1) { hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int p = PageAt(e.Y);
            if (p >= 0 && p != selected)
            {
                selected = p;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int y = Theme.S(2), k = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (show != null && !show[i]) continue;
                //  行铺满 RowH（2026-10-29 对齐 study：导航项连续堆叠，行间不留缝）
                var row = new Rectangle(0, y, Width, RowH);
                bool sel = i == selected;
                if (sel)
                    using (var b = new SolidBrush(Theme.Mix(Theme.Accent, Theme.Bg, 0.90)))
                        Theme.FillRound(g, row, Theme.RBtn, b);
                else if (i == hot)
                    using (var b = new SolidBrush(Theme.Hover))
                        Theme.FillRound(g, row, Theme.RBtn, b);

                Color fg = sel ? Theme.Accent : Theme.Text;
                Color ic = sel ? Theme.Accent : Theme.TextDim;
                //  图标 16（study 图标 16px）、行内缩进 12、文字跟图标间距 12 —— 与 study 导航项同构
                int iz = Theme.S(16);
                MainForm.DrawPageIcon(g, new Rectangle(Theme.S(12), y + (RowH - iz) / 2, iz, iz), i, ic);
                //  ★ 2026-10-29 再对齐 study（实测 14px/600 ≈ 10.5pt Medium）：侧栏是导航不是标题，
                //  字小一档 + Medium 字重才像 study 那种"安静的一列"。139 轮的 12pt regular 偏大偏轻。
                Theme.DrawTxt(g, items[i], Theme.FM(10.5f),
                    new Rectangle(Theme.S(40), y, Width - Theme.S(48), RowH), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis);
                y += RowH;
                k++;
            }
        }
    }

    public class Segmented : Control
    {
        string[] items = new string[0];
        bool[] show;                       // 分享版只露 3 项，但页码必须保持不变
        int selected;                      // 页码（可能是隐藏项，如分享版的仪表盘）
        int hoverPage = -1;
        readonly Anim aThumb;              // 滑块插值进度 0..1
        int fromSlot, toSlot;
        int[] slotOf;                      // 页码 -> 可见槽位（隐藏页 = -1）

        public Segmented()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(32);
            Cursor = Cursors.Hand;
            aThumb = new Anim(this, Motion.Normal, Motion.EmphDec);
            aThumb.Snap(1);
        }

        public void SetItems(string[] names, bool[] showMask)
        {
            items = names ?? new string[0];
            show = showMask;
            slotOf = new int[items.Length];
            int slot = 0;
            for (int i = 0; i < items.Length; i++) slotOf[i] = (show == null || show[i]) ? slot++ : -1;
            selected = Math.Max(0, Math.Min(selected, items.Length - 1));
            SnapThumb();
            PerformLayout2();
        }

        public int Selected { get { return selected; } }
        public event EventHandler SelectedChanged;

        // 程序化切换（Switch / 探针）：走动画 + 发事件。
        //  ⚠ 不能叫 Select —— 会撞 Control.Select()（焦点 API，CS0108 隐藏警告）。
        public void Pick(int page)
        {
            if (page < 0 || page >= items.Length || page == selected) return;
            selected = page;
            SlideTo(page);
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }

        // 静默同步（Switch 已经改完页面，这里只让控件跟上，不再发事件）
        public void SetSelected(int page)
        {
            if (page < 0 || page >= items.Length || page == selected) return;
            selected = page;
            SlideTo(page);
        }

        // 换主题重建后"当场证明选中态在"：把滑块的淡入重放一遍
        public void ReplayActive()
        {
            SnapThumb();
            aThumb.Snap(0.15);
            aThumb.To(1.0);
        }

        void SnapThumb()
        {
            fromSlot = toSlot = SlotOf(selected);
            aThumb.Snap(1);
        }

        void SlideTo(int page)
        {
            fromSlot = toSlot;                 // 从"当前画着的位置"出发，连点两下也是连续的
            toSlot = SlotOf(page);
            aThumb.Snap(0);
            aThumb.To(1.0);
        }

        int SlotOf(int page)
        {
            int sl = (slotOf != null && page >= 0 && page < slotOf.Length) ? slotOf[page] : page;
            if (sl >= 0) return sl;
            //  分享版兜底：选中页可能是隐藏页（如默认停在仪表盘）—— 滑块与命中都不允许出现负槽位，
            //  落到第一个可见段上。页码本身不变，Switch 会在启动时把选中页带到可见页。
            for (int i = 0; i < slotOf.Length; i++) if (slotOf[i] >= 0) return slotOf[i];
            return 0;
        }

        int SegW(int i)
        {
            //  不走 Graphics 的 MeasureText 重载：SetItems 在构造期就会调（主窗还没建句柄），
            //  CreateGraphics 会把句柄提前逼出来 —— 布局计算必须与句柄无关。
            return TextRenderer.MeasureText(items[i], Theme.FM(9.5f),
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Theme.S(28);
        }

        // 可见槽位 slot 的 X（含轨道内边距）
        int SlotX(int slot)
        {
            int x = Theme.S(2);
            for (int i = 0; i < items.Length; i++)
            {
                if (slotOf[i] < 0) continue;
                if (slotOf[i] >= slot) break;
                x += SegW(i);
            }
            return x;
        }

        int TrackW()
        {
            int w = Theme.S(4);
            for (int i = 0; i < items.Length; i++) if (slotOf[i] >= 0) w += SegW(i);
            return w;
        }

        void PerformLayout2()
        {
            int w = Math.Max(Theme.S(60), TrackW());
            if (Width != w) Width = w;
        }

        int TrackH { get { return Math.Min(Height, Theme.S(30)); } }
        int TrackY { get { return (Height - TrackH) / 2; } }

        Rectangle TrackRect()
        {
            return new Rectangle(Theme.S(2), TrackY, Math.Max(Theme.S(8), TrackW() - Theme.S(4)), TrackH);
        }

        Rectangle SlotRect(int slot)
        {
            int th = TrackH - Theme.S(6);
            return new Rectangle(SlotX(slot) + Theme.S(3), TrackY + Theme.S(3),
                                 SegW(PageAt(slot)) - Theme.S(6), th);
        }

        int PageAt(int slot)
        {
            for (int i = 0; i < slotOf.Length; i++) if (slotOf[i] == slot) return i;
            return 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, 0, 0, Width, Height);
            if (items.Length == 0 || slotOf == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var track = TrackRect();
            //  轨道：底色统一之后不能再拿 Panel 去调（Panel 与灰底无关了），
            //  按**自己的 BackColor** 往文字色方向压一档 —— 浅色差 18 级、深色差 20 级，刚好是一个"容器"。
            Color trackC = Theme.Mix(BackColor, Theme.Text, Theme.Dark ? 0.10 : 0.075);
            Theme.FillRound(g, track, track.Height / 2, trackC);

            // 滑块：白浮块 + 发丝边（Apple 的选中段不发光、不描强调色）
            double p = aThumb.Value;
            int x = fromSlot == toSlot ? SlotRect(toSlot).X
                  : SlotRect(fromSlot).X + (int)Math.Round((SlotRect(toSlot).X - SlotRect(fromSlot).X) * p);
            int segW = SlotRect(toSlot).Width;
            var thumb = new Rectangle(x, TrackY + Theme.S(3), segW, TrackH - Theme.S(6));
            //  深色主题下滑块必须比轨道**亮一档**（Apple 深色分段的"浮块=抬升面"）：
            //  Panel(#1F1F22) 压在 Field(#1A1A1D) 上只差 5 级，肉眼几乎认不出选中段；
            //  Hover(#2A2A2E) 差 16 级，刚好是"浮起来"的量。浅色维持纯白。
            Theme.FillRound(g, thumb, thumb.Height / 2, Theme.Dark ? Theme.Hover : Theme.Panel);
            Theme.EdgeRound(g, thumb, thumb.Height / 2, Theme.CardEdge, 1f);

            for (int i = 0; i < items.Length; i++)
            {
                if (slotOf[i] < 0) continue;
                var r = new Rectangle(SlotX(slotOf[i]), TrackY, SegW(i), TrackH);
                bool on = (i == selected);
                Theme.DrawTxt(g, items[i], Theme.FM(9.5f), r,
                    on ? Theme.Text : (i == hoverPage ? Theme.Text : Theme.TextDim),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        int HitPage(Point pt)
        {
            if (!TrackRect().Contains(pt)) return -1;
            for (int i = 0; i < items.Length; i++)
            {
                if (slotOf[i] < 0) continue;
                var r = new Rectangle(SlotX(slotOf[i]), TrackY, SegW(i), TrackH);
                if (r.Contains(pt)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            using (var g = CreateGraphics())
            {
                int i = HitPage(e.Location);
                if (i != hoverPage) { hoverPage = i; Invalidate(); }
            }
        }

        protected override void OnMouseLeave(EventArgs e) { hoverPage = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            using (var g = CreateGraphics())
            {
                int i = HitPage(e.Location);
                if (i < 0 || i == selected) return;
                Pick(i);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }
    }

    //  帧生成页的子页签（概览 / 参数 / 诊断）：现在就是 Segmented 的一层壳。
    //  保留原类名与公开面（Tabs / Selected / SelectTab / SelectedChanged）——
    //  探针（shell_probe）和主窗代码都按这套 API 走，壳一换里面全换。
    public class TabBar : Control
    {
        public string[] Tabs = new string[0];
        public int Selected;
        public event EventHandler SelectedChanged;
        readonly Segmented seg;

        public TabBar()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(40);
            Cursor = Cursors.Hand;

            seg = new Segmented();
            seg.Dock = DockStyle.Fill;
            seg.BackColor = Theme.Bg;
            seg.SelectedChanged += delegate
            {
                Selected = seg.Selected;
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            };
            Controls.Add(seg);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (seg != null && Tabs.Length > 0) seg.SetItems(Tabs, null);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (seg != null) seg.Enabled = Enabled;
        }

        //  兼容旧字段赋值（构造期 `tabsFg.Selected = curDetailTab` 在事件接线之前）：
        //  走 OnPaint 前的同步兜底，不在这里发事件。
        public void SyncSelected()
        {
            if (seg != null) seg.SetSelected(Selected);
        }

        public void SelectTab(int i)
        {
            if (i < 0 || i >= Tabs.Length || i == Selected) return;
            Selected = i;
            if (seg != null)
            {
                seg.SetItems(Tabs, null);
                seg.Pick(i);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, 0, 0, Width, Height);
            //  字段被直接赋值过（构造期/换主题重建）→ 这里兜底同步，再交给子控件画
            if (seg != null && seg.Selected != Selected) seg.SetSelected(Selected);
        }
    }

    // Steam 库左栏那种紧凑游戏列表：一行一个游戏（色块首字母 + 名称 + 平台/状态）。
    //  为什么不用 GameCard 网格当左栏：卡片 226×338，一屏只放得下两张，左栏要的是"能一眼扫完 28 个"。
    //  性能：只画文字和一个纯色方块，不解码封面位图 —— 滚动/切页不掉帧。
    // 左栏小图标：EXE 图标提取是 IO + GDI 重活（PrivateExtractIcons + 高质量缩放），
    //   绝不能放在 OnPaint 里 —— 13 行滚动时每帧 13 次提取会直接把滚动拖成幻灯片。
    //   这里做三件事：后台线程提取、进程内缓存、绘制期只查表。
    public static class GameGlyph
    {
        public const int Px = 28;
        static readonly Dictionary<string, Bitmap> done = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> dead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly object gate = new object();
        static int pending;

        public static int Pending { get { lock (gate) return pending; } }

        static string KeyOf(DlssgGame g)
        {
            if (g == null) return "";
            return ((g.Dir == null ? "" : g.Dir) + "|" + (g.Exe == null ? "" : g.Exe)).ToLowerInvariant();
        }

        //  绘制期只查表：排队中的条目返回 null，调用方画字母兜底（不会闪白）
        public static Bitmap Peek(DlssgGame g)
        {
            string k = KeyOf(g);
            if (k.Length == 0) return null;
            lock (gate)
            {
                Bitmap b;
                return done.TryGetValue(k, out b) ? b : null;
            }
        }

        //  列表填好后调一次：只排队，不等结果。
        //  只开**一个**消费者线程：库里 200 款时 200 个 ThreadPool 任务会把线程池占满，
        //  连扫描/封面下载这些后台活都会被拖住；单消费者顺序提取，慢一点但不抢资源。
        static readonly Queue<DlssgGame> todo = new Queue<DlssgGame>();
        static bool workerUp;

        public static void Prefetch(List<DlssgGame> items)
        {
            if (items == null) return;
            lock (gate)
            {
                foreach (DlssgGame g in items)
                {
                    string k = KeyOf(g);
                    if (k.Length == 0 || done.ContainsKey(k) || dead.Contains(k)) continue;
                    done[k] = null;              // 占位：防同一 exe 重复排队
                    todo.Enqueue(g);
                    pending++;
                }
            }
            Pump();
        }

        static void Pump()
        {
            lock (gate) { if (workerUp || todo.Count == 0) return; workerUp = true; }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    DlssgGame g;
                    lock (gate)
                    {
                        if (todo.Count == 0) { workerUp = false; return; }
                        g = todo.Dequeue();
                    }
                    ExtractOne(g);
                }
            });
        }

        static void ExtractOne(DlssgGame g)
        {
            string k = KeyOf(g);
            Bitmap made = null;
            try
            {
                string path = Resolve(g);
                if (path.Length > 0)
                {
                    Bitmap raw = GameIcon.Extract(path, 64);
                    if (raw != null) made = Bake(raw, Theme.S(Px));
                }
            }
            catch { }
            lock (gate)
            {
                pending--;
                done.Remove(k);
                if (made != null) done[k] = made; else dead.Add(k);
            }
        }

        //  解析 exe 全路径（2026-10-22 修）：以前只查"安装目录根层"，凡是 exe 在子目录的条目
        //  一律取不到图标，退化成首字母色块 —— 实测 PUBG 的主程序在
        //  `PUBG\TslGame\Binaries\Win64\TslGame.exe`（4 层深）、Wannabe 在
        //  `GalGod-1.0.0-win\GalGod.exe`（2 层深），两个都只有字母块。
        //  项目里早就有 `Lib.LaunchTargetOf`（手动指认 → 直拼 → 2 层浅搜 → 平台深找，带缓存与锁），
        //  "点启动"走的就是它 —— 图标应当和启动目标用**同一条路径**，否则会出现"能启动但没图标"。
        static string Resolve(DlssgGame g)
        {
            if (g == null) return "";
            try
            {
                string p = Lib.LaunchTargetOf(g);
                if (p.Length > 0) return p;
            }
            catch { }
            // 兜底：条目形态特殊（Dir 为空 / Exe 带相对路径）时，仍按老规矩在根层试一次
            string dir = g.Dir == null ? "" : g.Dir;
            string exe = g.Exe == null ? "" : g.Exe.Trim();
            if (dir.Length == 0 || exe.Length == 0) return "";
            try
            {
                string p = Path.Combine(dir, exe);
                return File.Exists(p) ? p : "";
            }
            catch { }
            return "";
        }

        //  裁圆角并缩到目标尺寸：边缘抗锯齿，圆角外透明（露出下层行底色）
        static Bitmap Bake(Bitmap src, int size)
        {
            if (src == null || size <= 0) return null;
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                using (var path = Theme.Round(new Rectangle(0, 0, size, size), Math.Max(2, Theme.S(6))))
                {
                    g.SetClip(path);
                    g.DrawImage(src, new Rectangle(0, 0, size, size));
                }
            }
            src.Dispose();
            return bmp;
        }
    }
    //  侧栏「游戏」快捷入口（2026-10-25 第 145 轮）
    //  ---------------------------------------------------------------------------
    //  缘起：252px 侧栏在 6 项导航下面空了 380px，到底只剩一个版本号 —— 用户截图原话
    //  「下面空一长串感觉空空的」。
    //  填装饰会有凑数感，所以填**真需求**：任何页面一步回到某个游戏，不用先切到「游戏库」
    //  再在封面墙里找（这正是这个工具的价值主张：少翻目录）。
    //  形态 = Steam 左栏的克制版：24×36 小封面 + 名字，行高 46。
    //  ⚠ 两件不做：
    //    ① 不画 exe 图标当行图标 —— 那一眼就是文件管理器（封面墙定稿时的判据）；
    //    ② 不挂"代理缺失 / 未生效"角标 —— 侧栏是导航，全库告警墙会把它变成待办清单。
    public class SideGameList : Control
    {
        public event Action<DlssgGame> Picked;
        public event Action MorePicked;      // "还有 N 款 ›" 被点（去游戏库）
        public string EmptyText = "扫描中…";   // 一条都没有时显示的话（由 RefreshSideGames 按库状态给）

        readonly List<DlssgGame> items = new List<DlssgGame>();
        readonly List<Image> covers = new List<Image>();     // 与 items 一一对应（可含 null）
        string selId = "";
        string lastSig = "";        // 上一批游戏的签名（相同就不重取封面）
        int hot = -1;

        //  行高 42（不是 46）：导航行是 36，列表带 36px 高的封面本来就得更高，
        //  但 46 会让侧栏"上紧下松"—— 两段节奏差 10px，整列看着像拼的。
        //  42 = 封面 36 + 上下各 3，与导航只差 6px，读起来还是一列。
        int RowH { get { return Theme.S(42); } }
        int CovW { get { return Theme.S(24); } }
        int CovH { get { return Theme.S(36); } }
        //  左基准（第 145 轮对齐）：与 NavMenu 共用同一条。
        //  导航那边是「块 x=0 / 图标 x=10 宽 18（中心 19）/ 文字 x=38」，
        //  这里小封面比图标宽 6px，所以**对齐视觉中心而不是左缘**：
        //  封面 24 从 x=7 起 → 中心 19，与图标中心同一条竖线；文字仍从 38 起。
        //  这样导航和列表看起来是一列，不是两套缩进（差 8px 在侧栏里一眼能看出"歪"）。
        int TagX { get { return Theme.S(38); } }
        int CovX { get { return Theme.S(7); } }
        int RowsFit { get { return Math.Max(0, (Height - Theme.S(4)) / RowH); } }
        //  放不下时最后一行让给「还有 N 款 ›」—— 否则用户会以为库里就这些。
        //  不做滚轮：侧栏是常驻导航，滚轮命中要求控件先拿到焦点，抢焦点会打断页面滚动。
        bool HasMore { get { return items.Count > RowsFit; } }
        int DrawRows { get { return Math.Min(items.Count, RowsFit); } }
        int MoreRow { get { return HasMore ? RowsFit - 1 : -1; } }

        public SideGameList()
        {
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
        }

        //  只换数据不换控件：刷新（扫描 / 搜索 / 换主题重建）时会被反复调用，
        //  封面只在**这一批游戏真的变了**时重取 —— 搜索框每敲一个字都取一次 12 张图，
        //  是白白吃掉 GDI 和 IO（CoverArt.Card 第一次要读盘）。
        public void SetItems(List<DlssgGame> src, string selectedId)
        {
            var sig = new StringBuilder();
            items.Clear();
            if (src != null)
            {
                items.AddRange(src);
                for (int i = 0; i < src.Count; i++) sig.Append(Lib.IdOf(src[i])).Append(';');
            }
            selId = selectedId ?? "";
            if (hot >= items.Count) hot = -1;
            if (sig.ToString() != lastSig) { lastSig = sig.ToString(); ReloadCovers(); }
            Invalidate();
        }

        public void SetSelected(string id)
        {
            id = id ?? "";
            if (selId == id) return;
            selId = id;
            Invalidate();
        }

        void ReloadCovers()
        {
            //  Card() 返回的是**独立副本**（CoverArt 内部 Own() 复制），所以要自己释放；
            //  不释放的话，每次刷新（扫描/搜索/换主题）都会攒下 items.Count 张图。
            for (int i = 0; i < covers.Count; i++)
                if (covers[i] != null) { try { covers[i].Dispose(); } catch { } }
            covers.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                Image im = null;
                if (!Program.ShareBuild)
                {
                    //  2 倍取图再缩小画：24×36 直接取会糊（与封面墙同一套做法）
                    try { im = CoverArt.Card(items[i], CovW * 2, CovH * 2); } catch { }
                }
                covers.Add(im);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int i = 0; i < covers.Count; i++)
                    if (covers[i] != null) { try { covers[i].Dispose(); } catch { } }
                covers.Clear();
            }
            base.Dispose(disposing);
        }

        int RowAt(Point p)
        {
            if (p.X < 0 || p.X >= Width || p.Y < 0 || p.Y >= Height) return -1;
            int i = (p.Y - Theme.S(2)) / RowH;
            if (i < 0 || i >= DrawRows) return -1;
            return i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = RowAt(e.Location);
            if (i != hot)
            {
                hot = i;
                Cursor = (hot >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot != -1)
            {
                hot = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int i = RowAt(e.Location);
                if (i >= 0)
                {
                    if (i == MoreRow) { if (MorePicked != null) MorePicked(); }
                    else if (i < items.Count && Picked != null) { Picked(items[i]); }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            try
            {
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int n = DrawRows;
                if (n == 0)
                {
                    //  空态（扫描还没回来 / 一个游戏都没有）：给一行说明，别留一块纯白。
                    //  颜色走 TextDim —— 有信息量的文字要过 AA 4.5:1（TextFaint 只有 4.05，是图标档）。
                    Theme.DrawTxt(g, EmptyText ?? "", Theme.F(9f),
                        new Rectangle(TagX, Theme.S(2), Width - TagX - Theme.S(8), RowH),
                        Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                        | TextFormatFlags.EndEllipsis);
                }
                for (int i = 0; i < n; i++)
                {
                    bool hv = i == hot;
                    //  行块满宽（与 NavMenu 的 row 一致）：缩进交给内部的 CovX/TagX，
                    //  否则悬停块的左缘比导航块右 6px，"一列"就散了。
                    var r = new Rectangle(0, Theme.S(2) + i * RowH, Width, RowH - Theme.S(2));
                    if (i == MoreRow)
                    {
                        //  「还有 N 款」：不是游戏行，配色跟着弱化色走，点进去是游戏库。
                        //  ⚠ 箭头是**矢量**画的，不用字面「›」—— CJK 标点按文字行高落位，
                        //  比同行图标低 1~2px，而且笔画粗细和一套 1.4px 的图标对不上
                        //  （同第 110 轮把「«」换成 chevron 的理由）。
                        if (hv) Theme.FillRound(g, r, Theme.RBtn, Theme.Hover);
                        Theme.DrawTxt(g, "还有 " + (items.Count - MoreRow) + " 款", Theme.F(9f),
                            new Rectangle(TagX, r.Y, r.Width - TagX - Theme.S(22), r.Height),
                            hv ? Theme.Text : Theme.TextDim,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        //  ⚠ 箭头方向是**向右**（去游戏库），不是向下 ——
                        //    我最初照抄了 LibActionBar 那个下拉箭头的画法（∨），那是"展开"的意思，
                        //    用在这里会被读成"折叠这一组"。向右 = 继续走。
                        int cx = r.Width - Theme.S(14), cy = r.Y + r.Height / 2, q = Theme.S(3);
                        using (var p = new Pen(hv ? Theme.Text : Theme.TextFaint, 1.4f))
                        {
                            g.DrawLine(p, cx - q, cy - q, cx + q, cy);
                            g.DrawLine(p, cx - q, cy + q, cx + q, cy);
                        }
                        continue;
                    }
                    DlssgGame gi = items[i];
                    if (gi == null) continue;
                    bool on = selId.Length > 0 && selId == Lib.IdOf(gi);
                    if (on) Theme.FillRound(g, r, Theme.RBtn, Theme.Subtle);
                    else if (hv) Theme.FillRound(g, r, Theme.RBtn, Theme.Hover);

                    var cr = new Rectangle(CovX, r.Y + (r.Height - CovH) / 2, CovW, CovH);
                    Image im = i < covers.Count ? covers[i] : null;
                    if (im != null)
                    {
                        using (var gp = Theme.Round(cr, Theme.S(4)))
                        {
                            var old = g.Clip;
                            try
                            {
                                g.SetClip(gp);
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.DrawImage(im, cr);
                                g.InterpolationMode = InterpolationMode.Default;
                            }
                            //  ⚠ Graphics.Clip 的 getter 返回一个**新建的 Region**，必须 Dispose：
                            //    只写 `g.Clip = old` 不释放，每画一帧就漏一个 GDI 对象 ——
                            //    列表每行一次，悬停/滚动时一秒几十帧，GDI 句柄很快见底。
                            finally { g.Clip = old; old.Dispose(); }
                        }
                        Theme.EdgeRound(g, new Rectangle(cr.X, cr.Y, cr.Width - 1, cr.Height - 1),
                                        Theme.S(4), Color.FromArgb(26, 0, 0, 0), 1f);
                    }
                    else Theme.FillRound(g, cr, Theme.S(4), Theme.FieldOn(BackColor));

                    Theme.DrawTxt(g, gi.Title, Theme.F(10f),
                        new Rectangle(TagX, r.Y, r.Width - TagX - Theme.S(8), r.Height),
                        on ? Theme.Accent : (hv ? Theme.Text : Theme.TextDim),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
            catch
            {
                //  自绘抛异常会变成"白块 + 红叉"（同 CoverTile 的兜底理由）
                try { using (var b2 = new SolidBrush(BackColor)) g.FillRectangle(b2, 0, 0, Width, Height); } catch { }
            }
        }
    }

    //  圆角多行输入框（2026-10-26 第 148 轮，给「启动参数 / 环境变量」用）。
    //  RoundField 是单行专用（占位提示写进 TextBox 真文本、高度固定 30），多行复用不了。
    //  这里只在**外观**上跟 RoundField 一致：同一档填充色 + 聚焦时才画的柔光圈。
    class RoundMemo : Panel
    {
        public readonly TextBox Box;

        public RoundMemo(int w, int h)
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Width = w; Height = h;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Multiline = true;
            Box.AcceptsReturn = true;
            Box.ScrollBars = ScrollBars.Vertical;
            //  不折行：环境变量是"一行一条"，自动折行会让一条看起来像两条
            Box.WordWrap = false;
            Box.BackColor = Theme.FieldOn(BackColor);   // 与外框填充同色，否则圆角里露出灰方块
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Mono(9f);                  // 等宽：键值对齐看得清
            Controls.Add(Box);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.TextChanged += delegate { Invalidate(); };
            Lay();
        }

        void Lay()
        {
            if (Box == null) return;
            Box.Left = Theme.S(10); Box.Top = Theme.S(9);
            Box.Width = Math.Max(Theme.S(40), Width - Theme.S(20));
            Box.Height = Math.Max(Theme.S(18), Height - Theme.S(17));
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Lay(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.RField, Theme.FieldOn(BackColor));
            if (Box != null && Box.Focused)
            {
                var glow = new Rectangle(2, 2, Width - 5, Height - 5);
                Theme.EdgeRound(g, glow, Math.Max(2, Theme.RField - 2), Theme.Alpha(Theme.Accent, 0.32), 3f);
                Theme.EdgeRound(g, r, Theme.RField, Theme.Accent, 1.2f);
            }
        }
    }

    public class GameRowList : Control
    {
        public readonly List<DlssgGame> Items = new List<DlssgGame>();
        public int SelectedIndex = -1;              // -1 = 「库主页」（回到封面墙）
        public event EventHandler SelectedIndexChanged;

        //  「库主页」这一行（2026-10-23 已停用）：导航里已经有「游戏库」，它就是回到封面墙，
        //  列表再放一行「库主页」等于同一件事说两遍 —— 用户原话「有了游戏库为什么下面还会有库主页」。
        //  保留这个开关只是为了让 RowCount/Offset 的两套索引逻辑不用重写。
        public bool ShowHome = false;   // 2026-10-23：导航已有「游戏库」，列表不再放「库主页」
        public string HomeText = "库主页";

        int scrollY, hoverIdx = -1;
        //  hover 淡入/淡出：行底不再"啪"地变色（原来鼠标划过整条左栏在跳）
        const int HoverMs = 110;
        const int SelMs = 160;              // 选中条滑入（比 hover 稍慢，读起来像"落位"）
        int hoverAt, prevIdx = -3, prevAt;
        int selRow = -3, selAt, prevSelRow = -3, prevSelAt;
        Timer hoverTimer;
        int RowH { get { return Theme.S(36); } }
        int ViewH { get { return Math.Max(RowH, ClientSize.Height); } }
        int RowCount { get { return Items.Count + (ShowHome ? 1 : 0); } }
        int Offset { get { return ShowHome ? 1 : 0; } }
        int MaxScroll { get { return Math.Max(0, RowCount * RowH - ViewH); } }

        public GameRowList()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public void SetItems(List<DlssgGame> list, DlssgGame keep)
        {
            Items.Clear();
            if (list != null) Items.AddRange(list);
            SelectedIndex = keep == null ? -1 : Items.IndexOf(keep);
            //  -2 = 选中的游戏被搜索词过滤掉了：不点亮任何一行，也不假装停在库主页
            //  （否则左栏亮着「库主页」、右边却是该游戏详情，两处互相打脸）
            if (SelectedIndex < -1) SelectedIndex = -2;
            if (scrollY > MaxScroll) scrollY = MaxScroll;
            //  重建（扫描/搜索）不算"用户改选"：直接把选中条落到终点，别再滑一次
            selRow = SelectedIndex; prevSelRow = -3;
            GameGlyph.Prefetch(Items);   // 后台提取，不等结果
            KickGlyph();
            Invalidate();
        }

        //  图标在后台陆续到位：有未完成的就 180ms 重绘一次，全部到齐立刻停表
        //  （常驻 60fps 定时器会把左侧栏变成 CPU 暖手宝，这里只在启动那一两秒跑）
        static Timer glyphTimer;
        static readonly List<GameRowList> glyphOwners = new List<GameRowList>();
        void KickGlyph()
        {
            if (GameGlyph.Pending == 0) return;
            if (!glyphOwners.Contains(this)) glyphOwners.Add(this);
            if (glyphTimer == null)
            {
                glyphTimer = new Timer();
                glyphTimer.Interval = 180;
                glyphTimer.Tick += delegate
                {
                    for (int i = glyphOwners.Count - 1; i >= 0; i--)
                    {
                        GameRowList o = glyphOwners[i];
                        if (o == null || o.IsDisposed) { glyphOwners.RemoveAt(i); continue; }
                        o.Invalidate();
                    }
                    if (GameGlyph.Pending == 0 || glyphOwners.Count == 0) glyphTimer.Stop();
                };
            }
            if (!glyphTimer.Enabled) glyphTimer.Start();
        }

        // 标题 → 稳定的色相（同一游戏每次进来颜色一致）
        static Color Tint(string name)
        {
            int h = 0;
            string s = name == null ? "" : name;
            for (int i = 0; i < s.Length; i++) h = (h * 31 + s[i]) & 0x7FFFFFFF;
            return Theme.Mix(Theme.Accent, Theme.Violet, (h % 100) / 100.0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            //  底色跟随 BackColor：侧栏换成材质色（Theme.Sidebar）后，写死 Panel 会留一块白。
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (RowCount == 0) return;

            int first = Math.Max(0, scrollY / RowH);
            int last = Math.Min(RowCount - 1, (scrollY + ViewH) / RowH);
            for (int r = first; r <= last; r++)
            {
                int y = r * RowH - scrollY;
                if (ShowHome && r == 0)
                {
                    PaintRow(g, y, HomeText, Items.Count + " 款游戏", Theme.Accent, SelectedIndex == -1, SelOf(-1), HoverOf(-2), true, null);
                    //  「库主页」是导航项，下面才是游戏：一条淡线把两段分开（Steam 也有这一层分组）
                    using (var pen = new Pen(Theme.Line, 1f))
                        g.DrawLine(pen, Theme.S(14), y + RowH - 1, Width - Theme.S(14), y + RowH - 1);
                }
                else
                {
                    DlssgGame it = Items[r - Offset];
                    string nm = it.Title == null ? "" : it.Title;
                    //  ⚠ 副标题必须**每行都有**（2026-10-23）：以前本地游戏把"本地"省掉，
                    //  于是单行条目和双行条目交替出现，左边一列的行高参差 —— 截图里那排
                    //  "绝区零 / 崩坏" 明显比上下两行更矮、文字位置也不一样。
                    string sub = Lib.PlatformLabel(it.Platform);
                    if (sub.Length == 0) sub = "本地";
                    PaintRow(g, y, nm, sub, Tint(nm), r - Offset == SelectedIndex, SelOf(r - Offset), HoverOf(r - Offset), false, GameGlyph.Peek(it));
                }
            }
        }

        void PaintRow(Graphics g, int y, string title, string sub, Color tint, bool sel, double sp, double hv, bool home, Bitmap glyph)
        {
            var row = new Rectangle(Theme.S(4), y + Theme.S(2), Width - Theme.S(8), RowH - Theme.S(4));
            if (sel && sp > 0.004)
            {
                //  选中：只留**一层**淡蓝底（2026-10-23）。
                //  以前是「淡蓝底 + 左侧 3px 蓝竖条 + 蓝色标题」三重信号：竖条是网页侧边栏
                //  的做法，压在 24px 图标旁边会跟图标抢边；macOS 的侧栏选中就一块圆角填充。
                Theme.FillRound(g, row, Theme.RBtn, Theme.Alpha(Theme.AccentSoft, sp));
            }
            else if (hv > 0.004) Theme.FillRound(g, row, Theme.RBtn, Theme.Mix(BackColor, Theme.Hover, hv));

            int t = Theme.S(24);
            var tr = new Rectangle(row.X + Theme.S(10), y + (RowH - t) / 2, t, t);
            //  真实游戏图标（GameGlyph 已在后台裁好圆角）；没拿到之前画首字母色块兜底
            if (glyph != null)
                g.DrawImage(glyph, tr);
            else if (home)
            {
                //  「库主页」是导航项：浅底 + 描边格栅，和右边那些彩色游戏图标区分开
                Theme.FillRound(g, tr, Theme.S(7), Theme.Subtle);
                int q = Theme.S(5), gap = Theme.S(3);
                int bx = tr.X + (t - q * 2 - gap) / 2, by = tr.Y + (t - q * 2 - gap) / 2;
                using (var pen = new Pen(Theme.Accent, 1.4f))
                {
                    g.DrawRectangle(pen, bx, by, q, q);
                    g.DrawRectangle(pen, bx + q + gap, by, q, q);
                    g.DrawRectangle(pen, bx, by + q + gap, q, q);
                    g.DrawRectangle(pen, bx + q + gap, by + q + gap, q, q);
                }
            }
            else
            {
                Theme.FillRound(g, tr, Theme.S(7), tint);
                if (title.Length > 0)
                    Theme.DrawTxt(g, title.Substring(0, 1), Theme.FB(9f), tr, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            int tx = tr.Right + Theme.S(9);
            int tw = row.Right - tx - Theme.S(8);
            //  8.5f 加粗 → 9.5f Medium（2026-10-20）：加粗在 12px 上会把笔画糊成一团，
            //  换 Medium 同尺寸更清晰、也更贵；平台名 7f 太小（≈9px），抬到 8f。
            //
            //  ⚠ 框高必须用 Font.Height（2026-10-20 二次修正）：原来写死 S(17)/S(14)，
            //    而 Noto Sans SC 的行高是 1.48em —— 9.5pt 要 19px、8pt 要 16px，两行都被
            //    切掉顶上那 1~2 像素，字头是齐的切口。左栏是看得最多的文字，最容易被察觉成"怪"。
            Font fT = Theme.FM(9.5f), fS = Theme.F(8f);
            int hT = fT.Height, hS = fS.Height;
            if (sub.Length > 0)
            {
                int top2 = y + (RowH - hT - hS) / 2;
                Theme.DrawTxt(g, title, fT, new Rectangle(tx, top2, tw, hT),
                    sel ? Theme.Accent : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                Theme.DrawTxt(g, sub, fS, new Rectangle(tx, top2 + hT, tw, hS),
                    Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
                Theme.DrawTxt(g, title, fT, new Rectangle(tx, y, tw, RowH - Theme.S(4)),
                    sel ? Theme.Accent : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        //  -2 = 库主页；-1 = 空白；>=0 = Items 下标
        int HitRow(int y)
        {
            int r = (y + scrollY) / RowH;
            if (r < 0 || r >= RowCount) return -1;
            if (ShowHome && r == 0) return -2;
            return r - Offset;
        }

        double HoverOf(int row)
        {
            int now = Environment.TickCount;
            if (row == hoverIdx) { double p = unchecked(now - hoverAt) / (double)HoverMs; return p > 1 ? 1 : p; }
            if (row == prevIdx) { double p = unchecked(now - prevAt) / (double)HoverMs; return p > 1 ? 0 : 1 - p; }
            return 0;
        }

        void MoveHover(int i)
        {
            if (i == hoverIdx) return;
            int oldRow = hoverIdx;
            prevIdx = hoverIdx; prevAt = Environment.TickCount;
            hoverIdx = i; hoverAt = prevAt;
            EnsureHoverAnim();
            //  只重绘这两行：整控件重绘一次要 1.8ms，动画 7 帧就是白烧
            if (oldRow == -2) InvalidateListRow(0); else if (oldRow >= 0) InvalidateListRow(oldRow + Offset);
            InvalidateHov(i);
        }

        double SelOf(int row)
        {
            int now = Environment.TickCount;
            if (SelectedIndex != selRow)
            {
                prevSelRow = selRow; prevSelAt = now;
                selRow = SelectedIndex; selAt = now;
                EnsureHoverAnim();
            }
            if (row == selRow) { double p = unchecked(now - selAt) / (double)SelMs; return p > 1 ? 1 : p; }
            if (row == prevSelRow) { double p = unchecked(now - prevSelAt) / (double)SelMs; return p > 1 ? 0 : 1 - p; }
            return 0;
        }

        void InvalidateListRow(int row)
        {
            if (row < 0) return;
            Invalidate(new Rectangle(0, row * RowH - scrollY, Width, RowH));
        }
        //  两套索引各自映射到显示行：hover 用 -2 表示库主页，选中用 -1 表示库主页
        void InvalidateHov(int i) { if (i == -2) InvalidateListRow(0); else if (i >= 0) InvalidateListRow(i + Offset); }
        void InvalidateSel(int i) { if (i == -1) InvalidateListRow(0); else if (i >= 0) InvalidateListRow(i + Offset); }

        //  只在淡入/淡出那 110ms 里按 16ms 重绘；停手即停表（不常驻）
        void EnsureHoverAnim()
        {
            if (hoverTimer == null)
            {
                hoverTimer = new Timer();
                hoverTimer.Interval = 16;
                hoverTimer.Tick += delegate
                {
                    int now = Environment.TickCount;
                    bool live = unchecked(now - hoverAt) < HoverMs || unchecked(now - prevAt) < HoverMs
                             || unchecked(now - selAt) < SelMs || unchecked(now - prevSelAt) < SelMs;
                    if (IsDisposed) { hoverTimer.Stop(); return; }
                    InvalidateHov(hoverIdx);
                    InvalidateHov(prevIdx);
                    InvalidateSel(selRow);
                    InvalidateSel(prevSelRow);
                    if (!live) hoverTimer.Stop();
                };
            }
            if (!hoverTimer.Enabled) hoverTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MoveHover(HitRow(e.Y));
        }

        protected override void OnMouseLeave(EventArgs e) { MoveHover(-1); base.OnMouseLeave(e); }

        void Pick(int i)
        {
            int want = i < 0 ? -1 : i;
            if (want == SelectedIndex) return;
            SelectedIndex = want;
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = HitRow(e.Y);
            if (i < -1) { Pick(-1); return; }        // 库主页
            if (i >= 0) Pick(i);
        }

        // 右键：先选中该行，再交给页面弹菜单（动作与游戏库卡片一致）
        public event EventHandler RightPick;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            int i = HitRow(e.Y);
            if (i < 0) return;                       // 库主页没有游戏级菜单
            if (i != SelectedIndex)
            {
                SelectedIndex = i;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
            if (RightPick != null) RightPick(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0)
            {
                BubbleWheel(e.Delta);
                return;
            }
            double notch = SystemInformation.MouseWheelScrollDelta;
            if (notch <= 0) notch = 120;
            int d = (int)Math.Round((double)-e.Delta / notch * RowH);
            if (d == 0 && e.Delta != 0) d = e.Delta > 0 ? -1 : 1;
            int v = scrollY + d;
            if (v < 0) v = 0;
            if (v > MaxScroll) v = MaxScroll;
            if (v == scrollY)
            {
                BubbleWheel(e.Delta);
                return;
            }
            scrollY = v;
            Invalidate();
        }

        void BubbleWheel(int delta)
        {
            for (Control p = Parent; p != null; p = p.Parent)
            {
                Pg pg = p as Pg;
                if (pg != null)
                {
                    pg.ScrollByWheel(delta);
                    break;
                }
            }
        }
    }

    // 游戏卡片 —— 对齐《游戏启动器》的 .card 设计：
    //   封面铺满整张卡（object-fit: cover）→ 底部渐变遮罩 → 名称 / 平台 / 状态压在图上。
    // 为什么不再用"上方封面 + 下方两行文字"的分段式：两段之间天然有条接缝，
    //   整排看过去很割裂（用户原话"很多地方衔接不优美很割裂"）。
    // 顺带好处：整张卡只有一个绘制面（无子控件），少一类子控件重绘问题。
    //  窗口按钮种类：最小化 / 最大化（还原）/ 关闭
    enum WinKind { Min, Max, Close }

    //  标题栏窗口按钮（2026-10-23）：与图标同一套"细线 + 圆头"笔法，10px 图形，
    //  常态不着色、悬停才给底色；关闭键悬停才出现红底（study §5："红色只在 hover 出现"）。
    //  色阶照「自考工作台」§5（2026-10-26 修正"只有关闭键有悬停反馈"）：
    //    默认 = 透明（页头底色）→ hover = 比页头深约 6%（--surface-2）→ 按下再深一档（--border）。
    //  原来 hover 用 Theme.Hover(#F5F5F7)，比页头 Theme.Bg(#F2F2F7) 还**亮 3 个点** ——
    //  底色"隐形"，用户看到的就是"最小化/最大化没反应，只有关闭键会红"。
    //  图标墨色同表：默认次要文字色（--text-2，6.76:1），悬停提到正文色（--text-1，14.5:1）。
    class WinBtn : Control
    {
        public WinKind Kind;
        public bool Restore;        // 最大化时"最大化"键要画成"还原"（两个叠矩形），否则形状在骗人
        Anim aHov, aDn;             // 悬停/按下过渡（110ms，与 FlatBtn 同一套，消掉"啪"的跳变）
        public WinBtn()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            //  桌面窗口控件用**箭头**不用手型：这是系统级语义（Windows 自己的三个键也是箭头），
            //  手型会把它读成"网页里的一个按钮"。
            Cursor = Cursors.Default;
            BackColor = Theme.Bg;
            aHov = new Anim(this, Motion.Fast, Motion.Decel);
            aDn = new Anim(this, Motion.Press, Motion.Decel);
        }
        protected override void OnMouseEnter(EventArgs e) { aHov.To(1.0); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { aHov.To(0.0); aDn.To(0.0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { aDn.To(1.0); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { aDn.To(0.0); base.OnMouseUp(e); }
        //  --surface-2：比页头底深约 6%（对齐 study 的 14~18/255 步长）；按下再深一档（--border 档）。
        static Color HoverBg { get { return Theme.Mix(Theme.Bg, Theme.Text, 0.07); } }
        static Color ActiveBg { get { return Theme.Mix(Theme.Bg, Theme.Text, 0.13); } }
        //  关闭键悬停红照 study §5 色阶表（白字对比度 6.54:1 / 暗 4.88:1，≥4.5 达标；
        //  systemRed #FF3B30 配白字只有 3.55:1，不达标）。
        static Color CloseHotBg { get { return Theme.Dark ? Color.FromArgb(226, 83, 75) : Color.FromArgb(179, 38, 30); } }
        static Color CloseHotFg { get { return Theme.Dark ? Color.FromArgb(22, 20, 15) : Color.White; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            bool close = Kind == WinKind.Close;
            double h = aHov.Value, dn = aDn.Value;
            if (h > 0.001 || dn > 0.001)
            {
                Color hotBg = close ? CloseHotBg : HoverBg;
                Color dnBg = close ? Theme.Mix(CloseHotBg, Color.Black, 0.14) : ActiveBg;
                using (var b = new SolidBrush(Theme.Mix(Theme.Mix(Theme.Bg, hotBg, h), dnBg, dn)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(6), b);
                }
            }
            //  图标墨色：默认次级（text-2）→ 悬停提到正文（text-1）；关闭键悬停按规范换白（亮）/深墨（暗）。
            Color fg = close
                ? Theme.Mix(Theme.TextDim, CloseHotFg, Math.Max(h, dn))
                : Theme.Mix(Theme.TextDim, Theme.Text, Math.Max(h, dn));
            float w = Math.Max(1f, (float)Theme.S(1f));
            int cx = Width / 2;
            int cy = Height / 2;
            int s = Theme.S(10) / 2;
            using (var p = new Pen(fg, w))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Flat;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Flat;
                p.LineJoin = System.Drawing.Drawing2D.LineJoin.Miter;
                if (Kind == WinKind.Min)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawLine(p, cx - s, cy, cx + s, cy);
                }
                else if (Kind == WinKind.Max && !Restore)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawRectangle(p, cx - s, cy - s, s * 2, s * 2);
                }
                else if (Kind == WinKind.Max)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    int d = Theme.S(2);
                    g.DrawRectangle(p, cx - s, cy - s + d, s * 2 - d, s * 2 - d);
                    g.DrawLines(p, new Point[] {
                        new Point(cx - s + d, cy - s),
                        new Point(cx + s, cy - s),
                        new Point(cx + s, cy + s - d) });
                }
                else
                {
                    if (w > 1.2f)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawLine(p, cx - s + 0.5f, cy - s + 0.5f, cx + s + 0.5f, cy + s + 0.5f);
                        g.DrawLine(p, cx + s + 0.5f, cy - s + 0.5f, cx - s + 0.5f, cy + s + 0.5f);
                    }
                    else
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                        g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                    }
                }
            }
        }
    }

    // =========================================================================
    //  第 124 轮（2026-10-24）：封面网格（Steam / Xbox 那种排法）
    // =========================================================================
    //  第 123 轮的「列表 + 右侧检查器」是照着竞品抄的，用户一眼看穿："纯抄袭"。
    //  这次回到封面 —— 但也别退回第 122 轮那张 152×228 的小卡（他说"廉价"）：
    //    · 190×285（2:3，接近 Steam 库胶囊的观感），1040 宽下 4 列；
    //    · 卡面只留两行：名字 + **可点的方案胶囊** —— 这颗胶囊是和竞品真正的差别，
    //      点它就地换方案，不用进详情；
    //    · 点卡片 = 进详情（Steam 的行为），右下 ▶ = 直接启动。
    public class CoverTile : Control
    {
        public DlssgGame Game;
        bool picked, hot;
        Image cover;

        public event EventHandler Chosen;                 // 单击：进详情
        public event EventHandler Launched;               // 右下 ▶：启动
        public event Action<CoverTile> SchemeMenu;        // 胶囊：换方案

        Anim aHover, aPick;
        string tagCache;

        public CoverTile(DlssgGame g, int w, int h)
        {
            Game = g;
            Size = new Size(w, h);
            Margin = Padding.Empty;
            BackColor = Theme.Bg;                 // 圆角之外露出的底色（= 页面底色）
            SetStyle(ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            aHover = new Anim(this, Motion.Fast, Motion.Decel);     // 悬停 110ms：与按钮/输入框同档（规范 hover=80–120ms），原 200 超表
            aHover.Snap(0);
            aPick = new Anim(this, Motion.Normal, Motion.EmphDec);
            aPick.Snap(0);
            cover = Program.ShareBuild ? null : CoverArt.Card(g, w, h);
            Theme.Tip.SetToolTip(this, g.Title + (g.Dir != null && g.Dir.Length > 0 ? "\r\n" + g.Dir : ""));
            Click += delegate { if (Chosen != null) Chosen(this, EventArgs.Empty); };
            DoubleClick += delegate { if (Launched != null) Launched(this, EventArgs.Empty); };
            MouseEnter += delegate { SetHover(true); };
            MouseLeave += delegate { SetHover(false); };
        }

        //  换绑到另一个 DlssgGame 对象（扫描后对象会整批重建，但 Id 没变）。
        //  同一 Id 就只换引用、不重取封面 —— CoverArt.Own() 每次都要 new Bitmap 复制一份，
        //  12 张卡每次刷新各复制一次，就是 1.9MB 的纯垃圾（2026-10-24 第 133 轮）。
        public void Rebind(DlssgGame g)
        {
            string oldId = Game == null ? "" : Game.Id;
            Game = g;
            tagCache = null;
            playCache = null;
            if (g == null) return;
            if (g.Id != oldId || cover == null)
            {
                //  Card() 每次返回的都是**独立副本**（内部 Hit/Remember 都走 Own() 复制），
                //  所有权归调用方 —— 换游戏时必须把上一张释放掉，否则卡片一直复用就会攒图。
                if (cover != null) { try { cover.Dispose(); } catch { } cover = null; }
                cover = Program.ShareBuild ? null : CoverArt.Card(g, Width, Height);
            }
            Invalidate();
        }

        public bool Picked
        {
            get { return picked; }
            set
            {
                if (picked == value) return;
                picked = value;
                aPick.To(value ? 1.0 : 0.0);
                Invalidate();
            }
        }

        public void SetHover(bool on)
        {
            hot = on;
            aHover.To(on ? 1.0 : 0.0);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (cover != null) { cover.Dispose(); cover = null; } } catch { }
                try { Theme.Tip.SetToolTip(this, null); } catch { }
            }
            base.Dispose(disposing);
        }

        //  方案短名：判据只认 Plan.Current（详情页也用它），卡面与详情页不会各说各话。
        //  ⚠ 不能叫 Tag：Control.Tag 是基类属性，撞名会被 static_check 拦下
        //
        //  ★ 进程级缓存（2026-10-24 第 127 轮）：Plan.Current 要 File.Exists + 取签名，
        //  一张卡一次就已经是十几次 IO；而 tile 在**每次搜索/排序**时都会被整批重建 ——
        //  用户敲一个字，28 张卡就重摸一遍文件。缓存键带上「装没装 / 入口名」，
        //  安装或换方案后 Key 自然变化，缓存自己失效，不需要谁去手动清。
        static readonly Dictionary<string, string> tagMemo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string SchemeTag()
        {
            if (tagCache != null) return tagCache;
            DlssgGame g = Game;
            if (g == null) return tagCache = "";
            string memoKey = (g.Dir == null ? "" : g.Dir) + "|" + g.Installed + "|" + g.HasFrameGen
                           + "|" + (g.Entry == null ? "" : g.Entry) + "|" + g.Ignored;
            string hit;
            if (tagMemo.TryGetValue(memoKey, out hit)) return tagCache = hit;
            tagCache = ComputeTag(g);
            tagMemo[memoKey] = tagCache;
            return tagCache;
        }

        static string ComputeTag(DlssgGame g)
        {
            if (g.Ignored) return "已忽略";
            if (g.Installed || Dlssg030.ShortTag(g).Length > 0 || XeMfg.ShortTag(g).Length > 0)
            {
                string a = Plan.Current(g.Dir);
                if (a == Plan.Opti) return "方案 A";
                if (a == Plan.D030) return "MFG 0.3";
                if (a == Plan.D040) return "MFG 0.4";
                if (a == Plan.Nvbr) return "方案 B";
                if (a == Plan.Legacy) return "旧代理";
                return "已开启";
            }
            if (g.HasFrameGen) return "可接入";
            return "无组件";
        }

        //  累计时长的短写法（"12h" / "35m"）：完整格式（"12.5 小时"）在小角标里放不下
        string playCache;
        string PlayLabel()
        {
            if (playCache != null) return playCache;
            playCache = "";
            try
            {
                if (Game == null) return playCache;
                long sec = Program.PlayTimeOf(Lib.IdOf(Game));
                if (sec >= 3600) playCache = (sec / 3600) + "h";
                else if (sec >= 60) playCache = (sec / 60) + "m";
            }
            catch { playCache = ""; }
            return playCache;
        }

        Color DotColor()
        {
            DlssgGame g = Game;
            if (g == null) return Theme.TextFaint;
            if (g.Ignored) return Theme.Warn;
            if (tagCache == "可接入") return Theme.Accent;
            if (tagCache == "无组件") return Theme.TextFaint;
            return Theme.Ok;
        }

        static Font FName() { return Theme.FM(10.5f); }
        static Font FMeta() { return Theme.F(8.5f); }

        Rectangle PlayRect()
        {
            int d = Theme.S(30);
            return new Rectangle(Width - d - Theme.S(10), Height - d - Theme.S(10), d, d);
        }

        Rectangle SchemeRect()
        {
            string t = SchemeTag();
            if (t.Length == 0) return Rectangle.Empty;
            int w = Theme.S(9) + Theme.S(4) + Theme.S(5) + TextRenderer.MeasureText(t, FMeta()).Width + Theme.S(9);
            int room = PlayRect().Left - Theme.S(10) - Theme.S(6);
            if (room > Theme.S(30)) w = Math.Min(w, room);
            return new Rectangle(Theme.S(10), Height - Theme.S(9) - Theme.S(18), w, Theme.S(18));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && SchemeRect().Contains(e.Location))
            {
                if (SchemeMenu != null) SchemeMenu(this);
                return;                                   // 不再触发"选中/进详情"
            }
            if (e.Button == MouseButtons.Left && aHover.Value > 0.5 && PlayRect().Contains(e.Location))
            {
                if (Launched != null) Launched(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        //  绘制异常兜底：.NET 对 OnPaint 抛异常的控件就是"白块 + 红叉"，
        //  一张封面解码失败就能毁掉整面墙（2026-09-15 用户截图实测）。宁可退成一块纯底色。
        static bool errLogged;
        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintTile(e.Graphics); }
            catch (Exception ex)
            {
                if (!errLogged)
                {
                    errLogged = true;
                    try { Program.Log("封面卡绘制异常（已兜底为纯底色）：" + ex.GetType().Name + ": " + ex.Message); } catch { }
                }
                try { using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, 0, 0, Width, Height); } catch { }
            }
        }

        void PaintTile(Graphics g)
        {
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            double hv = Math.Max(0, aHover.Value);
            //  聚焦感来自"这张弹起来"：放大 7%、上浮 6px。
            //  3.5%/3px 太含蓄，鼠标移上去只是"亮了一下"（用户：沉浸感不够）。
            int lift = (int)Math.Round(Theme.S(6) * hv);
            var full = new Rectangle(0, -lift, Width, Height);

            //  封面：悬停放大 3.5%（用裁切实现，不动控件尺寸，否则整排会重排）
            if (cover != null)
            {
                double z = 1.0 + 0.07 * hv;
                var dst = new Rectangle((int)Math.Round((Width - Width * z) / 2),
                                        -lift + (int)Math.Round((Height - Height * z) / 2),
                                        (int)Math.Round(Width * z), (int)Math.Round(Height * z));
                var old = g.Clip;
                try
                {
                    using (var gp = Theme.Round(full, Theme.RCard)) g.SetClip(gp);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(cover, dst);
                    g.InterpolationMode = InterpolationMode.Default;
                }
                finally { g.Clip = old; old.Dispose(); }
            }
            else Theme.FillRound(g, full, Theme.RCard, Theme.Panel);

            //  底部渐变：0.58 才起始、最深 228α。再淡字就飘了（浅色封面上实测），
            //  再浓封面下半截就糊成一块黑布 —— 这不是"让字可读"，是把图盖掉了。
            using (var gp = Theme.Round(full, Theme.RCard))
            using (var br = new LinearGradientBrush(full, Color.Black, Color.Black, 90f))
            {
                var blend = new ColorBlend(5);
                blend.Colors = new Color[] {
                    Color.FromArgb(0, 8, 12, 18),
                    Color.FromArgb(0, 8, 12, 18),
                    Color.FromArgb(72, 8, 12, 18),
                    Color.FromArgb(172, 8, 12, 18),
                    Color.FromArgb(228, 8, 12, 18) };
                blend.Positions = new float[] { 0f, 0.58f, 0.74f, 0.88f, 1f };
                br.InterpolationColors = blend;
                g.FillPath(br, gp);
            }

            //  悬停提亮（按补间给 alpha，避免"一下全亮"）
            if (hv > 0.01 && aPick.Value < 0.5)
            {
                int ha = (int)Math.Round(18 * hv);
                if (ha > 0)
                {
                    using (var ov = new SolidBrush(Color.FromArgb(ha, 255, 255, 255)))
                    using (var gpH = Theme.Round(full, Theme.RCard))
                        g.FillPath(ov, gpH);
                }
            }

            //  外框：选中 = 强调色 2.2px + 外发光；悬停 = 颜色/粗细一起过渡 + 电光蓝微光外描边；静止 = 发丝边
            var edgeRect = new Rectangle(0, -lift, Width - 1, Height - 1);
            double ap = aPick.Value;
            if (ap > 0.01)
            {
                int ga = (int)Math.Round(40 * ap);
                if (ga > 0)
                {
                    using (var pen = new Pen(Color.FromArgb(ga, Theme.Accent), 4f))
                    using (var gpE = Theme.Round(edgeRect, Theme.RCard))
                        g.DrawPath(pen, gpE);
                }
                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.Accent, (float)(2.2 * ap));
            }
            else if (hv > 0.02)
            {
                // 悬停电光蓝微光外描边
                int ga = (int)Math.Round(45 * hv);
                if (ga > 0)
                {
                    var glowRect = new Rectangle(Theme.S(1), Theme.S(1), Width - 1 - Theme.S(2), Height - 1 - Theme.S(2));
                    using (var glowPen = new Pen(Color.FromArgb(ga, Theme.Accent), Theme.S(2)))
                    using (var gpE = Theme.Round(glowRect, Theme.RCard))
                        g.DrawPath(glowPen, gpE);
                }
                Theme.EdgeRound(g, edgeRect, Theme.RCard,
                    Theme.Mix(Theme.Line, Theme.Accent, hv), (float)(1.0 + 0.8 * hv));
            }
            else
                Theme.EdgeRound(g, edgeRect, Theme.RCard, Theme.CardEdge, 1f);

            //  ---- 角标：左上收藏、右上累计时长 ----
            //  卡面只写名字会显得"空"（用户原话：简陋）。这两样都是启动器本来就有的数据，
            //  以前只藏在悬浮提示里。时长用黑底白字那种"视频缩略图"的做法，
            //  压在任何封面上都读得出来（白字直接压浅色封面会飘）。
            if (Game != null && Game.Favorite)
                Theme.DrawPhotoText(g, "★", Theme.FB(10f),
                    new Rectangle(Theme.S(8), Theme.S(6), Theme.S(18), Theme.S(18)),
                    Theme.Golden);
            string played = PlayLabel();
            if (played.Length > 0)
            {
                Font fp = Theme.F(8f);
                Size szP = TextRenderer.MeasureText(played, fp);
                int pw = szP.Width + Theme.S(10), ph = Theme.S(16);
                var pr2 = new Rectangle(Width - pw - Theme.S(9), Theme.S(8), pw, ph);
                Theme.FillRound(g, pr2, Theme.S(4), Color.FromArgb(140, 8, 12, 18));
                Theme.EdgeRound(g, pr2, Theme.S(4), Color.FromArgb(40, 255, 255, 255), 1f);
                Theme.DrawPhotoText(g, played, fp, pr2, Color.FromArgb(238, 255, 255, 255),
                                    StringAlignment.Center);
            }

            //  ---- 角标：实测倍率（第 144 轮）----
            //  这是本工具唯一"别人给不了"的那一格信息：代理到底跑起来了、实际几倍。
            //  以前只有一行全局摘要（"最近生效：某某 · 6X"），用户得自己猜是不是这块游戏。
            //  规则（刻意保守）：
            //   · 只在**真读到 generated_count** 时出现 —— 没读数就当没有，绝不写"未生效"，
            //     否则整面墙挂满告警牌，比不显示更糟（用户会开始无视它）；
            //   · 数据来自 FgVerify 的缓存字典（纯查表），绘制路径里没有任何文件 IO。
            int genMz = FgVerify.MeasuredGen(Game);
            if (genMz >= 0)
            {
                string t2 = "实测 " + (genMz + 1) + "X";
                Font fg2 = Theme.F(8f);
                Size szF = TextRenderer.MeasureText(t2, fg2);
                int fw = szF.Width + Theme.S(10), fh = Theme.S(16);
                int fy = Theme.S(8) + (played.Length > 0 ? fh + Theme.S(4) : 0);
                var fr = new Rectangle(Width - fw - Theme.S(9), fy, fw, fh);
                Theme.FillRound(g, fr, Theme.S(4), Color.FromArgb(140, 8, 12, 18));
                Theme.EdgeRound(g, fr, Theme.S(4), Color.FromArgb(40, 255, 255, 255), 1f);
                //  淡薄荷而不是纯 #34C759：白/绿系压在照片上要靠明度活下来，
                //  饱和度顶满会变成"廉价贴纸"（同 DrawPhotoText 那条判据）。
                Theme.DrawPhotoText(g, t2, fg2, fr, Color.FromArgb(240, 208, 255, 216),
                                    StringAlignment.Center);
            }

            //  ---- 卡面文字：名字 + 方案胶囊，都贴着底边 ----
            int rowH = Theme.S(18);
            int my = Height - Theme.S(9) - rowH;
            Font fName = FName();
            Theme.DrawPhotoText(g, Game == null ? "" : Game.Title, fName,
                new Rectangle(Theme.S(10), my - fName.Height - Theme.S(3), Width - Theme.S(20), fName.Height),
                Color.White);

            var cr = SchemeRect();
            if (cr.Width > 0)
            {
                int k = (hv > 0.02 && ap < 0.9) ? 1 : 0;
                double kk = k == 1 ? hv : 0;
                Theme.FillRound(g, cr, cr.Height / 2,
                    Color.FromArgb((int)Math.Round(26 + 202 * kk), 255, 255, 255));
                if (kk > 0.02)
                    Theme.EdgeRound(g, new Rectangle(cr.X, cr.Y, cr.Width - 1, cr.Height - 1), cr.Height / 2,
                        Color.FromArgb((int)Math.Round(44 * kk), 0, 0, 0), 1f);
                int dot = Theme.S(4);
                int dx = cr.X + Theme.S(9);
                using (var b = new SolidBrush(DotColor()))
                    g.FillEllipse(b, dx, cr.Y + (cr.Height - dot) / 2, dot, dot);
                int tx = (int)Math.Round(255 - 215 * kk);
                int tg = (int)Math.Round(255 - 211 * kk);
                int tb = (int)Math.Round(255 - 203 * kk);
                Theme.DrawPhotoText(g, SchemeTag(), FMeta(),
                    new Rectangle(dx + dot + Theme.S(5), cr.Y, cr.Width - (dx - cr.X) - dot - Theme.S(9), cr.Height),
                    Color.FromArgb(tx, tg, tb));
            }

            //  ---- 悬停时右下角浮出 ▶ ----
            if (hv > 0.15 && ap < 0.5)
            {
                var pr = PlayRect();
                int kd = (int)Math.Round(232 * Math.Min(1.0, hv));
                //  白圆压在浅色封面上会融进去（实测像一片白雾），先描一圈 30α 黑边托住
                using (var b = new SolidBrush(Color.FromArgb((int)Math.Round(34 * Math.Min(1.0, hv)), 0, 0, 0)))
                    g.FillEllipse(b, pr.X - 1, pr.Y - 1, pr.Width + 2, pr.Height + 2);
                using (var b = new SolidBrush(Color.FromArgb(kd, 255, 255, 255)))
                    g.FillEllipse(b, pr);
                using (var b = new SolidBrush(Color.FromArgb(kd, 12, 16, 24)))
                {
                    //  三角的光学居中：重心偏左，往右让 2px 才"看着在中间"
                    int tw = pr.Width * 2 / 7, th = pr.Height * 2 / 5;
                    int cx = pr.X + (pr.Width - tw) / 2 + Theme.S(2), cy = pr.Y + pr.Height / 2;
                    g.FillPolygon(b, new Point[] {
                        new Point(cx, cy - th / 2),
                        new Point(cx, cy + th / 2),
                        new Point(cx + tw, cy) });
                }
            }
        }
    }

    //  筛选胶囊（2026-10-24 第 126 轮）
    //  ---------------------------------------------------------------------------
    //  用户的问题：「游戏少的时候没问题，变多了页面非常挤，你该怎么解决？」
    //  封面墙本身不解决"找得到" —— 100 个游戏就是 25 行纯滚动。Steam 靠左栏分类、
    //  Xbox 靠顶部横向分组，本质都是**先缩小范围再看**。
    //  这里做最轻的一档：工具条上一排胶囊，各自带数量；数量为 0 的自动隐藏 ——
    //  12 个游戏时它只是"全部 12 / Steam 8 / 本地 4"一行小字，不碍事；
    //  100 个游戏时它就是导航。
    public class FilterChips : Control
    {
        public string[] Labels = new string[0];
        public string[] Ids = new string[0];
        public int[] Counts = new int[0];
        public string SelectedId = "all";
        public event EventHandler Changed;

        int hot = -1;
        readonly List<Rectangle> rects = new List<Rectangle>();
        Anim aSel;                 // 选中过渡：新 chip 淡入、旧 chip 淡出（110ms，同一条动画驱动两端）
        string prevSelId;          // 上一个选中 id（sv<1 时按 1-v 淡出）

        public FilterChips()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(28);
            Cursor = Cursors.Hand;
            aSel = new Anim(this, Motion.Fast, Motion.Decel);
            aSel.Snap(1);
        }

        static Font FChip() { return Theme.F(8.5f); }

        //  可见项：'all' 永远在；其余 count > 0 才露脸（12 个游戏时不会出现 WeGame 这种空分类）
        //  ⚠ 不能叫 Visible：Control.Visible 是基类属性
        List<int> VisibleItems()
        {
            var v = new List<int>();
            for (int i = 0; i < Labels.Length; i++)
                if (Ids[i] == "all" || (Counts != null && i < Counts.Length && Counts[i] > 0)) v.Add(i);
            return v;
        }

        int ChipW(int i)
        {
            string t = Labels[i] + " " + (Counts != null && i < Counts.Length ? Counts[i] : 0);
            return TextRenderer.MeasureText(t, FChip()).Width + Theme.S(18);
        }

        void LayoutChips()
        {
            rects.Clear();
            int x = 0;
            List<int> vis = VisibleItems();
            for (int k = 0; k < vis.Count; k++)
            {
                int i = vis[k];
                int w = ChipW(i);
                if (x + w > Width) break;                 // 放不下就不画（窄窗口时宁可少几项）
                rects.Add(new Rectangle(x, (Height - Theme.S(24)) / 2, w, Theme.S(24)));
                x += w + Theme.S(6);
            }
        }

        public void SetData(string[] labels, string[] ids, int[] counts)
        {
            Labels = labels; Ids = ids; Counts = counts;
            LayoutChips();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChips();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            LayoutChips();
            List<int> vis = VisibleItems();
            for (int k = 0; k < rects.Count && k < vis.Count; k++)
            {
                int i = vis[k];
                var r = rects[k];
                string t = Labels[i] + " " + (Counts != null && i < Counts.Length ? Counts[i] : 0);
                bool sel = Ids[i] == SelectedId;
                //  选中 = 淡强调底 + 强调字；其余 = 无底 + 次级文字。不做描边（一排胶囊都描边会太吵）
                //  选中切换带 110ms 过渡：新 chip 按进度淡入，旧 chip 同步淡出（重排不做动画，只有状态色做）
                double sv = aSel.Value;
                bool prev = !sel && prevSelId != null && Ids[i] == prevSelId && sv < 1.0;
                if (sel && sv < 1.0)
                {
                    Theme.FillRound(g, r, r.Height / 2, Theme.Alpha(Theme.AccentSoft, sv));
                    Theme.DrawTxt(g, t, FChip(), r,
                        Theme.Mix(Theme.TextDim, Theme.Accent, sv),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.NoPrefix);
                    continue;
                }
                if (prev)
                {
                    Theme.FillRound(g, r, r.Height / 2, Theme.Alpha(Theme.AccentSoft, 1.0 - sv));
                    Theme.DrawTxt(g, t, FChip(), r,
                        Theme.Mix(Theme.Accent, Theme.TextDim, sv),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.NoPrefix);
                    continue;
                }
                if (sel) Theme.FillRound(g, r, r.Height / 2, Theme.AccentSoft);
                else if (k == hot) Theme.FillRound(g, r, r.Height / 2, Theme.Hover);
                Theme.DrawTxt(g, t, FChip(), r,
                    sel ? Theme.Accent : Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
            }
        }

        int HitIndex(int x, int y)
        {
            for (int k = 0; k < rects.Count; k++)
                if (rects[k].Contains(new Point(x, y))) return k;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int k = HitIndex(e.X, e.Y);
            if (k != hot) { hot = k; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot != -1) { hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int k = HitIndex(e.X, e.Y);
            List<int> vis = VisibleItems();
            if (e.Button == MouseButtons.Left && k >= 0 && k < vis.Count)
            {
                string id = Ids[vis[k]];
                if (id != SelectedId)
                {
                    prevSelId = SelectedId;
                    SelectedId = id;
                    aSel.Snap(0);
                    aSel.To(1);
                    Invalidate();
                    if (Changed != null) Changed(this, EventArgs.Empty);
                }
            }
            base.OnMouseDown(e);
        }
    }

    //  底部状态条（2026-10-24 第 125 轮，第二版）
    //  ---------------------------------------------------------------------------
    //  第一版是"横跨全宽的白卡浮层 + 右上角 ✕"—— 用户原话："会让我下意识以为是广告弹窗去关掉它"。
    //  那不是配色问题，是**形态**：横幅广告就是"浮在内容上、右边一个叉、里面塞满东西"。
    //  这一版把它做成界面**固有的一段**：
    //    · Dock=Bottom，占真实布局（页面自己让位，不再盖住任何东西）；
    //    · 没有 ✕、没有阴影、没有圆角 —— 它不是弹出来的东西，是页面底部的一条状态栏；
    //    · 只在选中游戏时占用高度，切走/取消选中就自动收起。
    //  条上只放三件事：这是谁、现在什么状态（可点换方案）、能做什么。
    public class LibActionBar : Panel
    {
        public DlssgGame Game;
        Image cover;
        string coverKey;

        public event Action<DlssgGame> Install, Launch, Details, PickScheme;
        string tagCache; Color dotCache;      // 状态摘要（SetGame 时算一次，绘制零 IO）

        readonly FlatBtn bInstall, bLaunch;
        Rectangle rChip, rDetail;
        bool hotChip, hotDetail;

        public LibActionBar()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            bInstall = MkBtn("安装并应用", BtnKind.Primary);
            bLaunch = MkBtn("启动", BtnKind.Default);
            bInstall.Click += delegate { if (Game != null && Install != null) Install(Game); };
            bLaunch.Click += delegate { if (Game != null && Launch != null) Launch(Game); };
        }

        FlatBtn MkBtn(string text, BtnKind k)
        {
            var b = new FlatBtn();
            b.Text = text;
            b.Kind = k;
            b.Corner = Theme.Bg;          // 圆角外的四角 = 状态栏底色（整条与页面同底，不是一张卡）
            b.Font = Theme.F(9.5f);
            Controls.Add(b);
            return b;
        }

        public void SetGame(DlssgGame g)
        {
            Game = g;
            string key = g == null ? "" : (Lib.IdOf(g) + "|" + g.Title);
            if (key != coverKey)
            {
                coverKey = key;
                if (cover != null) { try { cover.Dispose(); } catch { } cover = null; }
                if (g != null && !Program.ShareBuild)
                    try { cover = CoverArt.Card(g, Theme.S(28), Theme.S(42)); } catch { }
            }
            RefreshState();
            PerformLayout();
            Invalidate();
        }

        void RefreshState()
        {
            tagCache = ""; dotCache = Theme.TextFaint;
            if (Game == null) return;
            string a = Game.Ignored ? "" : (Game.Installed ? Plan.Current(Game.Dir) : "");
            Color dot;
            string tag;
            if (Game.Ignored) { tag = "已忽略"; dot = Theme.Warn; }
            else if (a == Plan.Opti) { tag = "方案 A"; dot = Theme.Ok; }
            else if (a == Plan.D030) { tag = "DLSS MFG " + Dlssg030.Ver; dot = Theme.Ok; }
            else if (a == Plan.D040) { tag = "DLSS MFG " + Dlssg040.Ver; dot = Theme.Ok; }
            else if (a == Plan.Nvbr) { tag = "方案 B"; dot = Theme.Ok; }
            else if (a == Plan.Legacy) { tag = "旧代理"; dot = Theme.Ok; }
            else if (Game.Installed) { tag = "已开启"; dot = Theme.Ok; }
            else if (Game.HasFrameGen) { tag = "可接入"; dot = Theme.Accent; }
            else { tag = "无组件"; dot = Theme.TextFaint; }
            tagCache = tag;
            dotCache = dot;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && cover != null) { try { cover.Dispose(); } catch { } cover = null; }
            base.Dispose(disposing);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            //  ⚠ 构造期 Controls.Add 会立刻触发一次 PerformLayout：那时按钮字段还是 null
            if (bInstall == null || bLaunch == null) return;
            int h = Theme.S(30);
            int cy = (Height - h) / 2;
            int right = Width - Theme.S(18);
            bLaunch.SetBounds(right - Theme.S(76), cy, Theme.S(76), h);
            bInstall.SetBounds(bLaunch.Left - Theme.S(8) - Theme.S(100), cy, Theme.S(100), h);
            rDetail = new Rectangle(bInstall.Left - Theme.S(14) - Theme.S(76), cy, Theme.S(76), h);
            rChip = new Rectangle(Theme.S(18), cy, Theme.S(150), h);
        }

        //  顶部一条两端渐隐的发丝线：和标题栏、页脚同一套语言 —— 它是"区域边界"，不是"卡片边缘"。
        void PaintTopLine(Graphics g)
        {
            int w = Width;
            if (w <= 4) return;
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, 1),
                       Color.FromArgb(0, Theme.Line), Theme.Line, 0f))
            {
                var cb = new ColorBlend(4);
                cb.Colors = new Color[] { Color.FromArgb(0, Theme.Line), Theme.Line, Theme.Line, Color.FromArgb(0, Theme.Line) };
                cb.Positions = new float[] { 0f, 0.08f, 0.9f, 1f };
                br.InterpolationColors = cb;
                g.FillRectangle(br, 0, 0, w, 1);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            PaintTopLine(g);
            if (Game == null) return;

            //  只留"现在什么状态 + 能做什么"：名字和封面在主视觉里已经有一份，
            //  同屏出现两遍就是噪音（用户看第一版截图时应该就是被这个劝退的）。
            //  ⚠ 状态在 SetGame 里算好（见 RefreshState）：Plan.Current 会摸文件 + 验签，
            //  留在 OnPaint 里等于"鼠标每动一下就读一遍磁盘"。
            string tag = tagCache ?? "";
            Color dot = dotCache;

            Theme.FillRound(g, rChip, Theme.RBtn, hotChip ? Theme.Hover : Theme.Subtle);
            int ds = Theme.S(6);
            using (var b = new SolidBrush(dot))
                g.FillEllipse(b, rChip.X + Theme.S(11), rChip.Y + (rChip.Height - ds) / 2, ds, ds);
            Theme.DrawTxt(g, tag, Theme.F(9f),
                new Rectangle(rChip.X + Theme.S(24), rChip.Y, rChip.Width - Theme.S(24) - Theme.S(20), rChip.Height),
                dot == Theme.Ok ? Theme.OkText : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var p = new Pen(Theme.TextFaint, 1.4f))
            {
                int cx = rChip.Right - Theme.S(13), cy2 = rChip.Y + rChip.Height / 2 - Theme.S(1), q = Theme.S(3);
                g.DrawLine(p, cx - q, cy2 - q / 2, cx, cy2 + q / 2);
                g.DrawLine(p, cx, cy2 + q / 2, cx + q, cy2 - q / 2);
            }

            Theme.DrawTxt(g, "详细设置 ›", Theme.F(9f), rDetail,
                hotDetail ? Theme.Accent : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool a = rChip.Contains(e.Location), b = rDetail.Contains(e.Location);
            if (a != hotChip || b != hotDetail)
            {
                hotChip = a; hotDetail = b;
                Cursor = (a || b) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotChip || hotDetail)
            {
                hotChip = hotDetail = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Game != null)
            {
                if (rChip.Contains(e.Location)) { if (PickScheme != null) PickScheme(Game); return; }
                if (rDetail.Contains(e.Location)) { if (Details != null) Details(Game); return; }
            }
            base.OnMouseDown(e);
        }
    }

    //  库页主视觉（2026-10-24 第 132 轮）
    //  ---------------------------------------------------------------------------
    //  用户连说两次"沉浸感不够 / 还是差点意思"。前面的微调（间距、角标、大标题）
    //  都只是把"一屏小贴纸"整理得更整齐 —— 真问题是**页面上没有主角**。
    //  Steam 的商店页、Apple TV 的首页，第一眼都是一张占满宽度的画面；
    //  我们之前第一眼是一排小卡片。
    //  这里给库页一个 22:5 的宽幅 hero：当前选中的游戏（默认最近玩过的那个），
    //  启动按钮压在画面右下。点下面的卡片，它就换人 —— 不离开库页。
    public class LibHero : Control
    {
        public DlssgGame Game;
        Image img;
        Image prev;                 // 上一张（交叉淡出用，200ms 后释放）
        Anim aFade;
        string imgKey;
        public event Action<DlssgGame> Launch, Details;
        Rectangle rLaunch, rDetail;
        bool hotLaunch, hotDetail;

        public LibHero()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            aFade = new Anim(this, Motion.Normal, Motion.Decel);
            aFade.Snap(1);
        }

        //  ★ 主视觉的高度**跟着内容区宽度走**（第 142 轮）。
        //  原来写死 168：内容区 842 宽时是 5:1 —— 又薄又长的一条，16:9 的素材被横着切一刀，
        //  只剩角色胸口那一段，第一眼看不出"这是主角"，反而像"一条被裁掉的图"。
        //  按 3.5:1 取（Steam 的 library hero 是 3.1，Xbox 约 2.7），夹在 [164, 300]：
        //  窄窗口不比原来更矮（卡片墙要留出两排），宽窗口也不会长成"封面 + 一行字"。
        //  ⚠ 上限别抬：hero 一高，首屏就只剩 hero，卡片墙被挤到屏幕外。
        public static int HeightFor(int w)
        {
            if (w <= 0) return Theme.S(164);
            int h = (int)Math.Round(w / 3.5);
            return Math.Max(Theme.S(164), Math.Min(Theme.S(300), h));
        }

        public void SetGame(DlssgGame g)
        {
            Game = g;
            string k = g == null ? "" : (Lib.IdOf(g) + "|" + Theme.S(900).ToString());
            if (k != imgKey)
            {
                imgKey = k;
                //  交叉淡入：旧图留在 prev 里垫底，新图 200ms 淡上来。
                //  硬切在换游戏时是"啪"一下换脸，Apple TV / Steam 都是淡过去。
                if (prev != null) { try { prev.Dispose(); } catch { } prev = null; }
                if (img != null) { prev = img; img = null; }
                if (g != null && !Program.ShareBuild)
                {
                    try { img = CoverArt.Hero(g, Theme.S(900)); } catch { }
                    if (img == null) { try { img = CoverArt.Card(g, Theme.S(400), Theme.S(168)); } catch { } }
                }
                aFade.Snap(0);
                aFade.To(1);
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (img != null) { try { img.Dispose(); } catch { } img = null; }
                if (prev != null) { try { prev.Dispose(); } catch { } prev = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Theme.S(34);
            rLaunch = new Rectangle(Width - Theme.S(112) - Theme.S(16), Height - Theme.S(16) - h, Theme.S(112), h);
            rDetail = new Rectangle(rLaunch.Left - Theme.S(88) - Theme.S(6), Height - Theme.S(16) - h, Theme.S(88), h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool a = rLaunch.Contains(e.Location), b = rDetail.Contains(e.Location);
            if (a != hotLaunch || b != hotDetail)
            {
                hotLaunch = a; hotDetail = b;
                Cursor = (a || b) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotLaunch || hotDetail)
            {
                hotLaunch = hotDetail = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Game != null)
            {
                if (rLaunch.Contains(e.Location)) { if (Launch != null) Launch(Game); return; }
                if (rDetail.Contains(e.Location)) { if (Details != null) Details(Game); return; }
                if (Launch != null) Launch(Game);      // 点画面任意处 = 启动（它是这张卡的全部意义）
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (Game == null || Width <= 0 || Height <= 0) return;
            OnLayoutClean();

            var full = new Rectangle(0, 0, Width, Height);
            double fade = Math.Max(0, Math.Min(1, aFade.Value));
            if (fade >= 1 && prev != null) { try { prev.Dispose(); } catch { } prev = null; }

            //  画布：prev 打底、img 按 fade 叠上去
            if (img != null || prev != null)
            {
                var old = g.Clip;
                try
                {
                    using (var gp = Theme.Round(full, Theme.RCard)) g.SetClip(gp);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    if (prev != null) DrawCover(g, prev, 1.0);
                    if (img != null) DrawCover(g, img, prev == null ? 1.0 : fade);
                    g.InterpolationMode = InterpolationMode.Default;
                }
                finally { g.Clip = old; old.Dispose(); }
            }
            else Theme.FillRound(g, full, Theme.RCard, Theme.Subtle);

            //  底 + 左两侧压暗：文字要压在画面最亮的地方也能读
            using (var gp = Theme.Round(full, Theme.RCard))
            using (var br = new LinearGradientBrush(full, Color.Black, Color.Black, 90f))
            {
                var cb = new ColorBlend(4);
                cb.Colors = new Color[] { Color.FromArgb(0, 6, 9, 14), Color.FromArgb(0, 6, 9, 14),
                                          Color.FromArgb(96, 6, 9, 14), Color.FromArgb(214, 6, 9, 14) };
                cb.Positions = new float[] { 0f, 0.42f, 0.72f, 1f };
                br.InterpolationColors = cb;
                g.FillPath(br, gp);
            }
            using (var gpL = Theme.Round(full, Theme.RCard))
            using (var brL = new LinearGradientBrush(full, Color.Black, Color.Black, 0f))
            {
                var cbL = new ColorBlend(3);
                cbL.Colors = new Color[] { Color.FromArgb(120, 6, 9, 14), Color.FromArgb(0, 6, 9, 14), Color.FromArgb(0, 6, 9, 14) };
                cbL.Positions = new float[] { 0f, 0.55f, 1f };
                brL.InterpolationColors = cbL;
                g.FillPath(brL, gpL);
            }

            Theme.EdgeRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.RCard, Theme.CardEdge, 1f);

            //  ---- 左下：名字 + 状态 ----
            int tx = Theme.S(24);
            Font fName = Theme.FM(19f);
            Theme.DrawPhotoText(g, Game.Title, fName,
                new Rectangle(tx, Height - Theme.S(74), Math.Max(Theme.S(120), Width - Theme.S(360)), fName.Height),
                Color.White);
            string sub = HeroSub();
            if (sub.Length > 0)
                Theme.DrawPhotoText(g, sub, Theme.F(9.5f),
                    new Rectangle(tx, Height - Theme.S(46), Math.Max(Theme.S(120), Width - Theme.S(360)), Theme.S(18)),
                    Color.FromArgb(205, 255, 255, 255));

            //  ---- 右下：详细设置 + 启动 ----
            Theme.DrawTxt(g, "详细设置 ›", Theme.F(9f), rDetail,
                Color.FromArgb(hotDetail ? 255 : 215, 255, 255, 255),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            Theme.FillRound(g, rLaunch, rLaunch.Height / 2,
                hotLaunch ? Color.FromArgb(255, 255, 255) : Color.FromArgb(238, 255, 255, 255));
            Theme.DrawTxt(g, "启动游戏", Theme.FM(9.5f), rLaunch,
                Color.FromArgb(16, 20, 28),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        //  按宽度铺满、偏顶焦点裁切（角色立绘头部位于上部，严禁对称居中削头）
        void DrawCover(Graphics g, Image im, double alpha)
        {
            if (im == null || alpha <= 0.004) return;
            double sc = Math.Max((double)Width / im.Width, (double)Height / im.Height);
            int dw = (int)Math.Round(im.Width * sc), dh = (int)Math.Round(im.Height * sc);
            // 焦点保护：多出高度顶部仅裁切 12%（留白），其余下置，确保全屏/宽屏下角色额头眼睛完整展现
            int extraY = Math.Max(0, dh - Height);
            int top = Math.Min(Theme.S(80), (int)Math.Round(extraY * 0.12));
            var dst = new Rectangle((Width - dw) / 2, -top, dw, dh);
            if (alpha >= 0.996) { g.DrawImage(im, dst); return; }
            var cm = new System.Drawing.Imaging.ColorMatrix();
            cm.Matrix33 = (float)alpha;
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(im, dst, 0, 0, im.Width, im.Height, GraphicsUnit.Pixel, ia);
            }
        }

        void OnLayoutClean()
        {
            int h = Theme.S(34);
            rLaunch = new Rectangle(Width - Theme.S(112) - Theme.S(20), Height - Theme.S(18) - h, Theme.S(112), h);
            rDetail = new Rectangle(rLaunch.Left - Theme.S(84) - Theme.S(10), Height - Theme.S(18) - h, Theme.S(84), h);
        }

        //  副标题：状态 + 最近游玩（"可接入 · 昨天玩过"）
        string HeroSub()
        {
            string a = "";
            try
            {
                if (Game.Installed)
                {
                    string p = Plan.Current(Game.Dir);
                    if (p == Plan.Opti) a = "方案 A";
                    else if (p == Plan.D030) a = "DLSS MFG " + Dlssg030.Ver;
                    else if (p == Plan.D040) a = "DLSS MFG " + Dlssg040.Ver;
                    else if (p == Plan.Nvbr) a = "方案 B";
                    else a = "已开启";
                }
                else if (Game.HasFrameGen) a = "可接入帧生成";
            }
            catch { }
            string when = "";
            try
            {
                if (Game.LastPlayed > 0)
                {
                    var t = new DateTime(1970, 1, 1, 8, 0, 0, DateTimeKind.Local).AddSeconds(Game.LastPlayed);
                    TimeSpan d = DateTime.Now - t;
                    if (d.TotalMinutes < 60) when = "刚刚玩过";
                    else if (t.Date == DateTime.Today) when = "今天玩过";
                    else if (t.Date == DateTime.Today.AddDays(-1)) when = "昨天玩过";
                    else if (d.TotalDays < 30) when = (int)d.TotalDays + " 天前玩过";
                }
            }
            catch { }
            //  只显示「最近玩过」：方案状态在底部状态栏已经有一份，两处都写就是重复
            if (when.Length > 0) return when;
            if (a.Length > 0) return a;
            return "";
        }
    }

    //  封面网格：固定格宽、列数随宽度浮动（Playnite / Steam 的算法）。
    //  ⚠ 高度必须**主动**算：FlowLayoutPanel 在 WrapContents 下不会把换行算进 AutoSize，
    //    不自己写 FitHeight 的话整面墙只能显示第一行，其余全被父容器裁掉。
    public class CoverGrid : FlowLayoutPanel
    {
        //  ★ 第 142 轮：格宽改成**由可用宽度倒算**（Steam / Playnite 的算法）。
        //  原来格宽写死 174，列数 = Width/174 取整 —— 内容区 842 宽时正好卡在 4.84，
        //  取整得 4 列、右边空出 146px 一条灰；窗口再宽一点又会突然跳成 5 列。
        //  现在先按理想格宽四舍五入定列数，再用「可用宽 / 列数」定格宽：
        //  卡片宽度稳定在 156~168，而且**永远铺满**，右边不再有残条。
        //  ⚠ 卡片尺寸只改 Size、不重建：封面位图是 2 倍烘焙，DrawImage 会缩放到当前尺寸，
        //    零解码、零闪烁（第 124 轮踩过"重建 = 700ms"的坑）。
        //  ⚠ 必须是**属性**，不能是字段初始化器：字段初始化器在构造那一刻求值，
        //    那时 Theme.S 的 DPI 缩放还没生效 —— 实测拿到 174（未缩放），
        //    而页面构建时拿的是 200（1.15 倍），两边算出不同的列数，
        //    结果卡片按 4 列排、CellW 却写着 5 列的 168，位置和尺寸对不上。
        public int IdealCellW { get { return Theme.S(174); } }   // 期望格宽（卡片 162 + 间距 12）
        public int CellW = Theme.S(174), CellH = Theme.S(255);
        public string Placeholder = "";
        public Action ScanRequested;            // 空态主行动按钮（懒创建，见 OnPaint）触发
        public bool ScanEnabled = true;         // 扫描进行中置 false：空态不显示主按钮（避免"正在扫描…"下再摆一颗"扫描游戏"）
        FlatBtn btnEmpty;
        bool busy;

        public int CardW { get { return Math.Max(Theme.S(96), CellW - Theme.S(12)); } }
        public int CardH { get { return CardW * 3 / 2; } }

        //  按当前宽度定格宽 / 卡片尺寸（幂等：算出同样的值就直接返回）
        void FitCell()
        {
            int w = ClientSize.Width;
            if (w <= IdealCellW) return;                  // 宽度还没定（启动初期）→ 不算
            int cols = Math.Max(1, (int)Math.Round((double)w / IdealCellW));
            int cw = w / cols;
            int cwCard = Math.Max(Theme.S(96), cw - Theme.S(12));
            int ch = cwCard * 3 / 2 + Theme.S(12);
            if (cw == CellW && ch == CellH) return;
            CellW = cw; CellH = ch;
            int tw = CardW, th = CardH;
            foreach (Control c in Controls)
            {
                var t = c as CoverTile;
                if (t != null && (t.Width != tw || t.Height != th)) t.Size = new Size(tw, th);
            }
        }

        public CoverGrid()
        {
            WrapContents = true;
            FlowDirection = FlowDirection.LeftToRight;
            AutoScroll = false;                 // 整页只有一条滚动条（Pg 的）
            //  顶部留 12px：第一排贴着工具条底边太紧，肉眼读成"被裁了"
            Padding = new Padding(0, Theme.S(12), 0, 0);
            Margin = Padding.Empty;
            BackColor = Theme.Bg;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            FitHeight();
        }

        //  ★ 格宽必须在 base 之前定：OnLayout 一旦跑完，卡片位置就按旧 CellW 排好了 ——
        //  事后再改 CellW 只会得到"尺寸是新的、坐标是旧的"的错位（实测卡在 X=606、该在 174）。
        protected override void OnClientSizeChanged(EventArgs e)
        {
            FitCell();
            base.OnClientSizeChanged(e);
            FitHeight();
        }

        void FitHeight()
        {
            if (busy) return;
            //  宽度还没确定（启动初期 ClientSize≈0）→ 不算：否则会算出"每行 1 张、共 28 行"的假高度，
            //  WinForms 会把它缓存成滚动区，之后滚出一大片空白（2026-09-15 实测）。
            if (ClientSize.Width <= CellW) return;
            busy = true;
            bool changed = false;
            try
            {
                int perRow = Math.Max(1, ClientSize.Width / CellW);
                int rows = Controls.Count == 0 ? 0 : (Controls.Count + perRow - 1) / perRow;
                //  Padding 不参与行数计算，必须自己加回来，否则最后一行会被父容器裁掉
                int want = rows == 0
                    ? (Placeholder.Length > 0 ? Theme.S(150) : 0)
                    : rows * CellH + Padding.Top + Padding.Bottom;
                if (Height != want) { Height = want; changed = true; }
            }
            finally { busy = false; }

            //  ★ 高度变了必须通知所属分区重排（2026-10-24 第 127 轮补回）。
            //  漏掉这一段的表现很隐蔽：窗口一改大小，网格行数变了、卡片也重排了，
            //  但 Pg 的滚动范围还停在旧值 —— 于是能滚到内容之外，看到整片空白。
            //  （第 124 轮重写 CoverGrid 时把 CardGrid 里这段丢了，探针用 SetScrollY(9999) 才抓到。）
            if (!changed) return;
            Sec sec = Tag as Sec;
            if (sec != null) { try { sec.Resync(); } catch { } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            //  顶部一条两端渐隐的发丝线：把"工具条"和"卡片墙"分成两段。
            //  没有它，一排筛选字和第一排封面之间只有 12px 空白，读起来是"一坨"（用户：简陋）。
            if (Width > Theme.S(40))
            {
                var g0 = e.Graphics;
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, Width, 1),
                           Color.FromArgb(0, Theme.Line), Theme.Line, 0f))
                {
                    var cbl = new ColorBlend(4);
                    cbl.Colors = new Color[] { Color.FromArgb(0, Theme.Line), Theme.Line, Theme.Line, Color.FromArgb(0, Theme.Line) };
                    cbl.Positions = new float[] { 0f, 0.06f, 0.94f, 1f };
                    br.InterpolationColors = cbl;
                    g0.FillRectangle(br, 0, 0, Width, 1);
                }
            }

            if (Placeholder.Length == 0 || Width <= 0) { if (btnEmpty != null) btnEmpty.Visible = false; return; }
            //  空态 = 图标 + 标题 + 说明 + 主行动按钮（「扫描游戏」）。有卡片时按钮隐藏、空态不画。
            bool hasTiles = false;
            foreach (Control c in Controls) { if (c is CoverTile) { hasTiles = true; break; } }
            if (hasTiles) { if (btnEmpty != null) btnEmpty.Visible = false; return; }
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string head = Placeholder, tail = null;
            int bar = Placeholder.IndexOf('|');
            if (bar > 0) { head = Placeholder.Substring(0, bar); tail = Placeholder.Substring(bar + 1); }
            Font f1 = Theme.F(11f), f2 = Theme.F(9.5f);
            //  纵向排布全部居中：图标 48 → 标题 → 说明 → 按钮（宽/高与页头「扫描游戏」同规格）
            int y = Theme.S(28);
            var ir = new Rectangle((Width - Theme.S(48)) / 2, y, Theme.S(48), Theme.S(48));
            Theme.DrawIconGamepad(g, ir, Theme.TextFaint);
            y += Theme.S(48) + Theme.S(12);
            Theme.DrawTxt(g, head, f1, new Rectangle(0, y, Width, f1.Height), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += f1.Height;
            if (tail != null)
            {
                Theme.DrawTxt(g, tail, f2, new Rectangle(0, y + Theme.S(6), Width, f2.Height),
                    Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += f2.Height + Theme.S(6);
            }
            if (btnEmpty == null)
            {
                btnEmpty = new FlatBtn();
                btnEmpty.Text = "扫描游戏";
                btnEmpty.Width = Theme.S(92);
                btnEmpty.Height = Theme.S(32);
                btnEmpty.Click += delegate { var h = ScanRequested; if (h != null) h(); };
                Controls.Add(btnEmpty);
            }
            btnEmpty.Visible = ScanEnabled;
            if (ScanEnabled)
            {
                btnEmpty.Left = (Width - btnEmpty.Width) / 2;
                btnEmpty.Top = y + Theme.S(16);
            }
        }
    }


    //  自绘日志视图（2026-10-20）。
    //  为什么不再用 RichTextBox：本项目的 RichTextBox 出现"有文本却不显示" ——
    //  实测内容 426 字、`GetPositionFromCharIndex(0)={1,0}`、前景 #0F172A / 背景 #F8FAFC，
    //  截图上却一片空白；而且它自带一条原生竖滚动条（又是一处系统灰带）。
    //  日志只需要"看最后几行 + 滚轮翻"，自绘足够，还顺手把内存钉死：只留最近 MaxLines 行。
    public class LogView : Control
    {
        readonly List<string> lines = new List<string>();
        public int MaxLines = 500;          // 上限：日志再多也不涨内存
        int scroll;                          // 0=贴底跟随最新；>0=往上翻了 N 行
        public Color TextColor = Theme.Text;

        public LogView()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Subtle;
            Font = Theme.F(9f);
        }
        public int LineCount { get { return lines.Count; } }
        public void Clear() { lines.Clear(); scroll = 0; Invalidate(); }
        public void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lines.Add(line);
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
            Invalidate();
        }
        int LineH() { return Math.Max(1, Font.Height + Theme.S(2)); }
        int VisRows() { return Math.Max(1, (ClientSize.Height - Theme.S(6)) / LineH()); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int vis = VisRows();
            int max = Math.Max(0, lines.Count - vis);
            int delta = e.Delta > 0 ? 3 : -3;      // 向上滚 = 离开底部
            int next = Math.Max(0, Math.Min(max, scroll + delta));
            bool atBoundary = (max == 0) || (scroll == 0 && e.Delta < 0) || (scroll == max && e.Delta > 0);
            if (next != scroll)
            {
                scroll = next;
                Invalidate();
            }
            if (atBoundary)
            {
                for (Control p = Parent; p != null; p = p.Parent)
                {
                    Pg pg = p as Pg;
                    if (pg != null)
                    {
                        pg.ScrollByWheel(e.Delta);
                        break;
                    }
                }
            }
            base.OnMouseWheel(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            if (lines.Count == 0) return;
            int lh = LineH();
            int vis = VisRows();
            int end = Math.Max(0, lines.Count - scroll);
            int start = Math.Max(0, end - vis);
            int y = Theme.S(3);
            for (int i = start; i < end; i++)
            {
                Theme.DrawTxt(g, lines[i], Font,
                    new Rectangle(Theme.S(2), y, Math.Max(0, ClientSize.Width - Theme.S(4)), lh), TextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                y += lh;
            }
        }
    }

    //  自绘悬停提示（2026-10-20）。
    //  为什么不用系统 ToolTip：它是淡黄底 + 深灰边 + 20 年前的字体，鼠标一停就冒出来，
    //  和整套自绘界面完全不是一套东西 —— 这是"原生 chrome 混入"里出现频率最高的一处。
    //  对外保持 SetToolTip(Control, string) 的调用方式，内部换成自己的弹层。
    public class FlatTip
    {
        public int InitialDelay = 400, AutoPopDelay = 8000;
        readonly Dictionary<Control, string> texts = new Dictionary<Control, string>();
        readonly HashSet<Control> hooked = new HashSet<Control>();
        readonly System.Windows.Forms.Timer wait = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer stay = new System.Windows.Forms.Timer();
        Control cur;
        TipForm form;

        public FlatTip()
        {
            wait.Interval = InitialDelay;
            wait.Tick += delegate { wait.Stop(); ShowFor(cur); };
            stay.Tick += delegate { stay.Stop(); Hide(); };
        }

        public void SetToolTip(Control c, string text)
        {
            if (c == null) return;
            texts[c] = text == null ? "" : text;
            if (!hooked.Add(c)) return;
            c.MouseEnter += delegate
            {
                cur = c;
                string t; if (!texts.TryGetValue(c, out t) || t.Length == 0) return;
                wait.Interval = InitialDelay; wait.Start();
            };
            c.MouseLeave += delegate
            {
                if (cur == c) cur = null;
                wait.Stop(); Hide();
            };
            c.Disposed += delegate { texts.Remove(c); hooked.Remove(c); };
        }

        void Hide()
        {
            stay.Stop();
            if (form != null && form.Visible) form.Hide();
        }

        //  外部主动控制（表格单元格悬停用）：内容和位置由调用方给，不走 400ms 延时
        public void ShowAt(Point screen, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (form != null && form.Visible && form.CurText == text) return;   // 同一格不重复刷
            if (form == null) form = new TipForm();
            wait.Stop();
            form.SetText(text);
            Point p = screen;
            p.Offset(Theme.S(14), Theme.S(20));
            Rectangle scr = Screen.FromPoint(p).WorkingArea;
            if (p.X + form.Width > scr.Right) p.X = Math.Max(scr.Left, scr.Right - form.Width - Theme.S(4));
            if (p.Y + form.Height > scr.Bottom) p.Y = Math.Max(scr.Top, screen.Y - form.Height - Theme.S(10));
            form.Location = p;
            if (!form.Visible) form.Show();
            form.BringToFront();
            stay.Interval = AutoPopDelay; stay.Start();
        }
        public void HideNow() { Hide(); }

        void ShowFor(Control c)
        {
            if (c == null || c.IsDisposed) return;
            string t; if (!texts.TryGetValue(c, out t) || t.Length == 0) return;
            if (form == null) form = new TipForm();
            form.SetText(t);
            //  贴着光标右下，出屏时往回收：提示被屏幕边缘切掉比不显示更难看
            Point p = Cursor.Position;
            p.Offset(Theme.S(14), Theme.S(20));
            Rectangle scr = Screen.FromPoint(p).WorkingArea;
            if (p.X + form.Width > scr.Right) p.X = Math.Max(scr.Left, scr.Right - form.Width - Theme.S(4));
            if (p.Y + form.Height > scr.Bottom) p.Y = Math.Max(scr.Top, Cursor.Position.Y - form.Height - Theme.S(10));
            form.Location = p;
            if (!form.Visible) form.Show();
            form.BringToFront();
            stay.Interval = AutoPopDelay; stay.Start();
        }
    }

    //  提示弹层本体：无边框、不抢焦点、圆角 + 发丝边，字体/配色跟界面一致
    class TipForm : Form
    {
        string text = "";
        public string CurText { get { return text; } }
        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint
                     | ControlStyles.AllPaintingInWmPaint, true);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt-Tab
                return cp;
            }
        }
        public void SetText(string t)
        {
            text = t == null ? "" : t;
            //  ⚠ 用**离屏位图**测，别用 CreateGraphics()：窗体还没 Show 时拿到的 DC 不可靠，
            //    实测会量出偏小的尺寸 → 提示框把文字裁掉（第一版就是这么翻车的）。
            Size sz;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                //  高度上限给 int.MaxValue：传 0 会被当成"高度 0"，量出来的宽度也偏小
                sz = TextRenderer.MeasureText(g, text, Theme.F(9f), new Size(Theme.S(320), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            //  +26/+17：MeasureText 给的是**紧贴**的尺寸，只加 20 会让最后一个字贴边
            Size = new Size(sz.Width + Theme.S(26), sz.Height + Theme.S(17));
            try
            {
                int rad = Theme.S(8);
                using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), rad))
                    Region = new Region(path);
            }
            catch { }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var br = new SolidBrush(Theme.Panel)) g.FillRectangle(br, ClientRectangle);
            using (var pen = new Pen(Theme.Line, 1f)) using (var gp = Theme.Round(r, Theme.S(8))) g.DrawPath(pen, gp);
            Theme.DrawTxt(g, text, Theme.F(9f),
                new Rectangle(Theme.S(10), Theme.S(7), Width - Theme.S(20), Height - Theme.S(14)),
                Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    //  表格容器：**自绘表头** + ListView（2026-10-20）。
    //  为什么不直接用原生表头：`lv.DrawColumnHeader` 在本项目**从不触发**（打日志零次，
    //  去掉 SetWindowTheme 也一样），表头永远是系统那条灰带 —— #F0F0F0 底、竖分隔线、
    //  #A0A0A0 硬下边线、20px 高、文字只内缩 6px。用户说的"这一栏表现不好"就是它。
    //  自绘之后：白底、无竖线、一条发丝线、30px 高、字与单元格共用同一个左基准。
    public class TableBox : Panel
    {
        public ListView List;
        public int HeadH = Theme.S(30);
        public TableBox(ListView lv)
        {
            List = lv;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            lv.HeaderStyle = ColumnHeaderStyle.None;
            lv.BorderStyle = BorderStyle.None;
            Controls.Add(lv);
            Fit();
        }
        public void Fit()
        {
            if (List == null) return;
            int h = HeadH + Math.Max(0, List.Height);
            if (Height != h) Height = h;
            List.SetBounds(0, HeadH, Math.Max(0, Width), Math.Max(0, Height - HeadH));
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (List != null) List.SetBounds(0, HeadH, Math.Max(0, Width), Math.Max(0, Height - HeadH));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor == Color.Empty ? Theme.Panel : BackColor);
            if (List == null) return;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fh = Theme.FM(8.5f);
            int x = 0;
            for (int i = 0; i < List.Columns.Count; i++)
            {
                ColumnHeader col = List.Columns[i];
                if (x >= Width) break;
                var r = new Rectangle(x + Theme.S(4), 0, Math.Max(0, col.Width - Theme.S(8)), HeadH);
                //  ⚠ 不加 NoPadding：单元格那条路（DrawSubItem）用的是默认内边距，
                //    表头若用 NoPadding 就会比单元格左 4px（实测 303 vs 307）。两边算法一致才对齐。
                Theme.DrawTxt(g, col.Text, fh, r, Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                x += col.Width;
            }
            //  唯一的一条分隔：表头与内容的发丝线（不画竖线 —— 竖线是"老表格"的观感来源之一）
            using (var p = new Pen(Theme.Line, 1f))
                g.DrawLine(p, 0, HeadH - 1, Width, HeadH - 1);
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
            public Func<int, int> AutoH;   // 非空时：先给宽度，再按宽度回算高度（主视觉这类）
        }

        int padX, padY, gap, labelW, top;
        //  ★ 第 142 轮：左基准线只有一个。
        //  原来有两条互不相干的线：卡片内容在 padX+inset（Pg 内 = GapSize+5+28 = 57），
        //  而 Plain 分区（大标题 / 工具条 / hero / 封面网格）直接吃 padX（= GapSize+28 = 52）。
        //  于是①页大标题比卡片左缘右缩 23px，肉眼读成"标题歪了"；
        //  ②库页整条链比其它页的卡片外框右缩 23px，切页时内容左右边界会跳一下。
        //  现在 Plain 分区的内容直接贴到**卡片外框**那条线（ShadowMargin），
        //  卡片外元素与卡片外框共用一条基准，卡片内的文字自己再缩 padX —— 两层各自整齐。
        int BaseX { get { return Plain ? ShadowMargin : padX + inset; } }
        // ★ 卡片四周留给阴影的边距（2026-10-21）：
        //   阴影是比卡片大一圈的几层圆角矩形。以前卡片直接铺满整个控件，阴影往外扩的部分
        //   被 Control 的矩形剪切掉 → 圆角外侧只剩一圈**方角**的残影（用户截图：卡片左下角那块灰）。
        //   现在卡片内缩这么多像素，阴影正好填满控件，圆角处不再出现直角。
        //   Plain（无标题工具条）不画卡片，所以 inset = 0，布局完全不受影响。
        int inset;
        //  ★ 第 150 轮：5 → 7。SoftShadow 的层数 = 边距，层数越多投影越"化开"。
        //    原来 5 层在浅色上是一圈能看出边界的灰 —— 那就是"廉价卡片"的来源；
        //    7 层 + 同样的 8% 发丝边，边界化开，卡片才像"浮起"。
        public static int ShadowMargin { get { return Theme.S(7); } }
        Color contentBg;   // 行内容底色：盒式分区=Panel，工具条（Plain）=Bg。写死 Panel 会在灰底上留白补丁
        //  "下一行之前多留 n px"。为什么需要：gap 是全局量，但**语义分组**需要不同的行距 ——
        //  「结论 + 评星」是一组判词，要贴紧；「依据」是另一层内容，要拉开。全用 14 就成了
        //  "每行都一样远"，读起来没有层次（方案推荐卡改版时发现）。
        //  空行（KText 且无文字）不消耗这份额外间距，否则空 Note 的游戏会白多一段空白。
        readonly Dictionary<int, int> extras = new Dictionary<int, int>();

        // 把"框外底色"确定性地下发给整棵子树
        void ApplySurface(Control c)
        {
            if (c == null) return;
            FlatBtn fb = c as FlatBtn; if (fb != null) fb.Corner = contentBg;
            RoundCheck rc = c as RoundCheck; if (rc != null) rc.Corner = contentBg;
            RoundRadio rr = c as RoundRadio; if (rr != null) rr.Corner = contentBg;
            RoundField rf = c as RoundField; if (rf != null) rf.SetCorner(contentBg);
            RoundCombo rb = c as RoundCombo; if (rb != null) rb.SetCorner(contentBg);
            BreathingLabel bl = c as BreathingLabel; if (bl != null) bl.Corner = contentBg;
            RadialGauge rg = c as RadialGauge; if (rg != null) rg.Corner = contentBg;
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
        public bool FullSpan;              // 双栏页面下：拉伸至全宽（如仪表盘底部的系统信息卡）

        public bool Plain;                 // 工具条：不画分区底与描边、不做标签列缩进
        int titleY, titleH;          // 标题位置（OnPaint 画强调条用）
        Label titleLbl;              // 标题本体（Mark() 要改它的颜色）
        readonly List<Item> items = new List<Item>();
        string secTitle = "";
        public string TitleText = "";      // 只读用：Pg.FindSec 靠它做标签栏跳转

        public Sec(string title)
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Theme.Bg;
            //  留白按 4/8 栅格取整（Refactoring UI 的核心：不接受"随手 14px"这种值）：
            //  内边距 20 / 项间距 14 / 标签列 104，比原来各档都大一档，卡片才"透气"。
            //  ★ 留白加大（2026-10-24 第 137 轮）：原来 内边距 20/16、行距 14、卡片间距 18 ——
            //  内外几乎相等，整页没有「呼吸」。用户说「一点儿都不高端」，根子在这里：
            //  高端观感来自「每样东西周围都空得起来」，不是来自加装饰。
            //  ★ 第 141 轮再放大一档：高雅最直接来源是留白。
            //  macOS 设置的行间距约 22、卡片内边距 24~28；我们原来 18/20/24 每一项都差 4~6px，
            //  叠起来就是"挤"。这次一次推到对齐（内边距 28/24、行间距 22）。
            //  ★ 第 149 轮：padY 24 → 18。padY 在这里唯一的作用是**卡片底部内边距**，
            //    而顶部内边距 t - inset = S(13) —— 上 13 / 下 24，每张卡的下半截都多空 11px。
            //    单看一张不明显，一屏三张卡就是"内容浮在上半、下面一片白"，这是最容易被读成
            //    "没做完"的那种空。18 让上下接近对称（底部仍略大 5px，视觉重心偏上才稳）。
            padX = Theme.S(28); padY = Theme.S(18); gap = Theme.S(22); labelW = Theme.S(112);
            Width = Theme.S(600);
            inset = 0;          // Plain 工具条不画卡片，也就没有阴影边距（下面非 Plain 分支才置上）
            secTitle = title == null ? "" : title;
            TitleText = secTitle;

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

            inset = ShadowMargin;               // 从这里往下都是"有卡片"的路径：四周留出阴影边距
            int t = Theme.S(13) + inset;
            var lb = new TxtLbl();
            lb.AutoSize = true; lb.UseMnemonic = false; lb.BackColor = Theme.Panel;
            //  标题 10f → 11.5f：和正文 9f 拉开 2.5pt（≈3.4px），接近"标题/正文至少差 4px"的经验线。
            //
            //  ★ 括号补充拆成第二枚**浅色小字**（2026-10-20）：「优化项明细（当前系统实际设置）」
            //    整串按标题字重画出来是一坨，主标题和限定语没有层级。拆开后标题只有主干，
            //    括号里那句退到 TextFaint 小一号 —— 既保住信息，又立刻有了"文气"。
            string titleMain = title, titleNote = "";
            int bp = title.IndexOf('（');
            if (bp <= 0) bp = title.IndexOf('(');
            if (bp > 0) { titleMain = title.Substring(0, bp).TrimEnd(); titleNote = title.Substring(bp); }
            lb.Text = titleMain; lb.Font = Theme.FM(13f); lb.ForeColor = Theme.Text;
            lb.Location = new Point(padX + inset, t);                  // 第 136 轮：图标槽取消，标题直接贴左基准线
            Controls.Add(lb);
            titleLbl = lb;
            if (titleNote.Length > 0 && !Plain)
            {
                var ln = new TxtLbl();
                ln.AutoSize = true; ln.UseMnemonic = false; ln.BackColor = Theme.Panel;
                ln.Text = titleNote; ln.Font = Theme.F(10.5f); ln.ForeColor = Theme.TextDim;
                //  基线对齐：小字比标题矮，按高度差往下压，两者的底边才在同一条线上
                ln.Location = new Point(lb.Right + Theme.S(6), t + Math.Max(0, lb.Height - ln.Height - Theme.S(1)));
                Controls.Add(ln);
            }
            titleY = t; titleH = lb.Height;
            top = t + lb.Height + Theme.S(12);
            Height = top + padY + inset;
            contentBg = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var __bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(__bg, 0, 0, Width, Height);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Plain) return;

            // 卡片内缩 inset，阴影正好扩到控件边缘 —— 不再被 Control 剪成直角
            var r = new Rectangle(inset, inset,
                Math.Max(1, Width - inset * 2 - 1), Math.Max(1, Height - inset * 2 - 1));

            // 柔和投影：层数 = 边距（外扩量正好等于预留空间，一圈都不越界）
            Theme.SoftShadow(g, r, Theme.RPanel, inset);

            // 卡片底板
            Theme.FillRound(g, r, Theme.RPanel, Theme.Panel);

            // 顶边微高光线 (漫反射与立体感)
            int hiAlpha = Theme.Dark ? 20 : 90;
            using (var pLight = new Pen(Color.FromArgb(hiAlpha, 255, 255, 255), 1f))
            {
                int arcD = Theme.RPanel * 2;
                g.DrawArc(pLight, r.X, r.Y, arcD, arcD, 180, 90);
                g.DrawLine(pLight, r.X + Theme.RPanel, r.Y, r.Right - Theme.RPanel, r.Y);
                g.DrawArc(pLight, r.Right - arcD, r.Y, arcD, arcD, 270, 90);
            }

            //  浅色不再画 1px 描边：阴影 + 边框同时用，是"廉价卡片"最典型的组合
            //  （Refactoring UI 与多篇设计规范都明确二选一）。深色保留细边 —— 那里底色分层弱，需要边。
            //  ★ 2026-10-21 反过来补上了：这条结论成立的前提是"边框用 Line 那种实色"（#E2E8F0，
            //    在白卡上是明显的一条线）。Apple 的做法是 8% 黑的**发丝边**——肉眼几乎看不见，
            //    只负责把边界"钉住"，投影因此可以画得更淡。所以现在两套主题都画，但用的是 CardEdge。
            Theme.EdgeRound(g, r, Theme.RPanel, Theme.CardEdge, 1f);

            //  「领头卡」的强调色顶线已删除（2026-10-22 用户截图"很廉价"）：
            //  卡片顶边一条彩色渐隐带是典型的"模板仪表盘"记号 —— Apple 的层级从不靠
            //  给卡片描色，第一张卡的身份由"它在最上面 + 页头正下方"这一位置表达。
            //  Hero 标记保留（布局与探针还在引用），只是不再画出任何东西。

            //  ★ 分区标题不再画图标（2026-10-24 第 136 轮）。
            //  原来的方案是按标题关键词分派 12 种单线图标（联动→手柄、负载→脉搏、方案→芯片…），
            //  设计上没错，但**16px 是多线图标的死线**：芯片那种七八条横线的图形缩到 16px
            //  就是一团灰点，同屏五六张卡各顶一个灰点，看起来像"没画完"。
            //  库页已经走"内容为主、装饰最少"（纯图 + 标题），这里对齐：只留标题，
            //  层级靠字号和字重 —— Apple 的分组标题（设置、音乐）本来也没有图标。
            //  槽位一起取消：标题直接贴 padX，与正文、按钮共享同一条左基准线。
        }

        Label MkLabel(string text, Font f, Color c)
        {
            var l = new TxtLbl();
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
                    //  ★ 一行里所有控件**等高**（2026-10-23 用户截图"没对齐"）。
                    //    FlowLayoutPanel 只做"左上"排布，不认 Anchor：26~28px 的下拉框旁边放一个
                    //    20px 的复选框，复选框就贴在行的上沿 —— 实测文字中心差 2.5px，看着像没对基准线。
                    //    统一抬到 28px 之后，各自的文字都在自己高度里居中，基准线自然对齐。
                    //    复选框/单选框是 AutoSize 的，先按首选宽度定好宽再把 AutoSize 关掉，否则会被拉回默认宽度。
                    if (c.Height < Theme.S(28))
                    {
                        int w = c.Width;
                        if (c.AutoSize) { w = c.GetPreferredSize(Size.Empty).Width; c.AutoSize = false; }
                        c.Width = w;
                        c.Height = Theme.S(28);
                    }
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
            if (cap1 != null && cap1.Length > 0) it.A = MkLabel(cap1, Theme.F(10f), Theme.TextDim);
            if (f1 != null) { if (w1 > 0) f1.Width = w1; Controls.Add(f1); it.B = f1; }
            if (cap2 != null && cap2.Length > 0) it.C = MkLabel(cap2, Theme.F(10f), Theme.TextDim);
            if (f2 != null) { if (w2 > 0) f2.Width = w2; Controls.Add(f2); it.D = f2; }
            items.Add(it);
            return it.A as Label;
        }
        // 整行说明文字（自动换行，高度按当前宽度实测）
        public Label Body(string text)
        {
            //  正文 9.5 → 10.5pt（2026-10-29 第 158 轮，对齐「自考工作台」的 Body=14px）：
            //  12.7px 的中文正文是"密排小灰字"，14px 才是阅读层。布局按文字自动量高，
            //  卡片跟着长高，不产生固定值要同步。9pt=12px 仍是全站下限（micro 档）。
            var l = MkLabel(text, Theme.F(10.5f), Theme.TextDim);
            l.AutoSize = false;
            // ★ 回指所属分区：SetBody() 运行期改文案时靠它找到本分区并重排。
            //   忘了这一行，说明文字一变长就会被旧高度裁掉（而且不报错，只能靠肉眼发现）。
            l.Tag = this;
            var it = new Item(); it.Kind = KText; it.A = l;
            items.Add(it);
            return l;
        }
        public T Body<T>(T l) where T : Label
        {
            if (l != null)
            {
                l.AutoSize = false;
                l.Tag = this;
                Controls.Add(l);
                var it = new Item(); it.Kind = KText; it.A = l;
                items.Add(it);
            }
            return l;
        }
        // 固定高度的说明行：文字变化不改变分区高度 → 不触发整页重排。
        // 选中游戏状态行必须用它（否则每次点卡片都会改高度 → 整页重排 → 滚动位置被重算）。
        // ★ 刻意不设 Tag：SetBody 找不到所属分区就不会调 Resync。
        public Label BodyFixed(string text, int lines)
        {
            var l = MkLabel(text, Theme.F(10.5f), Theme.TextDim);   // 与 Body 同层（第 158 轮）
            l.AutoSize = false;
            l.AutoEllipsis = true;
            var it = new Item();
            it.Kind = KText;
            it.A = l;
            it.FixedH = Math.Max(1, lines) * (TextRenderer.MeasureText("测", l.Font).Height + Theme.S(2));
            items.Add(it);
            return l;
        }

        // 覆盖"下一行之前"的间距（**绝对值**，0 = 两行贴紧；不调用就用全局 gap）。
        //  为什么用绝对值：调排版时脑子里想的是"这两行之间留 8px"，而不是"在 14 上减 6"。
        //  空行（KText 且无文字）不消耗这份间距 —— 否则 Note 为空的游戏会白多一段空白。
        //  取 >= 0 保证两行的框不会互相压住（负值会让文字叠字）。
        public void Space(int px)
        {
            extras[items.Count] = Math.Max(0, Theme.S(px));
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
        // 整行控件，高度由宽度算（主视觉 / 横幅这类"应该跟着内容区变"的区块）
        public T Block<T>(T c, Func<int, int> autoH) where T : Control
        {
            Controls.Add(c);
            var it = new Item(); it.Kind = KBlock; it.B = c; it.AutoH = autoH;
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
            //  允许换行：窗口拉窄 / 高 DPI 下，原来那一排按钮会被右边界整排裁掉（不换行），
            //  用户看到的就是"按钮消失了"。宽度上限在 DoLayout 里按区块实际宽度下发。
            fl.WrapContents = true;
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
        // 下一行之前额外留 n px（绝对值，0 = 两行贴紧）。见 Space() 注释。
        // 推荐方案高亮：标题染成该分区的色相，其它保持正文色。
        //  为什么不整卡描边：描边 + 阴影是"廉价卡片"组合（本文件 OnPaint 注释），
        //  且页面会同时显示多个方案区块，只有"标题变色"这一笔不会加重视觉噪音。
        public void Mark(bool hot)
        {
            if (titleLbl == null) return;
            Color want = hot ? SecHue(secTitle) : Theme.Text;
            if (titleLbl.ForeColor != want) { titleLbl.ForeColor = want; titleLbl.Invalidate(); }
        }

        public void DoLayout()
        {
            int pxl = BaseX;                        // 卡片内的内容左边界（Plain 分区 = 卡片外框线）
            int w = Width - pxl * 2;
            if (w < Theme.S(200)) w = Theme.S(200);
            int y = top;

            ApplySurface(this);      // 每次排版都把真实分区底色下发一遍（廉价且不会漏）

            for (int ii = 0; ii < items.Count; ii++)
            {
                Item it = items[ii];
                //  行前间距覆盖（Space()）：空行不消耗 —— 否则 Note 为空的游戏会白多一段
                int gb;
                if (extras.TryGetValue(ii, out gb)
                    && !(it.Kind == KText && it.FixedH <= 0
                         && (it.A == null || it.A.Text == null || it.A.Text.Length == 0)))
                    y += gb - gap;
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
                        l.Location = new Point(pxl, y);
                        y += l.Height + gap;
                        continue;
                    }
                    //  空字符串不占行：卡片里的"次级说明"（指标 / 注意）多数游戏上是空的，
                    //  留一个零内容的标签会白撑一行 —— 那正是"整块空空的"观感的来源。
                    if (l.Text == null || l.Text.Length == 0)
                    {
                        l.AutoSize = false;
                        l.Height = 0;
                        l.Width = w;
                        l.Location = new Point(pxl, y);
                        continue;                 // 不推进 y、不加 gap
                    }
                    // 换行高度用 TextRenderer 自己量（含 \n 与自动折行），不依赖 Label.PreferredHeight
                    // 的内部实现 —— 量出来的高度和 Label 实际渲染用同一套 GDI 度量，必然一致。
                    var sz = TextRenderer.MeasureText(l.Text, l.Font, new Size(w, int.MaxValue),
                                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    l.AutoSize = false;
                    l.Height = sz.Height + Theme.S(2);
                    l.Width = w;
                    l.Location = new Point(pxl, y);
                    y += l.Height + gap;
                    continue;
                }
                if (it.Kind == KBlock || it.Kind == KFlow)
                {
                    var b = it.B;
                    if (b == null) continue;
                    if (it.Kind == KBlock)
                    {
                        b.Width = w;
                        //  ★ 第 142 轮：高度随宽度走（主视觉）。
                        //  在一个地方量完就收敛 —— 不用在窗体 Resize 里外挂钩子，
                        //  也就不会出现"窗口变了但高度停在旧值"的错位。
                        if (it.AutoH != null)
                        {
                            int nh2 = it.AutoH(w);
                            if (nh2 > 0 && b.Height != nh2) b.Height = nh2;
                        }
                    }
                    else
                    {
                        //  按钮行按区块实际宽度换行：FlowLayoutPanel 只有在有宽度上限时才会 wrap，
                        //  否则 AutoSize 会一路向右撑开、超出部分被父容器裁掉（窗口拉窄时整排消失）。
                        b.MaximumSize = new Size(w, 0);
                        b.Size = b.GetPreferredSize(new Size(w, 0));
                    }
                    b.Location = new Point(pxl, y);
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
                        rc.Left = Math.Max(pxl, Width - pxl - rc.Width);
                        rc.Top = y + (hh - rc.Height) / 2;
                    }
                    if (lc != null)
                    {
                        int lw = Width - pxl * 2 - (rc != null ? rc.Width + Theme.S(20) : 0);
                        if (lw < Theme.S(140)) lw = Theme.S(140);
                        lc.Width = lw;
                        lc.Left = pxl;
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
                    //  列间距 12 → 32（2026-10-20）：瓦片去掉边框之后，左列的右对齐数值会和
                    //  右列的图标/标签贴在一起（"13% 🌀 内存"）。没有框线兜底，只能靠留白分组。
                    int gc = Theme.S(32);
                    int cw = (w - gc) / 2;
                    if (g1 != null) { g1.Width = cw; g1.Location = new Point(pxl, y); }
                    if (g2 != null) { g2.Width = w - cw - gc; g2.Location = new Point(pxl + cw + gc, y); }
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
                if (it.A != null) { it.A.Left = pxl; it.A.Top = y + (h - it.A.Height) / 2; }
                // ★ 标签宽度按真实文字量算，不能用固定值 labelW（102px）：
                //   标签是不透明底色（contentBg），比 labelW 长的标题会**直接盖住右侧控件** ——
                //   用户截图里那个只显示 "n.dll" 的下拉就是这么来的（"version.dll" 左半被标题糊掉）。
                int lwA = (it.A == null) ? 0 : Math.Max(labelW, it.A.Width + Theme.S(8));
                if (it.B != null)
                {
                    int avail = Width - pxl * 2 - lwA;
                    if (avail > Theme.S(48) && it.B.Width > avail) it.B.Width = avail;   // 放不下就缩，宁可窄也不重叠
                    it.B.Left = (it.A == null) ? pxl : pxl + lwA;
                    it.B.Top = y + (h - it.B.Height) / 2;
                    //  ★ 第 142 轮：单列行的「值」**右对齐到内容区右缘**。
                    //  原来是紧贴标签列左对齐，右边留一大片空 —— 卡片越宽空得越多，
                    //  读起来像"表单没写完"（窗口 1080 时右侧空出 400px）。
                    //  右对齐之后所有下拉框/输入框共用一条右边界，一列
                    //  「标签 ←……→ 值」的结构就立起来了（macOS 设置 / iOS 表单都是这一派）。
                    //  ⚠ 三条边界：
                    //   ① 只动输入类控件（RoundCombo/RoundField）。复选框、按钮自带文字，
                    //     右对齐会让它的字长长地伸向左边，读起来更乱；
                    //   ② 只动单列行。Pair 的左右两列各有各的列宽，右对齐会把左列的值
                    //     推出列外压到标签上（实测 B.Width 237 时左对齐位 135 < 标签右缘 169）；
                    //   ③ 右对齐后不能压到标签，压到就退回左对齐。
                    //   ④ **必须有标签**。`Row(null, cb, 0)` 这种"只有值"的行（详情页的「方案」下拉）
                    //     一旦右对齐，下拉框会孤零零飘到卡片右上角，和它下面那行说明文字失联 ——
                    //     那种行左对齐、与说明文字同一条左基线才对。
                    if (it.A != null && it.C == null && it.D == null
                        && (it.B is RoundCombo || it.B is RoundField))
                    {
                        int rl = Width - pxl - it.B.Width;
                        if (rl >= pxl + lwA) it.B.Left = rl;
                    }
                }
                if (it.C != null || it.D != null)
                {
                    //  ★ 第 142 轮：Pair 重新算两列。
                    //  原来右列写死 `half`、左列控件不限宽 —— 左列的下拉框右缘会**压到右列标签下面**
                    //  （标签是不透明白底，视觉上下拉框被截掉一截，箭头都没了），
                    //  右列控件也不收宽，直接顶出卡片右边被父容器裁掉。
                    //  现在两列各自给出闭区间、且**列内的值靠右对齐** ——
                    //  和单列行同一个规则（标签在左、值在右），整张卡片的右缘才连成一条线。
                    int colGap = Theme.S(16);
                    int halfX = Width / 2;
                    int leftColEnd = halfX - colGap / 2;      // 左列右缘
                    int rightColX = halfX + colGap / 2;       // 右列起点
                    int rightColEnd = Width - pxl;            // 右列右缘
                    if (it.B != null)
                    {
                        int bw = Math.Min(it.B.Width, leftColEnd - it.B.Left);
                        int bl = leftColEnd - bw;
                        if (bw > Theme.S(64) && bl >= it.B.Left) { it.B.Width = bw; it.B.Left = bl; }
                        else if (it.B.Left + it.B.Width > leftColEnd) it.B.Width = leftColEnd - it.B.Left;
                    }
                    if (it.D != null)
                    {
                        int lwC = Math.Max(labelW, (it.C == null ? 0 : it.C.Width + Theme.S(8)));
                        int dl = rightColX + lwC;
                        int dw = Math.Min(it.D.Width, rightColEnd - dl);
                        it.D.Width = dw;
                        it.D.Left = Math.Max(dl, rightColEnd - dw);
                        it.D.Top = y + (h - it.D.Height) / 2;
                        //  ★ 右列的标签**跟着值走**：值既然靠右，标签要贴着它 ——
                        //  否则「档位」孤零零留在列首、下拉框在列尾，中间空 140px，读起来两个东西失联。
                        if (it.C != null)
                        {
                            int cl = it.D.Left - it.C.Width - Theme.S(10);
                            it.C.Left = Math.Max(rightColX, cl);
                            it.C.Top = y + (h - it.C.Height) / 2;
                        }
                    }
                    else if (it.C != null) { it.C.Left = rightColX; it.C.Top = y + (h - it.C.Height) / 2; }
                }
                y += h + gap;
            }
            int nh = y - gap + padY + inset;
            if (nh != Height) Height = nh;
            Invalidate();
        }

        // 运行期文案变化后重排：先按新文字量出自己的新高度，再让所属页面把后面的分区一起下移。
        // 只做 DoLayout 而不通知 Pg，本分区变高了但后面分区的 y 不变 -> 会互相压住。
        public void Resync()
        {
            DoLayout();
            var p = Parent as Pg;
            if (p != null)
            {
                p.InvalidateLayout();
                p.Reflow();
            }
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

        //  滚动时浮出来、停手 550ms 后淡回去 —— mac 的 overlay scrollbar 行为
        //  （Win11 也是自动隐藏那一派，只是不跟着滚动显形）。
        //  只由**程序性滚动**（滚轮 / 惯性）触发；鼠标真悬停在条上时归 hover 管，两者互不打断：
        //  Pulse 的收尾里判了 hover/drag，正在用就绝不把它淡掉。
        Timer hideT;
        public void Pulse()
        {
            aShow.To(1.0);
            if (hideT == null)
            {
                hideT = new Timer();
                hideT.Interval = 550;
                hideT.Tick += delegate { hideT.Stop(); if (!hover && !drag) aShow.To(0.0); };
            }
            hideT.Stop();
            hideT.Start();
        }

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
            //  悬停 / 拖拽时滑块**变宽**（4 → 7px）：macOS 与 Win11 都是这一派（细条常驻、
            //  用到时才变粗），比恒定宽度更容易让人意识到"这里可以抓"。
            int w = Theme.S(4) + (int)Math.Round(Theme.S(3) * sp);
            int x = (Width - w) / 2;
            var r = new Rectangle(x, top, Math.Max(Theme.S(4), w), len);
            Theme.FillRound(g, r, r.Width / 2, c);
        }
    }

    // ======================= 页面容器（纵向堆叠 + 自适应宽度） =======================
    public class Pg : Panel
    {
        readonly List<Sec> secs = new List<Sec>();
        readonly List<HeadBox> heads = new List<HeadBox>();
        bool busy, pending;
        //  卡片之间的呼吸：14 → 18（2026-10-20）。14 时卡片挨得太紧，一页看起来像"堆起来的"；
        //  18 让每张卡都有独立边界，代价只是页面高 4px×卡数（本来就能滚）。
        public int GapSize = Theme.S(24);
        //  ★ 卡片(分区)之间的**纵向**间距（2026-10-29 第 155 轮）：24 与卡内行距 22 只差 2px，
        //    组内 22 / 组间 24 ≈ 1.09，三张卡读起来是"一条清单"而不是"三组"。
        //    经验线是组间 ≈ 组内 × 1.5 → 32（4/8 栅格）。
        //    ⚠ GapSize 同时是页面左右内边距与 hero 的对齐基准，不能直接改它 ——
        //    所有纵向推进单独走 SecGap；hero 后的 +GapSize 是与顶部偏移 mg 配对的补偿，保持不动。
        public int SecGap = Theme.S(32);
        //  完整重排（LayoutOnce relayout=true）的执行次数。重排闸的判据：
        //  几何自检修好后，内容与宽度都没变的 Reflow 必须命中缓存 → 计数不再增长。
        //  （ui_snapshot real 模式打印 [reflow] runs before → after）
        public int LayoutRuns;
        //  ★ 第 142 轮：分区的「阅读宽度」上限（>0 生效，超出则整列居中）。
        //  表单页（设置 / 说明 / 性能优化）本来就是一列「标签 ←→ 值」。
        //  窗口拉到 1920 时卡片跟着变宽，可控件还是那么窄 ——
        //  一行里两头各站一个东西、中间空出 400px，读起来像"表单没写完"。
        //  纸面排版里这叫"栏宽"：正文栏不会因为纸更宽就无限拉长。
        //  库页 / 仪表盘不设（它们要铺满：封面墙要列数、指标瓦片要宽度）。
        public int MaxSecW;
        // ★ 页面顶部"消融"总长（2026-09-20 二稿）：页头只有 58px 高，装不下一条够长的
        //   白→灰过渡；把剩下的一段交给 Pg 自己的顶部渐变接着画，两段同曲线。
        //   用户原话"色块颜色变化还是不够丝滑"—— 17px 的渐变在这个尺度上就是个"糊掉的小段"。
        public int TopFadeLen = Theme.S(110);

        // ---- 可选左栏（Steam 式「列表 + 详情」）----
        //  LeftWidth = 0（默认）= 原来的单列，现有页面**一行都不用改**；
        //  只有显式 AddLeft 的页面才走双栏 —— 出问题也只影响那一页，不会拖垮全局。
        public int LeftWidth;
        public double LeftRatio;           // > 0 时：按 (avail - colGap) * LeftRatio 动态计算左栏宽度
        public bool GridRowAlign;          // Bento Grid 模式：双栏逐行等高对齐，底部不参差
        //  hero 只占右区（Steam 库的形态）：左栏列表从顶部开始，页头不跨越左栏
        public bool HeadRightOnly;
        //  内容装不满一屏时把空白让给页头（封面），而不是在底下留一片死白。
        //  只有帧生成详情页开它 —— 那一页本来就短，别的页一开就会把页头拉成一张大图。
        public bool FillHead;
        public int HeadMaxGrow = Theme.S(260);   // 页头最多长这么高（再多就成"封面 + 一条字"）
        readonly List<Sec> leftSecs = new List<Sec>();
        readonly List<Sec> visLeft = new List<Sec>();
        readonly List<Sec> visRight = new List<Sec>();
        readonly List<Sec> visFull = new List<Sec>();
        //  ⚠ 必须同时进 secs：LayoutOnce / ContentHeight 遍历的是 secs，
        //  只进 leftSecs 的话这个分区永远不会被排版（表现就是左栏一片空白）。
        public void AddLeft(Sec s)
        {
            if (s == null) return;
            secs.Add(s);
            leftSecs.Add(s);
            Controls.Add(s);
        }

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
        public void SetScrollY(int v)
        {
            int prev = sb.Value;
            try
            {
                //  ★ 保险（2026-10-24 第 127 轮）：滚动上界以**子控件实测位置**为准。
                //  滚动范围平时由 Reflow 里的 want 决定，而 want 是缓存值 —— 布局有中间态时
                //  （比如网格先按 1 列算出 3600 高、再变成 4 列 782 高），它可能停在旧值上，
                //  于是能滚到内容之外，看到整片空白（探针 SetScrollY(9999) 实测过）。
                //  子控件此刻的真实高度不会骗人：它们被整体上移了 ScrollY，加回来即可。
                int real = 0;
                for (int i = 0; i < Controls.Count; i++)
                {
                    Control c = Controls[i];
                    if (c == null || !c.Visible) continue;
                    int bottom = c.Bottom + sb.Value;
                    if (bottom > real) real = bottom;
                }
                if (real > 0) real += Theme.S(24);
                //  ⚠ 只在"明显越过内容"时兜底，不能按 real 精确夹 ——
                //  real 是子控件实测值，布局中间态下它本身也可能偏大；
                //  按它精确夹会把**合法**的滚动目标也截断（实测把惯性滑行从 400ms 压到 179ms）。
                if (v > real) v = Math.Max(0, real - ClientSize.Height);
                if (v < 0) v = 0;
                sb.Value = v;                 // FlatScroll 自己再夹一次（两者取小，不冲突）
                if (!chasing) scrollTarget = sb.Value;   // 程序性滚动随时收回追踪目标，两边不打架
            }
            catch { }
            //  内容真的动了 → 收掉还钉在旧屏幕坐标上的下拉菜单（2026-10-31 用户"菜单乱晃"）。
            //  挂在这里一个钩子就覆盖了全部滚动路径：滚轮（直通/惯性）、拖滚动条、程序性滚动。
            //  值没变（布局中间态反复夹到同一点）不动它，避免无谓的 Invalidate。
            if (sb.Value != prev) RoundCombo.CloseOpenPopup();
        }

        //  标签栏用：按标题关键词找分区 + 滚过去。
        //  这样标签栏可以放在页面顶部，不必等后面那些分区构造完再绑定。
        public Sec FindSec(string key)
        {
            if (key == null || key.Length == 0) return null;
            foreach (Sec s in secs)
                if (s != null && s.TitleText != null
                    && s.TitleText.IndexOf(key, StringComparison.Ordinal) >= 0) return s;
            return null;
        }

        public void ScrollToSec(Sec s)
        {
            if (s == null) return;
            int t = s.Top + ScrollY - Theme.S(8);
            SetScrollY(t < 0 ? 0 : t);
        }

        // ---- 滚轮节流 + 停手补画（v3.9.0）-----------------------------------------
        // 用户 2026-09-20 再次反馈：「上下滑太快还是会有有的选项变成空白」。
        // 之前几轮已经把"漏画"该堵的都堵了（改真滚动条、HardRepaint 整棵子树、自绘逐格自检+自愈），
        // 但**根上的开销没降**：每个 WM_MOUSEWHEEL 都立刻"搬一遍所有子控件 + 整棵子树 RDW_UPDATENOW"。
        // 滚轮/触控板一秒能发上百个事件（高分辨率滚轮更密），绘制把消息泵吃满，
        // 来不及画的那几帧就留下"背景铺了、内容没画"的死区 —— 正是用户描述的现象。
        // 做法：事件只记账，15ms（≈60fps）统一应用一次；停手 120ms 后再补一次完整重绘兜底。
        //  15ms 的延迟人眼看不出来，但每一帧都是完整的。
        //  （字段声明与滚轮/追踪模型合并放在下面"惯性滚动"一节，2026-10-22）

        // ---- 惯性滚动（2026-10-22 重做：目标点 + 临界阻尼追踪）----------------------
        //  旧模型两阶段：输入期"瞬移"（一格 56px 当帧直接跳过去）+ 离手后指数滑行。
        //  用户两次反馈"阻尼感廉价/拉垮"——病根在输入期：物理滚轮一格就是一次 56px 的位置
        //  突变，帧与帧之间全是台阶；再叠一段滑行，"先瞬移、再飘一段"就是廉价感的来源。
        //  新模型只有一个阶段（Chrome smooth-scroll 同款）：
        //    滚轮只把"目标滚动值"往前记账，位置每帧朝目标做指数逼近（帧率无关），
        //    大落后量时限速巡航 —— 单格是 180ms 的顺滑补间，连滚是持续的追随，
        //    停手后剩下的落后量自然成为惯性尾巴，没有阶段切换的"接缝"。
        //  硬约束：
        //    ① Motion.Enabled 为假（系统关了动画）→ 退回瞬移老路，一格就是一格。
        //    ② 落后量最多 maxChase：跟不上的输入不丢（总量不少走），但尾巴有界。
        //    ③ 撞到顶/底立刻把目标收回：不顶着墙空转。
        //    ④ 程序性滚动（SetScrollY / 拖滚动条）随时收回目标，两边不打架。
        const int WheelIdleMs = 120;      // 停手多久算一轮结束（关动画路径用）
        const double ChaseTau = 42.0;     // 逼近时间常数 ms：63%@42ms、95%@126ms、单格全程 ~180ms
        const double ChaseVmax = 1.35;    // px/ms 巡航上限（≈1350px/s）
        double scrollTarget;              // 目标滚动值（浮点）
        double carry;                     // 亚像素余量：每帧只搬整数像素，零头攒着 → 细碎滚动才连续
        long chaseAt;                     // 上一帧追踪时刻（dt 用）
        bool chasing;                     // MoveBy 正在搬位置：ValueChanged 不回写目标
        int wheelAccum;
        int lastWheelAt;
        static Timer wheelTimer;
        static readonly List<Pg> wheelPending = new List<Pg>();

        public void ScrollByWheel(int delta)
        {
            if (!sb.Visible) return;
            int now = Environment.TickCount;
            //  新手势（间隔够久）：以当前位置重新出发 —— 拖过滚动条 / 程序性滚动之后
            //  目标可能已经过期，锚在旧目标上会把页面拽回去
            if (unchecked(now - lastWheelAt) > 400)
            {
                scrollTarget = sb.Value;
                chaseAt = 0;
            }
            wheelAccum += delta;
            lastWheelAt = now;
            if (!wheelPending.Contains(this)) wheelPending.Add(this);
            EnsureWheelTimer();
        }

        //  返回 false = 这一轮结束了（调用方把它移出待办表）
        bool WheelTick()
        {
            if (!Motion.Enabled)
            {
                //  关了动画就沿用老行为：立即应用 + 停手 120ms 后收尾一次（Reflow + 整棵重绘）
                if (wheelAccum != 0) { ApplyWheelDirect(); return true; }
                if (unchecked(Environment.TickCount - lastWheelAt) > WheelIdleMs) { FinishWheel(); return false; }
                return true;
            }
            if (wheelAccum != 0) ApplyWheelTarget();
            double diff = scrollTarget - sb.Value;
            if (Math.Abs(diff) < 0.5 && Math.Abs(carry) < 0.5) { FinishWheel(); return false; }
            Chase(diff);
            return true;
        }

        //  关动画路径：目标点直接落地（保持"一格就是一格"的旧手感）
        void ApplyWheelDirect()
        {
            int d = wheelAccum; wheelAccum = 0;
            double notch = SystemInformation.MouseWheelScrollDelta;
            if (notch <= 0) notch = 120;
            MoveBy(-Theme.S(56) * (d / notch));
        }

        //  动画路径：滚轮只记账（正文见类注释）
        void ApplyWheelTarget()
        {
            int d = wheelAccum; wheelAccum = 0;
            double notch = SystemInformation.MouseWheelScrollDelta;
            if (notch <= 0) notch = 120;
            double dv = -Theme.S(56) * (d / notch);
            //  反向输入 = 用户改主意：**先清掉还没展示的旧尾巴**再记账 ——
            //  否则快速下甩之后点一下上滚，页面会先把旧目标走完才回头（探针 §4 抓的就是这个）。
            double curLag = scrollTarget - sb.Value;
            if (curLag != 0 && (dv == 0 || Math.Sign(dv) != Math.Sign(curLag))) scrollTarget = sb.Value;
            // 移除 400px 硬截断，维护真实的未消费位移蓄水池，范围由 sb.MaxValue 兜底
            scrollTarget = Math.Max(0, Math.Min(sb.MaxValue, scrollTarget + dv));
        }

        void Chase(double diff)
        {
            long now = Motion.Now;
            if (chaseAt <= 0) chaseAt = now - 15;
            long dt = now - chaseAt;
            if (dt <= 0) return;
            if (dt > 60) dt = 60;                    // 卡顿之后别一次跳太远
            chaseAt = now;
            //  指数逼近（帧率无关）：tau=42ms 的临界阻尼——到位快、无过冲、没有"尾颤"
            //  移除 1.35px/ms 巡航限速，恢复自然非线性加速度曲线
            double step = diff * (1 - Math.Exp(-dt / ChaseTau));
            if (MoveBy(step)) scrollTarget = sb.Value;   // 顶到头：收回剩余目标，不顶着墙较劲
        }

        //  按"滚动值变化量"移动；亚像素零头留在 carry 里，下一次接着用。
        //  返回 true = 真的撞到了顶/底（一个像素都没动）。
        bool MoveBy(double dv)
        {
            carry += dv;
            int move = (int)Math.Round(carry);
            if (move == 0) return false;
            carry -= move;
            int before = sb.Value;
            chasing = true;
            try { SetScrollY(before + move); }
            finally { chasing = false; }
            if (sb.Value == before) { carry = 0; return true; }
            sb.Pulse();
            return false;
        }

        void FinishWheel()
        {
            scrollTarget = sb.Value; carry = 0; chaseAt = 0;
            //  收尾校准：滚动期间只搬位置不重排，万一期间有条目高度变了
            //  （状态文字多一行、卡片网格换行等），这里统一重排，避免"半截文字"留到下次刷新。
            try { Reflow(); } catch { }
            Theme.HardRepaint(this);
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
                        //  WheelTick 返回 false = 这一轮（输入 + 惯性）彻底结束，它自己已收尾
                        //  （停手后补一次"整棵子树立刻重绘"：节流期间万一有帧被系统合并掉，
                        //    这一步保证留在屏幕上的是完整画面 —— TickCount 会回绕，差值必须 unchecked）
                        if (!p.WheelTick())
                        {
                            p.lastWheelAt = 0;
                            wheelPending.RemoveAt(i);
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
            // 双缓冲与自绘背景：确保整页背景彻底擦除，绝不留显存残影
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            sb.Dock = DockStyle.Right;
            sb.Minimum = 0;
            //  滚动只搬位置，不重排内容（见 LayoutOnce 的 relayout 参数）。
            //  chasing=false 时的 Value 变化 = 拖滚动条 / 页面翻页等"外部"滚动 → 追踪目标跟着归位，
            //  否则下一次滚轮会把页面拽回过期目标（滚轮只记账的代价，2026-10-22）。
            sb.ValueChanged += delegate
            {
                if (chasing) { if (!laying) LayoutOnce(sb.Visible, false); return; }
                scrollTarget = sb.Value;
                if (!laying) LayoutOnce(sb.Visible, false);
            };
            Controls.Add(sb);
        }
        bool heroAssigned;
        public void Add(Sec s)
        {
            // 每页的**第一张真卡片**（跳过工具条）自动成为"领头卡"
            if (!heroAssigned && s != null && !s.Plain) { s.Hero = true; heroAssigned = true; }
            secs.Add(s); Controls.Add(s);
        }
        public HeadBox AddHead(string title, string sub)
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
            return h;
        }
        //  给本页第一条可见页头放一次入场动画（切页时调用）
        public void PlayHeadEnter()
        {
            foreach (HeadBox hb in heads)
            {
                if (hb != null && hb.Visible) { hb.PlayEnter(); return; }
            }
        }

        int lastW = -1, lastCount = -1, lastTotal = -1;
        int laidBottom;                 // LayoutOnce 定位后实测的内容底（ContentHeight 的同源来源）
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
                //  ★ 起点与 LayoutOnce 同源（2026-10-29）：无可见页头时首块从 SecGap 起步
                //    （LayoutOnce：`if (!anyHead) y = SecGap`）。自检原来从 0 起步 →
                //    凡是 Plain 页头 / 无页头的页面自检恒 false，Reflow 的重排闸形同虚设。
                bool anyHead = false;
                foreach (HeadBox hb0 in heads) if (hb0 != null && hb0.Visible) { anyHead = true; break; }
                int y = anyHead ? 0 : SecGap;
                foreach (HeadBox hb in heads)
                {
                    if (!hb.Visible) continue;                      // 隐藏的页头不参与自检
                    if (hb.Height < Theme.S(20)) return false;      // 高度塌了
                    //  hero 卡片化后的 Top 带 +mg 顶部偏移（LayoutOnce 同时把高度减了一次
                    //  GapSize 配平）—— 自检原来没算它，带横幅的详情页同样恒 false。
                    //  推进量也同源：hero 后的 +GapSize 是偏移补偿，不是分区间距。
                    bool headRight = HeadRightOnly && LeftWidth > 0;
                    int mg = (!headRight && hb.Hero) ? GapSize : 0;
                    if (hb.Top + sy != y + mg) return false;         // 位置没落到算出来的地方
                    y += hb.Height + (hb.Hero ? GapSize : SecGap);
                }
                // Bento Grid 模式：双栏逐行等高网格对齐，检查配对卡片的高度和 Top 是否平齐
                if (GridRowAlign && (LeftWidth > 0 || LeftRatio > 0))
                {
                    var visL = new List<Sec>();
                    var visR = new List<Sec>();
                    var visF = new List<Sec>();
                    foreach (Sec s in secs)
                    {
                        if (!s.Visible) continue;
                        if (s.FullSpan) visF.Add(s);
                        else if (leftSecs.Contains(s)) visL.Add(s);
                        else visR.Add(s);
                    }
                    int curY = y;
                    int maxP = Math.Max(visL.Count, visR.Count);
                    for (int i = 0; i < maxP; i++)
                    {
                        Sec sL = i < visL.Count ? visL[i] : null;
                        Sec sR = i < visR.Count ? visR[i] : null;
                        if (i > 0) curY += SecGap;
                        int rowH = 0;
                        if (sL != null && sR != null)
                        {
                            if (sL.Height < Theme.S(30) || sR.Height < Theme.S(30)) return false;
                            if (sL.Height != sR.Height) return false;
                            if (sL.Top + sy != curY || sR.Top + sy != curY) return false;
                            rowH = sL.Height;
                        }
                        else if (sL != null)
                        {
                            if (sL.Height < Theme.S(30)) return false;
                            if (sL.Top + sy != curY) return false;
                            rowH = sL.Height;
                        }
                        else if (sR != null)
                        {
                            if (sR.Height < Theme.S(30)) return false;
                            if (sR.Top + sy != curY) return false;
                            rowH = sR.Height;
                        }
                        curY += rowH;
                    }
                    for (int i = 0; i < visF.Count; i++)
                    {
                        Sec sF = visF[i];
                        if (curY > y) curY += SecGap;
                        if (sF.Height < Theme.S(30)) return false;
                        if (sF.Top + sy != curY) return false;
                        curY += sF.Height;
                    }
                    return true;
                }
                //  双栏（AddLeft / LeftRatio）时左右列各自推进，单列断言不适用。
                if (LeftWidth > 0 || LeftRatio > 0) return true;
                foreach (Sec s in secs)
                {
                    if (!s.Visible) continue;                        // 隐藏分区不占位、不参与自检
                    if (s.Height < Theme.S(30)) return false;        // 分区被压扁（这条能抓住"封面带"）
                    if (s.Top + sy != y) return false;
                    y += s.Height + SecGap;
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

        public void InvalidateLayout() { laidBottom = 0; }
        public int ContentHeightPublic() { return ContentHeight(); }

        // 内容总高 = 各分区已算好的高度之和（DoLayout 之后才有意义）
        //  双栏时取两列中较高的那列，否则滚动条会把矮的那列也算进去、滚出一大片空白。
        int ContentHeight()
        {
            //  ★ 2026-10-30（第 169 轮）：优先用 LayoutOnce **定位后实测的内容底**。
            //    原来这里是"按 secs 重新加一遍高度"，与 LayoutOnce 的推进是两套算法 ——
            //    差一个 SecGap(32px) 就表现为"滚到底最后一行被底边裁掉"（设置页三张卡中招）。
            //    User 反馈形态完全一致的内容：库页详情（隐藏分区没过滤）、设置页（间距不同源）。
            if (laidBottom > 0) return laidBottom;
            //  hero 后的推进是 +GapSize（偏移补偿），普通页头/分区之间是 SecGap —— 与 LayoutOnce 同一条
            bool anyHead = false;
            foreach (HeadBox b in heads) if (b != null && b.Visible) { anyHead = true; break; }
            int head = anyHead ? 0 : SecGap;
            foreach (HeadBox b in heads) if (b != null && b.Visible) head += b.Height + (b.Hero ? GapSize : SecGap);
            //  ⚠ 间距只加在"块与块之间"，最后一块后面不再加（2026-10-22）：
            //    原来每块都 +GapSize，末块后面凭空多出 18px —— 滚到底还能再滑一小段、
            //    底下却什么都没有（用户："下面明明没有东西为什么还能滑"）。
            if (LeftWidth <= 0 && LeftRatio <= 0)
            {
                //  ★ 2026-10-30（第 169 轮）：隐藏分区不占位 —— 与 LayoutOnce(7421)/双栏分支(7366)
                //    同一条规则，唯独这个单列分支漏了。后果：库页选中游戏后库墙 Sec 隐藏，
                //    但它的整段高度仍被计入滚动范围 —— 滚到底就是一大片空白
                //    （用户 1.3.0 截图："游戏下面已经没有内容了，你还要放那么大的空间"）。
                int h0 = 0, n = 0;
                for (int i = 0; i < secs.Count; i++)
                {
                    if (!secs[i].Visible) continue;
                    if (n > 0) h0 += SecGap;            // 间距只加在可见块之间
                    h0 += secs[i].Height; n++;
                }
                return head + h0 + Theme.S(24);
            }
            if (GridRowAlign && (LeftWidth > 0 || LeftRatio > 0))
            {
                var visL = new List<Sec>();
                var visR = new List<Sec>();
                var visF = new List<Sec>();
                foreach (Sec s in secs)
                {
                    if (!s.Visible) continue;
                    if (s.FullSpan) visF.Add(s);
                    else if (leftSecs.Contains(s)) visL.Add(s);
                    else visR.Add(s);
                }
                int cur = 0;
                int maxP = Math.Max(visL.Count, visR.Count);
                for (int i = 0; i < maxP; i++)
                {
                    if (i > 0) cur += SecGap;
                    int hl0 = i < visL.Count ? visL[i].Height : 0;
                    int hr0 = i < visR.Count ? visR[i].Height : 0;
                    cur += Math.Max(hl0, hr0);
                }
                for (int i = 0; i < visF.Count; i++)
                {
                    if (cur > 0) cur += SecGap;
                    cur += visF[i].Height;
                }
                return head + cur + Theme.S(24);
            }
            int hl = 0, hr = 0, nl = 0, nr = 0;
            foreach (Sec s in secs)
            {
                if (!s.Visible) continue;
                if (s.FullSpan)
                {
                    int sy2 = Math.Max(hl + Math.Max(0, nl - 1) * SecGap, hr + Math.Max(0, nr - 1) * SecGap);
                    if (nl > 0 || nr > 0) sy2 += SecGap;
                    hl = sy2 + s.Height; hr = hl; nl = 1; nr = 1;
                    continue;
                }
                if (leftSecs.Contains(s)) { hl += s.Height; nl++; }
                else { hr += s.Height; nr++; }
            }
            int gapL = Math.Max(0, nl - 1) * SecGap, gapR = Math.Max(0, nr - 1) * SecGap;
            return head + Math.Max(hl + gapL, hr + gapR) + Theme.S(24);
        }

        void LayoutOnce(bool reserveVScroll)
        {
            LayoutRuns++;                       // 完整重排计数：重排闸是否命中缓存就看它涨不涨（见字段注释）
            LayoutOnce(reserveVScroll, true);
        }

        //  relayout=false：只搬位置、不重排内容 —— 滚动路径专用。
        //  为什么：原来滚动也走完整重排，每个分区都要 DoLayout（量文字、重设几十个子控件位置），
        //  游戏库 28 张卡片时一帧几十毫秒；滚轮连发时"重排"追不上"搬运"，
        //  来不及画的那几帧就留下"底色铺了、内容没画"的白色死区（用户说的"滑动过快出现白色色块"）。
        //  滚动不改宽度、不改内容，跳过重排是安全的；真正需要重排的尺寸变化仍走 Reflow。
        void LayoutOnce(bool reserveVScroll, bool relayout)
        {
            // 预留竖向滚动条宽度：不预留时内容宽度刚好等于客户区宽度，竖条一出现就
            // 把内容挤出横向滚动条（用户截图底部那条多余的横条）
            int vw = reserveVScroll ? sb.Width : 0;
            int avail = ClientSize.Width - GapSize * 2 - vw;
            if (avail < Theme.S(360)) avail = Theme.S(360);

            laying = true;
            try
            {
            //  双栏：LeftWidth > 0 或 LeftRatio > 0 时，AddLeft 进来的分区排左栏，其余排右栏。
            //  单列页面 LeftWidth=0 且 LeftRatio=0，上面算出来的 avail 原样使用，行为与改前完全一致。
            int colGap = Theme.S(16);
            int leftW = LeftRatio > 0 ? (int)((avail - colGap) * LeftRatio) : (LeftWidth > 0 ? LeftWidth : 0);
            bool capped = false;
            if (MaxSecW > 0 && leftW == 0 && avail > MaxSecW) { avail = MaxSecW; capped = true; }
            //  ★ 居中偏移必须按**滚动条宽度固定值**算，不能跟着 vw 走：
            //  vw 在滚动条隐藏时是 0、显示时是 10 —— 用它算，整列会在"这一页内容刚好不用滚"
            //  和"要滚"之间左右跳 5px（实测 实时监控 页 X=116、设置页 X=111）。
            int centerOfs = capped ? Math.Max(0, (ClientSize.Width - GapSize * 2 - sb.Width - avail) / 2) : 0;
            int rightX = GapSize + (leftW > 0 ? leftW + colGap : centerOfs);
            int rightW = leftW > 0 ? Math.Max(Theme.S(320), avail - leftW - colGap) : avail;

            int sy = sb.Value;          // 自管滚动：子控件按 -sy 真的上移
            int y = 0;

            //  ① 先量：给宽度 + 排版，拿到每个分区的真实高度。
            //  以前这段和"定位"混在一个循环里，所以页头只能按固定高度铺 —— 内容比视口短时，
            //  底下就空出半屏（用户原话"下面又空出来该怎么办"）。量完再决定页头多高，才能把空白吃掉。
            foreach (Sec s in secs)
            {
                if (!s.Visible) continue;               // 隐藏分区不占位
                bool isLeft = leftW > 0 && leftSecs.Contains(s);
                if (relayout)
                {
                    s.Width = s.FullSpan ? avail : (isLeft ? leftW : rightW);
                    s.DoLayout();                       // 先给宽度再排版（换行文字要用宽度）
                    // 分区高度不该小于一个空分区的最小值；真的塌了就按内容重算一次再定位，
                    //  否则它会以"压扁"的姿态留在页面上（用户截图里的封面带就是这么来的）。
                    if (s.Height < Theme.S(30))
                    {
                        s.Height = Math.Max(s.Height, Theme.S(30));
                        s.DoLayout();
                    }
                }
            }

            //  ② 页头撑高：内容装不满一屏时，把剩下的高度给页头（封面），而不是在底下留白。
            //  只在详情页开启（FillHead），且最多长 HeadMaxGrow —— 再高就成"封面 + 一条字"了。
            if (FillHead && heads.Count > 0)
            {
                int need = 0;
                foreach (Sec s in secs) if (s.Visible) need += s.Height + SecGap;
                //  ⚠ 这里必须用 BaseH 而不是当前 Height：用过上一次撑高的结果去算剩余空间，
                //    每次重排都会得到不同的 grow（562 → 33 → 240 …），页头会来回抖。
                int headNow = 0;
                foreach (HeadBox hb in heads) if (hb.Visible) headNow += hb.BaseH + (hb.Hero ? 0 : SecGap);
                int slack = ClientSize.Height - (headNow + need);
                int grow = Math.Max(0, Math.Min(HeadMaxGrow, slack));
                foreach (HeadBox hb in heads)
                {
                    if (!hb.Visible) continue;
                    //  BaseH 是设计高度（构造时给的），每次重排都从它算起 —— 不能累加，
                    //  否则切一次页签页头就长一截。
                    int want = hb.BaseH + grow;
                    //  hero 是"卡片"：顶部让出与左右一致的边距（否则下移后会压到下一块）
                    if (hb.Hero) want = Math.Max(Theme.S(80), want - GapSize);
                    if (hb.Height != want) hb.Height = want;
                }
            }

            //  ③ 再定位
            //  没有页头时（2026-10-23：五个页面已去掉浮动标题），首块自己让出 SecGap ——
            //  否则卡片直接贴着导航行下沿，页面像被裁掉了上边。
            //  （2026-10-29 第 155 轮：AddPageTitle 空壳已删，这个起点是唯一的"页顶留白"出处；
            //    取 SecGap 与卡片间距同一档，4/8 栅格。）
            bool anyHead = false;
            foreach (HeadBox hb0 in heads) if (hb0 != null && hb0.Visible) { anyHead = true; break; }
            if (!anyHead && y == 0) y = SecGap;
            foreach (HeadBox hb in heads)
            {
                if (!hb.Visible) continue;      // 库视图下 hero 隐藏，不占位
                // ★ 页头通栏（2026-09-20）：以前这里和分区一样按 GapSize 缩进，于是页头白底
                //   左（右）边各让出 14px，露出 Pg 的灰底 —— 顶部白带被切出两条灰竖条。
                //   页头铺满 Pg 全宽后，白色从侧栏右边界一直连到窗口右边；
                //   文字的左缩进改到 HeadBox.OnPaint 里单独给（只缩文字不缩底）。
                if (HeadRightOnly && leftW > 0)
                {
                    //  Steam 库那种：hero 只占右区，左栏列表从窗口顶部就开始
                    hb.Width = rightW;
                    hb.Location = new Point(rightX, y - sy);
                }
                else
                {
                    //  hero 内缩成一张卡片（与下方卡片同边距、同圆角），不再顶天立地。
                    //  ⚠ 必须用 GapSize：写死 S(14) 时 hero 左缘比卡片多出 4px，两条边对不齐。
                    int mg = hb.Hero ? GapSize : 0;
                    hb.Width = ClientSize.Width - mg * 2;
                    hb.Location = new Point(mg, y - sy + mg);
                }
                //  hero 的 +GapSize 与它顶部偏移 mg（构造时高度已减过一次）配对成补偿，
                //  不是分区间距 —— 横幅下缘那条"空隙"由雾化带自己占位，改 SecGap 会把内容推低 8px。
                y += hb.Height + (hb.Hero ? GapSize : SecGap);      // 与分区间距同一条（原来页头后只空 10px，读起来挤）
            }
            //  ★ 左栏起点：hero 在右区时列表从顶部开始；否则两列都从页头下沿开始
            //    （上一版把两列都写成 0，hero 被卡片整片盖住，截图里只露出左边一条）。
            int yL = (HeadRightOnly && leftW > 0) ? 0 : y;
            int yR = y;

            //  ★ 2026-10-30（第 169 轮）：间距只加在**块与块之间**，末块后面不加 —— 与
            //    ContentHeight 同一条规则（改前这里每块都 +SecGap，末块后凭空多 32px，
            //    而滚动范围不算它 ⇒ 滚到底时最后 32px 内容滚不到，最后一行被底边裁掉
            //    （用户："被挡住了" —— 设置页数据与隐私/系统信息/显示器设备三张卡都中招））。
            if (GridRowAlign && leftW > 0)
            {
                visLeft.Clear();
                visRight.Clear();
                visFull.Clear();
                foreach (Sec s in secs)
                {
                    if (!s.Visible) continue;
                    if (s.FullSpan) visFull.Add(s);
                    else if (leftSecs.Contains(s)) visLeft.Add(s);
                    else visRight.Add(s);
                }
                int curY = y;
                int maxPairs = Math.Max(visLeft.Count, visRight.Count);
                for (int i = 0; i < maxPairs; i++)
                {
                    Sec sL = i < visLeft.Count ? visLeft[i] : null;
                    Sec sR = i < visRight.Count ? visRight[i] : null;
                    if (i > 0) curY += SecGap;
                    int rowH = 0;
                    if (sL != null && sR != null)
                    {
                        rowH = Math.Max(sL.Height, sR.Height);
                        sL.Height = rowH;
                        sR.Height = rowH;
                        sL.Location = new Point(GapSize + centerOfs, curY - sy);
                        sR.Location = new Point(rightX, curY - sy);
                    }
                    else if (sL != null)
                    {
                        rowH = sL.Height;
                        sL.Location = new Point(GapSize + centerOfs, curY - sy);
                    }
                    else if (sR != null)
                    {
                        rowH = sR.Height;
                        sR.Location = new Point(rightX, curY - sy);
                    }
                    curY += rowH;
                }
                for (int i = 0; i < visFull.Count; i++)
                {
                    Sec sF = visFull[i];
                    if (curY > y) curY += SecGap;
                    sF.Location = new Point(GapSize + centerOfs, curY - sy);
                    curY += sF.Height;
                }
                yL = curY;
                yR = curY;
            }
            else
            {
                int nvL = 0, nvR = 0;
                foreach (Sec s in secs)
                {
                    if (!s.Visible) continue;
                    if (s.FullSpan && leftW > 0)
                    {
                        int startY = Math.Max(yL, yR);
                        if (nvL > 0 || nvR > 0) startY += SecGap;
                        s.Location = new Point(GapSize + centerOfs, startY - sy);
                        yL = startY + s.Height;
                        yR = yL;
                        nvL++; nvR++;
                        continue;
                    }
                    bool isLeft = leftW > 0 && leftSecs.Contains(s);
                    if (isLeft)
                    {
                        if (nvL > 0) yL += SecGap;
                        s.Location = new Point(GapSize + centerOfs, yL - sy); yL += s.Height; nvL++;
                    }
                    else
                    {
                        if (nvR > 0) yR += SecGap;
                        s.Location = new Point(rightX, yR - sy); yR += s.Height; nvR++;
                    }
                }
            }
            //  ★ 定位完就把**实际内容底**记下来，ContentHeight() 直接吃它（见方法上的注释）：
            //    滚动范围与布局结果同源，永不再出现"两处各算一遍、差一个间距"的问题。
            laidBottom = Math.Max(yL, yR) + Theme.S(24);
            SyncScrollRange();            // 内容高定下来 → 滚动条定价（可能翻转 Visible，Reflow 会再跑一轮）
            // 完整重排才整棵强刷；滚动搬运仅标准 Invalidate，交由消息泵自然合并
            if (relayout)
                Theme.HardRepaint(this);
            else
                Invalidate();
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
            //  整屏只有一个底色（2026-10-22）：顶栏/侧栏/页头/内容区全部同色，
            //  层次交给"白卡片 + 阴影 + 发丝边"，不再靠底色深浅分层。
            //  ⇒ 顶部渐变、左缘渐变同时删除：两层变一层之后，渐变没有两端，只剩一道悬空的色带。
            g.FillRectangle(Theme.Solid(Theme.Bg), r);
        }
    }

    // ======================= 主窗口 =======================
    // =========================================================================
    // 现代 Windows 文件夹选择器（基于 Vista/Win10/Win11 原生 IFileOpenDialog）
    // 彻底摒弃老旧 SHBrowseForFolderW，在独立 STA 线程运行 + 消息泵保护，100% 杜绝未响应假死
    // =========================================================================
    public static class ModernFolderPicker
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
        private static extern int SHCreateItemFromParsingName(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid riid,
            out IShellItem ppv);

        public static string PickFolder(IntPtr owner, string title, string initialDirectory)
        {
            string selectedPath = null;
            var th = new System.Threading.Thread(() =>
            {
                IFileOpenDialog dialog = null;
                try
                {
                    dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                    uint options;
                    dialog.GetOptions(out options);
                    // FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40
                    dialog.SetOptions(options | 0x20 | 0x40);
                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                    if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    {
                        try
                        {
                            IShellItem folderItem;
                            Guid iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                            if (SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, iid, out folderItem) == 0 && folderItem != null)
                            {
                                dialog.SetFolder(folderItem);
                            }
                        }
                        catch { }
                    }

                    int hr = dialog.Show(owner);
                    if (hr == 0)
                    {
                        IShellItem result;
                        dialog.GetResult(out result);
                        if (result != null)
                        {
                            string path;
                            result.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out path);
                            selectedPath = path;
                        }
                    }
                }
                catch
                {
                    selectedPath = null;
                }
                finally
                {
                    if (dialog != null)
                    {
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(dialog); } catch { }
                    }
                }
            });

            th.SetApartmentState(System.Threading.ApartmentState.STA);
            th.IsBackground = true;
            th.Start();

            // 主线程消息泵平滑循环，杜绝任何未响应判定
            while (th.IsAlive)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }

            return selectedPath;
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.ClassInterface(System.Runtime.InteropServices.ClassInterfaceType.None)]
        [System.Runtime.InteropServices.TypeLibType(System.Runtime.InteropServices.TypeLibTypeFlags.FCanCreate)]
        [System.Runtime.InteropServices.Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [System.Runtime.InteropServices.PreserveSig] int Show([System.Runtime.InteropServices.In] IntPtr parent);
            void SetFileTypes([System.Runtime.InteropServices.In] uint cFileTypes, [System.Runtime.InteropServices.In] IntPtr rgFilterSpec);
            void SetFileTypeIndex([System.Runtime.InteropServices.In] uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] IntPtr pfde, out uint pdwCookie);
            void Unadvise([System.Runtime.InteropServices.In] uint dwCookie);
            void SetOptions([System.Runtime.InteropServices.In] uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] IShellItem psi);
            void SetFolder([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] IShellItem psi);
            void GetFolder([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItem ppsi);
            void GetCurrentSelection([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItem ppsi);
            void SetFileName([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
            void GetFileName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItem ppsi);
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler([System.Runtime.InteropServices.In] IntPtr pbc, [System.Runtime.InteropServices.In] ref Guid bhid, [System.Runtime.InteropServices.In] ref Guid riid, out IntPtr ppv);
            void GetParent([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItem ppsi);
            void GetDisplayName([System.Runtime.InteropServices.In] SIGDN sigdnName, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes([System.Runtime.InteropServices.In] uint sfgaoMask, out uint psfgaoAttribs);
            void Compare([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] IShellItem psi, [System.Runtime.InteropServices.In] uint hint, out int piOrder);
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }
    }

    // =========================================================================
    // 独立轻量数据目录选择窗口（秒开、支持输入/粘贴、支持现代浏览、实时进度可视）
    // =========================================================================
    class ChangeDataDirDialog : Form
    {
        TextBox txtPath;
        Label lblMsg;
        FlatBtn btnMigrate;
        FlatBtn btnBrowse;
        FlatBtn btnCancel;
        MainForm parent;

        public ChangeDataDirDialog(MainForm parent)
        {
            this.parent = parent;
            Text = "迁移数据目录";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.S(540), Theme.S(230));
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;

            var lblCur = new Label();
            lblCur.Text = "当前数据目录：" + Program.DataDir;
            lblCur.Location = new Point(Theme.S(20), Theme.S(16));
            lblCur.Size = new Size(Theme.S(490), Theme.S(20));
            lblCur.ForeColor = Theme.Text;
            Controls.Add(lblCur);

            var lblDesc = new Label();
            lblDesc.Text = "新目录（可直接粘贴/手动输入路径，或点击「浏览」选择）：";
            lblDesc.Location = new Point(Theme.S(20), Theme.S(44));
            lblDesc.Size = new Size(Theme.S(490), Theme.S(20));
            lblDesc.ForeColor = Theme.TextDim;
            Controls.Add(lblDesc);

            txtPath = new TextBox();
            txtPath.Location = new Point(Theme.S(20), Theme.S(70));
            txtPath.Size = new Size(Theme.S(380), Theme.S(26));
            txtPath.Font = new Font(Font.FontFamily, 10f);
            txtPath.BackColor = Theme.Dark ? Color.FromArgb(36, 36, 38) : Color.White;
            txtPath.ForeColor = Theme.Text;
            Controls.Add(txtPath);

            btnBrowse = new FlatBtn();
            btnBrowse.Text = "📁 浏览…";
            btnBrowse.Location = new Point(Theme.S(410), Theme.S(68));
            btnBrowse.Size = new Size(Theme.S(100), Theme.S(28));
            btnBrowse.Click += delegate
            {
                string picked = ModernFolderPicker.PickFolder(this.Handle, "选择新的数据目录", Program.AppDir);
                if (!string.IsNullOrEmpty(picked)) txtPath.Text = picked;
            };
            Controls.Add(btnBrowse);

            lblMsg = new Label();
            lblMsg.Text = "提示：配置、封面、备份与插帧包将整体搬移，源目录清空。";
            lblMsg.Location = new Point(Theme.S(20), Theme.S(110));
            lblMsg.Size = new Size(Theme.S(490), Theme.S(22));
            lblMsg.ForeColor = Theme.TextDim;
            Controls.Add(lblMsg);

            btnMigrate = new FlatBtn();
            btnMigrate.Text = "开始迁移";
            btnMigrate.Kind = BtnKind.Primary;
            btnMigrate.Location = new Point(Theme.S(300), Theme.S(145));
            btnMigrate.Size = new Size(Theme.S(100), Theme.S(32));
            btnMigrate.Click += delegate { DoStartMigrate(); };
            Controls.Add(btnMigrate);

            btnCancel = new FlatBtn();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(Theme.S(410), Theme.S(145));
            btnCancel.Size = new Size(Theme.S(100), Theme.S(32));
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);
        }

        void DoStartMigrate()
        {
            string tgt = txtPath.Text.Trim();
            if (string.IsNullOrEmpty(tgt))
            {
                MessageBox.Show(this, "请输入或浏览选择目标目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            tgt = tgt.TrimEnd('\\');
            if (string.Equals(Program.DataDir.TrimEnd('\\'), tgt, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "所选目录与当前数据目录相同，无需迁移。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnMigrate.Enabled = false;
            btnBrowse.Enabled = false;
            btnCancel.Enabled = false;
            btnMigrate.Text = "迁移中…";
            lblMsg.ForeColor = Theme.Accent;
            lblMsg.Text = ">>> 正在准备迁移，请稍候…";

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string err = Program.MigrateDataTo(tgt, delegate(string msg, int pct)
                {
                    try
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            lblMsg.Text = msg;
                            System.Windows.Forms.Application.DoEvents();
                        });
                    }
                    catch { }
                });

                this.BeginInvoke((MethodInvoker)delegate
                {
                    if (!string.IsNullOrEmpty(err))
                    {
                        btnMigrate.Enabled = true;
                        btnBrowse.Enabled = true;
                        btnCancel.Enabled = true;
                        btnMigrate.Text = "开始迁移";
                        lblMsg.ForeColor = Color.Red;
                        lblMsg.Text = "⚠ 迁移失败：" + err;
                        MessageBox.Show(this, "迁移失败：\\n" + err, "数据目录", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    lblMsg.ForeColor = Color.FromArgb(46, 175, 80);
                    lblMsg.Text = "✓ 数据目录已成功搬移！";
                    if (MessageBox.Show(this, "数据已成功迁移到：\\n  " + tgt + "\\n\\n现在立即重启应用生效吗？", "数据目录", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        try { System.Diagnostics.Process.Start(Application.ExecutablePath); } catch { }
                        Application.Exit();
                    }
                    else
                    {
                        Close();
                    }
                });
            });
        }
    }

    public class MainForm : Form
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_DROPFILES = 0x0233;
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        static readonly int WM_ACTIVATE_INSTANCE = Native.RegisterWindowMessage("FLUXION_SHOW_EXISTING_INSTANCE");
        const int HTCLIENT = 1, HTCAPTION = 2, HTSYSMENU = 3;
        //  缩放热区（无边框后自己给，见 WndProc 的 WM_NCHITTEST）
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                  HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        //  WM_NCCALCSIZE 的参数块：三个矩形（新窗口 / 旧窗口 / 旧客户区）+ 窗口位置结构
        [StructLayout(LayoutKind.Sequential)]
        struct NcRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct NcCalcParams
        {
            public NcRect New;          // 输出：新的**客户区**矩形（rgrc[0]）
            public NcRect OldWindow;    // 旧窗口矩形（rgrc[1]）
            public NcRect OldClient;    // 旧客户区矩形（rgrc[2]）
            public IntPtr Pos;
        }
        //  ★ 自绘标题栏的高度（2026-10-23）：对齐 FileButler 的 40px。
        //    这条栏**取代**系统标题栏（见 WndProc 的 WM_NCCALCSIZE），不是额外加一条 ——
        //    所以窗口总高只比原来多 10px 左右，视觉上却从"两个程序拼的"变成一整块。
        const int TitleH = 64;   // 与「自考工作台」页头同高（2026-10-29 实测 study header=64）：页名 + 右侧按钮组两层呼吸
        //  页名：导航分段控件与标题栏面包屑共用一份（改一处即可）
        static readonly string[] PageNames = new string[] { "仪表盘", "性能优化", "游戏库", "实时监控", "说明", "设置" };
        //  Steam 式骨架（2026-10-18）：顶栏放页面导航，左栏只放游戏库。
        //  以前是「左侧竖向导航 176px」，玩家要同时看导航与游戏列表时只能二选一；
        //  现在导航上移，左栏常驻游戏列表，选中游戏 → 右侧就是这台游戏的一切。
        const int GameSideW = 256;   // 「自考工作台」侧栏宽（实测 w-64 = 256px）
        const int TopH = 52;
        const int FootH = 30;

        Config Cfg;
        Control rootPanel;      // 外层骨架：换主题时要整棵摘掉重建
        Panel content;
        //  自绘标题栏（2026-10-23）：取代系统标题栏，见 BuildTitleBar / WndProc
        Panel titleBar, titleBtnBox;
        WinBtn btnMin, btnMax, btnClose;
        FlatBtn btnScanTop;
        FlatBtn btnAddTop;                             // 页头「添加游戏」——手动添加的可见入口（2026-11-01）
        Control btnSideMore, btnRailExpand;
        string crumbText = "";      // 初值必须空：Switch 幂等返回时不会调 SetCrumb，写死页名会漏到首帧
        int crumbIcon;
        Label lblCrumb;
        Action crumbPlace;
        bool scanOn;
        Timer scanBeat;         // 扫描指示器的节拍（只在扫描期间跑）
        // 页面索引常量：加页时不用再改散落各处的裸数字（v2.5.0 从 5 页扩到 7 页）
        //  「游戏库」页已并入「帧生成」（左游戏栏 + 右侧库墙/详情），索引不再保留空槽。
        const int P_DASH = 0, P_OPT = 1, P_FG = 2, P_MON = 3, P_HELP = 4, P_SET = 5;
        Pg[] pages = new Pg[6];
        NavMenu navMenu;               // 侧栏竖向菜单（FileButler 的 n-menu 造型）
        SideGameList sideList;         // 侧栏「游戏」快捷入口（第 145 轮填掉导航下那片空白）
        int sideGameCount;             // 标题右侧的总数（= 可点进详情的游戏数）
        TableLayoutPanel sideCol;      // 侧栏容器（折叠"搜索+列表"两行时要用）
        // ---- 侧栏抽屉（2026-10-22）----
        //  游戏列表可以折成 44px 的窄条，给封面墙腾视野；状态记在 config 的 ui.sideCollapsed。
        TableLayoutPanel bodyCol;      // 两列骨架：第 0 列就是侧栏宽度
        Control gameSidePane;          // 侧栏本体
        Control sideRailPane;          // 收起后的窄条（同一格里用 Visible 切换）
        bool sideCollapsed;
        const int SideRailW = 44;
        int SideW { get { return Theme.S(sideCollapsed ? SideRailW : GameSideW); } }
        int page;

        // 仪表盘
        Label lblSysInfo, lblGameState, lblLink, lblLinkAct, lblHealth, lblHealthSub, lblFgRuntime, lblFgLast;
        int lastHealthUnapplied = -1;
        void RefreshHealthTitle(int rw)
        {
            if (lblHealth == null || lastHealthUnapplied < 0) return;
            int n = lastHealthUnapplied;
            string txt;
            if (n == 0)
            {
                txt = rw < Theme.S(180) ? "🟢 优化良好" : "🟢 极致低延迟 · 全部已生效";
            }
            else
            {
                if (rw < Theme.S(170)) txt = "⚠ 建议优化 (" + n + "项)";
                else if (rw < Theme.S(220)) txt = "⚠ 建议优化 (" + n + " 项未生效)";
                else txt = "⚠ 建议一键优化 · " + n + " 项未生效";
            }
            SetBody(lblHealth, txt);
        }
        //  联动卡第三行的占位（第 147 轮）：原来空态时 Body("") 折叠成 2 行，游戏联动信息一来变 3 行，
        //  卡片高度跟着跳；而且 2 行内容压在 3 行高度的卡里，下面空一块，看着像"没写完"。
        //  恒定 3 行（占位常驻）同时解决高度抖动和下沿空。
        const string LinkIdleText = "联动就绪（启动游戏后自动生效）";
        //  面板（对齐 NVIDIA App）：一行状态 + 一行提示 + 两个整宽动作。
        //  这三样以前散在「方案推荐卡 / 游戏信息卡 / 运行时状态卡」三处，同一句话读三遍。
        Label lblFgState, lblFgInfo;
        FlatBtn btnInstall, btnUninstall;
        Bar barCpu, barMem, barGpu, barTemp;
        RadialGauge gaugeHealth;
        // 监控
        Spark spCpu, spGpu, spMem, spGpuTemp, spCpuTemp;
        LogView log;
        bool logVirgin = true;   // 日志占位：首条真日志到达前显示灰字提示
        Label lblLogEmpty;       // 日志区空态：居中一句提示，盖在空白 RichTextBox 上（首条日志到达后隐藏）
        int logLines;            // 日志控件里已有的行数（计数器：不再每行都 Split 整段文本）
        long lastLogLen = 0;
        Panel logBox;            // 日志容器（高度自适应，见 ResizeLogBox）
        Sec logSec;              // 日志所在分区（高度变了要 Resync，否则 Pg 滚动范围停在旧值）
        // 优化
        ListView lvOpt;
        Label lblDrift;
        // 帧生成
        Label lblRt, lblD3d12, lbl030, lblDlssLib;
        Label lblJunk;   // 设置页：备份/隔离区占用一行（P0-1）
        RoundCombo cbJunkAge;   // 「回收范围」档位；SetJunkLabel 往各项回填条数/体积
        static readonly int[] JunkAges = new int[] { 30, 14, 7, 0 };   // 0 = 不限时长
        Label lblPatrol; // 帧生成页：入口巡检一行（同目录里有几套代理并存）（P0-3）
        //  概览「游戏信息」卡（2026-10-22）：概览页只有一张适配方案卡，窗口一高，
        //  卡片底下就是一整屏灰底 —— 用户"还能往下翻动，底下却什么都没有"。
        //  把游戏自身的事实（平台/入口/目录/时长）填在这里，页面不再空着翻。
        Label lblGi;
        MetaLine lblDashSub;           // 仪表盘「快捷操作」工具条右端的元信息行（第 154 轮从 Body 行挪进来）
        string dashOpt = "";           // 摘要里的优化状态（体检表读完后才有值）
        FlatBtn btnWebCover, btnSetCover, btnPasteCover, btnClearCover;   // 概览「游戏信息」卡里的自定义封面入口
        Label lblGiArgs;               // 概览「游戏信息」卡：当前启动参数/环境变量状态行（2026-10-31）
        FlatBtn btnLaunchCfg;          // 同卡「启动参数与环境变量…」可见入口 —— 原来只在右键菜单里，用户找不到
        // 资源包版本结论上次报的是什么 —— 只在变化时写一行日志，避免每次刷新刷满日志
        string packVerdictSeen = "";
        RoundCombo cbEntry;                                  // 3A 通用模式的入口名（6 选 1）
        // 0.4.x（2026-10-18 起的新包结构）与 GitHub 下载源
        Label lbl040;
        RoundCombo cbEntry040;                               // 0.4.x 部署用的入口名（6 选 1）
        FlatBtn btnUpRelease, btnAsi;
        // ---- 更新检查（第 160 轮）----
        FlatBtn btnUpdCheck, btnUpdOpen;
        Label lblUpd;
        RoundCheck chkUpdAuto;
        bool updBusy;
        string updUrl = "";                // 查到新版时的 release 页（「打开发布页」用）
        const string SelfRepo = "7SteveJohn/Fluxion";
        bool upstreamBusy = false;                           // 「从 GitHub 取新版」进行中的重入保护
        // 方案 B · MFG Bridge（gt2333588/nvidia_mfg_bridge，单文件 nvngx.dll）
        Label lblNv;
        RoundCombo cbEntryNv;
        FlatBtn btnNvFetch;
        // Smooth Motion（驱动级插帧）：不往游戏目录放文件，只写 NVIDIA 驱动配置的 per-game 档
        Label lblSm;
        // 方案选择器：5 套方案只显示一套（原来全铺，页面 3000+ px，要滚好几屏）
        RoundCombo cbScheme;
        GameRowList lstFg;             // 旧的游戏列表：已停用（保留字段只为兼容残留引用，恒为 null）
        RoundField lstSearch;          // 左栏搜索：同时过滤列表与右侧封面墙
        //  帧生成页的「库视图 ↔ 详情」两组分区：未选中游戏时显示库墙，选中后显示详情。
        //  用 Visible 切换（Pg.LayoutOnce 会跳过隐藏分区），不做两套页面。
        readonly List<Sec> libSecs = new List<Sec>();
        readonly List<Sec> detailSecs = new List<Sec>();
        TabBar tabsFg;                 // 帧生成页标签栏
        HeadBox headFg;                // 帧生成页的 Steam 式大横幅
        //  详情分成三页签（概览 / 参数 / 诊断）—— 以前三页签只是"滚到某个位置"，
        //  整页照样一次铺满（用户原话「内容还是不够精简」）。现在是真的切显示：
        //  「概览」只剩面板（一行状态 + 一行提示 + 两个动作），其余两页按需点开。
        int curDetailTab;                                    // 0 概览 · 1 参数 · 2 诊断
        bool libViewOn = true;                               // true = 封面墙；false = 该游戏的详情页
        readonly Dictionary<Sec, int> secTab = new Dictionary<Sec, int>();
        readonly Dictionary<Sec, bool> secWant = new Dictionary<Sec, bool>();   // 方案区块：这一套当前是否该显示
        Sec s030Sec, s040Sec, sNvSec, sSmSec;
        string curRecAction = "";      // 当前建议的动作（opti/030/asi/040/nvbr/none），供"自动"用
        // 0.4.x 的四个常用参数（写进游戏目录那份 dlssg_sm86.ini）
        RoundCombo cbOpt040, cbPreset040, cbEngine040, cbSr040;
        // 面板之外的解释性文字（为什么这么推荐 / 依据）搬进「诊断」页签，不再占概览
        Label lblWhy, lblRecNote;
        // 显卡名伪装（内置，以前要出去双击 .reg）
        Label lblSpoof;
        FlatBtn btnSpoofOn, btnSpoofOff, btnSpoofDev;
        // 游戏库（v2.5.0：平台扫描 + 封面卡片 + 库标记）
        CoverGrid libGrid;               // ★ 第 124 轮：库视图 = 封面网格
        LibActionBar actBar;             // ★ 第 125 轮：选中游戏后的底部状态栏（不跳页）
        FilterChips chips;               // ★ 第 126 轮：工具条上的分类筛选（数量为 0 的自动隐藏）
        LibHero libHero;                 // ★ 第 132 轮：库页主视觉（当前游戏的宽幅大图）
        Sec heroSec;                     //   它所在的分区
        bool heroHasData;                 //   主视觉有没有内容（ShowLibView 要用它决定显隐）
        //  ★ 键盘走位用：墙面此刻真实显示的顺序（排序/筛选之后），不是 games 的原始顺序。
        List<DlssgGame> libShown = new List<DlssgGame>();
        Label lblLibStat;
        bool webCoverRunning;
        bool redownloadRunning;          // 「重新获取官方封面」进行中（下载链已放后台，防重入）
        RoundCombo cbSort, cbTheme;
        RoundCombo cbMotion;                                 // 设置页「界面动效」三态
        CheckBox chkShowHidden;
        bool onlyHidden;                  // 左栏选中「已隐藏」时为真：只显示隐藏条目
        bool coverRunning;
        static readonly object[] PlatNames = new object[] { "全部", "收藏", "私密", "二次元", "Steam", "Epic", "WeGame", "本地" };
        static readonly string[] PlatIds = new string[] { "all", "fav", "private", "genre", "steam", "epic", "wegame", "local" };
        RoundCombo cbRouter, cbKernel, cbBilinear, cbMaxFrames, cbLogLevel, cbNr, cbNrPos;
        CheckBox cbFgAutoEntry, cbFgD3d12;
        FlatBtn btnDownload, btnFgApply, btnOptimize;
        FlatBtn btnRestore;      // 「恢复备份」：执行期间要置灰+改文案，所以必须是字段（原先是局部变量）
        FlatBtn btnIgnored;
        DlssgGame selected;
        List<DlssgGame> games = new List<DlssgGame>();
        string fgDirSeen = "";      // 「落地目录已纠正」这条日志只记一次
        // 设置
        RoundCombo cbScene, cbProfile, cbProfileGame;
        RoundCombo cbClose;      // 设置页「关闭窗口时」三态（v3.2.1 从「自定义优化项」搬来）
        CheckBox cbManual, cbAware, cbAutoStart;
        // 游戏分辨率档位（v3.8.0）：两个游戏各一个下拉。原来是写死在代码里的，
        //   换显示器后没有第二个选项 —— 现在可选、可按本机主屏标可用性。
        RoundCombo cbResCs, cbResVal;
        // 分辨率修改方式（2026-10-06）：自动修改 = 进游戏自动切/退出还原（原行为）；手动修改 = 程序不碰显示。
        // 落点是 displayLink.enable 这个一直在的总闸 —— 此前只能改 config.json，界面没有入口。
        RoundCombo cbResMode;
        // 进游戏时要在**设备管理器**层面停用的显示器（v3.9.0）。值 = 显示器型号（硬件 ID 第二段）。
        RoundCombo cbOffCs, cbOffVal;
        FlatBtn btnResBase;          // 「还原目标 = 当前主屏」
        Label lblResInfo;            // 档位区的说明 + 实时状态
        bool resComboSyncing = false;   // 回显下拉时抑制事件（否则回显本身会把配置又写一遍）
        string resDispSig;              // 上次重算分辨率档位时的显示签名（显示器/分辨率/刷新率）
        // 显示器设备（v3.9.0）：性能优化页的列表 + 兜底按钮
        ListView lvMon;
        FlatBtn btnMonOn, btnMonOff, btnMonAll;
        // 通用
        Label lblFoot, lblClock;
        Panel dotFoot;                          // 状态灯：跟随最后一条日志的级别（绿/黄）
        Color footDot = Theme.Ok;
        long footAt;                            // 最后一条状态栏消息的时刻（5 秒后回「就绪」）
        int sysIdleSkip;                        // 后台降频计数（见 UpdateSys）
        Timer fadeTimer;                        // 首屏淡入（字段持有：局部 Timer 可能被回收）
        Timer sysTimer, linkTimer, logTimer, clockTimer;
        // ---- 后台休眠（第 161 轮，用户"一直挂在后台"）----
        //  托盘隐藏 / 最小化时：停掉纯 UI 的定时器（页脚时钟 1s、日志跟随 5s），
        //  3 秒后把封面缓存 + 托管堆归还系统。联动（linkTimer）与守护（sysTimer 的
        //  GuardTickAsync）是后台的核心职责，照常跑；采样（UpdateSys）自带降频也照旧。
        bool bgMode;
        Timer bgTrimT;
        void SetBgMode(bool on)
        {
            if (closing || Disposing || IsDisposed) { bgMode = false; return; }
            if (bgMode == on) return;
            bgMode = on;
            if (on)
            {
                if (clockTimer != null) clockTimer.Stop();
                if (logTimer != null) logTimer.Stop();
                ScheduleBgTrim();
            }
            else
            {
                if (clockTimer != null) clockTimer.Start();
                if (logTimer != null) logTimer.Start();
            }
        }
        //  延迟 3 秒再瘦身：刚收进托盘的几秒里动画/重排还没停干净，
        //  立刻 GC 会白做一遍又长回去。3 秒内重新打开就取消（DoBgTrim 里再查一次 bgMode）。
        void ScheduleBgTrim()
        {
            if (bgTrimT == null)
            {
                bgTrimT = new Timer();
                bgTrimT.Interval = 3000;
                bgTrimT.Tick += delegate
                {
                    bgTrimT.Stop();
                    try { DoBgTrim(); } catch { }
                };
            }
            bgTrimT.Stop();
            bgTrimT.Start();
        }
        void DoBgTrim()
        {
            if (!bgMode || closing || Disposing || IsDisposed) return;
            //  封面缓存是进程里最大的一块浮动项（LRU 有上限但常驻几十 MB）；
            //  控件手里的都是 Own() 副本（所有权在控件），清缓存不动它们，
            //  恢复显示时按需从磁盘重解码即可。
            CoverArt.ClearCache();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            //  把物理页还给系统：后台挂着的进程，任务管理器里的内存数字是用户唯一的感知通道
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
            Program.Log("[后台] 已进入托盘休眠：UI 定时器停、封面缓存清空、内存已归还");
        }
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProc, IntPtr min, IntPtr max);
        NotifyIcon tray;
        ToolStripMenuItem miStatus;
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
        DlssgGame lastGameObj;           // 对应的库条目（退出后做帧生成自检要用它的目录）
        System.Windows.Forms.Timer fgCheckTimer;   // 退出后延迟自检的定时器（见 DelayFgSelfCheck）
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
            //  窗体自身的底色也要跟上（2026-10-22）：Dock 控件之间的 1px 接缝露出的是**窗体背景**，
            //  它不是 Bg 时，侧栏与内容区之间就会有一条细线（用户截图里"页头右侧那条竖线"）。
            BackColor = Theme.Bg;
            //  ★ 整条系统标题栏去掉（2026-10-30 第 171 轮）——照「自考学习工作台」的 frame:false：
            //    它的 electron/main.cjs 注释写得很清楚"win32 下去掉整条系统标题栏，应用头部自己当
            //    标题栏，右上角三个按钮由页面绘制"，代价只有一条（失去 Win11 悬停最大化按钮的
            //    贴边分屏菜单），他们已确认接受。
            //    旧的"保留系统边框、只从非客户区减掉标题栏那段"在 Win11 26H2(build 26300) 上
            //    推算失效 ⇒ 系统标题栏没被减掉 ⇒ 顶上系统栏 + 自绘页头两层（两套窗口按钮）。
            //    改成无边框后彻底不依赖 Windows 版本：拖放走 WM_NCHITTEST 的 HTCAPTION，
            //    缩放热区自己给（见 WndProc），圆角仍走 DWMWA_WINDOW_CORNER_PREFERENCE。
            FormBorderStyle = FormBorderStyle.None;
            //  后台搜到横版主视觉后回调：清缓存、重刷当前详情页（Hero 取图会命中新文件）
            CoverArt.HeroReady = delegate
            {
                try
                {
                    BeginInvoke((Action)(delegate
                    {
                        try { CoverArt.ClearCache(); RefreshFgSummary(); } catch { }
                    }));
                }
                catch { }
            };
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
            //  后台休眠（第 161 轮）：托盘隐藏 / 启动即托盘 → 停 UI 定时器 + 归还内存
            try { SetBgMode(!value); } catch { }
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
            if (WM_ACTIVATE_INSTANCE != 0)
                try { Native.ChangeWindowMessageFilter((uint)WM_ACTIVATE_INSTANCE, Native.MSGFLT_ADD); } catch { }

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
            clockTimer.Tick += delegate
            {
                try { if (lblClock != null) lblClock.Text = DateTime.Now.ToString("HH:mm:ss"); } catch { }
                // 状态栏消息 5 秒后回「就绪」：常驻的一句内部提示，是"廉价感"的主要来源之一。
                try
                {
                    if (lblFoot != null && !lblFoot.IsDisposed
                        && Environment.TickCount - footAt > 5000 && lblFoot.Text != "就绪")
                    {
                        lblFoot.Text = "就绪";
                        lblFoot.ForeColor = Theme.TextDim;
                        footDot = Theme.Ok;
                        if (dotFoot != null && !dotFoot.IsDisposed) dotFoot.Invalidate();
                    }
                }
                catch { }
            };
            clockTimer.Start();

            //  启动时把焦点从"第一个可选控件"（侧栏搜索框）上拿走：否则一进界面搜索框就带着
            //  强调色焦点环，看起来像被选中了。必须挂 Shown —— Load 早于 WinForms 设初始焦点。
            Shown += delegate
            {
                try { ActiveControl = null; } catch { }
                //  ★ 标题栏配色必须**显示之后**再刷一次（2026-10-20）：在创建句柄时设的
                //    DWMWA_CAPTION_COLOR 会被 DWM 首次合成覆盖回去 —— 实测窗口顶部仍是
                //    Win10 那套灰蓝标题栏，和下面纯白的品牌条之间横着一条色带，就是"两块拼的"。
                //    放在 Shown 里重设一次才真正生效（同款坑：DWM 属性要在窗口可见后设）。
                try { Theme.ApplyChrome(Handle); } catch { }
                //  静默更新检查（第 160 轮）：窗口显示后后台查一次（24h 节流在 BeginUpdateCheck 里）；
                //  有新版只写日志 + 设置页一行，不弹窗不打扰。
                try { BeginUpdateCheck(false); } catch { }
                try { DoAutoFetchCovers(); } catch { }
            };
            Load += delegate
            {
                //  ★ 无边框后最大化会盖住任务栏：把"最大化后的矩形"限制在所在屏的工作区
                //    （系统边框原来自带这个约束，FormBorderStyle.None 之后必须自己给）。
                try
                {
                    var sc = Screen.FromHandle(Handle);
                    MaximizedBounds = sc.WorkingArea;
                }
                catch { }
                for (int i = 0; i < pages.Length; i++) pages[i].Reflow();
                ShowShareDisclaimer();
                FadeInWindow();
                //  帧生成页左栏的游戏列表：首屏先填一次（扫描完成后 FillGameList 还会再调一次）
                try { RefreshFgSummary(); } catch { }
            };

            // 回到窗口时按需自动重扫（新装的游戏自动出现，见 MaybeAutoScan）
            Activated += delegate { MaybeAutoScan(); };

            Switch(Program.ShareBuild ? P_FG : P_DASH);       // 分享版首屏给「帧生成」（库已并入这里）
            UpdateSys();
            if (!Program.ShareBuild) UpdateLink();
            RefreshGridAsync();
            SyncProfileEnabled();
            // 启动即加载自定义游戏（games.json），不用等扫描；忽略清单同时生效
            games = Dlssg.MergeScan(new List<DlssgGame>());
            FillGameList();
            RefreshFgSummary();
            RunScan(false);     // 起步即后台自动扫描一遍（静默、不阻塞界面）
            //  建页时那次扫盘如果早于句柄创建，结果暂存在 junkPending —— 这里句柄一定已建，补填一次
            if (junkPending != null) { ApplyJunkLabel(junkPending); junkPending = null; }
            Log("界面已就绪（缩放 " + Theme.ScaleText() + "，窗口 " + Width + "x" + Height + "）· 字体：" + Theme.FaceInfo());
            Log("Fluxion 已启动（管理员: " + IsAdmin() + "）");
        }

        // 外层骨架（纵贯全高侧栏 / 内容工作台区 / 底栏）。抽出来是为了"换主题时原地重建"能复用同一段代码。
        void BuildChrome()
        {
            // 全高纵贯式一体化现代侧栏（Full-Height Sidebar）
            var root = new CaptionTable();
            root.Dock = DockStyle.Fill;
            Buffered(root);
            root.ColumnCount = 2; root.RowCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(GameSideW)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.BackColor = Theme.Bg;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;

            bodyCol = root;
            gameSidePane = BuildGameSide();
            sideRailPane = BuildSideRail();
            sideRailPane.Visible = false;
            root.Controls.Add(gameSidePane, 0, 0);
            root.Controls.Add(sideRailPane, 0, 0);

            // 右侧内容工作台（顶栏/面包屑/窗口键 + 内容画布 + 极简底栏）
            var rightPane = new CaptionTable();
            rightPane.Dock = DockStyle.Fill;
            Buffered(rightPane);
            rightPane.ColumnCount = 1; rightPane.RowCount = 3;
            rightPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rightPane.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(TitleH)));
            rightPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rightPane.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(FootH)));
            rightPane.BackColor = Theme.Bg;
            rightPane.Margin = Padding.Empty;
            rightPane.Padding = Padding.Empty;

            rightPane.Controls.Add(BuildTitleBar(), 0, 0);
            rightPane.Controls.Add(BuildContent(), 0, 1);
            rightPane.Controls.Add(BuildFooter(), 0, 2);

            root.Controls.Add(rightPane, 1, 0);
            Controls.Add(root);
            rootPanel = root;
            //  恢复上次的抽屉状态（记住的是"我习惯收起"，不是每次都要重新收一遍）
            if (Cfg != null && Cfg.SideCollapsed) SetSideCollapsed(true);
        }

    class CaptionTable : TableLayoutPanel
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }
    }

    class CaptionPanel : Panel
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }
    }

    class CaptionLabel : TxtLbl
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }
    }

        // ---------------------- 自绘标题栏（取代系统标题栏） ----------------------
        //  布局照 FileButler：左=后台活动指示 / 中=面包屑（当前页）/ 右=窗口按钮。
        //  ❗ 刻意不放 logo：下面顶栏左列已经有品牌（图标 + 字标），一条栏上放两遍是最典型的廉价感。
        Control BuildTitleBar()
        {
            var bar = new CaptionPanel();
            bar.Dock = DockStyle.Fill;
            bar.Height = Theme.S(TitleH);
            bar.BackColor = Theme.Bg;
            bar.Margin = Padding.Empty;
            bar.Padding = Padding.Empty;
            Buffered(bar);
            titleBar = bar;

            MouseEventHandler dragDown = delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                }
            };
            EventHandler dblMax = delegate(object sender, EventArgs e)
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            };
            bar.MouseDown += dragDown;
            bar.DoubleClick += dblMax;

            // 中：面包屑 = 小图标 + 当前页名（FileButler 的 crumb：13px / 500 / opacity .88）
            bar.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                //  图标：跟着面包屑文字左缘走（文字是子 Label，宽度由它自己量）
                //  文字为空时连图标一起不画 —— 留一个孤立图标比留空更像 bug。
                if (lblCrumb != null && crumbText.Length > 0)
                {
                    //  图标固定在左缘 S(20)（页头左基准），文字由 place() 跟着它走
                    int iz = Theme.S(16);
                    DrawPageIcon(g, new Rectangle(Theme.S(20), (bar.Height - iz) / 2, iz, iz),
                                 crumbIcon, Theme.TextDim);
                }
                //  后台活动指示：避开面包屑文字区域，防止在左侧重叠遮挡
                if (scanOn)
                {
                    int sx = (lblCrumb != null && crumbText.Length > 0) ? lblCrumb.Right + Theme.S(16) : Theme.S(20);
                    DrawScanPulse(g, sx, bar.Height / 2);
                }
                //  底部发丝线：整条栏与下面导航同一底色，没有这条线两者会连成一块 92px 的灰板
                //  页头与内容区之间一条 1px 实线（照「自考工作台」的 `border-bottom`）：
                using (var pen = new Pen(Theme.Line, 1f))
                    g.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };

            //  面包屑用真 Label：自绘文字要靠"谁记得 Invalidate"来刷新，切页/选中游戏时
            //  经常是别的地方在重绘、这条栏不在重绘范围里 —— 实测出现过"文字停在旧页名"。
            lblCrumb = new CaptionLabel();
            lblCrumb.AutoSize = false;
            lblCrumb.BackColor = Theme.Bg;
            lblCrumb.ForeColor = Theme.Text;        // 页名是主文字，不是注脚
            lblCrumb.Font = Theme.FM(15f);          // 与 study 页头标题同档（15px / 600）
            lblCrumb.TextAlign = ContentAlignment.MiddleLeft;
            lblCrumb.AutoEllipsis = true;
            lblCrumb.Text = crumbText;
            lblCrumb.Height = Math.Max(1, bar.Height - Theme.S(1));
            lblCrumb.MouseDown += dragDown;
            lblCrumb.DoubleClick += dblMax;
            bar.Controls.Add(lblCrumb);
            Action place = delegate
            {
                if (lblCrumb == null || bar.Width <= 0) return;
                int w = TextRenderer.MeasureText(lblCrumb.Text, lblCrumb.Font).Width + Theme.S(8);
                lblCrumb.Width = Math.Max(Theme.S(20), w);
                lblCrumb.Height = Math.Max(1, bar.Height - Theme.S(1));   // 别盖住页头底线，见上
                lblCrumb.Left = Theme.S(20) + Theme.S(16) + Theme.S(9);
                lblCrumb.Top = 0;
            };
            bar.Layout += delegate { place(); };
            lblCrumb.TextChanged += delegate { place(); bar.Invalidate(); };
            crumbPlace = place;

            //  扫描指示要动起来，得有个节拍器；只在扫描期间跑（平时一个定时器都不多开）
            scanBeat = new Timer();
            scanBeat.Interval = 90;
            scanBeat.Tick += delegate { if (titleBar != null && scanOn) titleBar.Invalidate(); };

            // 右：窗口按钮（细线矢量，与图标同一套笔画粗细）
            int bw = Theme.S(34), bh = Theme.S(32), gap = Theme.S(2), lead = Theme.S(10);
            btnMin = MakeWinBtn(WinKind.Min); btnMin.Width = bw;
            btnMax = MakeWinBtn(WinKind.Max); btnMax.Width = bw;
            btnClose = MakeWinBtn(WinKind.Close); btnClose.Width = bw;

            btnScanTop = new FlatBtn();
            btnScanTop.Text = "扫描游戏";
            btnScanTop.Width = Theme.S(92);
            btnScanTop.Height = Theme.S(32);
            btnScanTop.Click += delegate { RunScan(true); };

            //  「添加游戏」提升为页头可见按钮（2026-11-01）：手动添加功能一直藏在 btnMore 菜单里，
            //  用户从来没发现过（"手动导入游戏到底在哪儿"）。放页头与「扫描游戏」并列。
            btnAddTop = new FlatBtn();
            btnAddTop.Text = "添加游戏";
            btnAddTop.Width = Theme.S(92);
            btnAddTop.Height = Theme.S(32);
            btnAddTop.Click += delegate { DoAddGame(); };

            var box = new CaptionPanel();
            box.Dock = DockStyle.None;
            box.BackColor = Theme.Bg;
            box.Margin = Padding.Empty;
            box.Height = Theme.S(TitleH);
            int scanGap = Theme.S(14);                       // 业务按钮 ↔ 窗口按钮的距离
            box.Width = Theme.S(12) + btnScanTop.Width + Theme.S(8) + btnAddTop.Width + scanGap + lead + bw * 3 + gap * 2 + Theme.S(12);
            Buffered(box);
            box.MouseDown += dragDown;
            box.DoubleClick += dblMax;

            //  从左到右：扫描游戏 添加游戏 (间距) 最小化 最大化 关闭
            btnScanTop.Left = Theme.S(12);
            btnScanTop.Top = (box.Height - btnScanTop.Height) / 2;
            btnAddTop.Left = btnScanTop.Right + Theme.S(8);
            btnAddTop.Top = btnScanTop.Top;
            int sepX = btnAddTop.Right + scanGap;
            box.Paint += delegate(object s2, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                using (var pLine = new Pen(Theme.Line, 1f))
                    g.DrawLine(pLine, 0, box.Height - 1, box.Width, box.Height - 1);
            };
            int bx = sepX + lead;
            foreach (WinBtn b in new WinBtn[] { btnMin, btnMax, btnClose })
            {
                b.Dock = DockStyle.None;
                b.Left = bx; b.Top = (box.Height - bh) / 2; b.Width = bw; b.Height = bh;
                bx += bw + gap;
            }
            box.Controls.Add(btnClose);   // 先加的在最右：关闭在最右边
            box.Controls.Add(btnMax);
            box.Controls.Add(btnMin);
            box.Controls.Add(btnScanTop);
            box.Controls.Add(btnAddTop);   // 2026-11-01：创建+定位了却没挂进容器，按钮永远不显示（漏加 Controls.Add）
            bar.Controls.Add(box);
            titleBtnBox = box;
            Action placeBtnBox = delegate
            {
                if (box.Width <= 0 || bar.Height <= 0) return;
                box.Height = bar.Height;
                box.Left = bar.Width - box.Width;
                box.Top = 0;
                btnScanTop.Top = (box.Height - btnScanTop.Height) / 2;
                int bx2 = btnAddTop.Right + scanGap + lead;   // 2026-11-01：从「添加游戏」右边起摆 —— 旧的按扫描游戏算会叠在添加游戏上
                foreach (WinBtn b in new WinBtn[] { btnMin, btnMax, btnClose })
                {
                    if (b != null)
                    {
                        b.Left = bx2;
                        b.Top = (box.Height - b.Height) / 2;
                        bx2 += b.Width + gap;
                    }
                }
                box.Invalidate();
            };
            bar.Resize += delegate { placeBtnBox(); };
            placeBtnBox();
            //  最大化/还原时按钮图形要跟着换（否则"最大化"键在最大化状态下画的是错的）
            EventHandler syncMax = delegate {
                if (btnMax != null) { btnMax.Restore = WindowState == FormWindowState.Maximized; btnMax.Invalidate(); }
                if (titleBtnBox != null) titleBtnBox.Invalidate();
                if (titleBar != null) titleBar.Invalidate();
            };
            Resize += syncMax;
            return bar;
        }

        WinBtn MakeWinBtn(WinKind kind)
        {
            var b = new WinBtn();
            b.Kind = kind;
            b.Width = Theme.S(34);
            b.Height = Theme.S(32);
            b.Click += delegate
            {
                if (kind == WinKind.Min) WindowState = FormWindowState.Minimized;
                else if (kind == WinKind.Max)
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                else Close();                 // 走原有的"最小化到托盘 / 退出"询问
            };
            return b;
        }

        //  命中测试里要排除窗口按钮（那是 HTCLIENT，不然按钮点不到）
        bool OnWindowButton(Point pt)
        {
            Point sPt = PointToScreen(pt);
            Control[] ctrls = new Control[] { btnMin, btnMax, btnClose, btnScanTop, btnSideMore, btnRailExpand };
            for (int i = 0; i < ctrls.Length; i++)
            {
                Control c = ctrls[i];
                if (c != null && c.Visible && c.RectangleToScreen(c.ClientRectangle).Contains(sPt))
                    return true;
            }
            return false;
        }

        //  左：后台活动指示（扫描中）—— 一个小旋转圈 + 一句"扫描中"。
        //  为什么放在这儿：FileButler 把"编目中"放在标题栏左侧，用户抬头就能知道程序在忙什么；
        //  放在底部状态栏的话，眼睛根本不会去看（实测：用户以为"卡住了"）。
        void DrawScanPulse(Graphics g, int x, int cy)
        {
            long ms = Motion.Now;
            double a = (ms % 900) / 900.0 * 2 * Math.PI;
            using (var pen = new Pen(Theme.Accent, Math.Max(1.2f, Theme.S(1.4f))))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(pen, x - Theme.S(6), cy - Theme.S(6), Theme.S(12), Theme.S(12),
                          (float)(a * 180 / Math.PI), 110f);
            }
            Theme.DrawTxt(g, "扫描中", Theme.F(11f),
                new Rectangle(x + Theme.S(10), cy - Theme.S(10), Theme.S(90), Theme.S(20)), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        void SetCrumb(string text, int icon)
        {
            //  ★ 2026-10-26（照「自考工作台」的页头）：页名**常显**。
            //  第 149 轮把这里清空，前提是"主区还有个 21pt 大标题"—— 那确实一屏两个"仪表盘"。
            //  现在主区的大标题已撤（第 155 轮连 AddPageTitle 空壳也删了），页头就是唯一的页名位置，
            //  再清空就变成一条空白栏（用户看截图的原话："三个符号飘在白底上"）。
            //
            //  空值只可能是调用方真的没给（例如构造期还没切页），那种情况才保持空。
            crumbText = text == null ? "" : text;
            crumbIcon = icon;
            if (lblCrumb != null)
            {
                if (lblCrumb.Text != crumbText) lblCrumb.Text = crumbText;   // TextChanged 里会重新定位
                else if (crumbPlace != null) crumbPlace();
            }
            if (titleBar != null) titleBar.Invalidate();   // 图标与扫描指示要重画
        }

        //  面包屑图标：与分区标题那一套矢量图标共用（仪表盘 / 优化 / 游戏库 / 监控 / 说明 / 设置）
        internal static void DrawPageIcon(Graphics g, Rectangle r, int page, Color c)
        {
            switch (page)
            {
                case 0: Theme.DrawIconSpeedometer(g, r, c); break;
                case 1: Theme.DrawIconSliders(g, r, c); break;
                case 2: Theme.DrawIconGamepad(g, r, c); break;
                case 3: Theme.DrawIconPulse(g, r, c); break;
                case 4: Theme.DrawIconBook(g, r, c); break;
                default: Theme.DrawIconGear(g, r, c); break;
            }
        }

        //  扫描状态：抬头就能看见"程序在忙什么"（FileButler 把编目指示器放在标题栏左侧）
        void SetScanning(bool on)
        {
            if (scanOn == on) return;
            scanOn = on;
            if (scanBeat != null) { if (on) scanBeat.Start(); else scanBeat.Stop(); }
            if (titleBar != null) titleBar.Invalidate();
        }

        // ---- 导航行：页面导航 + 守护状态（品牌已移回侧栏顶部） ----
        // 两端渐隐的 1px 线：顶栏 / 底栏 / 页头共用同一种过渡，接缝不割裂
        static void PaintFadeLine(Graphics g, int w, int y)
        {
            Color lc = Theme.Line;
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), 1),
                       Color.FromArgb(0, lc), lc, 0f))
            {
                var cb = new ColorBlend(3);
                cb.Colors = new Color[] { Color.FromArgb(0, lc), lc, Color.FromArgb(0, lc) };
                cb.Positions = new float[] { 0f, 0.18f, 1f };
                br.InterpolationColors = cb;
                g.FillRectangle(br, 0, y, w, 1);
            }
        }


        // 换主题：**原地重建整棵控件树**。
        //  为什么必须重建：本项目的配色有大量是在**构造期**就被控件抄进字段的（Label 的前后景、
        //  RoundCheck/Spark/Bar 的底色、ListView/RichTextBox 的原生色…），只重绘刷不干净 ——
        //  以前的办法是"提示重启"，体验上等于"深色根本没法用"（用户实测反馈）。
        //  重建只覆盖"骨架 + 7 个页面"，**绝不碰窗体级的东西**（托盘、4 个定时器、Load/Activated 事件），
        //  那些在 BuildUi 里只装一次，重复装会双份触发。
        //  换主题的视觉过渡（2026-10-29 第 165 轮）：RebuildChrome 整棵重建 ~363ms，
        //  期间窗口是"旧内容被移除 → 全量重画"的硬切（用户看到一闪）。改为：
        //  重建前 PrintWindow 抓一帧旧画面铺在最上层（veil）—— 重建期间用户看到的
        //  仍是旧界面，完成后 120ms 淡出。抓帧失败（最小化 / 分层受限）就退回原行为，
        //  绝不盖一层黑/空帧。关动画（Motion.Enabled=false）时整段跳过。
                // Win32 标准：WM_SETREDRAW 窗口级原子更新，彻底消除换主题时的黑白混杂撕裂与闪烁
        const int WM_SETREDRAW = 0x000B;

        // 递归确保整棵控件树底色与前景色彻底对齐当前主题，杜绝黑白斑驳
        static void ApplyThemeToTree(Control root)
        {
            if (root == null) return;
            if (root is Pg || root is CaptionTable || root is CaptionPanel)
            {
                root.BackColor = Theme.Bg;
                root.ForeColor = Theme.Text;
            }
            else if (root is Sec)
            {
                root.BackColor = Theme.Bg;
                root.ForeColor = Theme.Text;
            }
            foreach (Control ch in root.Controls) ApplyThemeToTree(ch);
        }

        void RebuildChrome()
        {
            int keepPage = page;
            Cursor = Cursors.WaitCursor;
            try
            {
                // 1. 冻结窗口重绘（原子锁定）：重建控件树期间操作系统不产生任何中间帧，防止黑白撕裂
                if (IsHandleCreated)
                    SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

                SuspendLayout();
                try
                {
                    if (rootPanel != null) { Controls.Remove(rootPanel); rootPanel.Dispose(); rootPanel = null; }
                }
                catch { }

                // 2. 彻底同步整窗基底色与字体
                BackColor = Theme.Bg;
                ForeColor = Theme.Text;
                Font = Theme.F(9f);
                Icon = Program.GetAppIcon();

                // 3. 构建全树
                BuildChrome();
                try { Theme.ApplyChrome(Handle); } catch { }
                try { Theme.ScrollThemeTree(this); } catch { }
                try { ApplyThemeToTree(this); } catch { }
                ResumeLayout(true);

                // 4. 重排各页面并恢复选中视图
                for (int i = 0; i < pages.Length; i++) if (pages[i] != null) pages[i].Reflow();
                Switch(keepPage);
                FillGameList();
                SelectGame(selected, false);
                try { RefreshFgSummary(); } catch { }
                try { UpdateSys(); } catch { }
                try { UpdateLink(); } catch { }
                try { RefreshGridAsync(); } catch { }
            }
            catch (Exception ex)
            {
                Program.Log("换主题重建失败：" + ex.GetType().Name + " " + ex.Message + "（建议重启程序）");
            }
            finally
            {
                // 5. 恢复重绘并强制全树原子化上屏（一帧到位，丝滑纯净）
                if (IsHandleCreated)
                {
                    SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    Theme.HardRepaint(this);
                    try
                    {
                        Invalidate(true);
                        Update();
                        Refresh();
                    }
                    catch { }
                }
                Cursor = Cursors.Default;
            }
        }

        //  收起后的窄条：只有一枚"展开"按钮。宽度 44，和列表左缘的图标列一个量级，
        //  收起时内容区能多出 ~208px —— 封面墙一屏多放一列。
        Control BuildSideRail()
        {
            var rail = new CaptionPanel();
            rail.Dock = DockStyle.Fill;
            rail.BackColor = Theme.Bg;
            rail.Margin = Padding.Empty;
            Buffered(rail);
            rail.Paint += delegate(object s2, PaintEventArgs e)
            {
                using (var p = new Pen(Theme.Line, 1f))
                    e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height);
            };

            //  收起成导轨时窗口上没有任何品牌了（品牌行在展开态侧栏里），这里补一枚居中的徽标
            var logo = new CaptionPanel();
            logo.Dock = DockStyle.Top;
            logo.Height = Theme.S(46);
            logo.BackColor = Theme.Bg;
            logo.Paint += delegate(object s2, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int z = Theme.S(24);
                Theme.DrawBrandLogo(e.Graphics, new Rectangle((logo.Width - z) / 2, (logo.Height - z) / 2, z, z));
            };
            rail.Controls.Add(logo);

            var btn = new TxtLbl();
            btn.Dock = DockStyle.Top;
            btn.Height = Theme.S(52);            // 与标题行同高，展开后按钮落回原位
            btn.Text = "»";
            btn.TextAlign = ContentAlignment.MiddleCenter;
            btn.Font = Theme.FM(11f);
            btn.ForeColor = Theme.TextDim;
            btn.BackColor = Theme.Bg;
            btn.Cursor = Cursors.Hand;
            Theme.Tip.SetToolTip(btn, "展开侧边栏（Ctrl+B）");
            btn.Click += delegate { SetSideCollapsed(false); };
            btn.MouseEnter += delegate { btn.ForeColor = Theme.Text; };
            btn.MouseLeave += delegate { btn.ForeColor = Theme.TextDim; };
            rail.Controls.Add(btn);
            btnRailExpand = btn;
            return rail;
        }

        //  抽屉开合：只改第 0 列宽 + 两个面板的可见性，不动任何子控件布局。
        //  ⚠ 顶栏页签是按内容区中心摆的，侧栏一变就得重算 —— 否则页签停在旧中心，
        //    收起瞬间就能看出"导航歪了"。
        void SetSideCollapsed(bool on)
        {
            if (sideCollapsed == on && bodyCol != null) return;
            sideCollapsed = on;
            ApplySideVisibleItems();
            try { if (pages != null && page >= 0 && page < pages.Length && pages[page] != null) pages[page].Reflow(); } catch { }
            if (Cfg != null && Cfg.SideCollapsed != on)
            {
                Cfg.SideCollapsed = on;
                try { Program.SetConfigBool("ui", "sideCollapsed", on); } catch { }
            }
        }

        //  左栏可见性（2026-10-23）：**游戏库页不显示整列游戏栏**。
        //  理由：那一页的封面墙本身就是选择器，左栏只是把同一批游戏再列一遍 ——
        //  一样的 12 个名字、一样的点击结果（进详情），却占掉 252px 宽和 6 行高。
        //  其它五页保留它：仪表盘 / 优化 / 监控的操作对象都是"当前游戏"，得能就地换。
        void ApplySideVisibleItems()
        {
            //  左栏现在只有 品牌 / 导航 / 版本（2026-10-23 第三版）：
            //  库内检索与筛选都回到了工具条，所以这里不再有"按页折叠某一行"的逻辑。
            if (bodyCol != null && bodyCol.ColumnStyles.Count > 0)
                bodyCol.ColumnStyles[0].Width = Theme.S(sideCollapsed ? SideRailW : GameSideW);
            if (gameSidePane != null) gameSidePane.Visible = !sideCollapsed;
            if (sideRailPane != null) sideRailPane.Visible = sideCollapsed;
            if (bodyCol != null) bodyCol.PerformLayout();
        }

        // ---- 左栏：游戏库（Steam / 小黑盒那种常驻游戏栏）----
        //  为什么常驻：以前游戏列表只挂在「帧生成」页，用户在仪表盘/优化页想换游戏得先切页；
        // ---- 左栏：游戏库（Steam / 小黑盒那种常驻游戏栏）----
        //  为什么常驻：以前游戏列表只挂在「帧生成」页，用户在仪表盘/优化页想换游戏得先切页；
        //  现在左栏在**所有页面**都在，选中即进入该游戏的详情（帧生成页），点「库主页」回封面墙。
        Control BuildGameSide()
        {
            var side = new CaptionTable();
            side.Dock = DockStyle.Fill;
            Buffered(side);
            side.ColumnCount = 1; side.RowCount = 4;
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(68)));   // 品牌与操作
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(248)));  // 竖向菜单（6×40 + 8）
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));           // 弹性硬件状态看板
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));   // 版本脚注
            side.BackColor = Theme.Bg;
            side.Margin = Padding.Empty;
            side.Padding = Padding.Empty;
            side.Paint += delegate(object s2, PaintEventArgs e)
            {
                using (var p = new Pen(Theme.Line, 1f))
                    e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height);
            };
            sideCol = side;

            // 品牌（FileButler 的 fb-brand）：logo 32 圆角 9 + 名称 14.5 + 标语 10.5
            var brandPane = new CaptionPanel();
            brandPane.Dock = DockStyle.Fill;
            brandPane.BackColor = Theme.Bg;
            brandPane.Margin = Padding.Empty;
            Buffered(brandPane);
            brandPane.Paint += delegate(object s2, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int z = Theme.S(32);
                Theme.DrawBrandLogo(g, new Rectangle(Theme.S(14), (brandPane.Height - z) / 2, z, z));
                int tx = Theme.S(14) + z + Theme.S(10);
                Theme.DrawTxt(g, "Fluxion", Theme.FB(12f),
                    new Rectangle(tx, Theme.S(12), brandPane.Width - tx - Theme.S(38), Theme.S(22)), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                Theme.DrawTxt(g, "游戏启动与帧生成", Theme.F(9f),
                    new Rectangle(tx, brandPane.Height / 2 + Theme.S(2), brandPane.Width - tx - Theme.S(38), Theme.S(18)),
                    Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };

            brandPane.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                }
            };
            brandPane.DoubleClick += delegate(object sender, EventArgs e)
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            };

            // 库操作菜单按钮
            var btnMore = new TxtLbl();
            btnMore.Text = "";
            btnMore.Dock = DockStyle.Right;
            btnMore.Width = Theme.S(34);
            btnMore.ForeColor = Theme.TextDim;
            btnMore.BackColor = Theme.Bg;
            btnMore.Cursor = Cursors.Hand;
            Theme.Tip.SetToolTip(btnMore, "库操作菜单");
            btnMore.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = Math.Max(2.6f, Theme.S(3.2f));          // 点径
                float gap = Theme.S(5.5f);                         // 点距
                float cy = btnMore.Height / 2f, cx = btnMore.Width / 2f;
                using (var b = new SolidBrush(btnMore.ForeColor))
                    for (int i = -1; i <= 1; i++)
                        g.FillEllipse(b, cx + i * gap - d / 2, cy - d / 2, d, d);
            };
            btnMore.Click += delegate { ShowLibMenu(btnMore); };
            btnMore.MouseEnter += delegate { btnMore.ForeColor = Theme.Text; btnMore.Invalidate(); };
            btnMore.MouseLeave += delegate { btnMore.ForeColor = Theme.TextDim; btnMore.Invalidate(); };
            brandPane.Controls.Add(btnMore);
            btnSideMore = btnMore;

            // 竖向菜单：两侧留 12px
            var menuWrap = new Panel();
            menuWrap.Dock = DockStyle.Fill;
            menuWrap.BackColor = Theme.Bg;
            menuWrap.Margin = Padding.Empty;
            menuWrap.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            Buffered(menuWrap);
            navMenu = new NavMenu();
            navMenu.Dock = DockStyle.Top;
            navMenu.SetItems(PageNames, Program.ShareBuild
                ? new bool[] { false, false, true, false, true, true }
                : new bool[] { true, true, true, true, true, true });
            navMenu.SelectedChanged += delegate { SwitchFromNav(navMenu.Selected); };
            menuWrap.Controls.Add(navMenu);

            // 侧栏下半区：快捷游戏列表 + 系统守护状态看板
            var guardWrap = new Panel();
            guardWrap.Dock = DockStyle.Fill;
            guardWrap.BackColor = Theme.Bg;
            guardWrap.Margin = Padding.Empty;
            guardWrap.Padding = new Padding(Theme.S(12), Theme.S(16), Theme.S(12), Theme.S(12));
            Buffered(guardWrap);

            var guardCard = new Panel();
            guardCard.Dock = DockStyle.Bottom;
            guardCard.Height = Theme.S(96);
            guardCard.BackColor = Theme.Bg;
            guardCard.Margin = Padding.Empty;
            Buffered(guardCard);
            guardCard.Paint += delegate(object s2, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var cr = new Rectangle(0, 0, guardCard.Width - 1, guardCard.Height - 1);
                Theme.FillRound(g, cr, Theme.RCard, Theme.Panel);
                Theme.EdgeRound(g, cr, Theme.RCard, Theme.CardEdge, 1f);

                // 状态指示点与微标题
                int dotR = Theme.S(6);
                int dotX = Theme.S(12);
                int dotY = Theme.S(14);
                using (var b = new SolidBrush(Theme.Ok))
                    g.FillEllipse(b, dotX, dotY, dotR, dotR);

                int tx = dotX + dotR + Theme.S(6);
                Theme.DrawTxt(g, "系统守护待命", Theme.FB(9.5f),
                    new Rectangle(tx, Theme.S(8), guardCard.Width - tx - Theme.S(10), Theme.S(18)),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                // 硬件显卡型号/能力
                string gpu = Dlssg.GpuName();
                if (gpu.Length == 0) gpu = Program.GpuDisplayName;
                if (gpu.Length == 0 || gpu.Contains("探测")) gpu = "DirectX 12 独显";
                Theme.DrawTxt(g, gpu, Theme.F(9f),
                    new Rectangle(Theme.S(12), Theme.S(34), guardCard.Width - Theme.S(24), Theme.S(18)),
                    Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                // 优化能力/帧生成就绪徽章
                int badgeY = Theme.S(58);
                int badgeH = Theme.S(22);
                var bRect = new Rectangle(Theme.S(12), badgeY, guardCard.Width - Theme.S(24), badgeH);
                Theme.FillRound(g, bRect, Theme.S(4), Theme.Field);
                Theme.DrawTxt(g, "DLSSG / 帧生成已就绪", Theme.FM(8.5f),
                    bRect, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };

            sideList = new SideGameList();
            sideList.Dock = DockStyle.Fill;
            sideList.Picked += delegate(DlssgGame g)
            {
                if (g != null)
                {
                    SelectGame(g);
                    Switch(P_FG);
                }
            };
            sideList.MorePicked += delegate
            {
                Switch(P_FG);
                ShowLibView(true);
            };

            guardWrap.Controls.Add(sideList);
            guardWrap.Controls.Add(guardCard);

            // 底部：版本与权限
            var verCard = new Panel();
            verCard.Dock = DockStyle.Fill;
            verCard.BackColor = Theme.Bg;
            verCard.Margin = Padding.Empty;
            verCard.Padding = new Padding(Theme.S(14), 0, Theme.S(14), Theme.S(10));
            Buffered(verCard);
            verCard.Paint += delegate(object s2, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cy = (verCard.Height - Theme.S(18)) / 2;
                Theme.DrawTxt(g, "v" + Program.AppVersion + (Program.IsAdmin() ? " · 管理员" : ""),
                    Theme.F(9f),
                    new Rectangle(Theme.S(14), cy, verCard.Width - Theme.S(28), Theme.S(18)),
                    Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };

            side.Controls.Add(brandPane, 0, 0);
            side.Controls.Add(menuWrap, 0, 1);
            side.Controls.Add(guardWrap, 0, 2);
            side.Controls.Add(verCard, 0, 3);
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

        //  卡片上的「方案」菜单（2026-10-23）：点完直接应用，封面墙不消失。
        //  为什么需要它：选中游戏会把整面墙换成详情页，想改方案就得"离开库墙 → 进详情 → 改 → 返回"，
        //  用户原话是"很麻烦"。这里把它压成 卡片右上角 → 菜单 → 完成。

        //  卡面胶囊：菜单贴着卡片右侧展开
        void ShowSchemeMenuFor(CoverTile t)
        {
            if (t == null || t.Game == null) return;
            ShowSchemeMenuAt(t.Game, t, new Point(Math.Max(Theme.S(8), t.Width - Theme.S(250)), Theme.S(40)));
        }

        //  通用版：任意锚点弹出同一个方案菜单（卡面胶囊 / 详情页都走这里）。
        //  菜单内容与详情页的 cbScheme 同一份数据源 —— 三处入口不会各说各话。
        void ShowSchemeMenuAt(DlssgGame gm, Control anchor, Point at)
        {
            if (gm == null || cbScheme == null || anchor == null) return;
            var m = MenuFactory.New();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false;
            string cur = Plan.Current(gm.Dir);
            for (int i = 0; i < cbScheme.Items.Count; i++)
            {
                int idx = i;
                string label = cbScheme.Items[i].ToString();
                //  标一下"当前这套"：菜单里能一眼看出改之前是什么
                if (i > 0 && SchemeShort(ChosenActionAt(idx)) == cur) label = "✓ " + label;
                m.Items.Add(label, null, delegate { PickSchemeFor(gm, idx); });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("打开完整详情…", null, delegate { SelectGame(gm, true); });
            m.Show(anchor, at);
        }

        //  把"菜单里第 idx 项"翻成 action 字符串（不依赖当前 selected，菜单着色用）
        string ChosenActionAt(int idx)
        {
            if (cbScheme == null || idx < 0 || idx >= cbScheme.Items.Count) return "none";
            int keep = cbScheme.SelectedIndex;
            cbScheme.SelectedIndex = idx;
            string a = ChosenAction();
            cbScheme.SelectedIndex = keep;
            return a;
        }

        void PickSchemeFor(DlssgGame g, int idx)
        {
            if (g == null) return;
            SelectGame(g, false);                      // 只同步 selected，**不切页** —— 墙留在原地
            if (cbScheme != null && idx >= 0 && idx < cbScheme.Items.Count) cbScheme.SelectedIndex = idx;
            DoInstallCurrent();
            FillGameList();                            // 卡上的"可嵌入/无组件"之类状态可能变了
        }

        void ShowLibMenu(Control anchor)
        {
            var m = MenuFactory.New();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false;
            m.Items.Add("扫描游戏", null, delegate { RunScan(true); });
            m.Items.Add("添加扫描目录…", null, delegate { DoAddScanRoot(); });
            if (!Program.ShareBuild)
                m.Items.Add("获取封面", null, delegate { DoFetchCovers(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("添加游戏…", null, delegate { DoAddGame(); });
            m.Items.Add(new ToolStripSeparator());
            //  低频开关放菜单：顶栏那一行要留给"我现在是什么状态"（统计），不是给开关
            string hid = (chkShowHidden != null && chkShowHidden.Checked) ? "✓ 显示已隐藏" : "显示已隐藏";
            m.Items.Add(hid, null, delegate
            {
                if (chkShowHidden != null) chkShowHidden.Checked = !chkShowHidden.Checked;
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("收起侧栏（Ctrl+B）", null, delegate { SetSideCollapsed(true); });
            m.Show(anchor, new Point(0, anchor.Height));
        }

        // ---- 底部状态栏 ----
        Panel BuildFooter()
        {
            var foot = new Panel();
            foot.Dock = DockStyle.Fill;
            foot.Margin = Padding.Empty;
            foot.BackColor = Theme.Bg;
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

            lblFoot = new TxtLbl();
            lblFoot.Dock = DockStyle.Fill;
            lblFoot.Font = Theme.F(9f);
            lblFoot.ForeColor = Theme.TextDim;
            lblFoot.BackColor = Theme.Bg;
            lblFoot.TextAlign = ContentAlignment.MiddleLeft;
            lblFoot.Text = "就绪";

            var dotPanel = new Panel();
            dotFoot = dotPanel;
            dotPanel.Dock = DockStyle.Left;
            dotPanel.Width = Theme.S(28);
            dotPanel.BackColor = Theme.Bg;
            Buffered(dotPanel);
            dotPanel.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int dotR = Theme.S(5);
                int dotX = Theme.S(16);
                int dotY = (dotPanel.Height - dotR) / 2;
                //  ★ 第 142 轮：去掉外围那圈 46 alpha 的"光晕"。
                //  浅色底上它是一团淡绿雾，和 Cursor / 呼吸灯那种效果撞脸 —— 廉价感的一半来自这里。
                //  状态灯只需要一件事：告诉我"是好的还是坏的"。一个实心圆点就够。
                using (var bDot = new SolidBrush(footDot))
                    e.Graphics.FillEllipse(bDot, dotX, dotY, dotR, dotR);
            };

            var statusBox = new Panel();
            statusBox.Dock = DockStyle.Fill;
            statusBox.BackColor = Theme.Bg;
            Buffered(statusBox);
            statusBox.Controls.Add(lblFoot);
            statusBox.Controls.Add(dotPanel);
            dotPanel.BringToFront();
            lblFoot.BringToFront();

            var clockBox = new Panel();
            clockBox.Dock = DockStyle.Right;
            clockBox.Width = Theme.S(90);
            clockBox.BackColor = Theme.Bg;
            clockBox.Padding = new Padding(0, Theme.S(4), Theme.S(12), Theme.S(4));
            Buffered(clockBox);

            lblClock = new TxtLbl();
            lblClock.Dock = DockStyle.Fill;
            lblClock.Font = Theme.Num(9f);
            //  时钟是**被动信息**：不用强调色、也不铺一块比底色深一档的方块。
            //  以前是 Cyan 字 + Field 底 —— 一个青绿色的矩形贴在右下角，看着像"能点的按钮"，
            //  而同屏的蓝色本该只表示"可点击"（2026-10-23 与状态灯语义一起收口）。
            lblClock.ForeColor = Theme.TextDim;
            lblClock.BackColor = Theme.Bg;
            lblClock.TextAlign = ContentAlignment.MiddleCenter;
            lblClock.Text = DateTime.Now.ToString("HH:mm:ss");
            clockBox.Controls.Add(lblClock);

            foot.Controls.Add(clockBox);

            var btnQuick = new FlatBtn();
            btnQuick.Text = "▶ 快速启动";
            btnQuick.Width = Theme.S(84);
            btnQuick.Dock = DockStyle.Right;
            btnQuick.Font = Theme.F(8.5f);
            btnQuick.Corner = Theme.Bg;
            btnQuick.Margin = Padding.Empty;
            btnQuick.Cursor = Cursors.Hand;
            Theme.Tip.SetToolTip(btnQuick, "快速启动当前选中或最近游戏");
            btnQuick.Click += delegate
            {
                if (selected != null) { DoLaunchGame(); return; }
                if (games != null && games.Count > 0)
                {
                    DlssgGame topGame = null;
                    foreach (DlssgGame g in games)
                    {
                        if (g != null && !g.Ignored && !g.Hidden)
                        {
                            topGame = g;
                            break;
                        }
                    }
                    if (topGame != null)
                    {
                        SelectGame(topGame, false);
                        DoLaunchGame();
                        return;
                    }
                }
                Switch(P_FG);
            };
            foot.Controls.Add(btnQuick);

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
            //  底部留白与块间距同一档（原来是 S(10)，滚到底时最后一块离底栏太近）
            content.Padding = new Padding(0, 0, 0, Theme.S(18));

            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = new Pg();
                pages[i].Dock = DockStyle.Fill;
                pages[i].Visible = false;
                //  ★ 第 142 轮：表单页的栏宽上限。
                //  设置 / 说明 / 实时监控都是「一列标签 + 值」的表单，窗口一宽，卡片跟着宽、
                //  控件却没宽 —— 中间空出 400px，像表单没写完。
                //  库页不设：封面墙要列数，限宽反而浪费。
                //  ★ 2026-10-29 对齐「自考工作台」的主区列宽（实测 main max-w-[1120px] 居中）：
                //  除长文页外全部放开到 1120 —— 窄窗口下交给窗口，超宽屏封顶居中。
                //  行内控件本来就是"标签左、值右对齐"（第 142 轮），列宽放大只是把留白还给中间。
                //  说明页保持 680：那是长文页，一行 9.5pt 中文拉到 1120 ≈ 88 字，超出阅读宽度。
                if (i == P_FG) pages[i].MaxSecW = 0;
                else if (i == P_HELP) pages[i].MaxSecW = Theme.S(680);
                else pages[i].MaxSecW = Theme.S(1120);
                content.Controls.Add(pages[i]);
            }

            BuildPageDashboard();
            BuildPageOptimize();
            BuildPageFrameGen();
            BuildPageMonitor();
            BuildPageHelp();
            BuildPageSettings();
            return content;
        }

        //  Ctrl+B：侧栏抽屉开合（按钮旁 tooltip 里承诺过，得有）
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.B)) { SetSideCollapsed(!sideCollapsed); return true; }

            //  ★ 封面墙的键盘走位：只在「库视图 + 焦点不在输入框」时接管，
            //  否则会把搜索框里的左右键（移动光标）也吃掉。
            if (page == P_FG && libViewOn && !FocusInTextField()
                && libShown != null && libShown.Count > 0)
            {
                if (keyData == Keys.Left)  { LibMoveFocus(-1, 0); return true; }
                if (keyData == Keys.Right) { LibMoveFocus(1, 0);  return true; }
                if (keyData == Keys.Up)    { LibMoveFocus(0, -1); return true; }
                if (keyData == Keys.Down)  { LibMoveFocus(0, 1);  return true; }
                if (keyData == Keys.Enter && selected != null) { DoLaunchGame(); return true; }
            }

            //  Esc 两条路：详情页 → 回列表；库视图里 → 取消选中（收起底部状态栏）
            if (keyData == Keys.Escape && page == P_FG && !FocusInTextField())
            {
                if (!libViewOn) ShowLibView(true);
                else if (selected != null) SelectGame(null, false);
                return true;
            }
            //  Ctrl+V：剪贴板是图片 → 贴成当前选中游戏的封面（自定义封面最快的路：截图 → Ctrl+V）
            if (keyData == (Keys.Control | Keys.V))
            {
                if (!Program.ShareBuild && selected != null && !FocusInTextField() && ClipboardHasImage())
                {
                    DoPasteCover();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        //  ★ 用户点导航：已经在库页的详情视图里再点「游戏库」= 回列表。
        //  这条只挂在导航点击上，不能放进 Switch —— Switch 也被"重建现场 / 程序跳转"调用，
        //  那些场景要的是"保持用户原来所在的视图"（探针里"换主题后仍是详情视图"就钉这一点）。
        void SwitchFromNav(int idx)
        {
            if (idx == P_FG && page == P_FG && !libViewOn)
            {
                ShowLibView(true);
                SetCrumb(PageNames[P_FG], P_FG);
                return;
            }
            Switch(idx);
        }

        void Switch(int idx)
        {
            page = idx;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = (i == idx);
            }
            //  分段控件的滑块自己会滑过去（200ms）—— 这里只需静默同步，不发事件（页面已在上面切完）。
            if (navMenu != null) navMenu.SetSelected(idx);
            ApplySideVisibleItems();
            if (pages[idx].Visible) pages[idx].Reflow();
            //  页头文字淡入（200ms）：切页这一下有个"新内容到了"的信号，比硬切柔和。
            //  只影响页头那两行自绘文字，不动任何布局 —— 切页本身仍然是瞬时的。
            try { pages[idx].PlayHeadEnter(); } catch { }
            //  标题栏面包屑跟着走（FileButler 的做法：抬头就能确认自己在哪页）
            SetCrumb((PageNames.Length > idx ? PageNames[idx] : "")
                     + (idx == P_FG && selected != null ? " · " + selected.Title : ""), idx);
            if (idx == P_FG) RefreshFgSummary();
            if (actBar != null) ShowActionBar(idx == P_FG && libViewOn && selected != null);
            // 设置页：重算分辨率档位的"本机可用性"（用户可能刚换过显示器/改过分辨率，
            //   而档位标注是在构建页面那一刻算的 —— 不刷新就一直显示旧屏的判断）
            if (idx == P_SET)
            {
                try { RefreshResCombos(); } catch { }
                //  更新检查是后台静默跑的（第 160 轮）：结果只进日志，进设置页这一刻才回填到卡上
                if (lblUpd != null && !lblUpd.IsDisposed)
                {
                    string last = Cfg != null && Cfg.UpdateCheckAt.Length > 0 ? Cfg.UpdateCheckAt : "还没检查过";
                    SetBody(lblUpd, "当前版本 " + Program.AppVersion + " · 上次检查：" + last);
                }
            }
            // 性能优化页：重读显示器设备状态（用户可能刚在设备管理器里改过、或插拔过屏）
            if (idx == P_OPT) { try { FillMonList(); } catch { } }
            // 实时监控页：切页立即采样一次，无需等待下一个 4 秒定时器
            if (idx == P_MON) { try { UpdateSys(); } catch { } }
        }

        // ---------------------- 页 1：仪表盘 ----------------------
        //  页面大标题（2026-10-24 第 136 轮）
        //  ---------------------------------------------------------------------------
        //  库页早就有这一层（Large Title），其余五页没有 —— 那几页顶部直接就是第一张卡，
        //  和库页摆在一起像两个软件。这里补成同一套：21pt Medium + 右边可选的一行统计。
        //  ⚠ 用空标题的 Sec（Plain）来装，这样不会被 Pg.Add 误当成"领头卡"（hero）。
        //  页头副标题（页名在标题栏里常显，主区不再画大标题）。
        //  ★ AddPageTitle 已删（2026-10-29 第 155 轮）：页名搬进页头（第 151 轮）、副标题并进
        //    「快捷操作」的 MetaLine 之后，它的 5 个调用点全部退化成"只要顶部多 34px"的空壳
        //    （new Sec("") + 4px Block，永远返回 null）。页顶留白改由 Pg.LayoutOnce 的
        //    首块起点统一给（y = SecGap），不再靠每页塞一个空分区。

        void BuildPageDashboard()
        {
            var p = pages[P_DASH];
            p.LeftRatio = 0.50; // 现代 2x2 Bento Grid 等宽双栏布局 (50% / 50%)
            p.GridRowAlign = true; // 逐行等高网格对齐，底部平齐消除大块留白

            // ================= Bento 1 (左上): 游戏联动与实时状态 =================
            var sLink = new Sec("游戏联动");
            lblGameState = sLink.Body(new BreathingLabel("待命 · 等待状态…"));
            lblGameState.Font = Theme.FB(13.5f);
            lblGameState.ForeColor = Theme.Text;

            lblLink = sLink.Body("");
            lblLink.Font = Theme.F(9.5f);
            lblLink.ForeColor = Theme.TextDim;

            lblLinkAct = sLink.Body(LinkIdleText);

            var b1 = new FlatBtn(); b1.Text = "游戏库"; b1.Kind = BtnKind.Primary; b1.Width = Theme.S(80);
            b1.Click += delegate { Switch(P_FG); };
            var b2 = new FlatBtn(); b2.Text = "刷新联动"; b2.Width = Theme.S(80);
            b2.Click += delegate { UpdateLink(); Log("已刷新联动状态"); };
            var b3 = new FlatBtn(); b3.Text = "联动设置"; b3.Width = Theme.S(80);
            b3.Click += delegate { Switch(P_SET); };
            sLink.Buttons(b1, b2, b3);

            p.AddLeft(sLink);

            // ================= Bento 2 (右上): 系统优化健康度看板与核心行动 =================
            var sHealth = new Sec("优化健康度");
            var healthBox = new Panel();
            healthBox.Margin = Padding.Empty;
            healthBox.Padding = Padding.Empty;
            healthBox.BackColor = Theme.Panel;

            gaugeHealth = new RadialGauge();
            gaugeHealth.Location = new Point(0, Theme.S(12));
            gaugeHealth.Size = new Size(Theme.S(74), Theme.S(74));
            healthBox.Controls.Add(gaugeHealth);

            int rightX = Theme.S(82);
            int initRw = Math.Max(Theme.S(180), Theme.S(350) - rightX);

            lblHealth = new TxtLbl();
            lblHealth.Location = new Point(rightX, Theme.S(4));
            lblHealth.Font = Theme.FB(10.5f);
            lblHealth.ForeColor = Theme.TextSafe(Theme.Ok);
            lblHealth.Text = "优化健康度探测中…";
            lblHealth.AutoSize = false;
            lblHealth.Size = new Size(initRw, Theme.S(22));
            healthBox.Controls.Add(lblHealth);

            var pnlBtns = new FlowLayoutPanel();
            pnlBtns.Location = new Point(rightX, Theme.S(32));
            pnlBtns.Size = new Size(initRw, Theme.S(36));
            pnlBtns.FlowDirection = FlowDirection.LeftToRight;
            pnlBtns.WrapContents = false; // 严禁折行，保持单行整齐排列
            pnlBtns.Margin = Padding.Empty;
            pnlBtns.Padding = Padding.Empty;
            pnlBtns.BackColor = Theme.Panel;

            var q1 = new FlatBtn(); q1.Text = "一键优化"; q1.Kind = BtnKind.Primary;
            q1.Margin = new Padding(0, 0, Theme.S(4), 0);
            q1.Click += delegate { DoOptimize(); };
            var q2 = new FlatBtn(); q2.Text = "系统体检";
            q2.Margin = new Padding(0, 0, Theme.S(4), 0);
            q2.Click += delegate { RefreshGridAsync(); Log("已刷新优化项状态（体检结果见「性能优化」页）"); Switch(P_OPT); };
            var q3 = new FlatBtn(); q3.Text = "恢复备份";
            q3.Margin = Padding.Empty;
            q3.Click += delegate { DoRestore(); };
            pnlBtns.Controls.Add(q1);
            pnlBtns.Controls.Add(q2);
            pnlBtns.Controls.Add(q3);
            healthBox.Controls.Add(pnlBtns);

            lblDashSub = new MetaLine();
            lblDashSub.Location = new Point(rightX, Theme.S(72));
            lblDashSub.Size = new Size(initRw, Theme.S(22));
            healthBox.Controls.Add(lblDashSub);

            lblHealthSub = new Label();
            lblHealthSub.Visible = false;
            healthBox.Controls.Add(lblHealthSub);

            Action updateBento2 = delegate
            {
                int rw = Math.Max(Theme.S(150), healthBox.Width - rightX - Theme.S(6));
                lblHealth.Width = rw;
                pnlBtns.Width = rw;
                lblDashSub.Width = rw;
                int gap = Theme.S(4);
                // 弹性布局：确保按钮宽度不超界，杜绝出现“恢”半字裁切
                if (rw >= Theme.S(236))
                {
                    // 宽屏模式：完整三按钮
                    q3.Visible = true;
                    q1.Text = "一键优化"; q1.Width = Theme.S(78);
                    q2.Text = "系统体检"; q2.Width = Theme.S(78);
                    q3.Text = "恢复备份"; q3.Width = Theme.S(78);
                }
                else if (rw >= Theme.S(175))
                {
                    // 自适应紧凑模式：三按钮精简文案（72 + 4 + 48 + 4 + 48 = 176px）完美容纳
                    q3.Visible = true;
                    q1.Text = "一键优化"; q1.Width = Theme.S(72);
                    q2.Text = "体检";     q2.Width = Theme.S(48);
                    q3.Text = "备份";     q3.Width = Theme.S(48);
                }
                else
                {
                    // 窄屏双按钮模式：只保留核心操作，隐藏次要按钮
                    q3.Visible = false;
                    int btnW = Math.Max(Theme.S(48), (rw - gap) / 2);
                    q1.Text = "一键优化"; q1.Width = btnW;
                    q2.Text = "系统体检"; q2.Width = btnW;
                }
                RefreshHealthTitle(rw);
            };
            healthBox.Resize += delegate { updateBento2(); };
            updateBento2();

            sHealth.Block(healthBox, Theme.S(108));
            p.Add(sHealth);

            // ================= Bento 3 (左下): 核心硬件遥测 (KPI 4 瓦片) =================
            var sLoad = new Sec("实时负载");
            barCpu = new Bar(); barCpu.Caption = "CPU"; barCpu.BarColor = Theme.Accent;
            barMem = new Bar(); barMem.Caption = "内存"; barMem.BarColor = Theme.Accent;
            barGpu = new Bar(); barGpu.Caption = "GPU"; barGpu.BarColor = Theme.Ok;
            barTemp = new Bar(); barTemp.Caption = "GPU 温度"; barTemp.BarColor = Theme.Warn;
            barCpu.Grid = barMem.Grid = barGpu.Grid = barTemp.Grid = true;
            sLoad.Block2(barCpu, barMem, Theme.S(72));
            sLoad.Block2(barGpu, barTemp, Theme.S(72));
            p.AddLeft(sLoad);

            // ================= Bento 4 (右下): 帧生成 (DLSSG) 运行看板 =================
            var sFg = new Sec("帧生成（DLSSG）");
            lblFgRuntime = sFg.Body("—");
            lblFgLast = sFg.Body("");
            var btnGo = new FlatBtn(); btnGo.Text = "管理帧生成 →"; btnGo.Kind = BtnKind.Primary; btnGo.Width = Theme.S(124);
            btnGo.Click += delegate { Switch(P_FG); };
            sFg.Buttons(btnGo);
            p.Add(sFg);

            // ================= Bento 5 (通栏底部): 本机硬件与系统环境 =================
            var sSys = new Sec("系统信息");
            sSys.FullSpan = true;
            lblSysInfo = sSys.Body("正在探测硬件…");
            p.Add(sSys);
        }

        // ---------------------- 页 2：性能优化 ----------------------
        void BuildPageOptimize()
        {
            var p = pages[P_OPT];

            var s1 = new Sec("操作");
            btnOptimize = new FlatBtn(); btnOptimize.Text = "一键优化"; btnOptimize.Kind = BtnKind.Primary; btnOptimize.Width = Theme.S(100);
            btnOptimize.Click += delegate { DoOptimize(); };
            var btnCustom = new FlatBtn(); btnCustom.Text = "自定义项"; btnCustom.Width = Theme.S(92);
            btnCustom.Click += delegate { ShowCustomDialog(); };
            //  与仪表盘「快捷操作」里的同名按钮统一中性（5657 行）：同一个动作、同一个确认框，
            //  一处红一处中性本身就是不一致；红块还会让"操作"整排看着像警告区。
            btnRestore = new FlatBtn(); btnRestore.Text = "恢复备份"; btnRestore.Width = Theme.S(92);
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
            // 2026-10-21 新增：一键优化会写注册表 / 改服务 / 动 BCD，「恢复备份」只认得我们自己
            //   备份的那几项 —— 落在备份覆盖不到的地方时，系统还原点是唯一退路。
            //   自动打点（勾了实验档时）见 DoOptimize；这个按钮是手动入口。
            var btnRestorePt = new FlatBtn(); btnRestorePt.Text = "创建还原点"; btnRestorePt.Width = Theme.S(96);
            btnRestorePt.Click += delegate
            {
                btnRestorePt.Enabled = false; btnRestorePt.Text = "创建中…";
                var gw0 = new BackgroundWorker();
                gw0.DoWork += delegate(object s, DoWorkEventArgs e)
                { e.Result = Program.CreateRestorePoint("Fluxion 手动 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")); };
                gw0.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
                {
                    btnRestorePt.Enabled = true; btnRestorePt.Text = "创建还原点";
                    string why = e.Error != null ? e.Error.Message : (e.Result as string);
                    if (string.IsNullOrEmpty(why)) { Log(">>> 系统还原点创建完成"); return; }
                    Log("⚠ 未创建还原点：" + why);
                    // 手动点的按钮，失败必须让人看见 —— 优化仍然能做，只是少了这层保险
                    MessageBox.Show(this, "没能创建还原点：\n\n" + why
                        + "\n\n优化本身不受影响，只是少了这层保险。", "创建还原点",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                gw0.RunWorkerAsync();
            };
            var btnRefresh = new FlatBtn(); btnRefresh.Text = "刷新"; btnRefresh.Width = Theme.S(76);
            btnRefresh.Click += delegate { RefreshGridAsync(); UpdateLink(); Log("已刷新状态"); };
            s1.Buttons(btnOptimize, btnCustom, btnRestore, btnDefaults, btnRestorePt, btnRefresh);
            s1.Body("💡 使用建议：若平时需要使用安卓模拟器、WSL2 或 Docker，一键优化时保持推荐配置（排除底部的「实验档」）即可，兼顾低延迟与环境兼容。\n所有修改在执行前均会自动快照备份到数据目录的 backup\\，可随时还原。");
            p.Add(s1);

            var s2 = new Sec("优化项明细（当前系统实际设置）");
            // 状态列 86（不是 70）：胶囊里要放「已生效」这种 3 个汉字 + 圆点，
            // 70px 下胶囊的可用文字区只剩 ~37px，正好卡在省略号边缘（v3.6.1 离屏渲染实测）。
            //  高度 250 → 300：行高从 20 抬到 28 之后，同样的高度只剩 9 行可见，
            //  加一档把可见行数拉回 10 行以上（页面本来就能滚，不占额外版面）。
            //  列宽（2026-10-25 第 147 轮实测）：探针量出「优化项」列宽 196 / 需要 216 → 名字被截
            //  （「处理器最大状态(睿频上限)」断在省略号上），而「状态」列 177 却只用 136。
            //  把状态列的富余让给优化项 —— 名字是行的标识，断了就认不出是哪一项；
            //  当前值/期望值里有解释性长句（需要 500+px），本就放不下，靠 AttachCellTips 看全文。
            //  ⚠ 状态列 118 有富余是**故意留的**：Resize 按"当前列宽比例"缩放（第 142 轮的设计，
            //    要保留用户拖过的相对比例），精确控宽度做不到；优化项是硬要求，宁可状态列宽点。
            lvOpt = NewList(new string[] { "优化项", "状态", "当前值", "期望值" }, new int[] { 240, 118, 200, 0 }, Theme.S(300));
            lvOpt.Resize += delegate { FillLastColumn(lvOpt, Theme.S(160)); };
            // v3.6.0：体检表不再只是"看"。带 ⚙ 的行双击即可改这一项的具体取值
            //（着色器缓存 12 档、DLSS 预设字母、量子长度、前后台帧率上限…）。
            lvOpt.DoubleClick += delegate { OpenKnobForRow(); };
            var btnKnobSel = new FlatBtn(); btnKnobSel.Text = "调整选中项"; btnKnobSel.Width = Theme.S(100);
            btnKnobSel.Click += delegate { OpenKnobForRow(); };
            var btnKnobAll = new FlatBtn(); btnKnobAll.Text = "取值细调…"; btnKnobAll.Width = Theme.S(100);
            btnKnobAll.Click += delegate { ShowKnobDialog(null); };
            s2.Buttons(btnKnobSel, btnKnobAll);
            //  高度交给内容自己定（见 FillList 末尾）：让表**永远不需要内层滚动** ——
            //  ListView 的竖条画在非客户区，是一条压不下去的系统灰带（藏它会和控件重排
            //  互相触发、直接把探针跑挂，见记忆 五十二轮）。表长到装下全部行之后，
            //  那条灰带根本不会出现，滚动交给页面自己那条细条。
            s2.Block(BoxOf(lvOpt), 0);
            s2.Body("已生效=一致，未生效=被重置，灰色=不适用。\n"
                    + "带 ⚙ 的行可调：选中后按「调整选中项」；列宽乱了在表头右键恢复。");
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
            s4.Block(BoxOf(lvMon), 0);          // 高度由 FitListToRows 跟着行数定
            s4.Body("「禁用」= 设备管理器禁用，重启后仍禁；只剩一块屏时拒绝。误操作点「全部启用」。");
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
            //  同 lvOpt：行数变了就把表高调到刚好装下，别让原生竖条冒出来（见 FitListToRows）
            FitListToRows(lvMon, 3);
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
            //  换主题会重建整棵控件树 —— 这两个登记表是字段，不先清空就会把上一轮
            //  已 Dispose 的分区也记进来（探针实测：重建一次 2/13 变 4/26）。
            //  页签归属表同一道理：留着旧对象既没用，也拦不住 GC。
            libSecs.Clear();
            secTab.Clear();
            secWant.Clear();
            detailSecs.Clear();
            var p = pages[P_FG];
            // Steam 式大横幅：封面铺满 + 底部压暗 + 大号游戏名（选中游戏时更新）
            headFg = p.AddHead("游戏库", "从左侧选一个游戏");
            headFg.Hero = true;
            //  下方渐隐带：过渡太短会"生硬"，太长又会把横幅挤扁。
            //  92px 的版本被用户指出"这一块空空的"—— 带子里没有任何内容，一长就是一片死白。
            //  现在收到 70px（smoothstep 两端斜率为 0，70px 依然不见起止），
            //  同时把正文高度从 300 收到 282，整条页头从 392 降到 352。
            //  ★ 取消雾化带（2026-10-22，学 Steam）：hero 就是一张**圆角卡片**，图片满铺到底，
            //  底部只做压暗（保证标题/按钮可读），不再"淡出成页面底色"。
            //  浅色主题下"图 → 灰"的雾化必然是一段白雾，加多长都像一条带子；
            //  Steam 那边是深色底 + 内容压在图上，压根没有这段过渡 —— 交给卡片边界最干净。
            headFg.FogH = 0;
            headFg.Height = Theme.S(352);
            headFg.BaseH = headFg.Height;
            //  详情页内容本来就短（一块面板 + 两个按钮）。内容装不满一屏时，多出来的高度给封面，
            //  而不是在底下留半屏死白 —— 用户原话"下面又空出来该怎么办"。
            p.FillHead = true;

            //  操作条不再单独占一块：开始游戏与「平台 · 目录」直接画在 hero 底部（见 HeadBox.PaintHero）
            headFg.Play = delegate { DoLaunchGame(); };

            // ---------- 概览锚点 ----------
            //  以前这里是一张「游戏信息」卡（平台 / 目录 / 组件 / 状态 三行）。这三项在 hero 副标题、
            //  推荐理由、方案区块状态里都在说 —— 用户原话「真的有这么精简吗」。
            //  卡片整张删掉，标签栏的「概览」改为直接滚回页顶（见下方 sTabs）。

            // ---------- 标签栏（Steam 库：操作条下面一排分区入口）----------
            //  以前三个页签只是"滚到某个位置"：内容全铺在一条 3000 px 的竖条上，
            //  点"诊断"等于跳过两屏。现在改成真切换（见 ApplyDetailVis）：
            //  「概览」只有一块面板，「参数」「诊断」按需点开 —— 这才是参考图那个密度。
            var sTabs = new Sec("");
            tabsFg = new TabBar();
            tabsFg.Tabs = new string[] { "概览", "参数", "诊断" };
            //  换主题会重建整棵控件树：新 TabBar 默认选中「概览」，
            //  但 curDetailTab 还是旧值 —— 不同步就会出现"标签亮在概览、内容却是诊断那几块"。
            tabsFg.Selected = curDetailTab;
            tabsFg.SelectedChanged += delegate
            {
                int ti = tabsFg.Selected;
                if (ti < 0 || ti > 2) return;
                curDetailTab = ti;
                if (pages != null && P_FG < pages.Length && pages[P_FG] != null)
                {
                    pages[P_FG].SetScrollY(0);          // 换页签回到顶部，别停在上一个页签的滚动位置
                    ApplyDetailVis();
                    pages[P_FG].Reflow();
                }
            };
            sTabs.Block(tabsFg, Theme.S(40));
            AddDetail(p, sTabs, -1);        // -1 = 任何页签都显示（标签栏自己）


            // XeSS 多帧生成：社区验证的两套部署（绝区零 d3d12 入口 / 鸣潮 dxgi 入口），2026-09-13 实机跑通
            // ---------- ① 方案推荐：自动识别 + 强调色 + 一键应用 ----------
            //  用户反馈"说法太乱、没有推荐度"：这里只给一条结论（大号强调色）+ 适用度星级 +
            //  一句话理由，剩下的全部交给「一键应用推荐方案」。
            //  ---- 卡片结构（2026-10-19 重排）----
            //  以前是「大标题 / 星级+判定+状态挤一行 / 一坨 150 字不带气口的正文」。改成四层：
            //    ① 结论   12pt 加粗强调色  —— 一眼看到该怎么装
            //    ② 评价   9f   加粗强调色   —— 几星、什么判定、现在什么状态
            //    ③ 理由   9.5pt 正文色     —— 一句话，不再是"理由+限制+实测数据"混在一起
            //    ④ 依据   9f   弱化色，一行一条 —— 刷新率甜点、前置条件、警告
            //  ④ 在多数游戏上是空的，靠 Sec 的"空行不占位"自动收掉。
            //  行距按语义分组（绝对值）：①②是同一组判词，贴紧；③是解释，④另起一层。
            // ---------- 此游戏的适配方案（对齐 NVIDIA App）----------
            //  以前这里是一整张「① 方案推荐」卡（结论 / 星级 / 理由 / 依据 / 两个按钮，264 px），
            //  但同样的结论在 hero 副标题、方案下拉、方案区块的彩色标题里已经各说了一遍 ——
            //  用户原话「真的有这么精简吗」。现在只留"选择器 + 两行结论"：
            //    第 1 行：方案名（推荐的那套，跟着游戏变）
            //    第 2 行：一句话理由
            //  动作按钮交给下面方案区块的「切换到本方案」，依据类细节（刷新率甜点 / NR 警告）搬到参数区。
            //  2026-10-20「拼接感」的主要来源就在这块：标题、下拉、状态、两个**通栏色块**
            //  直接铺在灰底上，没有任何"面"把它们收拢 —— 读起来像一段网页表单。
            //  现在收进标准 Sec 卡片（和别的页同一种卡，同一套内边距），动作也从两根通栏大条
            //  改成并排一行：主按钮 200 / 次按钮 140（两个等宽通栏条是最像表单的形态）。
            var sPick = new Sec("此游戏的适配方案");
            cbScheme = new RoundCombo();
            cbScheme.Items.AddRange(new object[] {
                "自动 · 按游戏推荐", "OptiScaler · 方案 A", "DLSS MFG 0.3.5 · 基础兼容",
                "DLSS MFG " + Dlssg040.Ver + " · 进阶内核", "MFG Bridge · 方案 B", "Smooth Motion · 驱动插帧" });
            cbScheme.SelectedIndex = 0;
            cbScheme.SelectedIndexChanged += delegate { ApplySchemeVis(); };
            //  标题已经在卡片头上了，这一行只放选择器（宽度按参考图那个面板量级：320）
            sPick.Pair(null, cbScheme, Theme.S(320), null, null, 0);
            //  状态一行：当前到底哪套在跑（原生 / 某套在用的槽位）
            lblFgState = sPick.Body("检查中…");
            lblFgState.Font = Theme.F(9.5f);
            lblFgState.ForeColor = Theme.TextDim;
            //  提示一行（NVIDIA App 那条 info 条的位置）：只放"要不要动它"的一句话，空就折掉
            lblFgInfo = sPick.Body("");
            lblFgInfo.Font = Theme.F(9.5f);
            lblFgInfo.ForeColor = Theme.TextDim;
            //  动作行：安装并应用 / 卸载补丁
            btnInstall = new FlatBtn();
            btnInstall.Text = "安装并应用";
            btnInstall.Kind = BtnKind.Primary;
            btnInstall.Width = Theme.S(200);
            btnInstall.Click += delegate { DoInstallCurrent(); };
            btnUninstall = new FlatBtn();
            btnUninstall.Text = "卸载补丁";
            btnUninstall.Width = Theme.S(140);
            btnUninstall.Click += delegate { DoUninstallCurrent(); };
            sPick.Buttons(btnInstall, btnUninstall);
            AddDetail(p, sPick, 0);

            // ---------- 游戏信息（概览 · 2026-10-22）----------
            //  以前概览页在"无需安装"的游戏上只有一张适配方案卡，底下全是灰底还能翻 ——
            //  空翻的观感比内容少更廉价。这里放游戏自身的事实（都是库/扫描里现成的）：
            //  平台 / 入口程序 / 安装目录 / 累计时长 · 最近游玩。纯展示，无操作。
            var sGi = new Sec("游戏信息");
            lblGi = sGi.Body("");
            lblGi.Font = Theme.F(9.5f);
            lblGi.ForeColor = Theme.TextDim;
            //  每游戏「启动参数」的**可见入口**（2026-10-31）：对话框第 148 轮就做好了，
            //  但入口只埋在右键菜单里 —— 用户直接问"填参数的地方在哪"
            //  （现实需求：鸣潮 3.7 起直启 exe 必须带 -krqlv=uhd 这类画质档参数）。
            //  状态行 + 编辑按钮放进「游戏信息」卡，选中游戏就能看到；保存后经 RefreshFgSummary 回显。
            //  与右键菜单同一闸（!ShareBuild）：分享构建没有编辑入口，就别摆一行"未设置"干瞪眼。
            if (!Program.ShareBuild)
            {
                lblGiArgs = sGi.Body("");
                lblGiArgs.Font = Theme.F(9.5f);
                lblGiArgs.ForeColor = Theme.TextDim;
                btnLaunchCfg = new FlatBtn();
                btnLaunchCfg.Text = "启动参数与环境变量…";
                btnLaunchCfg.Width = Theme.S(150);
                btnLaunchCfg.Click += delegate { ShowLaunchConfigDialog(); };
                sGi.Buttons(btnLaunchCfg);
            }
            //  自定义封面的**显眼入口**（2026-10-23）：功能一直在，但只在卡片右键菜单里，
            //  用户直接问"能不能添加自定义封面" —— 说明藏得太深。选中游戏后这里就能看到：
            //  选一张本地图片，或按 Ctrl+V 直接把剪贴板里的截图贴成封面。
            if (!Program.ShareBuild)
            {
                btnWebCover = new FlatBtn();
                btnWebCover.Text = "🌐 联网搜封面";
                btnWebCover.Width = Theme.S(116);
                btnWebCover.Click += delegate { DoWebCover(); };

                btnSetCover = new FlatBtn();
                btnSetCover.Text = "📁 本地选图…";
                btnSetCover.Width = Theme.S(106);
                btnSetCover.Click += delegate { DoSetCover(); };

                btnPasteCover = new FlatBtn();
                btnPasteCover.Text = "📋 粘贴图片";
                btnPasteCover.Width = Theme.S(100);
                btnPasteCover.Click += delegate { DoPasteCover(); };

                btnClearCover = new FlatBtn();
                btnClearCover.Text = "🗑 移除封面";
                btnClearCover.Width = Theme.S(96);
                btnClearCover.Click += delegate { DoClearCover(); };

                sGi.Buttons(btnWebCover, btnSetCover, btnPasteCover, btnClearCover);
            }
            AddDetail(p, sGi, 0);

            // ---------- 推荐依据（诊断页签）----------
            //  以前这段是概览上的 150 字正文 —— 用户原话「废话还是多」。
            //  它仍然有用（为什么不是别的方案、刷新率甜点、前置条件），但不能占着第一屏：
            //  挪到「诊断」页签顶上，想深究的人点一下就在。
            var sWhy = new Sec("为什么是这套");
            lblWhy = sWhy.Body("检查中…");
            lblRecNote = sWhy.Body("");
            lblRecNote.Font = Theme.F(9.5f);
            lblRecNote.ForeColor = Theme.TextDim;
            AddDetail(p, sWhy, 2);

            // ---------- 运行时状态与操作（一张卡 · 诊断页签）----------
            //  以前是两张卡：「运行时状态」（下载 / 清理）和「帧生成操作」（开启 / 关闭 / 换入口 / 诊断…）。
            //  合并成一张，并且整张挪进「诊断」—— 概览页只需要"装 / 卸"两个动作。
            //  删掉「目标游戏」下拉：左栏游戏列表一直可见，hero 也写着当前游戏名，第三处重复没意义。
            var s2 = new Sec("运行时状态与操作");
            //  4 行（2026-11-01）：前 3 行是"状态"，第 2 行是普通人看得懂的解释 ——
            //  用户原话"参数说明太极客化了，很多游戏玩家并不了解"。每项按「结论 + 人话注解」成对出现，
            //  注解用括号、与结论同色系，不额外加字号层级。
            lblRt = s2.BodyFixed("检查中…", 3);
            //  DLSS 运行库版本（只读体检，2026-10-20）：DLSS Swapper 那类工具的核心价值就是
            //  "让我看见每个游戏的库版本" —— 补丁覆盖回来时界面上完全看不出来。这里额外跟包内版本比一版。
            lblDlssLib = s2.Body("");
            lblDlssLib.Font = Theme.F(9.5f);
            lblDlssLib.ForeColor = Theme.TextDim;
            btnDownload = new FlatBtn(); btnDownload.Text = "下载运行时"; btnDownload.Width = Theme.S(112);
            btnDownload.Click += delegate { DoDownload(); };
            var btnResidue = new FlatBtn(); btnResidue.Text = "清理残留"; btnResidue.Width = Theme.S(96);
            btnResidue.Click += delegate { DoCleanResidue(); };
            s2.Buttons(btnDownload, btnResidue);
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
            lblPatrol = s2.BodyFixed("入口巡检：检查中…", 1);
            AddDetail(p, s2, 2);

            // ---------- 方案区块（5 套只显示一套）----------
            //  选择器已在上方「此游戏的适配方案」，这里只负责把选中的那一套铺出来。
            //  「自动」按当前游戏的推荐结果显示，选具体项则只看那一套。

            //  方案 A 原来单独一块（状态 + 两个按钮），现在状态与动作都在上面的面板里，
            //  再留一块就是同一件事说两遍 —— 整块删掉（OptiNote 的说明也已并进面板的提示行）。
            s030Sec = new Sec("DLSS MFG " + Dlssg030.Ver + " · 真 DLSS 多帧生成（代理模式）");
            var s030 = s030Sec;
            lbl030 = s030.BodyFixed("检查中…", 1);
            //  这一行"它是什么"删了：区块标题已经写着 DLSS MFG · 真 DLSS 多帧生成，
            //  倍率在「帧生成参数」里也能看到 —— 操作区只留状态与控件。
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
            // 入口折叠成一行（标签 + 下拉）：以前"说明行 + 整行铺满"占两行，只为说"不加载就换一个"。
            //  ⚠ 不能再让下拉整行铺满 —— Row 的标签列起手，宽度给固定值即可（老注释里的"被盖住只剩尾巴"
            //  是标签列被长标题挤占造成的，这里标签只有「DLL 入口」四个字，不复现）。
            s030.Row("DLL 入口", cbEntry, Theme.S(220));
            var btn030Pre = new FlatBtn(); btn030Pre.Text = "前置条件检查"; btn030Pre.Width = Theme.S(116);
            btn030Pre.Click += delegate { Do030Precheck(); };
            btnAsi = new FlatBtn(); btnAsi.Text = "ASI 入口（异环这类）"; btnAsi.Width = Theme.S(140);
            btnAsi.Click += delegate { DoAsiDeploy(); };
            var btn030Check = new FlatBtn(); btn030Check.Text = "检查是否生效（读日志）"; btn030Check.Width = Theme.S(176);
            btn030Check.Click += delegate { DoFgLogCheck(); };
            // 两行：一行放不下会被 FlowLayoutPanel 直接裁掉（它不换行）
            s030.Buttons(btn030Pre);
            s030.Buttons(btnAsi, btn030Check);
            AddDetail(p, s030, 1);

            // ---------- 0.4.x：作者换了发布结构（代理缩到 0.8 MB + dlssg_sm86\ 资源目录） ----------
            //  这一块与 0.3.x 并列：两者的资源包、判据、部署清单都不是一回事，
            //  合并只会让用户看不出"游戏里跑的到底是哪一代"。
            s040Sec = new Sec("DLSS MFG " + Dlssg040.Ver + " · 新版内核包（0.4.x，与上一代互斥）");
            var s040 = s040Sec;
            lbl040 = s040.BodyFixed("检查中…", 4);
            cbEntry040 = new RoundCombo();
            cbEntry040.Items.AddRange(Dlssg040.Entries);
            int ei4 = 0;
            for (int i = 0; i < Dlssg040.Entries.Length; i++)
                if (Dlssg040.Entries[i].Equals(Cfg.DlssgEntry040, StringComparison.OrdinalIgnoreCase)) { ei4 = i; break; }
            cbEntry040.SelectedIndex = ei4;
            cbEntry040.SelectedIndexChanged += delegate
            {
                if (cbEntry040.SelectedIndex < 0 || cbEntry040.SelectedIndex >= Dlssg040.Entries.Length) return;
                Cfg.DlssgEntry040 = Dlssg040.Entries[cbEntry040.SelectedIndex];
                Program.SetConfigStr("dlssg", "entry040", Cfg.DlssgEntry040);
                Log("0.4.x 的部署入口已设为 " + Cfg.DlssgEntry040);
                RefreshFgSummary();
            };
            s040.Row("DLL 入口", cbEntry040, Theme.S(220));

            // 0.4.x 的四个常用参数：出厂值 = 2 / Auto / DLSS / Off（与作者 ini 一致）。
            //  只改对应那一行，其余键原样 —— 用户不必去手改 6.8 KB 的 INI。
            s040.Body("参数（重启游戏生效）：");
            cbOpt040 = new RoundCombo();
            cbOpt040.DropDownStyle = ComboBoxStyle.DropDownList;
            cbOpt040.Items.AddRange(new object[] {
                "0 · 原厂内核（最保守）", "1 · 逐位一致（与官方同画面）",
                "2 · 默认（加有损图像内核）", "3 · 最快（全部有损）" });
            cbOpt040.SelectedIndex = Math.Max(0, Math.Min(3, Cfg.DlssgOptimized040));
            cbOpt040.SelectedIndexChanged += delegate { Save040Tuning(); };
            s040.Row("一致性档位 Optimized", cbOpt040, Theme.S(220));

            cbPreset040 = new RoundCombo();
            cbPreset040.DropDownStyle = ComboBoxStyle.DropDownList;
            cbPreset040.Items.AddRange(new object[] {
                "A · 关闭 UI 重组（默认）", "B · 开启 UI 重组（多数游戏无效）",
                "C · FG Lite（每生成帧快 29~34%）" });
            cbPreset040.SelectedIndex = PresetIndex(Cfg.DlssgPreset040);
            cbPreset040.SelectedIndexChanged += delegate { Save040Tuning(); };
            s040.Row("渲染预设 Preset", cbPreset040, Theme.S(220));

            cbEngine040 = new RoundCombo();
            cbEngine040.DropDownStyle = ComboBoxStyle.DropDownList;
            cbEngine040.Items.AddRange(new object[] { "DLSS · 官方模型（默认）", "XeSS · Intel 模型（支持 2X~4X，1440p 更快）" });
            cbEngine040.SelectedIndex = string.Equals(Cfg.DlssgEngine040, "XeSS", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            cbEngine040.SelectedIndexChanged += delegate { Save040Tuning(); };
            s040.Row("插帧引擎 Engine", cbEngine040, Theme.S(220));

            cbSr040 = new RoundCombo();
            cbSr040.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSr040.Items.AddRange(new object[] { "Off · 不介入超分（默认）", "M-INT8 · 走 INT8 超分内核（仅 20/30 系）" });
            cbSr040.SelectedIndex = Cfg.DlssgSr040 ? 1 : 0;
            cbSr040.SelectedIndexChanged += delegate { Save040Tuning(); };
            s040.Row("超分 INT8 SuperResolution", cbSr040, Theme.S(220));

            btnUpRelease = new FlatBtn(); btnUpRelease.Text = "从 GitHub 取新版"; btnUpRelease.Width = Theme.S(140);
            btnUpRelease.Click += delegate { DoUpstreamFetch(); };
            var btn040Src = new FlatBtn(); btn040Src.Text = "下载源 / 资源包"; btn040Src.Width = Theme.S(140);
            btn040Src.Click += delegate { DoUpstreamSettings(); };
            var btn040Purge = new FlatBtn(); btn040Purge.Text = "彻底清理"; btn040Purge.Kind = BtnKind.Danger; btn040Purge.Width = Theme.S(104);
            btn040Purge.Click += delegate { Do040Purge(); };
            s040.Buttons(btnUpRelease, btn040Src, btn040Purge);
            var btn040Apply = new FlatBtn(); btn040Apply.Text = "应用参数到已装游戏"; btn040Apply.Width = Theme.S(160);
            btn040Apply.Click += delegate { Do040Tuning(); };
            var btn040Check = new FlatBtn(); btn040Check.Text = "检查是否生效（读日志）"; btn040Check.Width = Theme.S(176);
            btn040Check.Click += delegate { DoFgLogCheck(); };
            var btnNetDisk = new FlatBtn(); btnNetDisk.Text = "网盘下载源"; btnNetDisk.Width = Theme.S(150);
            btnNetDisk.Click += delegate { DoNetDiskSources(); };
            //  「导入资源包…」（2026-11-01）：拖拽这条路对**管理员窗口**走不通 —— Windows 的 UIPI
            //  会把普通权限资源管理器发来的拖放**静默丢弃**（实测两次都没进程序，日志里连"拖入"都没有）。
            //  所以给一条不依赖拖拽的路：文件选择对话框（程序自己弹的，没有 UIPI 问题）。
            var btnPackImport = new FlatBtn(); btnPackImport.Text = "导入资源包…"; btnPackImport.Width = Theme.S(120);
            btnPackImport.Click += delegate
            {
                //  2026-11-01 推倒自造列表：直接用产品现成的文件夹浏览器（改数据目录/扫描/导出四处
                //  都在用、本机实测可用），浏览到包所在的文件夹 → 里面的 zip 自动列出：
                //  只有 1 个直接导；多个弹小列表挑；0 个明确说"没有 zip"。
                string folder = ModernFolderPicker.PickFolder(this.Handle,
                    "选择插件包所在的文件夹（里面的 zip 会被列出）",
                    lastPackDir.Length > 0 ? lastPackDir : "");
                if (folder == null || folder.Length == 0) return;   // 取消
                lastPackDir = folder;
                var zips = new List<string>();
                try { zips.AddRange(Directory.GetFiles(folder, "*.zip")); }
                catch (Exception ex) { Log("  读不了这个文件夹：" + ex.Message); return; }
                zips.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                if (zips.Count == 0)
                {
                    //  没有 zip ≠ 导不了：用户选的可能是**解压后的散装包目录**（extracted\ 那种，
                    //  version.dll + dlssg_sm86\ 平铺）—— 识别器本来就吃这种目录。
                    //  直接交给识别器：认得出就正常导入，认不出会明确说"不在导入范围"。
                    Log("  文件夹里没有 zip，按散装包目录识别：" + folder);
                    StartPackImport(new List<string> { folder });
                    return;
                }
                if (zips.Count == 1) { StartPackImport(new List<string> { zips[0] }); return; }
                ShowPackZipList(zips, folder);                      // 多个包 → 列表挑一个
            };
            s040.Buttons(btn040Apply, btn040Check, btnPackImport, btnNetDisk);
            AddDetail(p, s040, 1);

            // ---------- 方案 B · MFG Bridge（第三方，单文件 nvngx.dll）----------
            //  功能面上比 0.4.0 更全（Home 键覆盖倍率、三引擎可切、自带 NR 与光流开关），
            //  但仓库**不含源码、只发二进制** —— 所以定位是"知情后可选"，不是默认推荐。
            sNvSec = new Sec("方案 B · MFG Bridge（第三方单文件 · 可选）");
            var sNv = sNvSec;
            lblNv = sNv.BodyFixed("检查中…", 2);
            sNv.Body("第三方单文件 nvngx.dll；⚠ 发布件无数字签名，与其它方案互斥。");
            cbEntryNv = new RoundCombo();
            cbEntryNv.Items.AddRange(MfgBridge.Entries);
            int eiN = 0;
            for (int i = 0; i < MfgBridge.Entries.Length; i++)
                if (MfgBridge.Entries[i].Equals(Cfg.DlssgEntryNvbr, StringComparison.OrdinalIgnoreCase)) { eiN = i; break; }
            cbEntryNv.SelectedIndex = eiN;
            cbEntryNv.SelectedIndexChanged += delegate
            {
                if (cbEntryNv.SelectedIndex < 0 || cbEntryNv.SelectedIndex >= MfgBridge.Entries.Length) return;
                Cfg.DlssgEntryNvbr = MfgBridge.Entries[cbEntryNv.SelectedIndex];
                Program.SetConfigStr("dlssg", "entryNvbr", Cfg.DlssgEntryNvbr);
                Log("方案 B 的部署入口已设为 " + Cfg.DlssgEntryNvbr);
                RefreshFgSummary();
            };
            sNv.Row("DLL 入口", cbEntryNv, Theme.S(220));
            btnNvFetch = new FlatBtn(); btnNvFetch.Text = "从 GitHub 取新版"; btnNvFetch.Width = Theme.S(140);
            btnNvFetch.Click += delegate { DoNvFetch(); };
            sNv.Buttons(btnNvFetch);
            AddDetail(p, sNv, 1);

            // ---------- Smooth Motion · 驱动级插帧（覆盖 DX11 / Vulkan）----------
            //  这是唯一能覆盖"游戏本身没有 DLSS"的那类（DX11 老游戏、独立游戏）的路子：
            //  不往游戏目录放任何文件，只写 NVIDIA 驱动配置里的 per-game 开关。
            sSmSec = new Sec("Smooth Motion · 驱动级插帧（DX11 / DX12 / Vulkan）");
            var sSm = sSmSec;
            lblSm = sSm.BodyFixed("检查中…", 2);
            sSm.Body("不要求游戏支持 DLSS —— 20/30 系靠它才能给 DX11 老游戏补帧。");
            AddDetail(p, sSm, 1);

            //  顺序：选方案 → 方案区块 → 伪装 → 状态与操作 → 参数。
            //  伪装卡原来夹在方案之前，滚一屏先看到"改显卡名"，却看不到"该装哪套"。
            // ---------- ② 显卡名伪装（内置：以前要导出 .reg 再双击导入） ----------
            var sSpoof = new Sec("显卡名伪装（网游的帧生成选项靠它出现）");
            lblSpoof = sSpoof.BodyFixed("检查中…", 2);
            // ⚠ 2026-09-16 实测现象（别把「重启显卡设备」当成等效手段）：
            //   21:26:03 写入伪装 → 21:27:06 重启显卡设备 → 21:27:44 启动绝区零 →
            //   21:28:10 游戏日志读到的仍是 "NVIDIA GeForce RTX 3060 Ti"。
            //   而更早那次「导入 .reg + 重启电脑」之后，游戏日志确实读到了 "RTX 5090"（03:46 / 10:50 两条）。
            //   结论：以「重启电脑」为准；「重启显卡设备」只算辅助（部件重新枚举可能把值写回去）。
            //  这条结论已写进状态行（见 RefreshFgSummary 的 lblSpoof），不再单占一行说明。
            btnSpoofOn = new FlatBtn(); btnSpoofOn.Text = "开启伪装（改为 RTX 5090）";
            btnSpoofOn.Kind = BtnKind.Success; btnSpoofOn.Width = Theme.S(186);
            btnSpoofOn.Click += delegate { DoSpoof(true); };
            btnSpoofOff = new FlatBtn(); btnSpoofOff.Text = "还原真实显卡名"; btnSpoofOff.Width = Theme.S(136);
            btnSpoofOff.Click += delegate { DoSpoof(false); };
            btnSpoofDev = new FlatBtn(); btnSpoofDev.Text = "重启显卡设备"; btnSpoofDev.Width = Theme.S(120);
            btnSpoofDev.Click += delegate { DoSpoofRestartDevice(); };
            sSpoof.Buttons(btnSpoofOn, btnSpoofOff, btnSpoofDev);
            AddDetail(p, sSpoof, 1);

            ApplySchemeVis();      // 初始：按「自动」显示当前游戏推荐的那一套

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
            //  NR 的位置（2026-10-20）：OptiScaler.ini 的 [DlssNr] RunBeforeSR。
            //  键名与默认值都取自包内那份 ini 的注释，不是我猜的。
            cbNrPos = new RoundCombo();
            cbNrPos.Items.AddRange(new object[] { "超分之后（默认）", "超分之前 · Pre-SR" });
            cbNrPos.SelectedIndex = Cfg.DlssgNrPreSR ? 1 : 0;
            s3.Pair("架构 (Router)", cbRouter, Theme.S(140), "最大生成帧", cbMaxFrames, Theme.S(140));
            s3.Pair("内核类型", cbKernel, Theme.S(140), "日志级别", cbLogLevel, Theme.S(140));
            s3.Row("光流精度", cbBilinear, Theme.S(140));
            s3.Pair("神经渲染 NR", cbNr, Theme.S(140), "NR 位置", cbNrPos, Theme.S(190));
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
            //  推荐依据（刷新率甜点 / NR 警告 / 前置条件）已经搬到「诊断」页签的「为什么是这套」，
            //  参数页只留"改哪个值"本身 —— 一页只讲一件事。
            //  一行讲完两件事：上限要新运行库、实际倍率游戏说了算。资源包版本号不用再复读
            //  ——「运行时状态」区块就是它的权威来源，这里再拼一遍 PackCaption 只会把行撑爆。
            s3.BodyFixed("※ 5X/6X 要 310.9 运行库；上限只是天花板，实际倍率由游戏菜单决定（多数只给 4X）。", 1);
            btnFgApply = new FlatBtn(); btnFgApply.Text = "应用参数"; btnFgApply.Kind = BtnKind.Primary; btnFgApply.Width = Theme.S(100);
            btnFgApply.Click += delegate { DoApplyFgCfg(); };
            s3.Buttons(btnFgApply);
            AddDetail(p, s3, 1);
            BuildLibView(p);
            ShowLibView(selected == null);   // 初始：没选中游戏就先看库全貌
        }

        // ---------------------- 库视图（未选中游戏时铺满右区） ----------------------
        //  Steam / 小黑盒的库主页：一排筛选 + 封面墙。搜索框只在左栏 —— 一个搜索词
        //  同时过滤列表和这里，避免两处输入框各说各话。
        //  第 124 轮：库视图 = **封面网格**（Steam / Xbox 那种排法）
        //  ---------------------------------------------------------------------------
        //  第 123 轮把它改成「列表 + 右侧检查器」，是照着竞品抄的 —— 用户一句"纯抄袭"就否了。
        //  回到封面，但不退回第 122 轮那张 152×228 的小卡（他说"廉价"）：
        //    · 190×285（2:3），1040 宽下 4 列，1040 高下 2 行；
        //    · 卡面只留两行字，方案做成**可点的胶囊**（点它就地换，不离开墙）；
        //    · 点卡片 = 进详情（Steam 的行为），右下 ▶ = 直接启动。
        void BuildLibView(Pg p)
        {
            lstSearch = new RoundField(Theme.S(190), "搜索游戏…");
            lstSearch.SetCorner(Theme.Bg);        // 圆角框的"自己的底"必须是所在容器色，否则露白角
            lstSearch.Width = Theme.S(190);
            lstSearch.Margin = Padding.Empty;
            lstSearch.Box.TextChanged += delegate { SearchDebounce(); };

            cbSort = new RoundCombo();
            cbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            cbSort.Items.AddRange(new object[] { "最近游玩", "名称", "平台" });
            cbSort.SelectedIndex = 0;
            cbSort.Width = Theme.S(110);
            cbSort.SelectedIndexChanged += delegate
            {
                lastOrderValid = false;   // 用户主动换排序 → 作废"保持原顺序"，按新规则重排
                FillGameList();
            };

            //  「显示已隐藏」不进工具条（低频开关，占 96px 会把统计挤成省略号）——
            //  挂左栏「···」菜单，控件本身只当状态载体。
            chkShowHidden = new RoundCheck();
            Theme.StyleCheck(chkShowHidden, "显示已隐藏");
            chkShowHidden.CheckedChanged += delegate { FillGameList(); };

            lblLibStat = new TxtLbl();
            lblLibStat.AutoSize = false;
            lblLibStat.AutoEllipsis = true;
            lblLibStat.Font = Theme.F(9f);
            lblLibStat.ForeColor = Theme.TextDim;
            lblLibStat.BackColor = Theme.Bg;
            lblLibStat.TextAlign = ContentAlignment.MiddleLeft;
            //  Label 的 MiddleLeft 与自己绘制的控件在 28px 里落基线差 1px，用 1px 上内边距补平
            lblLibStat.Padding = new Padding(0, Theme.S(1), 0, 0);
            lblLibStat.Text = "尚未扫描 —— 点左栏标题旁的「···」";

            //  分类胶囊取代了原来那个"12 款 · 2 款可接入"的统计文字。
            //  数量本身挪进胶囊里（"全部 12"），少一行文字、多一个能点的入口。
            chips = new FilterChips();
            chips.Changed += delegate
            {
                lastOrderValid = false;
                FillGameList();
            };

            //  ★ 第 158 轮：Large Title 撤掉。第 130 轮加它是因为"没有'我在这页的哪儿'的锚点"；
            //    第 152 轮起页名常显在标题栏，这里再画一遍就是一屏两个"游戏库"
            //    （其余五页的主区标题第 151 轮已清，库页当时是独立构建漏了）。
            //    这一行只留**实时统计**（"12 款" / "5 / 12 款" / "已选 鸣潮 · 12 款"），
            //    右对齐到工具条的搜索框/排序正上方，凑成一条右缘线。
            var titleBar = new Panel();
            titleBar.BackColor = Theme.Bg;
            lblLibStat.BackColor = Theme.Bg;
            lblLibStat.Font = Theme.F(10.5f);
            lblLibStat.TextAlign = ContentAlignment.MiddleRight;
            titleBar.Controls.Add(lblLibStat);
            titleBar.Resize += delegate
            {
                lblLibStat.SetBounds(Math.Max(Theme.S(200), titleBar.Width - Theme.S(300)), 0,
                                     Theme.S(300) - Theme.S(8), Theme.S(26));
            };
            //  ★ 第 142 轮：大标题和工具条**并进同一个分区**。
            //  原来它们是两块，Pg 给的分区间距一视同仁 —— 于是「标题↔工具条」和
            //  「工具条↔内容」的视觉间距是 37 和 33，差 4px：读不成"标题带工具条"这一组，
            //  整页从顶到底都是同一个呼吸频率，看着就"平"。
            //  同一组内的间距交给 Sec.Space（4），组与组之间仍是 SecGap —— 分组靠间距差表达。
            var s0 = new Sec("");
            s0.Block(titleBar, Theme.S(26));
            s0.Space(Theme.S(4));                        // 标题与工具条贴成一组
            var btnFetchAll = new FlatBtn();
            btnFetchAll.Text = "⚡ 补全封面";
            btnFetchAll.Width = Theme.S(88);
            btnFetchAll.Click += delegate { DoFetchAllCovers(); };
            s0.Bar2(chips, lstSearch, cbSort, btnFetchAll);
            AddLib(p, s0);

            //  ★ 库页主视觉（第 132 轮）：当前游戏的宽幅大图，压在工具条下面。
            //  之前一屏里全是 162px 的小卡，第一眼没有「主角」—— 沉浸感缺的就是这个。
            libHero = new LibHero();
            libHero.Launch += delegate(DlssgGame g) { SelectGame(g, false); DoLaunchGame(); };
            libHero.Details += delegate(DlssgGame g) { SelectGame(g, true); };
            heroSec = new Sec("");
            heroSec.Block(libHero, LibHero.HeightFor);   // 高度随内容区宽度自适应
            heroSec.Visible = false;                 // 有游戏可显示时才出现
            AddLib(p, heroSec);

            var s2 = new Sec("");
            var grid = new CoverGrid();
            grid.CellW = Theme.S(174);                   // 卡片 162 + 左右各 6（内容区 702 宽下 4 列）
            grid.CellH = Theme.S(255);                   // 卡片 243 + 上下各 6
            grid.Tag = s2;                               // 高度变化时通知所属分区重排
            grid.ScanRequested += delegate { RunScan(true); };   // 空态主按钮 = 页头那颗的同款入口
            libGrid = grid;
            s2.Block(grid, Theme.S(255));
            AddLib(p, s2);

            //  底部操作条：浮在页面之上（不参与 Pg 的滚动），选中游戏才出现。
            //  为什么是"浮层"而不是页面里的一段：墙面滚到哪儿它都在手边，且不占墙的高度。
            //  Dock=Bottom：它占真实布局，页面自己让位 —— 不是浮层，所以不会被当成弹窗。
            //  Visible=false 时 Dock 控件不占高度，其他页面完全不受影响。
            actBar = new LibActionBar();
            actBar.Dock = DockStyle.Bottom;
            actBar.Height = Theme.S(52);
            actBar.Visible = false;
            actBar.Install += delegate(DlssgGame g) { SelectGame(g, false); DoInstallCurrent(); FillGameList(); };
            actBar.Launch += delegate(DlssgGame g) { SelectGame(g, false); DoLaunchGame(); };
            actBar.Details += delegate(DlssgGame g) { SelectGame(g, true); };
            actBar.PickScheme += delegate(DlssgGame g)
            {
                ShowSchemeMenuAt(g, actBar, new Point(Theme.S(150), Theme.S(10)));
            };
            content.Controls.Add(actBar);
        }

        //  显示/收起底部状态栏。
        //  它 Dock=Bottom，显示时页面自己缩 60px —— 不需要任何"让位"补丁（第一版是浮层，
        //  得靠给网格加底部内边距来避免压住最后一行，那是浮层带来的额外复杂度）。
        void ShowActionBar(bool on)
        {
            if (actBar == null) return;
            if (actBar.Visible == on) return;
            actBar.Visible = on;
        }

        //  卡片宽自适应（2026-10-22）：**只改控件的 Size**，不重建卡片。
        //  为什么不再走"重建"：重建要 `Controls.Clear()` + 14 张封面按新尺寸重新解码，
        //  一次 ~700ms；窗口一拖就反复触发，stress 探针直接卡死（返回 143）。
        //  现在封面位图本来就是 2 倍烘焙，绘制时 DrawImage 会缩放到当前尺寸 —— 改 Size 就够，
        //  零解码、零闪烁、即时生效。
        void AddLib(Pg p, Sec s) { libSecs.Add(s); p.Add(s); }
        //  tab：0 概览 · 1 参数 · 2 诊断 · -1 任何页签都显示（标签栏自己）
        void AddDetail(Pg p, Sec s, int tab)
        {
            detailSecs.Add(s);
            if (s != null) secTab[s] = tab;
            p.Add(s);
        }

        //  详情页的可见性 = 「不在库视图」∧「属于当前页签」∧「这套方案当前该显示」。
        //  第三条由 ApplySchemeVis 通过 secWant 决定（5 套方案只显示一套）。
        void ApplyDetailVis()
        {
            for (int i = 0; i < detailSecs.Count; i++)
            {
                Sec s = detailSecs[i];
                if (s == null) continue;
                int t;
                if (!secTab.TryGetValue(s, out t)) t = 0;
                bool on = !libViewOn && (t < 0 || t == curDetailTab);
                bool want;
                if (on && secWant.TryGetValue(s, out want)) on = want;
                if (s.Visible != on) s.Visible = on;
            }
        }

        //  true = 工作台（列表 + 检查器）；false = 该游戏的完整详情页（Pg + Sec）。
        //  第 123 轮起这两者是**同级**的两种呈现，不再由"有没有选中游戏"决定 ——
        //  列表里点一行只更新选中与检查器，视图不动（这正是参考项目最核心的一条）。
        //  封面墙的键盘走位（2026-10-25 第 140 轮）
        //  ---------------------------------------------------------------------------
        //  库页是待得最久的地方，但方向键一直没有任何作用 —— 卡在"能看的封面墙"和
        //  "能操作的工具"之间。这里补上：方向键在格子里走、Enter 启动、Esc 取消选中。
        //  走的是**墙面实际顺序**（libShown），不是 games 的原始顺序。
        void LibMoveFocus(int dx, int dy)
        {
            if (libShown == null || libShown.Count == 0) return;
            int cols = 1;
            if (libGrid != null && libGrid.CellW > 0)
                cols = Math.Max(1, libGrid.ClientSize.Width / libGrid.CellW);
            int cur = -1;
            for (int i = 0; i < libShown.Count; i++)
                if (libShown[i] == selected) { cur = i; break; }
            int want;
            if (cur < 0) want = 0;
            else
            {
                int row = cur / cols, col = cur % cols;
                want = (row + dy) * cols + (col + dx);
                //  ⚠ 左右走到行首/行尾就**停住**，不要绕到相邻行 ——
                //  在网格里"绕行"会让光标突然跳到不相邻的位置，手感很乱。
                if (dy == 0 && want / cols != row) return;
            }
            if (want < 0) want = 0;
            if (want >= libShown.Count) want = libShown.Count - 1;
            if (want == cur) return;
            SelectGame(libShown[want], false);
        }

        void ShowLibView(bool on)
        {
            libViewOn = on;
            if (on && page == P_FG) SetCrumb(PageNames[P_FG], P_FG);
            for (int i = 0; i < libSecs.Count; i++)
            {
                if (libSecs[i] == null) continue;
                //  「继续游玩」只在有数据时才算库视图的一部分 —— 否则详情页里会冒出一条空的横带
                libSecs[i].Visible = on && (libSecs[i] != heroSec || heroHasData);
            }
            ApplyDetailVis();
            if (headFg != null) headFg.Visible = !on;
            if (tabsFg != null) tabsFg.Enabled = !on;
            //  操作条只在"墙 + 有选中"时出现；进详情页时收起（详情页自己就是完整参数）
            if (actBar != null) ShowActionBar(on && selected != null && page == P_FG);
            if (pages != null && P_FG < pages.Length && pages[P_FG] != null)
            {
                pages[P_FG].SetScrollY(0);              // 换视图回到顶部，别停在上一个视图的滚动位置
                pages[P_FG].Reflow();
            }
        }

        // ---------------------- 页 6：说明（长文都挪到这一页） ----------------------
        void BuildPageHelp()
        {
            var p = pages[P_HELP];

            if (Program.ShareBuild)
            {
                // 分享版说明页：只讲三件事，句子尽量短（用户要求：别高深、别字多）
                var sShare = new Sec("这个软件能做什么");
                sShare.Body("· RTX 20/30 系也能开「帧生成」"
                + "\n· 自动找游戏，一键启动（可带 DX12 参数）"
                + "\n· 启动前告诉你是 DX11 还是 DX12"
                + "\n· 有没有生效，看「运行诊断」");
                p.Add(sShare);

                var sPlug = new Sec("插件怎么放 / 怎么更新");
                sPlug.Body("① 新版压缩包拖进窗口即可（自动识别版本并归位）"
                + "\n② 手动放：便携版与 Fluxion.exe 同目录，安装版在 C:\\ProgramData\\Fluxion"
                + "\n③ 出新版就覆盖同名文件：dlssg\\source\\ 通用 · dlssg030-pack\\ 0.3.x · xess-pack\\zzz\\ 绝区零"
                + "\n④ 换完回「概览」点「安装并应用」，要确认生效再点「运行诊断」");
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
                s2.Body("① 左侧点游戏 → 「概览」里确认方案 → 点「安装并应用」→ 退出游戏再进"
                      + "\n② 游戏里出现「DLSS 帧生成」= 成功；只有 FSR = 入口没被调用，换入口重试"
                      + "\n③ 回工具点「运行诊断」，日志里有 evaluate 才算真的在补帧");
                p.Add(s2);
            }

            if (!Program.ShareBuild)
            {
                //  手动资源（2026-11-01）：网盘里那两个"不是帧生成插件、需要自己动手放"的包。
                //  数据目录里各放一份原样文件，说明页讲清"在哪、干嘛、怎么用"。
                //  刻意不做自动部署：RE 框架必须与游戏版本匹配，超分模型替换也要用户自己确认。
                var sManual = new Sec("手动资源（不是插件，按需自己拿）");
                sManual.Body("· DLSS 超分模型 4 / 4.5 —— 游戏画面发糊、有拖影时用"
                    + "\n  在 " + Path.Combine(Program.DataDir, @"manual-pack\dlss4-4.5\nvngx_dlss.dll")
                    + "\n  用法：复制到游戏 exe 同目录覆盖同名文件（先备份原文件）"
                    + "\n· RE 框架 —— 只给卡普空的游戏（生化、鬼武者、龙之信条2、怪猎）"
                    + "\n  在 " + Path.Combine(Program.DataDir, @"manual-pack\re-framework\dinput8.dll")
                    + "\n  用法：复制到游戏 exe 同目录（它按 dinput8.dll 这个名字加载）"
                    + "\n⚠ 两个都要和游戏版本匹配，工具不代装；放错了游戏起不来，删掉那两份文件即可恢复");
                p.Add(sManual);
            }

            if (!Program.ShareBuild)
            {
                var s3 = new Sec("使用须知（实测结论）");
                s3.Body("· 先开 HAGS（设置 → 系统 → 显示 → 图形 → 硬件加速 GPU 调度）"
                        + "\n· 基础帧 <40 会变糊、延迟升高"
                        + "\n· 竞技射击不建议开"
                        + "\n· 倍率不吃显存（只随输出分辨率变）"
                        + "\n· 游戏更新会覆盖代理，更新后重新开启"
                        + "\n· 生效判据：诊断日志里出现 jsonl"
                        + "\n· 反作弊游戏用 d3d12 入口，常规入口会按文件名拦掉（报错 11008），封号自负"
                        + "\n· 被反作弊改名隔离的文件，用「清理残留」回收");
                p.Add(s3);
            }

            if (!Program.ShareBuild)
            {
                var s4 = new Sec("反作弊与入口选择");
                s4.Body("绝区零（HoYoKProtect）/ 鸣潮（腾讯 ACE）按文件名拦 version.dll、winmm.dll（报错 11008），"
                      + "所以自动改用 d3d12.dll（DX12 必加载，拦不掉）。\n只解决「装得进去」，封号风险仍是你的。");
                p.Add(s4);
            }

            // ---------- 方案详解（从「帧生成」页搬过来的长文案，放这里不挡视线）----------
            if (!Program.ShareBuild)
            {
                var sPlan = new Sec("帧生成方案详解（怎么选 / 前置条件 / 6X 边界）");
                sPlan.Body("【方案 A · OptiScaler】绝区零 / 鸣潮专用"
                         + "\n· 绝区零：d3d12.dll 入口 + 伪装 RTX 5090，帧生成走 DLSSG→XeFG"
                         + "\n· 鸣潮：dxgi.dll 入口，原生 DLSSG + DLSS 5 NR"
                         + "\n· 游戏内 F10 呼出面板；官方启动器报文件缺失就直接双击游戏 exe"
                         + "\n· 与 0.3.x 互斥（抢同一入口），切换只改名停放，不删文件"
                         + "\n· 例外：绝区零 3.0+ DX12 自带 FSR 帧生成，有就直接用，不必注入");
                sPlan.Body("【0.3.x · DLSSG for SM86】任意游戏可用"
                         + "\n· 代理内嵌原厂 DLSS-G，只拦 nvngx_dlssg.dll，跑真 DLSS 帧生成（2X-6X）"
                         + "\n· 入口：绝区零 d3d12 / 鸣潮 dxgi / 通用 version（不加载就换 winmm / dbghelp / dinput8 / dxgi / d3d12）"
                         + "\n· 适用：游戏自己请求 DLSS 帧生成即可；单机开箱即用，网游常需先伪装 RTX 5090"
                         + "\n· 版本 " + Dlssg030.Ver + "：0.3.5 修重建特性后的花屏/崩溃，0.3.4 修超分误吃架构改写"
                         + "\n· 可升级路径：0.3.2 或 0.3.5+，跳过 0.3.3 / 0.3.4"
                         + "\n· 前置：驱动 ≥591.86；要 DLSS 5 NR 就停在 616.56；6X 需游戏侧 Streamline 支持"
                         + "\n· 6X：MaxGeneratedFrames=5；旧版代理只认 1..3，工具会夹到 3 并提示升级");
                var sPlan4 = new Sec("【0.4.x · DLSSG for SM86】新包结构版（2026-10-18 加）");
                sPlan4.Body("【0.4.x】新包结构"
                         + "\n· 部署三样：入口 DLL + dlssg_sm86.ini + 整个 dlssg_sm86\\ 目录（代理只 0.8 MB，只拷 DLL 无效）"
                         + "\n· 出厂开关：超分 INT8（30/20 系）、FG Lite（3070 每帧快 29~34%）、XeSS 插帧（支持 2X~4X）"
                         + "\n· INI 重排为 [DLSSG] / [XeSSFG]，旧键逐条告警，工具留 .bak 再重写"
                         + "\n· 从 0.3.x 升级先删干净；6X 仍要自己写 MaxGeneratedFrames=5"
                         + "\n· 版本以界面显示为准（体积判据这一代失效）");
                p.Add(sPlan4);

                var sPlan5 = new Sec("【下载源】从 GitHub 直接取新版（2026-10-18 加）");
                sPlan5.Body("「从 GitHub 取新版」做了什么"
                         + "\n· 列最近 5 个 release（tag + 大小）"
                         + "\n· 0.3.5 及更早只有源码，会标注出来"
                         + "\n· 下载走「拖包进窗口」同一套识别与签名核验"
                         + "\n· 归位后问是否重抄到已装游戏"
                         + "\n· 匿名限流 60 次/小时；国内建议在设置页开代理"
                         + "\n· 仓库地址可在「下载源」按钮里改");
                p.Add(sPlan5);
                p.Add(sPlan);
            }

            // 分享版没有封面（列表是信息行），这一节直接不出现
            if (!Program.ShareBuild)
            {
                var s5 = new Sec("封面库");
                s5.Body("顺序：covers.json → Steam 本地缓存 → Steam 官方竖版 → 生成卡。"
                      + "\n自放：图片命名「卡片标题.png / .jpg」放进：" + CoverArt.CoversDir
                      + "\n联网补齐：库视图点「获取封面」，只走 Steam 域名与设置页代理。");
                p.Add(s5);
            }

            var s6 = new Sec("数据与隐私");
            s6.Body("只写本机 " + Program.DataDir
                  + "\n  · games.json —— 忽略清单 / 手动添加"
                  + "\n  · library.json —— 收藏 / 私密 / 隐藏 / 改名 / appid"
                  + (Program.ShareBuild ? "" : "\n  · covers\\ —— 封面缓存")
                  + "\n「私密」只在「私密」分类出现；「隐藏」要勾「显示已隐藏」。");
            p.Add(s6);
        }

        // ---------------------- 页 5：实时监控 ----------------------
        void BuildPageMonitor()
        {
            var p = pages[P_MON];

            var s1 = new Sec("实时曲线（最近 90 个采样）");
            var tlp = new TableLayoutPanel();
            Buffered(tlp);
            tlp.ColumnCount = 5; tlp.RowCount = 1;
            tlp.BackColor = Theme.Panel;
            tlp.Margin = Padding.Empty;
            tlp.Padding = Padding.Empty;
            for (int i = 0; i < 5; i++) tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlp.Height = Theme.S(120);

            spCpu = new Spark(); spCpu.Caption = "CPU %"; spCpu.LineColor = Theme.Accent;
            spGpu = new Spark(); spGpu.Caption = "GPU %"; spGpu.LineColor = Theme.Ok;
            spMem = new Spark(); spMem.Caption = "内存 %"; spMem.LineColor = Theme.Cyan;
            spGpuTemp = new Spark(); spGpuTemp.Caption = "GPU 温度"; spGpuTemp.LineColor = Theme.Warn; spGpuTemp.Unit = "°C";
            spCpuTemp = new Spark(); spCpuTemp.Caption = "CPU 温度"; spCpuTemp.LineColor = Theme.Err; spCpuTemp.Unit = "°C";
            spCpu.Dock = DockStyle.Fill; spCpu.Margin = new Padding(0, 0, Theme.S(10), 0);
            spGpu.Dock = DockStyle.Fill; spGpu.Margin = new Padding(0, 0, Theme.S(10), 0);
            spMem.Dock = DockStyle.Fill; spMem.Margin = new Padding(0, 0, Theme.S(10), 0);
            spGpuTemp.Dock = DockStyle.Fill; spGpuTemp.Margin = new Padding(0, 0, Theme.S(10), 0);
            spCpuTemp.Dock = DockStyle.Fill; spCpuTemp.Margin = Padding.Empty;
            tlp.Controls.Add(spCpu, 0, 0);
            tlp.Controls.Add(spGpu, 1, 0);
            tlp.Controls.Add(spMem, 2, 0);
            tlp.Controls.Add(spGpuTemp, 3, 0);
            tlp.Controls.Add(spCpuTemp, 4, 0);
            s1.Block(tlp, Theme.S(120));
            p.Add(s1);

            var s2 = new Sec("运行日志（与数据目录 logs\\ 同步）");
            logSec = s2;                        // 高度自适应要用（见 ResizeLogBox）
            //  自绘日志视图（见 LogView）：替换掉"有文本却不显示"的 RichTextBox，
            //  顺带去掉它那条原生竖滚动条。行数上限 500，内存不随运行时长增长。
            log = new LogView();
            log.BackColor = Theme.Subtle;
            log.TextColor = Theme.Text;
            log.MaxLines = 500;
            var logBox = new Panel();
            this.logBox = logBox;               // 高度自适应要用
            logBox.BackColor = Theme.Subtle;
            logBox.BorderStyle = BorderStyle.None;
            logBox.Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(8), Theme.S(6));
            log.Dock = DockStyle.Fill;
            logBox.Controls.Add(log);
            log.Text = "";
            logLines = 0;
            //  空态：一块 200px 高、只有左上角一行小灰字的空白，读起来就是"这里坏掉了"。
            //  改成居中一句提示；有没有内容按**控件里真的有字**判断，不靠 logVirgin ——
            //  换主题重建时 logVirgin 已经是 false，但新控件是空的。
            lblLogEmpty = new TxtLbl();
            lblLogEmpty.Dock = DockStyle.Fill;
            lblLogEmpty.BackColor = Theme.Subtle;
            lblLogEmpty.ForeColor = Theme.TextDim;
            lblLogEmpty.Font = Theme.F(9.5f);
            lblLogEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblLogEmpty.Text = "还没有日志 —— 启动一次游戏，这里会实时滚动";
            logBox.Controls.Add(lblLogEmpty);
            lblLogEmpty.BringToFront();
            //  换主题会重建控件树：log 是新的空控件，而 lastLogLen 还停在上次读到的位置，
            //  不补一次历史就会一直空到下一次有新日志（用户看到的就是"日志没了"）。
            if (!logVirgin) ReplayLogTail();
            //  ★ 空态靠**互斥显示**，不靠 z-order（2026-10-20）：原来 log 和 lblLogEmpty 都是
            //    Dock=Fill，叠在一起时谁在上面取决于添加顺序 + BringToFront 的时序 ——
            //    实测这版渲染出来是"日志卡整块空白"，连提示都没有。空的时候把 RTB 藏掉，
            //    提示自然是唯一可见的那个；来第一条日志时 AppendLog 会把 RTB 放回来。
            lblLogEmpty.Visible = log.LineCount == 0;
            log.Visible = !lblLogEmpty.Visible;
            //  高度自适应（第 147 轮）：原来固定 200px，只来 2 行日志时下面空 300px，
            //  看着像"日志卡没写完"。现在按行数分档（1-3 / 4-8 / 9-16 / >16），
            //  档内不重排，跨档才 Resync —— 日志流式追加时不会每行都触发整页重排。
            s2.Block(logBox, LogBoxHeight(0));
            ResizeLogBox();
            p.Add(s2);
        }

        // ---------------------- 页 7：设置 ----------------------
        void BuildPageSettings()
        {
            var p = pages[P_SET];

            var s0 = new Sec("界面主题");
            cbTheme = new RoundCombo();
            cbTheme.Items.AddRange(new object[] { "浅色", "深色 · 曜石黑 + 电竞极光绿（Obsidian Cyber）" });
            cbTheme.SelectedIndex = (Cfg != null && Cfg.UiTheme == "dark") ? 1 : 0;
            cbTheme.SelectedIndexChanged += delegate {
                BeginInvoke(new Action(delegate {
                    DoSwitchTheme(cbTheme.SelectedIndex == 1);
                }));
            };
            s0.Row("界面主题", cbTheme, Theme.S(260));
            s0.Body("切换后立即生效，不用重启。");

            // 界面动效三档。说明文案里的系统状态是**现算的** —— 否则用户看到"什么都没变"
            // 只会以为是我们没做，而不是系统把动画关了。依据见 docs/motion-and-ui-research.md。
            cbMotion = new RoundCombo();
            cbMotion.Items.AddRange(new object[] { "跟随系统", "始终开启", "关闭" });
            cbMotion.SelectedIndex = Motion.Mode == "on" ? 1 : (Motion.Mode == "off" ? 2 : 0);
            cbMotion.SelectedIndexChanged += delegate { DoSwitchMotion(cbMotion.SelectedIndex); };
            s0.Row("界面动效", cbMotion, Theme.S(260));
            s0.Body(Motion.OsAllows
                ? "系统动画已开启 · 「跟随系统」即启用全部过渡（单段最长 300ms）。"
                : "⚠ 系统动画已关闭 · 「跟随系统」= 无过渡。想看效果就选「始终开启」。");
            p.Add(s0);

            //  ---- 更新（第 160 轮）----
            //  0926 竞品定案推荐顺序第 2，一直挂着"没有版本源"——仓库本身就在 GitHub，
            //  Releases API 就是版本源（复用 Upstream 的代理/直连两路通道）。
            var sUpd = new Sec("更新");
            lblUpd = sUpd.Body("当前版本 " + Program.AppVersion
                + (Cfg != null && Cfg.UpdateCheckAt.Length > 0 ? " · 上次检查：" + Cfg.UpdateCheckAt : " · 还没检查过"));
            btnUpdCheck = new FlatBtn(); btnUpdCheck.Text = "检查更新"; btnUpdCheck.Width = Theme.S(96);
            btnUpdCheck.Click += delegate { BeginUpdateCheck(true); };
            btnUpdOpen = new FlatBtn(); btnUpdOpen.Text = "打开发布页"; btnUpdOpen.Width = Theme.S(110);
            btnUpdOpen.Visible = false;
            btnUpdOpen.Click += delegate
            {
                if (updUrl.Length == 0) { Log("  还没有查到新版本的发布页"); return; }
                try { System.Diagnostics.Process.Start(updUrl); }
                catch (Exception ex) { Log("  打开发布页失败：" + ex.Message + "\n  地址：" + updUrl); }
            };
            sUpd.Buttons(btnUpdCheck, btnUpdOpen);
            chkUpdAuto = new RoundCheck();
            Theme.StyleCheck(chkUpdAuto, "启动时自动检查（静默，间隔 24 小时，有新版才提示）");
            chkUpdAuto.Checked = Cfg == null || Cfg.UpdateCheck;
            chkUpdAuto.CheckedChanged += delegate
            {
                Cfg.UpdateCheck = chkUpdAuto.Checked;
                bool w = Program.SetConfigBool("ui", "updateCheck", chkUpdAuto.Checked);
                if (!w) Log("⚠ 开关没能写进配置（config.json 不可写）—— 本次运行内生效");
            };
            sUpd.Pair(null, chkUpdAuto, 0, null, null, 0);
            sUpd.Body("只查 GitHub Releases 的最新 tag，不上传任何本机信息；检查不顺畅时可在下方「下载源」里开代理。");
            p.Add(sUpd);

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
                //  两列行（这是设置页唯一一行双列表单）：宽度按列补满，右边不要留一段空档。
                //  原来 180 / 130 —— 第一列只到表格 1/3 处，第二个标签孤零零飘在中间，像被截断了。
                s1.Pair("游戏", cbProfileGame, Theme.S(290), "档位", cbProfile, Theme.S(250));

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

                s1.Body("办公 = 不触发联动；游戏 = 按档位联动（fps / mmo / aaa / gacha）。只有 fps 档会暂停远控。");

                // ---- 游戏分辨率档位（v3.8.0）----
                // 为什么要有它：CS2 / 无畏契约的分辨率原来是**写死在代码里的一条**，用户换显示器
                //   （换尺寸 / 换分辨率 / 换刷新率）之后没有第二个选项，只能去手改 config.json。
                //   这里列的是"这两个游戏的玩家实际会选的分辨率"（职业选手统计数据，见 Core.cs 档位表注释），
                //   并按本机主屏**实际支持的模式**标注可用性 —— 换屏后一眼能看出哪一档得先建自定义分辨率。
                // ---- 分辨率修改方式（2026-10-06）----
                // 用户要求：除了选哪一档，还要能选「自动修改还是手动修改」。
                // 自动修改 = 原行为：从库点「启动」先预应用、进程检测兜底，退出自动还原。
                // 手动修改 = 程序完全不碰显示状态（不切档位、不禁屏），档位只作记录，分辨率自己改；
                //   游戏进行中已生效的那轮联动不受影响，退出时照常还原（LinkGameExit 看的是 linkActive，不是这个开关）。
                //   远控/电源/加速包等其它联动也与之无关，不受影响。
                cbResMode = new RoundCombo();
                cbResMode.Items.AddRange(new object[] { "自动修改", "手动修改" });
                cbResMode.SelectedIndex = Cfg.ResLinkEnable ? 0 : 1;   // 先回显再挂事件，构造期不触发
                cbResMode.SelectedIndexChanged += delegate
                {
                    if (resComboSyncing) return;
                    bool auto = cbResMode.SelectedIndex == 0;
                    Cfg.ResLinkEnable = auto;
                    SaveCfgBool("displayLink", "enable", auto);
                    Log("[分辨率联动] 修改方式 → " + (auto
                        ? "自动修改：进游戏自动切档位/禁屏，退出自动还原"
                        : "手动修改：程序不再动显示状态，档位只作记录，分辨率自己在游戏内/系统设置里改"
                          + "（当前这局如已联动，退出时仍会自动还原）"));
                    RefreshResInfo("");
                };
                s1.Row("分辨率修改", cbResMode, Theme.S(110));

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
                s1.Row("CS2 游戏时禁用", cbOffCs, Theme.S(240));   // 240：值文字 10f 后"不禁用（所有显示器保持启用）"在 214 下出省略号

                cbOffVal = new RoundCombo();
                cbOffVal.Items.AddRange(OffMonItems(ResKeyVal));
                cbOffVal.SelectedIndexChanged += delegate { OnResOffMonPicked(ResKeyVal, cbOffVal); };
                s1.Row("无畏契约游戏时禁用", cbOffVal, Theme.S(240));

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
            s1b.Body("点 X 的行为；托盘「退出」永远直接退出，不受这里影响。");
            p.Add(s1b);

            var s2 = new Sec(Program.ShareBuild ? "下载源" : "下载源与联网封面");
            if (Program.ShareBuild)
                s2.Body("下载代理设置：帧生成运行时 / 插件包从这里下载时走它（直连失败会自动回退到代理）。失败原因写进下方日志。");
            else
            s2.Body("封面：Steam CDN → 按名搜 appid → Bing 兜底，逐级回退。挑错了右键「设置封面…」。");
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
            sd.Body("当前：" + Program.DataDir + "\n迁移 = 复制 → 校验 → 删源；建议先退出游戏。");
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
            sj.Body("回收 = 送回收站（可还原）；每组最新一份永远保留。逐项清理点「备份明细」。");
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
            s3.Body("数据目录：" + Program.DataDir + "（卸载不删）\n"
                  + "写入前都有备份；代理只往游戏目录放 1~2 个文件，删掉即还原。");
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
        //  表格单元格悬停：文字被列宽**真的截断**时，停 350ms 显示完整内容。
        //  为什么必须有它："期望值"里有 370~415px 的解释性长句，任何列宽都放不下 ——
        //  截断是必然的，但用户得能读到全文，不能只能猜（用户反馈"每个列表内容"看不全）。
        //  只对"确实超宽"的格子弹，其余不打扰。
        static void AttachCellTips(ListView lv)
        {
            var hover = new System.Windows.Forms.Timer { Interval = 350 };
            ListViewItem.ListViewSubItem cur = null;
            Point last = Point.Empty;
            hover.Tick += delegate
            {
                hover.Stop();
                if (cur == null || lv.IsDisposed) return;
                Theme.Tip.ShowAt(last, cur.Text);
            };
            lv.MouseMove += delegate(object s2, MouseEventArgs e)
            {
                ListViewHitTestInfo info = lv.HitTest(e.Location);
                if (info.Item == null || info.SubItem == null || info.SubItem.Text.Length == 0)
                {
                    hover.Stop(); Theme.Tip.HideNow(); cur = null; return;
                }
                int idx = info.Item.SubItems.IndexOf(info.SubItem);   // ListViewSubItem 没有 Index
                if (idx < 0 || idx >= lv.Columns.Count) { hover.Stop(); Theme.Tip.HideNow(); cur = null; return; }
                int avail = lv.Columns[idx].Width - Theme.S(14);
                int need = TextRenderer.MeasureText(info.SubItem.Text, Theme.F(9.5f),
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (need <= avail) { hover.Stop(); Theme.Tip.HideNow(); cur = null; return; }
                if (info.SubItem == cur) return;
                cur = info.SubItem;
                last = Cursor.Position;
                hover.Stop(); hover.Start();
            };
            lv.MouseLeave += delegate { hover.Stop(); Theme.Tip.HideNow(); cur = null; };
            lv.Disposed += delegate { try { hover.Dispose(); } catch { } };
        }

        //  ListView → 它的表格容器（表头由 TableBox 自绘，见该类注释）
        static readonly Dictionary<ListView, TableBox> tableBoxes = new Dictionary<ListView, TableBox>();
        public static TableBox BoxOf(ListView lv)
        {
            TableBox b = null;
            tableBoxes.TryGetValue(lv, out b);
            return b;
        }

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
            //  行高（2026-10-20）：ListView 没有 RowHeight 属性，默认行高只有 20px ——
            //  9.5pt 的字顶天立地，五十行读起来像 2005 年的资源管理器，这是数据表"廉价感"
            //  最直接的来源。用一张 1×28 的透明占位图撑高行距（ListView 的既定手法），
            //  行间一旦有呼吸，同一张表立刻"贵"了一个档。
            var rowPad = new ImageList();
            rowPad.ImageSize = new Size(1, Theme.S(28));
            rowPad.ColorDepth = ColorDepth.Depth32Bit;
            lv.SmallImageList = rowPad;
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

            //  ★ 第 142 轮：列宽跟着容器宽度按**比例**走。
            //  列宽是建表时写死的，卡片一窄（MaxSecW 限宽、窗口缩小）最后一列就被挤到可视区外，
            //  或反过来留下一条空白 —— 表现为「期望值」列被省略成"双通道 + 标称频率（…"。
            //  按**当前**列宽等比缩放（不是按默认值重算），用户自己拖过的相对比例因此保留。
            //  ⚠ 只有总和与客户区差超过 8px 才动：改完两者就相等，下一次 Resize 会立刻返回，
            //    不会和用户拖动列宽打架。
            bool fitting = false;
            lv.Resize += delegate
            {
                if (fitting || lv.IsDisposed || lv.Columns.Count == 0) return;
                int avail2 = lv.ClientSize.Width;
                if (avail2 <= Theme.S(120)) return;
                //  有竖向滚动条时客户区已经扣掉了，不用自己再减
                int cur = 0;
                foreach (ColumnHeader ch in lv.Columns) cur += ch.Width;
                if (Math.Abs(cur - avail2) <= Theme.S(8)) return;
                if (cur <= 0) return;
                fitting = true;
                try
                {
                    int acc = 0, n = lv.Columns.Count;
                    for (int i = 0; i < n; i++)
                    {
                        int ow = lv.Columns[i].Width;
                        int w = (i == n - 1) ? avail2 - acc
                                              : (int)((long)avail2 * ow / cur);
                        if (w < Theme.S(56)) w = Theme.S(56);
                        if (i == n - 1) w = Math.Max(Theme.S(56), avail2 - acc);
                        lv.Columns[i].Width = w;
                        acc += w;
                    }
                }
                catch { }
                finally { fitting = false; }
            };

            // 列宽复原入口（v3.9.0）：原生 ListView 本来就支持拖列宽、也支持双击分隔线按内容自适应，
            //   但**拖乱之后没有任何退路** —— 尤其是把某列拖到只剩十几像素时，靠自己拖回来很别扭。
            // 为什么**只**给「恢复默认列宽」、不再给"全部按内容自适应"：
            //   实测本表三列内容宽合计 833 > 客户区 773（「当前值」单列就要 514）——「按内容撑开」
            //   必然冒出横向滚动条，而横条会把客户区高度压掉一行，用户视角就是"表格少了一行"。
            //   用户自己也说了「这个栏还能自动调节」—— 逐列自适应（双击分隔线）本来就在，不重复提供。
            var cmW = MenuFactory.New();
            var lvRef = lv;
            var miResetW = new ToolStripMenuItem("恢复默认列宽");
            miResetW.Click += delegate { Theme.ResetListWidths(lvRef); };
            cmW.Items.Add(miResetW);
            lv.ContextMenuStrip = cmW;

            lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                //  ⚠ 实测（2026-10-20）：本项目的 ListView **从不触发**这个事件（打了日志、一次都没进），
                //    所以表头一直是系统那条浅灰带（#F0F0F0 + #A0A0A0 下边线）。
                //    试过：去掉 SetWindowTheme(hh,…) 也没用 ⇒ 不是主题压掉了自绘，是 WinForms 这条路径
                //    在本控件上不通。要改表头外观只能自己画（HeaderStyle=None + 首行假表头）。
                var g = e.Graphics;

                // 表头不再铺那条钛灰带（2026-10-20）：一条灰带横在圆角白卡里，
                // 是整张页面上最"原生控件贴上来"的一块。改成与卡片同底 + 一条 1px 下划线，
                // 表格读起来就是"写在卡片里的一张表"，而不是"嵌了一个控件"。
                // （仍要填一次：不填会留下上一次绘制的残影，滚动时尤其明显。）
                g.FillRectangle(Theme.Solid(Theme.Panel), e.Bounds);

                // 底部分隔线
                g.DrawLine(Theme.Hair(Theme.Line), e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
                else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                else flags |= TextFormatFlags.Left;

                Rectangle tr = new Rectangle(e.Bounds.Left + Theme.S(10), e.Bounds.Top, Math.Max(0, e.Bounds.Width - Theme.S(20)), e.Bounds.Height);
                Theme.DrawTxt(g, e.Header.Text, Theme.FM(8.5f), tr, Theme.TextDim, flags);
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
                //  ★ 铺满**客户区整宽**，不是只铺 e.Bounds（2026-10-20）：列宽合计永远比客户区
                //    窄几像素（留余量防横向滚动条），那一小条空区由 ListView 自己按主题色刷 ——
                //    在白卡里就是一条压在右边的灰带（用户截图「像拼接的一块」）。
                var rowBg = new Rectangle(0, e.Bounds.Top, Math.Max(e.Bounds.Width, lv.ClientSize.Width), e.Bounds.Height);
                g.FillRectangle(Theme.Solid(isSel ? Theme.Hover : Theme.Panel), rowBg);
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
                    //  状态格改成「小圆点 + 文字」，**不再画实心胶囊**（2026-10-20）。
                    //  一屏二十行全是绿底胶囊 —— 那是"仪表盘模板"的观感，也是"廉价"最直接的来源：
                    //  二十个填充块把一张表变成了二十张贴纸。去掉底色与描边之后，同一列仍然
                    //  一眼能扫出状态（点带色、字在前），但视觉重量只剩原来的五分之一。
                    //  ⚠ 文字颜色：正常态用正文色（8.5pt 的 #10B981 在白底上对比度不够，读起来发虚），
                    //  只有异常态（未生效 / 需处理）才把文字也染成状态色 —— 安静是默认，出问题才响。
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Color c = (e.SubItem != null && e.SubItem.ForeColor != Color.Empty) ? e.SubItem.ForeColor : Theme.Text;
                    Color txtC = (c == Theme.Ok) ? Theme.Text : Theme.TextSafe(c);
                    int dotR = Theme.S(6);
                    int bx = e.Bounds.Left + Theme.S(6);
                    int dotY = e.Bounds.Top + (e.Bounds.Height - dotR) / 2;

                    // 微型发光圆点指示灯：外圈微光扩散 + 内芯实心 + 核心微高亮
                    int haloSz = dotR + Theme.S(4);
                    using (var hb = new SolidBrush(Color.FromArgb(Theme.Dark ? 45 : 30, c)))
                        g.FillEllipse(hb, bx - Theme.S(2), dotY - Theme.S(2), haloSz, haloSz);
                    g.FillEllipse(Theme.Solid(c), bx, dotY, dotR, dotR);
                    using (var cb = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                        g.FillEllipse(cb, bx + Theme.S(1), dotY + Theme.S(1), Theme.S(2), Theme.S(2));

                    int tx = bx + dotR + Theme.S(6);
                    Rectangle tr = new Rectangle(tx, e.Bounds.Top, Math.Max(0, e.Bounds.Right - Theme.S(6) - tx), e.Bounds.Height);
                    Theme.DrawTxt(g, txt, Theme.FM(9f), tr, txtC,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    g.SmoothingMode = SmoothingMode.Default;
                }
                else
                {
                    // 普通单元格文字排版
                    Color tc = isSel ? Theme.AccentText : (e.SubItem != null && e.SubItem.ForeColor != lv.ForeColor && e.SubItem.ForeColor != Color.Empty ? e.SubItem.ForeColor : Theme.Text);
                    //  左内缩 10 → 4（2026-10-20 像素审计）：表头是**原生控件**画的（自绘事件在本项目
                    //  从不触发，见上），它的左侧内缩只有 ~6px；单元格若按 10px 排，表头文字就比
                    //  单元格文字左 8px —— 整张表每一列都歪。4px + 字形左侧留白正好贴上表头。
                    Rectangle tr = new Rectangle(e.Bounds.Left + Theme.S(4), e.Bounds.Top, Math.Max(0, e.Bounds.Width - Theme.S(10)), e.Bounds.Height);
                    Theme.DrawTxt(g, txt, Theme.F(9.5f), tr, tc,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            };

            FillLastColumn(lv, Theme.S(140));
            //  宽度变化时重新分配：建表那一刻算一次是不够的 —— 缩到窗口最小尺寸（860）时
            //  末列会被挤出可视区（只剩横向滚动条）。见 Theme.FitColumns 的注释。
            //  ⚠ 必须**延迟一帧**再算：Resize / VisibleChanged 触发时父容器这一轮布局还没落定，
            //    当场读 ClientSize 拿到的是上一版宽度（实测钩子当场跑，列宽合计停在 513 不动）。
            EventHandler refitNow = delegate { Theme.FitColumns(lv, Theme.S(140), Theme.S(96)); };
            EventHandler refitLater = delegate
            {
                try
                {
                    if (lv.IsHandleCreated && !lv.IsDisposed) lv.BeginInvoke(refitNow);
                }
                catch { }
            };
            lv.Resize += refitLater;
            lv.VisibleChanged += delegate { if (lv.Visible) refitLater(lv, EventArgs.Empty); };
            //  包进自绘表头的容器：表头返回我们自己画（原生表头改不动，见 TableBox 注释）
            AttachCellTips(lv);
            //  滚轮转发：整表展开后 ListView 自己没有滚动范围，而它是**原生控件** ——
            //  会吃掉 WM_MOUSEWHEEL 又不往上传（和 FlatScroll 那条注释是同一个坑），
            //  结果就是"鼠标停在表格上滚半天，页面一动不动"。这里显式交给所属页面。
            lv.MouseWheel += delegate(object sw, MouseEventArgs ew)
            {
                Pg pgw = PgOf(lv);
                if (pgw != null) pgw.ScrollByWheel(ew.Delta);
            };
            var box = new TableBox(lv);
            tableBoxes[lv] = box;
            box.Width = Theme.S(600);
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
        //  显示签名：主屏设备名 + 分辨率 + 当前刷新率。
        //  够用且便宜（不枚举模式）；换屏 / 改分辨率 / 改刷新率都会变，别的情况不用重算。
        static string DispSignature()
        {
            try
            {
                Screen s = Screen.PrimaryScreen;
                return s.DeviceName + "|" + s.Bounds.Width + "x" + s.Bounds.Height + "|" + Program.RefreshHz();
            }
            catch { return ""; }
        }

        void RefreshResCombos()
        {
            if (cbResCs == null || cbResCs.IsDisposed) return;
            //  重算一次要 InvalidateModes() + 最多 512 次 EnumDisplaySettings（实测 38 ms）。
            //  而 Switch(P_SET) 每次进设置页都调它 —— 显示器根本没变时这一趟纯属白跑。
            //  下拉为空（刚建页 / 刚换主题重建）时必须算，否则会是空列表。
            string sig = DispSignature();
            if (cbResCs.Items.Count > 0 && sig.Length > 0 && sig == resDispSig) return;
            resDispSig = sig;
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
            string primDev = ResLink.PrimaryDevice() ?? "主显示器";
            DispGuard.Mode? curMode = DispGuard.Current(primDev);
            string curModeStr = curMode.HasValue ? curMode.Value.W + "×" + curMode.Value.H + "@" + curMode.Value.Hz + "Hz" : "探测中";
            string monHeader = "• 本机主屏物理硬件：" + primDev.Replace("\\\\.\\", "") + "（当前运行：" + curModeStr + "，最大支持 " + mw + "×" + mh + "）";
            string cur = monHeader + "\n当前设置：CS2 " + ResDescr(ResKeyCs) + "　无畏契约 " + ResDescr(ResKeyVal)
                       + "　还原目标 " + Cfg.BaseW + "×" + Cfg.BaseH + "@" + Cfg.BaseHz + "Hz"
                       + "　修改方式 " + (Cfg.ResLinkEnable ? "自动" : "手动");
            string off = "游戏时禁用显示器：CS2 " + OffMonDescr(ResKeyCs) + "　无畏契约 " + OffMonDescr(ResKeyVal);
            string legend = "可用性按本机主屏（" + (mw > 0 ? "最大 " + mw + "×" + mh : "探测不到，一律按可用处理")
                          + "）：✓ 直接可切 · △ 本机没有这一档，需先在显卡驱动里建自定义分辨率 · ✕ 超出屏幕。"
                          + "\n「分辨率修改」**默认手动**（2026-10-18 起关闭自动）：自动 = 进游戏按档位切（含禁屏）、退出还原；"
                          + "手动 = 程序完全不碰显示状态，档位与禁屏只作记录，分辨率自己在游戏内 / 系统显示设置里改。"
                          + "\n刷新率自动跟随你当前主屏，换 240Hz 屏不用改这里。"
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
        // 2026-10-21：原来这里是一句 MessageBox「将执行一键优化：电源 / GPU / 调度 / 网络 / 内存 / 服务 / 输入等」。
        //   它列的是**模块名**，不是**代价**。点"是"的那一刻，用户并不知道其中两步会把
        //   WSL2 / Docker / 安卓模拟器 / Windows 沙盒一起关掉 —— 这是这类工具最容易丢信任的地方。
        //   现在改成按风险分组的勾选清单：每一项都写清影响什么，用户可以只勾自己要的那部分。
        //   默认仍然全勾（分级只是把代价摆到明面上，不是替用户减配）。
        // 构造「一键优化」的勾选清单。
        //   ★ 抽成 static 是为了能被探针离屏抓图验证 —— ShowDialog 会阻塞，抓不到画面。
        //     改动这里请顺手跑 tools/run_optimize_dlg_probe.py（它按像素量"三组是不是都画出来了"）。
        //
        //   ★ 展示顺序按风险排（稳妥 → 谨慎 → 实验），**执行顺序仍按 Steps() 原始顺序** ——
        //     Steps() 的顺序有语义（先电源后调度，后面 powercfg 才不会报"设置不存在"），
        //     所以这里只排显示、不动执行。map 就是用来把"用户勾的框"还原回原始步骤的。
        internal static Form BuildOptimizeDialog(List<OptStep> steps,
            out Dictionary<OptStep, RoundCheck> map, out List<RoundCombo> knobViews)
        {
            var localMap = new Dictionary<OptStep, RoundCheck>();
            map = localMap;
            knobViews = new List<RoundCombo>();

            // 档位表只构造一次：每一步都去调 Program.Knobs() 会重复算 20 遍
            var knobMap = new Dictionary<string, OptKnob>();
            foreach (var k in Program.Knobs()) knobMap[k.Key] = k;

            var dlg = new Form();
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
            dlg.Text = "一键优化（选择要执行的项）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(620), Theme.S(440));
            dlg.ClientSize = new Size(Theme.S(700), Theme.S(720));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;

            // 按档位稳定排序（不稳的话同档内的执行顺序会变，日志对不上）
            var shown = new List<OptStep>(steps);
            var order = new Dictionary<OptStep, int>();
            for (int i = 0; i < steps.Count; i++) order[steps[i]] = i;
            shown.Sort(delegate(OptStep a, OptStep b)
            {
                int d = a.Level.CompareTo(b.Level);
                return d != 0 ? d : order[a].CompareTo(order[b]);
            });

            // 顶部使用建议与快捷预设
            var sTip = new Sec("💡 优化推荐与预设选择");
            var lbTip = sTip.Body("使用建议：若平时需要使用「安卓模拟器」、「WSL2」、「Docker」或「Windows 沙盒」，保持默认推荐配置（排除底部的 2 项「实验档」，不关闭虚拟化）即可，兼顾极致低输入延迟与开发/模拟器兼容性。");
            lbTip.ForeColor = Theme.TextSafe(Theme.Ok);

            var btnRec = new FlatBtn(); btnRec.Text = "推荐配置（日常/开发/模拟器兼顾）"; btnRec.Kind = BtnKind.Primary; btnRec.Width = Theme.S(230);
            var btnAll = new FlatBtn(); btnAll.Text = "全选（极致电竞）"; btnAll.Width = Theme.S(130);
            btnRec.Click += delegate
            {
                foreach (var pair in localMap) pair.Value.Checked = (pair.Key.Level != RiskLevel.Experimental);
            };
            btnAll.Click += delegate
            {
                foreach (var pair in localMap) pair.Value.Checked = true;
            };
            sTip.Buttons(btnRec, btnAll);
            pg.Add(sTip);

            int lastLevel = -1;
            Sec sec = null;
            foreach (var s in shown)
            {
                if (s.Level != lastLevel)
                {
                    sec = new Sec(RiskLevel.Name(s.Level) + "档（" + RiskLevel.Blurb(s.Level) + "）");
                    pg.Add(sec);
                    lastLevel = s.Level;
                }
                var rc = new RoundCheck();
                rc.Text = s.Name;
                rc.Checked = (s.Level != RiskLevel.Experimental); // 默认排除实验档，兼顾日常/模拟器/开发安全
                rc.Height = Theme.S(22);
                sec.Row("", rc, Theme.S(560));
                if (!string.IsNullOrEmpty(s.Impact))
                {
                    var lb = sec.Body(s.Impact);
                    // 实验档的代价用错误色标出来：绿色主题下这一行必须一眼能认出来
                    if (s.Level == RiskLevel.Experimental) lb.ForeColor = Theme.TextSafe(Theme.Err);
                }

                // ---- 档位（2026-10-21）----
                // 有可选档位的步骤，就地给下拉框 —— 用户不必"勾上 → 记下名字 → 再去取值细调里找"。
                // 这里改的是 config（保存时落盘 + 落系统），和「取值细调」同一套键，两处改一处生效。
                if (s.Knobs != null && s.Knobs.Length > 0)
                {
                    foreach (var key in s.Knobs)
                    {
                        OptKnob kn;
                        if (!knobMap.TryGetValue(key, out kn)) continue;

                        var cb = new RoundCombo();
                        cb.Tag = kn;
                        cb.Width = Theme.S(380);
                        for (int i = 0; i < kn.Labels.Length; i++) cb.Items.Add(kn.Labels[i]);
                        if (kn.Index >= 0) cb.SelectedIndex = kn.Index;
                        else
                        {
                            // 当前值不在档位表里（别处写过工具不提供的值）：必须预选最后那条
                            // "不在档位表中"，否则用户只是打开对话框再点开始，就会被静默改成第 0 档。
                            cb.Items.Add("（当前值 " + kn.Value + "，不在档位表中 —— 不动它）");
                            cb.SelectedIndex = kn.Labels.Length;
                        }
                        bool dead = kn.HybridOnly && !Program.HybridCpu;
                        if (dead) cb.Enabled = false;

                        string label = string.IsNullOrEmpty(kn.Short) ? kn.Title : kn.Short;
                        if (kn.Restart) label += "（重启生效）";
                        sec.Row(label, cb, Theme.S(380));
                        if (dead) sec.Body("本机不是混合架构 CPU（没有 P/E 核），这一档没有对应设置 —— 保持默认即可。");
                        knobViews.Add(cb);
                    }
                }

                localMap[s] = rc;
            }

            var sFoot = new Sec("安全保障说明");
            sFoot.Body("· 默认推荐配置已自动排除关闭虚拟化与服务的「实验档」，兼顾极致游戏帧率与日常开发/模拟器兼容性。\n"
                     + "· 所有系统修改在执行前均会自动快照备份，随时可在主界面点击「恢复备份」一键还原。\n"
                     + "· 若手动勾选了「实验档」，系统将在执行前自动创建 Windows 系统还原点作为双重保障。");
            pg.Add(sFoot);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnGo = new FlatBtn(); btnGo.Text = "开始优化"; btnGo.Kind = BtnKind.Primary; btnGo.Width = Theme.S(104);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnGo.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnGo);
            dlg.Controls.Add(bar);
            dlg.AcceptButton = btnGo;
            dlg.CancelButton = btnCancel;

            pg.Reflow();
            // ⚠ 必须补这一手：Show 之前 Pg 还没被 Dock 算过尺寸，ClientSize 是 0 —— 上面那次
            //   Reflow 只能用兜底宽度排一版，实际显示时会缩回压缩态（探针实测：所有分区 Top=0、
            //   高度一律 72px，内容全叠在左上角）。Shown 之后宽度才是真的，得再排一次。
            dlg.Shown += delegate { try { pg.Reflow(); } catch { } };
            return dlg;
        }

        void DoOptimize()
        {
            var steps = Program.Steps();
            if (steps.Count == 0) return;

            Dictionary<OptStep, RoundCheck> map;
            List<RoundCombo> knobViews;
            Form dlg = BuildOptimizeDialog(steps, out map, out knobViews);

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // ---- ① 先落档位 ----
            // 顺序不能反：档位决定下面 ApplyAll 往系统里写什么值（选「平衡」还是「卓越性能」
            // 决定了 PROCTHROTTLEMAX 写进哪个方案）。
            var changed = new List<OptKnob>();
            foreach (var cb in knobViews)
            {
                var k = (OptKnob)cb.Tag;
                int idx = cb.SelectedIndex;
                if (idx < 0 || idx >= k.Values.Length) continue;   // 选中"不在档位表中" = 不动它
                int want = k.Values[idx];
                if (want == k.Value) continue;
                if (Program.SetKnob(k, want)) changed.Add(k);
                else Log("⚠ 档位写配置失败：" + k.Key);
            }
            if (changed.Count > 0)
            {
                // 同一类只应用一次：改 3 个电源项不该连切 3 遍电源方案
                var kinds = new List<string>();
                foreach (var k in changed) if (!kinds.Contains(k.Apply)) kinds.Add(k.Apply);
                foreach (var kind in kinds)
                {
                    OptKnob probe = changed.Find(delegate(OptKnob x) { return x.Apply == kind; });
                    string r = Program.ApplyKnob(probe);
                    if (!string.IsNullOrEmpty(r)) Log("[档位 · " + kind + "] " + r);
                }
                Cfg = Config.Load(Program.ConfigPath);
                Program.ReloadCfg(Cfg);
                Log(">>> 档位已保存：" + changed.Count + " 项，应用到系统 " + kinds.Count + " 组");
            }

            // ---- ② 再按勾选执行 ----
            // 按原始顺序取勾选结果（展示顺序是按风险排过的，执行顺序不能跟着变）
            var chosen = new List<OptStep>();
            foreach (var s in steps)
            {
                RoundCheck rc;
                if (map.TryGetValue(s, out rc) && rc.Checked) chosen.Add(s);
            }
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "一项都没勾，什么也不会执行。", "一键优化",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log(">>> 开始一键优化…（已选 " + chosen.Count + "/" + steps.Count + " 步）");
            btnOptimize.Enabled = false; btnOptimize.Text = "优化中…";
            var gw = new BackgroundWorker();
            gw.DoWork += delegate
            {
                // 只在勾了「实验」档时才打还原点：稳妥 / 谨慎档靠「恢复备份」就够，
                // 没必要为了关个动画留一个还原点（每个点都占系统盘空间，还慢 10~40 秒）。
                // 打点失败**不拦路** —— 它是保险，不是门槛；但必须把原因写进日志。
                bool hasExperimental = chosen.Exists(delegate(OptStep s) { return s.Level == RiskLevel.Experimental; });
                if (Cfg.RestorePointBeforeOpt && hasExperimental)
                {
                    Log("正在创建系统还原点（10~40 秒，失败不影响优化）…");
                    string why = Program.CreateRestorePoint("Fluxion 一键优化前 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                    if (why.Length > 0) Log("⚠ 未创建还原点：" + why);
                }
                else if (hasExperimental && !Cfg.RestorePointBeforeOpt)
                    Log("⚠ 未创建还原点：设置里关掉了「优化前自动创建还原点」");
                Program.ApplyAll(Log, chosen);
            };
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
        List<StatusItem> lastStatus;      // 当前表里的整份数据（第二趟补 DPC 行时要就地改一条 + 重算健康度）

        // 进程当前 OS 线程数（诊断用，只写日志）。
        //  为什么不用 ThreadPool.ThreadCount：那是 .NET 4.5 才加的成员，本工程用 4.0 的 csc 编译；
        //  而且要判断"线程池是不是被长阻塞任务占满了"，看进程总线程数更直接。
        static int ProcThreads()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) return p.Threads.Count;
            }
            catch { return -1; }
        }

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
            //  ★ 第 143 轮：把"排队"和"执行"拆开计时。
            //  以前只有一个总耗时（日志里 6~10 秒），但它**从 RunWorkerAsync 之前就开始计**，
            //  所以里面既有线程池排队、也有真正的读取。而单进程冷测 GetStatusItems 只要 0.8 秒 ——
            //  差的那几秒只能来自排队，不拆开就永远只能瞎猜。
            long queuedMs = -1;
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                queuedMs = sw.ElapsedMilliseconds;
                //  首屏**不带** DPC 那一行（唯一要 WMI 性能库的项，冷启动 0.3~2 秒）：
                //  先让 49 行 + 健康度出来，那一行随后由第二趟补（见下面的补行任务）。
                e.Result = Program.GetStatusItems(false);
            };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                gridBusyFlag = 0;
                long ms = sw.ElapsedMilliseconds;
                long work = queuedMs >= 0 ? ms - queuedMs : -1;
                if (e.Error != null)
                {
                    Log("状态读取失败: " + e.Error.Message);
                    ShowGridPlaceholder("读取失败：" + e.Error.Message + "（点「刷新」重试）");
                    return;
                }
                if (e.Result != null) FillList((List<StatusItem>)e.Result);
                if (ms > 1500)
                {
                    //  诊断写**文件日志**就够了（2026-10-20）：原文是给开发者看的，而本机每次启动
                    //  都会触发（≈1.6s），等于把"内部计时 + 请转告作者"常驻在页脚 —— 这是整个
                    //  界面最直接的"未完成感"。只有真慢到用户会觉得卡（>4s）才升为界面提示，
                    //  并且换成用户能理解的话。
                    Program.Log("[体检] 状态读取耗时 " + ms + " ms（排队 " + queuedMs + " / 执行 " + work
                        + "，进程线程 " + ProcThreads() + "）");
                    if (ms > 4000)
                        Log("体检数据读取较慢（" + (ms / 1000.0).ToString("0.0") + " 秒）· 点「刷新」可重试");
                }
                FillDpcRowAsync();
            };
            gw.RunWorkerAsync();
        }

        // 第二趟：只补「DPC / 中断分布」这一行。
        //  为什么单独一趟：它是整张表唯一要 WMI 性能库的项（首次查询要拉起 wmiprvse），
        //  而结论只是"单次取样、看个大概"。让它和主表串行，等于用 2 秒换一个字。
        //  失败/超时都不动界面 —— 占位行会停在「🟡 读取中」，用户点「刷新」可重取。
        void FillDpcRowAsync()
        {
            if (lvOpt == null || lvOpt.IsDisposed) return;
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = Program.DpcSnapshotItem(); };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null || e.Result == null) return;
                PatchStatusRow((StatusItem)e.Result);
            };
            bw.RunWorkerAsync();
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

        //  让明细表长到装下全部行（2026-10-20）。
        //  为什么要这样：ListView 的竖向滚动条画在**非客户区**，是一条约 17px 的系统灰带，
        //  嵌在白卡中间就是"拼上去的一块"。想过藏掉它换自绘 —— 实测 `ShowScrollBar` 会和
        //  控件的重排互相触发，直接把探针跑挂（记忆 五十二轮有完整证据），此路封死。
        //  于是反过来做：**让表不需要滚动**。表有多长就多高，滚动交给页面自己那条细滚动条。
        //  代价是这张卡会随体检项数量变长（本机 52 项 ≈ 1480px），换来的是整页没有一条
        //  原生灰带 —— 用户要的正是这个。
        void FitTableHeight() { FitListToRows(lvOpt, 8); }

        //  让表格长到装下全部行（lvOpt / lvMon 共用）。
        //  为什么必须这样：ListView 的竖条画在**非客户区**，是系统灰带；只要内容溢出它就会出现。
        //  高度差一行就够触发 —— 所以这里向控件要真实行高，不写死。
        //  向上找所在页：表可能在不同页上（lvOpt / lvMon），改完高度要让**所在页**重排，
        //  不能写死 pages[P_OPT]。
        static Pg PgOf(Control c)
        {
            for (Control p = c; p != null; p = p.Parent)
            {
                Pg pg = p as Pg;
                if (pg != null) return pg;
            }
            return null;
        }

        static void FitListToRows(ListView lvOpt, int minRows)
        {
            if (lvOpt == null || lvOpt.IsDisposed) return;
            try
            {
                int rows = Math.Max(1, lvOpt.Items.Count);
                //  ★ 表头高 / 行高**向控件本身要**，不写死 S(25)/S(28)：
                //    原生表头是独立窗口，125%/150% 缩放下它的高度不等于 Theme.S(25)，
                //    差几像素就会让内容溢出 → 原生滚动条又冒出来，白改一场。
                //  表头已由 TableBox 自绘，ListView 本身**没有** header 了 → 表头高按 0 算；
                //  行高仍然向控件要真实值（原生表头/行高在不同 DPI 下跟 Theme.S 不完全等值）。
                int hdr = 0, rowH = Theme.S(28);
                try
                {
                    Rectangle r0 = lvOpt.GetItemRect(0);
                    if (r0.Top > 4) hdr = r0.Top;
                    if (r0.Height > 4) rowH = r0.Height;
                }
                catch { }
                int h = hdr + rows * rowH + Theme.S(4);       // +4：收边 + 一点余量，宁可多不可少
                //  至少留 minRows 行的框：载入中/空表时不能塌成一条
                h = Math.Max(h, hdr + Math.Max(1, minRows) * rowH);
                if (lvOpt.Height != h)
                {
                    lvOpt.Height = h;
                    TableBox bx = BoxOf(lvOpt); if (bx != null) bx.Fit();
                    Pg pg0 = PgOf(lvOpt); if (pg0 != null) pg0.Reflow();
                    //  延迟一帧再落一次：这一轮布局里还有别的东西（占位行/自适应列宽）会改动它
                    try
                    {
                        lvOpt.BeginInvoke((Action)(delegate
                        {
                            try
                            {
                                if (lvOpt != null && !lvOpt.IsDisposed)
                                {
                                    lvOpt.Height = h;
                                    TableBox bx1 = BoxOf(lvOpt); if (bx1 != null) bx1.Fit();
                                    Pg pg1 = PgOf(lvOpt); if (pg1 != null) pg1.Reflow();
                                }
                            }
                            catch { }
                        }));
                    }
                    catch { }
                }
            }
            catch { }
        }

        void FillList(List<StatusItem> items)
        {
            if (lvOpt == null) return;
            if (lvOpt.InvokeRequired) { lvOpt.Invoke((Action)(delegate { FillList(items); })); return; }
            lastStatus = items;
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
                    // 实验档**不在这一列做视觉标记**：状态列已经用橙色胶囊表示"未生效"，
                    // 项名再涂成橙色会让同一行出现两块橙色、各自含义不同。实验档的告知放在
                    // 底部说明行 + 「一键优化」的分组清单里（那里有完整的代价文字）。
                    lvOpt.Items.Add(lvi);
                }
            }
            finally { lvOpt.EndUpdate(); }
            FitTableHeight();
            UpdateHealth(items);
        }

        // 第二趟补回来的那一行（DPC / 中断分布）：**原地改**，不重建整表 ——
        //  FillList 会 Items.Clear()，把滚动位置和选中态一起丢掉；用户正看到一半时
        //  表格自己跳回顶部，比那行晚 1 秒出现糟糕得多。
        void PatchStatusRow(StatusItem it)
        {
            if (it == null) return;
            if (lvOpt == null || lvOpt.IsDisposed) return;
            if (lvOpt.InvokeRequired)
            {
                try { lvOpt.Invoke((Action)(delegate { PatchStatusRow(it); })); } catch { }
                return;
            }
            try
            {
                ListViewItem row = null;
                foreach (ListViewItem x in lvOpt.Items)
                    if (x.Text != null && x.Text.StartsWith(it.Item, StringComparison.Ordinal)) { row = x; break; }
                if (row == null || row.SubItems.Count < 4) return;
                int kind = Program.BadgeKind(it.Status);
                row.SubItems[1].Text = Program.BadgeText(it.Status);
                row.SubItems[1].ForeColor = kind == Program.BadgeOk ? Theme.Ok
                                          : kind == Program.BadgeWarn ? Theme.Warn
                                          : kind == Program.BadgeInfo ? Theme.TextDim : Theme.Err;
                row.SubItems[2].Text = it.Current == null ? "" : it.Current.Trim();
                row.SubItems[3].Text = it.Expected == null ? "" : it.Expected.Trim();
                // 健康度要跟着重算：这一行可能从"待确认"翻成"已生效"，顶上的计数不跟着变就是错的
                if (lastStatus != null)
                {
                    for (int i = 0; i < lastStatus.Count; i++)
                        if (string.Equals(lastStatus[i].Item, it.Item, StringComparison.Ordinal)) { lastStatus[i] = it; break; }
                    UpdateHealth(lastStatus);
                }
                lvOpt.Invalidate();
            }
            catch { }
        }

        //  仪表盘标题下的摘要句（第 148 轮）。三段都是"现在怎么样"的事实，
        //  没有一段需要用户点进去才知道 —— 这就是它该待在标题下面的理由。
        void RefreshDashSub()
        {
            if (lblDashSub == null || lblDashSub.IsDisposed) return;
            //  分两段：结论段（跟着状态变色）+ 量纲段（恒为次级灰）。见 MetaLine 注释。
            string head = dashOpt.Length > 0 ? dashOpt : "正在读取优化状态…";
            string tail = "";
            if (games != null && games.Count > 0) tail += "　·　库 " + games.Count + " 款";
            if (lastScanAt != DateTime.MinValue) tail += "　·　" + lastScanAt.ToString("HH:mm") + " 扫描";
            bool warn = head.IndexOf("未生效", StringComparison.Ordinal) >= 0;
            lblDashSub.Head = head;
            lblDashSub.Tail = tail;
            lblDashSub.HeadColor = warn ? Theme.TextSafe(Theme.Warn) : Theme.TextDim;
            lblDashSub.Invalidate();
        }

        // 健康度 / 漂移提示。从 FillList 抽出来的原因见 PatchStatusRow：
        // 补一行之后要重算计数，但不该重建整张表。
        void UpdateHealth(List<StatusItem> items)
        {
            int ok = 0, warn = 0, bad = 0, info = 0, expN = 0;
            foreach (var it in items)
            {
                int kind = Program.BadgeKind(it.Status);
                if (kind == Program.BadgeOk) ok++;
                else if (kind == Program.BadgeWarn) warn++;
                else if (kind == Program.BadgeInfo) info++;
                else bad++;
                if (it.Risk == RiskLevel.Experimental) expN++;
            }

            dashOpt = (warn + bad == 0) ? "全部优化已生效" : (warn + bad) + " 项优化未生效";
            RefreshDashSub();
            if (lblHealth != null)
            {
                lastHealthUnapplied = warn + bad;
                RefreshHealthTitle(lblHealth.Width);
                lblHealth.ForeColor = Theme.TextSafe((warn + bad == 0) ? Theme.Ok : (warn > 0 ? Theme.Warn : Theme.Err));
            }
            if (gaugeHealth != null)
            {
                Color gaugeCol = (warn + bad == 0) ? Theme.Ok : (warn > 0 ? Theme.Warn : Theme.Err);
                gaugeHealth.SetHealth(ok, items.Count, gaugeCol);
            }
            if (lblHealthSub != null)
                SetBody(lblHealthSub, "已生效 " + ok + " 项 · 未生效 " + warn + " 项"
                    + (info > 0 ? " · 待确认 " + info + " 项" : "")
                    + (bad > 0 ? " · 需处理 " + bad + " 项" : "") +
                    (warn + bad > 0 ? "\n建议在「性能优化」页点一次一键优化"
                                    : (info > 0 ? "\n其余「待确认」不是缺陷（可选 / 不适用 / 读不到），可逐行双击查看"
                                                : "\n全部优化已生效"))
                    // 单独一行说清"实验档"是什么 —— 这一档的代价是功能会直接不可用，
                    // 不该只写在清单弹窗里（有人根本不点开就取消了）。
                    + (expN > 0 ? "\n其中有 " + expN + " 项属「实验」档：会让 WSL2 / Docker / 模拟器 / 系统服务不可用。"
                                + "点「一键优化」时会单独成组并写明代价，可只勾其余项。" : ""));
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
                else { SetBody(lblDrift, "⚠ 检测到 " + (warn + bad) + " 项未生效（系统更新 / 升级常静默重置优化项），点「一键优化」恢复。"); lblDrift.ForeColor = Theme.TextSafe(Theme.Warn); }
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
            //  降频：只有"正停在仪表盘页/实时监控页 + 窗口可见 + 没最小化"时才每 4 秒采样。
            //  实时曲线只有看得见才有意义；后台时 nvidia-smi 每 4 秒拉一次纯属浪费（实测 41ms/次），
            //  降到每 8 个 tick（约 32 秒）一次，高温告警仍然有效。
            bool watching = !closing && (page == P_DASH || page == P_MON) && Visible && WindowState != FormWindowState.Minimized;
            if (!watching)
            {
                sysIdleSkip++;
                if (sysIdleSkip % 8 != 0) return;
            }
            if (System.Threading.Interlocked.CompareExchange(ref sysBusyFlag, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int c = Program.GetCpu(), m = Program.GetMem(), g = Program.GetGpuPct(), t = Program.GetGpuTemp();
                    int ct = Program.GetCpuTemp();
                    if (!closing && barCpu != null)
                    {
                        // BeginInvoke：UI 线程被同步 IO 钉住时池线程不必陪着等（2026-09-30 体检）
                        barCpu.BeginInvoke((Action)(delegate
                        {
                            //  GetCpu 首次采样没有差值（返回 -1），这里跳过而不是把 -1% 画上去
                            if (c >= 0) { barCpu.Value = c; barCpu.RightText = c + "%"; barCpu.BarColor = LoadColor(c, Cfg.AlertCpuPct); }
                            if (m >= 0) { barMem.Value = m; barMem.RightText = m + "%"; barMem.BarColor = LoadColor(m, Cfg.AlertMemPct); }
                            if (g >= 0) { barGpu.Value = g; barGpu.RightText = g + "%"; }
                            barTemp.Value = Math.Min(100, Math.Max(0, t)); barTemp.RightText = t + "°C";
                            barTemp.BarColor = t >= 83 ? Theme.Err : (t >= 70 ? Theme.Warn : Theme.Ok);
                            if (c >= 0 && spCpu != null) spCpu.Push(c);
                            if (g >= 0 && spGpu != null) spGpu.Push(g);
                            if (m >= 0 && spMem != null) spMem.Push(m);
                            if (t >= 0 && spGpuTemp != null) spGpuTemp.Push(t);
                            if (ct >= 0 && spCpuTemp != null) spCpuTemp.Push(ct);
                        }));
                    }
                    CheckAlarms(c, m, t);
                }
                catch { }
                finally { sysBusyFlag = 0; }
            });
        }

        //  条的颜色跟随阈值（第 148 轮）：页脚已经在喊"内存 97%（阈值 90%）"，
        //  条却还是蓝色 —— 同一屏两处信息打架。三档与温度条 barTemp 一致：
        //  绿=正常、橙=到阈值、红=超阈值 5 点。GPU 不跟着变（游戏满载是正常状态，不是问题）。
        static Color LoadColor(int v, int warnAt)
        {
            if (warnAt > 0 && v >= warnAt + 5) return Theme.Err;
            if (warnAt > 0 && v >= warnAt) return Theme.Warn;
            return Theme.Ok;
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
                if (cat == null) cat = "";
                string remote = Program.GetRemoteState();
                if (remote == null) remote = "";
                if (Cfg == null) Cfg = Program.Cfg ?? new Config();
                if (gameStartKey == null) gameStartKey = "";
                bool inGame = game != null && game.Length > 0;
                string state = inGame
                    ? ("⚡ 游戏中：" + Program.GameDisplayName(game) + (cat.Length > 0 ? "  [" + cat + "]" : "  [自动]"))
                    : (Program.SceneOffice ? "🛡️ 防护开启（办公场景）" : "待命 · 未检测到游戏");
                if (lblGameState != null)
                {
                    //  层级（2026-10-20）：14pt 大标题只留给「正在玩」这一个时刻。
                    //  空态 / 办公态降到 11pt 弱化色 —— 否定状态不该比正常状态还响，
                    //  原来一进来最抢眼的就是那句「未检测到游戏」。
                    Font want = inGame ? Theme.FB(14f) : Theme.F(11f);
                    if (lblGameState.Font != want) lblGameState.Font = want;
                    var bl = lblGameState as BreathingLabel;
                    if (bl != null) bl.SetMode(inGame, Program.SceneOffice);
                    SetBody(lblGameState, state);
                    //  ★ 2026-10-26（第 154 轮）：颜色改回语义。
                    //    原来「在玩」用 Accent（蓝）—— 蓝在本项目里专指"能点"，一个不能点的
                    //    状态文字染成蓝色，等于把交互色降级成装饰；而"检测到游戏、联动已生效"
                    //    本来就是**状态**，该走绿。空闲时用正文色（原来是 TextDim 灰，
                    //    这张卡的第一行因此读起来像一句注脚，卡也显得没开机）。
                    lblGameState.ForeColor = inGame ? Theme.TextSafe(Theme.Ok) : Theme.Text;
                }
                // 分隔统一用「　·　」（与帧生成那行一致）：四个半角空格在中文下面忽宽忽窄
                SetBody(lblLink, "远控状态：" + remote + "　·　联动：" + (Cfg.AwareEnable ? "已开启" : "已关闭")
                               + "　·　场景：" + (Program.SceneOffice ? "办公" : "游戏")
                               + (inGame && gameStartAt != DateTime.MinValue ? "　·　时长：" + FmtDur(DateTime.Now - gameStartAt) : ""));
                if (tray != null)
                {
                    string tip = "Fluxion" + (inGame ? (" - 游戏中：" + Program.GameDisplayName(game)) : " - 待命中");
                    if (!Program.ShareBuild && remote == "运行中") tip += "\n远控：运行中";
                    try { tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; } catch { }
                }
                if (miStatus != null)
                {
                    string gName = Program.GameDisplayName(game);
                    if (gName.Length > 8) gName = gName.Substring(0, 7) + "…";
                    miStatus.Text = inGame ? ("● 游戏中：" + gName) : "● 待命中";
                }

                // 联动触发：以「游戏进程名」为准（从无到有、或换了一个游戏，都重新触发一轮）
                if (game != lastGame)
                {
                    if (game != null)
                    {
                        gameStartAt = DateTime.Now;
                        gameStartKey = KeyOfProcess(game);
                        lastGameObj = GameByKey(gameStartKey);
                        Log("检测到游戏启动: " + Program.GameDisplayName(game) + "（档位 " + (cat.Length > 0 ? cat : "自动") + "）");
                        LinkGameEnter(cat, game);
                    }
                    else
                    {
                        gameStartAt = DateTime.MinValue;
                        Log("游戏已退出，联动收尾");
                        LinkGameExit();
                        DelayFgSelfCheck();     // ★ 退出后自动补一次帧生成自检（第 144 轮）
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

        // 稳定 Id → 库条目（游戏退出自检要用它的目录）
        DlssgGame GameByKey(string key)
        {
            if (string.IsNullOrEmpty(key) || games == null) return null;
            foreach (DlssgGame g in games)
                if (g != null && Lib.IdOf(g) == key) return g;
            return null;
        }

        // ---------------- 帧生成「生效自检」自动化（第 144 轮） ----------------
        //  为什么要有：代理到底跑没跑起来、实际几倍，只有它自己的 jsonl 知道。以前要"打完一局 →
        //  出来翻日志"，而这恰好是用户最不想做的一步（他要的是"少翻目录、少折腾"）。
        //  现在：游戏退出 → 等 8 秒 → 后台读那一份日志 → 结论写日志 + 刷封面卡上的「实测 6X」。
        //  ⚠ 延后 8 秒不是保险起见：游戏刚退时代理还在收尾写 jsonl、进程也可能没退干净，
        //    立刻读会读到空文件/旧文件，得出"这次没跑"的错误结论 —— 误判比不报更糟。
        void DelayFgSelfCheck()
        {
            if (fgCheckTimer == null)
            {
                fgCheckTimer = new System.Windows.Forms.Timer();
                fgCheckTimer.Interval = 8000;
                fgCheckTimer.Tick += delegate(object s, EventArgs e)
                {
                    fgCheckTimer.Stop();
                    if (closing || IsDisposed) return;
                    SelfCheckFg(lastGameObj);
                };
            }
            fgCheckTimer.Stop();      // 连开两局时只认最后一次退出
            fgCheckTimer.Start();
        }

        void SelfCheckFg(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Dir.Length == 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                FgResult r = null;
                try { r = FgVerify.Check(g.Dir); } catch { }   // 后台 IO，顺带把结果填进缓存
                string line;
                if (r == null || !r.HasLog)
                {
                    //  ★ 2026-10-31 分层（用户原话"云里雾里"）：FgVerify 只认 SM86 的 jsonl
                    //    **会话日志**，没有它 ≠ 代理没加载 —— OptiScaler.log 是代理一注入就写的，
                    //    它在刚跑过的时间窗内有更新 = 已加载。原来一刀切报"没被游戏加载"，
                    //    和游戏目录里明明有 OptiScaler.log 自相矛盾。
                    bool loaded = false;
                    try
                    {
                        string ol = Path.Combine(g.Dir, "OptiScaler.log");
                        if (File.Exists(ol) && (DateTime.Now - File.GetLastWriteTime(ol)).TotalMinutes <= 30)
                            loaded = true;
                    }
                    catch { }
                    line = loaded
                        ? "代理已加载，但还没有帧生成会话记录 —— 进游戏把「帧生成」打开跑一局，退出后再看"
                        : "没有代理日志 —— 代理没被游戏加载（换入口 / 看「运行诊断」）";
                }
                else if (r.GenCount >= 0) line = "实测 " + (r.GenCount + 1) + "X（" + r.When.ToString("MM-dd HH:mm") + "）";
                else if (r.IsEmpty && r.HasPrev && r.PrevGenCount >= 0)
                    line = "这次没跑帧生成；上次实测 " + (r.PrevGenCount + 1) + "X（" + r.PrevWhen.ToString("MM-dd HH:mm") + "）";
                else if (r.Note.Length > 0) line = r.Note;
                else line = "有代理日志但没有倍率记录（游戏里没开帧生成？）";
                try { Program.Log("[自检] " + g.Title + "：" + line); } catch { }
                try
                {
                    if (!closing && IsHandleCreated && !IsDisposed)
                        BeginInvoke((Action)delegate { RefreshFgSummary(); });
                }
                catch { }
            });
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
                if (lblLinkAct != null) SetBody(lblLinkAct, LinkIdleText);
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
                if (bgMode) return;                     // 后台休眠：日志框看不见，5s 一次的读盘停掉
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
                //  左栏游戏列表跟着库一起刷新（keep=当前选中，不会把用户刚点的行跳走）
                FillFgList();

                //  方案下拉的版本号是**构建时快照**（BuildPageFrameGen 里那批字面量）：包一换，
                //  界面还停在上一个版本号，要重启才更新（2026-11-01 用户："你这下拉就只能支持这么多"）。
                //  这里每次刷新都把两类版本号重读一遍 —— RefreshFgSummary 是导入 / 部署 / 换游戏
                //  都会走到的地方。Items.Set 只改文案（索引不变、不触发 SelectedIndexChanged），
                //  与原值相同则内部直接返回，幂等不闪。
                if (cbScheme != null)
                {
                    cbScheme.Items.Set(2, "DLSS MFG 0.3.5 · 基础兼容");
                    cbScheme.Items.Set(3, "DLSS MFG " + Dlssg040.Ver + " · 进阶内核");
                }

                if (lblGi != null)
                {
                    if (selected == null) SetBody(lblGi, "");
                    else
                    {
                        string exe = selected.Exe;
                        if (exe == null || exe.Length == 0) exe = "—";
                        string dir = selected.Dir;
                        long played = Program.PlayTimeOf(Lib.IdOf(selected));
                        long last = selected.LastPlayed;   // MergeScan 时已并入库标记
                        string when = "";
                        if (last > 0)
                        {
                            try
                            {
                                DateTime t = new DateTime(1970, 1, 1, 8, 0, 0, DateTimeKind.Local).AddSeconds(last);
                                TimeSpan d = DateTime.Now - t;
                                if (d.TotalMinutes < 60) when = "刚刚";
                                else if (t.Date == DateTime.Today) when = "今天";
                                else if (t.Date == DateTime.Today.AddDays(-1)) when = "昨天";
                                else if (d.TotalDays < 30) when = ((int)d.TotalDays / 7) + " 周前";
                                else when = t.ToString("yyyy-MM-dd");
                            }
                            catch { }
                        }
                        string line = "平台：" + Lib.PlatformLabel(selected.Platform)
                            + "\n入口程序：" + exe
                            + "\n安装目录：" + (dir == null || dir.Length == 0 ? "—" : dir);
                        string tail = "";
                        if (played > 0) tail += "累计时长：" + Program.FmtPlay(played);
                        if (when.Length > 0) tail += (tail.Length > 0 ? " · " : "") + "最近游玩：" + when;
                        if (tail.Length > 0) line += "\n" + tail;
                        //  实测帧生成（第 144 轮）：卡面只有一枚「实测 6X」角标，点开这里要看得到
                        //  确切时间和来源。**这一行恒定存在**（没读数时说"暂无"）——
                        //  有/无之间切行数会让卡片高度跳一下，比信息少更扎眼。
                        int genMz = FgVerify.MeasuredGen(selected);
                        line += "\n实测帧生成：" + (genMz >= 0
                            ? (genMz + 1) + "X（" + FgVerify.MeasuredWhen(selected).ToString("MM-dd HH:mm") + "）"
                            : "暂无读数（进游戏跑一局，退出后自动读回）");
                        SetBody(lblGi, line);
                    }
                }

                //  启动参数状态行（与上面 lblGi 同卡）：跟着选中游戏走，保存对话框后立即回显。
                //  2026-10-31 起区分三态：用户自定义 / 内置预设（鸣潮这类自带启动规矩的游戏，自动带上）/
                //  未设置 —— 让"到底会不会带参数"在界面上就能看穿，不用翻 library.json。
                if (lblGiArgs != null)
                {
                    if (selected == null) SetBody(lblGiArgs, "");
                    else
                    {
                        string custom, preset; int nEnv;
                        Lib.LaunchConfigView(selected, out custom, out preset, out nEnv);
                        string tail = nEnv > 0 ? " · 环境变量 " + nEnv + " 条" : "";
                        //  文案保持一行能放下（Body 不折行，长句会顶出卡片圆角）；
                        //  参数怎么写、举什么例，教给对话框正文说。
                        SetBody(lblGiArgs, custom.Length > 0 ? ("启动参数：" + custom + tail)
                            : preset.Length > 0 ? ("启动参数：" + preset + "（内置，启动自动带上）" + tail)
                            : (nEnv > 0 ? "启动参数：无" + tail : "启动参数：未设置"));
                    }
                }

                bool src = Dlssg.SourceReady();
                string hags = Dlssg.HagsText();
                int hagsWarn = Dlssg.HagsState() == 2 ? 0 : 1;
                if (lblRt != null)
                {
                    // 固定 3 行（BodyFixed）：文案变化不再改变分区高度，避免整页重排
                    Dlssg030.PackActualInfo pa = Dlssg030.PackActual();
                    string srcHint = Dlssg.SourceHint();
                    //  ★ 2026-10-31 文案重写（用户原话"云里雾里"）：每行先回答"能不能用 / 缺什么 / 点哪里"，
                    //    术语给全称（HAGS → 硬件加速 GPU 调度）。字节级实测（体积 / INI / 备用入口数 /
                    //    签名）不再堆卡片 —— 异常态才展开原文，常态只留代号与日期，细节在日志与「运行诊断」。
                    //  ★ 2026-11-01 再改：每项补一行**普通人能听懂**的括号注解（"很多游戏玩家并不了解"）。
                    string rt = "帧生成运行时：" + (src ? "已就绪"
                                : (srcHint.Length > 0 ? "未就绪（" + srcHint + "）" : "未安装 → 点「下载运行时」"));
                    rt += "\n硬件加速 GPU 调度：" + (hagsWarn == 1 ? "未开启 ⚠（帧生成必需）" : "已开启 ✓");
                    string pk;
                    if (!pa.Found) pk = "方案包：未导入（点「下载运行时」会自动就位）";
                    else if (pa.Warn) pk = pa.Line();        // 异常态直接给字节级原文 —— 这种时候细节就是信息
                    else
                    {
                        string when = pa.ImportedAt != null && pa.ImportedAt.Length > 0 ? pa.ImportedAt : pa.EntryTime;
                        pk = "方案包：" + pa.Code + (when.Length > 0 ? "（" + when + "）" : "");
                    }
                    SetBody(lblRt, rt + "\n" + pk);
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
                if (lblDlssLib != null)
                {
                    string lv = selected == null ? "" : Dlssg.LibVersions(selected.Dir);
                    //  第二行是给普通人的注解：说清"这是个啥、看到可升级该怎么办"（不承诺自动替换 ——
                    //  产品刻意不做，会和用户自己装的 OptiScaler 代理撞车）。
                    SetBody(lblDlssLib, lv);
                }
                if (lblD3d12 != null)
                {
                    string dh = Dlssg.D3d12Hint();
                    SetBody(lblD3d12, "d3d12 入口（反作弊游戏用）：" + (dh.Length == 0 ? "可用 ✓" : dh));
                    lblD3d12.ForeColor = dh.Length == 0 ? Theme.TextDim : Theme.Warn;
                }
                if (lblPatrol != null)
                {
                    //  每次进页面 / 换选中游戏都按目录里的真实文件重算一遍（只读）。
                    //  刻意不写日志：巡检只负责"看得见"，要不要处理由用户点按钮决定，
                    //  每次刷页面都记一行会把日志刷满。
                    string pt = selected == null ? "入口状态：未选中游戏（先在列表里点一个）"
                                                 : Plan.PatrolLine(selected.Dir);
                    SetBody(lblPatrol, pt);
                    lblPatrol.ForeColor = pt.IndexOf('\u26a0') >= 0 ? Theme.Warn : Theme.TextDim;
                }
                if (lbl030 != null)
                {
                    if (selected == null)
                    {
                        SetBody(lbl030, "当前状态：未选中游戏");
                        lbl030.ForeColor = Theme.TextDim;
                    }
                    else if (XeMfg.Detect(FgDir(selected)).Length == 0)
                    {
                        // 通用（3A 单机）：入口名由上面的下拉决定
                        string en = Cfg.DlssgGenericEntry;
                        string pf = Path.Combine(XeMfg.TargetDir(FgDir(selected), ""), en);
                        bool inPlace = File.Exists(pf) && Plan.OwnerOf(pf) == Plan.D030;
                        bool legacyIn = false;
                        foreach (string e2 in Plan.AllEntryNames)
                        {
                            string q = Path.Combine(FgDir(selected), e2);
                            if (File.Exists(q) && Plan.OwnerOf(q) == Plan.Legacy) legacyIn = true;
                        }
                        SetBody(lbl030, "当前：通用模式（3A 单机）· 入口 " + en + "  "
                            + (inPlace ? "← 本方案在用" : (legacyIn ? "← 装着旧版代理（上限 4X），切过来即可升到 6X"
                                                                  : "← 未接入")));
                        lbl030.ForeColor = inPlace ? Theme.Ok : Theme.TextDim;
                    }
                    else
                    {
                        string cur3 = Plan.Current(FgDir(selected));
                        string slot3 = Plan.EntryName(XeMfg.Detect(FgDir(selected)));
                        bool mine3 = cur3 == Plan.D030;
                        SetBody(lbl030, cur3 == Plan.None
                            ? "当前：原生纯净（未安装）"
                            : "当前：" + Plan.PlanName(cur3) + "（" + slot3 + " 槽位）"
                              + (mine3 ? "  ← 本方案在用" : "  ← 切到本方案会把它改名停放"));
                        lbl030.ForeColor = mine3 ? Theme.Ok : Theme.TextDim;
                    }
                }
                if (lbl040 != null)
                {
                    string packLine = Dlssg040.PackCaption();
                    if (selected == null)
                    {
                        SetBody(lbl040, packLine + "\n未选中游戏（先在列表里点一个）");
                        lbl040.ForeColor = Theme.TextDim;
                    }
                    else
                    {
                        string cur4 = Plan.Current(FgDir(selected));
                        string e4 = Dlssg040.InstalledEntry(FgDir(selected));
                        string miss4 = Dlssg040.DeployedLack(FgDir(selected));
                        bool mine4 = cur4 == Plan.D040;
                        // 二游：把这张表给的答案摆到状态行，别让人靠猜。
                        //  0.4.x 与 0.3.x 的推荐入口是同一套（同一颗代理换名字）；
                        //  异环例外 —— 0.4.0 没有 ASI 版。
                        string gk = SecondGame.Detect(selected);
                        string want4 = gk.Length > 0 ? SecondGame.SuggestEntry040(gk) : "";
                        string gkLine = gk.Length > 0
                            ? "\n0.4.x 建议入口：" + (want4.Length > 0 ? want4
                                : "无（《" + SecondGame.Label(gk) + "》的 ASI 版停在 0.3.5，建议用上一区块）")
                            : "";
                        SetBody(lbl040,
                            packLine + "\n"
                            + (cur4 == Plan.None ? "当前：原生纯净（未安装）"
                                                 : "当前：" + Plan.PlanName(cur4)
                                                   + (e4.Length > 0 ? "（入口 " + e4 + " 在位）" : "")
                                                   + (mine4 ? "  ← 本方案在用" : "  ← 切过来会把它改名停放"))
                            + gkLine
                            + (mine4 && miss4.Length > 0 ? "\n⚠ 部署不齐：" + miss4 : ""));
                        lbl040.ForeColor = mine4 ? Theme.Ok : Theme.TextDim;
                    }
                }
                if (lblNv != null)
                {
                    string capNv = MfgBridge.Caption();
                    if (selected == null)
                    {
                        SetBody(lblNv, capNv + "\n未选中游戏（先在列表里点一个）");
                        lblNv.ForeColor = Theme.TextDim;
                    }
                    else
                    {
                        string eN = MfgBridge.InstalledEntry(FgDir(selected));
                        bool mineN = eN.Length > 0;
                        string curN = Plan.Current(FgDir(selected));
                        SetBody(lblNv,
                            capNv + "\n"
                            + (mineN ? "当前：" + Plan.PlanName(curN) + "（入口 " + eN + " 在位）  ← 本方案在用"
                                     : (curN == Plan.None ? "当前：原生纯净（未安装）"
                                                          : "当前：" + Plan.PlanName(curN) + "  ← 切过来会把它改名停放")));
                        lblNv.ForeColor = mineN ? Theme.Ok : Theme.TextDim;
                    }
                }
                if (lblSm != null)
                {
                    //  读驱动库里 Smooth Motion 的记录（只读 nvdrsdb，不动任何东西）
                    uint? smv = NvDrsDb.ReadUserValue(NvDrsDb.IdSmoothMotion);
                    string st = !smv.HasValue ? "驱动库无记录（默认关）"
                              : (smv.Value == 5 ? "已开启（值 5）" : "当前值 " + smv.Value);
                    SetBody(lblSm, "驱动状态：" + st
                        + "\n" + (selected == null
                            ? "未选中游戏"
                            : "选中：" + selected.Title + "　·　按 exe 单独写入，不影响别的游戏"));
                    lblSm.ForeColor = (smv.HasValue && smv.Value == 5) ? Theme.Ok : Theme.TextDim;
                }
                if (btnAsi != null) btnAsi.Enabled = DlssgAsi.Available();
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
                    //  排版（2026-10-20）：原来用四个半角空格拼键值对，中文下面是忽宽忽窄的缝，
                    //  再加 ✅/⚠ emoji、整行随状态染绿 —— 一行的"调试输出感"就够了。
                    //  现在统一用「　·　」分隔（与其它卡片的正文一致），异常才染色，正常保持正文色。
                    string mode = "";
                    if (selected != null)
                    {
                        string api = Lib.RenderApi(selected);
                        if (api.Length > 0)
                            mode = "　·　" + selected.Title + " 运行模式：" + api
                                 + (api == "DX12" ? "" : "（未开 DX12，右键卡片可切）");
                    }
                    SetBody(lblFgRuntime, "运行时：" + (src ? "已就绪" : "未下载")
                                        + "　·　HAGS：" + hags
                                        + "　·　方案 A 注入 " + xe + " 个"
                                        + "　·　0.3.x 通用 " + g30 + " 个"
                                        + (ign > 0 ? "　·　被忽略 " + ign + " 个" : "") + mode);
                    lblFgRuntime.ForeColor = mode.IndexOf("未开 DX12") >= 0 ? Theme.Warn : Theme.TextDim;
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
                //  ★ 第 144 轮：上面那次 Summary 会把每款游戏的读数填进 FgVerify 的缓存，
                //    封面卡右上角的「实测 6X」就是从这里拿的 —— 所以必须让网格重画一次，
                //    否则用户退出游戏回来，数字要等下次重排才更新（看起来像"自检没生效"）。
                if (libGrid != null && !libGrid.IsDisposed) libGrid.Invalidate(true);
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
            string dir = ModernFolderPicker.PickFolder(this.Handle, "选择要纳入扫描的游戏目录（例如 F:\\黄油）", Program.AppDir);
            if (string.IsNullOrEmpty(dir)) return;
            if (Program.Cfg.DlssgExtraRoots.Contains(dir)) { Log("  该目录已在扫描列表：" + dir); return; }
            Program.Cfg.DlssgExtraRoots.Add(dir);
            Program.SetConfigList("dlssg", "extraRoots", Program.Cfg.DlssgExtraRoots);
            Log("  已加入扫描目录：" + dir + "（重新扫描中…）");
            RunScan(true);
        }

        // 主题切换：**立即生效**（原地重建控件树），不提示、不重启、不弹窗
        bool isSwitchingTheme = false;
        void DoSwitchTheme(bool dark)
        {
            if (isSwitchingTheme) return;
            string v = dark ? "dark" : "light";
            if (Cfg != null && Cfg.UiTheme == v) return;
            isSwitchingTheme = true;
            try
            {
            bool wrote = Program.SetConfigStr("ui", "theme", v);
            if (Cfg != null) Cfg.UiTheme = v;
            if (!wrote)
                Log("⚠ 主题没能写进配置文件（" + Program.DataDir + "\\config.json 不可写）"
                    + "—— 本次运行内生效，重启后会退回原主题。请到下面「数据目录」换成可写目录。");
            Theme.SetMode(v);
            RebuildChrome();
            Log("界面主题已切换为「" + (dark ? "深色" : "浅色") + "」（原地重建控件树，无需重启）");
            }
            finally { isSwitchingTheme = false; }
        }

        // 动效档位切换：立即生效 + 写配置（与主题切换同一套写法），并当场演示一次选中淡入，
        // 否则用户改成「始终开启」之后要等下一次切页才知道有没有生效。
        void DoSwitchMotion(int idx)
        {
            string v = idx == 1 ? "on" : (idx == 2 ? "off" : "auto");
            if (Cfg != null && Cfg.UiMotion == v)
            {
                if (navMenu != null) navMenu.Invalidate();
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
            if (navMenu != null) navMenu.Invalidate();
        }

        // 配置导出：把"换机/重装后需要恢复的东西"整包拷到一个文件夹
        void DoExportConfig()
        {
            string picked = ModernFolderPicker.PickFolder(this.Handle, "选择配置导出目标文件夹", Program.AppDir);
            if (string.IsNullOrEmpty(picked)) return;
            string dst = Path.Combine(picked, "GameBoost-配置备份");
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

        // 配置导入：校验后再落盘（坏文件不许进）
        void DoImportConfig()
        {
            string src = ModernFolderPicker.PickFolder(this.Handle, "选择之前导出的 GameBoost-配置备份 文件夹", Program.AppDir);
            if (string.IsNullOrEmpty(src)) return;
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

        // 数据目录迁移：秒开自绘面板（零假死）+ 现代 IFileOpenDialog 浏览 + 路径直接粘贴支持
        void DoChangeDataDir()
        {
            using (var dlg = new ChangeDataDirDialog(this))
            {
                dlg.ShowDialog(this);
            }
        }

        void RunScan(bool manual)
        {
            if (scanRunning) return;
            scanRunning = true;
            SetScanning(true);
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
                List<DlssgGame> merged = Dlssg.MergeScan(scanned, out igHits);
                //  Steam 本地缓存里就有官方封面：扫描完直接补，零网络（以前只有 MergeScanThen 那条路有，
                //  自动扫描走的是这里，所以按钮外始终补不上）
                try
                {
                    int got = CoverArt.FillLocalCovers(merged);
                    if (got > 0) { CoverArt.ClearCache(); Program.Log("  " + got + " 张封面取自 Steam 本地缓存"); }
                }
                catch { }
                e.Result = new object[] { merged, igHits };
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                scanRunning = false;
                SetScanning(false);
                lastScanAt = DateTime.Now;
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
                int fg = 0, xe = 0, act = 0, newCount = 0;
                string news = "";
                foreach (var g in games)
                {
                    if (g.Ignored) continue;
                    act++;
                    if (g.HasFrameGen) fg++;
                    if (XeMfg.ShortTag(g).Length > 0) xe++;
                    if (!old.Contains(g.Dir.ToLowerInvariant())) { news += (news.Length > 0 ? "、" : "") + g.Title; newCount++; }
                }
                //  明细写日志（含新增了哪几款），页脚只留一句短结论 —— 参照 FileButler：页脚是"状态"，
                //  不是日志窗口。以前这里把整串新增名单塞进页脚，一条提示铺满整行、还会被 96 字截断成
                //  半句话（"…新增 Chill with You Lo-Fi Story、MyDockF…"），是最像"内部工具"的一处。
                Program.Log(">>> " + (manual ? "扫描完成" : "自动扫描完成") + "：游戏目录 " + games.Count
                    + " 个（有效 " + act + " 个）；已接入方案 A 注入 " + xe + " 个；含 DLSS 帧生成组件 " + fg + " 个"
                    + (news.Length > 0 ? "；新增 " + news : ""));
                Foot("扫描完成 · " + games.Count + " 款"
                    //  首次扫描（old 为空）时"全是新增"，说出来只是噪音；只有重扫才报增量
                    + (newCount > 0 && old.Count > 0 ? "，新增 " + newCount + " 款" : ""));
                try { DoAutoFetchCovers(); } catch { }
                try { DoAutoFetchCovers(); } catch { }
                // 被忽略的条目也照样进列表（灰显），这里同时把原因说出来 ——
                // 此前静默丢弃正是"装了却识别不出来"的来源（2026-09-13 原神即此）
                if (igHits.Count > 0)
                {
                    string ign = "";
                    foreach (var g in igHits) ign += (ign.Length > 0 ? "、" : "") + g.Title + "（" + g.Dir + "）";
                    // 状态栏只给结论；完整清单写进日志文件（以及「被忽略的目录」那一页）。
                    //  原来把 3 个游戏的标题+路径全塞进状态栏，一条提示占满整行，很"内部工具"。
                    Log(">>> " + igHits.Count + " 个已忽略目录未参与扫描（列表里灰显，选中后点「开启选中」可恢复）");
                    Program.Log("    已忽略：" + ign);
                }
                RefreshFgSummary();
                //  自动扫描这条路径不会经过 MergeScanThen —— 补封面必须在这里也接一次
                AutoFetchSteamCovers(games);
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
                    FillFgList();               // 左栏列表与封面墙共用同一个搜索词
                };
            }
            searchDebounce.Stop();
            searchDebounce.Start();
        }

        //  第 124 轮：填封面墙。
        //  tile 会被整批重建（扫描/搜索/排序都会调这里），所以选中态在新建时就按 selected 落好，
        //  不靠"建完再回头点亮"——那一步在重建时最容易漏。
        void FillGameList()
        {
            if (libGrid == null) return;
            if (libGrid.InvokeRequired) { libGrid.Invoke((Action)(delegate { FillGameList(); })); return; }
            libGrid.SuspendLayout();
            try
            {
                // 读 Query 而不是 Text：占位文案不能被当成搜索词
                string q = (lstSearch == null) ? "" : lstSearch.Query.ToLowerInvariant();
                string fid = PlatId();
                bool showHidden = chkShowHidden != null && chkShowHidden.Checked;
                var shown = new List<DlssgGame>();
                foreach (DlssgGame g in games)
                {
                    if (onlyHidden) { if (!g.Hidden) continue; }
                    else if (g.Hidden && !showHidden) continue;            // 私密不进「全部」
                    if (g.Private && fid != "private") continue;           // 私密不进「全部」
                    if (fid == "fav" && !g.Favorite) continue;
                    if (fid == "private" && !g.Private) continue;
                    if (fid == "genre" && (g.Genre == null || g.Genre.Length == 0)) continue;
                    if ((fid == "steam" || fid == "epic" || fid == "wegame" || fid == "local") && g.Platform != fid) continue;
                    if (q.Length > 0 && !FuzzyHit(g, q)) continue;
                    shown.Add(g);
                }
                SortGames(shown);

                libGrid.Placeholder = games.Count == 0
                    ? (scanRunning
                        ? "正在扫描游戏…|装好的游戏会自动出现在这里，不用手动添加"
                        : "还没有游戏|点下面的按钮开始扫描，或直接把游戏文件夹拖进来")
                    : "";
                libGrid.ScanEnabled = !scanRunning;

                //  ★ 扫描/刷新会重建 DlssgGame 对象，selected 还指着上一批：
                //  不按目录重新定位，高亮和详情页会一直显示旧对象（能力/接入状态都是旧的）。
                if (selected != null)
                {
                    DlssgGame fresh = null;
                    foreach (DlssgGame g in games)
                        if (string.Equals(g.Dir, selected.Dir, StringComparison.OrdinalIgnoreCase)) { fresh = g; break; }
                    if (fresh != null) selected = fresh;
                }

                //  ★ 差分更新（2026-10-24 第 133 轮）：只增删真正变化的那几张卡。
                //  旧实现是"全删再全建"—— 搜索每敲一个字就把 12 个 tile 连同它们的封面位图
                //  整个重建一遍（CoverArt.Own 每次 new Bitmap 复制）。现在同一个目录的卡复用，
                //  位图不重新分配，只更新 Game 引用和选中态。
                var keep = new Dictionary<string, CoverTile>(StringComparer.OrdinalIgnoreCase);
                var olds = new List<CoverTile>();
                foreach (Control c in libGrid.Controls)
                {
                    var t = c as CoverTile;
                    if (t == null) continue;
                    olds.Add(t);
                    string d = t.Game == null ? null : t.Game.Dir;
                    if (!string.IsNullOrEmpty(d)) keep[d] = t;
                }
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DlssgGame g in shown) if (!string.IsNullOrEmpty(g.Dir)) want.Add(g.Dir);

                libGrid.Controls.Clear();            // 只是从集合摘下，不 Dispose
                foreach (CoverTile t in olds)
                {
                    string d = t.Game == null ? null : t.Game.Dir;
                    if (string.IsNullOrEmpty(d) || !want.Contains(d)) { try { t.Dispose(); } catch { } }
                }

                foreach (DlssgGame g in shown)
                {
                    CoverTile t;
                    string d = g.Dir == null ? "" : g.Dir;
                    if (!keep.TryGetValue(d, out t))
                    {
                        //  尺寸取网格此刻算出来的卡片尺寸（列数一变，卡片就跟着变宽变高）
                        t = new CoverTile(g, libGrid.CardW, libGrid.CardH);
                        //  间距 12（每卡 6）：6px 时整面墙糊成一片、卡片像贴纸。
                        //  格 174×255 与 CoverGrid.CellW/CellH 必须同步。
                        t.Margin = new Padding(Theme.S(6));
                        t.Chosen += delegate(object o, EventArgs e) { SelectCard((CoverTile)o); };
                        t.Launched += delegate(object o, EventArgs e)
                        {
                            var tile = (CoverTile)o;
                            if (tile.Game != null) { SelectGame(tile.Game, false); DoLaunchGame(); }
                        };
                        t.SchemeMenu += ShowSchemeMenuFor;
                    }
                    else if (t.Game != g) t.Rebind(g);
                    //  复用卡也要跟上当前格宽（窗口/列数变过之后，旧卡的尺寸可能还停在上一档）
                    if (t.Width != libGrid.CardW || t.Height != libGrid.CardH)
                        t.Size = new Size(libGrid.CardW, libGrid.CardH);
                    t.Picked = (g == selected);
                    libGrid.Controls.Add(t);
                }
                libGrid.PerformLayout();     // 让网格按新行数定高（CoverGrid 自己算）

                libShown = new List<DlssgGame>(shown);   // 键盘走位按这个顺序

                //  主视觉显示谁：当前选中的；没选中就挑最近玩过的那个。
                if (libHero != null && heroSec != null)
                {
                    DlssgGame show = selected;
                    if (show == null)
                    {
                        long best = 0;
                        for (int i = 0; i < games.Count; i++)
                        {
                            DlssgGame g = games[i];
                            if (g == null || g.Hidden || g.Ignored) continue;
                            if (g.LastPlayed > best) { best = g.LastPlayed; show = g; }
                        }
                        if (show == null)
                            for (int i = 0; i < games.Count; i++)
                                if (games[i] != null && !games[i].Hidden && !games[i].Ignored) { show = games[i]; break; }
                    }
                    libHero.SetGame(show);
                    bool vis = show != null;
                    //  ⚠ 只更新标志，不要直接设 Visible —— 那会绕过「当前是不是库视图」，
                    //  结果详情页里也会冒出这块大图。
                    if (heroHasData != vis)
                    {
                        heroHasData = vis;
                        if (libViewOn) heroSec.Visible = vis;
                        try { if (pages != null && pages[P_FG] != null) pages[P_FG].Reflow(); } catch { }
                    }
                }

                // 记下这次的实际顺序，供下一轮"集合未变则保持顺序"使用
                lastOrder.Clear();
                for (int i = 0; i < shown.Count; i++) lastOrder[OrderKey(shown[i])] = i;
                lastOrderValid = true;

                if (lstFg != null) lstFg.SetItems(games, selected);

                //  各分类的真实数量（不受当前搜索/筛选影响）—— 胶囊上写的数字必须是"库里有多少"，
                //  写"筛完之后有多少"就变成自我循环了，用户永远点不回多的那一类。
                if (chips != null)
                {
                    int[] cnt = new int[PlatIds.Length];
                    for (int i = 0; i < games.Count; i++)
                    {
                        DlssgGame g = games[i];
                        if (g == null) continue;
                        cnt[0]++;
                        if (g.Favorite) cnt[1]++;
                        if (g.Private) cnt[2]++;
                        if (g.Genre != null && g.Genre.Length > 0) cnt[3]++;
                        if (g.Platform == "steam") cnt[4]++;
                        else if (g.Platform == "epic") cnt[5]++;
                        else if (g.Platform == "wegame") cnt[6]++;
                        else if (g.Platform == "local") cnt[7]++;
                    }
                    string[] labels = new string[PlatNames.Length];
                    for (int i = 0; i < PlatNames.Length; i++) labels[i] = PlatNames[i] == null ? "" : PlatNames[i].ToString();
                    chips.SetData(labels, PlatIds, cnt);
                }
                if (lblLibStat != null)
                {
                    string cnt = shown.Count == games.Count
                        ? games.Count + " 款"
                        : shown.Count + " / " + games.Count + " 款";
                    SetBody(lblLibStat, (selected != null ? "已选 " + selected.Title + "　·　" : "") + cnt);
                }
            }
            finally { libGrid.ResumeLayout(true); }
            RefreshSideGames();
            RefreshDashSub();
        }

        //  侧栏「游戏」快捷入口的数据（第 145 轮）
        //  排序 = 最近玩过的在前；没玩过的按标题 —— 后者是为了**顺序稳定**：
        //  按扫描顺序排的话，每次扫描（进窗口就扫一次）列表都会跳一遍。
        void RefreshSideGames()
        {
            if (sideList == null || sideList.IsDisposed) return;
            var list = new List<DlssgGame>();
            if (games != null)
                foreach (DlssgGame g in games)
                    if (g != null && !g.Ignored && !g.Hidden && g.Dir != null && g.Dir.Length > 0) list.Add(g);
            list.Sort(delegate(DlssgGame a, DlssgGame b)
            {
                int c = b.LastPlayed.CompareTo(a.LastPlayed);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            if (sideGameCount != list.Count)
            {
                sideGameCount = list.Count;
                if (sideList.Parent != null) sideList.Parent.Invalidate();   // 标题右侧的数字
            }
            //  空态分两种：库里确实没有游戏（叫用户去扫描）／有但全被隐藏或忽略（说清原因，
            //  否则用户看着一行"没有游戏"会以为扫描坏了）
            sideList.EmptyText = (games == null || games.Count == 0)
                ? "还没有游戏 · 去「游戏库」扫描"
                : "没有可显示的游戏（已隐藏或忽略）";
            sideList.SetItems(list, selected == null ? "" : Lib.IdOf(selected));
        }

        //  点卡片 = **选中**（底部升起操作条），不跳页；双击才启动；要完整参数点操作条上的「详细设置」。
        //  第 124 轮这里是"点卡片就进详情"，用户随即问"那选中之后怎么调节帧生成"——
        //  跳页之后墙面没了，调参还是得来回切。现在调参的默认路径就在墙上。
        void SelectCard(CoverTile t)
        {
            if (t == null || t.Game == null) return;
            SelectGame(t.Game, false);
        }

        string PlatId()
        {
            return chips == null ? "all" : chips.SelectedId;
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
        //  词首缩写：按空格 / - / : / · / 数字边界取首字母
        //  （"Counter-Strike 2" → cs2，"崩坏：星穹铁道" → bhxqtd 由 PyInitials 负责）
        static string WordInitials(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool atWord = true;
            foreach (char ch in s)
            {
                bool sep = ch == ' ' || ch == '-' || ch == ':' || ch == '·' || ch == '_' || ch == '.'
                           || ch == '\'' || ch == '!' || ch == '(' || ch == ')';
                if (sep) { atWord = true; continue; }
                if (atWord && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                atWord = false;
            }
            return sb.ToString();
        }

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
            //  词首缩写：Counter-Strike 2 → "cs2"，搜 "cs" 命中（Steam 的行为）。
            //  这条比子序列精准得多，先于子序列判定，避免两三个字母就把半个库捞出来。
            string wi = WordInitials(g.Title);
            if (wi.Length > 0 && wi.StartsWith(q, StringComparison.Ordinal)) return true;
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


        // 统一的选中入口：左栏列表 / 封面墙 / 目标下拉 / 换主题后的恢复，全走这里。
        //  g == null = 「库主页」：右区换成封面墙。
        void SelectGame(DlssgGame g) { SelectGame(g, true); }

        //  goDetail=false：只同步高亮与视图，不切页（换主题重建后恢复现场用）
        void SelectGame(DlssgGame g, bool goDetail)
        {
            selected = g;
            //  面包屑带上当前游戏（"游戏库 · 绝区零"）—— 标题栏就不只是个装饰了
            if (page == P_FG)
                SetCrumb(g == null ? PageNames[P_FG] : PageNames[P_FG] + " · " + g.Title, P_FG);
            //  墙上那张卡的高亮（不等同于"进详情"：换方案时只换高亮，墙留在原地）
            if (libGrid != null)
                foreach (Control c in libGrid.Controls)
                {
                    CoverTile t = c as CoverTile;
                    if (t != null) t.Picked = (t.Game == g);
                }
            //  左栏高亮跟着走（直接赋 SelectedIndex 不触发 SelectedIndexChanged，不会递归）
            if (lstFg != null)
            {
                int want = g == null ? -1 : lstFg.Items.IndexOf(g);
                if (g != null && want < 0) want = -2;      // 被搜索过滤掉：不高亮任何一行
                if (lstFg.SelectedIndex != want) { lstFg.SelectedIndex = want; lstFg.Invalidate(); }
            }
            if (libHero != null && g != null) libHero.SetGame(g);
            if (sideList != null) sideList.SetSelected(g == null ? "" : Lib.IdOf(g));
            if (actBar != null)
            {
                actBar.SetGame(g);
                ShowActionBar(g != null && libViewOn && page == P_FG);
            }
            if (g == null) ShowLibView(true);
            else if (goDetail) { if (page != P_FG) Switch(P_FG); ShowLibView(false); }
            RefreshFgSummary();
        }

        // ---- 库标记操作 ----
        // 卡片右键菜单的动作分发（动作名见 GameCard.BuildMenu）
        void OnCardMenu(string act)
        {
            if (selected == null) return;
            if (act == "launch") { DoLaunchGame(); return; }
            if (act == "dx12") { DoToggleDx12(); return; }
            if (act == "launchcfg") { ShowLaunchConfigDialog(); return; }
            if (act == "fgcfg") { ShowFgConfigDialog(); return; }
            if (act == "dxapi") { DoCheckRenderApi(); return; }
            if (act == "opendir") { DoOpenGameDir(); return; }
            if (act == "fav") { DoToggleFavorite(); return; }
            if (act == "rename") { DoRenameGame(); return; }
            if (act == "priv") { DoTogglePrivate(); return; }
            if (act == "hid") { DoToggleHidden(); return; }
            if (act == "setcover") { DoSetCover(); return; }
            if (act == "pastecover") { DoPasteCover(); return; }
            if (act == "clearcover") { DoClearCover(); return; }
            if (act == "dlcover") { DoRedownloadCover(); return; }
            if (act == "webcover") { DoWebCover(); return; }
            if (act == "coverdir") { DoOpenCoversDir(); return; }
            if (act == "remove") { DoRemoveGame(); return; }
            if (act == "fg")
            {
                Switch(P_FG);
                Log("  已切到「游戏库」，目标游戏已选中：" + selected.Title + " —— 点「开启选中」即可");
            }
        }

        // 帧生成页左栏：按搜索词过滤（空串 = 全部）
        void FillFgList()
        {
            if (lstFg == null) return;
            //  ⚠ 这里必须和库墙用**同一套**匹配（2026-10-22）：原来只做 Title 子串，
            //  "cs" 搜不到 "Counter-Strike 2"（Steam 能搜到，用户直接拿这个对比）。
            string q = lstSearch == null ? "" : (lstSearch.RealText ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) { lstFg.SetItems(games, selected); return; }
            var hit = new List<DlssgGame>();
            foreach (DlssgGame g in games)
                if (g != null && FuzzyHit(g, q)) hit.Add(g);
            lstFg.SetItems(hit, selected);
        }

        // 帧生成页左栏的右键菜单：只放常用项（完整 13 项在卡片页，那边现在没有入口了）。
        //  动作全部转给 OnCardMenu，避免两套逻辑各改各的。
        void ShowRowMenu(Point screen)
        {
            if (selected == null) return;
            var m = MenuFactory.New();
            m.Renderer = new ThemedMenuRenderer();
            m.ShowImageMargin = false;
            Action<string, string> add = delegate(string text, string act)
            {
                var it = new ToolStripMenuItem(text);
                it.Click += delegate { OnCardMenu(act); };
                m.Items.Add(it);
            };
            add("启动游戏", "launch");
            add("打开目录", "opendir");
            if (!Program.ShareBuild)
            {
                //  ★ 第 148 轮：把「DX12 模式启动」放回菜单里。
                //  它一直是**死的**：`OnCardMenu("dx12")` 的分发在，`DoToggleDx12()` 也在，
                //  但没有任何菜单项发这个动作 —— 而帧生成页那行提示还写着"右键卡片可切"
                //  （`GameCard.BuildMenu` 的注释也是这么说的）。用户照提示右键，找不到。
                //  ⚠ 不用 Checked 属性：自绘渲染器没实现 OnRenderItemCheck，对勾画不对。
                //    用动词表达状态，和下面的「取消收藏 / 收藏」同一套写法。
                m.Items.Add(new ToolStripSeparator());
                add(Lib.HasDx12Arg(selected) ? "关闭 DX12 模式启动" : "开启 DX12 模式启动", "dx12");
                add("启动参数与环境变量…", "launchcfg");
                add("帧生成参数…", "fgcfg");
            }
            m.Items.Add(new ToolStripSeparator());
            add(selected.Favorite ? "取消收藏" : "收藏", "fav");
            add(selected.Private ? "取消私密" : "私密", "priv");
            add(selected.Hidden ? "取消隐藏" : "隐藏", "hid");
            if (!Program.ShareBuild)
            {
                m.Items.Add(new ToolStripSeparator());
                var miCover = new ToolStripMenuItem("封面设置");
                Action<string, string> addSub = delegate(string text, string act)
                {
                    var it = new ToolStripMenuItem(text);
                    it.Click += delegate { OnCardMenu(act); };
                    miCover.DropDownItems.Add(it);
                };
                addSub("联网重新搜索封面", "webcover");
                addSub("选择本地图片…", "setcover");
                addSub("粘贴剪贴板图片（Ctrl+V）", "pastecover");
                addSub("重新下载官方封面", "dlcover");
                miCover.DropDownItems.Add(new ToolStripSeparator());
                addSub("清除自定义封面", "clearcover");
                m.Items.Add(miCover);
            }
            m.Items.Add(new ToolStripSeparator());
            add("从列表移除", "remove");
            m.Show(screen);
        }

        // 换完封面之后两处都要刷新：封面墙（FillGameList）+ 详情页横幅（RefreshFgSummary 里的 SetHero）。
        //  以前只刷前者，从详情页改完封面横幅还是旧图，看着像"没生效"。
        void AfterCoverChanged()
        {
            FillGameList();
            try { RefreshFgSummary(); } catch { }
        }

        // 手动指定封面：本地任意图片，拷成 <游戏名>.png（优先于官方封面）
        void DoClearCover()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            CoverArt.ClearCover(selected);
            AfterCoverChanged();
        }

        void DoSetCover()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "为「" + selected.Title + "」选择封面图片（建议竖版 2:3）";
                dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string err = CoverArt.SetManualCover(selected, dlg.FileName);
                if (err.Length == 0)
                {
                    Log("  已设置封面：" + selected.Title + " ← " + Path.GetFileName(dlg.FileName));
                    AfterCoverChanged();
                }
                else Log("  设置封面失败：" + err);
            }
        }

        //  粘贴封面（Ctrl+V，2026-10-23）：截图 → 直接贴成封面，不用先存盘。
        //  另一条路是拖图片到窗口（见 OnPackDropped），两条都走 SetManualCoverImage。
        void DoPasteCover()
        {
            if (Program.ShareBuild) return;
            if (selected == null) { Log("  粘贴封面：请先在左侧选中一个游戏"); return; }
            string from = "";
            Image img = null;
            try
            {
                try { if (Clipboard.ContainsImage()) { img = Clipboard.GetImage(); from = "剪贴板截图"; } }
                catch { }
                if (img == null)
                {
                    try
                    {
                        if (Clipboard.ContainsFileDropList())
                        {
                            var files = Clipboard.GetFileDropList();
                            foreach (string f in files)
                            {
                                if (f != null && IsImageFile(f) && File.Exists(f))
                                {
                                    using (Image s = Image.FromFile(f)) img = new Bitmap(s);
                                    from = Path.GetFileName(f);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (img == null) { Log("  剪贴板里没有图片（先截图，或复制一个图片文件）"); return; }
                using (img)
                {
                    string err = CoverArt.SetManualCoverImage(selected, img);
                    if (err.Length == 0)
                    {
                        Log("  已设置封面（粘贴）：" + selected.Title + " ← " + from);
                        AfterCoverChanged();
                    }
                    else Log("  粘贴封面失败：" + err);
                }
            }
            catch (Exception ex) { Log("  粘贴封面失败：" + ex.Message); }
        }

        //  剪贴板里有没有"能当封面"的东西（决定 Ctrl+V 该不该被我们接走）
        static bool ClipboardHasImage()
        {
            try
            {
                if (Clipboard.ContainsImage()) return true;
                if (Clipboard.ContainsFileDropList())
                    foreach (string f in Clipboard.GetFileDropList())
                        if (f != null && IsImageFile(f)) return true;
            }
            catch { }
            return false;
        }

        //  Ctrl+V 不能抢输入框的粘贴：光标在搜索框 / 文本框里时一律放行
        bool FocusInTextField()
        {
            try
            {
                Control c = ActiveControl;
                while (c != null)
                {
                    if (c is TextBoxBase || c is ComboBox) return true;
                    c = c.Parent;
                }
            }
            catch { }
            return false;
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
            try { if (File.Exists(CoverArt.GenFile(selected.Id))) File.Delete(CoverArt.GenFile(selected.Id)); } catch { }
            string oldNm = CoverArt.SafeName(selected.Title);
            try { if (File.Exists(Path.Combine(CoverArt.CoversDir, oldNm + ".jpg"))) File.Delete(Path.Combine(CoverArt.CoversDir, oldNm + ".jpg")); } catch { }
            try { if (File.Exists(Path.Combine(CoverArt.CoversDir, oldNm + ".png"))) File.Delete(Path.Combine(CoverArt.CoversDir, oldNm + ".png")); } catch { }
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

        bool fetchAllCoversRunning = false;
        void DoFetchAllCovers()
        {
            if (fetchAllCoversRunning) { Log("  批量获取封面正在进行中…"); return; }
            if (games == null || games.Count == 0) { Log("  库中暂无游戏"); return; }
            fetchAllCoversRunning = true;
            Log(">>> 开始一键补全全库封面（后台并行检索）…");
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                int total = games.Count, done = 0, fetched = 0;
                for (int i = 0; i < total; i++)
                {
                    DlssgGame g = games[i];
                    if (g == null) continue;
                    // 若已有自定义/官方封面文件，跳过
                    string id = g.Id ?? "";
                    string cardJpg = id.Length > 0 ? CoverArt.CardFile(id) : "";
                    bool hasCover = false;
                    foreach (string cand in CoverArt.GetTitleCandidates(g.Title))
                    {
                        if (System.IO.File.Exists(System.IO.Path.Combine(CoverArt.CoversDir, cand + ".jpg")) ||
                            System.IO.File.Exists(System.IO.Path.Combine(CoverArt.CoversDir, cand + ".png")))
                        { hasCover = true; break; }
                    }
                    if (!hasCover && cardJpg.Length > 0 && System.IO.File.Exists(cardJpg)) hasCover = true;
                    if (!hasCover)
                    {
                        try
                        {
                            string appid = CoverArt.AppIdOf(g);
                            bool ok = false;
                            if (appid.Length > 0) ok = CoverArt.DownloadSteam(appid, g.Id);
                            if (!ok) ok = CoverArt.SearchWebCover(g).Length == 0;
                            if (ok) fetched++;
                        }
                        catch { }
                    }
                    done++;
                }
                e.Result = fetched;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                fetchAllCoversRunning = false;
                CoverArt.ClearCache();
                int fetched = (e.Result is int) ? (int)e.Result : 0;
                Log("✓ 全库封面补全完成，新下载 " + fetched + " 款游戏封面");
                FillGameList();
                try { RefreshFgSummary(); } catch { }
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
        }

        string Prompt(string title, string hint, string def)
        {
            using (Form f = new Form())
            {
                Fx.FadeOnClose(f);   // 关闭淡出（关动画时零行为）
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

                var lb = new TxtLbl();
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

        bool autoCoverRunning;
        void DoAutoFetchCovers()
        {
            if (Program.ShareBuild || autoCoverRunning || coverRunning) return;
            if (!Cfg.DlssgOnlineCovers) return;
            if (games == null || games.Count == 0) return;

            autoCoverRunning = true;
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                int fetched = 0;
                foreach (DlssgGame g in new List<DlssgGame>(games))
                {
                    if (g == null || CoverArt.HasRealCover(g)) continue;
                    if (g.Id == null || g.Id.Length == 0) g.Id = Lib.IdOf(g);

                    string appid = g.AppId == null ? "" : g.AppId;
                    bool ok = false;
                    if (appid.Length > 0 && CoverArt.CopyLocalSteam(appid, g.Id)) ok = true;
                    if (!ok && appid.Length > 0) ok = CoverArt.DownloadSteam(appid, g.Id);
                    if (!ok)
                    {
                        string alt = CoverArt.SearchAppId(g.Title);
                        if (alt.Length > 0 && alt != appid && CoverArt.DownloadSteam(alt, g.Id))
                        {
                            Lib.RememberAppId(g, alt);
                            ok = true;
                        }
                    }
                    if (!ok)
                    {
                        ok = CoverArt.SearchWebCover(g).Length == 0;
                    }
                    if (ok)
                    {
                        fetched++;
                        try { if (File.Exists(CoverArt.GenFile(g.Id))) File.Delete(CoverArt.GenFile(g.Id)); } catch { }
                    }
                }
                e.Result = fetched;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                autoCoverRunning = false;
                int n = (e.Result is int) ? (int)e.Result : 0;
                if (n > 0)
                {
                    CoverArt.ClearCache();
                    FillGameList();
                    Program.Log("  自动补全封面完成：已下载 " + n + " 款游戏的高清封面");
                }
            };
            bw.RunWorkerAsync();
        }


        // 联网补封面（Steam 官方 CDN，走设置里的代理）；后台跑，完成刷新列表
        void DoFetchCovers()
        {
            if (Program.ShareBuild) { Log("  分享版不含封面功能（游戏列表用信息行显示）"); return; }
            if (coverRunning) { Log("  封面获取正在进行中…"); return; }
            bool online = Cfg.DlssgOnlineCovers;
            coverRunning = true;
            Log(">>> 开始获取封面（先读 Steam 本地缓存" + (online ? "，缺失的再走官方 CDN" : "；联网封面已关闭，只取本地") + "）…");
            var bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                int ok = 0, tried = 0, web = 0, local = 0;
                foreach (DlssgGame g in new List<DlssgGame>(games))
                {
                    if (CoverArt.HasRealCover(g)) continue;
                    if (g.Id == null || g.Id.Length == 0) g.Id = Lib.IdOf(g);
                    string appid = g.AppId == null ? "" : g.AppId;
                    tried++;
                    if (appid.Length > 0 && CoverArt.CopyLocalSteam(appid, g.Id)) { ok++; local++; continue; }
                    if (!online) continue;
                    // 无论是否有 appid，先尝试 Steam 别名反查
                    string alt = CoverArt.SearchAppId(g.Title);
                    if (alt.Length > 0 && alt != appid && CoverArt.DownloadSteam(alt, g.Id))
                    {
                        Lib.RememberAppId(g, alt);
                        ok++;
                        continue;
                    }
                    // 非 Steam 大作或独立游戏：全网检索 2:3 官方高清竖版海报
                    if (CoverArt.SearchWebCover(g).Length == 0) { ok++; web++; }
                    if (tried >= 30) break;
                }
                e.Result = new int[] { ok, local };
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                coverRunning = false;
                int[] r = e.Result as int[];
                int ok = r == null ? 0 : r[0];
                int local = (r == null || r.Length < 2) ? 0 : r[1];
                Log("  " + (ok > 0 ? "已获取 " + ok + " 张封面" + (local > 0 ? "（其中本地 " + local + " 张）" : "")
                                  : "没有新封面（Steam 本地无缓存 / 离线 / 已全部就绪）"));
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
                //  Steam 本地缓存里有官方封面 → 直接补（零网络，秒级），用户不用再点「获取封面」
                try
                {
                    int got = CoverArt.FillLocalCovers(merged);
                    if (got > 0) { CoverArt.ClearCache(); Program.Log("  " + got + " 张封面取自 Steam 本地缓存"); }
                }
                catch { }
                try { BeginInvoke((Action)(delegate { games = merged; if (after != null) after(); })); }
                catch { }

                AutoFetchSteamCovers(merged);
            });
        }

        //  ★ 缺失封面从 Steam 官方 CDN 补齐（2026-10-23，P3）。
        //  ⚠ 两条合并路径都要调：手动扫描走 MergeScanThen，**自动扫描走 BackgroundWorker 的
        //    完成回调**（第一次只挂在 MergeScanThen 上，结果自动扫描根本补不到 —— 实测无日志）。
        //  刻意放在"列表已经刷出来之后"的第二趟：网络慢时不能挡住封面墙出现。
        void AutoFetchSteamCovers(List<DlssgGame> list)
        {
            if (Program.ShareBuild || Cfg == null || !Cfg.DlssgOnlineCovers || list == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0;
                try { n = CoverArt.FillSteamCovers(list, true, 30); } catch { }
                if (n <= 0) return;
                try { CoverArt.ClearCache(); } catch { }
                try
                {
                    BeginInvoke((Action)(delegate
                    {
                        Program.Log("  " + n + " 张封面已从 Steam 官方补齐");
                        FillGameList();
                    }));
                }
                catch { }
            });
        }

        void DoAddGame()
        {
            //  2026-11-01：OpenFileDialog 在本机会挂死（shell 层，与导入资源包同款问题）——
            //  改用产品现成的文件夹浏览器 + 自家 exe 列表挑件（顶层 + 一层子目录，
            //  覆盖"启动器在根、游戏本体在子目录"的常见结构，如异环 NTELauncher\NTEGame.exe）。
            string folder = ModernFolderPicker.PickFolder(this.Handle,
                "选择游戏所在的文件夹（里面的游戏 exe 会被列出）",
                lastAddDir.Length > 0 ? lastAddDir : "");
            if (string.IsNullOrEmpty(folder)) return;
            lastAddDir = folder;

            var exes = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(folder, "*.exe"))
                    if (!Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) exes.Add(f);
                foreach (string d in Directory.GetDirectories(folder))
                    try
                    {
                        foreach (string f in Directory.GetFiles(d, "*.exe"))
                            if (!Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) exes.Add(f);
                    }
                    catch { }
            }
            catch (Exception ex) { Log("  读不了这个文件夹：" + ex.Message); return; }
            if (exes.Count == 0)
            {
                MessageBox.Show(this, "这个文件夹（含一层子目录）里没有 exe。", "添加游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            exes.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            if (exes.Count == 1) { DoAddGameExe(exes[0]); return; }
            ShowSimplePickList("添加游戏 · " + Path.GetFileName(folder.TrimEnd('\\')) + "（" + exes.Count + " 个 exe）",
                exes, delegate(string f)
                {
                    return Path.GetFileName(f);
                },
                delegate(string pick) { DoAddGameExe(pick); });
        }

        void DoAddGameExe(string exe)
        {
            Log(">>> 添加自定义游戏：" + Dlssg.AddCustomGame(exe));
            MergeScanThen(games, delegate { FillGameList(); RefreshFgSummary(); });
        }

        //  通用单选列表窗（文件夹浏览之后挑一个文件）：标题 + 行格式化 + 选中回调。双击 = 确认。
        void ShowSimplePickList(string title, List<string> items, Func<string, string> fmt, Action<string> onPick)
        {
            var dlg = new Form();
            Fx.FadeOnClose(dlg);
            dlg.Text = title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.ShowInTaskbar = false; dlg.ShowIcon = false;
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(360));
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var tip = new TxtLbl();
            tip.Text = "点选一项 → 「确定」；双击同效。";
            tip.AutoSize = true;
            tip.ForeColor = Theme.TextDim;
            tip.Font = Theme.F(9f);
            tip.Location = new Point(Theme.S(16), Theme.S(12));
            dlg.Controls.Add(tip);

            var lb = new ListBox();
            lb.Font = Theme.F(9.5f);
            lb.ItemHeight = Theme.S(22);
            lb.BorderStyle = BorderStyle.FixedSingle;
            lb.HorizontalScrollbar = true;
            foreach (string it in items)
            {
                string row = fmt(it);
                int maxW = lb.ClientSize.Width - Theme.S(24);
                while (row.Length > 6 && TextRenderer.MeasureText(row, lb.Font).Width > maxW)
                    row = row.Substring(0, row.Length - 2).TrimEnd() + "…";
                lb.Items.Add(row);
            }
            if (items.Count > 0) lb.SelectedIndex = 0;
            lb.Location = new Point(Theme.S(16), Theme.S(40));
            lb.Size = new Size(dlg.ClientSize.Width - Theme.S(32), dlg.ClientSize.Height - Theme.S(108));
            dlg.Controls.Add(lb);

            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnCancel.Location = new Point(dlg.ClientSize.Width - Theme.S(100), dlg.ClientSize.Height - Theme.S(52));
            btnCancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(btnCancel);

            var btnGo = new FlatBtn(); btnGo.Text = "确定"; btnGo.Kind = BtnKind.Primary; btnGo.Width = Theme.S(110);
            btnGo.Location = new Point(dlg.ClientSize.Width - Theme.S(224), dlg.ClientSize.Height - Theme.S(52));
            btnGo.Click += delegate
            {
                if (lb.SelectedIndex < 0 || lb.SelectedIndex >= items.Count) return;
                string pick = items[lb.SelectedIndex];
                dlg.Close();
                onPick(pick);
            };
            dlg.Controls.Add(btnGo);
            lb.DoubleClick += delegate { btnGo.PerformClick(); };

            dlg.ShowDialog(this);
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
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
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
            sec.Block(BoxOf(lv), Theme.S(200) + Theme.S(30));
            var lblCount = sec.Body("");
            sec.Body("只记忽略，不动文件；「放行选中」可恢复收录。");

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
                        if (!Directory.Exists(d)) { st = "目录不存在（可放行清理）"; c = Theme.TextDim; }
                        else
                        {
                            var g = Dlssg.Inspect(d, true);
                            if (g != null) { st = "可收录 · " + g.Exe; c = Theme.Ok; }
                            else if (Dlssg.IsPlatformClient(Path.GetFileName(d.TrimEnd('\\', '/'))))
                            { st = "对战平台（不是游戏，已排除）"; c = Theme.TextDim; }
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
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
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
            sec.Block(BoxOf(lv), Theme.S(230) + Theme.S(30));
            var lblSum = sec.Body("");
            sec.Body("带 * 的是目录修改时间；回收站可还原，不硬删。");
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
                            row.ForeColor = Theme.TextDim;
                            row.SubItems[1].ForeColor = Theme.TextSafe(Theme.Warn);
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

        //  扫盘（备份 / 隔离区整棵目录树）放后台，扫完再回填。
        //  以前这里直接 Junk.Scan() 同步跑，而这个方法是在**建设置页**时调的 ——
        //  于是每建一次设置页（启动、换主题、切页签重建）都白卡 80~100 ms。
        void SetJunkLabel()
        {
            if (lblJunk == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<JunkItem> all;
                try { all = Junk.Scan(); }
                catch { return; }
                try
                {
                    if (IsDisposed) return;
                    //  建页阶段句柄可能还没创建（MainForm 构造函数里就调到这里了），
                    //  那时 BeginInvoke 会抛异常被吞掉 → 标签永远停在"统计中…"。
                    //  先存起来，等 OnLoad（句柄一定已建）再回填。
                    if (!IsHandleCreated) { junkPending = all; return; }
                    BeginInvoke((Action)(delegate { ApplyJunkLabel(all); }));
                }
                catch { }        // 窗口已关 / 控件已重建 —— 丢弃这次结果即可
            });
        }

        //  回填（必须在 UI 线程）：控件可能已被 RebuildChrome 换掉，逐个判空 + IsDisposed
        //  建页时扫盘先结束、句柄又还没建好 → 结果暂存这里，由 OnLoad 末尾回填一次
        List<JunkItem> junkPending;

        void ApplyJunkLabel(List<JunkItem> all)
        {
            if (all == null) return;
            if (lblJunk == null || lblJunk.IsDisposed) return;
            try
            {
                // 下拉项按当前统计回填条数/体积 —— 默认的 30 天档在新装的机器上"一条都清不掉"
                //  （备份都还没满 30 天），不回填的话用户只会以为这功能没反应。
                if (cbJunkAge != null && !cbJunkAge.IsDisposed)
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
            string cur = Plan.Current(FgDir(selected));
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
                       + "目录：" + FgDir(selected);
            if (MessageBox.Show(this, msg, "停放遗留代理",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                Log(">>> 停放遗留项：用户取消，未做任何改动");
                return;
            }
            string r;
            try { r = Plan.ParkForeignReport(FgDir(selected)); }
            catch (Exception ex) { r = "异常：" + ex.GetType().Name + "：" + ex.Message; }
            Log(">>> 停放遗留项：" + selected.Title + " —— " + r.Replace("\n", "\n      "));
            Log("  " + Plan.PatrolLine(FgDir(selected)));
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
            string fgSrc = Dlssg030.SourceEntryPath(FgDir(selected));
            if (!TrustGateFile(fgSrc, selected.Title))
            {
                Log("  已取消：源代理未通过签名核验（" + fgSrc + "）");
                return;
            }
            string r = Plan.SwitchTo(FgDir(selected), Plan.D030, true);
            Log("  " + r);
            FillGameList();
            RefreshFgSummary();
            if (string.Equals(Plan.EntryName(XeMfg.Detect(FgDir(selected))), Dlssg.D3d12Entry, StringComparison.OrdinalIgnoreCase))
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
                || File.Exists(Path.Combine(FgDir(selected), Dlssg.D3d12Entry)))
            {
                string dv = Dlssg.VerifyD3d12(FgDir(selected));
                Log("  d3d12 三件套：" + (dv.Length == 0
                    ? "完整（代理 3,584 B + 系统原件 + 载体，导入链闭合）"
                    : "异常 → " + dv));
            }
            else
            {
                string dh = Dlssg.D3d12Hint();
                if (dh.Length > 0) Log("  d3d12 入口：" + dh);
            }
            var residue = Dlssg.ScanResidue(FgDir(selected));
            if (residue.Count > 0)
                Log("  ⚠ 发现 " + residue.Count + " 个反作弊隔离残留（形如 version.dll.数字）→ 点「清理残留」删除");
            var v = Dlssg.CheckLogs(FgDir(selected));
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
            //  ★ 2026-10-31：日志打的是**实际生效**的参数（磁盘设置 + 内置预设合成后的结果）——
            //    只打内存 g.LaunchArgs 时，预设生效了日志却看不出来，等于让人猜。
            string largs = Lib.EffectiveLaunchArgs(selected);
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
        // 方案下拉里用的短名：只有"方案名 + 版本"，不带解释。
        static string SchemeShort(string a)
        {
            if (a == "opti") return "OptiScaler 方案 A";
            if (a == "asi") return "异环专用 ASI 变体";
            if (a == "030" || a == "030spoof") return "DLSS MFG 0.3.5";
            if (a == "040") return "DLSS MFG " + Dlssg040.Ver;
            if (a == "nvbr") return "MFG Bridge 方案 B";
            if (a == "dx12") return "需切 DX12";
            return "无需安装";
        }

        // 方案选择器：只显示选中的那一套（"自动"= 按当前游戏的推荐结果）
        void ApplySchemeVis()
        {
            if (cbScheme == null) return;
            int i = cbScheme.SelectedIndex;
            string a = curRecAction == null ? "" : curRecAction;
            //  第一项跟着推荐走（「自动 · OptiScaler 方案 A」）：换游戏时下拉本身就换名字，
            //  一眼就看到这台机器该装哪套 —— 这就是"根据不同游戏推荐不同"，不用再读正文。
            //  用 Items.Set（就地改文案）而不是重建列表：重建会把 SelectedIndex 复位。
            cbScheme.Items.Set(0, "自动 · " + SchemeShort(a));
            bool v030 = i == 2 || (i == 0 && (a == "030" || a == "asi" || a == "030spoof"));
            bool v040 = i == 3 || (i == 0 && a == "040");
            bool vNv = i == 4 || (i == 0 && a == "nvbr");
            //  Smooth Motion 没有"自动"推荐（推荐引擎不产出它），只有手动选才显示；
            //  没选中游戏时"自动"一套都不显示，只留上方的推荐卡说明该做什么。
            bool vSm = i == 5;

            //  这里只登记"该不该显示"，真正落 Visible 的是 ApplyDetailVis ——
            //  它还要叠加"当前在哪个页签"，直接写 Visible 会被页签切换覆盖掉。
            if (s030Sec != null) secWant[s030Sec] = v030;
            if (s040Sec != null) secWant[s040Sec] = v040;
            if (sNvSec != null) secWant[sNvSec] = vNv;
            if (sSmSec != null) secWant[sSmSec] = vSm;

            //  推荐高亮：标题染色 = "推荐引擎点的是它"。手动选了别的方案时，
            //  看到的区块标题是暗的 —— 这本身就是一个信息（"你现在用的不是推荐"）。
            if (s030Sec != null) s030Sec.Mark(a == "030" || a == "030spoof" || a == "asi");
            if (s040Sec != null) s040Sec.Mark(a == "040");
            if (sNvSec != null) sNvSec.Mark(a == "nvbr");

            ApplyDetailVis();
            UpdatePanelButtons();          // 按钮文案跟着下拉走（手选方案时缀上方案名）
            try { if (pages != null && pages[P_FG] != null) pages[P_FG].Reflow(); } catch { }
        }

        //  Rec.Note 是"一行一条"的多行文本 → 排成项目符号列表。
        //  以 ⚠ 开头的行保留原样（它本身就是标记，再加点会重复）。
        static string Bullet(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.StartsWith("⚠") ? t : "•  " + t);
            }
            return sb.ToString();
        }

        //  Rec.Note 里"会咬人"的那一行（⚠ 开头）。
        //  面板的提示行只放它：其余（刷新率甜点 / 前置条件）都是读一次就够的背景，
        //  放第一屏只会把两个按钮挤下去 —— 用户在「诊断」页签里能看到全文。
        static string WarnLine(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            foreach (string raw in note.Split('\n'))
            {
                string t = raw.Trim();
                if (t.StartsWith("⚠")) return t;
            }
            return "";
        }

        void RefreshRec()
        {
            var rec = Dlssg.BuildRec(selected);
            //  面板 = 参考图那五行：标题（下拉标签）/ 当前 / 一条提示 / 两个整宽动作。
            //  星级、判定、"推荐：…"这类评分语言一律不进面板：结论已经在下拉第一项
            //  （「自动 · OptiScaler 方案 A」）和按钮上，再写一遍就是用户说的"废话"。
            if (lblFgState != null)
            {
                string st = rec.State == null ? "" : rec.State.Trim();
                SetBody(lblFgState, st.Length == 0 ? "" : "当前：" + st);
            }
            if (lblFgInfo != null)
            {
                //  提示行 = 会咬人的那一句（⚠），没有的话退一步解释"按钮为什么是灰的"：
                //  只剩一个不能点的按钮、却不说原因，比写一句话更让人困惑。
                string info = WarnLine(rec.Note);
                if (info.Length == 0)
                {
                    string ca = ChosenAction();
                    if (ca.Length == 0 || ca == "none") info = rec.Head;
                }
                if (info.Length > 0 && !info.StartsWith("⚠")) info = "ⓘ " + info;
                SetBody(lblFgInfo, info);
            }
            //  解释性文字全部落在「诊断」页签（为什么是这套 / 依据），不占概览
            if (lblWhy != null) SetBody(lblWhy, rec.Why == null ? "" : rec.Why);
            if (lblRecNote != null) SetBody(lblRecNote, Bullet(rec.Note));

            //  推荐结果变了 → "自动"要跟着换一套方案卡片（切游戏时这套必须同步，否则显示的还是上一个游戏的方案）
            curRecAction = rec.Action;

            //  大横幅也跟着走：标题 = 游戏名，副标题 = 平台 + 推荐结论，背景 = 封面
            if (headFg != null && headFg.Hero)
            {
                if (selected == null)
                {
                    headFg.SetHero("游戏库", "从左侧选一个游戏", null);
                    headFg.Meta = ""; headFg.Note = "";
                }
                else
                {
                    string sub = Lib.PlatformLabel(selected.Platform);
                    if (curRecAction == "asi") sub += " · 推荐 ASI 变体";
                    else if (curRecAction == "030" || curRecAction == "030spoof") sub += " · 推荐 0.3.5";
                    else if (curRecAction == "040") sub += " · 推荐 " + Dlssg040.Ver;
                    else if (curRecAction == "opti") sub += " · 推荐方案 A";
                    else if (curRecAction == "nvbr") sub += " · 推荐方案 B";
                    else if (curRecAction == "dx12") sub += " · 需切换 DX12";
                    //  ⚠ 尺寸别按"横幅看起来多大"要（以前要 800×300）：Fit 会先按这个比例裁一刀，
                    //    Compose 再按自己的比例裁第二刀，两刀下来画面被削掉一圈，而且 800 宽
                    //    在 900+ 的详情区里是**拉伸**上去的 —— 用户看到的"封面有点糊"就是这个。
                    //    1200×540 比横幅略大、比例贴近 16:9，两刀合一刀，且永远走缩小。
                    //  只传"最大宽度"：取景（比例）由 HeadBox.Compose 按实际横幅裁一次，
                    //  素材本身不再被预裁 —— 见 CoverArt.Hero 的注释。
                    headFg.SetHero(selected.Title, sub,
                        Program.ShareBuild ? null : CoverArt.Hero(selected, Theme.S(1200)));

                    //  操作条分左右两半：左边「平台 · 推荐方案」，右端一个状态点。
                    //  以前这行放的是整条游戏目录路径 —— 又长、又和下面「游戏信息」卡重复，
                    //  而且只占左边一小截，右半条横幅全是空的（用户："这一块空空的"）。
                    headFg.Meta = sub;
                    //  横幅右端不再重复「当前：…」—— 那行原样写在下面那张卡里，
                    //  两处一模一样的状态是"拼接感"的典型来源（一条信息出现在两个高度上）。
                    headFg.Note = "";
                    headFg.NoteColor = Theme.TextDim;
                }
            }
            ApplySchemeVis();
            UpdatePanelButtons();
        }

        // ---------------------- 面板上的两个整宽动作 ----------------------
        //  下拉选的是哪一套（"自动" = 推荐结果）。索引与 BuildPageFrameGen 里的 Items.AddRange 一一对应。
        string ChosenAction()
        {
            int i = cbScheme == null ? 0 : cbScheme.SelectedIndex;
            if (i == 1) return Plan.Opti;
            if (i == 2) return Plan.D030;
            if (i == 3) return Plan.D040;
            if (i == 4) return Plan.Nvbr;
            if (i == 5) return "sm";
            return curRecAction == null ? "" : curRecAction;
        }

        //  这个游戏目录里现在有没有我们的东西（任何一代）。只用来决定「卸载补丁」是否可点。
        static bool InstalledAny(DlssgGame g)
        {
            if (g == null) return false;
            try { if (Plan.Current(g.Dir) != Plan.None) return true; } catch { }
            return g.Installed;
        }

        void UpdatePanelButtons()
        {
            if (btnInstall != null)
            {
                string a = ChosenAction();
                //  dx12 那一档不是"装补丁"，是"换个启动参数"，按钮文案得说实话
                if (a == "dx12") btnInstall.Text = "开启 DX12 启动";
                else
                {
                    //  手选了某一套（不是"自动"）就把方案名缀在按钮上 ——
                    //  那一套的详情在「参数」页签里，概览上看不到，按钮是唯一能确认"我现在选的是它"的地方。
                    string tag = ManualSchemeTag();
                    btnInstall.Text = tag.Length == 0 ? "安装并应用" : "安装并应用（" + tag + "）";
                }
                btnInstall.Enabled = selected != null && a.Length > 0 && a != "none";
            }
            if (btnUninstall != null)
                btnUninstall.Enabled = selected != null && InstalledAny(selected);
        }

        //  下拉里手选的那一套的短名（"自动"→ 空串）
        string ManualSchemeTag()
        {
            int i = cbScheme == null ? 0 : cbScheme.SelectedIndex;
            if (i == 1) return "方案 A";
            if (i == 2) return Dlssg030.Ver;
            if (i == 3) return Dlssg040.Ver;
            if (i == 4) return "方案 B";
            if (i == 5) return "Smooth Motion";
            return "";
        }

        //  执行一个动作（opti / 030 / 040 / asi / 030spoof / nvbr / sm / dx12）。
        //  「自动」的推荐结果与面板上下拉手选的方案都走这一条 —— 分成两份实现迟早会走岔。
        void ApplyAction(string a)
        {
            if (selected == null) { Log("  先从左侧游戏栏选一个游戏"); return; }
            if (string.IsNullOrEmpty(a) || a == "none") return;
            if (a == Plan.Opti) DoXeDeploy();
            else if (a == Plan.D030) Do030Deploy();
            else if (a == Plan.D040) Do040Deploy();
            else if (a == Plan.Nvbr) DoNvDeploy();
            else if (a == "sm") DoSmoothMotion(true);
            else if (a == "asi") DoAsiDeploy();
            else if (a == "dx12")
            {
                Lib.SetDx12(selected, true);
                Log("  已开启 DX12 模式启动（" + Lib.ArgDx12 + "）：" + selected.Title);
                Log("  下一步：点「启动游戏」；启动后本工具会自动确认是否真的跑在 DX12。");
                FillGameList();
            }
            else if (a == "030spoof")
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
        }

        //  「安装并应用」：按面板下拉的当前选择执行
        void DoInstallCurrent()
        {
            if (selected == null) { Log("  先从左侧游戏栏选一个游戏"); return; }
            string a = ChosenAction();
            if (a.Length == 0 || a == "none")
            {
                var rec = Dlssg.BuildRec(selected);
                MessageBox.Show(rec.Head + "\n\n" + rec.Why, "适配方案说明",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Log(">>> 安装并应用（" + SchemeShort(a) + "）：" + selected.Title);
            ApplyAction(a);
            RefreshFgSummary();
        }

        //  「卸载补丁」：按目录里实际装着的那一套去卸（不是按下拉 —— 下拉可能选着别家）
        void DoUninstallCurrent()
        {
            if (selected == null) { Log("  先从左侧游戏栏选一个游戏"); return; }
            string cur = Plan.Current(selected.Dir);
            Log(">>> 卸载补丁（" + Plan.PlanName(cur) + "）：" + selected.Title);
            if (cur == Plan.Opti) DoXeUndeploy();
            else if (cur == Plan.D030) Do030Undeploy();
            else if (cur == Plan.D040) Do040Undeploy();
            else if (cur == Plan.Nvbr) DoNvUndeploy();
            else if (cur == Plan.Legacy) DoFgUninstall();
            else if (SmoothMotionOn(selected)) DoSmoothMotion(false);
            else
            {
                //  目录里什么都没有：仍走一次通用卸载，把"两代并存"这类历史残留一起清干净
                Log("  目录里没有正在生效的代理，按通用卸载清理残留");
                DoFgUninstall();
            }
            RefreshFgSummary();
        }

        //  这台机器上 Smooth Motion 对这个游戏开没开（驱动库只读）
        static bool SmoothMotionOn(DlssgGame g)
        {
            if (g == null) return false;
            try
            {
                uint? v = NvDrsDb.ReadUserValue(NvDrsDb.IdSmoothMotion);
                return v.HasValue && v.Value == 5;
            }
            catch { return false; }
        }

        void DoApplyRecommend()
        {
            if (selected == null) { Log("  先从左侧游戏栏选一个游戏"); return; }
            var rec = Dlssg.BuildRec(selected);
            Log(">>> 一键应用推荐：" + rec.Head);
            if (rec.Action == "none")
            {
                MessageBox.Show(rec.Head + "\n\n" + rec.Why, "方案推荐",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ApplyAction(rec.Action);
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
            if (selected == null) { Log("  请先从左侧游戏栏选一个游戏"); return; }
            string fgDir = FgDir(selected);
            string kind = XeMfg.Detect(fgDir);
            string entry = "dxgi.dll";
            string reason = null;
            string auto = Cfg.DlssgAutoEntry ? Dlssg.ResolveOverrideEntry(fgDir, selected.Title, out reason) : null;
            if (!string.IsNullOrEmpty(auto))
            {
                entry = auto;
                Log("  自动识别专用入口：" + auto + (string.IsNullOrEmpty(reason) ? "" : "（" + reason + "）"));
            }
            else if (kind == "zzz") entry = "d3d12.dll";

            string desc = kind == "zzz"
                ? "· 绝区零：d3d12.dll 入口 · 帧生成 DLSSG→XeFG（绕反作弊按名拦截）\n"
                : (kind == "wuwa"
                    ? "· 鸣潮：dxgi.dll 入口 · 帧生成走原生 DLSSG（SM86 解锁）· 支持 DLSS 5 NR\n"
                    : "· 通用单机/3A 游戏：入口 " + entry + " · 跨平台插帧（XeSS-FG / FSR 3.1 兼容）\n");

            if (MessageBox.Show(
                "将往游戏渲染目录部署 OptiScaler 跨平台补帧组件（XeSS-FG / FSR 3.1）：\n"
                + fgDir + "\n\n"
                + desc
                + "· 适用：AMD Radeon / Intel Arc 显卡、GTX 10/16 系列，或突破锁倍率的进阶玩法\n"
                + "· 属于第三方组件注入，带内核反作弊的游戏请自行评估风险\n"
                + "· 覆盖的游戏自带文件会自动备份；已调好的 OptiScaler.ini 会被保留，不被出厂值覆盖\n"
                + "· 同一目录里已有的其它方案会被改名停放（<文件>.parked.xxx），随时可切回\n"
                + "· 完全退出游戏再部署\n\n确定部署吗？",
                "切换到方案 A · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Log("切换到 " + Plan.PlanName(Plan.Opti) + "：" + fgDir);
            Log("  " + Plan.SwitchTo(fgDir, Plan.Opti, false, entry));
            FillGameList();
            RefreshFgSummary();
        }

        void DoXeUndeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            string fgDir = FgDir(selected);
            if (MessageBox.Show(
                "把方案 A 的入口代理与配套组件改名停放（<文件>.parked.opti），游戏回到原生状态。\n\n"
                + "· 不删除任何文件：全部改名保留，OptiScaler.ini 里你调好的伪装 / NR 参数也一起存着\n"
                + "· 想切回来：在「概览」把方案选成它，点「安装并应用」即可，停放的文件会原样改回原名\n\n"
                + "确定停放吗？",
                "停放方案 A · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log("停放方案 A（入口 + 配套组件，只改名不删文件）：" + fgDir);
            Log("  " + Plan.SwitchTo(fgDir, Plan.None));
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

        // 帧生成代理的落地目录。
        //  选中条目可能落在**启动器层**（异环实测：…\Neverness To Everness\NTELauncher\ 里
        //  同时有 NTELauncher.exe 与 NTEGame.exe），而代理只有放在渲染 EXE 旁边才会被加载。
        //  这里统一解析一次，只影响帧生成相关的部署/查询，不动条目本身的 Dir/Exe（否则会改坏「启动游戏」）。
        string FgDir(DlssgGame g)
        {
            if (g == null) return "";
            string d = SecondGame.ResolveDir(g.Dir);
            bool changed = d != null && g.Dir != null
                && !string.Equals(d.TrimEnd('\\'), g.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (changed && !string.Equals(fgDirSeen, d, StringComparison.OrdinalIgnoreCase))
            {
                fgDirSeen = d;
                Log("  落地目录从启动器层纠正为渲染目录：" + d);
            }
            else if (!changed && SecondGame.LooksLauncher(d)
                     && !string.Equals(fgDirSeen, "warn:" + d, StringComparison.OrdinalIgnoreCase))
            {
                fgDirSeen = "warn:" + d;
                Log("  ⚠ 这一层是启动器目录（含有启动器引导程序），没找到渲染目录 —— "
                    + "代理装在这里很可能不生效。请确认渲染 exe 所在的子目录。");
            }
            return d;
        }

        void Do030Deploy()
        {
            if (selected == null) { Log("  请先在左侧游戏栏选中一个游戏"); return; }
            string kind = XeMfg.Detect(FgDir(selected));
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
                + "  · 绝区零按显卡名决定是否显示 DLSS / 帧生成选项，非 40/50 系显卡（如 RTX 20/30 系列）会被官方识别判定为不支持原生 DLSS 3 帧生成；\n"
                + "  · 但**不要**再尝试导入外部注册表显卡名伪装（如改注册表或假冒 RTX 4090）：\n"
                + "    DeviceFP 已经会将非真实伪装的 GPU 名记成 invalid（MIHOYOSDK_GPU_NAME），\n"
                + "    伪装无论走注册表还是走外部 DXGI 注入都很可能被游戏完整性校验判为环境篡改 → 进游戏后弹 (0,11008) 闪退。\n"
                + "  · " + Dlssg030.Knowledge("zzz-spoof", "版本相关的伪装结论请先看说明页，别照旧印象操作") + "。\n"
                + "    （这条是按当前代理版本 " + Dlssg030.Ver + " 从 catalog.json 的 knowledge 表挑的）\n"
                + "  · 绝区零当前唯一能让「帧生成」选项出现的是方案 A。要用方案 A 就关掉本对话框，\n"
                + "    在「概览」把方案选成「OptiScaler · 方案 A」，点「安装并应用」。\n\n"
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
                    + "  想切回去，在「概览」把方案选成「OptiScaler · 方案 A」再点「安装并应用」，停放的文件会原样改回原名。\n"
                    + "· 覆盖的游戏自带 nvngx 运行库会自动备份\n")
                + "\n确定为它切换到本方案吗？",
                "切换到 DLSS MFG " + Dlssg030.Ver + " · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // 二游规则：作者发布包里那张「几个网游注入.txt」表只给了 0.3.x 那套的入口。
            //  跟作者给的不一致时先摆出来 —— 允许继续装（游戏不加载某个入口名是常态），
            //  但别让人以为自己在跑作者推荐的那套。
            string k2 = SecondGame.Detect(selected);
            if (k2.Length > 0)
            {
                string want = SecondGame.SuggestEntry(k2);
                if (!string.Equals(Cfg.DlssgGenericEntry ?? "", want, StringComparison.OrdinalIgnoreCase))
                {
                    DialogResult dr = MessageBox.Show(
                        "《" + SecondGame.Label(k2) + "》作者给的入口是 " + want + "\n"
                        + "  原因：" + SecondGame.Why(k2) + "\n\n"
                        + "现在选的是 " + (string.IsNullOrEmpty(Cfg.DlssgGenericEntry) ? "version.dll" : Cfg.DlssgGenericEntry)
                        + " —— 可以按它装，但你得知道这不是作者在表上给的那个。\n\n"
                        + "要用作者给的 " + want + " 吗？\n"
                        + "（「否」= 按现在这个装；「取消」= 不装）",
                        "二游入口建议 · " + selected.Title,
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (dr == DialogResult.Cancel) { Log("  已取消，未做任何改动"); return; }
                    if (dr == DialogResult.Yes)
                    {
                        Cfg.DlssgGenericEntry = want;
                        Program.SetConfigStr("dlssg", "genericEntry", want);
                        for (int i2 = 0; i2 < Dlssg030.GenericEntries.Length; i2++)
                            if (Dlssg030.GenericEntries[i2].Equals(want, StringComparison.OrdinalIgnoreCase))
                                cbEntry.SelectedIndex = i2;
                        Log("  已按作者网游注入表把入口改为 " + want);
                    }
                }
            }
            string fgDir = FgDir(selected);
            string reason030 = null;
            string autoEntry030 = Cfg.DlssgAutoEntry ? Dlssg.ResolveOverrideEntry(fgDir, selected.Title, out reason030) : null;
            string entry030 = !string.IsNullOrEmpty(autoEntry030) ? autoEntry030 : (generic ? Cfg.DlssgGenericEntry : Dlssg030.EntryFor(kind));
            if (!string.IsNullOrEmpty(autoEntry030))
            {
                Log("  自动识别游戏专用入口：" + autoEntry030 + (string.IsNullOrEmpty(reason030) ? "" : "（" + reason030 + "）"));
            }
            if (!TrustGateFiles(Dlssg030.Planned030Sources(fgDir, kind, entry030), selected.Title))
            {
                Log("  已取消：源件未通过签名核验");
                return;
            }
            Log("部署 DLSS MFG " + Dlssg030.Ver + "：" + fgDir + (generic ? "（通用模式，入口 " + entry030 + "）" : ""));
            foreach (string ln in Dlssg030.PrereqReport().Split('\n'))
            {
                string q = ln.TrimEnd();
                if (q.Length > 0) Log("  " + q);
            }
            Log("  " + Plan.SwitchTo(fgDir, Plan.D030, false, entry030));
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
            Log("停放 " + Plan.PlanName(Plan.D030) + "（入口 + 配套组件，只改名不删文件）：" + FgDir(selected));
            Log("  " + Plan.SwitchTo(FgDir(selected), Plan.None));
            FillGameList();
            RefreshFgSummary();
        }

        // ---------------------- 0.4.x（2026-10-18 新增）----------------------

        void Do040Deploy()
        {
            if (selected == null) { Log("  请先在左侧游戏栏选中一个游戏"); return; }
            string miss;
            if (!Dlssg040.Ready(out miss))
            {
                Log("  0.4.x 资源包还没到位（缺 " + miss + "）—— 先点本区块的「从 GitHub 取新版」");
                return;
            }
            string entry = Cfg.DlssgEntry040;
            if (entry == null || entry.Length == 0) entry = "version.dll";
            // 二游：作者只在 0.3.x 那套给过网游注入表（异环 Version.asi / 绝区零 d3d12 / 鸣潮 version-dxgi），
            //  0.4.x 的文档里没有对应说法 —— 先讲清楚，装不装由用户决定。
            string gk = SecondGame.Detect(selected);
            string gachaWarn = gk.Length > 0
                ? "※ 《" + SecondGame.Label(gk) + "》是二游：作者的网游注入表只覆盖 0.3.x 那套（"
                  + SecondGame.SuggestEntry(gk) + "），0.4.x 没有给这张表。\n"
                  + "  要照作者给的来，就用上一区块的 0.3.x"
                  + (SecondGame.WantsAsi(gk) ? " ASI 入口" : "") + "。\n\n"
                : "";
            // 入口按二游规则挑：同一颗代理换名字（0.4.0 只是把六个名字都放进 alternatives\）。
            //  绝区零必须 d3d12（其它名字可能被反作弊隔离）；鸣潮 version 优先、dxgi 备选；
            //  异环没有 0.4.x 的 ASI 版 —— 只能明确劝回 0.3.x。
            if (gk.Length > 0)
            {
                string want4 = SecondGame.SuggestEntry040(gk);
                if (want4.Length == 0)
                {
                    if (MessageBox.Show(
                        "《" + SecondGame.Label(gk) + "》在 0.4.x 上没有专用装法：\n"
                        + "  · 作者 0.4.0 没出 ASI 版（Version.asi + wininet.dll 那套停在 0.3.5）\n"
                        + "  · 这游戏又会占住 / 屏蔽常规入口名，用 " + entry + " 大概率加载不上\n\n"
                        + "建议关掉本对话框，改用上一区块的「ASI 入口」。\n\n仍要按 " + entry + " 试一次吗？",
                        "异环 · 0.4.x 没有专用入口", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    {
                        Log("  已取消（建议改用 0.3.x 的 ASI 入口）");
                        return;
                    }
                }
                else if (!string.Equals(entry, want4, StringComparison.OrdinalIgnoreCase))
                {
                    DialogResult dr4 = MessageBox.Show(
                        "《" + SecondGame.Label(gk) + "》建议用 " + want4 + "\n"
                        + "  " + SecondGame.Why(gk) + "\n\n"
                        + "现在选的是 " + entry + " —— 可以按它装，但作者在表上给的不是那个。\n\n"
                        + "要用 " + want4 + " 吗？（「否」= 按现在这个装；「取消」= 不装）",
                        "二游入口建议 · " + selected.Title,
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (dr4 == DialogResult.Cancel) { Log("  已取消，未做任何改动"); return; }
                    if (dr4 == DialogResult.Yes)
                    {
                        entry = want4;
                        Log("  已按二游规则改用入口 " + want4);
                    }
                }
            }
            string fgDir040 = FgDir(selected);
            string reason040 = null;
            string autoEntry040 = Cfg.DlssgAutoEntry ? Dlssg.ResolveOverrideEntry(fgDir040, selected.Title, out reason040) : null;
            if (!string.IsNullOrEmpty(autoEntry040))
            {
                entry = autoEntry040;
                Log("  自动识别游戏专用入口：" + autoEntry040 + (string.IsNullOrEmpty(reason040) ? "" : "（" + reason040 + "）"));
            }
            if (!TrustGateFiles(Dlssg040.PlannedSources(fgDir040, entry), selected.Title))
            {
                Log("  已取消：源件未通过签名核验");
                return;
            }
            if (MessageBox.Show(
                gachaWarn
                + "将部署 DLSSG for SM86 " + Dlssg040.Ver + "（新版结构）到：\n" + fgDir040 + "\n\n"
                + "· 部署清单：入口 " + entry + " + dlssg_sm86.ini + 整个 dlssg_sm86\\ 目录（"
                + Dlssg040.DeploySizeNote() + "）\n"
                + "· 同一目录里已有的 0.3.x / 方案 A 会被改名停放（<文件>.parked.xxx），不删任何文件\n"
                + "· 升级自 0.3.x 时旧代理（0.3.1–0.3.6 那四个 DLL）与旧 dlssg_sm86\\ 都会一并让位，"
                + "这是作者 README 要求的（同目录多个代理时只有第一个被加载的那个生效）\n"
                + "· 出厂 MaxGeneratedFrames=3（4X）；要 6X 到下面「帧生成参数」把上限改成 5\n"
                + "· 完全退出游戏再部署：DLL 被占用时改名会失败\n\n确定吗？",
                "切换到 DLSS MFG " + Dlssg040.Ver + " · " + selected.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Log("部署 " + Plan.PlanName(Plan.D040) + "：" + fgDir040 + "（入口 " + entry + "）");
            Log("  " + Plan.SwitchTo(fgDir040, Plan.D040, false, entry));
            //  每游戏参数覆盖（第 160 轮）：SwitchTo→Install 落的是全局值，这里把本游戏的覆盖值补上
            string perGame = ApplyPerGameFg(selected);
            if (perGame.Length > 0) Log("  " + perGame);
            FillGameList();
            RefreshFgSummary();
        }

        // ---- 每游戏帧生成参数（第 160 轮）----
        //  生效值解析：游戏自己覆盖了就用覆盖值，否则用全局（config.json 的 dlssg 段）。
        int EffFgMax(DlssgGame g) { return g != null && g.FgMax > 0 ? g.FgMax : Cfg.DlssgMaxFrames; }
        string EffFgPreset(DlssgGame g)
        {
            return g != null && g.FgPreset != null && g.FgPreset.Length > 0 ? g.FgPreset : Cfg.DlssgPreset040;
        }
        int EffFgOpt(DlssgGame g) { return g != null && g.FgOpt >= 0 ? g.FgOpt : Cfg.DlssgOptimized040; }

        //  把"本游戏生效的 FG 参数"写进已部署的目录。返回给人看的摘要；
        //  没装我们的代理 / 没有任何覆盖时返回空串（不打扰日志）。
        //  方案 A / 旧版 / 方案 B 的参数不归这里管（OptiScaler 读自己的 ini / 方案 B 是游戏内覆盖）。
        string ApplyPerGameFg(DlssgGame g)
        {
            if (g == null || g.Dir == null || g.Dir.Length == 0) return "";
            string rdir = SecondGame.ResolveDir(g.Dir);
            string gdir = XeMfg.TargetDir(rdir, XeMfg.Detect(rdir));
            string own = "";
            foreach (string e in Plan.AllEntryNames)
            {
                string f = Path.Combine(gdir, e);
                if (!File.Exists(f)) continue;
                string o = Plan.OwnerOf(f);
                if (o.Length > 0) { own = o; break; }
            }
            if (own == Plan.D030)
            {
                if (g.FgMax <= 0) return "";
                return "每游戏参数 · " + g.Title + "（0.3.x）：" + Dlssg030.SetMaxFrames(gdir, g.FgMax);
            }
            if (own == Plan.D040)
            {
                var parts = new List<string>();
                if (g.FgMax > 0) parts.Add(Dlssg040.SetMaxFrames(gdir, g.FgMax));
                if (g.FgOpt >= 0 || (g.FgPreset != null && g.FgPreset.Length > 0))
                    parts.Add(Dlssg040.ApplyTuning(gdir, EffFgOpt(g), EffFgPreset(g)));
                if (parts.Count == 0) return "";
                return "每游戏参数 · " + g.Title + "（0.4.x）：" + string.Join("；", parts.ToArray());
            }
            return "";
        }

        void Do040Undeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (MessageBox.Show(
                "把 0.4.x 的入口代理、dlssg_sm86.ini 与 dlssg_sm86\\ 资源目录改名停放（<文件>.parked.040），游戏回到原生。\n"
                + "· 只改名不删文件，随时可切回\n"
                + "· 资源目录有几十 MB：确定不再用了可以到「彻底清理」真删掉\n\n确定停放吗？",
                "停放 " + Plan.PlanName(Plan.D040) + " · " + selected.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log("停放 " + Plan.PlanName(Plan.D040) + "：" + FgDir(selected));
            Log("  " + Plan.SwitchTo(FgDir(selected), Plan.None));
            FillGameList();
            RefreshFgSummary();
        }

        void Do040Purge()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            string e = Dlssg040.InstalledEntry(FgDir(selected));
            if (e.Length == 0) { Log("  这个目录里没有 0.4.x 的代理（无需清理）：" + FgDir(selected)); return; }
            if (MessageBox.Show(
                "将**删除**（不是停放）该目录里的 0.4.x 代理 " + e + "、dlssg_sm86.ini 与整个 dlssg_sm86\\ 资源目录。\n"
                + "· 与「停放」不同：这一步是真的删除，切不回来，只能重新部署\n"
                + "· 只在确定不再用这套时才做\n\n确定删除吗？",
                "彻底清理 0.4.x · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Log("彻底清理 0.4.x：" + FgDir(selected));
            Log("  " + Dlssg040.Purge(FgDir(selected)));
            FillGameList();
            RefreshFgSummary();
        }

        // 0.4.x 参数：下拉 → 配置（不落 ini）；点「应用参数到已装游戏」才写进各游戏目录
        static int PresetIndex(string v)
        {
            if (string.Equals(v, "B", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(v, "C", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        static string PresetText(int i)
        {
            if (i == 1) return "B";
            if (i == 2) return "C";
            return "A";
        }

        void Save040Tuning()
        {
            if (cbOpt040 != null)
            {
                Cfg.DlssgOptimized040 = cbOpt040.SelectedIndex;
                Program.SetConfigInt("dlssg", "optimized040", Cfg.DlssgOptimized040);
            }
            if (cbPreset040 != null)
            {
                Cfg.DlssgPreset040 = PresetText(cbPreset040.SelectedIndex);
                Program.SetConfigStr("dlssg", "preset040", Cfg.DlssgPreset040);
            }
            if (cbEngine040 != null)
            {
                Cfg.DlssgEngine040 = cbEngine040.SelectedIndex == 1 ? "XeSS" : "DLSS";
                Program.SetConfigStr("dlssg", "engine040", Cfg.DlssgEngine040);
            }
            if (cbSr040 != null)
            {
                Cfg.DlssgSr040 = cbSr040.SelectedIndex == 1;
                Program.SetConfigBool("dlssg", "srMode040", Cfg.DlssgSr040);
            }
        }

        void Do040Tuning()
        {
            Save040Tuning();
            Log(">>> 应用 0.4.x 参数：Optimized=" + Cfg.DlssgOptimized040
                + " · Preset=" + Cfg.DlssgPreset040
                + " · Engine=" + Cfg.DlssgEngine040
                + " · SuperResolution=" + (Cfg.DlssgSr040 ? "M-INT8" : "Off"));
            int n = 0;
            foreach (var g in games)
            {
                if (g == null || g.Dir == null) continue;
                string d = SecondGame.ResolveDir(g.Dir);
                if (!Dlssg040.Installed(d)) continue;
                //  每游戏覆盖（第 160 轮）：Preset/Optimized 取"本游戏生效值"写进各自目录
                Log("  " + g.Title + "：" + Dlssg040.ApplyTuning(d, EffFgOpt(g), EffFgPreset(g))
                    + ((g.FgOpt >= 0 || (g.FgPreset != null && g.FgPreset.Length > 0)) ? "（含本游戏覆盖）" : ""));
                n++;
            }
            if (n == 0) Log("  没有已部署 0.4.x 的游戏 —— 参数已记住，下次部署时一并写入");
            RefreshFgSummary();
        }

        // 生效检查：0.3.x 与 0.4.x 写的是同一个日志目录（dlssg_sm86\logs），所以共用一套读法。
        //  只读，不改任何文件；结论只统计文件名与几个关键串，不做严格 JSON 解析。
        void DoFgLogCheck()
        {
            if (selected == null) { Log("  请先选中一个游戏"); return; }
            string d = FgDir(selected);
            Log(">>> 帧生成生效检查：" + selected.Title + " · " + Plan.PlanName(Plan.Current(d)));
            Log("  目录：" + d);
            Dlssg040.LogCheck lc = Dlssg040.CheckLogs(d);
            Log("  " + lc.Line());
            if (!lc.HasDir)
                Log("  说明：日志只在游戏启动之后才写。装好还没进过游戏时这里是空的，属于正常。");
            else if (!lc.Loaded)
                Log("  说明：日志目录在、但没有 loader/backend 文件 → 入口 DLL 没被加载。"
                    + "回本区块把入口换成 winmm / dbghelp / dinput8 再试。");
            else if (lc.KernelMissing > 0)
                Log("  说明：缺少内核包，帧生成不会介入 —— 重新部署一次，或确认 "
                    + "dlssg_sm86\\kernels\\fg_kernels.dll 在游戏目录里。");
        }

        void DoAsiDeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            string miss;
            if (!DlssgAsi.Ready(out miss))
            {
                Log("  ASI 变体资源还没到位（缺 " + miss + "）—— 把作者那份 ASI 版压缩包拖进窗口即可");
                return;
            }
            // Version.asi 是作者签的，随行的 wininet.dll（Ultimate ASI Loader）通常不是 —— 逐个过一遍。
            //  这一步不能省：它会被复制进游戏目录，而游戏目录里常有别的 mod 也在用它。
            foreach (string nm in new string[] { DlssgAsi.Proxy, DlssgAsi.Loader })
            {
                if (!TrustGateFile(Path.Combine(DlssgAsi.Dir, nm), selected.Title))
                {
                    Log("  已取消：" + nm + " 没通过签名核验");
                    return;
                }
            }
            string tip = DlssgAsi.IsGame(selected) ? "（认出它是异环：常规入口名在这游戏上不管用，走 ASI 加载器）" : "";
            if (MessageBox.Show(
                "将部署 ASI 变体架构（" + DlssgAsi.Proxy + " 与 " + DlssgAsi.Loader + "）：\n"
                + (string.IsNullOrEmpty(tip) ? "" : tip + "\n")
                + "• 采用 ASI 加载机制规避常规 DLL 拦截，核心代理组件保持一致\n"
                + "• 加载器组件如遇冲突将自动备份已有模块，不覆盖用户自定义配置\n"
                + "• 卸载说明：可通过方案切换自动停用，或手动清理对应 DLL 文件\n\n确认部署？",
                "ASI 入口 · " + selected.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Log("部署 ASI 变体：" + FgDir(selected));
            Log("  " + DlssgAsi.Install(FgDir(selected), true));
            Log("  " + Dlssg030.SetMaxFrames(FgDir(selected), Cfg.DlssgMaxFrames));
            FillGameList();
            RefreshFgSummary();
        }

        // ---------------------- 方案 B · MFG Bridge ----------------------

        // Smooth Motion：写 NVIDIA 驱动配置的 per-game 档（不往游戏目录放任何文件）
        void DoSmoothMotion(bool on)
        {
            if (selected == null) { Log("  请先选中一个游戏"); return; }
            string exe = (selected.Exe ?? "").Trim();
            if (exe.Length == 0) { Log("  这个条目没有 exe 名，写不了驱动档（先在游戏库里补上 exe）"); return; }
            if (on && MessageBox.Show(
                "为《" + selected.Title + "》开启 Smooth Motion（驱动级插帧）：\n\n"
                + "· 写入 NVIDIA 驱动配置（按 exe 单独一档），写之前会备份 nvdrsdb\n"
                + "· 只对这一个 exe 生效，不动别的游戏\n"
                + "· 支持 DX11 / DX12 / Vulkan，不要求游戏本身支持 DLSS\n"
                + "· ⚠ 30 系通常还需要 NVSmooth30 / MFG-version 这类 mod 才能真生效\n"
                + "· ⚠ 联机 / 带反作弊的游戏不要用\n\n继续吗？",
                "Smooth Motion · " + selected.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Log("Smooth Motion：" + (on ? "开启" : "关闭") + " · " + selected.Title + "（" + exe + "）");
            Log("  " + NvDrs.SetSmoothMotion(exe, on));
            RefreshFgSummary();
        }

        void DoNvDeploy()
        {
            if (selected == null) { Log("  请先在左侧游戏栏选中一个游戏"); return; }
            string miss;
            if (!MfgBridge.Ready(out miss))
            {
                Log("  方案 B 资源包还没到位（缺 " + miss + "）—— 先点本区块的「从 GitHub 取新版」");
                return;
            }
            string entry = Cfg.DlssgEntryNvbr;
            if (entry == null || entry.Length == 0) entry = "version.dll";
            if (!TrustGateFiles(MfgBridge.PlannedSources(), selected.Title))
            {
                Log("  已取消：源件未通过签名核验");
                return;
            }
            // 二游：作者那张网游表只针对 0.3.x，方案 B 是第三方项目，入口名清单不一样 —— 说一句再装。
            string gk = SecondGame.Detect(selected);
            string gachaWarn = gk.Length > 0
                ? "※ 《" + SecondGame.Label(gk) + "》包含反作弊组件。方案 B 为第三方独立代理架构，推荐使用受信任入口并避免在多人对抗模式下注入。\n\n"
                : "";
            string fgDirNv = FgDir(selected);
            string reasonNv = null;
            string autoEntryNv = Cfg.DlssgAutoEntry ? Dlssg.ResolveOverrideEntry(fgDirNv, selected.Title, out reasonNv) : null;
            if (!string.IsNullOrEmpty(autoEntryNv))
            {
                entry = autoEntryNv;
                Log("  自动识别游戏专用入口：" + autoEntryNv + (string.IsNullOrEmpty(reasonNv) ? "" : "（" + reasonNv + "）"));
            }
            if (MessageBox.Show(
                gachaWarn
                + "将把发布包里的 nvngx.dll 改名成 " + entry + " 后放进：\n" + fgDirNv + "\n\n"
                + "· 它运行时会按需把 provider 解包到 %TEMP%\\nvidia_mfg_bridge\\\n"
                + "· 同一目录里已有的其它方案会被改名停放（<文件>.parked.xxx），不删任何文件\n"
                + "· 游戏内按 Home 键调倍率覆盖，按 Insert 键切 DLSS 降噪 / 光追重建\n"
                + "· 出厂 MaxGeneratedFrames 取决于它自带的 ini；要改档位用下面「帧生成参数」\n"
                + "· 完全退出游戏再部署\n\n确定吗？",
                "切换到方案 B · " + selected.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            Log("部署方案 B：" + fgDirNv + "（入口 " + entry + "）");
            Log("  " + Plan.SwitchTo(fgDirNv, Plan.Nvbr, false, entry));
            FillGameList();
            RefreshFgSummary();
        }

        void DoNvUndeploy()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            if (MessageBox.Show(
                "把方案 B 的入口 DLL 与它自带的 ini 改名停放（<文件>.parked.nvbr），游戏回到原生。\n"
                + "· 只改名不删文件，随时可切回\n\n确定停放吗？",
                "停放方案 B · " + selected.Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log("停放方案 B：" + FgDir(selected));
            Log("  " + Plan.SwitchTo(FgDir(selected), Plan.None));
            FillGameList();
            RefreshFgSummary();
        }

        // 「从 GitHub 取新版」（方案 B 的仓库）
        void DoNvFetch()
        {
            if (upstreamBusy) { Log("  正在取新版，稍等"); return; }
            upstreamBusy = true;
            if (btnNvFetch != null) btnNvFetch.Enabled = false;
            Log(">>> 查询 " + Upstream.RepoOf("mfgbridge") + " 的最新 release …"
                + (Cfg.DlssgUseProxy ? "（经代理 " + Cfg.DlssgProxy + "）" : "（直连）"));

            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                string err;
                List<Upstream.Release> list = Upstream.Fetch(5, Upstream.RepoOf("mfgbridge"), out err);
                e.Result = new object[] { list, err };
            };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                try
                {
                    object[] pack = e.Result as object[];
                    List<Upstream.Release> list = pack != null ? pack[0] as List<Upstream.Release> : null;
                    string err = pack != null ? pack[1] as string : "";
                    if (list == null || list.Count == 0)
                    {
                        Log("  没取到 release：" + (err.Length > 0 ? err : "返回为空"));
                        Log("  提示：国内网络建议到「设置」页把代理打开并填对本机代理端口");
                        return;
                    }
                    foreach (Upstream.Release r in list) Log(r.Line());
                    Upstream.Release top = null;
                    foreach (Upstream.Release r in list) if (r.HasZip) { top = r; break; }
                    if (top == null) { Log("  这些 release 里没有打包好的 zip / 7z 资产。"); return; }
                    AskDownLoadRelease(top);
                }
                finally
                {
                    upstreamBusy = false;
                    if (btnNvFetch != null) btnNvFetch.Enabled = true;
                    if (btnUpRelease != null) btnUpRelease.Enabled = true;
                }
            };
            gw.RunWorkerAsync();
        }

        // 作者的网盘链接：不再"复制一大段文本让人自己粘"（2026-11-01 用户原话：
        //  "难道不会定向到这个链接吗？还要别人一个一个复制吗"）—— 改成每条一个「打开」按钮，
        //  点了直接跳浏览器；百度链接自带 ?pwd= 提取码，打开即免输。
        //  仍不自动下载 —— 网盘要登录、直链会过期，写个抓取器也是"十次里成功两次"，
        //  不如把入口做到一键直达。
        void DoNetDiskSources()
        {
            var all = NetDiskSources.All;
            var dlg = new Form();
            Fx.FadeOnClose(dlg);
            dlg.Text = "网盘下载源";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.ShowInTaskbar = false;
            dlg.ShowIcon = false;
            int rowH = Theme.S(52);
            dlg.ClientSize = new Size(Theme.S(640), Theme.S(46 + all.Length * rowH + 54));
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var tip = new TxtLbl();
            tip.Text = "点「打开」直接跳到网盘（提取码已带上）；下到的包用 0.4.x 区块的「导入资源包…」按钮选择导入。";
            tip.AutoSize = true;
            tip.ForeColor = Theme.TextDim;
            tip.Font = Theme.F(9f);
            tip.Location = new Point(Theme.S(16), Theme.S(12));
            dlg.Controls.Add(tip);

            int y = Theme.S(42);
            foreach (NetDisk d in all)
            {
                var t = new TxtLbl();
                t.Text = d.Name;
                t.AutoSize = true;
                t.Font = Theme.FB(9.5f);
                t.Location = new Point(Theme.S(16), y);
                dlg.Controls.Add(t);

                var f = new TxtLbl();
                //  行 2 = 用途。测量截断到按钮区之前（2026-11-01：夸克条那行太长，压到「打开」下面 ——
                //  用户原话"挡在那儿"。提取码不再拼在行尾 —— 「复制提取码」按钮就在旁边，码本身不必露出）。
                string line2 = d.For;
                int maxW = dlg.ClientSize.Width - Theme.S(232);
                while (line2.Length > 4 && TextRenderer.MeasureText(line2, f.Font).Width > maxW)
                    line2 = line2.Substring(0, line2.Length - 2).TrimEnd() + "…";
                f.Text = line2;
                f.AutoSize = true;
                f.ForeColor = Theme.TextDim;
                f.Font = Theme.F(9f);
                f.Location = new Point(Theme.S(16), y + Theme.S(21));
                dlg.Controls.Add(f);

                string url = d.Url, code = d.Code;
                var btnOpen = new FlatBtn();
                btnOpen.Text = "打开";
                btnOpen.Kind = BtnKind.Primary;
                btnOpen.Width = Theme.S(72);
                btnOpen.Location = new Point(dlg.ClientSize.Width - Theme.S(184), y + Theme.S(4));
                btnOpen.Click += delegate
                {
                    try { Process.Start(url); }
                    catch (Exception ex) { Log("  打开网盘失败：" + ex.Message + "（链接：" + url + "）"); }
                };
                dlg.Controls.Add(btnOpen);

                var btnCode = new FlatBtn();
                btnCode.Text = "复制提取码";
                btnCode.Width = Theme.S(100);
                btnCode.Location = new Point(dlg.ClientSize.Width - Theme.S(104), y + Theme.S(4));
                btnCode.Visible = code.IndexOf("免密", StringComparison.Ordinal) < 0;   // 夸克免密，没有码可复制
                btnCode.Click += delegate
                {
                    try { Clipboard.SetText(code); Log("  提取码已复制：" + code); } catch { }
                };
                dlg.Controls.Add(btnCode);

                y += rowH;
            }

            //  分享 / 存档用：老行为（整段文本进剪贴板）保留成一个角落按钮
            var btnAll = new FlatBtn();
            btnAll.Text = "复制全部文本";
            btnAll.Width = Theme.S(110);
            btnAll.Location = new Point(Theme.S(16), dlg.ClientSize.Height - Theme.S(40));
            btnAll.Click += delegate
            {
                try { Clipboard.SetText(NetDiskSources.Block()); Log("  网盘清单已复制到剪贴板"); } catch { }
            };
            dlg.Controls.Add(btnAll);

            Log(">>> 网盘下载源：打开 " + all.Length + " 条（点「打开」直达对应网盘）");
            dlg.ShowDialog(this);
        }

        // ---- 应用内更新检查（第 160 轮）----
        //  复用 Upstream 的 GitHub Releases 通道（代理/直连两路、JSON 解析的坑都趟过）。
        //  节流：真正发出请求后记日期（成败都记）—— 静默模式每天最多一次；手动点按钮不受限。
        void BeginUpdateCheck(bool manual)
        {
            if (updBusy) { if (manual) Log("  正在检查，稍等"); return; }
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!manual)
            {
                if (Cfg == null || !Cfg.UpdateCheck) return;
                if (Cfg.UpdateCheckAt == today) return;
            }
            updBusy = true;
            if (btnUpdCheck != null) btnUpdCheck.Enabled = false;
            if (manual) Log(">>> 检查更新（" + SelfRepo + "）…");
            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                string err;
                List<Upstream.Release> list = Upstream.Fetch(1, SelfRepo, out err);
                e.Result = new object[] { list, err };
            };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                try
                {
                    object[] pack = e.Result as object[];
                    List<Upstream.Release> list = pack != null ? pack[0] as List<Upstream.Release> : null;
                    string err = pack != null ? pack[1] as string : "";
                    //  成败都记日期：离线时不该每次启动都去撞一个必失败的 API
                    if (Cfg != null)
                    {
                        Cfg.UpdateCheckAt = today;
                        Program.SetConfigStr("ui", "updateCheckAt", today);
                    }
                    if (lblUpd != null && !lblUpd.IsDisposed && page == P_SET && Visible)
                        SetBody(lblUpd, "当前版本 " + Program.AppVersion + " · 上次检查：" + today);
                    Upstream.Release top = list != null && list.Count > 0 ? list[0] : null;
                    if (top == null)
                    {
                        if (manual) Log("  没查到 release：" + (err.Length > 0 ? err : "返回为空")
                            + "（国内网络建议在「下载源」里开代理）");
                        return;
                    }
                    int c = CmpVer(top.Tag, Program.AppVersion);
                    if (c > 0)
                    {
                        updUrl = top.Html.Length > 0 ? top.Html : ("https://github.com/" + SelfRepo + "/releases");
                        if (btnUpdOpen != null) btnUpdOpen.Visible = true;
                        string msg = "发现新版本 " + top.Tag + "（当前 " + Program.AppVersion + "）"
                            + (top.At.Length >= 10 ? " · " + top.At.Substring(0, 10) : "");
                        Log("[更新] " + msg);
                        if (lblUpd != null && !lblUpd.IsDisposed && page == P_SET && Visible)
                            SetBody(lblUpd, "有新版本：" + msg + " —— 点「打开发布页」下载");
                        if (manual)
                            MessageBox.Show(this, msg + "\n\n发布页已在设置页给出（按钮「打开发布页」）。",
                                "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        updUrl = "";
                        if (btnUpdOpen != null) btnUpdOpen.Visible = false;
                        if (manual) Log("  已是最新（" + Program.AppVersion + "）");
                        if (manual && lblUpd != null && !lblUpd.IsDisposed && page == P_SET && Visible)
                            SetBody(lblUpd, "已是最新（" + Program.AppVersion + "）· 上次检查：" + today);
                    }
                }
                finally
                {
                    updBusy = false;
                    if (btnUpdCheck != null) btnUpdCheck.Enabled = true;
                }
            };
            gw.RunWorkerAsync();
        }

        //  三段式版本号比较（v 前缀可有可无；缺段按 0）。返回 >0 = a 更新，<0 = b 更新，0 = 相同。
        //  只为本程序的 self-update 服务：tag 形如 v1.3.0 / 1.3.0，其它格式一律按 0 段算（不误报更新）。
        static int CmpVer(string a, string b)
        {
            int[] pa = VerParts(a), pb = VerParts(b);
            for (int i = 0; i < 3; i++)
                if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
            return 0;
        }
        static int[] VerParts(string v)
        {
            int[] r = new int[3];
            if (v == null) return r;
            v = v.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            string[] seg = v.Split('.');
            for (int i = 0; i < 3 && i < seg.Length; i++)
            {
                int n;
                if (int.TryParse(seg[i].Trim(), out n) && n >= 0) r[i] = n;
                else return new int[3];                     // 非数字段（beta1 之类）→ 不参与比较
            }
            return r;
        }

        // 「下载源 / 资源包」：改 GitHub 仓库地址与"要不要连带超分组件"
        void DoUpstreamSettings()
        {
            string cur = Cfg.DlssgGitHub;
            if (cur == null || cur.Length == 0) cur = Upstream.Repo();
            string v = Prompt("GitHub 下载源",
                "留空或写错会退回作者那个仓库（sdli1995/dlssg_for_sm86）。\n"
                + "想跟着别人 fork 走，就把它的地址填在这里（支持完整 URL 或 owner/repo）。", cur);
            if (v == null) return;
            v = v.Trim();
            if (v.Length > 0)
            {
                Cfg.DlssgGitHub = v;
                Program.SetConfigStr("dlssg", "github", v);
            }
            string sr = Prompt("0.4.x 的超分组件",
                "导入资源包时是否连另 sr\\ 一起落盘（约 186 MB）。\n"
                + "[SuperResolution] Mode=On 才用得上，出厂是 Off。\n\n"
                + "填 1 或 yes 表示要，其它表示不要。", Cfg.DlssgSr ? "1" : "0");
            if (sr != null)
            {
                bool want = sr.Trim() == "1" || sr.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
                         || sr.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                Cfg.DlssgSr = want;
                Program.SetConfigBool("dlssg", "superResolutionFiles", want);
            }
            Log("下载源已设为 " + Upstream.Repo() + "；代理 " + (Cfg.DlssgUseProxy ? Cfg.DlssgProxy : "直连")
                + "（代理开关在「设置」页）");
            Log("超分组件（186 MB）：" + (Cfg.DlssgSr ? "下次导入时一并落盘" : "不落盘（开 SuperResolution 时再导一次）"));
        }

        // 「从 GitHub 取新版」：查 release → 下 asset → 交给 Pack 识别归位
        void DoUpstreamFetch()
        {
            if (upstreamBusy) { Log("  正在取新版，稍等"); return; }
            upstreamBusy = true;
            if (btnUpRelease != null) btnUpRelease.Enabled = false;
            // 每一步都打进日志：卡在查询、下载还是识别阶段，用户一眼能看出来
            Log(">>> 查询 " + Upstream.Repo() + " 的最新 release …"
                + (Cfg.DlssgUseProxy ? "（经代理 " + Cfg.DlssgProxy + "）" : "（直连）"));

            var gw = new BackgroundWorker();
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                string err;
                List<Upstream.Release> list = Upstream.Fetch(5, out err);
                e.Result = new object[] { list, err };
            };
            gw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                try
                {
                    object[] pack = e.Result as object[];
                    List<Upstream.Release> list = pack != null ? pack[0] as List<Upstream.Release> : null;
                    string err = pack != null ? pack[1] as string : "";
                    if (list == null || list.Count == 0)
                    {
                        Log("  没取到 release：" + (err.Length > 0 ? err : "返回为空"));
                        Log("  提示：国内网络建议到「设置」页把代理打开并填对本机代理端口（默认 127.0.0.1:7890）");
                        return;
                    }
                    foreach (Upstream.Release r in list) Log(r.Line());
                    Upstream.Release top = PickZip(list);
                    if (top == null)
                    {
                        Log("  这些 release 里没有打包好的 zip（作者从 0.4.0 才开始附二进制；"
                            + "老版本只有源码，拿不到可用资源包）。");
                        return;
                    }
                    AskDownLoadRelease(top);
                }
                finally
                {
                    upstreamBusy = false;
                    if (btnUpRelease != null) btnUpRelease.Enabled = true;
                }
            };
            gw.RunWorkerAsync();
        }

        // 最新的一个带 zip 的版本（0.3.5 及以前只有源码，跳过去）
        static Upstream.Release PickZip(List<Upstream.Release> list)
        {
            foreach (Upstream.Release r in list)
                if (r != null && r.HasZip) return r;
            return null;
        }

        void AskDownLoadRelease(Upstream.Release r)
        {
            string local = Dlssg040.Ver;
            bool same = string.Equals(local, r.Tag, StringComparison.OrdinalIgnoreCase);
            if (MessageBox.Show(
                "GitHub 上有：" + r.Tag + "  " + r.Asset + "  " + Math.Round(r.Size / 1048576.0, 1) + " MB\n"
                + (r.At.Length >= 10 ? "发布时间 " + r.At.Substring(0, 10) + "\n" : "")
                + "本机资源包现在是 " + local + (same ? "（与它同一版 —— 重复导一次只会浪费流量）" : "") + "\n\n"
                + "下载后会自动识别、校验并归位到资源包目录，然后问你要不要重抄到已装的游戏。\n\n现在下载吗？",
                "从 GitHub 取新版 · " + Upstream.Repo(), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            upstreamBusy = true;
            if (btnUpRelease != null) btnUpRelease.Enabled = false;
            Log(">>> 下载 " + r.Asset + "（" + Math.Round(r.Size / 1048576.0, 1) + " MB）…");
            var gw = new BackgroundWorker();
            string localZip = null, err = null;
            gw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                localZip = Upstream.Download(r, delegate(string m) { Log(m); }, out err);
            };
            gw.RunWorkerCompleted += delegate
            {
                upstreamBusy = false;
                if (btnUpRelease != null) btnUpRelease.Enabled = true;
                try
                {
                    if (localZip == null)
                    {
                        Log("  下载失败：" + err);
                        return;
                    }
                    Log("  已下载 → " + localZip);
                    //  统一走 StartPackImport 的两段式（Inspect 后台 + 交互回 UI）——
                    //  原来这里在 UI 线程同步 Inspect 大 zip，同样是"未响应"的来源之一
                    StartPackImport(new List<string> { localZip });
                }
                finally { RefreshFgSummary(); }
            };
            gw.RunWorkerAsync();
        }

        //  多个包时的挑件列表（文件夹浏览之后才会出现）：只列该文件夹里的 zip，行 = 名字·大小·时间。
        //  双击 = 导入；逻辑仍走 StartPackImport 两段式（重活后台）。
        void ShowPackZipList(List<string> zips, string folder)
        {
            var dlg = new Form();
            Fx.FadeOnClose(dlg);
            dlg.Text = "导入资源包 · " + Path.GetFileName(folder.TrimEnd('\\')) + "（" + zips.Count + " 个包）";
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.ShowInTaskbar = false; dlg.ShowIcon = false;
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(360));
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var tip = new TxtLbl();
            tip.Text = "这个文件夹里有 " + zips.Count + " 个插件包（新→旧）。点选一个 → 「导入选中」。";
            tip.AutoSize = true;
            tip.ForeColor = Theme.TextDim;
            tip.Font = Theme.F(9f);
            tip.Location = new Point(Theme.S(16), Theme.S(12));
            dlg.Controls.Add(tip);

            var lb = new ListBox();
            lb.Font = Theme.F(9.5f);
            lb.ItemHeight = Theme.S(22);
            lb.BorderStyle = BorderStyle.FixedSingle;
            lb.HorizontalScrollbar = true;
            var picked = new List<string>();
            foreach (string z in zips)
            {
                picked.Add(z);
                FileInfo fi = new FileInfo(z);
                string row = string.Format("{0}　·　{1:0.0} MB　·　{2:MM-dd HH:mm}",
                    Path.GetFileName(z), fi.Length / 1048576.0, fi.LastWriteTime);
                int maxW = lb.ClientSize.Width - Theme.S(24);
                while (row.Length > 6 && TextRenderer.MeasureText(row, lb.Font).Width > maxW)
                    row = row.Substring(0, row.Length - 2).TrimEnd() + "…";
                lb.Items.Add(row);
            }
            if (picked.Count > 0) lb.SelectedIndex = 0;
            lb.Location = new Point(Theme.S(16), Theme.S(40));
            lb.Size = new Size(dlg.ClientSize.Width - Theme.S(32), dlg.ClientSize.Height - Theme.S(108));
            dlg.Controls.Add(lb);

            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnCancel.Location = new Point(dlg.ClientSize.Width - Theme.S(100), dlg.ClientSize.Height - Theme.S(52));
            btnCancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(btnCancel);

            var btnGo = new FlatBtn(); btnGo.Text = "导入选中"; btnGo.Kind = BtnKind.Primary; btnGo.Width = Theme.S(110);
            btnGo.Location = new Point(dlg.ClientSize.Width - Theme.S(224), dlg.ClientSize.Height - Theme.S(52));
            btnGo.Click += delegate
            {
                if (lb.SelectedIndex < 0 || lb.SelectedIndex >= picked.Count) return;
                string pick = picked[lb.SelectedIndex];
                dlg.Close();
                StartPackImport(new List<string> { pick });   // 重活在后台（两段式），窗口不卡
            };
            dlg.Controls.Add(btnGo);
            lb.DoubleClick += delegate { btnGo.PerformClick(); };

            dlg.ShowDialog(this);
        }

        // 下载的 zip 走 StartPackImport 的统一两段式（识别 → 校验 → 归位），与拖入/按钮完全一致。
        //  旧的 ImportDownloaded 已删：它在 UI 线程同步做"解压整包 + 逐件 SHA256"，大包必卡死 UI。
        //  （它尾巴上的 OfferRedeploy 也一并删了 —— ShowPackDialog 的导入问句里已含"要不要重抄"，
        //    同一件事不再有第二个入口。）

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
            Cfg.DlssgNrPreSR = cbNrPos.SelectedIndex == 1;
            Program.SetConfigStr("dlssg", "router", Cfg.DlssgRouter);
            Program.SetConfigStr("dlssg", "kernelImage", Cfg.DlssgKernel);
            Program.SetConfigInt("dlssg", "hardwareBilinear", Cfg.DlssgBilinear);
            Program.SetConfigInt("dlssg", "maxGeneratedFrames", Cfg.DlssgMaxFrames);

            // 6X 不是"越高越好"：XDA 实测（4070 Ti Super /《Backrooms》）
            //   4X = 296 平均 / 243 1% low / 29.1ms；6X = 351 平均 / 172 1% low / 37.5ms
            // 165Hz 这类屏上抬到 6X，平均帧好看，但 1% low 与延迟都退步。
            if (Cfg.DlssgMaxFrames >= 5)
            {
                int hz6 = Program.RefreshHz();
                if (hz6 > 0 && hz6 < 240)
                    Log("  ⚠ 选了 6X，但显示器只有 " + hz6 + "Hz：实测 4X→6X 的 1% low 从 243 掉到 172、"
                        + "延迟 29.1→37.5ms —— 平均帧更高但手感更差。这一档建议留在 4X。");
            }
            Program.SetConfigInt("dlssg", "loggingLevel", Cfg.DlssgLogLevel);
            Program.SetConfigBool("dlssg", "nrEnabled", Cfg.DlssgNrEnabled);
            Program.SetConfigBool("dlssg", "nrPreSR", Cfg.DlssgNrPreSR);

            // 逐游戏：先看这个目录里到底有没有我们的代理、是哪一代，再决定写什么。
            //  2026-09-16 事故：旧逻辑对所有游戏写同一份 261 字节模板 + Config 里的 maxGeneratedFrames(=5)，
            //  而旧版代理（14.9 MB / 310.1 运行库）只认 1..3 → 报 "Invalid INI integer" →
            //  FH6 直接卡死在加载界面。写之前必须先认代理，写的时候必须按"代理年代"夹上限。
            int n030 = 0, nOld = 0, nSkip = 0, nOpti = 0, n040 = 0;
            foreach (var g in games)
            {
                if (g == null || g.Dir == null) continue;
                //  异环这类"启动器层 / 渲染层"分开的目录，改 ini 也要落在渲染层
                string rdir = SecondGame.ResolveDir(g.Dir);
                string gdir = XeMfg.TargetDir(rdir, XeMfg.Detect(rdir));
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
                        //  每游戏覆盖（第 160 轮）：EffFgMax 解析"本游戏生效值"，覆盖了才跟全局不同
                        int m30 = EffFgMax(g);
                        string r = Dlssg030.SetMaxFrames(gdir, m30);
                        if (r.IndexOf("已设为") >= 0) n030++;
                        Log("  0.3.5 · " + g.Title + "：" + r
                            + (g.FgMax > 0 ? "（本游戏覆盖）" : ""));
                    }
                    else if (own == Plan.D040)
                    {
                        // 0.4.x 的 INI 是另一套布局（Optimized / Preset 搬到 [DLSSG]，多一组 [XeSSFG]），
                        //  只能改 MaxGeneratedFrames 这一行，整份重写会把新段的键全丢掉。
                        //  出厂是 3 = 4X；这里照"本游戏生效值"显式写（第 160 轮起支持每游戏覆盖）。
                        int m40 = EffFgMax(g);
                        string r = Dlssg040.SetMaxFrames(gdir, m40);
                        if (r.IndexOf("已写入") >= 0) n040++;
                        Log("  0.4.x · " + g.Title + "：" + r
                            + (m40 < 5 ? "（要 6X 选「5 · 6X」并把 MaxGeneratedFrames 改成 5）" : "")
                            + (g.FgMax > 0 ? "（本游戏覆盖 " + m40 + "）" : ""));
                    }
                    else if (own == Plan.Legacy)
                    {
                        int m = Math.Min(EffFgMax(g), 3);
                        File.WriteAllText(Path.Combine(gdir, Dlssg.IniName),
                                          Dlssg.BuildIniFor(m, Cfg.DlssgLogLevel), new UTF8Encoding(false));
                        nOld++;
                        if (m != Cfg.DlssgMaxFrames)
                            Log("  " + g.Title + "：装的是旧版代理（上限 4X），已按 MaxGeneratedFrames=3 写入"
                                + "；如需多倍补帧请在「概览」中点击「安装并应用」部署方案");
                    }
                    else if (own == Plan.Nvbr)
                    {
                        //  方案 B 的倍率是**游戏内 Home 键覆盖**的（与它的 ini 里 FGMode 是两回事），
                        //  本工具不代改它的 ini —— 段名与键位不写死猜测值。
                        Log("  方案 B · " + g.Title + "：倍率在游戏内按 Home 键调（覆盖菜单需先开 EnableFrameGenOverrideMenu）");
                    }
                    else // Plan.Opti：方案A 在用。OptiScaler 的参数归 OptiScaler.ini / 游戏内 F10 菜单调，
                         // 它根本不读 dlssg_sm86.ini —— 绝不能把旧模板写进去（2026-09-16 02:40 事故根源）
                    {
                        // 唯一的例外：DLSS 5 神经渲染的总开关。它只改 [DlssNr] Enabled 一行，
                        //  且这是**用户明确要求可调**的一项（此前被 2.9.1 固化成 true，每次启动必开）。
                        string nr = XeMfg.SetNrEnabled(gdir, Cfg.DlssgNrEnabled);
                        if (nr.IndexOf("已") >= 0) nOpti++;
                        Log("  方案A · " + g.Title + "：" + nr);
                        //  NR 位置：NR 开着才写。注意**两种取值都要写** —— 用户从 Pre-SR 关回默认时，
                        //  ini 里还留着 RunBeforeSR=true，不写 false 就等于关不掉。
                        if (Cfg.DlssgNrEnabled)
                            Log("  方案A · " + g.Title + "：" + XeMfg.SetNrPreSR(gdir, Cfg.DlssgNrPreSR));
                    }
                }
                catch { }
            }
            Log("帧生成参数已保存（Router=" + Cfg.DlssgRouter + " 内核=" + Cfg.DlssgKernel +
                " 光流=" + Cfg.DlssgBilinear + " 最大帧=" + Cfg.DlssgMaxFrames + " 日志=" + Cfg.DlssgLogLevel + "）");
            if (n030 > 0) Log("  已同步 " + n030 + " 个 0.3.5 部署游戏的倍率上限（需重启游戏生效）");
            if (n040 > 0) Log("  已同步 " + n040 + " 个 0.4.x 部署游戏的倍率上限（需重启游戏生效）");
            if (nOld > 0) Log("  已更新 " + nOld + " 个旧版代理游戏的配置文件（上限 4X，需重启游戏生效）");
            if (nSkip > 0) Log("  跳过 " + nSkip + " 个未接入本方案的游戏（不写它们的配置文件）");
            if (nOpti > 0) Log("  跳过 " + nOpti + " 个方案A游戏（OptiScaler 的参数在游戏内 F10 菜单调，不写它的 ini）");
        }

        // ---------------------- 托盘 ----------------------
        void BuildTray()
        {
            var menu = MenuFactory.New();

            miStatus = new ToolStripMenuItem("● 待命中");
            miStatus.Tag = "status";
            miStatus.Enabled = false;
            miStatus.Font = Theme.F(9f);
            menu.Items.Add(miStatus);
            menu.Items.Add(new ToolStripSeparator());

            var miOpen = new ToolStripMenuItem("打开主窗口");
            miOpen.Font = Theme.FB(9.5f);
            miOpen.Click += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            menu.Items.Add(miOpen);

            if (!Program.ShareBuild)
            {
                menu.Items.Add(new ToolStripSeparator());
                var miOpt = new ToolStripMenuItem("一键优化");
                miOpt.Click += delegate { DoOptimize(); };
                menu.Items.Add(miOpt);

                var miRestore = new ToolStripMenuItem("恢复备份");
                miRestore.Click += delegate { DoRestore(); };
                menu.Items.Add(miRestore);
            }

            menu.Items.Add(new ToolStripSeparator());
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate
            {
                closing = true;
                if (!Program.ShareBuild) try { Program.GameBoostCleanupOrphans(); } catch { }
                tray.Visible = false;
                Application.Exit();
            };
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            try { tray.Icon = Program.GetAppIcon(); } catch { }
            tray.Text = "Fluxion - 待命中";
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

        //  无边框对话框拖动（标题栏区域按下 = 拖窗口）
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        //  自绘标题栏要用：算"窗口矩形 vs 客户区矩形"的差值（见 WndProc / WM_NCCALCSIZE）
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out NcRect r);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // 关闭确认框：返回值 false = 用户取消（窗口不关）。
        // toTray / remember 通过 out 回传，正文里那句"稍后可在设置中修改此行为"对应的是
        // 自定义优化项里的「关闭窗口时询问」开关（ui.closeAsk）。
        //  只负责"把对话框造出来"：ShowDialog 与结果解读留给 AskCloseAction ——
        //  截屏探针（ui_snapshot closedlg）要 Show 这个真实实例，不能再复制一份 UI 代码。
        Form BuildCloseDialog(out RadioButton rbTrayOut, out CheckBox cbRememberOut)
        {
            bool toTray = Cfg.CloseToTray;

            //  无边框自绘（2026-10-18）：原来是 FormBorderStyle.FixedDialog + 套一层 Pg ——
            //  白色系统标题栏、直角、Pg 自带的滚动条与顶部渐变全挤在这 420×268 里，
            //  最后那句提示被裁成半行。现在标题栏自己画，正文不再走页面容器。
            var dlg = new Form();
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
            dlg.FormBorderStyle = FormBorderStyle.None;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ShowInTaskbar = false;
            dlg.ShowIcon = false;
            dlg.BackColor = Theme.Panel;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.KeyPreview = true;
            dlg.ClientSize = new Size(Theme.S(440), Theme.S(246));

            //  ---- 标题栏（自绘：标题 + 关闭）----
            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = Theme.S(46);
            head.BackColor = Theme.Bg;
            Buffered(head);
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Theme.DrawTxt(e.Graphics, "关闭窗口", Theme.FB(12f),
                    new Rectangle(Theme.S(20), 0, head.Width - Theme.S(68), head.Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            var btnX = new TxtLbl();
            btnX.Text = "×";
            btnX.Dock = DockStyle.Right;
            btnX.Width = Theme.S(44);
            btnX.TextAlign = ContentAlignment.MiddleCenter;
            btnX.Font = Theme.F(13f);
            btnX.ForeColor = Theme.TextDim;
            btnX.BackColor = Theme.Bg;
            btnX.Cursor = Cursors.Hand;
            btnX.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            btnX.MouseEnter += delegate { btnX.ForeColor = Theme.Text; };
            btnX.MouseLeave += delegate { btnX.ForeColor = Theme.TextDim; };
            head.Controls.Add(btnX);

            //  ---- 正文（纵向流式，不套 Pg：不会再出现滚动条与渐变裁切）----
            var body = new FlowLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.FlowDirection = FlowDirection.TopDown;
            body.WrapContents = false;
            body.AutoScroll = false;
            body.BackColor = Theme.Bg;
            body.Padding = new Padding(Theme.S(20), Theme.S(2), Theme.S(20), Theme.S(4));

            var desc = new TxtLbl();
            desc.AutoSize = true;
            desc.MaximumSize = new Size(Theme.S(392), 0);
            desc.ForeColor = Theme.TextDim;
            desc.BackColor = Theme.Bg;
            desc.Margin = new Padding(0, 0, 0, Theme.S(10));
            desc.Text = Program.ShareBuild
                ? "帧生成诊断与运行模式确认需要程序保持后台运行。"
                : "游戏联动、硬件告警、帧生成诊断需要程序保持后台运行。";
            body.Controls.Add(desc);

            var rbTray = new RoundRadio(); Theme.StyleRadio(rbTray, "最小化到通知区域图标（后台继续运行）");
            var rbExit = new RoundRadio();
            Theme.StyleRadio(rbExit, Program.ShareBuild ? "退出程序" : "退出程序（联动与告警一并停止）");
            rbTray.Checked = toTray; rbExit.Checked = !toTray;
            rbTray.Margin = new Padding(0, 0, 0, Theme.S(6));
            rbExit.Margin = new Padding(0, 0, 0, Theme.S(8));
            body.Controls.Add(rbTray);
            body.Controls.Add(rbExit);

            var cbRemember = new RoundCheck();
            Theme.StyleCheck(cbRemember, "记住我的选择，不再询问");
            cbRemember.Margin = new Padding(0, 0, 0, Theme.S(6));
            body.Controls.Add(cbRemember);

            var hint = new TxtLbl();
            hint.AutoSize = true;
            hint.MaximumSize = new Size(Theme.S(392), 0);
            hint.Font = Theme.F(8f);
            hint.ForeColor = Theme.TextDim;
            hint.BackColor = Theme.Bg;
            hint.Margin = new Padding(0);
            hint.Text = "勾选后不再问；恢复去「设置 → 窗口与托盘」。";
            body.Controls.Add(hint);

            //  ---- 底部按钮 ----
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(58);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Bg;
            bar.Padding = new Padding(0, Theme.S(10), Theme.S(18), 0);
            var btnOk = new FlatBtn(); btnOk.Text = "确认"; btnOk.Kind = BtnKind.Primary; btnOk.Width = Theme.S(88);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(88);
            btnOk.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnOk);

            dlg.Controls.Add(body);
            dlg.Controls.Add(bar);
            dlg.Controls.Add(head);
            dlg.AcceptButton = btnOk; dlg.CancelButton = btnCancel;

            //  圆角 + 1px 描边：Region 是硬边，但配合底色一致的外框看不出来
            Action round = delegate
            {
                try
                {
                    if (dlg.Region != null) dlg.Region.Dispose();
                    dlg.Region = new Region(Theme.Round(new Rectangle(0, 0, dlg.Width, dlg.Height), Theme.S(10)));
                }
                catch { }
            };
            dlg.Resize += delegate { round(); };
            dlg.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.EdgeRound(e.Graphics, new Rectangle(0, 0, dlg.Width - 1, dlg.Height - 1), Theme.S(10), Theme.Line, 1f);
            };

            //  按住标题栏拖窗口（无边框窗口默认不能拖）
            MouseEventHandler drag = delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(dlg.Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            };
            head.MouseDown += drag;
            round();

            rbTrayOut = rbTray;
            cbRememberOut = cbRemember;
            return dlg;
        }

        // 关闭确认框：返回值 false = 用户取消（窗口不关）。
        // toTray / remember 通过 out 回传，正文里那句"稍后可在设置中修改此行为"对应的是
        // 设置页「窗口与托盘」里的「关闭窗口时询问」。
        bool AskCloseAction(out bool toTray, out bool remember)
        {
            toTray = Cfg.CloseToTray; remember = false;
            RadioButton rb; CheckBox cb;
            var dlg = BuildCloseDialog(out rb, out cb);
            var r = dlg.ShowDialog(this);
            bool ok = r == DialogResult.OK;
            if (ok) { toTray = rb.Checked; remember = cb.Checked; }
            try { dlg.Dispose(); } catch { }
            return ok;
        }
        // ---------------------- 日志 ----------------------
        //  页脚短提示（不动日志文件）：参照 FileButler —— 页脚是"状态"，不是"日志窗口"。
        //  扫描摘要那类明细只该写进日志，页脚留一句"扫描完成 · 12 款"就够；
        //  以前把整串新增名单塞进页脚，一条提示铺满整行、还被 96 字截成半句话。
        void Foot(string msg)
        {
            if (msg == null) return;
            string t = CleanFoot(msg);
            bool warn = FootWarn(t);
            Action put = delegate
            {
                try
                {
                    if (lblFoot == null || lblFoot.IsDisposed) return;
                    lblFoot.Text = t;
                    lblFoot.ForeColor = warn ? Theme.Warn : Theme.TextDim;
                    footDot = warn ? Theme.Warn : Theme.Ok;
                    footAt = Environment.TickCount;
                    if (dotFoot != null && !dotFoot.IsDisposed) dotFoot.Invalidate();
                }
                catch { }
            };
            if (lblFoot != null && lblFoot.InvokeRequired) lblFoot.BeginInvoke(put);
            else put();
        }

        //  状态灯语义：最后一条是警告/失败/要重试时变黄 —— "专业工具"和"贴了个绿点"的区别。
        static bool FootWarn(string t)
        {
            return t.IndexOf('⚠') >= 0
                || t.IndexOf("失败", StringComparison.Ordinal) >= 0
                || t.IndexOf("错误", StringComparison.Ordinal) >= 0
                || t.IndexOf("异常", StringComparison.Ordinal) >= 0
                //  2026-10-23：把"慢 / 要重试"也算警示 —— 以前「体检数据读取较慢
                //  （6.7 秒）· 点「刷新」可重试」配的是**绿点**，绿在本项目里是
                //  "已生效/成功"，一条提醒配成功色，是语义层面的廉价感。
                || t.IndexOf("较慢", StringComparison.Ordinal) >= 0
                || t.IndexOf("超时", StringComparison.Ordinal) >= 0
                || t.IndexOf("重试", StringComparison.Ordinal) >= 0;
        }

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
                    string t = CleanFoot(msg);
                    bool warn = FootWarn(t);
                    Action put = delegate
                    {
                        try
                        {
                            if (lblFoot == null || lblFoot.IsDisposed) return;
                            lblFoot.Text = t;
                            lblFoot.ForeColor = warn ? Theme.Warn : Theme.TextDim;
                            footDot = warn ? Theme.Warn : Theme.Ok;
                            footAt = Environment.TickCount;
                            if (dotFoot != null && !dotFoot.IsDisposed) dotFoot.Invalidate();
                        }
                        catch { }
                    };
                    if (lblFoot.InvokeRequired) lblFoot.BeginInvoke(put);
                    else put();
                }
            }
            catch { }
            Program.Log(msg);
        }

        // 首屏淡入：窗口不是"啪"地出现，而是 160ms 淡到全显。
        //  尊重系统动画开关 —— Motion.Enabled 为 false 时完全不碰 Opacity。
        //  ⚠ 只跑 160ms 且中途不做布局；结束后立刻复位成 1，
        //    否则整窗长期走 DWM 合成会拖慢自绘。
        void FadeInWindow()
        {
            try
            {
                if (!Motion.Enabled) { Opacity = 1; return; }
                Opacity = 0;
                if (fadeTimer != null) { try { fadeTimer.Stop(); fadeTimer.Dispose(); } catch { } }
                long t0 = Motion.Now;
                fadeTimer = new Timer();
                fadeTimer.Interval = 16;
                fadeTimer.Tick += delegate
                {
                    try
                    {
                        double x = (Motion.Now - t0) / 160.0;
                        if (x >= 1.0) { Opacity = 1; fadeTimer.Stop(); return; }
                        Opacity = 0.2 + 0.8 * Motion.Ease(Motion.Decel, x);
                    }
                    catch { Opacity = 1; }
                };
                fadeTimer.Start();
            }
            catch { Opacity = 1; }
        }

        // 状态栏只留"一句人话"。
        //  原来是把日志原样截断塞进来，于是屏幕上常年挂着
        //  `>>> 注意: 有 2 个目录在忽略清单里（列表中已灰显）· 5EClient (C:\Program Files\...`
        //  —— 一眼就是"内部工具"。这里去掉日志前缀、把长路径中间省略、限长。
        static string CleanFoot(string msg)
        {
            if (msg == null) return "";
            string s = msg.Trim();
            while (s.StartsWith(">>>", StringComparison.Ordinal)) s = s.Substring(3).TrimStart();
            // 路径中间省略：G:\a\b\c\game.exe → G:\…\game.exe（保留盘符与文件名）
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"([A-Za-z]:\\)[^\\\r\n]{1,120}\\([^\\\r\n]{1,80})", "$1…\\$2");
            if (s.Length > 96) s = s.Substring(0, 95) + "…";
            return s;
        }

        //  把今天日志的最后一段重新灌进控件（换主题重建后用）。只读文件，不改任何东西。
        void ReplayLogTail()
        {
            try
            {
                string f = Program.LogToday();
                if (!File.Exists(f)) return;
                string[] lines = File.ReadAllLines(f);
                int from = Math.Max(0, lines.Length - 200);
                for (int i = from; i < lines.Length; i++)
                {
                    string s = lines[i].TrimEnd('\r');
                    if (s.Length > 0) AppendLog(s);
                }
                lastLogLen = new FileInfo(f).Length;
            }
            catch { }
        }

        //  日志卡高度分档（第 147 轮）：档内不变，跨档才触发 Resync。
        //  原因：日志是流式追加的，若每行都改高度，每次 Resync 都是整页重排 + 滚动范围重算。
        static int LogBoxHeight(int n)
        {
            if (n <= 3) return Theme.S(80);
            if (n <= 8) return Theme.S(140);
            if (n <= 16) return Theme.S(200);
            return Theme.S(260);
        }

        void ResizeLogBox()
        {
            if (logBox == null || logBox.IsDisposed || logSec == null) return;
            int want = LogBoxHeight(Math.Max(1, logLines));
            if (logBox.Height == want) return;
            logBox.Height = want;
            logSec.Resync();          // 自绘容器高度变化必须通知父分区，否则 Pg 滚动范围停在旧值
        }

        void AppendLog(string line)
        {
            if (log == null || log.IsDisposed) return;
            if (logVirgin)
            {
                logVirgin = false;
                log.Clear();
                if (lblLogEmpty != null && !lblLogEmpty.IsDisposed) lblLogEmpty.Visible = false;
                log.Visible = true;                     // 与空态互斥，见 BuildPageMonitor 的注释
            }
            //  裁剪与"贴底跟随"都由 LogView 自己管（翻看历史时不会被新日志拽回底部）
            log.Append(line);
            logLines = log.LineCount;
            ResizeLogBox();           // 日志卡高度跟行数走（档内不变，见 LogBoxHeight）
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
            //  最小化（不是托盘）也算后台：任务栏里挂着的那份不需要 1Hz 的时钟重绘
            try { SetBgMode(WindowState == FormWindowState.Minimized); } catch { }
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
            //  ★ 自绘标题栏（2026-10-23，参照 FileButler 的 frameless 做法）：
            //    保留原生边框（左右下三边 + 阴影 + Win11 圆角 + Aero Snap），只把**系统标题栏
            //    那一段**从非客户区里减掉，让客户区顶到窗口上沿，我们在那儿画自己的栏。
            //    这样不用处理 Region / 自绘阴影 / 自绘缩放热区 —— 三处最容易翻车的地方都留给系统。
            //  ★ 2026-10-30 第 171 轮：窗体已改 FormBorderStyle.None（无边框），
            //    不再有非客户区、也就不再需要"从 NC 里减掉标题栏那段"的推算 ——
            //    WM_NCCALCSIZE 分支整段删除。旧实现靠 `topNC − leftNC` 反推标题栏高度，
            //    Win11 26H2 下退化成 0 ⇒ 系统标题栏残留（双层标题栏）。删掉即根治。
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                var pt = PointToClient(new Point(x, y));

                //  ★ 缩放热区（2026-10-30 第 171 轮）：无边框后系统不再提供边框可拖缩放，
                //    这里自己给一圈 6px 热区（四角放大到 12px，手型光标由系统按返回值给）。
                //    最大化和右侧/底部内容区不受影响 —— 只有贴边那 6px 变成缩放。
                int bw = Theme.S(6);
                if (WindowState != FormWindowState.Maximized)
                {
                    bool l = pt.X < bw, r = pt.X >= Width - bw;
                    bool b = pt.Y >= Height - bw;
                    int hit = 0;
                    if (l && b) hit = HTBOTTOMLEFT;
                    else if (r && b) hit = HTBOTTOMRIGHT;
                    else if (l) hit = HTLEFT;
                    else if (r) hit = HTRIGHT;
                    else if (b) hit = HTBOTTOM;
                    //  ⚠ 顶部**不给**缩放热区：那一条要留给拖动（窗口顶沿是最自然的抓手，
                    //     改成缩放会让"抓着顶边拖窗口"变成改窗口高度）。
                    if (hit != 0) { m.Result = (IntPtr)hit; return; }
                }

                //  把"我们的标题栏"整条变成可拖动的 HTCAPTION（窗口按钮除外，否则点不到按钮）。
                //  拿到 HTCAPTION 之后，拖动 / 双击最大化 由系统接管，不用自己写。
                //  +S(10) 是手感余量：页头下沿那一条也让它能拖，不用精准压在 64px 里。
                if (m.Result.ToInt32() == HTCLIENT
                    && pt.Y >= 0 && pt.Y < Theme.S(TitleH) + Theme.S(10) && !OnWindowButton(pt))
                    m.Result = (IntPtr)HTCAPTION;
                return;
            }
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
            if (WM_ACTIVATE_INSTANCE != 0 && m.Msg == WM_ACTIVATE_INSTANCE)
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Show();
                Native.ShowWindow(Handle, Native.SW_RESTORE);
                Native.SetForegroundWindow(Handle);
                Activate();
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

        static bool IsImageFile(string p)
        {
            string ext = Path.GetExtension(p == null ? "" : p).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
        }

        //  光标下最深的控件，再向上找第一张卡片

        bool packBusy;
        string lastPackDir = "";            // 上次导入/扫描的文件夹 —— 下次「导入资源包」优先扫它（2026-11-01）

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

            //  单张图片 → 设为封面（2026-10-22，2026-10-23 放宽）：
            //  ⚠ 卡片自己不能设 AllowDrop：卡片会被 FillGameList 反复重建，OLE 的
            //  RegisterDragDrop 会在同一 HWND 上重复注册，抛「DragDrop 注册失败。」。
            //  所以统一在窗口这一层分流 —— WM_DROPFILES 不带落点，用光标位置做命中测试；
            //  没落在任何卡片上就给**当前选中的游戏**（以前这时会掉进插件包导入，
            //  把一张 png 当插件包解，报一堆无关错误）。
            if (paths.Count == 1 && IsImageFile(paths[0]))
            {
                //  第 123 轮：库里没有卡片了，拖图就设成**当前选中**那一行的封面
                DlssgGame tgt = selected;
                if (tgt != null)
                {
                    string err = CoverArt.SetManualCover(tgt, paths[0]);
                    if (err.Length == 0)
                    {
                        Log("  已设置封面（拖入）：" + tgt.Title + " ← " + Path.GetFileName(paths[0]));
                        AfterCoverChanged();
                    }
                    else Log("  设置封面失败：" + err);
                    return;
                }
            }
            StartPackImport(paths);
        }

        // 解压 + 识别放后台线程：一个 0.3.x 的包解压出来约 250 MB，扔在 UI 线程上会白屏两秒。
        void StartPackImport(List<string> paths)
        {
            if (packBusy) { Log("  上一次插件包导入还没结束，请稍候再拖"); return; }
            //  rar 不认（Pack 只会解 zip）：给一句人话指引，别让用户对着"识别失败"发懵
            var usable = new List<string>();
            foreach (string p in paths)
            {
                if (string.Equals(Path.GetExtension(p), ".rar", StringComparison.OrdinalIgnoreCase))
                {
                    Log("  " + Path.GetFileName(p) + " 是 .rar —— 工具只认 zip 或解压后的文件夹。请先右键解压，再导入解压出来的内容");
                    continue;
                }
                usable.Add(p);
            }
            if (usable.Count == 0) return;
            paths = usable;
            packBusy = true;
            Log(">>> 插件包导入：共 " + paths.Count + " 项（重活已在后台跑，窗口不会卡）");
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

        // 部署前置：对"这次要写进游戏目录的**全部**源文件"核验一次。
        //  只查入口是不够的 —— 0.4.x 的后端 / 内核包 / XeSS 模型同样是从资源包原样复制进游戏目录、
//  同样会进游戏进程。这里只负责把"谁签的"逐条列清楚，装不装由用户按确认键。
        bool TrustGateFiles(List<string> paths, string what)
        {
            if (paths == null) return true;
            List<string> good = new List<string>(), bad = new List<string>();
            foreach (string p in paths)
            {
                if (p == null || p.Length == 0 || !File.Exists(p)) continue;
                CertInfo c = ProxySign.Of(p);
                string name = Path.GetFileName(p);
                if (c.Ok) good.Add(name);
                else bad.Add(name + " — " + ProxySign.Short(c));
            }
            Log("签名核验（这次会写进游戏目录的文件）：");
            foreach (string s in good) Log("  ✔ " + s);
            foreach (string s in bad) Log("  ✘ " + s);
            if (bad.Count == 0) return true;
            return TrustGate(bad, what);
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
                    Log("  → 不在导入支持范围内，未做修改");
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
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
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
            sNote.Body("每项对应 config.json 的一个键，重启后仍保留；标「需重启」的当次不生效。\n"
                     + "只放「改坏能改回」的项；会让设备消失的那类只做只读核验。");
            pg.Add(sNote);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Bg;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存并应用"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(104);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            // ★ 只在 Show 之前 Reflow 等于白跑（2026-10-21 实测）：那一刻 Pg 的分区全都
            //   Visible=false（父窗体还没显示，Control.Visible 会一路上溯），LayoutOnce 把这些
            //   分区整片跳过 —— 宽度没给、DoLayout 没跑 —— 却照样把 lastW / lastCount 记成
            //   "已是最新"。于是弹出来就是一坨内容挤在左上角，而且之后再没人触发重排。
            //   真正的重排必须等 Shown。（同「一键优化」对话框，硬规则）
            pg.Reflow();
            dlg.Shown += delegate
            {
                try { pg.Reflow(); } catch { }
                if (focusCb != null) { try { focusCb.Focus(); } catch { } }
            };

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
        //  每游戏「启动参数 + 环境变量」（2026-10-26 第 148 轮）
        //  为什么要有：`LaunchArgs` 早就在（DX12 开关在写它），但**没有任何手动入口** ——
        //  想在启动时加 `-novid`、临时挂个 `DXVK_HUD=fps`，只能去翻 library.json 手改。
        //  只作用于当前游戏；留空与"从没配过"完全等价（不写任何默认值）。
        void ShowLaunchConfigDialog()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            DlssgGame g = selected;
            bool steam = g.Platform == "steam" && g.AppId != null && System.Text.RegularExpressions.Regex.IsMatch(g.AppId, @"^\d{1,12}$");

            var dlg = new Form();
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
            dlg.Text = "启动参数与环境变量 · " + g.Title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(520), Theme.S(480));
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(620));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;

            //  Body 的每一行都按"不折行"来写：Sec 的高度按换行符数量算，
            //  长句自动折行会顶出卡片圆角（这轮实测的溢出就是这么来的）。
            var sArgs = new Sec("启动参数");
            var fArgs = new RoundField(Theme.S(520), "例如  -novid  -windowed");
            fArgs.SetText(g.LaunchArgs == null ? "" : g.LaunchArgs);
            sArgs.Row("参数", fArgs);
            //  文案里的参数名直接引用常量：`ArgDx12` 是 `-use-d3d12`（不是很多游戏惯用的 `-dx12`），
            //  写死在文案里迟早对不上（这轮差点就写成 -dx12）。
            sArgs.Body("写法照游戏官方文档，空格分隔；值里带空格用双引号包起来。\n"
                     + "「" + Lib.ArgDx12 + "」不必手写 —— 右键「DX12 模式启动」会自动加/去。\n"
                     + (Lib.KnownLaunchArgs(g).Length > 0
                        ? "这个游戏留空也会自动带上内置参数 " + Lib.KnownLaunchArgs(g) + "；手动填写则覆盖内置。"
                        : "例：鸣潮 3.7+ 直启要带画质档参数 -krqlv=sd / hd / uhd。"));
            pg.Add(sArgs);

            var sEnv = new Sec("环境变量");
            var fEnv = new RoundMemo(Theme.S(520), Theme.S(150));
            fEnv.Box.Text = g.LaunchEnv == null ? "" : g.LaunchEnv.Replace("\n", "\r\n");
            sEnv.Row("变量", fEnv);
            //  Steam 的提醒并进这张卡的正文，不单开一张卡：
            //  为一句话占一整张卡片（约 90px）会把内容顶出一屏，用户还得滚一下才看得全。
            sEnv.Body("一行一条 KEY=VALUE；空行与 # 开头的行忽略。\n"
                     + "值里可以再有 =，只按第一个 = 切分。例：DXVK_HUD=fps"
                     + (steam ? "\n⚠ 这个游戏走 steam:// 协议启动，参数与环境变量都不会传进去。" : ""));
            pg.Add(sEnv);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Bg;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(96);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            //  ⚠ Show 之前 Reflow 等于白跑（会留下永久坍缩的假缓存）—— 硬规则，必须挂 Shown
            pg.Reflow();
            dlg.Shown += delegate
            {
                try { pg.Reflow(); } catch { }
                //  焦点不能留在第一个输入框上：RoundField 的占位提示是"聚焦即清空"，
                //  一打开就被清掉等于没有提示（浅色截图里那个空框就是这么来的）。
                try { dlg.ActiveControl = null; } catch { }
            };

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string envText = fEnv.Box.Text == null ? "" : fEnv.Box.Text;
            var bad = new List<string>();
            int ln = 0;
            foreach (string raw in envText.Replace("\r\n", "\n").Split('\n'))
            {
                ln++;
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                if (t.IndexOf('=') <= 0) bad.Add("第 " + ln + " 行（缺少 KEY=）");
            }
            if (bad.Count > 0)
            {
                MessageBox.Show(this, "环境变量这几行格式不对：\n\n" + string.Join("\n", bad.ToArray())
                    + "\n\n要写成 KEY=VALUE，一行一条。", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string args = fArgs.RealText;
            Lib.SetLaunchConfig(g, args, envText);
            int n = Lib.ParseEnv(envText).Count;
            Log("[启动配置] " + g.Title + "：" + (args.Length > 0 ? "参数 " + args : "无参数")
                + " · 环境变量 " + n + " 条" + (steam && (args.Length > 0 || n > 0) ? "（Steam 协议启动，不会生效）" : ""));
            try { RefreshFgSummary(); } catch { }
        }

        // ---- 每游戏帧生成参数对话框（第 160 轮，竞品差距清单③）----
        //  竞品（RTX-FG-Manager 4.3.x）已做到"每文件夹配置"；本工具的 FG 参数此前是全局单值。
        //  入口 = 库/卡片右键「帧生成参数…」。三个覆盖项：倍率上限 / DLSS-G 预设 / Optimized；
        //  "跟随全局" = 不写每游戏覆盖。0.3.x 只吃倍率（它的 INI 没有后两个键）。
        void ShowFgConfigDialog()
        {
            if (selected == null) { Log("  请先选中游戏"); return; }
            DlssgGame g = selected;

            var dlg = new Form();
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
            dlg.Text = "帧生成参数 · " + g.Title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.Sizable;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.F(9f);
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.MinimumSize = new Size(Theme.S(560), Theme.S(400));
            dlg.ClientSize = new Size(Theme.S(600), Theme.S(388));
            dlg.ShowIcon = false;
            try { Theme.ApplyChrome(dlg.Handle); } catch { }

            var pg = new Pg();
            pg.Dock = DockStyle.Fill;

            var s1 = new Sec("本游戏覆盖");
            var cbMax = new RoundCombo();
            cbMax.Items.AddRange(new object[] { "跟随全局", "2X（1 帧）", "3X（2 帧）", "4X（3 帧）", "5X（4 帧）", "6X（5 帧）" });
            cbMax.SelectedIndex = g.FgMax > 0 ? Math.Min(5, g.FgMax) : 0;
            s1.Row("倍率上限", cbMax, Theme.S(180));

            var cbPreset = new RoundCombo();
            cbPreset.Items.AddRange(new object[] { "跟随全局", "A", "B", "C（FG Lite）" });
            cbPreset.SelectedIndex = 0;
            if (g.FgPreset == "Auto" || g.FgPreset == "A") cbPreset.SelectedIndex = 1;
            else if (g.FgPreset == "B") cbPreset.SelectedIndex = 2;
            else if (g.FgPreset == "C") cbPreset.SelectedIndex = 3;
            s1.Row("DLSS-G 预设", cbPreset, Theme.S(180));

            var cbOpt = new RoundCombo();
            cbOpt.Items.AddRange(new object[] { "跟随全局", "0", "1", "2", "3" });
            cbOpt.SelectedIndex = g.FgOpt >= 0 ? Math.Min(4, g.FgOpt + 1) : 0;
            s1.Row("Optimized 档", cbOpt, Theme.S(180));
            s1.Body("「跟随全局」= 不覆盖，用「帧生成」页参数区里的全局值。\n"
                     + "倍率对 0.3.x / 0.4.x 都生效；预设与 Optimized 只对 0.4.x 生效。\n"
                     + "保存时如果这个游戏已部署，会立即写进它的 INI 并在日志里给回执。");
            pg.Add(s1);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Bg;
            bar.Padding = new Padding(0, Theme.S(11), Theme.S(16), 0);
            var btnSave = new FlatBtn(); btnSave.Text = "保存"; btnSave.Kind = BtnKind.Primary; btnSave.Width = Theme.S(96);
            var btnCancel = new FlatBtn(); btnCancel.Text = "取消"; btnCancel.Width = Theme.S(84);
            btnSave.DialogResult = DialogResult.OK;
            btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(btnCancel);
            bar.Controls.Add(btnSave);
            dlg.Controls.Add(bar);

            pg.Reflow();
            dlg.Shown += delegate
            {
                try { pg.Reflow(); } catch { }
                try { dlg.ActiveControl = null; } catch { }
            };

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            //  索引 → 覆盖语义：0 = 跟随全局（0/""/-1）
            int max = cbMax.SelectedIndex > 0 ? cbMax.SelectedIndex : 0;
            string preset = cbPreset.SelectedIndex <= 0 ? "" : (cbPreset.SelectedIndex == 1 ? "A" : (cbPreset.SelectedIndex == 2 ? "B" : "C"));
            int opt = cbOpt.SelectedIndex > 0 ? cbOpt.SelectedIndex - 1 : -1;
            Lib.SetFgConfig(g, max, preset, opt);
            Log("[帧生成参数] " + g.Title + "：倍率 " + (max > 0 ? max + 1 + "X" : "跟随全局")
                + " · 预设 " + (preset.Length > 0 ? preset : "跟随全局")
                + " · Optimized " + (opt >= 0 ? opt.ToString() : "跟随全局"));
            string applied = ApplyPerGameFg(g);
            if (applied.Length > 0) Log("  " + applied);
            else Log("  （未部署补丁代理，参数已保存，部署后自动生效）");
            RefreshFgSummary();
        }

        void ShowCustomDialog()
        {
            var dlg = new Form();
            Fx.FadeOnClose(dlg);   // 关闭淡出 200ms（关动画/系统关特效时零行为）
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
                // 不是"优化项"，是执行策略：一键优化里只要勾了「实验」档，就先在系统盘打一个还原点。
                // 放这里是因为它就是"开不开"的二选一，和上面这些开关同一个语义层级。
                new object[] { "一键优化前自动建还原点（勾了实验档时才建）", "restore", "beforeOptimize", Cfg.RestorePointBeforeOpt },
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

            // ---- 配置迁移（2026-10-21）----
            // 单开一段而不是塞进底部按钮栏：按钮栏已经有三个，"导出/导入"和它们不是一类操作
            //（那两个改的是"这次勾什么"，这两个搬的是整份配置）。
            var secIO = new Sec("配置迁移");
            var btnExport = new FlatBtn(); btnExport.Text = "导出配置…"; btnExport.Width = Theme.S(100);
            var btnImport = new FlatBtn(); btnImport.Text = "导入配置…"; btnImport.Width = Theme.S(100);
            btnExport.Click += delegate
            {
                var sd = new SaveFileDialog();
                sd.Title = "导出 Fluxion 配置";
                sd.Filter = "Fluxion 配置 (*.json)|*.json";
                sd.FileName = "fluxion-config-" + DateTime.Now.ToString("yyyyMMdd") + ".json";
                if (sd.ShowDialog(this) != DialogResult.OK) return;
                bool awareOff;
                string why = Program.ExportConfig(sd.FileName, out awareOff);
                if (why.Length > 0)
                {
                    MessageBox.Show(this, why, "导出配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MessageBox.Show(this, "已导出到：\n" + sd.FileName
                    + (awareOff ? "\n\n游戏联动已置为关闭：对方导入后不会自动在后台改系统，要开由他自己在设置页打开。" : "")
                    + "\n\n这份文件可以带到另一台机器上导入，也能直接读（就是个 JSON）。",
                    "导出配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            btnImport.Click += delegate
            {
                var od = new OpenFileDialog();
                od.Title = "导入 Fluxion 配置";
                od.Filter = "Fluxion 配置 (*.json)|*.json|所有文件 (*.*)|*.*";
                if (od.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "将用这份文件整份替换当前配置（执行策略、档位、外观全部覆盖）。\n\n"
                    + "旧配置会自动备份成 config.json.before-import.bak，随时能换回去。\n\n确定导入吗？",
                    "导入配置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                var notes = new List<string>();
                string why = Program.ImportConfig(od.FileName, out notes);
                if (why.Length > 0)
                {
                    MessageBox.Show(this, why, "导入配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Cfg = Config.Load(Program.ConfigPath);
                Program.ReloadCfg(Cfg);
                RefreshGridAsync();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("配置已导入并立即生效。");
                foreach (var n in notes) sb.AppendLine("· " + n);
                Log(">>> 导入配置：" + od.FileName + (notes.Count > 0 ? "（" + notes.Count + " 条提示）" : ""));
                MessageBox.Show(this, sb.ToString(), "导入配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            secIO.Buttons(btnExport, btnImport);
            secIO.Body("只搬本工具自己的 config.json（执行策略 / 档位 / 外观 / 下载源）。\n"
                     + "系统设置不在这份文件里 —— 那部分由「恢复备份」负责，两者不会互相覆盖。");
            pg.Add(secIO);

            dlg.Controls.Add(pg);
            pg.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(52);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.WrapContents = false;
            bar.BackColor = Theme.Bg;
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

            // 同上：Show 之前的分区全不可见，Reflow 会被"缓存已最新"挡住 → 必须等 Shown 再来一次
            pg.Reflow();
            dlg.Shown += delegate
            {
                try { pg.Reflow(); } catch { }
                //  焦点不能留在第一个输入框上：RoundField 的占位提示是"聚焦即清空"，
                //  一打开就被清掉等于没有提示（浅色截图里那个空框就是这么来的）。
                try { dlg.ActiveControl = null; } catch { }
            };
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
