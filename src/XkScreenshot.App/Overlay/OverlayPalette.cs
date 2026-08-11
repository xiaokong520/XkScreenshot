using System.Windows.Media;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 覆盖层上那几块浮动面板的配色：提示面板、工具条、子工具栏、放大镜。
///
/// 单独一份而不是各层各写各的，是因为它们贴在一起：工具条底下就挂着子工具栏，
/// 差一档色就看得出来是两块拼上去的。选区遮罩和标注不在这里 ——
/// 遮罩的职责是把选区外压暗，它跟主题无关。
///
/// 面板底色不是画上去的一块纯色，而是「模糊背景 + 一层底调」叠出来的，
/// 所以浅色主题除了换画刷，还得把背景本身的亮度往上抬，见
/// <see cref="BackdropScale"/>。
/// </summary>
public sealed class OverlayPalette
{
    /// <summary>
    /// 毛玻璃背景的亮度重映射：v * Scale + Lift，最后再夹到 0~255。
    ///
    /// 深色主题只压暗（Lift = 0）；浅色主题得连压带抬 —— 光抬不压的话，
    /// 底下截到一块黑图时面板还是黑的，上面的深色文字直接消失。
    /// </summary>
    public required double BackdropScale { get; init; }

    /// <inheritdoc cref="BackdropScale"/>
    public required double BackdropLift { get; init; }

    // ---------------- 面板本体 ----------------

    /// <summary>投影的单层颜色，会叠好几层。</summary>
    public required Brush PanelShadow { get; init; }

    /// <summary>压在模糊背景上的底调。</summary>
    public required Brush PanelTint { get; init; }

    /// <summary>模糊背景不可用时的兜底底色。</summary>
    public required Brush PanelOpaque { get; init; }

    public required Pen PanelBorder { get; init; }

    /// <summary>沿顶边的一道内高光，玻璃的立体感基本全靠它。</summary>
    public required Pen PanelTopHighlight { get; init; }

    // ---------------- 文字与图标 ----------------

    public required Brush Text { get; init; }
    public required Brush TextSecondary { get; init; }

    /// <summary>标题、单位、辅助说明这一类不该抢眼的字。</summary>
    public required Brush TextMuted { get; init; }

    public required Brush Icon { get; init; }

    /// <summary>
    /// 禁用态用「同色降透明度」，而不是一个固定的灰。
    ///
    /// 面板是毛玻璃：底下截到什么，它就有多亮。固定灰总有一档亮度会和面板撞在一起，
    /// 按钮整个消失 —— 用户看到的是分隔线右边空了一截，而不是「这几个按钮不可用」。
    /// 降透明度则永远是在面板自身的亮度上让一档，深底浅底都保得住对比。
    /// </summary>
    public required Brush IconDisabled { get; init; }

    /// <summary>取消按钮那一个。</summary>
    public required Brush IconDanger { get; init; }

    /// <summary>选中的工具那一个。</summary>
    public required Brush IconActive { get; init; }

    // ---------------- 强调与交互态 ----------------

    public required Brush Accent { get; init; }

    /// <summary>选中态按钮的底。</summary>
    public required Brush AccentFill { get; init; }

    /// <summary>选中态按钮的描边。</summary>
    public required Pen AccentBorder { get; init; }

    public required Brush Hover { get; init; }
    public required Brush Separator { get; init; }

    /// <summary>提示面板标题下那条发丝线。</summary>
    public required Pen Rule { get; init; }

    // ---------------- 键帽 ----------------

    public required Brush ChipFill { get; init; }
    public required Pen ChipBorder { get; init; }

    /// <summary>键帽底边那道暗线，没有它键帽就是个扁框。</summary>
    public required Pen ChipShade { get; init; }

    // ---------------- 滑块 ----------------

    public required Brush SliderTrack { get; init; }
    public required Pen SliderKnobBorder { get; init; }

    // ---------------- 气泡提示 ----------------

    public required Brush TipFill { get; init; }
    public required Brush TipText { get; init; }

    // ---------------- 取色相关 ----------------

    /// <summary>放大镜取景框的外框。</summary>
    public required Pen ViewBorder { get; init; }

    /// <summary>色块、预设色格子的描边。</summary>
    public required Pen SwatchBorder { get; init; }

    /// <summary>选中的那一格色块的描边，比上面那条粗一倍。</summary>
    public required Pen SwatchActiveBorder { get; init; }

