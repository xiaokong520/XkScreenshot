using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长截图的控制条窗口：只负责「在哪儿」和「收哪几个键」，长相全交给 <see cref="ScrollPanelView"/>。
///
/// 为什么拆成两半：窗口这一层要处理的是物理像素落位、排除截屏、键盘；面板那层要处理的是
/// 圆角、按钮、图标、投影。混在一起时，为了迁就 WPF 的布局，面板的每个尺寸都只能写成
/// 「大概是这个数」；拆开之后面板按工具条那套自绘，窗口这边只管给它一块正好大的地方。
///
/// **不挂状态句子、不显示尺寸、不写快捷键**：尺寸在选区左上角（<see cref="ScrollSizeTagWindow"/>），
/// 状态句子在滚动期间一直在变、挂在这条窄条上只是噪声，快捷键又和右边三个按钮一一对应。
/// </summary>
internal sealed class ScrollPanelWindow : Window
{
    /// <summary>
    /// 面板边缘和目标区域之间的空隙，DIP —— 与覆盖层工具条的 <c>GapToSelection</c> 取同一个值。
    ///
    /// 注意这是**面板**到选区的距离，不是窗口到选区的距离：窗口四周还各留了
    /// <see cref="ScrollPanelView.ShadowPad"/> 给投影。早先按窗口算，于是屏幕上看到的面板
    /// 比工具条远出整整一个 ShadowPad，两个面板的间距一眼就不一样。
    /// </summary>
    private const double GapToSelection = 12;

    private readonly PixelRect _region;
    private readonly CaptureAction _defaultAction;
    private readonly ScrollPanelView _view;
    private readonly ScrollTipWindow _tip;

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
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;

        // 尺寸是常数（五个等宽按钮算出来的），直接给定，不依赖布局结果 ——
        // 落位要用它算坐标，等布局算出来会多一帧错位
        var size = ScrollPanelView.OuterSize;
        Width = size.Width;
        Height = size.Height;

        // 摆到正确位置之前先透明：否则面板会先在窗口默认位置闪一下
        Opacity = 0;

        _view = new ScrollPanelView(OverlayPalette.For(dark));
        _view.ShowMode(mode);
        _view.ModeChanged += m => ModeChanged?.Invoke(m);
        _view.Accepted += () => Accepted?.Invoke(_defaultAction);
        _view.EditRequested += () => EditRequested?.Invoke();
        _view.Cancelled += () => Cancelled?.Invoke();
        Content = _view;

        // 悬停说明用独立的小窗：气泡超出这条窄条的范围，塞不进面板那块地方
        _tip = new ScrollTipWindow(OverlayPalette.For(dark));
        _view.TipChanged += OnTipChanged;

        // 面板关掉的时候气泡也得跟着走，否则会留一枚孤零零的气泡在屏幕上
        Closed += (_, _) => _tip.CloseTip();

        SourceInitialized += (_, _) => ScrollChrome.ExcludeFromCapture(this);
        ContentRendered += (_, _) => Place();
    }

    /// <summary>
    /// 面板报的是局部 DIP 矩形，气泡要的是屏幕物理像素 —— 这一步换算放在窗口这层做，
    /// 因为只有这里同时知道窗口在屏幕上的位置和当前 DPI。
    /// </summary>
    private void OnTipChanged(Rect local, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _tip.HideTip();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        _tip.ShowTip(text, new PixelRect(
            rect.Left + (int)Math.Round(local.X * scale),
            rect.Top + (int)Math.Round(local.Y * scale),
            (int)Math.Round(local.Width * scale),
            (int)Math.Round(local.Height * scale)));
    }

    /// <summary>
    /// 只跟状态走。尺寸不在这儿显示了 —— 它搬到了选区左上角那枚标签上
    /// （<see cref="ScrollSizeTagWindow"/>），和普通截图同一个位置。
    /// </summary>
    public void UpdateProgress(ScrollProgress progress) => _view.ShowMode(progress.Mode);

    // ---------------- 落位 ----------------

    /// <summary>
    /// 摆到目标区域外面。依次试下、上、右、左，都塞不下才退到工作区右下角 ——
    /// 那时候面板会压在区域上，只能指望「排除截屏」兜着。
    ///
    /// 全程按**面板**矩形算（不含四周的投影留白），算出面板在哪儿之后再外扩 ShadowPad
    /// 得到窗口矩形。这样「面板离选区多远」是一个能直接对照工具条的量。
    /// </summary>
    private void Place()
    {
        if (_placed) return;
        _placed = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        int pad = (int)Math.Round(ScrollPanelView.ShadowPad * scale);
        int gap = (int)Math.Round(GapToSelection * scale);

        // 窗口带着一圈投影留白，别让这圈压到选区上：压在选区上的那几行会截走滚轮消息，
        // 手动模式下用户在选区底部滚不动就是这么来的
        gap = Math.Max(gap, pad);

        int panelW = Math.Max(1, rect.Right - rect.Left - pad * 2);
        int panelH = Math.Max(1, rect.Bottom - rect.Top - pad * 2);

        var work = WorkArea();

        foreach (var panel in Candidates(panelW, panelH, work, gap))
        {
            if (panel.X < work.X || panel.Y < work.Y) continue;
            if (panel.Right > work.Right || panel.Bottom > work.Bottom) continue;
            if (panel.IntersectsWith(_region)) continue;
            if (!Avoid.IsEmpty && panel.IntersectsWith(Avoid)) continue;

            ShowAt(panel, pad);
            return;
        }

        // 实在无处可去：先试右下，预览在那边就让到左下
        var fallback = new PixelRect(work.Right - panelW - gap, work.Bottom - panelH - gap, panelW, panelH);
        if (!Avoid.IsEmpty && fallback.IntersectsWith(Avoid))
            fallback = new PixelRect(work.X + gap, work.Bottom - panelH - gap, panelW, panelH);

        ShowAt(fallback, pad);
    }

    /// <summary>按面板矩形摆窗口：窗口 = 面板外扩一圈投影留白。</summary>
    private void ShowAt(PixelRect panel, int pad)
    {
        ScrollChrome.PlacePixels(this, new PixelRect(
            panel.X - pad, panel.Y - pad,
            panel.Width + pad * 2, panel.Height + pad * 2), activate: true);

        Opacity = 1;
    }

    private IEnumerable<PixelRect> Candidates(int w, int h, PixelRect work, int gap)
    {
        int centerX = Math.Clamp(
            _region.X + (_region.Width - w) / 2, work.X, Math.Max(work.X, work.Right - w));
        int centerY = Math.Clamp(
            _region.Y + (_region.Height - h) / 2, work.Y, Math.Max(work.Y, work.Bottom - h));

        yield return new PixelRect(centerX, _region.Bottom + gap, w, h);
        yield return new PixelRect(centerX, _region.Y - gap - h, w, h);
        yield return new PixelRect(_region.Right + gap, centerY, w, h);
        yield return new PixelRect(_region.X - gap - w, centerY, w, h);
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
    /// 用 Preview 而不是 OnKeyDown：焦点可能停在面板上，
    /// 那样 Enter 会被控件先吃掉，「回车完成」就时灵时不灵。
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
}
