namespace XkScreenshot.Gif;

/// <summary>
/// 一次 GIF 录制的可调项，全部来自设置界面。
///
/// 抓帧间隔、帧差判定这些不在这里：它们是「录出来的动画能不能看」的前提，不是口味问题。
/// 用户能调的是帧率、画质、最长时长、要不要录进鼠标指针。
/// </summary>
public sealed record GifOptions(
    int Fps, int MaxSeconds, bool CaptureCursor, GifQuality Quality = GifQuality.Balanced)
{
    public const int MinFps = 4;
    public const int MaxFps = 30;

    /// <summary>
    /// 默认帧率。10fps 对界面演示够用，但一录视频、拖窗口这种连续运动就明显发顿；
    /// 15fps 是「看起来是动的」和「文件别翻倍」之间的折中。再快的动作可以自己调到 20~30。
    /// </summary>
    public const int DefaultFps = 15;

    /// <summary>GIF 的帧延迟以 10ms 为单位：30fps 是 3cs，再高就会低于多数播放器的下限。</summary>
    public const int MinSeconds = 2;
    public const int MaxSecondsLimit = 120;
    public const int DefaultMaxSeconds = 30;

    public static readonly GifOptions Standard = new(DefaultFps, DefaultMaxSeconds, true);

    /// <summary>设置在磁盘上是可以被人手改坏的，用之前夹回可用范围。</summary>
    public GifOptions Sanitized() => new(
        Math.Clamp(Fps, MinFps, MaxFps),
        Math.Clamp(MaxSeconds, MinSeconds, MaxSecondsLimit),
        CaptureCursor,
        Enum.IsDefined(Quality) ? Quality : GifQuality.Balanced);

    /// <summary>两帧之间隔多久。不写死 100ms：帧率变了，节拍得跟着变。</summary>
    public int FrameIntervalMs
        => Math.Max(20, (int)Math.Round(1000.0 / Math.Clamp(Fps, MinFps, MaxFps)));

    /// <summary>整段共用的色表最多放几种颜色（不含透明位）。</summary>
    public int PaletteColors => Quality switch
    {
        GifQuality.Original => 255,
        GifQuality.Small => 32,
        _ => 64,
    };

    /// <summary>
    /// 输出画面最长边（像素）。0 = 不缩放。
    ///
    /// 视频、照片这类每一帧都在整屏变化的内容，GIF 的 LZW 几乎压不动 —— 尺寸是唯一
    /// 真正有效的大小杠杆。界面演示不需要它，所以「原始」把这一项关掉。
    /// </summary>
    public int MaxDimension => Quality switch
    {
        GifQuality.Original => 0,
        GifQuality.Small => 720,
        _ => 1024,
    };

    /// <summary>
    /// 是否开启抖动。只在「质量优先」档开：GIF 的 256 色对照片、视频一定会出现色带，
    /// 抖动把量化误差摊成细密的噪点，观感上比一条条色带更接近原画面。代价是 LZW 压不动
    /// 那些噪点，文件会大 20~40% —— 另外两档是拿画质换体积，不该再付这笔账。
    ///
    /// 界面截图通常本来就在 256 色以内（色表是精确的），编码器会跳过抖动，
    /// 不会把干净的文字边缘弄噪。
    /// </summary>
    public bool Dither => Quality == GifQuality.Original;
}

/// <summary>
/// 画质档位。GIF 只有 256 色、又是逐像素压缩，画质和体积几乎是一根线上的两端；
/// 与其让用户去猜「128 色是什么意思」，不如给三个有明确取舍的档。
/// </summary>
public enum GifQuality
{
    /// <summary>
    /// 原始尺寸、256 色 + 抖动。质量优先档，但要说清楚：GIF 最多 256 色，
    /// 这一档也不是「无损」，只是把尺寸和颜色的损失都压到格式允许的最小。
    /// </summary>
    Original,

    /// <summary>最长边 1024、64 色。默认：界面演示通常看不出差别，体积小一截。</summary>
    Balanced,

    /// <summary>最长边 720、32 色。发给别人、贴进聊天窗口时用。</summary>
    Small,
}

/// <summary>这一趟录制为什么停了下来。用户看到的那句话照它写。</summary>
public enum GifStopReason
{
    /// <summary>用户按了停止。</summary>
    UserStopped,

    /// <summary>到了设置里的最长时长。</summary>
    DurationLimit,

    /// <summary>缓冲占满了（画面一直在整屏变化，最常见的是在录视频）。</summary>
    MemoryLimit,

    /// <summary>抓不到画面。</summary>
    Failed,
}
