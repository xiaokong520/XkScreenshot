using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using XkScreenshot.Core.Llm;
using XkScreenshot.Core.Native;
using XkScreenshot.Ocr;
using XkScreenshot.Translate;
using WinForms = System.Windows.Forms;

namespace XkScreenshot.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\XkScreenshot.SingleInstance";

    // 热键靠名字分发。HotkeyManager 只管注册，按下之后干什么是这一层的事
    private const string CaptureHotkeyName = "Capture";
    private const string PinHotkeyName = "Pin";

    /// <summary>提权重启时，新实例最多等老实例腾出单实例名额多久。</summary>
    private static readonly TimeSpan RestartHandoverTimeout = TimeSpan.FromSeconds(10);

    private readonly PinManager _pins = new();

    private Mutex? _instanceMutex;
    private HotkeyManager? _hotkeys;
    private CaptureController? _controller;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripItem? _captureMenuItem;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = new();
    private BitmapSource? _lastCapture;

    /// <summary>启动早期出的岔子。那会儿托盘图标还没建起来，只能先记着，等有地方说了再说。</summary>
    private string? _startupNotice;
    private IOcrEngine? _ocrEngine;
    private ITranslator? _translator;
    private TranslationCache? _translationCache;
    private LlmApiClient? _llmClient;

    /// <summary>上一次截图的原始位置。F3 贴图若贴的正是这一张，回到原位而不是落在光标下。</summary>
    private PixelRect _lastCaptureBounds;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstance(e.Args))
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

        // 权限同样以现实为准：右键「以管理员身份运行」进来的，配置文件里存着什么已经不算数了
        if (Elevation.IsElevated) _settings.RunAsAdmin = true;
        else if (_settings.RunAsAdmin && ElevateAtStartup(e.Args)) return;

        _controller = new CaptureController(new GdiScreenCapture());
        _controller.Captured += OnCaptured;
        _controller.Notice += ShowTrayWarning;
        _controller.History.Changed += SaveHistory;

        _pins.CopyRequested += CopyImage;
        _pins.SaveRequested += SaveImage;

        CreateEngines();
        _translationCache = new TranslationCache();

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkeyPressed;

        SetupTrayIcon();
        ApplySettings();

        if (_startupNotice is not null) ShowTrayWarning(_startupNotice);

        // 起手直接指过去，不走 Relocate：这会儿默认目录里的东西早在上一次切换时就搬走了，
        // 再搬一次只会把别的什么东西卷进来
        HistoryStore.Directory = _settings.ResolveHistoryDirectory();

        // 装历史必须排在 ApplySettings 后面：容量得先由设置定下来，
        // 反过来的话这里会按默认那 30 条把用户设的更长的历史截掉一段
        _controller.History.Restore(HistoryStore.Load());

        // 上次跑的时候可能在「写完 PNG、还没来得及写索引」之间被结束掉，
        // 那种文件谁也认领不了，只会一直占着磁盘
        HistoryStore.PruneImages(_controller.History.ImageIds());
    }

    /// <summary>
    /// 抢下「本机只跑一个」的名额。
    ///
    /// 提权重启这条路上，新实例是在老实例还活着的时候被拉起来的（中间隔着一次 UAC），
    /// 所以带着重启标记进来的实例得给老实例留一点退出的时间。不等的话用户看到的会是
    /// 「点了重启、UAC 也过了，程序却弹一句『已经在运行了』然后没了」。
    /// </summary>
    private bool AcquireSingleInstance(string[] args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);
        if (isFirst) return true;
        if (!args.Contains(Elevation.RestartArgument, StringComparer.OrdinalIgnoreCase)) return false;

        try
        {
            return _instanceMutex.WaitOne(RestartHandoverTimeout);
        }
        catch (AbandonedMutexException)
        {
            // 老实例是直接 Dispose 掉互斥体的，没有 ReleaseMutex，所以正常路径下等到的
            // 就是「已废弃」这一支 —— 它同样意味着名额已经归我们了
            return true;
        }
    }

    /// <summary>
    /// 设了「以管理员权限运行」却没提权，就在这儿掉头重来。返回 true 表示正在退出。
    ///
    /// 摆在启动最前面：托盘图标、热键、OCR 引擎都还没建起来，此刻退出的代价最小，
    /// 用户看到的也只是一次 UAC，而不是图标闪一下又没了。
    /// </summary>
    private bool ElevateAtStartup(string[] args)
    {
        // 带着标记回来却还是没提权，说明这台机器上就是提不上去（UAC 关着、账户不是管理员）。
        // 再拉一次就是没完没了的自我重启，所以到此为止，本次先以普通权限跑着。
        if (args.Contains(Elevation.RestartArgument, StringComparer.OrdinalIgnoreCase)) return false;

        // 用户在 UAC 上点了取消也走这一支：不改设置，下次启动还会再问一次 ——
        // 他要是不想再被问，把通用设置里那个开关关掉就是了
        if (!Elevation.Restart(out string? error))
        {
            _startupNotice = error;
            return false;
        }

        Shutdown();
        return true;
    }

    /// <summary>
    /// 每截一次图就写一遍。攒到退出时再写是不行的 —— 进程被任务管理器结束、
    /// 或者跟着关机一起没掉，攒着的那一批就全丢了，而「重启之后历史空了」
    /// 恰恰是这个功能最不能出的岔子。文件只有一两 KB，写它的代价可以忽略。
    /// </summary>
    private void SaveHistory()
    {
        var history = _controller!.History;
        HistoryStore.SaveIndex(history.Items);
        // 索引里没有的画面就是垃圾：容量调小、条目被挤掉都会留下这种文件，
        // 而用户是按条数设的上限，不会想到磁盘上还压着几十张
        HistoryStore.PruneImages(history.ImageIds());
    }

    /// <summary>
    /// 换历史存放目录：文件搬过去，内存里那份也照新目录重装一遍。
    ///
    /// 重装是必要的 —— 用户可能把路径指到一个存过历史的目录上，那儿的索引和内存里
    /// 这一份是两码事。搬失败就停在原地，不动内存，界面上的历史仍旧对得上磁盘。
    /// </summary>
    private void MoveHistoryTo(string directory)
    {
        if (string.Equals(HistoryStore.Directory, directory, StringComparison.OrdinalIgnoreCase)) return;

        if (HistoryStore.Relocate(directory) is { } error)
        {
            ShowTrayWarning(error);
            return;
        }

        _controller?.History.Restore(HistoryStore.Load());
        HistoryStore.PruneImages(_controller?.History.ImageIds() ?? []);
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
        if (_controller is not null)
        {
            _controller.Defaults = _settings.ToCaptureDefaults();
            _controller.ScrollOptions = _settings.ToScrollOptions();
            // 调小了就当场裁掉多余的那几条，不必等下一次截图
            _controller.History.Capacity = _settings.HistoryCapacity;
        }

        CreateEngines();
        _translationCache?.Clear();

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

        _pins.Create(image, PlacePinned(image));
    }

    /// <summary>
    /// 决定一张要贴的图落在哪：
    /// 正是上一次截的那张（剪贴板为空退回、或剪贴板里那份尺寸对得上）→ 回到截图时的原位，
    /// 视觉上是把画面「冻」在原来的地方，这才是贴图的语义；其它来源（别处复制的图、文本）
    /// 没有「原位」可言，落在光标手边最省事。
    ///
    /// 用尺寸判定「是不是上次那张」：剪贴板退回的那份是同一对象；Copy 截图后剪贴板里那份
    /// 是重新解码的，只能靠尺寸对上。误判的代价很低 —— 刚好和上次截图同尺寸的外部图片，
    /// 贴回原位而已。
    /// </summary>
    private PixelRect PlacePinned(BitmapSource image)
    {
        bool fromLastCapture = _lastCapture is not null
            && image.PixelWidth == _lastCapture.PixelWidth
            && image.PixelHeight == _lastCapture.PixelHeight;
        return fromLastCapture ? _lastCaptureBounds : PlaceUnderCursor(image);
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
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => _controller?.Start();
    }

    /// <summary>
    /// 托盘图标：按系统当前的小图标尺寸从 .ico 里现挑一档。
    ///
    /// 不写死 16×16 —— 那是 100% 缩放下的尺寸，125% 要 20、150% 要 24、200% 要 32。
    /// 挑错档的后果不是小一点，是被拉着放大，一个十几像素的图标糊起来格外明显。
    /// 读不出来就退回系统图标：托盘里没有图标等于程序在用户眼里消失了，
    /// 而这条路上没有任何值得为此不启动的理由。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/XkScreenshot.ico"));
            if (resource is null) return SystemIcons.Application;

            using var stream = resource.Stream;
            return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>
    /// 切到另一个权限高度。令牌是进程启动时定死的，改不了，只能整个重来一次。
    /// 返回 true 表示新实例已经在路上、这个进程正在退出，调用方别再做别的事。
    /// </summary>
    private bool SwitchElevation(bool toAdmin)
    {
        string? error;
        if (toAdmin)
        {
            if (Elevation.Restart(out error)) { Shutdown(); return true; }
        }
        else
        {
            // explorer 代拉的那个实例没法带上握手参数，只能这边先腾位置，
            // 它进门时才不会把自己判成重复运行
            ReleaseSingleInstance();
            if (Elevation.RestartUnelevated(out error)) { Shutdown(); return true; }

            // 没走成就把名额收回来，否则这个进程会一直空着位置，谁都能再开一个
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _);
        }

        // error 为 null = 用户在 UAC 上点了取消。是他自己撤销的动作，不用再提示一次，
        // 但设置里那个开关得跟着退回来 —— 留着它，界面上就写着一个并没有生效的状态
        if (error is not null) ShowTrayWarning(error);
        _settings.RunAsAdmin = Elevation.IsElevated;
        SettingsStore.Save(_settings);
        return false;
    }

    /// <summary>交出单实例名额。只有降权重启会用到，见 <see cref="SwitchElevation"/>。</summary>
    private void ReleaseSingleInstance()
    {
        if (_instanceMutex is null) return;

        try { _instanceMutex.ReleaseMutex(); }
        catch (ApplicationException) { /* 本来就没握着，那正是想要的结果 */ }

        _instanceMutex.Dispose();
        _instanceMutex = null;
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

                MoveHistoryTo(_settings.ResolveHistoryDirectory());

                // 权限得先存后换：换权限意味着这个进程马上就没了，没存的东西全跟着一起没
                if (_settings.RunAsAdmin != Elevation.IsElevated
                    && SwitchElevation(_settings.RunAsAdmin))
                    return;
            }

            // 取消也要走一遍：关窗时热键框可能正握着焦点、热键正让出去着，不装回来就再也回不来了
            ApplySettings();
        };
        window.Show();
        window.Activate();
    }

    private void CreateEngines()
    {
        string modelRoot = _settings.ResolveModelsDirectory();
        string paddleOcrDir = System.IO.Path.Combine(modelRoot, "paddleocr");
        string bergamotDir = System.IO.Path.Combine(modelRoot, BergamotModelDir.FolderName);

        // OCR engine
        (_ocrEngine as IDisposable)?.Dispose();
        if (_settings.Recognition.Mode == OcrMode.Online)
        {
            var config = new LlmApiConfig(
                _settings.Translation.ApiProtocol == ApiProtocolSetting.Anthropic
                    ? ApiProtocol.Anthropic : ApiProtocol.OpenAI,
                _settings.Translation.ApiBase,
                _settings.Translation.ApiKey,
                _settings.Translation.Model);
            _llmClient = new LlmApiClient();
            _ocrEngine = new LLMOcrEngine(_llmClient, config);
        }
        else
        {
            if (System.IO.Directory.Exists(paddleOcrDir))
            {
                try
                {
                    _ocrEngine = new PaddleOcrEngine(paddleOcrDir);
                }
                catch (Exception ex)
                {
                    ShowTrayWarning("PaddleOCR 初始化失败：" + ex.Message);
                }
            }
        }

        // Translation engine
        (_translator as IDisposable)?.Dispose();
        if (_settings.Translation.Mode == OcrMode.Online)
        {
            _llmClient ??= new LlmApiClient();
            var config = new LlmApiConfig(
                _settings.Translation.ApiProtocol == ApiProtocolSetting.Anthropic
                    ? ApiProtocol.Anthropic : ApiProtocol.OpenAI,
                _settings.Translation.ApiBase,
                _settings.Translation.ApiKey,
                _settings.Translation.Model);
            _translator = new LLMTranslator(_llmClient, config);
        }
        else
        {
            try
            {
                // 能翻哪些语种由目录里装了什么决定，不另外记一份清单 ——
                // 记了就会跟磁盘上的实际情况漂移，而漂移的那一刻正好是用户要用的时候
                _translator = new BergamotTranslator(bergamotDir);
            }
            catch
            {
                // 一个语种都没下，translator 保持 null，真去翻的时候提示用户去下
            }
        }
    }

    private async Task RunOcrAsync(BitmapSource image)
    {
        if (_ocrEngine is null)
        {
            ShowTrayWarning("OCR 引擎未就绪。请检查模型文件或在线 API 配置。");
            return;
        }

        var window = new TextResultWindow(image, "文字识别结果");
        window.Show();

        try
        {
            var lines = await _ocrEngine.RecognizeAsync(image);
            var text = string.Join(Environment.NewLine, lines.Select(l => l.Text));

            window.ShowResult(text);
        }
        catch (Exception ex)
        {
            window.Close();
            ShowTrayWarning("OCR 失败：" + ex.Message);
        }
    }

    private async Task RunTranslateAsync(BitmapSource image)
    {
        if (_ocrEngine is null)
        {
            ShowTrayWarning("OCR 引擎未就绪。");
            return;
        }
        if (_translator is null)
        {
            ShowTrayWarning("翻译引擎未就绪。请检查模型文件或在线 API 配置。");
            return;
        }

        var window = new TextResultWindow(image, "翻译结果");
        window.Show();

        try
        {
            // 第一阶段：OCR
            window.ShowLoading("识别中...");
            var lines = await _ocrEngine.RecognizeAsync(image);
            var sourceText = string.Join(Environment.NewLine, lines.Select(l => l.Text));

            // 第二阶段：定语种。先判原文是什么，再据此列出翻得到的目标。
            // 在线引擎不报语种 —— 翻译那件事它整个交给了大模型 —— 但判语种本来就不依赖引擎，
            // 离线引擎多做的只是拿「装了哪些模型」在同种文字的几个候选之间消歧。
            // 不判就只能在界面上写「检测到 原文」，那句话什么也没告诉用户
            var catalog = _translator as ILanguageCatalog;
            string source = catalog?.DetectLanguage(sourceText)
                ?? ScriptLanguage.Detect(sourceText)
                ?? "auto";
            var targets = TargetOptionsFor(catalog, source);

            // 认得出是什么语言、但没装它的模型。这时候不能挑一个别的语种硬翻 ——
            // 翻出来的东西没人看得懂，用户还以为是翻译坏了，而真正该做的只是去下个模型
            if (targets.Count == 0)
            {
                window.ShowResult(
                    $"检测到{catalog!.DisplayName(source)}，但没有装它的离线翻译模型。\n"
                    + $"到「设置 → 识别 / 翻译 → 离线翻译语言」里下载{catalog.DisplayName(source)}即可。\n\n"
                    + "───────── 识别到的原文 ─────────\n" + sourceText);
                return;
            }

            string target = DefaultTargetFor(source, targets);

            // 第三阶段：翻译
            window.ShowLoading("翻译中...");
            window.ShowResult(await TranslateCached(sourceText, source, target));

            window.SetupTargetLanguage(
                // 一个字形都没认出来（纯数字、纯符号）时给 null，界面上就只剩「译成」——
                // 报一个猜的语种出来，用户会以为程序看懂了
                source == "auto" ? null : BergamotCatalog.DisplayName(source),
                targets,
                target,
                async code =>
                {
                    try
                    {
                        window.ShowLoading("翻译中...");
                        window.ShowResult(await TranslateCached(sourceText, source, code));
                    }
                    catch (Exception ex)
                    {
                        window.ShowResult("翻译失败：" + ex.Message);
                    }
                });
        }
        catch (Exception ex)
        {
            window.Close();
            ShowTrayWarning("翻译失败：" + ex.Message);
        }
    }

    private async Task<string> TranslateCached(string text, string source, string target)
    {
        if (_translationCache?.Get(text, source, target) is { } hit) return hit;

        string translated = await _translator!.TranslateAsync(text, source, target);
        _translationCache?.Set(text, source, target, translated);
        return translated;
    }

    /// <summary>
    /// 在线引擎答不上「你能翻成什么」，给它一份常用语种垫着；
    /// 离线引擎则严格按装了什么列，免得下拉里出现一选就报错的项。
    /// </summary>
    private static IReadOnlyList<TextResultWindow.TargetOption> TargetOptionsFor(
        ILanguageCatalog? catalog, string source)
    {
        var codes = catalog?.TargetsFrom(source)
            ?? (string[])["zh", "en", "ja", "ko", "fr", "de", "es", "ru"];

        return [.. codes
            .Where(c => c != source)
            .Select(c => new TextResultWindow.TargetOption(c, BergamotCatalog.DisplayName(c)))];
    }

    /// <summary>
    /// 默认译成简体中文；原文已经是简体的话译成英文 —— 把简体翻成简体没有意义，
    /// 而这时候人多半是想知道「这句英文该怎么说」。
    ///
    /// 繁体走的是「译成简体」这一支而不是「译成英文」：简繁虽然都算中文，
    /// 但会去截一段繁体来翻的人，要的通常就是把它变成看着顺眼的简体。
    /// </summary>
    private static string DefaultTargetFor(
        string source, IReadOnlyList<TextResultWindow.TargetOption> targets)
    {
        string[] preferred = source switch
        {
            "zh" => ["en"],
            "zh_hant" => ["zh", "en"],
            _ => ["zh", "zh_hant"],
        };

        return preferred.FirstOrDefault(p => targets.Any(t => t.Code == p))
            ?? targets.FirstOrDefault()?.Code
            ?? "en";
    }

    private async void OnCaptured(CaptureResult result)
    {
        _lastCapture = result.Image;
        _lastCaptureBounds = result.Bounds;

        switch (result.Action)
        {
            case CaptureAction.Ocr:
                await RunOcrAsync(result.Image);
                break;

            case CaptureAction.Translate:
                await RunTranslateAsync(result.Image);
                break;

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
        _controller?.AbortScroll();
        (_ocrEngine as IDisposable)?.Dispose();
        (_translator as IDisposable)?.Dispose();
        _llmClient?.Dispose();
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
