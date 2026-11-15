# 第三方组件与许可

本项目以 **MIT** 许可发布（见 [LICENSE](LICENSE)）。它包含、链接或跟随分发下列第三方作品，
各自的许可与版权归原作者所有。分发本项目的二进制产物时，请一并保留本文件与
[`LICENSE-ScreenSound-MIT.txt`](LICENSE-ScreenSound-MIT.txt)。

## 1. 上游项目（代码派生来源）

### ScreenSound

- 项目：<https://github.com/twibster/ScreenSound>
- 许可：MIT，Copyright © 2026 Omar Omran
- 用途：本项目的起点。音频路由引擎、`Interop/AudioPolicyConfig.cs`、显示器/会话服务、
  WPF 界面结构等大块代码派生自它；本项目在其之上做了中文化、按显示器型号的音频端点自动匹配、
  应用清单（按 exe 固定扬声器）、进程级虚拟通道等改动。
- MIT 许可要求随二进制保留版权与许可声明，原文见 `LICENSE-ScreenSound-MIT.txt`，
  项目里 `src/VideoTurner/VideoTurner.csproj` 的 `<Copyright>` 与安装包许可页也一并保留。

## 2. NuGet 依赖（随程序分发）

| 包 | 版本 | 许可 | 版权 |
|---|---|---|---|
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) | 8.4.2 | MIT | © .NET Foundation and Contributors |
| [NAudio](https://www.nuget.org/packages/NAudio) | 2.3.0 | MIT | © Mark Heath |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | 8.0.10 | MIT | © .NET Foundation and Contributors |
| [WPF-UI](https://www.nuget.org/packages/WPF-UI) | 2.1.0 | MIT | © Lepo.i (WPF-UI) |

各自完整许可文本随 NuGet 包分发，也可在上述包页面的 *License* 一节查看。

## 3. 构建 / 打包工具（不随程序分发）

| 工具 | 用途 | 许可 |
|---|---|---|
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | 生成安装包（`tools/make-installer.ps1`） | Inno Setup License（允许自由分发由其编译的安装程序） |
| [.NET 8 SDK](https://dotnet.microsoft.com/) | 构建 | MIT |

`tools/InnoSetup6/` 与 `dist/` 不入库；Inno Setup 由使用者自行下载，或由 `tools/install-inno-setup.ps1` 现装。

## 4. 可选运行时依赖（用户自装，不随本项目分发）

### VB-Audio Virtual Cable

- 项目：<https://vb-audio.com/Cable/>
- 用途：虚拟通道模式需要一台静音虚拟声卡作为「落点」。
- 本项目**不捆绑、不再分发**它的任何二进制；`tools/install-virtual-cable.ps1` 只从 VB-Audio 官方
  地址下载官方安装包并调用它。请遵守 VB-Audio 自己的许可条款（含其捐赠/授权政策）。

## 5. 商标

Windows、DirectShow、WASAPI 等名称是 Microsoft Corporation 的商标。
本项目与 Microsoft、VB-Audio、ScreenSound 作者均无从属关系。
