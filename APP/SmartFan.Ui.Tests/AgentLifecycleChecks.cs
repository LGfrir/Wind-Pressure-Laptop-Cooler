using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;
using SmartFan.Runtime;
using SmartFan.Runtime.Ipc;

namespace SmartFan.Ui.Tests;

/// <summary>Real isolated IPC and lifetime markers; no production process, hardware, BLE or UAC.</summary>
internal static class AgentLifecycleChecks
{
    public static void Run(Action<bool, string> check)
    {
        string pipeName = "SmartFan.UiTests.Lifecycle." + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(AppContext.BaseDirectory, "test-agent-lifecycle", Guid.NewGuid().ToString("N"));
        var client = new BackgroundClient(pipeName);
        var vm = new MainViewModel(client, new SettingsStore(directory, false), new AppLog(), () => IsRunning(pipeName));
        try
        {
            check(!vm.IsAgentRunning && vm.AgentActionText == "打开后台", "absent backend offers open before the frontend has ever attached");
            using (var fixture = new Fixture(pipeName, directory))
            {
                var opening = vm.ToggleAgentCommand.ExecuteAsync(null);
                fixture.PublishRunning();
                PumpUntil(() => vm.AgentActionProgress == "正在打开…" && vm.Busy);
                check(!vm.ToggleAgentCommand.CanExecute(null), "opening disables repeated backend actions while startup is pending");
                fixture.AllowSensorEnumeration(); Complete(opening);
                check(client.IsAttached && vm.IsAgentRunning && vm.AgentActionText == "关闭后台" && !vm.IsConnected,
                    "opening attaches the backend and offers close even while ESP Bluetooth is disconnected");
                check(vm.ToggleAgentCommand.CanExecute(null), "successful startup restores the backend action");
                var detached = new MainViewModel(new BackgroundClient(pipeName), new SettingsStore(directory, false), new AppLog(), () => IsRunning(pipeName));
                check(detached.IsAgentRunning && detached.AgentActionText == "关闭后台", "an existing backend is detected without a frontend IPC connection");
                Complete(detached.DisposeAsync().AsTask());

                vm.ConfirmAction = _ => false;
                Complete(vm.ToggleAgentCommand.ExecuteAsync(null));
                check(client.IsAttached && !fixture.ShutdownRequested && vm.AgentActionText == "关闭后台", "canceling close preserves the running backend and its button state");

                vm.ConfirmAction = _ => true;
                var closing = vm.ToggleAgentCommand.ExecuteAsync(null);
                PumpUntil(() => fixture.ShutdownRequested);
                check(vm.AgentActionText == "正在关闭…" && !vm.ToggleAgentCommand.CanExecute(null), "acknowledging shutdown keeps the button disabled until the backend actually exits");
                fixture.AllowExit(); Complete(closing);
                check(!client.IsAttached && !IsRunning(pipeName) && !vm.IsAgentRunning && vm.AgentActionText == "打开后台",
                    "completed shutdown changes the same button back to open and releases the instance marker");
            }
            using (var restarted = new Fixture(pipeName, directory))
            {
                restarted.AllowSensorEnumeration();
                Complete(vm.ToggleAgentCommand.ExecuteAsync(null)); restarted.PublishRunning();
                check(client.IsAttached && vm.IsAgentRunning && vm.AgentActionText == "关闭后台", "the existing frontend client can open the backend again after a complete shutdown");
                restarted.RequestShutdown(); restarted.AllowExit(); Complete(restarted.Completion);
                PumpUntil(() => !client.IsAttached); vm.RefreshAgentState();
                check(!vm.IsAgentRunning && vm.AgentActionText == "打开后台", "an external backend exit is reflected without clicking the action or restarting the frontend");
            }
        }
        finally
        {
            Complete(vm.DisposeAsync().AsTask());
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            AgentLauncher.ResetActiveConfiguration();
        }
    }

    private static bool IsRunning(string name) => (bool)(typeof(AgentLauncher).GetMethod("IsRunning", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("AgentLauncher.IsRunning")).Invoke(null, [name])!;
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Offline backend lifecycle did not complete.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AgentHost _host;
        private readonly AgentPipeServer _server;
        private readonly Sampler _sampler = new();
        private readonly string _pipeName;
        private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Mutex? _instance;
        public bool ShutdownRequested => _shutdown.Task.IsCompleted;
        public Task Completion { get; }
        public Fixture(string pipeName, string directory)
        {
            _pipeName = pipeName;
            _host = new AgentHost(new UnusedBle(), _sampler, new AgentConfigurationStore(Path.Combine(directory, "agent.json")));
            _server = new AgentPipeServer(pipeName, _host);
            _host.ShutdownRequested += (_, _) => _shutdown.TrySetResult();
            Complete(_host.StartAsync()); Complete(_server.StartAsync());
            Completion = Task.Run(async () =>
            {
                await _shutdown.Task; await _exit.Task;
                await _server.DisposeAsync(); await _host.DisposeAsync();
                _instance?.Dispose();
            });
        }
        public void PublishRunning() => _instance = new Mutex(false, "Local\\" + _pipeName);
        public void AllowSensorEnumeration() => _sampler.Ready.TrySetResult();
        public void RequestShutdown() => _host.RequestShutdown();
        public void AllowExit() => _exit.TrySetResult();
        public void Dispose() { AllowSensorEnumeration(); RequestShutdown(); AllowExit(); Complete(Completion); }
    }

    private sealed class Sampler : IHardwareSampler
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TelemetrySnapshot> ReadAsync(SensorPreferences? preferences = null, bool demo = false, CancellationToken cancellationToken = default) => Task.FromResult(new TelemetrySnapshot { Timestamp = DateTimeOffset.Now });
        public async Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default) { await Ready.Task.WaitAsync(cancellationToken); return []; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedBle : IBleClient
    {
        public BleConnectionState State => BleConnectionState.Disconnected;
        public bool IsConnected => false;
        public ushort MaxPduSize => 23;
        public BleDeviceInfo? ConnectedDevice => null;
        public IReadOnlyList<DiscoveredCharacteristic> Characteristics => [];
        public bool AutoReconnect { get; set; }
        public TimeSpan ReconnectInterval { get; set; }
        public TimeSpan OperationTimeout { get; set; }
        public event EventHandler<BleDeviceInfo>? DeviceDiscovered { add { } remove { } }
        public event EventHandler<BleConnectionEventArgs>? ConnectionChanged { add { } remove { } }
        public event EventHandler<RpmEventArgs>? RpmReceived { add { } remove { } }
        public event EventHandler<BleDiagnosticEventArgs>? Diagnostic { add { } remove { } }
        public Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE scan.");
        public Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE connection.");
        public Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE connection.");
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE disconnect.");
        public Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE read.");
        public Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE write.");
        public Task<bool> UnpairAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected BLE unpair.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
