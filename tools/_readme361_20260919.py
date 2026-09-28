# -*- coding: utf-8 -*-
r"""往 README.md 补 v3.6.1 的内容：

  ① 修正 4.1 里的两个数字/说法（18 行可调 → 19 行；行数不再写死 49）；
  ② 在 4.1 之后插一节 4.2「状态分档 + 表格观感」。

README 是 UTF-8 无 BOM + 纯 CRLF（安装包里带的就是这份），必须字节级/保行尾处理。
"""
import sys

P = r"D:\youhua\GameBoost-DLSSG\README.md"

OLD_COUNT = "体检表共 49 行：**18 行带 ⚙（可调）**，**31 行只读核验** —— 表下方直接写明这个区分。"
NEW_COUNT = ("体检表**总行数随环境变化**（驱动层要能读到 nvdrsdb、异类策略只在混合架构 CPU 上出现；\n"
             "本机是 51 行）。其中 **19 行带 ⚙（可调）**，其余是只读核验 —— 表下方直接写明这个区分。")

OLD_LIST = "处理器最大状态 / 前台响应优先级 / Nagle / 网络流量节流。"
NEW_LIST = ("处理器最大状态 / 前台响应优先级 / Nagle / 网络流量节流。\n"
            "另有「HAGS × 竞技游戏(A/B 建议)」一行也带 ⚙ —— 它与「硬件加速GPU计划(HAGS)」共用同一个旋钮。")

ANCHOR = "整体回退用「恢复备份」。\r\n\r\n---\r\n"

LINES = [
    "### 4.2 v3.6.1：状态分档 + 表格观感（用户指出的两处）",
    "",
    "**① 「HAGS × 竞技游戏(A/B 建议)」这一行为什么没反应、为什么顶着红徽章？**",
    "",
    "它是**建议**，不是缺陷：只在「RTX 30/20 系 + HAGS 已开 + 有竞技档游戏」时出现，意思是"
    "「现状已是推荐值，但值得你自己 A/B 验一次」。v3.6.0 之前它有两个毛病：状态写的是 `🟡`，"
    "而界面只认 `✅`/`⚠️` 两种开头 —— **开头不是这两个的一律渲染成红色「需处理」**，"
    "于是这一行里写着「HAGS 已开」、徽章却在报错；而且它没挂可调键，双击没有任何反应。",
    "",
    "现在状态分成四档，并且这一行也挂上了 `hags.enable`（看到「可以 A/B」就能当场切，切完要重启系统）：",
    "",
    "| 徽章 | 含义 |",
    "|---|---|",
    "| 已生效（绿） | 当前值与优化目标一致 |",
    "| 未生效（橙） | 该生效却没生效（系统更新 / 驱动重置） |",
    "| **待确认（灰）** | **不是缺陷**：可选 / 不适用 / 读不到（DPR 计数器不可用、HAGS 的 A/B 建议……） |",
    "| 需处理（红） | 读取失败或需要管理员权限 |",
    "",
    "**② 滚动时「有的色块更白、更割裂」**",
    "",
    "三个原因叠在一起，都已修掉：",
    "",
    "| 原因 | 修法 |",
    "|---|---|",
    "| 行底色是**逐格**铺的（一格一个 `DrawSubItem`），格与格之间的 1px 接缝、最右列右侧的空档没人画 | 改到 `DrawItem` **整行铺一次** |",
    "| 斑马纹是 `#FFFFFF` / `#F8FAFC`（只差 3%）：看不出「这是交替条纹」，只看得出「这些行颜色不一样」 | 去掉斑马纹，**所有行同色 + 底部一条极浅分隔线** |",
    "| ListView 是原生控件，`Control.DoubleBuffered` 对它**无效** —— 滚动时先擦成白底再逐行重绘，中间那一帧就是一片更白 | 打原生扩展样式 `LVS_EX_DOUBLEBUFFER`（探针读回确认：扩展样式 `0x00010020`） |",
    "",
    "顺带修掉一个自 3.x 就存在的隐性问题：自绘里判断「是否选中」用的是 `e.State` / `e.ItemState`，"
    "而实测在部分绘制路径下**它会把 Selected 位给到每一行**（`tools/ui_snapshot.py light lst 1` 可复现："
    "控件里没有任何一行被选中，绘制回调里的 Selected 位却行行都有）—— 按它画，整个列表会被涂成"
    "「选中态」底色并给每行加一条绿色强调条，看起来就是「这些行为什么跟别的不一样」。"
    "现在改成读行自己的 `e.Item.Selected`。",
    "",
]

b = open(P, "rb").read()
assert b[:3] != b"\xef\xbb\xbf", "README 本来就没有 BOM，别给它加上"
raw = b.decode("utf-8")
assert raw.count("\r\n") > 0 and raw.count("\n") - raw.count("\r\n") == 0, "README 行尾不是纯 CRLF，先确认再改"

for name, old in (("行数那句", OLD_COUNT), ("可调项清单收尾", OLD_LIST), ("4.2 锚点", ANCHOR)):
    n = raw.count(old)
    assert n == 1, "%s 命中 %d 次（应为 1）—— 锚点写错或已经改过" % (name, n)

section = "\r\n".join(LINES) + "\r\n"
# ⚠ 多行字符串里写的是 \n，必须换成 \r\n 再塞进这份纯 CRLF 的文档里 ——
#   否则会留下孤立 LF 行（后面那条 loneLF==0 的断言就是防这个的）。
raw = raw.replace(OLD_COUNT, NEW_COUNT.replace("\n", "\r\n"))
raw = raw.replace(OLD_LIST, NEW_LIST.replace("\n", "\r\n"))
raw = raw.replace(ANCHOR, "整体回退用「恢复备份」。\r\n\r\n" + section + "---\r\n")

# 回读断言：三处都落盘、接缝都对、行尾没被换掉
assert NEW_COUNT.replace("\n", "\r\n") in raw and OLD_COUNT not in raw, "行数那句没改成"
assert NEW_LIST.replace("\n", "\r\n") in raw and "处理器最大状态 / 前台响应优先级 / Nagle / 网络流量节流。\r\n\r\n" not in raw, \
    "可调项清单没改成"
assert raw.count("### 4.2 v3.6.1") == 1, "4.2 节标题没进去"
assert raw.count("现在改成读行自己的 `e.Item.Selected`。\r\n\r\n---\r\n") == 1, "4.2 节尾没接回 --- 分隔线"
assert raw.count("## 5. 帧生成（DLSSG）模块") == 1, "第 5 节被弄丢了"
assert raw.count("\n") - raw.count("\r\n") == 0, "行尾被破坏了"

open(P, "wb").write(raw.encode("utf-8"))
chk = open(P, "rb").read()
print("BOM =", chk[:3] == b"\xef\xbb\xbf", "| CRLF =", chk.count(b"\r\n"),
      "| loneLF =", chk.count(b"\n") - chk.count(b"\r\n"), "|", len(chk), "bytes",
      "(+%d)" % (len(chk) - len(b)))
sys.exit(0)
