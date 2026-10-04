using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 自动对局机器人：连接、加入、按阶段自动行动，用于配合真实 GUI 做多轮联调。
/// 用法：dotnet run --project ClientGUI.Tests -- --bots bob carol dave 90
/// 若机器人最先加入（成为房主），凑够 4 人后会自动开局。
/// </summary>
public static class BotMode
{
    private sealed class Bot
    {
        public required string Name { get; init; }
        public required GameClient Client { get; init; }

        /// <summary>本回合是否已经行动过（避免重复提交）。</summary>
        public string? ActedKey { get; set; }

        public bool RequestedStart { get; set; }
    }

    public static async Task<int> RunAsync(string[] names, int seconds, string url)
    {
        Console.WriteLine($"[bots] {names.Length} 个机器人上线 {seconds} 秒：{string.Join(", ", names)}");

        var bots = names.Select(n => new Bot { Name = n, Client = new GameClient() }).ToList();

        foreach (var bot in bots)
        {
            var self = bot;
            self.Client.StateReceived += state => OnState(self, state);
            self.Client.MessageReceived += m =>
            {
                if (m.Contains("你的身份是") || m.Contains("游戏开始") || m.Contains("被投票放逐")
                    || m.Contains("昨晚") || m.Contains("获胜") || m.StartsWith("💬")
                    || m.Contains("开枪") || m.Contains("守护"))
                    Console.WriteLine($"[{self.Name}] {m.Replace("\n", " ")}");
            };
        }

        foreach (var bot in bots)
        {
            try
            {
                await bot.Client.ConnectAsync(url);
                var res = await bot.Client.JoinAsync(bot.Name, "pw" + bot.Name);
                Console.WriteLine($"[{bot.Name}] 加入 ok={res.ok} msg={res.msg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{bot.Name}] 连接失败：{ex.Message}");
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(400);

        foreach (var bot in bots)
        {
            try { await bot.Client.DisposeAsync(); } catch { }
        }

        Console.WriteLine("[bots] 结束。");
        return 0;
    }

    private static void OnState(Bot bot, GameStateDto state)
    {
        var me = state.Players.FirstOrDefault(p => p.IsYou);

        // 房主机器人：凑够 4 人自动开局
        if (!bot.RequestedStart && me is { IsHost: true } && state.Players.Count >= 4
            && state.Phase is Phases.Waiting or Phases.GameOver)
        {
            bot.RequestedStart = true;
            _ = Fire(bot, "开局", async () =>
            {
                var r = await bot.Client.StartGameAsync();
                Console.WriteLine($"[{bot.Name}] 开局 ok={r.ok} msg={r.msg}");
            });
            return;
        }

        // 出局玩家只有猎人开枪阶段还需要行动
        if (!state.IsAlive && state.Phase != Phases.HunterShoot) return;

        var key = $"{state.Day}:{state.Phase}";
        if (bot.ActedKey == key) return;

        switch (state.Phase)
        {
            case Phases.Night:
                bot.ActedKey = key;
                _ = Fire(bot, "夜间", () => NightAction(bot, state));
                break;

            case Phases.Vote:
                bot.ActedKey = key;
                var voteTarget = PickTarget(bot, state, allowSelf: false);
                _ = Fire(bot, "投票", () => bot.Client.VoteAsync(voteTarget));
                break;

            case Phases.HunterShoot when state.YourRole == Roles.Hunter:
                bot.ActedKey = key;
                var shot = PickTarget(bot, state, allowSelf: false);
                _ = Fire(bot, "开枪", () => bot.Client.HunterShotAsync(shot));
                break;

            case Phases.Discussion:
                bot.ActedKey = key;
                _ = Fire(bot, "发言", () => bot.Client.ChatAsync($"{bot.Name} 报到，讨论中。"));
                break;
        }
    }

    private static async Task NightAction(Bot bot, GameStateDto state)
    {
        var target = PickTarget(bot, state, allowSelf: false);
        switch (state.YourRole)
        {
            case Roles.Witch:
                await bot.Client.NightActionAsync(-2, "heal");
                break;
            case Roles.Werewolf:
            case Roles.Seer:
            case Roles.Guard:
                await bot.Client.NightActionAsync(target, "do");
                break;
        }
    }

    private static int PickTarget(Bot bot, GameStateDto state, bool allowSelf)
    {
        var candidates = state.Players
            .Where(p => p.IsAlive && (allowSelf || !p.IsYou))
            .Select(p => p.Id)
            .ToList();

        if (candidates.Count == 0) return -1;

        // 用名字做确定性分散，避免所有机器人选中同一目标
        return candidates[Math.Abs(bot.Name.GetHashCode()) % candidates.Count];
    }

    private static async Task Fire(Bot bot, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{bot.Name}] {what}失败：{ex.Message}");
        }
    }
}
