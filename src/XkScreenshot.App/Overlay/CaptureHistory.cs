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

    /// <summary>至少留一条。回溯这个功能是一直开着的，没有「一条都不记」这一档。</summary>
    public const int MinCapacity = 1;

    /// <summary>上限只是防呆。回溯是一格一格按过去的，几百条根本翻不到底。</summary>
    public const int MaxCapacity = 200;

    private readonly List<HistoryEntry> _items = [];
    private int _capacity = DefaultCapacity;

    /// <summary>内容变了。落盘的时机就看它。</summary>
    public event Action? Changed;

    /// <summary>缓存多少条。</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Clamp(value, MinCapacity, MaxCapacity);
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

    /// <summary>
    /// 记一次截图，返回刚记下的那一条（选区为空时返回 null）。
    ///
    /// 不按选区去重：每一次截图都是一个独立的时刻。「同一块地方内容变了、再截一次」
    /// 正是这个功能存在的理由，把位置相同的旧条目顶掉，顶掉的恰恰是用户最想翻回去的那一张。
    /// （只记矩形的那一版是去重的 —— 那时候位置相同确实就是同一条信息，
    /// 加上画面之后这个前提就不成立了。）
    /// </summary>
    public HistoryEntry? Record(PixelRect bounds, PixelRect desktop, string? image)
    {
        if (bounds.IsEmpty) return null;

        var entry = new HistoryEntry(bounds, desktop, image);
        _items.Insert(0, entry);
        // 裁掉的是末尾那几条，刚插进来的这一条排在最前，容量再小也轮不到它
        Trim();
        Changed?.Invoke();
        return entry;
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
            if (item.Bounds.IsEmpty) continue;
            _items.Add(item);
        }
    }

    /// <summary>
    /// 把画面补挂到已经记下的那一条上。
    ///
    /// 记条目和存画面是分开的两步：PNG 编码是几百毫秒的事，压在确认截图那一下上，
    /// 用户会感到「截完之后卡了一顿」。所以先把选区记上，图在后台编码完再回来认领。
    /// 返回 false 表示那一条已经被后来的挤掉了 —— 调用方据此把白存的文件删掉。
    ///
    /// 认的是条目本身而不是选区：同一块区域可以有好几条（不同时刻各截了一次），
    /// 按选区找会认到别人头上去。
    /// </summary>
    public bool Attach(HistoryEntry entry, string image)
    {
        int i = _items.FindIndex(e => ReferenceEquals(e, entry));
        if (i < 0) return false;

        _items[i] = entry with { Image = image };
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 全清掉。本来就是空的就什么也不做 —— 那一下没有任何变化，不值得让人跟着落一次盘。
    /// </summary>
    public void Clear()
    {
        if (_items.Count == 0) return;

        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// 画面文件改了名，把条目上的指向跟着换过来（换成 null = 那张图没了，条目退回「只有框」）。
    ///
    /// 不触发 <see cref="Changed"/>：调用它的人正走在落盘的路上，指向马上就会被写进索引，
    /// 再报一次变化只会让落盘自己套自己。
    /// </summary>
    public void Rename(IReadOnlyDictionary<string, string?> renamed)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Image is { } id && renamed.TryGetValue(id, out string? id2))
                _items[i] = _items[i] with { Image = id2 };
    }

    private bool Trim()
    {
        if (_items.Count <= _capacity) return false;

        _items.RemoveRange(_capacity, _items.Count - _capacity);
        return true;
    }
}
