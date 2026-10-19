using System.Collections.Concurrent;
using System.Globalization;
using SmartFan.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SmartFan.Core.Ble;

/// <summary>
/// 使用 Windows 原生 WinRT BLE。SemaphoreSlim 相当于 Java 的 Semaphore，
/// 保证所有 GATT 请求串行；async/await 类似 CompletableFuture 的异步组合，不阻塞 UI。
/// </summary>
public sealed class NativeBleClient : IBleClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<GattDeviceService> _services = new();
    private readonly Dictionary<CharacteristicKey, GattCharacteristic> _characteristics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reconnectCancellation;
    private CancellationTokenSource? _connectionCancellation;
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _rpmCharacteristic;
    private BleDeviceInfo? _lastDevice;
    private bool _manualDisconnect;
    private bool _disposed;
    private int _recovering;
    private int _scanning;

    public BleConnectionState State { get; private set; } = BleConnectionState.Disconnected;
    public bool IsConnected => State == BleConnectionState.Connected && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public ushort MaxPduSize => _session?.MaxPduSize ?? 23;
    public BleDeviceInfo? ConnectedDevice { get; private set; }
    public IReadOnlyList<DiscoveredCharacteristic> Characteristics { get; private set; } = Array.Empty<DiscoveredCharacteristic>();
    public bool AutoReconnect { get; set; } = true;
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    public event EventHandler<BleConnectionEventArgs>? ConnectionChanged;
    public event EventHandler<RpmEventArgs>? RpmReceived;
    public event EventHandler<BleDiagnosticEventArgs>? Diagnostic;

    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (Interlocked.Exchange(ref _scanning, 1) != 0) throw new BleOperationException("扫描正在进行，请等待当前扫描结束。");
        var devices = new ConcurrentDictionary<ulong, BleDeviceInfo>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var hasService = args.Advertisement.ServiceUuids.Contains(BleUuids.FanService);
            // 主广播带服务 UUID，后续扫描响应可能只有中文名；合并同一地址的信息。
            if (filterFanService && !hasService && !devices.ContainsKey(args.BluetoothAddress)) return;
            devices.AddOrUpdate(args.BluetoothAddress, _ => new BleDeviceInfo
            {
                BluetoothAddress = args.BluetoothAddress, Name = args.Advertisement.LocalName,
                SignalStrength = args.RawSignalStrengthInDBm, AdvertisesFanService = hasService
            }, (_, previous) => new BleDeviceInfo
            {
                BluetoothAddress = args.BluetoothAddress,
                Name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? previous.Name : args.Advertisement.LocalName,
                SignalStrength = args.RawSignalStrengthInDBm, AdvertisesFanService = hasService || previous.AdvertisesFanService
            });
            DeviceDiscovered?.Invoke(this, devices[args.BluetoothAddress]);
        }
        void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (args.Error != BluetoothError.Success) failure.TrySetResult(new BleOperationException($"蓝牙扫描停止：{args.Error}。请检查蓝牙适配器、电源和 Windows 蓝牙设置。"));
        }
        watcher.Received += Received;
        watcher.Stopped += Stopped;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (!IsConnected) SetState(BleConnectionState.Scanning, "正在扫描蓝牙设备");
            watcher.Start();
            var timer = Task.Delay(duration, linked.Token);
            var completed = await Task.WhenAny(timer, failure.Task).ConfigureAwait(false);
            if (completed == failure.Task) throw await failure.Task.ConfigureAwait(false);
            await timer.ConfigureAwait(false);
            return devices.Values.OrderByDescending(item => item.AdvertisesFanService).ThenByDescending(item => item.SignalStrength).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("扫描失败", ex);
            throw ex is BleOperationException ? ex : new BleOperationException("扫描失败。请打开 Windows 蓝牙并检查适配器。", ex);
        }
        finally
        {
            watcher.Received -= Received; watcher.Stopped -= Stopped;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
            Interlocked.Exchange(ref _scanning, 0);
            if (State == BleConnectionState.Scanning) SetState(BleConnectionState.Disconnected, "扫描结束");
        }
    }

    public Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressOrId);
        var compact = addressOrId.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (compact.Length == 12 && ulong.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            return ConnectAsync(new BleDeviceInfo { BluetoothAddress = address }, cancellationToken);
        return ConnectAsync(new BleDeviceInfo { Id = addressOrId.Trim() }, cancellationToken);
    }

    public async Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);
        if (device.BluetoothAddress == 0 && string.IsNullOrWhiteSpace(device.Id)) throw new ArgumentException("设备地址或 Windows DeviceInformation.Id 不能为空。", nameof(device));
        _manualDisconnect = false;
        _reconnectCancellation?.Cancel();
        await ConnectCoreAsync(device, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(BleDeviceInfo target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseConnection();
            SetState(BleConnectionState.Connecting, "正在连接设备");
            _device = !string.IsNullOrWhiteSpace(target.Id)
                ? await RunAsync(BluetoothLEDevice.FromIdAsync(target.Id), "按设备 Id 连接", cancellationToken).ConfigureAwait(false)
                : await RunAsync(BluetoothLEDevice.FromBluetoothAddressAsync(target.BluetoothAddress), "按蓝牙地址连接", cancellationToken).ConfigureAwait(false);
            if (_device is null) throw new BleOperationException("Windows 未能打开此蓝牙设备。请确认地址、蓝牙电源及设备是否可连接。");
            _device.ConnectionStatusChanged += DeviceConnectionStatusChanged;
            _session = await RunAsync(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId), "创建 GATT 会话", cancellationToken).ConfigureAwait(false);
            if (_session is not null) { _session.MaintainConnection = true; _session.MaxPduSizeChanged += SessionMaxPduSizeChanged; }
            _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ConnectedDevice = new BleDeviceInfo
            {
                Id = _device.DeviceId, BluetoothAddress = _device.BluetoothAddress,
                Name = string.IsNullOrWhiteSpace(_device.Name) ? target.Name : _device.Name,
                IsPaired = _device.DeviceInformation.Pairing.IsPaired, AdvertisesFanService = target.AdvertisesFanService
            };
            _lastDevice = ConnectedDevice;
            SetState(BleConnectionState.Discovering, "正在发现并校验 4 个服务、27 个特征");
            var details = new List<DiscoveredCharacteristic>();
            foreach (var serviceUuid in BleUuids.Services)
            {
                var service = await DiscoverServiceAsync(serviceUuid, cancellationToken).ConfigureAwait(false);
                _services.Add(service);
                var found = await DiscoverCharacteristicsAsync(service, cancellationToken).ConfigureAwait(false);
                foreach (var definition in ProtocolCatalog.All.Where(item => item.ServiceUuid == serviceUuid))
                {
                    var characteristic = found.FirstOrDefault(item => item.Uuid == definition.Uuid);
                    if (characteristic is null) throw new BleOperationException($"固件缺少特征 {definition.Name} ({definition.Uuid})。");
                    var properties = characteristic.CharacteristicProperties;
                    var canRead = properties.HasFlag(GattCharacteristicProperties.Read);
                    var canWrite = properties.HasFlag(GattCharacteristicProperties.Write);
                    var canNotify = properties.HasFlag(GattCharacteristicProperties.Notify);
                    if ((definition.CanRead && !canRead) || !canWrite || (definition.CanNotify && !canNotify))
                        throw new BleOperationException($"特征 {definition.Name} 权限不符，实际 {properties}。");
                    _characteristics.Add(definition.Key, characteristic);
                    details.Add(new DiscoveredCharacteristic { Definition = definition, Properties = properties.ToString(), CanRead = canRead, CanWrite = canWrite, CanNotify = canNotify });
                }
            }
            Characteristics = details.AsReadOnly();
            _rpmCharacteristic = GetCharacteristic(CharacteristicKey.FanRpm);
            _rpmCharacteristic.ValueChanged += RpmValueChanged;
            var subscribe = await RunAsync(_rpmCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), "订阅 RPM 通知", cancellationToken).ConfigureAwait(false);
            EnsureSuccess(subscribe, "订阅 RPM 通知");
            SetState(BleConnectionState.Connected, $"已连接 {ConnectedDevice.Name}，MTU {MaxPduSize}，27 个特征已校验");
        }
        catch (OperationCanceledException)
        {
            ReleaseConnection(); SetState(BleConnectionState.Disconnected, "连接已取消"); throw;
        }
        catch (Exception ex)
        {
            ReleaseConnection(); SetState(BleConnectionState.Faulted, ex.Message); Log("连接失败", ex);
            throw ex is BleOperationException ? ex : new BleOperationException("连接失败。若刚重烧固件，请在 Windows 蓝牙设置中删除旧设备后重试。", ex);
        }
        finally { _gate.Release(); }
    }

    private async Task<GattDeviceService> DiscoverServiceAsync(Guid uuid, CancellationToken cancellationToken)
    {
        var device = _device ?? throw new BleOperationException("设备连接已释放。");
        foreach (var cache in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            GattDeviceServicesResult result;
            try { result = await RunAsync(device.GetGattServicesForUuidAsync(uuid, cache), $"发现服务 {uuid} ({cache})", cancellationToken).ConfigureAwait(false); }
            catch (BleOperationException ex) { Log($"服务 {uuid}/{cache} 查询异常，继续后备策略", ex); continue; }
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                var chosen = result.Services[0];
                foreach (var unused in result.Services.Skip(1)) unused.Dispose();
                Log($"服务 {uuid}：按 UUID/{cache} 发现"); return chosen;
            }
            foreach (var unused in result.Services) unused.Dispose();
            Log($"服务 {uuid} 按 UUID/{cache} 发现结果：{result.Status}，尝试后备策略");
        }
        var all = await RunAsync(device.GetGattServicesAsync(BluetoothCacheMode.Uncached), "发现全部服务", cancellationToken).ConfigureAwait(false);
        GattDeviceService? match = null;
        foreach (var service in all.Services)
        {
            if (service.Uuid == uuid && match is null) match = service; else service.Dispose();
        }
        if (all.Status != GattCommunicationStatus.Success || match is null)
        {
            match?.Dispose();
            throw new BleOperationException($"找不到服务 {uuid}。状态：{all.Status}。请确认开发板固件版本；若重烧过固件，请删除 Windows 旧配对缓存。");
        }
        return match;
    }

    private async Task<IReadOnlyList<GattCharacteristic>> DiscoverCharacteristicsAsync(GattDeviceService service, CancellationToken cancellationToken)
    {
        foreach (var cache in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            GattCharacteristicsResult result;
            try { result = await RunAsync(service.GetCharacteristicsAsync(cache), $"发现特征 {service.Uuid}", cancellationToken).ConfigureAwait(false); }
            catch (BleOperationException ex) { Log($"服务 {service.Uuid} 的特征查询异常，继续后备策略", ex); continue; }
            if (result.Status == GattCommunicationStatus.Success)
            {
                var expected = ProtocolCatalog.All.Where(item => item.ServiceUuid == service.Uuid);
                if (expected.All(item => result.Characteristics.Any(found => found.Uuid == item.Uuid))) return result.Characteristics;
            }
            Log($"特征发现 {service.Uuid}/{cache}：{result.Status}，数量 {result.Characteristics.Count}");
        }
        throw new BleOperationException($"服务 {service.Uuid} 的特征不完整，请检查固件或 Windows GATT 缓存。");
    }

    public async Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default)
    {
        if (!ProtocolCatalog.Get(key).CanRead) throw new BleOperationException("fan_rpm 没有 READ 权限；请订阅实测转速通知，不能读取目标转速。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureConnected(); return await ReadCoreAsync(key, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> ReadCoreAsync(CharacteristicKey key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(GetCharacteristic(key).ReadValueAsync(BluetoothCacheMode.Uncached), $"读取 {ProtocolCatalog.Get(key).Name}", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result.Status, $"读取 {ProtocolCatalog.Get(key).Name}", result.ProtocolError);
        var bytes = BufferBytes(result.Value);
        try { PacketCodec.Validate(key, bytes); }
        catch (ArgumentException ex) { throw new BleOperationException($"{ProtocolCatalog.Get(key).Name} 返回了无效载荷：{Convert.ToHexString(bytes)}。{ex.Message}", ex); }
        return bytes;
    }

    public async Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default)
    {
        // 拷贝快照，调用者在等待队列期间修改原数组也不会改变实际发包。
        var bytes = payload.ToArray();
        PacketCodec.Validate(key, bytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var limit = Math.Max(0, MaxPduSize - 3);
            if (bytes.Length > limit) throw new BleOperationException($"载荷 {bytes.Length} 字节超过当前 MTU {MaxPduSize} 的 {limit} 字节单次写入上限。请重连以协商更大 MTU；客户端不会静默截断。");
            using var writer = new DataWriter(); writer.WriteBytes(bytes);
            // 固件只声明 WRITE，不能为了吞吐擅自使用 WriteWithoutResponse。
            var result = await RunAsync(GetCharacteristic(key).WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse), $"写入 {ProtocolCatalog.Get(key).Name}", cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result.Status, $"写入 {ProtocolCatalog.Get(key).Name}", result.ProtocolError);
            if (!verify) return new WriteVerificationResult { Key = key, Status = WriteVerificationStatus.NotRequested };
            if (key == CharacteristicKey.FanPwm) return new WriteVerificationResult { Key = key, Status = WriteVerificationStatus.FirmwarePwmReadbackUnavailable };
            if (key == CharacteristicKey.FanRpm) return new WriteVerificationResult { Key = key, Status = WriteVerificationStatus.TargetRpmNotReadable };
            // 写与读回共占一个队列位置，避免其他调用在中间覆盖同一配置。
            var readback = await ReadCoreAsync(key, cancellationToken).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(readback)) throw new BleOperationException($"{ProtocolCatalog.Get(key).Name} 读回不一致：写 {Convert.ToHexString(bytes)}，读 {Convert.ToHexString(readback)}。请报告固件接口问题。");
            return new WriteVerificationResult { Key = key, Status = WriteVerificationStatus.Verified, Readback = readback };
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _manualDisconnect = true;
        _reconnectCancellation?.Cancel();
        _connectionCancellation?.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rpmCharacteristic is not null && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                try
                {
                    var status = await RunAsync(_rpmCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None), "取消 RPM 订阅", cancellationToken, false).ConfigureAwait(false);
                    if (status != GattCommunicationStatus.Success) Log($"取消订阅结果：{status}；继续释放连接资源");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log("取消订阅失败，继续释放连接资源", ex); }
            }
        }
        finally
        {
            // CCCD 取消恰好遇到外部取消时也必须释放句柄，避免留下半断开的连接。
            ReleaseConnection(); SetState(BleConnectionState.Disconnected, "已断开连接");
            _gate.Release();
        }
    }

    public async Task<bool> UnpairAsync(CancellationToken cancellationToken = default)
    {
        var target = ConnectedDevice ?? _lastDevice ?? throw new BleOperationException("没有可忘记的设备。");
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        BluetoothLEDevice? temporary = null;
        try
        {
            temporary = !string.IsNullOrWhiteSpace(target.Id)
                ? await RunAsync(BluetoothLEDevice.FromIdAsync(target.Id), "打开配对设备", cancellationToken, false).ConfigureAwait(false)
                : await RunAsync(BluetoothLEDevice.FromBluetoothAddressAsync(target.BluetoothAddress), "打开配对设备", cancellationToken, false).ConfigureAwait(false);
            if (temporary is null) throw new BleOperationException("无法打开设备，请到 Windows 蓝牙设置中手动删除。");
            var result = await RunAsync(temporary.DeviceInformation.Pairing.UnpairAsync(), "忘记蓝牙设备", cancellationToken, false).ConfigureAwait(false);
            return result.Status == Windows.Devices.Enumeration.DeviceUnpairingResultStatus.Unpaired || result.Status == Windows.Devices.Enumeration.DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        finally { temporary?.Dispose(); }
    }

    private async Task<T> RunAsync<T>(IAsyncOperation<T> operation, string description, CancellationToken cancellationToken, bool linkConnection = true)
    {
        using var linked = linkConnection && _connectionCancellation is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, _connectionCancellation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(OperationTimeout > TimeSpan.Zero ? OperationTimeout : TimeSpan.FromSeconds(10));
        try { return await operation.AsTask(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested && !(linkConnection && _connectionCancellation?.IsCancellationRequested == true))
        { throw new BleOperationException($"{description} 超时（{OperationTimeout.TotalSeconds:0.#} 秒）。请检查连接、电源及配对缓存。", ex); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new BleOperationException($"{description} 失败：{ex.GetType().Name}，HRESULT 0x{ex.HResult:X8}，{ex.Message}。若重烧固件后出现此问题，请删除 Windows 旧配对缓存。", ex); }
    }

    private void DeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || _manualDisconnect || _disposed) return;
        // 连接发现早期 Windows 可能短暂报告 Disconnected；仅已建立会话进入恢复流程。
        if (State != BleConnectionState.Connected) return;
        _connectionCancellation?.Cancel();
        _ = RecoverAsync();
    }

    private async Task RecoverAsync()
    {
        if (Interlocked.Exchange(ref _recovering, 1) != 0) return;
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { ReleaseConnection(); SetState(BleConnectionState.Disconnected, "设备连接已断开"); }
            finally { _gate.Release(); }
            if (!AutoReconnect || _manualDisconnect || _lastDevice is null) return;
            _reconnectCancellation?.Dispose();
            _reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _reconnectCancellation.Token;
            var attempt = 0;
            while (AutoReconnect && !_manualDisconnect && !token.IsCancellationRequested)
            {
                var delaySeconds = Math.Min(30, Math.Max(1, ReconnectInterval.TotalSeconds) * Math.Pow(2, Math.Min(attempt, 4)));
                SetState(BleConnectionState.Reconnecting, $"连接断开，{delaySeconds:0} 秒后重试（第 {attempt + 1} 次）");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                try { await ConnectCoreAsync(_lastDevice, token).ConfigureAwait(false); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { attempt++; Log("自动重连失败", ex); }
            }
        }
        catch (OperationCanceledException) { Log("自动重连已取消"); }
        catch (Exception ex) { Log("自动重连停止", ex); SetState(BleConnectionState.Faulted, ex.Message); }
        finally { Interlocked.Exchange(ref _recovering, 0); }
    }

    private void RpmValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var bytes = BufferBytes(args.CharacteristicValue); PacketCodec.Validate(CharacteristicKey.FanRpm, bytes);
            RpmReceived?.Invoke(this, new RpmEventArgs(PacketCodec.DecodeUInt32(bytes), DateTimeOffset.UtcNow));
        }
        catch (Exception ex) { Log("RPM 通知载荷无效或通知处理失败", ex); }
    }

    private void SessionMaxPduSizeChanged(GattSession sender, object args) => Log($"MTU 已更新为 {sender.MaxPduSize}");
    private GattCharacteristic GetCharacteristic(CharacteristicKey key) => _characteristics.TryGetValue(key, out var value) ? value : throw new BleOperationException($"未发现特征 {ProtocolCatalog.Get(key).Name}。");
    private void EnsureConnected() { ObjectDisposedException.ThrowIf(_disposed, this); if (!IsConnected) throw new BleOperationException("尚未连接设备，请先连接后再操作。"); }
    private static void EnsureSuccess(GattCommunicationStatus status, string description, byte? protocolError = null)
    {
        if (status != GattCommunicationStatus.Success) throw new BleOperationException($"{description} 失败：{status}{(protocolError.HasValue ? $"，ATT 0x{protocolError:X2}" : "")}。请检查固件接口、设备电源及 Windows 配对缓存。");
    }
    private static byte[] BufferBytes(IBuffer buffer) { var bytes = new byte[buffer.Length]; using var reader = DataReader.FromBuffer(buffer); reader.ReadBytes(bytes); return bytes; }
    private void SetState(BleConnectionState state, string message) { State = state; ConnectionChanged?.Invoke(this, new BleConnectionEventArgs(state, message)); Log(message); }
    private void Log(string message, Exception? exception = null) => Diagnostic?.Invoke(this, new BleDiagnosticEventArgs(message, exception));

    private void ReleaseConnection()
    {
        if (_rpmCharacteristic is not null) { _rpmCharacteristic.ValueChanged -= RpmValueChanged; _rpmCharacteristic = null; }
        if (_device is not null) _device.ConnectionStatusChanged -= DeviceConnectionStatusChanged;
        if (_session is not null) { _session.MaxPduSizeChanged -= SessionMaxPduSizeChanged; _session.MaintainConnection = false; _session.Dispose(); _session = null; }
        foreach (var service in _services) service.Dispose();
        _services.Clear(); _characteristics.Clear();
        _device?.Dispose(); _device = null;
        _connectionCancellation?.Cancel(); _connectionCancellation?.Dispose(); _connectionCancellation = null;
        ConnectedDevice = null; Characteristics = Array.Empty<DiscoveredCharacteristic>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true; _lifetime.Cancel(); _reconnectCancellation?.Dispose(); _lifetime.Dispose();
        // 正在结束的通知 / 重连任务仍可能释放 gate，不提前 Dispose semaphore。
    }
}
