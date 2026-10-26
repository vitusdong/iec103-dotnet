using System.Text;

namespace Iec103;

/// <summary>通用分类标识号 GIN：组（1 字节）+ 条目（1 字节）。组 0 条目 0 用于"读全目录"。</summary>
public readonly record struct Gin103(byte Group, byte Entry)
{
    public static Gin103 Zero => new(0, 0);

    public bool IsZero => Group == 0 && Entry == 0;

    public byte[] Encode() => [Group, Entry];

    public static Gin103 Decode(ReadOnlySpan<byte> bytes) => new(bytes[0], bytes[1]);

    public override string ToString() => $"{Group}.{Entry}";
}

/// <summary>描述类型 KOD（IEC 60870-5-103 7.2.6.32）。</summary>
public static class Kod103
{
    public const byte ActualValue = 1;
    public const byte DefaultValue = 2;
    public const byte Range = 3;
    public const byte Precision = 5;
    public const byte Factor = 6;
    public const byte PercentReference = 7;
    public const byte Enumeration = 8;
    public const byte Dimension = 9;
    public const byte Description = 10;
    public const byte PasswordEntry = 12;
    public const byte ReadOnly = 13;
    public const byte WriteOnly = 14;
    public const byte CorrespondingFunInf = 19;
    public const byte CorrespondingEvent = 20;
    public const byte EnumeratedTextArray = 21;
    public const byte EnumeratedValueArray = 22;
    public const byte RelatedEntries = 23;

    public static string Describe(byte kod) => kod switch
    {
        ActualValue => "实际值",
        DefaultValue => "缺省值",
        Range => "量程（最小、最大、步长）",
        Precision => "精度（n,m）",
        Factor => "因子",
        PercentReference => "% 基准",
        Enumeration => "枚举",
        Dimension => "量纲",
        Description => "描述",
        PasswordEntry => "口令录入",
        ReadOnly => "只读",
        WriteOnly => "只写",
        CorrespondingFunInf => "对应功能类型与信息序号",
        CorrespondingEvent => "对应事件",
        EnumeratedTextArray => "枚举文本数组",
        EnumeratedValueArray => "枚举值数组",
        RelatedEntries => "相关条目",
        _ => $"KOD {kod}",
    };
}

/// <summary>通用分类数据类型（GDD 的类型字段，IEC 60870-5-103 7.2.6.33）。</summary>
public static class GddDataType103
{
    public const byte None = 0;
    public const byte Ascii = 1;
    public const byte Bitstring = 2;
    public const byte UnsignedInt = 3;
    public const byte SignedInt = 4;
    public const byte UnsignedFixed = 5;
    public const byte Normalized = 6;
    public const byte ShortReal = 7;
    public const byte Real = 8;
    public const byte DoublePoint = 9;
    public const byte SinglePoint = 10;
    public const byte BinaryTime = 14;
    public const byte Gin = 15;
    public const byte RelativeTime = 16;
    public const byte FunInf = 17;
    public const byte ExternalText = 21;
    public const byte GenericReplyCode = 22;
    public const byte DataStructure = 23;
    public const byte Index = 24;

    public static string Describe(byte dataType) => dataType switch
    {
        None => "无数据",
        Ascii => "ASCII 字符串",
        Bitstring => "位串",
        UnsignedInt => "无符号整数",
        SignedInt => "有符号整数",
        UnsignedFixed => "无符号定点（/256）",
        Normalized => "归一化（/32767）",
        ShortReal => "短实数 R32.23",
        Real => "实数 R64.53",
        DoublePoint => "双点信息",
        SinglePoint => "单点信息",
        BinaryTime => "二进制时间",
        Gin => "通用分类标识号",
        RelativeTime => "相对时间",
        FunInf => "功能类型与信息序号",
        ExternalText => "外部文本号",
        GenericReplyCode => "通用分类回复码",
        DataStructure => "数据结构",
        Index => "索引",
        _ => $"类型 {dataType}",
    };
}

/// <summary>通用分类回复码 GRC（写确认数据集的值，类型 22）。</summary>
public static class Grc103
{
    /// <summary>认可（默认）。</summary>
    public const byte Acknowledge = 0;

    /// <summary>通用否定（未认可；细分码因装置而异）。</summary>
    public const byte NotAcknowledged = 1;

    /// <summary>常见否定细分：GIN 不存在。</summary>
    public const byte UnknownGin = 2;

