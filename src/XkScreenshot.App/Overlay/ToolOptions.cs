using System;
using System.Windows.Media;
using XkScreenshot.Annotate;

namespace XkScreenshot.App.Overlay;

/// <summary>马赛克的两种用法：框一块糊掉，或者像画笔一样涂过去。</summary>
public enum MosaicStyle
{
    /// <summary>拖出一个矩形，整块糊掉。</summary>
    Area,
    /// <summary>按住涂抹，笔迹经过的地方糊掉。</summary>
    Brush,
}

/// <summary>
/// 当前工具那个「可以调大小」的数值。
///
/// 每个工具只有一个，所以滚轮不需要先选调什么 —— 滚就是了。
/// </summary>
public enum SizeOption
{
    None,
    /// <summary>描边线宽。</summary>
    Thickness,
    /// <summary>文字字号。</summary>
    FontSize,
    /// <summary>马赛克块边长。</summary>
    MosaicBlock,
}

/// <summary>
/// 一个连续可调的数值：取值范围、滚轮步长、默认值。
///
/// 单位一律是「选区局部物理像素」，和标注坐标系一致。
/// </summary>
public readonly record struct OptionRange(double Min, double Max, double Step, double Default)
{
    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    /// <summary>
    /// 滚 notches 格。先把当前值对齐到步长网格再走，
    /// 否则从一个拖滑块拖出来的零头（比如 7.3）起步，滚一路都带着那个零头。
    /// </summary>
    public double Nudge(double value, int notches)
        => Clamp(Math.Round(Clamp(value) / Step) * Step + notches * Step);

    /// <summary>当前值在滑块上的位置，0~1。</summary>
    public double Fraction(double value) => (Clamp(value) - Min) / (Max - Min);

    /// <summary>滑块位置 → 值。取到步长的整数倍，读数才不会跳出一串小数。</summary>
    public double At(double fraction)
        => Clamp(Math.Round((Min + Math.Clamp(fraction, 0, 1) * (Max - Min)) / Step) * Step);
}

/// <summary>子工具栏里那些参数的取值范围与预设色。</summary>
public static class ToolOptions
{
    /// <summary>
    /// 线宽。上限给到 40：在 4K 屏上截图时，10 像素的线细得几乎看不见。
    /// </summary>
    public static readonly OptionRange Thickness = new(1, 40, 1, 4);

    /// <summary>字号。步长取 2 —— 一格一格滚过整个范围，一次滚动就能到头。</summary>
    public static readonly OptionRange FontSize = new(10, 96, 2, 20);

    /// <summary>马赛克块边长。越大越糊，也越看不出原内容。</summary>
    public static readonly OptionRange MosaicBlock = new(2, 60, 2, 12);

    /// <summary>
    /// 涂抹笔宽 = 块边长 × 它。
    ///
    /// 笔宽单独给一个滑块只会让马赛克的子工具栏比别的工具都长一截，而这两个值本来就该联动：
    /// 笔比块还细的话，涂一道下去只能盖住半个块，看起来像没生效。
    /// </summary>
    public const double MosaicBrushWidthRatio = 2.6;

    /// <summary>常用色。红色排第一 —— 标注的绝大多数场合都是圈重点。</summary>
    public static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30),
        Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x3B, 0x9E, 0xFF),
        Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0x1C, 0x1C, 0x1E),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    /// <summary>某个工具画出来的东西，「大小」指的是哪个数值。</summary>
    public static SizeOption SizeOf(ToolKind tool) => tool switch
    {
        ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Arrow or ToolKind.Ink => SizeOption.Thickness,
        ToolKind.Text => SizeOption.FontSize,
        ToolKind.Mosaic => SizeOption.MosaicBlock,
        _ => SizeOption.None,
    };

    public static OptionRange RangeOf(SizeOption option) => option switch
    {
        SizeOption.FontSize => FontSize,
        SizeOption.MosaicBlock => MosaicBlock,
        _ => Thickness,
    };

    public static string LabelOf(SizeOption option) => option switch
    {
        SizeOption.FontSize => "字号",
        SizeOption.MosaicBlock => "粒度",
        _ => "粗细",
    };

    /// <summary>找出颜色落在哪个预设上，用于高亮。找不到返回 -1，表示是色盘挑的自定义色。</summary>
    public static int IndexOf(Color[] colors, Color value) => Array.IndexOf(colors, value);
}
