using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace SmartFan.App.Infrastructure;

public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _lines = new();
    public string DirectoryPath { get; } = Path.Combine(ClientPaths.DataDirectory, "frontend-logs");
    public string CurrentFilePath => Path.Combine(DirectoryPath, $"smartfan-{DateTime.Today:yyyyMMdd}.jsonl");
    public string MinimumLevel { get; set; } = "Info";
    public event Action<string>? Added;
    public string RecentText => string.Join(Environment.NewLine, _lines);

    public void Write(string message, string level = "Info", Exception? exception = null)
    {
        if (MinimumLevel == "Error" && level != "Error") return;
        if (MinimumLevel == "Warning" && level == "Info") return;
        var line = $"{DateTimeOffset.Now:HH:mm:ss} [{level}] {message}";
        _lines.Enqueue(line);
        while (_lines.Count > 180) _lines.TryDequeue(out _);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(CurrentFilePath, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, level, message, detail = exception?.ToString() }) + Environment.NewLine);
                // 每日文件保留两周，避免常驻应用无限占用磁盘。
                foreach (var old in Directory.EnumerateFiles(DirectoryPath, "smartfan-*.jsonl").Where(p => File.GetLastWriteTime(p) < DateTime.Now.AddDays(-14))) File.Delete(old);
            }
        }
        catch (IOException ex) { System.Diagnostics.Trace.WriteLine($"日志写入失败：{ex.Message}"); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.WriteLine($"日志访问失败：{ex.Message}"); }
        Added?.Invoke(line);
    }

    public async Task ExportAsync(string path)
    {
        string[] files;
        lock (_gate) files = Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath, "*.jsonl") : [];
        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        foreach (var file in files.Order()) await writer.WriteAsync(await File.ReadAllTextAsync(file));
    }
}
