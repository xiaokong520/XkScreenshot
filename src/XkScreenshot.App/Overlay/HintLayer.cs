using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 快捷键提示面板。只在光标所在那块屏上显示，H 键开关。
/// 单独一层是因为它几乎不变，不该跟着放大镜每帧重绘。
/// </summary>
public sealed class HintLayer : FrameworkElement
{
    /// <summary>一行提示：若干个键帽 + 一句说明。Plus 为真时键帽之间画「+」表示组合键。</summary>
    private sealed record Row(string[] Keys, bool Plus, string Action);

    private static readonly Row[] Rows =
    [
        new(["拖拽"], false, "框选区域"),
        new(["单击"], false, "选中整个窗口"),
        new(["Tab"], false, "切换整窗 / 控件级检测"),
        new(["W", "A", "S", "D"], false, "光标移动 1 像素"),
        new(["←", "↑", "↓", "→"], false, "移动选区 / 选中的标注"),
        new(["滚轮"], false, "调粗细 / 字号 / 马赛克粒度"),
        new(["Delete"], false, "删除选中的标注"),
        new(["Ctrl", "A"], true, "选中整屏 / 整个桌面"),
        new([",", "."], false, "回溯截屏区域历史"),
        new(["C"], false, "复制颜色值"),
        new(["Shift"], false, "切换 RGB / HEX"),
        new(["Enter"], false, "确认截图，双击选区亦可"),
        new(["Esc"], false, "逐级返回：标注 → 工具 → 重选"),
        new(["H"], false, "隐藏本面板"),
    ];

    private const string PanelTitle = "快捷键";

    private const double KeyFontSize = 11.5;
    private const double ActionFontSize = 12.5;
    private const double TitleFontSize = 11;

    private const double ChipHeight = 20;
    private const double ChipMinWidth = 22;
    private const double ChipPadX = 7;
    private const double ChipGap = 4;
    private const double PlusWidth = 11;
    private const double ColumnGap = 18;
    private const double RowHeight = 26;
    private const double PadX = 16;
    private const double PadY = 13;
    private const double TitleBlock = 25;
    private const double EdgeInset = 24;
    private const double CornerRadius = 9;

