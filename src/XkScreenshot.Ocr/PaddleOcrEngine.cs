using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Ocr;

/// <summary>
/// PaddleOCR 离线引擎：封装 RapidOcrNet，DBNet 文本检测 → CRNN 文字识别。
///
/// 模型文件从 <c>models/paddleocr/</c> 加载：<c>det.onnx</c>、<c>cls.onnx</c>、
/// <c>rec.onnx</c>、<c>dict.txt</c>，外加可选的 <c>rec-{语种}.onnx</c> /
/// <c>dict-{语种}.txt</c>（见 <see cref="OcrLanguagePacks"/>）。
///
/// 识别模型按文字系统分家，一份认不了所有文字。默认那份认汉字、假名和拉丁字母；
/// 谚文、西里尔、天城文各要一份。选哪一份不能问用户 —— 截图前先去设置里切语言，
/// 这件事没人记得住，忘了就是满屏乱码。所以先用默认的跑，结果不像话再拿装了的语言包
/// 各试一遍，取最好的那个。中英日是常见情况，一分钱不多花。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    /// <summary>
    /// 送进识别前把图放大到几倍。
    ///
    /// 识别模型吃的是高约 48px 的行切片，而屏幕正文通常只有 12~16px ——
    /// 直接送进去等于让它读一张糊图。实测同一段 12px 英文：原样识别出
    /// 「Dislamhjtsrihicalngdrhnharu」，放大两倍后一字不差。
    /// 三倍没有额外收益，像素却多一倍还不止。
    /// </summary>
    private const float UpscaleFactor = 2f;

    /// <summary>放大后的上限。整屏截图再乘二会让检测慢一倍，这两个数是给它封顶的。</summary>
    private const int MaxSide = 4096;
    private const long MaxPixels = 8_400_000;

    /// <summary>
    /// 默认模型识别得低于这个平均字符置信度，就认为它可能读的不是自己认识的文字，
    /// 去问问装了的语言包。实测正常识别在 0.95 以上，用错模型时明显掉下来
    /// （韩文喂给中文模型是 0.82，而且只吐出几个字符）。
    /// </summary>
    private const double SuspectMeanScore = 0.92;

    /// <summary>
    /// 语言包要比默认模型好这么多倍才换。
    ///
    /// 纯英文这类两个模型都认得的输入，得分几乎一样（实测 58.8 对 58.9），
    /// 差之毫厘就换模型只会让同一张图每次识别出的结果飘忽不定。
    /// </summary>
    private const double SwitchMargin = 1.15;

    /// <summary>
    /// 这么久没有识别就把会话全放掉。
    ///
    /// 这是个常驻托盘的截图工具，识别是偶尔按一次的动作，而会话一旦跑过一张整屏图
    /// 就会攥着几百兆不放（见 <see cref="Load"/> 里那段关于内存池的说明）。
    /// 攥着换来的是下一次省掉三百毫秒的加载 —— 十分钟没动过的话，这笔交易不划算。
    /// </summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);

    private readonly string _modelDir;
    private readonly string _detPath;
    private readonly string _clsPath;
    private readonly string _recPath;
    private readonly string _keysPath;

    /// <summary>
    /// 默认识别会话。按需建、空闲久了放掉，所以可能为 null ——
    /// 开机就把它建起来等于让一个多数时候用不到的功能白占内存。
    /// </summary>
    private RapidOcr? _base;

    /// <summary>装了的语言包，同样按需加载 —— 多数截图根本用不上。</summary>
    private readonly Dictionary<string, RapidOcr> _packs = [];

    /// <summary>RapidOcr 实例不保证能并发调用，而同一时刻本来也只有一次识别在跑。</summary>
    private readonly object _gate = new();

    /// <summary>空闲释放的闹钟。每次识别完重新上弦，没响之前一直往后推。</summary>
    private readonly Timer _idle;

    private bool _disposed;

    public PaddleOcrEngine(string modelDir)
    {
        _modelDir = modelDir;
        _detPath = Path.Combine(modelDir, "det.onnx");
        _clsPath = Path.Combine(modelDir, "cls.onnx");
        _recPath = OcrLanguagePacks.RecPath(modelDir, null);
        _keysPath = OcrLanguagePacks.DictPath(modelDir, null);

        // 文件在不在当场就查清楚，不留到第一次识别时才炸 ——
        // 那时用户已经框完图了，报「模型没装」比按下热键就报要难受得多
        if (!File.Exists(_detPath))
            throw new FileNotFoundException($"检测模型未找到：{_detPath}");
        if (!File.Exists(_recPath))
            throw new FileNotFoundException($"识别模型未找到：{_recPath}");
        if (!File.Exists(_clsPath))
            throw new FileNotFoundException($"方向分类模型未找到：{_clsPath}。请到设置页面重新下载 PaddleOCR 模型。");
        if (!File.Exists(_keysPath))
            throw new FileNotFoundException($"字典文件未找到：{_keysPath}");

        _idle = new Timer(_ => ReleaseSessions(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 建一个识别会话。
    ///
    /// 关掉 CPU 内存池（arena）：ONNX Runtime 默认会把推理用过的内存留在自己的池子里
    /// 等下次复用，而且**只涨不还**。这个模型的中间张量大小随图片尺寸走，一张放大后的
    /// 整屏图（3840×2160）能让池子涨到 4.8 GB 并且一直挂着；关掉之后同样的图跑四遍
    /// 稳定在 0.49 GB，耗时 5.0 秒对 5.1 秒 —— 池子在这里省不出时间，只在攒内存。
    ///
    /// 日志压到只报错误：这批模型是从 Paddle 转过来的，加载时 ONNX Runtime 会为几十个
    /// 用不上的初始化器各刷一行警告 —— 全是无害的转换残留，却能把控制台淹掉。
    /// </summary>
    private RapidOcr Load(string recPath, string keysPath)
    {
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        options.EnableCpuMemArena = false;

        var ocr = new RapidOcr();
        ocr.InitModels(_detPath, _clsPath, recPath, keysPath, options);
        return ocr;
    }

    /// <summary>把所有会话放掉。空闲到点了调，也可以在别处主动调来腾内存。</summary>
    public void ReleaseSessions()
    {
        lock (_gate)
        {
            _base?.Dispose();
            _base = null;

            foreach (var pack in _packs.Values) pack.Dispose();
            _packs.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(BitmapSource image, CancellationToken ct = default)
    {
        using var original = ConvertToSkBitmap(image);
        float scale = ResolveScale(original.Width, original.Height);

        using var input = scale <= 1.01f
            ? original.Copy()
            : original.Resize(
                new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
                // 三次插值：放大字形要的是笔画边缘平滑，双线性会把细笔画抹平
                new SKSamplingOptions(SKCubicResampler.Mitchell));

        return await Task.Run(() => Recognize(input, scale, ct), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<OcrLine> Recognize(SKBitmap input, float scale, CancellationToken ct)
    {
        var options = OptionsFor(input);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                _base ??= Load(_recPath, _keysPath);

                var best = Run(_base, input, scale, options);
                if (best.MeanScore >= SuspectMeanScore) return best.Lines;

                // 默认模型读得吃力。可能是图本身糊，也可能这段文字它压根不认识 ——
                // 后者装了对应语言包就能救，前者试一圈也只是白花一两百毫秒
                foreach (var pack in OcrLanguagePacks.Installed(_modelDir))
                {
                    ct.ThrowIfCancellationRequested();

                    var attempt = Run(GetPack(pack.Code), input, scale, options);
                    if (attempt.TotalScore > best.TotalScore * SwitchMargin) best = attempt;
                }

                return best.Lines;
            }
            finally
            {
                // 连着识别几张时闹钟一次次往后推，停下来之后才开始倒计时
                if (!_disposed) _idle.Change(IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>调用方必须已经持有 <see cref="_gate"/>。</summary>
    private RapidOcr GetPack(string code)
    {
        if (_packs.TryGetValue(code, out var cached)) return cached;

        var ocr = Load(
            OcrLanguagePacks.RecPath(_modelDir, code), OcrLanguagePacks.DictPath(_modelDir, code));
        _packs[code] = ocr;
        return ocr;
    }

    private static Attempt Run(RapidOcr ocr, SKBitmap input, float scale, RapidOcrOptions options)
    {
        OcrResult result = ocr.Detect(input, options);

        var lines = new List<OcrLine>();
        double total = 0;
        int characters = 0;

        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;

            // 框是在放大后的图上量的，得除回原图坐标
            lines.Add(new OcrLine(block.Text, BoxPointsToRect(block.BoxPoints, scale), Array.Empty<OcrWord>()));

            foreach (float score in block.CharScores ?? []) { total += score; characters++; }
        }

        return new Attempt(lines, total, characters == 0 ? 0 : total / characters);
    }

    /// <summary>
    /// 一次识别的结果和它有多可信。
    ///
    /// 比较两个模型时看 <paramref name="TotalScore"/>（每个字符的置信度之和）而不是平均分：
    /// 用错模型的那一边往往只吐出零星几个字符，平均分反而不难看，但总分差着一个数量级
    /// —— 实测同一张韩文图，中文模型 6.6 分 8 个字符，韩语模型 42.4 分 44 个字符。
    /// </summary>
    private sealed record Attempt(IReadOnlyList<OcrLine> Lines, double TotalScore, double MeanScore);

    /// <summary>放大到 <see cref="UpscaleFactor"/> 倍，但不越过长边和总像素两条线。</summary>
    private static float ResolveScale(int width, int height)
    {
        float byLongSide = (float)MaxSide / Math.Max(width, height);
        float byArea = MathF.Sqrt((float)MaxPixels / ((long)width * height));
        return Math.Clamp(Math.Min(UpscaleFactor, Math.Min(byLongSide, byArea)), 1f, UpscaleFactor);
    }

    private static RapidOcrOptions OptionsFor(SKBitmap image) => RapidOcrOptions.Default with
    {
        // 默认 736，比一张截图小得多 —— 检测前会先把图缩到这个尺寸，
        // 等于把上面刚放大的又缩了回去，正文小字直接糊掉。放开到图本身的大小，
        // 它是上限不是目标，图比这小的时候不会被反过来拉大。
        LimitSideLen = Math.Max(Math.Max(image.Width, image.Height), 736),
        ImgResize = Math.Max(Math.Max(image.Width, image.Height), 736),
        MaxSideLen = Math.Max(Math.Max(image.Width, image.Height), 2000),

        // 方向分类器是为「拍歪了的照片」准备的，而屏幕上的字天生就是正的。
        // 留着它只会偶尔把某一行判成倒转再翻过来，翻出一串形近字母 ——
        // based 会变成 paseq、open 会变成 uado，看着像乱码，其实是镜像字形。
        DoAngle = false,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // 不等回调收尾：它要拿的正是刚放开的这把锁，而它干的事（放掉会话）跟下面这行
        // 一模一样，且能重复做。晚到一步的那次回调最多是对着已经空了的字段再走一遍
        _idle.Dispose();
        ReleaseSessions();
    }

    // ---------------- 坐标转换 ----------------

    private static PixelRect BoxPointsToRect(SKPointI[] points, float scale)
    {
        if (points is null || points.Length == 0) return default;

        float minX = points[0].X, maxX = points[0].X;
        float minY = points[0].Y, maxY = points[0].Y;

        for (int i = 1; i < points.Length; i++)
        {
            if (points[i].X < minX) minX = points[i].X;
            if (points[i].X > maxX) maxX = points[i].X;
            if (points[i].Y < minY) minY = points[i].Y;
            if (points[i].Y > maxY) maxY = points[i].Y;
        }

        return new PixelRect(
            (int)(minX / scale), (int)(minY / scale),
            (int)((maxX - minX) / scale), (int)((maxY - minY) / scale));
    }

    // ---------------- 图像转换 ----------------

    private static SKBitmap ConvertToSkBitmap(BitmapSource source)
    {
        // 编码为 PNG → SKBitmap 解码，兼容所有像素格式
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }
}
