using System.Runtime.InteropServices;
using VideoTurner.Interop;
using IAudioCaptureClientRaw = VideoTurner.Interop.WasapiInterop.IAudioCaptureClientRaw;
using IAudioClientRaw = VideoTurner.Interop.WasapiInterop.IAudioClientRaw;

namespace VideoTurner.Services;

/// <summary>
/// 抓取「某一个进程（含其子进程树）正在播放的音频」。
///
/// <para>用的是 Windows 10 2004+ 的 <b>Process Loopback</b>：
/// <c>ActivateAudioInterfaceAsync</c> + <c>VAD\Process_Loopback</c> +
/// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>。
/// 它和「设备回环」的区别很关键：<b>只抓指定进程树的声音，并且与这个进程把声音送到哪台设备无关</b>。
/// 所以只要让程序把声音送进一个静音虚拟声卡（见 VirtualChannelEngine），
/// 我们就能拿到它干净的一路音频，而用户听不到那份原始输出。</para>
///
/// <para>格式处理：捕获端固定请求一种格式（48 kHz / 立体声 / float32），
/// 并带上 <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY</c>，
/// 让 Windows 自己从虚拟声卡的混音格式转过来。这样目标设备怎么换都不用重采样。</para>
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly IAudioClientRaw _client;
    private readonly IAudioCaptureClientRaw _capture;
    private readonly Thread _thread;
    private readonly byte[] _packet = new byte[64 * 1024];
    private volatile bool _running = true;

    public uint ProcessId { get; }
    public AudioFormat Format { get; }
    public bool IsRunning => _running;

    /// <summary>最近一次峰值（0..1），用于判断「这一路到底有没有声音」。</summary>
    public double PeakLevel { get; private set; }

    /// <summary>累计捕获到的字节数。</summary>
    public long TotalBytes { get; private set; }

    /// <summary>每块音频（块大小不固定，通常 10–20 ms）。</summary>
    public event Action<byte[], int>? DataAvailable;

    private ProcessLoopbackCapture(uint processId, IAudioClientRaw client,
        IAudioCaptureClientRaw capture, AudioFormat format)
    {
        ProcessId = processId;
        _client = client;
        _capture = capture;
        Format = format;

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = $"ProcessLoopback-{processId}" };
        _thread.Start();
    }

    /// <summary>
    /// 开始捕获某个进程。失败时抛异常（带具体 HRESULT），调用方写日志即可。
    /// </summary>
    public static ProcessLoopbackCapture Start(uint processId)
    {
        var candidates = new[]
        {
            new AudioFormat(48000, 2, 32, true),
            new AudioFormat(44100, 2, 32, true),
            new AudioFormat(48000, 2, 16, false),
            new AudioFormat(44100, 2, 16, false)
        };

        var errors = new List<string>();

        // 第一轮带 AUTOCONVERTPCM（Windows 负责转格式），失败再试不带标志的裸格式。
        foreach (var autoConvert in new[] { true, false })
        {
            foreach (var format in candidates)
            {
                var flags = WasapiInterop.AUDCLNT_STREAMFLAGS_LOOPBACK;
                if (autoConvert)
                    flags |= WasapiInterop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                             | WasapiInterop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

                IAudioClientRaw? client = null;
                try
                {
                    client = Activate(processId);

                    var formatPtr = format.AllocNative();
                    try
                    {
                        // 20 ms 缓冲，周期 0 = 由引擎决定
                        var hr = client.Initialize(
                            WasapiInterop.AUDCLNT_SHAREMODE_SHARED, flags,
                            200_000, 0, formatPtr, IntPtr.Zero);

                        if (hr < 0)
                        {
                            errors.Add($"{format}（{(autoConvert ? "autoconvert" : "raw")}）= 0x{hr:X8}");
                            continue;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(formatPtr);
                    }

                    var iid = WasapiInterop.IID_IAudioCaptureClient;
                    var serviceHr = client.GetService(ref iid, out var service);
                    Marshal.ThrowExceptionForHR(serviceHr);
                    var capture = (IAudioCaptureClientRaw)service;

                    var startHr = client.Start();
                    Marshal.ThrowExceptionForHR(startHr);

                    return new ProcessLoopbackCapture(processId, client, capture, format);
                }
                catch (Exception ex)
                {
                    errors.Add($"{format}（{(autoConvert ? "autoconvert" : "raw")}）= {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    if (client != null) Marshal.ReleaseComObject(client);
                }
            }
        }

        throw new InvalidOperationException(
            $"进程回环捕获启动失败（PID {processId}）：{string.Join("；", errors)}");
    }

    /// <summary>激活一个绑定到指定 PID 的进程回环 IAudioClient。</summary>
    private static IAudioClientRaw Activate(uint processId)
    {
        // PROPVARIANT 里存的是指向 AUDIOCLIENT_ACTIVATION_PARAMS 的指针，
        // 只有调用期间需要有效，激活完成后立即释放。
        var propVariant = WasapiInterop.CreateProcessLoopbackPropVariant(processId, out var blob);
        try
        {
            return WasapiInterop.ActivateAudioClient(
                WasapiInterop.ProcessLoopbackDevicePath, propVariant);
        }
        finally
        {
            WasapiInterop.FreeActivationParams(propVariant, blob);
        }
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            try
            {
                var hr = _capture.GetNextPacketSize(out var packetFrames);
                if (hr < 0) break;

                bool got = false;
                while (packetFrames > 0 && _running)
                {
                    hr = _capture.GetBuffer(out var data, out var frames, out var flags,
                        out _, out _);
                    if (hr < 0) break;

                    int bytes = (int)frames * Format.BlockAlign;
                    if (bytes > 0)
                    {
                        if (_packet.Length < bytes)
                        {
                            // 正常不会到这里（包大小由引擎决定）；真遇到就丢弃这一包
                            _capture.ReleaseBuffer(frames);
                        }
                        else
                        {
                            if ((flags & WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                                Array.Clear(_packet, 0, bytes);
                            else
                                Marshal.Copy(data, _packet, 0, bytes);

                            _capture.ReleaseBuffer(frames);

                            TotalBytes += bytes;
                            PeakLevel = ComputePeak(_packet, bytes, Format);
                            try { DataAvailable?.Invoke(_packet, bytes); }
                            catch { /* 消费者异常不能弄死捕获线程 */ }
                        }
                    }
                    else
                    {
                        _capture.ReleaseBuffer(frames);
                    }

                    got = true;
                    if (_capture.GetNextPacketSize(out packetFrames) < 0) break;
                }

                if (!got) Thread.Sleep(4);
            }
            catch
            {
                // 设备失效 / 进程退出 —— 结束捕获，由上层决定是否重建
                break;
            }
        }
    }

    private static double ComputePeak(byte[] buffer, int bytes, AudioFormat format)
    {
        double peak = 0;
        if (format.IsFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytes; i += 4)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                double magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytes; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                double magnitude = Math.Abs(sample / 32768.0);
                if (magnitude > peak) peak = magnitude;
            }
        }

        return peak;
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;

        try { _thread.Join(500); } catch { }
        try { _client.Stop(); } catch { }
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
    }
}
