using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartFan.App.Infrastructure;

/// <summary>
/// 显式开启的短时渲染诊断。WPF Rendering 回调频率不是 GPU 实际呈现帧率；
/// 空闲、最小化或没有视觉变化时回调减少，不能单凭此数字判定掉帧。
/// </summary>
public sealed class RenderPerformanceProbe : IDisposable
{
    private const int MaximumSamples = 8192;
    private static readonly TimeSpan SentinelInterval = TimeSpan.FromMilliseconds(20);
    private static readonly ConditionalWeakTable<Window, RenderPerformanceProbe> Active = new();
    private readonly Window _window;
    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _duration;
    private readonly string _label;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly double? _cpuAtStart;
    private readonly List<RenderCallbackSample> _renderSamples = new(MaximumSamples);
    private readonly List<DispatcherLatencySample> _dispatcherSamples = new(MaximumSamples);
    private readonly TaskCompletionSource<RenderPerformanceSummary> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _timer;
    private readonly int _renderTier;
    private readonly string _renderMode;
    private readonly double _dpiX;
    private readonly double _dpiY;
    private readonly double? _width;
    private readonly double? _height;
    private readonly bool _visibleAtStart;
    private readonly string _windowStateAtStart;
    private long? _previousCallback;
    private TimeSpan? _previousRenderingTime;
    private int _stopped;
    private int _finishScheduled;
    private int _sentinelPending;
    private int _sentinelsSkipped;
    private int _renderSamplesDropped;
    private int _dispatcherSamplesDropped;
    private int _uniqueCallbacks;
    private int _duplicateCallbacks;

    private RenderPerformanceProbe(Window window, AppLog log, TimeSpan duration, string label)
    {
        _window = window; _log = log; _dispatcher = window.Dispatcher; _duration = duration; _label = label;
        _cpuAtStart = ReadCpuMilliseconds();
        _renderTier = RenderCapability.Tier >> 16;
        _renderMode = RenderOptions.ProcessRenderMode.ToString();
        var dpi = VisualTreeHelper.GetDpi(window);
        _dpiX = dpi.PixelsPerInchX; _dpiY = dpi.PixelsPerInchY;
        _width = Finite(window.ActualWidth > 0 ? window.ActualWidth : window.Width);
        _height = Finite(window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        _visibleAtStart = window.IsVisible; _windowStateAtStart = window.WindowState.ToString();
        CompositionTarget.Rendering += OnRendering;
        window.Closed += OnWindowClosed;
        _dispatcher.ShutdownStarted += OnDispatcherShutdown;
        _timer = new System.Threading.Timer(OnTimer, null, SentinelInterval, SentinelInterval);
        // 只在显式开始时记录一次能力信息；每个采样回调不写日志、不操作控件。
        _log.Write($"已开启 {_duration.TotalSeconds:F0} 秒渲染诊断：Tier {_renderTier}，{_renderMode}，DPI {_dpiX:F0}×{_dpiY:F0}。指标是 WPF 回调与调度延迟，不是 GPU 呈现 FPS。");
    }

    public bool IsRunning => Volatile.Read(ref _stopped) == 0;
    public Task<RenderPerformanceSummary> Completion => _completion.Task;
    public string? SummaryPath { get; private set; }
    public string? SamplesPath { get; private set; }

    /// <summary>必须在窗口 UI 线程调用；同一窗口重复开启会返回仍在运行的探针。</summary>
    public static RenderPerformanceProbe Start(Window window, AppLog log, TimeSpan? duration = null, string label = "manual")
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(log);
        window.Dispatcher.VerifyAccess();
        if (window.Dispatcher.HasShutdownStarted) throw new InvalidOperationException("窗口调度器正在退出，无法开始诊断。");
        var captureDuration = duration ?? TimeSpan.FromSeconds(15);
        if (captureDuration < TimeSpan.FromSeconds(1) || captureDuration > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(duration), "诊断时长需要在 1～60 秒内。");
        if (Active.TryGetValue(window, out var active) && active.IsRunning) return active;
        Active.Remove(window);
        string boundedLabel = string.IsNullOrWhiteSpace(label) ? "manual" : label.Trim();
        if (boundedLabel.Length > 64) boundedLabel = boundedLabel[..64];
        var probe = new RenderPerformanceProbe(window, log, captureDuration, boundedLabel);
        Active.Add(window, probe);
        return probe;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (!IsRunning || args is not RenderingEventArgs rendering) return;
        if (_previousRenderingTime == rendering.RenderingTime) { _duplicateCallbacks++; return; }
        long now = Stopwatch.GetTimestamp();
        _uniqueCallbacks++;
        if (_previousCallback.HasValue)
        {
            var sample = new RenderCallbackSample
            {
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds,
                WallIntervalMilliseconds = Stopwatch.GetElapsedTime(_previousCallback.Value, now).TotalMilliseconds,
                RenderingTimelineIntervalMilliseconds = _previousRenderingTime.HasValue
                    ? (rendering.RenderingTime - _previousRenderingTime.Value).TotalMilliseconds : null
            };
            if (_renderSamples.Count < MaximumSamples) _renderSamples.Add(sample);
            else _renderSamplesDropped++;
        }
        _previousRenderingTime = rendering.RenderingTime;
        _previousCallback = now;
    }

