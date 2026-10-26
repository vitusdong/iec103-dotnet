namespace Iec103;

/// <summary>IEC 60870-5-103（DL/T 667-1999）ASDU 类型标识，本工具已实现部分。</summary>
public static class TypeId103
{
    /// <summary>带时标 CP56Time2a 的监视方向信息（1 级数据）。</summary>
    public const byte TimeTaggedMessage = 1;

    /// <summary>带相对时标 CP32Time2a 和故障序号的监视方向信息（专用范围）。</summary>
    public const byte TimeTaggedMessagePrivate = 2;

    /// <summary>标识报文（复位/总召唤时应答）。</summary>
    public const byte Identification = 5;

    /// <summary>时间同步。</summary>
    public const byte ClockSync = 6;

    /// <summary>总召唤命令。</summary>
    public const byte GeneralInterrogation = 7;

    /// <summary>总召唤终止。</summary>
    public const byte GeneralInterrogationEnd = 8;

    /// <summary>测量值（循环/2 级数据）。</summary>
    public const byte Measurand = 9;

    /// <summary>通用分类数据（读响应/写命令/写确认，COT 区分语义）。</summary>
    public const byte GenericData = 10;

    /// <summary>通用分类标识（响应读标识）。</summary>
    public const byte GenericIdentification = 11;

    /// <summary>通用分类读命令（RII+NOG+GIN/KOD 列表）。</summary>
    public const byte GenericCommand = 21;

    /// <summary>一般命令。</summary>
    public const byte GeneralCommand = 20;

    /// <summary>被记录的扰动表（FAN+SOF+CP56 × 记录数）。</summary>
    public const byte DisturbanceList = 23;

    /// <summary>扰动数据传输的命令（TOO/TOV/FAN/ACC）。</summary>
    public const byte DisturbanceCommand = 24;

    /// <summary>带标签的通道扰动数据（标签传输）。</summary>
    public const byte DisturbanceTags = 29;

    /// <summary>扰动数据传输的认可。</summary>
    public const byte DisturbanceAck = 25;

    /// <summary>扰动数据传输准备就绪（FAN/NOC/NOE/INT/CP32）。</summary>
    public const byte DisturbanceReady = 26;

    /// <summary>通道一次值（RPV/RSV/RFA）。</summary>
    public const byte ChannelRating = 27;

    /// <summary>带标签的通道扰动数据段（NDV/NFE/SDV）。</summary>
    public const byte ChannelData = 30;

    /// <summary>通道扰动数据传输终止。</summary>
    public const byte ChannelEnd = 31;

    public static string Describe(byte typeId) => typeId switch
    {
        TimeTaggedMessage => "带时标 CP56Time2a 的监视方向信息",
        TimeTaggedMessagePrivate => "带相对时标的监视方向信息（专用范围）",
        Identification => "标识报文",
        ClockSync => "时间同步",
        GeneralInterrogation => "总召唤",
        GeneralInterrogationEnd => "总召唤终止",
        Measurand => "测量值",
        GenericData => "通用分类数据",
        GeneralCommand => "一般命令",
        DisturbanceCommand => "扰动数据传输的命令",
        DisturbanceAck => "扰动数据传输的认可",
        DisturbanceTags => "带标签的通道扰动数据",
        DisturbanceList => "被记录的扰动表",
        DisturbanceReady => "扰动数据传输准备就绪",
        ChannelRating => "通道一次值",
        ChannelData => "通道扰动数据段",
        ChannelEnd => "通道扰动数据传输终止",
        _ => $"ASDU {typeId}",
    };
}

/// <summary>
/// 103 传输原因（COT，1 字节，bit7 为 P/N 否定位）。
/// 与 101/104 在 3 之后分道（103 专有表，ARIEC103 与 Wireshark dissector 双源核对）：
/// 3=复位FCB、4=复位CU（ZLG 标识报文 COT=4 实测吻合）、5=启动/重启、8=时间同步、
/// 9=总召唤启动、10=终止、11/12=远方/当地操作（亦作命令返送）、20/21=命令肯定/否定确认、
/// 40-44=通用分类系列。
/// 总召唤数据点回送默认 COT=11（工程通行；部分装置用 5/9）。
/// </summary>
public static class Cot103
{
    public const byte Spontaneous = 1;
    public const byte Cyclic = 2;
    public const byte ResetFcb = 3;
    public const byte ResetCu = 4;
    public const byte Restart = 5;
    public const byte PowerOn = 6;
    public const byte TestMode = 7;
    public const byte ClockSync = 8;
    public const byte GiStart = 9;
    public const byte GiStop = 10;
    public const byte RemoteOperation = 11;
    public const byte LocalOperation = 12;
    public const byte CommandAck = 20;
    public const byte CommandNack = 21;
    public const byte DisturbanceTransfer = 31;
    public const byte GenericWrite = 40;
    public const byte GenericWriteAckPositive = 40;
    public const byte GenericWriteAckNegative = 41;
    public const byte GenericRead = 42;
    public const byte GenericReadValid = 42;
    public const byte GenericReadInvalid = 43;
    public const byte GenericWriteConfirm = 44;

