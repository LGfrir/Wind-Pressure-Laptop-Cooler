using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises the real sensor inspector with an injected backend callback and no hardware or visible window.</summary>
internal static class SensorInspectorChecks
{
    public static void Run(Action<bool, string> check)
    {
        var previousDirectory = ClientPaths.DataDirectory;
        ClientPaths.Initialize(["--data-dir", Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", "sensors", Guid.NewGuid().ToString("N"))]);
        try { CheckRowsAndRefresh(check); CheckPendingClose(check); }
        finally { ClientPaths.Initialize(["--data-dir", previousDirectory]); }
    }

    private static void CheckRowsAndRefresh(Action<bool, string> check)
    {
        var sample = new SensorDescriptor[]
        {
            new() { Id = "cpu/temp", HardwareName = "Intel Core Ultra 9 285HX", HardwareType = "Cpu", Name = "CPU Package", SensorType = "Temperature", Value = null, ReadStatus = "未安装 PawnIO；此项需要 CPU 硬件访问驱动" },
            new() { Id = "cpu/load", HardwareName = "Intel Core Ultra 9 285HX", HardwareType = "Cpu", Name = "CPU Total", SensorType = "Load", Value = 0 },
            new() { Id = "gpu/power", HardwareName = "NVIDIA", HardwareType = "GpuNvidia", Name = "GPU Power", SensorType = "Power", Value = 28.125 },
            new() { Id = "gpu/clock", HardwareName = "NVIDIA", HardwareType = "GpuNvidia", Name = "GPU Core", SensorType = "Clock", Value = 2400 },
            new() { Id = "ram/total", HardwareName = "Generic Memory", HardwareType = "Memory", Name = "Memory Available", SensorType = "Data", Value = 32 },
            new() { Id = "fan/rpm", HardwareName = "Mainboard", HardwareType = "Motherboard", Name = "Fan #1", SensorType = "Fan", Value = double.NaN },
            new() { Id = "other/raw", HardwareName = "Unknown Device", HardwareType = "Unknown", Name = "Raw Sensor", SensorType = "FutureType", Value = 1 }
        };
        var reads = 0;
        var diagnostic = "未安装 PawnIO，当前 CPU 温度无法读取。";
        var log = new AppLog();
        var window = new SensorInspectorWindow(() =>
        {
            reads++;
            return reads switch
            {
                3 => Task.FromException<IReadOnlyList<SensorDescriptor>>(new IOException("后台连接已断开")),
                4 => Task.FromResult<IReadOnlyList<SensorDescriptor>>([]),
                _ => Task.FromResult<IReadOnlyList<SensorDescriptor>>(sample)
            };
        }, () => diagnostic, log);
        try
        {
            var grid = (DataGrid)window.FindName("SensorGrid");
            var filter = (TextBox)window.FindName("FilterInput");
            var empty = (TextBlock)window.FindName("EmptyText");
            var status = (TextBlock)window.FindName("ReadStatus");
            var diagnosis = (TextBlock)window.FindName("DiagnosticText");
            check(reads == 0 && !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero, "sensor inspector construction opens no native window and starts no sampler");
            check(diagnosis.Text == diagnostic, "sensor inspector immediately exposes the CPU driver diagnostic");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Drain();
            var rows = grid.Items.Cast<SensorReadingRow>().ToArray();
            check(reads == 1 && rows.Length == sample.Length, "sensor inspector first load displays the backend sensor list automatically");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Drain();
            check(reads == 1, "repeated Loaded events do not create periodic or duplicate sensor reads");
            check(rows.Single(row => row.Id == "cpu/load").ValueText == "0", "sensor inspector preserves a genuine zero reading");
            check(rows.Single(row => row.Id == "cpu/temp").ReadStatus.Contains("PawnIO"), "per-sensor missing-driver reason reaches the independent sensor table");
            check(rows.Single(row => row.Id == "cpu/temp").ValueText == "无读数" && rows.Single(row => row.Id == "fan/rpm").ValueText == "无读数", "missing and nonfinite sensor readings never become zero");
            check(rows.Single(row => row.Id == "cpu/temp").Unit == "°C" && rows.Single(row => row.Id == "cpu/load").Unit == "%"
                && rows.Single(row => row.Id == "gpu/power").Unit == "W" && rows.Single(row => row.Id == "gpu/clock").Unit == "MHz"
                && rows.Single(row => row.Id == "ram/total").Unit == "GiB" && rows.Single(row => row.Id == "fan/rpm").Unit == "RPM", "sensor inspector labels raw values with the corresponding sensor units");
            check(rows.Single(row => row.Id == "other/raw").Unit == "—", "unknown sensor types keep their raw value without inventing a unit");
            check(grid.Columns.Count == 8 && grid.Columns.All(column => column is DataGridTextColumn textColumn && textColumn.Binding is System.Windows.Data.Binding { Mode: System.Windows.Data.BindingMode.OneWay }), "sensor table presents selected sources, values and missing-data reasons without write-back bindings");

            filter.Text = "intel"; Drain();
            check(grid.Items.Count == 2, "sensor search filters hardware names without case sensitivity");
            filter.Text = "GPU Power"; Drain();
            check(grid.Items.Count == 1 && ((SensorReadingRow)grid.Items[0]).SensorType == "Power", "sensor search filters sensor names");
            filter.Text = "no matching sensor"; Drain();
            check(grid.Items.Count == 0 && empty.Text.Contains("筛选", StringComparison.Ordinal), "filtered-empty state differs from an empty backend result");
            filter.Text = ""; Drain();
            check(grid.Items.Count == sample.Length && reads == 1, "clearing sensor search restores cached rows without a backend call");

            diagnostic = "已连接后台，CPU 驱动仍不可用。";
            ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            check(reads == 2 && diagnosis.Text == diagnostic, "manual sensor refresh updates both readings and the visible diagnostic");
            window.RefreshAsync().GetAwaiter().GetResult(); Drain();
            check(reads == 3 && grid.Items.Count == sample.Length && status.Text.Contains("读取失败", StringComparison.Ordinal) && status.Text.Contains("保留", StringComparison.Ordinal), "failed sensor refresh reports the failure and marks retained readings as previous data");
            check(log.RecentText.Contains("传感器查看读取失败", StringComparison.Ordinal), "sensor read failures are persisted through AppLog");
            window.RefreshAsync().GetAwaiter().GetResult(); Drain();
            check(grid.Items.Count == 0 && status.Text.Contains("0 个传感器", StringComparison.Ordinal) && !status.Text.Contains("读取失败", StringComparison.Ordinal), "successful empty sensor result clears stale rows and differs from a failure");
        }
        finally { window.Close(); Drain(); }
    }

    private static void CheckPendingClose(Action<bool, string> check)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<SensorDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var window = new SensorInspectorWindow(() => { reads++; return completion.Task; }, () => "等待后台诊断", new AppLog());
        var grid = (DataGrid)window.FindName("SensorGrid");
        var status = (TextBlock)window.FindName("ReadStatus");
        var button = (Button)window.FindName("RefreshButton");
        var pending = window.RefreshAsync();
        check(!button.IsEnabled && status.Text.Contains("正在读取", StringComparison.Ordinal), "sensor inspector distinguishes an in-flight read and disables its refresh action");
        window.RefreshAsync().GetAwaiter().GetResult();
        check(reads == 1, "sensor inspector prevents overlapping backend reads");
        window.Close();
        var closedStatus = status.Text;
        completion.SetResult([new() { Id = "late", Name = "Late CPU", SensorType = "Temperature", Value = 60 }]);
        PumpUntil(pending);
        check(grid.Items.Count == 0 && status.Text == closedStatus, "closing sensor inspector ignores late backend results and UI callbacks");
        window.RefreshAsync().GetAwaiter().GetResult();
        check(reads == 1, "closed sensor inspector cannot start another backend read");
    }

    private static void PumpUntil(Task task)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
        // Capture the UI dispatcher: a worker's CurrentDispatcher would refer to a different thread.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Sensor inspector stub did not complete.");
        task.GetAwaiter().GetResult(); Drain();
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}
