using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XkScreenshot.Translate;

/// <summary>
/// Bergamot 模型在磁盘上怎么摆，以及那份 marian 启动时要读的 config.txt。
///
/// 下载器和推理引擎必须对同一套文件名达成一致，所以这些规则集中放在这里，
/// 两边都从这儿问，免得一边改了文件名另一边加载不到。
///
/// <code>
/// models/bergamot/{源}-{目标}/
///     model.*.intgemm.alphas.bin   权重（int8）
///     lex.50.50.*.s2t.bin          词汇捷径表
///     vocab.*.spm                  收发共用一份词表
///       —— 或者分开的两份：srcvocab.*.spm + trgvocab.*.spm
///     config.txt                   按上面实际的文件名生成
/// </code>
///
/// 语言代码用 Mozilla 模型库那一套（<c>zh</c>、<c>zh_hant</c>、<c>nb</c>…），
/// 里面不含连字符，所以目录名里第一个 <c>-</c> 就是源和目标的分界。
/// </summary>
public static class BergamotModelDir
{
    /// <summary>模型根目录下装 Bergamot 的那个子目录名。</summary>
    public const string FolderName = "bergamot";

    public static string PairDir(string root, string from, string to)
        => Path.Combine(root, $"{from}-{to}");

    /// <summary>三类文件齐了才算装好 —— 缺一个 marian 都起不来。</summary>
    public static bool IsInstalled(string root, string from, string to)
        => Classify(PairDir(root, from, to)) is not null;

    /// <summary>扫出根目录下所有装齐了的方向。</summary>
    public static IEnumerable<(string From, string To)> EnumerateInstalled(string root)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(dir);
            int dash = name.IndexOf('-');
            if (dash <= 0 || dash == name.Length - 1) continue;
            if (Classify(dir) is null) continue;

            yield return (name[..dash], name[(dash + 1)..]);
        }
    }

    /// <summary>
    /// 保证目录里有 config.txt，返回它的路径。
    ///
    /// 生成而不是跟模型一起下：这份配置得跟目录里的实际文件名对上，而同一个语言方向
    /// 在模型库里换代时文件名会变（共用词表变成收发两份就是一例）。照目录里有什么写什么，
    /// 手工拷进来的模型也能直接用。
    /// </summary>
    public static string EnsureConfig(string pairDir)
    {
        string path = Path.Combine(pairDir, "config.txt");
        if (File.Exists(path)) return path;

        WriteConfig(pairDir);
        return path;
    }

    public static void WriteConfig(string pairDir)
    {
        var files = Classify(pairDir)
            ?? throw new FileNotFoundException($"{pairDir} 里的 Bergamot 模型文件不全。");

        // 权重文件名里带 alphas 的是「每列一个缩放系数」那种量化，用另一套 GEMM 内核
        string gemm = files.Model.Contains("alphas", StringComparison.OrdinalIgnoreCase)
            ? "int8shiftAlphaAll"
            : "int8shiftAll";

        var sb = new StringBuilder();
        sb.AppendLine("relative-paths: true");
        sb.AppendLine("models:");
        sb.AppendLine($"- {files.Model}");
        sb.AppendLine("vocabs:");
        sb.AppendLine($"- {files.SrcVocab}");
        sb.AppendLine($"- {files.TrgVocab}");
        sb.AppendLine("shortlist:");
        sb.AppendLine($"- {files.Shortlist}");
        sb.AppendLine("- false");
        // beam-size 1 就是贪心：Firefox 翻网页也是这么跑的，束搜索那点质量换不回延迟
        sb.AppendLine("beam-size: 1");
        sb.AppendLine("normalize: 1.0");
        sb.AppendLine("word-penalty: 0");
        sb.AppendLine("max-length-break: 128");
        sb.AppendLine("mini-batch-words: 1024");
        sb.AppendLine("workspace: 128");
        sb.AppendLine("max-length-factor: 2.0");
        sb.AppendLine("skip-cost: true");
        // 0 = 就在调用线程上算。翻译本来就已经在后台线程上了，再让它自己开线程池没意义
        sb.AppendLine("cpu-threads: 0");
        sb.AppendLine("quiet: true");
        sb.AppendLine("quiet-translation: true");
        sb.AppendLine($"gemm-precision: {gemm}");

        // 不带 BOM：marian 那边是普通的 YAML 解析，BOM 会被当成第一个 key 的一部分
        File.WriteAllText(Path.Combine(pairDir, "config.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>目录里的文件按用途归类，缺任何一类就返回 null。</summary>
    internal static ModelFiles? Classify(string pairDir)
    {
        if (!Directory.Exists(pairDir)) return null;

        string? model = null, shortlist = null, vocab = null, srcVocab = null, trgVocab = null;

        // 自己比前后缀而不用通配符：Win32 的匹配会连 8.3 短名一起算进去，
        // 而 srcvocab/trgvocab/vocab 这三个名字只差一个前缀，错配一次就配错了模型
        foreach (string file in Directory.EnumerateFiles(pairDir))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("model.", StringComparison.Ordinal)
                && name.EndsWith(".bin", StringComparison.Ordinal)) model = name;
            else if (name.StartsWith("lex.", StringComparison.Ordinal)
                && name.EndsWith(".bin", StringComparison.Ordinal)) shortlist = name;
            else if (name.StartsWith("srcvocab.", StringComparison.Ordinal)
                && name.EndsWith(".spm", StringComparison.Ordinal)) srcVocab = name;
            else if (name.StartsWith("trgvocab.", StringComparison.Ordinal)
                && name.EndsWith(".spm", StringComparison.Ordinal)) trgVocab = name;
            else if (name.StartsWith("vocab.", StringComparison.Ordinal)
                && name.EndsWith(".spm", StringComparison.Ordinal)) vocab = name;
        }

        // 共用词表的模型只发一份 vocab，配置里当收发两份写
        srcVocab ??= vocab;
        trgVocab ??= vocab;

        if (model is null || shortlist is null || srcVocab is null || trgVocab is null) return null;
        return new ModelFiles(model, shortlist, srcVocab, trgVocab);
    }

    internal sealed record ModelFiles(string Model, string Shortlist, string SrcVocab, string TrgVocab);
}
