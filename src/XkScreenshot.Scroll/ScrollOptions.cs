namespace XkScreenshot.Scroll;

/// <summary>滚动由谁驱动。</summary>
public enum ScrollMode
{
    /// <summary>程序自己发滚轮。</summary>
    Auto,

    /// <summary>用户自己滚，程序只负责抓帧与拼接。</summary>
    Manual,
}

/// <summary>一次长截图为什么停下来。用户看到的那句话就是照它写的。</summary>
public enum ScrollFinishReason
{
    /// <summary>用户自己按了完成。</summary>
    UserFinished,

    /// <summary>自动模式连着几次滚不动了，判定到底。</summary>
    BottomReached,

    /// <summary>撞到高度上限。</summary>
    HeightLimit,

    /// <summary>连着几帧对不上，停在这里。</summary>
    MatchLost,

    /// <summary>用户取消，没有成品。</summary>
    Cancelled,

    /// <summary>抓帧失败之类的硬错误，没有成品。</summary>
    Failed,
}

/// <summary>
/// 长截图的可调项。全部来自设置界面。
///
/// 抓帧节奏（多久抓一帧、等多久算稳定）不在这里 —— 那几个数是拼接能不能成立的前提，
/// 不是口味问题，调错了功能直接坏掉，见 <see cref="ScrollTiming"/>。
/// </summary>
public sealed record ScrollOptions(ScrollMode Mode, int WheelNotches, int MaxHeight)
{
    public const int MinHeightLimit = 1000;
    public const int MaxHeightLimit = 60000;

    /// <summary>自动模式每次滚多少格。1 格 ≈ 三行字 ≈ 50px，慢慢截、帧间重叠最大。</summary>
    internal const int DefaultWheelNotches = 1;

    public static readonly ScrollOptions Standard = new(ScrollMode.Auto, DefaultWheelNotches, 20000);

    /// <summary>把设置里可能被人手改坏的值夹回可用范围。</summary>
    public ScrollOptions Sanitized() => new(
        Mode,
        Math.Clamp(WheelNotches, 1, 10),
        Math.Clamp(MaxHeight, MinHeightLimit, MaxHeightLimit));
}

/// <summary>抓帧与滚动的节奏。</summary>
public static class ScrollTiming
{
    /// <summary>抓帧间隔。60ms 上下：再快只是白抓，再慢用户会觉得滚一屏要等半天。</summary>
    public const int TickMs = 60;

    /// <summary>
    /// 发完滚轮后等画面开始动的上限。超时就当成「这一下没滚动」。
    ///
    /// 不能只看一帧就下结论：滚轮到目标进程要走一圈输入队列，平滑滚动的应用
    /// 更是要过几十毫秒才开始动，那时候画面确实还和刚才一模一样。
    /// </summary>
    public const int ChangeTimeoutMs = 700;

    /// <summary>
    /// 画面开始动之后，等它停下来的上限。超时就按当前这一帧硬拼。
    ///
    /// 有这条兜底是因为「稳定」在有些页面上永远等不到：正在播的视频、跑马灯、
    /// 一秒一跳的时钟，都会让每一帧都跟上一帧不一样。宁可拼一张可能带残影的，
    /// 也不能卡在那里一动不动。
    /// </summary>
    public const int StableTimeoutMs = 900;

    /// <summary>自动模式下连着几次滚不动就判定到底。</summary>
    public const int BottomConfirmTries = 3;

    /// <summary>连着几帧对不上就停手。</summary>
    public const int MatchLostLimit = 6;

    /// <summary>自动模式接管鼠标后，光标偏离锚点超过这个距离就认为用户要自己来。</summary>
    public const int HandoverDistancePx = 12;

    /// <summary>刚开始的这段时间不判定「用户动了鼠标」—— 那多半是他刚点完按钮手还没停。</summary>
    public const int HandoverGraceMs = 500;
}
