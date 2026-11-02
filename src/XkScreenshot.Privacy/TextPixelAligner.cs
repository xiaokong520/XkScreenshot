using System;
using System.Collections.Generic;
using System.Linq;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 用文字行的垂直投影校准字符边界。OCR 不返回单字框时，不能用整行平均字宽去切混排文本：
/// 邮箱里的 @、m、句点宽度不同，末尾的手机号就会切进前面的汉字。
/// 只在字符与笔画组的字宽匹配可靠时采用；严重粘连、复杂背景等保留原来的保守回退。
/// </summary>
internal static class TextPixelAligner
{
    private readonly record struct Run(int Left, int Right);

    public static bool TryAlign(OcrLine line, PixelBuffer image, out OcrLine aligned)
    {
        aligned = line;
        if (line.Words.Count > 0 || line.Bounds.IsEmpty || line.Text.Length is < 2 or > 256
            || line.Text.Any(c => char.IsSurrogate(c) || char.IsControl(c))) return false;
        var box = line.Bounds;
        var clipped = box.Intersect(new PixelRect(0, 0, image.Width, image.Height));
        if (clipped != box || box.Height is < 6 or > 128 || box.Width > 8192) return false;

        var characters = Enumerable.Range(0, line.Text.Length).Where(i => !char.IsWhiteSpace(line.Text[i])).ToArray();
        if (characters.Length < 2) return false;
        int background = AvatarFinder.DominantColor(image, box);
        var contrast = new int[box.Width];
        for (int y = box.Y; y < box.Bottom; y++)
        {
            int p = image.IndexOf(box.X, y);
            for (int x = 0; x < box.Width; x++, p += 4)
                contrast[x] = Math.Max(contrast[x],
                    AvatarFinder.Distance(image.Bgra[p], image.Bgra[p + 1], image.Bgra[p + 2], background));
        }
        // 用较实的笔画分组：低灰度抗锯齿常把相邻字粘在一起。输出时再补回淡边。
        var runs = new List<Run>();
        int begin = -1;
        for (int x = box.X; x < box.Right; x++)
        {
            bool ink = contrast[x - box.X] > 180;
            if (ink && begin < 0) begin = x;
            if (!ink && begin >= 0) { runs.Add(new Run(begin, x)); begin = -1; }
        }
        if (begin >= 0) runs.Add(new Run(begin, box.Right));
        int n = characters.Length, m = runs.Count;
        if (m < (n + 3) / 4 || m > n * 4) return false;

        // 一个汉字的偏旁可能分成多组；紧排的数字也可能粘成一组。
        // 只在实笔画组之间切分，不强行拆开实笔画，避免漏掉半个字符。
        double totalWeight = characters.Sum(i => WidthWeight(line.Text[i]));
        double unit = (runs[^1].Right - runs[0].Left) / totalWeight;
        if (unit < 2) return false;
        var costs = new double[n + 1, m + 1];
        var previous = new (int Character, int Run)[n + 1, m + 1];
        for (int c = 0; c <= n; c++)
        for (int r = 0; r <= m; r++) costs[c, r] = double.PositiveInfinity;
        costs[0, 0] = 0;
        for (int c = 0; c < n; c++)
        for (int r = 0; r < m; r++)
        {
            if (double.IsPositiveInfinity(costs[c, r])) continue;
            bool wide = LineSlicer.Weight(line.Text[characters[c]]) == 2;
            double weight = 0;
            for (int count = 1; count <= 4 && c + count <= n; count++)
            {
                int at = characters[c + count - 1];
                if ((LineSlicer.Weight(line.Text[at]) == 2) != wide
                    || (count > 1 && at != characters[c + count - 2] + 1)) break;
                weight += WidthWeight(line.Text[at]);
                int maxParts = count == 1 && wide ? 4 : 1;
                for (int parts = 1; parts <= maxParts && r + parts <= m; parts++)
                {
                    int end = r + parts;
                    if (m - end < (n - c - count + 3) / 4 || m - end > (n - c - count) * 4) continue;
                    double width = runs[end - 1].Right - runs[r].Left;
                    double leftGap = r == 0 ? 0 : runs[r].Left - runs[r - 1].Right;
                    double rightGap = end == m ? 0 : runs[end].Left - runs[end - 1].Right;
                    double advance = width + (Math.Min(leftGap, unit * 0.4) + Math.Min(rightGap, unit * 0.4)) / 2;
                    double delta = advance / unit - weight;
                    // 容差不能随分组字数放大，否则把三位数字当成四位也会通过，
                    // 并将号码首位错误地划到前面的普通文字里。
                    if (Math.Abs(delta) > 0.75) continue;
                    double cost = costs[c, r] + delta * delta / count + 0.04 * (count - 1);
                    if (cost >= costs[c + count, end]) continue;
                    costs[c + count, end] = cost;
                    previous[c + count, end] = (c, r);
                }
            }
        }
        if (double.IsPositiveInfinity(costs[n, m]) || costs[n, m] / n > 0.3) return false;

        var words = new OcrWord[n];
        int next = m, character = n;
        while (character > 0)
        {
            var (before, first) = previous[character, next];
            int left = runs[first].Left, right = runs[next - 1].Right;
            // 补回抗锯齿淡边，但绝不进入相邻字的实笔画。两个字的淡边粘连时允许
            // 共享间隙，以多盖至多一点淡边换取不漏字，不能把相邻整个汉字划进来。
            int limitLeft = first == 0 ? box.X : runs[first - 1].Right;
            int limitRight = next == m ? box.Right : runs[next].Left;
            while (left > limitLeft && contrast[left - 1 - box.X] > 24) left--;
            while (right < limitRight && contrast[right - box.X] > 24) right++;
            if (left - limitLeft >= 2) left--;
            if (limitRight - right >= 2) right++;
            var bounds = PixelRect.FromLtrb(left, box.Y, right, box.Bottom);
            for (int c = before; c < character; c++)
                words[c] = new OcrWord(line.Text[characters[c]].ToString(), bounds, 0);
            character = before;
            next = first;
        }
        aligned = line with { Words = words };
        return true;
    }

    private static double WidthWeight(char c) => LineSlicer.Weight(c) == 2 ? 2 : c switch
    {
        'i' or 'l' or 'I' or '.' or ',' or ':' or ';' or '!' or '\'' or '|' => 0.5,
        'f' or 't' or 'j' or '(' or ')' or '[' or ']' => 0.65,
        'm' or 'w' or 'M' or 'W' => 1.5,
        '@' => 1.8,
        _ => 1,
    };
}
