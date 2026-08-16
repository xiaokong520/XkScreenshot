using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XkScreenshot.App;

/// <summary>
/// 查到的新版本。<see cref="AssetUrl"/> 为 null 表示没有能在这台机器上直接装的包
/// （便携版、或 release 里缺了对应资产），那就只能把发布页递给用户。
/// </summary>
public sealed record UpdateInfo(string Version, string PageUrl, string? AssetName, string? AssetUrl);

/// <summary>
/// 检查更新与自动升级，版本的事实来源是 GitHub Releases。
///
/// 「有没有新版」拿最新 release 的 tag 和程序集版本比；「怎么升」按这台机器的来路分两条：
/// 安装版（目录里有 Inno 写下的卸载器）下载同一口味的安装包静默升级；便携版是用户自己
/// 解压自己管的目录，程序不该伸手去覆盖，只把发布页指给他。
/// </summary>
public static class UpdateChecker
{
    /// <summary>发布页。检查不了或没有对上号的安装包时，人肉下载的落点。</summary>
    public const string ReleasesPage = "https://github.com/xiaokong520/XkScreenshot/releases/latest";

    // releases/latest 只返回最新的正式版，草稿和预发布天然被滤掉，不用自己挑
    private const string ApiUrl =
        "https://api.github.com/repos/xiaokong520/XkScreenshot/releases/latest";

    /// <summary>是安装出来的还是解压出来的：Inno 装出的目录里必带它写下的卸载器。</summary>
    public static bool IsInstalled
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// 随包带没带运行时。自包含发布把 coreclr 摆在程序目录里，框架依赖的用系统共享那份。
    /// 升级必须下同一种：给框架依赖的用户换上自包含的包，体积凭空多出一百多兆；反过来换，
    /// 没装运行时的机器升完直接起不来。
    /// </summary>
    private static bool IsSelfContained
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    /// <summary>问 GitHub 有没有更新。null = 已是最新（或最新 tag 压根不是个版本号）。</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        // GitHub 的 API 对不带 User-Agent 的请求直接 403
        request.Headers.UserAgent.ParseAdd("XkScreenshot/" + CurrentVersion());
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = doc.RootElement;

        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } remote || remote <= CurrentVersion()) return null;

        string page = release.TryGetProperty("html_url", out var url)
            ? url.GetString() ?? ReleasesPage
            : ReleasesPage;

        var (name, download) = IsInstalled ? PickSetupAsset(release) : (null, null);
        return new UpdateInfo(tag.TrimStart('v', 'V'), page, name, download);
    }

    /// <summary>
    /// 从 release 的资产里挑跟本机同一口味的安装包。
    ///
    /// 按文件名尾巴认而不拼完整名字 —— 中缀是版本号，自己拼一遍等于又造一处要同步的地方。
    /// 「-setup.exe」不会误配自包含那个包：它的结尾是「-setup-selfcontained.exe」，尾巴对不上。
    /// </summary>
    private static (string? Name, string? Url) PickSetupAsset(JsonElement release)
    {
        string suffix = IsSelfContained ? "-setup-selfcontained.exe" : "-setup.exe";

        if (!release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            if (asset.TryGetProperty("browser_download_url", out var u)
                && u.GetString() is { } download)
                return (name, download);
        }

        return (null, null);
    }

    /// <summary>程序集版本砍到三段 —— tag 上是三段式，段数对齐了才能比。</summary>
    private static Version CurrentVersion()
    {
        var v = typeof(UpdateChecker).Assembly.GetName().Version;
        return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>从「v1.2.3」「1.2.3-beta」里挖出数字部分。挖不出来就当没有更新，别拿垃圾去比。</summary>
    private static Version? ParseVersion(string tag)
    {
        string s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        return Version.TryParse(s, out var v)
            ? new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0))
            : null;
    }

    /// <summary>安装包落进临时目录下自己的小间。按文件名存，重复下载原地覆盖，不攒尸体。</summary>
    public static string StagingPath(string fileName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "XkScreenshot-Update");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// 派一个 PowerShell 在门外候着：等本进程退出、静默跑安装包、装完把程序重新拉起来。
    ///
    /// 只能托外人 —— 要换的正是当前跑着的这些文件，进程活着谁也动不了它们；而安装脚本里
    /// 设着 AppMutex，人不退干净安装器就会停下来发问，静默也照问。等退出认的是进程号而
    /// 不是拍脑袋的延时：慢盘、杀毒软件都能把退出拖长，赌时间迟早输一回。
    ///
    /// 这里只负责把人送出门，真正的退出由调用方来 —— 送不出去（PowerShell 被策略拦了）
    /// 会抛异常，那时候程序还好好的，报个错就行，犯不上白关一次。
    /// </summary>
    public static void InstallOnExit(string setupPath)
    {
        string app = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "XkScreenshot.exe");

        // /SILENT 只留一条进度，装完不弹「运行程序」那页 —— 重新拉起的事脚本自己做，
        // 这样用户取消安装时老版本也能回来
        string script = string.Join("\r\n",
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue",
            $"Start-Process -FilePath {Quote(setupPath)} -ArgumentList '/SILENT','/NORESTART' -Wait",
            $"Start-Process -FilePath {Quote(app)}");

        string ps1 = StagingPath("install.ps1");
        // 带 BOM 写：Windows PowerShell 认不出没 BOM 的 UTF-8，会按 ANSI 读，
        // 临时目录里带中文的用户名一变乱码，脚本等的就是一条不存在的路
        File.WriteAllText(ps1, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var chaperone = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("没能启动 PowerShell，无法接手安装");
    }

    /// <summary>PowerShell 单引号字符串：里面的单引号写成两个，其余一概不转义。</summary>
    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}
