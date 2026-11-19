using SmartFan.Core.Protocol;

namespace SmartFan.Core.Ble;

public enum BleConnectionState { Disconnected, Scanning, Connecting, Discovering, Connected, Reconnecting, Faulted }

public sealed class BleDeviceInfo
{
    public string Id { get; set; } = "";
    public ulong BluetoothAddress { get; set; }
    public string Name { get; set; } = "";
    public short SignalStrength { get; set; }
    public bool IsPaired { get; set; }
    public bool AdvertisesFanService { get; set; }
    public string Address => string.Join(":", Enumerable.Range(0, 6).Select(i => ((BluetoothAddress >> ((5 - i) * 8)) & 0xff).ToString("X2")));
    public override string ToString() => $"{(string.IsNullOrWhiteSpace(Name) ? "未命名设备" : Name)} · {Address}";
}

public sealed class BleConnectionEventArgs : EventArgs
{
    public BleConnectionEventArgs(BleConnectionState state, string message) { State = state; Message = message; }
    public BleConnectionState State { get; }
    public string Message { get; }
}

public sealed class RpmEventArgs : EventArgs
{
    public RpmEventArgs(uint rpm, DateTimeOffset timestamp) { Rpm = rpm; Timestamp = timestamp; }
    public uint Rpm { get; }
    public DateTimeOffset Timestamp { get; }
}

public sealed class BleDiagnosticEventArgs : EventArgs
{
    public BleDiagnosticEventArgs(string message, Exception? exception = null) { Message = message; Exception = exception; }
    public string Message { get; }
    public Exception? Exception { get; }
}

public sealed class DiscoveredCharacteristic
{
    public required CharacteristicDefinition Definition { get; init; }
    public required string Properties { get; init; }
    public bool CanRead { get; init; }
    public bool CanWrite { get; init; }
    public bool CanNotify { get; init; }
}

public enum WriteVerificationStatus { Verified, NotRequested, FirmwarePwmReadbackUnavailable, TargetRpmNotReadable }

public sealed class WriteVerificationResult
{
    public required CharacteristicKey Key { get; init; }
    public required WriteVerificationStatus Status { get; init; }
    public byte[]? Readback { get; init; }
    public bool IsVerified => Status == WriteVerificationStatus.Verified;
    public string Message => Status switch
    {
        WriteVerificationStatus.Verified => "已写入并读回验证",
        WriteVerificationStatus.FirmwarePwmReadbackUnavailable => "已收到写入应答；固件 PWM 读取恒为 0，无法读回验证",
        WriteVerificationStatus.TargetRpmNotReadable => "已收到写入应答；目标 RPM 没有读取接口，通知仅代表实测 RPM",
        _ => "已收到写入应答，未要求读回验证"
    };
}

public interface IBleClient : IAsyncDisposable
{
    BleConnectionState State { get; }
    bool IsConnected { get; }
    ushort MaxPduSize { get; }
    BleDeviceInfo? ConnectedDevice { get; }
    IReadOnlyList<DiscoveredCharacteristic> Characteristics { get; }
    bool AutoReconnect { get; set; }
    TimeSpan ReconnectInterval { get; set; }
    TimeSpan OperationTimeout { get; set; }
    event EventHandler<BleDeviceInfo>? DeviceDiscovered;
    event EventHandler<BleConnectionEventArgs>? ConnectionChanged;
    event EventHandler<RpmEventArgs>? RpmReceived;
    event EventHandler<BleDiagnosticEventArgs>? Diagnostic;
    Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, bool filterFanService = true, CancellationToken cancellationToken = default);
    Task ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default);
    Task ConnectAsync(string addressOrId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<byte[]> ReadAsync(CharacteristicKey key, CancellationToken cancellationToken = default);
    Task<WriteVerificationResult> WriteAsync(CharacteristicKey key, ReadOnlyMemory<byte> payload, bool verify = true, CancellationToken cancellationToken = default);
    Task<bool> UnpairAsync(CancellationToken cancellationToken = default);
}

public sealed class BleOperationException : Exception
{
    public BleOperationException(string message, Exception? innerException = null) : base(message, innerException) { }
}
