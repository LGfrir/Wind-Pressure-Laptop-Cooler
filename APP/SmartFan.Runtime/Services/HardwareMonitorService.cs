using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

public sealed class HardwareMonitorService : SmartFan.Runtime.IHardwareSampler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _log;
    private readonly AsusEcSensorProvider _asusEc = new();
    private readonly WindowsCpuSensorProvider _windowsCpu = new();
    private readonly NvidiaNvmlSensorProvider _nvidia = new();
    private Computer? _computer;
    private bool _disposed;
    private string? _openError;
    private string? _cpuSamplingError;
    private HardwareMonitorDiagnostics? _environment;
    private DateTimeOffset _environmentCheckedAt;
    private string? _lastCpuDiagnostic;
    private DateTimeOffset _lastOpenAttempt;
    private DateTimeOffset? _lastCaptureAt;
    private readonly Dictionary<string, string> _samplingErrors = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _demoStarted = DateTimeOffset.Now;

    public HardwareMonitorService(Action<string>? log = null) => _log = log;

    public async Task<TelemetrySnapshot> ReadAsync(SensorPreferences? preferences = null, bool demo = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (demo) return CreateDemoSnapshot();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => ReadCore(preferences ?? new SensorPreferences()), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SensorDescriptor>> EnumerateSensorsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run<IReadOnlyList<SensorDescriptor>>(() => CaptureSensors(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private TelemetrySnapshot ReadCore(SensorPreferences preferences)
    {
        var sensors = CaptureSensors();
        var environment = _environment ?? throw new InvalidOperationException("硬件环境未初始化。");
        var cpu = sensors.Where(s => s.HardwareType == "Cpu").ToList();
        var gpuId = SensorSelection.SelectGpuId(sensors, preferences.GpuHardwareId);
        var gpu = sensors.Where(s => gpuId is not null && s.HardwareId == gpuId).ToList();
        var names = new Dictionary<string, string>();
        var cpuTemp = SensorSelection.SelectTemperature(cpu, true, preferences.CpuTemperatureSensorId);
        var gpuTemp = TelemetrySourceSelection.GpuTemperature(gpu, gpuId, preferences.GpuTemperatureSensorId);
        var cpuStatus = CpuTemperatureDiagnostics.Evaluate(cpu, cpuTemp, preferences.CpuTemperatureSensorId,
            environment, _openError, _cpuSamplingError);
        var diagnosticKey = $"{cpuStatus.Code}|{cpuStatus.SensorId}|{environment.IsAdministrator}|{environment.PawnIoVersion}|{environment.PawnIoDeviceErrorCode}";
        if (_lastCpuDiagnostic != diagnosticKey)
        {
            _lastCpuDiagnostic = diagnosticKey;
            _log?.Invoke($"CPU 温度采集 {cpuStatus.Code}：{cpuStatus.Message} " +
                $"[LHM {environment.ProviderVersion}；后台管理员={environment.IsAdministrator}；PawnIO={environment.PawnIoVersion ?? "未安装"}；驱动访问错误={environment.PawnIoDeviceErrorCode?.ToString() ?? "无"}]");
        }
        if (cpuTemp is not null) names["CPU 温度"] = $"{cpuTemp.HardwareName} / {cpuTemp.Name} [{cpuTemp.Id}]";
        if (gpuTemp is not null) names["GPU 温度"] = $"{gpuTemp.HardwareName} / {gpuTemp.Name} [{gpuTemp.Id}]";
        var cpuClockSensor = CpuClockSelection.Select(cpu);
        var cpuClockKind = cpuClockSensor is null ? CpuClockKind.Unknown : CpuClockKind.HardwareCore;
        if (cpuClockSensor is null)
        {
            cpuClockSensor = cpu.FirstOrDefault(s => s.Id == "/windows/cpu/clock/reported" && s.Value is > 0 && double.IsFinite(s.Value.Value));
            if (cpuClockSensor is not null) cpuClockKind = CpuClockKind.WindowsReported;
        }
        var cpuUsage = TelemetrySourceSelection.CpuUsage(cpu);
        var gpuUsage = TelemetrySourceSelection.GpuMetric(gpu, gpuId, "Load", "GPU Core", "D3D 3D", "GPU D3D 3D");
        var gpuClock = TelemetrySourceSelection.GpuMetric(gpu, gpuId, "Clock", "GPU Core", "GPU Graphics");
        var cpuVoltage = CpuVoltageSelection.Select(cpu);
        var cpuVoltageSensor = cpuVoltage?.Sensor;
        RegisterSource("CPU 使用率", cpuUsage);
        RegisterSource("CPU 频率", cpuClockSensor); RegisterSource("CPU 电压", cpuVoltageSensor);
        RegisterSource("GPU 使用率", gpuUsage);
        RegisterSource("GPU 频率", gpuClock);
        void RegisterSource(string label, SensorDescriptor? sensor) { if (sensor is not null) names[label] = $"{sensor.HardwareName} / {sensor.Name} [{sensor.Id}]"; }

        var fanSensors = SensorSelection.SelectFanSlots(sensors);
        var present = new double[5];
        var fanRpm = new double?[5];
        for (int i = 0; i < 5; i++)
        {
            var sensor = fanSensors[i];
            present[i] = preferences.ManualFanCount.HasValue ? (i < preferences.ManualFanCount.Value ? 1 : 0) : (sensor is not null ? 1 : 0);
            if (present[i] == 1 && sensor is not null)
            {
                fanRpm[i] = ValidNonnegative(sensor.Value);
                names[$"内置风扇 {i + 1}"] = $"{sensor.HardwareName} / {sensor.Name} [{sensor.Id}]";
            }
        }
        var nativeMemory = ReadPhysicalMemory();
        var memory = TelemetrySourceSelection.PhysicalMemory(nativeMemory.Used, nativeMemory.Total, sensors);
        names["系统内存"] = memory.Source;
        var gpuMemoryUsed = TelemetrySourceSelection.MemoryMiB(gpu, "GPU Memory Used", "GPU Memory Dedicated", "D3D Dedicated Memory Used");
        var gpuMemoryTotal = TelemetrySourceSelection.MemoryMiB(gpu, "GPU Memory Total");
        RegisterSource("GPU 显存使用", gpuMemoryUsed.Sensor); RegisterSource("GPU 显存总量", gpuMemoryTotal.Sensor);
        var missing = new List<string>();
        if (cpuTemp is null) missing.Add(cpuStatus.Message);
        if (gpuTemp is null) missing.Add("GPU 温度不可用");
        var status = missing.Count == 0 ? "实时硬件数据" : string.Join("；", missing);
        return new TelemetrySnapshot
        {
            Status = status, CpuName = cpu.FirstOrDefault()?.HardwareName ?? "CPU", GpuName = gpu.FirstOrDefault()?.HardwareName ?? "GPU 不可用",
            CpuTemperature = cpuTemp?.Value, GpuTemperature = gpuTemp?.Value,
            CpuTemperatureStatus = cpuStatus, HardwareMonitor = environment,
            VendorSensorStatus = _asusEc.Status,
            ProviderStatuses = new Dictionary<string, string>
            {
                ["LibreHardwareMonitor"] = _openError ?? (_samplingErrors.Count > 0 ? string.Join("；", _samplingErrors.Values.Take(3))
                    : "Intel / AMD CPU 与 NVIDIA 硬件源已启用；各项可用性以传感器读数为准"),
                ["Windows"] = _windowsCpu.Status,
                ["NVIDIA NVML"] = _nvidia.Status,
                ["厂商 EC"] = _asusEc.Status
            },
            CpuUsage = cpuUsage?.Value,
            GpuUsage = gpuUsage?.Value,
            CpuVoltage = cpuVoltageSensor?.Value, CpuClockMhz = cpuClockSensor?.Value,
            CpuVoltageKind = cpuVoltage?.Kind ?? CpuVoltageKind.Unknown,
            CpuClockKind = cpuClockKind,
            CpuVoltageReadStatus = cpuVoltage?.IsVid == true
                ? "CPU 核心 VID：有效核心请求电压的最大值，不是主板实测 VCore；仅供本机显示。"
                : cpuVoltageSensor is not null ? $"CPU 核心遥测电压：{cpuVoltageSensor.Name} [{cpuVoltageSensor.Id}]"
                : CpuMetricDiagnostics.Describe("电压", null, cpu.Count(s => s.SensorType == "Voltage"), environment),
            CpuClockReadStatus = cpuClockKind == CpuClockKind.WindowsReported
                ? cpuClockSensor!.ReadStatus ?? "Windows 报告频率；部分平台可能只报告标称或请求值，不代表瞬时硬件核心频率，仅供本机显示。"
                : cpuClockSensor is not null ? "硬件采集库各核心报告频率的最大值；库内单次 MSR 读取失败可能使用 TSC 回退，不等同于含空闲时间的有效频率。"
                : CpuMetricDiagnostics.Describe("核心频率", null, CpuClockSelection.CountCandidates(cpu), environment) + "；Windows 备援：" + _windowsCpu.Status,
            GpuClockMhz = gpuClock?.Value,
            RamUsedMiB = memory.Used, RamTotalMiB = memory.Total,
            GpuRamUsedMiB = gpuMemoryUsed.Value, GpuRamTotalMiB = gpuMemoryTotal.Value,
            FanPresent = present, FanRpm = fanRpm, FanSensorsAvailable = fanSensors.Any(s => s is not null),
            FanPresenceAvailable = preferences.ManualFanCount.HasValue || SensorSelection.HasReliableFanPresence(fanSensors),
            SensorNames = names
        };
    }

    private List<SensorDescriptor> CaptureSensors()
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastCaptureAt.HasValue && now - _lastCaptureAt.Value > TimeSpan.FromSeconds(10)) _nvidia.NotifyResume();
        _lastCaptureAt = now;
        if (_environment is null || DateTimeOffset.UtcNow - _environmentCheckedAt >= TimeSpan.FromSeconds(30))
        {
            _environment = HardwareMonitorEnvironment.Capture();
            _environmentCheckedAt = DateTimeOffset.UtcNow;
        }
        _cpuSamplingError = null;
        if (_computer is null && (_openError is null || DateTimeOffset.UtcNow - _lastOpenAttempt >= TimeSpan.FromSeconds(30)))
        {
            _lastOpenAttempt = DateTimeOffset.UtcNow;
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true };
            try { computer.Open(); _computer = computer; _openError = null; }
            catch (Exception ex)
            {
                _openError = $"硬件监控初始化失败：{ex.Message}";
                _log?.Invoke(_openError);
                try { computer.Close(); }
                catch (Exception closeError) { _log?.Invoke($"初始化失败后的监控清理异常：{closeError.Message}"); }
            }
        }
        var result = new List<SensorDescriptor>();
        if (_computer is not null) foreach (var hardware in _computer.Hardware)
        {
            CaptureHardware(hardware, result);
        }
        result.AddRange(_asusEc.Capture());
        result.AddRange(_windowsCpu.Capture());
        var gpuDeviceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_computer is not null)
            foreach (var gpu in _computer.Hardware.OfType<GenericGpu>().Where(g => g.HardwareType == HardwareType.GpuNvidia))
                if (!string.IsNullOrWhiteSpace(gpu.DeviceId)) gpuDeviceIds[gpu.Identifier.ToString()] = gpu.DeviceId;
        _nvidia.SetLhmDeviceIds(gpuDeviceIds);
        result.AddRange(_nvidia.Capture());
        return result;
    }

    private void CaptureHardware(IHardware hardware, List<SensorDescriptor> result)
    {
        if (hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel) return;
        var hardwareId = hardware.Identifier.ToString();
        try
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                var value = sensor.Value.HasValue && float.IsFinite(sensor.Value.Value) ? (double?)sensor.Value.Value : null;
                var reading = SensorReadPolicy.Normalize(hardware.Identifier.ToString(), sensor.SensorType.ToString(), value, _environment);
                result.Add(new SensorDescriptor
                {
                    ReadStatus = reading.Status,
                    Id = sensor.Identifier.ToString(), Name = sensor.Name, HardwareId = hardware.Identifier.ToString(),
                    HardwareName = hardware.Name, HardwareType = hardware.HardwareType.ToString(), SensorType = sensor.SensorType.ToString(), Value = reading.Value
                });
            }
            if (_samplingErrors.Remove(hardwareId)) _log?.Invoke($"传感器采样已恢复 {hardware.Name}");
        }
        catch (Exception ex)
        {
            var message = $"传感器采样失败 {hardware.Name}：{ex.Message}";
            if (hardware.HardwareType == HardwareType.Cpu) _cpuSamplingError = message;
            if (!_samplingErrors.TryGetValue(hardwareId, out var previous) || previous != message) _log?.Invoke(message);
            _samplingErrors[hardwareId] = message;
        }
        foreach (var child in hardware.SubHardware) CaptureHardware(child, result);
    }

    private static double? ValidNonnegative(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;

    private static (double? Used, double? Total) ReadPhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.AvailablePhysical > status.TotalPhysical) return (null, null);
        return ((status.TotalPhysical - status.AvailablePhysical) / 1048576d, status.TotalPhysical / 1048576d);
    }

    private TelemetrySnapshot CreateDemoSnapshot()
    {
        double t = (DateTimeOffset.Now - _demoStarted).TotalSeconds;
        return new TelemetrySnapshot
        {
            IsDemo = true, Status = "演示数据 · 不会上报或参与真实调速", CpuName = "演示 CPU", GpuName = "演示 GPU",
            CpuTemperature = 58 + 12 * Math.Sin(t / 12), GpuTemperature = 54 + 10 * Math.Cos(t / 15),
            CpuTemperatureStatus = new TemperatureReadStatus { Code = TemperatureReadCode.Available, Message = "演示数据，不代表真实 CPU 温度" },
            CpuUsage = 38 + 20 * Math.Sin(t / 5), GpuUsage = 52 + 30 * Math.Cos(t / 7),
            CpuVoltage = 1.12, CpuVoltageKind = CpuVoltageKind.VCore, CpuClockMhz = 4200, CpuClockKind = CpuClockKind.HardwareCore, GpuClockMhz = 1900,
            RamUsedMiB = 11800 + 400 * Math.Sin(t / 30), RamTotalMiB = 32768, GpuRamUsedMiB = 4200, GpuRamTotalMiB = 12288,
            FanPresent = [1, 1, 0, 0, 0], FanRpm = [2400 + 200 * Math.Sin(t / 8), 2600, null, null, null],
            FanSensorsAvailable = true, FanPresenceAvailable = true
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _asusEc.Dispose();
            _windowsCpu.Dispose();
            _nvidia.Dispose();
            if (_computer is not null)
            {
                await Task.Run(() => _computer.Close()).ConfigureAwait(false);
                _computer = null;
            }
        }
        finally { _gate.Release(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
