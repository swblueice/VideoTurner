using System.Text;
using System.Text.Json;
using VideoTurner.Interop;
using VideoTurner.Models;
using VideoTurner.Services;

Console.OutputEncoding = Encoding.UTF8;

var setPid = 0u;
string? setName = null;
var clearPid = 0u;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "set" && i + 2 < args.Length) { setPid = uint.Parse(args[i + 1]); setName = args[i + 2]; }
    else if (args[i] == "clear" && i + 1 < args.Length) { clearPid = uint.Parse(args[i + 1]); }
}

Console.WriteLine("=== 环境 ===");
Console.WriteLine($"  OS: {Environment.OSVersion}  (Build {Environment.OSVersion.Version.Build})");
Console.WriteLine($"  接口分支: {(Environment.OSVersion.Version.Build >= 22000 ? "21H2+ (ab3d4648-…)" : "旧版 (2a59116d-…)")}");
Console.WriteLine($"  位数: {(Environment.Is64BitProcess ? "x64" : "x86")}   管理员: {IsElevated()}");

// 读程序实际用的设置文件，用它来复现「引擎会怎么决策」
var settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoTurner", "settings.json");
var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(settingsPath))
{
    try
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
        foreach (var m in settings?.Mappings ?? new List<MonitorAudioMapping>())
            mappings[m.MonitorDeviceName] = m.AudioDeviceId;
        Console.WriteLine($"  设置文件: {settingsPath}  （IsEnabled={settings?.IsEnabled}, 映射 {mappings.Count} 条）");
    }
    catch (Exception ex) { Console.WriteLine($"  设置文件读取失败: {ex.Message}"); }
}
else
{
    Console.WriteLine($"  设置文件不存在: {settingsPath}");
}
foreach (var kv in mappings) Console.WriteLine($"      映射: {kv.Key} → {kv.Value}");

