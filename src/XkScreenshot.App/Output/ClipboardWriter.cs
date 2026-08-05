using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XkScreenshot.App.Output;

/// <summary>
/// 把截图写进剪贴板。
///
/// 只写一种格式必然会在某个常用程序里出问题，所以三种一起写，让接收方各取所需：
///   · "PNG"      —— Chrome / Edge / Figma / Slack / 飞书，保真且带 alpha
///   · CF_DIBV5   —— 支持 alpha 的传统程序
///   · CF_DIB     —— 微信 / QQ / 旧版 Office 只认这个，且不认 alpha，
///                   所以这一份必须预先把透明区域合成到白底上，
///                   否则粘出来是一片黑（透明被当成黑色 0x000000）。
/// </summary>
public static class ClipboardWriter
{
    private const int RetryCount = 8;
    private const int RetryDelayMs = 60;

    public static void SetImage(BitmapSource image)
    {
        var data = new DataObject();

        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        data.SetData("PNG", png, autoConvert: false);

        data.SetData(DataFormats.Dib, BuildDibV5(image), autoConvert: false);

        // WPF 的 SetImage 走 CF_BITMAP/CF_DIB，是给旧程序兜底的那一份
        data.SetImage(FlattenOntoWhite(image));

        // 剪贴板是全局独占资源，输入法、云剪贴板、密码管理器都可能正好占着它。
        // 这里必然偶发 CLIPBRD_E_CANT_OPEN，重试几次是标准做法，不是偷懒。
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < RetryCount)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    /// <summary>把带 alpha 的图合成到白底，供不认识 alpha 的接收方使用。</summary>
    private static BitmapSource FlattenOntoWhite(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(image, rect);
        }

        var target = new RenderTargetBitmap(
            image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// 构造 CF_DIBV5 数据块：BITMAPV5HEADER + 像素。
    /// 注意剪贴板里的 DIB 不含 BITMAPFILEHEADER，多写了接收方会解析失败。
    /// </summary>
    private static MemoryStream BuildDibV5(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        const int headerSize = 124; // sizeof(BITMAPV5HEADER)
        var ms = new MemoryStream(headerSize + pixels.Length);
        var w = new BinaryWriter(ms);

        w.Write(headerSize);          // bV5Size
        w.Write(width);               // bV5Width
        w.Write(height);              // bV5Height（正数 = bottom-up）
        w.Write((short)1);            // bV5Planes
        w.Write((short)32);           // bV5BitCount
        w.Write(3);                   // bV5Compression = BI_BITFIELDS
        w.Write(pixels.Length);       // bV5SizeImage
        w.Write(2835);                // bV5XPelsPerMeter（72 DPI，接收方一般忽略）
        w.Write(2835);                // bV5YPelsPerMeter
        w.Write(0);                   // bV5ClrUsed
        w.Write(0);                   // bV5ClrImportant
        w.Write(0x00FF0000);          // bV5RedMask
        w.Write(0x0000FF00);          // bV5GreenMask
        w.Write(0x000000FF);          // bV5BlueMask
        w.Write(unchecked((int)0xFF000000)); // bV5AlphaMask
        w.Write(0x73524742);          // bV5CSType = 'sRGB'
        for (int i = 0; i < 9; i++) w.Write(0); // bV5Endpoints（CSType 为 sRGB 时忽略）
        w.Write(0); w.Write(0); w.Write(0);     // bV5Gamma{Red,Green,Blue}
        w.Write(4);                   // bV5Intent = LCS_GM_IMAGES
        w.Write(0); w.Write(0); w.Write(0);     // bV5ProfileData / Size / Reserved

        // DIB 是 bottom-up 的，必须逐行倒着写，否则粘出来上下颠倒
        for (int y = height - 1; y >= 0; y--)
            ms.Write(pixels, y * stride, stride);

        ms.Position = 0;
        return ms;
    }
}
