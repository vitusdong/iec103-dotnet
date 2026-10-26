using System.Net;
using System.Net.Sockets;
using Iec103;


namespace Iec103.Tests;

/// <summary>
/// 103 协议栈硬化（代码审查 2C/3H 修复的行为锁定）：
/// 畸形帧长度域、短载荷命令帧、扰动表分帧、fault 并发注入、长描述预算、校时时钟。
/// </summary>
public class Iec103HardeningTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Workspace103Model BuildWorkspace(int disturbanceCount = 0)
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            Name = "硬化测试站",
            Points = [new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 1, Description = "测试点" }],
            Disturbances = Enumerable.Range(1, disturbanceCount)
                .Select(fan => new DisturbanceRecord103Model
                {
                    Fan = fan,
                    FaultTime = DateTime.Now.AddMinutes(-fan),
                    Channels = [new DisturbanceChannel103Model { Acc = 1, SampleCount = 10 }],
                })
                .ToList(),
        };
        return new Workspace103Model { Stations = [station] };
    }

    private static (Iec103Server Server, Iec103Client Client) StartPair(Workspace103Model workspace)
    {
        var port = GetFreePort();
        var server = new Iec103Server(workspace, port);
        server.Start();
        var client = new Iec103Client("127.0.0.1", port);
        client.Connect();
        client.ResetLink(1);
        return (server, client);
    }

    [Theory]
    [InlineData("6800 0068 0016")]           // L=0，CS=0 恰匹配空和
    [InlineData("6801 0168 2B2B 16")]         // L=1，CS=0x2B 匹配单字节和
    public void 可变帧长度域小于2_解码拒绝不抛异常(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));

        var decoded = Frame103.TryDecode(bytes, out var frame, out var error);

        Assert.False(decoded);
        Assert.Null(frame);
        Assert.NotNull(error);
        Assert.Contains("小于最小值", error);

        // 分帧器路径同样安全：坏帧舍弃事件而非异常
        var parser = new FrameStreamParser103();
        var rejected = new List<string>();
        parser.FrameRejected += (_, reason) => { lock (rejected) { rejected.Add(reason); } };
        var frames = parser.Feed(bytes);
        Assert.Empty(frames);
        lock (rejected)
        {
            Assert.Contains(rejected, reason => reason.Contains("小于最小值"));
        }
    }

    [Fact]
    public void 短载荷命令帧_守卫拒绝且会话保持()
    {
        var (server, client) = StartPair(BuildWorkspace());
        using var _ = client;
        using var __ = server;

        // 四类曾因短 Data 越界的命令：扰动命令（空）、扰动认可（空）、一般命令（1 字节）、通用写（1 字节）
        var malformed = new[]
        {
            new Asdu103(TypeId103.DisturbanceCommand, true, 1, Cot103.DisturbanceTransfer, false, 1, 255, 0, []),
            new Asdu103(TypeId103.DisturbanceAck, true, 1, Cot103.DisturbanceTransfer, false, 1, 255, 0, []),
            new Asdu103(TypeId103.GeneralCommand, true, 1, Cot103.CommandAck, false, 1, 160, 1, [1]),
            new Asdu103(TypeId103.GenericData, true, 1, Cot103.GenericWrite, false, 1, 254, 0, [7]),
        };

        foreach (var asdu in malformed)
        {
            var ack = client.SendConfirmed(1, asdu);
            Assert.Equal(Frame103Format.FixedLength, ack.Format);
        }

        // 会话存活：时钟同步仍正常应答，1 级可取回读帧
        client.SendClockSync(1, DateTime.Now);
        var readback = client.DrainClass1(1, a => a.TypeId == TypeId103.ClockSync);
        Assert.Contains(readback, a => a.TypeId == TypeId103.ClockSync);
    }

    [Fact]
    public void 扰动表30条_按帧容量分帧完整取回()
    {
        // 25 条以上曾使 ASDU 23 超过 253 字节帧上限（会话终止）；现按 24 条/帧分帧
        var (server, client) = StartPair(BuildWorkspace(disturbanceCount: 30));
        using var _ = client;
        using var __ = server;

        var entries = client.RequestDisturbanceList(1);

        Assert.Equal(30, entries.Count);
        Assert.Equal(Enumerable.Range(1, 30), entries.Select(e => e.Fan));
    }

    [Fact]
    public async Task fault注入与扰动表请求并发_不崩溃且条数一致()
    {
        // server 与 RuntimeCommands 必须共享同一 workspace 实例（同 serve103 进程语义）
        var workspace = BuildWorkspace(disturbanceCount: 5);
        var (server, client) = StartPair(workspace);
        using var _ = client;
        using var __ = server;
        var station = workspace.Stations[0];

        // 控制台线程持续注入（经 RuntimeCommands103），主站线程持续取表
        var inject = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                RuntimeCommands103.TryInjectDisturbance(workspace, 1);
            }
        });

        var pollExceptions = new List<Exception>();
        var polls = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    client.RequestDisturbanceList(1);
                }
                catch (Exception ex)
                {
                    lock (pollExceptions)
                    {
                        pollExceptions.Add(ex);
                    }
                }
            }
        });

        await Task.WhenAll(inject, polls);
        lock (pollExceptions)
        {
            Assert.Empty(pollExceptions);
        }

        Assert.Equal(25, station.Disturbances.Count);
    }

    [Fact]
    public void 超长描述_数据集截断到单帧预算且宽度一致()
    {
        // 500 个汉字 GB18030 编码 1000 字节——曾溢出帧上限且宽度字节与 GID 失配
        var text = new string('测', 500);
        var set = GenericDataSet103.Ascii(new Gin103(1, 1), Kod103.Description, text);

        Assert.True(set.Gid.Length <= GenericDataSet103.MaxAsciiBytes, $"GID {set.Gid.Length} 应截断到 {GenericDataSet103.MaxAsciiBytes} 字节内");
        Assert.Equal(set.Gid.Length, set.Gdd.DataWidth);
        // 按字符边界截断：解码仍是完整汉字串前缀
        var decoded = System.Text.Encoding.GetEncoding("GB18030").GetString(set.Gid);
        Assert.StartsWith(decoded, text);
        Assert.NotEmpty(decoded);
    }

    [Fact]
    public void 超长描述_工作区校验拒绝()
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            GenericEntries =
            [
                new GenericEntry103Model { Group = 1, Entry = 1, DataType = "ascii", Description = new string('测', 500) },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => WorkspaceJson103.Save(new Workspace103Model { Stations = [station] }));
    }

    [Fact]
    public void 采样数超16位上限_工作区校验拒绝()
    {
        var station = new Iec103StationModel
        {
            Address = 1,
            Disturbances =
            [
                new DisturbanceRecord103Model
                {
                    Fan = 1,
                    Channels = [new DisturbanceChannel103Model { Acc = 1, SampleCount = 70_000 }],
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => WorkspaceJson103.Save(new Workspace103Model { Stations = [station] }));
    }

    [Fact]
    public void 校时到过去时刻_总召唤时间戳使用同步钟()
    {
        // 校时后事件时戳曾仍用 PC 墙钟——同步到 2020-01-01 后 GI 点时戳年份应为 2020
        var (server, client) = StartPair(BuildWorkspace());
        using var _ = client;
        using var __ = server;

        client.SendClockSync(1, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local));
        client.SendGeneralInterrogation(1);
        var frames = client.DrainClass1UntilGiEnd(1);

        var point = frames.Single(a => a.TypeId == TypeId103.TimeTaggedMessage);
        var timestamp = Cp56Time2a103.Decode(point.Data.AsSpan(5, 7)).ToDateTime();
        Assert.Equal(2020, timestamp.Year);
    }
}
