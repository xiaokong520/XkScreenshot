using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Core.Monitors;

/// <summary>
/// 一台显示器。Bounds / WorkArea 都是虚拟屏幕物理像素（PMv2 下 Win32 返回的就是物理像素）。
/// </summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    /// <summary>缩放倍率，如 1.0 / 1.25 / 1.5 / 2.0。</summary>
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;
}