    /// <summary>常见否定细分：条目为只读。</summary>
    public const byte ReadOnlyEntry = 7;

    public static string Describe(byte grc) => grc switch
    {
        Acknowledge => "认可",
        UnknownGin => "GIN 不存在",
        ReadOnlyEntry => "条目只读",
        _ => $"回复码 {grc}",
    };
}

/// <summary>
/// 通用分类数据描述 GDD（3 字节）：数据类型 + 数据宽度 + 数量（bit0-6）与连续标志 CON（bit7）。
/// 位串类型时宽度为位数，其余类型宽度为字节数；GID 长度 = 元素字节数 × 数量。
/// </summary>
public readonly record struct Gdd103(byte DataType, byte DataWidth, byte Number, bool Continuation)
{
    public byte[] Encode() => [DataType, DataWidth, (byte)((Number & 0x7F) | (Continuation ? 0x80 : 0))];

    public static Gdd103 Decode(ReadOnlySpan<byte> bytes) => new(
        DataType: bytes[0],
        DataWidth: bytes[1],
        Number: (byte)(bytes[2] & 0x7F),
        Continuation: (bytes[2] & 0x80) != 0);

    /// <summary>单个元素的编码字节数（位串按位向上取整到字节）。</summary>
    public int ElementSize =>
        DataType == GddDataType103.Bitstring ? Math.Clamp((DataWidth - 1) / 8 + 1, 1, 32) : DataWidth;

    /// <summary>该 GDD 描述的 GID 总字节数。</summary>
    public int GidSize => ElementSize * Number;
}

/// <summary>
/// 通用分类数据集（ASDU 10 的一个条目）：GIN + KOD + GDD + GID。
/// 编码：GIN(2) + KOD(1) + GDD(3) + GID；提供 ASCII/整数/浮点/量程的便捷构造与解码。
/// </summary>
public sealed record GenericDataSet103(Gin103 Gin, byte Kod, Gdd103 Gdd, byte[] Gid)
{
    /// <summary>
    /// OS8ASCII 文本编码：国内装置通用分类描述通行 GB2312 中文（8 位字符集），
    /// 用 GB18030（向下兼容）编解码，纯 ASCII 文本行为与 ASCII 完全一致。
    /// </summary>
    private static readonly Encoding Os8Text = CreateOs8Encoding();