    private const byte NegativeMask = 0x80;

    public static string Describe(byte cot)
    {
        var name = (byte)(cot & 0x7F) switch
        {
            Spontaneous => "突发",
            Cyclic => "周期、循环",
            ResetFcb => "复位帧计数位（FCB）",
            ResetCu => "复位通信单元（CU）",
            Restart => "启动/重启",
            PowerOn => "上电",
            TestMode => "测试模式",
            ClockSync => "时间同步",
            GiStart => "总召唤启动",
            GiStop => "总召唤终止",
            RemoteOperation => "远方操作/返送（远方命令）",
            LocalOperation => "当地操作/返送（当地命令）",
            CommandAck => "命令肯定确认",
            CommandNack => "命令否定确认",
            DisturbanceTransfer => "扰动数据传输",
            GenericWriteAckPositive => "通用分类写命令肯定确认",
            GenericWriteAckNegative => "通用分类写命令否定确认",
            GenericReadValid => "通用分类读命令有效数据响应",
            GenericReadInvalid => "通用分类读命令无效数据响应",
            GenericWriteConfirm => "通用分类写确认",
            _ => $"COT {cot & 0x7F}",
        };
        return (cot & NegativeMask) != 0 ? $"{name}（否定 N）" : name;
    }
}

/// <summary>
/// IEC 60870-5-103 时标 CP56Time2a（7 字节，与 101/104 布局一致）：
/// 毫秒 LE16；分钟低 6 位 + IV(bit7)；小时低 5 位 + SU(bit7)；日低 5 位；月低 4 位；年 0-99。
/// </summary>
public readonly record struct Cp56Time2a103(int Millisecond, int Minute, int Hour, int Day, int Month, int Year, bool Invalid, bool SummerTime)
{
    public static Cp56Time2a103 FromDateTime(DateTime time)
    {
        return new Cp56Time2a103(
            Millisecond: time.Second * 1000 + time.Millisecond, // CP56 毫秒域 0-59999 含秒
            Minute: time.Minute,
            Hour: time.Hour,
            Day: time.Day,
            Month: time.Month,
            Year: time.Year % 100,
            Invalid: false,
            SummerTime: time.IsDaylightSavingTime());
    }

    public DateTime ToDateTime()
    {
        var year = 2000 + Year;
        var day = Math.Clamp(Day, 1, DateTime.DaysInMonth(year, Math.Clamp(Month, 1, 12)));
        var ms = Math.Clamp(Millisecond, 0, 59999);
        return new DateTime(year, Math.Clamp(Month, 1, 12), day,
            Math.Clamp(Hour, 0, 23), Math.Clamp(Minute, 0, 59), ms / 1000, ms % 1000);
    }

    public byte[] Encode()
    {
        var ms = Math.Clamp(Millisecond, 0, 59999);
        var minute = (byte)(Math.Clamp(Minute, 0, 59) | (Invalid ? 0x80 : 0));
        var hour = (byte)(Math.Clamp(Hour, 0, 23) | (SummerTime ? 0x80 : 0));
        return
        [
            (byte)(ms & 0xFF),
            (byte)(ms >> 8),
            minute,
            hour,
            (byte)(Math.Clamp(Day, 1, 31) & 0x1F),
            (byte)(Math.Clamp(Month, 1, 12) & 0x0F),
            (byte)(Math.Clamp(Year, 0, 99) & 0x7F),
        ];
    }

    public static Cp56Time2a103 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 7)
        {
            throw new ArgumentException($"CP56Time2a 需要 7 字节，实际 {bytes.Length}", nameof(bytes));
        }

        return new Cp56Time2a103(
            Millisecond: bytes[0] | (bytes[1] << 8),
            Minute: bytes[2] & 0x3F,
            Hour: bytes[3] & 0x1F,
            Day: bytes[4] & 0x1F,
            Month: bytes[5] & 0x0F,
            Year: bytes[6] & 0x7F,
            Invalid: (bytes[2] & 0x80) != 0,
            SummerTime: (bytes[3] & 0x80) != 0);
    }

    public override string ToString()
    {
        return $"{Year + 2000:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Millisecond / 1000:D2}.{Millisecond % 1000:D3}" +
            (Invalid ? " IV" : string.Empty);
    }
}

