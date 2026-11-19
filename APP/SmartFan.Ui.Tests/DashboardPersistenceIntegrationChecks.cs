using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises the real settings → VM → shell → editor → disk → fresh shell path without starting an HWND or backend.</summary>
internal static class DashboardPersistenceIntegrationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var priorDirectory = ClientPaths.DataDirectory; var priorPortable = ClientPaths.IsPortable;
        var application = Application.Current ?? throw new InvalidOperationException("Persistence checks require the existing offline WPF Application.");
        var resources = application.Resources;
        var priorMerged = resources.MergedDictionaries.ToArray();
        var priorLocal = resources.Keys.Cast<object>().Select(key => new KeyValuePair<object, object>(key, resources[key]!)).ToArray();
        var runRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-runs"));
        var directory = Path.GetFullPath(Path.Combine(runRoot, $"dashboard-persistence-{Guid.NewGuid():N}"));
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Shell? first = null; Shell? second = null;
        var priorContext = SynchronizationContext.Current;
        // A shown WPF window normally establishes this; the offline shell deliberately never creates an HWND.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try
        {
            ClientPaths.Initialize(["--data-dir", directory]);
            var store = new SettingsStore(directory, migrateOldPreferences: false);
            knownFiles.UnionWith([store.SettingsPath, store.SettingsPath + ".bak", Path.Combine(directory, "dashboard-draft.json"), Path.Combine(directory, "dashboard-draft.json.tmp")]);
            var initial = FixtureSettings();
            var untouched = NonDashboardSignature(initial);
            Await(store.SaveAsync(initial));
            var seeded = ReadDashboardJson(store.SettingsPath);
            check(seeded.GetArrayLength() == 10 && Path.GetFullPath(store.SettingsPath).StartsWith(runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "actual settings store seeds ten widgets in a unique application-local test directory instead of production AppData");

            first = CreateShell(store, knownFiles);
            var dashboard = (DashboardView)first.Window.FindName("Dashboard");
            check(first.Vm.Settings.Dashboard.Widgets.Count == 10 && DashboardEditHistory.Equivalent(dashboard.CurrentLayout, initial.Dashboard) && IsDetached(first),
                "VM initialization and the actual MainWindow dashboard binding load all ten exact persisted widgets without an HWND or backend");

            dashboard.BeginEditing();
            var changed = dashboard.TryChangeWidgetKind("persist-clock", "cpu.usage");
            var expected = dashboard.CurrentLayout.Clone();
            check(changed && expected.Widgets.Count == 10 && expected.Widgets.Single(widget => widget.Id == "persist-clock").Kind == "cpu.usage" &&
                  DashboardEditHistory.Equivalent(first.Vm.Settings.Dashboard, initial.Dashboard) && DashboardLayoutEngine.Validate(expected).Count == 0,
                "a valid kind edit changes the real editor draft while preserving the committed ten-widget VM layout until save");

            var committed = Await(dashboard.CommitLayoutAsync());
            Await(first.Window.SaveWindowAsync());
            var written = Await(new SettingsStore(directory, migrateOldPreferences: false).LoadAsync());
            check(committed && !dashboard.IsEditing && written.Warnings.Count == 0 && ReadDashboardJson(store.SettingsPath).GetArrayLength() == 10 &&
                  DashboardEditHistory.Equivalent(written.Settings.Dashboard, expected) && NonDashboardSignature(written.Settings) == untouched,
                "the real MainWindow save callback and exit flush persist the complete ten-widget edit without changing unrelated preferences");

            first.Dispose(); first = null;
            second = CreateShell(new SettingsStore(directory, migrateOldPreferences: false), knownFiles);
            var reopened = (DashboardView)second.Window.FindName("Dashboard");
            check(second.Vm.Settings.Dashboard.Widgets.Count == 10 && DashboardEditHistory.Equivalent(reopened.CurrentLayout, expected) &&
                  DashboardEditHistory.Equivalent(second.Vm.Settings.Dashboard, expected) && IsDetached(second),
                "a fresh VM and actual MainWindow reopen all ten widgets and the saved edit through the same isolated settings path");

            var finalRead = Await(new SettingsStore(directory, migrateOldPreferences: false).LoadAsync());
            check(finalRead.Warnings.Count == 0 && DashboardEditHistory.Equivalent(finalRead.Settings.Dashboard, expected) &&
                  NonDashboardSignature(finalRead.Settings) == untouched && NonDashboardSignature(second.Vm.Settings) == untouched,
                "an independent fresh disk read agrees with the reopened shell and preserves appearance, connection, telemetry, window and profile preferences");
        }
        finally
        {
            try { first?.Dispose(); second?.Dispose(); }
            finally
            {
                try
                {
                    ClientPaths.Initialize(["--data-dir", priorDirectory]);
                    typeof(ClientPaths).GetProperty(nameof(ClientPaths.IsPortable))!.GetSetMethod(nonPublic: true)!.Invoke(null, [priorPortable]);
                    resources.MergedDictionaries.Clear(); resources.Clear();
                    foreach (var dictionary in priorMerged) resources.MergedDictionaries.Add(dictionary);
                    foreach (var pair in priorLocal) resources[pair.Key] = pair.Value;
                    Cleanup(directory, runRoot, knownFiles);
                }
                finally { SynchronizationContext.SetSynchronizationContext(priorContext); }
            }
        }
    }

    private static Shell CreateShell(SettingsStore store, HashSet<string> knownFiles)
    {
        var log = new AppLog(); knownFiles.Add(log.CurrentFilePath);
        var backend = new BackgroundClient($"SmartFan.Persistence.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, store, log);
        var desktop = new DesktopService(log);
        try
        {
            Await(vm.InitializeAsync());
            var window = new SmartFan.App.MainWindow(vm, new AppearanceService(), desktop, log);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1360, 930)); root.Arrange(new Rect(0, 0, 1360, 930)); root.UpdateLayout(); Drain();
            return new(window, vm, backend, desktop, log, knownFiles);
        }
        catch { Await(vm.DisposeAsync().AsTask()); desktop.Dispose(); throw; }
    }
    private static bool IsDetached(Shell shell) => !shell.Window.IsVisible && new WindowInteropHelper(shell.Window).Handle == IntPtr.Zero && !shell.Backend.IsAttached;
    private static JsonElement ReadDashboardJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Dashboard").GetProperty("Widgets").Clone();
    }
    private static string NonDashboardSignature(AppSettings settings)
    {
        var copy = SettingsStore.Clone(settings); copy.Dashboard = new();
        return JsonSerializer.Serialize(copy);
    }
    private static AppSettings FixtureSettings()
    {
        var settings = new AppSettings
        {
            Dashboard = new()
            {
                Widgets =
                [
                    Widget("persist-gpu", "gpu.combined", 4, 0, 4, 3), Widget("persist-ram", "memory.usage", 4, 3, 4, 3),
                    Widget("persist-fans", "laptop.fans", 0, 0, 4, 3), Widget("persist-rpm", "fan.rpm", 0, 3, 4, 5),
                    Widget("persist-control", "fan.control", 8, 3, 5, 5), Widget("persist-cpu", "cpu.combined", 8, 0, 5, 3),
                    Widget("persist-voltage", "cpu.voltage", 4, 6, 4, 2), Widget("persist-vram", "gpu.memory", 13, 0, 3, 3),
                    Widget("persist-clock", "cpu.clock", 13, 3, 3, 2), Widget("persist-gpu-clock", "gpu.clock", 13, 5, 3, 3)
                ]
            },
            Window = new() { Width = Math.Clamp(SystemParameters.WorkArea.Width, 800, 1360), Height = Math.Clamp(SystemParameters.WorkArea.Height, 600, 930), Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop },
            Appearance = new() { ThemeName = "Nebula", AccentColor = "#55AABB", UiScale = 1.05, TemperatureUnit = "F", MemoryUnit = "MiB", PanelOpacity = .86 },
            Connection = new() { AutoReconnect = false, ReconnectInitialSeconds = 4, FilterFanService = false },
            Telemetry = new() { IntervalMs = 2000 }, MinimizeToTray = false, StartWithWindows = false
        };
        settings.Profiles[0].Name = "离线持久化场景"; settings.Profiles[0].ManualPwm = 33;
        return settings;
    }
    private static DashboardWidgetSettings Widget(string id, string kind, int column, int row, int width, int height) =>
        new() { Id = id, Kind = kind, Column = column, Row = row, Width = width, Height = height };

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("Isolated dashboard persistence did not finish while pumping its WPF dispatcher.");
        }
        task.GetAwaiter().GetResult(); Drain();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame); }
    private static void Cleanup(string directory, string runRoot, IEnumerable<string> knownFiles)
    {
        var absolute = Path.GetFullPath(directory);
        if (!absolute.StartsWith(Path.GetFullPath(runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to clean a persistence fixture outside test-runs.");
        foreach (var path in knownFiles)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a file outside this fixture.");
            if (File.Exists(full)) File.Delete(full);
        }
        var logs = Path.Combine(absolute, "frontend-logs");
        if (Directory.Exists(logs) && !Directory.EnumerateFileSystemEntries(logs).Any()) Directory.Delete(logs, recursive: false);
        if (Directory.Exists(absolute) && !Directory.EnumerateFileSystemEntries(absolute).Any()) Directory.Delete(absolute, recursive: false);
    }
    private sealed class Shell(SmartFan.App.MainWindow window, MainViewModel vm, BackgroundClient backend, DesktopService desktop, AppLog log, HashSet<string> knownFiles) : IDisposable
    {
        private bool _disposed;
        public SmartFan.App.MainWindow Window { get; } = window;
        public MainViewModel Vm { get; } = vm;
        public BackgroundClient Backend { get; } = backend;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            knownFiles.Add(log.CurrentFilePath);
            // The plain offline Application has no production App.ExitAsync lifetime to invoke.
            var handler = typeof(SmartFan.App.MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException("MainWindow.OnClosing");
            Window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), Window, handler);
            try { Window.Close(); }
            finally { try { Await(Vm.DisposeAsync().AsTask()); } finally { desktop.Dispose(); } }
        }
    }
}
