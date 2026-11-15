## 这个 PR 做了什么

<!-- 一到三句话说明改动与动机 -->

关联 issue：<!-- #123 -->

## 改动类型

- [ ] Bug 修复
- [ ] 新功能
- [ ] 重构 / 代码整理（不改变行为）
- [ ] 文档
- [ ] 构建 / 打包 / CI
- [ ] 从上游移植补丁（请注明来源 commit 或 PR 号）

## 影响范围

- [ ] 音频路由逻辑（`Services/RoutingEngine.cs`、`AudioRouterService.cs`）
- [ ] 自动匹配（`AudioEndpointMatcher.cs`、`DeviceContainerService.cs`、`DisplayConfig.cs`）
- [ ] 虚拟通道（`VirtualChannelEngine.cs`、`ProcessLoopbackCapture.cs`）
- [ ] 界面（`Views/`、`ViewModels/`）
- [ ] 安装包 / 打包脚本
- [ ] 只涉及文档

## 怎么验证的

<!--
  涉及路由逻辑的改动，请贴「改前 / 改后」的首页状态行或 routing.log 片段对比。
  只读探针（不改系统设置）在这里很好用：
    dotnet run --project tools/MatcherProbe
    dotnet run --project tools/LoopbackProbe
-->

```powershell
dotnet build src/VideoTurner/VideoTurner.csproj -c Release
```

## 自查清单

- [ ] 本地 `dotnet build -c Release` 通过
- [ ] 没有引入联网检查更新、遥测或崩溃上报
- [ ] 新增依赖的许可证与 MIT 兼容，并已更新 `THIRD-PARTY-NOTICES.md`
- [ ] 改的是界面文案时，中文表述与现有风格一致
- [ ] 提交信息说明了「改了什么 / 为什么」
