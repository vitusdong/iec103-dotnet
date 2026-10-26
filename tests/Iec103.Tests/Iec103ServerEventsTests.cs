using System.Net;
using System.Net.Sockets;
using Iec103;

namespace Iec103.Tests;

/// <summary>
/// 103 从站/主站 M4 事件面：主站接入/断开、线路帧踪迹、主站 LinkClosed。
/// </summary>
public class Iec103ServerEventsTests
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
        var workspace = new Workspace103Model();
        var station = new Iec103StationModel { Address = 1, Name = "103 测试站" };
        station.Points.Add(new Iec103PointModel { Fun = 160, Inf = 99, Dpi = 1, Description = "监视点" });
        workspace.Stations.Add(station);
        return workspace;
    }

    private static async Task WaitAsync(Task task, string timeoutMessage)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == task, timeoutMessage);
    }

    /// <summary>主站接入/断开事件 + 复位链路的收发帧踪迹。</summary>
    [Fact]
    public async Task 事件面_接入断开与帧踪迹()
    {
        var port = GetFreePort();
        using var server = new Iec103Server(BuildWorkspace(), port);
        var connections = new List<(bool Connected, string Remote)>();
        var traces = new List<FrameTrace>();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += remote =>
        {
            lock (connections)
            {
                connections.Add((true, remote));
            }

            connected.TrySetResult();
        };
        server.ClientDisconnected += remote =>
        {
            lock (connections)
            {
                connections.Add((false, remote));
            }

            disconnected.TrySetResult();
        };
        server.FrameTraced += trace =>
        {
            lock (traces)
            {
                traces.Add(trace);
            }
        };
        server.Start();

        var linkClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = new Iec103Client("127.0.0.1", port))
        {
            client.LinkClosed += () => linkClosed.TrySetResult();
            client.Connect();
            await WaitAsync(connected.Task, "等待主站接入事件超时");
            client.ResetLink(1);
            await Task.Delay(500);
        }

        await WaitAsync(disconnected.Task, "等待主站断开事件超时");
        await WaitAsync(linkClosed.Task, "等待客户端 LinkClosed 超时");

        lock (connections)
        {
            Assert.Equal([(true, connections[0].Remote)], connections.Where(c => c.Connected).ToList());
            Assert.Contains(connections, c => !c.Connected);
        }

        lock (traces)
        {
            // 复位链路：客户端发固定帧（10 40 01 40 16），服务端回 ACK 固定帧——两方向都留痕
            Assert.Contains(traces, t => !t.Sent && t.Hex.StartsWith("10"));
            Assert.Contains(traces, t => t.Sent && t.Hex.StartsWith("10"));
        }
    }

    /// <summary>监听地址可配置（bindAddress 参数生效）。</summary>
    [Fact]
    public async Task 监听地址_任意网卡可接入()
    {
        var port = GetFreePort();
        using var server = new Iec103Server(BuildWorkspace(), port, "0.0.0.0");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnected += _ => connected.TrySetResult();
        server.Start();

        using var client = new Iec103Client("127.0.0.1", port);
        client.Connect();
        await WaitAsync(connected.Task, "等待接入事件超时");
    }
}
