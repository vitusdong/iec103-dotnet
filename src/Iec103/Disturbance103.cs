namespace Iec103;

/// <summary>扰动数据传输命令 TOO（IEC 60870-5-103，Wireshark dissector 枚举）。</summary>
public static class Too103
{
    /// <summary>选择故障。</summary>
    public const byte SelectFault = 1;

    /// <summary>请求扰动数据。</summary>
    public const byte RequestDisturbanceData = 2;

    /// <summary>中止扰动数据。</summary>
    public const byte AbortDisturbanceData = 3;

    /// <summary>请求通道（ACC 指定通道）。</summary>
    public const byte RequestChannel = 8;

    /// <summary>中止通道。</summary>
    public const byte AbortChannel = 9;

    /// <summary>请求标签。</summary>
    public const byte RequestTags = 16;

    /// <summary>中止标签。</summary>
    public const byte AbortTags = 17;

    /// <summary>请求被记录的扰动表。</summary>
    public const byte RequestDisturbanceList = 24;

    /// <summary>扰动数据传输结束（无中止）。</summary>
    public const byte EndDisturbance = 32;

    /// <summary>通道传输结束（无中止）。</summary>
    public const byte EndChannel = 35;

    /// <summary>标签传输结束（无中止）。</summary>
    public const byte EndTags = 38;

    /// <summary>扰动数据传输成功（主站认可）。</summary>
    public const byte PositiveAck = 64;

    /// <summary>扰动数据传输失败（主站认可）。</summary>
    public const byte NegativeAck = 65;

    public static string Describe(byte too) => too switch
    {
        SelectFault => "选择故障",
        RequestDisturbanceData => "请求扰动数据",
        AbortDisturbanceData => "中止扰动数据",
        RequestChannel => "请求通道",
        AbortChannel => "中止通道",
        RequestTags => "请求标签",
        AbortTags => "中止标签",
        RequestDisturbanceList => "请求被记录的扰动表",
        EndDisturbance => "扰动数据传输结束（无中止）",
        EndDisturbance + 1 => "扰动数据传输结束（主站中止）",
        EndDisturbance + 2 => "扰动数据传输结束（装置中止）",
        EndChannel => "通道传输结束（无中止）",
        EndChannel + 1 => "通道传输结束（主站中止）",
        EndChannel + 2 => "通道传输结束（装置中止）",
        EndTags => "标签传输结束（无中止）",
        EndTags + 1 => "标签传输结束（主站中止）",
        EndTags + 2 => "标签传输结束（装置中止）",
        PositiveAck => "扰动数据传输成功（肯定）",
        NegativeAck => "扰动数据传输失败（否定）",
        _ => $"TOO {too}",
    };
}

/// <summary>扰动数据传输类型 TOV：1=瞬时值。</summary>
public static class Tov103
{
    public const byte InstantaneousValues = 1;
}

/// <summary>ACC 扰动通道归属（相别）：0 全局、1-3 IL1-IL3、4 IN、5+ 装置自定义。</summary>
public static class Acc103
{
    public const byte Global = 0;

    public static string Describe(byte acc) => acc switch
    {
        Global => "全局",
        1 => "IL1",
        2 => "IL2",
        3 => "IL3",
        4 => "IN",
        5 => "VL1E",
        6 => "VL2E",
        7 => "VL3E",
        _ => $"ACC {acc}",
    };
}

/// <summary>
/// 故障状态字节 SOF：bit7 TP 保护跳闸、bit6 TM 跳闸命令、bit5 TEST 试验、bit4 OTEV 事件由装置启动。
/// </summary>
public readonly record struct Sof103(bool TripProtection, bool TripCommand, bool Test, bool EquipmentStart)
{
    public byte Encode()
    {
        var value = (byte)0;
        if (TripProtection)
        {
            value |= 0x80;
        }

        if (TripCommand)
        {
            value |= 0x40;
        }

        if (Test)
        {
            value |= 0x20;
        }

        if (EquipmentStart)
        {
            value |= 0x10;
        }

        return value;
    }

    public static Sof103 Decode(byte value) => new(
        TripProtection: (value & 0x80) != 0,
        TripCommand: (value & 0x40) != 0,
        Test: (value & 0x20) != 0,
        EquipmentStart: (value & 0x10) != 0);
}

