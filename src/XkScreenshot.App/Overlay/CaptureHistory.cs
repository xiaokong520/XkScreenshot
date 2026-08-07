using System;
using System.Collections.Generic;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 截过的区域，最近的排在最前。
///
/// 只记矩形，不记图 —— 用途是「刚才那块地方内容变了，再截一次」，
/// 要的是那个框，不是那张旧图。这也让缓存三十条的代价小到可以忽略。
///
/// 只活在内存里，不落盘：显示器插拔、改分辨率、换扩展屏排布之后，
/// 上一次开机时的坐标指向的多半已经不是同一块地方了，
/// 而一份看着像那么回事、实际全错位的历史比没有历史更糟。
/// </summary>
public sealed class CaptureHistory
{
    public const int DefaultCapacity = 30;

    /// <summary>上限只是防呆。回溯是一格一格按过去的，几百条根本翻不到底。</summary>
    public const int MaxCapacity = 200;

    private readonly List<PixelRect> _items = [];
    private int _capacity = DefaultCapacity;

    /// <summary>缓存多少条。0 = 关掉这个功能。</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Clamp(value, 0, MaxCapacity);
            Trim();
        }
    }

    /// <summary>下标越大越早。</summary>
    public IReadOnlyList<PixelRect> Items => _items;

    public void Record(PixelRect rect)
    {
        if (_capacity == 0 || rect.IsEmpty) return;

        // 同一块区域反复截是常事（就是为了看它变成什么样了）。不去重的话，
        // 三十格会被同一个矩形占满，回溯就再也翻不到别的区域上去了。
        _items.Remove(rect);
        _items.Insert(0, rect);
        Trim();
    }

    private void Trim()
    {
        if (_items.Count > _capacity)
            _items.RemoveRange(_capacity, _items.Count - _capacity);
    }
}
