using System.Runtime.InteropServices;

namespace XkScreenshot.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct SIZE
{
    public int cx, cy;
}

/// <summary>SendInput 的鼠标事件。滚轮的格数放在 mouseData 里（一格 = WHEEL_DELTA）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

/// <summary>
/// SendInput 的输入项。原生结构里跟在 type 后面的是一个联合体（鼠标/键盘/硬件），
/// 这里只声明最大的那个成员（鼠标）—— 本项目只发滚轮，其余两种用不上，
/// 而联合体的整体大小由最大成员决定，所以布局和 cbSize 都是对的。
/// 仅限 x64：本解决方案 Platforms 就只有 x64。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public MOUSEINPUT mi;
}

/// <summary>UpdateLayeredWindow 用的混合参数。四个字节，顺序不能错。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEXW
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    // 32bpp 无调色板，但结构体需要占位
    public uint bmiColors0;
    public uint bmiColors1;
    public uint bmiColors2;
}

public enum MonitorDpiType
{
    Effective = 0,
    Angular = 1,
    Raw = 2,
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}
