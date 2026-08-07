using System.Windows;
using System.Windows.Automation;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Core.Windows;

/// <summary>一次控件级命中的结果。</summary>
public enum ElementHit
{
    /// <summary>这个窗口的控件树还在后台扫，暂时只能退回整窗。</summary>
    Scanning,
    /// <summary>扫完了，但这个点上没有够格的控件（窗口边框、或者压根没有 UIA 提供者）。</summary>
    None,
    /// <summary>命中了一个控件。</summary>
    Found,
}

/// <summary>
/// 控件级命中检测：把一个顶层窗口的 UIA 子树摊平成一组矩形，之后的命中就是纯几何运算。
///
/// 为什么不用 UIA 的 ElementFromPoint：那个点上永远是我们自己的覆盖层 —— 它是 topmost 的，
/// 而且必须接收鼠标，没法做成点击穿透。摊平成矩形还有一个额外好处：命中判定与冻屏画面同步，
/// 用户看到的和量到的是同一时刻的界面，跟 <see cref="WindowEnumerator"/> 的整窗命中完全同构。
///
/// 扫描一律走后台线程。UIA 是跨进程 RPC，复杂界面（浏览器、Electron）一次子树查询要几百毫秒，
/// 放在 UI 线程上鼠标会整个卡住。扫描期间调用方拿到 false，退回整窗高亮，扫完再自动补上。
/// </summary>
public sealed class UiElementProbe : IDisposable
{
    /// <summary>小于这个边长的控件不收：那种尺寸下用户点不准，收进来只会让高亮跳来跳去。</summary>
    private const int MinSide = 6;

    /// <summary>单个窗口最多收这么多矩形。深不见底的 DOM 树见过十万级，全留着命中会变成线性扫描的瓶颈。</summary>
    private const int MaxRects = 20000;

    /// <summary>建 Probe 那一刻的线程（UI 线程），扫描结果一律 Post 回这里，缓存才不必上锁。</summary>
    private readonly SynchronizationContext? _sync;

    /// <summary>按 HWND 缓存。值为 null 表示「正在扫」，用来防止同一个窗口被反复排队。</summary>
    private readonly Dictionary<IntPtr, PixelRect[]?> _cache = new();

    private bool _disposed;

    public UiElementProbe() => _sync = SynchronizationContext.Current;

    /// <summary>某个窗口扫完了。调用方据此重跑一次命中，把结果补到画面上。</summary>
    public event Action? Updated;

    /// <summary>
    /// 命中窗口内最内层的那个控件。第一次问到某个窗口时会就地排一次后台扫描并返回
    /// <see cref="ElementHit.Scanning"/>；扫完之后 <see cref="Updated"/> 会通知调用方重问一次。
    /// </summary>
    /// <param name="frame">窗口的实际边框，用来裁掉跑到窗口外面去的控件矩形。</param>
    public ElementHit HitTest(IntPtr hWnd, PixelRect frame, PixelPoint point, out PixelRect rect)
    {
        rect = PixelRect.Empty;
        if (_disposed || hWnd == IntPtr.Zero) return ElementHit.None;

        if (!_cache.TryGetValue(hWnd, out var rects))
        {
            _cache[hWnd] = null;
            QueueScan(hWnd, frame);
            return ElementHit.Scanning;
        }

        if (rects is null) return ElementHit.Scanning;

        // 列表按面积升序，第一个包住该点的就是最内层的控件
        foreach (var r in rects)
        {
            if (!r.Contains(point)) continue;
            rect = r;
            return ElementHit.Found;
        }
        return ElementHit.None;
    }

    private void QueueScan(IntPtr hWnd, PixelRect frame)
    {
        // 线程池线程默认是 MTA，正是 UIA 客户端该待的公寓模型
        Task.Run(() =>
        {
            var rects = Scan(hWnd, frame);
            if (_disposed) return;

            void Publish(object? _)
            {
                if (_disposed) return;
                _cache[hWnd] = rects;
                Updated?.Invoke();
            }

            if (_sync is not null) _sync.Post(Publish, null);
            else Publish(null);
        });
    }

    private static PixelRect[] Scan(IntPtr hWnd, PixelRect frame)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root is null) return [];

            // 一次批量取回所有子孙的包围盒。不加 CacheRequest 的话，每读一个 BoundingRectangle
            // 都是一次跨进程往返，几千个控件能拖到几十秒。
            var request = new CacheRequest
            {
                TreeScope = TreeScope.Subtree,
                TreeFilter = Automation.ControlViewCondition,
                AutomationElementMode = AutomationElementMode.None,
            };
            request.Add(AutomationElement.BoundingRectangleProperty);

            AutomationElementCollection found;
            using (request.Activate())
                found = root.FindAll(TreeScope.Subtree, Automation.ControlViewCondition);

            var seen = new HashSet<PixelRect>();
            var rects = new List<PixelRect>();

            foreach (AutomationElement element in found)
            {
                if (rects.Count >= MaxRects) break;

                Rect box;
                try
                {
                    box = element.Cached.BoundingRectangle;
                }
                catch (Exception)
                {
                    // 控件在扫描途中消失是常态，跳过就好
                    continue;
                }

                if (box.IsEmpty || double.IsInfinity(box.Width) || double.IsInfinity(box.Height)) continue;

                // UIA 的包围盒本来就是物理像素（进程声明了 PerMonitorV2），不需要任何 DPI 换算
                var px = PixelRect.FromLtrb(
                    (int)Math.Round(box.Left), (int)Math.Round(box.Top),
                    (int)Math.Round(box.Right), (int)Math.Round(box.Bottom)).Intersect(frame);

                if (px.Width < MinSide || px.Height < MinSide) continue;
                if (seen.Add(px)) rects.Add(px);
            }

            // 面积升序 = 由内到外。命中时第一个包住光标的就是最深的那个控件，
            // 不必再去还原树的层级关系（父子的包围盒本来也未必真的嵌套）。
            rects.Sort(static (a, b) => a.Area.CompareTo(b.Area));
            return [.. rects];
        }
        catch (Exception)
        {
            // 目标进程没起 UIA 提供者、权限不够（管理员窗口）、扫到一半窗口关了 ——
            // 这些都不该把截图流程带下水，静默退回整窗即可
            return [];
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
        Updated = null;
    }
}
