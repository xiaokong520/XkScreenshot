using System;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Core.Llm;

namespace XkScreenshot.Privacy;

public enum PrivacyStage
{
    Recognizing,
    Locating,
    OnlineNames,
}

public enum PrivacyKind
{
    Email,
    Phone,
    IdCard,
    BankCard,
    IpAddress,
    /// <summary>API Key、访问令牌、JWT 这类一泄露就能直接拿去用的串。</summary>
    Secret,
    /// <summary>「省市区 + 街道门牌」这种能落到一栋楼的邮寄地址。</summary>
    Address,
    Name,
    Avatar,
}

/// <summary>一块该糊掉的区域。坐标是选区局部物理像素，与标注坐标系一致。</summary>
public readonly record struct PrivacyRegion(PixelRect Bounds, PrivacyKind Kind)
{
    // 由原始像素确认过字界的文字不再横向外扩；纵向仍保留抗锯齿余量。
    internal int HorizontalPadding { get; init; } = 2;
}

/// <summary>
/// 选区内的原始画面：**非预乘** BGRA。
/// 头像判定要比较颜色，预乘数据在半透明边缘上会偏暗，跟背景色对不上。
/// </summary>
public sealed record PixelBuffer(byte[] Bgra, int Width, int Height, int Stride)
{
    public int IndexOf(int x, int y) => y * Stride + x * 4;
}

/// <summary>在线名字识别要用的连接。只在用户明确打开时才会有。</summary>
public sealed record LlmAccess(LlmApiClient Client, LlmApiConfig Config);

/// <summary>这一次要找哪几类。每一项都能在设置里单独关掉。</summary>
public sealed record PrivacyOptions(
    bool Email = true,
    bool Phone = true,
    bool IdCard = true,
    bool BankCard = true,
    bool IpAddress = false,
    bool Secret = true,
    bool Address = true,
    bool Avatar = true,
    bool Name = true,
    LlmAccess? Llm = null)
{
    public static readonly PrivacyOptions Standard = new();

    public bool Wants(PrivacyKind kind) => kind switch
    {
        PrivacyKind.Email => Email,
        PrivacyKind.Phone => Phone,
        PrivacyKind.IdCard => IdCard,
        PrivacyKind.BankCard => BankCard,
        PrivacyKind.IpAddress => IpAddress,
        PrivacyKind.Secret => Secret,
        PrivacyKind.Address => Address,
        PrivacyKind.Name => Name,
        PrivacyKind.Avatar => Avatar,
        _ => false,
    };

    /// <summary>要不要跑 OCR。只要头像的话也得跑 —— 头像是靠旁边那行字定位的。</summary>
    public bool NeedsText => Email || Phone || IdCard || BankCard || IpAddress || Secret || Address || Name || Avatar;
}

/// <summary>一次检测的结果，外加一句需要让用户知道的话（比如在线识别失败退回了本地）。</summary>
public sealed record PrivacyReport(IReadOnlyList<PrivacyRegion> Regions, string? Notice);
