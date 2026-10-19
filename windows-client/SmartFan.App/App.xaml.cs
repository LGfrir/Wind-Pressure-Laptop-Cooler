using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartFan.App.Infrastructure;
using SmartFan.App.Services;
using SmartFan.App.ViewModels;
using SmartFan.Core.Ble;
using SmartFan.Runtime.Ipc;
using System.Windows;

namespace SmartFan.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private AppLog? _log;
    private bool _exiting;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ClientPaths.Initialize(e.Args);
        _log = new AppLog();
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write("界面异常", "Error", args.Exception);
            System.Windows.MessageBox.Show($"{args.Exception.Message}\n\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n错误日志：{_log.CurrentFilePath}", "SmartFan 出错了");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Write("进程未处理异常", "Error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { _log.Write("异步任务异常", "Error", args.Exception); args.SetObserved(); Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show("后台任务发生异常，详情已记录到日志。", "SmartFan")); };
        try
        {
            _host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            {
                services.AddSingleton(_log);
                services.AddSingleton<BackgroundClient>();
                services.AddSingleton(new SettingsStore(ClientPaths.DataDirectory, !ClientPaths.IsPortable));
                services.AddSingleton<AppearanceService>(); services.AddSingleton<DesktopService>();
                services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>();
            }).Build();
            await _host.StartAsync();
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            _host.Services.GetRequiredService<DesktopService>().Attach(window, () => _ = ExitAsync());
            window.Show();
            await vm.ExecuteAsync(vm.AttachBackendAsync);
            if (e.Args.Contains("--tray")) window.Hide();
        }
        catch (Exception ex) { _log.Write("启动失败", "Error", ex); System.Windows.MessageBox.Show($"启动失败：{ex.Message}\n日志目录：{_log.DirectoryPath}", "SmartFan"); Shutdown(1); }
    }

    public async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        if (_host is not null)
        {
            try { await _host.Services.GetRequiredService<MainWindow>().SaveWindowAsync(); }
            catch (Exception ex) { _log?.Write("退出时保存设置失败", "Error", ex); }
            _host.Services.GetRequiredService<DesktopService>().Dispose();
            try { await _host.Services.GetRequiredService<MainViewModel>().DisposeAsync(); await _host.StopAsync(); }
            catch (Exception ex) { _log?.Write("退出清理失败", "Error", ex); }
            // 服务由 Host 统一异步释放；MainViewModel 的清理具备幂等性。
            if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync(); else _host.Dispose();
        }
        Shutdown();
    }
}
