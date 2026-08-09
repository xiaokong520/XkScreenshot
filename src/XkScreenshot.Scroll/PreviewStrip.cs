using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XkScreenshot.Scroll;

/// <summary>
/// 长图的缩略图，随拼接一起长。
///
/// 每接上一段就顺手抽几行存进来，而不是收工前把整张长图缩一遍：长图会长到几万行，
/// 每次进度更新都全量缩放的话，越拼越卡，正好卡在用户盯着看的时候。
///
/// 纵向用「抽行」而不是面积平均：抽行是常数代价且可以随长度加倍再抽稀一次，
/// 而面积平均要在缩放比例变化时把已有的内容重算一遍 —— 缩略图不值这个钱。
/// 代价是细横线可能被抽没，但缩略图的用途是「看一眼拼得对不对」，不是看细节。
/// </summary>
internal sealed class PreviewStrip
{
    private const int TargetWidth = 128;

    /// <summary>缩略图自身的行数上限。到顶就抽稀一半，抽样密度随之减半。</summary>
    private const int MaxRows = 720;

    private readonly int _srcStride;
    private readonly int _colStep;
    private readonly int _width;
    private readonly byte[] _data;

    private int _rows;
    private int _rowStep = 1;
    private int _phase;

    public PreviewStrip(int srcWidth, int srcStride)
    {
        _srcStride = srcStride;
        _colStep = Math.Max(1, srcWidth / TargetWidth);
        _width = Math.Max(1, srcWidth / _colStep);
        _data = new byte[MaxRows * _width * 4];
    }

    /// <summary>长图此刻的真实行数。缩略图纵向是抽过行的，画的时候得按这个数还原比例。</summary>
    public int SourceRows { get; private set; }

    public void Append(byte[] src, int fromRow, int count)
    {
        SourceRows += count;

        for (int y = fromRow; y < fromRow + count; y++)
        {
            if (_phase++ % _rowStep != 0) continue;
            if (_rows >= MaxRows) Halve();
            CopyRow(src, y);
        }
    }

    private void CopyRow(byte[] src, int y)
    {
        int from = y * _srcStride;
        int to = _rows * _width * 4;

        for (int k = 0; k < _width; k++)
        {
            int i = from + k * _colStep * 4;
            _data[to] = src[i];
            _data[to + 1] = src[i + 1];
            _data[to + 2] = src[i + 2];
            _data[to + 3] = 0xFF;
            to += 4;
        }
        _rows++;
    }

    private void Halve()
    {
        int line = _width * 4;
        for (int y = 1; y * 2 < _rows; y++)
            Buffer.BlockCopy(_data, y * 2 * line, _data, y * line, line);

        _rows /= 2;
        _rowStep *= 2;
        _phase = 0;
    }

    public BitmapSource? Build()
    {
        if (_rows == 0) return null;

        var bitmap = BitmapSource.Create(
            _width, _rows, 96, 96, PixelFormats.Bgra32, null, _data, _width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
