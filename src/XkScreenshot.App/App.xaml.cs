using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using XkScreenshot.App.Output;
using XkScreenshot.Capture;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Core.Native;
using WinForms = System.Windows.Forms;

namespace XkScreenshot.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\XkScreenshot.SingleInstance";

    private Mutex? _instanceMutex;
    private HotkeyManager? _hotkeys;
    private CaptureController? _controller;
    private WinForms.NotifyIcon? _trayIcon;
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

        _controller = new CaptureController(new GdiScreenCapture());
        _controller.Captured += OnCaptured;

        SetupTrayIcon();
        SetupHotkeys();
    }

    private void SetupHotkeys()
    {
        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += _ => _controller?.Start();

        // M1 做设置界面时再开放自定义
        var results = new List<HotkeyRegistrationResult>
        {
            _hotkeys.Register(new HotkeyBinding("Capture", HotkeyModifiers.None, 0x70)), // VK_F1
        };

        // 注册失败必须说出来。RegisterHotKey 失败是静默的，
        // 不提示的话用户只会感知到「按了没反应」，然后放弃这个软件。
        var failed = results.Where(r => !r.Success).Select(r => r.Error).ToList();
        if (failed.Count > 0)
            ShowTrayWarning(string.Join(Environment.NewLine, failed));
    }

    private void SetupTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("截图 (F1)", null, (_, _) => _controller?.Start());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _trayIcon = new WinForms.NotifyIcon
        {
            // M1 换成自己的 .ico；现在先借系统图标，省掉一个二进制资源
            Icon = SystemIcons.Application,
            Text = "XkScreenshot — 按 F1 截图",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => _controller?.Start();
    }

    private void OnCaptured(BitmapSource image)
    {
        _lastCapture = image;

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
