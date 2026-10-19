using System.Windows;
using System.Windows.Controls;
namespace SmartFan.App.Controls;
public partial class LaptopFanWidget : UserControl
{
    private double _availableHeight;
    public static readonly DependencyProperty FanRowHeightProperty = DependencyProperty.Register(nameof(FanRowHeight), typeof(double), typeof(LaptopFanWidget), new PropertyMetadata(30d));
    public static readonly DependencyProperty FanNumberSizeProperty = DependencyProperty.Register(nameof(FanNumberSize), typeof(double), typeof(LaptopFanWidget), new PropertyMetadata(18d));
    public static readonly DependencyProperty FanLabelSizeProperty = DependencyProperty.Register(nameof(FanLabelSize), typeof(double), typeof(LaptopFanWidget), new PropertyMetadata(11d));
    public static readonly DependencyProperty FanUnitSizeProperty = DependencyProperty.Register(nameof(FanUnitSize), typeof(double), typeof(LaptopFanWidget), new PropertyMetadata(10d));
    public double FanRowHeight { get => (double)GetValue(FanRowHeightProperty); private set => SetValue(FanRowHeightProperty, value); }
    public double FanNumberSize { get => (double)GetValue(FanNumberSizeProperty); private set => SetValue(FanNumberSizeProperty, value); }
    public double FanLabelSize { get => (double)GetValue(FanLabelSizeProperty); private set => SetValue(FanLabelSizeProperty, value); }
    public double FanUnitSize { get => (double)GetValue(FanUnitSizeProperty); private set => SetValue(FanUnitSizeProperty, value); }
    public LaptopFanWidget()
    {
        InitializeComponent();
        Fans.ItemContainerGenerator.ItemsChanged += (_, _) => UpdateDensity();
        Loaded += (_, _) => UpdateDensity();
    }
    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Height) && constraint.Height > 0)
        { _availableHeight = constraint.Height; UpdateDensity(); }
        return base.MeasureOverride(constraint);
    }
    private void UpdateDensity()
    {
        if (_availableHeight <= 0) return;
        var count = Fans.Items.Count;
        var footerVisible = count == 0 || _availableHeight >= count * 27 + 22;
        SourceFooter.Visibility = footerVisible ? Visibility.Visible : Visibility.Collapsed;
        var available = Math.Max(0, _availableHeight - (footerVisible ? 22 : 0));
        // Whole-pixel rows also fit when the containing window uses layout rounding.
        FanRowHeight = Math.Min(32, Math.Floor(available / Math.Max(1, count)));
        FanNumberSize = Math.Clamp((FanRowHeight - 1) / Math.Max(1.2, FontFamily.LineSpacing), 11, 18);
        FanLabelSize = Math.Clamp(FanNumberSize * .65, 9, 11);
        FanUnitSize = Math.Clamp(FanNumberSize * .55, 9, 10);
    }
}
