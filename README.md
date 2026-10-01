# Fluxion

![Version](https://img.shields.io/badge/version-1.1.0-blue) ![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-lightgrey) ![Tech](https://img.shields.io/badge/C%23_WinForms-.NET-orange)

**Windows 游戏优化 + DLSS 帧生成管理**（本地自用工具，当前 v1.1.0）

一句话：**一键把系统调到适合游戏的状态，并给 RTX 20/30 系游戏接上 NVIDIA 只给 40 系开的帧生成。**

## 这是什么

一款独立开发的桌面级游戏优化工具，源码完全公开。它把散落在系统各处的游戏相关设置收敛成一键可切换的优化档，并配套完整的备份/还原体系；同时用 DLL 代理方式为 RTX 20/30 系补上 NVIDIA 官方只给 40 系开的 DLSS Frame Generation。

- **适合谁**：想在 8GB 显存 / 老卡上榨出更多帧数的玩家；想了解每一项 Windows 游戏优化到底改了什么的开发者
- **怎么做的**：全部走系统公开接口（powercfg / 注册表 / NVAPI / 网卡配置），不注入游戏、不改游戏本体文件
- **安全边界**：每项优化都记录默认值、支持一键还原；VBS / 内存完整性等敏感项只检测不代改

## 功能

**性能优化**（一键应用 / 一键还原，18 项带 ⚙ 档位可调，默认值 = 改造前硬编码，不改零变化）

| 类别 | 内容 |
|---|---|
| 电源 | 卓越性能计划；游戏期临时切档（进游戏切、退出还原） |
| 调度 | Win32PrioritySeparation；混合架构 P 核优先（异类线程调度） |
| GPU | GameDVR / 游戏模式 / HAGS；NVAPI 驱动配置双档自动化（竞技档 / 3A 档，等价面板手动设置） |
| 网络 | 关 Nagle 与流量节流、SystemResponsiveness=10、网卡节能全关 |
| 内存/后台 | 内核不换页；游戏加速包（挂起后台进程 + 内存整理 + 暂停更新下载，退出完整恢复） |
| 输入/桌面 | 关鼠标加速；游戏期 0.5ms 定时器（仅 FPS 档）；DWM 动效精简 |
| 监控 | nvlddmkm 崩溃取证、C 盘守护、网络哨兵、驱动配置巡检 |

**帧生成（DLSSG）**：接入开源项目 `dlssg_for_sm86`——DLL 代理接管游戏对 `nvngx_dlssg.dll` 的加载，换成补齐 SM86/SM75 内核的运行时（AI 模型仍为 NVIDIA 原厂）。3060 Ti 也能开官方 DLSS Frame Generation，最高 6X。

**游戏库**：Steam / Epic / WeGame / 战网 / 育碧 / EA 扫描，封面卡片，双击启动，启动目标四级解析，收藏 / 私密 / 隐藏。

## 帧生成使用

1. 设置页下载运行时（国内需 7890 端口代理）→ 帧生成页「扫描游戏」
2. 选中游戏 →「开启选中」；不想要了「关闭选中」完全还原
3. 游戏内：帧生成选 **DLSS Frame Generation**、关垂直同步、帧率上限设无限

**须知**：

- 必须开启 HAGS（工具会检测并提示）；基础帧低于 40 不建议开
- **竞技射击别开**（CS2 / Valorant / 三角洲——三角洲自带官方 FG）；8GB 显存 1080p 约需 320MB 余量
- 带内核反作弊的游戏（绝区零 / 鸣潮等）自动改用 `d3d12.dll` 入口（这类反作弊按文件名拦常规入口，拦不住 d3d12）
- 是否真正激活以「运行诊断」读日志为准（`runtime_redirect` / `evaluate` 事件）；代理加载了没日志 → 「换入口重试」。实测经验已内置（如 CP2077 必须走 winmm 入口，实测 135 → 190 帧）
- 游戏更新覆盖代理文件后需重新开启；代理只放 1~2 个文件（d3d12 入口 3 个 + ini），删除即还原

## 编译

| 动作 | 命令 |
|---|---|
| 编译 | `powershell -NoProfile -ExecutionPolicy Bypass -File compile.ps1`（产出根目录 `Fluxion.exe`；报告写 `compile_result.txt`，`COMPILE_OK` 才算通过） |
| 静态预检 | `python tools/static_check.py src`（项目根执行，30 秒抓大多数低级错误，不能替代编译器） |

- 升版本改 `src/Core.cs` 的 `AppVersion`（`DisplayVersion` 跟随它）
- `compile.ps1` 保持 ASCII-only 注释：PS5.1 读无 BOM 文件按 ANSI，中文注释会导致假编译成功
- 程序带 `requireAdministrator` 清单，启动弹一次 UAC；配置文件已存在时升级不覆盖
- 便携 / 安装版同一 exe 自动判定：exe 同目录有 `config.json` = 便携（数据在旁），否则数据在 `%ProgramData%\Fluxion\`（config / logs / backup / dlssg）
- 打包脚本（Inno Setup / 分发版）属自用定制，不随仓库发布；仓库只维护源码与校验工具

## 目录结构

```
src/Core.cs     逻辑层：配置、优化执行、看门狗、游戏联动、NVAPI、备份还原
src/Dlssg.cs    帧生成模块：扫描、代理装卸、方案切换、诊断、判据
src/Lib.cs      游戏库：平台扫描、启动解析、封面管线
src/Pack.cs     资源包导入（拖入 dlssg030-pack）、备份回收
src/Ui.cs       全部 WinForms UI（自绘主题引擎）
tools/          静态校验脚本与探针（static_check / member_check / orphan_check 等）
icon/           程序图标与候选稿
docs/           完整开发日志
```

## 风险声明

- 不修改游戏本体、不注入竞技游戏、不以绕过反作弊为目的——代理替换的是游戏自身的 DLSS 加载链，能否生效仍取决于反作弊是否放行
- 帧生成仅建议用于**单机 3A**；带内核级反作弊的网游上使用第三方 DLL 代理有封号风险，请自行判断
- Defender 排除是安全权衡项，默认关闭；VBS / 内存完整性只检测不代改

## 更多

- 完整开发日志（40+ 版迭代实录与实测经验）：[docs/README-完整开发日志.md](docs/README-完整开发日志.md)
- 研究笔记见 `docs/`；版本历史直接看 git log
