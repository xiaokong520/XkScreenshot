using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.Annotate;
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
    private readonly AnnotationLayer _annotationLayer = new();
    private readonly ToolbarLayer _toolbarLayer = new();
    private readonly AnnotationController _annotations;
    private readonly TextBox _textInput;
    private readonly FrostedBackdrop _backdrop;
    private bool _capturing;
    private bool _magnifierWasVisible;
    private bool _shiftConsumed;
    private bool _annotating;
    private Point _textOrigin;

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

        // 毛玻璃背景三层共用一份：模糊一次全屏画面就够了，没必要各算各的
        var backdrop = new FrostedBackdrop(frame.Frame);
        _magnifierLayer.Frame = frame.Frame;
        _magnifierLayer.Backdrop = backdrop;
        _hintLayer.Backdrop = backdrop;
        _toolbarLayer.Backdrop = backdrop;
        _backdrop = backdrop;

        _annotations = new AnnotationController { Document = session.Annotations };
        _annotationLayer.Document = session.Annotations;
        _annotationLayer.MonitorOrigin = new PixelPoint(frame.Monitor.Bounds.X, frame.Monitor.Bounds.Y);
        _annotationLayer.ScaleX = frame.Monitor.ScaleX;
        _annotationLayer.ScaleY = frame.Monitor.ScaleY;

        _textInput = CreateTextInput();

        // 层序即绘制顺序：标注压在冻结画面上，工具条和放大镜浮在最上面
        Content = new Grid
        {
            Children =
            {
                image, _selectionLayer, _annotationLayer,
                _hintLayer, _toolbarLayer, _textInput, _magnifierLayer,
            },
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
        SyncAnnotationState();
        SyncToolbar();
        UpdateHintVisibility();
    }

    private void SyncAnnotationState()
    {
        _annotations.Tool = _session.ActiveTool;
        _annotations.Stroke = _session.StrokeColor;
        _annotations.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        _annotationLayer.Selection = _session.Selection;
        _annotationLayer.Context = _session.Selection.IsEmpty ? null : _session.MosaicContext();
        _annotationLayer.Preview = _annotations.Preview;
        _annotationLayer.Refresh();
    }

    /// <summary>
    /// 工具条只出现在「持有选区右下角」的那块屏上。跨屏选区时若每块屏都画，
    /// 会出现两个工具条，点哪个都对不上。
    /// </summary>
    private void SyncToolbar()
    {
        var selection = _session.Selection;
        bool visible = _session.Phase == SelectionPhase.Settled
                       && !selection.IsEmpty
                       && _frame.Monitor.Bounds.Contains(new PixelPoint(selection.Right - 1, selection.Bottom - 1));

        _toolbarLayer.Visible = visible;
        _toolbarLayer.ActiveTool = _session.ActiveTool;
        _toolbarLayer.ActiveColorIndex = _session.ColorIndex;
        _toolbarLayer.CanUndo = _session.Annotations.CanUndo;
        _toolbarLayer.CanRedo = _session.Annotations.CanRedo;

        if (visible)
            _toolbarLayer.Layout(ToLocalDip(selection.Intersect(_frame.Monitor.Bounds)));

        _toolbarLayer.Refresh();
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

    /// <summary>虚拟屏幕物理坐标 → 选区局部物理坐标（标注文档用的坐标系）。</summary>
    private Point ToAnnotationPoint(PixelPoint p)
        => new(p.X - _session.Selection.X, p.Y - _session.Selection.Y);

    private Point ToWindowDip(PixelPoint p)
        => ToLocalDipPoint(p);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pt = Cursor2Pixel();

        _session.UpdateCursor(pt);

        if (_annotating)
        {
            _annotations.Update(ToAnnotationPoint(pt));
            _annotationLayer.Preview = _annotations.Preview;
            _annotationLayer.Refresh();
            return;
        }

        if (_capturing)
        {
            _session.UpdatePress(pt);
            return;
        }

        _session.UpdateHover(pt);
        if (_toolbarLayer.UpdateHover(ToWindowDip(pt)))
            _toolbarLayer.Refresh();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var cursor = Cursor2Pixel();
        var local = ToWindowDip(cursor);

        CommitPendingText();

        // 工具条最优先：它盖在选区上，点它绝不能被当成框选或标注
        if (_toolbarLayer.Contains(local))
        {
            HandleToolbarClick(local);
            return;
        }

        bool insideSelection = _session.Phase == SelectionPhase.Settled
                               && _session.Selection.Contains(cursor);

        if (insideSelection && _session.ActiveTool == ToolKind.Text)
        {
            BeginTextInput(cursor);
            return;
        }

        if (insideSelection && _annotations.IsDragTool)
        {
            _annotating = true;
            _capturing = CaptureMouse();
            _annotations.Begin(ToAnnotationPoint(cursor));
            return;
        }

        // 已确定选区后，双击选区内部 = 确认
        if (e.ClickCount == 2 && insideSelection)
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

        if (_annotating)
        {
            _annotating = false;
            _annotations.End(ToAnnotationPoint(Cursor2Pixel()));
            _annotationLayer.Preview = null;
            SyncAnnotationState();
            SyncToolbar();
            return;
        }

        _session.EndPress(Cursor2Pixel());
    }

    private void HandleToolbarClick(Point local)
    {
        int swatch = _toolbarLayer.HitTestSwatch(local);
        if (swatch >= 0)
        {
            _session.SetColorIndex(swatch);
            return;
        }

        var item = _toolbarLayer.HitTest(local);
        if (item is null) return;

        if (item.Tool != ToolKind.None)
        {
            _session.SetTool(item.Tool);
            return;
        }

        switch (item.Command)
        {
            case ToolbarCommand.Undo: _session.Annotations.Undo(); SyncAfterEdit(); break;
            case ToolbarCommand.Redo: _session.Annotations.Redo(); SyncAfterEdit(); break;
            case ToolbarCommand.Pin: _session.Confirm(CaptureAction.Pin); break;
            case ToolbarCommand.Copy: _session.Confirm(CaptureAction.Copy); break;
            case ToolbarCommand.Save: _session.Confirm(CaptureAction.Save); break;
            case ToolbarCommand.Cancel: _session.Escape(); break;
        }
    }

    private void SyncAfterEdit()
    {
        SyncAnnotationState();
        SyncToolbar();
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

        if (_annotating)
        {
            _annotating = false;
            _annotations.Cancel();
            SyncAfterEdit();
            return;
        }

        _session.EndPress(Cursor2Pixel());
    }

    // ---------------- 文字标注 ----------------

    /// <summary>
    /// 文字标注用真正的 TextBox 承接输入，而不是自己处理按键。
    /// 中文输入法的候选窗、组合串、光标定位全部由它负责 —— 手写一套只会又难用又有 bug。
    /// </summary>
    private TextBox CreateTextInput()
    {
        var box = new TextBox
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AcceptsReturn = true,
            MinWidth = 60,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x3B, 0x9E, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            CaretBrush = Brushes.White,
        };

        box.LostFocus += (_, _) => CommitPendingText();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                box.Text = string.Empty;
                HideTextInput();
                e.Handled = true;
            }
            // 单独 Enter 提交，Shift+Enter 换行 —— 多行标注偶尔要用
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitPendingText();
                e.Handled = true;
            }
        };
        return box;
    }

    private void BeginTextInput(PixelPoint cursor)
    {
        _textOrigin = ToAnnotationPoint(cursor);
        var local = ToWindowDip(cursor);

        _textInput.Text = string.Empty;
        _textInput.FontSize = _annotations.FontSize / _frame.Monitor.ScaleY;
        _textInput.Foreground = new SolidColorBrush(_session.StrokeColor);
        _textInput.Margin = new Thickness(local.X, local.Y, 0, 0);
        _textInput.Visibility = Visibility.Visible;
        _textInput.Focus();
    }

    private void CommitPendingText()
    {
        if (_textInput.Visibility != Visibility.Visible) return;

        string text = _textInput.Text;
        HideTextInput();

        if (_annotations.CommitText(_textOrigin, text))
            SyncAfterEdit();
    }

    private void HideTextInput()
    {
        _textInput.Visibility = Visibility.Collapsed;
        _textInput.Text = string.Empty;
        Focus();
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
                // 有工具在用时，Esc 先退出工具，再按才是退回重选
                if (_session.ActiveTool != ToolKind.None) _session.SetTool(_session.ActiveTool);
                else _session.Escape();
                break;

            case Key.Enter:
                _session.Confirm();
                break;

            case Key.Z when ctrl:
                _session.Annotations.Undo();
                SyncAfterEdit();
                break;

            case Key.Y when ctrl:
                _session.Annotations.Redo();
                SyncAfterEdit();
                break;

            case Key.T when ctrl:
                _session.Confirm(CaptureAction.Pin);
                break;

            case Key.S when ctrl:
                _session.Confirm(CaptureAction.Save);
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
