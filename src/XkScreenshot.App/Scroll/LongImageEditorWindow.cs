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

    /// <summary>
    /// 放大上限，按物理像素算的百分比：200% = 一个图片像素占两个屏幕像素。
    ///
    /// 再往上就只剩一层马赛克了（截图本身没有更多细节），而且画布面积翻倍涨，
    /// 长图上会明显拖慢。缩小那头不设固定值，以「适应宽度」为下限 ——
    /// 那已经能把整张图的宽度塞进视口，再小只是白白浪费地方。
    /// </summary>
    private const int MaxZoomPercent = 200;

    /// <summary>
    /// 缩放档位，单位同上。都是常见百分比，跳跃感小；<see cref="StepZoom"/> 只在这些档位间走，
    /// 当前值不在档位上（例如刚打开时的「适应宽度」可能是 62%）时，往上取下一个、往下取上一个。
    /// </summary>
    private static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 100, 125, 150, 200];

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
    private readonly TextBlock _zoomText = new();
    private readonly Button _zoomOut;
    private readonly Button _zoomIn;
    private readonly TextBlock _sizeLabel = new();
    private readonly StackPanel _sizeGroup = new();
    private readonly StackPanel _mosaicChips = new();
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _deleteButton;

    /// <summary>置顶按钮里的那枚图标：切到「没钉住」时要换成划掉的图钉。</summary>
    private IconView? _pinIcon;

    private double _zoom = 1;

    /// <summary>本窗口所在显示器的缩放。DIP ↔ 物理像素，也是「100%」这个口径的基准。</summary>
    private double _dpi = 1;

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

        // 这一版加了最小化按钮，就必须在任务栏里占个位置：没有任务栏按钮的窗口最小化后
        // 会缩成屏幕边上一条光秃秃的残条，还跟着置顶浮在最前（同 TextResultWindow 的注释）
        ShowInTaskbar = true;
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
        _zoomOut = IconButton(Icons.Minus, "缩小", () => StepZoom(-1), iconSize: 13);
        _zoomIn = IconButton(Icons.Plus, "放大", () => StepZoom(1), iconSize: 13);
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
        // 先给个值，免得第一次布局到 ApplyZoom 之间那一瞬间是空的
        _dims.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} × {1}", _image.PixelWidth, _image.PixelHeight);

        var close = IconButton(Icons.Close, "取消  Esc", Cancel);
        DockPanel.SetDock(close, Dock.Right);

        var pin = PinButton();
        DockPanel.SetDock(pin, Dock.Right);

        var minimize = IconButton(Icons.Minimize, "最小化", () => WindowState = WindowState.Minimized);
        DockPanel.SetDock(minimize, Dock.Right);

        var header = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(14, 0, 10, 0),
            // 从右往左钉：关闭贴最右，然后是置顶、最小化
            Children = { close, pin, minimize, title, _dims },
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

        // 底图是整张长图（动辄上千 × 上万像素），而它和标注层是两层东西：
        // 标注一改就重画标注层，底图本该原样复用。但高质量重采样（双三次）在每一帧
        // 合成时都要重新算，长图上一帧几十毫秒 —— 拖标注时手会明显发滞。
        //
        // 平时用双线性：都在缩小显示，双三次那点细腻度肉眼看不出来，速度差好几倍。
        // 拖动期间再降一档（见 SetInteracting），松手后恢复。
        RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.Linear);

        _layer.Document = _document;
        _layer.Context = MosaicContext;
        _layer.IsHitTestVisible = false;

        _surface.Background = Brushes.Transparent;
        // 缩到比视口窄时（最小 25%）水平居中：留白分在两边，看着是一张画布，
        // 而不是一张贴在左边的小图。放大超出视口时这个对齐不起作用，照常从左起滚
        _surface.HorizontalAlignment = HorizontalAlignment.Center;
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

        // 横向用 Auto 而不是 Disabled：放大到超过视口宽度时，Disabled 会把内容按视口宽裁掉 ——
        // 右侧那半张图既看不到也滚不过去。Auto 只在真的放不下时才亮出一条滚动条
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.PanningMode = PanningMode.VerticalOnly;
        _scroll.Focusable = false;

        // 缩放后画布宽度会小于视口，此时居中（见 _surface 的对齐）；纵向一律靠上
        _scroll.HorizontalContentAlignment = HorizontalAlignment.Center;
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
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 缩放组：（− 100% +）放在三个去向左边。
        //
        // 它管的是「现在看到的是哪一块」，和复制/保存/贴图一样是动作，放同一条栏里顺手；
        // 标题栏那边则只留「这是什么图、多大」这类静态信息，不再混进可点的东西。
        // 百分比本身也是按钮，点了回到「适应宽度」—— 放大到 200% 之后，
        // 手里只有一个数字，没有这枚按钮就得一路点减号才回得来。
        _zoomOut.Width = 24;
        _zoomOut.Height = 24;
        _zoomOut.Margin = new Thickness(0);

        _zoomIn.Width = 24;
        _zoomIn.Height = 24;

        var zoomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Children = { _zoomOut, ZoomResetButton(), _zoomIn },
        };

        buttons.Children.Add(zoomRow);
        buttons.Children.Add(ActionButton("复制", CaptureAction.Copy, isDefault: EffectiveDefault == CaptureAction.Copy));
        buttons.Children.Add(ActionButton("保存", CaptureAction.Save, isDefault: EffectiveDefault == CaptureAction.Save));
        buttons.Children.Add(ActionButton("贴图", CaptureAction.Pin, isDefault: EffectiveDefault == CaptureAction.Pin));

        // 底栏只留三个去向。快捷键那行说明去掉了 —— Esc / Enter / Ctrl+Z 都是通用约定，
        // 常驻一行不但没人看，还会把视线从按钮上分走
        return new Border
        {
            BorderBrush = Brush("EdBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Border
            {
                Margin = new Thickness(14, 0, 14, 0),
                Child = buttons,
            },
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

    private Button IconButton(Geometry icon, string tip, Action action, double iconSize = 17)
    {
        var button = new Button
        {
            Style = (Style)FindResource("EdIconButton"),
            ToolTip = tip,
            Content = new IconView { Icon = icon, IconSize = iconSize, IconBrush = Brush("EdTextSecondary") },
        };

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// 标题栏上的百分比。点一下回到「适应宽度」—— 放大到 200% 之后，
    /// 用户手里只有一个数字，没有这枚按钮就得一路点 − 才回得来。
    /// </summary>
    private Button ZoomResetButton()
    {
        _zoomText.FontSize = 11.5;
        _zoomText.Foreground = Brush("EdTextSecondary");
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Text = "100%";

        var button = new Button
        {
            Style = (Style)FindResource("EdIconButton"),
            Width = 50,
            Height = 24,
            // 夹在 − 和 + 之间，左右各留两像素：三个元素挤在一起时数字会显得被夹住
            Margin = new Thickness(2, 0, 2, 0),
            ToolTip = "适应宽度",
            Content = _zoomText,
        };

        button.Click += (_, _) => ApplyZoomLevel(FitZoom);
        return button;
    }

    /// <summary>
    /// 置顶开关。窗口本来就是 Topmost，这个按钮给的是「临时放到底下」的能力 ——
    /// 比如要照着另一个窗口的内容改标注。
    ///
    /// 用普通按钮 + 换图标，而不是 ToggleButton：这里只有「钉住 / 没钉住」两种状态，
    /// 图标本身已经说清楚了（图钉 vs 划掉的图钉），再叠一层选中底色反而花。
    /// </summary>
    private Button PinButton()
    {
        _pinIcon = new IconView
        {
            Icon = Icons.Pin,
            IconSize = 17,
            IconBrush = Brush("EdTextSecondary"),
        };

        var button = new Button
        {
            Style = (Style)FindResource("EdIconButton"),
            ToolTip = "取消置顶",
            Content = _pinIcon,
        };

        button.Click += (_, _) => TogglePin(button);
        return button;
    }

    private void TogglePin(Button button)
    {
        Topmost = !Topmost;

        if (_pinIcon is not null)
            _pinIcon.Icon = Topmost ? Icons.Pin : Icons.PinOff;

        button.ToolTip = Topmost ? "取消置顶" : "置于顶层";
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

    /// <summary>「适应宽度」对应的倍率：宽度方向铺满可视区，但不放大到 100% 以上。</summary>
    private double FitZoom
    {
        get
        {
            double available = Math.Max(240, _scroll.ActualWidth - 20);
            return Math.Min(1 / Math.Max(_dpi, 1e-6), available / Math.Max(1, _image.PixelWidth));
        }
    }

    /// <summary>
    /// 当前百分比。**按物理像素算**（1 图片像素 = 1 屏幕像素），而不是 DIP ——
    /// 截图工具里说 100% 就是指原图大小，125% 缩放的屏上按 DIP 铺满会显得「怎么变大了」。
    /// </summary>
    private int ZoomPercent => (int)Math.Round(_zoom * _dpi * 100);

    /// <summary>「适应宽度」是多少百分比。</summary>
    private int FitPercent => (int)Math.Round(FitZoom * _dpi * 100);

    /// <summary>
    /// 缩小的下限。图比视口宽时，「适应宽度」本身就是最小；图不宽时（fit 已经到 100%）
    /// 仍然允许缩到 25%，否则长图只能看到一小段，缩不下去看全貌。
    /// </summary>
    private int MinZoomPercent => Math.Min(FitPercent, 25);

    private void ApplyZoom()
    {
        if (_zoomApplied) return;
        _zoomApplied = true;

        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_dpi <= 0) _dpi = 1;

        _layer.Selection = new PixelRect(0, 0, _image.PixelWidth, _image.PixelHeight);
        _layer.MonitorOrigin = new PixelPoint(0, 0);

        // 打开时先按「适应宽度」摆好；之后用户可以用标题栏那一组按钮在 fit ~ 200% 之间调
        ApplyZoomLevel(FitZoom, keepCenter: false);
    }

    /// <summary>
    /// 换一个缩放倍率。倍率是「画布 DIP / 图片像素」。
    ///
    /// <paramref name="keepCenter"/> 为真时，让视口中心指着的那块内容在缩放前后不动。
    /// 否则 ScrollViewer 的偏移是按像素记的，放大之后同一个偏移指向的是更靠左上的地方，
    /// 用户会觉得「画面跳走了」。代价是改完尺寸要先跑一次布局，才拿得到新的滚动范围。
    /// </summary>
    private void ApplyZoomLevel(double zoom, bool keepCenter = true)
    {
        // 上限 200%（按物理像素）。下限见 MinZoomPercent —— 再小整张图也不会更清楚，只是白占地方
        double min = MinZoomPercent / 100.0 / _dpi;
        zoom = Math.Clamp(zoom, min, Math.Max(min, MaxZoomPercent / 100.0 / _dpi));

        // NaN 比较恒为 false，首次必然继续往下走
        if (_surface.Width > 0 && Math.Abs(zoom - _zoom) < 1e-9)
        {
            UpdateZoomUi();
            return;
        }

        var center = keepCenter
            ? new Point(
                (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / _zoom,
                (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / _zoom)
            : new Point(0, 0);

        _zoom = zoom;

        _surface.Width = _image.PixelWidth * _zoom;
        _surface.Height = _image.PixelHeight * _zoom;

        _layer.ScaleX = _layer.ScaleY = 1 / _zoom;
        _layer.InvalidateVisual();

        // 正在输入的文字是按 DIP 绝对定位的，跟着一起重摆，否则缩放后会飘到别处
        if (_text.Visibility == Visibility.Visible)
        {
            _text.FontSize = Math.Max(9, _fontSize * _zoom);
            _text.Margin = new Thickness(_textOrigin.X * _zoom, _textOrigin.Y * _zoom, 0, 0);
        }

        UpdateZoomUi();

        // 倍数变了，底图该用哪种重采样也跟着变（见 RestingScaleMode）
        SetInteracting(false);

        if (!keepCenter) return;

        // 布局得先跑一遍：否则 ScrollToXxxOffset 写进去的值会被旧的滚动范围夹住
        _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(center.X * _zoom - _scroll.ViewportWidth / 2);
        _scroll.ScrollToVerticalOffset(center.Y * _zoom - _scroll.ViewportHeight / 2);

        // 再跑一次：ScrollViewer 的偏移要等下一次 arrange 才真正落到内容上，
        // 不补这一下，紧接着的第二次缩放读到的还是旧偏移，中心会一格一格地漂
        _scroll.UpdateLayout();
    }

    /// <summary>按档位放大/缩小一格。当前值不在档位上（如「适应宽度」的 62%）时取相邻那一档。</summary>
    private void StepZoom(int direction)
    {
        int current = ZoomPercent;
        int min = MinZoomPercent;

        int target = direction > 0
            ? ZoomSteps.Where(p => p > current).DefaultIfEmpty(MaxZoomPercent).First()
            : ZoomSteps.Where(p => p < current).DefaultIfEmpty(min).Last();

        target = Math.Clamp(target, min, MaxZoomPercent);
        if (target == current) return;

        // 档位是百分比，倍率换算回「画布 DIP / 图片像素」
        ApplyZoomLevel(target / 100.0 / _dpi);
    }

    private void UpdateZoomUi()
    {
        _dims.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} × {1}", _image.PixelWidth, _image.PixelHeight);

        int percent = ZoomPercent;
        _zoomText.Text = string.Format(CultureInfo.InvariantCulture, "{0}%", percent);

        // 到顶/到底就置灰：比「点了没反应」更清楚，也不会让人以为按钮坏了
        _zoomOut.IsEnabled = percent > MinZoomPercent;
        _zoomIn.IsEnabled = percent < MaxZoomPercent;
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

        // 从「工具栏上此刻显示的那个数」开始加，而不是从默认值开始：
        // 选中一个 5 号的框、工具栏也写着 5，点一下 + 该变成 6，不是从 15 跳到 16
        double next = range.Nudge(CurrentSize(option), direction);

        switch (option)
        {
            case SizeOption.FontSize:
                _fontSize = next;
                break;

            case SizeOption.MosaicBlock:
                _mosaicBlock = next;
                break;

            default:
                _thickness = next;
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
    {
        var option = ToolOptions.SizeOf(_tool);
        bool enabled = _tool != ToolKind.None;
        _sizeGroup.IsEnabled = enabled;

        _sizeLabel.Text = enabled
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1:0}", ToolOptions.LabelOf(option), CurrentSize(option))
            : "—";

        _mosaicChips.Visibility = _tool == ToolKind.Mosaic ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 工具栏此刻该显示的尺寸：**选中了标注就是它的，没选中才是「下次要用的默认值」**。
    ///
    /// 两者必须分开。早先这里直接读 `_thickness`，于是选中一个 5 号的框、工具栏还写着 15；
    /// 而如果反过来在选中时把 `_thickness` 改成 5，又会污染默认值 —— 取消选中后新画的
    /// 也跟着变细，用户设的 20 白设了。
    /// </summary>
    private double CurrentSize(SizeOption option)
    {
        var selected = SelectedStyle();

        return option switch
        {
            SizeOption.FontSize => selected?.FontSize ?? _fontSize,
            SizeOption.MosaicBlock => selected?.MosaicBlock ?? _mosaicBlock,
            _ => selected?.Thickness ?? _thickness,
        };
    }

    /// <summary>选中标注的样式；没选中返回 null。用 StyleOver 取，矩形不会长出字号。</summary>
    private AnnotationStyle? SelectedStyle()
    {
        if (_selected < 0 || _selected >= _document.Items.Count) return null;

        return _document.Items[_selected].StyleOver(
            new AnnotationStyle(_color, _thickness, _fontSize,
                (int)Math.Round(_mosaicBlock), _mosaicBlock * ToolOptions.MosaicBrushWidthRatio));
    }

    // ---------------- 画布输入 ----------------

    private Point ToImage(Point dip) => new(dip.X / _zoom, dip.Y / _zoom);

    private double Tolerance => HandleSize / _zoom;

    /// <summary>
    /// 正在拖 / 正在画的时候，把底图的重采样再降一档（最近邻，最便宜的一档）。
    ///
    /// 这段时间画面每帧都在变，跟手比那一点画质要紧；松手立刻恢复成双线性。
    /// 代价是缩小时会有点摩尔纹 —— 只在拖动那几百毫秒里出现，停手就没了。
    /// </summary>
    private void SetInteracting(bool interacting)
    {
        var mode = interacting ? BitmapScalingMode.NearestNeighbor : RestingScaleMode;
        if (RenderOptions.GetBitmapScalingMode(_imageView) == mode) return;

        RenderOptions.SetBitmapScalingMode(_imageView, mode);
    }

    /// <summary>
    /// 不交互时的重采样方式。
    ///
    /// 放大到 100% 以上改成最近邻：双线性会把一个像素摊成一片模糊，而放大看截图
    /// 正是为了看清那一个像素。缩小时仍用双线性 —— 那时候最近邻会整行整列丢像素，出摩尔纹。
    /// </summary>
    private BitmapScalingMode RestingScaleMode
        => ZoomPercent > 100 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear;

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Focus();

        var p = ToImage(e.GetPosition(_surface));

        // 点到别处先提交文字，否则那句还没按回车的标注会跟着下一次操作一起丢掉
        CommitText();

        // 接下来多半是拖或画：底图先降到低质量档，松手再恢复
        SetInteracting(true);

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
            UpdateSizeUi();

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
        SetInteracting(false);
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

        SetInteracting(false);
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

        // 取消选中之后，工具栏要退回「下次要用的默认值」——它刚才显示的是那个标注的尺寸
        UpdateSizeUi();
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
        SettleAfterHistory();
    }

    private void Redo()
    {
        CommitText();
        if (!_document.Redo()) return;
        SettleAfterHistory();
    }

    /// <summary>
    /// 撤销/重做之后收拾选中状态。工具栏不用另行同步 ——
    /// <see cref="UpdateSizeUi"/> 本来就优先显示选中项的尺寸，标注退回旧值它自然跟着退。
    ///
    /// 之前这里一律取消选中，于是「调粗细 → 撤销」看起来就像数字卡住了：
    /// 标注细回去了，工具栏还停在刚调大的数上（那个数是默认值，撤销并不会动它）。
    /// 现在只要那条标注还在就继续选中它，显示自然对得上。
    /// </summary>
    private void SettleAfterHistory()
    {
        if (_selected >= _document.Items.Count) _selected = -1;

        _layer.Selected = _selected >= 0 ? _document.Items[_selected] : null;
        _layer.InvalidateVisual();
        UpdateSizeUi();
        SyncEditButtons();
    }

    private void DeleteSelected()
    {
        if (_selected < 0) return;

        CommitText();
        if (!_document.RemoveAt(_selected)) return;

        Deselect();
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
        private Geometry? _icon;

        /// <summary>可改：置顶按钮要在「已置顶 / 未置顶」两枚图标之间来回换。</summary>
        public Geometry? Icon
        {
            get => _icon;
            set
            {
                _icon = value;
                InvalidateVisual();
            }
        }

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
