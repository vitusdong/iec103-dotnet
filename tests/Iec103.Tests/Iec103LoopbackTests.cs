using System.Net;
using System.Net.Sockets;
using Iec103;

namespace Iec103.Tests;

/// <summary>103 主从自环（TCP 承载 FT1.2）：复位→时间同步→总召唤→突发上送全流程。</summary>
public class Iec103LoopbackTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Workspace103Model BuildWorkspace()
    {
        return new Workspace103Model
        {
            Stations =
            [
                new Iec103StationModel
                {
                    Address = 1,
                    Name = "测试保护装置",
                    CompatIdent = "TEST-103",
                    PrivateIdent = "PROT-A01",
                    Points =
                    [
                        new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 2, Description = "断路器合位" },
                        new Iec103PointModel { Fun = 160, Inf = 2, Dpi = 1, Description = "隔离刀闸分位" },
                        new Iec103PointModel { Fun = 161, Inf = 1, Dpi = 0, Description = "保护动作（不确定）" },
                        new Iec103PointModel { Fun = 128, Inf = 20, Dpi = 1, Description = "位置指示（兼容范围）" },
                    ],
                },
            ],
        };
    }

    /// <summary>自环容器：同时释放服务端与客户端。</summary>
    private sealed class Loopback103 : IDisposable
    {
        public Iec103Server Server { get; }

        public Iec103Client Client { get; private set; } = null!;

        public Loopback103(Workspace103Model workspace)
        {
            Server = new Iec103Server(workspace, GetFreePort());
        }

        public Loopback103 Start()
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
    }

    private static Loopback103 StartLoopback(Workspace103Model workspace)
    {
        return new Loopback103(workspace).Start();
    }

    [Fact]
    public void 复位链路后召唤1级可取到兼容与专用标识()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;

        var ack = client.ResetLink(1);
        Assert.Equal(Frame103Format.FixedLength, ack.Format);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);
        Assert.True(LinkControl103.FromByte(ack.Control).FcbOrAcd); // 队列有标识，ACD=1

        var first = client.RequestClass1(1);
        Assert.Equal(Frame103Format.VariableLength, first.Format);
        Asdu103.TryDecode(first.Asdu, out var compat, out _);
        Assert.NotNull(compat);
        Assert.Equal(TypeId103.Identification, compat!.TypeId);
        Assert.Equal(255, compat.Fun);
        Assert.Equal(80, compat.Inf);
        Assert.Equal(Cot103.ResetCu, compat.Cot);

        var second = client.RequestClass1(1);
        Asdu103.TryDecode(second.Asdu, out var privateIdent, out _);
        Assert.Equal(81, privateIdent!.Inf);
        Assert.Equal(Cot103.Restart, privateIdent.Cot);

        // 队列取空后应答“无所召唤的数据”
        var empty = client.RequestClass1(1);
        Assert.Equal(Frame103Format.FixedLength, empty.Format);
        Assert.Equal((byte)SecondaryFunction103.RespondNoData, LinkControl103.FromByte(empty.Control).Function);
    }

    [Fact]
    public void 时间同步后1级队列含FUN255回读帧()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);
        client.RequestClass1(1); // 兼容标识
        client.RequestClass1(1); // 专用标识

        var time = new DateTime(2026, 10, 16, 18, 0, 0);
        var ack = client.SendClockSync(1, time);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);

        var echoFrame = client.RequestClass1(1);
        Asdu103.TryDecode(echoFrame.Asdu, out var echo, out _);
        Assert.NotNull(echo);
        Assert.Equal(TypeId103.ClockSync, echo!.TypeId);
        Assert.Equal(255, echo.Fun);
        Assert.Equal(Cot103.ClockSync, echo.Cot);
        Assert.Equal(time, Cp56Time2a103.Decode(echo.Data).ToDateTime());
    }

    [Fact]
    public void 总召唤回送全部监视点并以TI8终止帧结束()
    {
        using var container = StartLoopback(BuildWorkspace());
        var server = container.Server;
        var client = container.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        var ack = client.SendGeneralInterrogation(1);
        Assert.Equal((byte)SecondaryFunction103.Ack, LinkControl103.FromByte(ack.Control).Function);

        var received = client.DrainClass1UntilGiEnd(1);
        var points = received.Where(a => a.TypeId == TypeId103.TimeTaggedMessage).ToList();
        Assert.Equal(4, points.Count);
        Assert.All(points, a => Assert.Equal(Cot103.RemoteOperation, a.Cot));
        Assert.Equal([(160, 1), (160, 2), (161, 1), (128, 20)],
            points.Select(a => ((int)a.Fun, (int)a.Inf)));
        // DPI 随 workspace 配置
        Assert.Equal([2, 1, 0, 1], points.Select(a => a.Data[0] & 0x03));

        var end = received.Single(a => a.TypeId == TypeId103.GeneralInterrogationEnd);
        Assert.Equal(Cot103.GiStop, end.Cot);

        // 召唤后队列应已取空
        var empty = client.RequestClass1(1);
        Assert.Equal((byte)SecondaryFunction103.RespondNoData, LinkControl103.FromByte(empty.Control).Function);
        _ = server;
    }

    [Fact]
    public void 突发上送经1级召唤收取_COT为突发()
    {
        using var container = StartLoopback(BuildWorkspace());
        var server = container.Server;
        var client = container.Client;
        client.ResetLink(1);
        client.RequestClass1(1);
        client.RequestClass1(1);

        server.SendSpontaneous(1, new Iec103PointModel { Fun = 160, Inf = 1, Dpi = 1, Description = "断路器分位" });
        Thread.Sleep(100); // 入队跨线程，等一拍

        var frame = client.RequestClass1(1);
        Asdu103.TryDecode(frame.Asdu, out var asdu, out _);
        Assert.NotNull(asdu);
        Assert.Equal(TypeId103.TimeTaggedMessage, asdu!.TypeId);
        Assert.Equal(Cot103.Spontaneous, asdu.Cot);
        Assert.Equal(1, asdu.Data[0] & 0x03);
    }

    [Fact]
    public void 召唤2级数据_无2级数据时回无数据帧()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.ResetLink(1);

        var response = client.RequestClass2(1);
        Assert.Equal(Frame103Format.FixedLength, response.Format);
        Assert.Equal((byte)SecondaryFunction103.RespondNoData, LinkControl103.FromByte(response.Control).Function);
    }

    [Fact]
    public void 未知地址帧被忽略_链路服务超时()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.TimeoutMs = 500;

        // 工作区只有地址 1；未知地址的复位被会话忽略，客户端超时
        Assert.Throws<TimeoutException>(() => client.ResetLink(99));
    }

    [Fact]
    public void 广播复位不回确认帧()
    {
        using var container = StartLoopback(BuildWorkspace());
        var client = container.Client;
        client.TimeoutMs = 500;

        // 广播地址（255）复位 SEND/无回答语义：从站不回帧
        Assert.Throws<TimeoutException>(() => client.ResetLink(255));
    }
}
