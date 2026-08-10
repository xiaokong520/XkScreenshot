using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Capture;

/// <summary>
/// 一块抓取结果：一张冻结的位图，加上它在虚拟屏幕坐标系里的位置。
///
/// 曾经在这里额外留一份整帧的 <c>byte[]</c>，理由是「放大镜和取色器每次鼠标移动都要读像素，
/// 走 CopyPixels 太慢」。那个理由不成立 —— 它们要的是光标周围十几个像素，
/// CopyPixels 是可以只取一小块矩形的，代价与整帧无关。而留着那份缓冲的代价是实打实的：
/// 每台显示器多压一份 W×H×4（2K 屏就是 15 MB）在大对象堆上，整个会话期间不放。
///
/// 位图格式的契约：32bpp、字节序 B,G,R,(A)，也就是 <see cref="PixelFormats.Bgr32"/>
/// 或 <see cref="PixelFormats.Bgra32"/>。抓屏这一路全程不经过预乘 alpha 的格式，
/// 取色器报出来的就是屏幕上的真值。
/// </summary>
public sealed class CapturedFrame
{
    public required BitmapSource Image { get; init; }

    /// <summary>本帧在虚拟屏幕坐标系中的位置与尺寸。</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>取一个像素用的四字节缓冲。取色器每次鼠标移动调一次，不值得每次新建。</summary>
    private readonly byte[] _pixel = new byte[4];

    /// <summary>放大镜取样用的中转缓冲，按需长大后一直复用。</summary>
    private byte[] _sample = [];

    /// <summary>取虚拟屏幕坐标处的颜色。点不在本帧内时返回 false。</summary>
    public bool TryGetColor(PixelPoint point, out Color color)
    {
        if (!Bounds.Contains(point))
        {
            color = default;
            return false;
        }

        Image.CopyPixels(
            new Int32Rect(point.X - Bounds.X, point.Y - Bounds.Y, 1, 1), _pixel, 4, 0);
        color = Color.FromRgb(_pixel[2], _pixel[1], _pixel[0]);
        return true;
    }

    /// <summary>
    /// 把 firstRow 起的 rowCount 行整宽拷进 destination，行距为整帧宽度。
    /// 毛玻璃背景要过一遍整帧像素，但它是按条带降采样的 —— 一次几行，不必把整帧摊开。
    /// </summary>
    public void CopyRows(int firstRow, int rowCount, byte[] destination)
        => Image.CopyPixels(
            new Int32Rect(0, firstRow, Bounds.Width, rowCount), destination, Bounds.Width * 4, 0);

    /// <summary>
    /// 取以 center 为中心、width×height 大小的一块像素，顺便把每个源像素复制成
    /// blockW×blockH 的方块 —— 也就是自己做最近邻放大，结果写进 destination。
    ///
    /// 为什么不交给 WPF 缩放：那需要给整个绘制层设 BitmapScalingMode.NearestNeighbor，
    /// 而同一层上的毛玻璃背景恰恰需要平滑插值，两者互斥。自己按设备像素展开之后
    /// 绘制是严格 1:1 的，缩放模式就完全不参与了，任何 DPI 下都锐利。
    ///
    /// 缓冲由调用方给：这个方法在鼠标移动时每帧都调，自己新建的话每一帧都往堆上扔一片。
    /// 越界部分用 fill 填充 —— 光标贴到屏幕边缘时放大镜依然要能画满。
    /// </summary>
    public void SampleBlock(byte[] destination, PixelPoint center, int width, int height,
        Color fill, int blockW, int blockH)
    {
        int originX = center.X - width / 2;
        int originY = center.Y - height / 2;
        int stride = width * 4;

        int need = stride * height;
        if (_sample.Length < need) _sample = new byte[need];
        var sample = _sample;

        // 先整片铺成 fill，落在帧外的行列就保持这个颜色
        for (int i = 0; i < need; i += 4)
        {
            sample[i] = fill.B;
            sample[i + 1] = fill.G;
            sample[i + 2] = fill.R;
        }

        // 与本帧相交的那块一次拷进来。CopyPixels 对越界矩形会直接抛，
        // 所以先求交集，再把它放到取样窗口里对应的位置上
        var window = new PixelRect(originX, originY, width, height);
        var overlap = window.Intersect(Bounds);
        if (!overlap.IsEmpty)
        {
            int offset = ((overlap.Y - originY) * width + (overlap.X - originX)) * 4;
            Image.CopyPixels(
                new Int32Rect(overlap.X - Bounds.X, overlap.Y - Bounds.Y, overlap.Width, overlap.Height),
                sample, stride, offset);
        }

        int outW = width * blockW;
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int src = row * stride + col * 4;
                byte b = sample[src], g = sample[src + 1], r = sample[src + 2];

                for (int by = 0; by < blockH; by++)
                {
                    int dst = ((row * blockH + by) * outW + col * blockW) * 4;
                    for (int bx = 0; bx < blockW; bx++, dst += 4)
                    {
                        destination[dst] = b;
                        destination[dst + 1] = g;
                        destination[dst + 2] = r;
                        destination[dst + 3] = 0xFF;
                    }
                }
            }
        }
    }
}
