using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 长截图控制条的面板本体：**自绘**，和覆盖层工具条（<see cref="ToolbarLayer"/>）同一套画法。
///
/// 为什么不用 WPF 控件拼（Border + Button + Viewbox）：那样做出来的东西参数怎么调都只是「像」。
/// 圆角、按钮尺寸、图标线宽、投影、描边、顶边高光、分组间距，工具条这边全是手写的具体数值，
/// 控件那边是样式叠加出来的近似值 —— 摆在同一个屏幕上，一眼就能看出是两套。
/// 这里直接调用同一份 <see cref="PanelChrome.DrawGlassPanel"/> 和 <see cref="Icons.Draw"/>，
/// 参数逐个照抄工具条，两个面板就成了同一个画法下的两种内容。
///
/// 唯一的差别在底色：工具条底下是**冻结的截图**，能先模糊再压色；控制条压在**实时画面**上，
/// 没有可模糊的底，于是走 <see cref="PanelChrome.DrawGlassPanel"/> 里 backdrop 为空的那条
/// 分支 —— 同一个圆角、同一道高光、同一圈描边，只是用不透明底。
///
/// 命中测试也是手写的（和工具条一样）：按钮就五个，矩形在布局时顺手记下来，
/// 用不着为一排图标撑起一棵控件树。
/// </summary>
internal sealed class ScrollPanelView : FrameworkElement
{
    // 下面这组数值与 ToolbarLayer 逐个对齐，改一处就要一起改，否则两个面板立刻不一样
    private const double ButtonSize = 32;
    private const double IconSize = 18;
    private const double ButtonGap = 2;
    private const double GroupGap = 12;
    private const double PadX = 8;
    private const double PadY = 7;
    private const double CornerRadius = 10;
    private const double ButtonRadius = 7;

    /// <summary>
    /// 面板四周给投影留的空白。取值与 <see cref="PanelChrome.DrawShadow"/> 的最大扩张一致
    /// （6 层 × 每层 2），少一格投影就会被窗口边缘切掉。
    /// </summary>
    public const double ShadowPad = 12;

    /// <summary>窗口要的尺寸（面板 + 四周投影留白）。</summary>
    public static Size OuterSize => new(
        PanelSize.Width + ShadowPad * 2,
        PanelSize.Height + ShadowPad * 2);

    /// <summary>面板本身的尺寸，不含投影留白。宽度是按五个等宽按钮算出来的常数。</summary>
    private static Size PanelSize => new(
        PadX * 2 + 2 * ButtonSize + ButtonGap + GroupGap + 3 * ButtonSize + 2 * ButtonGap,
        ButtonSize + PadY * 2);

    private enum Item
    {
        Auto,
        Manual,
        Accept,
        Edit,
        Cancel,
    }

    private readonly OverlayPalette _palette;

    private Item? _hovered;
    private Item? _pressed;

    public ScrollPanelView(OverlayPalette palette)
    {
        _palette = palette;
        Focusable = false;
    }

    /// <summary>当前滚动方式，决定左边两枚按钮哪一个是亮的。</summary>
    public ScrollMode Mode { get; private set; } = ScrollMode.Auto;

    public event Action<ScrollMode>? ModeChanged;

    public event Action? Accepted;

    public event Action? EditRequested;

    public event Action? Cancelled;

    /// <summary>
    /// 悬停到某个按钮上（局部 DIP 矩形 + 一句说明）；移开时矩形为空、文字为空。
    ///
    /// 为什么由外面去显示：气泡超出这条窄条的范围，只能另开一扇窗（见
    /// <see cref="ScrollTipWindow"/>）。面板这边只知道「鼠标压在哪枚按钮上」，
    /// 换算成屏幕坐标是窗口那层的事。
    /// </summary>
    public event Action<Rect, string>? TipChanged;

    /// <summary>
    /// 由引擎回填当前模式。**不触发 <see cref="ModeChanged"/>** ——
    /// 这是「我说了算的现状」，不是「用户点了按钮」，回填再发通知会和引擎来回打转。
    /// </summary>
    public void ShowMode(ScrollMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        InvalidateVisual();
    }

    private void PickMode(ScrollMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        InvalidateVisual();
        ModeChanged?.Invoke(mode);
    }

    protected override Size MeasureOverride(Size availableSize) => OuterSize;

    protected override Size ArrangeOverride(Size finalSize) => OuterSize;

