using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using XkScreenshot.Core.Geometry;

namespace XkScreenshot.Pin;

/// <summary>
/// 管理所有已打开的贴图窗口。
/// 贴图是长期存在的对象，用户可能钉几十张然后忘掉，
/// 所以必须有一个统一入口能一次性收拾干净。
/// </summary>
public sealed class PinManager
{
    private readonly List<PinWindow> _pins = [];

    public int Count => _pins.Count;

    /// <summary>贴图请求复制到剪贴板。</summary>
    public event Action<BitmapSource>? CopyRequested;
    /// <summary>贴图请求另存为文件。</summary>
    public event Action<BitmapSource>? SaveRequested;

    /// <summary>
    /// 钉一张图。origin 是它在虚拟屏幕上的原始位置 —— 贴图正好出现在内容原本所在的地方，
    /// 视觉上就像画面被「冻」住了，而不是凭空弹出一个窗口。
    /// </summary>
    public PinWindow Create(BitmapSource image, PixelRect origin)
    {
        var pin = new PinWindow(image, origin);
        pin.CopyRequested += img => CopyRequested?.Invoke(img);
        pin.SaveRequested += img => SaveRequested?.Invoke(img);
        pin.Closed += (_, _) => _pins.Remove(pin);

        _pins.Add(pin);
        pin.Show();
        pin.Activate();
        return pin;
    }

    public void CloseAll()
    {
        // 复制一份再遍历：Closed 事件会回来改 _pins
        foreach (var pin in _pins.ToArray())
            pin.Close();
        _pins.Clear();
    }
}
