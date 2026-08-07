using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Pin;

/// <summary>
/// 贴图窗口：把一张截图钉在桌面最上层，窗口随缩放长大。
///
/// 绘制走 **WS_EX_LAYERED 分层窗口 + UpdateLayeredWindow**，而不是普通窗口的 WM_PAINT。
/// 每次把整幅画面（缩放后的图片 + 边框）渲染进一张位图，连同窗口几何在同一个
/// UpdateLayeredWindow 调用里原子交给 DWM 合成 —— 几何和内容永远同时到位，DWM 拿不到
/// 「旧纹理配新尺寸」的中间帧。
///
/// 这就是之前几版反复闪的根源：WPF 异步渲染和 GDI 双缓冲都一样，改窗口尺寸和重绘是
/// 两个独立的屏幕更新，之间总隔着一帧（DWM 先把旧纹理拉到新几何上，等应用画好才换）。
/// 分层窗口把两者合并成一次原子提交，所以怎么缩放都不闪 —— 贴图类工具（Snipaste 等）
/// 用分层窗口/DirectComposition 正是这个道理。
///
/// 位置与尺寸全程物理像素。UpdateLayeredWindow 用的就是屏幕物理坐标，天然跨屏正确，
/// 不碰 WPF 那套 DIP。锚点缩放用双精度 _left/_top 直接算、只在下发时取整，误差不累积，
/// 贴图放大碰到屏幕边界时位置不会乱跑。
/// </summary>
public sealed class PinForm : Form
{
    private const double MinScale = 0.1;
    private const double MaxScale = 16.0;
    private const double MinOpacity = 0.2;

    /// <summary>滚轮一格的缩放倍率。实际用的是它的 delta/120 次方，见 <see cref="OnMouseWheel"/>。</summary>
    private const double ZoomPerNotch = 1.1;

    private const double OpacityPerNotch = 0.08;

    /// <summary>
    /// 放到这个倍数以上才切最近邻。
    ///
    /// 阈值不能低：最近邻在 2、3 倍这一带最难看 —— 每格缩放都会让「哪些源像素行被复制成两行」
    /// 重新洗一次牌，字的笔画于是一格一个样，看着就是在跳。到了四倍以上，一个源像素已经
    /// 摊开成一大块，抖那一两个像素占比很小，而这时候用户多半正是想数像素，锐利才是他要的。
    /// </summary>
    private const double CrispThreshold = 4.0;

    /// <summary>
    /// 单帧位图的总像素上限。窗口随缩放长大，帧位图也跟着长 —— 大图无限放大的话
    /// 一张帧就几十上百 MB，new Bitmap 直接 OOM。
    ///
    /// 注意这里的钳制必须落在 **_scale** 上，而不是渲染尺寸上：锚点公式
    /// 假定「窗口尺寸 = 图片尺寸 × _scale」严格成立。只掐渲染尺寸、让 _scale 继续涨，
    /// 光标下的内容就会开始滑动（贴图飘、甚至滑出屏幕）—— 那正是之前那版的 bug。
    /// 见 <see cref="MaxScaleForImage"/>。
    /// </summary>
    private const long MaxFramePixels = 48_000_000;

    /// <summary>
    /// 当前源图在不突破帧位图上限时最多能放到几倍。大图触顶早（高分屏整屏截图
    /// 往往一倍多就到顶），小图照样能到 <see cref="MaxScale"/>。
    /// </summary>
    private double MaxScaleForImage
        => Math.Sqrt(MaxFramePixels / (double)((long)_imageW * _imageH));

    private static readonly Color BorderColor = Color.FromArgb(0x3B, 0x9E, 0xFF);

    private readonly BitmapSource _source;
    private readonly Bitmap _bitmap;
    private readonly int _imageW;
    private readonly int _imageH;

    private double _scale = 1.0;

    /// <summary>
    /// 贴图左上角在虚拟屏幕上的位置，保留小数，只在下发时取整。
    /// 位置只从倍率和锚点算，不经过取整过的尺寸 —— 这就是「放大不累积误差、碰边界不乱跑」的全部原因。
    /// </summary>
    private double _left;
    private double _top;

