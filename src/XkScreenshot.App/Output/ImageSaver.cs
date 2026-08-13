using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace XkScreenshot.App.Output;

public static class ImageSaver
{
    /// <summary>弹保存对话框。返回实际保存路径，用户取消则返回 null。</summary>
    public static string? SaveAs(BitmapSource image, string defaultDirectory, string prefix)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|BMP 图片 (*.bmp)|*.bmp",
            FileName = SuggestFileName(prefix),
            InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return null;

        Save(image, dialog.FileName);
        return dialog.FileName;
    }

    /// <summary>
    /// 直接存进目录，不打扰用户。
    ///
    /// 时间戳只精确到秒，连拍两张是会撞名的 —— 撞了就往后加序号，
    /// 而不是覆盖：默默盖掉用户上一秒截的那张，是没法挽回的。
    /// </summary>
    public static string SaveInto(BitmapSource image, string directory, string prefix)
    {
        Directory.CreateDirectory(directory);

        string stamp = Timestamp();
        string path = Path.Combine(directory, $"{prefix}_{stamp}.png");

        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{prefix}_{stamp}_{n}.png");

        Save(image, path);
        return path;
    }

    public static void Save(BitmapSource image, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(image));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// 在文件管理器里打开这张图所在的目录，并把它选中。
    ///
    /// 只开目录是不够的：自动保存的目录里攒着几十张名字只差几秒的截图，
    /// 让用户自己去那堆时间戳里认出刚才那张，等于没帮上忙。
    ///
    /// 托 explorer.exe 去开而不是 ShellExecute 目录：提权运行时，前者会把差事
    /// 转交给已经在跑的那个普通权限的 explorer，用户不会莫名多出一个管理员文件窗口。
    /// </summary>
    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 打不开就算了。这只是回执上顺手给的一下，为它弹个错误框太重了
        }
    }

    private static string SuggestFileName(string prefix) => $"{prefix}_{Timestamp()}.png";

    private static string Timestamp()
        => DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
}
