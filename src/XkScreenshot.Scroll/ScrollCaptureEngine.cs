using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;

namespace XkScreenshot.Scroll;

/// <summary>长截图此刻在干什么。界面上那句话照它写。</summary>
public enum ScrollState
{
    /// <summary>刚开始，第一帧已经收下。</summary>
    Starting,

    /// <summary>自动模式：滚轮在连续往外发，画面正在慢慢往前滚。</summary>
    Scrolling,

    /// <summary>手动模式：等用户自己滚。</summary>
    Waiting,

    /// <summary>自动模式，但光标不在选区里，先停着 —— 滚轮发不到目标窗口上。</summary>
    Paused,

    /// <summary>画面正在动，等它停稳再抓。</summary>
    Settling,

    /// <summary>接不上，正在等一帧能接回去的。</summary>
    Lost,
}

public sealed record ScrollProgress(
    ScrollState State,
    ScrollMode Mode,
    int Frames,
    int Width,
    int Height,
    BitmapSource? Preview,
    int PreviewSourceRows);

public sealed record ScrollCaptureResult(
    BitmapSource? Image,
    ScrollFinishReason Reason,
    int Frames,
    int Height);

/// <summary>
/// 长截图的主循环：连续抓帧 + 拼接，自动模式下同时匀速喂滚轮。
///
/// ## 为什么不再是「滚一下、等它停、抓一张」
///
/// 那样每一屏都要等两段：画面开始动（确认滚轮生效）、画面停下来（确认真抓到了完整的一屏）。
/// 两段等待加起来常常比滚动本身还长，屏幕上看到的就是「顿一下、滑一段、再顿一下」。
/// 要的是手机长截图那种观感 —— 一直匀速地滑，一边滑一边抽帧。
///
/// 所以滚轮和抓帧各自跑：滚轮按 <see cref="ScrollTiming.PumpMs"/> 的节拍连续发小量，
/// 抓帧按 <see cref="ScrollTiming.CaptureMs"/> 抓，两者互不等待。每一帧都是滚动过程中的
/// 一个瞬时切片，拼接器只要相邻两帧有重叠就能求出位移（见 <see cref="ScrollStitcher"/>），
/// 并不需要画面是静止的。位移小反而更好求：重叠大，能对上的候选少。
///
/// ## 滚轮怎么发才「慢」
///
/// 一格滚轮是 120 个单位，浏览器一格就是屏高的十分之一 —— 按格发永远慢不下来。
/// 所以发的量小于一格，靠精确式触控板用了很多年的那条路（见 <see cref="ScrollDriver.Wheel"/>）。
/// 发的量固定，慢靠的是**间隔**：速率由实测速度反推，不同应用的每格像素数差三倍也照样对上。
///
/// ## 什么时候还得停下来等
///
/// 连续滚动时抓到的帧理论上是完整的一帧，但有些应用在动画途中渲染的是重采样过的画面
/// （整幅按小数位置插值过），逐像素比不过。于是连着对不上之后，循环会**暂停喂滚轮**，
/// 等画面真正停稳再抓一张（<see cref="ScrollState.Settling"/>），接上之后恢复巡航。
///
/// 连着两次都这样，就认定这个应用「运动中也接不上」，改成按整格滚 ——
/// 每一轮至少前进一屏的十分之一，退回到从前的节奏，不会慢到没法用。
///
/// ## 手动模式是同一套循环
///
/// 手动模式只是把「发滚轮」那一步去掉 —— 抓帧、拼接、判定到底，全都照旧。
/// 于是用户在自动滚动途中随时插手都不会乱：只要把模式一换，循环别的部分完全不用知道。
///
/// 而且用户不必去点那个开关：自动模式要占用鼠标（滚轮只认光标底下的窗口），
/// 一旦他按下鼠标键、或者把光标挪出选区，就说明他要自己来，当场让位。
/// </summary>
public sealed class ScrollCaptureEngine : IDisposable
{
    private readonly ScrollOptions _options;
    private readonly RegionGrabber _grabber;
    private readonly ScrollStitcher _stitcher;
    private readonly PixelPoint _anchor;
    private readonly int _pixelBudget;
    private readonly CancellationTokenSource _cts = new();

    private byte[] _current;
    private byte[] _previous;

    private ScrollMode _mode;

    // ---------------- 自动滚动 ----------------

    /// <summary>每次发出去的滚轮量。默认不满一格，见 <see cref="ScrollTiming.WheelUnits"/>。</summary>
    private int _units = ScrollTiming.WheelUnits;

