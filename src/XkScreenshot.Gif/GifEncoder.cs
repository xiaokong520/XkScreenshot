using System.Buffers.Binary;

namespace XkScreenshot.Gif;

/// <summary>
/// 把一段录制写成 GIF89a。
///
/// 不借 WPF 的 <c>GifBitmapEncoder</c>：那一套是给「一张图存成 gif」用的，多帧要靠
/// 元数据硬塞，帧延迟、循环次数、透明差分全都不在它的接口上；而屏幕录制恰恰全靠这三样
/// 才小得下来。自己写一遍 LZW 的成本，比跟编码器斗智低得多。
///
/// 三条省体积的做法，缺一条文件都会大出好几倍：
/// 1. 帧只写变化矩形（录制时已经切好）；
/// 2. 矩形里没变的像素写成透明，解码器沿用上一帧；
/// 3. 整段共用一张全局色表，颜色索引在帧之间保持一致，LZW 也更容易压。
///
/// 代价是颜色上限 255（索引 0 留给透明）。这是 GIF 的天花板，不是这里的取舍。
/// </summary>
public static class GifEncoder
{
    /// <summary>
    /// 帧延迟的下限，单位 10ms。
    ///
    /// GIF 允许写 0/1，但浏览器和多数播放器会把小于 2cs 的帧强行当成 10cs ——
    /// 与其让实际播放速度和写进去的数字对不上，不如从源头就不写这种值。
    /// </summary>
    private const int MinDelayCs = 2;

    public static byte[] Encode(
        GifRecording recording, Action<double>? progress = null, CancellationToken ct = default)
    {
        var frames = recording.Frames;
        if (frames.Count == 0) throw new ArgumentException("录制里没有画面", nameof(recording));

        int width = recording.Width;
        int height = recording.Height;
        if (width is <= 0 or > 65535 || height is <= 0 or > 65535)
            throw new ArgumentException("录制尺寸超出 GIF 能表示的范围", nameof(recording));

        ct.ThrowIfCancellationRequested();

        var palette = GifPalette.Build(recording, recording.PaletteColors, ct);
        progress?.Invoke(0.10);

        using var output = new MemoryStream(1 << 20);
        WriteHeader(output, width, height, palette);
        WriteLoopExtension(output);

        var lzw = new GifLzwWriter();
        var indices = new byte[width * height];

        // 精确色表没必要抖；大片平涂的界面截图也不能抖 —— 只会把纯色弄成噪点、文件翻几倍。
        // 只有照片/视频这种连续色调、色表又确实不够用的画面才值得。
        bool dither = recording.Dither && !palette.IsExact && !palette.IsFlat;

        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var frame = frames[i];
            int pixels = frame.Width * frame.Height;
            bool transparent = frame.Mask is not null;

            // 录制时每一帧都存了那块矩形的完整像素（不只是变了的那些），所以这里直接映射就行，
            // 不需要在编码器里再攒一张画布 —— 透明像素交给解码器沿用上一帧。
            if (dither && !transparent)
            {
                MapWithDither(frame.Pixels, frame.Width, frame.Height, palette, indices);
            }
            else
            {
                for (int p = 0; p < pixels; p++)
                {
                    if (transparent && (frame.Mask![p >> 3] & (1 << (p & 7))) == 0)
                    {
                        indices[p] = 0;
                        continue;
                    }

                    uint color = BinaryPrimitives.ReadUInt32LittleEndian(frame.Pixels.AsSpan(p * 4, 4));
                    indices[p] = palette.IndexOf(color);
                }
            }

            WriteFrame(output, lzw, frame, indices, pixels, palette, transparent);

            progress?.Invoke(0.10 + 0.88 * (i + 1) / frames.Count);
        }

        output.WriteByte(0x3B);   // Trailer
        progress?.Invoke(1.0);

