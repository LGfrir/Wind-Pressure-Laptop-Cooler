using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class WidgetDragChecks
{
    public static void Run(Action<bool, string> check)
    {
        var source = new DashboardLayoutSettings(); var grid = new DashboardGridGeometry(1600, 800);
        var start = grid.GetBounds(source.Widgets.Single(widget => widget.Id == "cpu"));
        var session = new DashboardDragSession(source, "cpu", grid, start, false);
        var timer = Stopwatch.StartNew(); DashboardDragUpdate update = default;
        for (var i = 1; i <= 1000; i++) update = session.Update(new Vector(i * .03, i * .02));
        timer.Stop();
        check(Math.Abs(update.VisualBounds.X - start.X - 30) < .001 && Math.Abs(update.VisualBounds.Y - start.Y - 20) < .001, "sub-cell pointer movements continuously move the card rather than waiting for a grid boundary");
        check(session.PlacementEvaluationCount == 0 && !update.PlacementChanged, "1000 pointer events inside the same cell do not run the layout packer");
        update = session.Update(new Vector(grid.PitchX * 4, 0));
        check(update.PlacementChanged && session.PreviewLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 4 && DashboardLayoutEngine.Validate(session.PreviewLayout).Count == 0, "crossing a compatible occupied slot previews a valid swap");
        var count = session.PlacementEvaluationCount;
        session.Update(new Vector(grid.PitchX * 4 + 10, 0)); session.Update(new Vector(0, 0)); session.Update(new Vector(grid.PitchX * 4, 0));
        check(count == session.PlacementEvaluationCount, "revisiting a grid cell reuses its per-gesture placement result");
        session.Update(new Vector(grid.PitchX, 0)); count = session.PlacementEvaluationCount;
        update = session.Update(new Vector(grid.PitchX + 12.5, 0));
        check(!update.CandidateFits && Math.Abs(update.VisualBounds.X - start.X - grid.PitchX - 12.5) < .001 && session.PlacementEvaluationCount == count,
            "an invalid drop cell still follows the pointer and is not repeatedly recomputed");
        check(source.Widgets.Single(widget => widget.Id == "cpu").Column == 0, "drag previews never mutate the input draft or saved settings");
        update = session.Update(new Vector(-100000, 100000));
        check(grid.BoardBounds.Contains(update.VisualBounds), "extreme pointer coordinates keep the visual card inside the desktop");
        var resize = new DashboardDragSession(source, "cpu", grid, start, true);
        update = resize.Update(new Vector(13.5, 9.25));
        check(Math.Abs(update.VisualBounds.Width - start.Width - 13.5) < .001 && resize.PlacementEvaluationCount == 0, "resize handles respond continuously while keeping content layout stable inside a cell");
        update = resize.Update(new Vector(grid.PitchX * 2, 0));
        check(!update.CandidateFits && resize.PreviewLayout.Widgets.Single(widget => widget.Id == "cpu").Width == 4, "an impossible full-desktop resize preserves the last valid drop target");
        var ratioLayout = new DashboardLayoutSettings { Widgets = [new() { Id = "wide", Kind = "cpu.usage", Width = 6, Height = 3, AspectRatio = DashboardAspectRatio.Wide }] };
        var ratioSession = new DashboardDragSession(ratioLayout, "wide", grid, grid.GetBounds(ratioLayout.Widgets[0]), true);
        update = ratioSession.Update(new Vector(28, 10));
        check(Math.Abs(update.VisualBounds.Width / update.VisualBounds.Height - 2) < .001, "continuous resize maintains the locked physical aspect ratio");
        check(grid.GetBounds(source.Widgets.Single(widget => widget.Id == "pc-fans")).Right >= grid.Width - grid.Gap && grid.GetBounds(source.Widgets.Single(widget => widget.Id == "control")).Bottom >= grid.Height - grid.Gap,
            "normal layout uses the full desktop width and height instead of centering a square-cell board");
        CheckRenderOnlyMovement(check);
        CheckViewGesture(check);
        CheckWholeCardDragTargets(check);
        Console.WriteLine($"Drag probe: 1000 sub-cell pointer updates {timer.Elapsed.TotalMilliseconds:F2} ms, 0 placement evaluations (logic benchmark; not a desktop FPS measurement).");
    }
    private static void CheckRenderOnlyMovement(Action<bool, string> check)
    {
        var child = new MeasureProbe(); var border = new Border { Child = child }; var canvas = new Canvas(); canvas.Children.Add(border);
        var motion = new DashboardWidgetMotion(border); motion.SetTarget(new Rect(20, 30, 220, 120), false); Layout(canvas, 800, 500);
        var measures = child.Measures; motion.BeginFollowing();
        for (var i = 0; i < 300; i++) motion.SetVisualBounds(new Rect(20 + i * .25, 30 + i * .1, 220 + i * .02, 120 + i * .01));
        Layout(canvas, 800, 500);
        check(child.Measures == measures && Canvas.GetLeft(border) == 20 && border.Width == 220, "continuous movement and resize alter render transforms without remeasuring card contents or moving Canvas layout");
        check(border.CacheMode is BitmapCache && motion.IsFollowing, "the active widget uses a temporary bitmap cache during pointer movement");
        motion.EndFollowing(); motion.SetTarget(new Rect(100, 100, 260, 150), false, true);
        check(border.CacheMode is null && !motion.IsFollowing && motion.VisualBounds == new Rect(100, 100, 260, 150), "release restores the original caching policy and exact settled bounds");
        if (SystemParameters.ClientAreaAnimation)
        {
            motion.SetTarget(new Rect(400, 100, 260, 150), true); Drain(65);
            var before = motion.VisualBounds;
            motion.SetTarget(new Rect(650, 100, 260, 150), true);
            var after = motion.VisualBounds;
            check(Math.Abs(after.X - before.X) < 1, "retargeting an in-flight neighbor animation starts at its current displayed position");
            Drain(210); check(Math.Abs(motion.VisualBounds.X - 650) < .1, "retargeted motion settles at the latest destination");
        }
    }
    private static void CheckViewGesture(Action<bool, string> check)
    {
        var view = new DashboardView(); Layout(view, 1304, 620); view.BeginEditing(); Layout(view, 1304, 620);
        var boardZoom = (ScaleTransform)view.FindName("BoardZoom");
        boardZoom.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, boardZoom.ScaleX, TimeSpan.FromMilliseconds(500)));
        Invoke(view, "SelectWidget", "gpu");
        check(boardZoom.HasAnimatedProperties, "selecting a card during the editor transition does not restart or cut short the board zoom");
        boardZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        var canvas = (Canvas)view.FindName("WidgetCanvas"); var card = canvas.Children.OfType<Border>().Single(border => Equals(border.Tag, "cpu"));
        var left = Canvas.GetLeft(card); var top = Canvas.GetTop(card); var width = card.Width; var height = card.Height;
        Invoke(view, "StartGesture", "cpu", false); Invoke(view, "ApplyGestureDelta", new Vector(12.25, 7.5));
        check(Canvas.GetLeft(card) == left && Canvas.GetTop(card) == top && card.Width == width && card.Height == height && card.CacheMode is BitmapCache,
            "the actual dashboard gesture path keeps Canvas bounds unchanged while the pointer moves");
        check(Math.Abs(card.RenderTransform.Value.OffsetX - 12.25) < .01 && view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 0,
            "the actual card follows fractional pointer offsets without prematurely committing layout");
        Invoke(view, "CompleteGesture", true); Drain(210);
        check(card.CacheMode is null && Math.Abs(card.RenderTransform.Value.OffsetX) < .01 && view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 0,
            "canceling a gesture removes preview transforms and retains the original model");
        Invoke(view, "StartGesture", "cpu", false); Invoke(view, "ApplyGestureDelta", new Vector(1304d / 16 * 4, 0)); Invoke(view, "CompleteGesture", false); Drain(210);
        check(view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 4 && card.CacheMode is null, "releasing commits the preview and releases the drag cache");
        var workspace = (FrameworkElement)view.FindName("Workspace"); var drawer = (FrameworkElement)view.FindName("Inspector");
        var board = (FrameworkElement)view.FindName("BoardSurface"); var scaled = board.TransformToAncestor(view).TransformBounds(new Rect(board.RenderSize));
        check(scaled.Width < workspace.ActualWidth && scaled.Bottom < drawer.TransformToAncestor(view).TransformBounds(new Rect(drawer.RenderSize)).Top,
            "the entire desktop shrinks uniformly above the compact bottom controls");
        Invoke(view, "StartGesture", "cpu", false); Layout(view, 1004, 445); Drain(210);
        check(card.CacheMode is null && view.CurrentLayout.Widgets.Single(widget => widget.Id == "cpu").Column == 4, "window resizing cancels the in-flight pointer gesture without changing the last committed draft");
        view.CancelLayoutEditing(); Layout(view, 1004, 445); Drain(210);
        var zoom = (ScaleTransform)view.FindName("BoardZoom");
        check(zoom.ScaleX == 1 && zoom.ScaleY == 1 && drawer.Visibility == Visibility.Collapsed && board.ActualWidth == workspace.ActualWidth,
            "leaving edit mode restores the full-width desktop and reserves no sidebar or drawer space");
        var picker = (ComboBox)view.FindName("AddPicker"); picker.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        check(picker.Template.FindName("PART_Popup", picker) is System.Windows.Controls.Primitives.Popup { Placement: System.Windows.Controls.Primitives.PlacementMode.Top }, "bottom editor selectors open upward");
    }
    private static void CheckWholeCardDragTargets(Action<bool, string> check)
    {
        var view = new DashboardView();
        var host = new Window { Content = view, Width = 1304, Height = 620, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Title = "SmartFan isolated widget input check" };
        try
        {
            host.Show(); Drain(1); host.UpdateLayout(); CheckWholeCardInput(view, check);
        }
        finally { host.Close(); }
    }
    private static void CheckWholeCardInput(DashboardView view, Action<bool, string> check)
    {
        var canvas = (Canvas)view.FindName("WidgetCanvas");
        Border Card(string id) => canvas.Children.OfType<Border>().Single(card => Equals(card.Tag, id));
        var control = Card("control");
        var fan = Descendants<DashboardFanControl>(control).Single();
        var slider = Descendants<Slider>(fan).Single();
        var mode = Descendants<ComboBox>(fan).Single();
        var apply = Descendants<Button>(fan).Single(button => Equals(button.Content, "应用风扇设置"));
        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), control);
        check(Ancestor<Slider>(control.InputHitTest(Center(slider)) as DependencyObject) == slider &&
            Ancestor<ComboBox>(control.InputHitTest(Center(mode)) as DependencyObject) == mode,
            "browsing mode delivers pointer input to the real fan slider and mode picker");
        var configure = Descendants<Button>(control).Single(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("编辑 "));
        check(Ancestor<Button>(control.InputHitTest(Center(configure)) as DependencyObject) == configure, "browsing mode retains the card's edit button");
        view.BeginEditing(); Layout(view, 1304, 620);
        foreach (var card in canvas.Children.OfType<Border>())
        {
            var move = Descendants<Thumb>(card).Single(thumb => System.Windows.Automation.AutomationProperties.GetName(thumb).StartsWith("移动 "));
            var points = new[] { new Point(card.ActualWidth / 2, card.ActualHeight / 2), new Point(3, card.ActualHeight / 2),
                new Point(card.ActualWidth - 3, card.ActualHeight / 2), new Point(card.ActualWidth / 2, 3), new Point(card.ActualWidth / 2, card.ActualHeight - 3), new Point(40, 20) };
            check(points.All(point => Ancestor<Thumb>(card.InputHitTest(point) as DependencyObject) == move),
                $"editing captures the entire {card.Tag} card, including body, header and padding, for dragging");
            var resize = Descendants<Thumb>(card).Single(thumb => System.Windows.Automation.AutomationProperties.GetName(thumb).StartsWith("缩放 "));
            var resizeCenter = resize.TranslatePoint(new Point(resize.ActualWidth / 2, resize.ActualHeight / 2), card);
            check(Ancestor<Thumb>(card.InputHitTest(resizeCenter) as DependencyObject) == resize, $"the {card.Tag} resize handle takes priority over whole-card dragging");
        }
        var controlMove = Descendants<Thumb>(control).Single(thumb => System.Windows.Automation.AutomationProperties.GetName(thumb).StartsWith("移动 "));
        check(new[] { mode, (FrameworkElement)slider, apply }.All(element => Ancestor<Thumb>(control.InputHitTest(Center(element)) as DependencyObject) == controlMove),
            "editing intercepts fan mode, PWM slider and apply-button input instead of activating device controls");
        controlMove.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Invoke(view, "ApplyGestureDelta", new Vector(-8.5, -4.25));
        check(control.CacheMode is BitmapCache && Math.Abs(control.RenderTransform.Value.OffsetX + 8.5) < .01,
            "the full-card thumb is wired into the continuous drag gesture");
        controlMove.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
        check(control.CacheMode is null, "canceling the full-card thumb gesture releases its temporary cache");
        view.CancelLayoutEditing(); Layout(view, 1304, 620);
        control = Card("control"); slider = Descendants<Slider>(control).Single();
        check(Ancestor<Slider>(control.InputHitTest(Center(slider)) as DependencyObject) == slider,
            "leaving editing removes the input overlay and restores normal slider interaction");
    }
    private static T? Ancestor<T>(DependencyObject? item) where T : DependencyObject
    {
        while (item is not null) { if (item is T match) return match; item = VisualTreeHelper.GetParent(item); }
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Invoke(DashboardView view, string name, params object[] arguments) =>
        (typeof(DashboardView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name)).Invoke(view, arguments);
    private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Drain(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class MeasureProbe : FrameworkElement
    {
        public int Measures { get; private set; }
        protected override Size MeasureOverride(Size availableSize) { Measures++; return new Size(100, 50); }
    }
}
