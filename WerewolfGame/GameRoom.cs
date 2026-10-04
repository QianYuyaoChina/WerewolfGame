using Werewolf.Client.Share;

namespace Werewolf.Server;

public class GameRoom
{
    private readonly List<Player> _players = new();
    private readonly Random _rng = new();

    /// <summary>本局生效的规则（时长 / 夜晚顺序 / 人数上下限），由配置注入。</summary>
    private readonly GameRules _rules;

    /// <summary>规范化后的夜晚行动顺序（配置驱动的角色序列）。</summary>
    private readonly List<Role> _nightOrder;

    /// <summary>出站加密通道：所有推送都由它加密后发出，业务代码不碰密文。</summary>
    private readonly EncryptedChannel _channel;
    private readonly object _sync = new();

    /// <summary>房间密码的存储值（<see cref="RoomPassword.Hash"/> 的结果）；无密码为 null。绝不出房间对象。</summary>
    private readonly string? _passwordHash;

    /// <summary>无在线玩家起始时间（UTC）；有在线玩家为 null，用于宽限期回收判定。</summary>
    private DateTime? _emptySince;

    /// <summary>是否为「保留房间」：无人也不被回收，一直存在到服务器退出。</summary>
    private bool _persistent;

    private Phase _phase = Phase.Waiting;
    private int _day;
    private volatile bool _running;

    /// <summary>夜晚当前轮到哪个角色行动；null 表示没有角色正在等待（白天/等待/结束）。</summary>
    private Role? _actingRole;

    private int _wolfTarget = -1;
    private int _guardTarget = -1;
    private int _witchHealTarget = -1;
    private int _witchPoisonTarget = -1;

    /// <summary>
    /// 构造一个房间。密码为 null/空白时视为公开房间；否则仅保存带盐哈希（不保存明文）。
    /// </summary>
    public GameRoom(string id, string name, string? password, EncryptedChannel channel, GameRules rules)
    {
        Id = id ?? "";
        Name = string.IsNullOrWhiteSpace(name) ? "房间" : name.Trim();
        _channel = channel;
        _rules = rules;
        _nightOrder = RoleInfo.ResolveNightOrder(rules.NightOrder);

        if (string.IsNullOrWhiteSpace(password))
        {
            HasPassword = false;
            _passwordHash = null;
        }
        else
        {
            HasPassword = true;
            _passwordHash = RoomPassword.Hash(Id, password);
        }

        CreatedAt = DateTime.UtcNow;
    }

    // ==================== 房间身份信息（只读快照） ====================

    /// <summary>5 位房间号，唯一标识。</summary>
    public string Id { get; }

    /// <summary>房间名（可重名）。</summary>
    public string Name { get; }

    /// <summary>是否设置了密码。</summary>
    public bool HasPassword { get; }

    /// <summary>创建时间（UTC），列表按此排序。</summary>
    public DateTime CreatedAt { get; }

    /// <summary>房主用户名（取房间内 IsHost 的玩家；无则 ""）。</summary>
    public string HostUsername
    {
        get { lock (_sync) return _players.FirstOrDefault(p => p.IsHost)?.Username ?? ""; }
    }

    /// <summary>名单人数。</summary>
    public int PlayerCount { get { lock (_sync) return _players.Count; } }

