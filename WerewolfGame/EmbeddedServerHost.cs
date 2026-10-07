using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Werewolf.Server;

/// <summary>内置服务器的启动参数。所有字段都有默认值，宿主方只覆盖需要改的。</summary>
public sealed class EmbeddedServerOptions
{
    /// <summary>首选端口（PRD Q1 裁定：固定 5000）。</summary>
    public int PreferredPort { get; init; } = 5000;

    /// <summary>漂移范围起点（含）。</summary>
    public int PortRangeStart { get; init; } = 5001;

    /// <summary>漂移范围终点（含）。</summary>
    public int PortRangeEnd { get; init; } = 5100;

    /// <summary>首选端口被占用时是否自动换一个空闲端口（默认开启）。</summary>
    public bool AllowPortDrift { get; init; } = true;

    /// <summary>监听地址（PRD Q2 裁定：固定 0.0.0.0，界面不提供「仅本机」开关）。</summary>
    public string ListenAddress { get; init; } = "0.0.0.0";

    /// <summary>强制指定工作目录；为空时按 <see cref="EmbeddedServerPaths.TryResolve"/> 解析。</summary>
    public string? WorkDirectory { get; init; }

    /// <summary>命令行参数（透传给 <see cref="ServerBootstrap"/>）。</summary>
    public string[]? Args { get; init; }
}

/// <summary>
/// 一次启动的结果。本类型是**单一来源**：界面显示的地址、自动连接用的地址、
/// 以及随后生成的邀请码（取自 <c>GameClient.Endpoint</c>，即连接地址）全部来自同一份，
/// 因此不可能出现「显示 5001 却连 5000」（PRD E-16 / P1-6）。
/// </summary>
public sealed class EmbeddedServerInfo
{
    /// <summary>是否已成功启动并正在监听。</summary>
    public bool Ok { get; init; }

    /// <summary>失败时的人话原因（成功时为空字符串）。绝不把异常堆栈交给用户。</summary>
    public string ErrorMessage { get; init; } = "";

    /// <summary>实际监听端口（漂移后为漂移到的那个）。</summary>
    public int Port { get; init; }

    /// <summary>实际工作目录（配置 / 账号 / 词库所在）。</summary>
    public string WorkDirectory { get; init; } = "";

    /// <summary>本机地址（含 <c>/gamehub</c>）。</summary>
    public string LocalUrl { get; init; } = "";

    /// <summary>局域网地址（含 <c>/gamehub</c>；无局域网网卡时与 <see cref="LocalUrl"/> 相同）。</summary>
    public string LanUrl { get; init; } = "";

    /// <summary>
    /// 自动连接使用的地址（含 <c>/gamehub</c>）。
    /// 决定：优先局域网地址 —— 邀请码里的地址取自 <c>GameClient.Endpoint</c>，
    /// 若自动连接用 127.0.0.1，好友拿到邀请码就连不进来。
    /// </summary>
    public string ConnectUrl { get; init; } = "";

    /// <summary>启动期的提示（配置自愈 / 端口漂移 / 目录回退），供界面与控制台直接展示。</summary>
    public IReadOnlyList<string> BootstrapMessages { get; init; } = Array.Empty<string>();

    /// <summary>未启动时的占位结果。</summary>
    public static EmbeddedServerInfo NotStarted { get; } = new()
    {
        Ok = false,
        ErrorMessage = "内置服务器尚未启动。"
    };

    /// <summary>构造一个失败结果（人话原因）。</summary>
    public static EmbeddedServerInfo Fail(string message) => new() { Ok = false, ErrorMessage = message };

    /// <summary>在保留目录 / 提示的前提下把状态置为「已停止」（端口等运行信息清空）。</summary>
    public EmbeddedServerInfo AsStopped() => new()
    {
        Ok = false,
        ErrorMessage = "",
        Port = 0,
        WorkDirectory = WorkDirectory,
        LocalUrl = "",
        LanUrl = "",
        ConnectUrl = "",
        BootstrapMessages = BootstrapMessages
    };
}

