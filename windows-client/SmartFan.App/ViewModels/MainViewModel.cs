using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFan.App.Infrastructure;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.Core.Ble;
using SmartFan.Core.Protocol;
using SmartFan.Runtime;
using SmartFan.Runtime.Ipc;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace SmartFan.App.ViewModels;

// ObservableObject 类似 JavaFX Property，通知绑定控件更新；异步命令类似可取消的 CompletableFuture。
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IBleClient _ble;
    private readonly SettingsStore _store;
    private readonly BackgroundClient _background;
    private readonly AppLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _userOperation = new(1, 1);
    private uint? _lastRpm;
    private DateTimeOffset? _lastRpmTime;
    private bool _backendDisconnected;
    private readonly List<TelemetrySnapshot> _dashboardHistory = [];
    private readonly Dictionary<string, string> _dashboardSourceIdentities = new(StringComparer.Ordinal);
    private TelemetryDataState? _lastPresentedFreshness;
    public MainViewModel(BackgroundClient ble, SettingsStore store, AppLog log)
    {
        _ble = ble; _background = ble; _store = store; _log = log;
        _background.SnapshotReceived += (_, snapshot) => Ui(() => { _backendDisconnected = false; LatestSnapshot = snapshot; PresentSample(snapshot); });
        _background.AgentStatusChanged += (_, status) => Ui(() => PresentAgentStatus(status));
        _ble.Diagnostic += (_, e) => _log.Write(e.Message, e.Exception is null ? "Info" : "Warning", e.Exception);
        _ble.ConnectionChanged += (_, e) => Ui(() => HandleConnection(e));
        _ble.RpmReceived += (_, e) => Ui(() =>
        {
            _backendDisconnected = false; _lastRpm = e.Rpm; _lastRpmTime = e.Timestamp; RefreshRpmPresentation(DateTimeOffset.Now, true);
            RpmSample?.Invoke(e.Timestamp, e.Rpm);
            var row = Diagnostics.First(item => item.Key == CharacteristicKey.FanRpm);
            row.ApplyPayload(PacketCodec.EncodeUInt32(e.Rpm), "ESP32 转速通知", e.Timestamp);
            row.Result = "已收到转速通知";
        });
        foreach (var definition in ProtocolCatalog.All) Diagnostics.Add(new DiagnosticRow(definition));
    }

    [ObservableProperty] private AppSettings _settings = new();
    [ObservableProperty] private DeviceProfile? _selectedProfile;
    [ObservableProperty] private BleDeviceInfo? _selectedDevice;
    [ObservableProperty] private RecentDevice? _selectedRecent;
    [ObservableProperty] private string _manualAddress = "";
    [ObservableProperty] private string _connectionText = "未连接";
    [ObservableProperty] private string _deviceText = "选择一个散热底座，或使用保存的地址直接连接。";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private string _agentText = "后台尚未接入";
    [ObservableProperty] private string _agentDetail = "独立后台负责硬件采样、蓝牙与遥测。";
    [ObservableProperty] private string _deviceReadback = "编辑区为本机场景草稿；尚未读取设备。";
    [ObservableProperty] private bool _autoConnectBackground;
    [ObservableProperty] private bool _telemetryEnabled;
    [ObservableProperty] private string _statusText = "就绪 · 未向设备发送配置";
    [ObservableProperty] private string _rpmText = "—";
    [ObservableProperty] private string _rpmStatus = "等待设备转速通知";
    [ObservableProperty] private string _dataFreshnessText = "等待硬件采样";
    [ObservableProperty] private bool _telemetryIsStale;
    [ObservableProperty] private string _fanControlStatusText = "修改模式后点击应用发送。";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private double? _cpuTemperatureValue;
    [ObservableProperty] private double? _cpuUsageValue;
    [ObservableProperty] private double? _gpuTemperatureValue;
    [ObservableProperty] private double? _gpuUsageValue;
    [ObservableProperty] private double? _ramUsedValue;
    [ObservableProperty] private double? _ramTotalValue;
    [ObservableProperty] private double? _fanRpmValue;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _cpuDetail = "读取本机传感器中";
    [ObservableProperty] private string _gpuDetail = "读取本机传感器中";
    [ObservableProperty] private string _monitorStatus = "本机硬件监控尚未采样";
    [ObservableProperty] private string _sensorSummary = "尚无传感器信息";
    [ObservableProperty] private string _cpuTemperatureStatusText = "等待后台采集 CPU 温度";
    [ObservableProperty] private string _hardwareMonitorDetail = "等待后台采集诊断";
    [ObservableProperty] private string _vendorSensorStatusText = "等待厂商采集源诊断";
    [ObservableProperty] private string _mtuText = "—";
    [ObservableProperty] private string _pairingText = "未连接";
    [ObservableProperty] private int _fanModeIndex;
    [ObservableProperty] private double _manualPwm = 20;
    [ObservableProperty] private string _targetRpm = "3000";
    [ObservableProperty] private int _lightModeIndex = 2;
    [ObservableProperty] private double _brightness = 128;
    [ObservableProperty] private int _refreshPeriod = 10;
    [ObservableProperty] private int _editorMode = 1;
    [ObservableProperty] private DiagnosticRow? _selectedDiagnostic;
    [ObservableProperty] private string _rawHex = "";
    [ObservableProperty] private string _profileName = "新场景";
    [ObservableProperty] private string _recentAlias = "我的散热器";
    public ObservableCollection<BleDeviceInfo> Devices { get; } = [];
    public ObservableCollection<RecentDevice> RecentDevices { get; } = [];
    public ObservableCollection<DeviceProfile> Profiles { get; } = [];
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];
    public ObservableCollection<SensorDescriptor> Sensors { get; } = [];
    public ObservableCollection<SensorDescriptor> CpuTemperatureSensors { get; } = [];
    public ObservableCollection<SensorDescriptor> GpuTemperatureSensors { get; } = [];
    public ObservableCollection<SensorDescriptor> GpuAdapters { get; } = [];
    public IReadOnlyList<string> LogLevels { get; } = ["Information", "Debug", "Warning", "Error"];
    public IReadOnlyList<int> RefreshPeriods { get; } = [10, 20, 50, 100, 200];
    public IReadOnlyList<string> FanModes { get; } = ["手动控制", "设备自动控制", "全速运行"];
    public IReadOnlyList<string> LightModes { get; } = ["○ 关闭", "● 静态色", "◉ 彩虹渐变", "◌ 单色呼吸", "✦ 随机闪烁", "◐ 双色闪烁", "◎ 自定义呼吸", "➜ 追逐", "◍ 环绕"];
    public IReadOnlyList<string> Themes { get; } = ["Nebula", "Light", "Cyber", "Graphite", "System"];
    public event Action? ProfileLoaded;
    public event Action? AppearanceChanged;
    public event Action? AutostartChanged;
    public event Action? BackendConfigurationLoaded;
    public event Action<DateTimeOffset, uint>? RpmSample;
    public event Action? ConnectionLost;
    public Func<Task<bool>>? ConfirmSync { get; set; }
    public Func<string, bool>? ConfirmAction { get; set; }
    public TelemetrySnapshot? LatestSnapshot { get; private set; }
    public DashboardTelemetryData DashboardData { get; } = new();
    public bool IsRefreshingSensors { get; private set; }
    public string SettingsFilePath => _store.SettingsPath;
    public string ActualSettingsFilePath { get; private set; } = "";
    public int LoadedDashboardWidgetCount { get; private set; }

    public async Task InitializeAsync()
    {
        var loaded = await _store.LoadAsync(_lifetime.Token);
        Settings = loaded.Settings;
        foreach (var warning in loaded.Warnings) _log.Write(warning, "Warning");
        var storage = SettingsStorageLocation.Resolve(_store.SettingsPath);
        ActualSettingsFilePath = storage.ActualPath; LoadedDashboardWidgetCount = Settings.Dashboard.Widgets.Count;
        OnPropertyChanged(nameof(ActualSettingsFilePath)); OnPropertyChanged(nameof(LoadedDashboardWidgetCount));
        _log.Write($"设置加载位置 · 请求={storage.RequestedPath} · 实际={storage.ActualPath} · 路径已确认={storage.IsResolved} · 已加载组件={LoadedDashboardWidgetCount}");
        LoadCollections();
        _log.Write("控制面板已启动；硬件采样和设备通信由独立后台负责。");
    }

    public async Task AttachBackendAsync()
    {
        _backendReady = false;
        await new AgentLauncher(_log).AttachOrStartAsync(_background, _lifetime.Token);
        var configuration = await _background.GetConfigurationAsync(_lifetime.Token);
        Settings.Telemetry = configuration.Telemetry;
        Settings.Telemetry.Enabled = configuration.EnabledTelemetry;
        TelemetryEnabled = configuration.EnabledTelemetry;
        Settings.Connection.AutoReconnect = configuration.AutoReconnect;
        Settings.Connection.ReconnectInitialSeconds = configuration.ReconnectInitialSeconds;
        Settings.Connection.AutoUploadConfiguration = configuration.AutoUploadConfiguration;
        AutoConnectBackground = configuration.AutoConnect;
        DemoMode = configuration.DemoMode;
        OnPropertyChanged(nameof(Settings));
        if (_background.LatestSnapshot is not null) { LatestSnapshot = _background.LatestSnapshot; PresentSample(_background.LatestSnapshot); }
        if (_background.AgentStatus is not null) PresentAgentStatus(_background.AgentStatus);
        _backendReady = true;
        BackendConfigurationLoaded?.Invoke();
        try { await ReadSensorsForInspectorAsync(); }
        catch (Exception ex) { _log.Write($"首次传感器枚举失败：{ex.Message}", "Warning", ex); }
    }

    private void PresentAgentStatus(AgentStatus status)
    {
        AgentText = status.ProcessId > 0 ? $"后台运行中 · PID {status.ProcessId}" : "后台正在接入";
        AgentLauncher.UseActiveConfiguration(status.ConfigurationPath);
        AgentDetail = $"已采样 {status.SampleCount:N0} 次 · {(status.IsAdministrator ? "管理员" : "普通用户")} · {status.TelemetryMessage}。关闭控制面板后继续运行。";
    }

    private void LoadCollections()
    {
        Profiles.Clear(); foreach (var profile in Settings.Profiles) Profiles.Add(profile);
        RecentDevices.Clear(); foreach (var device in Settings.Connection.RecentDevices) RecentDevices.Add(device);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId) ?? Profiles.FirstOrDefault();
        SelectedRecent = RecentDevices.FirstOrDefault(p => p.Id == Settings.Connection.DefaultDeviceId) ?? RecentDevices.FirstOrDefault();
        if (SelectedRecent is not null) ManualAddress = string.IsNullOrWhiteSpace(SelectedRecent.Id) ? SelectedRecent.Address : SelectedRecent.Id;
    }

    partial void OnSelectedProfileChanged(DeviceProfile? value)
    {
        if (value is null) return;
        Settings.ActiveProfileId = value.Id; FanModeIndex = value.FanMode; ManualPwm = value.ManualPwm;
        TargetRpm = value.TargetRpm.ToString(); LightModeIndex = value.LightMode; Brightness = value.Brightness; ProfileName = value.Name;
        ProfileLoaded?.Invoke();
    }

    private void ConfigureBle()
    {
        _ble.AutoReconnect = Settings.Connection.AutoReconnect;
        _ble.ReconnectInterval = TimeSpan.FromSeconds(Settings.Connection.ReconnectInitialSeconds);
    }

    private void HandleConnection(BleConnectionEventArgs e)
    {
        _backendDisconnected = !_background.IsAttached;
        if (!_background.IsAttached) { AgentText = "后台连接中断"; AgentDetail = "后台可能仍在运行，可点击重新接入；不会因此断开设备。"; }
        IsConnected = _ble.IsConnected;
        ConnectionText = e.State switch { BleConnectionState.Disconnected => "未连接", BleConnectionState.Scanning => "扫描中", BleConnectionState.Connecting => "连接中", BleConnectionState.Discovering => "发现接口中", BleConnectionState.Connected => "已连接 · 已订阅", BleConnectionState.Reconnecting => "正在重连", _ => "连接异常" };
        StatusText = e.Message; _log.Write(e.Message);
        RefreshDataFreshness();
        if (!IsConnected)
        {
            _lastRpm = null; _lastRpmTime = null;
            RpmText = "—"; FanRpmValue = null; DashboardData.FanRpm = null; RpmStatus = "连接已断开，历史记录保留"; MtuText = "—"; ConnectionLost?.Invoke();
            UpdateRpmContext();
            foreach (var row in Diagnostics) { row.ClearPayload("连接已断开，请重新读取设备"); row.Result = "尚未测试"; row.Properties = "未发现"; }
        }
        else
        {
            MtuText = _ble.MaxPduSize.ToString(); PairingText = _ble.ConnectedDevice?.IsPaired == true ? "已配对" : "未配对 · 可直接使用";
            DeviceText = _ble.ConnectedDevice?.ToString() ?? "设备已连接";
            _ = AfterConnectAsync();
        }
    }

    private async Task AfterConnectAsync()
    {
        await ExecuteAsync(async () =>
        {
            if (!_ble.IsConnected) return;
            var status = _background.AgentStatus;
            var receipt = $"{status.BootId}:{status.ConnectionGeneration}";
            if (_lastSyncReceipt == receipt) return;
            _lastSyncReceipt = receipt;
            if (File.Exists(SyncReceiptPath) && await File.ReadAllTextAsync(SyncReceiptPath, _lifetime.Token) == receipt) return;
            var device = _ble.ConnectedDevice;
            if (device is not null)
            {
                var recent = Settings.Connection.RecentDevices.FirstOrDefault(p => p.Id == device.Id || p.Address == device.Address);
                if (recent is null) { recent = new RecentDevice { Id = device.Id, Address = device.Address, Name = device.Name }; Settings.Connection.RecentDevices.Insert(0, recent); RecentDevices.Insert(0, recent); }
                recent.LastConnected = DateTimeOffset.Now;
                if (Settings.Connection.RecentDevices.Count > 20) Settings.Connection.RecentDevices.RemoveRange(20, Settings.Connection.RecentDevices.Count - 20);
                await SaveAsync();
            }
            // 自动同步由后台独立执行，重新打开窗口不重复写 Flash。
            if (!Settings.Connection.AutoUploadConfiguration)
            {
                bool upload = ConfirmSync is not null && await ConfirmSync();
                if (upload) await UploadProfileCoreAsync(); else await ReadDeviceCoreAsync();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SyncReceiptPath) ?? throw new InvalidOperationException("同步记录目录无效。"));
            await File.WriteAllTextAsync(SyncReceiptPath, receipt, _lifetime.Token);
        }, true);
    }
    private string? _lastSyncReceipt;
    private static string SyncReceiptPath => Path.Combine(ClientPaths.DataDirectory, "sync-receipt.txt");

    [RelayCommand] private async Task ScanAsync() => await ExecuteAsync(async () =>
    {
        Devices.Clear(); var found = await _ble.ScanAsync(TimeSpan.FromSeconds(6), Settings.Connection.FilterFanService, _lifetime.Token);
        foreach (var d in found) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault(); StatusText = $"扫描结束 · 找到 {Devices.Count} 台设备";
    });
    [RelayCommand] private async Task ConnectAsync() => await ExecuteAsync(async () =>
    {
        ConfigureBle(); if (SelectedDevice is null) throw new InvalidOperationException("请先选择扫描到的设备。");
        await _ble.ConnectAsync(SelectedDevice, _lifetime.Token);
    });
    [RelayCommand] private async Task ConnectAddressAsync() => await ExecuteAsync(async () => { ConfigureBle(); await _ble.ConnectAsync(ManualAddress.Trim(), _lifetime.Token); });
    [RelayCommand] private async Task ConnectRecentAsync() => await ExecuteAsync(async () =>
    {
        ConfigureBle(); var d = SelectedRecent ?? throw new InvalidOperationException("请先选择最近使用的设备。");
        await _ble.ConnectAsync(string.IsNullOrWhiteSpace(d.Id) ? d.Address : d.Id, _lifetime.Token);
    });
    [RelayCommand] private async Task DisconnectAsync() => await ExecuteAsync(() => _ble.DisconnectAsync(_lifetime.Token));
    [RelayCommand] private async Task AttachAgentAsync() => await ExecuteAsync(AttachBackendAsync);
    [RelayCommand] private async Task StopAgentAsync() => await ExecuteAsync(async () =>
    {
        if (ConfirmAction?.Invoke("停止后台会中断硬件采样、遥测和 ESP 蓝牙连接。仅关闭控制面板可让它继续运行。确定停止后台？") != true) return;
        await _background.StopAgentAsync(_lifetime.Token);
        AgentLauncher.ResetActiveConfiguration();
        AgentText = "后台已停止"; AgentDetail = "点击重新接入后台可再次启动。";
        StatusText = "后台已停止，控制面板仍可离线编辑场景。";
    });
    [RelayCommand] private async Task ForgetDeviceAsync() => await ExecuteAsync(async () =>
    {
        if (ConfirmAction?.Invoke("这将移除当前设备的 Windows 配对记录，之后可以重新连接。继续？") != true) return;
        StatusText = await _ble.UnpairAsync(_lifetime.Token) ? "配对记录已移除" : "设备没有配对记录或操作未完成";
    });

    [RelayCommand] private async Task ApplyFanAsync() => await ExecuteAsync(async () =>
    {
        FanControlStatusText = "正在发送风扇设置并校验…";
        try
        {
            RequireLiveControl(); CaptureProfile();
            await WriteAsync(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(FanModeIndex));
            if (FanModeIndex == 0) await WriteAsync(CharacteristicKey.FanPwm, PacketCodec.EncodeByte((byte)ManualPwm));
            await SaveAsync(); FanControlStatusText = $"{FanModes[FanModeIndex]}已应用 · {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex) { FanControlStatusText = $"应用失败：{ex.Message}"; throw; }
    });
    [RelayCommand] private async Task ApplyTargetRpmAsync() => await ExecuteAsync(async () =>
    {
        RequireLiveControl(); if (!uint.TryParse(TargetRpm, out uint target)) throw new ArgumentException("目标 RPM 需要填写非负整数；设备允许范围及 0 的语义尚待确认。");
        if (ConfirmAction?.Invoke("目标 RPM 的硬件范围尚未确认。这是手动调试接口，不是温控曲线上传。确认发送？") != true) return;
        await WriteAsync(CharacteristicKey.FanRpm, PacketCodec.EncodeUInt32(target));
        if (SelectedProfile is not null) SelectedProfile.TargetRpm = target; await SaveAsync();
    });
    [RelayCommand] private async Task ApplyLightAsync() => await ExecuteAsync(async () =>
    {
        RequireLiveControl(); CaptureProfile();
        await WriteAsync(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(LightModeIndex));
        await WriteAsync(CharacteristicKey.LightBrightness, PacketCodec.EncodeByte((byte)Brightness));
        await WriteAsync(CharacteristicKey.LightUpdatePeriod, PacketCodec.EncodeByte((byte)RefreshPeriod));
        await SaveAsync();
    });
    public async Task ReadEditorAsync(int mode, Action<byte[]> apply) => await ExecuteAsync(async () =>
    {
        var bytes = await _ble.ReadAsync(ConfigKey(mode), _lifetime.Token); apply(bytes); StatusText = "已读取设备灯效参数";
    });
    public async Task ApplyEditorAsync(int mode, byte[] bytes) => await ExecuteAsync(async () =>
    {
        RequireLiveControl(); var key = ConfigKey(mode); PacketCodec.Validate(key, bytes);
        await WriteAsync(key, bytes);
        if (SelectedProfile is not null) SelectedProfile.LightingPayloads[ProtocolCatalog.Get(key).Name] = bytes.ToArray();
        await SaveAsync();
    });
    public static CharacteristicKey ConfigKey(int mode) => mode switch
    {
        1 => CharacteristicKey.StaticConfig, 2 => CharacteristicKey.RainbowConfig, 3 => CharacteristicKey.BreathConfig,
        4 => CharacteristicKey.RandomFlashConfig, 5 => CharacteristicKey.BlinkConfig, 6 => CharacteristicKey.CustomBreathConfig,
        7 => CharacteristicKey.ChaseConfig, 8 => CharacteristicKey.RoundConfig, _ => throw new ArgumentException("关闭模式没有灯效参数。")
    };

    [RelayCommand] private async Task ReadDeviceAsync() => await ExecuteAsync(ReadDeviceCoreAsync);
    private async Task ReadDeviceCoreAsync()
    {
        RequireConnected();
        var results = new List<string>();
        foreach (var key in new[] { CharacteristicKey.FanMode, CharacteristicKey.LightMode, CharacteristicKey.LightBrightness, CharacteristicKey.LightUpdatePeriod })
        {
            try
            {
                var bytes = await _ble.ReadAsync(key, _lifetime.Token);
                var value = bytes.Length == 4 ? PacketCodec.DecodeInt32(bytes).ToString() : bytes[0].ToString();
                results.Add($"{ProtocolCatalog.Get(key).Name}={value}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { results.Add($"{ProtocolCatalog.Get(key).Name} 读取失败"); _log.Write($"设备读取 {key}: {ex.Message}", "Warning"); }
        }
        DeviceReadback = string.Join(" · ", results);
        StatusText = "设备读取结束；编辑区仍保留本机场景草稿。";
    }

    [RelayCommand] private async Task UploadProfileAsync() => await ExecuteAsync(async () => { CaptureProfile(); await UploadProfileCoreAsync(); });
    private async Task UploadProfileCoreAsync()
    {
        RequireLiveControl(); var p = SelectedProfile ?? throw new InvalidOperationException("未选择场景。");
        foreach (var pair in p.LightingPayloads) if (ProtocolCatalog.TryGet(pair.Key, out var key)) await WriteAsync(key, pair.Value);
        await WriteAsync(CharacteristicKey.LightMode, PacketCodec.EncodeInt32(p.LightMode));
        await WriteAsync(CharacteristicKey.LightBrightness, PacketCodec.EncodeByte(p.Brightness));
        await WriteAsync(CharacteristicKey.FanMode, PacketCodec.EncodeInt32(p.FanMode));
        if (p.FanMode == 0) await WriteAsync(CharacteristicKey.FanPwm, PacketCodec.EncodeByte(p.ManualPwm));
        StatusText = "已同步现有接口支持的场景配置；温控曲线、温度来源、10 秒失效策略待固件接口对齐。";
        await SaveAsync();
    }
    private async Task WriteAsync(CharacteristicKey key, byte[] bytes)
    {
        var result = await _ble.WriteAsync(key, bytes, true, _lifetime.Token); StatusText = result.Message; _log.Write($"{ProtocolCatalog.Get(key).Name}: {result.Message}");
    }
    private void RequireConnected() { if (!_ble.IsConnected) throw new InvalidOperationException("请先连接设备。"); }
    private void RequireLiveControl() { RequireConnected(); if (DemoMode) throw new InvalidOperationException("演示模式下暂停设备控制和上报，请先关闭演示模式。"); }
    public void CaptureProfile()
    {
        if (SelectedProfile is null) return;
        SelectedProfile.FanMode = FanModeIndex; SelectedProfile.ManualPwm = (byte)Math.Clamp(ManualPwm, 0, 100);
        SelectedProfile.LightMode = LightModeIndex; SelectedProfile.Brightness = (byte)Math.Clamp(Brightness, 0, 255);
    }
    [RelayCommand] private async Task SaveSettingsAsync() => await ExecuteAsync(async () => { CaptureProfile(); await SynchronizeAgentSettingsAsync(); await SaveAsync(); AppearanceChanged?.Invoke(); AutostartChanged?.Invoke(); StatusText = "控制面板与后台参数已保存"; });
    public async Task SynchronizeAgentSettingsAsync()
    {
        var configuration = await _background.GetConfigurationAsync(_lifetime.Token);
        configuration.EnabledTelemetry = Settings.Telemetry.Enabled && !DemoMode;
        configuration.Telemetry = Settings.Telemetry;
        configuration.AutoReconnect = Settings.Connection.AutoReconnect;
        configuration.ReconnectInitialSeconds = Settings.Connection.ReconnectInitialSeconds;
        configuration.AutoConnect = AutoConnectBackground;
        configuration.DemoMode = DemoMode;
        configuration.AutoUploadConfiguration = Settings.Connection.AutoUploadConfiguration;
        configuration.AutoUploadProfile = SelectedProfile is null ? null : AgentJson.Clone(SelectedProfile);
        configuration.LogLevel = Settings.Connection.LogLevel;
        await _background.ConfigureAsync(configuration, _lifetime.Token);
    }
    partial void OnDemoModeChanged(bool value)
    {
        if (!_backendReady) return;
        _ = ExecuteAsync(async () => { await _background.SetDemoAsync(value, _lifetime.Token); StatusText = value ? "后台演示模式 · 已暂停设备写入" : "后台已恢复真实硬件采样"; }, true);
    }
    partial void OnTelemetryEnabledChanged(bool value)
    {
        Settings.Telemetry.Enabled = value;
        if (_backendReady) _ = ExecuteAsync(SynchronizeAgentSettingsAsync, true);
    }
    private bool _backendReady;
    public Task SaveAsync() => _store.SaveAsync(Settings, _lifetime.Token);
    public async Task<bool> SaveEditorDraftAsync(int mode, byte[] bytes)
    {
        bool saved = false;
        await ExecuteAsync(async () =>
        {
            var key = ConfigKey(mode); PacketCodec.Validate(key, bytes);
            if (SelectedProfile is null) throw new InvalidOperationException("请先选择场景。");
            SelectedProfile.LightingPayloads[ProtocolCatalog.Get(key).Name] = bytes.ToArray();
            await SaveAsync(); saved = true; StatusText = "灯效参数已保存到当前本机场景，尚未发送设备。";
        });
        return saved;
    }
    [RelayCommand] private async Task SaveProfileAsync() => await ExecuteAsync(async () =>
    {
        CaptureProfile(); if (SelectedProfile is not null) SelectedProfile.Name = string.IsNullOrWhiteSpace(ProfileName) ? "未命名场景" : ProfileName.Trim();
        await SaveAsync(); var selection = SelectedProfile; Profiles.Clear(); foreach (var p in Settings.Profiles) Profiles.Add(p); SelectedProfile = selection;
        StatusText = "场景已保存到本机";
    });
    [RelayCommand] private async Task NewProfileAsync() => await ExecuteAsync(async () =>
    {
        var p = new DeviceProfile { Name = string.IsNullOrWhiteSpace(ProfileName) ? "新场景" : ProfileName.Trim() }; Settings.Profiles.Add(p); Profiles.Add(p); SelectedProfile = p; await SaveAsync();
    });
    [RelayCommand] private async Task DeleteProfileAsync() => await ExecuteAsync(async () =>
    {
        if (SelectedProfile is null || Profiles.Count <= 1) throw new InvalidOperationException("至少保留一个场景。");
        if (ConfirmAction?.Invoke($"删除本机场景“{SelectedProfile.Name}”？") != true) return;
        Settings.Profiles.Remove(SelectedProfile); Profiles.Remove(SelectedProfile); SelectedProfile = Profiles.First(); await SaveAsync();
    });
    [RelayCommand] private async Task RenameRecentAsync() => await ExecuteAsync(async () =>
    {
        if (SelectedRecent is null) return; SelectedRecent.Name = RecentAlias.Trim(); await SaveAsync();
        var selected = SelectedRecent; RecentDevices.Clear(); foreach (var d in Settings.Connection.RecentDevices) RecentDevices.Add(d); SelectedRecent = selected;
    });
    [RelayCommand] private async Task SetDefaultRecentAsync() => await ExecuteAsync(async () => { Settings.Connection.DefaultDeviceId = SelectedRecent?.Id; await SaveAsync(); StatusText = "已设为默认设备"; });
    [RelayCommand] private async Task RemoveRecentAsync() => await ExecuteAsync(async () => { if (SelectedRecent is null) return; Settings.Connection.RecentDevices.Remove(SelectedRecent); RecentDevices.Remove(SelectedRecent); await SaveAsync(); });

    [RelayCommand] private async Task RunDiagnosticsAsync() => await ExecuteAsync(async () =>
    {
        RequireConnected();
        foreach (var row in Diagnostics)
        {
            var discovered = _ble.Characteristics.FirstOrDefault(c => c.Definition.Key == row.Key);
            row.Properties = discovered?.Properties ?? "缺失";
            if (discovered is null) { row.ClearPayload("设备未发现此接口"); row.Result = "接口缺失"; continue; }
            if (!row.Definition.CanRead)
            {
                if (!row.HasPayload) row.ClearPayload("等待 ESP32 转速通知");
                row.Result = row.HasPayload ? "已收到转速通知 · 不支持 READ" : "仅 NOTIFY / WRITE";
                continue;
            }
            try
            {
                var bytes = await _ble.ReadAsync(row.Key, _lifetime.Token); row.ApplyPayload(bytes, "设备 READ");
                row.Result = row.IsInvalidPayload ? "已读 · 解析失败" : row.Key == CharacteristicKey.FanPwm ? "已读 · 固件恒回 0" : $"已读 · {bytes.Length} B";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { row.ClearPayload($"本次读取失败：{ex.Message}（旧载荷已清除）"); row.Result = $"失败：{ex.Message}"; _log.Write($"自检 {row.Name}: {ex.Message}", "Warning"); }
        }
        StatusText = "读取自检结束；写入、重启持久化与物理效果需分别验收。";
    });
    [RelayCommand] private async Task WriteDiagnosticAsync() => await ExecuteAsync(async () =>
    {
        RequireLiveControl(); var row = SelectedDiagnostic ?? throw new InvalidOperationException("请选择接口。");
        var bytes = Convert.FromHexString(RawHex.Replace(" ", "").Replace("-", "")); PacketCodec.Validate(row.Key, bytes);
        if (ConfirmAction?.Invoke($"向 {row.Name} 写入 {bytes.Length} 字节并校验？配置特征可能写入设备 Flash。") != true) return;
        var result = await _ble.WriteAsync(row.Key, bytes, true, _lifetime.Token); row.Result = result.Message;
        // RPM 的写入是目标值，通知是实际读数，不能用目标载荷覆盖通知解析。
        if (row.Key != CharacteristicKey.FanRpm) row.ApplyPayload(bytes, $"写入载荷 · {result.Message}");
    });
    [RelayCommand] private async Task RefreshSensorsAsync() => await ExecuteAsync(async () =>
    {
        await ReadSensorsForInspectorAsync();
        StatusText = $"后台已发现 {Sensors.Count} 个传感器";
    });
    public async Task<IReadOnlyList<SensorDescriptor>> ReadSensorsForInspectorAsync()
    {
        var sensors = await _background.EnumerateSensorsAsync(_lifetime.Token);
        PresentSensors(sensors);
        return sensors;
    }
    public Task<IReadOnlyList<SensorDescriptor>> GetSensorReadingsForInspectorAsync() => _background.EnumerateSensorsAsync(_lifetime.Token);
    public void RefreshFanPresentation() => OnPropertyChanged(nameof(SelectedProfile));
    private void PresentSensors(IReadOnlyList<SensorDescriptor> sensors)
    {
        IsRefreshingSensors = true;
        try
        {
            Sensors.Clear(); foreach (var s in sensors) Sensors.Add(s);
            CpuTemperatureSensors.Clear(); GpuTemperatureSensors.Clear(); GpuAdapters.Clear();
            CpuTemperatureSensors.Add(new SensorDescriptor { Id = "", Name = "自动选择 CPU 温度", HardwareName = "自动" });
            GpuTemperatureSensors.Add(new SensorDescriptor { Id = "", Name = "自动选择 GPU 温度", HardwareName = "自动" });
            GpuAdapters.Add(new SensorDescriptor { HardwareId = "", HardwareName = "自动优先独显" });
            foreach (var s in sensors.Where(s => s.HardwareType == "Cpu" && SensorSelection.IsAbsoluteTemperature(s))) CpuTemperatureSensors.Add(s);
            foreach (var s in sensors.Where(s => s.SensorType == "Temperature" && s.HardwareType.StartsWith("Gpu", StringComparison.Ordinal))) GpuTemperatureSensors.Add(s);
            foreach (var s in sensors.Where(s => s.HardwareType.StartsWith("Gpu", StringComparison.Ordinal)).DistinctBy(s => s.HardwareId)) GpuAdapters.Add(s);
            OnPropertyChanged(nameof(Settings));
        }
        finally { IsRefreshingSensors = false; }
    }
    public async Task ImportAsync(string path) => await ExecuteAsync(async () => { Settings = await _store.ImportAsync(path, _lifetime.Token); LoadCollections(); ConfigureBle(); await SaveAsync(); AppearanceChanged?.Invoke(); StatusText = "配置已导入本机，尚未上传设备。"; });
    public async Task ExportAsync(string path) => await ExecuteAsync(async () => { CaptureProfile(); await _store.ExportAsync(path, Settings, _lifetime.Token); StatusText = "配置已导出"; });

    private void PresentSample(TelemetrySnapshot sample)
    {
        LatestSnapshot = sample;
        // Keep the last known physical source through missing readings. The sampler omits a
        // SensorNames entry when its value is temporarily unavailable; that is a gap, not a switch.
        string[] selectedSources = ["CPU 温度", "GPU 温度", "CPU 使用率", "CPU 电压", "GPU 使用率", "GPU 频率"];
        var sourceChanged = selectedSources.Any(key => sample.SensorNames.TryGetValue(key, out var source) && !string.IsNullOrWhiteSpace(source)
            && _dashboardSourceIdentities.TryGetValue(key, out var previousSource) && previousSource != source);
        var resetHistory = sourceChanged;
        if (_dashboardHistory.Count > 0)
        {
            var previous = _dashboardHistory[^1];
            // A changed physical source or demo stream is a new series, not a continuous measurement.
            // CPU frequency is a maximum across cores; the winning core may legitimately change each sample.
            resetHistory |= previous.IsDemo != sample.IsDemo || previous.CpuName != sample.CpuName || previous.GpuName != sample.GpuName
                || previous.CpuVoltageKind != sample.CpuVoltageKind || sample.Timestamp < previous.Timestamp;
        }
        if (resetHistory) { _dashboardHistory.Clear(); _dashboardSourceIdentities.Clear(); }
        foreach (var key in selectedSources)
            if (sample.SensorNames.TryGetValue(key, out var knownSource) && !string.IsNullOrWhiteSpace(knownSource)) _dashboardSourceIdentities[key] = knownSource;
        if (_dashboardHistory.Count == 0 || sample.Timestamp > _dashboardHistory[^1].Timestamp) _dashboardHistory.Add(sample);
        _dashboardHistory.RemoveAll(item => item.Timestamp < sample.Timestamp - TimeSpan.FromMinutes(5));
        if (_dashboardHistory.Count > 600) _dashboardHistory.RemoveRange(0, _dashboardHistory.Count - 600);
        CpuTemperatureValue = sample.CpuTemperature; CpuUsageValue = sample.CpuUsage;
        GpuTemperatureValue = sample.GpuTemperature; GpuUsageValue = sample.GpuUsage;
        RamUsedValue = sample.RamUsedMiB / 1024; RamTotalValue = sample.RamTotalMiB / 1024;
        string Temperature(double? v) => v.HasValue ? Settings.Appearance.TemperatureUnit == "F" ? $"{v * 1.8 + 32:F1} °F" : $"{v:F1} °C" : "不可用";
        CpuText = Temperature(sample.CpuTemperature); GpuText = Temperature(sample.GpuTemperature);
        CpuDetail = $"{sample.CpuName}\n占用 {Format(sample.CpuUsage, "%")}  ·  {Format(sample.CpuClockMhz, " MHz")}";
        GpuDetail = $"{sample.GpuName}\n占用 {Format(sample.GpuUsage, "%")}  ·  {Format(sample.GpuClockMhz, " MHz")}";
        double divisor = Settings.Appearance.MemoryUnit == "MiB" ? 1 : 1024;
        MemoryText = sample.RamUsedMiB.HasValue ? $"{sample.RamUsedMiB / divisor:F1} / {sample.RamTotalMiB / divisor:F1} {(divisor == 1 ? "MiB" : "GiB")}" : "不可用";
        MonitorStatus = (sample.IsDemo ? "演示数据 · 设备控制与遥测已暂停。 " : "真实传感器 · ") + sample.Status;
        SensorSummary = string.Join(Environment.NewLine, sample.SensorNames.Select(p => $"{p.Key}: {p.Value}"));
        CpuTemperatureStatusText = sample.CpuTemperature.HasValue
            ? sample.CpuTemperatureStatus is { Code: TemperatureReadCode.Available, Message.Length: > 0 } source ? source.Message : "CPU 温度采集正常"
            : sample.CpuTemperatureStatus is null || sample.CpuTemperatureStatus.Code == TemperatureReadCode.NotSampled || string.IsNullOrWhiteSpace(sample.CpuTemperatureStatus.Message)
                ? "未读到 CPU 温度；当前后台未提供具体采集诊断，请更新后台后检查。"
                : sample.CpuTemperatureStatus.Message;
        var monitor = sample.HardwareMonitor;
        HardwareMonitorDetail = monitor is null ? "当前后台未提供驱动诊断，请更新后台后检查。"
            : $"标准采集库 {monitor.ProviderVersion.Split('+')[0]} · 后台{(monitor.IsAdministrator ? "管理员" : "普通")}权限 · PawnIO {(monitor.PawnIoInstalled ? monitor.PawnIoVersion ?? "已安装" : "未安装")}";
        VendorSensorStatusText = string.IsNullOrWhiteSpace(sample.VendorSensorStatus) ? "后台未提供厂商采集源信息" : sample.VendorSensorStatus;
        RefreshDashboard();
        RefreshDataFreshness();
    }

    public void RefreshDashboard() => RefreshDashboardAt(DateTimeOffset.Now);
    private void RefreshDashboardAt(DateTimeOffset presentationTime)
    {
        bool fahrenheit = Settings.Appearance.TemperatureUnit == "F";
        double? Temperature(double? value) => fahrenheit ? value * 1.8 + 32 : value;
        double divisor = Settings.Appearance.MemoryUnit == "MiB" ? 1 : 1024;
        DashboardData.TemperatureUnit = fahrenheit ? "°F" : "°C";
        DashboardData.TemperatureMaximum = fahrenheit ? 212 : 100;
        DashboardData.MemoryUnit = Settings.Appearance.MemoryUnit == "MiB" ? "MiB" : "GiB";
        DashboardData.MemoryValueFormat = divisor == 1 ? "0" : "0.0";
        var sample = LatestSnapshot;
        DashboardData.CpuVoltageLabel = sample?.CpuVoltageKind == CpuVoltageKind.Vid ? "最高核心 VID" : "核心电压";
        DashboardData.CpuVoltageCardTitle = sample?.CpuVoltageKind == CpuVoltageKind.Vid ? "CPU 核心 VID" : "CPU 核心电压";
        var freshness = TelemetryFreshness.Evaluate(sample?.Timestamp, presentationTime, Settings.Telemetry.IntervalMs, !_backendDisconnected);
        _lastPresentedFreshness = freshness;
        DataFreshnessText = TelemetryFreshness.Describe(freshness, sample?.Timestamp);
        if (sample?.IsDemo == true) DataFreshnessText = "演示数据 · " + DataFreshnessText;
        TelemetryIsStale = freshness is TelemetryDataState.Expired or TelemetryDataState.BackendDisconnected;
        DashboardData.FreshnessText = DataFreshnessText; DashboardData.IsStale = TelemetryIsStale;
        RefreshRpmPresentation(presentationTime, false);
        if (sample is null || freshness != TelemetryDataState.Live)
        {
            CpuTemperatureValue = CpuUsageValue = GpuTemperatureValue = GpuUsageValue = RamUsedValue = RamTotalValue = null;
            DashboardData.CpuTemperature = DashboardData.CpuUsage = DashboardData.GpuTemperature = DashboardData.GpuUsage = null;
            DashboardData.CpuClockMhz = DashboardData.CpuVoltage = DashboardData.GpuClockMhz = DashboardData.GpuMemoryUsed = DashboardData.GpuMemoryTotal = null;
            DashboardData.MemoryUsed = DashboardData.MemoryTotal = null;
            DashboardData.LaptopFans = Array.Empty<LaptopFanReading>(); DashboardData.LaptopFanSummary = DataFreshnessText;
            DashboardData.LaptopFanDetail = DataFreshnessText;
            DashboardData.CpuTemperatureStatus = DashboardData.GpuTemperatureStatus = DashboardData.CpuClockStatus = DashboardData.CpuVoltageStatus = DataFreshnessText;
            CpuText = GpuText = MemoryText = freshness == TelemetryDataState.Waiting ? "—" : "已过期";
            UpdateMetricContext(sample, divisor, fahrenheit, DataFreshnessText); return;
        }
        CpuTemperatureValue = sample.CpuTemperature; CpuUsageValue = sample.CpuUsage; GpuTemperatureValue = sample.GpuTemperature; GpuUsageValue = sample.GpuUsage;
        RamUsedValue = sample.RamUsedMiB / 1024; RamTotalValue = sample.RamTotalMiB / 1024;
        DashboardData.CpuTemperature = Temperature(sample.CpuTemperature);
        DashboardData.CpuUsage = sample.CpuUsage;
        DashboardData.GpuTemperature = Temperature(sample.GpuTemperature);
        DashboardData.GpuUsage = sample.GpuUsage;
        DashboardData.CpuClockMhz = sample.CpuClockMhz;
        DashboardData.CpuVoltage = sample.CpuVoltage;
        DashboardData.GpuClockMhz = sample.GpuClockMhz;
        DashboardData.GpuMemoryUsed = sample.GpuRamUsedMiB / divisor;
        DashboardData.GpuMemoryTotal = sample.GpuRamTotalMiB / divisor;
        DashboardData.CpuHardwareStatus = sample.HardwareMonitor is { PawnIoDeviceAccessible: false } environment
            ? !environment.PawnIoInstalled ? "未安装 PawnIO，CPU 频率与电压暂不可读" : "后台无法访问 PawnIO，请检查驱动与后台权限"
            : "采集库尚未提供有效的核心频率或电压";
        DashboardData.CpuClockStatus = sample.CpuClockMhz.HasValue ? "本机有效核心频率的最大值" : !string.IsNullOrWhiteSpace(sample.CpuClockReadStatus) ? sample.CpuClockReadStatus : DashboardData.CpuHardwareStatus;
        DashboardData.CpuVoltageStatus = !string.IsNullOrWhiteSpace(sample.CpuVoltageReadStatus) ? sample.CpuVoltageReadStatus
            : sample.CpuVoltage.HasValue ? sample.CpuVoltageKind == CpuVoltageKind.Vid ? "本机最高有效核心 VID（请求电压），不是主板实测 VCore" : "本机 CPU 核心电压传感器"
            : DashboardData.CpuHardwareStatus;
        DashboardData.MemoryUsed = sample.RamUsedMiB / divisor;
        DashboardData.MemoryTotal = sample.RamTotalMiB / divisor;
        DashboardData.CpuTemperatureStatus = CpuTemperatureStatusText;
        DashboardData.GpuTemperatureStatus = sample.GpuTemperature.HasValue ? "本机实时数据" : "未读到显卡温度，请检查显卡与传感器选择";
        var installedFans = Enumerable.Range(0, Math.Min(5, sample.FanPresent.Length)).Where(slot => sample.FanPresent[slot] == 1).ToArray();
        if (sample.FanPresenceAvailable && installedFans.Length > 0)
        {
            string Rpm(int slot) => slot < sample.FanRpm.Length && sample.FanRpm[slot] is { } rpm && double.IsFinite(rpm) && rpm >= 0 ? rpm.ToString("0") : "—";
            string Name(int slot)
            {
                sample.SensorNames.TryGetValue($"内置风扇 {slot + 1}", out var source);
                return source?.Contains("/asus-ec/fan/0-cpu", StringComparison.Ordinal) == true ? "CPU"
                    : source?.Contains("/asus-ec/fan/1-gpu", StringComparison.Ordinal) == true ? "GPU"
                    : source?.Contains("/asus-ec/fan/2-middle", StringComparison.Ordinal) == true ? "辅助"
                    : $"风扇 {slot + 1}";
            }
            DashboardData.LaptopFanSummary = $"内置风扇 {string.Join(" / ", installedFans.Select(Rpm))} RPM";
            DashboardData.LaptopFans = installedFans.Select(slot => new LaptopFanReading(Name(slot), slot < sample.FanRpm.Length && sample.FanRpm[slot] is { } rpm && double.IsFinite(rpm) && rpm >= 0 ? rpm : null)).ToArray();
            DashboardData.LaptopFanDetail = "电脑内置风扇，与 ESP32 散热底座独立：\n" + string.Join("\n", installedFans.Select(slot => $"{Name(slot)}：{Rpm(slot)} RPM"));
        }
        else
        {
            DashboardData.LaptopFanSummary = "电脑内置风扇转速不可用";
            DashboardData.LaptopFans = Array.Empty<LaptopFanReading>();
            DashboardData.LaptopFanDetail = "未确定电脑内置风扇数量或没有可用读数；可在硬件传感器窗口检查来源。";
        }
        UpdateMetricContext(sample, divisor, fahrenheit, null);
    }
    public void RefreshDataFreshness(DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        var freshness = TelemetryFreshness.Evaluate(LatestSnapshot?.Timestamp, time, Settings.Telemetry.IntervalMs, !_backendDisconnected);
        if (_lastPresentedFreshness != freshness) RefreshDashboardAt(time);
        RefreshRpmPresentation(time, true);
    }
    private void RefreshRpmPresentation(DateTimeOffset time, bool updateContext)
    {
        var previousValue = FanRpmValue; var previousStatus = RpmStatus;
        if (_lastRpmTime is { } timestamp && _lastRpm is { } rpm)
        {
            if (!_backendDisconnected && time - timestamp <= TimeSpan.FromSeconds(3) && timestamp - time <= TimeSpan.FromSeconds(5))
            {
                FanRpmValue = rpm; DashboardData.FanRpm = rpm; RpmText = $"{rpm:N0}";
                RpmStatus = $"ESP32 设备通知 · {timestamp:HH:mm:ss}";
            }
            else
            {
                FanRpmValue = null; DashboardData.FanRpm = null; RpmText = "—";
                RpmStatus = _backendDisconnected ? "后台连接中断 · 等待设备转速通知" : $"通知已过期 · 最后值 {rpm:N0} RPM，{timestamp:HH:mm:ss}";
            }
        }
        else { FanRpmValue = null; DashboardData.FanRpm = null; RpmText = "—"; }
        if (updateContext && (previousValue != FanRpmValue || previousStatus != RpmStatus)) UpdateRpmContext();
    }
    private void UpdateRpmContext()
    {
        var contexts = DashboardData.MetricContext.ToDictionary();
        contexts["fan.rpm"] = new DashboardMetricContext("ESP32 散热底座", "设备转速通知", RpmStatus, Array.Empty<DashboardTrendPoint>());
        DashboardData.MetricContext = contexts;
    }
    private void UpdateMetricContext(TelemetrySnapshot? sample, double memoryDivisor, bool fahrenheit, string? unavailable)
    {
        var result = new Dictionary<string, DashboardMetricContext>();
        string Source(string name, string fallback) => sample?.SensorNames.TryGetValue(name, out var value) == true && !string.IsNullOrWhiteSpace(value) ? value
            : _dashboardSourceIdentities.TryGetValue(name, out var known) ? known : fallback;
        double? Temp(double? value) => fahrenheit ? value * 1.8 + 32 : value;
        var cpu = sample?.CpuName ?? "CPU"; var gpu = sample?.GpuName ?? "GPU";
        Add("cpu.temperature", cpu, Source("CPU 温度", CpuTemperatureStatusText), unavailable ?? DashboardData.CpuTemperatureStatus, item => Temp(item.CpuTemperature));
        Add("cpu.usage", cpu, Source("CPU 使用率", "CPU Total · 全处理器使用率"), unavailable ?? DataFreshnessText, item => item.CpuUsage);
        Add("cpu.clock", cpu, Source("CPU 频率", "CPU 各核心硬件频率"), unavailable ?? DashboardData.CpuClockStatus, item => item.CpuClockMhz);
        Add("cpu.voltage", cpu, Source("CPU 电压", "CPU 核心电压传感器"), unavailable ?? DashboardData.CpuVoltageStatus, item => item.CpuVoltage);
        Add("gpu.temperature", gpu, Source("GPU 温度", "GPU Core"), unavailable ?? DashboardData.GpuTemperatureStatus, item => Temp(item.GpuTemperature));
        Add("gpu.usage", gpu, Source("GPU 使用率", "GPU Core · 图形引擎负载"), unavailable ?? DataFreshnessText, item => item.GpuUsage);
        Add("gpu.clock", gpu, Source("GPU 频率", "GPU Core"), unavailable ?? DataFreshnessText, item => item.GpuClockMhz);
        Add("gpu.memory", gpu, "独显专用显存", unavailable ?? DataFreshnessText, item => item.GpuRamUsedMiB / memoryDivisor);
        Add("memory.usage", "系统内存", "Windows 物理内存", unavailable ?? DataFreshnessText, item => item.RamUsedMiB / memoryDivisor);
        result["fan.rpm"] = new DashboardMetricContext("ESP32 散热底座", "设备转速通知", RpmStatus, Array.Empty<DashboardTrendPoint>());
        DashboardData.MetricContext = result;
        void Add(string key, string device, string source, string status, Func<TelemetrySnapshot, double?> selector)
        {
            var current = sample is null ? null : selector(sample);
            var title = key switch { "cpu.temperature" => "CPU 温度", "cpu.usage" => "CPU 使用率", "cpu.clock" => "CPU 核心频率", "cpu.voltage" => "CPU 核心电压", "gpu.temperature" => "GPU 温度", "gpu.usage" => "GPU 使用率", "gpu.clock" => "GPU 核心频率", "gpu.memory" => "独显显存使用量", "memory.usage" => "系统内存使用量", _ => source };
            if (unavailable is null)
            {
                if (current is { } number && !double.IsFinite(number)) status = $"{title}读数无效（非有限数值）";
                else if (current is null && key is not ("cpu.temperature" or "cpu.clock" or "cpu.voltage" or "gpu.temperature")) status = $"当前传感器未提供{title}";
            }
            if (sample?.IsDemo == true && !status.Contains("演示数据", StringComparison.Ordinal)) status = "演示数据 · " + status;
            var points = new List<DashboardTrendPoint>(); DateTimeOffset? previous = null;
            foreach (var item in _dashboardHistory)
            {
                if (previous.HasValue && item.Timestamp - previous.Value > TimeSpan.FromMilliseconds(Math.Max(3000, Settings.Telemetry.IntervalMs * 3L))) points.Add(new(item.Timestamp.AddTicks(-1), null));
                points.Add(new(item.Timestamp, selector(item))); previous = item.Timestamp;
            }
            if (unavailable is not null && points.Count > 0) points.Add(new(DateTimeOffset.Now, null));
            result[key] = new DashboardMetricContext(device, source, status, points);
        }
    }
    private static string Format(double? value, string unit) => value.HasValue ? $"{value:F0}{unit}" : "不可用";

    public async Task ExecuteAsync(Func<Task> action, bool wait = false)
    {
        try
        {
            if (wait) await _userOperation.WaitAsync(_lifetime.Token);
            else if (!await _userOperation.WaitAsync(0)) { StatusText = "上一项操作仍在进行，请稍候。"; return; }
        }
        catch (OperationCanceledException) { return; }
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) { StatusText = "操作已取消"; }
        catch (Exception ex) { StatusText = ex.Message; _log.Write(ex.Message, "Error", ex); }
        finally { Busy = false; _userOperation.Release(); }
    }
    private static void Ui(Action action) { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action); }
    private bool _disposed;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _lifetime.Cancel(); await _background.DisposeAsync(); _lifetime.Dispose();
    }
}

