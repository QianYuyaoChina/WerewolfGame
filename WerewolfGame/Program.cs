using Werewolf.Server;
using Werewolf.Server.Games;

// 配置自愈：appsettings.json 缺失 / 损坏时自动生成默认配置并警告，保证服务器始终能起来。
// （必须在 CreateBuilder 之前：否则读配置时就会抛异常，或静默用内置缺省值。）
foreach (var line in ConfigBootstrap.Ensure())
    Console.WriteLine(line);

// 组合根下沉到 ServerBootstrap：独立 exe 与进程内内置服务器（EmbeddedServerHost）
// 共用同一份 DI / 中间件 / 游戏发现，保证两者功能等价。
// 控制台输出、端口与工作目录的决策留在本文件（宿主方职责）。
var app = ServerBootstrap.Build(new ServerBootstrapOptions
{
    ContentRoot = Directory.GetCurrentDirectory(),   // 与改造前一致：工作目录 = 当前目录
    Url = "http://0.0.0.0:5000",                     // 与改造前一致
    Args = args
});

var catalog = app.Services.GetRequiredService<IGameCatalog>();
var roomOptions = app.Services.GetRequiredService<RoomOptions>();

Console.WriteLine("多游戏房间服务端已启动：http://localhost:5000/gamehub");
Console.WriteLine("传输加密：PSK 认证 DH 握手 + AES-GCM 会话加密（应用层，未使用 TLS）");
Console.WriteLine($"可用游戏（{catalog.Available.Count}）：" +
                  string.Join("　", catalog.Available.Select(g => g.Label)));
Console.WriteLine($"多房间：上限 {roomOptions.MaxRooms} 个 · 空房宽限 {roomOptions.EmptyGraceSeconds} 秒 · " +
                  $"扫描间隔 {roomOptions.RecycleScanSeconds} 秒");
Console.WriteLine($"账号文件：{Path.GetFullPath("accounts.json")}");
app.Run();
