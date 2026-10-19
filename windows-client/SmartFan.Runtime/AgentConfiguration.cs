using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFan.App.Models;
using SmartFan.App.Services;
using SmartFan.Core.Ble;

namespace SmartFan.Runtime;

/// <summary>后台专用启动参数，与窗口外观文件独立。默认只采样、不连接、不上报。</summary>
public sealed class AgentConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public bool EnabledTelemetry { get; set; }
    public bool AutoConnect { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectInitialSeconds { get; set; } = 2;
    public BleDeviceInfo? SavedDevice { get; set; }
    public bool DemoMode { get; set; }
    public bool AutoUploadConfiguration { get; set; }
    public DeviceProfile? AutoUploadProfile { get; set; }
    public TelemetrySettings Telemetry { get; set; } = new();
    public string LogLevel { get; set; } = "Information";
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartFanNext", "agent.json");

    public static void Validate(AgentConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1) throw new InvalidDataException("不支持的后台配置版本。");
        if (configuration.ReconnectInitialSeconds is < 1 or > 60) throw new InvalidDataException("后台重连间隔必须在 1～60 秒。");
        if (configuration.Telemetry is null) throw new InvalidDataException("后台缺少 Telemetry 配置。");
        if (configuration.DemoMode && configuration.EnabledTelemetry) throw new InvalidDataException("演示模式不能开启真实硬件上报。");
        if (configuration.AutoUploadConfiguration && configuration.AutoUploadProfile is null) throw new InvalidDataException("自动同步需要明确保存的设备场景。");
        if (configuration.AutoUploadProfile is not null)
        {
            var profileErrors = SettingsValidator.ValidateProfile(configuration.AutoUploadProfile);
            if (profileErrors.Count > 0) throw new InvalidDataException(string.Join("；", profileErrors));
        }
        if (configuration.LogLevel is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical")) throw new InvalidDataException("未知后台日志级别。");
        if (configuration.AutoConnect && (configuration.SavedDevice is null || configuration.SavedDevice.BluetoothAddress == 0 && string.IsNullOrWhiteSpace(configuration.SavedDevice.Id)))
            throw new InvalidDataException("自动连接需要保存的设备地址或 Id。");
        var errors = SettingsValidator.Validate(new AppSettings { Telemetry = configuration.Telemetry });
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
    }
}

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options) ?? throw new InvalidDataException("无法复制后台配置。");
}

public sealed class AgentConfigurationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public AgentConfigurationStore(string path) => FilePath = Path.GetFullPath(path);
    public string FilePath { get; }
    public async Task<AgentConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new AgentConfiguration();
        if (new FileInfo(FilePath).Length > 1024 * 1024) throw new InvalidDataException("后台配置超过 1 MiB。");
        var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("后台配置缺少 SchemaVersion。");
        var configuration = JsonSerializer.Deserialize<AgentConfiguration>(json, AgentJson.Options) ?? throw new InvalidDataException("后台配置为空。");
        AgentConfiguration.Validate(configuration);
        configuration.Telemetry.Enabled = configuration.EnabledTelemetry;
        return configuration;
    }
    public async Task SaveAsync(AgentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        AgentConfiguration.Validate(configuration);
        var copy = AgentJson.Clone(configuration);
        copy.Telemetry.Enabled = copy.EnabledTelemetry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath) ?? throw new InvalidDataException("后台配置路径无效。");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            var options = new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true };
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, copy, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true); else File.Move(temporary, FilePath);
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
