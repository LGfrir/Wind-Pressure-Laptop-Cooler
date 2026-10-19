using System.IO;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.Core.Protocol;
using SmartFan.Core.Ble;
using SmartFan.App.Infrastructure;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Offline STA checks: no production App startup, hardware monitor, BLE scan, or visible OS window.</summary>
internal static class Program
{
    private static int _checks;
    private static Application _application => Application.Current ?? throw new InvalidOperationException("Test Application has not been initialized.");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Contains("--adaptive-edges-only"))
            {
                SetTheme("Light"); AdaptiveEdgeRegressionChecks.Run(Check);
                Console.WriteLine($"PASS {_checks} adaptive edge assertions."); return 0;
            }
            if (args.Contains("--compact-fan-only"))
            {
                SetTheme("Light"); CompactFanControlChecks.Run(Check);
                Console.WriteLine($"PASS {_checks} compact fan control assertions."); return 0;
            }
            if (args.Contains("--diagnostic-scroll"))
            {
                SetTheme("Light"); DiagnosticScrollChecks.Run(Check, args.Contains("--legacy-scroll"));
                Console.WriteLine($"PASS {_checks} diagnostic HWND scrolling assertions."); return 0;
            }
            CheckReadonlyRunNegativeControl();
            CheckThemes(); CheckEditorBindings(); CheckSceneIsolation(); CheckCurve(); CheckNavigation(); CheckScrolling();
            var captureIndex = Array.IndexOf(args, "--render");
            if (captureIndex >= 0)
            {
                if (captureIndex + 1 >= args.Length) throw new ArgumentException("--render requires an output directory.");
                CaptureExamples(Path.GetFullPath(args[captureIndex + 1]));
            }
            var renderDirectory = captureIndex >= 0 ? Path.GetFullPath(args[captureIndex + 1]) : null;
            CheckDashboard(); CheckShell(renderDirectory); DashboardPresentationChecks.Run(Check);
            DataFreshnessAndDraftChecks.Run(Check); MetricFreshnessRegressionChecks.Run(Check);
            DiagnosticsWindowChecks.Run(Check); SensorInspectorChecks.Run(Check); InputsMotionChecks.Run(Check); SensorSelectionChecks.Run(Check); FanSummaryChecks.Run(Check);
            WidgetWorkspaceChecks.Run(Check, renderDirectory); WidgetDragChecks.Run(Check); WidgetComposerChecks.Run(Check, renderDirectory); WidgetEditingChecks.Run(Check, renderDirectory);
            WidgetBindingRegressionChecks.Run(Check);
            SettingsStorageLocationChecks.Run(Check);
            CpuVoltagePresentationChecks.Run(Check);
            AgentLaunchPolicyChecks.Run(Check);
            DashboardPersistenceIntegrationChecks.Run(Check);
            AdaptiveWidgetChecks.Run(Check, renderDirectory); AdaptiveEdgeRegressionChecks.Run(Check); DenseDashboardChecks.Run(Check, renderDirectory);
            FanModeAndLiveSensorChecks.Run(Check); DiagnosticExpansionChecks.Run(Check); DiagnosticScrollChecks.Run(Check);
            CompactFanControlChecks.Run(Check);
            Console.WriteLine($"PASS {_checks} offline WPF assertions (themes, lighting forms, scene isolation, curve, navigation, scrolling, dashboard, actual shell, populated scan rows).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current?.Shutdown(); }
    }

    private static void SetTheme(string theme)
    {
        _application.Resources.MergedDictionaries.Clear();
        foreach (var resource in new[] { $"Themes/{theme}.xaml", "Themes/Controls.xaml", "Themes/Motion.xaml", "Themes/Inputs.xaml", "Resources/Strings.zh-CN.xaml", "Resources/Brand.xaml" })
            _application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/SmartFan;component/{resource}") });
    }

    private static void CheckThemes()
    {
        foreach (var theme in new[] { "Nebula", "Light", "Cyber", "Graphite" })
        {
            SetTheme(theme);
            Check(_application.TryFindResource("TextBrush") is Brush, $"{theme} readable text resource");
            Check(_application.TryFindResource(typeof(FluidTabControl)) is Style, $"{theme} motion navigation style");
            var form = new LightConfigEditor(); form.SetMode(3); Layout(form, 850);
            Check(Descendants<Slider>(form).All(slider => slider.Template is not null), $"{theme} slider templates instantiated");
            var combo = new ComboBox { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0 };
            Layout(combo, 300, 48); Check(combo.ApplyTemplate() || combo.Template is not null, $"{theme} combo template instantiated");
        }
        SetTheme("Nebula");
        var createHue = typeof(ColorInput).GetMethod("CreateHueTemplate", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("ColorInput hue template factory");
        Check(createHue.Invoke(null, null) is ControlTemplate, "HSV palette native template parses at runtime");
    }

    private static void CheckReadonlyRunNegativeControl()
    {
        var device = new BleDeviceInfo { Name = "negative control", BluetoothAddress = 0x84C7BB566A16, SignalStrength = -42 };
        var text = new TextBlock(); var run = new Run(); text.Inlines.Add(run);
        Exception? failure = null;
        DispatcherUnhandledExceptionEventHandler capture = (_, e) => { failure = e.Exception; e.Handled = true; };
        _application.DispatcherUnhandledException += capture;
        try
        {
            // This is the formerly failing template behavior: Run.Text defaults to TwoWay, Address is computed/read-only.
            BindingOperations.SetBinding(run, Run.TextProperty, new Binding(nameof(BleDeviceInfo.Address)) { Source = device });
            Layout(text, 400, 40); DrainDispatcher();
        }
        catch (InvalidOperationException ex) { failure = ex; }
        finally { BindingOperations.ClearAllBindings(run); _application.DispatcherUnhandledException -= capture; }
        Check(failure is InvalidOperationException && failure.Message.Contains(nameof(BleDeviceInfo.Address), StringComparison.Ordinal), "negative control reproduces read-only Address failure from default Run.Text binding");
        Console.WriteLine("Expected negative control: default Run.Text binding fails for read-only BleDeviceInfo.Address.");
    }

    private static void CheckEditorBindings()
    {
        var editor = new LightConfigEditor();
        for (var mode = 1; mode <= 8; mode++)
        {
            editor.SetMode(mode); var first = editor.GetPayload();
            PacketCodec.Validate(PacketCodec.ConfigurationKey((LightMode)mode), first);
            editor.LoadPayload(first); Check(first.SequenceEqual(editor.GetPayload()), $"mode {mode} wire/form roundtrip");
            editor.AcceptApplied();
        }
        foreach (var mode in new[] { LightMode.CustomBreath, LightMode.Chase })
        for (var size = 1; size <= 9; size++)
        {
            var cfg = new LightingConfiguration
            {
                Mode = mode,
                Colors = Enumerable.Range(0, size).Select(i => new RgbColor((byte)(i * 24), (byte)(255 - i * 20), (byte)(i * 15))).ToList(),
                FadeMilliseconds = Enumerable.Range(0, size).Select(i => (ushort)(1 + i * 8191)).ToList()
            };
            var payload = PacketCodec.EncodeLighting(cfg); editor.SetMode((int)mode); editor.LoadPayload(payload);
            Check(payload.SequenceEqual(editor.GetPayload()), $"{mode} size {size} strict color/duration binding");
        }
        editor.SetMode(1); editor.LoadPayload([1, 2, 3]); Layout(editor, 850);
        var color = Descendants<ColorInput>(editor).First(); var hex = Descendants<TextBox>(color).First();
        hex.Text = "#ABCDEF"; Check(editor.GetPayload().SequenceEqual(new byte[] { 171, 205, 239 }), "hex color updates canonical RGB");
        int applies = 0; editor.ApplyRequested += (_, _) => applies++;
        Click(editor, "应用到设备"); Check(applies == 1, "valid apply event");
        hex.Text = "#GGGGGG"; Click(editor, "应用到设备"); Check(applies == 1, "invalid color cannot apply");
        Click(editor, "撤销修改"); Check(editor.GetPayload().SequenceEqual(new byte[] { 1, 2, 3 }), "undo restores baseline");
        Check(!ColorInput.TryParse("##112233", out _), "invalid double color prefix rejected");
        Layout(editor, 850);
        var channels = Descendants<TextBox>(Descendants<ColorInput>(editor).First()).ToArray();
        channels[1].Text = "256"; Click(editor, "应用到设备"); Check(applies == 1, "invalid RGB channel cannot apply");
        channels[1].Text = "42"; Check(editor.GetPayload()[0] == 42, "RGB channel synchronizes hex and payload");
        editor.SetMode(0); Layout(editor, 850); Check(Descendants<Button>(editor).All(button => !button.IsEnabled), "off actions disabled");

        var listEditor = new LightConfigEditor(); listEditor.SetMode(6); int saved = 0;
        listEditor.SaveDraftRequested += (_, _) => saved++;
        for (var i = 0; i < 12; i++) { Layout(listEditor, 850); FindButton(listEditor, caption => caption.Contains("添加颜色")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        Check(listEditor.GetPayload()[0] == 9, "color count capped at nine"); Layout(listEditor, 850);
        Check(!FindButton(listEditor, caption => caption.Contains("添加颜色")).IsEnabled, "add disabled at maximum");
        for (var i = 0; i < 12; i++) { Layout(listEditor, 850); Click(listEditor, "×"); }
        Check(listEditor.GetPayload()[0] == 1, "color count cannot drop below one"); Layout(listEditor, 850);
        Click(listEditor, "保存到场景"); Check(saved == 1, "valid offline draft save event");
        listEditor.SetMode(2); Layout(listEditor, 850); Descendants<TextBox>(listEditor).First().Text = "0";
        Click(listEditor, "保存到场景"); Check(saved == 1, "zero duration cannot save");
    }

    private static void CheckSceneIsolation()
    {
        var editor = new LightConfigEditor();
        var first = new byte[] { 10, 20, 30 }; var second = new byte[] { 90, 80, 70 };
        editor.LoadSceneDrafts(new Dictionary<int, byte[]> { [1] = first, [2] = [100, 0] });
        first[0] = 255; Check(editor.GetPayload()[0] == 10, "scene input buffers cloned");
        Layout(editor, 850); Descendants<TextBox>(Descendants<ColorInput>(editor).First()).First().Text = "#112233";
        editor.SetMode(2); editor.SetMode(1); Check(editor.GetPayload().SequenceEqual(new byte[] { 17, 34, 51 }), "mode switches retain current-scene drafts");
        editor.LoadSceneDrafts(new Dictionary<int, byte[]> { [1] = second }); Check(editor.GetPayload().SequenceEqual(second), "scene switch replaces selected mode");
        editor.SetMode(2); Check(!editor.GetPayload().SequenceEqual(new byte[] { 100, 0 }), "absent mode cannot leak from old scene");
        editor.SetMode(1); Layout(editor, 850); Descendants<TextBox>(Descendants<ColorInput>(editor).First()).First().Text = "#AABBCC"; Click(editor, "撤销修改");
        Check(editor.GetPayload().SequenceEqual(second), "undo belongs to new scene");
        bool rejected = false;
        try { editor.LoadSceneDrafts(new Dictionary<int, byte[]> { [1] = [1, 2, 3], [2] = [0, 0] }); } catch (ArgumentException) { rejected = true; }
        Check(rejected && editor.GetPayload().SequenceEqual(second), "invalid scene replacement is atomic");
        editor.ClearDrafts(); Check(!editor.GetPayload().SequenceEqual(second), "clear removes scene baselines and drafts");
        int successDuringEvent = 0;
        editor.SaveDraftRequested += (_, _) => { if (Descendants<TextBlock>(editor).Any(text => text.Text == "草稿已保存到当前场景。")) successDuringEvent++; editor.ShowStatus("模拟保存失败", true); };
        Layout(editor, 850); Click(editor, "保存到场景");
        Check(successDuringEvent == 0 && Descendants<TextBlock>(editor).Any(text => text.Text == "模拟保存失败"), "editor never overwrites persistence result with premature success");
    }

    private static void CheckCurve()
    {
        var curve = new FanCurveEditor();
        curve.SetPoints([new() { TemperatureCelsius = 35, TargetRpm = 800 }, new() { TemperatureCelsius = 60, TargetRpm = 2600 }, new() { TemperatureCelsius = 85, TargetRpm = 4500 }]);
        Check(curve.Points.Count == 3, "curve binding");
        var detached = curve.Points; detached[0].TargetRpm = 999; Check(curve.Points[0].TargetRpm == 800, "curve output is detached");
        bool rejected = false; try { curve.SetPoints([new() { TemperatureCelsius = 50, TargetRpm = 1000 }, new() { TemperatureCelsius = 50, TargetRpm = 2000 }]); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "curve duplicate temperatures rejected");
        Layout(curve, 850); Click(curve, "＋ 添加控制点"); Check(curve.Points.Count == 4, "curve add action updates points");
        Layout(curve, 850); Click(curve, "移除"); Check(curve.Points.Count == 3, "curve remove action updates points");
    }

    private static void CheckNavigation()
    {
        var navigation = new FluidTabControl();
        navigation.Items.Add(new TabItem { Header = "概览", Content = new TextBlock { Text = "overview" } });
        navigation.Items.Add(new TabItem { Header = "灯光与效果", Content = new TextBlock { Text = "lights" } });
        navigation.Items.Add(new TabItem { Header = "温控", Content = new TextBlock { Text = "curve" } });
        navigation.SelectedIndex = 0; Layout(navigation, 850, 400); DrainDispatcher();
        var indicator = navigation.Template.FindName("PART_Indicator", navigation) as Border;
        var rail = navigation.Template.FindName("PART_NavRail", navigation) as FrameworkElement;
        var page = navigation.Template.FindName("PART_Page", navigation) as FrameworkElement;
        Check(indicator is not null && rail is not null && page is not null, "motion template parts resolved at runtime");
        navigation.SelectedIndex = 1; Layout(navigation, 850, 400); DrainDispatcher(TimeSpan.FromMilliseconds(380)); Layout(navigation, 850, 400);
        var selected = navigation.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement ?? throw new InvalidOperationException("selected tab container");
        Check(navigation.SelectedContent is TextBlock text && text.Text == "lights", "navigation swaps business content");
        Check(indicator is not null && Math.Abs(indicator.ActualWidth - selected.ActualWidth) < 1, $"selection background follows header width (actual {indicator?.ActualWidth}, selected {selected.ActualWidth}, visible {selected.IsVisible})");
        Check(indicator?.RenderTransform is TranslateTransform shift && rail is not null && Math.Abs(shift.X - selected.TranslatePoint(new Point(), rail).X) < 1, "selection background follows header location");
    }

    private static void CheckScrolling()
    {
        var viewer = new SmoothScrollViewer { Content = new Border { Height = 1800 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Layout(viewer, 600, 280); DrainDispatcher(); Layout(viewer, 600, 280);
        Check(viewer.ScrollableHeight > 1000, "smooth viewer has real scroll extent");
        viewer.AnimatedOffset = 150; Layout(viewer, 600, 280); Check(Math.Abs(viewer.VerticalOffset - 150) < 1, "animated offset reaches scroll viewer");
        viewer.AnimatedOffset = viewer.ScrollableHeight + 1000; Layout(viewer, 600, 280);
        Check(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 1, "offset is clamped by content bounds");
        viewer.AnimatedOffset = 0; Layout(viewer, 600, 280);
        var handler = typeof(SmoothScrollViewer).GetMethod("OnPreviewMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException("mouse wheel handler");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer };
        handler.Invoke(viewer, [wheel]); DrainDispatcher(TimeSpan.FromMilliseconds(340)); Layout(viewer, 600, 280);
        Check(viewer.VerticalOffset > 0, "wheel scroll moves content with animation or OS fallback");
    }

    private static void CheckDashboard()
    {
        Check(DashboardWidgetRegistry.Available.Select(item => item.Key).Distinct().Count() == DashboardWidgetRegistry.Available.Count, "widget keys are unique");
        Check(DashboardWidgetRegistry.Create("cpu.temperature") is DashboardMetricTile && DashboardWidgetRegistry.Create("cpu.usage") is DashboardMetricTile, "CPU metrics can be composed independently");
        var combined = DashboardWidgetRegistry.Create("cpu.combined"); Layout(combined, 350, 110);
        Check(Descendants<DashboardMetricTile>(combined).Count() == 2, "CPU combined widget reuses standalone metrics");
        var gauge = new DashboardGauge { AutoScale = true, Value = 12345 };
        Check(gauge.DisplayMaximum > 12345, "RPM gauge scale adapts without a hardware RPM limit");
        gauge.Value = double.NaN; Check(double.IsFinite(gauge.DisplayMaximum), "unavailable gauge values keep scale finite");
        var dashboard = new DashboardView(); Layout(dashboard, 1004, 445);
        Check(!Descendants<ScrollViewer>(dashboard).Any(), "homepage contains no scrolling container");
        Check(Descendants<DashboardWidgetHost>(dashboard).Count() == 5, "homepage composes metric groups and a separate laptop-fan summary");
        CheckDashboardDataBindings();
        var editor = new LightConfigEditor(); editor.SetMode(3); Layout(editor, 850);
        var illustration = Descendants<FrameworkElement>(editor).First(element => element.GetType().Name == "LightingIllustration");
        var subscribed = illustration.GetType().GetField("_subscribed", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(subscribed?.GetValue(illustration) is false, "unshown lighting illustration never subscribes to continuous rendering");
    }

    private static void CheckDashboardDataBindings()
    {
        var data = new DashboardTelemetryData
        {
            CpuTemperature = 67.5, CpuUsage = 43, GpuTemperature = 59, GpuUsage = 21,
            MemoryUsed = 10, MemoryTotal = 32, FanRpm = 2400
        };
        // Widgets consume their explicit presentation contract even if the surrounding page has an unrelated model.
        var host = new DashboardWidgetHost { WidgetKey = "cpu.combined", Metrics = data, DataContext = new { CpuTemperature = 999d } };
        Layout(host, 350, 110); DrainDispatcher();
        var tiles = Descendants<DashboardMetricTile>(host).ToArray();
        var temperature = tiles.Single(tile => tile.Title == "CPU 温度");
        var usage = tiles.Single(tile => tile.Title == "CPU 使用率");
        Check(temperature.Value == 67.5 && usage.Value == 43, "combined CPU widget consumes only its explicit metric data");
        Check(BindingOperations.GetBinding(temperature, DashboardMetricTile.ValueProperty)?.Mode == BindingMode.OneWay, "dashboard metrics never write back to sample data");

        data.CpuTemperature = null; data.CpuTemperatureStatus = "当前处理器尚未提供可读取的温度传感器。";
        DrainDispatcher(); Layout(host, 350, 110);
        Check(temperature.Value is null && Descendants<TextBlock>(temperature).Any(text => text.Text == "—"), "missing CPU temperature stays unavailable rather than showing fabricated data");
        Check(temperature.ToolTip?.ToString()?.Contains(data.CpuTemperatureStatus) == true &&
              !Descendants<TextBlock>(temperature).Any(text => text.Text == data.CpuTemperatureStatus),
            "missing temperature keeps its actual sensor diagnostic in the tooltip while the home card stays concise");
        data.CpuTemperature = 153.5; data.TemperatureUnit = "°F"; data.TemperatureMaximum = 212;
        DrainDispatcher(); Layout(host, 350, 110);
        Check(temperature.Value == 153.5 && temperature.Unit == "°F" && temperature.Maximum == 212 && usage.Unit == "%", "temperature units and gauge range update independently of usage");

        var memory = new DashboardWidgetHost { WidgetKey = "memory.usage", Metrics = data };
        Layout(memory, 300, 110); DrainDispatcher();
        var memoryTile = Descendants<DashboardMetricTile>(memory).Single();
        Check(memoryTile.Value == 10 && memoryTile.TotalValue == 32 && memoryTile.Unit == "GiB", "memory component receives explicit values and units");
        data.MemoryUsed = 10240; data.MemoryTotal = 32768; data.MemoryUnit = "MiB"; data.MemoryValueFormat = "0";
        DrainDispatcher(); Layout(memory, 300, 110);
        Check(memoryTile.Value == 10240 && memoryTile.TotalValue == 32768 && memoryTile.Unit == "MiB" && memoryTile.ValueFormat == "0", "memory unit changes update existing widgets");
        Check(Descendants<TextBlock>(memoryTile).Any(text => text.Text == "10240"), "MiB memory value uses the selected numeric precision");

        var replacement = new DashboardTelemetryData { CpuTemperature = 55, CpuUsage = 12 };
        host.Metrics = replacement; DrainDispatcher(); Layout(host, 350, 110);
        data.CpuTemperature = 180; DrainDispatcher();
        Check(temperature.Value == 55 && usage.Value == 12 && temperature.Unit == "°C", "replacing a widget data source detaches the old sample object");
        host.WidgetKey = "fan.rpm"; host.Metrics = data; Layout(host, 180, 150); DrainDispatcher();
        Check(Descendants<DashboardGauge>(host).Single().Value == 2400, "switching widget type preserves explicit RPM data binding");
        host.Metrics = null; DrainDispatcher();
        Check(Descendants<DashboardGauge>(host).Single().Value is null, "clearing a widget source clears its displayed RPM");

        var dashboard = new DashboardView { Metrics = data }; Layout(dashboard, 1004, 445); DrainDispatcher();
        Check(Descendants<DashboardWidgetHost>(dashboard).All(widget => ReferenceEquals(widget.Metrics, data)), "dashboard composition forwards one explicit metric source to every widget");
    }

    private static void CheckShell(string? renderDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", Guid.NewGuid().ToString("N"));
        ClientPaths.Initialize(["--data-dir", directory]);
        foreach (var theme in new[] { "Nebula", "Light" })
        {
            SetTheme(theme);
            var backend = new BackgroundClient($"SmartFan.UiTests.Unattached.{Guid.NewGuid():N}");
            var vm = new MainViewModel(backend, new SettingsStore(directory, migrateOldPreferences: false), new AppLog());
            vm.InitializeAsync().GetAwaiter().GetResult(); vm.Settings.Appearance.ThemeName = theme;
            using var desktop = new DesktopService(new AppLog());
            var window = new SmartFan.App.MainWindow(vm, new AppearanceService(), desktop, new AppLog());
            try
            {
                var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("Actual shell has no layout root.");
                LayoutShell(content, 1360, 930);
                var background = window.Background as SolidColorBrush;
                var expected = _application.FindResource("BackgroundBrush") as SolidColorBrush;
                var foreground = window.Foreground as SolidColorBrush;
                Check(background is not null && expected is not null && background.Color == expected.Color, $"{theme} actual MainWindow background follows theme");
                Check(foreground is not null && background is not null && Contrast(foreground.Color, background.Color) >= 4.5, $"{theme} actual shell text/background contrast");
                Check(new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !backend.IsAttached, "shell layout test opens no OS window or backend connection");
                var tabs = (FluidTabControl)window.FindName("MainTabs");
                var dashboard = (DashboardView)window.FindName("Dashboard");
                foreach (var size in new[] { new Size(1360, 930), new Size(1060, 720) })
                {
                    LayoutShell(content, size.Width, size.Height);
                    Check(dashboard.ActualHeight > 0 && dashboard.ActualWidth > 0, $"{theme} dashboard participates in actual shell layout at {size}");
                    foreach (var widget in Descendants<DashboardWidgetHost>(dashboard))
                    {
                        var bounds = widget.TransformToAncestor(dashboard).TransformBounds(new Rect(widget.RenderSize));
                        Check(bounds.Bottom <= dashboard.ActualHeight + 1 && bounds.Right <= dashboard.ActualWidth + 1, $"{theme} dashboard widget stays inside viewport at {size}");
                    }
                }
                LayoutShell(content, 1360, 930);
                if (theme == "Nebula" && renderDirectory is not null) CaptureShell(window, content, Path.Combine(renderDirectory, "shell-overview-offline.png"));
                tabs.SelectedIndex = 1; LayoutShell(content, 1360, 930); DrainDispatcher(TimeSpan.FromMilliseconds(350)); LayoutShell(content, 1360, 930);
                Check(tabs.SelectedIndex == 1 && Descendants<LightConfigEditor>(content).Any(), "actual shell navigation shows dedicated lighting editor");
                if (theme == "Nebula" && renderDirectory is not null) CaptureShell(window, content, Path.Combine(renderDirectory, "shell-lighting-offline.png"));
                Check(!vm.StatusText.Contains("外观或系统设置失败", StringComparison.Ordinal), "actual shell appearance initialization succeeded");
                CheckAdvancedScanRows(window, content, tabs, vm, theme);
            }
            finally
            {
                // The production close handler owns App lifetime; this test owns a plain Application instead.
                var close = typeof(SmartFan.App.MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException("MainWindow close handler");
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, close);
                window.Close(); vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        SetTheme("Nebula");
    }
    private static void CheckAdvancedScanRows(SmartFan.App.MainWindow window, FrameworkElement content, FluidTabControl tabs, MainViewModel vm, string theme)
    {
        var dispatcherFailures = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, e) => { dispatcherFailures.Add(e.Exception); e.Handled = true; };
        _application.DispatcherUnhandledException += capture;
        try
        {
            tabs.SelectedIndex = 4; LayoutShell(content, 1360, 930); DrainDispatcher(TimeSpan.FromMilliseconds(350)); LayoutShell(content, 1360, 930);
            var scanList = Descendants<ListBox>(content).Single(list => ReferenceEquals(list.ItemsSource, vm.Devices));
            var batches = new BleDeviceInfo[][]
            {
                [new() { Id = "scan-esp", Name = "散热底座 · ESP32-S3", BluetoothAddress = 0x84C7BB566A16, SignalStrength = -42, AdvertisesFanService = true },
                 new() { Id = "scan-other", Name = "测试蓝牙设备", BluetoothAddress = 0x123456789ABC, SignalStrength = -67 }],
                [new() { Id = "scan-updated", Name = "第二轮扫描结果", BluetoothAddress = 0xA1B2C3D4E5F6, SignalStrength = -53, AdvertisesFanService = true }],
                [new() { Id = "scan-esp", Name = "散热底座 · 已重新发现", BluetoothAddress = 0x84C7BB566A16, SignalStrength = -35, AdvertisesFanService = true },
                 new() { Id = "scan-fresh", Name = "另一台设备", BluetoothAddress = 0x102030405060, SignalStrength = -81 }]
            };
            for (var wave = 0; wave < batches.Length; wave++)
            {
                vm.Devices.Clear(); foreach (var device in batches[wave]) vm.Devices.Add(device);
                LayoutShell(content, 1360, 930); DrainDispatcher(); LayoutShell(content, 1360, 930);
                Check(scanList.Items.Count == batches[wave].Length, $"{theme} advanced scan wave {wave + 1} replaces results");
                foreach (var device in batches[wave])
                {
                    scanList.ScrollIntoView(device); LayoutShell(content, 1360, 930);
                    var row = scanList.ItemContainerGenerator.ContainerFromItem(device) as ListBoxItem;
                    Check(row is not null, $"{theme} actual advanced scan template materializes {device.Name}");
                    var visible = row is null ? [] : Descendants<TextBlock>(row).Select(block => new TextRange(block.ContentStart, block.ContentEnd).Text).ToArray();
                    Check(visible.Contains(device.Name, StringComparer.Ordinal), $"{theme} scan row renders device name after wave {wave + 1}");
                    var detail = visible.FirstOrDefault(value => value.Contains(device.Address, StringComparison.Ordinal));
                    Check(detail is not null && detail.Contains(device.SignalStrength.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) && detail.Contains("dBm", StringComparison.Ordinal), $"{theme} scan row renders read-only Address and RSSI after wave {wave + 1} (rendered: {string.Join(" | ", visible)})");
                }
                Check(dispatcherFailures.Count == 0, $"{theme} populated advanced scan wave {wave + 1} has no Dispatcher binding exceptions");
            }
            tabs.SelectedIndex = 5; LayoutShell(content, 1360, 930); DrainDispatcher(TimeSpan.FromMilliseconds(350)); LayoutShell(content, 1360, 930);
            var diagnostics = Descendants<DataGrid>(content).Single(grid => ReferenceEquals(grid.ItemsSource, vm.Diagnostics));
            Check(diagnostics.Items.Count == ProtocolCatalog.All.Count, $"{theme} diagnostics page materializes real read-only model rows");
            var first = vm.Diagnostics[0]; diagnostics.ScrollIntoView(first); LayoutShell(content, 1360, 930);
            var diagnosticRow = diagnostics.ItemContainerGenerator.ContainerFromItem(first) as DataGridRow;
            Check(diagnosticRow is not null && Descendants<TextBlock>(diagnosticRow).Any(block => block.Text == first.Name), $"{theme} diagnostics renders computed read-only Name");
            Check(dispatcherFailures.Count == 0, $"{theme} populated advanced/diagnostics pages have zero Dispatcher exceptions");
        }
        finally { _application.DispatcherUnhandledException -= capture; vm.Devices.Clear(); }
    }
    private static void LayoutShell(FrameworkElement content, double width, double height)
    { content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); DrainDispatcher(); }
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) { var normalized = value / 255d; return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var first = Luminance(a); var second = Luminance(b); return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static void CaptureShell(Window window, FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap(1360, 930, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual(); using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, 1360, 930));
        bitmap.Render(backdrop); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void CaptureExamples(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var mode in new[] { 6, 8 })
        {
            var editor = new LightConfigEditor(); editor.SetMode(mode); editor.ShowStatus("本地示例草稿 · 未连接硬件");
            var background = new Border { Padding = new Thickness(28), Background = (Brush)_application.FindResource("BackgroundBrush"), Child = editor };
            Layout(background, 960);
            var bitmap = new RenderTargetBitmap(960, (int)Math.Ceiling(background.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(background);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"lighting-form-{mode}.png")); encoder.Save(stream);
        }
        Console.WriteLine($"Control renders: {directory} (illustrative offline control renders, not production GUI acceptance)");
    }

    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException($"FAIL: {name}"); _checks++; }
    private static void Layout(FrameworkElement control, double width, double? height = null)
    {
        // Orphan Controls do not all enter the initialized state; a logical parent exercises real implicit styles.
        FrameworkElement host = control;
        while (VisualTreeHelper.GetParent(host) is FrameworkElement parent) host = parent;
        if (ReferenceEquals(host, control)) host = new Border { Child = control };
        host.Measure(new Size(width, height ?? double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, height ?? Math.Max(host.DesiredSize.Height, 210))); host.UpdateLayout();
    }
    private static void DrainDispatcher(TimeSpan? duration = null)
    {
        var frame = new DispatcherFrame();
        if (duration is { } wait)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = wait };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start();
        }
        else Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    private static Button FindButton(DependencyObject control, Func<string, bool> predicate) => Descendants<Button>(control).First(button => button.Content is string text && predicate(text));
    private static void Click(DependencyObject control, string label) => FindButton(control, caption => caption == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