    public static readonly OverlayPalette Dark = new()
    {
        BackdropScale = 0.72,
        BackdropLift = 0,

        PanelShadow = Fill(0x14, 0x00, 0x00, 0x00),
        // 刻意做得比较淡：背景在构建时已经压暗过一档，这里再重手压就会把玻璃感盖没
        PanelTint = Fill(0x8E, 0x12, 0x14, 0x1A),
        PanelOpaque = Fill(0xF2, 0x15, 0x16, 0x1A),
        PanelBorder = Line(0x2E, 0xFF, 0xFF, 0xFF),
        PanelTopHighlight = Line(0x1E, 0xFF, 0xFF, 0xFF),

        Text = Fill(0xFF, 0xEC, 0xEF, 0xF3),
        TextSecondary = Fill(0xFF, 0xB4, 0xBA, 0xC4),
        TextMuted = Fill(0xFF, 0x6E, 0x76, 0x82),
        Icon = Fill(0xFF, 0xE4, 0xE8, 0xEE),
        IconDisabled = Fill(0x70, 0xE4, 0xE8, 0xEE),
        IconDanger = Fill(0xFF, 0xFF, 0x8A, 0x84),
        IconActive = Fill(0xFF, 0xA8, 0xD4, 0xFF),

        Accent = Fill(0xFF, 0x3B, 0x9E, 0xFF),
        AccentFill = Fill(0x44, 0x3B, 0x9E, 0xFF),
        AccentBorder = Line(0xB0, 0x3B, 0x9E, 0xFF),
        Hover = Fill(0x26, 0xFF, 0xFF, 0xFF),
        Separator = Fill(0x28, 0xFF, 0xFF, 0xFF),
        Rule = Line(0x18, 0xFF, 0xFF, 0xFF),

        ChipFill = Fill(0x22, 0xFF, 0xFF, 0xFF),
        ChipBorder = Line(0x38, 0xFF, 0xFF, 0xFF),
        ChipShade = Line(0x40, 0x00, 0x00, 0x00),

        SliderTrack = Fill(0x3A, 0xFF, 0xFF, 0xFF),
        SliderKnobBorder = Line(0x70, 0x00, 0x00, 0x00),

        TipFill = Fill(0xEE, 0x0E, 0x10, 0x14),
        TipText = Fill(0xFF, 0xDA, 0xDF, 0xE6),

        ViewBorder = Line(0x30, 0xFF, 0xFF, 0xFF),
        SwatchBorder = Line(0x66, 0xFF, 0xFF, 0xFF),
        SwatchActiveBorder = Line(0xFF, 0xFF, 0xFF, 0xFF, 2),
    };

    /// <summary>
    /// 浅色那一套。不是把深色的每个值取反 —— 半透明白叠在浅底上几乎没有效果，
    /// 所以「提亮一档」的那几处（悬停、分隔线、键帽）在这边一律换成半透明黑。
    /// </summary>
    public static readonly OverlayPalette Light = new()
    {
        // 抬到 140 打底：底下截到纯黑时面板也还有个浅灰的样子
        BackdropScale = 0.45,
        BackdropLift = 140,

        // 比深色那套重一点：浅面板压在同样浅的截图上，全靠这圈影子分层
        PanelShadow = Fill(0x1E, 0x00, 0x00, 0x00),
        PanelTint = Fill(0x96, 0xF7, 0xF8, 0xFA),
        PanelOpaque = Fill(0xF2, 0xF6, 0xF7, 0xF9),
        PanelBorder = Line(0x30, 0x00, 0x00, 0x00),
        PanelTopHighlight = Line(0x66, 0xFF, 0xFF, 0xFF),

        Text = Fill(0xFF, 0x1B, 0x1E, 0x23),
        TextSecondary = Fill(0xFF, 0x45, 0x4B, 0x54),
        TextMuted = Fill(0xFF, 0x7A, 0x82, 0x8E),
        Icon = Fill(0xFF, 0x22, 0x26, 0x2C),
        IconDisabled = Fill(0x70, 0x22, 0x26, 0x2C),
        IconDanger = Fill(0xFF, 0xC6, 0x2B, 0x22),
        IconActive = Fill(0xFF, 0x1B, 0x74, 0xD4),

        // 浅底上强调色压深一档，和设置界面用的是同一个值
        Accent = Fill(0xFF, 0x1B, 0x74, 0xD4),
        AccentFill = Fill(0x33, 0x1B, 0x74, 0xD4),
        AccentBorder = Line(0xB0, 0x1B, 0x74, 0xD4),
        Hover = Fill(0x18, 0x00, 0x00, 0x00),
        Separator = Fill(0x22, 0x00, 0x00, 0x00),
        Rule = Line(0x14, 0x00, 0x00, 0x00),

        ChipFill = Fill(0x18, 0x00, 0x00, 0x00),
        ChipBorder = Line(0x33, 0x00, 0x00, 0x00),
        ChipShade = Line(0x20, 0x00, 0x00, 0x00),

        SliderTrack = Fill(0x26, 0x00, 0x00, 0x00),
        SliderKnobBorder = Line(0x50, 0x00, 0x00, 0x00),

        TipFill = Fill(0xF7, 0xFC, 0xFC, 0xFD),
        TipText = Fill(0xFF, 0x1B, 0x1E, 0x23),

        ViewBorder = Line(0x40, 0x00, 0x00, 0x00),
        SwatchBorder = Line(0x66, 0x00, 0x00, 0x00),
        SwatchActiveBorder = Line(0xFF, 0x1B, 0x1E, 0x23, 2),
    };

    public static OverlayPalette For(bool dark) => dark ? Dark : Light;

    private static Brush Fill(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen Line(byte a, byte r, byte g, byte b, double thickness = 1)
    {
        var pen = new Pen(Fill(a, r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
