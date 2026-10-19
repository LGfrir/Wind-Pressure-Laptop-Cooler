using SmartFan.App.Models;

namespace SmartFan.App.Views;

/// <summary>Local editor recovery only; null Draft explicitly discards a saved recovery draft.</summary>
public sealed class DashboardDraftChangedEventArgs(DashboardLayoutSettings baseline, DashboardLayoutSettings? draft) : EventArgs
{
    public DashboardLayoutSettings Baseline { get; } = baseline.Clone();
    public DashboardLayoutSettings? Draft { get; } = draft?.Clone();
}
