namespace VideoTurner.Services;

/// <summary>
/// 捕获线程与渲染线程之间的环形缓冲（按字节，元素大小 = 一帧 = blockAlign 字节）。
///
/// <para>两个线程由两个不同的时钟驱动：捕获跟着虚拟声卡的音频引擎走，渲染跟着目标声卡的
/// 硬件时钟走。长期运行必然漂移，所以这里在写满时<b>丢弃最旧的数据</b>（宁可丢 5 ms，
/// 也不要让延迟越积越大），读空时由渲染端补静音。</para>
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _read;
    private int _count;

    public AudioRingBuffer(int capacityBytes)
    {
        _buffer = new byte[Math.Max(capacityBytes, 4096)];
    }

    public int Capacity => _buffer.Length;

    public int BufferedBytes
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>因为溢出被丢掉的字节数（诊断用：持续增长说明两端口时钟差太大）。</summary>
    public long DroppedBytes { get; private set; }

    public void Write(byte[] source, int count)
    {
        if (count <= 0) return;

        lock (_gate)
        {
            if (count >= _buffer.Length)
            {
                // 一次写入就超过整个缓冲：只保留最后一段
                int skip = count - _buffer.Length;
                Array.Copy(source, skip, _buffer, 0, _buffer.Length);
                _read = 0;
                _count = _buffer.Length;
                DroppedBytes += skip;
                return;
            }

            int overflow = _count + count - _buffer.Length;
            if (overflow > 0)
            {
                _read = (_read + overflow) % _buffer.Length;
                _count -= overflow;
                DroppedBytes += overflow;
            }

            int write = (_read + _count) % _buffer.Length;
            int first = Math.Min(count, _buffer.Length - write);
            Array.Copy(source, 0, _buffer, write, first);
            if (count > first)
                Array.Copy(source, first, _buffer, 0, count - first);
            _count += count;
        }
    }

    /// <summary>读取最多 <paramref name="count"/> 字节，返回实际读到的字节数（可能为 0）。</summary>
    public int Read(byte[] destination, int count)
    {
        lock (_gate)
        {
            int take = Math.Min(count, _count);
            if (take <= 0) return 0;

            int first = Math.Min(take, _buffer.Length - _read);
            Array.Copy(_buffer, _read, destination, 0, first);
            if (take > first)
                Array.Copy(_buffer, 0, destination, first, take - first);

            _read = (_read + take) % _buffer.Length;
            _count -= take;
            return take;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _count = 0;
        }
    }
}
