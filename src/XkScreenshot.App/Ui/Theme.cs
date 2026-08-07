using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Ui;

/// <summary>
/// 常规窗口（目前只有设置界面）的配色。
///
/// 跟随系统的浅色/深色，而不是钉死一套：设置界面是唯一一个「像普通程序」的窗口，
/// 它跟系统对不上的时候，突兀感比配色好看与否明显得多。覆盖层不用这套 ——
/// 它盖在冻结画面上，永远是深色才看得清底下的内容。
///
/// 颜色以资源字典的形式挂到窗口上，控件模板一律 DynamicResource 引用，
/// 这样一份模板同时管两套皮。
/// </summary>
public static class Theme
{
    /// <summary>品牌强调色，和覆盖层的选框、控制点是同一个蓝。</summary>
    private static readonly Color BrandAccent = Color.FromRgb(0x3B, 0x9E, 0xFF);

    /// <summary>浅色底上强调色要压深一档，不然填充按钮上的白字看不清。</summary>
    private static readonly Color BrandAccentDeep = Color.FromRgb(0x1B, 0x74, 0xD4);

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // 键不存在（企业策略、老系统）时当浅色，那是 Windows 的默认
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把一整套颜色画刷灌进窗口资源。</summary>
    public static void Apply(FrameworkElement target, bool dark)
    {
        var r = target.Resources;

        if (dark)
        {
            Put(r, "PageBg", 0x20, 0x20, 0x20);
            Put(r, "NavBg", 0x26, 0x26, 0x26);
            Put(r, "NavHover", 0x2F, 0x2F, 0x2F);
            Put(r, "NavSelected", 0x38, 0x38, 0x38);
            Put(r, "CardBg", 0x2B, 0x2B, 0x2B);
            Put(r, "CardBgHover", 0x30, 0x30, 0x30);
            Put(r, "CardBorder", 0x3A, 0x3A, 0x3A);
            Put(r, "FlyoutBg", 0x2C, 0x2C, 0x2C);
            Put(r, "FooterBg", 0x1C, 0x1C, 0x1C);
            Put(r, "ControlBg", 0x36, 0x36, 0x36);
            Put(r, "ControlBgHover", 0x3D, 0x3D, 0x3D);
            Put(r, "ControlBgPressed", 0x32, 0x32, 0x32);
            Put(r, "ControlBorder", 0x48, 0x48, 0x48);
            Put(r, "Text", 0xF0, 0xF1, 0xF3);
            Put(r, "TextSecondary", 0x9C, 0xA3, 0xAD);
            // 冲突提示用琥珀而不是红：热键被占用是「这样设不会生效」，不是「你错了」
            Put(r, "Warn", 0xF2, 0xB0, 0x5E);
            Put(r, "Accent", BrandAccent);
            Put(r, "AccentHover", 0x59, 0xAE, 0xFF);
            Put(r, "AccentPressed", 0x2F, 0x86, 0xDB);
            Put(r, "ToggleOffTrack", 0x00, 0x00, 0x00, 0x00);
            Put(r, "ToggleOffBorder", 0x8C, 0x93, 0x9C);
            Put(r, "ToggleOffThumb", 0xC8, 0xCD, 0xD4);
            Put(r, "ScrollThumb", 0x5A, 0x5A, 0x5A);
        }
        else
        {
            Put(r, "PageBg", 0xF3, 0xF3, 0xF3);
            Put(r, "NavBg", 0xFA, 0xFA, 0xFA);
            Put(r, "NavHover", 0xEC, 0xEC, 0xEC);
            Put(r, "NavSelected", 0xE3, 0xE3, 0xE3);
            Put(r, "CardBg", 0xFF, 0xFF, 0xFF);
            Put(r, "CardBgHover", 0xFA, 0xFA, 0xFA);
            Put(r, "CardBorder", 0xE5, 0xE5, 0xE5);
            Put(r, "FlyoutBg", 0xFC, 0xFC, 0xFC);
            Put(r, "FooterBg", 0xEE, 0xEE, 0xEE);
            Put(r, "ControlBg", 0xFD, 0xFD, 0xFD);
            Put(r, "ControlBgHover", 0xF4, 0xF4, 0xF4);
            Put(r, "ControlBgPressed", 0xED, 0xED, 0xED);
            Put(r, "ControlBorder", 0xD6, 0xD6, 0xD6);
            Put(r, "Text", 0x1B, 0x1B, 0x1B);
            Put(r, "TextSecondary", 0x5F, 0x66, 0x6E);
            Put(r, "Warn", 0x9A, 0x5B, 0x05);
            Put(r, "Accent", BrandAccentDeep);
            Put(r, "AccentHover", 0x2E, 0x84, 0xE0);
            Put(r, "AccentPressed", 0x17, 0x62, 0xB4);
            Put(r, "ToggleOffTrack", 0x00, 0x00, 0x00, 0x00);
            Put(r, "ToggleOffBorder", 0x86, 0x86, 0x86);
            Put(r, "ToggleOffThumb", 0x5D, 0x5D, 0x5D);
            Put(r, "ScrollThumb", 0xC2, 0xC2, 0xC2);
        }
    }

    /// <summary>
    /// 让系统把标题栏也画成深色。深色窗体配一条亮白标题栏是最扎眼的一种半吊子深色模式。
    /// 必须在窗口有句柄之后调用（SourceInitialized 之后）。
    /// </summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = dark ? 1 : 0;
        // 老版本 Windows 不认这个属性，返回非 0 即可，没有副作用
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private static void Put(ResourceDictionary r, string key, byte red, byte green, byte blue)
        => Put(r, key, Color.FromRgb(red, green, blue));

    private static void Put(ResourceDictionary r, string key, byte a, byte red, byte green, byte blue)
        => Put(r, key, Color.FromArgb(a, red, green, blue));

    private static void Put(ResourceDictionary r, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }
}
