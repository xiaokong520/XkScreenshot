using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Core.Native;
using XkScreenshot.Scroll;

namespace XkScreenshot.App.Settings;

/// <summary>一个可持久化的热键。<see cref="HotkeyBinding"/> 带着业务名字，不适合直接落盘。</summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>不设热键。全局热键会把那个键从整个系统里抢走，允许关掉某一项是必要的。</summary>
    public static readonly HotkeySpec None = new(HotkeyModifiers.None, 0);

    /// <summary>0x70 = VK_F1。</summary>
    public static readonly HotkeySpec CaptureDefault = new(HotkeyModifiers.None, 0x70);

    /// <summary>0x72 = VK_F3。</summary>
    public static readonly HotkeySpec PinDefault = new(HotkeyModifiers.None, 0x72);

    public bool IsSet => VirtualKey != 0;

    public HotkeyBinding ToBinding(string name) => new(name, Modifiers, VirtualKey);

    public override string ToString() => IsSet ? ToBinding(string.Empty).ToString() : "未设置";
}

public sealed class AppSettings
{
    public const string DefaultPrefix = "XkScreenshot";

    public HotkeySpec CaptureHotkey { get; set; } = HotkeySpec.CaptureDefault;

    /// <summary>把剪贴板里的图钉到屏幕上。</summary>
    public HotkeySpec PinHotkey { get; set; } = HotkeySpec.PinDefault;

    /// <summary>留空 = 系统「图片」文件夹。存空串而不是当场解析，换了机器/换了用户配置才不会带着旧路径走。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>true = 保存时直接落进 <see cref="SaveDirectory"/>，不弹对话框。</summary>
    public bool SaveWithoutPrompt { get; set; }

    public string FileNamePrefix { get; set; } = DefaultPrefix;

    /// <summary>Enter / 双击选区的去向。工具条上那三个按钮各说各的，不受这一项影响。</summary>
    public CaptureAction DefaultAction { get; set; } = CaptureAction.Copy;

    /// <summary>截图时默认展开快捷键提示面板。用熟了的人会想关掉它。</summary>
    public bool ShowHints { get; set; } = true;

    /// <summary>截图时默认用控件级检测而不是整窗。</summary>
    public bool ElementMode { get; set; }

    /// <summary>回溯截屏历史缓存多少条。每条含一张整屏画面，0 = 关掉。</summary>
    public int HistoryCapacity { get; set; } = CaptureHistory.DefaultCapacity;

    /// <summary>
    /// 截屏历史存到哪儿。留空 = <see cref="HistoryStore.DefaultDirectory"/>。
    ///
    /// 值得单独拿出来给用户改：每条历史压着一张整屏 PNG，几十条就是几百兆，
    /// 而 %APPDATA% 在系统盘上，系统盘紧张的机器上这一项迟早会硌着人。
    /// </summary>
    public string HistoryDirectory { get; set; } = string.Empty;

    // ---------------- 长截图 ----------------

    /// <summary>长截图默认滚动方式：自动（程序发滚轮）或手动（用户自己滚）。</summary>
    public ScrollMode ScrollMode { get; set; } = ScrollOptions.Standard.Mode;

    /// <summary>长截图最大高度（像素），防内存爆掉。</summary>
    public int ScrollMaxHeight { get; set; } = ScrollOptions.Standard.MaxHeight;

    public bool RunAtStartup { get; set; }

    /// <summary>以管理员权限运行。切换它要重启整个进程 —— 令牌在进程启动时就定死了。</summary>
    public bool RunAsAdmin { get; set; }

    // ---------------- 文字识别与翻译 ----------------

    /// <summary>离线模型存放目录。留空 = 软件根目录下的 models/。</summary>
    public string ModelsDirectory { get; set; } = string.Empty;

