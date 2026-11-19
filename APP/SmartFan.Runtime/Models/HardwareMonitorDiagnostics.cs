namespace SmartFan.App.Models;

public enum TemperatureReadCode
{
    NotSampled, Available, DriverMissing, DriverUnavailable, PermissionRequired,
    InitializationFailed, NoCpuSensors, SelectedSensorUnavailable, NoSupportedSensor, SensorReadFailed
}

/// <summary>读数和失败原因分别传递；失败不允许用零或主板热区温度代替。</summary>
public sealed class TemperatureReadStatus
{
    public TemperatureReadCode Code { get; init; } = TemperatureReadCode.NotSampled;
    public string Message { get; init; } = "CPU 温度尚未采样";
    public string? SensorId { get; init; }
}

/// <summary>后台采样进程的真实权限及驱动访问预检，不代表前端窗口的权限。</summary>
public sealed class HardwareMonitorDiagnostics
{
    public string ProviderVersion { get; init; } = "";
    public bool IsAdministrator { get; init; }
    public bool PawnIoInstalled { get; init; }
    public string? PawnIoVersion { get; init; }
    public bool PawnIoDeviceAccessible { get; init; }
    public int? PawnIoDeviceErrorCode { get; init; }
}
