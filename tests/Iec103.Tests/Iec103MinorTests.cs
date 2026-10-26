using Iec103;

namespace Iec103.Tests;

/// <summary>M3 收尾小项：TI=2 保护事件、TI=20 一般命令、TI=9 二级测量、ASDU 29 扰动标签。</summary>
public class Iec103MinorTests
{
    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    private static Iec103StationModel BuildStation()
    {
        return new Iec103StationModel
        {
            Address = 1,
            Points =
            [
                new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 2, Description = "断路器位置" },
            ],
            Measurements =
            [
                new Iec103MeasurementModel { Fun = 1, Inf = 1, Value = 5725, Description = "Ia" },
                new Iec103MeasurementModel { Fun = 1, Inf = 2, Value = -123, Description = "Ua" },
            ],
            Disturbances =
            [
                new DisturbanceRecord103Model
                {
                    Fan = 3,
                    Channels = [new DisturbanceChannel103Model { Acc = 1, SampleCount = 20 }],
                },
            ],
        };
    }

    [Fact]
    public void 保护事件编码_布局为DPI加RET加FAN加CP56()
    {
        var time = new Cp56Time2a103(1000, 1, 2, 3, 4, 26, false, false);
        var asdu = Asdu103.ProtectionEvent(
            ca: 1, fun: 160, inf: 5, dpi: DpiQuality103.FromDpi(2),
            relativeTimeMs: 0x0203, fan: 7, time: time, cot: Cot103.Spontaneous);
        Assert.Equal(TypeId103.TimeTaggedMessagePrivate, asdu.TypeId);
        Assert.Equal(12, asdu.Data.Length);
        Assert.Equal(2, asdu.Data[0] & 0x03);          // DPI 合
        Assert.Equal([0x03, 0x02], asdu.Data[1..3]);   // RET LE
        Assert.Equal([0x07, 0x00], asdu.Data[3..5]);   // FAN LE
        Assert.Equal(time.Encode(), asdu.Data[5..]);   // CP56

        var (ret, fan) = Asdu103.DecodeProtectionEvent(asdu);
        Assert.Equal((0x0203, 7), (ret, fan));
    }

    [Fact]
    public void 一般命令编码_命令值在bit01_QU在bit67()
    {
        var command = Asdu103.GeneralCommand(ca: 1, fun: 160, inf: 1, dco: 1, rii: 5);
        // TI=0x14(20) VSQ=0x81 COT=0x14(20) CA=1 FUN=A0 INF=01 DCO=0x01(bit0-1=01 分) RII=05
        Assert.Equal(Hex("14 81 14 01 A0 01 01 05"), command.Encode());

        var (dct, qu, rii) = Asdu103.DecodeGeneralCommand(command);
        Assert.Equal((1, 0, (byte)5), (dct, qu, rii));

        var on = Asdu103.GeneralCommand(1, 160, 1, 2, 0);
        Assert.Equal(0x02, on.Data[0]);
    }

    [Theory]
    [InlineData(0, 1)] // SCS OFF（bit0=0）→ 分
    [InlineData(1, 1)] // DCS 01 → 分
    [InlineData(2, 2)] // DCS 10 → 合
    public void 自环_一般命令命令值映射点位DPI(int dco, int expectedDpi)
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        var ack = client.SendGeneralCommand(1, fun: 160, inf: 1, dco: dco);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);

        var echo = DrainFirstAsdu(client, TypeId103.TimeTaggedMessage);
        Assert.Equal(Cot103.RemoteOperation, echo.Cot);
        Assert.False(echo.Negative);
        Assert.Equal(160, echo.Fun);
        Assert.Equal(1, echo.Inf);
        Assert.Equal(expectedDpi, echo.Data[0] & 0x03);
    }

    [Fact]
    public void 自环_一般命令无RII单字节DCO_按GPGate实帧执行()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        // GPGate 实测帧 ASDU：TI=20 VSQ=1 COT=12(当地操作) CA=1 FUN=160 INF=1 DCO=0x02（无 RII）
        var frame = client.SendConfirmed(1,
            new Asdu103(TypeId103.GeneralCommand, true, 1, Cot103.LocalOperation, false, 1, 160, 1, [0x02]));
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(frame.Control).Function);

        var echo = DrainFirstAsdu(client, TypeId103.TimeTaggedMessage);
        Assert.False(echo.Negative);
        Assert.Equal(Cot103.RemoteOperation, echo.Cot);
        Assert.Equal(2, echo.Data[0] & 0x03); // DCO bit0-1=10 → 合
    }

    [Fact]
    public void 自环_一般命令QU限定词不影响命令值()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        // QU=1（bit6-7）+ 合（bit0-1=10）：命令值应取 bit0-1 而非误读 bit6-7
        var frame = client.SendConfirmed(1,
            new Asdu103(TypeId103.GeneralCommand, true, 1, Cot103.CommandAck, false, 1, 160, 1, [0x42, 0]));
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(frame.Control).Function);

        var echo = DrainFirstAsdu(client, TypeId103.TimeTaggedMessage);
        Assert.False(echo.Negative);
        Assert.Equal(2, echo.Data[0] & 0x03); // 合
    }

    [Fact]
    public void 自环_一般命令不确定值3回否定返送()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        var ack = client.SendGeneralCommand(1, fun: 160, inf: 1, dco: 3);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function); // 链路仍 ACK

        var echo = DrainFirstAsdu(client, TypeId103.TimeTaggedMessage);
        Assert.True(echo.Negative);
        Assert.Equal(Cot103.RemoteOperation, echo.Cot);
    }

    [Fact]
    public void 自环_一般命令未知点位回否定返送()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        client.SendGeneralCommand(1, fun: 200, inf: 99, dco: 2);

        var echo = DrainFirstAsdu(client, TypeId103.TimeTaggedMessage);
        Assert.True(echo.Negative);
        Assert.Equal(Cot103.RemoteOperation, echo.Cot);
    }

    [Fact]
    public void 自环_二级召唤逐帧收齐循环测量后无数据()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);

        var measurements = client.ReadMeasurements(1);
        Assert.Equal([(1, 1, (short)5725), (1, 2, (short)-123)], measurements);

        // 收齐（无数据帧止）后，再请求开启新一轮轮询（首个测点数据帧）
        var nextRound = client.RequestClass2(1);
        Assert.Equal(Frame103Format.VariableLength, nextRound.Format);
    }

    [Fact]
    public void 自环_保护事件经一级召唤收取并解码RET与FAN()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var server = loopback.Server;
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        server.SendProtectionEvent(1, fun: 160, inf: 5, dpi: 2, relativeTimeMs: 1234, fan: 3);
        Thread.Sleep(100);

        var frame = client.RequestClass1(1);
        Assert.True(Asdu103.TryDecode(frame.Asdu, out var asdu, out _));
        Assert.Equal(TypeId103.TimeTaggedMessagePrivate, asdu!.TypeId);
        Assert.Equal(Cot103.Spontaneous, asdu.Cot);
        Assert.Equal((1234, 3), Asdu103.DecodeProtectionEvent(asdu));
    }

    [Fact]
    public void 自环_扰动标签请求返回监视点快照()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);
        client.RequestDisturbanceList(1);

        var tags = client.ReadDisturbanceTags(1, fan: 3);
        Assert.Single(tags);
        Assert.Equal((160, 1, 2), (tags[0].Fun, tags[0].Inf, tags[0].Dpi));

        // 标签终止帧随后可取
        var end = DrainFirstAsdu(client, TypeId103.ChannelEnd);
        Assert.Equal(Too103.EndTags, end.Data[0]);
    }

    private static Asdu103 DrainFirstAsdu(Iec103Client client, byte typeId)
    {
        var frame = client.DrainClass1(1, a => a.TypeId == typeId)
            .First(a => a.TypeId == typeId);
        return frame;
    }

    /// <summary>最小自环容器。</summary>
    private sealed class Loopback(Iec103StationModel station) : IDisposable
    {
        public Iec103Server Server { get; } = new(new Workspace103Model { Stations = [station] }, GetFreePort());

        public Iec103Client Client { get; private set; } = null!;

        public Loopback Start()
        {
            Server.Start();
            Client = new Iec103Client("127.0.0.1", Server.Port);
            Client.Connect();
            return this;
        }

        public void Dispose()
        {
            Client?.Dispose();
            Server.Dispose();
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
