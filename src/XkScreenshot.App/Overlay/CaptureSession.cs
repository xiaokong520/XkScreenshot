using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Annotate;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Windows;

namespace XkScreenshot.App.Overlay;

public enum ColorFormat
{
    Rgb,
    Hex,
}

/// <summary>确认截图时用户选的去向。</summary>
public enum CaptureAction
{
    Copy,
    Pin,
    Save,
}

/// <summary>一次截图的成品：已烧好标注的位图，以及它在屏幕上的原始位置（贴图要用）。</summary>
public sealed record CaptureResult(BitmapSource Image, PixelRect Bounds, CaptureAction Action);

public enum SelectionPhase
{
    /// <summary>还没框选，鼠标悬停哪个窗口就高亮哪个。</summary>
    Idle,
    /// <summary>正在拉出一个新选区。</summary>
    Dragging,
    /// <summary>选区已确定，等待用户确认或调整。</summary>
    Settled,
}

/// <summary>
/// 一次截图交互的共享状态。多台显示器上的覆盖层窗口全部指向同一个 Session，
/// 选区用「虚拟屏幕物理像素」表示 —— 这样跨屏拖拽天然成立，
/// 每个覆盖层只负责把选区跟自己的 Bounds 求交后画出来。
/// </summary>
public sealed class CaptureSession
{
    /// <summary>小于这个距离视为「点击」而不是「拖拽」，用来触发窗口整窗选取。</summary>
    private const int ClickThresholdPx = 4;

    /// <summary>一次按下-移动-抬起到底在干什么。</summary>
    private enum PressKind
    {
        None,
        /// <summary>拉出一个新选区。</summary>
        Selecting,
        /// <summary>整体平移已有的选区。</summary>
        Moving,
    }

    private PressKind _press = PressKind.None;
    private PixelPoint _anchor;
    private PixelRect _moveOrigin;

