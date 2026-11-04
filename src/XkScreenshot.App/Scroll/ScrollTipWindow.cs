using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 控制条按钮的悬停说明气泡：一个独立的小窗口，画法和覆盖层工具条的提示一模一样
/// （<see cref="ToolbarLayer"/> 的 DrawTip）—— 同一套字号、内边距、圆角、描边和投影。
///
/// 为什么要单开一扇窗：控制条窗口只有「面板 + 给投影留的 12px」，气泡放不进去；
/// 把窗口加高又会多挡住下面一条透明的可点区域。气泡窗本身点击穿透、无焦点、
/// 排除截屏，多这一扇不改变任何交互。
///
/// 尺寸同样是自己量、自己摆（同 <see cref="ScrollSizeTagWindow"/>）：`SizeToContent`
/// 在 150% 缩放下会把窗口算成内容的 1.7 倍，气泡会飘到离按钮老远的地方。
/// </summary>
internal sealed class ScrollTipWindow : Window
{
    private const double TipFontSize = 11.5;
    private const double PadX = 8;
    private const double PadY = 4.5;
    private const double CornerRadius = 6;

    /// <summary>气泡与按钮之间的空隙，和工具条一致。</summary>
    private const int GapPx = 7;

    /// <summary>四周给投影留的空白，与 <see cref="PanelChrome.DrawShadow"/> 的最大扩张一致。</summary>
    private const double ShadowPad = 12;

    private readonly Bubble _bubble;

    private bool _shown;

    public ScrollTipWindow(OverlayPalette palette)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.Manual;
        Opacity = 0;

        Content = _bubble = new Bubble(palette);

        SourceInitialized += (_, _) =>
        {
            ScrollChrome.MakeClickThrough(this);
            ScrollChrome.ExcludeFromCapture(this);
        };
    }

    /// <summary>
    /// 在按钮下方显示一句说明。放不下（按钮贴着屏幕底）就翻到按钮上方。
    /// </summary>
    /// <param name="text">说明文字。</param>
    /// <param name="anchor">按钮在屏幕上的物理像素矩形。</param>
    public void ShowTip(string text, PixelRect anchor)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var (dipW, dipH) = _bubble.Measure(text, scale);

        // 窗口比气泡大一圈：四周的 ShadowPad 是给投影的
        int w = (int)Math.Ceiling((dipW + ShadowPad * 2) * scale);
        int h = (int)Math.Ceiling((dipH + ShadowPad * 2) * scale);

        int x = anchor.X + (anchor.Width - w) / 2;
        int y = anchor.Bottom + GapPx - (int)Math.Round(ShadowPad * scale);

        // 掉出屏幕底就翻到按钮上方
        var screen = ScreenBounds(anchor);
        if (y + h > screen.Bottom) y = anchor.Y - GapPx - h + (int)Math.Round(ShadowPad * scale);

        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - w));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - h));

        // 必须先显示再摆位：窗口没 Show 过就没有 HWND，SetWindowPos 会直接落空，
        // 于是第一枚气泡会先在窗口默认位置闪一下
        if (!_shown)
        {
            Show();
            _shown = true;
        }

        ScrollChrome.PlacePixels(this, new PixelRect(x, y, w, h));
        Opacity = 1;
    }

    public void HideTip()
    {
        if (!_shown) return;
        _shown = false;
        Hide();
    }

    /// <summary>收工：连同这扇窗一起关掉。从没显示过的窗口没有 HWND，不必也不能关。</summary>
    public void CloseTip()
    {
        if (!_shown) return;
        _shown = false;
        Close();
    }

    private static PixelRect ScreenBounds(PixelRect anchor)
    {
        var monitors = MonitorEnumerator.Enumerate();
        var point = new PixelPoint(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2);

        var monitor = MonitorEnumerator.FromPoint(monitors, point)
                      ?? (monitors.Count > 0 ? monitors[0] : null);

        return monitor?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
    }

    /// <summary>
    /// 气泡本体：投一层影、铺一块圆角底、描一圈边、写一行字。
    /// 参数照抄工具条的 DrawTip，两个提示摆在同一片画面上才看不出是两处写的。
    /// </summary>
    private sealed class Bubble : FrameworkElement
    {
        private readonly OverlayPalette _palette;
        private FormattedText? _text;

        public Bubble(OverlayPalette palette) => _palette = palette;

        /// <summary>按给定 DPI 量出气泡的 DIP 尺寸，顺手把排版结果留给 OnRender。</summary>
        public (double W, double H) Measure(string text, double pixelsPerDip)
        {
            var typeface = new Typeface("Microsoft YaHei UI");
            _text = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, TipFontSize, _palette.TipText, pixelsPerDip);

            // 必须自己失效：从「自动滚动」滑到「手动滚动」时，两句话一样宽，
            // 窗口尺寸没变 —— WPF 就不会重排、也就不会重画，气泡会停在上一句上。
            // 用户看到的就是「两个按钮都写着手动滚动」。
            InvalidateVisual();

            return (_text.Width + PadX * 2, _text.Height + PadY * 2);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var text = _text;
            if (text is null) return;

            // 气泡扣掉四周的投影留白
            var box = new Rect(
                ShadowPad, ShadowPad,
                Math.Max(0, ActualWidth - ShadowPad * 2),
                Math.Max(0, ActualHeight - ShadowPad * 2));

            PanelChrome.DrawShadow(dc, box, CornerRadius, _palette);

            // 描一圈边：浅色主题下气泡近乎全白，压在同样浅的画面上光靠投影分不出边界
            dc.DrawRoundedRectangle(_palette.TipFill, _palette.PanelBorder, box, CornerRadius, CornerRadius);
            dc.DrawText(text, new Point(box.X + PadX, box.Y + PadY));
        }
    }
}