/// <summary>进程内查询到的运行信息（房间数 / 已连接玩家数）。不新增任何协议。</summary>
/// <param name="RoomCount">当前存活房间数。</param>
/// <param name="PlayerCount">已完成握手的连接数（自身 + 其他连入者）。</param>
public readonly record struct EmbeddedServerRuntime(int RoomCount, int PlayerCount);

/// <summary>
/// 进程内内置服务器：负责工作目录隔离、端口绑定与漂移、启停、运行信息与人话失败原因。
///
/// 客户端（GUI / 控制台）只通过本类驱动服务端 —— **不直接触碰任何 ASP.NET Core API**
/// （客户端没有 ASP.NET Core 的隐式 using，直接碰会踩编译错；也让两端天然共用同一份宿主代码）。
///
/// 契约：<see cref="StartAsync"/> **绝不抛出**——失败以 <c>Info.Ok=false</c> + 人话
/// <see cref="EmbeddedServerInfo.ErrorMessage"/> 返回，由调用方呈现给用户。
/// </summary>
public sealed class EmbeddedServerHost : IAsyncDisposable
{
    /// <summary>SignalR 终结点路径（所有对外地址都必须带）。</summary>
    public const string HubPath = "/gamehub";

    private WebApplication? _app;
    private string _previousCwd = "";
    private bool _cwdChanged;

    public EmbeddedServerHost(EmbeddedServerOptions options)
    {
        Options = options;
        Info = EmbeddedServerInfo.NotStarted;
    }

    public EmbeddedServerOptions Options { get; }

    /// <summary>是否已成功启动并正在监听。</summary>
    public bool IsRunning => _app != null;

    /// <summary>最近一次启动结果（未启动 / 失败时 <c>Ok=false</c>，<c>ErrorMessage</c> 为人话）。</summary>
    public EmbeddedServerInfo Info { get; private set; }

    // ==================== 启动 ====================

    /// <summary>
    /// 启动：解析工作目录 → 配置自愈 → 绑定端口（占用则按 <see cref="EmbeddedServerOptions.AllowPortDrift"/> 漂移）。
    /// 绝不抛出——失败以 <c>Info.Ok=false</c> + 人话 <c>ErrorMessage</c> 返回。
    /// </summary>
    public async Task<EmbeddedServerInfo> StartAsync(CancellationToken ct = default)
    {
        // 重复启动：保持状态与端口的单一来源（PRD E-2）。
        if (_app != null) return Info;

        var notes = new List<string>();

        string workDir;
        if (!string.IsNullOrWhiteSpace(Options.WorkDirectory))
        {
            workDir = Options.WorkDirectory!;
            if (!EmbeddedServerPaths.IsWritable(workDir))
            {
                Info = EmbeddedServerInfo.Fail($"服务器目录 {workDir} 不可写入，请换一个目录或检查权限后重试。");
                return Info;
            }
        }
        else if (!EmbeddedServerPaths.TryResolve(out workDir, out var usedFallback, out var dirError))
        {
            Info = EmbeddedServerInfo.Fail(dirError);
            return Info;
        }
        else
        {
            if (usedFallback)
                notes.Add($"⚠️ 程序所在目录不可写，服务器文件已改用 {workDir}");
        }

        // 配置自愈：缺失 / 损坏时自动重建并备份（E-5）。提示会展示在界面 / 控制台上。
        try
        {
            notes.AddRange(ConfigBootstrap.Ensure(workDir));
        }
        catch (Exception ex)
        {
            notes.Add($"⚠️ 配置文件检查失败（{ex.Message}），服务器将使用内置缺省值继续启动。");
        }

        // 启动前切换进程 CWD 到工作目录：
        // 这是让「所有按相对路径读写的服务端代码」落进工作目录的唯一不改游戏模块的办法，
        // 等价于「在那个目录里单独启动服务端 exe」，属于平台级、游戏无关的处理。
        _previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.CurrentDirectory = workDir;
            _cwdChanged = true;
        }
        catch
        {
            _cwdChanged = false;
            notes.Add("⚠️ 无法切换工作目录，服务器文件可能落在程序目录里。");
        }

