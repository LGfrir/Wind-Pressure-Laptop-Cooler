using System.Buffers.Binary;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Read-only DSTS endpoints validated on the ASUS G615LRG. No control commands exist here.</summary>
public static class AsusEcProtocol
{
    public const uint CpuTemperature = 0x00120094;
    public const uint CpuFan = 0x00110013;
    public const uint GpuFan = 0x00110014;
    public const uint MiddleFan = 0x00110031;
    public const string CpuTemperatureId = "/asus-ec/cpu/temperature";

    public static bool IsSupportedMachine(string manufacturer, string model) =>
        (manufacturer.Equals("ASUS", StringComparison.OrdinalIgnoreCase) || manufacturer.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase)) &&
        model.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Equals("G615LRG", StringComparison.OrdinalIgnoreCase));

    // Accept only the flag layout observed and documented for these scalar endpoints.
    public static bool HasSupportedValue(uint raw) => (raw & 0xFFFF0000) == 0x00010000;
    public static double? DecodeCpuTemperature(uint raw) => HasSupportedValue(raw) && (raw & 0xFFFF) is >= 1 and <= 125 ? raw & 0xFFFF : null;
    public static double? DecodeFanRpm(uint raw) => HasSupportedValue(raw) && (raw & 0xFFFF) <= 120 ? (raw & 0xFFFF) * 100 : null;
}

internal sealed class AsusEcSensorProvider : IDisposable
{
    private SafeFileHandle? _handle;
    private bool _machineChecked;
    private bool _supportedMachine;
    private DateTimeOffset _nextOpenAttempt;
    private string _model = "";
    private readonly HashSet<int> _knownFanSlots = [];
    public string Status { get; private set; } = "ASUS EC 尚未检查";

    public IReadOnlyList<SensorDescriptor> Capture()
    {
        try { return CaptureCore(); }
        catch (Exception ex)
        {
            Status = $"ASUS EC 采样失败：{ex.Message}；使用标准硬件监控";
            return [];
        }
    }

    private IReadOnlyList<SensorDescriptor> CaptureCore()
    {
        if (!_machineChecked)
        {
            _machineChecked = true;
            try
            {
                using var query = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                using var machines = query.Get();
                foreach (ManagementObject machine in machines)
                {
                    using (machine)
                    {
                        var manufacturer = machine["Manufacturer"]?.ToString() ?? "";
                        _model = machine["Model"]?.ToString() ?? "";
                        _supportedMachine = AsusEcProtocol.IsSupportedMachine(manufacturer, _model);
                    }
                    break;
                }
                Status = _supportedMachine ? $"ASUS EC 已匹配 {_model}" : "ASUS EC 不适用于此机型；使用标准硬件监控";
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { Status = $"ASUS 机型识别失败：{ex.Message}；使用标准硬件监控"; }
        }
        if (!_supportedMachine) return [];
        if (_handle is null || _handle.IsClosed || _handle.IsInvalid)
        {
            if (DateTimeOffset.UtcNow < _nextOpenAttempt) return [];
            _nextOpenAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            _handle?.Dispose();
            _handle = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                Status = $"ASUS EC 设备不可访问：{new Win32Exception(Marshal.GetLastWin32Error()).Message}；使用标准硬件监控";
                return [];
            }
        }
        var sensors = new List<SensorDescriptor>();
        var failures = new List<string>();
        void CaptureOne(uint endpoint, string id, string name, string type, string hardwareType, int? fanSlot = null)
        {
            var raw = ReadStatus(endpoint, out var error);
            if (error is not null) failures.Add(error);
            var supported = raw.HasValue && AsusEcProtocol.HasSupportedValue(raw.Value);
            var value = !supported ? null : fanSlot.HasValue ? AsusEcProtocol.DecodeFanRpm(raw!.Value) : AsusEcProtocol.DecodeCpuTemperature(raw!.Value);
            if (error is null && (!supported || !value.HasValue)) failures.Add($"0x{endpoint:X8}：未返回有效的已验证读数");
            if (supported && fanSlot.HasValue) _knownFanSlots.Add(fanSlot.Value);
            // An already observed installed fan remains present when a later query fails.
            if (!supported && !(fanSlot.HasValue && _knownFanSlots.Contains(fanSlot.Value))) return;
            sensors.Add(new SensorDescriptor
            {
                Id = id, Name = name, HardwareId = "/asus-ec", HardwareName = $"ASUS EC ({_model})",
                HardwareType = hardwareType, SensorType = type, FanSlot = fanSlot,
                Value = value
            });
        }
        CaptureOne(AsusEcProtocol.CpuTemperature, AsusEcProtocol.CpuTemperatureId, "ASUS EC CPU Temperature", "Temperature", "Cpu");
        CaptureOne(AsusEcProtocol.CpuFan, "/asus-ec/fan/0-cpu", "ASUS EC CPU Fan", "Fan", "EmbeddedController", 0);
        CaptureOne(AsusEcProtocol.GpuFan, "/asus-ec/fan/1-gpu", "ASUS EC GPU Fan", "Fan", "EmbeddedController", 1);
        CaptureOne(AsusEcProtocol.MiddleFan, "/asus-ec/fan/2-middle", "ASUS EC Middle Fan", "Fan", "EmbeddedController", 2);
        Status = failures.Count > 0 ? $"ASUS EC 部分读取失败：{string.Join("；", failures.Distinct())}"
            : sensors.Count > 0 ? "ASUS EC 只读采样正常（CPU 温度 / CPU、GPU、Middle 风扇）"
            : "ASUS EC 未返回已验证的传感器值；使用标准硬件监控";
        return sensors;
    }

    private uint? ReadStatus(uint endpoint, out string? error)
    {
        var input = new byte[16];
        var output = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 0x53545344); // DSTS: Get Device Status
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), endpoint);
        var success = DeviceIoControl(_handle!, 0x0022240C, input, (uint)input.Length, output, (uint)output.Length, out var returned, IntPtr.Zero);
        if (!success) { error = $"0x{endpoint:X8}：{new Win32Exception(Marshal.GetLastWin32Error()).Message}"; return null; }
        if (returned < 4) { error = $"0x{endpoint:X8}：回复不足 4 字节"; return null; }
        error = null;
        return BinaryPrimitives.ReadUInt32LittleEndian(output);
    }

    public void Dispose() => _handle?.Dispose();
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength, byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}
