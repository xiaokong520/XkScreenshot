using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Core.Llm;
using XkScreenshot.Core.Native;
using XkScreenshot.Gif;
using XkScreenshot.Privacy;
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

    /// <summary>
    /// 把收起来的贴图重新摆出来。0x52 = VK_R，「Restore」。
    ///
    /// 本来和「隐藏」凑成一对 H（Ctrl+Shift+H），实测这台机器上它**已经被别的程序占着** ——
    /// RegisterHotKey 是先到先得，抢不到就等于这一项根本没有热键（只会弹一句失败提示）。
    /// 换成 Ctrl+Alt+R：和「隐藏」同一个 Ctrl+Alt 手势，两个键都好按。别改成
    /// Ctrl+Shift+S / Ctrl+Alt+V 这类：那是全局的「另存为」「选择性粘贴」，抢走之后
    /// 别的程序就按不动了，而这两个键在本程序里一点也不比 H 更好记。
    /// </summary>
    public static readonly HotkeySpec ShowAllPinsDefault
        = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52);

    /// <summary>
    /// 一次收起屏幕上所有贴图。0x48 = VK_H，「Hide」，和「显示」共用 Ctrl+Alt 这个手势。
    /// </summary>
    public static readonly HotkeySpec HideAllPinsDefault
        = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x48);

    /// <summary>
    /// 一次关掉屏幕上所有贴图。0x43 = VK_C，「Close」。
    ///
    /// 三个贴图动作共用 Ctrl+Alt 这一个手势（H 收、R 放、C 关），记的是一个动作而不是一个键位。
    /// 按错的代价不一样：H 收起来还能 R 回来，C 是按真删除走的 —— 所以刻意让它们不同字母，
    /// 别为了「一排好看」改成 H / H+Shift / H+Shift+Alt 那种一字之差。
    /// </summary>
    public static readonly HotkeySpec CloseAllPinsDefault
        = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43);

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

    /// <summary>
    /// 把「隐藏贴图 / 隐藏全部贴图」收起来的那批重新摆到屏幕上。
    ///
    /// 收起来之后屏幕上就没有任何东西能点了 —— 那些贴图右键够不着、托盘菜单也只在鼠标那边，
    /// 所以必须留一条纯键盘的回路把它们叫回来。它和「隐藏全部贴图」是一对：收起来容易，
    /// 叫回来才是不能失手的那一步。
    /// </summary>
    public HotkeySpec ShowAllPinsHotkey { get; set; } = HotkeySpec.ShowAllPinsDefault;

    /// <summary>
    /// 一次收起屏幕上所有贴图，和托盘菜单里那一项是同一个动作。
    ///
    /// 和「显示所有贴图」分成两项、而不是合成一个切换键：屏上常常是「一部分收着、一部分摆着」
    /// （谁都可以单独收起来一张），这时候「切换」到底该收还是该显示没有正确答案。
    /// 两个键各干各的，按下去的结果永远确定，也和托盘那两条命令一一对应。
    /// </summary>
    public HotkeySpec HideAllPinsHotkey { get; set; } = HotkeySpec.HideAllPinsDefault;

    /// <summary>
    /// 一次关掉屏幕上所有贴图，和托盘菜单里那一项是同一个动作。
    ///
    /// 它不像「隐藏」那样能反悔：关掉的贴图连存档里那份画面一起清掉，
    /// 「显示所有贴图」也叫不回来。所以它虽然也有默认热键（用户要的就是纯键盘能走完整套），
    /// 但主键故意和「隐藏」差一个字母 —— 手滑按错时，「收起来」和「删干净」的后果差得远。
    /// </summary>
    public HotkeySpec CloseAllPinsHotkey { get; set; } = HotkeySpec.CloseAllPinsDefault;

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

    /// <summary>
    /// 办成之后弹不弹那句回执（复制好了、存到哪儿了）。
    ///
    /// 默认不弹：截图这件事一天要做几十遍，每一遍都跳出来说一句「复制好了」，
    /// 那句话很快就从回执变成了噪音。想要的人自己去开。
    ///
    /// 只管「办成了」这一类。没办成的那几句不受它管 —— 用户按下热键正等着结果，
    /// 那种时候一声不吭，他只会以为程序坏了。
    /// </summary>
    public bool ShowToasts { get; set; }

    /// <summary>回溯截屏历史缓存多少条。每条含一张整屏画面。</summary>
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

    /// <summary>
    /// 重启之后把上次贴着的图重新摆回来：位置、大小、旋转角度、透明度都照旧。
    ///
    /// 默认关着。一个用惯了「随手贴、随手关」的人，不会指望程序退出时屏幕上的东西还被记着；
    /// 攒了几十张贴图的人更是如此 —— 下次开机一起涌出来，只会让他去点「关闭全部贴图」。
    /// 要它的人自己去开，见 <see cref="XkScreenshot.Pin.PinStore"/>。
    /// </summary>
    public bool RestorePins { get; set; }

    // ---------------- GIF 录制 ----------------

    /// <summary>
    /// 录制帧率。默认 10：界面演示够顺，文件也不至于大到发不出去。
    ///
    /// 帧率几乎是文件大小的线性杠杆 —— 同样的操作录 20 帧/秒，体积大致是 10 帧/秒的两倍，
    /// 而 GIF 的延迟以 10ms 为单位，20fps（5cs）再往上也没有更细的时间分辨率了。
    /// </summary>
    public int GifFps { get; set; } = GifOptions.DefaultFps;

    /// <summary>一次录制最长多少秒。到点自动停并保存前面的部分。</summary>
    public int GifMaxSeconds { get; set; } = GifOptions.DefaultMaxSeconds;

    /// <summary>
    /// 录制时把鼠标指针画进画面。
    ///
    /// 默认开：会去录 GIF 的场合多半是演示操作，看不见点在哪儿就少了一半信息。
    /// 只录界面变化的人可以关掉，画面会干净一些。
    /// </summary>
    public bool GifCaptureCursor { get; set; } = true;

    /// <summary>
    /// 画质档位：原始尺寸/256 色、1024/64 色、720/32 色。
    ///
    /// 默认「标准」而不是「原始」：GIF 的 LZW 对视频、照片几乎压不动，1080p 十秒就能到
    /// 十几兆，默认档先把尺寸和颜色各收一档，界面演示基本看不出差别。
    /// </summary>
    public GifQuality GifQuality { get; set; } = GifQuality.Balanced;

    public bool RunAtStartup { get; set; }

    /// <summary>以管理员权限运行。切换它要重启整个进程 —— 令牌在进程启动时就定死了。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>
    /// 亮色 / 暗色 / 跟随系统。管设置界面、识别与翻译结果窗口，以及截图时那几块浮动面板。
    ///
    /// 默认跟随系统，而且存的是「跟随」这个意思本身而不是当时算出来的那个值 ——
    /// 存死了的话，用户哪天在系统里切了深色，这边纹丝不动，而他明明什么都没改过。
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // ---------------- 文字识别与翻译 ----------------

    /// <summary>离线模型存放目录。留空 = 软件根目录下的 models/。</summary>
    public string ModelsDirectory { get; set; } = string.Empty;

    public RecognitionSettings Recognition { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();

    /// <summary>智能打码（马赛克选项行里那个盾牌、快捷键 P）找哪几类。</summary>
    public PrivacySettings Privacy { get; set; } = new();

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

    /// <summary>此刻该用深色还是浅色。跟随系统时现读一次注册表。</summary>
    public bool ResolveDark() => Ui.Theme.IsDark(Theme);

    /// <summary>
    /// 会话起手用的那几项。主题传的是模式而不是算好的深浅：覆盖层是每次截图现搭的，
    /// 到那会儿再解析，用户中途在系统里切了主题也能跟上，不用重开设置。
    /// </summary>
    public CaptureDefaults ToCaptureDefaults() => new(DefaultAction, ShowHints, ElementMode, Theme);

    /// <summary>长截图起手用的参数。Sanitized 会把越界的值拉回来。</summary>
    public ScrollOptions ToScrollOptions() => new(ScrollMode, ScrollMaxHeight);

    /// <summary>录制起手用的参数。同样在出口处夹一次，手改坏配置不会录出一堆 0 帧。</summary>
    public GifOptions ToGifOptions()
        => new GifOptions(GifFps, GifMaxSeconds, GifCaptureCursor, GifQuality).Sanitized();

    /// <summary>
    /// 程序根目录。跟着程序走、又不该塞进 %APPDATA% 的东西都放它下面：
    /// 模型（models/）和截屏历史（history/）。
    ///
    /// 开发阶段先往上走到 sln 根目录 —— 不然每次重新生成，这些东西都得跟着 bin 目录再来一遍。
    /// </summary>
    public static string AppRootDirectory
    {
        get
        {
            string baseDir = AppContext.BaseDirectory;
            var dir = new System.IO.DirectoryInfo(baseDir);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XkScreenshot.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? baseDir;
        }
    }

    /// <summary><see cref="ModelsDirectory"/> 留空时找模型的目录：程序目录下的 models/。</summary>
    public static string DefaultModelsDirectory
        => System.IO.Path.Combine(AppRootDirectory, "models");

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
        c.Privacy = Privacy with { };
        return c;
    }

    /// <summary>
    /// 智能打码这一次找哪几类。
    ///
    /// 在线模式只改「正文里的人名」这一段：文字和头像仍然靠本地 PaddleOCR 定位 ——
    /// 大模型 OCR 给不出行框，头像是拿文字行当锚点找的，走在线就全乱。
    /// 在线名字识别还要求「识别 / 翻译」页里填了 API Key：只选了在线却没配 Key，
    /// 每次都去撞一个必然失败的请求再提示一遍，毫无意义。
    /// </summary>
    public PrivacyOptions ToPrivacyOptions(LlmApiClient? client)
    {
        var p = Privacy;
        bool online = p.Mode == OcrMode.Online;
        LlmAccess? llm = null;
        if (online && client is not null && !string.IsNullOrWhiteSpace(Translation.ApiKey))
        {
            llm = new LlmAccess(client, new LlmApiConfig(
                Translation.ApiProtocol == ApiProtocolSetting.Anthropic ? ApiProtocol.Anthropic : ApiProtocol.OpenAI,
                Translation.ApiBase, Translation.ApiKey, Translation.Model));
        }

        // 一律写参数名：这个 record 的位置参数加过几次，按顺序传很容易把「头像」和「名字」弄反
        return new PrivacyOptions(
            Email: p.Email,
            Phone: p.Phone,
            IdCard: p.IdCard,
            BankCard: p.BankCard,
            IpAddress: p.IpAddress,
            Secret: p.Secret,
            Address: p.Address,
            Avatar: p.Avatar,
            Name: p.Name,
            Llm: llm,
            // 两种模式互斥：在线用大模型找名字，离线就自动用内置的分词词库
            LocalNames: !online,
            Online: online);
    }
}

/// <summary>智能打码各类隐私信息的开关。9 类全部默认开启，用户按需关掉自己不在意的。</summary>
public sealed record PrivacySettings
{
    public bool Email { get; set; } = true;
    public bool Phone { get; set; } = true;
    public bool IdCard { get; set; } = true;
    public bool BankCard { get; set; } = true;
    public bool IpAddress { get; set; } = true;
    public bool Secret { get; set; } = true;
    /// <summary>邮寄地址（省市区 + 街道门牌）。默认开：定位卡片、快递单上的地址就是要遮的东西。</summary>
    public bool Address { get; set; } = true;
    public bool Avatar { get; set; } = true;
    public bool Name { get; set; } = true;

    /// <summary>
    /// 打码的识别方式。离线全部本地（规则 + 内置分词词库）；在线只把「正文里的人名」
    /// 交给大模型判断，文字和头像仍然是本地 PaddleOCR 在定位（在线 OCR 给不出行框）。
    /// </summary>
    public OcrMode Mode { get; set; } = OcrMode.Offline;
}

/// <summary>界面配色。System = 跟着系统的浅色/深色走。</summary>
public enum ThemeMode { System, Light, Dark }

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
