using Iec103;

namespace Iec103.Tests;

/// <summary>通用分类服务测试：GIN/KOD/GDD/GID 编解码、读命令 golden、自环读写全流程。</summary>
public class Iec103GenericTests
{
    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    [Fact]
    public void 读全目录命令编码_FUN254_COT42()
    {
        var asdu = Asdu103.GenericReadAll(ca: 1, rii: 0);
        // TI=0x15(21) VSQ=0x81 COT=0x2A(42) CA=1 FUN=0xFE(254) INF=0
        // RII=0 NOG=1 GIN=0000 KOD=0A(描述)
        Assert.Equal(Hex("15 81 2A 01 FE 00 00 01 00 00 0A"), asdu.Encode());
    }

    [Fact]
    public void 读单条命令编码()
    {
        var asdu = Asdu103.GenericRead(1, rii: 5, (new Gin103(1, 2), Kod103.ActualValue));
        Assert.Equal(Hex("15 81 2A 01 FE 00 05 01 01 02 01"), asdu.Encode());
    }

    [Fact]
    public void GDD与NGD编解码往返()
    {
        var gdd = new Gdd103(GddDataType103.Ascii, 8, 1, false);
        var decoded = Gdd103.Decode(gdd.Encode());
        Assert.Equal(gdd, decoded);

        var continued = new Gdd103(GddDataType103.ShortReal, 4, 3, true);
        Assert.True(Gdd103.Decode(continued.Encode()).Continuation);

        Assert.Equal((5, true), Ngd103.Decode(Ngd103.Encode(5, true)));
        Assert.Equal((0, false), Ngd103.Decode(Ngd103.Encode(0, false)));
    }

    [Fact]
    public void GDD与NGD位布局符合DL_T667()
    {
        // NGD：数目 bit5-0、后续 D 在 bit7（GPGate 按规约 &0x3F 读，曾因 count<<2 错位）
        Assert.Equal(0x02, Ngd103.Encode(2, false));
        Assert.Equal(0x85, Ngd103.Encode(5, true));
        Assert.Equal((2, false), Ngd103.Decode(0x02));
        Assert.Equal((5, true), Ngd103.Decode(0x85));

        // GDD 第 3 字节：数目 bit0-6、连续 CON 在 bit7
        var gdd = Gdd103.Decode([(byte)GddDataType103.ShortReal, 4, 0x83]);
        Assert.Equal(3, gdd.Number);
        Assert.True(gdd.Continuation);
        Assert.Equal([(byte)GddDataType103.Ascii, 0x04, 0x83], new Gdd103(GddDataType103.Ascii, 4, 3, true).Encode());
    }

    [Fact]
    public void 数据集ASCII与整数值编解码()
    {
        var ascii = GenericDataSet103.Ascii(new Gin103(1, 1), Kod103.Description, "断路器位置");
        GenericDataSet103.TryDecode(ascii.Encode(), out var decoded, out var consumed);
        Assert.Equal(ascii.EncodedSize, consumed);
        Assert.Equal("断路器位置", decoded!.DecodeValue());

        var unsigned = GenericDataSet103.Unsigned(new Gin103(1, 2), Kod103.ActualValue, 300, width: 2);
        GenericDataSet103.TryDecode(unsigned.Encode(), out var uintSet, out _);
        Assert.Equal(300L, uintSet!.DecodeValue());

        var signed = GenericDataSet103.Signed(new Gin103(1, 3), Kod103.ActualValue, -2, width: 1);
        GenericDataSet103.TryDecode(signed.Encode(), out var intSet, out _);
        Assert.Equal(-2L, intSet!.DecodeValue());
    }

    [Fact]
    public void 数据集浮点与量程编解码()
    {
        var value = GenericDataSet103.Float(new Gin103(1, 2), Kod103.ActualValue, 5.25f);
        GenericDataSet103.TryDecode(value.Encode(), out var decoded, out _);
        Assert.Equal(5.25f, decoded!.DecodeValue());

        var range = GenericDataSet103.Range(new Gin103(1, 2), 0.5f, 100f, 0.1f);
        GenericDataSet103.TryDecode(range.Encode(), out var rangeSet, out _);
        // 量程 = 最小/最大/步长 3 个短实数（GDD 数量 3）
        Assert.Equal(3, rangeSet!.Gdd.Number);
        var floats = new[]
        {
            BitConverter.ToSingle(rangeSet.Gid, 0),
            BitConverter.ToSingle(rangeSet.Gid, 4),
            BitConverter.ToSingle(rangeSet.Gid, 8),
        };
        Assert.Equal([0.5f, 100f, 0.1f], floats);
    }

