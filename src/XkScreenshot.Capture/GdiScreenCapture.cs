using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Capture;

/// <summary>
/// BitBlt 抓屏。兼容性最好（Win7 起全平台可用），是所有其他后端的兜底。
/// 局限：抓不到受 DRM 保护的窗口（结果是纯黑），也抓不到部分独占全屏的 D3D 应用。
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    public bool IsAvailable => true;

    public CapturedFrame CaptureRect(PixelRect rect, double dpiX, double dpiY)
    {
        if (rect.IsEmpty)
            throw new ArgumentException("抓取矩形为空", nameof(rect));

        IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            throw new InvalidOperationException("GetDC(NULL) 失败");

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                throw new InvalidOperationException("CreateCompatibleDC 失败");

            // biHeight 取负 => top-down DIB，扫描线顺序和 WPF 一致，省一次翻转
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = rect.Width,
                    biHeight = -rect.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            hBitmap = NativeMethods.CreateDIBSection(hdcScreen, ref bmi,
                NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");

            hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

            if (!NativeMethods.BitBlt(hdcMem, 0, 0, rect.Width, rect.Height,
                    hdcScreen, rect.X, rect.Y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new InvalidOperationException(
                    $"BitBlt 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");

            int stride = rect.Width * 4;

            // 直接从 DIB 内存建位图，不在托管堆上过一手：整屏是 8~15 MB 一片，
            // 中转那一份除了给大对象堆添垃圾之外什么也不做（WPF 无论如何都要自己拷一份）。
            //
            // 格式取 Bgr32 而不是 Bgra32：GDI 不维护 alpha，BitBlt 出来的第四个字节是垃圾值
            // （常见为 0）。当成 Bgra32 的话整张图会被 WPF 判成全透明 —— 这是抓屏最经典的坑。
            // 以前是抓完再整片刷一遍 A=255（一块 2K 屏就是三百多万次写），
            // 而 Bgr32 本身就规定第四个字节无意义，那一趟白跑的活整个不必存在。
            var bmp = BitmapSource.Create(rect.Width, rect.Height, dpiX, dpiY,
                PixelFormats.Bgr32, null, bits, stride * rect.Height, stride);
            bmp.Freeze(); // 冻结后可跨线程使用，也免掉后续渲染的锁开销

            return new CapturedFrame
            {
                Image = bmp,
                Bounds = rect,
            };
        }
        finally
        {
            if (hOld != IntPtr.Zero) NativeMethods.SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(hdcMem);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
