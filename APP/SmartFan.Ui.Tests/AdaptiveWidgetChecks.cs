using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;

namespace SmartFan.Ui.Tests;

internal static class AdaptiveWidgetChecks
{
    public static void Run(Action<bool, string> check, string? captureDirectory = null)
    {
        var now = DateTimeOffset.UtcNow;
        DashboardTrendPoint[] points = [new(now.AddSeconds(-60), 40), new(now.AddSeconds(-45), 70), new(now.AddSeconds(-30), null), new(now.AddSeconds(-15), 58), new(now, 63.5)];
        var context = new DashboardMetricContext("Intel Core Ultra 9 285HX", "ASUS EC / CPU Temperature [/asus-ec/cpu/temperature]", "当前采样有效", points);
        var data = new DashboardTelemetryData { CpuTemperature = 63.5, CpuUsage = 27, MetricContext = new Dictionary<string, DashboardMetricContext> { ["cpu.temperature"] = context } };
        var host = new DashboardWidgetHost { Metrics = data, WidgetKey = "cpu.temperature" };
        Layout(host, 260, 300); Drain();
        var tile = Descendants<DashboardMetricTile>(host).Single();
        var texts = Descendants<TextBlock>(tile).ToArray();
        check(ReferenceEquals(tile.Metadata, context), "widget metadata uses the root-projected context without a second sampler");
        check(!texts.Any(text => text.Text.Contains("Intel") || text.Text.Contains("ASUS") || text.Text.Contains("/asus-ec/")), "large home metrics omit hardware models and sensor identifiers from the visible card body");
        check(tile.ToolTip?.ToString()?.Contains(context.DeviceName) == true && tile.ToolTip.ToString()!.Contains(context.SourceName), "home metrics preserve complete hardware and sensor provenance in the card tooltip");
        var trend = Descendants<DashboardMiniTrend>(tile).Single();
        check(trend.Visibility == Visibility.Visible && ReferenceEquals(trend.Points, points) && trend.Points[2].Value is null, "large cards show timestamped projected history while retaining unavailable gaps");
        check(texts.Any(text => text.Text == "最近峰值 70 °C"), "large metrics derive the recent peak from valid display-unit samples");
        check(!texts.Any(text => text.Text == "本机实时数据"), "adaptive cards omit repetitive generic live-data subtitles");
        check(VisibleTextFits(tile), "large metric values and recent peak fit within the simplified widget body");

        Layout(host, 260, 140); Drain();
        check(trend.Visibility == Visibility.Collapsed && !texts.Any(text => text.Text.Contains("Intel") || text.Text.Contains("ASUS") || text.Text.Contains("/asus-ec/")) && texts.Any(text => text.Text == "63.5"), "medium home cards prioritize their readout without adding hardware or sensor-source subtitles");
        Layout(host, 110, 55); Drain();
        check(trend.Visibility == Visibility.Collapsed && texts.Single(text => text.Text == "CPU 温度").Visibility == Visibility.Collapsed && texts.Any(text => text.Text == "63.5" && text.Visibility == Visibility.Visible), "small cards prioritize the actual value without duplicating their outer title");
        check(VisibleTextFits(tile), "compact metric numbers and units remain within the clipped card");

        data.CpuTemperature = null;
        data.MetricContext = new Dictionary<string, DashboardMetricContext> { ["cpu.temperature"] = context with { Status = "数据已过期：等待后台重新采样" } };
        Drain(); Layout(host, 110, 55);
        var badge = Descendants<Border>(tile).Single(border => border.Child is TextBlock text && text.Text == "不可用");
        check(badge.Visibility == Visibility.Visible && badge.ToolTip?.ToString()?.Contains("已过期") == true && texts.Any(text => text.Text == "—"), "unavailable compact cards expose a readable badge and the exact missing-data reason");
        check(Descendants<Grid>(tile).Single(grid => grid.ColumnDefinitions.Count == 2).Visibility == Visibility.Collapsed, "unavailable readings render no misleading zero progress bar");

        tile.UnavailableText = "未安装 PawnIO，CPU 频率与电压暂不可读；安装驱动并重启后台后重新检查。";
        Layout(host, 260, 300); Drain();
        check(!texts.Any(text => text.Text.Contains("已过期") || text.Text.Contains("PawnIO") || text.Text.Contains("Intel") || text.Text.Contains("/asus-ec/")) && texts.Any(text => text.Text == "不可用"),
            "large unavailable home cards keep the dash and badge without turning driver or stale-data explanations into body paragraphs");
        check(tile.ToolTip?.ToString()?.Contains("已过期") == true && tile.ToolTip.ToString()!.Contains("PawnIO") && tile.ToolTip.ToString()!.Contains(context.SourceName),
            "simplifying an unavailable home card retains its full stale state, driver reason and source in the tooltip");

        data.MetricContext = new Dictionary<string, DashboardMetricContext> { ["cpu.temperature"] = context };
        data.CpuTemperature = 67; host.DisplayMode = DashboardDisplayMode.Gauge; Drain(); Layout(host, 300, 320);
        var gauge = Descendants<DashboardGauge>(host).Single();
        check(gauge.Metadata?.SourceName == context.SourceName && gauge.Value == 67 && Math.Abs(gauge.DisplayedFraction - .67) < .00001, "switching to a gauge preserves source, data and immediate offscreen fraction");
        var gaugeImage = new RenderTargetBitmap(300, 320, 96, 96, PixelFormats.Pbgra32); gaugeImage.Render(host);
        var gaugeDrawing = VisualTreeHelper.GetDrawing(gauge);
        var painted = gaugeDrawing is null ? "" : string.Concat(Drawings(gaugeDrawing).OfType<GlyphRunDrawing>().Select(drawing => drawing.GlyphRun.Characters is { } characters ? new string(characters.ToArray()) : ""));
        check(painted.Contains("67") && !painted.Contains("Intel") && !painted.Contains("ASUS") && !painted.Contains("/asus-ec/") && gauge.ToolTip?.ToString()?.Contains(context.SourceName) == true,
            "large gauges draw no hardware provenance text while keeping the complete source available on hover");
        gauge.Value = double.NaN;
        check(gauge.DisplayedFraction == 0, "gauge rejects nonfinite values without retaining a valid-looking arc");
        gauge.Value = -1;
        check(gauge.DisplayedFraction == 0, "gauge rejects negative sensor values without drawing a false arc");

        var memory = new DashboardWidgetHost { WidgetKey = "memory.usage", Metrics = new() { MemoryUsed = 8, MemoryTotal = 32 } };
        Layout(memory, 260, 140); Drain();
        check(Descendants<TextBlock>(memory).Any(text => text.Visibility == Visibility.Visible && text.Text.StartsWith("共 32.0") && text.Text.Contains("GiB") && text.Text.Contains("已用")),
            "simplified home metrics retain useful numeric memory-capacity and utilization detail");

        CheckMotion(check);
        if (captureDirectory is not null) Capture(captureDirectory, data, context);
    }

