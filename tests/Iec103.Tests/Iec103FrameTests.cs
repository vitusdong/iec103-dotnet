using Iec103;

namespace Iec103.Tests;

/// <summary>
/// FT1.2 帧编解码测试。golden 取自 ZLG E8000（DL/T 667-1999 实现说明，
/// 真实装置对拍字节）与 ARIEC103 公开测试向量。
/// </summary>
public class Iec103FrameTests
{
    // ZLG E8000 例程字节
    private static readonly byte[] ResetCuFrame = [0x10, 0x40, 0x01, 0x41, 0x16];
    private static readonly byte[] AckAcdFrame = [0x10, 0x20, 0x01, 0x21, 0x16];
    private static readonly byte[] NoDataFrame = [0x10, 0x09, 0x01, 0x0A, 0x16];
    private static readonly byte[] RequestClass1Fcb1 = [0x10, 0x7A, 0x01, 0x7B, 0x16];
    private static readonly byte[] ClockSyncRequestFrame =
        [0x68, 0x0F, 0x0F, 0x68, 0x73, 0x01, 0x06, 0x81, 0x08, 0x01, 0x00, 0x00, 0x75, 0xC2, 0x32, 0x09, 0x07, 0x06, 0x0E, 0x91, 0x16];
    private static readonly byte[] ClockSyncEchoFrame =
        [0x68, 0x0F, 0x0F, 0x68, 0x08, 0x01, 0x06, 0x81, 0x08, 0x01, 0xFF, 0x00, 0xE8, 0xD2, 0x32, 0x09, 0xC7, 0x06, 0x0E, 0x68, 0x16];
    private static readonly byte[] IdentFrame =
        [0x68, 0x15, 0x15, 0x68, 0x28, 0x01, 0x05, 0x81, 0x04, 0x01, 0xC2, 0x03, 0x03, 0x53, 0x5A, 0x4E, 0x41, 0x52, 0x49, 0x20, 0x20, 0x30, 0x31, 0x30, 0x31, 0x55, 0x16];

    // ARIEC103 公开测试向量（FT1.2 帧层）
    private static readonly byte[] ArieClass1Event = [0x68, 0x0E, 0x0E, 0x68, 0x08, 0x01, 0x01, 0x01, 0x01, 0x01, 0xC0, 0x24, 0x02, 0x39, 0x30, 0x22, 0x0E, 0x00, 0x8C, 0x16];
    private static readonly byte[] ArieGiEnd = [0x68, 0x09, 0x09, 0x68, 0x08, 0x01, 0x08, 0x01, 0x0A, 0x01, 0x00, 0x00, 0x00, 0x1D, 0x16];
    private static readonly byte[] ArieFixedNoData = [0x10, 0x09, 0x01, 0x0A, 0x16];

    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    private static Frame103 AssertOk(byte[] frame)
    {
        var ok = Frame103.TryDecode(frame, out var result, out var error);
        Assert.True(ok, $"帧应解码成功：{error}");
        return Assert.IsType<Frame103>(result);
    }

    [Fact]
    public void 固定帧_复位通信单元_按ZLG实测字节解码()
    {
        var frame = AssertOk(ResetCuFrame);
        Assert.Equal(Frame103Format.FixedLength, frame.Format);
        Assert.Equal(0x40, frame.Control);
        Assert.Equal(1, frame.Address);
    }

    [Fact]
    public void 固定帧_确认与无数据帧解码()
    {
        Assert.Equal(Frame103Format.FixedLength, AssertOk(AckAcdFrame).Format);
        Assert.Equal(Frame103Format.FixedLength, AssertOk(NoDataFrame).Format);
        Assert.Equal(Frame103Format.FixedLength, AssertOk(RequestClass1Fcb1).Format);
        Assert.Equal(Frame103Format.FixedLength, AssertOk(ArieFixedNoData).Format);
    }

    [Fact]
    public void 变长帧_时间同步请求_按ZLG实测字节解码()
    {
        var frame = AssertOk(ClockSyncRequestFrame);
        Assert.Equal(Frame103Format.VariableLength, frame.Format);
        Assert.Equal(0x73, frame.Control);
        Assert.Equal(1, frame.Address);
        Assert.Equal(13, frame.Asdu.Length);
        Assert.Equal(0x06, frame.Asdu[0]);
    }

    [Fact]
    public void 变长帧_时间同步回读与标识帧解码()
    {
        var echo = AssertOk(ClockSyncEchoFrame);
        Assert.Equal(0x08, echo.Control);
        Assert.Equal(0x06, echo.Asdu[0]);

        var ident = AssertOk(IdentFrame);
        Assert.Equal(0x28, ident.Control);
        Assert.Equal(19, ident.Asdu.Length);
        Assert.Equal(0x05, ident.Asdu[0]);
    }

