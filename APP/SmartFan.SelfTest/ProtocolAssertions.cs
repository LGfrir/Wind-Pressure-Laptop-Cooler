using System.Buffers.Binary;
using System.Globalization;
using SmartFan.Core.Protocol;

namespace SmartFan.SelfTest;

internal static class ProtocolAssertions
{
    private static int _assertions;
    public static int Run()
    {
        _assertions = 0;
        Check(ProtocolCatalog.All.Count == 27, "27 个特征");
        Check(ProtocolCatalog.All.Select(item => item.Uuid).Distinct().Count() == 27, "UUID 唯一");
        Check(BleUuids.Services.Count == 4, "4 个服务");
        Check(ProtocolCatalog.All.Count(item => item.ServiceUuid == BleUuids.LaptopService) == 13, "13 个电脑状态特征");
        Check(!ProtocolCatalog.Get(CharacteristicKey.FanRpm).CanRead, "RPM 没有 READ");
        Check(ProtocolCatalog.Get(CharacteristicKey.FanRpm).CanNotify, "RPM 可通知");
        Check(PacketCodec.EncodeInt32(2).SequenceEqual(new byte[] { 2, 0, 0, 0 }), "i32 小端");
        Check(PacketCodec.EncodeUInt32(0x12345678).SequenceEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }), "u32 小端");
        Check(PacketCodec.DecodeUInt32(new byte[] { 0x60, 0x09, 0x00, 0x00 }) == 2400, "底座 2400 RPM 通知保持原值，没有百分比或每秒换算");
        Check(PacketCodec.DecodeUInt32(new byte[] { 0x27, 0x00, 0x00, 0x00 }) == 39, "底座低转速通知原值保留");
        Check(PacketCodec.DecodeUInt32(new byte[] { 0x00, 0x00, 0x01, 0x00 }) == 65536, "RPM 读取完整四字节，不截断为 uint16");
        Accept(CharacteristicKey.FanRpm, new byte[] { 0x60, 0x09, 0x00, 0x00 });
        Reject(CharacteristicKey.FanRpm, new byte[] { 0x60, 0x09 });
        Reject(CharacteristicKey.FanRpm, new byte[] { 0x60, 0x09, 0x00, 0x00, 0x00 });
        Check(PacketCodec.DecodeDouble(PacketCodec.EncodeDouble(65.5)) == 65.5, "double 往返");

        for (var value = 0; value <= 255; value++)
        {
            var bytes = new[] { (byte)value };
            if (PacketCodec.AllowedUpdatePeriods.Contains((byte)value)) Accept(CharacteristicKey.LightUpdatePeriod, bytes); else Reject(CharacteristicKey.LightUpdatePeriod, bytes);
            if (value <= 100) Accept(CharacteristicKey.FanPwm, bytes); else Reject(CharacteristicKey.FanPwm, bytes);
            Accept(CharacteristicKey.LightBrightness, bytes);
            if (value <= 1) Accept(CharacteristicKey.RandomFlashConfig, bytes); else Reject(CharacteristicKey.RandomFlashConfig, bytes);
        }
        for (var value = -2; value <= 10; value++)
        {
            if (value is >= 0 and <= 2) Accept(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(value)); else Reject(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(value));
            if (value is >= 0 and <= 8) Accept(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(value)); else Reject(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(value));
        }
        foreach (var key in new[] { CharacteristicKey.RainbowConfig, CharacteristicKey.BreathConfig })
        {
            var size = ProtocolCatalog.Get(key).FixedLength ?? 0;
            Reject(key, new byte[size]);
            var payload = new byte[size]; BinaryPrimitives.WriteUInt16LittleEndian(payload, ushort.MaxValue); Accept(key, payload);
        }

        foreach (LightMode mode in Enum.GetValues<LightMode>())
        {
            if (mode == LightMode.Off) continue;
            var configuration = new LightingConfiguration { Mode = mode, MainColor = new(1, 2, 3), SecondaryColor = new(4, 5, 6), SwitchMilliseconds = 54321, Range = 4.5f, InnerRange = 2.5f, CenterSpeed = 1.5f, WaitMilliseconds = 2345 };
            var payload = PacketCodec.EncodeLighting(configuration);
            var key = PacketCodec.ConfigurationKey(mode);
            var decoded = PacketCodec.DecodeLighting(key, payload);
            Check(PacketCodec.EncodeLighting(decoded).SequenceEqual(payload), $"{mode} 编解码往返");
            Reject(key, payload.Concat(new byte[] { 0 }).ToArray());
            Reject(key, payload.Take(payload.Length - 1).ToArray());
        }
        var breath = new LightingConfiguration { Mode = LightMode.Breath, SwitchMilliseconds = 1000, MainColor = new(255, 0, 0) };
        Check(PacketCodec.EncodeLighting(breath).SequenceEqual(new byte[] { 0xe8, 3, 255, 0, 0 }), "呼吸 5 字节布局");
        var blink = new LightingConfiguration { Mode = LightMode.Blink, SwitchMilliseconds = 2000, Colors = new() { new(255, 0, 0), new(0, 0, 255) } };
        Check(Convert.ToHexString(PacketCodec.EncodeLighting(blink)) == "02FF00000000FFD007", "双色闪烁精确布局");
        for (var count = 1; count <= 9; count++)
        {
            var configuration = new LightingConfiguration { Mode = LightMode.CustomBreath, Colors = new(), FadeMilliseconds = new() };
            for (var i = 0; i < count; i++) { configuration.Colors.Add(new((byte)(i + 1), (byte)(i + 20), (byte)(i + 40))); configuration.FadeMilliseconds.Add((ushort)(1000 + i)); }
            var payload = PacketCodec.EncodeLighting(configuration);
            Check(payload.Length == 1 + 5 * count, $"自定义呼吸 N={count} 长度");
            Check(payload[1] == 1 && payload[2] == 20 && payload[3] == 40, "RGB 颜色顺序");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1 + 3 * count)) == 1000, "时长在全部颜色之后");
            Check(PacketCodec.EncodeLighting(PacketCodec.DecodeLighting(CharacteristicKey.CustomBreathConfig, payload)).SequenceEqual(payload), "多色呼吸往返");
            payload[1 + 3 * count] = 0; payload[2 + 3 * count] = 0; Reject(CharacteristicKey.CustomBreathConfig, payload);
            configuration.Mode = LightMode.Chase;
            Check(PacketCodec.EncodeLighting(configuration).Length == 1 + 3 * count, "流光长度");
        }
        Reject(CharacteristicKey.CustomBreathConfig, new byte[] { 0 });
        Reject(CharacteristicKey.CustomBreathConfig, new byte[51] { 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Reject(CharacteristicKey.ChaseConfig, new byte[] { 0 });
        var round = new LightingConfiguration { Mode = LightMode.Round };
        var roundPayload = PacketCodec.EncodeLighting(round);
        Check(roundPayload.Length == 22, "环绕精确 22 字节");
        Check(BinaryPrimitives.ReadSingleLittleEndian(roundPayload.AsSpan(6)) == 5, "range 偏移 6");
        Check(BinaryPrimitives.ReadSingleLittleEndian(roundPayload.AsSpan(10)) == 1, "range_main 偏移 10");
        Check(BinaryPrimitives.ReadInt32LittleEndian(roundPayload.AsSpan(14)) == 2000, "wait 偏移 14");
        Check(BinaryPrimitives.ReadSingleLittleEndian(roundPayload.AsSpan(18)) == 1, "center_speed 偏移 18");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            foreach (var offset in new[] { 6, 10, 18 })
            {
                var payload = roundPayload.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), invalid); Reject(CharacteristicKey.RoundConfig, payload);
            }
        }
        var zeroRange = roundPayload.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(zeroRange.AsSpan(6), 0); Reject(CharacteristicKey.RoundConfig, zeroRange);
        var badInner = roundPayload.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(badInner.AsSpan(10), 6); Reject(CharacteristicKey.RoundConfig, badInner);
        var badWait = roundPayload.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(badWait.AsSpan(14), -1); Reject(CharacteristicKey.RoundConfig, badWait);
        var fanUsed = PacketCodec.EncodeFanArray(new double[] { 1, 1, 1, 0, 0 }); Accept(CharacteristicKey.LaptopFanUsed, fanUsed);
        Check(PacketCodec.DecodeFanArray(fanUsed).SequenceEqual(new double[] { 1, 1, 1, 0, 0 }), "三风扇存在标志往返");
        Reject(CharacteristicKey.LaptopFanUsed, PacketCodec.EncodeFanArray(new double[] { 1, 2, 0, 0, 0 }));
        foreach (var definition in ProtocolCatalog.All.Where(item => item.ServiceUuid == BleUuids.LaptopService))
        {
            var payload = definition.FixedLength == 40 ? new byte[40] : PacketCodec.EncodeDouble(65.5);
            Accept(definition.Key, payload);
            Reject(definition.Key, payload.Take(payload.Length - 1).ToArray());
            Reject(definition.Key, payload.Concat(new byte[] { 0 }).ToArray());
            if (definition.FixedLength == 8)
            {
                Reject(definition.Key, PacketCodec.EncodeDouble(double.NaN));
                Reject(definition.Key, PacketCodec.EncodeDouble(double.PositiveInfinity));
            }
        }
        CheckDiagnosticDescriptions();
        return _assertions;
    }

    private static void CheckDiagnosticDescriptions()
    {
        var samples = new Dictionary<CharacteristicKey, byte[]>
        {
            [CharacteristicKey.FanMode] = PacketCodec.EncodeInt32(1),
            [CharacteristicKey.FanPwm] = new byte[] { 75 },
            [CharacteristicKey.FanRpm] = PacketCodec.EncodeUInt32(2400),
            [CharacteristicKey.LightMode] = PacketCodec.EncodeInt32(6),
            [CharacteristicKey.LightUpdatePeriod] = new byte[] { 20 },
            [CharacteristicKey.LightBrightness] = new byte[] { 128 },
            [CharacteristicKey.LaptopFanUsed] = PacketCodec.EncodeFanArray(new double[] { 1, 0, 1, 0, 1 }),
            [CharacteristicKey.LaptopFanSpeed] = PacketCodec.EncodeFanArray(new double[] { 0, 1250.5, 2500, 3000, 12345.75 })
        };
        foreach (var mode in Enum.GetValues<LightMode>())
            if (mode != LightMode.Off) samples[PacketCodec.ConfigurationKey(mode)] = PacketCodec.EncodeLighting(new LightingConfiguration { Mode = mode });
        foreach (var definition in ProtocolCatalog.All.Where(item => item.ServiceUuid == BleUuids.LaptopService && item.FixedLength == 8))
            samples[definition.Key] = PacketCodec.EncodeDouble(65.5);

        Check(samples.Count == 27, "自检解码样本覆盖全部 27 个特征");
        foreach (var definition in ProtocolCatalog.All)
        {
            var key = definition.Key;
            var payload = samples[key];
            var description = DiagnosticPayloadDecoder.Decode(key, payload);
            Check(description.IsValid && description.Error is null, $"{key} 可解释合法载荷");
            Check(description.Summary.Length > 0 && description.Fields.Count > 0, $"{key} 提供摘要和字段");
            Check(description.Fields.All(field => field.Name.Length > 0 && field.Value.Length > 0 && field.Type.Length > 0 && field.Bytes.Length > 0), $"{key} 字段信息完整");
            CheckInvalidDescription(key, payload.Take(payload.Length - 1).ToArray(), "短一字节");
            CheckInvalidDescription(key, payload.Concat(new byte[] { 0 }).ToArray(), "长一字节");
            CheckInvalidDescription(key, Array.Empty<byte>(), "空载荷");
        }

        for (var mode = 0; mode <= 2; mode++)
        {
            var decoded = DiagnosticPayloadDecoder.Decode(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(mode));
            var expected = new[] { "手动", "自动", "全速" }[mode];
            Check(decoded.Summary.Contains(expected, StringComparison.Ordinal) && decoded.Fields[0].Bytes == "0..3", "风扇枚举中文名和 i32 偏移");
        }
        for (var mode = 0; mode <= 8; mode++)
        {
            var decoded = DiagnosticPayloadDecoder.Decode(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(mode));
            Check(decoded.IsValid && decoded.Fields[0].Value.StartsWith($"{mode} · ", StringComparison.Ordinal), "九种灯光枚举带原始数值");
        }
        CheckInvalidDescription(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(-1), "负风扇枚举");
        CheckInvalidDescription(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(3), "越界风扇枚举");
        CheckInvalidDescription(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(9), "越界灯光枚举");
        CheckInvalidDescription((CharacteristicKey)999, new byte[4], "未知特征不抛到 UI");
        CheckInvalidDescription(CharacteristicKey.LightUpdatePeriod, new byte[] { 33 }, "不合法周期");
        CheckInvalidDescription(CharacteristicKey.RandomFlashConfig, new byte[] { 2 }, "不合法随机模式");
        CheckInvalidDescription(CharacteristicKey.RainbowConfig, new byte[] { 0, 0 }, "零过渡时长");

        var pwm = DiagnosticPayloadDecoder.Decode(CharacteristicKey.FanPwm, new byte[] { 75 });
        Check(pwm.Fields[0].Value == "75%" && pwm.Summary.Contains("不代表实际输出", StringComparison.Ordinal), "PWM 解码保留已知固件读回限制");
        var rpm = DiagnosticPayloadDecoder.Decode(CharacteristicKey.FanRpm, new byte[] { 0x60, 0x09, 0, 0 });
        Check(rpm.Fields[0].Value == "2400 RPM" && rpm.Summary.Contains("通知=设备反馈，写入=目标", StringComparison.Ordinal), "RPM 小端值与读写语义，不把测试反馈称为实测");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.FanRpm, PacketCodec.EncodeUInt32(uint.MaxValue)).Fields[0].Value == "4294967295 RPM", "RPM 不误解为有符号 int32");
        foreach (var period in PacketCodec.AllowedUpdatePeriods)
        {
            var description = DiagnosticPayloadDecoder.Decode(CharacteristicKey.LightUpdatePeriod, new[] { period });
            Check(description.Fields[0].Value == $"{period} ms（{1000 / period} Hz）", "每个合法周期都有正确 Hz");
        }
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LightBrightness, new byte[] { 0 }).Fields[0].Value == "0 / 255（0%）", "亮度零点");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LightBrightness, new byte[] { 128 }).Fields[0].Value == "128 / 255（50.2%）", "亮度百分比按 255 缩放");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LightBrightness, new byte[] { 255 }).Fields[0].Value == "255 / 255（100%）", "亮度满量程");

        var staticDescription = DiagnosticPayloadDecoder.Decode(CharacteristicKey.StaticConfig, new byte[] { 1, 2, 3 });
        Check(staticDescription.Fields[0].Value == "#010203（R=1，G=2，B=3）" && staticDescription.Fields[0].Bytes == "0..2", "颜色按 RGB 解释并给出偏移");
        var rainbow = DiagnosticPayloadDecoder.Decode(CharacteristicKey.RainbowConfig, new byte[] { 0x34, 0x12 });
        Check(rainbow.Fields[0].Value == "4660 ms" && rainbow.Fields[0].Bytes == "0..1", "uint16 过渡时长显式小端");
        var breath = DiagnosticPayloadDecoder.Decode(CharacteristicKey.BreathConfig, new byte[] { 0xe8, 3, 4, 5, 6 });
        Check(breath.Fields[0].Value == "1000 ms" && breath.Fields[0].Name.Contains("单程", StringComparison.Ordinal), "呼吸时间含义是单程");
        Check(breath.Fields[1].Bytes == "2..4" && breath.Fields[1].Value.StartsWith("#040506", StringComparison.Ordinal), "呼吸颜色位于 2..4");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.RandomFlashConfig, new byte[] { 0 }).Fields[0].Value.Contains("熄灭交替", StringComparison.Ordinal), "随机模式 0 是开关交替");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.RandomFlashConfig, new byte[] { 1 }).Fields[0].Value.Contains("每帧", StringComparison.Ordinal), "随机模式 1 是连续随机");
        var blink = DiagnosticPayloadDecoder.Decode(CharacteristicKey.BlinkConfig, Convert.FromHexString("02FF00000000FFD007"));
        Check(blink.Fields.Count == 4 && blink.Fields[1].Bytes == "1..3" && blink.Fields[2].Bytes == "4..6", "双色闪烁颜色数组顺序");
        Check(blink.Fields[3].Value == "2000 ms" && blink.Fields[3].Bytes == "7..8", "双色闪烁 period 偏移和值");

        var custom = DiagnosticPayloadDecoder.Decode(CharacteristicKey.CustomBreathConfig, Convert.FromHexString("0201020304050634127856"));
        Check(custom.Fields.Count == 5 && custom.Fields[1].Value.StartsWith("#010203", StringComparison.Ordinal) && custom.Fields[2].Value.StartsWith("#040506", StringComparison.Ordinal), "自定义呼吸先解析全部颜色");
        Check(custom.Fields[3].Value == "4660 ms" && custom.Fields[3].Bytes == "7..8", "自定义呼吸首时长在颜色数组之后");
        Check(custom.Fields[4].Value == "22136 ms" && custom.Fields[4].Bytes == "9..10" && custom.Fields[4].Name.Contains("1 → 0", StringComparison.Ordinal), "最后渐变时长循环回第一色");
        for (var count = 1; count <= 9; count++)
        {
            var configuration = new LightingConfiguration { Mode = LightMode.CustomBreath, Colors = new(), FadeMilliseconds = new() };
            for (var index = 0; index < count; index++) { configuration.Colors.Add(new((byte)index, 2, 3)); configuration.FadeMilliseconds.Add((ushort)(1000 + index)); }
            var payload = PacketCodec.EncodeLighting(configuration);
            var description = DiagnosticPayloadDecoder.Decode(CharacteristicKey.CustomBreathConfig, payload);
            Check(description.IsValid && description.Fields.Count == 1 + 2 * count, "1..9 色呼吸包含所有颜色和时长字段");
            Check(description.Fields[^1].Bytes == $"{payload.Length - 2}..{payload.Length - 1}", "1..9 色末时长偏移正确");
            configuration.Mode = LightMode.Chase;
            var chase = DiagnosticPayloadDecoder.Decode(CharacteristicKey.ChaseConfig, PacketCodec.EncodeLighting(configuration));
            Check(chase.Fields.Count == 1 + count && chase.Fields[^1].Bytes == $"{1 + 3 * (count - 1)}..{3 * count}", "1..9 色流光包含完整 RGB 数组");
        }

        var roundConfiguration = new LightingConfiguration { Mode = LightMode.Round, MainColor = new(1, 2, 3), SecondaryColor = new(4, 5, 6), Range = 4.5f, InnerRange = 1.25f, WaitMilliseconds = 16909060, CenterSpeed = 2.5f };
        var roundBytes = PacketCodec.EncodeLighting(roundConfiguration);
        var round = DiagnosticPayloadDecoder.Decode(CharacteristicKey.RoundConfig, roundBytes);
        Check(round.Fields.Count == 6, "Round 暴露全部六个结构字段");
        Check(round.Fields[2].Value == "4.5 灯距" && round.Fields[2].Bytes == "6..9" && round.Fields[2].Type.Contains("float32", StringComparison.Ordinal), "Round range float32 偏移 6");
        Check(round.Fields[3].Value == "1.25 灯距" && round.Fields[3].Bytes == "10..13", "Round range_main 偏移 10");
        Check(round.Fields[4].Value == "16909060 ms" && round.Fields[4].Bytes == "14..17" && round.Fields[4].Type.Contains("int32", StringComparison.Ordinal), "Round wait i32 偏移 14，小端多字节值");
        Check(round.Fields[5].Value == "2.5 灯距/s" && round.Fields[5].Bytes == "18..21", "Round speed float32 偏移 18");
        foreach (var offset in new[] { 6, 10, 18 })
        {
            var invalid = roundBytes.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(offset), float.NaN);
            CheckInvalidDescription(CharacteristicKey.RoundConfig, invalid, "Round NaN 不抛到 UI");
        }

        foreach (var definition in ProtocolCatalog.All.Where(item => item.ServiceUuid == BleUuids.LaptopService && item.FixedLength == 8))
        {
            CheckInvalidDescription(definition.Key, PacketCodec.EncodeDouble(double.NaN), "遥测 NaN");
            CheckInvalidDescription(definition.Key, PacketCodec.EncodeDouble(double.PositiveInfinity), "遥测无穷大");
        }
        var cpuTemperature = DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopCpuTemperature, Convert.FromHexString("0000000000D45040"));
        Check(cpuTemperature.Fields[0].Value == "67.3125 °C" && cpuTemperature.Fields[0].Bytes == "0..7", "真实收发向量解码小端 double 温度");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopCpuUsage, PacketCodec.EncodeDouble(12.5)).Fields[0].Value == "12.5 %", "占用率单位为百分比");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopCpuVoltage, PacketCodec.EncodeDouble(1.125)).Fields[0].Value == "1.125 V", "CPU 电压单位为 V");
        Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopGpuClock, PacketCodec.EncodeDouble(1800)).Fields[0].Value == "1800 MHz", "频率单位为 MHz");
        foreach (var key in new[] { CharacteristicKey.LaptopMemoryUsed, CharacteristicKey.LaptopMemoryTotal, CharacteristicKey.LaptopGpuMemoryUsed, CharacteristicKey.LaptopGpuMemoryTotal })
        {
            var description = DiagnosticPayloadDecoder.Decode(key, PacketCodec.EncodeDouble(2048));
            Check(description.Fields[0].Value == "2048（协议单位待确认）" && !description.Summary.Contains("GiB", StringComparison.Ordinal), "内存不猜测单位或换算 GiB");
        }
        var fans = DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopFanUsed, samples[CharacteristicKey.LaptopFanUsed]);
        Check(fans.Fields.Count == 5 && fans.Summary.Contains("3 个已安装槽位", StringComparison.Ordinal), "fan_used 代表存在数量而非占用率/转动状态");
        Check(fans.Fields[1].Value == "0 · 未安装" && fans.Fields[4].Value == "1 · 已安装" && fans.Fields[4].Bytes == "32..39", "fan_used 保留稀疏的五个安装槽位");
        var fanSpeeds = DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopFanSpeed, samples[CharacteristicKey.LaptopFanSpeed]);
        Check(fanSpeeds.Fields.Count == 5 && fanSpeeds.Fields[1].Value == "1250.5 RPM" && fanSpeeds.Fields[4].Value == "12345.75 RPM" && fanSpeeds.Fields[4].Bytes == "32..39", "fan_speed 是五个 double RPM，不按 uint32 读取");
        CheckInvalidDescription(CharacteristicKey.LaptopFanUsed, PacketCodec.EncodeFanArray(new double[] { 1, 2, 0, 0, 0 }), "非法安装标记");
        CheckInvalidDescription(CharacteristicKey.LaptopFanSpeed, PacketCodec.EncodeFanArray(new double[] { 1, double.NaN, 0, 0, 0 }), "风扇数组 NaN");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(DiagnosticPayloadDecoder.Decode(CharacteristicKey.LaptopCpuVoltage, PacketCodec.EncodeDouble(1.125)).Fields[0].Value == "1.125 V", "诊断数值不随系统小数逗号改变");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static void CheckInvalidDescription(CharacteristicKey key, byte[] payload, string context)
    {
        var description = DiagnosticPayloadDecoder.Decode(key, payload);
        Check(!description.IsValid && !string.IsNullOrWhiteSpace(description.Error) && description.Fields.Count == 0, $"{key} {context} 返回明确错误而不抛异常");
    }

    private static void Accept(CharacteristicKey key, byte[] payload) { PacketCodec.Validate(key, payload); _assertions++; }
    private static void Reject(CharacteristicKey key, byte[] payload)
    {
        try { PacketCodec.Validate(key, payload); }
        catch (ArgumentException) { _assertions++; return; }
        throw new InvalidOperationException($"不合法载荷未被拒绝：{key} {Convert.ToHexString(payload)}");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
}