Console.WriteLine();
Console.WriteLine("=== 1) 激活 Windows.Media.Internal.AudioPolicyConfig ===");
AudioPolicyConfigClient client;
try
{
    client = new AudioPolicyConfigClient();
    Console.WriteLine("  ✅ 激活成功");
}
catch (Exception ex)
{
    Console.WriteLine($"  ❌ 激活失败: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
    return 1;
}

using (client)
{
    var sessions = new AudioSessionService().GetActiveSessions();
    var monitorService = new MonitorService();
    monitorService.GetMonitors(forceRefresh: true);

    using var deviceService = new AudioDeviceService();
    var devices = deviceService.GetOutputDevices();
    string NameOf(string? id) => string.IsNullOrEmpty(id)
        ? "(无固定项)"
        : devices.FirstOrDefault(d => d.Id == id)?.FriendlyName ?? $"(未知设备 {id})";

    Console.WriteLine();
    Console.WriteLine($"=== 输出设备 {devices.Count} 个 ===");
    foreach (var d in devices)
        Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.FriendlyName}   {d.Id}");

    Console.WriteLine();
    Console.WriteLine($"=== 2) 活动音频会话 {sessions.Count} 个 ===");
    if (sessions.Count == 0)
        Console.WriteLine("  （当前没有程序在出声；播放一点音频再跑本命令才能看到会话）");

    foreach (var s in sessions)
    {
        Console.WriteLine();
        Console.WriteLine($"  进程 {s.ProcessName} (PID {s.ProcessId})");
        Console.WriteLine($"    当前播放设备 : {s.AudioDeviceName}");

        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia })
        {
            try
            {
                var persisted = client.GetPersistedDefaultAudioEndpoint(s.ProcessId, EDataFlow.eRender, role);
                Console.WriteLine($"    {role,-13}: {(string.IsNullOrEmpty(persisted) ? "(无固定项 → 跟随系统默认)" : NameOf(persisted) + "   [" + persisted + "]")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {role,-13}: ❌ 读取失败 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        // 复现引擎的决策：找到窗口所在显示器 → 查映射 → 决定目标设备
        var monitor = monitorService.GetMonitorForProcess(s.ProcessId);
        if (monitor == null)
        {
            Console.WriteLine("    窗口检测     : ❌ 找不到该进程的可见窗口（引擎会当作『无规则』并清除固定项）");
        }
        else
        {
            Console.WriteLine($"    窗口所在显示器: {monitor.FriendlyName} ({monitor.DeviceName})");
            var hasMapping = mappings.TryGetValue(monitor.DeviceName, out var target);
            Console.WriteLine(hasMapping && !string.IsNullOrEmpty(target)
                ? $"    引擎目标设备 : {NameOf(target)}"
                : "    引擎目标设备 : （该显示器没有映射 → 引擎会清除固定项，跟随系统默认）");
        }
    }

    if (setPid != 0)
    {
        var target = devices.FirstOrDefault(d => d.FriendlyName.Contains(setName!, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            Console.WriteLine($"\n❌ 没找到名字包含「{setName}」的设备。可用：");
            foreach (var d in devices) Console.WriteLine($"     {d.FriendlyName}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"=== 3) 写入测试：把 PID {setPid} 固定到「{target.FriendlyName}」 ===");
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia })
        {
            try
            {
                client.SetPersistedDefaultAudioEndpoint(setPid, EDataFlow.eRender, role, target.Id);
                var back = client.GetPersistedDefaultAudioEndpoint(setPid, EDataFlow.eRender, role);
                var ok = string.Equals(back, target.Id, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  {role,-13} Set 成功；读回 = {NameOf(back)}   {(ok ? "✅ 一致" : "⚠ 不一致：" + back)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {role,-13} ❌ 失败 0x{ex.HResult:X8} {ex.Message}");
            }
        }
    }

    if (args.Contains("selftest"))
    {
        // 零副作用自检：拿本进程自己的 PID 试「写入 → 读回 → 清除」，
        // 本进程没有音频会话、也不会出声，所以不会影响任何正在播放的东西。
        var self = (uint)Environment.ProcessId;
        var target = devices.FirstOrDefault(d => d.FriendlyName.Contains("HDMI", StringComparison.OrdinalIgnoreCase))
                     ?? devices.FirstOrDefault();

        Console.WriteLine();
        Console.WriteLine($"=== 自检：用本进程 PID {self} 试 写入 → 读回 → 清除（目标设备「{target?.FriendlyName}」）===");
        if (target == null) { Console.WriteLine("  没有可用输出设备"); return 1; }

        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia })
        {
            try
            {
                client.SetPersistedDefaultAudioEndpoint(self, EDataFlow.eRender, role, target.Id);
                var back = client.GetPersistedDefaultAudioEndpoint(self, EDataFlow.eRender, role);
                Console.WriteLine($"  {role,-13} Set → 读回 = {NameOf(back)}   {(string.Equals(back, target.Id, StringComparison.OrdinalIgnoreCase) ? "✅ 一致" : "⚠ 不一致")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {role,-13} ❌ 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        client.ClearProcessRouting(self);
        Console.WriteLine("  已清除本进程的固定项，恢复跟随系统默认");
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia })
        {
            var back = client.GetPersistedDefaultAudioEndpoint(self, EDataFlow.eRender, role);
            Console.WriteLine($"  {role,-13} 清除后读回 = {(string.IsNullOrEmpty(back) ? "(无固定项 ✅)" : "⚠ 仍有 " + back)}");
        }
    }

    if (clearPid != 0)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 3) 清除：让 PID {clearPid} 回到跟随系统默认 ===");
        try
        {
            client.ClearProcessRouting(clearPid);
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia })
            {
                var back = client.GetPersistedDefaultAudioEndpoint(clearPid, EDataFlow.eRender, role);
                Console.WriteLine($"  {role,-13} 清除后读回 = {(string.IsNullOrEmpty(back) ? "(无固定项 → 跟随系统默认)" : back)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 失败 0x{ex.HResult:X8} {ex.Message}");
        }
    }
}

return 0;

static bool IsElevated()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
