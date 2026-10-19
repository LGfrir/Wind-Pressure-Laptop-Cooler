using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartFan.Core.Protocol;

namespace SmartFan.App.Controls;

/// <summary>Eight local lighting drafts. No BLE or flash write happens until ApplyRequested is handled.</summary>
public sealed class LightConfigEditor : UserControl
{
    private readonly StackPanel _fields = new();
    private readonly TextBlock _title = ControlUi.Text("灯效参数", size: 24);
    private readonly TextBlock _description = ControlUi.Text("", "MutedBrush", 13);
    private readonly TextBlock _badge = ControlUi.Text("", "AccentBrush", 11);
    private readonly TextBlock _status = ControlUi.Text("读取设备后编辑；只在点击应用时写入。", "MutedBrush", 12);
    private readonly Dictionary<string, TextBox> _numbers = new();
    private readonly List<ColorInput> _colors = new();
    private readonly List<TextBox> _durations = new();
    private readonly Dictionary<int, byte[]> _drafts = new();
    private readonly Dictionary<int, byte[]> _baselines = new();
    private readonly Button _read;
    private readonly Button _apply;
    private readonly Button _undo;
    private readonly Button _save;
    private CheckBox? _continuous;
    private WrapPanel? _colorList;
    private int _mode;
    private LightingIllustration? _illustration;

    public event EventHandler? ApplyRequested;
    public event EventHandler? ReadRequested;
    public event EventHandler? UndoRequested;
    public event EventHandler? SaveDraftRequested;
    public event Action<string>? ValidationFailed;
    public int Mode => _mode;
    public static string[] ModeNames { get; } = ["关闭", "单色常亮", "彩虹渐变", "单色呼吸", "随机闪烁", "双色闪烁", "自定义呼吸", "流光", "环绕扫描"];

