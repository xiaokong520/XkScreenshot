using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.Ocr;

/// <summary>一个可选的识别语言包。<paramref name="Megabytes"/> 只用来给界面报个量级。</summary>
public sealed record OcrLanguagePack(string Code, string Name, string Repo, double Megabytes);

/// <summary>
/// PaddleOCR 的识别模型按文字系统分家：默认那份认汉字、假名和拉丁字母，
/// 认不了谚文、西里尔、天城文……**字典里根本没有那些字符**，
/// 所以遇到韩文它不是认错，是压根吐不出来 —— 会硬把谚文凑成几个形近的汉字或字母。
///
/// 这里管的就是「除了默认那份之外还装了哪些」。检测（det）和方向分类（cls）与语种无关，
/// 各语言包只换识别模型和字典。
///
/// 模型取自 PaddlePaddle 在 HuggingFace 的官方组织，不走第三方转换的镜像 ——
/// 模型文件是要下载到用户机器上跑的东西，来源得站得住。
/// </summary>
public static class OcrLanguagePacks
{
    private const string HuggingFace = "https://huggingface.co";

    /// <summary>默认识别模型：汉字（简繁）、日文假名、拉丁字母、数字。</summary>
    public const string BaseRecRepo = "PaddlePaddle/PP-OCRv5_mobile_rec_onnx";
    public const string BaseDetRepo = "PaddlePaddle/PP-OCRv5_mobile_det_onnx";

    /// <summary>
    /// 方向分类模型。官方那套 ONNX 里没有它，只好还从 RapidOCR 作者的仓库拿。
    /// 我们把方向分类关掉了（见 <see cref="PaddleOcrEngine"/>），但底层库要求这个路径存在。
    /// </summary>
    public const string ClsUrl =
        HuggingFace + "/SWHL/RapidOCR/resolve/main/PP-OCRv3/ch_ppocr_mobile_v2.0_cls_train.onnx";

    /// <summary>
    /// 可选语言包。
    ///
    /// 官方还发了 <c>eslav</c>（东斯拉夫专用）和 <c>en</c>（纯英文），这里都没收：
    /// 前者被 <c>cyrillic</c> 盖住，后者默认模型就认得。摆两个作用重叠的选项出来，
    /// 只会让人多花一次时间去想它们有什么区别。
    /// </summary>
    public static IReadOnlyList<OcrLanguagePack> All { get; } =
    [
        new("korean", "韩语", "PaddlePaddle/korean_PP-OCRv5_mobile_rec_onnx", 12.8),
        new("latin", "拉丁字母扩展（法德西葡意越等）", "PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx", 7.7),
        new("cyrillic", "西里尔字母（俄乌塞保等）", "PaddlePaddle/cyrillic_PP-OCRv5_mobile_rec_onnx", 7.7),
        new("arabic", "阿拉伯字母（阿拉伯语、波斯语、乌尔都语）", "PaddlePaddle/arabic_PP-OCRv5_mobile_rec_onnx", 7.6),
        new("devanagari", "天城文（印地语、马拉地语）", "PaddlePaddle/devanagari_PP-OCRv5_mobile_rec_onnx", 7.5),
        new("el", "希腊语", "PaddlePaddle/el_PP-OCRv5_mobile_rec_onnx", 7.4),
        new("th", "泰语", "PaddlePaddle/th_PP-OCRv5_mobile_rec_onnx", 7.5),
        new("ta", "泰米尔语", "PaddlePaddle/ta_PP-OCRv5_mobile_rec_onnx", 7.5),
        new("te", "泰卢固语", "PaddlePaddle/te_PP-OCRv5_mobile_rec_onnx", 7.5),
    ];

    public static OcrLanguagePack? Find(string code)
        => All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    // ---------------- 文件布局 ----------------

    public static string RecPath(string modelDir, string? code)
        => Path.Combine(modelDir, code is null ? "rec.onnx" : $"rec-{code}.onnx");

