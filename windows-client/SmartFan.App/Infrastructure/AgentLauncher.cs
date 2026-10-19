using SmartFan.Runtime.Ipc;
using SmartFan.Core.Ble;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace SmartFan.App.Infrastructure;

public sealed class AgentLauncher
{
    private const string AttachAccessDeniedMessage = "已存在的后台拒绝连接，可能版本不匹配；请关闭旧后台并更新前后端后重试。";
    private readonly AppLog _log;
    public AgentLauncher(AppLog log) { _log = log; }
    private static string? _activeConfigurationPath;
    public static string ConfigurationPath => _activeConfigurationPath ?? Path.Combine(ClientPaths.DataDirectory, "agent.json");
    public static void UseActiveConfiguration(string? path) { if (!string.IsNullOrWhiteSpace(path)) _activeConfigurationPath = Path.GetFullPath(path); }
    public static void ResetActiveConfiguration() => _activeConfigurationPath = null;
    public static string FindExecutable()
    {
        var adjacent = Path.Combine(AppContext.BaseDirectory, "SmartFan.Agent.exe");
        if (File.Exists(adjacent)) return adjacent;
        // 开发时两个工程各有输出目录，发布后两个可执行文件并排放置。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory.FullName, "SmartFan.Agent", "bin", configuration, "net10.0-windows10.0.19041.0", "SmartFan.Agent.exe");
                if (File.Exists(candidate)) return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("找不到后台程序 SmartFan.Agent.exe，请先构建解决方案，或将两个客户端放在同一发布目录。");
    }
    public async Task AttachOrStartAsync(BackgroundClient client, CancellationToken token = default)
    {
        try { await client.AttachAsync(token); return; }
        catch (Exception ex) when (HasAttachAccessDenied(ex)) { throw new IOException(AttachAccessDeniedMessage, ex); }
        catch (Exception ex) when (ex is IOException or TimeoutException or BleOperationException) { System.Diagnostics.Trace.WriteLine($"后台尚未运行：{ex.Message}"); }
        var executable = FindExecutable();
        var requiresElevation = RequiresElevation(IsPawnIoInstalled(), IsFrontendAdministrator());
        _log.Write($"准备{(requiresElevation ? "以管理员权限" : "")}启动后台：{executable}；配置：{ConfigurationPath}");
        var info = CreateStartInfo(executable, ConfigurationPath, requiresElevation);
        // ShellExecute waits for UAC consent. Keep that wait off the WPF dispatcher.
        using var process = await StartProcessAsync(info, token);
        _log.Write($"后台启动 PID={process.Id}");
        var errorTask = info.RedirectStandardError ? ReadStartupErrorAsync(process.StandardError, token)
            : Task.FromResult("管理员后台的启动诊断请查看 agent.json 同目录的 logs。");
        Exception? last = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var error = await errorTask;
                _log.Write($"后台启动退出码={process.ExitCode}；{error}", "Error");
                // 另一个前端可能先完成单实例启动，最后再尝试接入一次。
                try { await client.AttachAsync(token); return; }
                catch (Exception ex) when (HasAttachAccessDenied(ex)) { throw new IOException(AttachAccessDeniedMessage, ex); }
                catch (Exception ex) when (ex is IOException or TimeoutException or BleOperationException)
                { throw new IOException($"后台启动后退出（{process.ExitCode}）：{error}。日志位于 agent.json 同目录的 logs。", ex); }
            }
            try { await client.AttachAsync(token); return; }
            catch (Exception ex) when (HasAttachAccessDenied(ex)) { throw new IOException(AttachAccessDeniedMessage, ex); }
            catch (Exception ex) when (ex is IOException or TimeoutException or BleOperationException) { last = ex; await Task.Delay(200, token); }
        }
        throw new IOException("后台程序未能就绪。请查看 agent 日志。", last);
    }

    internal static bool RequiresElevation(bool pawnIoInstalled, bool frontendIsAdministrator) => pawnIoInstalled && !frontendIsAdministrator;

    internal static bool HasAttachAccessDenied(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is UnauthorizedAccessException) return true;
        return false;
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string configurationPath, bool requiresElevation)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = requiresElevation, Verb = requiresElevation ? "runas" : "",
            CreateNoWindow = !requiresElevation, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = !requiresElevation
        };
        info.ArgumentList.Add("--config"); info.ArgumentList.Add(configurationPath);
        return info;
    }

    internal static async Task<Process> StartProcessAsync(ProcessStartInfo info, CancellationToken token = default, Func<ProcessStartInfo, Process?>? start = null)
    {
        var launch = start ?? new Func<ProcessStartInfo, Process?>(Process.Start);
        try { return await Task.Run(() => launch(info), token).ConfigureAwait(false) ?? throw new IOException("Windows 未能创建后台进程。"); }
        catch (Win32Exception ex) when (info.UseShellExecute && info.Verb.Equals("runas", StringComparison.OrdinalIgnoreCase) && ex.NativeErrorCode == 1223)
        { throw new IOException("管理员后台启动已取消，未启动新的后台进程。需要硬件采样时，请重新接入并确认 Windows 管理员提示。", ex); }
    }

    private bool IsPawnIoInstalled()
    {
        // Installer registration only. Hardware availability is diagnosed by the existing backend sampler.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key?.GetValue("DisplayVersion") is string version && !string.IsNullOrWhiteSpace(version)) return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { _log.Write($"读取 PawnIO 安装注册信息失败（{view}）：{ex.Message}", "Warning"); }
        }
        return false;
    }

    private static bool IsFrontendAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<string> ReadStartupErrorAsync(StreamReader reader, CancellationToken token)
    {
        try { return await reader.ReadToEndAsync(token); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        { System.Diagnostics.Trace.WriteLine($"后台启动诊断流已关闭：{ex.Message}"); return ""; }
    }
}
