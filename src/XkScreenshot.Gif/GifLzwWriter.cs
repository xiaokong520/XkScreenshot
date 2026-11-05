namespace XkScreenshot.Gif;

/// <summary>
/// GIF 版 LZW。和 TIFF 的 LZW 不是同一种打包方式：码流按**低位在前**塞进字节、
/// 字典满 4096 要发一个 clear 重来、每 255 字节前面要加一个长度字节。
///
/// 一个实例可以连着一帧一帧写：字典和位缓冲每帧重置，哈希表只分配一次。
/// </summary>
internal sealed class GifLzwWriter
{
    private const int HashSize = 5003;   // 质数，且大于 4096，线性探测一定有空位
    private const int MaxCodes = 4096;

    private readonly int[] _hashKeys = new int[HashSize];
    private readonly int[] _hashCodes = new int[HashSize];
    private readonly byte[] _block = new byte[255];

    private Stream _output = Stream.Null;
    private int _blockLength;
    private int _accumulator;
    private int _bitCount;
    private int _codeSize;
    private int _nextCode;
    private int _minCodeSize;

    /// <summary>压缩一帧的索引像素并写进流（含起始码宽、数据子块与 0 结束符）。</summary>
    public void Write(Stream output, byte[] indices, int count, int minCodeSize)
    {
        _output = output;
        _minCodeSize = minCodeSize;
        _blockLength = 0;
        _accumulator = 0;
        _bitCount = 0;

        output.WriteByte((byte)minCodeSize);

        if (count <= 0)
        {
            output.WriteByte(0);
            return;
        }

        int clear = 1 << minCodeSize;
        int eoi = clear + 1;

        _codeSize = minCodeSize + 1;
        _nextCode = eoi + 1;
        ClearDictionary();

        Emit(clear);

        int prefix = indices[0];

        for (int i = 1; i < count; i++)
        {
            int pixel = indices[i];
            int key = (prefix << 8) | pixel;

            int code = Find(key);
            if (code >= 0)
            {
                prefix = code;
                continue;
            }

            Emit(prefix);

            if (_nextCode < MaxCodes)
            {
                Insert(key, _nextCode);
                _nextCode++;
            }
            else
            {
                // 字典满了：告诉解码器「从头来」，随后所有状态一起归零
                Emit(clear);
                ClearDictionary();
                _codeSize = _minCodeSize + 1;
                _nextCode = eoi + 1;
            }

            prefix = pixel;
        }

        Emit(prefix);
        Emit(eoi);

        FlushBits();
        FlushBlock();
        output.WriteByte(0);   // 图像数据到此为止
    }

    private void ClearDictionary()
    {
        Array.Fill(_hashKeys, -1);
    }

    private static int Hash(int key) => (int)(((uint)key * 2654435761u) % HashSize);

    private int Find(int key)
    {
        int slot = Hash(key);

        while (true)
        {
            int stored = _hashKeys[slot];
            if (stored == key) return _hashCodes[slot];
            if (stored == -1) return -1;
            slot = slot + 1 == HashSize ? 0 : slot + 1;
        }
    }

    private void Insert(int key, int code)
    {
        int slot = Hash(key);

        while (_hashKeys[slot] != -1)
            slot = slot + 1 == HashSize ? 0 : slot + 1;

        _hashKeys[slot] = key;
        _hashCodes[slot] = code;
    }

    /// <summary>
    /// 写一个码，并按「下一个要分配的码还能不能塞进当前码宽」决定是否加宽。
    ///
    /// 这个判断必须放在**写完码之后**：解码器比编码器晚一步建表（它读到第二个码才
    /// 建第一条），所以它是在读完同一个码之后才加宽。提前一个码加宽，两边就从这里
    /// 开始各读各的，画面会碎成噪点 —— 这类错不会抛异常，只会静静地把图毁掉。
    /// </summary>
    private void Emit(int code)
    {
        _accumulator |= code << _bitCount;
        _bitCount += _codeSize;

        while (_bitCount >= 8)
        {
            AddByte((byte)_accumulator);
            _accumulator >>= 8;
            _bitCount -= 8;
        }

        if (_nextCode > (1 << _codeSize) - 1 && _codeSize < 12) _codeSize++;
    }

    private void FlushBits()
    {
        if (_bitCount > 0) AddByte((byte)_accumulator);
        _accumulator = 0;
        _bitCount = 0;
    }

    private void AddByte(byte value)
    {
        _block[_blockLength++] = value;
        if (_blockLength == _block.Length) FlushBlock();
    }

    private void FlushBlock()
    {
        if (_blockLength == 0) return;

        _output.WriteByte((byte)_blockLength);
        _output.Write(_block, 0, _blockLength);
        _blockLength = 0;
    }
}
