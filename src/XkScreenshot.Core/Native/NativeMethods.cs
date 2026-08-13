using System.Runtime.InteropServices;
using System.Text;

namespace XkScreenshot.Core.Native;

public static class NativeMethods
{
    // ---------------- user32 ----------------

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    /// <summary>SendInput 的鼠标输入。</summary>
    public const uint INPUT_MOUSE = 0;

    public const uint MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>滚轮一格。SendInput 的 mouseData 用它的整数倍，负数是往下滚。</summary>
    public const int WHEEL_DELTA = 120;

    /// <summary>
    /// 把某个窗口排除在截屏之外。长截图的控制面板与区域边框都设它 ——
    /// 抓帧走的是屏幕 DC，面板压在目标区域上就会被拍进去。
    /// Win10 2004（19041）起才有 WDA_EXCLUDEFROMCAPTURE，更早的系统上调用会失败，
    /// 那时只能靠把面板摆到区域外面躲开。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    public const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;
    /// <summary>UpdateLayeredWindow 用逐像素 alpha 合成（blend.SourceConstantAlpha 作整体不透明度）。</summary>
    public const uint ULW_ALPHA = 2;
    /// <summary>SetLayeredWindowAttributes 只改整体不透明度，不动画面。</summary>
    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>置顶带。贴图的右键菜单用它：贴图自己是 TopMost，菜单不加这个会被压在底下。</summary>
    public const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>鼠标穿透。长截图那圈区域边框用它 —— 它盖在目标窗口上，绝不能挡住点击。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>不进 Alt+Tab。回执浮窗用：它自己两秒就没了，没有「切回去」这一说。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// 点了也不激活。回执浮窗用：它是浮在别人界面上的一句话，
    /// 用户正打字的那个窗口不该因为它冒出来、或者被点了一下就丢掉焦点。
    /// </summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// 分层窗口。贴图用它：内容走 UpdateLayeredWindow 整幅送上去，DWM 原子合成，
    /// 几何和画面在同一帧到位，缩放不会闪（见 PinForm 类注释）。
    /// </summary>
    public const int WS_EX_LAYERED = 0x00080000;

    public const uint MONITORINFOF_PRIMARY = 1;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;

    public const int WM_HOTKEY = 0x0312;

    // ---------------- gdi32 ----------------

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;
    public const uint SRCCOPY = 0x00CC0020;
    /// <summary>抓取分层窗口（半透明窗口、部分输入法候选框）必须带上这个标志。</summary>
    public const uint CAPTUREBLT = 0x40000000;

    // ---------------- shcore / dwmapi ----------------

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    /// <summary>让系统把标题栏画成深色。Win10 2004 以前的版本不认这个值，调用会失败但无副作用。</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
}
