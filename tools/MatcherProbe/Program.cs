using System.Text;
using VideoTurner.Interop;
using VideoTurner.Services;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine($"CCD 结构体自检：{(DisplayConfig.LayoutLooksSane ? "OK" : "失败（布局对不上，会退回旧路径）")}");
Console.WriteLine();

var monitorService = new MonitorService();
var monitors = monitorService.GetMonitors(forceRefresh: true);

using var deviceService = new AudioDeviceService();
var devices = deviceService.GetOutputDevices();

Console.WriteLine("=== 当前活动显示器（经 CCD 补全型号/容器）===");
foreach (var m in monitors)
{
    var tokens = AudioEndpointMatcher.ExtractModelTokens(m.FriendlyName);
    Console.WriteLine($"  {m.DeviceName}  {m.FriendlyName}  {m.Width}x{m.Height}  主屏={m.IsPrimary}");
    Console.WriteLine($"      DevicePath  = {m.DevicePath}");
    Console.WriteLine($"      ContainerId = {(string.IsNullOrEmpty(m.ContainerId) ? "(取不到)" : m.ContainerId)}   可能带音频={m.MayCarryAudio}");
    Console.WriteLine($"      型号令牌    = [{string.Join(", ", tokens)}]");
}

Console.WriteLine();
Console.WriteLine("=== 活动音频输出端点（含容器 ID）===");
foreach (var d in devices)
{
    Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.FriendlyName}");
    Console.WriteLine($"      ContainerId = {(string.IsNullOrEmpty(d.ContainerId) ? "(取不到)" : d.ContainerId)}");
    Console.WriteLine($"      id = {d.Id}");
}

Console.WriteLine();
Console.WriteLine("=== 自动匹配结果 ===");
var suggestions = AudioEndpointMatcher.Suggest(monitors, devices);
if (suggestions.Count == 0)
{
    Console.WriteLine("  （没有匹配到任何一项）");
}
foreach (var s in suggestions)
{
    var deviceName = devices.FirstOrDefault(d => d.Id == s.AudioDeviceId)?.FriendlyName ?? s.AudioDeviceId;
    Console.WriteLine($"  {s.MonitorDeviceName}  →  {deviceName}");
    Console.WriteLine($"      理由：{s.Reason}");
}

Console.WriteLine();
Console.WriteLine("=== 交叉验证：容器 ID 对照表 ===");
foreach (var d in devices.Where(x => !string.IsNullOrEmpty(x.ContainerId)))
{
    var sameContainer = monitors.Where(m => string.Equals(m.ContainerId, d.ContainerId, StringComparison.OrdinalIgnoreCase))
                                .Select(m => $"{m.DeviceName}({m.FriendlyName})")
                                .ToList();
    Console.WriteLine($"  {d.FriendlyName,-42} 容器 {d.ContainerId[..8]}… → " +
                      $"{(sameContainer.Count > 0 ? string.Join(", ", sameContainer) : "（不对应任何显示器，通常是内置/虚拟设备）")}");
}

return 0;
