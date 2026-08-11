using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace XkScreenshot.App;

/// <summary>
/// 提权重启。
///
/// 为什么需要它：任务管理器、注册表编辑器这类以管理员权限运行的窗口在前台时，
/// 系统不会把 WM_HOTKEY 派发给权限比它低的进程 —— 热键明明注册成功了，
/// 按下去却什么也不会发生，而且没有任何错误可报。这是 UIPI（用户界面特权隔离），
/// 不是能在代码里绕过去的东西：换成 WH_KEYBOARD_LL 低级键盘钩子一样收不到。
/// 唯一的出路是让本程序也站到同一权限高度上。
///
/// 为什么不干脆在清单里一直提权：提权之后贴图窗口收不到普通程序拖来的文件
/// （同一道 UIPI，方向反过来），而拖放是天天要用的，对着提权窗口截图是偶尔为之。
/// 所以默认仍是 asInvoker，把提权做成一个按需切换的入口。
/// </summary>
public static class Elevation
{
    /// <summary>
    /// 重启时带给新实例的标记，告诉它「老实例正在退出，单实例名额马上就腾出来」。
    /// </summary>
    public const string RestartArgument = "--elevated-restart";

    /// <summary>ShellExecute 在用户点掉 UAC 时返回的错误码。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// 当前进程是不是提权的。
    ///
    /// 管理员账户平时拿的是过滤过的令牌，管理员组在里头是禁用状态，所以这个判断
    /// 问的是「此刻有没有管理员权限」，而不是「这个用户是不是管理员」—— 后者对
    /// 热键能不能收到毫无意义。
    /// </summary>
    public static bool IsElevated { get; } = CheckElevated();

    /// <summary>
    /// 以管理员身份把自己重新拉起来。返回 true 表示新实例已经启动，调用方应当立刻退出，
    /// 把单实例名额让出去。
    ///
    /// 返回 false 且 <paramref name="error"/> 为 null 是「用户在 UAC 上点了取消」——
    /// 他自己撤销的动作，不需要再被提示一次。
    /// </summary>
    public static bool Restart(out string? error)
    {
        string? path = Environment.ProcessPath;
        if (path is null)
        {
            error = "找不到程序自身的可执行文件路径，无法重启。";
            return false;
        }

        var info = new ProcessStartInfo(path)
        {
            // runas 只有走 ShellExecute 才有效：提权是外壳弹 UAC 之后代为启动的，
            // CreateProcess 这条路上没有任何环节能把令牌换掉
            UseShellExecute = true,
            Verb = "runas",
            Arguments = RestartArgument,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
        };

        try
        {
            Process.Start(info);
            error = null;
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = null;
            return false;
        }
        catch (Exception ex)
        {
            error = "以管理员身份重启失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 退回普通权限重新启动。
    ///
    /// 提权进程直接 Process.Start 出来的仍然是提权的 —— 子进程继承的是父进程那个令牌，
    /// 没有「降权启动」这种动词。所以托 explorer.exe 去开：它跑在普通用户权限上，
    /// 由它转手拉起来的才是普通权限的进程。代价是参数带不过去，也拿不到新进程的句柄，
    /// explorer 只管转交、不回话，所以调用方必须先把单实例名额腾出来再叫它。
    /// </summary>
    public static bool RestartUnelevated(out string? error)
    {
        string? path = Environment.ProcessPath;
        if (path is null)
        {
            error = "找不到程序自身的可执行文件路径，无法重启。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "以普通权限重启失败：" + ex.Message;
            return false;
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 查不出来就当没提权：这个值只用来决定要不要把「提权重启」这个入口摆出来，
            // 多摆一次的代价是用户白点一下，少摆一次是他根本找不到这个功能
            return false;
        }
    }
}
