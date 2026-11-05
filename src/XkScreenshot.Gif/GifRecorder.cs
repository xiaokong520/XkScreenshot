using System.Diagnostics;
using XkScreenshot.Capture;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Gif;

/// <summary>
/// 一次录制：后台线程按固定节拍抓取选区，只把「变了的那块」存下来。
///
/// 抓帧和编码分成两步走，是因为两件事的时机不同：抓帧必须准点（迟到的帧会让动作看起来
/// 一卡一卡），编码则要等全部画面都到齐（调色板要按整段内容来定）。录制过程中不能编码，
/// 否则前面用掉的颜色会被后面的画面否定。
///
/// 线程模型：抓帧线程是唯一往 <see cref="_frames"/> 里写的人，<see cref="Stop"/> 在
/// 停表之后才拿锁收尾。界面线程只读两个用 Interlocked/Stopwatch 维护的计数。
/// </summary>
public sealed class GifRecorder : IDisposable
{
    /// <summary>
    /// 画面缓冲的上限。超了就自动停下并如实告诉用户 —— 一段整屏都在变的录像
    /// （典型是录视频）按 10fps 存下去，一分钟就是几个 GB，先停下比让进程被系统杀掉强。
    /// </summary>
    public const long BufferLimitBytes = 384L * 1024 * 1024;

    /// <summary>
    /// 变化像素占这块矩形的比例超过它，就不再做逐像素掩码。
    ///
    /// 掩码是给「一小块里只有几个像素变了」省空间的；一块 800×600 的区域里变了 90%，
    /// 掩码本身要 60 KB，而它能省下的透明像素寥寥无几 —— 不如整块实写，LZW 还更好压。
    /// </summary>
    private const double FullBlockRatio = 0.65;

    /// <summary>
    /// 判定「这个像素没变」的容差，单位是 8 位色阶。
    ///
    /// 屏幕抓帧不是逐位稳定的：DWM 的阴影、渐变抖动、硬件叠加层每次重绘都可能差一两级。
    /// 逐位相等的话，这些一两级的噪声会让整帧都被判成「变了」，差分等于没做。
    ///
    /// 关键是**拿上一次真正写进 GIF 的颜色做参照**（<c>reference</c>），而不是拿上一张原始
    /// 抓帧。否则每一帧都只差一两级的慢渐变会被永远冻结在起始颜色上；有了参照，累计差
    /// 超过容差就会更新，既不会抖也不会漂。
    /// </summary>
    private const int DiffTolerance = 3;

    private readonly PixelRect _region;
    private readonly GifOptions _options;
    private readonly int _width;
    private readonly int _height;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();

    private List<GifFrame> _frames = [];
    private GifRecording? _recording;
    private long _bufferBytes;
    private long _lastChangeMs;
    private long _stopRequestedMs;
    private int _capturedFrames;
    private int _startDelayMs;
    private volatile bool _stop;
    private volatile bool _selfStopped;
    private bool _frozen;
    private bool _disposed;
    private GifStopReason _reason = GifStopReason.UserStopped;

    public GifRecorder(PixelRect region, GifOptions options)
    {
        if (region.IsEmpty) throw new ArgumentException("录制区域为空", nameof(region));

        _region = region;
        _options = options.Sanitized();

        // 画质档位决定成品尺寸。缩放放在抓帧这一侧：录一段 1080p 视频，存 40 帧原始像素
        // 就是两百多兆，而缩到 1024 宽之后连缓冲带编码全都跟着小下去。
        int maxDimension = _options.MaxDimension;
        int longest = Math.Max(region.Width, region.Height);
        if (maxDimension > 0 && longest > maxDimension)
        {
            double scale = (double)maxDimension / longest;
            _width = Math.Max(1, (int)Math.Round(region.Width * scale));
            _height = Math.Max(1, (int)Math.Round(region.Height * scale));
        }
        else
        {
            _width = region.Width;
            _height = region.Height;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "GifRecorder",
        };
    }

    /// <summary>已经开始计时多久。控制条上的时长用它，不必自己再养一只表。</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>抓到的总帧数（含没变化、没存下来的）。</summary>
    public int CapturedFrames => Volatile.Read(ref _capturedFrames);

    /// <summary>
    /// 抓帧线程自己收工了（撞到时长/内存上限，或者抓不到画面）。
    ///
    /// 界面那层靠它发现「没人按停止，但已经录完了」—— 否则控制条会停在最后一帧、
    /// 计时不再走，看起来像卡死。用户按停止不会置这一位：那一路本来就会主动调 Stop。
    /// </summary>
    public bool HasStopped => _selfStopped;

