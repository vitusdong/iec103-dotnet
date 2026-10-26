using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Iec103;

/// <summary>接收到的 103 ASDU 快照。</summary>
public sealed record ReceivedAsdu103(Asdu103 Asdu, byte LinkAddress);

/// <summary>
/// IEC 60870-5-103 主站（TCP 或串口承载 FT1.2 字节流，帧层一致）。同步一问一答模型：
/// 每个链路服务（SEND/CONFIRM、REQUEST/RESPOND）发送后等待单个响应帧。
/// FCB 随每轮成功的服务翻转；响应帧与 ASDU 以事件暴露。
/// </summary>
public sealed class Iec103Client : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly FrameStreamParser103 _parser = new();
    private readonly ConcurrentQueue<Frame103> _responses = new();
    private readonly object _receivedLock = new();
    private readonly List<ReceivedAsdu103> _received = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable? _portOwner;
    private Stream? _stream;
    private bool _fcb;

    public string Host { get; }

    public int Port { get; }

    /// <summary>链路服务超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>
    /// 静默 1 级召唤的空响应帧（FC=9 无数据固定帧）：周期轮询场景下，
    /// 每次召唤都回调 FrameReceived 会使诊断日志被 10 09 xx 0A 16 刷屏。
    /// </summary>
    public bool SilenceEmptyPollResponses { get; set; }

    /// <summary>收到响应帧时触发（诊断用），参数为线路十六进制与解码结果。</summary>
    public event Action<string, Frame103>? FrameReceived;

    public event Action<ReceivedAsdu103>? AsduReceived;

    /// <summary>后台读循环意外故障时触发（非 IO/取消类异常）；其后所有事务将超时。</summary>
    public event Action<Exception>? ReadFaulted;

    /// <summary>接收 ASDU 快照上限（防长连接无限增长）。</summary>
    private const int ReceivedCap = 1024;

    /// <summary>读循环退出（对端关闭/IO 异常/主动取消）时触发，LinkReady 语义的断线通知。</summary>
    public event Action? LinkClosed;

    public Iec103Client(string host, int port)
    {
        Host = host;
        Port = port;
        WireParserRejection();
    }

    /// <summary>
    /// 串口承载模式：构造即附流（Connect 只启动读循环，不走 TCP 连接）。
    /// portOwner 为串口宿主（SerialPort），Dispose 时随流一并释放。
    /// </summary>
    public Iec103Client(Stream stream, IDisposable? portOwner = null)
    {
        Host = "serial";
        Port = 0;
        _stream = stream;
        _portOwner = portOwner;
        WireParserRejection();
    }

    private void WireParserRejection()
    {
        _parser.FrameRejected += (hex, reason) => FrameReceived?.Invoke($"!{hex} {reason}", new Frame103(Frame103Format.SingleChar, 0, 0, []));
    }

    public void Connect(int timeoutMs = 5000)
    {
        if (_stream is not null)
        {
            // 串口承载：流已就绪，直接进入读循环
            _ = Task.Run(() => ReadLoop(_cts.Token));
            return;
        }

        if (!_client.ConnectAsync(Host, Port).Wait(timeoutMs))
        {
            throw new TimeoutException($"连接 {Host}:{Port} 超时（{timeoutMs}ms）");
        }

        _stream = _client.GetStream();
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = _stream!;
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, token);
                }
                catch (TimeoutException)
                {
                    // 串口读超时（50ms 周期醒一次观测取消）：链路仍在，空转重读
                    continue;
                }

                if (read == 0)
                {
                    break;
                }

                foreach (var frame in _parser.Feed(buffer.AsSpan(0, read)))
                {
                    OnFrame(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 通道断开/停服时流被 Dispose 打断（串口读阻塞在 ReadAsync）均属正常收尾
        }
        catch (Exception ex)
        {
            // 未预期异常（如畸形帧解码缺陷）：上抛事件供诊断，否则事务只会默默超时
            ReadFaulted?.Invoke(ex);
        }
        finally
        {
            LinkClosed?.Invoke();
        }
    }

    private void OnFrame(Frame103 frame)
    {
        if (SilenceEmptyPollResponses && frame.Format == Frame103Format.FixedLength &&
            LinkControl103.FromByte(frame.Control).Function == (byte)SecondaryFunction103.RespondNoData)
        {
            return;
        }

        FrameReceived?.Invoke(Convert.ToHexString(frame.ToBytes()), frame);
        if (frame.Format == Frame103Format.VariableLength &&
            Asdu103.TryDecode(frame.Asdu, out var asdu, out _) && asdu is not null)
        {
            var received = new ReceivedAsdu103(asdu, frame.Address);
            lock (_receivedLock)
            {
                _received.Add(received);
                while (_received.Count > ReceivedCap)
                {
                    _received.RemoveAt(0);
                }
            }

            AsduReceived?.Invoke(received);
        }

        _responses.Enqueue(frame);
    }

    public List<ReceivedAsdu103> GetReceivedSnapshot()
    {
        lock (_receivedLock)
        {
            return [.. _received];
        }
    }

    // —— 链路服务 ——

    /// <summary>复位通信单元（RESET CU）：清 FCB，从站回 ACK（或 E5）。</summary>
    public Frame103 ResetLink(byte address)
    {
        _fcb = false;
        return Transact(Frame103.EncodeFixed(LinkControl103.ResetCu().ToByte(), address));
    }

    /// <summary>发送/确认用户数据（SEND/CONFIRM），FCB 每轮翻转。返回从站确认帧。</summary>
    public Frame103 SendConfirmed(byte address, Asdu103 asdu)
    {
        var control = LinkControl103.SendConfirmed(_fcb).ToByte();
        var response = Transact(Frame103.EncodeVariable(control, address, asdu.Encode()));
        _fcb = !_fcb;
        return response;
    }

    /// <summary>召唤 1 级用户数据。返回数据帧（变长）或无数据帧（固定 FC=9）。</summary>
    public Frame103 RequestClass1(byte address)
    {
        var control = LinkControl103.RequestClass1(_fcb).ToByte();
        var response = Transact(Frame103.EncodeFixed(control, address));
        _fcb = !_fcb;
        return response;
    }

    /// <summary>召唤 2 级用户数据。</summary>
    public Frame103 RequestClass2(byte address)
    {
        var control = LinkControl103.RequestClass2(_fcb).ToByte();
        var response = Transact(Frame103.EncodeFixed(control, address));
        _fcb = !_fcb;
        return response;
    }

    // —— 基础四件套 ——

    /// <summary>时间同步（TI=6，COT=8，FUN=0 与多数主站一致）。从站回读帧需 1 级召唤收取。</summary>
    public Frame103 SendClockSync(byte address, DateTime time)
    {
        return SendConfirmed(address, Asdu103.ClockSync(Cp56Time2a103.FromDateTime(time), address));
    }

    /// <summary>总召唤（TI=7，FUN=255 INF=0，COT=9）。应答数据需 1 级召唤收取。</summary>
    public Frame103 SendGeneralInterrogation(byte address, byte scn = 0)
    {
        return SendConfirmed(address, Asdu103.GeneralInterrogation(address, scn));
    }

    /// <summary>
    /// 轮询 1 级队列直至谓词满足（对收到的 ASDU 求值）或无数据。
    /// 返回期间收到的全部 ASDU（含满足谓词的最后一帧）。
    /// </summary>
    public List<Asdu103> DrainClass1(byte address, Func<Asdu103, bool> stop, int maxFrames = 512)
    {
        var result = new List<Asdu103>();
        for (var i = 0; i < maxFrames; i++)
        {
            var response = RequestClass1(address);
            if (response.Format != Frame103Format.VariableLength)
            {
                break;
            }

            if (!Asdu103.TryDecode(response.Asdu, out var asdu, out _) || asdu is null)
            {
                continue;
            }

            result.Add(asdu);
            if (stop(asdu))
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 轮询 1 级队列直至收到总召唤终止帧（TI=8、COT=10）或无数据。
    /// 返回期间收到的全部 ASDU。
    /// </summary>
    public List<Asdu103> DrainClass1UntilGiEnd(byte address, int maxFrames = 512)
    {
        return DrainClass1(address,
            asdu => asdu.TypeId == TypeId103.GeneralInterrogationEnd && asdu.Cot == Cot103.GiStop,
            maxFrames);
    }

    // —— 通用分类 ——

    private byte _rii;

    private byte NextRii() => _rii = (byte)((_rii + 1) & 0x7F);

    /// <summary>
    /// 读全目录（GIN 0000、KOD=描述）：收齐同 RII 的全部 ASDU 10（NGD 后续标志清零即末帧）。
    /// </summary>
    public List<GenericDataSet103> ReadGenericProfile(byte address)
    {
        var rii = NextRii();
        SendConfirmed(address, Asdu103.GenericReadAll(address, rii));
        return CollectGenericSets(address, rii);
    }

    /// <summary>读指定条目的指定 KOD。</summary>
    public List<GenericDataSet103> ReadGenericEntries(byte address, params (Gin103 Gin, byte Kod)[] entries)
    {
        var rii = NextRii();
        SendConfirmed(address, Asdu103.GenericRead(address, rii, entries));
        return CollectGenericSets(address, rii);
    }

    /// <summary>写条目实际值（TI=10，COT=40），返回从站写确认数据集（含回复码）。</summary>
    public GenericDataSet103 WriteGenericValue(byte address, GenericDataSet103 valueSet)
    {
        var rii = NextRii();
        SendConfirmed(address, Asdu103.GenericDataAsdu(address, rii, continuation: false, Cot103.GenericWrite, [valueSet]));

        // 队列中可能残留复位标识等无关帧，过滤后取写确认
        var ack = DrainClass1(address, asdu =>
                asdu.TypeId == TypeId103.GenericData &&
                asdu.Cot is Cot103.GenericWriteAckPositive or Cot103.GenericWriteAckNegative)
            .FirstOrDefault(asdu => asdu.TypeId == TypeId103.GenericData);
        if (ack is null)
        {
            throw new TimeoutException("未收到通用分类写确认");
        }

        GenericDataSet103.TryDecode(ack.Data.AsSpan(2), out var set, out _);
        return set ?? throw new FormatException("通用分类写确认数据集解码失败");
    }

    /// <summary>收集同 RII 的通用分类响应数据集（跨 NGD 续帧）。</summary>
    private List<GenericDataSet103> CollectGenericSets(byte address, byte rii)
    {
        var sets = new List<GenericDataSet103>();
        foreach (var frame in DrainClass1(address, asdu =>
            asdu.TypeId == TypeId103.GenericData &&
            asdu.Data.Length >= 2 &&
            asdu.Data[0] == rii &&
            !Ngd103.Decode(asdu.Data[1]).Continuation))
        {
            if (frame.TypeId != TypeId103.GenericData || frame.Data.Length < 2 || frame.Data[0] != rii)
            {
                continue;
            }

            var offset = 2;
            while (GenericDataSet103.TryDecode(frame.Data.AsSpan(offset), out var set, out var consumed) && set is not null)
            {
                sets.Add(set);
                offset += consumed;
            }
        }

        return sets;
    }

    // —— 扰动数据传输 ——

    /// <summary>取回一条扰动记录的全部通道采样（选择→就绪→认可→一次值→逐通道数据段→结束）。</summary>
    public sealed record DisturbanceReadout(
        int Fan, int ChannelCount, int SampleCount, int IntervalUs, DateTime FaultTime,
        IReadOnlyList<ChannelReadout> Channels);

    public sealed record ChannelReadout(byte Acc, float Primary, float Secondary, float Factor, short[] Samples);

    /// <summary>
    /// 请求被记录的扰动表（TOO=24）。从站按帧容量分帧（24 条/帧），
    /// 客户端排空 1 级队列聚合全部 ASDU 23。
    /// </summary>
    public List<DisturbanceAsdu.DisturbanceEntry> RequestDisturbanceList(byte address)
    {
        SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceCommand, address, Too103.RequestDisturbanceList, 0, 0, Cot103.DisturbanceTransfer));
        var entries = new List<DisturbanceAsdu.DisturbanceEntry>();
        foreach (var frame in DrainClass1(address, _ => false))
        {
            if (frame.TypeId == TypeId103.DisturbanceList)
            {
                entries.AddRange(DisturbanceAsdu.DecodeDisturbanceList(frame));
            }
        }

        return entries;
    }

    /// <summary>
    /// 完整取回一条扰动：ASDU 24 选择（TOO=1）→ 就绪（TI=26）→ 认可（TI=25 TOO=64）
    /// → 扰动数据（TOO=2，通道一次值 TI=27）→ 逐通道数据段（TOO=8，TI=30）至通道终止（TI=31）。
    /// </summary>
    public DisturbanceReadout ReadDisturbance(byte address, int fan)
    {
        // 1. 选择故障 → 传输准备就绪（就绪帧固定 15 字节数据区）
        SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceCommand, address, Too103.SelectFault, fan, 0, Cot103.DisturbanceTransfer));
        var ready = DrainFirst(address, TypeId103.DisturbanceReady);
        if (ready.Data.Length < 15)
        {
            throw new FormatException($"传输就绪帧数据区不足 15 字节（{ready.Data.Length}），装置实现异常");
        }

        var channelCount = ready.Data[6];
        var sampleCount = ready.Data[7] | (ready.Data[8] << 8);
        var intervalUs = ready.Data[9] | (ready.Data[10] << 8);
        var faultTime = Cp32Time2a103.Decode(ready.Data.AsSpan(11, 4)).ToDateTime();

        // 2. 认可就绪
        SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceAck, address, Too103.PositiveAck, fan, 0, Cot103.DisturbanceTransfer));

        // 3. 请求扰动数据 → 每通道一次值（一次值帧固定 17 字节数据区）
        SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceCommand, address, Too103.RequestDisturbanceData, fan, 0, Cot103.DisturbanceTransfer));
        var ratings = new Dictionary<byte, (float Primary, float Secondary, float Factor)>();
        for (var i = 0; i < channelCount; i++)
        {
            var rating = DrainFirst(address, TypeId103.ChannelRating);
            if (rating.Data.Length < 17)
            {
                throw new FormatException($"通道一次值帧数据区不足 17 字节（{rating.Data.Length}），装置实现异常");
            }

            var acc = rating.Data[4];
            ratings[acc] = (
                BitConverter.ToSingle(rating.Data, 5),
                BitConverter.ToSingle(rating.Data, 9),
                BitConverter.ToSingle(rating.Data, 13));
        }

        // 4. 逐通道取数据段至通道终止（TOO=35 且 FAN/ACC 匹配才终止——
        //    TI=31 亦被扰动结束（32）/标签结束（38）复用，宽匹配会提前截断）
        var channels = new List<ChannelReadout>();
        foreach (var acc in ratings.Keys)
        {
            SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
                TypeId103.DisturbanceCommand, address, Too103.RequestChannel, fan, acc, Cot103.DisturbanceTransfer));
            var segments = new List<DisturbanceAsdu.ChannelDataSegment>();
            var endReached = false;
            while (!endReached)
            {
                foreach (var frame in DrainClass1(address, a => IsChannelEnd(a, fan, acc)))
                {
                    if (frame.TypeId == TypeId103.ChannelData)
                    {
                        segments.Add(DisturbanceAsdu.DecodeChannelData(frame));
                    }
                    else if (IsChannelEnd(frame, fan, acc))
                    {
                        endReached = true;
                    }
                }

                if (!endReached)
                {
                    throw new TimeoutException($"FAN={fan} ACC={acc} 通道数据未收到终止帧");
                }
            }

            if (segments.Count == 0)
            {
                throw new FormatException($"FAN={fan} ACC={acc} 通道终止前未收到任何数据段");
            }

            var samples = new short[sampleCount];
            foreach (var segment in segments)
            {
                var startIndex = segment.FirstElement - 1;
                if (startIndex < 0 || startIndex + segment.Values.Length > samples.Length)
                {
                    throw new FormatException(
                        $"FAN={fan} ACC={acc} 数据段范围越界：首元素 {segment.FirstElement}，" +
                        $"{segment.Values.Length} 点，总采样 {samples.Length}");
                }

                Array.Copy(segment.Values, 0, samples, startIndex, segment.Values.Length);
            }

            var (primary, secondary, factor) = ratings[acc];
            channels.Add(new ChannelReadout(acc, primary, secondary, factor, samples));
        }

        return new DisturbanceReadout(fan, channelCount, sampleCount, intervalUs, faultTime, channels);
    }

    /// <summary>通道终止判定：TI=31 且 TOO=35（通道结束）且 FAN/ACC 匹配本通道。</summary>
    private static bool IsChannelEnd(Asdu103 asdu, int fan, byte acc)
    {
        return asdu.TypeId == TypeId103.ChannelEnd &&
            asdu.Data.Length >= 5 &&
            asdu.Data[0] == Too103.EndChannel &&
            (asdu.Data[2] | (asdu.Data[3] << 8)) == fan &&
            asdu.Data[4] == acc;
    }

    /// <summary>召唤 1 级直至出现指定类型帧；忽略队列中其他无关帧（标识等残留）。</summary>
    private Asdu103 DrainFirst(byte address, byte typeId)
    {
        var frame = DrainClass1(address, asdu => asdu.TypeId == typeId)
            .FirstOrDefault(asdu => asdu.TypeId == typeId);
        return frame ?? throw new TimeoutException($"未收到 ASDU {typeId} 响应");
    }

    /// <summary>
    /// 一般命令（TI=20）：FUN/INF 目标 + DCO（1 分/2 合）。返回链路确认帧；
    /// 命令返送（TI=1，COT=远方操作）经 1 级召唤收取。
    /// </summary>
    public Frame103 SendGeneralCommand(byte address, byte fun, byte inf, int dco)
    {
        return SendConfirmed(address, Asdu103.GeneralCommand(address, fun, inf, dco, rii: 0));
    }

    /// <summary>召唤 2 级收齐循环测量（TI=9），返回 (FUN, INF, 工程值) 列表；无测点返回空表。</summary>
    public List<(int Fun, int Inf, short Value)> ReadMeasurements(byte address)
    {
        var result = new List<(int, int, short)>();
        for (var i = 0; i < 256; i++)
        {
            var response = RequestClass2(address);
            if (response.Format != Frame103Format.VariableLength ||
                !Asdu103.TryDecode(response.Asdu, out var asdu, out _) || asdu is null ||
                asdu.TypeId != TypeId103.Measurand)
            {
                break;
            }

            result.Add((asdu.Fun, asdu.Inf, Asdu103.DecodeMeasurement(asdu)));
        }

        return result;
    }

    /// <summary>请求扰动标签（TOO=16）：返回标签列表 (FUN, INF, DPI)。</summary>
    public List<(byte Fun, byte Inf, int Dpi)> ReadDisturbanceTags(byte address, int fan)
    {
        SendConfirmed(address, DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceCommand, address, Too103.RequestTags, fan, 0, Cot103.DisturbanceTransfer));
        var tags = DrainFirst(address, TypeId103.DisturbanceTags);
        var result = new List<(byte, byte, int)>();
        for (var offset = 5; offset + 3 <= tags.Data.Length; offset += 3)
        {
            result.Add((tags.Data[offset], tags.Data[offset + 1], tags.Data[offset + 2] & 0x03));
        }

        return result;
    }

    private Frame103 Transact(byte[] request)
    {
        _responses.Clear();
        _stream!.Write(request);
        var deadline = Environment.TickCount64 + TimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_responses.TryDequeue(out var frame))
            {
                return frame;
            }

            Thread.Sleep(10);
        }

        // 超时即清已到的迟到帧：否则残留响应会被下一事务当作自己的答案
        _responses.Clear();
        throw new TimeoutException($"链路服务超时（{TimeoutMs}ms）：请求 {Convert.ToHexString(request)}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream?.Close();
        _client.Close();
        _portOwner?.Dispose();
    }
}
