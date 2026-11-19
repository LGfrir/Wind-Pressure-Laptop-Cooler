using System.IO;

namespace SmartFan.App.Infrastructure;

public static class ClientPaths
{
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartFanNext");
    public static bool IsPortable { get; private set; }
    public static void Initialize(string[] arguments)
    {
        int index = Array.IndexOf(arguments, "--data-dir");
        if (index < 0) return;
        if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1])) throw new ArgumentException("--data-dir 后需要指定配置目录。");
        DataDirectory = Path.GetFullPath(arguments[index + 1]);
        IsPortable = true;
    }
}
