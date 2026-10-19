namespace SmartFan.App.Models;

public enum DashboardDisplayMode { Automatic, Number, Progress, Gauge }
public enum DashboardAspectRatio { Free, Square, Wide, Tall }

/// <summary>Local UI preferences only. Grid coordinates never enter device configuration or telemetry.</summary>
public sealed class DashboardLayoutSettings
{
    public List<DashboardWidgetSettings> Widgets { get; set; } = CreateDefaultWidgets();
    public DashboardLayoutSettings Clone() => new() { Widgets = Widgets.Select(widget => widget.Clone()).ToList() };
    private static List<DashboardWidgetSettings> CreateDefaultWidgets() =>
    [
        new() { Id = "cpu", Kind = "cpu.combined", Column = 0, Row = 0, Width = 4, Height = 3 },
        new() { Id = "gpu", Kind = "gpu.combined", Column = 4, Row = 0, Width = 4, Height = 3 },
        new() { Id = "ram", Kind = "memory.usage", Column = 8, Row = 0, Width = 4, Height = 3 },
        new() { Id = "pc-fans", Kind = "laptop.fans", Column = 12, Row = 0, Width = 4, Height = 3 },
        new() { Id = "esp-rpm", Kind = "fan.rpm", Column = 0, Row = 3, Width = 4, Height = 5 },
        new() { Id = "history", Kind = "fan.history", Column = 4, Row = 3, Width = 6, Height = 5 },
        new() { Id = "control", Kind = "fan.control", Column = 10, Row = 3, Width = 6, Height = 5 }
    ];
}

public sealed class DashboardWidgetSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "cpu.temperature";
    public int Column { get; set; }
    public int Row { get; set; }
    public int Width { get; set; } = 4;
    public int Height { get; set; } = 3;
    public DashboardDisplayMode DisplayMode { get; set; }
    public DashboardAspectRatio AspectRatio { get; set; }
    public DashboardWidgetSettings Clone() => (DashboardWidgetSettings)MemberwiseClone();
}

/// <summary>Pure grid placement shared by persistence validation and pointer/keyboard editing.</summary>
public static class DashboardLayoutEngine
{
    public const int Columns = 16;
    public const int Rows = 8;
    public static readonly IReadOnlyList<string> Kinds = Array.AsReadOnly(new[]
    {
        "cpu.temperature", "cpu.usage", "gpu.temperature", "gpu.usage", "cpu.combined", "gpu.combined",
        "memory.usage", "laptop.fans", "fan.rpm", "fan.history", "fan.control", "cpu.clock", "cpu.voltage", "gpu.clock", "gpu.memory"
    });
    public static (int Width, int Height) Minimum(string kind) => kind switch
    {
        "fan.history" or "fan.control" => (5, 3),
        "cpu.combined" or "gpu.combined" or "laptop.fans" => (4, 3),
        "fan.rpm" => (3, 2),
        _ => (2, 2)
    };
    public static bool SupportsDisplayMode(string kind) => kind is not ("laptop.fans" or "fan.history" or "fan.control");
    public static bool SupportsAspectRatio(string kind, DashboardAspectRatio ratio)
    {
        if (!Kinds.Contains(kind) || !Enum.IsDefined(ratio)) return false;
        if (ratio == DashboardAspectRatio.Free) return true;
        var minimum = Minimum(kind); var (w, h) = RatioUnits(ratio);
        return Math.Max((minimum.Width + w - 1) / w, (minimum.Height + h - 1) / h) <= Math.Min(Columns / w, Rows / h);
    }

