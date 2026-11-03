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
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长截图的控制条：拼到多大了、谁在滚、拼完往哪儿去。
///
/// 形态是**一行**贴边的窄条，而不是一张占了小半屏的卡片 ——
/// 长截图期间用户要盯着的是**正在滚动的目标窗口**，任何压在旁边的厚面板都在抢视线。
/// 缩略图从这条里搬了出去（见 <see cref="ScrollPreviewWindow"/>）。
///
/// 排版从左到右就四样：尺寸 → 自动/手动 → 完成/编辑/取消。
/// 原来是两行（上面尺寸+模式、下面提示+按钮），第一行只有两个元素、中间空出一大片，
/// 看着像两块不相干的东西凑在一起；两行的高度也让这条压在画面上更碍事。
///
/// **不挂状态句子**（"正在慢速滚动…"、"这一帧接不上…"）：滚动期间它一直在变，
/// 挂在这条窄条上只是噪声。**也不挂快捷键提示**：那几个键本来就和右边三个按钮一一对应，
/// 同一件事说两遍，还会把按钮挤出卡片 —— 真忘了 Esc 取消也不致命。
///
/// 落位优先摆在目标区域**外面**（下、上、右、左依次试）。这不是审美问题：抓帧走的是
/// 屏幕 DC，压在区域上的东西会被原样拍进长图。区域占满整屏时确实无处可躲，
/// 那时只能靠 <see cref="ScrollChrome.ExcludeFromCapture"/>。
///
/// 三个去向：完成（走设置里的默认动作）、编辑（进长图编辑器）、取消。
/// 「完成」不写成「复制/保存/贴图」：按钮上写动作名，用户得先看懂那个动作是不是自己要的；
/// 写「完成」则一眼就是「我拼够了」，具体是复制还是保存，本来就是设置里定好的。
/// </summary>
internal sealed class ScrollPanelWindow : Window
{
    private const double PanelWidth = 412;

    /// <summary>控制条和目标区域之间的空隙，物理像素。</summary>
    private const int GapPx = 14;

    private readonly PixelRect _region;
    private readonly CaptureAction _defaultAction;

    private readonly RadioButton _autoChip;
    private readonly RadioButton _manualChip;
    private readonly TextBlock _metrics = new();

    /// <summary>正在按引擎的状态回填模式开关，这期间的 Checked 不该再发回引擎。</summary>
    private bool _syncing;

    private bool _placed;

    /// <summary>
    /// 摆位时要避开的矩形（选区右侧的预览窗）。空 = 不用避。
    /// 预览总是先摆，它占住右边之后，控制条就不该再往同一块地方挤。
    /// </summary>
    public PixelRect Avoid { get; set; }

    /// <summary>用户在面板上换了滚动方式。</summary>
    public event Action<ScrollMode>? ModeChanged;

    /// <summary>用户要收工，参数是成品的去向。</summary>
    public event Action<CaptureAction>? Accepted;

    /// <summary>用户要拿这张长图继续编辑，而不是直接出图。</summary>
    public event Action? EditRequested;

    /// <summary>用户放弃这次长截图。</summary>
    public event Action? Cancelled;

    public ScrollPanelWindow(PixelRect region, ScrollMode mode, CaptureAction defaultAction, bool dark)
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

        // 必须在建控件之前注入：主题里的颜色全是 DynamicResource，控件一建就从这儿找
        ApplyPalette(OverlayPalette.For(dark));

        _autoChip = ModeChip("自动", ScrollMode.Auto);
        _manualChip = ModeChip("手动", ScrollMode.Manual);
        Content = BuildLayout();
        SetMode(mode);

