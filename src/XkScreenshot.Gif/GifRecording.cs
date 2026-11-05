using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Gif;

/// <summary>
/// 录下来的一帧：只存「和上一帧不一样」的那块矩形，外加一位一像素的变化掩码。
///
/// 不存整屏是这类录制能不能用的关键：界面演示里绝大多数帧只动一小块（光标、打字、
/// 一个按钮变色），整屏存下来 10fps × 30 秒的 1080p 就是 2.4 GB。只存变化块之后，
/// 一段普通的操作演示通常只有几十兆，而且编码时正好可以把它写成 GIF 的「透明差分帧」——
/// 没变的像素留透明，解码器沿用上一帧，文件也跟着小一大截。
///
/// <see cref="Mask"/> 为 null 表示这块矩形里每个像素都要写（首帧、或者整块几乎全变了）；
/// 否则第 p 位为 1 = 这个像素变了，0 = 没变、编码时写成透明。
/// </summary>
internal sealed class GifFrame
{
    public GifFrame(int x, int y, int width, int height, byte[] pixels, byte[]? mask)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Pixels = pixels;
        Mask = mask;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>这块矩形的 BGRA 像素（top-down，非预乘）。</summary>
    public byte[] Pixels { get; }

    /// <summary>变化掩码。null = 整块都要写。</summary>
    public byte[]? Mask { get; }

    /// <summary>
    /// 这一帧该显示多久，毫秒。
    ///
    /// 值是**下一帧到来时**补上的：录制的时候只知道「从这一帧到下一帧过了多久」，
    /// 那正是 GIF 的 delay 语义（显示这一帧，等这么久，再换下一帧）。
    /// 最后一帧在停止那一刻补，所以「停在一个静态画面上三秒」也会如实播三秒。
    /// </summary>
    public int DelayMs { get; set; }
}

/// <summary>
/// 一趟录制的产物。只管画面与时间，不含编码结果 —— 编码器是纯函数，
/// 这样同一份录制可以被重试、也可以在探针里直接喂。
/// </summary>
public sealed class GifRecording
{
    internal GifRecording(
        PixelRect region, int width, int height, List<GifFrame> frames, TimeSpan duration,
        GifStopReason reason, int capturedFrames, long bufferBytes, int paletteColors, bool dither)
    {
        Region = region;
        Width = width;
        Height = height;
        Frames = frames;
        Duration = duration;
        StopReason = reason;
        CapturedFrames = capturedFrames;
        BufferBytes = bufferBytes;
        PaletteColors = paletteColors;
        Dither = dither;
    }

    /// <summary>录制时框选的屏幕区域。贴回执、算落位都用它。</summary>
    public PixelRect Region { get; }

    /// <summary>成品尺寸。画质档位会缩放到这个尺寸，可能小于 <see cref="Region"/>。</summary>
    public int Width { get; }

    public int Height { get; }

    public TimeSpan Duration { get; }
    public GifStopReason StopReason { get; }

    /// <summary>整段共用的色表最多几种颜色（不含透明位）。按录制时的画质档位定。</summary>
    public int PaletteColors { get; }

    /// <summary>是否对量化后的画面做抖动。见 <see cref="GifOptions.Dither"/>。</summary>
    public bool Dither { get; }

    /// <summary>抓到的总帧数（含没变化、没存下来的那些）。控制条上的「帧数」用它。</summary>
    public int CapturedFrames { get; }

    /// <summary>真正会写进 GIF 的帧数。一段静止画面只会有一帧。</summary>
    public int FrameCount => Frames.Count;

    /// <summary>画面缓冲占了多少字节。撞到上限时用它解释原因。</summary>
    public long BufferBytes { get; }

    internal List<GifFrame> Frames { get; }
}
