using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.Translate;

/// <summary>
/// 翻译引擎的公共接口。
/// sourceLang / targetLang 用 BCP-47 标签（"zh"、"en"、"ja"、"auto" 等）。
/// </summary>
public interface ITranslator
{
    /// <summary>翻译一段文本。一批行合在一起送进来，保持原文换行结构出来。</summary>
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default);
}
