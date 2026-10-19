using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartFan.App.Models;

namespace SmartFan.App.Controls;

public sealed record DashboardWidgetDescriptor(string Key, string Title, IReadOnlyList<string> Metrics);

/// <summary>Explicit composition point for built-in or application-registered widgets; no external assembly loading.</summary>
public interface IDashboardWidgetFactory
{
    DashboardWidgetDescriptor Descriptor { get; }
    FrameworkElement CreateWidget();
}

public static class DashboardWidgetRegistry
{
    private sealed class Factory(DashboardWidgetDescriptor descriptor, Func<FrameworkElement> create) : IDashboardWidgetFactory
    {
        public DashboardWidgetDescriptor Descriptor => descriptor;
        public FrameworkElement CreateWidget() => create();
    }
    private static readonly Dictionary<string, IDashboardWidgetFactory> Factories = new(StringComparer.Ordinal);
    static DashboardWidgetRegistry()
    {
        RegisterTemperature("cpu.temperature", "CPU 温度", nameof(DashboardTelemetryData.CpuTemperature), nameof(DashboardTelemetryData.CpuTemperatureStatus));
        RegisterMetric("cpu.usage", "CPU 使用率", nameof(DashboardTelemetryData.CpuUsage), "%", "0", 100);
        RegisterTemperature("gpu.temperature", "GPU 温度", nameof(DashboardTelemetryData.GpuTemperature), nameof(DashboardTelemetryData.GpuTemperatureStatus));
        RegisterMetric("gpu.usage", "GPU 使用率", nameof(DashboardTelemetryData.GpuUsage), "%", "0", 100);
        RegisterMetric("gpu.clock", "GPU 核心频率", nameof(DashboardTelemetryData.GpuClockMhz), "MHz", "N0", 4000);
        Register(new Factory(new("cpu.clock", "CPU 核心频率", ["cpu.clock"]), () =>
        {
            var tile = Tile("最高核心频率", nameof(DashboardTelemetryData.CpuClockMhz), "MHz", "N0", 6000);
            Bind(tile, DashboardMetricTile.UnavailableTextProperty, nameof(DashboardTelemetryData.CpuClockStatus)); return tile;
        }));
        Register(new Factory(new("cpu.voltage", "CPU 核心电压", ["cpu.voltage"]), () =>
        {
            var tile = Tile("核心电压", nameof(DashboardTelemetryData.CpuVoltage), "V", "0.000", 2);
            tile.SetBinding(DashboardMetricTile.TitleProperty, new Binding(nameof(DashboardTelemetryData.CpuVoltageLabel)) { Mode = BindingMode.OneWay, FallbackValue = "核心电压", TargetNullValue = "核心电压" });
            Bind(tile, DashboardMetricTile.UnavailableTextProperty, nameof(DashboardTelemetryData.CpuVoltageStatus)); return tile;
        }));
        Register(new Factory(new("gpu.memory", "独显显存", ["gpu.memory.used", "gpu.memory.total"]), () =>
        {
            var tile = Tile("显存使用", nameof(DashboardTelemetryData.GpuMemoryUsed), "GiB", "0.0", 0);
            Bind(tile, DashboardMetricTile.TotalValueProperty, nameof(DashboardTelemetryData.GpuMemoryTotal));
            Bind(tile, DashboardMetricTile.UnitProperty, nameof(DashboardTelemetryData.MemoryUnit));
            Bind(tile, DashboardMetricTile.ValueFormatProperty, nameof(DashboardTelemetryData.MemoryValueFormat)); return tile;
        }));
        Register(new Factory(new("cpu.combined", "处理器", ["cpu.temperature", "cpu.usage"]), () => Combined("cpu.temperature", "cpu.usage")));
        Register(new Factory(new("gpu.combined", "显卡", ["gpu.temperature", "gpu.usage"]), () => Combined("gpu.temperature", "gpu.usage")));
        Register(new Factory(new("memory.usage", "系统内存", ["memory.used", "memory.total"]), () =>
        {
            var tile = Tile("内存使用", nameof(DashboardTelemetryData.MemoryUsed), "GiB", "0.0", 0);
            Bind(tile, DashboardMetricTile.TotalValueProperty, nameof(DashboardTelemetryData.MemoryTotal));
            Bind(tile, DashboardMetricTile.UnitProperty, nameof(DashboardTelemetryData.MemoryUnit));
            Bind(tile, DashboardMetricTile.ValueFormatProperty, nameof(DashboardTelemetryData.MemoryValueFormat));
            return tile;
        }));
        Register(new Factory(new("fan.rpm", "底座风扇", ["fan.rpm"]), () =>
        {
            var gauge = new DashboardGauge { AutoScale = true, Unit = "RPM", ShowScaleNote = true, MinWidth = 0, MinHeight = 0 };
            Bind(gauge, DashboardGauge.ValueProperty, nameof(DashboardTelemetryData.FanRpm)); BindContext(gauge, DashboardGauge.MetadataProperty, "fan.rpm"); return gauge;
        }));
        Register(new Factory(new("laptop.fans", "电脑内置风扇", ["laptop.fans"]), () =>
        {
            return new LaptopFanWidget();
        }));
    }
    public static IReadOnlyList<DashboardWidgetDescriptor> Available => Factories.Values.Select(factory => factory.Descriptor).ToArray();
    public static void Register(IDashboardWidgetFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.Descriptor.Key) || !Factories.TryAdd(factory.Descriptor.Key, factory))
            throw new ArgumentException("仪表组件名称不能为空或重复。", nameof(factory));
    }
    public static FrameworkElement Create(string key) => Factories.TryGetValue(key, out var factory) ? factory.CreateWidget() : throw new ArgumentException($"未知仪表组件：{key}", nameof(key));
    public static FrameworkElement Create(string key, DashboardDisplayMode mode)
    {
        var content = Create(key);
        return Present(content, mode);
    }
    private static FrameworkElement Present(FrameworkElement content, DashboardDisplayMode mode)
    {
        if (content is DashboardMetricTile tile)
        {
            if (mode != DashboardDisplayMode.Gauge) { tile.DisplayMode = mode; return tile; }
            var gauge = new DashboardGauge { MinWidth = 0, MinHeight = 0 };
            foreach (var pair in new[]
            {
                (DashboardMetricTile.ValueProperty, DashboardGauge.ValueProperty), (DashboardMetricTile.TotalValueProperty, DashboardGauge.TotalValueProperty),
                (DashboardMetricTile.MaximumProperty, DashboardGauge.MaximumProperty), (DashboardMetricTile.UnitProperty, DashboardGauge.UnitProperty),
                (DashboardMetricTile.ValueFormatProperty, DashboardGauge.ValueFormatProperty), (DashboardMetricTile.AutoScaleProperty, DashboardGauge.AutoScaleProperty),
                (DashboardMetricTile.MetadataProperty, DashboardGauge.MetadataProperty), (DashboardMetricTile.UnavailableTextProperty, DashboardGauge.UnavailableTextProperty)
            })
            {
                var binding = BindingOperations.GetBinding(tile, pair.Item1);
                if (binding is not null) gauge.SetBinding(pair.Item2, binding);
                else gauge.SetValue(pair.Item2, tile.GetValue(pair.Item1));
            }
            var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            var heading = ControlUi.Text(tile.Title, "MutedBrush", 10);
            var titleBinding = BindingOperations.GetBindingBase(tile, DashboardMetricTile.TitleProperty);
            if (titleBinding is not null) heading.SetBinding(TextBlock.TextProperty, titleBinding);
            root.Children.Add(heading); Grid.SetRow(gauge, 1); root.Children.Add(gauge);
            var status = BindingOperations.GetBinding(tile, DashboardMetricTile.UnavailableTextProperty);
            if (status is not null) root.SetBinding(FrameworkElement.ToolTipProperty, status);
            return root;
        }
        if (content is DashboardGauge rpm && mode is DashboardDisplayMode.Number or DashboardDisplayMode.Progress)
        {
            var rpmTile = Tile("ESP32 通知", nameof(DashboardTelemetryData.FanRpm), "RPM", "N0", 1000);
            rpmTile.AutoScale = true; rpmTile.DisplayMode = mode; return rpmTile;
        }
        if (content is Grid grid)
        {
            foreach (var child in grid.Children.OfType<FrameworkElement>().ToArray())
            {
                var replacement = Present(child, mode);
                if (ReferenceEquals(child, replacement)) continue;
                Grid.SetColumn(replacement, Grid.GetColumn(child)); grid.Children.Remove(child); grid.Children.Add(replacement);
            }
        }
        return content;
    }
    private static void RegisterMetric(string key, string title, string path, string unit, string format, double maximum) =>
        Register(new Factory(new(key, title, [key]), () => Tile(title, path, unit, format, maximum)));
    private static void RegisterTemperature(string key, string title, string path, string statusPath) =>
        Register(new Factory(new(key, title, [key]), () =>
        {
            var tile = Tile(title, path, "°C", "0.#", 100);
            Bind(tile, DashboardMetricTile.UnitProperty, nameof(DashboardTelemetryData.TemperatureUnit));
            Bind(tile, DashboardMetricTile.MaximumProperty, nameof(DashboardTelemetryData.TemperatureMaximum));
            Bind(tile, DashboardMetricTile.UnavailableTextProperty, statusPath);
            return tile;
        }));
    private static DashboardMetricTile Tile(string title, string path, string unit, string format, double maximum)
    {
        var tile = new DashboardMetricTile { Title = title, Unit = unit, ValueFormat = format, Maximum = maximum };
        Bind(tile, DashboardMetricTile.ValueProperty, path);
        var key = path switch
        {
            nameof(DashboardTelemetryData.CpuTemperature) => "cpu.temperature", nameof(DashboardTelemetryData.CpuUsage) => "cpu.usage",
            nameof(DashboardTelemetryData.GpuTemperature) => "gpu.temperature", nameof(DashboardTelemetryData.GpuUsage) => "gpu.usage",
            nameof(DashboardTelemetryData.CpuClockMhz) => "cpu.clock", nameof(DashboardTelemetryData.CpuVoltage) => "cpu.voltage",
            nameof(DashboardTelemetryData.GpuClockMhz) => "gpu.clock", nameof(DashboardTelemetryData.GpuMemoryUsed) => "gpu.memory",
            nameof(DashboardTelemetryData.MemoryUsed) => "memory.usage", nameof(DashboardTelemetryData.FanRpm) => "fan.rpm", _ => ""
        };
        BindContext(tile, DashboardMetricTile.MetadataProperty, key); return tile;
    }
    private static void Bind(FrameworkElement element, DependencyProperty property, string path) => element.SetBinding(property, new Binding(path) { Mode = BindingMode.OneWay });
    private static void BindContext(FrameworkElement element, DependencyProperty property, string key) => element.SetBinding(property, new Binding("MetricContext") { Mode = BindingMode.OneWay, Converter = new DashboardMetricContextConverter(key) });
    private static FrameworkElement Combined(string firstKey, string secondKey)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(Create(firstKey)); var second = Create(secondKey); Grid.SetColumn(second, 2); grid.Children.Add(second);
        bool vertical = false;
        grid.SizeChanged += (_, _) =>
        {
            bool next = grid.ActualHeight > grid.ActualWidth * 1.05;
            if (next != vertical)
            {
                vertical = next; grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear();
                if (vertical)
                {
                    grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); grid.RowDefinitions.Add(new RowDefinition());
                }
                else
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                }
                var currentSecond = grid.Children.OfType<FrameworkElement>().Last(); Grid.SetColumn(currentSecond, vertical ? 0 : 2); Grid.SetRow(currentSecond, vertical ? 2 : 0);
            }
            var cellWidth = vertical ? grid.ActualWidth : (grid.ActualWidth - 18) / 2;
            foreach (var tile in grid.Children.OfType<DashboardMetricTile>()) tile.SharedNumberSize = Math.Clamp((cellWidth - 28) / 2.9, 16, grid.ActualHeight >= 200 ? 42 : 32);
        };
        return grid;
    }
}

