using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartFan.App.Controls;

/// <summary>原生可键盘操作的展开按钮；只在切换时短暂旋转，不启动持续动画。</summary>
[TemplatePart(Name = "PART_Arrow", Type = typeof(FrameworkElement))]
public sealed class DisclosureToggle : ToggleButton
{
    private RotateTransform? _rotation;
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rotation?.BeginAnimation(RotateTransform.AngleProperty, null);
        _rotation = new RotateTransform(IsChecked == true ? 90 : 0);
        if (GetTemplateChild("PART_Arrow") is FrameworkElement arrow) arrow.RenderTransform = _rotation;
    }
    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Rotate(90); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Rotate(0); }
    private void Rotate(double angle)
    {
        if (_rotation is null) return;
        if (IsLoaded && SystemParameters.ClientAreaAnimation)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { _rotation.BeginAnimation(RotateTransform.AngleProperty, null); _rotation.Angle = angle; }
    }
}
