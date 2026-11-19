using SmartFan.App.Services;

internal static class WindowsCpuSensorProviderChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(WindowsCpuSampling.IsCounterStatusValid(0) && WindowsCpuSampling.IsCounterStatusValid(1) &&
              !WindowsCpuSampling.IsCounterStatusValid(0xC0000BBA),
            "Windows CPU counters accept valid and new data statuses, never an invalid counter payload");
        check(WindowsCpuSampling.ValidateUsage(0) == 0 && WindowsCpuSampling.ValidateUsage(100) == 100 &&
              new double?[] { null, -1, 101, double.NaN, double.PositiveInfinity }.All(v => WindowsCpuSampling.ValidateUsage(v) is null),
            "CPU usage preserves genuine zero and rejects missing, out-of-range and nonfinite values");
        check(WindowsCpuSampling.CanUseSystemTimesForWholeMachine(1) && WindowsCpuSampling.CanUseSystemTimesForWholeMachine(64) &&
              !WindowsCpuSampling.CanUseSystemTimesForWholeMachine(0) && !WindowsCpuSampling.CanUseSystemTimesForWholeMachine(65) &&
              !WindowsCpuSampling.CanUseSystemTimesForWholeMachine(128),
            "GetSystemTimes is never advertised as an all-machine fallback above 64 logical processors");

        var initial = new WindowsCpuSystemTimes(100, 200, 100);
        check(WindowsCpuSampling.CalculateUsage(initial, new(150, 300, 200)) == 75,
            "native CPU usage subtracts idle from kernel plus user deltas without counting idle twice");
        check(WindowsCpuSampling.CalculateUsage(initial, new(200, 300, 100)) == 0 &&
              WindowsCpuSampling.CalculateUsage(initial, new(100, 300, 100)) == 100,
            "fully idle and fully busy native intervals retain their real endpoints");
        check(WindowsCpuSampling.CalculateUsage(initial, initial) is null &&
              WindowsCpuSampling.CalculateUsage(initial, new(90, 300, 200)) is null &&
              WindowsCpuSampling.CalculateUsage(initial, new(2000, 300, 200)) is null &&
              WindowsCpuSampling.CalculateUsage(initial, new(100, 190, 200)) is null &&
              WindowsCpuSampling.CalculateUsage(initial, new(100, 300, 90)) is null,
            "zero intervals, counter resets and impossible idle deltas remain unavailable");
        check(WindowsCpuSampling.CalculateUsage(new(0, 0, 0), new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)) == 50,
            "large FILETIME deltas do not overflow the combined native CPU interval");

        WindowsCpuPerformanceReading[] hybrid = [new("0,0", 3000, 150), new("0,1", 2000, 50), new("_Total", 9999, 9999), new("0,_Total", 9999, 9999)];
        var reported = WindowsCpuSampling.ReportedFrequency(hybrid, 2, out var explanation);
        check(reported == 2500 && explanation == WindowsCpuSensorProvider.ReportedClockStatus,
            "complete hybrid raw counters return their original MHz average with limited Windows-reported semantics, without performance weighting");
        check(WindowsCpuSampling.ReportedFrequency([new("0,0", 4200, 150)], 1, out _) == 4200 &&
              WindowsCpuSampling.DescribeRawFrequencyCounters([new("0,0", 4200, 150)]).Contains("4200 MHz") &&
              WindowsCpuSampling.DescribeRawFrequencyCounters([new("0,0", 4200, 150)]).Contains("150 %"),
            "an already-current 4200 MHz raw reading cannot be multiplied by a 150 percent performance counter into a false 6300 MHz clock");
        check(WindowsCpuSampling.ReportedFrequency([new("0,0", 3000, null)], 1, out _) == 3000 &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", 3000, 0)], 1, out _) == 3000 &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", 3000, double.NaN)], 1, out _) == 3000,
            "missing, idle or invalid optional performance diagnostics do not alter the independently reported frequency");
        check(WindowsCpuSampling.ReportedFrequency([new("0,0", null, 150)], 1, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", 0, 100)], 1, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", -1, 100)], 1, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", double.PositiveInfinity, 100)], 1, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", double.NaN, 100)], 1, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", double.MaxValue, 100), new("0,1", double.MaxValue, 100)], 2, out _) is null,
            "missing, zero, nonfinite, negative and overflowing raw provider frequencies remain unavailable");
        check(WindowsCpuSampling.ReportedFrequency(hybrid, 3, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", 3000, 100), new("0,0", 3000, 100)], 2, out _) is null &&
              WindowsCpuSampling.ReportedFrequency(hybrid, 0, out _) is null &&
              WindowsCpuSampling.ReportedFrequency([new("0,0", 3000, 100), new("0,1", null, 100)], 2, out _) is null,
            "partial processor coverage, duplicated logical instances and unknown processor counts are not presented as machine frequency");
        var many = Enumerable.Range(0, 128).Select(i => new WindowsCpuPerformanceReading($"{i / 64},{i % 64}", 2000, 125)).ToArray();
        check(WindowsCpuSampling.ReportedFrequency(many, 128, out _) == 2000 &&
              WindowsCpuSampling.DescribeRawFrequencyCounters(many).Contains("128/128 有效") &&
              !WindowsCpuSampling.IsLogicalProcessorInstance("1,_Total") && !WindowsCpuSampling.IsLogicalProcessorInstance("_Total") &&
              !WindowsCpuSampling.IsLogicalProcessorInstance("-1,0"),
            "multi-group reported-frequency means cover all logical processors without double-counting summary instances or inventing a nominal baseline");

        var time = new FixtureTimeProvider();
        var source = new FixtureSource();
        using var provider = new WindowsCpuSensorProvider(source, time, "Fixture CPU");
        source.Next = new() { UsageStatus = "PDH 正在预热", ClockStatus = "PDH 正在预热", ActiveProcessorCount = 2 };
        var warmup = provider.Capture();
        check(warmup.Count == 2 && warmup.All(s => s.Value is null) && provider.Status.Contains("预热"),
            "first-interval warmup exposes explicit unavailable sensors rather than fabricated zeroes");
        check(warmup.All(s => s.HardwareId == "/windows/cpu" && s.HardwareType == "Cpu" && s.HardwareName == "Fixture CPU") &&
              warmup.Single(s => s.SensorType == "Load") is { Id: "/windows/cpu/load/total", Name: "CPU Total" } &&
              warmup.Single(s => s.SensorType == "Clock") is { Id: "/windows/cpu/clock/reported", Name: "Windows CPU Reported Frequency" },
            "Windows fallback sensors expose stable identities, units and the explicit reported-frequency name for model selection");

        source.Next = new() { TotalUsagePercent = 0, Processors = hybrid, ActiveProcessorCount = 2, UsageStatus = "PDH busy time" };
        time.Advance(TimeSpan.FromMilliseconds(999));
        check(ReferenceEquals(provider.Capture(), warmup) && source.Captures == 1,
            "subsecond callers reuse the captured interval without sleeping, resampling or looping");
        time.Advance(TimeSpan.FromMilliseconds(1));
        var measured = provider.Capture();
        check(source.Captures == 2 && measured.Single(s => s.SensorType == "Load").Value == 0 &&
              measured.Single(s => s.SensorType == "Clock").Value == 2500 && provider.RawFrequencyCounters.SequenceEqual(hybrid) &&
              measured.Single(s => s.SensorType == "Clock").ReadStatus == WindowsCpuSensorProvider.ReportedClockStatus,
            "the next eligible interval captures genuine zero CPU load and complete raw diagnostics while identifying the reported frequency as local display only");

        source.Next = new() { TotalUsagePercent = 55, ActiveProcessorCount = 2, ClockStatus = "PDH frequency 拒绝访问" };
        time.Advance(TimeSpan.FromSeconds(1));
        var partial = provider.Capture();
        check(partial.Single(s => s.SensorType == "Load").Value == 55 && partial.Single(s => s.SensorType == "Clock").Value is null &&
              partial.Single(s => s.SensorType == "Clock").ReadStatus!.Contains("拒绝访问"),
            "a failed frequency channel does not erase valid CPU usage and preserves its diagnostic reason");

        source.Failure = new UnauthorizedAccessException("fixture permission denied");
        time.Advance(TimeSpan.FromSeconds(1));
        var denied = provider.Capture();
        check(denied.All(s => s.Value is null) && denied.All(s => s.ReadStatus!.Contains("fixture permission denied")) && provider.RawFrequencyCounters.Count == 0,
            "sampling exceptions clear previous readings and surface access diagnostics without returning cached live values");

        provider.Dispose(); provider.Dispose();
        var threw = false;
        try { provider.Capture(); } catch (ObjectDisposedException) { threw = true; }
        check(source.Disposals == 1 && threw && provider.RawFrequencyCounters.Count == 0,
            "disposal releases the sampler exactly once, clears raw diagnostics and prevents later native calls");
    }

    private sealed class FixtureSource : IWindowsCpuCounterSource
    {
        public WindowsCpuCounterSample Next { get; set; } = new();
        public Exception? Failure { get; set; }
        public int Captures { get; private set; }
        public int Disposals { get; private set; }
        public WindowsCpuCounterSample Capture()
        {
            Captures++;
            if (Failure is not null) throw Failure;
            return Next;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }
}
