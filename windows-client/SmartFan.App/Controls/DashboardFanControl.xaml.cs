using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SmartFan.App.Models;

namespace SmartFan.App.Controls;
public partial class DashboardFanControl : UserControl
{
    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsCompact), typeof(bool), typeof(DashboardFanControl), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;
    public bool IsCompact => (bool)GetValue(IsCompactProperty);
    private bool? _compactApplied;
    private Size _availableSize = new(double.PositiveInfinity, double.PositiveInfinity);
    public static readonly RoutedEvent OpenCurveRequestedEvent = EventManager.RegisterRoutedEvent(nameof(OpenCurveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardFanControl));
    public event RoutedEventHandler OpenCurveRequested { add => AddHandler(OpenCurveRequestedEvent, value); remove => RemoveHandler(OpenCurveRequestedEvent, value); }
    public DashboardFanControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdatePresentation(_availableSize);
    }
    protected override Size MeasureOverride(Size constraint)
    {
        // The parent clips the body to a finite slot. A regular child can otherwise retain a
        // larger desired/actual height, so select the compact layout before measuring its children.
        _availableSize = constraint; UpdatePresentation(constraint);
        return base.MeasureOverride(constraint);
    }
    private void UpdatePresentation(Size available)
    {
        var height = double.IsFinite(available.Height) ? available.Height : ActualHeight;
        var width = double.IsFinite(available.Width) ? available.Width : ActualWidth;
        var compact = height is >= 0 and < 145;
        var visibility = height < 235 ? Visibility.Collapsed : Visibility.Visible;
        ManualHint.Visibility = visibility; AutomaticHint.Visibility = visibility;
        if (_compactApplied != compact)
        {
            _compactApplied = compact; SetValue(IsCompactPropertyKey, compact);
            Grid.SetColumnSpan(ModePicker, compact ? 1 : 2);
            Grid.SetRow(ApplyButton, compact ? 0 : 2); Grid.SetColumn(ApplyButton, compact ? 1 : 0); Grid.SetColumnSpan(ApplyButton, compact ? 1 : 2);
            ModeContent.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 7, 0, 7);
            ApplyButton.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0);
            ApplyButton.Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(12, 7, 12, 7);
            ActionStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            if (compact)
            {
                ModePicker.FontSize = ApplyButton.FontSize = 11;
                ModePicker.MinHeight = ApplyButton.MinHeight = 31;
            }
            else
            {
                ModePicker.ClearValue(FontSizeProperty); ApplyButton.ClearValue(FontSizeProperty);
                ModePicker.ClearValue(MinHeightProperty); ApplyButton.ClearValue(MinHeightProperty);
            }
            ManualPanel.Orientation = AutomaticPanel.Orientation = FullSpeedPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            ManualReadout.Width = compact ? 76 : double.NaN;
            ManualLabel.Text = compact ? "PWM" : "手动 PWM"; ManualLabel.FontSize = compact ? 10 : 11;
            ManualValue.FontSize = compact ? 13 : 17;
            ManualSlider.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 4, 0, 0);
            AutomaticTemperature.FontSize = compact ? 11 : 12;
            AutomaticTemperature.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 5);
            AutomaticTemperature.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            AutomaticTemperature.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            CurveHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            CurveRow.Margin = compact ? new Thickness(0) : new Thickness(0, 3, 0, 0);
            CurveRow.Width = compact ? 88 : double.NaN;
            CurveButton.Width = compact ? 80 : double.NaN;
            CurveButton.Margin = new Thickness(compact ? 8 : 9, 0, 0, 0); CurveButton.MinHeight = compact ? 31 : 0;
            FullSpeedTitle.Text = compact ? "全速" : "全速运行"; FullSpeedTitle.FontSize = compact ? 14 : 23;
            FullSpeedTitle.Width = compact ? 36 : double.NaN;
            FullSpeedTitle.Margin = compact ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 6);
            FullSpeedHint.Text = compact ? "应用后请求 ESP32 全速" : "应用后请求 ESP32 切换至全速模式。";
            FullSpeedHint.FontSize = compact ? 10 : 11;
            FullSpeedHint.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            FullSpeedHint.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        }
        ManualSlider.Width = compact ? Math.Max(0, width - 84) : double.NaN;
        AutomaticTemperature.Width = compact ? Math.Max(0, width - 88) : double.NaN;
        FullSpeedHint.Width = compact ? Math.Max(0, width - 44) : double.NaN;
    }
    private void OpenCurveClicked(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(OpenCurveRequestedEvent, this));
}

/// <summary>Summarizes local automatic-mode input without evaluating or uploading a control curve.</summary>
public sealed class FanTemperatureSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var source = (values.ElementAtOrDefault(0) as DeviceProfile)?.TemperatureSource ?? TemperatureSource.Highest;
        var label = source switch { TemperatureSource.Cpu => "CPU 温度", TemperatureSource.Gpu => "GPU 温度", _ => "CPU / GPU 较高温度" };
        var compact = values.ElementAtOrDefault(6) is true;
        if (values.ElementAtOrDefault(3) is true) return compact ? "演示预览 · 不参与调速" : $"本机预览 · {label}\n演示数据不参与真实调速";
        if (values.ElementAtOrDefault(5) is true) return compact ? "本机预览 · 温度已过期" : $"本机预览 · {label}\n温度数据已过期，等待后台更新";
        double? Finite(object? value) => value is double d && double.IsFinite(d) ? d : null;
        var cpu = Finite(values.ElementAtOrDefault(1)); var gpu = Finite(values.ElementAtOrDefault(2));
        double? temperature = source switch { TemperatureSource.Cpu => cpu, TemperatureSource.Gpu => gpu, _ when cpu.HasValue && gpu.HasValue => Math.Max(cpu.Value, gpu.Value), _ => cpu ?? gpu };
        if (!temperature.HasValue) return compact ? "本机预览 · 温度不可用" : $"本机预览 · {label}\n当前温度不可用";
        bool fahrenheit = Equals(values.ElementAtOrDefault(4), "F");
        var display = fahrenheit ? temperature.Value * 1.8 + 32 : temperature.Value;
        var number = $"{display.ToString("0.0", culture)} {(fahrenheit ? "°F" : "°C")}";
        if (compact) return $"本机预览 · {(source == TemperatureSource.Cpu ? "CPU" : source == TemperatureSource.Gpu ? "GPU" : "较高")} {number}";
        return $"本机预览 · {label}\n当前 {number}";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
