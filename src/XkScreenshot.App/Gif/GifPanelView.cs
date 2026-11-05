using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;

namespace XkScreenshot.App.Gif;

/// <summary>
/// 录制控制条的面板本体：**自绘**，和长截图控制条（<c>ScrollPanelView</c>）同一套画法。
///
/// 内容从左到右是一块「状态」和一组「收工」：状态是呼吸的红点、已录时长、抓到多少帧；
/// 收工是停止和取消。编码期间整条换成进度，只留一个取消 —— 那会儿已经停不下来了，
/// 再摆一个停止按钮只会让人以为还能「提前收工」。
///
/// 尺寸是常数而不是靠布局量出来的：窗口要用它算落位，等 WPF 量完会多出一帧错位。
/// </summary>
internal sealed class GifPanelView : FrameworkElement
{
    // 与 ToolbarLayer / ScrollPanelView 逐个对齐，改一处要一起改，否则两个面板立刻不一样
    private const double ButtonSize = 32;
    private const double IconSize = 18;
    private const double ButtonGap = 2;
    private const double GroupGap = 12;
    private const double PadX = 8;
    private const double PadY = 7;
    private const double CornerRadius = 10;
    private const double ButtonRadius = 7;

    private const double TimeFontSize = 13.5;
    private const double FramesFontSize = 11.5;
    private const double ProgressFontSize = 12.5;

    /// <summary>面板四周给投影留的空白，与 <see cref="PanelChrome.DrawShadow"/> 的最大扩张一致。</summary>
    public const double ShadowPad = 12;

    private const double PanelWidth = 236;
    private const double PanelHeight = ButtonSize + PadY * 2;

    public static Size OuterSize => new(PanelWidth + ShadowPad * 2, PanelHeight + ShadowPad * 2);

    /// <summary>录制时那颗红点。两套主题共用 —— 「正在录」这件事不该跟着配色变淡。</summary>
    private static readonly Color RecordRed = Color.FromRgb(0xF0, 0x44, 0x38);

    private enum Item
    {
        Stop,
        Cancel,
    }

    private readonly OverlayPalette _palette;

    private Item? _hovered;
    private Item? _pressed;

    private TimeSpan _elapsed;
    private int _frames;
    private bool _encoding;
    private double _progress;

    public GifPanelView(OverlayPalette palette)
    {
        _palette = palette;
        Focusable = false;
    }

    public event Action? StopRequested;
    public event Action? CancelRequested;

    /// <summary>悬停说明（局部 DIP 矩形 + 文字）。气泡塞不进这条窄条，由窗口另开一扇。</summary>
    public event Action<Rect, string>? TipChanged;

    public void UpdateRecording(TimeSpan elapsed, int frames)
    {
        _elapsed = elapsed;
        _frames = frames;
        InvalidateVisual();
    }

    public void ShowEncoding()
    {
        _encoding = true;
        _progress = 0;
        _hovered = null;
        InvalidateVisual();
    }

