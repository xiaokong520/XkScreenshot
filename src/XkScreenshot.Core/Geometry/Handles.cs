using System;
using System.Windows;

namespace XkScreenshot.Core.Geometry;

/// <summary>
/// 矩形八个控制点的几何：编号、位置、命中测试、拉伸规则。
///
/// 选区和标注共用同一份实现 —— 两处的手感必须完全一致，
/// 各写一份迟早会在「拖过头能不能翻转」「边缘控制点动几条边」这类细节上分叉。
/// 编号顺序与绘制顺序相同：自上而下、自左而右。
/// </summary>
public static class Handles
{
    public const int Count = 8;

    public const int TopLeft = 0;
    public const int Top = 1;
    public const int TopRight = 2;
    public const int Left = 3;
    public const int Right = 4;
    public const int BottomLeft = 5;
    public const int Bottom = 6;
    public const int BottomRight = 7;

    public static Point At(Rect r, int index)
    {
        double cx = r.Left + r.Width / 2;
        double cy = r.Top + r.Height / 2;

        return index switch
        {
            TopLeft => new Point(r.Left, r.Top),
            Top => new Point(cx, r.Top),
            TopRight => new Point(r.Right, r.Top),
            Left => new Point(r.Left, cy),
            Right => new Point(r.Right, cy),
            BottomLeft => new Point(r.Left, r.Bottom),
            Bottom => new Point(cx, r.Bottom),
            BottomRight => new Point(r.Right, r.Bottom),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    /// <summary>
    /// 命中测试，返回控制点编号，-1 表示没命中。
    ///
    /// 矩形小到控制点会互相压住时一律不认：那种尺寸下点哪儿都是歧义，
    /// 与其让用户拉出一个自己都没想清楚的结果，不如退回「拖动 = 整体平移」。
    /// </summary>
    public static int HitTest(Rect r, Point p, double tolerance)
    {
        if (r.Width < tolerance * 2 || r.Height < tolerance * 2) return -1;

        for (int i = 0; i < Count; i++)
        {
            var h = At(r, i);
            if (Math.Abs(p.X - h.X) <= tolerance && Math.Abs(p.Y - h.Y) <= tolerance) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把编号为 index 的控制点拖到 to，返回新矩形。
    /// 拖过头翻转是允许的，结果总是规范化的正矩形（宽高非负）。
    /// </summary>
    public static Rect Resize(Rect r, int index, Point to)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

        if (index is TopLeft or Left or BottomLeft) left = to.X;
        if (index is TopRight or Right or BottomRight) right = to.X;
        if (index is TopLeft or Top or TopRight) top = to.Y;
        if (index is BottomLeft or Bottom or BottomRight) bottom = to.Y;

        return new Rect(
            Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
    }

    /// <summary>控制点是否值得画出来。太小的矩形上画八个点只会糊成一团。</summary>
    public static bool FitIn(Rect r, double handleSize)
        => r.Width >= handleSize * 3 && r.Height >= handleSize * 3;

    // ---------------- 物理像素版本 ----------------
    // 选区用的是整数物理像素，换算一次比让调用方到处 new Rect 干净。

    public static PixelPoint At(PixelRect r, int index)
    {
        var p = At(ToRect(r), index);
        return new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    public static int HitTest(PixelRect r, PixelPoint p, double tolerance)
        => HitTest(ToRect(r), new Point(p.X, p.Y), tolerance);

    public static PixelRect Resize(PixelRect r, int index, PixelPoint to)
    {
        var next = Resize(ToRect(r), index, new Point(to.X, to.Y));
        return PixelRect.FromLtrb(
            (int)Math.Round(next.Left), (int)Math.Round(next.Top),
            (int)Math.Round(next.Right), (int)Math.Round(next.Bottom));
    }

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}
