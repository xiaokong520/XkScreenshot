using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XkScreenshot.Translate;

/// <summary>
/// SentencePiece tokenizer，支持两种格式：
/// 1. JSON 格式（sentencepiece 库导出的标准格式，含 vocab + 可选 BPE merges）
/// 2. .spm 二进制格式（SentencePiece 原生 protobuf 模型文件）
///
/// 解码只需 ID→token 映射；编码使用预分词 + 最长前缀匹配。
/// </summary>
public sealed class SentencePieceTokenizer
{
    private Dictionary<string, int> _vocab = null!;
    private string[] _idToToken = null!;
    private readonly List<(string, string)> _merges = [];
    private int _bosId;
    private int _eosId;
    private int _unkId;

    public int BosId => _bosId;
    public int EosId => _eosId;
    public int VocabSize => _idToToken.Length;

    /// <summary>
    /// 从文件路径构造。扩展名 .spm 走二进制 parser，.json 走 JSON parser。
    /// </summary>
    public SentencePieceTokenizer(string path)
    {
        if (path.EndsWith(".spm", StringComparison.OrdinalIgnoreCase))
        {
            var (vocab, idToToken) = LoadFromSpm(path);
            _vocab = vocab;
            _idToToken = idToToken;
        }
        else
        {
            var (vocab, idToToken) = LoadFromJson(path);
            _vocab = vocab;
            _idToToken = idToToken;
        }

        _bosId = 0;
        _eosId = 0;
        _unkId = 0;
        if (_vocab.TryGetValue("<s>", out int b)) _bosId = b;
        if (_vocab.TryGetValue("</s>", out int e)) _eosId = e;
        if (_vocab.TryGetValue("<unk>", out int u)) _unkId = u;
    }

    // ---------------- 公共方法 ----------------

    /// <summary>把源语言文本转成 token ID 序列（含 BOS/EOS）。</summary>
    public IReadOnlyList<int> Encode(string text)
    {
        var pieces = PreTokenize(text);

        if (_merges.Count > 0)
            pieces = ApplyBpe(pieces);

        var ids = new List<int> { _bosId };
        foreach (var piece in pieces)
        {
            if (_vocab.TryGetValue(piece, out int id))
                ids.Add(id);
            else
            {
                foreach (char c in piece)
                {
                    string cs = c.ToString();
                    ids.Add(_vocab.TryGetValue(cs, out int cid) ? cid : _unkId);
                }
            }
        }
        ids.Add(_eosId);
        return ids;
    }