    /// <summary>在线人数（已连接且持有连接号）。</summary>
    public int ConnectedCount
    {
        get { lock (_sync) return _players.Count(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId)); }
    }

    /// <summary>无在线玩家的起始时间（UTC）；有在线玩家时为 null。</summary>
    public DateTime? EmptySince { get { lock (_sync) return _emptySince; } }

    /// <summary>
    /// 是否为「保留房间」：房主在房间空无一人时选择保留，房间将一直存在到服务器退出
    /// （不参与宽限期回收）。一旦有新玩家加入并再次成为房主，语义不变——是否保留只由房主退出时决定。
    /// </summary>
    public bool Persistent
    {
        get { lock (_sync) return _persistent; }
        set { lock (_sync) _persistent = value; }
    }

    public bool IsRunning { get { lock (_sync) return _running; } }
    public Phase Phase { get { lock (_sync) return _phase; } }

    /// <summary>
    /// 维护在线占用时间戳：有在线玩家时清空 <see cref="EmptySince"/>；
    /// 归零时记录首次归零时间（保留最早值，便于宽限期判定）。每次玩家增/减后调用。
    /// </summary>
    public void TouchOccupancy()
    {
        lock (_sync)
        {
            var anyOnline = _players.Any(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId));
            if (anyOnline) _emptySince = null;
            else _emptySince ??= DateTime.UtcNow;
        }
    }

    /// <summary>校验密码：公开房间一律放行；加密房间比对带盐哈希。</summary>
    public bool VerifyPassword(string? password)
        => !HasPassword || RoomPassword.Verify(Id, password, _passwordHash);

    /// <summary>按用户名（忽略大小写）查找房间内玩家。</summary>
    public Player? GetPlayerByName(string username)
    {
        lock (_sync)
            return _players.FirstOrDefault(p =>
                string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 取某个连接视角下的当前状态快照；该连接不在本房间时返回 null。
    /// 供「主动拉取状态」使用（进房广播早于新界面创建时，用它补齐初始状态）。
    /// </summary>
    public GameStateDto? SnapshotFor(string connId)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(connId)) return null;

            var viewer = _players.FirstOrDefault(p => p.ConnectionId == connId);
            if (viewer == null) return null;

            return BuildStateFor(viewer, _players.ToList(), _phase, _day, _running, _actingRole, _rules, Id, Name);
        }
    }

    /// <summary>生成房间列表摘要。</summary>
    public RoomInfoDto ToInfo()
    {
        lock (_sync)
        {
            var status = _running
                ? RoomStatus.Running
                : _phase == Phase.GameOver ? RoomStatus.GameOver : RoomStatus.Waiting;

            return new RoomInfoDto
            {
                Id = Id,
                Name = Name,
                PlayerCount = _players.Count,
                MaxPlayers = _rules.MaxPlayers,
                Status = status,
                HasPassword = HasPassword,
                Host = _players.FirstOrDefault(p => p.IsHost)?.Username ?? ""
            };
        }
    }

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
                    return (false, ServerMessages.AlreadyInRoom, null, false);

                existing.ConnectionId = connId;
                existing.IsConnected = true;
                return (true, "", existing, true);
            }

            // ---- 新玩家 ----
            if (_running)
                return (false, ServerMessages.GameInProgress, null, false);
            if (_phase != Phase.Waiting && _phase != Phase.GameOver)
                return (false, ServerMessages.GameInProgress, null, false);
            if (_players.Count >= _rules.MaxPlayers)
                return (false, ServerMessages.RoomFull(_rules.MaxPlayers), null, false);
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

    /// <summary>
    /// 连接离开房间：未开局时把玩家移出名单，开局中只标记断线（局内继续）。
    /// 若离开者正是房主且房间内还有成员，则按**加入顺序**把房主让给下一位
    /// （优先让给仍在线的成员，避免房主落在一个掉线玩家身上导致房间无法开局）。
    /// </summary>
    /// <returns>新房主的用户名；没有发生转让时返回空字符串。</returns>
    public string OnDisconnected(string connId)
    {
        lock (_sync)
        {
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null) return "";

            p.IsConnected = false;
            p.ConnectionId = "";

            // 游戏未运行时，直接踢出列表
            if (!_running) _players.Remove(p);

            if (!p.IsHost) return "";

            // 按加入顺序找下一位：先找最早加入且仍在线的，其次找最早加入的。
            var next = _players.FirstOrDefault(x => x.IsConnected && !string.IsNullOrEmpty(x.ConnectionId))
                       ?? _players.FirstOrDefault();
            if (next == null) return "";

            foreach (var q in _players) q.IsHost = false;
            next.IsHost = true;
            return next.Username;
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
                return (false, ServerMessages.OnlyHostCanStart);

            // 重开：先清掉上一局的夜晚/猎人等临时数据，保证是干净的一局。
            if (_phase == Phase.GameOver) ResetForNewGame(connId);

            // 清理断线玩家
            _players.RemoveAll(p => !p.IsConnected);
            if (_players.Count < _rules.MinPlayers) return (false, $"至少需要 {_rules.MinPlayers} 名玩家");
            if (_players.Count > _rules.MaxPlayers) return (false, "玩家过多");

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

    /// <summary>
    /// 重开一局前的清场：清空上一局的夜晚/投票/猎人等临时数据（保留玩家名单与房主）。
    /// 由 <c>StartGame</c> 在 <c>_phase == GameOver</c> 时调用。
    /// </summary>
    public (bool ok, string msg) ResetForNewGame(string connId)
    {
        lock (_sync)
        {
            _wolfTarget = _guardTarget = _witchHealTarget = _witchPoisonTarget = -1;
            _actingRole = null;
            _day = 0;

            foreach (var p in _players)
            {
                p.NightTarget = -1;
                p.NightActionType = null;
                p.NightActionSubmitted = false;
                p.VoteTarget = -1;
                p.VoteSubmitted = false;
                p.HunterCanShoot = false;
                p.HunterShotSubmitted = false;
            }

            return (true, "");
        }
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

            // 只有“当前轮到行动的角色”才能提交技能，未轮到时一律忽略。
            if (_actingRole == null || p.Role != _actingRole.Value) return;

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

        // 夜晚顺序由规则配置驱动（默认 守卫 → 狼人 → 女巫 → 预言家）。
        foreach (var role in _nightOrder)
            await NightStepAsync(role);
    }

    /// <summary>按角色执行一夜中的一步行动。</summary>
    private async Task NightStepAsync(Role role)
    {
        switch (role)
        {
            case Role.Guard: await GuardStepAsync(); return;
            case Role.Werewolf: await WerewolfStepAsync(); return;
            case Role.Witch: await WitchStepAsync(); return;
            case Role.Seer: await SeerStepAsync(); return;
        }
    }

    /// <summary>守卫：守护一名玩家，不能连续两晚守护同一人。</summary>
    private async Task GuardStepAsync()
    {
        var guards = AliveWithRole(Role.Guard);
        if (guards.Count == 0) return;

        await PrivateToAsync(guards, "【守卫】请选择今晚守护的玩家编号（不能连续两晚守护同一人）。");
        await WaitNightActionAsync(Role.Guard, _rules.NightActionMs);

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

    /// <summary>狼人：共同选择一名玩家击杀（多数票，平票随机）。</summary>
    private async Task WerewolfStepAsync()
    {
        var wolves = AliveWithRole(Role.Werewolf);
        if (wolves.Count == 0) return;

        string mates = string.Join("、", wolves.Select(w => $"[{IdOf(w)}]{w.Username}"));
        await PrivateToAsync(wolves, $"【狼人】你的队友：{mates}\n请选择今晚要击杀的目标编号。");
        await WaitNightActionAsync(Role.Werewolf, _rules.NightActionMs);

        int t = PickMostVotedNight(Role.Werewolf);
        if (t >= 0 && IsAlive(t)) _wolfTarget = t;

        await PrivateToAsync(wolves, _wolfTarget >= 0
            ? $"你们选择了击杀 [{_wolfTarget}]{NameOf(_wolfTarget)}。"
            : "你们今晚放弃了击杀。");
    }

    /// <summary>女巫：可使用解药救活被击杀者，或用毒药毒杀一名玩家。</summary>
    private async Task WitchStepAsync()
    {
        var witch = AliveWithRole(Role.Witch).FirstOrDefault();
        if (witch == null) return;

        string killed = _wolfTarget >= 0
            ? $"今晚 [{_wolfTarget}]{NameOf(_wolfTarget)} 被狼人击杀。"
            : "今晚没有人被狼人击杀。";
        await PrivateAsync(witch,
            $"【女巫】{killed}\n解药：{(witch.HasAntidote ? "可用" : "已用")}　毒药：{(witch.HasPoison ? "可用" : "已用")}\n" +
            "输入 heal 使用解药 / poison <编号> 使用毒药 / none 不使用。");

        await WaitNightActionAsync(Role.Witch, _rules.NightActionMs);

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

    /// <summary>预言家：查验一名玩家的阵营。</summary>
    private async Task SeerStepAsync()
    {
        var seer = AliveWithRole(Role.Seer).FirstOrDefault();
        if (seer == null) return;

        await PrivateAsync(seer, "【预言家】请选择你要查验的玩家编号。");
        await WaitNightActionAsync(Role.Seer, _rules.NightActionMs);

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
        await BroadcastAsync($"💬 自由讨论时间（{_rules.DiscussionSeconds} 秒），输入 `chat 内容` 发言。");
        await BroadcastStateAsync();
        await Task.Delay(_rules.DiscussionMs);

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
        }, _rules.VoteMs);

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
        }, _rules.HunterShotMs);

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

    /// <summary>房间内已连接（可推送）的玩家快照。</summary>
    public List<Player> ConnectedPlayers() => Connected();

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
        // 进入等待前标记“当前轮到该角色”，期间其他角色提交的技能会被忽略。
        lock (_sync) _actingRole = role;

        // 广播一次状态，让所有客户端知道现在轮到谁行动（用于“未轮到不能放技能”）。
        await BroadcastStateAsync();

        try
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
        finally
        {
            lock (_sync) _actingRole = null;
        }
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
            await _channel.SendMessageAsync(p.ConnectionId, message);
    }

    private async Task PrivateAsync(Player p, string message)
    {
        if (!p.IsConnected || string.IsNullOrEmpty(p.ConnectionId)) return;
        await _channel.SendMessageAsync(p.ConnectionId, message);
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
        Role? actingRole;
        lock (_sync)
        {
            all = _players.ToList();
            viewers = Connected();
            phase = _phase;
            day = _day;
            running = _running;
            actingRole = _actingRole;
        }

        foreach (var viewer in viewers)
        {
            var dto = BuildStateFor(viewer, all, phase, day, running, actingRole, _rules, Id, Name);
            await _channel.SendStateAsync(viewer.ConnectionId, dto);
        }
    }

    private static GameStateDto BuildStateFor(Player viewer, List<Player> all,
                                              Phase phase, int day, bool running,
                                              Role? actingRole, GameRules rules,
                                              string roomId, string roomName)
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
            ActingRole = actingRole.HasValue ? RoleInfo.Name(actingRole.Value) : "",
            Rules = rules,
            RoomId = roomId,
            RoomName = roomName,
            HostName = all.FirstOrDefault(p => p.IsHost)?.Username ?? "",
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