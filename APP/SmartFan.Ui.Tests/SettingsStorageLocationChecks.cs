using System.IO;
using SmartFan.App.Infrastructure;

namespace SmartFan.Ui.Tests;

internal static class SettingsStorageLocationChecks
{
    public static void Run(Action<bool, string> check)
    {
        // The test output is in the workspace; never probe production roaming directories.
        var directory = Path.Combine(AppContext.BaseDirectory, "storage-location-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "配置-σ.json");
        const string payload = "isolated settings location check";
        try
        {
            File.WriteAllText(path, payload);
            var location = SettingsStorageLocation.Resolve(path);
            check(location.IsResolved && string.Equals(location.RequestedPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
                "settings storage diagnostics resolve an existing Unicode workspace file through its read-only OS handle");
            check(string.Equals(location.ActualPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.ReadAllText(location.ActualPath) == payload,
                "the resolved physical path points to the actual isolated workspace file and preserves its content");
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
            var relativeLocation = SettingsStorageLocation.Resolve(relative);
            check(relativeLocation.IsResolved && string.Equals(relativeLocation.ActualPath, location.ActualPath, StringComparison.OrdinalIgnoreCase),
                "relative diagnostic requests resolve to the same actual opened file");
            var missingPath = Path.Combine(directory, "not-created", "settings.json");
            var missing = SettingsStorageLocation.Resolve(missingPath);
            check(!missing.IsResolved && missing.RequestedPath == Path.GetFullPath(missingPath) && missing.ActualPath == missing.RequestedPath && !Directory.Exists(Path.GetDirectoryName(missingPath)),
                "missing settings gracefully report an unresolved requested path without creating directories or files");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var unavailable = SettingsStorageLocation.Resolve(path);
                check(!unavailable.IsResolved && unavailable.ActualPath == Path.GetFullPath(path),
                    "an unreadable locked settings file gracefully retains the requested path without changing sharing or permissions");
            }
            check(File.ReadAllText(path) == payload && Directory.GetFiles(directory).Length == 1,
                "location inspection never changes settings content or creates sidecar files");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