    public CaptureSession(DesktopSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public DesktopSnapshot Snapshot { get; }

    /// <summary>本次截图的标注。坐标是选区局部物理像素。</summary>
    public AnnotationDocument Annotations { get; } = new();

    public ToolKind ActiveTool { get; private set; } = ToolKind.None;
    public int ColorIndex { get; private set; }
    public Color StrokeColor => ToolbarLayer.Palette[ColorIndex];

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Idle;
    public PixelRect Selection { get; private set; } = PixelRect.Empty;
    public PixelRect HoverWindow { get; private set; } = PixelRect.Empty;

    /// <summary>光标当前所在的虚拟屏幕物理坐标。</summary>
    public PixelPoint Cursor { get; private set; }

    /// <summary>光标下那一个像素的颜色（取自冻结帧，非预乘，精确到位）。</summary>
    public Color CursorColor { get; private set; }

    /// <summary>光标是否落在某台显示器上（多屏非矩形排布时可能落在空隙里）。</summary>
    public bool CursorOnScreen { get; private set; }

    public ColorFormat ColorFormat { get; private set; } = ColorFormat.Rgb;

    public bool ShowHints { get; private set; } = true;

    /// <summary>选区/悬停变化 —— 低频，会触发所有覆盖层重绘遮罩层。</summary>
    public event Action? Changed;

    /// <summary>
    /// 光标移动 —— 高频。单独一个事件是为了让放大镜层自己重绘，
    /// 不必带着遮罩层、控制点、尺寸标签一起重算。
    /// </summary>
    public event Action? CursorMoved;

    /// <summary>用户确认了截图，附带已经烧好标注的成品位图。</summary>
    public event Action<CaptureResult>? Confirmed;
    /// <summary>用户放弃了本次截图。</summary>
    public event Action? Cancelled;

    public void UpdateCursor(PixelPoint cursor)
    {
        Cursor = cursor;

        var frame = Snapshot.FrameAt(cursor);
        if (frame is not null && frame.TryGetColor(cursor, out var color))
        {
            CursorOnScreen = true;
            CursorColor = color;
        }
        else
        {
            CursorOnScreen = false;
            CursorColor = default;
        }

        CursorMoved?.Invoke();
    }

    public void ToggleColorFormat()
    {
        ColorFormat = ColorFormat == ColorFormat.Rgb ? ColorFormat.Hex : ColorFormat.Rgb;
        CursorMoved?.Invoke();
    }

    public void ToggleHints()
    {
        ShowHints = !ShowHints;
        Changed?.Invoke();
    }

    /// <summary>选同一个工具再点一次等于取消选择，回到「拖拽=移动选区」。</summary>
    public void SetTool(ToolKind tool)
    {
        ActiveTool = ActiveTool == tool ? ToolKind.None : tool;
        Changed?.Invoke();
    }

    public void SetColorIndex(int index)
    {
        if (index < 0 || index >= ToolbarLayer.Palette.Length) return;
        ColorIndex = index;
        Changed?.Invoke();
    }

    private PixelRect _mosaicKey = PixelRect.Empty;
    private MosaicSource? _mosaicSource;

    /// <summary>
    /// 马赛克要读选区内的原始像素。裁剪结果按选区缓存 ——
    /// 选区被平移后必须重裁，否则马赛克会取到旧位置的内容。
    /// </summary>
    public IAnnotationContext MosaicContext()
    {
        if (_mosaicSource is not null && _mosaicKey == Selection) return _mosaicSource;

        var cropped = Snapshot.Crop(Selection);
        // 统一转成非预乘 BGRA：块平均必须在非预乘数据上算，否则半透明像素会偏色
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        _mosaicKey = Selection;
        _mosaicSource = new MosaicSource(buffer, converted.PixelWidth, converted.PixelHeight, stride);
        return _mosaicSource;
    }

    /// <summary>按当前格式把光标处的颜色格式化成可复制的文本。</summary>
    public string FormatCursorColor() => ColorFormat switch
    {
        ColorFormat.Hex => string.Format(CultureInfo.InvariantCulture,
            "#{0:X2}{1:X2}{2:X2}", CursorColor.R, CursorColor.G, CursorColor.B),
        _ => string.Format(CultureInfo.InvariantCulture,
            "{0}, {1}, {2}", CursorColor.R, CursorColor.G, CursorColor.B),
    };

    /// <summary>
    /// 选中一整块屏幕。第一次取光标所在的那台显示器，
    /// 已经是单屏全屏时再按一次扩展到整个虚拟桌面。
    /// </summary>
    public void SelectWholeScreen()
    {
        var monitor = Snapshot.Frames.FirstOrDefault(f => f.Monitor.Bounds.Contains(Cursor))?.Monitor
                      ?? Snapshot.Frames[0].Monitor;

        Selection = Selection == monitor.Bounds ? Snapshot.VirtualBounds : monitor.Bounds;
        Phase = SelectionPhase.Settled;
        HoverWindow = PixelRect.Empty;
        _press = PressKind.None;
        Changed?.Invoke();
    }

    public void UpdateHover(PixelPoint cursor)
    {
        if (Phase != SelectionPhase.Idle) return;

        var hit = WindowEnumerator.HitTest(Snapshot.Windows, cursor);
        var rect = hit is null
            ? PixelRect.Empty
            : hit.Bounds.Intersect(Snapshot.VirtualBounds);

        if (rect == HoverWindow) return;
        HoverWindow = rect;
        Changed?.Invoke();
    }

    /// <summary>
    /// 鼠标按下。
    ///
    /// 关键：选区已确定后，在选区**内部**按下绝不能开始一次新的框选。
    /// 那样的话双击确认会自毁 —— 双击的消息序列是 Down(1)/Up/Down(2)/Up，
    /// 第一次 Down 会清空选区，紧接着的 Up 因为光标没动而被判成单击，
    /// 于是选区被替换成光标下那个窗口，等第二次 Down 真的触发确认时，
    /// 截下来的已经是整个窗口而不是用户辛苦拖出来的区域了。
    /// 在选区内按下的正确语义是「准备平移选区」（或只是即将双击）。
    /// </summary>
    public void BeginPress(PixelPoint cursor)
    {
        _anchor = cursor;

        if (Phase == SelectionPhase.Settled && Selection.Contains(cursor))
        {
            _press = PressKind.Moving;
            _moveOrigin = Selection;
            return; // 选区原样保留，什么都还没变，不必通知重绘
        }

        _press = PressKind.Selecting;
        Phase = SelectionPhase.Dragging;
        Selection = PixelRect.Empty;
        Changed?.Invoke();
    }

    public void UpdatePress(PixelPoint cursor)
    {
        switch (_press)
        {
            case PressKind.Selecting:
                Selection = PixelRect.FromPoints(_anchor, cursor).Intersect(Snapshot.VirtualBounds);
                break;

            case PressKind.Moving:
                var delta = cursor - _anchor;
                Selection = _moveOrigin.Offset(delta.X, delta.Y).ClampInto(Snapshot.VirtualBounds);
                break;

            default:
                return;
        }
        Changed?.Invoke();
    }

    public void EndPress(PixelPoint cursor)
    {
        switch (_press)
        {
            case PressKind.Selecting:
                // 没拖动就是单击：直接选中光标下的整个窗口
                if (cursor.ManhattanTo(_anchor) <= ClickThresholdPx)
                {
                    var hit = WindowEnumerator.HitTest(Snapshot.Windows, cursor);
                    Selection = hit is null
                        ? PixelRect.Empty
                        : hit.Bounds.Intersect(Snapshot.VirtualBounds);
                }
                else
                {
                    Selection = PixelRect.FromPoints(_anchor, cursor).Intersect(Snapshot.VirtualBounds);
                }

                if (Selection.IsEmpty)
                {
                    Phase = SelectionPhase.Idle;
                }
                else
                {
                    Phase = SelectionPhase.Settled;
                    HoverWindow = PixelRect.Empty;
                }
                break;

            case PressKind.Moving:
                Phase = SelectionPhase.Settled;
                break;

            default:
                return;
        }

        _press = PressKind.None;
        Changed?.Invoke();
    }

    public void Confirm(CaptureAction action = CaptureAction.Copy)
    {
        if (Selection.IsEmpty) return;
        Confirmed?.Invoke(new CaptureResult(RenderResult(), Selection, action));
    }

    /// <summary>
    /// 把选区连同标注一起烧成最终位图。
    /// 标注坐标本来就是选区局部像素，所以这里不需要任何平移或缩放。
    /// </summary>
    public BitmapSource RenderResult()
    {
        var image = Snapshot.Crop(Selection);
        if (Annotations.IsEmpty) return image;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, Selection.Width, Selection.Height));
            Annotations.Draw(dc, MosaicContext());
        }

        var target = new RenderTargetBitmap(
            Selection.Width, Selection.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// Esc 的两段式行为：已有选区时先退回重选，没有选区才真正退出。
    /// 直接退出会让「框歪了想重来」变成「重新按一次热键」，很烦。
    /// </summary>
    public void Escape()
    {
        _press = PressKind.None;

        if (Phase == SelectionPhase.Settled && !Selection.IsEmpty)
        {
            Phase = SelectionPhase.Idle;
            Selection = PixelRect.Empty;
            Changed?.Invoke();
            return;
        }
        Cancelled?.Invoke();
    }

    /// <summary>方向键微调选区位置，按住 Shift 时步长放大。</summary>
    public void NudgeSelection(int dx, int dy)
    {
        if (Selection.IsEmpty) return;
        Selection = Selection.Offset(dx, dy).ClampInto(Snapshot.VirtualBounds);
        Changed?.Invoke();
    }
}
