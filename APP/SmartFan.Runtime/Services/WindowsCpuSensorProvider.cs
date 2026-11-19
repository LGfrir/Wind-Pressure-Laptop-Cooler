using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Raw Windows provider readings. ReportedFrequencyMhz is NOT a verified nominal reference.</summary>
public readonly record struct WindowsCpuPerformanceReading(string InstanceName, double? ReportedFrequencyMhz, double? PerformancePercent);

public readonly record struct WindowsCpuSystemTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>A sampling seam: fixtures can exercise selection and lifetime without calling Windows.</summary>
public sealed record WindowsCpuCounterSample
{
    public double? TotalUsagePercent { get; init; }
    public IReadOnlyList<WindowsCpuPerformanceReading> Processors { get; init; } = [];
    public uint ActiveProcessorCount { get; init; }
    public string UsageStatus { get; init; } = "Windows CPU 使用率尚无有效采样";
    public string ClockStatus { get; init; } = "Windows CPU 频率尚无有效采样";
}

public interface IWindowsCpuCounterSource : IDisposable
{
    WindowsCpuCounterSample Capture();
}

/// <summary>Pure validation shared by native sampling and offline regression fixtures.</summary>
public static class WindowsCpuSampling
{
    public static bool IsCounterStatusValid(uint status) => status is 0 or 1;
    public static bool CanUseSystemTimesForWholeMachine(uint activeProcessors) => activeProcessors is > 0 and <= 64;
    public static double? ValidateUsage(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;

    public static double? CalculateUsage(WindowsCpuSystemTimes previous, WindowsCpuSystemTimes current)
    {
        // GetSystemTimes kernel time already includes idle time; summing idle again is incorrect.
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var total = (double)(current.Kernel - previous.Kernel) + (current.User - previous.User);
        var idle = (double)(current.Idle - previous.Idle);
        return total > 0 && idle <= total ? ValidateUsage(100 * (total - idle) / total) : null;
    }

    public static bool IsLogicalProcessorInstance(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var parts = name.Split(',');
        return parts.Length == 2 && uint.TryParse(parts[0], out _) && uint.TryParse(parts[1], out _);
    }

    public static double? ReportedFrequency(IReadOnlyList<WindowsCpuPerformanceReading> readings, uint activeProcessors, out string reason)
    {
        // No documented user-mode contract establishes that PDH "Processor Frequency" is
        // the nominal MHz corresponding to "% Processor Performance". Multiplication could
        // apply a performance ratio to an already-current frequency for a second time.
        // PEP also distinguishes frequency feedback from platform-specific performance units:
        // https://learn.microsoft.com/windows-hardware/drivers/ddi/pep_x/ns-pep_x-_pep_processor_feedback_counter
        // Return only the provider's original MHz values, with their limited reported semantics.
        var logical = readings.Where(r => IsLogicalProcessorInstance(r.InstanceName)).ToArray();
        if (activeProcessors == 0 || logical.Length != activeProcessors ||
            logical.Select(r => r.InstanceName).Distinct(StringComparer.Ordinal).Count() != logical.Length)
        {
            reason = "Windows 频率计数器未覆盖全部逻辑处理器；不返回部分 CPU 的平均值";
            return null;
        }
        var sum = 0d;
        foreach (var reading in logical)
        {
            if (reading.ReportedFrequencyMhz is not > 0 || !double.IsFinite(reading.ReportedFrequencyMhz.Value) ||
                !double.IsFinite(sum += reading.ReportedFrequencyMhz.Value))
            {
                reason = "Windows 报告频率无效或部分逻辑处理器缺少读数；不使用零值或部分 CPU 的平均值";
                return null;
            }
        }
        reason = WindowsCpuSensorProvider.ReportedClockStatus;
        return sum / logical.Length;
    }

    public static string DescribeRawFrequencyCounters(IReadOnlyList<WindowsCpuPerformanceReading> readings)
    {
        var logical = readings.Where(r => IsLogicalProcessorInstance(r.InstanceName)).ToArray();
        if (logical.Length == 0) return "Windows PDH 尚无逻辑处理器原始频率计数器";
        static string Range(IEnumerable<double?> candidates, int total, string unit)
        {
            var valid = candidates.Where(v => v.HasValue && double.IsFinite(v.Value) && v.Value >= 0).Select(v => v!.Value).ToArray();
            if (valid.Length == 0) return $"不可用（0/{total} 有效）";
            var minimum = valid.Min(); var maximum = valid.Max();
            var range = minimum == maximum ? FormattableString.Invariant($"{minimum:0.##}") : FormattableString.Invariant($"{minimum:0.##}–{maximum:0.##}");
            return $"{range} {unit}（{valid.Length}/{total} 有效）";
        }
        return $"PDH 原始 Processor Frequency={Range(logical.Select(r => r.ReportedFrequencyMhz), logical.Length, "MHz")}；% Processor Performance={Range(logical.Select(r => r.PerformancePercent), logical.Length, "%")}。两者不能据此相乘得到实时核心频率";
    }
}

/// <summary>Read-only, non-sleeping Windows fallback. It does not load a kernel driver or change power settings.</summary>
public sealed class WindowsCpuSensorProvider : IDisposable
{
    public const string HardwareId = "/windows/cpu";
    public const string UsageId = HardwareId + "/load/total";
    public const string ReportedClockId = HardwareId + "/clock/reported";
    public const string ReportedClockName = "Windows CPU Reported Frequency";
    public const string ReportedClockStatus = "Windows 报告频率；部分平台可能只报告标称/请求值，不代表瞬时硬件核心频率，仅供本机显示";
    private readonly object _gate = new();
    private readonly IWindowsCpuCounterSource _source;
    private readonly TimeProvider _time;
    private readonly string _cpuName;
    private IReadOnlyList<SensorDescriptor> _cached = [];
    private long? _lastCapture;
    private bool _disposed;
    public string Status { get; private set; } = "Windows CPU 尚未采样";
    /// <summary>Complete last raw provider interval for diagnostics; never a hardware clock measurement.</summary>
    public IReadOnlyList<WindowsCpuPerformanceReading> RawFrequencyCounters { get; private set; } = [];

