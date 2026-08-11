using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public TextResultWindow(BitmapSource image, string title)
    {
        Title = title;
        Width = 1000;
        Height = 620;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.CanResize;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        bool dark = Theme.IsSystemDark();

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
            Background = Brushes.Transparent,
        };

        // 右边：加载态
        _loadingLabel = new TextBlock
        {
            Text = "识别中...",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 16,
            Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 200,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0x9E, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
        };

        var loadingStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        loadingStack.Children.Add(_loadingLabel);
        loadingStack.Children.Add(progressBar);

        _loadingPanel = new Grid
        {
            Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
                : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
        };
        _loadingPanel.Children.Add(loadingStack);

        // 右边：结果文字
        _textBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(0),
            Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
                : new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE))
                : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Visibility = Visibility.Collapsed,
        };

        // 右侧容器
        var rightPanel = new Grid();
        rightPanel.Children.Add(_loadingPanel);
        rightPanel.Children.Add(_textBox);

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
            Width = 80,
            Height = 32,
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

        var closeBtn = new Button
        {
            Content = "关闭",
            Width = 80,
            Height = 32,
        };
        closeBtn.Click += (_, _) => { _closed = true; Close(); };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
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
            Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
        };

        _targetBox = new ComboBox
        {
            Width = 132,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            DisplayMemberPath = nameof(TargetOption.Name),
        };

        _targetPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(12),
            Visibility = Visibility.Collapsed,
        };
        _targetPanel.Children.Add(_detectedLabel);
        _targetPanel.Children.Add(_targetBox);

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

        Grid.SetColumn(buttonPanel, 2);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(buttonPanel);

        // 和按钮同一格：语种在左、按钮在右，各自靠边，中间自然让开
        Grid.SetColumn(_targetPanel, 2);
        Grid.SetRow(_targetPanel, 1);
        grid.Children.Add(_targetPanel);

        // 底部分割线
        var bottomBar = new Border
        {
            Height = 1,
            Background = dark
                ? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x15, 0x00, 0x00, 0x00)),
        };
        Grid.SetColumn(bottomBar, 0);
        Grid.SetColumnSpan(bottomBar, 3);
        Grid.SetRow(bottomBar, 1);
        grid.Children.Add(bottomBar);

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
