using System.Text.RegularExpressions;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Selects the highest available raw core clock, without substituting effective, bus or nominal clocks.</summary>
public static class CpuClockSelection
{
    private static readonly Regex CpuHardware = new(@"^/(?:intelcpu|amdcpu)/[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CoreClockName = new(@"^(?:CPU Core|(?:(?:CPU *)?Core|P-Core|E-Core) *# *[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static SensorDescriptor? Select(IReadOnlyList<SensorDescriptor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        // LHM IntelCpu/GenericCpu and AMD families expose these raw core names. Amd17Cpu
        // separately exposes "Core #n (Effective)" and "Cores (Average Effective)".
        // https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu
        var package = sensors.Where(IsCandidate)
            .Where(sensor => sensor.Value is { } value && double.IsFinite(value) && value > 0)
            .GroupBy(sensor => sensor.HardwareId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return package?.OrderByDescending(sensor => sensor.Value!.Value)
            .ThenBy(sensor => sensor.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Counts supported raw core sources, including currently unavailable readings, for diagnostics.</summary>
    public static int CountCandidates(IReadOnlyList<SensorDescriptor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        return sensors.Count(IsCandidate);
    }

    private static bool IsCandidate(SensorDescriptor sensor) =>
        sensor.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
        && sensor.SensorType.Equals("Clock", StringComparison.OrdinalIgnoreCase)
        && CpuHardware.IsMatch(sensor.HardwareId)
        && CoreClockName.IsMatch(string.Join(' ', sensor.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
}