    public WindowsCpuSensorProvider() : this(new NativeCounterSource()) { }

    public WindowsCpuSensorProvider(IWindowsCpuCounterSource source, TimeProvider? timeProvider = null, string? cpuName = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = timeProvider ?? TimeProvider.System;
        _cpuName = string.IsNullOrWhiteSpace(cpuName) ? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows CPU" : cpuName;
    }

    public IReadOnlyList<SensorDescriptor> Capture()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetTimestamp();
            if (_lastCapture.HasValue && _time.GetElapsedTime(_lastCapture.Value, now) < TimeSpan.FromSeconds(1)) return _cached;
            _lastCapture = now;
            WindowsCpuCounterSample sample;
            try { sample = _source.Capture(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sample = new() { UsageStatus = $"Windows CPU 采样失败：{ex.Message}", ClockStatus = $"Windows CPU 采样失败：{ex.Message}" };
            }
            var usage = WindowsCpuSampling.ValidateUsage(sample.TotalUsagePercent);
            RawFrequencyCounters = Array.AsReadOnly(sample.Processors.ToArray());
            var clock = WindowsCpuSampling.ReportedFrequency(sample.Processors, sample.ActiveProcessorCount, out var clockReason);
            var usageReason = usage.HasValue ? sample.UsageStatus : sample.TotalUsagePercent.HasValue
                ? "Windows CPU 使用率返回无效值" : sample.UsageStatus;
            if (!clock.HasValue && !string.IsNullOrWhiteSpace(sample.ClockStatus)) clockReason = sample.ClockStatus + "；" + clockReason;
            _cached = Array.AsReadOnly(new[]
            {
                Describe(UsageId, "CPU Total", "Load", usage, usageReason),
                Describe(ReportedClockId, ReportedClockName, "Clock", clock, clockReason)
            });
            Status = $"{usageReason}；{clockReason}；{WindowsCpuSampling.DescribeRawFrequencyCounters(RawFrequencyCounters)}";
            return _cached;
        }
    }

