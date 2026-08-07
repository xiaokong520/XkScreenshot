using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;
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

    // 热键靠名字分发。HotkeyManager 只管注册，按下之后干什么是这一层的事
    private const string CaptureHotkeyName = "Capture";
    private const string PinHotkeyName = "Pin";

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
        _hotkeys.Pressed += OnHotkeyPressed;

        SetupTrayIcon();
        ApplySettings();
    }

    private void OnHotkeyPressed(HotkeyBinding binding)
    {
        switch (binding.Name)
        {
            case CaptureHotkeyName:
                _controller?.Start();
                break;

            case PinHotkeyName:
                PinFromClipboard();
                break;
        }
    }

    /// <summary>
    /// 把当前设置铺到各处。设置界面点确定后也走这里，所以每一项都必须是「重设」而不是「叠加」。
    /// </summary>
    private void ApplySettings()
    {
        if (_controller is not null) _controller.Defaults = _settings.ToCaptureDefaults();

        // 注册失败必须说出来。RegisterHotKey 失败是静默的，
        // 不提示的话用户只会感知到「按了没反应」，然后放弃这个软件。
        var failed = RegisterHotkeys().Where(r => !r.Success).Select(r => r.Error!).ToList();
        if (failed.Count > 0) ShowTrayWarning(string.Join(Environment.NewLine, failed));

        string capture = _settings.CaptureHotkey.ToString();
        if (_captureMenuItem is not null)
            _captureMenuItem.Text = _settings.CaptureHotkey.IsSet ? $"截图 ({capture})" : "截图";
        if (_trayIcon is not null)
            _trayIcon.Text = _settings.CaptureHotkey.IsSet
                ? $"XkScreenshot — 按 {capture} 截图"
                : "XkScreenshot";
    }

    /// <summary>按当前设置注册全部热键。没设的那些（虚拟键为 0）跳过，送进去只会白拿一个失败。</summary>
    private IReadOnlyList<HotkeyRegistrationResult> RegisterHotkeys()
    {
        var bindings = new List<HotkeyBinding>();
        if (_settings.CaptureHotkey.IsSet)
            bindings.Add(_settings.CaptureHotkey.ToBinding(CaptureHotkeyName));
        if (_settings.PinHotkey.IsSet)
            bindings.Add(_settings.PinHotkey.ToBinding(PinHotkeyName));

        return _hotkeys!.Reset(bindings);
    }

    /// <summary>
    /// 把剪贴板里的东西钉到屏幕上；剪贴板里没有可贴的内容就钉上一次截的那张 ——
    /// 「刚截完想再看一眼」和「从别处复制了点什么想摆着对照」是同一个动作的两种由头。
    /// </summary>
    private void PinFromClipboard()
    {
        // 截图进行中不打扰：那时候满屏都是覆盖层，弹出来的贴图会被压在下面
        if (_controller?.IsActive == true) return;

        var image = ClipboardReader.ReadPinnable(CursorScale(), out bool busy);

        // 抢不到剪贴板时不能退到上次截图：用户要的是剪贴板里那份东西，
        // 端一张旧截图上来，他会以为自己复制的内容没进剪贴板。
        if (busy)
        {
            ShowTrayWarning("剪贴板正被其他程序占用，稍后再按一次");
            return;
        }

        image ??= _lastCapture;
        if (image is null)
        {
            ShowTrayInfo("剪贴板里没有可以贴的内容，也还没有截过图");
            return;
        }

        _pins.Create(image, PlaceUnderCursor(image));
    }

    /// <summary>光标所在显示器的缩放倍率。文本贴图按它出图，才不会在高 DPI 屏上小一圈。</summary>
    private static double CursorScale()
    {
        var cursor = MonitorEnumerator.GetCursorPosition();
        return MonitorEnumerator.FromPoint(MonitorEnumerator.Enumerate(), cursor)?.ScaleX ?? 1.0;
    }

    /// <summary>
    /// 贴图落在光标处。截图来的贴图会回到画面原位，而剪贴板里的图没有「原位」可言，
    /// 落在手边是最省事的答案。
    /// </summary>
    private static PixelRect PlaceUnderCursor(BitmapSource image)
    {
        int w = image.PixelWidth;
        int h = image.PixelHeight;

        var cursor = MonitorEnumerator.GetCursorPosition();
        var rect = new PixelRect(cursor.X - w / 2, cursor.Y - h / 2, w, h);

        var monitor = MonitorEnumerator.FromPoint(MonitorEnumerator.Enumerate(), cursor);
        if (monitor is null) return rect;

        // 只挪位置不改尺寸：比屏幕还大的图得原样钉出来，缩了就不是原图了。
        // 所以不能用 ClampInto —— 它会连宽高一起裁。
        var bounds = monitor.Bounds;
        return rect with
        {
            X = Math.Clamp(rect.X, bounds.X, Math.Max(bounds.X, bounds.Right - w)),
            Y = Math.Clamp(rect.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - h)),
        };
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

        var window = new SettingsWindow(
            _settings,
            spec => _hotkeys?.Holds(spec.Modifiers, spec.VirtualKey) == true);
        _settingsWindow = window;

        // 只在用户点进热键框、正在录键的那一段让出热键：不让的话他按 F1 会当场触发一次截图，
        // 而不是被录进框里。整个开窗期间都让出去则是另一个极端 —— 设置窗可以一直开着，
        // 那样等于把热键关掉了。这里不报注册失败：录键期间来回让进让出，弹几次气泡只会烦人，
        // 关窗时的 ApplySettings 会统一提示一次。
        window.RecordingChanged += recording =>
        {
            if (recording) _hotkeys?.Clear();
            else RegisterHotkeys();
        };
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;

            if (window.Result is { } updated)
            {
                // 自启动写的是注册表，不是配置文件，失败了要单独说一声
                if (updated.RunAtStartup != StartupRegistration.IsEnabled()
                    && StartupRegistration.Apply(updated.RunAtStartup) is { } startupError)
                {
                    ShowTrayWarning(startupError);
                    updated.RunAtStartup = StartupRegistration.IsEnabled();
                }

                _settings = updated;
                if (SettingsStore.Save(_settings) is { } saveError) ShowTrayWarning(saveError);
            }

            // 取消也要走一遍：关窗时热键框可能正握着焦点、热键正让出去着，不装回来就再也回不来了
            ApplySettings();
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
