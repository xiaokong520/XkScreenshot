using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长截图的控制面板：拼到哪儿了、谁在滚、拼完往哪儿去。
///
/// 落位优先摆在目标区域**外面**（下、上、右、左依次试）。这不是审美问题：抓帧走的是
/// 屏幕 DC，压在区域上的东西会被原样拍进长图。区域占满整屏时确实无处可躲，
/// 那时只能靠 <see cref="ScrollChrome.ExcludeFromCapture"/>。
///
/// 出图去向给了三个按钮而不是一个「完成」：长截图动辄几千像素高，
/// 各人要的去向差别很大 —— 存档的想保存、发群里的想复制、对照着写东西的想贴出来。
/// 回车仍然走设置里那个默认去向，对应的按钮会高亮，省得用户去猜回车干什么。
/// </summary>
internal sealed class ScrollPanelWindow : Window
{
    private const double PanelWidth = 440;
    private const double PreviewWidth = 104;
    private const double PreviewHeight = 150;

    /// <summary>面板和目标区域之间的空隙，物理像素。</summary>
    private const int GapPx = 14;

    private readonly PixelRect _region;
    private readonly CaptureAction _defaultAction;

    private readonly RadioButton _autoChip;
    private readonly RadioButton _manualChip;
    private readonly TextBlock _state = new();
    private readonly TextBlock _metrics = new();
    private readonly TextBlock _hint = new();
    private readonly PreviewBox _preview = new();

    /// <summary>正在按引擎的状态回填模式开关，这期间的 Checked 不该再发回引擎。</summary>
    private bool _syncing;

    private bool _placed;

    /// <summary>用户在面板上换了滚动方式。</summary>
    public event Action<ScrollMode>? ModeChanged;

    /// <summary>用户要收工，参数是成品的去向。</summary>
    public event Action<CaptureAction>? Accepted;

    /// <summary>用户放弃这次长截图。</summary>
    public event Action? Cancelled;

