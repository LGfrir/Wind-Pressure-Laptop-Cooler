using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmartFan.App.Controls;
using SmartFan.App.Models;
using SmartFan.App.Views;

namespace SmartFan.Ui.Tests;

internal static class CompactFanControlChecks
{
    public static void Run(Action<bool, string> check)
    {
        var state = new State();
        var view = new DashboardView
        {
            DataContext = state,
            Layout = new() { Widgets = [new() { Id = "small-control", Kind = "fan.control", Width = 5, Height = 3 }] }
        };
        Layout(view, 1004, 445);
        var fan = Descendants<DashboardFanControl>(view).Single();
        var body = (FrameworkElement)VisualTreeHelper.GetParent(fan);
        var picker = (ComboBox)fan.FindName("ModePicker"); var apply = (Button)fan.FindName("ApplyButton");
        var slider = (Slider)fan.FindName("ManualSlider"); var status = (TextBlock)fan.FindName("ActionStatus");
        var manual = (StackPanel)fan.FindName("ManualPanel"); var automatic = (StackPanel)fan.FindName("AutomaticPanel"); var full = (StackPanel)fan.FindName("FullSpeedPanel");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(body).TransformBounds(new Rect(element.RenderSize));
        bool Fits(FrameworkElement element)
        {
            var bounds = new Rect(body.RenderSize); bounds.Inflate(1, 1);
            return element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Contains(Bounds(element));
        }
        TextBlock Caption(Control owner, string value) => Descendants<TextBlock>(owner).Single(text => text.Text == value);
        check(fan.IsCompact && body.ActualHeight < 100 && PresentationSource.FromVisual(view) is null && Window.GetWindow(view) is null,
            "a real 5 by 3 dashboard card selects compact fan presentation from its finite clipped slot without creating a native window");
        check(Descendants<ComboBox>(fan).Count() == 1 && Descendants<Slider>(fan).Count() == 1,
            "compact fan presentation reflows the original controls instead of creating duplicate bindings or interaction targets");
        for (var mode = 0; mode < 3; mode++)
        {
            state.FanModeIndex = mode; state.Notify(); Layout(view, 1004, 445);
            check(Fits(picker) && Fits(apply) && Math.Abs(Bounds(picker).Top - Bounds(apply).Top) < 1 && Grid.GetRow(apply) == 0,
                $"mode {mode} keeps the picker and explicit apply action together and completely inside the minimum card body");
            check(FullGlyphFits(Caption(picker, state.FanModes[mode])) && FullGlyphFits(Caption(apply, "应用风扇设置")),
                $"mode {mode} renders complete picker and apply-button captions at the legal minimum card size");
            check(status.Visibility == Visibility.Collapsed && Equals(apply.ToolTip, state.FanControlStatusText) && apply.Command == state.ApplyFanCommand,
                "compact layout keeps operation status in the apply tooltip while preserving the existing command binding");
            if (mode == 0)
            {
                var thumb = Descendants<Thumb>(slider).Single();
                check(manual.Visibility == Visibility.Visible && automatic.Visibility == Visibility.Collapsed && full.Visibility == Visibility.Collapsed && Fits(slider) && Fits(thumb) && Bounds(slider).Top >= Bounds(picker).Bottom - 1,
                    "minimum manual mode exposes the complete slider and draggable thumb below the header instead of clipping them");
                check(FullGlyphFits((TextBlock)fan.FindName("ManualLabel")) && FullGlyphFits((TextBlock)fan.FindName("ManualValue")) && slider.Value == 17,
                    "minimum manual mode retains its full PWM label, current percentage and actual value binding");
            }
            else if (mode == 1)
            {
                var temperature = (TextBlock)fan.FindName("AutomaticTemperature"); var curve = (Button)fan.FindName("CurveButton");
                check(automatic.Visibility == Visibility.Visible && manual.Visibility == Visibility.Collapsed && Fits(temperature) && Fits(curve) && FullGlyphFits(temperature) && FullGlyphFits(Caption(curve, "编辑曲线 ↗")),
                    "minimum automatic mode keeps the complete temperature preview and local curve-navigation button visible");
                check(temperature.Text.Contains("本机预览") && temperature.Text.Contains("81.0") && !temperature.Text.Contains('\n') && curve.ToolTip.ToString()!.Contains("固件对齐"),
                    "compact automatic mode stays an honest one-line local preview and preserves the firmware-interface limitation");
            }
            else
            {
                var title = (TextBlock)fan.FindName("FullSpeedTitle"); var hint = (TextBlock)fan.FindName("FullSpeedHint");
                check(full.Visibility == Visibility.Visible && manual.Visibility == Visibility.Collapsed && automatic.Visibility == Visibility.Collapsed && Fits(title) && Fits(hint) && FullGlyphFits(title) && FullGlyphFits(hint),
                    "minimum full-speed mode renders its complete request explanation within the clipped card body");
            }
        }
        state.FanModeIndex = 0; state.Notify(); Layout(view, 2000, 1100);
        check(!fan.IsCompact && manual.Orientation == Orientation.Vertical && double.IsNaN(slider.Width) && Grid.GetRow(apply) == 2 && Grid.GetColumnSpan(apply) == 2,
            "regrowing the same card restores the normal vertical control layout and independent apply row");
        check(status.Visibility == Visibility.Visible && ((TextBlock)fan.FindName("ManualHint")).Visibility == Visibility.Visible && Fits(status) && Fits(slider) && Fits(apply),
            "a larger card restores instructions and operation status without leaving compact dimensions attached");
        state.FanModeIndex = 1; state.Notify(); Layout(view, 2000, 1100);
        check(((TextBlock)fan.FindName("AutomaticTemperature")).Text.Contains('\n') && ((TextBlock)fan.FindName("AutomaticHint")).Visibility == Visibility.Visible && automatic.Orientation == Orientation.Vertical,
            "automatic mode restores its detailed temperature and policy explanation when the card regrows");
        Layout(view, 1004, 445);
        check(fan.IsCompact && Grid.GetRow(apply) == 0 && !((TextBlock)fan.FindName("AutomaticTemperature")).Text.Contains('\n') && Fits(apply),
            "shrinking the already regrown card returns safely to the compact presentation");
        check(state.Applies == 0 && view.CurrentLayout.Widgets.Single() is { Width: 5, Height: 3 },
            "minimum-size layout checks neither send commands nor change saved widget geometry");
    }
    private static bool FullGlyphFits(TextBlock text)
    {
        var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var glyph = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection, face, text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return text.ActualWidth > 0 && text.ActualHeight > 0 && glyph.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5 && glyph.Height <= text.ActualHeight + 1.5;
    }
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle); Dispatcher.PushFrame(frame);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return item;
    }
    private sealed class State : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public int FanModeIndex { get; set; }
        public IReadOnlyList<string> FanModes { get; } = ["手动控制", "设备自动控制", "全速运行"];
        public double ManualPwm { get; set; } = 17;
        public bool IsConnected => true;
        public bool Busy => false;
        public bool DemoMode => false;
        public bool TelemetryIsStale => false;
        public bool TelemetryEnabled => true;
        public double CpuTemperatureValue => 72;
        public double GpuTemperatureValue => 81;
        public AppSettings Settings { get; } = new();
        public DeviceProfile SelectedProfile { get; } = new();
        public string FanControlStatusText => "离线布局检查 · 尚未发送";
        public int Applies { get; private set; }
        public ICommand ApplyFanCommand { get; }
        public State() => ApplyFanCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Applies++);
        public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
