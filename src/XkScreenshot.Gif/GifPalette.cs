namespace XkScreenshot.Gif;

/// <summary>
/// 整段录制共用的一张调色板（GIF 的全局色表），索引 0 留给透明。
///
/// 为什么是全局而不是每帧一张：每帧自带色表会让同一种颜色在不同帧里落到不同的索引，
/// 播放时背景和文字会轻微「跳色」，而且每帧多出 768 字节的色表。屏幕内容本来色数就少，
/// 拿整段画面统计一次足够了。
///
/// 两条路：
/// - 采样到的颜色不超过 255 种 —— 直接照搬。界面截图绝大多数走这条，出来是像素级无损的。
/// - 超过 —— 按 5 位 RGB 统计直方图，再中位切分出 255 个代表色。视频、照片走这条，
///   必然有损失，这是 GIF 这个格式的天花板，不是实现的选择。
/// </summary>
internal sealed class GifPalette
{
    /// <summary>采样上限。再多也不会让调色板变准，只是让按下停止到出图之间多等一会儿。</summary>
    private const int MaxSamples = 300_000;

    /// <summary>精确色统计的上限：超过它就不必再数了，反正一定会走量化。</summary>
    private const int MaxExactColors = 512;

    private readonly byte[] _red;
    private readonly byte[] _green;
    private readonly byte[] _blue;

    /// <summary>精确色 → 调色板索引（1 起）。只有走「照搬」那条路时才有值。</summary>
    private readonly Dictionary<int, byte>? _exact;

    /// <summary>
    /// 5 位 RGB → 最近索引的查表已经是旧方案了：每通道先把颜色截到 32 档再找最近色，
    /// 等于在 256 色之上又压了一刀，照片会明显发闷。现在按 6 位（64 档）建表，
    /// 额外误差从每通道最多 7 级降到 3 级，内存 256 KB，建表也只要几十毫秒。
    /// </summary>
    private readonly byte[] _lut;

    /// <summary>LUT 每通道的位数。</summary>
    private const int LutBits = 6;
    private const int LutSize = 1 << (LutBits * 3);
    private const int LutMask = (1 << LutBits) - 1;
    private const int LutShift = 8 - LutBits;

    private GifPalette(List<(byte R, byte G, byte B)> colors, Dictionary<int, byte>? exact, bool isFlat)
    {
        if (colors.Count == 0) colors.Add((0, 0, 0));
        if (colors.Count > 255) colors.RemoveRange(255, colors.Count - 255);

        Count = colors.Count;

        TableSize = 2;
        while (TableSize < Count + 1) TableSize <<= 1;

        _red = new byte[Count];
        _green = new byte[Count];
        _blue = new byte[Count];

        // 索引 0 是透明位，色表里给它留一个纯黑占位；真正的颜色从 1 开始
        Table = new byte[TableSize * 3];
        for (int i = 0; i < Count; i++)
        {
            var (r, g, b) = colors[i];
            _red[i] = r;
            _green[i] = g;
            _blue[i] = b;

            int o = (i + 1) * 3;
            Table[o] = r;
            Table[o + 1] = g;
            Table[o + 2] = b;
        }

        _exact = exact;
        IsFlat = isFlat;
        _lut = BuildLut();
    }

    /// <summary>实际颜色数（不含透明占位）。</summary>
    public int Count { get; }

    /// <summary>色表是不是精确的（画面里的颜色都被原样收下了）。精确色表不该做抖动。</summary>
    public bool IsExact => _exact is not null;

    /// <summary>
    /// 画面是不是以大片平涂为主（界面截图）。
    ///
    /// 抖动只对照片/视频有意义：界面的大块纯色被抖动只会变成噪点，文件还要大好几倍。
    /// 判据是采样像素里「和自己左边一个像素完全相同」的比例。实测：深色 IDE 截图约 94%，
    /// 一张大面积渐变的壁纸约 64%，带皮肤/头发的视频帧通常更低。阈值取 80%：
    /// 再低会把渐变壁纸也误判成平涂，再高则文本截图会漏掉。
    /// </summary>
    public bool IsFlat { get; }

    /// <summary>取某个索引（1 起）对应的 RGB。抖动要用它算量化误差。</summary>
    public (byte R, byte G, byte B) ColorOf(byte index)
    {
        int i = index - 1;
        if ((uint)i >= (uint)Count) return (0, 0, 0);
        return (_red[i], _green[i], _blue[i]);
    }

    /// <summary>色表长度，一定是 2 的幂：GIF 的色表只能按 2、4、8 …… 256 写。</summary>
    public int TableSize { get; }

    /// <summary>RGB 三元组，长度 <see cref="TableSize"/> × 3。</summary>
    public byte[] Table { get; }

