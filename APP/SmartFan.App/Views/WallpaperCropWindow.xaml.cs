using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SmartFan.App.Views;

public partial class WallpaperCropWindow : Window
{
    private readonly BitmapImage _bitmap;
    private readonly string _wallpaperPath;
    private Rect _crop;
    private Point? _dragStart;

    public WallpaperCropWindow(
        string wallpaperPath,
        double cropX,
        double cropY,
        double cropWidth,
        double cropHeight)
    {
        InitializeComponent();
        _wallpaperPath = wallpaperPath;

        _bitmap = new BitmapImage();
        _bitmap.BeginInit();
        _bitmap.UriSource = new Uri(wallpaperPath);
        _bitmap.CacheOption = BitmapCacheOption.OnLoad;
        _bitmap.EndInit();
        _bitmap.Freeze();

        WallpaperPreview.Source = _bitmap;
        _crop = new Rect(
            Math.Clamp(cropX, 0, 0.98),
            Math.Clamp(cropY, 0, 0.98),
            Math.Clamp(cropWidth, 0.02, 1),
            Math.Clamp(cropHeight, 0.02, 1));
        NormalizeCrop();
    }

    public double CropX => _crop.X;

    public double CropY => _crop.Y;

    public double CropWidth => _crop.Width;

    public double CropHeight => _crop.Height;

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutImageAndCrop();
    }

    private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PreviewCanvas);
        PreviewCanvas.CaptureMouse();
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(PreviewCanvas);
        var imageRect = GetImageRect();
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
        {
            return;
        }

        var start = ClampToRect(_dragStart.Value, imageRect);
        var end = ClampToRect(current, imageRect);
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(8, Math.Abs(start.X - end.X));
        var height = Math.Max(8, Math.Abs(start.Y - end.Y));

        _crop = new Rect(
            (left - imageRect.X) / imageRect.Width,
            (top - imageRect.Y) / imageRect.Height,
            width / imageRect.Width,
            height / imageRect.Height);
        NormalizeCrop();
        LayoutCropRectangle(imageRect);
    }

    private void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        PreviewCanvas.ReleaseMouseCapture();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void LayoutImageAndCrop()
    {
        var imageRect = GetImageRect();
        Canvas.SetLeft(WallpaperPreview, imageRect.X);
        Canvas.SetTop(WallpaperPreview, imageRect.Y);
        WallpaperPreview.Width = imageRect.Width;
        WallpaperPreview.Height = imageRect.Height;
        LayoutCropRectangle(imageRect);
    }

    private void LayoutCropRectangle(Rect imageRect)
    {
        Canvas.SetLeft(CropRectangle, imageRect.X + _crop.X * imageRect.Width);
        Canvas.SetTop(CropRectangle, imageRect.Y + _crop.Y * imageRect.Height);
        CropRectangle.Width = _crop.Width * imageRect.Width;
        CropRectangle.Height = _crop.Height * imageRect.Height;
        CropInfoText.Text = $"当前边界：X {_crop.X:P0} / Y {_crop.Y:P0} / 宽 {_crop.Width:P0} / 高 {_crop.Height:P0}";
    }

    private Rect GetImageRect()
    {
        var canvasWidth = PreviewCanvas.ActualWidth;
        var canvasHeight = PreviewCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0 || _bitmap.PixelWidth <= 0 || _bitmap.PixelHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(canvasWidth / _bitmap.PixelWidth, canvasHeight / _bitmap.PixelHeight);
        var width = _bitmap.PixelWidth * scale;
        var height = _bitmap.PixelHeight * scale;
        return new Rect((canvasWidth - width) / 2, (canvasHeight - height) / 2, width, height);
    }

    private void NormalizeCrop()
    {
        var x = Math.Clamp(_crop.X, 0, 0.98);
        var y = Math.Clamp(_crop.Y, 0, 0.98);
        var width = Math.Clamp(_crop.Width, 0.02, 1 - x);
        var height = Math.Clamp(_crop.Height, 0.02, 1 - y);
        _crop = new Rect(x, y, width, height);
    }

    private static Point ClampToRect(Point point, Rect rect)
    {
        return new Point(
            Math.Clamp(point.X, rect.Left, rect.Right),
            Math.Clamp(point.Y, rect.Top, rect.Bottom));
    }
}

