using System;
using System.Windows.Media;

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
/// 子工具栏里那些参数的档位表。
///
/// 一律用离散档位而不是连续滑块：截图标注是「几秒钟内做完」的事，
/// 滑块要求用户先瞄准再微调，而这几档已经覆盖了实际会用到的全部范围。
/// 档位还让「当前选的是哪一档」一眼可见 —— 滑块只能靠位置去猜。
/// </summary>
public static class ToolOptions
{
    /// <summary>线宽（选区局部物理像素）。</summary>
    public static readonly double[] Thicknesses = [2, 4, 7, 11];

    public static readonly double[] FontSizes = [14, 20, 28, 40];

    /// <summary>马赛克块边长。越大越糊，也越看不出原内容。</summary>
    public static readonly int[] MosaicBlocks = [6, 12, 20, 32];

    /// <summary>
    /// 涂抹笔宽 = 块边长 × 它。
    ///
    /// 笔宽单独给一档只会让马赛克的子工具栏比别的工具都长一截，而这两个值本来就该联动：
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

    /// <summary>找出 value 落在哪一档，用于高亮当前档位。找不到返回 -1。</summary>
    public static int IndexOf(double[] steps, double value)
    {
        for (int i = 0; i < steps.Length; i++)
            if (Math.Abs(steps[i] - value) < 0.001) return i;

        return -1;
    }

    public static int IndexOf(int[] steps, int value) => Array.IndexOf(steps, value);

    public static int IndexOf(Color[] colors, Color value) => Array.IndexOf(colors, value);
}
