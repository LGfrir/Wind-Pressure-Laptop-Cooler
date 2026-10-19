using SmartFan.App.Controls;
using SmartFan.App.Infrastructure;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.App.Views;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartFan.App;

// 此处只处理视图事件、文件对话框和窗口生命周期；BLE 操作由 VM 调用注入的服务。
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppearanceService _appearance;
    private readonly DesktopService _desktop;
    private readonly AppLog _log;
    private bool _viewReady;
    private bool _exitRequested;
    private bool _changingProfile;
    private LogWindow? _logWindow;
    private SensorInspectorWindow? _sensorWindow;
    private readonly DashboardDraftStore _dashboardDraftStore = new(ClientPaths.DataDirectory);
    private readonly DispatcherTimer _draftTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _freshnessTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DashboardDraftChangedEventArgs? _pendingDashboardDraft;
    private readonly SemaphoreSlim _draftFlushGate = new(1, 1);
    private long _draftGeneration;
    private bool _draftRestoreChecked;
    public MainWindow(MainViewModel vm, AppearanceService appearance, DesktopService desktop, AppLog log)
    {
        _vm = vm; _appearance = appearance; _desktop = desktop; _log = log;
        InitializeComponent(); DataContext = vm;
        _vm.ConfirmAction = text => System.Windows.MessageBox.Show(this, text, "确认设备操作", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        _vm.ConfirmSync = () => Task.FromResult(System.Windows.MessageBox.Show(this, "是否将当前本机场景同步到 ESP？\n\n确定：上传现有接口支持的配置。\n取消：仅读取设备当前设置。\n可在场景页启用“连接后自动上传”。", "连接后同步", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        _vm.ProfileLoaded += LoadProfile;
        _vm.AppearanceChanged += ApplyAppearance;
        _vm.AutostartChanged += () => _desktop.SetAutostart(_vm.Settings.StartWithWindows);
        _vm.BackendConfigurationLoaded += () => { BuildTelemetryItems(); IntervalBox.SelectedValue = _vm.Settings.Telemetry.IntervalMs.ToString(); };
        _vm.RpmSample += Dashboard.AddRpmSample;
        _vm.ConnectionLost += () => Dashboard.MarkRpmGap(DateTimeOffset.Now);
        Dashboard.SaveLayoutAsync = async layout =>
        {
            var previous = _vm.Settings.Dashboard; _vm.Settings.Dashboard = layout.Clone();
            try { await _vm.SaveAsync(); var storage = SettingsStorageLocation.Resolve(_vm.SettingsFilePath); _log.Write($"主页组件布局已保存 · 组件={layout.Widgets.Count} · 实际配置={storage.ActualPath} · 路径已确认={storage.IsResolved}"); return true; }
            catch (Exception ex) { _vm.Settings.Dashboard = previous; _log.Write("组件布局保存失败", "Error", ex); return false; }
        };
        Dashboard.AdvancedNavigationRequested += (_, _) => MainTabs.SelectedIndex = 4;
        AddHandler(DashboardFanControl.OpenCurveRequestedEvent, new RoutedEventHandler((_, _) => MainTabs.SelectedIndex = 2));
        Dashboard.DraftChanged += (_, args) => { _draftGeneration++; _pendingDashboardDraft = args; _draftTimer.Stop(); _draftTimer.Start(); };
        _draftTimer.Tick += async (_, _) => { _draftTimer.Stop(); await FlushDashboardDraftAsync(); };
        _freshnessTimer.Tick += (_, _) => _vm.RefreshDataFreshness();
        LightEditor.ReadRequested += async (_, _) => await _vm.ReadEditorAsync(LightEditor.Mode, LightEditor.LoadPayload);
        LightEditor.ApplyRequested += async (_, _) =>
        {
            try { var bytes = LightEditor.GetPayload(); await _vm.ApplyEditorAsync(LightEditor.Mode, bytes); }
            catch (Exception ex) { LightEditor.ShowStatus(ex.Message); _log.Write(ex.Message, "Warning"); }
        };
        LightEditor.ValidationFailed += message => { _vm.StatusText = message; _log.Write(message, "Warning"); };
        LightEditor.SaveDraftRequested += async (_, _) =>
        {
            try { bool saved = await _vm.SaveEditorDraftAsync(LightEditor.Mode, LightEditor.GetPayload()); LightEditor.ShowStatus(saved ? "已保存到本机场景，尚未同步设备。" : _vm.StatusText, !saved); }
            catch (Exception ex) { LightEditor.ShowStatus(ex.Message); _log.Write(ex.Message, "Warning"); }
        };
        CurveEditor.CurveChanged += (_, _) => { if (!_changingProfile && _vm.SelectedProfile is not null) _vm.SelectedProfile.Curve = CurveEditor.Points.Select(p => new FanCurvePoint { TemperatureCelsius = p.TemperatureCelsius, TargetRpm = p.TargetRpm }).ToList(); };
        IntervalBox.SelectedValue = _vm.Settings.Telemetry.IntervalMs.ToString();
        IntervalBox.SelectionChanged += (_, _) => { if (IntervalBox.SelectedValue is string value && int.TryParse(value, out int interval)) _vm.Settings.Telemetry.IntervalMs = interval; };
        BuildTelemetryItems();
        RestoreWindow(); _viewReady = true; ApplyAppearance(); LoadProfile();
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            _log.Write($"桌面启动 · 管理员={IsAdministrator()}"); _freshnessTimer.Start();
            if (_draftRestoreChecked) return; _draftRestoreChecked = true;
            try
            {
                var baseline = _vm.Settings.Dashboard.Clone(); var generation = _draftGeneration;
                var draft = await _dashboardDraftStore.LoadAsync(baseline);
                if (draft is not null && generation == _draftGeneration && !Dashboard.IsEditing && DashboardEditHistory.Equivalent(baseline, _vm.Settings.Dashboard)) Dashboard.RestoreDraft(draft);
            }
            catch (Exception ex) { _log.Write("组件草稿未恢复，原文件保留", "Warning", ex); }
        };
        SourceInitialized += (_, _) => WindowMaterial.Apply(this, _vm.Settings.Appearance.ThemeName != "Light");
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Closed += (_, _) => { _draftTimer.Stop(); _freshnessTimer.Stop(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; };
    }

    private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    { if (_vm.Settings.Appearance.ThemeName == "System" || _vm.Settings.Appearance.FollowSystemTheme) Dispatcher.BeginInvoke(ApplyAppearance); }
    private void BuildTelemetryItems()
    {
        TelemetryItems.Children.Clear();
        foreach (var pair in _vm.Settings.Telemetry.EnabledItems)
        {
            var key = pair.Key;
            var check = new System.Windows.Controls.CheckBox { Content = key.Replace("laptop_", ""), IsChecked = pair.Value, Margin = new Thickness(0, 8, 20, 8) };
            check.Checked += (_, _) => _vm.Settings.Telemetry.EnabledItems[key] = true;
            check.Unchecked += (_, _) => _vm.Settings.Telemetry.EnabledItems[key] = false;
            TelemetryItems.Children.Add(check);
        }
    }
    private void LoadProfile()
    {
        if (!_viewReady || _vm.SelectedProfile is null) return;
        _changingProfile = true;
        try
        {
            CurveEditor.SetPoints(_vm.SelectedProfile.Curve);
            TemperatureSourceBox.SelectedIndex = (int)_vm.SelectedProfile.TemperatureSource;
            var drafts = new Dictionary<int, byte[]>();
            for (int mode = 1; mode <= 8; mode++)
            {
                var alias = SmartFan.Core.Protocol.ProtocolCatalog.Get(MainViewModel.ConfigKey(mode)).Name;
                if (_vm.SelectedProfile.LightingPayloads.TryGetValue(alias, out var payload)) drafts[mode] = payload;
            }
            LightEditor.LoadSceneDrafts(drafts);
        }
        finally { _changingProfile = false; }
    }
    private void EditorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady) return; LightEditor.SetMode(EditorModeSelector.SelectedIndex + 1);
    }
    private void LoadEditorFromProfile()
    {
        var profile = _vm.SelectedProfile; if (profile is null) return;
        var alias = SmartFan.Core.Protocol.ProtocolCatalog.Get(MainViewModel.ConfigKey(LightEditor.Mode)).Name;
        if (profile.LightingPayloads.TryGetValue(alias, out var bytes)) { LightEditor.LoadPayload(bytes); LightEditor.ShowStatus("已载入当前场景草稿，尚未同步设备。"); }
    }
    private void TemperatureSourceChanged(object sender, SelectionChangedEventArgs e)
    { if (_viewReady && !_changingProfile && _vm.SelectedProfile is not null) { _vm.SelectedProfile.TemperatureSource = (TemperatureSource)TemperatureSourceBox.SelectedIndex; _vm.RefreshFanPresentation(); } }
    private void CpuSensorChanged(object sender, SelectionChangedEventArgs e)
    { if (_viewReady && !_vm.IsRefreshingSensors && CpuSensorBox.SelectedValue is string id) _vm.Settings.Telemetry.SensorPreferences.CpuTemperatureSensorId = string.IsNullOrWhiteSpace(id) ? null : id; }
    private void GpuSensorChanged(object sender, SelectionChangedEventArgs e)
    { if (_viewReady && !_vm.IsRefreshingSensors && GpuSensorBox.SelectedValue is string id) _vm.Settings.Telemetry.SensorPreferences.GpuTemperatureSensorId = string.IsNullOrWhiteSpace(id) ? null : id; }
    private void GpuAdapterChanged(object sender, SelectionChangedEventArgs e)
    { if (_viewReady && !_vm.IsRefreshingSensors && GpuAdapterBox.SelectedValue is string id) _vm.Settings.Telemetry.SensorPreferences.GpuHardwareId = string.IsNullOrWhiteSpace(id) ? null : id; }

    private void ApplyAppearance()
    {
        try
        {
            var a = _vm.Settings.Appearance;
            _appearance.Apply(a.FollowSystemTheme ? "System" : a.ThemeName, a.AccentColor);
            WindowMaterial.Apply(this, a.ThemeName != "Light");
            var uiFont = new System.Windows.Media.FontFamily($"{a.UiFontFamily}, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            System.Windows.Application.Current.Resources["UiFontFamily"] = uiFont;
            FontSize = 14 * a.UiScale;
            _vm.RefreshDashboard();
            if (_logWindow is not null) _logWindow.FontSize = FontSize;
            Wallpaper.Source = a.BackgroundMode == "Wallpaper" && a.WallpaperPath is not null ? AppearanceService.LoadWallpaper(a.WallpaperPath, a.WallpaperCropX, a.WallpaperCropY, a.WallpaperCropWidth, a.WallpaperCropHeight) : null;
            Wallpaper.Opacity = a.WallpaperOpacity;
            _log.MinimumLevel = _vm.Settings.Connection.LogLevel == "Information" ? "Info" : _vm.Settings.Connection.LogLevel;
        }
        catch (Exception ex) { _log.Write($"外观或系统设置失败：{ex.Message}", "Error", ex); _vm.StatusText = ex.Message; }
    }
    private void RestoreWindow()
    {
        var p = _vm.Settings.Window;
        Width = Math.Min(Math.Max(1060, p.Width), SystemParameters.WorkArea.Width);
        Height = Math.Min(Math.Max(720, p.Height), SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(1060, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(720, SystemParameters.WorkArea.Height);
        if (p.Left.HasValue && p.Top.HasValue)
        {
            Left = Math.Clamp(p.Left.Value, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(p.Top.Value, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }
    public async Task SaveWindowAsync()
    {
        await FlushDashboardDraftAsync();
        var p = _vm.Settings.Window; var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        p.Left = bounds.Left; p.Top = bounds.Top; p.Width = bounds.Width; p.Height = bounds.Height; p.Maximized = WindowState == WindowState.Maximized;
        _vm.CaptureProfile(); await _vm.SaveAsync();
    }
    private async Task FlushDashboardDraftAsync()
    {
        _draftTimer.Stop();
        // An exit flush must also await a timer-triggered write that already took the pending draft.
        await _draftFlushGate.WaitAsync();
        try
        {
            while (_pendingDashboardDraft is { } pending)
            {
                var generation = _draftGeneration; _pendingDashboardDraft = null;
                try { await _dashboardDraftStore.SaveAsync(pending.Baseline, pending.Draft); }
                catch (Exception ex)
                {
                    _log.Write("组件草稿暂存失败", "Error", ex);
                    if (generation == _draftGeneration) _pendingDashboardDraft = pending;
                    // Keep the latest edit for a later retry; never retry in an unbounded failure loop.
                    break;
                }
            }
        }
        finally { _draftFlushGate.Release(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (_vm.Settings.MinimizeToTray && !_exitRequested) { _logWindow?.Hide(); _sensorWindow?.Hide(); Hide(); try { await SaveWindowAsync(); } catch (Exception ex) { _log.Write(ex.Message, "Error", ex); } }
        else { _exitRequested = true; await ((App)System.Windows.Application.Current).ExitAsync(); }
    }
    private async void ExitApplication(object sender, RoutedEventArgs e) { _exitRequested = true; await ((App)System.Windows.Application.Current).ExitAsync(); }
    private async void ExportConfig(object sender, RoutedEventArgs e)
    { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 配置|*.json", FileName = "SmartFan-config.json" }; if (dialog.ShowDialog(this) == true) await _vm.ExportAsync(dialog.FileName); }
    private async void ImportConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 配置|*.json" }; if (dialog.ShowDialog(this) != true) return;
        if (System.Windows.MessageBox.Show(this, "导入将替换本地场景与设置，是否继续？", "导入配置", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await _vm.ImportAsync(dialog.FileName); BuildTelemetryItems(); IntervalBox.SelectedValue = _vm.Settings.Telemetry.IntervalMs.ToString();
    }
    private async void ExportLogs(object sender, RoutedEventArgs e)
    { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 日志|*.jsonl", FileName = "SmartFan-diagnostics.jsonl" }; if (dialog.ShowDialog(this) == true) await _vm.ExecuteAsync(() => _log.ExportAsync(dialog.FileName)); }
    private void OpenLogs(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow(_log, this);
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        _logWindow.Show();
        if (_logWindow.WindowState == WindowState.Minimized) _logWindow.WindowState = WindowState.Normal;
        _logWindow.Activate();
    }
    private void OpenSensors(object sender, RoutedEventArgs e)
    {
        if (_sensorWindow is null)
        {
            _sensorWindow = new SensorInspectorWindow(_vm.GetSensorReadingsForInspectorAsync,
                () => $"{_vm.CpuTemperatureStatusText}\n{_vm.HardwareMonitorDetail}\n{_vm.VendorSensorStatusText}\nCPU 频率：{_vm.DashboardData.CpuClockStatus}\nCPU 电压：{_vm.DashboardData.CpuVoltageStatus}", _log, this,
                () => SensorInspectorContext.FromSnapshot(_vm.LatestSnapshot, _vm.DataFreshnessText));
            _sensorWindow.Closed += (_, _) => _sensorWindow = null;
        }
        _sensorWindow.Show();
        if (_sensorWindow.WindowState == WindowState.Minimized) _sensorWindow.WindowState = WindowState.Normal;
        _sensorWindow.Activate();
    }
    private async void ChooseWallpaper(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var a = _vm.Settings.Appearance;
            var crop = new WallpaperCropWindow(dialog.FileName, 0, 0, 1, 1) { Owner = this };
            if (crop.ShowDialog() != true) return;
            a.WallpaperPath = dialog.FileName; a.BackgroundMode = "Wallpaper"; a.WallpaperCropX = crop.CropX; a.WallpaperCropY = crop.CropY; a.WallpaperCropWidth = crop.CropWidth; a.WallpaperCropHeight = crop.CropHeight;
            ApplyAppearance(); await _vm.SaveAsync();
        }
        catch (Exception ex) { _vm.StatusText = ex.Message; _log.Write(ex.Message, "Warning"); }
    }
    private async void ClearWallpaper(object sender, RoutedEventArgs e) { _vm.Settings.Appearance.WallpaperPath = null; _vm.Settings.Appearance.BackgroundMode = "Layered"; ApplyAppearance(); await _vm.ExecuteAsync(_vm.SaveAsync); }
    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private void ShowCpuTemperatureHelp(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            $"当前状态：{_vm.CpuTemperatureStatusText}\n\nCPU 温度由独立后台采集，来源可在“查看硬件传感器”中确认。优先读取有效的 CPU Package 或核心温度；本机另有经过验证的 ASUS EC CPU 温度来源，标准来源无读数时使用它，不会把它改名为 CPU Package。\n\n{_vm.VendorSensorStatusText}\n\n只有标准监控不可用、也没有可用厂商来源时，才需要根据诊断处理驱动或权限。标准采集库所用 PawnIO 的官方网站是 https://pawnio.eu 。提升控制面板不会改变已运行后台的权限。\n\n任何来源没有真实读数时保持缺失，并跳过该项上报。",
            "CPU 温度采集说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void OpenAgentConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AgentLauncher.ConfigurationPath)) throw new FileNotFoundException("后台参数文件尚未生成，请先接入后台。");
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { AgentLauncher.ConfigurationPath } });
        }
        catch (Exception ex) { _vm.StatusText = ex.Message; _log.Write(ex.Message, "Warning"); }
    }
}
