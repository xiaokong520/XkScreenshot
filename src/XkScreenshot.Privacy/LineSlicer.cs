using System;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 把一行里的某段字符换算成这行框里的一截。
///
/// OCR 只给到行框（RapidOcrNet 不吐单字位置），而「邮箱：foo@bar.com」这种行
/// 只该糊后半截 —— 整行糊掉，看图的人连那里原本是邮箱都不知道。
/// 屏幕上的字宽很规整：汉字、全角符号占两个单位，其余占一个，按比例切就八九不离十。
/// 切歪的那一点靠左右多留大半个字补上 —— 宁可多盖一点，也不能露半个字母出来。
/// </summary>
internal static class LineSlicer
{
    /// <summary>左右各外扩多少个「单位字宽」。比例字体里 m、W 比 i、l 宽出一倍多，要靠它兜住。</summary>
    private const double PadUnits = 0.8;

    public static PixelRect Slice(OcrLine line, int start, int length)
    {
        // 有真实词框时优先使用，不能把不等宽的标签、名字和号码全按行宽平分。
        // 当前离线引擎只有行框；像素校准会补出字符框，其他引擎也可提供真实词框。
        if (length <= 0) return PixelRect.Empty;
        int cursor = 0;
        var result = PixelRect.Empty;
        foreach (var word in line.Words)
        {
            if (string.IsNullOrEmpty(word.Text) || word.Bounds.IsEmpty) break;
            int at = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (at < 0 || !string.IsNullOrWhiteSpace(line.Text[cursor..at])) break;
            int end = at + word.Text.Length;
            if (at < start + length && start < end)
            {
                var part = Slice(word.Text, word.Bounds, Math.Max(0, start - at),
                    Math.Min(end, start + length) - Math.Max(at, start));
                result = result.IsEmpty ? part : PixelRect.FromLtrb(Math.Min(result.X, part.X), Math.Min(result.Y, part.Y),
                    Math.Max(result.Right, part.Right), Math.Max(result.Bottom, part.Bottom));
            }
            cursor = end;
            if (cursor >= start + length && !result.IsEmpty) return result;
        }
        return Slice(line.Text, line.Bounds, start, length);
    }

    public static PixelRect Slice(string text, PixelRect bounds, int start, int length)
    {
        if (text.Length == 0 || bounds.IsEmpty || length <= 0) return PixelRect.Empty;

        start = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + length, start, text.Length);
        if (start == 0 && end == text.Length) return bounds;

        double total = 0, before = 0, inside = 0;
        for (int i = 0; i < text.Length; i++)
        {
            double w = Weight(text[i]);
            total += w;
            if (i < start) before += w;
            else if (i < end) inside += w;
        }

        double unit = bounds.Width / total;
        double pad = unit * PadUnits;

        int left = (int)Math.Floor(bounds.X + before * unit - pad);
        int right = (int)Math.Ceiling(bounds.X + (before + inside) * unit + pad);
        left = Math.Max(left, bounds.X);
        right = Math.Min(right, bounds.Right);

        return right <= left ? bounds : PixelRect.FromLtrb(left, bounds.Y, right, bounds.Bottom);
    }

    /// <summary>东亚宽字符（汉字、假名、谚文、全角符号）算两个单位，其余一个。</summary>
    public static double Weight(char c) => IsWide(c) ? 2 : 1;

    // 用码位而不是字面字符：CJK 兼容区的字一经 Unicode 规范化就会变成别的码位，
    // 区间端点悄悄挪走却完全看不出来
    private static bool IsWide(char c) => (int)c switch
    {
        >= 0x1100 and <= 0x115F => true, // 谚文字母
        >= 0x2E80 and <= 0xA4CF => true, // 汉字、假名、注音、CJK 标点
        >= 0xAC00 and <= 0xD7A3 => true, // 谚文音节
        >= 0xF900 and <= 0xFAFF => true, // CJK 兼容汉字
        >= 0xFE30 and <= 0xFE4F => true, // CJK 兼容标点
        >= 0xFF00 and <= 0xFF60 => true, // 全角 ASCII
        >= 0xFFE0 and <= 0xFFE6 => true, // 全角符号
        _ => false,
    };
}
