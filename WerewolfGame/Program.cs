using Werewolf.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<GameRoomService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();
app.UseCors();
app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => "🐺 Werewolf Server is running.");

Console.WriteLine("狼人杀服务端已启动：http://localhost:5000/gamehub");
Console.WriteLine($"账号文件：{Path.GetFullPath("accounts.json")}");
app.Run();