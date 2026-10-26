using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iec103;

/// <summary>103 监视方向信息点：FUN/INF 两级寻址 + 当前 DPI 状态。</summary>
public sealed class Iec103PointModel
{
    /// <summary>功能类型。兼容范围 0-127（标准语义）、专用范围 160-255（厂家自定义）。</summary>
    public int Fun { get; set; }

    /// <summary>信息序号（与 FUN 联合定位一个信息点）。</summary>
    public int Inf { get; set; }

    /// <summary>当前双点状态：0 不确定、1 分、2 合。</summary>
    public int Dpi { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>通用分类目录条目：GIN + 描述 + 类型与当前值 + 量程 + 只读标志。</summary>
public sealed class GenericEntry103Model
{
    public int Group { get; set; }

    public int Entry { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>值类型：ascii | uint | int | float。</summary>
    public string DataType { get; set; } = "ascii";

    /// <summary>数值当前值（uint/int/float 类型）。</summary>
    public double? ActualValue { get; set; }

    /// <summary>ASCII 当前值（ascii 类型；描述条目通常为空串）。</summary>
    public string? AsciiValue { get; set; }

    /// <summary>整数宽度（字节，uint/int 类型用，1/2/4）。</summary>
    public int IntWidth { get; set; } = 4;

    /// <summary>量程最小值（提供时响应 KOD=3）。</summary>
    public double? MinValue { get; set; }

    /// <summary>量程最大值。</summary>
    public double? MaxValue { get; set; }

    /// <summary>量程步长。</summary>
    public double? StepValue { get; set; }

    /// <summary>只读条目：写命令回否定确认（COT=41）。</summary>
    public bool ReadOnly { get; set; }

    public Gin103 ToGin() => new((byte)Group, (byte)Entry);
}

/// <summary>循环测量点（2 级数据，TI=9）：FUN/INF 定位 + 工程值（有符号 int16 传输）。</summary>
public sealed class Iec103MeasurementModel
{
    public int Fun { get; set; }

    public int Inf { get; set; }

    /// <summary>工程值（编码为 int16，超出截断）。</summary>
    public double Value { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>103 从站（保护装置模拟）：链路地址 + 标识 + 监视方向信息点表 + 通用分类目录。</summary>
public sealed class Iec103StationModel
{
    /// <summary>链路地址（同时作为 ASDU 公共地址，1-247）。</summary>
    public int Address { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>兼容范围标识文本（8 字符，TI=5 INF=80）。</summary>
    public string CompatIdent { get; set; } = "TEST-103";

    /// <summary>专用范围标识文本（8 字符，TI=5 INF=81）。</summary>
    public string PrivateIdent { get; set; } = "TEST-DEV";

    public List<Iec103PointModel> Points { get; set; } = [];

    /// <summary>通用分类目录（响应 TI=21 读命令 / 接受 TI=10 写命令）。</summary>
    public List<GenericEntry103Model> GenericEntries { get; set; } = [];

    /// <summary>扰动记录（故障录波模拟，响应 ASDU 24 扰动传输服务）。</summary>
    public List<DisturbanceRecord103Model> Disturbances { get; set; } = [];

    /// <summary>跨线程访问扰动列表的同步门：控制台 fault 注入（控制台线程）
    /// 与扰动表/选择故障服务（会话线程）共享同一列表。</summary>
    [JsonIgnore]
    public object DisturbanceGate { get; } = new();

    /// <summary>循环测量点（响应 2 级召唤，TI=9）。</summary>
    public List<Iec103MeasurementModel> Measurements { get; set; } = [];
}

/// <summary>
/// 103 传输通道配置。103 原生设计为串口（RS-485/RS-232 总线），
/// TCP 为现场普遍的"网络化 103"（FT1.2 字节流不变跑 TCP）——两者帧层完全一致。
/// </summary>
public sealed class Transport103Model
{
    /// <summary>类型：tcp（默认，FT1.2 over TCP）| serial（本机串口，RS-485 多站按链路地址寻址）。</summary>
    public string Type { get; set; } = "tcp";

    /// <summary>串口名（serial 用）。</summary>
    public string PortName { get; set; } = "COM1";

    /// <summary>波特率（serial 用）。串口 103 常见 9600/19200。</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>校验位：none | odd | even（FT1.2 缺省偶校验）。</summary>
    public string Parity { get; set; } = "even";

    /// <summary>数据位（serial 用，5-8）。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位（serial 用，1/2）。</summary>
    public int StopBits { get; set; } = 1;
}

/// <summary>103 工作区：一个或多个模拟从站（每 TCP 连接按地址寻址一个站）。</summary>
public sealed class Workspace103Model
{
    /// <summary>TCP 监听端口（GUI 页签运行用；serve103 CLI 可覆盖）。</summary>
    public int Port { get; set; } = 2405;

    /// <summary>监听绑定地址（默认全部网卡，局域网网关如 GPGate 可接入；可改回 127.0.0.1 仅回环）。</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>传输通道（tcp 用 Port/BindAddress；serial 用串口参数）。</summary>
    public Transport103Model Transport { get; set; } = new();

    public List<Iec103StationModel> Stations { get; set; } = [];
}

/// <summary>103 工作区 JSON 读写（iec103 工作区独立于 104 workspace 格式）。</summary>
public static class WorkspaceJson103
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Workspace103Model Load(string path)
    {
        var json = File.ReadAllText(path);
        var workspace = JsonSerializer.Deserialize<Workspace103Model>(json, Options)
            ?? throw new InvalidOperationException($"103 工作区文件反序列化为空: {path}");

        Normalize(workspace);
        foreach (var station in workspace.Stations)
        {
            ValidateStation(station);
        }

        return workspace;
    }

    /// <summary>从流加载（内存克隆/导入路径，语义同 <see cref="Load(string)"/>）。</summary>
    public static Workspace103Model Load(Stream stream)
    {
        var workspace = JsonSerializer.Deserialize<Workspace103Model>(stream, Options)
            ?? throw new InvalidOperationException("103 工作区反序列化为空");

        Normalize(workspace);
        foreach (var station in workspace.Stations)
        {
            ValidateStation(station);
        }

        return workspace;
    }

    /// <summary>手工编辑的 JSON 可能显式写 null/缺省节点：归一化为缺省实例。</summary>
    private static void Normalize(Workspace103Model workspace)
    {
        workspace.Transport ??= new Transport103Model();
    }

    public static string Save(Workspace103Model workspace)
    {
        foreach (var station in workspace.Stations)
        {
            ValidateStation(station);
        }

        return JsonSerializer.Serialize(workspace, Options);
    }

    private static void ValidateStation(Iec103StationModel station)
    {
        if (station.Address is < 1 or > 247)
        {
            throw new InvalidOperationException($"站点链路地址须在 1-247：{station.Address}");
        }

        foreach (var point in station.Points)
        {
            if (point.Fun is < 0 or > 255)
            {
                throw new InvalidOperationException($"FUN 须在 0-255：{point.Fun}");
            }

            if (point.Inf is < 0 or > 255)
            {
                throw new InvalidOperationException($"INF 须在 0-255：{point.Inf}");
            }
        }

        var gins = new HashSet<(int, int)>();
        foreach (var entry in station.GenericEntries)
        {
            if (entry.Group is < 0 or > 255 || entry.Entry is < 0 or > 255)
            {
                throw new InvalidOperationException($"通用分类 GIN 组/条目须在 0-255：{entry.Group}.{entry.Entry}");
            }

            if (!gins.Add((entry.Group, entry.Entry)))
            {
                throw new InvalidOperationException($"通用分类 GIN 重复：{entry.Group}.{entry.Entry}");
            }

            if (entry.DataType is not ("ascii" or "uint" or "int" or "float"))
            {
                throw new InvalidOperationException($"通用分类类型须为 ascii/uint/int/float：{entry.DataType}");
            }

            // 描述/ASCII 值经 GB18030 编码后须能装进单帧单数据集预算（239 字节）
            if (Gb18030ByteCount(entry.Description) > GenericDataSet103.MaxAsciiBytes ||
                (entry.DataType == "ascii" && entry.AsciiValue is { } ascii && Gb18030ByteCount(ascii) > GenericDataSet103.MaxAsciiBytes))
            {
                throw new InvalidOperationException(
                    $"通用分类 GIN={entry.Group}.{entry.Entry} 描述/ASCII 值 GB18030 编码超过 {GenericDataSet103.MaxAsciiBytes} 字节上限");
            }
        }

        var fans = new HashSet<int>();
        foreach (var disturbance in station.Disturbances)
        {
            if (disturbance.Fan is < 1 or > 65535)
            {
                throw new InvalidOperationException($"故障序号 FAN 须在 1-65535：{disturbance.Fan}");
            }

            if (!fans.Add(disturbance.Fan))
            {
                throw new InvalidOperationException($"扰动记录 FAN 重复：{disturbance.Fan}");
            }

            if (disturbance.Channels.Count == 0 || disturbance.Channels.Any(c => c.SampleCount < 1))
            {
                throw new InvalidOperationException($"扰动记录 FAN={disturbance.Fan} 需要至少一个通道且采样数 ≥1");
            }

            // 采样数经 16 位 NOE 传输，上界 65535；超出会在传输就绪帧回绕
            if (disturbance.Channels.Any(c => c.SampleCount > ushort.MaxValue))
            {
                throw new InvalidOperationException($"扰动记录 FAN={disturbance.Fan} 采样数超过 16 位上限 65535");
            }

            if (disturbance.Channels.Any(c => c.SampleCount != disturbance.Channels[0].SampleCount))
            {
                throw new InvalidOperationException($"扰动记录 FAN={disturbance.Fan} 各通道采样数须一致（NOE 单值）");
            }
        }
    }

    /// <summary>GB18030 编码字节数（与 Generic103 发送侧一致的编码器）。</summary>
    private static int Gb18030ByteCount(string text)
    {
        return GenericDataSet103.EncodeGb18030(text).Length;
    }
}
