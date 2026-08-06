using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Annotate;
using XkScreenshot.App.Ui;
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

/// <summary>
/// 光标此刻落在什么上面，决定该显示哪种指针。
/// 由 Session 统一给出，而不是各个窗口自己猜 —— 判定顺序必须和
/// <see cref="CaptureSession.BeginPress"/> 完全一致，否则指针会承诺一件按下去不会发生的事。
/// </summary>
public enum CursorHint
{
    /// <summary>十字：拉一个新选区。</summary>
    Cross,
    /// <summary>四向箭头：整体平移。</summary>
    Move,
    SizeNS,
    SizeWE,
    SizeNWSE,
    SizeNESW,
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

    /// <summary>控制点的默认命中半径（物理像素）。调用方按自己那块屏的缩放传入更准的值。</summary>
    public const double DefaultHandleTolerance = 8;

    /// <summary>一次按下-移动-抬起到底在干什么。</summary>
    private enum PressKind
    {
        None,
        /// <summary>拉出一个新选区。</summary>
        Selecting,
        /// <summary>整体平移已有的选区。</summary>
        Moving,
        /// <summary>拖某个控制点改变选区尺寸。</summary>
        Resizing,
        /// <summary>整体平移一个已选中的标注。</summary>
        MovingAnnotation,
        /// <summary>拖某个控制点改变标注的形状。</summary>
        ResizingAnnotation,
    }

    private PressKind _press = PressKind.None;
    private PixelPoint _anchor;

    /// <summary>按下那一刻的选区。平移和拉伸都从它算起，而不是从上一帧的结果累加。</summary>
    private PixelRect _pressOrigin;

    private int _resizeHandle = -1;

    /// <summary>
    /// 按下时光标与控制点之间的偏移。
    ///
    /// 拉伸时用「光标 - 偏移」而不是光标本身，有两个作用：抓得偏一点也不会让边框
    /// 跳到光标底下；以及原地按一下（双击的第一次 Down）算出来的目标点和原位置相同，
    /// 选区纹丝不动 —— 否则双击确认会先把选区抽歪一下。
    /// </summary>
    private PixelPoint _resizeGrab;

    /// <summary>被拖的那个标注在按下那一刻的样子。撤销点和每一帧的重算都以它为基准。</summary>
    private Annotation? _editOrigin;
    private int _editIndex = -1;
    private int _editHandle = -1;

    /// <summary>按下时光标与标注控制点之间的偏移（选区局部像素），作用同 <see cref="_resizeGrab"/>。</summary>
    private Vector _editGrab;

    /// <summary>
    /// 尚未记入撤销历史的那次改样式的起点，见 <see cref="Restyle"/>。
    /// -1 表示没有待记的改动。
    /// </summary>
    private Annotation? _styleOrigin;
    private int _styleIndex = -1;

