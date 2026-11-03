using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Annotate;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长图编辑器：把拼好的长图当成一张可以上下滚动的画布来标注。
///
/// 为什么不复用覆盖层那套（OverlayWindow + CaptureSession）：那套的前提是
/// 「底图就是当前桌面、1:1、和屏幕一样大」。长图动辄几千像素高，塞不进一屏；
/// 硬塞只能缩放到整屏，字小到没法标。这里是另一套前提 —— 底图是一张普通位图，
/// 画布比屏幕高，靠滚动看，所以坐标从「选区局部物理像素」换成「图片像素」，
/// 其余（AnnotationDocument / AnnotationController / AnnotationLayer / MosaicSource）
/// 全部原样复用：标注实现本来就不知道显示器存在。
///
/// 坐标换算只有一处：图片像素 ÷ <see cref="_zoom"/> = 画布 DIP。
/// </summary>
internal sealed class LongImageEditorWindow : Window
{
    private const double ToolbarHeight = 46;
    private const double HeaderHeight = 46;
    private const double FooterHeight = 56;
    private const double SurfacePadding = 14;

    /// <summary>控制点命中半径（DIP），和覆盖层保持一致。</summary>
    private const double HandleSize = 9;

    private readonly BitmapSource _image;
    private readonly PixelRect _origin;
    private readonly CaptureAction _defaultAction;

    private readonly AnnotationDocument _document = new();
    private readonly AnnotationController _controller;
    private readonly AnnotationLayer _layer = new();
    private readonly Grid _surface = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Image _imageView = new();
    private readonly TextBox _text;
    private readonly TextBlock _dims = new();
    private readonly TextBlock _sizeLabel = new();
    private readonly StackPanel _sizeGroup = new();
    private readonly StackPanel _mosaicChips = new();
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _deleteButton;

    private double _zoom = 1;
    private bool _zoomApplied;

    private ToolKind _tool = ToolKind.None;
    private Color _color = ToolOptions.Palette[0];
    private double _thickness = ToolOptions.Thickness.Default;
    private double _fontSize = ToolOptions.FontSize.Default;
    private double _mosaicBlock = ToolOptions.MosaicBlock.Default;
    private MosaicStyle _mosaicStyle = MosaicStyle.Area;
    private MosaicSource? _mosaic;
    private IAnnotationContext? _mosaicContext;

    /// <summary>当前选中的标注在文档里的下标，-1 表示没选中。</summary>
    private int _selected = -1;

    private bool _drawing;
    private bool _moving;
    private bool _resizing;
    private int _dragHandle = -1;
    private Point _dragStart;
    private Annotation? _dragBefore;

    private Point _textOrigin;
    private bool _closing;

