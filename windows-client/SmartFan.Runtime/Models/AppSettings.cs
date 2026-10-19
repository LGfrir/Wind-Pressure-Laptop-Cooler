using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartFan.App.Models;

public enum TemperatureSource { Highest, Cpu, Gpu }
public enum RamWireUnit { Unspecified, Bytes, MiB, GiB }

/// <summary>本地设置与设备协议分开保存；刷新周期属于会话参数，不在场景中持久化。</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AppearanceSettings Appearance { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public TelemetrySettings Telemetry { get; set; } = new();
    public WindowPlacementSettings Window { get; set; } = new();
    public DashboardLayoutSettings Dashboard { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public string ActiveProfileId { get; set; } = "balanced";
    public List<DeviceProfile> Profiles { get; set; } = DeviceProfile.CreateDefaults();
}

public sealed class AppearanceSettings
{
    public string ThemeName { get; set; } = "Nebula";
    public bool FollowSystemTheme { get; set; }
    public string AccentColor { get; set; } = "#8B7CFF";
    public string BackgroundMode { get; set; } = "Layered";
    public string? WallpaperPath { get; set; }
    public double WallpaperOpacity { get; set; } = 0.30;
    public double PanelOpacity { get; set; } = 0.92;
    public double WallpaperCropX { get; set; }
    public double WallpaperCropY { get; set; }
    public double WallpaperCropWidth { get; set; } = 1;
    public double WallpaperCropHeight { get; set; } = 1;
    public string UiFontFamily { get; set; } = "Microsoft YaHei UI";
    public double UiScale { get; set; } = 1;
    public string TemperatureUnit { get; set; } = "C";
    public string MemoryUnit { get; set; } = "GiB";
}

public sealed class WindowPlacementSettings
{
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 930;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
}

public sealed class ConnectionSettings
{
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectInitialSeconds { get; set; } = 2;
    public bool FilterFanService { get; set; } = true;
    public bool AutoUploadConfiguration { get; set; }
    public string? DefaultDeviceId { get; set; }
    public List<RecentDevice> RecentDevices { get; set; } = [];
    public string LogLevel { get; set; } = "Information";
}

public sealed class RecentDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "散热底座";
    public string Address { get; set; } = "";
    public DateTimeOffset LastConnected { get; set; }
}

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; }
    public int IntervalMs { get; set; } = 1000;
    // RAM 单位尚未由作者确认；默认禁止把本地 MiB 直接猜成固件单位。
    public RamWireUnit RamWireUnit { get; set; } = RamWireUnit.Unspecified;
    public SensorPreferences SensorPreferences { get; set; } = new();
    public Dictionary<string, bool> EnabledItems { get; set; } = new()
    {
        ["laptop_cpu_usage"] = true, ["laptop_cpu_temp"] = true,
        ["laptop_cpu_volta"] = true, ["laptop_cpu_clock"] = true,
        ["laptop_gpu_usage"] = true, ["laptop_gpu_temp"] = true,
        ["laptop_gpu_clock"] = true, ["laptop_gpu_ram"] = true,
        ["laptop_gpu_tot_r"] = true, ["laptop_ram"] = true,
        ["laptop_tot_ram"] = true, ["laptop_fan_used"] = false,
        ["laptop_fan_speed"] = false
    };
}

public sealed class SensorPreferences
{
    public string? GpuHardwareId { get; set; }
    public string? CpuTemperatureSensorId { get; set; }
    public string? GpuTemperatureSensorId { get; set; }
    public int? ManualFanCount { get; set; }
}

public sealed class DeviceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新场景";
    public int FanMode { get; set; }
    public byte ManualPwm { get; set; } = 20;
    public uint TargetRpm { get; set; } = 3000;
    public int LightMode { get; set; } = 2;
    public byte Brightness { get; set; } = 128;
    public Dictionary<string, byte[]> LightingPayloads { get; set; } = new();
    public TemperatureSource TemperatureSource { get; set; } = TemperatureSource.Highest;
    public int LastTemperatureTimeoutSeconds { get; set; } = 10;
    public byte FallbackPwm { get; set; } = 20;
    // 曲线只用于本地编辑和导出：尚无固件上传特征，PC 不执行调速闭环。
    public List<FanCurvePoint> Curve { get; set; } = CreateCurve(1000, 2000, 3000, 4500);

    public static List<DeviceProfile> CreateDefaults() =>
    [
        new() { Id = "balanced", Name = "日常办公", Curve = CreateCurve(1000, 1800, 2800, 4200) },
        new() { Id = "gaming", Name = "游戏性能", FanMode = 1, LightMode = 7, Brightness = 200,
            Curve = CreateCurve(1600, 2500, 3600, 4800) },
        new() { Id = "silent", Name = "安静专注", LightMode = 3, Brightness = 64,
            Curve = CreateCurve(800, 1200, 2200, 3800) }
    ];

    private static List<FanCurvePoint> CreateCurve(double a, double b, double c, double d) =>
    [
        new() { TemperatureCelsius = 40, TargetRpm = a },
        new() { TemperatureCelsius = 55, TargetRpm = b },
        new() { TemperatureCelsius = 70, TargetRpm = c },
        new() { TemperatureCelsius = 85, TargetRpm = d }
    ];
}

public sealed class FanCurvePoint : ObservableObject
{
    private double _temperatureCelsius;
    private double _targetRpm;
    public double TemperatureCelsius
    {
        get => _temperatureCelsius;
        set => SetProperty(ref _temperatureCelsius, value);
    }
    public double TargetRpm
    {
        get => _targetRpm;
        set => SetProperty(ref _targetRpm, value);
    }
}
