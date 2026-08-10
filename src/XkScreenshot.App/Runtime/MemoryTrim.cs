using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace XkScreenshot.App.Runtime;

/// <summary>
/// 一次截图收摊之后把内存还给系统。
///
/// 为什么需要显式这么做：这个程序平时是一个待在托盘里、几乎不动的进程，偶尔被叫起来
/// 处理几张整屏尺寸的位图。这种「长期极闲 + 偶尔一大把」的形状恰好是 GC 最不擅长的：
/// 一次截图会让堆瞬间涨到几十兆，图早就没人引用了，但没有新的分配压力去触发第二代回收 ——
/// 于是那几十兆就一直挂在提交内存里，任务管理器上看着就是「截了一次图涨到 140 MB 再也不降」。
/// 真正吃内存的不是活着的对象，是没人来收的垃圾。
///
/// 另外整屏位图全都走大对象堆，那里默认不压缩，反复几次截图会留下一堆填不满的空洞，
/// 所以顺手压一次 —— 平时绝不该这么干，这里的场合恰好是它成立的那个例外：
/// 用户已经拿到图了，进程正闲着，几十毫秒的停顿没有任何人看得见。
/// </summary>
public static class MemoryTrim
{
    /// <summary>
    /// 等这么久再收。截图刚结束时后面还跟着一串活（存历史那张 PNG 在后台编码、
    /// 贴图窗口正在出现），这时候收既收不干净 —— 那些东西还被引用着 ——
    /// 又会去和它们抢 CPU。等场面静下来再收。
    /// </summary>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private static DispatcherTimer? _timer;

    /// <summary>
    /// 排一次回收。重复调只是把闹钟往后推 ——
    /// 连着截好几张图时不该每张都停一下，最后那张之后收一次就够。
    /// </summary>
    public static void Schedule()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess()) return;
        // 退出路上也会走到这里（收长截图那一摊）。进程马上就没了，收它做什么
        if (dispatcher.HasShutdownStarted) return;

        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
            {
                Interval = Delay,
            };
            _timer.Tick += (_, _) =>
            {
                _timer!.Stop();
                Reclaim();
            };
        }

        // Stop + Start 才算重新上弦，光 Start 对一个正在跑的计时器没有任何作用
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// 收一遍。两趟是必要的：整屏位图的非托管内存挂在 WIC 对象的终结器上，
    /// 第一趟只是把它们送进终结队列，等终结器真跑完才轮得到那块内存，
    /// 而那时候得再收一次才把腾出来的空间还给系统。
    /// </summary>
    private static void Reclaim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        GC.WaitForPendingFinalizers();

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        TrimWorkingSet();
    }

    /// <summary>
    /// 把工作集里的冷页交还系统。
    ///
    /// 上面那两趟只管托管堆，而截一次图真正涨起来的那一大块在托管堆之外：WPF 第一次显示窗口
    /// 时拉起来的整套图形栈（D3D 设备、显卡驱动的用户态 DLL、字体缓存），以及两块全屏窗口的
    /// 渲染表面。那些内存不是垃圾、收不掉，但它在两次截图之间整个是凉的 ——
    /// 而两次截图之间就是这个程序 99.9% 的时间。
    ///
    /// 交还之后下一次截图会为此付一点缺页的代价，但那些页绝大多数是 DLL 映像，
    /// 从系统文件缓存里回来，不走磁盘。对一个成天待在托盘里的程序，这笔交易明显划算。
    /// </summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch (Exception)
        {
            // 纯属锦上添花的一步，失败了什么也不影响
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