    /// <summary>每秒发几格。由 <see cref="Measure"/> 按实测速度往回校正。</summary>
    private double _rate = ScrollTiming.InitialNotchesPerSecond;

    private long _nextWheelAt;
    private long _lastAdvanceAt;
    private long _speedAt;
    private int _speedRows;

    /// <summary>光标被我们挪到锚点上了；收工时要还回去。</summary>
    private bool _movedCursor;
    private PixelPoint _cursorRestore;
    private bool _handedOver;
    private long _autoStart;

    /// <summary>光标不在选区里，自动滚动暂时停着（不是交还控制权，挪回来就继续）。</summary>
    private bool _paused;

    /// <summary>光标的原始状态，以及它是什么时候变成这样的 —— 边界上来回抖不算。</summary>
    private bool _pointerAway;
    private long _pointerChangedAt;

    // ---------------- 抓帧 ----------------

    private long _lastGrabAt;

    /// <summary>退到「等画面停稳」的那条支路。<see cref="_settleStart"/> 是进来的时刻。</summary>
    private bool _settling;
    private long _settleStart;

    /// <summary>连续几帧没对上。接上或者判定没变化就清零 —— 只有**连续**失败才算接不上。</summary>
    private int _missed;

    private long _previewAt;
    private BitmapSource? _preview;

    /// <summary>界面线程写、循环线程读。-1 = 没有待处理的切换。</summary>
    private int _modeRequest = -1;

    private volatile bool _finishRequested;
    private volatile bool _cancelRequested;

    /// <summary>抓帧器只能关一次。用它保证「循环自己收摊」和「外面 Dispose」不会重复释放句柄。</summary>
    private int _grabberClosed;

    /// <summary>进度更新。在后台线程上发，调用方自己往界面线程转。</summary>
    public event Action<ScrollProgress>? Progress;

    /// <summary>结束。同样在后台线程上发。</summary>
    public event Action<ScrollCaptureResult>? Finished;

    public ScrollCaptureEngine(PixelRect region, ScrollOptions options)
    {
        _options = options.Sanitized();
        _mode = _options.Mode;
        _grabber = new RegionGrabber(region);
        _stitcher = new ScrollStitcher(region.Width, region.Height, _options.MaxHeight);
        _anchor = ScrollDriver.AnchorFor(region);
        _pixelBudget = FrameCompare.PixelBudget(region.Width, region.Height);
        _current = new byte[_grabber.ByteCount];
        _previous = new byte[_grabber.ByteCount];
    }

    public void Start() => Task.Run(RunAsync);

    /// <summary>收工，把已经拼好的部分交出来。</summary>
    public void RequestFinish() => _finishRequested = true;

    /// <summary>放弃，什么都不交。</summary>
    public void Cancel()
    {
        _cancelRequested = true;
        _cts.Cancel();
    }

    public void SetMode(ScrollMode mode) => Volatile.Write(ref _modeRequest, (int)mode);

    private static long Now => Environment.TickCount64;

    private async Task RunAsync()
    {
        var reason = ScrollFinishReason.UserFinished;

        try
        {
            if (!_grabber.Grab(_current))
            {
                Complete(ScrollFinishReason.Failed);
                return;
            }

            _stitcher.Push(_current);
            Report(ScrollState.Starting);

            long now = Now;
            _lastGrabAt = now;
            _lastAdvanceAt = now;
            _speedAt = now;

            if (_mode == ScrollMode.Auto) EnterAuto(now);

            // 每拍做的事很少（发一条滚轮消息、看一眼光标），真正花钱的抓帧另有节奏。
            // 用 PeriodicTimer 而不是 Task.Delay：后者每 16ms 建一个计时器，一分钟三千多个
            using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(ScrollTiming.PumpMs));

            while (await tick.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_cancelRequested) { reason = ScrollFinishReason.Cancelled; break; }
                if (_finishRequested) { reason = ScrollFinishReason.UserFinished; break; }

                ApplyPendingMode();

                now = Now;
                CheckPointer(now);

                if (_mode == ScrollMode.Auto) Cruise(now);

                if (now - _lastGrabAt < ScrollTiming.CaptureMs) continue;
                if (Capture(now) is { } stop)
                {
                    reason = stop;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = ScrollFinishReason.Cancelled;
        }
        catch (Exception)
        {
            reason = ScrollFinishReason.Failed;
        }

        RestoreCursor();
        // 先关抓帧器再发结束事件：调用方收到 Finished 时资源已经放干净了，
        // 它那边的 Dispose 就是一次空操作，不存在「关的时候循环还在抓」这种交叠
        CloseGrabber();
        Complete(reason);
    }

