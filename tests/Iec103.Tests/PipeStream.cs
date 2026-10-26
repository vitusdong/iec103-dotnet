namespace Iec103.Tests;

/// <summary>
/// 内存双工管道：CreatePair 得到一对交叉连通的 Stream，
/// 供 CS101Master/CS101Slave 在无物理串口/TCP 环境下自环测试。
/// 读语义对齐 SerialPort：无数据时最多等 ReadTimeout，超时抛 TimeoutException
/// （lib60870 SerialTransceiverFT12 依赖该语义轮询起始字节）。
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Queue<byte> _inbox = new();
    private readonly object _writeLock = new();
    private PipeStream? _peer;
    private bool _closed;
    private int _readTimeout = 50;

    public static (Stream Left, Stream Right) CreatePair()
    {
        var left = new PipeStream();
        var right = new PipeStream();
        left._peer = right;
        right._peer = left;
        return (left, right);
    }

    public override bool CanRead => !_closed;

    public override bool CanWrite => !_closed;

    public override bool CanSeek => false;

    public override bool CanTimeout => true;

    public override int ReadTimeout
    {
        get => _readTimeout;
        set => _readTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_inbox)
        {
            if (_inbox.Count == 0)
            {
                if (_closed)
                {
                    return 0;
                }

                if (!Monitor.Wait(_inbox, _readTimeout))
                {
                    throw new TimeoutException();
                }
            }

            var read = Math.Min(count, _inbox.Count);
            for (var i = 0; i < read; i++)
            {
                buffer[offset + i] = _inbox.Dequeue();
            }

            return read;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var peer = _peer ?? throw new IOException("管道未连接");
        lock (peer._writeLock)
        {
            lock (peer._inbox)
            {
                if (peer._closed)
                {
                    throw new IOException("对端已关闭");
                }

                for (var i = 0; i < count; i++)
                {
                    peer._inbox.Enqueue(buffer[offset + i]);
                }

                Monitor.PulseAll(peer._inbox);
            }
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        lock (_inbox)
        {
            Monitor.PulseAll(_inbox);
        }

        base.Dispose(disposing);
    }
}
