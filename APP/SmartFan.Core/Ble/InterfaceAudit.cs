using SmartFan.Core.Protocol;

namespace SmartFan.Core.Ble;

public enum InterfaceCheckStatus { Passed, Failed, KnownFirmwareLimitation, NotTested }

public sealed class InterfaceCheckResult
{
    public required CharacteristicKey Key { get; init; }
    public string Name => ProtocolCatalog.Get(Key).Name;
    public required InterfaceCheckStatus Status { get; init; }
    public required string Message { get; init; }
    public byte[]? Payload { get; init; }
    public string PayloadHex => Payload is null ? "—" : Convert.ToHexString(Payload);
}

/// <summary>控制台与 UI 共用的只读自检；不会改变风扇、灯光或 flash。</summary>
public static class InterfaceAudit
{
    public static async Task<IReadOnlyList<InterfaceCheckResult>> RunReadOnlyAsync(IBleClient client, IProgress<InterfaceCheckResult>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.IsConnected) throw new BleOperationException("自检前请先连接设备。");
        var checks = new List<InterfaceCheckResult>();
        foreach (var definition in ProtocolCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterfaceCheckResult result;
            if (!definition.CanRead)
            {
                result = new InterfaceCheckResult
                {
                    Key = definition.Key, Status = InterfaceCheckStatus.KnownFirmwareLimitation,
                    Message = "固件未提供 READ；连接时已校验 NOTIFY/WRITE 并订阅。请在 RPM 历史中确认通知；写入值代表目标转速。"
                };
            }
            else
            {
                try
                {
                    var payload = await client.ReadAsync(definition.Key, cancellationToken).ConfigureAwait(false);
                    result = new InterfaceCheckResult
                    {
                        Key = definition.Key, Payload = payload,
                        Status = definition.Key == CharacteristicKey.FanPwm ? InterfaceCheckStatus.KnownFirmwareLimitation : InterfaceCheckStatus.Passed,
                        Message = definition.Key == CharacteristicKey.FanPwm ? "读取成功；固件当前恒返回 0，不能证明实际 PWM。" : $"读取 {payload.Length} 字节，长度与取值校验通过；未进行写入或物理效果验收。"
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = new InterfaceCheckResult { Key = definition.Key, Status = InterfaceCheckStatus.Failed, Message = ex.Message }; }
            }
            checks.Add(result); progress?.Report(result);
        }
        return checks.AsReadOnly();
    }
}