    // ---------------- 自动滚动 ----------------

    /// <summary>自动滚动这一拍：该测速就测速，该发滚轮就发。</summary>
    private void Cruise(long now)
    {
        Measure(now);

        // 正在等画面停稳、或者光标不在选区里 —— 这时候再发滚轮都是白费，
        // 后者还会滚到别的窗口上去
        if (_settling || _paused) return;

        // 一直没动，先怀疑「这个控件只认整格滚轮」：发一整格试一次。
        // 动了就说明确实如此，从此按整格发（丑一点，但总比不动强）
        if (_units < ScrollDriver.UnitsPerNotch && now - _lastAdvanceAt >= ScrollTiming.ProbeAfterMs)
        {
            _units = ScrollDriver.UnitsPerNotch;
            Wheel(now);
            return;
        }

        if (now >= _nextWheelAt) Wheel(now);
    }

    /// <summary>
    /// 发一条滚轮消息，并排好下一条的时刻。
    ///
    /// 发的量是固定的，慢靠间隔：间隔 = 量 ÷ 速率。这样「只认整格」的控件（按消息条数滚）
    /// 和「按比例滚」的控件（按量滚）都归同一个速率管，实测速度反推过来即可。
    /// </summary>
    private void Wheel(long now)
    {
        ScrollDriver.Wheel(_units);

        double unitsPerSecond = _rate * ScrollDriver.UnitsPerNotch;
        int gap = (int)Math.Round(_units * 1000.0 / Math.Max(1, unitsPerSecond));
        _nextWheelAt = Math.Max(now, _nextWheelAt) + Math.Clamp(gap, 1, 250);
    }

    /// <summary>
    /// 按实测速度反推发送速率。
    ///
    /// 只有这一个反馈环：发得太快，一段时间内前进的行数就多，速率往下压；发得太慢则相反。
    /// 每次只改一部分（<see cref="ScrollTiming.SpeedGain"/>），一两秒收敛，也不会来回抖。
    /// 窗口按时间而不是按帧：不同应用一格滚多少差别很大，「三行一跳」的应用大部分帧是零位移，
    /// 按帧平均会被这堆零带偏。
    /// </summary>
    private void Measure(long now)
    {
        int ms = (int)(now - _speedAt);
        if (ms < ScrollTiming.SpeedWindowMs) return;

        if (_speedRows > 0)
        {
            double measured = _speedRows * 1000.0 / ms;
            double ratio = Math.Clamp(
                ScrollTiming.CruisePixelsPerSecond / measured, 0.5, 2.0);

            _rate = Math.Clamp(
                _rate * (1 + (ratio - 1) * ScrollTiming.SpeedGain),
                ScrollTiming.MinNotchesPerSecond,
                ScrollTiming.MaxNotchesPerSecond);
        }
        else
        {
            // 一格都没动。可能是到底了（那再怎么发也没用），也可能是这个控件不认小量。
            // 慢慢加一点就够，别把「到底了」也催成飞一样。
            // 暂停着的时候不加：停了半分钟回来，速率已经攒到顶，一恢复就是猛冲一下
            if (!_paused)
                _rate = Math.Min(ScrollTiming.MaxNotchesPerSecond, _rate * 1.15);
        }

        _speedRows = 0;
        _speedAt = now;
    }

    private void EnterAuto(long now)
    {
        if (!_movedCursor)
        {
            _cursorRestore = MonitorEnumerator.GetCursorPosition();
            _movedCursor = true;
        }

        ScrollDriver.MoveCursor(_anchor);

        // 先读一遍按键状态，把「上次之后按过没有」这位清掉。
        // 不清的话，刚才那下「框选」的鼠标左键会在这儿被当成用户要接手
        ScrollDriver.AnyButtonDown();

        // 手动模式里攒下的「对不上」不能带过来，否则一按自动就直接判接不上
        _missed = 0;
        _settling = false;
        _handedOver = false;
        _paused = false;
        _pointerAway = false;
        _pointerChangedAt = now;
        _autoStart = now;
        _lastAdvanceAt = now;
        _nextWheelAt = now;
    }

