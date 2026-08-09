using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;

namespace XkScreenshot.Scroll;

/// <summary>长截图此刻在干什么。界面上那句话照它写。</summary>
public enum ScrollState
{
    /// <summary>刚开始，第一帧已经收下。</summary>
    Starting,

    /// <summary>自动模式：滚轮已经发出去，等画面动。</summary>
    Scrolling,

    /// <summary>手动模式：等用户自己滚。</summary>
    Waiting,

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
/// 长截图的主循环：抓帧 → 等稳 → 拼接 → 该滚就滚。
///
/// ## 为什么是一个状态机而不是「滚一下、睡一会儿、抓一张」
///
/// 「睡多久」这个问题没有正确答案：同一个滚轮事件，记事本是瞬间到位，
/// 浏览器要做两三百毫秒的平滑滚动动画，远程桌面还要再加一段网络延迟。
/// 睡短了拍到的是动画中间的一帧（拼上去就是撕裂的），睡长了每一屏都白等半秒。
///
/// 所以改成盯着画面本身：先等它**开始动**（确认滚轮真的生效了），再等它**停下来**
/// （连着两帧一样就是停稳了），这时候抓的才是一帧完整的画面。两头都有超时兜底，
/// 因为「开始动」可能永远等不到（已经到底了），「停下来」也可能永远等不到（页面上有视频在播）。
///
/// ## 手动模式是同一套循环
///
/// 手动模式只是把「自己发滚轮」那一步去掉 —— 等画面动、等它停稳、拼接，全都照旧。
/// 于是用户在自动滚动途中随时插手都不会乱：只要把模式一换，循环别的部分完全不用知道。
///
/// 而且用户根本不必去点那个开关：自动模式要占用鼠标（滚轮只认光标底下的窗口），
/// 一旦发现光标被人挪走了，就说明用户想自己来，当场让位。
/// </summary>
public sealed class ScrollCaptureEngine : IDisposable
{
    private enum Phase
    {
        /// <summary>等画面开始动。</summary>
        WaitChange,

        /// <summary>画面在动，等它停稳。</summary>
        WaitStable,
    }

    private readonly ScrollOptions _options;
    private readonly RegionGrabber _grabber;
    private readonly ScrollStitcher _stitcher;
    private readonly PixelPoint _anchor;
    private readonly int _pixelBudget;
    private readonly CancellationTokenSource _cts = new();

    private byte[] _current;
    private byte[] _previous;

    private ScrollMode _mode;
    private Phase _phase = Phase.WaitChange;
    private long _phaseStart;
    private long _autoStart;
    private int _idle;
    private int _missed;
    private bool _handedOver;
    private PixelPoint _cursorRestore;
    private bool _movedCursor;
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

            if (_mode == ScrollMode.Auto) EnterAuto();
            _phaseStart = Now;

