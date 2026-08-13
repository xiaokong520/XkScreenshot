using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using XkScreenshot.App.Overlay;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 截屏历史的落盘。
///
/// 目录结构：
/// <code>
///   &lt;程序目录&gt;\history\
///       index.json          顺序、选区、画面归属
///       0001.png            那一次截图时整个虚拟桌面的冻结画面
///       0002.png
/// </code>
///
/// 画面就是普通 PNG，没有自定义容器：出了问题双击就能看，用任何看图软件都能确认
/// 「存下来的到底是不是那一屏」。把 PNG 裹进私有格式只换来一件事 —— 排查时得先写个工具。
/// 索引单独一个 JSON，因为它要频繁重写，而那些 PNG 一旦写完就再也不动了。
///
/// 单独一个目录而不是塞进 settings.json 旁边：这里的东西按条增删，
/// 让它和用户自己敲的配置混在一个层级上，迟早会有人误删。
/// </summary>
public static class HistoryStore
{
    /// <summary>落盘形态。分开写而不是直接序列化 <see cref="HistoryEntry"/>，是为了不把内部类型的形状焊到文件格式上。</summary>
    private sealed record Row(int X, int Y, int W, int H, int DX, int DY, int DW, int DH, string? Image);

    /// <summary>没在设置里指定路径时用的目录：程序目录下的 history\。</summary>
    public static string DefaultDirectory => Path.Combine(AppSettings.AppRootDirectory, "history");

    /// <summary>
    /// 默认位置从前是这儿。升级上来的人历史还压在这个目录里，
    /// 见 <see cref="AdoptAppDataHistory"/>；更老的那份 history.json 也认这个位置。
    /// </summary>
    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XkScreenshot", "history");

    private static string _directory = DefaultDirectory;

    /// <summary>
    /// 当前落盘目录。改它之前先调 <see cref="Relocate"/> 把已经存下的东西搬过去 ——
    /// 光换指向的话，索引里那些 id 会在新目录里一个都找不到。
    /// </summary>
    public static string Directory
    {
        get => _directory;
        set => _directory = string.IsNullOrWhiteSpace(value) ? DefaultDirectory : value;
    }

    public static string IndexPath => Path.Combine(Directory, "index.json");

    private const string IndexName = "index.json";

    /// <summary>
    /// 把已经存下的历史搬到新目录，搬完再切过去。返回错误信息，null = 没出问题。
    ///
    /// 目标目录里已经有索引时一个文件都不搬，直接认那一份 —— 那说明用户指回了一个
    /// 存过历史的地方，硬搬过去就是两份索引撞在一起，留谁都得丢掉另一半。
    ///
    /// 画面先搬、索引最后搬：中途失败的话，老索引还在老目录里指着，
    /// 找不到的那几张画面按「只剩选区」处理，比一份指向半空目录的新索引好收拾。
    /// </summary>
    public static string? Relocate(string target)
    {
        string from = Directory;
        string to = string.IsNullOrWhiteSpace(target) ? DefaultDirectory : target;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            System.IO.Directory.CreateDirectory(to);

            if (!File.Exists(Path.Combine(to, IndexName)) && System.IO.Directory.Exists(from))
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(from, "*.png"))
                    File.Move(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

                string index = Path.Combine(from, IndexName);
                if (File.Exists(index)) File.Move(index, Path.Combine(to, IndexName), overwrite: true);
            }

