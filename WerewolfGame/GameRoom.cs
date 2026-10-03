using Microsoft.AspNetCore.SignalR;

namespace Werewolf.Server;

public class GameRoom
{
    private const int NightActionTimeout = 40_000;
    private const int VoteTimeout = 45_000;
    private const int DiscussionTimeout = 45_000;

    private readonly List<Player> _players = new();
    private readonly Random _rng = new();
    private readonly IHubContext<GameHub> _hub;
    private readonly object _sync = new();

    private Phase _phase = Phase.Waiting;
    private int _day;
    private volatile bool _running;

    private int _wolfTarget = -1;
    private int _guardTarget = -1;
    private int _witchHealTarget = -1;
    private int _witchPoisonTarget = -1;

    public GameRoom(IHubContext<GameHub> hub) => _hub = hub;

    public bool IsRunning { get { lock (_sync) return _running; } }
    public Phase Phase { get { lock (_sync) return _phase; } }

    // ==================== 玩家管理 ====================

    /// <summary>加入或重连。返回 (成功, 错误, 玩家对象, 是否重连)</summary>
    public (bool ok, string msg, Player? player, bool reconnected)
        AddOrReconnect(string username, string connId)
    {
        lock (_sync)
        {
            var existing = _players.FirstOrDefault(p =>
                string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

            // ---- 已存在：重连 ----
            if (existing != null)
            {
                if (existing.IsConnected && existing.ConnectionId != connId)
                    return (false, "该账号已在房间中", null, false);

                existing.ConnectionId = connId;
                existing.IsConnected = true;
                return (true, "", existing, true);
            }

            // ---- 新玩家 ----
            if (_running)
                return (false, "游戏进行中，无法加入", null, false);
            if (_phase != Phase.Waiting && _phase != Phase.GameOver)
                return (false, "房间暂未开放", null, false);
            if (_players.Count >= 12)
                return (false, "房间已满（最多 12 人）", null, false);
            if (string.IsNullOrWhiteSpace(username))
                return (false, "用户名不能为空", null, false);

            var p = new Player
            {
                Username = username,
                ConnectionId = connId,
                IsHost = _players.Count == 0,
                IsConnected = true
            };
            _players.Add(p);
            return (true, "", p, false);
        }
    }

    public void OnDisconnected(string connId)
    {
        lock (_sync)
        {
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null) return;

            p.IsConnected = false;
            p.ConnectionId = "";

            // 游戏未运行时，直接踢出列表
            if (!_running)
            {
                _players.Remove(p);
                if (p.IsHost && _players.Count > 0)
                    _players[0].IsHost = true;
            }
        }
    }

    public Player? GetPlayer(string connId)
    {
        lock (_sync) return _players.FirstOrDefault(p => p.ConnectionId == connId);
    }

    // ==================== 开始游戏 ====================

    public (bool ok, string msg) StartGame(string connId)
    {
        lock (_sync)
        {
            if (_running) return (false, "游戏已开始");
            if (_phase != Phase.Waiting && _phase != Phase.GameOver)
                return (false, "游戏正在进行中");

            var host = _players.FirstOrDefault(p => p.IsHost);
            if (host == null || host.ConnectionId != connId)
                return (false, "只有房主可以开始游戏");

            // 清理断线玩家
            _players.RemoveAll(p => !p.IsConnected);
            if (_players.Count < 4) return (false, "至少需要 4 名玩家");
            if (_players.Count > 12) return (false, "玩家过多");

            // 重置每位玩家状态
            foreach (var p in _players)
            {
                p.IsAlive = true;
                p.HasAntidote = true;
                p.HasPoison = true;
                p.LastGuardTarget = -1;
                p.NightTarget = -1;
                p.NightActionType = null;
                p.NightActionSubmitted = false;
                p.VoteTarget = -1;
                p.VoteSubmitted = false;
                p.HunterCanShoot = false;
                p.HunterShotSubmitted = false;
            }

            AssignRoles();
            _day = 0;
            _running = true;
        }
        _ = Task.Run(RunAsync);
        return (true, "");
    }

