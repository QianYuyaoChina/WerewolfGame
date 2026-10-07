using Werewolf.Client.Share;
using Werewolf.Server;

namespace Werewolf.Client;

/// <summary>启动方式：内置服务器 / 连接现有服务器。</summary>
public enum EmbeddedServerStartMode
{
    /// <summary>在本进程里拉起内置服务器并自动连接。</summary>
    Embedded,

    /// <summary>连接一个已有的（本机或远端的）服务器。</summary>
    External
}

/// <summary>
/// 启动选择的结果：把「内置服务器是否已拉起」与「要连哪个地址」一起交给主流程。
/// <see cref="Url"/> 为空表示「沿用原有交互，由用户自己输入地址 / 粘贴邀请码」。
/// </summary>
public sealed class EmbeddedServerStartResult
{
    /// <summary>本次的启动方式。</summary>
    public EmbeddedServerStartMode Mode { get; init; }

    /// <summary>内置服务器宿主（仅 <see cref="EmbeddedServerStartMode.Embedded"/> 且启动成功时非空）。</summary>
    public EmbeddedServerHost? Host { get; init; }

    /// <summary>内置服务器启动结果（含实际端口与人话失败原因）。</summary>
    public EmbeddedServerInfo? Info { get; init; }

    /// <summary>自动连接用的地址（含 <c>/gamehub</c>）；为空表示需要用户手输。</summary>
    public string Url { get; init; } = "";

    /// <summary>地址是否已确定（<see cref="Url"/> 非空时不必再提示用户输入）。</summary>
    public bool SkipAddressPrompt => Url.Length > 0;
}

/// <summary>
/// 控制台端的「内置服务器」入口与收尾：
/// ① 启动菜单（零参数可用，P0）；② 命令行参数 <c>--embedded [--port N]</c> / <c>--server &lt;url&gt;</c>（P1-3）；
/// ③ 与 GUI **同一套**收尾语义：退出房间 → 断开自己的连接 → 停止内置服务器（预算 3s / 5s / 总 8s）。
/// </summary>
public static class EmbeddedServerConsole
{
    /// <summary>默认端口（PRD Q1 裁定：固定 5000）。</summary>
    public const int DefaultPort = 5000;

    /// <summary>退出房间的等待预算。</summary>
    public static readonly TimeSpan LeaveRoomBudget = TimeSpan.FromSeconds(3);

    /// <summary>停止内置服务器的等待预算。</summary>
    public static readonly TimeSpan StopServerBudget = TimeSpan.FromSeconds(5);

    /// <summary>整个收尾的总预算（超时即强制结束，绝不无限等待）。</summary>
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(8);

    // ==================== 启动入口 ====================

    /// <summary>
    /// 决定本次「怎么连服务器」：命令行参数优先，否则显示二选一菜单。
    /// 选内置时**顺带把服务器拉起来**（失败会打印人话原因并回退为「连接现有服务器」）。
    /// </summary>
    public static async Task<EmbeddedServerStartResult> ShowStartMenuAsync(string[] args)
    {
        if (TryParseEmbeddedArgs(args, out var port, out var argError))
        {
            var started = await StartEmbeddedAsync(port);
            if (started.Info is { Ok: true })
                return new EmbeddedServerStartResult
                {
                    Mode = EmbeddedServerStartMode.Embedded,
                    Host = started.Host,
                    Info = started.Info,
                    Url = started.Info.ConnectUrl
                };

            // 启动失败：人话提示后退化为「自己填地址连别的服务器」，绝不静默退出。
            Print("❌ " + (started.Info?.ErrorMessage ?? "内置服务器启动失败。"));
            Print("   你可以改用「连接现有服务器」。");
            Console.WriteLine();
        }
        else if (argError.Length > 0)
        {
            Print("⚠️ " + argError);
            Console.WriteLine();
        }

        var serverUrl = TryParseServerArg(args);
        if (serverUrl != null)
            return new EmbeddedServerStartResult
            {
                Mode = EmbeddedServerStartMode.External,
                Url = serverUrl
            };

        return await ShowMenuAsync();
    }

    /// <summary>首屏二选一菜单（零参数可用）。</summary>
    private static async Task<EmbeddedServerStartResult> ShowMenuAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("请选择启动方式：");
            Console.WriteLine("  1) 创建内置服务器（在这台电脑上开一个服务器，自己和朋友都能连）");
            Console.WriteLine("  2) 连接现有服务器（连别人已经开好的服务器）");
            Console.Write("输入序号（回车默认 1）：");

            var pick = (Console.ReadLine() ?? "").Trim();
            if (pick.Length == 0) pick = "1";

            if (pick == "1")
            {
                Console.Write($"端口（回车默认 {DefaultPort}，被占用会自动换一个空闲端口）：");
                var raw = (Console.ReadLine() ?? "").Trim();
                var port = DefaultPort;
                if (raw.Length > 0 && (!int.TryParse(raw, out port) || port < 1024 || port > 65535))
                {
                    Print($"⚠️ 端口必须是 1024–65535 之间的数字（输入的是「{raw}」）。");
                    continue;
                }

                var started = await StartEmbeddedAsync(port);
                if (started.Info is { Ok: true })
                    return new EmbeddedServerStartResult
                    {
                        Mode = EmbeddedServerStartMode.Embedded,
                        Host = started.Host,
                        Info = started.Info,
                        Url = started.Info.ConnectUrl
                    };

                Print("❌ " + (started.Info?.ErrorMessage ?? "内置服务器启动失败。"));
                Print("   可以换个端口重试，或改用「连接现有服务器」。");
                continue;
            }

