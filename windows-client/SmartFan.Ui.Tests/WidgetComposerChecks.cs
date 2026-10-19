using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises the actual dashboard composer without touching saved settings, devices or telemetry services.</summary>
internal static class WidgetComposerChecks
{
    public static void Run(Action<bool, string> check, string? captureDirectory = null)
    {
        CheckKindReplacement(check);
        CheckPreAddSize(check);
        CheckRatioCompatibility(check);
        CheckEditorBoundary(check, captureDirectory);
        CheckCompactMetricLayout(check);
        if (captureDirectory is not null) CaptureCompactNumbers(captureDirectory);
    }

    private static void CheckKindReplacement(Action<bool, string> check)
    {
        var source = new DashboardLayoutSettings
        {
            Widgets =
            [
                new() { Id = "gpu", Kind = "gpu.combined", Column = 4, Row = 0, Width = 4, Height = 3 },
                new() { Id = "ram", Kind = "memory.usage", Column = 4, Row = 3, Width = 4, Height = 3 },
                new() { Id = "pc-fans", Kind = "laptop.fans", Column = 0, Row = 0, Width = 4, Height = 3 },
                new() { Id = "esp-rpm", Kind = "fan.rpm", Column = 0, Row = 3, Width = 4, Height = 5 },
                new() { Id = "control", Kind = "fan.control", Column = 8, Row = 3, Width = 6, Height = 5 },
                new() { Id = "personal-cpu", Kind = "cpu.temperature", Column = 8, Row = 0, Width = 6, Height = 3 },
                new() { Id = "voltage", Kind = "cpu.voltage", Column = 4, Row = 6, Width = 4, Height = 2 }
            ]
        };
        var before = Snapshot(source);
        var metrics = new DashboardTelemetryData { CpuTemperature = 63.5, CpuUsage = 42 };
        var view = new DashboardView { Layout = source, Metrics = metrics };
        Layout(view, 1304, 620); view.BeginEditing();
        var cpu = Card(view, "personal-cpu");
        var configure = Descendants<Button>(cpu).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "编辑 CPU 温度");
        configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var kinds = (ComboBox)view.FindName("WidgetKindPicker"); kinds.SelectedValue = "cpu.combined";
        Layout(view, 1304, 620); Drain();
        var result = view.CurrentLayout.Widgets.Single(widget => widget.Id == "personal-cpu");
        check(result.Kind == "cpu.combined" && result.Column == 8 && result.Row == 0 && result.Width == 6 && result.Height == 3,
            "the selected-kind picker replaces a saved CPU temperature card with temperature plus usage while preserving its identity and slot");
        check(Snapshot(new() { Widgets = view.CurrentLayout.Widgets.Where(widget => widget.Id != result.Id).ToList() }) ==
              Snapshot(new() { Widgets = source.Widgets.Where(widget => widget.Id != result.Id).ToList() }),
            "replacing a fitting CPU card leaves all six other personal cards unchanged");
        var tiles = Descendants<DashboardMetricTile>(Card(view, result.Id)).ToArray();
        check(tiles.Length == 2 && tiles.Single(tile => tile.Unit == "°C").Value == 63.5 && tiles.Single(tile => tile.Unit == "%").Value == 42,
            "the CPU combined card displays distinct real temperature and utilization bindings");
        metrics.CpuTemperature = 71.6; metrics.CpuUsage = 57; Drain();
        check(tiles.Single(tile => tile.Unit == "°C").Value == 71.6 && tiles.Single(tile => tile.Unit == "%").Value == 57,
            "both metrics in the replacement CPU card continue following live values");
        check(Snapshot(source) == before, "changing a card kind never changes persisted settings before save");
        view.CancelLayoutEditing();
        check(Snapshot(view.CurrentLayout) == before && Snapshot(source) == before,
            "cancel restores the original single-metric CPU card and every personal placement");
    }

    private static void CheckPreAddSize(Action<bool, string> check)
    {
        var source = new DashboardLayoutSettings { Widgets = [new() { Id = "occupied", Kind = "fan.history", Width = 14, Height = 8 }] };
        var before = Snapshot(source);
        var view = new DashboardView { Layout = source }; Layout(view, 1304, 620); view.BeginEditing();
        var kind = (ComboBox)view.FindName("AddPicker"); var width = (ComboBox)view.FindName("AddWidthPicker");
        var height = (ComboBox)view.FindName("AddHeightPicker"); var ratio = (ComboBox)view.FindName("AddRatioPicker");
        var add = (Button)view.FindName("AddButton"); var fit = (TextBlock)view.FindName("AddFitStatus");
        kind.SelectedValue = "cpu.usage";
        check(Equals(width.SelectedItem, 2) && Equals(height.SelectedItem, 2) && add.IsEnabled,
            "a small numeric card can be prepared as 2 by 2 before insertion into a two-column gap");
        width.SelectedItem = 3;
        check(!add.IsEnabled && fit.Text == "放不下" && view.CurrentLayout.Widgets.Count == 1,
            "pre-add sizing immediately disables insertion when the chosen width cannot fit");
        check(!view.TryAddWidget("cpu.usage", 3, 2) && Snapshot(view.CurrentLayout) == before && Snapshot(source) == before,
            "a rejected three-column insertion preserves the entire draft and persisted source");
        width.SelectedItem = 2; ratio.SelectedValue = DashboardAspectRatio.Square;
        check(add.IsEnabled && fit.Text == "可添加" && Equals(height.SelectedItem, 2),
            "reducing the pending dimensions and choosing a square ratio restores add availability");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var inserted = view.CurrentLayout.Widgets.Single(widget => widget.Id != "occupied");
        check(inserted.Kind == "cpu.usage" && inserted.Width == 2 && inserted.Height == 2 && inserted.Column == 14 && inserted.Row == 0 && inserted.AspectRatio == DashboardAspectRatio.Square,
            "the add button inserts exactly the dimensions and ratio prepared in its controls");
        check(Snapshot(new() { Widgets = [view.CurrentLayout.Widgets.Single(widget => widget.Id == "occupied")] }) == before && Snapshot(source) == before,
            "adding a prepared small card never relocates the existing large card or writes settings");
        view.CancelLayoutEditing();
        check(Snapshot(view.CurrentLayout) == before, "cancel discards a newly added small card");

        var empty = new DashboardView { Layout = new() { Widgets = [] } }; Layout(empty, 1304, 620); empty.BeginEditing();
        ((ComboBox)empty.FindName("AddPicker")).SelectedValue = "cpu.usage";
        ((ComboBox)empty.FindName("AddWidthPicker")).SelectedItem = 6;
        ((ComboBox)empty.FindName("AddHeightPicker")).SelectedItem = 4;
        ((ComboBox)empty.FindName("AddRatioPicker")).SelectedValue = DashboardAspectRatio.Wide;
        ((ComboBox)empty.FindName("AddHeightPicker")).SelectedItem = 4;
        check(Equals(((ComboBox)empty.FindName("AddWidthPicker")).SelectedItem, 8) && Equals(((ComboBox)empty.FindName("AddHeightPicker")).SelectedItem, 4) && empty.CurrentLayout.Widgets.Count == 0,
            "pending ratio sizing follows the edited height and creates no card before Add");
        ((Button)empty.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var wide = empty.CurrentLayout.Widgets.Single();
        check(wide.Width == 8 && wide.Height == 4 && wide.AspectRatio == DashboardAspectRatio.Wide,
            "pre-add dimensions support a larger locked wide card without silently substituting a default size");
        DashboardLayoutSettings? saved = null; empty.SaveLayoutAsync = layout => { saved = layout.Clone(); return Task.FromResult(true); };
        check(empty.CommitLayoutAsync().GetAwaiter().GetResult() && saved?.Widgets.Single() is { Width: 8, Height: 4, AspectRatio: DashboardAspectRatio.Wide },
            "Done persists the prepared composition through the existing local-layout save callback");
    }

    private static void CheckRatioCompatibility(Action<bool, string> check)
    {
        var view = new DashboardView { Layout = new() { Widgets = [] } }; Layout(view, 1304, 620); view.BeginEditing();
        foreach (var kind in new[] { "fan.control", "fan.history" })
        {
            ((ComboBox)view.FindName("AddPicker")).SelectedValue = kind;
            var choices = ((ComboBox)view.FindName("AddRatioPicker")).Items.Cast<KeyValuePair<DashboardAspectRatio, string>>().Select(pair => pair.Key).ToArray();
            check(!choices.Contains(DashboardAspectRatio.Tall) && choices.Contains(DashboardAspectRatio.Free) && choices.Contains(DashboardAspectRatio.Wide),
                $"pre-add ratio choices exclude the impossible tall ratio for {kind}");
        }
        ((ComboBox)view.FindName("AddPicker")).SelectedValue = "cpu.combined";
        ((ComboBox)view.FindName("AddRatioPicker")).SelectedValue = DashboardAspectRatio.Wide;
        var wideWidths = ((ComboBox)view.FindName("AddWidthPicker")).Items.Cast<int>().ToArray();
        check(wideWidths.Contains(6) && wideWidths.Contains(8) && !wideWidths.Contains(7),
            "a wide combined CPU card offers only widths that preserve its exact two-to-one grid ratio");
        foreach (var kind in new[] { "fan.control", "fan.history" })
        {
            var original = new DashboardLayoutSettings { Widgets = [new() { Id = "tall", Kind = "cpu.usage", Width = 4, Height = 8, AspectRatio = DashboardAspectRatio.Tall, DisplayMode = DashboardDisplayMode.Gauge }] };
            var replacement = new DashboardView { Layout = original }; Layout(replacement, 1304, 620); replacement.BeginEditing();
            ((ComboBox)replacement.FindName("WidgetKindPicker")).SelectedValue = kind;
            var changed = replacement.CurrentLayout.Widgets.Single();
            check(changed.Kind == kind && changed.Id == "tall" && changed.AspectRatio == DashboardAspectRatio.Free && changed.DisplayMode == DashboardDisplayMode.Automatic && changed.Width == 5 && changed.Height == 8 && DashboardLayoutEngine.Validate(replacement.CurrentLayout).Count == 0,
                $"changing a tall metric to {kind} falls back to valid free sizing and supported presentation");
            var ratios = ((ComboBox)replacement.FindName("RatioPicker")).Items.Cast<KeyValuePair<DashboardAspectRatio, string>>().Select(pair => pair.Key);
            check(!ratios.Contains(DashboardAspectRatio.Tall), $"the selected {kind} editor also excludes impossible tall proportions");
        }
    }

    private static void CheckEditorBoundary(Action<bool, string> check, string? captureDirectory)
    {
        var view = new DashboardView { Layout = new() { Widgets = [new() { Id = "numbers", Kind = "cpu.usage", Width = 16, Height = 8 }] } };
        Layout(view, 1304, 620);
        var boundary = (FrameworkElement)view.FindName("EditorBoundary"); var backdrop = (FrameworkElement)view.FindName("EditorBoardBackdrop");
        var scrim = (FrameworkElement)view.FindName("EditorScrim"); var board = (Panel)view.FindName("BoardSurface");
        check(boundary.Visibility == Visibility.Collapsed && backdrop.Visibility == Visibility.Collapsed && scrim.Visibility == Visibility.Collapsed,
            "the editing frame and background treatment are absent during normal dashboard use");
        view.BeginEditing();
        foreach (var size in new[] { new Size(1004, 445), new Size(1304, 620) })
        {
            Layout(view, size.Width, size.Height); Drain(); Layout(view, size.Width, size.Height);
            var drawer = (FrameworkElement)view.FindName("Inspector");
            var frameBounds = boundary.TransformToAncestor(view).TransformBounds(new Rect(boundary.RenderSize));
            var boardBounds = board.TransformToAncestor(view).TransformBounds(new Rect(board.RenderSize));
            var drawerBounds = drawer.TransformToAncestor(view).TransformBounds(new Rect(drawer.RenderSize));
            check(boundary.Visibility == Visibility.Visible && backdrop.Visibility == Visibility.Visible && scrim.Visibility == Visibility.Visible && !boundary.IsHitTestVisible && !backdrop.IsHitTestVisible && !scrim.IsHitTestVisible,
                $"editing at {size.Width} by {size.Height} shows a visible board frame and background without stealing pointer input");
            check(ReferenceEquals(VisualTreeHelper.GetParent(boundary), board) && board.Children.IndexOf(boundary) > board.Children.IndexOf((UIElement)view.FindName("WidgetCanvas")) && boardBounds.Contains(frameBounds) && Math.Abs(frameBounds.Width - boardBounds.Width) < 3,
                $"the frame at {size.Width} by {size.Height} follows the same centered board transform and overlays its cards");
            check(frameBounds.Bottom < drawerBounds.Top && frameBounds.Left > 0 && frameBounds.Top >= 0,
                $"the entire framed board at {size.Width} by {size.Height} remains above the bottom drawer and inside the viewport");
            ((ComboBox)view.FindName("AddPicker")).SelectedValue = "cpu.usage";
            ((ComboBox)view.FindName("AddWidthPicker")).SelectedItem = 16;
            ((ComboBox)view.FindName("AddHeightPicker")).SelectedItem = 8;
            Layout(view, size.Width, size.Height); Drain();
            foreach (var name in new[] { "AddWidthPicker", "AddHeightPicker", "WidthPicker", "HeightPicker" })
            {
                var picker = (ComboBox)view.FindName(name);
                var content = (ContentPresenter)picker.Template.FindName("SelectedContent", picker);
                var expected = new FormattedText(Convert.ToString(picker.SelectedItem, CultureInfo.CurrentCulture) ?? "", CultureInfo.CurrentCulture, picker.FlowDirection,
                    new Typeface(picker.FontFamily, picker.FontStyle, picker.FontWeight, picker.FontStretch), picker.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(picker).PixelsPerDip);
                check(content.ActualWidth >= expected.WidthIncludingTrailingWhitespace - .5,
                    $"{name} at {size.Width} viewport has room for the full selected numeric size");
            }
            if (captureDirectory is not null) Save(view, Path.Combine(captureDirectory, $"widget-composer-frame-{size.Width:0}.png"));
        }
        view.CancelLayoutEditing(); Layout(view, 1304, 620); Drain();
        check(boundary.Visibility == Visibility.Collapsed && backdrop.Visibility == Visibility.Collapsed && scrim.Visibility == Visibility.Collapsed && ((ScaleTransform)view.FindName("BoardZoom")).ScaleX == 1,
            "cancel removes the editing frame and restores the full-size normal board");
    }

    private static void CheckCompactMetricLayout(Action<bool, string> check)
    {
        var view = new DashboardView
        {
            Layout = new()
            {
                Widgets =
                [
                    new() { Id = "small", Kind = "cpu.usage", Width = 2, Height = 2, DisplayMode = DashboardDisplayMode.Progress },
                    new() { Id = "combined", Kind = "cpu.combined", Column = 2, Width = 4, Height = 3 }
                ]
            },
            Metrics = new() { CpuUsage = 73, CpuTemperature = 63.5 }
        };
        Layout(view, 1004, 445); Drain(); Layout(view, 1004, 445);
        var host = Descendants<DashboardWidgetHost>(Card(view, "small")).Single();
        var body = (FrameworkElement)VisualTreeHelper.GetParent(host);
        var tile = Descendants<DashboardMetricTile>(host).Single();
        var texts = Descendants<TextBlock>(tile).ToArray();
        var title = texts.Single(text => text.Text == "CPU 使用率");
        var number = texts.Single(text => text.Text == "73");
        var unit = texts.Single(text => text.Text == "%");
        var bar = Descendants<Grid>(tile).Single(grid => grid.ColumnDefinitions.Count == 2 && grid.Children.OfType<Border>().Count() == 2);
        var progress = bar.Children.OfType<Border>().Single(border => Grid.GetColumnSpan(border) == 1);
        var bodyBounds = new Rect(body.RenderSize);
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(body).TransformBounds(new Rect(element.RenderSize));
        check(tile.ActualHeight < 80 && title.Visibility == Visibility.Collapsed && number.Visibility == Visibility.Visible && unit.Visibility == Visibility.Visible,
            "a 2 by 2 numeric card omits its duplicate internal title while retaining the actual number and unit");
        check(bodyBounds.Contains(Bounds(number)) && bodyBounds.Contains(Bounds(unit)) && bodyBounds.Contains(Bounds(bar)),
            "a 2 by 2 progress card keeps the complete number, unit and progress track inside its clipped content body");
        check(bar.Visibility == Visibility.Visible && bar.ActualHeight == 8 && progress.ActualWidth > 0 && Math.Abs(progress.ActualWidth - bar.ActualWidth * .73) < 1 && bodyBounds.Contains(Bounds(progress)),
            "the compact progress presentation renders an eight-pixel utilization bar at the real 73 percent value without clipping");
        var labels = Descendants<DashboardMetricTile>(Card(view, "combined")).Select(combinedTile =>
            Descendants<TextBlock>(combinedTile).Single(text => text.Text == combinedTile.Title)).ToArray();
        check(labels.Length == 2 && labels.All(label => label.Visibility == Visibility.Visible && label.ActualHeight > 0) &&
              labels.Select(label => label.Text).ToHashSet().SetEquals(new[] { "CPU 温度", "CPU 使用率" }),
            "a compact 4 by 3 combined card retains both temperature and utilization labels so the two metrics stay identifiable");
    }

    private static void CaptureCompactNumbers(string directory)
    {
        var view = new DashboardView
        {
            Layout = new()
            {
                Widgets =
                [
                    new() { Id = "frequency", Kind = "cpu.clock", Width = 2, Height = 2 },
                    new() { Id = "memory", Kind = "memory.usage", Column = 2, Width = 2, Height = 2 },
                    new() { Id = "progress", Kind = "cpu.usage", Column = 4, Width = 2, Height = 2, DisplayMode = DashboardDisplayMode.Progress },
                    new() { Id = "combined", Kind = "cpu.combined", Column = 6, Width = 4, Height = 3 }
                ]
            },
            Metrics = new() { CpuClockMhz = 12345, MemoryUsed = 16384, MemoryTotal = 32768, MemoryUnit = "MiB", MemoryValueFormat = "N0", CpuUsage = 73, CpuTemperature = 63.5 }
        };
        Layout(view, 1004, 445); Drain(); Layout(view, 1004, 445);
        Save(view, Path.Combine(directory, "widget-composer-small-long-values.png"));
    }

    private static Border Card(DashboardView view, string id) => ((Canvas)view.FindName("WidgetCanvas")).Children.OfType<Border>().Single(card => Equals(card.Tag, id));
    private static string Snapshot(DashboardLayoutSettings layout) => string.Join("|", layout.Widgets.Select(widget => $"{widget.Id}:{widget.Kind}:{widget.Column},{widget.Row},{widget.Width},{widget.Height}:{widget.DisplayMode}:{widget.AspectRatio}"));
    private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static void Save(FrameworkElement root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output);
    }
}
