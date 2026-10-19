using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartFan.App.Infrastructure;

/// <summary>Reports the file actually opened by this process, including Windows package redirection.
/// Inspection is read-only and never creates a directory or changes a storage policy.</summary>
public sealed record SettingsStorageLocation(string RequestedPath, string ActualPath, bool IsResolved)
{
    public static SettingsStorageLocation Resolve(string requestedPath)
    {
        var absolute = Path.GetFullPath(requestedPath);
        var fallback = new SettingsStorageLocation(absolute, absolute, false);
        try
        {
            using var handle = File.OpenHandle(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return fallback;
            if (length >= buffer.Capacity)
            {
                if (length > 32768) return fallback;
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return fallback;
            }
            var actual = buffer.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            return string.IsNullOrWhiteSpace(actual) ? fallback : new(absolute, actual, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { return fallback; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
