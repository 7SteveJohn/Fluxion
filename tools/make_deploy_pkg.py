#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 d3d12 代理部署包里的安装/卸载脚本与说明文档（CRLF，纯 ASCII 脚本）。"""
import os

PKG = r"D:\youhua\Fluxion\deploy\d3d12_proxy"
FILES = "d3d12.dll d3d12_orig.dll dinput8.dll dlssg_sm86.ini"

INI = r"""; Native 0.2.4 output for SM86 (RTX 30 series). Exit the game before editing.
[Compatibility]
; SM86 = Ampere / RTX 30 series. SM75 = Turing / RTX 20 series.
Router=SM86
; PTX = driver JIT (safe default). Cubin needs an exact GPU match.
KernelImage=PTX
; 0 = exact output (default). 1 = approximate sampling, SM86 only.
HardwareBilinear=0

[FrameGeneration]
; Capability cap: 1=2X, 2=3X, 3=4X. The game decides the actual multiplier.
MaxGeneratedFrames=3

[Logging]
; 0=off, 1=errors, 2=diagnostics, 3=verbose. Level 2 is for troubleshooting only.
Level=2
"""

INSTALL = r"""@echo off
rem ==========================================================
rem  Fluxion : d3d12-entry DLSSG proxy installer
rem  d3d12.dll is the only entry name the anti-cheat cannot
rem  blacklist, because a DX12 game must load it.
rem ==========================================================
setlocal
set "PKG=%~dp0"
set "GAME=%~1"
if "%GAME%"=="" echo Usage: %~nx0 "GAME_DIR" & exit /b 1
if not exist "%GAME%\" echo [X] Directory not found: %GAME% & exit /b 1

echo [1/3] Backing up existing files...
set "BK=%GAME%\_dlssg_backup"
if not exist "%BK%" mkdir "%BK%"
for %%F in (FILES) do if exist "%GAME%\%%F" move /Y "%GAME%\%%F" "%BK%\%%F.bak" >nul

echo [2/3] Installing proxy (4 files)...
copy /Y "%PKG%d3d12.dll"      "%GAME%\d3d12.dll"      >nul
copy /Y "%PKG%d3d12_orig.dll" "%GAME%\d3d12_orig.dll" >nul
copy /Y "%PKG%dinput8.dll"    "%GAME%\dinput8.dll"    >nul
copy /Y "%PKG%dlssg_sm86.ini" "%GAME%\dlssg_sm86.ini" >nul

echo [3/3] Done.
echo [OK] Installed into: %GAME%
echo.
echo Launch the game, then check Settings - Graphics - Advanced - Frame Generation.
echo Log: %GAME%\dlssg_sm86\logs\native_*.jsonl
exit /b 0
"""

UNINSTALL = r"""@echo off
rem ==========================================================
rem  Fluxion : d3d12-entry DLSSG proxy uninstaller
rem ==========================================================
setlocal
set "GAME=%~1"
if "%GAME%"=="" echo Usage: %~nx0 "GAME_DIR" & exit /b 1
if not exist "%GAME%\" echo [X] Directory not found: %GAME% & exit /b 1

echo [1/2] Removing proxy files...
for %%F in (FILES) do if exist "%GAME%\%%F" del /f /q "%GAME%\%%F"

set "BK=%GAME%\_dlssg_backup"
if exist "%BK%" (
  echo [2/2] Restoring backups...
  for %%F in (FILES) do if exist "%BK%\%%F.bak" move /Y "%BK%\%%F.bak" "%GAME%\%%F" >nul
  rmdir "%BK%" 2>nul
)
echo [OK] Uninstalled from: %GAME%
exit /b 0
"""