    protected override void OnRender(DrawingContext dc)
    {
        // 透明底铺满整个窗口：命中测试按绘制内容算，没有这一笔，投影留白那圈点不到
        // （点空白处 = 拖走面板，这条得一直有效）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var panel = new Rect(ShadowPad, ShadowPad, PanelSize.Width, PanelSize.Height);

        // 与工具条同一条路径：投影 → 底 → 顶边内高光 → 描边。
        // backdrop 传 null（压在实时画面上，没有可模糊的底），其余一模一样
        PanelChrome.DrawGlassPanel(dc, panel, CornerRadius, null, RenderSize, _palette);

        double x = panel.X + PadX;
        double y = panel.Y + PadY;

        foreach (var item in new[] { Item.Auto, Item.Manual })
        {
            DrawButton(dc, new Rect(x, y, ButtonSize, ButtonSize), item);
            x += ButtonSize + ButtonGap;
        }

        // 分组：先跳过 GroupGap，再在两组正中间补一道分隔线（算法与工具条一致）
        x += GroupGap - ButtonGap;
        dc.DrawRectangle(_palette.Separator, null,
            new Rect(Math.Round(x - GroupGap / 2.0) + 0.5, y + 5, 1, ButtonSize - 10));

        foreach (var item in new[] { Item.Accept, Item.Edit, Item.Cancel })
        {
            DrawButton(dc, new Rect(x, y, ButtonSize, ButtonSize), item);
            x += ButtonSize + ButtonGap;
        }
    }

    private void DrawButton(DrawingContext dc, Rect rect, Item item)
    {
        bool active = item switch
        {
            Item.Auto => Mode == ScrollMode.Auto,
            Item.Manual => Mode == ScrollMode.Manual,
            // 「完成」是这一条上的主按钮，常亮强调色 —— 和工具条上「当前工具」那枚同款
            Item.Accept => true,
            _ => false,
        };

        if (active)
            dc.DrawRoundedRectangle(_palette.AccentFill, _palette.AccentBorder, rect, ButtonRadius, ButtonRadius);
        else if (_hovered == item)
            dc.DrawRoundedRectangle(_palette.Hover, null, rect, ButtonRadius, ButtonRadius);

        var brush = active ? _palette.IconActive
            : item == Item.Cancel ? _palette.IconDanger
            : _palette.Icon;

        Icons.Draw(dc, IconOf(item), rect, brush, IconSize);
    }

    private static Geometry IconOf(Item item) => item switch
    {
        Item.Auto => Icons.ChevronsDown,
        Item.Manual => Icons.Hand,
        Item.Accept => Icons.Check,
        Item.Edit => Icons.Pencil,
        _ => Icons.Close,
    };

    private IEnumerable<(Item Item, Rect Rect)> Layout()
    {
        var panel = new Rect(ShadowPad, ShadowPad, PanelSize.Width, PanelSize.Height);

        double x = panel.X + PadX;
        double y = panel.Y + PadY;

        foreach (var item in new[] { Item.Auto, Item.Manual })
        {
            yield return (item, new Rect(x, y, ButtonSize, ButtonSize));
            x += ButtonSize + ButtonGap;
        }

        x += GroupGap - ButtonGap;

        foreach (var item in new[] { Item.Accept, Item.Edit, Item.Cancel })
        {
            yield return (item, new Rect(x, y, ButtonSize, ButtonSize));
            x += ButtonSize + ButtonGap;
        }
    }

    private Item? HitTest(Point local)
    {
        foreach (var (item, rect) in Layout())
            if (rect.Contains(local)) return item;
        return null;
    }

    // ---------------- 输入 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit == _hovered) return;

        _hovered = hit;
        InvalidateVisual();
        RaiseTip();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is null) return;

        _hovered = null;
        InvalidateVisual();
        RaiseTip();
    }

    /// <summary>把「当前悬停在哪枚按钮上」报给窗口，由它去摆那枚气泡。</summary>
    private void RaiseTip()
    {
        if (_hovered is not { } item)
        {
            TipChanged?.Invoke(Rect.Empty, string.Empty);
            return;
        }

        foreach (var (candidate, rect) in Layout())
        {
            if (candidate != item) continue;
            TipChanged?.Invoke(rect, TipOf(item));
            return;
        }
    }

    private static string TipOf(Item item) => item switch
    {
        Item.Auto => "自动滚动",
        Item.Manual => "手动滚动",
        Item.Accept => "完成  Enter",
        Item.Edit => "编辑长图",
        _ => "取消  Esc",
    };

    /// <summary>
    /// 按在按钮上就记账，按在别处就把面板拖走 —— 和之前用控件树时的分工一样：
    /// 按钮吃掉点击，空白处留给拖动。区域外面塞不下时面板会压在画面上，
    /// 而用户往往正需要把它挪开看清底下。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit is null)
        {
            try { Window.GetWindow(this)?.DragMove(); }
            catch (InvalidOperationException) { /* 按键已经松开了，没什么可拖的 */ }
            return;
        }

        _pressed = hit;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_pressed is not { } item) return;

        _pressed = null;
        ReleaseMouseCapture();

        // 拖到按钮外面再松手 = 反悔，和标准按钮一个脾气
        if (HitTest(e.GetPosition(this)) != item) return;

        switch (item)
        {
            case Item.Auto: PickMode(ScrollMode.Auto); break;
            case Item.Manual: PickMode(ScrollMode.Manual); break;
            case Item.Accept: Accepted?.Invoke(); break;
            case Item.Edit: EditRequested?.Invoke(); break;
            case Item.Cancel: Cancelled?.Invoke(); break;
        }
    }
}
