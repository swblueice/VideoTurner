# 参与贡献

感谢愿意花时间改进 VideoTurner。下面是最省事的参与方式。

## 报 Bug / 提需求

开 [issue](https://github.com/swblueice/VideoTurner/issues/new/choose) 前先扫一眼
[已知限制](README.md#已知限制)和[排错顺序](README.md#路由没生效按这个顺序查) —— 里面已经写了
「最小化后声音跑掉」「程序只在启动时读一次设备」这类最常见的情况。

有效的 bug 报告通常包含：

- Windows 版本（`winver`）、显示器数量与接口（HDMI / DP / 内置屏）、音频设备名称
- 是直连模式还是虚拟通道模式；有没有装 VB-Cable 之类的虚拟声卡
- 设置页 →「运行自检」的完整报告（可复制）
- `%APPDATA%\VideoTurner\routing.log` 里相关的几行
- 具体哪个程序、期望去哪台设备、实际去了哪台

> 贴日志前把里面的用户名、机器名、设备名过一遍，只留复现需要的信息。

## 提 PR

1. Fork 并从 `main` 开分支，分支名用 `fix/…`、`feat/…` 这类前缀。
2. 保持改动聚焦：一个 PR 解决一件事。顺手的大重构单独开。
3. 提交信息写清「改了什么 / 为什么」，中英文都行。
4. 提之前本地至少跑通：
   ```powershell
   dotnet build src/VideoTurner/VideoTurner.csproj -c Release
   dotnet run --project tools/MatcherProbe     # 只读探针，不改任何系统设置
   ```
5. 涉及路由逻辑的改动，请附上「改前 / 改后」的 `routing.log` 片段或首页状态行对比。

### 代码约定

- C# / .NET 8 / WPF，`Nullable` 已开启，新代码不要引 `#nullable disable`。
- 界面文案中文，代码标识符英文；注释可以是中文。
- 新增 P/Invoke 放 `Interop/`，业务逻辑放 `Services/`，不要直接写进 View 的 code-behind。
- **不要**引入联网检查更新、遥测、崩溃上报。本项目刻意不做这些。
- 新增 NuGet 依赖请确认许可证与 MIT 兼容，并同步更新
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

### 关于上游代码

本项目派生自 [ScreenSound](https://github.com/twibster/ScreenSound)（MIT）。
从上游移植补丁是可以的，但请在 PR 说明里注明来源 commit / PR 号，
方便日后回溯。GPL 或无许可证项目的代码不能直接抄进来。

## 打包验证（可选）

```powershell
powershell -ExecutionPolicy Bypass -File tools/make-installer.ps1
powershell -ExecutionPolicy Bypass -File tools/make-portable-zip.ps1
```

CI 会在 Windows runner 上自动构建并产出这两个包的 artifact，PR 里不必自己上传。

## 行为准则

参与本项目即表示同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
