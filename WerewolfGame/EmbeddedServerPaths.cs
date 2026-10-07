namespace Werewolf.Server;

/// <summary>
/// 内置服务器的**工作目录**解析：配置、账号、词库都落在这里，
/// 不污染客户端程序根目录（避免与客户端自身文件混在一起、避免污染安装包目录）。
///
/// 策略（PRD Q3 / E-13）：
/// <list type="number">
///   <item>首选：客户端程序目录下的 <c>embedded-server\</c>（用户最容易找到并手工改配置 / 词库）；</item>
///   <item>回退：<c>%LOCALAPPDATA%\Werewolf\embedded-server\</c>（程序目录只读如 Program Files 时）；</item>
///   <item>两者都不可用 → 解析失败，由宿主给出人话「无法创建服务器目录」。</item>
/// </list>
/// </summary>
public static class EmbeddedServerPaths
{
    /// <summary>工作目录名（放在基目录下）。</summary>
    public const string DirectoryName = "embedded-server";

    /// <summary>回退基目录的厂商 / 产品名。</summary>
    public const string FallbackCompany = "Werewolf";

    /// <summary>写探针时用的临时文件名。</summary>
    private const string ProbeFileName = ".write-probe.tmp";

    /// <summary>首选基目录：客户端程序目录（<see cref="AppContext.BaseDirectory"/>，即 exe 所在目录）。</summary>
    public static string PreferredBase() => AppContext.BaseDirectory;

    /// <summary>回退基目录：<c>%LOCALAPPDATA%\Werewolf</c>（程序目录只读时使用）。</summary>
    public static string FallbackBase() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FallbackCompany);

    /// <summary>首选工作目录（纯函数，不创建任何东西）。</summary>
    public static string PreferredDirectory() => Path.Combine(PreferredBase(), DirectoryName);

    /// <summary>回退工作目录（纯函数，不创建任何东西）。</summary>
    public static string FallbackDirectory() => Path.Combine(FallbackBase(), DirectoryName);

    /// <summary>
    /// 纯函数：目录是否可写（建目录 + 写探针文件 + 删探针）。
    /// 抽成纯函数是为了契约与单测：它只依赖文件系统，不依赖任何宿主状态。
    /// </summary>
    /// <param name="directory">待判定的目录（不存在时会尝试创建）。</param>
    public static bool IsWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ProbeFileName);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            // 只读目录 / 权限不足 / 非法路径：一律视为不可写。
            return false;
        }
    }

    /// <summary>
    /// 解析工作目录：首选 → 回退 → 失败。
    /// 本方法**不抛异常**，失败信息为人话，交由宿主（<see cref="EmbeddedServerHost"/>）呈现给用户。
    /// </summary>
    /// <param name="directory">成功时为实际使用的目录；失败时为空字符串。</param>
    /// <param name="usedFallback">是否因首选目录不可写而改用回退目录。</param>
    /// <param name="error">失败时的人话原因；成功时为空字符串。</param>
    public static bool TryResolve(out string directory, out bool usedFallback, out string error)
    {
        directory = "";
        usedFallback = false;
        error = "";

        var preferred = PreferredDirectory();
        if (IsWritable(preferred))
        {
            directory = preferred;
            return true;
        }

        var fallback = FallbackDirectory();
        if (IsWritable(fallback))
        {
            directory = fallback;
            usedFallback = true;
            return true;
        }

        error = $"无法创建服务器目录（已尝试 {preferred} 与 {fallback}）。" +
                "请确认程序所在磁盘可写，或把客户端放到一个有写入权限的文件夹里再试。";
        return false;
    }

    /// <summary>
    /// 在资源管理器 / 文件管理器里打开目录（仅 Windows 有 ShellExecute，其它平台尽力而为）。
    /// 返回是否发起了打开请求；失败时调用方应自行给出提示。
    /// </summary>
    public static bool TryOpen(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
                return true;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { directory },
                UseShellExecute = false
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
