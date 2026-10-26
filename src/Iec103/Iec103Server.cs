using System.Net;
using System.Net.Sockets;

namespace Iec103;

/// <summary>线路帧踪迹：方向 + 十六进制（GUI 报文窗）。</summary>
public sealed record FrameTrace(bool Sent, string Hex);

/// <summary>
/// IEC 60870-5-103 从站（TCP 或串口承载 FT1.2 字节流，帧层一致；
/// TCP 对应网络化 103/串口服务器透传，串口对应 RS-485/RS-232 原生接线）。
/// 非平衡式：仅响应主站链路服务。
/// 复位（RESET CU）→ 清 1 级队列并入队兼容/专用标识（TI=5，COT=4/5）；
/// 时间同步（TI=6）→ 更新站时钟并入队回读帧（FUN=255，COT=8）；
/// 总召唤（TI=7，FUN=255 INF=0）→ 入队全部监视点（TI=1，COT=11）与终止帧（TI=8，COT=10）；
/// 数据经 1 级召唤（FC=10）逐帧取出，ACD=1 提示队列非空。
/// </summary>
public sealed class Iec103Server : IDisposable
{
    private readonly Workspace103Model _workspace;
    private readonly int _port;
    private readonly string _bindAddress;
    private readonly Transport103Model? _serialTransport;
    private TcpListener? _listener;
    private bool _streamStarted;
    private readonly List<Session> _sessions = [];
    private readonly object _sessionsLock = new();
    private CancellationTokenSource? _cts;

    public int Port => _port;

    public event Action<string>? Log;

    /// <summary>主站接入/断开（对端端点字符串；accept/会话收尾线程触发）。</summary>
    public event Action<string>? ClientConnected;

    public event Action<string>? ClientDisconnected;

    /// <summary>线路帧收发踪迹（会话 IO 线程触发，GUI 报文窗数据源）。</summary>
    public event Action<FrameTrace>? FrameTraced;

    /// <summary>TCP 监听模式（多主站连接，绑定地址可控）。</summary>
    public Iec103Server(Workspace103Model workspace, int port, string bindAddress = "127.0.0.1")
    {
        _workspace = workspace;
        _port = port;
        _bindAddress = bindAddress;
    }

    /// <summary>
    /// 串口模式（RS-485/RS-232 单总线一条字节流；工作区内多站按链路地址区分，
    /// 非本站地址帧静默忽略）。串口打开失败（占用/不存在）时 Start 抛出，由调用方提示。
    /// </summary>
    public Iec103Server(Workspace103Model workspace, Transport103Model serialTransport)
    {
        _workspace = workspace;
        _serialTransport = serialTransport;
        _bindAddress = "serial";
    }

    public void Start()
    {
        // 幂等：重复启动会泄漏监听器/串口与旧 CTS
        if (_listener is not null || _streamStarted)
        {
            return;
        }

        if (_serialTransport is not null)
        {
            var channel = Iec103Transport.OpenSerial(_serialTransport);
            StartStream(channel.Stream, channel.Port, DescribeSerial(_serialTransport));
            var points = _workspace.Stations.Sum(s => s.Points.Count);
            Log?.Invoke($"已加载 {_workspace.Stations.Count} 个站点：{points} 个监视点");
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(_bindAddress), _port);
        _listener.Start();
        _ = AcceptLoop(_cts.Token);
        Log?.Invoke($"103 从站监听 {_bindAddress}:{_port}");
        var tcpPoints = _workspace.Stations.Sum(s => s.Points.Count);
        Log?.Invoke($"已加载 {_workspace.Stations.Count} 个站点：{tcpPoints} 个监视点");
    }

    private static string DescribeSerial(Transport103Model transport)
    {
        var parity = transport.Parity switch
        {
            "none" => "N",
            "odd" => "O",
            _ => "E",
        };
        return $"{transport.PortName} {transport.BaudRate}-{transport.DataBits}{parity}-{transport.StopBits}";
    }

