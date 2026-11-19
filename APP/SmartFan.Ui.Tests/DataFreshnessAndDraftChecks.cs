using System.IO;
using System.Reflection;
using System.Text.Json;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Core.Ble;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

internal static class DataFreshnessAndDraftChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        check(TelemetryFreshness.Evaluate(null, now, 1000) == TelemetryDataState.Waiting, "no sample means waiting rather than a fabricated zero or driver failure");
        check(TelemetryFreshness.Evaluate(now.AddSeconds(-3), now, 1000) == TelemetryDataState.Live && TelemetryFreshness.Evaluate(now.AddSeconds(-3.1), now, 1000) == TelemetryDataState.Expired,
            "data expires independently after three expected sample periods with a three-second floor");
        check(TelemetryFreshness.Evaluate(now.AddSeconds(-5), now, 2000) == TelemetryDataState.Live && TelemetryFreshness.Evaluate(now, now, 1000, false) == TelemetryDataState.BackendDisconnected,
            "freshness honors slower sampling and a disconnected UI pipe immediately invalidates old data");
        check(TelemetryFreshness.Evaluate(now.AddMinutes(1), now, 1000) == TelemetryDataState.Expired, "a future timestamp cannot look permanently fresh");
        CheckProjection(check, now); CheckDraftStore(check);
        var environment = new HardwareMonitorDiagnostics { PawnIoInstalled = false, PawnIoDeviceAccessible = false, PawnIoDeviceErrorCode = 2 };
        check(CpuMetricDiagnostics.Describe("核心频率", null, 24, environment).Contains("未安装 PawnIO") && CpuMetricDiagnostics.Describe("电压", null, 0, environment).Contains("重启后台"),
            "missing CPU clock and voltage explain the driver dependency and the correct process to restart");
        check(CpuMetricDiagnostics.Describe("电压", null, 0, new() { PawnIoInstalled = true, PawnIoDeviceAccessible = true }).Contains("没有") &&
            CpuMetricDiagnostics.Describe("核心频率", null, 24, new() { PawnIoInstalled = true, PawnIoDeviceAccessible = true }).Contains("匹配读数"),
            "an accessible driver does not falsely promise that unsupported CPU sensors are readable");
        check(CpuMetricDiagnostics.Describe("电压", null, 0, new() { PawnIoInstalled = true, PawnIoDeviceErrorCode = 5 }).Contains("后台的管理员权限"),
            "driver access denied is attributed to the sampling backend rather than the frontend window");
    }
    private static void CheckProjection(Action<bool, string> check, DateTimeOffset now)
    {
        var backend = new BackgroundClient($"SmartFan.Freshness.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, new SettingsStore(Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", Guid.NewGuid().ToString("N")), false), new AppLog());
        try
        {
            vm.Settings.Appearance.TemperatureUnit = "F";
            var sample = new TelemetrySnapshot { Timestamp = now, CpuName = "Real CPU", CpuTemperature = 50, CpuUsage = 0, GpuTemperature = 60, RamUsedMiB = 8192,
                SensorNames = new Dictionary<string, string> { ["CPU 温度"] = "ASUS EC CPU [/asus-ec/cpu/temperature]" } };
            Invoke(vm, "PresentSample", sample);
            check(vm.DashboardData.CpuTemperature == 122 && vm.DashboardData.CpuUsage == 0 && !vm.TelemetryIsStale, "a fresh real zero survives presentation and unit conversion");
            var context = vm.DashboardData.MetricContext["cpu.temperature"];
            check(context.DeviceName == "Real CPU" && context.SourceName.Contains("/asus-ec/") && context.History.Single().Value == 122, "large-widget context preserves actual hardware source and converts trend history to the same display unit");
            vm.RefreshDataFreshness(now.AddSeconds(4));
            check(vm.TelemetryIsStale && vm.DashboardData.CpuTemperature is null && vm.DashboardData.CpuUsage is null && vm.CpuTemperatureValue is null && vm.DashboardData.MemoryUsed is null,
                "timer-driven expiration clears every displayed hardware value without waiting for another sample");
            check(vm.DataFreshnessText.Contains("已过期") && vm.DashboardData.MetricContext["cpu.temperature"].History.Last().Value is null && sample.CpuTemperature == 50,
                "expired widgets explain the stale state and break history without mutating raw telemetry");
            Invoke(vm, "PresentSample", new TelemetrySnapshot { Timestamp = now.AddMilliseconds(10), CpuTemperature = 0, CpuUsage = 0 });
            check(!vm.TelemetryIsStale && vm.DashboardData.CpuTemperature == 32 && vm.DashboardData.CpuUsage == 0, "a new sample restores the presentation and keeps actual zero distinct from missing data");
            Invoke(vm, "HandleConnection", new BleConnectionEventArgs(BleConnectionState.Disconnected, "test pipe closed"));
            check(vm.TelemetryIsStale && vm.DashboardData.CpuTemperature is null && vm.DataFreshnessText.Contains("后台连接中断"), "UI pipe loss immediately invalidates retained CPU data even when its timestamp is recent");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void CheckDraftStore(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-drafts", "draft-" + Guid.NewGuid().ToString("N"));
        var store = new DashboardDraftStore(directory); var baseline = new DashboardLayoutSettings();
        var original = JsonSerializer.Serialize(baseline); var draft = baseline.Clone(); draft.Widgets.RemoveAt(0);
        try
        {
            check(store.LoadAsync(baseline).GetAwaiter().GetResult() is null, "a new installation has no implicit edit recovery");
            store.SaveAsync(baseline, draft).GetAwaiter().GetResult();
            var recovered = store.LoadAsync(baseline).GetAwaiter().GetResult();
            check(recovered is not null && recovered.Widgets.Count == 6 && JsonSerializer.Serialize(baseline) == original, "draft persistence recovers edits without changing committed settings");
            recovered!.Widgets.Clear();
            check(store.LoadAsync(baseline).GetAwaiter().GetResult()!.Widgets.Count == 6, "recovered drafts do not alias the stored layout");
            check(store.LoadAsync(draft).GetAwaiter().GetResult() is null, "recovery does not overwrite a newly imported or separately saved baseline");
            store.SaveAsync(baseline, null).GetAwaiter().GetResult();
            check(store.LoadAsync(baseline).GetAwaiter().GetResult() is null, "cancel and completed saves discard obsolete recovery state");
            store.SaveAsync(baseline, draft).GetAwaiter().GetResult();
            var path = Path.Combine(directory, "dashboard-draft.json"); File.WriteAllText(path, "invalid recovery json");
            var rejected = false; try { store.LoadAsync(baseline).GetAwaiter().GetResult(); } catch (JsonException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == "invalid recovery json", "corrupt recovery files are reported and preserved for diagnosis");
        }
        finally
        {
            foreach (var name in new[] { "dashboard-draft.json", "dashboard-draft.json.tmp" }) { var path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static void Invoke(MainViewModel vm, string method, object argument) =>
        (typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(method)).Invoke(vm, [argument]);
}
