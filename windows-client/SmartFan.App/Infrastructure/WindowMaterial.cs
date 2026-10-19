using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SmartFan.App.Infrastructure;

internal static class WindowMaterial
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int useDark = dark ? 1 : 0;
        int rounded = 2;
        // 较旧系统不支持的属性会返回 HRESULT；面板本身仍使用 WPF 的玻璃材质作为回退。
        int result = DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
        int cornerResult = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        if (result < 0 || cornerResult < 0) System.Diagnostics.Trace.WriteLine($"DWM 材质回退：{result:X8}/{cornerResult:X8}");
    }
}
