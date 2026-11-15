using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VideoTurner.Services;

/// <summary>
/// 「试听」：向指定的输出设备播一小段提示音。
///
/// <para>为什么需要：路由「成功」不等于用户能听见 —— 如果映射到的那个 HDMI 端点
/// 其实没有喇叭（显示器无扬声器、或那台「显示器」是采集/虚拟屏），
/// 声音就是被搬到了一个哑巴设备上，主观感受同样是「没用」。
/// 有了试听，用户能在一分钟内分辨「设备本身不出声」和「路由没生效」。</para>
/// </summary>
public static class DeviceTonePlayer
{
    /// <summary>
    /// 向 <paramref name="deviceId"/> 播 <paramref name="seconds"/> 秒的提示音。
    /// 找不到设备或播放失败时返回 false（并写日志），不抛异常。
    /// </summary>
    public static bool Play(string deviceId, string deviceName, double seconds = 0.8, double frequency = 440)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                RoutingLog.Warn($"试听失败：找不到设备 {deviceName}（{deviceId}）—— 可能已拔出或未激活");
                return false;
            }

            var tone = new SignalGenerator(device.AudioClient.MixFormat.SampleRate, device.AudioClient.MixFormat.Channels)
            {
                Gain = 0.25,
                Frequency = frequency,
                Type = SignalGeneratorType.Sin
            }.Take(TimeSpan.FromSeconds(seconds));

            using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            output.Init(tone.ToWaveProvider());
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(50);

            RoutingLog.Info($"试听：已在「{deviceName}」播出一声提示音");
            return true;
        }
        catch (Exception ex)
        {
            RoutingLog.Warn($"试听失败（{deviceName}）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
