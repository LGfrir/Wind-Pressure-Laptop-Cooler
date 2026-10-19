using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SmartFan.App.Controls;
using SmartFan.App.Models;

namespace SmartFan.App.Views;

/// <summary>One-page widget workspace. Editing uses a detached draft and persists only local UI settings.</summary>
public partial class DashboardView : UserControl
{
    public static readonly DependencyProperty MetricsProperty = DependencyProperty.Register(nameof(Metrics), typeof(DashboardTelemetryData), typeof(DashboardView), new PropertyMetadata(null, (owner, _) => ((DashboardView)owner).RefreshMetrics()));
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(nameof(Layout), typeof(DashboardLayoutSettings), typeof(DashboardView), new PropertyMetadata(null, (owner, args) => ((DashboardView)owner).LoadLayout((DashboardLayoutSettings?)args.NewValue)));
    public DashboardTelemetryData? Metrics { get => (DashboardTelemetryData?)GetValue(MetricsProperty); set => SetValue(MetricsProperty, value); }
    public DashboardLayoutSettings? Layout { get => (DashboardLayoutSettings?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public event EventHandler? AdvancedNavigationRequested;
    public event EventHandler<DashboardDraftChangedEventArgs>? DraftChanged;
    public Func<DashboardLayoutSettings, Task<bool>>? SaveLayoutAsync { get; set; }
    public bool IsEditing { get; private set; }
    public bool CanUndo => IsEditing && !_saving && _editHistory.CanUndo;
    public bool CanRedo => IsEditing && !_saving && _editHistory.CanRedo;
    public DashboardLayoutSettings CurrentLayout => _working.Clone();
    private readonly DashboardEditHistory _editHistory = new();
    private DashboardLayoutSettings _working = new();
    private DashboardLayoutSettings _baseline = new();
    private string? _selected;
    private bool _updatingInspector;
    private bool _updatingAdd;
    private DashboardWidgetSettings _addDraft = new() { Kind = "cpu.combined", Width = 4, Height = 3 };
    private bool _saving;
    private DashboardGridGeometry _geometry;
    private Size _guideSize;
    private bool _editingChromeShown;
    private readonly Dictionary<string, WidgetFrame> _frames = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset Time, uint? Rpm)> _history = [];
    private DashboardDragSession? _dragSession;
    private Point _gesturePointer;
    private string? _gestureId;
    private bool _gestureFits = true;
    private bool _showAddPreview;
    private DashboardWidgetSettings? _recommendedAdd;
    private DashboardWidgetSettings? Selected => _working.Widgets.FirstOrDefault(widget => widget.Id == _selected);
    private sealed record WidgetFrame(Border Border, FrameworkElement Content, Thumb Move, Thumb Resize, Button Configure, TextBlock Title, DashboardWidgetMotion Motion);
    private static string Title(string kind) => kind switch
    {
        "cpu.combined" => "处理器 / CPU", "gpu.combined" => "显卡 / GPU", "memory.usage" => "系统内存 / RAM",
        "laptop.fans" => "电脑内置风扇", "fan.rpm" => "底座转速 · ESP32", "fan.history" => "底座转速历史", "fan.control" => "底座风扇控制",
        _ => DashboardWidgetRegistry.Available.FirstOrDefault(item => item.Key == kind)?.Title ?? kind
    };
    private static string ChoiceTitle(string kind) => kind switch
    {
        "cpu.combined" => "CPU 温度＋使用率", "gpu.combined" => "GPU 温度＋使用率", _ => Title(kind)
    };
    private static Dictionary<DashboardAspectRatio, string> RatioChoices(string kind) => new Dictionary<DashboardAspectRatio, string>
    {
        [DashboardAspectRatio.Free] = "自由比例", [DashboardAspectRatio.Square] = "正方形 · 1:1",
        [DashboardAspectRatio.Wide] = "横向 · 2:1", [DashboardAspectRatio.Tall] = "纵向 · 1:2"
    }.Where(pair => DashboardLayoutEngine.SupportsAspectRatio(kind, pair.Key)).ToDictionary();
    private static IEnumerable<int> DimensionChoices(string kind, DashboardAspectRatio ratio, bool width)
    {
        var minimum = DashboardLayoutEngine.Minimum(kind); var (w, h) = DashboardLayoutEngine.RatioUnits(ratio);
        var start = width ? minimum.Width : minimum.Height; var end = width ? DashboardLayoutEngine.Columns : DashboardLayoutEngine.Rows;
        return Enumerable.Range(start, end - start + 1).Where(value => ratio == DashboardAspectRatio.Free ||
            (width ? value % w == 0 && value / w * h >= minimum.Height && value / w * h <= DashboardLayoutEngine.Rows
                   : value % h == 0 && value / h * w >= minimum.Width && value / h * w <= DashboardLayoutEngine.Columns)).ToArray();
    }
    public DashboardView()
    {
        InitializeComponent();
        DisplayPicker.ItemsSource = new Dictionary<DashboardDisplayMode, string>
        {
            [DashboardDisplayMode.Automatic] = "推荐显示", [DashboardDisplayMode.Number] = "大号数值",
            [DashboardDisplayMode.Progress] = "进度条", [DashboardDisplayMode.Gauge] = "仪表盘"
        };
        var kinds = DashboardLayoutEngine.Kinds.OrderBy(key => key == "cpu.combined" ? 0 : key == "gpu.combined" ? 1 : 2)
            .Select(key => new KeyValuePair<string, string>(key, ChoiceTitle(key))).ToArray();
        WidgetKindPicker.ItemsSource = kinds;
        AddPicker.ItemsSource = kinds; AddPicker.SelectedValue = _addDraft.Kind;
        Rebuild(); UpdateEditingChrome();
        Unloaded += (_, _) => { if (_dragSession is not null) CompleteGesture(true); WidgetGallery.IsOpen = false; };
        AddButton.MouseEnter += (_, _) => { _showAddPreview = true; RefreshAddAvailability(); };
    }
    private void LoadLayout(DashboardLayoutSettings? layout)
    {
        // Saving may temporarily publish the proposed settings through the host binding.
        // Only the transaction result may replace this editor's baseline and draft.
        if (_saving) return;
        var incoming = layout is not null && DashboardLayoutEngine.Validate(layout).Count == 0 ? layout.Clone() : new();
        // Settings notifications also retransfer equivalent clones (for example after a
        // sensor refresh). Such notifications must not discard an active local edit.
        if (IsEditing && DashboardEditHistory.Equivalent(_baseline, incoming)) return;
        _baseline = incoming;
        _working = _baseline.Clone(); IsEditing = false; _selected = null;
        _editHistory.Reset(_baseline);
        if (WidgetCanvas is not null) { Rebuild(); UpdateEditingChrome(); }
    }
    public void BeginEditing()
    {
        if (_saving || IsEditing) return;
        _editHistory.Reset(_working); IsEditing = true; _selected = _working.Widgets.FirstOrDefault()?.Id; UpdateEditingChrome();
        WorkspaceStatus.Text = "拖动组件任意位置移动，右下角调整尺寸；下方可修改显示方式。方向键移动，Ctrl + 方向键缩放。";
    }
    public void CancelLayoutEditing()
    {
        if (_saving) return;
        if (_dragSession is not null) CompleteGesture(true);
        _working = _baseline.Clone(); _editHistory.Reset(_baseline); IsEditing = false; _selected = null; WidgetGallery.IsOpen = false; _showAddPreview = false;
        Rebuild(); UpdateEditingChrome(); WorkspaceStatus.Text = ""; NotifyDraft(null);
    }
    public async Task<bool> CommitLayoutAsync()
    {
        if (!IsEditing || _saving) return false;
        _saving = true; EditActions.IsEnabled = false; Inspector.IsEnabled = false; WidgetCanvas.IsEnabled = false;
        try
        {
            var snapshot = _working.Clone();
            if (SaveLayoutAsync is not null && !await SaveLayoutAsync(snapshot))
            { SetCurrentValue(LayoutProperty, _baseline.Clone()); WorkspaceStatus.Text = "布局保存失败，草稿已保留，请重试。"; return false; }
            _baseline = snapshot.Clone(); _working = snapshot.Clone(); _editHistory.Reset(_baseline); IsEditing = false; _selected = null; WidgetGallery.IsOpen = false; _showAddPreview = false;
            SetCurrentValue(LayoutProperty, snapshot.Clone()); UpdateEditingChrome(); WorkspaceStatus.Text = "组件布局已保存到本机";
            NotifyDraft(null);
            return true;
        }
        catch (Exception ex) { SetCurrentValue(LayoutProperty, _baseline.Clone()); WorkspaceStatus.Text = $"布局保存失败：{ex.Message}"; return false; }
        finally { _saving = false; EditActions.IsEnabled = true; Inspector.IsEnabled = true; WidgetCanvas.IsEnabled = true; }
    }
    public bool TryAddWidget(string kind)
    {
        var minimum = DashboardLayoutEngine.Minimum(kind);
        return TryAddWidget(kind, minimum.Width, minimum.Height);
    }
    public bool TryAddWidget(string kind, int width, int height, DashboardAspectRatio ratio = DashboardAspectRatio.Free)
    {
        if (!IsEditing || _saving) return false;
        var location = DashboardLayoutEngine.FindAvailableWidget(_working, kind, width, height, ratio);
        if (location is null) { WorkspaceStatus.Text = "所选尺寸放不下，请在添加前缩小宽高，或调整现有组件。"; RefreshAddAvailability(); return false; }
        _working.Widgets.Add(location); _selected = location.Id; _showAddPreview = false; Rebuild(); UpdateEditingChrome(); RecordEdit(); WorkspaceStatus.Text = $"已添加 {Title(kind)}，可拖动到合适位置。"; return true;
    }
    public bool TryUpdateWidget(DashboardWidgetSettings proposed)
    {
        if (!IsEditing || _saving || !_frames.ContainsKey(proposed.Id)) return false;
        var plan = DashboardLayoutEngine.TryPlace(_working, proposed);
        if (plan is null) { WorkspaceStatus.Text = "这个尺寸放不下，请缩小组件或移除其他组件。"; RefreshInspector(); return false; }
        bool kindChanged = _working.Widgets.Single(widget => widget.Id == proposed.Id).Kind != proposed.Kind;
        _working = plan;
        if (kindChanged) { Rebuild(); UpdateEditingChrome(); }
        else { PositionFrames(true); RefreshInspector(); }
        RecordEdit(); return true;
    }
    public bool TryChangeWidgetKind(string id, string kind)
    {
        if (!IsEditing || _saving || !DashboardLayoutEngine.Kinds.Contains(kind)) return false;
        var item = _working.Widgets.FirstOrDefault(widget => widget.Id == id); if (item is null) return false;
        var candidate = item.Clone(); candidate.Kind = kind;
        if (!DashboardLayoutEngine.SupportsAspectRatio(kind, candidate.AspectRatio)) candidate.AspectRatio = DashboardAspectRatio.Free;
        (candidate.Width, candidate.Height) = DashboardLayoutEngine.NormalizeSize(kind, candidate.Width, candidate.Height, candidate.AspectRatio);
        if (!DashboardLayoutEngine.SupportsDisplayMode(kind)) candidate.DisplayMode = DashboardDisplayMode.Automatic;
        candidate.Column = Math.Min(candidate.Column, DashboardLayoutEngine.Columns - candidate.Width);
        candidate.Row = Math.Min(candidate.Row, DashboardLayoutEngine.Rows - candidate.Height);
        if (!TryUpdateWidget(candidate)) return false;
        WorkspaceStatus.Text = $"已切换为 {ChoiceTitle(kind)}，点击完成保存。"; return true;
    }
    public bool RemoveWidget(string id)
    {
        if (!IsEditing || _saving) return false;
        var item = _working.Widgets.FirstOrDefault(widget => widget.Id == id); if (item is null) return false;
        _working.Widgets.Remove(item); _selected = _working.Widgets.FirstOrDefault()?.Id; Rebuild(); UpdateEditingChrome();
        RecordEdit();
        WorkspaceStatus.Text = "组件已移除，可从底部重新添加。"; return true;
    }
    public bool RestoreDraft(DashboardLayoutSettings draft)
    {
        if (_saving || IsEditing || DashboardLayoutEngine.Validate(draft).Count != 0 || DashboardEditHistory.Equivalent(_baseline, draft)) return false;
        _editHistory.Reset(_baseline); _working = draft.Clone(); _editHistory.Record(_working);
        IsEditing = true; _selected = _working.Widgets.FirstOrDefault()?.Id;
        Rebuild(); UpdateEditingChrome(); WorkspaceStatus.Text = "已恢复上次未完成的布局草稿。完成可保存，取消可还原；Ctrl + Z 可撤销。"; return true;
    }
    public bool UndoLayoutEdit() => ApplyHistory(false);
    public bool RedoLayoutEdit() => ApplyHistory(true);
    private bool ApplyHistory(bool redo)
    {
        if (!IsEditing || _saving || _dragSession is not null) return false;
        var snapshot = redo ? _editHistory.Redo() : _editHistory.Undo(); if (snapshot is null) return false;
        _working = snapshot; if (!snapshot.Widgets.Any(widget => widget.Id == _selected)) _selected = snapshot.Widgets.FirstOrDefault()?.Id;
        Rebuild(); UpdateEditingChrome(); NotifyCurrentDraft();
        WorkspaceStatus.Text = redo ? "已重做上一步布局操作。" : "已撤销上一步布局操作。"; return true;
    }
    private void RecordEdit()
    {
        if (_editHistory.Record(_working)) NotifyCurrentDraft();
        RefreshUndoButtons();
    }
    private void RefreshUndoButtons() { UndoButton.IsEnabled = CanUndo; RedoButton.IsEnabled = CanRedo; }
    private void NotifyCurrentDraft() => NotifyDraft(DashboardEditHistory.Equivalent(_baseline, _working) ? null : _working);
    private void NotifyDraft(DashboardLayoutSettings? draft) => DraftChanged?.Invoke(this, new(_baseline, draft));
    private void Rebuild()
    {
        foreach (var frame in _frames.Values) frame.Motion.EndFollowing();
        _dragSession = null; _gestureId = null; DropPreview.Visibility = InvalidDropPreview.Visibility = Visibility.Collapsed;
        WidgetCanvas.Children.Clear(); _frames.Clear();
        foreach (var widget in _working.Widgets)
        {
            FrameworkElement content;
            if (widget.Kind == "fan.history")
            {
                var chart = new RpmHistoryChart { MinHeight = 0 };
                foreach (var sample in _history) { if (sample.Rpm is { } rpm) chart.AddSample(sample.Time, rpm); else chart.MarkGap(sample.Time); }
                content = chart;
            }
            else if (widget.Kind == "fan.control")
            {
                content = new DashboardFanControl(); content.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
            }
            else content = new DashboardWidgetHost { WidgetKey = widget.Kind, Metrics = Metrics, DisplayMode = widget.DisplayMode };
            var border = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), ClipToBounds = true, Focusable = true, Tag = widget.Id };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var root = new Grid();
            var contents = new Grid { Margin = new Thickness(14, 8, 14, 12) }; contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) }); contents.RowDefinitions.Add(new RowDefinition()); root.Children.Add(contents);
            var title = ControlUi.Text(Title(widget.Kind), size: 12); title.SetResourceReference(TextBlock.FontWeightProperty, "UiHeadingWeight"); title.VerticalAlignment = VerticalAlignment.Center; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(0, 0, 24, 0); contents.Children.Add(title);
            if (widget.Kind == "cpu.voltage")
            {
                var voltageTitle = new MultiBinding { Mode = BindingMode.OneWay, Converter = new VoltageCardTitleConverter() };
                voltageTitle.Bindings.Add(new Binding("Metrics.CpuVoltageCardTitle") { Source = this, Mode = BindingMode.OneWay, FallbackValue = "CPU 核心电压", TargetNullValue = "CPU 核心电压" });
                voltageTitle.Bindings.Add(new Binding(nameof(ActualWidth)) { Source = title, Mode = BindingMode.OneWay });
                title.SetBinding(TextBlock.TextProperty, voltageTitle);
                title.SetBinding(ToolTipProperty, new Binding("Metrics.CpuVoltageCardTitle") { Source = this, Mode = BindingMode.OneWay, FallbackValue = "CPU 核心电压", TargetNullValue = "CPU 核心电压" });
            }
            var configure = new Button { Content = "⋯", FontSize = 17, Padding = new Thickness(3, 0, 3, 0), Height = 24, MinWidth = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "编辑此组件" };
            AutomationProperties.SetName(configure, $"编辑 {Title(widget.Kind)}"); configure.Click += (_, _) => { BeginEditing(); SelectWidget(widget.Id); }; contents.Children.Add(configure);
            var body = new Grid { ClipToBounds = true, Margin = new Thickness(0, 3, 0, 0) }; body.Children.Add(content); Grid.SetRow(body, 1); contents.Children.Add(body);
            // A full-card input surface intercepts content controls only while editing.
            var move = new Thumb { Style = (Style)FindResource("WidgetDragHandle") };
            AutomationProperties.SetName(move, $"移动 {Title(widget.Kind)}"); Panel.SetZIndex(move, 1); root.Children.Add(move);
            var resize = new Thumb { Style = (Style)FindResource("WidgetResizeHandle"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 7, 5) };
            AutomationProperties.SetName(resize, $"缩放 {Title(widget.Kind)}"); Panel.SetZIndex(resize, 2); root.Children.Add(resize);
            border.Child = root; WidgetCanvas.Children.Add(border); _frames.Add(widget.Id, new(border, content, move, resize, configure, title, new DashboardWidgetMotion(border)));
            border.PreviewMouseLeftButtonDown += (_, _) => { if (IsEditing && _selected != widget.Id) SelectWidget(widget.Id); };
            move.DragStarted += (_, _) => StartGesture(widget.Id, false); resize.DragStarted += (_, _) => StartGesture(widget.Id, true);
            move.DragDelta += (_, _) => ContinueGesture(); resize.DragDelta += (_, _) => ContinueGesture();
            move.DragCompleted += (_, e) => CompleteGesture(e.Canceled); resize.DragCompleted += (_, e) => CompleteGesture(e.Canceled);
        }
        EmptyHint.Visibility = _working.Widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionFrames(false); RefreshMetrics();
    }
    private void RefreshMetrics()
    {
        foreach (var host in _frames.Values.Select(frame => frame.Content).OfType<DashboardWidgetHost>()) host.Metrics = Metrics;
        foreach (var button in GalleryItems.Children.OfType<Button>())
            if (button.Content is Panel panel)
                foreach (var host in panel.Children.OfType<DashboardWidgetHost>()) host.Metrics = Metrics;
    }
    private void UpdateEditingChrome()
    {
        EditButton.Visibility = IsEditing ? Visibility.Collapsed : Visibility.Visible;
        Inspector.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        GuideCanvas.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        EditorScrim.Visibility = EditorBoardBackdrop.Visibility = EditorBoundary.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, frame) in _frames)
        {
            Panel.SetZIndex(frame.Border, IsEditing && id == _selected ? 2 : 0);
            frame.Move.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
            frame.Resize.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
            frame.Configure.Visibility = IsEditing ? Visibility.Collapsed : Visibility.Visible;
            frame.Content.IsHitTestVisible = !IsEditing;
            frame.Border.BorderThickness = new Thickness(IsEditing && id == _selected ? 2 : 1);
            frame.Border.SetResourceReference(Border.BorderBrushProperty, IsEditing && id == _selected ? "AccentBrush" : "BorderBrush");
        }
        RefreshInspector(); RefreshUndoButtons(); PositionFrames(false);
        bool changed = _editingChromeShown != IsEditing; _editingChromeShown = IsEditing;
        UpdateBoardPresentation(changed, changed && IsEditing);
    }
    private void SelectWidget(string id)
    {
        _selected = id; UpdateEditingChrome();
        if (_frames.TryGetValue(id, out var frame)) frame.Border.Focus();
    }
    private void RefreshInspector()
    {
        _updatingInspector = true;
        try
        {
            var item = Selected; SelectedInspector.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            if (item is null) return;
            WidgetKindPicker.SelectedValue = item.Kind;
            DisplayPicker.IsEnabled = DashboardLayoutEngine.SupportsDisplayMode(item.Kind); DisplayPicker.SelectedValue = item.DisplayMode;
            WidthPicker.ItemsSource = DimensionChoices(item.Kind, item.AspectRatio, true); WidthPicker.SelectedItem = item.Width;
            HeightPicker.ItemsSource = DimensionChoices(item.Kind, item.AspectRatio, false); HeightPicker.SelectedItem = item.Height;
            RatioPicker.ItemsSource = RatioChoices(item.Kind); RatioPicker.SelectedValue = item.AspectRatio;
        }
        finally { _updatingInspector = false; RefreshAddAvailability(); }
    }
    private void RefreshAddOptions()
    {
        _updatingAdd = true;
        try
        {
            AddWidthPicker.ItemsSource = DimensionChoices(_addDraft.Kind, _addDraft.AspectRatio, true); AddWidthPicker.SelectedItem = _addDraft.Width;
            AddHeightPicker.ItemsSource = DimensionChoices(_addDraft.Kind, _addDraft.AspectRatio, false); AddHeightPicker.SelectedItem = _addDraft.Height;
            AddRatioPicker.ItemsSource = RatioChoices(_addDraft.Kind); AddRatioPicker.SelectedValue = _addDraft.AspectRatio;
        }
        finally { _updatingAdd = false; }
        RefreshAddAvailability();
    }
    private void RefreshAddAvailability()
    {
        var available = DashboardLayoutEngine.FindAvailableWidget(_working, _addDraft.Kind, _addDraft.Width, _addDraft.Height, _addDraft.AspectRatio);
        _recommendedAdd = available is null ? FindRecommendedAddSize() : null;
        AddButton.IsEnabled = IsEditing && !_saving && available is not null;
        AddFitStatus.Text = _working.Widgets.Count >= 20 ? "已达上限" : available is not null ? "可添加" : "放不下";
        AddRecommendation.Visibility = _recommendedAdd is null ? Visibility.Collapsed : Visibility.Visible;
        AddRecommendation.Text = _recommendedAdd is { } recommendation ? $"↳ {recommendation.Width}×{recommendation.Height}" : "";
        AddFitStatus.SetResourceReference(TextBlock.ForegroundProperty, available is not null ? "SuccessBrush" : "WarningBrush");
        AddFitStatus.ToolTip = $"{ChoiceTitle(_addDraft.Kind)} · {_addDraft.Width} × {_addDraft.Height} 格。" +
            (_working.Widgets.Count >= 20 ? "最多放置 20 个组件，请先移除一个。" : available is not null ? "有可用空位，添加不会挪动现有组件。" : "没有连续空位，请先减小宽高，或调整已有组件；数值单项最小支持 2 × 2 格。");
        AddButton.ToolTip = AddFitStatus.ToolTip;
        AddRecommendation.ToolTip = _recommendedAdd is { } fit ? $"点击改用可放入的 {fit.Width} × {fit.Height} 格；现有组件不会被移动。" : null;
        if (IsEditing && _showAddPreview && _dragSession is null && available is not null)
        {
            var rect = _geometry.GetBounds(available); PositionPreview(AddPlacementPreview, rect);
            AddPlacementLabel.Text = $"待添加\n{ChoiceTitle(_addDraft.Kind)}\n{available.Width} × {available.Height}";
        }
        else AddPlacementPreview.Visibility = Visibility.Collapsed;
    }
    private DashboardWidgetSettings? FindRecommendedAddSize()
    {
        if (_working.Widgets.Count >= 20) return null;
        var minimum = DashboardLayoutEngine.Minimum(_addDraft.Kind);
        var sizes = from width in Enumerable.Range(minimum.Width, _addDraft.Width - minimum.Width + 1)
                    from height in Enumerable.Range(minimum.Height, _addDraft.Height - minimum.Height + 1)
                    orderby width * height descending, Math.Abs(width * _addDraft.Height - height * _addDraft.Width), width descending
                    select (width, height);
        foreach (var (width, height) in sizes)
            if (DashboardLayoutEngine.FindAvailableWidget(_working, _addDraft.Kind, width, height, _addDraft.AspectRatio) is { } fit) return fit;
        return null;
    }
    private void ApplyRecommendedSize(object sender, MouseButtonEventArgs e)
    {
        if (_recommendedAdd is not { } fit) return;
        _addDraft.Width = fit.Width; _addDraft.Height = fit.Height; _showAddPreview = true; RefreshAddOptions();
        WorkspaceStatus.Text = $"已选择可放入的 {fit.Width} × {fit.Height} 格，现有组件保持原位。"; e.Handled = true;
    }
    private void AddKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAdd || AddPicker.SelectedValue is not string kind) return;
        var minimum = DashboardLayoutEngine.Minimum(kind);
        _addDraft = new() { Kind = kind, Width = minimum.Width, Height = minimum.Height }; _showAddPreview = IsEditing; RefreshAddOptions();
    }
    private void AddDimensionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAdd || AddWidthPicker.SelectedItem is not int width || AddHeightPicker.SelectedItem is not int height) return;
        (_addDraft.Width, _addDraft.Height) = DashboardLayoutEngine.NormalizeSize(_addDraft.Kind, width, height, _addDraft.AspectRatio, ReferenceEquals(sender, AddWidthPicker));
        _showAddPreview = IsEditing;
        RefreshAddOptions();
    }
    private void AddRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAdd || AddRatioPicker.SelectedValue is not DashboardAspectRatio ratio) return;
        _addDraft.AspectRatio = ratio;
        (_addDraft.Width, _addDraft.Height) = DashboardLayoutEngine.NormalizeSize(_addDraft.Kind, _addDraft.Width, _addDraft.Height, ratio);
        _showAddPreview = IsEditing;
        RefreshAddOptions();
    }
    private void WidgetKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || Selected is not { } item || WidgetKindPicker.SelectedValue is not string kind || kind == item.Kind) return;
        TryChangeWidgetKind(item.Id, kind);
    }
    private void PositionFrames(bool animate, DashboardLayoutSettings? arrangement = null, string? forceId = null)
    {
        if (Workspace.ActualWidth <= 0 || Workspace.ActualHeight <= 0) return;
        _geometry = new DashboardGridGeometry(Workspace.ActualWidth, Workspace.ActualHeight);
        foreach (var widget in (arrangement ?? _working).Widgets)
        {
            if (!_frames.TryGetValue(widget.Id, out var frame) || frame.Motion.IsFollowing) continue;
            frame.Motion.SetTarget(_geometry.GetBounds(widget), animate, widget.Id == forceId);
        }
        DrawGuides();
        RefreshAddAvailability();
    }
    private void UpdateBoardPresentation(bool animate, bool revealDrawer = false)
    {
        var height = Workspace.ActualHeight; if (height <= 0) return;
        var drawerHeight = Inspector.ActualHeight > 0 ? Inspector.ActualHeight : 102;
        var scale = IsEditing ? Math.Clamp((height - drawerHeight - 20) / height, .55, .92) : 1;
        var shift = IsEditing ? -(drawerHeight + 12) / 2 : 0;
        var shouldAnimate = animate && IsLoaded && SystemParameters.ClientAreaAnimation;
        Transition(BoardZoom, ScaleTransform.ScaleXProperty, scale); Transition(BoardZoom, ScaleTransform.ScaleYProperty, scale);
        Transition(BoardShift, TranslateTransform.YProperty, shift);
        if (shouldAnimate && IsEditing && revealDrawer)
        {
            DrawerShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(35, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            Inspector.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
        void Transition(Animatable target, DependencyProperty property, double value)
        {
            if (Math.Abs((double)target.GetAnimationBaseValue(property) - value) < .0001) return;
            var current = (double)target.GetValue(property); target.BeginAnimation(property, null); target.SetValue(property, value);
            if (shouldAnimate) target.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    private void DrawGuides()
    {
        if (!IsEditing) { GuideCanvas.Children.Clear(); _guideSize = default; return; }
        var size = new Size(Workspace.ActualWidth, Workspace.ActualHeight);
        if (_guideSize == size && GuideCanvas.Children.Count > 0) return;
        _guideSize = size; GuideCanvas.Children.Clear();
        for (var row = 0; row <= DashboardLayoutEngine.Rows; row++)
            for (var col = 0; col <= DashboardLayoutEngine.Columns; col++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 2, Height = 2, Opacity = .55 };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MutedBrush");
                Canvas.SetLeft(dot, col * _geometry.PitchX - 1); Canvas.SetTop(dot, row * _geometry.PitchY - 1); GuideCanvas.Children.Add(dot);
            }
    }
    private void StartGesture(string id, bool resize)
    {
        if (!IsEditing || _saving) return;
        WidgetGallery.IsOpen = false; _showAddPreview = false; AddPlacementPreview.Visibility = Visibility.Collapsed;
        SelectWidget(id); _gestureId = id; _gesturePointer = Mouse.GetPosition(WidgetCanvas);
        var motion = _frames[id].Motion; motion.BeginFollowing();
        _dragSession = new DashboardDragSession(_working, id, _geometry, motion.VisualBounds, resize);
        _gestureFits = true; InvalidDropPreview.Visibility = Visibility.Collapsed;
        ShowDropPreview();
    }
    private void ContinueGesture()
    {
        if (_dragSession is null || _gestureId is null) return;
        ApplyGestureDelta(Mouse.GetPosition(WidgetCanvas) - _gesturePointer);
    }
    private void ApplyGestureDelta(Vector delta)
    {
        if (_dragSession is null || _gestureId is null) return;
        var update = _dragSession.Update(delta);
        _frames[_gestureId].Motion.SetVisualBounds(update.VisualBounds);
        _gestureFits = update.CandidateFits;
        if (_gestureFits)
        { InvalidDropPreview.Visibility = Visibility.Collapsed; WorkspaceStatus.Text = "松手将吸附到浅色框标出的位置。"; }
        else
        { PositionPreview(InvalidDropPreview, update.VisualBounds); WorkspaceStatus.Text = "红框位置放不下；松手将返回浅色框标出的最后有效位置。"; }
        if (!update.PlacementChanged) return;
        PositionFrames(true, _dragSession.PreviewLayout); ShowDropPreview();
    }
    private void CompleteGesture(bool canceled)
    {
        if (_dragSession is null || _gestureId is null) return;
        if (!canceled) _working = _dragSession.PreviewLayout.Clone();
        var wasValid = _gestureFits;
        var id = _gestureId; _frames[id].Motion.EndFollowing();
        _dragSession = null; _gestureId = null; DropPreview.Visibility = InvalidDropPreview.Visibility = Visibility.Collapsed;
        PositionFrames(true, forceId: id); RefreshInspector();
        if (!canceled) RecordEdit();
        WorkspaceStatus.Text = canceled ? "已取消本次拖动。" : wasValid ? "位置已吸附，点击完成保存布局。" : "当前位置放不下，已返回预览标出的最后有效位置；可撤销本次操作。";
    }
    private void ShowDropPreview()
    {
        if (_dragSession is null || _gestureId is null) return;
        var item = _dragSession.PreviewLayout.Widgets.Single(widget => widget.Id == _gestureId);
        PositionPreview(DropPreview, _geometry.GetBounds(item));
    }
    private static void PositionPreview(Border preview, Rect rect)
    { Canvas.SetLeft(preview, rect.Left); Canvas.SetTop(preview, rect.Top); preview.Width = rect.Width; preview.Height = rect.Height; preview.Visibility = Visibility.Visible; }
    private static void ConstrainRatio(DashboardWidgetSettings item, bool fromWidth)
    {
        (item.Width, item.Height) = DashboardLayoutEngine.NormalizeSize(item.Kind, item.Width, item.Height, item.AspectRatio, fromWidth);
    }
    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || Selected is not { } item || DisplayPicker.SelectedValue is not DashboardDisplayMode mode) return;
        item.DisplayMode = mode; if (_frames[item.Id].Content is DashboardWidgetHost host) host.DisplayMode = mode;
        RecordEdit();
        WorkspaceStatus.Text = $"{Title(item.Kind)}已切换显示方式。";
    }
    private void DimensionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || Selected is not { } item || WidthPicker.SelectedItem is not int width || HeightPicker.SelectedItem is not int height) return;
        var candidate = item.Clone(); candidate.Width = width; candidate.Height = height; ConstrainRatio(candidate, ReferenceEquals(sender, WidthPicker));
        candidate.Column = Math.Min(candidate.Column, DashboardLayoutEngine.Columns - candidate.Width); candidate.Row = Math.Min(candidate.Row, DashboardLayoutEngine.Rows - candidate.Height);
        if (TryUpdateWidget(candidate)) WorkspaceStatus.Text = $"尺寸已调整为 {candidate.Width} × {candidate.Height} 格。";
    }
    private void RatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || Selected is not { } item || RatioPicker.SelectedValue is not DashboardAspectRatio ratio) return;
        var candidate = item.Clone(); candidate.AspectRatio = ratio; ConstrainRatio(candidate, true);
        candidate.Column = Math.Min(candidate.Column, DashboardLayoutEngine.Columns - candidate.Width); candidate.Row = Math.Min(candidate.Row, DashboardLayoutEngine.Rows - candidate.Height);
        if (TryUpdateWidget(candidate)) WorkspaceStatus.Text = "比例已设置，拖动缩放时会保持该比例。";
    }
    public void AddRpmSample(DateTimeOffset time, uint rpm)
    {
        AppendHistory(time, rpm); foreach (var chart in _frames.Values.Select(frame => frame.Content).OfType<RpmHistoryChart>()) chart.AddSample(time, rpm);
    }
    public void MarkRpmGap(DateTimeOffset time)
    {
        AppendHistory(time, null); foreach (var chart in _frames.Values.Select(frame => frame.Content).OfType<RpmHistoryChart>()) chart.MarkGap(time);
    }
    private void AppendHistory(DateTimeOffset time, uint? rpm)
    {
        if (_history.Count > 0 && time < _history[^1].Time) return;
        _history.Add((time, rpm)); _history.RemoveAll(sample => sample.Time < time - TimeSpan.FromMinutes(5.2)); if (_history.Count > 4096) _history.RemoveRange(0, _history.Count - 4096);
    }
    private void DashboardKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEditing || _saving || _dragSession is not null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y)
        { if (e.Key == Key.Y || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) RedoLayoutEdit(); else UndoLayoutEdit(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is not Border { Tag: string } || Selected is not { } item) return;
        if (e.Key == Key.Delete) { RemoveWidget(item.Id); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var candidate = item.Clone(); var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0; var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { candidate.Width += dx; candidate.Height += dy; ConstrainRatio(candidate, dx != 0); }
        else { candidate.Column += dx; candidate.Row += dy; } TryUpdateWidget(candidate); e.Handled = true;
    }
    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dragSession is not null) CompleteGesture(true);
        PositionFrames(false); UpdateBoardPresentation(false);
    }
    private void InspectorSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBoardPresentation(IsEditing && IsLoaded);
    private void EditorPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox picker && picker.Template.FindName("PART_Popup", picker) is Popup popup) popup.Placement = PlacementMode.Top;
    }
    private void StartEditing(object sender, RoutedEventArgs e) => BeginEditing();
    private void CancelEditing(object sender, RoutedEventArgs e) => CancelLayoutEditing();
    private async void FinishEditing(object sender, RoutedEventArgs e) => await CommitLayoutAsync();
    private void ResetLayout(object sender, RoutedEventArgs e) { _working = new(); _selected = _working.Widgets[0].Id; Rebuild(); UpdateEditingChrome(); RecordEdit(); WorkspaceStatus.Text = "已恢复推荐布局，可撤销；点击完成保存。"; }
    private void UndoEditing(object sender, RoutedEventArgs e) => UndoLayoutEdit();
    private void RedoEditing(object sender, RoutedEventArgs e) => RedoLayoutEdit();
    private void ToggleGallery(object sender, RoutedEventArgs e)
    {
        if (WidgetGallery.IsOpen) { WidgetGallery.IsOpen = false; return; }
        if (GalleryItems.Children.Count == 0) PopulateGallery();
        _showAddPreview = true; RefreshAddAvailability(); WidgetGallery.IsOpen = true;
    }
    private void GalleryClosed(object? sender, EventArgs e) => GalleryButton.Focus();
    private void PopulateGallery()
    {
        foreach (var kind in ((KeyValuePair<string, string>[])AddPicker.ItemsSource).Select(pair => pair.Key))
        {
            var panel = new StackPanel(); var label = ControlUi.Text(ChoiceTitle(kind), size: 11); label.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(label);
            if (kind is not ("fan.control" or "fan.history"))
                panel.Children.Add(new DashboardWidgetHost { WidgetKey = kind, Metrics = Metrics, Height = 84, IsHitTestVisible = false });
            else
            {
                var sample = new Border { Height = 84, CornerRadius = new CornerRadius(12), Padding = new Thickness(12) };
                sample.SetResourceReference(Border.BackgroundProperty, "SelectionBrush");
                sample.Child = ControlUi.Text(kind == "fan.history" ? "╱╲＿＿╱╲╱\n最近 5 分钟的底座转速" : "自动 / 手动\n风扇策略与工作状态", size: 11); panel.Children.Add(sample);
            }
            var minimum = DashboardLayoutEngine.Minimum(kind);
            var sizeLabel = ControlUi.Text($"最小 {minimum.Width} × {minimum.Height} 格", brush: "MutedBrush", size: 10); sizeLabel.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(sizeLabel);
            var button = new Button { Content = panel, Width = 190, Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = kind };
            AutomationProperties.SetName(button, $"预览并选择 {ChoiceTitle(kind)}");
            button.Click += (_, _) => { AddPicker.SelectedValue = kind; _showAddPreview = true; RefreshAddAvailability(); WidgetGallery.IsOpen = false; };
            GalleryItems.Children.Add(button);
        }
    }
    private void ChooseSizePreset(object sender, RoutedEventArgs e)
    {
        var minimum = DashboardLayoutEngine.Minimum(_addDraft.Kind); var preset = (sender as Button)?.Tag as string;
        _addDraft.AspectRatio = preset == "wide" ? DashboardAspectRatio.Wide : DashboardAspectRatio.Free;
        (_addDraft.Width, _addDraft.Height) = DashboardLayoutEngine.NormalizeSize(_addDraft.Kind,
            preset == "compact" ? minimum.Width : preset == "wide" ? Math.Max(6, minimum.Width) : Math.Max(4, minimum.Width),
            preset == "compact" ? minimum.Height : Math.Max(3, minimum.Height), _addDraft.AspectRatio);
        _showAddPreview = true; RefreshAddOptions(); WidgetGallery.IsOpen = false;
    }
    private void RemoveSelected(object sender, RoutedEventArgs e) { if (_selected is not null) RemoveWidget(_selected); }
    private void AddWidget(object sender, RoutedEventArgs e) => TryAddWidget(_addDraft.Kind, _addDraft.Width, _addDraft.Height, _addDraft.AspectRatio);
    private sealed class VoltageCardTitleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var full = values.ElementAtOrDefault(0) as string ?? "CPU 核心电压";
            var width = values.ElementAtOrDefault(1) is double available ? available : 0;
            // Keep VID explicit even when the smallest card cannot fit its full descriptive title.
            return full.Contains("VID", StringComparison.OrdinalIgnoreCase) && width is > 0 and < 86 ? width < 50 ? "VID" : "CPU VID" : full;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => [Binding.DoNothing, Binding.DoNothing];
    }
    private void OpenAdvanced(object sender, RoutedEventArgs e) => AdvancedNavigationRequested?.Invoke(this, EventArgs.Empty);
}
