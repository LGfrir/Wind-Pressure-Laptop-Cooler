using SmartFan.App.Models;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;

namespace SmartFan.Runtime;

public sealed class AgentCharacteristicStatus
{
    public CharacteristicKey Key { get; set; }
    public string Properties { get; set; } = "";
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanNotify { get; set; }
}

public sealed class AgentStatus
{
    public int ProcessId { get; set; }
    public string BootId { get; set; } = "";
    public string ConfigurationPath { get; set; } = "";
    public long ConnectionGeneration { get; set; }
    public bool IsAdministrator { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public BleConnectionState State { get; set; }
    public bool IsConnected { get; set; }
    public ushort MaxPduSize { get; set; } = 23;
    public BleDeviceInfo? ConnectedDevice { get; set; }
    public List<AgentCharacteristicStatus> Characteristics { get; set; } = new();
    public TelemetrySnapshot? Snapshot { get; set; }
    public long SampleCount { get; set; }
    public uint? LastRpm { get; set; }
    public DateTimeOffset? LastRpmAt { get; set; }
    public string Message { get; set; } = "后台正在启动";
    public string TelemetryMessage { get; set; } = "自动上报已关闭";
    public bool EnabledTelemetry { get; set; }
    public bool DemoMode { get; set; }
}
