using System;
using System.Windows.Media;

namespace XkScreenshot.App.Ui;

/// <summary>
/// 色相/饱和度/明度。取色盘要用它而不是 RGB：
/// 色盘的两个维度（横向饱和度、纵向明度）在 RGB 里根本不是独立的轴。
///
/// 另外它必须作为状态存下来，不能每次从当前 RGB 反推：明度拉到 0 时颜色一律是黑，
/// 色相信息当场丢失，再往上拉就会跳回红色 —— 用户会以为色盘坏了。
/// </summary>
/// <param name="H">色相，0~360 度。</param>
/// <param name="S">饱和度，0~1。</param>
/// <param name="V">明度，0~1。</param>
public readonly record struct Hsv(double H, double S, double V)
{
    public Color ToColor()
    {
        double h = ((H % 360) + 360) % 360 / 60;
        double s = Math.Clamp(S, 0, 1);
        double v = Math.Clamp(V, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public static Hsv FromColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = d < 1e-6 ? 0
            : max == r ? 60 * (((g - b) / d + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);

        return new Hsv(h, max < 1e-6 ? 0 : d / max, max);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
