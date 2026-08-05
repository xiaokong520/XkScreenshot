using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

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

    /// <summary>该标注影响到的区域，用于局部重绘与命中测试。</summary>
    public abstract Rect Bounds { get; }

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

    public override Rect Bounds => Inflate(Area, Thickness);

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

    internal static Rect Inflate(Rect r, double by)
    {
        var result = r;
        result.Inflate(by, by);
        return result;
    }

    internal static Rect Shift(Rect r, double dx, double dy)
        => new(r.X + dx, r.Y + dy, r.Width, r.Height);
}

public sealed class EllipseAnnotation : Annotation
{
    public required Rect Area { get; init; }

    public override Rect Bounds => RectangleAnnotation.Inflate(Area, Thickness);

    public override void Draw(DrawingContext dc, IAnnotationContext context)
        => dc.DrawEllipse(null, CreatePen(),
            new Point(Area.X + Area.Width / 2, Area.Y + Area.Height / 2),
            Area.Width / 2, Area.Height / 2);

    public override Annotation Translate(double dx, double dy) => new EllipseAnnotation
    {
        Area = RectangleAnnotation.Shift(Area, dx, dy), Stroke = Stroke, Thickness = Thickness,
    };
}

public sealed class ArrowAnnotation : Annotation
{
    public required Point From { get; init; }
    public required Point To { get; init; }

    /// <summary>箭头大小跟着线宽走，细线配大箭头会很怪。</summary>
    private double HeadLength => Math.Max(10, Thickness * 4.5);

    public override Rect Bounds
        => RectangleAnnotation.Inflate(new Rect(From, To), HeadLength);

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

    public override Rect Bounds
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
            return RectangleAnnotation.Inflate(new Rect(minX, minY, maxX - minX, maxY - minY), Thickness);
        }
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

    public override Rect Bounds
    {
        get
        {
            var t = Build();
            return new Rect(Origin.X, Origin.Y, t.Width, t.Height);
        }
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

    public override void Draw(DrawingContext dc, IAnnotationContext context)
        => context.DrawMosaic(dc, Area, Block);

    public override Annotation Translate(double dx, double dy) => new MosaicAnnotation
    {
        Area = RectangleAnnotation.Shift(Area, dx, dy),
        Block = Block,
        Stroke = Stroke,
        Thickness = Thickness,
    };
}
