using Microsoft.Win32;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SmartFan.App.Infrastructure;

public sealed class DesktopService : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private readonly AppLog _log;
    public DesktopService(AppLog log) { _log = log; }
    public void Attach(Window window, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 SmartFan", null, (_, _) => Show(window));
        menu.Items.Add("关闭控制面板（后台继续运行）", null, (_, _) => window.Dispatcher.Invoke(exit));
        _icon = new Forms.NotifyIcon { Text = "SmartFan · 控制面板", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => Show(window);
    }
    private static void Show(Window window) => window.Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
    public void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("SmartFanNext.Agent", $"\"{AgentLauncher.FindExecutable()}\" --config \"{AgentLauncher.ConfigurationPath}\"");
        else key.DeleteValue("SmartFanNext.Agent", false);
        _log.Write(enabled ? "已启用后台程序在当前用户登录时启动" : "已关闭后台程序登录启动");
    }
    public void Dispose() { if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); _icon = null; } }
}
