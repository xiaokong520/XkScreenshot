using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Annotate;
using XkScreenshot.App.Ui;

namespace XkScreenshot.App.Overlay;

public enum ToolbarCommand
{
    None,
    /// <summary>退出当前标注工具，回到「拖拽 = 平移选区」。</summary>
    Select,
    /// <summary>删掉当前选中的那个标注。</summary>
    Delete,
    Undo,
    Redo,
    Pin,
    Copy,
    Save,
    Cancel,
}

/// <summary>工具条上的一个按钮：要么切换标注工具，要么触发一次命令。</summary>
public sealed record ToolbarItem(Geometry Icon, string Tip, ToolKind Tool, ToolbarCommand Command);

/// <summary>
/// 选区确定后浮在旁边的工具条。
///
/// 自绘 + 手工命中测试，而不是放真正的 WPF Button：一来能跟放大镜、提示面板共用
/// 同一套毛玻璃外观，二来覆盖层的鼠标事件是在 Window 级统一接管的，
/// 塞进控件树反而要处理路由事件被谁吃掉的问题。
/// </summary>
public sealed class ToolbarLayer : FrameworkElement
{
    private const double ButtonSize = 32;
    private const double IconSize = 18;
    private const double ButtonGap = 2;
    private const double GroupGap = 12;
    private const double PadX = 8;
    private const double PadY = 7;
    private const double CornerRadius = 10;
    private const double GapToSelection = 12;
    private const double TipFontSize = 11.5;

    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ActiveBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0x3B, 0x9E, 0xFF)));
    private static readonly Pen ActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x3B, 0x9E, 0xFF)), 1));
    private static readonly Brush IconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE)));
    private static readonly Brush ActiveIconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xD4, 0xFF)));
    /// <summary>
    /// 禁用态用「同色降透明度」，而不是一个固定的深灰。
    ///
    /// 面板是毛玻璃：底下截到什么，它就有多亮。白底下面板会亮到 90 上下，
    /// 而固定深灰恰好也在那个亮度，撞在一起之后按钮整个消失 —— 用户看到的是
    /// 分隔线右边空了一截，而不是「这几个按钮现在不可用」。
    /// 半透明白则永远是在面板自身的亮度上提一档，深底浅底都保得住对比。
    /// </summary>
    private static readonly Brush DisabledIconBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xE4, 0xE8, 0xEE)));
    private static readonly Brush DangerIconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x84)));
    private static readonly Brush TipBackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0x0E, 0x10, 0x14)));
    private static readonly Brush TipTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDA, 0xDF, 0xE6)));
    private static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface TipFace = new("Microsoft YaHei UI");

    /// <summary>工具与命令分成两组，中间隔一道分隔线，避免手滑点到「取消」。</summary>
    private static readonly ToolbarItem[] Tools =
    [
        // 「无工具」必须是一个看得见、点得着的按钮。否则选了矩形之后，
        // 拖拽全被判成画图，选区再也拖不动，用户会以为程序卡死了 ——
        // 光靠「再点一次同一个工具取消」这种隐藏规则是发现不了的。
        new(Icons.Cursor, "选择  拖动选区与标注", ToolKind.None, ToolbarCommand.Select),
        new(Icons.Square, "矩形", ToolKind.Rectangle, ToolbarCommand.None),
        new(Icons.Circle, "椭圆", ToolKind.Ellipse, ToolbarCommand.None),
        new(Icons.ArrowUpRight, "箭头", ToolKind.Arrow, ToolbarCommand.None),
        new(Icons.Pencil, "画笔", ToolKind.Ink, ToolbarCommand.None),
        new(Icons.Type, "文字", ToolKind.Text, ToolbarCommand.None),
        new(Icons.Grid, "马赛克", ToolKind.Mosaic, ToolbarCommand.None),
    ];

    private static readonly ToolbarItem[] Commands =
    [
        // 删除排在撤销旁边，且只在真的选中了标注时才亮。
        // 选中图形之后眼睛第一时间会往工具条上找「删掉它」，
        // 只给 Delete 键的话，不看提示面板的人根本不知道这个功能存在。
        new(Icons.Trash, "删除选中的标注  Delete", ToolKind.None, ToolbarCommand.Delete),
        new(Icons.Undo, "撤销  Ctrl+Z", ToolKind.None, ToolbarCommand.Undo),
        new(Icons.Redo, "重做  Ctrl+Y", ToolKind.None, ToolbarCommand.Redo),
        new(Icons.Pin, "贴图  Ctrl+T", ToolKind.None, ToolbarCommand.Pin),
        new(Icons.Close, "取消  Esc", ToolKind.None, ToolbarCommand.Cancel),
        // 保存和复制这两个「拿走成品」的动作排在最右：一条工具条从左看到右，
        // 依次是画什么、改什么、最后拿走，末尾这一格也是光标最容易甩到的位置
        new(Icons.Save, "保存  Ctrl+S", ToolKind.None, ToolbarCommand.Save),
        new(Icons.Copy, "复制  Enter", ToolKind.None, ToolbarCommand.Copy),
    ];

    private readonly List<(Rect Rect, ToolbarItem Item)> _hitBoxes = [];
    private ToolbarItem? _hovered;
    private Rect _hoveredRect;

    public ToolbarLayer() => IsHitTestVisible = false;

    public FrostedBackdrop? Backdrop { get; set; }
    public bool Visible { get; set; }
    public ToolKind ActiveTool { get; set; } = ToolKind.None;
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }

    /// <summary>有没有选中的标注可删。</summary>
    public bool CanDelete { get; set; }
    public Rect PanelRect { get; private set; }

    /// <summary>工具条是不是翻到了选区上方。子工具栏据此决定往哪一侧叠。</summary>
    public bool PlacedAbove { get; private set; }

    public void Refresh() => InvalidateVisual();

    /// <summary>
    /// 按选区位置摆放工具条：优先放在选区正下方，下面不够就翻到上方，
    /// 上下都不够（选区几乎占满整屏）就压在选区内部的底边。
    /// </summary>
    public void Layout(Rect selectionLocal)
    {
        double w = MeasureWidth();
        double h = ButtonSize + PadY * 2;

        double x = Math.Clamp(selectionLocal.Right - w, 0, Math.Max(0, ActualWidth - w));
        double y = selectionLocal.Bottom + GapToSelection;
        PlacedAbove = false;

        if (y + h > ActualHeight)
        {
            double above = selectionLocal.Top - GapToSelection - h;
            PlacedAbove = above >= 0;
            y = PlacedAbove ? above : Math.Max(0, ActualHeight - h - GapToSelection);
        }

        PanelRect = new Rect(x, y, w, h);
    }

    /// <summary>本窗口局部 DIP 坐标的按钮命中测试。null 表示没点在任何按钮上。</summary>
    public ToolbarItem? HitTest(Point local)
    {
        foreach (var (rect, item) in _hitBoxes)
            if (rect.Contains(local)) return item;
        return null;
    }


    /// <summary>点是否落在工具条面板上。落在上面的鼠标操作不能当成框选。</summary>
    public bool Contains(Point local) => Visible && PanelRect.Contains(local);

    /// <summary>更新悬停状态；有变化时返回 true，调用方据此决定要不要重绘。</summary>
    public bool UpdateHover(Point local)
    {
        var hit = Visible ? HitTest(local) : null;
        if (ReferenceEquals(hit, _hovered)) return false;
        _hovered = hit;
        return true;
    }

    private static double MeasureWidth()
    {
        double tools = Tools.Length * ButtonSize + (Tools.Length - 1) * ButtonGap;
        double commands = Commands.Length * ButtonSize + (Commands.Length - 1) * ButtonGap;
        return PadX * 2 + tools + GroupGap + commands;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hitBoxes.Clear();
        if (!Visible || PanelRect.IsEmpty) return;

        PanelChrome.DrawGlassPanel(dc, PanelRect, CornerRadius, Backdrop, new Size(ActualWidth, ActualHeight));

        double x = PanelRect.X + PadX;
        double y = PanelRect.Y + PadY;

        foreach (var item in Tools)
        {
            var rect = new Rect(x, y, ButtonSize, ButtonSize);
            DrawButton(dc, rect, item, active: ActiveTool == item.Tool, enabled: true);
            _hitBoxes.Add((rect, item));
            x += ButtonSize + ButtonGap;
        }

        x += GroupGap - ButtonGap;
        DrawSeparator(dc, x - GroupGap / 2);

        foreach (var item in Commands)
        {
            bool enabled = item.Command switch
            {
                ToolbarCommand.Delete => CanDelete,
                ToolbarCommand.Undo => CanUndo,
                ToolbarCommand.Redo => CanRedo,
                _ => true,
            };

            var rect = new Rect(x, y, ButtonSize, ButtonSize);
            DrawButton(dc, rect, item, active: false, enabled);
            if (enabled) _hitBoxes.Add((rect, item));
            x += ButtonSize + ButtonGap;
        }

        if (_hovered is not null)
            DrawTip(dc, _hovered);
    }

    private void DrawSeparator(DrawingContext dc, double x)
        => dc.DrawRectangle(SeparatorBrush, null,
            new Rect(Math.Round(x) + 0.5, PanelRect.Y + PadY + 5, 1, ButtonSize - 10));

    private void DrawButton(DrawingContext dc, Rect rect, ToolbarItem item, bool active, bool enabled)
    {
        bool hovered = enabled && ReferenceEquals(item, _hovered);

        if (active)
            dc.DrawRoundedRectangle(ActiveBrush, ActivePen, rect, 7, 7);
        else if (hovered)
            dc.DrawRoundedRectangle(HoverBrush, null, rect, 7, 7);

        var brush = !enabled ? DisabledIconBrush
            : active ? ActiveIconBrush
            : item.Command == ToolbarCommand.Cancel ? DangerIconBrush
            : IconBrush;

        Icons.Draw(dc, item.Icon, rect, brush, IconSize);

        if (hovered) _hoveredRect = rect;
    }

    private void DrawTip(DrawingContext dc, ToolbarItem item)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(item.Tip, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TipFace, TipFontSize, TipTextBrush, ppd);

        double w = text.Width + 16;
        double h = text.Height + 9;
        double x = Math.Clamp(_hoveredRect.X + (_hoveredRect.Width - w) / 2, 0, Math.Max(0, ActualWidth - w));

        // 气泡默认在工具条上方；上方放不下就翻到下方
        double y = PanelRect.Y - h - 7;
        if (y < 0) y = Math.Min(PanelRect.Bottom + 7, Math.Max(0, ActualHeight - h));

        var bubble = new Rect(x, y, w, h);
        PanelChrome.DrawShadow(dc, bubble, 6);
        dc.DrawRoundedRectangle(TipBackBrush, null, bubble, 6, 6);
        dc.DrawText(text, new Point(x + 8, y + 4.5));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
