using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.Translate;

/// <summary>
/// 翻译引擎的公共接口。
/// sourceLang / targetLang 用 BCP-47 标签（"zh"、"en"、"ja"、"auto" 等）。
/// </summary>
public interface ITranslator
{
    /// <summary>
    /// 翻译一段文本。一批行合在一起送进来，段落结构照原样出来。
    ///
    /// 注意出来的行数不一定等于进去的行数：OCR 给的是图上的视觉行，
    /// 其中被排版折断的那些会先接回成句子再翻，否则每一截都缺上下文。
    /// </summary>
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default);
}

/// <summary>
/// 报得出自己认识哪些语种的翻译引擎。
///
/// 单独一个接口而不是并进 <see cref="ITranslator"/>：在线引擎把这两件事都交给了
/// 大模型，答不上来「你能翻成什么」，不该被逼着实现一份假的。界面按
/// <c>is ILanguageCatalog</c> 分流 —— 问得出就照着列，问不出就用一份通用清单。
/// </summary>
public interface ILanguageCatalog
{
    /// <summary>
    /// 判这段文字是什么语种。
    ///
    /// 报的是它**实际**是什么，不管自己翻不翻得了 —— 翻不了是下一步的事，
    /// 由 <see cref="TargetsFrom"/> 返回空来表达。两件事混在一起，就没法对用户说清
    /// 「认得出是韩文，但你没装韩语模型」，只能默默按别的语种翻出一堆不知所云。
    /// </summary>
    string DetectLanguage(string text);

    /// <summary>
    /// 从这个源语种出发能到的所有目标语种，含要中转的。
    /// 返回空表示这个语种当前翻不了（多半是模型没装）。
    /// </summary>
    IReadOnlyList<string> TargetsFrom(string source);

    /// <summary>语种代码对应的中文名，不认识就返回代码本身。</summary>
    string DisplayName(string code);
}
