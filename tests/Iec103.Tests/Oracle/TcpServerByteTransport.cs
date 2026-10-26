using System.Net;
using System.Net.Sockets;
using ArIEC103.Master.Transport;

namespace Iec103.Tests.Oracle;

/// <summary>
/// IByteTransport 的 TCP 承载实现：把 ArIEC103 从站模拟器接到 TCP 上，
/// 供本库的 Iec103Client（TCP 主站）直连对拍。
/// 构造即监听（端口可指定或自动分配，见 <see cref="Port"/>），
/// OpenAsync 时接受单个主站连接。
/// </summary>
public sealed class TcpServerByteTransport : IByteTransport
{
    private readonly TcpListener _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpServerByteTransport(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
    }

    /// <summary>实际监听端口（构造后即可读，供客户端连接）。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool IsOpen => _stream is { } stream && _client?.Connected == true;

    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        _client = _listener.AcceptTcpClient();
        _stream = _client.GetStream();
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return _stream!.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        return _stream!.WriteAsync(buffer, cancellationToken);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        _stream?.Close();
        _client?.Close();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _listener.Stop();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
