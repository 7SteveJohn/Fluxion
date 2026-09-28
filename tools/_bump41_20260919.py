# -*- coding: utf-8 -*-
r"""版本号 3.6.0 -> 3.6.1（字节级改，保 BOM / CRLF）。

为什么用字节级而不是 Edit：`installer\GameBoost-DLSSG.iss` 是 BOM + 纯 CRLF，
文本模式读写会把行尾换掉（模板里的 install 段落对行尾敏感），Core.cs 则丢过好几次 BOM。
两处版本号必须一起改 —— build.py 只校验"两处一致"，改一半会被它拦下（这是好事）。
"""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

PAIRS = [
    (os.path.join(ROOT, 'src', 'Core.cs'),
     b'public const string AppVersion = "3.6.0";',
     b'public const string AppVersion = "3.6.1";'),
    (os.path.join(ROOT, 'installer', 'GameBoost-DLSSG.iss'),
     b'#define AppVersion   "3.6.0"',
     b'#define AppVersion   "3.6.1"'),
]

for path, old, new in PAIRS:
    b = open(path, 'rb').read()
    n = b.count(old)
    assert n == 1, '%s: 命中 %d 次（应为 1）—— 锚点写错或已经改过' % (path, n)
    nb = b.replace(old, new)
    open(path, 'wb').write(nb)
    chk = open(path, 'rb').read()          # 回读断言，不信写完就完事
    assert chk[:3] == b[:3], '%s: BOM 被改坏' % path
    assert chk.count(b'\r\n') == b.count(b'\r\n'), '%s: CRLF 数变了' % path
    assert chk.count(b'\n') == b.count(b'\n'), '%s: 总行数变了' % path
    assert new in chk and old not in chk, '%s: 替换没落盘' % path
    print('OK  %-30s %d -> %d bytes' % (os.path.basename(path), len(b), len(chk)))

print('版本号 3.6.1 就位（Core.cs + .iss 两处）')
