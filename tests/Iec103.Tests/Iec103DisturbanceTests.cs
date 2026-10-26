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
        Assert.Equal(0xB0, sof.Encode());

        var time = new Cp32Time2a103(53992, 50, 9, Invalid: false, SummerTime: false);
        Assert.Equal([0xE8, 0xD2, 0x32, 0x09], time.Encode());
        var decoded = Cp32Time2a103.Decode(time.Encode());
        Assert.Equal((53992, 50, 9), (decoded.Millisecond, decoded.Minute, decoded.Hour));
    }

    [Fact]
    public void 扰动表ASDU23编码含FAN_SOF_CP56()
    {
        var station = BuildStation();
        var asdu = DisturbanceAsdu.DisturbanceList(1, station.Disturbances);
        Assert.Equal(TypeId103.DisturbanceList, asdu.TypeId);
        Assert.Equal(1, asdu.Count); // VSQ 数量 = 记录数（3 是通道数）
        Assert.Equal(Cot103.DisturbanceTransfer, asdu.Cot);
        Assert.Equal(10, asdu.Data.Length);
        Assert.Equal(7, asdu.Data[0] | (asdu.Data[1] << 8)); // FAN=7
        Assert.Equal(0xD0, asdu.Data[2]); // SOF: TP+TM+OTEV（记录默认状态）
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
        Assert.Equal(21, segment.FirstElement); // 首元素序号 = 偏移+1
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
