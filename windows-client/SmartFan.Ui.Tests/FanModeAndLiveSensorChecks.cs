using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class FanModeAndLiveSensorChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckFanMode(check);
        CheckAdoptedSources(check);
        var previous = ClientPaths.DataDirectory;
        ClientPaths.Initialize(["--data-dir", Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", "live-sensors", Guid.NewGuid().ToString("N"))]);
        try { CheckLiveInspector(check); }
        finally { ClientPaths.Initialize(["--data-dir", previous]); }
    }

    private static void CheckFanMode(Action<bool, string> check)
    {
        var state = new FanControlState();
        var control = new DashboardFanControl { DataContext = state };
        void Layout(double height = 300) { control.Measure(new Size(360, height)); control.Arrange(new Rect(0, 0, 360, height)); control.UpdateLayout(); Drain(); }
        var manual = (StackPanel)control.FindName("ManualPanel");
        var auto = (StackPanel)control.FindName("AutomaticPanel");
        var full = (StackPanel)control.FindName("FullSpeedPanel");
        var apply = (Button)control.FindName("ApplyButton");
        var summary = (TextBlock)control.FindName("AutomaticTemperature");
        Layout();
        check(manual.Visibility == Visibility.Visible && auto.Visibility == Visibility.Collapsed && full.Visibility == Visibility.Collapsed, "manual fan mode exposes the PWM panel alone");
        check(!apply.IsEnabled, "fan apply is unavailable before an ESP connection");
        state.IsConnected = true; state.Notify(); Drain();
        check(apply.IsEnabled, "connected idle fan controls allow explicit apply");
        state.Busy = true; state.Notify(); Drain();
        check(!apply.IsEnabled, "pending fan operations cannot be applied twice");
        state.Busy = false; state.FanModeIndex = 1; state.Notify(); Layout();
        check(auto.Visibility == Visibility.Visible && manual.Visibility == Visibility.Collapsed && full.Visibility == Visibility.Collapsed, "automatic mode hides the inactive PWM slider");
        check(summary.Text.Contains("较高温度") && summary.Text.Contains("81.0"), "automatic mode shows the selected CPU/GPU temperature source and actual larger reading");
        var navigation = 0; control.OpenCurveRequested += (_, _) => navigation++;
        ((Button)control.FindName("CurveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(navigation == 1, "local curve navigation uses a routed UI event rather than sending a device command");
        state.SelectedProfile.TemperatureSource = TemperatureSource.Cpu; state.Notify(); Drain();
        check(summary.Text.Contains("CPU 温度") && summary.Text.Contains("72.0"), "automatic source changes refresh without altering either sensor reading");
        state.TelemetryIsStale = true; state.Notify(); Drain();
        check(summary.Text.Contains("过期") && !summary.Text.Contains("72.0"), "automatic mode suppresses stale control temperature");
        state.TelemetryIsStale = false; state.DemoMode = true; state.Notify(); Drain();
        check(summary.Text.Contains("演示") && !summary.Text.Contains("72.0"), "automatic mode never labels demo temperature as real control input");
        check(!apply.IsEnabled, "demo mode keeps actual ESP fan writes unavailable");
        state.FanModeIndex = 2; state.Notify(); Layout();
        check(full.Visibility == Visibility.Visible && manual.Visibility == Visibility.Collapsed && auto.Visibility == Visibility.Collapsed, "full-speed mode presents its own operation instead of an irrelevant PWM slider");
        state.FanModeIndex = 0; state.Notify(); Layout(195);
        check(((TextBlock)control.FindName("ManualHint")).Visibility == Visibility.Collapsed, "small fan cards hide secondary instructions to preserve primary controls");
        Layout();
        check(((TextBlock)control.FindName("ManualHint")).Visibility == Visibility.Visible, "larger fan cards restore explanatory text");
    }

    private static void CheckAdoptedSources(Action<bool, string> check)
    {
        var timestamp = DateTimeOffset.Now.AddSeconds(-7);
        var snapshot = new TelemetrySnapshot { Timestamp = timestamp, SensorNames = new Dictionary<string, string>
        {
            ["CPU 温度"] = "CPU / Package [/cpu/temp/0]", ["GPU 温度"] = "GPU [/chip] / Core [/gpu/temp/0]",
            ["内置风扇 1"] = "EC / CPU Fan [/asus-ec/fan/0-cpu]", ["无标识说明"] = "不是一个来源标识"
        }};
        var context = SensorInspectorContext.FromSnapshot(snapshot, "数据已过期");
        check(context.Timestamp == timestamp && context.FreshnessText.Contains("过期"), "sensor inspector context preserves the actual backend timestamp and freshness");
        check(context.AdoptedSources.Count == 3 && context.AdoptedSources["/cpu/temp/0"] == "CPU 温度" && context.AdoptedSources["/gpu/temp/0"] == "GPU 温度", "adopted CPU and GPU sensors match the exact trailing sensor identifier");
        check(context.AdoptedSources["/asus-ec/fan/0-cpu"] == "内置风扇 1", "sensor inspector distinguishes selected laptop fan sources from ESP RPM");
        check(SensorInspectorContext.FromSnapshot(new TelemetrySnapshot { IsDemo = true, SensorNames = snapshot.SensorNames }).AdoptedSources.Count == 0, "demo snapshots cannot highlight physical sensors as adopted data");
    }

    private static void CheckLiveInspector(Action<bool, string> check)
    {
        var reads = 0;
        var context = SensorInspectorContext.FromSnapshot(new TelemetrySnapshot { SensorNames = new Dictionary<string, string> { ["CPU 温度"] = "CPU / Package [/cpu/temp]" } }, "数据有效");
        var window = new SensorInspectorWindow(() =>
        {
            reads++; return Task.FromResult<IReadOnlyList<SensorDescriptor>>([new() { Id = "/cpu/temp", HardwareName = "CPU", Name = "Package", SensorType = "Temperature", Value = 40 + reads }]);
        }, () => "同一个后台采集源", new AppLog(), context: () => context)
        { Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        var timer = (DispatcherTimer)typeof(SensorInspectorWindow).GetField("_liveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        timer.Interval = TimeSpan.FromMilliseconds(60);
        try
        {
            ((CheckBox)window.FindName("LiveRefreshToggle")).IsChecked = true;
            window.Show(); Drain();
            var grid = (DataGrid)window.FindName("SensorGrid");
            var first = (SensorReadingRow)grid.Items[0]; grid.SelectedItem = first;
            check(timer.IsEnabled && first.IsAdopted && first.AdoptedFor == "CPU 温度", "visible sensor inspector starts optional live refresh and marks the adopted CPU source");
            PumpUntil(() => reads >= 2);
            check(ReferenceEquals(grid.Items[0], first) && ReferenceEquals(grid.SelectedItem, first) && first.ValueText != "41", "live refresh updates values in place while preserving row identity and selection");
            ((TextBox)window.FindName("FilterInput")).Text = "CPU 温度"; Drain();
            check(grid.Items.Count == 1, "sensor filter can find the role adopted by the dashboard");
            ((CheckBox)window.FindName("LiveRefreshToggle")).IsChecked = false; Drain();
            var pausedReads = reads; PumpFor(140);
            check(!timer.IsEnabled && reads == pausedReads, "pausing live sensors stops periodic backend requests");
            window.RefreshAsync().GetAwaiter().GetResult(); Drain();
            check(reads == pausedReads + 1, "manual sensor refresh remains available while live refresh is paused");
            context = context with { FreshnessText = "数据已过期", Timestamp = DateTimeOffset.Now.AddMinutes(-1) };
            window.RefreshAsync().GetAwaiter().GetResult(); Drain();
            check(((TextBlock)window.FindName("SourceStampText")).Text.Contains("已过期"), "sensor inspector separates backend sample freshness from list refresh time");
            window.Close(); PumpFor(100);
            check(!timer.IsEnabled, "closing sensor inspector tears down its refresh timer");
        }
        finally { window.Close(); }
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(3);
        while (!done() && DateTimeOffset.Now < deadline) PumpFor(20);
        if (!done()) throw new TimeoutException("Live sensor refresh stub did not run.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle); Dispatcher.PushFrame(frame);
    }

    private sealed class FanControlState : INotifyPropertyChanged
    {
        public IReadOnlyList<string> FanModes { get; } = ["手动控制", "设备自动控制", "全速运行"];
        public int FanModeIndex { get; set; }
        public double ManualPwm { get; set; } = 30;
        public bool IsConnected { get; set; }
        public bool Busy { get; set; }
        public bool DemoMode { get; set; }
        public bool TelemetryIsStale { get; set; }
        public double CpuTemperatureValue { get; set; } = 72;
        public double GpuTemperatureValue { get; set; } = 81;
        public AppSettings Settings { get; } = new();
        public DeviceProfile SelectedProfile { get; } = new();
        public string FanControlStatusText { get; } = "尚未发送";
        public System.Windows.Input.ICommand ApplyFanCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
