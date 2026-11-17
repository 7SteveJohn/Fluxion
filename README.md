# Fluxion

<div align="center">

[![Release](https://img.shields.io/github/v/release/7SteveJohn/Fluxion?style=flat-square&color=0078D6&label=release&logo=github)](https://github.com/7SteveJohn/Fluxion/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6?style=flat-square&logo=windows)](https://github.com/7SteveJohn/Fluxion/releases)
[![Framework](https://img.shields.io/badge/.NET%20Framework-4.8%20%2F%20WinForms-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![License](https://img.shields.io/github/license/7SteveJohn/Fluxion?style=flat-square&color=2ea44f)](LICENSE)
[![Downloads](https://img.shields.io/github/downloads/7SteveJohn/Fluxion/total?style=flat-square&color=success&logo=github)](https://github.com/7SteveJohn/Fluxion/releases)
[![Stars](https://img.shields.io/github/stars/7SteveJohn/Fluxion?style=flat-square&color=yellow&logo=github)](https://github.com/7SteveJohn/Fluxion/stargazers)

**Windows 系统性能调优与 DLSS / XeSS / FSR 帧生成管理工具**

[快速下载](#快速下载) • [项目简介](#项目简介) • [核心功能](#核心功能) • [帧生成方案说明](#帧生成方案说明) • [构建指南](#构建指南) • [项目结构](#项目结构) • [致谢与参考项目](#致谢与参考项目) • [免责声明](#免责声明)

</div>

---

## 快速下载

最新正式版本为 **v1.3.2**，提供安装版与免安装便携版：

| 分发形态 | 文件名称 | 适用场景 | 下载地址 |
|---|---|---|---|
| **安装版 (推荐)** | `Fluxion-Setup-1.3.2.exe` | 标准 Windows 向导式安装，内置中英双语，支持桌面快捷方式与提权自启任务 | [官方发布页下载](https://github.com/7SteveJohn/Fluxion/releases/download/v1.3.2/Fluxion-Setup-1.3.2.exe) |
| **便携版 (免安装)** | `Fluxion-v1.3.2-Portable.zip` | 解压即用，配置、封面素材与日志全本地化独立存储，适合放置在移动硬盘或游戏目录中 | [官方发布页下载](https://github.com/7SteveJohn/Fluxion/releases/download/v1.3.2/Fluxion-v1.3.2-Portable.zip) |

---

## 项目简介

Fluxion 是一套运行在 Windows 环境下的轻量级开源桌面工具，主要包含两部分能力：

1. **系统底层调优**：将分散在 Windows 各处的系统性能配置（电源方案拓扑、异构 CPU 调度策略、NVIDIA 驱动级参数、DWM 动效开销、网络传输队列与高精度系统时钟）整合到统一面板中。所有优化项均基于 Windows 公开接口（`powercfg`、官方 NVAPI、标准注册表与网络适配器参数）执行，具备原始状态记录与一键还原机制。
2. **帧生成管理**：集成开源社区成熟的 DLL 转译与补丁生态，支持在单机游戏中部署与管理 DLSS 0.4.1、DLSS 0.3.5、OptiScaler 及 XeSS-FG 等帧生成方案。程序可根据显卡架构与硬件环境自动推荐最佳方案，支持多层级启动器目录穿透解析与原子级文件停放/恢复，无需手动复制或替换 DLL。

---

## 核心功能

### 1. 界面与交互设计
- **双主题三层表面体系**：提供「宣纸石白 / 曜石石墨」浅深双主题，构建侧边栏基底、工作台画布与悬浮卡片三层表面层级；采用 `1160×780` 黄金首屏视口、全窗贯通顶底水平基准线与四向等距像素级悬停胶囊对齐。
- **原生圆角弹层与深色滚动条**：下拉框与右键菜单接入 Windows 11 DWM 原生硬件圆角与边框渲染，全面适配深色模式原生滚动条，消除边缘锯齿、底层透字与白边闪烁。
- **内嵌字体与精确排版度量**：完整内置 MiSans 三字重子集（Regular、Medium、Bold），统一采用 GDI+ 排版度量引擎计算行高与菜单宽度，避免跨系统版本缺字、折行裁切或高分屏重叠。
- **全自绘模态弹窗与无闪换肤**：内置目录迁移、细调档位、DLSS 运行库管理等全套主题化模态弹窗；切换深浅主题时直接复用内存体检快照，实现无闪烁即时换肤，并支持 `WM_DPICHANGED` 跨显示器动态 DPI 缩放。

### 2. 游戏库管理与封面检索
- 自动检测并索引常见游戏平台（Steam、Epic Games、WeGame、战网、Ubisoft Connect、EA App）的本地安装目录。
- 支持多级封面获取策略：优先调用 Steam 官方高清素材，缺失时通过名称索引与网络接口补全封面。
- 采用自适应宽幅裁切渲染，在紧凑视图与高分辨率全屏状态下均保持固定比例，并提供完整的卡片与侧栏右键快捷操作菜单。

### 3. 数据目录管理与安全迁移
- 采用全自绘异步文件目录选择器，支持绝对路径直接粘贴与快速浏览。
- 提供完整的数据目录跨盘符迁移能力，迁移过程包含复制前校验、哈希比对与配置同步，保障历史配置、资源包与自订封面不丢失。

### 4. 系统底层参数调优
内置 50 余项系统底层状态巡检与一键诊断，支持按「全部 / 未生效 / 可调项 / 已生效 / 待确认」动态胶囊（`FilterChips`）实时筛选，支持双击或 `Enter` 键快捷调档，并可通过右键菜单一键复制附带硬件摘要的未生效项诊断信息。主要调优模块包括：
- **电源管理与硬件自适应**：支持切换至卓越性能方案及「游戏运行期动态切档」（进入游戏自动启用极限电源档，退出游戏自动复原），针对笔记本平台自动校准自适应最小处理器状态阈值。
- **进程调度与内核优化**：配置 `Win32PrioritySeparation`，支持 Intel 12 代及以上异构混合架构（P+E 核）短线程调度策略精细选档与状态核验。
- **图形与驱动栈（NVAPI）**：禁用 GameDVR 录制开销，结合显卡硬件能力校验 HAGS（硬件加速 GPU 计划）状态；通过官方 NVAPI 直写显卡驱动级偏好（最高性能优先、超低延迟模式、后台帧率上限 `0x10835005` 及 DLSS 超分预设）。
- **输入延迟与网络传输**：禁用系统级鼠标加速度，竞技场景下启用 0.5ms 系统高精度计时器；支持网络流量节流档位调节、禁用 Nagle 算法并优化网卡中断响应。
- **进退局串行联动与内存豁免保护**：进退游戏期间按单线程严格串行执行分辨率切换、电源切档、远控软件暂停与第三方覆盖层挂起；执行后台内存工作集修剪时，自动豁免全量预设及入库游戏（FPS / 3A / 二游 / Steam），防止游戏本体物理内存被换出引发缺页卡顿。

### 5. 帧生成多架构适配与目录穿透
- **单机 3A 与多架构自适应**：依据显卡微架构（RTX 20/30/40/50 系列、GTX 10/16 系列、AMD Radeon、Intel Arc）、驱动版本（R580+ 门槛提示）与屏幕刷新率，自适应匹配最佳帧生成方案。
- **DLSS 运行库版本管理与热替换**：内置 `nvngx_dlss.dll`（超分）与 `nvngx_dlssg.dll`（帧生成）版本巡检，支持签名白名单核验、`.fluxion-orig.bak` 原厂备份留存与一键无损升级/还原。
- **多层级启动器穿透识别**：针对具备二级启动器的游戏（如通过 `REDprelauncher.exe` 启动的《赛博朋克 2077》），自动穿透并定位到底层真实渲染目录（`bin\x64\Cyberpunk2077.exe`）及虚幻引擎插件目录，确保代理部署、配置调优与日志诊断均作用于有效工作目录。
- **原生组件备份与无冲突停放**：通过统一所有权模型跟踪代理文件；部署跨架构方案覆盖游戏自带同名组件（如 `libxess.dll`）前自动留存 `.fluxion-orig.bak` 原生组件备份，切换或停放回原生模式时无损复原，避免多套方案残留或原生文件丢失引发闪退。

---

## 帧生成方案说明

| 方案名称 | 核心代理入口 | 适用场景与典型游戏 | 技术特性 |
|---|---|---|---|
| **DLSS 0.4.1 进阶内核** | `version.dll` / `winmm.dll` / `d3d12.dll` | 《赛博朋克 2077》、《黑神话：悟空》等现代 DX12 3A 游戏 | 适配 RTX 20/30/40 系，支持 M-INT8 超分加速、A/B/C 预设与最高 6X 多帧生成（需 R580+ 驱动） |
| **DLSS 0.3.5 通用模式** | `version.dll` / `dxgi.dll` / `Version.asi` | 《霍格沃茨之遗》、《巫师 3 次世代版》、《绝区零》等 | 成熟稳定的 SM86/SM75 帧生成代理内核，支持 ASI 变体与专用入口自适应 |
| **MFG Bridge (方案 B)** | `version.dll` / `winmm.dll` 等 9 槽位 | 支持 DLSS-G 的单机游戏轻量转译场景 | 单文件 `nvngx` 桥接方案，支持游戏内 `Home` 键呼出覆盖菜单调节倍率 |
| **OptiScaler 方案 (方案 A)** | `dxgi.dll` / `d3d12.dll` | 《鸣潮》等二次元开放世界游戏，或 GTX / AMD / Intel 显卡 | 集成跨架构渲染中间件，支持 FSR 3.1 / XeSS-FG 转译与游戏内 `Insert`/`F10` 调试 |

### 使用前置条件
1. 系统为 64 位 Windows 10 (2004 及以上) 或 Windows 11。
2. Windows 设置中已开启「硬件加速 GPU 计划 (HAGS)」并重启生效。
3. 建议游戏原生渲染帧率达到 40 FPS 以上以保证插帧质量。
4. 游戏内开启 DLSS 帧生成选项后，建议关闭垂直同步（V-Sync）并将帧率限制设置为无上限。

---

## 构建指南

Fluxion 基于 .NET Framework 4.0/4.8 开发，支持直接使用 Windows 系统内置的 `csc.exe` 编译器构建，无需安装庞大的 IDE 环境。

### 环境要求
- 操作系统：Windows 10 / 11 64-bit
- 编译工具：.NET Framework 4.0 或更高版本自带的 `csc.exe`
- 脚本环境：PowerShell 7 (`pwsh`)
- 解释器（可选）：Python 3.8+（用于执行静态分析与单元测试套件）

### 构建步骤
```powershell
# 1. 执行源码静态检查（校验类型完整性与排版契约）
python tools/static_check.py src

# 2. 编译生成主程序
pwsh -NoProfile -ExecutionPolicy Bypass -File compile.ps1
```

编译成功后，将在项目根目录下生成 `Fluxion.exe`。

---

## 项目结构

```
Fluxion/
├── src/
│   ├── Core.cs          # 核心调度逻辑：系统底层调优、NVAPI 接口、后台监控、配置模型
│   ├── Dlssg.cs         # 帧生成中枢：扫描检索、代理文件状态机、推荐与诊断
│   ├── Dlssg040.cs      # DLSS 0.4.1 / MFG 运行时管理与配置文件解析
│   ├── MfgBridge.cs     # 渲染管线与方案桥接接口
│   ├── Lib.cs           # 游戏库中枢：平台检索、启动路径穿透、封面获取管线
│   ├── Pack.cs          # 补丁包与便携资源解压提取引擎
│   ├── Ui.cs            # WinForms 界面渲染：双主题表面引擎、自绘控件族、模态弹窗与交互层
│   ├── app.manifest     # UAC 管理员权限与 DPI-PerMonitorV2 清晰度声明
│   └── fonts/           # 内嵌 MiSans 字体子集资源（TTF）
├── tools/               # 静态检查、布局校验与各模块自动化回归探测套件
├── icon/                # 多分辨率应用图标与视觉候选资源
├── docs/                # 完整开发演进与架构变更日志（README-完整开发日志.md）
├── catalog.json         # 补丁元数据、签名特征与游戏专用入口规则表
├── compile.ps1          # 基于系统内置 csc.exe 的主程序构建脚本
└── LICENSE              # MIT 开源许可证
```

---

## 致谢与参考项目

Fluxion 的研发与实现借鉴了开源社区多位开发者的研究成果。在此向以下开源项目及作者表示诚挚感谢（按英文首字母排序）：

- **[cdozdil](https://github.com/cdozdil) 与 [OptiScaler 社区](https://github.com/OptiScaler/OptiScaler)**：打造了优秀的跨架构超分辨率与帧生成转译框架。
- **[emoose](https://github.com/emoose)**：开源了 [fakenvapi](https://github.com/emoose/fakenvapi) 等接口模拟项目，为图形功能解锁与硬件参数适配提供了技术基础。
- **[Nukem9](https://github.com/Nukem9)**：开源了 [dlssg-to-fsr3](https://github.com/Nukem9/dlssg-to-fsr3)，为帧生成转译技术在社区的普及提供了开拓性的参考。
- **[sdli1995](https://github.com/sdli1995)**：开发并维护了 [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86)，通过补齐内核使 Turing 与 Ampere 架构显卡能够体验原厂 DLSS 帧生成。
- **[ShyVortex](https://github.com/ShyVortex)**：在 [dlss-unlocked](https://github.com/ShyVortex/dlss-unlocked) 项目中对 DLSS 加载机制与跨硬件兼容进行了深入探索。
- **[小米公司 (Xiaomi)](https://github.com/MiSans)**：开源了 [MiSans](https://hyperos.mi.com/font/) 字体家族（SIL Open Font License），为本项目提供了清晰规范的界面排版视觉基础。

---

## 免责声明

1. **非侵入设计**：Fluxion 不修改游戏本体二进制文件，不包含针对游戏授权或数字版权管理（DRM）的绕过行为。
2. **反作弊风险**：帧生成相关模块仅适用于**单机游戏**。切勿在任何具有联机竞技性质或包含内核级反作弊驱动（如 EAC、BattlEye、Vanguard、ACE 等）的游戏中使用 DLL 代理，否则可能导致账号受限或封禁。
3. **责任界限**：本项目作为开源技术探索按“现状”（AS IS）提供，开发者不对因使用本程序或第三方代理补丁可能产生的数据损坏、账号异常或硬件问题承担责任。
