using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Gif;

namespace XkScreenshot.App.Gif;

/// <summary>一段录好的 GIF 成品。数据已经编码完，保存与否由外面决定。</summary>
public sealed record GifResult(
    byte[] Data, PixelRect Bounds, int Width, int Height,
    TimeSpan Duration, int Frames, GifStopReason StopReason);

/// <summary>
/// 一次 GIF 录制的完整生命周期：铺红框与控制条 → 后台抓帧 → 停止后编码 → 交出成品。
///
/// 抓帧在 <see cref="GifRecorder"/> 自己的线程上，编码又是一段 Task —— 中间只有
/// 「点停止」这一下会在界面线程上等一个抓帧周期（默认 100ms），换来的是最后那一帧
/// 不会一边被读、一边被下一次 BitBlt 覆盖。其余全部异步，控制条不会僵住。
///
/// 取消与完成共用一条收尾路径，靠 <see cref="_encodeCts"/> 认领：取消之后那条编码任务
/// 可能还在跑几十毫秒，它回头想关窗口时必须发现「这一摊已经不归我了」，否则它会把
/// 用户新开的一次录制连人带窗一起收掉。
/// </summary>
internal sealed class GifCaptureController
{
    private GifRecorder? _recorder;
    private GifPanelWindow? _panel;
    private GifFrameWindow? _frame;
    private DispatcherTimer? _ticker;
    private CancellationTokenSource? _encodeCts;
    private PixelRect _region;

    /// <summary>这一摊还在（录制中、编码中，或者正等着被收尾）。</summary>
    private bool _active;

    /// <summary>已经在收工了。停止按钮点第二下不该再走一遍停表。</summary>
    private bool _stopping;

    public bool IsRunning => _active;

    /// <summary>编码完了，参数是成品。窗口这时已经全部收掉。</summary>
    public event Action<GifResult>? Completed;

    /// <summary>这一摊结束了，不论有没有成品。</summary>
    public event Action? Ended;

    /// <summary>硬错误（抓不到画面、编码失败），值得弹一句话。</summary>
    public event Action<string>? Notice;

    /// <summary>顺口说一句（到时长上限了、缓冲满了），不该打断用户。</summary>
    public event Action<string>? Hint;

    public void Start(PixelRect region, GifOptions options, bool dark)
    {
        if (_active || region.IsEmpty) return;

        _region = region;
        _active = true;
        _stopping = false;

        _frame = new GifFrameWindow(region);
        _frame.Show();

        var panel = new GifPanelWindow(region, dark);
        _panel = panel;
        panel.StopRequested += Stop;
        panel.CancelRequested += Cancel;

        // 面板被别的路径关掉（Alt+F4）等同于取消：留一个没人管的录制线程在后台空转最糟
        panel.Closed += (_, _) =>
        {
            if (_panel == panel) Cancel();
        };

        panel.Show();

        var recorder = new GifRecorder(region, options);
        _recorder = recorder;

        // 等合成器把覆盖层撤下去再抓第一帧，见 GifRecorder.Start(int)
        recorder.Start(120);

        var ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        ticker.Tick += (_, _) =>
        {
            _panel?.UpdateRecording(recorder.Elapsed);

            // 撞到时长/内存上限时抓帧线程会自己收工，没有人按停止 ——
            // 这里替它补上收尾，否则控制条会永远停在最后一帧上
            if (recorder.HasStopped) Stop();
        };
        ticker.Start();
        _ticker = ticker;
    }

