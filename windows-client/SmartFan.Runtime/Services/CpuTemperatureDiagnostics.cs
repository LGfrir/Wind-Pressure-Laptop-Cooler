using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>纯诊断规则，避免把驱动缺失误报成不支持 CPU 或建议反复更换传感器。</summary>
public static class CpuTemperatureDiagnostics
{
    public static TemperatureReadStatus Evaluate(IReadOnlyCollection<SensorDescriptor> cpuSensors,
        SensorDescriptor? selected, string? preferredId, HardwareMonitorDiagnostics environment,
        string? initializationError = null, string? samplingError = null)
    {
        TemperatureReadStatus Status(TemperatureReadCode code, string message) => new()
        { Code = code, Message = message, SensorId = selected?.Id ?? preferredId };
        if (selected is not null)
            return Status(TemperatureReadCode.Available, $"正在读取 {selected.HardwareName} / {selected.Name} [{selected.Id}]");
        if (initializationError is not null)
            return Status(TemperatureReadCode.InitializationFailed, initializationError);
        if (samplingError is not null)
            return Status(TemperatureReadCode.SensorReadFailed, samplingError);
        if (cpuSensors.Count == 0)
            return Status(TemperatureReadCode.NoCpuSensors, "未枚举到 CPU 传感器，请打开硬件传感器窗口检查列表，并保留后台日志。");
        if (!string.IsNullOrWhiteSpace(preferredId) && !cpuSensors.Any(s => s.Id == preferredId && SensorSelection.IsAbsoluteTemperature(s)))
            return Status(TemperatureReadCode.SelectedSensorUnavailable, "指定的 CPU 温度传感器不存在或不是绝对温度，请重新选择传感器或恢复自动选择。");
        if (!environment.PawnIoDeviceAccessible)
        {
            if (!environment.PawnIoInstalled && environment.PawnIoDeviceErrorCode is 2 or 3)
                return Status(TemperatureReadCode.DriverMissing, "未安装 PawnIO 硬件访问驱动，CPU 温度暂不可读；请从高级设置打开官方安装说明，安装后重启后台再验证。");
            if (environment.PawnIoDeviceErrorCode == 5 && !environment.IsAdministrator)
                return Status(TemperatureReadCode.PermissionRequired, "后台无权访问 PawnIO 驱动；需要以管理员身份重启后台，单独提升前端窗口无效。");
            return Status(TemperatureReadCode.DriverUnavailable, $"PawnIO 驱动无法访问（Windows 错误 {environment.PawnIoDeviceErrorCode?.ToString() ?? "未知"}）；请检查官方驱动安装和服务状态，然后重启后台。");
        }
        if (!string.IsNullOrWhiteSpace(preferredId))
            return Status(TemperatureReadCode.SelectedSensorUnavailable, "指定的 CPU 温度传感器已枚举，但当前未返回有效读数；可恢复自动选择，并在硬件传感器窗口查看诊断。");
        if (!cpuSensors.Any(s => SensorSelection.IsAbsoluteTemperature(s) && SensorSelection.TemperatureRank(s.Name, true) != int.MaxValue))
            return Status(TemperatureReadCode.NoSupportedSensor, "CPU 已识别，但未发现可用的封装或核心温度传感器；请在高级设置手动选择真实 CPU 温度传感器。");
        return Status(TemperatureReadCode.SensorReadFailed, "CPU 封装或核心传感器已枚举，但驱动未返回有效温度；请检查硬件传感器窗口和后台日志以排查兼容性，不能用主板热区代替。");
    }
}
