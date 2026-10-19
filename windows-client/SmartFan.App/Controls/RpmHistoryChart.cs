using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartFan.App.Controls;

/// <summary>Five-minute notification history. Missing notifications and disconnections are rendered as gaps.</summary>
public sealed class RpmHistoryChart : FrameworkElement
{
    private readonly List<(DateTimeOffset Time, uint? Rpm)> _samples = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public RpmHistoryChart()
    {
        MinHeight = 190;
        ClipToBounds = true;
        SetResourceReference(TextElement.FontFamilyProperty, "UiFontFamily");
        SetResourceReference(TextElement.FontWeightProperty, "UiBodyWeight");
        _timer.Tick += (_, _) => { Trim(DateTimeOffset.UtcNow); InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void AddSample(DateTimeOffset timestamp, uint rpm)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddSample(timestamp, rpm)); return; }
        if (_samples.Count > 0 && timestamp < _samples[^1].Time) return;
        _samples.Add((timestamp, rpm)); Trim(DateTimeOffset.UtcNow); InvalidateVisual();
    }

    public void MarkGap(DateTimeOffset timestamp)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => MarkGap(timestamp)); return; }
        if (_samples.Count == 0 || timestamp >= _samples[^1].Time) _samples.Add((timestamp, null));
        InvalidateVisual();
    }

    public void Clear()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Clear); return; }
        _samples.Clear(); InvalidateVisual();
    }

    private void Trim(DateTimeOffset now)
    {
        var before = now - Window - TimeSpan.FromSeconds(10);
        _samples.RemoveAll(sample => sample.Time < before);
        if (_samples.Count > 4096) _samples.RemoveRange(0, _samples.Count - 4096);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 90 || ActualHeight < 60) return;
        var foreground = ControlUi.Brush(this, "MutedBrush", Color.FromRgb(146, 162, 183));
        var grid = ControlUi.Brush(this, "BorderBrush", Color.FromRgb(52, 65, 87));
        var accent = ControlUi.Brush(this, "AccentBrush", Color.FromRgb(34, 211, 238));
        const double left = 58, top = 24, right = 16, bottom = 30;
        var width = Math.Max(1, ActualWidth - left - right);
        var height = Math.Max(1, ActualHeight - top - bottom);
        var now = DateTimeOffset.UtcNow;
        var start = now - Window;
        var visible = _samples.Where(sample => sample.Time >= start && sample.Time <= now).ToArray();
        var high = visible.Where(sample => sample.Rpm.HasValue).Select(sample => (double)sample.Rpm.GetValueOrDefault()).DefaultIfEmpty(0).Max();
        var max = Math.Max(1000, Math.Ceiling(high * 1.15 / 500) * 500);
        DrawText(dc, "RPM · 最近 5 分钟", new Point(left, 0), foreground, 11);
        var verticalDivisions = height < 100 ? 2 : 4;
        for (var i = 0; i <= verticalDivisions; i++)
        {
            var y = top + height * i / verticalDivisions;
            dc.DrawLine(new Pen(grid, 1), new Point(left, y), new Point(left + width, y));
            DrawText(dc, (max * (1 - (double)i / verticalDivisions)).ToString("0", CultureInfo.InvariantCulture), new Point(0, y - 7), foreground, 10);
        }
        var timeDivisions = Math.Clamp((int)(width / 78), 1, 5);
        for (var i = 0; i <= timeDivisions; i++)
        {
            var x = left + width * i / timeDivisions;
            DrawText(dc, start.AddTicks(Window.Ticks * i / timeDivisions).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), new Point(Math.Clamp(x - 22, 0, ActualWidth - 48), top + height + 9), foreground, 10);
        }
        Point? previous = null; DateTimeOffset? previousTime = null;
        var pen = new Pen(accent, 2);
        foreach (var sample in visible)
        {
            if (!sample.Rpm.HasValue) { previous = null; previousTime = null; continue; }
            var point = new Point(left + (sample.Time - start).TotalMilliseconds / Window.TotalMilliseconds * width, top + height * (1 - sample.Rpm.Value / max));
            if (previous.HasValue && previousTime.HasValue && sample.Time - previousTime.Value <= TimeSpan.FromSeconds(5)) dc.DrawLine(pen, previous.Value, point);
            dc.DrawEllipse(accent, null, point, 1.8, 1.8);
            previous = point; previousTime = sample.Time;
        }
        if (!visible.Any(sample => sample.Rpm.HasValue)) DrawText(dc, "等待设备转速通知", new Point(left + Math.Max(0, width / 2 - (width < 180 ? 44 : 65)), top + height / 2 - 9), foreground, width < 180 ? 11 : 13);
    }

    private void DrawText(DrawingContext dc, string value, Point position, Brush brush, double size)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, TextElement.GetFontWeight(this), FontStretches.Normal);
        dc.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), position);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.FontFamilyProperty || e.Property == TextElement.FontWeightProperty) InvalidateVisual();
    }
}
