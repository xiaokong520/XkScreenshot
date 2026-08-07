using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 设置界面。
///
/// 版式照 Windows 11 设置页那套「卡片行」：一张卡 = 一个设置项，
/// 左边图标 + 标题 + 一句说明，右边就是那个控件。比「标签 : 控件」的表格版式好在，
/// 每一项的说明就贴在它自己身上，眼睛不用在标签列和说明行之间来回找对应关系。
/// 具体尺寸取自 WinUI 的 SettingsCard：最小高 68、内边距 16、图标 20 且右留 20、说明 12px。
///
/// 结构在这儿搭、皮肤在 SettingsTheme.xaml 里：前者是循环和条件，后者是带触发器的元素树，
/// 各自用最顺手的那种写法。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>WinUI SettingsCard 的规格，照抄免得自己拍脑袋。</summary>
    private const double CardMinHeight = 68;
    private const double CardPadding = 16;
    private const double CardGap = 4;
    private const double IconSize = 20;
    private const double IconGap = 20;
    private const double ContentGap = 24;
    private const double PagePadding = 24;

    private static readonly (CaptureAction Action, string Label)[] Actions =
    [
        (CaptureAction.Copy, "复制到剪贴板"),
        (CaptureAction.Pin, "贴到屏幕上"),
        (CaptureAction.Save, "保存为文件"),
    ];

    private readonly AppSettings _draft;
    private readonly bool _dark;

    private readonly HotkeyBox _hotkey = new() { Width = 132 };
    private readonly TextBox _directory = new();
    private readonly TextBox _prefix = new() { Width = 200 };
    private readonly ComboBox _defaultAction = new() { Width = 168 };
    private readonly ToggleButton _saveWithoutPrompt = new();
    private readonly ToggleButton _showHints = new();
    private readonly ToggleButton _elementMode = new();
    private readonly ToggleButton _runAtStartup = new();

    /// <summary>用户点了「确定」才有值，取消时保持 null。</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        _draft = current.Clone();
        _dark = Theme.IsSystemDark();

        Title = "XkScreenshot 设置";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        // 小屏上不能顶出工作区，超了就交给里面的滚动条
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 托盘程序没有主窗口，设置窗被别的窗口盖住之后，任务栏是唯一能把它找回来的地方
        ShowInTaskbar = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // 中文字形交给雅黑，拉丁字母和数字走 Segoe：混排时后者的字重和字宽明显更匀。
        // 不写 Segoe UI Variable：WPF 不支持可变字体，只会挑一个固定实例，白担一层不确定性。
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;

        // 皮肤要在搭界面之前就位：下面到处都在 FindResource 取画刷
        Theme.Apply(this, _dark);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XkScreenshot;component/Settings/SettingsTheme.xaml"),
        });

        foreach (var (_, label) in Actions) _defaultAction.Items.Add(label);
        foreach (var toggle in new[] { _saveWithoutPrompt, _showHints, _elementMode, _runAtStartup })
            toggle.Style = (Style)FindResource("ToggleSwitch");

        Background = Brush("PageBg");
        Content = BuildLayout();
        LoadFrom(_draft);

        // 深色窗体配一条亮白标题栏是最扎眼的一种半吊子深色模式，但要等窗口有了句柄才能改
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this, _dark);
    }

    private UIElement BuildLayout()
    {
        var page = new StackPanel();

        page.Children.Add(Section("热键", first: true));
        page.Children.Add(Card(Icons.Command, "开始截图",
            "点进输入框后按下想用的组合键。",
            Line(_hotkey, Button("恢复默认", () => _hotkey.Value = HotkeySpec.CaptureDefault))));

        page.Children.Add(Section("保存"));
        page.Children.Add(StackedCard(Icons.Folder, "默认目录",
            "留空则用系统「图片」文件夹。",
            Fill(_directory, Button("浏览…", BrowseDirectory))));
        page.Children.Add(Card(Icons.Type, "文件名前缀",
            "形如 前缀_20260807_142530.png。", _prefix));
        page.Children.Add(Card(Icons.Save, "保存时不弹对话框",
            "直接存进上面的目录，重名自动加序号。", _saveWithoutPrompt));

        page.Children.Add(Section("默认行为"));
        page.Children.Add(Card(Icons.CornerDownLeft, "确认截图后",
            "指按 Enter 或双击选区，工具条上的按钮不受影响。",
            _defaultAction));
        page.Children.Add(Card(Icons.Eye, "显示快捷键提示面板",
            "截图中按 H 也能开关。", _showHints));
        page.Children.Add(Card(Icons.Cursor, "默认用控件级检测",
            "截图中按 Tab 在整窗与控件级之间切换。", _elementMode));
        page.Children.Add(Card(Icons.Power, "开机自动启动", null, _runAtStartup));

        var scroll = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(PagePadding, 4, PagePadding - 8, PagePadding),
            Focusable = false,
        };

        var ok = Button("确定", Commit, (Style)FindResource("AccentButton"));
        ok.IsDefault = true;
        ok.MinWidth = 92;
        var cancel = Button("取消", Close);
        cancel.IsCancel = true;
        cancel.MinWidth = 92;

        var footer = new Border
        {
            Background = Brush("FooterBg"),
            BorderBrush = Brush("CardBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(PagePadding, 14, PagePadding, 14),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { ok, cancel },
            },
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        return root;
    }

    // ---------------- 卡片 ----------------

    /// <summary>一张设置卡：图标 + 标题 + 说明在左，控件贴右。</summary>
    private Border Card(Geometry icon, string title, string? description, UIElement control)
    {
        var grid = NewCardGrid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = TitleBlock(title, description);
        var host = Center(control);
        host.Margin = new Thickness(ContentGap, 0, 0, 0);
        Grid.SetColumn(host, 2);

        grid.Children.Add(IconBox(icon));
        grid.Children.Add(text);
        grid.Children.Add(host);
        return Shell(grid);
    }

    /// <summary>控件另起一行的卡片。给那些一行放不下的控件用（比如路径框 + 浏览按钮）。</summary>
    private Border StackedCard(Geometry icon, string title, string? description, UIElement control)
    {
        var grid = NewCardGrid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = TitleBlock(title, description);
        text.VerticalAlignment = VerticalAlignment.Top;

        control.SetValue(MarginProperty, new Thickness(0, 12, 0, 0));
        Grid.SetColumn(control, 1);
        Grid.SetRow(control, 1);

        var box = IconBox(icon);
        box.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRowSpan(box, 2);

        grid.Children.Add(box);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return Shell(grid);
    }

    private static Grid NewCardGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private Border Shell(UIElement content) => new()
    {
        Background = Brush("CardBg"),
        BorderBrush = Brush("CardBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        MinHeight = CardMinHeight,
        Padding = new Thickness(CardPadding),
        Margin = new Thickness(0, 0, 0, CardGap),
        SnapsToDevicePixels = true,
        Child = content,
    };

    /// <summary>
    /// 图标按 24×24 的设计尺寸画，再用 Viewbox 整体缩到 20 ——
    /// 线宽跟着一起缩，视觉重量才和覆盖层上那套图标一致。
    /// </summary>
    private FrameworkElement IconBox(Geometry icon)
    {
        // 全名限定：System.IO.Path 也在场，短名是歧义的
        var path = new System.Windows.Shapes.Path
        {
            Data = icon,
            Stroke = Brush("TextSecondary"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        };

        return new Viewbox
        {
            Child = path,
            Width = IconSize,
            Height = IconSize,
            Margin = new Thickness(2, 0, IconGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private FrameworkElement TitleBlock(string title, string? description)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush("Text"),
            TextWrapping = TextWrapping.Wrap,
        });

        if (description is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = Brush("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        Grid.SetColumn(stack, 1);
        return stack;
    }

    private static TextBlock Section(string title, bool first = false) => new()
    {
        Text = title,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, first ? 12 : 22, 0, 8),
    };

    // ---------------- 小零件 ----------------

    private Brush Brush(string key) => (Brush)FindResource(key);

    /// <summary>一排控件靠左排开。</summary>
    private static UIElement Line(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>第一个控件吃掉剩余宽度，后面的贴在右边。</summary>
    private static UIElement Fill(UIElement fill, params UIElement[] trailing)
    {
        var panel = new DockPanel { LastChildFill = true };

        // 右靠的先加：DockPanel 按加入顺序从右边界往里排，最后一个孩子才拿到剩下的空间
        foreach (var item in trailing)
        {
            DockPanel.SetDock(item, Dock.Right);
            panel.Children.Add(item);
        }
        panel.Children.Add(fill);
        return panel;
    }

    /// <summary>把控件包一层，让它在卡片里垂直居中而不被拉伸。</summary>
    private static FrameworkElement Center(UIElement control)
    {
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.HorizontalAlignment = HorizontalAlignment.Right;
            return fe;
        }

        return new ContentControl { Content = control, VerticalAlignment = VerticalAlignment.Center };
    }

    private Button Button(string text, Action onClick, Style? style = null)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (style is not null) button.Style = style;
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------- 读写 ----------------

    private void LoadFrom(AppSettings s)
    {
        _hotkey.Value = s.CaptureHotkey;
        _directory.Text = s.SaveDirectory;
        _prefix.Text = s.FileNamePrefix;
        _saveWithoutPrompt.IsChecked = s.SaveWithoutPrompt;
        _showHints.IsChecked = s.ShowHints;
        _elementMode.IsChecked = s.ElementMode;
        _runAtStartup.IsChecked = s.RunAtStartup;

        int index = Array.FindIndex(Actions, a => a.Action == s.DefaultAction);
        _defaultAction.SelectedIndex = index < 0 ? 0 : index;
    }

    private void BrowseDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认保存目录",
            InitialDirectory = Directory.Exists(_directory.Text) ? _directory.Text : null,
        };

        if (dialog.ShowDialog(this) == true) _directory.Text = dialog.FolderName;
    }

    private void Commit()
    {
        string directory = _directory.Text.Trim();
        if (directory.Length > 0 && !Directory.Exists(directory))
        {
            // 目录不存在就当场拦下。等到真去保存时才发现，那张截图往往已经没了
            MessageBox.Show(this, "保存目录不存在：" + directory, "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.CaptureHotkey = _hotkey.Value;
        _draft.SaveDirectory = directory;
        _draft.FileNamePrefix = _prefix.Text.Trim();
        _draft.SaveWithoutPrompt = _saveWithoutPrompt.IsChecked == true;
        _draft.DefaultAction = Actions[Math.Max(0, _defaultAction.SelectedIndex)].Action;
        _draft.ShowHints = _showHints.IsChecked == true;
        _draft.ElementMode = _elementMode.IsChecked == true;
        _draft.RunAtStartup = _runAtStartup.IsChecked == true;

        Result = _draft;
        Close();
    }
}
