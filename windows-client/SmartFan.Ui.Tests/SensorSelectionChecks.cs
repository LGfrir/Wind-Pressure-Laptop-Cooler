using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Real selector bindings and collection refresh behavior, without a backend or native window.</summary>
internal static class SensorSelectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var previousDirectory = ClientPaths.DataDirectory;
        ClientPaths.Initialize(["--data-dir", Path.Combine(Path.GetTempPath(), "SmartFan-UiTests", "sensor-selection", Guid.NewGuid().ToString("N"))]);
        var log = new AppLog();
        var backend = new BackgroundClient($"SmartFan.UiTests.SensorSelection.Unattached.{Guid.NewGuid():N}");
        var vm = new MainViewModel(backend, new SettingsStore(ClientPaths.DataDirectory, migrateOldPreferences: false), log);
        vm.InitializeAsync().GetAwaiter().GetResult();
        using var desktop = new DesktopService(log);
        var window = new SmartFan.App.MainWindow(vm, new AppearanceService(), desktop, log);
        try
        {
            var content = (FrameworkElement)window.Content;
            ((FluidTabControl)window.FindName("MainTabs")).SelectedIndex = 4;
            Layout(content); Drain();
            var cpu = (ComboBox)window.FindName("CpuSensorBox");
            var gpu = (ComboBox)window.FindName("GpuSensorBox");
            var adapter = (ComboBox)window.FindName("GpuAdapterBox");
            var present = typeof(MainViewModel).GetMethod("PresentSensors", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("MainViewModel.PresentSensors");
            void Present(IReadOnlyList<SensorDescriptor> sensors) { present.Invoke(vm, [sensors]); Drain(); Layout(content); Drain(); }
            SensorDescriptor[] sensors =
            [
                new() { Id = "cpu/package", HardwareId = "cpu/0", HardwareName = "CPU", HardwareType = "Cpu", Name = "CPU Package", SensorType = "Temperature", Value = 63 },
                new() { Id = "gpu/0/temp", HardwareId = "gpu/0", HardwareName = "NVIDIA", HardwareType = "GpuNvidia", Name = "GPU Core", SensorType = "Temperature", Value = 58 },
                new() { Id = "gpu/1/temp", HardwareId = "gpu/1", HardwareName = "AMD", HardwareType = "GpuAmd", Name = "GPU Core", SensorType = "Temperature", Value = 51 }
            ];
            var preferences = vm.Settings.Telemetry.SensorPreferences;
            Present(sensors);
            check(new[] { cpu, gpu, adapter }.All(box => box.SelectedIndex == 0 && box.SelectedValue is ""), "unset sensor preferences display the explicit automatic option in all three actual selectors");
            check(preferences.CpuTemperatureSensorId is null && preferences.GpuTemperatureSensorId is null && preferences.GpuHardwareId is null, "initial sensor enumeration keeps default automatic preferences null");

            // SelectedItem follows the real selector selection path; assigning SelectedValue directly would replace its binding.
            cpu.SelectedItem = cpu.Items.Cast<SensorDescriptor>().Single(sensor => sensor.Id == "cpu/package");
            gpu.SelectedItem = gpu.Items.Cast<SensorDescriptor>().Single(sensor => sensor.Id == "gpu/1/temp");
            adapter.SelectedItem = adapter.Items.Cast<SensorDescriptor>().Single(sensor => sensor.HardwareId == "gpu/1");
            Drain();
            bool HasManualPreferences() => preferences.CpuTemperatureSensorId == "cpu/package" && preferences.GpuTemperatureSensorId == "gpu/1/temp" && preferences.GpuHardwareId == "gpu/1";
            check(HasManualPreferences(), "user selections explicitly update all three sensor preference identifiers");
            check(new[] { cpu, gpu, adapter }.All(box => BindingOperations.GetBinding(box, Selector.SelectedValueProperty)?.Mode == BindingMode.OneWay), "user selection preserves the actual OneWay SelectedValue bindings");

            Present(sensors);
            check(HasManualPreferences(), "clearing and rebuilding sensor collections cannot erase saved manual preferences");
            check(cpu.SelectedValue is "cpu/package" && gpu.SelectedValue is "gpu/1/temp" && adapter.SelectedValue is "gpu/1", "rebuilt sensor collections restore all three visible manual selections");

            Present([]);
            check(HasManualPreferences(), "temporarily absent preferred sensors retain their saved identifiers");
            check(new[] { cpu, gpu, adapter }.All(box => box.SelectedItem is null), "missing preferred sensors are not silently replaced by the automatic option");
            Present(sensors);
            check(cpu.SelectedValue is "cpu/package" && gpu.SelectedValue is "gpu/1/temp" && adapter.SelectedValue is "gpu/1", "preferred sensors reappearing after a refresh are selected again");

            cpu.SelectedItem = cpu.Items[0]; gpu.SelectedItem = gpu.Items[0]; adapter.SelectedItem = adapter.Items[0]; Drain();
            check(preferences.CpuTemperatureSensorId is null && preferences.GpuTemperatureSensorId is null && preferences.GpuHardwareId is null, "explicitly choosing automatic writes null preferences through the real selection handlers");
            check(!vm.IsRefreshingSensors && !backend.IsAttached && new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero, "sensor-selection checks leave refresh guards released and open no backend or native window");
        }
        finally
        {
            var closing = typeof(SmartFan.App.MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException("MainWindow.OnClosing");
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, closing);
            window.Close(); vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ClientPaths.Initialize(["--data-dir", previousDirectory]);
        }
    }

    private static void Layout(FrameworkElement root)
    { root.Measure(new Size(1360, 930)); root.Arrange(new Rect(0, 0, 1360, 930)); root.UpdateLayout(); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}
