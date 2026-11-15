namespace VideoTurner.Models;

public class AudioSessionInfo
{
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string MonitorFriendlyName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
    public string AudioDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// 该会话「实际正在播放」的设备。引擎会把 <see cref="AudioDeviceId"/> 改成目标设备
    /// 以便界面显示「声音会去哪」，真实值保留在这里，用来判断
    /// 「策略设了但程序没搬过去」（只在启动时读一次输出设备的那类程序）。
    /// </summary>
    public string ActualAudioDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// True when this session's audio device was chosen by an explicit per-app pin
    /// (see <c>AppSettings.AppOverrides</c>) rather than the monitor → device mapping.
    /// Used by the UI to render a pin indicator.
    /// </summary>
    public bool IsOverridden { get; set; }
}
