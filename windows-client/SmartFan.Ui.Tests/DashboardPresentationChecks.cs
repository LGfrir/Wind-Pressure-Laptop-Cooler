using System.Reflection;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

internal static class DashboardPresentationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var backend = new BackgroundClient($"SmartFan.Presentation.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, new SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartFan-UiTests", Guid.NewGuid().ToString("N")), false), new AppLog());
        try
        {
            vm.Settings.Appearance.TemperatureUnit = "F"; vm.Settings.Appearance.MemoryUnit = "MiB";
            vm.RefreshDashboard();
            check(vm.DashboardData.TemperatureUnit == "°F" && vm.DashboardData.MemoryUnit == "MiB" && vm.DashboardData.CpuTemperature is null,
                "selected units initialize before the first sample without creating fake values");
            var sample = new TelemetrySnapshot { CpuTemperature = 50, GpuTemperature = 60, CpuUsage = 27, GpuUsage = 34, RamUsedMiB = 8192, RamTotalMiB = 16384, CpuClockMhz = 4300, CpuVoltage = 1.15, GpuClockMhz = 1380, GpuRamUsedMiB = 4096, GpuRamTotalMiB = 12288 };
            Present(vm, sample);
            check(vm.DashboardData.CpuTemperature == 122 && vm.DashboardData.GpuTemperature == 140 && vm.DashboardData.TemperatureMaximum == 212,
                "sample adapter converts Celsius to Fahrenheit for all temperature widgets");
            check(vm.DashboardData.MemoryUsed == 8192 && vm.DashboardData.MemoryTotal == 16384 && vm.DashboardData.MemoryValueFormat == "0",
                "sample adapter preserves MiB values and precision");
            vm.Settings.Appearance.TemperatureUnit = "C"; vm.Settings.Appearance.MemoryUnit = "GiB"; vm.RefreshDashboard();
            check(vm.DashboardData.CpuTemperature == 50 && vm.DashboardData.MemoryUsed == 8 && vm.DashboardData.MemoryTotal == 16 && vm.DashboardData.MemoryUnit == "GiB",
                "changing display units reprojects the latest sample without a new hardware read");
            check(sample.CpuTemperature == 50 && sample.RamUsedMiB == 8192,
                "display conversion never changes the raw telemetry used for ESP packets");
            check(vm.DashboardData.CpuClockMhz == 4300 && vm.DashboardData.CpuVoltage == 1.15 && vm.DashboardData.GpuClockMhz == 1380 && vm.DashboardData.GpuMemoryUsed == 4 && vm.DashboardData.GpuMemoryTotal == 12,
                "extra widgets receive CPU frequency/voltage, GPU frequency and converted VRAM from the existing sampler");
            Present(vm, new TelemetrySnapshot
            {
                CpuTemperatureStatus = new TemperatureReadStatus { Code = TemperatureReadCode.DriverMissing, Message = "未安装 PawnIO 驱动" },
                HardwareMonitor = new HardwareMonitorDiagnostics { ProviderVersion = "0.9.6", PawnIoInstalled = false, IsAdministrator = false }
            });
            check(vm.DashboardData.CpuTemperature is null && vm.DashboardData.CpuTemperatureStatus == "未安装 PawnIO 驱动" && vm.HardwareMonitorDetail.Contains("未安装", StringComparison.Ordinal),
                "backend driver diagnosis reaches both the dashboard and advanced settings");
            check(vm.DashboardData.CpuClockMhz is null && vm.DashboardData.CpuVoltage is null && vm.DashboardData.CpuHardwareStatus.Contains("未安装 PawnIO"), "missing privileged CPU metrics retain their individual driver explanation");
            Present(vm, new TelemetrySnapshot());
            check(vm.CpuTemperatureStatusText.Contains("未提供", StringComparison.Ordinal) && vm.HardwareMonitorDetail.Contains("未提供", StringComparison.Ordinal),
                "older backend snapshots do not become false driver-missing conclusions");
            check(!backend.IsAttached, "presentation checks never attach the production background or ESP");
            Present(vm, new TelemetrySnapshot
            {
                CpuTemperature = 81,
                CpuTemperatureStatus = new TemperatureReadStatus { Code = TemperatureReadCode.Available, Message = "正在读取 ASUS EC CPU Temperature", SensorId = "/asus-ec/cpu/temperature" },
                VendorSensorStatus = "ASUS EC 只读采样正常",
                FanPresenceAvailable = true, FanPresent = [1, 1, 1, 0, 0], FanRpm = [5400, 4100, 6000, null, null],
                SensorNames = new Dictionary<string, string> { ["内置风扇 1"] = "ASUS EC CPU Fan [/asus-ec/fan/0-cpu]", ["内置风扇 2"] = "ASUS EC GPU Fan [/asus-ec/fan/1-gpu]", ["内置风扇 3"] = "ASUS EC Middle Fan [/asus-ec/fan/2-middle]" }
            });
            check(vm.CpuTemperatureStatusText.Contains("ASUS EC", StringComparison.Ordinal) && vm.VendorSensorStatusText.Contains("正常", StringComparison.Ordinal),
                "available vendor CPU temperature retains its actual source instead of becoming a generic Package label");
            check(vm.DashboardData.LaptopFanSummary.Contains("5400 / 4100 / 6000", StringComparison.Ordinal) && vm.DashboardData.LaptopFanDetail.Contains("CPU：5400", StringComparison.Ordinal) && vm.DashboardData.FanRpm is null,
                "laptop fan RPM and explicit ASUS slot labels never populate the separate ESP bottom-fan gauge");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Present(MainViewModel vm, TelemetrySnapshot snapshot) =>
        (typeof(MainViewModel).GetMethod("PresentSample", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException("sample presenter")).Invoke(vm, [snapshot]);
}
