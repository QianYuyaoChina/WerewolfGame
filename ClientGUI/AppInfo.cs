using System.Reflection;

namespace ClientGUI;

/// <summary>
/// 应用元信息（名称、版本、技术栈），集中一处供「关于 / 更新日志」窗口复用，
/// 避免版本号散落在各个界面里。
/// </summary>
public static class AppInfo
{
    public const string ProductName = "狼人杀 · 客户端";
    public const string ShortName = "狼人杀客户端";

    public const string Description =
        "基于 WPF 的狼人杀桌面客户端：登录注册、房间大厅、夜晚行动、白天讨论与投票、猎人开枪，全流程由服务端状态驱动。";

    public const string SolutionName = "Werewolf.Server";

    /// <summary>版本号取自程序集信息（csproj 的 &lt;Version&gt; / &lt;InformationalVersion&gt;）。</summary>
    public static string Version { get; } = ResolveVersion();

    public static string Framework => "net8.0-windows · WPF";

    /// <summary>技术栈标签，供关于窗口以“徽章”形式展示。</summary>
    public static IReadOnlyList<string> TechStack { get; } = new[]
    {
        "WPF",
        ".NET 8",
        "MVVM",
        "SignalR",
        "快速幂 Diffie-Hellman",
        "PSK 身份认证 (HMAC)",
        "AES-GCM (HKDF)",
        "规则可配置化",
        "心跳与自动重连",
        "多房间与邀请码",
        "多游戏可插拔",
        "服务端权威校验",
        "共享核心库"
    };

    private static string ResolveVersion()
    {
        var asm = typeof(AppInfo).Assembly;

        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SDK 会把源码版本以「1.3.0+<commit>」的形式附加进来，界面上只保留版本号。
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
