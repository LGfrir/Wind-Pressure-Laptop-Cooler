using SmartFan.App.Services;

internal static class NvidiaNvmlSensorProviderChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string pnp = @"PCI\VEN_10DE&DEV_2782&SUBSYS_513417AA&REV_A1\4&11223344&0&0008";
        var pci = new NvidiaPciIdentity(0, 1, 0, 0, 0x278210DE, 0x513417AA);
        check(NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI bus 1, device 0, function 0", out var parsed) && parsed == pci,
            "NVIDIA PnP parsing includes vendor, device, subsystem and complete single-segment PCI address");
        check(NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI segment 2 bus 1, device 0, function 0", out parsed) && parsed == (pci with { Domain = 2 }),
            "a documented multi-segment location retains its segment instead of silently treating it as zero");
        check(!NvidiaPnpIdentityReader.TryParseIdentity(pnp.Replace("10DE", "1002"), "PCI bus 1, device 0, function 0", out _) &&
              !NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI bus 256, device 0, function 0", out _) &&
              !NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI bus 1, device 32, function 0", out _) &&
              !NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI bus 1, device 0, function 8", out _) &&
              !NvidiaPnpIdentityReader.TryParseIdentity(pnp, "PCI 总线 1、设备 0、功能 0", out _),
            "non-NVIDIA IDs, impossible PCI bounds and unproved localized segments cannot create an exact identity");
        check(NvidiaPnpIdentityReader.TryParseNumericIdentity(pnp, 17, (3u << 16) | 2u, out parsed) && parsed == (pci with { Bus = 17, Device = 3, Function = 2 }) &&
              !NvidiaPnpIdentityReader.TryParseNumericIdentity(pnp, 1, uint.MaxValue, out _) &&
              !NvidiaPnpIdentityReader.TryParseNumericIdentity(pnp, 1, 8, out _),
            "language-independent CM numeric properties decode PCI device/function words and reject absent addresses");
        check(NvidiaNvmlPciBusId.TryParse("00000002:0A:03.1", out var nvmlPci) && nvmlPci is { Domain: 2, Bus: 10, Device: 3, Function: 1 } &&
              !NvidiaNvmlPciBusId.TryParse("0000:01:20.0", out _) && !NvidiaNvmlPciBusId.TryParse("0000:01:00.8", out _),
            "NVML hexadecimal BDF parsing preserves domain and function and rejects impossible addresses");
        var candidates = NvidiaNvmlLibraryPaths.GetCandidates();
        check(candidates.Count == 2 && candidates.All(Path.IsPathFullyQualified) &&
              candidates[0] == Path.Combine(Environment.SystemDirectory, "nvml.dll") &&
              candidates[1] == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            "NVML loading candidates are OS-known full paths rather than current-directory or PATH lookup");

        var a = new NvidiaNvmlDevice(1, "GPU-aaa", "Identical GPU model", pci);
        var b = new NvidiaNvmlDevice(2, "GPU-bbb", "Identical GPU model", pci with { Bus = 4 });
        NvidiaLhmPciAdapter[] adapters = [new("/gpu-nvidia/0", b.Pci!), new("/gpu-nvidia/1", a.Pci!)];
        var matches = NvidiaNvmlSensorProvider.MatchHardwareIds([a, b], adapters);
        check(matches["gpu-aaa"] == "/gpu-nvidia/1" && matches["gpu-bbb"] == "/gpu-nvidia/0",
            "identical GPU names and reversed library enumeration orders cannot cross-wire physical GPUs");
        check(NvidiaNvmlSensorProvider.MatchHardwareIds([a], [adapters[1] with { Pci = pci with { Domain = 1 } }]).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a], [adapters[1] with { Pci = pci with { Function = 1 } }]).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a], [adapters[1] with { Pci = pci with { SubsystemId = 0 } }]).Count == 0,
            "different PCI segment, function or subsystem is never merged because bus and model name happen to match");
        check(NvidiaNvmlSensorProvider.MatchHardwareIds([a, a with { Handle = 3, Uuid = "GPU-ccc" }], adapters).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a, a with { Handle = 3, Pci = b.Pci }], adapters).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a], [adapters[1], adapters[1]]).Count == 0,
            "duplicate PCI addresses, UUIDs and candidate identities reject ambiguous aliasing");
        var numericOnly = adapters.Select(adapter => adapter with { DomainKnown = false }).ToArray();
        check(NvidiaNvmlSensorProvider.MatchHardwareIds([a, b], numericOnly).Count == 2 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a, b], numericOnly, completeLhmIdentitySet: false).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a, b], [numericOnly[1]]).Count == 0 &&
              NvidiaNvmlSensorProvider.MatchHardwareIds([a with { Pci = pci with { Domain = 1 } }, b], numericOnly).Count == 0,
            "localized numeric identities merge only with complete matching single-segment sets, never partial or multi-segment sets");

        var now = DateTimeOffset.UnixEpoch;
        var api = new FixtureApi { Devices = [a] };
        var identityReader = new FixtureIdentities { Adapters = [adapters[1]] };
        using var provider = new NvidiaNvmlSensorProvider(api, identityReader, () => now);
        var first = provider.Capture();
        check(first.Count == 5 && first.All(sensor => sensor.HardwareType == "GpuNvidia" && sensor.HardwareId == "/nvml/gpu-aaa" && sensor.ReadStatus!.Contains("NVML")) &&
              first.Single(sensor => sensor.SensorType == "Load").Value == 0,
            "independent NVML enumeration needs no NVAPI or LHM and preserves a genuine zero GPU load");
        check(first.Single(sensor => sensor.Name == "GPU Memory Used").Value == 4096 && first.Single(sensor => sensor.Name == "GPU Memory Total").Value == 8192 &&
              first.Single(sensor => sensor.SensorType == "Clock").Value == 1800,
            "NVML memory bytes become MiB while graphics clocks remain MHz");
        first.Clear(); now = now.AddMilliseconds(999);
        check(provider.Capture().Count == 5 && api.TemperatureReads == 1 && api.Initializations == 1,
            "subsecond sampling returns a collection-safe snapshot without native calls or blocking sleeps");
        provider.SetLhmDeviceIds(new Dictionary<string, string> { ["/gpu-nvidia/1"] = pnp });
        var merged = provider.Capture();
        check(merged.Count == 5 && merged.All(sensor => sensor.HardwareId == "/gpu-nvidia/1" && sensor.Id.StartsWith("/nvml/gpu-aaa/", StringComparison.Ordinal)) &&
              identityReader.Reads == 1 && api.TemperatureReads == 2,
            "a changed PnP mapping replaces the subsecond cached identity immediately while preserving explicit NVML sensor origins");
        provider.SetLhmDeviceIds(new Dictionary<string, string> { ["/gpu-nvidia/1"] = pnp });
        provider.Capture();
        check(identityReader.Reads == 1, "semantically identical LHM identity updates do not force enumeration or bypass the sampling throttle");

        api.TemperatureResult = NvidiaNvmlResult.NotSupported; api.Usage = 37; api.ThrowClock = true;
        now = now.AddSeconds(1); var partial = provider.Capture();
        check(partial.Single(sensor => sensor.SensorType == "Temperature").Value is null && partial.Single(sensor => sensor.SensorType == "Temperature").ReadStatus!.Contains("NotSupported") &&
              partial.Single(sensor => sensor.SensorType == "Clock").Value is null && partial.Single(sensor => sensor.SensorType == "Load").Value == 37 &&
              partial.Single(sensor => sensor.Name == "GPU Memory Used").Value == 4096,
            "unsupported and throwing metrics clear their old readings without erasing independent valid channels");
        api.ThrowClock = false; api.Clock = double.NaN; api.MemoryUsed = api.MemoryTotal + 1;
        now = now.AddSeconds(1); var invalid = provider.Capture();
        check(invalid.Single(sensor => sensor.SensorType == "Clock").Value is null && invalid.Where(sensor => sensor.SensorType == "SmallData").All(sensor => sensor.Value is null),
            "nonfinite clocks and impossible used-versus-total memory are unavailable instead of fabricated readings");

        api.TemperatureResult = NvidiaNvmlResult.GpuLost;
        now = now.AddSeconds(1); var lost = provider.Capture();
        check(api.Resets == 1 && lost.Count == 5 && lost.All(sensor => sensor.Value is null && sensor.ReadStatus!.Contains("GpuLost")),
            "GPU loss invalidates the current device snapshot and resets the owned NVML session");
        now = now.AddSeconds(1);
        check(provider.Capture().All(sensor => sensor.Value is null) && api.Initializations == 1,
            "GPU-loss backoff keeps known descriptors explicitly unavailable and never replays old valid samples");
        api.TemperatureResult = NvidiaNvmlResult.Success; api.Clock = 1815; api.MemoryUsed = 0;
        now = now.AddSeconds(1); var recovered = provider.Capture();
        check(api.Initializations == 2 && recovered.Single(sensor => sensor.SensorType == "Temperature").Value == 64 && recovered.Single(sensor => sensor.Name == "GPU Memory Used").Value == 0,
            "a later eligible capture reinitializes and re-enumerates a recovered GPU, including genuine zero allocation");

        provider.NotifyResume(); api.InitializationResult = NvidiaNvmlResult.DriverNotLoaded;
        now = now.AddSeconds(1); var noDriver = provider.Capture();
        check(noDriver.Count == 5 && noDriver.All(sensor => sensor.Value is null && sensor.ReadStatus!.Contains("DriverNotLoaded")),
            "resume plus driver-unavailable initialization clears previously valid values while retaining diagnostics");
        var attempted = api.Initializations; now = now.AddSeconds(9); provider.Capture();
        check(api.Initializations == attempted, "failed NVML initialization is retried after a bounded backoff, not on every worker call");
        api.InitializationResult = NvidiaNvmlResult.Success; now = now.AddSeconds(1); provider.Capture();
        check(api.Initializations == attempted + 1, "initialization retries become eligible after ten seconds without sleeping or requiring a restart");
        api.CountResult = NvidiaNvmlResult.NoPermission; now = now.AddSeconds(31);
        check(provider.Capture().All(sensor => sensor.Value is null) && provider.Status.Contains("NoPermission"),
            "enumeration failure cannot leave previous values marked current");
        provider.Dispose(); provider.Dispose(); var disposed = false;
        try { provider.Capture(); } catch (ObjectDisposedException) { disposed = true; }
        check(api.Disposals == 1 && disposed, "NVML ownership is disposed exactly once and no later capture may access native delegates");

        using var replaced = new NvidiaNvmlSensorProvider(new FixtureApi { Devices = [b] }, new FixtureIdentities { Adapters = [adapters[0]] }, () => now);
        replaced.SetLhmDeviceIds(new Dictionary<string, string> { ["/gpu-nvidia/0"] = pnp });
        check(replaced.Capture().All(sensor => sensor.HardwareId == "/gpu-nvidia/0"), "a separate GPU has its own stable source and canonical identity");

        var transferApi = new FixtureApi { Devices = [a] };
        using var transfer = new NvidiaNvmlSensorProvider(transferApi, new FixtureIdentities { Adapters = [adapters[1]] }, () => now);
        transfer.SetLhmDeviceIds(new Dictionary<string, string> { ["/gpu-nvidia/1"] = pnp });
        var beforeTransfer = transfer.Capture();
        transfer.SetLhmDeviceIds(new Dictionary<string, string>());
        var afterTransfer = transfer.Capture();
        check(beforeTransfer.Count == 5 && beforeTransfer.All(sensor => sensor.HardwareId == "/gpu-nvidia/1") &&
              afterTransfer.Count == 5 && afterTransfer.All(sensor => sensor.HardwareId == "/nvml/gpu-aaa") && transferApi.TemperatureReads == 2,
            "removing an LHM mapping at the identical timestamp cannot return rows carrying its previous canonical alias");
        transfer.SetLhmDeviceIds(new Dictionary<string, string>());
        transfer.Capture();
        check(transferApi.TemperatureReads == 2,
            "an unchanged empty identity map retains the one-second cache instead of repeatedly invalidating it");
    }

    private sealed class FixtureIdentities : INvidiaLhmIdentityReader
    {
        public IReadOnlyList<NvidiaLhmPciAdapter> Adapters { get; init; } = [];
        public int Reads { get; private set; }
        public IReadOnlyList<NvidiaLhmPciAdapter> ReadAdapters(IReadOnlyDictionary<string, string> deviceIds) { Reads++; return Adapters; }
    }
    private sealed class FixtureApi : INvidiaNvmlApi
    {
        public NvidiaNvmlDevice[] Devices { get; set; } = [];
        public NvidiaNvmlResult InitializationResult { get; set; } = NvidiaNvmlResult.Success;
        public NvidiaNvmlResult CountResult { get; set; } = NvidiaNvmlResult.Success;
        public NvidiaNvmlResult TemperatureResult { get; set; } = NvidiaNvmlResult.Success;
        public double Usage { get; set; }
        public double Clock { get; set; } = 1800;
        public bool ThrowClock { get; set; }
        public ulong MemoryUsed { get; set; } = 4UL * 1024 * 1024 * 1024;
        public ulong MemoryTotal { get; set; } = 8UL * 1024 * 1024 * 1024;
        public int Initializations { get; private set; }
        public int TemperatureReads { get; private set; }
        public int Resets { get; private set; }
        public int Disposals { get; private set; }
        public NvidiaNvmlResult Initialize() { Initializations++; return InitializationResult; }
        public NvidiaNvmlResult GetDeviceCount(out uint count) { count = (uint)Devices.Length; return CountResult; }
        public NvidiaNvmlResult GetDevice(uint index, out NvidiaNvmlDevice? device) { device = Devices[(int)index]; return NvidiaNvmlResult.Success; }
        public NvidiaNvmlResult ReadTemperature(nint handle, out double value) { TemperatureReads++; value = 64; return TemperatureResult; }
        public NvidiaNvmlResult ReadUsage(nint handle, out double value) { value = Usage; return NvidiaNvmlResult.Success; }
        public NvidiaNvmlResult ReadGraphicsClock(nint handle, out double value) { value = Clock; if (ThrowClock) throw new IOException("fixture channel failed"); return NvidiaNvmlResult.Success; }
        public NvidiaNvmlResult ReadMemory(nint handle, out ulong used, out ulong total) { used = MemoryUsed; total = MemoryTotal; return NvidiaNvmlResult.Success; }
        public void Reset() => Resets++;
        public void Dispose() => Disposals++;
    }
}
