using System;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.App.Scroll;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Gif;

/// <summary>
/// 录制期间套在选区外面的那圈框。
///
/// 和长截图那圈是同一个理由：覆盖层一关，屏幕上就再没有东西说明「正在录的是这一块」。
/// 区别只在颜色 —— 红色是「正在录」的通用信号，长截图那圈是主题强调色。
///
/// 整圈画在选区**外面**（窗口比选区大一圈、中间透明），所以它绝不会被录进去；
/// 再加上排除截屏，压在边上也不怕。
/// </summary>
internal sealed class GifFrameWindow : Window
{
    /// <summary>边框宽度，物理像素。</summary>
    private const int BorderPx = 3;

    private static readonly Brush RecordRed = Freeze(Color.FromRgb(0xF0, 0x44, 0x38));

    private readonly PixelRect _outer;

    public GifFrameWindow(PixelRect region)
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
        Content = new Ring { Thickness = BorderPx, Brush = RecordRed };

        SourceInitialized += (_, _) =>
        {
            ScrollChrome.MakeClickThrough(this);
            ScrollChrome.ExcludeFromCapture(this);
            ScrollChrome.PlacePixels(this, _outer);
        };
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Ring : FrameworkElement
    {
        public int Thickness { get; init; } = 3;
        public Brush Brush { get; init; } = Brushes.Red;

        public Ring() => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0) scale = 1;

            double t = Thickness / scale;
            var pen = new Pen(Brush, t);
            pen.Freeze();

            // 描边以路径为中线往两边各铺一半，矩形往里缩半个线宽，内沿才贴着选区边界
            dc.DrawRectangle(null, pen, new Rect(
                t / 2, t / 2,
                Math.Max(0, ActualWidth - t),
                Math.Max(0, ActualHeight - t)));
        }
    }
}
