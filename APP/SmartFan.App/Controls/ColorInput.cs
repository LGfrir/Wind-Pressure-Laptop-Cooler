using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Markup;

namespace SmartFan.App.Controls;

/// <summary>Local RGB color editor with a native WPF HSV palette.</summary>
public sealed class ColorInput : UserControl
{
    private readonly TextBox _hex;
    private readonly TextBox[] _channels = new TextBox[3];
    private readonly Border _swatch;
    private readonly Grid _rgb = new();
    private readonly Grid _inputs;
    private readonly TextBlock _swatchHex = ControlUi.Text("", size: 17);
    private readonly TextBlock _swatchHint = ControlUi.Text("点击打开调色板", size: 11);
    private readonly TextBlock _hexCaption = ControlUi.Text("HEX", "MutedBrush", 10);
    private bool _sync;
    private bool _compact;
    public event EventHandler? ColorChanged;

    public ColorInput() : this(Color.FromRgb(117, 104, 255)) { }

    public ColorInput(Color color)
    {
        var root = new StackPanel();
        var swatchContent = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        var captions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _swatchHex.SetResourceReference(TextBlock.FontWeightProperty, "UiMetricWeight");
        _swatchHint.Margin = new Thickness(0, 4, 0, 0);
        captions.Children.Add(_swatchHex); captions.Children.Add(_swatchHint); swatchContent.Children.Add(captions);
        var glyph = new TextBlock { Text = "◉", FontSize = 30, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = .65 };
        glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = _swatchHex }); swatchContent.Children.Add(glyph);
        _swatch = new Border { Height = 88, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = swatchContent, Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(_swatch, "打开 RGB 调色板");
        _swatch.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ShowPicker(); e.Handled = true; } };
        _swatch.MouseLeftButtonUp += (_, _) => ShowPicker(); root.Children.Add(_swatch);
        _hex = ControlUi.Input(Format(color), 80); _hex.MaxLength = 7;
        _hex.ToolTip = "6 位 RGB 十六进制，例如 #7568FF"; _hex.TextChanged += (_, _) => HexEdited();
        var inputs = _inputs = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var hexGroup = new StackPanel(); hexGroup.Children.Add(_hex);
        _hexCaption.HorizontalAlignment = HorizontalAlignment.Center; _hexCaption.Margin = new Thickness(0, 4, 0, 0); hexGroup.Children.Add(_hexCaption); inputs.Children.Add(hexGroup);
        Grid.SetColumn(_rgb, 1); _rgb.Margin = new Thickness(10, 0, 0, 0); inputs.Children.Add(_rgb);
        for (var i = 0; i < 3; i++)
        {
            _rgb.ColumnDefinitions.Add(new ColumnDefinition());
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0) };
            var channel = ControlUi.Input("0", 28); channel.MaxLength = 3; channel.Padding = new Thickness(7, 7, 7, 7);
            channel.ToolTip = $"{"RGB"[i]} · 0～255"; _channels[i] = channel; channel.TextChanged += (_, _) => ChannelsEdited(); cell.Children.Add(channel);
            var label = ControlUi.Text("RGB"[i].ToString(), "MutedBrush", 10); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 4, 0, 0);
            cell.Children.Add(label); Grid.SetColumn(cell, i); _rgb.Children.Add(cell);
        }
        root.Children.Add(inputs); Content = root; SetColor(color, false);
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value; _swatch.Height = value ? 55 : 88;
            _swatchHint.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _rgb.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _hexCaption.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _inputs.ColumnDefinitions[1].Width = value ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        }
    }
    public Color Color
    {
        get
        {
            if (!TryParse(_hex.Text, out var color)) throw new ArgumentException("颜色应为 6 位 RGB 十六进制，例如 #7568FF。");
            for (var i = 0; i < 3; i++) if (!byte.TryParse(_channels[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new ArgumentException($"{"RGB"[i]} 通道需要填写 0～255 的整数。");
            return color;
        }
        set => SetColor(value, true);
    }
    public static bool TryParse(string text, out Color color)
    {
        var value = text.Trim(); if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        { color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true; }
        color = Colors.Transparent; return false;
    }
    private static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private void SetColor(Color color, bool notify)
    {
        _sync = true; _hex.Text = Format(color); var values = new[] { color.R, color.G, color.B };
        for (var i = 0; i < 3; i++) _channels[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
        _sync = false; Paint(color); if (notify) ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    private void HexEdited()
    {
        if (_sync || !TryParse(_hex.Text, out var color)) return;
        _sync = true; var values = new[] { color.R, color.G, color.B };
        for (var i = 0; i < 3; i++) _channels[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
        _sync = false; Paint(color); ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ChannelsEdited()
    {
        if (_sync) return; var values = new byte[3];
        for (var i = 0; i < 3; i++) if (!byte.TryParse(_channels[i].Text, NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return;
        var color = Color.FromRgb(values[0], values[1], values[2]); _sync = true; _hex.Text = Format(color); _sync = false; Paint(color); ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Paint(Color color)
    {
        var brighter = Color.FromRgb((byte)Math.Min(255, color.R + 24), (byte)Math.Min(255, color.G + 24), (byte)Math.Min(255, color.B + 24));
        _swatch.Background = new LinearGradientBrush(brighter, color, 35);
        var light = .2126 * color.R + .7152 * color.G + .0722 * color.B > 155;
        _swatchHex.Foreground = _swatchHint.Foreground = light ? new SolidColorBrush(Color.FromRgb(20, 25, 42)) : Brushes.White; _swatchHex.Text = Format(color);
    }
    private void ShowPicker()
    {
        var initial = TryParse(_hex.Text, out var parsed) ? parsed : Colors.White;
        ToHsv(initial, out var hue, out var saturation, out var value);
        var popup = new Popup { PlacementTarget = _swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 8 };
        var panel = new StackPanel { Width = 292, Margin = new Thickness(16), Focusable = true };
        panel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.Opened += (_, _) => Keyboard.Focus(panel);
        var title = ControlUi.Text("调色板", size: 15); title.SetResourceReference(TextBlock.FontWeightProperty, "UiHeadingWeight"); title.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(title);
        var sv = new Grid { Width = 292, Height = 155, Cursor = Cursors.Cross, ClipToBounds = true };
        var hueBase = new Border { CornerRadius = new CornerRadius(8) }; sv.Children.Add(hueBase);
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, 0) }); sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) });
        var markerCanvas = new Canvas { IsHitTestVisible = false }; var marker = new Ellipse { Width = 13, Height = 13, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent };
        markerCanvas.Children.Add(marker); sv.Children.Add(markerCanvas); panel.Children.Add(sv);
        var hueSlider = new Slider { Minimum = 0, Maximum = 360, Value = hue, IsSnapToTickEnabled = false, Margin = new Thickness(0, 12, 0, 12), Height = 36 };
        hueSlider.Background = Spectrum();
        hueSlider.Template = CreateHueTemplate();
        panel.Children.Add(hueSlider);
        var palette = new UniformGrid { Columns = 10, Margin = new Thickness(0, 0, 0, 13) };
        var picks = new[] { "#FF4D6D", "#FF8B44", "#FFD75E", "#9DE85A", "#31D7B2", "#40B9FF", "#7568FF", "#C36EFF", "#FF7CC9", "#FFFFFF" };
        foreach (var hex in picks)
        {
            TryParse(hex, out var preset); var dot = new Border { Width = 21, Height = 21, Background = new SolidColorBrush(preset), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand };
            dot.MouseLeftButtonUp += (_, _) => { Color = preset; popup.IsOpen = false; }; palette.Children.Add(dot);
        }
        panel.Children.Add(palette); var preview = new Border { Height = 38, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12) };
        var valueText = ControlUi.Text("", size: 12); valueText.HorizontalAlignment = HorizontalAlignment.Center; preview.Child = valueText; panel.Children.Add(preview);
        void Update()
        {
            var color = FromHsv(hue, saturation, value); hueBase.Background = new SolidColorBrush(FromHsv(hue, 1, 1)); preview.Background = new SolidColorBrush(color);
            valueText.Text = Format(color); valueText.Foreground = value > .7 && saturation < .5 ? Brushes.Black : Brushes.White;
            Canvas.SetLeft(marker, saturation * 292 - 6.5); Canvas.SetTop(marker, (1 - value) * 155 - 6.5);
        }
        void Pick(MouseEventArgs e) { var point = e.GetPosition(sv); saturation = Math.Clamp(point.X / 292, 0, 1); value = 1 - Math.Clamp(point.Y / 155, 0, 1); Update(); }
        sv.MouseLeftButtonDown += (_, e) => { sv.CaptureMouse(); Pick(e); e.Handled = true; };
        sv.MouseMove += (_, e) => { if (sv.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e); }; sv.MouseLeftButtonUp += (_, e) => { sv.ReleaseMouseCapture(); e.Handled = true; };
        hueSlider.ValueChanged += (_, _) => { hue = hueSlider.Value; Update(); };
        var accept = ControlUi.Button("使用这个颜色", (_, _) => { Color = FromHsv(hue, saturation, value); popup.IsOpen = false; });
        if (TryFindResource("PrimaryButton") is Style style) accept.Style = style; accept.Margin = new Thickness(0); panel.Children.Add(accept);
        var shell = ControlUi.Card(panel); shell.Padding = new Thickness(0); shell.CornerRadius = new CornerRadius(16); shell.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 5, Opacity = .25 };
        popup.Child = shell; Update(); popup.IsOpen = true;
    }

    private static ControlTemplate CreateHueTemplate() => (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:controls="clr-namespace:SmartFan.App.Controls;assembly=SmartFan" TargetType="Slider">
              <Grid><Border Height="12" CornerRadius="6" VerticalAlignment="Center" Background="{TemplateBinding Background}"/>
                <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Opacity="0"/></Track.DecreaseRepeatButton>
                  <Track.Thumb><controls:MotionThumb/></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Opacity="0"/></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
            """);
    internal static LinearGradientBrush Spectrum()
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (var i = 0; i <= 6; i++) gradient.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6d)); return gradient;
    }
    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
        var rgb = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((rgb.Item1 + m) * 255), (byte)Math.Round((rgb.Item2 + m) * 255), (byte)Math.Round((rgb.Item3 + m) * 255));
    }
    private static void ToHsv(Color color, out double h, out double s, out double v)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d; var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var diff = max - min;
        h = diff == 0 ? 0 : max == r ? 60 * ((g - b) / diff % 6) : max == g ? 60 * ((b - r) / diff + 2) : 60 * ((r - g) / diff + 4);
        if (h < 0) h += 360; s = max == 0 ? 0 : diff / max; v = max;
    }
}
