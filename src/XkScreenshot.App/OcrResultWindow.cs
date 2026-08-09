using System;
using System.Collections.Generic;
using System.Linq;
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
/// OCR / 翻译结果展示窗口。左侧显示原图，右侧先显示加载动画，
/// 等异步处理完后调用 ShowResult() 填入文字。
/// </summary>
public sealed class OcrResultWindow : Window
{
    private readonly TextBox _textBox;
    private readonly Grid _loadingPanel;
    private readonly TextBlock _loadingLabel;

    /// <summary>窗口关闭后忽略后续的 ShowResult / ShowLoading 调用。</summary>
    private bool _closed;

    public OcrResultWindow(BitmapSource image, string title = "文字识别结果")
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
        var copyBtn = new Button
        {
            Content = "复制",
            Width = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
        };
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(_textBox.Text); }
            catch { /* 剪贴板被占用 */ }
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
        buttonPanel.Children.Add(copyBtn);
        buttonPanel.Children.Add(closeBtn);

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
        Closed += (_, _) => _closed = true;
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

    /// <summary>填入结果文字，隐藏加载动画。可附带原始响应用于调试对比。</summary>
    public void ShowResult(string text, string? rawText = null)
    {
        if (_closed) return;
        Dispatcher.Invoke(() =>
        {
            if (_closed) return;
            if (!string.IsNullOrWhiteSpace(rawText))
                _textBox.Text = $"──────── 原始响应 ────────\n{rawText}\n──────── 识别结果 ────────\n{text}";
            else
                _textBox.Text = text;
            _loadingPanel.Visibility = Visibility.Collapsed;
            _textBox.Visibility = Visibility.Visible;
        });
    }
}
