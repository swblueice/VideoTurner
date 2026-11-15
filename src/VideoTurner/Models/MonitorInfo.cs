namespace VideoTurner.Models;

public class MonitorInfo
{
    public IntPtr Handle { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>
    /// 显示器设备接口路径（CCD 提供），形如
    /// <c>\\?\DISPLAY#SKY0020#5&amp;78450be&amp;0&amp;UID513#{e6f07b5f-…}</c>。
    /// 用它回查注册表可以拿到 ContainerID 与 EDID，从而和音频端点对上号。
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// 显示器所属设备容器 ID（来自注册表 <c>Enum\DISPLAY</c>）。HDMI / DP 音频端点的容器 ID
    /// 与所连显示器相同，这是自动匹配最可靠的依据。空字符串表示取不到。
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// 该显示器是否通过 HDMI / DisplayPort 之类的数字接口连接（也就是「这台屏可能自带音频」）。
    /// 内置屏（eDP/LVDS）通常只能走机身扬声器。
    /// </summary>
    public bool MayCarryAudio { get; set; }

    public string DisplayText => IsPrimary
        ? $"{FriendlyName}（主显示器） · {Width}×{Height}"
        : $"{FriendlyName} · {Width}×{Height}";
}
