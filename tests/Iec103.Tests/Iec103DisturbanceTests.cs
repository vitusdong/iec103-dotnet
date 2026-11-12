using Iec103;

namespace Iec103.Tests;

/// <summary>扰动数据传输测试：ASDU 23/24/26/27/30/31 编解码与自环全流程（TOO 驱动）。</summary>
public class Iec103DisturbanceTests
{
    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    private static Iec103StationModel BuildStation()
    {
        return new Iec103StationModel
        {
            Address = 1,
            Name = "扰动测试站",
            Disturbances =
            [
                new DisturbanceRecord103Model
                {
                    Fan = 7,
                    FaultTime = new DateTime(2026, 10, 17, 9, 30, 15, 500),
                    SampleIntervalUs = 1000,
                    Channels =
                    [
                        new DisturbanceChannel103Model { Acc = 1, Primary = 600, Secondary = 5, Factor = 120, SampleCount = 250, Amplitude = 0.3, FaultStartIndex = 100, FaultAmplitude = 0.9 },
                        new DisturbanceChannel103Model { Acc = 2, Primary = 600, Secondary = 5, Factor = 120, SampleCount = 250, Amplitude = 0.3, FaultStartIndex = 100, FaultAmplitude = 0.85 },
                        new DisturbanceChannel103Model { Acc = 4, Primary = 100, Secondary = 5, Factor = 20, SampleCount = 250, Amplitude = 0.1, FaultStartIndex = 0, FaultAmplitude = 0.1 },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void 扰动表命令编码布局正确()
    {
        var asdu = DisturbanceAsdu.DisturbanceCommand(
            TypeId103.DisturbanceCommand, ca: 1, Too103.RequestDisturbanceList, fan: 0x0102, acc: 0, Cot103.DisturbanceTransfer);
        // TI=0x18(24) VSQ=0x81 COT=0x1F(31) CA=01 FUN=FF INF=00
        // TOO=0x18(24) TOV=01 FAN=02 01 ACC=00
        Assert.Equal(Hex("18 81 1F 01 FF 00 18 01 02 01 00"), asdu.Encode());
    }

    [Fact]
    public void SOF与CP32编解码往返()
    {
        var sof = new Sof103(TripProtection: true, TripCommand: false, Test: true, EquipmentStart: true);
        Assert.Equal(sof, Sof103.Decode(sof.Encode()));
        // 7.2.6.24：TP=bit1、TM=bit2、TEST=bit3、OTEV=bit4
        Assert.Equal(0x0D, sof.Encode());
        Assert.True(Sof103.Decode(0x01).TripProtection);
        Assert.Equal(new Sof103(false, true, false, false), Sof103.Decode(0x02));
        Assert.Equal(new Sof103(false, false, false, false), Sof103.Decode(0xF0)); // RES 位忽略

        var time = new Cp32Time2a103(53992, 50, 9, Invalid: false, SummerTime: false);
        Assert.Equal([0xE8, 0xD2, 0x32, 0x09], time.Encode());
        var decoded = Cp32Time2a103.Decode(time.Encode());
        Assert.Equal((53992, 50, 9), (decoded.Millisecond, decoded.Minute, decoded.Hour));
    }

    [Fact]
    public void 扰动表ASDU23编码含FAN_SOF_CP56()
    {
        var station = BuildStation();
        var asdu = DisturbanceAsdu.DisturbanceList(1, station.Disturbances, fun: 160);
        Assert.Equal(TypeId103.DisturbanceList, asdu.TypeId);
        Assert.Equal(1, asdu.Count); // VSQ 数量 = 记录数（3 是通道数）
        // 7.3.1.11：VSQ 的 SQ=0（0x01），FUN=装置功能类型，INF=0
        Assert.Equal(Hex("17 01 1F 01 A0 00"), asdu.Encode()[..6]);
        Assert.Equal(Cot103.DisturbanceTransfer, asdu.Cot);
        Assert.Equal(10, asdu.Data.Length);
        Assert.Equal(7, asdu.Data[0] | (asdu.Data[1] << 8)); // FAN=7
        Assert.Equal(0x0B, asdu.Data[2]); // SOF: TP+TM+OTEV（记录默认状态）
        var entries = DisturbanceAsdu.DecodeDisturbanceList(asdu);
        Assert.Single(entries);
        Assert.Equal(7, entries[0].Fan);
        Assert.True(entries[0].Sof.TripProtection);
        Assert.Equal(new DateTime(2026, 10, 17, 9, 30, 15, 500), entries[0].FaultTime);
    }

    [Fact]
    public void 通道数据段编码与解码往返()
    {
        var samples = new short[25];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(i - 10);
        }

        var asdu = DisturbanceAsdu.ChannelData(1, Too103.RequestChannel, fan: 5, acc: 2, samples, offset: 20, count: 5);
        var segment = DisturbanceAsdu.DecodeChannelData(asdu);
        Assert.Equal(5, segment.Fan);
        Assert.Equal(2, segment.Acc);
        Assert.Equal(20, segment.FirstElement); // 首元素序号 = 偏移（NFE 从 0 起，7.2.6.9）
        Assert.Equal(0, DisturbanceAsdu.DecodeChannelData(
            DisturbanceAsdu.ChannelData(1, Too103.RequestChannel, fan: 5, acc: 2, samples, offset: 0, count: 5)).FirstElement);
        Assert.Equal(samples[20..25], segment.Values);
    }

    [Fact]
    public void 扰动自环_完整取回多通道采样()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1); // 兼容标识
        client.RequestClass1(1); // 专用标识

        var list = client.RequestDisturbanceList(1);
        var entry = Assert.Single(list);
        Assert.Equal(7, entry.Fan);
        Assert.True(entry.Sof.TripProtection);

        var readout = client.ReadDisturbance(1, 7);
        Assert.Equal(7, readout.Fan);
        Assert.Equal(3, readout.ChannelCount);
        Assert.Equal(250, readout.SampleCount);
        Assert.Equal(1000, readout.IntervalUs);
        // 故障时刻来自就绪帧 CP32（4 字节无日期，还原时取当天拼合）——只断言时刻部分
        Assert.Equal(new TimeSpan(9, 30, 15).Add(TimeSpan.FromMilliseconds(500)), readout.FaultTime.TimeOfDay);

        var il1 = readout.Channels.Single(c => c.Acc == 1);
        Assert.Equal(250, il1.Samples.Length);
        Assert.Equal(600f, il1.Primary);
        Assert.Equal(5f, il1.Secondary);
        Assert.Equal(120f, il1.Factor);

        // 采样与从站生成逻辑一致：故障前幅值 0.3，故障段（第 100 点起）超过 0.5
        Assert.All(il1.Samples[..100], s => Assert.True(Math.Abs(s) <= 0.35 * 32767));
        Assert.Contains(il1.Samples[100..], s => Math.Abs(s) > 0.5 * 32767);
    }

    [Fact]
    public void 扰动自环_通道归属与额定因数齐全()
    {
        using var loopback = new Loopback(BuildStation()).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);
        client.RequestDisturbanceList(1);

        var readout = client.ReadDisturbance(1, 7);
        Assert.Equal([1, 2, 4], readout.Channels.Select(c => (int)c.Acc).OrderBy(a => a));

        var neutral = readout.Channels.Single(c => c.Acc == 4);
        Assert.Equal(100f, neutral.Primary);
        Assert.Equal(20f, neutral.Factor);
        Assert.All(neutral.Samples, s => Assert.True(Math.Abs(s) <= 0.15 * 32767));
    }

    [Fact]
    public void 扰动自环_按7_4_7流程先标签后逐通道并逐个认可()
    {
        var station = BuildStation();
        station.Points = [new Iec103PointModel { Fun = 160, Inf = 84, Dpi = 2 }];
        using var loopback = new Loopback(station).Start();
        var client = loopback.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        var before = client.GetReceivedSnapshot().Count;
        var readout = client.ReadDisturbance(1, 7);
        Assert.Equal([1, 2, 4], readout.Channels.Select(c => (int)c.Acc));
        var tag = Assert.Single(readout.Tags);
        Assert.Equal(new Iec103Client.DisturbanceTag(0, 160, 84, 2), tag);

        // 监视方向的顺序（图 10）：26 → 28 → 29 → 31(38) → 每通道 27 → 30… → 31(35) → 31(32) → 认可后更新的 23
        var received = client.GetReceivedSnapshot().Skip(before).Select(r => r.Asdu)
            .Where(a => a.TypeId != TypeId103.ChannelData)
            .Select(a => a.TypeId == TypeId103.ChannelEnd ? $"31/{a.Data[0]}" : a.TypeId == TypeId103.ChannelRating ? $"27/{a.Data[4]}" : $"{a.TypeId}")
            .ToList();
        Assert.Equal(["26", "28", "29", "31/38", "27/1", "31/35", "27/2", "31/35", "27/4", "31/35", "31/32"], received);

        // TOO=64 认可后装置上送更新的扰动表
        var list = client.DrainClass1(1, a => a.TypeId == TypeId103.DisturbanceList);
        Assert.Equal(TypeId103.DisturbanceList, list.Last().TypeId);
    }

    [Fact]
    public void 扰动自环_扰动表FUN为装置功能类型且SQ为0()
    {
        var station = BuildStation();
        station.FunctionType = 160;
        using var loopback = new Loopback(station).Start();
        var client = loopback.Client;
        client.ResetLink(1);

        client.RequestDisturbanceList(1);
        var list = client.GetReceivedSnapshot().Select(r => r.Asdu).Single(a => a.TypeId == TypeId103.DisturbanceList);
        Assert.False(list.Sq);
        Assert.Equal(1, list.Count);
        Assert.Equal(160, list.Fun);
        Assert.Equal(0, list.Inf);
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
