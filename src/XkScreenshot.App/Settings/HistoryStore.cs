using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 截屏区域历史的落盘。
///
/// 单独一个文件而不是塞进 settings.json：历史每截一次图就要重写一遍，而设置是用户自己
/// 敲进去的东西 —— 让一个高频写入去碰它，等于给「配置文件被写坏」多开一扇门。
///
/// 写失败一声不吭：这是截图流程的尾巴，为了一条历史没存上去弹个气泡，
/// 打扰的程度远超过这件事本身的分量。读失败退回空历史，同理。
/// </summary>
public static class HistoryStore
{
    /// <summary>落盘用的紧凑形态。直接序列化 PixelRect 会把 Right/Bottom/Area 那几个算出来的属性也写进去。</summary>
    private sealed record Entry(int X, int Y, int W, int H);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XkScreenshot", "history.json");

    public static IReadOnlyList<PixelRect> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
            if (entries is null) return [];

            var rects = new List<PixelRect>(entries.Count);
            foreach (var e in entries) rects.Add(new PixelRect(e.X, e.Y, e.W, e.H));
            return rects;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<PixelRect> rects)
    {
        try
        {
            var entries = new List<Entry>();
            foreach (var r in rects) entries.Add(new Entry(r.X, r.Y, r.Width, r.Height));

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception)
        {
            // 见类注释：这条路上没有值得打断用户的失败
        }
    }
}