    private void AssignRoles()
    {
        int n = _players.Count;
        var roles = new List<Role>();

        int wolves = n <= 5 ? 1 : n <= 8 ? 2 : 3;
        for (int i = 0; i < wolves; i++) roles.Add(Role.Werewolf);

        roles.Add(Role.Seer);
        roles.Add(Role.Witch);
        if (n >= 6) roles.Add(Role.Hunter);
        if (n >= 9) roles.Add(Role.Guard);
        while (roles.Count < n) roles.Add(Role.Villager);

        for (int i = roles.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (roles[i], roles[j]) = (roles[j], roles[i]);
        }
        for (int i = 0; i < _players.Count; i++)
            _players[i].Role = roles[i];
    }

    // ==================== 玩家输入 ====================

    public void SubmitNightAction(string connId, int targetId, string action)
    {
        lock (_sync)
        {
            if (_phase != Phase.Night) return;
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null || !p.IsAlive) return;
            p.NightTarget = targetId;
            p.NightActionType = action;
            p.NightActionSubmitted = true;
        }
    }

    public void SubmitVote(string connId, int targetId)
    {
        lock (_sync)
        {
            if (_phase != Phase.Vote) return;
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null || !p.IsAlive) return;
            if (targetId >= 0 && !IsAlive(targetId)) return;
            p.VoteTarget = targetId;
            p.VoteSubmitted = true;
        }
    }

    public void SubmitHunterShot(string connId, int targetId)
    {
        lock (_sync)
        {
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null || !p.HunterCanShoot || p.HunterShotSubmitted) return;
            if (targetId >= 0 && !IsAlive(targetId)) return;
            p.NightTarget = targetId;
            p.HunterShotSubmitted = true;
        }
    }

    // ==================== 主循环 ====================

    private async Task RunAsync()
    {
        try
        {
            await SendAllRolesAsync();
            await BroadcastAsync("🎮 游戏开始！");
            await BroadcastStateAsync();

            while (_running)
            {
                _day++;
                await NightPhaseAsync();
                if (await CheckGameOverAsync()) break;

                await DayPhaseAsync();
                if (await CheckGameOverAsync()) break;
            }
        }
        catch (Exception ex)
        {
            await BroadcastAsync("⚠️ 服务器错误：" + ex.Message);
        }
        finally
        {
            _running = false;
            SetPhase(Phase.GameOver);
            await BroadcastAllRolesAsync();
            await BroadcastStateAsync();
        }
    }

    // ==================== 夜晚 ====================

    private async Task NightPhaseAsync()
    {
        ResetNightData();
        SetPhase(Phase.Night);
        await BroadcastAsync($"════ 第 {_day} 夜 · 天黑请闭眼 ════");
        await BroadcastStateAsync();

        // ---------- 守卫 ----------
        var guards = AliveWithRole(Role.Guard);
        if (guards.Count > 0)
        {
            await PrivateToAsync(guards, "【守卫】请选择今晚守护的玩家编号（不能连续两晚守护同一人）。");
            await WaitNightActionAsync(Role.Guard, NightActionTimeout);

            var guard = guards[0];
            int t = guard.NightTarget;
            if (t >= 0 && t != guard.LastGuardTarget && IsAlive(t))
            {
                _guardTarget = t;
                guard.LastGuardTarget = t;
                await PrivateAsync(guard, $"你守护了 [{t}]{NameOf(t)}。");
            }
            else
            {
                guard.LastGuardTarget = -1;
                await PrivateAsync(guard, "守护无效（不能连守同一人 / 目标已死亡），今晚你未守护任何人。");
            }
        }

        // ---------- 狼人 ----------
        var wolves = AliveWithRole(Role.Werewolf);
        if (wolves.Count > 0)
        {
            string mates = string.Join("、", wolves.Select(w => $"[{IdOf(w)}]{w.Username}"));
            await PrivateToAsync(wolves, $"【狼人】你的队友：{mates}\n请选择今晚要击杀的目标编号。");
            await WaitNightActionAsync(Role.Werewolf, NightActionTimeout);

            int t = PickMostVotedNight(Role.Werewolf);
            if (t >= 0 && IsAlive(t)) _wolfTarget = t;

            await PrivateToAsync(wolves, _wolfTarget >= 0
                ? $"你们选择了击杀 [{_wolfTarget}]{NameOf(_wolfTarget)}。"
                : "你们今晚放弃了击杀。");
        }

        // ---------- 女巫 ----------
        var witch = AliveWithRole(Role.Witch).FirstOrDefault();
        if (witch != null)
        {
            string killed = _wolfTarget >= 0
                ? $"今晚 [{_wolfTarget}]{NameOf(_wolfTarget)} 被狼人击杀。"
                : "今晚没有人被狼人击杀。";
            await PrivateAsync(witch,
                $"【女巫】{killed}\n解药：{(witch.HasAntidote ? "可用" : "已用")}　毒药：{(witch.HasPoison ? "可用" : "已用")}\n" +
                "输入 heal 使用解药 / poison <编号> 使用毒药 / none 不使用。");

            await WaitNightActionAsync(Role.Witch, NightActionTimeout);

            string? act = witch.NightActionType;
            if (act == "heal" && witch.HasAntidote && _wolfTarget >= 0)
            {
                _witchHealTarget = _wolfTarget;
                witch.HasAntidote = false;
                await PrivateAsync(witch, $"你使用了解药，救活了 [{_wolfTarget}]{NameOf(_wolfTarget)}。");
            }
            else if (act == "poison" && witch.HasPoison && witch.NightTarget >= 0 && IsAlive(witch.NightTarget))
            {
                _witchPoisonTarget = witch.NightTarget;
                witch.HasPoison = false;
                await PrivateAsync(witch, $"你使用了毒药，毒杀了 [{_witchPoisonTarget}]{NameOf(_witchPoisonTarget)}。");
            }
            else
            {
                await PrivateAsync(witch, "你今晚没有使用任何药水。");
            }
        }

        // ---------- 预言家 ----------
        var seer = AliveWithRole(Role.Seer).FirstOrDefault();
        if (seer != null)
        {
            await PrivateAsync(seer, "【预言家】请选择你要查验的玩家编号。");
            await WaitNightActionAsync(Role.Seer, NightActionTimeout);

            int t = seer.NightTarget;
            if (t >= 0 && IsAlive(t))
            {
                var target = PlayerById(t)!;
                await PrivateAsync(seer, $"🔍 查验结果：[{t}]{target.Username} 是 【{(target.IsWolf ? "狼人" : "好人")}】。");
            }
            else
            {
                await PrivateAsync(seer, "你今晚没有查验任何人。");
            }
        }
    }

    // ==================== 白天 ====================

    private async Task DayPhaseAsync()
    {
        SetPhase(Phase.Day);

        var deaths = new List<int>();
        lock (_sync)
        {
            if (_wolfTarget >= 0)
            {
                bool saved = _guardTarget == _wolfTarget || _witchHealTarget == _wolfTarget;
                if (!saved) deaths.Add(_wolfTarget);
            }
            if (_witchPoisonTarget >= 0 && !deaths.Contains(_witchPoisonTarget))
                deaths.Add(_witchPoisonTarget);

            foreach (var id in deaths)
            {
                var p = PlayerById(id);
                if (p != null) p.IsAlive = false;
            }
        }

        await BroadcastAsync($"════ 第 {_day} 天 · 天亮了 ════");
        await BroadcastAsync(deaths.Count == 0
            ? "🌤 昨晚是平安夜，没有人死亡。"
            : $"💀 昨晚死亡的是：{string.Join("、", deaths.Select(d => $"[{d}]{NameOf(d)}"))}");

        foreach (var id in deaths)
        {
            var p = PlayerById(id);
            if (p != null && p.Role == Role.Hunter && id != _witchPoisonTarget)
                await HunterShootAsync(p);
        }

        if (await CheckGameOverAsync()) return;

        SetPhase(Phase.Discussion);
        await BroadcastAsync($"💬 自由讨论时间（{DiscussionTimeout / 1000} 秒），输入 `chat 内容` 发言。");
        await BroadcastStateAsync();
        await Task.Delay(DiscussionTimeout);

        if (await CheckGameOverAsync()) return;

        await VotePhaseAsync();
    }

    private async Task VotePhaseAsync()
    {
        lock (_sync)
        {
            foreach (var p in _players) { p.VoteTarget = -1; p.VoteSubmitted = false; }
        }

        SetPhase(Phase.Vote);
        await BroadcastAsync("🗳 请投票放逐一名玩家（输入编号，输入 -1 弃票）。");
        await BroadcastStateAsync();

        await WaitUntilAsync(() =>
        {
            lock (_sync)
            {
                var alive = _players.Where(p => p.IsAlive && p.IsConnected).ToList();
                return alive.Count == 0 || alive.All(p => p.VoteSubmitted);
            }
        }, VoteTimeout);

        Dictionary<int, int> tally;
        lock (_sync)
        {
            tally = _players.Where(p => p.IsAlive && p.VoteTarget >= 0)
                            .GroupBy(p => p.VoteTarget)
                            .ToDictionary(g => g.Key, g => g.Count());
        }

        if (tally.Count == 0)
        {
            await BroadcastAsync("全员弃票，无人被放逐。");
            return;
        }

        await BroadcastAsync("📊 投票结果：" + string.Join("　",
            tally.OrderByDescending(kv => kv.Value).Select(kv => $"[{kv.Key}]{NameOf(kv.Key)} {kv.Value}票")));

        int max = tally.Values.Max();
        var top = tally.Where(kv => kv.Value == max).Select(kv => kv.Key).ToList();

        if (top.Count > 1)
        {
            await BroadcastAsync("⚖️ 平票，无人被放逐。");
            await BroadcastStateAsync();
            return;
        }

        int outId = top[0];
        Player? outP;
        lock (_sync)
        {
            outP = PlayerById(outId);
            if (outP != null) outP.IsAlive = false;
        }
        await BroadcastAsync($"⚖️ [{outId}]{outP!.Username} 被投票放逐。");

        if (outP.Role == Role.Hunter)
            await HunterShootAsync(outP);

        await BroadcastStateAsync();
    }

    // ==================== 猎人开枪 ====================

    private async Task HunterShootAsync(Player hunter)
    {
        lock (_sync)
        {
            hunter.HunterCanShoot = true;
            hunter.HunterShotSubmitted = false;
            hunter.NightTarget = -1;
        }

        SetPhase(Phase.HunterShoot);
        await BroadcastAsync($"💥 [{IdOf(hunter)}]{hunter.Username} 是猎人！可以选择开枪带走一名玩家。");
        await PrivateAsync(hunter, "【猎人】请选择你要开枪带走的玩家编号，或输入 -1 放弃。");
        await BroadcastStateAsync();

        await WaitUntilAsync(() =>
        {
            lock (_sync) return hunter.HunterShotSubmitted;
        }, 30_000);

        hunter.HunterCanShoot = false;
        int target = hunter.NightTarget;

        if (target >= 0 && IsAlive(target))
        {
            Player? t;
            lock (_sync)
            {
                t = PlayerById(target);
                if (t != null) t.IsAlive = false;
            }
            await BroadcastAsync($"🔫 猎人 [{IdOf(hunter)}]{hunter.Username} 开枪带走了 [{target}]{t!.Username}。");
        }
        else
        {
            await BroadcastAsync("猎人放弃了开枪。");
        }
        await BroadcastStateAsync();
    }

    // ==================== 胜负判定 ====================

    private async Task<bool> CheckGameOverAsync()
    {
        List<Player> snap;
        lock (_sync) snap = _players.ToList();

        int wolves = snap.Count(p => p.IsAlive && p.Role == Role.Werewolf);
        int others = snap.Count(p => p.IsAlive && p.Role != Role.Werewolf);

        if (wolves == 0)
        {
            _running = false;
            await BroadcastAsync("🎉 所有狼人已被消灭，【好人阵营】获胜！");
            return true;
        }
        if (wolves >= others)
        {
            _running = false;
            await BroadcastAsync("🐺 狼人数量已达到或超过好人，【狼人阵营】获胜！");
            return true;
        }
        return false;
    }

    // ==================== 辅助 ====================

    private void ResetNightData()
    {
        lock (_sync)
        {
            _wolfTarget = _guardTarget = _witchHealTarget = _witchPoisonTarget = -1;
            foreach (var p in _players)
            {
                p.NightTarget = -1;
                p.NightActionType = null;
                p.NightActionSubmitted = false;
            }
        }
    }

    private void SetPhase(Phase p) { lock (_sync) _phase = p; }

    private Player? PlayerById(int id)
    {
        lock (_sync) return (id >= 0 && id < _players.Count) ? _players[id] : null;
    }

    private bool IsAlive(int id)
    {
        lock (_sync) return id >= 0 && id < _players.Count && _players[id].IsAlive;
    }

    private string NameOf(int id)
    {
        lock (_sync) return (id >= 0 && id < _players.Count) ? _players[id].Username : "?";
    }

    private int IdOf(Player p) { lock (_sync) return _players.IndexOf(p); }

    private List<Player> AliveWithRole(Role role)
    {
        lock (_sync) return _players.Where(p => p.IsAlive && p.Role == role).ToList();
    }

    private List<Player> Connected()
    {
        lock (_sync) return _players
            .Where(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId))
            .ToList();
    }

    private int PickMostVotedNight(Role role)
    {
        lock (_sync)
        {
            var grouped = _players
                .Where(p => p.IsAlive && p.Role == role && p.NightTarget >= 0)
                .GroupBy(p => p.NightTarget)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList();

            if (grouped.Count == 0) return -1;
            int max = grouped[0].Count;
            var top = grouped.Where(x => x.Count == max).ToList();
            return top[_rng.Next(top.Count)].Id;
        }
    }

    private async Task WaitNightActionAsync(Role role, int timeoutMs)
    {
        await WaitUntilAsync(() =>
        {
            lock (_sync)
            {
                var actors = _players
                    .Where(p => p.IsAlive && p.Role == role && p.IsConnected)
                    .ToList();
                return actors.Count == 0 || actors.All(p => p.NightActionSubmitted);
            }
        }, timeoutMs);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(150);
        }
        return condition();
    }

    // ==================== 消息推送 ====================

    public async Task BroadcastAsync(string message)
    {
        foreach (var p in Connected())
        {
            try { await _hub.Clients.Client(p.ConnectionId).SendAsync("Message", message); }
            catch { /* 忽略单个客户端异常 */ }
        }
    }

    private async Task PrivateAsync(Player p, string message)
    {
        if (!p.IsConnected || string.IsNullOrEmpty(p.ConnectionId)) return;
        try { await _hub.Clients.Client(p.ConnectionId).SendAsync("Message", message); }
        catch { }
    }

    private async Task PrivateToAsync(IEnumerable<Player> ps, string message)
    {
        foreach (var p in ps) await PrivateAsync(p, message);
    }

    private async Task SendAllRolesAsync()
    {
        List<Player> snap;
        lock (_sync) snap = _players.ToList();
        foreach (var p in snap)
            await PrivateAsync(p, $"🎭 你的身份是【{RoleInfo.Name(p.Role)}】：{RoleInfo.Desc(p.Role)}");
    }

    /// <summary>重连时补发一次角色私聊</summary>
    public async Task ResendRoleAsync(Player p)
        => await PrivateAsync(p, $"🎭 你的身份是【{RoleInfo.Name(p.Role)}】：{RoleInfo.Desc(p.Role)}");

    private async Task BroadcastAllRolesAsync()
    {
        List<Player> snap;
        lock (_sync) snap = _players.ToList();
        var lines = snap.Select((p, i) => $"[{i}]{p.Username} → {RoleInfo.Name(p.Role)}");
        await BroadcastAsync("📜 本局身份公开：\n" + string.Join("\n", lines));
    }

    public async Task BroadcastStateAsync()
    {
        List<Player> all;
        List<Player> viewers;
        Phase phase;
        int day;
        bool running;
        lock (_sync)
        {
            all = _players.ToList();
            viewers = Connected();
            phase = _phase;
            day = _day;
            running = _running;
        }

        foreach (var viewer in viewers)
        {
            var dto = BuildStateFor(viewer, all, phase, day, running);
            try { await _hub.Clients.Client(viewer.ConnectionId).SendAsync("State", dto); }
            catch { }
        }
    }

    private static GameStateDto BuildStateFor(Player viewer, List<Player> all,
                                              Phase phase, int day, bool running)
    {
        bool revealAll = !running || phase == Phase.GameOver;

        return new GameStateDto
        {
            Phase = phase.ToString(),
            Day = day,
            YourId = all.IndexOf(viewer),
            YourRole = RoleInfo.Name(viewer.Role),
            YourRoleDesc = RoleInfo.Desc(viewer.Role),
            IsAlive = viewer.IsAlive,
            Players = all.Select((p, i) => new PlayerDto
            {
                Id = i,
                Name = p.Username,
                IsAlive = p.IsAlive,
                IsHost = p.IsHost,
                IsConnected = p.IsConnected,
                IsYou = p.Username == viewer.Username,
                Role = (revealAll
                        || p.Username == viewer.Username
                        || (viewer.Role == Role.Werewolf && p.Role == Role.Werewolf))
                       ? RoleInfo.Name(p.Role) : null
            }).ToList()
        };
    }
}

public class GameRoomService
{
    public GameRoom Room { get; }
    public GameRoomService(IHubContext<GameHub> hub) => Room = new GameRoom(hub);
}