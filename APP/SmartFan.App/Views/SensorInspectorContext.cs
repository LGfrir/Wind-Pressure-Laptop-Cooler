using SmartFan.App.Models;

namespace SmartFan.App.Views;

/// <summary>Describes sources selected by the existing sampler; the inspector never guesses sensor selection.</summary>
public sealed record SensorInspectorContext(DateTimeOffset? Timestamp, string FreshnessText, IReadOnlyDictionary<string, string> AdoptedSources, bool IsDemo = false)
{
    public static SensorInspectorContext FromSnapshot(TelemetrySnapshot? snapshot, string? freshnessText = null)
    {
        if (snapshot is null) return new(null, freshnessText ?? "等待后台首次采样", new Dictionary<string, string>());
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!snapshot.IsDemo)
            foreach (var (label, description) in snapshot.SensorNames)
            {
                var open = description.LastIndexOf('['); var close = description.LastIndexOf(']');
                if (open < 0 || close != description.Length - 1 || close <= open + 1) continue;
                var id = description[(open + 1)..close];
                if (!sources.TryGetValue(id, out var labels)) sources[id] = labels = [];
                labels.Add(label);
            }
        return new(snapshot.Timestamp, freshnessText ?? (snapshot.IsDemo ? "演示数据 · 不参与真实遥测与调速" : "后台真实采样"),
            sources.ToDictionary(pair => pair.Key, pair => string.Join(" / ", pair.Value), StringComparer.Ordinal), snapshot.IsDemo);
    }
}
