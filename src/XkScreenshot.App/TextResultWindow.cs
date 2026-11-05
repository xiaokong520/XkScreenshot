using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using XkScreenshot.App.Ui;
using XkScreenshot.Ocr;

namespace XkScreenshot.App;

/// <summary>
/// 文本结果展示窗口，文字识别和翻译共用。左侧显示原图，右侧先显示加载动画，
/// 等异步处理完后调用 ShowResult() 填入文字。
///
/// 翻译比识别多一步选目标语种，那部分挂在 SetupTargetLanguage() 里，不调就不显示。
/// 翻译还会多一块「原文」：识别出来的文字单独摆在译文上面，由 ShowSource() 挂出来。
/// </summary>
public sealed class TextResultWindow : Window
{
    /// <summary>目标语种下拉里的一项。</summary>
    public sealed record TargetOption(string Code, string Name);

    private readonly TextBox _textBox;
    private readonly Grid _loadingPanel;
    private readonly TextBlock _loadingLabel;

    /// <summary>原文那一块：小标题 + 只读文本框。纯识别模式整个不显示。</summary>
    private readonly Grid _sourcePane;
    private readonly TextBox _sourceBox;
    private readonly RowDefinition _sourceRow;

    /// <summary>译文那块的小标题。挂了原文之后才露出来 —— 只有一栏时它只是废话。</summary>
    private readonly TextBlock _resultHeader;

    /// <summary>原文与译文之间的分隔条和那条细线（一起显隐）。</summary>
    private readonly Border _splitterLine;
    private readonly GridSplitter _rowSplitter;

    private readonly StackPanel _targetPanel;
    private readonly TextBlock _detectedLabel;
    private readonly ComboBox _targetBox;
    private readonly Button _copyBtn;
    private readonly ToggleButton _pinBtn;
    private readonly Path _pinIcon;

    /// <summary>原文那一行占的高度权重：译文仍是 1.2，默认四六开，偏向要读的那一侧。</summary>
    private const double SourceWeight = 0.8;

    private const string DefaultCopyLabel = "复制";

    /// <summary>复制按钮平时写什么。挂了原文之后变成「复制译文」——</summary>
    private string _copyLabel = DefaultCopyLabel;

    /// <summary>「已复制」显示多久变回「复制」。</summary>
    private static readonly TimeSpan CopyFlash = TimeSpan.FromSeconds(1.5);
    private DispatcherTimer? _copyFlashTimer;

    /// <summary>窗口关闭后忽略后续的 ShowResult / ShowLoading 调用。</summary>
    private bool _closed;

