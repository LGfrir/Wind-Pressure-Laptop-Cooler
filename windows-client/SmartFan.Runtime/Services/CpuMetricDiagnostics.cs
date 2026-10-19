using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Explain the actual acquisition boundary, without treating a missing sensor as a zero reading.</summary>
public static class CpuMetricDiagnostics
{
    public static string Describe(string metric, double? value, int sensorCount, HardwareMonitorDiagnostics? environment)
    {
        if (value is { } number && double.IsFinite(number) && number >= 0) return $"CPU {metric}采集正常";
        if (environment is { PawnIoDeviceAccessible: false })
            return !environment.PawnIoInstalled ? $"未安装 PawnIO；CPU {metric}需要硬件访问驱动。安装官方签名版后重启后台并重新枚举。"
                : environment.PawnIoDeviceErrorCode == 5 ? $"PawnIO 拒绝访问；请检查采样后台的管理员权限，单独提升前端无效。"
                : $"PawnIO 驱动设备不可访问（Windows 错误 {environment.PawnIoDeviceErrorCode?.ToString() ?? "未知"}）；请检查驱动运行状态后重启后台。";
        return sensorCount == 0 ? $"当前采集库没有为这颗 CPU 提供{metric}传感器；驱动可用也不代表此型号支持该读数。"
            : $"已发现 {sensorCount} 个 CPU {metric}项，但没有可用的匹配读数；需要继续核对采集库对当前 CPU 的支持。";
    }
}
