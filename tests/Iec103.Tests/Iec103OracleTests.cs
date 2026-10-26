using ArIEC103.Master.Slave;
using Iec103;
using Iec103.Tests.Oracle;

namespace Iec103.Tests;

/// <summary>
/// 互操作对拍：IecTestbench 主站（Iec103Client）↔ ArIEC103 从站模拟器（第三方独立实现，
/// Apache-2.0，vendored 于 Oracle/ArIEC103）。经 TCP 承载双方 FT1.2 字节流互通，
/// 校验复位/校时/总召唤/二级轮询/一般命令/保护事件全链路行为一致性。
/// </summary>
public class Iec103OracleTests
{
    /// <summary>后台从站（TCP 传输）+ 我方主站。</summary>
    private sealed class OraclePair : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpServerByteTransport _transport;

        public Iec103Client Client { get; }

        public OraclePair(Func<Iec103SlaveSimulatorSettings>? createSettings = null)
        {
            var settings = createSettings?.Invoke() ?? new Iec103SlaveSimulatorSettings
            {
                EnableProtectionBehavior = false,
                TurnaroundDelayMs = 0,
                ResponseTimeoutMs = 500,
            };

            _transport = new TcpServerByteTransport();
            var session = new Iec103SlaveSimulatorSession(settings, _transport);

            Client = new Iec103Client("127.0.0.1", _transport.Port);
            Client.Connect();

            // RunAsync 在传输上 accept 已完成握手的连接并进入读循环
            _ = session.RunAsync(_cts.Token);
            var openDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!_transport.IsOpen && DateTime.UtcNow < openDeadline)
            {
                Thread.Sleep(20);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            Client.Dispose();
            _transport.Dispose();
        }
    }

    [Fact]
    public void 对拍_复位链路回ACK()
    {
        using var pair = new OraclePair();
        var ack = pair.Client.ResetLink(1);

        Assert.Equal(Frame103Format.FixedLength, ack.Format);
        var link = LinkControl103.FromByte(ack.Control);
        Assert.Equal((byte)SecondaryFunction103.Ack, link.Function);
        Assert.False(link.IsPrimary);
    }

    [Fact]
    public void 对拍_时间同步回ACK()
    {
        using var pair = new OraclePair();
        pair.Client.ResetLink(1);

        var ack = pair.Client.SendClockSync(1, DateTime.Now);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);
    }

    [Fact]
    public void 对拍_总召唤_收齐快照并以GIEnd终止()
    {
        using var pair = new OraclePair();
        var client = pair.Client;
        client.ResetLink(1);

        var ack = client.SendGeneralInterrogation(1);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);

        var frames = client.DrainClass1UntilGiEnd(1);

        // ArIEC103 GI 快照：6×DPI 时标事件 + 3×测量 + 标识 + GI END
        var dpiEvents = frames.Where(f => f.TypeId == TypeId103.TimeTaggedMessage).ToList();
        var measurands = frames.Where(f => f.TypeId == TypeId103.Measurand).ToList();
        var identification = frames.Where(f => f.TypeId == TypeId103.Identification).ToList();
        var giEnd = frames.Where(f => f.TypeId == TypeId103.GeneralInterrogationEnd).ToList();

        Assert.Equal(6, dpiEvents.Count);
        Assert.All(dpiEvents, f => Assert.Equal(Cot103.GiStart, f.Cot));
        Assert.All(dpiEvents, f => Assert.Contains(f.Fun, new[] { 160, 161 }));
        // DPI 在/分状态：三相 pickup 与 trip 均为 OFF（快照时无故障）
        Assert.All(dpiEvents, f => Assert.Equal(1, f.Data[0] & 0x03));

        Assert.Equal(3, measurands.Count);
        Assert.All(measurands, f => Assert.Equal(144, f.Fun));
        Assert.All(measurands, f => Assert.Equal(Cot103.GiStart, f.Cot));

        var ident = Assert.Single(identification);
        Assert.Equal(3, ident.Cot);
        Assert.Contains("ARIEC103", System.Text.Encoding.ASCII.GetString(ident.Data));

        var end = Assert.Single(giEnd);
        Assert.Equal(Cot103.GiStop, end.Cot);
        Assert.Equal(255, end.Fun);

        // 队列取空：再召唤 1 级回无数据帧
        var empty = client.RequestClass1(1);
        Assert.Equal((byte)SecondaryFunction103.RespondNoData, LinkControl103.FromByte(empty.Control).Function);
    }

    [Fact]
    public void 对拍_二级召唤返回动画电流测量()
    {
        using var pair = new OraclePair();
        var client = pair.Client;
        client.ResetLink(1);

        var response = client.RequestClass2(1);
        Assert.Equal(Frame103Format.VariableLength, response.Format);
        Assert.True(Asdu103.TryDecode(response.Asdu, out var asdu, out _));
        Assert.Equal(TypeId103.Measurand, asdu!.TypeId);
        Assert.Equal(Cot103.Cyclic, asdu.Cot);
        Assert.Equal(144, asdu.Fun);
        // 动画电流幅值：110±12A × 100（short 编码）
        var value = Asdu103.DecodeMeasurement(asdu);
        Assert.InRange(value, 90 * 100, 140 * 100);
    }

    [Fact]
    public void 对拍_一般命令保护复位回ACK()
    {
        using var pair = new OraclePair();
        var client = pair.Client;
        client.ResetLink(1);

        // ArIEC103 默认复位命令 FUN=255 INF=19
        var ack = client.SendGeneralCommand(1, fun: 255, inf: 19, dco: 1);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);
    }

    [Fact]
    public void 对拍_保护启动与跳闸事件经一级召唤收取()
    {
        using var pair = new OraclePair(() => new Iec103SlaveSimulatorSettings
        {
            EnableProtectionBehavior = true,
            InitialFaultDelaySeconds = 1,
            TripDelayMs = 200,
            AutoResetSeconds = 999,
            FaultRepeatDelaySeconds = 999,
            TurnaroundDelayMs = 0,
            ResponseTimeoutMs = 500,
        });
        var client = pair.Client;
        client.ResetLink(1);

        // 1s 后启动（pickup），200ms 后跳闸（trip）；等待两个事件入队
        Thread.Sleep(1800);

        var pickupFrame = client.RequestClass1(1);
        Assert.True(Asdu103.TryDecode(pickupFrame.Asdu, out var pickup, out _));
        Assert.Equal(TypeId103.TimeTaggedMessage, pickup!.TypeId);
        Assert.Equal(Cot103.Spontaneous, pickup.Cot);
        Assert.Equal(160, pickup.Fun); // FUN=160 启动
        Assert.Equal(2, pickup.Data[0] & 0x03); // ON

        // 从站的保护状态机由收帧驱动：trip 定时（200ms）自下一次帧处理起算，
        // 轮询间隔需大于该定时才能在后续召唤中取到跳闸事件
        Thread.Sleep(400);
        var tripFrame = client.RequestClass1(1);
        Assert.True(Asdu103.TryDecode(tripFrame.Asdu, out var trip, out _));
        Assert.Equal(161, trip!.Fun); // FUN=161 跳闸
        Assert.Equal(Cot103.Spontaneous, trip.Cot);
    }

    [Fact]
    public async Task 对拍_坏校验帧被主站舍弃并超时()
    {
        using var pair = new OraclePair(() => new Iec103SlaveSimulatorSettings
        {
            EnableProtectionBehavior = false,
            TurnaroundDelayMs = 0,
            ResponseTimeoutMs = 500,
            BadChecksumMode = true,
        });
        pair.Client.TimeoutMs = 500;

        // 从站固定发坏校验帧：主站分帧器舍弃，链路服务超时
        await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() => pair.Client.ResetLink(1)));
    }
}
