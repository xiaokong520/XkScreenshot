using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace XkScreenshot.Annotate;

/// <summary>
/// 一次截图的全部标注，附带撤销/重做。
///
/// 撤销采用整份列表快照而不是命令模式。标注数量最多几十个、每个只是几十字节的对象引用，
/// 快照的内存与拷贝代价可以忽略；换来的是绝对不会出现「撤销之后状态对不上」这类
/// 命令模式最容易写错、又最难查的 bug。标注本身是不可变的，快照之间安全共享。
/// </summary>
public sealed class AnnotationDocument
{
    private const int MaxHistory = 100;

    private readonly List<Annotation> _items = [];
    private readonly List<Annotation[]> _undo = [];
    private readonly List<Annotation[]> _redo = [];

    public IReadOnlyList<Annotation> Items => _items;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsEmpty => _items.Count == 0;

    public event Action? Changed;

    public void Add(Annotation annotation)
    {
        PushUndo();
        _items.Add(annotation);
        Changed?.Invoke();
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return false;

        PushUndo();
        _items.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 命中光标的最上面那个标注，-1 表示没命中。
    /// 倒着找：后画的压在先画的上面，重叠时理应先选到看得见的那个。
    /// </summary>
    public int HitTest(Point p, double tolerance)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].HitTest(p, tolerance)) return i;

        return -1;
    }

    /// <summary>
    /// 拖拽过程中的实时替换，不记撤销点。
    ///
    /// 一次拖拽会产生几百帧，每帧都记一次的话，用户想撤销一次移动得按上几百下 Ctrl+Z。
    /// 撤销点由拖拽结束时的 <see cref="CommitEdit"/> 补记。
    /// </summary>
    public void ReplaceLive(int index, Annotation next)
    {
        if (index < 0 || index >= _items.Count) return;
        if (ReferenceEquals(_items[index], next)) return;

        _items[index] = next;
        Changed?.Invoke();
    }

    /// <summary>
    /// 一次拖拽结束，用拖拽前的那个对象补记撤销点。
    /// 原地按一下没真的动过就什么都不记，免得历史里堆满空操作。
    /// </summary>
    public bool CommitEdit(int index, Annotation before)
    {
        if (index < 0 || index >= _items.Count) return false;
        if (ReferenceEquals(_items[index], before)) return false;

        var snapshot = _items.ToArray();
        snapshot[index] = before;
        PushSnapshot(snapshot);
        return true;
    }

    /// <summary>删除最后一个标注。工具条上的「删除」用它，语义比撤销更直白。</summary>
    public bool RemoveLast()
    {
        if (_items.Count == 0) return false;

        PushUndo();
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;

        PushUndo();
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// 彻底丢弃全部标注与历史。
    ///
    /// 重新框选时必须调用它。标注坐标是选区局部的，换了选区之后旧标注会按新的原点
    /// 重新画出来，画到完全不相干的位置上。这里连撤销历史一并清掉 —— 留着的话，
    /// 用户撤销一步就会把属于上一个选区的标注捞回来，状态直接对不上。
    /// </summary>
    public void Reset()
    {
        bool had = _items.Count > 0 || _undo.Count > 0 || _redo.Count > 0;

        _items.Clear();
        _undo.Clear();
        _redo.Clear();

        if (had) Changed?.Invoke();
    }

    /// <summary>
    /// 整体平移全部标注，连同撤销历史一起。
    ///
    /// 选区被移动时调用：坐标是选区局部的，选区往右挪一格，标注就得往左挪一格，
    /// 才能仍然盖在原来那块画面上。历史快照也必须一起平移 —— 否则撤销一步之后，
    /// 标注会跳回按旧选区原点算出来的位置。
    /// 这是纯粹的坐标重基，不是一次编辑，所以不产生新的撤销点。
    /// </summary>
    public void Rebase(double dx, double dy)
    {
        if (dx == 0 && dy == 0) return;
        if (_items.Count == 0 && _undo.Count == 0 && _redo.Count == 0) return;

        for (int i = 0; i < _items.Count; i++) _items[i] = _items[i].Translate(dx, dy);
        for (int i = 0; i < _undo.Count; i++) _undo[i] = ShiftSnapshot(_undo[i]);
        for (int i = 0; i < _redo.Count; i++) _redo[i] = ShiftSnapshot(_redo[i]);

        Changed?.Invoke();

        Annotation[] ShiftSnapshot(Annotation[] snapshot)
        {
            var moved = new Annotation[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
                moved[i] = snapshot[i].Translate(dx, dy);
            return moved;
        }
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        _redo.Add(_items.ToArray());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        _undo.Add(_items.ToArray());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke();
        return true;
    }

    private void PushUndo() => PushSnapshot(_items.ToArray());

    private void PushSnapshot(Annotation[] snapshot)
    {
        _undo.Add(snapshot);
        // 新的操作让原有的重做分支失效，这是所有编辑器的共同约定
        _redo.Clear();

        if (_undo.Count > MaxHistory)
            _undo.RemoveAt(0);
    }

    private void Restore(Annotation[] snapshot)
    {
        _items.Clear();
        _items.AddRange(snapshot);
    }

    /// <summary>按添加顺序绘制全部标注 —— 后画的压在先画的上面。</summary>
    public void Draw(DrawingContext dc, IAnnotationContext context)
    {
        foreach (var item in _items)
            item.Draw(dc, context);
    }
}
