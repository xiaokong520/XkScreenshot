using System.Windows;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 给输入框挂一句占位文字：框里空着的时候用浅色显出来，一敲字就让开。
///
/// 用在「留空则用某个默认值」的那几个框上 —— 光写一句「留空则用系统「图片」文件夹」
/// 是不够的，用户想知道的是那个文件夹到底在哪。占位文字正好能把当前实际生效的路径
/// 摆出来，同时不动框里的值：填进去就变成显式配置了，往后系统的图片文件夹搬了家，
/// 存图还留在老地方。
///
/// 做成附加属性而不是借 Tag：Tag 是个谁都能用的通用口袋，模板一旦认它，
/// 哪天有人拿 Tag 存别的东西，框里就会莫名多出一行字。
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(string.Empty));

    public static void SetText(DependencyObject element, string value)
        => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element)
        => (string)element.GetValue(TextProperty);
}
