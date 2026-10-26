using Iec103;

namespace Iec103.Tests;

/// <summary>103 ASDU 应用层测试：CP56 时标（ZLG 实测值）、基础四件套工厂与编解码。</summary>
public class Iec103AsduTests
{
    private static byte[] Hex(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    [Fact]
    public void CP56时标_按ZLG实测字节解码为2014年6月7日9时50分49秒781()
    {
        // ZLG 校时帧时标：49781ms = 49s781，分 50，时 9，日 7，月 6，年 14
        var time = Cp56Time2a103.Decode(Hex("75 C2 32 09 07 06 0E"));
        Assert.Equal(49781, time.Millisecond);
        Assert.Equal(50, time.Minute);
        Assert.Equal(9, time.Hour);
        Assert.Equal(7, time.Day);
        Assert.Equal(6, time.Month);
        Assert.Equal(14, time.Year);
        Assert.False(time.Invalid);
        Assert.False(time.SummerTime);
    }

    [Fact]
    public void CP56时标_编码与ZLG实测字节一致()
    {
        var time = new Cp56Time2a103(49781, 50, 9, 7, 6, 14, Invalid: false, SummerTime: false);
        Assert.Equal(Hex("75 C2 32 09 07 06 0E"), time.Encode());
    }

    [Fact]
    public void CP56时标_IV无效位与夏令时位编码解码()
    {
        var time = new Cp56Time2a103(0, 1, 2, 3, 4, 25, Invalid: true, SummerTime: true);
        var decoded = Cp56Time2a103.Decode(time.Encode());
        Assert.True(decoded.Invalid);
        Assert.True(decoded.SummerTime);
        Assert.Equal(1, decoded.Minute);
        Assert.Equal(2, decoded.Hour);
    }

    [Fact]
    public void CP56时标_DateTime往返保持毫秒精度()
    {
        var origin = new DateTime(2026, 10, 16, 12, 34, 56, 789);
        var roundTrip = Cp56Time2a103.FromDateTime(origin).ToDateTime();
        Assert.Equal(origin, roundTrip);
    }

    [Fact]
    public void 时间同步命令_编码与ZLG主站帧ASDU一致()
    {
        var time = new Cp56Time2a103(49781, 50, 9, 7, 6, 14, false, false);
        var asdu = Asdu103.ClockSync(time, ca: 1, fun: 0);
        Assert.Equal(Hex("06 81 08 01 00 00 75 C2 32 09 07 06 0E"), asdu.Encode());
    }

    [Fact]
    public void 时间同步回读_FUN为全局255_可按ZLG从站帧解码()
    {
        // ZLG 从站回读帧 ASDU：TI=6 COT=8 FUN=FF INF=0 + 设备时间
        var ok = Asdu103.TryDecode(Hex("06 81 08 01 FF 00 E8 D2 32 09 C7 06 0E"), out var asdu, out _);
        Assert.True(ok);
        Assert.Equal(TypeId103.ClockSync, asdu!.TypeId);
        Assert.Equal(255, asdu.Fun);
        Assert.Equal(Cot103.ClockSync, asdu.Cot);
        Assert.Equal(53992, Cp56Time2a103.Decode(asdu.Data).Millisecond);
    }

    [Fact]
    public void 总召唤命令_编码布局符合DL667()
    {
        var asdu = Asdu103.GeneralInterrogation(ca: 1, scn: 0);
        Assert.Equal(Hex("07 81 09 01 FF 00 00"), asdu.Encode());
    }

    [Fact]
    public void 总召唤终止_编码布局()
    {
        var asdu = Asdu103.GeneralInterrogationEnd(ca: 1, scn: 0);
        Assert.Equal(Hex("08 81 0A 01 FF 00 00"), asdu.Encode());
    }

    [Fact]
    public void 标识报文_兼容与专用范围FUN_INF_COT区分()
    {
        var compat = Asdu103.Identification(ca: 1, privateRange: false, "TEST-103");
        Assert.Equal(TypeId103.Identification, compat.TypeId);
        Assert.Equal(255, compat.Fun);
        Assert.Equal(80, compat.Inf);
        Assert.Equal(Cot103.ResetCu, compat.Cot);
        Assert.Equal("TEST-103", System.Text.Encoding.ASCII.GetString(compat.Data));

        var privateRange = Asdu103.Identification(ca: 1, privateRange: true, "PROT");
        Assert.Equal(81, privateRange.Inf);
        Assert.Equal(Cot103.Restart, privateRange.Cot);
        Assert.Equal(8, privateRange.Data.Length);
    }

    [Fact]
    public void 带时标监视信息_数据为DPI加4个附加信息字节加CP56共12字节()
    {
        var time = new Cp56Time2a103(1000, 1, 2, 3, 4, 25, false, false);
        var asdu = Asdu103.TimeTaggedMessage(
            ca: 1, fun: 160, inf: 5, dpi: DpiQuality103.FromDpi(2), time: time, cot: Cot103.RemoteOperation);

        Assert.Equal(TypeId103.TimeTaggedMessage, asdu.TypeId);
        Assert.Equal(160, asdu.Fun);
        Assert.Equal(5, asdu.Inf);
        Assert.Equal(12, asdu.Data.Length);
        Assert.Equal(2, asdu.Data[0]);
        // 附加信息字节 FF FF FF FF（兼容方向固定）
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], asdu.Data[1..5]);
        Assert.Equal(time.Encode(), asdu.Data[5..]);
    }

    [Fact]
    public void DPI品质字节_编解码往返()
    {
        var dpi = new DpiQuality103(Dpi: 1, Invalid: true, NonTopical: false, Substituted: true, Blocked: false, El: true);
        var decoded = DpiQuality103.Decode(dpi.Encode());
        Assert.Equal(dpi, decoded);
        Assert.Equal("分（OFF）", decoded.State);
    }

    [Fact]
    public void ASDU编解码往返_含否定位与VSQ()
    {
        var asdu = new Asdu103(
            TypeId: 20, Sq: false, Count: 3, Cot: Cot103.PowerOn, Negative: true,
            Ca: 7, Fun: 200, Inf: 42, Data: [0x01, 0x02]);
        var decoded = Asdu103.TryDecode(asdu.Encode(), out var result, out var error);
        Assert.True(decoded, error);
        // record 的数组字段按引用比较，逐字段断言
        Assert.Equal(asdu.TypeId, result!.TypeId);
        Assert.Equal(asdu.Sq, result.Sq);
        Assert.Equal(asdu.Count, result.Count);
        Assert.Equal(asdu.Cot, result.Cot);
        Assert.Equal(asdu.Negative, result.Negative);
        Assert.Equal(asdu.Ca, result.Ca);
        Assert.Equal(asdu.Fun, result.Fun);
        Assert.Equal(asdu.Inf, result.Inf);
        Assert.Equal(asdu.Data, result.Data);
        Assert.Equal(0x03, result!.Encode()[1]); // VSQ：SQ=0 + 数量 3
        Assert.Equal(0x86, result.Encode()[2]); // COT = 激活 | P/N
    }

    [Fact]
    public void ASDU解码_不足6字节报错()
    {
        Assert.False(Asdu103.TryDecode(Hex("06 81 08"), out _, out var error));
        Assert.Contains("6 字节", error);
    }
}
