using System.Runtime.InteropServices;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Scroll;

/// <summary>
/// 反复抓同一块屏幕区域。
///
/// 不复用 <c>GdiScreenCapture</c>：那一个每次都要建 DC、建 DIBSection、出一张 BitmapSource，
/// 而长截图一秒要抓十几帧，每帧还是几 MB 的 byte[] —— 那种尺寸的数组直接进大对象堆，
/// 抓上几十帧就是几百 MB 的垃圾。这里把 DC 和 DIB 建一次用到底，每帧只做一次 BitBlt
/// 加一次拷贝，进程内不留任何新分配。
///
/// 目标缓冲由调用方提供并反复使用，理由同上。
/// </summary>
public sealed class RegionGrabber : IDisposable
{
    private readonly IntPtr _hdcScreen;
    private readonly IntPtr _hdcMem;
    private readonly IntPtr _hBitmap;
    private readonly IntPtr _hOld;
    private readonly IntPtr _bits;

    public RegionGrabber(PixelRect region)
    {
        if (region.IsEmpty) throw new ArgumentException("抓取区域为空", nameof(region));

        Region = region;
        Stride = region.Width * 4;
        ByteCount = Stride * region.Height;

        _hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (_hdcScreen == IntPtr.Zero)
            throw new InvalidOperationException("GetDC(NULL) 失败");

        try
        {
            _hdcMem = NativeMethods.CreateCompatibleDC(_hdcScreen);
            if (_hdcMem == IntPtr.Zero)
                throw new InvalidOperationException("CreateCompatibleDC 失败");

            // biHeight 取负 => top-down DIB，扫描线顺序和 WPF 一致，省一次翻转
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            _hBitmap = NativeMethods.CreateDIBSection(_hdcScreen, ref bmi,
                NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");

            _hOld = NativeMethods.SelectObject(_hdcMem, _hBitmap);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public PixelRect Region { get; }
    public int Width => Region.Width;
    public int Height => Region.Height;
    public int Stride { get; }

    /// <summary>一帧的字节数。调用方按它开缓冲。</summary>
    public int ByteCount { get; }

    /// <summary>
    /// 抓一帧到 <paramref name="destination"/>（非预乘 BGRA，top-down）。失败返回 false。
    ///
    /// GDI 不维护 alpha，BitBlt 出来的 A 是垃圾值，这里统一刷成 255：
    /// 既让最终出图不至于整张透明，也让「两帧是否相同」的比较不受那一个字节干扰。
    /// </summary>
    public bool Grab(byte[] destination)
    {
        if (destination.Length < ByteCount)
            throw new ArgumentException("目标缓冲装不下一帧", nameof(destination));

        if (!NativeMethods.BitBlt(_hdcMem, 0, 0, Region.Width, Region.Height,
                _hdcScreen, Region.X, Region.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            return false;

        Marshal.Copy(_bits, destination, 0, ByteCount);
        for (int i = 3; i < ByteCount; i += 4)
            destination[i] = 0xFF;

        return true;
    }

    public void Dispose()
    {
        if (_hOld != IntPtr.Zero) NativeMethods.SelectObject(_hdcMem, _hOld);
        if (_hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(_hBitmap);
        if (_hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(_hdcMem);
        if (_hdcScreen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _hdcScreen);
    }
}
