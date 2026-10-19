using System.Globalization;

namespace SmartFan.Core.Protocol;

public sealed class DiagnosticPayloadField
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Type { get; init; } = "";
    public string Bytes { get; init; } = "";
}

public sealed class DiagnosticPayloadDescription
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<DiagnosticPayloadField> Fields { get; init; } = Array.Empty<DiagnosticPayloadField>();
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 将已有协议解码结果转换为自检页可展示的字段。长度、合法值和字节序统一由
/// PacketCodec 决定；此处只补中文名称、单位和字段偏移，不建立第二套收包规则。
/// </summary>
public static class DiagnosticPayloadDecoder
{
    private const string UInt8Type = "uint8";
    private const string UInt16Type = "uint16 · 小端";
    private const string Int32Type = "int32 · 小端";
    private const string UInt32Type = "uint32 · 小端";
    private const string Float32Type = "float32 · 小端";
    private const string DoubleType = "double64 · 小端";

    public static DiagnosticPayloadDescription Decode(CharacteristicKey key, ReadOnlySpan<byte> payload)
    {
        try
        {
            PacketCodec.Validate(key, payload);
            var fields = new List<DiagnosticPayloadField>();
            string summary;
            switch (key)
            {
                case CharacteristicKey.FanMode:
                    var fanMode = PacketCodec.DecodeInt32(payload);
                    var fanModeName = FanModeName((FanMode)fanMode);
                    Add(fields, "风扇模式", $"{fanMode} · {fanModeName}", Int32Type, 0, 4);
                    summary = $"风扇模式：{fanModeName}";
                    break;
                case CharacteristicKey.FanPwm:
                    Add(fields, "PWM 百分比", $"{payload[0]}%", UInt8Type, 0, 1);
                    summary = $"风扇 PWM：{payload[0]}%（当前固件读回值不代表实际输出）";
                    break;
                case CharacteristicKey.FanRpm:
                    var rpm = PacketCodec.DecodeUInt32(payload);
                    Add(fields, "转速值（通知为设备反馈，写入为目标）", $"{rpm} RPM", UInt32Type, 0, 4);
                    summary = $"{rpm} RPM（通知=设备反馈，写入=目标）";
                    break;
                case CharacteristicKey.LightMode:
                    var lightMode = PacketCodec.DecodeInt32(payload);
                    var lightModeName = LightModeName((LightMode)lightMode);
                    Add(fields, "灯效模式", $"{lightMode} · {lightModeName}", Int32Type, 0, 4);
                    summary = $"灯效模式：{lightModeName}";
                    break;
                case CharacteristicKey.LightUpdatePeriod:
                    var milliseconds = payload[0];
                    var frequency = Format(1000d / milliseconds);
                    Add(fields, "刷新周期", $"{milliseconds} ms（{frequency} Hz）", UInt8Type, 0, 1);
                    summary = $"刷新周期 {milliseconds} ms · {frequency} Hz";
                    break;
                case CharacteristicKey.LightBrightness:
                    var percentage = (payload[0] * 100d / 255).ToString("0.#", CultureInfo.InvariantCulture);
                    Add(fields, "亮度", $"{payload[0]} / 255（{percentage}%）", UInt8Type, 0, 1);
                    summary = $"亮度 {payload[0]} / 255 · {percentage}%";
                    break;
                case CharacteristicKey.StaticConfig:
                case CharacteristicKey.RainbowConfig:
                case CharacteristicKey.BreathConfig:
                case CharacteristicKey.RandomFlashConfig:
                case CharacteristicKey.BlinkConfig:
                case CharacteristicKey.CustomBreathConfig:
                case CharacteristicKey.ChaseConfig:
                case CharacteristicKey.RoundConfig:
                    summary = DescribeLighting(PacketCodec.DecodeLighting(key, payload), fields);
                    break;
                case CharacteristicKey.LaptopFanUsed:
                    var installed = PacketCodec.DecodeFanArray(payload);
                    var count = 0;
                    for (var i = 0; i < installed.Length; i++)
                    {
                        if (installed[i] == 1) count++;
                        Add(fields, $"内置风扇 [{i}] 安装标记", installed[i] == 1 ? "1 · 已安装" : "0 · 未安装", DoubleType, i * 8, 8);
                    }
                    summary = $"笔记本内置风扇：{count} 个已安装槽位（共 5 个槽位）";
                    break;
                case CharacteristicKey.LaptopFanSpeed:
                    var speeds = PacketCodec.DecodeFanArray(payload);
                    for (var i = 0; i < speeds.Length; i++)
                        Add(fields, $"内置风扇 [{i}] 转速", $"{Format(speeds[i])} RPM", DoubleType, i * 8, 8);
                    summary = "笔记本内置风扇转速：" + string.Join(" / ", speeds.Select(Format)) + " RPM";
                    break;
                default:
                    var (name, unit) = TelemetryNameAndUnit(key);
                    var value = PacketCodec.DecodeDouble(payload);
                    var displayed = unit.Length == 0 ? $"{Format(value)}（协议单位待确认）" : $"{Format(value)} {unit}";
                    Add(fields, name, displayed, DoubleType, 0, 8);
                    summary = $"{name}：{displayed}";
                    break;
            }
            return new DiagnosticPayloadDescription { IsValid = true, Summary = summary, Fields = fields.AsReadOnly() };
        }
        catch (ArgumentException ex)
        {
            var error = Enum.IsDefined(key) ? ex.Message : $"不支持的特征编号：{(int)key}。";
            return new DiagnosticPayloadDescription { IsValid = false, Summary = "载荷无效", Error = error };
        }
    }

