using VideoTurner.Models;
using VideoTurner.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoTurner.ViewModels;

public partial class MonitorMappingViewModel : ObservableObject
{
    public MonitorInfo Monitor { get; }

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private int _volume = 100; // 0-100, maps to device master volume

    [ObservableProperty]
    private bool _hasDevice; // true when a real device is selected (not "No mapping")

    /// <summary>
    /// 这条映射是不是「按显示器型号自动匹配」填进去的（见
    /// <see cref="Services.AudioEndpointMatcher"/>）。用户手动改过下拉框之后会被清掉，
    /// 界面上用「自动」小徽章区分机器猜的与人选的。手动选择永远优先于自动匹配。
    /// </summary>
    [ObservableProperty]
    private bool _isAutoMatched;

    /// <summary>自动匹配的理由，显示在「自动」徽章的 ToolTip 上。</summary>
    [ObservableProperty]
    private string _matchReason = string.Empty;

    /// <summary>
    /// 指定过某台设备、但该设备<b>此刻不在设备列表里</b>（关了显示器、拔了 HDMI、
    /// 端点未激活……）。这是会造成「静默失效」的关键状态：映射仍留在
    /// <see cref="DesiredAudioDeviceId"/> 里，但推送路由时会因设备不存在而被过滤掉 ——
    /// 于是「拖窗口没反应」而界面看不出任何异常。现在把它显式标出来。
    /// </summary>
    [ObservableProperty]
    private bool _isDeviceMissing;

    public string MissingDeviceHint =>
        "此屏指定的音频设备当前不可用（未连接或未激活），路由暂时不会生效；" +
        "接回设备后会自动恢复，也可以在右侧改选一台当前可用的设备。";

    /// <summary>
    /// The user's persistent intent — the audio device ID this monitor should
    /// map to. Distinct from <see cref="SelectedAudioDevice"/>, which mirrors
    /// what's actually present in the dropdown right now.
    /// <para>
    /// When a device is unplugged, <see cref="SelectedAudioDevice"/> goes null
    /// (FirstOrDefault returns nothing for a missing ID), but DesiredAudioDeviceId
    /// stays — so re-plugging the same endpoint, or restarting the app while it
    /// happens to be present again, restores the mapping. Empty string means
    /// "(No mapping)" / explicitly cleared.
    /// </para>
    /// </summary>
    public string DesiredAudioDeviceId { get; set; } = string.Empty;

    public string DisplayName => Monitor.DisplayText;

    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; }

    /// <summary>
    /// 下拉框选中项发生变化。<paramref name="vm"/> 是被改动的那台显示器，
    /// 用来把该条映射标记为「手动选择」（自动匹配写入时不走这里，见 <c>_isLoading</c> 守卫）。
    /// </summary>
    public event Action<MonitorMappingViewModel>? MappingChanged;
    public event Action<MonitorMappingViewModel>? VolumeChanged;

    public MonitorMappingViewModel(MonitorInfo monitor, ObservableCollection<AudioDeviceInfo> devices)
    {
        Monitor = monitor;
        AvailableDevices = devices;
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        HasDevice = value != null && !string.IsNullOrEmpty(value.Id);
        // Capture the user's intent — but only when the change comes with a
        // real value. The refresh path assigns null when the device the user
        // wanted is currently unavailable; that must NOT erase the desired ID,
        // otherwise re-plugging the device can't restore the mapping. User
        // picks always carry a non-null value (the "(No mapping)" sentinel
        // included — its Id is "").
        if (value != null)
            DesiredAudioDeviceId = value.Id;
        MappingChanged?.Invoke(this);
    }

    partial void OnVolumeChanged(int value)
    {
        VolumeChanged?.Invoke(this);
    }
}

/// <summary>
/// 首页「活动音频会话」里的一行：一个正在出声的进程，以及它此刻的出声设备。
/// <para>
/// 这一行同时也是「给这个程序分配扬声器」的入口：<see cref="SelectedAssignment"/>
/// 是扬声器下拉框（Id 为空的那项表示「自动 · 按窗口所在显示器」），
/// <see cref="IsRemembered"/> 是「记入清单」勾选框 —— 勾上就写进设置文件，
/// 下次启动由清单优先分配。
/// </para>
/// </summary>
public partial class SessionDisplayViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private uint _processId;
    [ObservableProperty] private string _monitorName = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>
    /// True when this session is being routed by an explicit per-app assignment
    /// rather than the monitor → device mapping. Drives the pin glyph in the row UI
    /// and the "取消分配" entry in the right-click menu.
    /// </summary>
    [ObservableProperty] private bool _isOverridden;

    /// <summary>
    /// 引擎这一轮实际送达的设备 ID（行里那行小字）。「自动」模式下用它告诉用户
    /// 自动分配的结果；同时勾「记入清单」时会拿它当默认值。
    /// </summary>
    [ObservableProperty] private string _effectiveAudioDeviceId = string.Empty;

    /// <summary>扬声器下拉框当前选中项；Id 为空的那项表示「自动（按窗口所在显示器）」。</summary>
    [ObservableProperty] private AudioDeviceInfo? _selectedAssignment;

    /// <summary>是否把这个分配记入清单（记入后下次启动沿用）。</summary>
    [ObservableProperty] private bool _isRemembered;

    /// <summary>下拉框选项，与主 ViewModel 共享同一个集合（含「自动」项）。</summary>
    public ObservableCollection<AudioDeviceInfo> AssignmentOptions { get; }

    /// <summary>程序化同步时置位，避免把界面回填当成用户操作。</summary>
    internal bool SuppressAssignmentChange { get; set; }

    /// <summary>用户改了扬声器下拉框。</summary>
    public event Action<SessionDisplayViewModel>? AssignmentChanged;

    /// <summary>用户勾/取消了「记入清单」。</summary>
    public event Action<SessionDisplayViewModel>? RememberChanged;

    public SessionDisplayViewModel(ObservableCollection<AudioDeviceInfo> assignmentOptions)
    {
        AssignmentOptions = assignmentOptions;
        // 默认勾上「记入清单」：分配一次就记住，是绝大多数人的预期；
        // 只想临时改一下的，取消勾选即可（只对本次运行有效）。
        _isRemembered = true;
    }

    partial void OnSelectedAssignmentChanged(AudioDeviceInfo? value)
    {
        if (!SuppressAssignmentChange)
            AssignmentChanged?.Invoke(this);
    }

    partial void OnIsRememberedChanged(bool value)
    {
        if (!SuppressAssignmentChange)
            RememberChanged?.Invoke(this);
    }

    /// <summary>程序化回填下拉框与勾选框（不触发用户操作事件）。</summary>
    internal void ApplyAssignmentState(AudioDeviceInfo? option, bool remembered)
    {
        SuppressAssignmentChange = true;
        try
        {
            SelectedAssignment = option;
            IsRemembered = remembered;
        }
        finally
        {
            SuppressAssignmentChange = false;
        }
    }
}

/// <summary>
/// 「应用清单」里的一条：进程名 + 分配到的扬声器。
/// <see cref="AudioDeviceName"/> 每次设备刷新都会按当前设备列表重新解析 ——
/// 设备被拔掉时退回占位名，但底层 ID 保留，插回来就自动恢复。
/// <para>
/// <see cref="IsRemembered"/> 表示这条分配是否会写进设置文件（下次启动沿用）。
/// 取消勾选的条目只对本次运行有效。
/// </para>
/// </summary>
public partial class AppOverrideViewModel : ObservableObject
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _audioDeviceId = string.Empty;
    [ObservableProperty] private string _audioDeviceName = string.Empty;

    /// <summary>
    /// App icon shown in the assignment page. Captured from the originating session
    /// at assignment-creation time, or back-filled by <c>MainWindowViewModel</c> when an
    /// entry loaded from disk matches a live session on the next session update.
    /// Null while the app isn't running — the UI falls back to a pin glyph.
    /// </summary>
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>勾上 = 记入清单（写进 settings.json，下次启动按清单优先分配）。</summary>
    [ObservableProperty] private bool _isRemembered = true;

    /// <summary>这个程序此刻是否正在运行（行里的小徽章）。</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>清单页下拉框的选中项。</summary>
    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;

    /// <summary>下拉框选项，与主 ViewModel 共享同一个集合（含「自动」项）。</summary>
    public ObservableCollection<AudioDeviceInfo> AssignmentOptions { get; }

    /// <summary>程序化同步时置位，避免把界面回填当成用户操作。</summary>
    internal bool SuppressChange { get; set; }

    public event Action<AppOverrideViewModel>? AssignmentChanged;
    public event Action<AppOverrideViewModel>? RememberChanged;

    public AppOverrideViewModel(ObservableCollection<AudioDeviceInfo> assignmentOptions)
    {
        AssignmentOptions = assignmentOptions;
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (!SuppressChange)
            AssignmentChanged?.Invoke(this);
    }

    partial void OnIsRememberedChanged(bool value)
    {
        if (!SuppressChange)
            RememberChanged?.Invoke(this);
    }

    /// <summary>程序化回填字段（不触发用户操作事件）。</summary>
    internal void ApplyState(string deviceId, string deviceName, bool remembered, ImageSource? icon = null)
    {
        SuppressChange = true;
        try
        {
            AudioDeviceId = deviceId;
            AudioDeviceName = deviceName;
            IsRemembered = remembered;
            if (icon != null) Icon = icon;
        }
        finally
        {
            SuppressChange = false;
        }
    }
}

