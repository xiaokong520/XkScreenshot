using System.Windows.Interop;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Core.Hotkeys;

/// <summary>
/// 探一个组合键有没有被别人占了 —— 办法就是真去注册一次，成了立刻撤掉。
///
/// 没有别的办法：Windows 不提供「查询某个热键归谁」的 API，RegisterHotKey 的成败
/// 是唯一的信息来源。好在注册-撤销这一对操作很轻，也不会打扰到占着它的那个程序。
///
/// 注意 RegisterHotKey 是全系统去重的，本进程已经注册的同样会被判成占用。
/// 所以探测期间调用方必须先把自己的热键撤掉，否则每个热键都会把自己认成冲突。
/// </summary>
public sealed class HotkeyProbe : IDisposable
{
    /// <summary>只是个占位 id，探完就撤，不会和别处的 id 打架。</summary>
    private const int ProbeId = 0x4B01;

    private readonly HwndSource _source;
    private bool _disposed;

    public HotkeyProbe()
    {
        var parameters = new HwndSourceParameters("XkScreenshot.HotkeyProbe")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
    }

    /// <summary>true = 这个组合键已经被占用。没设热键（虚拟键为 0）时恒为 false。</summary>
    public bool IsTaken(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (_disposed || virtualKey == 0) return false;

        uint mods = (uint)(modifiers | HotkeyModifiers.NoRepeat);
        if (!NativeMethods.RegisterHotKey(_source.Handle, ProbeId, mods, virtualKey)) return true;

        NativeMethods.UnregisterHotKey(_source.Handle, ProbeId);
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 正常路径下探测完就撤了，这里只是兜底：万一探测中途抛了异常，
        // 那个组合键会一直挂在这个窗口上，别人再也注册不上
        NativeMethods.UnregisterHotKey(_source.Handle, ProbeId);
        _source.Dispose();
    }
}
