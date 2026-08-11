using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Ui;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Ocr;
using XkScreenshot.Scroll;
using XkScreenshot.Translate;

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

    /// <summary>标题后面那个感叹号。比正文小一圈，是个记号而不是又一个图标。</summary>
    private const double HintMarkSize = 14;
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
    private readonly ComboBox _scrollMode = new() { Width = 168 };
    private readonly TextBox _scrollMaxHeight = new() { Width = 72, MaxLength = 5 };
    private readonly ToggleButton _saveWithoutPrompt = new();
    private readonly ToggleButton _showHints = new();
    private readonly ToggleButton _elementMode = new();
    private readonly ToggleButton _runAtStartup = new();
    private readonly ToggleButton _runAsAdmin = new();

    // ---------------- 文字识别与翻译 ----------------

    private readonly ComboBox _ocrMode = new() { Width = 168 };
    private readonly ComboBox _translationMode = new() { Width = 168 };
    private readonly ComboBox _apiProtocol = new() { Width = 168 };
    private readonly TextBox _apiBase = new();

    /// <summary>地址栏下面那行「实际请求」。自动补出来的东西得看得见，不然只能靠猜。</summary>
    private readonly TextBlock _apiEndpoint = new();

    /// <summary>
    /// API Key 的两个身子：平时用掩码框，点了眼睛换成明文框。
    ///
    /// 为什么要两个控件而不是一个：WPF 里掩码是 PasswordBox 的行为，改不成明文；
    /// 反过来拿 TextBox 自己画圆点，就得一边显示假字符一边攥着真值，
    /// 光标位置、选中、退格、粘贴每一样都要自己对齐 —— 那种输入框迟早会把 Key 弄坏。
    /// 两个控件各干自己那份，切换时把值倒过去，只有「谁在台上」这一个状态要管。
    /// </summary>
    private readonly PasswordBox _apiKeyMasked = new();
    private readonly TextBox _apiKeyPlain = new();
    private readonly Button _apiKeyReveal = new();
    private bool _apiKeyRevealed;

    private readonly TextBox _model = new() { Width = 200 };

    // ---------------- 模型管理 ----------------

    private readonly TextBox _modelsDir = new();
    private readonly TextBlock _paddleStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _paddlePanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _langPairsPanel = new();

    /// <summary>OCR 模型正在下载。期间按钮换成「下载中…」并禁用。</summary>
    private bool _paddleBusy;

    /// <summary>
    /// 待下载语种的下拉。装过的不出现在里面。
    ///
    /// 往 Items 里填的是显示名而不是对象，取回时按下标查 <see cref="_langChoices"/> ——
    /// 和这一页其他几个下拉一个路子。填对象要靠 DisplayMemberPath，而它只作用于展开后的
    /// 列表项，收起来那个选中框走的是另一条路，会把对象直接 ToString 出来。
    /// </summary>
    private readonly ComboBox _langPicker = new() { Width = 168 };
    private List<BergamotLanguage> _langChoices = [];

    /// <summary>待下载 OCR 语言包的下拉。同上，填名字、按下标取回。</summary>
    private readonly ComboBox _ocrPackPicker = new() { Width = 260 };
    private List<OcrLanguagePack> _ocrPackChoices = [];

    private readonly StackPanel _ocrPacksPanel = new();

    /// <summary>正在下载的 OCR 语言包名，没有就是 null。</summary>
    private string? _busyOcrPack;

    private readonly ProgressBar _ocrPackProgress = new()
    {
        Height = 4,
        Minimum = 0,
        Maximum = 100,
        Value = 0,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>正在下载的语种名，没有就是 null。</summary>
    private string? _busyLangPair;
    private readonly ProgressBar _dlProgress = new()
    {
        Height = 4,
        Minimum = 0,
        Maximum = 100,
        Value = 0,
        Visibility = Visibility.Collapsed,
    };
    private readonly ProgressBar _langPairProgress = new()
    {
        Height = 4,
        Minimum = 0,
        Maximum = 100,
        Value = 0,
        Visibility = Visibility.Collapsed,
    };

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
        Height = Math.Min(680, Math.Max(360, SystemParameters.WorkArea.Height - 80));
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

        _scrollMaxHeight.PreviewTextInput += (_, e) =>
        {
            foreach (char c in e.Text)
            {
                if (char.IsAsciiDigit(c)) continue;
                e.Handled = true;
                return;
            }
        };

        foreach (var (_, label) in Actions) _defaultAction.Items.Add(label);
        _scrollMode.Items.Add("自动");
        _scrollMode.Items.Add("手动");
        _ocrMode.Items.Add("离线");
        _ocrMode.Items.Add("在线");
        _translationMode.Items.Add("离线");
        _translationMode.Items.Add("在线");
        _apiProtocol.Items.Add("OpenAI");
        _apiProtocol.Items.Add("Anthropic");
        foreach (var toggle in new[]
                 { _saveWithoutPrompt, _showHints, _elementMode, _runAtStartup, _runAsAdmin })
            toggle.Style = (Style)FindResource("ToggleSwitch");

        // 「留空则用默认」的框，把那个默认值当占位文字摆出来 ——
        // 只说「留空则用系统「图片」文件夹」，用户还得自己去猜那是哪个盘的哪一层
        Placeholder.SetText(_directory, AppSettings.DefaultSaveDirectory);
        Placeholder.SetText(_modelsDir, AppSettings.DefaultModelsDirectory);

        Background = Brush("PageBg");
        Content = BuildLayout();
        BuildPages();
        LoadFrom(_draft);

        // 端点是地址和协议一起决定的，两边任一动了都得重算那行提示
        _apiBase.TextChanged += (_, _) => RefreshApiEndpoint();
        _apiProtocol.SelectionChanged += (_, _) => RefreshApiEndpoint();

        // 换了模型目录，上面的「已安装 / 未下载」得跟着重算，不能等下次开窗口
        _modelsDir.TextChanged += (_, _) =>
        {
            RefreshPaddleStatus();
            RefreshLangPairs();
        };

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
            Card(Icons.Power, "开机自动启动", null, _runAtStartup),
            Card(Icons.Shield, "以管理员权限运行", null, _runAsAdmin));

        AddPage(Icons.Command, "热键",
            HotkeyCard(Icons.Camera, "开始截图",
                _captureHotkey, _captureStatus, HotkeySpec.CaptureDefault),
            HotkeyCard(Icons.Pin, "贴图",
                _pinHotkey, _pinStatus, HotkeySpec.PinDefault));

        AddPage(Icons.Crop, "截图",
            Card(Icons.CornerDownLeft, "确认截图后", null, _defaultAction),
            Card(Icons.Eye, "显示快捷键提示面板", null, _showHints),
            Card(Icons.Cursor, "默认用控件级检测", null, _elementMode),
            Card(Icons.History, "记住多少条截屏历史", null, Line(_historyCapacity, Suffix("条"))),
            Card(Icons.Scroll, "长截图滚动方式", null, _scrollMode),
            Card(Icons.MoveVertical, "长截图最大高度", null,
                Line(_scrollMaxHeight, Suffix("像素（1000–60000）"))));

        AddPage(Icons.ScanLine, "识别 / 翻译",
            Card(Icons.ScanText, "OCR 工作模式",
                "离线：PaddleOCR ONNX（约 17 MB）；在线：调用大模型识别。", _ocrMode),
            Card(Icons.Languages, "翻译工作模式",
                "离线：Bergamot（每种语言 30~120 MB）；在线：调用大模型翻译。", _translationMode),
            Card(Icons.Braces, "在线 · API 协议",
                "OCR 和翻译共用同一个协议与 Key。", _apiProtocol),
            StackedCard(Icons.Link, "在线 · API 地址",
                "填到域名就行，端点按协议自动补。",
                ApiBaseRow()),
            StackedCard(Icons.Key, "在线 · API Key", null, ApiKeyRow()),
            Card(Icons.Bot, "在线 · 模型", null, _model),

            // ---- 模型管理 ----
            StackedCard(Icons.Folder, "离线模型目录",
                "留空则用软件根目录下的 models/ 文件夹。",
                Fill(_modelsDir, Button("浏览…", BrowseModelsDir))),
            Card(Icons.Package, "PaddleOCR 模型", null, PaddleOcrRow()),
            StackedCard(Icons.SpellCheck, "OCR 语言包", null, OcrPackRow()),
            StackedCard(Icons.ArrowRightLeft, "离线翻译语言",
                "每种 30~120 MB。非英语互译靠英语中转，两边都要装。",
                LangPairRow()));

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
    /// 热键卡：标题和输入框上下排，冲突提示紧跟在输入框下面。
    ///
    /// 不跟别的卡一样把控件贴到右边：热键框加一个「恢复默认」已经占掉大半宽度。
    /// 而提示必须就在那个框旁边 —— 攒到点确定时才一次性弹出来，
    /// 用户得回头猜是哪一项、当初按的又是什么组合，可这两件事他刚才明明都知道。
    /// </summary>
    private Border HotkeyCard(
        Geometry icon, string title,
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

        var text = TitleBlock(title, null);
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

    /// <summary>
    /// 卡片左边那块：标题，需要解释的项在标题后面跟一个感叹号，说明挂在它的悬停提示上。
    ///
    /// 说明不再直接铺在标题下面：那样每张卡都多出一两行小字，一页翻下来满屏都是字，
    /// 真正要动的那个控件反而不显眼。说明只在第一次看的时候有用，之后就是噪音。
    /// </summary>
    private StackPanel TitleBlock(string title, string? hint)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush("Text"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (hint is not null) line.Children.Add(HintMark(hint));

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(line);

        Grid.SetColumn(stack, 1);
        return stack;
    }

    /// <summary>
    /// 标题后面那个感叹号。
    ///
    /// 图标只有描边，鼠标从圈里穿过是碰不到它的 —— 命中测试只认画出来的那几个像素，
    /// 所以垫一层透明底把整块方形都变成可悬停的区域。说明文字给个最大宽度让它换行，
    /// 不然一句话会拉成横贯屏幕的一条。
    /// </summary>
    private FrameworkElement HintMark(string hint) => new Border
    {
        Background = Brushes.Transparent,
        Child = IconBox(Icons.Alert, HintMarkSize, 0),
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Help,
        ToolTip = new TextBlock { Text = hint, MaxWidth = 280, TextWrapping = TextWrapping.Wrap },
    };

    // ---------------- 小零件 ----------------

    private Brush Brush(string key) => (Brush)FindResource(key);

    /// <summary>
    /// 卡片里的一行普通文字。
    ///
    /// 走这个而不是就地 new TextBlock：不给 Foreground 的 TextBlock 是黑字，
    /// 浅色皮肤下看着一切正常，换成深色皮肤就是一行看不见的字 —— 而写代码的人
    /// 多半正用着浅色，测不出来。
    /// </summary>
    private TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brush("Text"),
        VerticalAlignment = VerticalAlignment.Center,
    };

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
        _runAsAdmin.IsChecked = s.RunAsAdmin;
        _historyCapacity.Text = s.HistoryCapacity.ToString(CultureInfo.InvariantCulture);

        _ocrMode.SelectedIndex = s.Recognition.Mode == OcrMode.Online ? 1 : 0;
        _translationMode.SelectedIndex = s.Translation.Mode == OcrMode.Online ? 1 : 0;
        _apiProtocol.SelectedIndex = s.Translation.ApiProtocol == ApiProtocolSetting.Anthropic ? 1 : 0;
        _apiBase.Text = s.Translation.ApiBase;
        // 每次装载都退回掩码：窗口关了再开，Key 不该还明晃晃摊在那儿
        _apiKeyRevealed = false;
        _apiKeyMasked.Password = s.Translation.ApiKey;
        _apiKeyPlain.Text = string.Empty;
        _apiKeyPlain.Visibility = Visibility.Collapsed;
        _apiKeyMasked.Visibility = Visibility.Visible;
        RefreshApiKeyReveal();
        _model.Text = s.Translation.Model;
        RefreshApiEndpoint();
        _modelsDir.Text = s.ModelsDirectory;
        RefreshPaddleStatus();
        RefreshOcrPacks();
        RefreshLangPairs();

        _scrollMode.SelectedIndex = s.ScrollMode == ScrollMode.Auto ? 0 : 1;
        _scrollMaxHeight.Text = s.ScrollMaxHeight.ToString(CultureInfo.InvariantCulture);

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

    private void BrowseModelsDir()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择离线模型存放目录",
            InitialDirectory = Directory.Exists(_modelsDir.Text) ? _modelsDir.Text : null,
        };
        if (dialog.ShowDialog(this) == true) _modelsDir.Text = dialog.FolderName;
    }

    private void RefreshPaddleStatus()
    {
        string dir = Path.Combine(ResolveCurrentModelsDir(), "paddleocr");
        bool hasDet = File.Exists(Path.Combine(dir, "det.onnx"));
        bool hasRec = File.Exists(Path.Combine(dir, "rec.onnx"));
        bool hasCls = File.Exists(Path.Combine(dir, "cls.onnx"));
        bool hasDict = File.Exists(Path.Combine(dir, "dict.txt"));
        bool installed = hasDet && hasRec && hasCls && hasDict;

        if (!_paddleBusy)
        {
            _paddleStatus.Text = installed ? "已安装 ✓"
                : (hasDet || hasRec || hasCls || hasDict) ? "不完整，需重新下载"
                : "未下载";
        }

        _paddlePanel.Children.Clear();
        _paddlePanel.Children.Add(_paddleStatus);
        if (_paddleBusy)
        {
            var busy = Button("下载中…", () => { });
            busy.IsEnabled = false;
            _paddlePanel.Children.Add(busy);
        }
        else if (installed)
        {
            _paddlePanel.Children.Add(Button("删除", DeletePaddleOcr));
        }
        else
        {
            _paddlePanel.Children.Add(Button("下载", DownloadPaddleOcr));
        }
    }

    private void DeletePaddleOcr()
    {
        try { Directory.Delete(Path.Combine(ResolveCurrentModelsDir(), "paddleocr"), recursive: true); }
        catch { /* ignore */ }
        RefreshPaddleStatus();
    }

    /// <summary>下载途中刷新一行文案，顺便把按钮压成禁用的「下载中…」。</summary>
    private void SetPaddleBusy(string status)
    {
        _paddleBusy = true;
        _paddleStatus.Text = status;
        RefreshPaddleStatus();
    }

    /// <summary>
    /// 模型根目录，按框里现在填的算 —— 「已安装 / 未下载」得跟着用户正在改的那个路径走，
    /// 不能等按了确定才对。留空时的落点和 AppSettings 共用一份，免得两边各走一套逻辑。
    /// </summary>
    private string ResolveCurrentModelsDir()
    {
        string configured = _modelsDir.Text.Trim();
        return configured.Length > 0 ? configured : AppSettings.DefaultModelsDirectory;
    }

    private async void DownloadPaddleOcr()
    {
        string root = ResolveCurrentModelsDir();
        string targetDir = Path.Combine(root, "paddleocr");
        Directory.CreateDirectory(targetDir);

        _dlProgress.Visibility = Visibility.Visible;
        _dlProgress.Value = 0;
        var progress = new Progress<int>(p => _dlProgress.Value = p);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            // 检测和识别取自 PaddlePaddle 在 HuggingFace 的官方组织；
            // 方向分类那份官方没发 ONNX，只好还从 RapidOCR 作者的仓库拿
            SetPaddleBusy("下载中… 检测模型");
            await OcrLanguagePacks.DownloadFileAsync(http,
                $"https://huggingface.co/{OcrLanguagePacks.BaseDetRepo}/resolve/main/inference.onnx",
                Path.Combine(targetDir, "det.onnx"), progress);
            SetPaddleBusy("下载中… 识别模型");
            await OcrLanguagePacks.DownloadBaseRecAsync(http, targetDir, progress);
            SetPaddleBusy("下载中… 方向模型");
            await OcrLanguagePacks.DownloadFileAsync(http,
                OcrLanguagePacks.ClsUrl, Path.Combine(targetDir, "cls.onnx"), progress);

            _paddleBusy = false;
            RefreshPaddleStatus();
            RefreshOcrPacks();
            _dlProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "PaddleOCR 模型下载完成。", "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _paddleBusy = false;
            RefreshPaddleStatus();
            _paddleStatus.Text = "下载失败";
            _dlProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "下载失败：" + ex.Message, "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement OcrPackRow()
    {
        var stack = new StackPanel();
        stack.Children.Add(Line(_ocrPackPicker, Button("下载", () => _ = DownloadOcrPack())));
        stack.Children.Add(_ocrPacksPanel);
        stack.Children.Add(_ocrPackProgress);
        return stack;
    }

    private void RefreshOcrPacks()
    {
        _ocrPacksPanel.Children.Clear();
        string dir = Path.Combine(ResolveCurrentModelsDir(), "paddleocr");
        var installed = OcrLanguagePacks.Installed(dir).ToList();

        var installedCodes = installed.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        string? keep = SelectedOcrPack()?.Code;
        _ocrPackChoices = [.. OcrLanguagePacks.All.Where(p => !installedCodes.Contains(p.Code))];
        _ocrPackPicker.ItemsSource = _ocrPackChoices.Select(p => p.Name).ToList();
        _ocrPackPicker.SelectedIndex = _ocrPackChoices.Count == 0
            ? -1
            : Math.Max(_ocrPackChoices.FindIndex(p => p.Code == keep), 0);
        _ocrPackPicker.IsEnabled = _busyOcrPack is null && _ocrPackChoices.Count > 0;

        foreach (var pack in installed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Label(pack.Name));

            var button = Button("删除", () =>
            {
                try { File.Delete(OcrLanguagePacks.RecPath(dir, pack.Code)); } catch { /* 被占着就算了 */ }
                try { File.Delete(OcrLanguagePacks.DictPath(dir, pack.Code)); } catch { /* 同上 */ }
                RefreshOcrPacks();
            });
            if (_busyOcrPack is not null) button.IsEnabled = false;
            row.Children.Add(button);
            _ocrPacksPanel.Children.Add(row);
        }

        if (_busyOcrPack is not null)
        {
            _ocrPacksPanel.Children.Add(new TextBlock
            {
                Text = $"正在下载 {_busyOcrPack}…",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush("TextSecondary"),
            });
        }
    }

    private OcrLanguagePack? SelectedOcrPack()
        => (uint)_ocrPackPicker.SelectedIndex < (uint)_ocrPackChoices.Count
            ? _ocrPackChoices[_ocrPackPicker.SelectedIndex]
            : null;

    private async Task DownloadOcrPack()
    {
        if (_busyOcrPack is not null) return;
        if (SelectedOcrPack() is not { } pack) return;

        string dir = Path.Combine(ResolveCurrentModelsDir(), "paddleocr");
        _ocrPackProgress.Visibility = Visibility.Visible;
        _ocrPackProgress.Value = 0;
        _busyOcrPack = pack.Name;
        RefreshOcrPacks();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            await OcrLanguagePacks.DownloadPackAsync(http, dir, pack,
                new Progress<int>(p => _ocrPackProgress.Value = p));

            _ocrPackProgress.Visibility = Visibility.Collapsed;
            _busyOcrPack = null;
            RefreshOcrPacks();
            MessageBox.Show(this, $"{pack.Name}识别模型下载完成。", "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _ocrPackProgress.Visibility = Visibility.Collapsed;
            _busyOcrPack = null;
            RefreshOcrPacks();
            MessageBox.Show(this, "下载失败：" + ex.Message, "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement PaddleOcrRow()
    {
        // 字段初始化那会儿还拿不到窗口资源，颜色只能等到这里再上
        _paddleStatus.Foreground = Brush("Text");

        var stack = new StackPanel();
        stack.Children.Add(_paddlePanel);
        stack.Children.Add(_dlProgress);
        return stack;
    }

    /// <summary>掩码框（或明文框）+ 右边那颗眼睛。</summary>
    private UIElement ApiKeyRow()
    {
        _apiKeyPlain.Visibility = Visibility.Collapsed;

        _apiKeyReveal.Width = 40;
        // 按钮样式默认左右各留 16 的内边距，那是给文字的；40 宽的按钮减掉 32
        // 只剩 8px 放图标，图标会被 Viewbox 压成一根弧线。图标按钮自己把内边距清掉
        _apiKeyReveal.Padding = new Thickness(0);
        _apiKeyReveal.Margin = new Thickness(8, 0, 0, 0);
        _apiKeyReveal.VerticalAlignment = VerticalAlignment.Center;
        _apiKeyReveal.Click += (_, _) => ToggleApiKeyRevealed();
        RefreshApiKeyReveal();

        // 两个框叠在同一格里：谁显示都占同一块地方，切换时输入框不会跳一下
        var stack = new Grid();
        stack.Children.Add(_apiKeyMasked);
        stack.Children.Add(_apiKeyPlain);

        return Fill(stack, _apiKeyReveal);
    }

    private void ToggleApiKeyRevealed()
    {
        _apiKeyRevealed = !_apiKeyRevealed;

        // 值只在切换这一刻倒一次手，倒的方向由「刚才是谁在台上」决定
        if (_apiKeyRevealed) _apiKeyPlain.Text = _apiKeyMasked.Password;
        else _apiKeyMasked.Password = _apiKeyPlain.Text;

        _apiKeyPlain.Visibility = _apiKeyRevealed ? Visibility.Visible : Visibility.Collapsed;
        _apiKeyMasked.Visibility = _apiKeyRevealed ? Visibility.Collapsed : Visibility.Visible;
        RefreshApiKeyReveal();

        // 焦点跟着走，不然点完眼睛想接着改，光标还在那个已经藏起来的框里
        if (_apiKeyRevealed) _apiKeyPlain.Focus(); else _apiKeyMasked.Focus();
    }

    private void RefreshApiKeyReveal()
    {
        _apiKeyReveal.Content = new Viewbox
        {
            Width = 16,
            Height = 16,
            Child = new System.Windows.Shapes.Path
            {
                // 显示的是「点下去会变成什么」：藏着时给睁眼，露着时给闭眼
                Data = _apiKeyRevealed ? Icons.EyeOff : Icons.Eye,
                Stroke = Brush("Text"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
            },
        };
        _apiKeyReveal.ToolTip = _apiKeyRevealed ? "隐藏" : "显示";
    }

    /// <summary>当前 Key，不管此刻是哪个框在台上。</summary>
    private string CurrentApiKey => _apiKeyRevealed ? _apiKeyPlain.Text : _apiKeyMasked.Password;

    /// <summary>地址栏 + 底下那行补全结果。</summary>
    private UIElement ApiBaseRow()
    {
        _apiEndpoint.FontSize = 12;
        _apiEndpoint.Foreground = Brush("TextSecondary");
        _apiEndpoint.TextWrapping = TextWrapping.Wrap;
        _apiEndpoint.Margin = new Thickness(0, 6, 0, 0);

        var stack = new StackPanel();
        stack.Children.Add(_apiBase);
        stack.Children.Add(_apiEndpoint);
        return stack;
    }

    /// <summary>重算「实际请求」那行。地址空着就整行收起来，不留一句半截话。</summary>
    private void RefreshApiEndpoint()
    {
        var protocol = _apiProtocol.SelectedIndex == 1
            ? Core.Llm.ApiProtocol.Anthropic : Core.Llm.ApiProtocol.OpenAI;
        string endpoint = Core.Llm.LlmEndpoint.Resolve(protocol, _apiBase.Text);

        _apiEndpoint.Text = "实际请求：" + endpoint;
        _apiEndpoint.Visibility = endpoint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement LangPairRow()
    {
        var stack = new StackPanel();
        stack.Children.Add(Line(_langPicker, Button("下载", () => _ = DownloadLanguage())));
        stack.Children.Add(_langPairsPanel);
        stack.Children.Add(_langPairProgress);
        return stack;
    }

    private static async Task DownloadFile(HttpClient http, string url, string path,
        IProgress<int>? progress = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        using var src = await response.Content.ReadAsStreamAsync();
        using var dst = File.Create(path);

        if (total > 0 && progress is not null)
        {
            var buf = new byte[8192];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                progress.Report((int)(read * 100 / total));
            }
        }
        else
        {
            await src.CopyToAsync(dst);
        }

        progress?.Report(100);
    }

    /// <summary>模型目录下装了哪些语种 —— 英语不算，它是靠「某语言 ↔ 英语」隐含存在的。</summary>
    private static List<BergamotLanguage> InstalledLanguages(string bergamotDir)
        => [.. BergamotModelDir.EnumerateInstalled(bergamotDir)
            .SelectMany(d => new[] { d.From, d.To })
            .Where(c => c != BergamotCatalog.Pivot)
            .Distinct(StringComparer.Ordinal)
            .Select(BergamotCatalog.Find)
            .OfType<BergamotLanguage>()
            .OrderBy(l => l.Name, StringComparer.CurrentCulture)];

    private void RefreshLangPairs()
    {
        _langPairsPanel.Children.Clear();
        string bergamotDir = Path.Combine(ResolveCurrentModelsDir(), BergamotModelDir.FolderName);
        var installed = InstalledLanguages(bergamotDir);

        // 下拉里只留没装的：装过的那些下面已经各有一行，重复列出来只会让人以为能装两遍
        var installedCodes = installed.Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        string? keep = SelectedLanguage()?.Code;
        _langChoices = [.. BergamotCatalog.Languages.Where(l => !installedCodes.Contains(l.Code))];
        _langPicker.ItemsSource = _langChoices.Select(l => l.Name).ToList();
        _langPicker.SelectedIndex = _langChoices.Count == 0
            ? -1
            : Math.Max(_langChoices.FindIndex(l => l.Code == keep), 0);
        _langPicker.IsEnabled = _busyLangPair is null && _langChoices.Count > 0;

        if (installed.Count == 0 && _busyLangPair is null)
        {
            _langPairsPanel.Children.Add(new TextBlock
            {
                Text = "还没装任何语言，离线翻译用不了。",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush("TextSecondary"),
            });
            return;
        }

        foreach (var lang in installed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Label(lang.ToEnglishOnly ? $"{lang.Name} → 英语" : $"{lang.Name} ↔ 英语"));

            var button = Button("删除", () => DeleteLanguage(bergamotDir, lang));
            // 一根进度条管所有语言，所以下载期间把整组按钮都按住，不让并发下载
            if (_busyLangPair is not null) button.IsEnabled = false;
            row.Children.Add(button);
            _langPairsPanel.Children.Add(row);
        }

        if (_busyLangPair is not null)
        {
            _langPairsPanel.Children.Add(new TextBlock
            {
                Text = $"正在下载 {_busyLangPair}…",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush("TextSecondary"),
            });
        }
    }

    private void DeleteLanguage(string bergamotDir, BergamotLanguage lang)
    {
        foreach (string direction in BergamotCatalog.DirectionsOf(lang))
        {
            try { Directory.Delete(Path.Combine(bergamotDir, direction), recursive: true); }
            catch { /* 正被引擎占着或者已经没了，两种都不值得打断用户 */ }
        }
        RefreshLangPairs();
    }

    /// <summary>
    /// 下一个语种，两个方向一起下。
    ///
    /// 不给用户拆成两行各下各的：非英语之间的翻译要靠英语中转，缺哪一半都会在
    /// 某个方向上突然翻不了，而那时候他早就忘了当初只勾了一半。
    /// </summary>
    private BergamotLanguage? SelectedLanguage()
        => (uint)_langPicker.SelectedIndex < (uint)_langChoices.Count
            ? _langChoices[_langPicker.SelectedIndex]
            : null;

    private async Task DownloadLanguage()
    {
        if (_busyLangPair is not null) return;
        if (SelectedLanguage() is not { } lang) return;

        string bergamotDir = Path.Combine(ResolveCurrentModelsDir(), BergamotModelDir.FolderName);
        Directory.CreateDirectory(bergamotDir);

        _langPairProgress.Visibility = Visibility.Visible;
        _langPairProgress.Value = 0;
        _busyLangPair = lang.Name;
        RefreshLangPairs();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var registry = await BergamotCatalog.LoadRegistryAsync(http);

            var directions = BergamotCatalog.DirectionsOf(lang).Where(registry.Has).ToList();
            if (directions.Count == 0)
                throw new InvalidOperationException($"模型库里暂时没有 {lang.Name} 的模型。");

            for (int i = 0; i < directions.Count; i++)
            {
                int index = i;
                _busyLangPair = $"{lang.Name}（{index + 1}/{directions.Count}）";
                RefreshLangPairs();

                // 两个方向共用一根进度条，各占一半
                var progress = new Progress<int>(p =>
                    _langPairProgress.Value = (index * 100 + p) / directions.Count);
                await registry.DownloadAsync(http, bergamotDir, directions[index], progress);
            }

            _langPairProgress.Visibility = Visibility.Collapsed;
            _busyLangPair = null;
            RefreshLangPairs();
            MessageBox.Show(this, $"{lang.Name}翻译模型下载完成。", "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _langPairProgress.Visibility = Visibility.Collapsed;
            _busyLangPair = null;
            RefreshLangPairs();
            MessageBox.Show(this, "下载失败：" + ex.Message, "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        if (!ConfirmElevationSwitch()) return;

        _draft.CaptureHotkey = _captureHotkey.Value;
        _draft.PinHotkey = _pinHotkey.Value;
        _draft.SaveDirectory = directory;
        _draft.FileNamePrefix = _prefix.Text.Trim();
        _draft.SaveWithoutPrompt = _saveWithoutPrompt.IsChecked == true;
        _draft.DefaultAction = Actions[Math.Max(0, _defaultAction.SelectedIndex)].Action;
        _draft.ShowHints = _showHints.IsChecked == true;
        _draft.ElementMode = _elementMode.IsChecked == true;
        _draft.RunAtStartup = _runAtStartup.IsChecked == true;
        _draft.RunAsAdmin = _runAsAdmin.IsChecked == true;

        // 解析不出来（粘进去一段乱七八糟的）就退回默认值，而不是当成 0 把功能关掉 ——
        // 用户来这儿是想调条数，不是想关掉它
        _draft.HistoryCapacity = int.TryParse(
            _historyCapacity.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity)
            ? Math.Clamp(capacity, 0, CaptureHistory.MaxCapacity)
            : CaptureHistory.DefaultCapacity;

        _draft.Recognition.Mode = _ocrMode.SelectedIndex == 1 ? OcrMode.Online : OcrMode.Offline;
        _draft.Translation.Mode = _translationMode.SelectedIndex == 1 ? OcrMode.Online : OcrMode.Offline;
        _draft.Translation.ApiProtocol = _apiProtocol.SelectedIndex == 1
            ? ApiProtocolSetting.Anthropic : ApiProtocolSetting.OpenAI;
        _draft.Translation.ApiBase = _apiBase.Text.Trim();
        _draft.Translation.ApiKey = CurrentApiKey.Trim();
        _draft.Translation.Model = _model.Text.Trim();
        _draft.ModelsDirectory = _modelsDir.Text.Trim();

        _draft.ScrollMode = _scrollMode.SelectedIndex == 1 ? ScrollMode.Manual : ScrollMode.Auto;
        _draft.ScrollMaxHeight = int.TryParse(
            _scrollMaxHeight.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxHeight)
            ? Math.Clamp(maxHeight, 1000, 60000) : ScrollOptions.Standard.MaxHeight;

        Result = _draft;
        Close();
    }

    /// <summary>
    /// 动了「以管理员权限运行」就得先打个招呼。权限是进程启动时定死的，改不了，
    /// 只能整个重来一次 —— 点确定之后程序自己关掉又开起来，事先不说会像是崩了。
    ///
    /// 拿当前进程的实际权限比，而不是比设置里存的那个值：用户完全可能是右键
    /// 「以管理员身份运行」进来的，那时候存的值是什么已经不重要了。
    /// </summary>
    private bool ConfirmElevationSwitch()
    {
        bool wanted = _runAsAdmin.IsChecked == true;
        if (wanted == Elevation.IsElevated) return true;

        return MessageBox.Show(this,
            wanted
                ? "保存后程序会退出并以管理员权限重新启动，中间会弹一次 UAC。继续吗？"
                : "保存后程序会退出并以普通权限重新启动。继续吗？",
            "XkScreenshot", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
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