/// <summary>
/// IEC 60870-5-103 二进制时间 CP32Time2a（4 字节）：毫秒(LE16，含秒) + 分钟(低6位|IV bit7) + 小时(低5位|SU bit7)。
/// 用于 ASDU 26 扰动数据传输准备就绪的故障起始时刻。
/// </summary>
public readonly record struct Cp32Time2a103(int Millisecond, int Minute, int Hour, bool Invalid, bool SummerTime)
{
    public static Cp32Time2a103 FromDateTime(DateTime time) => new(
        time.Second * 1000 + time.Millisecond, time.Minute, time.Hour, Invalid: false, time.IsDaylightSavingTime());

    public byte[] Encode()
    {
        var ms = Math.Clamp(Millisecond, 0, 59999);
        return
        [
            (byte)(ms & 0xFF),
            (byte)(ms >> 8),
            (byte)(Math.Clamp(Minute, 0, 59) | (Invalid ? 0x80 : 0)),
            (byte)(Math.Clamp(Hour, 0, 23) | (SummerTime ? 0x80 : 0)),
        ];
    }

    public static Cp32Time2a103 Decode(ReadOnlySpan<byte> bytes) => new(
        Millisecond: bytes[0] | (bytes[1] << 8),
        Minute: bytes[2] & 0x3F,
        Hour: bytes[3] & 0x1F,
        Invalid: (bytes[2] & 0x80) != 0,
        SummerTime: (bytes[3] & 0x80) != 0);

    /// <summary>CP32 不含日期，取当天日期拼时/分/秒毫秒。</summary>
    public DateTime ToDateTime()
    {
        var today = DateTime.Today;
        var ms = Math.Clamp(Millisecond, 0, 59999);
        return new DateTime(today.Year, today.Month, today.Day,
            Math.Clamp(Hour, 0, 23), Math.Clamp(Minute, 0, 59), ms / 1000, ms % 1000);
    }
}

/// <summary>扰动录波通道：ACC 相别 + 一次/二次值与变比 + 归一化采样（−32767…32767）。</summary>
public sealed class DisturbanceChannel103Model
{
    /// <summary>通道归属（ACC：1-3 三相电流、4 中性点等）。</summary>
    public int Acc { get; set; }

    /// <summary>一次额定值（RPV，float）。</summary>
    public double Primary { get; set; }

    /// <summary>二次额定值（RSV，float）。</summary>
    public double Secondary { get; set; }

    /// <summary>额定因数（RFA，float）。</summary>
    public double Factor { get; set; } = 1.0;

    /// <summary>采样点数（全通道一致，见记录的 NOE）。</summary>
    public int SampleCount { get; set; }

    /// <summary>正常段正弦幅值（归一化，0-1）。</summary>
    public double Amplitude { get; set; } = 0.3;

    /// <summary>故障段起始采样序号（0 表示无故障段）。</summary>
    public int FaultStartIndex { get; set; }

    /// <summary>故障段幅值（归一化）。</summary>
    public double FaultAmplitude { get; set; } = 0.9;

    /// <summary>生成归一化采样：工频正弦，故障段（含 2 次谐波模拟）幅值增强。</summary>
    public short[] GenerateSamples()
    {
        var samples = new short[SampleCount];
        const double cycleSamples = 20.0; // 每周波采样数（50Hz/1kHz）
        for (var i = 0; i < SampleCount; i++)
        {
            var amplitude = FaultStartIndex > 0 && i >= FaultStartIndex ? FaultAmplitude : Amplitude;
            var value = amplitude * Math.Sin(2.0 * Math.PI * i / cycleSamples);
            if (FaultStartIndex > 0 && i >= FaultStartIndex)
            {
                // 故障段叠加衰减直流分量，模拟突变量
                value += 0.2 * FaultAmplitude * Math.Exp(-(i - FaultStartIndex) / 40.0);
            }

            samples[i] = (short)Math.Clamp((int)(value * 32767), short.MinValue, short.MaxValue);
        }

        return samples;
    }
}

/// <summary>一条扰动记录：故障序号 FAN + 状态 SOF + 起始时刻 + 采样间隔 + 通道集。</summary>
public sealed class DisturbanceRecord103Model
{
    /// <summary>故障序号（1-65535）。</summary>
    public int Fan { get; set; }

    public Sof103 Sof { get; set; } = new(TripProtection: true, TripCommand: true, Test: false, EquipmentStart: true);

    /// <summary>故障起始时刻。</summary>
    public DateTime FaultTime { get; set; } = DateTime.Now;

    /// <summary>采样间隔（微秒）。</summary>
    public int SampleIntervalUs { get; set; } = 1000;

    public List<DisturbanceChannel103Model> Channels { get; set; } = [];

