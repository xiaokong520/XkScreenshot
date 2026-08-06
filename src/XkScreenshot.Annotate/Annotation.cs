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

    public override Rect Bounds => Inflated(Area, Thickness);
    public override Rect Frame => Area;

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

    public override Rect Bounds => Inflated(Area, Thickness);
    public override Rect Frame => Area;

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

    public override Rect Bounds => Inflated(new Rect(From, To), HeadLength);
    public override Rect Frame => new(From, To);

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

    public override Rect Bounds => Inflated(Frame, Thickness);

    public override Rect Frame
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in Points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
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

    /// <summary>逐段量距离。笔迹的外框往往很大而墨迹很稀，用外框会点哪儿都命中。</summary>
    public override bool HitTest(Point p, double tolerance)
    {
        if (Points.Count == 0) return false;
        if (Points.Count == 1) return DistanceToSegment(p, Points[0], Points[0]) <= Reach(tolerance);

        double reach = Reach(tolerance);
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= reach) return true;

        return false;
    }

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

    public override Rect Bounds => Frame;

    public override Rect Frame
    {
        get
        {
            var t = Build();
            return new Rect(Origin.X, Origin.Y, t.Width, t.Height);
        }
    }

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

    public override Rect Bounds => Area;
    public override Rect Frame => Area;

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
