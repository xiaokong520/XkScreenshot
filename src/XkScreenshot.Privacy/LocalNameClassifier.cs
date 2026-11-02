using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using JiebaNet.Segmenter;
using JiebaNet.Segmenter.PosSeg;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 本地的正文人名识别：结巴分词 + 词性标注，只取 nr（人名）。
///
/// 词库打包在程序集里，第一次用到时解到缓存目录。开这个开关的人要的就是离线识别，
/// 再让他去下载十兆词库没有道理；但也没必要每次启动都加载，所以按需解压、按需建分词器。
///
/// 准确率比不上大模型：实测「李雷」「王小明」「张三」「司马懿」都对，
/// 「支付宝」会被标成 nr（品牌词在词库里就是人名标注），「欧阳明月」会被拆成两截。
/// 所以它默认关着，只用来补「名字」那条规则覆盖不到的正文提及。
/// </summary>
internal static class LocalNameClassifier
{
    private const string ResourceName = "jieba-resources.zip";

    /// <summary>词库版本。换了打包内容要跟着改，否则用户机器上的旧解压目录会被直接复用。</summary>
    private const string CacheVersion = "v1";

    /// <summary>结巴标成 nr 但其实是品牌、平台名的，糊掉只会让人莫名其妙。</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "支付宝", "微信", "淘宝", "天猫", "京东", "拼多多", "美团", "饿了么", "抖音", "快手",
        "百度", "腾讯", "阿里", "阿里巴巴", "华为", "小米", "苹果", "微软", "谷歌", "字节",
        "网易", "哔哩哔哩", "知乎", "微博", "豆瓣", "滴滴", "携程", "去哪儿", "顺丰", "菜鸟",
    };

    private static readonly object Gate = new();
    private static PosSegmenter? _segmenter;

    public static List<(int Line, TextSpan Span)> Classify(IReadOnlyList<OcrLine> lines, CancellationToken ct)
    {
        var found = new List<(int, TextSpan)>();
        if (lines.Count == 0) return found;

        PosSegmenter segmenter;
        try
        {
            segmenter = Ensure();
        }
        catch (Exception)
        {
            // 词库解压失败（磁盘满、没权限）不该拖垮整次打码，退回其它规则就行
            return found;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string text = lines[i].Text;
            if (text.Length < 2) continue;

            int at = 0;
            foreach (var token in segmenter.Cut(text))
            {
                ct.ThrowIfCancellationRequested();
                if (token.Word.Length == 0) continue;

                // 分词是按顺序切出来的，从上一个词的末尾往后找就能还原出原始下标
                int index = text.IndexOf(token.Word, at, StringComparison.Ordinal);
                if (index < 0) continue;
                at = index + token.Word.Length;

                if (token.Flag != "nr") continue;

                string word = token.Word;
                if (word.Length is < 2 or > 4) continue;
                if (!NameFinder.LooksLikeSurname(word[0]) || NotNames.Contains(word)) continue;

                found.Add((i, new TextSpan(index, word.Length, PrivacyKind.Name)));
            }
        }

        return found;
    }

    private static PosSegmenter Ensure()
    {
        lock (Gate)
        {
            if (_segmenter is not null) return _segmenter;

            string dir = CacheDirectory();
            if (!File.Exists(Path.Combine(dir, "Resources", "dict.txt")))
            {
                Directory.CreateDirectory(dir);
                using var stream = typeof(LocalNameClassifier).Assembly.GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException("分词词库没有打进程序集");
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                zip.ExtractToDirectory(dir, overwriteFiles: true);
            }

            // ConfigFileBaseDir 指的就是词库所在的那一层（jieba 默认给的是 AppBase/Resources），
            // 不是它的父目录
            ConfigManager.ConfigFileBaseDir = Path.Combine(dir, "Resources");
            _segmenter = new PosSegmenter();
            return _segmenter;
        }
    }

    private static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XkScreenshot", "jieba-" + CacheVersion);
}