            Directory = to;
            return null;
        }
        catch (Exception ex)
        {
            return "截图历史目录切换失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把 %APPDATA% 里那份历史接过来，只在用户没自己指过目录时调。
    /// 返回错误信息，null = 没出问题（包括「没什么可搬的」）。
    ///
    /// 默认位置从 %APPDATA% 挪到程序目录之后，升级上来的人一开机会发现历史一条不剩 ——
    /// 而他什么都没改过。东西其实还在老地方，但没人会去那儿找。
    ///
    /// 搬完老目录就空着不管了：里面可能还有别的版本留下的东西，
    /// 这里只认得 index.json 和那些 PNG，不该替用户删自己不认识的文件。
    /// </summary>
    public static string? AdoptAppDataHistory()
    {
        string from = AppDataDirectory;
        if (string.Equals(from, DefaultDirectory, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(Path.Combine(from, IndexName))) return null;

        // 借 Relocate 来搬：它那条「目标已经有索引就一个文件都不动」的规矩这里同样要 ——
        // 搬过一次之后老目录多半还在，不认这条的话每次开机都要来一遍
        Directory = from;
        return Relocate(DefaultDirectory);
    }

    /// <summary>
    /// 读回历史。索引里指着一个已经不在的 PNG 时，条目本身留着、画面置空 ——
    /// 选区还是有用的，没必要因为图丢了就把这一条一起丢掉。
    /// </summary>
    public static IReadOnlyList<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return LoadLegacy();

            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(IndexPath));
            if (rows is null) return [];

            var entries = new List<HistoryEntry>(rows.Count);
            foreach (var row in rows)
            {
                var bounds = new PixelRect(row.X, row.Y, row.W, row.H);
                if (bounds.IsEmpty) continue;

                var desktop = new PixelRect(row.DX, row.DY, row.DW, row.DH);
                // 画面文件没了就当这一条只有选区。桌面范围为空同理 ——
                // 没有原点就没法把那张图摆回虚拟屏幕坐标系里，图有等于没有。
                string? image = row.Image is not null && !desktop.IsEmpty && File.Exists(ImagePath(row.Image))
                    ? row.Image
                    : null;

                entries.Add(new HistoryEntry(bounds, desktop, image));
            }
            return entries;
        }
        catch (Exception)
        {
            // 手改坏了、写到一半断电了 —— 都不该让程序起不来
            return [];
        }
    }

    /// <summary>
    /// 上一版把历史存成一个只有矩形的 <c>history.json</c>，就放在设置文件旁边。
    /// 那些矩形照样有用，接过来当「只有框」的条目 —— 升级一次就把人家攒的历史清空，
    /// 是这个功能最不该干的事。接完就把旧文件删掉，免得下次又走一遍。
    /// </summary>
    private static IReadOnlyList<HistoryEntry> LoadLegacy()
    {
        // 认死 %APPDATA%：旧版本没有「换个地方存」这回事，那个文件只可能在这儿
        string legacy = Path.Combine(Path.GetDirectoryName(AppDataDirectory)!, "history.json");

        try
        {
            if (!File.Exists(legacy)) return [];

            var rows = JsonSerializer.Deserialize<List<LegacyRow>>(File.ReadAllText(legacy));
            var entries = new List<HistoryEntry>();
            foreach (var row in rows ?? [])
            {
                var bounds = new PixelRect(row.X, row.Y, row.W, row.H);
                if (!bounds.IsEmpty) entries.Add(new HistoryEntry(bounds, PixelRect.Empty, null));
            }

            SaveIndex(entries);
            File.Delete(legacy);
            return entries;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private sealed record LegacyRow(int X, int Y, int W, int H);

    public static void SaveIndex(IEnumerable<HistoryEntry> entries)
    {
        try
        {
            var rows = new List<Row>();
            foreach (var e in entries)
                rows.Add(new Row(
                    e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height,
                    e.Desktop.X, e.Desktop.Y, e.Desktop.Width, e.Desktop.Height,
                    e.Image));

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
        }
        catch (Exception)
        {
            // 见类注释：这条路上没有值得打断用户的失败
        }
    }

    public static string ImagePath(string id) => Path.Combine(Directory, id + ".png");

    /// <summary>
    /// 把整屏画面写成 PNG，返回它的 id；失败返回 null（调用方照样能只记选区）。
    /// 图必须已经 Freeze —— 这个方法是给后台线程调的。
    ///
    /// 拿到 id 的一方认领完之后要调 <see cref="ReleaseId"/>：在那之前这个号一直占着，
    /// <see cref="Renumber"/> 不会碰它，也不会有第二张图领到同一个号。
    /// </summary>
    public static string? SaveImage(BitmapSource image)
    {
        string? pending = null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            string id = pending = NextId();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            // 先写临时文件再改名：写到一半被打断的话，留下的是一个残缺的 .tmp，
            // 而不是一个索引正指着、打开却是半张的 PNG
            string tmp = ImagePath(id) + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, ImagePath(id), overwrite: true);
            return id;
        }
        catch (Exception)
        {
            // 没写成，这个号也就没人会来认领了，当场还回去
            if (pending is not null) ReleaseId(pending);
            return null;
        }
    }

    /// <summary>读一张存档画面；文件没了或者解不出来返回 null。</summary>
    public static BitmapSource? LoadImage(string id)
    {
        try
        {
            // OnLoad 会当场读完，出了 using 文件就不再被占用 —— 否则清理时删不掉
            using var fs = File.OpenRead(ImagePath(id));
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 删掉不在 keep 里的 PNG（含写了一半的 .tmp）。
    /// 容量裁剪和「索引与目录对不上」都靠它收尾 —— 只删索引不删文件的话，
    /// 目录会一直涨，而用户是按条数设的上限，不会想到磁盘上还压着几十张。
    /// </summary>
    public static void PruneImages(IEnumerable<string> keep)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var live = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && live.Contains(Path.GetFileNameWithoutExtension(file))) continue;

                try { File.Delete(file); } catch (Exception) { /* 正被看图软件占着，下次再说 */ }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 把画面文件按条目顺序重排成 0001 起的一段连号，返回「旧 id → 新 id」（新 id 为 null =
    /// 那张图没能挪过去，按丢了算）。返回空表示这一轮什么都不用动。
    ///
    /// 为什么要重排：容量裁剪只删最旧的那一条，号却是一路往上发的，攒一阵子目录里就成了
    /// 0178~0210 这样一段浮在半空的号。重排之后目录里永远是 0001 开头、按时间从旧到新的连号。
    ///
    /// 代价是稳态下每截一张都要把整批文件改一遍名（默认 30 个，上限 200 个）。
    /// 改名只动目录项、不搬数据，这个量级压在落盘那一步上是划算的。
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Renumber(IReadOnlyList<HistoryEntry> items)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        lock (IdLock)
        {
            // 有图正在后台编码就这一轮不动手：那个号已经发出去、文件还没落盘，
            // 此刻重排有可能正好把另一张图挪到它头上，等它写完就是两条指着同一张。
            // 不会一直拖着 —— 它落盘时会认领条目，那一下又会走到这儿来。
            if (Pending.Count > 0) return map;
        }

        // 下标越大越早，所以倒着数：最旧的拿 0001，最新的拿最大号。
        // 没有画面的条目不占号，免得目录里空出一个谁也用不上的号
        var moves = new List<(string Old, string New)>();
        int n = 0;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Image is not { } old) continue;

            string id = (++n).ToString("D4", CultureInfo.InvariantCulture);
            if (!id.Equals(old, StringComparison.OrdinalIgnoreCase)) moves.Add((old, id));
        }
        if (moves.Count == 0) return map;

        // 分两趟：先全挪到临时名，再从临时名落到目标名。一趟直接改是不行的 ——
        // 0002→0001 这种目标位置上正压着另一张同样要挪的图，一趟走就把它盖没了。
        // 临时名挂 .tmp 后缀，中途崩了留下的残骸下次由 PruneImages 顺手收走。
        var staged = new List<(string Temp, string New, string Old)>(moves.Count);
        foreach (var (old, id) in moves)
        {
            string temp = Path.Combine(Directory, old + ".ren.tmp");
            try
            {
                File.Move(ImagePath(old), temp, overwrite: true);
                staged.Add((temp, id, old));
            }
            catch (Exception)
            {
                // 文件没了，或者正被看图软件占着。这一张按丢了算，条目退回「只有框」——
                // 硬留着旧名字的话，它占的号正是别人的目标，下一趟就会被盖掉
                map[old] = null;
            }
        }

        foreach (var (temp, id, old) in staged)
        {
            try
            {
                // overwrite：目标位置上可能压着一张刚被裁掉、还没轮到 PruneImages 收的孤儿
                File.Move(temp, ImagePath(id), overwrite: true);
                map[old] = id;
            }
            catch (Exception)
            {
                map[old] = null;
            }
        }

        return map;
    }

    private static readonly object IdLock = new();

    /// <summary>号已经发出去、图还在后台编码路上的那些。</summary>
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 领一个新序号，取目录里现有的最大值加一。
    /// 不用时间戳：同一秒内连截两张就会撞名，而这里恰恰是连着截图的场景。
    ///
    /// 整段锁住、并且把发出去的号记进 <see cref="Pending"/> 直到落盘：编码是甩给后台线程做的，
    /// 连着截两张就有两个线程同时走到这儿，光看磁盘的话它们会拿到同一个号 ——
    /// 一个抢不到临时文件退化成「只有框」，或者后写的把先写的盖掉，历史里两条指着同一张图。
    /// </summary>
    private static string NextId()
    {
        lock (IdLock)
        {
            int n = 0;
            try
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.png"))
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(file),
                            NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max > n)
                        n = max;
                }
            }
            catch (Exception)
            {
            }

            string id;
            do { id = (++n).ToString("D4", CultureInfo.InvariantCulture); }
            while (!Pending.Add(id));
            return id;
        }
    }

    /// <summary>
    /// 交还 <see cref="SaveImage"/> 领走的号。
    ///
    /// 必须紧挨着认领那一步调、中间不能让出线程：还回去之后这个号就任由
    /// <see cref="Renumber"/> 处置了，隔着一次调度的话，图有可能在认领之前
    /// 就被别的条目盖掉，最后两条指着同一张。
    /// </summary>
    public static void ReleaseId(string id)
    {
        lock (IdLock) Pending.Remove(id);
    }
}
