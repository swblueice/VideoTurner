using VideoTurner.Models;

namespace VideoTurner.Services;

/// <summary>
/// 一条「显示器 → 音频端点」的自动匹配建议。
/// </summary>
/// <param name="MonitorDeviceName">显示器的设备名，如 <c>\\.\DISPLAY1</c>（映射表的主键）。</param>
/// <param name="AudioDeviceId">建议使用的音频端点 ID。</param>
/// <param name="Reason">给用户看的中文理由，会显示在界面「自动」徽章的悬停提示里。</param>
public sealed record MonitorMatchSuggestion(string MonitorDeviceName, string AudioDeviceId, string Reason);

/// <summary>
/// 按显示器自动匹配音频端点。两条依据，从强到弱：
///
/// <list type="number">
///   <item><b>设备容器（最强）</b>：HDMI / DisplayPort 音频端点的
///     <c>PKEY_Device_ContainerId</c> 与它当前所连显示器的 <c>ContainerID</c> 完全相同 —
///     这是 Windows 自己的配对方式（所以系统设置里那个 HDMI 输出会显示成显示器型号）。
///     本机实测：NVIDIA HDMI 端点的容器 = <c>58ab3f70-…</c> = 对应显示器在
///     <c>Enum\DISPLAY</c> 里的 ContainerID。这条不依赖任何名字，最可靠。</item>
///   <item><b>名称里的型号</b>：部分机器（尤其是 AMD / Intel 显卡）会把显示器型号写进端点名，
///     如 <c>AMD High Definition Audio Device — 3 - U2720Q</c>。把显示器型号令牌
///     （含数字、长度 ≥ 3，如 <c>U2720Q</c>、<c>U2720Q</c>）拿去和归一化后的端点名做包含判断。</item>
/// </list>
///
/// <para>两条都不命中就 <b>不猜</b>：该显示器保持未映射，界面上提示用户手动选择。
/// 宁可漏配也不要把声音切到错误的设备上。</para>
///
/// <para>已有映射的显示器（无论手动还是上次自动匹配）都不会被覆盖：手动选择永远优先。</para>
/// </summary>
public static class AudioEndpointMatcher
{
    /// <summary>
    /// 端点名里没有区分度的词。做名称比对时先归一化（只留字母数字、转大写），
    /// 然后要求端点名包含显示器的型号令牌，因此这些词只用于过滤显示器侧的令牌。
    /// </summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "GENERIC", "PNP", "MONITOR", "DISPLAY", "DEFAULT", "DIGITAL", "ANALOG", "INTERNAL",
        "AUDIO", "DEVICE", "HIGH", "DEFINITION", "SPEAKER", "SPEAKERS", "REALTEK",
        "NVIDIA", "AMD", "INTEL", "HDMI", "DISPLAYPORT", "USB", "OUTPUT", "LINE", "OUT",
        "SOUND", "WINDOWS", "MICROSOFT", "VIRTUAL", "PRIMARY", "SCREEN", "PANEL"
    };

    /// <summary>
    /// 为一组显示器计算自动匹配建议。
    /// </summary>
    /// <param name="monitors">当前显示器列表（<see cref="MonitorService.GetMonitors"/> 的结果）。</param>
    /// <param name="devices">当前活动音频输出端点（<see cref="AudioDeviceService.GetOutputDevices"/> 的结果）。</param>
    /// <param name="alreadyAssigned">
    /// 已有映射的「显示器 → 音频端点」。这些显示器直接跳过（手动选择优先），
    /// 同时它们占用的端点也不会再分给别人。
    /// </param>
    public static List<MonitorMatchSuggestion> Suggest(
        IEnumerable<MonitorInfo> monitors,
        IEnumerable<AudioDeviceInfo> devices,
        IReadOnlyDictionary<string, string>? alreadyAssigned = null)
    {
        var results = new List<MonitorMatchSuggestion>();

        var monitorList = monitors.ToList();
        var deviceList = devices
            .Where(d => !string.IsNullOrEmpty(d.Id))
            .ToList();

        if (deviceList.Count == 0 || monitorList.Count == 0)
            return results;

        // 已被占用的端点：已映射的显示器占用的，加上本轮循环里刚分出去的
        var usedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (alreadyAssigned != null)
        {
            foreach (var kvp in alreadyAssigned)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                    usedDeviceIds.Add(kvp.Value);
            }
        }

        foreach (var monitor in monitorList)
        {
            if (alreadyAssigned != null
                && alreadyAssigned.TryGetValue(monitor.DeviceName, out var existing)
                && !string.IsNullOrEmpty(existing))
            {
                continue;
            }

            var match = MatchByContainer(monitor, deviceList, usedDeviceIds)
                        ?? MatchByNameToken(monitor, deviceList, usedDeviceIds);
            if (match is not { } hit) continue;

            usedDeviceIds.Add(hit.DeviceId);
            results.Add(new MonitorMatchSuggestion(monitor.DeviceName, hit.DeviceId, hit.Reason));
        }

        return results;
    }

    /// <summary>第一层：设备容器 ID 相同 —— HDMI / DP 音频端点与它所在的那台显示器。</summary>
    private static (string DeviceId, string Reason)? MatchByContainer(
        MonitorInfo monitor, List<AudioDeviceInfo> devices, HashSet<string> usedDeviceIds)
    {
        if (string.IsNullOrEmpty(monitor.ContainerId)) return null;

        var hit = devices.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.ContainerId)
            && string.Equals(d.ContainerId, monitor.ContainerId, StringComparison.OrdinalIgnoreCase)
            && !usedDeviceIds.Contains(d.Id));

        if (hit == null) return null;

        return (hit.Id, $"该音频端点与这台显示器属于同一个设备容器（Windows 把 {hit.FriendlyName} 归到这台显示器名下）");
    }

    /// <summary>第二层：端点名字里带着显示器型号。</summary>
    private static (string DeviceId, string Reason)? MatchByNameToken(
        MonitorInfo monitor, List<AudioDeviceInfo> devices, HashSet<string> usedDeviceIds)
    {
        var tokens = ExtractModelTokens(monitor.FriendlyName);
        if (tokens.Count == 0) return null;

        // 长令牌更可信（U2720Q 比 2716 这种更容易误命中）
        foreach (var token in tokens.OrderByDescending(t => t.Length))
        {
            var hit = devices.FirstOrDefault(d =>
                Normalize(d.FriendlyName).Contains(token, StringComparison.OrdinalIgnoreCase)
                && !usedDeviceIds.Contains(d.Id));

            if (hit != null)
                return (hit.Id, $"型号 {token} 同时出现在显示器与音频端点名称中");
        }

        return null;
    }

    /// <summary>
    /// 从显示器友好名里抽取型号令牌。只保留含数字、长度 ≥ 3 的词
    /// （型号几乎总带数字，如 <c>U2720Q</c>、<c>U2720Q</c>、<c>27GP950</c>），并过滤通用词与纯数字噪声。
    /// </summary>
    internal static List<string> ExtractModelTokens(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
            return new List<string>();

        var tokens = new List<string>();
        foreach (var raw in SplitTokens(friendlyName))
        {
            if (raw.Length < 3) continue;
            if (NoiseTokens.Contains(raw)) continue;
            if (!raw.Any(char.IsDigit)) continue;
            if (!tokens.Contains(raw, StringComparer.OrdinalIgnoreCase))
                tokens.Add(raw);
        }

        return tokens;
    }

    /// <summary>
    /// 归一化：只保留字母数字并转大写。<c>U2720Q</c> 因此能匹配上
    /// <c>3 - U2720Q</c>、<c>[U2720Q]</c>、（甚至 <c>T-214-F06</c>）等各种写法。
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var buffer = new char[value.Length];
        int length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToUpperInvariant(ch);
        }

        return new string(buffer, 0, length);
    }

    /// <summary>按非字母数字切词。</summary>
    private static IEnumerable<string> SplitTokens(string value)
    {
        var current = new System.Text.StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToUpperInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
