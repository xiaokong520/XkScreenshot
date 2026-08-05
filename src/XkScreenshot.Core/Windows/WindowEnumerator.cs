using System.Runtime.InteropServices;
using System.Text;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Core.Windows;

/// <summary>一个可作为截图目标的顶层窗口。列表顺序即 z-order，越靠前越在上层。</summary>
public sealed record CapturedWindow(IntPtr Handle, string ClassName, string Title, PixelRect Bounds);

public static class WindowEnumerator
{
    /// <summary>桌面壁纸宿主，命中它没有意义，直接过滤掉。</summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>
    /// 必须在冻屏「之前」调用：一旦覆盖层弹出来，z-order 就被我们自己污染了。
    /// </summary>
    public static IReadOnlyList<CapturedWindow> Enumerate(IReadOnlyCollection<IntPtr> exclude)
    {
        var result = new List<CapturedWindow>();
        var cls = new StringBuilder(256);
        var title = new StringBuilder(512);

        bool Callback(IntPtr hWnd, IntPtr _)
        {
            if (exclude.Contains(hWnd)) return true;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.IsIconic(hWnd)) return true;

            // UWP 有大量「已存在但被隐藏」的幽灵窗口，只能靠 DWM cloaked 位识别
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            cls.Clear();
            NativeMethods.GetClassName(hWnd, cls, cls.Capacity);
            string className = cls.ToString();
            if (IgnoredClasses.Contains(className)) return true;

            var bounds = GetFrameBounds(hWnd);
            if (bounds.Width < 8 || bounds.Height < 8) return true;

            title.Clear();
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);

            result.Add(new CapturedWindow(hWnd, className, title.ToString(), bounds));
            return true;
        }

        NativeMethods.EnumWindows(Callback, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Win10+ 的 GetWindowRect 会带上不可见的阴影边框（左右各约 7px），
    /// 用它截图会多出一圈透明边。必须用 DWM 的实际边框。
    /// </summary>
    public static PixelRect GetFrameBounds(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT dwm, Marshal.SizeOf<RECT>()) == 0)
            return PixelRect.FromLtrb(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);

        return NativeMethods.GetWindowRect(hWnd, out RECT r)
            ? PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom)
            : PixelRect.Empty;
    }

    /// <summary>命中测试：返回 z-order 最靠上的、包含该点的窗口。</summary>
    public static CapturedWindow? HitTest(IReadOnlyList<CapturedWindow> windows, PixelPoint pt)
        => windows.FirstOrDefault(w => w.Bounds.Contains(pt));
}