    public CaptureSession(DesktopSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public DesktopSnapshot Snapshot { get; }

    /// <summary>本次截图的标注。坐标是选区局部物理像素。</summary>
    public AnnotationDocument Annotations { get; } = new();

    /// <summary>
    /// 当前选中的标注在文档里的下标，-1 表示没选中。
    ///
    /// 放在 Session 而不是某个窗口里：标注可以跨屏，控制点得在每块屏上都画出来；
    /// 存下标而不是对象引用，是因为平移选区会把所有标注整体重基成新对象，
    /// 引用当场失效，而下标在重基前后始终指向同一个标注。
    /// </summary>
    public int SelectedAnnotation { get; private set; } = -1;

    public Annotation? SelectedAnnotationItem
        => SelectedAnnotation >= 0 && SelectedAnnotation < Annotations.Items.Count
            ? Annotations.Items[SelectedAnnotation]
            : null;

    public ToolKind ActiveTool { get; private set; } = ToolKind.None;

    /// <summary>
    /// 子工具栏此刻在为谁服务：选中着标注就是它，否则是当前工具。
    ///
    /// 有了它，「选中一个旧矩形就能改它的颜色线宽」和「选好矩形工具再调参数」
    /// 就是同一套界面，用户不必知道这两件事在内部是不一样的。
    /// </summary>
    public ToolKind EffectiveTool => SelectedAnnotationItem?.Kind ?? ActiveTool;

    // ---------------- 画笔参数 ----------------
    // 挂在 Session 上而不是各窗口的 AnnotationController 上：多屏时每块屏一个 Controller，
    // 参数放那儿的话，在 A 屏调粗的线拖到 B 屏上画出来还是细的。

    /// <summary>描边颜色。可能来自预设，也可能是色盘里挑的任意颜色。</summary>
    public Color StrokeColor { get; private set; } = ToolOptions.Palette[0];

    /// <summary>色盘的 HSV 状态。为什么单独存而不是从 RGB 反推，见 <see cref="Hsv"/>。</summary>
    public Hsv PickerHsv { get; private set; } = Hsv.FromColor(ToolOptions.Palette[0]);

    /// <summary>色盘弹层是否展开。</summary>
    public bool ColorPickerOpen { get; private set; }

    public double StrokeThickness { get; private set; } = ToolOptions.Thickness.Default;
    public double FontSize { get; private set; } = ToolOptions.FontSize.Default;
    public int MosaicBlock { get; private set; } = (int)ToolOptions.MosaicBlock.Default;

    /// <summary>
    /// 马赛克是框选还是涂抹。
    ///
    /// 这一项是「接下来怎么画」而不是某个标注的属性 —— 两种马赛克是两类图形，
    /// 已经画下去的那一个换不了，所以它始终显示画笔的设置，不随选中的标注变。
    /// </summary>
    public MosaicStyle MosaicStyle { get; private set; } = MosaicStyle.Area;

    /// <summary>涂抹式马赛克的笔宽，跟着粒度走，理由见 <see cref="ToolOptions.MosaicBrushWidthRatio"/>。</summary>
    public double MosaicBrushWidth => MosaicBlock * ToolOptions.MosaicBrushWidthRatio;

    /// <summary>画笔的默认参数，新画的标注用它。</summary>
    private AnnotationStyle Defaults
        => new(StrokeColor, StrokeThickness, FontSize, MosaicBlock, MosaicBrushWidth);

    /// <summary>
    /// 子工具栏该显示的参数：选中着标注就显示它自己的，否则显示画笔默认值。
    /// 显示的和即将改的必须是同一份，否则「看到 2px、点一下变成 8px」这种事就会发生。
    /// </summary>
    public AnnotationStyle Style => SelectedAnnotationItem?.StyleOver(Defaults) ?? Defaults;

    /// <summary>当前这一档「大小」调的是什么。None 表示没什么可调（没选工具也没选标注）。</summary>
    public SizeOption SizeOption => ToolOptions.SizeOf(EffectiveTool);

    public OptionRange SizeRange => ToolOptions.RangeOf(SizeOption);

    /// <summary>当前「大小」的值。</summary>
    public double SizeValue => SizeOption switch
    {
        SizeOption.FontSize => Style.FontSize,
        SizeOption.MosaicBlock => Style.MosaicBlock,
        _ => Style.Thickness,
    };

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

        // 换到别的工具就是要画新东西了，旧标注的控制点留在屏幕上只会挡路。
        // 反过来，退出工具（再点一次同一个）保留选中 —— 用户多半正是想接着调它。
        if (ActiveTool != ToolKind.None)
        {
            FlushStyleEdit();
            SelectedAnnotation = -1;
        }

        // 换工具意味着换了一整排子工具栏，色盘弹层还悬在那儿会盖住新的按钮
        ColorPickerOpen = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// 退出当前标注工具。返回值表示「刚才确实有工具在用」，
    /// 供 Esc / 右键做逐级返回：先退工具，没工具可退才退选区。
    /// </summary>
    public bool ClearTool()
    {
        if (ActiveTool == ToolKind.None) return false;

        ActiveTool = ToolKind.None;
        ColorPickerOpen = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 某个窗口直接改动了标注文档（画完一笔、提交一段文字）之后，
    /// 通知所有覆盖层重绘 —— 标注跟选区一样可以跨屏，不能只刷自己那一块。
    /// </summary>
    public void NotifyAnnotationsChanged() => Changed?.Invoke();

    public void SelectAnnotation(int index)
    {
        if (index == SelectedAnnotation) return;

        FlushStyleEdit();
        SelectedAnnotation = index;
        AdoptStyle(SelectedAnnotationItem);
        Changed?.Invoke();
    }

    /// <summary>
    /// 选中一个标注时，把它的参数收作画笔的当前参数。
    ///
    /// 不这么做的话，子工具栏显示的是选中图形的参数、而接着画出来的却是画笔原来那套，
    /// 两者对不上。收过来之后，「看到的 = 要改的 = 接下来会画的」始终是同一套值。
    /// </summary>
    private void AdoptStyle(Annotation? item)
    {
        if (item is null) return;

        var style = item.StyleOver(Defaults);
        StrokeColor = style.Stroke;
        PickerHsv = Hsv.FromColor(style.Stroke);
        StrokeThickness = ToolOptions.Thickness.Clamp(style.Thickness);
        FontSize = ToolOptions.FontSize.Clamp(style.FontSize);
        MosaicBlock = (int)ToolOptions.MosaicBlock.Clamp(style.MosaicBlock);
    }

    /// <summary>取消标注选中。返回值表示「刚才确实选中着一个」，供逐级返回判断。</summary>
    public bool ClearAnnotationSelection()
    {
        if (SelectedAnnotation < 0) return false;

        FlushStyleEdit();
        SelectedAnnotation = -1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 逐级返回一步：色盘弹层 → 标注选中 → 工具。
    /// 返回 false 表示这几级都没得退，该由调用方决定是退选区还是退出截图。
    /// </summary>
    public bool StepBack() => CloseColorPicker() || ClearAnnotationSelection() || ClearTool();

    public bool DeleteSelectedAnnotation()
    {
        // 先把攒着的改样式记账再删：不然撤销一步只能撤回删除，
        // 而删掉之前那次改色改粗细已经没有落脚点了
        FlushStyleEdit();
        if (!Annotations.RemoveAt(SelectedAnnotation)) return false;

        // 删掉之后下标全体前移，留着原来那个会指到隔壁的标注上
        SelectedAnnotation = -1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 撤销/重做整份列表都会换掉，下标随时可能指到另一个标注上，所以一并取消选中。
    /// </summary>
    public bool Undo()
    {
        // 攒着的改样式先落进历史，Ctrl+Z 才能一步一步退回去
        FlushStyleEdit();
        if (!Annotations.Undo()) return false;

        SelectedAnnotation = -1;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        FlushStyleEdit();
        if (!Annotations.Redo()) return false;

        SelectedAnnotation = -1;
        Changed?.Invoke();
        return true;
    }

    public void SetColorIndex(int index)
    {
        if (index < 0 || index >= ToolOptions.Palette.Length) return;
        SetStrokeColor(ToolOptions.Palette[index]);
    }

    /// <summary>
    /// 换颜色。色盘的 HSV 跟着同步一次，这样点了预设色再打开色盘，
    /// 光标就落在那个颜色上，而不是停在上一次调的位置上。
    /// </summary>
    public void SetStrokeColor(Color color)
    {
        // 两个都要比：选中的标注是蓝的、画笔默认是红的时候点「红」，
        // 只比画笔默认值的话会当成没变化，那个蓝图形就永远变不成红的
        if (color == StrokeColor && Style.Stroke == color) return;

        var restyled = Style with { Stroke = color };
        StrokeColor = color;
        PickerHsv = Hsv.FromColor(color);
        Restyle(restyled);
        Changed?.Invoke();
    }

    /// <summary>色盘拖拽时走这里：HSV 是权威，颜色由它算出来。</summary>
    public void SetPickerHsv(Hsv hsv)
    {
        if (hsv == PickerHsv) return;

        var color = hsv.ToColor();
        var restyled = Style with { Stroke = color };
        PickerHsv = hsv;
        StrokeColor = color;
        Restyle(restyled);
        Changed?.Invoke();
    }

    public void ToggleColorPicker()
    {
        ColorPickerOpen = !ColorPickerOpen;
        Changed?.Invoke();
    }

    /// <summary>关掉色盘弹层。返回值表示「刚才确实开着」，供逐级返回判断。</summary>
    public bool CloseColorPicker()
    {
        if (!ColorPickerOpen) return false;

        ColorPickerOpen = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 改「大小」。同时写进画笔默认值和选中的那个标注 ——
    /// 用户调粗一条线之后，接着画的自然也该是粗的。
    /// </summary>
    public bool SetSizeValue(double value)
    {
        if (SizeOption == SizeOption.None) return false;

        value = SizeRange.Clamp(value);
        if (value == SizeValue) return false;

        switch (SizeOption)
        {
            case SizeOption.FontSize:
                var sized = Style with { FontSize = value };
                FontSize = value;
                Restyle(sized);
                break;

            case SizeOption.MosaicBlock:
                int block = (int)Math.Round(value);
                var grained = Style with
                {
                    MosaicBlock = block,
                    MosaicBrushWidth = block * ToolOptions.MosaicBrushWidthRatio,
                };
                MosaicBlock = block;
                Restyle(grained);
                break;

            default:
                var thicker = Style with { Thickness = value };
                StrokeThickness = value;
                Restyle(thicker);
                break;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 滚轮调「大小」。返回 false 表示此刻没什么可调，滚轮该留给别人。
    /// 已经滚到头也算调过了 —— 那是「到顶了」，不是「这里不能滚」。
    /// </summary>
    public bool StepSize(int notches)
    {
        if (SizeOption == SizeOption.None) return false;

        SetSizeValue(SizeRange.Nudge(SizeValue, notches));
        return true;
    }

    public void SetMosaicStyle(MosaicStyle style)
    {
        if (style == MosaicStyle) return;

        MosaicStyle = style;
        Changed?.Invoke();
    }

    /// <summary>
    /// 把新样式套到选中的标注上。
    ///
    /// 连续的调整（拖滑块、一路滚滚轮）只该留一个撤销点，所以这里只做实时替换，
    /// 起点先攒着；等到下一次别的操作（按下鼠标、换选中对象、撤销……）把它冲掉时
    /// 再一次性记账，见 <see cref="FlushStyleEdit"/>。
    /// </summary>
    private void Restyle(AnnotationStyle style)
    {
        if (SelectedAnnotationItem is not { } item) return;

        if (_styleIndex != SelectedAnnotation)
        {
            FlushStyleEdit();
            _styleIndex = SelectedAnnotation;
            _styleOrigin = item;
        }

        Annotations.ReplaceLive(SelectedAnnotation, item.WithStyle(style));
    }

    /// <summary>把攒着的那次改样式记进撤销历史。没有待记的改动时什么都不做。</summary>
    public void FlushStyleEdit()
    {
        if (_styleOrigin is not null) Annotations.CommitEdit(_styleIndex, _styleOrigin);

        _styleOrigin = null;
        _styleIndex = -1;
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

        StartFreshSelection(Selection == monitor.Bounds ? Snapshot.VirtualBounds : monitor.Bounds);
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
    public void BeginPress(PixelPoint cursor, double handleTolerance = DefaultHandleTolerance)
    {
        _anchor = cursor;
        // 一次新的按下就是上一串参数调整的终点，到此为止记一个撤销点
        FlushStyleEdit();

        // 判定顺序就是优先级，改动前先想清楚：
        // 已选中标注的控制点 > 选区控制点 > 标注本体 > 选区内部。
        // 标注的控制点排在选区前面，是因为贴着选区边缘画的图形，两处控制点会叠在一起 ——
        // 那种情况下用户盯着的显然是自己刚选中的那个图形。
        if (BeginAnnotationResize(cursor, handleTolerance)) return;

        int handle = HitTestHandle(cursor, handleTolerance);
        if (handle >= 0)
        {
            _press = PressKind.Resizing;
            _pressOrigin = Selection;
            _resizeHandle = handle;
            _resizeGrab = cursor - Handles.At(Selection, handle);
            return; // 同上：还什么都没变
        }

        if (BeginAnnotationMove(cursor, handleTolerance)) return;

        if (Phase == SelectionPhase.Settled && Selection.Contains(cursor))
        {
            // 点在空白处＝放弃当前选中的标注，和所有画布类工具的习惯一致
            ClearAnnotationSelection();
            _press = PressKind.Moving;
            _pressOrigin = Selection;
            return; // 选区原样保留，什么都还没变，不必通知重绘
        }

        _press = PressKind.Selecting;
        Phase = SelectionPhase.Dragging;
        StartFreshSelection(PixelRect.Empty);
        Changed?.Invoke();
    }

    /// <summary>标注只在选区已定之后才谈得上编辑。</summary>
    private bool CanEditAnnotations
        => Phase == SelectionPhase.Settled && !Selection.IsEmpty;

    /// <summary>虚拟屏幕物理坐标 → 选区局部坐标（标注文档用的坐标系）。</summary>
    private Point ToLocal(PixelPoint p) => new(p.X - Selection.X, p.Y - Selection.Y);

    private bool BeginAnnotationResize(PixelPoint cursor, double tolerance)
    {
        int handle = HitTestAnnotationHandle(cursor, tolerance);
        if (handle < 0) return false;

        var item = SelectedAnnotationItem!;
        _press = PressKind.ResizingAnnotation;
        _editIndex = SelectedAnnotation;
        _editOrigin = item;
        _editHandle = handle;
        _editGrab = ToLocal(cursor) - item.HandleAt(handle);
        return true;
    }

    private bool BeginAnnotationMove(PixelPoint cursor, double tolerance)
    {
        int index = HitTestAnnotation(cursor, tolerance);
        if (index < 0) return false;

        _press = PressKind.MovingAnnotation;
        _editIndex = index;
        _editOrigin = Annotations.Items[index];

        // 点中即选中：不必先点一下选、再点一下拖，一次按下就能开始移动
        SelectAnnotation(index);
        return true;
    }

    /// <summary>
    /// 命中已选中标注的控制点，返回控制点编号，-1 表示没命中。
    ///
    /// 有画图工具在用时照样认：刚画完的图形是选中状态，用户十有八九接着就想拽一下
    /// 把它调正 —— 控制点只有指甲盖大小，点在上面的意图不会有第二种解释。
    /// </summary>
    public int HitTestAnnotationHandle(PixelPoint cursor, double tolerance = DefaultHandleTolerance)
    {
        if (!CanEditAnnotations || SelectedAnnotationItem is not { } item) return -1;
        if (!HandlesUsable(item, tolerance)) return -1;

        var local = ToLocal(cursor);
        for (int i = 0; i < item.HandleCount; i++)
        {
            var h = item.HandleAt(i);
            if (Math.Abs(local.X - h.X) <= tolerance && Math.Abs(local.Y - h.Y) <= tolerance) return i;
        }
        return -1;
    }

    /// <summary>
    /// 图形小到八个控制点互相压住时一律不认，理由同 <see cref="Handles.HitTest"/>：
    /// 那种尺寸下点哪儿都是歧义，退回「拖动＝整体平移」反而合手。
    /// 端点式的控制点（箭头两头、文字四角）不受此限 —— 它们本来就不在一条边上排队，
    /// 而且一根水平箭头的外框高度是 0，按外框判会把它的两个端点一并判没。
    ///
    /// 画与点必须同规同尺：<see cref="AnnotationLayer"/> 用同一个判据决定画不画，
    /// 否则会出现「看得见却抓不着」或者反过来的鬼影控制点。
    /// </summary>
    public static bool HandlesUsable(Annotation item, double tolerance)
        => item.HandleCount != Handles.Count || Handles.FitIn(item.Frame, tolerance);

    /// <summary>
    /// 命中任意标注本体，返回下标，-1 表示没命中。
    ///
    /// 只认选区内部：拖出界的那部分是被裁掉、看不见的，让看不见的东西抢走点击，
    /// 用户只会觉得「这块地方莫名其妙框不了新选区」。要把它拖回来还有控制点。
    ///
    /// 有画图工具在用时一律不认：那时候在图形上按下去是要在它上面再画一笔，
    /// 而不是把它挪走 —— 和控制点不同，图形本体大得很，误判的代价也大得多。
    /// </summary>
    public int HitTestAnnotation(PixelPoint cursor, double tolerance = DefaultHandleTolerance)
        => CanEditAnnotations && ActiveTool == ToolKind.None && Selection.Contains(cursor)
            ? Annotations.HitTest(ToLocal(cursor), tolerance)
            : -1;

    /// <summary>
    /// 光标此刻该显示什么指针。判定顺序与 <see cref="BeginPress"/> 严格一致 ——
    /// 指针是对「现在按下去会发生什么」的承诺，两边分叉就是骗人。
    /// </summary>
    public CursorHint HintAt(PixelPoint cursor, double tolerance = DefaultHandleTolerance)
    {
        int annotationHandle = HitTestAnnotationHandle(cursor, tolerance);
        if (annotationHandle >= 0)
            return HintForHandle(SelectedAnnotationItem!, annotationHandle);

        // 除了那几个控制点，有画图工具在用时一律是「即将画一笔」，指针不该暗示别的
        if (ActiveTool != ToolKind.None) return CursorHint.Cross;

        int handle = HitTestHandle(cursor, tolerance);
        if (handle >= 0) return HintForIndex(handle);

        if (HitTestAnnotation(cursor, tolerance) >= 0) return CursorHint.Move;

        return Phase == SelectionPhase.Settled && Selection.Contains(cursor)
            ? CursorHint.Move
            : CursorHint.Cross;
    }

    /// <summary>
    /// 自由端点（箭头的两头）不存在「往哪个方向拉伸」，给方向箭头反而误导 ——
    /// 它是可以往任意方向拖的。
    /// </summary>
    private static CursorHint HintForHandle(Annotation item, int handle)
    {
        int axis = item.HandleAxis(handle);
        return axis < 0 ? CursorHint.Move : HintForIndex(axis);
    }

    private static CursorHint HintForIndex(int handle) => handle switch
    {
        Handles.TopLeft or Handles.BottomRight => CursorHint.SizeNWSE,
        Handles.TopRight or Handles.BottomLeft => CursorHint.SizeNESW,
        Handles.Top or Handles.Bottom => CursorHint.SizeNS,
        _ => CursorHint.SizeWE,
    };

    public void UpdatePress(PixelPoint cursor)
    {
        switch (_press)
        {
            case PressKind.Selecting:
                StartFreshSelection(PixelRect.FromPoints(_anchor, cursor).Intersect(Snapshot.VirtualBounds));
                break;

            case PressKind.Moving:
                var delta = cursor - _anchor;
                ReshapeSelection(_pressOrigin.Offset(delta.X, delta.Y).ClampInto(Snapshot.VirtualBounds));
                break;

            case PressKind.Resizing:
                ResizeTo(cursor);
                break;

            case PressKind.MovingAnnotation:
                // 选区在这期间不动，所以屏幕上的位移就是选区局部的位移，不必换算
                var moved = cursor - _anchor;
                Annotations.ReplaceLive(_editIndex, _editOrigin!.Translate(moved.X, moved.Y));
                break;

            case PressKind.ResizingAnnotation:
                var target = ToLocal(cursor) - _editGrab;
                Annotations.ReplaceLive(_editIndex, _editOrigin!.DragHandle(_editHandle, target));
                break;

            default:
                return;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 选区只在 Settled 且没有标注工具在用时才认控制点。
    /// 画图的时候手正好落在边角上不能变成拉框，那会让人莫名其妙。
    /// </summary>
    public int HitTestHandle(PixelPoint cursor, double tolerance = DefaultHandleTolerance)
        => Phase == SelectionPhase.Settled && ActiveTool == ToolKind.None && !Selection.IsEmpty
            ? Handles.HitTest(Selection, cursor, tolerance)
            : -1;

    private void ResizeTo(PixelPoint cursor)
    {
        // 先把目标点夹进桌面范围，再算矩形。直接夹结果矩形会连带把对边也推走
        var target = new PixelPoint(
            Math.Clamp(cursor.X - _resizeGrab.X, Snapshot.VirtualBounds.X, Snapshot.VirtualBounds.Right),
            Math.Clamp(cursor.Y - _resizeGrab.Y, Snapshot.VirtualBounds.Y, Snapshot.VirtualBounds.Bottom));

        // 始终从按下那一刻的矩形算起。用上一帧的结果累加的话，拖过头翻转之后
        // 控制点编号就和实际边对不上了，选区会开始自己乱跳。
        var next = Handles.Resize(_pressOrigin, _resizeHandle, target);

        // 拖到完全塌掉时保持原样，别让一次手滑把选区整个抹掉
        if (next.Width < 1 || next.Height < 1) return;

        ReshapeSelection(next);
    }

    /// <summary>
    /// 换成一块全新的选区。旧标注必须整个丢掉 —— 它们的坐标是相对旧选区左上角的，
    /// 留着的话会按新原点重新画出来，落到完全不相干的位置上。
    /// </summary>
    private void StartFreshSelection(PixelRect next)
    {
        Selection = next;
        ActiveTool = ToolKind.None;
        SelectedAnnotation = -1;
        // 标注连同历史一起丢掉，攒着的那个起点也就没有落脚处了
        _styleOrigin = null;
        _styleIndex = -1;
        Annotations.Reset();
    }

    /// <summary>
    /// 换一个位置或尺寸，但仍是同一块选区（平移、拉伸、方向键微调都走这里）。
    /// 标注要跟着画面内容走而不是跟着选框走，所以坐标按原点位移反向重基；
    /// 只改宽高不动原点的拉伸（右/下方向）位移为零，标注原样不动。
    /// </summary>
    private void ReshapeSelection(PixelRect next)
    {
        if (next == Selection) return;

        Annotations.Rebase(Selection.X - next.X, Selection.Y - next.Y);
        Selection = next;
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
                    StartFreshSelection(hit is null
                        ? PixelRect.Empty
                        : hit.Bounds.Intersect(Snapshot.VirtualBounds));
                }
                else
                {
                    StartFreshSelection(
                        PixelRect.FromPoints(_anchor, cursor).Intersect(Snapshot.VirtualBounds));
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
            case PressKind.Resizing:
                Phase = SelectionPhase.Settled;
                break;

            case PressKind.MovingAnnotation:
            case PressKind.ResizingAnnotation:
                // 整段拖拽合成一个撤销点。原地按一下没真动过的话什么都不记
                Annotations.CommitEdit(_editIndex, _editOrigin!);
                break;

            default:
                return;
        }

        _press = PressKind.None;
        _resizeHandle = -1;
        _editIndex = -1;
        _editHandle = -1;
        _editOrigin = null;
        Changed?.Invoke();
    }

    public void Confirm(CaptureAction action = CaptureAction.Copy)
    {
        if (Selection.IsEmpty) return;
        FlushStyleEdit();
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
        _editOrigin = null;
        _editIndex = -1;
        FlushStyleEdit();

        if (Phase == SelectionPhase.Settled && !Selection.IsEmpty)
        {
            Phase = SelectionPhase.Idle;
            StartFreshSelection(PixelRect.Empty);
            Changed?.Invoke();
            return;
        }
        Cancelled?.Invoke();
    }

    /// <summary>方向键微调，按住 Shift 时步长放大。</summary>
    public void NudgeSelection(int dx, int dy)
    {
        // 选中着标注时调的是那个标注：此刻用户的注意力明摆着在它身上，
        // 而选区随时可以按 Esc 取消选中之后再调。
        if (NudgeAnnotation(dx, dy)) return;

        if (Selection.IsEmpty) return;
        ReshapeSelection(Selection.Offset(dx, dy).ClampInto(Snapshot.VirtualBounds));
        Changed?.Invoke();
    }

    private bool NudgeAnnotation(int dx, int dy)
    {
        if (!CanEditAnnotations || SelectedAnnotationItem is not { } item) return false;

        // 攒着的改样式先记账，否则撤销历史里两笔编辑会前后颠倒
        FlushStyleEdit();
        Annotations.ReplaceLive(SelectedAnnotation, item.Translate(dx, dy));
        // 一次按键就是一次编辑，各自记一个撤销点
        Annotations.CommitEdit(SelectedAnnotation, item);
        Changed?.Invoke();
        return true;
    }
}
