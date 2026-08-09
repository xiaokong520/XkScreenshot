using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace XkScreenshot.Translate;

/// <summary>
/// 翻译缓存：按源文本的 SHA256 hash 记住翻译结果。
/// 生命周期绑定一次截图会话 —— 会话结束就清掉。
/// 同一段文字（导航栏、重复段落等）只翻译一次。
/// </summary>
public sealed class TranslationCache
{
    private readonly Dictionary<string, string> _cache = [];

    /// <summary>查缓存。未命中返回 null。</summary>
    public string? Get(string text, string sourceLang, string targetLang)
    {
        string key = MakeKey(text, sourceLang, targetLang);
        return _cache.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>写入缓存。</summary>
    public void Set(string text, string sourceLang, string targetLang, string translation)
    {
        string key = MakeKey(text, sourceLang, targetLang);
        _cache[key] = translation;
    }

    /// <summary>清空所有缓存条目。</summary>
    public void Clear() => _cache.Clear();

    private static string MakeKey(string text, string sourceLang, string targetLang)
    {
        string comb = $"{sourceLang}→{targetLang}:{text}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(comb));
        return Convert.ToHexString(hash);
    }
}
