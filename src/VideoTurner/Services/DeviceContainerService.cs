using Microsoft.Win32;

namespace VideoTurner.Services;

/// <summary>
/// 从注册表读「显示器 ↔ 音频端点」的硬件关联信息。两件事：
///
/// <list type="number">
///   <item><b>显示器</b>：由 CCD 拿到的设备接口路径
///     （<c>\\?\DISPLAY#SKY0020#5&amp;78450be&amp;0&amp;UID513#{…}</c>）回查
///     <c>HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\…</c>，读出 <c>ContainerID</c>，
///     并在型号名缺失时从 <c>Device Parameters\EDID</c> 里解析出显示器名称。</item>
///   <item><b>音频端点</b>：<c>PKEY_Device_ContainerId</c>
///     （<c>{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2</c>）。关键点：HDMI / DisplayPort
///     音频端点的容器 ID 与它当前所连显示器的容器 ID <b>完全相同</b>
///     （Windows 就是靠这个把「NVIDIA High Definition Audio — HDMI」显示成那台显示器的名字）。
///     这是把音频端点和具体某台显示器对上号的可靠依据，比猜名字稳得多。</item>
/// </list>
///
/// <para>全部是只读注册表访问，不需要管理员权限。任何一步失败都只是返回 null，
/// 匹配流程会退回「按名称里的型号」那一层。</para>
/// </summary>
public static class DeviceContainerService
{
    private const string EnumDisplayRoot = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";
    private const string MmDevicesRenderRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    /// <summary>PKEY_Device_ContainerId，在 MMDevices 里以 REG_BINARY 存放。</summary>
    private const string ContainerIdValueName = "{8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2";

    /// <summary>
    /// 「空容器」GUID。内置声卡、虚拟声卡、以及部分内置屏在注册表里都是这个值，
    /// 它表示「没有真正归属到某个物理设备」，千万不能拿来配对 ——
    /// 否则会把机身扬声器/虚拟声卡错配给一台显示器（实测踩到过）。
    /// </summary>
    private static readonly Guid NullContainerId = new("00000000-0000-0000-ffff-ffffffffffff");

    private static readonly Dictionary<string, string?> MonitorContainerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> MonitorModelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> EndpointContainerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>
    /// 由显示器设备接口路径取 ContainerID（GUID 字符串，形如
    /// <c>58ab3f70-d21a-5244-92c4-8f2a77d15987</c>）。取不到返回 null。
    /// </summary>
    public static string? GetMonitorContainerId(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

        lock (CacheLock)
        {
            if (MonitorContainerCache.TryGetValue(monitorDevicePath, out var cached))
                return cached;
        }

        var result = NormalizeGuid(ReadMonitorValue(monitorDevicePath, "ContainerID") as string);

        lock (CacheLock)
        {
            MonitorContainerCache[monitorDevicePath] = result;
        }

        return result;
    }

    /// <summary>
    /// 由显示器设备接口路径从 EDID 里解析显示器名称（EDID 中 tag 0xFC 的描述符）。
    /// CCD 已经给了型号名，这里只是兜底与交叉验证。
    /// </summary>
    public static string? GetMonitorModelFromEdid(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

        lock (CacheLock)
        {
            if (MonitorModelCache.TryGetValue(monitorDevicePath, out var cached))
                return cached;
        }

        var result = ParseEdidMonitorName(ReadMonitorValue(monitorDevicePath, "EDID") as byte[]);

        lock (CacheLock)
        {
            MonitorModelCache[monitorDevicePath] = result;
        }

        return result;
    }

    /// <summary>
    /// 由音频端点 ID（NAudio 的 <c>device.ID</c>，形如
    /// <c>{0.0.0.00000000}.{ee72fff9-…}</c>）取它的设备容器 ID。取不到返回 null。
    /// </summary>
    public static string? GetAudioEndpointContainerId(string? audioDeviceId)
    {
        var endpointGuid = ExtractEndpointGuid(audioDeviceId);
        if (endpointGuid == null) return null;

        lock (CacheLock)
        {
            if (EndpointContainerCache.TryGetValue(endpointGuid, out var cached))
                return cached;
        }

        string? result = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{MmDevicesRenderRoot}\{endpointGuid}\Properties");
            if (key?.GetValue(ContainerIdValueName) is byte[] blob && blob.Length >= 16)
            {
                // MMDevices 存的是「头部 + GUID」，GUID 在最后 16 字节，小端字节序。
                var guidBytes = blob[^16..];
                result = FilterContainerGuid(new Guid(guidBytes));
            }
        }
        catch
        {
            // 注册表被策略挡住 / 键不存在 —— 交给上层退回按名称匹配
        }

