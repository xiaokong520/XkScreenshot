using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Privacy;

/// <summary>
/// YuNet 人脸检测（OpenCV Zoo，MIT 许可）。
///
/// 模型只有两百多 KB，作为嵌入资源打在程序集里，装上就能用，不必像 OCR 那样先去下载。
/// 解码照着 OpenCV <c>FaceDetectorYN</c> 的实现来：三个步长各出一组
/// cls / obj / bbox，分数取 √(cls·obj)，框是相对格点的偏移加上 exp 过的宽高。
///
/// 会话的生命周期照搬 <see cref="XkScreenshot.Ocr.PaddleOcrEngine"/>：用到才建、
/// 空闲十分钟放掉、关掉内存池 —— 理由在那边写得很清楚，这里不重复。
/// </summary>
internal sealed class FaceDetector : IDisposable
{
    private const string ResourceName = "yunet.onnx";

    /// <summary>
    /// 长边超过它就先缩小再检测。头像里的脸至少有二三十像素，
    /// 缩到 1280 之后依然在模型的 8 像素步长能分辨的范围内，而整屏 4K 图的耗时能省掉三分之二。
    /// </summary>
    private const int MaxSide = 1280;

    private const float ScoreThreshold = 0.6f;
    private const float NmsThreshold = 0.3f;
    private static readonly int[] Strides = [8, 16, 32];

    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Timer _idle;
    private InferenceSession? _session;
    private bool _disposed;

