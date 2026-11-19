using SmartFan.App.Models;

namespace SmartFan.App.Services;

public enum NvidiaNvmlResult
{
    Success = 0, Uninitialized = 1, InvalidArgument = 2, NotSupported = 3, NoPermission = 4,
    NotFound = 6, InsufficientSize = 7, DriverNotLoaded = 9, Timeout = 10, LibraryNotFound = 12,
    FunctionNotFound = 13, GpuLost = 15, ResetRequired = 16, DriverMismatch = 18,
    NoData = 21, VersionMismatch = 25, Unknown = 999
}
public sealed record NvidiaPciIdentity(uint Domain, uint Bus, uint Device, uint Function, uint DeviceId, uint SubsystemId);
public sealed record NvidiaNvmlDevice(nint Handle, string Uuid, string Name, NvidiaPciIdentity? Pci);
public sealed record NvidiaLhmPciAdapter(string HardwareId, NvidiaPciIdentity Pci, bool DomainKnown = true);
public interface INvidiaNvmlApi : IDisposable
{
    NvidiaNvmlResult Initialize();
    NvidiaNvmlResult GetDeviceCount(out uint count);
    NvidiaNvmlResult GetDevice(uint index, out NvidiaNvmlDevice? device);
    NvidiaNvmlResult ReadTemperature(nint handle, out double value);
    NvidiaNvmlResult ReadUsage(nint handle, out double value);
    NvidiaNvmlResult ReadGraphicsClock(nint handle, out double value);
    NvidiaNvmlResult ReadMemory(nint handle, out ulong used, out ulong total);
    void Reset();
}
public interface INvidiaLhmIdentityReader
{
    IReadOnlyList<NvidiaLhmPciAdapter> ReadAdapters(IReadOnlyDictionary<string, string> deviceIds);
}

