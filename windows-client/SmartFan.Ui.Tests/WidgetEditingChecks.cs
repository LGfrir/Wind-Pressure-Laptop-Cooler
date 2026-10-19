using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

/// <summary>Real composer mutations and recovery callbacks; no saved user settings or devices.</summary>
internal static class WidgetEditingChecks
{
    public static void Run(Action<bool, string> check, string? captureDirectory = null)
    {
        CheckUndoAndRecovery(check);
        CheckPointerFeedback(check);
        CheckGalleryAndSuggestion(check);
        CheckBoundedHistory(check);
    }
    private static void CheckUndoAndRecovery(Action<bool, string> check)
    {
        var source = new DashboardLayoutSettings { Widgets = [] };
        var view = new DashboardView { Layout = source }; Layout(view); view.BeginEditing();
        var events = new List<DashboardDraftChangedEventArgs>(); view.DraftChanged += (_, args) => events.Add(args);
        check(!view.CanUndo && !view.CanRedo, "an untouched editor starts with no undo or redo entries");
        check(view.TryAddWidget("cpu.usage", 2, 2), "a semantic add operation enters local editor history");
        var id = view.CurrentLayout.Widgets.Single().Id;
        var candidate = view.CurrentLayout.Widgets.Single(); candidate.Column = 4; view.TryUpdateWidget(candidate);
        ((ComboBox)view.FindName("DisplayPicker")).SelectedValue = DashboardDisplayMode.Gauge;
        check(events.Count == 3 && events.All(args => args.Baseline.Widgets.Count == 0) && source.Widgets.Count == 0,
            "add, move and display changes emit detached drafts with the original baseline and never write settings");
        check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Single().DisplayMode == DashboardDisplayMode.Automatic && view.CanRedo,
            "undo restores the previous display style independently of its position");
        check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Single().Column == 0,
            "a second undo restores the previous complete grid position");
        check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Count == 0 && !view.CanUndo && events[^1].Draft is null,
            "undoing to the saved baseline clears the recovery draft");
        check(view.RedoLayoutEdit() && view.CurrentLayout.Widgets.Single().Id == id,
            "redo restores the exact added card identity");
        check(view.RedoLayoutEdit() && view.CurrentLayout.Widgets.Single().Column == 4,
            "redo restores the exact moved grid position");
        var unchanged = view.CurrentLayout.Widgets.Single(); var count = events.Count; view.TryUpdateWidget(unchanged);
        check(events.Count == count && view.CanRedo, "a no-op edit does not consume undo space or discard redo history");
        check(view.TryChangeWidgetKind(id, "gpu.usage") && !view.CanRedo,
            "a new semantic edit after undo starts a new history branch");
        var draft = view.CurrentLayout;
        var recovered = new DashboardView { Layout = source }; Layout(recovered);
        check(recovered.RestoreDraft(draft) && recovered.IsEditing && recovered.CanUndo && recovered.CurrentLayout.Widgets.Single().Kind == "gpu.usage",
            "a validated recovery draft reopens in edit mode and retains undo back to the saved baseline");
        draft.Widgets[0].Column = 12;
        check(recovered.CurrentLayout.Widgets.Single().Column == 4 && !recovered.RestoreDraft(source),
            "recovery clones its input and cannot overwrite an already active editor");
        recovered.UndoLayoutEdit();
        check(recovered.CurrentLayout.Widgets.Count == 0 && recovered.RedoLayoutEdit(),
            "a recovered session can undo and redo the entire recovered draft");
        DashboardDraftChangedEventArgs? cleared = null; recovered.DraftChanged += (_, args) => cleared = args;
        recovered.CancelLayoutEditing();
        check(!recovered.IsEditing && recovered.CurrentLayout.Widgets.Count == 0 && cleared?.Draft is null,
            "cancel explicitly clears recovery and leaves the persisted baseline untouched");
        var invalid = new DashboardLayoutSettings { Widgets = [new() { Width = 99 }] };
        check(!recovered.RestoreDraft(invalid) && !recovered.RestoreDraft(source),
            "invalid and baseline-identical recovery drafts are rejected");
        events[0].Draft!.Widgets.Clear();
        check(view.CurrentLayout.Widgets.Count == 1, "mutating an event snapshot cannot change the active workspace");
        view.SaveLayoutAsync = _ => Task.FromResult(false);
        check(!view.CommitLayoutAsync().GetAwaiter().GetResult() && view.CanUndo && events[^1].Draft is not null,
            "failed persistence retains both edit history and recoverable draft");
        view.SaveLayoutAsync = _ => Task.FromResult(true);
        check(view.CommitLayoutAsync().GetAwaiter().GetResult() && events[^1].Draft is null && !view.CanUndo,
            "successful persistence explicitly clears recovery and history");
    }
    private static void CheckPointerFeedback(Action<bool, string> check)
    {
        var view = new DashboardView(); Layout(view); view.BeginEditing();
        var invalid = (Border)view.FindName("InvalidDropPreview"); var target = (Border)view.FindName("DropPreview");
        var workspace = (FrameworkElement)view.FindName("Workspace"); var pitch = workspace.ActualWidth / DashboardLayoutEngine.Columns;
        Invoke(view, "StartGesture", "cpu", false);
        Invoke(view, "ApplyGestureDelta", new Vector(pitch * 4, 0));
        var validLeft = Canvas.GetLeft(target);
        Invoke(view, "ApplyGestureDelta", new Vector(pitch, 0));
        check(invalid.Visibility == Visibility.Visible && target.Visibility == Visibility.Visible && Canvas.GetLeft(target) == validLeft &&
              Canvas.GetLeft(invalid) < validLeft && ((TextBlock)view.FindName("WorkspaceStatus")).Text.Contains("最后有效位置"),
            "invalid drag positions show a separate red pointer outline and retain the last valid landing frame with an explanation");
        check(!view.CanUndo, "pointer preview events do not create premature history entries");
        Invoke(view, "CompleteGesture", false);
        check(invalid.Visibility == Visibility.Collapsed && target.Visibility == Visibility.Collapsed && view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 4 && view.CanUndo,
            "release from an invalid candidate settles on the visibly previewed valid target as one undo step");
        check(view.UndoLayoutEdit() && view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 0 && !view.CanUndo,
            "one undo reverts a complete pointer gesture including all displaced cards");
        Invoke(view, "StartGesture", "cpu", false); Invoke(view, "ApplyGestureDelta", new Vector(pitch * 4, 0)); Invoke(view, "CompleteGesture", true);
        check(!view.CanUndo && view.CanRedo, "canceling a pointer gesture neither creates an edit nor discards redo history");
    }
    private static void CheckGalleryAndSuggestion(Action<bool, string> check)
    {
        var source = new DashboardLayoutSettings { Widgets = [new() { Id = "occupied", Kind = "fan.history", Width = 14, Height = 8 }] };
        var view = new DashboardView { Layout = source }; Layout(view); view.BeginEditing();
        ((ComboBox)view.FindName("AddPicker")).SelectedValue = "cpu.usage";
        ((ComboBox)view.FindName("AddWidthPicker")).SelectedItem = 4;
        var suggestion = (TextBlock)view.FindName("AddRecommendation");
        check(suggestion.Visibility == Visibility.Visible && suggestion.Text.Contains("2×2") && !((Button)view.FindName("AddButton")).IsEnabled,
            "the composer recommends a real fitting smaller size when the pending size cannot fit");
        suggestion.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        var preview = (Border)view.FindName("AddPlacementPreview");
        check(Equals(((ComboBox)view.FindName("AddWidthPicker")).SelectedItem, 2) && ((Button)view.FindName("AddButton")).IsEnabled && preview.Visibility == Visibility.Visible,
            "clicking the recommendation prepares that exact size and reveals an actual desktop landing preview");
        var workspace = (FrameworkElement)view.FindName("Workspace"); var expected = new DashboardGridGeometry(workspace.ActualWidth, workspace.ActualHeight).GetBounds(new() { Column = 14, Width = 2, Height = 2 });
        check(Math.Abs(Canvas.GetLeft(preview) - expected.Left) < .01 && Math.Abs(preview.Width - expected.Width) < .01 && source.Widgets.Count == 1 && view.CurrentLayout.Widgets.Count == 1,
            "pre-add preview uses the actual remaining gap without creating or relocating any cards");
        Invoke(view, "PopulateGallery");
        var gallery = (WrapPanel)view.FindName("GalleryItems");
        check(gallery.Children.OfType<Button>().Count() == DashboardLayoutEngine.Kinds.Count && gallery.Children.OfType<Button>().All(button => button.Content is Panel),
            "the gallery includes a visible thumbnail and label for every supported component");
        var gpu = gallery.Children.OfType<Button>().Single(button => Equals(button.Tag, "gpu.combined")); gpu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Equals(((ComboBox)view.FindName("AddPicker")).SelectedValue, "gpu.combined") && view.CurrentLayout.Widgets.Count == 1,
            "gallery selection updates the pending component instead of inserting it before sizing");
        view.CancelLayoutEditing();
        check(preview.Visibility == Visibility.Collapsed, "leaving edit mode removes pending-add landing hints");
    }
    private static void CheckBoundedHistory(Action<bool, string> check)
    {
        var layout = new DashboardLayoutSettings { Widgets = [new() { Id = "tile", Kind = "cpu.usage", Width = 2, Height = 2 }] };
        var history = new DashboardEditHistory(); history.Reset(layout);
        for (var i = 1; i <= 100; i++) { layout.Widgets[0].Column = i % 15; history.Record(layout); }
        var undos = 0; while (history.Undo() is not null) undos++;
        check(undos == 60, "editor history keeps at most sixty complete undo operations");
    }
    private static void Layout(FrameworkElement view) { view.Measure(new Size(1304, 620)); view.Arrange(new Rect(0, 0, 1304, 620)); view.UpdateLayout(); }
    private static void Invoke(DashboardView view, string name, params object[] args) =>
        (typeof(DashboardView).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException(name)).Invoke(view, args);
}
