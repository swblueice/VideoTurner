using VideoTurner.Interop;

namespace VideoTurner.Services;

/// <summary>
/// 一路「虚拟通道」：把一个进程的音频捕获下来，实时送进它当前该去的那个物理设备。
///
/// <para>生命周期：捕获端（跟着进程）在通道创建时建立，直到进程退出都不变；
/// 目标设备变了只重建渲染端 —— 所以窗口在两台显示器之间来回拖时，
/// 声音只是「换个出口」，正在播放的音频不会断，程序也不会察觉。</para>
///
/// <para>缓冲：200 ms 环形缓冲。捕获与渲染由两个时钟驱动，长期会漂移；
/// 写满就丢最旧的数据（宁可丢几毫秒也不让延迟累积），读空由渲染端补静音。</para>
/// </summary>
public sealed class ProcessChannel : IDisposable
{
    private readonly ProcessLoopbackCapture _capture;
    private readonly AudioRingBuffer _ring;
    private readonly object _gate = new();
    private WasapiDeviceRenderer? _renderer;

    public uint ProcessId { get; }
    public string ProcessName { get; }
    public string TargetDeviceId { get; private set; }
    public string TargetDeviceName { get; private set; } = string.Empty;
    public AudioFormat Format => _capture.Format;
    public string? LastError { get; private set; }

    public long CapturedBytes => _capture.TotalBytes;
    public double PeakLevel => _capture.PeakLevel;
    public long DroppedBytes => _ring.DroppedBytes;
    public int Underruns
    {
        get { lock (_gate) return _renderer?.Underruns ?? 0; }
    }

    /// <summary>渲染端是否在跑（设备被拔掉 / 被独占时会停）。</summary>
    public bool IsRendering
    {
        get { lock (_gate) return _renderer?.IsRunning == true; }
    }

    public ProcessChannel(uint processId, string processName, string targetDeviceId, string targetDeviceName)
    {
        ProcessId = processId;
        ProcessName = processName;
        TargetDeviceId = targetDeviceId;
        TargetDeviceName = targetDeviceName;

        _capture = ProcessLoopbackCapture.Start(processId);
        _ring = new AudioRingBuffer(_capture.Format.AverageBytesPerSecond / 5); // ≈200 ms
        _capture.DataAvailable += (buffer, count) => _ring.Write(buffer, count);

        StartRenderer(targetDeviceId);
    }

    /// <summary>换一个出口设备。捕获端与缓冲保持不变（进程毫无感知）。</summary>
    public void SwitchTarget(string deviceId, string deviceName)
    {
        lock (_gate)
        {
            if (string.Equals(deviceId, TargetDeviceId, StringComparison.OrdinalIgnoreCase)
                && _renderer?.IsRunning == true)
                return;

            TargetDeviceId = deviceId;
            TargetDeviceName = deviceName;
        }

        StartRenderer(deviceId);
    }

    private void StartRenderer(string deviceId)
    {
        WasapiDeviceRenderer? old;
        lock (_gate) { old = _renderer; _renderer = null; }
        old?.Dispose();

        try
        {
            var renderer = WasapiDeviceRenderer.Start(deviceId, _capture.Format, _ring);
            renderer.Stopped += reason => LastError = reason;
            lock (_gate) { _renderer = renderer; }
        }
        catch (Exception ex)
        {
            LastError = $"渲染端启动失败：0x{ex.HResult:X8} {ex.Message}";
            RoutingLog.Warn($"虚拟通道 {ProcessName} (PID {ProcessId}) {LastError}");
        }
    }

    /// <summary>给界面/诊断用的一行摘要。</summary>
    public string Describe()
        => $"{ProcessName} (PID {ProcessId}) → {TargetDeviceName}" +
           $"；捕获 {CapturedBytes / (double)Format.AverageBytesPerSecond:0.0}s，峰值 {PeakLevel:0.000}" +
           $"{(IsRendering ? "" : "（渲染端已停：" + (LastError ?? "未知") + "）")}";

    public void Dispose()
    {
        WasapiDeviceRenderer? renderer;
        lock (_gate) { renderer = _renderer; _renderer = null; }

        renderer?.Dispose();
        _capture.Dispose();
    }
}
