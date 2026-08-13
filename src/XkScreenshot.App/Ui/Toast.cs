using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Ui;

/// <summary>回执的语气。图标跟着变，别的都一样。</summary>
public enum ToastKind
{
    /// <summary>办成了。</summary>
    Done,

    /// <summary>只是知会一声，没有什么被做成。</summary>
    Info,
}

/// <summary>
/// 办成之后那句一闪而过的回执。
///
/// 从前走的是托盘气泡，但那条路上的每一环都不归本程序管：气泡会被「专注助手」按下、
/// 会径直排进通知中心攒成一列没人看的旧消息、样式与停留时长全由系统定 ——
/// 而「已复制」这种话，晚三秒说出来就等于没说。
///
/// 所以自己画一个：贴着刚截的那块区域弹出来。用户的视线本来就落在那儿，
/// 不必再扫到屏幕角上去找一句两秒后就消失的话。没有选区可贴的场合
/// （剪贴板里没东西可贴之类）退回光标所在那块屏的右下角。
/// </summary>
public static class Toast
{
    /// <summary>
    /// 同一时刻只留一条。回执之间是覆盖关系而不是叠加关系 ——
    /// 连截两张时，用户要知道的是「这一张复制好了」，上一张那句已经没有意义。
    /// </summary>
    private static ToastWindow? _current;

    /// <param name="anchor">贴着哪块区域弹。留空则退到屏幕角落。</param>
    /// <param name="detail">跟在正文后面的次要信息，用浅一档的颜色，如尺寸、文件名。</param>
    /// <param name="tip">悬停时的完整说明。正文里放不下的东西（如完整路径）搁这儿。</param>
    /// <param name="click">给了就说明这条回执可以点，同时它也不再鼠标穿透。</param>
    public static void Show(
        bool dark,
        string text,
        string? detail = null,
        PixelRect anchor = default,
        ToastKind kind = ToastKind.Done,
        string? tip = null,
        Action? click = null)
    {
        DismissAll();

        var toast = new ToastWindow(dark, text, detail, anchor, kind, tip, click);
        _current = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, toast)) _current = null;
        };
        toast.Show();
    }

    /// <summary>
    /// 当场收掉。开始截图前要调一次：覆盖层也是置顶的，两个置顶窗口压在一起
    /// 谁在上面并无定论，而回执被压在覆盖层下面只会露出半截。
    /// </summary>
    public static void DismissAll()
    {
        var toast = _current;
        _current = null;
        toast?.Close();
    }
}

/// <summary>
/// 回执浮窗本体。
///
/// 无边框、不进任务栏、不进 Alt+Tab、不抢焦点：它是浮在别人界面上的一句话，
/// 用户手上正在做的事不该因为它断一下。不可点的那些还额外设了鼠标穿透 ——
/// 它盖在别人的界面上，挡住点击就等于把那块界面废掉了。
/// </summary>
internal sealed class ToastWindow : Window
{
    /// <summary>给投影留的空当（DIP）。窗口比卡片大一圈，这一圈是透明的。</summary>
    private const double ShadowMargin = 16;

    /// <summary>卡片和选区之间的空隙，物理像素。</summary>
    private const int GapPx = 12;

    /// <summary>退到屏幕角落时离工作区边缘多远，物理像素。</summary>
    private const int EdgePx = 24;

    /// <summary>正文排到这儿就折行。回执是一句话，不是一段话。</summary>
    private const double MaxTextWidth = 420;

