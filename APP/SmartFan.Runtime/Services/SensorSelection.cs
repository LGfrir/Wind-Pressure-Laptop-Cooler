using System.Text.RegularExpressions;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>名称白名单与真实硬件采样解耦，防止把热点/显存温度误作 CPU/GPU 核心温度。</summary>
public static class SensorSelection
{
    private static readonly Regex CoreTemperatureName = new(@"^(?:CPU Core|(?:(?:CPU *)?Core|P-Core|E-Core) *# *[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CcdTemperatureName = new(@"^CCD[0-9]+ \(Tdie\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static SensorDescriptor? SelectTemperature(IEnumerable<SensorDescriptor> sensors, bool cpu, string? preferredId = null)
    {
        var candidates = sensors.Where(s => IsAbsoluteTemperature(s) && s.Value.HasValue && double.IsFinite(s.Value.Value)).ToList();
        if (!string.IsNullOrWhiteSpace(preferredId)) return candidates.FirstOrDefault(s => s.Id == preferredId);
        var ranked = candidates.Select(s => new { Sensor = s, Rank = TemperatureRank(s.Name, cpu) })
            .Where(s => s.Rank < int.MaxValue).OrderBy(s => s.Rank).ThenByDescending(s => s.Sensor.Value).ToList();
        return ranked.Count == 0 ? null : ranked[0].Sensor;
    }

    public static int TemperatureRank(string name, bool cpu)
    {
        var normalized = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (normalized.Contains("distance to") || normalized.Contains("tjmax")) return int.MaxValue;
        if (cpu)
        {
            if (normalized is "cpu package" or "package") return 0;
            // LHM Amd17Cpu distinguishes the package/die sensor from CCD aggregates and Tctl.
            // Exact names keep a hotter CCD or an unrelated rail from replacing the package reading.
            if (normalized is "core (tdie)" or "core (tctl/tdie)") return 1;
            if (normalized is "core max" or "cpu core max") return 2;
            if (normalized == "core (tctl)") return 3;
            if (normalized == "cpu cores") return 4; // Amd10Cpu's legacy aggregate temperature.
            if (CoreTemperatureName.IsMatch(normalized)) return 5;
            if (normalized == "ccds max (tdie)") return 6;
            if (CcdTemperatureName.IsMatch(normalized)) return 7;
            if (normalized == "ccds average (tdie)") return 8;
            if (normalized == "asus ec cpu temperature") return 9;
        }
        else if (normalized is "gpu core" or "gpu temperature" or "core" or "gpu") return 0;
        return int.MaxValue;
    }

    public static bool IsAbsoluteTemperature(SensorDescriptor sensor) => sensor.SensorType == "Temperature" &&
        !sensor.Name.Contains("distance to", StringComparison.OrdinalIgnoreCase) &&
        !sensor.Name.Contains("tjmax", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<SensorDescriptor?> SelectFanSlots(IEnumerable<SensorDescriptor> sensors)
    {
        var fans = sensors.Where(s => s.SensorType == "Fan").ToList();
        var explicitSlots = fans.Where(s => s.FanSlot is >= 0 and < 5).ToList();
        if (explicitSlots.Count > 0)
            return Enumerable.Range(0, 5).Select(slot => explicitSlots.FirstOrDefault(s => s.FanSlot == slot)).ToArray();
        var ordered = fans.OrderBy(s => s.Id, StringComparer.Ordinal).Take(5).ToList();
        return Enumerable.Range(0, 5).Select(slot => ordered.ElementAtOrDefault(slot)).ToArray();
    }

    public static bool HasReliableFanPresence(IReadOnlyList<SensorDescriptor?> slots) =>
        slots.Any(s => s?.FanSlot.HasValue == true)
            // Only the three fixed ASUS endpoints currently provide explicit slot mappings.
            ? slots.Count >= 3 && Enumerable.Range(0, 3).All(slot => slots[slot]?.FanSlot == slot)
            : slots.Any(s => s is not null);

    public static string? SelectGpuId(IEnumerable<SensorDescriptor> sensors, string? preferredId)
    {
        var gpus = sensors.Where(s => s.HardwareType.Equals("GpuNvidia", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(preferredId)) return gpus.Any(g => g.HardwareId == preferredId) ? preferredId : null;
        return gpus.OrderBy(g => g.HardwareId, StringComparer.Ordinal).FirstOrDefault()?.HardwareId;
    }
}
