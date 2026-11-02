using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XkScreenshot.Privacy;

/// <summary>一行文字里的一段：从第几个字符开始、多长、是什么。下标针对的是原始文本。</summary>
internal readonly record struct TextSpan(int Start, int Length, PrivacyKind Kind)
{
    public int End => Start + Length;
    public bool Overlaps(TextSpan o) => Start < o.End && o.Start < End;
}

/// <summary>
/// 靠文字本身就能认出来的隐私信息：邮箱、手机号、证件号、卡号、IP、密钥。
///
/// 数字类的规则都带校验（身份证校验码、银行卡 Luhn）。截图里满屏都是订单号、时间戳、
/// 版本号，光看位数的话每张图都能糊掉一大片不相干的数字 —— 打码打多了，
/// 用户要一个个删，这个功能就不如手工框来得快了。
/// </summary>
internal static class TextPatterns
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex EmailLabel = new(@"(?:邮箱|邮件|电邮|(?i:e-?mail))\s*[:：=]?\s*$", Options, MatchTimeout);
    private static readonly Regex NumericQq = new(@"^\d{5,12}[@aeQ]?qq\.com$", Options, MatchTimeout);

    /// <summary>
    /// 一条规则。命名组 v 存在时只糊 v 那一段 —— 「密码：xxx」里要遮的是 xxx，
    /// 连「密码」两个字一起糊掉，看图的人就不知道那里原本是什么了。
    /// </summary>
    private sealed record Rule(PrivacyKind Kind, Regex Regex, Func<string, bool>? Validate = null, bool NeedsEmailContext = false);

    /// <summary>
    /// 按优先级排：前面的先占位，后面的跟它重叠就让。
    /// 密钥最先 —— sk-xxx 里的数字串不该再被当成银行卡号去拆；
    /// 身份证排在手机号前面，否则 18 位证件号中间那 11 位会先被认成手机号。
    /// </summary>
    private static readonly Rule[] Rules =
    [
        new(PrivacyKind.Secret, new Regex(
            @"(?<v>sk-(?:proj-|ant-)?[A-Za-z0-9_\-]{20,})" +
            @"|(?<v>gh[pousr]_[A-Za-z0-9]{30,})" +
            @"|(?<v>github_pat_[A-Za-z0-9_]{22,})" +
            @"|(?<v>AKIA[0-9A-Z]{16})" +
            @"|(?<v>AIza[0-9A-Za-z_\-]{35})" +
            @"|(?<v>xox[abprs]-[A-Za-z0-9\-]{10,})" +
            @"|(?<v>eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})", Options, MatchTimeout)),

        // 「密码: xxx」「token = xxx」这种带标签的，标签留着，只糊后面的值
        new(PrivacyKind.Secret, new Regex(
            @"(?i)(?:api[_\- ]?key|secret|token|password|passwd|pwd|密码|密钥|口令)\s*[""']?\s*[:=：]\s*[""']?(?<v>[^\s""',，;；}{]{4,})",
            Options, MatchTimeout)),

        new(PrivacyKind.Email, new Regex(
            @"(?<v>[A-Za-z0-9._%+\-]+ *@ *[A-Za-z0-9\-]+(?: *\. *[A-Za-z0-9\-]+)* *\. *[A-Za-z]{2,})", Options, MatchTimeout)),

        // 中文字体里的 @ 又小又圆，OCR 常把它读丢，或读成 a、e、Q。
        // 通用规则认不出没有 @ 的邮箱，只能靠常见邮箱域名兜底
        new(PrivacyKind.Email, new Regex(
            @"(?<![A-Za-z0-9._%+\-])(?<v>[A-Za-z0-9._%+\-]{3,} ?[@aeQ]? ?(?:qq|vip\.qq|foxmail|163|126|yeah|sina|sohu|aliyun|139|189|gmail|outlook|hotmail|live|icloud|yahoo)\.(?:com|cn|net)(?:\.cn)?)",
            Options, MatchTimeout), NeedsEmailContext: true),

        new(PrivacyKind.IdCard, new Regex(
            @"(?<![0-9A-Za-z])(?<v>\d{6} ?(?:18|19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01]) ?\d{3}[\dXx])(?![0-9A-Za-z])",
            Options, MatchTimeout), IsValidIdCard),

        new(PrivacyKind.Phone, new Regex(
            @"(?<![0-9A-Za-z_+])(?<v>(?:\+?86[ \-]?)?1[3-9]\d(?:[ \-]?\d{4}){2})(?![0-9A-Za-z_])", Options, MatchTimeout)),

        // 只收 3~6 开头的：国内银联 62、Visa 4、万事达 5、JCB/运通 3。
        // Luhn 对随机数字串有一成的误判率，再卡一道开头能把订单号挡掉一大半
        new(PrivacyKind.BankCard, new Regex(
            @"(?<![0-9A-Za-z_])(?<v>[3-6](?:[ \-]?\d){12,18})(?![0-9A-Za-z_])", Options, MatchTimeout), IsValidCard),

        new(PrivacyKind.IpAddress, new Regex(
            @"(?<![\d.])(?<v>(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d))(?![\d.])",
            Options, MatchTimeout)),

        // 邮寄地址：至少要落下「市 + 区/县」，再一直吃到街道门牌。
        //
        // 不要求最前面的省 —— 微信的定位卡片、快递单常把省省掉。反过来，
        // 只有「XX 市 XX 区」而没有街道门牌的（「他在福州市台江区上班」）不算，
        // 这种句子在聊天里太常见，糊掉只会让人一个个去删。
        //
        // 中间那段用贪婪匹配：地址行尾巴上常跟着「附近」「旁边」这类词，
        // 非贪婪会在第一个「路」字就收尾，把「交通路」后面的门牌号漏在外面。
        new(PrivacyKind.Address, new Regex(
            @"(?<v>(?:[\u4e00-\u9fa5]{2,7}(?:省|自治区|特别行政区))?" +
            @"[\u4e00-\u9fa5]{2,7}(?:市|自治州|地区|盟)" +
            @"[\u4e00-\u9fa5]{2,7}(?:区|县|市|旗)" +
            @"[\u4e00-\u9fa5A-Za-z0-9\-]{1,40}" +
            @"(?:街道|路|街|大道|胡同|巷|弄|村|镇|小区|大厦|广场|花园|公寓|号楼|号院|号|栋|幢|单元|室|层))",
            Options, MatchTimeout)),
    ];

    /// <summary>找出一行里所有要糊的片段，按起点排序、互不重叠。</summary>
    public static List<TextSpan> Find(string text, PrivacyOptions options)
    {
        var found = new List<TextSpan>();
        if (string.IsNullOrWhiteSpace(text)) return found;

        string normalized = Normalize(text);

        foreach (var rule in Rules)
        {
            if (!options.Wants(rule.Kind)) continue;

            foreach (Match m in rule.Regex.Matches(normalized))
            {
                var group = m.Groups["v"];
                if (!group.Success || group.Length == 0) continue;
                if (rule.Validate is not null && !rule.Validate(group.Value)) continue;
                if (rule.NeedsEmailContext && !NumericQq.IsMatch(group.Value)
                    && !EmailLabel.IsMatch(normalized[..group.Index])) continue;

                var span = new TextSpan(group.Index, group.Length, rule.Kind);
                if (found.Any(f => f.Overlaps(span))) continue;
                found.Add(span);
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// 全角转半角，一个字符换一个字符，下标原样对得上。
    ///
    /// OCR 认中文界面时常把 @ 认成 ＠、把数字认成全角数字 ——
    /// 不先抹平，规则里每个字符都得写成 [@＠] 这种样子，没人维护得动。
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
            else if (c is '　' or '\u00a0' or '\u202f') sb.Append(' ');
            // OCR 把 @ 认成形近的圈字符
            else if (c is '©' or '®' or 'ⓐ' or '⑧') sb.Append('@');
            // 句号夹在字母数字之间多半是 OCR 把域名里的点认成了「。」
            else if (c == '。' && sb.Length > 0 && char.IsAsciiLetterOrDigit(sb[^1])) sb.Append('.');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string DigitsOnly(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsAsciiDigit(c) || c is 'X' or 'x') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>GB 11643 校验码：前 17 位加权求和模 11，查表得第 18 位。</summary>
    internal static bool IsValidIdCard(string raw)
    {
        string id = DigitsOnly(raw);
        if (id.Length != 18) return false;
        // 校验码并不能保证出生日期存在（例如 2 月 31 日也能算出合法校验码）。
        if (!int.TryParse(id.AsSpan(6, 4), out int year) || year is < 1800 or > 2099
            || !int.TryParse(id.AsSpan(10, 2), out int month) || month is < 1 or > 12
            || !int.TryParse(id.AsSpan(12, 2), out int day) || day < 1
            || day > DateTime.DaysInMonth(year, month)) return false;

        ReadOnlySpan<int> weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        const string checks = "10X98765432";

        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            if (!char.IsAsciiDigit(id[i])) return false;
            sum += (id[i] - '0') * weights[i];
        }

        return char.ToUpperInvariant(id[17]) == checks[sum % 11];
    }

    internal static bool IsValidCard(string raw)
    {
        string digits = DigitsOnly(raw);
        if (digits.Length is < 13 or > 19) return false;
        if (digits.Distinct().Count() == 1) return false;

        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(digits[i])) return false;
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }

        return sum % 10 == 0;
    }
}
