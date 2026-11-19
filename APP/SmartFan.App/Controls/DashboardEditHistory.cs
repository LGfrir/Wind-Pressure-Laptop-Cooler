using SmartFan.App.Models;

namespace SmartFan.App.Controls;

/// <summary>Bounded, detached snapshots. A complete pointer gesture is one edit.</summary>
public sealed class DashboardEditHistory
{
    private readonly List<DashboardLayoutSettings> _entries = [];
    private int _index;
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index + 1 < _entries.Count;
    public void Reset(DashboardLayoutSettings initial)
    { _entries.Clear(); _entries.Add(initial.Clone()); _index = 0; }
    public bool Record(DashboardLayoutSettings layout)
    {
        if (_entries.Count == 0) { Reset(layout); return false; }
        if (Equivalent(_entries[_index], layout)) return false;
        if (CanRedo) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(layout.Clone()); _index++;
        if (_entries.Count > 61) { _entries.RemoveAt(0); _index--; }
        return true;
    }
    public DashboardLayoutSettings? Undo() => CanUndo ? _entries[--_index].Clone() : null;
    public DashboardLayoutSettings? Redo() => CanRedo ? _entries[++_index].Clone() : null;
    public static bool Equivalent(DashboardLayoutSettings left, DashboardLayoutSettings right) =>
        left.Widgets.Count == right.Widgets.Count && left.Widgets.Zip(right.Widgets).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind &&
            pair.First.Column == pair.Second.Column && pair.First.Row == pair.Second.Row &&
            pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height &&
            pair.First.DisplayMode == pair.Second.DisplayMode && pair.First.AspectRatio == pair.Second.AspectRatio);
}
