using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.Translate;

/// <summary>一个可下载的语种。<paramref name="ToEnglishOnly"/> 的只有「→英语」那一半。</summary>
public sealed record BergamotLanguage(string Code, string Name, bool ToEnglishOnly = false);

/// <summary>
/// Bergamot 能下哪些语种，以及去哪儿下。
///
/// 语种表写死在代码里，模型清单临下载时才去网上取 —— 设置界面要在离线状态下也能
/// 把清单列出来（用户可能正是因为没网才想装离线翻译），而具体文件路径每次换代都会变，
/// 写死了迟早对不上。
/// </summary>
public static class BergamotCatalog
{
    /// <summary>
    /// Mozilla 的模型登记表。
    ///
    /// 早年那个 <c>mozilla/firefox-translations-models</c> GitHub 仓库已经停止维护，
    /// 模型改从这里分发，别再回去翻那个仓库。
    /// </summary>
    public const string RegistryUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json";

    /// <summary>模型库里英语是所有语言的枢纽，它不作为独立语种下载。</summary>
    public const string Pivot = "en";

    /// <summary>
    /// 支持的语种。代码用模型库那一套，不是 BCP-47（繁体中文在那边写作 <c>zh_hant</c>）。
    ///
    /// 登记表里还有 <c>no</c> 和 <c>hbs</c>，这里都没收：<c>no</c> 跟 <c>nb</c> 指向同一份
    /// 模型，<c>hbs</c>（塞尔维亚-克罗地亚语）的地盘已经被 bs / hr / sr 三个分掉了。
    /// 摆两个作用相同的选项出来，只会让人多花一次时间去想它们有什么区别。
    /// </summary>
    public static IReadOnlyList<BergamotLanguage> Languages { get; } =
    [
        new("zh", "中文（简体）"),
        new("zh_hant", "中文（繁体）"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("vi", "越南语"),
        new("th", "泰语"),
        new("id", "印尼语"),
        new("ms", "马来语"),

        new("de", "德语"),
        new("fr", "法语"),
        new("es", "西班牙语"),
        new("pt", "葡萄牙语"),
        new("it", "意大利语"),
        new("nl", "荷兰语"),
        new("ca", "加泰罗尼亚语"),
        new("gl", "加利西亚语"),
        new("eu", "巴斯克语"),

        new("sv", "瑞典语"),
        new("da", "丹麦语"),
        new("nb", "挪威语（书面）"),
        new("nn", "挪威语（尼诺斯克）", ToEnglishOnly: true),
        new("fi", "芬兰语"),
        new("is", "冰岛语"),

        new("ru", "俄语"),
        new("uk", "乌克兰语"),
        new("be", "白俄罗斯语", ToEnglishOnly: true),
        new("pl", "波兰语"),
        new("cs", "捷克语"),
        new("sk", "斯洛伐克语"),
        new("sl", "斯洛文尼亚语"),
        new("hr", "克罗地亚语"),
        new("sr", "塞尔维亚语"),
        new("bs", "波斯尼亚语"),
        new("bg", "保加利亚语"),
        new("hu", "匈牙利语"),
        new("ro", "罗马尼亚语"),
        new("sq", "阿尔巴尼亚语"),
        new("el", "希腊语"),
        new("et", "爱沙尼亚语"),
        new("lv", "拉脱维亚语"),
        new("lt", "立陶宛语"),

        new("ar", "阿拉伯语"),
        new("he", "希伯来语"),
        new("fa", "波斯语"),
        new("tr", "土耳其语"),
        new("az", "阿塞拜疆语"),
        new("ur", "乌尔都语"),

        new("hi", "印地语"),
        new("mr", "马拉地语"),
        new("bn", "孟加拉语"),
        new("ta", "泰米尔语"),
        new("te", "泰卢固语"),
        new("kn", "卡纳达语"),
        new("gu", "古吉拉特语"),
        new("ml", "马拉雅拉姆语"),
    ];

    private static readonly Dictionary<string, string> NamesByCode =
        Languages.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);

    public static BergamotLanguage? Find(string code)
        => Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string code)
        => string.Equals(code, Pivot, StringComparison.OrdinalIgnoreCase) ? "英语"
            : NamesByCode.GetValueOrDefault(code, code);

    /// <summary>装一个语种要下的方向。单向语种只有一个。</summary>
    public static IReadOnlyList<string> DirectionsOf(BergamotLanguage lang)
        => lang.ToEnglishOnly
            ? [$"{lang.Code}-{Pivot}"]
            : [$"{lang.Code}-{Pivot}", $"{Pivot}-{lang.Code}"];

    public static async Task<BergamotRegistry> LoadRegistryAsync(
        HttpClient http, CancellationToken ct = default)
    {
        await using var stream = await http.GetStreamAsync(RegistryUrl, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return BergamotRegistry.Parse(doc.RootElement);
    }
}

/// <summary>models.json 解析完的样子：每个方向只留下选中的那个候选。</summary>
public sealed class BergamotRegistry
{
    /// <summary>
    /// 同一个方向往往有好几个候选（不同架构、不同训练批次），按发布状态挑。
    /// 桌面版专用的排最前，其次是通用发布版；夜间构建和没打标记的排最后 ——
    /// 那些是还没定版的，只在别无选择时才用。
    /// </summary>
    private static readonly string[] StatusOrder =
        ["Release Desktop", "Release", "Release Android", "Nightly"];

    private readonly Dictionary<string, Candidate> _directions;

    public string BaseUrl { get; }

