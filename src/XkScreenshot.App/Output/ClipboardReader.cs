using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XkScreenshot.App.Output;

/// <summary>
/// 把剪贴板里的东西读成一张可以钉出来的图。
///
/// 优先级是「越接近原样越优先」：图片 &gt; 单个图片文件 &gt; 文本（含文件路径列表）。
/// 复制一张图片文件时贴的是图本身而不是它的路径，复制一段话时贴的是排好版的文字。
/// </summary>
public static class ClipboardReader
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico"];

    /// <summary>
    /// 读一张可贴的图；剪贴板为空或内容不认识时返回 null。
    /// scale 只用于文本渲染，见 <see cref="TextImage.Render"/>。
    ///
    /// busy 为 true 表示这次压根没读成 —— 剪贴板被别的进程占着。
    /// 它和「剪贴板里确实没东西」必须分开报：两者都返回 null，
    /// 但前者再按一次就好，后者按多少次都一样。
    /// </summary>
    public static BitmapSource? ReadPinnable(double scale, out bool busy)
    {
        busy = false;

        try
        {
            if (ReadImage() is { } image) return image;
            if (ReadFiles(scale) is { } fromFiles) return fromFiles;
            if (Clipboard.ContainsText()) return TextImage.Render(Clipboard.GetText(), scale);
            return null;
        }
        catch (COMException)
        {
            // 剪贴板是全局独占资源，输入法、云剪贴板、密码管理器都可能正好占着它。
            // 这里不再自己套一层重试：WPF 的 Clipboard 内部已经重试了约一秒才把异常抛上来，
            // 外面再循环八次就是把这一秒乘八倍（实测最坏 9.5 秒），而这段时间 UI 线程是冻住的 ——
            // 贴图热键正跑在它上面。抢不到就如实说一声，比让整个程序假死好。
            busy = true;
            return null;
        }
        catch (Exception)
        {
            // 剪贴板里那份数据本身是坏的，不该让程序崩掉
            return null;
        }
    }

    private static BitmapSource? ReadImage()
    {
        // 先试 PNG：它保真且带 alpha，浏览器和多数设计工具都会放一份
        if (Clipboard.GetData("PNG") is Stream png)
        {
            var decoded = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            decoded.Freeze();
            return decoded;
        }

        if (!Clipboard.ContainsImage()) return null;

        var image = Clipboard.GetImage();
        if (image is null) return null;

        image = RepairAlpha(image);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 修掉「整张图 alpha 全是 0」的位图。
    ///
    /// CF_DIB 的头里没有「这 32 位到底带不带 alpha」这一位。很多程序写 32bpp 时压根不管
    /// 最高那个字节，留下一整片 0；读的一方老老实实把它当 alpha，就得到一张全透明的图 ——
    /// 钉出来是一整块窗口底色，用户看到的是「贴图全黑」。
    ///
    /// 全透明的图没有任何意义，所以整张 alpha 都是 0 时只能判定它其实是不透明的。
    /// 注意这里不做格式转换，按源格式原样取像素：源若是 Pbgra32，
    /// 转一次颜色就会被 alpha=0 乘没，原始 RGB 再也捞不回来。
    /// </summary>
    private static BitmapSource RepairAlpha(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
            return source;

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        long size = (long)width * height * 4;
        if (size <= 0 || size > int.MaxValue) return source;

        var pixels = new byte[size];
        int stride = width * 4;
        source.CopyPixels(pixels, stride, 0);

        // 内存里是 B G R A，第 4 个字节才是 alpha
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0)
                return source; // 有一处不透明，说明这张图的 alpha 是当真的

        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 0xFF;

        // 按直通 alpha 建：原来那份就算标着 Pbgra32，字节本身也是没乘过的原始 RGB
        var repaired = BitmapSource.Create(
            width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        repaired.Freeze();
        return repaired;
    }

    private static BitmapSource? ReadFiles(double scale)
    {
        if (!Clipboard.ContainsFileDropList()) return null;

        var files = Clipboard.GetFileDropList()
            .Cast<string?>()
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
        if (files.Count == 0) return null;

        if (files.Count == 1 && DecodeFile(files[0]) is { } single) return single;

        return TextImage.Render(string.Join(Environment.NewLine, files), scale);
    }

    /// <summary>解不出来就返回 null，交给上层当普通文本贴路径。</summary>
    private static BitmapSource? DecodeFile(string path)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;

        try
        {
            // OnLoad 会当场读完，出了 using 文件就不再被占用 —— 别让贴图把用户的文件锁住
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
