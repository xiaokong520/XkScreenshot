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
    public AnnotationLayer() => IsHitTestVisible = false;

    public AnnotationDocument? Document { get; set; }
    public IAnnotationContext? Context { get; set; }

    /// <summary>正在拖拽、尚未提交的那一个标注。</summary>
    public Annotation? Preview { get; set; }

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

        Document.Draw(dc, Context);
        Preview?.Draw(dc, Context);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }
}
