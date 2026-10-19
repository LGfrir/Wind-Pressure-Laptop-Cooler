using SmartFan.App.Models;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;

namespace SmartFan.App.Services;

public sealed class TelemetrySendResult
{
    public int Written { get; init; }
    public int Unchanged { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string Message => $"已发送 {Written} 项 · {Unchanged} 项无变化" + (Notes.Count > 0 ? $" · {string.Join("；", Notes)}" : "");
}

/// <summary>仅推送真实监控值；既不计算 PWM，也不在 PC 端代替固件执行温控。</summary>
public sealed class TelemetryUploader
{
    private readonly IBleClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<CharacteristicKey, byte[]> _lastSent = new();
    private readonly Dictionary<CharacteristicKey, DateTimeOffset> _lastWrite = new();
    private long _generation;

    public TelemetryUploader(IBleClient client) => _client = client;

    // 根会话在每次连接成功时调用，保证设备重启后常量也会重新发送。
    // 用代数失效，避免与正在执行的一次异步写并发修改 Dictionary。
    public void ResetCache() => Interlocked.Increment(ref _generation);
    private long _cacheGeneration = -1;

    public async Task<TelemetrySendResult> SendAsync(TelemetrySnapshot snapshot, TelemetrySettings settings, CancellationToken cancellationToken = default)
    {
        if (snapshot.IsDemo) return new TelemetrySendResult { Notes = ["演示模式禁止上报"] };
        if (!settings.Enabled) return new TelemetrySendResult { Notes = ["自动上报已关闭"] };
        if (!_client.IsConnected) return new TelemetrySendResult { Notes = ["设备未连接"] };
        if (DateTimeOffset.Now - snapshot.Timestamp > TimeSpan.FromSeconds(5))
            return new TelemetrySendResult { Notes = ["采样已过期，本次不上报"] };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation = Interlocked.Read(ref _generation);
            if (generation != _cacheGeneration)
            {
                _lastSent.Clear(); _lastWrite.Clear(); _cacheGeneration = generation;
            }
            var values = BuildPayloads(snapshot, settings, out var notes);
            int written = 0, unchanged = 0;
            foreach (var pair in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool heartbeat = pair.Key is CharacteristicKey.LaptopCpuTemperature or CharacteristicKey.LaptopGpuTemperature &&
                    (!_lastWrite.TryGetValue(pair.Key, out var lastWrite) || DateTimeOffset.Now - lastWrite >= TimeSpan.FromSeconds(5));
                if (!heartbeat && _lastSent.TryGetValue(pair.Key, out var previous) && previous.AsSpan().SequenceEqual(pair.Value))
                { unchanged++; continue; }
                // 普通 WRITE 有应答；所有真实 GATT 操作仍由 BLE 层的串行队列管理。
                await _client.WriteAsync(pair.Key, pair.Value, verify: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                // 连接恰在 await 期间更新时，不保留旧连接缓存。
                if (generation != Interlocked.Read(ref _generation)) break;
                _lastSent[pair.Key] = pair.Value;
                _lastWrite[pair.Key] = DateTimeOffset.Now;
                written++;
            }
            foreach (var key in _lastSent.Keys.Where(key => !values.ContainsKey(key)).ToList())
            {
                _lastSent.Remove(key); _lastWrite.Remove(key);
            }
            return new TelemetrySendResult { Written = written, Unchanged = unchanged, Notes = notes };
        }
        finally { _gate.Release(); }
    }

