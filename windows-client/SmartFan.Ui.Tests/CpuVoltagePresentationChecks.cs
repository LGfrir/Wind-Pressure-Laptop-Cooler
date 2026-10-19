using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class CpuVoltagePresentationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var data = new DashboardTelemetryData
        {
            CpuVoltage = 1.05,
            MetricContext = new Dictionary<string, DashboardMetricContext>
            {
                ["cpu.voltage"] = new("Test CPU Model", "CPU Core #1 VID [/cpu/vid/0]", "VID 是核心请求电压，并非实测 VCore。", [])
            }
        };
        check(data.CpuVoltageLabel == "核心电压" && data.CpuVoltageCardTitle == "CPU 核心电压",
            "unclassified or VCore voltage presentation starts with the compatible core-voltage labels");
        var host = new DashboardWidgetHost { WidgetKey = "cpu.voltage", Metrics = data };
        Layout(host, 260, 140);
        var tile = Descendants<DashboardMetricTile>(host).Single();
        check(tile.Title == "核心电压" && tile.Value == 1.05 && tile.Unit == "V" && tile.ValueFormat == "0.000",
            "the normal voltage tile preserves its real numeric value, unit and precision");
        SetVid(data, 1.128); Layout(host, 260, 140);
        check(tile.Title == "最高核心 VID" && tile.Value == 1.128 && Descendants<TextBlock>(tile).Any(text => text.Text == "最高核心 VID") && ReferenceEquals(tile, host.Content),
            "switching to VID updates the existing tile label and reading without rebuilding it or pretending it is VCore");
        check(tile.ToolTip?.ToString()?.Contains("并非实测 VCore") == true && !BodyLeaksProvenance(host),
            "VID provenance and physical-voltage distinction stay in the tooltip while the home body remains concise");

        host.DisplayMode = DashboardDisplayMode.Gauge; Layout(host, 260, 180);
        var gauge = Descendants<DashboardGauge>(host).Single();
        var heading = ((Grid)host.Content).Children.OfType<TextBlock>().Single();
        check(heading.Text == "最高核心 VID" && gauge.Value == 1.128 && BindingOperations.GetBinding(heading, TextBlock.TextProperty)?.Path.Path == nameof(DashboardTelemetryData.CpuVoltageLabel),
            "the voltage gauge header carries the dynamic voltage-label binding instead of a copied static string");
        data.CpuVoltageLabel = "核心电压"; data.CpuVoltage = 1.036; Layout(host, 260, 180);
        check(heading.Text == "核心电压" && gauge.Value == 1.036 && ReferenceEquals(gauge, Descendants<DashboardGauge>(host).Single()),
            "an existing gauge changes back to the VCore label and reading without replacing its content");

        SetVid(data, 1.128);
        var view = new DashboardView { Metrics = data, Layout = new() { Widgets = [new() { Id = "voltage", Kind = "cpu.voltage", Width = 4, Height = 3 }] } };
        Layout(view, 1004, 445);
        var frame = Card(view); var outerTitle = OuterTitle(frame); var outerHost = Descendants<DashboardWidgetHost>(frame).Single();
        check(outerTitle.Text == "CPU 核心 VID" && BindingOperations.GetMultiBinding(outerTitle, TextBlock.TextProperty) is not null && !BodyLeaksProvenance(frame),
            "the outer voltage card title identifies VID dynamically while avoiding model and sensor-source text");
        data.CpuVoltageLabel = "核心电压"; data.CpuVoltageCardTitle = "CPU 核心电压"; Layout(view, 1004, 445);
        check(outerTitle.Text == "CPU 核心电压" && ReferenceEquals(frame, Card(view)) && ReferenceEquals(outerHost, Descendants<DashboardWidgetHost>(frame).Single()),
            "changing voltage semantics updates the existing outer title without a dashboard rebuild");
        SetVid(data, 1.2); Layout(view, 1004, 445);
        var replacement = new DashboardTelemetryData { CpuVoltage = 1.016 };
        view.Metrics = replacement; Layout(view, 1004, 445);
        check(outerTitle.Text == "CPU 核心电压" && outerHost.Metrics == replacement && Descendants<DashboardMetricTile>(outerHost).Single() is { Title: "核心电压", Value: 1.016 } && ReferenceEquals(frame, Card(view)),
            "replacing the telemetry-data object restores the VCore defaults in the same outer frame and metric tile");
        SetVid(data, 1.3); Layout(view, 1004, 445);
        check(outerTitle.Text == "CPU 核心电压" && Descendants<DashboardMetricTile>(outerHost).Single().Value == 1.016,
            "after telemetry replacement old VID-data updates no longer change the visible card");

        var small = new DashboardView { Metrics = data, Layout = new() { Widgets = [new() { Id = "voltage", Kind = "cpu.voltage", Width = 2, Height = 2 }] } };
        Layout(small, 1004, 445);
        var smallTitle = OuterTitle(Card(small));
        var face = new Typeface(smallTitle.FontFamily, smallTitle.FontStyle, smallTitle.FontWeight, smallTitle.FontStretch);
        var glyph = new FormattedText(smallTitle.Text, CultureInfo.CurrentUICulture, smallTitle.FlowDirection, face, smallTitle.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(smallTitle).PixelsPerDip);
        check(smallTitle.Text.Contains("VID") && glyph.WidthIncludingTrailingWhitespace <= smallTitle.ActualWidth + 1.5 && Equals(smallTitle.ToolTip, "CPU 核心 VID"),
            "the smallest legal voltage card keeps VID completely visible while retaining its full card title on hover");
        check(PresentationSource.FromVisual(view) is null && Window.GetWindow(view) is null && PresentationSource.FromVisual(small) is null,
            "voltage-semantics presentation checks open no native window, configuration store or hardware sampler");
    }
    private static void SetVid(DashboardTelemetryData data, double voltage)
    { data.CpuVoltageLabel = "最高核心 VID"; data.CpuVoltageCardTitle = "CPU 核心 VID"; data.CpuVoltage = voltage; }
    private static Border Card(DashboardView view) => ((Canvas)view.FindName("WidgetCanvas")).Children.OfType<Border>().Single();
    private static TextBlock OuterTitle(Border card) => ((Grid)card.Child).Children.OfType<Grid>().Single().Children.OfType<TextBlock>().Single();
    private static bool BodyLeaksProvenance(DependencyObject root) => Descendants<TextBlock>(root).Any(text => text.Text.Contains("Test CPU Model") || text.Text.Contains("/cpu/vid/") || text.Text.Contains("并非实测"));
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle); Dispatcher.PushFrame(frame);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return item;
    }
}
