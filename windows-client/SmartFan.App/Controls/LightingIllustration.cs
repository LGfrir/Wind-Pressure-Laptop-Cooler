using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SmartFan.App.Controls;

/// <summary>Explains an effect's rhythm, rather than claiming to emulate the connected LED hardware.</summary>
internal sealed class LightingIllustration : FrameworkElement
{
    private readonly Stopwatch _clock = new();
    private bool _subscribed;
    private TimeSpan? _lastCompositionTime;
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly Dictionary<(Color Color, double Width), Pen> _pens = new();
    private readonly Dictionary<(string Text, double Size, Brush Brush, double Dpi), FormattedText> _text = new();
    private static readonly Color[] RandomPalette = [Color.FromRgb(255, 99, 147), Color.FromRgb(117, 104, 255), Color.FromRgb(63, 216, 196), Color.FromRgb(255, 193, 83)];
    private static readonly LinearGradientBrush Spectrum = Freeze(ColorInput.Spectrum());
    private static readonly Pen WhiteStop = Freeze(new Pen(Brushes.White, 1.5));
    private StreamGeometry? _wave;
    private double _waveWidth;
    private Color[] _blendColors = [];
    private LinearGradientBrush? _blend;
    private Pen? _trackPen;
    public int Mode { get; init; }
    public Func<Color[]> Colors { get; init; } = () => [];
    public Func<double> Duration { get; init; } = () => 1000;
    public Func<double[]> Segments { get; init; } = () => [];
    public Func<double> Range { get; init; } = () => 4;
    public Func<double> InnerRange { get; init; } = () => 1;
    public Func<double> Speed { get; init; } = () => 3;
    public Func<double> Wait { get; init; } = () => 500;
    public Func<bool> Continuous { get; init; } = () => true;

    public LightingIllustration()
    {
        Height = 142; ClipToBounds = true; IsHitTestVisible = false;
        SetResourceReference(TextElement.FontFamilyProperty, "UiFontFamily");
        SetResourceReference(TextElement.FontWeightProperty, "UiBodyWeight");
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged; UpdateSubscription(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged; StopRendering(); };
        IsVisibleChanged += (_, _) => { UpdateSubscription(); if (IsVisible) InvalidateVisual(); };
    }

