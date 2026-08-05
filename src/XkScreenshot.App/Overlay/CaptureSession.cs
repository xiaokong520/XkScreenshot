using System;
using System.Collections.Generic;
using System.Linq;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Windows;

namespace XkScreenshot.App.Overlay;

public enum SelectionPhase
{
    /// <summary>还没框选，鼠标悬停哪个窗口就高亮哪个。</summary>
    Idle,
    /// <summary>正在拖拽。</summary>
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

    private PixelPoint _anchor;

    public CaptureSession(DesktopSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public DesktopSnapshot Snapshot { get; }
    public SelectionPhase Phase { get; private set; } = SelectionPhase.Idle;
    public PixelRect Selection { get; private set; } = PixelRect.Empty;
    public PixelRect HoverWindow { get; private set; } = PixelRect.Empty;

    public event Action? Changed;
    /// <summary>用户确认了选区。</summary>
    public event Action<PixelRect>? Confirmed;
    /// <summary>用户放弃了本次截图。</summary>
    public event Action? Cancelled;

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

    public void BeginDrag(PixelPoint cursor)
    {
        _anchor = cursor;
        Phase = SelectionPhase.Dragging;
        Selection = PixelRect.Empty;
        Changed?.Invoke();
    }

    public void UpdateDrag(PixelPoint cursor)
    {
        if (Phase != SelectionPhase.Dragging) return;
        Selection = PixelRect.FromPoints(_anchor, cursor).Intersect(Snapshot.VirtualBounds);
        Changed?.Invoke();
    }

    public void EndDrag(PixelPoint cursor)
    {
        if (Phase != SelectionPhase.Dragging) return;

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
        if (Phase == SelectionPhase.Settled && !Selection.IsEmpty)
        {
            Phase = SelectionPhase.Idle;
            Selection = PixelRect.Empty;
            Changed?.Invoke();
            return;
        }
        Cancelled?.Invoke();
    }

    /// <summary>方向键微调选区边界，按住 Shift 时步长放大。</summary>
    public void NudgeSelection(int dx, int dy)
    {
        if (Selection.IsEmpty) return;
        Selection = Selection.Offset(dx, dy).ClampInto(Snapshot.VirtualBounds);
        Changed?.Invoke();
    }
}
