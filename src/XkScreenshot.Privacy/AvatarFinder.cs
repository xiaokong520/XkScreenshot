using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>一个认定的头像。<paramref name="Lines"/> 是紧挨着它的那几行字（找名字要用），Background 是它周围的底色。</summary>
internal sealed record Avatar(PixelRect Bounds, IReadOnlyList<int> Lines, int Background, bool Above);

/// <summary>
/// 找头像 —— 包括人脸检测抓不到的那些：卡通、风景、字母头像、Logo。
///
/// 单看像素认不出一张图是不是头像，但头像在界面里的摆法几乎是固定的：
/// 聊天、评论、邮件列表里，它是名字左边一块方方正正的图；个人主页里，它在名字正上方。
/// 所以这里不在整张图上盲找，而是以每行字为锚点，只看它左边和上边那一小块。
///
/// 误判的主要来源是图标。区分靠两点：图标颜色少、线条稀（填充率低），头像颜色杂、
/// 是实心的一整块；另外列表里的头像会一行一个、同一列、同样大小地重复出现 ——
/// 这个特征强到连字母头像这种颜色很少的也能靠它认出来。
/// </summary>
internal static class AvatarFinder
{
    /// <summary>与底色差多少算前景（三个通道差值之和）。</summary>
    private const int ForegroundDelta = 40;

    /// <summary>够「杂」才算图片：4 位量化后的颜色数和亮度标准差。纯色图标、按钮过不了这一关。</summary>
    private const int RichColors = 48;
    private const double RichLumaStd = 20;

    private const int MinSide = 16;
    private const int MaxSideLeft = 128;
    private const int MaxSideAbove = 180;

    /// <summary>成列头像的边长上限。比 <see cref="MaxSideLeft"/> 宽一些：表情包、大图头像能到 150 上下。</summary>
    private const int MaxSideColumn = 160;

    /// <summary>
    /// 成列头像的最小边长。
    ///
    /// 比「行旁边」那条路的 16 严：那边有名字和间距佐证，这边只靠「同列同大小」。
    /// 实测 21~24px 的成对小块是 VS Code 窗口角落的图标，一次就被当成了两个头像。
    /// 头像在各家界面里最小也有 32px 上下（100% 缩放），28 能挡住图标又不伤真头像。
    /// </summary>
    private const int MinSideColumn = 28;

    /// <summary>成列头像至少要几块才算数。单个孤立方块不算 —— 图标、按钮、插图都是单个的。</summary>
    private const int MinColumnHits = 2;

    /// <summary>候选块没有对应的文字行（<see cref="Side.Column"/> 找到的那种）。</summary>
    private const int NoLine = -1;

    /// <summary>
    /// 候选块里至少要有这么多像素与周围底色不同。
    ///
    /// 原来卡 0.35，实测把浅色主题下的头像全挡掉了：白底卡通头像大面积留白，
    /// 只有头发和脸算前景，45×45 的框里只占 0.30 出头 —— 而它跟旁边昵称行、
    /// 同列重复出现的证据都很齐全。门槛降到 0.22 让这种头像进候选，
    /// 是否真的接受仍由后面的「颜色够杂 / 实心 / 旁边是名字 / 同列重复」几关决定。
    /// </summary>
    private const double MinFill = 0.22;

    /// <summary>候选块在文字行的哪一侧，或来自「成列出现」这条路径。自己的消息头像是右对齐的，不能只往左边找。</summary>
    private enum Side
    {
        Left,
        Right,
        Above,

        /// <summary>整张图上成列出现的方形块，没有文字行可以锚定。</summary>
        Column,
    }

    private sealed record Candidate(PixelRect Bounds, int Line, int Background, bool Rich, bool Solid, Side Side);

    public static List<Avatar> Find(IReadOnlyList<OcrLine> lines, PixelBuffer image, IReadOnlyList<PixelRect> faces, CancellationToken ct = default)
    {
        var found = new List<Candidate>();

        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int h = lines[i].Bounds.Height;
            if (h < 8 || h > 96) continue;

            if (FindBeside(image, lines[i].Bounds, i, Side.Left) is { } left) found.Add(left);
            if (FindBeside(image, lines[i].Bounds, i, Side.Right) is { } right) found.Add(right);
            if (FindBeside(image, lines[i].Bounds, i, Side.Above) is { } top) found.Add(top);
        }

        // 文字行之外还有一条路：整张图上成列出现的方形块。
        // 图片消息、表情包旁边没有可识别的文字，只靠上面那圈锚点永远找不到它们的头像。
        AddColumns(image, found, ct);

