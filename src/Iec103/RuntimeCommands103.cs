using System.Globalization;
using System.Text;

namespace Iec103;

/// <summary>103 运行时命令执行结果；成功时携带目标站点与模型供调用方触发上送。</summary>
public sealed record RuntimeCommandResult103(
    bool Ok,
    string Message,
    Iec103StationModel? Station = null,
    Iec103PointModel? Point = null);

/// <summary>
/// serve103 运行时控制台命令：纯模型操作（不含 IO），改 workspace 内存状态，
/// 由调用方调用 <see cref="Iec103Server"/> 的上送 API 产生协议行为。
/// </summary>
public static class RuntimeCommands103
{
    /// <summary>全部站点概览：监视点 / 通用分类 / 测量 / 扰动。</summary>
    public static string DescribeStations(Workspace103Model workspace)
    {
        var builder = new StringBuilder();
        foreach (var station in workspace.Stations)
        {
            // Disturbances 快照（锁内），避免与会话线程的枚举竞争
            List<DisturbanceRecord103Model> disturbances;
            lock (station.DisturbanceGate)
            {
                disturbances = [.. station.Disturbances];
            }

            builder.AppendLine($"站点 {station.Address} \"{station.Name}\"：{station.Points.Count} 监视点，" +
                $"{station.GenericEntries.Count} 通用分类条目，{station.Measurements.Count} 测量点，{disturbances.Count} 条扰动");
            foreach (var point in station.Points)
            {
                builder.AppendLine($"  点 FUN={point.Fun} INF={point.Inf} DPI={point.Dpi}（{DpiText(point.Dpi)}） {point.Description}");
            }

            foreach (var entry in station.GenericEntries)
            {
                var value = entry.DataType == "ascii" ? entry.AsciiValue : entry.ActualValue?.ToString();
                builder.AppendLine($"  通用 GIN={entry.Group}.{entry.Entry} [{entry.DataType}] = {value}" +
                    (entry.ReadOnly ? "（只读）" : string.Empty) + " " + entry.Description);
            }

            foreach (var measurement in station.Measurements)
            {
                builder.AppendLine($"  测量 FUN={measurement.Fun} INF={measurement.Inf} = {measurement.Value} {measurement.Description}");
            }

            foreach (var disturbance in disturbances)
            {
                builder.AppendLine($"  扰动 FAN={disturbance.Fan} {disturbance.Channels.Count} 通道 × {disturbance.TotalSamples} 点 " +
                    $"@{disturbance.SampleIntervalUs}µs 起始 {disturbance.FaultTime:HH:mm:ss.fff}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>置监视点 DPI（0-3）。返回站点与点，调用方按需变位上送。</summary>
    public static RuntimeCommandResult103 TrySetPoint(Workspace103Model workspace, int address, int fun, int inf, int dpi)
    {
        if (dpi is < 0 or > 3)
        {
            return new RuntimeCommandResult103(false, "DPI 须在 0-3（0/3 不确定，1 分，2 合）");
        }

        var found = FindPoint(workspace, address, fun, inf);
        if (found is null)
        {
            return new RuntimeCommandResult103(false, $"FUN={fun} INF={inf} 监视点不存在");
        }

        found.Value.Point.Dpi = dpi;
        return new RuntimeCommandResult103(true, $"已置 FUN={fun} INF={inf} → {DpiText(dpi)}，可上送", found.Value.Station, found.Value.Point);
    }

    /// <summary>改通用分类条目实际值（按条目类型解析，ascii/uint/int/float）。下次读生效。</summary>
    public static RuntimeCommandResult103 TrySetGeneric(Workspace103Model workspace, int address, int group, int entry, string rawValue)
    {
        var station = FindStation(workspace, address);
        var target = station?.GenericEntries.FirstOrDefault(e => e.Group == group && e.Entry == entry);
        if (station is null || target is null)
        {
            return new RuntimeCommandResult103(false, $"GIN={group}.{entry} 不存在");
        }

        if (target.ReadOnly)
        {
            return new RuntimeCommandResult103(false, $"GIN={group}.{entry} 为只读条目");
        }

        switch (target.DataType)
        {
            case "ascii":
                target.AsciiValue = rawValue;
                break;
            case "uint" when long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned) && unsigned >= 0:
                target.ActualValue = unsigned;
                break;
            case "int" when long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed):
                target.ActualValue = signed;
                break;
            case "float" when double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating):
                target.ActualValue = floating;
                break;
            default:
                return new RuntimeCommandResult103(false, $"值 \"{rawValue}\" 与类型 {target.DataType} 不符");
        }

        return new RuntimeCommandResult103(true, $"已置 GIN={group}.{entry}（{target.DataType}）= {rawValue}，下次读生效", station);
    }

    /// <summary>改循环测量值（double，编码时截断为 int16）。下次 2 级召唤生效。</summary>
    public static RuntimeCommandResult103 TrySetMeasurement(Workspace103Model workspace, int address, int fun, int inf, string rawValue)
    {
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return new RuntimeCommandResult103(false, $"值 \"{rawValue}\" 不是数字");
        }

        var station = FindStation(workspace, address);
        var target = station?.Measurements.FirstOrDefault(m => m.Fun == fun && m.Inf == inf);
        if (station is null || target is null)
        {
            return new RuntimeCommandResult103(false, $"FUN={fun} INF={inf} 测量点不存在");
        }

        target.Value = value;
        return new RuntimeCommandResult103(true, $"已置测量 FUN={fun} INF={inf} = {value}，下次 2 级召唤生效", station);
    }

