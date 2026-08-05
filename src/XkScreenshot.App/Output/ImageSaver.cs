using System;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace XkScreenshot.App.Output;

public static class ImageSaver
{
    /// <summary>弹保存对话框。返回实际保存路径，用户取消则返回 null。</summary>
    public static string? SaveAs(BitmapSource image, string defaultDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|BMP 图片 (*.bmp)|*.bmp",
            FileName = SuggestFileName(),
            InitialDirectory = Directory.Exists(defaultDirectory) ? defaultDirectory : null,
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return null;

        Save(image, dialog.FileName);
        return dialog.FileName;
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

    private static string SuggestFileName()
        => "Snip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
}
