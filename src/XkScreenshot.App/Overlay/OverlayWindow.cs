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
    private readonly ToolOptionsLayer _toolOptionsLayer = new();
    private readonly AnnotationController _annotations;
    private readonly TextBox _textInput;
    private readonly FrostedBackdrop _backdrop;
    private bool _capturing;
    private bool _magnifierWasVisible;
    private bool _shiftConsumed;
    private bool _annotating;

    /// <summary>正在子工具栏上拖的那块（滑块或色盘）；None 表示没在拖。</summary>
    private ToolOptionDrag _dragging;
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
        _toolOptionsLayer.Backdrop = backdrop;
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
                _hintLayer, _toolbarLayer, _toolOptionsLayer, _textInput, _magnifierLayer,
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
        _annotations.Thickness = _session.StrokeThickness;
        _annotations.FontSize = _session.FontSize;
        _annotations.MosaicBlock = _session.MosaicBlock;
        _annotations.MosaicStyle = _session.MosaicStyle;
        _annotations.MosaicBrushWidth = _session.MosaicBrushWidth;
        _annotations.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        _annotationLayer.Selection = _session.Selection;
        _annotationLayer.Context = _session.Selection.IsEmpty ? null : _session.MosaicContext();
        _annotationLayer.Preview = _annotations.Preview;
        _annotationLayer.Selected = _session.SelectedAnnotationItem;
        _annotationLayer.Refresh();
    }

    /// <summary>
    /// 控制点的命中半径。按本屏缩放折算，这样 150% 缩放的屏上不会因为
    /// 物理像素被拉大而显得「点不着」—— 手感要跟屏幕上看到的大小一致。
    /// </summary>
    private double HandleTolerance
        => CaptureSession.DefaultHandleTolerance * _frame.Monitor.ScaleX;

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
        _toolbarLayer.CanUndo = _session.Annotations.CanUndo;
        _toolbarLayer.CanRedo = _session.Annotations.CanRedo;
        _toolbarLayer.CanDelete = _session.SelectedAnnotation >= 0;

        // 子工具栏跟着「选中的标注 / 当前工具」走，不是跟着工具走：
        // 选中一个画好的图形，调的就该是它的参数
        _toolOptionsLayer.Visible = visible;
        _toolOptionsLayer.ActiveTool = _session.EffectiveTool;
        _toolOptionsLayer.Size = _session.SizeOption;
        _toolOptionsLayer.SizeRange = _session.SizeRange;
        _toolOptionsLayer.SizeValue = _session.SizeValue;
        _toolOptionsLayer.MosaicStyle = _session.MosaicStyle;
        _toolOptionsLayer.Color = _session.Style.Stroke;
        _toolOptionsLayer.Hsv = _session.PickerHsv;
        _toolOptionsLayer.PickerOpen = _session.ColorPickerOpen;

        if (visible)
        {
            _toolbarLayer.Layout(ToLocalDip(selection.Intersect(_frame.Monitor.Bounds)));
            _toolOptionsLayer.Layout(_toolbarLayer.PanelRect, _toolbarLayer.PlacedAbove);
        }

        _toolbarLayer.Refresh();
        _toolOptionsLayer.Refresh();
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
        //
        // 但光标压在工具条上时必须收起来：那儿的画面是工具条自己，放大它没有任何意义，
        // 而这块面板足够大，跟着光标飘过去正好把要点的按钮盖住。
        var local = ToLocalDipPoint(_session.Cursor);
        bool onThisMonitor = _frame.Monitor.Bounds.Contains(_session.Cursor)
                             && _session.CursorOnScreen
                             && !_toolbarLayer.Contains(local)
                             && !_toolOptionsLayer.Contains(local);

        if (!onThisMonitor && !_magnifierWasVisible) return;
        _magnifierWasVisible = onThisMonitor;

        if (onThisMonitor)
        {
            _magnifierLayer.CursorPixel = _session.Cursor;
            _magnifierLayer.CursorLocal = local;
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

        if (_dragging != ToolOptionDrag.None)
        {
            DragToolOption(ToWindowDip(pt));
            return;
        }

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
        UpdateCursorShape(pt);
        if (_toolbarLayer.UpdateHover(ToWindowDip(pt)))
            _toolbarLayer.Refresh();
    }

    /// <summary>
    /// 指针形状是这套交互唯一的说明书 —— 控制点画得再清楚，
    /// 不变指针的话用户也未必想到那里能拖。判定全部交给 Session，
    /// 保证「指针承诺的」和「按下去发生的」永远是同一件事。
    /// </summary>
    private void UpdateCursorShape(PixelPoint cursor)
    {
        var local = ToWindowDip(cursor);
        var hint = _toolbarLayer.Contains(local) || _toolOptionsLayer.Contains(local)
            ? CursorHint.Cross
            : _session.HintAt(cursor, HandleTolerance);

        var shape = hint switch
        {
            CursorHint.Move => Cursors.SizeAll,
            CursorHint.SizeNS => Cursors.SizeNS,
            CursorHint.SizeWE => Cursors.SizeWE,
            CursorHint.SizeNWSE => Cursors.SizeNWSE,
            CursorHint.SizeNESW => Cursors.SizeNESW,
            _ => Cursors.Cross,
        };

        if (!ReferenceEquals(Cursor, shape)) Cursor = shape;
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

        if (_toolOptionsLayer.Contains(local))
        {
            HandleToolOptionsClick(local);
            return;
        }

        // 点在别处＝收起色盘。弹层是临时的，不该一直挂在画面上挡着
        if (_session.CloseColorPicker()) return;

        bool insideSelection = _session.Phase == SelectionPhase.Settled
                               && _session.Selection.Contains(cursor);

        // 画完的图形是选中状态，它的控制点优先于「再画一个」：
        // 那几个小方块是刚刚才出现的，按在上面只可能是想调整它
        bool onHandle = _session.HitTestAnnotationHandle(cursor, HandleTolerance) >= 0;

        if (!onHandle && insideSelection && _session.ActiveTool == ToolKind.Text)
        {
            BeginTextInput(cursor);
            return;
        }

        if (!onHandle && insideSelection && _annotations.IsDragTool)
        {
            // 开始画新的一笔就等于放弃刚才那个：旧控制点留着会跟新图形叠在一起
            _session.ClearAnnotationSelection();
            _annotating = true;
            _capturing = CaptureMouse();
            _annotations.Begin(ToAnnotationPoint(cursor));
            return;
        }

        // 已确定选区后，双击选区内部 = 确认。
        // 但双击落在标注上时不算：那时用户明显是在跟这个图形较劲（连点两下想选中、
        // 或者第二下只是手抖），把图截掉走人绝不是他要的。
        if (e.ClickCount == 2 && insideSelection && !HitsAnnotation(cursor))
        {
            _session.Confirm();
            return;
        }

        _capturing = CaptureMouse();
        _session.BeginPress(cursor, HandleTolerance);
    }

    private bool HitsAnnotation(PixelPoint cursor)
        => _session.HitTestAnnotationHandle(cursor, HandleTolerance) >= 0
           || _session.HitTestAnnotation(cursor, HandleTolerance) >= 0;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 子工具栏拖拽的收尾放在 _capturing 之前判断：CaptureMouse 是可能失败的，
        // 那时候标志是 false，但拖拽状态确实开着，漏掉就再也退不出取色了
        if (_dragging != ToolOptionDrag.None)
        {
            _dragging = ToolOptionDrag.None;
            _capturing = false;
            ReleaseMouseCapture();
            // 整段拖拽合成一个撤销点，松手才落账
            _session.FlushStyleEdit();
            return;
        }

        if (!_capturing) return;

        _capturing = false;
        ReleaseMouseCapture();

        if (_annotating)
        {
            _annotating = false;
            // 画完立刻选中它：多数时候第一笔的大小、颜色都要再调一下，
            // 让用户先切回选择工具、再去把刚画的东西点中，是白绕一圈
            if (_annotations.End(ToAnnotationPoint(Cursor2Pixel()))) SelectNewest();
            _annotationLayer.Preview = null;
            SyncAnnotationState();
            SyncToolbar();
            return;
        }

        _session.EndPress(Cursor2Pixel());
    }

    /// <summary>
    /// 子工具栏的点击。滑块和色盘都要能按住一路拖 —— 调粗细、调颜色本来就是
    /// 「按住改到满意为止」，只认单击的话得点几十次才能凑到想要的值。
    /// </summary>
    private void HandleToolOptionsClick(Point local)
    {
        var drag = _toolOptionsLayer.HitTestDrag(local);
        if (drag != ToolOptionDrag.None)
        {
            _dragging = drag;
            _capturing = CaptureMouse();
            DragToolOption(local);
            return;
        }

        var hit = _toolOptionsLayer.HitTest(local);
        if (hit is null) return;

        switch (hit.Kind)
        {
            case ToolOptionKind.MosaicStyle:
                _session.SetMosaicStyle((MosaicStyle)hit.Index);
                break;

            case ToolOptionKind.Color:
                _session.SetColorIndex(hit.Index);
                break;

            case ToolOptionKind.ColorPicker:
                _session.ToggleColorPicker();
                break;
        }
    }

    private void DragToolOption(Point local)
    {
        if (_dragging == ToolOptionDrag.Size)
            _session.SetSizeValue(_toolOptionsLayer.ValueAt(local));
        else
            _session.SetPickerHsv(_toolOptionsLayer.PickAt(local, _dragging));
    }

    private void HandleToolbarClick(Point local)
    {
        var item = _toolbarLayer.HitTest(local);
        if (item is null) return;

        if (item.Tool != ToolKind.None)
        {
            _session.SetTool(item.Tool);
            return;
        }

        switch (item.Command)
        {
            case ToolbarCommand.Select: _session.ClearTool(); break;
            case ToolbarCommand.Delete: _session.DeleteSelectedAnnotation(); break;
            case ToolbarCommand.Undo: _session.Undo(); break;
            case ToolbarCommand.Redo: _session.Redo(); break;
            case ToolbarCommand.Pin: _session.Confirm(CaptureAction.Pin); break;
            case ToolbarCommand.Copy: _session.Confirm(CaptureAction.Copy); break;
            case ToolbarCommand.Save: _session.Confirm(CaptureAction.Save); break;
            case ToolbarCommand.Cancel: _session.Escape(); break;
        }
    }

    /// <summary>
    /// 本窗口改动了标注之后的刷新。走 Session 广播而不是只刷自己 ——
    /// 选区可以跨屏，标注也就可以跨屏，另一块屏上那半边同样要跟着变。
    /// </summary>
    private void SyncAfterEdit() => _session.NotifyAnnotationsChanged();

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

        if (_dragging != ToolOptionDrag.None)
        {
            // 值已经跟着拖拽实时生效了，被强制收回捕获时保留就好，没什么要回滚的
            _dragging = ToolOptionDrag.None;
            _session.FlushStyleEdit();
            return;
        }

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

        if (!_annotations.CommitText(_textOrigin, text)) return;

        SelectNewest();
        SyncAfterEdit();
    }

    /// <summary>选中刚添加的那个标注 —— 它永远排在最后。</summary>
    private void SelectNewest()
        => _session.SelectAnnotation(_session.Annotations.Items.Count - 1);

    private void HideTextInput()
    {
        _textInput.Visibility = Visibility.Collapsed;
        _textInput.Text = string.Empty;
        Focus();
    }

    /// <summary>
    /// 滚轮调当前这一档「大小」：粗细 / 字号 / 马赛克粒度，看手上是什么工具。
    ///
    /// 每种工具只有一个尺寸参数，所以不需要先指定调什么 —— 滚就是了。
    /// 覆盖层是全屏的，光标在哪儿滚都算，不必先把鼠标挪到子工具栏上去。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // 一次事件可能带着好几格，高精度滚轮尤其如此
        int notches = e.Delta / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) return;

        if (_session.StepSize(notches)) e.Handled = true;
    }

    /// <summary>右键与 Esc 同为「逐级返回」：先取消选中的标注，再退工具，最后退选区。</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_session.StepBack()) _session.Escape();
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
                // 逐级返回：标注选中 → 工具 → 重选 → 退出
                if (!_session.StepBack()) _session.Escape();
                break;

            case Key.Enter:
                _session.Confirm();
                break;

            case Key.Delete:
            case Key.Back:
                _session.DeleteSelectedAnnotation();
                break;

            case Key.Z when ctrl:
                _session.Undo();
                break;

            case Key.Y when ctrl:
                _session.Redo();
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