        SourceInitialized += (_, _) => ScrollChrome.ExcludeFromCapture(this);
        ContentRendered += (_, _) => Place();
    }

    /// <summary>
    /// 把覆盖层那套配色搬进来。
    ///
    /// 面板和工具条会同时出现在屏幕上（选区框、工具条、这条控制条），差一档色就看得出来
    /// 是两个程序。所以颜色一律取自 <see cref="OverlayPalette"/> —— 深浅主题也跟着它走，
    /// 早先这里写死过一套深色，浅色主题下就它一个黑的。
    ///
    /// 底色用 <see cref="OverlayPalette.PanelOpaque"/> 而不是工具条那层半透明：
    /// 工具条底下是冻住的截图，能先模糊再压色；这条面板压在**实时**画面上，没有可模糊的底，
    /// 半透明叠上去遇到深色内容字就看不见了。
    /// </summary>
    private void ApplyPalette(OverlayPalette palette)
    {
        Resources["PanelBg"] = palette.PanelOpaque;
        Resources["PanelBorder"] = palette.PanelBorder.Brush;
        Resources["PanelTopHighlight"] = palette.PanelTopHighlight.Brush;
        Resources["PanelText"] = palette.Text;
        Resources["PanelTextSecondary"] = palette.TextSecondary;
        Resources["PanelTextMuted"] = palette.TextMuted;
        Resources["PanelAccent"] = palette.Accent;
        Resources["PanelAccentText"] = palette.IconActive;
        Resources["PanelAccentFill"] = palette.AccentFill;
        Resources["PanelAccentBorder"] = palette.AccentBorder.Brush;
        Resources["PanelControlHover"] = palette.Hover;
        Resources["PanelControlPressed"] = palette.ChipFill;

        // 「完成」悬停时底色要再亮一档，调色板里没有现成的那一档，从强调色算出来
        if (palette.Accent is SolidColorBrush accent)
        {
            var hover = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
            hover.Freeze();
            Resources["PanelAccentHover"] = hover;
        }
        else
        {
            Resources["PanelAccentHover"] = palette.AccentFill;
        }
    }

    // ---------------- 界面 ----------------

    /// <summary>
    /// 一行装完：`1325 × 5354` ｜ 自动 手动 ｜ 完成 编辑 取消。
    ///
    /// 用 DockPanel 从右往左钉：按钮最先钉（贴最右），模式胶囊钉在它左边，
    /// 尺寸吃剩下的空间。这样右半边永远不会被挤走 —— 尺寸再长也只是自己被省略号截短，
    /// 而答案按钮（完成/取消）不能挪位、不能缩，否则用户点空一次就够烦的。
    /// </summary>
    private UIElement BuildLayout()
    {
        _metrics.FontSize = 12.5;
        _metrics.Foreground = Brush("PanelText");
        _metrics.TextTrimming = TextTrimming.CharacterEllipsis;
        _metrics.VerticalAlignment = VerticalAlignment.Center;
        // 右边留出间距，尺寸和胶囊之间才有一条明确的空气缝，而不是两个东西挨着
        _metrics.Margin = new Thickness(2, 0, 16, 0);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _autoChip, _manualChip },
        };
        DockPanel.SetDock(chips, Dock.Right);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        actions.Children.Add(ActionButton("完成", isDefault: true, () => Accepted?.Invoke(_defaultAction)));
        actions.Children.Add(ActionButton("编辑", isDefault: false, () => EditRequested?.Invoke()));
        actions.Children.Add(ActionButton("取消", isDefault: false, () => Cancelled?.Invoke()));
        DockPanel.SetDock(actions, Dock.Right);

        // 顺序就是钉的顺序：actions 先在，贴最右；chips 后钉，落在它左边；尺寸填剩下的
        var row = new DockPanel { LastChildFill = true, Children = { actions, chips, _metrics } };

        // 沿顶边的一道内高光：工具条的玻璃感基本全靠它，这条面板跟着一起走，
        // 两块面板摆在同一片画面上才像一套东西（见 PanelChrome.DrawGlassPanel）
        var highlight = new System.Windows.Shapes.Rectangle
        {
            Height = 1,
            Margin = new Thickness(11, 1, 11, 0),
            VerticalAlignment = VerticalAlignment.Top,
            RadiusX = 0.5,
            RadiusY = 0.5,
            Fill = Brush("PanelTopHighlight"),
            IsHitTestVisible = false,
        };

        // 单行不需要上下两层留白：高度由 30 高（按钮）决定，左右只留够呼吸的量
        var body = new Grid
        {
            Margin = new Thickness(14, 9, 10, 9),
            Children = { row, highlight },
        };

        var card = new Border
        {
            Background = Brush("PanelBg"),
            BorderBrush = Brush("PanelBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = body,
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
        };
        return chip;
    }

    private Button ActionButton(string text, bool isDefault, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(isDefault ? "PanelAccentButton" : "PanelButton"),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ---------------- 状态 ----------------

    public void UpdateProgress(ScrollProgress progress)
    {
        SetMode(progress.Mode);

        _metrics.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} × {1}", progress.Width, progress.Height);
    }

    private void SetMode(ScrollMode mode)
    {
        var chip = mode == ScrollMode.Auto ? _autoChip : _manualChip;
        if (chip.IsChecked == true) return;

        // 引擎那边会自己让位到手动（用户按了鼠标键），回填这个状态不能再当成
        // 用户点了开关，否则两边来回发通知
        _syncing = true;
        chip.IsChecked = true;
        _syncing = false;
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
            if (!Avoid.IsEmpty && candidate.IntersectsWith(Avoid)) continue;

            ScrollChrome.PlacePixels(this, candidate, activate: true);
            Opacity = 1;
            return;
        }

        // 实在无处可去：先试右下，预览在那边就让到左下
        var fallback = new PixelRect(work.Right - w - GapPx, work.Bottom - h - GapPx, w, h);
        if (!Avoid.IsEmpty && fallback.IntersectsWith(Avoid))
            fallback = new PixelRect(work.X + GapPx, work.Bottom - h - GapPx, w, h);

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

    /// <summary>
    /// 用 Preview 而不是 OnKeyDown：焦点可能停在自动/手动胶囊或某个按钮上，
    /// 那样 Enter 会被控件先吃掉（切成另一枚胶囊），「回车完成」就时灵时不灵。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

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
}
