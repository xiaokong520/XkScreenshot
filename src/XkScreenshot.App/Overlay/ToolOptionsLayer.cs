using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XkScreenshot.Annotate;
using XkScreenshot.App.Ui;

namespace XkScreenshot.App.Overlay;

/// <summary>子工具栏里一次点击的含义。大小滑块不在其中，它走 <see cref="ToolOptionDrag"/>。</summary>
public enum ToolOptionKind
{
    MosaicStyle,
    /// <summary>选一个预设色。</summary>
    Color,
    /// <summary>展开/收起色盘。</summary>
    ColorPicker,
}

/// <summary>点中了子工具栏上的哪一格。</summary>
public sealed record ToolOptionHit(ToolOptionKind Kind, int Index);

/// <summary>子工具栏上按住能一路拖的那几块。</summary>
public enum ToolOptionDrag
{
    None,
    /// <summary>大小滑块。</summary>
    Size,
    /// <summary>色盘的饱和度/明度方块。</summary>
    PickerSquare,
    /// <summary>色盘的色相条。</summary>
    PickerHue,
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

    // 大小滑块
    private const double TrackWidth = 104;
    private const double TrackHeight = 4;
    private const double KnobRadius = 6.5;
    private const double ReadoutWidth = 26;
    private const double ReadoutGap = 8;
    private const double ReadoutFontSize = 12;

    // 色盘弹层
    private const double SquareWidth = 168;
    private const double SquareHeight = 112;
    private const double HueHeight = 13;
    private const double PickerGap = 9;
    private const double PreviewSize = 26;