    private static readonly Brush ChipBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen ChipBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen ChipShade = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), 1));
    private static readonly Brush KeyTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)));
    private static readonly Brush ActionTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xBA, 0xC4)));
    private static readonly Brush TitleTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x82)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF)));
    private static readonly Pen RulePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1));

    private static readonly FontFamily Family = new("Microsoft YaHei UI");
    private static readonly Typeface KeyFace = new(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ActionFace = new(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>排版结果只跟 DPI 有关，内容是固定的，量一次就够。</summary>
    private Layout? _layout;

    public bool Visible { get; set; }

    /// <summary>毛玻璃背景。为 null 时面板退回不透明底色。</summary>
    public FrostedBackdrop? Backdrop { get; set; }

    public HintLayer() => IsHitTestVisible = false;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (!Visible) return;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var layout = _layout is { } cached && Math.Abs(cached.PixelsPerDip - ppd) < 0.001
            ? cached
            : _layout = Layout.Measure(ppd);

        double x = EdgeInset;
        double y = Math.Max(EdgeInset, ActualHeight - layout.Height - EdgeInset);
        var panel = new Rect(x, y, layout.Width, layout.Height);

        PanelChrome.DrawGlassPanel(dc, panel, CornerRadius, Backdrop, new Size(ActualWidth, ActualHeight));

        // 标题：一个强调色小方块 + 淡灰标题 + 一条发丝分隔线，给面板一个视觉锚点
        double titleY = panel.Y + PadY;
        dc.DrawRectangle(AccentBrush, null, new Rect(panel.X + PadX, titleY + 3.5, 3, layout.Title.Height - 7));
        dc.DrawText(layout.Title, new Point(panel.X + PadX + 9, titleY));

        double ruleY = Math.Round(titleY + TitleBlock - 6) + 0.5;
        dc.DrawLine(RulePen, new Point(panel.X + PadX, ruleY), new Point(panel.Right - PadX, ruleY));

        for (int i = 0; i < Rows.Length; i++)
        {
            var measured = layout.RowsMeasured[i];
            double rowTop = panel.Y + PadY + TitleBlock + i * RowHeight;
            double chipTop = rowTop + (RowHeight - ChipHeight) / 2;

            double chipX = panel.X + PadX;
            for (int k = 0; k < measured.Keys.Length; k++)
            {
                var text = measured.Keys[k];
                double w = measured.KeyWidths[k];
                DrawChip(dc, new Rect(chipX, chipTop, w, ChipHeight), text, measured.KeyInk[k]);
                chipX += w;

                if (k == measured.Keys.Length - 1) break;

                if (Rows[i].Plus)
                {
                    var plus = measured.Plus!;
                    dc.DrawText(plus, new Point(
                        chipX + (PlusWidth - plus.Width) / 2,
                        chipTop + (ChipHeight - plus.Height) / 2));
                    chipX += PlusWidth;
                }
                else
                {
                    chipX += ChipGap;
                }
            }

            var action = measured.Action;
            dc.DrawText(action, new Point(
                panel.X + PadX + layout.KeyColumnWidth + ColumnGap,
                rowTop + (RowHeight - action.Height) / 2));
        }
    }

    /// <summary>
    /// 画一个键帽。
    ///
    /// 字按**墨迹**居中，不是按整行盒 —— 行盒含上伸部和下伸部，而键帽上多半只有
    /// 大写字母那么高的一块。按行盒居中的话，逗号、句点这种贴着基线的字形会缩到
    /// 键帽左下角，看起来就是个空白键帽（这一层的字才十来像素，差几像素就认不出了）。
    /// </summary>
    private static void DrawChip(DrawingContext dc, Rect rect, FormattedText label, Rect ink)
    {
        dc.DrawRoundedRectangle(ChipBrush, ChipBorder, rect, 4, 4);

        // 底边加一道暗线，键帽才有厚度感，不然就是个扁平的框
        double bottom = rect.Bottom - 0.5;
        dc.DrawLine(ChipShade, new Point(rect.Left + 3, bottom), new Point(rect.Right - 3, bottom));

        dc.DrawText(label, new Point(
            rect.X + (rect.Width - ink.Width) / 2 - ink.X,
            rect.Y + (rect.Height - ink.Height) / 2 - ink.Y));
    }

    private sealed class Layout
    {
        public required double PixelsPerDip { get; init; }
        public required FormattedText Title { get; init; }
        public required MeasuredRow[] RowsMeasured { get; init; }
        public required double KeyColumnWidth { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }

        public static Layout Measure(double ppd)
        {
            FormattedText Make(string s, Typeface face, double size, Brush brush) => new(
                s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, ppd);

            var measured = Rows.Select(r =>
            {
                var keys = r.Keys.Select(k => Make(k, KeyFace, KeyFontSize, KeyTextBrush)).ToArray();
                var widths = keys.Select(t => Math.Max(ChipMinWidth, Math.Ceiling(t.Width) + ChipPadX * 2)).ToArray();
                double total = widths.Sum() + (keys.Length - 1) * (r.Plus ? PlusWidth : ChipGap);

                return new MeasuredRow
                {
                    Keys = keys,
                    KeyWidths = widths,
                    KeyInk = keys.Select(InkBounds).ToArray(),
                    TotalKeyWidth = total,
                    Plus = r.Plus ? Make("+", ActionFace, KeyFontSize, TitleTextBrush) : null,
                    Action = Make(r.Action, ActionFace, ActionFontSize, ActionTextBrush),
                };
            }).ToArray();

            double keyColumn = measured.Max(m => m.TotalKeyWidth);
            double actionColumn = measured.Max(m => m.Action.Width);
            var title = Make(PanelTitle, ActionFace, TitleFontSize, TitleTextBrush);

            return new Layout
            {
                PixelsPerDip = ppd,
                Title = title,
                RowsMeasured = measured,
                KeyColumnWidth = keyColumn,
                Width = PadX * 2 + keyColumn + ColumnGap + actionColumn,
                Height = PadY * 2 + TitleBlock + RowHeight * Rows.Length,
            };
        }
    }

    /// <summary>
    /// 字形实际占的那一块（相对于绘制原点）。取不到就退回整行盒，
    /// 那正是它原来的样子 —— 拿不准的时候不要比原来更糟。
    /// </summary>
    private static Rect InkBounds(FormattedText text)
    {
        var bounds = text.BuildGeometry(new Point(0, 0))?.Bounds ?? Rect.Empty;
        return bounds.IsEmpty || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height)
            ? new Rect(0, 0, text.Width, text.Height)
            : bounds;
    }

    private sealed class MeasuredRow
    {
        public required FormattedText[] Keys { get; init; }
        public required double[] KeyWidths { get; init; }
        public required Rect[] KeyInk { get; init; }
        public required double TotalKeyWidth { get; init; }
        public required FormattedText? Plus { get; init; }
        public required FormattedText Action { get; init; }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
