using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartFan.App.Controls;

public sealed class SmoothScrollViewer : ScrollViewer
{
    public static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(nameof(AnimatedOffset), typeof(double), typeof(SmoothScrollViewer), new PropertyMetadata(0d, OffsetChanged));
    private double _target;
    private bool _animating;
    public double AnimatedOffset { get => (double)GetValue(AnimatedOffsetProperty); set => SetValue(AnimatedOffsetProperty, value); }
    private static void OffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((SmoothScrollViewer)sender).ScrollToVerticalOffset((double)e.NewValue);
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || Keyboard.Modifiers != ModifierKeys.None || ScrollableHeight <= 0) { base.OnPreviewMouseWheel(e); return; }
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source != this)
        {
            if (source is System.Windows.Controls.Primitives.Selector || source is Slider) { base.OnPreviewMouseWheel(e); return; }
            if (source is ScrollViewer nested && nested.ScrollableHeight > 0) { base.OnPreviewMouseWheel(e); return; }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : null;
        }
        var start = VerticalOffset;
        if (!_animating) _target = start;
        _target = Math.Clamp(_target - e.Delta * .75, 0, ScrollableHeight);
        if (Math.Abs(start - _target) < .1) return;
        BeginAnimation(AnimatedOffsetProperty, null);
        AnimatedOffset = start;
        var animation = new DoubleAnimation(start, _target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) => { _animating = false; };
        _animating = true;
        BeginAnimation(AnimatedOffsetProperty, animation);
        e.Handled = true;
    }
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        var offset = VerticalOffset;
        BeginAnimation(AnimatedOffsetProperty, null); AnimatedOffset = offset; _animating = false;
        base.OnPreviewMouseDown(e);
    }
}
