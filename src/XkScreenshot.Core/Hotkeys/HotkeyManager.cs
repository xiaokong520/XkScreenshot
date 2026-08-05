using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Core.Hotkeys;

public sealed record HotkeyBinding(string Name, HotkeyModifiers Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}

public sealed record HotkeyRegistrationResult(HotkeyBinding Binding, bool Success, string? Error);

/// <summary>
/// 全局热键。注意 RegisterHotKey 被别人占用时是「静默失败」的，
/// 所以这里必须把失败原因带出去给 UI 提示，否则用户只会觉得「按了没反应」。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private int _nextId = 1;
    private bool _disposed;

    public event Action<HotkeyBinding>? Pressed;

    public HotkeyManager()
    {
        // message-only 窗口：不可见、不进任务栏、只负责收 WM_HOTKEY
        var parameters = new HwndSourceParameters("XkScreenshot.HotkeySink")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public HotkeyRegistrationResult Register(HotkeyBinding binding)
    {
        int id = _nextId++;
        uint mods = (uint)(binding.Modifiers | HotkeyModifiers.NoRepeat);

        if (NativeMethods.RegisterHotKey(_source.Handle, id, mods, binding.VirtualKey))
        {
            _registered[id] = binding;
            return new HotkeyRegistrationResult(binding, true, null);
        }

        int err = Marshal.GetLastWin32Error();
        // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED
        string message = err == 1409
            ? $"热键 {binding} 已被其他程序占用"
            : $"热键 {binding} 注册失败：{new Win32Exception(err).Message}";
        return new HotkeyRegistrationResult(binding, false, message);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            Pressed?.Invoke(binding);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (int id in _registered.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
