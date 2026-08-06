using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Annotate;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 把「按下-拖动-抬起」翻译成一个标注。
///
/// 坐标一律是选区局部物理像素，和 AnnotationDocument 一致；
/// 屏幕坐标到选区局部的换算由调用方在入口处做掉，这里完全不碰显示器和 DPI。
/// </summary>
public sealed class AnnotationController
{
    /// <summary>拖拽距离小于它就当作没画，避免误点在图上留下一个小点。</summary>
    private const double MinDragPx = 3;

    private readonly List<Point> _inkPoints = [];
    private Point _anchor;
    private bool _active;

    public required AnnotationDocument Document { get; init; }
    public ToolKind Tool { get; set; } = ToolKind.None;
    public Color Stroke { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public double Thickness { get; set; } = 3;
    public double FontSize { get; set; } = 20;
    public double PixelsPerDip { get; set; } = 1;
    /// <summary>马赛克块边长（物理像素）。</summary>
    public int MosaicBlock { get; set; } = 12;

    /// <summary>马赛克是框一块还是涂抹。</summary>
    public MosaicStyle MosaicStyle { get; set; } = MosaicStyle.Area;

    /// <summary>涂抹式马赛克的笔宽。</summary>
    public double MosaicBrushWidth { get; set; } = 30;

    /// <summary>正在拖拽、尚未提交的标注，用于实时预览。</summary>
    public Annotation? Preview { get; private set; }

    public bool IsDrawing => _active;

    /// <summary>当前工具是否靠拖拽产生标注。文字工具是点击定位，不走拖拽。</summary>
    public bool IsDragTool => Tool is ToolKind.Rectangle or ToolKind.Ellipse
        or ToolKind.Arrow or ToolKind.Ink or ToolKind.Mosaic;

    public void Begin(Point local)
    {
        if (!IsDragTool) return;

        _anchor = local;
        _active = true;
        _inkPoints.Clear();
        _inkPoints.Add(local);
        Preview = null;
    }

    /// <summary>靠一串点而不是起止两点成形的工具。</summary>
    private bool IsStrokeTool => Tool == ToolKind.Ink
                                 || (Tool == ToolKind.Mosaic && MosaicStyle == MosaicStyle.Brush);

    public void Update(Point local)
    {
        if (!_active) return;

        if (IsStrokeTool)
        {
            // 相邻点太密只会让路径数据膨胀，抽稀一下，视觉上完全看不出差别
            var last = _inkPoints[^1];
            if (Math.Abs(local.X - last.X) + Math.Abs(local.Y - last.Y) >= 1.5)
                _inkPoints.Add(local);
        }

        Preview = Build(local);
    }

    /// <summary>结束拖拽。返回 true 表示确实产生了一个标注。</summary>
    public bool End(Point local)
    {
        if (!_active) return false;
        _active = false;

        var shape = Build(local);
        Preview = null;

        // 涂抹类工具原地点一下就该留下一个点，不能按「拖得太短」丢掉
        bool tooSmall = !IsStrokeTool
                        && Math.Abs(local.X - _anchor.X) < MinDragPx
                        && Math.Abs(local.Y - _anchor.Y) < MinDragPx;
        if (shape is null || tooSmall) return false;

        Document.Add(shape);
        return true;
    }

    public void Cancel()
    {
        _active = false;
        Preview = null;
        _inkPoints.Clear();
    }

    /// <summary>提交一条文字标注。空串直接忽略。</summary>
    public bool CommitText(Point local, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        Document.Add(new TextAnnotation
        {
            Origin = local,
            Text = text,
            FontSize = FontSize,
            PixelsPerDip = PixelsPerDip,
            Stroke = Stroke,
            Thickness = Thickness,
        });
        return true;
    }

    private Annotation? Build(Point local)
    {
        var area = new Rect(_anchor, local);

        return Tool switch
        {
            ToolKind.Rectangle => new RectangleAnnotation
            {
                Area = area, Stroke = Stroke, Thickness = Thickness,
            },
            ToolKind.Ellipse => new EllipseAnnotation
            {
                Area = area, Stroke = Stroke, Thickness = Thickness,
            },
            ToolKind.Arrow => new ArrowAnnotation
            {
                From = _anchor, To = local, Stroke = Stroke, Thickness = Thickness,
            },
            ToolKind.Ink => new InkAnnotation
            {
                Points = _inkPoints.ToArray(), Stroke = Stroke, Thickness = Thickness,
            },
            ToolKind.Mosaic when MosaicStyle == MosaicStyle.Brush => new MosaicStrokeAnnotation
            {
                Points = _inkPoints.ToArray(),
                Block = MosaicBlock,
                Stroke = Stroke,
                // 马赛克不描边，Thickness 在这里的含义是笔宽
                Thickness = MosaicBrushWidth,
            },
            ToolKind.Mosaic => new MosaicAnnotation
            {
                Area = area, Block = MosaicBlock, Stroke = Stroke, Thickness = Thickness,
            },
            _ => null,
        };
    }
}