/// <summary>「加入清单」下拉框里的一项：一个正在运行的进程名（不含 .exe）。</summary>
public class ProcessChoiceViewModel
{
    public string ProcessName { get; }

    public ProcessChoiceViewModel(string processName) => ProcessName = processName;

    public override string ToString() => ProcessName;
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;
    private readonly RoutingEngine _routingEngine;
    private readonly SettingsService _settingsService;
    private readonly AudioDeviceNotifier _deviceNotifier;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly Dispatcher _dispatcher;
    private bool _isLoading;
    // 演练模式（--dry-run）：界面照常工作、映射照常显示，但绝不启用路由、绝不写设置文件。
    private readonly bool _isDryRun = App.IsDryRun;
    // Volatile: read on the COM RPC thread in OnDeviceChangeDetected, written
    // on the UI thread in Dispose(). Without the barrier a late callback can
    // miss the write and queue dispatcher work during shutdown.
    private volatile bool _disposed;

    // Debounce window for coalescing bursts of device events. Plugging in a USB
    // DAC routinely fires OnDeviceAdded + OnDeviceStateChanged + OnDefaultDeviceChanged
    // back-to-back; without this we'd rebuild the dropdown three times in a row.
    private static readonly TimeSpan DeviceRefreshDebounce = TimeSpan.FromMilliseconds(300);

    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "VideoTurner";

    /// <summary>下拉框里「不映射」这一项的显示文字：选中它等于让该屏继续跟随系统默认设备。</summary>
    public const string NoMappingDisplayName = "(不映射 · 跟随系统默认设备)";

    /// <summary>
    /// 扬声器下拉框里「不单独分配」这一项的显示文字：选中它等于把该程序的出口交还给
    /// 「窗口所在显示器的映射」（也就是第二次自动分配），并取消它在清单里的条目。
    /// </summary>
    public const string AutoAssignmentDisplayName = "自动 · 按窗口所在显示器";

    /// <summary>设备当前不存在时的占位名（映射本身仍然保留，插回来就恢复）。</summary>
    private const string DeviceNotConnectedName = "(设备未连接)";