public sealed class DashboardWidgetHost : ContentControl
{
    public static readonly DependencyProperty MetricsProperty = DependencyProperty.Register(nameof(Metrics), typeof(DashboardTelemetryData), typeof(DashboardWidgetHost), new PropertyMetadata(null, (owner, args) =>
    {
        if (((DashboardWidgetHost)owner).Content is FrameworkElement content) content.DataContext = args.NewValue;
    }));
    public static readonly DependencyProperty WidgetKeyProperty = DependencyProperty.Register(nameof(WidgetKey), typeof(string), typeof(DashboardWidgetHost), new PropertyMetadata("", (owner, args) =>
    {
        var host = (DashboardWidgetHost)owner;
        if (string.IsNullOrWhiteSpace((string)args.NewValue)) { host.Content = null; return; }
        var content = DashboardWidgetRegistry.Create((string)args.NewValue, host.DisplayMode);
        content.DataContext = host.Metrics;
        host.Content = content;
    }));
    public DashboardTelemetryData? Metrics { get => (DashboardTelemetryData?)GetValue(MetricsProperty); set => SetValue(MetricsProperty, value); }
    public string WidgetKey { get => (string)GetValue(WidgetKeyProperty); set => SetValue(WidgetKeyProperty, value); }
    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(nameof(DisplayMode), typeof(DashboardDisplayMode), typeof(DashboardWidgetHost), new PropertyMetadata(DashboardDisplayMode.Automatic, (owner, _) =>
    {
        var host = (DashboardWidgetHost)owner;
        if (string.IsNullOrWhiteSpace(host.WidgetKey)) return;
        var content = DashboardWidgetRegistry.Create(host.WidgetKey, host.DisplayMode); content.DataContext = host.Metrics; host.Content = content;
        if (host.IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var shift = new TranslateTransform(0, 6); content.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }));
    public DashboardDisplayMode DisplayMode { get => (DashboardDisplayMode)GetValue(DisplayModeProperty); set => SetValue(DisplayModeProperty, value); }
    public DashboardWidgetHost() { HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; }
}

public sealed class DashboardMetricTile : UserControl
{
    public static readonly DependencyProperty SharedNumberSizeProperty = DependencyProperty.Register(nameof(SharedNumberSize), typeof(double), typeof(DashboardMetricTile), new PropertyMetadata(0d, Refresh));
    public double SharedNumberSize { get => (double)GetValue(SharedNumberSizeProperty); set => SetValue(SharedNumberSizeProperty, value); }
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(nameof(AutoScale), typeof(bool), typeof(DashboardMetricTile), new PropertyMetadata(false, Refresh));
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(nameof(DisplayMode), typeof(DashboardDisplayMode), typeof(DashboardMetricTile), new PropertyMetadata(DashboardDisplayMode.Automatic, Refresh));
    public DashboardDisplayMode DisplayMode { get => (DashboardDisplayMode)GetValue(DisplayModeProperty); set => SetValue(DisplayModeProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(DashboardMetricTile), new PropertyMetadata(null, Refresh));
    public static readonly DependencyProperty TotalValueProperty = DependencyProperty.Register(nameof(TotalValue), typeof(double?), typeof(DashboardMetricTile), new PropertyMetadata(null, Refresh));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(DashboardMetricTile), new PropertyMetadata("", Refresh));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(DashboardMetricTile), new PropertyMetadata("", Refresh));
    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(DashboardMetricTile), new PropertyMetadata("0", Refresh));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(DashboardMetricTile), new PropertyMetadata(100d, Refresh));
    public static readonly DependencyProperty UnavailableTextProperty = DependencyProperty.Register(nameof(UnavailableText), typeof(string), typeof(DashboardMetricTile), new PropertyMetadata("传感器不可用", Refresh));
    public static readonly DependencyProperty MetadataProperty = DependencyProperty.Register(nameof(Metadata), typeof(DashboardMetricContext), typeof(DashboardMetricTile), new PropertyMetadata(null, Refresh));
    public DashboardMetricContext? Metadata { get => (DashboardMetricContext?)GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double? TotalValue { get => (double?)GetValue(TotalValueProperty); set => SetValue(TotalValueProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string UnavailableText { get => (string)GetValue(UnavailableTextProperty); set => SetValue(UnavailableTextProperty, value); }
    private readonly TextBlock _title = ControlUi.Text("", "MutedBrush", 11);
    private readonly TextBlock _number = ControlUi.Text("—", size: 28);
    private readonly TextBlock _unit = ControlUi.Text("", "MutedBrush", 12);
    private readonly TextBlock _detail = ControlUi.Text("等待数据", "TextBrush", 10);
    private readonly DashboardMiniTrend _trend = new() { Height = 38, Margin = new Thickness(0, 10, 0, 8) };
    private readonly Border _unitBadge = new() { CornerRadius = new CornerRadius(5) };
    private readonly StackPanel _root = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _valueLine = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 4) };
    private readonly Grid _bar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
    private readonly ScaleTransform _progressScale = new(1, 1);
    private double _fraction;
    private int _progressVersion;
    public DashboardMetricTile()
    {
        _root.Children.Add(_title);
        _number.SetResourceReference(TextBlock.FontFamilyProperty, "UiFontFamily");
        _number.SetResourceReference(TextBlock.FontWeightProperty, "UiMetricWeight");
        _detail.MaxHeight = 28; _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        _unitBadge.Child = _unit; _unitBadge.Margin = new Thickness(5, 0, 0, 0); _unitBadge.VerticalAlignment = VerticalAlignment.Center;
        _valueLine.Children.Add(_number); _valueLine.Children.Add(_unitBadge); _root.Children.Add(_valueLine); _root.Children.Add(_detail);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2) }; track.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        _bar.ColumnDefinitions.Add(new ColumnDefinition()); _bar.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumnSpan(track, 2); _bar.Children.Add(track);
        var progress = new Border { Height = 4, CornerRadius = new CornerRadius(2), RenderTransform = _progressScale }; progress.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); _bar.Children.Add(progress);
        _root.Children.Add(_bar); _root.Children.Add(_trend);
        Content = _root; SizeChanged += (_, _) => Update();
        Unloaded += (_, _) => { _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _progressScale.ScaleX = 1; };
        Update();
    }
    private static void Refresh(DependencyObject owner, DependencyPropertyChangedEventArgs e) => ((DashboardMetricTile)owner).Update();
    private void Update()
    {
        if (_title is null) return;
        _title.Text = Title;
        var available = Value.HasValue && double.IsFinite(Value.Value);
        var reason = !string.IsNullOrWhiteSpace(Metadata?.Status) ? Metadata.Status : string.IsNullOrWhiteSpace(UnavailableText) ? "传感器不可用" : UnavailableText;
        _unit.Text = available ? Unit : "不可用";
        _unit.FontSize = available ? 12 : 10;
        _unitBadge.Padding = available ? new Thickness(0) : new Thickness(4, 1, 4, 1);
        _unitBadge.Background = available ? null : ControlUi.Brush(this, "SurfaceAltBrush", Colors.Transparent);
        _unitBadge.ToolTip = available ? null : reason;
        _number.Text = available ? Value.GetValueOrDefault().ToString(ValueFormat, CultureInfo.CurrentCulture) : "—";
        if (SharedNumberSize > 0) _number.FontSize = SharedNumberSize;
        else if (ActualWidth > 0) _number.FontSize = Math.Clamp((ActualWidth - _unit.Text.Length * _unit.FontSize * .7 - 12) / (Math.Max(1, _number.Text.Length) * .62), 16, ActualHeight >= 200 ? 44 : 32);
        var hasTotal = TotalValue is > 0 && double.IsFinite(TotalValue.Value);
        var maximum = hasTotal ? TotalValue.GetValueOrDefault() : Maximum;
        if (AutoScale) maximum = Math.Max(1000, Math.Ceiling((available ? Math.Max(0, Value.GetValueOrDefault()) : 0) * 1.15 / 500) * 500);
        var fraction = available && maximum > 0 ? Math.Clamp(Value.GetValueOrDefault() / maximum, 0, 1) : 0;
        var previousFraction = _fraction * _progressScale.ScaleX;
        var changed = Math.Abs(fraction - _fraction) > .00001; _fraction = fraction;
        _bar.ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star); _bar.ColumnDefinitions[1].Width = new GridLength(1 - fraction, GridUnitType.Star);
        _bar.Visibility = available && maximum > 0 && DisplayMode != DashboardDisplayMode.Number ? Visibility.Visible : Visibility.Collapsed;
        if (changed)
        {
            var version = ++_progressVersion;
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _progressScale.ScaleX = 1;
            if (IsLoaded && IsVisible && PresentationSource.FromVisual(this) is not null && SystemParameters.ClientAreaAnimation && fraction > 0)
            {
                var transition = new DoubleAnimation(previousFraction / fraction, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                transition.Completed += (_, _) => { if (version == _progressVersion) _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); };
                _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, transition, HandoffBehavior.SnapshotAndReplace);
            }
        }
        _bar.Height = DisplayMode == DashboardDisplayMode.Progress ? 8 : 4;
        foreach (var border in _bar.Children.OfType<Border>()) { border.Height = _bar.Height; border.CornerRadius = new CornerRadius(_bar.Height / 2); }
        var compact = ActualHeight is > 0 and < 80;
        var spacious = ActualHeight >= 210 && ActualWidth >= 145;
        _title.Visibility = compact && SharedNumberSize <= 0 ? Visibility.Collapsed : Visibility.Visible;
        _valueLine.Margin = compact ? new Thickness(0) : new Thickness(0, 5, 0, 4);
        _unit.Margin = new Thickness(0); _unit.VerticalAlignment = VerticalAlignment.Center;
        _bar.Margin = new Thickness(0, compact ? 2 : 8, 0, 0);
        if (compact) _number.FontSize = Math.Min(_number.FontSize, Math.Max(16, (ActualHeight - (_title.Visibility == Visibility.Visible ? 15 : 0) - (_bar.Visibility == Visibility.Visible ? _bar.Height + 2 : 0)) / 1.35));
        var points = Metadata?.History ?? Array.Empty<DashboardTrendPoint>();
        var valid = points.Where(point => point.Value is { } value && double.IsFinite(value)).ToArray();
        _trend.Points = points; _trend.Visibility = spacious && valid.Length >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _trend.ToolTip = points.Count > 0 ? $"{points[0].Timestamp:HH:mm:ss} — {points[^1].Timestamp:HH:mm:ss}" : null;
        _detail.Text = !available ? ""
            : hasTotal ? $"共 {TotalValue.GetValueOrDefault().ToString(ValueFormat, CultureInfo.CurrentCulture)} {Unit} · 已用 {fraction:P0}"
            : spacious && valid.Length > 0 ? $"最近峰值 {valid.Max(point => point.Value!.Value).ToString(ValueFormat, CultureInfo.CurrentCulture)} {Unit}"
            : "";
        _detail.Visibility = ActualHeight is > 0 and < 95 || string.IsNullOrWhiteSpace(_detail.Text) ? Visibility.Collapsed : Visibility.Visible;
        _detail.ToolTip = _detail.Text;
        ToolTip = string.Join("\n", new[] { available ? $"{Title} · {_number.Text} {Unit}" : $"{Title} · {reason}", Metadata?.DeviceName, Metadata?.SourceName, Metadata?.Status, !available && UnavailableText != reason ? UnavailableText : null }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }
}
