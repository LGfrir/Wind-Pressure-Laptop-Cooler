using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Real shell, diagnostics-window lifetime and rendered-font regression checks; no visible window or hardware.</summary>
internal static class DiagnosticsWindowChecks
{
    public static void Run(Action<bool, string> check)
    {
        var application = Application.Current ?? throw new InvalidOperationException("An STA test Application is required.");
        var previousDirectory = ClientPaths.DataDirectory;
        var directory = Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", "diagnostics", Guid.NewGuid().ToString("N"));
        var hadLocalFont = application.Resources.Keys.Cast<object>().Contains("UiFontFamily");
        var previousFont = application.TryFindResource("UiFontFamily");
        ClientPaths.Initialize(["--data-dir", directory]);
        try
        {
            CheckShellLogIsolation(check);
            CheckTypography(check);
        }
        finally
        {
            if (hadLocalFont) application.Resources["UiFontFamily"] = previousFont;
            else application.Resources.Remove("UiFontFamily");
            ClientPaths.Initialize(["--data-dir", previousDirectory]);
        }
    }

    private static void CheckShellLogIsolation(Action<bool, string> check)
    {
        var log = new AppLog();
        var backend = new BackgroundClient($"SmartFan.UiTests.Diagnostics.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, new SettingsStore(ClientPaths.DataDirectory, migrateOldPreferences: false), log);
        vm.InitializeAsync().GetAwaiter().GetResult();
        using var desktop = new DesktopService(log);
        var window = new SmartFan.App.MainWindow(vm, new AppearanceService(), desktop, log);
        try
        {
            var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("The real shell has no root.");
            var tabs = (FluidTabControl)window.FindName("MainTabs");
            for (var page = 0; page < tabs.Items.Count; page++)
            {
                tabs.SelectedIndex = page;
                Layout(content, 1360, 930); Drain(TimeSpan.FromMilliseconds(350)); Layout(content, 1360, 930);
                var buttons = Descendants<Button>(content).Select(button => button.Content?.ToString() ?? "").ToArray();
                var diagnosticEntryCount = buttons.Count(caption => caption == "运行日志与诊断");
                check(diagnosticEntryCount == (page is 4 or 5 ? 1 : 0), $"actual shell page {page} exposes diagnostics only in advanced settings or interface checks");
                check(!Descendants<Expander>(content).Any(expander => expander.Header?.ToString()?.Contains("日志", StringComparison.Ordinal) == true), $"actual shell page {page} contains no inline log expander");
                check(!buttons.Any(caption => caption.Contains("打开日志目录", StringComparison.Ordinal) || caption.Contains("测量界面流畅度", StringComparison.Ordinal)), $"actual shell page {page} keeps debug utility buttons out of the main layout");
            }
            tabs.SelectedIndex = 0; Layout(content, 1360, 930);
            check(new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !backend.IsAttached, "diagnostics isolation checks create neither an OS window nor a backend connection");
            CheckLogWindow(log, window, check);
        }
        finally
        {
            // The production handler owns the production App lifetime, whereas these tests own a plain Application.
            var close = typeof(SmartFan.App.MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException("MainWindow close handler");
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, close);
            window.Close();
            vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckLogWindow(AppLog log, Window owner, Action<bool, string> check)
    {
        var prefix = $"diagnostics-window-{Guid.NewGuid():N}";
        log.Write($"{prefix}: recent record");
        var subscriptionsBefore = LogSubscriptionCount(log);
        var diagnostics = new LogWindow(log, owner);
        var text = (TextBox)diagnostics.FindName("LogContent");
        var path = (TextBlock)diagnostics.FindName("LogFilePath");
        try
        {
            Layout((FrameworkElement)diagnostics.Content, 1040, 680);
            check(text.Text.Contains($"{prefix}: recent record", StringComparison.Ordinal), "independent diagnostics window immediately shows recent records");
            check(path.Text == log.CurrentFilePath, "independent diagnostics window exposes the actual persisted log path");
            check(LogSubscriptionCount(log) == subscriptionsBefore + 1, "diagnostics subscribes only while its window exists");
            check(!diagnostics.IsVisible && new System.Windows.Interop.WindowInteropHelper(diagnostics).Handle == 0, "log presentation is tested without showing a native window");

            Task.Run(() => log.Write($"{prefix}: worker update")).GetAwaiter().GetResult();
            Drain();
            check(text.Text.Contains($"{prefix}: worker update", StringComparison.Ordinal), "log records from a worker thread refresh on the UI dispatcher");

            // A pending callback must also be harmless if the user closes the diagnostics window immediately.
            log.Write($"{prefix}: queued before close");
            diagnostics.Close();
            var closedText = text.Text;
            Drain();
            log.Write($"{prefix}: after close"); Drain();
            check(text.Text == closedText && !text.Text.Contains($"{prefix}: after close", StringComparison.Ordinal), "closed diagnostics neither applies pending callbacks nor refreshes for later records");
            check(LogSubscriptionCount(log) == subscriptionsBefore, "closing diagnostics releases its AppLog event subscription");

            // Opening a second diagnostics window must show records written while the first was closed.
            var reopened = new LogWindow(log, owner);
            try
            {
                var reopenedText = (TextBox)reopened.FindName("LogContent");
                check(reopenedText.Text.Contains($"{prefix}: after close", StringComparison.Ordinal), "reopened diagnostics loads the current log buffer rather than stale window state");
            }
            finally { reopened.Close(); }
            check(LogSubscriptionCount(log) == subscriptionsBefore, "repeated diagnostics open/close leaves no event subscribers behind");
        }
        finally { diagnostics.Close(); Drain(); }
    }

    private static int LogSubscriptionCount(AppLog log)
    {
        var field = typeof(AppLog).GetField(nameof(AppLog.Added), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("AppLog event backing field");
        return (field.GetValue(log) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void CheckTypography(Action<bool, string> check)
    {
        var application = Application.Current!;
        var label = new TextBlock { Text = "CPU 温度" };
        var heading = new TextBlock { Text = "温度采集", Style = (Style)application.FindResource("SectionTitle") };
        var button = new Button { Content = "应用配置" };
        var metric = new DashboardMetricTile { Title = "CPU 温度", Value = 67.5, ValueFormat = "0.#" };
        var gauge = new DashboardGauge { Value = 2400, Unit = "RPM" };
        var chart = new RpmHistoryChart();
        var editor = new LightConfigEditor(); editor.SetMode(6);
        var panel = new StackPanel();
        foreach (var child in new FrameworkElement[] { label, heading, button, metric, gauge, chart, editor }) panel.Children.Add(child);
        var root = new Border { Child = panel };
        // Application resource changes are broadcast to its Windows. Match production's resource boundary
        // without creating an HWND; an orphan Border does not receive that notification reliably.
        var fontWindow = new Window { Content = root };
        try
        {
        Layout(root, 850, 1800);
        var illustration = Descendants<FrameworkElement>(editor).Single(element => element.GetType().Name == "LightingIllustration");

        check(label.FontWeight == FontWeights.Normal && button.FontWeight == FontWeights.Normal, "body labels and ordinary actions share Normal font weight");
        check(heading.FontWeight == FontWeights.SemiBold, "section headings retain the shared SemiBold visual hierarchy");
        check(Descendants<TextBlock>(metric).All(text => text.FontWeight == FontWeights.Normal), "dashboard metrics emphasize values by size without mixing extra-bold text");
        check(Descendants<TextBox>(editor).All(input => input.FontWeight == FontWeights.Normal), "lighting numeric and RGB editors use the shared regular weight");

        var tabs = new FluidTabControl();
        tabs.Items.Add(new TabItem { Header = "运行概览", Content = new Border() });
        tabs.Items.Add(new TabItem { Header = "灯光配置", Content = new Border() });
        tabs.SelectedIndex = 0; Layout(tabs, 650, 150); Drain();
        var headers = tabs.Items.Cast<TabItem>().ToArray();
        check(headers.All(tab => tab.FontWeight == FontWeights.Normal), "navigation headers have matching weights before selection changes");
        tabs.SelectedIndex = 1; Layout(tabs, 650, 150); Drain();
        check(headers.All(tab => tab.FontWeight == FontWeights.Normal), "navigation selection changes do not make text jump between weights");

        foreach (var family in new[] { "Segoe UI", "Tahoma" })
        {
            var selected = new FontFamily($"{family}, Microsoft YaHei UI, Microsoft YaHei");
            application.Resources["UiFontFamily"] = selected;
            Drain(); Layout(root, 850, 1800);
            check(Descendants<TextBlock>(metric).All(text => text.FontFamily.Source == selected.Source), $"dashboard labels and numbers follow the selected {family} family together (actual: {string.Join(" | ", Descendants<TextBlock>(metric).Select(text => text.FontFamily.Source).Distinct())})");
            check(Descendants<TextBox>(editor).All(input => input.FontFamily.Source == selected.Source), $"lighting inputs follow the selected {family} family including color HEX");
            foreach (var drawing in new FrameworkElement[] { gauge, chart, illustration })
            {
                check(TextElement.GetFontFamily(drawing).Source == selected.Source, $"{drawing.GetType().Name} updates its font resource to {family}");
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(drawing.ActualWidth)), Math.Max(1, (int)Math.Ceiling(drawing.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var glyphs = Glyphs(VisualTreeHelper.GetDrawing(drawing)).Where(run => run.Characters?.Any(char.IsAsciiDigit) == true).ToArray();
                check(glyphs.Length > 0 && glyphs.Any(run => run.GlyphTypeface.FamilyNames.Values.Any(name => name.Equals(family, StringComparison.OrdinalIgnoreCase))), $"{drawing.GetType().Name} actually renders numeric glyphs with the selected {family} family");
                check(glyphs.All(run => run.GlyphTypeface.Weight == FontWeights.Normal), $"{drawing.GetType().Name} draws numeric glyphs at the shared regular weight");
            }
        }
        check(!fontWindow.IsVisible && new System.Windows.Interop.WindowInteropHelper(fontWindow).Handle == 0, "dynamic font and rendered glyph checks create no visible window or HWND");
        }
        finally { fontWindow.Close(); }
    }

    private static IEnumerable<GlyphRun> Glyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var run in Glyphs(child)) yield return run;
    }

    private static void Layout(FrameworkElement content, double width, double height)
    {
        FrameworkElement root = content;
        while (VisualTreeHelper.GetParent(root) is FrameworkElement parent && parent is not Window) root = parent;
        if (ReferenceEquals(root, content) && LogicalTreeHelper.GetParent(root) is null && root is not Border) root = new Border { Child = content };
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }

    private static void Drain(TimeSpan? duration = null)
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

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
