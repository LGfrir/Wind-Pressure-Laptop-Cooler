using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;

namespace SmartFan.Runtime.Ipc;

public static class AgentPipeName
{
    // 同一用户跨管理员/普通窗口使用同一后台；服务端 ACL 与双方 User SID 校验限制本地访问。
    public static string Default => "SmartFan.Agent.v1." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName).Replace('\\', '_');
}

public sealed class PipeEnvelope
{
    public string Type { get; set; } = "request";
    public string Id { get; set; } = "";
    public string Method { get; set; } = "";
    public JsonElement Payload { get; set; }
    public string? Error { get; set; }
    public static PipeEnvelope Create(string type, string id, string method, object? payload = null, string? error = null) => new()
    { Type = type, Id = id, Method = method, Payload = JsonSerializer.SerializeToElement(payload, AgentJson.Options), Error = error };
    public T Read<T>() => Payload.Deserialize<T>(AgentJson.Options) ?? throw new InvalidDataException($"IPC {Method} 缺少有效载荷。");
}

public static class PipeFraming
{
    public const int MaximumFrameBytes = 1024 * 1024;
    public static async Task WriteAsync(Stream stream, PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, AgentJson.Options);
        if (payload.Length > MaximumFrameBytes) throw new InvalidDataException("IPC 载荷超过 1 MiB。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public static async Task<PipeEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaximumFrameBytes) throw new InvalidDataException("IPC 帧长度无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PipeEnvelope>(bytes, AgentJson.Options) ?? throw new InvalidDataException("IPC 帧为空。");
    }
}

public sealed class ScanRequest { public int DurationMilliseconds { get; set; } = 8000; public bool FilterFanService { get; set; } = true; }
public sealed class ConnectRequest { public BleDeviceInfo? Device { get; set; } public string? AddressOrId { get; set; } }
public sealed class ReadRequest { public CharacteristicKey Key { get; set; } }
public sealed class WriteRequest { public CharacteristicKey Key { get; set; } public byte[] Payload { get; set; } = Array.Empty<byte>(); public bool Verify { get; set; } = true; }
