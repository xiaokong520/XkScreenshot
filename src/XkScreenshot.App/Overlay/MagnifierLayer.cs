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
    private const double Zoom = 8;

    private const double ViewWidth = SourceCols * Zoom;
    private const double ViewHeight = SourceRows * Zoom;
    private const double PanelPadding = 6;
    private const double CursorGap = 22;
    private const double FontSize = 11.5;
    private const double LineHeight = 16;
    private const double SwatchSize = 11;

    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x14, 0x14)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    private static readonly Brush DimTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));
    private static readonly Pen PanelBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 0.5));
    private static readonly Pen CrossPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x3B, 0x9E, 0xFF)), 1));
    private static readonly Pen CenterPen = Freeze(new Pen(Brushes.White, 1));
    private static readonly Pen SwatchPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Color OutOfBoundsFill = Color.FromRgb(0x20, 0x20, 0x20);

    public MagnifierLayer()
    {
        // 放大镜的全部意义就是让人看清单个像素，绝不能让 WPF 做任何插值
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        IsHitTestVisible = false;
    }

    /// <summary>本屏的冻结帧，像素从这里取。</summary>
    public CapturedFrame? Frame { get; set; }

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

        double panelW = ViewWidth + PanelPadding * 2;
        double panelH = ViewHeight + PanelPadding * 2 + LineHeight * 3;
        var panel = PlacePanel(panelW, panelH);

        dc.DrawRoundedRectangle(PanelBrush, PanelBorder, panel, 4, 4);

        var view = new Rect(panel.X + PanelPadding, panel.Y + PanelPadding, ViewWidth, ViewHeight);
        DrawPixels(dc, cursor, view);
        DrawGrid(dc, view);
        DrawCrosshair(dc, view);
        DrawReadout(dc, cursor, new Point(panel.X + PanelPadding, view.Bottom + 3));
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

    private void DrawPixels(DrawingContext dc, PixelPoint cursor, Rect view)
    {
        var block = Frame!.SampleBlock(cursor, SourceCols, SourceRows, OutOfBoundsFill);
        var bmp = BitmapSource.Create(SourceCols, SourceRows, 96, 96,
            PixelFormats.Bgra32, null, block, SourceCols * 4);
        bmp.Freeze();
        dc.DrawImage(bmp, view);
    }

    private static void DrawGrid(DrawingContext dc, Rect view)
    {
        // 网格让「一个方块 = 一个像素」变得可数，否则纯色区域里根本看不出粒度
        for (int c = 1; c < SourceCols; c++)
        {
            double x = view.X + c * Zoom;
            dc.DrawLine(GridPen, new Point(x, view.Top), new Point(x, view.Bottom));
        }
        for (int r = 1; r < SourceRows; r++)
        {
            double y = view.Y + r * Zoom;
            dc.DrawLine(GridPen, new Point(view.Left, y), new Point(view.Right, y));
        }
    }

    private static void DrawCrosshair(DrawingContext dc, Rect view)
    {
        double cx = view.X + (SourceCols / 2) * Zoom;
        double cy = view.Y + (SourceRows / 2) * Zoom;

        dc.DrawLine(CrossPen, new Point(view.Left, cy + Zoom / 2), new Point(cx, cy + Zoom / 2));
        dc.DrawLine(CrossPen, new Point(cx + Zoom, cy + Zoom / 2), new Point(view.Right, cy + Zoom / 2));
        dc.DrawLine(CrossPen, new Point(cx + Zoom / 2, view.Top), new Point(cx + Zoom / 2, cy));
        dc.DrawLine(CrossPen, new Point(cx + Zoom / 2, cy + Zoom), new Point(cx + Zoom / 2, view.Bottom));

        // 正中那一个像素单独描白框：取色取的就是它，不能有歧义
        dc.DrawRectangle(null, CenterPen, new Rect(cx - 0.5, cy - 0.5, Zoom + 1, Zoom + 1));
    }

    private void DrawReadout(DrawingContext dc, PixelPoint cursor, Point origin)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, Brush brush) => new(
            s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, brush, ppd);

        dc.DrawText(Text($"({cursor.X}, {cursor.Y})", TextBrush), origin);

        var swatchRect = new Rect(origin.X, origin.Y + LineHeight + 2, SwatchSize, SwatchSize);
        var swatchBrush = new SolidColorBrush(Color);
        swatchBrush.Freeze();
        dc.DrawRectangle(swatchBrush, SwatchPen, swatchRect);

        string value = Format == ColorFormat.Hex
            ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", Color.R, Color.G, Color.B)
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", Color.R, Color.G, Color.B);
        dc.DrawText(Text(value, TextBrush), new Point(swatchRect.Right + 6, origin.Y + LineHeight));

        dc.DrawText(Text("C 复制  Shift 切换", DimTextBrush), new Point(origin.X, origin.Y + LineHeight * 2 + 2));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
