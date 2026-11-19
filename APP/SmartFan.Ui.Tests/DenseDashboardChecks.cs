using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

/// <summary>Pure WPF layout/render checks for a dense ten-card composition. No settings, HWND or sampler is opened.</summary>
internal static class DenseDashboardChecks
{
    public static void Run(Action<bool, string> check, string? captureDirectory = null)
    {
        // Copy only public widget kinds and geometry from the reported layout; identifiers and data are synthetic.
        var layout = new DashboardLayoutSettings
        {
            Widgets =
            [
                Card("dense-gpu", "gpu.combined", 4, 0, 4, 3),
                Card("dense-ram", "memory.usage", 4, 3, 4, 3),
                Card("dense-fans", "laptop.fans", 0, 0, 4, 3),
                Card("dense-rpm", "fan.rpm", 0, 3, 4, 5),
                Card("dense-control", "fan.control", 8, 3, 5, 5),
                Card("dense-cpu", "cpu.combined", 8, 0, 5, 3),
                Card("dense-voltage", "cpu.voltage", 4, 6, 4, 2),
                Card("dense-vram", "gpu.memory", 13, 0, 3, 3),
                Card("dense-clock", "cpu.clock", 13, 3, 3, 2),
                Card("dense-gpu-clock", "gpu.clock", 13, 5, 3, 3)
            ]
        };
        var environment = new HardwareMonitorDiagnostics { PawnIoInstalled = false, PawnIoDeviceAccessible = false, PawnIoDeviceErrorCode = 2 };
        var clockReason = CpuMetricDiagnostics.Describe("频率", null, 24, environment);
        var voltageReason = CpuMetricDiagnostics.Describe("电压", null, 0, environment);
        var metrics = new DashboardTelemetryData
        {
            CpuTemperature = 64, CpuUsage = 36, GpuTemperature = 61, GpuUsage = 52,
            MemoryUsed = 22.4, MemoryTotal = 48, GpuMemoryUsed = 3, GpuMemoryTotal = 12,
            GpuClockMhz = 2400, CpuClockMhz = null, CpuVoltage = null, FanRpm = 3100,
            CpuClockStatus = clockReason, CpuVoltageStatus = voltageReason,
            LaptopFans = [new("CPU", 1800), new("GPU", 1750), new("辅助", 2400)],
            LaptopFanSummary = "三颗电脑内置风扇 · 离线测试", FreshnessText = "离线合成数据 · 布局检查",
            MetricContext = new Dictionary<string, DashboardMetricContext>
            {
                ["cpu.clock"] = new("测试处理器", "CPU 各核心硬件频率", clockReason, []),
                ["cpu.voltage"] = new("测试处理器", "CPU 核心电压传感器", voltageReason, []),
                ["cpu.temperature"] = new("测试处理器", "ASUS EC CPU Temperature", "离线合成读数", []),
                ["cpu.usage"] = new("测试处理器", "CPU Total", "离线合成读数", []),
                ["gpu.temperature"] = new("测试显卡", "GPU Core", "离线合成读数", []),
                ["gpu.usage"] = new("测试显卡", "GPU Core Load", "离线合成读数", []),
                ["memory.usage"] = new("系统内存", "Windows 物理内存", "离线合成读数", [])
            }
        };
        var view = new DashboardView { Layout = layout, Metrics = metrics, DataContext = new DenseFanState() };
        view.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        check(DashboardLayoutEngine.Validate(layout).Count == 0 && layout.Widgets.Count == 10, "dense ten-card regression uses valid detached synthetic geometry");
        foreach (var size in new[] { new Size(1004, 445), new Size(1368, 620) })
        {
            Layout(view, size); Drain(); Layout(view, size);
            if (captureDirectory is not null && size.Width == 1004)
                Save(view, Path.Combine(captureDirectory, "dashboard-dense-10-missing-metrics.png"));
            var canvas = (Canvas)view.FindName("WidgetCanvas");
            var frames = canvas.Children.OfType<Border>().ToArray();
            var workspace = (FrameworkElement)view.FindName("Workspace");
            var viewport = new Rect(workspace.RenderSize); viewport.Inflate(.5, .5);
            check(frames.Length == 10 && frames.All(frame => frame.ActualWidth > 0 && frame.ActualHeight > 0 &&
                    viewport.Contains(frame.TransformToAncestor(workspace).TransformBounds(new Rect(frame.RenderSize)))),
                $"all ten dense dashboard frames stay within the {size.Width} by {size.Height} viewport");
            check(PresentationSource.FromVisual(view) is null && Window.GetWindow(view) is null,
                "dense dashboard layout checks create no native or visible window");
            CheckMissingMetric(view, "dense-clock", "MHz", clockReason, size, check);
            CheckMissingMetric(view, "dense-voltage", "V", voltageReason, size, check);
            CheckLiveBindings(view, check, size);
            foreach (var count in new[] { 3, 5 })
            {
                var fans = FanSummaryChecks.FiveFanReadings().Take(count).ToArray(); metrics.LaptopFans = fans;
                Layout(view, size); Drain(); Layout(view, size);
                var fanHost = Descendants<DashboardWidgetHost>(Frame(view, "dense-fans")).Single();
                FanSummaryChecks.CheckEveryReading((LaptopFanWidget)fanHost.Content, (FrameworkElement)VisualTreeHelper.GetParent(fanHost),
                    fans, check, $"dense ten-card {size.Width}×{size.Height} fan panel with {count} fans");
            }
            metrics.LaptopFans = FanSummaryChecks.FiveFanReadings().Take(3).ToArray();
        }
        check(layout.Widgets.Single(widget => widget.Id == "dense-clock") is { Width: 3, Height: 2 } &&
              layout.Widgets.Single(widget => widget.Id == "dense-voltage") is { Width: 4, Height: 2 },
            "missing-data layout checks retain the reported compact clock and voltage dimensions");
    }