    private void OnTimer(object? state)
    {
        if (!IsRunning) return;
        if (Stopwatch.GetElapsedTime(_startedTimestamp) >= _duration) { ScheduleFinish("duration_elapsed"); return; }
        // 最多一个诊断哨兵在队列中，UI 卡顿时不会每 20 ms 堆积新任务。
        if (Interlocked.CompareExchange(ref _sentinelPending, 1, 0) != 0) { Interlocked.Increment(ref _sentinelsSkipped); return; }
        long postedAt = Stopwatch.GetTimestamp();
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (!IsRunning) return;
                    long now = Stopwatch.GetTimestamp();
                    var sample = new DispatcherLatencySample
                    {
                        ElapsedMilliseconds = Stopwatch.GetElapsedTime(_startedTimestamp, now).TotalMilliseconds,
                        QueueLatencyMilliseconds = Stopwatch.GetElapsedTime(postedAt, now).TotalMilliseconds
                    };
                    if (_dispatcherSamples.Count < MaximumSamples) _dispatcherSamples.Add(sample);
                    else _dispatcherSamplesDropped++;
                }
                finally { Interlocked.Exchange(ref _sentinelPending, 0); }
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _sentinelPending, 0);
            // ShutdownStarted 会在 UI 线程完成退订；不能在后台线程访问窗口属性。
            _timer.Dispose();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs args) => FinishOnDispatcher("window_closed");
    private void OnDispatcherShutdown(object? sender, EventArgs args) => FinishOnDispatcher("dispatcher_shutdown");

    private void ScheduleFinish(string reason)
    {
        if (!IsRunning || Interlocked.Exchange(ref _finishScheduled, 1) != 0) return;
        if (_dispatcher.CheckAccess()) { FinishOnDispatcher(reason); return; }
        try { _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => FinishOnDispatcher(reason))); }
        catch (InvalidOperationException) { _timer.Dispose(); }
    }

    public void Dispose() => ScheduleFinish("disposed");

    private void FinishOnDispatcher(string reason)
    {
        _dispatcher.VerifyAccess();
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _timer.Dispose();
        CompositionTarget.Rendering -= OnRendering;
        _window.Closed -= OnWindowClosed;
        _dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        if (Active.TryGetValue(_window, out var active) && ReferenceEquals(active, this)) Active.Remove(_window);
        double wallMilliseconds = Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds;
        double? cpuDelta = _cpuAtStart.HasValue && ReadCpuMilliseconds() is { } cpuEnd ? Math.Max(0, cpuEnd - _cpuAtStart.Value) : null;
        var renderSamples = _renderSamples.ToArray();
        var dispatcherSamples = _dispatcherSamples.ToArray();
        var summary = new RenderPerformanceSummary
        {
            Label = _label, StartedAt = _startedAt, StopReason = reason,
            RequestedSeconds = _duration.TotalSeconds, CapturedSeconds = wallMilliseconds / 1000,
            RenderTier = _renderTier, ProcessRenderMode = _renderMode,
            DpiX = _dpiX, DpiY = _dpiY, WindowWidth = _width, WindowHeight = _height,
            VisibleAtStart = _visibleAtStart, VisibleAtEnd = _window.IsVisible,
            WindowStateAtStart = _windowStateAtStart, WindowStateAtEnd = _window.WindowState.ToString(),
            UniqueRenderingCallbacks = _uniqueCallbacks, DuplicateRenderingCallbacks = _duplicateCallbacks,
            RenderingCallbacksPerSecond = wallMilliseconds > 0 ? _uniqueCallbacks * 1000d / wallMilliseconds : null,
            RenderCallbackIntervals = RenderMetricStatistics.Calculate(renderSamples.Select(s => s.WallIntervalMilliseconds)),
            DispatcherQueueLatency = RenderMetricStatistics.Calculate(dispatcherSamples.Select(s => s.QueueLatencyMilliseconds)),
            DispatcherSentinelIntervalMilliseconds = SentinelInterval.TotalMilliseconds,
            SentinelsSkippedWhilePending = Volatile.Read(ref _sentinelsSkipped),
            RenderSamplesDroppedAtLimit = _renderSamplesDropped, DispatcherSamplesDroppedAtLimit = _dispatcherSamplesDropped,
            ProcessCpuMilliseconds = cpuDelta,
            ProcessCpuPercentOfMachine = wallMilliseconds > 0 ? cpuDelta * 100 / wallMilliseconds / Environment.ProcessorCount : null,
            LogicalProcessorCount = Environment.ProcessorCount
        };
        // 采集停止后再在线程池序列化并写盘，避免把诊断本身的 JSON I/O 算进 UI 卡顿。
        _ = Task.Run(() => PersistAsync(summary, renderSamples, dispatcherSamples));
    }

    private async Task PersistAsync(RenderPerformanceSummary summary, RenderCallbackSample[] renderSamples, DispatcherLatencySample[] dispatcherSamples)
    {
        try
        {
            Directory.CreateDirectory(_log.DirectoryPath);
            string stem = $"render-{_startedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            SummaryPath = Path.Combine(_log.DirectoryPath, stem + ".summary.json");
            SamplesPath = Path.Combine(_log.DirectoryPath, stem + ".samples.json");
            summary.SampleMetricsFileName = Path.GetFileName(SamplesPath);
            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(SamplesPath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                MaximumSamplesPerMetric = MaximumSamples,
                RenderingCallbacks = renderSamples,
                DispatcherSentinels = dispatcherSamples
            }, options)).ConfigureAwait(false);
            await File.WriteAllTextAsync(SummaryPath, JsonSerializer.Serialize(summary, options)).ConfigureAwait(false);
            _log.Write($"渲染诊断完成：WPF 回调 {summary.RenderingCallbacksPerSecond:F1}/s，间隔 P95 {summary.RenderCallbackIntervals.P95Milliseconds:F1} ms，调度延迟 P95 {summary.DispatcherQueueLatency.P95Milliseconds:F1} ms。记录：{SummaryPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            summary.PersistenceError = ex.GetType().Name;
            _log.Write("渲染诊断采集结束，但指标文件保存失败。", "Warning", ex);
        }
        finally { _completion.TrySetResult(summary); }
    }

    private static double? ReadCpuMilliseconds()
    {
        try { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime.TotalMilliseconds; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}

public sealed class RenderPerformanceSummary
{
    public int SchemaVersion { get; init; } = 1;
    public string Measurement { get; init; } = "WPF unique Rendering callback wall intervals and background-priority Dispatcher queue latency; not GPU presented FPS.";
    public string Interpretation { get; init; } = "Capture while repeating the same UI interaction. Idle, hidden or minimized windows may receive fewer rendering callbacks. Sentinel latency excludes thread-pool scheduling delay. CPU covers the frontend process only.";
    public string Label { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public string StopReason { get; init; } = "";
    public double RequestedSeconds { get; init; }
    public double CapturedSeconds { get; init; }
    public int RenderTier { get; init; }
    public string ProcessRenderMode { get; init; } = "";
    public double DpiX { get; init; }
    public double DpiY { get; init; }
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public bool VisibleAtStart { get; init; }
    public bool VisibleAtEnd { get; init; }
    public string WindowStateAtStart { get; init; } = "";
    public string WindowStateAtEnd { get; init; } = "";
    public int UniqueRenderingCallbacks { get; init; }
    public int DuplicateRenderingCallbacks { get; init; }
    public double? RenderingCallbacksPerSecond { get; init; }
    public RenderMetricStatistics RenderCallbackIntervals { get; init; } = new();
    public RenderMetricStatistics DispatcherQueueLatency { get; init; } = new();
    public double DispatcherSentinelIntervalMilliseconds { get; init; }
    public int SentinelsSkippedWhilePending { get; init; }
    public int RenderSamplesDroppedAtLimit { get; init; }
    public int DispatcherSamplesDroppedAtLimit { get; init; }
    public double? ProcessCpuMilliseconds { get; init; }
    public double? ProcessCpuPercentOfMachine { get; init; }
    public int LogicalProcessorCount { get; init; }
    public string? SampleMetricsFileName { get; set; }
    public string? PersistenceError { get; set; }
}

public sealed class RenderMetricStatistics
{
    public int Count { get; init; }
    public double? MeanMilliseconds { get; init; }
    public double? P50Milliseconds { get; init; }
    public double? P95Milliseconds { get; init; }
    public double? P99Milliseconds { get; init; }
    public double? MaximumMilliseconds { get; init; }
    public int AtLeast50Milliseconds { get; init; }
    public static RenderMetricStatistics Calculate(IEnumerable<double> values)
    {
        var ordered = values.Where(value => double.IsFinite(value) && value >= 0).Order().ToArray();
        if (ordered.Length == 0) return new RenderMetricStatistics();
        double Percentile(double percentile) => ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
        return new RenderMetricStatistics
        {
            Count = ordered.Length, MeanMilliseconds = ordered.Average(), P50Milliseconds = Percentile(0.5),
            P95Milliseconds = Percentile(0.95), P99Milliseconds = Percentile(0.99), MaximumMilliseconds = ordered[^1],
            AtLeast50Milliseconds = ordered.Count(value => value >= 50)
        };
    }
}

public sealed class RenderCallbackSample
{
    public double ElapsedMilliseconds { get; init; }
    public double WallIntervalMilliseconds { get; init; }
    public double? RenderingTimelineIntervalMilliseconds { get; init; }
}
public sealed class DispatcherLatencySample
{
    public double ElapsedMilliseconds { get; init; }
    public double QueueLatencyMilliseconds { get; init; }
}
