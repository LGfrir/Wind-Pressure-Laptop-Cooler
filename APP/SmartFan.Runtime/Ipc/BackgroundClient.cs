using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFan.App.Models;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;

namespace SmartFan.Runtime.Ipc;

/// <summary>前端代理。Dispose 仅关闭此窗口的 IPC，蓝牙和采样器由独立 Agent 继续持有。</summary>
public sealed class BackgroundClient : IBleClient
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PipeEnvelope>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _receiveStop;
    private Task? _receiveTask;
    private bool _disposed;
    private AgentConfiguration _configuration = new();
    public BackgroundClient(string? pipeName = null) => _pipeName = pipeName ?? AgentPipeName.Default;
    public AgentStatus AgentStatus { get; private set; } = new();
    public TelemetrySnapshot? LatestSnapshot { get; private set; }
    public bool IsAttached => _pipe?.IsConnected == true;
    public BleConnectionState State => IsAttached ? AgentStatus.State : BleConnectionState.Disconnected;
    public bool IsConnected => IsAttached && AgentStatus.IsConnected;
    public ushort MaxPduSize => AgentStatus.MaxPduSize;
    public BleDeviceInfo? ConnectedDevice => AgentStatus.ConnectedDevice;
    public IReadOnlyList<DiscoveredCharacteristic> Characteristics => AgentStatus.Characteristics.Select(item => new DiscoveredCharacteristic
    {
        Definition = ProtocolCatalog.Get(item.Key), Properties = item.Properties,
        CanRead = item.CanRead, CanWrite = item.CanWrite, CanNotify = item.CanNotify
    }).ToArray();
    public bool AutoReconnect { get => _configuration.AutoReconnect; set => _configuration.AutoReconnect = value; }
    public TimeSpan ReconnectInterval { get => TimeSpan.FromSeconds(_configuration.ReconnectInitialSeconds); set => _configuration.ReconnectInitialSeconds = Math.Clamp((int)value.TotalSeconds, 1, 60); }
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<BleConnectionEventArgs>? ConnectionChanged;
    public event EventHandler<RpmEventArgs>? RpmReceived;
    public event EventHandler<BleDiagnosticEventArgs>? Diagnostic;
    public event EventHandler<TelemetrySnapshot>? SnapshotReceived;
    public event EventHandler<AgentStatus>? AgentStatusChanged;

    public async Task AttachAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _attachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsAttached) return;
            _receiveStop?.Cancel(); _pipe?.Dispose();
            if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try { await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false); ValidateServerIdentity(pipe); }
            catch (Exception ex) { await pipe.DisposeAsync().ConfigureAwait(false); throw new BleOperationException("无法连接 SmartFan 后台。请确认 SmartFan.Agent.exe 已启动。", ex); }
            _pipe = pipe; _receiveStop = new CancellationTokenSource();
            var receiveToken = _receiveStop.Token;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(pipe, receiveToken), CancellationToken.None);
        }
        finally { _attachGate.Release(); }
        _configuration = await CallAsync<AgentConfiguration>("configuration", null, cancellationToken).ConfigureAwait(false);
        ApplyStatus(await CallAsync<AgentStatus>("status", null, cancellationToken).ConfigureAwait(false), true);
    }
    private static void ValidateServerIdentity(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new UnauthorizedAccessException("前端缺少 Windows 用户 SID。");
        var security = pipe.GetAccessControl();
        ValidateServerSecurity(security, userSid);
    }
    private static void ValidateServerSecurity(PipeSecurity security, SecurityIdentifier userSid)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != userSid || !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("后台管道不属于当前 Windows 用户，或访问控制未受保护。");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(userSid) && !rule.IdentityReference.Equals(systemSid)) ||
            !rules.Any(rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(networkSid) &&
                               (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl))
            throw new UnauthorizedAccessException("后台管道访问范围过宽或未拒绝网络连接。");
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await CallAsync<AgentStatus>("status", null, cancellationToken).ConfigureAwait(false); ApplyStatus(status, true); return status;
    }
    public Task<bool> StopAgentAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("shutdown", null, cancellationToken, TimeSpan.FromSeconds(10));
    public async Task<AgentConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _configuration = await CallAsync<AgentConfiguration>("configuration", null, cancellationToken).ConfigureAwait(false); return AgentJson.Clone(_configuration);
    }
    public async Task ConfigureAsync(AgentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        AgentConfiguration.Validate(configuration);
        _configuration = await CallAsync<AgentConfiguration>("configure", configuration, cancellationToken).ConfigureAwait(false);
    }
    public async Task ConfigureTelemetryAsync(TelemetrySettings settings, CancellationToken cancellationToken = default)
        => _configuration = await CallAsync<AgentConfiguration>("configureTelemetry", settings, cancellationToken).ConfigureAwait(false);
    public async Task SetDemoAsync(bool enabled, CancellationToken cancellationToken = default)
        => _configuration = await CallAsync<AgentConfiguration>("demo", enabled, cancellationToken).ConfigureAwait(false);
    public async Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default)
        => await CallAsync<List<SensorDescriptor>>("sensors", null, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default)
        => await CallAsync<List<BleDeviceInfo>>("scan", new ScanRequest { DurationMilliseconds = (int)duration.TotalMilliseconds, FilterFanService = filterFanService }, cancellationToken, duration + TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    public async Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
        => ApplyStatus(await CallAsync<AgentStatus>("connect", new ConnectRequest { Device = device }, cancellationToken, TimeSpan.FromSeconds(120)).ConfigureAwait(false), false);
    public async Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default)
        => ApplyStatus(await CallAsync<AgentStatus>("connect", new ConnectRequest { AddressOrId = addressOrId }, cancellationToken, TimeSpan.FromSeconds(120)).ConfigureAwait(false), false);
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        => ApplyStatus(await CallAsync<AgentStatus>("disconnect", null, cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false), false);
    public Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default)
        => CallAsync<byte[]>("read", new ReadRequest { Key = key }, cancellationToken);
    public Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default)
    {
        var bytes = payload.ToArray(); PacketCodec.Validate(key, bytes);
        return CallAsync<WriteVerificationResult>("write", new WriteRequest { Key = key, Payload = bytes, Verify = verify }, cancellationToken, OperationTimeout + OperationTimeout);
    }
    public Task<bool> UnpairAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("unpair", null, cancellationToken, TimeSpan.FromSeconds(30));

    private async Task<T> CallAsync<T>(string method, object? payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAttached) throw new BleOperationException("前端尚未接入后台；这不代表后台蓝牙已经断开。");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PipeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("IPC 请求 Id 重复。");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? OperationTimeout);
        try
        {
            await SendAsync(PipeEnvelope.Create("request", id, method, payload), timeoutSource.Token).ConfigureAwait(false);
            PipeEnvelope response;
            try { response = await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await SendCancelHandledAsync(id).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new BleOperationException($"后台 {method} 请求超时。");
            }
            if (!string.IsNullOrWhiteSpace(response.Error)) throw new BleOperationException(response.Error);
            return response.Read<T>();
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PipeFraming.WriteAsync(_pipe ?? throw new IOException("后台管道已断开。"), envelope, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
    private async Task SendCancelHandledAsync(string id)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await SendAsync(PipeEnvelope.Create("cancel", id, "cancel"), timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Diagnostic?.Invoke(this, new BleDiagnosticEventArgs("取消请求未送达后台", ex)); }
    }
    private async Task ReceiveLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await PipeFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (envelope.Type == "response") { if (_pending.TryGetValue(envelope.Id, out var pending)) pending.TrySetResult(envelope); continue; }
                if (envelope.Type != "event") throw new InvalidDataException("未知 IPC 消息类型。");
                switch (envelope.Method)
                {
                    case "status": ApplyStatus(envelope.Read<AgentStatus>(), false); break;
                    case "snapshot": LatestSnapshot = envelope.Read<TelemetrySnapshot>(); SnapshotReceived?.Invoke(this, LatestSnapshot); break;
                    case "rpm": RpmReceived?.Invoke(this, envelope.Read<RpmEventArgs>()); break;
                    case "device": DeviceDiscovered?.Invoke(this, envelope.Read<BleDeviceInfo>()); break;
                    case "diagnostic": Diagnostic?.Invoke(this, new BleDiagnosticEventArgs(envelope.Read<string>())); break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) Diagnostic?.Invoke(this, new BleDiagnosticEventArgs("与后台的界面连接已断开；后台可能仍在运行。", ex));
            foreach (var pending in _pending.Values) pending.TrySetException(new BleOperationException("后台管道已断开。", ex));
        }
        finally
        {
            pipe.Dispose();
            if (!_disposed) ConnectionChanged?.Invoke(this, new BleConnectionEventArgs(BleConnectionState.Disconnected, "前端 IPC 已断开，请重新连接后台"));
        }
    }
    private void ApplyStatus(AgentStatus status, bool replaySnapshot)
    {
        var previousState = AgentStatus.State;
        var previousProcess = AgentStatus.ProcessId;
        AgentStatus = status;
        if (replaySnapshot && status.Snapshot is not null) { LatestSnapshot = status.Snapshot; SnapshotReceived?.Invoke(this, status.Snapshot); }
        AgentStatusChanged?.Invoke(this, status);
        // 同一连接的每秒状态快照不重复伪造“刚连接成功”。
        if (previousState != status.State || previousProcess != status.ProcessId) ConnectionChanged?.Invoke(this, new BleConnectionEventArgs(status.State, status.Message));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _receiveStop?.Cancel();
        _pipe?.Dispose();
        foreach (var pending in _pending.Values) pending.TrySetException(new ObjectDisposedException(nameof(BackgroundClient)));
        if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
        _receiveStop?.Dispose();
        // 此处故意没有 _ble.DisconnectAsync：前端退出不影响真实 BLE 连接。
    }
}
