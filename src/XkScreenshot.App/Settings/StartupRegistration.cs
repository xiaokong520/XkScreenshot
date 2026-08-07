using System;
using Microsoft.Win32;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 开机自启。走 HKCU 的 Run 键而不是任务计划或启动文件夹：
/// 前者不需要管理员权限，后者会在资源管理器里留下一个用户可能不认识的快捷方式。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XkScreenshot";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>返回错误信息，null 表示成功。</summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "无法打开自启动注册表项";

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            // 路径里有空格是常态（Program Files），不加引号系统会把它拆成命令 + 参数
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return "取不到当前程序路径，无法设置开机自启";

            key.SetValue(ValueName, '"' + path + '"');
            return null;
        }
        catch (Exception ex)
        {
            return "设置开机自启失败：" + ex.Message;
        }
    }
}