    /// <summary>
    /// 用户点停止 / 按回车：先停表，再编码。
    ///
    /// 控制条立刻换成进度态，而不是等编码完才动 —— 一段稍长的录制要编十几秒，
    /// 这期间屏幕上要是还显示着一个定格的「00:12.4」，看起来就是卡死了。
    /// </summary>
    private void Stop()
    {
        if (!_active || _stopping) return;
        _stopping = true;

        StopTicker();

        var recorder = _recorder;
        _recorder = null;
        if (recorder is null)
        {
            // 理论到不了这儿（停止只可能从有录制的那一摊发起），但状态不能卡住
            Finish();
            return;
        }

        _panel?.ShowEncoding();

        var recording = recorder.Stop();
        recorder.Dispose();

        if (recording.FrameCount == 0)
        {
            Notice?.Invoke(recording.StopReason == GifStopReason.Failed
                ? "录制失败：抓不到画面"
                : "没有录到画面");

            Finish();
            return;
        }

        // 到上限自动停：说出来，否则用户会以为是自己手滑按到了什么
        if (recording.StopReason == GifStopReason.DurationLimit)
            Hint?.Invoke($"已到最长录制时长（{Math.Round(recording.Duration.TotalSeconds)} 秒），自动停止");
        else if (recording.StopReason == GifStopReason.MemoryLimit)
            Hint?.Invoke("画面变化太密集，缓冲已满，先停在这里（录视频这类内容最容易撞到）");
        else if (recording.StopReason == GifStopReason.Failed)
            Hint?.Invoke("录制中途抓不到画面了，已保存前面的部分");

        var cts = new CancellationTokenSource();
        _encodeCts = cts;

        // 用 IProgress 而不是 Progress：Report 是显式接口实现，写具体类型反而调不到
        IProgress<double> progress = new Progress<double>(p => _panel?.UpdateProgress(p));

        Task.Run(() => GifEncoder.Encode(recording, p => progress.Report(p), cts.Token))
            .ContinueWith(
                task => FinishEncoding(task, recording, cts),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FinishEncoding(Task<byte[]> task, GifRecording recording, CancellationTokenSource cts)
    {
        // 已经被取消、或者已经被新一次录制接管：这一摊的所有权不在了，什么都别碰
        if (!ReferenceEquals(_encodeCts, cts)) return;

        _encodeCts = null;
        cts.Dispose();

        if (task.IsCanceled) return;

        if (task.IsFaulted)
        {
            string message = task.Exception?.GetBaseException().Message ?? "未知错误";
            Finish();
            Notice?.Invoke("生成 GIF 失败：" + message);
            return;
        }

        var data = task.Result;
        Finish();
        Completed?.Invoke(new GifResult(
            data, _region, recording.Width, recording.Height,
            recording.Duration, recording.FrameCount, recording.StopReason));
    }

    private void Cancel()
    {
        if (!_active) return;

        StopTicker();

        // 先把所有权摘掉，再取消：编码任务的收尾会看到 _encodeCts 不匹配而直接返回
        var cts = _encodeCts;
        _encodeCts = null;
        cts?.Cancel();
        cts?.Dispose();

        StopRecorderAndDiscard();
        Finish();
    }

    /// <summary>程序退出时把还开着的这一摊收掉。</summary>
    public void Abort()
    {
        if (!_active) return;

        StopTicker();

        var cts = _encodeCts;
        _encodeCts = null;
        cts?.Cancel();
        cts?.Dispose();

        StopRecorderAndDiscard();

        CloseWindows();
        _active = false;
        _stopping = true;
        Ended?.Invoke();
    }

    private void StopRecorderAndDiscard()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;

        recorder.Stop();
        recorder.Dispose();
    }

    private void StopTicker()
    {
        var ticker = _ticker;
        _ticker = null;
        ticker?.Stop();
    }

    /// <summary>收掉这一摊：先关窗，再通知。窗口必须在回执/保存对话框出现之前消失。</summary>
    private void Finish()
    {
        if (!_active) return;

        CloseWindows();
        _active = false;
        _stopping = true;
        Ended?.Invoke();
    }

    private void CloseWindows()
    {
        // 先摘引用再关：关窗会回调 Closed → Cancel()，那时 _active 已经由调用方管着，
        // 这一层只需要保证「自己关掉的窗口不再触发取消」
        if (_panel is { } panel)
        {
            _panel = null;
            panel.Close();
        }

        if (_frame is { } frame)
        {
            _frame = null;
            frame.Close();
        }
    }
}
