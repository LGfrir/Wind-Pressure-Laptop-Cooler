namespace SmartFan.Core.Protocol;

public enum CharacteristicKey
{
    FanMode, FanPwm, FanRpm,
    LightMode, LightUpdatePeriod, LightBrightness,
    StaticConfig, RainbowConfig, BreathConfig, RandomFlashConfig,
    BlinkConfig, CustomBreathConfig, ChaseConfig, RoundConfig,
    LaptopCpuUsage, LaptopCpuTemperature, LaptopCpuVoltage, LaptopCpuClock,
    LaptopGpuUsage, LaptopGpuTemperature, LaptopGpuClock, LaptopGpuMemoryUsed,
    LaptopGpuMemoryTotal, LaptopMemoryUsed, LaptopMemoryTotal, LaptopFanUsed, LaptopFanSpeed
}

public enum FanMode { Manual = 0, Automatic = 1, FullSpeed = 2 }
public enum LightMode { Off = 0, Static = 1, Rainbow = 2, Breath = 3, RandomFlash = 4, Blink = 5, CustomBreath = 6, Chase = 7, Round = 8 }

/// <summary>UUID 的唯一来源。字符串以固件 gatt_svc.h 的实际服务表为准。</summary>
public static class BleUuids
{
    public static readonly Guid FanService = new("00000000-0000-0000-0063-76735f6e6166");
    public static readonly Guid LightService = new("00000000-0000-0063-7673-5f746867696c");
    public static readonly Guid ConfigurationService = new("00000000-0000-0000-0063-76735f676663");
    public static readonly Guid LaptopService = new("00000000-0000-6376-735f-706f7470616c");
    public static IReadOnlyList<Guid> Services { get; } = Array.AsReadOnly(new[] { FanService, LightService, ConfigurationService, LaptopService });

    public static Guid For(CharacteristicKey key) => ProtocolCatalog.Get(key).Uuid;
}

public sealed class CharacteristicDefinition
{
    public CharacteristicDefinition(CharacteristicKey key, string name, Guid serviceUuid, string uuid, int? fixedLength = null, bool canRead = true, bool canNotify = false, bool writesFlash = false)
    {
        Key = key; Name = name; ServiceUuid = serviceUuid; Uuid = new Guid(uuid);
        FixedLength = fixedLength; CanRead = canRead; CanNotify = canNotify; WritesFlash = writesFlash;
    }
    public CharacteristicKey Key { get; }
    public string Name { get; }
    public Guid ServiceUuid { get; }
    public Guid Uuid { get; }
    public int? FixedLength { get; }
    public bool CanRead { get; }
    public bool CanWrite => true;
    public bool CanNotify { get; }
    public bool WritesFlash { get; }
}

public static class ProtocolCatalog
{
    public static IReadOnlyList<CharacteristicDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new CharacteristicDefinition(CharacteristicKey.FanMode, "fan_mode", BleUuids.FanService, "00000000-0000-0000-6564-6f6d5f6e6166", 4),
        new CharacteristicDefinition(CharacteristicKey.FanPwm, "fan_pwm", BleUuids.FanService, "00000000-0000-0000-006d-77705f6e6166", 1),
        new CharacteristicDefinition(CharacteristicKey.FanRpm, "fan_rpm", BleUuids.FanService, "00000000-0000-0000-006d-70725f6e6166", 4, false, true),
        new CharacteristicDefinition(CharacteristicKey.LightMode, "light_mode", BleUuids.LightService, "00000000-0000-6564-6f6d-5f746867696c", 4, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.LightUpdatePeriod, "light_update_per", BleUuids.LightService, "7265705f-6574-6164-7075-5f746867696c", 1),
        new CharacteristicDefinition(CharacteristicKey.LightBrightness, "light_brightness", BleUuids.LightService, "7373656e-7468-6769-7262-5f746867696c", 1, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.StaticConfig, "cfg_lt_static", BleUuids.ConfigurationService, "00000063-6974-6174-735f-746c5f676663", 3, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.RainbowConfig, "cfg_lt_rainbow", BleUuids.ConfigurationService, "0000776f-626e-6961-725f-746c5f676663", 2, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.BreathConfig, "cfg_lt_breath", BleUuids.ConfigurationService, "00000068-7461-6572-625f-746c5f676663", 5, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.RandomFlashConfig, "cfg_lt_randflash", BleUuids.ConfigurationService, "6873616c-6664-6e61-725f-746c5f676663", 1, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.BlinkConfig, "cfg_lt_blink", BleUuids.ConfigurationService, "00000000-6b6e-696c-625f-746c5f676663", 9, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.CustomBreathConfig, "cfg_lt_custbreath", BleUuids.ConfigurationService, "74616572-6274-7375-635f-746c5f676663", writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.ChaseConfig, "cfg_lt_chase", BleUuids.ConfigurationService, "00000000-6573-6168-635f-746c5f676663", writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.RoundConfig, "cfg_lt_round", BleUuids.ConfigurationService, "00000000-646e-756f-725f-746c5f676663", 22, writesFlash: true),
        new CharacteristicDefinition(CharacteristicKey.LaptopCpuUsage, "laptop_cpu_usage", BleUuids.LaptopService, "65676173-755f-7570-635f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopCpuTemperature, "laptop_cpu_temp", BleUuids.LaptopService, "00706d65-745f-7570-635f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopCpuVoltage, "laptop_cpu_volta", BleUuids.LaptopService, "61746c6f-765f-7570-635f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopCpuClock, "laptop_cpu_clock", BleUuids.LaptopService, "6b636f6c-635f-7570-635f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopGpuUsage, "laptop_gpu_usage", BleUuids.LaptopService, "65676173-755f-7570-675f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopGpuTemperature, "laptop_gpu_temp", BleUuids.LaptopService, "00706d65-745f-7570-675f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopGpuClock, "laptop_gpu_clock", BleUuids.LaptopService, "6b636f6c-635f-7570-675f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopGpuMemoryUsed, "laptop_gpu_ram", BleUuids.LaptopService, "00006d61-725f-7570-675f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopGpuMemoryTotal, "laptop_gpu_tot_r", BleUuids.LaptopService, "725f746f-745f-7570-675f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopMemoryUsed, "laptop_ram", BleUuids.LaptopService, "00000000-0000-6d61-725f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopMemoryTotal, "laptop_tot_ram", BleUuids.LaptopService, "00006d61-725f-746f-745f-706f7470616c", 8),
        new CharacteristicDefinition(CharacteristicKey.LaptopFanUsed, "laptop_fan_used", BleUuids.LaptopService, "00646573-755f-6e61-665f-706f7470616c", 40),
        new CharacteristicDefinition(CharacteristicKey.LaptopFanSpeed, "laptop_fan_speed", BleUuids.LaptopService, "64656570-735f-6e61-665f-706f7470616c", 40)
    });

    public static CharacteristicDefinition Get(CharacteristicKey key)
    {
        foreach (var item in All) if (item.Key == key) return item;
        throw new ArgumentOutOfRangeException(nameof(key));
    }

    public static bool TryGet(string name, out CharacteristicKey key)
    {
        foreach (var item in All)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
            { key = item.Key; return true; }
        }
        key = default; return false;
    }
}