    /// <summary>拼好的长图 + 它在屏幕上的原始位置 + 默认去向。</summary>
    public LongImageEditorWindow(BitmapSource image, PixelRect origin, CaptureAction defaultAction)
    {
        _image = image;
        _origin = origin;
        _defaultAction = defaultAction;

        _controller = new AnnotationController
        {
            Document = _document,
            // 标注最终烧进 96 DPI 的长图位图，这里不跟屏幕缩放走：
            // FormattedText 的 PixelsPerDip 影响字宽计算，按屏幕缩放算会让导出的字距跟画布上不一致
            PixelsPerDip = 1.0,
        };

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x26));
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Opacity = 0;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XkScreenshot;component/Scroll/LongImageEditorTheme.xaml"),
        });

        _text = CreateTextInput();
        _undoButton = IconButton(Icons.Undo, "撤销  Ctrl+Z", Undo);
        _redoButton = IconButton(Icons.Redo, "重做  Ctrl+Y", Redo);
        _deleteButton = IconButton(Icons.Trash, "删除选中的标注  Delete", DeleteSelected);

        Content = BuildLayout();
        SyncEditButtons();

        _document.Changed += OnDocumentChanged;
        SourceInitialized += (_, _) => Place();
        ContentRendered += (_, _) => ApplyZoom();
        Closed += (_, _) => { _mosaic = null; _mosaicContext = null; };
    }

    /// <summary>用户选了去向，参数是烧好标注的长图。</summary>
    public event Action<BitmapSource, CaptureAction>? Finished;

    /// <summary>用户放弃编辑。</summary>
    public event Action? Cancelled;

    /// <summary>按钮高亮和 Enter 都只认这三个去向；设置里万一存了 OCR/翻译，退回复制。</summary>
    private CaptureAction EffectiveDefault => _defaultAction is CaptureAction.Copy
        or CaptureAction.Pin or CaptureAction.Save
        ? _defaultAction
        : CaptureAction.Copy;

    // ---------------- 界面 ----------------

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ToolbarHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FooterHeight) });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var body = BuildBody();
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return new Border
        {
            Background = Brush("EdBg"),
            BorderBrush = Brush("EdBorder"),
            BorderThickness = new Thickness(1),
            Child = root,
        };
    }

    private UIElement BuildHeader()
    {
        var title = new TextBlock
        {
            Text = "编辑长图",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("EdText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _dims.FontSize = 11.5;
        _dims.Foreground = Brush("EdTextMuted");
        _dims.Margin = new Thickness(12, 0, 0, 0);
        _dims.VerticalAlignment = VerticalAlignment.Center;

        var close = IconButton(Icons.Close, "关闭  Esc", Cancel);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Right);

        var header = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(14, 0, 10, 0),
            Children = { close, title, _dims },
        };

        // 没有系统标题栏，整条头都能拖
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { /* 按键已经松开了 */ }
        };

        return new Border
        {
            BorderBrush = Brush("EdBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };
    }

    private UIElement BuildToolbar()
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        tools.Children.Add(ToolButton(Icons.Cursor, "选择 / 移动标注", ToolKind.None, isDefault: true));
        tools.Children.Add(ToolButton(Icons.Square, "矩形", ToolKind.Rectangle));
        tools.Children.Add(ToolButton(Icons.Circle, "椭圆", ToolKind.Ellipse));
        tools.Children.Add(ToolButton(Icons.ArrowUpRight, "箭头", ToolKind.Arrow));
        tools.Children.Add(ToolButton(Icons.Pencil, "画笔", ToolKind.Ink));
        tools.Children.Add(ToolButton(Icons.Type, "文字", ToolKind.Text));
        tools.Children.Add(ToolButton(Icons.Grid, "马赛克", ToolKind.Mosaic));

        tools.Children.Add(Separator());

        foreach (var color in ToolOptions.Palette)
            tools.Children.Add(ColorSwatch(color));

        tools.Children.Add(Separator());

        // 粗细 / 字号 / 粒度：每种工具只有一个尺寸参数，所以不必先选调什么
        var minus = new Button
        {
            Content = "−",
            Style = (Style)FindResource("EdButton"),
            Width = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            ToolTip = "调小",
        };
        minus.Click += (_, _) => StepSize(-1);

        var plus = new Button
        {
            Content = "+",
            Style = (Style)FindResource("EdButton"),
            Width = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "调大",
        };
        plus.Click += (_, _) => StepSize(1);

        _sizeLabel.FontSize = 11.5;
        _sizeLabel.Foreground = Brush("EdTextSecondary");
        _sizeLabel.MinWidth = 48;
        _sizeLabel.TextAlignment = TextAlignment.Center;
        _sizeLabel.VerticalAlignment = VerticalAlignment.Center;

        _sizeGroup.Orientation = Orientation.Horizontal;
        _sizeGroup.VerticalAlignment = VerticalAlignment.Center;
        _sizeGroup.Children.Add(minus);
        _sizeGroup.Children.Add(_sizeLabel);
        _sizeGroup.Children.Add(plus);

        _mosaicChips.Orientation = Orientation.Horizontal;
        _mosaicChips.VerticalAlignment = VerticalAlignment.Center;
        _mosaicChips.Margin = new Thickness(6, 0, 0, 0);
        _mosaicChips.Children.Add(MosaicChip("框选", MosaicStyle.Area));
        _mosaicChips.Children.Add(MosaicChip("涂抹", MosaicStyle.Brush));

        tools.Children.Add(_sizeGroup);
        tools.Children.Add(_mosaicChips);

        tools.Children.Add(Separator());
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(_deleteButton);

        UpdateSizeUi();

        return new Border
        {
            BorderBrush = Brush("EdBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Margin = new Thickness(10, 0, 10, 0), Child = tools },
            },
        };
    }

    private UIElement BuildBody()
    {
        _imageView.Source = _image;
        _imageView.Stretch = Stretch.Fill;
        _imageView.IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.HighQuality);

        _layer.Document = _document;
        _layer.Context = MosaicContext;
        _layer.IsHitTestVisible = false;

        _surface.Background = Brushes.Transparent;
        // 显式宽度 + 默认 Stretch 会在更宽的视口里被居中；这里要的是左上角贴边
        _surface.HorizontalAlignment = HorizontalAlignment.Left;
        _surface.VerticalAlignment = VerticalAlignment.Top;
        _surface.Children.Add(_imageView);
        _surface.Children.Add(_layer);
        _surface.Children.Add(_text);

        _surface.MouseLeftButtonDown += OnSurfaceMouseDown;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseLeftButtonUp += OnSurfaceMouseUp;
        _surface.LostMouseCapture += (_, _) => CancelDrag();
        _surface.Cursor = Cursors.Arrow;

        _scroll.Content = _surface;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.PanningMode = PanningMode.VerticalOnly;
        _scroll.Focusable = false;

        // 图比可视区窄时靠左上摆，默认的居中会让画布两边各空一大块
        _scroll.HorizontalContentAlignment = HorizontalAlignment.Left;
        _scroll.VerticalContentAlignment = VerticalAlignment.Top;

        // 系统滚动条是浅色的，贴在深色画布边上像一条亮缝
        _scroll.Resources.Add(typeof(ScrollBar), (Style)FindResource("EdScrollBar"));

        return new Border
        {
            Background = Brush("EdSurface"),
            Padding = new Thickness(SurfacePadding),
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0E)),
                Child = _scroll,
            },
        };
    }

    private UIElement BuildFooter()
    {
        string action = EffectiveDefault switch
        {
            CaptureAction.Pin => "贴图",
            CaptureAction.Save => "保存",
            _ => "复制",
        };

        var hint = new TextBlock
        {
            Text = $"Ctrl+Z 撤销　·　Delete 删除　·　Esc 退出　·　Enter {action}",
            FontSize = 11.5,
            Foreground = Brush("EdTextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(ActionButton("复制", CaptureAction.Copy, isDefault: EffectiveDefault == CaptureAction.Copy));
        buttons.Children.Add(ActionButton("保存", CaptureAction.Save, isDefault: EffectiveDefault == CaptureAction.Save));
        buttons.Children.Add(ActionButton("贴图", CaptureAction.Pin, isDefault: EffectiveDefault == CaptureAction.Pin));
        DockPanel.SetDock(buttons, Dock.Right);

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(14, 0, 14, 0),
            Children = { buttons, hint },
        };

        return new Border
        {
            BorderBrush = Brush("EdBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row,
        };
    }

    private RadioButton ToolButton(Geometry icon, string tip, ToolKind kind, bool isDefault = false)
    {
        var button = new RadioButton
        {
            GroupName = "EdTools",
            Style = (Style)FindResource("EdToolToggle"),
            ToolTip = tip,
            IsChecked = isDefault,
            Content = new IconView { Icon = icon, IconSize = 18, IconBrush = Brush("EdText") },
        };

        button.Checked += (_, _) => SetTool(kind);
        return button;
    }

    private RadioButton ColorSwatch(Color color)
    {
        var swatch = new RadioButton
        {
            GroupName = "EdColors",
            Style = (Style)FindResource("EdSwatch"),
            Background = new SolidColorBrush(color),
            ToolTip = "标注颜色",
            IsChecked = color == _color,
        };

        swatch.Checked += (_, _) =>
        {
            _color = color;
            _controller.Stroke = color;
            ApplyStyleToSelection();
        };
        return swatch;
    }

    private RadioButton MosaicChip(string text, MosaicStyle style)
    {
        var chip = new RadioButton
        {
            Content = text,
            GroupName = "EdMosaic",
            Style = (Style)FindResource("EdChip"),
            IsChecked = style == MosaicStyle.Area,
        };

        chip.Checked += (_, _) =>
        {
            _mosaicStyle = style;
            _controller.MosaicStyle = style;
        };
        return chip;
    }

    private Button IconButton(Geometry icon, string tip, Action action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("EdIconButton"),
            ToolTip = tip,
            Content = new IconView { Icon = icon, IconSize = 17, IconBrush = Brush("EdTextSecondary") },
        };

        button.Click += (_, _) => action();
        return button;
    }

    private Button ActionButton(string text, CaptureAction action, bool isDefault)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(isDefault ? "EdAccentButton" : "EdButton"),
        };
        button.Click += (_, _) => Finish(action);
        return button;
    }

    private static UIElement Separator() => new Border
    {
        Width = 1,
        Height = 18,
        Margin = new Thickness(7, 0, 7, 0),
        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBox CreateTextInput()
    {
        var box = new TextBox
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AcceptsReturn = true,
            MinWidth = 80,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x3B, 0x9E, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            CaretBrush = Brushes.White,
        };

        InputMethod.SetIsInputMethodEnabled(box, true);

        box.LostFocus += (_, _) => CommitText();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                box.Text = string.Empty;
                HideTextInput();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitText();
                e.Handled = true;
            }
        };

        return box;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ---------------- 缩放与落位 ----------------

    private void Place()
    {
        var monitors = MonitorEnumerator.Enumerate();
        var anchor = new PixelPoint(_origin.X + _origin.Width / 2, _origin.Y + _origin.Height / 2);
        var monitor = MonitorEnumerator.FromPoint(monitors, anchor)
                      ?? monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(_origin))
                      ?? monitors.FirstOrDefault();

        var work = monitor?.WorkArea ?? new PixelRect(0, 0, 1280, 800);
        int w = Math.Clamp((int)(work.Width * 0.86), 760, 1700);
        int h = Math.Clamp((int)(work.Height * 0.86), 560, 1000);
        int x = work.X + (work.Width - w) / 2;
        int y = work.Y + (work.Height - h) / 2;

        ScrollChrome.PlacePixels(this, new PixelRect(x, y, w, h), activate: true);
        Opacity = 1;
    }

    /// <summary>
    /// 画布缩放：宽度方向铺满可视区，但不放大到 100% 以上。
    ///
    /// 「100%」按物理像素算（1 图片像素 = 1 屏幕像素），而不是 DIP ——
    /// 截图工具里说 100% 就是指原图大小，125% 缩放的屏上按 DIP 铺满会显得「怎么变大了」。
    /// </summary>
    private void ApplyZoom()
    {
        if (_zoomApplied) return;
        _zoomApplied = true;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1;

        double available = Math.Max(240, _scroll.ActualWidth - 20);
        double fit = available / Math.Max(1, _image.PixelWidth);
        _zoom = Math.Min(1 / dpi, fit);

        _surface.Width = _image.PixelWidth * _zoom;
        _surface.Height = _image.PixelHeight * _zoom;

        _layer.Selection = new PixelRect(0, 0, _image.PixelWidth, _image.PixelHeight);
        _layer.MonitorOrigin = new PixelPoint(0, 0);
        _layer.ScaleX = _layer.ScaleY = 1 / _zoom;
        _layer.InvalidateVisual();

        int percent = (int)Math.Round(_zoom * dpi * 100);
        _dims.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} × {1}　·　{2}%", _image.PixelWidth, _image.PixelHeight, percent);
    }

    // ---------------- 工具与参数 ----------------

    private void SetTool(ToolKind kind)
    {
        _tool = kind;
        _controller.Tool = kind;

        if (kind != ToolKind.None) Deselect();

        _surface.Cursor = kind switch
        {
            ToolKind.None => Cursors.Arrow,
            ToolKind.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };

        UpdateSizeUi();
    }

    private void StepSize(int direction)
    {
        var option = ToolOptions.SizeOf(_tool);
        var range = ToolOptions.RangeOf(option);

        switch (option)
        {
            case SizeOption.FontSize:
                _fontSize = range.Nudge(_fontSize, direction);
                break;

            case SizeOption.MosaicBlock:
                _mosaicBlock = range.Nudge(_mosaicBlock, direction);
                break;

            default:
                _thickness = range.Nudge(_thickness, direction);
                break;
        }

        ApplyOptions();
        UpdateSizeUi();
        ApplyStyleToSelection();
    }

    private void ApplyOptions()
    {
        _controller.Stroke = _color;
        _controller.Thickness = _thickness;
        _controller.FontSize = _fontSize;
        _controller.MosaicBlock = (int)Math.Round(_mosaicBlock);
        _controller.MosaicStyle = _mosaicStyle;
        _controller.MosaicBrushWidth = _mosaicBlock * ToolOptions.MosaicBrushWidthRatio;
    }

    /// <summary>
    /// 改颜色或大小的时候，如果正选中着一个标注，就顺手把它也改掉 ——
    /// 眼睛看着选中框、手在调色板上点一下，期待的就是「把它改成这个颜色」。
    /// 只改自己用得上的字段由各 Annotation.WithStyle 负责（矩形不会因此长出字号）。
    /// </summary>
    private void ApplyStyleToSelection()
    {
        if (_selected < 0 || _selected >= _document.Items.Count) return;

        var before = _document.Items[_selected];
        var style = new AnnotationStyle(
            _color, _thickness, _fontSize,
            (int)Math.Round(_mosaicBlock),
            _mosaicBlock * ToolOptions.MosaicBrushWidthRatio);

        var next = before.WithStyle(style);
        _document.ReplaceLive(_selected, next);
        _document.CommitEdit(_selected, before);
    }

    private void UpdateSizeUi()
    {        var option = ToolOptions.SizeOf(_tool);
        bool enabled = _tool != ToolKind.None;
        _sizeGroup.IsEnabled = enabled;

        double value = option switch
        {
            SizeOption.FontSize => _fontSize,
            SizeOption.MosaicBlock => _mosaicBlock,
            _ => _thickness,
        };

        _sizeLabel.Text = enabled
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1:0}", ToolOptions.LabelOf(option), value)
            : "—";

        _mosaicChips.Visibility = _tool == ToolKind.Mosaic ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 画布输入 ----------------

    private Point ToImage(Point dip) => new(dip.X / _zoom, dip.Y / _zoom);

    private double Tolerance => HandleSize / _zoom;

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Focus();

        var p = ToImage(e.GetPosition(_surface));

        // 点到别处先提交文字，否则那句还没按回车的标注会跟着下一次操作一起丢掉
        CommitText();

        if (_tool == ToolKind.Text)
        {
            BeginText(p);
            e.Handled = true;
            return;
        }

        if (_tool == ToolKind.None)
        {
            BeginSelect(p);
            e.Handled = true;
            return;
        }

        _controller.Begin(p);
        _drawing = true;
        _surface.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 选择工具下按一下：先认选中标注的控制点（拉伸），再认标注本身（移动），
    /// 最后才在空白处清掉选择。顺序必须和画出来的东西一致 ——
    /// 先认标注的话，控制点永远抓不着，用户会觉得「角上那个小方块是画着看的」。
    /// </summary>
    private void BeginSelect(Point p)
    {
        if (_selected >= 0 && _selected < _document.Items.Count)
        {
            var item = _document.Items[_selected];

            int handle = HandlesUsable(item)
                ? Handles.HitTest(item.Frame, p, Tolerance)
                : -1;

            if (handle >= 0)
            {
                _resizing = true;
                _dragHandle = handle;
                _dragBefore = item;
                _surface.CaptureMouse();
                return;
            }

            if (item.HitTest(p, Tolerance))
            {
                _moving = true;
                _dragStart = p;
                _dragBefore = item;
                _surface.CaptureMouse();
                return;
            }
        }

        int hit = _document.HitTest(p, Tolerance);
        if (hit >= 0)
        {
            _selected = hit;
            _layer.Selected = _document.Items[hit];
            _layer.InvalidateVisual();
            SyncEditButtons();

            _moving = true;
            _dragStart = p;
            _dragBefore = _document.Items[hit];
            _surface.CaptureMouse();
            return;
        }

        Deselect();
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var p = ToImage(e.GetPosition(_surface));

        if (_drawing)
        {
            _controller.Update(p);
            _layer.Preview = _controller.Preview;
            _layer.InvalidateVisual();
            return;
        }

        if (_resizing && _dragBefore is not null)
        {
            var next = _dragBefore.DragHandle(_dragHandle, p);
            _document.ReplaceLive(_selected, next);
            return;
        }

        if (_moving && _dragBefore is not null)
        {
            var next = _dragBefore.Translate(p.X - _dragStart.X, p.Y - _dragStart.Y);
            _document.ReplaceLive(_selected, next);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var p = ToImage(e.GetPosition(_surface));

        if (_drawing)
        {
            _drawing = false;
            _layer.Preview = null;
            if (_controller.End(p)) SelectNewest();
            _layer.InvalidateVisual();
        }
        else if (_moving || _resizing)
        {
            if (_dragBefore is not null)
                _document.CommitEdit(_selected, _dragBefore);

            _moving = false;
            _resizing = false;
            _dragHandle = -1;
            _dragBefore = null;
        }

        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        SyncEditButtons();
    }

    private void CancelDrag()
    {
        if (_drawing)
        {
            _drawing = false;
            _controller.Cancel();
            _layer.Preview = null;
        }

        _moving = false;
        _resizing = false;
        _dragHandle = -1;
        _dragBefore = null;

        _layer.InvalidateVisual();
    }

    private bool HandlesUsable(Annotation item)
        => item.HandleCount != Handles.Count || Handles.FitIn(item.Frame, Tolerance);

    private void OnDocumentChanged()
    {
        _layer.Selected = _selected >= 0 && _selected < _document.Items.Count
            ? _document.Items[_selected]
            : null;

        _layer.InvalidateVisual();
        SyncEditButtons();
    }

    private void SelectNewest()
    {
        if (_document.Items.Count == 0) return;
        _selected = _document.Items.Count - 1;
        _layer.Selected = _document.Items[_selected];
        _layer.InvalidateVisual();
    }

    private void Deselect()
    {
        _selected = -1;
        _layer.Selected = null;
        _layer.InvalidateVisual();
        SyncEditButtons();
    }

    private void SyncEditButtons()
    {
        _undoButton.IsEnabled = _document.CanUndo;
        _redoButton.IsEnabled = _document.CanRedo;
        _deleteButton.IsEnabled = _selected >= 0 && _selected < _document.Items.Count;
    }

    // ---------------- 文字 ----------------

    private void BeginText(Point p)
    {
        _textOrigin = p;
        _text.Text = string.Empty;
        _text.FontSize = Math.Max(9, _fontSize * _zoom);
        _text.Foreground = new SolidColorBrush(_color);
        _text.Margin = new Thickness(p.X * _zoom, p.Y * _zoom, 0, 0);
        _text.Visibility = Visibility.Visible;
        _text.Focus();
    }

    private void CommitText()
    {
        if (_text.Visibility != Visibility.Visible) return;

        string content = _text.Text;
        HideTextInput();

        if (_controller.CommitText(_textOrigin, content)) SelectNewest();
    }

    private void HideTextInput()
    {
        _text.Visibility = Visibility.Collapsed;
        _text.Text = string.Empty;
        Focus();
    }

    // ---------------- 命令 ----------------

    private void Undo()
    {
        CommitText();
        if (!_document.Undo()) return;
        _selected = -1;
        _layer.Selected = null;
        _layer.InvalidateVisual();
        SyncEditButtons();
    }

    private void Redo()
    {
        CommitText();
        if (!_document.Redo()) return;
        _selected = -1;
        _layer.Selected = null;
        _layer.InvalidateVisual();
        SyncEditButtons();
    }

    private void DeleteSelected()
    {
        if (_selected < 0) return;

        CommitText();
        if (!_document.RemoveAt(_selected)) return;

        _selected = -1;
        _layer.Selected = null;
        _layer.InvalidateVisual();
        SyncEditButtons();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 文字框自己处理 Enter / Esc，别在它上面抢
        if (_text.IsKeyboardFocusWithin)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            if (_selected >= 0) Deselect();
            else Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            Finish(CaptureAction.Save);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C)
        {
            Finish(CaptureAction.Copy);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Finish(EffectiveDefault);
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    // ---------------- 出图 ----------------

    private IAnnotationContext MosaicContext => _mosaicContext ??= new LazyMosaic(() =>
    {
        _mosaic ??= BuildMosaicSource();
        return _mosaic;
    });

    private MosaicSource? BuildMosaicSource()
    {
        try
        {
            BitmapSource source = _image;
            if (source.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
            }

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * 4;
            var buffer = new byte[checked(stride * height)];
            source.CopyPixels(buffer, stride, 0);
            return new MosaicSource(buffer, width, height, stride);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把标注烧进整张长图。没有标注就直接交原图 ——
    /// 一张两万像素高的图重新渲染一遍要几百毫秒，而这时候它和原图一个字节都不差。
    /// </summary>
    private BitmapSource? Render()
    {
        if (_document.IsEmpty) return _image;

        int width = _image.PixelWidth;
        int height = _image.PixelHeight;

        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_image, new Rect(0, 0, width, height));
                _document.Draw(dc, MosaicContext);
            }

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }
        catch (Exception ex)
        {
            // 用编辑器自己当 owner：这扇窗是 Topmost，把提示框交给外面弹，
            // 它会落在编辑器底下 —— 用户看到的是「点了复制，什么都没发生」
            Dialog.Notify(this, dark: true, "长图导出失败：" + ex.Message, DialogKind.Warning);
            return null;
        }
    }

    private void Finish(CaptureAction action)
    {
        if (_closing) return;

        CommitText();

        var result = Render();
        if (result is null) return;

        _closing = true;
        Finished?.Invoke(result, action);

        // 控制器可能为了让保存对话框/贴图浮到最上面，已经在事件里把窗口关掉了
        try { Close(); }
        catch (InvalidOperationException) { /* 已经关了 */ }
    }

    private void Cancel()
    {
        if (_closing) return;
        _closing = true;
        Cancelled?.Invoke();

        try { Close(); }
        catch (InvalidOperationException) { /* 已经关了 */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 / 系统关闭：跟点「取消」是同一种意思
        if (!_closing)
        {
            _closing = true;
            Cancelled?.Invoke();
        }

        base.OnClosing(e);
    }

    /// <summary>图标的画布。Icons 里的几何是描边图形，不能直接塞进 Path。</summary>
    private sealed class IconView : FrameworkElement
    {
        public Geometry? Icon { get; init; }
        public Brush IconBrush { get; init; } = Brushes.White;
        public double IconSize { get; init; } = 18;

        public IconView()
        {
            IsHitTestVisible = false;
        }

        protected override Size MeasureOverride(Size availableSize)
            => new(IconSize, IconSize);

        protected override void OnRender(DrawingContext dc)
        {
            if (Icon is null) return;
            Icons.Draw(dc, Icon, new Rect(0, 0, ActualWidth, ActualHeight), IconBrush, IconSize);
        }
    }

    /// <summary>马赛克的原图像素到第一次真的要用时才准备，没有马赛克标注就不花这份内存。</summary>
    private sealed class LazyMosaic : IAnnotationContext
    {
        private readonly Func<MosaicSource?> _factory;
        private MosaicSource? _source;

        public LazyMosaic(Func<MosaicSource?> factory) => _factory = factory;

        public void DrawMosaic(DrawingContext dc, Rect area, int block)
        {
            _source ??= _factory();
            _source?.DrawMosaic(dc, area, block);
        }
    }
}
