using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 覆盖层的绘制层：遮罩、高亮区边框、控制点、尺寸标签。
/// 全部在一次 OnRender 里画完，避免用一堆 Shape 元素导致拖拽时的布局抖动。
/// 输入是本显示器局部的 DIP 坐标，换算在 OverlayWindow 里完成。
/// </summary>
public sealed class SelectionLayer : FrameworkElement
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF)));
    private static readonly Brush HandleFill = Brushes.White;
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0x1A, 0x1A, 0x1A)));
    private static readonly Pen BorderPen = Freeze(new Pen(AccentBrush, 1.5));
    private static readonly Pen HandlePen = Freeze(new Pen(AccentBrush, 1.0));
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private const double HandleSize = 7;
    private const double LabelFontSize = 12;
    private const double LabelPadding = 5;

    /// <summary>
    /// 当前高亮区在本窗口内的 DIP 矩形；null 表示本屏没有高亮区。
    /// 它可能来自已确定的选区，也可能来自鼠标悬停命中的窗口 —— 两者视觉上一视同仁。
    /// </summary>
    public Rect? HighlightLocal { get; set; }

    /// <summary>标签上显示的物理像素尺寸（是完整高亮区的尺寸，不是被本屏裁掉之后的）。</summary>
    public PixelRect HighlightPixels { get; set; } = PixelRect.Empty;

    /// <summary>只有拥有高亮区左上角的那块屏才画尺寸标签，避免跨屏时重复显示。</summary>
    public bool ShowSizeLabel { get; set; }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);

        // 必须画一层（哪怕全透明）才能拿到鼠标命中
        dc.DrawRectangle(Brushes.Transparent, null, full);

        if (HighlightLocal is not { Width: > 0, Height: > 0 } rect)
        {
            dc.DrawRectangle(DimBrush, null, full);
            return;
        }

        // 把高亮区从遮罩里挖掉：目标区域保持冻结画面的原样，其余压暗。
        // 这样在真正按下鼠标之前，用户就能确认自己将要截到的到底是哪一块。
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(rect));
        mask.Freeze();
        dc.DrawGeometry(DimBrush, null, mask);

        // 边框画在高亮区外沿上，保证目标区域的内容一个像素都不被盖住
        dc.DrawRectangle(null, BorderPen, Inflate(rect, BorderPen.Thickness / 2));
        DrawHandles(dc, rect);

        if (ShowSizeLabel)
            DrawSizeLabel(dc, rect);
    }

    /// <summary>
    /// 控制点的位置一律问 <see cref="Handles"/> 要，不在这儿自己算。
    /// 画在一处、点在另一处是这类界面最烦人的 bug，而且极难被看出来。
    /// </summary>
    private static void DrawHandles(DrawingContext dc, Rect rect)
    {
        // 高亮区太小时控制点会糊成一团，直接不画
        if (!Handles.FitIn(rect, HandleSize)) return;

        double half = HandleSize / 2;
        for (int i = 0; i < Handles.Count; i++)
        {
            var p = Handles.At(rect, i);
            dc.DrawRectangle(HandleFill, HandlePen,
                new Rect(p.X - half, p.Y - half, HandleSize, HandleSize));
        }
    }

    private void DrawSizeLabel(DrawingContext dc, Rect rect)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            $"{HighlightPixels.Width} × {HighlightPixels.Height} px",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, LabelFontSize, Brushes.White, pixelsPerDip);

        double w = text.Width + LabelPadding * 2;
        double h = text.Height + LabelPadding;

        // 默认放在高亮区上方；顶到屏幕边缘就翻到区域内部，别跑出可视区
        double x = rect.Left;
        double y = rect.Top - h - 4;
        if (y < 0) y = Math.Min(rect.Top + 4, ActualHeight - h);
        if (x + w > ActualWidth) x = Math.Max(0, ActualWidth - w);

        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(x, y, w, h), 3, 3);
        dc.DrawText(text, new Point(x + LabelPadding, y + LabelPadding / 2));
    }

    private static Rect Inflate(Rect r, double by)
    {
        var result = r;
        result.Inflate(by, by);
        return result.Width < 0 || result.Height < 0 ? r : result;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