    public RecognitionSettings Recognition { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();

    /// <summary>
    /// <see cref="SaveDirectory"/> 留空时实际用的目录。
    ///
    /// 留空存的是空串而不是这个路径：存死了，用户哪天把系统的「图片」文件夹搬到别的盘，
    /// 存图还留在老地方，而他明明什么都没改过。
    /// </summary>
    public static string DefaultSaveDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>实际写文件的目录。设置里留空就用系统「图片」文件夹。</summary>
    public string ResolveSaveDirectory()
        => string.IsNullOrWhiteSpace(SaveDirectory) ? DefaultSaveDirectory : SaveDirectory;

    /// <summary>截屏历史实际落盘的目录。设置里留空就用默认那个。</summary>
    public string ResolveHistoryDirectory()
        => string.IsNullOrWhiteSpace(HistoryDirectory) ? HistoryStore.DefaultDirectory : HistoryDirectory;

    /// <summary>
    /// 清洗过的文件名前缀。用户在设置里能敲进任何字符，其中 \ / : * ? " &lt; &gt; | 会让
    /// 保存直接抛异常 —— 与其在保存那一刻失败，不如在这里悄悄换成下划线。
    /// </summary>
    public string ResolveFileNamePrefix()
    {
        string prefix = FileNamePrefix;
        if (string.IsNullOrWhiteSpace(prefix)) return DefaultPrefix;

        foreach (char c in Path.GetInvalidFileNameChars())
            prefix = prefix.Replace(c, '_');
        return prefix.Trim();
    }

    /// <summary>会话起手用的那三项。</summary>
    public CaptureDefaults ToCaptureDefaults() => new(DefaultAction, ShowHints, ElementMode);

    /// <summary>长截图起手用的参数。Sanitized 会把越界的值拉回来。</summary>
    public ScrollOptions ToScrollOptions() => new(ScrollMode, 1, ScrollMaxHeight);

    /// <summary>
    /// <see cref="ModelsDirectory"/> 留空时找模型的目录：程序目录下的 models/。
    /// 开发阶段先往上走到 sln 根目录 —— 不然每次重新生成，模型都得跟着 bin 目录再下一遍。
    /// </summary>
    public static string DefaultModelsDirectory
    {
        get
        {
            string baseDir = AppContext.BaseDirectory;
            var dir = new System.IO.DirectoryInfo(baseDir);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XkScreenshot.sln")))
                dir = dir.Parent;
            return System.IO.Path.Combine(dir?.FullName ?? baseDir, "models");
        }
    }

    /// <summary>找模型文件的实际目录。</summary>
    public string ResolveModelsDirectory()
        => string.IsNullOrWhiteSpace(ModelsDirectory) ? DefaultModelsDirectory : ModelsDirectory;

    /// <summary>给设置界面改着玩的副本。引用类型字段也要逐层克隆。</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Recognition = new RecognitionSettings { Mode = Recognition.Mode };
        c.Translation = new TranslationSettings
        {
            Mode = Translation.Mode,
            ApiProtocol = Translation.ApiProtocol,
            ApiBase = Translation.ApiBase,
            ApiKey = Translation.ApiKey,
            Model = Translation.Model,
        };
        return c;
    }
}

// ---------------- 文字识别与翻译设置类型 ----------------

public enum OcrMode { Offline, Online }

public sealed class RecognitionSettings
{
    public OcrMode Mode { get; set; } = OcrMode.Offline;
}

public enum ApiProtocolSetting { OpenAI, Anthropic }

/// <summary>
/// 翻译设置。
///
/// 这里没有源语言和目标语言：源语言由引擎按文字系统判，目标语言在翻译结果窗口里现选 ——
/// 一次截图要翻成什么，是看着这张图才知道的事，存成全局设置反而每次都得先去改。
/// 装了哪些离线语种也不记在这儿，那是扫模型目录得出的，记一份只会跟磁盘漂移。
/// </summary>
public sealed class TranslationSettings
{
    public OcrMode Mode { get; set; } = OcrMode.Offline;
    public ApiProtocolSetting ApiProtocol { get; set; } = ApiProtocolSetting.Anthropic;
    public string ApiBase { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

/// <summary>
/// 设置的落盘位置与读写。
///
/// 读失败一律退回默认值而不是抛出去：配置文件损坏（断电、手改坏了）不该让整个程序起不来，
/// 那种情况下用户连打开设置界面改回来的机会都没有。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XkScreenshot", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                   ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    /// <summary>返回错误信息，null 表示写成功。</summary>
    public static string? Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return null;
        }
        catch (Exception ex)
        {
            return "设置保存失败：" + ex.Message;
        }
    }
}
