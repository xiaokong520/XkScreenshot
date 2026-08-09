using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.Core.Llm;

/// <summary>大模型 API 协议。</summary>
public enum ApiProtocol { OpenAI, Anthropic }

/// <summary>在线模式的所有连接参数。不落盘，不内置任何预填值。</summary>
public sealed record LlmApiConfig(
    ApiProtocol Protocol,
    string BaseUrl,
    string ApiKey,
    string Model
);

/// <summary>
/// 封装 OpenAI Responses API 与 Anthropic Messages API 两种协议的 HTTP 通信。
/// 只负责发请求、拿回原始响应字符串 —— 解析逻辑由各调用方自己管。
///
/// 不持有任何配置（配置由调用方在每次调用时传入），因此同一个实例可以给
/// OCR 和翻译共用，只需在创建时配好 HttpClient。
/// </summary>
public sealed class LlmApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public LlmApiClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Clear();
    }

    /// <summary>OCR：文本提示词 + 一张 base64 图片，返回 LLM 的原始响应文本。</summary>
    public async Task<string> ChatWithImageAsync(
        LlmApiConfig config, string prompt, string imageBase64, CancellationToken ct = default)
    {
        object body = config.Protocol == ApiProtocol.Anthropic
            ? BuildAnthropicImageRequest(config, prompt, imageBase64)
            : BuildOpenAiImageRequest(config, prompt, imageBase64);

        return await SendAsync(config, body, ct).ConfigureAwait(false);
    }

    /// <summary>翻译：system prompt + 纯文本 user 消息，返回 LLM 的原始响应文本。</summary>
    public async Task<string> ChatAsync(
        LlmApiConfig config, string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        object body = config.Protocol == ApiProtocol.Anthropic
            ? BuildAnthropicTextRequest(config, systemPrompt, userMessage)
            : BuildOpenAiTextRequest(config, systemPrompt, userMessage);

        return await SendAsync(config, body, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    // ---------------- 请求体构造 ----------------

    /// <summary>OpenAI Chat Completions API: 图片 + 文本。</summary>
    private static object BuildOpenAiImageRequest(LlmApiConfig config, string prompt, string imageBase64)
    {
        return new
        {
            model = config.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/png;base64,{imageBase64}" },
                        },
                    },
                },
            },
        };
    }

    private static object BuildAnthropicImageRequest(LlmApiConfig config, string prompt, string imageBase64)
    {
        return new
        {
            model = config.Model,
            max_tokens = 4096,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = "image/png",
                                data = imageBase64,
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>OpenAI Chat Completions API: 纯文本。</summary>
    private static object BuildOpenAiTextRequest(LlmApiConfig config, string systemPrompt, string userMessage)
    {
        var msgList = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });
        msgList.Add(new { role = "user", content = userMessage });

        return new
        {
            model = config.Model,
            messages = msgList.ToArray(),
        };
    }

    private static object BuildAnthropicTextRequest(LlmApiConfig config, string systemPrompt, string userMessage)
    {
        return new
        {
            model = config.Model,
            max_tokens = 4096,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage },
            },
        };
    }

    // ---------------- 发送 ----------------

    private async Task<string> SendAsync(LlmApiConfig config, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl)
        {
            Content = content,
        };

        if (config.Protocol == ApiProtocol.Anthropic)
        {
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 从 API 响应 JSON 中提取纯文本内容
        return ExtractText(raw, config.Protocol);
    }

    /// <summary>从 API 响应的 JSON 中提取实际文本内容。</summary>
    private static string ExtractText(string raw, ApiProtocol protocol)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (protocol == ApiProtocol.Anthropic)
            {
                // Anthropic: { "content": [{"type": "text", "text": "..."}] }
                if (doc.RootElement.TryGetProperty("content", out var contentList)
                    && contentList.ValueKind == JsonValueKind.Array
                    && contentList.GetArrayLength() > 0)
                {
                    foreach (var block in contentList.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                            && block.TryGetProperty("text", out var txt))
                            return txt.GetString() ?? raw;
                    }
                }
                return raw;
            }
            else
            {
                // OpenAI Chat Completions: { "choices": [{"message": {"content": "..."}}] }
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var txt))
                        return txt.GetString() ?? raw;
                }
                // 兼容：有些国内服务商可能不以 "choices" 包装
                return raw;
            }
        }
        catch
        {
            // 解析失败 — 返回原文（调用方会自己处理）
            return raw;
        }
    }
}
