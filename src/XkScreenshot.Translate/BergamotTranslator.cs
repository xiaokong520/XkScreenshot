using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BergamotTranslatorSharp;

namespace XkScreenshot.Translate;

/// <summary>
/// Bergamot 离线翻译引擎。
///
/// 模型是 Mozilla 为「浏览器里实时翻整页网页」训练的：大模型蒸馏出 tiny/base 学生模型，
/// 再做 intgemm 8-bit 量化，一个方向 15~60 MB。截图 → OCR → 短句翻译这个场景对
/// 速度和语种数量的要求跟翻网页几乎重合，所以拿它替掉了原来那套一个方向 426 MB 的
/// OPUS-MT fp32 权重。
///
/// 磁盘上小不等于跑起来小：一个方向真跑起来要 640 MB 内存（见 <see cref="_service"/>），
/// 所以服务只留当前这一条路线，而且空闲久了整个放掉。
///
/// 模型全是「某语言 ↔ 英语」，没有任何非英语之间的直连，所以中→日是中→英→日两跳 ——
/// 这不是我们的取舍，是模型库本身就这么发的。原生库支持一次调用串起两个模型，
/// 中间那段英文不落地。
/// </summary>
public sealed class BergamotTranslator : ITranslator, ILanguageCatalog, IDisposable
{
    /// <summary>非英语之间的中转语言。模型库只有 X↔en，绕不开它。</summary>
    private const string Pivot = "en";

    /// <summary>一次送进去多少行。分批是为了让取消键有地方插进来 —— 原生调用本身不可中断。</summary>
    private const int LinesPerBatch = 16;

    /// <summary>
    /// 这么久没翻过东西就把已经建好的服务放掉，下次用再重建（加载约 80 MB / 两百毫秒）。
    /// 理由和识别那边一样：常驻托盘的工具不该为一个偶尔用一次的功能长期占着内存。
    /// </summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);

    private readonly string _root;
    private readonly HashSet<string> _installed;

    /// <summary>
    /// 当前这条路线的服务，以及它是按哪条路线建的。
    ///
    /// 只留一条：一个方向跑起来要 640 MB（模型文件才 40~60 MB，剩下的是原生层为计算图
    /// 开的内存，workspace、mini-batch-words 怎么调都压不下去，实测过），
    /// 而中转路线一条就含两个方向。把翻过的方向都留着，换几次目标语种就是几个 G。
    /// </summary>
    private BlockingService? _service;
    private string? _serviceKey;

    /// <summary>
    /// 一把锁管住服务和翻译调用两件事。
    ///
    /// 原生的 BlockingService 不是线程安全的，同一个实例并发调用会崩；而分成两把锁
    /// （一把护服务、一把护调用）并不能换来任何并行度 —— 整个程序同一时刻只有一次翻译在跑。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>空闲释放的闹钟。每次翻译完重新上弦。</summary>
    private readonly Timer _idle;

    private bool _disposed;

