using System.Runtime.InteropServices;

namespace VideoTurner.Interop;

/// <summary>
/// 一台「当前处于活动状态」的显示目标（一次 QueryDisplayConfig 的结果项）。
/// </summary>
/// <param name="GdiDeviceName">GDI 设备名，如 <c>\\.\DISPLAY1</c> —— 和 <see cref="Models.MonitorInfo.DeviceName"/> 是同一套名字。</param>
/// <param name="MonitorFriendlyName">显示器型号名，来自显示器 EDID（例如 <c>U2720Q</c>）。</param>
/// <param name="MonitorDevicePath">显示器设备接口路径，形如
/// <c>\\?\DISPLAY#SKY0020#5&amp;78450be&amp;0&amp;UID513#{e6f07b5f-…}</c>。</param>
/// <param name="OutputTechnology">输出接口类型（HDMI / DisplayPort / 内置屏 等）。</param>
/// <param name="EdidProductCodeId">EDID 产品代码。</param>
public sealed record DisplayTarget(
    string GdiDeviceName,
    string MonitorFriendlyName,
    string MonitorDevicePath,
    DisplayConfig.OutputTechnology OutputTechnology,
    ushort EdidProductCodeId)
{
    /// <summary>
    /// 该接口是否可能带音频（HDMI / DisplayPort / DVI 等外部数字接口）。
    /// 内置屏（eDP/LVDS）走的是机身扬声器，不参与「按显示器出声」。
    /// </summary>
    public bool MayCarryAudio =>
        OutputTechnology is DisplayConfig.OutputTechnology.Hdmi
            or DisplayConfig.OutputTechnology.DisplayPortExternal
            or DisplayConfig.OutputTechnology.DisplayPortEmbedded
            or DisplayConfig.OutputTechnology.Dvi
            or DisplayConfig.OutputTechnology.HdmiEmbedded
            or DisplayConfig.OutputTechnology.Miracast
            or DisplayConfig.OutputTechnology.IndirectWired;
}

/// <summary>
/// 显示器拓扑查询（CCD API）。相比 <c>EnumDisplayDevices</c>，这里能直接拿到
/// <b>显示器型号名</b>（Windows 显示设置里那个名字，来自 EDID）
/// 和显示器设备接口路径 —— 后者是回查注册表拿 ContainerID / EDID 的钥匙。
///
/// <para>全部为只读查询，不修改任何显示设置（flags 里只带 QDC_ONLY_ACTIVE_PATHS）。</para>
/// </summary>
public static class DisplayConfig
{
    /// <summary>与 DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY 对齐（只列出会用到的值）。</summary>
    public enum OutputTechnology
    {
        Other = -1,
        Vga = 0,
        Svid = 1,
        CompositeVideo = 2,
        ComponentVideo = 3,
        Dvi = 4,
        Hdmi = 5,
        Lvds = 6,
        DJpn = 8,
        Sdi = 9,
        DisplayPortExternal = 10,
        DisplayPortEmbedded = 11,
        UdiExternal = 12,
        UdiEmbedded = 13,
        SdtvDongle = 14,
        Miracast = 15,
        IndirectWired = 16,
        IndirectVirtual = 17,
        DisplayPortUsbTunnel = 18,
        HdmiEmbedded = unchecked((int)0x80000000), // 内置 HDMI（部分笔记本/一体机）
    }

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    // DISPLAYCONFIG_PATH_INFO 固定 72 字节，DISPLAYCONFIG_MODE_INFO 固定 64 字节。
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, IntPtr pathInfoArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName deviceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName deviceName);

    /// <summary>
    /// 结构体大小自检：CCD 的这几个结构体是固定布局的，一旦 P/Invoke 声明写错，
    /// 读出来就是垃圾内存而不是报错。这里提前断言，出错时直接放弃而不是给出错误结果。
    /// </summary>
    public static bool LayoutLooksSane =>
        Marshal.SizeOf<SourceDeviceName>() == 84
        && Marshal.SizeOf<TargetDeviceName>() == 420;

    /// <summary>
    /// 枚举当前活动的显示目标（每台亮着的显示器一项）。失败时返回空列表，
    /// 调用方应退回 <c>EnumDisplayDevices</c> 的老路径。
    /// </summary>
    public static List<DisplayTarget> GetActiveTargets()
    {
        var results = new List<DisplayTarget>();
        if (!LayoutLooksSane) return results;

        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return results;

            if (pathCount == 0) return results;

            IntPtr pathBuffer = Marshal.AllocHGlobal((int)pathCount * PathInfoSize);
            IntPtr modeBuffer = Marshal.AllocHGlobal((int)Math.Max(modeCount, 1) * ModeInfoSize);
            try
            {
                int err = QueryDisplayConfig(
                    QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, pathBuffer,
                    ref modeCount, modeBuffer,
                    IntPtr.Zero);

                if (err != 0 && err != ERROR_INSUFFICIENT_BUFFER)
                    return results;

                for (int i = 0; i < pathCount; i++)
                {
                    IntPtr pathPtr = IntPtr.Add(pathBuffer, i * PathInfoSize);

                    // DISPLAYCONFIG_PATH_INFO = sourceInfo(20) + targetInfo(48) + flags(4)
                    var adapterId = Marshal.PtrToStructure<LUID>(pathPtr);
                    uint sourceId = (uint)Marshal.ReadInt32(pathPtr, 8);
                    uint targetId = (uint)Marshal.ReadInt32(pathPtr, 20 + 8);

                    var target = QueryTargetName(adapterId, targetId);
                    if (target == null) continue;

                    var source = QuerySourceName(adapterId, sourceId);
                    if (string.IsNullOrEmpty(source)) continue;

                    results.Add(new DisplayTarget(
                        source!,
                        target.Value.FriendlyName,
                        target.Value.DevicePath,
                        (OutputTechnology)target.Value.OutputTechnology,
                        target.Value.ProductCodeId));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuffer);
                Marshal.FreeHGlobal(modeBuffer);
            }
        }
        catch
        {
            // CCD 在极老的系统上可能不可用 —— 静默退回老路径。
        }

        return results;
    }

    private static (string FriendlyName, string DevicePath, int OutputTechnology, ushort ProductCodeId)? QueryTargetName(
        LUID adapterId, uint targetId)
    {
        var info = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = adapterId,
                Id = targetId
            }
        };

        if (DisplayConfigGetDeviceInfo(ref info) != 0)
            return null;

        return (
            info.MonitorFriendlyDeviceName?.Trim() ?? string.Empty,
            info.MonitorDevicePath?.Trim() ?? string.Empty,
            info.OutputTechnology,
            info.EdidProductCodeId);
    }

    private static string? QuerySourceName(LUID adapterId, uint sourceId)
    {
        var info = new SourceDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                AdapterId = adapterId,
                Id = sourceId
            }
        };

        if (DisplayConfigGetDeviceInfo(ref info) != 0)
            return null;

        return info.ViewGdiDeviceName?.Trim();
    }
}