    private static void CheckMissingMetric(DashboardView view, string id, string physicalUnit, string reason, Size viewport, Action<bool, string> check)
    {
        var frame = Frame(view, id);
        var host = Descendants<DashboardWidgetHost>(frame).Single();
        var body = (FrameworkElement)VisualTreeHelper.GetParent(host);
        var tile = Descendants<DashboardMetricTile>(host).Single();
        var number = Descendants<TextBlock>(tile).Single(text => text.Text == "—");
        var unit = Descendants<TextBlock>(tile).Single(text => text.Text == "不可用");
        var badge = (Border)VisualTreeHelper.GetParent(unit);
        var bounds = new Rect(body.RenderSize); bounds.Inflate(.5, .5);
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(body).TransformBounds(new Rect(element.RenderSize));
        check(tile.Value is null && tile.Unit == physicalUnit && tile.UnavailableText == reason,
            $"{id} retains a missing value, physical unit and real driver diagnostic at {viewport.Width} width");
        check(number.Visibility == Visibility.Visible && unit.Visibility == Visibility.Visible && badge.Visibility == Visibility.Visible &&
                number.ActualHeight > 0 && unit.ActualHeight > 0 && badge.ActualWidth > 0,
            $"{id} shows an actual unavailable badge beside the missing-value mark");
        check(bounds.Contains(Bounds(number)) && bounds.Contains(Bounds(unit)) && bounds.Contains(Bounds(badge)),
            $"{id} keeps missing value and badge inside its clipped body at {viewport.Width} width (body={bounds}, value={Bounds(number)}, badge={Bounds(badge)})");
        foreach (var text in new[] { number, unit })
        {
            var expected = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            check(text.ActualWidth + .5 >= expected.WidthIncludingTrailingWhitespace && text.ActualHeight + .5 >= expected.Height,
                $"{id} allocates complete {text.Text} glyph bounds at {viewport.Width} width (actual={text.ActualWidth:0.##}×{text.ActualHeight:0.##}, expected={expected.WidthIncludingTrailingWhitespace:0.##}×{expected.Height:0.##})");
        }
        check(Convert.ToString(badge.ToolTip, CultureInfo.CurrentCulture) == reason &&
              Convert.ToString(tile.ToolTip, CultureInfo.CurrentCulture)!.Contains(reason, StringComparison.Ordinal),
            $"{id} exposes the actual PawnIO cause through both compact badge and card tooltips");
    }

    private static void CheckLiveBindings(DashboardView view, Action<bool, string> check, Size viewport)
    {
        DashboardMetricTile[] Tiles(string id) => Descendants<DashboardMetricTile>(Frame(view, id)).ToArray();
        var cpu = Tiles("dense-cpu"); var gpu = Tiles("dense-gpu"); var ram = Tiles("dense-ram").Single();
        check(cpu.Length == 2 && cpu.Single(tile => tile.Unit == "°C").Value == 64 && cpu.Single(tile => tile.Unit == "%").Value == 36 &&
              gpu.Length == 2 && gpu.Single(tile => tile.Unit == "°C").Value == 61 && gpu.Single(tile => tile.Unit == "%").Value == 52,
            $"dense CPU and GPU combined cards preserve distinct real temperature and usage bindings at {viewport.Width} width");
        check(ram.Value == 22.4 && ram.TotalValue == 48 && ram.Unit == "GiB",
            "dense memory cards keep their shared usage, capacity and unit bindings");
    }

    private static DashboardWidgetSettings Card(string id, string kind, int column, int row, int width, int height) =>
        new() { Id = id, Kind = kind, Column = column, Row = row, Width = width, Height = height };
    private static Border Frame(DashboardView view, string id) => ((Canvas)view.FindName("WidgetCanvas")).Children.OfType<Border>().Single(frame => Equals(frame.Tag, id));
    private static void Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static void Save(FrameworkElement root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class DenseFanState
    {
        public IReadOnlyList<string> FanModes { get; } = ["手动控制", "设备自动控制", "全速运行"];
        public int FanModeIndex { get; set; }
        public double ManualPwm { get; set; } = 20;
        public bool IsConnected => false;
        public bool Busy => false;
        public bool DemoMode => false;
        public bool TelemetryEnabled => false;
        public bool TelemetryIsStale => false;
        public double CpuTemperatureValue => 64;
        public double GpuTemperatureValue => 61;
        public AppSettings Settings { get; } = new();
        public DeviceProfile SelectedProfile { get; } = new();
        public string ConnectionText => "离线布局检查";
        public string FanControlStatusText => "合成数据 · 未向设备发送设置";
        public System.Windows.Input.ICommand ApplyFanCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
    }
}
