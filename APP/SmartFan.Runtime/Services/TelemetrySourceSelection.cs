using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Deterministic fallbacks per metric. A GPU's metrics always stay within the chosen physical adapter.</summary>
public static class TelemetrySourceSelection
{
    public static SensorDescriptor? CpuUsage(IEnumerable<SensorDescriptor> sensors) => Named(
        sensors.Where(s => s.HardwareType == "Cpu"), "Load", "CPU Total", "Total");

    public static SensorDescriptor? Named(IEnumerable<SensorDescriptor> sensors, string type, params string[] names)
    {
        var candidates = sensors.Where(s => s.SensorType == type && Valid(s.Value) &&
            (type != "Load" || s.Value <= 100)).OrderBy(SourceRank).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        // Source takes priority over aliases: a secondary channel is only used for an absent primary reading.
        foreach (var rank in candidates.Select(SourceRank).Distinct())
            foreach (var name in names)
                if (candidates.FirstOrDefault(s => SourceRank(s) == rank && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found)
                    return found;
        return null;
    }

    public static SensorDescriptor? GpuMetric(IEnumerable<SensorDescriptor> sensors, string? hardwareId, string type, params string[] names) =>
        hardwareId is null ? null : Named(sensors.Where(s => s.HardwareType == "GpuNvidia" && s.HardwareId == hardwareId), type, names);

    public static SensorDescriptor? GpuTemperature(IEnumerable<SensorDescriptor> sensors, string? hardwareId, string? preferredId)
    {
        var gpu = sensors.Where(s => hardwareId is not null && s.HardwareType == "GpuNvidia" && s.HardwareId == hardwareId).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredId)) return SensorSelection.SelectTemperature(gpu, false, preferredId);
        foreach (var group in gpu.GroupBy(SourceRank).OrderBy(group => group.Key))
            if (SensorSelection.SelectTemperature(group, false) is { } sensor) return sensor;
        return null;
    }

    public static (double? Value, SensorDescriptor? Sensor) MemoryMiB(IEnumerable<SensorDescriptor> sensors, params string[] names)
    {
        var candidates = sensors.Where(s => s.SensorType is "SmallData" or "Data").OrderBy(SourceRank).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        foreach (var rank in candidates.Select(SourceRank).Distinct())
            foreach (var name in names)
                if (candidates.FirstOrDefault(s => SourceRank(s) == rank && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Valid(s.Value)) is { } sensor)
                {
                    double value = sensor.Value!.Value * (sensor.SensorType == "Data" ? 1024 : 1);
                    if (double.IsFinite(value)) return (value, sensor);
                }
        return (null, null);
    }

    public sealed record PhysicalMemoryReading(double? Used, double? Total, string Source);
    public static PhysicalMemoryReading PhysicalMemory(double? nativeUsed, double? nativeTotal, IEnumerable<SensorDescriptor> sensors)
    {
        if (Valid(nativeUsed) && nativeTotal is > 0 && double.IsFinite(nativeTotal.Value) && nativeUsed <= nativeTotal)
            return new(nativeUsed, nativeTotal, "Windows GlobalMemoryStatusEx · 物理内存");
        // LHM GenericMemory exposes used and available in GiB. Never combine different memory devices.
        foreach (var group in sensors.Where(s => s.HardwareType == "Memory").GroupBy(s => s.HardwareId).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var used = MemoryMiB(group, "Memory Used");
            var available = MemoryMiB(group, "Memory Available");
            if (used.Value is { } u && available.Value is { } a && u + a > 0 && double.IsFinite(u + a))
                return new(u, u + a, $"{used.Sensor!.HardwareName} / Memory Used + Memory Available [{group.Key}]");
        }
        return new(null, null, "Windows 与硬件监控库均未提供有效物理内存读数");
    }

    private static bool Valid(double? value) => value is >= 0 && double.IsFinite(value.Value);
    // NVML returns a per-call status; LHM's NVAPI sensors can retain a previous Value on API failure.
    // Prefer fresh NVML metrics only after the provider has proved the physical adapter identity.
    private static int SourceRank(SensorDescriptor sensor) => sensor.Id.StartsWith("/windows/", StringComparison.Ordinal) ? 2
        : sensor.Id.StartsWith("/nvml/", StringComparison.Ordinal) ? 0 : 1;
}
