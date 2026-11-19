using System.Text;
using System.Text.Json;

namespace SmartFan.Runtime;

/// <summary>后台独立 JSONL 日志：按天分组，单文件 5 MiB，并保留 14 天。</summary>
public sealed class AgentFileLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maximumFileBytes;
    private readonly int _maximumArchives;
    private DateOnly? _lastCleanupDate;
    public AgentFileLogger(string directory, int retentionDays = 14, long maximumFileBytes = 5 * 1024 * 1024, int maximumArchives = 8)
    {
        if (retentionDays < 1 || maximumFileBytes < 512 || maximumArchives < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        _directory = Path.GetFullPath(directory); _retentionDays = retentionDays;
        _maximumFileBytes = maximumFileBytes; _maximumArchives = maximumArchives;
    }
    public void Write(string message)
    {
        var now = DateTimeOffset.Now;
        var serialized = JsonSerializer.Serialize(new { Timestamp = now, ProcessId = Environment.ProcessId, Message = message });
        var bytes = Encoding.UTF8.GetBytes(serialized + Environment.NewLine);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var date = DateOnly.FromDateTime(now.LocalDateTime);
                if (_lastCleanupDate != date) { Cleanup(now); _lastCleanupDate = date; }
                var stem = $"agent-{now:yyyyMMdd}";
                var path = Path.Combine(_directory, stem + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > _maximumFileBytes)
                {
                    var oldest = Path.Combine(_directory, $"{stem}.{_maximumArchives}.jsonl");
                    if (File.Exists(oldest)) File.Delete(oldest);
                    for (var index = _maximumArchives - 1; index >= 1; index--)
                    {
                        var source = Path.Combine(_directory, $"{stem}.{index}.jsonl");
                        if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"{stem}.{index + 1}.jsonl"));
                    }
                    File.Move(path, Path.Combine(_directory, stem + ".1.jsonl"));
                }
                // 单条异常也可能极大，截断为一条仍然有效的 JSON，避免突破文件上限。
                if (bytes.Length > _maximumFileBytes)
                {
                    var capped = message[..Math.Min(message.Length, (int)(_maximumFileBytes / 12))] + " [日志过长，已截断]";
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Timestamp = now, ProcessId = Environment.ProcessId, Message = capped }) + Environment.NewLine);
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"后台日志保存失败：{ex.Message}；原消息：{message}"); }
        }
    }
    private void Cleanup(DateTimeOffset now)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "agent-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Length < 14 || !DateOnly.TryParseExact(name.AsSpan(6, 8), "yyyyMMdd", out var fileDate)) continue;
            if (fileDate < DateOnly.FromDateTime(now.LocalDateTime).AddDays(-(_retentionDays - 1))) File.Delete(path);
        }
    }
}
