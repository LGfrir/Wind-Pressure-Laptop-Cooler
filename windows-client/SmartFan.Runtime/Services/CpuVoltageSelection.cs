using System.Text.RegularExpressions;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Selects a positive CPU voltage while retaining the distinction between VCore and requested VID.</summary>
public static class CpuVoltageSelection
{
    public sealed record SelectedReading(SensorDescriptor Sensor, CpuVoltageKind Kind)
    {
        public bool IsVid => Kind == CpuVoltageKind.Vid;
    }

    private static readonly string[] VCoreNames = ["CPU Core", "CPU VCore", "VCore"];
    private static readonly HashSet<string> ExplicitVidNames = new(["CPU VID", "CPU Core VID", "Core VID", "VID"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex CpuHardware = new(@"^/(?:intelcpu|amdcpu)/[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CoreVidName = new(@"^(?:(?:CPU *)?Core|P-Core|E-Core) *# *[0-9]+(?: +(?:VID|\(VID\)))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static SelectedReading? Select(IReadOnlyList<SensorDescriptor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var candidates = sensors.Where(sensor => sensor.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
                && sensor.SensorType.Equals("Voltage", StringComparison.OrdinalIgnoreCase)
                && CpuHardware.IsMatch(sensor.HardwareId)
                && sensor.Value is { } value && double.IsFinite(value) && value > 0)
            .Select(sensor => new { Sensor = sensor, Name = NormalizeName(sensor.Name) })
            .Select(item =>
            {
                // Intel's generic "CPU Core" uses the same IA32_PERF_STATUS VID bits as its per-core entries.
                var intelCoreVid = item.Sensor.HardwareId.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase);
                return new { item.Sensor, VCoreRank = intelCoreVid ? -1 : Array.FindIndex(VCoreNames, name => name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)),
                    IsVid = intelCoreVid || ExplicitVidNames.Contains(item.Name) || CoreVidName.IsMatch(item.Name) };
            })
            .Where(item => item.VCoreRank >= 0 || item.IsVid)
            .GroupBy(item => item.Sensor.HardwareId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        // Keep one CPU package coherent. The current client has no multi-package target selector.
        if (candidates is null) return null;
        var vcore = candidates.Where(item => item.VCoreRank >= 0).OrderBy(item => item.VCoreRank)
            .ThenBy(item => item.Sensor.Id, StringComparer.Ordinal).FirstOrDefault();
        if (vcore is not null) return new(vcore.Sensor, CpuVoltageKind.VCore);

        // LHM v0.9.6 IntelCpu names _coreVIDs after the core (P-Core/E-Core/Core), without a VID suffix.
        // https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs
        var vid = candidates.Where(item => item.IsVid).OrderByDescending(item => item.Sensor.Value!.Value)
            .ThenBy(item => item.Sensor.Id, StringComparer.Ordinal).FirstOrDefault();
        return vid is null ? null : new(vid.Sensor, CpuVoltageKind.Vid);
    }

    private static string NormalizeName(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