    private static Encoding CreateOs8Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    }

    public int EncodedSize => 6 + Gid.Length;

    public byte[] Encode()
    {
        var payload = new byte[EncodedSize];
        payload[0] = Gin.Group;
        payload[1] = Gin.Entry;
        payload[2] = Kod;
        Gdd.Encode().AsSpan().CopyTo(payload.AsSpan(3));
        Gid.CopyTo(payload, 6);
        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out GenericDataSet103? set, out int consumed)
    {
        set = null;
        if (bytes.Length < 6)
        {
            consumed = 0;
            return false;
        }

        var gdd = Gdd103.Decode(bytes[3..6]);
        var gidSize = gdd.GidSize;
        if (bytes.Length < 6 + gidSize)
        {
            consumed = 0;
            return false;
        }

        set = new GenericDataSet103(Gin103.Decode(bytes), bytes[2], gdd, bytes[6..(6 + gidSize)].ToArray());
        consumed = 6 + gidSize;
        return true;
    }

    // —— 便捷构造 ——

    /// <summary>单数据集 ASCII 字节预算：253(帧) - 6(ASDU 头) - 2(RII+NGD) - 6(数据集头)。</summary>
    public const int MaxAsciiBytes = 239;

    /// <summary>GB18030 编码（与数据集发送/解码一致的编码器）。</summary>
    public static byte[] EncodeGb18030(string text) => Os8Text.GetBytes(text);

    public static GenericDataSet103 Ascii(Gin103 gin, byte kod, string text)
    {
        var data = Os8Text.GetBytes(text);
        if (data.Length > MaxAsciiBytes)
        {
            // 超预算按字符边界截断：拦腰截 GB18030 多字节序列会产生乱码尾，
            // 且宽度字节须始终等于 GID 实际长度（否则对端解析失步）
            var truncated = new List<byte>(MaxAsciiBytes);
            foreach (var ch in text)
            {
                var encoded = Os8Text.GetBytes(ch.ToString());
                if (truncated.Count + encoded.Length > MaxAsciiBytes)
                {
                    break;
                }

                truncated.AddRange(encoded);
            }

            data = [.. truncated];
        }

        return new GenericDataSet103(gin, kod, new Gdd103(GddDataType103.Ascii, (byte)Math.Min(data.Length, 255), 1, false), data);
    }

    public static GenericDataSet103 Unsigned(Gin103 gin, byte kod, uint value, byte width = 4)
    {
        var data = new byte[width];
        for (var i = 0; i < width; i++)
        {
            data[i] = (byte)(value >> (8 * i));
        }

        return new GenericDataSet103(gin, kod, new Gdd103(GddDataType103.UnsignedInt, width, 1, false), data);
    }

    public static GenericDataSet103 Signed(Gin103 gin, byte kod, int value, byte width = 4)
    {
        var data = new byte[width];
        var raw = value;
        for (var i = 0; i < width; i++)
        {
            data[i] = (byte)(raw & 0xFF);
            raw >>= 8;
        }

        return new GenericDataSet103(gin, kod, new Gdd103(GddDataType103.SignedInt, width, 1, false), data);
    }

    public static GenericDataSet103 Float(Gin103 gin, byte kod, float value)
    {
        return new GenericDataSet103(gin, kod, new Gdd103(GddDataType103.ShortReal, 4, 1, false), BitConverter.GetBytes(value));
    }

    /// <summary>量程（KOD=3）：最小值、最大值、步长三个短实数。</summary>
    public static GenericDataSet103 Range(Gin103 gin, float min, float max, float step)
    {
        var data = new byte[12];
        BitConverter.GetBytes(min).CopyTo(data, 0);
        BitConverter.GetBytes(max).CopyTo(data, 4);
        BitConverter.GetBytes(step).CopyTo(data, 8);
        return new GenericDataSet103(gin, Kod103.Range, new Gdd103(GddDataType103.ShortReal, 4, 3, false), data);
    }

    /// <summary>回复码数据集（写确认用，类型 22）。</summary>
    public static GenericDataSet103 ReplyCode(Gin103 gin, byte kod, byte grc)
    {
        return new GenericDataSet103(gin, kod, new Gdd103(GddDataType103.GenericReplyCode, 1, 1, false), [grc]);
    }

    // —— 值解码 ——

    /// <summary>按 GDD 类型解码 GID：ASCII→string，整数→long，浮点→float，位串/其他→原始十六进制。</summary>
    public object? DecodeValue() => Gdd.DataType switch
    {
        GddDataType103.Ascii => Os8Text.GetString(Gid).TrimEnd('\0', ' '),
        GddDataType103.UnsignedInt => DecodeUnsigned(),
        GddDataType103.SignedInt => DecodeSigned(),
        GddDataType103.UnsignedFixed => Gid.Length > 0 ? Gid[0] / 256f : null,
        GddDataType103.Normalized => Gid.Length >= 2 ? BitConverter.ToInt16(Gid, 0) / 32767f : null,
        GddDataType103.ShortReal => Gid.Length >= 4 ? BitConverter.ToSingle(Gid, 0) : null,
        GddDataType103.Real => Gid.Length >= 8 ? BitConverter.ToDouble(Gid, 0) : null,
        _ => Convert.ToHexString(Gid),
    };

    private long DecodeUnsigned()
    {
        long value = 0;
        for (var i = 0; i < Gid.Length; i++)
        {
            value |= (long)Gid[i] << (8 * i);
        }

        return value;
    }

    private long DecodeSigned()
    {
        var value = DecodeUnsigned();
        var bits = Gid.Length * 8;
        if (bits is > 0 and < 64 && (Gid[^1] & 0x80) != 0)
        {
            value |= -1L << bits;
        }

        return value;
    }

    public override string ToString()
    {
        var value = DecodeValue();
        return $"GIN={Gin} KOD={Kod103.Describe(Kod)} {GddDataType103.Describe(Gdd.DataType)}" +
            (Gdd.Number > 1 ? $"×{Gdd.Number}" : string.Empty) +
            (value is null ? string.Empty : $" = {value}");
    }
}

/// <summary>NGD（数据集数目与后续标志）：bit5-0 数目、bit7 后续标志 D（后续 ASDU 同 RII）。</summary>
public static class Ngd103
{
    public static byte Encode(int count, bool continuation)
    {
        return (byte)(Math.Clamp(count, 0, 63) | (continuation ? 0x80 : 0));
    }

    public static (int Count, bool Continuation) Decode(byte ngd)
    {
        return (ngd & 0x3F, (ngd & 0x80) != 0);
    }
}
