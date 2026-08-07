using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;
using XkScreenshot.Core.Hotkeys;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 设置界面。
///
/// 左边一列分类，右边是那一类的设置项 —— 照 Windows 11 设置那套。
/// 不把所有项摊在一页上：光是 M1 就已经八项，M2~M4 的长截图、文字识别、翻译各自还要一组，
/// 摊平的话窗口会长到需要滚动，而滚动着找设置是最烦人的一种找法。
/// 分类往左边那一列里加就行，版式不用动。
///
/// 每一项是一张卡：左边图标 + 标题 + 一句说明，右边就是那个控件。比「标签 : 控件」的表格
/// 好在，说明贴在它自己身上，眼睛不用在标签列和说明行之间来回找对应关系。
/// 尺寸取自 WinUI 的 SettingsCard：最小高 68、内边距 16、图标 20 且右留 20、说明 12px。
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
    private const double NavWidth = 176;

    private static readonly (CaptureAction Action, string Label)[] Actions =
    [
        (CaptureAction.Copy, "复制到剪贴板"),
        (CaptureAction.Pin, "贴到屏幕上"),
        (CaptureAction.Save, "保存为文件"),
    ];

    private readonly AppSettings _draft;
    private readonly bool _dark;

    /// <summary>问「这个组合键是不是本程序自己占着的」。见 <see cref="IsTakenByOthers"/>。</summary>
    private readonly Func<HotkeySpec, bool> _heldBySelf;

    private readonly HotkeyBox _captureHotkey = new() { Width = 132 };
    private readonly HotkeyBox _pinHotkey = new() { Width = 132 };
    private readonly TextBlock _captureStatus = new();
    private readonly TextBlock _pinStatus = new();

    /// <summary>探热键占用的那个探针。窗口一关就撤，别一直占着一个消息窗口。</summary>
    private readonly HotkeyProbe _probe = new();

    /// <summary>上一次通知出去的录制状态，用来只在真正变化时才发事件。</summary>
    private bool _recording;

    private readonly TextBox _directory = new();
    private readonly TextBox _prefix = new() { Width = 200 };
    private readonly TextBox _historyCapacity = new() { Width = 64, MaxLength = 3 };
    private readonly ComboBox _defaultAction = new() { Width = 168 };
    private readonly ToggleButton _saveWithoutPrompt = new();
    private readonly ToggleButton _showHints = new();
    private readonly ToggleButton _elementMode = new();
    private readonly ToggleButton _runAtStartup = new();

    private readonly StackPanel _nav = new();
    private readonly ContentControl _pageHost = new();

    /// <summary>用户点了「确定」才有值，取消时保持 null。</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>
    /// 正在录热键 —— 这期间本程序必须让出全部热键，否则用户想把某个键录进框里时，
    /// 那个键会被自己的全局热键截走，当场触发一次截图而不是被记下来。
    /// </summary>
    public event Action<bool>? RecordingChanged;

    /// <param name="heldBySelf">判断某个组合键是不是本程序自己正占着的。</param>
    public SettingsWindow(AppSettings current, Func<HotkeySpec, bool> heldBySelf)
    {
        _draft = current.Clone();
        _heldBySelf = heldBySelf;
        _dark = Theme.IsSystemDark();

        Title = "XkScreenshot 设置";
        Width = 720;
        // 定高而不是随内容伸缩：切换分类时窗口跟着一页一页地变高变矮，比任何滚动条都晃眼。
        // 这个高度要能装下最长的那一页（此刻是「截图」的四张卡）—— 定高的意义在于不用滚，
        // 装不下就退化成「既不能自适应、又还是要滚」，两头都不讨好。加分类时记得重新对一下。
        Height = Math.Min(540, Math.Max(360, SystemParameters.WorkArea.Height - 80));
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

        // 只让数字敲进去，省掉一个「请输入数字」的弹窗。粘贴绕得过去，
        // 所以 Commit 那边照样得解析一次兜底。
        _historyCapacity.PreviewTextInput += (_, e) =>
        {
            foreach (char c in e.Text)
            {
                if (char.IsAsciiDigit(c)) continue;
                e.Handled = true;
                return;
            }
        };

        foreach (var (_, label) in Actions) _defaultAction.Items.Add(label);
        foreach (var toggle in new[] { _saveWithoutPrompt, _showHints, _elementMode, _runAtStartup })
            toggle.Style = (Style)FindResource("ToggleSwitch");

        Background = Brush("PageBg");
        Content = BuildLayout();
        BuildPages();
        LoadFrom(_draft);

        foreach (var box in new[] { _captureHotkey, _pinHotkey })
        {
            box.ValueChanged += RefreshHotkeyStatus;
            box.GotKeyboardFocus += (_, _) => UpdateRecording();
            box.LostKeyboardFocus += (_, _) => UpdateRecording();
        }

        // 窗口切走切回也要重算：人都去用别的程序了，热键就该是活的
        Activated += (_, _) => UpdateRecording();
        Deactivated += (_, _) => UpdateRecording();

        // 点在别处就把热键框的焦点收走。WPF 里点空白区域并不会让 TextBox 失焦，
        // 而框一直握着焦点就意味着热键一直让在外面 —— 用户录完键把窗口晾在那儿，
        // 热键就再也不响应了，这正是「打开设置就没热键」的另一种形态。
        PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideHotkeyBox(source)) return;
            if (_captureHotkey.IsKeyboardFocusWithin || _pinHotkey.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
        };

        RefreshHotkeyStatus();

        // 深色窗体配一条亮白标题栏是最扎眼的一种半吊子深色模式，但要等窗口有了句柄才能改
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this, _dark);
        Closed += (_, _) => _probe.Dispose();
    }

    private UIElement BuildLayout()
    {
        var nav = new Border
        {
            Width = NavWidth,
            Background = Brush("NavBg"),
            BorderBrush = Brush("CardBorder"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 14, 10, 14),
            Child = _nav,
        };

        var scroll = new ScrollViewer
        {
            Content = _pageHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(PagePadding, PagePadding - 6, PagePadding - 8, PagePadding),
            Focusable = false,
        };
        Grid.SetColumn(scroll, 1);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(nav);
        body.Children.Add(scroll);

        var ok = Button("确定", Commit, (Style)FindResource("AccentButton"));
        ok.IsDefault = true;
        ok.MinWidth = 92;
        var cancel = Button("取消", Close);
        cancel.IsCancel = true;
        cancel.MinWidth = 92;

        // 页脚横跨整个窗口而不是只占右半边：确定/取消 管的是整份设置，不是当前这一页
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
        Grid.SetRow(footer, 1);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(body);
        root.Children.Add(footer);
        return root;
    }

    private void BuildPages()
    {
        AddPage(Icons.Sliders, "通用",
            Card(Icons.Power, "开机自动启动",
                "登录 Windows 后自动常驻托盘。", _runAtStartup));

        AddPage(Icons.Command, "热键",
            HotkeyCard(Icons.Camera, "开始截图",
                "点进输入框后按下组合键，Delete 清除。",
                _captureHotkey, _captureStatus, HotkeySpec.CaptureDefault),
            HotkeyCard(Icons.Pin, "贴图",
                "把剪贴板里的图钉到屏幕上；剪贴板里没有图就钉上一次截的那张。",
                _pinHotkey, _pinStatus, HotkeySpec.PinDefault));

        AddPage(Icons.Crop, "截图",
            Card(Icons.CornerDownLeft, "确认截图后",
                "指按 Enter 或双击选区，工具条上的按钮不受影响。", _defaultAction),
            Card(Icons.Eye, "显示快捷键提示面板",
                "截图中按 H 也能开关。", _showHints),
            Card(Icons.Cursor, "默认用控件级检测",
                "截图中按 Tab 在整窗与控件级之间切换。", _elementMode),
            Card(Icons.History, "记住多少条截屏区域",
                "截图中按 [ 和 ] 回溯用过的区域，同一块只占一条。0 = 关闭。",
                Line(_historyCapacity, Suffix("条"))));

        AddPage(Icons.Folder, "保存",
            StackedCard(Icons.Folder, "默认目录",
                "留空则用系统「图片」文件夹。",
                Fill(_directory, Button("浏览…", BrowseDirectory))),
            Card(Icons.Type, "文件名前缀",
                "形如 前缀_20260807_142530.png。", _prefix),
            Card(Icons.Save, "保存时不弹对话框",
                "直接存进上面的目录，重名自动加序号。", _saveWithoutPrompt));
    }

    /// <summary>
    /// 加一个分类：左边一个导航项，右边一页卡片。
    ///
    /// 页面全部提前建好，切换只是换 <see cref="_pageHost"/> 的内容。没显示的那几页虽然不在
    /// 可视树上，控件的值照样留着 —— 那是依赖属性，不靠界面存活。
    /// </summary>
    private void AddPage(Geometry icon, string title, params UIElement[] cards)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Text"),
            Margin = new Thickness(2, 0, 0, 14),
        });
        foreach (var card in cards) page.Children.Add(card);

        var item = new RadioButton
        {
            Style = (Style)FindResource("NavItem"),
            GroupName = "SettingsNav",
            Content = NavLabel(icon, title),
        };
        item.Checked += (_, _) => _pageHost.Content = page;

        _nav.Children.Add(item);
        // 第一个分类默认打开，Checked 会顺带把它那一页装上
        if (_nav.Children.Count == 1) item.IsChecked = true;
    }

    private UIElement NavLabel(Geometry icon, string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(IconBox(icon, 17, gap: 12));
        panel.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("Text"),
        });
        return panel;
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

        grid.Children.Add(IconBox(icon, IconSize, IconGap));
        grid.Children.Add(text);
        grid.Children.Add(host);
        return Shell(grid);
    }

    /// <summary>
    /// 热键卡：说明和输入框上下排，冲突提示紧跟在输入框下面。
    ///
    /// 不跟别的卡一样把控件贴到右边：热键框加一个「恢复默认」已经占掉大半宽度，
    /// 说明会被挤成三行。而提示必须就在那个框旁边 —— 攒到点确定时才一次性弹出来，
    /// 用户得回头猜是哪一项、当初按的又是什么组合，可这两件事他刚才明明都知道。
    /// </summary>
    private Border HotkeyCard(
        Geometry icon, string title, string description,
        HotkeyBox box, TextBlock status, HotkeySpec fallback)
    {
        status.FontSize = 12;
        status.TextWrapping = TextWrapping.Wrap;
        status.Foreground = Brush("Warn");
        status.Margin = new Thickness(0, 8, 0, 0);
        status.Visibility = Visibility.Collapsed;

        var row = Line(box, Button("恢复默认", () => box.Value = fallback));
        ((FrameworkElement)row).Margin = new Thickness(0, 12, 0, 0);
        ((FrameworkElement)row).HorizontalAlignment = HorizontalAlignment.Left;

        var text = TitleBlock(title, description);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Children.Add(row);
        text.Children.Add(status);

        var icons = IconBox(icon, IconSize, IconGap);
        icons.VerticalAlignment = VerticalAlignment.Top;

        var grid = NewCardGrid();
        grid.Children.Add(icons);
        grid.Children.Add(text);
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

        var box = IconBox(icon, IconSize, IconGap);
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
    /// 图标按 24×24 的设计尺寸画，再用 Viewbox 整体缩到目标大小 ——
    /// 线宽跟着一起缩，视觉重量才和覆盖层上那套图标一致。
    /// </summary>
    private FrameworkElement IconBox(Geometry icon, double size, double gap)
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
            Width = size,
            Height = size,
            Margin = new Thickness(2, 0, gap, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private StackPanel TitleBlock(string title, string? description)
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

    // ---------------- 小零件 ----------------

    private Brush Brush(string key) => (Brush)FindResource(key);

    /// <summary>跟在输入框后面的单位。</summary>
    private TextBlock Suffix(string text) => new()
    {
        Text = text,
        Foreground = Brush("TextSecondary"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };

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

    // ---------------- 热键冲突 ----------------

    /// <summary>
    /// 重算「此刻是不是在录热键」，变了才通知。
    ///
    /// 只认一个判据（本窗口是活动窗口，且某个热键框握着键盘焦点），而不是在
    /// Got/Lost/Activated 几个事件里各记各的状态 —— 那几个事件会交错触发，
    /// 各记各的迟早会对不上，然后热键就永远地让在外面了。
    /// </summary>
    private void UpdateRecording()
    {
        bool recording = IsActive
                         && (_captureHotkey.IsKeyboardFocusWithin || _pinHotkey.IsKeyboardFocusWithin);
        if (recording == _recording) return;

        _recording = recording;
        RecordingChanged?.Invoke(recording);
        // 让出/收回热键会改变「谁占着它」，提示得跟着重算一次
        RefreshHotkeyStatus();
    }

    private static bool InsideHotkeyBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is HotkeyBox) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void RefreshHotkeyStatus()
    {
        Describe(_captureHotkey.Value, _pinHotkey.Value, _captureStatus);
        Describe(_pinHotkey.Value, _captureHotkey.Value, _pinStatus);
    }

    private void Describe(HotkeySpec spec, HotkeySpec other, TextBlock status)
    {
        string? message = null;

        if (!spec.IsSet)
            message = null;
        else if (spec == other)
            message = "和另一个热键设成了同一个组合键。";
        else if (IsTakenByOthers(spec))
            message = "已被其他程序占用，这样保存不会生效。";

        status.Text = message ?? string.Empty;
        status.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 这个组合键是不是被「别人」占了。
    ///
    /// 探测的办法是真去注册一次，而 <c>RegisterHotKey</c> 是全系统去重的 ——
    /// 本程序此刻正占着的那些同样会失败。所以先把自己排掉：那不是冲突，
    /// 那正是用户当初设的。
    /// </summary>
    private bool IsTakenByOthers(HotkeySpec spec)
        => spec.IsSet && !_heldBySelf(spec) && _probe.IsTaken(spec.Modifiers, spec.VirtualKey);

    // ---------------- 读写 ----------------

    private void LoadFrom(AppSettings s)
    {
        _captureHotkey.Value = s.CaptureHotkey;
        _pinHotkey.Value = s.PinHotkey;
        _directory.Text = s.SaveDirectory;
        _prefix.Text = s.FileNamePrefix;
        _saveWithoutPrompt.IsChecked = s.SaveWithoutPrompt;
        _showHints.IsChecked = s.ShowHints;
        _elementMode.IsChecked = s.ElementMode;
        _runAtStartup.IsChecked = s.RunAtStartup;
        _historyCapacity.Text = s.HistoryCapacity.ToString(CultureInfo.InvariantCulture);

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

        if (!CheckHotkeys()) return;

        _draft.CaptureHotkey = _captureHotkey.Value;
        _draft.PinHotkey = _pinHotkey.Value;
        _draft.SaveDirectory = directory;
        _draft.FileNamePrefix = _prefix.Text.Trim();
        _draft.SaveWithoutPrompt = _saveWithoutPrompt.IsChecked == true;
        _draft.DefaultAction = Actions[Math.Max(0, _defaultAction.SelectedIndex)].Action;
        _draft.ShowHints = _showHints.IsChecked == true;
        _draft.ElementMode = _elementMode.IsChecked == true;
        _draft.RunAtStartup = _runAtStartup.IsChecked == true;

        // 解析不出来（粘进去一段乱七八糟的）就退回默认值，而不是当成 0 把功能关掉 ——
        // 用户来这儿是想调条数，不是想关掉它
        _draft.HistoryCapacity = int.TryParse(
            _historyCapacity.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity)
            ? Math.Clamp(capacity, 0, CaptureHistory.MaxCapacity)
            : CaptureHistory.DefaultCapacity;

        Result = _draft;
        Close();
    }

    /// <summary>
    /// 保存前的最后一关。返回 false 表示别保存。
    ///
    /// 两项撞成同一个组合键是硬伤：真保存下去，两个功能里必然有一个永远轮不上，
    /// 而用户从界面上看两项都「设好了」，这种不一致必须当场堵死。
    /// 被别的程序占用则只是警告 —— 占着它的那个程序随时可能关掉，
    /// 用户想先设上等以后再说，那是他的自由。
    /// </summary>
    private bool CheckHotkeys()
    {
        if (_captureHotkey.Value.IsSet && _captureHotkey.Value == _pinHotkey.Value)
        {
            MessageBox.Show(this,
                $"「开始截图」和「贴图」都设成了 {_captureHotkey.Value}，请给其中一个换一个组合键。",
                "XkScreenshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var taken = new List<string>();
        if (IsTakenByOthers(_captureHotkey.Value)) taken.Add($"开始截图（{_captureHotkey.Value}）");
        if (IsTakenByOthers(_pinHotkey.Value)) taken.Add($"贴图（{_pinHotkey.Value}）");
        if (taken.Count == 0) return true;

        return MessageBox.Show(this,
            string.Join(Environment.NewLine, taken)
            + Environment.NewLine + Environment.NewLine
            + "以上热键已被其他程序占用，保存后不会生效。仍然保存吗？",
            "XkScreenshot", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }
}
