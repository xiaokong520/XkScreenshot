using System;
using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.Annotate;

/// <summary>
/// 马赛克的像素来源：选区内的原始画面。
///
/// 传进来的必须是**非预乘**的 BGRA 缓冲。用预乘数据求块平均会在半透明像素上偏色，
/// 而马赛克的全部作用就是遮盖敏感信息 —— 颜色偏了不影响遮盖效果，
/// 但会让马赛克块跟周围内容对不上，看起来很脏。
/// </summary>
public sealed class MosaicSource : IAnnotationContext
{
    private readonly byte[] _bgra;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;

    public MosaicSource(byte[] bgra, int width, int height, int stride)
    {
        _bgra = bgra;
        _width = width;
        _height = height;
        _stride = stride;
    }

    public void DrawMosaic(DrawingContext dc, Rect area, int block)
    {
        if (block < 1) block = 1;

        int x0 = Math.Max(0, (int)Math.Floor(area.X));
        int y0 = Math.Max(0, (int)Math.Floor(area.Y));
        int x1 = Math.Min(_width, (int)Math.Ceiling(area.Right));
        int y1 = Math.Min(_height, (int)Math.Ceiling(area.Bottom));
        if (x1 <= x0 || y1 <= y0) return;

        // 块的起点对齐到全局网格而不是选框左上角。不对齐的话，
        // 稍微挪动一下框选范围，整片马赛克就会跟着抖动重排。
        int startX = x0 - x0 % block;
        int startY = y0 - y0 % block;

        for (int by = startY; by < y1; by += block)
        {
            for (int bx = startX; bx < x1; bx += block)
            {
                int cellX0 = Math.Max(bx, x0);
                int cellY0 = Math.Max(by, y0);
                int cellX1 = Math.Min(bx + block, x1);
                int cellY1 = Math.Min(by + block, y1);
                if (cellX1 <= cellX0 || cellY1 <= cellY0) continue;

                var color = Average(cellX0, cellY0, cellX1, cellY1);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dc.DrawRectangle(brush, null,
                    new Rect(cellX0, cellY0, cellX1 - cellX0, cellY1 - cellY0));
            }
        }
    }

    private Color Average(int x0, int y0, int x1, int y1)
    {
        long b = 0, g = 0, r = 0;
        int n = 0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * _stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + x * 4;
                b += _bgra[i];
                g += _bgra[i + 1];
                r += _bgra[i + 2];
                n++;
            }
        }

        return n == 0
            ? Colors.Black
            : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