            if (pick == "2")
                return new EmbeddedServerStartResult { Mode = EmbeddedServerStartMode.External };

            Print("⚠️ 无效输入，请输入 1 或 2。");
        }
    }

    /// <summary>拉起内置服务器并打印运行信息（人话，不打异常堆栈）。</summary>
    private static async Task<(EmbeddedServerHost? Host, EmbeddedServerInfo? Info)> StartEmbeddedAsync(int port)
    {
        Print("正在启动内置服务器…");

        var host = new EmbeddedServerHost(new EmbeddedServerOptions
        {
            PreferredPort = port,
            AllowPortDrift = true
        });

        var info = await host.StartAsync();
        if (!info.Ok) return (null, info);

        Print($"✅ 内置服务器已启动：{info.LocalUrl}（本机）");
        if (!string.Equals(info.LanUrl, info.LocalUrl, StringComparison.Ordinal))
            Print($"🌐 局域网地址（发给朋友）：{info.LanUrl}");
        Print($"📁 服务器目录：{info.WorkDirectory}");
        foreach (var line in info.BootstrapMessages) Print("   " + line);
        Print("   提示：关掉本程序时内置服务器会一起关掉。");
        Console.WriteLine();

        return (host, info);
    }

    // ==================== 命令行参数 ====================

    /// <summary>是否出现 <c>--embedded [--port N]</c>。</summary>
    private static bool TryParseEmbeddedArgs(string[] args, out int port, out string error)
    {
        port = DefaultPort;
        error = "";

        var index = Array.FindIndex(args, a => string.Equals(a, "--embedded", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
        {
            if (parsed is < 1024 or > 65535)
            {
                error = $"端口必须是 1024–65535 之间的数字（给的是 {parsed}），已改用默认端口 {DefaultPort}。";
            }
            else
            {
                port = parsed;
            }
        }
        else if (index + 1 < args.Length && string.Equals(args[index + 1], "--port", StringComparison.OrdinalIgnoreCase)
                 && index + 2 < args.Length)
        {
            if (int.TryParse(args[index + 2], out var explicitPort) && explicitPort is >= 1024 and <= 65535)
                port = explicitPort;
            else
                error = $"--port 后面需要跟 1024–65535 之间的数字（给的是「{args[index + 2]}」），已改用默认端口 {DefaultPort}。";
        }

        return true;
    }

    /// <summary><c>--server &lt;url&gt;</c>：跳过菜单直接连指定地址。</summary>
    private static string? TryParseServerArg(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;

        var url = args[index + 1].Trim();
        return url.Length == 0 ? null : url;
    }

    // ==================== 收尾 ====================

    /// <summary>
    /// 与 GUI 同一套收尾语义（PRD 流程 C / E-12）：
    /// ① 在房间里 → 走正规「退出房间」；② 断开自己的连接；③ 停止内置服务器。
    /// 每一步都有预算，总预算 8 秒；超时即放弃等待，绝不无限阻塞退出。
    /// 本方法**幂等**（Ctrl+C 连按只执行一次）。
    /// </summary>
    /// <param name="client">当前客户端会话（可能为 null）。</param>
    /// <param name="host">内置服务器宿主（未启动时为 null）。</param>
    public static async Task ShutdownAsync(GameClient? client, EmbeddedServerHost? host)
    {
        Console.WriteLine();

        // 三步各自有预算，外面再套一层 8 秒总预算：绝不无限等待（PRD E-9）。
        await WithBudget(RunStepsAsync(client, host), TotalBudget);
        Print("已退出。");
    }

    private static async Task RunStepsAsync(GameClient? client, EmbeddedServerHost? host)
    {
        // ① 在房间里 → 走与菜单「退出房间」完全相同的路径（控制台不询问是否保留房间）。
        if (client != null && client.IsJoined)
        {
            Print("正在退出房间…");
            await WithBudget(LeaveRoomQuietlyAsync(client), LeaveRoomBudget);
        }

        // ② 先摘掉自己这条长连接，服务端才能立刻排空、端口才能马上释放（US-4）。
        if (client != null)
            await WithBudget(client.DisposeAsync().AsTask(), LeaveRoomBudget);

        // ③ 停内置服务器。
        if (host != null)
        {
            Print("正在停止内置服务器…");
            await WithBudget(host.StopAsync(StopServerBudget), StopServerBudget);
        }
    }

    /// <summary>退出房间并吞掉异常（收尾阶段不该因为失败而中断后续步骤）。</summary>
    private static async Task LeaveRoomQuietlyAsync(GameClient client)
    {
        try
        {
            await client.LeaveRoomAsync(keepRoom: false).ConfigureAwait(false);
        }
        catch
        {
            // 收尾阶段：离开失败也继续往下走（断开连接 + 停服务器）。
        }
    }

    /// <summary>等待一个任务，超过预算即放弃（不重试、不抛异常）。</summary>
    private static async Task WithBudget(Task task, TimeSpan budget)
    {
        try
        {
            var delay = Task.Delay(budget);
            await Task.WhenAny(task, delay).ConfigureAwait(false);
        }
        catch
        {
            // 收尾阶段的异常一律不影响进程退出。
        }
    }

    private static void Print(string message) => Console.WriteLine(message);
}