    /// <summary>
    /// 盯着光标，决定「继续滚」还是「让开」。
    ///
    /// 两件确定的事才算数：
    ///
    /// - **按了鼠标键** → 交还控制权。一定是在自己操作，没有含糊；程序自己只发滚轮，不发按键。
    /// - **光标跑出选区** → 只是**暂停**，不交还。滚轮只认光标底下的那个窗口，光标在外面时
    ///   再发就是去滚别的程序，所以必须停；但「手挪开一下」远不等于「我要自己滚」，
    ///   光标挪回来就接着滚。
    ///
    /// 早先的判据是「光标离开锚点十几个像素就交还」，太严了：刚框完选区手还停在鼠标上，
    /// 随手动一下就超过十几像素 —— 用户看到的是「滚了一点点就变手动」。
    /// 光标在选区里动来动去就更不该算数了，那只是手没离开鼠标。
    /// </summary>
    private void CheckPointer(long now)
    {
        if (_mode != ScrollMode.Auto)
        {
            _paused = false;
            return;
        }

        if (now - _autoStart < ScrollTiming.HandoverGraceMs) return;

        if (ScrollDriver.AnyButtonDown())
        {
            HandOver();
            return;
        }

        bool away = !_grabber.Region.Contains(MonitorEnumerator.GetCursorPosition());
        if (away != _pointerAway)
        {
            _pointerAway = away;
            _pointerChangedAt = now;
            return;
        }

        // 在边界上来回蹭不算：状态稳定一小会儿才当真
        if (away == _paused || now - _pointerChangedAt < ScrollTiming.PointerSettleMs) return;

        _paused = away;
        if (!away) _lastAdvanceAt = now;   // 停了这么久，别一回来就判「到底了」

        Report(_paused ? ScrollState.Paused : ScrollState.Scrolling);
    }

    /// <summary>交还控制权：停发滚轮，等用户自己滚。参考帧不动，用户滚回来还能接上。</summary>
    private void HandOver()
    {
        _mode = ScrollMode.Manual;
        _handedOver = true;
        _settling = false;
        _paused = false;
        Report(ScrollState.Waiting);
    }

    private void ApplyPendingMode()
    {
        int requested = Volatile.Read(ref _modeRequest);
        if (requested < 0) return;
        Volatile.Write(ref _modeRequest, -1);

        var mode = (ScrollMode)requested;
        if (mode == _mode) return;

        _mode = mode;

        if (mode == ScrollMode.Auto)
        {
            EnterAuto(Now);
            Report(ScrollState.Scrolling);
        }
        else
        {
            // 交还控制权：停在半路的动画别再去推它
            _settling = false;
            Report(ScrollState.Waiting);
        }
    }

    /// <summary>
    /// 把光标放回原处。只在我们还占着鼠标时才放 —— 光标还在选区里，说明是我们在用它；
    /// 用户已经把它挪走（或者按了键交还过），这时候再抢回来最惹人烦。
    /// </summary>
    private void RestoreCursor()
    {
        if (!_movedCursor || _handedOver) return;
        if (!_grabber.Region.Contains(MonitorEnumerator.GetCursorPosition())) return;

        ScrollDriver.MoveCursor(_cursorRestore);
    }

    // ---------------- 抓帧与拼接 ----------------

    /// <summary>抓一帧送进拼接器。返回非 null 表示该收工了。</summary>
    private ScrollFinishReason? Capture(long now)
    {
        _lastGrabAt = now;

        // 抓进上一帧那块缓冲，抓完再换手：任何时刻 _current 是最新的、
        // _previous 是上一次抓到的，两块轮着用，全程零分配
        if (!_grabber.Grab(_previous)) return null;
        (_current, _previous) = (_previous, _current);

        if (_stitcher.MatchesLast(_current))
        {
            // 和参考帧一模一样：画面没动。不送进拼接器 —— 它要为「位移是多少」
            // 做一轮全 band 搜索，而画面没变时搜出来的任何一个 d 都是噪声
            if (_settling) _settling = false;
            _missed = 0;
            return null;
        }

        if (_settling && !IsStill(now)) return null;
        _settling = false;

        var result = _stitcher.Push(_current);

        switch (result.Status)
        {
            case StitchStatus.Advanced:
                _missed = 0;
                _lastAdvanceAt = now;
                _speedRows += result.Rows;

                // 手动模式下「拼上一帧」不代表程序在滚：那一句该是「轮到你滚了」，
                // 否则用户看到的是「正在慢速滚动…」而画面其实停着等他
                Report(_mode == ScrollMode.Auto ? ScrollState.Scrolling : ScrollState.Waiting);
                break;

            case StitchStatus.NoChange:
                // 变了但没长出新内容（状态栏的数字跳了一下之类）。既不算失败也不算进度
                _missed = 0;
                break;

            case StitchStatus.Unmatched:
                _missed++;

                // 手动模式不因为接不上就收摊：用户随时可能往回滚一点，参考帧还在那儿等着。
                // 只有当程序自己在滚的时候，接不上才意味着这一趟没法继续了
                if (_mode != ScrollMode.Auto)
                {
                    // 画面已经停住了就别再喊「接不上」：那是刚才那一帧的历史，
                    // 用户现在什么也没做，该看到的提示是「轮到你滚了」
                    bool moving = !FrameCompare.NearlyEqual(
                        _current, _previous, _grabber.Stride, _grabber.Height, _pixelBudget);

                    Report(moving ? ScrollState.Lost : ScrollState.Waiting);
                    break;
                }

                if (_missed >= ScrollTiming.MatchLostLimit) return ScrollFinishReason.MatchLost;

                StartSettling(now);
                break;

            case StitchStatus.Full:
                return ScrollFinishReason.HeightLimit;
        }

        // 自动模式下一直没长出内容就是到底了。带「对不上」的时候不算 ——
        // 那种情况画面其实一直在动（只是接不上），说「到底了」是错的。
        // 暂停着也不算：没动是因为我们自己停手了，不是到头了
        if (_mode == ScrollMode.Auto && !_paused && _missed == 0
            && now - _lastAdvanceAt >= ScrollTiming.BottomIdleMs)
            return ScrollFinishReason.BottomReached;

        return null;
    }

