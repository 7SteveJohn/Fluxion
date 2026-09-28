# -*- coding: utf-8 -*-
"""探针 runner 的统一收尾（18 个 run_*.py 共用这一份）。

为什么要有这个文件（2026-09-21 二次收口）：
  第一版收尾是在**模块全局里猜结果变量名**（找 r2/r/res/proc，再扫 out/txt/body 里有没有
  "FAIL" 字样）—— 能跑，但前提是"每个 runner 恰好用了那几个名字之一"。谁改个变量名，
  探针红了 runner 照样 `exit 0`，而"红了却退 0"正是这一轮要修的 bug。所以收尾必须
  **显式**：把探针那次 subprocess 的结果对象直接传进来。

用法（放在 runner 文件末尾，脚本式 runner 都是顶层代码，所以 import 也在末尾）：

    from _probe_runner import finish
    finish(r, verdict=True)      # 有判据的探针：红了必须非 0
    finish(r, verdict=False)     # 诊断型探针（knobs / stall：只输出结论，无 PASS/FAIL）

verdict=True 的前提：那个探针**自己按 FAIL 数返回非 0**（tools/*_probe.cs 里的
`return Fail` 或 `return Fail == 0 ? 0 : 1`）。这个前提由 `syncheck.py` 的
「runner 收尾自检」盯着 —— 它同时检查 runner 传了 verdict、探针源码里有 FAIL 返回。
"""
import sys


def finish(proc, verdict=True):
    """按探针进程的返回码结束 runner。

    proc    : 探针那次 subprocess.run 的结果对象（必须有 returncode）
    verdict : 这个探针有没有判据。True = 返回码即结论；False = 跑起来没崩就算过。
    """
    if proc is None or not hasattr(proc, 'returncode'):
        print('!! 收尾失败：没拿到探针进程的结果对象（finish(proc) 的 proc 传错了）')
        sys.exit(2)
    rc = proc.returncode
    if rc == 0:
        print('=== runner 收尾：探针返回 0' + ('（已计为通过）' if verdict else '（诊断型，无判据）'))
    else:
        print('=== runner 收尾：探针返回 %d → runner 按同码退出' % rc)
    sys.exit(rc)