    [Fact]
    public void 变长帧_ARIEC向量解码()
    {
        Assert.Equal(Frame103Format.VariableLength, AssertOk(ArieClass1Event).Format);
        Assert.Equal(Frame103Format.VariableLength, AssertOk(ArieGiEnd).Format);
    }

    [Fact]
    public void 编码_固定帧与ZLG实测字节一致()
    {
        Assert.Equal(ResetCuFrame, Frame103.EncodeFixed(0x40, 1));
        Assert.Equal(AckAcdFrame, Frame103.EncodeFixed(0x20, 1));
        Assert.Equal(NoDataFrame, Frame103.EncodeFixed(0x09, 1));
        Assert.Equal(RequestClass1Fcb1, Frame103.EncodeFixed(0x7A, 1));
    }

    [Fact]
    public void 编码_变长帧与ZLG校时请求帧完全一致()
    {
        var asdu = Hex("06 81 08 01 00 00 75 C2 32 09 07 06 0E");
        Assert.Equal(ClockSyncRequestFrame, Frame103.EncodeVariable(0x73, 1, asdu));
    }

    [Fact]
    public void 编码_超出最大用户数据长度时抛出()
    {
        var tooLong = new byte[Frame103.MaxAsduLength + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => Frame103.EncodeVariable(0x73, 1, tooLong));
    }

    [Fact]
    public void 解码_校验和错误被拒绝()
    {
        var bad = (byte[])ResetCuFrame.Clone();
        bad[3] ^= 0xFF;
        Assert.False(Frame103.TryDecode(bad, out _, out var error));
        Assert.Contains("校验和", error);

        var badVariable = (byte[])ClockSyncRequestFrame.Clone();
        badVariable[^2] ^= 0xFF;
        Assert.False(Frame103.TryDecode(badVariable, out _, out _));
    }

    [Fact]
    public void 解码_长度不一致与结束符错误被拒绝()
    {
        var badLength = (byte[])ClockSyncRequestFrame.Clone();
        badLength[2] ^= 0xFF;
        Assert.False(Frame103.TryDecode(badLength, out _, out var lengthError));
        Assert.Contains("长度", lengthError);

        var badEnd = (byte[])ResetCuFrame.Clone();
        badEnd[4] = 0x00;
        Assert.False(Frame103.TryDecode(badEnd, out _, out var endError));
        Assert.Contains("结束符", endError);
    }

    [Fact]
    public void 解码_单字符E5()
    {
        var frame = AssertOk([0xE5]);
        Assert.Equal(Frame103Format.SingleChar, frame.Format);
    }

    [Fact]
    public void 流式分帧_粘包半包与垃圾字节()
    {
        var parser = new FrameStreamParser103();
        var wire = new List<byte>();
        // 噪声 + 复位 + 单字符 + 校时请求 + 校时回读 连续粘包
        wire.AddRange(Hex("00 FF EE"));
        wire.AddRange(ResetCuFrame);
        wire.AddRange(Hex("E5"));
        wire.AddRange(ClockSyncRequestFrame);
        wire.AddRange(ClockSyncEchoFrame);

        // 按任意边界分片喂入：先逐字节喂前 7 字节，其余一次喂
        var frames = new List<Frame103>();
        frames.AddRange(parser.Feed(wire.Take(7).ToArray()));
        frames.AddRange(parser.Feed(wire.Skip(7).ToArray()));

        Assert.Equal(4, frames.Count);
        Assert.Equal(Frame103Format.FixedLength, frames[0].Format);
        Assert.Equal(Frame103Format.SingleChar, frames[1].Format);
        Assert.Equal(0x73, frames[2].Control);
        Assert.Equal(0x08, frames[3].Control);
    }

    [Fact]
    public void 流式分帧_帧体内部截断后拼回()
    {
        var parser = new FrameStreamParser103();
        var frames = new List<Frame103>();
        var half = IdentFrame.Length / 2;
        frames.AddRange(parser.Feed(IdentFrame.Take(half).ToArray()));
        Assert.Empty(frames);
        frames.AddRange(parser.Feed(IdentFrame.Skip(half).ToArray()));

        var frame = Assert.Single(frames);
        Assert.Equal(0x28, frame.Control);
        Assert.Equal(19, frame.Asdu.Length);
    }

    [Fact]
    public void 流式分帧_坏帧被舍弃并上报原因()
    {
        var parser = new FrameStreamParser103();
        var rejected = new List<string>();
        parser.FrameRejected += (hex, reason) => rejected.Add(reason);

        var bad = (byte[])ResetCuFrame.Clone();
        bad[3] ^= 0xFF;
        var frames = parser.Feed(bad);

        Assert.Empty(frames);
        Assert.Single(rejected);
        // 坏帧后线路状态应复位，后续好帧正常
        var good = parser.Feed(ResetCuFrame);
        Assert.Single(good);
    }
}
