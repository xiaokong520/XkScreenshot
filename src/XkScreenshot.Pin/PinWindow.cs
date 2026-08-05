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
    /// <summary>缩放到这个倍数以上时切成最近邻，让像素保持锐利而不是被插值糊掉。</summary>
    private const double CrispThreshold = 1.5;

    private readonly BitmapSource _image;
    private readonly Image _presenter;
    private readonly Border _frame;

    private double _scale = 1.0;
    private bool _dragging;
    private PixelPoint _dragOrigin;
    private PixelRect _dragStartBounds;

    public PinWindow(BitmapSource image, PixelRect origin)
    {
        _image = image;
        _image.Freeze();
        PhysicalBounds = origin;

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

        // 一圈细边把贴图和它底下的桌面内容分开，否则截的是什么就跟背景糊成一片
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x3B, 0x9E, 0xFF)),
            Child = _presenter,
        };
        Content = _frame;

        SourceInitialized += (_, _) => ApplyBounds();
        BuildContextMenu();
    }

    /// <summary>贴图在虚拟屏幕上的物理像素矩形。</summary>
    public PixelRect PhysicalBounds { get; private set; }

    public double Scale => _scale;

    /// <summary>用户请求复制这张贴图。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>用户请求另存这张贴图。</summary>
    public event Action<BitmapSource>? SaveRequested;

    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            PhysicalBounds.X, PhysicalBounds.Y, PhysicalBounds.Width, PhysicalBounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void ApplyScalingMode()
        => RenderOptions.SetBitmapScalingMode(_presenter,
            _scale >= CrispThreshold ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

    private void Rescale(double factor, PixelPoint anchor)
    {
        double next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - _scale) < 0.0001) return;

        int w = Math.Max(1, (int)Math.Round(_image.PixelWidth * next));
        int h = Math.Max(1, (int)Math.Round(_image.PixelHeight * next));

        // 以光标所在处为锚点缩放：光标下的那一点内容保持不动，
        // 否则放大几次之后想看的地方早就跑出屏幕了。
        double relX = PhysicalBounds.Width == 0 ? 0.5 : (anchor.X - PhysicalBounds.X) / (double)PhysicalBounds.Width;
        double relY = PhysicalBounds.Height == 0 ? 0.5 : (anchor.Y - PhysicalBounds.Y) / (double)PhysicalBounds.Height;

        _scale = next;
        PhysicalBounds = new PixelRect(
            (int)Math.Round(anchor.X - relX * w),
            (int)Math.Round(anchor.Y - relY * h),
            w, h);

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
        PhysicalBounds = _dragStartBounds.Offset(now.X - _dragOrigin.X, now.Y - _dragOrigin.Y);
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

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.08 : -0.08), MinOpacity, 1.0);
            return;
        }

        Rescale(e.Delta > 0 ? 1.1 : 1 / 1.1, MonitorEnumerator.GetCursorPosition());
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
