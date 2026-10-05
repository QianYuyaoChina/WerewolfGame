using Werewolf.Server;
using Werewolf.Server.Games;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<CryptoSessionStore>();
builder.Services.AddSingleton<EncryptedChannel>();

// 只读配置门面：游戏模块经 IGameContext.Config 按节点名自取规则（如从 GameRules 节点读时长 / 人数）。
builder.Services.AddSingleton<IGameConfig>(new GameConfig(builder.Configuration));

// 游戏注册表：反射发现程序集内所有「公共无参构造」的 IGameModule 实现。
// 新增游戏只需新增一个模块文件，本组合根（Program.cs）无需改动，也绝不引用任何具体游戏类型。
var catalog = new GameCatalog();
catalog.RegisterAll(GameDiscovery.Discover(typeof(Program).Assembly));
builder.Services.AddSingleton<IGameCatalog>(catalog);

// 房间运行参数（宽限期 / 房间上限 / 扫描间隔）从 appsettings.json 的 Room 节点读取。
var roomOptions = builder.Configuration.GetSection("Room").Get<RoomOptions>() ?? new RoomOptions();
builder.Services.AddSingleton(roomOptions);

// 多游戏：注册表（纯注册表）+ 后台回收服务（周期回收空房）。
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddHostedService<RoomRecycleService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// 明文 HTTP 传输：不再使用 TLS 证书。
// 机密性与身份认证改由应用层保证——连接建立时用「PSK 认证的快速幂 Diffie-Hellman」握手，
// 之后所有收发内容都用协商出的密钥 AES-GCM 加密（见 GameHub/EncryptedChannel）。
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();
app.UseCors();
app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => "🕹 Game platform server is running.");

Console.WriteLine("多游戏房间服务端已启动：http://localhost:5000/gamehub");
Console.WriteLine("传输加密：PSK 认证 DH 握手 + AES-GCM 会话加密（应用层，未使用 TLS）");
Console.WriteLine($"可用游戏（{catalog.Available.Count}）：" +
                  string.Join("　", catalog.Available.Select(g => g.Label)));
Console.WriteLine($"多房间：上限 {roomOptions.MaxRooms} 个 · 空房宽限 {roomOptions.EmptyGraceSeconds} 秒 · " +
                  $"扫描间隔 {roomOptions.RecycleScanSeconds} 秒");
Console.WriteLine($"账号文件：{Path.GetFullPath("accounts.json")}");
app.Run();
