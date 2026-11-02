using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 可选的在线名字识别：把识别出的文字（编号后，只有文字，不带图）发给大模型，让它指出哪些是人名、机构名。
///
/// 本地规则只认得出「固定位置上的名字」，正文里顺口提到的「跟李雷说一下」它无能为力 ——
/// 这正是语言模型擅长的事。但它要把文字发出去，所以默认关着，由用户在设置里明确打开。
/// </summary>
internal static class LlmNameClassifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string SystemPrompt =
        "You find personal names and organization names in OCR text from a screenshot. " +
        "Each input line is \"<index>: <text>\". " +
        "Reply with ONLY a JSON array like [{\"line\":0,\"text\":\"张三\"}], where text is copied verbatim from that line. " +
        "Include people's names, nicknames, usernames and company/organization names. " +
        "Do NOT include email addresses, phone numbers, ID numbers, dates, times, or common words. " +
        "Reply [] if there are none.";

    public static async Task<List<(int Line, TextSpan Span)>> ClassifyAsync(
        LlmAccess llm, IReadOnlyList<OcrLine> lines, CancellationToken ct)
    {
        var found = new List<(int, TextSpan)>();
        if (lines.Count == 0) return found;

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
            sb.Append(i).Append(": ").Append(lines[i].Text.Replace('\n', ' ')).Append('\n');

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        string reply = await llm.Client.ChatAsync(llm.Config, SystemPrompt, sb.ToString(), timeout.Token)
            .ConfigureAwait(false);

        foreach (var (line, text) in Parse(reply))
        {
            if (line < 0 || line >= lines.Count || string.IsNullOrWhiteSpace(text)) continue;

            // 模型给回来的片段必须原样在那一行里 —— 找不到就是它编的，丢掉
            int at = lines[line].Text.IndexOf(text, StringComparison.Ordinal);
            if (at < 0) continue;
            found.Add((line, new TextSpan(at, text.Length, PrivacyKind.Name)));
        }

        return found;
    }

    /// <summary>从回复里抠出那个 JSON 数组。模型常把它包在 ```json 里，或者前后再说两句。</summary>
    internal static List<(int Line, string Text)> Parse(string reply)
    {
        var result = new List<(int, string)>();
        int start = reply.IndexOf('[');
        int end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("line", out var l) || !item.TryGetProperty("text", out var t)) continue;

                int line = l.ValueKind switch
                {
                    JsonValueKind.Number => l.TryGetInt32(out int n) ? n : -1,
                    JsonValueKind.String => int.TryParse(l.GetString(), out int n) ? n : -1,
                    _ => -1,
                };
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { } text) result.Add((line, text));
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }
}
