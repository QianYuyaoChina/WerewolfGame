using Werewolf.Server.Games;

namespace Werewolf.Server;

/// <summary>
/// 组合根的**唯一**入参：内容根（工作目录）、监听地址与命令行参数。
/// 独立服务端与进程内内置服务器共用本类型，差别只在传入的值。
/// </summary>
public sealed class ServerBootstrapOptions
{
    /// <summary>内容根与工作目录（独立服务端传当前目录；内置服务器传 embedded-server 目录）。</summary>
    public required string ContentRoot { get; init; }

    /// <summary>监听地址（如 <c>http://0.0.0.0:5000</c>）。</summary>
    public required string Url { get; init; }

    /// <summary>命令行参数（等价现有 <c>WebApplication.CreateBuilder(args)</c>）。</summary>
    public string[]? Args { get; init; }
}

/// <summary>
/// 唯一的组合根：注册全部服务、配置管道、映射终结点。
///
/// 独立服务端（<c>Program.cs</c>）与进程内内置服务器（<see cref="EmbeddedServerHost"/>）
/// 共用同一份，保证两者在 DI / 中间件 / 游戏发现上**结构上不可能**漂移：
/// 新增游戏只需新增一个模块文件，本文件无需改动，也绝不引用任何具体游戏类型。
///
/// 本类**不含**任何 <c>Console.WriteLine</c>、端口决策与工作目录决策 ——
/// 那些属于宿主方（独立 exe 或内置服务器）的职责。
/// </summary>
public static class ServerBootstrap
{
    /// <summary>
    /// 构建并返回一个已配置完成、但**尚未启动**的 <c>WebApplication</c>。
    /// </summary>
    /// <param name="options">内容根 / 监听地址 / 命令行参数。</param>
    /// <param name="configure">可选：宿主方对 builder 的额外配置（内置服务器用它关掉控制台日志）。</param>
    public static WebApplication Build(ServerBootstrapOptions options,
                                       Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = options.Args,
            ContentRootPath = options.ContentRoot
        });

        builder.Services.AddSignalR();

        // 账号文件显式传绝对路径：独立服务端 = CWD\accounts.json（与改造前同一个文件），
        // 内置服务器 = 工作目录\accounts.json。两边都不依赖进程 CWD 也能正确。
        builder.Services.AddSingleton(new AccountStore(Path.Combine(options.ContentRoot, "accounts.json")));

        // 账号目录接口：房间管理器 / 房间只认这个门面（称号下发 + 管理员判定），
        // 不认识 AccountStore 的存盘格式（v2.5.0 第 4 / 5 条）。
        builder.Services.AddSingleton<IAccountDirectory>(sp => sp.GetRequiredService<AccountStore>());

        builder.Services.AddSingleton<CryptoSessionStore>();
        builder.Services.AddSingleton<EncryptedChannel>();

        // 只读配置门面：游戏模块经 IGameContext.Config 按节点名自取规则（如从 GameRules 节点读时长 / 人数）。
        builder.Services.AddSingleton<IGameConfig>(new GameConfig(builder.Configuration));

        // 游戏注册表：反射发现程序集内所有「公共无参构造」的 IGameModule 实现。
        // 用 typeof(ServerBootstrap).Assembly 而不是顶层 Program，去掉对入口类的依赖。
        var catalog = new GameCatalog();
        catalog.RegisterAll(GameDiscovery.Discover(typeof(ServerBootstrap).Assembly));
        builder.Services.AddSingleton<IGameCatalog>(catalog);

        // 可编辑数据资源登记处（v2.5.0 第 5 条）：构造时遍历 IGameCatalog 的全部模块，
        // 逐个问「你有哪些可编辑资源」。平台层只认识 IEditableDataResource 六个成员，
        // 不认识任何游戏专属文件名 / 行格式（架构 K7：平台层零游戏后门）。
        builder.Services.AddSingleton(sp => new EditableDataRegistry(
            sp.GetRequiredService<IGameCatalog>(), options.ContentRoot));

        // 房间运行参数（宽限期 / 房间上限 / 扫描间隔）从 appsettings.json 的 Room 节点读取。
        var roomOptions = builder.Configuration.GetSection("Room").Get<RoomOptions>() ?? new RoomOptions();
        builder.Services.AddSingleton(roomOptions);

        // 多游戏：注册表（纯注册表）+ 后台回收服务（周期回收空房）。
        builder.Services.AddSingleton<RoomManager>();
        builder.Services.AddHostedService<RoomRecycleService>();

        builder.Services.AddCors(o => o.AddDefaultPolicy(
            p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        configure?.Invoke(builder);

        // 明文 HTTP 传输：不再使用 TLS 证书。
        // 机密性与身份认证改由应用层保证——连接建立时用「PSK 认证的快速幂 Diffie-Hellman」握手，
        // 之后所有收发内容都用协商出的密钥 AES-GCM 加密（见 GameHub/EncryptedChannel）。
        builder.WebHost.UseUrls(options.Url);

        var app = builder.Build();

        // 内置管理员账号（v2.5.0 第 5 条）：不存在则按默认密码创建。
        // 放在「组合根构建之后、启动之前」，独立服务端与内置服务器共用同一条路径；
        // 管理员改过密码后不会被重置（EnsureAdmin 只做「缺失补建」）。
        app.Services.GetRequiredService<AccountStore>().EnsureAdmin();

        app.UseCors();
        app.MapHub<GameHub>("/gamehub");
        app.MapGet("/", () => "🕹 Game platform server is running.");
        return app;
    }

    /// <summary>
    /// 宿主方的额外配置：清空日志提供程序。
    /// 内置服务器跑在客户端进程里，ASP.NET Core 默认的控制台日志会混进客户端输出，
    /// 因此内置宿主在 <see cref="Build"/> 时传入本方法。
    /// </summary>
    public static void SilenceLogging(WebApplicationBuilder builder)
        => builder.Logging.ClearProviders();
}
