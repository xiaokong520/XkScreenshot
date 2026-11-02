using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 智能打码的入口：一张图进来，一串该糊掉的框出去。
///
/// OCR 和人脸检测互不依赖，并行跑；头像要以文字行为锚点、名字要看头像在哪，这两步跟在后面。
/// 文字识别固定走本地 PaddleOCR —— 在线识别给不出可靠的行框，而且会把整张图发出去，
/// 用户点「打码」恰恰是因为图里有不想外传的东西。
/// </summary>
public sealed class PrivacyDetector : IDisposable
{
    /// <summary>各来源的框先各自外扩这么多再合并：OCR 行框是贴着字形量的，一点余量都没有。</summary>
    private const int Pad = 2;

    /// <summary>人脸框只框到五官，头像还有头发、背景，四周各放 25%。</summary>
    private const double FaceGrow = 0.25;

    private readonly IOcrEngine _ocr;
    private readonly FaceDetector _faces = new();

    public PrivacyDetector(IOcrEngine ocr)
    {
        _ocr = ocr;
    }

    /// <param name="image">选区的画面，喂给 OCR。</param>
    /// <param name="pixels">同一块画面的非预乘 BGRA，头像和人脸检测用。</param>
    public Task<PrivacyReport> DetectAsync(
        BitmapSource image, PixelBuffer pixels, PrivacyOptions options, CancellationToken ct = default,
        IProgress<PrivacyStage>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!image.IsFrozen) image = (BitmapSource)image.GetAsFrozen();
        var frozen = image;
        // 即使 OCR 命中缓存、同步返回，后续规则和像素扫描也绝不回到 UI 线程。
        return Task.Run(() => DetectCoreAsync(frozen, pixels, options, ct, progress), ct);
    }

    private async Task<PrivacyReport> DetectCoreAsync(
        BitmapSource image, PixelBuffer pixels, PrivacyOptions options, CancellationToken ct,
        IProgress<PrivacyStage>? progress)
    {
        progress?.Report(PrivacyStage.Recognizing);
        var ocrTask = options.NeedsText
            ? _ocr.RecognizeAsync(image, ct)
            : Task.FromResult<IReadOnlyList<OcrLine>>([]);
        var faceTask = options.Avatar
            ? Task.Run(() => _faces.Detect(pixels, ct), ct)
            : Task.FromResult<IReadOnlyList<PixelRect>>([]);

        // 两个任务都观察到完成/异常，不能因 OCR 先失败而遗留无人观察的人脸任务。
        await Task.WhenAll(ocrTask, faceTask).ConfigureAwait(false);
        var lines = await ocrTask.ConfigureAwait(false);
        var faces = await faceTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        progress?.Report(PrivacyStage.Locating);
        var regions = new List<PrivacyRegion>();
        var taken = new List<TextSpan>[lines.Count];
        var aligned = new OcrLine?[lines.Count];
        var precise = new bool[lines.Count];

        PrivacyRegion TextRegion(int index, TextSpan span)
        {
            // 同一行有邮箱和电话时只扫描一次像素；只有命中的行才做校准。
            if (aligned[index] is null)
            {
                precise[index] = TextPixelAligner.TryAlign(lines[index], pixels, out var refined);
                aligned[index] = refined;
            }
            return new PrivacyRegion(LineSlicer.Slice(aligned[index]!.Value, span.Start, span.Length), span.Kind)
            { HorizontalPadding = precise[index] ? 0 : Pad };
        }

        // 文字类
        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            taken[i] = TextPatterns.Find(lines[i].Text, options);
            foreach (var span in taken[i])
                regions.Add(TextRegion(i, span));
        }

        // 头像
        var avatars = new List<Avatar>();
        if (options.Avatar || options.Name)
        {
            avatars = AvatarFinder.Find(lines, pixels, faces, ct);
            if (options.Avatar)
            {
                foreach (var face in faces) regions.Add(new PrivacyRegion(Grow(face, FaceGrow), PrivacyKind.Avatar));
                foreach (var avatar in avatars) regions.Add(new PrivacyRegion(avatar.Bounds, PrivacyKind.Avatar));
            }
        }

        // 名字
        string? notice = null;
        if (options.Name)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var span in NameFinder.FindInText(lines[i].Text, taken[i]))
                {
                    taken[i].Add(span);
                    regions.Add(TextRegion(i, span));
                }
            }

            foreach (var rect in NameFinder.FindBesideAvatars(lines, avatars))
                regions.Add(new PrivacyRegion(rect, PrivacyKind.Name));

            // 窗口顶部那行标题名（联系人、文档名）。放在最后：它压不到别的规则上
            foreach (var rect in NameFinder.FindTitle(lines, pixels.Height))
                regions.Add(new PrivacyRegion(rect, PrivacyKind.Name));

            if (options.Llm is { } llm && lines.Count > 0)
            {
                progress?.Report(PrivacyStage.OnlineNames);
                try
                {
                    foreach (var (line, span) in await LlmNameClassifier.ClassifyAsync(llm, lines, ct).ConfigureAwait(false))
                    {
                        if (taken[line].Any(t => t.Overlaps(span))) continue;
                        taken[line].Add(span);
                        regions.Add(TextRegion(line, span));
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // 断网、超时、Key 不对都走这里。本地找到的照样糊上，只提一句
                    notice = "在线名字识别失败，已用本地结果";
                }
            }
        }

        // 本地分词识别的正文人名（默认关，比在线准不了多少，胜在不联网）
        if (options.LocalNames && lines.Count > 0)
        {
            foreach (var (line, span) in LocalNameClassifier.Classify(lines, ct))
            {
                if (taken[line].Any(t => t.Overlaps(span))) continue;
                taken[line].Add(span);
                regions.Add(TextRegion(line, span));
            }
        }

        ct.ThrowIfCancellationRequested();
        return new PrivacyReport(Merge(regions, pixels.Width, pixels.Height, ct), notice);
    }

    private static PixelRect Grow(PixelRect r, double ratio)
    {
        int dx = (int)Math.Round(r.Width * ratio), dy = (int)Math.Round(r.Height * ratio);
        return PixelRect.FromLtrb(r.X - dx, r.Y - dy, r.Right + dx, r.Bottom + dy);
    }

    /// <summary>只合并同一行、紧邻且不会额外遮住大片内容的框；头像不吞并旁边的文字。</summary>
    internal static List<PrivacyRegion> Merge(IEnumerable<PrivacyRegion> regions, int width, int height,
        CancellationToken ct = default)
    {
        var image = new PixelRect(0, 0, width, height);
        var list = regions.Where(r => !r.Bounds.IsEmpty)
            .Select(r => r with { Bounds = PixelRect.FromLtrb(r.Bounds.X - r.HorizontalPadding, r.Bounds.Y - Pad,
                r.Bounds.Right + r.HorizontalPadding, r.Bounds.Bottom + Pad).Intersect(image) })
            .Where(r => !r.Bounds.IsEmpty)
            .OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();

        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < list.Count;)
            {
                var a = list[i].Bounds;
                var b = list[j].Bounds;
                // 列表按 Y 排序；后面的框不可能再接上当前行。
                if (b.Y > a.Bottom + Pad) break;
                if ((list[i].Kind == PrivacyKind.Avatar) != (list[j].Kind == PrivacyKind.Avatar)
                    || !AvatarFinder.Inflate(a, Pad).IntersectsWith(b)) { j++; continue; }

                var union = PixelRect.FromLtrb(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                    Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
                var overlap = a.Intersect(b);
                double covered = a.Area + b.Area - overlap.Area;
                bool contained = overlap == a || overlap == b;
                int vertical = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                bool sameLine = vertical >= Math.Min(a.Height, b.Height) * 0.7;
                if (!contained && (!sameLine || union.Area > covered * 1.15)) { j++; continue; }

                list[i] = list[i] with { Bounds = union };
                list.RemoveAt(j);
            }
        }
        return list;
    }

    public void Dispose() => _faces.Dispose();
}
