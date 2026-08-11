using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.App.Overlay;

/// <summary>覆盖层上各个浮动面板共用的外观零件。颜色一律由调用方传进来的调色板决定。</summary>
internal static class PanelChrome
{
    private const int ShadowSteps = 6;
    private const double ShadowSpread = 2.0;
    private const double ShadowDepth = 2.0;

    /// <summary>
    /// 在圆角矩形背后画一层柔和投影。面板浮在截图画面之上，没有投影就像是被
    /// 「贴」进图里，分不清层次。
    ///
    /// 不用 DropShadowEffect：给一个 FrameworkElement 挂 Effect 会让 WPF 把
    /// 整层渲染到离屏中间表面再做模糊，而覆盖层是全屏尺寸的。放大镜每次鼠标
    /// 移动都要重绘，这个代价完全不划算。手工叠几层低透明度圆角矩形，效果够用、
    /// 开销可预测，也不产生任何中间表面。
    /// </summary>
    public static void DrawShadow(DrawingContext dc, Rect rect, double cornerRadius, OverlayPalette palette)
    {
        for (int i = ShadowSteps; i >= 1; i--)
        {
            double grow = i * ShadowSpread;
            var layer = new Rect(
                rect.X - grow,
                rect.Y - grow + ShadowDepth,
                rect.Width + grow * 2,
                rect.Height + grow * 2);
            dc.DrawRoundedRectangle(palette.PanelShadow, null, layer, cornerRadius + grow, cornerRadius + grow);
        }
    }

    /// <summary>
    /// 画一块毛玻璃面板：投影 → 模糊背景（裁成圆角）→ 底调 → 顶边内高光 → 描边。
    /// backdrop 为 null 时退回不透明底色，功能不受影响。
    /// </summary>
    public static void DrawGlassPanel(DrawingContext dc, Rect panel, double cornerRadius,
        FrostedBackdrop? backdrop, Size windowSize, OverlayPalette palette)
    {
        DrawShadow(dc, panel, cornerRadius, palette);

        var shape = new RectangleGeometry(panel, cornerRadius, cornerRadius);
        shape.Freeze();

        if (backdrop is null)
        {
            dc.DrawGeometry(palette.PanelOpaque, null, shape);
        }
        else
        {
            dc.PushClip(shape);
            backdrop.DrawInto(dc, panel, windowSize);
            dc.DrawRectangle(palette.PanelTint, null, panel);
            dc.Pop();
        }

        // 高光落在顶边内侧半像素处，避开描边本身，否则两条线会糊成一条
        dc.DrawLine(palette.PanelTopHighlight,
            new Point(panel.Left + cornerRadius, panel.Top + 0.5),
            new Point(panel.Right - cornerRadius, panel.Top + 0.5));

        dc.DrawGeometry(null, palette.PanelBorder, shape);
    }
}
