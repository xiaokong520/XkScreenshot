using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Hotkeys;
using XkScreenshot.Core.Native;

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

    /// <summary>回溯截屏区域历史缓存多少条。0 = 关掉。</summary>
    public int HistoryCapacity { get; set; } = CaptureHistory.DefaultCapacity;

    public bool RunAtStartup { get; set; }

    /// <summary>实际写文件的目录。设置里留空就用系统「图片」文件夹。</summary>
    public string ResolveSaveDirectory()
        => string.IsNullOrWhiteSpace(SaveDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : SaveDirectory;

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

    /// <summary>给设置界面改着玩的副本。字段全是不可变类型，浅拷贝就够。</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
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
