using System.IO;
using System.Reflection;
using System.Windows.Threading;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Core.Ble;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

internal static class MetricFreshnessRegressionChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckIndependentRpm(check); CheckMissingMetricReasons(check); CheckSeriesIdentity(check);
    }

    private static void CheckIndependentRpm(Action<bool, string> check)
    {
        var (vm, backend) = Create(); var now = DateTimeOffset.Now;
        try
        {
            Present(vm, new TelemetrySnapshot { Timestamp = now.AddSeconds(-20), CpuTemperature = 55 });
            ReceiveRpm(backend, 3200, now); vm.RefreshDashboard();
            check(vm.TelemetryIsStale && vm.DashboardData.CpuTemperature is null && vm.DashboardData.FanRpm == 3200 && vm.FanRpmValue == 3200,
                "fresh ESP notifications remain visible independently of expired PC hardware snapshots");
            check(vm.DashboardData.MetricContext["fan.rpm"].Status.Contains("ESP32") && !vm.DashboardData.MetricContext["fan.rpm"].Status.Contains("已过期"),
                "RPM provenance uses its own notification status instead of stale PC telemetry metadata");
            vm.RefreshDataFreshness(now.AddSeconds(4));
            check(vm.FanRpmValue is null && vm.DashboardData.FanRpm is null && vm.DashboardData.MetricContext["fan.rpm"].Status.Contains("已过期"),
                "ESP data expires after its own three-second notification timeout");
            ReceiveRpm(backend, 2700, now.AddSeconds(-5));
            Present(vm, new TelemetrySnapshot { Timestamp = DateTimeOffset.Now, CpuTemperature = 60 });
            check(!vm.TelemetryIsStale && vm.DashboardData.CpuTemperature == 60 && vm.DashboardData.FanRpm is null,
                "a fresh PC snapshot never revives an expired ESP RPM notification");
            ReceiveRpm(backend, 0, DateTimeOffset.Now);
            check(vm.DashboardData.FanRpm == 0 && vm.DashboardData.MetricContext["fan.rpm"].Status.Contains("ESP32"),
                "a real zero-RPM notification restores its own valid status without fabricating hardware telemetry");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void CheckMissingMetricReasons(Action<bool, string> check)
    {
        var (vm, _) = Create();
        try
        {
            Present(vm, new TelemetrySnapshot
            {
                Timestamp = DateTimeOffset.Now, CpuUsage = null, GpuUsage = null, GpuClockMhz = double.NaN,
                CpuClockReadStatus = "未安装 PawnIO，CPU 核心频率无法读取", CpuVoltageReadStatus = "未安装 PawnIO，CPU 核心电压无法读取"
            });
            foreach (var (key, title) in new[] { ("cpu.usage", "CPU 使用率"), ("gpu.usage", "GPU 使用率"), ("gpu.memory", "独显显存使用量"), ("memory.usage", "系统内存使用量") })
            {
                var status = vm.DashboardData.MetricContext[key].Status;
                check(status.Contains(title) && status.Contains("未提供") && !status.Contains("实时采集"), $"missing {key} explains that metric instead of presenting live-sampling status as its failure reason");
            }
            check(vm.DashboardData.MetricContext["gpu.clock"].Status.Contains("非有限数值") && vm.DashboardData.MetricContext["gpu.clock"].Status.Contains("GPU 核心频率"),
                "a nonfinite per-metric value reports an invalid reading rather than a healthy snapshot status");
            check(vm.DashboardData.MetricContext["cpu.clock"].Status.Contains("PawnIO") && vm.DashboardData.MetricContext["cpu.voltage"].Status.Contains("PawnIO"),
                "generic missing-data handling preserves actionable CPU driver diagnostics");
            Present(vm, new TelemetrySnapshot { Timestamp = DateTimeOffset.Now, CpuUsage = 0, GpuUsage = 0, GpuClockMhz = 0, GpuRamUsedMiB = 0, RamUsedMiB = 0 });
            check(vm.DashboardData.CpuUsage == 0 && vm.DashboardData.MetricContext["cpu.usage"].Status.Contains("实时采集") && vm.DashboardData.MetricContext["gpu.memory"].Status.Contains("实时采集"),
                "genuine zero values keep their valid status and do not become missing measurements");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void CheckSeriesIdentity(Action<bool, string> check)
    {
        var (vm, _) = Create(); var now = DateTimeOffset.Now;
        TelemetrySnapshot Sample(double seconds, bool demo, string sensor, string winningCore = "CPU Core #1") => new()
        {
            Timestamp = now.AddSeconds(seconds), IsDemo = demo, CpuName = "Test CPU", GpuName = "Test GPU", CpuTemperature = 50 + seconds, CpuUsage = 30,
            SensorNames = new Dictionary<string, string> { ["CPU 温度"] = sensor, ["CPU 频率"] = winningCore }
        };
        try
        {
            Present(vm, Sample(-2, true, "sensor/A"));
            check(vm.DataFreshnessText.StartsWith("演示数据") && vm.DashboardData.FreshnessText.Contains("演示数据") && vm.DashboardData.MetricContext["cpu.temperature"].Status.Contains("演示数据"),
                "demo data remains explicitly labeled in the main freshness header and compact-widget provenance");
            Present(vm, Sample(-1, false, "sensor/A"));
            check(!vm.DataFreshnessText.Contains("演示数据") && vm.DashboardData.MetricContext["cpu.temperature"].History.Count == 1,
                "switching from demo to real hardware starts a new history series rather than connecting simulated readings");
            Present(vm, Sample(-.5, false, "sensor/A", "CPU Core #2"));
            check(vm.DashboardData.MetricContext["cpu.temperature"].History.Count == 2,
                "a different winning core in maximum CPU-frequency aggregation does not reset unrelated temperature history");
            Present(vm, Sample(0, false, "sensor/B"));
            var history = vm.DashboardData.MetricContext["cpu.temperature"].History;
            check(history.Count == 1 && history[0].Timestamp == now && vm.DashboardData.MetricContext["cpu.temperature"].SourceName == "sensor/B",
                "selecting a different temperature source starts a new series with the matching current provenance");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static (MainViewModel Vm, BackgroundClient Backend) Create()
    {
        var backend = new BackgroundClient($"SmartFan.MetricFreshness.Unattached.{Guid.NewGuid():N}");
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "metric-freshness-" + Guid.NewGuid().ToString("N"));
        return (new MainViewModel(backend, new SettingsStore(directory, false), new AppLog { MinimumLevel = "Error" }), backend);
    }
    private static void Present(MainViewModel vm, TelemetrySnapshot sample) =>
        (typeof(MainViewModel).GetMethod("PresentSample", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException("PresentSample")).Invoke(vm, [sample]);
    private static void ReceiveRpm(BackgroundClient backend, uint rpm, DateTimeOffset timestamp)
    {
        var handler = (EventHandler<RpmEventArgs>?)typeof(BackgroundClient).GetField(nameof(BackgroundClient.RpmReceived), BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(backend)
            ?? throw new MissingFieldException("BackgroundClient.RpmReceived");
        handler(backend, new RpmEventArgs(rpm, timestamp));
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame);
    }
}
