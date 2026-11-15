# 变更记录

本项目从 v1.0.0 起按 [语义化版本](https://semver.org/lang/zh-CN/) 记录。

## [Unreleased]

### 计划

- 移植上游「最小化 / 隐藏时保持已分配的显示器」修复（Known limitations 第 6 条）
- 移植 `SysVAD` 派生的自研虚拟声卡驱动，去掉对第三方虚拟声卡的依赖
- 安装包代码签名

## [1.0.0] - 2026-11-12

首个公开版本。相对上游 [ScreenSound](https://github.com/twibster/ScreenSound) 的改动：

### 新增

- **中文界面**：首页 / 应用清单 / 设置 / 关于四页与托盘菜单全部中文化。
- **按显示器自动匹配音频端点**：设备容器 ID（`PKEY_Device_ContainerId` ↔ 显示器
  `ContainerID`）+ 端点名称里的显示器型号两层依据；匹配不到就保持未映射，不猜。
- **显示器型号取自 CCD**：`QueryDisplayConfig` → `DISPLAYCONFIG_TARGET_DEVICE_NAME`，
  解决 `EnumDisplayDevices` 只返回「Generic PnP Monitor」的问题；EDID（tag `0xFC`）兜底。
- **手动覆盖永远优先**：手改过的映射标记为「手动」，自动匹配不再覆盖。
- **按应用分配扬声器 + 应用清单**：每个运行中的程序可单独选扬声器，可记入清单持久化；
  启动顺序为先清单、后自动；每秒一次的看门狗让策略在程序出声之前就生效。
- **虚拟通道模式（进程级、无驱动）**：程序先落到静音虚拟声卡，再按窗口所在显示器把每路音频
  送到真正的设备；每个程序一条独立通道（进程回环捕获，Windows 10 2004+）。
- **黑匣子与诊断**：首页状态行、`routing.log`、设置页「运行自检 / 依次试听各设备 / 打开日志」。
- **`--dry-run` 演练模式**：只开界面，不动音频、不写设置。
- **一键安装包与便携版**：`tools/make-installer.ps1`、`tools/make-portable-zip.ps1`，
  自包含发布 + Inno Setup 中文向导，按用户安装、不弹 UAC。
- **只读探针**：`tools/MatcherProbe`、`tools/LoopbackProbe`、`tools/ApiProbe`。

### 变更

- `AppInfoService` 取代上游的 `UpdateService`，去掉联网检查更新与赞助页。
- 过滤「空容器」（`00000000-0000-0000-ffff-ffffffffffff`），避免虚拟声卡被错配给主屏。

### 移除

- 联网更新检查与赞助页（指向原作者仓库与 Ko-fi，对本项目无意义）。

### 已知问题

- 路由粒度是进程而非窗口（Windows 未公开接口的固有限制）。
- 最小化的播放器窗口可能被认到另一台显示器（继承自上游，修复待移植）。
- 无代码签名，SmartScreen 首次运行会提示未知发布者。

[1.0.0]: https://github.com/swblueice/VideoTurner/releases/tag/v1.0.0
