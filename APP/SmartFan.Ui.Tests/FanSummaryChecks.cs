using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class FanSummaryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var data = new DashboardTelemetryData();
        var host = new DashboardWidgetHost { WidgetKey = "laptop.fans", Metrics = data };
        Layout(host, 280, 130); Drain();
        var widget = host.Content as LaptopFanWidget ?? throw new InvalidOperationException("Laptop fan widget missing.");
        var label = Descendants<TextBlock>(widget).Single(text => text.Text == data.LaptopFanSummary);
        check(label.Text == "电脑内置风扇尚未采样", "laptop fan component has an explicit unsampled state");
        data.LaptopFanSummary = "内置风扇 5400 / 4100 / 6000 RPM";
        data.LaptopFanDetail = "CPU：5400 RPM\nGPU：4100 RPM\n辅助：6000 RPM\n与 ESP32 散热底座数据独立。";
        data.LaptopFans = [new("CPU", 5400), new("GPU", 4100), new("辅助", 6000)];
        Drain(); Layout(host, 280, 130);
        check(label.Text == data.LaptopFanSummary && Equals(widget.ToolTip, data.LaptopFanDetail), "laptop fan component refreshes summary and precise source labels together");
        check(Descendants<TextBlock>(widget).Any(text => text.Text == "5,400") && Descendants<TextBlock>(widget).Any(text => text.Text == "辅助"), "laptop fan widget shows separate real RPM readings and source labels");
        var replacement = new DashboardTelemetryData { LaptopFanSummary = "内置风扇 — / 1800 RPM", LaptopFanDetail = "风扇 1：无读数\n风扇 2：1800 RPM" };
        host.Metrics = replacement; data.LaptopFanSummary = "stale"; Drain();
        check(label.Text == replacement.LaptopFanSummary && Equals(widget.ToolTip, replacement.LaptopFanDetail), "generic fan slots and unavailable values use supplied labels without assuming CPU or GPU positions");

        var dashboard = new DashboardView { Metrics = replacement }; Layout(dashboard, 1004, 445); Drain(); Layout(dashboard, 1004, 445);
        var widgets = Descendants<DashboardWidgetHost>(dashboard).ToArray();
        check(widgets.Length == 5 && widgets.Count(widget => widget.WidgetKey == "laptop.fans") == 1 && widgets.Count(widget => widget.WidgetKey == "fan.rpm") == 1, "homepage keeps laptop fan summary distinct from the ESP32 RPM gauge");
        check(!Descendants<ScrollViewer>(dashboard).Any() && widgets.All(widget => widget.TransformToAncestor(dashboard).TransformBounds(new Rect(widget.RenderSize)).Bottom <= dashboard.ActualHeight + 1), "laptop fan summary fits the existing one-screen dashboard without scrolling");
        CheckMinimumCardRows(check);
    }

    private static void CheckMinimumCardRows(Action<bool, string> check)
    {
        var metrics = new DashboardTelemetryData();
        var dashboard = new DashboardView
        {
            Metrics = metrics,
            Layout = new() { Widgets = [new() { Id = "five-fan-check", Kind = "laptop.fans", Width = 4, Height = 3 }] }
        };
        foreach (var size in new[] { new Size(1004, 445), new Size(1368, 620) })
            foreach (var count in new[] { 3, 5 })
            {
                var readings = FiveFanReadings().Take(count).ToArray(); metrics.LaptopFans = readings;
                Layout(dashboard, size.Width, size.Height); Drain(); Layout(dashboard, size.Width, size.Height);
                var host = Descendants<DashboardWidgetHost>(dashboard).Single();
                CheckEveryReading((LaptopFanWidget)host.Content, (FrameworkElement)VisualTreeHelper.GetParent(host), readings, check,
                    $"minimum 4×3 fan card at {size.Width}×{size.Height} with {count} fans");
            }
    }

    internal static LaptopFanReading[] FiveFanReadings() => [new("CPU", 1800), new("GPU", 1750), new("辅助", 2400), new("风扇 4", 2200), new("风扇 5", null)];

    internal static void CheckEveryReading(LaptopFanWidget widget, FrameworkElement clippedBody, IReadOnlyList<LaptopFanReading> expected,
        Action<bool, string> check, string description)
    {
        var items = (ItemsControl)widget.FindName("Fans");
        var bodyBounds = new Rect(clippedBody.RenderSize); bodyBounds.Inflate(.5, .5);
        check(items.Items.Count == expected.Count, $"{description}: every sensor slot remains in the items source");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(clippedBody).TransformBounds(new Rect(element.RenderSize));
        Rect? previousRow = null;
        for (var index = 0; index < expected.Count; index++)
        {
            var reading = expected[index];
            var container = items.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement
                ?? throw new InvalidOperationException($"{description}: missing real container for fan {index + 1}");
            var texts = Descendants<TextBlock>(container).ToArray();
            var name = texts.Single(text => text.Text == reading.Name);
            var rpm = texts.Single(text => text.Text == (reading.Rpm?.ToString("N0", CultureInfo.CurrentCulture) ?? "—"));
            var unit = texts.Single(text => text.Text == " RPM");
            var rowBounds = Bounds(container);
            check(bodyBounds.Contains(rowBounds) && (previousRow is null || previousRow.Value.Bottom <= rowBounds.Top + .5),
                $"{description}: fan {index + 1} row stays inside the clipped body without overlapping another row (row={rowBounds}, body={bodyBounds})");
            foreach (var text in new[] { name, rpm, unit })
            {
                var glyphs = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                    VisualTreeHelper.GetDpi(text).PixelsPerDip);
                check(text.Visibility == Visibility.Visible && text.ActualWidth > 0 && text.ActualHeight > 0 && bodyBounds.Contains(Bounds(text)) &&
                      text.ActualWidth + .5 >= glyphs.WidthIncludingTrailingWhitespace && text.ActualHeight + .5 >= glyphs.Height,
                    $"{description}: complete fan {index + 1} {text.Text} is visible and fits (text={Bounds(text)}, body={bodyBounds})");
            }
            check(name.FontSize >= 9 && rpm.FontSize >= 11 && unit.FontSize >= 9,
                $"{description}: fan {index + 1} keeps readable labels, RPM and units");
            previousRow = rowBounds;
        }
        check(!Descendants<ScrollViewer>(widget).Any(), $"{description}: all fan readings fit without a scrollbar");
    }

    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
