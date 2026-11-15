using System.Runtime.InteropServices;

namespace VideoTurner.Interop;

/// <summary>
/// 一路音频的格式描述（我们只用一种固定格式：捕获端与渲染端都用它，
/// 端点格式差异交给 Windows 的 AUTOCONVERTPCM / SRC_DEFAULT_QUALITY 处理）。
/// </summary>
public sealed class AudioFormat
{
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public bool IsFloat { get; }

    public AudioFormat(int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        IsFloat = isFloat;
    }

    public int BlockAlign => Channels * BitsPerSample / 8;
    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    public WAVEFORMATEX ToNative() => new()
    {
        wFormatTag = (ushort)(IsFloat ? 3 /*WAVE_FORMAT_IEEE_FLOAT*/ : 1 /*WAVE_FORMAT_PCM*/),
        nChannels = (ushort)Channels,
        nSamplesPerSec = (uint)SampleRate,
        nAvgBytesPerSec = (uint)AverageBytesPerSecond,
        nBlockAlign = (ushort)BlockAlign,
        wBitsPerSample = (ushort)BitsPerSample,
        cbSize = 0
    };

    /// <summary>分配一块非托管 WAVEFORMATEX，调用方负责 Marshal.FreeHGlobal。</summary>
    public IntPtr AllocNative()
    {
        var native = ToNative();
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        Marshal.StructureToPtr(native, ptr, false);
        return ptr;
    }

    public override string ToString()
        => $"{SampleRate} Hz / {Channels} ch / {BitsPerSample} bit {(IsFloat ? "float" : "pcm")}";
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>进程回环捕获的激活参数（AUDIOCLIENT_ACTIVATION_PARAMS）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType; // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    public uint TargetProcessId;
    public int ProcessLoopbackMode; // 0 = INCLUDE_TARGET_PROCESS_TREE
}

/// <summary>带 VT_BLOB 的 PROPVARIANT（x64 下 24 字节）。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantBlob
{
    [FieldOffset(0)] public ushort vt;          // VT_BLOB = 65
    [FieldOffset(8)] public uint cbSize;        // BLOB.cbSize
    [FieldOffset(16)] public IntPtr pBlobData;  // BLOB.pBlobData
}

/// <summary>
/// WASAPI 裸互操作层。
///
/// <para>为什么不用 NAudio 的 WasapiLoopbackCapture / WasapiOut：</para>
/// <list type="bullet">
///   <item><b>进程回环捕获</b>（Windows 10 2004+，<c>VAD\Process_Loopback</c> +
///     <c>ActivateAudioInterfaceAsync</c>）NAudio 2.3 没有封装；</item>
///   <item>两端都需要精确控制 <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
///     SRC_DEFAULT_QUALITY</c>，让 Windows 自己做采样率/格式转换 ——
///     这样「捕获格式」可以固定，目标设备换了也不用重采样；</item>
///   <item>渲染端要自己控制写循环，才能在目标设备切换时保留抖动缓冲、不断流。</item>
/// </list>
///
/// <para>全部接口按 EarTrumpet / NAudio 已验证过的 vtable 顺序声明，
/// 只声明用得到的方法（IUnknown 的 3 个槽位由 .NET 自动占位）。</para>
/// </summary>
internal static class WasapiInterop
{
    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x00080000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

    /// <summary>进程回环捕获的「虚拟设备接口路径」。</summary>
    public const string ProcessLoopbackDevicePath = @"VAD\Process_Loopback";

    public const string RenderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    /// <summary>把一个 MMDevice 端点 ID 拼成 ActivateAudioInterfaceAsync 需要的设备接口路径。</summary>
    public static string ToDeviceInterfacePath(string deviceId)
        => $@"\\?\SWD#MMDEVAPI#{deviceId}#{RenderInterfaceGuid}";

    // ── 激活 ────────────────────────────────────────────────────────────

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
    }

    // 注意：导出在 mmdevapi.dll（不是 combase.dll；后者只有 WinRT 那套）
    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);
        public int HResult;
        public object? ActivatedInterface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                HResult = activateOperation.GetActivateResult(out var hr, out var iface);
                HResult = hr;
                ActivatedInterface = iface;
            }
            catch (Exception ex)
            {
                HResult = ex.HResult;
            }
            finally
            {
                _done.Set();
            }
        }

        public void Wait(int millisecondsTimeout)
        {
            if (!_done.Wait(millisecondsTimeout))
                throw new TimeoutException("ActivateAudioInterfaceAsync 超过 " + millisecondsTimeout + " ms 没有回调。");
            if (HResult < 0)
                Marshal.ThrowExceptionForHR(HResult);
            if (ActivatedInterface == null)
                throw new InvalidOperationException("接口激活成功但没有返回对象。");
        }
    }

    /// <summary>
    /// 激活一个 IAudioClient。<paramref name="activationParams"/> 为 PROPVARIANT 指针
    /// （进程回环时用 <see cref="CreateProcessLoopbackPropVariant"/> 创建），普通端点传 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IAudioClientRaw ActivateAudioClient(string deviceInterfacePath, IntPtr activationParams)
    {
        var iid = IID_IAudioClient;
        var handler = new ActivationHandler();

        var hr = ActivateAudioInterfaceAsync(
            deviceInterfacePath, ref iid, activationParams, handler, out var operation);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            handler.Wait(5000);
        }
        finally
        {
            if (operation != null) Marshal.ReleaseComObject(operation);
        }

        return (IAudioClientRaw)handler.ActivatedInterface!;
    }

    /// <summary>
    /// 为「捕获某个进程（含其子进程树）的音频」准备 PROPVARIANT。
    /// 返回的指针要连同 <paramref name="paramsPtr"/> 一起用 <see cref="FreeActivationParams"/> 释放。
    /// </summary>
    public static IntPtr CreateProcessLoopbackPropVariant(uint processId, out IntPtr paramsPtr)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1,             // PROCESS_LOOPBACK
            TargetProcessId = processId,
            ProcessLoopbackMode = 0         // INCLUDE_TARGET_PROCESS_TREE
        };

        paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        Marshal.StructureToPtr(activationParams, paramsPtr, false);

        var propVariant = new PropVariantBlob
        {
            vt = 65, // VT_BLOB
            cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
            pBlobData = paramsPtr
        };

        var propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        Marshal.StructureToPtr(propVariant, propPtr, false);
        return propPtr;
    }

    public static void FreeActivationParams(IntPtr propVariantPtr, IntPtr blobPtr)
    {
        if (propVariantPtr != IntPtr.Zero) Marshal.FreeHGlobal(propVariantPtr);
        if (blobPtr != IntPtr.Zero) Marshal.FreeHGlobal(blobPtr);
    }

    // ── COM 接口 ────────────────────────────────────────────────────────

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClientRaw
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
            IntPtr pFormat, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint numBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint numPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService([In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClientRaw
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClientRaw
    {
        [PreserveSig]
        int GetBuffer(uint numFramesRequested, out IntPtr data);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesWritten, uint flags);
    }
}
