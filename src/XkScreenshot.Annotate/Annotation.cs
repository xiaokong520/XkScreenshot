using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Annotate;

public enum ToolKind
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Ink,
    Text,
    Mosaic,
}

/// <summary>
/// 画笔参数的一份完整快照，与几何无关。
///
/// 各字段并非每种标注都用得上（矩形没有字号，文字没有马赛克粒度）。
/// 打包成一个整体传递，是为了让「改样式」这件事只有一个入口 ——
/// 每加一个参数就给每种标注加一个方法的话，很快就没人记得住哪种支持哪些了。
/// </summary>
public readonly record struct AnnotationStyle(
    Color Stroke, double Thickness, double FontSize, int MosaicBlock, double MosaicBrushWidth);

/// <summary>
/// 一个标注。
///
/// 坐标系是「选区局部像素」—— 原点在选区左上角，单位是物理像素。
/// 这样标注文档跟选区在屏幕上的位置完全解耦：选区被平移、贴图被拖到别的显示器、
/// 或者以后要把标注存进文件重新编辑，都不需要做任何坐标换算。
/// </summary>
public abstract class Annotation
{
    public required Color Stroke { get; init; }
    public required double Thickness { get; init; }

    /// <summary>这是哪个工具画出来的。选中它时，子工具栏据此决定摆哪几个参数。</summary>
    public abstract ToolKind Kind { get; }

    /// <summary>
    /// 把自己的样式盖在一份默认值上。
    ///
    /// 只覆盖自己真正用到的那几个字段：选中一个矩形时，子工具栏该显示它的线宽，
    /// 但「字号」这种它根本没有的参数只能沿用画笔的默认值。
    /// </summary>
    public virtual AnnotationStyle StyleOver(AnnotationStyle fallback)
        => fallback with { Stroke = Stroke, Thickness = Thickness };

    /// <summary>换一套样式，几何原样不动。同样只取自己用得上的那几个字段。</summary>
    public abstract Annotation WithStyle(AnnotationStyle style);

    public abstract void Draw(DrawingContext dc, IAnnotationContext context);

    /// <summary>该标注影响到的区域，用于局部重绘。含描边加粗。</summary>
    public abstract Rect Bounds { get; }

    /// <summary>
    /// 几何外框，不含描边加粗。控制点摆位和拉伸都基于它 ——
    /// 用 Bounds 的话，线越粗外框越比图形大一圈，控制点会浮在图形外面。
    /// </summary>
    public abstract Rect Frame { get; }

    /// <summary>按新外框等比重塑一份副本。</summary>
    public abstract Annotation WithFrame(Rect frame);

    /// <summary>控制点个数。默认是外框的八个点，箭头只有两个端点。</summary>
    public virtual int HandleCount => Handles.Count;

    public virtual Point HandleAt(int index) => Handles.At(Frame, index);

    /// <summary>
    /// 控制点对应的八向编号（<see cref="Handles"/> 里的常量），-1 表示这是个自由端点。
    /// 只用来决定指针形状：控制点少于八个时，编号和位置对不上，得由标注自己说明。
    /// </summary>
    public virtual int HandleAxis(int index) => index;

    /// <summary>把某个控制点拖到 to，返回重塑后的副本。</summary>
    public virtual Annotation DragHandle(int index, Point to)
        => WithFrame(Handles.Resize(Frame, index, to));

    /// <summary>
    /// 光标是否落在这个标注上（点选用）。
    /// 空心图形只认轮廓附近：否则画一个大方框圈住内容之后，
    /// 框内的空白全被它吃掉，选区再也拖不动了。
    /// </summary>
    public virtual bool HitTest(Point p, double tolerance) => Inflated(Frame, tolerance).Contains(p);

    protected double Reach(double tolerance) => tolerance + Thickness / 2;

    protected static Rect Inflated(Rect r, double by)
    {
        var result = r;
        result.Inflate(by, by);
        return result.Width < 0 || result.Height < 0 ? r : result;
    }

    /// <summary>把 from 里的一个点按相对位置映射进 to。退化的那条边取原位。</summary>
    protected static Point MapInto(Point p, Rect from, Rect to)
    {
        double fx = from.Width > 0 ? (p.X - from.X) / from.Width : 0;
        double fy = from.Height > 0 ? (p.Y - from.Y) / from.Height : 0;
        return new Point(to.X + fx * to.Width, to.Y + fy * to.Height);
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9) return Distance(p, a);