    /// <summary>LZW 起始码宽。GIF 规定最小是 2，哪怕只有两种颜色。</summary>
    public int MinCodeSize
    {
        get
        {
            int bits = 1;
            while ((1 << bits) < TableSize) bits++;
            return Math.Max(2, bits);
        }
    }

    public static GifPalette Build(GifRecording recording, int maxColors, CancellationToken ct)
    {
        maxColors = Math.Clamp(maxColors, 2, 255);

        long totalPixels = 0;
        foreach (var frame in recording.Frames)
            totalPixels += (long)frame.Width * frame.Height;

        // 按总量定步长：两百帧的录制和两帧的录制采样点数相当，调色板质量与耗时都稳定
        int step = (int)Math.Clamp(totalPixels / MaxSamples, 1, 64);

        var histogram = new int[32768];
        var exact = new Dictionary<int, int>(MaxExactColors);
        bool tooMany = false;

        // 平涂判据：采样点里和左边像素完全相同的比例
        long neighborPairs = 0;
        long neighborEqual = 0;

        foreach (var frame in recording.Frames)
        {
            ct.ThrowIfCancellationRequested();

            var pixels = frame.Pixels;
            int count = frame.Width * frame.Height;

            for (int i = 0; i < count; i += step)
            {
                int o = i * 4;
                byte b = pixels[o];
                byte g = pixels[o + 1];
                byte r = pixels[o + 2];

                if (i > 0 && i % frame.Width != 0)
                {
                    int left = (i - 1) * 4;
                    neighborPairs++;
                    if (pixels[o] == pixels[left]
                        && pixels[o + 1] == pixels[left + 1]
                        && pixels[o + 2] == pixels[left + 2])
                        neighborEqual++;
                }

                histogram[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)]++;

                if (tooMany) continue;

                int rgb = (r << 16) | (g << 8) | b;
                if (exact.TryGetValue(rgb, out int times))
                {
                    exact[rgb] = times + 1;
                }
                else if (exact.Count >= MaxExactColors)
                {
                    tooMany = true;
                }
                else
                {
                    exact[rgb] = 1;
                }
            }
        }

        bool isFlat = neighborPairs > 0 && neighborEqual * 100 >= neighborPairs * 80;

        if (!tooMany && exact.Count <= maxColors)
        {
            var colors = exact
                .OrderByDescending(p => p.Value)
                .Select(p => (
                    R: (byte)((p.Key >> 16) & 0xFF),
                    G: (byte)((p.Key >> 8) & 0xFF),
                    B: (byte)(p.Key & 0xFF)))
                .ToList();

            var index = new Dictionary<int, byte>(colors.Count);
            for (int i = 0; i < colors.Count; i++)
                index[(colors[i].R << 16) | (colors[i].G << 8) | colors[i].B] = (byte)(i + 1);

            return new GifPalette(colors, index, isFlat);
        }