    /// <summary>
    /// 起手。delayMs 是「先等一会儿再抓第一帧」。
    ///
    /// 不是「让用户准备好」—— 是为了别把刚关掉的覆盖层拍进第一帧。覆盖层是几扇全屏窗口，
    /// WPF 的 Close 只是销毁窗口，DWM 真正把桌面合成出来还要一两帧；这期间 BitBlt
    /// 拿到的仍是旧画面。120ms 在体感上是「按完就开录」，对合成器则宽裕得多。
    ///
    /// 这段等待也走 <see cref="_wake"/>，所以用户在 120ms 内点取消能立刻醒过来退出。
    /// </summary>
    public void Start(int delayMs = 0)
    {
        if (_thread.IsAlive) return;
        _startDelayMs = Math.Max(0, delayMs);
        _thread.Start();
    }

    private void Run()
    {
        if (_startDelayMs > 0) _wake.WaitOne(_startDelayMs);
        if (_stop) return;

        RegionGrabber grabber;
        try
        {
            grabber = new RegionGrabber(_region);
        }
        catch (Exception)
        {
            _reason = GifStopReason.Failed;
            _selfStopped = true;
            return;
        }

        int interval = _options.FrameIntervalMs;
        long maxMs = _options.MaxSeconds * 1000L;

        using (grabber)
        {
            bool scaled = _width != _region.Width || _height != _region.Height;

            // 三块缓冲各司其职：capture 是屏幕原图（仅缩放时需要），raw 是这一帧的成品像素，
            // reference 是「上一次真正写进 GIF 的画面」。
            //
            // 千万不能让抓帧缓冲和 reference 共用同一块内存：BitBlt 会先把它覆盖掉，
            // 于是每一帧都在跟「已经变成自己」的上一帧比 —— 结果是每帧全变、全部整帧写入。
            var capture = scaled ? new byte[grabber.ByteCount] : null;
            var raw = new byte[_width * _height * 4];
            var reference = new byte[raw.Length];
            bool hasReference = false;

            // 默认 15.6ms 的系统定时器粒度会把 20~30fps 的等待拉到下一格，帧间隔忽长忽短，
            // 播出来就是一顿一顿。录制期间抬到 1ms，收工必须还回去（finally）。
            _clock.Start();
            NativeMethods.TimeBeginPeriod(1);

            try
            {
                while (!_stop)
                {
                long frameStart = _clock.ElapsedMilliseconds;

                bool grabbed = scaled
                    ? grabber.Grab(capture!, _options.CaptureCursor)
                    : grabber.Grab(raw, _options.CaptureCursor);

                if (!grabbed)
                {
                    _reason = GifStopReason.Failed;
                    break;
                }

                if (scaled)
                    FrameDownscaler.Resize(capture!, _region.Width, _region.Height, raw, _width, _height);

                long stamp = _clock.ElapsedMilliseconds;
                bool bufferFull = false;

                lock (_sync)
                {
                    if (_frozen) break;

                    try
                    {
                        if (!hasReference)
                        {
                            AddFirstFrame(raw, stamp);
                            Buffer.BlockCopy(raw, 0, reference, 0, raw.Length);
                            hasReference = true;
                        }
                        else
                        {
                            AddFrame(raw, reference, stamp);
                        }

                        bufferFull = _bufferBytes >= BufferLimitBytes;
                    }
                    catch (OutOfMemoryException)
                    {
                        _reason = GifStopReason.MemoryLimit;
                        break;
                    }
                }

                Interlocked.Increment(ref _capturedFrames);

                if (stamp >= maxMs)
                {
                    _reason = GifStopReason.DurationLimit;
                    break;
                }

                if (bufferFull)
                {
                    _reason = GifStopReason.MemoryLimit;
                    break;
                }

                // 按绝对时间排下一拍：抓帧本身要几毫秒，不扣掉的话实际帧率会一路往下漂
                int wait = (int)(frameStart + interval - _clock.ElapsedMilliseconds);
                if (wait > 0) _wake.WaitOne(wait);
                }
            }
            finally
            {
                NativeMethods.TimeEndPeriod(1);
                _clock.Stop();
            }
        }

        // 不是被 Stop 叫停的，就是自己撞上了上限 / 抓不到画面
        if (!_stop) _selfStopped = true;
    }

    private void AddFirstFrame(byte[] frame, long stamp)
    {
        var pixels = new byte[frame.Length];
        Buffer.BlockCopy(frame, 0, pixels, 0, frame.Length);

        _frames.Add(new GifFrame(0, 0, _width, _height, pixels, null));
        _bufferBytes += pixels.Length;
        _lastChangeMs = stamp;
    }