/// <summary>103 监视方向 DPI + 品质（TI=1 信息体第一字节）：低 2 位 DPI，bit2 IV、bit3 NT、bit4 SB、bit5 BL、bit6 EI。</summary>
public readonly record struct DpiQuality103(int Dpi, bool Invalid, bool NonTopical, bool Substituted, bool Blocked, bool El)
{
    /// <summary>四个附加信息字节（DL/T 667 ASDU1 兼容方向固定为 FF）。</summary>
    public static readonly byte[] UnusedExtraInfo = [0xFF, 0xFF, 0xFF, 0xFF];

    public byte Encode()
    {
        var value = (byte)(Math.Clamp(Dpi, 0, 3) & 0x03);
        if (Invalid)
        {
            value |= 0x04;
        }

        if (NonTopical)
        {
            value |= 0x08;
        }

        if (Substituted)
        {
            value |= 0x10;
        }

        if (Blocked)
        {
            value |= 0x20;
        }

        if (El)
        {
            value |= 0x40;
        }

        return value;
    }

    public static DpiQuality103 Decode(byte value)
    {
        return new DpiQuality103(
            Dpi: value & 0x03,
            Invalid: (value & 0x04) != 0,
            NonTopical: (value & 0x08) != 0,
            Substituted: (value & 0x10) != 0,
            Blocked: (value & 0x20) != 0,
            El: (value & 0x40) != 0);
    }

    /// <summary>仅指定 DPI 状态（品质位全零），workspace 点位直接映射用。</summary>
    public static DpiQuality103 FromDpi(int dpi)
    {
        return new DpiQuality103(dpi, false, false, false, false, false);
    }

    public string State => Dpi switch
    {
        0 => "不确定",
        1 => "分（OFF）",
        2 => "合（ON）",
        _ => "不确定（品质异常）",
    };
}

/// <summary>
/// IEC 60870-5-103 应用服务数据单元：
/// TI(1) VSQ(1)（bit7 SQ + 低 7 位数目）COT(1)（bit7 P/N）公共地址 CA(1)
/// 功能类型 FUN(1) 信息序号 INF(1) + 其余数据。
/// FUN/INF 是 103 特有的两级寻址：兼容范围 0-127、专用范围 160-255、255 为全局。
/// </summary>
public sealed record Asdu103(byte TypeId, bool Sq, int Count, byte Cot, bool Negative, byte Ca, byte Fun, byte Inf, byte[] Data)
{
    public const byte GlobalFun = 255;

    /// <summary>全局信息序号（DL/T 667）：80 兼容范围标识、81 专用范围标识。</summary>
    public const byte InfCompatIdent = 80;

    public const byte InfPrivateIdent = 81;

    public byte[] Encode()
    {
        var vsq = (byte)((Sq ? 0x80 : 0) | (Count & 0x7F));
        var cot = (byte)((Cot & 0x7F) | (Negative ? 0x80 : 0));
        var payload = new byte[Data.Length + 6];
        payload[0] = TypeId;
        payload[1] = vsq;
        payload[2] = cot;
        payload[3] = Ca;
        payload[4] = Fun;
        payload[5] = Inf;
        Data.CopyTo(payload, 6);
        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Asdu103? asdu, out string? error)
    {
        asdu = null;
        if (bytes.Length < 6)
        {
            error = $"ASDU 至少 6 字节（TI/VSQ/COT/CA/FUN/INF），实际 {bytes.Length}";
            return false;
        }

        asdu = new Asdu103(
            TypeId: bytes[0],
            Sq: (bytes[1] & 0x80) != 0,
            Count: bytes[1] & 0x7F,
            Cot: (byte)(bytes[2] & 0x7F),
            Negative: (bytes[2] & 0x80) != 0,
            Ca: bytes[3],
            Fun: bytes[4],
            Inf: bytes[5],
            Data: bytes[6..].ToArray());
        error = null;
        return true;
    }

    // —— 基础四件套工厂 ——

