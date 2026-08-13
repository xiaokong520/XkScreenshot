using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XkScreenshot.App.Ui;

/// <summary>提示的语气。图标和它的颜色跟着变，别的都一样。</summary>
public enum DialogKind
{
    /// <summary>事情办成了、或者只是知会一声。</summary>
    Info,

    /// <summary>要么是没办成，要么是接下来那一下有代价。</summary>
    Warning,
}

/// <summary>
/// 程序自己的提示框。
///
/// 为什么不用 MessageBox：它的按钮尺寸、字号、配色全归系统管，一个都改不了 ——
/// 在这个窗口越做越大的设置界面旁边，那两颗小按钮显得格外将就。而且它永远是浅色的，
/// 设置窗切到深色主题之后，弹一次白底就闪一下眼。
///
/// 只有「知会一声」和「问一句」两种形态，够用就行：这里不做输入框、不做第三个选项，
/// 那些东西一旦开了口子，提示框就会慢慢长成另一个设置界面。
/// </summary>
public static class Dialog
{
    /// <summary>按钮比系统那两颗大一圈：这是整个提示框上唯一要点的东西，不该最难点。</summary>
    private const double ButtonHeight = 38;
    private const double ButtonMinWidth = 112;
    private const double IconSize = 26;

    /// <summary>正文最宽排到这儿就折行。一句话拉成横贯屏幕的一条比折两行更难读。</summary>
    private const double TextMaxWidth = 400;

    /// <summary>知会一声，只有一颗「知道了」。</summary>
    public static void Notify(Window? owner, bool dark, string message, DialogKind kind = DialogKind.Info)
        => Show(owner, dark, message, kind, confirm: false);

    /// <summary>问一句，点了「确定」返回 true。</summary>
    public static bool Confirm(
        Window? owner, bool dark, string message, DialogKind kind = DialogKind.Warning)
        => Show(owner, dark, message, kind, confirm: true);

    private static bool Show(Window? owner, bool dark, string message, DialogKind kind, bool confirm)
    {
        bool ok = false;

        var window = new Window
        {
            Title = "XkScreenshot",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            // 提示框在任务栏上单独占一格是没有意义的：它是模态的，
            // 除了先把它点掉，从任务栏点回来也做不了别的
            ShowInTaskbar = false,
            MinWidth = 380,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 14,
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);

        // 主人还在的时候居中到它身上：屏幕正中往往离用户刚点的那颗按钮很远
        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Theme.Apply(window, dark);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XkScreenshot;component/Settings/SettingsTheme.xaml"),
        });
        window.SetResourceReference(Control.BackgroundProperty, "PageBg");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var accept = NewButton(confirm ? "确定" : "知道了", (Style)window.FindResource("AccentButton"));
        accept.IsDefault = true;
        accept.Click += (_, _) =>
        {
            ok = true;
            window.Close();
        };
        buttons.Children.Add(accept);

        if (confirm)
        {
            var cancel = NewButton("取消", null);
            cancel.Margin = new Thickness(10, 0, 0, 0);
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
        }
        else
        {
            // 只有一颗按钮时它也得认 Esc，否则这个框只能用鼠标关掉
            accept.IsCancel = true;
        }

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 16, 24, 16),
            Child = buttons,
        };
        footer.SetResourceReference(Border.BackgroundProperty, "FooterBg");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        Grid.SetRow(footer, 1);

        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(24, 24, 24, 24),
        };
        body.Children.Add(IconBox(kind));

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = TextMaxWidth,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 22,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(text);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(body);
        grid.Children.Add(footer);
        window.Content = grid;

        // 深色窗体配一条亮白标题栏是最扎眼的一种半吊子深色模式
        window.SourceInitialized += (_, _) => Theme.ApplyTitleBar(window, dark);

        window.ShowDialog();
        return ok;
    }

    private static Button NewButton(string text, Style? style)
    {
        var button = new Button
        {
            Content = text,
            Height = ButtonHeight,
            MinWidth = ButtonMinWidth,
        };
        if (style is not null) button.Style = style;
        return button;
    }

    /// <summary>
    /// 图标按 24×24 的设计尺寸画，再用 Viewbox 缩到目标大小 ——
    /// 线宽跟着一起缩，视觉重量才和界面上别处的图标一致。
    /// </summary>
    private static FrameworkElement IconBox(DialogKind kind)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = kind == DialogKind.Warning ? Icons.Alert : Icons.Info,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        };
        path.SetResourceReference(
            System.Windows.Shapes.Shape.StrokeProperty,
            kind == DialogKind.Warning ? "Warn" : "Accent");

        return new Viewbox
        {
            Child = path,
            Width = IconSize,
            Height = IconSize,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
