using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmartFan.App.Controls;

internal static class ControlUi
{
    public static TextBlock Text(string value, string brush = "TextBrush", double size = 13)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFontFamily");
        text.SetResourceReference(TextBlock.FontWeightProperty, "UiBodyWeight");
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return text;
    }

    public static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), MinHeight = 34 };
        button.SetResourceReference(Control.FontFamilyProperty, "UiFontFamily");
        button.SetResourceReference(Control.FontWeightProperty, "UiBodyWeight");
        button.Click += click;
        return button;
    }

    public static TextBox Input(string value, double width = 130)
    {
        var input = new TextBox { Text = value, MinWidth = width, Padding = new Thickness(9, 7, 9, 7), VerticalContentAlignment = VerticalAlignment.Center };
        input.SetResourceReference(Control.FontFamilyProperty, "UiFontFamily");
        input.SetResourceReference(Control.FontWeightProperty, "UiBodyWeight");
        input.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        input.SetResourceReference(Control.BackgroundProperty, "SurfaceAltBrush");
        input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        return input;
    }

    public static FrameworkElement Field(string label, FrameworkElement input, string? help = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = Text(label, size: 13);
        title.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(title);
        panel.Children.Add(input);
        if (help is not null)
        {
            var description = Text(help, "MutedBrush", 12);
            description.Margin = new Thickness(0, 5, 0, 0);
            panel.Children.Add(description);
        }
        return panel;
    }

    public static Border Card(UIElement child)
    {
        var border = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    public static double Number(TextBox input, string name)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new ArgumentException($"{name}需要填写数字。");
        if (!double.IsFinite(value)) throw new ArgumentException($"{name}必须是有限数字。");
        return value;
    }

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static Brush Brush(FrameworkElement owner, string key, Color fallback) => owner.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