        // 投影到线段上的参数，夹到 [0,1] 就自然处理了两个端点外侧
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// 平移一份副本。选区被移动时，标注要跟着「内容」走而不是跟着选框走 ——
    /// 坐标是选区局部的，选区往右挪一格，标注的局部坐标就得往左挪一格，
    /// 才能仍然盖在原来那块画面上。标注本身不可变，所以返回新对象。
    /// </summary>
    public abstract Annotation Translate(double dx, double dy);

    protected Pen CreatePen()
    {
        var brush = new SolidColorBrush(Stroke);
        brush.Freeze();
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>绘制标注时需要向外索取的东西（目前只有马赛克要用到原图）。</summary>
public interface IAnnotationContext
{
    /// <summary>把选区局部矩形内的画面按 block 大小马赛克化后画出来。</summary>
    void DrawMosaic(DrawingContext dc, Rect area, int block);
}

public sealed class RectangleAnnotation : Annotation
{
    public required Rect Area { get; init; }
    public bool Filled { get; init; }

    public override ToolKind Kind => ToolKind.Rectangle;
    public override Rect Bounds => Inflated(Area, Thickness);
    public override Rect Frame => Area;

    public override Annotation WithStyle(AnnotationStyle style) => new RectangleAnnotation
    {
        Area = Area, Filled = Filled, Stroke = style.Stroke, Thickness = style.Thickness,
    };

    public override void Draw(DrawingContext dc, IAnnotationContext context)
    {
        Brush? fill = null;
        if (Filled)
        {
            var b = new SolidColorBrush(Stroke);
            b.Freeze();
            fill = b;
        }
        dc.DrawRectangle(fill, Filled ? null : CreatePen(), Area);
    }

    public override Annotation Translate(double dx, double dy) => new RectangleAnnotation
    {
        Area = Shift(Area, dx, dy), Filled = Filled, Stroke = Stroke, Thickness = Thickness,
    };

    public override Annotation WithFrame(Rect frame) => new RectangleAnnotation
    {
        Area = frame, Filled = Filled, Stroke = Stroke, Thickness = Thickness,
    };

    /// <summary>空心矩形只认边框附近 —— 框内的大片空白不该被它吃掉。</summary>
    public override bool HitTest(Point p, double tolerance)
    {
        double reach = Reach(tolerance);
        if (!Inflated(Area, reach).Contains(p)) return false;
        if (Filled) return true;

        var inner = Inflated(Area, -reach);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
    }

    internal static Rect Shift(Rect r, double dx, double dy)
        => new(r.X + dx, r.Y + dy, r.Width, r.Height);
}

public sealed class EllipseAnnotation : Annotation
{
    public required Rect Area { get; init; }

    public override ToolKind Kind => ToolKind.Ellipse;
    public override Rect Bounds => Inflated(Area, Thickness);
    public override Rect Frame => Area;

    public override Annotation WithStyle(AnnotationStyle style) => new EllipseAnnotation
    {
        Area = Area, Stroke = style.Stroke, Thickness = style.Thickness,
    };

    public override void Draw(DrawingContext dc, IAnnotationContext context)
        => dc.DrawEllipse(null, CreatePen(),
            new Point(Area.X + Area.Width / 2, Area.Y + Area.Height / 2),
            Area.Width / 2, Area.Height / 2);

    public override Annotation Translate(double dx, double dy) => new EllipseAnnotation
    {
        Area = RectangleAnnotation.Shift(Area, dx, dy), Stroke = Stroke, Thickness = Thickness,
    };

    public override Annotation WithFrame(Rect frame) => new EllipseAnnotation
    {
        Area = frame, Stroke = Stroke, Thickness = Thickness,
    };

    /// <summary>只认椭圆轮廓附近。把点归一化到单位圆上比较，再换算回像素距离。</summary>
    public override bool HitTest(Point p, double tolerance)
    {
        double rx = Area.Width / 2, ry = Area.Height / 2;
        if (rx <= 0 || ry <= 0) return false;

        double nx = (p.X - (Area.X + rx)) / rx;
        double ny = (p.Y - (Area.Y + ry)) / ry;
        double radial = Math.Sqrt(nx * nx + ny * ny);

        return Math.Abs(radial - 1) * Math.Min(rx, ry) <= Reach(tolerance);
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public required Point From { get; init; }
    public required Point To { get; init; }

    /// <summary>箭头大小跟着线宽走，细线配大箭头会很怪。</summary>
    private double HeadLength => Math.Max(10, Thickness * 4.5);

    public override ToolKind Kind => ToolKind.Arrow;
    public override Rect Bounds => Inflated(new Rect(From, To), HeadLength);
    public override Rect Frame => new(From, To);

    public override Annotation WithStyle(AnnotationStyle style) => new ArrowAnnotation
    {
        From = From, To = To, Stroke = style.Stroke, Thickness = style.Thickness,
    };

    /// <summary>箭头给两个端点，比外框八个点自然得多 —— 要调的本来就是「从哪指到哪」。</summary>
    public override int HandleCount => 2;

    public override Point HandleAt(int index) => index == 0 ? From : To;

    public override int HandleAxis(int index) => -1;

    public override Annotation DragHandle(int index, Point to) => new ArrowAnnotation
    {
        From = index == 0 ? to : From,
        To = index == 0 ? To : to,
        Stroke = Stroke,
        Thickness = Thickness,
    };

    public override Annotation WithFrame(Rect frame) => new ArrowAnnotation
    {
        From = MapInto(From, Frame, frame),
        To = MapInto(To, Frame, frame),
        Stroke = Stroke,
        Thickness = Thickness,
    };

    /// <summary>按到线段的距离判定。斜箭头的外框很大，用外框会点哪儿都命中。</summary>
    public override bool HitTest(Point p, double tolerance)
        => DistanceToSegment(p, From, To) <= Reach(tolerance) + HeadLength / 2;

    public override void Draw(DrawingContext dc, IAnnotationContext context)
    {
        var pen = CreatePen();

        double dx = To.X - From.X;
        double dy = To.Y - From.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5)
        {
            dc.DrawLine(pen, From, To);
            return;
        }

        double ux = dx / len, uy = dy / len;
        double head = Math.Min(HeadLength, len);

        // 线画到箭头根部为止。画到顶点的话，粗线会从三角形两侧鼓出来
        var baseCenter = new Point(To.X - ux * head, To.Y - uy * head);
        dc.DrawLine(pen, From, baseCenter);

        double half = head * 0.42;
        double px = -uy, py = ux;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(To, isFilled: true, isClosed: true);
            g.LineTo(new Point(baseCenter.X + px * half, baseCenter.Y + py * half), true, false);
            g.LineTo(new Point(baseCenter.X - px * half, baseCenter.Y - py * half), true, false);
        }
        geometry.Freeze();

        var fill = new SolidColorBrush(Stroke);
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    public override Annotation Translate(double dx, double dy) => new ArrowAnnotation
    {
        From = new Point(From.X + dx, From.Y + dy),
        To = new Point(To.X + dx, To.Y + dy),
        Stroke = Stroke,
        Thickness = Thickness,
    };
}

public sealed class InkAnnotation : Annotation
{
    public required IReadOnlyList<Point> Points { get; init; }

    public override ToolKind Kind => ToolKind.Ink;
    public override Rect Bounds => Inflated(Frame, Thickness);

    public override Rect Frame => Extent(Points);

    public override Annotation WithStyle(AnnotationStyle style) => new InkAnnotation
    {
        Points = Points, Stroke = style.Stroke, Thickness = style.Thickness,
    };

    /// <summary>一串点的外接矩形。涂抹式马赛克也是一串点，共用这一份。</summary>
    internal static Rect Extent(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return Rect.Empty;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>点是否落在这串折线附近。逐段量距离 —— 笔迹的外框往往很大而墨迹很稀。</summary>
    internal static bool NearStroke(IReadOnlyList<Point> points, Point p, double reach)
    {
        if (points.Count == 0) return false;
        if (points.Count == 1) return DistanceToSegment(p, points[0], points[0]) <= reach;

        for (int i = 1; i < points.Count; i++)
            if (DistanceToSegment(p, points[i - 1], points[i]) <= reach) return true;

        return false;
    }

    /// <summary>整条笔迹按外框等比缩放。</summary>
    public override Annotation WithFrame(Rect frame)
    {
        var from = Frame;
        var mapped = new Point[Points.Count];
        for (int i = 0; i < Points.Count; i++)
            mapped[i] = MapInto(Points[i], from, frame);

        return new InkAnnotation { Points = mapped, Stroke = Stroke, Thickness = Thickness };
    }

    public override bool HitTest(Point p, double tolerance) => NearStroke(Points, p, Reach(tolerance));

    public override void Draw(DrawingContext dc, IAnnotationContext context)
    {
        if (Points.Count == 0) return;
        if (Points.Count == 1)
        {
            var dot = new SolidColorBrush(Stroke);
            dot.Freeze();
            dc.DrawEllipse(dot, null, Points[0], Thickness / 2, Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++)
                g.LineTo(Points[i], true, true);
        }
        geometry.Freeze();

        dc.DrawGeometry(null, CreatePen(), geometry);
    }

    public override Annotation Translate(double dx, double dy)
    {
        var moved = new Point[Points.Count];
        for (int i = 0; i < Points.Count; i++)
            moved[i] = new Point(Points[i].X + dx, Points[i].Y + dy);

        return new InkAnnotation { Points = moved, Stroke = Stroke, Thickness = Thickness };
    }
}

public sealed class TextAnnotation : Annotation
{
    private static readonly Typeface Face = new("Microsoft YaHei UI");

    public required Point Origin { get; init; }
    public required string Text { get; init; }
    public required double FontSize { get; init; }
    /// <summary>排版需要它，且它与显示器绑定，所以由外部注入而不是在这里猜。</summary>
    public required double PixelsPerDip { get; init; }

    /// <summary>文字最小可读字号。再小就只剩一团糊，拖过头了也得留条退路。</summary>
    private const double MinFontSize = 6;

    /// <summary>
    /// 文字只给四个角。文字得保持长宽比，边控制点只能改一个方向，
    /// 拖了却看不出变化（或者只是把文字挪走），比不给还让人困惑。
    /// </summary>
    private static readonly int[] Corners =
        [Handles.TopLeft, Handles.TopRight, Handles.BottomLeft, Handles.BottomRight];

    public override ToolKind Kind => ToolKind.Text;

    public override Rect Bounds => Frame;

    public override Rect Frame
    {
        get
        {
            var t = Build();
            return new Rect(Origin.X, Origin.Y, t.Width, t.Height);
        }
    }

    /// <summary>文字的「大小」是字号，Thickness 在这里没有任何视觉含义。</summary>
    public override AnnotationStyle StyleOver(AnnotationStyle fallback)
        => fallback with { Stroke = Stroke, FontSize = FontSize };

    public override Annotation WithStyle(AnnotationStyle style) => new TextAnnotation
    {
        Origin = Origin,
        Text = Text,
        FontSize = Math.Max(MinFontSize, style.FontSize),
        PixelsPerDip = PixelsPerDip,
        Stroke = style.Stroke,
        Thickness = Thickness,
    };

    public override int HandleCount => Corners.Length;

    public override Point HandleAt(int index) => Handles.At(Frame, Corners[index]);

    public override int HandleAxis(int index) => Corners[index];

    public override Annotation DragHandle(int index, Point to)
        => WithFrame(Handles.Resize(Frame, Corners[index], to));

    /// <summary>
    /// 拉伸文字＝改字号。按高度比例缩放，宽度跟着字形自己走 ——
    /// 强行拉宽只会把字挤扁，那不是任何人想要的结果。
    /// </summary>
    public override Annotation WithFrame(Rect frame)
    {
        double height = Frame.Height;
        double scale = height > 0 ? frame.Height / height : 1;

        return new TextAnnotation
        {
            Origin = new Point(frame.X, frame.Y),
            Text = Text,
            FontSize = Math.Max(MinFontSize, FontSize * scale),
            PixelsPerDip = PixelsPerDip,
            Stroke = Stroke,
            Thickness = Thickness,
        };
    }

    public override void Draw(DrawingContext dc, IAnnotationContext context)
        => dc.DrawText(Build(), Origin);

    public override Annotation Translate(double dx, double dy) => new TextAnnotation
    {
        Origin = new Point(Origin.X + dx, Origin.Y + dy),
        Text = Text,
        FontSize = FontSize,
        PixelsPerDip = PixelsPerDip,
        Stroke = Stroke,
        Thickness = Thickness,
    };

    private FormattedText Build()
    {
        var brush = new SolidColorBrush(Stroke);
        brush.Freeze();
        return new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, FontSize, brush, PixelsPerDip);
    }
}

public sealed class MosaicAnnotation : Annotation
{
    public required Rect Area { get; init; }
    public required int Block { get; init; }

    public override ToolKind Kind => ToolKind.Mosaic;
    public override Rect Bounds => Area;
    public override Rect Frame => Area;

    /// <summary>马赛克没有颜色和线宽可言 —— 它取的就是画面本身的颜色。</summary>
    public override AnnotationStyle StyleOver(AnnotationStyle fallback)
        => fallback with { MosaicBlock = Block };

    public override Annotation WithStyle(AnnotationStyle style) => new MosaicAnnotation
    {
        Area = Area, Block = style.MosaicBlock, Stroke = Stroke, Thickness = Thickness,
    };

    public override void Draw(DrawingContext dc, IAnnotationContext context)
        => context.DrawMosaic(dc, Area, Block);

    /// <summary>马赛克是按 Area 现场采样的，换了框就自动去糊新的那一块，不必搬运像素。</summary>
    public override Annotation WithFrame(Rect frame) => new MosaicAnnotation
    {
        Area = frame, Block = Block, Stroke = Stroke, Thickness = Thickness,
    };

    public override Annotation Translate(double dx, double dy) => new MosaicAnnotation
    {
        Area = RectangleAnnotation.Shift(Area, dx, dy),
        Block = Block,
        Stroke = Stroke,
        Thickness = Thickness,
    };
}

/// <summary>
/// 涂抹式马赛克：笔迹经过的地方糊掉。
///
/// 框选马赛克要求先想清楚边界再拖，而实际要盖的往往是散落各处的几个头像、
/// 一串手机号 —— 那种情况下涂过去比框三四个矩形快得多。
///
/// 实现上仍然复用同一套块平均：把笔迹加粗成一条通道当裁剪区，
/// 再在通道的外框上照常铺马赛克块。块的位置是对齐全局网格的，
/// 所以涂出来的边缘会沿着块边界走，而不是一条毛糙的手抖曲线。
/// </summary>
public sealed class MosaicStrokeAnnotation : Annotation
{
    public required IReadOnlyList<Point> Points { get; init; }
    public required int Block { get; init; }

    public override ToolKind Kind => ToolKind.Mosaic;

    public override Rect Bounds => Inflated(Frame, Thickness);

    public override Rect Frame => InkAnnotation.Extent(Points);

    /// <summary>这里的 Thickness 是笔宽而不是描边宽度，所以对应的是笔宽那一档。</summary>
    public override AnnotationStyle StyleOver(AnnotationStyle fallback)
        => fallback with { MosaicBlock = Block, MosaicBrushWidth = Thickness };

    public override Annotation WithStyle(AnnotationStyle style) => new MosaicStrokeAnnotation
    {
        Points = Points,
        Block = style.MosaicBlock,
        Stroke = Stroke,
        Thickness = style.MosaicBrushWidth,
    };

    public override void Draw(DrawingContext dc, IAnnotationContext context)
    {
        var clip = BuildStroke();
        if (clip is null) return;

        dc.PushClip(clip);

        // 逐段铺，而不是对整条笔迹的外框铺一次：斜着涂一道的话，外框面积可以是
        // 笔迹本身的几十倍，那些块最后全被裁掉，白算。块的位置对齐全局网格，
        // 相邻段重叠的部分算出来的颜色完全相同，重复画不会有接缝。
        double reach = Thickness / 2 + Block;
        if (Points.Count == 1)
        {
            context.DrawMosaic(dc, Inflated(new Rect(Points[0], Points[0]), reach), Block);
        }
        else
        {
            for (int i = 1; i < Points.Count; i++)
                context.DrawMosaic(dc, Inflated(new Rect(Points[i - 1], Points[i]), reach), Block);
        }

        dc.Pop();
    }

    /// <summary>把笔迹加粗成一条有面积的通道。点太少时退化成一个圆点。</summary>
    private Geometry? BuildStroke()
    {
        if (Points.Count == 0) return null;

        double radius = Math.Max(1, Thickness / 2);
        if (Points.Count == 1)
        {
            var dot = new EllipseGeometry(Points[0], radius, radius);
            dot.Freeze();
            return dot;
        }

        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++)
                g.LineTo(Points[i], true, true);
        }

        // 圆头圆角，涂抹的拐弯处才不会出现缺口
        var pen = new Pen(Brushes.Black, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        var widened = path.GetWidenedPathGeometry(pen);
        widened.FillRule = FillRule.Nonzero;
        widened.Freeze();
        return widened;
    }

    public override Annotation WithFrame(Rect frame)
    {
        var from = Frame;
        var mapped = new Point[Points.Count];
        for (int i = 0; i < Points.Count; i++)
            mapped[i] = MapInto(Points[i], from, frame);

        return new MosaicStrokeAnnotation
        {
            Points = mapped, Block = Block, Stroke = Stroke, Thickness = Thickness,
        };
    }

    public override bool HitTest(Point p, double tolerance) => InkAnnotation.NearStroke(Points, p, Reach(tolerance));

    public override Annotation Translate(double dx, double dy)
    {
        var moved = new Point[Points.Count];
        for (int i = 0; i < Points.Count; i++)
            moved[i] = new Point(Points[i].X + dx, Points[i].Y + dy);

        return new MosaicStrokeAnnotation
        {
            Points = moved, Block = Block, Stroke = Stroke, Thickness = Thickness,
        };
    }
}
