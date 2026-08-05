using System.Runtime.InteropServices;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Core.Monitors;

public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data)
        {
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                return true;

            // GetDpiForMonitor 在 Win8.1 以下不存在；失败就退回 96（100%）。
            uint dpiX = 96, dpiY = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMonitor, MonitorDpiType.Effective, out var x, out var y) == 0)
                    (dpiX, dpiY) = (x, y);
            }
            catch (DllNotFoundException) { /* 保持 96 */ }
            catch (EntryPointNotFoundException) { /* 保持 96 */ }

            list.Add(new MonitorInfo(
                hMonitor,
                mi.szDevice,
                ToPixelRect(mi.rcMonitor),
                ToPixelRect(mi.rcWork),
                dpiX, dpiY,
                (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            return true;
        }

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // 主屏排前面，方便调试时读日志
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ToList();
    }

    /// <summary>整个虚拟桌面的外接矩形。</summary>
    public static PixelRect VirtualBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return PixelRect.Empty;
        int l = monitors.Min(m => m.Bounds.X);
        int t = monitors.Min(m => m.Bounds.Y);
        int r = monitors.Max(m => m.Bounds.Right);
        int b = monitors.Max(m => m.Bounds.Bottom);
        return PixelRect.FromLtrb(l, t, r, b);
    }

    public static MonitorInfo? FromPoint(IReadOnlyList<MonitorInfo> monitors, PixelPoint pt)
        => monitors.FirstOrDefault(m => m.Bounds.Contains(pt));

    public static PixelPoint GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var p);
        return new PixelPoint(p.X, p.Y);
    }

    private static PixelRect ToPixelRect(RECT r)
        => PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
}
