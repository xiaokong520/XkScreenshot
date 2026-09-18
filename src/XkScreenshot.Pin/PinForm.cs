using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
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
/// 旋转：帧位图 = 图片按倍率放大后、再绕自己的中心转过 _angle 之后的外接矩形，转出去的
/// 四个角留白（alpha 0，鼠标点上去会穿到下层）。所以角度一动手，窗口几何和画面得一起变，
/// 必须整帧重画 —— 帧缓存因此要连角度一起记（见 RenderFrame）。转的是外接矩形，窗口本身
/// 始终轴对齐，DWM 那一套合成不用改。
///
/// 旋转的入口是鼠标直接抓角：光标挪到图片四角附近就换成旋转光标，按住拖拽即绕中心转
/// （见 IsNearContentCorner、ApplyRotateDrag）。抓的是**图片自己**的角，不是帧的角 ——
/// 转过角度之后帧的四个角全落在透明区里，鼠标压根碰不到，拿它们做命中会永远不触发。
/// 角区只截左键「按下」这一个动作，别处按下仍然是拖着挪位置。
///
/// 位置与尺寸全程物理像素。UpdateLayeredWindow 用的就是屏幕物理坐标，天然跨屏正确，
/// 不碰 WPF 那套 DIP。锚点缩放用双精度 _left/_top 直接算、只在下发时取整，误差不累积，
/// 贴图放大碰到屏幕边界时位置不会乱跑。
///
/// 贴图可以存档：位置、倍率、角度、透明度和画面一起落在程序目录下的 pins\ 里，
/// 下次打开程序按原样摆回来（见 PinStore、PinManager.Restore）。存不存由设置里
/// 「重启后恢复贴图」决定 —— 一件事要跨会话活下去，就得有个地方把它写下来。
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
    /// Alt+滚轮一格的旋转角度。1° 是「把歪掉的截图摆正」够用的步子 ——
    /// 再粗就压不准任意角度了。
    /// </summary>
    private const double RotatePerNotch = 1.0;

    /// <summary>Alt+Shift+滚轮一格的旋转角度。要跨大角度时用，一格 15°。</summary>
    private const double RotatePerNotchCoarse = 15.0;

    /// <summary>
    /// 角上抓旋转的判定半径（物理像素）：光标离图片某个角这么近，就算抓住了那个角。
    ///
    /// 没做成「离角点距离为零」的精确命中：角只有 1px，鼠标对不准，用户会以为功能坏了。
    /// 18 这个数是在「一进角区就有反应」和「别把整条边都算成角」之间取的 —— 贴图通常
    /// 只有几百像素宽，再大会让边上很长一段都变成旋转区，想拖位置却拖不动。
    /// </summary>
    private const double RotateGrabRadius = 18.0;

    /// <summary>
    /// 自由旋转时吸附到 90° 整数倍的容差（度）。鼠标拖到「大概正了」的位置就自己卡进去。
    ///
    /// 必须有这个吸附：拖拽给不出精确角度，而「把歪掉的截图摆正」正是这个功能最主要的
    /// 用法 —— 没有吸附的话转完永远差那么零点几度，肉眼看得出来。
    /// </summary>
    private const double RotateSnapEpsilon = 3.0;

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
        => Math.Sqrt(MaxFramePixels / (double)((long)_imageW * _imageH) / RotationAreaFactor);

    /// <summary>
    /// 转过角度后，帧的外接矩形面积相对原图的倍数：0°/90°/180°/270° 是 1，
    /// 45° 附近最大 —— 正方形正好 2 倍。
    ///
    /// 它挂在 <see cref="MaxScaleForImage"/> 的分母上，这样「转一下」不会让一张已经
    /// 贴着上限的大贴图凭空多占一倍显存（帧位图 + HBITMAP，那可是几百 MB 的量级）。
    /// 代价是转过角度后允许的最大倍率会收紧一点，只有本来就压着上限的大图碰得到。
    /// </summary>
    private double RotationAreaFactor
    {
        get
        {
            if (IsQuarterTurn) return 1.0;

            var (c, s) = RotationTerms();
            double cAbs = Math.Abs(c), sAbs = Math.Abs(s);
            double w = _imageW, h = _imageH;
            return ((w * cAbs + h * sAbs) * (w * sAbs + h * cAbs)) / (w * h);
        }
    }

    private static readonly Color BorderColor = Color.FromArgb(0x3B, 0x9E, 0xFF);

    private readonly BitmapSource _source;
    private readonly Bitmap _bitmap;
    private readonly int _imageW;
    private readonly int _imageH;

    private double _scale = 1.0;

    /// <summary>
    /// 顺时针旋转角度，取值 [0, 360)，每次改动都量化到 1e-3 度 —— 量化之后
    /// 「角度没变就不重画」和「是不是 90° 的整数倍」这两处才能直接比。
    ///
    /// 只存角度不存矩阵：矩阵每次重画时由 <see cref="RotationTerms"/> 现算，
    /// 免得矩阵和角度两处状态各说各话。
    /// </summary>
    private double _angle;

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
    /// 正在角上拖拽旋转（而不是拖着挪位置）。两者互斥，按下那一刻由命中位置决定走哪条路。
    /// </summary>
    private bool _rotating;

    /// <summary>
    /// 按下那一刻的角度。拖拽期间的目标角度一律由它加上累计增量算出，不做逐帧累加 ——
    /// 那样每一帧的取整和吸附误差都会留在角度里，拖久了会漂。
    /// </summary>
    private double _rotateStartAngle;

    /// <summary>上一次算出的「中心 → 光标」方向（度）。增量靠它和新方向的差得到，见 <see cref="ApplyRotateDrag"/>。</summary>
    private double _rotateLastAngle;

    /// <summary>按下至今光标扫过的角度。可正可负，也能超过一圈 —— 拖着转两圈是允许的。</summary>
    private double _rotateAccum;

    /// <summary>光标当前是否落在某个角上。只在 <see cref="UpdateHoverCorner"/> 里改。</summary>
    private bool _hoverCorner;

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

    /// <summary>画 _frame 时用的角度。角度变了哪怕尺寸没变也得重画（见 <see cref="RenderFrame"/>）。</summary>
    private double _frameAngle;

    /// <summary>用户请求复制这张贴图。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>用户请求另存这张贴图。</summary>
    public event Action<BitmapSource>? SaveRequested;

    /// <summary>
    /// 这张贴图动过了：挪了位置、缩放了、转了角度、调了透明度、切了置顶。
    ///
    /// 落盘那一边听着它（见 PinManager）—— 攒到退出时再写是不行的，
    /// 理由见 PinManager.Save 的注释。
    /// </summary>
    internal event Action? Changed;

    /// <summary>一次「动完了」。拖拽和旋转的每一帧都走它的话，索引会被写成每秒几十遍。</summary>
    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// 存档里的画面文件 id，由 PinManager 认领后写回。贴图自己不用它 ——
    /// 摆在这儿只是让「哪张图对应哪个文件」跟着窗口走，不用在外面另记一份映射。
    /// </summary>
    internal string? StoreId { get; set; }

    /// <summary>这张贴图的画面。存档那边要拿它去后台编码，所以得有个出口。</summary>
    internal BitmapSource Source => _source;

    /// <summary>
    /// 存档要的那一份：位置、倍率、角度、透明度、置顶，外加画面文件 id。
    /// 倍率存的是权威值 <see cref="_scale"/>，不是渲染尺寸 —— 反过来的话恢复出来会跟着取整漂。
    /// </summary>
    internal PinSnapshot Snapshot(string image)
        => new(_left, _top, _scale, _angle, _opacity, TopMost, image);

    public PinForm(BitmapSource image, PixelRect origin)
        : this(image, origin.X, origin.Y, 1.0, 0.0, 1.0, topMost: true)
    {
    }

    /// <summary>
    /// 从存档恢复时用的那一份：位置、倍率、角度、透明度、置顶一并接过来（见 PinManager.Restore）。
    ///
    /// 倍率照旧要过一遍上限钳制 —— 存档可能是大屏上存的，今天这台机器的帧位图预算未必一样。
    /// 顺序上必须先定角度再算倍率：上限里那个 <see cref="RotationAreaFactor"/> 是跟着角度走的。
    /// </summary>
    internal PinForm(BitmapSource image, double left, double top,
        double scale, double angle, double opacity, bool topMost)
    {
        _source = image;
        _source.Freeze();

        _bitmap = ToBitmap(image);
        _imageW = _bitmap.Width;
        _imageH = _bitmap.Height;

        _left = left;
        _top = top;

        _angle = NormalizeAngle(Math.Round(angle, 3));

        // 源图大到连一倍都顶到帧位图上限时，从 1.0 收一档 —— 否则开局就破了
        // 「尺寸 = 图片 × 倍率」的不变量，锚点公式从第一格就飘。
        // 钳制一律落在倍率上（而不是渲染尺寸上），理由见 MaxFramePixels。
        _scale = Math.Clamp(scale, MinScale, Math.Min(MaxScale, MaxScaleForImage));

        // 透明度不用在这儿下发：它随每帧的 SourceConstantAlpha 一起走（见 UploadFrame），
        // OnLoad 里第一次 RenderFrame 就把它带上了
        _opacity = Math.Clamp(opacity, MinOpacity, 1.0);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = topMost;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // 尺寸位置全用物理像素，别让 WinForms 掺和缩放

        // 分层窗口不参与 WM_PAINT，内容由 ULW 整幅送上去；留着 UserPaint 只是保险，
        // 防止任何一次多余的 GDI 擦背景。真正的防闪在 WS_EX_LAYERED（见 CreateParams）。
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _menu = BuildContextMenu();
    }

    /// <summary>
    /// 把窗口挪回当前桌面里。只给「从存档恢复」用 —— 存档里的坐标来自上一次的显示器布局，
    /// 那块屏今天可能已经拔了、分辨率可能已经改了，那时候贴图还在，只是谁也看不见它。
    ///
    /// 只挪不缩：比屏幕还大的贴图本来就该那么大（同 App.PlaceUnderCursor 的注释）。
    /// 挪到哪台显示器上按相交面积挑，一块都不相交（那台屏没了）就退回主屏。
    ///
    /// 得在窗口显示**之前**调：显示之后再挪，用户会先看到它在错的地方闪一下。
    /// 尺寸现算而不是读 _frameW/_frameH —— 此刻 OnLoad 还没跑，帧还没画出来。
    /// </summary>
    internal void PullIntoDesktop(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return;

        var (w, h) = FrameSize(_scale);
        var rect = new PixelRect((int)Math.Round(_left), (int)Math.Round(_top), w, h);

        MonitorInfo? target = null;
        long best = 0;
        foreach (var monitor in monitors)
        {
            long overlap = rect.Intersect(monitor.Bounds).Area;
            if (overlap > best) { best = overlap; target = monitor; }
        }
        target ??= monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        var bounds = target.Bounds;
        _left = Math.Clamp(_left, bounds.X, Math.Max(bounds.X, bounds.Right - w));
        _top = Math.Clamp(_top, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - h));
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
        // 滚轮一格算一次，不是每帧一次，直接落盘
        NotifyChanged();
    }

    /// <summary>
    /// 当前帧的中心（虚拟屏幕物理像素）。帧就是图片旋转后的外接矩形，矩形中心和
    /// 图片内容的中心是同一个点，所以缩放、旋转、摆菜单都拿它当锚。
    /// </summary>
    private (double x, double y) ContentCenter
        => (_left + _frameW / 2.0, _top + _frameH / 2.0);

    /// <summary>
    /// 图片四角在本窗口客户区里的位置。
    ///
    /// 变换跟 <see cref="DrawFrame"/> 里那个矩阵同源 —— 图片自己的中心 ↦ 帧画布中心，
    /// 即 p' = R·(p - 图片中心) + 画布中心。两处的 cos/sin 都取自
    /// <see cref="RotationTerms"/>，所以 90° 的整数倍在这里也是精确的转置，
    /// 算出来的角就跟画出来的角严丝合缝。改这里的公式时那边得一起改。
    /// </summary>
    private PointF[] ContentCorners()
    {
        var (iw, ih) = ScaledSize(_scale);
        var (cos, sin) = RotationTerms();

        double cx = _frameW / 2.0, cy = _frameH / 2.0;
        double hx = iw / 2.0, hy = ih / 2.0;

        PointF Map(double x, double y) => new(
            (float)(cx + (x - hx) * cos - (y - hy) * sin),
            (float)(cy + (x - hx) * sin + (y - hy) * cos));

        return new[] { Map(0, 0), Map(iw, 0), Map(iw, ih), Map(0, ih) };
    }

    /// <summary>
    /// 光标在窗口客户区里的位置。
    ///
    /// 不用 MouseEventArgs.Location：分层窗口的尺寸是 ULW 直接定的，WinForms 那套客户区
    /// 几何并不跟着变，读出来常常是陈旧值（同 <see cref="_menu"/> 的注释）。屏幕坐标减去
    /// 窗口原点才跟上传时用的是同一个坐标系 —— 原点取整方式也和 UploadFrame 一致。
    /// </summary>
    private PointF MouseInWindow()
    {
        var p = Cursor.Position;
        return new PointF(p.X - (float)Math.Round(_left), p.Y - (float)Math.Round(_top));
    }

    private bool IsNearContentCorner() => IsNearContentCorner(MouseInWindow());

    /// <param name="p">窗口客户区坐标。单独开这个重载是为了不依赖真实光标位置，好单独验证。</param>
    private bool IsNearContentCorner(PointF p)
    {
        if (_frame is null || _frameW <= 0 || _frameH <= 0) return false;

        double r2 = RotateGrabRadius * RotateGrabRadius;
        foreach (var c in ContentCorners())
        {
            double dx = p.X - c.X, dy = p.Y - c.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    /// <summary>
    /// 「内容中心 → 光标」的方向（度）。
    ///
    /// 屏幕坐标 y 向下，atan2(dy, dx) 增大的方向恰好就是视觉上的顺时针 —— 跟
    /// <see cref="_angle"/> 的旋向是同一个约定（<see cref="RotationTerms"/> 里顺时针取
    /// +sin 也是这个道理），所以这里不用再翻一次符号。返回 (-180, 180]，
    /// 归一化交给用它的地方（差值和 NormalizeAngle 都会处理）。
    /// </summary>
    private double AngleToMouse()
    {
        var p = Cursor.Position;
        var (cx, cy) = ContentCenter;
        return Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI;
    }

    private void UpdateRotateDrag() => ApplyRotateDrag(AngleToMouse());

    /// <summary>
    /// 角上拖拽期间的一帧：目标角度 = 按下时的角度 + 光标扫过的角度。
    ///
    /// 累加**增量**，而不是把「中心 → 光标」的绝对方向直接当角度：那样光标扫过
    /// 0°/360° 那条接缝时会跳一整圈（atan2 在那儿从 +180 翻到 -180）。增量每帧折到
    /// (-180, 180] —— 两次鼠标移动之间不可能真转过半圈，出现更大的跳变一定是接缝，
    /// 折回来即可。
    /// </summary>
    /// <param name="pointerAngle">当前「中心 → 光标」的方向（度）。</param>
    private void ApplyRotateDrag(double pointerAngle)
    {
        double step = pointerAngle - _rotateLastAngle;
        if (step > 180.0) step -= 360.0;
        else if (step <= -180.0) step += 360.0;

        _rotateAccum += step;
        _rotateLastAngle = pointerAngle;

        RotateTo(SnapAngle(_rotateStartAngle + _rotateAccum, (ModifierKeys & Keys.Shift) != 0));
    }

    /// <summary>
    /// 拖拽得到的目标角度要不要吸附。按住 Shift 时按 <see cref="RotatePerNotchCoarse"/>
    /// 的粒度对齐（跟 Alt+Shift+滚轮同一档，两处手感一致）；否则只在 90° 整数倍附近吸附。
    /// </summary>
    /// <param name="fine">Shift 按住时为真，走 15° 一档。</param>
    private static double SnapAngle(double degrees, bool fine)
    {
        if (fine) return Math.Round(degrees / RotatePerNotchCoarse) * RotatePerNotchCoarse;

        double nearest = Math.Round(degrees / 90.0) * 90.0;
        return Math.Abs(degrees - nearest) <= RotateSnapEpsilon ? nearest : degrees;
    }

    /// <summary>
    /// 刷新「光标是不是在角上」，并据此换光标形状。
    ///
    /// 由 <see cref="WndProc"/> 在每次 WM_SETCURSOR 时现算，而不是用 OnMouseMove 缓存下来的
    /// 状态：系统可能先发 WM_SETCURSOR 再发那次鼠标移动的消息，用缓存的话第一下显示的还是
    /// 箭头。旋转/挪位置期间一律不算 —— 那时候图片在动，「角」每帧都在换地方。
    /// </summary>
    private void UpdateHoverCorner()
    {
        bool near = !_rotating && !_dragging && IsNearContentCorner();
        if (near == _hoverCorner) return;

        _hoverCorner = near;
        // 立刻生效，不等下一次 WM_SETCURSOR：光标可能已经停在角上不动了。
        // 这一句只是个补丁，真正的形状由 WndProc 那条路保证。
        Cursor.Current = near ? RotateCursor : Cursors.Default;
    }

    private static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        return a < 0 ? a + 360.0 : a;
    }

    private void RotateBy(double delta) => RotateTo(_angle + delta);

    /// <summary>
    /// 转到指定角度（顺时针，度）。以内容中心为锚 —— 旋转只改朝向，中心点钉在原地，
    /// 不然转一下整张贴图就绕着外接矩形的左上角甩出去半张屏幕。
    ///
    /// 中心的取法：先按**旧**帧（也就是旧角度下的外接矩形）算出中心，改完角度、
    /// 拿到新外接矩形之后再用它反推左上角。顺序反过来就不是绕中心转了。
    /// </summary>
    private void RotateTo(double degrees)
    {
        // 量化到 1e-3 度：一来浮点累加不会跑出一串无意义的小数，二来 IsQuarterTurn
        // 和帧缓存的比较都能直接用等号
        double next = NormalizeAngle(Math.Round(degrees, 3));
        if (Math.Abs(next - _angle) < 1e-9) return;

        var (cx, cy) = ContentCenter;
        _angle = next;

        // 角度一变，帧的外接矩形就跟着变大（45° 附近接近两倍面积），同一个倍率现在
        // 可能要占近两倍的帧位图。顺手把倍率收进新上限内 —— 只在本来贴着上限的大图上
        // 才会真的收，而且宁可大小变一点点，也好过 new Bitmap 抛 OOM。
        _scale = Math.Clamp(_scale, MinScale, Math.Min(MaxScale, MaxScaleForImage));

        var (w, h) = FrameSize(_scale);
        _left = cx - w / 2.0;
        _top = cy - h / 2.0;

        RenderFrame();

        // 拖拽旋转期间这条路上每帧都会走一遍，落盘得等松手那一下（见 OnMouseUp）。
        // 滚轮、菜单、Ctrl+R 那几条是离散动作，各转一次写一次
        if (!_rotating) NotifyChanged();
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
    /// 角度是不是 90° 的整数倍。<see cref="_angle"/> 改动时已量化到 1e-3 度，这里直接比。
    /// </summary>
    private bool IsQuarterTurn => Math.Abs(_angle % 90.0) < 1e-9;

    /// <summary>
    /// 旋转矩阵要用的 cos/sin，90° 的整数倍吸附到精确的 0 / ±1。
    ///
    /// 不吸附不行：浮点里 cos(90°) 是 6.1e-17 而不是 0，带着这点残渣去转，整张图会被
    /// 平移半个像素再插值一遍，1:1 时那股像素级的锐利就没了。而 90° 本来就只是把行和
    /// 列换个方向，一个采样点都不该动。
    /// </summary>
    private (double cos, double sin) RotationTerms()
    {
        double rad = _angle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        if (Math.Abs(cos) < 1e-12) cos = 0;
        else if (Math.Abs(Math.Abs(cos) - 1) < 1e-12) cos = Math.Sign(cos);

        if (Math.Abs(sin) < 1e-12) sin = 0;
        else if (Math.Abs(Math.Abs(sin) - 1) < 1e-12) sin = Math.Sign(sin);

        return (cos, sin);
    }

    /// <summary>
    /// 帧的重采样方式。没转角度就照旧走 <see cref="ResampleMode"/>。
    ///
    /// 转过角度得绕开 1:1 那一档：<see cref="ResampleMode"/> 在倍率正好 1.0 时给的正是
    /// 最近邻，而最近邻只对「像素网格跟屏幕对齐」的画面才叫锐利 —— 转过之后网格本来
    /// 就不对齐了，再用它只会满屏锯齿。
    ///
    /// 90° 的整数倍反过来，必须最近邻：那只是把行列换向，不需要插值，用双线/双三次
    /// 反而会踩在两个源像素正中间、白白糊掉一个像素。
    /// </summary>
    private InterpolationMode FrameResampleMode(bool rotated)
    {
        if (!rotated) return ResampleMode(_scale);
        if (IsQuarterTurn) return InterpolationMode.NearestNeighbor;
        return _scale < 1.0
            ? InterpolationMode.HighQualityBicubic
            : InterpolationMode.HighQualityBilinear;
    }

    /// <summary>
    /// 当前倍率下、还没旋转的图片尺寸。倍率已被 <see cref="ZoomAround"/> 钳在
    /// <see cref="MaxScaleForImage"/> 之内，这里直接相乘即可，不用再兜底 ——
    /// 兜底放在倍率上（不然又会破「尺寸 = 图片 × 倍率」的不变量）。
    /// </summary>
    private (int w, int h) ScaledSize(double scale)
        => (Math.Max(1, (int)Math.Round(_imageW * scale)),
            Math.Max(1, (int)Math.Round(_imageH * scale)));

    /// <summary>
    /// 帧位图尺寸 = 上面那个缩放尺寸、再绕中心转过 <see cref="_angle"/> 之后的外接矩形。
    ///
    /// 90° 的整数倍宽高正好互换，没有取整余量；其它角度一律向上取整，否则转出来的角
    /// 会被裁掉一条像素。取整只带来 1px 以内的误差，而且不累积 —— 位置始终由倍率和
    /// 角度直接算，不从渲染尺寸反推（见 <see cref="ZoomAround"/>）。
    ///
    /// 「外接矩形随倍率线性增长」这条不变量仍然成立，锚点缩放才继续有效。
    /// </summary>
    private (int w, int h) FrameSize(double scale)
    {
        var (iw, ih) = ScaledSize(scale);

        var (c, s) = RotationTerms();
        if (c == 1.0 && s == 0.0) return (iw, ih);

        double cAbs = Math.Abs(c), sAbs = Math.Abs(s);
        double w = iw * cAbs + ih * sAbs;
        double h = iw * sAbs + ih * cAbs;

        // 90° 的整数倍：两项里有一项必为 0，结果本来就是整数，Round 只是走个形式
        return IsQuarterTurn
            ? ((int)Math.Round(w), (int)Math.Round(h))
            : (Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h)));
    }

    /// <summary>
    /// 几何变了（缩放或旋转）：重画整帧，然后原子送上去。帧按「尺寸 + 角度」缓存 ——
    /// 拖动只改位置，直接复用这一帧，不重画。
    ///
    /// 角度必须一起比：转过一点点角度时外接矩形的取整结果常常没变（尺寸一样），
    /// 只比尺寸就会拿一张旧朝向的帧继续显示。
    /// </summary>
    private void RenderFrame()
    {
        var (w, h) = FrameSize(_scale);
        if (_frame is null || _frameW != w || _frameH != h || _frameAngle != _angle)
        {
            DrawFrame(w, h);
            _frameW = w;
            _frameH = h;
            _frameAngle = _angle;
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

        var (iw, ih) = ScaledSize(_scale);

        var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            // 图片自己的中心 ↦ 帧画布中心，也就是 p' = R·(p - 图片中心) + 画布中心。
            //
            // GDI+ 的仿射矩阵把 (x,y) 映射到 (x·m11 + y·m21 + dx, x·m12 + y·m22 + dy)，
            // 顺时针转 θ 就是 m11 = cos、m12 = sin、m21 = -sin、m22 = cos；dx/dy 直接填
            // 上面那条式子化简完的平移量，省一次 Translate 调用。
            //
            // 不走 Matrix.Rotate()：它内部自己算 sin/cos，90° 拿到的是 6.1e-17 那种残渣，
            // 整张图会被平移半个像素再插值一遍（见 RotationTerms）。
            var (cos, sin) = RotationTerms();
            float fc = (float)cos, fs = (float)sin;
            float halfW = w / 2f, halfH = h / 2f;
            float halfIW = iw / 2f, halfIH = ih / 2f;
            var transform = new Matrix(
                fc, fs, -fs, fc,
                halfW - (fc * halfIW - fs * halfIH),
                halfH - (fs * halfIW + fc * halfIH));

            // 没转角度时矩阵正好是单位阵（w == iw、h == ih，两个半宽相减是 0），
            // 不进变换分支，绘制路径跟加旋转之前一模一样。
            bool rotated = !transform.IsIdentity;
            if (rotated) g.Transform = transform;

            g.InterpolationMode = FrameResampleMode(rotated);
            g.PixelOffsetMode = _scale >= CrispThreshold
                ? PixelOffsetMode.Half
                : PixelOffsetMode.HighQuality;

            g.DrawImage(_bitmap, new Rectangle(0, 0, iw, ih));

            // 一圈实色细边把贴图跟桌面内容分开。
            //
            // 1px 的笔是居中对齐的：按整数坐标画时，上/左沿的笔有一半探到画布外
            // （y/x = 0 之外），那一半被 GDI 裁掉，于是只剩右/下沿有边。
            // 把矩形按半像素内移，笔正好铺满最外圈的那行/那列像素，四条边都在。
            // （唯一例外是左上角 (0,0) 那一个像素：非抗锯齿下 GDI+ 描闭合矩形会跳掉起笔
            // 那个点，实测就是这么漏一个像素。这一点在加旋转之前就存在，没跟着改。）
            // 图片此时已画完，这里把像素偏移切到不偏移的 None，保证半像素内移
            // 在任何倍率下都按原样生效，不受上面缩放用的偏移模式影响。
            //
            // 转过角度时边框是斜的，得开抗锯齿，否则 1px 的斜线是一格一格的台阶；
            // 不转时保持默认的 None —— 抗锯齿会把 1px 实线摊虚，那种锐利细边就没了。
            // 边框画在图片自己的矩形上（变换会跟着转），所以它始终贴着图的边。
            if (rotated && !IsQuarterTurn) g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.None;
            using var pen = new Pen(BorderColor);
            g.DrawRectangle(pen, 0.5f, 0.5f, iw - 1, ih - 1);
        }

        _frame = frame;
        // HBITMAP 缓存下来反复用：拖动时每次 ULW 不必重新 GetHbitmap 拷一遍整帧。
        //
        // 这个 Color.Black 现在是**要紧**的：转过角度后帧的四角是透明像素，而无参的
        // GetHbitmap() 会给透明像素填上垃圾 RGB（实测 A=0 但 RGB=211）。ULW 收的是
        // 预乘 alpha，A=0 而 RGB 不为 0 的像素合成时等于「把 RGB 加到底下的画面上」，
        // 于是四角会浮出一层灰白色块。传了黑底就没有这回事（透明像素是干净的 0,0,0,0）。
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
        NotifyChanged();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        // 角上按下是转，别处按下是挪。两件事都吃左键，靠命中的位置分 —— 贴图没有边距，
        // 没有别的地方能放旋转的入口。
        if (IsNearContentCorner())
        {
            _rotating = true;
            _rotateStartAngle = _angle;
            _rotateAccum = 0;
            _rotateLastAngle = AngleToMouse();
            _hoverCorner = false;
            return;
        }

        _dragOrigin = Cursor.Position; // 屏幕物理坐标
        _dragStartLeft = _left;
        _dragStartTop = _top;
        _dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 拖拽期间鼠标被 WinForms 捕获，光标移出窗口也照收消息 —— 旋转本来就是可以
        // 把光标甩到图外面的操作，这里不用额外处理。
        if (_rotating)
        {
            UpdateRotateDrag();
            return;
        }

        if (_dragging)
        {
            var now = Cursor.Position;
            _left = _dragStartLeft + (now.X - _dragOrigin.X);
            _top = _dragStartTop + (now.Y - _dragOrigin.Y);
            UploadFrame();
            return;
        }

        // 既没在拖也没在转：只管光标形状。命中判定得现算，缩放和旋转之后角都换地方了。
        UpdateHoverCorner();
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
        if (!_rotating)
        {
            // 只是挪了位置：也只在松手这一刻写一次存档（拖动期间每帧都写就是几十遍索引）
            NotifyChanged();
            return;
        }

        _rotating = false;
        // 刚转完，图片的四个角已经换地方了，光标形状得按新位置重判一次
        UpdateHoverCorner();
        NotifyChanged();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // 落在角上的不当作关闭手势。双击关闭是原有行为，而角区是这次新加的旋转入口 ——
        // 想抓着角转、结果手快点了两下，贴图就直接没了，这个坑不该留给用户踩。
        if (IsNearContentCorner()) return;

        Close();
    }

    protected override void WndProc(ref Message m)
    {
        // 悬停在图片角上时换成旋转光标。
        //
        // 自己 SetCursor 而不是挂 Control.Cursor：分层窗口没有子控件，而 WinForms 决定
        // 用哪个光标时走的是它自己维护的客户区几何（同 _menu 的注释），靠不住。
        // 直接处理这条消息最干脆。m.Result 置 1 是告诉系统「光标我设好了」——
        // 不置的话后面 DefWindowProc 还会把它设回箭头。
        if (m.Msg == NativeMethods.WM_SETCURSOR)
        {
            UpdateHoverCorner();
            if (_hoverCorner)
            {
                NativeMethods.SetCursor(RotateCursor.Handle);
                m.Result = new IntPtr(1);
                return;
            }
        }

        base.WndProc(ref m);
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

        // Alt+滚轮转角度，往上滚是顺时针。分粗细两档：默认一格 1°，「把歪掉的截图
        // 摆正」要的正是能停在任意角度上；跨大角度（90° 这种）按 Shift，一格 15°。
        // 同样按格数累加，精密滚轮给的小数格不会被吃成整格。
        if ((ModifierKeys & Keys.Alt) != 0)
        {
            double step = (ModifierKeys & Keys.Shift) != 0 ? RotatePerNotchCoarse : RotatePerNotch;
            RotateBy(step * notches);
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
            case Keys.Delete:
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
            case Keys.R when e.Control:
                // 只把角度归零，倍率不动 —— 跟 Ctrl+0 分工
                RotateTo(0);
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
        // 以窗口中心为锚回到 1.0，内容尽量原地不动。角度不动 —— 摆正和缩回原大小
        // 是两件事，各有各的快捷键（Ctrl+R / Ctrl+0）。
        var (cx, cy) = ContentCenter;
        ZoomAround(1.0 / _scale, new Point((int)Math.Round(cx), (int)Math.Round(cy)));
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

        // 旋转有三条路，各管一段：任意角度直接用鼠标抓角拖（最直观，也最常用）、
        // 90° 这类整角走菜单、微调用 Alt+滚轮逐度推。菜单里的灰字是后两条的说明 ——
        // 鼠标那条靠光标形状自己提示，不用写进菜单。
        var rotate = new ToolStripMenuItem("旋转");
        var angleLabel = new ToolStripMenuItem("当前角度：0°") { Enabled = false };
        rotate.DropDownItems.Add(angleLabel);
        rotate.DropDownItems.Add(new ToolStripSeparator());
        rotate.DropDownItems.Add("向右旋转 90°", null, (_, _) => RotateBy(90));
        rotate.DropDownItems.Add("向左旋转 90°", null, (_, _) => RotateBy(-90));
        rotate.DropDownItems.Add("旋转 180°", null, (_, _) => RotateBy(180));
        rotate.DropDownItems.Add(new ToolStripSeparator());
        rotate.DropDownItems.Add("重置旋转 (Ctrl+R)", null, (_, _) => RotateTo(0));
        rotate.DropDownItems.Add(new ToolStripSeparator());
        rotate.DropDownItems.Add("拖拽图片四角可直接旋转").Enabled = false;
        rotate.DropDownItems.Add("Alt+滚轮 1°/格，按住 Shift 15°/格").Enabled = false;
        menu.Items.Add(rotate);

        // 菜单是常驻的一份，角度得每次弹出时现读，不然「当前角度」会一直停在第一次的值
        menu.Opening += (_, _) => angleLabel.Text = $"当前角度：{_angle:0.##}°";

        // 初值照窗口此刻的实际状态来：从存档恢复的贴图可能本来就没置顶（见恢复构造），
        // 写死 true 的话，菜单上勾着、窗口却压在别的窗口底下
        var topmostItem = new ToolStripMenuItem("总在最前") { CheckOnClick = true, Checked = TopMost };
        topmostItem.Click += (_, _) =>
        {
            TopMost = topmostItem.Checked;
            // 置顶与否也要存：不存的话，一张特意摘掉置顶的贴图重启后又压回最上面
            NotifyChanged();
        };
        menu.Items.Add(topmostItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭 (Esc / Del)", null, (_, _) => Close());

        return menu;
    }

    /// <summary>菜单中心点（键盘呼出菜单时用，屏幕物理坐标）。</summary>
    private Point MenuCenterPoint
    {
        get
        {
            var (cx, cy) = ContentCenter;
            return new Point((int)Math.Round(cx), (int)Math.Round(cy));
        }
    }

    private static Cursor? _rotateCursor;

    /// <summary>
    /// 旋转光标。Windows 没有内置的旋转光标 —— IDC_* 那一串里只有箭头、工字光标和
    /// 四种缩放箭头，没有任何一个能表达「抓住这里转」。所以现画一个：一圈带箭头的弧，
    /// 深色描边压一条白芯，深浅不一的桌面上都看得见（这也是光标的老规矩 ——
    /// 万一某条渲染路径按 XOR 合成，这个配色也只是反色，形状还在）。
    ///
    /// 现画而不是塞一个 .cur 资源文件：省一个二进制资源，尺寸也能直接跟着系统光标大小走。
    /// 画一次就缓存住，别每次悬停都重画 —— 光标是高频路径。
    /// </summary>
    private static Cursor RotateCursor => _rotateCursor ??= BuildRotateCursor();

    private static Cursor BuildRotateCursor()
    {
        var size = SystemInformation.CursorSize; // 一般 32×32，跟着系统设置走
        int w = size.Width, h = size.Height;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float r = w * 0.30f;
            float thick = Math.Max(2f, w / 14f);
            var ring = new RectangleF(w / 2f - r, h / 2f - r, r * 2, r * 2);

            // GDI+ 的角度是「0° 在正右方、顺时针增大」，而屏幕坐标 y 向下，
            // 所以这个角度跟视觉上的顺时针就是同一回事。
            const float start = -60f;
            const float sweep = 270f;

            // 先粗描一圈深色，再用白色压在中间。光标落在深色背景上时靠白芯，
            // 落在浅色背景上时靠深边，两头都立得住。
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), thick + 2f))
                g.DrawArc(pen, ring, start, sweep);
            using (var pen = new Pen(Color.White, thick))
                g.DrawArc(pen, ring, start, sweep);

            // 箭头压在弧的收笔端，沿着切线继续指 —— 看着就是「从这儿往下转」
            double end = (start + sweep) * Math.PI / 180.0;
            float ex = w / 2f + (float)(r * Math.Cos(end));
            float ey = h / 2f + (float)(r * Math.Sin(end));
            float tx = (float)-Math.Sin(end), ty = (float)Math.Cos(end); // 切线
            float nx = -ty, ny = tx;                                     // 法线

            float len = w * 0.20f, half = w * 0.15f;
            var head = new[]
            {
                new PointF(ex + tx * len, ey + ty * len),
                new PointF(ex + nx * half, ey + ny * half),
                new PointF(ex - nx * half, ey - ny * half),
            };
            using (var brush = new SolidBrush(Color.White)) g.FillPolygon(brush, head);
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), Math.Max(1f, w / 24f)))
                g.DrawPolygon(pen, head);
        }

        return CursorFromBitmap(bmp);
    }

    /// <summary>
    /// 把位图做成一个光标。分两步：先 <see cref="EncodeCursor"/> 组出 .cur 字节，再落到
    /// 临时文件交给 <c>LoadCursorFromFile</c>。
    ///
    /// **不能走 new Cursor(Stream)**，哪怕它省事得多。实测（同一张 32bpp 图，读回句柄里的
    /// 位图看）：那条路出来的光标 hbmColor 是 0，也就是压根没有彩色位图，被降级成了
    /// 1bpp 单色光标 —— 屏幕上看就是一个纯黑剪影，压在深色桌面上等于没有。而且这个降级
    /// 对任何 .cur 都发生，拿 Windows 自带的 Cursors\*.cur 试也一样，不是我们这份文件的毛病。
    /// 走 LoadCursorFromFile 拿到的句柄是带 alpha 的彩图，热点也照 .cur 里写的走。
    ///
    /// 临时文件读完就删：LoadCursorFromFile 在调用当刻就把图像解出来了，句柄不依赖文件。
    /// </summary>
    private static Cursor CursorFromBitmap(Bitmap bmp)
    {
        var bytes = EncodeCursor(bmp);
        var tmp = Path.Combine(Path.GetTempPath(), $"xkscreenshot-rotate-{Environment.ProcessId}.cur");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var handle = NativeMethods.LoadCursorFromFile(tmp);
            if (handle != IntPtr.Zero) return new Cursor(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录写不进去或读不出来（少见，但不是不可能）。落到下面的兜底。
            // UnauthorizedAccessException 不是 IOException 的子类，得单独列 —— 漏了它
            // 就是一次悬停把整个程序带崩，光标这种东西不值这个代价。
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就算了：一个临时文件不值得把悬停反馈整个带崩
            }
        }

        // 兜底：形状不贴切也强过悬停在角上毫无反馈 —— 那等于功能不存在
        return Cursors.Hand;
    }

    /// <summary>
    /// 把一张位图组包成 .cur 的字节流。
    ///
    /// 之所以要自己组字节而不找现成 API：热点得跟着走。走 new Cursor(IntPtr) 那条路热点
    /// 只能是 (0,0)，光标尖戳在左上角，跟图上画的圆心差着半个光标，用起来明显别扭。
    /// （CreateIconFromResourceEx 也试过，它收的是单张图的位、没有热点信息，而且实测返回空。）
    ///
    /// .cur 的布局跟 .ico 几乎一样，两处不同：ICONDIR 的 type 是 2；.ico 用来放
    /// planes/bitCount 的那 4 个字节，在 .cur 里是热点坐标。图像数据本身是一张 DIB，
    /// BITMAPINFOHEADER 的 biHeight 要写成**两倍**高 —— 底下还跟着一张 1bpp 的 AND 掩码，
    /// 32bpp 的 alpha 已经够表达透明了，但格式要求这块必须在，填掉即可。
    /// </summary>
    private static byte[] EncodeCursor(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorStride = w * 4;
        int andStride = (w + 31) / 32 * 4;

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR
            bw.Write((ushort)0); // 保留
            bw.Write((ushort)2); // 类型：2 = 光标
            bw.Write((ushort)1); // 就一张图

            // ICONDIRENTRY。宽高各占 1 字节，256 记作 0
            bw.Write((byte)(w >= 256 ? 0 : w));
            bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0); // 调色板色数，32bpp 用不着
            bw.Write((byte)0); // 保留
            bw.Write((ushort)(w / 2)); // 热点：图案是同心圆，取正中
            bw.Write((ushort)(h / 2));
            bw.Write(40 + xorStride * h + andStride * h);
            bw.Write(22); // 图像数据偏移 = ICONDIR(6) + 一条目录项(16)

            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2); // 两倍高：XOR 位 + AND 掩码
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0); // BI_RGB
            bw.Write(xorStride * h + andStride * h);
            bw.Write(0); // 横向分辨率，用不着
            bw.Write(0); // 纵向分辨率
            bw.Write(0); // 调色板色数
            bw.Write(0); // 重要色数

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var xor = new byte[xorStride * h];
                var and = new byte[andStride * h];
                var row = new byte[xorStride];

                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, xorStride);
                    // DIB 是自下而上存的：源图第 y 行落到第 (h-1-y) 行
                    Buffer.BlockCopy(row, 0, xor, (h - 1 - y) * xorStride, xorStride);

                    // AND 掩码只有「透明 / 不透明」两档，给全透明像素打上 1（保留背景）。
                    // 现代 Windows 对 32bpp 光标直接认 alpha，这张掩码用不上 —— 留着兜底：
                    // 万一哪条渲染路径忽略 alpha，也只是颜色被 XOR 掉，不会整块变黑方块。
                    for (int x = 0; x < w; x++)
                        if (row[x * 4 + 3] == 0)
                            and[(h - 1 - y) * andStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }

                bw.Write(xor);
                bw.Write(and);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        return ms.ToArray();
    }

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
