using Werewolf.Client.Share;
using Werewolf.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<CryptoSessionStore>();
builder.Services.AddSingleton<EncryptedChannel>();

// 游戏规则从 appsettings.json 的 GameRules 节点读取（缺失时用内置默认值）。
// 时长、夜晚行动顺序、人数上下限都集中在这里，改配置即可调整玩法，无需改代码。
var rules = builder.Configuration.GetSection("GameRules").Get<GameRules>() ?? new GameRules();
builder.Services.AddSingleton(rules);

// 房间运行参数（宽限期 / 房间上限 / 扫描间隔）从 appsettings.json 的 Room 节点读取。
var roomOptions = builder.Configuration.GetSection("Room").Get<RoomOptions>() ?? new RoomOptions();
builder.Services.AddSingleton(roomOptions);

// 多房间：注册表（纯注册表）+ 后台回收服务（周期回收空房）。
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
app.MapGet("/", () => "🐺 Werewolf Server is running.");

Console.WriteLine("狼人杀服务端已启动：http://localhost:5000/gamehub");
Console.WriteLine("传输加密：PSK 认证 DH 握手 + AES-GCM 会话加密（应用层，未使用 TLS）");
Console.WriteLine($"游戏规则：夜晚单步 {rules.NightActionSeconds} 秒 · 讨论 {rules.DiscussionSeconds} 秒 · " +
                  $"投票 {rules.VoteSeconds} 秒 · 人数 {rules.MinPlayers}-{rules.MaxPlayers}");
Console.WriteLine($"夜晚顺序：{string.Join(" → ", RoleInfo.ResolveNightOrder(rules.NightOrder))}");
Console.WriteLine($"多房间：上限 {roomOptions.MaxRooms} 个 · 空房宽限 {roomOptions.EmptyGraceSeconds} 秒 · " +
                  $"扫描间隔 {roomOptions.RecycleScanSeconds} 秒");
Console.WriteLine($"账号文件：{Path.GetFullPath("accounts.json")}");
app.Run();
