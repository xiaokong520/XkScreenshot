using System;

namespace XkScreenshot.Core.Llm;

/// <summary>
/// 把用户填的服务地址补成真正要 POST 的那条端点。
///
/// 既然设置里已经要选协议，端点路径就是协议定死的（OpenAI 的 /v1/chat/completions、
/// Anthropic 的 /v1/messages），再让用户手抄一遍只是多一个抄错的机会，
/// 而抄错的表现是一个 404 —— 那个报错看不出错的是路径而不是 Key 或模型名。
///
/// 补法得容得下手上那串地址的各种形态：中转服务给的可能是根地址、可能带到 /v1、
/// 也可能就是一整条端点，粘进来常常还拖着一个斜杠。任何一种都得能用，
/// 所以这里只做「缺什么补什么」，从不推翻用户已经写明的部分。
/// </summary>
public static class LlmEndpoint
{
    /// <summary>各协议在版本段之后的那截路径。</summary>
    private static string PathFor(ApiProtocol protocol)
        => protocol == ApiProtocol.Anthropic ? "messages" : "chat/completions";

    /// <summary>
    /// 认得出来的端点末段。用来判断用户是不是已经写到端点了 ——
    /// 写到了就不能再往后接，还得能把别的协议的端点换成本协议的
    /// （切协议时地址栏里留着的往往是上一个协议那条）。
    /// 长的排前面：/v1/chat/completions 同时也以 /completions 结尾。
    /// </summary>
    private static readonly string[] KnownTails =
        ["chat/completions", "responses", "messages", "completions"];

    /// <summary>
    /// 解析出实际请求地址。地址为空时返回空串 —— 在线模式没配好，
    /// 让调用方按「未配置」处理，而不是在这儿拼出一个注定失败的 URL。
    /// </summary>
    public static string Resolve(ApiProtocol protocol, string? baseUrl)
    {
        string url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0) return string.Empty;

        // 只写了域名的按 https 处理，不然这串在 HttpClient 眼里根本不是个绝对地址
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;

        // 带查询串的原样发。Azure OpenAI 那种 ?api-version=… 的地址必然是完整端点，
        // 而且下面那些「以什么结尾」的判断遇上查询串全都不成立
        if (url.Contains('?', StringComparison.Ordinal)) return url;

        string tail = PathFor(protocol);
        if (EndsWithSegment(url, tail)) return url;

        // 写的是别的协议那条端点：砍掉换成本协议的，而不是当成基地址往后接
        foreach (var known in KnownTails)
        {
            if (!EndsWithSegment(url, known)) continue;
            url = url[..^(known.Length + 1)];
            break;
        }

        // 路径里已经有版本段就只接端点，没有就连版本段一起接
        return HasVersionSegment(url) ? $"{url}/{tail}" : $"{url}/v1/{tail}";
    }

    private static bool EndsWithSegment(string url, string segment)
        => url.EndsWith('/' + segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 路径里有没有 v1 / v1beta / v4 这样的版本段。
    ///
    /// 看整条路径而不只是末段，也不把版本号认死成 v1：兼容层常常挂在
    /// /v1beta/openai 这类路径下 —— 版本在中间，而且不叫 v1。
    /// 只看 //host 之后的部分，v2.example.com 这样的主机名不算。
    /// </summary>
    private static bool HasVersionSegment(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        int slash = url.IndexOf('/', scheme + 3);

        while (slash >= 0)
        {
            // 只看这一段的头两个字符，段尾在哪儿无所谓
            var rest = url.AsSpan(slash + 1);
            if (rest.Length >= 2 && (rest[0] is 'v' or 'V') && char.IsAsciiDigit(rest[1])) return true;

            slash = url.IndexOf('/', slash + 1);
        }

        return false;
    }
}