        var firstPort = Options.PreferredPort;
        foreach (var port in PortSequence(Options))
        {
            // 契约是「绝不抛出」：取消也以人话结果返回，而不是抛 OperationCanceledException。
            if (ct.IsCancellationRequested)
            {
                Info = EmbeddedServerInfo.Fail("启动已取消。");
                RestoreCwd();
                return Info;
            }

            WebApplication? app = null;
            try
            {
                app = ServerBootstrap.Build(new ServerBootstrapOptions
                {
                    ContentRoot = workDir,
                    Url = $"http://{Options.ListenAddress}:{port}",
                    Args = Options.Args
                }, ServerBootstrap.SilenceLogging);

                await app.StartAsync(ct).ConfigureAwait(false);

                _app = app;
                if (port != firstPort)
                    notes.Add($"端口 {firstPort} 已被占用，已改用 {port}。");

                // 三处地址全部来自同一个实际端口：界面显示、自动连接、以及随后生成的
                // 邀请码（取自 GameClient.Endpoint），天然满足 E-16 / P1-6。
                var lanUrl = FormatUrl(ResolveLanAddress(), port);
                Info = new EmbeddedServerInfo
                {
                    Ok = true,
                    ErrorMessage = "",
                    Port = port,
                    WorkDirectory = workDir,
                    LocalUrl = FormatUrl("127.0.0.1", port),
                    LanUrl = lanUrl,
                    ConnectUrl = lanUrl,
                    BootstrapMessages = notes
                };
                return Info;
            }
            catch (IOException ex) when (IsAddressInUse(ex))
            {
                // 绑定失败 → 释放该实例，换下一个端口（竞态安全：谁先绑上谁赢）。
                await SafeDisposeAsync(app).ConfigureAwait(false);
                continue;
            }
            catch (IOException ex)
            {
                // 非占用类绑定失败（权限 / 地址不可用）→ 立即失败，不再漂移（E-4）。
                await SafeDisposeAsync(app).ConfigureAwait(false);
                Info = EmbeddedServerInfo.Fail($"端口 {port} 无法绑定（{DescribeBindFailure(ex)}）。请换一个端口后重试。");
                RestoreCwd();
                return Info;
            }
            catch (Exception ex)
            {
                await SafeDisposeAsync(app).ConfigureAwait(false);
                Info = EmbeddedServerInfo.Fail("内置服务器启动失败：" + DescribeBindFailure(ex));
                RestoreCwd();
                return Info;
            }
        }

