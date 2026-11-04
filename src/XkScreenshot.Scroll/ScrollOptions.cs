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
/// 抓帧节奏和滚动速度都不在这里：它们是拼接能不能成立的前提，不是口味问题 ——
/// 抓太慢、滚太快，相邻两帧就没有重叠，接不上。所以那几个数写在 <see cref="ScrollTiming"/>，
/// 不接受用户随手改。
/// </summary>
public sealed record ScrollOptions(ScrollMode Mode, int MaxHeight)
{
    public const int MinHeightLimit = 1000;
    public const int MaxHeightLimit = 60000;

    public static readonly ScrollOptions Standard = new(ScrollMode.Auto, 20000);

    /// <summary>把设置里可能被人手改坏的值夹回可用范围。</summary>
    public ScrollOptions Sanitized() => new(
        Mode,
        Math.Clamp(MaxHeight, MinHeightLimit, MaxHeightLimit));
}

/// <summary>
/// 自动滚动的节奏。
///
/// ## 为什么是「一直慢慢滚」而不是「滚一下、等停、抓一张」
///
/// 后者每一屏都要等两件事：画面开始动（确认滚轮生效）、画面停下来（确认真抓到了完整的一屏）。
/// 两段等待加起来通常比滚动本身还长，屏幕上看到的就是「顿一下、滑一段、再顿一下」。
/// 手机上的长截图不这么干：它一直匀速滑，一边滑一边抽帧，看起来是在「扫」过整页。
///
/// 于是滚轮按 <see cref="PumpMs"/> 的节拍连续发小量，抓帧按 <see cref="CaptureMs"/> 自己的节奏，
/// 两件事彻底分开。抓到的每一帧都是滚动过程中的瞬时切片 —— 拼接器要的只是「和前面有足够重叠」，
/// 并不要求画面静止。
///
/// ## 速度为什么由程序自己校正
///
/// 一格滚轮在不同应用里是 40 到 120 像素（记事本三行、浏览器一百来像素、列表两三个条目），
/// 同一个发送速率发出去，实际快慢能差三倍。所以这里只给一个目标速度
/// <see cref="CruisePixelsPerSecond"/>，发多少由引擎按实测速度反推。
/// </summary>
public static class ScrollTiming
{
    /// <summary>主循环的节拍。16ms ≈ 60Hz，再密也只是白醒一趟。</summary>
    public const int PumpMs = 16;

    /// <summary>
    /// 抓帧间隔。
    ///
    /// 连续滚动时它同时决定「一帧跨多远」：巡航速度下就是二十几像素，
    /// 相对一屏还有九成以上的重叠，怎么都对得上。
    /// </summary>
    public const int CaptureMs = 60;

    /// <summary>
    /// 巡航速度，物理像素/秒。
    ///
    /// 按手机长截图的观感折算：大约三秒滚一屏，一屏按八百像素算。
    /// 再快用户就看不清滚的是什么，也更容易一帧跨过一屏、接不上。
    /// 这是观感参数，不是技术参数 —— 想改就改这一个数，起手速率会跟着算。
    /// </summary>
    public const int CruisePixelsPerSecond = 260;

    /// <summary>
    /// 每次发出去的滚轮量（单位 1/120 格，20 就是 1/6 格）。
    ///
    /// 不满一格的量是最顺的那种发法（见 <see cref="ScrollDriver.Wheel"/>），
    /// 但也别拆得太碎：有些控件是「收到一条消息就滚一格」，拆成六份它就会滚六倍。
    /// 这个量在「够细」和「别太多条消息」之间取了个中间值。
    /// </summary>
    public const int WheelUnits = 20;

    /// <summary>
    /// 起手速率（格/秒），按「一格约一百像素」估算。
    ///
    /// 不写死成一个数，是因为写死了第一个测速窗口之前会明显偏离目标：
    /// 目标 260px/s 却按每秒 4 格起手（约 400px/s），开头那一秒就是偏快的，
    /// 用户看到的是「起步猛一下、然后才慢下来」。
    /// </summary>
    public const double InitialNotchesPerSecond = CruisePixelsPerSecond / 100.0;

    public const double MinNotchesPerSecond = 0.8;
    public const double MaxNotchesPerSecond = 12.0;

    /// <summary>测速窗口。太短会被「三行一跳」这种量化噪声带偏。</summary>
    public const int SpeedWindowMs = 500;

    /// <summary>速率校正的力度（0~1）。大了会来回抖，小了要好几秒才收敛。</summary>
    public const double SpeedGain = 0.4;

    /// <summary>一直没动多久之后，按「这个控件只认整格」试一次整格滚轮。</summary>
    public const int ProbeAfterMs = 800;

    /// <summary>
    /// 自动模式连着多久没有新内容就判定到底。
    ///
    /// 给到两秒是有意的：懒加载的页面常常要缓一下才把下一屏放出来，
    /// 而「以为到底了」比「多等一秒」难受得多。
    /// </summary>
    public const int BottomIdleMs = 2000;

    /// <summary>
    /// 连着几帧对不上就**换锚**（认栽丢一小段，从当前画面接着往下拼）。
    ///
    /// 早先这里是「连不上 4 帧就整场结束」，结果一次偶发失配几乎必然升级成结束：
    /// 失配时参考帧不更新，而滚轮还在往前走 —— 参考帧一落后，重叠区就没了，
    /// 后面每一帧都必然对不上，凑满 4 次是时间问题。等停稳那条支路也救不回来，
    /// 因为它同样拿着那张已经落后的参考帧去比。
    /// </summary>
    public const int ReanchorAfterMisses = 2;

    /// <summary>
    /// 连着换锚这么多次还是接不上，才判定这一趟没法继续（比如页面在播视频）。
    ///
    /// 计的是「连续」：中间只要有一帧拼上了就清零。所以偶发的一两次换锚不会把会话拖垮，
    /// 真正一路都接不上的时候也不会无限换锚空转。
    /// </summary>
    public const int ReanchorLimit = 3;

    /// <summary>
    /// 「等画面停稳」的超时。有这条兜底是因为稳定在有些页面上永远等不到：
    /// 正在播的视频、跑马灯、一秒一跳的时钟，每一帧都跟上一帧不一样。
    /// </summary>
    public const int StableTimeoutMs = 900;

    /// <summary>每次退到「等停稳」把速率压掉多少 —— 追不上就说明这一格跨得太远。</summary>
    public const double SettleSlowdown = 0.75;

    /// <summary>刚开始的这段时间不判定「用户动了鼠标」—— 那多半是他刚点完按钮手还没停。</summary>
    public const int HandoverGraceMs = 500;

    /// <summary>
    /// 光标进出选区之后，要稳定多久才当真。
    ///
    /// 光标在选区边界上来回蹭的时候，别让「暂停 / 继续」跟着抖。
    /// </summary>
    public const int PointerSettleMs = 250;
}