    public int TotalSamples => Channels.Count > 0 ? Channels[0].SampleCount : 0;

    // SOF 转发属性（Sof 为 readonly record struct，嵌套绑定不可写；写路径 with 重建）
    public bool SofTripProtection
    {
        get => Sof.TripProtection;
        set => Sof = Sof with { TripProtection = value };
    }

    public bool SofTripCommand
    {
        get => Sof.TripCommand;
        set => Sof = Sof with { TripCommand = value };
    }

    public bool SofTest
    {
        get => Sof.Test;
        set => Sof = Sof with { Test = value };
    }

    public bool SofEquipmentStart
    {
        get => Sof.EquipmentStart;
        set => Sof = Sof with { EquipmentStart = value };
    }
}

/// <summary>扰动 ASDU 数据段编码（数据区位于 FUN/INF 之后；FUN=255、COT=31）。</summary>
public static class DisturbanceAsdu
{
    /// <summary>ASDU 23 被记录的扰动表：每条 FAN(2)+SOF(1)+CP56(7)，VSQ 记录数。</summary>
    public static Asdu103 DisturbanceList(byte ca, IReadOnlyList<DisturbanceRecord103Model> records)
    {
        var data = new byte[records.Count * 10];
        var offset = 0;
        foreach (var record in records)
        {
            var fan = (ushort)record.Fan;
            data[offset++] = (byte)(fan & 0xFF);
            data[offset++] = (byte)(fan >> 8);
            data[offset++] = record.Sof.Encode();
            Cp56Time2a103.FromDateTime(record.FaultTime).Encode().CopyTo(data, offset);
            offset += 7;
        }

        return new Asdu103(TypeId103.DisturbanceList, true, records.Count, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0, data);
    }

    /// <summary>ASDU 24/25：TOO(1)+TOV(1)+FAN(2)+ACC(1)。</summary>
    public static Asdu103 DisturbanceCommand(byte typeId, byte ca, byte too, int fan, byte acc, byte cot)
    {
        return new Asdu103(typeId, true, 1, cot, false, ca, Asdu103.GlobalFun, 0,
            [too, Tov103.InstantaneousValues, (byte)(fan & 0xFF), (byte)(fan >> 8), acc]);
    }

    public static (byte Too, byte Tov, int Fan, byte Acc) DecodeCommand(Asdu103 asdu)
    {
        return (asdu.Data[0], asdu.Data[1], asdu.Data[2] | (asdu.Data[3] << 8), asdu.Data[4]);
    }

    /// <summary>
    /// ASDU 26 扰动数据传输准备就绪：
    /// TOO+TOV+FAN+NOF(本记录故障数?2)+NOC(通道数1)+NOE(每通道元素数2)+INT(采样间隔µs 2)+CP32(4)。
    /// </summary>
    public static Asdu103 TransferReady(byte ca, byte too, DisturbanceRecord103Model record, int recordedCount)
    {
        var data = new byte[15];
        data[0] = too;
        data[1] = Tov103.InstantaneousValues;
        var fan = (ushort)record.Fan;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = (byte)(recordedCount & 0xFF);
        data[5] = (byte)(recordedCount >> 8);
        data[6] = (byte)record.Channels.Count;
        var noe = (ushort)record.TotalSamples;
        data[7] = (byte)(noe & 0xFF);
        data[8] = (byte)(noe >> 8);
        var interval = (ushort)record.SampleIntervalUs;
        data[9] = (byte)(interval & 0xFF);
        data[10] = (byte)(interval >> 8);
        Cp32Time2a103.FromDateTime(record.FaultTime).Encode().CopyTo(data, 11);
        return new Asdu103(TypeId103.DisturbanceReady, true, 1, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0, data);
    }

    /// <summary>ASDU 27 通道一次值：TOO+TOV+FAN+ACC+RPV(4f)+RSV(4f)+RFA(4f)。</summary>
    public static Asdu103 ChannelRating(byte ca, byte too, int fan, DisturbanceChannel103Model channel)
    {
        var data = new byte[17];
        data[0] = too;
        data[1] = Tov103.InstantaneousValues;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = (byte)channel.Acc;
        BitConverter.GetBytes((float)channel.Primary).CopyTo(data, 5);
        BitConverter.GetBytes((float)channel.Secondary).CopyTo(data, 9);
        BitConverter.GetBytes((float)channel.Factor).CopyTo(data, 13);
        return new Asdu103(TypeId103.ChannelRating, true, 1, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0, data);
    }

