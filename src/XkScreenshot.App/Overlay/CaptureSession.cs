using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
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

    /// <summary>用户确认了选区。</summary>
    public event Action<PixelRect>? Confirmed;
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

    public void Confirm()
    {
        if (Selection.IsEmpty) return;
        Confirmed?.Invoke(Selection);
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
