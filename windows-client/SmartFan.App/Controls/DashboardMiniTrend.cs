using System.Windows;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SmartFan.App.Controls;

public sealed record DashboardTrendPoint(DateTimeOffset Timestamp, double? Value);
public sealed record DashboardMetricContext(string DeviceName, string SourceName, string Status, IReadOnlyList<DashboardTrendPoint> History);

internal sealed class DashboardMetricContextConverter(string key) : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is IReadOnlyDictionary<string, DashboardMetricContext> contexts && contexts.TryGetValue(key, out var context) ? context : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A bounded, retained trend. Missing samples break the line rather than becoming zero.</summary>
public sealed class DashboardMiniTrend : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<DashboardTrendPoint>), typeof(DashboardMiniTrend), new FrameworkPropertyMetadata(Array.Empty<DashboardTrendPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<DashboardTrendPoint> Points { get => (IReadOnlyList<DashboardTrendPoint>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    private static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.Register("ThemeProbe", typeof(Brush), typeof(DashboardMiniTrend), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public DashboardMiniTrend() { MinHeight = 24; SetResourceReference(ThemeProbeProperty, "AccentBrush"); IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Draw(dc, Points, new Rect(RenderSize), ControlUi.Brush(this, "AccentBrush", Color.FromRgb(139, 124, 255)));
    }
    internal static void Draw(DrawingContext dc, IReadOnlyList<DashboardTrendPoint> source, Rect bounds, Brush brush)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        // The presentation model already bounds history. Keep the same complete time window
        // used by its peak label and tooltip rather than silently dropping older plotted samples.
        var points = source;
        var values = points.Where(point => point.Value is { } value && double.IsFinite(value)).Select(point => point.Value!.Value).ToArray();
        if (values.Length < 2 || points.Count < 2) return;
        var min = values.Min(); var max = values.Max(); var span = max - min;
        if (span < .001) { min -= .5; max += .5; span = 1; }
        else { min -= span * .12; max += span * .12; span = max - min; }
        var first = points[0].Timestamp; var seconds = Math.Max(.001, (points[^1].Timestamp - first).TotalSeconds);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var connected = false;
            foreach (var point in points)
            {
                if (point.Value is not { } value || !double.IsFinite(value)) { connected = false; continue; }
                var next = new Point(bounds.Left + Math.Clamp((point.Timestamp - first).TotalSeconds / seconds, 0, 1) * bounds.Width, bounds.Bottom - 2 - (value - min) / span * (bounds.Height - 4));
                if (connected) path.LineTo(next, true, false); else { path.BeginFigure(next, false, false); connected = true; }
            }
        }
        geometry.Freeze();
        var pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geometry);
    }
}

