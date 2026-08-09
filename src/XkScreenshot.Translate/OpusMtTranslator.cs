using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace XkScreenshot.Translate;

/// <summary>
/// OPUS-MT 离线翻译引擎：Helsinki-NLP Marian 模型，通过 ONNX Runtime 推理。
///
/// 使用 onnx-community 提供的 ONNX 模型（encoder/decoder 分离）：
/// <c>models/opus-mt/{from}-{to}/</c> 下应有：
/// <c>encoder_model.onnx</c>、<c>decoder_model.onnx</c>、
/// <c>source.spm</c>、<c>target.spm</c>。
/// </summary>
public sealed class OpusMtTranslator : ITranslator, IDisposable
{
    private readonly Dictionary<string, LanguagePair> _pairs = [];
    private const int MaxDecodeLen = 512;

    public OpusMtTranslator(string opusMtDir, IEnumerable<(string From, string To)> pairs)
    {
        foreach (var (from, to) in pairs)
        {
            string dir = Path.Combine(opusMtDir, $"{from}-{to}");
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException(
                    $"语言对 {from}→{to} 的模型目录不存在：{dir}。请先下载模型文件。");

            string encPath = Path.Combine(dir, "encoder_model.onnx");
            string decPath = Path.Combine(dir, "decoder_model.onnx");
            string srcTokPath = Path.Combine(dir, "source.spm");
            string tgtTokPath = Path.Combine(dir, "target.spm");

            if (!File.Exists(encPath))
                throw new FileNotFoundException($"编码器模型未找到：{encPath}");
            if (!File.Exists(decPath))
                throw new FileNotFoundException($"解码器模型未找到：{decPath}");
            if (!File.Exists(srcTokPath))
                throw new FileNotFoundException($"源语言分词器未找到：{srcTokPath}");
            if (!File.Exists(tgtTokPath))
                throw new FileNotFoundException($"目标语言分词器未找到：{tgtTokPath}");

            var encSession = new InferenceSession(encPath);
            var decSession = new InferenceSession(decPath);

            // Microsoft.ML.Tokenizers 的 SentencePieceTokenizer 直接加载 .spm protobuf
            using var srcStream = File.OpenRead(srcTokPath);
            var srcTok = SentencePieceTokenizer.Create(srcStream,
                addBeginningOfSentence: false, addEndOfSentence: false);

            using var tgtStream = File.OpenRead(tgtTokPath);
            var tgtTok = SentencePieceTokenizer.Create(tgtStream,
                addBeginningOfSentence: false, addEndOfSentence: false);

            // Helsinki-NLP Marian 约定：
            //   eos_token_id = 0 (= </s>)
            //   decoder_start_token_id (= <pad>) = vocab_size - 1（不在 .spm 里，是模型自己追加的）
            //   SentencePiece 的 </s> ID 可能不是 0，以 tokenizer 实际编码为准
            int srcEosId = ResolveTokenId(srcTok, "</s>");
            int tgtEosId = ResolveTokenId(tgtTok, "</s>");

            // 从解码器输出维度推断 vocab_size，最后一个维度即为词表大小
            var logitsDims = decSession.OutputMetadata.First().Value.Dimensions;
            int tgtVocabSize = logitsDims.Length > 0 ? logitsDims[^1] : tgtTok.Vocabulary.Count;
            if (tgtVocabSize <= 0) tgtVocabSize = tgtTok.Vocabulary.Count + 1;
            int tgtPadId = tgtVocabSize - 1; // Marian 的 <pad> 永远在词表末尾

            var pair = new LanguagePair
            {
                EncoderSession = encSession,
                DecoderSession = decSession,
                SrcTokenizer = srcTok,
                TgtTokenizer = tgtTok,
                SrcEosId = srcEosId,
                TgtEosId = tgtEosId,
                TgtPadId = tgtPadId,
                TgtVocabSize = tgtVocabSize,
            };

            // 按名称模式匹配 I/O，不依赖字典 key 顺序
            {
                var names = encSession.InputMetadata.Keys.ToList();
                pair.EncoderInputName = names.First(n =>
                    n.Contains("input", StringComparison.OrdinalIgnoreCase));
                pair.EncoderAttentionName = names.FirstOrDefault(
                    n => n is "attention_mask" or "encoder_attention_mask"
                         || n.Contains("attention", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    ?? "attention_mask";
                pair.EncoderOutputName = encSession.OutputMetadata.Keys.First();
            }
            {
                var names = decSession.InputMetadata.Keys.ToList();
                pair.DecoderInputName = names.First(n => n is "input_ids" or "decoder_input_ids"
                    || n.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
                pair.DecoderHiddenStateName = names.First(n =>
                    n.Contains("encoder_hidden", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("hidden_state", StringComparison.OrdinalIgnoreCase));
                pair.DecoderMaskName = names.FirstOrDefault(
                    n => n is "attention_mask" or "encoder_attention_mask"
                         || n.Contains("attention", StringComparison.OrdinalIgnoreCase))
                    ?? "attention_mask";
                pair.LogitsName = decSession.OutputMetadata.Keys.First();
            }
            _pairs[$"{from}→{to}"] = pair;
        }

        if (_pairs.Count == 0)
            throw new InvalidOperationException("没有配置任何离线翻译语言对。");
    }

    /// <inheritdoc />
    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        sourceLang = NormalizeCode(sourceLang);
        targetLang = NormalizeCode(targetLang);

        if (sourceLang == "auto")
        {
            var candidates = _pairs.Keys
                .Where(k => k.EndsWith($"→{targetLang}", StringComparison.Ordinal))
                .Select(k => k.Split('→')[0])
                .ToList();

            if (candidates.Count == 0)
                throw new NotSupportedException(
                    $"没有 →{targetLang} 的离线翻译模型。请在设置中下载该语言对。");

            sourceLang = candidates.Count == 1
                ? candidates[0]
                : candidates.First(c => c != targetLang);
        }

        string key = $"{sourceLang}→{targetLang}";
        if (!_pairs.TryGetValue(key, out var pair))
            throw new NotSupportedException(
                $"没有 {sourceLang}→{targetLang} 的离线翻译模型。请在设置中下载该语言对。");

        return await Task.Run(() => TranslateGreedy(pair, text, ct), ct).ConfigureAwait(false);
    }

    private static string NormalizeCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code == "auto") return "auto";
        int dash = code.IndexOf('-');
        return dash > 0 ? code[..dash] : code;
    }

    /// <summary>从 tokenizer 中解析指定特殊 token 的 ID。</summary>
    private static int ResolveTokenId(Tokenizer tokenizer, string token, int fallback = 0)
    {
        try
        {
            var ids = tokenizer.EncodeToIds(token);
            if (ids.Count == 1) return ids[0];
            // 去掉 BOS/EOS 包装：Create 时已关掉，这里再兜底
            return ids.FirstOrDefault(id => id != 1 && id != 2, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        foreach (var pair in _pairs.Values)
        {
            pair.EncoderSession.Dispose();
            pair.DecoderSession.Dispose();
        }
    }

    // ---------------- 贪婪解码 ----------------

    private static string TranslateGreedy(LanguagePair pair, string text, CancellationToken ct)
    {
        // 1. 源语言分词（</s> 由编码器输入自己带，Marian 标准做法）
        var srcIds = pair.SrcTokenizer.EncodeToIds(text).ToList();
        srcIds.Add(pair.SrcEosId);
        int srcLen = srcIds.Count;

        // 2. 源语言张量：[1, srcLen]
        var srcShape = new long[] { 1, srcLen };
        var srcArray = srcIds.Select(id => (long)id).ToArray();
        var attentionMask = Enumerable.Repeat(1L, srcLen).ToArray();

        using var srcTensor = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance, new Memory<long>(srcArray), srcShape);
        using var maskTensor = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance, new Memory<long>(attentionMask), srcShape);

        // 3. 编码器推理一次
        var encInputs = new Dictionary<string, OrtValue>
        {
            [pair.EncoderInputName] = srcTensor,
            [pair.EncoderAttentionName] = maskTensor,
        };

        using var encResults = pair.EncoderSession.Run(
            new RunOptions(), encInputs, [pair.EncoderOutputName]);

        var encData = encResults[0].GetTensorDataAsSpan<float>();
        var encShape = encResults[0].GetTensorTypeAndShape().Shape.ToArray();
        using var encoderHidden = OrtValue.CreateTensorValueFromMemory<float>(
            OrtMemoryInfo.DefaultInstance,
            new Memory<float>(encData.ToArray()),
            encShape);

        // 4. 自回归解码：Marian 用 <pad> (== </s>) 作为起始 token
        var resultIds = new List<int> { pair.TgtPadId };
        int vocSize = pair.TgtVocabSize;

        for (int step = 0; step < MaxDecodeLen; step++)
        {
            ct.ThrowIfCancellationRequested();

            int tgtLen = resultIds.Count;
            var tgtShape = new long[] { 1, tgtLen };
            var tgtArray = resultIds.Select(id => (long)id).ToArray();

            using var tgtTensor = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, new Memory<long>(tgtArray), tgtShape);

            var decInputs = new Dictionary<string, OrtValue>
            {
                [pair.DecoderInputName] = tgtTensor,
                [pair.DecoderHiddenStateName] = encoderHidden,
                [pair.DecoderMaskName] = maskTensor,
            };

            using var decResults = pair.DecoderSession.Run(
                new RunOptions(), decInputs, [pair.LogitsName]);

            var logitsSpan = decResults[0].GetTensorDataAsSpan<float>();
            int offset = (tgtLen - 1) * vocSize;

            // argmax
            int bestId = 0;
            float bestVal = float.MinValue;
            for (int v = 0; v < vocSize; v++)
            {
                float val = logitsSpan[offset + v];
                if (val > bestVal) { bestVal = val; bestId = v; }
            }

            if (bestId == pair.TgtEosId)
                break;

            resultIds.Add(bestId);
        }

        // 5. 解码（跳过起始 token）
        var finalIds = resultIds.Skip(1).ToList();
        return pair.TgtTokenizer.Decode(finalIds) ?? string.Empty;
    }

    // ---------------- 内部类型 ----------------

    private sealed class LanguagePair
    {
        public InferenceSession EncoderSession { get; set; } = null!;
        public InferenceSession DecoderSession { get; set; } = null!;
        public SentencePieceTokenizer SrcTokenizer { get; set; } = null!;
        public SentencePieceTokenizer TgtTokenizer { get; set; } = null!;
        public int SrcEosId { get; set; }
        public int TgtEosId { get; set; }
        public int TgtPadId { get; set; }
        public int TgtVocabSize { get; set; }
        public string EncoderInputName { get; set; } = null!;
        public string EncoderAttentionName { get; set; } = null!;
        public string EncoderOutputName { get; set; } = null!;
        public string DecoderInputName { get; set; } = null!;
        public string DecoderHiddenStateName { get; set; } = null!;
        public string DecoderMaskName { get; set; } = null!;
        public string LogitsName { get; set; } = null!;
    }
}
