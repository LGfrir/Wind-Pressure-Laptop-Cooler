using System.Text.Json;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.Core.Protocol;

internal static class TelemetryFallbackChecks
{
    public static void Run(Action<bool, string> check)
    {
        SensorDescriptor Sensor(string id, string hw, string kind, string type, string name, double? value) => new()
        { Id = id, HardwareId = hw, HardwareType = kind, SensorType = type, Name = name, HardwareName = hw, Value = value };
        var windows = Sensor("/windows/cpu/load/total", "/windows/cpu", "Cpu", "Load", "CPU Total", 14);
        var missing = Sensor("/intelcpu/0/load/0", "/intelcpu/0", "Cpu", "Load", "CPU Total", null);
        var healthy = Sensor(missing.Id, missing.HardwareId, "Cpu", "Load", "CPU Total", 23);
        check(TelemetrySourceSelection.CpuUsage([windows, missing]) == windows, "Windows CPU load survives unavailable hardware reads");
        check(TelemetrySourceSelection.CpuUsage([windows, healthy]) == healthy, "Healthy hardware load keeps priority regardless of input order");
        check(TelemetrySourceSelection.CpuUsage([Sensor(healthy.Id, healthy.HardwareId, "Cpu", "Load", "CPU Total", 101), windows]) == windows,
            "Out-of-range usage cannot suppress a valid Windows fallback");
        var nv0 = Sensor("/gpu-nvidia/0/load/0", "/gpu-nvidia/0", "GpuNvidia", "Load", "GPU Core", null);
        var nvml0 = Sensor("/nvml/GPU-A/load/core", nv0.HardwareId, "GpuNvidia", "Load", "GPU Core", 0);
        var nv1 = Sensor("/gpu-nvidia/1/load/0", "/gpu-nvidia/1", "GpuNvidia", "Load", "GPU Core", 88);
        check(TelemetrySourceSelection.GpuMetric([nv1, nvml0, nv0], nv0.HardwareId, "Load", "GPU Core") == nvml0,
            "NVML supplies valid idle zero only within the strongly matched adapter");
        check(TelemetrySourceSelection.GpuMetric([nv1, nv0], nv0.HardwareId, "Load", "GPU Core") is null,
            "A second GPU's nonzero reading never fills a missing selected GPU");
        check(TelemetrySourceSelection.GpuMetric([nv1, nvml0], null, "Load", "GPU Core") is null,
            "No selected physical GPU means no opportunistic metric merge");
        var temperature = Sensor("/gpu-nvidia/0/temperature/0", nv0.HardwareId, "GpuNvidia", "Temperature", "GPU Core", 61);
        var backupTemperature = Sensor("/nvml/GPU-A/temperature/core", nv0.HardwareId, "GpuNvidia", "Temperature", "GPU Core", 63);
        check(TelemetrySourceSelection.GpuTemperature([backupTemperature, temperature], nv0.HardwareId, null) == backupTemperature,
            "An explicitly successful NVML core temperature takes priority after physical identity is proven");
        check(TelemetrySourceSelection.GpuTemperature([backupTemperature], nv0.HardwareId, temperature.Id) is null,
            "An unavailable manually selected GPU thermometer is not silently replaced by another channel");
        check(TelemetrySourceSelection.GpuTemperature([backupTemperature, temperature], nv0.HardwareId, temperature.Id) == temperature,
            "A valid explicit GPU thermometer choice still overrides the automatic channel policy");
        var memory = new[]
        {
            Sensor("/ram/data/0", "/ram", "Memory", "Data", "Memory Used", 12),
            Sensor("/ram/data/1", "/ram", "Memory", "Data", "Memory Available", 20)
        };
        var native = TelemetrySourceSelection.PhysicalMemory(100, 200, memory);
        check(native.Used == 100 && native.Total == 200 && native.Source.Contains("GlobalMemoryStatusEx"), "Windows physical-memory source stays primary");
        var backup = TelemetrySourceSelection.PhysicalMemory(null, null, memory);
        check(backup.Used == 12288 && backup.Total == 32768, "Hardware-memory fallback converts GiB to MiB exactly once");
        check(TelemetrySourceSelection.PhysicalMemory(201, 200, memory).Total == 32768,
            "An inconsistent native memory result is rejected before fallback");
        check(TelemetrySourceSelection.PhysicalMemory(null, null,
            [memory[0], Sensor("/other/data/1", "/other", "Memory", "Data", "Memory Available", 20)]).Total is null,
            "Memory used and available cannot combine different hardware objects");
        var settings = new TelemetrySettings();
        settings.EnabledItems["laptop_cpu_clock"] = true;
        settings.EnabledItems["laptop_cpu_usage"] = true;
        var estimate = new TelemetrySnapshot
        {
            CpuClockMhz = 3200, CpuClockKind = CpuClockKind.WindowsReported, CpuUsage = 14,
            ProviderStatuses = new Dictionary<string, string> { ["Windows"] = "PDH reported frequency" }
        };
        var payload = TelemetryUploader.BuildPayloads(estimate, settings, out var notes);
        check(!payload.ContainsKey(CharacteristicKey.LaptopCpuClock) && payload.ContainsKey(CharacteristicKey.LaptopCpuUsage) && notes.Any(n => n.Contains("报告频率")),
            "OS estimates remain local while independently valid CPU load can still be uploaded");
        var restored = JsonSerializer.Deserialize<TelemetrySnapshot>(JsonSerializer.Serialize(estimate))!;
        check(restored.CpuClockKind == CpuClockKind.WindowsReported && restored.ProviderStatuses["Windows"] == "PDH reported frequency",
            "Clock semantics and per-provider diagnostics survive the backend IPC JSON round trip");
        check(TelemetryUploader.BuildPayloads(new TelemetrySnapshot { CpuClockMhz = 4200, CpuClockKind = CpuClockKind.HardwareCore }, settings, out _)
            .ContainsKey(CharacteristicKey.LaptopCpuClock), "Real hardware core clock retains the existing telemetry contract");
    }
}
