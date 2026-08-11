using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.App.Ui;

/// <summary>
/// 界面图标，取自 Lucide 图标集（MIT 协议，https://lucide.dev）。
///
/// 全部按矢量路径内嵌，不依赖任何图标字体。Segoe MDL2 / Fluent 那类系统字体在
/// 不同 Windows 版本上字形代号对不上，缺字时直接退化成豆腐块；而 Unicode 符号
/// （▭ ◯ ✎ 之类）在不同字体下粗细、基线、字面大小全不一致，排在一起很难看齐。
///
/// 原始设计尺寸是 24×24、线宽 2、圆头圆角。绘制时整体缩放到目标大小，
/// 线宽随之等比变细，视觉重量在任何尺寸下都保持一致。
/// </summary>
public static class Icons
{
    private const double DesignSize = 24.0;
    private const double DesignStroke = 2.0;

    /// <summary>选择工具 —— 不画任何东西，拖拽即平移选区。</summary>
    public static readonly Geometry Cursor = Build(
        paths: ["M4.037 4.688a.495.495 0 0 1 .651-.651l16 6.5a.5.5 0 0 1-.063.947l-6.124 1.58a2 2 0 0 0-1.438 1.435l-1.579 6.126a.5.5 0 0 1-.947.063z"]);

    /// <summary>矩形工具。</summary>
    public static readonly Geometry Square = Build(
        rects: [(3, 3, 18, 18, 2)]);

    /// <summary>椭圆工具。</summary>
    public static readonly Geometry Circle = Build(
        ellipses: [(12, 12, 10, 10)]);

    /// <summary>箭头工具。</summary>
    public static readonly Geometry ArrowUpRight = Build(
        paths: ["M13 5H19V11", "M19 5L5 19"]);

    /// <summary>画笔工具。</summary>
    public static readonly Geometry Pencil = Build(
        paths:
        [
            "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z",
            "m15 5 4 4",
        ]);

    /// <summary>文字工具。</summary>
    public static readonly Geometry Type = Build(
        paths: ["M12 4v16", "M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2", "M9 20h6"]);

    /// <summary>马赛克工具。</summary>
    public static readonly Geometry Grid = Build(
        rects: [(3, 3, 18, 18, 2)],
        paths: ["M3 9h18", "M3 15h18", "M9 3v18", "M15 3v18"]);

    public static readonly Geometry Undo = Build(
        paths: ["M9 14 4 9l5-5", "M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"]);

    public static readonly Geometry Redo = Build(
        paths: ["m15 14 5-5-5-5", "M20 9H9.5A5.5 5.5 0 0 0 4 14.5A5.5 5.5 0 0 0 9.5 20H13"]);

    public static readonly Geometry Pin = Build(
        paths:
        [
            "M12 17v5",
            "M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z",
        ]);

    public static readonly Geometry Copy = Build(
        rects: [(8, 8, 14, 14, 2)],
        paths: ["M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"]);

