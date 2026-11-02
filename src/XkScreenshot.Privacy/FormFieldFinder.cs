using System;
using System.Collections.Generic;
using XkScreenshot.Core.Geometry;
using XkScreenshot.Ocr;

namespace XkScreenshot.Privacy;

/// <summary>
/// 表单布局里的「标签 + 值」两列：左边一列字段名，右边一列内容。
///
/// 个人资料、收货地址这类页面会把地址拆成「街道 / 区县 / 城市 / 州 / 邮编」几行，
/// 每行只剩一个片段，`TextPatterns` 那条「省市区 + 门牌」的整段规则匹配不到。
/// 这里改成认左边的字段名，把它右边同一行的那段文字整块糊掉。
///
/// 只认地址相关的字段名 —— 同一个表单里的「性别」「Title」不该被地址开关顺手带走。
/// </summary>
internal static class FormFieldFinder
{
    private static readonly HashSet<string> AddressLabels = new(StringComparer.Ordinal)
    {
        "地址", "详细地址", "收货地址", "街道", "区县", "城市", "州", "省", "省份", "地区",
        "邮编", "邮政编码", "门牌", "楼栋", "乡镇",
    };

    /// <summary>标签列只可能贴在左边；右半页冒出来的「城市」多半是正文。</summary>
    private const int LabelZonePercent = 35;

    public static List<PixelRect> FindAddressValues(IReadOnlyList<OcrLine> lines, int imageWidth)
    {
        var found = new List<PixelRect>();
        if (lines.Count == 0) return found;

        int leftLimit = imageWidth * LabelZonePercent / 100;

        foreach (var label in lines)
        {
            if (label.Bounds.X > leftLimit) continue;
            if (!AddressLabels.Contains(label.Text.Trim())) continue;

            OcrLine? best = null;
            int bestGap = int.MaxValue;

            foreach (var value in lines)
            {
                if (value.Bounds == label.Bounds) continue;
                if (value.Bounds.X <= label.Bounds.Right + 8) continue;
                // 同一行：纵向起点的差不到半行高
                if (Math.Abs(value.Bounds.Y - label.Bounds.Y) > label.Bounds.Height * 0.6) continue;

                int gap = value.Bounds.X - label.Bounds.Right;
                if (gap < bestGap) { bestGap = gap; best = value; }
            }

            if (best is { } v)
                found.Add(new PixelRect(v.Bounds.X, v.Bounds.Y, v.Bounds.Width, v.Bounds.Height));
        }

        return found;
    }
}
