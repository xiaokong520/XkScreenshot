using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Scroll;

/// <summary>
/// 自动模式下替用户滚那一下。
///
/// 用 <c>SendInput</c> 而不是给目标窗口发 <c>WM_MOUSEWHEEL</c>：后者绕开了真正的输入栈，
/// 遇上自己处理原始输入、或者把滚轮转发给内部控件的应用（浏览器、Electron、各种自绘列表）
/// 就没有反应，而这些恰恰是最需要长截图的那一批。SendInput 走的是系统输入队列，
/// 目标程序收到的和用户真的滚了一下完全一样。
///
/// 代价是滚轮跟着光标走，所以必须先把光标挪进目标区域 —— 这也是自动模式会跟用户抢鼠标的原因，
/// 引擎那边据此做了「用户一动鼠标就交还控制权」的让位，见 <see cref="ScrollCaptureEngine"/>。
/// </summary>
public static class ScrollDriver
{
    /// <summary>往下滚 <paramref name="notches"/> 格。滚轮事件落在光标当前所在的窗口上。</summary>
    public static void WheelDown(int notches)
    {
        if (notches <= 0) return;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                // 正数是往上（远离用户），往下要取负；uint 字段按补码塞进去
                mouseData = unchecked((uint)(-notches * NativeMethods.WHEEL_DELTA)),
                dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
            },
        };

        NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public static void MoveCursor(PixelPoint point)
        => NativeMethods.SetCursorPos(point.X, point.Y);

    /// <summary>
    /// 滚轮该落在哪儿：区域正中。
    ///
    /// 挑正中不是随便定的 —— 边缘容易压在滚动条、分隔条或者相邻面板上，
    /// 那一下滚的就不是用户想要的那块内容了。正中当然也可能压到某个按钮上让它变色，
    /// 但那点像素差落在匹配的容差里，而滚错了对象是整个功能失效。
    /// </summary>
    public static PixelPoint AnchorFor(PixelRect region)
        => new(region.X + region.Width / 2, region.Y + region.Height / 2);
}
