"""把 MiSans 原字体裁成"界面够用"的子集，写进 src\\fonts\\。

为什么要有这个脚本（2026-10-20）
    程序自带 MiSans（不依赖用户装没装），但整族三个字重是 21MB，太大。
    子集化之后每个字重约 1.6MB，三个合计 4.9MB，视觉完全一样。

为什么用 GB2312 全集而不是"只取源码里出现的字"
    游戏名 / 目录名 / 日志都是**运行期**文本，源码里不可能穷举。
    只按源码取字（1357 个）体积能压到 280KB，但碰到没取到的字就是方框或回落到别的字体，
    反而更难看。GB2312（7540 字）覆盖日常中文，是"体积 / 覆盖"的平衡点。

用法
    1) 下载官方包：https://hyperos.mi.com/font/zh/download/  → MiSans.zip
    2) 解压，把 "MiSans 开发下载字重" 放到一个目录，例如 D:\\tmp\\misans
    3) 装 fonttools：<python> -m pip install fonttools
    4) 跑：
       <python> tools\\make_font_subset.py D:\\tmp\\misans\\"MiSans 开发下载字重"
    5) 脚本会把三个子集直接写进 src\\fonts\\，然后正常 build 即可。

授权提醒
    MiSans 由小米发布，官方许可允许免费商用；但**分发前请按官方许可原文确认**嵌入与再分发条款。
    本脚本只做体积裁剪，不改字名、不改字形。
"""

import os
import subprocess
import sys

WEIGHTS = ("Regular", "Medium", "Bold")
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "src", "fonts")


def gb2312_charset():
    """GB2312 的一、二级汉字 + 全角标点 + ASCII。"""
    chars = set()
    for hi in range(0xB0, 0xF8):
        for lo in range(0xA1, 0xFF):
            try:
                chars.add(bytes([hi, lo]).decode("gb2312"))
            except UnicodeDecodeError:
                pass
    for hi in range(0xA1, 0xAA):
        for lo in range(0xA1, 0xFF):
            try:
                chars.add(bytes([hi, lo]).decode("gb2312"))
            except UnicodeDecodeError:
                pass
    chars.update(chr(c) for c in range(0x20, 0x7F))
    return chars


def ui_string_chars():
    """扫 src/*.cs 的**字符串字面量**，把界面真正会画出来的非 ASCII 字符全收进来。

    为什么必须做（2026-10-21，"字体还是很怪"的根因）：
        Python 的 gb2312 编码表把 A1A4 解成 U+30FB'・'、A1AA 解成 U+2015'―'，
        而不是 U+00B7'·' / U+2014'—' —— 于是 GB2312 全集里**没有**间隔号和破折号。
        后果：'·' 在 UI 字符串里出现 383 次、'—' 341 次，全部逐字回落到别的字体，
        一行字里两种字体混排（基线/宽度/字重都不对），这就是用户看到的"怪"。
    """
    import glob
    import re
    chars = set()
    lit = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
    for path in glob.glob(os.path.join(ROOT, "src", "*.cs")):
        with open(path, encoding="utf-8", errors="ignore") as f:
            s = f.read()
        for m in lit.finditer(s):
            for ch in m.group(1):
                if ord(ch) > 0x7F:
                    chars.add(ch)
    return chars


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    src_dir = sys.argv[1]
    os.makedirs(OUT_DIR, exist_ok=True)
    text_file = os.path.join(ROOT, ".build", "misans_chars.txt")
    os.makedirs(os.path.dirname(text_file), exist_ok=True)
    gb = gb2312_charset()
    ui = ui_string_chars()
    extra = sorted(ui - gb)
    text = "".join(sorted(gb | ui))
    with open(text_file, "w", encoding="utf-8") as f:
        f.write(text)
    print("字符集 %d 个（GB2312 %d + 界面字符串补充 %d）-> %s"
          % (len(text), len(gb), len(extra), text_file))
    if extra:
        print("  补充字符: " + " ".join("U+%04X" % ord(c) for c in extra))

    for w in WEIGHTS:
        src = os.path.join(src_dir, "MiSans-%s.ttf" % w)
        if not os.path.exists(src):
            sys.exit("缺少字重文件: " + src)
        dst = os.path.join(OUT_DIR, "MiSans-%s.ttf" % w)
        cmd = [sys.executable, "-m", "fontTools.subset", src,
               "--text-file=" + text_file, "--output-file=" + dst,
               "--layout-features=*", "--drop-tables+=DSIG"]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            sys.exit("subset 失败(%s): %s" % (w, (r.stderr or r.stdout)[-400:]))
        print("  %-18s %6d KB" % (os.path.basename(dst), os.path.getsize(dst) // 1024))
    print("完成 -> " + OUT_DIR)


if __name__ == "__main__":
    main()
