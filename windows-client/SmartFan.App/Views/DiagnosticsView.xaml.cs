using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;

namespace SmartFan.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() { InitializeComponent(); }
    public event RoutedEventHandler? LogsRequested;
    public event RoutedEventHandler? ExportLogsRequested;
    private void OpenLogs(object sender, RoutedEventArgs e) => LogsRequested?.Invoke(this, e);
    private void ExportLogs(object sender, RoutedEventArgs e) => ExportLogsRequested?.Invoke(this, e);

    private void DisclosureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not DisclosureToggle { IsChecked: true } toggle) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (toggle.IsChecked != true || ItemsControl.ContainerFromElement(DiagnosticsGrid, toggle) is not DataGridRow row) return;
            DiagnosticsGrid.UpdateLayout();
            var viewport = Descendants<ScrollContentPresenter>(DiagnosticsGrid).FirstOrDefault();
            var scroller = Descendants<ScrollViewer>(DiagnosticsGrid).FirstOrDefault();
            if (viewport is null || scroller is null) return;
            var top = row.TranslatePoint(new Point(0, 0), viewport).Y;
            if (top < 0 || top + row.ActualHeight > viewport.ActualHeight)
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