    private static string DescribeLighting(LightingConfiguration configuration, List<DiagnosticPayloadField> fields)
    {
        switch (configuration.Mode)
        {
            case LightMode.Static:
                AddColor(fields, "颜色", configuration.MainColor, 0);
                return $"单色常亮 · {configuration.MainColor}";
            case LightMode.Rainbow:
                Add(fields, "单次颜色过渡时长", $"{configuration.SwitchMilliseconds} ms", UInt16Type, 0, 2);
                return $"彩虹渐变 · 单段 {configuration.SwitchMilliseconds} ms";
            case LightMode.Breath:
                Add(fields, "单程渐变时长", $"{configuration.SwitchMilliseconds} ms", UInt16Type, 0, 2);
                AddColor(fields, "呼吸颜色", configuration.MainColor, 2);
                return $"单色呼吸 · {configuration.MainColor} · 单程 {configuration.SwitchMilliseconds} ms";
            case LightMode.RandomFlash:
                var randomMode = configuration.RandomContinuous ? "1 · 每帧生成随机颜色（连续）" : "0 · 随机颜色与熄灭交替";
                Add(fields, "随机闪烁方式", randomMode, UInt8Type, 0, 1);
                return "随机闪烁 · " + (configuration.RandomContinuous ? "连续随机" : "随机与熄灭交替");
            case LightMode.Blink:
                Add(fields, "颜色数量", "2", UInt8Type, 0, 1);
                AddColor(fields, "颜色 [0]", configuration.Colors[0], 1);
                AddColor(fields, "颜色 [1]", configuration.Colors[1], 4);
                Add(fields, "颜色切换间隔", $"{configuration.SwitchMilliseconds} ms", UInt16Type, 7, 2);
                return $"双色闪烁 · 每 {configuration.SwitchMilliseconds} ms 切换颜色";
            case LightMode.CustomBreath:
            case LightMode.Chase:
                var count = configuration.Colors.Count;
                Add(fields, "颜色数量", count.ToString(CultureInfo.InvariantCulture), UInt8Type, 0, 1);
                for (var i = 0; i < count; i++) AddColor(fields, $"颜色 [{i}]", configuration.Colors[i], 1 + 3 * i);
                if (configuration.Mode == LightMode.CustomBreath)
                {
                    // 偏移说明与线上顺序相同：全部颜色之后，才是全部 u16 时长。
                    for (var i = 0; i < count; i++)
                        Add(fields, $"渐变时长 [{i}]（颜色 {i} → {(i + 1) % count}）", $"{configuration.FadeMilliseconds[i]} ms", UInt16Type, 1 + 3 * count + 2 * i, 2);
                    return $"自定义多色呼吸 · {count} 个颜色";
                }
                return $"流光 · {count} 个颜色";
            case LightMode.Round:
                AddColor(fields, "color_main（主色）", configuration.MainColor, 0);
                AddColor(fields, "color_sub（副色）", configuration.SecondaryColor, 3);
                Add(fields, "range（外范围）", $"{Format(configuration.Range)} 灯距", Float32Type, 6, 4);
                Add(fields, "range_main（内范围）", $"{Format(configuration.InnerRange)} 灯距", Float32Type, 10, 4);
                Add(fields, "wait_ms（等待时长）", $"{configuration.WaitMilliseconds} ms", Int32Type, 14, 4);
                Add(fields, "center_speed（中心速度）", $"{Format(configuration.CenterSpeed)} 灯距/s", Float32Type, 18, 4);
                return $"环绕扫描 · 范围 {Format(configuration.Range)} / {Format(configuration.InnerRange)} 灯距 · 等待 {configuration.WaitMilliseconds} ms · {Format(configuration.CenterSpeed)} 灯距/s";
            default: throw new ArgumentOutOfRangeException(nameof(configuration), "该模式没有可解析的灯效配置。");
        }
    }