            while (true)
            {
                await Task.Delay(ScrollTiming.TickMs, _cts.Token).ConfigureAwait(false);

                if (_cancelRequested) { reason = ScrollFinishReason.Cancelled; break; }
                if (_finishRequested) { reason = ScrollFinishReason.UserFinished; break; }

                ApplyPendingMode();

                // 抓进上上帧那块缓冲，抓完再换手：任何时刻 _current 是最新的、
                // _previous 是上一次抓到的，两块轮着用，全程零分配
                if (!_grabber.Grab(_previous)) continue;
                (_current, _previous) = (_previous, _current);

                CheckHandover();

                if (_phase == Phase.WaitChange)
                {
                    if (WaitForChange(ref reason)) break;
                    continue;
                }

                if (WaitForStable(ref reason)) break;
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

    /// <summary>等画面开始动。返回 true 表示该收工了。</summary>
    private bool WaitForChange(ref ScrollFinishReason reason)
    {
        if (!_stitcher.MatchesLast(_current))
        {
            _phase = Phase.WaitStable;
            _phaseStart = Now;
            Report(ScrollState.Settling);
            return false;
        }

        // 手动模式就一直等着 —— 用户可能正在读，也可能去泡了杯茶
        if (_mode != ScrollMode.Auto) return false;
        if (Now - _phaseStart < ScrollTiming.ChangeTimeoutMs) return false;

        // 滚了却没动静，多半是到底了。再试几次才下结论：
        // 懒加载的页面常常要缓一下才把下一屏放出来，第一次没动不等于没有了
        if (++_idle >= ScrollTiming.BottomConfirmTries)
        {
            reason = ScrollFinishReason.BottomReached;
            return true;
        }

        Scroll();
        // 重置计时：每一个新发出的滚轮事件都要等自己的 700ms，
        // 不重置的话，上一次超时已经吃掉了这 700ms，接下来的 tick 会立刻再判一次超时，
        // 3 次重试总共只花了 200ms 不到就判成「到底了」
        _phaseStart = Now;
        return false;
    }

    /// <summary>等画面停稳，稳了就拼一帧。返回 true 表示该收工了。</summary>
    private bool WaitForStable(ref ScrollFinishReason reason)
    {
        bool settled = FrameCompare.NearlyEqual(
            _current, _previous, _grabber.Stride, _grabber.Height, _pixelBudget);

        // 还在动，且还没等到不耐烦 —— 接着等
        if (!settled && Now - _phaseStart < ScrollTiming.StableTimeoutMs) return false;

        var result = _stitcher.Push(_current);

        switch (result.Status)
        {
            case StitchStatus.Advanced:
                _idle = 0;
                _missed = 0;
                Report(ScrollState.Settling);
                break;

            case StitchStatus.NoChange:
                _idle++;
                break;

            case StitchStatus.Unmatched:
                _missed++;
                // 连着对不上的帧够多了，认栽换锚：把当前帧立为新参考。
                // 长图里会留一道不连续的缝，但总比连已拼好的几十帧一起报废强。
                if (_missed >= ScrollTiming.MatchLostLimit / 2)
                {
                    _stitcher.ResetAnchor(_current);
                    _idle = 0;
                    _missed = 0;
                }
                Report(ScrollState.Lost);
                break;

            case StitchStatus.Full:
                reason = ScrollFinishReason.HeightLimit;
                return true;
        }

        if (_missed >= ScrollTiming.MatchLostLimit)
        {
            reason = ScrollFinishReason.MatchLost;
            return true;
        }

        if (_mode == ScrollMode.Auto)
        {
            if (_idle >= ScrollTiming.BottomConfirmTries)
            {
                reason = ScrollFinishReason.BottomReached;
                return true;
            }
            // 刚发生 Unmatched（不管是第几次）时都不自动滚：
            // 滚轮只会把画面推得更远，让匹配和参考帧之间的重叠更小。
            // 手动模式、或者「没变化」的情况下照常滚。
            if (_missed == 0)
                Scroll();
        }
        else
        {
            Report(ScrollState.Waiting);
        }

        _phase = Phase.WaitChange;
        _phaseStart = Now;
        return false;
    }

    // ---------------- 自动滚动 ----------------

    private void EnterAuto()
    {
        if (!_movedCursor)
        {
            _cursorRestore = MonitorEnumerator.GetCursorPosition();
            _movedCursor = true;
        }

        ScrollDriver.MoveCursor(_anchor);
        _autoStart = Now;
        _handedOver = false;
        Scroll();
    }

    private void Scroll()
    {
        ScrollDriver.WheelDown(_options.WheelNotches);
        Report(ScrollState.Scrolling);
    }

    /// <summary>
    /// 光标被挪走了就交还控制权。
    ///
    /// 自动模式必须占着鼠标 —— 滚轮事件只落在光标底下的那个窗口。所以「用户动了鼠标」
    /// 和「用户想自己滚」在这里是同一件事，不必再让他去点一次模式开关：
    /// 他多半正想把某一段拖出来看，或者要去点面板上的按钮。
    ///
    /// 刚开始的那一小段不算：那时候手还停在刚点过的按钮上，多半还在往回收。
    /// </summary>
    private void CheckHandover()
    {
        if (_mode != ScrollMode.Auto) return;
        if (Now - _autoStart < ScrollTiming.HandoverGraceMs) return;

        var cursor = MonitorEnumerator.GetCursorPosition();
        if (cursor.ManhattanTo(_anchor) <= ScrollTiming.HandoverDistancePx) return;

        _mode = ScrollMode.Manual;
        _handedOver = true;
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
        _idle = 0;

        if (mode == ScrollMode.Auto)
        {
            EnterAuto();
            _phase = Phase.WaitChange;
            _phaseStart = Now;
        }
        else
        {
            Report(ScrollState.Waiting);
        }
    }

    /// <summary>
    /// 把光标放回原处。只在我们还占着鼠标时才放 ——
    /// 用户已经接管了的话，光标正在他手上往某个按钮去，这时候把它抢回来最惹人烦。
    /// </summary>
    private void RestoreCursor()
    {
        if (!_movedCursor || _handedOver) return;
        if (MonitorEnumerator.GetCursorPosition().ManhattanTo(_anchor) > ScrollTiming.HandoverDistancePx) return;

        ScrollDriver.MoveCursor(_cursorRestore);
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
