using System.Buffers.Binary;

namespace SmartFan.Core.Protocol;

public readonly struct RgbColor : IEquatable<RgbColor>
{
    public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public bool Equals(RgbColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is RgbColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(R, G, B);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>UI 可编辑的灯效参数；字段编码由 PacketCodec 统一完成，不直接序列化 CLR 内存。</summary>
public sealed class LightingConfiguration
{
    public LightMode Mode { get; set; } = LightMode.Static;
    public RgbColor MainColor { get; set; } = new(90, 110, 255);
    public RgbColor SecondaryColor { get; set; } = new(0, 0, 0);
    public ushort SwitchMilliseconds { get; set; } = 1000;
    public bool RandomContinuous { get; set; } = true;
    public List<RgbColor> Colors { get; set; } = new() { new(255, 0, 0), new(0, 0, 255) };
    public List<ushort> FadeMilliseconds { get; set; } = new() { 1000, 1000 };
    public float Range { get; set; } = 5;
    public float InnerRange { get; set; } = 1;
    public int WaitMilliseconds { get; set; } = 2000;
    public float CenterSpeed { get; set; } = 1;
}

public static class PacketCodec
{
    public static IReadOnlyList<byte> AllowedUpdatePeriods { get; } = Array.AsReadOnly(new byte[] { 10, 20, 50, 100, 200 });
    public const int MaximumColorCount = 9;

    public static byte[] EncodeByte(byte value) => new[] { value };
    public static byte[] EncodeInt32(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    public static byte[] EncodeUInt32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes; }
    public static byte[] EncodeDouble(double value) { var bytes = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(bytes, value); return bytes; }
    public static int DecodeInt32(ReadOnlySpan<byte> payload) { RequireLength(payload, 4); return BinaryPrimitives.ReadInt32LittleEndian(payload); }
    public static uint DecodeUInt32(ReadOnlySpan<byte> payload) { RequireLength(payload, 4); return BinaryPrimitives.ReadUInt32LittleEndian(payload); }
    public static double DecodeDouble(ReadOnlySpan<byte> payload) { RequireLength(payload, 8); return BinaryPrimitives.ReadDoubleLittleEndian(payload); }

    public static byte[] EncodeFanArray(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 5) throw new ArgumentException("内置风扇数组必须包含 5 个 double；未安装的位置填 0。", nameof(values));
        var bytes = new byte[40];
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        return bytes;
    }

    public static double[] DecodeFanArray(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 40);
        var values = new double[5];
        for (var i = 0; i < 5; i++) values[i] = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(i * 8, 8));
        return values;
    }

    public static CharacteristicKey ConfigurationKey(LightMode mode) => mode switch
    {
        LightMode.Static => CharacteristicKey.StaticConfig,
        LightMode.Rainbow => CharacteristicKey.RainbowConfig,
        LightMode.Breath => CharacteristicKey.BreathConfig,
        LightMode.RandomFlash => CharacteristicKey.RandomFlashConfig,
        LightMode.Blink => CharacteristicKey.BlinkConfig,
        LightMode.CustomBreath => CharacteristicKey.CustomBreathConfig,
        LightMode.Chase => CharacteristicKey.ChaseConfig,
        LightMode.Round => CharacteristicKey.RoundConfig,
        _ => throw new ArgumentException("关灯模式没有独立配置特征。", nameof(mode))
    };

    public static byte[] EncodeLighting(LightingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = ConfigurationKey(configuration.Mode);
        byte[] payload;
        switch (configuration.Mode)
        {
            case LightMode.Static:
                payload = new byte[3]; WriteColor(payload, configuration.MainColor); break;
            case LightMode.Rainbow:
                payload = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(payload, configuration.SwitchMilliseconds); break;
            case LightMode.Breath:
                payload = new byte[5]; BinaryPrimitives.WriteUInt16LittleEndian(payload, configuration.SwitchMilliseconds);
                WriteColor(payload.AsSpan(2), configuration.MainColor); break;
            case LightMode.RandomFlash:
                payload = new[] { configuration.RandomContinuous ? (byte)1 : (byte)0 }; break;
            case LightMode.Blink:
                if (configuration.Colors.Count != 2) throw new ArgumentException("双色闪烁必须恰好有 2 个颜色。");
                payload = new byte[9]; payload[0] = 2;
                WriteColor(payload.AsSpan(1), configuration.Colors[0]); WriteColor(payload.AsSpan(4), configuration.Colors[1]);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(7), configuration.SwitchMilliseconds); break;
            case LightMode.CustomBreath:
                RequireColorCount(configuration.Colors.Count);
                if (configuration.FadeMilliseconds.Count != configuration.Colors.Count) throw new ArgumentException("每个自定义呼吸颜色必须有一个渐变时长。");
                payload = new byte[1 + 5 * configuration.Colors.Count]; payload[0] = (byte)configuration.Colors.Count;
                // 固件布局是“全部 RGB，再全部 u16 时长”，并非 RGB/时长交替。
                for (var i = 0; i < configuration.Colors.Count; i++)
                {
                    WriteColor(payload.AsSpan(1 + i * 3), configuration.Colors[i]);
                    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1 + 3 * configuration.Colors.Count + 2 * i), configuration.FadeMilliseconds[i]);
                }
                break;
            case LightMode.Chase:
                RequireColorCount(configuration.Colors.Count);
                payload = new byte[1 + 3 * configuration.Colors.Count]; payload[0] = (byte)configuration.Colors.Count;
                for (var i = 0; i < configuration.Colors.Count; i++) WriteColor(payload.AsSpan(1 + i * 3), configuration.Colors[i]);
                break;
            case LightMode.Round:
                // 明确写字段偏移，避免 C# / C 结构体对齐产生 24 字节，而协议要求 22 字节。
                payload = new byte[22]; WriteColor(payload, configuration.MainColor); WriteColor(payload.AsSpan(3), configuration.SecondaryColor);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6), configuration.Range);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(10), configuration.InnerRange);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(14), configuration.WaitMilliseconds);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18), configuration.CenterSpeed);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(configuration));
        }
        Validate(key, payload);
        return payload;
    }

    public static LightingConfiguration DecodeLighting(CharacteristicKey key, ReadOnlySpan<byte> payload)
    {
        Validate(key, payload);
        var result = new LightingConfiguration();
        switch (key)
        {
            case CharacteristicKey.StaticConfig: result.Mode = LightMode.Static; result.MainColor = ReadColor(payload); break;
            case CharacteristicKey.RainbowConfig: result.Mode = LightMode.Rainbow; result.SwitchMilliseconds = BinaryPrimitives.ReadUInt16LittleEndian(payload); break;
            case CharacteristicKey.BreathConfig:
                result.Mode = LightMode.Breath; result.SwitchMilliseconds = BinaryPrimitives.ReadUInt16LittleEndian(payload); result.MainColor = ReadColor(payload.Slice(2)); break;
            case CharacteristicKey.RandomFlashConfig: result.Mode = LightMode.RandomFlash; result.RandomContinuous = payload[0] == 1; break;
            case CharacteristicKey.BlinkConfig:
                result.Mode = LightMode.Blink; result.Colors = new() { ReadColor(payload.Slice(1)), ReadColor(payload.Slice(4)) };
                result.SwitchMilliseconds = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(7)); break;
            case CharacteristicKey.CustomBreathConfig:
            case CharacteristicKey.ChaseConfig:
                result.Mode = key == CharacteristicKey.ChaseConfig ? LightMode.Chase : LightMode.CustomBreath;
                result.Colors.Clear(); result.FadeMilliseconds.Clear();
                for (var i = 0; i < payload[0]; i++)
                {
                    result.Colors.Add(ReadColor(payload.Slice(1 + i * 3)));
                    if (key == CharacteristicKey.CustomBreathConfig) result.FadeMilliseconds.Add(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1 + 3 * payload[0] + 2 * i)));
                }
                break;
            case CharacteristicKey.RoundConfig:
                result.Mode = LightMode.Round; result.MainColor = ReadColor(payload); result.SecondaryColor = ReadColor(payload.Slice(3));
                result.Range = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(6)); result.InnerRange = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(10));
                result.WaitMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(14)); result.CenterSpeed = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(18)); break;
            default: throw new ArgumentException("该特征不是灯效配置。", nameof(key));
        }
        return result;
    }

    public static void Validate(CharacteristicKey key, ReadOnlySpan<byte> payload)
    {
        var definition = ProtocolCatalog.Get(key);
        if (definition.FixedLength.HasValue) RequireLength(payload, definition.FixedLength.Value);
        switch (key)
        {
            case CharacteristicKey.FanMode:
                if (DecodeInt32(payload) is < 0 or > 2) throw Invalid("风扇模式必须为 0、1 或 2。"); break;
            case CharacteristicKey.FanPwm:
                if (payload[0] > 100) throw Invalid("PWM 必须在 0..100%。"); break;
            case CharacteristicKey.LightMode:
                if (DecodeInt32(payload) is < 0 or > 8) throw Invalid("灯效模式必须在 0..8。"); break;
            case CharacteristicKey.LightUpdatePeriod:
                if (!AllowedUpdatePeriods.Contains(payload[0])) throw Invalid("刷新周期只能选择 10、20、50、100、200 ms。"); break;
            case CharacteristicKey.RainbowConfig:
            case CharacteristicKey.BreathConfig:
                RequirePositiveDuration(BinaryPrimitives.ReadUInt16LittleEndian(payload)); break;
            case CharacteristicKey.RandomFlashConfig:
                if (payload[0] > 1) throw Invalid("随机闪烁模式必须为 0 或 1。"); break;
            case CharacteristicKey.BlinkConfig:
                if (payload[0] != 2) throw Invalid("双色闪烁的颜色数量必须为 2。");
                RequirePositiveDuration(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(7))); break;
            case CharacteristicKey.CustomBreathConfig:
            case CharacteristicKey.ChaseConfig:
                if (payload.Length < 1) throw Invalid("颜色数组缺少数量字段。");
                RequireColorCount(payload[0]);
                RequireLength(payload, 1 + payload[0] * (key == CharacteristicKey.ChaseConfig ? 3 : 5));
                if (key == CharacteristicKey.CustomBreathConfig)
                    for (var i = 0; i < payload[0]; i++) RequirePositiveDuration(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1 + 3 * payload[0] + 2 * i)));
                break;
            case CharacteristicKey.RoundConfig:
                var range = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(6));
                var inner = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(10));
                var wait = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(14));
                var speed = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(18));
                if (!float.IsFinite(range) || !float.IsFinite(inner) || !float.IsFinite(speed) || range <= 0 || inner < 0 || inner > range || wait < 0 || speed < 0)
                    throw Invalid("环绕扫描要求有限数值、范围 > 0、0 ≤ 主色范围 ≤ 范围、等待和速度 ≥ 0。");
                break;
            case CharacteristicKey.LaptopFanUsed:
            case CharacteristicKey.LaptopFanSpeed:
                foreach (var value in DecodeFanArray(payload))
                {
                    if (!double.IsFinite(value) || value < 0) throw Invalid("内置风扇数据必须是有限非负数值。");
                    if (key == CharacteristicKey.LaptopFanUsed && value != 0 && value != 1) throw Invalid("fan_used 是已安装风扇槽位标志，只能写 0 或 1。");
                }
                break;
            default:
                if (definition.ServiceUuid == BleUuids.LaptopService)
                {
                    var value = DecodeDouble(payload);
                    if (!double.IsFinite(value)) throw Invalid("电脑监控数据必须是有限数值；不可用指标应停止上报。");
                    if ((key == CharacteristicKey.LaptopCpuUsage || key == CharacteristicKey.LaptopGpuUsage) && (value < 0 || value > 100)) throw Invalid("占用率必须在 0..100%。");
                    if (key != CharacteristicKey.LaptopCpuTemperature && key != CharacteristicKey.LaptopGpuTemperature && value < 0) throw Invalid("此项监控数据不能是负数。");
                }
                break;
        }
    }

    private static void WriteColor(Span<byte> destination, RgbColor color) { destination[0] = color.R; destination[1] = color.G; destination[2] = color.B; }
    private static RgbColor ReadColor(ReadOnlySpan<byte> source) => new(source[0], source[1], source[2]);
    private static void RequireLength(ReadOnlySpan<byte> payload, int expected) { if (payload.Length != expected) throw Invalid($"载荷必须恰好为 {expected} 字节，实际 {payload.Length} 字节。"); }
    private static void RequirePositiveDuration(ushort duration) { if (duration == 0) throw Invalid("渐变 / 闪烁时长必须在 1..65535 ms。"); }
    private static void RequireColorCount(int count) { if (count < 1 || count > MaximumColorCount) throw Invalid("颜色数量必须在 1..9。"); }
    private static ArgumentException Invalid(string message) => new(message, "payload");
}
