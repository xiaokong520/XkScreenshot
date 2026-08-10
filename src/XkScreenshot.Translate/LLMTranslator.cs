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
        string raw = await _client
            .ChatAsync(_config, BuildSystemPrompt(sourceLang, targetLang), text, ct)
            .ConfigureAwait(false);

        return raw.Trim();
    }

    /// <summary>
    /// 语种在提示词里写中文名而不是 zh / zh_hant 这种代码：代码是模型库的内部约定，
    /// 「翻译成 zh_hant」得靠模型去猜那是什么，而「翻译成中文（繁体）」不用猜。
    ///
    /// 源语种只当提示给，不当命令下 —— 它是按字形猜出来的，拉丁字母那一族根本分不开，
    /// 德语十有八九会被报成英语。说死了反而会把模型往错的方向带。
    /// </summary>
    private static string BuildSystemPrompt(string sourceLang, string targetLang)
    {
        string target = BergamotCatalog.DisplayName(targetLang);
        string source = BergamotCatalog.DisplayName(sourceLang);
        string hint = sourceLang is "auto" or "" || source == sourceLang
            ? "原文是什么语言你自己判断。"
            : $"原文大致是{source}，但以实际内容为准。";

        return $"""
你是专业翻译引擎。把用户发来的文本翻译成{target}。{hint}

原文是从截图上逐行抓下来的，一句话常常被排版断成好几行：按整句的意思翻，
不要逐行硬对，也不要把断行处的半句话当成独立一句。空行是段落分隔，保留。

规则：
- 只返回译文，不要任何解释、前缀或后缀，不要重复原文。
- 专有名词、代码、数字、网址原样保留。
- 已经是{target}的句子照原样返回。
- 保持原文的语气。
""";
    }
}
