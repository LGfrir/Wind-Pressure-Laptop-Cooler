using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>供编辑器验证及预览使用。实际温控和失联保护都由 ESP32 实施。</summary>
public static class CurveService
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<FanCurvePoint>? points)
    {
        var errors = new List<string>();
        if (points is null || points.Count < 2 || points.Count > 32)
        {
            errors.Add("温度曲线需要 2～32 个控制点。");
            return errors;
        }
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point is null || !double.IsFinite(point.TemperatureCelsius) ||
                point.TemperatureCelsius < -50 || point.TemperatureCelsius > 150)
                errors.Add($"控制点 {i + 1} 的温度必须在 -50～150 °C 内。");
            if (point is null || !double.IsFinite(point.TargetRpm) || point.TargetRpm < 0 || point.TargetRpm > uint.MaxValue)
                errors.Add($"控制点 {i + 1} 的目标转速必须是有效的非负 UInt32 数值。");
            if (i > 0 && point is not null && points[i - 1] is { } previous &&
                point.TemperatureCelsius <= previous.TemperatureCelsius)
                errors.Add("控制点温度必须严格递增，不能重复。");
        }
        return errors;
    }

    public static double EvaluateRpm(IReadOnlyList<FanCurvePoint> points, double temperature)
    {
        var errors = Validate(points);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(points));
        if (!double.IsFinite(temperature)) throw new ArgumentOutOfRangeException(nameof(temperature));
        if (temperature <= points[0].TemperatureCelsius) return points[0].TargetRpm;
        for (int i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].TemperatureCelsius) continue;
            var lower = points[i - 1];
            var upper = points[i];
            var factor = (temperature - lower.TemperatureCelsius) /
                         (upper.TemperatureCelsius - lower.TemperatureCelsius);
            return lower.TargetRpm + factor * (upper.TargetRpm - lower.TargetRpm);
        }
        return points[^1].TargetRpm;
    }

    public static double? SelectTemperature(TelemetrySnapshot snapshot, TemperatureSource source)
    {
        if (snapshot.IsDemo) return null; // 演示数据永远不作为真实温控输入。
        return source switch
        {
            TemperatureSource.Cpu => snapshot.CpuTemperature,
            TemperatureSource.Gpu => snapshot.GpuTemperature,
            _ when snapshot.CpuTemperature.HasValue && snapshot.GpuTemperature.HasValue =>
                Math.Max(snapshot.CpuTemperature.Value, snapshot.GpuTemperature.Value),
            _ => snapshot.CpuTemperature ?? snapshot.GpuTemperature
        };
    }
}
