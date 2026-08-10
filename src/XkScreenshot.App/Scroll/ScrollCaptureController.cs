using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Scroll;

/// <summary>
/// 一次长截图的完整生命周期：铺边框与面板 → 跑拼接引擎 → 出图。
///
/// 引擎整个跑在后台线程上（抓帧 + 逐像素匹配，压在界面线程上会让面板跟着一卡一卡），
/// 所以它发出来的每一个事件都要在这里转回界面线程 —— 这也是这一层存在的主要理由：
/// 引擎不必知道 WPF，界面不必知道线程。
/// </summary>
public sealed class ScrollCaptureController
{
    private readonly Dispatcher _dispatcher;

    private ScrollCaptureEngine? _engine;
    private ScrollPanelWindow? _panel;
    private ScrollFrameWindow? _frame;
    private PixelRect _region;
    private CaptureAction _action;

    /// <summary>已经在收工了。按钮点第二下不该再改去向 —— 图早就在编码路上了。</summary>
    private bool _finishing;

    public ScrollCaptureController()
        => _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool IsRunning => _engine is not null;

    /// <summary>拼完了，参数是成品、它在屏幕上的原始位置（贴图要用）、以及去向。</summary>
    public event Action<BitmapSource, PixelRect, CaptureAction>? Completed;

    /// <summary>
    /// 这一摊结束了，不论拼出图没有。取消掉的长截图不会有 <see cref="Completed"/>，
    /// 而调用方为它暂存的东西（那张冻屏）照样得有个地方放掉。
    /// </summary>
    public event Action? Ended;

    /// <summary>结束得不太顺利，得跟用户说一声。</summary>
    public event Action<string>? Notice;

    public void Start(PixelRect region, ScrollOptions options, CaptureAction defaultAction)
    {
        if (IsRunning || region.IsEmpty) return;

        _region = region;
        _action = defaultAction;
        _finishing = false;

        var engine = new ScrollCaptureEngine(region, options);
        _engine = engine;

        _frame = new ScrollFrameWindow(region);
        _frame.Show();

        var panel = new ScrollPanelWindow(region, options.Mode, defaultAction);
        _panel = panel;
        panel.ModeChanged += mode => engine.SetMode(mode);
        panel.Accepted += Accept;
        panel.Cancelled += Cancel;
        // 面板被别的方式关掉（Alt+F4）等同于取消：留着一个没人管的引擎在后台空转最糟
        panel.Closed += (_, _) => Cancel();
        panel.Show();

        engine.Progress += OnProgress;
        engine.Finished += OnFinished;
        engine.Start();
    }

    private void Accept(CaptureAction action)
    {
        if (_finishing) return;
        _finishing = true;
        _action = action;
        _engine?.RequestFinish();
    }

    private void Cancel()
    {
        if (_engine is null) return;
        _finishing = true;
        _engine.Cancel();
    }

    /// <summary>程序退出时把还开着的这一摊收掉。</summary>
    public void Abort()
    {
        if (_engine is null) return;

        var engine = _engine;
        _engine = null;
        engine.Progress -= OnProgress;
        engine.Finished -= OnFinished;
        engine.Cancel();
        engine.Dispose();
        CloseWindows();
        Ended?.Invoke();
    }

    private void OnProgress(ScrollProgress progress)
        => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => _panel?.UpdateProgress(progress));

    private void OnFinished(ScrollCaptureResult result)
        => _dispatcher.BeginInvoke(() => Finish(result));

    private void Finish(ScrollCaptureResult result)
    {
        var engine = _engine;
        if (engine is null) return;

        _engine = null;
        engine.Progress -= OnProgress;
        engine.Finished -= OnFinished;
        engine.Dispose();

        // 窗口必须先收掉：贴图会在成品出来那一刻弹出来，
        // 面板还挂着的话新贴图会被压在它下面
        CloseWindows();

        if (Describe(result) is { } message) Notice?.Invoke(message);
        if (result.Image is not null) Completed?.Invoke(result.Image, _region, _action);

        // 取消掉的那一趟没有 Completed，但它一样是结束了
        Ended?.Invoke();
    }

    /// <summary>
    /// 该不该说一句话。正常收工不说 —— 复制/保存/贴图各自本来就有回执，
    /// 再叠一条「长截图完成」只是让气泡排队。只有结果和用户预期不一样时才出声。
    /// </summary>
    private static string? Describe(ScrollCaptureResult result) => result.Reason switch
    {
        ScrollFinishReason.HeightLimit =>
            $"已到长截图高度上限，停在 {result.Height} 像素（可在设置里调大）",
        ScrollFinishReason.MatchLost =>
            $"连着几帧没能和前面对上，长截图停在 {result.Height} 像素",
        ScrollFinishReason.Failed => "长截图失败：抓不到画面",
        _ => null,
    };

    private void CloseWindows()
    {
        // 先摘掉引用再关：关窗会回调 Closed → Cancel()，那时候 _engine 已经是 null，
        // 那一趟就自然成了空操作
        if (_panel is { } panel)
        {
            _panel = null;
            panel.Close();
        }

        _frame?.Close();
        _frame = null;
    }
}
