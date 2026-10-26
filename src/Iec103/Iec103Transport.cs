using System.IO.Ports;

namespace Iec103;

/// <summary>
/// 103 串口通道工厂。TCP 承载不走此处——103 主从原生 TcpClient/TcpListener
/// （多主站会话、绑定地址可控），与 101 的 lib60870 虚拟串口方案不同；
/// 串口仅承载 FT1.2 字节流，读语义与 101 一致：ReadTimeout 50ms 到期抛
/// TimeoutException 供读循环轮询（Session/ReadLoop 依赖该语义空转观测取消）。
/// </summary>
public static class Iec103Transport
{
    /// <summary>串口通道：字节流 + 串口宿主（释放流之外还需释放 SerialPort 本体）。</summary>
    public sealed record SerialChannel(Stream Stream, SerialPort Port);

    /// <summary>打开并返回串口通道（占用/不存在时抛 IOException，由调用方提示）。</summary>
    public static SerialChannel OpenSerial(Transport103Model transport)
    {
        var port = new SerialPort(
            transport.PortName,
            transport.BaudRate,
            transport.Parity switch
            {
                "none" => Parity.None,
                "odd" => Parity.Odd,
                _ => Parity.Even,
            },
            transport.DataBits,
            transport.StopBits == 2 ? StopBits.Two : StopBits.One)
        {
            ReadTimeout = 50,
            WriteTimeout = 1000,
        };
        port.Open();
        return new SerialChannel(port.BaseStream, port);
    }
}
