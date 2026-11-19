using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFan.App.Services;

/// <summary>Minimal dynamically bound NVML ABI. An optional missing metric never disables enumeration.</summary>
internal sealed class NvidiaNvmlNativeApi : INvidiaNvmlApi
{
    private nint _library;
    private bool _initialized, _disposed;
    private readonly List<Init> _initialize = [];
    private readonly List<GetCount> _counts = [];
    private readonly List<GetHandle> _handles = [];
    private readonly List<GetPci> _pci = [];
    private Shutdown? _shutdown;
    private GetString? _uuid, _name;
    private GetTemperature? _temperature;
    private GetUsage? _usage;
    private GetClock? _clock;
    private GetMemoryV2? _memoryV2;
    private GetMemory? _memory;

    public NvidiaNvmlResult Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return NvidiaNvmlResult.Success;
        if (_library == 0 && !LoadLibrary()) return NvidiaNvmlResult.LibraryNotFound;
        // Own exactly one successful init reference. LHM may hold a separate reference in the same process.
        if (_initialize.Count == 0 || _shutdown is null || _counts.Count == 0 || _handles.Count == 0 || _uuid is null)
            return NvidiaNvmlResult.FunctionNotFound;
        var result = NvidiaNvmlResult.FunctionNotFound;
        foreach (var initialize in _initialize)
        {
            result = initialize();
            if (result == NvidiaNvmlResult.Success) { _initialized = true; return result; }
            if (!CanTryLegacy(result)) break;
        }
        return result;
    }

    public NvidiaNvmlResult GetDeviceCount(out uint count)
    {
        count = 0;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        var result = NvidiaNvmlResult.FunctionNotFound;
        foreach (var get in _counts)
        { result = get(out count); if (result == NvidiaNvmlResult.Success || !CanTryLegacy(result)) break; }
        return result;
    }

    public NvidiaNvmlResult GetDevice(uint index, out NvidiaNvmlDevice? device)
    {
        device = null;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        var result = NvidiaNvmlResult.FunctionNotFound; nint handle = 0;
        foreach (var get in _handles)
        { result = get(index, out handle); if (result == NvidiaNvmlResult.Success || !CanTryLegacy(result)) break; }
        if (result != NvidiaNvmlResult.Success) return result;
        if (handle == 0) return NvidiaNvmlResult.NoData;
        result = ReadString(_uuid, handle, 96, out var uuid);
        if (result != NvidiaNvmlResult.Success) return result;
        if (string.IsNullOrWhiteSpace(uuid) || !uuid.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)) return NvidiaNvmlResult.NoData;
        _ = ReadString(_name, handle, 128, out var name);
        device = new(handle, uuid, string.IsNullOrWhiteSpace(name) ? "NVIDIA GPU" : name, ReadPciIdentity(handle));
        return NvidiaNvmlResult.Success;
    }

    public NvidiaNvmlResult ReadTemperature(nint handle, out double value)
    {
        value = 0;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        if (_temperature is null) return NvidiaNvmlResult.FunctionNotFound;
        var result = _temperature(handle, 0, out var temperature); value = temperature; return result;
    }
    public NvidiaNvmlResult ReadUsage(nint handle, out double value)
    {
        value = 0;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        if (_usage is null) return NvidiaNvmlResult.FunctionNotFound;
        var result = _usage(handle, out var usage); value = usage.Gpu; return result;
    }
    public NvidiaNvmlResult ReadGraphicsClock(nint handle, out double value)
    {
        value = 0;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        if (_clock is null) return NvidiaNvmlResult.FunctionNotFound;
        // NVML_CLOCK_GRAPHICS (0), not memory clock, nominal boost clock, or controller busy percent.
        var result = _clock(handle, 0, out var clock); value = clock; return result;
    }
    public NvidiaNvmlResult ReadMemory(nint handle, out ulong used, out ulong total)
    {
        used = total = 0;
        if (!_initialized) return NvidiaNvmlResult.Uninitialized;
        var result = NvidiaNvmlResult.FunctionNotFound;
        if (_memoryV2 is not null)
        {
            var memory = new MemoryV2 { Version = (2u << 24) | 40u };
            result = _memoryV2(handle, ref memory);
            if (result == NvidiaNvmlResult.Success) { used = memory.Used; total = memory.Total; return result; }
            if (!CanTryLegacy(result)) return result;
        }
        if (_memory is null) return result;
        result = _memory(handle, out var legacy); used = legacy.Used; total = legacy.Total; return result;
    }

    private bool LoadLibrary()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var path in NvidiaNvmlLibraryPaths.GetCandidates())
        {
            try
            {
                // Full approved path, and dependency lookup restricted to that directory + System32.
                // Never use bare nvml.dll, current directory, the application's directory, or PATH.
                if (!NativeLibrary.TryLoad(path, typeof(NvidiaNvmlNativeApi).Assembly,
                    DllImportSearchPath.System32 | DllImportSearchPath.UseDllDirectoryForDependencies, out _library)) continue;
                Bind(_initialize, "nvmlInit_v2", "nvmlInit");
                _shutdown = Resolve<Shutdown>("nvmlShutdown");
                Bind(_counts, "nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
                Bind(_handles, "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
                _uuid = Resolve<GetString>("nvmlDeviceGetUUID"); _name = Resolve<GetString>("nvmlDeviceGetName");
                Bind(_pci, "nvmlDeviceGetPciInfo_v3", "nvmlDeviceGetPciInfo_v2", "nvmlDeviceGetPciInfo");
                _temperature = Resolve<GetTemperature>("nvmlDeviceGetTemperature");
                _usage = Resolve<GetUsage>("nvmlDeviceGetUtilizationRates");
                _clock = Resolve<GetClock>("nvmlDeviceGetClockInfo");
                _memoryV2 = Resolve<GetMemoryV2>("nvmlDeviceGetMemoryInfo_v2");
                _memory = Resolve<GetMemory>("nvmlDeviceGetMemoryInfo");
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileLoadException)
            { if (_library != 0) { NativeLibrary.Free(_library); _library = 0; } }
        }
        return false;
    }
    private T? Resolve<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(_library, name, out var pointer)
        ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : null;
    private void Bind<T>(List<T> target, params string[] names) where T : Delegate
    { foreach (var name in names) if (Resolve<T>(name) is { } method) target.Add(method); }
    private static bool CanTryLegacy(NvidiaNvmlResult result) => result is NvidiaNvmlResult.NotSupported or NvidiaNvmlResult.FunctionNotFound or NvidiaNvmlResult.VersionMismatch;
    private static NvidiaNvmlResult ReadString(GetString? get, nint handle, int length, out string value)
    {
        value = "";
        if (get is null) return NvidiaNvmlResult.FunctionNotFound;
        var buffer = new byte[length]; var result = get(handle, buffer, (uint)buffer.Length);
        if (result != NvidiaNvmlResult.Success) return result;
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) return NvidiaNvmlResult.InsufficientSize;
        value = Encoding.ASCII.GetString(buffer, 0, end).Trim(); return result;
    }
    private NvidiaPciIdentity? ReadPciIdentity(nint handle)
    {
        // v3 needs 68 bytes; older exports use the shared prefix and may write a shorter structure.
        // Keep the larger zeroed buffer for ALL versions; never hand a v3 export an old 52-byte buffer.
        var buffer = Marshal.AllocHGlobal(68);
        try
        {
            foreach (var get in _pci)
            {
                Marshal.Copy(new byte[68], 0, buffer, 68);
                var result = get(handle, buffer);
                if (result != NvidiaNvmlResult.Success) { if (CanTryLegacy(result)) continue; return null; }
                var legacy = new byte[16]; var current = new byte[32];
                Marshal.Copy(buffer, legacy, 0, legacy.Length); Marshal.Copy(buffer + 36, current, 0, current.Length);
                var busId = AsciiTerminated(current);
                if (string.IsNullOrWhiteSpace(busId)) busId = AsciiTerminated(legacy);
                var domain = unchecked((uint)Marshal.ReadInt32(buffer, 16));
                var bus = unchecked((uint)Marshal.ReadInt32(buffer, 20));
                var device = unchecked((uint)Marshal.ReadInt32(buffer, 24));
                if (!NvidiaNvmlPciBusId.TryParse(busId, out var parsed) || parsed.Domain != domain || parsed.Bus != bus || parsed.Device != device) return null;
                return parsed with { DeviceId = unchecked((uint)Marshal.ReadInt32(buffer, 28)), SubsystemId = unchecked((uint)Marshal.ReadInt32(buffer, 32)) };
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return null;
    }
    private static string AsciiTerminated(byte[] buffer)
    { var end = Array.IndexOf(buffer, (byte)0); return end < 0 ? "" : Encoding.ASCII.GetString(buffer, 0, end); }

    public void Reset()
    {
        if (!_initialized) return;
        _initialized = false;
        _shutdown?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { Reset(); }
        finally { if (_library != 0) { NativeLibrary.Free(_library); _library = 0; } }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetHandle(uint index, out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetString(nint handle, [Out] byte[] buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetPci(nint handle, nint info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetTemperature(nint handle, uint sensor, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetUsage(nint handle, out Utilization usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetClock(nint handle, uint type, out uint clock);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetMemory(nint handle, out Memory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NvidiaNvmlResult GetMemoryV2(nint handle, ref MemoryV2 memory);
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct MemoryV2
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public ulong Total;
        [FieldOffset(16)] public ulong Reserved;
        [FieldOffset(24)] public ulong Free;
        [FieldOffset(32)] public ulong Used;
    }
}

/// <summary>Only OS-known system and Program Files locations are allowed; there is no PATH lookup.</summary>
public static class NvidiaNvmlLibraryPaths
{
    public static IReadOnlyList<string> GetCandidates()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var directories = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        var result = new List<string>();
        if (Path.IsPathFullyQualified(directories[0])) result.Add(Path.Combine(directories[0], "nvml.dll"));
        if (Path.IsPathFullyQualified(directories[1])) result.Add(Path.Combine(directories[1], "NVIDIA Corporation", "NVSMI", "nvml.dll"));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class NvidiaNvmlPciBusId
{
    private static readonly Regex BusId = new(@"^\s*(?<domain>[0-9A-F]{4,8}):(?<bus>[0-9A-F]{2}):(?<device>[0-9A-F]{2})\.(?<function>[0-7])\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static bool TryParse(string? text, out NvidiaPciIdentity identity)
    {
        identity = new(0, 0, 0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var match = BusId.Match(text);
        if (!match.Success || !uint.TryParse(match.Groups["domain"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var domain) || domain > 65535 ||
            !uint.TryParse(match.Groups["bus"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) ||
            !uint.TryParse(match.Groups["device"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var device) || device > 31 ||
            !uint.TryParse(match.Groups["function"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var function)) return false;
        identity = new(domain, bus, device, function, 0, 0); return true;
    }
}
