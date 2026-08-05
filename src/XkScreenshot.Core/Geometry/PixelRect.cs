namespace XkScreenshot.Core.Geometry;

/// <summary>
/// 物理像素矩形，坐标系一律是「虚拟屏幕坐标」（多显示器拼成的大坐标系，左上角可能是负数）。
/// 整个项目内部只用它做几何运算，DIP 只在 WPF 渲染边界上出现一次。
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>由两个角点构造，自动归一化方向（支持反向拖拽）。</summary>
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b)
        => FromLtrb(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                    Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PixelPoint p)
        => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool IntersectsWith(PixelRect o)
        => o.X < Right && X < o.Right && o.Y < Bottom && Y < o.Bottom;

    public PixelRect Intersect(PixelRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? Empty : FromLtrb(l, t, r, b);
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>把矩形夹进 bounds 内部，保持尺寸不变（不够大时才裁剪）。</summary>
    public PixelRect ClampInto(PixelRect bounds)
    {
        int w = Math.Min(Width, bounds.Width);
        int h = Math.Min(Height, bounds.Height);
        int x = Math.Clamp(X, bounds.X, bounds.Right - w);
        int y = Math.Clamp(Y, bounds.Y, bounds.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    public override string ToString() => $"{Width}x{Height} @ ({X},{Y})";
}

public readonly record struct PixelPoint(int X, int Y)
{
    public static PixelPoint operator -(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);
    public int ManhattanTo(PixelPoint o) => Math.Abs(X - o.X) + Math.Abs(Y - o.Y);
}
