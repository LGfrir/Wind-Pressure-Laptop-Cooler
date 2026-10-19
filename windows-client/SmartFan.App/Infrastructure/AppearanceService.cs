using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.IO;

namespace SmartFan.App.Infrastructure;

public sealed class AppearanceService
{
    private static string? _wallpaperKey;
    private static ImageSource? _cachedWallpaper;
    private ResourceDictionary? _current;
    public void Apply(string name, string? accent = null)
    {
        if (name == "System")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            name = key?.GetValue("AppsUseLightTheme") is int n && n == 1 ? "Light" : "Nebula";
        }
        if (name is not ("Nebula" or "Light" or "Cyber" or "Graphite")) throw new ArgumentException("未知主题名称。");
        var next = new ResourceDictionary { Source = new Uri($"Themes/{name}.xaml", UriKind.Relative) };
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_current is not null) dictionaries.Remove(_current);
        else if (dictionaries.Count > 0) dictionaries.RemoveAt(0);
        dictionaries.Insert(0, next);
        _current = next;
        System.Windows.Application.Current.Resources.Remove("AccentBrush");
        if (!string.IsNullOrWhiteSpace(accent))
        {
            var color = (Color)ColorConverter.ConvertFromString(accent);
            System.Windows.Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
        }
    }

    public static ImageSource? LoadWallpaper(string path, double x, double y, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string cacheKey = FormattableString.Invariant($"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{x}|{y}|{width}|{height}");
        if (_wallpaperKey == cacheKey && _cachedWallpaper is not null) return _cachedWallpaper;
        var image = new BitmapImage();
        image.BeginInit(); image.UriSource = new Uri(path, UriKind.Absolute); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 960; image.EndInit(); image.Freeze();
        int left = (int)(Math.Clamp(x, 0, .98) * image.PixelWidth);
        int top = (int)(Math.Clamp(y, 0, .98) * image.PixelHeight);
        int w = Math.Clamp((int)(width * image.PixelWidth), 1, image.PixelWidth - left);
        int h = Math.Clamp((int)(height * image.PixelHeight), 1, image.PixelHeight - top);
        var cropped = new CroppedBitmap(image, new Int32Rect(left, top, w, h)); cropped.Freeze();
        // 背景是静态材质：只在选图/裁剪变化时做一次模糊，正常滚动时只合成已冻结的纹理。
        double scale = Math.Min(1, Math.Min(960d / w, 720d / h));
        int renderWidth = Math.Max(1, (int)(w * scale)), renderHeight = Math.Max(1, (int)(h * scale));
        var material = new Image { Source = cropped, Width = renderWidth, Height = renderHeight,
            Effect = new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance }, Stretch = Stretch.Fill };
        material.Measure(new Size(renderWidth, renderHeight)); material.Arrange(new Rect(0, 0, renderWidth, renderHeight));
        var frozen = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
        frozen.Render(material); frozen.Freeze();
        _wallpaperKey = cacheKey; _cachedWallpaper = frozen;
        return frozen;
    }
}
