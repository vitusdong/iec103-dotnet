namespace Iec103;

/// <summary>主站（PRM=1）链路功能码。DL/T 667-1999 表 2.1。</summary>
public enum PrimaryFunction103
{
    /// <summary>复位通信单元（RESET CU）。FCV=0。</summary>
    ResetCu = 0,

    /// <summary>发送/确认 传送数据（SEND/CONFIRM 用户数据）。FCV=1。</summary>
    SendConfirmed = 3,

    /// <summary>发送/无回答 传送数据（广播）。FCV=0。</summary>
    SendNoReply = 4,

    /// <summary>复位帧计数位（RESET FCB）。FCV=0。</summary>
    ResetFcb = 7,

    /// <summary>请求链路状态。FCV=0。</summary>
    RequestLinkStatus = 9,

    /// <summary>召唤 1 级用户数据（请求/响应）。FCV=1。</summary>
    RequestClass1 = 10,

    /// <summary>召唤 2 级用户数据（请求/响应）。FCV=1。</summary>
    RequestClass2 = 11,
}

/// <summary>从站（PRM=0）链路功能码。DL/T 667-1999 表 2.2。</summary>
public enum SecondaryFunction103
{
    /// <summary>确认帧（ACK）。</summary>
    Ack = 0,

    /// <summary>链路忙，未收到报文（NACK）。</summary>
    Nack = 1,

    /// <summary>以数据响应请求帧。</summary>
    RespondData = 8,

    /// <summary>无所召唤的数据。</summary>
    RespondNoData = 9,

    /// <summary>链路状态。</summary>
    LinkStatus = 11,
}

/// <summary>
/// 链路控制域。位布局（主从同构）：
/// bit6 PRM（1=启动站/主站）、bit5 FCB|ACD、bit4 FCV|DFC、bit0-3 功能码；
/// bit7 恒 0。主站对同一从站的新一轮 SEND/CONFIRM 或 REQUEST 服务需翻转 FCB。
/// </summary>
public readonly record struct LinkControl103(bool IsPrimary, byte Function, bool FcbOrAcd, bool FcvOrDfc)
{
    public byte ToByte()
    {
        var value = (byte)(Function & 0x0F);
        if (IsPrimary)
        {
            value |= 0x40;
        }

        if (FcbOrAcd)
        {
            value |= 0x20;
        }

        if (FcvOrDfc)
        {
            value |= 0x10;
        }

        return value;
    }

    public static LinkControl103 FromByte(byte control)
    {
        return new LinkControl103(
            IsPrimary: (control & 0x40) != 0,
            Function: (byte)(control & 0x0F),
            FcbOrAcd: (control & 0x20) != 0,
            FcvOrDfc: (control & 0x10) != 0);
    }

    // —— 常用构造 ——

    public static LinkControl103 Primary(PrimaryFunction103 function, bool fcb, bool fcv)
    {
        return new LinkControl103(true, (byte)function, fcb, fcv);
    }

    /// <summary>复位通信单元（10 40 …）：FCB=0、FCV=0。</summary>
    public static LinkControl103 ResetCu()
    {
        return Primary(PrimaryFunction103.ResetCu, false, false);
    }

    /// <summary>发送/确认用户数据（0x53/0x73 随 FCB 翻转）。</summary>
    public static LinkControl103 SendConfirmed(bool fcb)
    {
        return Primary(PrimaryFunction103.SendConfirmed, fcb, true);
    }

    /// <summary>召唤 1 级用户数据（0x5A/0x7A）。</summary>
    public static LinkControl103 RequestClass1(bool fcb)
    {
        return Primary(PrimaryFunction103.RequestClass1, fcb, true);
    }

    /// <summary>召唤 2 级用户数据（0x5B/0x7B）。</summary>
    public static LinkControl103 RequestClass2(bool fcb)
    {
        return Primary(PrimaryFunction103.RequestClass2, fcb, true);
    }

    /// <summary>从站响应：确认/数据/无数据，ACD=1 表示有 1 级数据待取。</summary>
    public static LinkControl103 Secondary(SecondaryFunction103 function, bool acd = false, bool dfc = false)
    {
        return new LinkControl103(false, (byte)function, acd, dfc);
    }

    public override string ToString()
    {
        var fcbOrAcd = IsPrimary ? $"FCB={(FcbOrAcd ? 1 : 0)} FCV={(FcvOrDfc ? 1 : 0)}" : $"ACD={(FcbOrAcd ? 1 : 0)} DFC={(FcvOrDfc ? 1 : 0)}";
        return $"PRM={(IsPrimary ? 1 : 0)} {fcbOrAcd} FC={Function}";
    }
}
