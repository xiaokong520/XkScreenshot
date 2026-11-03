using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 选区右侧那条「已经拼到哪儿了」的实时预览。
///
/// 这块预览是长截图里最要紧的一眼：滚到哪儿、接得对不对、还要不要继续，全靠它。
///
/// **窗口里只有那张图**：没有标题、没有尺寸、没有底板描边、没有渐隐 —— 用户明确要求
/// 只留图本身。按宽度铺满，超出高度时贴底显示最新内容（长图只会往下长，
/// 关心的永远是刚拼上去的那一段）。
///
/// 它只是块玻璃：点击穿透、不抢焦点、并且排除在截屏之外。
/// </summary>
internal sealed class ScrollPreviewWindow : Window
{
    /// <summary>预览窗宽度，物理像素。</summary>
    private const int WidthPx = 178;

    /// <summary>和选区之间的空隙，物理像素。</summary>
    private const int GapPx = 14;

    private readonly PixelRect _region;
    private readonly PreviewPane _pane = new();

    private bool _placed;

    /// <summary>摆好之后的物理矩形。控制条拿它避让，免得两扇窗叠在一起。</summary>
    public PixelRect Bounds { get; private set; }

    public ScrollPreviewWindow(PixelRect region, bool dark)
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
        Content = _pane;

        // 边框跟着主题走：深色主题是亮一点的蓝，浅色主题压深一档，和覆盖层的强调色同一个值
        _pane.FrameBrush = OverlayPalette.For(dark).Accent;

        // 落位之前先藏起来：SourceInitialized 里才会算出真正的物理尺寸，
        // 那之前窗口还挂在默认位置，直接显示会在屏幕角上闪一下
        Opacity = 0;

        SourceInitialized += (_, _) =>
        {
            ScrollChrome.MakeClickThrough(this);
            ScrollChrome.ExcludeFromCapture(this);
            Place();
            Opacity = 1;
        };
    }

    public void UpdatePreview(ScrollProgress progress)
    {
        _pane.Image = progress.Preview;
        _pane.SourceWidth = progress.Width;
        _pane.SourceRows = progress.PreviewSourceRows;
        _pane.InvalidateVisual();
    }

    // ---------------- 落位 ----------------

    /// <summary>
    /// 贴在选区右侧；右边塞不下就翻到左边；两边都不行才压在选区里（那一块会被排除截屏）。
    /// 竖向跟选区居中对齐，再整体夹进工作区 —— 选区贴着屏幕顶边时，
    /// 居中对齐会算到屏幕外面去。
    /// </summary>
    private void Place()
    {
        if (_placed) return;
        _placed = true;

        var monitors = MonitorEnumerator.Enumerate();
        var anchor = new PixelPoint(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        var monitor = MonitorEnumerator.FromPoint(monitors, anchor)
                      ?? monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(_region))
                      ?? monitors.FirstOrDefault();
        if (monitor is null) return;

        var work = monitor.WorkArea;

        // 高度跟着选区走：选区高，预览就高；但再高也不占满整块屏
        int height = Math.Clamp(_region.Height, 300, Math.Max(300, (int)(work.Height * 0.72)));

        int x = _region.Right + GapPx;
        if (x + WidthPx > work.Right) x = _region.X - GapPx - WidthPx;

        // 两边都塞不下：压进选区右下角，至少不跑到屏幕外
        if (x < work.X) x = Math.Min(work.Right - WidthPx, _region.Right - WidthPx - 10);

        int y = _region.Y + (_region.Height - height) / 2;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - WidthPx));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));

        // 窗口的 Width/Height 是 DIP，真正生效的是 PlacePixels 里的物理像素；
        // 这两个值只用来让内容在首次布局时有个合理的尺寸
        Width = WidthPx / monitor.ScaleX;
        Height = height / monitor.ScaleY;

        Bounds = new PixelRect(x, y, WidthPx, height);
        ScrollChrome.PlacePixels(this, Bounds);
    }

    // ---------------- 画 ----------------

    /// <summary>
    /// 只画那张长图，别的一概没有 —— 没有标题、没有尺寸、没有底板和描边。
    ///
    /// 用户的要求是「只需要留图」。所以这里是它唯一该做的事：
    /// 按宽度铺满，超出高度时贴底（露最新的那一段），其余留白处直接透明。
    /// 外面再围一圈细蓝边 —— 和选区那圈框同色，预览只剩图之后，边界得靠它自己说明。
    /// </summary>
    private sealed class PreviewPane : FrameworkElement
    {
        /// <summary>
        /// 和 <see cref="ScrollFrameWindow"/> 那圈选区框同一个蓝（跟着主题，见窗口构造）。
        /// </summary>
        public Brush FrameBrush { get; set; } = Brushes.DodgerBlue;

        /// <summary>边框宽度，物理像素。1 像素在 150% 缩放下细得几乎看不见，取 2。</summary>
        private const int FramePx = 2;

        public BitmapSource? Image { get; set; }
        public int SourceWidth { get; set; }
        public int SourceRows { get; set; }

        public PreviewPane()
        {
            // 预览条是缩小显示的，双线性在细字上更接近原样；最近邻会把一行字抽成锯齿
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Image is null || SourceWidth <= 0 || SourceRows <= 0) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            double scaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scaleX <= 0) scaleX = 1;

            double w = ActualWidth;
            double h = Math.Max(1, SourceRows * (w / SourceWidth));

            // 装得下就贴顶，装不下就贴底：长图只会往下长，用户关心的是刚拼上去的那一段
            double y = h <= ActualHeight ? 0 : ActualHeight - h;

            dc.DrawImage(Image, new Rect(0, y, w, h));

            // 边框只围「看得见的那一段」。图比窗口高时上沿跑到了窗口外面，
            // 那时边框贴窗口顶就行 —— 它要说明的是预览区到此为止，不是图的真实边界
            double top = Math.Max(0, y);
            double bottom = Math.Min(ActualHeight, y + h);
            if (bottom - top < 1) return;

            // 边框按物理像素给宽度：先折算成 DIP，再往里缩半个线宽，
            // 描边才正好压在边界内沿（和 ScrollFrameWindow 的 Ring 一个道理）
            double t = FramePx / scaleX;
            var pen = new Pen(FrameBrush, t);
            pen.Freeze();

            dc.DrawRectangle(null, pen, new Rect(
                t / 2, top + t / 2,
                Math.Max(0, w - t),
                Math.Max(0, bottom - top - t)));
        }
    }
}
