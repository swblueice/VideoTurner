namespace VideoTurner.Models;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    /// <summary>
    /// 音频端点的设备容器 ID（PKEY_Device_ContainerId）。对 HDMI / DisplayPort 端点来说，
    /// 这个值和所连显示器的 ContainerID 相同 —— 自动匹配就靠它。空字符串表示取不到。
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// 下拉框与屏幕阅读器都靠它取显示名。不加这个重写的话，
    /// ComboBox 的选中项在无障碍树里会显示成类型名「VideoTurner.Models.AudioDeviceInfo」。
    /// </summary>
    public override string ToString() => FriendlyName;
}