README = r"""Fluxion   d3d12 入口代理   部署包
============================================================

为什么需要它
------------------------------------------------------------
绝区零 / 鸣潮 都支持 DLSS 帧生成，但有两道门槛：

  门槛1  官方只给 RTX 40 系以上开放 DLSS 帧生成，3060 Ti 看不到选项
  门槛2  反作弊按“代理 DLL 文件名”识别，version / winmm / winhttp /
         dinput8 / dxgi 全部被拉黑

上游 issue 实证：
  #102  "Zenless zone zero doesn't work with any alternatives
         except d3d12.dll"
  #111  "绝区零的反作弊会识别到 version.dll 等的名词，从而使其失效"

d3d12.dll 是唯一无法被拉黑的名字 —— DX12 游戏必须加载它。
（绝区零 Player.log 实证：Forcing GfxDevice: Direct3D 12 / d3d12: loaded!）

但 dlssg_for_sm86 上游只提供 5 个入口，没有 d3d12 —— 这就是所有人
绝区零都失败的根本原因。

本包做了什么
------------------------------------------------------------
用纯 PE 构造（导出转发 + 导入依赖，不含任何机器码）造了个 d3d12.dll：

  1. 导出系统 d3d12.dll 全部 18 个函数，逐一转发给 d3d12_orig.dll
     → 对游戏完全透明
     （已实测：真调用 D3D12CreateDevice 返回 S_OK 并创建设备）
  2. 通过导入表静态依赖 dinput8.dll
     → Windows 加载 d3d12.dll 时自动把 dlssg 代理拉进游戏进程，
       装上 LoadLibrary 钩子去截获 nvngx_dlssg.dll 请求
     （已实测：诱饵 LoadLibrary 被截获，external_feature_dll_loaded=false）

为什么载体选 dinput8.dll 而不是 dxgi.dll
------------------------------------------------------------
UnityPlayer.dll 的导入表里没有 dinput8.dll —— 游戏自己不会调用它，
所以用它当载体不会劫持游戏的任何现有功能。

反过来，dxgi.dll 是 DX12 必经之路，拿它当载体等于让代理接管整个
显示层；上游 issue #111 也证实"绝区零用 dinput8 不闪退"。
注意 #111 那句"dinput8 不崩但帧数无变化"是因为当时它压根没被加载
（游戏不导入它，扔在目录里等于白放）—— 本包用导入表强制加载，
正好补上这个缺口。

文件清单
------------------------------------------------------------
@FILELIST@

安装（先完全退出游戏和启动器）
------------------------------------------------------------
绝区零：
  install.cmd "G:\miHoYo Launcher\games\ZenlessZoneZero Game"

鸣潮：
  install.cmd "G:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64"

把 install.cmd 拖进命令行，或右键编辑改好路径后双击。

卸载：uninstall.cmd "同一个目录"
（安装时同名旧文件会自动备份到 _dlssg_backup，卸载时自动还原）

怎么确认生效
------------------------------------------------------------
1. 游戏内 设置 - 画面 - 高级 - 帧生成，看有没有出现 DLSS 帧生成 / 倍率
2. 看安装目录下的 dlssg_sm86\logs\native_<pid>.jsonl：
       "event":"runtime_redirect"             代理接管成功
       "external_feature_dll_loaded":false
       "event":"evaluate"                     帧生成真的在跑
   若这个目录根本不出现 —— 代理没被加载，或被反作弊拦下了

本包预置 Logging.Level=2（诊断模式）便于验证。确认一切正常后，把 ini 里的
Level 改回 1，减少日志开销；INI 只有五项键：
Router / KernelImage / HardwareBilinear / MaxGeneratedFrames / Logging.Level。
改完必须完全退出游戏再启动，不支持热重载。

风险（务必看完）
------------------------------------------------------------
[红] 两个游戏都带内核反作弊（绝区零 HoYoKProtect、鸣潮腾讯 ACE）。
     注入未签名 DLL 可能被判违规，有封号风险。
     上游社区共识（Arknights Endfield issue #81）：
     "Any anti-cheat will block third-party DLLs, there is no way to fix this"
     本方案之所以还有机会，仅因为 d3d12.dll 这个入口名无法被拉黑，
     不代表反作弊不会用签名校验 / 行为检测发现它。

[黄] 已知个案：
     - 有玩家反馈 OptiScaler 的 d3d12 代理在绝区零国服"进去半分钟闪退"，
       国际服正常。本包不保证国服可用。
     - 鸣潮的 DLSSG 走 NVIDIA Streamline 加载，能否正确接管未经实测。

[绿] 验证顺序：先只上一个，进游戏看菜单 + 看日志。不要两个一起上。

[警告] 在意账号就别用。3060 Ti 上零风险替代方案：
     - 游戏自带 FSR 帧生成 + 游戏内 Reflex（若可开）
     - Lossless Scaling 等窗口级插帧（不碰游戏进程）

回滚
------------------------------------------------------------
uninstall.cmd，或手动删掉那 4 个文件。d3d12.dll 原本不在游戏目录
（从 System32 加载），删掉它不影响游戏完整性。
"""