        // 全部端口都不可用：按「是否开了漂移」给出不同的人话（E-1）。
        Info = EmbeddedServerInfo.Fail(Options.AllowPortDrift
            ? $"端口 {Options.PreferredPort} 已被占用，{Options.PortRangeStart}–{Options.PortRangeEnd} 也没有空闲端口了。" +
              "请关掉一个占用端口的程序后重试，或改用「连接现有服务器」。"
            : $"端口 {Options.PreferredPort} 已被占用，请换一个端口或关闭占用它的程序。");
        RestoreCwd();
        return Info;
    }

    // ==================== 运行信息 ====================

    /// <summary>进程内直接查询运行信息（不新增协议）。</summary>
    public EmbeddedServerRuntime Snapshot()
    {
        var app = _app;
        if (app == null) return new EmbeddedServerRuntime(0, 0);

        try
        {
            var rooms = app.Services.GetService<RoomManager>();
            var sessions = app.Services.GetService<CryptoSessionStore>();
            return new EmbeddedServerRuntime(rooms?.Count ?? 0, sessions?.Count ?? 0);
        }
        catch
        {
            // 运行信息只是展示用，查询失败不该影响主流程。
            return new EmbeddedServerRuntime(0, 0);
        }
    }

    // ==================== 停止 ====================

    /// <summary>优雅停止；超过 <paramref name="timeout"/> 即放弃等待（由调用方决定后续）。</summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var app = _app;
        if (app == null) return;

        _app = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 停止阶段的异常无需打扰用户：进程退出即视为服务器停止（E-15）。
        }

        await SafeDisposeAsync(app).ConfigureAwait(false);
        RestoreCwd();

        Info = Info.AsStopped();
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

    // ==================== 纯函数（供调用方与单测直接复用） ====================

    /// <summary>
    /// 端口尝试序列：<c>PreferredPort</c>，随后（若开了漂移）<c>PortRangeStart..PortRangeEnd</c>。
    /// 抽成纯函数是为了可测：它不绑定任何端口，只产出「该试哪些端口」。
    /// </summary>
    public static IEnumerable<int> PortSequence(EmbeddedServerOptions options)
    {
        yield return options.PreferredPort;
        if (!options.AllowPortDrift) yield break;

        var start = Math.Min(options.PortRangeStart, options.PortRangeEnd);
        var end = Math.Max(options.PortRangeStart, options.PortRangeEnd);
        for (var port = start; port <= end; port++)
        {
            if (port == options.PreferredPort) continue;
            yield return port;
        }
    }

    /// <summary>把「地址 + 端口」拼成带 <c>/gamehub</c> 的完整地址。</summary>
    public static string FormatUrl(string host, int port) => $"http://{host}:{port}{HubPath}";

    /// <summary>
    /// 局域网 IPv4 选取：网卡 Up、非 Loopback、取第一个非 APIPA（169.254.*）的 IPv4 单播地址；
    /// 找不到则返回 <c>127.0.0.1</c>。
    /// </summary>
    public static string ResolveLanAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address.Address)) continue;

                    var text = address.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    return text;
                }
            }
        }
        catch
        {
            // 取不到网卡信息：回落到本机地址，功能不受影响（只是好友连不进来）。
        }

        return "127.0.0.1";
    }

    /// <summary>纯函数：把绑定失败的异常映射成人话（不暴露堆栈）。</summary>
    public static string DescribeBindFailure(Exception ex)
    {
        var socket = InnermostSocketException(ex);
        if (socket != null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "端口已被其他程序占用",
                SocketError.AccessDenied => "没有权限使用该端口",
                SocketError.AddressNotAvailable => "该地址在当前网络环境下不可用",
                SocketError.NetworkDown => "网络不可用",
                SocketError.NetworkUnreachable => "网络不可达",
                _ => "网络错误（" + socket.SocketErrorCode + "）"
            };
        }

        if (ex is UnauthorizedAccessException) return "没有权限";
        if (ex is IOException) return "端口绑定失败";

        return ex.Message;
    }

    /// <summary>是否为「端口已被占用」——判定到最内层的 <see cref="SocketException"/>。</summary>
    public static bool IsAddressInUse(Exception ex)
    {
        var socket = InnermostSocketException(ex);
        return socket != null && socket.SocketErrorCode == SocketError.AddressAlreadyInUse;
    }

    private static SocketException? InnermostSocketException(Exception? ex)
    {
        SocketException? found = null;
        for (var current = ex; current != null; current = current.InnerException)
            if (current is SocketException socket) found = socket;

        return found;
    }

    private static async Task SafeDisposeAsync(WebApplication? app)
    {
        if (app == null) return;
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 绑定失败的实例内部可能已自行走到 shutting down 并释放 IServiceProvider，
            // 此时访问其属性（如 Urls）会抛 ObjectDisposedException —— 一律忽略。
        }
    }

    private void RestoreCwd()
    {
        if (!_cwdChanged) return;
        _cwdChanged = false;

        try
        {
            Environment.CurrentDirectory = _previousCwd;
        }
        catch
        {
            // 还原失败不影响后续：进程即将退出或下次启动会重新设置。
        }
    }
}
