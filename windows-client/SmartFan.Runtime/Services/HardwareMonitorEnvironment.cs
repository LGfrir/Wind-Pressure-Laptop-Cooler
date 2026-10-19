using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SmartFan.App.Models;

namespace SmartFan.App.Services;

internal static class HardwareMonitorEnvironment
{
    public static HardwareMonitorDiagnostics Capture()
    {
        string? version = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                version = key?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(version)) break;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        // Match LHM's access request; open/close only, without issuing a driver command.
        using var handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000,
            3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        var error = handle.IsInvalid ? Marshal.GetLastWin32Error() : (int?)null;
        return new HardwareMonitorDiagnostics
        {
            ProviderVersion = typeof(Computer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Computer).Assembly.GetName().Version?.ToString() ?? "未知",
            IsAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            PawnIoInstalled = !string.IsNullOrWhiteSpace(version), PawnIoVersion = version,
            PawnIoDeviceAccessible = !handle.IsInvalid, PawnIoDeviceErrorCode = error
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
