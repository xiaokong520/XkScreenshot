using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Media.Imaging;

namespace XkScreenshot.Pin;

/// <summary>
/// 存档里的一张贴图：它当时在哪、多大、转过多少、多透、是不是置顶，外加画面文件叫什么。
///
/// 位置/倍率/角度一律存原始的 double，不存取整过的窗口矩形 —— 贴图的位置本来就只有
/// 下发那一刻才取整（见 PinForm._left），存成整数的话，一张摆在小数坐标上的贴图
/// 每重启一次就朝左上挪半个像素以内的一点点，攒几次能看出来。
/// </summary>
public sealed record PinSnapshot(
    double X, double Y, double Scale, double Angle, double Opacity, bool TopMost, string Image);

/// <summary>
/// 贴图存档的落盘。
///
/// 目录结构（和截屏历史那套是一个模子，见 App/Settings/HistoryStore.cs）：
/// <code>
///   &lt;程序目录&gt;\pins\
///       index.json      每张贴图的位置、倍率、角度、透明度、置顶
///       0001.png        贴图当时的画面
/// </code>
///
/// 画面依旧是普通 PNG、索引依旧单独一个 JSON：前者写完就再也不动，后者每动一下就重写一遍 ——
/// 一张贴图的画面可能有好几兆，为了记一次拖动去重新编码整幅图是不行的（那个索引只有一两 KB）。
///
/// 画面文件在**后台线程**上编码。一张整屏 PNG 要一两百毫秒，压在「按下热键就贴出来」那一下上，
/// 用户看到的是贴图迟迟不出来（截屏历史那边也是同样的理由，见 TODO.md「画面存盘整个排到
/// UI 空下来之后」）。所以号先占住、索引当场写下去，编码完再落盘 —— 中间这一小段里索引指着的
/// 文件还不存在，那不要紧：读的一方（<see cref="Load"/>）认不出文件时会把这一条丢掉，
/// 进程真就在这段里没了，丢的也只是那一张。
/// </summary>
public static class PinStore
{
    /// <summary>落盘形态。分开写而不是直接序列化 <see cref="PinSnapshot"/>，是为了不把内部类型的形状焊到文件格式上。</summary>
    private sealed record Row(double X, double Y, double Scale, double Angle, double Opacity, bool TopMost, string? Image);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 存档目录。由调用方指过来 ——「程序目录在哪」这件事归 App 管（那边已经有
    /// AppSettings.AppRootDirectory），这儿不重复一份。默认值只是个兜底。
    /// </summary>
    public static string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "pins");

    public static string IndexPath => Path.Combine(Directory, "index.json");

    public static string ImagePath(string id) => Path.Combine(Directory, id + ".png");

    private static readonly object IdLock = new();

    /// <summary>号已经发出去、画面还在后台编码路上的那些。见 <see cref="ReserveId"/>。</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在后台编码的画面张数。落盘收尾（<see cref="FlushPending"/>）和清理（<see cref="PruneImages"/>）都要看它。</summary>
    private static int _encoding;

    /// <summary>
    /// 领一个新序号，取目录里现有的最大值加一，外加已经发出去还没落盘的那些。
    ///
    /// 不用时间戳：连着贴两张就会撞名，而这里恰恰是连着截图贴图的场景。
    /// 整段锁住、并且把发出去的号记进 <see cref="Reserved"/> 直到落盘，是因为编码在后台线程上 ——
    /// 光看磁盘的话，两次「取最大值加一」会拿到同一个号。
    /// </summary>
    public static string ReserveId()
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
                // 目录还不存在。下面 SaveIndex / WriteImage 会把它建起来
            }

            string id;
            do { id = (++n).ToString("D4", CultureInfo.InvariantCulture); }
            while (!Reserved.Add(id));
            return id;
        }
    }

    /// <summary>
    /// 把画面写进存档。**给后台线程调**，图必须已经 Freeze（贴图在构造时就冻过了）。
    ///
    /// 失败就当没这一张：号还回去，那一张重启后不会回来，别的照旧。
    /// 调用方那边 `StoreId` 仍旧指着这个号，于是索引里会留下一条指向不存在文件的记录 ——
    /// 下次 <see cref="Load"/> 认不出文件，条目自己就没了，不用专门回删一遍。
    /// </summary>
    public static void WriteImage(string id, BitmapSource image)
    {
        Interlocked.Increment(ref _encoding);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            // 先写临时文件再改名：写到一半被打断的话，留下的是一个残缺的 .tmp，
            // 而不是一张索引正指着、打开却是半截的 PNG
            string tmp = ImagePath(id) + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, ImagePath(id), overwrite: true);
        }
        catch (Exception)
        {
            // 见方法注释：这一张不回来，但不值得为它打断任何人
        }
        finally
        {
            ReleaseId(id);
            Interlocked.Decrement(ref _encoding);
        }
    }

    /// <summary>交还 <see cref="ReserveId"/> 领走的号。落盘之后它就在磁盘上了，不用再占着。</summary>
    public static void ReleaseId(string id)
    {
        lock (IdLock) Reserved.Remove(id);
    }

    /// <summary>
    /// 读回存档。画面文件没了的条目直接丢掉 —— 一张没有画面的贴图没有任何意义。
    ///
    /// 这一点和历史那边不同：历史的条目本身还留着选区、还能当个框用，所以它降级成
    /// 「只有框」；贴图除了画面什么都不是，没有画面就什么也不该摆出来。
    /// </summary>
    public static IReadOnlyList<PinSnapshot> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return [];

            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(IndexPath));
            if (rows is null) return [];

            var snapshots = new List<PinSnapshot>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Image is null || !File.Exists(ImagePath(row.Image))) continue;
                snapshots.Add(new PinSnapshot(
                    row.X, row.Y, row.Scale, row.Angle, row.Opacity, row.TopMost, row.Image));
            }
            return snapshots;
        }
        catch (Exception)
        {
            // 手改坏了、写到一半断电了 —— 都不该让程序起不来
            return [];
        }
    }

    public static void SaveIndex(IEnumerable<PinSnapshot> snapshots)
    {
        try
        {
            var rows = new List<Row>();
            foreach (var s in snapshots)
                rows.Add(new Row(s.X, s.Y, s.Scale, s.Angle, s.Opacity, s.TopMost, s.Image));

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(rows, JsonOptions));
        }
        catch (Exception)
        {
            // 落不下盘就落不下：贴图还在屏幕上，用户此刻要的是它，不是一条保存记录
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
    /// 删掉不在 keep 里的画面（含写了一半的 .tmp）。
    ///
    /// 贴图关掉之后它那份画面就没人认领了，只删索引不删文件的话目录会一直涨 ——
    /// 用户看不到有谁在占着磁盘，也就永远不会去清。和截屏历史是同一个道理。
    /// </summary>
    public static void PruneImages(IEnumerable<string> keep)
    {
        // 有画面正在后台编码就先不动手：它那个 .tmp 正摆在目录里，
        // 这一趟扫下来会把一张还没落地的贴图当残骸清掉。下一次落盘再收拾就是了
        if (Volatile.Read(ref _encoding) > 0) return;

        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var live = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                string ext = Path.GetExtension(file);
                bool png = ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
                if (!png && !ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (png && live.Contains(Path.GetFileNameWithoutExtension(file))) continue;

                try { File.Delete(file); }
                catch (Exception) { /* 正被看图软件占着，下次再说 */ }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 把整个存档收干净。设置里关掉「重启后恢复贴图」时调 ——
    /// 留着的话，用户哪天再打开这一项，冒出来的会是一批他早就不记得、当初也没打算留的图。
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                try { File.Delete(file); }
                catch (Exception) { /* 同上，删不掉就留着 */ }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 等后台那几张画面落盘。退出前调一次。
    ///
    /// 「贴完就去点重启」是真实存在的用法，而那时候画面可能还在编码路上：
    /// 索引里那一行已经写好了，文件却没有，下一次开机读回来是空的。
    /// 等它一下比丢一张图便宜得多，所以这里是**同步**等的，超时也只是兜底。
    /// </summary>
    public static void FlushPending(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Volatile.Read(ref _encoding) > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }
}
