using CommunityToolkit.Mvvm.ComponentModel;
using SmartFan.Core.Protocol;

namespace SmartFan.App.ViewModels;

/// <summary>保留原始载荷与独立展开状态，解析只使用共享协议层，不触发设备操作。</summary>
public partial class DiagnosticRow : ObservableObject
{
    public DiagnosticRow(CharacteristicDefinition definition) { Definition = definition; ClearParsedFields(); }
    public CharacteristicDefinition Definition { get; }
    public CharacteristicKey Key => Definition.Key;
    public string Name => Definition.Name;
    public string Uuid => Definition.Uuid.ToString();
    public string LayoutDescription => (Definition.FixedLength is { } length ? $"固定 {length} 字节" : "变长颜色数组") + " · 多字节数值为小端序 · 字节偏移从 0 开始";
    public string Notes => Key switch
    {
        CharacteristicKey.FanPwm => "当前固件的 PWM 读取接口恒回 0；这里解析的是收到的字节，不代表实际占空比或刚写入的设置。",
        CharacteristicKey.FanRpm => "该接口不支持 READ。这里显示最近的 ESP32 转速通知；写入同一接口表示目标 RPM，含义与通知不同。",
        _ when Definition.ServiceUuid == BleUuids.LaptopService => "这里是 ESP32 保存的电脑遥测值，并非重新读取电脑传感器。0 也可能是设备初始值；真实传感器可在高级设置中查看。",
        _ when Definition.WritesFlash => "此配置支持写入设备持久化存储。展开解析只查看数据，不会写入或应用设置。",
        _ => "展开解析只查看已取得的数据，不会写入或应用设置。"
    };

    [ObservableProperty] private string _properties = "未发现";
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private string _result = "尚未测试";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasPayload;
    [ObservableProperty] private bool _isInvalidPayload;
    [ObservableProperty] private string _summary = "尚无载荷，运行读取自检后可查看解析。";
    [ObservableProperty] private string _parseStatus = "等待数据";
    [ObservableProperty] private string _payloadSource = "尚未读取";
    [ObservableProperty] private IReadOnlyList<DiagnosticPayloadField> _fields = Array.Empty<DiagnosticPayloadField>();

    public void ApplyPayload(byte[] payload, string source, DateTimeOffset? capturedAt = null)
    {
        Hex = Convert.ToHexString(payload);
        if (payload.Length == 0) DecodePayload(payload);
        PayloadSource = $"{source} · {capturedAt ?? DateTimeOffset.Now:HH:mm:ss}";
    }

    public void ClearPayload(string reason)
    {
        Hex = "";
        ClearParsedFields();
        PayloadSource = reason;
    }

    partial void OnHexChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { ClearParsedFields(); return; }
        try
        {
            DecodePayload(Convert.FromHexString(value));
        }
        catch (FormatException)
        {
            HasPayload = true; Fields = Array.Empty<DiagnosticPayloadField>(); IsInvalidPayload = true;
            ParseStatus = "HEX 不合法"; Summary = "原始载荷不是完整的十六进制字节。";
        }
    }

    private void DecodePayload(ReadOnlySpan<byte> payload)
    {
        HasPayload = true;
        var decoded = DiagnosticPayloadDecoder.Decode(Key, payload);
        Fields = decoded.Fields; IsInvalidPayload = !decoded.IsValid;
        ParseStatus = decoded.IsValid ? "解析通过" : "载荷不合法";
        Summary = decoded.IsValid ? decoded.Summary : decoded.Error ?? "无法解析此载荷。";
    }

    private void ClearParsedFields()
    {
        Fields = Array.Empty<DiagnosticPayloadField>(); HasPayload = false; IsInvalidPayload = false;
        ParseStatus = "等待数据";
        Summary = Key == CharacteristicKey.FanRpm ? "等待 ESP32 转速通知；该接口不支持读取。" : "尚无有效载荷，运行读取自检后可查看解析。";
    }
}
