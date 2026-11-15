using VideoTurner.Models;

namespace VideoTurner.Services;

/// <summary>
/// 找「虚拟声卡」端点：它的作用是把程序的原始输出接住，让用户听不到，再由我们
/// 一路一路地重新送到真正的音箱 / 耳机上（见 <see cref="VirtualChannelEngine"/>）。
///
/// <para>为什么要虚拟声卡：有些程序（游戏、模拟器客户端、部分音乐播放器）
/// 启动时只读一次输出设备，之后怎么改系统策略都不搬。让它们一开始就落到一个
/// <b>永远存在、永远不变</b>的虚拟端点上，它们就再也不用被重启；
/// 「现在到底从哪台音箱出声」由我们在下游动态决定。</para>
///
/// <para>识别方式是<b>按名称</b>：VB-Cable（CABLE Input）、VoiceMeeter（VoiceMeeter Input）、
/// Virtual Audio Cable（Line 1）、以及将来我们自己的驱动（VideoTurner Virtual …）。
/// 名称里带显示器型号的虚拟端点还会被「自动匹配」认到对应的显示器上 ——
/// 这就是「按名称自动匹配的虚拟设备」那一层。</para>
/// </summary>
public static class VirtualSinkService
{
    private static readonly string[] VirtualNameHints =
    {
        "CABLE",            // VB-Audio Virtual Cable
        "VOICEMEETER",      // VoiceMeeter 系列
        "VIRTUAL AUDIO",    // Virtual Audio Cable
        "VB-AUDIO",
        "VBCABLE",
        "VIDEOTURNER",      // 将来的自研驱动
        "VIRTUAL",
        "LOOPBACK"
    };

    /// <summary>这个名字看起来像不像一个「虚拟输出端点」。</summary>
    public static bool LooksVirtual(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;

        var normalized = Normalize(friendlyName);
        foreach (var hint in VirtualNameHints)
        {
            if (normalized.Contains(Normalize(hint), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>从当前端点列表里挑出所有虚拟输出端点。</summary>
    public static List<AudioDeviceInfo> GetVirtualSinks(IEnumerable<AudioDeviceInfo> devices)
        => devices
            .Where(d => !string.IsNullOrEmpty(d.Id) && LooksVirtual(d.FriendlyName))
            .ToList();

    /// <summary>
    /// 名称自动匹配：虚拟端点的名字里若带着某台显示器的型号令牌（如 <c>U2720Q</c>），
    /// 就把这台显示器认给它。返回「显示器设备名 → 端点」。
    /// </summary>
    public static List<MonitorMatchSuggestion> SuggestSinksForMonitors(
        IEnumerable<MonitorInfo> monitors,
        IEnumerable<AudioDeviceInfo> virtualSinks,
        IReadOnlyDictionary<string, string>? alreadyAssigned = null)
    {
        var results = new List<MonitorMatchSuggestion>();
        var sinks = virtualSinks.ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (alreadyAssigned != null)
        {
            foreach (var value in alreadyAssigned.Values)
                if (!string.IsNullOrEmpty(value)) used.Add(value);
        }

        foreach (var monitor in monitors)
        {
            if (alreadyAssigned != null
                && alreadyAssigned.TryGetValue(monitor.DeviceName, out var existing)
                && !string.IsNullOrEmpty(existing))
                continue;

            var tokens = AudioEndpointMatcher.ExtractModelTokens(monitor.FriendlyName);
            if (tokens.Count == 0) continue;

            AudioDeviceInfo? hit = null;
            string? hitToken = null;
            foreach (var token in tokens.OrderByDescending(t => t.Length))
            {
                hit = sinks.FirstOrDefault(s =>
                    Normalize(s.FriendlyName).Contains(token, StringComparison.Ordinal)
                    && !used.Contains(s.Id));
                if (hit != null) { hitToken = token; break; }
            }

            if (hit == null) continue;

            used.Add(hit.Id);
            results.Add(new MonitorMatchSuggestion(monitor.DeviceName, hit.Id,
                $"虚拟端点名称里带有显示器型号 {hitToken}"));
        }

        return results;
    }

    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        int length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToUpperInvariant(ch);
        }
        return new string(buffer, 0, length);
    }
}
