using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartFan.App.Controls;

/// <summary>Retained, data-driven gauge. It repaints only when a value, theme, or size changes.</summary>
public sealed class DashboardGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(DashboardGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, UpdateFraction));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(DashboardGauge), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender, UpdateFraction));
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(nameof(AutoScale), typeof(bool), typeof(DashboardGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, UpdateFraction));
    public static readonly DependencyProperty ShowScaleNoteProperty = DependencyProperty.Register(nameof(ShowScaleNote), typeof(bool), typeof(DashboardGauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(DashboardGauge), new FrameworkPropertyMetadata("%", FrameworkPropertyMetadataOptions.AffectsRender, RefreshTooltip));
    public static readonly DependencyProperty TotalValueProperty = DependencyProperty.Register(nameof(TotalValue), typeof(double?), typeof(DashboardGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, UpdateFraction));
    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(DashboardGauge), new FrameworkPropertyMetadata("N0", FrameworkPropertyMetadataOptions.AffectsRender, RefreshTooltip));
    public static readonly DependencyProperty MetadataProperty = DependencyProperty.Register(nameof(Metadata), typeof(DashboardMetricContext), typeof(DashboardGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, RefreshTooltip));
    public static readonly DependencyProperty UnavailableTextProperty = DependencyProperty.Register(nameof(UnavailableText), typeof(string), typeof(DashboardGauge), new FrameworkPropertyMetadata("传感器不可用", FrameworkPropertyMetadataOptions.AffectsRender, RefreshTooltip));
    private static readonly DependencyProperty ArcFractionProperty = DependencyProperty.Register("ArcFraction", typeof(double), typeof(DashboardGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public DashboardMetricContext? Metadata { get => (DashboardMetricContext?)GetValue(MetadataProperty); set => SetValue(MetadataProperty, value); }
    public string UnavailableText { get => (string)GetValue(UnavailableTextProperty); set => SetValue(UnavailableTextProperty, value); }
    public double DisplayedFraction => (double)GetValue(ArcFractionProperty);
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public bool ShowScaleNote { get => (bool)GetValue(ShowScaleNoteProperty); set => SetValue(ShowScaleNoteProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double? TotalValue { get => (double?)GetValue(TotalValueProperty); set => SetValue(TotalValueProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    private StreamGeometry? _track;
    private double _trackRadius;
    private Point _trackCenter;
    private int _animationVersion;
    public DashboardGauge()
    {
        MinWidth = 100; MinHeight = 110;
        SetResourceReference(ThemeProbeProperty, "TextBrush");
        SetResourceReference(TextElement.FontFamilyProperty, "UiFontFamily");
        SetResourceReference(TextElement.FontWeightProperty, "UiMetricWeight");
        Unloaded += (_, _) => { ++_animationVersion; BeginAnimation(ArcFractionProperty, null); SetValue(ArcFractionProperty, Fraction); };
    }
    private static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.Register("ThemeProbe", typeof(Brush), typeof(DashboardGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double DisplayMaximum => AutoScale
        ? Math.Max(1000, Math.Ceiling((Value is { } value && double.IsFinite(value) ? Math.Max(0, value) : 0) * 1.15 / 500) * 500)
        : TotalValue is > 0 && double.IsFinite(TotalValue.Value) ? TotalValue.Value : double.IsFinite(Maximum) && Maximum > 0 ? Maximum : 100;
    private double Fraction => Value is { } value && double.IsFinite(value) && value >= 0 ? Math.Clamp(value / DisplayMaximum, 0, 1) : 0;
    private static void UpdateFraction(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (DashboardGauge)owner; var previous = gauge.DisplayedFraction; var next = gauge.Fraction; var version = ++gauge._animationVersion;
        gauge.BeginAnimation(ArcFractionProperty, null); gauge.SetValue(ArcFractionProperty, next);
        if (gauge.IsLoaded && gauge.IsVisible && PresentationSource.FromVisual(gauge) is not null && SystemParameters.ClientAreaAnimation && Math.Abs(previous - next) > .00001)
        {
            var animation = new DoubleAnimation(previous, next, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (version == gauge._animationVersion) gauge.BeginAnimation(ArcFractionProperty, null); };
            gauge.BeginAnimation(ArcFractionProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        RefreshTooltip(owner, e);
    }
    private static void RefreshTooltip(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (DashboardGauge)owner;
        var reading = gauge.Value is { } value && double.IsFinite(value) && value >= 0 ? $"{value.ToString(gauge.ValueFormat, CultureInfo.CurrentCulture)} {gauge.Unit}" : gauge.UnavailableText;
        gauge.ToolTip = string.Join("\n", new[] { reading, gauge.Metadata?.DeviceName, gauge.Metadata?.SourceName, gauge.Metadata?.Status }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (ActualWidth < 40 || ActualHeight < 24) return;
        var foreground = ControlUi.Brush(this, "TextBrush", Colors.White); var muted = ControlUi.Brush(this, "MutedBrush", Colors.Gray);
        var accent = ControlUi.Brush(this, "AccentBrush", Color.FromRgb(139, 124, 255)); var border = ControlUi.Brush(this, "BorderBrush", Colors.DimGray);
        var available = Value.HasValue && double.IsFinite(Value.Value) && Value >= 0;
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, TextElement.GetFontWeight(this), FontStretches.Normal);
        if (ActualHeight < 95)
        {
            var smallRadius = Math.Min(20, ActualHeight / 2 - 4); var smallCenter = new Point(smallRadius + 4, ActualHeight / 2);
            dc.DrawGeometry(null, new Pen(border, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(smallCenter, smallRadius, 240));
            var fraction = available ? DisplayedFraction : 0;
            if (fraction > 0) dc.DrawGeometry(null, new Pen(accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(smallCenter, smallRadius, fraction * 240));
            var textLeft = smallRadius * 2 + 10;
            var textWidth = Math.Max(1, ActualWidth - textLeft - 2);
            var textX = textLeft + textWidth / 2;
            var value = available ? Value.GetValueOrDefault().ToString(ValueFormat, CultureInfo.CurrentCulture) : "—";
            if (ActualHeight < 42)
            {
                var line = $"{value} {(available ? Unit : "不可用")}";
                Text(dc, line, textX, ActualHeight / 2 - 9, foreground, FitSize(line, textWidth, 13, face), face, textWidth);
            }
            else
            {
                Text(dc, value, textX, ActualHeight / 2 - 20, foreground, FitSize(value, textWidth, 20, face), face, textWidth);
                Text(dc, available ? Unit : "不可用", textX, ActualHeight / 2 + 7, available ? muted : foreground, 10, face, textWidth);
            }
            return;
        }
        var spacious = ActualWidth >= 240 && ActualHeight >= 260;
        var points = Metadata?.History ?? Array.Empty<DashboardTrendPoint>();
        var hasTrend = spacious && points.Count(point => point.Value is { } value && double.IsFinite(value)) >= 2;
        var noteHeight = (ShowScaleNote ? 20 : 0) + (hasTrend ? 47 : 0);
        var radius = Math.Max(12, Math.Min(ActualWidth / 2 - 12, (ActualHeight - noteHeight) / 2 - 9));
        var center = new Point(ActualWidth / 2, (ActualHeight - noteHeight) / 2);
        if (_track is null || radius != _trackRadius || center != _trackCenter) { _track = Arc(center, radius, 240); _trackRadius = radius; _trackCenter = center; }
        dc.DrawGeometry(null, new Pen(border, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, _track);
        if (available)
        {
            var fraction = DisplayedFraction;
            if (fraction > 0) dc.DrawGeometry(null, new Pen(accent, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, fraction * 240));
        }
        var numberSize = radius > 95 ? 40 : radius > 60 ? 30 : 23;
        Text(dc, available ? Value.GetValueOrDefault().ToString(ValueFormat, CultureInfo.CurrentCulture) : "—", center.X, center.Y - numberSize * .75, foreground, numberSize, face);
        Text(dc, available ? Unit : "不可用", center.X, center.Y + 15, available ? muted : foreground, 10, face);
        var footerTop = ActualHeight - noteHeight;
        if (ShowScaleNote) { Text(dc, "刻度自动适配", center.X, footerTop + 2, muted, 9, face); footerTop += 20; }
        if (hasTrend)
        {
            DashboardMiniTrend.Draw(dc, points, new Rect(8, footerTop + 3, ActualWidth - 16, 27), accent);
            var peak = points.Where(point => point.Value is { } value && double.IsFinite(value)).Max(point => point.Value!.Value);
            Text(dc, $"最近峰值 {peak.ToString(ValueFormat, CultureInfo.CurrentCulture)} {Unit}", center.X, footerTop + 31, foreground, 9, face); footerTop += 47;
        }
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.FontFamilyProperty || e.Property == TextElement.FontWeightProperty) InvalidateVisual();
    }
    private static StreamGeometry Arc(Point center, double radius, double sweep)
    {
        static Point At(Point c, double r, double degrees) { var angle = degrees * Math.PI / 180; return new Point(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle)); }
        var geometry = new StreamGeometry(); using (var drawing = geometry.Open())
        { drawing.BeginFigure(At(center, radius, 150), false, false); drawing.ArcTo(At(center, radius, 150 + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false); }
        geometry.Freeze(); return geometry;
    }
    private double FitSize(string text, double width, double preferred, Typeface face)
    {
        var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, preferred, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Clamp(preferred * width / Math.Max(1, measured.WidthIncludingTrailingWhitespace), 9, preferred);
    }
    private void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size, Typeface face, double? availableWidth = null)
    { var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, availableWidth ?? ActualWidth - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center }; dc.DrawText(formatted, new Point(x - formatted.MaxTextWidth / 2, y)); }
}
