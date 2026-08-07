using System;
using System.Collections.Generic;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.App.Overlay;

/// <summary>
/// 历史里的一条。
///
/// <paramref name="Bounds"/> 是截过的那块区域，<paramref name="Desktop"/> 是当时整个虚拟桌面的
/// 范围，<paramref name="Image"/> 指向盖住 Desktop 那一整张冻结画面。
/// Desktop 必须单独记：PNG 只带得出宽高，带不出它在虚拟屏幕坐标系里的原点，
/// 而副屏在主屏左边时那个原点是负数。
///
/// Image 为 null 表示只剩选区了 —— 画面没存下来（写盘失败），或者文件后来被删了。
/// 那种条目照样能回溯，只是把框摆在当前画面上，退回到「记住那块地方」这一层，
/// 而不是整条作废。
/// </summary>
public sealed record HistoryEntry(PixelRect Bounds, PixelRect Desktop, string? Image);

/// <summary>
/// 截过的东西，最近的排在最前。
///
/// 这个类只管顺序和去重，不碰文件：往哪儿存、什么时候存是 <see cref="Settings.HistoryStore"/> 的事。
/// </summary>
public sealed class CaptureHistory
{
    public const int DefaultCapacity = 30;

    /// <summary>上限只是防呆。回溯是一格一格按过去的，几百条根本翻不到底。</summary>
    public const int MaxCapacity = 200;

    private readonly List<HistoryEntry> _items = [];
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
    public IReadOnlyList<HistoryEntry> Items => _items;

    /// <summary>还活着的画面文件，供清理孤儿用。</summary>
    public IEnumerable<string> ImageIds()
    {
        foreach (var item in _items)
            if (item.Image is not null) yield return item.Image;
    }

    public void Record(PixelRect bounds, PixelRect desktop, string? image)
    {
        if (_capacity == 0 || bounds.IsEmpty) return;

        // 同一块区域反复截是常事（就是为了看它变成什么样了）。旧的那条要整个换掉：
        // 位置一样，但画面是新的那一张才对得上「我刚才截的是什么」。
        _items.RemoveAll(e => e.Bounds == bounds);
        _items.Insert(0, new HistoryEntry(bounds, desktop, image));
        Trim();
        Changed?.Invoke();
    }

    /// <summary>
    /// 从磁盘装回来。不触发 <see cref="Changed"/> —— 刚读上来的东西没必要原样写回去。
    ///
    /// 这里**不**剔除「已经不在当前桌面上」的条目：开机那一刻某台显示器可能还没醒、
    /// 笔记本可能正拔着扩展坞，照那时候的桌面去删，删掉的是过会儿就会回来的东西。
    /// 错位的条目在真要用它的时候由 <see cref="CaptureSession.StepHistory"/> 处理，
    /// 那时候的桌面才是作数的那一个。
    /// </summary>
    public void Restore(IEnumerable<HistoryEntry> items)
    {
        _items.Clear();
        foreach (var item in items)
        {
            if (_items.Count >= _capacity) break;
            if (item.Bounds.IsEmpty || _items.Exists(e => e.Bounds == item.Bounds)) continue;
            _items.Add(item);
        }
    }

    /// <summary>
    /// 把画面补挂到已经记下的那一条上。
    ///
    /// 记条目和存画面是分开的两步：PNG 编码是几百毫秒的事，压在确认截图那一下上，
    /// 用户会感到「截完之后卡了一顿」。所以先把选区记上，图在后台编码完再回来认领。
    /// 返回 false 表示那一条已经被后来的挤掉了 —— 调用方据此把白存的文件删掉。
    /// </summary>
    public bool Attach(PixelRect bounds, string image)
    {
        int i = _items.FindIndex(e => e.Bounds == bounds && e.Image is null);
        if (i < 0) return false;

        _items[i] = _items[i] with { Image = image };
        Changed?.Invoke();
        return true;
    }

    private bool Trim()
    {
        if (_items.Count <= _capacity) return false;

        _items.RemoveRange(_capacity, _items.Count - _capacity);
        return true;
    }
}
