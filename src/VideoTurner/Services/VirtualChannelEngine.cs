namespace VideoTurner.Services;

/// <summary>路由引擎算出来的「这个进程现在应该从哪台设备出声」。</summary>
public readonly record struct ChannelRequest(
    uint ProcessId,
    string ProcessName,
    string TargetDeviceId,
    string TargetDeviceName);

/// <summary>
/// 虚拟通道引擎：维护「进程 → 一路捕获 → 一个目标设备」的集合。
///
/// <para>它解决的是路由引擎解决不了的那半边问题：路由引擎只能设「某个进程的默认输出设备」，
/// 而有些程序启动时只读一次设备、之后再也不理系统策略。把这类程序统一钉到一个
/// <b>静音虚拟声卡</b>上之后，它们就永远「读」到同一个设备；真正去哪台音箱，
/// 由这里每一路的下游渲染决定，随时可换、程序无感。</para>
///
/// <para>线程模型：<see cref="Update"/> 在路由引擎的后台线程上调用；
/// 创建/销毁通道会 join 捕获与渲染线程，所以这些动作都在锁外做。</para>
/// </summary>
public sealed class VirtualChannelEngine : IDisposable
{
    private readonly Dictionary<uint, ProcessChannel> _channels = new();
    private readonly object _gate = new();
    private string? _sinkDeviceId;

    public string? SinkDeviceId
    {
        get { lock (_gate) return _sinkDeviceId; }
    }

    public int ChannelCount
    {
        get { lock (_gate) return _channels.Count; }
    }

    /// <summary>
    /// 用「期望状态」更新通道集合：多了就建、少了就拆、目标变了就只换出口。
    /// 幂等，可以每个路由周期调一次。
    /// </summary>
    public void Update(string? sinkDeviceId, IReadOnlyList<ChannelRequest> requests)
    {
        // 虚拟声卡换了（或关掉了虚拟模式）→ 所有通道作废
        var stale = new List<ProcessChannel>();
        bool sinkChanged;
        lock (_gate)
        {
            sinkChanged = !string.Equals(_sinkDeviceId, sinkDeviceId, StringComparison.OrdinalIgnoreCase);
            if (sinkChanged)
            {
                stale.AddRange(_channels.Values);
                _channels.Clear();
                _sinkDeviceId = sinkDeviceId;
            }
        }

        foreach (var channel in stale)
        {
            RoutingLog.Info($"虚拟通道关闭：{channel.ProcessName} (PID {channel.ProcessId})");
            channel.Dispose();
        }

        if (sinkChanged && sinkDeviceId == null)
            return;

        var wanted = new Dictionary<uint, ChannelRequest>();
        foreach (var request in requests)
        {
            if (request.ProcessId == 0 || request.ProcessId == (uint)Environment.ProcessId) continue;
            wanted[request.ProcessId] = request; // 同一 PID 多会话时取第一条
        }

        // 1) 拆掉不再需要的
        var toRemove = new List<ProcessChannel>();
        lock (_gate)
        {
            foreach (var pid in _channels.Keys.ToList())
            {
                if (wanted.ContainsKey(pid)) continue;
                toRemove.Add(_channels[pid]);
                _channels.Remove(pid);
            }
        }
        foreach (var channel in toRemove)
        {
            RoutingLog.Info($"虚拟通道结束：{channel.ProcessName} (PID {channel.ProcessId})");
            channel.Dispose();
        }

        // 2) 建新的 / 换出口
        foreach (var request in wanted.Values)
        {
            ProcessChannel? existing;
            lock (_gate) _channels.TryGetValue(request.ProcessId, out existing);

            if (existing == null)
            {
                try
                {
                    var channel = new ProcessChannel(
                        request.ProcessId, request.ProcessName,
                        request.TargetDeviceId, request.TargetDeviceName);
                    lock (_gate) _channels[request.ProcessId] = channel;

                    RoutingLog.Status(
                        $"虚拟通道建立：{request.ProcessName} (PID {request.ProcessId}) → {request.TargetDeviceName}" +
                        $"（捕获 {channel.Format}）");
                }
                catch (Exception ex)
                {
                    RoutingLog.Warn(
                        $"虚拟通道建立失败 {request.ProcessName} (PID {request.ProcessId})：" +
                        $"0x{ex.HResult:X8} {ex.Message}");
                }
                continue;
            }

            if (!string.Equals(existing.TargetDeviceId, request.TargetDeviceId, StringComparison.OrdinalIgnoreCase)
                || !existing.IsRendering)
            {
                existing.SwitchTarget(request.TargetDeviceId, request.TargetDeviceName);
                RoutingLog.Status(
                    $"虚拟通道换出口：{request.ProcessName} (PID {request.ProcessId}) → {request.TargetDeviceName}");
            }
        }
    }

    /// <summary>当前所有通道的一行一条摘要（诊断报告用）。</summary>
    public List<string> Describe()
    {
        List<ProcessChannel> snapshot;
        lock (_gate) snapshot = _channels.Values.ToList();
        return snapshot.Select(c => c.Describe()).ToList();
    }

    public void Dispose()
    {
        List<ProcessChannel> all;
        lock (_gate)
        {
            all = _channels.Values.ToList();
            _channels.Clear();
        }

        foreach (var channel in all)
        {
            try { channel.Dispose(); } catch { }
        }
    }
}