    private BergamotRegistry(string baseUrl, Dictionary<string, Candidate> directions)
    {
        BaseUrl = baseUrl;
        _directions = directions;
    }

    public bool Has(string direction) => _directions.ContainsKey(direction);

    /// <summary>权重文件解压后的大小。词表和捷径表另算，实际落盘还要再多两三成。</summary>
    public long ModelBytes(string direction)
        => _directions.TryGetValue(direction, out var c) ? c.ModelBytes : 0;

    /// <summary>装一个语种一共要占多少 —— 权重是大头，这个数只用来给界面报个量级。</summary>
    public long ApproxBytes(IEnumerable<string> directions)
        => directions.Sum(ModelBytes);

    internal static BergamotRegistry Parse(JsonElement root)
    {
        string baseUrl = root.GetProperty("baseUrl").GetString()
            ?? throw new InvalidDataException("模型登记表里没有 baseUrl。");

        var directions = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.GetProperty("models").EnumerateObject())
        {
            var best = entry.Value.EnumerateArray()
                .Select(Candidate.From)
                .Where(c => c is not null)
                .OrderBy(c => Rank(c!.ReleaseStatus))
                .FirstOrDefault();

            if (best is not null) directions[entry.Name] = best;
        }

        return new BergamotRegistry(baseUrl.TrimEnd('/'), directions);
    }

    private static int Rank(string? status)
    {
        int i = Array.IndexOf(StatusOrder, status);
        return i < 0 ? StatusOrder.Length : i;
    }

    /// <summary>
    /// 把一个方向的模型下到 <c>{root}/{direction}/</c> 并生成配置。
    ///
    /// 每个文件先解压进 <c>.part</c> 再改名：下到一半断网留下的半个权重文件，
    /// 大小看着像模像样，加载时才会以一句原生层的错误炸出来。
    /// </summary>
    public async Task DownloadAsync(
        HttpClient http, string root, string direction,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!_directions.TryGetValue(direction, out var candidate))
            throw new NotSupportedException($"模型库里没有 {direction} 这个方向。");

        string pairDir = Path.Combine(root, direction);
        Directory.CreateDirectory(pairDir);

        try
        {
            for (int i = 0; i < candidate.Paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                int index = i;
                var fileProgress = progress is null ? null : new Progress<int>(p =>
                    progress.Report((index * 100 + p) / candidate.Paths.Count));

                await DownloadOne(http, $"{BaseUrl}/{candidate.Paths[i]}", pairDir, fileProgress, ct)
                    .ConfigureAwait(false);
            }

            BergamotModelDir.WriteConfig(pairDir);
            progress?.Report(100);
        }
        catch
        {
            // 半装的目录留着只会在下次启动时以「模型文件不全」的形式再炸一次
            try { Directory.Delete(pairDir, recursive: true); } catch { /* 清不掉就算了 */ }
            throw;
        }
    }

    private static async Task DownloadOne(
        HttpClient http, string url, string pairDir, IProgress<int>? progress, CancellationToken ct)
    {
        // 登记表里的路径一律是 .gz，落盘要的是解压后的名字
        string name = Path.GetFileName(new Uri(url).AbsolutePath);
        bool gzipped = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        if (gzipped) name = name[..^3];

        string target = Path.Combine(pairDir, name);
        string part = target + ".part";

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var counted = new CountingStream(network, total, progress))
        await using (var source = gzipped
            ? new GZipStream(counted, CompressionMode.Decompress)
            : (Stream)counted)
        await using (var file = File.Create(part))
        {
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        File.Move(part, target, overwrite: true);
        progress?.Report(100);
    }

    /// <summary>
    /// 数网络上读了多少字节。
    ///
    /// 进度得在解压之前量：解压后的字节数没有分母（响应头给的是压缩后的长度），
    /// 而压缩前的进度跟用户等待的时间才是同一件事。
    /// </summary>
    private sealed class CountingStream(Stream inner, long total, IProgress<int>? progress) : Stream
    {
        private long _read;
        private int _lastPercent = -1;

        public override int Read(byte[] buffer, int offset, int count)
            => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
            => Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        private int Count(int n)
        {
            if (n <= 0 || total <= 0) return n;

            _read += n;
            int percent = (int)Math.Min(99, _read * 100 / total);
            if (percent != _lastPercent)
            {
                _lastPercent = percent;
                progress?.Report(percent);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // 底下那条流由调用方的 using 负责，这里只是套了一层计数
            base.Dispose(disposing);
        }
    }

    private sealed record Candidate(string? ReleaseStatus, long ModelBytes, IReadOnlyList<string> Paths)
    {
        /// <summary>三类文件缺任何一类都装不起来，直接当这个候选不存在。</summary>
        public static Candidate? From(JsonElement element)
        {
            if (!element.TryGetProperty("files", out var files)) return null;

            var paths = new List<string>();
            foreach (string key in new[] { "model", "lexicalShortlist", "vocab", "srcVocab", "trgVocab" })
            {
                if (files.TryGetProperty(key, out var file)
                    && file.TryGetProperty("path", out var path)
                    && path.GetString() is { Length: > 0 } value)
                    paths.Add(value);
            }

            // model + shortlist + （共用词表 1 份 或 收发词表 2 份）
            if (paths.Count < 3) return null;

            long bytes = 0;
            if (files.TryGetProperty("model", out var model)
                && model.TryGetProperty("uncompressedSize", out var size))
                bytes = size.GetInt64();

            string? status = element.TryGetProperty("releaseStatus", out var s)
                ? s.GetString() : null;

            return new Candidate(status, bytes, paths);
        }
    }
}
