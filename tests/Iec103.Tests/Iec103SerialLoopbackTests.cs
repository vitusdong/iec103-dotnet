using Iec103;

namespace Iec103.Tests;

/// <summary>
/// 103 主从自环（串口承载路径）：内存管道对拍（读语义对齐 SerialPort——
/// 无数据抛 TimeoutException），走 StartStream 会话 + 流式主站构造，
/// 与 TCP 路径共用帧层/链路层/应用层，验证串口化不破坏既有行为。
/// </summary>
public class Iec103SerialLoopbackTests
{
    private static Workspace103Model BuildWorkspace()
    {
        return new Workspace103Model
        {
            Stations =
            [
                new Iec103StationModel
                {
                    Address = 1,
                    Name = "串口测试保护装置",
                    CompatIdent = "TEST-103",
                    PrivateIdent = "PROT-A01",
                    Points =
                    [
                        new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 2, Description = "断路器合位" },
                        new Iec103PointModel { Fun = 160, Inf = 2, Dpi = 1, Description = "隔离刀闸分位" },
                    ],
                    GenericEntries =
                    [
                        new GenericEntry103Model { Group = 1, Entry = 1, Description = "定值 A", DataType = "float", ActualValue = 12.5 },
                        new GenericEntry103Model { Group = 1, Entry = 2, Description = "压板", DataType = "uint", ActualValue = 1 },
                    ],
                    Measurements =
                    [
                        new Iec103MeasurementModel { Fun = 160, Inf = 10, Value = 220.5 },
                    ],
                    Disturbances =
                    [
                        new DisturbanceRecord103Model
                        {
                            Fan = 1,
                            Channels =
                            [
                                new DisturbanceChannel103Model { Acc = 1, Primary = 1000, Secondary = 5, Factor = 200, SampleCount = 128 },
                                new DisturbanceChannel103Model { Acc = 2, Primary = 1000, Secondary = 5, Factor = 200, SampleCount = 128 },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>串口路径自环容器：一对管道（主站持左端，从站 StartStream 挂右端）。</summary>
    private sealed class SerialLoopback103 : IDisposable
    {
        public Iec103Server Server { get; }

        public Iec103Client Client { get; private set; } = null!;

        public SerialLoopback103(Workspace103Model workspace)
        {
            Server = new Iec103Server(workspace, new Transport103Model { Type = "serial", PortName = "PIPE" });
        }

        public SerialLoopback103 Start()
        {
            var (left, right) = PipeStream.CreatePair();
            Server.StartStream(right);
            Client = new Iec103Client(left);
            Client.Connect();
            return this;
        }

        public void Dispose()
        {
            Client?.Dispose();
            Server.Dispose();
        }
    }

    private static SerialLoopback103 StartLoopback(Workspace103Model workspace)
    {
        return new SerialLoopback103(workspace).Start();
    }

    [Fact]
    public void 串口路径_复位链路后可取到双标识并回无数据帧()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;

        var ack = client.ResetLink(1);
        Assert.Equal(Frame103Format.FixedLength, ack.Format);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);

        Asdu103.TryDecode(client.RequestClass1(1).Asdu, out var compat, out _);
        Asdu103.TryDecode(client.RequestClass1(1).Asdu, out var privateIdent, out _);
        Assert.Equal(TypeId103.Identification, compat!.TypeId);
        Assert.Equal(80, compat.Inf);
        Assert.Equal(81, privateIdent!.Inf);

        var empty = client.RequestClass1(1);
        Assert.Equal((byte)SecondaryFunction103.RespondNoData, LinkControl103.FromByte(empty.Control).Function);
    }

    [Fact]
    public void 串口路径_时间同步与总召唤全流程()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        var time = new DateTime(2026, 10, 24, 12, 0, 0);
        var ack = client.SendClockSync(1, time);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);
        Asdu103.TryDecode(client.RequestClass1(1).Asdu, out var echo, out _);
        Assert.Equal(TypeId103.ClockSync, echo!.TypeId);
        Assert.Equal(time, Cp56Time2a103.Decode(echo.Data).ToDateTime());

        client.SendGeneralInterrogation(1);
        var received = client.DrainClass1UntilGiEnd(1);
        var points = received.Where(a => a.TypeId == TypeId103.TimeTaggedMessage).ToList();
        Assert.Equal(2, points.Count);
        Assert.Contains(received, a => a.TypeId == TypeId103.GeneralInterrogationEnd);
    }

    [Fact]
    public void 串口路径_通用分类读目录与写值()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);

        var profile = client.ReadGenericProfile(1);
        Assert.Equal(2, profile.Count);
        Assert.Contains(profile, s => s.Kod == Kod103.Description && s.DecodeValue() is "定值 A");

        var ack = client.WriteGenericValue(1, GenericDataSet103.Float(new Gin103(1, 1), Kod103.ActualValue, 99.5f));
        Assert.Equal(Grc103.Acknowledge, ack.Gid[0]);
        var actual = client.ReadGenericEntries(1, (new Gin103(1, 1), Kod103.ActualValue)).Single();
        Assert.Equal(99.5f, (float)actual.DecodeValue()!);
    }

    [Fact]
    public void 串口路径_一般命令与二级测量()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);
        client.RequestClass1(1); // 兼容标识
        client.RequestClass1(1); // 专用标识

        var ack = client.SendGeneralCommand(1, fun: 160, inf: 1, dco: 1);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);
        var response = client.DrainClass1(1, a => a.TypeId == TypeId103.TimeTaggedMessage).Single();
        Assert.Equal(160, response.Fun);
        Assert.Equal(1, response.Inf);
        Assert.Equal(1, DpiQuality103.Decode(response.Data[0]).Dpi);

        var measurements = client.ReadMeasurements(1);
        var measurement = Assert.Single(measurements);
        Assert.Equal(160, measurement.Fun);
        Assert.Equal(10, measurement.Inf);
        Assert.Equal(220, measurement.Value);
    }

    [Fact]
    public void 串口路径_扰动表与录波取回()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);

        var disturbances = client.RequestDisturbanceList(1);
        var entry = Assert.Single(disturbances);
        Assert.Equal(1, entry.Fan);

        var readout = client.ReadDisturbance(1, entry.Fan);
        Assert.Equal(1, readout.Fan);
        Assert.Equal(2, readout.ChannelCount);
        Assert.Equal(128, readout.SampleCount);
        Assert.All(readout.Channels, channel => Assert.Equal(128, channel.Samples.Length));
        Assert.Contains(readout.Channels, c => c.Samples.Any(s => s != 0));
    }

    [Fact]
    public void 串口路径_总线多站他站地址帧被忽略()
    {
        // RS-485 多点总线：工作区两个站挂同一字节流，非本站地址（且非广播）不响应
        var workspace = BuildWorkspace();
        workspace.Stations.Add(new Iec103StationModel
        {
            Address = 2,
            Name = "总线第二站",
            CompatIdent = "ST2-103",
            Points = [new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 1 }],
        });
        using var container = StartLoopback(workspace);
        var client = container.Client;

        // 站 2 正常应答
        var ack2 = client.ResetLink(2);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack2.Control).Function);

        // 未配置的站 9 无应答（事务超时）
        client.TimeoutMs = 300;
        Assert.Throws<TimeoutException>(() => client.ResetLink(9));
    }
}
