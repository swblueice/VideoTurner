namespace VideoTurner.Models;

public class AppSettings
{
    public List<MonitorAudioMapping> Mappings { get; set; } = new();
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 开机自启（写 HKCU\...\CurrentVersion\Run）。
    /// </summary>
    public bool AutoStartWithWindows { get; set; }

    /// <summary>
    /// 点关闭按钮时最小化到托盘而不是退出。作为常驻路由工具，默认打开。
    /// </summary>
    public bool MinimizeOnClose { get; set; } = true;

    public bool StartMinimized { get; set; }

    /// <summary>
    /// 启动时对「还没有映射」的显示器自动按型号匹配音频端点。
    /// 手动选择过的映射永远优先，自动匹配只填空缺。默认打开。
    /// </summary>
    public bool AutoMatchOnStartup { get; set; } = true;

    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"

    /// <summary>
    /// 应用清单（按应用分配扬声器）：进程名（不含 .exe，忽略大小写）→ 音频端点 ID。
    /// 命中时优先于「显示器 → 设备」映射，窗口拖到哪台屏都不影响 ——
    /// 也就是启动时的分配顺序：<b>先按清单，再按显示器自动分配</b>。
    /// 只保存勾了「记入清单」的条目。
    /// </summary>
    public Dictionary<string, string> AppOverrides { get; set; } = new();

    // ── 虚拟通道模式 ────────────────────────────────────────────────────
    //
    // 把程序统一钉到一台静音虚拟声卡上，再用进程回环捕获每一路、单独送到目标设备。
    // 好处：程序永远不用重启（窗口移动只影响下游出口），能治「只在启动时读一次输出设备」。

    /// <summary>是否启用虚拟通道模式。</summary>
    public bool VirtualMode { get; set; }

    /// <summary>用哪台虚拟声卡做「静音出口」（AudioDeviceInfo.Id）。空 = 自动挑一台。</summary>
    public string VirtualSinkDeviceId { get; set; } = string.Empty;

    /// <summary>已纳入虚拟通道的进程名（不含 .exe）。看门狗会在它们一启动时就把输出指向虚拟声卡。</summary>
    public List<string> ManagedProcesses { get; set; } = new();
}

public class MonitorAudioMapping
{
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 这条映射是不是自动匹配（按显示器型号）产生的。仅用于界面上的「自动」标记：
    /// 用户手动改过之后会被置回 false，方便一眼看出哪些是机器猜的、哪些是人选的。
    /// </summary>
    public bool IsAutoMatched { get; set; }
}
