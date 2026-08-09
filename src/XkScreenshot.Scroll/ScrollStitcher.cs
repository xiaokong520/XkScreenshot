using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XkScreenshot.Scroll;

/// <summary>一帧送进拼接器之后发生了什么。</summary>
public enum StitchStatus
{
    /// <summary>第一帧，原样收下作为底子。</summary>
    First,

    /// <summary>接上了，长出了新内容。</summary>
    Advanced,

    /// <summary>和上一帧一模一样，什么都没长。</summary>
    NoChange,

    /// <summary>画面确实变了，但没能对上 —— 跳转、弹窗、或者一下滚过了头没留下重叠。</summary>
    Unmatched,

    /// <summary>撞到高度上限，后面的内容装不下了。</summary>
    Full,
}

public readonly record struct StitchResult(StitchStatus Status, int Rows);

/// <summary>
/// 把一帧帧滚动画面拼成一张长图。
///
/// ## 怎么求位移
///
/// 内容往上走了 d 行，就意味着「上一帧的第 r 行」和「这一帧的第 r-d 行」是同一行内容。
/// 于是从这一帧视口顶部取一条 band，在上一帧里逐个 d 试过去，找那个对得最齐的。
/// 先用行签名（每行降成 <see cref="SigColumns"/> 个灰度采样）粗筛，选出几个候选，
/// 再用原分辨率像素逐个精修验证 —— 粗筛负责快，精修负责别接错。
///
/// 只粗筛不精修的话，纯色区域、重复列表这类地方随便一个 d 都「差不多」，接出来就是错位；
/// 只精修不粗筛的话，一帧要在上千个 d 上做全分辨率比较，一秒十几帧根本跑不动。
///
/// ## 吸顶栏 / 吸底栏
///
/// 顶部固定的标题栏、底部固定的操作条不跟着滚，它们在两帧里位置和像素都不变。
/// 不把它们摘出去有两个后果：band 落在吸顶栏上时 d=0 才「最像」，于是判定成没滚动；
/// 而吸底栏会被当成正文，每翻一屏就在长图里重复印一遍。
///
/// 判定办法就是逐行比：从上往下数连续相同的行是吸顶，从下往上数是吸底。
/// 允许中间夹杂个别不同的行（连着 <see cref="StickyBreakRows"/> 行都不同才算真的进了正文）——
/// 顶栏上放个时钟、一个转圈的加载图标是很常见的，只按「完全相同」数会在那儿断掉。
///
/// **多判一点是安全的，少判才危险**：因为每次都是相对「视口下沿」去取新内容，
/// 把一条纯色的正文误当成吸底栏，只不过让它最后作为收尾整体贴上去，位置照样是对的；
/// 而漏判吸顶栏会让整个位移求错。所以这里的取舍一律偏向多判。
///
/// ## 画布怎么长
///
/// 首帧整帧收下（含吸顶栏，它在长图里只该出现这一次）。之后每接上一帧，
/// 只把「上次收到哪儿」到「这一帧视口下沿」之间那一段贴上去。收尾时再把最后一帧
/// 视口下沿以下的部分（吸底栏）贴一次。
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>行签名的列数。</summary>
    private const int SigColumns = 32;

    /// <summary>匹配用的 band 高度（行）。</summary>
    private const int BandRows = 96;
    private const int MinBandRows = 24;

    /// <summary>连着这么多行都不同，才算走出了吸顶/吸底栏。</summary>
    private const int StickyBreakRows = 8;

    /// <summary>吸顶/吸底各自最多占到整帧的这个比例。防止内容稀疏的页面被整帧判成「都不动」。</summary>
    private const double MaxStickyRatio = 0.4;

    /// <summary>单行粗筛得分的上限。个别行被污染（顶栏里的时钟）时不至于把整条 band 的均分拖垮。</summary>
    private const int RowScoreCap = 48;

    /// <summary>粗筛得分高于此值直接判定没戏，省掉一次精修。</summary>
    private const int CoarseReject = 40;

    /// <summary>送去精修的候选个数，以及候选之间至少要隔开多少行。</summary>
    private const int CandidateCount = 3;
    private const int CandidateSpread = 4;

    /// <summary>精修：band 内平均绝对差的上限。</summary>
    private const double FineMeanDiff = 6.0;

    /// <summary>精修：差得离谱的采样点占比上限，以及「差得离谱」的门槛。</summary>
    private const double FineBadRatio = 0.02;
    private const int FineBadDiff = 28;

    private readonly int _w;
    private readonly int _h;
    private readonly int _stride;
    private readonly int _maxRows;
    private readonly int _sigCols;
    private readonly PreviewStrip _preview;

    /// <summary>上一张**被接进画布**的帧。对不上的帧不会顶掉它 —— 见 <see cref="Push"/>。</summary>
    private byte[]? _prev;
    private byte[] _sigPrev;
    private byte[] _sigCur;

    private byte[] _canvas = [];
    private int _rows;

    /// <summary>画布已经收到 <see cref="_prev"/> 的哪一行（不含）。-1 表示还没接过任何一帧。</summary>
    private int _lastBottom = -1;

    private bool _full;

    public ScrollStitcher(int width, int height, int maxHeight)
    {
        _w = width;
        _h = height;
        _stride = width * 4;
        _maxRows = Math.Max(height, maxHeight);
        _sigCols = Math.Min(SigColumns, Math.Max(1, width));
        _sigPrev = new byte[height * _sigCols];
        _sigCur = new byte[height * _sigCols];
        _preview = new PreviewStrip(width, _stride);
    }

    public int Width => _w;

    /// <summary>成功接上的帧数（不含首帧）。</summary>
    public int StitchedFrames { get; private set; }

    /// <summary>此刻收工的话长图有多高。</summary>
    public int TotalRows => _lastBottom < 0
        ? (_prev is null ? 0 : _h)
        : _rows + Math.Max(0, _h - _lastBottom);

    /// <summary>已经装满，再滚也收不下了。</summary>
    public bool IsFull => _full;

    /// <summary>
    /// 这一帧和「上一张接进画布的帧」是不是一样的。
    ///
    /// 引擎靠它判断滚轮到底生没生效：一样就是还没动静，还得再等。
    /// 容差同 <see cref="FrameCompare"/> —— 光标闪一下不算画面动了。
    /// </summary>
    public bool MatchesLast(byte[] frame)
        => _prev is not null
           && FrameCompare.NearlyEqual(_prev, frame, _stride, _h, FrameCompare.PixelBudget(_w, _h));

    /// <summary>
    /// 送一帧进来。
    ///
    /// 对不上（<see cref="StitchStatus.Unmatched"/>）时**不**拿它顶掉参考帧：
    /// 参考帧一换，画布和它之间的对应关系就断了，后面再也接不回去。
    /// 留着旧的，用户往回滚一点、或者弹窗关掉之后还能接着拼。
    /// </summary>
    public StitchResult Push(byte[] frame)
    {
        if (_prev is null)
        {
            _prev = (byte[])frame.Clone();
            ComputeSignature(_prev, _sigPrev);
            return new StitchResult(StitchStatus.First, _h);
        }

        if (_full) return new StitchResult(StitchStatus.Full, 0);

        int top = LeadingStableRows(frame);
        int bottom = _h - TrailingStableRows(frame, top);

        // 变化的那一小块还没一条 band 高：这不是滚动，是光标在闪、按钮在悬停、
        // 顶栏的时钟跳了一秒。当成「没长东西」而不是「对不上」——
        // 后者会一路累加到「连着几帧接不上」，把好端端的会话掐掉。
        if (bottom - top < MinBandRows * 2) return new StitchResult(StitchStatus.NoChange, 0);

        ComputeSignature(frame, _sigCur);

        int d = FindOffset(frame, top, bottom);
        if (d <= 0) return new StitchResult(StitchStatus.Unmatched, 0);

        // 画布收到 _prev 的 _lastBottom 行，换算到这一帧的坐标系就是 _lastBottom - d。
        // 首次接上时画布还是空的，先把整个首帧（含吸顶栏）铺进去。
        if (_lastBottom < 0)
        {
            Append(_prev, 0, bottom);
            _lastBottom = bottom;
        }

        int from = Math.Clamp(_lastBottom - d, 0, _h);
        int rows = Math.Max(0, bottom - from);
        if (rows > 0) Append(frame, from, rows);

        // rows 为 0 时画布的下沿仍然停在 from —— 取两者较大的那个，两种情况一并成立
        _lastBottom = Math.Max(from, bottom);

        Adopt(frame);
        if (rows == 0) return new StitchResult(StitchStatus.NoChange, 0);

        StitchedFrames++;
        return new StitchResult(_full ? StitchStatus.Full : StitchStatus.Advanced, rows);
    }

    /// <summary>
    /// 连不上了，认栽换锚：把这一帧立为新参考，之前没贴完的旧参考先封口。
    ///
    /// 长图里会留一道不连续的缝 —— 那几行内容丢了 —— 但总比整个会话报废、
    /// 连已经拼好的几十帧一起作废强。
    /// </summary>
    public void ResetAnchor(byte[] frame)
    {
        if (_prev is not null && _lastBottom >= 0 && _lastBottom < _h)
        {
            Append(_prev, _lastBottom, _h - _lastBottom);
        }

        _prev = (byte[])frame.Clone();
        ComputeSignature(frame, _sigPrev);
        _lastBottom = -1;
    }

    /// <summary>把这一帧收作新的参考帧。签名两块缓冲轮着用，免得每帧重新分配。</summary>
    private void Adopt(byte[] frame)
    {
        Buffer.BlockCopy(frame, 0, _prev!, 0, _stride * _h);
        (_sigPrev, _sigCur) = (_sigCur, _sigPrev);
    }

    // ---------------- 吸顶 / 吸底 ----------------

    /// <summary>
    /// 顶上有多少行没动过。连着 <see cref="StickyBreakRows"/> 行都变了才算进了正文，
    /// 于是顶栏里嵌一个时钟、一个转圈图标都不会让判定提前断掉。
    /// </summary>
    private int LeadingStableRows(byte[] frame)
    {
        int limit = (int)(_h * MaxStickyRatio);
        int stable = 0, run = 0;

        for (int y = 0; y < limit; y++)
        {
            if (RowsEqual(_prev!, frame, y))
            {
                run = 0;
                stable = y + 1;
            }
            else if (++run >= StickyBreakRows)
            {
                break;
            }
        }
        return stable;
    }

    /// <summary>同上，从下往上数。<paramref name="top"/> 是已经判给吸顶栏的部分，不再重复算。</summary>
    private int TrailingStableRows(byte[] frame, int top)
    {
        int limit = Math.Min((int)(_h * MaxStickyRatio), _h - top);
        int stable = 0, run = 0;

        for (int i = 0; i < limit; i++)
        {
            int y = _h - 1 - i;
            if (RowsEqual(_prev!, frame, y))
            {
                run = 0;
                stable = i + 1;
            }
            else if (++run >= StickyBreakRows)
            {
                break;
            }
        }
        return stable;
    }

    private bool RowsEqual(byte[] a, byte[] b, int y)
    {
        int at = y * _stride;
        return a.AsSpan(at, _stride).SequenceEqual(b.AsSpan(at, _stride));
    }

    // ---------------- 求位移 ----------------

    /// <summary>
    /// 求这一帧相对上一帧往上走了多少行。返回 0 表示没找到可信的位移。
    ///
    /// 粗筛给出若干候选，逐个拿原分辨率像素验证，第一个过关的就是答案。
    /// 只取粗筛冠军是不够的：纯色和重复纹理会让某个错误的 d 分数最低，
    /// 而正确的那个排第二 —— 多留两个名额基本上就把这种情况兜住了。
    /// </summary>
    private int FindOffset(byte[] frame, int top, int bottom)
    {
        int span = bottom - top;
        int band = Math.Clamp(BandRows, MinBandRows, span / 2);
        if (band < MinBandRows) return 0;

        int bandTop = ChooseBandTop(top, bottom, band);
        int maxD = bottom - bandTop - band;
        if (maxD < 1) return 0;

        Span<int> bestD = stackalloc int[CandidateCount];
        Span<int> bestScore = stackalloc int[CandidateCount];
        int found = 0;

        for (int d = 1; d <= maxD; d++)
        {
            int score = CoarseScore(bandTop, band, d, found == CandidateCount ? bestScore[found - 1] : int.MaxValue);
            if (score > CoarseReject) continue;
            found = Offer(bestD, bestScore, found, d, score);
        }

        for (int i = 0; i < found; i++)
            if (Verify(frame, bandTop, band, bestD[i])) return bestD[i];

        return 0;
    }

    /// <summary>
    /// band 从视口的哪一行开始取。
    ///
    /// 默认就是视口顶 —— 那里离下沿最远，可搜索的位移范围最大。但顶上正好是一片空白时
    /// （很多页面正文上方留着大片留白），空白 band 对哪个 d 都「很像」，匹配就成了掷骰子。
    /// 所以在前三分之一里挑纹理最重的那一段。
    /// </summary>
    private int ChooseBandTop(int top, int bottom, int band)
    {
        int limit = Math.Min(top + (bottom - top) / 3, bottom - band);
        int bestTop = top;
        long bestEnergy = -1;

        for (int start = top; start <= limit; start += band / 2)
        {
            long energy = BandEnergy(start, band);
            if (energy <= bestEnergy) continue;
            bestEnergy = energy;
            bestTop = start;
        }
        return bestTop;
    }

    /// <summary>band 的纹理量：相邻行之间、以及行内相邻采样之间的落差总和。</summary>
    private long BandEnergy(int start, int band)
    {
        long energy = 0;
        for (int i = 0; i < band; i++)
        {
            int row = (start + i) * _sigCols;
            for (int k = 1; k < _sigCols; k++)
                energy += Math.Abs(_sigCur[row + k] - _sigCur[row + k - 1]);
        }
        return energy;
    }

    /// <summary>
    /// 粗筛得分：band 里每一行的平均绝对差，再对所有行取平均。越小越像。
    ///
    /// 单行得分先按 <see cref="RowScoreCap"/> 封顶，个别被污染的行才不至于一票否决；
    /// 累计超过当前最差候选就提前收手，这一条让绝大多数 d 只算几行就被淘汰。
    /// </summary>
    private int CoarseScore(int bandTop, int band, int d, int abandonAbove)
    {
        long total = 0;
        long budget = abandonAbove == int.MaxValue ? long.MaxValue : (long)abandonAbove * band;

        for (int i = 0; i < band; i++)
        {
            int a = (bandTop + i) * _sigCols;
            int b = (bandTop + d + i) * _sigCols;

            int rowDiff = 0;
            for (int k = 0; k < _sigCols; k++)
                rowDiff += Math.Abs(_sigCur[a + k] - _sigPrev[b + k]);

            total += Math.Min(rowDiff / _sigCols, RowScoreCap);
            if (total > budget) return int.MaxValue;
        }
        return (int)(total / band);
    }

    /// <summary>把一个候选插进按分数排好的名次表，太靠近已有候选时只保留更好的那个。</summary>
    private static int Offer(Span<int> ds, Span<int> scores, int count, int d, int score)
    {
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(ds[i] - d) >= CandidateSpread) continue;
            if (score >= scores[i]) return count;

            // 挤掉那个近邻，重新排一次
            for (int j = i; j < count - 1; j++)
            {
                ds[j] = ds[j + 1];
                scores[j] = scores[j + 1];
            }
            count--;
            break;
        }

        int at = count;
        while (at > 0 && scores[at - 1] > score) at--;
        if (at >= ds.Length) return count;

        for (int j = Math.Min(count, ds.Length - 1); j > at; j--)
        {
            ds[j] = ds[j - 1];
            scores[j] = scores[j - 1];
        }
        ds[at] = d;
        scores[at] = score;
        return Math.Min(count + 1, ds.Length);
    }

    /// <summary>
    /// 原分辨率验证。列上隔一个取一个 —— 精度绰绰有余，而代价只有一半。
    ///
    /// 两条判据缺一不可：平均差管的是「整体像不像」，离谱点占比管的是
    /// 「是不是有一小块完全对不上」。只看平均分的话，一大片纯白里错位半个控件照样能过关。
    /// </summary>
    private bool Verify(byte[] frame, int bandTop, int band, int d)
    {
        long sum = 0;
        int bad = 0;
        int samples = 0;

        for (int i = 0; i < band; i++)
        {
            int a = (bandTop + i) * _stride;
            int b = (bandTop + d + i) * _stride;

            for (int x = 0; x < _w; x += 2)
            {
                int ai = a + x * 4;
                int bi = b + x * 4;

                int diff = Math.Abs(frame[ai] - _prev![bi])
                           + Math.Abs(frame[ai + 1] - _prev[bi + 1])
                           + Math.Abs(frame[ai + 2] - _prev[bi + 2]);
                diff /= 3;

                sum += diff;
                if (diff > FineBadDiff) bad++;
                samples++;
            }
        }

        if (samples == 0) return false;
        return sum / (double)samples <= FineMeanDiff && bad / (double)samples <= FineBadRatio;
    }

    // ---------------- 行签名 ----------------

    /// <summary>
    /// 每行降成 <see cref="_sigCols"/> 个灰度采样。每个列块里再抽样几个点就够 ——
    /// 粗筛只负责挑候选，真正的把关在 <see cref="Verify"/>，这里多算一倍精度换不来任何东西。
    /// </summary>
    private void ComputeSignature(byte[] frame, byte[] sig)
    {
        for (int y = 0; y < _h; y++)
        {
            int row = y * _stride;
            int at = y * _sigCols;

            for (int k = 0; k < _sigCols; k++)
            {
                int x0 = k * _w / _sigCols;
                int x1 = Math.Max(x0 + 1, (k + 1) * _w / _sigCols);
                int step = Math.Max(1, (x1 - x0) / 8);

                int sum = 0, n = 0;
                for (int x = x0; x < x1; x += step)
                {
                    int i = row + x * 4;
                    // 近似灰度：(B + 2G + R) / 4，比正经的加权系数快，用途上完全够
                    sum += (frame[i] + frame[i + 1] * 2 + frame[i + 2]) >> 2;
                    n++;
                }
                sig[at + k] = (byte)(sum / n);
            }
        }
    }

    // ---------------- 画布 ----------------

    private void Append(byte[] src, int fromRow, int count)
    {
        if (count <= 0 || _full) return;

        if (_rows + count >= _maxRows)
        {
            count = _maxRows - _rows;
            _full = true;
            if (count <= 0) return;
        }

        EnsureCapacity(_rows + count);
        Buffer.BlockCopy(src, fromRow * _stride, _canvas, _rows * _stride, count * _stride);
        _preview.Append(src, fromRow, count);
        _rows += count;
    }

    private void EnsureCapacity(int rows)
    {
        int have = _canvas.Length / _stride;
        if (have >= rows) return;

        // 一次多要一些：长截图动辄几十帧，按需精确扩容会把整张画布反复搬运
        int next = Math.Min(_maxRows, Math.Max(rows, Math.Max(have * 2, _h * 4)));
        Array.Resize(ref _canvas, next * _stride);
    }

    /// <summary>当前长图的缩略图，以及它对应的真实高度（缩略图纵向是抽行的，比例对不上）。</summary>
    public BitmapSource? BuildPreview() => _preview.Build();

    public int PreviewSourceRows => _preview.SourceRows;

    /// <summary>
    /// 收工出图。会把最后一帧的吸底栏贴上去，所以只该在结束时调一次
    /// （再调一次也不会重复贴，<see cref="_lastBottom"/> 已经推到底了）。
    /// </summary>
    public BitmapSource? Build()
    {
        if (_prev is null) return null;

        // 一帧都没接上：那就是一张普通截图，原样给出去
        if (_lastBottom < 0) return CreateBitmap(_prev, _h);

        int footer = _h - _lastBottom;
        if (footer > 0)
        {
            Append(_prev, _lastBottom, footer);
            _lastBottom = _h;
        }

        return _rows > 0 ? CreateBitmap(_canvas, _rows) : null;
    }

    private BitmapSource CreateBitmap(byte[] buffer, int rows)
    {
        var bitmap = BitmapSource.Create(_w, rows, 96, 96, PixelFormats.Bgra32, null, buffer, _stride);
        bitmap.Freeze();
        return bitmap;
    }
}
