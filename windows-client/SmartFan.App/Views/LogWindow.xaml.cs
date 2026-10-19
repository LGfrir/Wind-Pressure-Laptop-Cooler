using SmartFan.App.Infrastructure;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SmartFan.App.Views;

/// <summary>按需订阅日志的独立诊断视图；关闭后不再刷新日志文本。</summary>
public partial class LogWindow : Window
{
    private readonly AppLog _log;
    private readonly Window _monitoredWindow;
    private bool _closed;

    public LogWindow(AppLog log, Window monitoredWindow)
    {
        _log = log; _monitoredWindow = monitoredWindow;
        InitializeComponent();
        if (new System.Windows.Interop.WindowInteropHelper(monitoredWindow).Handle != IntPtr.Zero) Owner = monitoredWindow;
        FontSize = monitoredWindow.FontSize;
        _log.Added += LogAdded;
        RefreshText();
        Closed += (_, _) => { _closed = true; _log.Added -= LogAdded; };
    }

    private void LogAdded(string line)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!_closed) RefreshText(); });
    }

    private void RefreshText()
    {
        var followEnd = LogContent.VerticalOffset >= LogContent.ExtentHeight - LogContent.ViewportHeight - 4;
        LogContent.Text = _log.RecentText;
        LogFilePath.Text = _log.CurrentFilePath;
        if (followEnd) LogContent.ScrollToEnd();
    }

    private void OpenFrontendLogs(object sender, RoutedEventArgs e) => OpenDirectory(_log.DirectoryPath);
    private void OpenBackendLogs(object sender, RoutedEventArgs e) => OpenDirectory(Path.Combine(Path.GetDirectoryName(AgentLauncher.ConfigurationPath)!, "logs"));
    private void OpenDirectory(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ReportFailure("打开日志目录失败", ex); }
    }

    private async void ExportFrontendLogs(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON 日志|*.jsonl", FileName = "SmartFan-diagnostics.jsonl" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _log.ExportAsync(dialog.FileName); DiagnosticsStatus.Text = $"已导出：{dialog.FileName}"; }
        catch (Exception ex) { ReportFailure("导出日志失败", ex); }
    }

    private async void MeasureRendering(object sender, RoutedEventArgs e)
    {
        MeasureButton.IsEnabled = false;
        try
        {
            DiagnosticsStatus.Text = "测量主窗口 15 秒渲染回调与调度延迟，可返回主窗口切换页面。此数值不是 GPU 呈现 FPS。";
            var probe = RenderPerformanceProbe.Start(_monitoredWindow, _log, TimeSpan.FromSeconds(15), "interactive");
            await probe.Completion;
            if (!_closed) DiagnosticsStatus.Text = $"流畅度诊断已保存：{probe.SummaryPath}";
        }
        catch (Exception ex) { ReportFailure("流畅度诊断失败", ex); }
        finally { if (!_closed) MeasureButton.IsEnabled = true; }
    }

    private void ReportFailure(string message, Exception exception)
    {
        _log.Write(message, "Error", exception);
        if (!_closed) DiagnosticsStatus.Text = $"{message}：{exception.Message}";
    }
}
