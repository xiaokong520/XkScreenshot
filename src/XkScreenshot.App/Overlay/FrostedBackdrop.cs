using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Capture;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 浮动面板的毛玻璃背景：本屏冻结画面的一份模糊副本。
///
/// 这里有个天然优势 —— 底下的画面是冻屏得到的静态位图，不会变。
/// 所以模糊只需要算一次然后一直复用，不像实时毛玻璃那样每帧都得重算。
/// 面板移动（放大镜跟着光标跑）时只是从这张图里换个位置取样而已。
/// </summary>
public sealed class FrostedBackdrop
{
    /// <summary>先降采样再模糊：模糊代价随分辨率平方增长，而降采样本身就是一次低通。</summary>
    private const int DownscaleFactor = 6;
    /// <summary>降采样后的模糊半径。乘上 DownscaleFactor 才是等效的全分辨率半径。</summary>
    private const int BlurRadius = 4;
    /// <summary>三次盒式模糊足以逼近高斯，再多肉眼看不出差别。</summary>
    private const int BlurPasses = 3;

    /// <summary>
    /// 预先压暗一档。面板上的文字必须在任何背景下都可读，而单靠加重底调会把模糊内容
    /// 整个盖死、玻璃感荡然无存。先把背景本身压暗，底调就可以做得很淡，
    /// 两个目标同时满足：白底也降到 180 左右，而明暗层次仍然完整保留。
    /// </summary>
    private const double LuminosityScale = 0.72;

    private readonly CapturedFrame _frame;
    private BitmapSource? _blurred;

    public FrostedBackdrop(CapturedFrame frame) => _frame = frame;

    /// <summary>构建耗时，返回毫秒数供性能验证用；已构建则返回 0。</summary>
    public double EnsureBuilt()
    {
        if (_blurred is not null) return 0;

        var sw = Stopwatch.StartNew();
        _blurred = Build();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 把模糊背景画进 panel 区域。整张图按窗口尺寸铺满，靠 clip 决定哪一块可见 ——
    /// 这样面板挪到哪里，透出来的就是那里的内容，跟真玻璃一致。
    /// </summary>
    public void DrawInto(DrawingContext dc, Rect panel, Size windowSize)
    {
        EnsureBuilt();
        if (_blurred is null) return;

        dc.DrawImage(_blurred, new Rect(0, 0, windowSize.Width, windowSize.Height));
    }

    private BitmapSource Build()
    {
        var small = Downscale(_frame, DownscaleFactor, out int sw, out int sh);
        BoxBlur(small, sw, sh, BlurRadius, BlurPasses);

        // 保持降采样后的小尺寸，绘制时交给 WPF 放大 —— GPU 的双线性插值本身就是
        // 一次额外的低通，正好是我们要的效果，而且完全不占 CPU。
        //
        // 曾经在这里用 RenderTargetBitmap 预先放大回全分辨率，实测要 200ms 以上，
        // 而这段代码就压在「按下热键 → 覆盖层出现」的路径上。之所以当时要放大，
        // 是因为放大镜那一层开了 NearestNeighbor，小图会被最近邻放大成马赛克；
        // 现在放大镜自己按设备像素展开像素块，那个约束没有了。
        var bitmap = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, small, sw * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 块平均降采样。一次只把 factor 行原始像素读进来（一块 2K 屏是 60 KB），
    /// 而不是先要一份整帧的拷贝 —— 那是十几兆的一片，用完就扔，纯粹是给大对象堆添堵。
    /// </summary>
    private static byte[] Downscale(CapturedFrame frame, int factor, out int dstW, out int dstH)
    {
        int srcW = frame.Bounds.Width;
        int srcH = frame.Bounds.Height;
        int stride = srcW * 4;

        dstW = Math.Max(1, srcW / factor);
        dstH = Math.Max(1, srcH / factor);
        var dst = new byte[dstW * dstH * 4];
        var band = new byte[stride * factor];

        for (int y = 0; y < dstH; y++)
        {
            int rows = Math.Min(factor, srcH - y * factor);
            frame.CopyRows(y * factor, rows, band);

            for (int x = 0; x < dstW; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, count = 0;

                for (int dy = 0; dy < rows; dy++)
                {
                    int row = dy * stride;

                    for (int dx = 0; dx < factor; dx++)
                    {
                        int sx = x * factor + dx;
                        if (sx >= srcW) break;
                        int i = row + sx * 4;
                        sumB += band[i];
                        sumG += band[i + 1];
                        sumR += band[i + 2];
                        count++;
                    }
                }

                int o = (y * dstW + x) * 4;
                dst[o] = (byte)(sumB / count * LuminosityScale);
                dst[o + 1] = (byte)(sumG / count * LuminosityScale);
                dst[o + 2] = (byte)(sumR / count * LuminosityScale);
                dst[o + 3] = 0xFF;
            }
        }

        return dst;
    }

    private static void BoxBlur(byte[] buffer, int w, int h, int radius, int passes)
    {
        var scratch = new byte[buffer.Length];
        for (int p = 0; p < passes; p++)
        {
            BlurAxis(buffer, scratch, w, h, radius, horizontal: true);
            BlurAxis(scratch, buffer, w, h, radius, horizontal: false);
        }
    }

    /// <summary>
    /// 可分离的盒式模糊，一个方向一趟。用滑动窗口累加，
    /// 每个像素只做一次加一次减，复杂度与半径无关。
    /// </summary>
    private static void BlurAxis(byte[] src, byte[] dst, int w, int h, int radius, bool horizontal)
    {
        int outer = horizontal ? h : w;
        int inner = horizontal ? w : h;
        int window = radius * 2 + 1;

        for (int o = 0; o < outer; o++)
        {
            int Index(int i) => horizontal ? (o * w + i) * 4 : (i * w + o) * 4;

            int sumB = 0, sumG = 0, sumR = 0;
            // 窗口初始化时越界的部分用边缘像素补，避免面板边缘出现暗边
            for (int k = -radius; k <= radius; k++)
            {
                int i = Index(Math.Clamp(k, 0, inner - 1));
                sumB += src[i];
                sumG += src[i + 1];
                sumR += src[i + 2];
            }

            for (int i = 0; i < inner; i++)
            {
                int d = Index(i);
                dst[d] = (byte)(sumB / window);
                dst[d + 1] = (byte)(sumG / window);
                dst[d + 2] = (byte)(sumR / window);
                dst[d + 3] = 0xFF;

                int add = Index(Math.Clamp(i + radius + 1, 0, inner - 1));
                int sub = Index(Math.Clamp(i - radius, 0, inner - 1));
                sumB += src[add] - src[sub];
                sumG += src[add + 1] - src[sub + 1];
                sumR += src[add + 2] - src[sub + 2];
            }
        }
    }
}