/// <summary>Independent, read-only NVIDIA fallback. NVML sensor IDs retain their origin even when
/// complete PCI evidence allows them to share the pinned LHM 0.9.6 NVAPI hardware identity.</summary>
public sealed class NvidiaNvmlSensorProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly INvidiaNvmlApi _api;
    private readonly INvidiaLhmIdentityReader _identities;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _assignedSlots = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _lhmDeviceIds = new Dictionary<string, string>();
    private IReadOnlyList<NvidiaNvmlDevice> _devices = [];
    private IReadOnlyList<NvidiaNvmlDevice> _knownDevices = [];
    private List<SensorDescriptor> _cached = [];
    private DateTimeOffset _lastCapture = DateTimeOffset.MinValue, _nextInitialize, _nextEnumerate;
    private bool _initialized, _disposed;
    public string Status { get; private set; } = "NVIDIA NVML 尚未采样";
    public NvidiaNvmlSensorProvider() : this(new NvidiaNvmlNativeApi()) { }
    public NvidiaNvmlSensorProvider(INvidiaNvmlApi api, INvidiaLhmIdentityReader? identities = null, Func<DateTimeOffset>? clock = null)
    { _api = api ?? throw new ArgumentNullException(nameof(api)); _identities = identities ?? new NvidiaPnpIdentityReader(); _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    /// <summary>Supply actual LHM GenericGpu.DeviceId values, never an inferred NVAPI/NVML index mapping.</summary>
    public void SetLhmDeviceIds(IReadOnlyDictionary<string, string> hardwareIdToDeviceId)
    {
        ArgumentNullException.ThrowIfNull(hardwareIdToDeviceId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = hardwareIdToDeviceId.Where(pair => pair.Key.StartsWith("/gpu-nvidia/", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (next.Count == _lhmDeviceIds.Count && next.All(pair => _lhmDeviceIds.TryGetValue(pair.Key, out var previous) && string.Equals(previous, pair.Value, StringComparison.OrdinalIgnoreCase))) return;
            _lhmDeviceIds = next;
            _aliases.Clear();
            _nextEnumerate = DateTimeOffset.MinValue;
            // A topology transfer invalidates the identity attached to cached rows, even within this second.
            _cached = [];
            _lastCapture = DateTimeOffset.MinValue;
        }
    }

    public IList<SensorDescriptor> Capture()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock();
            if (now >= _lastCapture && now - _lastCapture < TimeSpan.FromSeconds(1)) return _cached.ToList();
            _lastCapture = now; _cached = [];
            if (!_initialized && now < _nextInitialize) { AddUnavailable(Status); return _cached.ToList(); }
            try
            {
                if (!_initialized)
                {
                    var result = _api.Initialize();
                    if (result != NvidiaNvmlResult.Success)
                    { Status = $"NVIDIA NVML 初始化不可用：{result}；稍后重试"; _nextInitialize = now.AddSeconds(10); AddUnavailable(Status); return _cached.ToList(); }
                    _initialized = true; _nextEnumerate = DateTimeOffset.MinValue;
                }
                if (now >= _nextEnumerate) Enumerate(now);
                if (_devices.Count == 0) { AddUnavailable(Status); return _cached.ToList(); }
                var lost = false; var failures = new HashSet<string>(StringComparer.Ordinal);
                foreach (var device in _devices)
                {
                    var uuid = device.Uuid.Trim().ToLowerInvariant();
                    var hardwareId = _aliases.GetValueOrDefault(uuid) ?? $"/nvml/{Uri.EscapeDataString(uuid)}";
                    var origin = $"/nvml/{Uri.EscapeDataString(uuid)}";
                    Add("GPU Core", "Temperature", "temperature", () => { var result = _api.ReadTemperature(device.Handle, out var value); return (result, value); }, value => value is > 0 and <= 150);
                    Add("GPU Core", "Load", "usage", () => { var result = _api.ReadUsage(device.Handle, out var value); return (result, value); }, value => value is >= 0 and <= 100);
                    Add("GPU Core", "Clock", "clock", () => { var result = _api.ReadGraphicsClock(device.Handle, out var value); return (result, value); }, value => value is > 0 and < 20000);
                    NvidiaNvmlResult memoryResult; ulong used = 0, total = 0;
                    try { memoryResult = _api.ReadMemory(device.Handle, out used, out total); }
                    catch (Exception ex) { memoryResult = NvidiaNvmlResult.Unknown; failures.Add($"显存：{ex.GetType().Name}"); }
                    if (memoryResult == NvidiaNvmlResult.Success && (total == 0 || used > total)) memoryResult = NvidiaNvmlResult.NoData;
                    AddValue("GPU Memory Used", "SmallData", "memory-used", memoryResult, used / 1048576d);
                    AddValue("GPU Memory Total", "SmallData", "memory-total", memoryResult, total / 1048576d);

                    void Add(string name, string type, string suffix, Func<(NvidiaNvmlResult Result, double Value)> read, Func<double, bool> valid)
                    {
                        NvidiaNvmlResult result; double value = 0;
                        try { (result, value) = read(); }
                        catch (Exception ex) { result = NvidiaNvmlResult.Unknown; failures.Add($"{type}：{ex.GetType().Name}"); }
                        if (result == NvidiaNvmlResult.Success && (!double.IsFinite(value) || !valid(value))) result = NvidiaNvmlResult.NoData;
                        AddValue(name, type, suffix, result, value);
                    }
                    void AddValue(string name, string type, string suffix, NvidiaNvmlResult result, double value)
                    {
                        lost |= RequiresReinitialization(result);
                        if (result != NvidiaNvmlResult.Success) failures.Add($"{type}：{result}");
                        _cached.Add(new SensorDescriptor
                        {
                            Id = $"{origin}/{suffix}", HardwareId = hardwareId,
                            HardwareName = _aliases.ContainsKey(uuid) ? device.Name : $"{device.Name} · NVML",
                            HardwareType = "GpuNvidia", Name = name, SensorType = type,
                            Value = result == NvidiaNvmlResult.Success ? value : null,
                            ReadStatus = result == NvidiaNvmlResult.Success ? "NVIDIA NVML 只读采样" : $"NVIDIA NVML：{result}"
                        });
                    }
                }
                if (_devices.Count > 0) Status = $"NVIDIA NVML {_devices.Count} 个设备；{_cached.Count(item => item.Value.HasValue)}/{_cached.Count} 项有效；{_aliases.Count}/{_devices.Count} 个 PCI 身份已匹配" +
                    (failures.Count == 0 ? "" : $"；{string.Join("，", failures.OrderBy(item => item))}");
                if (lost)
                {
                    Status = $"NVIDIA NVML 设备暂不可用：{string.Join("，", failures.OrderBy(item => item))}；等待重新枚举";
                    ResetCore(now, 2); _cached = []; AddUnavailable(Status);
                }
            }
            catch (Exception ex)
            { Status = $"NVIDIA NVML 采样隔离失败：{ex.GetType().Name}：{ex.Message}"; ResetCore(now, 10); _cached = []; AddUnavailable(Status); }
            return _cached.ToList();
        }
    }
    private void Enumerate(DateTimeOffset now)
    {
        var result = _api.GetDeviceCount(out var count);
        if (result != NvidiaNvmlResult.Success) throw new IOException($"NVML 枚举失败：{result}");
        if (count > 64) throw new IOException("NVML 返回异常设备数量。");
        var devices = new List<NvidiaNvmlDevice>(); var complete = true;
        for (uint index = 0; index < count; index++)
        {
            try
            {
                var status = _api.GetDevice(index, out var device);
                if (RequiresReinitialization(status)) { Status = $"NVIDIA NVML 设备暂不可用：{status}；重新枚举"; ResetCore(now, 2); return; }
                if (status == NvidiaNvmlResult.Success && device is not null && device.Handle != 0 && !string.IsNullOrWhiteSpace(device.Uuid) && device.Uuid.Length <= 128) devices.Add(device);
                else complete = false;
            }
            catch { complete = false; }
        }
        var unique = devices.GroupBy(device => device.Uuid.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        complete &= unique.Length == devices.Count; _devices = unique;
        if (unique.Length > 0) _knownDevices = unique;
        _nextEnumerate = now.AddSeconds(unique.Length == 0 ? 10 : 30);
        Status = unique.Length == 0 ? "NVIDIA NVML 未枚举到可识别的设备（可能休眠、未授权或驱动未就绪）" : "NVIDIA NVML 已枚举";
        // Revalidate aliases with current configured PnP nodes; never keep an old alias after unplug.
        _aliases.Clear();
        if (!complete || _lhmDeviceIds.Count == 0) return;
        IReadOnlyList<NvidiaLhmPciAdapter> adapters;
        try { adapters = _identities.ReadAdapters(_lhmDeviceIds); } catch { return; }
        foreach (var (uuid, canonical) in MatchHardwareIds(unique, adapters, adapters.Count == _lhmDeviceIds.Count))
        {
            if (_aliases.ContainsKey(uuid) || _assignedSlots.TryGetValue(canonical, out var occupant) && occupant != uuid) continue;
            _aliases[uuid] = canonical; _assignedSlots[canonical] = uuid;
        }
    }
    /// <summary>Only exact, unique PCI domain/bus/device/function + device/subsystem evidence may bridge APIs.</summary>
    public static IReadOnlyDictionary<string, string> MatchHardwareIds(IReadOnlyList<NvidiaNvmlDevice> devices, IReadOnlyList<NvidiaLhmPciAdapter> adapters, bool completeLhmIdentitySet = true)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Numeric bus/address properties are language independent but do not prove segment by themselves.
        // Use that branch only with complete, uniquely identified single-segment NVIDIA sets on both sides.
        var allowUnknownDomain = completeLhmIdentitySet && devices.Count == adapters.Count && devices.Count > 0 &&
            devices.All(device => device.Pci is { Domain: 0 }) && adapters.All(adapter => adapter.Pci.Domain == 0);
        foreach (var device in devices)
        {
            if (device.Pci is null || string.IsNullOrWhiteSpace(device.Uuid) ||
                devices.Count(candidate => string.Equals(candidate.Uuid.Trim(), device.Uuid.Trim(), StringComparison.OrdinalIgnoreCase)) != 1 ||
                devices.Count(candidate => SameAddress(candidate.Pci, device.Pci)) != 1) continue;
            var matches = adapters.Where(adapter => SameAddress(adapter.Pci, device.Pci)).ToArray();
            if (matches.Length != 1 || matches[0].Pci != device.Pci ||
                (!matches[0].DomainKnown && !allowUnknownDomain) ||
                !matches[0].HardwareId.StartsWith("/gpu-nvidia/", StringComparison.Ordinal) ||
                adapters.Count(adapter => adapter.HardwareId == matches[0].HardwareId) != 1) continue;
            result[device.Uuid.Trim().ToLowerInvariant()] = matches[0].HardwareId;
        }
        return result;
    }
    private static bool SameAddress(NvidiaPciIdentity? left, NvidiaPciIdentity? right) => left is not null && right is not null &&
        left.Domain == right.Domain && left.Bus == right.Bus && left.Device == right.Device && left.Function == right.Function;
    private void AddUnavailable(string reason)
    {
        foreach (var device in _knownDevices)
        {
            var uuid = device.Uuid.Trim().ToLowerInvariant(); var origin = $"/nvml/{Uri.EscapeDataString(uuid)}";
            var hardwareId = _aliases.GetValueOrDefault(uuid) ?? origin;
            foreach (var (name, type, suffix) in new[] { ("GPU Core", "Temperature", "temperature"), ("GPU Core", "Load", "usage"),
                ("GPU Core", "Clock", "clock"), ("GPU Memory Used", "SmallData", "memory-used"), ("GPU Memory Total", "SmallData", "memory-total") })
                _cached.Add(new SensorDescriptor { Id = $"{origin}/{suffix}", HardwareId = hardwareId, HardwareName = device.Name,
                    HardwareType = "GpuNvidia", Name = name, SensorType = type, Value = null, ReadStatus = reason });
        }
    }
    private static bool RequiresReinitialization(NvidiaNvmlResult result) => result is NvidiaNvmlResult.GpuLost or NvidiaNvmlResult.ResetRequired or NvidiaNvmlResult.Uninitialized or NvidiaNvmlResult.DriverNotLoaded or NvidiaNvmlResult.DriverMismatch;
    private void ResetCore(DateTimeOffset now, int delaySeconds)
    {
        try { _api.Reset(); } catch { }
        _initialized = false; _devices = []; _nextInitialize = now.AddSeconds(delaySeconds);
    }
    public void NotifyResume()
    {
        lock (_gate)
        { if (_disposed) return; ResetCore(_clock(), 0); _cached = []; _lastCapture = DateTimeOffset.MinValue; }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _cached = []; _devices = [];
            _api.Dispose();
        }
    }
}
