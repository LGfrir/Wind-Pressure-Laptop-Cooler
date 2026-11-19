using System.Text.Json.Nodes;
using SmartFan.App.Models;
using SmartFan.App.Services;

internal static class WidgetLayoutChecks
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        WidgetAddChecks.Run(check);
        var defaults = new DashboardLayoutSettings();
        check(DashboardLayoutEngine.Validate(defaults).Count == 0, "default widget layout is valid");
        var cpu = defaults.Widgets.Single(widget => widget.Id == "cpu").Clone(); cpu.Column = 4;
        var moved = DashboardLayoutEngine.TryPlace(defaults, cpu);
        check(moved is not null && moved.Widgets.Single(widget => widget.Id == "gpu").Column == 0, "moving one full-grid tile swaps the displaced equal-sized tile");
        check(defaults.Widgets.Single(widget => widget.Id == "cpu").Column == 0, "layout placement never mutates its input");
        cpu.Width = 16; cpu.Height = 8; cpu.Column = 0;
        check(DashboardLayoutEngine.TryPlace(defaults, cpu) is null && defaults.Widgets.Count == 7, "impossible resize is atomic");
        cpu = defaults.Widgets[0].Clone(); cpu.Column = int.MaxValue;
        check(DashboardLayoutEngine.TryPlace(defaults, cpu) is null, "overflowing coordinates are rejected before packing");
        var malformed = defaults.Clone(); malformed.Widgets[0].Kind = "untrusted-widget";
        check(DashboardLayoutEngine.Validate(malformed).Count > 0, "unknown persisted widget types are rejected");
        malformed = defaults.Clone(); malformed.Widgets[1].Id = malformed.Widgets[0].Id;
        check(DashboardLayoutEngine.Validate(malformed).Count > 0, "duplicate persisted widget IDs are rejected");
        malformed = defaults.Clone(); malformed.Widgets[1].Column = 0;
        check(DashboardLayoutEngine.Validate(malformed).Count > 0, "overlapping imported layouts are rejected");
        malformed = defaults.Clone(); malformed.Widgets[0].DisplayMode = (DashboardDisplayMode)999;
        check(DashboardLayoutEngine.Validate(malformed).Count > 0, "unknown presentation modes are rejected");
        malformed = defaults.Clone(); malformed.Widgets[0].AspectRatio = DashboardAspectRatio.Square;
        check(DashboardLayoutEngine.Validate(malformed).Count > 0, "persisted ratio and dimensions must agree");
        check(DashboardLayoutEngine.Validate(new() { Widgets = [] }).Count == 0, "an intentionally empty widget desktop remains valid");
        var store = new SettingsStore(Path.Combine(directory, "widgets"), migrateOldPreferences: false);
        var settings = new AppSettings { Dashboard = moved! }; settings.Appearance.UiScale = .9;
        settings.Dashboard.Widgets.Single(widget => widget.Id == "cpu").DisplayMode = DashboardDisplayMode.Gauge;
        await store.SaveAsync(settings);
        var reloaded = await store.LoadAsync();
        check(reloaded.Warnings.Count == 0 && reloaded.Settings.Dashboard.Widgets.Single(widget => widget.Id == "cpu") is { Column: 4, DisplayMode: DashboardDisplayMode.Gauge }, "widget placement and presentation survive JSON persistence");
        check(reloaded.Settings.Appearance.UiScale == .9, "saving widgets preserves unrelated appearance settings");
        // Older v1 files do not contain the additive dashboard property.
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.SettingsPath))!.AsObject(); json.Remove("Dashboard");
        await File.WriteAllTextAsync(store.SettingsPath, json.ToJsonString());
        var old = await store.LoadAsync();
        check(old.Warnings.Count == 0 && old.Settings.Dashboard.Widgets.Count == 7 && old.Settings.Appearance.UiScale == .9, "existing v1 settings receive default widgets without losing preferences");
        var exported = Path.Combine(directory, "widget-export.json"); await store.ExportAsync(exported, settings);
        var imported = await store.ImportAsync(exported);
        check(imported.Dashboard.Widgets.Single(widget => widget.Id == "cpu").DisplayMode == DashboardDisplayMode.Gauge, "full configuration import/export includes the widget desktop");
        settings.Dashboard = new() { Widgets = [new() { Id = "ratio", Kind = "cpu.usage", Width = 6, Height = 3, AspectRatio = DashboardAspectRatio.Wide }] };
        await store.SaveAsync(settings); reloaded = await store.LoadAsync();
        check(reloaded.Settings.Dashboard.Widgets.Single().AspectRatio == DashboardAspectRatio.Wide, "locked aspect ratio persists alongside geometry");
        var missingDriver = new HardwareMonitorDiagnostics { PawnIoInstalled = false, PawnIoDeviceAccessible = false };
        var blockedPower = SensorReadPolicy.Normalize("/intelcpu/0", "Power", 0, missingDriver);
        check(blockedPower.Value is null && blockedPower.Status!.Contains("PawnIO"), "captured zero-watt Intel reading is unavailable when its required driver is absent");
        check(SensorReadPolicy.Normalize("/intelcpu/0", "Load", 0, missingDriver).Value == 0, "genuine zero CPU load survives missing MSR access");
        check(SensorReadPolicy.Normalize("/asus-ec/cpu", "Temperature", 82, missingDriver).Value == 82, "independent ASUS EC temperature remains valid without PawnIO");
        check(SensorReadPolicy.Normalize("/gpu-nvidia/0", "Voltage", .8, missingDriver).Value == .8, "NVIDIA API readings are not blocked by the Intel driver condition");
        check(SensorReadPolicy.Normalize("/intelcpu/0", "Power", 0, new() { PawnIoDeviceAccessible = true }).Value == 0, "real zero is preserved when the required endpoint is accessible");
    }
}
