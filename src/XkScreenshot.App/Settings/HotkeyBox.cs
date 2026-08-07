using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XkScreenshot.Core.Native;

namespace XkScreenshot.App.Settings;

/// <summary>
/// 录热键的输入框：获得焦点后按下什么组合键，它就是什么。
///
/// 不做成「下拉选修饰键 + 下拉选主键」是因为那样得让用户先把 F1 和 VK_70 对上号。
/// 直接按一下最快，代价是要把这个框里所有按键都拦下来，一个都不能漏给 WPF ——
/// 漏掉 Tab 会跳焦点、漏掉空格会当成点击、漏掉 Alt 会去激活菜单。
/// </summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>进入焦点那一刻的值。Esc 放弃录制时退回它。</summary>
    private HotkeySpec _committed = HotkeySpec.CaptureDefault;

    private HotkeySpec _value = HotkeySpec.CaptureDefault;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        // 居中和字重交给样式表，这里设了就把样式盖掉了（本地值优先级高于 Style Setter）
        ToolTip = "点这里，然后按下想用的组合键。Esc 放弃，Delete 恢复默认";
        Refresh();
    }

    public HotkeySpec Value
    {
        get => _value;
        set
        {
            _value = value;
            _committed = value;
            Refresh();
        }
    }

    private void Refresh() => Text = _value.ToString();

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _committed = _value;
        SelectAll();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        // 只按了修饰键就走开的话，框里还停着「Ctrl + …」那种半截提示
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        e.Handled = true;

        // Alt 组合键走 WM_SYSKEYDOWN，真正的键藏在 SystemKey 里
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 光秃秃的 Esc / Delete 当不了全局热键，拿来做这个框自己的操作正合适。
        // 带修饰键时不认：Ctrl+Delete 之类是完全合理的热键。
        if (Keyboard.Modifiers == ModifierKeys.None && !IsWinDown())
        {
            switch (key)
            {
                case Key.Escape:
                    _value = _committed;
                    Refresh();
                    return;

                case Key.Delete:
                case Key.Back:
                    _value = HotkeySpec.CaptureDefault;
                    Refresh();
                    return;
            }
        }

        // 修饰键自己不成键。先把已按住的那几个显示出来，用户才知道框在录了
        if (IsModifierKey(key))
        {
            Text = Describe(CurrentModifiers()) is { Length: > 0 } mods ? mods + " + …" : "…";
            return;
        }

        _value = new HotkeySpec(CurrentModifiers(), (uint)KeyInterop.VirtualKeyFromKey(key));
        Refresh();
    }

    /// <summary>
    /// 输入法激活时按键会以 ImeProcessed 的形式到达，SystemKey/Key 都拿不到真实按键。
    /// 拦下来当没发生，总比录进去一个 ImeProcessed 强。
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;

    private static HotkeyModifiers CurrentModifiers()
    {
        var mods = HotkeyModifiers.None;
        var wpf = Keyboard.Modifiers;

        if ((wpf & ModifierKeys.Control) != 0) mods |= HotkeyModifiers.Control;
        if ((wpf & ModifierKeys.Alt) != 0) mods |= HotkeyModifiers.Alt;
        if ((wpf & ModifierKeys.Shift) != 0) mods |= HotkeyModifiers.Shift;
        // WPF 的 ModifierKeys 里压根没有 Windows 键，只能直接问键盘状态
        if (IsWinDown()) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static bool IsWinDown()
        => Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System or Key.ImeProcessed or Key.None;

    private static string Describe(HotkeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        return string.Join(" + ", parts);
    }
}
