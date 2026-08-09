using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Ocr;

/// <summary>最小识别单元：一个词或连续的几个字符。Bounds 是图片像素坐标，原点在左上角。</summary>
public readonly record struct OcrWord(string Text, PixelRect Bounds, float Confidence);

/// <summary>按阅读顺序排列的一行文字。识别粒度够细时 Words 会拆到词级。</summary>
public readonly record struct OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord> Words)
{
    public float AverageConfidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);
}

/// <summary>
/// OCR 引擎的公共接口。每种实现只管把图扔进去、拿出 <see cref="OcrLine"/> 列表。
/// 各引擎内部已做好后处理，调用方直接用返回的行列表即可。
/// </summary>
public interface IOcrEngine
{
    /// <summary>对一张截图跑识别，返回按阅读顺序排好的行。图不变时多次调用应返回一致的结果。</summary>
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(BitmapSource image, CancellationToken ct = default);
}
