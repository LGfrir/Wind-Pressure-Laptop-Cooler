using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

/// <summary>Bound-source retransfers during editing and delayed saves, without files or devices.</summary>
internal static class WidgetBindingRegressionChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckEquivalentTransfers(check);
        CheckDelayedSave(check, false, false);
        CheckDelayedSave(check, false, true);
        CheckDelayedSave(check, true, false);
        CheckDifferentImports(check);
    }

    private static void CheckEquivalentTransfers(Action<bool, string> check)
    {
        var host = new BindingHost(); var view = CreateBound(host);
        var expression = view.GetBindingExpression(DashboardView.LayoutProperty);
        view.BeginEditing(); MoveCpu(view, 2);
        view.SaveLayoutAsync = proposed => { host.Settings.Dashboard = proposed.Clone(); return Task.FromResult(true); };
        check(view.CommitLayoutAsync().GetAwaiter().GetResult(), "binding regression setup commits an initial detached host layout");
        view.BeginEditing(); MoveCpu(view, 4);
        var before = view.CurrentLayout; var cards = Cards(view);
        host.NotifySettings();
        check(view.IsEditing && Same(view.CurrentLayout, before) && view.CanUndo && Cards(view).SequenceEqual(cards),
            "a Settings notification after a prior save preserves the complete active draft, visuals and undo history");
        for (var index = 0; index < 3; index++)
        { host.Settings.Dashboard = host.Settings.Dashboard.Clone(); host.NotifySettings(); }
        check(view.IsEditing && Same(view.CurrentLayout, before) && host.Settings.Dashboard.Widgets.Single().Column == 2,
            "equivalent baseline clones never overwrite an unsaved move or change the committed source");
        check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Single().Column == 2 && view.CanRedo,
            "undo remains available after a semantically equivalent bound-source retransfer");
        host.Settings.Dashboard = host.Settings.Dashboard.Clone(); host.NotifySettings();
        check(view.IsEditing && view.CanRedo && view.RedoLayoutEdit() && view.CurrentLayout.Widgets.Single().Column == 4,
            "baseline notifications also preserve redo history when the draft currently equals its baseline");
        check(ReferenceEquals(expression, view.GetBindingExpression(DashboardView.LayoutProperty)),
            "equivalent transfers and successful commit retain the original binding expression");
        view.CancelLayoutEditing();
    }

    private static void CheckDelayedSave(Action<bool, string> check, bool success, bool throwOnSave)
    {
        var host = new BindingHost(); var view = CreateBound(host);
        var expression = view.GetBindingExpression(DashboardView.LayoutProperty);
        var baseline = host.Settings.Dashboard.Clone(); var completion = new TaskCompletionSource<bool>();
        view.BeginEditing(); MoveCpu(view, 4); view.TryAddWidget("gpu.usage", 2, 2);
        ((ComboBox)view.FindName("DisplayPicker")).SelectedValue = DashboardDisplayMode.Progress;
        var proposed = view.CurrentLayout; var cards = Cards(view);
        DashboardDraftChangedEventArgs? latestEvent = null; view.DraftChanged += (_, args) => latestEvent = args;
        view.SaveLayoutAsync = async layout =>
        {
            var previous = host.Settings.Dashboard;
            host.Settings.Dashboard = layout.Clone(); host.NotifySettings();
            try
            {
                var saved = await completion.Task;
                if (!saved) host.Settings.Dashboard = previous;
                return saved;
            }
            catch { host.Settings.Dashboard = previous; throw; }
        };
        var pending = view.CommitLayoutAsync();
        check(!pending.IsCompleted && view.IsEditing && Same(view.CurrentLayout, proposed) && Cards(view).SequenceEqual(cards),
            $"a tentative host retransfer during delayed {(success ? "successful" : "failed")} save does not exit or rebuild the editor");
        var tentative = host.Settings.Dashboard;
        host.Settings.Dashboard = baseline.Clone(); host.NotifySettings();
        host.Settings.Dashboard = tentative.Clone(); host.NotifySettings();
        check(view.IsEditing && Same(view.CurrentLayout, proposed) && Same(view.Layout!, tentative),
            "both baseline and tentative-clone notifications are frozen until the current save transaction resolves");
        if (throwOnSave) completion.SetException(new IOException("isolated delayed save failure"));
        else completion.SetResult(success);
        DrainUntil(pending);
        check(pending.GetAwaiter().GetResult() == success && ReferenceEquals(expression, view.GetBindingExpression(DashboardView.LayoutProperty)),
            "delayed save returns its actual result and keeps the bound source expression intact");
        if (success)
        {
            check(!view.IsEditing && Same(view.CurrentLayout, proposed) && Same(view.Layout!, proposed) && Same(host.Settings.Dashboard, proposed) && latestEvent is { Draft: null },
                "successful delayed save coherently commits host, effective layout and visual draft, then clears recovery");
            view.BeginEditing(); MoveCpu(view, 6); var nextDraft = view.CurrentLayout;
            host.Settings.Dashboard = host.Settings.Dashboard.Clone(); host.NotifySettings();
            check(view.IsEditing && view.CanUndo && Same(view.CurrentLayout, nextDraft) && view.UndoLayoutEdit() && Same(view.CurrentLayout, proposed),
                "a fresh session after delayed success uses the new committed baseline and survives source notifications");
        }
        else
        {
            check(view.IsEditing && Same(view.CurrentLayout, proposed) && Same(view.Layout!, baseline) && Same(host.Settings.Dashboard, baseline) && Cards(view).SequenceEqual(cards),
                "failed delayed save restores only the committed bound value, retaining the complete draft and existing card visuals");
            check(((FrameworkElement)view.FindName("Inspector")).IsEnabled && ((FrameworkElement)view.FindName("WidgetCanvas")).IsEnabled && view.CanUndo,
                "failed save re-enables editor controls and retains undo availability");
            host.Settings.Dashboard = host.Settings.Dashboard.Clone(); host.NotifySettings();
            check(view.IsEditing && Same(view.CurrentLayout, proposed), "post-failure baseline notification cannot discard the retained draft");
            check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Single(widget => widget.Kind == "gpu.usage").DisplayMode == DashboardDisplayMode.Automatic,
                "the first undo after failed save restores only the most recent display change");
            check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Count == 1 && view.CurrentLayout.Widgets.Single().Column == 4,
                "the second undo after failed save removes only the added card");
            check(view.UndoLayoutEdit() && Same(view.CurrentLayout, baseline) && !view.CanUndo,
                "the complete pre-save history still reaches the original committed baseline");
        }
        view.CancelLayoutEditing();
    }

    private static void CheckDifferentImports(Action<bool, string> check)
    {
        var host = new BindingHost(); var view = CreateBound(host);
        var imported = new DashboardLayoutSettings { Widgets = [new() { Id = "imported-gpu", Kind = "gpu.usage", Column = 8, Width = 3, Height = 2 }] };
        view.BeginEditing(); MoveCpu(view, 4);
        host.Settings.Dashboard = imported.Clone(); host.NotifySettings();
        check(!view.IsEditing && Same(view.CurrentLayout, imported) && !view.CanUndo,
            "an actual different import outside a save replaces the active session and clears its obsolete undo history");
        var nextImport = new DashboardLayoutSettings { Widgets = [] };
        host.Settings.Dashboard = nextImport; host.NotifySettings();
        check(!view.IsEditing && Same(view.CurrentLayout, nextImport), "an actual different import also replaces an idle dashboard");
    }

    private static DashboardView CreateBound(BindingHost host)
    {
        var view = new DashboardView();
        view.SetBinding(DashboardView.LayoutProperty, new Binding("Settings.Dashboard") { Source = host, Mode = BindingMode.OneWay });
        view.Measure(new Size(1304, 620)); view.Arrange(new Rect(0, 0, 1304, 620)); view.UpdateLayout(); return view;
    }
    private static Border[] Cards(DashboardView view) => ((Canvas)view.FindName("WidgetCanvas")).Children.OfType<Border>().ToArray();
    private static void MoveCpu(DashboardView view, int column)
    { var item = view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu"); item.Column = column; if (!view.TryUpdateWidget(item)) throw new InvalidOperationException("Isolated move setup did not fit."); }
    private static bool Same(DashboardLayoutSettings left, DashboardLayoutSettings right) => DashboardEditHistory.Equivalent(left, right);
    private static void DrainUntil(Task pending)
    {
        if (pending.IsCompleted) return;
        var frame = new DispatcherFrame(); var elapsed = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (pending.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(5)) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); if (!pending.IsCompleted) throw new TimeoutException("The isolated save did not complete.");
    }
    private sealed class BindingHost : INotifyPropertyChanged
    {
        public AppSettings Settings { get; } = new() { Dashboard = new() { Widgets = [new() { Id = "cpu", Kind = "cpu.usage", Width = 2, Height = 2 }] } };
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifySettings() => PropertyChanged?.Invoke(this, new(nameof(Settings)));
    }
}
