using System;
using System.Collections.Generic;
using System.Linq;
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

    private void PushUndo()
    {
        _undo.Add(_items.ToArray());
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
