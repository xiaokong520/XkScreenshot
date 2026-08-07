using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Settings;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App;

/// <summary>
/// 一次截图的完整生命周期：冻屏 → 铺覆盖层 → 收选区与标注 → 出图。
/// 同一时刻只允许有一次会话。
/// </summary>
public sealed class CaptureController
{
    private readonly IScreenCapture _capture;
    private readonly List<OverlayWindow> _overlays = [];
    private CaptureSession? _session;

    /// <summary>上一次装出来的历史画面。来回翻同两条时省掉反复解码。</summary>
    private string? _cachedId;
    private DesktopSnapshot? _cachedSnapshot;

    public CaptureController(IScreenCapture capture) => _capture = capture;

    public bool IsActive => _session is not null;

    /// <summary>
    /// 下一次会话的起手状态。设置改完直接换掉这一份即可 ——
    /// 正在进行的会话不受影响，它已经拿走了自己那份快照。
    /// </summary>
    public CaptureDefaults Defaults { get; set; } = CaptureDefaults.Standard;

    /// <summary>
    /// 截过的区域，供覆盖层回溯。挂在这一层而不是会话里 ——
    /// 它要跨越一次次截图活下去，而会话每次都是新的。
    /// </summary>
    public CaptureHistory History { get; } = new();

    /// <summary>截图完成，参数是烧好标注的成品与用户选的去向。</summary>
    public event Action<CaptureResult>? Captured;

    public void Start()
    {
        if (IsActive) return;

        // 自己的窗口要排除掉，否则上一次残留的覆盖层会成为可选目标
        var own = Application.Current.Windows.OfType<Window>()
            .Select(w => new WindowInteropHelper(w).Handle)
            .Where(h => h != IntPtr.Zero)
            .ToHashSet();

        var snapshot = DesktopSnapshot.Take(_capture, own);
        if (snapshot.Frames.Count == 0) return;

        _session = new CaptureSession(snapshot, Defaults, History, LoadHistorySnapshot);
        _session.Confirmed += OnConfirmed;
        _session.Cancelled += Cancel;

        foreach (var frame in snapshot.Frames)
        {
            var overlay = new OverlayWindow(_session, frame);
            _overlays.Add(overlay);
            overlay.Show();
        }

        // 光标所在那块屏才激活，键盘事件才有着落；其余的只是铺满不抢焦点
        var cursor = Core.Monitors.MonitorEnumerator.GetCursorPosition();
        var active = _overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cursor))
                     ?? _overlays[0];
        active.Activate();
        active.Focus();

        _session.UpdateCursor(cursor);
        _session.UpdateHover(cursor);
    }

    /// <summary>
    /// 成品位图在 Confirm 时就已经渲染好了，所以这里可以先收掉覆盖层再派发 ——
    /// 覆盖层必须在贴图窗口出现之前消失，否则新贴图会被压在它下面。
    /// </summary>
    private void OnConfirmed(CaptureResult result)
    {
        // 只记真截下来的那些。取消掉的选区不算截过 ——
        // 那多半正是一个框歪了、用户不想要的框，把它塞进历史只会占掉一格。
        RecordHistory(result.Bounds);

        Teardown();
        Captured?.Invoke(result);
    }

    /// <summary>
    /// 把这一次记进历史：选区当场记，整屏画面丢给后台编码，编码完再回来认领。
    /// 必须在 <see cref="Teardown"/> 之前调 —— 画面要从还活着的那个会话上取。
    /// </summary>
    private void RecordHistory(PixelRect bounds)
    {
        if (_session is null || History.Capacity == 0) return;

        // 从历史画面上确认的：图就是刚才装进来的那一张，再存一遍纯属浪费磁盘
        if (_session.HistorySource is { Image: not null } source)
        {
            History.Record(bounds, source.Desktop, source.Image);
            return;
        }

        var snapshot = _session.Snapshot;
        var desktop = snapshot.VirtualBounds;
        History.Record(bounds, desktop, null);

        // 存画面整个排到 UI 空下来之后。多屏合成那一步只能在 UI 线程做（要 RenderTargetBitmap），
        // 实测 3840×1080 要九十来毫秒 —— 压在确认那一下上，用户会看到覆盖层关掉之前顿一下，
        // 而这一顿换来的东西他此刻根本不关心。编码更慢，那个能甩就甩到后台线程。
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            BitmapSource full;
            try
            {
                full = snapshot.Crop(desktop);
            }
            catch (Exception)
            {
                // 合成失败就让这一条停在「只有框」，不该反过来影响已经截好的图
                return;
            }

            Task.Run(() => HistoryStore.SaveImage(full)).ContinueWith(t =>
            {
                if (t.Result is not { } id) return;

                // 编码期间那一条可能已经被后来的截图挤出去了，那就把白存的文件删掉
                if (!History.Attach(bounds, id)) HistoryStore.PruneImages(History.ImageIds());
            }, TaskScheduler.FromCurrentSynchronizationContext());
        });
    }

    /// <summary>
    /// 把历史里的一条装成可以顶替实时冻屏的快照。
    ///
    /// 按当前这几台显示器切帧，所以用的是本次会话的显示器列表而不是重新枚举一遍 ——
    /// 覆盖层窗口就是照着它摆的，两边必须是同一份，否则会出现「画面切了、窗口还在老位置」。
    /// </summary>
    private DesktopSnapshot? LoadHistorySnapshot(HistoryEntry entry)
    {
        if (_session is null || entry.Image is null || entry.Desktop.IsEmpty) return null;

        // 来回翻的时候别反复解码同一张 PNG：一张 3840×1080 解出来是十几兆
        if (_cachedId == entry.Image) return _cachedSnapshot;

        var image = HistoryStore.LoadImage(entry.Image);
        if (image is null) return null;

        var monitors = _session.LiveSnapshot.Frames.Select(f => f.Monitor).ToList();
        _cachedSnapshot = DesktopSnapshot.FromImage(image, entry.Desktop, monitors);
        _cachedId = entry.Image;
        return _cachedSnapshot;
    }

    public void Cancel() => Teardown();

    private void Teardown()
    {
        if (_session is not null)
        {
            _session.Confirmed -= OnConfirmed;
            _session.Cancelled -= Cancel;
            _session.Dispose();
            _session = null;
        }

        foreach (var overlay in _overlays)
            overlay.Close();
        _overlays.Clear();
    }
}
