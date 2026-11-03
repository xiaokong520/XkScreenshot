using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Native;

namespace XkScreenshot.Scroll;

/// <summary>
/// 自动模式下替用户滚。
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
    /// <summary>一格滚轮的量。<see cref="Wheel"/> 的单位就是它的 1/120。</summary>
    public const int UnitsPerNotch = NativeMethods.WHEEL_DELTA;

    /// <summary>
    /// 往下滚 <paramref name="units"/>（单位见 <see cref="UnitsPerNotch"/>）。
    ///
    /// 允许发不满一格：连续慢速滚动全靠这一点。精确式触控板就是这么干的 —— 一次滑动拆成
    /// 几十个小量喂给系统，浏览器、Electron、UWP 都按比例滚，看起来就是匀速滑。
    /// 只认整格的老式控件会把零头攒起来，攒够一格滚一次，于是变成「三行一跳」；
    /// 速度和方向仍然是对的，只是没那么顺。
    /// </summary>
    public static void Wheel(int units)
    {
        if (units <= 0) return;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                // 正数是往上（远离用户），往下要取负；uint 字段按补码塞进去
                mouseData = unchecked((uint)-units),
                dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
            },
        };

        NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public static void MoveCursor(PixelPoint point)
        => NativeMethods.SetCursorPos(point.X, point.Y);

    /// <summary>
    /// 有没有鼠标键按着（左、右、中、两个侧键）。
    ///
    /// 自动模式用它判断「用户想自己来」。**不能拿光标位置判断**：刚框完选区手还在鼠标上，
    /// 人一边看一边微调位置，指针都会动 —— 那不是要接手，只是手没离开。
    /// 按键没有这种含糊：按下去一定是在操作。而程序自己只发滚轮，从不发按键。
    /// </summary>
    public static bool AnyButtonDown()
    {
        ReadOnlySpan<int> buttons = [NativeMethods.VK_LBUTTON, NativeMethods.VK_RBUTTON,
            NativeMethods.VK_MBUTTON, NativeMethods.VK_XBUTTON1, NativeMethods.VK_XBUTTON2];

        // 五个键都要读一遍，不能提前 return：这一读同时把「上次之后按过没有」那一位清掉，
        // 只读到一半就返回的话，剩下那几个键的陈年旧账会一直留着（见引擎 EnterAuto 的预读）
        bool down = false;
        foreach (int button in buttons)
        {
            // 最低位是「上次调用之后按过」，高位是「此刻按着」，两个都算
            if ((NativeMethods.GetAsyncKeyState(button) & 0x8001) != 0) down = true;
        }
        return down;
    }

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