    /// <summary>
    /// 在给定字节流上运行单个从站会话。Start 的串口分支内部走此路径；
    /// 测试可直接注入内存管道对拍（无物理串口环境）。
    /// </summary>
    /// <param name="stream">承载 FT1.2 字节流的通道。</param>
    /// <param name="owner">通道宿主（SerialPort 等），会话结束/Dispose 时释放。</param>
    /// <param name="remoteName">诊断端点名（日志/断线事件用）。</param>
    public void StartStream(Stream stream, IDisposable? owner = null, string remoteName = "stream")
    {
        if (_listener is not null || _streamStarted)
        {
            throw new InvalidOperationException("服务已启动，不能再附加流会话");
        }

        _cts ??= new CancellationTokenSource();
        _streamStarted = true;
        var session = new Session(
            stream, () => owner?.Dispose(), _workspace,
            message => Log?.Invoke(message), (sent, hex) => FrameTraced?.Invoke(new FrameTrace(sent, hex)));
        lock (_sessionsLock)
        {
            _sessions.Add(session);
        }

        session.Closed += () =>
        {
            lock (_sessionsLock)
            {
                _sessions.Remove(session);
            }

            Log?.Invoke($"串行会话结束（{remoteName}；串口/通道已释放，重新启动可恢复）");
            ClientDisconnected?.Invoke(remoteName);
        };
        _ = session.Run(_cts.Token);
        Log?.Invoke($"103 从站串行通道就绪：{remoteName}");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Dispose 竞态：监听器已随服务停止关闭
                break;
            }

