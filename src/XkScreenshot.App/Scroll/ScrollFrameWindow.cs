using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Scroll;

/// <summary>长截图期间自己那几个窗口的共同处理。</summary>
internal static class ScrollChrome
{
    /// <summary>
    /// 把窗口从截屏里排除掉。
    ///
    /// 长截图抓的是屏幕 DC，我们自己的面板一旦压在目标区域上就会被原样拍进长图里。
    /// 首选当然是把面板摆到区域外面（见 <see cref="ScrollPanelWindow"/> 的落位），
    /// 但区域占满整屏时就无处可躲，这一条是那时候唯一的退路。
    ///
    /// Win10 2004 才有 WDA_EXCLUDEFROMCAPTURE，更早的系统上会失败 ——
    /// 失败也没有补救办法，所以不报错：那时候用户顶多在长图角上看到一次面板，
    /// 而为此弹一个他看不懂也处理不了的警告只会更糟。
    /// </summary>
    public static void ExcludeFromCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>让窗口彻底不吃鼠标 —— 它盖在别人的界面上，挡住点击就等于把那块界面废掉了。</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TRANSPARENT);
    }

    /// <summary>
    /// 按物理像素摆窗口。理由同 <c>OverlayWindow</c>：WPF 的 Left/Top 是 DIP，
    /// 窗口还没归属某台显示器时换算基准是错的，多屏下必然摆偏。
    /// </summary>
    public static void PlacePixels(Window window, PixelRect rect, bool activate = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        uint flags = NativeMethods.SWP_SHOWWINDOW | (activate ? 0 : NativeMethods.SWP_NOACTIVATE);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            rect.X, rect.Y, rect.Width, rect.Height, flags);
    }
}

/// <summary>
/// 长截图期间套在目标区域外面的那一圈框。
///
/// 覆盖层一关，屏幕就恢复原样了 —— 用户面前是一个正常的窗口，没有任何迹象说明
/// 「现在正在拍的是这一块」。手动模式尤其需要它：得先知道拍的是哪儿，才谈得上往哪儿滚。
///
/// 整圈画在选区**外面**（窗口比选区大一圈，中间是透明的），所以它自己绝不会被拍进去 ——
/// 这比依赖「排除截屏」那条更牢靠，后者在旧系统上是不生效的。
/// </summary>
internal sealed class ScrollFrameWindow : Window
{
    /// <summary>边框宽度，物理像素。</summary>
    private const int BorderPx = 3;

    private readonly PixelRect _outer;

    public ScrollFrameWindow(PixelRect region)
    {
        _outer = new PixelRect(
            region.X - BorderPx, region.Y - BorderPx,
            region.Width + BorderPx * 2, region.Height + BorderPx * 2);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Content = new Ring { Thickness = BorderPx };

        SourceInitialized += (_, _) =>
        {
            ScrollChrome.MakeClickThrough(this);
            ScrollChrome.ExcludeFromCapture(this);
            ScrollChrome.PlacePixels(this, _outer);
        };
    }

    private sealed class Ring : FrameworkElement
    {
        private static readonly Pen BorderPen =
            Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x3B, 0x9E, 0xFF)), 1));

        /// <summary>边框的物理像素宽度。画的时候按本窗口的 DPI 折成 DIP。</summary>
        public int Thickness { get; init; } = 3;

        public Ring() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0) scale = 1;

            double t = Thickness / scale;
            var pen = BorderPen.Clone();
            pen.Thickness = t;
            pen.Freeze();

            // 描边是以路径为中线往两边各铺一半的，所以矩形要往里缩半个线宽，
            // 整条线才正好落在窗口那一圈上、内沿贴着选区边界
            dc.DrawRectangle(null, pen, new Rect(
                t / 2, t / 2,
                Math.Max(0, ActualWidth - t),
                Math.Max(0, ActualHeight - t)));
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
