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
/// 这个类只管数据，不碰文件：往哪儿存、什么时候存是 <see cref="Settings.HistoryStore"/> 的事。
/// </summary>
public sealed class CaptureHistory
{
    public const int DefaultCapacity = 30;

    /// <summary>上限只是防呆。回溯是一格一格按过去的，几百条根本翻不到底。</summary>
    public const int MaxCapacity = 200;

    private readonly List<PixelRect> _items = [];
    private int _capacity = DefaultCapacity;

    /// <summary>内容变了。落盘的时机就看它。</summary>
    public event Action? Changed;

    /// <summary>缓存多少条。0 = 关掉这个功能。</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Clamp(value, 0, MaxCapacity);
            if (Trim()) Changed?.Invoke();
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
        Changed?.Invoke();
    }

    /// <summary>
    /// 从磁盘装回来。不触发 <see cref="Changed"/> —— 刚读上来的东西没必要原样写回去。
    ///
    /// 这里**不**剔除「已经不在当前桌面上」的条目：开机那一刻某台显示器可能还没醒、
    /// 笔记本可能正拔着扩展坞，照那时候的桌面去删，删掉的是过会儿就会回来的东西。
    /// 错位的条目在真要用它的时候由 <see cref="CaptureSession.StepHistory"/> 跳过，
    /// 那时候的桌面才是作数的那一个。
    /// </summary>
    public void Restore(IEnumerable<PixelRect> items)
    {
        _items.Clear();
        foreach (var rect in items)
        {
            if (_items.Count >= _capacity) break;
            if (rect.IsEmpty || _items.Contains(rect)) continue;
            _items.Add(rect);
        }
    }

    private bool Trim()
    {
        if (_items.Count <= _capacity) return false;

        _items.RemoveRange(_capacity, _items.Count - _capacity);
        return true;
    }
}