        lock (CacheLock)
        {
            EndpointContainerCache[endpointGuid] = result;
        }

        return result;
    }

    private static object? ReadMonitorValue(string monitorDevicePath, string valueName)
    {
        var keyPath = ToEnumDisplayKeyPath(monitorDevicePath);
        if (keyPath == null) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>\\?\DISPLAY#SKY0020#5&amp;78450be&amp;0&amp;UID513#{接口GUID}</c>
    /// → <c>SYSTEM\CurrentControlSet\Enum\DISPLAY\SKY0020\5&amp;78450be&amp;0&amp;UID513</c>
    /// </summary>
    internal static string? ToEnumDisplayKeyPath(string monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

        var path = monitorDevicePath.Trim();

        // 统一去掉 \\?\ 前缀，并按 '#' 分段
        const string prefix = @"\\?\";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
            path = path[prefix.Length..];

        var parts = path.Split('#');
        if (parts.Length < 3) return null;
        if (!string.Equals(parts[0], "DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;

        // parts[1] = 硬件 ID（厂商+型号，如 SKY0020），parts[2] = 实例 ID（如 5&78450be&0&UID513）
        return $@"{EnumDisplayRoot}\{parts[1]}\{parts[2]}";
    }

    /// <summary>
    /// 从 <c>{0.0.0.00000000}.{ee72fff9-…}</c> 里取出最后一段端点的 GUID（带花括号）。
    /// </summary>
    internal static string? ExtractEndpointGuid(string? audioDeviceId)
    {
        if (string.IsNullOrWhiteSpace(audioDeviceId)) return null;

        var open = audioDeviceId.LastIndexOf('{');
        var close = audioDeviceId.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        var candidate = audioDeviceId.Substring(open + 1, close - open - 1);
        return Guid.TryParse(candidate, out var guid) ? $"{{{guid:D}}}" : null;
    }

    /// <summary>把 <c>{GUID}</c> / GUID 文本统一成小写、不带花括号的形式，便于比较。</summary>
    private static string? NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().Trim('{', '}');
        if (!Guid.TryParse(trimmed, out var guid)) return null;
        return FilterContainerGuid(guid);
    }

    /// <summary>
    /// 过滤掉「空容器」和全零 GUID：这两种值表示设备没有真正归属，
    /// 拿它们配对只会产生错误映射（见 <see cref="NullContainerId"/> 的说明）。
    /// </summary>
    private static string? FilterContainerGuid(Guid guid)
        => guid == Guid.Empty || guid == NullContainerId ? null : guid.ToString();

    /// <summary>
    /// 解析 EDID 里的显示器名称：从第 54 字节起是 4 个 18 字节的描述符，
    /// tag 为 0xFC 的那个就是名称（后面跟 13 字节 ASCII，0x0A 结束）。
    /// </summary>
    internal static string? ParseEdidMonitorName(byte[]? edid)
    {
        if (edid == null || edid.Length < 128) return null;

        for (int offset = 54; offset + 18 <= 126; offset += 18)
        {
            // 描述符前 3 字节为 0 且第 4 字节为 0xFC 时，这是一个「显示器名称」描述符
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;
            if (edid[offset + 3] != 0xFC) continue;

            var chars = new List<char>(13);
            for (int i = offset + 5; i < offset + 18; i++)
            {
                var b = edid[i];
                if (b == 0x0A || b == 0x00) break;
                chars.Add((char)b);
            }

            var name = new string(chars.ToArray()).Trim();
            if (name.Length > 0 && name.All(c => !char.IsControl(c)))
                return name;
        }

        return null;
    }
}
