using System.Runtime.InteropServices;
using VideoTurner.Interop;
using IAudioClientRaw = VideoTurner.Interop.WasapiInterop.IAudioClientRaw;
using IAudioRenderClientRaw = VideoTurner.Interop.WasapiInterop.IAudioRenderClientRaw;

namespace VideoTurner.Services;

/// <summary>
/// 把一路 PCM 音频送到某个输出端点（共享模式）。
///
/// <para>关键点：Initialize 时带 <c>AUTOCONVERTPCM | SRC_DEFAULT_QUALITY</c>，
/// 所以不管目标设备是 44.1 kHz 还是 48 kHz、16 bit 还是 float，Windows 都在引擎里转，
/// 我们这一侧永远用同一种格式 —— 换设备时只要重建这个渲染端，捕获端与缓冲都不用动。</para>
///
/// <para>写循环是 10 ms 轮询（共享模式共享缓冲），缓冲深度由 <see cref="AudioRingBuffer"/>
/// 控制（默认约 200 ms）。读空时补静音并记一次欠载，读满时由环形缓冲丢弃最旧数据，
/// 这样两个时钟长期漂移也不会让延迟越积越多。</para>
/// </summary>
public sealed class WasapiDeviceRenderer : IDisposable
{
    private readonly IAudioClientRaw _client;
    private readonly IAudioRenderClientRaw _render;
    private readonly AudioFormat _format;
    private readonly AudioRingBuffer _ring;
    private readonly uint _bufferFrames;
    private readonly byte[] _chunk;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public string DeviceId { get; }
    public AudioFormat Format => _format;
    public bool IsRunning => _running;
    public long WrittenBytes { get; private set; }
    public int Underruns { get; private set; }

    /// <summary>渲染线程异常退出时触发（设备被拔、被独占等）。</summary>
    public event Action<string>? Stopped;

    private WasapiDeviceRenderer(string deviceId, IAudioClientRaw client,
        IAudioRenderClientRaw render, AudioFormat format, AudioRingBuffer ring, uint bufferFrames)
    {
        DeviceId = deviceId;
        _client = client;
        _render = render;
        _format = format;
        _ring = ring;
        _bufferFrames = bufferFrames;
        _chunk = new byte[(int)bufferFrames * format.BlockAlign];

        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "WasapiRenderer" };
        _thread.Start();
    }

    public static WasapiDeviceRenderer Start(string deviceId, AudioFormat format, AudioRingBuffer ring)
    {
        var client = WasapiInterop.ActivateAudioClient(
            WasapiInterop.ToDeviceInterfacePath(deviceId), IntPtr.Zero);

        try
        {
            var formatPtr = format.AllocNative();
            try
            {
                // 100 ms 端点缓冲；周期 0 = 由音频引擎决定
                var hr = client.Initialize(
                    WasapiInterop.AUDCLNT_SHAREMODE_SHARED,
                    WasapiInterop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | WasapiInterop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    1_000_000, 0, formatPtr, IntPtr.Zero);

                if (hr < 0)
                    throw new InvalidOperationException(
                        $"渲染端 Initialize 失败：0x{hr:X8}（格式 {format}，设备 {deviceId}）");
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            var sizeHr = client.GetBufferSize(out var frames);
            Marshal.ThrowExceptionForHR(sizeHr);

            var iid = WasapiInterop.IID_IAudioRenderClient;
            var serviceHr = client.GetService(ref iid, out var service);
            Marshal.ThrowExceptionForHR(serviceHr);
            var render = (IAudioRenderClientRaw)service;

            var startHr = client.Start();
            Marshal.ThrowExceptionForHR(startHr);

            return new WasapiDeviceRenderer(deviceId, client, render, format, ring, frames);
        }
        catch
        {
            try { Marshal.ReleaseComObject(client); } catch { }
            throw;
        }
    }

    private void RenderLoop()
    {
        string? failure = null;
        while (_running)
        {
            try
            {
                Thread.Sleep(10);

                var hr = _client.GetCurrentPadding(out var padding);
                if (hr < 0) { failure = $"GetCurrentPadding 0x{hr:X8}"; break; }

                if (padding >= _bufferFrames) continue;
                uint free = _bufferFrames - padding;
                int want = (int)free * _format.BlockAlign;

                int got = _ring.Read(_chunk, want);
                if (got < want)
                {
                    Array.Clear(_chunk, got, want - got);
                    Underruns++;
                }

                hr = _render.GetBuffer(free, out var data);
                if (hr < 0) { failure = $"GetBuffer 0x{hr:X8}"; break; }

                Marshal.Copy(_chunk, 0, data, want);

                hr = _render.ReleaseBuffer(free, 0);
                if (hr < 0) { failure = $"ReleaseBuffer 0x{hr:X8}"; break; }

                WrittenBytes += want;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
                break;
            }
        }

        _running = false;
        if (failure != null)
        {
            RoutingLog.Warn($"渲染线程退出（设备 {DeviceId}）：{failure}");
            try { Stopped?.Invoke(failure); } catch { }
        }
    }

    public void Dispose()
    {
        bool wasRunning = _running;
        _running = false;

        try { _thread.Join(500); } catch { }
        if (wasRunning)
        {
            try { _client.Stop(); } catch { }
        }
        try { Marshal.ReleaseComObject(_render); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
    }
}