    private static readonly Brush ActiveBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0x3B, 0x9E, 0xFF)));
    private static readonly Pen ActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x3B, 0x9E, 0xFF)), 1));
    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE)));
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF)));
    private static readonly Pen KnobPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)), 1));
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

    /// <summary>一组参数：一个标签，后面跟若干格子；Kind 为 null 表示跟的是一个滑块。</summary>
    private sealed record Group(string Label, ToolOptionKind? Kind, int Count);

    private readonly List<(Rect Rect, ToolOptionHit Hit)> _hitBoxes = [];
    private readonly Dictionary<string, FormattedText> _labels = [];
    private double _labelPpd;

    public ToolOptionsLayer() => IsHitTestVisible = false;

    public FrostedBackdrop? Backdrop { get; set; }
    public bool Visible { get; set; }

    /// <summary>子工具栏在为谁服务：选中的标注，或者当前工具。</summary>
    public ToolKind ActiveTool { get; set; } = ToolKind.None;

    /// <summary>滑块此刻调的是哪个数值，以及它的范围和当前值。</summary>
    public SizeOption Size { get; set; }
    public OptionRange SizeRange { get; set; } = ToolOptions.Thickness;
    public double SizeValue { get; set; }

    public MosaicStyle MosaicStyle { get; set; }
    public Color Color { get; set; }
    public Hsv Hsv { get; set; }
    public bool PickerOpen { get; set; }

    public Rect PanelRect { get; private set; }
    public Rect PickerRect { get; private set; }
    private Rect _squareRect;
    private Rect _hueRect;
    private Rect _trackRect;

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

    /// <summary>
    /// 按住能拖的那几块。滑块排在最前 —— 它跟色盘弹层不会重叠，先判谁都一样，
    /// 但顺序写死了才不用担心以后布局挪动时出现两块重合。
    /// </summary>
    public ToolOptionDrag HitTestDrag(Point local)
    {
        if (!Visible) return ToolOptionDrag.None;
        if (Size != SizeOption.None && SliderZone.Contains(local)) return ToolOptionDrag.Size;

        if (!PickerOpen) return ToolOptionDrag.None;
        if (_squareRect.Contains(local)) return ToolOptionDrag.PickerSquare;
        if (_hueRect.Contains(local)) return ToolOptionDrag.PickerHue;
        return ToolOptionDrag.None;
    }

    /// <summary>
    /// 滑块的可点区域比那条 4 像素的轨道高得多：轨道画细是为了好看，
    /// 但真让用户去点一条 4 像素的线，谁都得瞄准两次。
    /// </summary>
    private Rect SliderZone => _trackRect.IsEmpty
        ? Rect.Empty
        : new Rect(_trackRect.X - KnobRadius, PanelRect.Y + PadY,
            _trackRect.Width + KnobRadius * 2, ChipSize);

    /// <summary>
    /// 把光标位置换算成颜色。坐标会被夹回区域内 —— 拖出边界还能继续调，
    /// 是取色器的基本手感，松手前一直跟着走。
    /// </summary>
    public Hsv PickAt(Point local, ToolOptionDrag area)
    {
        if (area == ToolOptionDrag.PickerHue)
        {
            double t = Math.Clamp((local.X - _hueRect.X) / _hueRect.Width, 0, 1);
            return Hsv with { H = t * 360 };
        }

        double s = Math.Clamp((local.X - _squareRect.X) / _squareRect.Width, 0, 1);
        double v = 1 - Math.Clamp((local.Y - _squareRect.Y) / _squareRect.Height, 0, 1);
        return Hsv with { S = s, V = v };
    }

    /// <summary>把光标位置换算成滑块的值，同样夹回范围内。</summary>
    public double ValueAt(Point local)
        => _trackRect.Width <= 0
            ? SizeValue
            : SizeRange.At((local.X - _trackRect.X) / _trackRect.Width);

    private Group[] GroupsFor(ToolKind tool) => tool switch
    {
        ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Arrow or ToolKind.Ink or ToolKind.Text =>
            [SizeGroup, ColorGroup],
        // 马赛克没有颜色可言 —— 它取的就是画面本身的颜色
        ToolKind.Mosaic => [new("方式", ToolOptionKind.MosaicStyle, 2), SizeGroup],
        _ => [],
    };

    /// <summary>标签跟着当前工具变：粗细 / 字号 / 粒度，调的是同一个滑块。</summary>
    private Group SizeGroup => new(ToolOptions.LabelOf(Size), null, 0);

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
            w += groups[i].Kind is null
                ? TrackWidth + ReadoutGap + ReadoutWidth
                : groups[i].Count * ChipSize + (groups[i].Count - 1) * ChipGap;
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
        _trackRect = Rect.Empty;
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

            if (group.Kind is not { } kind)
            {
                DrawSlider(dc, x, y);
                x += TrackWidth + ReadoutGap + ReadoutWidth;
                continue;
            }

            for (int i = 0; i < group.Count; i++)
            {
                var rect = new Rect(x, y, ChipSize, ChipSize);
                DrawChip(dc, rect, kind, i);
                x += ChipSize + ChipGap;
            }
            x -= ChipGap;
        }

        if (PickerOpen) DrawPicker(dc);
    }

    /// <summary>
    /// 大小滑块：一条轨道 + 一个圆钮 + 一个数字读数。
    ///
    /// 数字是必需的而不是装饰 —— 连续可调的东西没有读数，用户既说不出「现在是几」，
    /// 也没法把这次的设置在下次复现出来。
    /// </summary>
    private void DrawSlider(DrawingContext dc, double x, double y)
    {
        double cy = y + ChipSize / 2;
        _trackRect = new Rect(x, cy - TrackHeight / 2, TrackWidth, TrackHeight);

        double r = TrackHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, _trackRect, r, r);

        double knobX = _trackRect.X + SizeRange.Fraction(SizeValue) * TrackWidth;
        if (knobX > _trackRect.X)
            dc.DrawRoundedRectangle(FillBrush, null,
                new Rect(_trackRect.X, _trackRect.Y, knobX - _trackRect.X, TrackHeight), r, r);

        dc.DrawEllipse(Brushes.White, KnobPen, new Point(knobX, cy), KnobRadius, KnobRadius);

        var readout = new FormattedText(
            SizeValue.ToString("0", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face, ReadoutFontSize, InkBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // 右对齐：数字从一位变两位时，左对齐会让整块读数跟着抖
        dc.DrawText(readout, new Point(
            x + TrackWidth + ReadoutGap + ReadoutWidth - readout.Width,
            cy - readout.Height / 2));
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
        ToolOptionKind.MosaicStyle => (int)MosaicStyle == index,
        // 色盘那一格在「当前颜色不是任何预设」时点亮：自定义色总得有个去处显示
        ToolOptionKind.Color when isPicker => PickerOpen || ToolOptions.IndexOf(ToolOptions.Palette, Color) < 0,
        ToolOptionKind.Color => ToolOptions.IndexOf(ToolOptions.Palette, Color) == index,
        _ => false,
    };

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