        return new GifPalette(MedianCut(histogram, maxColors, ct), null, isFlat);
    }

    /// <summary>把一个 BGRA 像素映射到调色板索引。返回 1..Count，绝不会是 0（0 是透明）。</summary>
    public byte IndexOf(uint bgra)
        => IndexOf((int)((bgra >> 16) & 0xFF), (int)((bgra >> 8) & 0xFF), (int)(bgra & 0xFF));

    /// <summary>把 8 位 RGB 映射到调色板索引。抖动会先改颜色再调这个重载。</summary>
    public byte IndexOf(int r, int g, int b)
    {
        if (_exact is not null)
        {
            int rgb = (r << 16) | (g << 8) | b;
            if (_exact.TryGetValue(rgb, out byte hit)) return hit;
        }

        return _lut[((r >> LutShift) << (LutBits * 2)) | ((g >> LutShift) << LutBits) | (b >> LutShift)];
    }

    private byte[] BuildLut()
    {
        var lut = new byte[LutSize];

        for (int key = 0; key < lut.Length; key++)
        {
            int r = (key >> (LutBits * 2)) & LutMask;
            int g = (key >> LutBits) & LutMask;
            int b = key & LutMask;

            // 6 位扩回 8 位要复制高位：31→255 的那套写法在这里是 63→255
            lut[key] = Nearest(
                (r << LutShift) | (r >> (LutBits - 2)),
                (g << LutShift) | (g >> (LutBits - 2)),
                (b << LutShift) | (b >> (LutBits - 2)));
        }

        return lut;
    }

    private byte Nearest(int r, int g, int b)
    {
        int best = 1;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < Count; i++)
        {
            int dr = r - _red[i];
            int dg = g - _green[i];
            int db = b - _blue[i];
            int distance = dr * dr + dg * dg + db * db;

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i + 1;
            if (distance == 0) break;
        }

        return (byte)best;
    }

    // ---------------- 中位切分 ----------------

    private readonly record struct Entry(byte R, byte G, byte B, int Count);

    private sealed class Box
    {
        public Box(int start, int length, Entry[] entries)
        {
            Start = start;
            Length = length;

            byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            int total = 0;

            for (int i = start; i < start + length; i++)
            {
                var e = entries[i];
                if (e.R < minR) minR = e.R;
                if (e.R > maxR) maxR = e.R;
                if (e.G < minG) minG = e.G;
                if (e.G > maxG) maxG = e.G;
                if (e.B < minB) minB = e.B;
                if (e.B > maxB) maxB = e.B;
                total += e.Count;
            }

            MinR = minR; MaxR = maxR;
            MinG = minG; MaxG = maxG;
            MinB = minB; MaxB = maxB;
            Count = total;
        }

        public int Start { get; }
        public int Length { get; }
        public int Count { get; }
        public byte MinR { get; }
        public byte MaxR { get; }
        public byte MinG { get; }
        public byte MaxG { get; }
        public byte MinB { get; }
        public byte MaxB { get; }

        public int WidestChannel()
        {
            int r = MaxR - MinR;
            int g = MaxG - MinG;
            int b = MaxB - MinB;

            if (r >= g && r >= b) return 0;
            return g >= b ? 1 : 2;
        }

        public int Range => Math.Max(MaxR - MinR, Math.Max(MaxG - MinG, MaxB - MinB));
    }

    private static List<(byte R, byte G, byte B)> MedianCut(int[] histogram, int maxColors, CancellationToken ct)
    {
        var entries = new List<Entry>();
        for (int key = 0; key < histogram.Length; key++)
        {
            if (histogram[key] == 0) continue;

            int r5 = (key >> 10) & 31;
            int g5 = (key >> 5) & 31;
            int b5 = key & 31;

            entries.Add(new Entry(
                (byte)((r5 << 3) | (r5 >> 2)),
                (byte)((g5 << 3) | (g5 >> 2)),
                (byte)((b5 << 3) | (b5 >> 2)),
                histogram[key]));
        }

        if (entries.Count == 0) return [(0, 0, 0)];

        var array = entries.ToArray();
        var boxes = new List<Box> { new(0, array.Length, array) };

        while (boxes.Count < maxColors)
        {
            ct.ThrowIfCancellationRequested();

            // 挑「跨度 × 像素数」最大的那一箱：又宽又常用的颜色区间最值得再分一次
            int pick = -1;
            double bestScore = 0;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Length < 2 || box.Range == 0) continue;

                double score = (double)box.Range * box.Count;
                if (score <= bestScore) continue;
                bestScore = score;
                pick = i;
            }

            if (pick < 0) break;

            var chosen = boxes[pick];
            Split(array, chosen, out var left, out var right);
            boxes[pick] = left;
            boxes.Add(right);
        }

        var colors = new List<(byte, byte, byte)>(boxes.Count);
        var seen = new HashSet<int>();

        foreach (var box in boxes)
        {
            long r = 0, g = 0, b = 0, total = 0;
            for (int i = box.Start; i < box.Start + box.Length; i++)
            {
                var e = array[i];
                r += (long)e.R * e.Count;
                g += (long)e.G * e.Count;
                b += (long)e.B * e.Count;
                total += e.Count;
            }

            if (total == 0) continue;

            byte cr = (byte)Math.Clamp((int)((r + total / 2) / total), 0, 255);
            byte cg = (byte)Math.Clamp((int)((g + total / 2) / total), 0, 255);
            byte cb = (byte)Math.Clamp((int)((b + total / 2) / total), 0, 255);

            // 两个箱平均出同一个颜色是可能的，重复项只会白占表项
            if (!seen.Add((cr << 16) | (cg << 8) | cb)) continue;
            colors.Add((cr, cg, cb));
        }

        if (colors.Count == 0) colors.Add((0, 0, 0));
        return colors;
    }

    private static void Split(Entry[] entries, Box box, out Box left, out Box right)
    {
        int channel = box.WidestChannel();

        Comparison<Entry> comparison = channel switch
        {
            0 => static (a, b) => a.R.CompareTo(b.R),
            1 => static (a, b) => a.G.CompareTo(b.G),
            _ => static (a, b) => a.B.CompareTo(b.B),
        };

        Array.Sort(entries, box.Start, box.Length, Comparer<Entry>.Create(comparison));

        // 按「像素数过半」切，而不是按表项数过半：一大片纯色比几十种零星噪点更该被完整保留
        int half = Math.Max(1, box.Count / 2);
        int accumulated = 0;
        int split = box.Start + 1;

        for (int i = box.Start; i < box.Start + box.Length - 1; i++)
        {
            accumulated += entries[i].Count;
            split = i + 1;
            if (accumulated >= half) break;
        }

        left = new Box(box.Start, split - box.Start, entries);
        right = new Box(split, box.Start + box.Length - split, entries);
    }
}
