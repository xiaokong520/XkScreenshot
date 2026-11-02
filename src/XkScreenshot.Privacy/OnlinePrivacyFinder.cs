using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Privacy;

/// <summary>
/// 在线模式的整图检测：把截图直接交给多模态模型，让它给出隐私区域。
///
/// 和离线那条路的区别：离线是「本地 OCR 框出行 → 规则判哪块要糊」，
/// 这里连框都由模型给。代价是坐标是模型估的，密集小字上容易偏几十像素，
/// 所以只在线模式下用，且用户明确选了「在线」才走。
///
/// 坐标约定用千分比（0~1000）而不是像素：模型不知道这张图多少像素，
/// 报像素必然偏；比例它还能对着画面大致量出来。
/// </summary>
internal static class OnlinePrivacyFinder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static string PromptFor(int width, int height) =>
$$"""
你是截图隐私检测器。这张图是 {{width}}×{{height}} 像素。找出图中所有需要打码的隐私区域，只返回一个 JSON 数组。

每一项的格式：
{"kind":"avatar|name|email|phone|idcard|bankcard|ip|address|secret","text":"该区域里的文字","box":[左,上,右,下]}

规则：
- box 用图片的像素坐标，取值 0 到 {{width}}（横）和 0 到 {{height}}（纵），顺序是 [左, 上, 右, 下]
- avatar：框住整张头像（圆形/方形图片本身），text 留空
- name：只框名字或昵称本身，不要含后面的时间戳、消息正文
- 邮箱、手机号、身份证号、银行卡号、IP、地址、密钥各自框住那一段文字
- 只框真正的隐私内容，不要框普通聊天正文、按钮、菜单、标题栏
- 图中没有隐私内容时返回 []

只返回 JSON 数组，不要任何解释、不要 Markdown 代码块。
""";

    public static async Task<List<PrivacyRegion>> FindAsync(
        LlmAccess llm, BitmapSource image, int width, int height, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        string base64 = EncodePngBase64(image);
        string reply = await llm.Client.ChatWithImageAsync(llm.Config, PromptFor(width, height), base64, timeout.Token)
            .ConfigureAwait(false);

        return Parse(reply, width, height);
    }

    /// <summary>把模型回复解析成区域。解析不出来就是空 —— 模型给错了不该让整次打码崩掉。</summary>
    internal static List<PrivacyRegion> Parse(string reply, int width, int height)
    {
        var result = new List<PrivacyRegion>();
        if (string.IsNullOrWhiteSpace(reply)) return result;

        int start = reply.IndexOf('[');
        int end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var image = new PixelRect(0, 0, width, height);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("box", out var box) || box.ValueKind != JsonValueKind.Array) continue;

                double[] nums = box.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN)
                    .ToArray();
                if (nums.Length != 4 || nums.Any(double.IsNaN)) continue;

                var rect = PixelRect.FromLtrb(
                    ToPixel(nums[0], width), ToPixel(nums[1], height),
                    ToPixel(nums[2], width), ToPixel(nums[3], height)).Intersect(image);
                if (rect.Width < 4 || rect.Height < 4) continue;

                string? kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString() : null;

                // 模型给的框就是它眼里的边界，别再按「贴字形」的思路外扩
                result.Add(new PrivacyRegion(rect, KindOf(kind)) { HorizontalPadding = 0 });
            }
        }
        catch (JsonException)
        {
            // 模型偶尔会带上解释文字或 Markdown 围栏，括号里不是合法 JSON 就放弃这一张
        }

        return result;
    }

    private static int ToPixel(double value, int total)
        => (int)Math.Round(Math.Clamp(value, 0, total));

    private static PrivacyKind KindOf(string? kind) => kind?.ToLowerInvariant() switch
    {
        "avatar" => PrivacyKind.Avatar,
        "email" => PrivacyKind.Email,
        "phone" => PrivacyKind.Phone,
        "idcard" => PrivacyKind.IdCard,
        "bankcard" => PrivacyKind.BankCard,
        "ip" => PrivacyKind.IpAddress,
        "address" => PrivacyKind.Address,
        "secret" => PrivacyKind.Secret,
        _ => PrivacyKind.Name,
    };

    private static string EncodePngBase64(BitmapSource image)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