    /// <summary>整体不透明度 1.0~MinOpacity。走 SetLayeredWindowAttributes，不必为调透明度重渲染整幅图。</summary>
    private double _opacity = 1.0;

    private bool _dragging;
    private Point _dragOrigin;
    private double _dragStartLeft;
    private double _dragStartTop;

    /// <summary>
    /// 右键菜单。在 OnMouseUp 里手动 <see cref="ContextMenuStrip.Show(Point)"/> 弹，
    /// 用明确的屏幕坐标，不走 ContextMenuStrip 属性的 WM_CONTEXTMENU 路径 —— 那条路用
    /// WinForms 自己维护的几何（ClientRectangle/Height）算位置，分层窗口的尺寸是 ULW
    /// 直接定的、WinForms 那套常常是陈旧的 300×300，窗口一超出屏幕，算出来的菜单位置
    /// 就跑到屏幕外去了。
    /// </summary>
    private readonly ContextMenuStrip _menu;

    /// <summary>当前倍率下渲染好的整帧位图 + 它的 HBITMAP。拖动复用，缩放重画。</summary>
    private Bitmap? _frame;
    private IntPtr _frameHBitmap;
    private int _frameW;
    private int _frameH;

    /// <summary>用户请求复制这张贴图。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>用户请求另存这张贴图。</summary>
    public event Action<BitmapSource>? SaveRequested;

    public PinForm(BitmapSource image, PixelRect origin)
    {
        _source = image;
        _source.Freeze();

        _bitmap = ToBitmap(image);
        _imageW = _bitmap.Width;
        _imageH = _bitmap.Height;

        _left = origin.X;
        _top = origin.Y;

        // 源图大到连一倍都顶到帧位图上限时，从 1.0 收一档 —— 否则开局就破了
        // 「尺寸 = 图片 × 倍率」的不变量，锚点公式从第一格就飘
        _scale = Math.Min(1.0, MaxScaleForImage);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // 尺寸位置全用物理像素，别让 WinForms 掺和缩放

        // 分层窗口不参与 WM_PAINT，内容由 ULW 整幅送上去；留着 UserPaint 只是保险，
        // 防止任何一次多余的 GDI 擦背景。真正的防闪在 WS_EX_LAYERED（见 CreateParams）。
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _menu = BuildContextMenu();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 第一次显示前把几何和第一帧一起送上去：贴图一出现就在正确的位置、正确的大小。
        // 旧版漏了下发 bounds，贴图会先以默认尺寸出现在窗口角落，等用户动一下才归位。
        RenderFrame();
    }

