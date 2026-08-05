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
        Freeze(new SolidColorBrush(Color.FromArgb(0x11, 0, 0, 0)));

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

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