    /// <param name="dark">深色还是浅色。由调用方按设置里那一档解析好传进来。</param>
    public TextResultWindow(BitmapSource image, string title, bool dark)
    {
        Title = title;
        Width = 1000;
        Height = 620;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 这个窗口能最小化，就必须在任务栏里占个位置：没有任务栏按钮的窗口最小化后
        // 会退回老式的桌面残条 —— 屏幕角落上一条光秃秃的标题栏，还跟着置顶浮在最前
        ShowInTaskbar = true;
        Topmost = true;
        ResizeMode = ResizeMode.CanResize;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // 皮肤要在搭界面之前就位：下面到处都在按资源键取画刷。
        // 控件模板直接借设置界面那一份，两个窗口的按钮、下拉、滚动条才是同一个样子
        Theme.Apply(this, dark);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XkScreenshot;component/Settings/SettingsTheme.xaml"),
        });
        SetResourceReference(BackgroundProperty, "PageBg");

        // 深色窗体配一条亮白标题栏是最扎眼的一种半吊子深色模式，但要等窗口有了句柄才能改
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this, dark);

        // 左边：图片
        var imageControl = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
        };
        var imageBorder = new Border
        {
            Child = imageControl,
            Padding = new Thickness(12),
        };

        // 右边：加载态
        _loadingLabel = new TextBlock
        {
            Text = "识别中...",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        _loadingLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 200,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        progressBar.SetResourceReference(ForegroundProperty, "Accent");

        var loadingStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        loadingStack.Children.Add(_loadingLabel);
        loadingStack.Children.Add(progressBar);

        _loadingPanel = new Grid();
        _loadingPanel.Children.Add(loadingStack);

        // 右边：结果文字。
        _textBox = NewTextBox();
        _textBox.Visibility = Visibility.Collapsed;

        // 翻译模式下译文上方还有一块原文，两块只读框上下叠着，中间一条可拖的分隔。
        // 原文只是拿来对读的，默认比译文矮一点（四六开），嫌挤就拖那条分隔。
        _sourceBox = NewTextBox();
        _sourcePane = BuildPane(NewPaneHeader("原文"), _sourceBox);
        _sourcePane.Visibility = Visibility.Collapsed;

        _resultHeader = NewPaneHeader("译文");
        _resultHeader.Visibility = Visibility.Collapsed;

        // 右上角：置顶开关。
        //
        // 这个窗口默认置顶 —— 多数时候是照着它敲字，被别的窗口盖住就等于没开。
        // 但要对着原文核对、或者结果本身要贴到别处去的时候，压在最前反倒碍事，
        // 所以给一个开关，而不是替所有人定死一种。
        //
        // 摆在内容里而不是标题栏上：原生标题栏塞不进自己的按钮，非要塞就得把客户区
        // 顶上去自己画一整条 —— 那条带子只有透明像素才透得出系统那三颗按钮，
        // 而透明在这个系统上就是黑的，浅色主题下是白窗口顶着一条黑带子。
        _pinIcon = new Path
        {
            Data = Icons.Pin,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        };

        _pinBtn = new ToggleButton
        {
            // 初始态照着上面的 Topmost 来，两处不一致的话，第一下点击是「反着的」
            IsChecked = Topmost,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 6, 0),
            // 图标按 24×24 画好再整体缩放，线宽跟着一起细下来，和设置界面那套图标一个分量
            Content = new Viewbox { Child = _pinIcon, Width = 17, Height = 17 },
        };
        _pinBtn.SetResourceReference(StyleProperty, "IconToggle");
        _pinBtn.Checked += (_, _) => ApplyPinState();
        _pinBtn.Unchecked += (_, _) => ApplyPinState();
        ApplyPinState();

        // 右侧容器。加载动画和结果文字轮流上台，底色画在容器上，
        // 切换的那一下才不会闪一块窗口底色出来
        var rightPanel = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        rightPanel.SetResourceReference(Border.BackgroundProperty, "CardBg");
        rightPanel.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        // 译文那一块：小标题 + 结果框（加载动画和结果文字在同一个格子里轮流上台）
        var resultPane = BuildPane(_resultHeader, _loadingPanel, _textBox);

        // 原文与译文之间那条可拖的分隔。
        //
        // 两个元素都直接挂在 rightStack 的这一行里、并列着叠（细线在下、分隔条在上），
        // 不能为了「细线画在分隔条中间」再套一层 Grid：GridSplitter 只认**直接父级**里
        // 的相邻两行，套一层之后它看到的是容器自己那一行，PreviousAndNext 就无处可改，
        // 拖起来毫无反应（探针里量过：高度一个像素都不动）。
        _splitterLine = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _splitterLine.SetResourceReference(Border.BackgroundProperty, "CardBorder");

        _rowSplitter = new GridSplitter
        {
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };

        // 置顶开关单占一行，不浮在文字上面：浮着的话右上角那几个字会被它盖掉，
        // 而那正好是结果的开头
        var rightStack = new Grid();
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 原文那行默认 0 高：折叠掉的元素照样占星号行的位置，光靠 Visibility 收不干净
        _sourceRow = new RowDefinition { Height = new GridLength(0) };
        rightStack.RowDefinitions.Add(_sourceRow);
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });

        Grid.SetRow(_pinBtn, 0);
        rightStack.Children.Add(_pinBtn);

        Grid.SetRow(_sourcePane, 1);
        rightStack.Children.Add(_sourcePane);

        Grid.SetRow(_splitterLine, 2);
        rightStack.Children.Add(_splitterLine);

        Grid.SetRow(_rowSplitter, 2);
        rightStack.Children.Add(_rowSplitter);

        Grid.SetRow(resultPane, 3);
        rightStack.Children.Add(resultPane);
        rightPanel.Child = rightStack;

        // GridSplitter 分隔左右
        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
        };

        // 底部按钮
        _copyBtn = new Button
        {
            Content = _copyLabel,
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _copyBtn.Click += (_, _) =>
        {
            if (_textBox.Text.Length == 0) { FlashCopyResult("没有内容"); return; }

            try
            {
                Output.ClipboardWriter.SetText(_textBox.Text);
                _copyBtn.ToolTip = null;
                FlashCopyResult("已复制");
            }
            catch (Exception ex)
            {
                // 重试都用完了还写不进去。原因挂在按钮的提示上 ——
                // 按钮上只写得下「复制失败」，而「为什么」是这时候唯一有用的信息
                _copyBtn.ToolTip = Output.ClipboardWriter.Describe(ex);
                FlashCopyResult("复制失败");
            }
        };

        var closeBtn = new Button { Content = "关闭", MinWidth = 92 };
        closeBtn.Click += (_, _) => { _closed = true; Close(); };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttonPanel.Children.Add(_copyBtn);
        buttonPanel.Children.Add(closeBtn);

        // 底栏左半边：翻译模式下挂「检测到 X → [目标语种]」，纯 OCR 模式整个不显示
        _detectedLabel = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _detectedLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        // 用 ItemTemplate 而不是 DisplayMemberPath：后者只管展开后的列表项，
        // 收起来那个选中框另走一条路，没模板就把整个对象 ToString 出来 ——
        // record 的 ToString 是「TargetOption { Code = ..., Name = ... }」，正好显示在下拉上
        var nameCell = new FrameworkElementFactory(typeof(TextBlock));
        nameCell.SetBinding(TextBlock.TextProperty, new Binding(nameof(TargetOption.Name)));
        nameCell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        _targetBox = new ComboBox
        {
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new DataTemplate(typeof(TargetOption)) { VisualTree = nameCell },
        };

        _targetPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _targetPanel.Children.Add(_detectedLabel);
        _targetPanel.Children.Add(_targetBox);

        // 页脚横跨整个窗口，而不是只压在右半边下面：复制和关闭管的是整个窗口，
        // 而只占右边那一格的话，左边三分之二会空出一条什么都不是的带子 ——
        // 那条带子既没有底色也没有内容，看着就像窗口下面漏了一块
        var footerContent = new Grid { Margin = new Thickness(12) };
        footerContent.Children.Add(_targetPanel);
        footerContent.Children.Add(buttonPanel);

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerContent,
        };
        footer.SetResourceReference(Border.BackgroundProperty, "FooterBg");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        // 主布局
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(imageBorder, 0);
        Grid.SetRow(imageBorder, 0);
        grid.Children.Add(imageBorder);

        Grid.SetColumn(splitter, 1);
        Grid.SetRow(splitter, 0);
        grid.Children.Add(splitter);

        Grid.SetColumn(rightPanel, 2);
        Grid.SetRow(rightPanel, 0);
        grid.Children.Add(rightPanel);

        Grid.SetColumn(footer, 0);
        Grid.SetColumnSpan(footer, 3);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        Content = grid;

        // Esc 关闭
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _closed = true; Close(); }
        };
        // 计时器不停掉的话，窗口关了它还攥着这个窗口，直到那一下 Tick 才撒手
        Closed += (_, _) => { _closed = true; _copyFlashTimer?.Stop(); };
    }

    /// <summary>
    /// 把置顶开关的状态落到窗口上，顺带换掉图标和提示。
    ///
    /// 图标画的是「现在是什么状态」而不是「点下去会怎样」：这颗按钮自己有按下/弹起的
    /// 底色，两套语义反着来会互相打架 —— 底色说「开着」而图标画着划掉的钉子。
    /// </summary>
    private void ApplyPinState()
    {
        bool pinned = _pinBtn.IsChecked == true;

        Topmost = pinned;
        _pinIcon.Data = pinned ? Icons.Pin : Icons.PinOff;
        _pinIcon.SetResourceReference(Shape.StrokeProperty, pinned ? "Accent" : "TextSecondary");
        _pinBtn.ToolTip = pinned ? "已置顶，点击取消" : "点击让窗口保持在最前";
    }

    /// <summary>
    /// 复制完在按钮上给一句回执。
    ///
    /// 「复制」这个动作本身在屏幕上什么也不会发生 —— 不吭声的话，人只能靠再点一次
    /// 来确认它响应了，而再点一次同样什么也看不见。失败也要说：剪贴板被别的程序
    /// 占着是常有的事，那时候用户手里其实是上一次复制的东西，不说就要粘错。
    /// </summary>
    private void FlashCopyResult(string text)
    {
        _copyBtn.Content = text;

        _copyFlashTimer ??= NewCopyFlashTimer();

        // Stop + Start 才算重新上弦：连点时该从最后那一次点起算
        _copyFlashTimer.Stop();
        _copyFlashTimer.Start();
    }

    private DispatcherTimer NewCopyFlashTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = CopyFlash };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _copyBtn.Content = _copyLabel;
        };
        return timer;
    }

    /// <summary>
    /// 多行只读结果框。
    ///
    /// 高度要就地压掉：借来的那份输入框皮照着设置界面里一行高的框定了 Height=32，
    /// 套到这个要铺满一栏的框上就成了一条缝。模板换成自己的，原因见
    /// <see cref="BuildTextBoxTemplate"/>。
    /// </summary>
    private static TextBox NewTextBox()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Height = double.NaN,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(0),
        };
        box.SetResourceReference(BackgroundProperty, "CardBg");
        box.SetResourceReference(ForegroundProperty, "Text");
        box.Template = BuildTextBoxTemplate();
        return box;
    }

    /// <summary>
    /// 结果框自己的模板。
    ///
    /// 不复用设置界面那份输入框皮：它把 PART_ContentHost 钉成 VerticalAlignment=Center
    /// （那是照着一行高的输入框定的），而 VerticalContentAlignment 在模板里被忽略 ——
    /// 结果就是小标题下面空出一大块、正文浮在半当中。外观上两者本来也没别的差别：
    /// 结果框无边框、底色和面板同色，那份模板的圆角、悬停、聚焦都用不上。
    ///
    /// 顺带把原模板写死的「滚动条永远不出现」放开：两栏并排时，哪一栏下面还有没有内容
    /// 得能一眼看出来。滚动条本身走的是 SettingsTheme 里那条细的。
    /// </summary>
    private static ControlTemplate BuildTextBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(PaddingProperty));
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            new TemplateBindingExtension(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        host.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            new TemplateBindingExtension(ScrollViewer.VerticalScrollBarVisibilityProperty));

        border.AppendChild(host);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }

    /// <summary>「原文」/「译文」这类小标题，只交代这块是什么，不抢正文的注意力。</summary>
    private static TextBlock NewPaneHeader(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Margin = new Thickness(12, 8, 12, 2),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return label;
    }

    /// <summary>一块内容 = 一行小标题 + 铺满剩余高度的正文。传多个正文时它们叠在同一格里。</summary>
    private static Grid BuildPane(TextBlock header, params UIElement[] content)
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        pane.Children.Add(header);

        foreach (var item in content)
        {
            Grid.SetRow(item, 1);
            pane.Children.Add(item);
        }

        return pane;
    }

    /// <summary>更新加载提示文字（如从 "识别中..." 切到 "翻译中..."）。</summary>
    public void ShowLoading(string text)
    {
        if (_closed) return;
        Dispatcher.Invoke(() =>
        {
            if (_closed) return;
            _loadingLabel.Text = text;
            _loadingPanel.Visibility = Visibility.Visible;
            _textBox.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// 翻译模式才调：把识别出来的原文单独摆一块在译文上面，从这一刻起窗口就是「原文 / 译文」
    /// 上下两栏，纯识别的窗口一点也不变。
    ///
    /// 为什么不把原文塞进译文框里（原来「没装模型」那条分支就是这么干的）：那样两段文字
    /// 混在同一个滚动区里，读到一半分不清哪句是识别、哪句是翻译；而且译文一换（切换目标
    /// 语种）就得连原文一起重排。
    /// </summary>
    public void ShowSource(string text)
    {
        if (_closed) return;
        Dispatcher.Invoke(() =>
        {
            if (_closed) return;

            _sourceBox.Text = text;
            _sourcePane.Visibility = Visibility.Visible;
            // 折叠只收内容，星号行照样占着高度，所以行高也得一起给回来
            _sourceRow.Height = new GridLength(SourceWeight, GridUnitType.Star);
            _splitterLine.Visibility = Visibility.Visible;
            _rowSplitter.Visibility = Visibility.Visible;
            _resultHeader.Visibility = Visibility.Visible;

            // 两块文字摆在一起之后「复制」就说不清是复制哪一块了，而它复制的一直是译文
            _copyLabel = "复制译文";
            if (Equals(_copyBtn.Content, DefaultCopyLabel)) _copyBtn.Content = _copyLabel;
        });
    }

    /// <summary>
    /// 翻译模式才调：底栏左边挂出「检测到 X →」和目标语种下拉，换一个就回调重译。
    ///
    /// 目标语种在这儿选而不是在设置里定死：一次截图要翻成什么，是看着这张图才知道的事。
    /// 检测结果也一并显示出来 —— 判错了用户至少看得见是判错了，而不是以为翻译坏掉了。
    /// </summary>
    public void SetupTargetLanguage(
        string? detectedName, IReadOnlyList<TargetOption> options, string selected,
        Func<string, Task> onChanged)
    {
        if (_closed || options.Count == 0) return;

        Dispatcher.Invoke(() =>
        {
            if (_closed) return;

            // 没判出语种就只说「译成」：写「检测到 原文」等于什么都没说，
            // 而下拉本身已经把「往哪儿翻」交代清楚了
            _detectedLabel.Text = string.IsNullOrEmpty(detectedName)
                ? "译成" : $"检测到 {detectedName} →";
            _targetBox.ItemsSource = options;
            _targetBox.SelectedItem =
                options.FirstOrDefault(o => o.Code == selected) ?? options[0];
            _targetPanel.Visibility = Visibility.Visible;

            _targetBox.SelectionChanged += async (_, _) =>
            {
                if (_closed || _targetBox.SelectedItem is not TargetOption option) return;

                // 重译期间按住下拉：连点会让几次翻译排着队回来，最后显示的未必是最后选的那个
                _targetBox.IsEnabled = false;
                try { await onChanged(option.Code); }
                finally { if (!_closed) _targetBox.IsEnabled = true; }
            };
        });
    }

    /// <summary>填入结果文字，隐藏加载动画。</summary>
    public void ShowResult(string text)
    {
        if (_closed) return;
        Dispatcher.Invoke(() =>
        {
            if (_closed) return;
            _textBox.Text = text;
            _loadingPanel.Visibility = Visibility.Collapsed;
            _textBox.Visibility = Visibility.Visible;
        });
    }
}