    /// <summary>Keep editor dimensions within the widget's readable minimum and the desktop bounds.
    /// A locked ratio follows the changed dimension; the other dimension is derived from it.</summary>
    public static (int Width, int Height) NormalizeSize(string kind, int width, int height, DashboardAspectRatio ratio, bool fromWidth = true)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("未知主页组件。", nameof(kind));
        if (!Enum.IsDefined(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio));
        if (!SupportsAspectRatio(kind, ratio)) throw new ArgumentException("此组件的最小尺寸无法在主页内保持该比例。", nameof(ratio));
        var minimum = Minimum(kind);
        width = Math.Clamp(width, minimum.Width, Columns); height = Math.Clamp(height, minimum.Height, Rows);
        if (ratio == DashboardAspectRatio.Free) return (width, height);
        var (w, h) = RatioUnits(ratio);
        var least = Math.Max((minimum.Width + w - 1) / w, (minimum.Height + h - 1) / h);
        var most = Math.Min(Columns / w, Rows / h);
        var units = Math.Clamp((int)Math.Round(fromWidth ? (double)width / w : (double)height / h, MidpointRounding.AwayFromZero), least, most);
        return (w * units, h * units);
    }

    /// <summary>Prepare the exact chosen dimensions, without silently shrinking or changing the ratio.</summary>
    public static DashboardWidgetSettings? TryPrepareWidget(string kind, int width, int height, DashboardAspectRatio ratio = DashboardAspectRatio.Free)
    {
        var candidate = new DashboardWidgetSettings { Kind = kind, Width = width, Height = height, AspectRatio = ratio };
        return Validate(new DashboardLayoutSettings { Widgets = [candidate] }).Count == 0 ? candidate : null;
    }

    /// <summary>Read-only add preview. Existing cards remain in place, and no source settings are changed.</summary>
    public static DashboardWidgetSettings? FindAvailableWidget(DashboardLayoutSettings source, string kind, int width, int height, DashboardAspectRatio ratio = DashboardAspectRatio.Free)
    {
        if (Validate(source).Count != 0 || source.Widgets.Count >= 20) return null;
        var item = TryPrepareWidget(kind, width, height, ratio);
        return item is null ? null : FindSpace(item, source.Widgets);
    }

    public static bool Overlaps(DashboardWidgetSettings a, DashboardWidgetSettings b) =>
        a.Column < b.Column + b.Width && b.Column < a.Column + a.Width && a.Row < b.Row + b.Height && b.Row < a.Row + a.Height;
    public static bool InBounds(DashboardWidgetSettings widget)
    {
        var minimum = Minimum(widget.Kind);
        return widget.Column >= 0 && widget.Row >= 0 && widget.Width >= minimum.Width && widget.Height >= minimum.Height &&
            widget.Width <= Columns && widget.Height <= Rows && widget.Column <= Columns - widget.Width && widget.Row <= Rows - widget.Height;
    }
    public static IReadOnlyList<string> Validate(DashboardLayoutSettings? layout)
    {
        var errors = new List<string>();
        if (layout?.Widgets is null || layout.Widgets.Count > 20) return ["主页组件列表无效（最多 20 个）。"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in layout.Widgets)
        {
            if (widget is null) { errors.Add("主页组件不能为空。"); continue; }
            if (string.IsNullOrWhiteSpace(widget.Id) || widget.Id.Length > 128 || !ids.Add(widget.Id)) errors.Add("主页组件标识无效或重复。");
            if (!Kinds.Contains(widget.Kind)) errors.Add("未知主页组件。");
            if (!InBounds(widget)) errors.Add("主页组件尺寸或位置超出网格。");
            if (!Enum.IsDefined(widget.DisplayMode) || !Enum.IsDefined(widget.AspectRatio)) errors.Add("未知组件显示方式或比例。");
            if (!SupportsDisplayMode(widget.Kind) && widget.DisplayMode != DashboardDisplayMode.Automatic) errors.Add("此组件不支持数值显示方式。");
            var (w, h) = RatioUnits(widget.AspectRatio);
            if (widget.AspectRatio != DashboardAspectRatio.Free && widget.Width * h != widget.Height * w) errors.Add("组件尺寸与锁定的比例不一致。");
        }
        var items = layout.Widgets.Where(widget => widget is not null).ToArray();
        for (var i = 0; i < items.Length; i++)
            for (var j = i + 1; j < items.Length; j++)
                if (Overlaps(items[i], items[j])) errors.Add("主页组件不能重叠。");
        return errors;
    }
    public static (int Width, int Height) RatioUnits(DashboardAspectRatio ratio) => ratio switch
    { DashboardAspectRatio.Wide => (2, 1), DashboardAspectRatio.Tall => (1, 2), _ => (1, 1) };

    /// <summary>Pin the edited tile, then keep unaffected tiles in place; deterministically pack displaced tiles.
    /// Failure leaves the input untouched. A bounded fallback handles otherwise fragmented free space.</summary>
    public static DashboardLayoutSettings? TryPlace(DashboardLayoutSettings source, DashboardWidgetSettings proposed)
    {
        if (Validate(new DashboardLayoutSettings { Widgets = [proposed] }).Count != 0) return null;
        var original = source.Widgets.Where(widget => widget.Id != proposed.Id).ToArray();
        if (original.Sum(widget => widget.Width * widget.Height) + proposed.Width * proposed.Height > Columns * Rows) return null;
        var placed = new List<DashboardWidgetSettings> { proposed.Clone() };
        var displaced = new List<DashboardWidgetSettings>();
        foreach (var widget in original)
        {
            if (placed.Any(other => Overlaps(widget, other))) displaced.Add(widget);
            else placed.Add(widget.Clone());
        }
        if (Pack(displaced, placed)) return Ordered();
        // Retry largest first with all other tiles movable, preserving positions whenever space permits.
        placed = [proposed.Clone()];
        if (Pack(original.OrderByDescending(widget => widget.Width * widget.Height).ToArray(), placed)) return Ordered();
        return null;

        DashboardLayoutSettings Ordered() => new() { Widgets = source.Widgets.Select(widget => placed.Single(item => item.Id == widget.Id)).ToList() };
    }
    private static bool Pack(IEnumerable<DashboardWidgetSettings> items, List<DashboardWidgetSettings> placed)
    {
        foreach (var item in items)
        {
            var candidate = FindSpace(item, placed);
            if (candidate is null) return false;
            placed.Add(candidate);
        }
        return true;
    }
    public static DashboardWidgetSettings? FindSpace(DashboardWidgetSettings item, IEnumerable<DashboardWidgetSettings> others)
    {
        var occupied = others.ToArray();
        var candidates = from row in Enumerable.Range(0, Rows)
                         from column in Enumerable.Range(0, Columns)
                         orderby Math.Abs(column - item.Column) + Math.Abs(row - item.Row), row, column
                         select (column, row);
        foreach (var (column, row) in candidates)
        {
            var candidate = item.Clone(); candidate.Column = column; candidate.Row = row;
            if (InBounds(candidate) && occupied.All(other => !Overlaps(candidate, other))) return candidate;
        }
        return null;
    }
}