    /// <summary>
    /// 时间同步命令（TI=6，COT=8，数据 CP56Time2a）。
    /// 主站实测多见 FUN=0（如 ZLG E8000 例程），从站回读帧用 FUN=255（全局）。
    /// </summary>
    public static Asdu103 ClockSync(Cp56Time2a103 time, byte ca, byte fun = 0)
    {
        return new Asdu103(TypeId103.ClockSync, true, 1, Cot103.ClockSync, false, ca, fun, 0, time.Encode());
    }

    /// <summary>从站时间同步回读（FUN=255 全局，携带设备当前时钟）。</summary>
    public static Asdu103 ClockSyncEcho(Cp56Time2a103 time, byte ca)
    {
        return ClockSync(time, ca, GlobalFun);
    }

    /// <summary>总召唤命令（TI=7，FUN=255 INF=0，COT=9，数据为扫描序号 SCN）。</summary>
    public static Asdu103 GeneralInterrogation(byte ca, byte scn = 0)
    {
        return new Asdu103(TypeId103.GeneralInterrogation, true, 1, Cot103.GiStart, false, ca, GlobalFun, 0, [scn]);
    }

    /// <summary>总召唤终止（TI=8，COT=10，回带 SCN）。</summary>
    public static Asdu103 GeneralInterrogationEnd(byte ca, byte scn = 0)
    {
        return new Asdu103(TypeId103.GeneralInterrogationEnd, true, 1, Cot103.GiStop, false, ca, GlobalFun, 0, [scn]);
    }

    /// <summary>
    /// 标识报文（TI=5，FUN=255，INF=80 兼容范围 / 81 专用范围），数据为 8 字节 ASCII（空格填充）。
    /// </summary>
    public static Asdu103 Identification(byte ca, bool privateRange, string ascii)
    {
        var text = ascii.Length > 8 ? ascii[..8] : ascii.PadRight(8);
        var data = System.Text.Encoding.ASCII.GetBytes(text);
        return new Asdu103(
            TypeId103.Identification, true, 1,
            privateRange ? Cot103.Restart : Cot103.ResetCu,
            false, ca, GlobalFun, privateRange ? InfPrivateIdent : InfCompatIdent, data);
    }

    /// <summary>
    /// 带时标 CP56Time2a 的监视方向信息（TI=1）。
    /// 信息体数据 = DPI/品质(1) + 4 个附加信息字节（兼容方向固定 FF）+ CP56Time2a(7)。
    /// </summary>
    public static Asdu103 TimeTaggedMessage(byte ca, byte fun, byte inf, DpiQuality103 dpi, Cp56Time2a103 time, byte cot, byte[]? extraInfo = null)
    {
        var data = new byte[12];
        data[0] = dpi.Encode();
        (extraInfo ?? DpiQuality103.UnusedExtraInfo).AsSpan(0, 4).CopyTo(data.AsSpan(1));
        time.Encode().AsSpan().CopyTo(data.AsSpan(5));
        return new Asdu103(TypeId103.TimeTaggedMessage, true, 1, cot, false, ca, fun, inf, data);
    }

    // —— 通用分类（FUN=254 通用功能类型，INF=0）——

    /// <summary>通用分类 ASDU 的功能类型（GEN）。</summary>
    public const byte GenericFun = 254;

    /// <summary>读全目录命令（TI=21，COT=42）：RII + NOG=1 + GIN 0000 + KOD=描述。</summary>
    public static Asdu103 GenericReadAll(byte ca, byte rii)
    {
        var data = new byte[5];
        data[0] = rii;
        data[1] = 1;
        Gin103.Zero.Encode().CopyTo(data, 2);
        data[4] = Kod103.Description;
        return new Asdu103(TypeId103.GenericCommand, true, 1, Cot103.GenericRead, false, ca, GenericFun, 0, data);
    }

    /// <summary>读指定条目命令（TI=21，COT=42）：RII + NOG + 每条 GIN/KOD。</summary>
    public static Asdu103 GenericRead(byte ca, byte rii, params (Gin103 Gin, byte Kod)[] entries)
    {
        var data = new byte[2 + entries.Length * 3];
        data[0] = rii;
        data[1] = (byte)entries.Length;
        var offset = 2;
        foreach (var (gin, kod) in entries)
        {
            gin.Encode().CopyTo(data, offset);
            data[offset + 2] = kod;
            offset += 3;
        }

        return new Asdu103(TypeId103.GenericCommand, true, 1, Cot103.GenericRead, false, ca, GenericFun, 0, data);
    }