    public LightConfigEditor()
    {
        var root = new StackPanel();
        _badge.Margin = new Thickness(0, 0, 0, 7); root.Children.Add(_badge);
        _title.SetResourceReference(TextBlock.FontWeightProperty, "UiHeadingWeight"); _title.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_title);
        _description.Margin = new Thickness(0, 0, 0, 20); _description.LineHeight = 22; root.Children.Add(_description);
        _fields.RenderTransform = new TranslateTransform();
        _fields.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => _illustration?.RefreshConfiguration()));
        _fields.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => _illustration?.RefreshConfiguration()));
        _fields.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => _illustration?.RefreshConfiguration()));
        root.Children.Add(_fields);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 12) };
        _read = ControlUi.Button("读取当前值", (_, _) => ReadRequested?.Invoke(this, EventArgs.Empty));
        _apply = ControlUi.Button("应用到设备", (_, _) =>
        {
            try { _ = GetPayload(); ApplyRequested?.Invoke(this, EventArgs.Empty); }
            catch (ArgumentException ex) { ShowStatus(ex.Message, true); ValidationFailed?.Invoke(ex.Message); }
        });
        _undo = ControlUi.Button("撤销修改", (_, _) =>
        {
            if (_baselines.TryGetValue(_mode, out var value)) Populate(value);
            else Populate(DefaultPayload(_mode));
            ShowStatus(_baselines.ContainsKey(_mode) ? "已恢复上次读取或成功应用的值。" : "已恢复本地默认草稿；尚未读取设备。", false);
            UndoRequested?.Invoke(this, EventArgs.Empty);
        });
        _save = ControlUi.Button("保存到场景", (_, _) =>
        {
            try { _drafts[_mode] = GetPayload(); ShowStatus("正在保存草稿…", false); SaveDraftRequested?.Invoke(this, EventArgs.Empty); }
            catch (ArgumentException ex) { ShowStatus(ex.Message, true); ValidationFailed?.Invoke(ex.Message); }
        });
        if (TryFindResource("PrimaryButton") is Style primary) _save.Style = primary;
        buttons.Children.Add(_save);
        buttons.Children.Add(_apply);
        buttons.Children.Add(_read);
        buttons.Children.Add(_undo);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        Content = root;
        SetMode(1);
    }

    public void SetMode(int mode)
    {
        if (mode is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_mode > 0)
        {
            try { _drafts[_mode] = GetPayload(); }
            catch (ArgumentException) { /* An invalid field is not promoted into a reusable binary draft. */ }
        }
        _mode = mode;
        _title.Text = ModeNames[mode];
        _badge.Text = new[] { "OFF / 00", "STATIC / 01", "SPECTRUM / 02", "BREATH / 03", "RANDOM / 04", "DUO / 05", "COLOR STOPS / 06", "CHASE / 07", "SCAN / 08" }[mode];
        _description.Text = new[]
        {
            "让灯光安静下来。风扇调速和温度监控仍可独立工作。",
            "选一抹喜欢的颜色，保持纯粹、稳定的光。",
            "让色谱自然流转，用过渡时长决定颜色变化的节奏。",
            "在明暗之间缓缓呼吸。选择颜色，再决定一次过渡需要多久。",
            "为每次刷新留一点惊喜，选择持续变色或明暗交替。",
            "两种颜色，交替出现。用切换间隔创造清晰的节拍。",
            "编排一条属于你的色彩序列，每一段都能拥有自己的节奏。",
            "排列颜色，让光沿着灯带向前流动。",
            "一束光扫过灯带。配置扫描范围、色彩与移动节奏。"
        }[mode];
        Populate(_drafts.TryGetValue(mode, out var draft) ? draft : DefaultPayload(mode));
        ShowStatus(mode == 0 ? "关闭模式没有配置特征。" : _baselines.ContainsKey(mode) ? "本地草稿；点击应用后才会写入设备。" : "当前为本地默认草稿，尚未读取设备参数。", false);
    }

    public void LoadPayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Validate(_mode, payload);
        Populate(payload);
        _baselines[_mode] = payload.ToArray();
        _drafts[_mode] = payload.ToArray();
        ShowStatus("已读取设备参数。", false);
    }

    public void ClearDrafts()
    {
        _drafts.Clear(); _baselines.Clear(); Populate(DefaultPayload(_mode));
        ShowStatus("当前为本地默认草稿，尚未读取设备参数。", false);
    }

    /// <summary>Atomically replace every local draft when the user switches scenes.</summary>
    public void LoadSceneDrafts(IReadOnlyDictionary<int, byte[]> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var validated = new Dictionary<int, byte[]>();
        foreach (var (mode, payload) in drafts)
        {
            ArgumentNullException.ThrowIfNull(payload);
            Validate(mode, payload); validated.Add(mode, payload.ToArray());
        }
        _drafts.Clear(); _baselines.Clear();
        foreach (var (mode, payload) in validated) { _drafts.Add(mode, payload.ToArray()); _baselines.Add(mode, payload.ToArray()); }
        Populate(_drafts.TryGetValue(_mode, out var current) ? current : DefaultPayload(_mode));
        ShowStatus("已载入场景草稿，尚未同步设备。", false);
    }

    /// <summary>Call only after a successful device write/readback.</summary>
    public void AcceptApplied()
    {
        var payload = GetPayload();
        _baselines[_mode] = payload.ToArray();
        _drafts[_mode] = payload.ToArray();
        ShowStatus("设备已接受参数。", false);
    }

    public void SetBusy(bool busy)
    {
        _read.IsEnabled = _apply.IsEnabled = _undo.IsEnabled = _save.IsEnabled = !busy && _mode > 0;
        _fields.IsEnabled = !busy;
    }

    public void ShowStatus(string message, bool error = false)
    {
        _status.Text = message;
        if (error) _status.Foreground = new SolidColorBrush(Color.FromRgb(248, 126, 126));
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
    }

    public byte[] GetPayload()
    {
        if (_mode == 0) throw new ArgumentException("关闭模式没有灯效配置。请选择其他模式。");
        var data = _mode switch
        {
            1 => new byte[3],
            2 => new byte[2],
            3 => new byte[5],
            4 => new byte[1],
            5 => new byte[9],
            6 => new byte[1 + 5 * _colors.Count],
            7 => new byte[1 + 3 * _colors.Count],
            8 => new byte[22],
            _ => throw new ArgumentOutOfRangeException(nameof(Mode))
        };
        switch (_mode)
        {
            case 1: WriteColor(data, 0, _colors[0].Color); break;
            case 2: WriteU16(data, 0, "switch", "过渡时长"); break;
            case 3: WriteU16(data, 0, "switch", "单次过渡时长"); WriteColor(data, 2, _colors[0].Color); break;
            case 4: data[0] = _continuous?.IsChecked == true ? (byte)1 : (byte)0; break;
            case 5:
                data[0] = 2;
                WriteColor(data, 1, _colors[0].Color);
                WriteColor(data, 4, _colors[1].Color);
                WriteU16(data, 7, "period", "切换周期");
                break;
            case 6:
            case 7:
                data[0] = (byte)_colors.Count;
                for (var i = 0; i < _colors.Count; i++) WriteColor(data, 1 + i * 3, _colors[i].Color);
                if (_mode == 6)
                    for (var i = 0; i < _durations.Count; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1 + _colors.Count * 3 + i * 2), PositiveU16(_durations[i], $"第 {i + 1} 段时长"));
                break;
            case 8:
                WriteColor(data, 0, _colors[0].Color);
                WriteColor(data, 3, _colors[1].Color);
                WriteFloat(data, 6, "range", "扫描半径");
                WriteFloat(data, 10, "inner", "主色半径");
                var wait = ControlUi.Number(_numbers["wait"], "等待时长");
                if (wait < 0 || wait > int.MaxValue || wait != Math.Truncate(wait)) throw new ArgumentException("等待时长必须是 0～2147483647 的整数毫秒。");
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), (int)wait);
                WriteFloat(data, 18, "speed", "移动速度");
                break;
        }
        Validate(_mode, data);
        return data;
    }

    private static CharacteristicKey Key(int mode) => mode switch
    {
        1 => CharacteristicKey.StaticConfig, 2 => CharacteristicKey.RainbowConfig, 3 => CharacteristicKey.BreathConfig,
        4 => CharacteristicKey.RandomFlashConfig, 5 => CharacteristicKey.BlinkConfig, 6 => CharacteristicKey.CustomBreathConfig,
        7 => CharacteristicKey.ChaseConfig, 8 => CharacteristicKey.RoundConfig,
        _ => throw new ArgumentException("当前模式没有配置特征。")
    };

    private static void Validate(int mode, byte[] data) => PacketCodec.Validate(Key(mode), data);

    private void Populate(byte[] data)
    {
        if (_mode > 0) Validate(_mode, data);
        _fields.Children.Clear();
        _numbers.Clear(); _colors.Clear(); _durations.Clear(); _continuous = null; _colorList = null;
        _read.IsEnabled = _apply.IsEnabled = _undo.IsEnabled = _save.IsEnabled = _mode > 0;
        var illustration = _illustration = new LightingIllustration
        {
            Mode = _mode, Colors = () => _colors.Select(color => color.Color).ToArray(),
            Duration = () => _numbers.TryGetValue("switch", out var time) || _numbers.TryGetValue("period", out time) ? ControlUi.Number(time, "时长") : 1000,
            Segments = () => _durations.Select((input, index) => (double)PositiveU16(input, $"第 {index + 1} 段时长")).ToArray(),
            Range = () => ControlUi.Number(_numbers["range"], "扫描半径"), InnerRange = () => ControlUi.Number(_numbers["inner"], "主色半径"),
            Speed = () => ControlUi.Number(_numbers["speed"], "速度"), Wait = () => ControlUi.Number(_numbers["wait"], "扫描间隔"),
            Continuous = () => _continuous?.IsChecked == true
        };
        var illustrationCard = ControlUi.Card(illustration); illustrationCard.Padding = new Thickness(0); illustrationCard.Margin = new Thickness(0, 0, 0, 18);
        _fields.Children.Add(illustrationCard);
        switch (_mode)
        {
            case 0: _fields.Children.Add(ControlUi.Text("无需额外参数", "MutedBrush")); break;
            case 1: _fields.Children.Add(Section(ColorField("灯光颜色", ReadColor(data, 0)))); break;
            case 2: _fields.Children.Add(Section(TimeField("switch", "色彩过渡", U16(data, 0), "相邻颜色之间的过渡时长。刷新周期只控制帧率。"))); break;
            case 3:
                _fields.Children.Add(Columns(Section(ColorField("呼吸颜色", ReadColor(data, 2))), Section(TimeField("switch", "呼吸节奏", U16(data, 0), "变暗、变亮各占一次时长。完整呼吸包含两次过渡。"))));
                break;
            case 4:
                var random = new StackPanel();
                var randomTitle = ControlUi.Text("随机节奏", size: 16); randomTitle.SetResourceReference(TextBlock.FontWeightProperty, "UiHeadingWeight"); random.Children.Add(randomTitle);
                _continuous = new CheckBox { IsChecked = data[0] == 1, Content = "连续变色", Margin = new Thickness(0, 15, 0, 12), FontSize = 15 };
                random.Children.Add(_continuous); random.Children.Add(ControlUi.Text("开启：每次刷新生成随机颜色。\n关闭：随机颜色与熄灭交替。", "MutedBrush", 12));
                _fields.Children.Add(Section(random));
                break;
            case 5:
                _fields.Children.Add(Columns(Section(ColorField("01 · 颜色 A", ReadColor(data, 1))), Section(ColorField("02 · 颜色 B", ReadColor(data, 4)))));
                _fields.Children.Add(Section(TimeField("period", "切换节拍", U16(data, 7), "每到一个间隔切换至下一种颜色。")));
                break;
            case 6:
            case 7:
                var help = ControlUi.Text(_mode == 6 ? "颜色节点 · 1～9 个。每段过渡至下一色，最后一段回到首色。" : "色彩序列 · 1～9 个。按照排列顺序循环流动。", "MutedBrush", 12);
                help.Margin = new Thickness(0, 0, 0, 12);
                _fields.Children.Add(help);
                _colorList = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch }; _fields.Children.Add(_colorList);
                var entries = new List<(Color, ushort)>();
                for (var i = 0; i < data[0]; i++) entries.Add((ReadColor(data, 1 + i * 3), _mode == 6 ? U16(data, 1 + data[0] * 3 + i * 2) : (ushort)1000));
                BuildColorList(entries);
                break;
            case 8:
                _fields.Children.Add(Columns(Section(ColorField("扫描主色", ReadColor(data, 0))), Section(ColorField("扫描副色", ReadColor(data, 3)))));
                _fields.Children.Add(Columns(Section(NumberField("range", "扫描半径 · LED", ReadFloat(data, 6), "整个扫描光束的半径，必须大于 0。")), Section(NumberField("inner", "主色半径 · LED", ReadFloat(data, 10), "主色区域半径，不超过扫描半径。"))));
                _fields.Children.Add(Columns(Section(NumberField("wait", "扫描间隔 · ms", BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14)), "本轮扫描结束后的等待时长。")), Section(NumberField("speed", "移动速度 · LED/s", ReadFloat(data, 18), "0 表示停止移动。"))));
                break;
        }
        if (IsLoaded)
        {
            _fields.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            if (_fields.RenderTransform is TranslateTransform shift) shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private FrameworkElement ColorField(string label, Color color)
    {
        var input = new ColorInput(color); _colors.Add(input);
        return ControlUi.Field(label, input);
    }

    private FrameworkElement NumberField(string key, string label, double value, string help)
    {
        var input = ControlUi.Input(ControlUi.Format(value)); _numbers[key] = input;
        input.FontSize = 19; input.SetResourceReference(Control.FontWeightProperty, "UiMetricWeight");
        return ControlUi.Field(label, input, help);
    }

    private FrameworkElement TimeField(string key, string label, ushort value, string help)
    {
        var content = new StackPanel();
        var heading = ControlUi.Text(label, size: 15); heading.SetResourceReference(TextBlock.FontWeightProperty, "UiHeadingWeight"); heading.Margin = new Thickness(0, 0, 0, 11); content.Children.Add(heading);
        var row = new DockPanel(); var unit = ControlUi.Text("ms", "MutedBrush", 13); unit.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(unit, Dock.Right); row.Children.Add(unit);
        var input = ControlUi.Input(value.ToString(CultureInfo.InvariantCulture), 70); input.FontSize = 24; input.SetResourceReference(Control.FontWeightProperty, "UiMetricWeight"); _numbers[key] = input; row.Children.Add(input); content.Children.Add(row);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Log(value) / Math.Log(65535), IsSnapToTickEnabled = false, TickFrequency = .001, Margin = new Thickness(0, 13, 0, 10) };
        var syncing = false;
        slider.ValueChanged += (_, _) => { if (syncing) return; syncing = true; input.Text = Math.Max(1, (int)Math.Round(Math.Exp(slider.Value * Math.Log(65535)))).ToString(CultureInfo.InvariantCulture); syncing = false; };
        input.TextChanged += (_, _) => { if (syncing || !ushort.TryParse(input.Text, out var duration) || duration == 0) return; syncing = true; slider.Value = Math.Log(duration) / Math.Log(65535); syncing = false; };
        content.Children.Add(slider);
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var preset in new[] { 200, 500, 1000, 2000, 5000 })
        {
            var button = ControlUi.Button(preset >= 1000 ? $"{preset / 1000d:0.#} s" : $"{preset} ms", (_, _) => input.Text = preset.ToString(CultureInfo.InvariantCulture));
            button.Padding = new Thickness(9, 5, 9, 5); button.MinHeight = 28; button.FontSize = 11; button.Margin = new Thickness(0, 0, 5, 5); presets.Children.Add(button);
        }
        content.Children.Add(presets); content.Children.Add(ControlUi.Text(help, "MutedBrush", 12)); return content;
    }

    private static Border Section(UIElement content)
    {
        var card = ControlUi.Card(content); card.Padding = new Thickness(20); card.Margin = new Thickness(0, 0, 0, 14); return card;
    }
    private static Grid Columns(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(first); Grid.SetColumn(second, 2); grid.Children.Add(second); return grid;
    }

    private void BuildColorList(List<(Color Color, ushort Duration)> entries)
    {
        if (_colorList is null) return;
        _colorList.Children.Clear(); _colors.Clear(); _durations.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            var panel = new StackPanel();
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var remove = ControlUi.Button("×", (_, _) => ModifyList(index)); remove.ToolTip = "移除这个颜色"; remove.Padding = new Thickness(8, 2, 8, 2); remove.MinHeight = 24;
            remove.IsEnabled = entries.Count > 1;
            remove.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right);
            heading.Children.Add(remove); heading.Children.Add(ControlUi.Text($"{i + 1:00}  /  COLOR", "MutedBrush", 11)); panel.Children.Add(heading);
            var color = new ColorInput(entries[i].Color) { Compact = true }; _colors.Add(color); panel.Children.Add(color);
            if (_mode == 6)
            {
                var duration = ControlUi.Input(entries[i].Duration.ToString(CultureInfo.InvariantCulture));
                _durations.Add(duration);
                var durationField = ControlUi.Field("过渡至下一色 · ms", duration); durationField.Margin = new Thickness(0, 9, 0, 0); panel.Children.Add(durationField);
            }
            var card = ControlUi.Card(panel);
            card.Width = 230; card.Margin = new Thickness(0, 0, 12, 12); card.VerticalAlignment = VerticalAlignment.Top;
            _colorList.Children.Add(card);
        }
        var add = ControlUi.Button($"＋ 添加颜色（{entries.Count}/9）", (_, _) => ModifyList(null));
        add.IsEnabled = entries.Count < 9; add.Margin = new Thickness(0, 0, 12, 12); add.Width = 230; add.Height = _mode == 6 ? 234 : 153; add.VerticalAlignment = VerticalAlignment.Top;
        _colorList.Children.Add(add);
        _illustration?.RefreshConfiguration();
    }

    private void ModifyList(int? remove)
    {
        try
        {
            var entries = new List<(Color, ushort)>();
            for (var i = 0; i < _colors.Count; i++) entries.Add((_colors[i].Color, _mode == 6 ? PositiveU16(_durations[i], $"第 {i + 1} 段时长") : (ushort)1000));
            if (remove.HasValue && entries.Count > 1) entries.RemoveAt(remove.Value);
            else if (!remove.HasValue && entries.Count < 9) entries.Add((Color.FromRgb(0, 170, 255), 1000));
            BuildColorList(entries);
        }
        catch (ArgumentException ex) { ShowStatus(ex.Message, true); }
    }

    private static ushort PositiveU16(TextBox input, string name)
    {
        if (!ushort.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new ArgumentException($"{name}必须是 1～65535 的整数毫秒。");
        return value;
    }

    private void WriteU16(byte[] data, int offset, string key, string name) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), PositiveU16(_numbers[key], name));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static Color ReadColor(byte[] data, int offset) => Color.FromRgb(data[offset], data[offset + 1], data[offset + 2]);
    private static void WriteColor(byte[] data, int offset, Color color) { data[offset] = color.R; data[offset + 1] = color.G; data[offset + 2] = color.B; }
    private static float ReadFloat(byte[] data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)));
    private void WriteFloat(byte[] data, int offset, string key, string name)
    {
        var value = (float)ControlUi.Number(_numbers[key], name);
        if (!float.IsFinite(value)) throw new ArgumentException($"{name}超出 float 有效范围。");
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
    }

    private static byte[] DefaultPayload(int mode)
    {
        static byte[] Duration(int ms) => [(byte)ms, (byte)(ms >> 8)];
        switch (mode)
        {
            case 0: return [];
            case 1: return [0, 170, 255];
            case 2: return Duration(1000);
            case 3: return [232, 3, 0, 170, 255];
            case 4: return [0];
            case 5: return [2, 0, 170, 255, 255, 80, 150, 244, 1];
            case 6: return [2, 0, 170, 255, 255, 80, 150, 232, 3, 232, 3];
            case 7: return [2, 0, 170, 255, 255, 80, 150];
            case 8:
                var data = new byte[22]; data[1] = 170; data[2] = 255;
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(6), BitConverter.SingleToInt32Bits(2));
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), BitConverter.SingleToInt32Bits(1));
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 500);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), BitConverter.SingleToInt32Bits(3));
                return data;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
