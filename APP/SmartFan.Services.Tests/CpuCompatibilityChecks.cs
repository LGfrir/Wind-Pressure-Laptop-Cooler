using SmartFan.App.Models;
using SmartFan.App.Services;

internal static class CpuCompatibilityChecks
{
    public static void Run(Action<bool, string> check)
    {
        var svi2 = Sensor("svi2", "Core (SVI2 TFN)", "Voltage", 1.08);
        var selected = CpuVoltageSelection.Select([
            Sensor("soc", "SoC (SVI2 TFN)", "Voltage", 1.25),
            Sensor("vid", "Core #1 VID", "Voltage", 1.35), svi2]);
        check(selected is { Kind: CpuVoltageKind.VCore, IsVid: false } && ReferenceEquals(selected.Sensor, svi2),
            "AMD core SVI2 supply telemetry is recognized and takes precedence over requested VID while excluding SoC");

        check(CpuVoltageSelection.Select([Sensor("legacy-vid", "CPU Cores", "Voltage", 1.25)]) is { Kind: CpuVoltageKind.Vid }
              && CpuVoltageSelection.Select([
                  Sensor("soc", "SoC (SVI2 TFN)", "Voltage", 1.1), Sensor("nb", "Northbridge", "Voltage", 1.1),
                  Sensor("foreign", "Core (SVI2 TFN)", "Voltage", 1.1, "/intelcpu/0")]) is null,
            "legacy AMD CPU Cores is VID and AMD supply aliases cannot select Northbridge, SoC or an unrelated Intel channel");

        var die = Sensor("die", "Core (Tdie)", "Temperature", 64);
        var ccdMax = Sensor("ccd-max", "CCDs Max (Tdie)", "Temperature", 89);
        var ccdAverage = Sensor("ccd-average", "CCDs Average (Tdie)", "Temperature", 80);
        check(ReferenceEquals(SensorSelection.SelectTemperature([ccdMax, ccdAverage, die], true), die)
              && SensorSelection.SelectTemperature([
                  Sensor("combined", "Core (Tctl/Tdie)", "Temperature", 65), ccdMax], true)?.Id == "combined",
            "AMD package Tdie or combined Tctl/Tdie remains authoritative instead of a hotter CCD or CCD average");

        check(ReferenceEquals(SensorSelection.SelectTemperature([ccdAverage, ccdMax], true), ccdMax)
              && SensorSelection.SelectTemperature([Sensor("ccd-one", "CCD1 (Tdie)", "Temperature", 68)], true)?.Value == 68,
            "CCD maximum and individual CCD temperature are explicit fallbacks when the AMD package sensor is unavailable");

        check(SensorSelection.SelectTemperature([Sensor("k10", "CPU Cores", "Temperature", 56)], true)?.Value == 56
              && SensorSelection.SelectTemperature([Sensor("k8", "Core #1", "Temperature", 52)], true)?.Value == 52
              && SensorSelection.SelectTemperature([
                  Sensor("vrm", "VRM Tdie", "Temperature", 99), Sensor("delta", "Core #1 Distance to TjMax", "Temperature", 30)], true) is null,
            "older AMD temperature names are covered while misleading Tdie names and delta temperatures remain excluded");

        var raw = Sensor("raw", "Core #1", "Clock", 4450);
        SensorDescriptor[] mixedClocks = [
            raw, Sensor("effective", "Core #2 (Effective)", "Clock", 4800),
            Sensor("average", "Cores (Average)", "Clock", 4600),
            Sensor("effective-average", "Cores (Average Effective)", "Clock", 4900),
            Sensor("bus", "Bus Speed", "Clock", 100), Sensor("missing", "Core #2", "Clock", null)];
        check(ReferenceEquals(CpuClockSelection.Select(mixedClocks), raw) && CpuClockSelection.CountCandidates(mixedClocks) == 2,
            "raw AMD core clock selection excludes effective and average clocks while counting unavailable raw candidates accurately");

        var intelFast = Sensor("p-core", "P-Core #2", "Clock", 5100, "/intelcpu/0");
        check(ReferenceEquals(CpuClockSelection.Select([
                Sensor("e-core", "E-Core #1", "Clock", 3800, "/intelcpu/0"),
                Sensor("old-core", "CPU Core #1", "Clock", 4900, "/intelcpu/0"), intelFast]), intelFast)
              && CpuClockSelection.Select([Sensor("single", "CPU Core", "Clock", 3000, "/intelcpu/0")])?.Value == 3000,
            "Intel hybrid, older numbered and single-core clock names retain their raw-clock meaning");

        check(CpuClockSelection.Select([
                Sensor("gpu", "Core #1", "Clock", 6000, "/gpu-nvidia/0", "GpuNvidia"),
                Sensor("fake-id", "Core #1", "Clock", 6000, "/mainboard/0"),
                Sensor("suffix", "CPU Core #1 Average", "Clock", 6000),
                Sensor("wrong-kind", "Core #1", "Load", 80),
                Sensor("nan", "Core #1", "Clock", double.NaN),
                Sensor("infinite", "Core #1", "Clock", double.PositiveInfinity),
                Sensor("negative", "Core #1", "Clock", -1), Sensor("zero", "Core #1", "Clock", 0)]) is null,
            "CPU clock candidates reject foreign hardware, aggregates, invalid values and zero instead of inventing a frequency");

        var firstClock = Sensor("a-clock", "Core #1", "Clock", 4200);
        SensorDescriptor[] tiedClocks = [Sensor("second-package", "Core #1", "Clock", 5700, "/amdcpu/1"),
            Sensor("z-clock", "Core #2", "Clock", 4200), firstClock];
        var initialOrder = tiedClocks.ToArray();
        check(ReferenceEquals(CpuClockSelection.Select(tiedClocks), firstClock) && tiedClocks.SequenceEqual(initialOrder),
            "raw clocks stay within a deterministic CPU package and resolve equal values without mutating the source list");

        var missingDriver = new HardwareMonitorDiagnostics { PawnIoInstalled = false, PawnIoDeviceAccessible = false };
        var blockedDriver = new HardwareMonitorDiagnostics { PawnIoInstalled = true, PawnIoDeviceAccessible = false };
        check(new[] { "Temperature", "Clock", "Power", "Voltage" }.All(type =>
                  SensorReadPolicy.Normalize("/amdcpu/0", type, 1.55, missingDriver) is { Value: null, Status: not null })
              && SensorReadPolicy.Normalize("/amdcpu/0", "Voltage", 1.55, blockedDriver).Value is null,
            "AMD MSR-derived defaults are unavailable when PawnIO is absent or inaccessible, including plausible-looking VID values");

        check(SensorReadPolicy.Normalize("/amdcpu/0", "Load", 0, missingDriver).Value == 0
              && SensorReadPolicy.Normalize("/asus-ec/cpu", "Temperature", 74, missingDriver).Value == 74
              && SensorReadPolicy.Normalize("/gpu-nvidia/0", "Clock", 1900, missingDriver).Value == 1900
              && SensorReadPolicy.Normalize("/amdcpu/0", "Power", 0, new() { PawnIoDeviceAccessible = true }).Value == 0,
            "AMD driver protection preserves OS load, independent EC and NVIDIA channels, plus genuine hardware zero readings");

        SensorDescriptor[] adapters = [Sensor("intel", "GPU Core", "Temperature", 55, "intel", "GpuIntel"),
            Sensor("amd", "GPU Core", "Temperature", 65, "amd", "GpuAmd"),
            Sensor("nv-two", "GPU Core", "Temperature", 68, "nv2", "GpuNvidia"),
            Sensor("nv-one", "GPU Core", "Temperature", 70, "nv1", "GpuNvidia")];
        check(SensorSelection.SelectGpuId(adapters, null) == "nv1"
              && SensorSelection.SelectGpuId(adapters, "nv2") == "nv2"
              && SensorSelection.SelectGpuId(adapters, "intel") is null
              && SensorSelection.SelectGpuId(adapters, "missing") is null
              && SensorSelection.SelectGpuId(adapters.Take(2), null) is null,
            "NVIDIA-only GPU selection honors an explicit NVIDIA adapter and never silently replaces an unsupported or missing preference");
    }

    private static SensorDescriptor Sensor(string id, string name, string type, double? value,
        string hardwareId = "/amdcpu/0", string hardwareType = "Cpu") =>
        new() { Id = id, Name = name, SensorType = type, Value = value, HardwareId = hardwareId,
            HardwareType = hardwareType, HardwareName = "Synthetic compatibility fixture" };
}