    public static IReadOnlyDictionary<CharacteristicKey, byte[]> BuildPayloads(TelemetrySnapshot snapshot, TelemetrySettings settings, out IReadOnlyList<string> notes)
    {
        var result = new Dictionary<CharacteristicKey, byte[]>();
        var messages = new List<string>();
        if (snapshot.IsDemo) { notes = new[] { "演示数据不可上报" }; return result; }
        void Add(CharacteristicKey key, double? value)
        {
            string alias = ProtocolCatalog.Get(key).Name;
            if (!settings.EnabledItems.TryGetValue(alias, out bool enabled) || !enabled) return;
            if (!value.HasValue || !double.IsFinite(value.Value)) return;
            var bytes = PacketCodec.EncodeDouble(value.Value);
            PacketCodec.Validate(key, bytes);
            result[key] = bytes;
        }
        Add(CharacteristicKey.LaptopCpuUsage, snapshot.CpuUsage);
        Add(CharacteristicKey.LaptopCpuTemperature, snapshot.CpuTemperature);
        if (snapshot.CpuVoltageKind == CpuVoltageKind.Vid)
        {
            if (snapshot.CpuVoltage.HasValue && settings.EnabledItems.GetValueOrDefault("laptop_cpu_volta"))
                messages.Add("CPU VID 是请求电压，仅在本机显示，不作为实测 CPU 电压上报");
        }
        else Add(CharacteristicKey.LaptopCpuVoltage, snapshot.CpuVoltage);
        Add(CharacteristicKey.LaptopCpuClock, snapshot.CpuClockMhz);
        Add(CharacteristicKey.LaptopGpuUsage, snapshot.GpuUsage);
        Add(CharacteristicKey.LaptopGpuTemperature, snapshot.GpuTemperature);
        Add(CharacteristicKey.LaptopGpuClock, snapshot.GpuClockMhz);
        if (settings.RamWireUnit == RamWireUnit.Unspecified)
            messages.Add("RAM/显存协议单位待确认，四项内存指标未上报");
        else
        {
            Add(CharacteristicKey.LaptopGpuMemoryUsed, ConvertMemory(snapshot.GpuRamUsedMiB, settings.RamWireUnit));
            Add(CharacteristicKey.LaptopGpuMemoryTotal, ConvertMemory(snapshot.GpuRamTotalMiB, settings.RamWireUnit));
            Add(CharacteristicKey.LaptopMemoryUsed, ConvertMemory(snapshot.RamUsedMiB, settings.RamWireUnit));
            Add(CharacteristicKey.LaptopMemoryTotal, ConvertMemory(snapshot.RamTotalMiB, settings.RamWireUnit));
        }
        if (settings.EnabledItems.GetValueOrDefault("laptop_fan_used") && snapshot.FanPresenceAvailable)
        {
            if (snapshot.FanPresent.Length != 5 || snapshot.FanPresent.Any(v => v is not (0 or 1)))
                throw new ArgumentException("fan_used 必须为 5 个 0/1 存在标志。");
            result[CharacteristicKey.LaptopFanUsed] = PacketCodec.EncodeFanArray(snapshot.FanPresent);
        }
        if (settings.EnabledItems.GetValueOrDefault("laptop_fan_speed") && snapshot.FanPresenceAvailable)
        {
            if (snapshot.FanRpm.Length != 5 || snapshot.FanPresent.Length != 5) throw new ArgumentException("内置风扇数组必须为 5 个元素。");
            // 未安装的槽位填 0；已安装但读不到的传感器不能伪装为停转。
            bool canSend = Enumerable.Range(0, 5).All(i => snapshot.FanPresent[i] == 0 ||
                snapshot.FanRpm[i].HasValue && double.IsFinite(snapshot.FanRpm[i].GetValueOrDefault()) && snapshot.FanRpm[i] >= 0);
            if (canSend)
            {
                var speeds = Enumerable.Range(0, 5).Select(i => snapshot.FanPresent[i] == 0 ? 0d : snapshot.FanRpm[i].GetValueOrDefault()).ToArray();
                result[CharacteristicKey.LaptopFanSpeed] = PacketCodec.EncodeFanArray(speeds);
            }
            else messages.Add("部分内置风扇转速不可用，fan_speed 未上报");
        }
        notes = messages;
        return result;
    }

    private static double? ConvertMemory(double? mib, RamWireUnit unit) => unit switch
    {
        RamWireUnit.Bytes => mib * 1048576,
        RamWireUnit.MiB => mib,
        RamWireUnit.GiB => mib / 1024,
        _ => null
    };
}
