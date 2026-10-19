using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;
using System.Security.Principal;

namespace SmartFan.Runtime;

/// <summary>真正拥有 BLE 与采样器的后台会话；任何 UI 管道断开都不会调用此对象的 Stop。</summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly IBleClient _ble;
    private readonly IHardwareSampler _sampler;
    private readonly AgentConfigurationStore _store;
    private readonly TelemetryUploader _uploader;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _hardwareGate = new(1, 1);
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private AgentConfiguration _configuration = new();
    private Task? _samplingTask;
    private Task? _startupConnectionTask;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private TelemetrySnapshot? _snapshot;
    private long _sampleCount;
    private uint? _rpm;
    private DateTimeOffset? _rpmAt;
    private string _message = "后台正在启动";
    private string _telemetryMessage = "自动上报已关闭";
    private int _started;
    private int _disposed;
    private int _startupConnectionStopped;
    private long _connectionGeneration;
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly bool _isAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public AgentHost(IBleClient ble, IHardwareSampler sampler, AgentConfigurationStore store, Action<string>? log = null)
    {
        _ble = ble; _sampler = sampler; _store = store; _log = log;
        _uploader = new TelemetryUploader(ble);
        _ble.ConnectionChanged += ConnectionChanged;
        _ble.RpmReceived += RpmReceived;
        _ble.Diagnostic += Diagnostic;
        _ble.DeviceDiscovered += DeviceDiscovered;
    }
    public IBleClient BleClient => _ble;
    public event EventHandler<AgentStatus>? StatusChanged;
    public event EventHandler<TelemetrySnapshot>? SnapshotReceived;
    public event EventHandler<RpmEventArgs>? Rpm;
    public event EventHandler<BleDeviceInfo>? DeviceFound;
    public event EventHandler<string>? LogMessage;
    public event EventHandler? ShutdownRequested;

    public void RequestShutdown()
    {
        Log("用户明确请求停止后台，蓝牙和采样将一起关闭。");
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _configuration = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(_configuration, cancellationToken).ConfigureAwait(false);
        ApplyBleOptions(_configuration);
        _message = "后台运行中；关闭前端不会停止采样与蓝牙";
        _samplingTask = Task.Run(() => SamplingLoopAsync(_stop.Token), CancellationToken.None);
        var configuration = GetConfiguration();
        if (configuration.AutoConnect && configuration.SavedDevice is not null)
        {
            _startupConnectionTask = Task.Run(() => StartupConnectionLoopAsync(_stop.Token), CancellationToken.None);
        }
        RaiseStatus();
    }

    public AgentConfiguration GetConfiguration() => AgentJson.Clone(Volatile.Read(ref _configuration));
    public AgentStatus GetStatus() => new()
    {
        ProcessId = Environment.ProcessId, BootId = _bootId, ConfigurationPath = _store.FilePath, ConnectionGeneration = Interlocked.Read(ref _connectionGeneration),
        IsAdministrator = _isAdministrator, StartedAt = _startedAt, State = _ble.State,
        IsConnected = _ble.IsConnected, MaxPduSize = _ble.MaxPduSize, ConnectedDevice = _ble.ConnectedDevice,
        Characteristics = _ble.Characteristics.Select(item => new AgentCharacteristicStatus
        {
            Key = item.Definition.Key, Properties = item.Properties, CanRead = item.CanRead,
            CanWrite = item.CanWrite, CanNotify = item.CanNotify
        }).ToList(),
        Snapshot = _snapshot, SampleCount = Interlocked.Read(ref _sampleCount), LastRpm = _rpm,
        LastRpmAt = _rpmAt, Message = _message, TelemetryMessage = _telemetryMessage,
        EnabledTelemetry = _configuration.EnabledTelemetry, DemoMode = _configuration.DemoMode
    };

    public async Task ConfigureAsync(AgentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        AgentConfiguration.Validate(configuration);
        var copy = AgentJson.Clone(configuration);
        copy.Telemetry.Enabled = copy.EnabledTelemetry;
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(copy, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _configuration, copy);
            ApplyBleOptions(copy); _uploader.ResetCache();
            Log("后台启动参数已原子保存并生效"); RaiseStatus();
        }
        finally { _configurationGate.Release(); }
    }

    public async Task ConfigureTelemetryAsync(TelemetrySettings settings, CancellationToken cancellationToken = default)
    {
        var configuration = GetConfiguration();
        configuration.Telemetry = AgentJson.Clone(settings);
        configuration.EnabledTelemetry = settings.Enabled;
        await ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDemoAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var configuration = GetConfiguration(); configuration.DemoMode = enabled;
        if (enabled) { configuration.EnabledTelemetry = false; configuration.Telemetry.Enabled = false; }
        await ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _startupConnectionStopped, 1);
        await _ble.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
        var configuration = GetConfiguration(); configuration.SavedDevice = _ble.ConnectedDevice ?? device;
        await ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _startupConnectionStopped, 1);
        await _ble.ConnectAsync(addressOrId, cancellationToken).ConfigureAwait(false);
        if (_ble.ConnectedDevice is not { } device) return;
        var configuration = GetConfiguration(); configuration.SavedDevice = device;
        await ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _startupConnectionStopped, 1);
        await _ble.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartupConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _startupConnectionStopped) == 0)
        {
            var configuration = GetConfiguration();
            if (!configuration.AutoConnect || configuration.SavedDevice is null || _ble.IsConnected) return;
            try { await _ble.ConnectAsync(configuration.SavedDevice, cancellationToken).ConfigureAwait(false); return; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { Log($"启动时自动连接失败：{ex.Message}"); _message = ex.Message; RaiseStatus(); }
            if (!configuration.AutoReconnect) return;
            var seconds = Math.Min(30, configuration.ReconnectInitialSeconds * Math.Pow(2, Math.Min(attempt++, 4)));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    public Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default)
    {
        if (_configuration.DemoMode) throw new InvalidOperationException("后台处于演示模式，禁止写入真实设备；请先关闭演示模式。");
        return _ble.WriteAsync(key, payload, verify, cancellationToken);
    }

    public async Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default)
    {
        await _hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _sampler.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false); }
        finally { _hardwareGate.Release(); }
    }

    private async Task SamplingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = GetConfiguration();
            try
            {
                await _hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                TelemetrySnapshot snapshot;
                try { snapshot = await _sampler.ReadAsync(configuration.Telemetry.SensorPreferences, configuration.DemoMode, cancellationToken).ConfigureAwait(false); }
                finally { _hardwareGate.Release(); }
                _snapshot = snapshot; Interlocked.Increment(ref _sampleCount);
                SnapshotReceived?.Invoke(this, snapshot);
                if (configuration.EnabledTelemetry && !configuration.DemoMode)
                {
                    var settings = AgentJson.Clone(configuration.Telemetry); settings.Enabled = true;
                    var result = await _uploader.SendAsync(snapshot, settings, cancellationToken).ConfigureAwait(false);
                    _telemetryMessage = result.Message;
                }
                else _telemetryMessage = configuration.DemoMode ? "演示数据禁止上报" : "自动上报已关闭";
                RaiseStatus();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _telemetryMessage = $"后台采样/上报异常：{ex.Message}"; Log(_telemetryMessage); RaiseStatus(); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(configuration.Telemetry.IntervalMs), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private void ApplyBleOptions(AgentConfiguration configuration)
    {
        _ble.AutoReconnect = configuration.AutoReconnect;
        _ble.ReconnectInterval = TimeSpan.FromSeconds(configuration.ReconnectInitialSeconds);
    }
    private void ConnectionChanged(object? sender, BleConnectionEventArgs args)
    {
        _message = args.Message;
        if (args.State == BleConnectionState.Connected)
        {
            _uploader.ResetCache();
            var generation = Interlocked.Increment(ref _connectionGeneration);
            _ = ApplyAutomaticProfileAsync(generation);
        }
        RaiseStatus();
    }
    private void RpmReceived(object? sender, RpmEventArgs args) { _rpm = args.Rpm; _rpmAt = args.Timestamp; Rpm?.Invoke(this, args); RaiseStatus(); }
    private void DeviceDiscovered(object? sender, BleDeviceInfo args) => DeviceFound?.Invoke(this, args);
    private void Diagnostic(object? sender, BleDiagnosticEventArgs args) => Log(args.Message + (args.Exception is null ? "" : " " + args.Exception.Message));
    private void Log(string message) { _log?.Invoke(message); LogMessage?.Invoke(this, message); }
    private void RaiseStatus() => StatusChanged?.Invoke(this, GetStatus());

    private async Task ApplyAutomaticProfileAsync(long generation)
    {
        try
        {
            await _profileGate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                var configuration = GetConfiguration();
                if (!configuration.AutoUploadConfiguration || configuration.AutoUploadProfile is null || configuration.DemoMode || generation != Interlocked.Read(ref _connectionGeneration)) return;
                var profile = configuration.AutoUploadProfile;
                // 曲线、温度来源与失联策略尚无 GATT 接口，不编造 UUID；目标 RPM 也不代替 ESP 温控发送。
                await WriteAsync(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(profile.FanMode), true, _stop.Token).ConfigureAwait(false);
                if (profile.FanMode == 0) await WriteAsync(CharacteristicKey.FanPwm, PacketCodec.EncodeByte(profile.ManualPwm), true, _stop.Token).ConfigureAwait(false);
                foreach (var pair in profile.LightingPayloads)
                {
                    if (ProtocolCatalog.TryGet(pair.Key, out var key)) await WriteAsync(key, pair.Value, true, _stop.Token).ConfigureAwait(false);
                }
                await WriteAsync(CharacteristicKey.LightBrightness, PacketCodec.EncodeByte(profile.Brightness), true, _stop.Token).ConfigureAwait(false);
                await WriteAsync(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(profile.LightMode), true, _stop.Token).ConfigureAwait(false);
                Log($"已按用户开启的自动同步上传场景「{profile.Name}」；温控曲线接口仍待固件对齐。");
            }
            finally { _profileGate.Release(); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Log($"后台自动同步未完成：{ex.Message}"); _message = "自动同步失败：" + ex.Message; RaiseStatus(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_samplingTask is not null) await _samplingTask.ConfigureAwait(false);
        if (_startupConnectionTask is not null) await _startupConnectionTask.ConfigureAwait(false);
        _ble.ConnectionChanged -= ConnectionChanged; _ble.RpmReceived -= RpmReceived;
        _ble.DeviceDiscovered -= DeviceDiscovered; _ble.Diagnostic -= Diagnostic;
        await _ble.DisposeAsync().ConfigureAwait(false);
        await _sampler.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
