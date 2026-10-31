# Fluxion

<div align="center">

![Version](https://img.shields.io/badge/version-1.3.0-blue?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6?style=flat-square&logo=windows)
![Framework](https://img.shields.io/badge/.NET%20Framework-4.8%20%2F%20WinForms-512BD4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)
![Release](https://img.shields.io/badge/release-v1.3.0-brightgreen?style=flat-square)

**专为 PC 玩家打造的现代全景 Windows 系统调优工作台与智能帧生成管理中心**

[🚀 快速下载](#-快速下载) • [✨ 核心特性](#-核心特性) • [🖼️ 功能概览](#️-功能概览) • [🎮 帧生成实测指南](#-帧生成实测指南) • [🛠️ 编译与构建](#️-编译与构建) • [🙏 致谢与开源项目](#-致谢与开源项目)

</div>

---

## 💡 这是什么

**Fluxion** 是一款独立开发、源码完全公开的高性能桌面级系统优化与帧生成管理工具。

它将分散在 Windows 各处的底层优化设置（电源拓扑、异类 CPU 核心调度、NVAPI 驱动级配置、网卡传输队列、DWM 动效与后台资源管理）收敛为**清晰可调、完全可逆、一键应用/恢复**的现代化控制面板；同时深度融合社区优秀的开源 DLL 转译项目，为 **RTX 20 / 30 系显卡**玩家提供免 40 系显卡开启原厂 DLSS Frame Generation（最高 6X 多倍插帧）的开箱即用体验。

- **极简透明，拒绝黑盒**：所有系统修改项均基于 Windows 公开接口（`powercfg` / 官方 `NVAPI` / 标准注册表 / 网卡适配器配置），不强驻留、不塞常驻广告、退出即净。
- **百分之百可逆**：所有优化项均支持硬编码默认值记录与实时状态比对，提供「一键还原初始状态」与完整配置备份系统。
- **免 40 系限制开启 DLSSG**：智能接管游戏对帧生成运行时的加载，支持常规 DLL 注入与带内核反作弊游戏的免拦截入口（`d3d12.dll` / `dxgi.dll`）。

---

## 🚀 快速下载

最新正式版本为 **v1.3.0**，提供两种获取方式：

| 版本类型 | 资产文件 | 特点与适用场景 | 下载链接 |
|---|---|---|---|
| **安装版 (推荐)** | `Fluxion-Setup-1.3.0.exe` | 现代化向导式安装，支持快捷方式创建与标准卸载 | [官方下载](https://github.com/7SteveJohn/Fluxion/releases/download/v1.3.0/Fluxion-Setup-1.3.0.exe) |
| **便携版 (免安装)** | `Fluxion-v1.3.0-Portable.zip` | 解压即用，配置、封面与运行日志全本地化独立存储 | [官方下载](https://github.com/7SteveJohn/Fluxion/releases/download/v1.3.0/Fluxion-v1.3.0-Portable.zip) |

---

## ✨ 核心特性

### 1. 🎨 现代 Win11 亚克力视觉重塑
- **精致圆角与边缘消毛刺**：重构亚克力悬浮卡片边缘抗锯齿与阴影渐变，消除生硬折角与像素级裁切瑕疵，带来原生 Win11 般平滑温润的视觉体验。
- **平滑深浅色主题**：精心设计的全局调色板层级，托盘右键悬浮菜单与主窗口配色平滑无缝衔接，杜绝视觉割裂。
- **原生内嵌三字重 MiSans**：全量内置 MiSans 字体子集（Bold / Medium / Regular），杜绝不同 Windows 版本因系统字体缺失而导致的文字裁切或排版降级。

### 2. 🖼️ 全自动自适应游戏库与多级封面
- **全平台客户端自动索引**：自动扫描 Steam、Epic Games、WeGame、暴雪战网、Ubisoft Connect、EA App 本地已安装游戏。
- **宽幅黄金比例自适应渲染**：自适应居中宽幅裁切管线，无论在小窗紧凑视图还是 4K 全屏状态下，均能获得完整美观的画质呈现。
- **多级智能封面获取**：聚合 Steam CDN 官方高清素材库、游戏名称检索库与 Bing 智能封面抓取通道，彻底填补非 Steam 游戏（如崩坏：星穹铁道、绝区零、鸣潮等）封面缺失问题。

### 3. 📂 极速数据目录接管与安全迁移
- **秒开现代目录选择器**：彻底淘汰传统 Windows 原生目录浏览器易卡死超时的问题，改用全自绘异步无阻塞现代卡片选择器，支持绝对路径快速粘贴与一键浏览。
- **一键跨盘符完整迁移**：提供数据目录一键安全迁移向导（自动校验 -> 完整无损复制 -> 校验哈希 -> 智能切换），保障已有游戏配置、自订封面与历史备份完好无损。

### 4. ⚡ 深度硬核系统性能优化
提供 18 项精细调节开关与一键体检推荐方案，核心包含：
- **电源管理**：卓越性能模式 / 高性能计划，独创「游戏期动态切档」（进游戏自动切入极限电源档，退出游戏秒级自恢复）。
- **线程与架构调度**：智能配置 `Win32PrioritySeparation`，Intel 12~14 代异类混合架构（P+E 核）优先保障前台游戏进程算力。
- **GPU 与显示优化**：GameDVR 后台开销关闭、系统级游戏模式强制开启、HAGS 硬件加速监测；通过 NVAPI 为每款游戏自动写入 NVIDIA 驱动级参数（最高性能优先 + 超低延迟模式）。
- **网络流与输入响应**：关闭 Nagle 算法与系统流量节流，优化网卡中断与节能状态；解除系统级鼠标加速度，FPS 竞技档启用 0.5ms 高精度系统定时器。
- **内存与运行包守护**：内核内存不换页优化；进游戏自动挂起非关键后台进程、整理系统工作集，游戏退出即时全自动还原。

### 5. 🖥️ 动态分辨率与显示器适配
- **物理硬件实时探测**：实时枚举当前系统中已接入的所有物理显示器规格、可用分辨率与推荐纵横比（支持 CS2、无畏契约等热门竞技游戏 4:3、16:11 快速拉伸切换）。
- **单屏与副屏防黑屏保护**：内置单屏拉伸安全保护策略，杜绝误禁用正在使用的主显示屏而陷入黑屏的风险；全面支持 Windows DPI-PerMonitorV2 高清无损缩放。

---

## 🎮 帧生成实测指南

Fluxion 深度整合了社区验证的 DLL 代理注入方案，通过为 NVIDIA Turing (SM75) 与 Ampere (SM86) 架构显卡补齐原厂 DLSS 运行库缺失的内核指令，实现 20/30 系显卡在单机 3A 游戏中原生开启 DLSS Frame Generation。

### 支持方案矩阵
| 方案分类 | 代理入口 | 适用场景与代表游戏 | 效果与特点 |
|---|---|---|---|
| **标准 DLSSG 代理** | `version.dll` / `dxgi.dll` | 《赛博朋克 2077》、《霍格沃茨之遗》、《巫师 3 次世代》等常规单机 | 接入 `dlssg_for_sm86`，直接在游戏设置中开启官方 DLSS 帧生成，最高 6X |
| **反作弊绕行方案** | `d3d12.dll` | 《绝区零》等对常见文件名进行白名单扫描的游戏 | 借助 DX12 核心加载链绕开常规拦截，稳定调起帧生成支持 |
| **OptiScaler 方案** | `dxgi.dll` / `d3d12.dll` | 《鸣潮》等需配合超分辨率调度与参数定制的游戏 | 集成 OptiScaler 渲染框架，支持游戏内按键（F10）呼出调试面板 |

### 快速上手
1. 打开 Fluxion 进入「帧生成」页面，点击「扫描游戏」定位本机支持的游戏列表；
2. 选中目标游戏，选择推荐方案并点击「开启选中」；
3. 启动游戏，在游戏图形设置中打开 **DLSS Frame Generation**，关闭垂直同步（V-Sync），并将帧率上限设为无限制；
4. 退出游戏后，在 Fluxion 中点击「关闭选中」即可**彻底卸载所有代理文件**，完全恢复官方原始状态。

> ⚠️ **使用须知与安全守则**：
> - 开启前请确保 Windows「硬件加速 GPU 计划 (HAGS)」处于开启状态；
> - 建议游戏基础帧率达到 40 FPS 以上再开启帧生成，以获得最佳帧生成平滑度；
> - **切勿在带有强联网竞技/内核级反作弊的电竞游戏（如 CS2、无畏契约、三角洲行动等）中使用 DLL 代理**；帧生成推荐仅用于单机 3A 游戏。

---

## 🛠️ 编译与构建

Fluxion 保持着极简的工程依赖，支持仅通过 Windows 原生 .NET 工具链完成构建。

### 环境要求
- 操作系统：Windows 10 / 11 64-bit
- 编译工具：.NET Framework 4.8（系统内置 `csc.exe` 即可编译）
- 解释器（可选）：Python 3.8+（用于运行工程静态检查套件）

### 构建指令
```powershell
# 1. 运行静态预检（检测成员引用与排版契约）
python tools/static_check.py src

# 2. 编译生成可执行文件
powershell -NoProfile -ExecutionPolicy Bypass -File compile.ps1
```
编译成功后将在项目根目录下产出 `Fluxion.exe`。

---

## 📂 项目结构

```
Fluxion/
├── src/
│   ├── Core.cs          # 核心业务层：系统优化、NVAPI 接口、后台监控、配置与数据管理
│   ├── Dlssg.cs         # 帧生成中枢：游戏扫描、代理文件管理、诊断分析、方案适配
│   ├── Dlssg040.cs      # 新版帧生成运行时与模型分发对接
│   ├── MfgBridge.cs     # 方案适配与渲染管线桥接
│   ├── Lib.cs           # 游戏库中枢：平台扫描、启动路径解析、智能封面获取管线
│   ├── Pack.cs          # 补丁包与便携资源加载解压引擎
│   ├── Ui.cs            # 现代化 WinForms 界面：亚克力卡片引擎、自定义组件、动态交互
│   ├── app.manifest     # UAC 管理员提权与 DPI-PerMonitorV2 感知声明
│   └── fonts/           # 内嵌 MiSans 字体子集资源
├── tools/               # 静态校验、语法诊断与自动化辅助脚本
├── icon/                # 应用程序多分辨率高清图标
└── docs/                # 历史开发日志与架构演化档案
```

---

## 🙏 致谢与开源项目

Fluxion 的诞生离不开开源社区与先驱开发者们的慷慨分享与探索。在此向以下杰出的开源项目及作者表达最诚挚的敬意与感谢（排名不分先后）：

- **[sdli1995](https://github.com/sdli1995)** — 开源项目 [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 的作者。通过为 SM86/SM75 补齐关键内核，让无数 RTX 20/30 系显卡玩家能够跨代体验原厂 DLSS 帧生成的魅力。
- **[cdozdil](https://github.com/cdozdil) & [OptiScaler 社区](https://github.com/OptiScaler/OptiScaler)** — [OptiScaler](https://github.com/OptiScaler/OptiScaler) 的创作者与维护者。打造了强大的跨架构超分辨率与帧生成转译中间件，极大地拓展了现代游戏的渲染兼容性边界。
- **[Nukem9](https://github.com/Nukem9)** — [dlssg-to-fsr3](https://github.com/Nukem9/dlssg-to-fsr3) 的作者。作为开源帧生成转译框架的拓荒者，其富有远见的架构思想为整个玩家社区的技术探索照亮了道路。
- **[emoose](https://github.com/emoose)** — [fakenvapi](https://github.com/emoose/fakenvapi) 等底层逆向与接口模拟项目的作者。为社区解决硬件白名单限制、解锁高级图形特性提供了不可或缺的基石。
- **[ShyVortex](https://github.com/ShyVortex)** — [dlss-unlocked](https://github.com/ShyVortex/dlss-unlocked) 的探索者。在 DLSS 功能解锁与跨硬件特性兼容研究中做出了重要贡献。
- **[wilsjo2](https://github.com/wilsjo2)** — 在 OptiScaler 神经渲染与多重处理通道（PreSR-Multipass）方面的技术研究与分享。
- **[小米公司 (Xiaomi)](https://github.com/MiSans)** — [MiSans](https://hyperos.mi.com/font/) 开源字体项目。在开源字体协议（SIL Open Font License）下为本项目带来了清晰、现代且高可读性的视觉排版体验。

---

## ⚖️ 免责声明

1. **非侵入性设计**：Fluxion 本身不包含任何反编译、修改游戏本体二进制文件或绕过游戏授权机制的行为；
2. **合规使用提示**：帧生成功能仅推荐用于**单机 3A 游戏**。在任何具有多人对战性质、包含内核级反作弊系统（如 EAC、BE、Vanguard、ACE 等）的游戏中加载第三方 DLL 代理均存在被反作弊误判或封禁的风险，请玩家自行甄别并谨慎使用；
3. **权责划分**：本项目为开源技术探索成果，按“现状”（AS IS）提供，开发者不对因使用本工具或第三方插件造成的游戏存档损坏、账号封禁或硬件异常承担法律责任。