    public void RefreshConfiguration() { if (IsVisible || !IsLoaded) InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != TextElement.FontFamilyProperty && e.Property != TextElement.FontWeightProperty) return;
        _text.Clear();
        InvalidateVisual();
    }
    private void AnimationPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) { UpdateSubscription(); InvalidateVisual(); } }
    private void UpdateSubscription()
    {
        if (!IsLoaded || !IsVisible || Mode is 0 or 1 || !SystemParameters.ClientAreaAnimation) { StopRendering(); return; }
        if (_subscribed) return;
        _subscribed = true; _lastCompositionTime = null; _clock.Start(); CompositionTarget.Rendering += OnCompositionRendering;
    }
    private void StopRendering()
    {
        if (_subscribed) CompositionTarget.Rendering -= OnCompositionRendering;
        _subscribed = false; _lastCompositionTime = null; _clock.Stop();
    }
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || _lastCompositionTime == frame.RenderingTime) return;
        _lastCompositionTime = frame.RenderingTime;
        if (!IsVisible || !IsInsideViewport()) return;
        InvalidateVisual();
    }
    private bool IsInsideViewport()
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            var bounds = TransformToAncestor(scroll).TransformBounds(new Rect(RenderSize));
            return bounds.IntersectsWith(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
        }
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 40) return;
        Color[] colors;
        try { colors = Colors(); } catch (ArgumentException) { colors = []; }
        if (colors.Length == 0) colors = [Color.FromRgb(117, 104, 255), Color.FromRgb(43, 210, 207)];
        var muted = ControlUi.Brush(this, "MutedBrush", Color.FromRgb(146, 162, 183));
        var track = ControlUi.Brush(this, "BorderBrush", Color.FromRgb(58, 64, 86));
        var width = Math.Max(1, ActualWidth - 40); const double x = 20, y = 56;
        var seconds = _clock.Elapsed.TotalSeconds;
        var duration = 1000d;
        try { duration = Math.Clamp(Duration(), 1, 65535); } catch (ArgumentException) { }
        var phase = seconds / Math.Max(.3, duration / 1000);
        Text(dc, "节奏示意", new Point(x, 12), muted, 11);
        Text(dc, "以设备实际效果为准", new Point(Math.Max(x, ActualWidth - 140), 12), muted, 10);
        switch (Mode)
        {
            case 0:
            case 1:
                DrawDots(dc, colors, x, y, width, Mode == 0 ? .12 : 1, 0); break;
            case 2:
                dc.DrawRoundedRectangle(Spectrum, null, new Rect(x, y, width, 22), 11, 11);
                var markerX = x + width * (phase % 1); dc.DrawEllipse(Brushes.White, null, new Point(markerX, y + 11), 4, 4);
                Text(dc, "光谱连续过渡", new Point(x, 103), muted, 11); break;
            case 3:
                if (_wave is null || _waveWidth != width)
                {
                    _wave = new StreamGeometry(); _waveWidth = width;
                    using (var ctx = _wave.Open())
                    {
                        for (var i = 0; i <= 100; i++)
                        {
                            var p = new Point(x + width * i / 100, y + 25 - 21 * Math.Cos(i / 100d * Math.PI * 4));
                            if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
                        }
                    }
                    _wave.Freeze();
                }
                dc.DrawGeometry(null, ColorPen(colors[0], 3), _wave);
                var progress = phase % 2 / 2;
                dc.DrawEllipse(Brushes.White, null, new Point(x + width * progress, y + 25 - 21 * Math.Cos(progress * Math.PI * 4)), 4, 4);
                Text(dc, "变暗  →  变亮  →  循环", new Point(x, 111), muted, 11); break;
            case 4:
                DrawDots(dc, RandomPalette, x, y, width, Continuous() || (int)(seconds * 3) % 2 == 0 ? 1 : .06, (int)(seconds * 3));
                Text(dc, Continuous() ? "连续产生随机颜色" : "随机色与熄灭交替", new Point(x, 103), muted, 11); break;
            case 5:
                for (var i = 0; i < 2; i++)
                {
                    dc.PushOpacity((int)phase % 2 == i ? 1 : .22);
                    dc.DrawRoundedRectangle(ColorBrush(colors[i % colors.Length]), null, new Rect(x + i * (width / 2 + 5), y, width / 2 - 5, 29), 10, 10); dc.Pop();
                }
                Text(dc, "颜色 A  ⇄  颜色 B", new Point(x, 106), muted, 11); break;
            case 6:
                if (_blend is null || !colors.SequenceEqual(_blendColors))
                {
                    _blendColors = colors.ToArray();
                    _blend = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
                    for (var i = 0; i < colors.Length; i++) _blend.GradientStops.Add(new GradientStop(colors[i], i / (double)colors.Length));
                    _blend.GradientStops.Add(new GradientStop(colors[0], 1)); _blend.Freeze();
                }
                dc.DrawRoundedRectangle(_blend, null, new Rect(x, y, width, 25), 12, 12);
                for (var i = 0; i < colors.Length; i++) dc.DrawEllipse(ColorBrush(colors[i]), WhiteStop, new Point(x + width * i / colors.Length, y + 12.5), 5, 5);
                double[] segments;
                try { segments = Segments(); } catch (ArgumentException) { segments = []; }
                if (segments.Length == colors.Length && segments.All(segment => segment > 0 && double.IsFinite(segment)))
                {
                    var elapsed = seconds * 1000 % segments.Sum(); var selected = 0;
                    while (selected < segments.Length - 1 && elapsed >= segments[selected]) { elapsed -= segments[selected]; selected++; }
                    var progressX = x + width * (selected + elapsed / segments[selected]) / colors.Length;
                    dc.DrawEllipse(Brushes.White, null, new Point(progressX, y + 12.5), 4, 4);
                }
                Text(dc, $"{colors.Length} 个颜色节点 · 按各段时长循环过渡", new Point(x, 105), muted, 11); break;
            case 7: DrawDots(dc, colors, x, y, width, 1, (int)(seconds * 3)); Text(dc, "色彩依次向前流动", new Point(x, 106), muted, 11); break;
            case 8:
                const int count = 18;
                var radius = Safe(Range, 4, .05, 36); var core = Safe(InnerRange, 1, 0, radius);
                var speed = Safe(Speed, 3, 0, 100); var wait = Safe(Wait, 500, 0, 60000) / 1000;
                var travel = count + radius * 2;
                var center = speed == 0 ? count * .35 : Math.Min(travel, (seconds * speed + radius + count * .25) % (travel + wait * speed)) - radius;
                var sub = colors.Length > 1 ? colors[1] : System.Windows.Media.Colors.Black;
                for (var i = 0; i < count; i++)
                {
                    var distance = Math.Abs(center - i);
                    var strength = distance <= core ? 1 : Math.Clamp((radius - distance) / Math.Max(.01, radius - core), 0, 1);
                    var color = Color.FromRgb((byte)(sub.R + (colors[0].R - sub.R) * strength), (byte)(sub.G + (colors[0].G - sub.G) * strength), (byte)(sub.B + (colors[0].B - sub.B) * strength));
                    if (_trackPen is null || _trackPen.Brush != track) _trackPen = new Pen(track, .7);
                    dc.DrawRoundedRectangle(ColorBrush(color), _trackPen, new Rect(x + i * width / count, y, width / count - 4, 28), 5, 5);
                }
                Text(dc, "主色扫过 · 副色填充 · 等待后重复", new Point(x, 106), muted, 11); break;
        }
    }
    private static double Safe(Func<double> value, double fallback, double minimum, double maximum)
    {
        try { var result = value(); return double.IsFinite(result) ? Math.Clamp(result, minimum, maximum) : fallback; }
        catch (ArgumentException) { return fallback; }
    }
    private void DrawDots(DrawingContext dc, Color[] colors, double x, double y, double width, double opacity, int offset)
    {
        const int count = 16; dc.PushOpacity(opacity);
        for (var i = 0; i < count; i++) dc.DrawRoundedRectangle(ColorBrush(colors[Math.Abs(i + offset) % colors.Length]), null, new Rect(x + i * width / count, y, width / count - 4, 27), 5, 5);
        dc.Pop();
    }
    private SolidColorBrush ColorBrush(Color color)
    {
        if (_brushes.TryGetValue(color, out var cached)) return cached;
        if (_brushes.Count > 2048) _brushes.Clear();
        var brush = Freeze(new SolidColorBrush(color)); _brushes.Add(color, brush); return brush;
    }
    private Pen ColorPen(Color color, double width)
    {
        if (_pens.TryGetValue((color, width), out var cached)) return cached;
        if (_pens.Count > 64) _pens.Clear();
        var pen = Freeze(new Pen(ColorBrush(color), width)); _pens.Add((color, width), pen); return pen;
    }
    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
    private void Text(DrawingContext dc, string text, Point point, Brush brush, double size)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!_text.TryGetValue((text, size, brush, dpi), out var formatted))
        {
            if (_text.Count > 64) _text.Clear();
            var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, TextElement.GetFontWeight(this), FontStretches.Normal);
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);
            _text.Add((text, size, brush, dpi), formatted);
        }
        dc.DrawText(formatted, point);
    }
}
