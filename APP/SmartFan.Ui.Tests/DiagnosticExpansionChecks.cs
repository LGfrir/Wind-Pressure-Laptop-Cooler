using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Core.Protocol;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises the actual interface-check page, including generated row-detail templates and disclosure controls.</summary>
internal static class DiagnosticExpansionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var application = Application.Current ?? throw new InvalidOperationException("An STA Application is required.");
        var previousDirectory = ClientPaths.DataDirectory;
        var directory = Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", "diagnostic-expansion", Guid.NewGuid().ToString("N"));
        ClientPaths.Initialize(["--data-dir", directory]);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler collect = (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
        application.DispatcherUnhandledException += collect;
        var backend = new BackgroundClient($"SmartFan.UiTests.Expansion.Unattached.{Guid.NewGuid():N}");
        var log = new AppLog();
        var vm = new MainViewModel(backend, new SettingsStore(directory, migrateOldPreferences: false), log);
        vm.InitializeAsync().GetAwaiter().GetResult();
        using var desktop = new DesktopService(log);
        var window = new SmartFan.App.MainWindow(vm, new AppearanceService(), desktop, log);
        try
        {
            var root = window.Content as FrameworkElement ?? throw new InvalidOperationException("Actual shell layout root is missing.");
            var navigation = (FluidTabControl)window.FindName("MainTabs");
            navigation.SelectedIndex = 5; Layout(root); Drain(TimeSpan.FromMilliseconds(330)); Layout(root);
            var view = Descendants<DiagnosticsView>(root).Single();
            var grid = Descendants<DataGrid>(view).Single(table => ReferenceEquals(table.ItemsSource, vm.Diagnostics));
            check(grid.Name == "DiagnosticsGrid" && grid.Items.Count == ProtocolCatalog.All.Count, "actual interface page materializes the protocol rows");
            check(grid.IsReadOnly && grid.Columns.OfType<DataGridTextColumn>().All(column => column.Binding is Binding { Mode: BindingMode.OneWay }), "diagnostic display columns keep immutable model fields OneWay");

            var mode = vm.Diagnostics.Single(row => row.Key == CharacteristicKey.FanMode);
            var pwm = vm.Diagnostics.Single(row => row.Key == CharacteristicKey.FanPwm);
            var rpm = vm.Diagnostics.Single(row => row.Key == CharacteristicKey.FanRpm);
            mode.ApplyPayload(PacketCodec.EncodeInt32(1), "离线读取测试");
            pwm.ApplyPayload(PacketCodec.EncodeByte(20), "离线读取测试");
            vm.SelectedDiagnostic = pwm; Layout(root); Drain();
            var modeRow = Materialize(grid, mode, root);
            var toggle = Descendants<DisclosureToggle>(modeRow).Single();
            var arrow = toggle.Template.FindName("PART_Arrow", toggle) as FrameworkElement;
            check(AutomationProperties.GetName(toggle).Contains(mode.Name, StringComparison.Ordinal), "row disclosure has an accessible interface-specific name");
            check(!mode.IsExpanded && modeRow.DetailsVisibility == Visibility.Collapsed && toggle.IsChecked != true, "unexpanded row starts with collapsed detail and unchecked arrow");
            check(arrow?.RenderTransform is RotateTransform { Angle: 0 }, "disclosure arrow begins pointing right");

            Toggle(toggle); Drain(); Layout(root);
            modeRow = Materialize(grid, mode, root);
            check(mode.IsExpanded && toggle.IsChecked == true && modeRow.DetailsVisibility == Visibility.Visible, $"native disclosure toggle opens the corresponding model and row details (model={mode.IsExpanded}, checked={toggle.IsChecked}, details={modeRow.DetailsVisibility})");
            check(arrow?.RenderTransform is RotateTransform { Angle: 90 }, "expanded disclosure arrow rotates down");
            check(ReferenceEquals(vm.SelectedDiagnostic, pwm) && ReferenceEquals(grid.SelectedItem, pwm), "expanding another interface preserves the selected write target");
            CheckRenderedFields(modeRow, mode, check, "initial expanded payload");

            var previousValues = mode.Fields.Select(field => field.Value).ToArray();
            mode.ApplyPayload(PacketCodec.EncodeInt32(2), "第二次离线读取"); Drain(); Layout(root);
            modeRow = Materialize(grid, mode, root);
            check(!previousValues.SequenceEqual(mode.Fields.Select(field => field.Value)), "a new payload replaces parsed field values");
            check(mode.PayloadSource.StartsWith("第二次离线读取", StringComparison.Ordinal), "updated row details retain the new payload source");
            check(mode.IsExpanded && modeRow.DetailsVisibility == Visibility.Visible && ReferenceEquals(vm.SelectedDiagnostic, pwm), "payload refresh neither collapses details nor changes selection");
            CheckRenderedFields(modeRow, mode, check, "updated expanded payload");

            var pwmRow = Materialize(grid, pwm, root);
            Toggle(Descendants<DisclosureToggle>(pwmRow).Single()); Drain(); Layout(root);
            check(mode.IsExpanded && pwm.IsExpanded, "two interfaces keep independent expanded states");
            modeRow = Materialize(grid, mode, root); pwmRow = Materialize(grid, pwm, root);
            check(modeRow.DetailsVisibility == Visibility.Visible && pwmRow.DetailsVisibility == Visibility.Visible, "two generated row templates can remain expanded together");
            vm.SelectedDiagnostic = rpm; Drain(); Layout(root);
            check(mode.IsExpanded && pwm.IsExpanded && !rpm.IsExpanded, "selecting a third interface does not implicitly expand it or collapse other interfaces");

            modeRow = Materialize(grid, mode, root);
            check(mode.IsExpanded && modeRow.DetailsVisibility == Visibility.Visible && Descendants<DisclosureToggle>(modeRow).Single().IsChecked == true, "previously expanded row keeps its detail and arrow after other rows are selected and expanded");
            Toggle(Descendants<DisclosureToggle>(modeRow).Single()); Drain(); Layout(root);
            modeRow = Materialize(grid, mode, root);
            check(!mode.IsExpanded && modeRow.DetailsVisibility == Visibility.Collapsed && pwm.IsExpanded, "collapsing one interface leaves the other expanded");
            check(ReferenceEquals(vm.SelectedDiagnostic, rpm), "collapse preserves the selected interface");

            pwm.IsExpanded = false;
            rpm.ClearPayload("尚未收到 ESP32 通知");
            var rpmRow = Materialize(grid, rpm, root); Toggle(Descendants<DisclosureToggle>(rpmRow).Single()); Drain(); Layout(root);
            rpmRow = Materialize(grid, rpm, root);
            check(!rpm.HasPayload && !rpm.IsInvalidPayload && rpm.Fields.Count == 0 && rpm.Hex.Length == 0, "notify-only RPM with no notification has no fabricated payload or zero fields");
            check(VisibleTexts(rpmRow).Contains(rpm.Summary) && rpm.Summary.Contains("通知", StringComparison.Ordinal) && rpm.Notes.Contains("不支持 READ", StringComparison.Ordinal), "empty notify details explain the missing notification and READ restriction");
            rpm.ApplyPayload(PacketCodec.EncodeUInt32(0), "ESP32 NOTIFY 离线注入"); Drain(); Layout(root); rpmRow = Materialize(grid, rpm, root);
            check(rpm.HasPayload && !rpm.IsInvalidPayload && rpm.Fields.Count > 0 && rpm.Hex == "00000000", "a received zero-RPM payload is distinguished from no notification");
            CheckRenderedFields(rpmRow, rpm, check, "zero-RPM notification");
            rpm.ClearPayload("连接已断开，等待新通知"); Drain(); Layout(root); rpmRow = Materialize(grid, rpm, root);
            check(rpm.IsExpanded && !rpm.HasPayload && rpm.Fields.Count == 0 && VisibleTexts(rpmRow).Contains("连接已断开，等待新通知"), "clearing payload removes stale fields without closing the explanatory detail");

            mode.IsExpanded = true; mode.ApplyPayload([1], "不合法长度离线注入"); Drain(); Layout(root); modeRow = Materialize(grid, mode, root);
            check(mode.HasPayload && mode.IsInvalidPayload && VisibleTexts(modeRow).Contains(mode.Summary), "invalid payload length shows a visible parse error rather than crashing the row");
            mode.Hex = "GG"; Drain(); Layout(root); modeRow = Materialize(grid, mode, root);
            check(mode.IsInvalidPayload && mode.Fields.Count == 0 && mode.ParseStatus.Contains("HEX", StringComparison.Ordinal) && VisibleTexts(modeRow).Contains(mode.ParseStatus), "invalid HEX clears stale parsed fields and shows the format failure");
            mode.ApplyPayload(PacketCodec.EncodeInt32(0), "修正后的离线读取"); Drain(); Layout(root); modeRow = Materialize(grid, mode, root);
            check(mode.HasPayload && !mode.IsInvalidPayload && mode.Fields.Count > 0, "valid payload recovers the same expanded row after parse failure");
            mode.ClearPayload("尚未读取");
            mode.ApplyPayload(Array.Empty<byte>(), "收到零字节回复"); Drain(); Layout(root); modeRow = Materialize(grid, mode, root);
            check(mode.Hex.Length == 0 && mode.HasPayload && mode.IsInvalidPayload && mode.Summary.Length > 0 && VisibleTexts(modeRow).Contains(mode.Summary), "received zero-byte payload is an explicit parse failure even when Hex was already empty");
            check(mode.PayloadSource.StartsWith("收到零字节回复", StringComparison.Ordinal) && mode.IsExpanded, "zero-byte response retains provenance and current expansion");
            mode.ApplyPayload(PacketCodec.EncodeInt32(1), "空回复后的有效读取"); Drain(); Layout(root); modeRow = Materialize(grid, mode, root);
            check(mode.HasPayload && !mode.IsInvalidPayload && mode.Fields.Count > 0, "valid payload recovers after a received zero-byte response");

            foreach (var size in new[] { new Size(1360, 930), new Size(1060, 720) })
            {
                Layout(root, size.Width, size.Height); Materialize(grid, mode, root, size.Width, size.Height);
                var gridBounds = grid.TransformToAncestor(view).TransformBounds(new Rect(grid.RenderSize));
                var headerHeight = Descendants<DataGridColumnHeader>(grid).Select(header => header.ActualHeight).DefaultIfEmpty(0).Max();
                check(grid.ActualHeight >= headerHeight + modeRow.MinHeight && grid.ActualWidth > 700 && gridBounds.Bottom <= view.ActualHeight + 1 && gridBounds.Right <= view.ActualWidth + 1, $"diagnostic table keeps space for headers and a row inside the shell viewport at {size}");
                var details = Descendants<DataGridDetailsPresenter>(Materialize(grid, mode, root, size.Width, size.Height)).Single();
                check(!Descendants<ScrollViewer>(details).Any() && details.ActualHeight > 0, $"expanded payload belongs to the single table scroll surface at {size}");
                var writeInput = Descendants<TextBox>(view).Single(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path?.Path == "RawHex");
                var writeBounds = writeInput.TransformToAncestor(view).TransformBounds(new Rect(writeInput.RenderSize));
                check(writeBounds.Bottom <= view.ActualHeight + 1 && writeBounds.Right <= view.ActualWidth + 1, $"selected-interface write input remains within the minimum viewport at {size}");
            }
            check(dispatcherErrors.Count == 0, "populated expanded diagnostics produce zero Dispatcher exceptions");
            check(new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !backend.IsAttached, "diagnostic expansion checks open neither an OS window nor a background connection");
        }
        finally
        {
            application.DispatcherUnhandledException -= collect;
            var closing = typeof(SmartFan.App.MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException("MainWindow close handler");
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, closing);
            window.Close(); vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ClientPaths.Initialize(["--data-dir", previousDirectory]);
        }
    }

    private static void CheckRenderedFields(DataGridRow row, DiagnosticRow model, Action<bool, string> check, string label)
    {
        var details = Descendants<DataGridDetailsPresenter>(row).Single();
        var fields = Descendants<ItemsControl>(details).Single(control => ReferenceEquals(control.ItemsSource, model.Fields));
        var text = VisibleTexts(details);
        check(model.HasPayload && !model.IsInvalidPayload && model.Fields.Count > 0 && fields.Items.Count == model.Fields.Count, $"{label}: detail binds the current parsed field collection");
        check(model.Fields.All(field => text.Contains(field.Name) && text.Contains(field.Value) && text.Contains(field.Type) && text.Contains(field.Bytes)), $"{label}: generated detail displays field names, values, types and byte offsets");
        var bindings = Descendants<TextBlock>(details).Select(block => BindingOperations.GetBinding(block, TextBlock.TextProperty)).Where(binding => binding is not null).ToArray();
        check(bindings.Length > 0 && bindings.All(binding => binding?.Mode == BindingMode.OneWay), $"{label}: read-only parsed fields and captions remain OneWay");
    }
    private static DataGridRow Materialize(DataGrid grid, DiagnosticRow model, FrameworkElement root, double width = 1360, double height = 930)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            grid.ScrollIntoView(model); Layout(root, width, height); Drain(); Layout(root, width, height);
            if (grid.ItemContainerGenerator.ContainerFromItem(model) is DataGridRow row) return row;
            var scroller = Descendants<ScrollViewer>(grid).First();
            if (ReferenceEquals(model, grid.Items[grid.Items.Count - 1])) scroller.ScrollToEnd();
            else scroller.ScrollToTop();
            Layout(root, width, height); Drain();
        }
        throw new InvalidOperationException($"Diagnostic row {model.Name} was not generated after bounded layout retries.");
    }
    private static void Toggle(DisclosureToggle button)
    {
        var peer = new ToggleButtonAutomationPeer(button);
        var provider = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider ?? throw new InvalidOperationException("Disclosure must retain native toggle semantics.");
        provider.Toggle();
    }
    private static string[] VisibleTexts(DependencyObject root) => Descendants<TextBlock>(root).Select(block => block.Text).ToArray();
    private static void Layout(FrameworkElement root, double width = 1360, double height = 930)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Drain(TimeSpan? duration = null)
    {
        var frame = new DispatcherFrame();
        if (duration is { } interval)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        }
        else Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T element) yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