    /// <summary>
    /// 通用分类数据（TI=10）：数据 = RII + NGD + 数据集序列。
    /// COT 语义：40 主站写命令 / 42 读响应（有效）/ 43 读响应（无效）。
    /// </summary>
    public static Asdu103 GenericDataAsdu(byte ca, byte rii, bool continuation, byte cot, IReadOnlyList<GenericDataSet103> sets)
    {
        var data = new byte[2 + sets.Sum(s => s.EncodedSize)];
        data[0] = rii;
        data[1] = Ngd103.Encode(sets.Count, continuation);
        var offset = 2;
        foreach (var set in sets)
        {
            set.Encode().CopyTo(data, offset);
            offset += set.EncodedSize;
        }

        return new Asdu103(TypeId103.GenericData, true, 1, cot, false, ca, GenericFun, 0, data);
    }

    /// <summary>
    /// 带相对时标和故障序号的监视方向信息（TI=2，专用范围 FUN 160-255）。
    /// 信息体数据 = DPI/品质(1) + 相对时标 RET(2 LE ms) + 故障序号 FAN(2 LE) + CP56Time2a(7)。
    /// </summary>
    public static Asdu103 ProtectionEvent(byte ca, byte fun, byte inf, DpiQuality103 dpi, int relativeTimeMs, int fan, Cp56Time2a103 time, byte cot)
    {
        var data = new byte[12];
        data[0] = dpi.Encode();
        data[1] = (byte)(relativeTimeMs & 0xFF);
        data[2] = (byte)((relativeTimeMs >> 8) & 0xFF);
        data[3] = (byte)(fan & 0xFF);
        data[4] = (byte)((fan >> 8) & 0xFF);
        time.Encode().AsSpan().CopyTo(data.AsSpan(5));
        return new Asdu103(TypeId103.TimeTaggedMessagePrivate, true, 1, cot, false, ca, fun, inf, data);
    }

    /// <summary>
    /// 一般命令（TI=20，COT=20）：FUN/INF 指定目标，数据 = DCO+RII。
    /// DCO 按 DL/T 667：bit0-1 命令值 DCT（01 分/10 合/11 不确定；00 兼容单点 SCS OFF=分），
    /// bit6-7 限定词 QU（0 默认）。
    /// </summary>
    public static Asdu103 GeneralCommand(byte ca, byte fun, byte inf, int dco, byte rii)
    {
        return new Asdu103(TypeId103.GeneralCommand, true, 1, Cot103.CommandAck, false, ca, fun, inf,
            [(byte)(dco & 0x03), rii]);
    }

    /// <summary>测量值（TI=9，COT=2 循环）：FUN/INF 定位，数据 = 有符号 int16 LE（工程值由主站按变比换算）。</summary>
    public static Asdu103 Measurement(byte ca, byte fun, byte inf, short value)
    {
        return new Asdu103(TypeId103.Measurand, true, 1, Cot103.Cyclic, false, ca, fun, inf,
            [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)]);
    }

    /// <summary>解码测量值（TI=9）数据。</summary>
    public static short DecodeMeasurement(Asdu103 asdu)
    {
        return (short)(asdu.Data[0] | (asdu.Data[1] << 8));
    }

    /// <summary>解码一般命令 DCO 字节：命令值取 bit0-1（DCT：1 分/2 合/3 不确定，0 兼容 SCS OFF），QU 取 bit6-7；RII 可选（缺省 0）。</summary>
    public static (int Dct, int Qu, byte Rii) DecodeGeneralCommand(Asdu103 asdu)
    {
        return (asdu.Data[0] & 0x03, asdu.Data[0] >> 6, asdu.Data.Length > 1 ? asdu.Data[1] : (byte)0);
    }

    /// <summary>解码保护事件（TI=2）的相对时标（ms）与故障序号。</summary>
    public static (int RelativeTimeMs, int Fan) DecodeProtectionEvent(Asdu103 asdu)
    {
        return (asdu.Data[1] | (asdu.Data[2] << 8), asdu.Data[3] | (asdu.Data[4] << 8));
    }
    public static Asdu103 GenericWriteAck(byte ca, byte rii, Gin103 gin, byte kod, byte grc, bool negative)
    {
        var set = GenericDataSet103.ReplyCode(gin, kod, grc);
        var data = new byte[2 + set.EncodedSize];
        data[0] = rii;
        data[1] = Ngd103.Encode(1, false);
        set.Encode().CopyTo(data, 2);
        var cot = negative ? Cot103.GenericWriteAckNegative : Cot103.GenericWriteAckPositive;
        return new Asdu103(TypeId103.GenericData, true, 1, cot, false, ca, GenericFun, 0, data);
    }
}
