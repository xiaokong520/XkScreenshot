using System.Windows;
using System.Windows.Media;
using XkScreenshot.Annotate;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 画标注的那一层。
///
/// 标注文档里的坐标是「选区局部物理像素」，而 WPF 绘制用的是本窗口的 DIP，
/// 两者之间的换算全部压在这一个 PushTransform 里 —— 各个 Annotation 实现
/// 因此完全不需要知道显示器、DPI、选区位置的存在。
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF)));
    private static readonly Pen HandlePen = Freeze(new Pen(AccentBrush, 1.0));
    private static readonly Pen FramePen = Freeze(new Pen(AccentBrush, 1.0)
    {
        DashStyle = new DashStyle([4, 3], 0),
    });

    /// <summary>
    /// 控制点边长（DIP）。取值等于命中半径，好让「画出来多大」和「点得着多大」
    /// 用的是同一个数 —— 这两者一旦分家，判定就会开始和眼睛看到的对不上。
    /// </summary>
    private const double HandleSize = CaptureSession.DefaultHandleTolerance;

    public AnnotationLayer() => IsHitTestVisible = false;

    public AnnotationDocument? Document { get; set; }
    public IAnnotationContext? Context { get; set; }

    /// <summary>正在拖拽、尚未提交的那一个标注。</summary>
    public Annotation? Preview { get; set; }

    /// <summary>当前选中的标注，要给它画虚线框和控制点；null 表示没有选中。</summary>
    public Annotation? Selected { get; set; }

    /// <summary>选区在虚拟屏幕上的物理矩形。</summary>
    public PixelRect Selection { get; set; }

    /// <summary>本显示器原点（虚拟屏幕物理坐标）与缩放倍率。</summary>
    public PixelPoint MonitorOrigin { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (Document is null || Context is null || Selection.IsEmpty) return;
        if (Document.IsEmpty && Preview is null) return;

        DrawContent(dc, Document, Context);

        // 控制点画在裁剪和缩放之外：它是操作界面而不是画面内容 ——
        // 既不该被选区裁掉（图形拖出界了正需要靠它拖回来），
        // 也不该跟着 DPI 缩放变成高分屏上一个点不到三像素的小方块。
        if (Selected is { } selected) DrawAdorner(dc, selected);
    }

    private void DrawContent(DrawingContext dc, AnnotationDocument document, IAnnotationContext context)
    {
        // 裁进选区。标注是可以画到选区外的（拖拽时手一抖就出去了），
        // 但截出来的图只有选区那一块，画面上就不该看到超出的部分 —— 否则所见非所得。
        var clip = new RectangleGeometry(new Rect(
            (Selection.X - MonitorOrigin.X) / ScaleX,
            (Selection.Y - MonitorOrigin.Y) / ScaleY,
            Selection.Width / ScaleX,
            Selection.Height / ScaleY));
        clip.Freeze();
        dc.PushClip(clip);

        // 后 push 的先作用：先把选区局部坐标平移到显示器局部，再整体除以缩放倍率
        dc.PushTransform(new ScaleTransform(1 / ScaleX, 1 / ScaleY));
        dc.PushTransform(new TranslateTransform(
            Selection.X - MonitorOrigin.X,
            Selection.Y - MonitorOrigin.Y));

        document.Draw(dc, context);
        Preview?.Draw(dc, context);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawAdorner(DrawingContext dc, Annotation item)
    {
        // 只有两个端点的标注（箭头）画虚线框没有意义：那个框跟图形本身对不上，
        // 反而像是又多了一个可以拉的东西。
        if (item.HandleCount > 2)
        {
            var frame = ToDip(item.Frame);
            if (frame.Width > 0 && frame.Height > 0)
                dc.DrawRectangle(null, FramePen, frame);
        }

        // 判据跟命中测试用同一个，画得出来的就一定抓得着
        if (!CaptureSession.HandlesUsable(item, HandleSize * ScaleX)) return;

        double half = HandleSize / 2;
        for (int i = 0; i < item.HandleCount; i++)
        {
            var p = ToDip(item.HandleAt(i));
            dc.DrawRectangle(Brushes.White, HandlePen,
                new Rect(p.X - half, p.Y - half, HandleSize, HandleSize));
        }
    }

    /// <summary>选区局部物理像素 → 本窗口 DIP。</summary>
    private Point ToDip(Point p) => new(
        (Selection.X - MonitorOrigin.X + p.X) / ScaleX,
        (Selection.Y - MonitorOrigin.Y + p.Y) / ScaleY);

    private Rect ToDip(Rect r)
    {
        var origin = ToDip(r.TopLeft);
        return new Rect(origin.X, origin.Y, r.Width / ScaleX, r.Height / ScaleY);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
