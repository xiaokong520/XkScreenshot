using System;
using System.Globalization;
using System.IO;

namespace XkScreenshot.App.Output;

/// <summary>
/// GIF 的落盘。
///
/// 和截图不一样，GIF **不弹保存对话框**：一段录制少则几秒、多则一两分钟，用户在对话框上
/// 点一下取消，这段就彻底没了 —— 而截图取消保存还能重截一张。所以一律直接写进「保存」页
/// 指定的目录，文件名同样是「前缀_时间.gif」；回执上给一个可点的条目，点一下就能在
/// 资源管理器里看到它。真想换地方的，去设置里改目录就是了。
///
/// 命名规则与 <see cref="ImageSaver"/> 保持一致，只是扩展名换成 .gif —— 两套规则不一致的话，
/// 同一个目录里截图和录屏的文件名会一眼看出是两次写的。
/// </summary>
public static class GifSaver
{
    public static string SaveInto(byte[] data, string directory, string prefix)
    {
        Directory.CreateDirectory(directory);

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"{prefix}_{stamp}.gif");

        // 同一秒内录两段是会撞名的；撞了往后加序号，绝不覆盖上一段
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{prefix}_{stamp}_{n}.gif");

        File.WriteAllBytes(path, data);
        return path;
    }
}
