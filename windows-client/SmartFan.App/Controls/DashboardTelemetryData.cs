using System.Windows;

namespace SmartFan.App.Controls;

public sealed record LaptopFanReading(string Name, double? Rpm);

/// <summary>
/// Presentation data shared by the built-in dashboard widgets. Values are already in the displayed units;
/// collection, unit conversion and device control stay outside the widget layer.
/// </summary>
public sealed class DashboardTelemetryData : DependencyObject
{
    public static readonly DependencyProperty MetricContextProperty = DependencyProperty.Register(nameof(MetricContext), typeof(IReadOnlyDictionary<string, DashboardMetricContext>), typeof(DashboardTelemetryData), new PropertyMetadata(new Dictionary<string, DashboardMetricContext>()));
    public IReadOnlyDictionary<string, DashboardMetricContext> MetricContext { get => (IReadOnlyDictionary<string, DashboardMetricContext>)GetValue(MetricContextProperty); set => SetValue(MetricContextProperty, value); }
    public static readonly DependencyProperty FreshnessTextProperty = Text(nameof(FreshnessText), "等待硬件采样");
    public string FreshnessText { get => (string)GetValue(FreshnessTextProperty); set => SetValue(FreshnessTextProperty, value); }
    public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(nameof(IsStale), typeof(bool), typeof(DashboardTelemetryData), new PropertyMetadata(false));
    public bool IsStale { get => (bool)GetValue(IsStaleProperty); set => SetValue(IsStaleProperty, value); }
    public static readonly DependencyProperty CpuTemperatureProperty = Number(nameof(CpuTemperature));
    public static readonly DependencyProperty CpuUsageProperty = Number(nameof(CpuUsage));
    public static readonly DependencyProperty GpuTemperatureProperty = Number(nameof(GpuTemperature));
    public static readonly DependencyProperty GpuUsageProperty = Number(nameof(GpuUsage));
    public static readonly DependencyProperty MemoryUsedProperty = Number(nameof(MemoryUsed));
    public static readonly DependencyProperty MemoryTotalProperty = Number(nameof(MemoryTotal));
    public static readonly DependencyProperty FanRpmProperty = Number(nameof(FanRpm));
    public static readonly DependencyProperty CpuClockMhzProperty = Number(nameof(CpuClockMhz));
    public static readonly DependencyProperty CpuVoltageProperty = Number(nameof(CpuVoltage));
    public static readonly DependencyProperty GpuClockMhzProperty = Number(nameof(GpuClockMhz));
    public static readonly DependencyProperty GpuMemoryUsedProperty = Number(nameof(GpuMemoryUsed));
    public static readonly DependencyProperty GpuMemoryTotalProperty = Number(nameof(GpuMemoryTotal));
    public static readonly DependencyProperty CpuHardwareStatusProperty = Text(nameof(CpuHardwareStatus), "尚未采样");
    public static readonly DependencyProperty CpuClockStatusProperty = Text(nameof(CpuClockStatus), "尚未采样");
    public static readonly DependencyProperty CpuVoltageStatusProperty = Text(nameof(CpuVoltageStatus), "尚未采样");
    public static readonly DependencyProperty CpuVoltageLabelProperty = Text(nameof(CpuVoltageLabel), "核心电压");
    public static readonly DependencyProperty CpuVoltageCardTitleProperty = Text(nameof(CpuVoltageCardTitle), "CPU 核心电压");
    public string CpuVoltageLabel { get => (string)GetValue(CpuVoltageLabelProperty); set => SetValue(CpuVoltageLabelProperty, value); }
    public string CpuVoltageCardTitle { get => (string)GetValue(CpuVoltageCardTitleProperty); set => SetValue(CpuVoltageCardTitleProperty, value); }
    public string CpuClockStatus { get => (string)GetValue(CpuClockStatusProperty); set => SetValue(CpuClockStatusProperty, value); }
    public string CpuVoltageStatus { get => (string)GetValue(CpuVoltageStatusProperty); set => SetValue(CpuVoltageStatusProperty, value); }
    public string CpuHardwareStatus { get => (string)GetValue(CpuHardwareStatusProperty); set => SetValue(CpuHardwareStatusProperty, value); }
    public double? CpuClockMhz { get => (double?)GetValue(CpuClockMhzProperty); set => SetValue(CpuClockMhzProperty, value); }
    public double? CpuVoltage { get => (double?)GetValue(CpuVoltageProperty); set => SetValue(CpuVoltageProperty, value); }
    public double? GpuClockMhz { get => (double?)GetValue(GpuClockMhzProperty); set => SetValue(GpuClockMhzProperty, value); }
    public double? GpuMemoryUsed { get => (double?)GetValue(GpuMemoryUsedProperty); set => SetValue(GpuMemoryUsedProperty, value); }
    public double? GpuMemoryTotal { get => (double?)GetValue(GpuMemoryTotalProperty); set => SetValue(GpuMemoryTotalProperty, value); }
    public static readonly DependencyProperty TemperatureUnitProperty = Text(nameof(TemperatureUnit), "°C");
    public static readonly DependencyProperty TemperatureMaximumProperty = DependencyProperty.Register(nameof(TemperatureMaximum), typeof(double), typeof(DashboardTelemetryData), new PropertyMetadata(100d));
    public static readonly DependencyProperty MemoryUnitProperty = Text(nameof(MemoryUnit), "GiB");
    public static readonly DependencyProperty MemoryValueFormatProperty = Text(nameof(MemoryValueFormat), "0.0");
    public static readonly DependencyProperty CpuTemperatureStatusProperty = Text(nameof(CpuTemperatureStatus), "传感器不可用");
    public static readonly DependencyProperty GpuTemperatureStatusProperty = Text(nameof(GpuTemperatureStatus), "传感器不可用");
    public static readonly DependencyProperty LaptopFanSummaryProperty = Text(nameof(LaptopFanSummary), "电脑内置风扇尚未采样");
    public static readonly DependencyProperty LaptopFanDetailProperty = Text(nameof(LaptopFanDetail), "电脑内置风扇由本机传感器读取，与 ESP32 散热底座转速独立。");
    public static readonly DependencyProperty LaptopFansProperty = DependencyProperty.Register(nameof(LaptopFans), typeof(IReadOnlyList<LaptopFanReading>), typeof(DashboardTelemetryData), new PropertyMetadata(Array.Empty<LaptopFanReading>()));
    public IReadOnlyList<LaptopFanReading> LaptopFans { get => (IReadOnlyList<LaptopFanReading>)GetValue(LaptopFansProperty); set => SetValue(LaptopFansProperty, value); }

