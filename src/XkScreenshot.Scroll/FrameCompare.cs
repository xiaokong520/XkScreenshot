namespace XkScreenshot.Scroll;

/// <summary>两帧之间「有没有变」的判定。</summary>
internal static class FrameCompare
{
    /// <summary>
    /// 允许多少个像素不一样还算「没变」。
    ///
    /// 不用严格相等：文本光标在闪、顶栏的时钟跳了一秒、某个按钮还在做悬停动画，
    /// 都会让两帧永远不相等 —— 那样「等画面停稳」就永远等不到，每一帧都要拖到超时，
    /// 长截图会慢得像卡住了。而真正滚了一下，改变的像素是以万计的，
    /// 这个额度拦不住它。
    /// </summary>
    public static int PixelBudget(int width, int height)
        => Math.Max(64, width * height / 2000);

    public static bool NearlyEqual(byte[] a, byte[] b, int stride, int height, int budget)
    {
        int length = stride * height;

        // 绝大多数「没变」的情况是逐字节完全相同，整块比一次就出结果，还能吃到向量化
        if (a.AsSpan(0, length).SequenceEqual(b.AsSpan(0, length))) return true;

        int diff = 0;
        for (int y = 0; y < height; y++)
        {
            var ra = a.AsSpan(y * stride, stride);
            var rb = b.AsSpan(y * stride, stride);
            if (ra.SequenceEqual(rb)) continue;

            for (int x = 0; x < stride; x += 4)
            {
                if (ra[x] == rb[x] && ra[x + 1] == rb[x + 1] && ra[x + 2] == rb[x + 2]) continue;
                if (++diff > budget) return false;
            }
        }
        return true;
    }
}