    /// <summary>
    /// 把这一帧并进录制。完全没变化时不存：GIF 的 delay 是「这一帧显示多久」，
    /// 静止的时间并到上一帧的 delay 里就行，存一堆一模一样的帧只会让文件白白变大。
    ///
    /// 注意这里会顺手更新 <paramref name="reference"/>：只有真正写进 GIF 的像素才更新，
    /// 容差内被判成「没变」的像素保持旧值 —— 解码器那边也是这么做的（透明 = 沿用上一帧）。
    /// </summary>
    private void AddFrame(byte[] current, byte[] reference, long stamp)
    {
        int width = _width;
        int height = _height;

        if (!FindDiff(current, reference, width, height,
                out int left, out int top, out int right, out int bottom, out int changed))
            return;

        int blockWidth = right - left + 1;
        int blockHeight = bottom - top + 1;
        long area = (long)blockWidth * blockHeight;

        bool full = changed >= area * FullBlockRatio;
        var block = new byte[blockWidth * blockHeight * 4];
        var mask = full ? null : new byte[(blockWidth * blockHeight + 7) / 8];

        int p = 0;
        for (int y = top; y <= bottom; y++)
        {
            int rowStart = (y * width + left) * 4;
            Buffer.BlockCopy(current, rowStart, block, p * 4, blockWidth * 4);

            if (mask is not null)
            {
                for (int x = 0; x < blockWidth; x++)
                {
                    int o = (y * width + left + x) * 4;
                    if (!Same(current, reference, o))
                    {
                        mask[p >> 3] |= (byte)(1 << (p & 7));
                        reference[o] = current[o];
                        reference[o + 1] = current[o + 1];
                        reference[o + 2] = current[o + 2];
                    }

                    p++;
                }
            }
            else
            {
                // 整块实写：这块矩形里的每个像素都以新的为准
                Buffer.BlockCopy(current, rowStart, reference, rowStart, blockWidth * 4);
                p += blockWidth;
            }
        }

        // 上一帧一直显示到这一帧出现为止
        _frames[^1].DelayMs = (int)Math.Min(int.MaxValue, stamp - _lastChangeMs);
        _frames.Add(new GifFrame(left, top, blockWidth, blockHeight, block, mask));
        _bufferBytes += block.Length + (mask?.Length ?? 0);
        _lastChangeMs = stamp;
    }

    /// <summary>
    /// 求出两帧的差异矩形与变化像素数。逐像素比、带容差（见 <see cref="DiffTolerance"/>）。
    /// </summary>
    private static bool FindDiff(
        byte[] current, byte[] reference, int width, int height,
        out int left, out int top, out int right, out int bottom, out int changed)
    {
        left = width;
        top = -1;
        right = -1;
        bottom = -1;
        changed = 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int first = -1;
            int last = -1;

            for (int x = 0; x < width; x++)
            {
                int o = (row + x) * 4;
                if (Same(current, reference, o)) continue;

                changed++;
                if (first < 0) first = x;
                last = x;
            }

            if (first < 0) continue;

            if (top < 0) top = y;
            bottom = y;
            if (first < left) left = first;
            if (last > right) right = last;
        }

        return top >= 0;
    }

    private static bool Same(byte[] a, byte[] b, int offset)
    {
        int d = a[offset] - b[offset];
        if (d > DiffTolerance || d < -DiffTolerance) return false;

        d = a[offset + 1] - b[offset + 1];
        if (d > DiffTolerance || d < -DiffTolerance) return false;

        d = a[offset + 2] - b[offset + 2];
        return d <= DiffTolerance && d >= -DiffTolerance;
    }

    /// <summary>
    /// 停止并交出这一段录制。重复调用返回同一份结果。
    ///
    /// 抓帧线程最多再占一个抓帧周期（默认 100ms），等它是值得的：不然最后那一帧
    /// 可能一边被读、一边被下一次 BitBlt 覆盖。
    /// </summary>
    public GifRecording Stop()
    {
        lock (_sync)
        {
            if (_recording is not null) return _recording;
        }

        _stopRequestedMs = _clock.ElapsedMilliseconds;
        _stop = true;
        _wake.Set();

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));

        lock (_sync)
        {
            if (_recording is not null) return _recording;

            _frozen = true;

            long end = Math.Max(_stopRequestedMs, _lastChangeMs);
            if (_frames.Count > 0)
                _frames[^1].DelayMs = (int)Math.Max(0, end - _lastChangeMs);

            _recording = new GifRecording(
                _region, _width, _height, _frames, TimeSpan.FromMilliseconds(end),
                _reason, _capturedFrames, _bufferBytes, _options.PaletteColors, _options.Dither);

            // 收下之后不再有人往里写；换一份空表，Dispose 里的二次 Stop 也就没有副作用
            _frames = [];
            return _recording;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_recording is null) Stop();
        _wake.Dispose();
    }
}
