using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SmartFan.App.Models;
using SmartFan.App.Services;

namespace SmartFan.App.Controls;

/// <summary>Local temperature-to-RPM curve editor. It never drives the fan or uploads an invented protocol.</summary>
public sealed class FanCurveEditor : UserControl
{
    private const double Left = 65, Top = 24, Right = 25, Bottom = 42;
    private readonly Canvas _plot = new() { MinHeight = 300, Height = 340, ClipToBounds = true, Background = Brushes.Transparent, Focusable = true };
    private readonly List<FanCurvePoint> _points = new();
    private readonly TextBox _temperature = ControlUi.Input("", 100);
    private readonly TextBox _rpm = ControlUi.Input("", 120);
    private readonly TextBlock _hint = ControlUi.Text("添加至少两个控制点后，拖动圆点或输入精确数值。", "MutedBrush", 12);
    private readonly TextBlock _selection = ControlUi.Text("未选择控制点", "MutedBrush", 12);
    private readonly Button _remove;
    private int _selected = -1;
    private bool _dragging;
    private bool _dragChanged;
    private double _xMin, _xMax = 100, _yMax = 1000;

    public event EventHandler? CurveChanged;
    public IReadOnlyList<FanCurvePoint> Points => _points.Select(Clone).ToArray();

    public FanCurveEditor()
    {
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new StackPanel();
        var label = ControlUi.Text("本地曲线 · 设备上传接口待对齐", "AccentBrush", 13);
        label.Margin = new Thickness(0, 0, 0, 7); root.Children.Add(label);
        var note = ControlUi.Text("PC 上报 CPU/GPU 温度，由 ESP32 计算目标 RPM 并控制风扇。此处保存编辑草稿，等待固件提供曲线上传接口。", "MutedBrush", 12);
        note.Margin = new Thickness(0, 0, 0, 16); root.Children.Add(note);
        root.Children.Add(_plot);
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(ControlUi.Field("温度 · °C", _temperature));
        _temperature.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(ControlUi.Field("目标转速 · RPM", _rpm));
        _rpm.Margin = new Thickness(0, 0, 12, 0);
        var apply = ControlUi.Button("更新控制点", (_, _) => ApplyFields());
        apply.Margin = new Thickness(0, 22, 8, 14); footer.Children.Add(apply);
        var add = ControlUi.Button("＋ 添加控制点", (_, _) => AddPoint());
        add.Margin = new Thickness(0, 22, 8, 14); footer.Children.Add(add);
        _remove = ControlUi.Button("移除", (_, _) => RemoveSelected());
        _remove.Margin = new Thickness(0, 22, 0, 14); footer.Children.Add(_remove);
        root.Children.Add(footer); root.Children.Add(_selection);
        _hint.Margin = new Thickness(0, 8, 0, 0); root.Children.Add(_hint);
        Content = root;
        _plot.SizeChanged += (_, _) => Draw();
        Loaded += (_, _) => Draw();
        _plot.MouseMove += MovePoint;
        _plot.MouseLeftButtonUp += EndDrag;
        _plot.LostMouseCapture += (_, _) => FinishDrag();
        _plot.KeyDown += (_, e) => { if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; } };
        UpdateFields();
    }

    public void SetPoints(IEnumerable<FanCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var incoming = points.Select(Clone).ToArray();
        if (incoming.Length != 0)
        {
            var errors = CurveService.Validate(incoming);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(points));
        }
        _points.Clear(); _points.AddRange(incoming);
        _selected = _points.Count > 0 ? 0 : -1;
        _hint.Text = _points.Count < 2 ? "添加至少两个控制点后，拖动圆点或输入精确数值。" : "拖动控制点或输入精确数值；曲线仅保存为本地草稿。";
        UpdateBounds(); UpdateFields(); Draw();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty && IsLoaded) Draw();
    }

    private static FanCurvePoint Clone(FanCurvePoint p) => new() { TemperatureCelsius = p.TemperatureCelsius, TargetRpm = p.TargetRpm };
    private double PlotWidth => Math.Max(1, _plot.ActualWidth - Left - Right);
    private double PlotHeight => Math.Max(1, _plot.ActualHeight - Top - Bottom);
    private Point ToScreen(FanCurvePoint point) => new(Left + (point.TemperatureCelsius - _xMin) / (_xMax - _xMin) * PlotWidth, Top + (1 - point.TargetRpm / _yMax) * PlotHeight);

    private void UpdateBounds()
    {
        _xMin = _points.Count == 0 ? 0 : Math.Min(0, Math.Floor(_points.Min(p => p.TemperatureCelsius) / 10) * 10);
        _xMax = _points.Count == 0 ? 100 : Math.Max(100, Math.Ceiling(_points.Max(p => p.TemperatureCelsius) / 10) * 10);
        var highest = _points.Count == 0 ? 0 : _points.Max(p => p.TargetRpm);
        _yMax = Math.Max(1000, Math.Ceiling(highest * 1.15 / 500) * 500);
    }

    private void Draw()
    {
        _plot.Children.Clear();
        if (_plot.ActualWidth < 150) return;
        var gridBrush = ControlUi.Brush(this, "BorderBrush", Color.FromRgb(65, 75, 94));
        var accent = ControlUi.Brush(this, "AccentBrush", Color.FromRgb(34, 211, 238));
        for (var i = 0; i <= 5; i++)
        {
            var y = Top + PlotHeight * i / 5;
            AddLine(new Point(Left, y), new Point(Left + PlotWidth, y), gridBrush, 1);
            var number = ControlUi.Text((_yMax * (1 - i / 5d)).ToString("0", CultureInfo.InvariantCulture), "MutedBrush", 11);
            number.Width = Left - 10; number.TextAlignment = TextAlignment.Right;
            Put(number, 0, y - 8);
        }
        for (var i = 0; i <= 5; i++)
        {
            var x = Left + PlotWidth * i / 5;
            AddLine(new Point(x, Top), new Point(x, Top + PlotHeight), gridBrush, 1);
            var number = ControlUi.Text(($"{_xMin + (_xMax - _xMin) * i / 5:0}"), "MutedBrush", 11);
            number.Width = 44; number.TextAlignment = TextAlignment.Center;
            Put(number, x - 22, Top + PlotHeight + 12);
        }
        Put(ControlUi.Text("RPM", "MutedBrush", 11), 12, 0);
        Put(ControlUi.Text("温度 · °C", "MutedBrush", 11), Left + PlotWidth - 58, 0);
        if (_points.Count == 0)
        {
            var empty = ControlUi.Text("尚无曲线 · 点击“添加控制点”开始", "MutedBrush", 14);
            Put(empty, Left + Math.Max(10, PlotWidth / 2 - 130), Top + PlotHeight / 2 - 10);
            return;
        }
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(139, 124, 255);
        var fill = new LinearGradientBrush(Color.FromArgb(52, accentColor.R, accentColor.G, accentColor.B), Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B), 90);
        var area = new Polygon { Fill = fill, IsHitTestVisible = false };
        area.Points.Add(new Point(Left, Top + PlotHeight)); area.Points.Add(new Point(Left, ToScreen(_points[0]).Y));
        foreach (var point in _points) area.Points.Add(ToScreen(point));
        area.Points.Add(new Point(Left + PlotWidth, ToScreen(_points[^1]).Y)); area.Points.Add(new Point(Left + PlotWidth, Top + PlotHeight));
        _plot.Children.Add(area);
        var path = new Polyline { Stroke = accent, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.Points.Add(new Point(Left, ToScreen(_points[0]).Y));
        foreach (var point in _points) path.Points.Add(ToScreen(point));
        path.Points.Add(new Point(Left + PlotWidth, ToScreen(_points[^1]).Y));
        _plot.Children.Add(path);
        for (var i = 0; i < _points.Count; i++)
        {
            var index = i;
            var p = ToScreen(_points[i]);
            if (i == _selected)
            {
                var guide = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = Top + PlotHeight, Stroke = accent, StrokeThickness = 1, Opacity = .35, StrokeDashArray = [3, 4], IsHitTestVisible = false };
                _plot.Children.Add(guide);
                var halo = new Ellipse { Width = 29, Height = 29, Fill = accent, Opacity = .13, IsHitTestVisible = false }; Put(halo, p.X - 14.5, p.Y - 14.5);
            }
            var hit = new Ellipse
            {
                Width = i == _selected ? 17 : 13, Height = i == _selected ? 17 : 13,
                Fill = i == _selected ? accent : ControlUi.Brush(this, "SurfaceBrush", Color.FromRgb(23, 30, 46)),
                Stroke = accent, StrokeThickness = 2.5, Cursor = Cursors.Hand,
                ToolTip = $"{_points[i].TemperatureCelsius:0.#} °C → {_points[i].TargetRpm:0} RPM\n拖动修改，或选中后输入精确数值"
            };
            hit.MouseLeftButtonDown += (_, e) =>
            {
                _selected = index; _dragging = true; _dragChanged = false;
                UpdateFields(); _plot.Focus(); _plot.CaptureMouse(); Draw(); e.Handled = true;
            };
            Put(hit, p.X - hit.Width / 2, p.Y - hit.Height / 2);
        }
    }

    private void AddLine(Point p1, Point p2, Brush brush, double thickness) => _plot.Children.Add(new Line { X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });
    private void Put(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); _plot.Children.Add(element); }

    private void MovePoint(object sender, MouseEventArgs e)
    {
        if (!_dragging || _selected < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(_plot);
        var min = _selected > 0 ? _points[_selected - 1].TemperatureCelsius + 0.1 : -50;
        var max = _selected < _points.Count - 1 ? _points[_selected + 1].TemperatureCelsius - 0.1 : 150;
        if (min > max) return;
        var temperature = Math.Clamp(Math.Round((_xMin + (position.X - Left) / PlotWidth * (_xMax - _xMin)) * 10) / 10, min, max);
        var rpm = Math.Round(Math.Clamp((1 - (position.Y - Top) / PlotHeight) * _yMax, 0, uint.MaxValue));
        var point = _points[_selected];
        if (point.TemperatureCelsius == temperature && point.TargetRpm == rpm) return;
        point.TemperatureCelsius = temperature; point.TargetRpm = rpm;
        _dragChanged = true; UpdateFields(); Draw();
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag(); _plot.ReleaseMouseCapture(); e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        UpdateBounds(); Draw();
        if (_dragChanged) Changed();
    }

    private void ApplyFields()
    {
        if (_selected < 0) { _hint.Text = "先选择或添加一个控制点。"; return; }
        try
        {
            var temperature = ControlUi.Number(_temperature, "温度");
            var rpm = ControlUi.Number(_rpm, "目标转速");
            if (rpm != Math.Truncate(rpm)) throw new ArgumentException("目标转速应为整数 RPM。");
            var copy = _points.Select(Clone).ToArray();
            copy[_selected].TemperatureCelsius = temperature; copy[_selected].TargetRpm = rpm;
            var errors = CurveService.Validate(copy);
            if (_points.Count == 1)
            {
                if (temperature is < -50 or > 150 || rpm < 0 || rpm > uint.MaxValue) throw new ArgumentException("温度应在 -50～150 °C，转速应为有效的非负 UInt32 数值。");
            }
            else if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            _points[_selected] = copy[_selected]; UpdateBounds(); UpdateFields(); Draw(); Changed();
        }
        catch (ArgumentException ex) { _hint.Text = ex.Message; }
    }

    private void AddPoint()
    {
        if (_points.Count >= 32) { _hint.Text = "最多支持 32 个本地控制点。"; return; }
        var index = _selected < 0 ? _points.Count : _selected + 1;
        double temperature;
        double rpm;
        if (_points.Count == 0) { temperature = 40; rpm = 0; }
        else if (index < _points.Count)
        {
            temperature = (_points[index - 1].TemperatureCelsius + _points[index].TemperatureCelsius) / 2;
            rpm = Math.Round((_points[index - 1].TargetRpm + _points[index].TargetRpm) / 2);
        }
        else
        {
            temperature = Math.Min(150, _points[^1].TemperatureCelsius + 10); rpm = _points[^1].TargetRpm;
            if (temperature <= _points[^1].TemperatureCelsius)
            {
                if (_points.Count > 1) { index = _points.Count - 1; temperature = (_points[^2].TemperatureCelsius + _points[^1].TemperatureCelsius) / 2; }
                else { index = 0; temperature = _points[0].TemperatureCelsius - 10; }
            }
        }
        _points.Insert(index, new FanCurvePoint { TemperatureCelsius = temperature, TargetRpm = rpm });
        _selected = index; UpdateBounds(); UpdateFields(); Draw(); Changed();
    }

    private void RemoveSelected()
    {
        if (_selected < 0 || _selected >= _points.Count) return;
        _points.RemoveAt(_selected); _selected = Math.Min(_selected, _points.Count - 1);
        UpdateBounds(); UpdateFields(); Draw(); Changed();
    }

    private void UpdateFields()
    {
        var valid = _selected >= 0 && _selected < _points.Count;
        _temperature.IsEnabled = _rpm.IsEnabled = _remove.IsEnabled = valid;
        _temperature.Text = valid ? ControlUi.Format(_points[_selected].TemperatureCelsius) : "";
        _rpm.Text = valid ? ControlUi.Format(_points[_selected].TargetRpm) : "";
        _selection.Text = valid ? $"已选第 {_selected + 1} 个点 · 共 {_points.Count} 个控制点" : "未选择控制点";
    }

    private void Changed()
    {
        _hint.Text = _points.Count < 2 ? "保存完整曲线至少需要两个控制点。" : "曲线草稿已修改；本次不会下发到 ESP32。纵轴会随控制点自动缩放。";
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }
}
