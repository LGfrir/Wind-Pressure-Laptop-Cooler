using SmartFan.App.Models;
using SmartFan.App.Services;

internal static class CpuVoltageSelectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var core = Sensor("core", "CPU VCore", 1.05);
        var vid = Sensor("vid", "P-Core #1", 1.35);
        var selected = CpuVoltageSelection.Select([vid, core]);
        check(selected is { Kind: CpuVoltageKind.VCore, IsVid: false } && ReferenceEquals(selected.Sensor, core),
            "an explicitly named CPU VCore reading takes precedence over a higher per-core VID without losing its voltage kind");

        check(CpuVoltageSelection.Select([Sensor("intel-generic", "CPU Core", 1.1)]) is { Kind: CpuVoltageKind.Vid, IsVid: true },
            "Intel's generic CPU Core sensor is classified as VID because its MSR bits do not establish measured supply voltage");

        var vcore = Sensor("vcore", "  cpu   vcore  ", 1.11);
        check(ReferenceEquals(CpuVoltageSelection.Select([Sensor("soc", "SoC", 1.3), vcore])?.Sensor, vcore)
              && CpuVoltageSelection.Select([Sensor("alias", "vCoRe", 1.2)])?.Kind == CpuVoltageKind.VCore,
            "explicit VCore aliases tolerate case and whitespace while avoiding unrelated CPU rails");

        var maximumVid = Sensor("p2", "P-Core #2", 1.23);
        selected = CpuVoltageSelection.Select([Sensor("p1", "P-Core #1", .98), Sensor("e1", "E-Core #1", 1.1), maximumVid]);
        check(selected is { IsVid: true, Kind: CpuVoltageKind.Vid } && ReferenceEquals(selected.Sensor, maximumVid),
            "hybrid P-core and E-core voltage names select the largest positive VID and identify it as requested voltage");

        check(new[] { "Core #1", "CPU Core #1", "CPUCore#0", "P-Core #3 VID", "E-Core #2 (VID)", "CPU VID", "CPU Core VID", "Core VID", "VID" }
                .All(name => CpuVoltageSelection.Select([Sensor("named", name, 1.1)]) is { IsVid: true, Kind: CpuVoltageKind.Vid }),
            "numbered legacy core names and explicit VID aliases never become measured VCore");

        check(CpuVoltageSelection.Select([
            Sensor("gpu", "CPU Core", 1.4, "GpuNvidia", "/nvidiagpu/0"),
            Sensor("fake-cpu", "VCore", 1.3, "Cpu", "/nvidiagpu/0"),
            Sensor("board", "VCore", 1.2, "SuperIO", "/lpc/nct6798d"),
            Sensor("fake-type", "CPU Core", 1.1, "GpuIntel", "/intelcpu/0")]) is null,
            "both CPU hardware type and known CPU package identity are required so GPU or board voltages cannot leak into CPU readings");

        check(CpuVoltageSelection.Select(new[] { "Bus Speed", "SoC", "VDDIO", "CPU Package", "CPU AUX", "P-Core #1 Bus", "Core Max", "Core VID Average" }
            .Select((name, index) => Sensor($"rail-{index}", name, 1.1)).ToArray()) is null,
            "bus, SoC and other voltage rails cannot pass the anchored CPU-core voltage whitelist");

        check(CpuVoltageSelection.Select(new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity }
            .Select((value, index) => Sensor($"invalid-{index}", index % 2 == 0 ? "CPU Core" : "P-Core #1", value)).ToArray()) is null,
            "missing, zero, negative and nonfinite CPU voltages remain unavailable rather than becoming fabricated readings");

        var packageZeroVid = Sensor("package0", "E-Core #1", 1.2);
        var packageOneCore = Sensor("package1", "CPU Core", 1.4, hardwareId: "/intelcpu/1");
        selected = CpuVoltageSelection.Select([packageOneCore, packageZeroVid]);
        check(selected is { IsVid: true } && ReferenceEquals(selected.Sensor, packageZeroVid),
            "selection stays within a stable single CPU package instead of mixing another package's VCore into its VID result");

        var firstTie = Sensor("a-core", "P-Core #1", 1.2); var lastTie = Sensor("z-core", "E-Core #1", 1.2);
        SensorDescriptor[] original = [lastTie, firstTie]; var order = original.ToArray();
        check(ReferenceEquals(CpuVoltageSelection.Select(original)?.Sensor, firstTie) && original.SequenceEqual(order),
            "equal VID readings have a deterministic identity tie-break and selection never mutates the source list");

        var amd = Sensor("amd", "VCore", 1.15, hardwareId: "/amdcpu/0");
        check(ReferenceEquals(CpuVoltageSelection.Select([amd])?.Sensor, amd)
              && CpuVoltageSelection.Select([new() { Name = "CPU Core", HardwareId = "/intelcpu/0", HardwareType = "Cpu", SensorType = "Power", Value = 1.1 }]) is null,
            "recognized AMD CPU packages are supported while a similarly named non-voltage sensor remains excluded");
    }

    private static SensorDescriptor Sensor(string id, string name, double? value, string hardwareType = "Cpu", string hardwareId = "/intelcpu/0") =>
        new() { Id = id, Name = name, HardwareId = hardwareId, HardwareName = "Synthetic CPU", HardwareType = hardwareType, SensorType = "Voltage", Value = value };
}
