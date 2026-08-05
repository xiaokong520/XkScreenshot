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
    private readonly SelectionLayer _selectionLayer = new();
    private readonly MagnifierLayer _magnifierLayer = new();
    private readonly HintLayer _hintLayer = new();
    private readonly FrostedBackdrop _backdrop;
    private bool _capturing;
    private bool _magnifierWasVisible;
    private bool _shiftConsumed;

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
        // Tab 要留给「切换检测模式」，不能被 WPF 拿去做焦点跳转
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);

        var image = new Image
        {
            Source = frame.Image,
            Stretch = Stretch.Fill,
        };
        // 冻结图必须逐像素原样呈现，插值会让文字发虚 —— OCR 阶段尤其致命
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        // 毛玻璃背景两层共用一份：模糊一次全屏画面就够了，没必要各算各的
        var backdrop = new FrostedBackdrop(frame.Frame);
        _magnifierLayer.Frame = frame.Frame;
        _magnifierLayer.Backdrop = backdrop;
        _hintLayer.Backdrop = backdrop;
        _backdrop = backdrop;

        Content = new Grid
        {
            Children = { image, _selectionLayer, _hintLayer, _magnifierLayer },
        };

        _session.Changed += OnSessionChanged;
        _session.CursorMoved += OnCursorMoved;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _session.Changed -= OnSessionChanged;
            _session.CursorMoved -= OnCursorMoved;
        };
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
            _selectionLayer.HighlightLocal = ToLocalDip(highlight.Intersect(bounds));
            _selectionLayer.HighlightPixels = highlight;
            _selectionLayer.ShowSizeLabel = OwnsLabelAnchor(highlight);
        }
        else
        {
            _selectionLayer.HighlightLocal = null;
            _selectionLayer.ShowSizeLabel = false;
        }

        _selectionLayer.Refresh();
        UpdateHintVisibility();
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

    /// <summary>
    /// 光标移动是高频事件，而多屏时只有一块屏需要画放大镜。
    /// 其余的屏在「上一帧也没画」时直接跳过重绘，省掉大量无效的 InvalidateVisual。
    /// </summary>
    private void OnCursorMoved()
    {
        // 选区确定后放大镜依然保留：这时候用户往往正要核对选区边界是否压住了想要的那一行像素，
        // 或者只是想再取一个颜色 —— 恰恰是最需要它的时候。
        bool onThisMonitor = _frame.Monitor.Bounds.Contains(_session.Cursor)
                             && _session.CursorOnScreen;

        if (!onThisMonitor && !_magnifierWasVisible) return;
        _magnifierWasVisible = onThisMonitor;

        if (onThisMonitor)
        {
            _magnifierLayer.CursorPixel = _session.Cursor;
            _magnifierLayer.CursorLocal = ToLocalDipPoint(_session.Cursor);
            _magnifierLayer.Color = _session.CursorColor;
            _magnifierLayer.Format = _session.ColorFormat;
        }
        else
        {
            _magnifierLayer.CursorPixel = null;
        }

        _magnifierLayer.Refresh();
        UpdateHintVisibility();
    }

    private void UpdateHintVisibility()
    {
        // 提示面板只在光标所在那块屏显示。选区确定后照样留着 ——
        // Enter / Esc / 方向键这些恰恰是那之后才会用到的。
        bool visible = _session.ShowHints
                       && _frame.Monitor.Bounds.Contains(_session.Cursor);

        if (visible == _hintLayer.Visible) return;
        _hintLayer.Visible = visible;
        _hintLayer.Refresh();
    }

    /// <summary>虚拟屏幕物理像素 → 本窗口局部 DIP。整个项目里唯一做 DPI 换算的地方。</summary>
    private Rect ToLocalDip(PixelRect r)
    {
        var b = _frame.Monitor.Bounds;
        double sx = _frame.Monitor.ScaleX;
        double sy = _frame.Monitor.ScaleY;
        return new Rect((r.X - b.X) / sx, (r.Y - b.Y) / sy, r.Width / sx, r.Height / sy);
    }

    private Point ToLocalDipPoint(PixelPoint p)
    {
        var b = _frame.Monitor.Bounds;
        return new Point((p.X - b.X) / _frame.Monitor.ScaleX, (p.Y - b.Y) / _frame.Monitor.ScaleY);
    }

    // 鼠标位置一律走 GetCursorPos 取虚拟屏幕物理坐标，而不是 WPF 的 e.GetPosition：
    // 拖拽跨到另一台显示器时，WPF 给的仍是「相对本窗口」的 DIP，且换算用的是本屏 DPI，
    // 跨屏后完全失真。GetCursorPos 永远是全局物理坐标，跨屏天然正确。
    private static PixelPoint Cursor2Pixel() => MonitorEnumerator.GetCursorPosition();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pt = Cursor2Pixel();

        _session.UpdateCursor(pt);
        if (_capturing) _session.UpdatePress(pt);
        else _session.UpdateHover(pt);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var cursor = Cursor2Pixel();

        // 已确定选区后，双击选区内部 = 确认
        if (e.ClickCount == 2 && _session.Phase == SelectionPhase.Settled
            && _session.Selection.Contains(cursor))
        {
            _session.Confirm();
            return;
        }

        _capturing = CaptureMouse();
        _session.BeginPress(cursor);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_capturing) return;

        _capturing = false;
        ReleaseMouseCapture();
        _session.EndPress(Cursor2Pixel());
    }

    /// <summary>
    /// 系统可能在任何时候强制收回鼠标捕获（另一个窗口抢了捕获、Alt+Tab、显示器热插拔等）。
    /// 不跟着复位 _capturing 的话，标志会和实际状态脱节：后续的 MouseUp 会被
    /// 「if (!_capturing) return」吞掉，状态机永远停在按下状态，鼠标再也不响应。
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_capturing) return;

        _capturing = false;
        _session.EndPress(Cursor2Pixel());
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Escape();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Shift 单独按下才切换颜色格式；Shift 作为组合键修饰符时不能触发。
        // 记下「Shift 按住期间还按过别的键」，抬起时据此决定是不是一次纯粹的 Shift。
        if (e.Key is not (Key.LeftShift or Key.RightShift))
            _shiftConsumed = true;

        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                _session.Escape();
                break;

            case Key.Enter:
                _session.Confirm();
                break;

            case Key.A when ctrl:
                _session.SelectWholeScreen();
                break;

            case Key.C:
                CopyColor();
                break;

            case Key.H:
                _session.ToggleHints();
                UpdateHintVisibility();
                break;

            // WASD 精确移动光标一个像素。鼠标在高分屏上很难点准单个像素，
            // 而取色和选区边界恰恰要求像素级精度。
            case Key.W: MoveCursorBy(0, -step); break;
            case Key.S: MoveCursorBy(0, step); break;
            case Key.A: MoveCursorBy(-step, 0); break;
            case Key.D: MoveCursorBy(step, 0); break;

            case Key.Left: _session.NudgeSelection(-step, 0); break;
            case Key.Right: _session.NudgeSelection(step, 0); break;
            case Key.Up: _session.NudgeSelection(0, -step); break;
            case Key.Down: _session.NudgeSelection(0, step); break;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            if (!_shiftConsumed) _session.ToggleColorFormat();
            _shiftConsumed = false;
            e.Handled = true;
        }
    }

    private void MoveCursorBy(int dx, int dy)
    {
        var target = _session.Cursor;
        var clamped = new PixelPoint(
            Math.Clamp(target.X + dx, _session.Snapshot.VirtualBounds.X, _session.Snapshot.VirtualBounds.Right - 1),
            Math.Clamp(target.Y + dy, _session.Snapshot.VirtualBounds.Y, _session.Snapshot.VirtualBounds.Bottom - 1));

        NativeMethods.SetCursorPos(clamped.X, clamped.Y);

        // SetCursorPos 会产生 WM_MOUSEMOVE，但那是异步的；这里直接同步刷一次，
        // 免得连按 WASD 时读数跟不上手速。
        _session.UpdateCursor(clamped);
        if (_capturing) _session.UpdatePress(clamped);
        else _session.UpdateHover(clamped);
    }

    private void CopyColor()
    {
        if (!_session.CursorOnScreen) return;

        try
        {
            Clipboard.SetText(_session.FormatCursorColor());
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着是常事，取色失败不该打断截图流程
        }
    }
}
