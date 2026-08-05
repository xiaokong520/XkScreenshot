using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Capture;

/// <summary>
/// 一块抓取结果：既有给 WPF 渲染用的 BitmapSource，也保留一份原始 BGRA 缓冲。
///
/// 为什么要多留一份 byte[]：放大镜和取色器在鼠标每次移动时都要读像素，
/// 走 BitmapSource.CopyPixels 每次都要重新解码/拷贝，高频下必然掉帧。
/// 更要命的是精度问题 —— 合成路径产出的是 Pbgra32（预乘 alpha），
/// 从里面反推原色会有取整误差，取色器报出来的值就不再是屏幕上的真值。
/// 这份缓冲是 GDI 直出的非预乘 BGRA，取色绝对准确。
/// </summary>
public sealed class CapturedFrame
{
    public required BitmapSource Image { get; init; }

    /// <summary>非预乘 BGRA32，top-down 扫描线顺序。</summary>
    public required byte[] Bgra { get; init; }

    public required int Stride { get; init; }

    /// <summary>本帧在虚拟屏幕坐标系中的位置与尺寸。</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>取虚拟屏幕坐标处的颜色。点不在本帧内时返回 false。</summary>
    public bool TryGetColor(PixelPoint point, out Color color)
    {
        if (!Bounds.Contains(point))
        {
            color = default;
            return false;
        }

        int i = (point.Y - Bounds.Y) * Stride + (point.X - Bounds.X) * 4;
        color = Color.FromRgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        return true;
    }

    /// <summary>
    /// 取以 center 为中心、width×height 大小的一块像素，供放大镜使用。
    /// 越界部分用 fill 填充 —— 光标贴到屏幕边缘时放大镜依然要能画满，
    /// 否则会出现内容跳动或索引越界。
    /// </summary>
    public byte[] SampleBlock(PixelPoint center, int width, int height, Color fill)
        => SampleBlock(center, width, height, fill, 1, 1);

    /// <summary>
    /// 同上，但顺便把每个源像素复制成 blockW×blockH 的方块 —— 也就是自己做最近邻放大。
    ///
    /// 为什么不交给 WPF 缩放：那需要给整个绘制层设 BitmapScalingMode.NearestNeighbor，
    /// 而同一层上的毛玻璃背景恰恰需要平滑插值，两者互斥。自己按设备像素展开之后
    /// 绘制是严格 1:1 的，缩放模式就完全不参与了，任何 DPI 下都锐利。
    /// </summary>
    public byte[] SampleBlock(PixelPoint center, int width, int height, Color fill,
        int blockW, int blockH)
    {
        int outW = width * blockW;
        var buffer = new byte[outW * height * blockH * 4];
        int originX = center.X - width / 2;
        int originY = center.Y - height / 2;

        for (int row = 0; row < height; row++)
        {
            int srcY = originY + row - Bounds.Y;

            for (int col = 0; col < width; col++)
            {
                int srcX = originX + col - Bounds.X;

                byte b, g, r;
                if (srcX < 0 || srcY < 0 || srcX >= Bounds.Width || srcY >= Bounds.Height)
                {
                    (b, g, r) = (fill.B, fill.G, fill.R);
                }
                else
                {
                    int src = srcY * Stride + srcX * 4;
                    (b, g, r) = (Bgra[src], Bgra[src + 1], Bgra[src + 2]);
                }

                for (int by = 0; by < blockH; by++)
                {
                    int dst = ((row * blockH + by) * outW + col * blockW) * 4;
                    for (int bx = 0; bx < blockW; bx++, dst += 4)
                    {
                        buffer[dst] = b;
                        buffer[dst + 1] = g;
                        buffer[dst + 2] = r;
                        buffer[dst + 3] = 0xFF;
                    }
                }
            }
        }

        return buffer;
    }
}
