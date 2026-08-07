using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XkScreenshot.App.Output;

/// <summary>
/// 把一段文本画成图片。
///
/// 贴图这条路只认位图，所以文本要贴出来就得先变成图 —— 换来的好处是缩放、拖动、
/// 复制、另存这些行为全都不用为文本再实现一遍。
/// </summary>
public static class TextImage
{
    private const double Padding = 16;
    private const double FontSize = 15;
    private const double LineSpacing = 1.5;

    /// <summary>贴图的最大尺寸（DIP）。再长的内容会被裁掉，底下给一行提示。</summary>
    private const double MaxWidth = 720;
    private const double MaxHeight = 900;
    private const double MinWidth = 120;

    /// <summary>裁字数只是给排版兜底：这个量级早就超出 MaxHeight 能显示的行数了。</summary>
    private const int MaxChars = 20000;

    private const double FooterSize = 12;
    private const double FooterGap = 8;
    private const string FooterText = "…… 内容过长，已截断";

    private static readonly FontFamily Family = new("Segoe UI, Microsoft YaHei UI");

    private static readonly Typeface Face =
        new(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush Background = Freeze(Brushes.White);
    private static readonly Brush Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23)));
    private static readonly Brush FooterBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)));

    /// <summary>
    /// 渲染文本；内容为空时返回 null。
    /// scale 是目标显示器的缩放倍率 —— 贴图按物理像素摆放，
    /// 固定按 96 DPI 出图的话，在 150% 的屏上字会小掉三分之一。
    /// </summary>
    public static BitmapSource? Render(string? raw, double scale)
    {
        string text = Normalize(raw);
        if (text.Length == 0) return null;

        scale = Math.Clamp(scale, 1.0, 4.0);

        double contentWidth = MaxWidth - Padding * 2;
        double contentHeight = MaxHeight - Padding * 2;

        var body = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, FontSize, Foreground, scale)
        {
            MaxTextWidth = contentWidth,
            Trimming = TextTrimming.None,
            LineHeight = FontSize * LineSpacing,
        };

        FormattedText? footer = null;
        if (body.Height > contentHeight)
        {
            footer = new FormattedText(
                FooterText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face, FooterSize, FooterBrush, scale);

            // 先量后裁：FormattedText 高度超出 MaxTextHeight 是直接切掉，不会自己加省略号，
            // 所以那行提示得我们自己补，否则用户不知道后面还有东西。
            body.MaxTextHeight = Math.Max(body.LineHeight, contentHeight - footer.Height - FooterGap);
        }

        double bodyWidth = Math.Ceiling(body.Width);
        double width = Math.Max(MinWidth, Math.Max(bodyWidth, footer?.Width ?? 0) + Padding * 2);
        double height = Math.Ceiling(body.Height)
                        + (footer is null ? 0 : FooterGap + Math.Ceiling(footer.Height))
                        + Padding * 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
            dc.DrawText(body, new Point(Padding, Padding));
            if (footer is not null)
                dc.DrawText(footer, new Point(Padding, Padding + Math.Ceiling(body.Height) + FooterGap));
        }

        var target = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// 制表符要展开成空格：WPF 的 DrawText 不认制表位，原样画出来所有对齐都会散掉。
    /// 结尾的空白也要去掉 —— 从网页或编辑器复制多半会带上几个换行，
    /// 留着就是贴图底下白出来一大片。
    /// </summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        if (text.Length > MaxChars) text = text[..MaxChars];

        // 行首缩进要留着（复制代码时那是内容的一部分），只掐掉最前面的空行
        return text.TrimEnd().TrimStart('\n');
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
