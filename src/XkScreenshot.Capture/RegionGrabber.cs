using System.Runtime.InteropServices;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Capture;

/// <summary>
/// 反复抓同一块屏幕区域。
///
/// 不复用 <c>GdiScreenCapture</c>：那一个每次都要建 DC、建 DIBSection、出一张 BitmapSource，
/// 而长截图和 GIF 录制一秒要抓十几帧，每帧还是几 MB 的 byte[] —— 那种尺寸的数组直接进
/// 大对象堆，抓上几十帧就是几百 MB 的垃圾。这里把 DC 和 DIB 建一次用到底，每帧只做一次
/// BitBlt 加一次拷贝，进程内不留任何新分配。
///
/// 目标缓冲由调用方提供并反复使用，理由同上。
///
/// 住在 Capture 而不是 Scroll：GIF 录制要的是同一件事，只是多一个「把光标画进去」的选项。
/// 抓屏的冷知识（CAPTUREBLT、top-down、alpha 刷 255）只该有一份。
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
    /// <param name="drawCursor">
    /// 是否把鼠标指针画进这一帧。BitBlt 天生不带光标，而录教程的人恰恰需要看见点在哪儿 ——
    /// 所以这件事只能在拷贝内存之前、直接往 DIB 上补一笔。
    /// </param>
    public bool Grab(byte[] destination, bool drawCursor = false)
    {
        if (destination.Length < ByteCount)
            throw new ArgumentException("目标缓冲装不下一帧", nameof(destination));

        if (!NativeMethods.BitBlt(_hdcMem, 0, 0, Region.Width, Region.Height,
                _hdcScreen, Region.X, Region.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            return false;

        if (drawCursor) DrawCursor();

        Marshal.Copy(_bits, destination, 0, ByteCount);
        for (int i = 3; i < ByteCount; i += 4)
            destination[i] = 0xFF;

        return true;
    }

    /// <summary>
    /// 把当前鼠标指针画到内存 DC 上。
    ///
    /// 光标是系统在合成阶段叠上去的，抓屏 API 谁都拿不到它，只能自己去问
    /// <see cref="NativeMethods.GetCursorInfo"/> 再 <c>DrawIconEx</c> 补一遍。
    /// 热点要减掉，否则画出来的指针会整体偏出实际位置一截；<c>GetIconInfo</c> 借出来的
    /// 两张位图必须还回去，它不归调用方所有但确实占着 GDI 句柄。
    ///
    /// 光标有一半在区域外时照样画 —— DrawIconEx 会按 DIB 边界裁掉，屏幕上看到的就是
    /// 「指针压在选区边上」那半截，比整只不画更接近事实。
    /// </summary>
    private void DrawCursor()
    {
        var info = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info)) return;
        if ((info.flags & NativeMethods.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;

        if (!NativeMethods.GetIconInfo(info.hCursor, out var icon)) return;
        try
        {
            int x = info.ptScreenPos.X - Region.X - (int)icon.xHotspot;
            int y = info.ptScreenPos.Y - Region.Y - (int)icon.yHotspot;

            NativeMethods.DrawIconEx(_hdcMem, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero,
                NativeMethods.DI_NORMAL);
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmMask);
        }
    }

    public void Dispose()
    {
        if (_hOld != IntPtr.Zero) NativeMethods.SelectObject(_hdcMem, _hOld);
        if (_hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(_hBitmap);
        if (_hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(_hdcMem);
        if (_hdcScreen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _hdcScreen);
    }
}
