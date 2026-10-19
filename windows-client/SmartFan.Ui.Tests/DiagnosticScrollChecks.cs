using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Core.Protocol;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises a real off-screen HWND and routed wheel input over both the row edge and detail center.</summary>
internal static class DiagnosticScrollChecks
{
    public static void Run(Action<bool, string> check, bool legacyScroll = false)
    {
        var rows = new ObservableCollection<DiagnosticRow>(ProtocolCatalog.All.Select(definition => new DiagnosticRow(definition)));
        var view = new DiagnosticsView { DataContext = new ScrollingFixture { Diagnostics = rows, SelectedDiagnostic = rows[0] } };
        var host = new Window
        {
            Content = view, Width = 1160, Height = 720, Left = -30000, Top = -30000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Title = "SmartFan isolated scrolling check"
        };
        try
        {
            host.Show(); Pump(); host.UpdateLayout();
            var grid = (DataGrid)view.FindName("DiagnosticsGrid");
            if (legacyScroll) { grid.SetValue(ScrollViewer.CanContentScrollProperty, true); VirtualizingPanel.SetScrollUnit(grid, ScrollUnit.Item); Pump(); }
            var outer = Descendants<ScrollViewer>(grid).First();
            var unread = rows.Single(row => row.Key == CharacteristicKey.LightMode);
            var fanUsed = rows.Single(row => row.Key == CharacteristicKey.LaptopFanUsed);
            var custom = rows.Single(row => row.Key == CharacteristicKey.CustomBreathConfig);
            fanUsed.ApplyPayload(PacketCodec.EncodeFanArray([1, 1, 1, 0, 0]), "离线滚动夹具");
            custom.ApplyPayload(PacketCodec.EncodeLighting(new LightingConfiguration
            {
                Mode = LightMode.CustomBreath,
                Colors = Enumerable.Range(0, 9).Select(i => new RgbColor((byte)(i * 20), 100, 200)).ToList(),
                FadeMilliseconds = Enumerable.Range(0, 9).Select(i => (ushort)(100 + i * 100)).ToList()
            }), "离线滚动夹具");

            foreach (var target in new[] { unread, fanUsed, custom })
            {
                outer.ScrollToEnd(); Pump();
                grid.ScrollIntoView(target); Pump();
                var container = grid.ItemContainerGenerator.ContainerFromItem(target) as DataGridRow
                    ?? throw new InvalidOperationException($"Realized scrolling row missing: {target.Name}");
                var toggle = Descendants<DisclosureToggle>(container).Single();
                ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle(); Pump();
                if (!legacyScroll) { toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle)); Pump(); }
                check(target.IsExpanded && container.DetailsVisibility == Visibility.Visible, $"HWND: {target.Name} expands after scrolling to the lower list");
                var detail = Descendants<DataGridDetailsPresenter>(container).Single();
                check(!Descendants<ScrollViewer>(detail).Any(), $"HWND: {target.Name} detail has no nested wheel trap");
                var headerHeight = Descendants<DataGridColumnHeadersPresenter>(grid).First().ActualHeight;
                var centerText = Descendants<TextBlock>(detail).First();
                var edge = Descendants<DataGridCell>(container).First();
                var distances = new List<double>();
                foreach (var (area, wheelTarget) in new (string, UIElement)[] { ("row edge", edge), ("detail center", centerText) })
                {
                    outer.ScrollToVerticalOffset(legacyScroll ? rows.IndexOf(target) : outer.VerticalOffset + container.TranslatePoint(new Point(0, 0), grid).Y - headerHeight); Pump();
                    var before = outer.VerticalOffset;
                    Wheel(wheelTarget, -120); Pump();
                    var distance = outer.VerticalOffset - before;
                    distances.Add(distance);
                    Console.WriteLine($"wheel probe {target.Name} / {area}: offset={before:0.##}->{outer.VerticalOffset:0.##}, expanded={target.IsExpanded}, detail={container.DetailsVisibility}");
                    check(before >= outer.ScrollableHeight - .5 || distance > .5, $"HWND: wheel over {target.Name} {area} moves the shared list when more content exists");
                    var bounds = detail.TransformToAncestor(grid).TransformBounds(new Rect(detail.RenderSize));
                    check(bounds.Bottom > headerHeight && bounds.Top < grid.ActualHeight, $"HWND: one wheel notch on {target.Name} {area} cannot skip the entire expanded detail");
                    check(target.IsExpanded && container.DetailsVisibility == Visibility.Visible && toggle.IsChecked == true, $"HWND: {target.Name} arrow, model and detail agree after wheel over {area}");
                }
                check(Math.Abs(distances[0] - distances[1]) < 1, $"HWND: {target.Name} wheel distance is identical at the edge and center");
                if (target.Fields.Count > 0)
                {
                    var lastField = Descendants<TextBlock>(detail).Last(text => text.Text == target.Fields[^1].Name);
                    lastField.BringIntoView(); Pump();
                    var fieldBounds = lastField.TransformToAncestor(grid).TransformBounds(new Rect(lastField.RenderSize));
                    check(fieldBounds.Bottom > headerHeight && fieldBounds.Top < grid.ActualHeight, $"HWND: final parsed field of {target.Name} is reachable in the common viewport");
                }

                for (var cycle = 0; cycle < 3; cycle++)
                {
                    outer.ScrollToTop(); Pump(); outer.ScrollToEnd(); Pump();
                    grid.ScrollIntoView(target); Pump();
                    var restored = grid.ItemContainerGenerator.ContainerFromItem(target) as DataGridRow;
                    check(target.IsExpanded && restored?.DetailsVisibility == Visibility.Visible && Descendants<DisclosureToggle>(restored).Single().IsChecked == true,
                        $"HWND: {target.Name} retains model, row detail and arrow after complete scroll cycle {cycle + 1}");
                    check(restored is not null && VisibleTexts(restored).Contains(target.Summary), $"HWND: {target.Name} parsed content returns after scroll cycle {cycle + 1}");
                }
                target.IsExpanded = false; Pump();
            }
        }
        finally { host.Close(); Pump(); }
    }

    private sealed class ScrollingFixture
    {
        public ObservableCollection<DiagnosticRow> Diagnostics { get; init; } = [];
        public DiagnosticRow? SelectedDiagnostic { get; set; }
        public bool IsConnected => false;
        public string RawHex { get; set; } = "";
    }

    private static void Wheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent, Source = target });
    }
    private static string[] VisibleTexts(DependencyObject root) => Descendants<TextBlock>(root).Select(text => text.Text).ToArray();
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
