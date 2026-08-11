using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using XkScreenshot.App.Settings;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Ui;

/// <summary>
/// 常规窗口（设置界面、识别与翻译结果窗口）的配色。
///
/// 默认跟随系统的浅色/深色而不是钉死一套：这几个是仅有的「像普通程序」的窗口，
/// 跟系统对不上的时候，突兀感比配色好看与否明显得多。用户也可以在设置里锁死一档，
/// 见 <see cref="ThemeMode"/>。
///
/// 覆盖层上那几块浮动面板不用这一套：它们压在毛玻璃背景上，深浅是连背景亮度一起
/// 重映射出来的，另有一份 OverlayPalette。这里只管深浅怎么定，那边只管定了之后画成什么样。
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

    /// <summary>按设置里选的那一档给出深浅。跟随系统时现读一次注册表。</summary>
    public static bool IsDark(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => IsSystemDark(),
    };

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

    /// <summary>
    /// 把一整套颜色画刷灌进窗口资源。同一个元素可以反复调，用来当场换皮：
    /// 见 <see cref="Put(ResourceDictionary, string, Color)"/>。
    /// </summary>
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

        // 属性只是给 DWM 设了个标记，标题栏什么时候按新值重画由它自己定。开窗时那次
        // 重画紧接着就来，所以起手调一次就够；运行中途换主题却要等到窗口下次被激活
        // 之类的事情，用户看到的是「切了没反应，过一会儿自己变了」。
        //
        // 逼它当场重画：把窗口拉高一像素再放回来。DWM 是按窗框的尺寸出图的，
        // 尺寸一变就必须重出一张，那会儿它才会去读上面那个标记。
        //
        // 试过两条更省事的路，都不行：单给 SWP_FRAMECHANGED 只标脏非客户区，
        // 而标题栏压根不是窗口自己画的，DWM 不认；藏一下再显示确实立竿见影，
        // 但窗口一藏，任务栏按钮就被销毁，再显示时重建一个 —— 用户看到的是
        // 任务栏上的图标闪没了又冒出来一个新的，比慢半拍还醒目。
        //
        // 窗口还没显示出来的时候不动它：那会儿正走在开窗路上，尺寸归开窗流程管。
        if (!window.IsVisible) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        const uint keep = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE;

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, rect.Width, rect.Height + 1, keep);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, rect.Width, rect.Height, keep);
    }

    private static void Put(ResourceDictionary r, string key, byte red, byte green, byte blue)
        => Put(r, key, Color.FromRgb(red, green, blue));

    private static void Put(ResourceDictionary r, string key, byte a, byte red, byte green, byte blue)
        => Put(r, key, Color.FromArgb(a, red, green, blue));

    /// <summary>
    /// 换皮就是把这些键上的画刷整批换掉，字典一改，DynamicResource 那条路自己会重新解析。
    ///
    /// 所以界面上每一处颜色都必须走 DynamicResource（代码里是 SetResourceReference）。
    /// 谁要是把 FindResource 拿到的画刷直接赋给属性，换皮时它还攥着旧那个，一动不动。
    /// </summary>
    private static void Put(ResourceDictionary r, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }
}