    private static void CheckMotion(Action<bool, string> check)
    {
        var gauge = new DashboardGauge { MinWidth = 0, MinHeight = 0, Value = 20 };
        var tile = new DashboardMetricTile { Title = "使用率", Value = 20 };
        var panel = new Grid(); panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new()); panel.Children.Add(gauge); Grid.SetColumn(tile, 1); panel.Children.Add(tile);
        var window = new Window { Content = panel, Width = 500, Height = 200, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show(); Drain(); gauge.Value = 80; tile.Value = 80;
            var progress = Descendants<Border>(tile).Single(border => border.RenderTransform is ScaleTransform);
            if (SystemParameters.ClientAreaAnimation)
                check(gauge.HasAnimatedProperties && ((ScaleTransform)progress.RenderTransform).HasAnimatedProperties, "visible progress and gauge changes start finite transitions when Windows animations are enabled");
            Wait(260);
            check(Math.Abs(gauge.DisplayedFraction - .8) < .00001 && !gauge.HasAnimatedProperties && !((ScaleTransform)progress.RenderTransform).HasAnimatedProperties, "value transitions settle exactly and remove animation clocks instead of keeping a render loop alive");
            gauge.Value = 30; tile.Value = 30; Wait(35); gauge.Value = 60; tile.Value = 60; Wait(260);
            check(Math.Abs(gauge.DisplayedFraction - .6) < .00001 && !gauge.HasAnimatedProperties && !((ScaleTransform)progress.RenderTransform).HasAnimatedProperties, "rapid data changes retarget smoothly and do not leave old animation clocks attached");
        }
        finally { window.Close(); Drain(); }
    }

    private static bool VisibleTextFits(FrameworkElement root) => Descendants<TextBlock>(root).Where(text => text.IsVisible).All(text =>
    {
        var bounds = text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
        return bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= root.ActualWidth + .5 && bounds.Bottom <= root.ActualHeight + .5;
    });
    private static void Capture(string directory, DashboardTelemetryData data, DashboardMetricContext context)
    {
        data.CpuTemperature = 63.5; data.MetricContext = new Dictionary<string, DashboardMetricContext> { ["cpu.temperature"] = context };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        foreach (var size in new[] { new Size(130, 65), new Size(240, 155), new Size(270, 300) })
        {
            var host = new DashboardWidgetHost { Metrics = data, WidgetKey = "cpu.temperature", Width = size.Width, Height = size.Height };
            var border = new Border { Child = host, Padding = new Thickness(14), CornerRadius = new CornerRadius(20), Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); panel.Children.Add(border);
        }
        var root = new Border { Child = panel }; root.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Layout(root, 820, 380); Drain();
        var image = new RenderTargetBitmap(820, 380, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "widget-adaptive-sizes.png")); encoder.Save(output);
    }
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame); }
    private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var descendant in Drawings(child)) yield return descendant;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