    /// <summary>
    /// 画面停稳了没有：连续两帧一样就算停稳（光标闪一下不算），
    /// 等太久也认 —— 正在播视频的页面永远等不到「一样」。
    /// 只有退到「等停稳」那条支路时才需要问，所以走这条路才付这份比较的代价。
    /// </summary>
    private bool IsStill(long now)
    {
        if (now - _settleStart >= ScrollTiming.StableTimeoutMs) return true;

        return FrameCompare.NearlyEqual(
            _current, _previous, _grabber.Stride, _grabber.Height, _pixelBudget);
    }

    /// <summary>
    /// 停下滚轮，等画面自己收尾。
    ///
    /// 会走到这里说明这一帧「变了但对不上」：可能是应用正在滚动的动画途中（它渲染的是
    /// 重采样过的画面，逐像素验证过不了），也可能是个弹窗盖上来、页面跳了一下。
    /// 前一种只要等动画落地就能接回去，所以先按这条最便宜的路走。
    ///
    /// 连着两次都这样就不再指望「运动中也接得上」了：从第二次起按整格滚。
    /// 不这样的话，每一轮只蹭出去二三十像素、再等半秒，长截图会慢到没法用；
    /// 而按整格滚至少每轮前进一屏的十分之一，跟从前的表现一致。
    /// </summary>
    private void StartSettling(long now)
    {
        _settling = true;
        _settleStart = now;
        _rate = Math.Max(
            ScrollTiming.MinNotchesPerSecond, _rate * ScrollTiming.SettleSlowdown);

        if (_missed >= 2) _units = ScrollDriver.UnitsPerNotch;

        Report(ScrollState.Settling);
    }

    // ---------------- 对外 ----------------

    private void Report(ScrollState state)
    {
        // 缩略图重建有代价，而它变化得再快用户也看不过来，限到 150ms 一次
        long now = Now;
        if (now - _previewAt >= 150)
        {
            _previewAt = now;
            _preview = _stitcher.BuildPreview();
        }

        Progress?.Invoke(new ScrollProgress(
            state, _mode, _stitcher.StitchedFrames + 1,
            _stitcher.Width, _stitcher.TotalRows, _preview, _stitcher.PreviewSourceRows));
    }

    private void Complete(ScrollFinishReason reason)
    {
        BitmapSource? image = reason is ScrollFinishReason.Cancelled or ScrollFinishReason.Failed
            ? null
            : _stitcher.Build();

        Finished?.Invoke(new ScrollCaptureResult(
            image, reason, _stitcher.StitchedFrames + 1, image?.PixelHeight ?? 0));
    }

    private void CloseGrabber()
    {
        if (Interlocked.Exchange(ref _grabberClosed, 1) == 0) _grabber.Dispose();
    }

    /// <summary>
    /// 正常路径下循环自己就收拾干净了（<see cref="Finished"/> 发出来的时候已经收完），
    /// 这里只是给「还没跑起来就被关掉」兜底。
    /// </summary>
    public void Dispose()
    {
        _cancelRequested = true;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 循环已经收摊，正是想要的结果 */ }
        CloseGrabber();
    }
}
