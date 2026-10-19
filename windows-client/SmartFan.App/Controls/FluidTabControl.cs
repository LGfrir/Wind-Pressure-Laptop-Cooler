using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SmartFan.App.Controls;

/// <summary>原生键盘导航保留；选中底板与页面使用独立动画，不重建业务内容。</summary>
public sealed class FluidTabControl : TabControl
{
    private Border? _indicator;
    private FrameworkElement? _rail;
    private FrameworkElement? _content;
    private int _previousIndex;
    public FluidTabControl() => SizeChanged += (_, _) => PositionIndicator(false);
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("PART_Indicator") as Border;
        _rail = GetTemplateChild("PART_NavRail") as FrameworkElement;
        _content = GetTemplateChild("PART_Page") as FrameworkElement;
        Dispatcher.BeginInvoke(() => PositionIndicator(false), DispatcherPriority.Loaded);
    }
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (e.Source != this) return;
        int direction = SelectedIndex >= _previousIndex ? 1 : -1;
        _previousIndex = SelectedIndex;
        Dispatcher.BeginInvoke(() =>
        {
            PositionIndicator(true);
            if (_content is null || !SystemParameters.ClientAreaAnimation) return;
            if (_content.RenderTransform is not TranslateTransform transform)
            { transform = new TranslateTransform(); _content.RenderTransform = transform; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 22, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            // 整页透明度动画会额外合成大型中间表面；仅平移复用保留的绘制内容。
        }, DispatcherPriority.Loaded);
    }
    private void PositionIndicator(bool animate)
    {
        if (_indicator is null || _rail is null || SelectedIndex < 0) return;
        var tab = ItemContainerGenerator.ContainerFromIndex(SelectedIndex) as FrameworkElement;
        if (tab is null || tab.Visibility != Visibility.Visible || tab.ActualWidth <= 0) return;
        var location = tab.TranslatePoint(new Point(), _rail);
        var transform = _indicator.RenderTransform as TranslateTransform ?? new TranslateTransform();
        _indicator.RenderTransform = transform;
        var duration = TimeSpan.FromMilliseconds(animate && SystemParameters.ClientAreaAnimation ? 300 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(location.X, duration) { EasingFunction = ease });
        _indicator.BeginAnimation(WidthProperty, new DoubleAnimation(tab.ActualWidth, duration) { EasingFunction = ease });
    }
}
