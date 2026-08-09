using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace XkScreenshot.Translate;

/// <summary>
/// OPUS-MT 离线翻译引擎：Helsinki-NLP Marian 模型，通过 ONNX Runtime 推理。
///
/// 使用 onnx-community 提供的 ONNX 模型（encoder/decoder 分离）：
/// <c>models/opus-mt/{from}-{to}/</c> 下应有：
/// <c>encoder_model.onnx</c>、<c>decoder_model.onnx</c>、
/// <c>source.spm</c>、<c>target.spm</c>。
///
/// 首次使用时加载已配置的语言对；翻译时按源→目标语言选对应模型。
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

            // 兼容旧 JSON 格式
            if (!File.Exists(srcTokPath))
                srcTokPath = Path.Combine(dir, "tokenizer.src.json");
            if (!File.Exists(tgtTokPath))
                tgtTokPath = Path.Combine(dir, "tokenizer.tgt.json");

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
            var srcTok = new SentencePieceTokenizer(srcTokPath);
            var tgtTok = new SentencePieceTokenizer(tgtTokPath);

            var pair = new LanguagePair
            {
                EncoderSession = encSession,
                DecoderSession = decSession,
                SrcTokenizer = srcTok,
                TgtTokenizer = tgtTok,
                EncoderInputName = encSession.InputMetadata.Keys.First(),
                EncoderAttentionName = encSession.InputMetadata.Keys.Skip(1).FirstOrDefault() ?? "attention_mask",
                EncoderOutputName = encSession.OutputMetadata.Keys.First(),
                DecoderInputName = decSession.InputMetadata.Keys.First(),
                DecoderHiddenStateName = decSession.InputMetadata.Keys.Skip(1).FirstOrDefault() ?? "encoder_hidden_states",
                DecoderMaskName = decSession.InputMetadata.Keys.Skip(2).FirstOrDefault() ?? "encoder_attention_mask",
                LogitsName = decSession.OutputMetadata.Keys.First(),
            };
            _pairs[$"{from}→{to}"] = pair;
        }

        if (_pairs.Count == 0)
            throw new InvalidOperationException("没有配置任何离线翻译语言对。");
    }

    /// <inheritdoc />
    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        string key = $"{sourceLang}→{targetLang}";
        if (!_pairs.TryGetValue(key, out var pair))
            throw new NotSupportedException(
                $"没有 {sourceLang}→{targetLang} 的离线翻译模型。请在设置中下载该语言对。");

        return await Task.Run(() => TranslateGreedy(pair, text, ct), ct).ConfigureAwait(false);
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
        // 1. 源语言分词
        var srcIds = pair.SrcTokenizer.Encode(text);
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

        // 复制 encoder 输出到独立 buffer，供 decoder 循环复用
        var encData = encResults[0].GetTensorDataAsSpan<float>();
        var encShape = encResults[0].GetTensorTypeAndShape().Shape.ToArray();
        using var encoderHidden = OrtValue.CreateTensorValueFromMemory<float>(
            OrtMemoryInfo.DefaultInstance,
            new Memory<float>(encData.ToArray()),
            encShape);

        // encResults 释放：using 块在方法末尾自动处置

        // 4. 自回归解码
        var resultIds = new List<int> { pair.TgtTokenizer.BosId };
        int tgtVocabSize = pair.TgtTokenizer.VocabSize;

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

            // 输出形状：[1, tgtLen, vocabSize]，取最后一步
            var logitsSpan = decResults[0].GetTensorDataAsSpan<float>();
            int offset = (tgtLen - 1) * tgtVocabSize;

            // argmax
            int bestId = 0;
            float bestVal = float.MinValue;
            for (int v = 0; v < tgtVocabSize; v++)
            {
                float val = logitsSpan[offset + v];
                if (val > bestVal) { bestVal = val; bestId = v; }
            }

            if (bestId == pair.TgtTokenizer.EosId)
                break;

            resultIds.Add(bestId);
        }

        // 5. 解码（跳过 BOS）
        var finalIds = resultIds.Skip(1).ToList();
        return pair.TgtTokenizer.Decode(finalIds);
    }

    // ---------------- 内部类型 ----------------

    private sealed class LanguagePair
    {
        public required InferenceSession EncoderSession { get; init; }
        public required InferenceSession DecoderSession { get; init; }
        public required SentencePieceTokenizer SrcTokenizer { get; init; }
        public required SentencePieceTokenizer TgtTokenizer { get; init; }
        public required string EncoderInputName { get; init; }
        public required string EncoderAttentionName { get; init; }
        public required string EncoderOutputName { get; init; }
        public required string DecoderInputName { get; init; }
        public required string DecoderHiddenStateName { get; init; }
        public required string DecoderMaskName { get; init; }
        public required string LogitsName { get; init; }
    }
}
