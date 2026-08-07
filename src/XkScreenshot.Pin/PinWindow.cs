using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Pin;

/// <summary>
/// 贴图窗口：把一张截图钉在桌面最上层。
///
/// 尺寸与位置全程用物理像素经 SetWindowPos 控制，不碰 WPF 的 Left/Top/Width/Height。
/// 那几个属性是 DIP，而贴图会被拖到任意一台显示器上 —— 在 PerMonitorV2 下窗口跨屏时
/// DIP 基准会跟着变，用它们定位必然在混合 DPI 环境里错位。
/// </summary>
public sealed class PinWindow : Window
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

    private readonly BitmapSource _image;
    private readonly Image _presenter;
    private readonly Border _frame;

    private double _scale = 1.0;

    /// <summary>
    /// 贴图左上角在虚拟屏幕上的位置，保留小数，只在下发给 SetWindowPos 时取整。
    ///
    /// 这一条是简化不是修 bug：原来那种「每步从取整后的矩形反推相对位置」的写法误差并不累积
    /// （量过，滚 15 格两者都在半个源像素以内），但它要求矩形尺寸和倍数永远严丝合缝，
    /// 而尺寸恰恰是取过整的。位置只从倍数和锚点算，就没有这层耦合了。
    /// </summary>
    private double _left;
    private double _top;

    private bool _dragging;
    private PixelPoint _dragOrigin;
    private PixelRect _dragStartBounds;

    public PinWindow(BitmapSource image, PixelRect origin)
    {
        _image = image;
        _image.Freeze();
        _left = origin.X;
        _top = origin.Y;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Title = "XkScreenshot 贴图";

        _presenter = new Image { Source = _image, Stretch = Stretch.Fill };
        ApplyScalingMode();

        // 一圈细边把贴图和它底下的桌面内容分开，否则截的是什么就跟背景糊成一片。
        //
        // 边框**盖在图上**而不是把图挤进去。挤进去的话图实际被排到的宽度是窗口减两个边框：
        // 量出来 800 DIP 的窗口只给图 798 DIP —— 也就是说连 100% 都在重采样，字从来没清晰过；
        // 而每缩放一格，这个「差两个边框」会让实际倍数偏离标称值一点点，重采样把哪些像素
        // 摊成两个的账就得重算一遍，笔画于是一格一个样。边框改成盖在上面，图就正好铺满窗口。
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x3B, 0x9E, 0xFF)),
            IsHitTestVisible = false,
        };

        var stack = new Grid();
        stack.Children.Add(_presenter);
        stack.Children.Add(_frame);
        Content = stack;

        SourceInitialized += (_, _) => ApplyBounds(show: true);
        BuildContextMenu();
    }

    /// <summary>贴图在虚拟屏幕上的物理像素矩形。</summary>
    public PixelRect PhysicalBounds => new(
        (int)Math.Round(_left), (int)Math.Round(_top), ScaledWidth, ScaledHeight);

    public double Scale => _scale;

    private int ScaledWidth => Math.Max(1, (int)Math.Round(_image.PixelWidth * _scale));
    private int ScaledHeight => Math.Max(1, (int)Math.Round(_image.PixelHeight * _scale));

    /// <summary>用户请求复制这张贴图。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>用户请求另存这张贴图。</summary>
    public event Action<BitmapSource>? SaveRequested;

    /// <summary>
    /// 把当前的位置尺寸下发给窗口。
    ///
    /// 除了第一次显示，一律不动 Z 序：每滚一格都重新申明一次「置顶」，等于让窗口管理器
    /// 在每一帧里插一次 Z 序调整，缩放和拖拽都会因此一顿一顿的。
    /// </summary>
    private void ApplyBounds(bool show = false)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var rect = PhysicalBounds;
        uint flags = show
            ? NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
            : NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOCOPYBITS;

        NativeMethods.SetWindowPos(hwnd,
            show ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            rect.X, rect.Y, rect.Width, rect.Height, flags);
    }

    /// <summary>
    /// 按当前倍数挑重采样方式。三档各有各的道理：
    /// 缩小用 Fant（要按面积平均，否则细线会闪成一段一段的）；
    /// 常用的那一段放大用双线性（平滑，而且走显卡，滚起来不掉帧）；
    /// 放到很大才用最近邻，理由见 <see cref="CrispThreshold"/>。
    /// </summary>
    private void ApplyScalingMode()
    {
        var mode = _scale >= CrispThreshold ? BitmapScalingMode.NearestNeighbor
            : _scale < 1.0 ? BitmapScalingMode.HighQuality
            : BitmapScalingMode.Linear;

        RenderOptions.SetBitmapScalingMode(_presenter, mode);
    }

    /// <summary>
    /// 以 <paramref name="anchor"/> 为锚点缩放：光标底下那一点内容保持不动，
    /// 否则放大几次之后想看的地方早就跑出屏幕了。
    ///
    /// 算式直接落在「左上角」上，不再经由「光标在窗口里的相对位置」绕一圈，
    /// 理由见 <see cref="_left"/>。
    /// </summary>
    private void Rescale(double factor, PixelPoint anchor)
    {
        double next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - _scale) < 1e-6) return;

        double k = next / _scale;
        _left = anchor.X - (anchor.X - _left) * k;
        _top = anchor.Y - (anchor.Y - _top) * k;
        _scale = next;

        ApplyScalingMode();
        ApplyBounds();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        _dragOrigin = MonitorEnumerator.GetCursorPosition();
        _dragStartBounds = PhysicalBounds;
        _dragging = CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var now = MonitorEnumerator.GetCursorPosition();
        _left = _dragStartBounds.X + (now.X - _dragOrigin.X);
        _top = _dragStartBounds.Y + (now.Y - _dragOrigin.Y);
        ApplyBounds();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
    }

    /// <summary>捕获被系统强行收回时同样要复位，否则窗口会一直粘在鼠标上。</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        // 按「几格」算而不是只看正负。一条消息里可能带着好几格（滚快了系统会合并），
        // 精密滚轮和触控板给的更是不足一格的小数 —— 一律当成一整格的话，
        // 前者少走好几步、后者步步都迈满格，两头都是一跳一跳的。
        double notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        if (Math.Abs(notches) < 1e-6) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Opacity = Math.Clamp(Opacity + OpacityPerNotch * notches, MinOpacity, 1.0);
            return;
        }

        Rescale(Math.Pow(ZoomPerNotch, notches), MonitorEnumerator.GetCursorPosition());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                CopyRequested?.Invoke(_image);
                break;
            case Key.S when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                SaveRequested?.Invoke(_image);
                break;
            case Key.D0 when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                ResetScale();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ResetScale()
    {
        var center = new PixelPoint(
            PhysicalBounds.X + PhysicalBounds.Width / 2,
            PhysicalBounds.Y + PhysicalBounds.Height / 2);
        Rescale(1.0 / _scale, center);
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("复制 (Ctrl+C)", () => CopyRequested?.Invoke(_image));
        Add("另存为 (Ctrl+S)", () => SaveRequested?.Invoke(_image));
        menu.Items.Add(new Separator());
        Add("原始大小 (Ctrl+0)", ResetScale);

        var topmostItem = new MenuItem { Header = "总在最前", IsCheckable = true, IsChecked = true };
        topmostItem.Click += (_, _) =>
        {
            Topmost = topmostItem.IsChecked;
            if (Topmost) ApplyBounds();
        };
        menu.Items.Add(topmostItem);

        menu.Items.Add(new Separator());
        Add("关闭 (Esc)", Close);

        ContextMenu = menu;
    }
}