        return output.ToArray();
    }

    /// <summary>
    /// Floyd–Steinberg 抖动：把「这个像素被量化掉的那部分颜色」分给右边和下面几个像素。
    ///
    /// 256 色照片最大的毛病不是糊，是一道一道的色带；把误差摊成细密噪点之后，同样的色表
    /// 看起来会平顺得多，代价是 LZW 压不动这些噪点、文件更大。所以只有「高质量」档、
    /// 且色表确实被量化过（不是精确色表）时才走这里。
    ///
    /// 透明差分帧不做抖动：那些帧只写变化的像素，误差扩散到透明洞里会被直接丢掉，
    /// 反而在变化区边界留下接缝。视频里几乎每帧都是整块实写，够用了。
    /// </summary>
    private static void MapWithDither(
        byte[] pixels, int width, int height, GifPalette palette, byte[] indices)
    {
        // 每行错开一格的首尾，右边/左下才有地方落误差
        int stride = (width + 2) * 3;
        var current = new int[stride];
        var next = new int[stride];

        for (int y = 0; y < height; y++)
        {
            Array.Clear(next);

            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                int e = (x + 1) * 3;

                int r = Math.Clamp(pixels[o + 2] + current[e], 0, 255);
                int g = Math.Clamp(pixels[o + 1] + current[e + 1], 0, 255);
                int b = Math.Clamp(pixels[o] + current[e + 2], 0, 255);

                byte index = palette.IndexOf(r, g, b);
                indices[y * width + x] = index;

                var (pr, pg, pb) = palette.ColorOf(index);
                int er = r - pr;
                int eg = g - pg;
                int eb = b - pb;

                Spread(current, e + 3, er, eg, eb, 7);   // 右
                Spread(next, e - 3, er, eg, eb, 3);      // 左下
                Spread(next, e, er, eg, eb, 5);          // 下
                Spread(next, e + 3, er, eg, eb, 1);      // 右下
            }

            (current, next) = (next, current);
        }
    }

    private static void Spread(int[] row, int offset, int r, int g, int b, int weight)
    {
        row[offset] += r * weight / 16;
        row[offset + 1] += g * weight / 16;
        row[offset + 2] += b * weight / 16;
    }

    private static void WriteHeader(Stream output, int width, int height, GifPalette palette)
    {
        output.Write("GIF89a"u8);

        WriteUInt16(output, width);
        WriteUInt16(output, height);

        int bits = 1;
        while ((1 << bits) < palette.TableSize) bits++;

        // 全局色表 | 颜色分辨率 8 位（7） | 未排序 | 色表大小
        output.WriteByte((byte)(0x80 | (7 << 4) | (bits - 1)));
        output.WriteByte(0);   // 背景色索引：透明位，实际不会被用到
        output.WriteByte(0);   // 像素宽高比

        output.Write(palette.Table, 0, palette.Table.Length);
    }

    /// <summary>
    /// Netscape 循环扩展。0 = 无限循环 —— 录制一段操作演示，播完停在最后一帧最奇怪。
    /// </summary>
    private static void WriteLoopExtension(Stream output)
    {
        output.WriteByte(0x21);
        output.WriteByte(0xFF);
        output.WriteByte(0x0B);
        output.Write("NETSCAPE2.0"u8);
        output.WriteByte(0x03);
        output.WriteByte(0x01);
        WriteUInt16(output, 0);
        output.WriteByte(0);
    }

    private static void WriteFrame(
        Stream output, GifLzwWriter lzw, GifFrame frame, byte[] indices, int pixels,
        GifPalette palette, bool transparent)
    {
        int delay = Math.Clamp((int)Math.Round(frame.DelayMs / 10.0), MinDelayCs, 65535);

        // Graphic Control Extension：处置方式 1（不清除，下一帧叠在上面），
        // 这样透明像素才能沿用上一帧；换成 2（恢复背景）会在每帧之间闪出底色
        output.WriteByte(0x21);
        output.WriteByte(0xF9);
        output.WriteByte(0x04);
        output.WriteByte((byte)((1 << 2) | (transparent ? 1 : 0)));
        WriteUInt16(output, delay);
        output.WriteByte(0);   // 透明色索引
        output.WriteByte(0);

        // Image Descriptor：没有局部色表，不隔行
        output.WriteByte(0x2C);
        WriteUInt16(output, frame.X);
        WriteUInt16(output, frame.Y);
        WriteUInt16(output, frame.Width);
        WriteUInt16(output, frame.Height);
        output.WriteByte(0);

        lzw.Write(output, indices, pixels, palette.MinCodeSize);
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
