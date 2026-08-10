using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 放大镜 + 取色器。独立成一层，因为它跟着鼠标高频重绘，
/// 而遮罩、控制点、尺寸标签只在选区变化时才需要重算。
/// </summary>
public sealed class MagnifierLayer : FrameworkElement
{
    /// <summary>放大镜里横向/纵向各显示多少个源像素。取奇数，保证有唯一的中心像素。</summary>
    private const int SourceCols = 15;
    private const int SourceRows = 11;
    /// <summary>一个源像素放大成多少 DIP。</summary>
    private const double Zoom = 10;

    private const double ViewWidth = SourceCols * Zoom;
    private const double ViewHeight = SourceRows * Zoom;
    private const double PanelPadding = 8;
    private const double CursorGap = 24;
    private const double CornerRadius = 7;
    private const double FontSize = 12;
    private const double LineHeight = 19;
    private const double SwatchSize = 12;

    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)));
    private static readonly Brush DimTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x90, 0x9C)));
    private static readonly Pen ViewBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1));
    // 宽度必须是整数 1：本层开了 EdgeMode.Aliased，0.5px 的笔会被整像素对齐直接吃掉，
    // 结果就是网格线在纯色区域完全不可见 —— 而那恰恰是最需要网格来数像素的场景。
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen CrossPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x3B, 0x9E, 0xFF)), 1));
    private static readonly Pen CenterPen = Freeze(new Pen(Brushes.White, 1));
    private static readonly Pen SwatchPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Color OutOfBoundsFill = Color.FromRgb(0x20, 0x20, 0x20);

    public MagnifierLayer()
    {
        // 只关掉几何图元的抗锯齿，让网格线和准星落在整像素上。
        // 位图缩放模式这里刻意保持默认（平滑）—— 放大后的像素块是自己按设备像素
        // 展开好再 1:1 绘制的，不经过任何缩放；而同一层上的毛玻璃背景需要平滑插值。
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        IsHitTestVisible = false;
    }

    /// <summary>本屏的冻结帧，像素从这里取。</summary>
    public CapturedFrame? Frame { get; set; }

    /// <summary>毛玻璃背景。为 null 时面板退回不透明底色。</summary>
    public FrostedBackdrop? Backdrop { get; set; }

    /// <summary>光标位置（虚拟屏幕物理像素）。null 表示光标不在本屏，本层什么都不画。</summary>
    public PixelPoint? CursorPixel { get; set; }

    /// <summary>光标位置换算到本窗口的 DIP 坐标。</summary>
    public Point CursorLocal { get; set; }

    public Color Color { get; set; }
    public ColorFormat Format { get; set; }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (CursorPixel is not { } cursor || Frame is null) return;

        // 一个源像素占多少个设备像素。取整保证所有方块大小一致 —— 125% 这类分数缩放下
        // 若直接用 Zoom*scale，方块会时大时小，网格看起来就是歪的。
        var dpi = VisualTreeHelper.GetDpi(this);
        int blockW = Math.Max(1, (int)Math.Round(Zoom * dpi.DpiScaleX));
        int blockH = Math.Max(1, (int)Math.Round(Zoom * dpi.DpiScaleY));

        // 取整之后取景框的 DIP 尺寸会与标称的 Zoom*Cols 略有出入，面板尺寸随之算出来
        double cellW = blockW / dpi.DpiScaleX;
        double cellH = blockH / dpi.DpiScaleY;
        double viewW = cellW * SourceCols;
        double viewH = cellH * SourceRows;

        double panelW = viewW + PanelPadding * 2;
        double panelH = viewH + PanelPadding * 2 + LineHeight * 3 + 4;
        var panel = PlacePanel(panelW, panelH);

        PanelChrome.DrawGlassPanel(dc, panel, CornerRadius, Backdrop, new Size(ActualWidth, ActualHeight));

        var view = new Rect(panel.X + PanelPadding, panel.Y + PanelPadding, viewW, viewH);
        DrawPixels(dc, cursor, view, blockW, blockH, dpi);
        DrawGrid(dc, view, cellW, cellH);
        DrawCrosshair(dc, view, cellW, cellH);
        dc.DrawRectangle(null, ViewBorder, new Rect(view.X - 0.5, view.Y - 0.5, view.Width + 1, view.Height + 1));
        DrawReadout(dc, cursor, new Point(panel.X + PanelPadding, view.Bottom + 6));
    }

    /// <summary>
    /// 面板跟着光标走，但贴近屏幕边缘时翻到另一侧。
    /// 不翻的话面板会被窗口边界裁掉一半 —— 而光标贴边恰恰是最需要放大镜的时候。
    /// </summary>
    private Rect PlacePanel(double w, double h)
    {
        double x = CursorLocal.X + CursorGap;
        double y = CursorLocal.Y + CursorGap;

        if (x + w > ActualWidth) x = CursorLocal.X - CursorGap - w;
        if (y + h > ActualHeight) y = CursorLocal.Y - CursorGap - h;

        // 两侧都放不下（窗口比面板还小）时退回夹紧，至少保证完整可见
        x = Math.Max(0, Math.Min(x, ActualWidth - w));
        y = Math.Max(0, Math.Min(y, ActualHeight - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 取景框那张位图，连同展开像素块用的缓冲。
    ///
    /// 本层跟着鼠标每动一下就重绘一次，而每一帧新建一张 BitmapSource 是要付两笔账的：
    /// 一片托管缓冲的垃圾，和一个带终结器的 WIC 原生对象 —— 后者要等 GC 跑完终结队列
    /// 才真正把那块非托管内存还掉。拖一次选区几百帧，攒出来的就是任务管理器里那几十兆。
    /// 尺寸只跟 DPI 有关，画面每帧变的只是内容，所以一张可写位图原地更新正合适。
    /// </summary>
    private WriteableBitmap? _view;
    private byte[] _blocks = [];

    private void DrawPixels(DrawingContext dc, PixelPoint cursor, Rect view,
        int blockW, int blockH, DpiScale dpi)
    {
        int w = SourceCols * blockW;
        int h = SourceRows * blockH;
        double dpiX = 96 * dpi.DpiScaleX;
        double dpiY = 96 * dpi.DpiScaleY;

        // 跨屏拖动时 DPI 会变，方块尺寸随之变，那时候才需要换一张
        if (_view is null || _view.PixelWidth != w || _view.PixelHeight != h
            || Math.Abs(_view.DpiX - dpiX) > 0.01 || Math.Abs(_view.DpiY - dpiY) > 0.01)
        {
            _view = new WriteableBitmap(w, h, dpiX, dpiY, PixelFormats.Bgr32, null);
            _blocks = new byte[w * h * 4];
        }

        // 按设备像素展开好的方块阵列，绘制时严格 1:1，不经过任何缩放
        Frame!.SampleBlock(_blocks, cursor, SourceCols, SourceRows, OutOfBoundsFill, blockW, blockH);
        _view.WritePixels(new Int32Rect(0, 0, w, h), _blocks, w * 4, 0);
        dc.DrawImage(_view, view);
    }

    private static void DrawGrid(DrawingContext dc, Rect view, double cellW, double cellH)
    {
        // 网格让「一个方块 = 一个像素」变得可数，否则纯色区域里根本看不出粒度
        for (int c = 1; c < SourceCols; c++)
        {
            double x = view.X + c * cellW;
            dc.DrawLine(GridPen, new Point(x, view.Top), new Point(x, view.Bottom));
        }
        for (int r = 1; r < SourceRows; r++)
        {
            double y = view.Y + r * cellH;
            dc.DrawLine(GridPen, new Point(view.Left, y), new Point(view.Right, y));
        }
    }

    private static void DrawCrosshair(DrawingContext dc, Rect view, double cellW, double cellH)
    {
        double cx = view.X + (SourceCols / 2) * cellW;
        double cy = view.Y + (SourceRows / 2) * cellH;

        dc.DrawLine(CrossPen, new Point(view.Left, cy + cellH / 2), new Point(cx, cy + cellH / 2));
        dc.DrawLine(CrossPen, new Point(cx + cellW, cy + cellH / 2), new Point(view.Right, cy + cellH / 2));
        dc.DrawLine(CrossPen, new Point(cx + cellW / 2, view.Top), new Point(cx + cellW / 2, cy));
        dc.DrawLine(CrossPen, new Point(cx + cellW / 2, cy + cellH), new Point(cx + cellW / 2, view.Bottom));

        // 正中那一个像素单独描白框：取色取的就是它，不能有歧义
        dc.DrawRectangle(null, CenterPen, new Rect(cx - 0.5, cy - 0.5, cellW + 1, cellH + 1));
    }

    private void DrawReadout(DrawingContext dc, PixelPoint cursor, Point origin)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, Brush brush) => new(
            s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, brush, ppd);

        dc.DrawText(Text($"({cursor.X}, {cursor.Y})", TextBrush), origin);

        var swatchRect = new Rect(origin.X + 0.5, origin.Y + LineHeight + 3.5, SwatchSize, SwatchSize);
        var swatchBrush = new SolidColorBrush(Color);
        swatchBrush.Freeze();
        dc.DrawRoundedRectangle(swatchBrush, SwatchPen, swatchRect, 2, 2);

        string value = Format == ColorFormat.Hex
            ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", Color.R, Color.G, Color.B)
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", Color.R, Color.G, Color.B);
        dc.DrawText(Text(value, TextBrush), new Point(swatchRect.Right + 8, origin.Y + LineHeight));

        dc.DrawText(Text("C 复制   Shift 切换", DimTextBrush), new Point(origin.X, origin.Y + LineHeight * 2 + 3));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
