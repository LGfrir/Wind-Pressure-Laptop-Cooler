using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SmartFan.App.Infrastructure;
using SmartFan.Core.Ble;

namespace SmartFan.Ui.Tests;

/// <summary>Pure process policy and injected launch failures; never launches a process, UAC, pipe or sampler.</summary>
internal static class AgentLaunchPolicyChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string executable = @"C:\SmartFan App\SmartFan.Agent.exe";
        const string configuration = @"D:\Smart Fan Data\参数目录\agent.json";
        var ordinary = Create(executable, configuration, false);
        var elevated = Create(executable, configuration, true);
        check(!ordinary.UseShellExecute && ordinary.CreateNoWindow && ordinary.WindowStyle == ProcessWindowStyle.Hidden && ordinary.RedirectStandardError && ordinary.Verb.Length == 0,
            "ordinary backend startup remains hidden with redirected stderr and no elevation verb");
        check(elevated.UseShellExecute && !elevated.CreateNoWindow && elevated.WindowStyle == ProcessWindowStyle.Hidden && !elevated.RedirectStandardError && elevated.Verb == "runas",
            "elevated backend startup uses hidden ShellExecute runas without unsupported stream redirection");
        check(new[] { ordinary, elevated }.All(info => info.FileName == executable && info.ArgumentList.SequenceEqual(new[] { "--config", configuration }) && info.Arguments.Length == 0),
            "both launch modes preserve a spaced executable and Unicode configuration path as two unquoted ArgumentList entries");
        check(!Policy(false, false) && !Policy(false, true) && Policy(true, false) && !Policy(true, true),
            "only an installed PawnIO dependency combined with a nonadministrator frontend requires background elevation");
        check(AccessDenied(new BleOperationException("attach rejected", new IOException("pipe security", new UnauthorizedAccessException("owner mismatch"))))
              && AccessDenied(new UnauthorizedAccessException("denied")),
            "direct and nested pipe identity failures are detected before a replacement backend or UAC prompt is started");
        check(!AccessDenied(new BleOperationException("not running", new TimeoutException())) && !AccessDenied(new IOException("pipe not found")),
            "an absent or timed-out pipe still permits the existing normal startup path");

        var callerThread = Environment.CurrentManagedThreadId; var launchThread = callerThread; var calls = 0;
        var cancellation = Failure(elevated, _ =>
        {
            calls++; launchThread = Environment.CurrentManagedThreadId; throw new Win32Exception(1223);
        });
        check(cancellation is IOException { InnerException: Win32Exception { NativeErrorCode: 1223 } } && cancellation.Message.Contains("管理员后台启动已取消", StringComparison.Ordinal),
            "a declined UAC prompt produces a specific actionable cancellation message rather than an ordinary startup retry");
        check(calls == 1 && launchThread != callerThread,
            "UAC startup and its cancellation run on a worker once without blocking or relaunching from the UI thread");

        var denied = Failure(elevated, _ => throw new Win32Exception(5));
        check(denied is Win32Exception { NativeErrorCode: 5 },
            "unrelated process access failures keep their original diagnostic instead of being mislabeled as UAC cancellation");
        var missing = Failure(ordinary, _ => null);
        check(missing is IOException && missing.Message.Contains("未能创建后台进程", StringComparison.Ordinal),
            "a launch that returns no process has an explicit startup failure");
    }

    private static MethodInfo Method(string name) => typeof(AgentLauncher).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException($"AgentLauncher.{name}");
    private static ProcessStartInfo Create(string executable, string config, bool elevated) => (ProcessStartInfo)Method("CreateStartInfo").Invoke(null, [executable, config, elevated])!;
    private static bool Policy(bool installed, bool administrator) => (bool)Method("RequiresElevation").Invoke(null, [installed, administrator])!;
    private static bool AccessDenied(Exception error) => (bool)Method("HasAttachAccessDenied").Invoke(null, [error])!;
    private static Exception Failure(ProcessStartInfo info, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            var task = (Task<Process>)Method("StartProcessAsync").Invoke(null, [info, CancellationToken.None, start])!;
            using var process = task.GetAwaiter().GetResult();
            throw new InvalidOperationException("The injected launch fixture unexpectedly returned a process.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception) { return ex; }
    }
}
