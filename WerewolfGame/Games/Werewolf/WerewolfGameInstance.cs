using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace Werewolf.Server.Games.Werewolf;

/// <summary>狼人杀内部阶段机（游戏私有；框架不再感知阶段）。</summary>
public enum Phase { Waiting, Night, Day, Discussion, Vote, HunterShoot, GameOver }

/// <summary>
/// 狼人杀玩法：把原 <c>GameRoom</c> 的全部狼人杀逻辑搬迁到这里。
/// 平台只经 <see cref="IGameContext"/> 暴露原语（玩家快照 / 广播 / 私聊 / 状态推送 / 配置），
/// 本实例不引用任何平台实现类型。
///
/// 锁约定：<c>_sync</c> 只保护本实例的私有状态（阶段 / 夜晚目标 / 各玩家技能数据）；
/// 持有 <c>_sync</c> 期间绝不调用 <see cref="IGameContext"/>（其内部会取房间锁），
/// 以保证全局锁顺序恒为「房间锁 → 游戏锁」，避免死锁。
/// </summary>
public sealed class WerewolfGameInstance : IGameInstance
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IGameContext _ctx;
    private readonly object _sync = new();
    private readonly Random _rng = new();

    /// <summary>username → 狼人杀私有状态。与 <see cref="IGameContext.Players"/> 按用户名对齐。</summary>
    private readonly Dictionary<string, WerewolfPlayer> _wp = new(StringComparer.OrdinalIgnoreCase);

    private readonly GameRules _rules;
    private readonly List<Role> _nightOrder;

    private Phase _phase = Phase.Waiting;
    private int _day;
    private bool _running;
    private GameStatus _status = GameStatus.Waiting;

    /// <summary>夜晚当前轮到哪个角色行动；null 表示没有角色正在等待（白天 / 等待 / 结束）。</summary>
    private Role? _actingRole;

    private int _wolfTarget = -1;
    private int _guardTarget = -1;
    private int _witchHealTarget = -1;
    private int _witchPoisonTarget = -1;

    public WerewolfGameInstance(GameInfo info, IGameContext context)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _rules = context.Config.GetSection<GameRules>("GameRules") ?? new GameRules();
        _nightOrder = RoleInfo.ResolveNightOrder(_rules.NightOrder);
    }

    /// <inheritdoc />
    public GameInfo Info { get; }

    /// <inheritdoc />
    public string GameId => Info.GameId;

    /// <inheritdoc />
    public GameStatus Status { get { lock (_sync) return _status; } }

    // ==================== 开局 / 清场 ====================

    /// <inheritdoc />
    public (bool ok, string msg) Start()
    {
        lock (_sync)
        {
            if (_status == GameStatus.Running) return (false, "游戏已开始");
            if (_phase == Phase.GameOver) ResetInternal();   // 重开：先清上一局的临时数据
        }

        var players = SnapshotPlayers();

        lock (_sync)
        {
            // 人数上下限：优先用本游戏经配置下发的规则（与客户端门禁一致，运维可调），
            // 规则未提供（0）时回退到游戏元数据 Info 的声明值。
            var minPlayers = _rules.MinPlayers > 0 ? _rules.MinPlayers : Info.MinPlayers;
            var maxPlayers = _rules.MaxPlayers > 0 ? _rules.MaxPlayers : Info.MaxPlayers;

            if (players.Count < minPlayers) return (false, $"至少需要 {minPlayers} 名玩家");
            if (players.Count > maxPlayers) return (false, "玩家过多");

            foreach (var w in players)
            {
                w.IsAlive = true;
                w.HasAntidote = true;
                w.HasPoison = true;
                w.LastGuardTarget = -1;
                w.NightTarget = -1;
                w.NightActionType = null;
                w.NightActionSubmitted = false;
                w.VoteTarget = -1;
                w.VoteSubmitted = false;
                w.HunterCanShoot = false;
                w.HunterShotSubmitted = false;
            }

            AssignRoles(players);
            _day = 0;
            _phase = Phase.Waiting;
            _running = true;
            _status = GameStatus.Running;
            return (true, "");
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_sync) ResetInternal();
    }

    /// <summary>清场（保留名单 / 房主）。调用方需已持有 <c>_sync</c>。</summary>
    private void ResetInternal()
    {
        _wolfTarget = _guardTarget = _witchHealTarget = _witchPoisonTarget = -1;
        _actingRole = null;
        _day = 0;
        _phase = Phase.Waiting;
        _running = false;
        _status = GameStatus.Waiting;

        foreach (var w in _wp.Values)
        {
            w.NightTarget = -1;
            w.NightActionType = null;
            w.NightActionSubmitted = false;
            w.VoteTarget = -1;
            w.VoteSubmitted = false;
            w.HunterCanShoot = false;
            w.HunterShotSubmitted = false;
        }
    }

    private void AssignRoles(List<WerewolfPlayer> players)
    {
        int n = players.Count;
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
        for (int i = 0; i < players.Count; i++)
            players[i].Role = roles[i];
    }

    // ==================== 玩家输入（本游戏 op） ====================

    /// <inheritdoc />
    public (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId)
    {
        lock (_sync)
        {
            if (_status != GameStatus.Running) return (false, "游戏未开始");
        }

        switch (op)
        {
            case WerewolfOps.Night:
            {
                var act = payload?.Deserialize<NightPayload>(Json) ?? new NightPayload();
                return SubmitNightAction(connectionId, act.TargetId, act.Action ?? "do");
            }
            case WerewolfOps.Vote:
            {
                var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
                return SubmitVote(connectionId, act.TargetId);
            }
            case WerewolfOps.Hunter:
            {
                var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
                return SubmitHunterShot(connectionId, act.TargetId);
            }
            default:
                return (false, "未知操作");
        }
    }

    private (bool ok, string msg) SubmitNightAction(string connId, int targetId, string action)
    {
        var me = FindWpByConnection(connId);

        lock (_sync)
        {
            if (_phase != Phase.Night) return (false, "当前不是夜晚");
            if (me == null || !me.IsAlive) return (false, "你当前无法行动");
            // 只有“当前轮到行动的角色”才能提交技能，未轮到时一律忽略。
            if (_actingRole == null || me.Role != _actingRole.Value) return (false, "还没轮到你行动");

            me.NightTarget = targetId;
            me.NightActionType = action;
            me.NightActionSubmitted = true;
            return (true, "");
        }
    }

    private (bool ok, string msg) SubmitVote(string connId, int targetId)
    {
        var me = FindWpByConnection(connId);
        var targetAlive = targetId < 0 || IsAlive(targetId);

        lock (_sync)
        {
            if (_phase != Phase.Vote) return (false, "当前不是投票阶段");
            if (me == null || !me.IsAlive) return (false, "你当前无法投票");
            if (!targetAlive) return (false, "目标已出局");

            me.VoteTarget = targetId;
            me.VoteSubmitted = true;
            return (true, "");
        }
    }

    private (bool ok, string msg) SubmitHunterShot(string connId, int targetId)
    {
        var me = FindWpByConnection(connId);
        var targetAlive = targetId < 0 || IsAlive(targetId);

        lock (_sync)
        {
            if (me == null || !me.HunterCanShoot || me.HunterShotSubmitted) return (false, "当前无法开枪");
            if (!targetAlive) return (false, "目标已出局");

            me.NightTarget = targetId;
            me.HunterShotSubmitted = true;
            return (true, "");
        }
    }

    // ==================== 玩家变更通知 ====================

    /// <inheritdoc />
    public void OnPlayerLeft(RoomPlayer player, bool stillInGame)
    {
        if (stillInGame) return;   // 局中仅标记断线，数据保留
        lock (_sync) _wp.Remove(player.Username);
    }

    /// <inheritdoc />
    public void OnPlayerReconnected(RoomPlayer player)
    {
        if (Status != GameStatus.Running) return;

        WerewolfPlayer? w;
        lock (_sync) w = _wp.TryGetValue(player.Username, out var found) ? found : null;
        if (w != null) _ = SendRoleAsync(w);   // 局中重连：补发身份私聊
    }

    // ==================== 主循环 ====================

    /// <inheritdoc />
    public async Task RunAsync()
    {
        try
        {
            await SendAllRolesAsync();
            await _ctx.BroadcastAsync("🎮 游戏开始！");
            await _ctx.BroadcastStateAsync();

            while (true)
            {
                lock (_sync) { if (!_running) break; _day++; }

                await NightPhaseAsync();
                if (await CheckGameOverAsync()) break;

                await DayPhaseAsync();
                if (await CheckGameOverAsync()) break;
            }
        }
        catch (Exception ex)
        {
            await _ctx.BroadcastAsync("⚠️ 服务器错误：" + ex.Message);
        }
        finally
        {
            lock (_sync) { _running = false; _phase = Phase.GameOver; _status = GameStatus.GameOver; }
            await BroadcastAllRolesAsync();
            await _ctx.BroadcastStateAsync();
        }
    }

    // ==================== 夜晚 ====================

    private async Task NightPhaseAsync()
    {
        ResetNightData();
        SetPhase(Phase.Night);
        await _ctx.BroadcastAsync($"════ 第 {_day} 夜 · 天黑请闭眼 ════");
        await _ctx.BroadcastStateAsync();

        List<Role> order;
        lock (_sync) order = new List<Role>(_nightOrder);

        // 夜晚顺序由规则配置驱动（默认 守卫 → 狼人 → 女巫 → 预言家）。
        foreach (var role in order)
            await NightStepAsync(role);
    }

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
        int t;
        lock (_sync) t = guard.NightTarget;

        if (t >= 0 && t != guard.LastGuardTarget && IsAlive(t))
        {
            lock (_sync) { _guardTarget = t; guard.LastGuardTarget = t; }
            await _ctx.PrivateAsync(guard.ConnectionId, $"你守护了 [{t}]{NameOf(t)}。");
        }
        else
        {
            lock (_sync) guard.LastGuardTarget = -1;
            await _ctx.PrivateAsync(guard.ConnectionId, "守护无效（不能连守同一人 / 目标已死亡），今晚你未守护任何人。");
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
        if (t >= 0 && IsAlive(t)) { lock (_sync) _wolfTarget = t; }

        int wolfTarget;
        lock (_sync) wolfTarget = _wolfTarget;

        await PrivateToAsync(wolves, wolfTarget >= 0
            ? $"你们选择了击杀 [{wolfTarget}]{NameOf(wolfTarget)}。"
            : "你们今晚放弃了击杀。");
    }

    /// <summary>女巫：可使用解药救活被击杀者，或用毒药毒杀一名玩家。</summary>
    private async Task WitchStepAsync()
    {
        var witch = AliveWithRole(Role.Witch).FirstOrDefault();
        if (witch == null) return;

        int wolfTarget;
        bool hasAntidote, hasPoison;
        lock (_sync) { wolfTarget = _wolfTarget; hasAntidote = witch.HasAntidote; hasPoison = witch.HasPoison; }

        string killed = wolfTarget >= 0
            ? $"今晚 [{wolfTarget}]{NameOf(wolfTarget)} 被狼人击杀。"
            : "今晚没有人被狼人击杀。";
        await _ctx.PrivateAsync(witch.ConnectionId,
            $"【女巫】{killed}\n解药：{(hasAntidote ? "可用" : "已用")}　毒药：{(hasPoison ? "可用" : "已用")}\n" +
            "输入 heal 使用解药 / poison <编号> 使用毒药 / none 不使用。");

        await WaitNightActionAsync(Role.Witch, _rules.NightActionMs);

        string? act;
        int nightTarget;
        lock (_sync) { act = witch.NightActionType; nightTarget = witch.NightTarget; }

        if (act == "heal" && witch.HasAntidote && wolfTarget >= 0)
        {
            lock (_sync) { _witchHealTarget = wolfTarget; witch.HasAntidote = false; }
            await _ctx.PrivateAsync(witch.ConnectionId, $"你使用了解药，救活了 [{wolfTarget}]{NameOf(wolfTarget)}。");
        }
        else if (act == "poison" && witch.HasPoison && nightTarget >= 0 && IsAlive(nightTarget))
        {
            lock (_sync) { _witchPoisonTarget = nightTarget; witch.HasPoison = false; }
            await _ctx.PrivateAsync(witch.ConnectionId, $"你使用了毒药，毒杀了 [{_witchPoisonTarget}]{NameOf(_witchPoisonTarget)}。");
        }
        else
        {
            await _ctx.PrivateAsync(witch.ConnectionId, "你今晚没有使用任何药水。");
        }
    }

    /// <summary>预言家：查验一名玩家的阵营。</summary>
    private async Task SeerStepAsync()
    {
        var seer = AliveWithRole(Role.Seer).FirstOrDefault();
        if (seer == null) return;

        await _ctx.PrivateAsync(seer.ConnectionId, "【预言家】请选择你要查验的玩家编号。");
        await WaitNightActionAsync(Role.Seer, _rules.NightActionMs);

        int t;
        lock (_sync) t = seer.NightTarget;

        if (t >= 0 && IsAlive(t))
        {
            var target = PlayerById(t)!;
            await _ctx.PrivateAsync(seer.ConnectionId,
                $"🔍 查验结果：[{t}]{target.Username} 是 【{(target.IsWolf ? "狼人" : "好人")}】。");
        }
        else
        {
            await _ctx.PrivateAsync(seer.ConnectionId, "你今晚没有查验任何人。");
        }
    }

    // ==================== 白天 ====================

    private async Task DayPhaseAsync()
    {
        SetPhase(Phase.Day);

        var deaths = new List<int>();
        int wolfTarget, guardTarget, healTarget, poisonTarget;
        lock (_sync)
        {
            wolfTarget = _wolfTarget;
            guardTarget = _guardTarget;
            healTarget = _witchHealTarget;
            poisonTarget = _witchPoisonTarget;
        }

        if (wolfTarget >= 0)
        {
            bool saved = guardTarget == wolfTarget || healTarget == wolfTarget;
            if (!saved) deaths.Add(wolfTarget);
        }
        if (poisonTarget >= 0 && !deaths.Contains(poisonTarget))
            deaths.Add(poisonTarget);

        foreach (var id in deaths)
        {
            var p = PlayerById(id);
            if (p != null) p.IsAlive = false;
        }

        await _ctx.BroadcastAsync($"════ 第 {_day} 天 · 天亮了 ════");
        await _ctx.BroadcastAsync(deaths.Count == 0
            ? "🌤 昨晚是平安夜，没有人死亡。"
            : $"💀 昨晚死亡的是：{string.Join("、", deaths.Select(d => $"[{d}]{NameOf(d)}"))}");

        foreach (var id in deaths)
        {
            var p = PlayerById(id);
            if (p != null && p.Role == Role.Hunter && id != poisonTarget)
                await HunterShootAsync(p);
        }

        if (await CheckGameOverAsync()) return;

        SetPhase(Phase.Discussion);
        await _ctx.BroadcastAsync($"💬 自由讨论时间（{_rules.DiscussionSeconds} 秒），输入 `chat 内容` 发言。");
        await _ctx.BroadcastStateAsync();
        await Task.Delay(_rules.DiscussionMs);

        if (await CheckGameOverAsync()) return;

        await VotePhaseAsync();
    }

    private async Task VotePhaseAsync()
    {
        lock (_sync)
        {
            foreach (var w in _wp.Values) { w.VoteTarget = -1; w.VoteSubmitted = false; }
        }

        SetPhase(Phase.Vote);
        await _ctx.BroadcastAsync("🗳 请投票放逐一名玩家（输入编号，输入 -1 弃票）。");
        await _ctx.BroadcastStateAsync();

        await WaitUntilAsync(() =>
        {
            var alive = SnapshotPlayers().Where(w => w.IsAlive && w.IsConnected).ToList();
            return alive.Count == 0 || alive.All(w => w.VoteSubmitted);
        }, _rules.VoteMs);

        Dictionary<int, int> tally;
        lock (_sync)
        {
            tally = _wp.Values
                .Where(w => w.IsAlive && w.VoteTarget >= 0)
                .GroupBy(w => w.VoteTarget)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        if (tally.Count == 0)
        {
            await _ctx.BroadcastAsync("全员弃票，无人被放逐。");
            return;
        }

        await _ctx.BroadcastAsync("📊 投票结果：" + string.Join("　",
            tally.OrderByDescending(kv => kv.Value).Select(kv => $"[{kv.Key}]{NameOf(kv.Key)} {kv.Value}票")));

        int max = tally.Values.Max();
        var top = tally.Where(kv => kv.Value == max).Select(kv => kv.Key).ToList();

        if (top.Count > 1)
        {
            await _ctx.BroadcastAsync("⚖️ 平票，无人被放逐。");
            await _ctx.BroadcastStateAsync();
            return;
        }

        int outId = top[0];
        var outP = PlayerById(outId);
        if (outP == null) return;
        lock (_sync) outP.IsAlive = false;
        await _ctx.BroadcastAsync($"⚖️ [{outId}]{outP.Username} 被投票放逐。");

        if (outP.Role == Role.Hunter)
            await HunterShootAsync(outP);

        await _ctx.BroadcastStateAsync();
    }

    // ==================== 猎人开枪 ====================

    private async Task HunterShootAsync(WerewolfPlayer hunter)
    {
        lock (_sync)
        {
            hunter.HunterCanShoot = true;
            hunter.HunterShotSubmitted = false;
            hunter.NightTarget = -1;
        }

        SetPhase(Phase.HunterShoot);
        await _ctx.BroadcastAsync($"💥 [{IdOf(hunter)}]{hunter.Username} 是猎人！可以选择开枪带走一名玩家。");
        await _ctx.PrivateAsync(hunter.ConnectionId, "【猎人】请选择你要开枪带走的玩家编号，或输入 -1 放弃。");
        await _ctx.BroadcastStateAsync();

        await WaitUntilAsync(() =>
        {
            lock (_sync) return hunter.HunterShotSubmitted;
        }, _rules.HunterShotMs);

        int target;
        lock (_sync) { hunter.HunterCanShoot = false; target = hunter.NightTarget; }

        if (target >= 0 && IsAlive(target))
        {
            var t = PlayerById(target);
            if (t != null) lock (_sync) t.IsAlive = false;
            await _ctx.BroadcastAsync($"🔫 猎人 [{IdOf(hunter)}]{hunter.Username} 开枪带走了 [{target}]{t?.Username}。");
        }
        else
        {
            await _ctx.BroadcastAsync("猎人放弃了开枪。");
        }
        await _ctx.BroadcastStateAsync();
    }

    // ==================== 胜负判定 ====================

    private async Task<bool> CheckGameOverAsync()
    {
        var snap = SnapshotPlayers();
        int wolves = snap.Count(w => w.IsAlive && w.Role == Role.Werewolf);
        int others = snap.Count(w => w.IsAlive && w.Role != Role.Werewolf);

        if (wolves == 0)
        {
            lock (_sync) { _running = false; _status = GameStatus.GameOver; }
            await _ctx.BroadcastAsync("🎉 所有狼人已被消灭，【好人阵营】获胜！");
            return true;
        }
        if (wolves >= others)
        {
            lock (_sync) { _running = false; _status = GameStatus.GameOver; }
            await _ctx.BroadcastAsync("🐺 狼人数量已达到或超过好人，【狼人阵营】获胜！");
            return true;
        }
        return false;
    }

    // ==================== 状态构建（视角隔离） ====================

    /// <inheritdoc />
    public JsonNode? BuildPayload(string viewerConnectionId)
    {
        var me = _ctx.FindByConnection(viewerConnectionId);
        if (me == null) return null;

        WerewolfPlayer? w;
        Phase phase;
        int day;
        Role? actingRole;
        GameRules rules;
        lock (_sync)
        {
            w = _wp.TryGetValue(me.Username, out var found) ? found : null;
            phase = _phase;
            day = _day;
            actingRole = _actingRole;
            rules = _rules;
        }
        if (w == null) return null;

        var payload = new WerewolfGameState
        {
            Phase = phase.ToString(),
            Day = day,
            YourRole = RoleInfo.Name(w.Role),
            YourRoleDesc = RoleInfo.Desc(w.Role),
            ActingRole = actingRole.HasValue ? RoleInfo.Name(actingRole.Value) : "",
            Rules = rules
        };
        return JsonSerializer.SerializeToNode(payload, Json);
    }

    /// <inheritdoc />
    public void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername)
    {
        // 先与平台玩家对齐（未开局时身份为默认平民），保证「等待中」也保持旧观感。
        SnapshotPlayers();

        WerewolfPlayer? viewer;
        bool revealAll;
        lock (_sync)
        {
            viewer = _wp.TryGetValue(viewerUsername, out var v) ? v : null;
            revealAll = !_running || _phase == Phase.GameOver;
        }

        foreach (var row in rows)
        {
            WerewolfPlayer? w;
            lock (_sync) w = _wp.TryGetValue(row.Name, out var found) ? found : null;

            if (w == null)
            {
                row.Tag = null;
                row.TagColor = null;
                continue;
            }

            bool show = revealAll
                        || (viewer != null && string.Equals(viewer.Username, row.Name, StringComparison.OrdinalIgnoreCase))
                        || (viewer != null && viewer.IsWolf && w.IsWolf);

            row.Tag = show ? RoleInfo.Name(w.Role) : null;
            row.TagColor = show ? RoleInfo.ColorHex(w.Role) : null;
        }
    }

    // ==================== 辅助 ====================

    private void ResetNightData()
    {
        lock (_sync)
        {
            _wolfTarget = _guardTarget = _witchHealTarget = _witchPoisonTarget = -1;
            foreach (var w in _wp.Values)
            {
                w.NightTarget = -1;
                w.NightActionType = null;
                w.NightActionSubmitted = false;
            }
        }
    }

    private void SetPhase(Phase p) { lock (_sync) _phase = p; }

    /// <summary>
    /// 读取一次平台玩家快照，并与本实例的狼人私有状态按用户名对齐（补新 / 清旧）。
    /// 返回的列表顺序与 <see cref="IGameContext.Players"/> 一致（即玩家编号）。调用方不得持有 <c>_sync</c>。
    /// </summary>
    private List<WerewolfPlayer> SnapshotPlayers()
    {
        var roomPlayers = _ctx.Players;
        lock (_sync)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rp in roomPlayers)
            {
                seen.Add(rp.Username);
                if (_wp.TryGetValue(rp.Username, out var w)) w.Player = rp;
                else _wp[rp.Username] = new WerewolfPlayer(rp);
            }

            if (_wp.Count != seen.Count)
            {
                foreach (var name in _wp.Keys.Where(k => !seen.Contains(k)).ToList())
                    _wp.Remove(name);
            }

            return roomPlayers.Select(rp => _wp[rp.Username]).ToList();
        }
    }

    private WerewolfPlayer? FindWpByConnection(string connId)
    {
        var rp = _ctx.FindByConnection(connId);
        if (rp == null) return null;
        lock (_sync) return _wp.TryGetValue(rp.Username, out var w) ? w : null;
    }

    private WerewolfPlayer? PlayerById(int id)
    {
        var list = SnapshotPlayers();
        return (id >= 0 && id < list.Count) ? list[id] : null;
    }

    private bool IsAlive(int id)
    {
        var ps = _ctx.Players;
        return id >= 0 && id < ps.Count && ps[id].IsAlive;
    }

    private string NameOf(int id) => PlayerById(id)?.Username ?? "?";

    private int IdOf(WerewolfPlayer w)
    {
        var ps = _ctx.Players;
        for (int i = 0; i < ps.Count; i++)
            if (string.Equals(ps[i].Username, w.Username, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private List<WerewolfPlayer> AliveWithRole(Role role)
        => SnapshotPlayers().Where(w => w.IsAlive && w.Role == role).ToList();

    private int PickMostVotedNight(Role role)
    {
        var grouped = SnapshotPlayers()
            .Where(w => w.IsAlive && w.Role == role && w.NightTarget >= 0)
            .GroupBy(w => w.NightTarget)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        if (grouped.Count == 0) return -1;
        int max = grouped[0].Count;
        var top = grouped.Where(x => x.Count == max).ToList();
        return top[_rng.Next(top.Count)].Id;
    }

    private async Task WaitNightActionAsync(Role role, int timeoutMs)
    {
        // 进入等待前标记“当前轮到该角色”，期间其他角色提交的技能会被忽略。
        lock (_sync) _actingRole = role;

        // 广播一次状态，让所有客户端知道现在轮到谁行动（用于“未轮到不能放技能”）。
        await _ctx.BroadcastStateAsync();

        try
        {
            await WaitUntilAsync(() =>
            {
                var actors = SnapshotPlayers().Where(w => w.IsAlive && w.Role == role && w.IsConnected).ToList();
                return actors.Count == 0 || actors.All(w => w.NightActionSubmitted);
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

    private async Task PrivateToAsync(IEnumerable<WerewolfPlayer> players, string message)
    {
        foreach (var w in players) await PrivateAsync(w, message);
    }

    private async Task PrivateAsync(WerewolfPlayer w, string message)
    {
        if (!w.IsConnected || string.IsNullOrEmpty(w.ConnectionId)) return;
        await _ctx.PrivateAsync(w.ConnectionId, message);
    }

    private async Task SendAllRolesAsync()
    {
        var snap = SnapshotPlayers();
        foreach (var w in snap)
            await SendRoleAsync(w);
    }

    private async Task SendRoleAsync(WerewolfPlayer w)
        => await PrivateAsync(w, $"🎭 你的身份是【{RoleInfo.Name(w.Role)}】：{RoleInfo.Desc(w.Role)}");

    private async Task BroadcastAllRolesAsync()
    {
        var snap = SnapshotPlayers();
        var lines = snap.Select((w, i) => $"[{i}]{w.Username} → {RoleInfo.Name(w.Role)}");
        await _ctx.BroadcastAsync("📜 本局身份公开：\n" + string.Join("\n", lines));
    }
}
