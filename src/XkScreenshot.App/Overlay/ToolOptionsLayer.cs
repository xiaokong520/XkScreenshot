using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Annotate;
using XkScreenshot.App.Ui;

namespace XkScreenshot.App.Overlay;

/// <summary>子工具栏里一次点击的含义。</summary>
public enum ToolOptionKind
{
    Thickness,
    FontSize,
    MosaicBlock,
    MosaicStyle,
    /// <summary>选一个预设色。</summary>
    Color,
    /// <summary>展开/收起色盘。</summary>
    ColorPicker,
}

/// <summary>点中了子工具栏上的哪一格。</summary>
public sealed record ToolOptionHit(ToolOptionKind Kind, int Index);

/// <summary>色盘弹层里可拖的两块区域。</summary>
public enum PickerArea
{
    None,
    /// <summary>饱和度/明度方块。</summary>
    Square,
    /// <summary>色相条。</summary>
    Hue,
}

/// <summary>
/// 主工具条下面那条子工具栏：当前工具的参数。
///
/// 单独一条而不是塞进主工具条：参数是随工具变的，塞进去会让主工具条时长时短，
/// 按钮位置跟着漂 —— 而主工具条上那几个按钮（复制、保存、取消）恰恰是最需要
/// 形成肌肉记忆的。分成两条之后，上面一条永远不动，变的只有下面一条。
/// </summary>
public sealed class ToolOptionsLayer : FrameworkElement
{
    private const double ChipSize = 26;
    private const double ChipGap = 3;
    private const double SwatchSize = 17;
    private const double GroupGap = 12;
    private const double PadX = 10;
    private const double PadY = 7;
    private const double CornerRadius = 10;
    private const double GapToToolbar = 6;
    private const double LabelFontSize = 11;
    private const double LabelGap = 7;