    /// <param name="bergamotRoot">models/bergamot 目录。</param>
    /// <exception cref="InvalidOperationException">目录下一个装齐的方向都没有。</exception>
    public BergamotTranslator(string bergamotRoot)
    {
        _root = bergamotRoot;
        _installed = [.. BergamotModelDir.EnumerateInstalled(bergamotRoot)
            .Select(p => $"{p.From}-{p.To}")];

        if (_installed.Count == 0)
            throw new InvalidOperationException(
                $"{bergamotRoot} 下没有装好的离线翻译模型。请先在设置里下载。");

        _idle = new Timer(_ => ReleaseService(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>把已经建好的服务放掉。空闲到点了调，也可以在别处主动调来腾内存。</summary>
    public void ReleaseService()
    {
        lock (_gate)
        {
            _service?.Dispose();
            _service = null;
            _serviceKey = null;
        }
    }

    /// <inheritdoc />
    public Task<string> TranslateAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(text);

        string to = Normalize(targetLang);
        string from = Normalize(sourceLang);
        if (from == "auto") from = Detect(text);

        // 猜出来跟目标是同一种语言：原样退回去，别绕一圈把话说坏
        if (from == to) return Task.FromResult(text);

        string[]? route = ResolveRoute(from, to)
            ?? throw new NotSupportedException(
                $"没有 {DescribeRoute(from, to)} 的离线翻译模型。请在设置里下载对应语言。");

        return Task.Run(() => Translate(route, text, ct), ct);
    }

    /// <summary>装好的方向，形如 <c>("zh", "en")</c>。</summary>
    public IEnumerable<(string From, string To)> InstalledDirections
        => _installed.Select(k => (k[..k.IndexOf('-')], k[(k.IndexOf('-') + 1)..]));

    // ---------------- ILanguageCatalog ----------------

    /// <inheritdoc />
    public string DetectLanguage(string text) => Detect(text);

    /// <inheritdoc />
    public IReadOnlyList<string> TargetsFrom(string source)
    {
        string from = Normalize(source);

        // 目标候选就是「装了的语言」这个集合本身：X↔en 的形态决定了任何装了的语种
        // 既能当源也能当目标，除非它是 be / nn 那种只发了「→英语」的
        var codes = _installed
            .SelectMany(k => new[] { k[..k.IndexOf('-')], k[(k.IndexOf('-') + 1)..] })
            .Distinct(StringComparer.Ordinal);

        return [.. codes.Where(to => to != from && ResolveRoute(from, to) is not null)
            .OrderBy(BergamotCatalog.DisplayName, StringComparer.CurrentCulture)];
    }

    /// <inheritdoc />
    public string DisplayName(string code) => BergamotCatalog.DisplayName(Normalize(code));

    // ---------------- 选路 ----------------

    /// <summary>
    /// 找一条从 <paramref name="from"/> 到 <paramref name="to"/> 的路，返回要串起来的配置文件。
    /// 找不到返回 null。
    /// </summary>
    private string[]? ResolveRoute(string from, string to)
    {
        if (_installed.Contains($"{from}-{to}"))
            return [ConfigOf(from, to)];

        // 两头都不是英语才谈得上中转；有一头是英语却没直连，那就是模型没下，不必再绕
        if (from != Pivot && to != Pivot
            && _installed.Contains($"{from}-{Pivot}")
            && _installed.Contains($"{Pivot}-{to}"))
            return [ConfigOf(from, Pivot), ConfigOf(Pivot, to)];

        return null;
    }

    private string ConfigOf(string from, string to)
        => BergamotModelDir.EnsureConfig(BergamotModelDir.PairDir(_root, from, to));

    private string DescribeRoute(string from, string to)
        => from == Pivot || to == Pivot ? $"{from}→{to}" : $"{from}→{to}（需要 {from}→en 和 en→{to} 两段）";

    /// <summary>
    /// 把 BCP-47 标签压成模型库用的代码。
    ///
    /// 中文两种字形在模型库里是两个独立方向，不能像别的语言那样把连字符后面一刀切掉。
    /// </summary>
    private static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "auto";

        string c = code.Trim().ToLowerInvariant().Replace('_', '-');
        if (c == "auto") return "auto";

        if (c is "zh" or "zh-cn" or "zh-hans" or "zh-sg" or "zh-hans-cn") return "zh";
        if (c is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zh-hant-tw") return "zh_hant";

        int dash = c.IndexOf('-');
        return dash > 0 ? c[..dash] : c;
    }

    // ---------------- 猜源语言 ----------------

    /// <summary>
    /// 判源语种。字形怎么判见 <see cref="ScriptLanguage"/>，这里只管接上「装了什么」。
    ///
    /// **判出来的是这段文字实际是什么语言，跟装没装模型无关。**
    /// 早先这里会拿「装了没有」去筛候选，结果是：截了一段韩文、韩语模型没装，
    /// 它一路退到「随便挑一个装了的」，挑中中文，界面还照直报「检测到中文」——
    /// 猜不出来时不吭声，装作猜出来了。判语种和能不能翻是两件事，
    /// 混在一起就没法对用户说清「认得出，但是没装」。
    /// 所以装了的只用来在**同一种文字**的几个候选之间消歧（西里尔那一族，以及
    /// 全用通用字、简繁看不出分别的中文），而且只在字形本身给不出证据时才轮到它。
    /// </summary>
    private string Detect(string text)
    {
        if (ScriptLanguage.Detect(text, IsInstalledSource) is { } detected) return detected;

        // 一个字形都没认出来 —— 纯数字、纯符号。这种输入翻不翻都一样，
        // 退回任意一个装了的源语种，别在这儿抛错打断用户
        return _installed
            .Select(k => k[..k.IndexOf('-')])
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? Pivot;
    }

    private bool IsInstalledSource(string code)
        => _installed.Any(k => k.StartsWith($"{code}-", StringComparison.Ordinal));

    // ---------------- 翻译 ----------------

    private string Translate(string[] route, string text, CancellationToken ct)
    {
        string[] lines = JoinWrappedLines(text.ReplaceLineEndings("\n").Split('\n'));
        var result = new string[lines.Length];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var service = GetService(route);

                for (int i = 0; i < lines.Length; i += LinesPerBatch)
                {
                    ct.ThrowIfCancellationRequested();

                    int count = Math.Min(LinesPerBatch, lines.Length - i);
                    var chunk = new string[count];
                    Array.Copy(lines, i, chunk, 0, count);
                    Array.Copy(TranslateChunk(service, chunk), 0, result, i, count);
                }
            }
            finally
            {
                // 在结果窗口里连着换几次目标语种时闹钟一次次往后推，停下来才开始倒计时
                if (!_disposed) _idle.Change(IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// 把 OCR 交出来的视觉行接回成句子。
    ///
    /// OCR 是按图上看到的行给的，而那些换行多半是原排版排到边了自己折的，不带语义。
    /// 照着行翻，一句话会被拆成几截各翻各的，每截都缺上下文 —— 实测
    /// 「violate the terms of service of Anthropic and other upstream / providers.」
    /// 折在 upstream 后面，就会译成「违反……及其他上游服务条款 / 供应商」，
    /// 而整句喂进去是「违反……及其他上游服务提供商的服务条款」。差别不在模型好坏，
    /// 在于我们喂了半句话进去。
    ///
    /// 判据只有一条：上一行结尾像不像话说完了。没说完就接上，说完了就断开。
    /// 空行和项目符号开头的行强制断开 —— 那两个是真的排版意图，不是折行。
    ///
    /// 代价是输出的行数不再等于输入的行数。对照阅读要的是通顺，这个换得值；
    /// 将来做「译文画回原包围盒」时得另走一条按行对齐的路，那本来也需要每行的坐标。
    /// </summary>
    private static string[] JoinWrappedLines(string[] lines)
    {
        var joined = new List<string>();
        var current = new StringBuilder();

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (line.Length == 0)
            {
                Flush();
                joined.Add(string.Empty);
                continue;
            }

            if (current.Length > 0 && !StartsNewBlock(line) && !LooksFinished(current[^1]))
            {
                // 汉字之间不能塞空格，拉丁词之间必须有
                if (!IsCjk(current[^1]) || !IsCjk(line[0])) current.Append(' ');
                current.Append(line);
            }
            else
            {
                Flush();
                current.Append(line);
            }
        }

        Flush();
        return [.. joined];

        void Flush()
        {
            if (current.Length == 0) return;
            joined.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>这行是不是明显另起一段：项目符号、编号列表。</summary>
    private static bool StartsNewBlock(string line)
    {
        if ("•·▪◦‣∙*-–—+>#⊕□○●■◆✓✗".Contains(line[0])) return true;

        // 「1. 」「2) 」这类编号
        int i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        return i > 0 && i < line.Length && line[i] is '.' or ')' or '、';
    }

    /// <summary>上一行以句末标点收尾，就当它说完了。</summary>
    private static bool LooksFinished(char last)
        => ".!?:;…。！？：；、".Contains(last);

    private static bool IsCjk(char ch)
        => ch is >= '一' and <= '鿿' or >= '぀' and <= 'ヿ' or >= '가' and <= '힯'
            or >= '　' and <= '〿';

    /// <summary>调用方必须已经持有 <see cref="_gate"/>。</summary>
    private BlockingService GetService(string[] route)
    {
        string key = string.Join("|", route);
        if (_serviceKey == key && _service is not null) return _service;

        // 换路线就先把上一条放掉再建新的。顺序不能反 —— 两条同时在内存里是一个多 G 的瞬间
        ReleaseService();

        var service = new BlockingService(route);
        _service = service;
        _serviceKey = key;
        return service;
    }

    /// <summary>
    /// 一批行进去、一批行出来，行数不变。
    ///
    /// 批量接口内部把每行包成 <c>&lt;p&gt;</c> 走 HTML 通道，再按标签拆回来，所以行与行
    /// 不会串味。理论上出来的条数应该和进去的一样，但那是靠正则从译文里数标签数出来的，
    /// 不能当契约：对不上就退回逐行翻 —— 宁可慢，也不能把整段译文错位一行。
    /// </summary>
    private static string[] TranslateChunk(BlockingService service, string[] lines)
    {
        if (Array.TrueForAll(lines, string.IsNullOrWhiteSpace)) return lines;

        var translated = service.Translate(lines);
        if (translated.Length == lines.Length) return translated;

        var one = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            one[i] = string.IsNullOrWhiteSpace(lines[i]) ? lines[i] : service.Translate(lines[i]);
        return one;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // 不等回调收尾：它要拿的正是刚放开的这把锁，而它干的事（放掉服务）跟下面这行
        // 一模一样，且能重复做。晚到一步的那次回调最多是对着已经空了的字段再走一遍
        _idle.Dispose();
        ReleaseService();
    }
}