    [Fact]
    public void 通用分类自环_读全目录含分帧续传()
    {
        // 30 条 ASCII 描述目录：单帧 ASDU 预算 253-8-2=243 字节，每集 6+len → 分成 2+ 帧
        var station = new Iec103StationModel
        {
            Address = 1,
            Name = "通用分类测试站",
            GenericEntries = Enumerable.Range(1, 30).Select(i => new GenericEntry103Model
            {
                Group = 1,
                Entry = (byte)i,
                Description = $"条目{i:D2}",
                DataType = "uint",
                ActualValue = i,
                IntWidth = 2,
            }).ToList(),
        };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var profile = client.ReadGenericProfile(1);

        Assert.Equal(30, profile.Count);
        Assert.Equal([1, 1], [profile[0].Gin.Group, profile[0].Gin.Entry]);
        Assert.Equal("条目01", profile[0].DecodeValue());
        Assert.Equal("条目30", profile[^1].DecodeValue());
        Assert.All(profile, set => Assert.Equal(Kod103.Description, set.Kod));
    }

    [Fact]
    public void 通用分类自环_读单条描述实际值与量程()
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            GenericEntries =
            [
                new GenericEntry103Model
                {
                    Group = 1, Entry = 2, Description = "保护定值 I>", DataType = "float",
                    ActualValue = 5.0, MinValue = 0.5, MaxValue = 100.0, StepValue = 0.1,
                },
                new GenericEntry103Model
                {
                    Group = 2, Entry = 1, Description = "装置名称", DataType = "ascii", AsciiValue = "RELAY-A",
                },
            ],
        };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var description = client.ReadGenericEntries(1, (new Gin103(1, 2), Kod103.Description)).Single();
        Assert.Equal("保护定值 I>", description.DecodeValue());

        var actual = client.ReadGenericEntries(1, (new Gin103(1, 2), Kod103.ActualValue)).Single();
        Assert.Equal(5.0f, actual.DecodeValue());

        var range = client.ReadGenericEntries(1, (new Gin103(1, 2), Kod103.Range)).Single();
        Assert.Equal(3, range.Gdd.Number);
        Assert.Equal(0.5f, BitConverter.ToSingle(range.Gid, 0));

        var ascii = client.ReadGenericEntries(1, (new Gin103(2, 1), Kod103.ActualValue)).Single();
        Assert.Equal("RELAY-A", ascii.DecodeValue());
    }

    [Fact]
    public void 通用分类自环_写实际值成功并可读回()
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            GenericEntries =
            [
                new GenericEntry103Model { Group = 1, Entry = 2, Description = "定值", DataType = "float", ActualValue = 5.0 },
            ],
        };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var ack = client.WriteGenericValue(1, GenericDataSet103.Float(new Gin103(1, 2), Kod103.ActualValue, 7.5f));
        Assert.Equal(Grc103.Acknowledge, ack.Gid[0]);

        var actual = client.ReadGenericEntries(1, (new Gin103(1, 2), Kod103.ActualValue)).Single();
        Assert.Equal(7.5f, actual.DecodeValue());
    }

    [Fact]
    public void 通用分类自环_写只读条目回否定确认()
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            GenericEntries =
            [
                new GenericEntry103Model { Group = 2, Entry = 2, Description = "版本", DataType = "ascii", AsciiValue = "V1", ReadOnly = true },
            ],
        };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var ack = client.WriteGenericValue(1, GenericDataSet103.Ascii(new Gin103(2, 2), Kod103.ActualValue, "V9"));
        Assert.Equal(Grc103.ReadOnlyEntry, ack.Gid[0]);
        Assert.Equal(GddDataType103.GenericReplyCode, ack.Gdd.DataType);

        // 值不应被修改
        var actual = client.ReadGenericEntries(1, (new Gin103(2, 2), Kod103.ActualValue)).Single();
        Assert.Equal("V1", actual.DecodeValue());
    }

    [Fact]
    public void 通用分类自环_写未知GIN回否定确认()
    {
        var station = new Iec103StationModel { Address = 1, GenericEntries = [] };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var ack = client.WriteGenericValue(1, GenericDataSet103.Float(new Gin103(9, 9), Kod103.ActualValue, 1f));
        Assert.Equal(Grc103.UnknownGin, ack.Gid[0]);
    }

    [Fact]
    public void 通用分类自环_读未知GIN回无效数据响应()
    {
        var station = new Iec103StationModel { Address = 1, GenericEntries = [] };
        using var loopback = StartLoopback(station);
        var client = loopback.Client;
        client.ResetLink(1);

        var rii = (byte)1;
        client.SendConfirmed(1, Asdu103.GenericRead(1, rii, (new Gin103(9, 9), Kod103.Description)));
        var response = client.DrainClass1(1, asdu => asdu.TypeId == TypeId103.GenericData)
            .First(asdu => asdu.TypeId == TypeId103.GenericData);
        Assert.Equal(Cot103.GenericReadInvalid, response.Cot);
    }

    /// <summary>最小自环容器：地址 1 单站。</summary>
    private sealed class Loopback : IDisposable
    {
        public Iec103Server Server { get; }

        public Iec103Client Client { get; private set; } = null!;

        public Loopback(Iec103StationModel station)
        {
            Server = new Iec103Server(new Workspace103Model { Stations = [station] }, GetFreePort());
        }

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

    private static Loopback StartLoopback(Iec103StationModel station) => new Loopback(station).Start();
}
