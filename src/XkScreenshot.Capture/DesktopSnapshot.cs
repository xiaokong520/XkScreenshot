using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Windows;

namespace XkScreenshot.Capture;

/// <summary>某台显示器的冻结画面。</summary>
public sealed record MonitorFrame(MonitorInfo Monitor, CapturedFrame Frame)
{
    public BitmapSource Image => Frame.Image;
}

/// <summary>
/// 「冻屏」快照：按下热键那一瞬间的整个桌面。
///
/// 之所以先冻结再显示覆盖层，而不是做一个透明窗口盖在实时桌面上：
///   1. 实时方案会抓到自己的覆盖层，且点击穿透在多屏下坑极多；
///   2. 冻屏能截到已经打开的右键菜单 —— 覆盖层一抢焦点菜单就关了，
///      但那时菜单已经在这张位图里了；
///   3. 选区交互全部作用在静态位图上，天然不掉帧。
/// </summary>
public sealed class DesktopSnapshot
{
    public required IReadOnlyList<MonitorFrame> Frames { get; init; }
    public required IReadOnlyList<CapturedWindow> Windows { get; init; }
    public required PixelRect VirtualBounds { get; init; }

    /// <param name="excludeWindows">自己的窗口句柄，避免把上一次的覆盖层算进候选目标。</param>
    public static DesktopSnapshot Take(IScreenCapture capture, IReadOnlyCollection<IntPtr> excludeWindows)
    {
        // 顺序很关键：窗口列表必须在覆盖层出现之前枚举，否则 z-order 被自己污染。
        var windows = WindowEnumerator.Enumerate(excludeWindows);
        var monitors = MonitorEnumerator.Enumerate();

        var frames = new List<MonitorFrame>(monitors.Count);
        foreach (var m in monitors)
            frames.Add(new MonitorFrame(m, capture.CaptureRect(m.Bounds, m.DpiX, m.DpiY)));

        return new DesktopSnapshot
        {
            Frames = frames,
            Windows = windows,
            VirtualBounds = MonitorEnumerator.VirtualBounds(monitors),
        };
    }

    /// <summary>取包含该点的那台显示器的冻结帧；点落在显示器之间的空隙里时返回 null。</summary>
    public CapturedFrame? FrameAt(PixelPoint point)
        => Frames.FirstOrDefault(f => f.Monitor.Bounds.Contains(point))?.Frame;

    /// <summary>
    /// 从冻结画面里裁一块出来。跨显示器的选区会被逐屏拼接，
    /// 混合 DPI 时以「物理像素 1:1」为准拼，不做缩放对齐。
    /// </summary>
    public BitmapSource Crop(PixelRect selection)
    {
        if (selection.IsEmpty)
            throw new ArgumentException("选区为空", nameof(selection));

        var hits = Frames
            .Select(f => (Frame: f, Part: f.Monitor.Bounds.Intersect(selection)))
            .Where(t => !t.Part.IsEmpty)
            .ToList();

        if (hits.Count == 0)
            throw new InvalidOperationException("选区不在任何显示器上");

        // 单屏是绝大多数情况，走快路径，避免多余的一次合成
        if (hits.Count == 1)
        {
            var (frame, part) = hits[0];
            var local = part.Offset(-frame.Monitor.Bounds.X, -frame.Monitor.Bounds.Y);
            var cropped = new CroppedBitmap(frame.Image,
                new System.Windows.Int32Rect(local.X, local.Y, local.Width, local.Height));
            cropped.Freeze();
            return cropped;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var (frame, part) in hits)
            {
                var local = part.Offset(-frame.Monitor.Bounds.X, -frame.Monitor.Bounds.Y);
                var piece = new CroppedBitmap(frame.Image,
                    new System.Windows.Int32Rect(local.X, local.Y, local.Width, local.Height));
                var dest = new System.Windows.Rect(
                    part.X - selection.X, part.Y - selection.Y, part.Width, part.Height);
                dc.DrawImage(piece, dest);
            }
        }

        // 目标位图统一按 96 DPI 落地：拼出来的是纯像素数据，不再归属任何一台显示器
        var target = new RenderTargetBitmap(
            selection.Width, selection.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
