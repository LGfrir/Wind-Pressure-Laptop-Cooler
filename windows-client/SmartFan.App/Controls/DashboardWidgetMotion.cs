using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartFan.App.Controls;

/// <summary>Pointer movement touches render transforms only. Layout changes happen at grid transitions
/// or on release. Retargeted animations start from the current visual position.</summary>
public sealed class DashboardWidgetMotion
{
    private readonly Border _element;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translation = new();
    private CacheMode? _previousCache;
    private int _motionVersion;
    public bool IsFollowing { get; private set; }
    public DashboardWidgetMotion(Border element)
    {
        _element = element;
        var transforms = new TransformGroup(); transforms.Children.Add(_scale); transforms.Children.Add(_translation);
        element.RenderTransform = transforms;
    }
    public Rect VisualBounds => new(CanvasCoordinate(Canvas.GetLeft(_element)) + _translation.X,
        CanvasCoordinate(Canvas.GetTop(_element)) + _translation.Y,
        Math.Max(1, Width * _scale.ScaleX), Math.Max(1, Height * _scale.ScaleY));
    private double Width => double.IsFinite(_element.Width) ? _element.Width : Math.Max(1, _element.ActualWidth);
    private double Height => double.IsFinite(_element.Height) ? _element.Height : Math.Max(1, _element.ActualHeight);
    private static double CanvasCoordinate(double value) => double.IsFinite(value) ? value : 0;
    public void BeginFollowing()
    {
        var visual = VisualBounds; StopAnimations(); SetVisualBounds(visual);
        if (!IsFollowing) _previousCache = _element.CacheMode;
        _element.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        IsFollowing = true;
    }
    public void EndFollowing()
    {
        if (!IsFollowing) return;
        _element.CacheMode = _previousCache; _previousCache = null; IsFollowing = false;
    }
    public void SetVisualBounds(Rect visual)
    {
        _scale.ScaleX = visual.Width / Width; _scale.ScaleY = visual.Height / Height;
        _translation.X = visual.X - CanvasCoordinate(Canvas.GetLeft(_element));
        _translation.Y = visual.Y - CanvasCoordinate(Canvas.GetTop(_element));
    }
    public void SetTarget(Rect target, bool animate, bool force = false)
    {
        var oldLeft = Canvas.GetLeft(_element); var oldTop = Canvas.GetTop(_element);
        if (!force && oldLeft == target.X && oldTop == target.Y && Width == target.Width && Height == target.Height) return;
        var visual = VisualBounds; var hadBounds = double.IsFinite(oldLeft) && double.IsFinite(oldTop);
        StopAnimations();
        if (oldLeft != target.X) Canvas.SetLeft(_element, target.X);
        if (oldTop != target.Y) Canvas.SetTop(_element, target.Y);
        if (Width != target.Width || !double.IsFinite(_element.Width)) _element.Width = target.Width;
        if (Height != target.Height || !double.IsFinite(_element.Height)) _element.Height = target.Height;
        _scale.ScaleX = _scale.ScaleY = 1; _translation.X = _translation.Y = 0;
        if (!animate || !hadBounds || !SystemParameters.ClientAreaAnimation) return;
        Animate(_translation, TranslateTransform.XProperty, visual.X - target.X, 0);
        Animate(_translation, TranslateTransform.YProperty, visual.Y - target.Y, 0);
        Animate(_scale, ScaleTransform.ScaleXProperty, visual.Width / target.Width, 1);
        Animate(_scale, ScaleTransform.ScaleYProperty, visual.Height / target.Height, 1);
    }
    private void Animate(Animatable target, DependencyProperty property, double from, double to)
    {
        if (Math.Abs(from - to) < .0001) return;
        var version = _motionVersion;
        // A newly attached WPF clock may not tick until the next render. Keep its starting pose
        // as the base value so a second input in that interval cannot see the final pose early.
        target.SetValue(property, from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (version != _motionVersion) return;
            target.BeginAnimation(property, null); target.SetValue(property, to);
        };
        target.BeginAnimation(property, animation);
    }
    private void StopAnimations()
    {
        _motionVersion++;
        _translation.BeginAnimation(TranslateTransform.XProperty, null); _translation.BeginAnimation(TranslateTransform.YProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