    public ScrollPanelWindow(PixelRect region, ScrollMode mode, CaptureAction defaultAction)
    {
        _region = region;
        _defaultAction = defaultAction;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // 摆到正确位置之前先透明：SizeToContent 要等内容量完才知道尺寸，
        // 而尺寸不知道就算不出位置 —— 中间那一帧会让面板在屏幕上闪一下
        Opacity = 0;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XkScreenshot;component/Scroll/ScrollPanelTheme.xaml"),
        });

        _autoChip = ModeChip("自动", ScrollMode.Auto);
        _manualChip = ModeChip("手动", ScrollMode.Manual);
        Content = BuildLayout();
        SetMode(mode);
        UpdateHint();

        SourceInitialized += (_, _) => ScrollChrome.ExcludeFromCapture(this);
        ContentRendered += (_, _) => Place();
    }

    // ---------------- 界面 ----------------

    private UIElement BuildLayout()
    {
        var title = new TextBlock
        {
            Text = "长截图",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("PanelText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _autoChip, _manualChip },
        };
        DockPanel.SetDock(chips, Dock.Right);

        var header = new DockPanel { LastChildFill = true, Children = { chips, title } };

        _preview.Width = PreviewWidth;
        _preview.Height = PreviewHeight;

        _state.Foreground = Brush("PanelText");
        _state.TextTrimming = TextTrimming.CharacterEllipsis;

        _metrics.Foreground = Brush("PanelTextSecondary");
        _metrics.FontSize = 12;
        _metrics.Margin = new Thickness(0, 7, 0, 0);
        _metrics.TextTrimming = TextTrimming.CharacterEllipsis;

        _hint.Foreground = Brush("PanelTextMuted");
        _hint.FontSize = 11.5;
        _hint.Margin = new Thickness(0, 12, 0, 0);
        _hint.TextWrapping = TextWrapping.Wrap;

        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        info.Children.Add(_state);
        info.Children.Add(_metrics);
        info.Children.Add(_hint);
        Grid.SetColumn(info, 1);

        var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_preview);
        body.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children =
            {
                ActionButton("复制", CaptureAction.Copy),
                ActionButton("保存", CaptureAction.Save),
                ActionButton("贴图", CaptureAction.Pin),
                Spacer(),
                CancelButton(),
            },
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        stack.Children.Add(actions);

        var card = new Border
        {
            Background = Brush("PanelBg"),
            BorderBrush = Brush("PanelBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = stack,
            // 面板浮在实时画面上，没有投影就像是被贴进了别人的界面里
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.42, Color = Colors.Black },
        };

        // 投影要有地方落，四周留白就是给它的
        return new Grid { Margin = new Thickness(14), Children = { card } };
    }

    private RadioButton ModeChip(string text, ScrollMode mode)
    {
        var chip = new RadioButton
        {
            Content = text,
            GroupName = "ScrollMode",
            Style = (Style)FindResource("ModeChip"),
        };
        chip.Checked += (_, _) =>
        {
            if (_syncing) return;
            ModeChanged?.Invoke(mode);
            UpdateHint();
        };
        return chip;
    }

    private Button ActionButton(string text, CaptureAction action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(action == _defaultAction ? "PanelAccentButton" : "PanelButton"),
        };
        button.Click += (_, _) => Accepted?.Invoke(action);
        return button;
    }

    private Button CancelButton()
    {
        var button = new Button { Content = "取消", Style = (Style)FindResource("PanelButton") };
        button.Click += (_, _) => Cancelled?.Invoke();
        return button;
    }

    private static UIElement Spacer() => new Border { Width = 10 };

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ---------------- 状态 ----------------

    public void UpdateProgress(ScrollProgress progress)
    {
        SetMode(progress.Mode);

        _state.Text = Describe(progress.State, progress.Mode);
        _metrics.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} × {1}　·　已拼 {2} 帧", progress.Width, progress.Height, progress.Frames);

        _preview.Image = progress.Preview;
        _preview.SourceWidth = progress.Width;
        _preview.SourceRows = progress.PreviewSourceRows;
        _preview.InvalidateVisual();
    }

    private static string Describe(ScrollState state, ScrollMode mode) => state switch
    {
        ScrollState.Scrolling => "正在自动滚动…",
        ScrollState.Settling => "画面还在动，等它停稳",
        ScrollState.Lost => "这一帧接不上，往回滚一点再试",
        ScrollState.Waiting => "轮到你滚了，滚到哪儿就拼到哪儿",
        _ => mode == ScrollMode.Auto ? "正在自动滚动…" : "轮到你滚了，滚到哪儿就拼到哪儿",
    };

    private void SetMode(ScrollMode mode)
    {
        var chip = mode == ScrollMode.Auto ? _autoChip : _manualChip;
        if (chip.IsChecked == true) return;

        // 引擎那边会自己让位到手动（用户一动鼠标就交还控制权），
        // 回填这个状态不能再当成用户点了开关，否则两边来回发通知
        _syncing = true;
        chip.IsChecked = true;
        _syncing = false;
        UpdateHint();
    }

    private void UpdateHint()
    {
        string action = _defaultAction switch
        {
            CaptureAction.Pin => "贴图",
            CaptureAction.Save => "保存",
            _ => "复制",
        };

        string extra = _autoChip.IsChecked == true
            ? "　·　动一下鼠标即可自己接手"
            : string.Empty;

        _hint.Text = $"Enter {action}　·　Esc 取消{extra}";
    }

    // ---------------- 落位 ----------------

    /// <summary>
    /// 摆到目标区域外面。依次试下、上、右、左，都塞不下才退到工作区右下角 ——
    /// 那时候面板会压在区域上，只能指望「排除截屏」兜着。
    /// </summary>
    private void Place()
    {
        if (_placed) return;
        _placed = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        int w = rect.Width;
        int h = rect.Height;
        var work = WorkArea();

        foreach (var candidate in Candidates(w, h, work))
        {
            if (candidate.X < work.X || candidate.Y < work.Y) continue;
            if (candidate.Right > work.Right || candidate.Bottom > work.Bottom) continue;
            if (candidate.IntersectsWith(_region)) continue;

            ScrollChrome.PlacePixels(this, candidate, activate: true);
            Opacity = 1;
            return;
        }

        var fallback = new PixelRect(work.Right - w - GapPx, work.Bottom - h - GapPx, w, h);
        ScrollChrome.PlacePixels(this, fallback, activate: true);
        Opacity = 1;
    }

    private IEnumerable<PixelRect> Candidates(int w, int h, PixelRect work)
    {
        int centerX = Math.Clamp(
            _region.X + (_region.Width - w) / 2, work.X, Math.Max(work.X, work.Right - w));
        int centerY = Math.Clamp(
            _region.Y + (_region.Height - h) / 2, work.Y, Math.Max(work.Y, work.Bottom - h));

        yield return new PixelRect(centerX, _region.Bottom + GapPx, w, h);
        yield return new PixelRect(centerX, _region.Y - GapPx - h, w, h);
        yield return new PixelRect(_region.Right + GapPx, centerY, w, h);
        yield return new PixelRect(_region.X - GapPx - w, centerY, w, h);
    }

    private PixelRect WorkArea()
    {
        var monitors = MonitorEnumerator.Enumerate();
        var anchor = new PixelPoint(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        var monitor = MonitorEnumerator.FromPoint(monitors, anchor)
                      ?? monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(_region))
                      ?? monitors.FirstOrDefault();
        return monitor?.WorkArea ?? _region;
    }

    // ---------------- 输入 ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Enter:
                Accepted?.Invoke(_defaultAction);
                break;

            case Key.Escape:
                Cancelled?.Invoke();
                break;

            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 面板可以拖走。区域外面塞不下的时候它会压在画面上，
    /// 而用户往往正需要看清底下那一块才好决定滚到哪儿。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); }
        catch (InvalidOperationException) { /* 按键已经松开了，没什么可拖的 */ }
    }

    /// <summary>
    /// 长图的缩略图。
    ///
    /// 缩略图纵向是抽过行的（见 PreviewStrip），像素高度和真实高度没有关系，
    /// 所以按**真实**宽高比去铺，而不是按位图自己的宽高比 —— 否则拼得越长，
    /// 画出来的比例越离谱。
    /// </summary>
    private sealed class PreviewBox : FrameworkElement
    {
        private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)));
        private static readonly Pen Border = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), 1));

        public BitmapSource? Image { get; set; }
        public int SourceWidth { get; set; }
        public int SourceRows { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            var box = new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
            dc.DrawRoundedRectangle(Back, Border, box, 5, 5);

            if (Image is null || SourceWidth <= 0 || SourceRows <= 0) return;

            var inner = new Rect(box.X + 3, box.Y + 3, Math.Max(0, box.Width - 6), Math.Max(0, box.Height - 6));
            if (inner.Width <= 0 || inner.Height <= 0) return;

            double scale = Math.Min(inner.Width / SourceWidth, inner.Height / SourceRows);
            double w = Math.Max(1, SourceWidth * scale);
            double h = Math.Max(1, SourceRows * scale);
            var dest = new Rect(inner.X + (inner.Width - w) / 2, inner.Y + (inner.Height - h) / 2, w, h);

            dc.DrawImage(Image, dest);
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
