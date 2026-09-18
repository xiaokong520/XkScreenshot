using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Monitors;

namespace XkScreenshot.Pin;

/// <summary>
/// 管理所有已打开的贴图窗口。
/// 贴图是长期存在的对象，用户可能钉几十张然后忘掉，
/// 所以必须有一个统一入口能一次性收拾干净。
///
/// 它还管这些贴图的**存档**：位置、倍率、角度、透明度和画面一起落在程序目录下的 pins\ 里，
/// 下次打开程序按原样摆回来（见 PinStore）。之所以归它管而不是交给 App：
/// 「屏幕上有哪些贴图」这件事只有它知道，挪一张、关一张都要在一处收口，
/// 散出去的话迟早有哪条路忘了写。
/// </summary>
public sealed class PinManager
{
    static PinManager()
    {
        // WinForms 进程只初始化这一次。EnableVisualStyles 给 ContextMenuStrip 等控件
        // 一套跟系统一致的主题，没它菜单是 9x 年代的灰色。
        // 得在第一个控件句柄创建前调用，所以放静态构造里。
        Application.EnableVisualStyles();
    }

    private readonly List<PinForm> _pins = [];

    /// <summary>要不要把贴图存进存档。设置里「重启后恢复贴图」那一项，默认关。</summary>
    private bool _persist;

    public int Count => _pins.Count;

    /// <summary>贴图请求复制到剪贴板。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>贴图请求另存为文件。</summary>
    public event Action<BitmapSource>? SaveRequested;

    /// <summary>
    /// 打开或关掉「重启后恢复贴图」。
    ///
    /// 关掉时顺手把存档收干净，而且**不看这次是不是真的从「开」变过来的**：存档在而设置关着
    /// 是一种自相矛盾的状态（手改配置文件、降级、崩溃都可能留下它），留着的话，用户哪天
    /// 再打开这一项，冒出来的是一批他早就不记得、当初也没打算留的图。
    ///
    /// 只管开关，不负责把眼前这批立刻存上 —— 那个时机在 App.ApplySettings（它会紧跟一句 Save），
    /// 摆在这儿的话，启动那条路上会赶在恢复之前先照着「一张贴图也没有」把目录清一遍。
    /// </summary>
    public void SetPersistence(bool on)
    {
        _persist = on;
        if (!on) Forget();
    }

    /// <summary>
    /// 钉一张图。origin 是它在虚拟屏幕上的原始位置 —— 贴图正好出现在内容原本所在的地方，
    /// 视觉上就像画面被「冻」住了，而不是凭空弹出一个窗口。
    /// </summary>
    public PinForm Create(BitmapSource image, PixelRect origin)
    {
        var pin = Attach(new PinForm(image, origin));

        // 贴出来的这一刻就落一次盘：用户完全可能贴完立刻去点重启，
        // 而「等下一次拖动再写」在那时候什么都留不下
        Save();
        return pin;
    }

    /// <summary>
    /// 把上次贴着的那批图重新摆出来。画面读不出来的条目直接跳过 ——
    /// 一张没有画面的贴图没有任何意义（见 PinStore.Load）。
    /// </summary>
    public void Restore(IReadOnlyList<PinSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;

        var monitors = MonitorEnumerator.Enumerate();

        foreach (var snapshot in snapshots)
        {
            if (PinStore.LoadImage(snapshot.Image) is not { } image) continue;

            var pin = new PinForm(image, snapshot.X, snapshot.Y,
                snapshot.Scale, snapshot.Angle, snapshot.Opacity, snapshot.TopMost);

            // 存档里的坐标来自上一次的显示器布局，那块屏今天可能已经拔了 ——
            // 先挪回屏幕里再显示，不然贴图在、谁也看不见它
            pin.PullIntoDesktop(monitors);
            pin.StoreId = snapshot.Image; // 画面已经在盘上了，别再编码一遍

            Attach(pin);
        }

        // 全部摆回来之后才落一次盘。中途写的话，清理会把还没轮到的那几张画面当孤儿删掉 ——
        // 索引此刻只认已经恢复的那些，而目录里摆着全部
        Save();
    }

    /// <summary>
    /// 把当前这批贴图的全量快照写进存档。挪一下、缩一格、转一度都会走到这儿。
    ///
    /// 攒到退出时再写是不行的：进程被任务管理器结束、或者跟着关机一起没掉，攒着的那一批就全丢了，
    /// 而「重启之后贴图空了」恰恰是这个功能最不能出的岔子。索引只有一两 KB，
    /// 写它的代价可以忽略（画面文件不重写，见 PinStore）。
    /// </summary>
    public void Save()
    {
        if (!_persist) return;

        var rows = new List<PinSnapshot>(_pins.Count);
        foreach (var pin in _pins)
        {
            // 新贴图在这一步领号、把画面甩到后台去编码
            if (pin.StoreId is null) Store(pin);

            // 号领到了就先记进索引，不等画面落盘 —— 否则「贴完立刻重启」那一小段里
            // 索引是空的，而用户看到的贴图明明就在屏幕上
            if (pin.StoreId is { } id) rows.Add(pin.Snapshot(id));
        }

        PinStore.SaveIndex(rows);
        PinStore.PruneImages(rows.Select(r => r.Image));
    }

    public void CloseAll()
    {
        // 复制一份再遍历：FormClosed 事件会回来改 _pins
        foreach (var pin in _pins.ToArray())
            pin.Close();

        // 上面每关一张都会走一次 Save（见 Attach），这里只剩兜底
        Save();
    }

    /// <summary>
    /// 把一个新贴图挂上事件、加进名册、显示出来。落盘不在这儿 ——
    /// 两个调用方各有一份时机（Create 要立刻写、Restore 要等全部摆完，见各自的注释）。
    /// </summary>
    private PinForm Attach(PinForm pin)
    {
        pin.CopyRequested += img => CopyRequested?.Invoke(img);
        pin.SaveRequested += img => SaveRequested?.Invoke(img);

        // 贴图动了就重写一遍索引。攒着不写的话，用户拖完位置就去重启，
        // 回来的还是那张搁在原来地方的图
        pin.Changed += Save;

        pin.FormClosed += (_, _) =>
        {
            _pins.Remove(pin);
            // 关掉之后这一张的存档就没人认领了：索引里去掉，
            // 画面文件由这一轮 Save 的清理顺手收走
            Save();
        };

        _pins.Add(pin);
        pin.Show();
        pin.Activate();
        return pin;
    }

    /// <summary>
    /// 给一张还没入档的贴图领个号，把画面甩到后台线程去编码。
    ///
    /// 后台是必须的：一张整屏 PNG 要一两百毫秒，压在「按下热键就贴出来」那一下上，
    /// 用户看到的是贴图迟迟不出来。号当场占住，所以索引立刻就能引用它。
    /// </summary>
    private static void Store(PinForm pin)
    {
        string id = PinStore.ReserveId();
        pin.StoreId = id;

        // 贴图在构造时就把源图 Freeze 过了，可以给别的线程读
        var image = pin.Source;
        _ = Task.Run(() => PinStore.WriteImage(id, image));
    }

    /// <summary>
    /// 把存档整个丢掉：每一张现存的贴图都忘了自己存过，存档文件也清光。
    ///
    /// 「忘了自己存过」这一半是必须的 —— 光清文件的话，贴图的 StoreId 还指着那些已经不存在的
    /// 画面，下一轮 Save 会以为已经存过了，索引里就写着一条指向空文件的记录。
    /// </summary>
    private void Forget()
    {
        foreach (var pin in _pins) pin.StoreId = null;
        PinStore.Clear();
    }
}
