using VideoTurner.Interop;
using VideoTurner.Models;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace VideoTurner.Services;

/// <summary>
/// Drives audio routing in response to signals:
///   * window moved / resized / foreground changed  (<see cref="WinEventHook"/>)
///   * new audio session created                    (<see cref="AudioSessionNotifier"/>)
///   * display configuration changed                (<see cref="SystemEvents.DisplaySettingsChanged"/>)
///   * periodic heartbeat                           (safety net every <see cref="HeartbeatInterval"/>)
///
/// All signals call <see cref="Trigger"/>, which releases a semaphore. A single
/// background task consumes the semaphore, debounces rapid bursts, and runs one
/// <see cref="PerformRoutingCycle"/> per batch — so dragging a window to a new
/// monitor results in exactly one routing pass ~<see cref="DebounceMs"/>ms after
/// the drag ends, rather than up to a full polling interval of latency.
///
/// Compared to the old 1000ms polling loop this reduces idle CPU wake-ups ~30x
/// (one every 30s vs every 1s) and drops worst-case routing latency from ~1s to
/// effectively the debounce window.
/// </summary>
public class RoutingEngine : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AudioSessionService _sessionService;
    private readonly AudioRouterService _routerService;

    // Coalesces bursts of events into a single routing cycle.
    private const int DebounceMs = 150;

    // Safety-net interval: also re-evaluates even if no events fire. Catches edge
    // cases (programmatic window moves, devices that don't raise session-created
    // events, missed hooks) without burning CPU.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _workTask;
    private SemaphoreSlim? _signal;

    private WinEventHook? _foregroundHook;
    private WinEventHook? _moveResizeHook;
    private AudioSessionNotifier? _sessionNotifier;

    // Track last-routed device per PID to avoid redundant COM calls
    private readonly ConcurrentDictionary<uint, string> _lastRoutedDevice = new();

    // In-memory mappings pushed from the ViewModel — no disk reads in the hot loop
    private volatile Dictionary<string, string> _mappings = new();

    // 清单分配（按应用指定扬声器）：进程名（忽略大小写，不含 .exe）→ 音频设备 ID。
    // 优先于显示器映射检查，所以分配过的程序不管窗口在哪台屏都留在自己那台设备上。
    // volatile + 整个字典替换 = 工作循环总能拿到一份一致快照，不需要加锁。
    private volatile Dictionary<string, string> _appOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    // Device name lookup cached per cycle
    private Dictionary<string, string> _deviceNameCache = new();

    // ── 虚拟通道（无驱动进程级路由）──────────────────────────────────────
    //
    // 一部分程序（游戏、模拟器客户端、部分播放器）只在启动时读一次输出设备，
    // 之后怎么改策略都不搬。虚拟模式把它们统一钉到一台「静音虚拟声卡」上，
    // 再用进程回环捕获每一路单独送进目标设备 —— 程序永远不用重启，
    // 窗口拖到哪台显示器只影响下游的出口。
    private readonly VirtualChannelEngine _virtualChannels = new();
    private volatile bool _virtualEnabled;
    private volatile string? _sinkDeviceId;
    private readonly ConcurrentDictionary<string, byte> _managedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, byte> _sinkPinned = new();

    /// <summary>某个进程第一次被纳入虚拟通道时触发（界面据此持久化托管列表）。</summary>
    public event Action<string>? ManagedProcessDiscovered;

    public bool VirtualModeEnabled => _virtualEnabled;

    public string? VirtualSinkDeviceId => _sinkDeviceId;

    // 已经提示过「策略设了但程序没搬过去」的 PID（每个进程只提示一次，避免刷屏）
    private readonly HashSet<uint> _mismatchWarned = new();

    // 清单分配看门狗：有些程序（游戏、模拟器客户端）
    // 只在启动时读一次输出设备，等它出声再去设策略已经晚了。
    // 所以只要清单里有分配，就每秒扫一次这些进程，进程一出现就把策略设好。
    private System.Threading.Timer? _pinWatchdog;
    private static readonly TimeSpan PinWatchdogInterval = TimeSpan.FromSeconds(1);

    public bool IsRunning => _workTask != null && !_workTask.IsCompleted;

    public event Action<List<AudioSessionInfo>>? SessionsUpdated;

    public RoutingEngine(
        MonitorService monitorService,
        AudioSessionService sessionService,
        AudioRouterService routerService)
    {
        _monitorService = monitorService;
        _sessionService = sessionService;
        _routerService = routerService;
    }

    public void UpdateMappings(List<MonitorAudioMapping> mappings)
    {
        _mappings = mappings.ToDictionary(m => m.MonitorDeviceName, m => m.AudioDeviceId);
        // Mappings changed — re-evaluate immediately so already-running sessions
        // get routed without waiting for the next user action.
        Trigger();
    }

    /// <summary>
    /// Replaces the 清单（按应用分配）. Triggers an immediate routing pass so
    /// a newly-added assignment takes effect without the user having to interact with the
    /// app, and a removed one allows the monitor mapping to take over right away.
    /// </summary>
    public void UpdateAppOverrides(IDictionary<string, string> overrides)
    {
        _appOverrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        UpdatePinWatchdog();
        Trigger();
    }

    /// <summary>
    /// 虚拟通道模式的开关、虚拟声卡端点、托管进程名单。界面改设置时调用。
    /// 关掉虚拟模式会立刻拆掉所有通道（声音恢复正常直连）。
    /// </summary>
    public void UpdateVirtualRouting(bool enabled, string? sinkDeviceId, IEnumerable<string>? managedProcesses)
    {
        _virtualEnabled = enabled;
        _sinkDeviceId = string.IsNullOrWhiteSpace(sinkDeviceId) ? null : sinkDeviceId;

        if (managedProcesses != null)
        {
            _managedNames.Clear();
            foreach (var name in managedProcesses)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    _managedNames[name.Trim()] = 1;
            }
        }

        if (!enabled)
        {
            _virtualChannels.Update(null, Array.Empty<ChannelRequest>());
            _sinkPinned.Clear();
        }

        UpdatePinWatchdog();
        Trigger();
    }

    /// <summary>当前每一路虚拟通道的摘要（诊断报告用）。</summary>
    public List<string> DescribeVirtualChannels() => _virtualChannels.Describe();

    /// <summary>
    /// 清单里有分配时开启每秒一次的扫描，进程一出现就先把它固定到目标设备 ——
    /// 专门对付「只在启动时读一次输出设备」的程序（游戏 / 模拟器客户端）。
    /// 清单为空且没开虚拟通道时不做任何事，几乎零开销。
    /// </summary>
    private void UpdatePinWatchdog()
    {
        // 注意：虚拟模式的「预置」不在这里做 —— 实测 SetPersistedDefaultAudioEndpoint
        // 对一个还没有音频会话的进程会返回 E_INVALIDARG(0x80070057)，
        // 所以只能在它出声之后设。好在策略是**按可执行文件持久化**的：
        // 设过一次之后，这个程序以后每次启动都会自己落在虚拟声卡上。
        if (_appOverrides.Count == 0 && !_virtualEnabled)
        {
            _pinWatchdog?.Dispose();
            _pinWatchdog = null;
            return;
        }

        _pinWatchdog ??= new System.Threading.Timer(_ => ApplyPinsToRunningProcesses(),
            null, PinWatchdogInterval, PinWatchdogInterval);
    }

    /// <summary>
    /// 把每个清单分配应用到「当前正在运行的、名字匹配的进程」上。
    /// 与 <see cref="PerformRoutingCycle"/> 的区别：不依赖音频会话 ——
    /// 进程刚启动、还没出声时就先把策略设好。
    /// </summary>
    private void ApplyPinsToRunningProcesses()
    {
        try
        {
            // 虚拟模式下「清单分配」表达的是目标设备，不是要设给进程的策略 ——
            // 进程的策略必须一律指向虚拟声卡，否则声音会从物理设备漏出来。
            if (!_virtualEnabled)
                ApplyDevicePins();
        }
        catch (Exception ex)
        {
            RoutingLog.Error($"清单分配看门狗异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyDevicePins()
    {
        var overrides = _appOverrides;
        if (overrides.Count == 0) return;

        foreach (var (processName, deviceId) in overrides)
        {
            if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrEmpty(deviceId)) continue;

            // 清单里那台设备此刻不在（拔了 / 关了端点）：别白试着调用，
            // 也不能把它设成一个不存在的设备 —— 等它回来，看门狗会自动补上。
            if (!IsDeviceUsable(deviceId)) continue;

            System.Diagnostics.Process[] processes;
            try { processes = System.Diagnostics.Process.GetProcessesByName(processName); }
            catch { continue; }

            foreach (var process in processes)
            {
                try
                {
                    var pid = (uint)process.Id;
                    if (_lastRoutedDevice.TryGetValue(pid, out var already) && already == deviceId)
                        continue; // 已经设过了

                    if (_routerService.TryRouteProcessToDevice(pid, deviceId, out var error))
                    {
                        _lastRoutedDevice[pid] = deviceId;
                        RoutingLog.Status($"{processName} (PID {pid}) 一启动就按清单分配到「{DeviceNameOf(deviceId)}」✅");
                    }
                    else
                    {
                        RoutingLog.Warn($"清单分配预置失败 {processName} (PID {pid})：{error}");
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private string DeviceNameOf(string deviceId)
        => _deviceNameCache.TryGetValue(deviceId, out var name) ? name : deviceId;

    /// <summary>
    /// 设备此刻是否还路由得过去。以本轮枚举到的端点表为准。
    /// 如果这轮枚举失败（表为空），就乐观地当作可用 —— 宁可试一次，
    /// 也不要因为一次枚举失败把清单里所有分配都停掉。
    /// </summary>
    private bool IsDeviceUsable(string deviceId)
        => _deviceNameCache.Count == 0 || _deviceNameCache.ContainsKey(deviceId);

    /// <summary>
    /// Starts the engine. Must be called from a thread with a message pump
    /// (the WPF UI thread) because <see cref="WinEventHook"/> needs one to
    /// receive out-of-context callbacks.
    /// </summary>
    /// <remarks>
    /// Construction builds everything into locals first so a failure partway
    /// through (e.g. <c>SetWinEventHook</c> returning NULL, or the audio
    /// service being unavailable) can unwind cleanly without leaving hooks
    /// registered in the OS or half-initialized fields hanging around for the
    /// next <c>Start()</c> call to overwrite.
    /// </remarks>
    public void Start()
    {
        if (IsRunning) return;

        var cts = new CancellationTokenSource();
        var signal = new SemaphoreSlim(initialCount: 0);

        // All signal sources release the same semaphore. Captured as a local
        // closure so the callbacks don't depend on the eventual field value
        // (which Stop() nulls out — we don't want callbacks firing on a
        // replaced or nulled field in the middle of a restart).
        Action release = () => { try { signal.Release(); } catch { /* disposed */ } };

        WinEventHook? moveResizeHook = null;
        WinEventHook? foregroundHook = null;
        AudioSessionNotifier? sessionNotifier = null;

        try
        {
            // Window-move / snap / maximize-restore end — THE "user dragged this to another monitor" signal.
            moveResizeHook = new WinEventHook(
                WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
                WinEventHook.EVENT_SYSTEM_MOVESIZEEND,
                release);

            // Foreground change — covers alt-tab and apps that move their own windows programmatically.
            // Debouncing in the work loop keeps this cheap even though FOREGROUND can fire frequently.
            foregroundHook = new WinEventHook(
                WinEventHook.EVENT_SYSTEM_FOREGROUND,
                WinEventHook.EVENT_SYSTEM_FOREGROUND,
                release);

            sessionNotifier = new AudioSessionNotifier(release);
            sessionNotifier.Start();
        }
        catch
        {
            // Roll back anything that was created before the failure.
            sessionNotifier?.Dispose();
            foregroundHook?.Dispose();
            moveResizeHook?.Dispose();
            signal.Dispose();
            cts.Dispose();
            throw;
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Pass signal + token by value so the loop doesn't read instance fields
        // that Stop() might mutate concurrently.
        var workTask = Task.Run(() => WorkLoop(signal, cts.Token));

        // Commit to instance state only after everything succeeded.
        _cts = cts;
        _signal = signal;
        _moveResizeHook = moveResizeHook;
        _foregroundHook = foregroundHook;
        _sessionNotifier = sessionNotifier;
        _workTask = workTask;

        // 清单里有分配时开启「进程一出现就先设策略」的看门狗
        UpdatePinWatchdog();

        // Kick off one immediate pass so the UI populates without waiting for an event.
        release();
    }

    /// <summary>
    /// Stops the engine and waits briefly for the background loop to exit
    /// before disposing shared state. Safe to call repeatedly; safe to call
    /// concurrently with a subsequent <see cref="Start"/> (the old loop observes
    /// cancellation and exits against its own captured semaphore, not the new one).
    /// </summary>
    public void Stop()
    {
        // Snapshot current state then null the fields FIRST, so any concurrent
        // Trigger()/Start() sees "no engine running" and operates on its own
        // fresh resources rather than racing on the ones we're about to dispose.
        var cts = _cts;
        var signal = _signal;
        var workTask = _workTask;
        var moveResizeHook = _moveResizeHook;
        var foregroundHook = _foregroundHook;
        var sessionNotifier = _sessionNotifier;

        _cts = null;
        _signal = null;
        _workTask = null;
        _moveResizeHook = null;
        _foregroundHook = null;
        _sessionNotifier = null;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _pinWatchdog?.Dispose();
        _pinWatchdog = null;
        _mismatchWarned.Clear();

        // Unhook OS / NAudio callbacks before we tear down the semaphore they
        // write to — prevents a late callback hitting a disposed semaphore.
        foregroundHook?.Dispose();
        moveResizeHook?.Dispose();
        sessionNotifier?.Dispose();

        try { cts?.Cancel(); } catch { /* disposed */ }
        // Wake the loop so it observes cancellation immediately instead of
        // sitting in WaitAsync for the full heartbeat interval.
        try { signal?.Release(); } catch { /* disposed */ }

        // Wait for the loop to actually exit before disposing what it's reading.
        // Bounded: if something wedges, we log and move on rather than hang forever.
        if (workTask != null)
        {
            try
            {
                if (!workTask.Wait(TimeSpan.FromSeconds(3)))
                    System.Diagnostics.Debug.WriteLine("RoutingEngine: work loop did not exit within 3s");
            }
            catch { /* task faulted — we own cleanup anyway */ }
        }

        signal?.Dispose();
        cts?.Dispose();

        _lastRoutedDevice.Clear();
    }

    /// <summary>
    /// Wakes the work loop. Safe to call from any thread and from native hook callbacks.
    /// Non-blocking; never throws.
    /// </summary>
    public void Trigger()
    {
        try { _signal?.Release(); } catch { /* disposed */ }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitor layout changed — invalidate our cached monitor list and re-route.
        _monitorService.InvalidateCache();
        Trigger();
    }

    // Takes signal + token as parameters (rather than reading the fields) so that
    // Stop() can safely null the fields without the loop dereferencing them
    // concurrently. The loop always operates on the exact semaphore/CTS it was
    // launched with — any overlap with a subsequent Start() is harmless.
    private async Task WorkLoop(SemaphoreSlim signal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for a signal OR the heartbeat timeout, whichever comes first.
                bool signaled = await signal.WaitAsync(HeartbeatInterval, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                if (signaled)
                {
                    // Debounce: absorb any follow-up signals that arrive in the next DebounceMs
                    // so a burst of events (drag end + foreground change + session created) runs
                    // the cycle exactly once.
                    await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
                    while (await signal.WaitAsync(0, ct).ConfigureAwait(false)) { }
                }

                PerformRoutingCycle();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Routing cycle error: {ex.Message}");
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void PerformRoutingCycle()
    {
        var mappings = _mappings;
        var overrides = _appOverrides;
        var virtualEnabled = _virtualEnabled;
        var sinkId = _sinkDeviceId;

        var sessions = _sessionService.GetActiveSessions();

        // Refresh device name cache（顺带记住「系统默认设备」，虚拟模式下没有映射时回落到它）
        var defaultDeviceId = string.Empty;
        try
        {
            using var deviceService = new AudioDeviceService();
            var devices = deviceService.GetOutputDevices();
            _deviceNameCache = devices.ToDictionary(d => d.Id, d => d.FriendlyName);
            defaultDeviceId = devices.FirstOrDefault(d => d.IsDefault)?.Id ?? string.Empty;
        }
        catch { /* keep previous cache */ }

        // 每个周期记一行摘要：没有这条，「到底有没有跑过路由循环」都无从判断
        RoutingLog.Info($"路由周期：会话 {sessions.Count} 个，映射 {mappings.Count} 条，清单分配 {overrides.Count} 条" +
                        (virtualEnabled ? $"，虚拟通道 {_virtualChannels.ChannelCount} 路" : string.Empty));

        // Clean up stale PIDs
        var activePids = new HashSet<uint>(sessions.Select(s => s.ProcessId));
        foreach (var pid in _lastRoutedDevice.Keys)
        {
            if (!activePids.Contains(pid))
                _lastRoutedDevice.TryRemove(pid, out _);
        }
        foreach (var pid in _sinkPinned.Keys)
        {
            if (!activePids.Contains(pid))
                _sinkPinned.TryRemove(pid, out _);
        }

        var virtualRequests = new List<ChannelRequest>();
        var sinkName = !string.IsNullOrEmpty(sinkId) && _deviceNameCache.TryGetValue(sinkId, out var sn)
            ? sn
            : sinkId ?? string.Empty;

        foreach (var session in sessions)
        {
            try
            {
                // Default to "not overridden" — flipped on below if a pin matches.
                // Reset every cycle so removing a pin clears the indicator on the
                // very next pass even if the session is still alive.
                session.IsOverridden = false;

                // Always detect which monitor the process is on
                var monitor = _monitorService.GetMonitorForProcess(session.ProcessId);
                if (monitor != null)
                {
                    session.MonitorDeviceName = monitor.DeviceName;
                    session.MonitorFriendlyName = monitor.FriendlyName;
                }

                // Show current audio device name
                if (_deviceNameCache.TryGetValue(session.AudioDeviceId, out var currentDeviceName))
                {
                    session.AudioDeviceName = currentDeviceName;
                }

                // Resolve the target device. 清单（按应用分配）优先于显示器映射 ——
                // 分配过的程序固定从它那台设备出声，窗口拖到哪台屏都不受影响。
                // 但清单里那台设备此刻不可用（拔了、关了、端点未激活）时不能死等：
                // 回落到显示器映射/系统默认，等设备接回来再自动切回去。
                string? targetDeviceId = null;
                var ruleSource = string.Empty;
                if (!string.IsNullOrEmpty(session.ProcessName) &&
                    overrides.TryGetValue(session.ProcessName, out var pinnedDeviceId) &&
                    !string.IsNullOrEmpty(pinnedDeviceId))
                {
                    if (IsDeviceUsable(pinnedDeviceId))
                    {
                        targetDeviceId = pinnedDeviceId;
                        session.IsOverridden = true;
                        ruleSource = "清单分配";
                    }
                    else
                    {
                        RoutingLog.Info(
                            $"  {session.ProcessName}：清单里指定的「{DeviceNameOf(pinnedDeviceId)}」当前不可用，" +
                            "本次按显示器映射/系统默认分配");
                    }
                }

                if (string.IsNullOrEmpty(targetDeviceId) && monitor != null && mappings.Count > 0 &&
                    mappings.TryGetValue(monitor.DeviceName, out var monitorTarget) &&
                    !string.IsNullOrEmpty(monitorTarget))
                {
                    targetDeviceId = monitorTarget;
                    ruleSource = "显示器映射";
                }

                // ── 虚拟通道模式：程序已经在虚拟声卡上，声音由我们这一路下游决定 ──
                if (virtualEnabled && !string.IsNullOrEmpty(sinkId))
                {
                    HandleVirtualSession(session, targetDeviceId, ruleSource, sinkId, sinkName,
                        defaultDeviceId, virtualRequests);
                    continue;
                }

                if (string.IsNullOrEmpty(targetDeviceId))
                {
                    // No rule applies right now. If we previously routed this PID
                    // (pin removed, monitor mapping cleared, window moved to a
                    // monitor without a mapping, …), undo our prior per-app
                    // policy so Windows falls back to the system default. Without
                    // this, the stale override set by the pin would persist after
                    // unpinning and audio would stay stuck on the old device.
                    // TryRemove-first means we only clear once per transition —
                    // subsequent heartbeat cycles find no cache entry and skip.
                    RoutingLog.Info(
                        $"  {session.ProcessName} (PID {session.ProcessId}) 不路由：" +
                        (monitor == null
                            ? "找不到该进程的可见窗口。" + MonitorService.DescribeProcess(session.ProcessId)
                            : $"窗口在 {monitor.FriendlyName} ({monitor.DeviceName})，" +
                              (mappings.Count == 0 ? "当前没有任何映射" : "该显示器没有映射")));

                    if (_lastRoutedDevice.TryRemove(session.ProcessId, out _))
                    {
                        if (_routerService.TryClearProcessRouting(session.ProcessId, out var clearError))
                            RoutingLog.Status($"{session.ProcessName}：已取消分配，恢复系统默认设备");
                        else
                            RoutingLog.Status($"{session.ProcessName}：取消分配失败（{clearError}）");
                    }
                    continue;
                }

                // Update display to show the target device
                if (_deviceNameCache.TryGetValue(targetDeviceId, out var targetDeviceName))
                {
                    // 保留真实播放设备，用于下面的「策略设了但没搬」判断
                    session.ActualAudioDeviceId = session.AudioDeviceId;
                    session.AudioDeviceId = targetDeviceId;
                    session.AudioDeviceName = targetDeviceName;
                }

                // Skip if already routed to this device
                if (_lastRoutedDevice.TryGetValue(session.ProcessId, out var lastDevice) && lastDevice == targetDeviceId)
                {
                    // 策略已经设过了。但「设过」不等于「声音真的搬过去了」：
                    // 游戏 / 模拟器客户端只在启动时读一次输出设备，
                    // 运行中改策略不会迁移它已经在播的音频流。
                    // 这里对比「该会话当前实际播放的设备」，不一致就明确提示一次。
                    var actualDeviceId = session.ActualAudioDeviceId;
                    if (!string.IsNullOrEmpty(actualDeviceId)
                        && !string.Equals(actualDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase)
                        && _mismatchWarned.Add(session.ProcessId))
                    {
                        var currentName = _deviceNameCache.TryGetValue(actualDeviceId, out var cn) ? cn : actualDeviceId;
                        RoutingLog.Status(
                            $"{session.ProcessName}：已把输出设备设为「{DeviceNameOf(targetDeviceId)}」，" +
                            $"但它当前仍播在「{currentName}」—— 这类程序只在启动时读取一次输出设备，" +
                            "重启该程序即可生效（想以后免重启，可在首页给它分配扬声器并勾「记入清单」）");
                    }
                    else
                    {
                        RoutingLog.Info($"  {session.ProcessName} (PID {session.ProcessId}) 已是目标设备，跳过");
                    }
                    continue;
                }

                var targetName = _deviceNameCache.TryGetValue(targetDeviceId, out var tn) ? tn : targetDeviceId;
                if (_routerService.TryRouteProcessToDevice(session.ProcessId, targetDeviceId, out var error))
                {
                    _lastRoutedDevice[session.ProcessId] = targetDeviceId;
                    RoutingLog.Status($"{session.ProcessName} → {targetName}（{ruleSource}）✅");
                }
                else
                {
                    // 这条是排查「拖了窗口没反应」最关键的一行：失败原因（含 HRESULT）会出现在界面和日志里
                    var hint = string.Empty;
                    if (MonitorService.IsProcessElevated(session.ProcessId) == true && !IsSelfElevated())
                    {
                        hint = "；目标程序以管理员身份运行 —— 需要把 VideoTurner 也以管理员身份启动才能给它设设备";
                    }
                    RoutingLog.Status($"{session.ProcessName} → {targetName}（{ruleSource}）❌ {error}{hint}");
                }
            }
            catch (Exception ex)
            {
                RoutingLog.Error($"处理 PID {session.ProcessId} 时异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // 虚拟通道：把「本周期期望的通道」推给引擎（多了建、少了拆、目标变了只换出口）
        _virtualChannels.Update(
            virtualEnabled && !string.IsNullOrEmpty(sinkId) ? sinkId : null,
            virtualRequests);

        SessionsUpdated?.Invoke(sessions);
    }

    /// <summary>
    /// 虚拟模式下单个会话的处理。
    ///
    /// <para>两步：</para>
    /// <list type="number">
    ///   <item>程序还在直接播到物理设备 → 把它的输出设备指向虚拟声卡，并提示重启一次
    ///     （之后它的每一次启动都由看门狗在出声前设好，永远落在虚拟声卡上）；</item>
    ///   <item>已经在虚拟声卡上 → 算出它此刻该去的那台设备，登记为一路通道。</item>
    /// </list>
    ///
    /// <para>目标解析：清单分配（按应用） &gt; 显示器映射 &gt; 系统默认设备。
    /// 最后这一条是刻意的：虚拟模式下程序的声音不能「没有出口」，否则就等于静音。</para>
    /// </summary>
    private void HandleVirtualSession(
        AudioSessionInfo session,
        string? targetDeviceId,
        string ruleSource,
        string sinkDeviceId,
        string sinkName,
        string defaultDeviceId,
        List<ChannelRequest> requests)
    {
        var effectiveTarget = targetDeviceId;
        if (string.IsNullOrEmpty(effectiveTarget))
        {
            effectiveTarget = defaultDeviceId;
            ruleSource = "系统默认设备（该显示器没有映射）";
        }

        if (string.IsNullOrEmpty(effectiveTarget))
        {
            RoutingLog.Info($"  {session.ProcessName} 不路由：当前没有任何可用输出设备");
            return;
        }

        // 1) 还没落到虚拟声卡上
        if (!string.Equals(session.AudioDeviceId, sinkDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            if (!_sinkPinned.ContainsKey(session.ProcessId))
            {
                if (_routerService.TryRouteProcessToDevice(session.ProcessId, sinkDeviceId, out var error))
                {
                    _sinkPinned[session.ProcessId] = 1;
                    _lastRoutedDevice.TryRemove(session.ProcessId, out _);
                    RegisterManagedProcess(session.ProcessName);

                    RoutingLog.Status(
                        $"{session.ProcessName}：已指向虚拟通道「{sinkName}」，" +
                        $"但它当前仍直接播在「{session.AudioDeviceName}」—— " +
                        "重启该程序后就会走虚拟通道（此后拖窗口不用再重启）");
                }
                else
                {
                    RoutingLog.Status($"{session.ProcessName}：指向虚拟通道失败 {error}");
                }
            }
            return;
        }

        // 2) 已经在虚拟声卡上 —— 登记这一路
        _sinkPinned[session.ProcessId] = 1;
        RegisterManagedProcess(session.ProcessName);

        var targetName = _deviceNameCache.TryGetValue(effectiveTarget, out var tn) ? tn : effectiveTarget;

        // 界面上显示「实际会从哪台设备出声」，而不是虚拟声卡
        session.AudioDeviceId = effectiveTarget;
        session.AudioDeviceName = targetName;

        requests.Add(new ChannelRequest(
            session.ProcessId, session.ProcessName, effectiveTarget, targetName));

        RoutingLog.Info($"  {session.ProcessName} (PID {session.ProcessId}) 虚拟通道 → {targetName}（{ruleSource}）");
    }

    /// <summary>第一次见到某个进程被纳入虚拟通道时，记下它的名字并通知界面持久化。</summary>
    private void RegisterManagedProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        if (!_managedNames.TryAdd(processName, 1)) return;

        try { ManagedProcessDiscovered?.Invoke(processName); } catch { }
    }

    /// <summary>
    /// Resets all routed processes back to their system default audio device.
    /// Call this when the app is closing so audio doesn't stay stuck on a non-default device.
    /// </summary>
    public void ResetAllRouting()
    {
        // 虚拟通道：退出前拆掉所有通道，并把托管进程的「钉到虚拟声卡」清掉 ——
        // 否则本程序没在跑的时候，那些程序会把声音送进静音虚拟声卡，等于全哑。
        var sink = _sinkDeviceId;
        if (_virtualEnabled && !string.IsNullOrEmpty(sink))
        {
            foreach (var name in _managedNames.Keys)
            {
                System.Diagnostics.Process[] processes;
                try { processes = System.Diagnostics.Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var process in processes)
                {
                    try
                    {
                        if ((uint)process.Id == (uint)Environment.ProcessId) continue;
                        _routerService.TryClearProcessRouting((uint)process.Id);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
        }

        _virtualChannels.Dispose();
        _sinkPinned.Clear();

        foreach (var pid in _lastRoutedDevice.Keys.ToList())
        {
            try
            {
                _routerService.TryClearProcessRouting(pid);
            }
            catch
            {
                // Process may have already exited
            }
        }
        _lastRoutedDevice.Clear();
    }

    public void Dispose()
    {
        // Stop() disposes CTS + signal itself now, so this is just a one-liner.
        Stop();
    }

    /// <summary>本进程自己是否以管理员运行（用来判断提权提示要不要附加）。</summary>
    private static bool IsSelfElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
