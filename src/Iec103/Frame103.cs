namespace Iec103;

/// <summary>FT1.2 帧格式（IEC 60870-5-103 采用与 101 相同的固定帧/可变帧/单字符）。</summary>
public enum Frame103Format
{
    /// <summary>固定帧长：10 C A CS 16（复位链路、确认、无所请求数据等）。</summary>
    FixedLength,

    /// <summary>可变帧长：68 L L 68 C A ASDU CS 16（L = C+A+ASDU 字节数，最大 255）。</summary>
    VariableLength,

    /// <summary>单字符确认 E5。</summary>
    SingleChar,
}

/// <summary>
/// IEC 60870-5-103 链路层帧。DL/T 667-1999 采用 FT1.2：
/// 固定帧 10 C A CS 16（CS=(C+A) mod 256）；可变帧 68 L L 68 C A &lt;ASDU&gt; CS 16
/// （L 为 C、地址、用户数据长度之和；CS 为 C、地址、用户数据之和 mod 256）；单字符 E5。
/// 校验失败/长度不一致的帧整体无效并舍弃（标准规定）。
/// </summary>
public sealed record Frame103(Frame103Format Format, byte Control, byte Address, byte[] Asdu)
{
    private const byte StartFixed = 0x10;
    private const byte StartVariable = 0x68;
    private const byte End = 0x16;
    private const byte SingleAck = 0xE5;

    /// <summary>链路层最大用户数据长度（L ≤ 255，其中 C/A 占 2 字节）。</summary>
    public const int MaxAsduLength = 253;

    public static byte[] EncodeFixed(byte control, byte address)
    {
        return [StartFixed, control, address, (byte)(control + address), End];
    }

    public static byte[] EncodeSingleChar()
    {
        return [SingleAck];
    }

    public static byte[] EncodeVariable(byte control, byte address, byte[] asdu)
    {
        if (asdu.Length > MaxAsduLength)
        {
            throw new ArgumentOutOfRangeException(nameof(asdu), $"用户数据 {asdu.Length} 字节超过 FT1.2 上限 {MaxAsduLength}");
        }

        var length = (byte)(asdu.Length + 2);
        var checksum = (byte)(control + address);
        foreach (var b in asdu)
        {
            checksum += b;
        }

        // 68 L L 68（4）+ C A（2）+ ASDU（N）+ CS + 16（2）
        var frame = new byte[asdu.Length + 8];
        frame[0] = StartVariable;
        frame[1] = length;
        frame[2] = length;
        frame[3] = StartVariable;
        frame[4] = control;
        frame[5] = address;
        asdu.CopyTo(frame, 6);
        frame[^2] = checksum;
        frame[^1] = End;
        return frame;
    }

    /// <summary>编码为线路字节。单字符帧不含控制域/地址。</summary>
    public byte[] ToBytes()
    {
        return Format switch
        {
            Frame103Format.FixedLength => EncodeFixed(Control, Address),
            Frame103Format.VariableLength => EncodeVariable(Control, Address, Asdu),
            Frame103Format.SingleChar => EncodeSingleChar(),
            _ => throw new InvalidOperationException($"未知帧格式 {Format}"),
        };
    }

    /// <summary>
    /// 解码完整帧。返回 false 时 error 说明原因（校验和错、长度不一致、结束符错等）。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Frame103? frame, out string? error)
    {
        frame = null;
        error = null;

        if (bytes.Length == 0)
        {
            error = "空帧";
            return false;
        }

        if (bytes.Length == 1)
        {
            if (bytes[0] == SingleAck)
            {
                frame = new Frame103(Frame103Format.SingleChar, 0, 0, []);
                return true;
            }

            error = $"无法识别的单字节 0x{bytes[0]:X2}";
            return false;
        }

        if (bytes[0] == StartFixed)
        {
            return TryDecodeFixed(bytes, ref frame, ref error);
        }

        if (bytes[0] == StartVariable)
        {
            return TryDecodeVariable(bytes, ref frame, ref error);
        }

        error = $"未知启动字符 0x{bytes[0]:X2}";
        return false;
    }

    private static bool TryDecodeFixed(ReadOnlySpan<byte> bytes, ref Frame103? frame, ref string? error)
    {
        if (bytes.Length != 5)
        {
            error = $"固定帧长应为 5 字节，实际 {bytes.Length}";
            return false;
        }

        var (control, address, checksum) = (bytes[1], bytes[2], bytes[3]);
        if (bytes[4] != End)
        {
            error = $"固定帧结束符应为 16H，实际 0x{bytes[4]:X2}";
            return false;
        }

        var expected = (byte)(control + address);
        if (checksum != expected)
        {
            error = $"固定帧校验和错：期望 0x{expected:X2}，实际 0x{checksum:X2}";
            return false;
        }

        frame = new Frame103(Frame103Format.FixedLength, control, address, []);
        return true;
    }

