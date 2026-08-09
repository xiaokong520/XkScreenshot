using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Llm;

namespace XkScreenshot.Ocr;

/// <summary>
/// 在线 OCR：把截图编码为 base64 PNG 发给大模型，返回的文本按换行符拆成行。
/// </summary>
public sealed class LLMOcrEngine : IOcrEngine
{
    private readonly LlmApiClient _client;
    private readonly LlmApiConfig _config;

    private const string Prompt =
"""
你是一台精准的文字识别引擎。请识别这张截图中的所有可见文字。
按从上到下、从左到右的顺序输出，每行文字占一行，保持原文的换行结构。
只返回识别到的文字，不要任何解释、问候语或前缀后缀。
""";

    /// <summary>最近一次 API 返回的原始文本，用于调试。</summary>
    public string? LastRawResponse { get; private set; }

    public LLMOcrEngine(LlmApiClient client, LlmApiConfig config)
    {
        _client = client;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(BitmapSource image, CancellationToken ct = default)
    {
        string base64 = EncodePngBase64(image);

        string raw = await _client.ChatWithImageAsync(_config, Prompt, base64, ct)
            .ConfigureAwait(false);

        LastRawResponse = raw;
        return ParseResponse(raw);
    }

    private static List<OcrLine> ParseResponse(string raw)
    {
        var lines = new List<OcrLine>();
        foreach (string line in raw.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0)
                lines.Add(new OcrLine(t, default, Array.Empty<OcrWord>()));
        }
        return lines;
    }

    private static string EncodePngBase64(BitmapSource image)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