    /// <summary>
    /// BitmapSource → GDI 位图。先由 WPF 把源归一到 Bgra32 再原样拷字节 —— GDI 的
    /// 32bpp 内存布局就是 BGRA，跟 Bgra32 一一对应。别自己逐像素转：源若标着 Pbgra32
    /// 这类预乘格式，转错一步颜色就没了，格式转换交给 WPF 按格式做才正确。
    /// </summary>
    private static Bitmap ToBitmap(BitmapSource src)
    {
        var bgra = src.Format == System.Windows.Media.PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        var bmp = new Bitmap(bgra.PixelWidth, bgra.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            bgra.CopyPixels(
                new System.Windows.Int32Rect(0, 0, bgra.PixelWidth, bgra.PixelHeight),
                data.Scan0, data.Stride * data.Height, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>
    /// 以 <paramref name="anchor"/>（虚拟屏幕物理像素）为锚点缩放：光标底下那一点内容
    /// 保持不动，否则放大几次之后想看的地方早就跑出屏幕了。
    ///
    /// 算式直接落在双精度的左上角上：_left = anchor.X - (anchor.X - _left) * k。
    /// 窗口探出屏幕是允许的 —— Windows 不会夹住它，锚点持续生效。
    /// </summary>
    private void ZoomAround(double factor, Point anchor)
    {
        double next = Math.Clamp(_scale * factor, MinScale, Math.Min(MaxScale, MaxScaleForImage));
        if (Math.Abs(next - _scale) < 1e-6) return;

        double k = next / _scale;
        _left = anchor.X - (anchor.X - _left) * k;
        _top = anchor.Y - (anchor.Y - _top) * k;
        _scale = next;

        RenderFrame();
    }

    /// <summary>
    /// 按当前倍率挑重采样方式，跟 WPF 版同一套道理：
    /// 缩小用 Bicubic（按面积平均，否则细线会闪成一段一段的）；
    /// 常用的那一段放大用 Bilinear（平滑、快）；
    /// 放到很大才切最近邻，理由见 <see cref="CrispThreshold"/>。
    /// 正好 1.0 时是 1:1 原样复制，用最近邻保证像素级锐利。
    /// </summary>
    private static InterpolationMode ResampleMode(double scale)
        => Math.Abs(scale - 1.0) < 1e-9
            ? InterpolationMode.NearestNeighbor
            : scale >= CrispThreshold
                ? InterpolationMode.NearestNeighbor
                : scale < 1.0
                    ? InterpolationMode.HighQualityBicubic
                    : InterpolationMode.HighQualityBilinear;

    /// <summary>
    /// 当前倍率下的渲染尺寸。倍率已被 <see cref="ZoomAround"/> 钳在
    /// <see cref="MaxScaleForImage"/> 之内，这里直接相乘即可，不用再兜底 ——
    /// 兜底放在倍率上（不然又会破「尺寸 = 图片 × 倍率」的不变量）。
    /// </summary>
    private (int w, int h) FrameSize(double scale)
        => (Math.Max(1, (int)Math.Round(_imageW * scale)),
            Math.Max(1, (int)Math.Round(_imageH * scale)));

    /// <summary>
    /// 几何变了（缩放）：按新倍率重画整帧，然后原子送上去。帧按尺寸缓存 ——
    /// 拖动只改位置，直接复用这一帧，不重画。
    /// </summary>
    private void RenderFrame()
    {
        var (w, h) = FrameSize(_scale);
        if (_frame is null || _frameW != w || _frameH != h)
        {
            DrawFrame(w, h);
            _frameW = w;
            _frameH = h;
        }
        UploadFrame();
    }

    private void DrawFrame(int w, int h)
    {
        if (_frameHBitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_frameHBitmap);
            _frameHBitmap = IntPtr.Zero;
        }
        _frame?.Dispose();

        var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.InterpolationMode = ResampleMode(_scale);
            g.PixelOffsetMode = _scale >= CrispThreshold
                ? PixelOffsetMode.Half
                : PixelOffsetMode.HighQuality;

            g.DrawImage(_bitmap, new Rectangle(0, 0, w, h));

            // 一圈实色细边把贴图跟桌面内容分开。整帧不透明，ULW 的预乘 alpha 对
            // 不透明像素是恒等变换，不会有边缘偏色的坑。
            //
            // 1px 的笔是居中对齐的：按整数坐标画时，上/左沿的笔有一半探到画布外
            // （y/x = 0 之外），那一半被 GDI 裁掉，于是只剩右/下沿有边。
            // 把矩形按半像素内移，笔正好铺满最外圈的那行/那列像素，四条边都在。
            // 图片此时已画完，这里把像素偏移切到不偏移的 None，保证半像素内移
            // 在任何倍率下都按原样生效，不受上面缩放用的偏移模式影响。
            g.PixelOffsetMode = PixelOffsetMode.None;
            using var pen = new Pen(BorderColor);
            g.DrawRectangle(pen, 0.5f, 0.5f, w - 1, h - 1);
        }

        _frame = frame;
        // HBITMAP 缓存下来反复用：拖动时每次 ULW 不必重新 GetHbitmap 拷一遍整帧
        _frameHBitmap = frame.GetHbitmap(Color.Black);
    }

    /// <summary>
    /// 把当前这帧连位置一起原子提交给 DWM。缩放和拖动都走这里 ——
    /// 分层窗口的几何和内容必须同一次调用到位，分开调用又会露出中间帧。
    /// </summary>
    private void UploadFrame()
    {
        if (_frame is null || _frameHBitmap == IntPtr.Zero) return;

        var dst = new POINT { X = (int)Math.Round(_left), Y = (int)Math.Round(_top) };
        var size = new SIZE { cx = _frameW, cy = _frameH };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = (byte)Math.Round(_opacity * 255),
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            oldBitmap = NativeMethods.SelectObject(memDc, _frameHBitmap);
            NativeMethods.UpdateLayeredWindow(Handle, screenDc,
                ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldBitmap);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void SetOpacity(double value)
    {
        value = Math.Clamp(value, MinOpacity, 1.0);
        if (Math.Abs(value - _opacity) < 1e-6) return;

        _opacity = value;
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(_opacity * 255), NativeMethods.LWA_ALPHA);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _dragOrigin = Cursor.Position; // 屏幕物理坐标
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var now = Cursor.Position;
        _left = _dragStartLeft + (now.X - _dragOrigin.X);
        _top = _dragStartTop + (now.Y - _dragOrigin.Y);
        UploadFrame();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            // 屏幕坐标直接弹，不经过 WM_CONTEXTMENU 的几何换算（见 _menu 字段注释）
            _menu.Show(Cursor.Position);
            return;
        }
        _dragging = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Close();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // 按「几格」算而不是只看正负。一条消息里可能带着好几格（滚快了系统会合并），
        // 精密滚轮和触控板给的更是不足一格的小数 —— 一律当成一整格的话，
        // 前者少走好几步、后者步步都迈满格，两头都是一跳一跳的。
        double notches = e.Delta / (double)SystemInformation.MouseWheelScrollDelta;
        if (Math.Abs(notches) < 1e-6) return;

        if ((ModifierKeys & Keys.Control) != 0)
        {
            SetOpacity(_opacity + OpacityPerNotch * notches);
            return;
        }

        // 锚点取屏幕物理坐标的光标位置 —— WinForms 里 e.Location 是客户区坐标，
        // 而 _left/_top 是虚拟屏幕坐标，用 Cursor.Position 才同一个坐标系。
        ZoomAround(Math.Pow(ZoomPerNotch, notches), Cursor.Position);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                e.Handled = true;
                break;
            case Keys.C when e.Control:
                CopyRequested?.Invoke(_source);
                e.Handled = true;
                break;
            case Keys.S when e.Control:
                SaveRequested?.Invoke(_source);
                e.Handled = true;
                break;
            case Keys.D0 when e.Control:
                ResetScale();
                e.Handled = true;
                break;
            case Keys.Apps:
            case Keys.F10 when e.Shift:
                _menu.Show(MenuCenterPoint);
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private void ResetScale()
    {
        // 以窗口中心为锚回到 1.0，内容尽量原地不动
        var center = new Point(
            (int)Math.Round(_left) + _frameW / 2,
            (int)Math.Round(_top) + _frameH / 2);
        ZoomAround(1.0 / _scale, center);
    }

    /// <summary>
    /// 贴图是 TopMost 置顶窗口，而 ContextMenuStrip 默认不置顶。右键菜单弹在光标处，
    /// 光标落在贴图上 —— 贴图盖满屏幕时菜单会整个被压在贴图底下，看起来就像「没弹出来」。
    /// 加上 WS_EX_TOPMOST 让菜单和贴图同处置顶带、又晚于贴图显示，于是盖在贴图上面。
    /// 图片小的时候菜单能露在贴图外，所以这个问题只在贴图铺满屏幕时才暴露出来。
    /// </summary>
    private sealed class TopmostContextMenuStrip : ContextMenuStrip
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new TopmostContextMenuStrip();

        menu.Items.Add("复制 (Ctrl+C)", null, (_, _) => CopyRequested?.Invoke(_source));
        menu.Items.Add("另存为 (Ctrl+S)", null, (_, _) => SaveRequested?.Invoke(_source));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("原始大小 (Ctrl+0)", null, (_, _) => ResetScale());

        var topmostItem = new ToolStripMenuItem("总在最前") { CheckOnClick = true, Checked = true };
        topmostItem.Click += (_, _) => TopMost = topmostItem.Checked;
        menu.Items.Add(topmostItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭 (Esc)", null, (_, _) => Close());

        return menu;
    }

    /// <summary>菜单中心点（键盘呼出菜单时用，屏幕物理坐标）。</summary>
    private Point MenuCenterPoint
        => new((int)Math.Round(_left) + _frameW / 2, (int)Math.Round(_top) + _frameH / 2);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap.Dispose();
            if (_frameHBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_frameHBitmap);
                _frameHBitmap = IntPtr.Zero;
            }
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
