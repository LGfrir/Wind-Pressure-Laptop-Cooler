using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.IO;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class WidgetWorkspaceChecks
{
    public static void Run(Action<bool, string> check, string? captureDirectory = null)
    {
        var source = new DashboardLayoutSettings();
        var view = new DashboardView { Layout = source, Metrics = new DashboardTelemetryData { CpuTemperature = 63.5, CpuUsage = 42, FanRpm = 6200 } };
        Layout(view, 1304, 620);
        check(view.CurrentLayout.Widgets.Count == 7 && DashboardLayoutEngine.Validate(view.CurrentLayout).Count == 0, "default workspace has seven non-overlapping independent cards");
        view.BeginEditing(); Layout(view, 1004, 445); Drain(); Layout(view, 1004, 445);
        var drawer = (FrameworkElement)view.FindName("Inspector"); var workspace = (FrameworkElement)view.FindName("Workspace"); var board = (FrameworkElement)view.FindName("BoardSurface");
        check(view.IsEditing && drawer.ActualHeight <= 120 && drawer.ActualWidth > 900, "editing uses a compact bottom drawer across the minimum viewport");
        var selectedInspector = (FrameworkElement)view.FindName("SelectedInspector"); var addPicker = (FrameworkElement)view.FindName("AddPicker");
        check(selectedInspector.TranslatePoint(new Point(), view).Y >= addPicker.TranslatePoint(new Point(0, addPicker.ActualHeight), view).Y, "bottom drawer separates component selection from size and presentation controls");
        var boardBounds = board.TransformToAncestor(view).TransformBounds(new Rect(board.RenderSize)); var drawerBounds = drawer.TransformToAncestor(view).TransformBounds(new Rect(drawer.RenderSize));
        check(boardBounds.Bottom < drawerBounds.Top && Math.Abs(boardBounds.Left + boardBounds.Width / 2 - workspace.ActualWidth / 2) < 1, "editing scales the whole desktop uniformly around its center above the bottom drawer");
        if (captureDirectory is not null) Save(view, Path.Combine(captureDirectory, "widget-editor-minimum.png"));
        var canvas = (Canvas)view.FindName("WidgetCanvas");
        var combinedTiles = Descendants<DashboardWidgetHost>(view).Where(host => host.WidgetKey == "cpu.combined").SelectMany(host => Descendants<DashboardMetricTile>(host)).ToArray();
        check(combinedTiles.Length == 2 && combinedTiles[0].SharedNumberSize == combinedTiles[1].SharedNumberSize && combinedTiles[0].SharedNumberSize >= 16, "a compact combined widget keeps both numbers at the same readable font size");
        check(canvas.Children.OfType<Border>().All(card => card.ActualWidth > 100 && card.ActualHeight > 70 && Canvas.GetLeft(card) >= 0 && Canvas.GetTop(card) >= 0), "all editable cards fit within the minimum viewport");
        check(Descendants<DashboardWidgetHost>(view).All(host => !host.IsHitTestVisible) && Descendants<Thumb>(view).Count(thumb => thumb.Visibility == Visibility.Visible && System.Windows.Automation.AutomationProperties.GetName(thumb).StartsWith("移动 ") || thumb.Visibility == Visibility.Visible && System.Windows.Automation.AutomationProperties.GetName(thumb).StartsWith("缩放 ")) == 14, "editing exposes move/resize handles and blocks accidental device controls");
        var move = view.CurrentLayout.Widgets.Single(item => item.Id == "cpu"); move.Column = 4;
        check(view.TryUpdateWidget(move) && view.CurrentLayout.Widgets.Single(item => item.Id == "cpu").Column == 4 && DashboardLayoutEngine.Validate(view.CurrentLayout).Count == 0, "moving into an occupied slot repositions displaced widgets without overlap");
        check(source.Widgets.Single(item => item.Id == "cpu").Column == 0, "editing never mutates persisted settings before save");
        check(!view.TryAddWidget("cpu.temperature") && view.CurrentLayout.Widgets.Count == 7, "full workspace explains capacity without losing widgets");
        check(view.RemoveWidget("cpu") && view.TryAddWidget("cpu.temperature"), "removing a group makes room for a standalone metric");
        var metric = view.CurrentLayout.Widgets.Single(item => item.Kind == "cpu.temperature");
        var configure = Descendants<Button>(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "编辑 CPU 温度"); configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = (ComboBox)view.FindName("DisplayPicker"); picker.SelectedValue = DashboardDisplayMode.Gauge; Layout(view, 1004, 445); Drain();
        var host = Descendants<DashboardWidgetHost>(view).Single(item => item.WidgetKey == "cpu.temperature");
        var gauge = Descendants<DashboardGauge>(host).Single();
        check(gauge.Value == 63.5 && gauge.Unit == "°C" && host.DisplayMode == DashboardDisplayMode.Gauge, "presentation selector replaces the actual metric content with a bound gauge");
        view.Metrics!.TemperatureUnit = "°F"; view.Metrics.TemperatureMaximum = 212; view.Metrics.CpuTemperature = 146.3; Drain();
        check(gauge.Value == 146.3 && gauge.Unit == "°F" && gauge.DisplayMaximum == 212, "custom gauge follows the same live values and selected units");
        picker.SelectedValue = DashboardDisplayMode.Number; Drain();
        check(host.Content is DashboardMetricTile tile && tile.DisplayMode == DashboardDisplayMode.Number && tile.Value == 146.3, "number presentation preserves live metric data");
        picker.SelectedValue = DashboardDisplayMode.Progress;
        check(host.Content is DashboardMetricTile progress && progress.DisplayMode == DashboardDisplayMode.Progress, "progress presentation is selectable independently of metric kind");
        check(view.CurrentLayout.Widgets.Single(item => item.Id == metric.Id).DisplayMode == DashboardDisplayMode.Progress, "display choice is part of the editable layout");
        view.CancelLayoutEditing(); Layout(view, 1304, 620);
        check(!view.IsEditing && view.CurrentLayout.Widgets.Select(item => item.Id).SequenceEqual(source.Widgets.Select(item => item.Id)), "cancel restores removed widgets, positions and presentation settings");
        view.BeginEditing(); view.RemoveWidget("cpu"); view.TryAddWidget("cpu.usage");
        view.SaveLayoutAsync = _ => Task.FromResult(false);
        check(!view.CommitLayoutAsync().GetAwaiter().GetResult() && view.IsEditing, "failed save retains the editable draft");
        DashboardLayoutSettings? saved = null;
        view.SaveLayoutAsync = layout => { saved = layout.Clone(); return Task.FromResult(true); };
        check(view.CommitLayoutAsync().GetAwaiter().GetResult() && !view.IsEditing && saved is not null, "successful save exits edit mode only after persistence acknowledges it");
        var reopened = new DashboardView { Layout = saved, Metrics = view.Metrics }; Layout(reopened, 1304, 620);
        check(reopened.CurrentLayout.Widgets.Select(item => item.Id).SequenceEqual(view.CurrentLayout.Widgets.Select(item => item.Id)), "reopened workspace restores the saved composition");
        check(!Descendants<ScrollViewer>(reopened).Any(), "normal workspace remains a single page without a scroll container");
        if (captureDirectory is not null) Save(reopened, Path.Combine(captureDirectory, "widget-desktop-custom.png"));
        // A large empty workspace permits exact aspect-ratio verification without capacity constraints.
        var ratioView = new DashboardView { Layout = new() { Widgets = [new() { Id = "solo", Kind = "cpu.usage", Width = 4, Height = 4, AspectRatio = DashboardAspectRatio.Square }] } };
        Layout(ratioView, 1004, 445); ratioView.BeginEditing(); Layout(ratioView, 1004, 445);
        var ratio = (ComboBox)ratioView.FindName("RatioPicker"); ratio.SelectedValue = DashboardAspectRatio.Wide; Layout(ratioView, 1004, 445); Drain(); Layout(ratioView, 1004, 445);
        var card = ((Canvas)ratioView.FindName("WidgetCanvas")).Children.OfType<Border>().Single();
        check(ratioView.CurrentLayout.Widgets.Single().AspectRatio == DashboardAspectRatio.Wide && Math.Abs(card.ActualWidth / card.ActualHeight - 2) < .01, "locked wide ratio produces an actual 2:1 card, not a stretched imitation");
        ratio.SelectedValue = DashboardAspectRatio.Tall; Layout(ratioView, 1304, 620); Drain(); Layout(ratioView, 1304, 620);
        check(Math.Abs(card.ActualWidth / card.ActualHeight - .5) < .01, "locked tall ratio survives viewport changes");
        var rpm = new DashboardWidgetHost { WidgetKey = "fan.rpm", DisplayMode = DashboardDisplayMode.Progress, Metrics = view.Metrics }; Layout(rpm, 240, 150);
        check(rpm.Content is DashboardMetricTile rpmTile && rpmTile.AutoScale && rpmTile.Value == 6200, "RPM progress uses automatic scale without confusing PC and ESP readings");
        var bindingSource = new AppSettings(); var bound = new DashboardView(); bound.SetBinding(DashboardView.LayoutProperty, new Binding("Dashboard") { Source = bindingSource }); Layout(bound, 1304, 620);
        bound.BeginEditing(); bound.CommitLayoutAsync().GetAwaiter().GetResult();
        check(BindingOperations.IsDataBound(bound, DashboardView.LayoutProperty), "saving layout preserves the settings binding for later imports");
    }
    private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Save(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output);
    }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
