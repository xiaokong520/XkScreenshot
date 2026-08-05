using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.App.Overlay;

/// <summary>覆盖层上各个浮动面板共用的外观零件。</summary>
internal static class PanelChrome
{
    private const int ShadowSteps = 6;
    private const double ShadowSpread = 2.0;
    private const double ShadowDepth = 2.0;

    private static readonly Brush ShadowBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)));

    /// <summary>
    /// 压在模糊背景上的暗色底调。刻意做得比较淡：模糊背景在构建时已经压暗过一档，
    /// 这里再重手压就会把玻璃感彻底盖没，变回一块普通的深色面板。
    /// </summary>
    private static readonly Brush TintBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0x8E, 0x12, 0x14, 0x1A)));

    /// <summary>模糊背景不可用时的兜底底色。</summary>
    private static readonly Brush OpaqueBrush =
        Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x16, 0x1A)));

    private static readonly Pen BorderPen =
        Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 1));

    /// <summary>沿顶边的一道内高光。玻璃的立体感基本全靠这一条线。</summary>
    private static readonly Pen TopHighlightPen =
        Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), 1));

    /// <summary>
    /// 在圆角矩形背后画一层柔和投影。面板浮在截图画面之上，没有投影就像是被
    /// 「贴」进图里，分不清层次。
    ///
    /// 不用 DropShadowEffect：给一个 FrameworkElement 挂 Effect 会让 WPF 把
    /// 整层渲染到离屏中间表面再做模糊，而覆盖层是全屏尺寸的。放大镜每次鼠标
    /// 移动都要重绘，这个代价完全不划算。手工叠几层低透明度圆角矩形，效果够用、
    /// 开销可预测，也不产生任何中间表面。
    /// </summary>
    public static void DrawShadow(DrawingContext dc, Rect rect, double cornerRadius)
    {
        for (int i = ShadowSteps; i >= 1; i--)
        {
            double grow = i * ShadowSpread;
            var layer = new Rect(
                rect.X - grow,
                rect.Y - grow + ShadowDepth,
                rect.Width + grow * 2,
                rect.Height + grow * 2);
            dc.DrawRoundedRectangle(ShadowBrush, null, layer, cornerRadius + grow, cornerRadius + grow);
        }
    }

    /// <summary>
    /// 画一块毛玻璃面板：投影 → 模糊背景（裁成圆角）→ 暗色底调 → 顶边内高光 → 描边。
    /// backdrop 为 null 时退回不透明底色，功能不受影响。
    /// </summary>
    public static void DrawGlassPanel(DrawingContext dc, Rect panel, double cornerRadius,
        FrostedBackdrop? backdrop, Size windowSize)
    {
        DrawShadow(dc, panel, cornerRadius);

        var shape = new RectangleGeometry(panel, cornerRadius, cornerRadius);
        shape.Freeze();

        if (backdrop is null)
        {
            dc.DrawGeometry(OpaqueBrush, null, shape);
        }
        else
        {
            dc.PushClip(shape);
            backdrop.DrawInto(dc, panel, windowSize);
            dc.DrawRectangle(TintBrush, null, panel);
            dc.Pop();
        }

        // 高光落在顶边内侧半像素处，避开描边本身，否则两条线会糊成一条
        dc.DrawLine(TopHighlightPen,
            new Point(panel.Left + cornerRadius, panel.Top + 0.5),
            new Point(panel.Right - cornerRadius, panel.Top + 0.5));

        dc.DrawGeometry(null, BorderPen, shape);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
