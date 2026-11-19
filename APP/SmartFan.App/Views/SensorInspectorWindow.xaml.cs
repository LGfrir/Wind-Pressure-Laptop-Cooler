using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;

namespace SmartFan.App.Views;

/// <summary>Optional live views reuse the background sampler; no hardware collector is created by this window.</summary>
public partial class SensorInspectorWindow : Window
{
    private readonly Func<Task<IReadOnlyList<SensorDescriptor>>> _refresh;
    private readonly Func<string> _diagnostic;
    private readonly AppLog _log;
    private readonly Func<SensorInspectorContext>? _context;
    private readonly DispatcherTimer _liveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly ObservableCollection<SensorReadingRow> _visibleRows = [];
    private IReadOnlyList<SensorReadingRow> _allRows = [];
    private bool _reading;
    private bool _initialReadStarted;
    private bool _closed;
    private string? _readError;
    private DateTimeOffset? _lastRead;
    private string? _lastLoggedError;
    private bool _initialized;

    public SensorInspectorWindow(Func<Task<IReadOnlyList<SensorDescriptor>>> refresh, Func<string> diagnostic, AppLog log, Window? owner = null, Func<SensorInspectorContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(refresh); ArgumentNullException.ThrowIfNull(diagnostic); ArgumentNullException.ThrowIfNull(log);
        _refresh = refresh; _diagnostic = diagnostic; _log = log; _context = context;
        InitializeComponent();
        if (owner is not null)
        {
            if (new System.Windows.Interop.WindowInteropHelper(owner).Handle != IntPtr.Zero) Owner = owner;
            FontSize = owner.FontSize;
        }
        SensorGrid.ItemsSource = _visibleRows;
        _initialized = true;
        _liveTimer.Tick += async (_, _) => { if (!_reading && !_closed && IsVisible) await RefreshCoreAsync(true); };
        UpdateDiagnostic(); UpdateRows();
        Loaded += async (_, _) => { if (!_initialReadStarted) await RefreshAsync(); UpdateLiveTimer(); };
        IsVisibleChanged += (_, _) => UpdateLiveTimer();
        Closed += (_, _) => { _closed = true; _liveTimer.Stop(); };
    }

    public Task RefreshAsync() => RefreshCoreAsync(false);