    private static bool TryDecodeVariable(ReadOnlySpan<byte> bytes, ref Frame103? frame, ref string? error)
    {
        if (bytes.Length < 6)
        {
            error = $"可变帧过短：{bytes.Length} 字节";
            return false;
        }

        var length = bytes[1];
        if (length != bytes[2])
        {
            error = $"可变帧两个长度字节不一致：{length} != {bytes[2]}";
            return false;
        }

        // L = C + A + ASDU，最小 2（C/A 各 1，ASDU 空）；L<2 会令下方切片长度为负
        if (length < 2)
        {
            error = $"可变帧长度域 L={length} 小于最小值 2";
            return false;
        }

        if (bytes[3] != StartVariable)
        {
            error = $"可变帧第二启动字符应为 68H，实际 0x{bytes[3]:X2}";
            return false;
        }

        var expectedTotal = length + 6;
        if (bytes.Length != expectedTotal)
        {
            error = $"可变帧总长应为 {expectedTotal}（L={length}），实际 {bytes.Length}";
            return false;
        }

        if (bytes[^1] != End)
        {
            error = $"可变帧结束符应为 16H，实际 0x{bytes[^1]:X2}";
            return false;
        }

        var checksum = bytes[^2];
        var calculated = 0;
        for (var i = 4; i < 4 + length; i++)
        {
            calculated += bytes[i];
        }

        if (checksum != (byte)calculated)
        {
            error = $"可变帧校验和错：期望 0x{(byte)calculated:X2}，实际 0x{checksum:X2}";
            return false;
        }

        var asdu = bytes.Slice(6, length - 2).ToArray();
        frame = new Frame103(Frame103Format.VariableLength, bytes[4], bytes[5], asdu);
        return true;
    }
}

/// <summary>
/// 字节流分帧器（TCP 透传串口场景）：按 FT1.2 逐字节状态机切帧，
/// 处理粘包/半包；无法识别的字节静默丢弃，坏帧整体舍弃并上报原因。
/// </summary>
public sealed class FrameStreamParser103
{
    private enum State { WaitStart, FixedBody, VariableHeader, VariableBody }

    private readonly List<byte> _buffer = [];
    private State _state = State.WaitStart;

    /// <summary>坏帧被舍弃时触发（诊断用）。</summary>
    public event Action<string, string>? FrameRejected;

    /// <summary>当前是否处于帧中间（用于判断还需要多少字节）。</summary>
    public int PendingBytes => _state == State.VariableBody
        ? _variableNeeded - _buffer.Count
        : 1;

    private int _variableNeeded;

    public List<Frame103> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<Frame103>();
        foreach (var b in bytes)
        {
            switch (_state)
            {
                case State.WaitStart:
                    if (b == 0xE5)
                    {
                        frames.Add(new Frame103(Frame103Format.SingleChar, 0, 0, []));
                    }
                    else if (b == 0x10)
                    {
                        _buffer.Clear();
                        _buffer.Add(b);
                        _state = State.FixedBody;
                    }
                    else if (b == 0x68)
                    {
                        _buffer.Clear();
                        _buffer.Add(b);
                        _state = State.VariableHeader;
                    }

                    // 其余字节视为线路噪声丢弃
                    break;

                case State.FixedBody:
                    _buffer.Add(b);
                    if (_buffer.Count == 5)
                    {
                        Flush(frames);
                    }

                    break;

                case State.VariableHeader:
                    _buffer.Add(b);
                    if (_buffer.Count == 4)
                    {
                        if (_buffer[1] == _buffer[2] && _buffer[3] == 0x68)
                        {
                            _variableNeeded = _buffer[1] + 6;
                            _state = State.VariableBody;
                        }
                        else
                        {
                            Reject("可变帧头不一致（L1/L2 或第二启动字符错）");
                        }
                    }

                    break;

                case State.VariableBody:
                    _buffer.Add(b);
                    if (_buffer.Count == _variableNeeded)
                    {
                        Flush(frames);
                    }

                    break;
            }
        }

        return frames;
    }

    private void Flush(List<Frame103> frames)
    {
        if (Frame103.TryDecode(_buffer.ToArray(), out var frame, out var error) && frame is not null)
        {
            frames.Add(frame);
        }
        else
        {
            Reject(error ?? "未知错误");
        }

        _buffer.Clear();
        _state = State.WaitStart;
    }

    private void Reject(string reason)
    {
        FrameRejected?.Invoke(Convert.ToHexString(_buffer.ToArray()), reason);
        _buffer.Clear();
        _state = State.WaitStart;
    }
}