    // 色盘弹层
    private const double SquareWidth = 168;
    private const double SquareHeight = 112;
    private const double HueHeight = 13;
    private const double PickerGap = 9;
    private const double PreviewSize = 26;

    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ActiveBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0x3B, 0x9E, 0xFF)));
    private static readonly Pen ActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x3B, 0x9E, 0xFF)), 1));
    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE)));
    private static readonly Brush ActiveInkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xD4, 0xFF)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0x84, 0x91)));
    private static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen SwatchPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen SwatchActivePen = Freeze(new Pen(Brushes.White, 2));
    private static readonly Pen MarkerPen = Freeze(new Pen(Brushes.White, 2));
    private static readonly Pen MarkerShadePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3.5));
    private static readonly Typeface Face = new("Microsoft YaHei UI");

    /// <summary>色相条的彩虹渐变。六个角 + 收尾，少一个都会缺一段颜色。</summary>
    private static readonly Brush HueBrush = Freeze(new LinearGradientBrush(
    [
        new GradientStop(Color.FromRgb(0xFF, 0x00, 0x00), 0.0),
        new GradientStop(Color.FromRgb(0xFF, 0xFF, 0x00), 1 / 6.0),
        new GradientStop(Color.FromRgb(0x00, 0xFF, 0x00), 2 / 6.0),
        new GradientStop(Color.FromRgb(0x00, 0xFF, 0xFF), 3 / 6.0),
        new GradientStop(Color.FromRgb(0x00, 0x00, 0xFF), 4 / 6.0),
        new GradientStop(Color.FromRgb(0xFF, 0x00, 0xFF), 5 / 6.0),
        new GradientStop(Color.FromRgb(0xFF, 0x00, 0x00), 1.0),
    ], 0));

    private static readonly Brush WhiteFade = Freeze(new LinearGradientBrush(
        Colors.White, Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0));

    private static readonly Brush BlackFade = Freeze(new LinearGradientBrush(
        Color.FromArgb(0, 0, 0, 0), Colors.Black, 90));

    /// <summary>一组参数：一个标签 + 若干格子。</summary>
    private sealed record Group(string Label, ToolOptionKind Kind, int Count);

    private readonly List<(Rect Rect, ToolOptionHit Hit)> _hitBoxes = [];
    private readonly Dictionary<string, FormattedText> _labels = [];
    private double _labelPpd;

    public ToolOptionsLayer() => IsHitTestVisible = false;

    public FrostedBackdrop? Backdrop { get; set; }
    public bool Visible { get; set; }

    public ToolKind ActiveTool { get; set; } = ToolKind.None;
    public double Thickness { get; set; }
    public double FontSize { get; set; }
    public int MosaicBlock { get; set; }
    public MosaicStyle MosaicStyle { get; set; }
    public Color Color { get; set; }
    public Hsv Hsv { get; set; }
    public bool PickerOpen { get; set; }

    public Rect PanelRect { get; private set; }
    public Rect PickerRect { get; private set; }
    private Rect _squareRect;
    private Rect _hueRect;

    /// <summary>当前工具有没有参数可调。没有的话整条栏都不出现。</summary>
    public bool HasOptions => GroupsFor(ActiveTool).Length > 0;

    public void Refresh() => InvalidateVisual();

    /// <summary>
    /// 贴着主工具条摆。主工具条在选区下方时挂在它下面，翻到上方时也跟着翻，
    /// 始终位于「远离选区」的那一侧 —— 否则子工具栏会压在选区上，挡住刚画的东西。
    /// </summary>
    public void Layout(Rect toolbar, bool toolbarAbove)
    {
        if (!HasOptions)
        {
            PanelRect = Rect.Empty;
            PickerRect = Rect.Empty;
            return;
        }

        double w = MeasureWidth();
        double h = ChipSize + PadY * 2;
        double x = Math.Clamp(toolbar.Right - w, 0, Math.Max(0, ActualWidth - w));

        PanelRect = new Rect(x, StackOutward(toolbar, h, toolbarAbove), w, h);
        LayoutPicker(toolbarAbove);
    }

    private void LayoutPicker(bool above)
    {
        if (!PickerOpen)
        {
            PickerRect = Rect.Empty;
            return;
        }

        double w = SquareWidth + PadX * 2;
        double h = PadY * 2 + SquareHeight + PickerGap + HueHeight + PickerGap + PreviewSize;
        double x = Math.Clamp(PanelRect.Right - w, 0, Math.Max(0, ActualWidth - w));

        PickerRect = new Rect(x, StackOutward(PanelRect, h, above), w, h);

        _squareRect = new Rect(PickerRect.X + PadX, PickerRect.Y + PadY, SquareWidth, SquareHeight);
        _hueRect = new Rect(_squareRect.X, _squareRect.Bottom + PickerGap, SquareWidth, HueHeight);
    }

    /// <summary>把高度为 h 的一块叠在 anchor 的外侧；那一侧放不下就翻到另一侧。</summary>
    private double StackOutward(Rect anchor, double h, bool above)
    {
        double y = above ? anchor.Y - GapToToolbar - h : anchor.Bottom + GapToToolbar;
        if (y >= 0 && y + h <= ActualHeight) return y;

        double flipped = above ? anchor.Bottom + GapToToolbar : anchor.Y - GapToToolbar - h;
        if (flipped >= 0 && flipped + h <= ActualHeight) return flipped;

        return Math.Clamp(y, 0, Math.Max(0, ActualHeight - h));
    }

    /// <summary>点是否落在子工具栏或色盘上。落在上面的鼠标操作不能当成框选。</summary>
    public bool Contains(Point local)
        => Visible && (PanelRect.Contains(local) || (PickerOpen && PickerRect.Contains(local)));

    public ToolOptionHit? HitTest(Point local)
    {
        if (!Visible) return null;

        foreach (var (rect, hit) in _hitBoxes)
            if (rect.Contains(local)) return hit;

        return null;
    }

    /// <summary>色盘弹层的命中测试：拖拽取色要靠它分辨用户抓的是方块还是色相条。</summary>
    public PickerArea HitTestPicker(Point local)
    {
        if (!Visible || !PickerOpen) return PickerArea.None;
        if (_squareRect.Contains(local)) return PickerArea.Square;
        if (_hueRect.Contains(local)) return PickerArea.Hue;
        return PickerArea.None;
    }

    /// <summary>
    /// 把光标位置换算成颜色。坐标会被夹回区域内 —— 拖出边界还能继续调，
    /// 是取色器的基本手感，松手前一直跟着走。
    /// </summary>
    public Hsv PickAt(Point local, PickerArea area)
    {
        if (area == PickerArea.Hue)
        {
            double t = Math.Clamp((local.X - _hueRect.X) / _hueRect.Width, 0, 1);
            return Hsv with { H = t * 360 };
        }

        double s = Math.Clamp((local.X - _squareRect.X) / _squareRect.Width, 0, 1);
        double v = 1 - Math.Clamp((local.Y - _squareRect.Y) / _squareRect.Height, 0, 1);
        return Hsv with { S = s, V = v };
    }

    private static Group[] GroupsFor(ToolKind tool) => tool switch
    {
        ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Arrow or ToolKind.Ink =>
        [
            new("粗细", ToolOptionKind.Thickness, ToolOptions.Thicknesses.Length),
            ColorGroup,
        ],
        ToolKind.Text =>
        [
            new("字号", ToolOptionKind.FontSize, ToolOptions.FontSizes.Length),
            ColorGroup,
        ],
        // 马赛克没有颜色可言 —— 它取的就是画面本身的颜色
        ToolKind.Mosaic =>
        [
            new("方式", ToolOptionKind.MosaicStyle, 2),
            new("粒度", ToolOptionKind.MosaicBlock, ToolOptions.MosaicBlocks.Length),
        ],
        _ => [],
    };

    /// <summary>预设色后面多一格：那是打开色盘的入口。</summary>
    private static Group ColorGroup => new("颜色", ToolOptionKind.Color, ToolOptions.Palette.Length + 1);

    private double MeasureWidth()
    {
        var groups = GroupsFor(ActiveTool);
        double w = PadX * 2;

        for (int i = 0; i < groups.Length; i++)
        {
            if (i > 0) w += GroupGap;
            w += Label(groups[i].Label).Width + LabelGap;
            w += groups[i].Count * ChipSize + (groups[i].Count - 1) * ChipGap;
        }
        return w;
    }

    private FormattedText Label(string text)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Math.Abs(ppd - _labelPpd) > 0.001)
        {
            _labels.Clear();
            _labelPpd = ppd;
        }

        if (_labels.TryGetValue(text, out var cached)) return cached;

        var made = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, LabelFontSize, LabelBrush, ppd);
        _labels[text] = made;
        return made;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hitBoxes.Clear();
        if (!Visible || PanelRect.IsEmpty) return;

        PanelChrome.DrawGlassPanel(dc, PanelRect, CornerRadius, Backdrop, new Size(ActualWidth, ActualHeight));

        double x = PanelRect.X + PadX;
        double y = PanelRect.Y + PadY;
        var groups = GroupsFor(ActiveTool);

        for (int gi = 0; gi < groups.Length; gi++)
        {
            var group = groups[gi];
            if (gi > 0)
            {
                DrawSeparator(dc, x + GroupGap / 2 - ChipGap);
                x += GroupGap;
            }

            var label = Label(group.Label);
            dc.DrawText(label, new Point(x, PanelRect.Y + (PanelRect.Height - label.Height) / 2));
            x += label.Width + LabelGap;

            for (int i = 0; i < group.Count; i++)
            {
                var rect = new Rect(x, y, ChipSize, ChipSize);
                DrawChip(dc, rect, group.Kind, i);
                x += ChipSize + ChipGap;
            }
            x -= ChipGap;
        }

        if (PickerOpen) DrawPicker(dc);
    }

    private void DrawSeparator(DrawingContext dc, double x)
        => dc.DrawRectangle(SeparatorBrush, null,
            new Rect(Math.Round(x) + 0.5, PanelRect.Y + PadY + 4, 1, ChipSize - 8));

    private void DrawChip(DrawingContext dc, Rect rect, ToolOptionKind kind, int index)
    {
        // 颜色组最后一格是色盘入口，语义跟前面的预设色不同
        bool isPicker = kind == ToolOptionKind.Color && index == ToolOptions.Palette.Length;
        var hit = new ToolOptionHit(isPicker ? ToolOptionKind.ColorPicker : kind, index);
        _hitBoxes.Add((rect, hit));

        bool active = IsActive(kind, index, isPicker);
        if (active && !isPicker && kind != ToolOptionKind.Color)
            dc.DrawRoundedRectangle(ActiveBrush, ActivePen, rect, 7, 7);

        var ink = active ? ActiveInkBrush : InkBrush;

        switch (kind)
        {
            case ToolOptionKind.Thickness:
                DrawDot(dc, rect, ToolOptions.Thicknesses[index], ink);
                break;

            case ToolOptionKind.FontSize:
                DrawGlyph(dc, rect, index, ink);
                break;

            case ToolOptionKind.MosaicBlock:
                DrawGrain(dc, rect, index, ink);
                break;

            case ToolOptionKind.MosaicStyle:
                // 涂抹沿用画笔的图标：这两处是同一个手势（按住拖过去），
                // 换一个图标反而要让用户再认一次
                Icons.Draw(dc, index == 0 ? Icons.MosaicArea : Icons.Pencil, rect, ink, 18);
                break;

            case ToolOptionKind.Color when isPicker:
                DrawPickerChip(dc, rect, active);
                break;

            default:
                DrawSwatch(dc, rect, ToolOptions.Palette[index], active);
                break;
        }
    }

    private bool IsActive(ToolOptionKind kind, int index, bool isPicker) => kind switch
    {
        ToolOptionKind.Thickness => ToolOptions.IndexOf(ToolOptions.Thicknesses, Thickness) == index,
        ToolOptionKind.FontSize => ToolOptions.IndexOf(ToolOptions.FontSizes, FontSize) == index,
        ToolOptionKind.MosaicBlock => ToolOptions.IndexOf(ToolOptions.MosaicBlocks, MosaicBlock) == index,
        ToolOptionKind.MosaicStyle => (int)MosaicStyle == index,
        // 色盘那一格在「当前颜色不是任何预设」时点亮：自定义色总得有个去处显示
        ToolOptionKind.Color when isPicker => PickerOpen || ToolOptions.IndexOf(ToolOptions.Palette, Color) < 0,
        ToolOptionKind.Color => ToolOptions.IndexOf(ToolOptions.Palette, Color) == index,
        _ => false,
    };

    /// <summary>线宽用一个同等直径的实心圆表示，比写数字直观。</summary>
    private static void DrawDot(DrawingContext dc, Rect rect, double thickness, Brush brush)
    {
        // 直接按线宽画会让最细的那档小得看不清，压缩到 4~13 这个区间
        double d = 4 + thickness * 0.85;
        dc.DrawEllipse(brush, null,
            new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), d / 2, d / 2);
    }

    private void DrawGlyph(DrawingContext dc, Rect rect, int index, Brush brush)
    {
        double size = 9 + index * 3;
        var text = new FormattedText("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(
            rect.X + (rect.Width - text.Width) / 2,
            rect.Y + (rect.Height - text.Height) / 2));
    }

    /// <summary>粒度用格子密度表示：格子越大越糊。</summary>
    private static void DrawGrain(DrawingContext dc, Rect rect, int index, Brush brush)
    {
        int cells = 5 - index;                       // 5,4,3,2
        double box = 15;
        double cell = box / cells;
        double x0 = rect.X + (rect.Width - box) / 2;
        double y0 = rect.Y + (rect.Height - box) / 2;

        for (int r = 0; r < cells; r++)
        {
            for (int c = 0; c < cells; c++)
            {
                // 棋盘式填一半，格子的大小才看得出来 —— 全填满就只是一个方块
                if ((r + c) % 2 != 0) continue;
                dc.DrawRectangle(brush, null,
                    new Rect(x0 + c * cell, y0 + r * cell, cell, cell));
            }
        }
    }

    private static void DrawSwatch(DrawingContext dc, Rect rect, Color color, bool active)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        double inset = (rect.Width - SwatchSize) / 2;
        dc.DrawRoundedRectangle(brush, active ? SwatchActivePen : SwatchPen,
            new Rect(rect.X + inset, rect.Y + inset, SwatchSize, SwatchSize), 4, 4);
    }

    /// <summary>色盘入口画成一小块彩虹，不需要任何图标就能看懂。</summary>
    private void DrawPickerChip(DrawingContext dc, Rect rect, bool active)
    {
        double inset = (rect.Width - SwatchSize) / 2;
        var box = new Rect(rect.X + inset, rect.Y + inset, SwatchSize, SwatchSize);

        dc.DrawRoundedRectangle(HueBrush, active ? SwatchActivePen : SwatchPen, box, 4, 4);

        // 中间嵌一小格当前色：既是入口也是「现在是什么颜色」的读数
        var current = new SolidColorBrush(Color);
        current.Freeze();
        double d = SwatchSize / 2.4;
        dc.DrawEllipse(current, SwatchPen,
            new Point(box.X + box.Width / 2, box.Y + box.Height / 2), d / 2, d / 2);
    }

    private void DrawPicker(DrawingContext dc)
    {
        PanelChrome.DrawGlassPanel(dc, PickerRect, CornerRadius, Backdrop, new Size(ActualWidth, ActualHeight));

        // 饱和度/明度方块：纯色相打底，横向叠白、纵向叠黑
        var hue = new SolidColorBrush(new Hsv(Hsv.H, 1, 1).ToColor());
        hue.Freeze();
        dc.DrawRectangle(hue, null, _squareRect);
        dc.DrawRectangle(WhiteFade, null, _squareRect);
        dc.DrawRectangle(BlackFade, null, _squareRect);
        dc.DrawRectangle(null, SwatchPen, _squareRect);

        var marker = new Point(
            _squareRect.X + Math.Clamp(Hsv.S, 0, 1) * _squareRect.Width,
            _squareRect.Y + (1 - Math.Clamp(Hsv.V, 0, 1)) * _squareRect.Height);
        // 先描一圈半透明黑：浅色区域上的白圈单独放会看不见
        dc.DrawEllipse(null, MarkerShadePen, marker, 6, 6);
        dc.DrawEllipse(null, MarkerPen, marker, 6, 6);

        dc.DrawRectangle(HueBrush, null, _hueRect);
        dc.DrawRectangle(null, SwatchPen, _hueRect);

        double hx = _hueRect.X + Math.Clamp(Hsv.H, 0, 360) / 360 * _hueRect.Width;
        var slider = new Rect(hx - 3, _hueRect.Y - 2, 6, _hueRect.Height + 4);
        dc.DrawRoundedRectangle(null, MarkerShadePen, slider, 3, 3);
        dc.DrawRoundedRectangle(null, MarkerPen, slider, 3, 3);

        DrawReadout(dc);
    }

    /// <summary>底部一行：当前色 + 十六进制值。取色之后总要有个能抄走的读数。</summary>
    private void DrawReadout(DrawingContext dc)
    {
        var swatch = new Rect(_hueRect.X, _hueRect.Bottom + PickerGap, PreviewSize, PreviewSize);
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        dc.DrawRoundedRectangle(brush, SwatchPen, swatch, 5, 5);

        string hex = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", Color.R, Color.G, Color.B);
        var text = new FormattedText(hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face, 12.5, InkBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(swatch.Right + 9, swatch.Y + (swatch.Height - text.Height) / 2));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
