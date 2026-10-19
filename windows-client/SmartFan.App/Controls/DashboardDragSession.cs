using System.Windows;
using SmartFan.App.Models;

namespace SmartFan.App.Controls;

public readonly record struct DashboardGridGeometry(double Width, double Height)
{
    public double PitchX => Math.Max(1, Width / DashboardLayoutEngine.Columns);
    public double PitchY => Math.Max(1, Height / DashboardLayoutEngine.Rows);
    public double Gap => Math.Min(12, Math.Min(PitchX, PitchY) * .2);
    public Rect BoardBounds => new(Gap / 2, Gap / 2, Math.Max(1, Width - Gap), Math.Max(1, Height - Gap));
    public Rect GetBounds(DashboardWidgetSettings item)
    {
        var allocatedWidth = item.Width * PitchX - Gap; var allocatedHeight = item.Height * PitchY - Gap;
        var width = allocatedWidth; var height = allocatedHeight;
        if (item.AspectRatio != DashboardAspectRatio.Free)
        {
            var (w, h) = DashboardLayoutEngine.RatioUnits(item.AspectRatio); var ratio = (double)w / h;
            width = Math.Min(width, height * ratio); height = width / ratio;
        }
        return new Rect(item.Column * PitchX + Gap / 2 + (allocatedWidth - width) / 2,
            item.Row * PitchY + Gap / 2 + (allocatedHeight - height) / 2, Math.Max(1, width), Math.Max(1, height));
    }
}

public readonly record struct DashboardDragUpdate(Rect VisualBounds, bool PlacementChanged, bool CandidateFits);

/// <summary>Continuous pointer geometry is independent of discrete drop placement. A per-gesture
/// cache avoids evaluating the same cell repeatedly; no persistent cache or rendering timer.</summary>
public sealed class DashboardDragSession
{
    private readonly DashboardLayoutSettings _original;
    private readonly DashboardWidgetSettings _item;
    private readonly DashboardGridGeometry _grid;
    private readonly Rect _startVisual;
    private readonly bool _resize;
    private readonly Dictionary<(int Col, int Row, int Width, int Height), DashboardLayoutSettings?> _placements = [];
    private (int Col, int Row, int Width, int Height) _lastCell;
    private bool _lastFit = true;
    public DashboardLayoutSettings PreviewLayout { get; private set; }
    public int PlacementEvaluationCount { get; private set; }
    public DashboardDragSession(DashboardLayoutSettings original, string id, DashboardGridGeometry grid, Rect startVisual, bool resize)
    {
        _original = original.Clone(); _item = _original.Widgets.Single(widget => widget.Id == id);
        _grid = grid; _startVisual = startVisual; _resize = resize;
        PreviewLayout = _original.Clone(); _lastCell = Key(_item); _placements[_lastCell] = PreviewLayout;
    }
    public DashboardDragUpdate Update(Vector delta)
    {
        var candidate = _item.Clone(); var bounds = _grid.BoardBounds; Rect visual;
        if (!_resize)
        {
            visual = new Rect(Math.Clamp(_startVisual.X + delta.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - _startVisual.Width)),
                Math.Clamp(_startVisual.Y + delta.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - _startVisual.Height)), _startVisual.Width, _startVisual.Height);
            candidate.Column = Math.Clamp(_item.Column + (int)Math.Round((visual.X - _startVisual.X) / _grid.PitchX), 0, DashboardLayoutEngine.Columns - _item.Width);
            candidate.Row = Math.Clamp(_item.Row + (int)Math.Round((visual.Y - _startVisual.Y) / _grid.PitchY), 0, DashboardLayoutEngine.Rows - _item.Height);
        }
        else
        {
            var minimum = DashboardLayoutEngine.Minimum(_item.Kind);
            var width = Math.Clamp(_startVisual.Width + delta.X, Math.Min(_startVisual.Width, minimum.Width * _grid.PitchX - _grid.Gap), Math.Max(_startVisual.Width, bounds.Right - _startVisual.Left));
            var height = Math.Clamp(_startVisual.Height + delta.Y, Math.Min(_startVisual.Height, minimum.Height * _grid.PitchY - _grid.Gap), Math.Max(_startVisual.Height, bounds.Bottom - _startVisual.Top));
            bool fromWidth = Math.Abs(delta.X) >= Math.Abs(delta.Y);
            if (_item.AspectRatio != DashboardAspectRatio.Free)
            {
                var factor = fromWidth ? width / _startVisual.Width : height / _startVisual.Height;
                var (w, h) = DashboardLayoutEngine.RatioUnits(_item.AspectRatio);
                var smallest = Math.Max((int)Math.Ceiling((double)minimum.Width / w), (int)Math.Ceiling((double)minimum.Height / h));
                var minimumModel = _item.Clone(); minimumModel.Width = smallest * w; minimumModel.Height = smallest * h;
                var minimumRect = _grid.GetBounds(minimumModel);
                var minScale = Math.Max(minimumRect.Width / _startVisual.Width, minimumRect.Height / _startVisual.Height);
                var maxScale = Math.Min((bounds.Right - _startVisual.Left) / _startVisual.Width, (bounds.Bottom - _startVisual.Top) / _startVisual.Height);
                factor = Math.Clamp(factor, Math.Min(minScale, maxScale), Math.Max(minScale, maxScale));
                width = _startVisual.Width * factor; height = _startVisual.Height * factor;
            }
            visual = new Rect(_startVisual.X, _startVisual.Y, width, height);
            candidate.Width = Math.Clamp(_item.Width + (int)Math.Round((width - _startVisual.Width) / _grid.PitchX), minimum.Width, DashboardLayoutEngine.Columns - _item.Column);
            candidate.Height = Math.Clamp(_item.Height + (int)Math.Round((height - _startVisual.Height) / _grid.PitchY), minimum.Height, DashboardLayoutEngine.Rows - _item.Row);
            if (_item.AspectRatio != DashboardAspectRatio.Free)
            {
                var (w, h) = DashboardLayoutEngine.RatioUnits(_item.AspectRatio);
                var least = Math.Max((int)Math.Ceiling((double)minimum.Width / w), (int)Math.Ceiling((double)minimum.Height / h));
                var most = Math.Min((DashboardLayoutEngine.Columns - _item.Column) / w, (DashboardLayoutEngine.Rows - _item.Row) / h);
                var units = Math.Clamp((int)Math.Round(fromWidth ? (double)candidate.Width / w : (double)candidate.Height / h), least, most);
                candidate.Width = units * w; candidate.Height = units * h;
            }
        }
        var key = Key(candidate);
        if (key == _lastCell) return new(visual, false, _lastFit);
        _lastCell = key;
        if (!_placements.TryGetValue(key, out var plan))
        { PlacementEvaluationCount++; plan = DashboardLayoutEngine.TryPlace(_original, candidate); _placements.Add(key, plan); }
        _lastFit = plan is not null;
        if (plan is null) return new(visual, false, false);
        PreviewLayout = plan; return new(visual, true, true);
    }
    private static (int, int, int, int) Key(DashboardWidgetSettings item) => (item.Column, item.Row, item.Width, item.Height);
}
