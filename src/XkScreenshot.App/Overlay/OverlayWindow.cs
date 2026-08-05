using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 单台显示器上的截图覆盖层。多屏时每屏一个实例，共享同一个 CaptureSession。
///
/// 为什么不用一个横跨整个虚拟桌面的大窗口：混合 DPI 下 WPF 只会按窗口所在的
/// 那台显示器做 DIP 换算，跨屏部分会被整体缩放，画出来的选区框和实际像素对不上。
/// 一屏一窗，每个窗口在自己的 DPI 上下文里 1:1 渲染，才是唯一可靠的做法。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly CaptureSession _session;
    private readonly MonitorFrame _frame;
    private readonly SelectionLayer _layer = new();
    private bool _capturing;

    public OverlayWindow(CaptureSession session, MonitorFrame frame)
    {
        _session = session;
        _frame = frame;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // 冻结图是不透明的，用 AllowsTransparency 会强制走软件渲染，白白掉一半帧率
        AllowsTransparency = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var image = new Image
        {
            Source = frame.Image,
            Stretch = Stretch.Fill,
        };
        // 冻结图必须逐像素原样呈现，插值会让文字发虚 —— OCR 阶段尤其致命
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        Content = new Grid { Children = { image, _layer } };

        _session.Changed += OnSessionChanged;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _session.Changed -= OnSessionChanged;
    }

    public MonitorInfo Monitor => _frame.Monitor;

    /// <summary>
    /// 用 SetWindowPos 直接按物理像素定位，绕开 WPF 的 Left/Top/Width/Height（那些是 DIP，
    /// 在窗口尚未归属某台显示器时换算基准是错的，多屏下会摆错位置）。
    /// 用 Bounds 而非 WorkArea 是为了盖住任务栏 —— 任务栏本身也要能截。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var b = _frame.Monitor.Bounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            b.X, b.Y, b.Width, b.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnSessionChanged()
    {
        var bounds = _frame.Monitor.Bounds;

        // 还没框选时，鼠标悬停命中的窗口就是「预备选区」，和已确定的选区同等对待：
        // 一样从遮罩里挖空、一样描边、一样标尺寸。用户在按下鼠标之前就能看清将要截到什么。
        var selection = _session.Selection;
        var highlight = selection.IsEmpty ? _session.HoverWindow : selection;

        if (!highlight.IsEmpty && highlight.IntersectsWith(bounds))
        {
            _layer.HighlightLocal = ToLocalDip(highlight.Intersect(bounds));
            _layer.HighlightPixels = highlight;
            _layer.ShowSizeLabel = OwnsLabelAnchor(highlight);
        }
        else
        {
            _layer.HighlightLocal = null;
            _layer.ShowSizeLabel = false;
        }

        _layer.Refresh();
    }

    /// <summary>
    /// 尺寸标签只画一次：由持有高亮区左上角的那块屏负责，跨屏时才不会出现两个标签。
    /// 左上角恰好落在显示器之间的空隙里时（非矩形排布的多屏会出现），
    /// 退回到「第一块与高亮区相交的屏」兜底，保证有且仅有一块屏认领。
    /// </summary>
    private bool OwnsLabelAnchor(PixelRect highlight)
    {
        var anchor = new PixelPoint(highlight.X, highlight.Y);
        if (_frame.Monitor.Bounds.Contains(anchor)) return true;
        if (_session.Snapshot.Frames.Any(f => f.Monitor.Bounds.Contains(anchor))) return false;

        var fallback = _session.Snapshot.Frames
            .FirstOrDefault(f => f.Monitor.Bounds.IntersectsWith(highlight));
        return fallback?.Monitor.Handle == _frame.Monitor.Handle;
    }

    /// <summary>虚拟屏幕物理像素 → 本窗口局部 DIP。整个项目里唯一做 DPI 换算的地方。</summary>
    private Rect ToLocalDip(PixelRect r)
    {
        var b = _frame.Monitor.Bounds;
        double sx = _frame.Monitor.ScaleX;
        double sy = _frame.Monitor.ScaleY;
        return new Rect((r.X - b.X) / sx, (r.Y - b.Y) / sy, r.Width / sx, r.Height / sy);
    }

    // 鼠标位置一律走 GetCursorPos 取虚拟屏幕物理坐标，而不是 WPF 的 e.GetPosition：
    // 拖拽跨到另一台显示器时，WPF 给的仍是「相对本窗口」的 DIP，且换算用的是本屏 DPI，
    // 跨屏后完全失真。GetCursorPos 永远是全局物理坐标，跨屏天然正确。
    private static PixelPoint Cursor2Pixel() => MonitorEnumerator.GetCursorPosition();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pt = Cursor2Pixel();
        if (_capturing) _session.UpdateDrag(pt);
        else _session.UpdateHover(pt);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 已确定选区后，双击选区内部 = 确认
        if (e.ClickCount == 2 && _session.Phase == SelectionPhase.Settled
            && _session.Selection.Contains(Cursor2Pixel()))
        {
            _session.Confirm();
            return;
        }

        _capturing = CaptureMouse();
        _session.BeginDrag(Cursor2Pixel());
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_capturing) return;

        _capturing = false;
        ReleaseMouseCapture();
        _session.EndDrag(Cursor2Pixel());
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Escape();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;

        switch (e.Key)
        {
            case Key.Escape:
                _session.Escape();
                e.Handled = true;
                break;
            case Key.Enter:
                _session.Confirm();
                e.Handled = true;
                break;
            case Key.Left: _session.NudgeSelection(-step, 0); e.Handled = true; break;
            case Key.Right: _session.NudgeSelection(step, 0); e.Handled = true; break;
            case Key.Up: _session.NudgeSelection(0, -step); e.Handled = true; break;
            case Key.Down: _session.NudgeSelection(0, step); e.Handled = true; break;
        }
    }
}