        // 同一个头像会被好几行字各找到一次（名字那行、消息那行），先按位置归并
        var groups = new List<List<Candidate>>();
        foreach (var c in found)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups.FirstOrDefault(g => IoU(g[0].Bounds, c.Bounds) > 0.5);
            if (group is null) groups.Add([c]);
            else group.Add(c);
        }

        var accepted = new bool[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[g];
            var first = group[0];
            bool face = faces.Any(f => Overlaps(f, first.Bounds));
            bool repeated = first.Side == Side.Left
                            && groups.Count(o => o != group && o[0].Side == Side.Left && SameColumn(o[0].Bounds, first.Bounds)) >= 1;
            bool rich = group.Any(c => c.Rich);
            bool solid = group.Any(c => c.Solid);
            bool nameBeside = group.Any(c => c.Line >= 0 && NameFinder.LooksLikeName(lines[c.Line].Text));

            accepted[g] = face
                          || first.Side == Side.Column
                          || (first.Side == Side.Right && rich)
                          || (repeated && (rich || (solid && nameBeside)))
                          || (rich && nameBeside);
        }

        // 第二遍：黑白头像、双色的字母头像颜色太少，过不了「杂」那一关，
        // 但它跟同一列里已经认定的头像一样大、一样靠左 —— 同一个列表里摆着的，就是头像。
        for (int g = 0; g < groups.Count; g++)
        {
            if (accepted[g] || groups[g][0].Side != Side.Left) continue;
            for (int o = 0; o < groups.Count; o++)
            {
                if (o == g || !accepted[o] || groups[o][0].Side != Side.Left
                    || !SameColumn(groups[o][0].Bounds, groups[g][0].Bounds)) continue;
                accepted[g] = true;
                break;
            }
        }

        var result = new List<Avatar>();
        for (int g = 0; g < groups.Count; g++)
        {
            if (!accepted[g]) continue;
            var first = groups[g][0];
            result.Add(new Avatar(first.Bounds,
                groups[g].Select(c => c.Line).Where(i => i >= 0).Distinct().ToList(),
                first.Background, first.Side == Side.Above));
        }

        return result;
    }

    /// <summary>同一列、同样大小：x 和边长都差不出 3 像素。</summary>
    private static bool SameColumn(PixelRect a, PixelRect b)
        => Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

    /// <summary>同列的块是不是等距排开（间距的标准差不到均值的 10%）。</summary>
    private static bool EvenlySpaced(List<PixelRect> boxes)
    {
        var tops = boxes.Select(b => b.Y).OrderBy(y => y).ToList();
        if (tops.Count < 3) return false;

        var gaps = new List<int>();
        for (int i = 1; i < tops.Count; i++) gaps.Add(tops[i] - tops[i - 1]);

        double mean = gaps.Average();
        if (mean <= 0) return true;

        double variance = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count;
        return Math.Sqrt(variance) / mean < 0.1;
    }

    /// <summary>
    /// 整张图上成列出现的方形块 —— 头像最稳的通用特征。
    ///
    /// 聊天、评论、私信、通知列表里头像都长这样：同一列、同样大小、一串排下去。
    /// 这条线索跟文字无关，所以图片消息、表情包、语音条旁边没有可识别的字也能找到；
    /// 反过来单看一块方形图片认不出是不是头像，所以要求同列至少两块。
    /// 只在左右两条边带里找：版面中间那些等大的方形图块（相册网格、文件缩略图）
    /// 不该被当成头像；真在中间的列表自然有文字锚点那条路兜着。
    /// </summary>
    private static void AddColumns(PixelBuffer image, List<Candidate> found, CancellationToken ct)
    {
        int bandWidth = Math.Max(64, image.Width / 4);
        var bands = new[]
        {
            (Rect: new PixelRect(0, 0, bandWidth, image.Height), Right: false),
            (Rect: new PixelRect(image.Width - bandWidth, 0, bandWidth, image.Height), Right: true),
        };

        foreach (var band in bands)
        {
            ct.ThrowIfCancellationRequested();
            int background = DominantColor(image, band.Rect);
            var squares = new List<(PixelRect Box, bool Rich, bool Solid)>();

            foreach (var (box, count) in Blobs(image, band.Rect, background))
            {
                int side = Math.Max(box.Width, box.Height);
                if (side < MinSideColumn || side > MaxSideColumn) continue;
                // 方形要求比「行旁边」那条路更严：这里没有名字、没有间距可以佐证
                if (Math.Min(box.Width, box.Height) < side * 0.85) continue;

                double fill = (double)count / box.Area;
                if (fill < MinFill) continue;
                squares.Add((box, IsRich(image, box), fill >= 0.6));
            }

            var taken = new bool[squares.Count];
            for (int i = 0; i < squares.Count; i++)
            {
                if (taken[i]) continue;

                var column = new List<int> { i };
                for (int j = i + 1; j < squares.Count; j++)
                    if (!taken[j] && SameColumn(squares[i].Box, squares[j].Box)) column.Add(j);

                if (column.Count < MinColumnHits) continue;

                // 等距排开的一列更像导航栏/工具栏的图标列表：菜单项高度是设计死的，
                // 聊天消息的高度随内容变，间距不会这么齐。等距的名单（联系人、群成员）
                // 依然有名字行那条路兜着，这里放掉不会真漏。
                if (column.Count >= 3
                    && EvenlySpaced(column.Select(k => squares[k].Box).ToList())) continue;

                // 只有两块、又紧贴着图片左边缘：网页/应用的左侧工具栏就是「图标 + 标签」
                // 两两成列（首页/教程/FAQ/分享），光凭方块认不出来。头像离边缘总留一点
                // 内边距（实测 3% 宽），工具栏图标是贴着窗口边画的（不到 1%）。
                // 左边这种两块宁可漏 —— 手动补两块，比整条侧栏被糊掉容易收拾。
                if (column.Count == 2 && !band.Right && squares[column[0]].Box.X <= image.Width / 50) continue;

                foreach (int k in column)
                {
                    taken[k] = true;
                    var (box, rich, solid) = squares[k];
                    found.Add(new Candidate(box, NoLine, background, rich, solid, Side.Column));
                }
            }
        }
    }

    private static bool Overlaps(PixelRect face, PixelRect box)
    {
        int cx = face.X + face.Width / 2, cy = face.Y + face.Height / 2;
        return box.Contains(new PixelPoint(cx, cy)) || IoU(face, box) > 0.3;
    }

    internal static double IoU(PixelRect a, PixelRect b)
    {
        var inter = a.Intersect(b);
        if (inter.IsEmpty) return 0;
        double union = a.Area + b.Area - inter.Area;
        return union <= 0 ? 0 : inter.Area / union;
    }

    /// <summary>
    /// 在一行字的左边、右边（或正上方）找一块像头像的东西。
    /// 找法：取这片区域的主色当底色，把跟底色明显不同的像素连成块，
    /// 再挑出大小、形状、位置都对得上的那一块。
    /// </summary>
    private static Candidate? FindBeside(PixelBuffer image, PixelRect line, int index, Side side)
    {
        bool above = side == Side.Above;
        int h = line.Height;
        int gap = (int)(h * 1.5) + 2;
        int minSide = Math.Max(MinSide, above ? h * 2 : (int)(h * 1.2));
        int maxSide = above ? Math.Min(MaxSideAbove, h * 6) : Math.Min(MaxSideLeft, h * 5);
        if (maxSide < minSide) return null;

        var imageRect = new PixelRect(0, 0, image.Width, image.Height);
        int centerX = line.X + line.Width / 2;
        var region = side switch
        {
            Side.Above => PixelRect.FromLtrb(centerX - maxSide, line.Y - gap - maxSide - 2, centerX + maxSide, line.Y),
            // 自己的消息头像是右对齐的；右边那一块之外还要留出气泡到这个头像的间距
            Side.Right => PixelRect.FromLtrb(line.Right, line.Y - maxSide, line.Right + gap + maxSide + 2, line.Bottom + maxSide),
            _ => PixelRect.FromLtrb(line.X - gap - maxSide - 2, line.Y - maxSide, line.X, line.Bottom + maxSide),
        };
        region = region.Intersect(imageRect);
        if (region.Width < minSide || region.Height < minSide) return null;

        int background = DominantColor(image, region);
        var boxes = Blobs(image, region, background);

        Candidate? best = null;
        double bestGap = double.MaxValue;

        foreach (var (box, count) in boxes)
        {
            int size = Math.Max(box.Width, box.Height);
            if (size < minSide || size > maxSide) continue;
            if (Math.Min(box.Width, box.Height) < size * 0.8) continue;

            // 被区域边界截断的块，多半是一整块更大的东西（气泡、卡片、横幅）露出来的一角 ——
            // 除非截断它的是图片本身的边缘
            if (TouchesCut(box, region, imageRect)) continue;

            double fill = (double)count / box.Area;
            if (fill < MinFill) continue;

            double distance;
            if (above)
            {
                int boxCenter = box.X + box.Width / 2;
                if (Math.Abs(boxCenter - centerX) > size * 0.3) continue;
                distance = line.Y - box.Bottom;
            }
            else
            {
                if (box.Y >= line.Bottom || box.Bottom <= line.Y) continue;
                distance = side == Side.Right ? box.X - line.Right : line.X - box.Right;
            }
            if (distance < -2 || distance > gap) continue;

            if (distance < bestGap)
            {
                bestGap = distance;
                best = new Candidate(box, index, background, IsRich(image, box), fill >= 0.6, side);
            }
        }

        return best;
    }

    private static bool TouchesCut(PixelRect box, PixelRect region, PixelRect image)
        => (box.X <= region.X && region.X > image.X)
           || (box.Y <= region.Y && region.Y > image.Y)
           || (box.Right >= region.Right && region.Right < image.Right)
           || (box.Bottom >= region.Bottom && region.Bottom < image.Bottom);

    /// <summary>区域里出现最多的颜色（4 位量化后取那一格的实际均值），当作底色。打包成 0xRRGGBB。</summary>
    internal static int DominantColor(PixelBuffer image, PixelRect region)
    {
        var counts = new int[4096];
        var data = image.Bgra;

        for (int y = region.Y; y < region.Bottom; y++)
        {
            int i = image.IndexOf(region.X, y);
            for (int x = region.X; x < region.Right; x++, i += 4)
                counts[Bin(data[i], data[i + 1], data[i + 2])]++;
        }

        int mode = Array.IndexOf(counts, counts.Max());
        long sb = 0, sg = 0, sr = 0, n = 0;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            int i = image.IndexOf(region.X, y);
            for (int x = region.X; x < region.Right; x++, i += 4)
            {
                if (Bin(data[i], data[i + 1], data[i + 2]) != mode) continue;
                sb += data[i]; sg += data[i + 1]; sr += data[i + 2]; n++;
            }
        }

        return n == 0 ? 0 : (int)(sr / n) << 16 | (int)(sg / n) << 8 | (int)(sb / n);
    }

    private static int Bin(byte b, byte g, byte r) => (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);

    internal static int Distance(byte b, byte g, byte r, int packed)
        => Math.Abs(r - (packed >> 16 & 0xFF)) + Math.Abs(g - (packed >> 8 & 0xFF)) + Math.Abs(b - (packed & 0xFF));

    /// <summary>
    /// 前景连通块的外框和像素数。
    ///
    /// 头像跟底色相近的地方（白底上的白衣服）会把一张图切成几块，
    /// 所以连通之后再把挨得很近的外框并起来 —— 否则一个头像会被拆成几个小碎块，个个都不够大。
    /// </summary>
    private static List<(PixelRect Box, int Count)> Blobs(PixelBuffer image, PixelRect region, int background)
    {
        int w = region.Width, h = region.Height;
        var mask = new bool[w * h];
        var data = image.Bgra;

        for (int y = 0; y < h; y++)
        {
            int i = image.IndexOf(region.X, region.Y + y);
            for (int x = 0; x < w; x++, i += 4)
                mask[y * w + x] = Distance(data[i], data[i + 1], data[i + 2], background) > ForegroundDelta;
        }

        var blobs = new List<(PixelRect Box, int Count)>();
        var seen = new bool[w * h];
        var stack = new Stack<int>();

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            stack.Push(start);
            seen[start] = true;

            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                count++;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;

                if (px > 0) Visit(p - 1);
                if (px < w - 1) Visit(p + 1);
                if (py > 0) Visit(p - w);
                if (py < h - 1) Visit(p + w);
            }

            if (count >= 4)
                blobs.Add((PixelRect.FromLtrb(region.X + minX, region.Y + minY, region.X + maxX + 1, region.Y + maxY + 1), count));
        }

        return MergeNear(blobs);

        void Visit(int q)
        {
            if (!mask[q] || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }
    }

    private static List<(PixelRect Box, int Count)> MergeNear(List<(PixelRect Box, int Count)> blobs)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < blobs.Count && !merged; i++)
            {
                var grown = Inflate(blobs[i].Box, 2);
                for (int j = i + 1; j < blobs.Count; j++)
                {
                    if (!grown.IntersectsWith(blobs[j].Box)) continue;

                    var a = blobs[i].Box; var b = blobs[j].Box;
                    blobs[i] = (PixelRect.FromLtrb(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                        Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)), blobs[i].Count + blobs[j].Count);
                    blobs.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }
        return blobs;
    }

    internal static PixelRect Inflate(PixelRect r, int by)
        => PixelRect.FromLtrb(r.X - by, r.Y - by, r.Right + by, r.Bottom + by);

    /// <summary>够不够「像一张图」：颜色种类多、明暗有起伏。</summary>
    private static bool IsRich(PixelBuffer image, PixelRect box)
    {
        var bins = new HashSet<int>();
        double sum = 0, sumSq = 0;
        long n = 0;
        var data = image.Bgra;

        for (int y = box.Y; y < box.Bottom; y++)
        {
            int i = image.IndexOf(box.X, y);
            for (int x = box.X; x < box.Right; x++, i += 4)
            {
                byte b = data[i], g = data[i + 1], r = data[i + 2];
                bins.Add(Bin(b, g, r));
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                sum += luma;
                sumSq += luma * luma;
                n++;
            }
        }

        if (n == 0) return false;
        double mean = sum / n;
        double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return bins.Count >= RichColors && std >= RichLumaStd;
    }
}
