using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Scroll;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Gif;

/// <summary>
/// 录制控制条的窗口：只管落位、排除截屏和按键，长相全在 <see cref="GifPanelView"/>。
///
/// 落位与长截图控制条同一套：依次试下、上、右、左，挑第一个完全在桌面内且不压选区的位置；
/// 都塞不下才退到工作区角上，那时候只能靠「排除截屏」保证它不被录进去。
/// </summary>
internal sealed class GifPanelWindow : Window
{
    /// <summary>面板与选区之间的空隙，DIP。与长截图控制条取同一个值。</summary>
    private const double GapToSelection = 12;

    private readonly PixelRect _region;
    private readonly GifPanelView _view;
    private readonly ScrollTipWindow _tip;

    private bool _placed;

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public GifPanelWindow(PixelRect region, bool dark)
    {
        _region = region;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;

        var size = GifPanelView.OuterSize;
        Width = size.Width;
        Height = size.Height;

        // 摆到正确位置之前先透明，否则面板会先在窗口默认位置闪一下
        Opacity = 0;

        var palette = OverlayPalette.For(dark);

        _view = new GifPanelView(palette);
        _view.StopRequested += () => StopRequested?.Invoke();
        _view.CancelRequested += () => CancelRequested?.Invoke();
        Content = _view;

        // 悬停说明超出这条窄条，只能另开一扇窗 —— 和长截图控制条共用同一个气泡窗
        _tip = new ScrollTipWindow(palette);
        _view.TipChanged += OnTipChanged;
        Closed += (_, _) => _tip.CloseTip();

        SourceInitialized += (_, _) => ScrollChrome.ExcludeFromCapture(this);
        ContentRendered += (_, _) => Place();
    }

    public void UpdateRecording(TimeSpan elapsed, int frames) => _view.UpdateRecording(elapsed, frames);

    public void ShowEncoding() => _view.ShowEncoding();

    public void UpdateProgress(double progress) => _view.UpdateProgress(progress);

    /// <summary>面板报的是局部 DIP 矩形，气泡要的是屏幕物理像素 —— 只有窗口这层同时知道两者。</summary>
    private void OnTipChanged(Rect local, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _tip.HideTip();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        _tip.ShowTip(text, new PixelRect(
            rect.Left + (int)Math.Round(local.X * scale),
            rect.Top + (int)Math.Round(local.Y * scale),
            (int)Math.Round(local.Width * scale),
            (int)Math.Round(local.Height * scale)));
    }

    // ---------------- 落位 ----------------

    private void Place()
    {
        if (_placed) return;
        _placed = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        int pad = (int)Math.Round(GifPanelView.ShadowPad * scale);
        int gap = Math.Max((int)Math.Round(GapToSelection * scale), pad);

        int panelW = Math.Max(1, rect.Right - rect.Left - pad * 2);
        int panelH = Math.Max(1, rect.Bottom - rect.Top - pad * 2);

        var work = WorkArea();

        foreach (var panel in Candidates(panelW, panelH, work, gap))
        {
            if (panel.X < work.X || panel.Y < work.Y) continue;
            if (panel.Right > work.Right || panel.Bottom > work.Bottom) continue;
            if (panel.IntersectsWith(_region)) continue;

            ShowAt(panel, pad);
            return;
        }

        // 实在无处可去：贴工作区右下角，靠「排除截屏」兜着
        ShowAt(new PixelRect(work.Right - panelW - gap, work.Bottom - panelH - gap, panelW, panelH), pad);
    }

    private void ShowAt(PixelRect panel, int pad)
    {
        ScrollChrome.PlacePixels(this, new PixelRect(
            panel.X - pad, panel.Y - pad,
            panel.Width + pad * 2, panel.Height + pad * 2), activate: true);

        Opacity = 1;
    }

    private IEnumerable<PixelRect> Candidates(int w, int h, PixelRect work, int gap)
    {
        int centerX = Math.Clamp(
            _region.X + (_region.Width - w) / 2, work.X, Math.Max(work.X, work.Right - w));
        int centerY = Math.Clamp(
            _region.Y + (_region.Height - h) / 2, work.Y, Math.Max(work.Y, work.Bottom - h));

        yield return new PixelRect(centerX, _region.Bottom + gap, w, h);
        yield return new PixelRect(centerX, _region.Y - gap - h, w, h);
        yield return new PixelRect(_region.Right + gap, centerY, w, h);
        yield return new PixelRect(_region.X - gap - w, centerY, w, h);
    }

    private PixelRect WorkArea()
    {
        var monitors = MonitorEnumerator.Enumerate();
        var anchor = new PixelPoint(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        var monitor = MonitorEnumerator.FromPoint(monitors, anchor)
                      ?? monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(_region))
                      ?? monitors.FirstOrDefault();
        return monitor?.WorkArea ?? _region;
    }

    // ---------------- 输入 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        switch (e.Key)
        {
            case Key.Enter:
                StopRequested?.Invoke();
                break;

            case Key.Escape:
                CancelRequested?.Invoke();
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