    /// <summary>本程序自己的进程名（「加入清单」的候选列表里当然不该出现它）。</summary>
    private static readonly string SelfProcessName =
        System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "VideoTurner");

    /// <summary>没有音频能力、列出来只会碍眼的系统进程。</summary>
    private static readonly HashSet<string> NonAudioProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "services"
    };

    private readonly Dictionary<uint, ImageSource?> _iconCache = new();

    /// <summary>用户选过的虚拟声卡 ID。设备暂时不在时也保留，插回来能自动恢复。</summary>
    private string _desiredVirtualSinkId = string.Empty;

    public ObservableCollection<MonitorMappingViewModel> Monitors { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();
    public ObservableCollection<SessionDisplayViewModel> ActiveSessions { get; } = new();

    /// <summary>
    /// 按应用分配的扬声器（引擎眼里的「清单」）。进程名（不含 .exe，忽略大小写）是键。
    /// 命中的分配优先于「显示器 → 设备」映射：这就是「优先清单，然后自动分配」。
    /// <para>
    /// 条目分两种：<see cref="AppOverrideViewModel.IsRemembered"/> 为 true 的会写进
    /// settings.json，下次启动沿用；为 false 的只对本次运行有效。
    /// </para>
    /// </summary>
    public ObservableCollection<AppOverrideViewModel> AppOverrides { get; } = new();

    // ── 按应用分配扬声器（清单）─────────────────────────────────────────

    /// <summary>扬声器下拉框共享选项的第一项：不单独分配，交给「窗口所在显示器」的映射。</summary>
    private readonly AudioDeviceInfo _autoAssignmentOption = new()
    {
        Id = string.Empty,
        FriendlyName = AutoAssignmentDisplayName
    };

    /// <summary>下拉框选项的实例缓存（按设备 ID）。复用实例，刷新设备列表时选中项才不会被打断。</summary>
    private readonly Dictionary<string, AudioDeviceInfo> _assignmentOptionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有扬声器下拉框共用的选项：「自动」+ 当前可用设备 + 清单里暂时失联的设备占位项。</summary>
    public ObservableCollection<AudioDeviceInfo> AssignmentOptions { get; } = new();

    /// <summary>「加入清单」时可选的真实扬声器（不含「自动」，不含失联设备的占位项）。</summary>
    public ObservableCollection<AudioDeviceInfo> SpeakerChoices { get; } = new();

    /// <summary>「加入清单」下拉框里的候选程序（当前正在运行的进程名）。</summary>
    public ObservableCollection<ProcessChoiceViewModel> RunningProcesses { get; } = new();

    /// <summary>分配/清单操作的最近一次结果（首页与清单页各显示一处）。</summary>
    [ObservableProperty]
    private string _assignmentStatus = string.Empty;

    [ObservableProperty]
    private ProcessChoiceViewModel? _selectedRunningProcess;

    /// <summary>「加入清单」时给新条目选的扬声器，默认取系统默认设备。</summary>
    [ObservableProperty]
    private AudioDeviceInfo? _newAssignmentDevice;

    // ── 虚拟通道 ────────────────────────────────────────────────────────

    /// <summary>当前可用的虚拟声卡端点（按名称识别：VB-Cable / VoiceMeeter / Virtual Audio Cable / 将来的自研驱动）。</summary>
    public ObservableCollection<AudioDeviceInfo> VirtualSinks { get; } = new();

    /// <summary>已纳入虚拟通道的进程名（不含 .exe）。</summary>
    public ObservableCollection<string> ManagedProcesses { get; } = new();

    /// <summary>
    /// 是否启用虚拟通道模式。开启后程序先落到静音虚拟声卡上，再由我们按窗口所在显示器
    /// 把它们的声音送到真正的设备 —— 程序本身再也不用重启。
    /// </summary>
    [ObservableProperty]
    private bool _virtualModeEnabled;

    /// <summary>选定的虚拟声卡端点；null = 还没选（或一台都没有）。</summary>
    [ObservableProperty]
    private AudioDeviceInfo? _selectedVirtualSink;

    /// <summary>虚拟通道状态说明（显示在设置页）。</summary>
    [ObservableProperty]
    private string _virtualModeStatus = string.Empty;

    [ObservableProperty]
    private bool _isRoutingEnabled;

    [ObservableProperty]
    private int _monitorCount;

    [ObservableProperty]
    private int _deviceCount;

    /// <summary>
    /// 最近一次路由的结果（成功或失败，失败含 HRESULT）。显示在首页，
    /// 并写入 <c>%APPDATA%\VideoTurner\routing.log</c> —— 排查「拖了窗口没反应」全靠它。
    /// </summary>
    [ObservableProperty]
    private string _lastRoutingStatus = string.Empty;

    /// <summary>自检报告（设置页「诊断」卡片里可复制）。</summary>
    [ObservableProperty]
    private string _diagnosticsReport = string.Empty;

    public string LogFilePath => RoutingLog.LogPath;

    [ObservableProperty]
    private bool _autoStartWithWindows;

    [ObservableProperty]
    private bool _minimizeOnClose;

    [ObservableProperty]
    private bool _startMinimized;

    /// <summary>启动时按显示器型号自动匹配音频端点（只填空缺，不覆盖手动选择）。</summary>
    [ObservableProperty]
    private bool _autoMatchOnStartup = true;

    /// <summary>上一次自动匹配的结果说明（界面上一行小字）。</summary>
    [ObservableProperty]
    private string _autoMatchStatus = string.Empty;

    [ObservableProperty]
    private int _selectedThemeIndex; // 0 = System, 1 = Light, 2 = Dark

    public string[] ThemeOptions { get; } = { "跟随系统", "浅色", "深色" };

    [ObservableProperty]
    private int _currentPage; // 0 = Home, 1 = Pinned, 2 = Settings, 3 = About

    // --- 关于页绑定 -------------------------------------------------------
    //
    // 版本号与版权行都从程序集元数据现读（见 AppInfoService），
    // 这样界面上显示的永远是实际在跑的那个构建，不会和 csproj 里的字面量脱节。
    // 自有版本不做联网更新检查（上游那套是查 twibster/ScreenSound 的 GitHub Release，
    // 对本项目没有意义），所以这里没有 UpdateStatus / LatestReleaseUrl 之类的状态。

    public string AppVersionDisplay => $"版本 {AppInfoService.CurrentVersion}";

    /// <summary>
    /// 关于页底部的版权行。MSBuild 从 csproj 的 &lt;Copyright&gt; 生成标准写法
    /// "Copyright (c) YYYY …"，这里把 "(c)" 换成 © 字形，只为了排版好看；
    /// csproj 里保留 ASCII 形式是因为 Win32 文件属性对话框更认那种写法。
    /// </summary>
    public string CopyrightDisplay =>
        AppInfoService.Copyright.Replace("Copyright (c)", "©", StringComparison.OrdinalIgnoreCase);

    /// <summary>关于页里的许可说明（本项目基于 MIT 许可的 ScreenSound）。</summary>
    public string LicenseNotice =>
        "本项目基于 MIT 许可的开源项目 ScreenSound（Copyright © 2026 Omar Omran）修改而来，" +
        "按 MIT 许可要求保留其版权与许可声明，详见随程序附带的 LICENSE-ScreenSound-MIT.txt。";

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        _monitorService = new MonitorService();
        _audioDeviceService = new AudioDeviceService();
        _sessionService = new AudioSessionService();
        _routerService = new AudioRouterService();
        _settingsService = new SettingsService();
        _routingEngine = new RoutingEngine(_monitorService, _sessionService, _routerService);
        _routingEngine.ManagedProcessDiscovered += OnManagedProcessDiscovered;

        _routingEngine.SessionsUpdated += OnSessionsUpdated;

        // Debounced timer for device hot-plug refreshes. Constructed bound to the
        // UI dispatcher so the Tick handler runs on the UI thread and can safely
        // mutate the AudioDevices ObservableCollection.
        _deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = DeviceRefreshDebounce
        };
        _deviceRefreshTimer.Tick += OnDeviceRefreshTimerTick;

        _deviceNotifier = new AudioDeviceNotifier();
        _deviceNotifier.DevicesChanged += OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged += OnDeviceChangeDetected;
        _deviceNotifier.Start();

        // 显示器插拔/分辨率变化（接扩展坞、投影、换线）时重建映射行，
        // 否则新插入的显示器在界面上不出现，也就没法给它选音频设备。
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        LoadDevicesAndMonitors();
        LoadSettings();

        if (_isDryRun)
        {
            AutoMatchStatus = "演练模式（--dry-run）：只展示界面与匹配结果，不会改动任何程序的音频输出，"
                              + "也不会保存设置。确认无误后去掉该参数重新启动即可生效。";
        }
    }

    /// <summary>
    /// 显示配置变化回调（可能在非 UI 线程），把重建工作丢回 UI 线程执行。
    /// 重建会短暂停一下路由引擎，随后按当前映射重启，因此正在播放的声音不会掉。
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            Refresh();
        });
    }

    private void LoadDevicesAndMonitors()
    {
        AudioDevices.Clear();
        Monitors.Clear();

        AudioDevices.Add(new AudioDeviceInfo { Id = "", FriendlyName = NoMappingDisplayName });

        foreach (var device in _audioDeviceService.GetOutputDevices())
        {
            AudioDevices.Add(device);
        }

        _monitorService.InvalidateCache();
        foreach (var monitor in _monitorService.GetMonitors(forceRefresh: true))
        {
            var vm = new MonitorMappingViewModel(monitor, AudioDevices);
            vm.MappingChanged += OnMappingChanged;
            vm.VolumeChanged += OnVolumeChanged;
            Monitors.Add(vm);
        }

        MonitorCount = Monitors.Count;
        DeviceCount = AudioDevices.Count - 1;

        RefreshVirtualSinks();
        RefreshAssignmentOptions();
    }

    /// <summary>
    /// Hot-plug entry point. Runs on a COM RPC thread. All it does is restart
    /// the debounce timer on the UI thread — the actual refresh happens once
    /// the burst of events settles.
    /// </summary>
    private void OnDeviceChangeDetected()
    {
        if (_disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Stop+Start resets the elapsed time, so rapid-fire events keep
            // pushing the refresh out until the stream quiets down.
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Start();
        });
    }

    private void OnDeviceRefreshTimerTick(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        if (_disposed) return;
        RefreshAudioDevicesPreservingSelections();
    }

    /// <summary>
    /// Rebuilds <see cref="AudioDevices"/> from the current endpoint list while
    /// preserving per-monitor selections wherever the same device ID is still
    /// present. Devices that disappeared leave their monitor's selection at
    /// null — the dropdown reflects reality rather than showing a ghost — but
    /// the desired ID stays on each monitor VM, so re-plugging the device
    /// restores the mapping on the next refresh. Must be called on the UI thread.
    /// </summary>
    private void RefreshAudioDevicesPreservingSelections()
    {
        // Snapshot the user's intent (DesiredAudioDeviceId), not the live
        // SelectedAudioDevice — otherwise an unplug-then-replug cycle erases
        // the mapping: the unplug refresh nulls SelectedAudioDevice, and the
        // replug refresh would then snapshot string.Empty and have nothing to
        // restore. Snapshotting intent makes the round-trip lossless.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);

        // _isLoading suppresses SaveSettings while we mutate selections
        // programmatically — a hot-plug must not overwrite the user's saved
        // mappings just because a device went away.
        _isLoading = true;
        try
        {
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDeviceInfo { Id = string.Empty, FriendlyName = NoMappingDisplayName });
            foreach (var device in _audioDeviceService.GetOutputDevices())
                AudioDevices.Add(device);

            DeviceCount = AudioDevices.Count - 1;

            foreach (var monitorVm in Monitors)
            {
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                // Match by Id (not reference) — AudioDeviceInfo is rebuilt each
                // refresh. If the device is gone, FirstOrDefault returns null;
                // the OnSelectedAudioDeviceChanged guard keeps DesiredAudioDeviceId
                // intact in that case so a future refresh can still resolve it.
                monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
            }
        }
        finally
        {
            _isLoading = false;
        }

        // Push to engine even if mappings didn't semantically change — a newly
        // plugged device might make a previously-dangling mapping valid again,
        // and a newly removed one needs the engine to stop trying to route to it.
        PushMappingsToEngine();

        // Re-resolve names on existing pins (their device IDs are stable, but
        // the friendly-name string for an unplugged device flips between
        // "(Device not connected)" and the real name on hot-plug).
        RefreshAppOverrideDeviceNames();

        // 扬声器下拉框（首页会话行 + 清单页）也要跟上新的设备列表：
        // 选项里增删设备、失联设备换成占位项，然后把各行选中项重新对上。
        RefreshAssignmentOptions();
        SyncAssignmentViews();

        // 虚拟声卡列表也要跟着刷新（插拔 USB 声卡 / 安装虚拟声卡之后）
        RefreshVirtualSinks();

        // Re-sync sliders in case a re-plugged device has a different volume
        // than the one we last remembered.
        RefreshAssignedVolumes();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var settings = _settingsService.Load();

            foreach (var mapping in settings.Mappings)
            {
                var monitorVm = Monitors.FirstOrDefault(m => m.Monitor.DeviceName == mapping.MonitorDeviceName);
                if (monitorVm != null)
                {
                    // Set DesiredAudioDeviceId unconditionally so a hot-plug
                    // later in the session can resolve a device that was missing
                    // at app start. SelectedAudioDevice only gets set when the
                    // device is actually present right now.
                    monitorVm.DesiredAudioDeviceId = mapping.AudioDeviceId;
                    monitorVm.IsAutoMatched = mapping.IsAutoMatched;
                    var device = AudioDevices.FirstOrDefault(d => d.Id == mapping.AudioDeviceId);
                    if (device != null)
                        monitorVm.SelectedAudioDevice = device;
                }
            }

            AutoStartWithWindows = settings.AutoStartWithWindows;
            MinimizeOnClose = settings.MinimizeOnClose;
            StartMinimized = settings.StartMinimized;
            AutoMatchOnStartup = settings.AutoMatchOnStartup;

            // 虚拟通道设置
            _desiredVirtualSinkId = settings.VirtualSinkDeviceId ?? string.Empty;
            RefreshVirtualSinks();
            ManagedProcesses.Clear();
            foreach (var name in settings.ManagedProcesses ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ManagedProcesses.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                ManagedProcesses.Add(name.Trim());
            }
            VirtualModeEnabled = settings.VirtualMode;
            SelectedThemeIndex = settings.ThemeMode switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            // Rebuild the per-app pin list from disk. Resolve device names against
            // the current device list — pins whose device is currently unplugged
            // get a placeholder name but keep their ID, so they reactivate cleanly
            // when the device returns.
            //
            // Collapse duplicate keys case-insensitively on the way in. System.Text.Json
            // deserializes into a Dictionary with the default (ordinal) comparer, so a
            // hand-edited settings.json containing both "chrome" and "Chrome" would load
            // without complaint — but PushOverridesToEngine() / SaveSettings() build an
            // OrdinalIgnoreCase dict downstream and would throw on the collision,
            // aborting startup. Normalize here; last key wins, consistent with a
            // re-save of what we end up with.
            AppOverrides.Clear();
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in settings.AppOverrides)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    continue;
                normalized[kvp.Key.Trim()] = kvp.Value;
            }
            foreach (var (processName, deviceId) in normalized)
            {
                var deviceName = AudioDevices.FirstOrDefault(d => d.Id == deviceId)?.FriendlyName
                                 ?? DeviceNotConnectedName;
                // 从设置文件载入的条目按定义都算「记入清单」；只有本次运行临时分配、
                // 没勾「记入清单」的条目才会留在集合里却不落盘。
                var entry = new AppOverrideViewModel(AssignmentOptions) { ProcessName = processName };
                entry.AssignmentChanged += OnOverrideAssignmentChanged;
                entry.RememberChanged += OnOverrideRememberChanged;
                entry.ApplyState(deviceId, deviceName, remembered: true);
                AppOverrides.Add(entry);
            }

            RefreshAssignedVolumes();

            // 启动时补空缺：只对「还没有映射」的显示器按型号自动配对，
            // 手动选择过的映射不受影响。放在 IsRoutingEnabled 赋值之前，
            // 这样路由引擎起来时用的就已经是补全后的映射表。
            if (AutoMatchOnStartup)
                RunAutoMatch(announce: false);

            PushMappingsToEngine();
            PushOverridesToEngine();
            PushVirtualRouting();
            UpdateVirtualModeStatus();

            // 清单里的分配先落到下拉框上（「自动」的那部分由引擎按显示器映射补）。
            RefreshAssignmentOptions();
            SyncAssignmentViews();

            if (AppOverrides.Count > 0)
            {
                AssignmentStatus =
                    $"已按清单载入 {AppOverrides.Count} 个程序的扬声器分配（清单优先）；" +
                    "清单之外的程序按窗口所在显示器自动分配。";
            }

            // 演练模式下一律不启用路由：映射照常显示，但不会去动任何程序的音频
            IsRoutingEnabled = !_isDryRun && settings.IsEnabled;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnMappingChanged(MonitorMappingViewModel vm)
    {
        // During programmatic refreshes (LoadSettings, hot-plug refresh, Refresh
        // command) selections are assigned in a loop. Running the full push-mappings
        // + volume-read fanout for each one would do O(N²) NAudio COM calls on the
        // UI thread and fire N half-built routing pushes. Each programmatic caller
        // is responsible for invoking PushMappingsToEngine + RefreshAssignedVolumes
        // itself, once, after its loop finishes.
        if (_isLoading) return;

        // 走到这里说明是用户自己在改下拉框（程序化赋值都在 _isLoading 下进行），
        // 于是这台显示器的映射从此算「手动选择」：摘掉「自动」徽章，
        // 之后的自动匹配不会再覆盖它。
        vm.IsAutoMatched = false;
        vm.MatchReason = string.Empty;

        PushMappingsToEngine();
        SaveSettings();
        RefreshAssignedVolumes();
    }

    /// <summary>
    /// Reads the current master volume from each monitor's assigned audio device
    /// and syncs it to the monitor's <c>Volume</c> slider. Runs on the UI thread;
    /// each call makes one NAudio COM round-trip per assigned device, so callers
    /// should run it once after a batch of selection changes rather than per-change.
    /// </summary>
    private void RefreshAssignedVolumes()
    {
        foreach (var m in Monitors)
        {
            if (m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            {
                var vol = _audioDeviceService.GetDeviceVolume(m.SelectedAudioDevice.Id);
                m.Volume = (int)Math.Round(vol * 100);
            }
        }
    }

    private void OnVolumeChanged(MonitorMappingViewModel vm)
    {
        if (vm.SelectedAudioDevice == null || string.IsNullOrEmpty(vm.SelectedAudioDevice.Id))
            return;

        _audioDeviceService.SetDeviceVolume(vm.SelectedAudioDevice.Id, vm.Volume / 100f);
    }

    private void PushMappingsToEngine()
    {
        var mappings = Monitors
            .Where(m => m.SelectedAudioDevice != null && !string.IsNullOrEmpty(m.SelectedAudioDevice.Id))
            .Select(m => new MonitorAudioMapping
            {
                MonitorDeviceName = m.Monitor.DeviceName,
                AudioDeviceId = m.SelectedAudioDevice!.Id
            })
            .ToList();

        _routingEngine.UpdateMappings(mappings);
        UpdateMissingDeviceFlags();
    }

    /// <summary>
    /// 标出「本来指定了设备、但该设备现在不可用」的显示器。
    /// 这类映射会被 <see cref="PushMappingsToEngine"/> 过滤掉（设备不存在就没法路由），
    /// 以前界面上完全看不出来 —— 用户只会觉得「拖窗口没反应」。
    /// </summary>
    private void UpdateMissingDeviceFlags()
    {
        foreach (var m in Monitors)
        {
            m.IsDeviceMissing = !string.IsNullOrEmpty(m.DesiredAudioDeviceId)
                                && (m.SelectedAudioDevice == null || string.IsNullOrEmpty(m.SelectedAudioDevice.Id));
        }
    }

    private void PushOverridesToEngine()
    {
        // 引擎需要知道**全部**分配（含本次运行临时分配的），所以这里不过滤 IsRemembered：
        // 「记入清单」只决定落不落盘，不决定当下生效与否。
        var dict = AppOverrides.ToDictionary(
            o => o.ProcessName,
            o => o.AudioDeviceId,
            StringComparer.OrdinalIgnoreCase);
        _routingEngine.UpdateAppOverrides(dict);
    }

    // ── 按应用分配扬声器 + 清单 ──────────────────────────────────────────
    //
    // 三层优先级（引擎里的顺序也一样）：
    //   1. 清单：某个程序被分配了一台扬声器 → 窗口拖到哪台屏都不影响；
    //   2. 自动：没有分配的程序 → 按窗口所在显示器的映射；
    //   3. 系统默认：连映射都没有 → 不动它，跟随系统默认设备。

    /// <summary>在清单里找某个进程名的条目（大小写不敏感）。</summary>
    private AppOverrideViewModel? FindOverride(string processName)
        => string.IsNullOrWhiteSpace(processName)
            ? null
            : AppOverrides.FirstOrDefault(o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>设备 ID → 当前能显示的名字（设备不在列表里时给占位名）。</summary>
    private string DeviceNameOf(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return string.Empty;
        return AudioDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))?.FriendlyName
               ?? DeviceNotConnectedName;
    }

    /// <summary>
    /// 重建所有扬声器下拉框的选项集合。
    /// <para>
    /// 设备实例按 ID 缓存复用，所以刷新设备列表（拔插 USB 声卡等）时已经选中的项不会被打断；
    /// 清单里指定了、但此刻不在设备列表里的设备会补一个占位项 —— 否则下拉框找不到选中项就会
    /// 显示成空白，用户会以为分配被清掉了。
    /// </para>
    /// </summary>
    private void RefreshAssignmentOptions()
    {
        var wanted = new List<AudioDeviceInfo> { _autoAssignmentOption };
        var speakers = new List<AudioDeviceInfo>();

        AudioDeviceInfo Take(string deviceId, string friendlyName)
        {
            if (!_assignmentOptionCache.TryGetValue(deviceId, out var item))
            {
                item = new AudioDeviceInfo { Id = deviceId };
                _assignmentOptionCache[deviceId] = item;
            }
            item.FriendlyName = friendlyName;
            return item;
        }

        foreach (var device in AudioDevices)
        {
            if (string.IsNullOrEmpty(device.Id)) continue;
            var option = Take(device.Id, device.FriendlyName);
            wanted.Add(option);
            speakers.Add(option);
        }

        // 清单里失联的设备：留占位项，等它插回来名字会自动恢复
        foreach (var ovr in AppOverrides)
        {
            if (string.IsNullOrEmpty(ovr.AudioDeviceId)) continue;
            if (wanted.Any(d => string.Equals(d.Id, ovr.AudioDeviceId, StringComparison.OrdinalIgnoreCase))) continue;
            wanted.Add(Take(ovr.AudioDeviceId, ovr.AudioDeviceName));
        }

        SyncCollection(AssignmentOptions, wanted);
        SyncCollection(SpeakerChoices, speakers);

        if (NewAssignmentDevice == null || SpeakerChoices.All(d => !ReferenceEquals(d, NewAssignmentDevice)))
            NewAssignmentDevice = SpeakerChoices.FirstOrDefault(d => d.IsDefault) ?? SpeakerChoices.FirstOrDefault();
    }

    /// <summary>
    /// 就地合并集合：删掉多余项、补上缺的、按目标顺序移位。
    /// 用「就地」而不是先 Clear 再 Add，是为了不在刷新设备列表时把用户正打开的下拉框清空。
    /// </summary>
    private static void SyncCollection<T>(ObservableCollection<T> target, List<T> wanted)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var index = target.IndexOf(wanted[i]);
            if (index < 0)
                target.Insert(Math.Min(i, target.Count), wanted[i]);
            else if (index != i)
                target.Move(index, i);
        }
    }

    /// <summary>把会话行与清单行的下拉框/勾选框同步成实际状态（不触发用户操作事件）。</summary>
    private void SyncAssignmentViews()
    {
        foreach (var session in ActiveSessions)
            SyncSessionAssignment(session);
        foreach (var ovr in AppOverrides)
            SyncOverrideAssignment(ovr);
    }

    private void SyncSessionAssignment(SessionDisplayViewModel session)
    {
        var ovr = FindOverride(session.ProcessName);
        var option = ovr == null
            ? _autoAssignmentOption
            : AssignmentOptions.FirstOrDefault(d => string.Equals(d.Id, ovr.AudioDeviceId, StringComparison.OrdinalIgnoreCase))
              ?? _autoAssignmentOption;

        session.ApplyAssignmentState(option, ovr?.IsRemembered ?? false);
    }

    private void SyncOverrideAssignment(AppOverrideViewModel ovr)
    {
        var option = AssignmentOptions.FirstOrDefault(d => string.Equals(d.Id, ovr.AudioDeviceId, StringComparison.OrdinalIgnoreCase))
                     ?? _autoAssignmentOption;
        ovr.SuppressChange = true;
        try
        {
            ovr.SelectedDevice = option;
        }
        finally
        {
            ovr.SuppressChange = false;
        }
    }

    /// <summary>
    /// 给某个程序分配一台扬声器（并在需要时记入清单）。会替换该程序原有的分配，
    /// 立即落盘并推给引擎 —— 下一次路由（通常 &lt;200ms）就会生效。
    /// </summary>
    /// <param name="icon">
    /// 可选的程序图标。调用方如果已经拿到（首页会话行的右键/下拉框、清单页），
    /// 传进来可以立刻显示，不用等下一轮会话刷新。
    /// </param>
    /// <param name="remember">
    /// 是否记入清单（写进设置文件，下次启动沿用）。false 表示只对本次运行有效。
    /// </param>
    public void SetAppOverride(string processName, string deviceId, ImageSource? icon = null, bool remember = true)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(deviceId))
            return;

        var deviceName = DeviceNameOf(deviceId);
        var existing = FindOverride(processName);

        if (existing != null)
        {
            existing.ApplyState(deviceId, deviceName, remember, icon);
        }
        else
        {
            var entry = new AppOverrideViewModel(AssignmentOptions) { ProcessName = processName };
            entry.AssignmentChanged += OnOverrideAssignmentChanged;
            entry.RememberChanged += OnOverrideRememberChanged;
            entry.ApplyState(deviceId, deviceName, remember, icon);
            AppOverrides.Add(entry);
            existing = entry;
        }

        // 先补齐下拉框选项（可能是刚插上/刚拔掉的设备），再把各行选中项对齐
        RefreshAssignmentOptions();
        SyncAssignmentViews();

        PushOverridesToEngine();
        SaveSettings();

        AssignmentStatus = remember
            ? $"已把「{processName}」分配到「{deviceName}」并记入清单 —— 下次启动会沿用它（清单优先于自动分配）。"
            : $"已把「{processName}」分配到「{deviceName}」（未记入清单，仅本次运行有效）。";
    }

    /// <summary>
    /// 取消某个程序的分配。条目不存在时什么都不做。
    /// 之后它重新走「窗口所在显示器 → 设备」的自动分配。
    /// </summary>
    public void RemoveAppOverride(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var existing = FindOverride(processName);
        if (existing == null) return;

        AppOverrides.Remove(existing);
        RefreshAssignmentOptions();
        SyncAssignmentViews();
        PushOverridesToEngine();
        SaveSettings();

        AssignmentStatus = $"已取消「{existing.ProcessName}」的分配：之后按窗口所在显示器自动分配。";
    }

    /// <summary>把某条分配记入/移出清单（不改变它当下生效与否）。</summary>
    private void SetOverrideRemembered(AppOverrideViewModel entry, bool remember)
    {
        // 注意：调用方已经把这行的勾选状态改掉了，所以这里不能因为
        // 「状态一样」就提前返回 —— 那样既不会落盘，也不会给出任何提示。
        entry.ApplyState(entry.AudioDeviceId, entry.AudioDeviceName, remember);
        PushOverridesToEngine();
        SaveSettings();
        SyncAssignmentViews();

        AssignmentStatus = remember
            ? $"已把「{entry.ProcessName}」记入清单（扬声器：「{entry.AudioDeviceName}」），下次启动会沿用。"
            : $"已把「{entry.ProcessName}」移出清单：本次运行仍保持当前分配，重启 VideoTurner 后按自动分配处理。";
    }

    /// <summary>首页会话行：用户改了扬声器下拉框。</summary>
    private void OnSessionAssignmentChanged(SessionDisplayViewModel session)
    {
        if (_isLoading || session.SelectedAssignment == null) return;

        var option = session.SelectedAssignment;
        if (string.IsNullOrEmpty(option.Id))
        {
            // 选「自动」= 取消这个程序的分配，把出口交还给显示器映射
            if (FindOverride(session.ProcessName) != null)
                RemoveAppOverride(session.ProcessName);
            else
                AssignmentStatus = $"「{session.ProcessName}」本来就没有单独分配，按窗口所在显示器自动分配。";
            return;
        }

        // 首次给某个程序指定扬声器时按「记入清单」处理（分配一次、以后都算数）；
        // 只有用户明确取消过勾选（已有条目 IsRemembered=false）才保持「仅本次运行」。
        var existing = FindOverride(session.ProcessName);
        var remember = existing?.IsRemembered ?? true;

        SetAppOverride(session.ProcessName, option.Id, session.Icon, remember);
    }

    /// <summary>首页会话行：用户勾/取消了「记入清单」。</summary>
    private void OnSessionRememberChanged(SessionDisplayViewModel session)
    {
        if (_isLoading) return;

        var existing = FindOverride(session.ProcessName);
        if (session.IsRemembered)
        {
            // 还没有显式分配：把「此刻实际送达的那台设备」记下来，
            // 这样用户看到自动分配的结果满意时，一键就能钉住。
            var deviceId = string.IsNullOrEmpty(existing?.AudioDeviceId)
                ? session.EffectiveAudioDeviceId
                : existing!.AudioDeviceId;

            if (string.IsNullOrEmpty(deviceId))
            {
                SyncAssignmentViews(); // 勾选框弹回未勾
                AssignmentStatus = $"「{session.ProcessName}」目前没有可沿用的扬声器（既没有单独分配，也不在任何显示器映射上）" +
                                   "—— 先在右侧下拉框里选一台扬声器，再勾「记入清单」。";
                return;
            }

            if (existing == null)
                SetAppOverride(session.ProcessName, deviceId, session.Icon, remember: true);
            else
                SetOverrideRemembered(existing, true);
            return;
        }

        if (existing != null)
            SetOverrideRemembered(existing, false);
    }

    /// <summary>清单页：用户改了某条分配的扬声器。</summary>
    private void OnOverrideAssignmentChanged(AppOverrideViewModel entry)
    {
        if (_isLoading || entry.SelectedDevice == null) return;

        if (string.IsNullOrEmpty(entry.SelectedDevice.Id))
        {
            // 在清单页选「自动」= 这条分配不要了（等价于点删除）
            RemoveAppOverride(entry.ProcessName);
            return;
        }

        SetAppOverride(entry.ProcessName, entry.SelectedDevice.Id, entry.Icon, entry.IsRemembered);
    }

    /// <summary>清单页：用户勾/取消了某条分配的「记入清单」。</summary>
    private void OnOverrideRememberChanged(AppOverrideViewModel entry)
    {
        if (_isLoading) return;
        SetOverrideRemembered(entry, entry.IsRemembered);
    }

    /// <summary>
    /// 重新枚举正在运行的进程，填「加入清单」的候选下拉框。
    /// 清单页显示时会自动跑一次，也可以手动点「刷新」。
    /// </summary>
    [RelayCommand]
    private void RefreshRunningProcesses()
    {
        var previous = SelectedRunningProcess?.ProcessName;

        var names = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (string.Equals(name, SelfProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (NonAudioProcessNames.Contains(name)) continue;
                    if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                    names.Add(name);
                }
                catch { /* 进程可能在枚举途中退出 */ }
                finally { process.Dispose(); }
            }
        }
        catch { /* 拿不到进程列表就保留原列表 */ }

        names.Sort(StringComparer.OrdinalIgnoreCase);

        RunningProcesses.Clear();
        foreach (var name in names)
            RunningProcesses.Add(new ProcessChoiceViewModel(name));

        // 不预选第一项：默默给「列表里第一个进程」加一条清单分配太容易误操作，
        // 让用户自己在下拉框里挑。
        SelectedRunningProcess =
            RunningProcesses.FirstOrDefault(p => string.Equals(p.ProcessName, previous, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>清单页「加入清单」：把选中的程序分配到选中的扬声器，并记入清单。</summary>
    [RelayCommand]
    private void AddToChecklist()
    {
        var processName = SelectedRunningProcess?.ProcessName?.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            AssignmentStatus = "请先选择要分配的程序（点「刷新」可以重新枚举正在运行的程序）。";
            return;
        }

        var device = NewAssignmentDevice
                     ?? SpeakerChoices.FirstOrDefault(d => d.IsDefault)
                     ?? SpeakerChoices.FirstOrDefault();
        if (device == null || string.IsNullOrEmpty(device.Id))
        {
            AssignmentStatus = "当前没有可用的扬声器。";
            return;
        }

        SetAppOverride(processName, device.Id, GetProcessIconByName(processName), remember: true);
        AssignmentStatus = $"已把「{processName}」分配到「{device.FriendlyName}」并记入清单 —— " +
                           "它下次启动时（甚至在出声之前）就会落到这台设备上。";
    }

    /// <summary>按进程名取图标（「加入清单」用，此时还没有音频会话）。</summary>
    private ImageSource? GetProcessIconByName(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    var icon = GetProcessIcon((uint)process.Id);
                    if (icon != null) return icon;
                }
            }
        }
        catch { /* 取不到图标不影响功能 */ }

        return null;
    }

    /// <summary>
    /// Re-resolve the friendly name of every assignment against the current device list.
    /// Called after a hot-plug refresh so a previously-unplugged device
    /// starts showing its real name (and vice versa).
    /// </summary>
    private void RefreshAppOverrideDeviceNames()
    {
        foreach (var ovr in AppOverrides)
        {
            var name = AudioDevices.FirstOrDefault(d => d.Id == ovr.AudioDeviceId)?.FriendlyName;
            if (name == null)
            {
                ovr.AudioDeviceName = DeviceNotConnectedName;
                continue;
            }

            // 只改显示名，不要碰 IsRemembered/SelectedDevice（会触发用户操作事件）
            ovr.SuppressChange = true;
            try
            {
                ovr.AudioDeviceName = name;
            }
            finally
            {
                ovr.SuppressChange = false;
            }
        }
    }

    private void SaveSettings()
    {
        // 演练模式不落盘：避免用一次「只看界面」的启动覆盖掉用户真实的映射与开关状态
        if (_isDryRun) return;

        var settings = new AppSettings
        {
            IsEnabled = IsRoutingEnabled,
            AutoStartWithWindows = AutoStartWithWindows,
            MinimizeOnClose = MinimizeOnClose,
            StartMinimized = StartMinimized,
            AutoMatchOnStartup = AutoMatchOnStartup,
            ThemeMode = SelectedThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" },
            // Persist user intent (DesiredAudioDeviceId), not the live SelectedAudioDevice.
            // Otherwise a Save triggered while a device is temporarily unplugged
            // (e.g. the user toggles AutoStartWithWindows during a hot-unplug)
            // would write that mapping out of the file permanently.
            Mappings = Monitors
                .Where(m => !string.IsNullOrEmpty(m.DesiredAudioDeviceId))
                .Select(m => new MonitorAudioMapping
                {
                    MonitorDeviceName = m.Monitor.DeviceName,
                    AudioDeviceId = m.DesiredAudioDeviceId,
                    IsAutoMatched = m.IsAutoMatched
                })
                .ToList(),
            // 清单条目在设备拔掉时也留着（存的是设备 ID，不是「此刻在不在」）；
            // 只写勾了「记入清单」的条目 —— 本次运行临时分配的不落盘。
            AppOverrides = AppOverrides
                .Where(o => o.IsRemembered && !string.IsNullOrWhiteSpace(o.ProcessName)
                            && !string.IsNullOrWhiteSpace(o.AudioDeviceId))
                .ToDictionary(
                    o => o.ProcessName,
                    o => o.AudioDeviceId,
                    StringComparer.OrdinalIgnoreCase),
            // 虚拟通道
            VirtualMode = VirtualModeEnabled,
            VirtualSinkDeviceId = SelectedVirtualSink?.Id ?? _desiredVirtualSinkId,
            ManagedProcesses = ManagedProcesses.ToList()
        };

        _settingsService.Save(settings);
    }

    partial void OnIsRoutingEnabledChanged(bool value)
    {
        if (_isDryRun && value)
        {
            // 演练模式下不允许真正开始路由：把开关弹回去并说明原因
            _isLoading = true;
            IsRoutingEnabled = false;
            _isLoading = false;
            AutoMatchStatus = "演练模式（--dry-run）：不会改动任何程序的音频输出；请去掉该参数再启动。";
            return;
        }

        if (value)
        {
            PushMappingsToEngine();
            _routingEngine.Start();
        }
        else
        {
            _routingEngine.Stop();
            ActiveSessions.Clear();
        }

        if (!_isLoading)
            SaveSettings();
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (!_isLoading)
        {
            ApplyAutoStart(value);
            SaveSettings();
        }
    }

    partial void OnMinimizeOnCloseChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    partial void OnAutoMatchOnStartupChanged(bool value)
    {
        if (_isLoading) return;

        SaveSettings();

        // 刚打开开关就立刻跑一次，让用户马上看到匹配结果（关掉开关不清除已有映射）。
        if (value)
            RunAutoMatch(announce: true);
        else
            AutoMatchStatus = "已关闭启动时自动匹配。已有映射保持不变，可点「自动匹配」手动跑一次。";
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (!_isLoading)
            SaveSettings();
    }

    /// <summary>
    /// 切到「应用清单」页时刷新候选程序列表（进程随时会起起落落），
    /// 顺便把清单行的「运行中」徽章对齐一次。
    /// </summary>
    partial void OnCurrentPageChanged(int value)
    {
        if (value != 1) return;

        RefreshRunningProcesses();

        foreach (var ovr in AppOverrides)
            ovr.IsRunning = IsProcessRunning(ovr.ProcessName);
    }

    private void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch { }
    }

    // ── 自动匹配（按显示器型号 → 音频端点） ──────────────────────────────
    //
    // 匹配逻辑本身在 Services/AudioEndpointMatcher 里，这里只负责：
    //   * 何时触发（启动时按设置、用户点「自动匹配」按钮、拖完窗口刷新后补空缺）
    //   * 绝不覆盖用户手动选择过的映射
    //   * 把结果写进界面状态（AutoMatchStatus）供用户确认

    /// <summary>
    /// 按显示器型号自动匹配音频端点，只填空缺：
    /// 已有手动映射的显示器不动；曾经自动匹配过、但那个设备现在不在了的，重新匹配。
    /// </summary>
    /// <param name="announce">是否把结果写进界面提示（启动时静默，按钮点击时提示）。</param>
    /// <returns>本次新填/改写的映射条数。</returns>
    private int RunAutoMatch(bool announce)
    {
        var assigned = Monitors
            .Where(m => !string.IsNullOrEmpty(m.DesiredAudioDeviceId))
            // 曾经自动匹配过、但那个设备现在不在的显示器要重新匹配（例如换了线、端点重编号），
            // 手动选择过的则一律不动。
            .Where(m => !(m.IsAutoMatched && AudioDevices.All(d => d.Id != m.DesiredAudioDeviceId)))
            .ToDictionary(m => m.Monitor.DeviceName, m => m.DesiredAudioDeviceId);

        var suggestions = AudioEndpointMatcher.Suggest(
            Monitors.Select(m => m.Monitor),
            AudioDevices,
            assigned);

        if (suggestions.Count == 0)
        {
            if (announce)
            {
                AutoMatchStatus = Monitors.Count == 0
                    ? "没有检测到显示器。"
                    : "没有找到可以自动配对的显示器型号 —— 请在右侧下拉框里手动选择音频设备。";
            }
            return 0;
        }

        // 保留调用方原来的加载标记（LoadSettings 期间调用本方法时不能提前把 _isLoading 归零，
        // 否则后续的程序化赋值会触发保存/推送）
        var wasLoading = _isLoading;
        _isLoading = true;
        var applied = 0;
        try
        {
            foreach (var suggestion in suggestions)
            {
                var vm = Monitors.FirstOrDefault(m => m.Monitor.DeviceName == suggestion.MonitorDeviceName);
                if (vm == null) continue;

                var device = AudioDevices.FirstOrDefault(d => d.Id == suggestion.AudioDeviceId);
                if (device == null) continue;

                vm.DesiredAudioDeviceId = device.Id;
                vm.SelectedAudioDevice = device;
                vm.IsAutoMatched = true;
                vm.MatchReason = suggestion.Reason;
                applied++;
            }
        }
        finally
        {
            _isLoading = wasLoading;
        }

        if (applied > 0)
        {
            PushMappingsToEngine();
            SaveSettings();
            RefreshAssignedVolumes();
        }

        if (announce)
        {
            AutoMatchStatus = applied > 0
                ? $"已按显示器型号自动匹配 {applied} 台显示器；右侧下拉框可随时手动改，手动选择优先。"
                : "没有找到可以自动配对的显示器型号 —— 请在右侧下拉框里手动选择音频设备。";
        }

        return applied;
    }

    /// <summary>界面上「自动匹配」按钮的命令入口。</summary>
    [RelayCommand]
    private void AutoMatch()
    {
        RunAutoMatch(announce: true);
    }

    // ── 虚拟通道 ────────────────────────────────────────────────────────
    //
    // 架构：程序 → 静音虚拟声卡（永远不变）→ 我们按进程回环捕获每一路 →
    //       按「窗口所在显示器的映射」或「用户固定」渲染到真正的设备。
    // 好处：程序不需要重启（治「只读一次输出设备」），还能对每个程序单独控制出口。

    partial void OnVirtualModeEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (_isDryRun && value)
        {
            _isLoading = true;
            VirtualModeEnabled = false;
            _isLoading = false;
            VirtualModeStatus = "演练模式（--dry-run）：不会改动任何程序的音频输出。";
            return;
        }

        // 关掉虚拟模式：先把托管程序在虚拟声卡上的固定项还原，否则它们会继续把声音
        // 送进静音端点（我们又不再捕获）—— 表现就是「全哑了」。
        if (!value)
            ClearSinkPinsFor(ManagedProcesses);

        PushVirtualRouting();
        SaveSettings();
        UpdateVirtualModeStatus();
    }

    /// <summary>
    /// 把一批进程在虚拟声卡上的「持久化固定项」清掉（只影响它们**之后**创建的音频流）。
    ///
    /// <para>为什么必须清：一旦某个程序被固定在虚拟声卡上，而我们又不再捕获它
    /// （关掉虚拟模式、退出程序、把它移出托管列表），它的声音就会进静音端点 —— 等于哑了。
    /// 清掉固定项之后，它下次启动就会回到系统默认设备。</para>
    /// </summary>
    private void ClearSinkPinsFor(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in processes)
            {
                try
                {
                    if ((uint)process.Id == (uint)Environment.ProcessId) continue;
                    _routerService.TryClearProcessRouting((uint)process.Id);
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    partial void OnSelectedVirtualSinkChanged(AudioDeviceInfo? value)
    {
        if (value != null && !string.IsNullOrEmpty(value.Id))
            _desiredVirtualSinkId = value.Id;

        if (_isLoading) return;

        PushVirtualRouting();
        SaveSettings();
        UpdateVirtualModeStatus();
    }

    /// <summary>把虚拟模式设置推给路由引擎（开关、虚拟声卡、托管进程名单）。</summary>
    private void PushVirtualRouting()
    {
        _routingEngine.UpdateVirtualRouting(
            VirtualModeEnabled,
            SelectedVirtualSink?.Id,
            ManagedProcesses.ToList());
    }

    /// <summary>重新扫描虚拟声卡端点，并尽量保住用户原来的选择。</summary>
    [RelayCommand]
    private void RefreshVirtualSinks()
    {
        var previousId = SelectedVirtualSink?.Id ?? _desiredVirtualSinkId;

        VirtualSinks.Clear();
        foreach (var sink in VirtualSinkService.GetVirtualSinks(AudioDevices))
            VirtualSinks.Add(sink);

        // 用户以前选过的那台如果不在「名称看起来像虚拟声卡」的名单里（例如他自己
        // 选了一台没接音箱的物理端点来当静音出口），也要留在列表里，否则选择会被悄悄丢掉。
        if (!string.IsNullOrEmpty(previousId) && VirtualSinks.All(d => d.Id != previousId))
        {
            var manual = AudioDevices.FirstOrDefault(d => d.Id == previousId);
            if (manual != null) VirtualSinks.Add(manual);
        }

        var wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            SelectedVirtualSink =
                VirtualSinks.FirstOrDefault(d => d.Id == previousId)
                ?? VirtualSinks.FirstOrDefault();
        }
        finally
        {
            _isLoading = wasLoading;
        }

        UpdateVirtualModeStatus();
    }

    /// <summary>把当前正在出声的程序一键纳入虚拟通道（之后它们启动时就会被预置到虚拟声卡）。</summary>
    [RelayCommand]
    private void AddActiveSessionsToVirtual()
    {
        var added = 0;
        foreach (var session in ActiveSessions)
        {
            if (string.IsNullOrWhiteSpace(session.ProcessName)) continue;
            if (ManagedProcesses.Any(n => string.Equals(n, session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;

            ManagedProcesses.Add(session.ProcessName);
            added++;
        }

        if (added > 0)
        {
            PushVirtualRouting();
            SaveSettings();
        }

        UpdateVirtualModeStatus();
        AutoMatchStatus = added > 0
            ? $"已把 {added} 个正在出声的程序纳入虚拟通道。"
            : "当前没有新的程序可以纳入（列表里可能已经有了）。";
    }

    [RelayCommand]
    private void ClearManagedProcesses()
    {
        if (ManagedProcesses.Count == 0) return;

        ClearSinkPinsFor(ManagedProcesses);
        ManagedProcesses.Clear();
        PushVirtualRouting();
        SaveSettings();
        UpdateVirtualModeStatus();
        AutoMatchStatus = "已清空托管列表：这些程序下次启动会回到系统默认设备（当前正在播的那一路会继续走虚拟通道，直到它重启）。";
    }

    public void RemoveManagedProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var existing = ManagedProcesses.FirstOrDefault(
            n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        ClearSinkPinsFor(new[] { existing });
        ManagedProcesses.Remove(existing);
        PushVirtualRouting();
        SaveSettings();
        UpdateVirtualModeStatus();
    }

    /// <summary>路由引擎自动发现并纳管了某个进程：持久化下来，下次启动就能预置。</summary>
    private void OnManagedProcessDiscovered(string processName)
    {
        if (_disposed || string.IsNullOrWhiteSpace(processName)) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (ManagedProcesses.Any(n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)))
                return;

            ManagedProcesses.Add(processName);
            SaveSettings();
            UpdateVirtualModeStatus();
        });
    }

    private void UpdateVirtualModeStatus()
    {
        if (!VirtualModeEnabled)
        {
            VirtualModeStatus = "虚拟通道已关闭：程序直接播到「窗口所在显示器」映射的设备；" +
                                "只在启动时读一次输出设备的程序需要重启才能跟上切换。";
            return;
        }

        if (VirtualSinks.Count == 0)
        {
            VirtualModeStatus = "未检测到虚拟声卡。需要一台「静音虚拟输出端点」（VB-Cable / VoiceMeeter 等）" +
                                "作为程序统一的落点；装好后点「重新扫描」。没有它，虚拟通道不会生效。";
            return;
        }

        if (SelectedVirtualSink == null)
        {
            VirtualModeStatus = "请选择一台虚拟声卡作为虚拟通道的出口。";
            return;
        }

        VirtualModeStatus =
            $"虚拟通道已开启：程序先落到「{SelectedVirtualSink.FriendlyName}」（静音），" +
            $"再由本程序按窗口所在显示器送到真正的音箱；已托管 {ManagedProcesses.Count} 个程序。" +
            "首次把某个程序纳入时，它需要重启一次（之后永不重启）。";
    }


    /// <summary>
    /// 设置页「诊断」按钮：把当前状态一次性抓成一段可复制的报告（同时写进 routing.log）。
    /// 内容包括：未公开接口的自检结果（含 HRESULT）、显示器与映射、活动会话及其决策、
    /// 最近一次路由状态。目标是一份报告就能定位「为什么没生效」。
    /// </summary>
    [RelayCommand]
    private void RunDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"进程：VideoTurner PID {Environment.ProcessId}，{(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine($"系统：{Environment.OSVersion}（Build {Environment.OSVersion.Version.Build}）");
        sb.AppendLine($"模式：{(_isDryRun ? "演练模式 --dry-run（不写设置、不启用路由）" : "正常运行")}");
        sb.AppendLine();

        sb.AppendLine("--- 1) 未公开接口自检 ---");
        try
        {
            using var router = new AudioRouterService();
            var (_, report) = router.SelfTest(writeTest: !_isDryRun);
            sb.Append(report);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"自检本身出错：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- 2) 显示器 {Monitors.Count} 台 / 音频设备 {DeviceCount} 个 ---");
        foreach (var m in Monitors)
        {
            var device = m.SelectedAudioDevice == null || string.IsNullOrEmpty(m.SelectedAudioDevice.Id)
                ? "(未映射 → 跟随系统默认)"
                : m.SelectedAudioDevice.FriendlyName;
            sb.AppendLine($"  {m.Monitor.DeviceName}  {m.Monitor.FriendlyName}  →  {device}" +
                          (m.IsAutoMatched ? "  [自动匹配]" : ""));
        }

        sb.AppendLine();
        sb.AppendLine($"--- 3) 应用清单 / 按应用分配 {AppOverrides.Count} 条 ---");
        if (AppOverrides.Count == 0)
            sb.AppendLine("  （清单为空：所有程序都按窗口所在显示器自动分配）");
        foreach (var ovr in AppOverrides)
        {
            sb.AppendLine($"  {ovr.ProcessName}  →  {ovr.AudioDeviceName}" +
                          $"  [{(ovr.IsRemembered ? "记入清单" : "仅本次运行")}]" +
                          $"{(ovr.IsRunning ? "  运行中" : string.Empty)}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- 4) 活动音频会话 {ActiveSessions.Count} 个 ---");
        if (ActiveSessions.Count == 0)
            sb.AppendLine("  （当前没有程序在出声；播放一点音频再点一次自检）");
        foreach (var s in ActiveSessions)
        {
            sb.AppendLine($"  {s.ProcessName} (PID {s.ProcessId})");
            sb.AppendLine($"      窗口所在显示器: {(string.IsNullOrEmpty(s.MonitorName) ? "未识别" : s.MonitorName)}");
            sb.AppendLine($"      当前输出设备  : {s.AudioDeviceName}{(s.IsOverridden ? "  [按清单分配]" : "")}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- 5) 虚拟通道（{(VirtualModeEnabled ? "已开启" : "未开启")}）---");
        if (!VirtualModeEnabled)
        {
            sb.AppendLine("  （未启用：程序直接播到物理设备）");
        }
        else
        {
            sb.AppendLine($"  虚拟声卡: {(SelectedVirtualSink == null ? "(未选择)" : SelectedVirtualSink.FriendlyName)}");
            sb.AppendLine($"  托管进程: {(ManagedProcesses.Count == 0 ? "(无)" : string.Join(", ", ManagedProcesses))}");
            var channels = _routingEngine.DescribeVirtualChannels();
            if (channels.Count == 0)
                sb.AppendLine("  当前没有活动通道（托管的程序没在出声，或还没重启进虚拟通道）");
            foreach (var line in channels)
                sb.AppendLine("  " + line);
        }

        sb.AppendLine();
        sb.AppendLine("--- 6) 最近一次路由状态 ---");
        sb.AppendLine($"  {(string.IsNullOrEmpty(LastRoutingStatus) ? "(还没有跑过路由循环)" : LastRoutingStatus)}");
        sb.AppendLine();
        sb.AppendLine($"日志文件：{RoutingLog.LogPath}");

        DiagnosticsReport = sb.ToString();
        RoutingLog.Info("界面自检报告：\n" + DiagnosticsReport);
    }

    /// <summary>打开日志所在目录并选中日志文件。</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{RoutingLog.LogPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了 */ }
    }

    /// <summary>
    /// 依次在每台音频设备上播一声提示音，用来快速分辨「设备本身不出声」和「路由没生效」。
    /// 播放放到后台线程，避免界面卡住。
    /// </summary>
    [RelayCommand]
    private async Task TestDevices()
    {
        var devices = AudioDevices.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();
        if (devices.Count == 0)
        {
            AutoMatchStatus = "没有可试听的音频设备。";
            return;
        }

        foreach (var device in devices)
        {
            LastRoutingStatus = $"正在试听：{device.FriendlyName} …（听是哪一台在响）";
            await Task.Run(() => DeviceTonePlayer.Play(device.Id, device.FriendlyName));
            await Task.Delay(150);
        }

        LastRoutingStatus = $"试听结束（依次播过 {devices.Count} 台设备）。"
                            + "哪台没响，就是那台设备本身没有声音输出 —— 别把映射指过去。";
        RoutingLog.Status(LastRoutingStatus);
    }

    /// <summary>供托盘菜单调用：启用/暂停路由，立即生效并落盘。</summary>
    public void ToggleRouting()
    {
        IsRoutingEnabled = !IsRoutingEnabled;
    }

    [RelayCommand]
    private void Refresh()
    {
        var wasRunning = IsRoutingEnabled;
        if (wasRunning)
            _routingEngine.Stop();

        // Snapshot intent (DesiredAudioDeviceId) rather than current resolved
        // selection — a missing device's mapping should survive a manual
        // refresh the same way it survives a hot-plug refresh.
        var desiredByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.DesiredAudioDeviceId);
        // 「这条映射是自动匹配来的」也要一起留下来：重建 VM 列表后若丢掉这个标记，
        // 用户手选的映射会被下一次自动匹配误认成空缺而覆盖。
        var autoFlagByMonitor = Monitors.ToDictionary(
            m => m.Monitor.DeviceName,
            m => m.IsAutoMatched);

        LoadDevicesAndMonitors();

        _isLoading = true;
        try
        {
            foreach (var monitorVm in Monitors)
            {
                if (!desiredByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var desiredId))
                    continue;

                // Re-seed the desired ID first — LoadDevicesAndMonitors built
                // brand-new MonitorMappingViewModel instances with empty defaults.
                monitorVm.DesiredAudioDeviceId = desiredId;
                if (autoFlagByMonitor.TryGetValue(monitorVm.Monitor.DeviceName, out var wasAuto))
                    monitorVm.IsAutoMatched = wasAuto;
                monitorVm.SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == desiredId);
            }

            // OnMappingChanged is suppressed under _isLoading, so read volumes
            // explicitly — sliders would otherwise show their default of 100.
            RefreshAssignedVolumes();
        }
        finally
        {
            _isLoading = false;
        }

        // Pin device names are derived from AudioDevices — refresh them after
        // the device list rebuild so the assignment page reflects the new state.
        RefreshAppOverrideDeviceNames();
        RefreshAssignmentOptions();
        SyncAssignmentViews();

        // 手动刷新后同样给空着的显示器补一次自动匹配（开关关掉时不动）。
        if (AutoMatchOnStartup)
            RunAutoMatch(announce: false);

        if (wasRunning)
        {
            PushMappingsToEngine();
            PushOverridesToEngine();
            _routingEngine.Start();
            _isLoading = true;
            IsRoutingEnabled = true;
            _isLoading = false;
        }
    }

    private ImageSource? GetProcessIcon(uint processId)
    {
        if (_iconCache.TryGetValue(processId, out var cached))
            return cached;

        ImageSource? icon = null;
        try
        {
            var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            if (path != null)
            {
                using var drawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (drawingIcon != null)
                {
                    icon = Imaging.CreateBitmapSourceFromHIcon(
                        drawingIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    icon.Freeze();
                }
            }
        }
        catch { }

        _iconCache[processId] = icon;
        return icon;
    }

    private void OnSessionsUpdated(List<AudioSessionInfo> sessions)
    {
        var sessionData = sessions.Select(s => new
        {
            Session = s,
            Icon = GetProcessIcon(s.ProcessId)
        }).ToList();

        _dispatcher.BeginInvoke(() =>
        {
            // 把路由引擎刚写下的状态同步到界面（首页状态行），
            // 这样「拖了窗口没反应」时用户能直接看到原因，而不用去翻日志。
            LastRoutingStatus = RoutingLog.LastStatus;

            // 与会话行**就地**对齐（按 PID），而不是先 Clear 再加一遍：
            // 每次路由周期都会推一次会话列表（心跳 30 秒一次，拖窗口/切前台更频繁），
            // 整表重建会把用户正打开着的行内下拉框/勾选框直接掀掉。
            var seen = new HashSet<uint>();
            foreach (var item in sessionData)
            {
                seen.Add(item.Session.ProcessId);

                var row = ActiveSessions.FirstOrDefault(s => s.ProcessId == item.Session.ProcessId);
                if (row == null)
                {
                    row = new SessionDisplayViewModel(AssignmentOptions) { ProcessId = item.Session.ProcessId };
                    row.AssignmentChanged += OnSessionAssignmentChanged;
                    row.RememberChanged += OnSessionRememberChanged;
                    ActiveSessions.Add(row);
                }

                row.ProcessName = item.Session.ProcessName;
                row.MonitorName = item.Session.MonitorFriendlyName;
                row.AudioDeviceName = item.Session.AudioDeviceName;
                row.EffectiveAudioDeviceId = item.Session.AudioDeviceId;
                row.IsOverridden = item.Session.IsOverridden;
                if (item.Icon != null) row.Icon = item.Icon;

                SyncSessionAssignment(row);
            }

            for (var i = ActiveSessions.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(ActiveSessions[i].ProcessId))
                    ActiveSessions.RemoveAt(i);
            }

            // Back-fill icons on assignments loaded from disk: when a listed app
            // first starts playing audio we now have a PID to extract its icon from,
            // so the assignment page switches from the pin-glyph fallback to the real
            // app icon. Matches are case-insensitive; first live session wins.
            // 顺带标出「这个程序现在是否在运行」。
            foreach (var ovr in AppOverrides)
            {
                var match = sessionData.FirstOrDefault(s =>
                    string.Equals(s.Session.ProcessName, ovr.ProcessName, StringComparison.OrdinalIgnoreCase));

                ovr.IsRunning = match != null || IsProcessRunning(ovr.ProcessName);

                if (ovr.Icon != null) continue;
                var iconMatch = sessionData.FirstOrDefault(s =>
                    string.Equals(s.Session.ProcessName, ovr.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && s.Icon != null);
                if (iconMatch != null)
                    ovr.Icon = iconMatch.Icon;
            }
        });
    }

    /// <summary>进程是否在运行（比会话列表更宽：还没出声的程序也算）。</summary>
    private static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        try
        {
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length > 0; }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe + dispose the notifier BEFORE stopping the timer so any
        // late COM-thread callback that was already queued observes _disposed
        // and bails instead of kicking the timer from a background thread.
        _deviceNotifier.DevicesChanged -= OnDeviceChangeDetected;
        _deviceNotifier.DefaultDeviceChanged -= OnDeviceChangeDetected;
        _deviceNotifier.Dispose();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tick -= OnDeviceRefreshTimerTick;

        _routingEngine.SessionsUpdated -= OnSessionsUpdated;

        // Reset all audio routing back to system defaults
        _routingEngine.ResetAllRouting();

        _routingEngine.Dispose();
        _routerService.Dispose();
        _audioDeviceService.Dispose();
    }
}
