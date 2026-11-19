using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

internal static class AdaptiveEdgeRegressionChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckTransientSourceGap(check); CheckCompleteTrendWindow(check); CheckCompactGaugeBounds(check);
    }
    private static void CheckTransientSourceGap(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var backend = new BackgroundClient($"SmartFan.AdaptiveEdges.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, new SettingsStore(Path.Combine(AppContext.BaseDirectory, "test-data", "adaptive-edges-" + Guid.NewGuid().ToString("N")), false), new AppLog { MinimumLevel = "Error" });
        TelemetrySnapshot Sample(double seconds, double? usage, string? source, bool demo = false) => new()
        {
            Timestamp = now.AddSeconds(seconds), CpuName = "Stable CPU", GpuName = "Stable GPU", CpuTemperature = 50, CpuUsage = usage, IsDemo = demo,
            SensorNames = source is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["CPU 使用率"] = source }
        };
        void Present(TelemetrySnapshot sample) => (typeof(MainViewModel).GetMethod("PresentSample", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException("PresentSample")).Invoke(vm, [sample]);
        try
        {
            Present(Sample(-1.5, 30, "sensor/A")); Present(Sample(-1, null, null));
            var missing = vm.DashboardData.MetricContext["cpu.usage"];
            check(missing.History.Count == 2 && missing.History[0].Value == 30 && missing.History[1].Value is null && missing.SourceName == "sensor/A",
                "a transient omitted source creates a missing-value gap without deleting previous readings or their known physical source");
            check(vm.DashboardData.MetricContext["cpu.temperature"].History.Count == 2,
                "a single temporarily unavailable usage sensor does not erase unrelated temperature history");
            Present(Sample(-.5, 0, "sensor/A"));
            var recovered = vm.DashboardData.MetricContext["cpu.usage"];
            check(recovered.History.Count == 3 && recovered.History[1].Value is null && recovered.History[2].Value == 0 && vm.DashboardData.CpuUsage == 0,
                "recovering the same source retains the gap and a genuine zero in the existing series");
            Present(Sample(0, 40, "sensor/B"));
            check(vm.DashboardData.MetricContext["cpu.usage"].History.Count == 1 && vm.DashboardData.MetricContext["cpu.usage"].SourceName == "sensor/B",
                "a known physical source replacement still starts a new series after transient-gap recovery");
            Present(Sample(.1, 40, "sensor/B", true));
            check(vm.DashboardData.MetricContext["cpu.usage"].History.Count == 1 && vm.DataFreshnessText.Contains("演示数据"),
                "last-known-source tracking preserves explicit demo stream isolation");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void CheckCompleteTrendWindow(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var points = Enumerable.Range(0, 600).Select(index => new DashboardTrendPoint(now.AddMilliseconds((index - 599) * 500), index == 0 ? 100 : 20d)).ToArray();
        var trend = new DashboardMiniTrend { Points = points };
        var continuousImage = LayoutAndRender(trend, 300, 60);
        var drawing = VisualTreeHelper.GetDrawing(trend) ?? throw new InvalidOperationException("Trend has no retained drawing.");
        var line = Descendants(drawing).OfType<GeometryDrawing>().Single();
        check(line.Geometry.Bounds.Height > 39 && line.Geometry.Bounds.Width > 295,
            "a 600-sample five-minute trend renders its old peak and full time range instead of discarding the first half");
        // Use a visible interval: one missing half-pixel sample can be covered by the round stroke caps.
        var withGap = points.Select((point, index) => index is >= 330 and <= 370 ? point with { Value = null } : point).ToArray();
        trend.Points = withGap; var gapImage = LayoutAndRender(trend, 300, 60);
        var gapLine = Descendants(VisualTreeHelper.GetDrawing(trend)!).OfType<GeometryDrawing>().Single();
        var flatY = (int)Math.Round(gapLine.Geometry.Bounds.Bottom);
        check(ContainsInk(continuousImage, 175, flatY) && ContainsInk(gapImage, 150, flatY) && !ContainsInk(gapImage, 175, flatY) && gapLine.Geometry.Bounds.Height > 39,
            "rendering the complete retained window preserves the old peak and leaves an actual visible gap instead of connecting unavailable samples");
    }
    private static void CheckCompactGaugeBounds(Action<bool, string> check)
    {
        foreach (var (width, height, value, unit) in new[] { (80d, 55d, 12345d, "MHz"), (90d, 55d, 123456d, "RPM"), (80d, 34d, 12345d, "MHz") })
        {
            var gauge = new DashboardGauge { MinWidth = 0, MinHeight = 0, Value = value, Unit = unit, ValueFormat = "N0", Maximum = 150000 };
            LayoutAndRender(gauge, width, height);
            var bounds = VisualTreeHelper.GetContentBounds(gauge);
            check(!bounds.IsEmpty && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
                $"a compact {width}x{height} gauge fits its long {unit} readout within the region beside the arc");
            check(gauge.ToolTip?.ToString()?.Contains($"{value:N0} {unit}") == true,
                "a compact gauge preserves the complete exact reading in its tooltip when a narrow view needs ellipsis");
        }
    }
    private static IEnumerable<Drawing> Descendants(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static bool ContainsInk(BitmapSource bitmap, int x, int y)
    {
        var rectangle = new Int32Rect(Math.Max(0, x - 2), Math.Max(0, y - 2), 5, 5);
        var pixels = new byte[rectangle.Width * rectangle.Height * 4];
        bitmap.CopyPixels(rectangle, pixels, rectangle.Width * 4, 0);
        return Enumerable.Range(0, rectangle.Width * rectangle.Height).Any(index => pixels[index * 4 + 3] > 0);
    }
    private static RenderTargetBitmap LayoutAndRender(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        // AffectsRender invalidation is queued separately from layout. Drain through Render before
        // asking a detached visual for its retained drawing after a same-size dependency-property update.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); return bitmap;
    }
}