    public double? CpuTemperature { get => (double?)GetValue(CpuTemperatureProperty); set => SetValue(CpuTemperatureProperty, value); }
    public double? CpuUsage { get => (double?)GetValue(CpuUsageProperty); set => SetValue(CpuUsageProperty, value); }
    public double? GpuTemperature { get => (double?)GetValue(GpuTemperatureProperty); set => SetValue(GpuTemperatureProperty, value); }
    public double? GpuUsage { get => (double?)GetValue(GpuUsageProperty); set => SetValue(GpuUsageProperty, value); }
    public double? MemoryUsed { get => (double?)GetValue(MemoryUsedProperty); set => SetValue(MemoryUsedProperty, value); }
    public double? MemoryTotal { get => (double?)GetValue(MemoryTotalProperty); set => SetValue(MemoryTotalProperty, value); }
    public double? FanRpm { get => (double?)GetValue(FanRpmProperty); set => SetValue(FanRpmProperty, value); }
    public string TemperatureUnit { get => (string)GetValue(TemperatureUnitProperty); set => SetValue(TemperatureUnitProperty, value); }
    public double TemperatureMaximum { get => (double)GetValue(TemperatureMaximumProperty); set => SetValue(TemperatureMaximumProperty, value); }
    public string MemoryUnit { get => (string)GetValue(MemoryUnitProperty); set => SetValue(MemoryUnitProperty, value); }
    public string MemoryValueFormat { get => (string)GetValue(MemoryValueFormatProperty); set => SetValue(MemoryValueFormatProperty, value); }
    public string CpuTemperatureStatus { get => (string)GetValue(CpuTemperatureStatusProperty); set => SetValue(CpuTemperatureStatusProperty, value); }
    public string GpuTemperatureStatus { get => (string)GetValue(GpuTemperatureStatusProperty); set => SetValue(GpuTemperatureStatusProperty, value); }
    public string LaptopFanSummary { get => (string)GetValue(LaptopFanSummaryProperty); set => SetValue(LaptopFanSummaryProperty, value); }
    public string LaptopFanDetail { get => (string)GetValue(LaptopFanDetailProperty); set => SetValue(LaptopFanDetailProperty, value); }

    private static DependencyProperty Number(string name) => DependencyProperty.Register(name, typeof(double?), typeof(DashboardTelemetryData), new PropertyMetadata(null));
    private static DependencyProperty Text(string name, string value) => DependencyProperty.Register(name, typeof(string), typeof(DashboardTelemetryData), new PropertyMetadata(value));
}