    private static string FanModeName(FanMode mode) => mode switch
    {
        FanMode.Manual => "手动（MANUAL）",
        FanMode.Automatic => "自动（AUTO）",
        FanMode.FullSpeed => "全速（FULL_SPEED）",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static string LightModeName(LightMode mode) => mode switch
    {
        LightMode.Off => "关灯（OFF）",
        LightMode.Static => "单色常亮（STATIC）",
        LightMode.Rainbow => "彩虹渐变（RAINBOW）",
        LightMode.Breath => "单色呼吸（BREATH）",
        LightMode.RandomFlash => "随机闪烁（RANDOM_FLASH）",
        LightMode.Blink => "双色闪烁（BLINK）",
        LightMode.CustomBreath => "自定义多色呼吸（CUSTOM_BREATH）",
        LightMode.Chase => "流光（CHASE）",
        LightMode.Round => "环绕扫描（ROUND）",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static (string Name, string Unit) TelemetryNameAndUnit(CharacteristicKey key) => key switch
    {
        CharacteristicKey.LaptopCpuUsage => ("CPU 占用率", "%"),
        CharacteristicKey.LaptopCpuTemperature => ("CPU 温度", "°C"),
        CharacteristicKey.LaptopCpuVoltage => ("CPU 电压", "V"),
        CharacteristicKey.LaptopCpuClock => ("CPU 频率", "MHz"),
        CharacteristicKey.LaptopGpuUsage => ("GPU 占用率", "%"),
        CharacteristicKey.LaptopGpuTemperature => ("GPU 温度", "°C"),
        CharacteristicKey.LaptopGpuClock => ("GPU 频率", "MHz"),
        CharacteristicKey.LaptopGpuMemoryUsed => ("显存已用", ""),
        CharacteristicKey.LaptopGpuMemoryTotal => ("显存总量", ""),
        CharacteristicKey.LaptopMemoryUsed => ("内存已用", ""),
        CharacteristicKey.LaptopMemoryTotal => ("内存总量", ""),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static string Format(float value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static void AddColor(List<DiagnosticPayloadField> fields, string name, RgbColor color, int offset)
        => Add(fields, name, $"{color}（R={color.R}，G={color.G}，B={color.B}）", "RGB · 3 × uint8", offset, 3);
    private static void Add(List<DiagnosticPayloadField> fields, string name, string value, string type, int offset, int length)
        => fields.Add(new DiagnosticPayloadField { Name = name, Value = value, Type = type, Bytes = length == 1 ? offset.ToString(CultureInfo.InvariantCulture) : $"{offset}..{offset + length - 1}" });
}
