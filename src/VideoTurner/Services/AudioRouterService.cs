using VideoTurner.Interop;

namespace VideoTurner.Services;

public class AudioRouterService : IDisposable
{
    private AudioPolicyConfigClient? _client;
    private readonly object _lock = new();

    private AudioPolicyConfigClient GetClient()
    {
        if (_client == null)
        {
            lock (_lock)
            {
                _client ??= new AudioPolicyConfigClient();
            }
        }
        return _client;
    }

    /// <summary>
    /// 把某个进程的默认输出设备固定到 <paramref name="deviceId"/>。
    /// 失败时通过 <paramref name="error"/> 返回具体原因（含 HRESULT），
    /// 不再像以前那样只写 Debug 输出 —— 那是「拖了窗口没反应却查不出原因」的根源。
    /// </summary>
    public bool TryRouteProcessToDevice(uint processId, string deviceId, out string? error)
    {
        try
        {
            GetClient().RouteProcessToDevice(processId, deviceId);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"HRESULT 0x{ex.HResult:X8}: {ex.Message}";
            RoutingLog.Error($"路由失败 PID {processId} → {deviceId}：{error}");
            return false;
        }
    }

    public bool TryRouteProcessToDevice(uint processId, string deviceId)
        => TryRouteProcessToDevice(processId, deviceId, out _);

    public bool TryClearProcessRouting(uint processId, out string? error)
    {
        try
        {
            GetClient().ClearProcessRouting(processId);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"HRESULT 0x{ex.HResult:X8}: {ex.Message}";
            RoutingLog.Error($"清除固定项失败 PID {processId}：{error}");
            return false;
        }
    }

    public bool TryClearProcessRouting(uint processId)
        => TryClearProcessRouting(processId, out _);

    /// <summary>
    /// 自检：激活未公开接口并在<b>本进程</b>上做一次 写入 → 读回 → 清除。
    /// 本进程没有音频会话、不会出声，所以完全无副作用，适合做成界面上的「诊断」按钮 ——
    /// 用来区分「接口被系统拒绝（权限/版本问题）」和「引擎逻辑没走到」。
    /// </summary>
    public (bool Ok, string Report) SelfTest(bool writeTest = true)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var client = GetClient();
            sb.AppendLine($"接口激活：成功（{(Environment.OSVersion.Version.Build >= 22000 ? "21H2+ 分支" : "旧版分支")}）");

            using var deviceService = new AudioDeviceService();
            var devices = deviceService.GetOutputDevices();
            sb.AppendLine($"可见输出设备：{devices.Count} 个");
            foreach (var d in devices)
                sb.AppendLine($"    {(d.IsDefault ? "*" : " ")} {d.FriendlyName}");

            if (devices.Count == 0)
                return (false, sb.AppendLine("没有任何输出设备，无法自检。").ToString());

            var target = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
            var self = (uint)Environment.ProcessId;

            if (!writeTest)
            {
                var current = client.GetPersistedDefaultAudioEndpoint(self, Interop.EDataFlow.eRender, Interop.ERole.eConsole);
                sb.AppendLine($"读接口：成功（本进程当前固定项 = {(string.IsNullOrEmpty(current) ? "无" : current)}）");
                sb.AppendLine("结论：接口可用。演练模式（--dry-run）下不做写入测试。");
                return (true, sb.ToString());
            }

            foreach (var role in new[] { Interop.ERole.eConsole, Interop.ERole.eMultimedia })
            {
                client.SetPersistedDefaultAudioEndpoint(self, Interop.EDataFlow.eRender, role, target.Id);
                var back = client.GetPersistedDefaultAudioEndpoint(self, Interop.EDataFlow.eRender, role);
                // 读回的是完整接口路径（\\?\SWD#MMDEVAPI#{id}#{guid}），要归一化后再比
                var ok = !string.IsNullOrEmpty(back) && back.Contains(target.Id, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"写入 {role}：成功；读回{(ok ? "一致 ✅" : "⚠ 不一致")} = {back}");
            }

            client.ClearProcessRouting(self);
            var after = client.GetPersistedDefaultAudioEndpoint(self, Interop.EDataFlow.eRender, Interop.ERole.eConsole);
            sb.AppendLine($"清除后读回：{(string.IsNullOrEmpty(after) ? "(空，已恢复系统默认)" : after)}");
            sb.AppendLine("结论：本进程可以正常调用该接口，路由失败的原因不在权限。");
            return (true, sb.ToString());
        }
        catch (Exception ex)
        {
            sb.AppendLine($"结论：调用失败 —— HRESULT 0x{ex.HResult:X8}：{ex.Message}");
            sb.AppendLine("如果这里是 0x80070005（拒绝访问），说明当前进程上下文被系统拒绝：");
            sb.AppendLine("  * 常见原因是程序被某个受限环境（沙箱 / AppContainer / 受限令牌）启动；");
            sb.AppendLine("  * 请从「开始菜单 / 桌面快捷方式」正常双击启动后再试。");
            return (false, sb.ToString());
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
