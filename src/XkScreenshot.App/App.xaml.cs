using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using XkScreenshot.App.Output;
using XkScreenshot.App.Overlay;
using XkScreenshot.App.Settings;
using XkScreenshot.Capture;
using XkScreenshot.Pin;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Core.Native;
using WinForms = System.Windows.Forms;

namespace XkScreenshot.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\XkScreenshot.SingleInstance";

    private readonly PinManager _pins = new();

    private Mutex? _instanceMutex;
    private HotkeyManager? _hotkeys;
    private CaptureController? _controller;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripItem? _captureMenuItem;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private BitmapSource? _lastCapture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("XkScreenshot 已经在运行了。", "XkScreenshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = SettingsStore.Load();
        // 自启动的真相在注册表里，不在配置文件里：用户可能在任务管理器的启动项里关掉过它。
        // 以注册表为准，设置界面上那个勾才不会撒谎。
        _settings.RunAtStartup = StartupRegistration.IsEnabled();

        _controller = new CaptureController(new GdiScreenCapture());
        _controller.Captured += OnCaptured;

        _pins.CopyRequested += CopyImage;
        _pins.SaveRequested += SaveImage;

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += _ => _controller?.Start();

        SetupTrayIcon();
        ApplySettings();
    }

    /// <summary>
    /// 把当前设置铺到各处。设置界面点确定后也走这里，所以每一项都必须是「重设」而不是「叠加」。
    /// </summary>
    private void ApplySettings()
    {
        if (_controller is not null) _controller.Defaults = _settings.ToCaptureDefaults();

        var result = _hotkeys!.Reset(_settings.CaptureHotkey.ToBinding("Capture"));

        // 注册失败必须说出来。RegisterHotKey 失败是静默的，
        // 不提示的话用户只会感知到「按了没反应」，然后放弃这个软件。
        if (!result.Success) ShowTrayWarning(result.Error!);

        string hotkey = _settings.CaptureHotkey.ToString();
        if (_captureMenuItem is not null) _captureMenuItem.Text = $"截图 ({hotkey})";
        if (_trayIcon is not null) _trayIcon.Text = $"XkScreenshot — 按 {hotkey} 截图";
    }

    private void SetupTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        _captureMenuItem = menu.Items.Add("截图", null, (_, _) => _controller?.Start());
        menu.Items.Add("关闭全部贴图", null, (_, _) => _pins.CloseAll());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _trayIcon = new WinForms.NotifyIcon
        {
            // M1 换成自己的 .ico；现在先借系统图标，省掉一个二进制资源
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => _controller?.Start();
    }

    /// <summary>
    /// 设置窗口只开一个。开着的时候再点托盘就把它拉到前面来 ——
    /// 两个窗口各改各的，后关的那个会把先关的改动整个盖掉。
    /// </summary>
    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings);
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (window.Result is not { } updated) return;

            // 自启动写的是注册表，不是配置文件，失败了要单独说一声
            if (updated.RunAtStartup != StartupRegistration.IsEnabled()
                && StartupRegistration.Apply(updated.RunAtStartup) is { } startupError)
            {
                ShowTrayWarning(startupError);
                updated.RunAtStartup = StartupRegistration.IsEnabled();
            }

            _settings = updated;
            ApplySettings();

            if (SettingsStore.Save(_settings) is { } saveError) ShowTrayWarning(saveError);
        };
        window.Show();
        window.Activate();
    }

    private void OnCaptured(CaptureResult result)
    {
        _lastCapture = result.Image;

        switch (result.Action)
        {
            case CaptureAction.Pin:
                _pins.Create(result.Image, result.Bounds);
                break;

            case CaptureAction.Save:
                SaveImage(result.Image);
                break;

            default:
                CopyImage(result.Image);
                break;
        }
    }

    private void CopyImage(BitmapSource image)
    {
        try
        {
            ClipboardWriter.SetImage(image);
            ShowTrayInfo($"已复制到剪贴板（{image.PixelWidth}×{image.PixelHeight}）");
        }
        catch (Exception ex)
        {
            ShowTrayWarning("写入剪贴板失败：" + ex.Message);
        }
    }

    private void SaveImage(BitmapSource image)
    {
        string directory = _settings.ResolveSaveDirectory();
        string prefix = _settings.ResolveFileNamePrefix();

        try
        {
            var path = _settings.SaveWithoutPrompt
                ? ImageSaver.SaveInto(image, directory, prefix)
                : ImageSaver.SaveAs(image, directory, prefix);

            if (path is not null) ShowTrayInfo("已保存到 " + path);
        }
        catch (Exception ex)
        {
            ShowTrayWarning("保存失败：" + ex.Message);
        }
    }

    private void ShowTrayInfo(string message)
        => _trayIcon?.ShowBalloonTip(2000, "XkScreenshot", message, WinForms.ToolTipIcon.Info);

    private void ShowTrayWarning(string message)
        => _trayIcon?.ShowBalloonTip(5000, "XkScreenshot", message, WinForms.ToolTipIcon.Warning);

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
