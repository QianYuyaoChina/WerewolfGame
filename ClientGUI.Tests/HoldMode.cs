using ClientGUI.Services;

namespace ClientGUI.Tests;

/// <summary>
/// 保持若干客户端在线（非房主，不操作），用于给 GUI 手动联调凑齐 4 人以上。
/// 用法：dotnet run --project ClientGUI.Tests -- --hold alice bob carol 60
/// </summary>
public static class HoldMode
{
    public static async Task<int> RunAsync(string[] names, int seconds, string url)
    {
        Console.WriteLine($"保持 {names.Length} 个客户端在线 {seconds} 秒：{string.Join(", ", names)}");
        var clients = new List<GameClient>();

        foreach (var name in names)
        {
            var c = new GameClient();
            c.MessageReceived += m =>
            {
                if (m.Contains("你的身份是") || m.Contains("游戏开始") || m.Contains("被投票放逐") || m.Contains("死亡"))
                    Console.WriteLine($"[{name}] {m.Replace("\n", " ")}");
            };
            try
            {
                await c.ConnectAsync(url);
                var res = await c.JoinAsync(name, "pw" + name);
                Console.WriteLine($"[{name}] join ok={res.ok} msg={res.msg}");
                clients.Add(c);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{name}] 连接失败：{ex.Message}");
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        foreach (var c in clients)
        {
            try { await c.DisposeAsync(); } catch { }
        }

        Console.WriteLine("保持模式结束。");
        return 0;
    }
}
