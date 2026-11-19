using System.Text.Json;
using SmartFan.App.Services;
using SmartFan.Core.Ble;
using SmartFan.Runtime;
using SmartFan.Runtime.Ipc;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var pipeName = Option("--pipe") ?? AgentPipeName.Default;
var configPath = Path.GetFullPath(Option("--config") ?? AgentConfiguration.DefaultPath);
var logDirectory = Path.Combine(Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory, "logs");
var logger = new AgentFileLogger(logDirectory);
void Log(string message) => logger.Write(message);
try
{
    if (args.Contains("--probe") || args.Contains("--sensor-audit"))
    {
        await using var client = new BackgroundClient(pipeName);
        await client.AttachAsync();
        var status = await client.GetStatusAsync();
        object result = status;
        if (args.Contains("--sensor-audit"))
        {
            var sensors = await client.EnumerateSensorsAsync();
            status = await client.GetStatusAsync();
            result = new { CapturedAt = DateTimeOffset.Now, status, configuration = await client.GetConfigurationAsync(), sensors };
        }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true });
        var output = Option("--output");
        if (output is not null) await File.WriteAllTextAsync(output, json);
        Console.WriteLine(json);
        return 0;
    }
    if (args.Contains("--stop"))
    {
        await using var client = new BackgroundClient(pipeName);
        await client.AttachAsync();
        var accepted = await client.StopAgentAsync();
        Console.WriteLine(accepted ? "后台已接受停止请求。" : "后台未接受停止请求。");
        return accepted ? 0 : 1;
    }
    if (args.Contains("--print-default-config"))
    {
        var json = JsonSerializer.Serialize(new AgentConfiguration(), new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true });
        var output = Option("--output");
        if (output is not null) await File.WriteAllTextAsync(output, json);
        Console.WriteLine(json); return 0;
    }
    using var singleInstance = new Mutex(true, "Local\\" + pipeName, out var created);
    if (!created) { Log("相同用户/管道的后台已经运行，本次启动退出，不改变原有会话。"); return 0; }
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stop.Cancel(); };
    EventHandler exitHandler = (_, _) => stop.Cancel();
    Console.CancelKeyPress += cancelHandler;
    AppDomain.CurrentDomain.ProcessExit += exitHandler;
    try
    {
        var runFor = Option("--run-for-seconds");
        if (int.TryParse(runFor, out var seconds) && seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        Log($"后台启动。配置：{configPath}；管道：{pipeName}");
        var sampler = new HardwareMonitorService(Log);
        var ble = new NativeBleClient();
        await using var host = new AgentHost(ble, sampler, new AgentConfigurationStore(configPath), Log);
        host.ShutdownRequested += (_, _) => stop.Cancel();
        await host.StartAsync(stop.Token);
        await using var server = new AgentPipeServer(pipeName, host, Log);
        await server.StartAsync(stop.Token);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Log("后台收到退出信号，正在释放蓝牙与监控资源。"); }
        return 0;
    }
    finally
    {
        // ProcessExit 在 Main 返回后发生，必须解绑，不能再访问已 Dispose 的 CTS。
        Console.CancelKeyPress -= cancelHandler;
        AppDomain.CurrentDomain.ProcessExit -= exitHandler;
    }
}
catch (Exception ex) { Log($"后台致命错误：{ex}"); Console.Error.WriteLine(ex.Message); return 1; }
