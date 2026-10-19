namespace SmartFan.App.Models;

/// <summary>VID is a requested voltage and must not be presented or uploaded as measured VCore.</summary>
public enum CpuVoltageKind { Unknown, VCore, Vid }

/// <summary>可空值代表传感器不可用；零值只代表真实读取到的零。</summary>
public sealed class TelemetrySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool IsDemo { get; init; }
    public string Status { get; init; } = "尚未采样";
    public string CpuName { get; init; } = "CPU";
    public string GpuName { get; init; } = "GPU";
    public double? CpuTemperature { get; init; }
    public TemperatureReadStatus CpuTemperatureStatus { get; init; } = new();
    public HardwareMonitorDiagnostics? HardwareMonitor { get; init; }
    public string VendorSensorStatus { get; init; } = "";
    public double? GpuTemperature { get; init; }
    public double? CpuUsage { get; init; }
    public double? GpuUsage { get; init; }
    public double? CpuVoltage { get; init; }
    public CpuVoltageKind CpuVoltageKind { get; init; }
    public double? CpuClockMhz { get; init; }
    public string CpuVoltageReadStatus { get; init; } = "";
    public string CpuClockReadStatus { get; init; } = "";
    public double? GpuClockMhz { get; init; }
    public double? RamUsedMiB { get; init; }
    public double? RamTotalMiB { get; init; }
    public double? GpuRamUsedMiB { get; init; }
    public double? GpuRamTotalMiB { get; init; }
    public double[] FanPresent { get; init; } = new double[5];
    public double?[] FanRpm { get; init; } = new double?[5];
    public bool FanSensorsAvailable { get; init; }
    public bool FanPresenceAvailable { get; init; }
    public IReadOnlyDictionary<string, string> SensorNames { get; init; } = new Dictionary<string, string>();
}

public sealed class SensorDescriptor
{
    public string? ReadStatus { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public string HardwareType { get; init; } = "";
    public string SensorType { get; init; } = "";
    public double? Value { get; init; }
    /// <summary>Only explicit vendor mappings define a stable laptop fan slot; ordinary LHM fans use identifier order.</summary>
    public int? FanSlot { get; init; }
    public override string ToString() => $"{HardwareName} / {Name}";
}

public sealed class SettingsLoadResult
{
    public required AppSettings Settings { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