    /// <summary>把 token ID 序列还原为文本。</summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder();
        foreach (int id in ids)
        {
            if (id < 0 || id >= _idToToken.Length) continue;
            string token = _idToToken[id];
            sb.Append(token.Replace("▁", " "));
        }
        return sb.ToString().Trim();
    }

    // ---------------- JSON 加载 ----------------

    private (Dictionary<string, int>, string[]) LoadFromJson(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var vocabObj = root.GetProperty("model").GetProperty("vocab");
        var dict = new Dictionary<string, int>(vocabObj.GetArrayLength());
        var temp = new Dictionary<int, string>();

        foreach (var item in vocabObj.EnumerateArray())
        {
            string token = item[0].GetString()!;
            int id = item[1].GetInt32();
            dict[token] = id;
            temp[id] = token;
        }

        if (root.GetProperty("model").TryGetProperty("merges", out var mergesElem))
        {
            foreach (var merge in mergesElem.EnumerateArray())
            {
                string m = merge.GetString()!;
                int space = m.IndexOf(' ');
                if (space > 0)
                    _merges.Add((m[..space], m[(space + 1)..]));
            }
        }

        string[] idToToken = new string[temp.Count];
        foreach (var (id2, token2) in temp)
            idToToken[id2] = token2;

        return (dict, idToToken);
    }

    // ---------------- SPM 二进制加载 ----------------

    /// <summary>
    /// 解析 SentencePiece protobuf 模型文件，只提取 pieces 表。
    ///
    /// ModelProto 结构：
    ///   field 1: pieces (repeated SentencePiece) — 每个元素独立 tag(1,2) + length + submessage
    ///   field 2: trainer_spec — 跳过
    ///   field 3: normalizer_spec — 跳过
    ///
    /// SentencePiece 子消息：
    ///   field 1: piece  (string)
    ///   field 2: score  (float)
    ///   field 3: type   (int32)
    /// </summary>
    private static (Dictionary<string, int>, string[]) LoadFromSpm(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int pos = 0;

        var dict = new Dictionary<string, int>(65000);
        var temp = new Dictionary<int, string>();
        int nextId = 0;

        while (pos < data.Length)
        {
            var (fieldNum, wireType) = ReadTag(data, ref pos);
            if (fieldNum == 0) break; // tag 为 0 表示文件末尾

            if (fieldNum == 1 && wireType == 2)
            {
                // 一个 SentencePiece 元素
                int msgLen = (int)ReadVarint(data, ref pos);
                int end = pos + msgLen;

                string piece = "";
                while (pos < end)
                {
                    var (fn, wt) = ReadTag(data, ref pos);
                    if (fn == 1 && wt == 2)
                    {
                        int len = (int)ReadVarint(data, ref pos);
                        piece = Encoding.UTF8.GetString(data, pos, len);
                        pos += len;
                    }
                    else if (fn == 2 && wt == 5)
                    {
                        pos += 4; // float score，不需要
                    }
                    else if (fn == 3 && wt == 0)
                    {
                        ReadVarint(data, ref pos); // type，不需要
                    }
                    else
                    {
                        SkipField(data, ref pos, wt);
                    }
                }

                pos = end;
                if (!string.IsNullOrEmpty(piece))
                {
                    dict[piece] = nextId;
                    temp[nextId] = piece;
                    nextId++;
                }
            }
            else
            {
                SkipField(data, ref pos, wireType);
            }
        }

        string[] idToToken = new string[nextId];
        foreach (var (id, token) in temp)
            idToToken[id] = token;

        return (dict, idToToken);
    }

    // ---------------- Protobuf 底层 ----------------

    private static (int, int) ReadTag(byte[] data, ref int pos)
    {
        if (pos >= data.Length) return (0, 0);
        ulong tag = ReadVarint(data, ref pos);
        return ((int)(tag >> 3), (int)(tag & 0x07));
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return value;
    }

    private static void SkipField(byte[] data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(data, ref pos); break;
            case 1: pos += 8; break;
            case 5: pos += 4; break;
            case 2: int len = (int)ReadVarint(data, ref pos); pos += len; break;
        }
    }

    // ---------------- 预分词 ----------------

    private static List<string> PreTokenize(string text)
    {
        var pieces = new List<string>();

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                pieces.Add("▁");
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }
            else if (IsCjk(c))
            {
                pieces.Add(c.ToString());
                i++;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i]))
                    i++;
                pieces.Add(text[start..i]);
            }
        }

        return pieces;
    }

    private static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿'
            or >= '぀' and <= 'ヿ'
            or >= '가' and <= '힯';

    // ---------------- BPE 合并 ----------------

    private List<string> ApplyBpe(List<string> pieces)
    {
        var tokens = new List<string>(pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
            tokens.Add(i == 0 ? pieces[i] : $"▁{pieces[i]}");

        bool changed = true;
        while (changed)
        {
            changed = false;
            int bestRank = int.MaxValue;
            int bestIdx = -1;

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                int rank = Rank(tokens[i], tokens[i + 1]);
                if (rank >= 0 && rank < bestRank)
                {
                    bestRank = rank;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                tokens[bestIdx] = tokens[bestIdx] + tokens[bestIdx + 1];
                tokens.RemoveAt(bestIdx + 1);
                changed = true;
            }
        }

        return tokens;
    }

    private int Rank(string a, string b)
    {
        for (int i = 0; i < _merges.Count; i++)
        {
            if (_merges[i].Item1 == a && _merges[i].Item2 == b)
                return i;
        }
        return -1;
    }
}