    public void UpdateProgress(double progress)
    {
        _progress = Math.Clamp(progress, 0, 1);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => OuterSize;

    protected override Size ArrangeOverride(Size finalSize) => OuterSize;

    protected override void OnRender(DrawingContext dc)
    {
        // 透明底铺满：命中测试按绘制内容算，没有这一笔，投影留白那圈点不到（点空白 = 拖走面板）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var panel = new Rect(ShadowPad, ShadowPad, PanelWidth, PanelHeight);

        // 压在实时画面上，没有可模糊的底，走 DrawGlassPanel 的不透明分支 —— 与长截图控制条一致
        PanelChrome.DrawGlassPanel(dc, panel, CornerRadius, null, RenderSize, _palette);

        double centerY = panel.Y + panel.Height / 2;
        double left = panel.X + PadX;

        if (_encoding)
            DrawProgress(dc, panel, left, centerY);
        else
            DrawRecording(dc, panel, left, centerY);

        foreach (var (item, rect) in Layout(panel))
            DrawButton(dc, rect, item);

        if (_hovered is not null && !_encoding)
            DrawTip(dc, panel, _hovered.Value);
    }

    private void DrawRecording(DrawingContext dc, Rect panel, double left, double centerY)
    {
        // 呼吸的红点：亮 650ms、暗 350ms。一帧一帧地闪会显得焦虑，这个节奏刚好在
        // 余光里能看出「还在录」，又不至于跟内容抢注意力
        bool on = _elapsed.TotalMilliseconds % 1000 < 650;
        var red = new SolidColorBrush(Color.FromArgb(on ? (byte)0xFF : (byte)0x50,
            RecordRed.R, RecordRed.G, RecordRed.B));
        red.Freeze();

        dc.DrawEllipse(red, null, new Point(left + 5, centerY), 4.5, 4.5);

        var time = Text(FormatTime(_elapsed), TimeFontSize, FontWeights.SemiBold, _palette.Text);
        dc.DrawText(time, new Point(left + 17, centerY - time.Height / 2));

        var frames = Text($"· {_frames} 帧", FramesFontSize, FontWeights.Normal, _palette.TextMuted);
        dc.DrawText(frames, new Point(left + 72, centerY - frames.Height / 2));

        // 分隔线落在「状态」和「收工」之间，与工具条那套分组画法一致
        double separatorX = panel.Right - PadX - ButtonSize * 2 - ButtonGap - GroupGap / 2;
        dc.DrawRectangle(_palette.Separator, null,
            new Rect(Math.Round(separatorX) + 0.5, panel.Y + PadY + 5, 1, ButtonSize - 10));
    }

    private void DrawProgress(DrawingContext dc, Rect panel, double left, double centerY)
    {
        int percent = (int)Math.Round(_progress * 100);
        var text = Text($"生成 GIF {percent}%", ProgressFontSize, FontWeights.SemiBold, _palette.Text);
        dc.DrawText(text, new Point(left, centerY - text.Height / 2));

        // 沿着面板底边内侧走一条细进度线：不占地方，也不会把文字挤走
        double y = panel.Bottom - 5.5;
        double width = panel.Width - PadX * 2;

        dc.DrawRoundedRectangle(_palette.Separator, null, new Rect(left, y, width, 2), 1, 1);

        if (_progress <= 0) return;

        dc.DrawRoundedRectangle(_palette.Accent, null,
            new Rect(left, y, Math.Max(1, width * _progress), 2), 1, 1);
    }

    private void DrawButton(DrawingContext dc, Rect rect, Item item)
    {
        bool hovered = _hovered == item;

        if (hovered)
            dc.DrawRoundedRectangle(_palette.Hover, null, rect, ButtonRadius, ButtonRadius);

        var brush = item == Item.Cancel ? _palette.IconDanger : _palette.Icon;

        if (item == Item.Stop)
        {
            // 停止画成实心方块：描边的方框在 32px 的按钮里像一枚复选框，
            // 而它是这条栏上最该被一眼认出来的动作
            dc.DrawRoundedRectangle(brush, null,
                new Rect(rect.X + 11, rect.Y + 11, 10, 10), 1.5, 1.5);
        }
        else
        {
            Icons.Draw(dc, Icons.Close, rect, brush, IconSize);
        }
    }

    private IEnumerable<(Item Item, Rect Rect)> Layout(Rect panel)
    {
        double y = panel.Y + PadY;
        double cancelX = panel.Right - PadX - ButtonSize;

        if (_encoding)
        {
            yield return (Item.Cancel, new Rect(cancelX, y, ButtonSize, ButtonSize));
            yield break;
        }

        double stopX = cancelX - ButtonGap - ButtonSize;
        yield return (Item.Stop, new Rect(stopX, y, ButtonSize, ButtonSize));
        yield return (Item.Cancel, new Rect(cancelX, y, ButtonSize, ButtonSize));
    }

    private void DrawTip(DrawingContext dc, Rect panel, Item item)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = new FormattedText(
            item == Item.Stop ? "停止录制  Enter" : "取消  Esc",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 11.5, _palette.TipText, ppd);

        double w = text.Width + 16;
        double h = text.Height + 9;

        var anchor = Rect.Empty;
        foreach (var (candidate, rect) in Layout(panel))
            if (candidate == item) anchor = rect;
        if (anchor.IsEmpty) return;

        double x = Math.Clamp(anchor.X + (anchor.Width - w) / 2, 0, Math.Max(0, ActualWidth - w));

        double y = panel.Y - h - 7;
        if (y < 0) y = Math.Min(panel.Bottom + 7, Math.Max(0, ActualHeight - h));

        var bubble = new Rect(x, y, w, h);
        PanelChrome.DrawShadow(dc, bubble, 6, _palette);
        dc.DrawRoundedRectangle(_palette.TipFill, _palette.PanelBorder, bubble, 6, 6);
        dc.DrawText(text, new Point(x + 8, y + 4.5));
    }

    private FormattedText Text(string value, double size, FontWeight weight, Brush brush)
        => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatTime(TimeSpan elapsed)
    {
        int minutes = (int)elapsed.TotalMinutes;
        int seconds = elapsed.Seconds;
        int tenths = elapsed.Milliseconds / 100;
        return $"{minutes:00}:{seconds:00}.{tenths}";
    }

    // ---------------- 输入 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit == _hovered) return;

        _hovered = hit;
        InvalidateVisual();
        RaiseTip();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is null) return;

        _hovered = null;
        InvalidateVisual();
        RaiseTip();
    }

    private Item? HitTest(Point local)
    {
        var panel = new Rect(ShadowPad, ShadowPad, PanelWidth, PanelHeight);
        foreach (var (item, rect) in Layout(panel))
            if (rect.Contains(local)) return item;
        return null;
    }

    private void RaiseTip()
    {
        if (_hovered is not { } item)
        {
            TipChanged?.Invoke(Rect.Empty, string.Empty);
            return;
        }

        var panel = new Rect(ShadowPad, ShadowPad, PanelWidth, PanelHeight);
        foreach (var (candidate, rect) in Layout(panel))
        {
            if (candidate != item) continue;
            TipChanged?.Invoke(rect, item == Item.Stop ? "停止录制  Enter" : "取消  Esc");
            return;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit is null)
        {
            try { Window.GetWindow(this)?.DragMove(); }
            catch (InvalidOperationException) { /* 按键已经松开了，没什么可拖的 */ }
            return;
        }

        _pressed = hit;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_pressed is not { } item) return;

        _pressed = null;
        ReleaseMouseCapture();

        if (HitTest(e.GetPosition(this)) != item) return;

        if (item == Item.Stop) StopRequested?.Invoke();
        else CancelRequested?.Invoke();
    }
}