DESC = {
    "d3d12.dll": "核心：转发代理（导出转发给 d3d12_orig.dll）",
    "d3d12_orig.dll": "系统 d3d12.dll 副本（转发目标）",
    "dinput8.dll": "dlssg_for_sm86 的 dinput8 入口代理（被上面静态导入）",
    "dlssg_sm86.ini": "配置（SM86 / PTX / 精确档 / 上限 4X）",
}


def filelist():
    lines = []
    for f in FILES.split():
        p = os.path.join(PKG, f)
        sz = os.path.getsize(p) if os.path.exists(p) else 0
        lines.append("  %-16s %12s 字节   %s" % (f, format(sz, ","), DESC[f]))
    lines.append("  %-16s %12s        %s" % ("install.cmd", "", "安装"))
    lines.append("  %-16s %12s        %s" % ("uninstall.cmd", "", "卸载 / 回滚"))
    lines.append("  %-16s %12s        %s" % ("make_deploy_pkg.py", "", "本包的生成脚本"))
    return "\n".join(lines)


def w(name, text):
    text = text.replace("FILES", FILES).replace("@FILELIST@", filelist())
    p = os.path.join(PKG, name)
    with open(p, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)
    return os.path.getsize(p)


for name in ("install.cmd", "uninstall.cmd"):
    text = INSTALL if name == "install.cmd" else UNINSTALL
    # 脚本内不能出现非 ASCII，否则 cmd 控制台会乱码
    text = text.replace("FILES", FILES)
    assert all(ord(c) < 128 for c in text), "%s 含非 ASCII" % name
    with open(os.path.join(PKG, name), "w", encoding="ascii", newline="\r\n") as f:
        f.write(text)
    print("   %-18s %8d" % (name, os.path.getsize(os.path.join(PKG, name))))

with open(os.path.join(PKG, "dlssg_sm86.ini"), "w", encoding="ascii", newline="\r\n") as f:
    f.write(INI)
print("   %-18s %8d" % ("dlssg_sm86.ini", os.path.getsize(os.path.join(PKG, "dlssg_sm86.ini"))))

print("   %-18s %8d" % ("README.txt", w("README.txt", README)))

# ---- 一致性守卫：install.cmd 里 copy 的源文件必须真的在包里，且与 FILES 完全一致 ----
import re as _re

_copied = _re.findall(r'copy /Y "%PKG%([^"]+)"',
                      open(os.path.join(PKG, "install.cmd"), encoding="ascii").read())
_missing = [f for f in _copied if not os.path.exists(os.path.join(PKG, f))]
if _missing:
    raise SystemExit("[FAIL] install.cmd 引用了包里不存在的文件: %s" % _missing)
if sorted(_copied) != sorted(FILES.split()):
    raise SystemExit("[FAIL] install.cmd 拷贝清单与 FILES 不一致\n  拷贝 : %s\n  FILES: %s"
                     % (sorted(_copied), sorted(FILES.split())))
print("\n[OK] install.cmd 拷贝清单与 FILES 一致，且包内 4 个文件齐全")


print()
print("=== 最终部署包 ===")
tot = 0
for f in sorted(os.listdir(PKG)):
    s = os.path.getsize(os.path.join(PKG, f)); tot += s
    print("   %-20s %12d" % (f, s))
print("   合计 %.1f MB" % (tot / 1048576.0))
