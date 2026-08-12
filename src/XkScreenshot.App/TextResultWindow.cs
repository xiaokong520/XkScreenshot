using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XkScreenshot.App.Ui;
using XkScreenshot.Ocr;

namespace XkScreenshot.App;

/// <summary>
/// 文本结果展示窗口，文字识别和翻译共用。左侧显示原图，右侧先显示加载动画，
/// 等异步处理完后调用 ShowResult() 填入文字。
///
/// 翻译比识别多一步选目标语种，那部分挂在 SetupTargetLanguage() 里，不调就不显示。
/// </summary>
public sealed class TextResultWindow : Window
{
    /// <summary>目标语种下拉里的一项。</summary>
    public sealed record TargetOption(string Code, string Name);

    private readonly TextBox _textBox;
    private readonly Grid _loadingPanel;
    private readonly TextBlock _loadingLabel;

    private readonly StackPanel _targetPanel;
    private readonly TextBlock _detectedLabel;
    private readonly ComboBox _targetBox;
    private readonly Button _copyBtn;

    private const string CopyLabel = "复制";

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
        //
        // 高度和垂直对齐要就地压掉：借来的那份输入框皮是照着设置界面里那种一行高的框
        // 定的（定高 32、内容垂直居中），套到这个要铺满右半边的多行框上就成了一条缝。
        _textBox = new TextBox
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
            Visibility = Visibility.Collapsed,
        };
        _textBox.SetResourceReference(BackgroundProperty, "CardBg");
        _textBox.SetResourceReference(ForegroundProperty, "Text");

        // 右侧容器。加载动画和结果文字轮流上台，底色画在容器上，
        // 切换的那一下才不会闪一块窗口底色出来
        var rightPanel = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        rightPanel.SetResourceReference(Border.BackgroundProperty, "CardBg");
        rightPanel.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        var rightStack = new Grid();
        rightStack.Children.Add(_loadingPanel);
        rightStack.Children.Add(_textBox);
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
            Content = CopyLabel,
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
            _copyBtn.Content = CopyLabel;
        };
        return timer;
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
