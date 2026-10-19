using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>同目录临时文件 + 原子替换，写入中断不会留下半份 JSON。</summary>
public sealed class SettingsStore
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _migrateOldPreferences;
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public SettingsStore(string? directory = null, bool migrateOldPreferences = true)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartFanNext");
        _migrateOldPreferences = migrateOldPreferences;
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartFan", "settings.json");
            if (_migrateOldPreferences && File.Exists(oldPath))
            {
                try
                {
                    await MigrateAppearanceAsync(oldPath, defaults, cancellationToken).ConfigureAwait(false);
                    warnings.Add("已从旧版 SmartFan 读取外观偏好；旧文件未改动。旧 BLE UUID 和百分比曲线未迁移。");
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
                {
                    warnings.Add($"旧版外观偏好未迁移：{ex.Message}");
                }
            }
            return new SettingsLoadResult { Settings = defaults, Warnings = warnings };
        }
        try
        {
            var settings = await ReadSettingsAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            return new SettingsLoadResult { Settings = settings, Warnings = warnings };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"设置加载失败，当前使用默认值；原文件保留在 {SettingsPath}：{ex.Message}");
            return new SettingsLoadResult { Settings = new AppSettings(), Warnings = warnings };
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => WriteSettingsAsync(SettingsPath, settings, cancellationToken);
    public Task ExportAsync(string path, AppSettings settings, CancellationToken cancellationToken = default) => WriteSettingsAsync(path, settings, cancellationToken);
    public Task<AppSettings> ImportAsync(string path, CancellationToken cancellationToken = default) => ReadSettingsAsync(path, cancellationToken);

    public async Task ExportProfileAsync(string path, DeviceProfile profile, CancellationToken cancellationToken = default)
    {
        ThrowIfInvalid(SettingsValidator.ValidateProfile(profile));
        var envelope = new ProfileEnvelope { Profile = profile };
        await WriteAtomicAsync(path, JsonSerializer.Serialize(envelope, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceProfile> ImportProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        string json = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("场景文件缺少 SchemaVersion。");
        var envelope = JsonSerializer.Deserialize<ProfileEnvelope>(json, JsonOptions);
        if (envelope?.SchemaVersion != AppSettings.CurrentSchemaVersion || envelope.Profile is null)
            throw new InvalidDataException("场景文件版本或内容无效。");
        ThrowIfInvalid(SettingsValidator.ValidateProfile(envelope.Profile));
        return envelope.Profile;
    }

    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("无法复制设置。");

    private async Task WriteSettingsAsync(string path, AppSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfInvalid(SettingsValidator.Validate(settings));
        await WriteAtomicAsync(path, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AppSettings> ReadSettingsAsync(string path, CancellationToken cancellationToken)
    {
        string json = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        // 缺少 SchemaVersion 的新目录文件不能悄悄当成当前格式。
        using var document = JsonDocument.Parse(json);
        bool hasVersion = document.RootElement.ValueKind == JsonValueKind.Object &&
                          document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase));
        if (!hasVersion) throw new InvalidDataException("设置文件缺少 SchemaVersion。");
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new InvalidDataException("设置文件为空。");
        ThrowIfInvalid(SettingsValidator.Validate(settings));
        return settings;
    }

    private async Task WriteAtomicAsync(string path, string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("无效保存路径。");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, fullPath + ".bak", true);
            else File.Move(temporary, fullPath);
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _writeGate.Release();
        }
    }

    private static async Task<string> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("设置文件超过 2 MiB 限制。");
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfInvalid(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static async Task MigrateAppearanceAsync(string path, AppSettings settings, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false));
        var old = JsonSerializer.Deserialize<AppearanceSettings>(document.RootElement.GetRawText(), JsonOptions);
        if (old is null) return;
        // 旧版曾把 Cascadia Code 误作全局字体，迁移时使用中文 UI 字体。
        if (old.UiFontFamily == "Cascadia Code") old.UiFontFamily = "Microsoft YaHei UI";
        var candidate = new AppSettings { Appearance = old };
        ThrowIfInvalid(SettingsValidator.Validate(candidate));
        settings.Appearance = old;
    }

    private sealed class ProfileEnvelope
    {
        public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;
        public DeviceProfile? Profile { get; set; }
    }
}
