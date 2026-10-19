using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Reject Intel MSR readings when their required PawnIO endpoint is unavailable.
/// Load counters, GPU APIs and independent ASUS EC readings retain their own availability.</summary>
public static class SensorReadPolicy
{
    public static (double? Value, string? Status) Normalize(string hardwareId, string sensorType, double? value, HardwareMonitorDiagnostics? environment)
    {
        bool requiresDriver = hardwareId.StartsWith("/intelcpu/", StringComparison.Ordinal) && sensorType is "Temperature" or "Clock" or "Power" or "Voltage";
        if (requiresDriver && environment is { PawnIoDeviceAccessible: false })
            return (null, !environment.PawnIoInstalled ? "未安装 PawnIO；此项需要 CPU 硬件访问驱动" : "PawnIO 不可访问；请检查驱动及后台权限");
        return value is { } number && !double.IsFinite(number) ? (null, "采集库返回非有限值") : (value, null);
    }
}
