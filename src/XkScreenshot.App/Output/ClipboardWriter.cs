using System;
using System.ComponentModel;
using System.Diagnostics;
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
    /// <summary>
    /// 重试总共熬这么久，超了就认输。
    ///
    /// 按时间算而不是按次数算，是因为一次尝试要多久根本不由我们定：
    /// WPF 的 Clipboard.SetDataObject 内部自己就会重试十次、每次隔 100 毫秒，
    /// 也就是说光是「失败一次」就要花掉一秒。原来那版按次数写（重试 8 次），
    /// 遇上剪贴板被长期占着的机器，界面能整整卡九秒才弹出「复制失败」。
    ///
    /// 这个数是这么定的：真正的偶发占用只有几十毫秒，WPF 自带那一秒早就够了；
    /// 熬到两秒还进不去的，多半是有个程序（远程控制的剪贴板同步、剪贴板管理器、
    /// 输入法）在持续霸着它，再熬下去也是白熬，不如早点把话说给用户听。
    /// </summary>
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(2);

    private const int RetryDelayMs = 60;

    public static void SetImage(BitmapSource image)
    {
        var data = new DataObject();

        // 预留容量：截图 PNG 大约压到每像素半字节上下，一次给够就不必让 MemoryStream
        // 一路翻倍扩容 —— 那些中途丢掉的缓冲每一片都在大对象堆上
        using var png = new MemoryStream(image.PixelWidth * image.PixelHeight / 2 + 1024);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        data.SetData("PNG", png, autoConvert: false);

        data.SetData(DataFormats.Dib, BuildDibV5(image), autoConvert: false);

        // WPF 的 SetImage 走 CF_BITMAP/CF_DIB，是给旧程序兜底的那一份。
        // 图本身就不带 alpha 时不必合成 —— 那一步会再复制一整张图出来，
        // 而没有透明区域可合成的图，合出来的和原图一模一样
        data.SetImage(IsOpaque(image.Format) ? image : FlattenOntoWhite(image));

        Put(data);
    }

    /// <summary>
    /// 把一段文字写进剪贴板。
    ///
    /// 走 Win32 原生的 CF_UNICODETEXT，不走 <see cref="Clipboard.SetText"/> —— 后者内部是
    /// OLE 剪贴板（OleSetClipboard + OleFlushClipboard），而 OLE 那一层会被剪贴板监听方
    /// 按住：远程控制软件的剪贴板同步、剪贴板历史服务这类程序，每次剪贴板一变就拿
    /// OleGetClipboard 去看内容，看完不放手，于是接下来好几秒里谁走 OLE 都写不进去，
    /// 报 CLIPBRD_E_CANT_OPEN。实测过：同一台机器同一秒内交替写十二次，OLE 六次全失败
    /// （每次还要先卡满 WPF 内部那一秒重试），原生六次全成功、每次 0~2 毫秒。
    ///
    /// 纯文字本来也用不着 OLE 那套：CF_UNICODETEXT 摆上去就完事，CF_TEXT / CF_OEMTEXT
    /// 由系统自己合成，接收方一样认；HGLOBAL 的所有权交给系统，进程退出后内容照样在。
    /// </summary>
    public static void SetText(string text)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                RawSetText(text);
                return;
            }
            // 拒绝访问就是「真的有人正开着剪贴板」，这个才值得等
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorAccessDenied
                                          && clock.Elapsed < RetryBudget)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    private static void RawSetText(string text)
    {
        // 传 IntPtr.Zero：剪贴板不归任何窗口，纯文字也没有延迟渲染的余地，
        // 用不着一个窗口留在那儿接 WM_RENDERFORMAT
        if (!OpenClipboard(IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr block = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());

            // CF_UNICODETEXT 要的是 UTF-16 加一个结尾的 0
            var chars = new char[text.Length + 1];
            text.CopyTo(0, chars, 0, text.Length);

            block = GlobalAlloc(GmemMoveable, (UIntPtr)(chars.Length * sizeof(char)));
            if (block == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr address = GlobalLock(block);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(chars, 0, address, chars.Length); }
            finally { GlobalUnlock(block); }

            if (SetClipboardData(CfUnicodeText, block) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // 成了：这块内存从此归系统，我们不能再碰，更不能释放
            block = IntPtr.Zero;
        }
        finally
        {
            if (block != IntPtr.Zero) GlobalFree(block);
            CloseClipboard();
        }
    }

    /// <summary>
    /// 真正下手那一下。
    ///
    /// 剪贴板是全局独占资源，输入法、云剪贴板、密码管理器、远程控制软件的剪贴板同步
    /// 都可能正好占着它 —— 谁都只占几十毫秒，但那几十毫秒里谁也写不进去。
    /// 这里必然偶发 CLIPBRD_E_CANT_OPEN，重试是标准做法，不是偷懒。
    ///
    /// 熬完 <see cref="RetryBudget"/> 还进不去就把异常抛出去，让调用方去告诉用户。
    /// 咽下去最坏：用户手里还是上一次复制的东西，粘错了也查不出原因。
    /// </summary>
    private static void Put(DataObject data)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (clock.Elapsed < RetryBudget)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    /// <summary>
    /// 把写失败的原因翻成人话，给界面用。
    ///
    /// 原样把错误码摆出来（「OpenClipboard 失败 (0x800401D0)」）对用户没有用：
    /// 它既不说是谁的问题，也不说该怎么办 —— 而这个错几乎总是别的程序造成的，
    /// 说清楚了用户才知道该去关掉那个程序，而不是以为这个软件坏了。
    /// </summary>
    public static string Describe(Exception error)
        => error is COMException { HResult: ClipboardCantOpen }
            or Win32Exception { NativeErrorCode: ErrorAccessDenied }
            ? "剪贴板正被其他程序占用（远程控制、剪贴板管理器、输入法都会抢它），过一会儿再试"
            : error.Message;

    // ---------------- Win32 ----------------

    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>OpenClipboard 撞上「已经有别的窗口开着它」时给的就是这个。</summary>
    private const int ErrorAccessDenied = 5;

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr block);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr block);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr block);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr block);

    /// <summary>这个格式压根没有 alpha 通道 —— 抓屏直出的冻结画面就是这一类。</summary>
    private static bool IsOpaque(PixelFormat format)
        => format == PixelFormats.Bgr32 || format == PixelFormats.Bgr24
            || format == PixelFormats.Rgb24 || format == PixelFormats.Bgr565
            || format == PixelFormats.Bgr555;

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
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;

        const int headerSize = 124; // sizeof(BITMAPV5HEADER)
        // 一块缓冲干到底：头写在前面，像素逐行直接解到它该在的位置上。
        // 先取一份完整像素再往流里搬一遍的话，同一张图会同时有两份在内存里。
        var buffer = new byte[headerSize + stride * height];
        var ms = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: true);
        var w = new BinaryWriter(ms);

        w.Write(headerSize);          // bV5Size
        w.Write(width);               // bV5Width
        w.Write(height);              // bV5Height（正数 = bottom-up）
        w.Write((short)1);            // bV5Planes
        w.Write((short)32);           // bV5BitCount
        w.Write(3);                   // bV5Compression = BI_BITFIELDS
        w.Write(stride * height);     // bV5SizeImage
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

        // DIB 是 bottom-up 的，必须逐行倒着解，否则粘出来上下颠倒
        for (int y = 0; y < height; y++)
        {
            bgra.CopyPixels(
                new Int32Rect(0, y, width, 1), buffer, stride,
                headerSize + (height - 1 - y) * stride);
        }

        ms.Position = 0;
        return ms;
    }
}
