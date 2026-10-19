using System.Text.Json;
using SmartFan.App.Models;

internal static class WidgetAddChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(DashboardLayoutEngine.Minimum("cpu.temperature") == (2, 2), "standalone readings support a compact two-column tile");
        check(DashboardLayoutEngine.Minimum("cpu.combined") == (4, 3) && DashboardLayoutEngine.Minimum("fan.control") == (5, 3), "combined readings and interactive controls retain their readable minimum");
        check(DashboardLayoutEngine.NormalizeSize("cpu.temperature", int.MinValue, int.MaxValue, DashboardAspectRatio.Free) == (2, 8), "free dimensions clamp safely even for extreme input");
        check(DashboardLayoutEngine.NormalizeSize("cpu.combined", 4, 3, DashboardAspectRatio.Wide) == (6, 3), "combined wide preset grows enough to retain both readings");
        check(DashboardLayoutEngine.NormalizeSize("cpu.temperature", 3, 2, DashboardAspectRatio.Wide) == (4, 2), "odd width rounds predictably to an exact wide ratio");
        check(DashboardLayoutEngine.NormalizeSize("cpu.temperature", 2, 5, DashboardAspectRatio.Tall, fromWidth: false) == (3, 6), "height-driven tall sizing derives width from the chosen height");
        check(!DashboardLayoutEngine.SupportsAspectRatio("fan.control", DashboardAspectRatio.Tall) && !DashboardLayoutEngine.SupportsAspectRatio("fan.history", DashboardAspectRatio.Tall), "unfittable tall controls are omitted from the ratio choices");
        var impossibleRejected = false;
        try { DashboardLayoutEngine.NormalizeSize("fan.control", 16, 8, DashboardAspectRatio.Tall); }
        catch (ArgumentException) { impossibleRejected = true; }
        check(impossibleRejected, "normalization rejects an impossible preset without silently violating minimum size");
        foreach (var kind in DashboardLayoutEngine.Kinds)
            foreach (var ratio in Enum.GetValues<DashboardAspectRatio>())
            {
                // Tall 1:2 controls would require height >= 10 in an eight-row desktop.
                if (!DashboardLayoutEngine.SupportsAspectRatio(kind, ratio)) continue;
                var minimum = DashboardLayoutEngine.Minimum(kind);
                var size = DashboardLayoutEngine.NormalizeSize(kind, minimum.Width, minimum.Height, ratio);
                check(DashboardLayoutEngine.TryPrepareWidget(kind, size.Width, size.Height, ratio) is not null, $"normalized {kind}/{ratio} size is ready for addition");
            }
        check(DashboardLayoutEngine.TryPrepareWidget("cpu.temperature", 1, 2) is null, "pre-add dimensions reject unreadable sizes");
        check(DashboardLayoutEngine.TryPrepareWidget("cpu.temperature", 3, 2, DashboardAspectRatio.Wide) is null, "exact add preparation rejects a ratio mismatch");
        check(DashboardLayoutEngine.TryPrepareWidget("unknown", 3, 2) is null && DashboardLayoutEngine.TryPrepareWidget("cpu.temperature", 3, 2, (DashboardAspectRatio)99) is null, "invalid kinds and ratio values are rejected");

        var narrowGap = new DashboardLayoutSettings { Widgets = [new() { Id = "occupied", Kind = "cpu.combined", Width = 14, Height = 8 }] };
        var before = JsonSerializer.Serialize(narrowGap);
        var compact = DashboardLayoutEngine.FindAvailableWidget(narrowGap, "cpu.temperature", 2, 2);
        check(compact is { Column: 14, Row: 0, Width: 2, Height: 2 }, "a preselected compact reading fits the two-column gap without moving existing cards");
        check(DashboardLayoutEngine.FindAvailableWidget(narrowGap, "cpu.temperature", 3, 2) is null, "larger chosen dimensions report insufficient space before addition");
        check(JsonSerializer.Serialize(narrowGap) == before, "fit previews leave saved identifiers, dimensions and positions unchanged");
        compact!.Column = 0;
        check(narrowGap.Widgets[0].Column == 0 && narrowGap.Widgets[0].Width == 14, "mutating a preview cannot alter an existing card");
        var twenty = new DashboardLayoutSettings { Widgets = [] };
        for (var i = 0; i < 20; i++) twenty.Widgets.Add(new() { Kind = "cpu.temperature", Column = (i % 8) * 2, Row = (i / 8) * 2, Width = 2, Height = 2 });
        check(DashboardLayoutEngine.Validate(twenty).Count == 0 && DashboardLayoutEngine.FindAvailableWidget(twenty, "cpu.temperature", 2, 2) is null, "fit preview honors the twenty-widget limit even when physical space remains");
    }
}