    public FaceDetector()
    {
        _idle = new Timer(_ => Release(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>检测一张图里的人脸，返回原图像素坐标的框。</summary>
    public IReadOnlyList<PixelRect> Detect(PixelBuffer image, CancellationToken ct = default)
    {
        if (image.Width < 16 || image.Height < 16) return [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();

            try
            {
                _session ??= Load();
                ct.ThrowIfCancellationRequested();
                var result = Run(_session, image);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                if (!_disposed) _idle.Change(IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static InferenceSession Load()
    {
        using var stream = typeof(FaceDetector).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new FileNotFoundException("程序集里缺少人脸检测模型：" + ResourceName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        using var options = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
            InterOpNumThreads = 1,
        };
        options.EnableCpuMemArena = false;
        return new InferenceSession(ms.ToArray(), options);
    }

    private static IReadOnlyList<PixelRect> Run(InferenceSession session, PixelBuffer image)
    {
        var (inputName, meta) = session.InputMetadata.First();

        // 模型若是导出成固定尺寸的，就只能按它要的尺寸喂；动态尺寸则按图本身来，补齐到 32 的倍数
        int fixedH = meta.Dimensions.Length == 4 ? meta.Dimensions[2] : -1;
        int fixedW = meta.Dimensions.Length == 4 ? meta.Dimensions[3] : -1;

        float scale = Math.Min(1f, (float)MaxSide / Math.Max(image.Width, image.Height));
        if (fixedW > 0 && fixedH > 0)
            scale = Math.Min((float)fixedW / image.Width, (float)fixedH / image.Height);

        int scaledW = Math.Max(1, (int)(image.Width * scale));
        int scaledH = Math.Max(1, (int)(image.Height * scale));
        int padW = fixedW > 0 ? fixedW : (scaledW + 31) / 32 * 32;
        int padH = fixedH > 0 ? fixedH : (scaledH + 31) / 32 * 32;

        var tensor = ToTensor(image, scaledW, scaledH, padW, padH);

        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var byName = outputs.ToDictionary(o => o.Name, o => o.AsTensor<float>().ToArray());

        var boxes = new List<(float X, float Y, float W, float H, float Score)>();
        foreach (int stride in Strides)
        {
            if (!byName.TryGetValue($"cls_{stride}", out var cls)
                || !byName.TryGetValue($"obj_{stride}", out var obj)
                || !byName.TryGetValue($"bbox_{stride}", out var bbox)) continue;

            int cols = padW / stride;
            int rows = padH / stride;
            int count = Math.Min(rows * cols, Math.Min(cls.Length, Math.Min(obj.Length, bbox.Length / 4)));

            for (int idx = 0; idx < count; idx++)
            {
                float score = MathF.Sqrt(Math.Clamp(cls[idx], 0, 1) * Math.Clamp(obj[idx], 0, 1));
                if (score < ScoreThreshold) continue;

                int r = idx / cols, c = idx % cols;
                float cx = (c + bbox[idx * 4]) * stride;
                float cy = (r + bbox[idx * 4 + 1]) * stride;
                float w = MathF.Exp(bbox[idx * 4 + 2]) * stride;
                float h = MathF.Exp(bbox[idx * 4 + 3]) * stride;
                boxes.Add((cx - w / 2, cy - h / 2, w, h, score));
            }
        }

        var bounds = new PixelRect(0, 0, image.Width, image.Height);
        return Nms(boxes)
            .Select(b => PixelRect.FromLtrb(
                (int)MathF.Floor(b.X / scale), (int)MathF.Floor(b.Y / scale),
                (int)MathF.Ceiling((b.X + b.W) / scale), (int)MathF.Ceiling((b.Y + b.H) / scale)))
            .Select(r => r.Intersect(bounds))
            .Where(r => !r.IsEmpty)
            .ToList();
    }

    /// <summary>BGRA → 1×3×H×W 的 BGR 浮点（0~255，不减均值），右下角补零。缩小用双线性采样。</summary>
    private static DenseTensor<float> ToTensor(PixelBuffer image, int w, int h, int padW, int padH)
    {
        var tensor = new DenseTensor<float>([1, 3, padH, padW]);
        var buffer = tensor.Buffer.Span;
        int plane = padH * padW;
        var data = image.Bgra;
        float sx = (float)image.Width / w;
        float sy = (float)image.Height / h;

        for (int y = 0; y < h; y++)
        {
            float fy = Math.Max(0, (y + 0.5f) * sy - 0.5f);
            int y0 = Math.Min((int)fy, image.Height - 1);
            int y1 = Math.Min(y0 + 1, image.Height - 1);
            float wy = fy - y0;

            for (int x = 0; x < w; x++)
            {
                float fx = Math.Max(0, (x + 0.5f) * sx - 0.5f);
                int x0 = Math.Min((int)fx, image.Width - 1);
                int x1 = Math.Min(x0 + 1, image.Width - 1);
                float wx = fx - x0;

                int i00 = image.IndexOf(x0, y0), i01 = image.IndexOf(x1, y0);
                int i10 = image.IndexOf(x0, y1), i11 = image.IndexOf(x1, y1);

                for (int ch = 0; ch < 3; ch++)
                {
                    float top = data[i00 + ch] * (1 - wx) + data[i01 + ch] * wx;
                    float bottom = data[i10 + ch] * (1 - wx) + data[i11 + ch] * wx;
                    buffer[ch * plane + y * padW + x] = top * (1 - wy) + bottom * wy;
                }
            }
        }

        return tensor;
    }

    private static List<(float X, float Y, float W, float H, float Score)> Nms(
        List<(float X, float Y, float W, float H, float Score)> boxes)
    {
        var kept = new List<(float X, float Y, float W, float H, float Score)>();
        foreach (var b in boxes.OrderByDescending(b => b.Score))
        {
            if (kept.All(k => IoU(k, b) <= NmsThreshold)) kept.Add(b);
        }
        return kept;

        static float IoU((float X, float Y, float W, float H, float Score) a,
                         (float X, float Y, float W, float H, float Score) b)
        {
            float l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
            float r = Math.Min(a.X + a.W, b.X + b.W), btm = Math.Min(a.Y + a.H, b.Y + b.H);
            float inter = Math.Max(0, r - l) * Math.Max(0, btm - t);
            float union = a.W * a.H + b.W * b.H - inter;
            return union <= 0 ? 0 : inter / union;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _idle.Dispose();
        Release();
    }
}