    private SensorDescriptor Describe(string id, string name, string type, double? value, string reason) => new()
    {
        Id = id, Name = name, HardwareId = HardwareId, HardwareName = _cpuName,
        HardwareType = "Cpu", SensorType = type, Value = value, ReadStatus = reason
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cached = [];
            RawFrequencyCounters = [];
            _source.Dispose();
            Status = "Windows CPU 采样已释放";
        }
    }

    private sealed class NativeCounterSource : IWindowsCpuCounterSource
    {
        private const uint MoreData = 0x800007D2;
        private const uint DoubleFormat = 0x00000200;
        private const uint NoCap100 = 0x00008000;
        private SafePdhQueryHandle? _query;
        private IntPtr _usage, _frequency, _performance;
        private string _queryStatus = "Windows PDH 尚未初始化";
        private string _usageSetupStatus = "", _clockSetupStatus = "";
        private long _nextOpen;
        private long? _lastSample;
        private bool _collected;
        private WindowsCpuSystemTimes? _previousTimes;
        private uint _previousProcessorCount;

        public WindowsCpuCounterSample Capture()
        {
            if (!OperatingSystem.IsWindows()) return new() { UsageStatus = "Windows CPU 采样仅支持 Windows", ClockStatus = "Windows CPU 采样仅支持 Windows" };
            var now = Stopwatch.GetTimestamp();
            var activeProcessors = GetActiveProcessorCount(0xFFFF); // ALL_PROCESSOR_GROUPS, including >64 LP machines.
            if (_lastSample.HasValue && (Stopwatch.GetElapsedTime(_lastSample.Value, now) > TimeSpan.FromSeconds(10) || activeProcessors != _previousProcessorCount))
            {
                _collected = false;
                _previousTimes = null;
            }
            _lastSample = now;
            _previousProcessorCount = activeProcessors;
            var nativeUsage = ReadSystemUsage(activeProcessors, out var systemStatus);
            EnsureQuery(now);
            double? usage = null;
            var usageStatus = _queryStatus;
            var clockStatus = _queryStatus;
            IReadOnlyList<WindowsCpuPerformanceReading> processors = [];
            if (_query is { IsInvalid: false, IsClosed: false })
            {
                var collect = PdhCollectQueryData(_query);
                if (collect != 0)
                {
                    usageStatus = clockStatus = "Windows PDH 采样失败：" + Error(collect);
                    CloseQuery();
                    _nextOpen = now + 30 * Stopwatch.Frequency;
                    _queryStatus = usageStatus;
                }
                else if (!_collected)
                {
                    _collected = true;
                    usageStatus = "Windows PDH 正在预热，等待下一次区间采样";
                    clockStatus = string.IsNullOrEmpty(_clockSetupStatus) ? usageStatus : _clockSetupStatus;
                }
                else
                {
                    usage = ReadValue(_usage, DoubleFormat, out usageStatus);
                    if (_usage == IntPtr.Zero) usageStatus = _usageSetupStatus;
                    if (_frequency == IntPtr.Zero) clockStatus = _clockSetupStatus;
                    else
                    {
                        var frequencies = ReadArray(_frequency, DoubleFormat, out var frequencyStatus);
                        // Turbo performance is allowed above 100; only this counter disables the cap.
                        var performanceStatus = "Windows PDH 性能百分比诊断计数器不可用";
                        var performance = _performance == IntPtr.Zero ? new Dictionary<string, double?>(StringComparer.Ordinal)
                            : ReadArray(_performance, DoubleFormat | NoCap100, out performanceStatus);
                        processors = frequencies.Select(pair => new WindowsCpuPerformanceReading(pair.Key, pair.Value,
                            performance.TryGetValue(pair.Key, out var percent) ? percent : null)).ToArray();
                        clockStatus = string.Join("；", new[] { frequencyStatus, performanceStatus }.Where(s => s.Length > 0));
                    }
                }
            }
            usage = WindowsCpuSampling.ValidateUsage(usage);
            if (usage.HasValue) usageStatus = "Windows PDH：CPU 忙碌时间百分比（非按性能加权的 Processor Utility）";
            else if (nativeUsage.HasValue) { usage = nativeUsage; usageStatus = "Windows GetSystemTimes：全机 CPU 忙碌时间百分比"; }
            else usageStatus = string.Join("；", new[] { usageStatus, systemStatus }.Where(s => s.Length > 0));
            return new() { TotalUsagePercent = usage, ActiveProcessorCount = activeProcessors, Processors = processors, UsageStatus = usageStatus, ClockStatus = clockStatus };
        }

        private double? ReadSystemUsage(uint count, out string status)
        {
            if (!WindowsCpuSampling.CanUseSystemTimesForWholeMachine(count))
            {
                _previousTimes = null;
                status = count == 0 ? "无法确认逻辑处理器数量" : "超过 64 个逻辑处理器，GetSystemTimes 只覆盖调用线程所在组，未作为全机备援";
                return null;
            }
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _previousTimes = null;
                status = $"Windows GetSystemTimes 读取失败（0x{Marshal.GetLastWin32Error():X8}）";
                return null;
            }
            var current = new WindowsCpuSystemTimes(idle.Value, kernel.Value, user.Value);
            var previous = _previousTimes;
            _previousTimes = current;
            status = previous.HasValue ? "Windows CPU 使用率区间无效，等待下一次采样" : "Windows CPU 使用率正在预热";
            return previous.HasValue ? WindowsCpuSampling.CalculateUsage(previous.Value, current) : null;
        }

        private void EnsureQuery(long now)
        {
            if (_query is { IsInvalid: false, IsClosed: false })
            {
                // A missing optional performance diagnostic never interrupts valid reported MHz.
                if ((_usage != IntPtr.Zero && _frequency != IntPtr.Zero) || now < _nextOpen) return;
                CloseQuery();
            }
            if (now < _nextOpen) return;
            _nextOpen = now + 30 * Stopwatch.Frequency;
            var result = PdhOpenQuery(null, UIntPtr.Zero, out var query);
            if (result != 0) { query?.Dispose(); _queryStatus = "Windows PDH 初始化失败：" + Error(result); return; }
            _query = query;
            var loadResult = PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Time", UIntPtr.Zero, out _usage);
            var frequencyResult = PdhAddEnglishCounter(query, @"\Processor Information(*)\Processor Frequency", UIntPtr.Zero, out _frequency);
            var performanceResult = PdhAddEnglishCounter(query, @"\Processor Information(*)\% Processor Performance", UIntPtr.Zero, out _performance);
            if (loadResult != 0) _usage = IntPtr.Zero;
            if (frequencyResult != 0) _frequency = IntPtr.Zero;
            if (performanceResult != 0) _performance = IntPtr.Zero;
            _usageSetupStatus = loadResult == 0 ? "" : "Windows CPU 使用率计数器不可用：" + Error(loadResult);
            _clockSetupStatus = frequencyResult == 0 ? "" : "Windows CPU 报告频率计数器不可用：" + Error(frequencyResult);
            _queryStatus = "Windows PDH 等待采样";
            _collected = false;
            if (_usage == IntPtr.Zero && _frequency == IntPtr.Zero && _performance == IntPtr.Zero)
            {
                _queryStatus = string.Join("；", new[] { _usageSetupStatus, _clockSetupStatus }.Where(s => s.Length > 0));
                CloseQuery();
            }
        }

        private static double? ReadValue(IntPtr counter, uint flags, out string status)
        {
            if (counter == IntPtr.Zero) { status = "Windows PDH 计数器不可用"; return null; }
            var result = PdhGetFormattedCounterValue(counter, flags, out _, out var value);
            if (result != 0 || !WindowsCpuSampling.IsCounterStatusValid(value.Status) || !double.IsFinite(value.DoubleValue))
            { status = $"Windows PDH 无有效区间数据：{Error(result != 0 ? result : value.Status)}"; return null; }
            status = "";
            return value.DoubleValue;
        }

        private static Dictionary<string, double?> ReadArray(IntPtr counter, uint flags, out string status)
        {
            var values = new Dictionary<string, double?>(StringComparer.Ordinal);
            uint bytes = 0;
            var result = PdhGetFormattedCounterArray(counter, flags, ref bytes, out _, IntPtr.Zero);
            for (var attempt = 0; result == MoreData && attempt < 3; attempt++)
            {
                if (bytes == 0 || bytes > 4 * 1024 * 1024) break;
                var buffer = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    var capacity = bytes;
                    result = PdhGetFormattedCounterArray(counter, flags, ref capacity, out var count, buffer);
                    if (result == 0)
                    {
                        var size = Marshal.SizeOf<FormattedItem>();
                        if ((ulong)count * (uint)size > bytes) { status = "Windows PDH 返回的数组长度无效"; return values; }
                        for (var index = 0; index < count; index++)
                        {
                            var item = Marshal.PtrToStructure<FormattedItem>(IntPtr.Add(buffer, checked((int)index * size)));
                            var name = Marshal.PtrToStringUni(item.Name);
                            if (name is not null) values[name] = WindowsCpuSampling.IsCounterStatusValid(item.Value.Status) && double.IsFinite(item.Value.DoubleValue) ? item.Value.DoubleValue : null;
                        }
                        status = "";
                        return values;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
                // Instance membership may change between the sizing and fill calls.
                bytes = 0;
                result = PdhGetFormattedCounterArray(counter, flags, ref bytes, out _, IntPtr.Zero);
            }
            status = result == 0 ? "Windows PDH 暂无逻辑处理器实例" : "Windows PDH 实例读取失败：" + Error(result);
            return values;
        }

        private static string Error(uint status) => status switch
        {
            0 => "成功",
            5 or 0xC0000BDB => $"拒绝访问（0x{status:X8}）",
            _ => $"0x{status:X8}"
        };
        private void CloseQuery()
        {
            _query?.Dispose(); _query = null;
            _usage = _frequency = _performance = IntPtr.Zero;
            _collected = false;
        }
        public void Dispose() => CloseQuery();

        [StructLayout(LayoutKind.Sequential)]
        private struct FormattedValue { public uint Status; public double DoubleValue; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FormattedItem { public IntPtr Name; public FormattedValue Value; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint Low, High;
            public readonly ulong Value => ((ulong)High << 32) | Low;
        }
        private sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafePdhQueryHandle() : base(true) { }
            protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
        }
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out SafePdhQueryHandle query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(SafePdhQueryHandle query, string path, UIntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(SafePdhQueryHandle query);
        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedValue value);
        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetActiveProcessorCount(ushort group);
    }
}
