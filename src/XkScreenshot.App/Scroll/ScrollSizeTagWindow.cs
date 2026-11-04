using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长截图期间贴在选区左上角的那枚尺寸标签：`1149 × 800 px`，随拼出来的高度实时长。
///
/// 为什么从控制条里搬出来：尺寸是「这块区域有多大」，用户看它的时机和看选区本身一样 ——
/// 眼睛在选区左上角，不在屏幕另一头的控制条上。普通截图就是这么摆的
/// （<see cref="Overlay.SelectionLayer"/> 的 DrawSizeLabel），两处摆法一致，
/// 用户在同一个位置上找同一个数。
///
/// 样式也照抄那一份：深色圆角底 + 白字 12 号，不跟深浅主题走。它压在实时画面上，
/// 底下是什么颜色不可预知，深底白字是唯一在亮暗内容上都读得清的组合。
///
/// **尺寸是自己量、自己摆的，没有用 SizeToContent**：那套在 150% 缩放的屏上会把窗口
/// 算成内容的 1.7 倍大（实测 202×56 物理，文字只需要 119×31），多出来的高度让标签底边
/// 离选区老远，看起来就是「没贴住」。这里改成用 FormattedText 在 DIP 下量、乘 DPI 取整成
/// 物理像素，再用 SetWindowPos 一次摆到位 —— 位置和尺寸是同一个数算出来的，不会互相打架。
///
/// 它只是块玻璃：点击穿透、不抢焦点、排除在截屏之外 —— 翻进选区内部时也不会被拼进长图。
/// </summary>
internal sealed class ScrollSizeTagWindow : Window
{
    // 下面这组数值和 SelectionLayer.DrawSizeLabel 对齐：字号、内边距、与选区的间距
    private const double LabelFontSize = 12;
    private const double LabelPadding = 5;
    private const int GapPx = 4;

    private readonly PixelRect _region;
    private readonly Label _label;

    public ScrollSizeTagWindow(PixelRect region)
    {
        _region = region;

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

        // 不设 Width/Height：窗口尺寸完全交给 SetWindowPos，WPF 只要照着排内容就行。
        // 设了反而会被 WPF 强制回那个值，两边各说各的
        Content = _label = new Label
        {
            // 先用选区自己的尺寸顶上，第一帧拼出来之前就是这个值，不会先闪一个 0
            Text = Format(region.Width, region.Height),
        };

        SourceInitialized += (_, _) =>
        {
            ScrollChrome.MakeClickThrough(this);
            ScrollChrome.ExcludeFromCapture(this);
        };

        // 尺寸和位置都要等拿到真实 DPI 才算得准，在那之前先透明，免得先闪一个错位的小窗
        Opacity = 0;

        // 第一次能拿到真实 DPI 是在内容渲染之后（那时窗口已经归属到某台显示器上）
        ContentRendered += (_, _) =>
        {
            Reposition();
            Opacity = 1;
        };

        // 拖到另一块 DPI 不同的屏上时，标签要按新 DPI 重新量一遍
        DpiChanged += (_, _) => Reposition();
    }

    /// <summary>拼出来的尺寸变了。文字变宽就重新量、重新摆（见 <see cref="Reposition"/>）。</summary>
    public void Update(int width, int height)
    {
        string text = Format(width, height);

        // 每 60ms 会调一次，数字没变就别动 —— 重排一次意味着一次 SetWindowPos
        if (_label.Text == text) return;

        _label.Text = text;
        Reposition();
    }

    private static string Format(int width, int height)
        => string.Format(CultureInfo.InvariantCulture, "{0} × {1} px", width, height);

    /// <summary>
    /// 贴到选区左上角的外面；上面放不下（选区贴着屏幕顶）就翻进选区内部，
    /// 和普通截图那枚标签同一套规则 —— 宁可盖住一点内容，也不能跑到屏幕外看不见。
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        // 量的是 DIP，摆的是物理像素：两套单位只在最后这一步换算，中间不混用
        var (dipW, dipH) = _label.Measure(scale);
        int w = Math.Max(1, (int)Math.Ceiling(dipW * scale));
        int h = Math.Max(1, (int)Math.Ceiling(dipH * scale));

        var screen = ScreenBounds();

        int x = _region.X;
        int y = _region.Y - h - GapPx;
        if (y < screen.Y) y = _region.Y + GapPx;

        // 选区贴着屏幕右边时把标签往回贴，别让它探出去
        if (x + w > screen.Right) x = Math.Max(screen.X, screen.Right - w);

        ScrollChrome.PlacePixels(this, new PixelRect(x, y, w, h));
    }

    /// <summary>选区所在的那台显示器。跨屏选区按左上角算，标签本来就挂在这一角上。</summary>
    private PixelRect ScreenBounds()
    {
        var monitors = MonitorEnumerator.Enumerate();
        var anchor = new PixelPoint(_region.X + 1, _region.Y + 1);

        var monitor = MonitorEnumerator.FromPoint(monitors, anchor)
                      ?? monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(_region))
                      ?? monitors.FirstOrDefault();

        return monitor?.Bounds ?? _region;
    }

    /// <summary>
    /// 标签本体：一块深色圆角底 + 一行白字，尺寸由 <see cref="Measure"/> 说了算。
    ///
    /// 不用 Border + TextBlock：那两个的尺寸要经过 WPF 布局，而布局结果又受窗口尺寸影响，
    /// 窗口尺寸还得先知道内容多大 —— 绕回去就成了循环。这里直接画，量多少就是多少。
    /// </summary>
    private sealed class Label : FrameworkElement
    {
        private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0x1A, 0x1A, 0x1A)));
        private static readonly Typeface Face = new("Segoe UI");

        private FormattedText? _text;

        public string Text { get; set; } = string.Empty;

        /// <summary>按给定 DPI 量出标签的 DIP 尺寸。顺手把排版结果缓存给 OnRender 用。</summary>
        public (double W, double H) Measure(double pixelsPerDip)
        {
            _text = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Face, LabelFontSize, Brushes.White, pixelsPerDip);

            return (_text.Width + LabelPadding * 2, _text.Height + LabelPadding);
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 尺寸由 SetWindowPos 定，这里按实际大小铺满；文字从左上角按内边距排
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = _text ?? new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Face, LabelFontSize, Brushes.White, ppd);

            dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
            dc.DrawText(text, new Point(LabelPadding, LabelPadding / 2));
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