    public static readonly Geometry Save = Build(
        paths: ["M12 15V3", "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "m7 10 5 5 5-5"]);

    /// <summary>框选式马赛克：拖一个矩形，整块糊掉。</summary>
    public static readonly Geometry MosaicArea = Build(
        rects: [(3, 4, 18, 16, 2)],
        paths: ["M3 12h18", "M12 4v16"]);

    /// <summary>删除选中的标注。</summary>
    public static readonly Geometry Trash = Build(
        paths:
        [
            "M3 6h18",
            "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6",
            "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
            "M10 11v6",
            "M14 11v6",
        ]);

    public static readonly Geometry Close = Build(
        paths: ["M18 6 6 18", "m6 6 12 12"]);

    // ---------------- 设置界面 ----------------

    /// <summary>热键。用命令键的结环图案，比键盘轮廓在 20px 下更容易认。</summary>
    public static readonly Geometry Command = Build(
        paths: ["M15 6v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3"]);

    public static readonly Geometry Folder = Build(
        paths: ["M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"]);

    /// <summary>确认截图后的去向 —— 就是 Enter 那个折返箭头。</summary>
    public static readonly Geometry CornerDownLeft = Build(
        paths: ["M20 4v7a4 4 0 0 1-4 4H4", "m9 10-5 5 5 5"]);

    public static readonly Geometry Eye = Build(
        ellipses: [(12, 12, 3, 3)],
        paths: ["M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"]);

    /// <summary>盖住的眼睛 —— 「点一下就看得见」那颗按钮按下之后的样子。</summary>
    public static readonly Geometry EyeOff = Build(
        paths:
        [
            "M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49",
            "M14.084 14.158a3 3 0 0 1-4.242-4.242",
            "M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143",
            "m2 2 20 20",
        ]);

    public static readonly Geometry Power = Build(
        paths: ["M12 2v10", "M18.4 6.6a9 9 0 1 1-12.77.04"]);

    /// <summary>设置项标题后面那个感叹号，说明挂在它的悬停提示上。</summary>
    public static readonly Geometry Alert = Build(
        ellipses: [(12, 12, 10, 10)],
        paths: ["M12 8v4", "M12 16h.01"]);

    /// <summary>管理员权限。</summary>
    public static readonly Geometry Shield = Build(
        paths: ["M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"]);

    /// <summary>「通用」分类。</summary>
    public static readonly Geometry Sliders = Build(
        ellipses: [(7, 7, 3, 3), (17, 17, 3, 3)],
        paths: ["M20 7h-9", "M14 17H5"]);

    /// <summary>
    /// 主题颜色 —— 一块调色板配四个颜料点。
    ///
    /// 那四个点在原图里是实心圆，这里画成半径 0.5 的描边圆：线宽 2 一描，
    /// 外径正好三个单位，和实心点看起来是一回事，还省得给 Build 加一条填充路径。
    /// </summary>
    public static readonly Geometry Palette = Build(
        ellipses: [(13.5, 6.5, 0.5, 0.5), (17.5, 10.5, 0.5, 0.5),
                   (8.5, 7.5, 0.5, 0.5), (6.5, 12.5, 0.5, 0.5)],
        paths: ["M12 22a1 1 0 0 1 0-20 10 9 0 0 1 10 9 5 5 0 0 1-5 5h-2.25a1.75 1.75 0 0 0-1.4 2.8l.3.4a1.75 1.75 0 0 1-1.4 2.8z"]);

    /// <summary>「截图」分类。</summary>
    public static readonly Geometry Crop = Build(
        paths: ["M6 2v14a2 2 0 0 0 2 2h14", "M18 22V8a2 2 0 0 0-2-2H2"]);

    /// <summary>截屏区域历史：表盘配一支逆时针回拨的箭头。</summary>
    public static readonly Geometry History = Build(
        paths:
        [
            "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8",
            "M3 3v5h5",
            "M12 7v5l4 2",
        ]);

    public static readonly Geometry Camera = Build(
        ellipses: [(12, 13, 3, 3)],
        paths: ["M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3l-2.5-3z"]);

    /// <summary>长截图：箭头朝下，底边一道横线表示「拼到底」。</summary>
    public static readonly Geometry Scroll = Build(
        paths: ["M12 2v14", "m7 11 5 5 5-5", "M5 21h14"]);

    /// <summary>文字识别 —— 扫描线 + 文本。</summary>
    public static readonly Geometry ScanLine = Build(
        paths: ["M3 7V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v2",
                "M3 17v2a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2",
                "M7 12h10"]);

    /// <summary>
    /// 翻译 —— 左上一个「文」字，右下一个拉丁字母 A，翻译类图标的通行画法。
    ///
    /// 路径按 Lucide 的 languages 原样抄，别照着感觉改：这几笔任意一笔挪了位置，
    /// 出来就既不像「文」也不像 A —— 之前那版就是这样，24 像素下看着像「VTV」。
    /// </summary>
    public static readonly Geometry Languages = Build(
        paths: ["m5 8 6 6", "m4 14 6-6 2-3", "M2 5h12", "M7 2h1",
                "m22 22-5-10-5 10", "M14 18h6"]);

    /// <summary>OCR —— 四角取景框圈住几行文字，比 <see cref="ScanLine"/> 更明确是「认字」。</summary>
    public static readonly Geometry ScanText = Build(
        paths: ["M3 7V5a2 2 0 0 1 2-2h2", "M17 3h2a2 2 0 0 1 2 2v2",
                "M21 17v2a2 2 0 0 1-2 2h-2", "M7 21H5a2 2 0 0 1-2-2v-2",
                "M7 8h8", "M7 12h10", "M7 16h6"]);

    /// <summary>API 协议 —— 一对花括号，报文长什么样一眼就知道。</summary>
    public static readonly Geometry Braces = Build(
        paths: ["M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5c0 1.1.9 2 2 2h1",
                "M16 21h1a2 2 0 0 0 2-2v-5c0-1.1.9-2 2-2a2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1"]);

    /// <summary>API 地址 —— 链条。填的是 URL 而不是本机路径，所以不该用文件夹。</summary>
    public static readonly Geometry Link = Build(
        paths: ["M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71",
                "M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"]);

    /// <summary>API Key —— 钥匙。</summary>
    public static readonly Geometry Key = Build(
        ellipses: [(7.5, 15.5, 5.5, 5.5)],
        paths: ["m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4",
                "m21 2-9.6 9.6"]);

    /// <summary>大模型 —— 机器人。</summary>
    public static readonly Geometry Bot = Build(
        rects: [(4, 8, 16, 12, 2)],
        paths: ["M12 8V4H8", "M2 14h2", "M20 14h2", "M15 13v2", "M9 13v2"]);

    /// <summary>可下载的模型 —— 一个包裹。</summary>
    public static readonly Geometry Package = Build(
        paths:
        [
            "m7.5 4.27 9 5.15",
            "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z",
            "m3.3 7 8.7 5 8.7-5",
            "M12 22V12",
        ]);

    /// <summary>OCR 语言包 —— 一个 A 加一枚勾，「这些字认得了」。</summary>
    public static readonly Geometry SpellCheck = Build(
        paths: ["m6 16 6-12 6 12", "M8 12h8", "m16 20 2 2 4-4"]);

    /// <summary>互译方向 —— 一来一回两支箭头。语言包是按「X ↔ 英语」成对装的。</summary>
    public static readonly Geometry ArrowRightLeft = Build(
        paths: ["m16 3 4 4-4 4", "M20 7H4", "m8 21-4-4 4-4", "M4 17h16"]);

    /// <summary>高度上限 —— 上下双向箭头。</summary>
    public static readonly Geometry MoveVertical = Build(
        paths: ["M12 2v20", "m8 18 4 4 4-4", "m8 6 4-4 4 4"]);

    private static readonly Dictionary<Brush, Pen> PenCache = [];

    /// <summary>
    /// 把图标画在 box 中央。size 是图标边长（DIP），线宽随之等比缩放。
    /// </summary>
    public static void Draw(DrawingContext dc, Geometry icon, Rect box, Brush brush, double size)
    {
        double scale = size / DesignSize;
        double x = box.X + (box.Width - size) / 2;
        double y = box.Y + (box.Height - size) / 2;

        // 变换会同时作用在几何与画笔上，所以这里用设计线宽即可，
        // 缩放后自然得到 DesignStroke * scale 的实际线宽。
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, GetPen(brush), icon);
        dc.Pop();
        dc.Pop();
    }

    private static Pen GetPen(Brush brush)
    {
        if (PenCache.TryGetValue(brush, out var cached)) return cached;

        var pen = new Pen(brush, DesignStroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        PenCache[brush] = pen;
        return pen;
    }

    private static Geometry Build(
        (double X, double Y, double W, double H, double R)[]? rects = null,
        (double Cx, double Cy, double Rx, double Ry)[]? ellipses = null,
        string[]? paths = null)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var r in rects ?? [])
            group.Children.Add(new RectangleGeometry(new Rect(r.X, r.Y, r.W, r.H), r.R, r.R));

        foreach (var e in ellipses ?? [])
            group.Children.Add(new EllipseGeometry(new Point(e.Cx, e.Cy), e.Rx, e.Ry));

        // Geometry.Parse 用的是不变区域性解析数字，中文环境下小数点不会出问题
        foreach (var d in paths ?? [])
            group.Children.Add(Geometry.Parse(d));

        group.Freeze();
        return group;
    }
}