    /// <summary>
    /// ASDU 30 通道扰动数据段：TOO+TOV+FAN+ACC+NDV(本段值数1)+NFE(首元素序号2)+SDV×NDV（归一化 int16 LE）。
    /// </summary>
    public static Asdu103 ChannelData(byte ca, byte too, int fan, byte acc, short[] samples, int offset, int count)
    {
        var data = new byte[9 + count * 2];
        data[0] = too;
        data[1] = Tov103.InstantaneousValues;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = acc;
        data[5] = (byte)count;
        var nfe = (ushort)(offset + 1); // 元素序号从 1 起
        data[6] = (byte)(nfe & 0xFF);
        data[7] = (byte)(nfe >> 8);
        for (var i = 0; i < count; i++)
        {
            var raw = samples[offset + i];
            data[8 + i * 2] = (byte)(raw & 0xFF);
            data[9 + i * 2] = (byte)((raw >> 8) & 0xFF);
        }

        return new Asdu103(TypeId103.ChannelData, true, 1, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0, data);
    }

    /// <summary>ASDU 31 通道/扰动传输终止：TOO+TOV+FAN+ACC。</summary>
    public static Asdu103 ChannelEnd(byte ca, byte too, int fan, byte acc)
    {
        return new Asdu103(TypeId103.ChannelEnd, true, 1, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0,
            [too, Tov103.InstantaneousValues, (byte)(fan & 0xFF), (byte)(fan >> 8), acc]);
    }

    /// <summary>ASDU 29 带标签的通道扰动数据：FAN(2)+NOT(标签数)+TAP(标签位置)+NOT×(FUN+INF+DPI)。</summary>
    public static Asdu103 DisturbanceTags(byte ca, int fan, int tap, IReadOnlyList<(byte Fun, byte Inf, int Dpi)> tags)
    {
        var data = new byte[5 + tags.Count * 3];
        data[0] = (byte)(fan & 0xFF);
        data[1] = (byte)(fan >> 8);
        data[2] = (byte)tags.Count;
        var tap16 = (ushort)tap;
        data[3] = (byte)(tap16 & 0xFF);
        data[4] = (byte)(tap16 >> 8);
        for (var i = 0; i < tags.Count; i++)
        {
            data[5 + i * 3] = tags[i].Fun;
            data[6 + i * 3] = tags[i].Inf;
            data[7 + i * 3] = (byte)(tags[i].Dpi & 0x03);
        }

        return new Asdu103(TypeId103.DisturbanceTags, true, 1, Cot103.DisturbanceTransfer, false, ca, Asdu103.GlobalFun, 0, data);
    }

    // —— 客户端解码 ——

    public sealed record DisturbanceEntry(int Fan, Sof103 Sof, DateTime FaultTime);

    public static List<DisturbanceEntry> DecodeDisturbanceList(Asdu103 asdu)
    {
        var entries = new List<DisturbanceEntry>();
        for (var offset = 0; offset + 10 <= asdu.Data.Length; offset += 10)
        {
            entries.Add(new DisturbanceEntry(
                Fan: asdu.Data[offset] | (asdu.Data[offset + 1] << 8),
                Sof: Sof103.Decode(asdu.Data[offset + 2]),
                FaultTime: Cp56Time2a103.Decode(asdu.Data.AsSpan(offset + 3, 7)).ToDateTime()));
        }

        return entries;
    }

    public sealed record ChannelDataSegment(int Fan, byte Acc, int FirstElement, short[] Values);

    public static ChannelDataSegment DecodeChannelData(Asdu103 asdu)
    {
        // 头部 8 字节（TOO/TOV/FAN/ACC/NDV/NFE）+ NDV×2；第三方装置短帧须报错而非越界
        if (asdu.Data.Length < 8)
        {
            throw new FormatException($"通道数据段数据区不足 8 字节（{asdu.Data.Length}）");
        }

        var count = asdu.Data[5];
        if (asdu.Data.Length < 8 + count * 2)
        {
            throw new FormatException($"通道数据段声明 {count} 点但数据区仅 {asdu.Data.Length} 字节");
        }

        var values = new short[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (short)(asdu.Data[8 + i * 2] | (asdu.Data[9 + i * 2] << 8));
        }

        return new ChannelDataSegment(
            Fan: asdu.Data[2] | (asdu.Data[3] << 8),
            Acc: asdu.Data[4],
            FirstElement: asdu.Data[6] | (asdu.Data[7] << 8),
            Values: values);
    }
}
