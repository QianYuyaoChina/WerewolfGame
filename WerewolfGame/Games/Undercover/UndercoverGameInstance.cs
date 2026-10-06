using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace Werewolf.Server.Games.Undercover;

/// <summary>
/// 谁是卧底玩法实例。规则要点：
/// <list type="bullet">
///   <item>绝大多数玩家拿到**同一个词**（平民），少数玩家拿到**相近的另一个词**（卧底），
///         人多时还会有**白板**（没有词，只能听别人的描述推测）；</item>
///   <item>每轮：所有人用**聊天**描述自己的词（不能说出词本身）→ 投票放逐一人；</item>
///   <item>卧底阵营（卧底 + 白板）全部出局 → 平民胜；卧底阵营人数 ≥ 平民人数 → 卧底阵营胜；</item>
///   <item>出局者的**身份**会被公开（词要到整局结束才揭晓）。</item>
/// </list>
///
/// **视角隔离（v2.3.2）**：玩家**不知道自己是卧底还是平民**——局中 <c>YourRole</c>
/// 恒为空串，只有整局结束才下发真实身份。白板必然知道自己没有词（这是规则本身，不算泄露阵营）。
/// 任何时候都不能从某人视角的负载里推出别人的词或别人的阵营。
///
/// 锁约定与狼人杀一致：<c>_sync</c> 只保护本实例私有状态，持锁期间**绝不**调用 <see cref="IGameContext"/>
/// （其内部会取房间锁），保证锁序恒为「房间锁 → 游戏锁」。
/// </summary>
public sealed class UndercoverGameInstance : IGameInstance
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IGameContext _ctx;
    private readonly object _sync = new();
    private readonly Random _rng = new();
    private readonly UndercoverRules _rules;

    /// <summary>username → 谁是卧底私有状态。</summary>
    private readonly Dictionary<string, UndercoverPlayer> _up = new(StringComparer.OrdinalIgnoreCase);

    private string _phase = UndercoverPhases.Waiting;
    private int _round;
    private bool _running;
    private GameStatus _status = GameStatus.Waiting;

    /// <summary>
    /// 本局**是否已经发牌**。发牌之前谁都没有身份，因此
    /// ① 玩家列表的 <c>Tag</c> 一律为空（不能写成默认的「平民」）；
    /// ② 私有负载同样不给出身份。
    /// 由 <see cref="Start"/> 置 true，<see cref="ResetInternal"/> 置回 false。
    /// </summary>
    private bool _dealt;

    private string _category = "";
    private string _civilianWord = "";
    private string _undercoverWord = "";

    // ---- 轮流发言（v2.3.1） ----
    /// <summary>当前轮到发言的玩家编号；-1 表示此刻没人需要发言。</summary>
    private int _currentSpeakerId = -1;
    /// <summary>当前轮到发言的玩家名。</summary>
    private string _currentSpeakerName = "";
    /// <summary>当前发言人是否已经发言完毕（发过聊天 或 点了跳过）。</summary>
    private bool _speakerSpoken;
    /// <summary>本轮已经发言完毕的玩家名。</summary>
    private readonly List<string> _spoken = new();
    /// <summary>本轮需要发言的人数。</summary>
    private int _speakerCount;

    public UndercoverGameInstance(GameInfo info, IGameContext context)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _rules = context.Config.GetSection<UndercoverRules>("Undercover") ?? new UndercoverRules();
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
            if (_phase == UndercoverPhases.GameOver) ResetInternal();
        }

        var players = SnapshotPlayers();

        // 人数下限兜底到 3：少于 3 人时「平民 > 卧底阵营」在数学上不可能成立
        // （n=1 → 平民 0 人；n=2 → 1:1 平手，开局瞬间即判卧底阵营胜）。
        // 即使有人把 appsettings.json 的 Undercover:MinPlayers 配成 1 / 2，这里也要挡住。
        var minPlayers = Math.Max(MinPlayablePlayers, _rules.MinPlayers > 0 ? _rules.MinPlayers : Info.MinPlayers);
        var maxPlayers = _ctx.MaxPlayers;   // 房间实际上限（房主可调）

        if (players.Count < minPlayers) return (false, UndercoverMessages.NeedMorePlayers(minPlayers));
        if (players.Count > maxPlayers) return (false, $"玩家过多（房间上限 {maxPlayers} 人）");

        // 词库（缺失时自动生成示例并警告）。
        var pairs = LoadWordBank();
        if (pairs.Count == 0)
            return (false, "词库为空：请在服务器目录的 words.undercover.txt 里按「平民词|卧底词」添加词条后重试。");

        // 铁律兜底（第二道闸门）：配额本身也要验一次，不满足一律拒绝开局。
        // 这里**不改** ComputeRoleCounts 的算法（它是被穷举验证过的纯函数），只负责拦。
        var (undercover, blank) = ComputeRoleCounts(players.Count, _rules);
        var civilian = players.Count - undercover - blank;
        if (undercover < 1 || civilian <= undercover + blank)
        {
            var needed = MinBalancedPlayers(_rules);
            return needed > 0
                ? (false, UndercoverMessages.CannotBalanceWithHint(
                    players.Count, undercover, blank, civilian, Math.Max(needed, minPlayers)))
                : (false, UndercoverMessages.CannotBalance(players.Count, undercover, blank, civilian));
        }

        lock (_sync)
        {
            var pair = pairs[_rng.Next(pairs.Count)];
            _civilianWord = pair.Civilian;
            _undercoverWord = pair.Undercover;
            _category = pair.Category;

            // 洗牌决定谁是卧底 / 白板。
            var order = players.OrderBy(_ => _rng.Next()).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                var w = order[i];
                var role = i < undercover ? UndercoverRoles.Undercover
                         : i < undercover + blank ? UndercoverRoles.Blank
                         : UndercoverRoles.Civilian;
                w.Role = role;
                w.Word = role == UndercoverRoles.Undercover ? _undercoverWord
                       : role == UndercoverRoles.Blank ? ""
                       : _civilianWord;
                w.VoteTarget = -1;
                w.VoteSubmitted = false;
                w.ReadySubmitted = false;
                w.IsAlive = true;
            }

            _round = 0;
            _phase = UndercoverPhases.Waiting;
            _running = true;
            _status = GameStatus.Running;
            _dealt = true;              // 至此才有身份可言（发牌前不展示任何身份）

            // 轮流发言的临时状态也要清干净（否则重开一局会沿用上一局的发言人）。
            _currentSpeakerId = -1;
            _currentSpeakerName = "";
            _speakerSpoken = false;
            _spoken.Clear();
            _speakerCount = 0;
        }

        return (true, "");
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_sync) ResetInternal();
    }

    /// <summary>
    /// 按人数计算「卧底 / 白板」数量（纯函数，发牌与测试共用，便于把发牌结果当规格来校验）。
    ///
    /// **铁律：<c>平民数 &gt; 卧底数 + 白板数</c>** —— 否则开局即达成「卧底阵营胜」，这局毫无意义。
    /// 不满足时先减白板（减到 0 为止），再按既有逻辑 clamp 卧底数；最后再兜底一次，绝不违反铁律。
    /// </summary>
    /// <param name="playerCount">参与玩家数。</param>
    /// <param name="rules">生效规则；null 表示用内置默认值。</param>
    /// <returns>(卧底数, 白板数)。</returns>
    public static (int Undercover, int Blank) ComputeRoleCounts(int playerCount, UndercoverRules? rules = null)
    {
        if (playerCount <= 0) return (0, 0);

        var r = rules ?? new UndercoverRules();

        // 卧底阵营的人数上限：卧底 + 白板 &lt; 人数 / 2 ⟺ 卧底 + 白板 ≤ (人数 - 1) / 2。
        var campCap = Math.Max(1, (playerCount - 1) / 2);

        var blank = BaseBlankCount(playerCount, r);
        var roomForUndercover = campCap - blank;

        if (roomForUndercover < 1)
        {
            // 白板挤占了卧底的配额：先把白板减到能留出至少 1 个卧底为止。
            blank = Math.Max(0, campCap - 1);
            roomForUndercover = campCap - blank;
        }

        var undercover = Math.Min(BaseUndercoverCount(playerCount), Math.Max(1, roomForUndercover));

        // 兜底：仍然超配额就再减白板（铁律优先于白板数量）。
        if (undercover + blank > campCap) blank = Math.Max(0, campCap - undercover);

        return (undercover, blank);
    }

    /// <summary>
    /// 开局的人数下限（无论配置怎么写都不低于这个值）。
    /// 少于 3 人时 <see cref="ComputeRoleCounts"/> 不可能满足「平民 &gt; 卧底阵营」。
    /// </summary>
    public const int MinPlayablePlayers = 3;

    /// <summary>
    /// 能满足铁律「平民 &gt; 卧底阵营 且 卧底 ≥ 1」的**最少人数**；找不到返回 -1。
    /// 只在开局被拒绝时用来给房主一句「至少还要加几个人」的提示。
    /// </summary>
    public static int MinBalancedPlayers(UndercoverRules? rules = null, int probeUpTo = 64)
    {
        for (var n = MinPlayablePlayers; n <= probeUpTo; n++)
        {
            var (u, b) = ComputeRoleCounts(n, rules);
            var c = n - u - b;
            if (u >= 1 && c > u + b) return n;
        }
        return -1;
    }

    /// <summary>卧底数量的既有增长曲线（5 人以内 1 个，8 人以内 2 个，再多 3 个）。</summary>
    private static int BaseUndercoverCount(int playerCount)
        => playerCount <= 5 ? 1 : playerCount <= 8 ? 2 : 3;

    /// <summary>白板数量：由 <see cref="UndercoverRules.BlankMinPlayers"/> 两个阈值决定 0 / 1 / 2。</summary>
    private static int BaseBlankCount(int playerCount, UndercoverRules r)
    {
        var one = r.BlankMinPlayers;
        if (one <= 0) return 0;                       // 关闭白板

        var two = r.BlankTwoMinPlayers > one ? r.BlankTwoMinPlayers : int.MaxValue;
        return playerCount >= two ? 2 : playerCount >= one ? 1 : 0;
    }

    /// <summary>清场（保留名单 / 房主）。调用方需已持有 <c>_sync</c>。</summary>
    private void ResetInternal()
    {
        _phase = UndercoverPhases.Waiting;
        _round = 0;
        _running = false;
        _status = GameStatus.Waiting;
        _dealt = false;
        _civilianWord = "";
        _undercoverWord = "";
        _category = "";
        _currentSpeakerId = -1;
        _currentSpeakerName = "";
        _speakerSpoken = false;
        _spoken.Clear();
        _speakerCount = 0;

        foreach (var w in _up.Values)
        {
            w.Word = "";
            w.VoteTarget = -1;
            w.VoteSubmitted = false;
            w.ReadySubmitted = false;
        }
    }

    /// <summary>
    /// 读取词库：优先用工作目录里的文件；缺失时用**程序内置默认词库**生成一份并打印警告。
    /// 工作目录只读（写不进去）时也不至于开不了局——此时直接用内置那份，不落盘。
    /// </summary>
    private List<UndercoverWordBank.Pair> LoadWordBank()
    {
        var file = (_rules.WordFile ?? "").Trim();
        if (file.Length == 0) file = "words.undercover.txt";
        var path = Path.IsPathRooted(file) ? file : Path.Combine(Directory.GetCurrentDirectory(), file);

        var warning = UndercoverWordBank.EnsureSample(path);
        if (warning != null) Console.WriteLine(warning);

        return UndercoverWordBank.LoadOrDefault(path);
    }

    // ==================== 玩家输入 ====================

    /// <inheritdoc />
    public (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId)
    {
        lock (_sync)
        {
            if (_status != GameStatus.Running) return (false, "游戏未开始");
        }

        switch (op)
        {
            case UndercoverOps.Vote:
            {
                var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
                return SubmitVote(connectionId, act.TargetId);
            }
            case UndercoverOps.Skip:
            {
                var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
                return SubmitSkip(connectionId, act.TargetId >= 0);
            }
            default:
                return (false, "未知操作");
        }
    }

    private (bool ok, string msg) SubmitVote(string connId, int targetId)
    {
        var me = FindByConnection(connId);
        var players = _ctx.Players;   // 先取房间快照，再进游戏锁
        var targetAlive = targetId < 0 || (targetId < players.Count && players[targetId].IsAlive);
        var myId = me == null ? -1 : IdOf(me);

        lock (_sync)
        {
            if (_phase != UndercoverPhases.Vote) return (false, "当前不是投票阶段");
            if (me == null || !me.IsAlive) return (false, "你当前无法投票");
            if (!targetAlive) return (false, "目标已出局");
            if (targetId >= 0 && targetId == myId) return (false, "不能投票给自己");

            me.VoteTarget = targetId;
            me.VoteSubmitted = true;
            return (true, "");
        }
    }

    /// <summary>轮到发言的人跳过本轮（不想描述）。</summary>
    private (bool ok, string msg) SubmitSkip(string connId, bool skip)
    {
        var me = FindByConnection(connId);

        lock (_sync)
        {
            if (_phase != UndercoverPhases.Describe) return (false, "当前不是描述阶段");
            if (me == null || !me.IsAlive) return (false, "你当前无法发言");
            if (!string.Equals(_currentSpeakerName, me.Username, StringComparison.OrdinalIgnoreCase))
                return (false, "还没轮到你发言");
            if (_speakerSpoken && !skip) return (false, "你已经发言过了");

            _speakerSpoken = true;
            return (true, "");
        }
    }

    /// <summary>
    /// 平台在广播聊天**之前**先问一次：**只有当前发言人**的发言才放行（其余人插话会被拒绝）。
    /// 当前发言人一发言即视为本轮发言完成，主循环随即轮到下一位。
    /// 注意：本方法由网络层线程调用，持锁期间绝不调用 <see cref="IGameContext"/>（锁序「房间锁 → 游戏锁」）。
    /// </summary>
    public (bool ok, string msg) OnPlayerChat(RoomPlayer player, string text)
    {
        lock (_sync)
        {
            if (_phase != UndercoverPhases.Describe) return (true, "");
            if (_speakerSpoken) return (true, "");   // 本轮已结束（或没人轮到），自由聊天
            if (_currentSpeakerName.Length == 0) return (true, "");

            if (!string.Equals(_currentSpeakerName, player.Username, StringComparison.OrdinalIgnoreCase))
                return (false, $"还没轮到你发言（当前是 {_currentSpeakerName}）。");

            _speakerSpoken = true;
            return (true, "");
        }
    }

    // ==================== 玩家变更通知 ====================

    /// <inheritdoc />
    public void OnPlayerLeft(RoomPlayer player, bool stillInGame)
    {
        if (stillInGame) return;   // 局中仅标记断线，数据保留
        lock (_sync) _up.Remove(player.Username);
    }

    /// <inheritdoc />
    public void OnPlayerReconnected(RoomPlayer player)
    {
        if (Status != GameStatus.Running) return;

        UndercoverPlayer? w;
        lock (_sync) w = _up.TryGetValue(player.Username, out var found) ? found : null;
        if (w != null) _ = SendWordAsync(w);   // 局中重连：补发自己的词
    }

    // ==================== 主循环 ====================

    /// <inheritdoc />
    public async Task RunAsync()
    {
        try
        {
            // 先取房间快照（会取房间锁），再进游戏锁 —— 锁序恒为「房间锁 → 游戏锁」。
            var openingPlayers = SnapshotPlayers();
            string summary;
            lock (_sync) summary = RoleSummary(openingPlayers);

            await _ctx.BroadcastAsync($"📋 本局身份配置：{summary}");
            await SendAllWordsAsync();
            await _ctx.BroadcastAsync(
                "🎮 游戏开始！按顺序描述你的词，找出拿到不同词的那个人" +
                "（你不会被告知自己是卧底还是平民，请靠大家的描述自己判断）。");
            await _ctx.BroadcastStateAsync();

            while (true)
            {
                lock (_sync) { if (!_running) break; _round++; }

                await DescribePhaseAsync();
                if (await CheckGameOverAsync()) break;

                await VotePhaseAsync();
                if (await CheckGameOverAsync()) break;
            }
        }
        catch (Exception ex)
        {
            await _ctx.BroadcastAsync("⚠️ 服务器错误：" + ex.Message);
        }
        finally
        {
            lock (_sync) { _running = false; _phase = UndercoverPhases.GameOver; _status = GameStatus.GameOver; }
            await RevealAllAsync();
            await _ctx.BroadcastStateAsync();
        }
    }

    // ==================== 描述（轮流发言） ====================

    private async Task DescribePhaseAsync()
    {
        int round;
        lock (_sync) { _phase = UndercoverPhases.Describe; round = _round; }

        var order = SnapshotPlayers().Where(w => w.IsAlive).ToList();

        lock (_sync)
        {
            _spoken.Clear();
            _speakerCount = order.Count;
            _currentSpeakerId = -1;
            _currentSpeakerName = "";
            _speakerSpoken = false;
        }

        await _ctx.BroadcastAsync(
            $"💬 第 {round} 轮 · 按顺序描述你的词（不要说词本身），每人限时 {_rules.DescribeSeconds} 秒。");
        await _ctx.BroadcastStateAsync();

        foreach (var speaker in order)
        {
            // 轮到他之前如果已经出局（理论上不会发生），跳过。
            if (!AliveByName(speaker.Username)) continue;

            var speakerId = IdOf(speaker);
            lock (_sync)
            {
                _currentSpeakerId = speakerId;
                _currentSpeakerName = speaker.Username;
                _speakerSpoken = false;
            }

            await _ctx.BroadcastAsync($"🎤 轮到 [{speakerId}]{speaker.Username} 描述。");
            await _ctx.BroadcastStateAsync();

            await WaitUntilAsync(() =>
            {
                lock (_sync) return _speakerSpoken;
            }, _rules.DescribeMs);

            bool spoke;
            lock (_sync)
            {
                spoke = _speakerSpoken;
                _spoken.Add(speaker.Username);
            }

            if (!spoke)
                await _ctx.BroadcastAsync($"⌛ [{speakerId}]{speaker.Username} 超时未发言。");
        }

        lock (_sync)
        {
            _currentSpeakerId = -1;
            _currentSpeakerName = "";
            _speakerSpoken = false;
        }

        await _ctx.BroadcastAsync("描述结束，开始投票。");
        await _ctx.BroadcastStateAsync();
    }

    private bool AliveByName(string username)
        => _ctx.Players.Any(p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase) && p.IsAlive);

    // ==================== 投票 ====================

    private async Task VotePhaseAsync()
    {
        lock (_sync)
        {
            _phase = UndercoverPhases.Vote;
            foreach (var w in _up.Values) { w.VoteTarget = -1; w.VoteSubmitted = false; }
        }

        await _ctx.BroadcastAsync($"🗳 请投票选出你认为的卧底（限时 {_rules.VoteSeconds} 秒；-1 弃票）。");
        await _ctx.BroadcastStateAsync();

        await WaitUntilAsync(() =>
        {
            var alive = SnapshotPlayers().Where(w => w.IsAlive && w.IsConnected).ToList();
            return alive.Count == 0 || alive.All(w => w.VoteSubmitted);
        }, _rules.VoteMs);

        Dictionary<int, int> tally;
        lock (_sync)
        {
            tally = _up.Values
                .Where(w => w.IsAlive && w.VoteTarget >= 0)
                .GroupBy(w => w.VoteTarget)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        if (tally.Count == 0)
        {
            await _ctx.BroadcastAsync("全员弃票，本轮无人出局。");
            return;
        }

        await _ctx.BroadcastAsync("📊 投票结果：" + string.Join("　",
            tally.OrderByDescending(kv => kv.Value).Select(kv => $"[{kv.Key}]{NameOf(kv.Key)} {kv.Value}票")));

        var max = tally.Values.Max();
        var top = tally.Where(kv => kv.Value == max).Select(kv => kv.Key).ToList();

        if (top.Count > 1)
        {
            await _ctx.BroadcastAsync("⚖️ 平票，本轮无人出局。");
            await _ctx.BroadcastStateAsync();
            return;
        }

        var outId = top[0];
        var victim = PlayerById(outId);
        if (victim == null) return;

        lock (_sync) victim.IsAlive = false;

        // 出局者的**身份**公开（词要到整局结束才揭晓）。
        await _ctx.BroadcastAsync($"⚖️ [{outId}]{victim.Username} 被投票出局，身份是【{victim.Role}】。");
        if (victim.IsUndercover)
            await _ctx.BroadcastAsync("🎯 抓到一个卧底！");
        else if (victim.IsBlank)
            await _ctx.BroadcastAsync("🎯 抓到一个白板（白板也算卧底阵营）！");
        else
            await _ctx.BroadcastAsync("😵 出局的是自己人…");

        await _ctx.BroadcastStateAsync();
    }

    // ==================== 胜负判定 ====================

    /// <summary>
    /// 胜负判定：**白板计入卧底阵营**。
    /// 卧底阵营（卧底 + 白板）全部出局 → 平民阵营胜；卧底阵营人数不少于平民 → 卧底阵营胜。
    /// </summary>
    private async Task<bool> CheckGameOverAsync()
    {
        var snap = SnapshotPlayers();
        var aliveSpyCamp = snap.Count(w => w.IsAlive && UndercoverRoles.IsSpyCamp(w.Role));
        var aliveCivilian = snap.Count(w => w.IsAlive && w.IsCivilian);

        if (aliveSpyCamp == 0)
        {
            lock (_sync) { _running = false; _status = GameStatus.GameOver; }
            await _ctx.BroadcastAsync("🎉 卧底阵营（卧底 + 白板）全部出局，【平民阵营】获胜！");
            return true;
        }
        if (aliveSpyCamp >= aliveCivilian)
        {
            lock (_sync) { _running = false; _status = GameStatus.GameOver; }
            await _ctx.BroadcastAsync(
                $"🕵️ 卧底阵营还有 {aliveSpyCamp} 人，已不少于平民的 {aliveCivilian} 人，【卧底阵营】获胜！");
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

        UndercoverPlayer? w;
        string phase, summary, speakerName;
        int round, speakerId, speakerCount;
        List<string> spoken;
        bool reveal;
        lock (_sync)
        {
            w = _up.TryGetValue(me.Username, out var found) ? found : null;
            phase = _phase;
            round = _round;
            summary = RoleSummary(SnapshotPlayersLocked());
            speakerId = _currentSpeakerId;
            speakerName = _currentSpeakerName;
            speakerCount = _speakerCount;
            spoken = new List<string>(_spoken);
            // 视角隔离：只有**发牌之后 + 整局结束**才揭晓本人身份；局中一律不下发（否则等于开天眼）。
            reveal = _dealt && (!_running || _phase == UndercoverPhases.GameOver);
        }
        if (w == null) return null;

        var payload = new UndercoverGameState
        {
            Phase = phase,
            Round = round,
            YourWord = w.Word,
            HasWord = w.Word.Length > 0,
            YourRole = reveal ? w.Role : "",
            YourRoleKnown = reveal,
            YourRoleDesc = reveal ? DescOf(w.Role) : UndercoverText.HiddenRoleDesc,
            CategoryHint = _category,
            RoleSummary = summary,
            CurrentSpeakerId = speakerId,
            CurrentSpeakerName = speakerName,
            IsMyTurnToSpeak = speakerName.Length > 0
                               && string.Equals(speakerName, me.Username, StringComparison.OrdinalIgnoreCase),
            SpokenNames = spoken,
            TotalSpeakers = speakerCount,
            Rules = _rules
        };
        return JsonSerializer.SerializeToNode(payload, Json);
    }

    /// <inheritdoc />
    public void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername)
    {
        SnapshotPlayers();

        bool revealAll, dealt;
        lock (_sync)
        {
            revealAll = !_running || _phase == UndercoverPhases.GameOver;
            dealt = _dealt;
        }

        foreach (var row in rows)
        {
            // 发牌之前：谁都还没有身份，标签一律为空。
            // （不能沿用 _up 里 SnapshotPlayers 补出来的默认「平民」——那时这个标签是错的。）
            if (!dealt)
            {
                row.Tag = null;
                row.TagColor = null;
                continue;
            }

            UndercoverPlayer? w;
            lock (_sync) w = _up.TryGetValue(row.Name, out var found) ? found : null;

            if (w == null)
            {
                row.Tag = null;
                row.TagColor = null;
                continue;
            }

            // 存活时身份保密；**出局者的身份公开**；整局结束后全部公开。
            var show = revealAll || !w.IsAlive;
            row.Tag = show ? w.Role : null;
            row.TagColor = show ? UndercoverRoles.ColorHex(w.Role) : null;
        }
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 身份说明文案。**只在整局结束后**才用（局中用 <see cref="UndercoverText.HiddenRoleDesc"/>），
    /// 因此这里写「你是卧底 / 平民」是安全的。
    /// </summary>
    private static string DescOf(string role) => role switch
    {
        UndercoverRoles.Undercover => "你拿到了和别人不同的词。别被发现，把票引向别人。",
        UndercoverRoles.Blank => "你没有词。听别人说，把自己伪装成一个正常人。",
        _ => "你和大多数人拿到同一个词。描述它，并找出那个拿到不同词的人。"
    };

    /// <summary>
    /// 身份配置的**公开**文案（不含具体词，也不含「是谁」）：「🕵️ 卧底×1、👤 平民×5、⬜ 白板×1」。
    /// 公布数量属于玩法的一部分，不泄露任何人的身份。
    /// </summary>
    private static string RoleSummary(List<UndercoverPlayer> players)
    {
        var undercover = players.Count(p => p.IsUndercover);
        var blank = players.Count(p => p.IsBlank);
        var civilian = players.Count - undercover - blank;

        var text = $"{UndercoverRoles.Emoji(UndercoverRoles.Undercover)} 卧底×{undercover}、" +
                   $"{UndercoverRoles.Emoji(UndercoverRoles.Civilian)} 平民×{civilian}";
        if (blank > 0)
            text += $"、{UndercoverRoles.Emoji(UndercoverRoles.Blank)} 白板×{blank}";
        return text;
    }

    /// <summary>供 <see cref="BuildPayload"/> 在已持锁时使用（不重新取房间锁）。</summary>
    private List<UndercoverPlayer> SnapshotPlayersLocked() => _up.Values.ToList();

    /// <summary>
    /// 读取一次平台玩家快照并与本实例私有状态按用户名对齐（补新 / 清旧）。
    /// 返回顺序与 <see cref="IGameContext.Players"/> 一致（即玩家编号）。调用方不得持有 <c>_sync</c>。
    /// </summary>
    private List<UndercoverPlayer> SnapshotPlayers()
    {
        var roomPlayers = _ctx.Players;
        lock (_sync)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rp in roomPlayers)
            {
                seen.Add(rp.Username);
                if (_up.TryGetValue(rp.Username, out var w)) w.Player = rp;
                else _up[rp.Username] = new UndercoverPlayer(rp, UndercoverRoles.Civilian);
            }

            if (_up.Count != seen.Count)
            {
                foreach (var name in _up.Keys.Where(k => !seen.Contains(k)).ToList())
                    _up.Remove(name);
            }

            return roomPlayers.Select(rp => _up[rp.Username]).ToList();
        }
    }

    private UndercoverPlayer? FindByConnection(string connId)
    {
        var rp = _ctx.FindByConnection(connId);
        if (rp == null) return null;
        lock (_sync) return _up.TryGetValue(rp.Username, out var w) ? w : null;
    }

    private UndercoverPlayer? PlayerById(int id)
    {
        var list = SnapshotPlayers();
        return id >= 0 && id < list.Count ? list[id] : null;
    }

    private string NameOf(int id) => PlayerById(id)?.Username ?? "?";

    private int IdOf(UndercoverPlayer w)
    {
        var ps = _ctx.Players;
        for (var i = 0; i < ps.Count; i++)
            if (string.Equals(ps[i].Username, w.Username, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
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

    private async Task SendAllWordsAsync()
    {
        foreach (var w in SnapshotPlayers())
            await SendWordAsync(w);
    }

    /// <summary>私发「你的词」；白板也会收到提示。</summary>
    private async Task SendWordAsync(UndercoverPlayer w)
    {
        if (!w.IsConnected || w.ConnectionId.Length == 0) return;

        var hint = _category.Length > 0 ? $"（提示分类：{_category}）" : "";

        // 注意：私发文案**不能暗示阵营**（否则等于开天眼），因此这里只说「别说出词本身」。
        var text = w.Word.Length > 0
            ? $"🎭 你的词是【{w.Word}】{hint}\n{UndercoverText.HiddenRoleDesc}"
            : $"🎭 你是【{UndercoverRoles.Blank}】：你没有词{hint}。{UndercoverText.BlankSelfHint}";

        await _ctx.PrivateAsync(w.ConnectionId, text);
    }

    /// <summary>整局结束：公开每个人的身份与词。</summary>
    private async Task RevealAllAsync()
    {
        var snap = SnapshotPlayers();
        var lines = snap.Select((w, i) =>
            $"[{i}]{w.Username} → {w.Role}　词：{(w.Word.Length > 0 ? w.Word : UndercoverText.NoWordPlaceholder)}");
        await _ctx.BroadcastAsync("📜 本局揭晓：\n" + string.Join("\n", lines));
    }
}