    /// <summary>淡入时同时往上抬这么多 DIP，让它像是「浮」上来的。</summary>
    private const double RiseDip = 8;

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);

    /// <summary>停留多久。够读完一句话，短过一次犹豫。</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1900);

    /// <summary>鼠标移开之后再留一会儿，省得手一抖就没了。</summary>
    private static readonly TimeSpan HoldAfterHover = TimeSpan.FromMilliseconds(800);

    private readonly PixelRect _anchor;
    private readonly Action? _click;
    private readonly TranslateTransform _rise = new();
    private readonly DispatcherTimer _timer = new();

    /// <summary>已经在淡出了。这期间鼠标移上来还能把它救回来，见 <see cref="Rescue"/>。</summary>
    private bool _leaving;

    /// <summary>
    /// 这一轮的悬停不算数。
    ///
    /// 卡片常常正好弹在光标底下 —— 刚拖完选区，手就停在选区边上，而回执就贴着那儿弹。
    /// 那一下 MouseEnter 是卡片自己撞上来的，不是用户把鼠标移过来的；当成悬停处理的话，
    /// 只要人不动鼠标，这条回执就再也不会消失。所以要等鼠标真正离开过一次才认。
    /// </summary>
    private bool _hoverBlocked;

    public ToastWindow(
        bool dark, string text, string? detail, PixelRect anchor, ToastKind kind,
        string? tip, Action? click)
    {
        _anchor = anchor;
        _click = click;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // 摆到位置之前先透明：SizeToContent 要等内容量完才知道尺寸，
        // 尺寸不知道就算不出位置 —— 中间那一帧会让卡片在屏幕上闪一下
        Opacity = 0;

        Theme.Apply(this, dark);
        Content = BuildCard(dark, text, detail, kind, tip);

        SourceInitialized += (_, _) => ApplyWindowTraits();
        ContentRendered += (_, _) =>
        {
            Place();
            Enter();
        };

        // 卡片被摆到另一台缩放不同的显示器上时，WPF 会按新 DPI 重新排一遍版，
        // 窗口尺寸随之变化 —— 位置得跟着重算，否则会偏出去
        DpiChanged += (_, _) => Place();

        _timer.Tick += (_, _) => Leave();
        Closed += (_, _) => _timer.Stop();
    }

    // ---------------- 界面 ----------------

    private UIElement BuildCard(bool dark, string text, string? detail, ToastKind kind, string? tip)
    {
        var line = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxTextWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        line.Inlines.Add(new Run(text));

        if (!string.IsNullOrWhiteSpace(detail))
        {
            // 全角空格当间隔：正文和次要信息之间要有一口气，但不值得为它拆成两行
            var run = new Run("　" + detail);
            run.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
            line.Inlines.Add(run);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph(kind == ToastKind.Done ? Icons.Check : Icons.Info, "Accent",
            new Thickness(0, 0, 10, 0)));
        row.Children.Add(line);

        // 可点的那些右边挂一枚文件夹图标。光有手型光标是不够的 ——
        // 鼠标不路过就永远不知道这儿能点
        if (_click is not null)
            row.Children.Add(Glyph(Icons.Folder, "TextSecondary", new Thickness(12, 0, 0, 0)));

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 9, 16, 9),
            Margin = new Thickness(ShadowMargin),
            Child = row,
            RenderTransform = _rise,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                // 卡片浮在任何东西上面，跟背景之间没有别的分界；深色下那圈边框
                // 本来就快看不见了，全靠投影把它从底下的画面里托出来
                Opacity = dark ? 0.5 : 0.22,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        if (!string.IsNullOrWhiteSpace(tip)) card.ToolTip = tip;

        if (_click is not null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) =>
            {
                _click();
                Leave();
            };
        }

        // 鼠标停在上面就别走。可点的那条尤其需要：还没够着就自己消失，
        // 那颗按钮等于不存在
        MouseEnter += (_, _) =>
        {
            if (!_hoverBlocked) Rescue();
        };
        MouseLeave += (_, _) =>
        {
            _hoverBlocked = false;
            if (!_leaving) Rest(HoldAfterHover);
        };

        return card;
    }

    private static FrameworkElement Glyph(Geometry icon, string brushKey, Thickness margin)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = icon,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);

        // 按 24×24 的设计尺寸画再缩，线宽跟着一起缩，视觉重量才和别处的图标一致
        return new Viewbox
        {
            Child = path,
            Width = 17,
            Height = 17,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---------------- 窗口性状 ----------------

    private void ApplyWindowTraits()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long extra = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;

        // 不可点的那些彻底不吃鼠标。它盖在别人的界面上，凭什么挡住那一下点击
        if (_click is null) extra |= NativeMethods.WS_EX_TRANSPARENT;

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style.ToInt64() | extra));
    }

    // ---------------- 落位 ----------------

    /// <summary>
    /// 按物理像素摆窗口。理由同覆盖层：WPF 的 Left/Top 是 DIP，
    /// 窗口还没归属某台显示器时换算基准是错的，多屏下必然摆偏。
    /// </summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        var monitors = MonitorEnumerator.Enumerate();
        var monitor = HostMonitor(monitors);
        var work = monitor?.WorkArea ?? MonitorEnumerator.VirtualBounds(monitors);
        double scale = monitor?.ScaleY ?? 1;

        // 窗口比卡片大一圈（那圈透明的是投影的地盘）。落位算的是卡片，
        // 拿去摆窗口时再把这一圈补回去 —— 不然「离选区 12 像素」会凭空多出一截
        int inset = (int)Math.Round(ShadowMargin * scale);
        int cardW = Math.Max(1, rect.Width - inset * 2);
        int cardH = Math.Max(1, rect.Height - inset * 2);

        var card = _anchor.IsEmpty
            ? new PixelPoint(work.Right - EdgePx - cardW, work.Bottom - EdgePx - cardH)
            : AnchoredCard(cardW, cardH, work);

        int x = Clamp(card.X, work.X + GapPx, work.Right - GapPx - cardW);
        int y = Clamp(card.Y, work.Y + GapPx, work.Bottom - GapPx - cardH);

        _hoverBlocked = new PixelRect(x, y, cardW, cardH)
            .Contains(MonitorEnumerator.GetCursorPosition());

        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            x - inset, y - inset, rect.Width, rect.Height,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 贴着选区落位：先试正下方，塞不下就翻到正上方。
    ///
    /// 上下都塞不下（选区高得快占满整屏）时贴进选区里侧的下沿 —— 那时候压住的是
    /// 一块刚截完、已经不用再看的画面，比把回执挤到屏幕边上强。
    /// </summary>
    private PixelPoint AnchoredCard(int w, int h, PixelRect work)
    {
        int x = _anchor.X + (_anchor.Width - w) / 2;

        int below = _anchor.Bottom + GapPx;
        if (below + h <= work.Bottom - GapPx) return new PixelPoint(x, below);

        int above = _anchor.Y - GapPx - h;
        if (above >= work.Y + GapPx) return new PixelPoint(x, above);

        return new PixelPoint(x, _anchor.Bottom - GapPx - h);
    }

    private MonitorInfo? HostMonitor(IReadOnlyList<MonitorInfo> monitors)
    {
        var probe = _anchor.IsEmpty
            ? MonitorEnumerator.GetCursorPosition()
            : new PixelPoint(_anchor.X + _anchor.Width / 2, _anchor.Y + _anchor.Height / 2);

        return MonitorEnumerator.FromPoint(monitors, probe)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    /// <summary>屏幕比卡片还窄的时候 min 会大过 max，那种情形下贴着左上角就是了。</summary>
    private static int Clamp(int value, int min, int max)
        => max <= min ? min : Math.Clamp(value, min, max);

    // ---------------- 进退 ----------------

    private void Enter()
    {
        Animate(OpacityProperty, this, 1, FadeIn, EasingMode.EaseOut);
        Animate(TranslateTransform.YProperty, _rise, 0, FadeIn, EasingMode.EaseOut, from: RiseDip);
        Rest(Hold);
    }

    private void Rest(TimeSpan span)
    {
        _timer.Stop();
        _timer.Interval = span;
        _timer.Start();
    }

    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        _timer.Stop();

        Animate(TranslateTransform.YProperty, _rise, -RiseDip / 2, FadeOut, EasingMode.EaseIn);

        var fade = Animation(0, FadeOut, EasingMode.EaseIn);
        fade.Completed += (_, _) =>
        {
            // 淡出途中被鼠标救回来了，这一枪就不该再响
            if (_leaving) Close();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>把已经开始淡出的那条拉回来 —— 鼠标都追上去了，说明用户还想要它。</summary>
    private void Rescue()
    {
        _leaving = false;
        _timer.Stop();

        Animate(OpacityProperty, this, 1, FadeIn, EasingMode.EaseOut);
        Animate(TranslateTransform.YProperty, _rise, 0, FadeIn, EasingMode.EaseOut);
    }

    private static void Animate(
        DependencyProperty property, IAnimatable target, double to, TimeSpan span,
        EasingMode easing, double? from = null)
    {
        var animation = Animation(to, span, easing);
        if (from is not null) animation.From = from;
        target.BeginAnimation(property, animation);
    }

    private static DoubleAnimation Animation(double to, TimeSpan span, EasingMode easing) => new(to, span)
    {
        EasingFunction = new CubicEase { EasingMode = easing },
    };
}
