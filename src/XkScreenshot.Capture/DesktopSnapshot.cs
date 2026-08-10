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

    /// <summary>
    /// 用一张存档的整屏画面拼一个快照出来，供回溯历史时顶替实时冻屏。
    ///
    /// 按**当前**这几台显示器切帧，而不是按存档时的排布：切完之后每块屏一帧、
    /// 帧的范围就是那块屏的范围，形状和实时冻屏完全一样 —— 于是裁剪、取色、马赛克、
    /// 放大镜、覆盖层定位全都不必知道自己正看着的是历史还是现在。
    ///
    /// 存档的桌面盖不住的地方填黑（换过分辨率、拔掉一台屏之后必然出现）。
    /// 留黑而不是拉伸：拉伸会让「回溯出来的画面」和「当时真正看到的画面」在尺寸上对不上，
    /// 而这个功能的全部价值就在于它就是当时那一屏。
    /// </summary>
    /// <param name="image">存档画面，覆盖 <paramref name="desktop"/> 这块范围。</param>
    /// <param name="desktop">该画面在虚拟屏幕坐标系中的位置与尺寸。</param>
    /// <param name="monitors">当前的显示器。</param>
    public static DesktopSnapshot FromImage(
        BitmapSource image, PixelRect desktop, IReadOnlyList<MonitorInfo> monitors)
    {
        // 统一转成 32bpp 不带 alpha：存档画面本来就是不透明的一屏，
        // 而 CapturedFrame 的契约要的正是「B,G,R 各一字节、第四字节无意义」
        var source = image.Format == PixelFormats.Bgr32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);

        int srcW = source.PixelWidth;
        int srcH = source.PixelHeight;

        var frames = new List<MonitorFrame>(monitors.Count);
        foreach (var monitor in monitors)
            frames.Add(new MonitorFrame(monitor, Slice(source, desktop, srcW, srcH, monitor.Bounds)));

        return new DesktopSnapshot
        {
            Frames = frames,
            // 历史画面里的窗口早就不在了，给不出可以点选的目标。
            // 空列表让悬停高亮自然什么都不给，比拿一份此刻的窗口列表去套一张旧图诚实得多。
            Windows = [],
            VirtualBounds = MonitorEnumerator.VirtualBounds(monitors),
        };
    }

    private static CapturedFrame Slice(
        BitmapSource source, PixelRect desktop, int srcW, int srcH, PixelRect target)
    {
        int stride = target.Width * 4;
        // 零初始化就是不透明黑（Bgr32 没有 alpha 通道），盖不住的地方保持这个样子
        var dst = new byte[stride * target.Height];

        // 存档画面的实际像素范围（存档时的桌面尺寸和图的尺寸理论上一致，
        // 但文件是可以被人动过的，以图为准才不会越界）
        var available = new PixelRect(desktop.X, desktop.Y, Math.Min(desktop.Width, srcW), Math.Min(desktop.Height, srcH));
        var overlap = available.Intersect(target);

        if (!overlap.IsEmpty)
        {
            // 逐行从解码结果里取，不先把整张存档摊成一个 byte[] ——
            // 一张 2K 桌面摊开是 15 MB，而这里真正要的只是它落在本屏上的那一块
            source.CopyPixels(
                new System.Windows.Int32Rect(
                    overlap.X - desktop.X, overlap.Y - desktop.Y, overlap.Width, overlap.Height),
                dst, stride,
                (overlap.Y - target.Y) * stride + (overlap.X - target.X) * 4);
        }

        var bitmap = BitmapSource.Create(
            target.Width, target.Height, 96, 96, PixelFormats.Bgr32, null, dst, stride);
        bitmap.Freeze();

        return new CapturedFrame
        {
            Image = bitmap,
            Bounds = target,
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
