using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Real snapshot projection and dynamic clock bindings without a native window or sampler.</summary>
internal static class CpuClockPresentationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var backend = new BackgroundClient($"SmartFan.CpuClockPresentation.Unattached.{Guid.NewGuid():N}");
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "cpu-clock-" + Guid.NewGuid().ToString("N"));
        var vm = new MainViewModel(backend, new SettingsStore(directory, false), new AppLog { MinimumLevel = "Error" });
        vm.Settings.Telemetry.IntervalMs = 5000;
        var now = DateTimeOffset.Now;
        const string rawStatus = "CPU 硬件核心频率：原始核心时钟的最大有效值。";
        const string reportedStatus = "Windows Processor Information 报告频率，可能为标称或请求值，不代表瞬时硬件核心频率；仅供本机显示。";
        TelemetrySnapshot Sample(double seconds, CpuClockKind kind, double clock, string? readStatus = null) => new()
        {
            Timestamp = now.AddSeconds(seconds), CpuName = "Synthetic CPU Model", CpuTemperature = 63, CpuUsage = 24,
            CpuClockMhz = clock, CpuClockKind = kind, CpuClockReadStatus = readStatus ?? "",
            CpuVoltage = 1.128, CpuVoltageKind = CpuVoltageKind.Vid,
            CpuVoltageReadStatus = "核心 VID 是请求电压，不代表主板实测 VCore。",
            HardwareMonitor = new() { ProviderVersion = "0.9.6", PawnIoInstalled = true, PawnIoDeviceAccessible = true },
            ProviderStatuses = new Dictionary<string, string> { ["WindowsPerf"] = "性能计数器可用", ["Unused"] = " " },
            SensorNames = new Dictionary<string, string>
            {
                ["CPU 频率"] = kind == CpuClockKind.WindowsReported ? "Windows Processor Information / _Total" : "Synthetic CPU / Core #1"
            }
        };
        try
        {
            Present(vm, Sample(-.4, CpuClockKind.HardwareCore, 4875, rawStatus));
            var data = vm.DashboardData;
            check(data.CpuClockLabel == "最高核心频率" && data.CpuClockCardTitle == "CPU 核心频率"
                  && data.CpuClockMhz == 4875 && data.CpuClockStatus == rawStatus,
                "hardware-clock snapshots retain raw-core titles, exact MHz and their specific diagnostic status");

            var host = new DashboardWidgetHost { WidgetKey = "cpu.clock", Metrics = data };
            Layout(host, 260, 160);
            var tile = Descendants<DashboardMetricTile>(host).Single();
            check(tile is { Title: "最高核心频率", Value: 4875, Unit: "MHz", ValueFormat: "N0" }
                  && BindingOperations.GetBinding(tile, DashboardMetricTile.TitleProperty)?.Path.Path == nameof(DashboardTelemetryData.CpuClockLabel),
                "the normal clock widget binds its semantic label while preserving the numeric reading and MHz format");

            var view = new DashboardView { Metrics = data, Layout = new() { Widgets = [new() { Id = "clock", Kind = "cpu.clock", Width = 4, Height = 3 }] } };
            Layout(view, 1004, 445);
            var card = Card(view); var outerTitle = OuterTitle(card);
            check(outerTitle.Text == "CPU 核心频率" && BindingOperations.GetMultiBinding(outerTitle, TextBlock.TextProperty) is not null,
                "the outer CPU clock card uses the live semantic-title binding");

            Present(vm, Sample(-.3, CpuClockKind.HardwareCore, 4900, rawStatus));
            check(data.MetricContext["cpu.clock"].History.Count == 2,
                "successive readings of one hardware-clock kind remain in the same clock history");

            Present(vm, Sample(-.2, CpuClockKind.WindowsReported, 2875, reportedStatus));
            Layout(host, 260, 160); Layout(view, 1004, 445);
            check(data.CpuClockLabel == "Windows 报告频率" && data.CpuClockCardTitle == "CPU 报告频率"
                  && tile.Title == "Windows 报告频率" && tile.Value == 2875 && outerTitle.Text == "CPU 报告频率"
                  && ReferenceEquals(tile, host.Content) && ReferenceEquals(card, Card(view)),
                "a Windows-reported frequency updates existing tile and outer-card titles without rebuilding them or changing the reading");
            check(data.CpuClockStatus == reportedStatus && tile.ToolTip?.ToString()?.Contains(reportedStatus) == true
                  && tile.Metadata?.SourceName == "Windows Processor Information / _Total"
                  && !tile.ToolTip!.ToString()!.Contains("最高核心频率"),
                "an available Windows-reported frequency preserves the backend's exact diagnostic and source instead of claiming the highest hardware core");
            check(data.MetricContext["cpu.clock"].History is [{ Value: 2875 }],
                "changing from hardware clocks to OS-reported values starts a separate series instead of connecting incompatible frequency samples");
            check(vm.HardwareMonitorDetail.Contains("WindowsPerf：性能计数器可用")
                  && !vm.HardwareMonitorDetail.Contains("Unused") && !BodyLeaksDiagnostics(view),
                "provider availability is exposed in advanced diagnostics while blank entries and verbose provenance stay off the home cards");

            host.DisplayMode = DashboardDisplayMode.Gauge; Layout(host, 260, 180);
            var gauge = Descendants<DashboardGauge>(host).Single();
            var heading = ((Grid)host.Content).Children.OfType<TextBlock>().Single();
            check(heading.Text == "Windows 报告频率" && gauge.Value == 2875 && gauge.Unit == "MHz"
                  && BindingOperations.GetBinding(heading, TextBlock.TextProperty)?.Path.Path == nameof(DashboardTelemetryData.CpuClockLabel),
                "gauge presentation inherits the dynamic Windows-reported label, exact MHz reading and its binding");

            Present(vm, Sample(-.1, CpuClockKind.WindowsReported, 3000)); Layout(host, 260, 180);
            check(data.MetricContext["cpu.clock"].History.Count == 2 && heading.Text == "Windows 报告频率"
                  && gauge.Value == 3000 && data.CpuClockStatus.Contains("Windows") && data.CpuClockStatus.Contains("标称或请求值"),
                "successive OS-reported frequencies keep one series and missing detailed status still explains nominal/requested-value limits");

            Present(vm, Sample(0, CpuClockKind.HardwareCore, 5050, rawStatus));
            Layout(host, 260, 180); Layout(view, 1004, 445);
            check(heading.Text == "最高核心频率" && gauge.Value == 5050 && outerTitle.Text == "CPU 核心频率"
                  && ReferenceEquals(gauge, Descendants<DashboardGauge>(host).Single())
                  && data.MetricContext["cpu.clock"].History is [{ Value: 5050 }]
                  && data.CpuVoltageLabel == "最高核心 VID" && data.CpuVoltageCardTitle == "CPU 核心 VID",
                "returning to hardware clock changes existing gauge and card labels, separates history, and preserves VID presentation");

            Present(vm, Sample(.1, CpuClockKind.WindowsReported, 2975, reportedStatus));
            var minimum = DashboardLayoutEngine.Minimum("cpu.clock");
            var small = new DashboardView { Metrics = data, Layout = new() { Widgets = [new() { Id = "small-clock", Kind = "cpu.clock", Width = minimum.Width, Height = minimum.Height }] } };
            Layout(small, 1004, 445);
            var smallTitle = OuterTitle(Card(small));
            var glyph = new FormattedText(smallTitle.Text, CultureInfo.CurrentUICulture, smallTitle.FlowDirection,
                new Typeface(smallTitle.FontFamily, smallTitle.FontStyle, smallTitle.FontWeight, smallTitle.FontStretch),
                smallTitle.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(smallTitle).PixelsPerDip);
            check(smallTitle.Text.Contains("报告") && glyph.WidthIncludingTrailingWhitespace <= smallTitle.ActualWidth + 1.5
                  && Equals(smallTitle.ToolTip, "CPU 报告频率"),
                "the minimum-size clock card keeps its OS-reported qualifier completely visible and retains the full title on hover");

            var replacement = new DashboardTelemetryData { CpuClockMhz = 4100 };
            view.Metrics = replacement; Layout(view, 1004, 445);
            check(outerTitle.Text == "CPU 核心频率" && ReferenceEquals(card, Card(view))
                  && Descendants<DashboardMetricTile>(card).Single() is { Title: "最高核心频率", Value: 4100 },
                "replacing shared telemetry updates both the outer and inner clock title bindings without retaining the old Windows-reported label");
            check(!backend.IsAttached && PresentationSource.FromVisual(view) is null && Window.GetWindow(view) is null
                  && PresentationSource.FromVisual(small) is null && !Directory.Exists(directory),
                "clock presentation checks do not attach a backend, create a native window, sample hardware or write settings");
        }
        finally { vm.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Present(MainViewModel vm, TelemetrySnapshot sample) =>
        (typeof(MainViewModel).GetMethod("PresentSample", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("MainViewModel.PresentSample")).Invoke(vm, [sample]);
    private static Border Card(DashboardView view) => ((Canvas)view.FindName("WidgetCanvas")).Children.OfType<Border>().Single();
    private static TextBlock OuterTitle(Border card) => ((Grid)card.Child).Children.OfType<Grid>().Single().Children.OfType<TextBlock>().Single();
    private static bool BodyLeaksDiagnostics(DependencyObject root) => Descendants<TextBlock>(root).Any(text =>
        text.Text.Contains("Synthetic CPU Model") || text.Text.Contains("性能计数器可用") || text.Text.Contains("Processor Information"));
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle); Dispatcher.PushFrame(frame);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
