using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 快捷键提示面板。只在光标所在那块屏上显示，且只在选区尚未确定时显示 ——
/// 选区定下来之后用户的注意力应该在选区上，面板就该让位。
/// 单独一层是因为它几乎不变，不该跟着放大镜每帧重绘。
/// </summary>
public sealed class HintLayer : FrameworkElement
{
    private static readonly (string Key, string Action)[] Entries =
    [
        ("拖拽", "框选区域"),
        ("单击", "选中整个窗口"),
        ("W A S D", "光标移动 1 像素"),
        ("方向键", "移动选区（Shift 加速）"),
        ("Ctrl + A", "选中整屏 / 整个桌面"),
        ("C", "复制颜色值"),
        ("Shift", "切换 RGB / HEX"),
        ("Enter / 双击", "确认截图"),
        ("Esc", "重选 / 退出"),
        ("H", "显示 / 隐藏本面板"),
    ];

    private const double FontSize = 12;
    private const double LineHeight = 19;
    private const double Padding = 12;
    private const double ColumnGap = 14;
    private const double EdgeInset = 24;

    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x14, 0x14)));
    private static readonly Brush KeyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xC0, 0xFF)));
    private static readonly Brush ActionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)));
    private static readonly Pen PanelBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Typeface Face = new("Microsoft YaHei UI");

    public bool Visible { get; set; }

    public HintLayer() => IsHitTestVisible = false;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (!Visible) return;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, Brush brush) => new(
            s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, brush, ppd);

        var keys = Entries.Select(e => Text(e.Key, KeyBrush)).ToArray();
        var actions = Entries.Select(e => Text(e.Action, ActionBrush)).ToArray();

        double keyColumn = keys.Max(t => t.Width);
        double actionColumn = actions.Max(t => t.Width);
        double w = Padding * 2 + keyColumn + ColumnGap + actionColumn;
        double h = Padding * 2 + LineHeight * Entries.Length;

        // 贴左下角。放大镜跟着光标走且默认在光标右下，两者撞上的概率最低。
        double x = EdgeInset;
        double y = ActualHeight - h - EdgeInset;
        if (y < EdgeInset) y = EdgeInset;

        dc.DrawRoundedRectangle(PanelBrush, PanelBorder, new Rect(x, y, w, h), 5, 5);

        for (int i = 0; i < Entries.Length; i++)
        {
            double lineY = y + Padding + i * LineHeight;
            // 键名右对齐，视线扫下来是一条直线
            dc.DrawText(keys[i], new Point(x + Padding + keyColumn - keys[i].Width, lineY));
            dc.DrawText(actions[i], new Point(x + Padding + keyColumn + ColumnGap, lineY));
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
