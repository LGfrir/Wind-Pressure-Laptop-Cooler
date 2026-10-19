using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SmartFan.App.Controls;

namespace SmartFan.Ui.Tests;

/// <summary>Exercises the real input templates and native drag routing without mouse injection or a visible window.</summary>
internal static class InputsMotionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var value = new ValueSource { Value = .42 };
        var slider = new Slider { Minimum = 0, Maximum = 1, SmallChange = .05, LargeChange = .2 };
        slider.SetBinding(Slider.ValueProperty, new Binding(nameof(ValueSource.Value)) { Source = value, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Layout(slider, 360, 42);
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        check(track?.Thumb is MotionThumb, "slider materializes its native Track with the shared motion thumb");
        var thumb = (MotionThumb)track!.Thumb;
        thumb.ApplyTemplate();
        var visual = (FrameworkElement)thumb.Template.FindName("PART_ThumbVisual", thumb);
        var halo = (FrameworkElement)thumb.Template.FindName("PART_Halo", thumb);
        var scale = (ScaleTransform)visual.RenderTransform;
        check(!scale.IsFrozen, "thumb animations own a mutable transform rather than a frozen template resource");
        check(!slider.IsSnapToTickEnabled, "ordinary sliders are continuous rather than implicitly constrained to integer ticks");
        check(thumb.ActualWidth >= 30 && thumb.ActualHeight >= 30 && Descendants<Ellipse>(thumb).Count() >= 3, "slider provides a comfortable circular thumb target and halo");
        foreach (var repeat in new[] { track.DecreaseRepeatButton, track.IncreaseRepeatButton })
        {
            var rail = (Border)repeat.Template.FindName("Rail", repeat);
            check(rail.CornerRadius.TopLeft >= 3 && rail.Height == 6, "slider rail buttons render only rounded tracks without default square chrome");
        }
        check(Descendants<FrameworkElement>(slider).All(element => element.Effect is null), "slider motion has no blur or effect surface to redraw");

        var before = slider.Value;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
        thumb.RaiseEvent(new DragDeltaEventArgs(24, 0));
        check(slider.Value > before && Math.Abs(value.Value - slider.Value) < 1e-9, "native thumb dragging immediately updates the real value and its two-way source");
        check(!DependencyPropertyHelper.GetValueSource(slider, Slider.ValueProperty).IsAnimated && !DependencyPropertyHelper.GetValueSource(track, Track.ValueProperty).IsAnimated, "decorative motion never delays slider or track range values with animation");
        var afterDrag = slider.Value;
        Slider.IncreaseSmall.Execute(null, slider);
        check(Math.Abs(slider.Value - afterDrag - .05) < 1e-8 && Math.Abs(value.Value - slider.Value) < 1e-9, "native keyboard range commands still update the source immediately");

        thumb.MotionEnabled = false;
        check(Math.Abs(scale.ScaleX - .94) < 1e-9 && Math.Abs(halo.Opacity - .2) < 1e-9 && !scale.HasAnimatedProperties, "disabling motion cancels decoration clocks and applies the current pressed state immediately");
        thumb.RaiseEvent(new DragCompletedEventArgs(24, 0, false));
        check(scale.ScaleX == 1 && scale.ScaleY == 1 && halo.Opacity == 0, "nonanimated drag completion restores the resting circle and halo");
        thumb.MotionEnabled = true;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
        // Confirm clock attachment immediately, then let WPF's timing dispatcher start the clock.
        check(scale.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "thumb decoration follows the system animation preference");
        var stableValue = slider.Value;
        PumpTiming();
        check(DependencyPropertyHelper.GetValueSource(scale, ScaleTransform.ScaleXProperty).IsAnimated == SystemParameters.ClientAreaAnimation,
            "thumb decoration reaches an active animation after WPF timing runs");
        check(DependencyPropertyHelper.GetValueSource(halo, UIElement.OpacityProperty).IsAnimated == SystemParameters.ClientAreaAnimation,
            "thumb halo also follows the system animation preference");
        check(slider.Value == stableValue && value.Value == stableValue, "running decoration clocks never interpolate or change the actual slider value");
        thumb.MotionEnabled = false;
        check(!scale.HasAnimatedProperties && !DependencyPropertyHelper.GetValueSource(halo, UIElement.OpacityProperty).IsAnimated, "local reduced motion leaves no lingering decoration clocks");
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));

        var integer = new Slider { Minimum = 0, Maximum = 255, Value = 100, IsSnapToTickEnabled = true, TickFrequency = 1 };
        Layout(integer, 360, 42);
        var integerTrack = (Track)integer.Template.FindName("PART_Track", integer);
        integerTrack.Thumb.ApplyTemplate();
        var integerVisual = (FrameworkElement)integerTrack.Thumb.Template.FindName("PART_ThumbVisual", integerTrack.Thumb);
        var integerScale = (ScaleTransform)integerVisual.RenderTransform;
        check(!ReferenceEquals(scale, integerScale) && !integerScale.IsFrozen, "separate slider thumbs never share their mutable animation transform");
        integerTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
        integerTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(13.5, 0));
        check(integer.Value > 100 && integer.Value == Math.Round(integer.Value), "explicit brightness and PWM tick constraints preserve whole-number drag values");
        integerTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(13.5, 0, false));

        CheckForms(check);
    }

    private static void CheckForms(Action<bool, string> check)
    {
        var input = new TextBox { Text = "abcdef" }; Layout(input, 280, 42);
        var inputShell = (Border)input.Template.FindName("InputShell", input);
        check(inputShell.CornerRadius.TopLeft == 12 && input.Template.FindName("PART_ContentHost", input) is ScrollViewer, "rounded text inputs retain the native editing content host");
        input.Select(1, 3); input.SelectedText = "XY";
        check(input.Text == "aXYef" && input.FontWeight == FontWeights.Normal, "rounded input styling preserves selection editing and regular text weight");

        var combo = new ComboBox { ItemsSource = new[] { "办公", "游戏" }, SelectedIndex = 0 }; Layout(combo, 280, 42);
        combo.SelectedIndex = 1; Layout(combo, 280, 42);
        var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
        var shell = (Border)toggle.Template.FindName("ComboShell", toggle);
        check(shell.CornerRadius.TopLeft == 12 && combo.Template.FindName("PART_Popup", combo) is Popup, "rounded selector keeps the native named popup and customizes its visible shell");
        check(Descendants<TextBlock>(combo).Any(text => text.Text == "游戏"), "rounded selector still renders the changed selection");
        combo.IsEditable = true; Layout(combo, 280, 42);
        check(combo.Template.FindName("PART_EditableTextBox", combo) is TextBox { Visibility: Visibility.Visible }, "editable selectors preserve the native editing part");

        var checkbox = new CheckBox { Content = "后台采样", IsThreeState = true }; Layout(checkbox, 280, 36);
        var checkShell = (Border)checkbox.Template.FindName("CheckShell", checkbox);
        var mark = (System.Windows.Shapes.Path)checkbox.Template.FindName("CheckMark", checkbox);
        var mixed = (Border)checkbox.Template.FindName("IndeterminateMark", checkbox);
        checkbox.IsChecked = true;
        check(checkShell.CornerRadius.TopLeft == 7 && mark.Visibility == Visibility.Visible, "rounded checkbox renders its checked state without default square chrome");
        checkbox.IsChecked = null;
        check(mark.Visibility == Visibility.Collapsed && mixed.Visibility == Visibility.Visible, "rounded checkbox preserves the native three-state presentation");
    }

    private sealed class ValueSource : INotifyPropertyChanged
    {
        private double _value;
        public double Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static void Layout(FrameworkElement control, double width, double height)
    {
        FrameworkElement root = control;
        while (VisualTreeHelper.GetParent(root) is FrameworkElement parent) root = parent;
        if (ReferenceEquals(root, control)) root = new Border { Child = control };
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }

    private static void PumpTiming()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(32) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