    public static string DictPath(string modelDir, string? code)
        => Path.Combine(modelDir, code is null ? "dict.txt" : $"dict-{code}.txt");

    public static bool IsInstalled(string modelDir, string code)
        => File.Exists(RecPath(modelDir, code)) && File.Exists(DictPath(modelDir, code));

    public static IEnumerable<OcrLanguagePack> Installed(string modelDir)
        => All.Where(p => IsInstalled(modelDir, p.Code));

    // ---------------- 下载 ----------------

    /// <summary>下一个语言包：识别模型 + 从 inference.yml 里抽出来的字典。</summary>
    public static Task DownloadPackAsync(
        HttpClient http, string modelDir, OcrLanguagePack pack,
        IProgress<int>? progress = null, CancellationToken ct = default)
        => DownloadRecAsync(http, modelDir, pack.Repo, pack.Code, progress, ct);

    /// <summary>下默认的识别模型和字典。</summary>
    public static Task DownloadBaseRecAsync(
        HttpClient http, string modelDir,
        IProgress<int>? progress = null, CancellationToken ct = default)
        => DownloadRecAsync(http, modelDir, BaseRecRepo, null, progress, ct);

    private static async Task DownloadRecAsync(
        HttpClient http, string modelDir, string repo, string? code,
        IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelDir);
        string baseUrl = $"{HuggingFace}/{repo}/resolve/main";

        // 模型是大头，配置只有几十 KB，进度条按 0~95 / 95~100 分
        await DownloadFileAsync(http, $"{baseUrl}/inference.onnx", RecPath(modelDir, code),
            Rescale(progress, 0, 95), ct).ConfigureAwait(false);

        string yml = await http.GetStringAsync($"{baseUrl}/inference.yml", ct).ConfigureAwait(false);
        File.WriteAllLines(DictPath(modelDir, code), ExtractDictionary(yml), new UTF8Encoding(false));
        progress?.Report(100);
    }

    public static async Task DownloadFileAsync(
        HttpClient http, string url, string path,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string part = path + ".part";
        using (var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(part);

            var buffer = new byte[81920];
            long read = 0;
            int n, last = -1;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;

                if (total <= 0 || progress is null) continue;
                int percent = (int)(read * 100 / total);
                if (percent == last) continue;
                last = percent;
                progress.Report(percent);
            }
        }

        // 先落 .part 再改名：下到一半断网留下的半个模型，大小看着像模像样，
        // 要等到加载时才以一句原生层的错误炸出来
        File.Move(part, path, overwrite: true);
        progress?.Report(100);
    }

    /// <summary>
    /// 从 <c>inference.yml</c> 里抽出字符表。
    ///
    /// 官方 ONNX 仓库不单独发字典，它嵌在配置里，是 <c>character_dict:</c> 底下一串
    /// <c>- 字</c>。格式简单到不值得为它引一个 YAML 库，但**引号必须自己解** ——
    /// 标点那三十来个条目是带引号的（<c>'!'</c>、<c>''''</c>），
    /// 照抄进字典就会让识别结果里凭空多出引号。
    /// </summary>
    internal static List<string> ExtractDictionary(string yml)
    {
        var characters = new List<string>();
        bool inside = false;

        foreach (string line in yml.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!inside)
            {
                inside = line.Trim() == "character_dict:";
                continue;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal))
                characters.Add(Unquote(line[4..]));
            else if (line.Trim().Length > 0)
                break;
        }

        if (characters.Count == 0)
            throw new InvalidDataException("模型配置里没找到字符表（character_dict）。");

        return characters;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }

    /// <summary>把 0~100 的进度压进 [<paramref name="from"/>, <paramref name="to"/>] 这一段。</summary>
    private static IProgress<int>? Rescale(IProgress<int>? progress, int from, int to)
        => progress is null ? null : new Progress<int>(p => progress.Report(from + p * (to - from) / 100));
}
