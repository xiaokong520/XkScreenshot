using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Ocr;

/// <summary>
/// PaddleOCR 离线引擎：封装 RapidOcrNet，
/// DBNet 文本检测 → 方向分类 → CRNN 文字识别。
///
/// 模型文件从 <c>models/paddleocr/</c> 加载：
/// <c>det.onnx</c>、<c>cls.onnx</c>、<c>rec.onnx</c>、<c>dict.txt</c>。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly RapidOcr _ocr;
    private readonly RapidOcrOptions _options;

    public PaddleOcrEngine(string modelDir)
    {
        string detPath = Path.Combine(modelDir, "det.onnx");
        string clsPath = Path.Combine(modelDir, "cls.onnx");
        string recPath = Path.Combine(modelDir, "rec.onnx");
        string keysPath = Path.Combine(modelDir, "dict.txt");

        if (!File.Exists(detPath))
            throw new FileNotFoundException($"检测模型未找到：{detPath}");
        if (!File.Exists(recPath))
            throw new FileNotFoundException($"识别模型未找到：{recPath}");
        if (!File.Exists(clsPath))
            throw new FileNotFoundException($"方向分类模型未找到：{clsPath}。请到设置页面重新下载 PaddleOCR 模型（新增了 cls.onnx）。");
        if (!File.Exists(keysPath))
            throw new FileNotFoundException($"字典文件未找到：{keysPath}");

        _ocr = new RapidOcr();
        _ocr.InitModels(detPath, clsPath, recPath, keysPath);

        _options = RapidOcrOptions.Default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(BitmapSource image, CancellationToken ct = default)
    {
        using var skBitmap = ConvertToSkBitmap(image);

        return await Task.Run(() =>
        {
            OcrResult result = _ocr.Detect(skBitmap, _options);
            int blockCount = result.TextBlocks.Count();
            var lines = new List<OcrLine>(blockCount);

            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;

                var rect = BoxPointsToRect(block.BoxPoints);
                lines.Add(new OcrLine(block.Text, rect, Array.Empty<OcrWord>()));
            }

            return (IReadOnlyList<OcrLine>)lines;
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _ocr.Dispose();
    }

    // ---------------- 坐标转换 ----------------

    private static PixelRect BoxPointsToRect(SKPointI[] points)
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

        int x = (int)minX;
        int y = (int)minY;
        int w = (int)(maxX - minX);
        int h = (int)(maxY - minY);
        return new PixelRect(x, y, w, h);
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
