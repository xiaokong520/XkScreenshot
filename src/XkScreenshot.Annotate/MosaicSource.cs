using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    // 缓存的是每块一个像素的小位图，不是每次重绘都创建数千个画刷。
    // 上限约 4MB 像素数据，且随选区的 MosaicSource 一起释放。
    private const int MaxCachedPixels = 1_048_576;
    private const int MaxCachedRegions = 128;
    private readonly Dictionary<(int X0, int Y0, int X1, int Y1, int Block), (DrawingGroup Drawing, int Pixels)> _cache = new();
    private readonly Queue<(int, int, int, int, int)> _cacheOrder = new();
    private int _cachedPixels;

    public MosaicSource(byte[] bgra, int width, int height, int stride)
    {
        _bgra = bgra;
        _width = width;
        _height = height;
        _stride = stride;
    }

    /// <summary>底下那份非预乘 BGRA。只读着用 —— 智能打码拿它找头像，免得再裁一遍。</summary>
    public byte[] Bgra => _bgra;
    public int Width => _width;
    public int Height => _height;
    public int Stride => _stride;

    public void DrawMosaic(DrawingContext dc, Rect area, int block)
    {
        if (block < 1) block = 1;

        int x0 = Math.Max(0, (int)Math.Floor(area.X));
        int y0 = Math.Max(0, (int)Math.Floor(area.Y));
        int x1 = Math.Min(_width, (int)Math.Ceiling(area.Right));
        int y1 = Math.Min(_height, (int)Math.Ceiling(area.Bottom));
        if (x1 <= x0 || y1 <= y0) return;

        var key = (x0, y0, x1, y1, block);
        if (_cache.TryGetValue(key, out var cached))
        {
            dc.DrawDrawing(cached.Drawing);
            return;
        }

        // 与原来一样对齐全局网格；边缘块只平均实际覆盖的部分，不引入框外像素。
        int startX = x0 - x0 % block, startY = y0 - y0 % block;
        int cols = (x1 - startX + block - 1) / block;
        int rows = (y1 - startY + block - 1) / block;
        var pixels = new byte[checked(cols * rows * 4)];
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < cols; col++)
        {
            int bx = startX + col * block, by = startY + row * block;
            var color = Average(Math.Max(bx, x0), Math.Max(by, y0), Math.Min(bx + block, x1), Math.Min(by + block, y1));
            int i = (row * cols + col) * 4;
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }

        var image = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Bgra32, null, pixels, cols * 4);
        image.Freeze();
        var drawing = new DrawingGroup();
        // 最近邻保证每块是纯色；双线性会把块间边界抹成渐变，降低遮盖效果。
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
        using (var target = drawing.Open())
        {
            target.PushClip(new RectangleGeometry(new Rect(x0, y0, x1 - x0, y1 - y0)));
            target.DrawImage(image, new Rect(startX, startY, cols * block, rows * block));
            target.Pop();
        }
        drawing.Freeze();
        int count = cols * rows;
        if (count <= MaxCachedPixels)
        {
            while (_cache.Count >= MaxCachedRegions || _cachedPixels + count > MaxCachedPixels)
            {
                var oldest = _cacheOrder.Dequeue();
                _cachedPixels -= _cache[oldest].Pixels;
                _cache.Remove(oldest);
            }
            _cache[key] = (drawing, count);
            _cacheOrder.Enqueue(key);
            _cachedPixels += count;
        }
        dc.DrawDrawing(drawing);
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