            var session = new Session(
                client.GetStream(), client.Close, _workspace, message => Log?.Invoke(message),
                (sent, hex) => FrameTraced?.Invoke(new FrameTrace(sent, hex)));
            lock (_sessionsLock)
            {
                _sessions.Add(session);
            }

            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            session.Closed += () =>
            {
                lock (_sessionsLock)
                {
                    _sessions.Remove(session);
                }

                ClientDisconnected?.Invoke(remote);
            };
            _ = session.Run(token);
            ClientConnected?.Invoke(remote);
            Log?.Invoke($"主站接入 {remote}");
        }
    }

    /// <summary>
    /// 保护事件上送（TI=2，带相对时标与故障序号，COT=3 突发）到全部会话的 1 级队列。
    /// 用于故障期间的保护动作信号（专用范围 FUN 160-255）。
    /// </summary>
    public void SendProtectionEvent(int address, int fun, int inf, int dpi, int relativeTimeMs, int fan)
    {
        var station = _workspace.Stations.FirstOrDefault(s => s.Address == address)
            ?? throw new InvalidOperationException($"未知站点地址 {address}");

        var asdu = Asdu103.ProtectionEvent(
            (byte)station.Address, (byte)fun, (byte)inf, DpiQuality103.FromDpi(dpi),
            relativeTimeMs, fan, Cp56Time2a103.FromDateTime(DateTime.Now), Cot103.Spontaneous);

        List<Session> snapshot;
        lock (_sessionsLock)
        {
            snapshot = [.. _sessions];
        }

        foreach (var session in snapshot)
        {
            session.EnqueueClass1(asdu);
        }

        Log?.Invoke($"保护事件：FUN={fun} INF={inf} DPI={dpi} RET={relativeTimeMs}ms FAN={fan} → {snapshot.Count} 个连接");
    }

    /// <summary>突发上送一个监视点（TI=1，COT=3）到全部会话的 1 级队列。</summary>
    public void SendSpontaneous(int address, Iec103PointModel point)
    {
        var station = _workspace.Stations.FirstOrDefault(s => s.Address == address)
            ?? throw new InvalidOperationException($"未知站点地址 {address}");

        var asdu = Asdu103.TimeTaggedMessage(
            (byte)station.Address, (byte)point.Fun, (byte)point.Inf,
            DpiQuality103.FromDpi(point.Dpi), Cp56Time2a103.FromDateTime(DateTime.Now), Cot103.Spontaneous);

        List<Session> snapshot;
        lock (_sessionsLock)
        {
            snapshot = [.. _sessions];
        }

        foreach (var session in snapshot)
        {
            session.EnqueueClass1(asdu);
        }

        Log?.Invoke($"突发上送：FUN={point.Fun} INF={point.Inf} DPI={point.Dpi} → {snapshot.Count} 个连接");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        List<Session> snapshot;
        lock (_sessionsLock)
        {
            snapshot = [.. _sessions];
        }

        foreach (var session in snapshot)
        {
            session.Close();
        }
    }

    /// <summary>单主站通道会话（TCP 连接或串口总线）：独立 1 级队列与站时钟。</summary>
    private sealed class Session
    {
        private readonly Stream _stream;
        private readonly Action _close;
        private readonly Workspace103Model _workspace;
        private readonly Action<string> _log;
        private readonly Action<bool, string> _trace;
        private readonly FrameStreamParser103 _parser = new();
        private readonly Queue<byte[]> _class1Queue = new();
        private readonly object _queueLock = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private DateTime _stationClock = DateTime.Now;
        private DateTime _stationClockSetAt = DateTime.Now;

        /// <summary>装置当前时钟：校时基准 + 实际流逝时间（校时后事件时戳不再用 PC 墙钟）。</summary>
        private DateTime StationNow => _stationClock + (DateTime.Now - _stationClockSetAt);

        /// <summary>已传完的扰动通道（FAN → ACC 集合），齐全后发送扰动传输结束。</summary>
        private readonly Dictionary<int, HashSet<byte>> _transferredChannels = [];

        /// <summary>2 级循环测量队列（一轮快照）。</summary>
        private readonly Queue<byte[]> _class2Queue = new();

        /// <summary>2 级一轮进行中（取空后先回无数据帧，再请求开新一轮）。</summary>
        private bool _class2RoundActive;

        public event Action? Closed;

        public Session(Stream stream, Action close, Workspace103Model workspace, Action<string> log, Action<bool, string> trace)
        {
            _stream = stream;
            _close = close;
            _workspace = workspace;
            _log = log;
            _trace = trace;
            _parser.FrameRejected += (hex, reason) =>
            {
                _trace(false, hex);
                _log($"舍弃坏帧 {hex[..Math.Min(32, hex.Length)]}…: {reason}");
            };
        }

        public void EnqueueClass1(Asdu103 asdu)
        {
            lock (_queueLock)
            {
                _class1Queue.Enqueue(asdu.Encode());
            }
        }

        public void Close()
        {
            try
            {
                _close();
            }
            catch (Exception)
            {
                // 连接已断开时忽略
            }
        }

        public async Task Run(CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await _stream.ReadAsync(buffer, token);
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
                        await HandleFrame(frame);
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
                _log($"会话异常终止: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Closed?.Invoke();
                Close();
            }
        }

        private async Task HandleFrame(Frame103 frame)
        {
            var control = LinkControl103.FromByte(frame.Control);
            var hex = Convert.ToHexString(frame.ToBytes());
            _trace(false, hex);
            _log($"收到帧 {hex}（{control}）");

            // 地址校验：非本站地址且非广播（255）的帧忽略
            var station = _workspace.Stations.FirstOrDefault(s => s.Address == frame.Address);
            if (station is null && frame.Address != 255)
            {
                _log($"忽略未知链路地址 {frame.Address} 的帧（{control}）");
                return;
            }

            if (frame.Format == Frame103Format.SingleChar)
            {
                return;
            }

            if (!control.IsPrimary)
            {
                _log($"收到非主站帧（{control}），忽略");
                return;
            }

            switch ((PrimaryFunction103)control.Function)
            {
                case PrimaryFunction103.ResetCu:
                    HandleResetCu(station);
                    if (frame.Address != 255)
                    {
                        await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.Ack, acd: HasClass1()).ToByte(), frame.Address));
                    }

                    break;

                case PrimaryFunction103.ResetFcb:
                    if (frame.Address != 255)
                    {
                        await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.Ack, acd: HasClass1()).ToByte(), frame.Address));
                    }

                    break;

                case PrimaryFunction103.RequestLinkStatus:
                    await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.LinkStatus, acd: HasClass1()).ToByte(), frame.Address));
                    break;

                case PrimaryFunction103.RequestClass1:
                    await RespondFromClass1(frame.Address);
                    break;

                case PrimaryFunction103.RequestClass2:
                    await RespondFromClass2(frame.Address, station);
                    break;

                case PrimaryFunction103.SendConfirmed:
                case PrimaryFunction103.SendNoReply:
                    await HandleUserData(frame, station);
                    break;

                default:
                    _log($"未支持的主站功能码 {control.Function}（0x{frame.Control:X2}），回 NACK");
                    await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.Nack, acd: HasClass1()).ToByte(), frame.Address));
                    break;
            }
        }

        private void HandleResetCu(Iec103StationModel? station)
        {
            lock (_queueLock)
            {
                _class1Queue.Clear();
            }

            // 复位后应答标识报文（兼容/专用范围），与 E8000 实测行为一致（两帧，COT=4/5）
            if (station is not null)
            {
                var ca = (byte)station.Address;
                EnqueueClass1(Asdu103.Identification(ca, privateRange: false, station.CompatIdent));
                EnqueueClass1(Asdu103.Identification(ca, privateRange: true, station.PrivateIdent));
                _log($"复位通信单元：地址 {station.Address}，标识已入 1 级队列");
            }
            else
            {
                _log("复位通信单元（广播地址）");
            }
        }

        private async Task HandleUserData(Frame103 frame, Iec103StationModel? station)
        {
            var isBroadcast = frame.Address == 255;
            if (!Asdu103.TryDecode(frame.Asdu, out var asdu, out var error) || asdu is null)
            {
                _log($"ASDU 解码失败：{error}，回 NACK");
                if (!isBroadcast)
                {
                    await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.Nack, acd: HasClass1()).ToByte(), frame.Address));
                }

                return;
            }

            var handled = asdu.TypeId switch
            {
                TypeId103.ClockSync => HandleClockSync(asdu, station),
                TypeId103.GeneralInterrogation => HandleGeneralInterrogation(asdu, station),
                TypeId103.GenericCommand when asdu.Cot == Cot103.GenericRead => HandleGenericRead(asdu, station),
                TypeId103.GenericData when asdu.Cot == Cot103.GenericWrite => HandleGenericWrite(asdu, station),
                TypeId103.DisturbanceCommand when asdu.Cot == Cot103.DisturbanceTransfer => HandleDisturbanceCommand(asdu, station),
                TypeId103.DisturbanceAck when asdu.Cot == Cot103.DisturbanceTransfer => HandleDisturbanceAck(asdu),
                // COT 放宽 20/11/12：GPGate 等网关下发一般命令用 COT=12（老后端真机字节流），仅认 20 会静默丢弃
                TypeId103.GeneralCommand when asdu.Cot is Cot103.CommandAck or Cot103.RemoteOperation or Cot103.LocalOperation => HandleGeneralCommand(asdu, station),
                _ => false,
            };

            if (!handled)
            {
                _log($"未支持的 ASDU：TI={asdu.TypeId} FUN={asdu.Fun} INF={asdu.Inf} COT={asdu.Cot}");
            }

            if ((PrimaryFunction103)LinkControl103.FromByte(frame.Control).Function == PrimaryFunction103.SendConfirmed && !isBroadcast)
            {
                await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.Ack, acd: HasClass1()).ToByte(), frame.Address));
            }
        }

        private bool HandleClockSync(Asdu103 asdu, Iec103StationModel? station)
        {
            if (asdu.Data.Length < 7)
            {
                _log($"时间同步数据不足 7 字节（{asdu.Data.Length}）");
                return true;
            }

            var time = Cp56Time2a103.Decode(asdu.Data);
            _stationClock = time.ToDateTime();
            _stationClockSetAt = DateTime.Now;
            if (station is not null)
            {
                // 回读帧按 E8000 实测行为：FUN=255、COT=8、携带设备当前时钟
                EnqueueClass1(Asdu103.ClockSyncEcho(Cp56Time2a103.FromDateTime(_stationClock), (byte)station.Address));
                _log($"时间同步：地址 {station.Address} 时钟置为 {time}");
            }

            return true;
        }

        private bool HandleGeneralInterrogation(Asdu103 asdu, Iec103StationModel? station)
        {
            if (station is null)
            {
                _log($"总召唤：广播/未知地址 {asdu.Ca}，忽略");
                return true;
            }

            if (asdu.Fun != Asdu103.GlobalFun || asdu.Inf != 0)
            {
                _log($"总召唤：非全局 FUN/INF（FUN={asdu.Fun} INF={asdu.Inf}），忽略");
                return true;
            }

            var scn = asdu.Data.Length > 0 ? asdu.Data[0] : (byte)0;
            var ca = (byte)station.Address;
            var now = StationNow;
            foreach (var point in station.Points)
            {
                EnqueueClass1(Asdu103.TimeTaggedMessage(
                    ca, (byte)point.Fun, (byte)point.Inf,
                    DpiQuality103.FromDpi(point.Dpi), Cp56Time2a103.FromDateTime(now), Cot103.RemoteOperation));
            }

            EnqueueClass1(Asdu103.GeneralInterrogationEnd(ca, scn));
            _log($"总召唤：地址 {station.Address} SCN={scn}，入队 {station.Points.Count} 点 + 终止帧");
            return true;
        }

        private bool HandleGenericRead(Asdu103 asdu, Iec103StationModel? station)
        {
            if (station is null)
            {
                _log($"通用分类读：广播/未知地址 {asdu.Ca}，忽略");
                return true;
            }

            if (asdu.Data.Length < 2)
            {
                _log("通用分类读命令数据不足（RII+NOG）");
                return true;
            }

            var rii = asdu.Data[0];
            var count = asdu.Data[1];
            var requests = new List<(Gin103 Gin, byte Kod)>();
            for (var i = 0; i < count && 2 + i * 3 + 3 <= asdu.Data.Length; i++)
            {
                requests.Add((Gin103.Decode(asdu.Data.AsSpan(2 + i * 3, 2)), asdu.Data[2 + i * 3 + 2]));
            }

            var ca = (byte)station.Address;
            var sets = new List<GenericDataSet103>();
            var allValid = true;
            foreach (var (gin, kod) in requests)
            {
                // GIN 0000 = 读全目录；GIN 组.0 = 读全组（entry=0 为通配，点表条目从 1 编号）
                var targets = gin.IsZero
                    ? station.GenericEntries
                    : station.GenericEntries.Where(e => e.Group == gin.Group && (gin.Entry == 0 || e.Entry == gin.Entry)).ToList();
                if (targets.Count == 0)
                {
                    _log($"通用分类读：GIN {gin} 不存在");
                    allValid = false;
                    continue;
                }

                foreach (var entry in targets)
                {
                    var set = BuildEntryDataSet(entry.ToGin(), kod, entry);
                    if (set is not null)
                    {
                        sets.Add(set);
                    }
                    else
                    {
                        _log($"通用分类读：GIN {gin} KOD={kod} 未支持，跳过");
                        allValid = false;
                    }
                }
            }

            var cot = allValid ? Cot103.GenericReadValid : Cot103.GenericReadInvalid;
            foreach (var response in ChunkGeneric(ca, rii, cot, sets))
            {
                EnqueueClass1(response);
            }

            _log($"通用分类读：RII={rii} {requests.Count} 条请求 → {sets.Count} 数据集（COT={cot}）");
            return true;
        }

        private bool HandleGenericWrite(Asdu103 asdu, Iec103StationModel? station)
        {
            if (station is null)
            {
                _log($"通用分类写：广播/未知地址 {asdu.Ca}，忽略");
                return true;
            }

            // RII + NGD 至少 2 字节；短载荷命令帧拒绝而非异常终止会话
            if (asdu.Data.Length < 2)
            {
                _log($"通用分类写命令数据不足 {asdu.Data.Length} 字节（RII+NGD），忽略");
                return true;
            }

            if (!GenericDataSet103.TryDecode(asdu.Data.AsSpan(2), out var set, out _) || set is null)
            {
                _log("通用分类写命令数据集解码失败");
                return true;
            }

            var rii = asdu.Data[0];
            var ca = (byte)station.Address;
            var gin = set.Gin;
            var entry = station.GenericEntries.FirstOrDefault(e => e.Group == gin.Group && e.Entry == gin.Entry);
            if (entry is null)
            {
                EnqueueClass1(Asdu103.GenericWriteAck(ca, rii, gin, set.Kod, Grc103.UnknownGin, negative: true));
                _log($"通用分类写：GIN {gin} 不存在，回否定确认");
                return true;
            }

            if (entry.ReadOnly)
            {
                EnqueueClass1(Asdu103.GenericWriteAck(ca, rii, gin, set.Kod, Grc103.ReadOnlyEntry, negative: true));
                _log($"通用分类写：GIN {gin} 只读，回否定确认");
                return true;
            }

            var applied = ApplyWriteValue(entry, set);
            var grc = applied ? Grc103.Acknowledge : Grc103.NotAcknowledged;
            EnqueueClass1(Asdu103.GenericWriteAck(ca, rii, gin, set.Kod, grc, negative: !applied));
            _log($"通用分类写：GIN {gin}={set.DecodeValue() ?? "?"} → {(applied ? "认可" : "否定（类型不符）")}");
            return true;
        }

        private bool HandleDisturbanceCommand(Asdu103 asdu, Iec103StationModel? station)
        {
            if (station is null)
            {
                _log($"扰动传输：广播/未知地址 {asdu.Ca}，忽略");
                return true;
            }

            // TOO+TOV+FAN(2)+ACC 共 5 字节；短载荷命令帧拒绝而非异常终止会话
            if (asdu.Data.Length < 5)
            {
                _log($"扰动命令数据不足 {asdu.Data.Length} 字节（TOO+TOV+FAN+ACC），忽略");
                return true;
            }

            var (too, _, fan, acc) = DisturbanceAsdu.DecodeCommand(asdu);
            var ca = (byte)station.Address;
            // 扰动列表可能被控制台 fault 命令并发修改——锁内快照
            List<DisturbanceRecord103Model> disturbances;
            int disturbanceCount;
            lock (station.DisturbanceGate)
            {
                disturbances = [.. station.Disturbances];
                disturbanceCount = disturbances.Count;
            }

            var record = disturbances.FirstOrDefault(r => r.Fan == fan);
            switch (too)
            {
                case Too103.RequestDisturbanceList:
                    // 每条记录 10 字节，单帧容量 253-6 ASDU 头 → 24 条/帧分帧发送
                    const int listChunkSize = 24;
                    for (var offset = 0; offset < disturbanceCount; offset += listChunkSize)
                    {
                        var chunk = disturbances.Skip(offset).Take(listChunkSize).ToList();
                        EnqueueClass1(DisturbanceAsdu.DisturbanceList(ca, chunk));
                    }

                    if (disturbanceCount == 0)
                    {
                        // 空表也回一帧 VSQ=0，客户端据此得知无扰动
                        EnqueueClass1(DisturbanceAsdu.DisturbanceList(ca, []));
                    }

                    _log($"扰动表请求 → {disturbanceCount} 条记录（{(disturbanceCount + listChunkSize - 1) / listChunkSize} 帧）");
                    break;

                case Too103.SelectFault when record is not null:
                    EnqueueClass1(DisturbanceAsdu.TransferReady(ca, too, record, disturbanceCount));
                    _log($"选择故障 FAN={fan}：{record.Channels.Count} 通道 × {record.TotalSamples} 点，间隔 {record.SampleIntervalUs}µs");
                    _transferredChannels[fan] = [];
                    break;

                case Too103.RequestDisturbanceData when record is not null:
                    foreach (var channel in record.Channels)
                    {
                        EnqueueClass1(DisturbanceAsdu.ChannelRating(ca, too, fan, channel));
                    }

                    _log($"扰动数据请求 FAN={fan} → {record.Channels.Count} 条通道一次值");
                    break;

                case Too103.RequestChannel when record is not null:
                    var target = record.Channels.FirstOrDefault(c => c.Acc == acc);
                    if (target is null)
                    {
                        _log($"请求通道 FAN={fan} ACC={acc} 不存在");
                        break;
                    }

                    var samples = target.GenerateSamples();
                    const int chunkSize = 100;
                    for (var offset = 0; offset < samples.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, samples.Length - offset);
                        EnqueueClass1(DisturbanceAsdu.ChannelData(ca, too, fan, acc, samples, offset, count));
                    }

                    EnqueueClass1(DisturbanceAsdu.ChannelEnd(ca, Too103.EndChannel, fan, acc));
                    _log($"通道数据 FAN={fan} ACC={acc}：{samples.Length} 点（{Ceil(samples.Length, chunkSize)} 段）+ 通道终止");

                    var transferred = _transferredChannels.TryGetValue(fan, out var set)
                        ? set
                        : _transferredChannels[fan] = [];
                    _ = transferred.Add(acc);
                    if (transferred.Count >= record.Channels.Count)
                    {
                        EnqueueClass1(DisturbanceAsdu.ChannelEnd(ca, Too103.EndDisturbance, fan, Acc103.Global));
                        _log($"FAN={fan} 全部通道传完 → 扰动传输结束");
                    }

                    break;

                case Too103.RequestTags when record is not null:
                    // 标签 = 故障时刻动作的监视点（前 8 点），TAP 标签位置 0
                    var tags = station.Points
                        .Take(8)
                        .Select(p => ((byte)p.Fun, (byte)p.Inf, p.Dpi))
                        .ToList();
                    EnqueueClass1(DisturbanceAsdu.DisturbanceTags(ca, fan, tap: 0, tags));
                    EnqueueClass1(DisturbanceAsdu.ChannelEnd(ca, Too103.EndTags, fan, Acc103.Global));
                    _log($"标签请求 FAN={fan} → {tags.Count} 条标签 + 标签终止");
                    break;

                default:
                    _log($"扰动命令未支持/记录不存在：TOO={Too103.Describe(too)} FAN={fan} ACC={acc}");
                    break;
            }

            return true;
        }

        private bool HandleDisturbanceAck(Asdu103 asdu)
        {
            // TOO+TOV+FAN(2)+ACC 共 5 字节；短载荷拒绝而非异常终止会话
            if (asdu.Data.Length < 5)
            {
                _log($"扰动认可数据不足 {asdu.Data.Length} 字节，忽略");
                return true;
            }

            var (too, _, fan, _) = DisturbanceAsdu.DecodeCommand(asdu);
            _log($"扰动认可：FAN={fan} {Too103.Describe(too)}");
            return true;
        }

        private static int Ceil(int value, int divisor) => (value + divisor - 1) / divisor;

        private bool HandleGeneralCommand(Asdu103 asdu, Iec103StationModel? station)
        {
            if (station is null)
            {
                _log($"一般命令：广播/未知地址 {asdu.Ca}，忽略");
                return true;
            }

            // DCO 1 字节必备；RII 可选（GPGate/老后端真机不带 RII，实测 1 字节信息体）
            if (asdu.Data.Length < 1)
            {
                _log($"一般命令数据不足 {asdu.Data.Length} 字节（DCO），忽略");
                return true;
            }

            var (dct, qu, rii) = Asdu103.DecodeGeneralCommand(asdu);
            var ca = (byte)station.Address;
            var point = station.Points.FirstOrDefault(p => p.Fun == asdu.Fun && p.Inf == asdu.Inf);

            // 命令值映射（DL/T 667 DCT）：1=分、2=合；0 兼容单点 SCS OFF=分；3=不确定不执行
            if (point is null || dct == 3)
            {
                // 未匹配或命令值不确定：负确认（TI=1，COT=远方操作，P/N=1）
                var negative = Asdu103.TimeTaggedMessage(
                    ca, asdu.Fun, asdu.Inf, DpiQuality103.FromDpi(0),
                    Cp56Time2a103.FromDateTime(StationNow), Cot103.RemoteOperation);
                EnqueueClass1(negative with { Negative = true });
                _log($"⚠ 一般命令拒绝：FUN={asdu.Fun} INF={asdu.Inf} DCT={dct} QU={qu} RII={rii}" +
                    (point is null ? "（点位不在模拟点表）" : "（命令值 3=不确定）") +
                    "；链路仍回 ACK——GPGate 类网关将判成功，值未变化");
                return true;
            }

            point.Dpi = dct is 1 or 0 ? 1 : 2;
            EnqueueClass1(Asdu103.TimeTaggedMessage(
                ca, (byte)point.Fun, (byte)point.Inf, DpiQuality103.FromDpi(point.Dpi),
                Cp56Time2a103.FromDateTime(StationNow), Cot103.RemoteOperation));
            _log($"ACK command applied FUN={point.Fun}, INF={point.Inf}, DPI={point.Dpi}" +
                $"（{(point.Dpi == 1 ? "分/OFF" : "合/ON")}，QU={qu} RII={rii}），返送已入 1 级队列");
            return true;
        }

        /// <summary>按条目类型应用写值；类型不符返回 false。</summary>
        private static bool ApplyWriteValue(GenericEntry103Model entry, GenericDataSet103 set)
        {
            switch (entry.DataType)
            {
                case "ascii" when set.Gdd.DataType == GddDataType103.Ascii:
                    entry.AsciiValue = (string?)set.DecodeValue() ?? string.Empty;
                    return true;
                case "uint" when set.Gdd.DataType == GddDataType103.UnsignedInt:
                case "int" when set.Gdd.DataType == GddDataType103.SignedInt:
                case "float" when set.Gdd.DataType == GddDataType103.ShortReal:
                    if (set.DecodeValue() is long integral)
                    {
                        entry.ActualValue = integral;
                    }
                    else if (set.DecodeValue() is float floating)
                    {
                        entry.ActualValue = floating;
                    }
                    else
                    {
                        return false;
                    }

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>按 KOD 构造条目数据集；未支持的 KOD 返回 null。</summary>
        private static GenericDataSet103? BuildEntryDataSet(Gin103 gin, byte kod, GenericEntry103Model entry)
        {
            switch (kod)
            {
                case Kod103.Description:
                    return GenericDataSet103.Ascii(gin, kod, entry.Description);
                case Kod103.ActualValue:
                case Kod103.DefaultValue:
                    return BuildValueSet(gin, kod, entry);
                case Kod103.Range:
                    return GenericDataSet103.Range(gin,
                        (float)(entry.MinValue ?? 0), (float)(entry.MaxValue ?? 0), (float)(entry.StepValue ?? 0));
                default:
                    return null;
            }
        }

        private static GenericDataSet103 BuildValueSet(Gin103 gin, byte kod, GenericEntry103Model entry)
        {
            return entry.DataType switch
            {
                "ascii" => GenericDataSet103.Ascii(gin, kod, entry.AsciiValue ?? string.Empty),
                "uint" => GenericDataSet103.Unsigned(gin, kod, (uint)(entry.ActualValue ?? 0), (byte)Math.Clamp(entry.IntWidth, 1, 4)),
                "int" => GenericDataSet103.Signed(gin, kod, (int)(entry.ActualValue ?? 0), (byte)Math.Clamp(entry.IntWidth, 1, 4)),
                _ => GenericDataSet103.Float(gin, kod, (float)(entry.ActualValue ?? 0)),
            };
        }

        /// <summary>数据集序列按 FT1.2 单帧字节预算分帧；除最后一帧外 NGD 后续标志置位。</summary>
        private static IEnumerable<Asdu103> ChunkGeneric(byte ca, byte rii, byte cot, List<GenericDataSet103> sets)
        {
            var budget = Frame103.MaxAsduLength - 8 - 2; // ASDU 头 8 字节 + RII + NGD
            var current = new List<GenericDataSet103>();
            var used = 0;
            foreach (var set in sets)
            {
                if (current.Count > 0 && used + set.EncodedSize > budget)
                {
                    yield return Asdu103.GenericDataAsdu(ca, rii, continuation: true, cot, current);
                    current = [];
                    used = 0;
                }

                current.Add(set);
                used += set.EncodedSize;
            }

            yield return Asdu103.GenericDataAsdu(ca, rii, continuation: false, cot, current);
        }

        private async Task RespondFromClass1(byte address)
        {
            byte[]? asdu = null;
            lock (_queueLock)
            {
                if (_class1Queue.Count > 0)
                {
                    asdu = _class1Queue.Dequeue();
                }
            }

            if (asdu is null)
            {
                await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.RespondNoData).ToByte(), address));
                return;
            }

            var control = LinkControl103.Secondary(SecondaryFunction103.RespondData, acd: HasClass1()).ToByte();
            await WriteFrame(Frame103.EncodeVariable(control, address, asdu));
        }

        private bool HasClass1()
        {
            lock (_queueLock)
            {
                return _class1Queue.Count > 0;
            }
        }

        /// <summary>
        /// 2 级召唤：循环测量（TI=9）逐帧应答。一轮语义：
        /// 首次请求填充快照队列 → 逐帧出队 → 取空后回一次无数据帧并复位 →
        /// 再请求开始新一轮（保证主站能感知一轮结束）。
        /// </summary>
        private async Task RespondFromClass2(byte address, Iec103StationModel? station)
        {
            if (station is not null && _class2Queue.Count == 0 && !_class2RoundActive && station.Measurements.Count > 0)
            {
                foreach (var measurement in station.Measurements)
                {
                    var value = (short)Math.Clamp((int)measurement.Value, short.MinValue, short.MaxValue);
                    _class2Queue.Enqueue(Asdu103.Measurement(address, (byte)measurement.Fun, (byte)measurement.Inf, value).Encode());
                }

                _class2RoundActive = true;
            }

            byte[]? asdu = null;
            if (_class2Queue.Count > 0)
            {
                asdu = _class2Queue.Dequeue();
            }

            if (asdu is null)
            {
                _class2RoundActive = false;
                await WriteFrame(Frame103.EncodeFixed(LinkControl103.Secondary(SecondaryFunction103.RespondNoData, acd: HasClass1()).ToByte(), address));
                return;
            }

            var control = LinkControl103.Secondary(SecondaryFunction103.RespondData, acd: HasClass1()).ToByte();
            await WriteFrame(Frame103.EncodeVariable(control, address, asdu));
        }

        private async Task WriteFrame(byte[] frame)
        {
            await _writeLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(frame);
                _trace(true, Convert.ToHexString(frame));
                _log($"发送帧 {Convert.ToHexString(frame)}");
            }
            catch (IOException)
            {
                // 主站断开时由读循环收尾
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
