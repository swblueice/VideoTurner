using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VideoTurner.Interop;
using VideoTurner.Services;

namespace LoopbackProbe;

/// <summary>
/// 只读探针：验证「进程回环捕获 + 共享模式渲染」这条链路。
/// 不启动 VideoTurner 的界面、不调用任何路由接口、不改任何系统设置。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var toneIndex = Array.IndexOf(args, "--tone");
        if (toneIndex >= 0 && toneIndex + 1 < args.Length)
        {
            var seconds = double.Parse(args[toneIndex + 1], System.Globalization.CultureInfo.InvariantCulture);
            var delay = 0.0;
            var afterIndex = Array.IndexOf(args, "--after");
            if (afterIndex >= 0 && afterIndex + 1 < args.Length)
                delay = double.Parse(args[afterIndex + 1], System.Globalization.CultureInfo.InvariantCulture);

            if (delay > 0) Thread.Sleep(TimeSpan.FromSeconds(delay));
            PlayTone(seconds);
            return 0;
        }

        if (args.Contains("--channel-test"))
            return RunChannelTest();

        var targetArgIndex = Array.IndexOf(args, "--target");
        var targetHint = targetArgIndex >= 0 && targetArgIndex + 1 < args.Length
            ? args[targetArgIndex + 1]
            : null;

        return RunSelfTest(targetHint);
    }

    // ── 放音子进程 ────────────────────────────────────────────────────────

    private static void PlayTone(double seconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;

        var generator = new SignalGenerator(mix.SampleRate, mix.Channels)
        {
            Gain = 0.3,
            Frequency = 440,
            Type = SignalGeneratorType.Sin
        };

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        output.Init(generator.ToWaveProvider());
        output.Play();

        Console.WriteLine($"[tone] PID {Environment.ProcessId} 正在向「{device.FriendlyName}」播放 440 Hz，" +
                          $"{seconds:0.#} 秒");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    // ── 自检 ──────────────────────────────────────────────────────────────

    private static int RunSelfTest(string? targetHint)
    {
        Console.WriteLine("=== 0) 输出端点 ===");
        var devices = new List<AudioDeviceInfoLite>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfoLite(device.ID, device.FriendlyName,
                    device.ID == enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID));
            }
        }
        foreach (var device in devices)
            Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}   {device.Id}");

        var target = PickTarget(devices, targetHint);
        if (target == null)
        {
            Console.WriteLine("没有可用目标设备。");
            return 1;
        }
        Console.WriteLine($"\n目标设备（要把声音送过去的那台）：{target.Name}");

        // 1) 起一个放音子进程
        var exePath = Environment.ProcessPath!;
        using var tone = Process.Start(new ProcessStartInfo(exePath, "--tone 12")
        {
            UseShellExecute = false,
            CreateNoWindow = false
        });
        if (tone == null) { Console.WriteLine("无法启动放音子进程。"); return 1; }
        Thread.Sleep(1500);

        // 2) 抓这个子进程的音频
        ProcessLoopbackCapture capture;
        try
        {
            capture = ProcessLoopbackCapture.Start((uint)tone.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ 进程回环捕获启动失败：0x{ex.HResult:X8} {ex.Message}");
            try { tone.Kill(); } catch { }
            return 2;
        }

        Console.WriteLine($"\n=== 1) 进程回环捕获（PID {tone.Id}）===");
        Console.WriteLine($"  捕获格式：{capture.Format}");

        var ring = new AudioRingBuffer(capture.Format.AverageBytesPerSecond / 5); // 200 ms
        double capturePeak = 0;
        long captureBytes = 0;
        capture.DataAvailable += (buffer, count) =>
        {
            ring.Write(buffer, count);
            captureBytes += count;
            double peak = PeakOf(buffer, count, capture.Format);
            if (peak > capturePeak) capturePeak = peak;
        };

        Thread.Sleep(2000);
        Console.WriteLine($"  2 秒内收到 {captureBytes} 字节（{captureBytes / (double)capture.Format.AverageBytesPerSecond:0.00} 秒音频），" +
                          $"峰值 {capturePeak:0.000}");

        if (captureBytes == 0)
            Console.WriteLine("  ⚠ 没抓到任何数据：确认子进程确实在放音、系统是 Windows 10 2004+。");

        // 3) 渲染到目标设备
        Console.WriteLine($"\n=== 2) 渲染到「{target.Name}」===");
        WasapiDeviceRenderer renderer;
        try
        {
            renderer = WasapiDeviceRenderer.Start(target.Id, capture.Format, ring);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 渲染端启动失败：0x{ex.HResult:X8} {ex.Message}");
            capture.Dispose();
            try { tone.Kill(); } catch { }
            return 3;
        }

        // 4) 同时量目标设备自己的回环电平，证明声音真的到了那台设备
        double targetPeak = 0;
        long targetSamples = 0;
        var loopbackDevice = new MMDeviceEnumerator().GetDevice(target.Id);
        using (var loop = new WasapiLoopbackCapture(loopbackDevice))
        {
            loop.DataAvailable += (_, e) =>
            {
                double peak = PeakOf(e.Buffer, e.BytesRecorded, loop.WaveFormat);
                if (peak > targetPeak) targetPeak = peak;
                targetSamples += e.BytesRecorded;
            };
            loop.StartRecording();
            Thread.Sleep(2500);
            loop.StopRecording();
        }

        Console.WriteLine($"  已写入 {renderer.WrittenBytes} 字节，欠载 {renderer.Underruns} 次，" +
                          $"环形缓冲丢弃 {ring.DroppedBytes} 字节，当前积压 {ring.BufferedBytes} 字节");
        Console.WriteLine($"  目标端点回环电平：峰值 {targetPeak:0.000}，收到 {targetSamples} 字节");

        var ok = captureBytes > 0 && renderer.WrittenBytes > 0 && targetPeak > 0.001;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "✅ 链路成立：进程音频 → 捕获 → 目标设备（目标端点确实收到了有声数据）"
            : "❌ 链路不成立，见上面的数字");

        renderer.Dispose();
        capture.Dispose();
        try { tone.Kill(); } catch { }
        return ok ? 0 : 4;
    }

    // ── 虚拟通道链路测试 ──────────────────────────────────────────────────

    /// <summary>
    /// 把真实使用流程拆成三步验证：
    ///   1) 正在出声的程序 → 把它的输出设备改成「虚拟声卡」（这里用一台非默认端点扮演）；
    ///   2) 用进程回环把它的音频抓下来，渲染到另一台设备（程序不用重启、当前音频流也不断）；
    ///   3) 再启动一次同样的程序，确认它这次一出声就直接落在虚拟声卡上
    ///      （= 策略是持久化的，所以「只读一次输出设备」的程序重启一次之后就一直听话）。
    /// </summary>
    private static int RunChannelTest()
    {
        Console.WriteLine("=== 虚拟通道链路测试 ===");

        var devices = new List<AudioDeviceInfoLite>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                devices.Add(new AudioDeviceInfoLite(device.ID, device.FriendlyName, device.ID == defaultId));
        }

        var sink = devices.FirstOrDefault(d => !d.IsDefault);
        var source = devices.FirstOrDefault(d => d.IsDefault);
        if (sink == null || source == null)
        {
            Console.WriteLine("需要至少两台输出端点（一台当虚拟声卡，一台当原设备）。");
            return 1;
        }

        Console.WriteLine($"  虚拟声卡（假装是静音端点）：{sink.Name}");
        Console.WriteLine($"  原设备（程序本来在用的）：{source.Name}");

        var exePath = Environment.ProcessPath!;

        // ── 1) 一个正在出声的程序 ──
        using var toneA = Process.Start(new ProcessStartInfo(exePath, "--tone 12")
        {
            UseShellExecute = false
        });
        if (toneA == null) { Console.WriteLine("无法启动放音子进程。"); return 1; }
        Thread.Sleep(2500);

        var sessionA = new AudioSessionService().GetActiveSessions()
            .FirstOrDefault(s => s.ProcessId == (uint)toneA.Id);
        Console.WriteLine($"\n1) 程序 A（PID {toneA.Id}）当前会话设备：{sessionA?.AudioDeviceName ?? "（无）"}");

        using (var router = new AudioRouterService())
        {
            if (!router.TryRouteProcessToDevice((uint)toneA.Id, sink.Id, out var error))
            {
                Console.WriteLine($"❌ 把 A 指向虚拟声卡失败：{error}");
                try { toneA.Kill(); } catch { }
                return 2;
            }
        }

        using (var client = new VideoTurner.Interop.AudioPolicyConfigClient())
        {
            var persisted = client.GetPersistedDefaultAudioEndpoint(
                (uint)toneA.Id, VideoTurner.Interop.EDataFlow.eRender, VideoTurner.Interop.ERole.eConsole);
            Console.WriteLine($"   写入后读回：{(string.IsNullOrEmpty(persisted) ? "(空)" : persisted)}");
            Console.WriteLine("   （当前正在播的那一路不会迁移，这是 Windows 的行为；下一步验证的是捕获不影响它）");
        }

        // ── 2) 捕获 A → 渲染到虚拟声卡那台端点，量它自己的回环 ──
        Console.WriteLine("\n2) 把 A 的音频抓下来，送到另一台设备（A 不重启）");
        ProcessChannel channel;
        try
        {
            channel = new ProcessChannel((uint)toneA.Id, "ToneA", sink.Id, sink.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 建立通道失败：0x{ex.HResult:X8} {ex.Message}");
            try { toneA.Kill(); } catch { }
            return 3;
        }

        double targetPeak = 0;
        using (var loopDevice = new MMDeviceEnumerator().GetDevice(sink.Id))
        using (var loop = new WasapiLoopbackCapture(loopDevice))
        {
            loop.DataAvailable += (_, e) =>
                targetPeak = Math.Max(targetPeak, PeakOf(e.Buffer, e.BytesRecorded, loop.WaveFormat));
            loop.StartRecording();
            Thread.Sleep(3000);
            loop.StopRecording();
        }
        Console.WriteLine($"   通道：{channel.Describe()}");
        Console.WriteLine($"   目标端点回环峰值：{targetPeak:0.000}");

        // ── 3) 再启动一次同样的程序：这次一出声就应该直接在虚拟声卡上 ──
        Console.WriteLine("\n3) 重启同样的程序，看它是不是直接落在虚拟声卡上");
        using var toneB = Process.Start(new ProcessStartInfo(exePath, "--tone 6 --after 1")
        {
            UseShellExecute = false
        });
        Thread.Sleep(3500);

        var sessionB = toneB == null
            ? null
            : new AudioSessionService().GetActiveSessions()
                .FirstOrDefault(s => s.ProcessId == (uint)toneB.Id);
        Console.WriteLine($"   程序 B（PID {toneB?.Id}）会话设备：{sessionB?.AudioDeviceName ?? "（无）"}");

        var restartLandsOnSink =
            sessionB != null &&
            string.Equals(sessionB.AudioDeviceName, sink.Name, StringComparison.OrdinalIgnoreCase);

        var ok = targetPeak > 0.001 && restartLandsOnSink;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "✅ 虚拟通道成立：进程捕获 → 另一台设备出声；并且重启后程序直接落在虚拟声卡上"
            : $"❌ 结果：捕获/渲染 {(targetPeak > 0.001 ? "OK" : "失败")}，" +
              $"重启落点 {(restartLandsOnSink ? "OK" : "不对（可能策略是按 PID 持久化的）")}");

        channel.Dispose();

        // 清理：把两个进程的固定项清掉，避免影响后续运行
        using (var router = new AudioRouterService())
        {
            router.TryClearProcessRouting((uint)toneA.Id);
            if (toneB != null) router.TryClearProcessRouting((uint)toneB.Id);
        }
        try { toneA.Kill(); } catch { }
        try { toneB?.Kill(); } catch { }
        return ok ? 0 : 4;
    }

    private static AudioDeviceInfoLite? PickTarget(List<AudioDeviceInfoLite> devices, string? hint)
    {
        if (!string.IsNullOrEmpty(hint))
            return devices.FirstOrDefault(d => d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));

        // 优先挑一台「不是系统默认」的设备，这样能证明声音被搬了家
        return devices.FirstOrDefault(d => !d.IsDefault)
               ?? devices.FirstOrDefault();
    }

    private static double PeakOf(byte[] buffer, int count, AudioFormat format)
    {
        double peak = 0;
        if (format.IsFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= count; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= count; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
        }
        return peak;
    }

    private static double PeakOf(byte[] buffer, int count, WaveFormat format)
    {
        double peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= count; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= count; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
        }
        return peak;
    }

    private sealed record AudioDeviceInfoLite(string Id, string Name, bool IsDefault);
}
