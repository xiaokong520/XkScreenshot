using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using XkScreenshot.App.Overlay;
using XkScreenshot.Capture;

namespace XkScreenshot.App;

/// <summary>
/// 一次截图的完整生命周期：冻屏 → 铺覆盖层 → 收选区与标注 → 出图。
/// 同一时刻只允许有一次会话。
/// </summary>
public sealed class CaptureController
{
    private readonly IScreenCapture _capture;
    private readonly List<OverlayWindow> _overlays = [];
    private CaptureSession? _session;

    public CaptureController(IScreenCapture capture) => _capture = capture;

    public bool IsActive => _session is not null;

    /// <summary>截图完成，参数是烧好标注的成品与用户选的去向。</summary>
    public event Action<CaptureResult>? Captured;

    public void Start()
    {
        if (IsActive) return;

        // 自己的窗口要排除掉，否则上一次残留的覆盖层会成为可选目标
        var own = Application.Current.Windows.OfType<Window>()
            .Select(w => new WindowInteropHelper(w).Handle)
            .Where(h => h != IntPtr.Zero)
            .ToHashSet();

        var snapshot = DesktopSnapshot.Take(_capture, own);
        if (snapshot.Frames.Count == 0) return;

        _session = new CaptureSession(snapshot);
        _session.Confirmed += OnConfirmed;
        _session.Cancelled += Cancel;

        foreach (var frame in snapshot.Frames)
        {
            var overlay = new OverlayWindow(_session, frame);
            _overlays.Add(overlay);
            overlay.Show();
        }

        // 光标所在那块屏才激活，键盘事件才有着落；其余的只是铺满不抢焦点
        var cursor = Core.Monitors.MonitorEnumerator.GetCursorPosition();
        var active = _overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cursor))
                     ?? _overlays[0];
        active.Activate();
        active.Focus();

        _session.UpdateCursor(cursor);
        _session.UpdateHover(cursor);
    }

    /// <summary>
    /// 成品位图在 Confirm 时就已经渲染好了，所以这里可以先收掉覆盖层再派发 ——
    /// 覆盖层必须在贴图窗口出现之前消失，否则新贴图会被压在它下面。
    /// </summary>
    private void OnConfirmed(CaptureResult result)
    {
        Teardown();
        Captured?.Invoke(result);
    }

    public void Cancel() => Teardown();

    private void Teardown()
    {
        if (_session is not null)
        {
            _session.Confirmed -= OnConfirmed;
            _session.Cancelled -= Cancel;
            _session = null;
        }

        foreach (var overlay in _overlays)
            overlay.Close();
        _overlays.Clear();
    }
}
