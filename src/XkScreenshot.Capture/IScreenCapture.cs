using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Capture;

/// <summary>
/// 抓屏后端。M0 只有 GDI 实现；M2 做长截图时会加 WindowsGraphicsCapture 实现
/// （能按 HWND 抓、能抓被遮挡窗口、硬件加速），GDI 保留为兜底。
/// </summary>
public interface IScreenCapture
{
    /// <summary>后端是否在当前系统上可用。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 抓取虚拟屏幕坐标系下的一块矩形。
    /// dpi 决定返回位图的 DpiX/DpiY，让它在对应显示器上能 1 图像像素 = 1 设备像素地渲染。
    /// </summary>
    CapturedFrame CaptureRect(PixelRect rect, double dpiX, double dpiY);
}
