namespace XkScreenshot.Gif;

/// <summary>
/// 把抓到的整屏帧缩到目标尺寸。
///
/// 用面积平均（box filter）而不是最近邻：录视频、照片时最近邻会把细节抖成一片噪点，
/// 而噪点在 LZW 里几乎不可压缩 —— 看起来更"锐"，文件反而更大。面积平均出来的颜色更少、
/// 过渡更平，正合 GIF 这套 256 色的胃口。
///
/// 这不做任意比例的高质量重采样：目标只是「小一点、还能看」，双三次那套在这里
/// 既慢又不会让 LZW 更高兴。
/// </summary>
internal static class FrameDownscaler
{
    public static void Resize(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int width, int height)
    {
        if (width == sourceWidth && height == sourceHeight)
        {
            Buffer.BlockCopy(source, 0, destination, 0, sourceWidth * sourceHeight * 4);
            return;
        }

        for (int dy = 0; dy < height; dy++)
        {
            int sy0 = dy * sourceHeight / height;
            int sy1 = Math.Max(sy0 + 1, (dy + 1) * sourceHeight / height);

            for (int dx = 0; dx < width; dx++)
            {
                int sx0 = dx * sourceWidth / width;
                int sx1 = Math.Max(sx0 + 1, (dx + 1) * sourceWidth / width);

                int b = 0, g = 0, r = 0, count = 0;

                for (int sy = sy0; sy < sy1; sy++)
                {
                    int offset = (sy * sourceWidth + sx0) * 4;
                    for (int sx = sx0; sx < sx1; sx++, offset += 4)
                    {
                        b += source[offset];
                        g += source[offset + 1];
                        r += source[offset + 2];
                        count++;
                    }
                }

                int target = (dy * width + dx) * 4;
                destination[target] = (byte)(b / count);
                destination[target + 1] = (byte)(g / count);
                destination[target + 2] = (byte)(r / count);
                destination[target + 3] = 0xFF;
            }
        }
    }
}
