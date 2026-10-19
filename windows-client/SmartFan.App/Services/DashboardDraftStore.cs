using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFan.App.Controls;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

/// <summary>Crash recovery is separate from committed UI settings and bound to its original layout.</summary>
public sealed class DashboardDraftStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "dashboard-draft.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private sealed record Envelope(int Version, DashboardLayoutSettings Baseline, DashboardLayoutSettings Draft);
    public async Task<DashboardLayoutSettings?> LoadAsync(DashboardLayoutSettings currentBaseline)
    {
        if (!File.Exists(_path)) return null;
        var file = new FileInfo(_path); if (file.Length > 1024 * 1024) throw new InvalidDataException("组件草稿文件过大，原文件已保留。");
        var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(_path).ConfigureAwait(false), Options);
        if (envelope is null || envelope.Version != 1 || DashboardLayoutEngine.Validate(envelope.Baseline).Count != 0 || DashboardLayoutEngine.Validate(envelope.Draft).Count != 0)
            throw new InvalidDataException("组件草稿格式无效，原文件已保留。");
        return DashboardEditHistory.Equivalent(envelope.Baseline, currentBaseline) ? envelope.Draft.Clone() : null;
    }
    public async Task SaveAsync(DashboardLayoutSettings baseline, DashboardLayoutSettings? draft)
    {
        if (DashboardLayoutEngine.Validate(baseline).Count != 0 || draft is not null && DashboardLayoutEngine.Validate(draft).Count != 0)
            throw new InvalidDataException("拒绝保存无效的组件草稿。");
        var json = draft is null || DashboardEditHistory.Equivalent(baseline, draft) ? null : JsonSerializer.Serialize(new Envelope(1, baseline.Clone(), draft.Clone()), Options);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (json is null) { if (File.Exists(_path)) File.Delete(_path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}
