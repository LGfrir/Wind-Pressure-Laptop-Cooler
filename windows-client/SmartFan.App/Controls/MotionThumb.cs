using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartFan.App.Controls;

/// <summary>Animates only a slider thumb's decoration; native drag events and range values remain immediate.</summary>
[TemplatePart(Name = "PART_ThumbVisual", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Halo", Type = typeof(FrameworkElement))]
public sealed class MotionThumb : Thumb
{
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled), typeof(bool), typeof(MotionThumb),
        new PropertyMetadata(true, (owner, _) => ((MotionThumb)owner).UpdateAppearance(false)));
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }

    private ScaleTransform? _scale;
    private FrameworkElement? _halo;
    private bool _dragging;
    private bool _watchingPreferences;

    public MotionThumb()
    {
        Focusable = false; Cursor = Cursors.Hand;
        DragStarted += (_, _) => { _dragging = true; UpdateAppearance(true); };
        DragCompleted += (_, _) => { _dragging = false; UpdateAppearance(true); };
        IsEnabledChanged += (_, _) => UpdateAppearance(true);
        Loaded += (_, _) =>
        {
            if (!_watchingPreferences) { SystemParameters.StaticPropertyChanged += PreferencesChanged; _watchingPreferences = true; }
            UpdateAppearance(false);
        };
        Unloaded += (_, _) =>
        {
            if (_watchingPreferences) SystemParameters.StaticPropertyChanged -= PreferencesChanged;
            _watchingPreferences = false; _dragging = false; UpdateAppearance(false);
        };
    }

    public override void OnApplyTemplate()
    {
        StopAnimations(); base.OnApplyTemplate();
        var visual = GetTemplateChild("PART_ThumbVisual") as FrameworkElement;
        _halo = GetTemplateChild("PART_Halo") as FrameworkElement;
        // Template Freezables can be frozen/shared by WPF; animation always owns a mutable instance.
        _scale = (visual?.RenderTransform as ScaleTransform)?.CloneCurrentValue() ?? new ScaleTransform();
        if (visual is not null) { visual.RenderTransform = _scale; visual.RenderTransformOrigin = new Point(.5, .5); }
        UpdateAppearance(false);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); UpdateAppearance(true); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); UpdateAppearance(true); }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false; UpdateAppearance(true);
    }

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (Dispatcher.CheckAccess()) UpdateAppearance(false);
        else Dispatcher.BeginInvoke(() => UpdateAppearance(false));
    }

    private void UpdateAppearance(bool animate)
    {
        var scale = IsEnabled && _dragging ? .94 : IsEnabled && IsMouseOver ? 1.08 : 1;
        var halo = IsEnabled && _dragging ? .2 : IsEnabled && IsMouseOver ? .12 : 0;
        var useAnimation = animate && MotionEnabled && SystemParameters.ClientAreaAnimation;
        if (_scale is not null)
        {
            Change(_scale, ScaleTransform.ScaleXProperty, scale, useAnimation);
            Change(_scale, ScaleTransform.ScaleYProperty, scale, useAnimation);
        }
        if (_halo is not null) Change(_halo, OpacityProperty, halo, useAnimation);
    }

    private static void Change(Animatable target, DependencyProperty property, double value, bool animate)
    {
        if (animate)
            target.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { target.BeginAnimation(property, null); target.SetValue(property, value); }
    }

    private static void Change(UIElement target, DependencyProperty property, double value, bool animate)
    {
        if (animate)
            target.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { target.BeginAnimation(property, null); target.SetValue(property, value); }
    }

    private void StopAnimations()
    {
        if (_scale is { IsFrozen: false }) { _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); }
        _halo?.BeginAnimation(OpacityProperty, null);
    }
}
