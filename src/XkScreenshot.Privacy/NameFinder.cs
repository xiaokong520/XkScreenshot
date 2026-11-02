using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 找人名 —— 不靠认字，靠位置和上下文。
///
/// 一个词是不是人名，光看文字几乎判断不了（「张伟」「Summer」「老王」都行），
/// 但名字在界面上出现的地方很固定：「发件人：」后面、@ 后面、头像右边那一行的开头。
/// 这三个位置以外的名字交给可选的 <see cref="LlmNameClassifier"/>。
/// </summary>
internal static class NameFinder
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> UiLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "设置", "首页", "搜索", "返回", "确定", "取消", "保存", "删除", "下载", "上传", "分享", "更多",
        "文件", "文件夹", "图片", "视频", "应用", "帮助", "通知", "消息", "联系人", "收藏", "历史记录",
        "Settings", "Home", "Search", "Back", "Save", "Cancel", "Download", "Upload", "Share", "Help",
    };

    /// <summary>「姓名：张三」这类带标签的，只糊冒号后面那一截。</summary>
    private static readonly Regex Keyword = new(
        @"(?:姓名|名字|昵称|用户名|联系人|发件人|收件人|负责人|经办人|作者|(?<![A-Za-z])(?i:name|from|to|author|user(?:name)?))\s*[:：]\s*(?<v>\S(?:.{0,29}?\S)?)(?=\s+(?:电话|手机|邮箱|地址|姓名|昵称|(?i:phone|email|address|name))\s*[:：=]|\s{2,}|\s*[,，;；|<(（]|$)",
        Options, MatchTimeout);

    /// <summary>
    /// 群聊昵称：「【头衔】昵称」整行都是名字。
    ///
    /// 这种写法在 QQ、企业微信、Discord 汉化版里都一样，头衔里带「！」「·」是常态，
    /// 后面还可能跟一段空格分隔的备注。它跟头像没关系 —— 图片底部被裁掉一半的头像
    /// 检测不到，昵称照样得糊。
    /// </summary>
    private static readonly Regex GroupName = new(
        @"^(?<v>【[^】\r\n]{1,12}】\s*\S[^\r\n]{0,47})$", Options, MatchTimeout);

    /// <summary>
    /// 【】里装的是系统标签而不是昵称头衔的那些。QQ 群里「【公告】…」「【文件】xxx.mp3」
    /// 和昵称长得一模一样，实测整段公告和文件名都被糊掉了。
    /// </summary>
    private static readonly HashSet<string> GroupTags = new(StringComparer.Ordinal)
    {
        "公告", "群公告", "文件", "图片", "视频", "语音", "链接", "红包", "转账", "投票",
        "群通知", "通知", "系统消息", "新闻", "直播", "小程序", "位置", "名片", "表情",
        "动画表情", "聊天记录", "合并转发", "收藏", "打卡", "签到", "接龙", "作业", "问卷",
        "文件传输助手", "群助手", "群管家",
    };

    /// <summary>带扩展名的多半是文件名，不是昵称。</summary>
    private static readonly Regex Fileish = new(
        @"\.(?:mp3|mp4|wav|avi|mkv|mov|jpg|jpeg|png|gif|webp|bmp|pdf|docx?|xlsx?|pptx?|zip|rar|7z|txt|csv|json)\b",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>
    /// 名字那行后面常跟着的时间、日期、状态 —— 从第一个命中处截断，后面的不算名字。
    /// 括号不在此列：群聊里「【头衔】昵称（备注#id）」整个都是名字，括号里的备注和 id 更要糊。
    /// </summary>
    private static readonly Regex Trailer = new(
        @"\s*(?:\d{1,2}:\d{2}|\d{1,4}[/\-年]\d{1,2}(?:[/\-月]\d{1,2}日?)?|昨天|前天|今天|刚刚|星期[一二三四五六日天]|周[一二三四五六日天]|上午|下午|凌晨|晚上|\d+\s*(?:分钟|小时|天)前|[|·•<]).*$",
        Options, MatchTimeout);

    /// <summary>名字最长多少个字符。群聊昵称带头衔和备注能到二三十个字。</summary>
    private const int MaxNameLength = 32;

    /// <summary>
    /// 一行字（去掉时间戳之后）像不像一个名字：不空、不超过 <see cref="MaxNameLength"/> 个字、不全是数字标点。
    /// 头像判定也要用它：孤零零一块彩色的方块，右边紧挨着一个短名字，才敢认成头像。
    /// </summary>
    public static bool LooksLikeName(string text)
    {
        var name = NamePart(text);
        return name.Length > 0;
    }

    /// <summary>行首那段名字的长度（原始下标），不像名字时是 0。</summary>
    internal static int NameLength(string text) => NamePart(text).Length;

    private static string NamePart(string text, bool excludeUiLabels = true)
    {
        string normalized = TextPatterns.Normalize(text);
        var m = Trailer.Match(normalized);
        // 时间戳前面若有没闭合的括号（「张三（10:32」），把括号也去掉
        string head = (m.Success ? normalized[..m.Index] : normalized).TrimEnd(' ', ':', '：', ',', '，', '-', '(', '（', '[', '【');
        string core = head.Trim();
        if (core.Length is 0 or > MaxNameLength) return "";
        if (!core.Any(char.IsLetter) || (excludeUiLabels && UiLabels.Contains(core))) return "";

        // 「【头衔】昵称」里带感叹号、空格、备注括号都是常态，不能按「一整句话」处理
        bool titled = core.StartsWith('【');
        // 一整句话不是名字：带句末标点、或者空格分出三段以上
        if (!titled && core.IndexOfAny(['。', '！', '？', '!', '?', '，', ':', '：', '=', '/', '\\']) >= 0) return "";
        if (head.EndsWith('.') || core.Contains('@')) return "";
        if (core.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > (titled ? 6 : 3)) return "";
        // 归一化是逐字符替换，下标和原文一一对应
        return text[..head.Length];
    }

    /// <summary>一行文字里靠关键词、@ 提及和群昵称格式认出来的名字片段。已被别的规则占掉的（比如邮箱）不再算。</summary>
    internal static List<TextSpan> FindInText(string text, IReadOnlyList<TextSpan> taken)
    {
        var found = new List<TextSpan>();
        if (string.IsNullOrWhiteSpace(text)) return found;

        string normalized = TextPatterns.Normalize(text);

        foreach (Match m in Keyword.Matches(normalized))
        {
            var g = m.Groups["v"];
            if (!g.Success || NamePart(g.Value, excludeUiLabels: false).Length == 0) continue;

            var span = new TextSpan(g.Index, g.Length, PrivacyKind.Name);
            if (taken.Any(t => t.Overlaps(span)) || found.Any(f => f.Overlaps(span))) continue;
            found.Add(span);
        }

        // 「@张三」不再算名字：聊天、评论里的 @ 提及是消息正文的一部分，
        // 糊掉以后整句话就读不通了，而那个昵称本来就会在头像旁边被糊一次。
        foreach (Match m in GroupName.Matches(normalized))
        {
            var g = m.Groups["v"];
            if (!g.Success) continue;

            string value = g.Value;
            int close = value.IndexOf('】');
            if (close > 1 && GroupTags.Contains(value[1..close].Trim())) continue;
            if (Fileish.IsMatch(value)) continue;
            // 昵称里不会出现这些排版符号。浏览器标签页标题也常以【】开头
            //（「【某某社区】活动通知：×」这种浏览器标签），实测整条被当成昵称糊掉。
            if (value.IndexOfAny(['：', ':', '×', '|', '—']) >= 0) continue;

            var span = new TextSpan(g.Index, g.Length, PrivacyKind.Name);
            if (taken.Any(t => t.Overlaps(span)) || found.Any(f => f.Overlaps(span))) continue;
            found.Add(span);
        }

        return found;
    }

    /// <summary>
    /// 窗口/页面最上方的标题名。
    ///
    /// 聊天窗口顶部的联系人名不在「头像旁边」「标签后面」任何一条固定位置规则里，
    /// 但它有两个跟正文不一样的硬特征：字号比正文大、而且顶部那一带只有它一行
    /// （导航栏、标签页是好几行挤在一起）。两条都满足才打 —— 只靠「像名字」的话，
    /// 任何页面的主标题都会被误伤。
    /// </summary>
    internal static List<PixelRect> FindTitle(IReadOnlyList<OcrLine> lines, int imageHeight)
    {
        var found = new List<PixelRect>();
        if (lines.Count < 2) return found;

        var heights = lines.Select(l => l.Bounds.Height).OrderBy(h => h).ToList();
        int median = heights[heights.Count / 2];
        if (median <= 0) return found;

        int top = Math.Max(8, imageHeight / 20);

        foreach (var line in lines)
        {
            var b = line.Bounds;
            if (b.Y > top) continue;
            if (b.Height < median * 1.05) continue;
            // 名字不会占满一整行：浏览器标签栏那种十几个字的标题，宽高比一眼就露馅
            if (b.Width > b.Height * 7) continue;
            if (lines.Any(o => o.Bounds != b && o.Bounds.Y <= top)) continue;

            int length = NameLength(line.Text);
            if (length < 2) continue;
            found.Add(LineSlicer.Slice(line, 0, length));
        }

        return found;
    }

    /// <summary>
    /// 头像旁边的名字：左边缘在头像右侧 1.5 倍行高以内、跟头像上半部分有纵向重叠的那一行
    /// （头像在上方时则是紧贴头像下沿、水平居中的那一行），从行首糊到时间戳之前。
    /// </summary>
    internal static List<PixelRect> FindBesideAvatars(IReadOnlyList<OcrLine> lines, IReadOnlyList<Avatar> avatars)
    {
        var found = new List<PixelRect>();

        foreach (var avatar in avatars)
        {
            var a = avatar.Bounds;
            int best = -1;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < lines.Count; i++)
            {
                if (!LooksLikeName(lines[i].Text)) continue;
                var b = lines[i].Bounds;
                int h = Math.Max(1, b.Height);
                int distance;

                if (avatar.Above)
                {
                    int gap = b.Y - a.Bottom;
                    int center = b.X + b.Width / 2;
                    if (gap < -2 || gap > h * 2) continue;
                    if (Math.Abs(center - (a.X + a.Width / 2)) > a.Width) continue;
                    distance = gap;
                }
                else
                {
                    int gap = b.X - a.Right;
                    // 头像和昵称之间的留白各家不一样：QQ 群聊比微信宽两个像素，卡太死整行名字就漏了
                    if (gap < -2 || gap > h * 2 + 6) continue;
                    // 昵称跟头像顶对齐（实测差 3px 上下）；气泡里的消息文字要往下让出内边距，
                    // 单行消息也有 13~17px。这个差值比「间距」可靠得多 —— 微信里两者离头像一样远。
                    if (b.Bottom <= a.Y || b.Y - a.Y > a.Height * 0.15) continue;
                    distance = Math.Abs(b.Y - a.Y);
                }

                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }

            if (best < 0) continue;

            var line = lines[best];
            int length = NameLength(line.Text);
            if (length == 0) continue;
            found.Add(LineSlicer.Slice(line, 0, length));
        }

        return found;
    }
}
