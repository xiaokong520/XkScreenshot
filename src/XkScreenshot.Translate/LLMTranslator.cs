using System.Threading;
using System.Threading.Tasks;
using XkScreenshot.Core.Llm;

namespace XkScreenshot.Translate;

/// <summary>在线翻译：把文本发给大模型，拿回译文。</summary>
public sealed class LLMTranslator : ITranslator
{
    private readonly LlmApiClient _client;
    private readonly LlmApiConfig _config;

    public LLMTranslator(LlmApiClient client, LlmApiConfig config)
    {
        _client = client;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        string systemPrompt = BuildSystemPrompt(sourceLang, targetLang);

        // 用三个竖线分隔多行，比换行更不容易被 API 吞掉
        string formatted = text.Replace("\n", "\n|||\n");

        string raw = await _client.ChatAsync(_config, systemPrompt, formatted, ct)
            .ConfigureAwait(false);

        return raw.Trim();
    }

    private static string BuildSystemPrompt(string sourceLang, string targetLang)
    {
        return $"""
你是专业翻译引擎。把用户发来的文本从 {sourceLang} 翻译成 {targetLang}。
用户用 ||| 分隔了多行，你也要用 ||| 分隔对应的译文，保持行数一致。

规则：
- 只返回译文，不要任何解释、前缀或后缀。
- 专有名词、代码、数字原样保留。
- 已是目标语言的句子直接返回原文。
- 保持原文的语气和格式。
""";
    }
}