    /// <summary>
    /// 注入一条扰动记录（FAN 取现有最大 +1）：三相电流 + 中性点，
    /// 240 点 @1ms，故障自第 60 点起（正弦 0.3 → 故障段 0.9/0.85/0.1）。
    /// </summary>
    public static RuntimeCommandResult103 TryInjectDisturbance(Workspace103Model workspace, int address)
    {
        var station = FindStation(workspace, address);
        if (station is null)
        {
            return new RuntimeCommandResult103(false, $"站点 {address} 不存在");
        }

        // 与会话线程（扰动表/选择故障服务）共用 DisturbanceGate，避免枚举中修改
        int fan;
        lock (station.DisturbanceGate)
        {
            fan = station.Disturbances.Count > 0 ? station.Disturbances.Max(d => d.Fan) + 1 : 1;
            if (fan > 65535)
            {
                return new RuntimeCommandResult103(false, "故障序号 FAN 已达 65535 上限");
            }

            station.Disturbances.Add(new DisturbanceRecord103Model
            {
                Fan = fan,
                FaultTime = DateTime.Now,
                SampleIntervalUs = 1000,
                Channels =
                [
                    new DisturbanceChannel103Model { Acc = 1, Primary = 600, Secondary = 5, Factor = 120, SampleCount = 240, Amplitude = 0.3, FaultStartIndex = 60, FaultAmplitude = 0.9 },
                    new DisturbanceChannel103Model { Acc = 2, Primary = 600, Secondary = 5, Factor = 120, SampleCount = 240, Amplitude = 0.3, FaultStartIndex = 60, FaultAmplitude = 0.85 },
                    new DisturbanceChannel103Model { Acc = 3, Primary = 600, Secondary = 5, Factor = 120, SampleCount = 240, Amplitude = 0.3, FaultStartIndex = 60, FaultAmplitude = 0.88 },
                    new DisturbanceChannel103Model { Acc = 4, Primary = 100, Secondary = 5, Factor = 20, SampleCount = 240, Amplitude = 0.1, FaultStartIndex = 0, FaultAmplitude = 0.1 },
                ],
            });
        }

        return new RuntimeCommandResult103(true, $"已注入扰动 FAN={fan}（4 通道 × 240 点），主站可 TOO=24 获取扰动表", station);
    }

    public static (Iec103StationModel Station, Iec103PointModel Point)? FindPoint(Workspace103Model workspace, int address, int fun, int inf)
    {
        var station = FindStation(workspace, address);
        var point = station?.Points.FirstOrDefault(p => p.Fun == fun && p.Inf == inf);
        return station is null || point is null ? null : (station, point);
    }

    private static Iec103StationModel? FindStation(Workspace103Model workspace, int address)
    {
        return workspace.Stations.FirstOrDefault(s => s.Address == address);
    }

    private static string DpiText(int dpi) => dpi switch
    {
        1 => "分（OFF）",
        2 => "合（ON）",
        _ => "不确定",
    };
}