    private async Task RefreshCoreAsync(bool automatic)
    {
        if (_closed || _reading) return;
        _initialReadStarted = true; _reading = true; _readError = null;
        RefreshButton.IsEnabled = false; UpdateDiagnostic(); UpdateRows();
        try
        {
            var sensors = await _refresh();
            if (_closed) return;
            var context = ReadContext();
            var existing = _allRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            _allRows = sensors.Select(sensor =>
                {
                    if (!existing.TryGetValue(sensor.Id, out var row)) row = new SensorReadingRow(sensor);
                    else row.Update(sensor);
                    row.SetAdoption(context?.AdoptedSources.GetValueOrDefault(sensor.Id) ?? "");
                    return row;
                })
                .OrderBy(sensor => sensor.HardwareName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(sensor => sensor.HardwareType, StringComparer.Ordinal)
                .ThenBy(sensor => sensor.SensorType, StringComparer.Ordinal)
                .ThenBy(sensor => sensor.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            _lastRead = DateTimeOffset.Now;
            _lastLoggedError = null;
            if (!automatic) _log.Write($"传感器查看已读取 {_allRows.Count} 项，其中 {_allRows.Count(row => row.HasValue)} 项有有效读数。");
        }
        catch (Exception ex)
        {
            if (_lastLoggedError != ex.Message) { _log.Write("传感器查看读取失败", "Error", ex); _lastLoggedError = ex.Message; }
            if (!_closed) _readError = ex.Message;
        }
        finally
        {
            _reading = false;
            if (!_closed) { RefreshButton.IsEnabled = true; UpdateDiagnostic(); UpdateRows(); }
        }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void FilterChanged(object sender, TextChangedEventArgs e) { if (SensorGrid is not null) UpdateRows(); }
    private void LiveRefreshChanged(object sender, RoutedEventArgs e) { if (_initialized) { UpdateLiveTimer(); UpdateRows(); } }
    private void UpdateLiveTimer()
    {
        if (!_closed && IsVisible && LiveRefreshToggle.IsChecked == true) _liveTimer.Start();
        else _liveTimer.Stop();
    }

    private SensorInspectorContext? ReadContext()
    {
        try { return _context?.Invoke(); }
        catch (Exception ex) { _log.Write("传感器来源说明读取失败", "Warning", ex); return null; }
    }

    private void UpdateDiagnostic()
    {
        try { DiagnosticText.Text = _diagnostic() is { Length: > 0 } detail ? detail : "后台尚未提供 CPU 温度诊断，请先确认后台已连接。"; }
        catch (Exception ex) { DiagnosticText.Text = $"无法取得 CPU 温度诊断：{ex.Message}"; _log.Write("读取传感器诊断说明失败", "Warning", ex); }
        var context = ReadContext();
        SourceStampText.Text = context is null ? "主页来源标记将在后台提供采样信息后显示。" :
            context.Timestamp.HasValue ? $"主页采样 {context.Timestamp:HH:mm:ss} · {context.FreshnessText}" : context.FreshnessText;
    }

    private void UpdateRows()
    {
        var query = FilterInput.Text.Trim();
        var desired = _allRows.Where(row => row.Matches(query)).ToArray();
        // Keeping item identity preserves selection and scroll position during background refreshes.
        if (!_visibleRows.SequenceEqual(desired))
        {
            var selectedId = (SensorGrid.SelectedItem as SensorReadingRow)?.Id;
            _visibleRows.Clear(); foreach (var row in desired) _visibleRows.Add(row);
            SensorGrid.SelectedItem = desired.FirstOrDefault(row => row.Id == selectedId);
        }
        CountText.Text = $"{_visibleRows.Count} / {_allRows.Count} 项";
        EmptyState.Visibility = _visibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LastReadText.Text = _lastRead.HasValue ? $"列表刷新 {_lastRead:HH:mm:ss} · {(LiveRefreshToggle.IsChecked == true ? "实时" : "已暂停")}" : "尚未成功读取";
        if (_reading)
        {
            ReadStatus.Text = _lastRead.HasValue ? "正在读取后台传感器，当前显示上次结果…" : "正在读取后台传感器…";
            EmptyText.Text = "正在向后台请求传感器列表与读数…";
        }
        else if (_readError is not null)
        {
            ReadStatus.Text = $"读取失败：{_readError}" + (_lastRead.HasValue ? "；当前保留上次读取结果。" : "；请确认后台已连接后重试。");
            EmptyText.Text = "传感器读取失败。请检查下方错误与顶部 CPU 温度诊断后重试。";
        }
        else if (!_initialReadStarted)
        {
            ReadStatus.Text = "窗口打开后将自动读取一次。"; EmptyText.Text = "等待读取后台传感器。";
        }
        else if (_allRows.Count == 0)
        {
            ReadStatus.Text = "后台本次返回 0 个传感器，未生成模拟读数。";
            EmptyText.Text = "后台未返回硬件传感器。请查看顶部诊断，确认采集库、驱动与权限状态。";
        }
        else
        {
            ReadStatus.Text = $"共 {_allRows.Count} 项传感器，{_allRows.Count(row => row.HasValue)} 项有有效读数；无读数表示后台返回空值。";
            EmptyText.Text = "没有符合筛选条件的传感器，请修改或清空搜索。";
        }
    }
}

/// <summary>Displays the sampler's raw unit and preserves the distinction between zero and a missing value.</summary>
public sealed class SensorReadingRow : INotifyPropertyChanged
{
    public string Id { get; private set; } = "";
    public string HardwareName { get; private set; } = "";
    public string HardwareType { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string SensorType { get; private set; } = "";
    public string ValueText { get; private set; } = "";
    public string Unit { get; private set; } = "";
    public bool HasValue { get; private set; }
    public string ReadStatus { get; private set; } = "";
    public string AdoptedFor { get; private set; } = "";
    public bool IsAdopted => AdoptedFor.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SensorReadingRow(SensorDescriptor sensor) => Update(sensor);
    public void Update(SensorDescriptor sensor)
    {
        Id = sensor.Id; HardwareName = sensor.HardwareName; HardwareType = sensor.HardwareType; Name = sensor.Name; SensorType = sensor.SensorType;
        HasValue = sensor.Value.HasValue && double.IsFinite(sensor.Value.Value);
        ReadStatus = sensor.ReadStatus ?? (HasValue ? "有效读数" : "采集库返回空值；硬件或当前采集源未提供此项");
        ValueText = HasValue ? sensor.Value.GetValueOrDefault().ToString("0.##", CultureInfo.CurrentCulture) : "无读数";
        Unit = sensor.SensorType switch
        {
            "Temperature" => "°C", "Load" or "Control" or "Level" or "Humidity" => "%",
            "Clock" => "MHz", "Frequency" => "Hz", "Fan" => "RPM", "Voltage" => "V",
            "Current" => "A", "Power" => "W", "Data" => "GiB", "SmallData" => "MiB",
            "Throughput" => "B/s", "Flow" => "L/h", "Factor" => "×", _ => "—"
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void SetAdoption(string label)
    {
        if (AdoptedFor == label) return;
        AdoptedFor = label;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdoptedFor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdopted)));
    }

    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) || HardwareName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || HardwareType.Contains(query, StringComparison.OrdinalIgnoreCase)
        || SensorType.Contains(query, StringComparison.OrdinalIgnoreCase) || Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || ReadStatus.Contains(query, StringComparison.CurrentCultureIgnoreCase) || AdoptedFor.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
