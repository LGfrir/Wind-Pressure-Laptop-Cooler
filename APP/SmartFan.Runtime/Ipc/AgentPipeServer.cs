using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFan.Core.Ble;

namespace SmartFan.Runtime.Ipc;

public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly AgentHost _host;
    private readonly Action<string>? _log;
    private readonly SecurityIdentifier _userSid;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptTask;
    public AgentPipeServer(string pipeName, AgentHost host, Action<string>? log = null)
    {
        _pipeName = pipeName; _host = host; _log = log;
        using (var identity = WindowsIdentity.GetCurrent())
            _userSid = identity.User ?? throw new UnauthorizedAccessException("后台缺少 Windows 用户 SID。");
        _host.StatusChanged += (_, status) => Broadcast("status", status);
        _host.SnapshotReceived += (_, snapshot) => Broadcast("snapshot", snapshot);
        _host.Rpm += (_, rpm) => Broadcast("rpm", new { rpm.Rpm, rpm.Timestamp });
        _host.DeviceFound += (_, device) => Broadcast("device", device);
        _host.LogMessage += (_, message) => Broadcast("diagnostic", message);
    }
    public int ClientCount => _clients.Count;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _acceptTask ??= Task.Run(() => AcceptLoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateLocalUserPipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var session = new ClientSession(pipe, this, cancellationToken);
                _clients[session.Id] = session;
                _ = session.RunAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false); break; }
            catch (Exception ex) { if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false); _log?.Invoke($"后台管道接入失败：{ex.Message}"); await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
        }
    }
    private NamedPipeServerStream CreateLocalUserPipe()
    {
        // .NET 10 CurrentUserOnly uses token.Owner, which changes to Administrators
        // after UAC elevation. Authorize the actual account SID instead.
        var security = new PipeSecurity(); security.SetOwner(_userSid);
        security.SetAccessRuleProtection(true, false);
        // Named pipes can otherwise be opened through SMB, even by the same account.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(_userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
    private void ValidateClientIdentity(NamedPipeServerStream pipe)
    {
        SecurityIdentifier? clientSid = null;
        // This must run after the first read, when Windows has the client's pipe token.
        // Identification-level SQOS permits querying identity, never executing work as it.
        pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); clientSid = identity?.User; });
        if (clientSid != _userSid) throw new UnauthorizedAccessException("拒绝不同 Windows 用户的后台管道请求。");
    }
    private void Broadcast(string method, object payload)
    {
        foreach (var client in _clients.Values) _ = client.SendEventHandledAsync(PipeEnvelope.Create("event", "", method, payload));
    }
    private async Task<object?> DispatchAsync(PipeEnvelope request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "status": return _host.GetStatus();
            case "configuration": return _host.GetConfiguration();
            case "shutdown": return true;
            case "configure": await _host.ConfigureAsync(request.Read<AgentConfiguration>(), cancellationToken).ConfigureAwait(false); return _host.GetConfiguration();
            case "configureTelemetry": await _host.ConfigureTelemetryAsync(request.Read<SmartFan.App.Models.TelemetrySettings>(), cancellationToken).ConfigureAwait(false); return _host.GetConfiguration();
            case "demo": await _host.SetDemoAsync(request.Read<bool>(), cancellationToken).ConfigureAwait(false); return _host.GetConfiguration();
            case "sensors": return await _host.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false);
            case "scan":
                var scan = request.Read<ScanRequest>();
                return await _host.BleClient.ScanAsync(TimeSpan.FromMilliseconds(scan.DurationMilliseconds), scan.FilterFanService, cancellationToken).ConfigureAwait(false);
            case "connect":
                var connect = request.Read<ConnectRequest>();
                if (connect.Device is not null) await _host.ConnectAsync(connect.Device, cancellationToken).ConfigureAwait(false);
                else if (!string.IsNullOrWhiteSpace(connect.AddressOrId))
                {
                    await _host.ConnectAsync(connect.AddressOrId, cancellationToken).ConfigureAwait(false);
                }
                else throw new InvalidDataException("连接请求缺少设备。");
                return _host.GetStatus();
            case "disconnect": await _host.DisconnectAsync(cancellationToken).ConfigureAwait(false); return _host.GetStatus();
            case "read": return await _host.BleClient.ReadAsync(request.Read<ReadRequest>().Key, cancellationToken).ConfigureAwait(false);
            case "write":
                var write = request.Read<WriteRequest>();
                return await _host.WriteAsync(write.Key, write.Payload, write.Verify, cancellationToken).ConfigureAwait(false);
            case "unpair": return await _host.BleClient.UnpairAsync(cancellationToken).ConfigureAwait(false);
            default: throw new InvalidDataException($"未知后台方法：{request.Method}");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        foreach (var client in _clients.Values) await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear(); _stop.Dispose();
    }

    private sealed class ClientSession : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly AgentPipeServer _server;
        private readonly CancellationTokenSource _stop;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
        private int _disposed;
        private bool _identityValidated;
        public ClientSession(NamedPipeServerStream pipe, AgentPipeServer server, CancellationToken cancellationToken)
        { _pipe = pipe; _server = server; _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); }
        public Guid Id { get; } = Guid.NewGuid();
        public async Task RunAsync()
        {
            try
            {
                await SendAsync(PipeEnvelope.Create("event", "", "status", _server._host.GetStatus()), _stop.Token).ConfigureAwait(false);
                while (!_stop.IsCancellationRequested)
                {
                    var request = await PipeFraming.ReadAsync(_pipe, _stop.Token).ConfigureAwait(false);
                    if (!_identityValidated) { _server.ValidateClientIdentity(_pipe); _identityValidated = true; }
                    if (request.Type == "cancel")
                    {
                        if (_requests.TryGetValue(request.Id, out var cancel)) cancel.Cancel();
                        continue;
                    }
                    if (request.Type != "request" || string.IsNullOrWhiteSpace(request.Id)) throw new InvalidDataException("IPC 请求类型或 Id 无效。");
                    if (_requests.Count >= 32) throw new InvalidDataException("IPC 同时在途请求超过 32 个。");
                    var source = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    source.CancelAfter(TimeSpan.FromSeconds(150));
                    if (!_requests.TryAdd(request.Id, source)) { source.Dispose(); throw new InvalidDataException("重复的 IPC 请求 Id。"); }
                    _ = HandleRequestAsync(request, source);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (EndOfStreamException) { _server._log?.Invoke("前端已退出，后台继续运行"); }
            catch (IOException ex) { _server._log?.Invoke($"前端管道已断开，后台继续运行：{ex.Message}"); }
            catch (Exception ex) { _server._log?.Invoke($"前端管道错误：{ex.Message}"); }
            finally { _server._clients.TryRemove(Id, out _); await DisposeAsync().ConfigureAwait(false); }
        }
        private async Task HandleRequestAsync(PipeEnvelope request, CancellationTokenSource source)
        {
            try
            {
                object? result = null; string? error = null;
                try { result = await _server.DispatchAsync(request, source.Token).ConfigureAwait(false); }
                catch (Exception ex) { error = ex is OperationCanceledException ? "后台操作已取消或超时。" : ex.Message; }
                await SendAsync(PipeEnvelope.Create("response", request.Id, request.Method, result, error), _stop.Token).ConfigureAwait(false);
                // 必须先把 ACK 写进管道，再通知宿主关闭生命周期；否则前端只能看到断管。
                if (request.Method == "shutdown" && error is null) _server._host.RequestShutdown();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { _server._log?.Invoke($"前端响应未送达：{ex.Message}"); }
            finally { _requests.TryRemove(request.Id, out _); source.Dispose(); }
        }
        public async Task SendEventHandledAsync(PipeEnvelope envelope)
        {
            try { await SendAsync(envelope, _stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            { _server._log?.Invoke($"前端事件推送停止：{ex.Message}"); await DisposeAsync().ConfigureAwait(false); }
        }
        private async Task SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _writeGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try { await PipeFraming.WriteAsync(_pipe, envelope, timeout.Token).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel(); foreach (var source in _requests.Values) source.Cancel();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            // 请求任务会自行释放自己的 CTS；关闭前端只取消该前端请求，不断开 BLE。
        }
    }
}
