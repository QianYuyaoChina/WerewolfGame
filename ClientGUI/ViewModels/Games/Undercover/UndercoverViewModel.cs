using System.Collections.ObjectModel;
using System.Text.Json;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels.Games.Undercover;

/// <summary>
/// 谁是卧底操作面板的视图模型：把游戏私有负载（<see cref="UndercoverGameState"/>，
/// 来自 <see cref="GameStateDto.GamePayload"/>）翻译成操作按钮 / 提示 / 状态胶囊。
///
/// 只依赖宿主桥 <see cref="IGameHostBridge"/>，不感知房间列表、账号、主题等平台模块。
/// 描述阶段直接用宿主的**聊天框**发言（不另造输入框）——这样所有人的描述都留在同一条消息流里。
/// </summary>
public sealed class UndercoverViewModel : ObservableObject, IGameViewModel
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly GameClient _client;
    private readonly IGameHostBridge _bridge;

    private GameStateDto? _state;
    private UndercoverGameState? _payload;
    private UndercoverRules? _rules;

    private string _phase = UndercoverPhases.Waiting;
    private int _round;
    private string _yourWord = "";
    private string _yourRole = "";
    private bool _yourRoleKnown;
    private bool _isBlank;
    private string _yourRoleDesc = "";
    private string _categoryHint = "";
    private string _roleSummary = "";
    private int _yourId;
    private bool _isAlive = true;
    private bool _isJoined;
    private string _ownName = "";
    private string _actionHint = "";
    private bool _voteSubmitted;
    private int _lastVoteTarget = -1;

    // ---- 轮流发言（v2.3.1） ----
    private int _currentSpeakerId = -1;
    private string _currentSpeakerName = "";
    private bool _isMyTurnToSpeak;
    private List<string> _spokenNames = new();
    private int _totalSpeakers;

    public UndercoverViewModel(GameClient client, IGameHostBridge bridge)
    {
        _client = client;
        _bridge = bridge;

        StartGame = new AsyncRelayCommand(_ => StartGameAsync());
        SkipSpeak = new AsyncRelayCommand(_ => SkipSpeakAsync());
        ConfirmVote = new AsyncRelayCommand(_ => VoteAsync(_bridge.SelectedTargetId));
        AbstainVote = new AsyncRelayCommand(_ => VoteAsync(-1));
    }

    // ==================== IGameViewModel ====================

    /// <inheritdoc />
    public string GameId => GameIds.Undercover;

    /// <inheritdoc />
    public IReadOnlyList<GameAction> Actions => _actions;

    /// <summary>操作面板按钮。</summary>
    public ObservableCollection<GameAction> ActionsList { get; } = new();

    private readonly List<GameAction> _actions = new();

    /// <summary>标题栏状态胶囊（轮次 / 阶段）。</summary>
    public ObservableCollection<StatusChip> Chips { get; } = new();

    /// <inheritdoc />
    IReadOnlyList<StatusChip> IGameViewModel.Chips => Chips;

    /// <inheritdoc />
    public string Hint => ActionHint;

    /// <inheritdoc />
    public bool CanSelectPlayer(PlayerRow row)
        => _phase == UndercoverPhases.Vote && _isAlive && row.IsAlive && !row.IsYou && !_voteSubmitted;

    /// <summary>
    /// 只有**轮到自己的发言人**才能在描述阶段发消息；其他阶段自由聊天。
    /// 宿主用它控制聊天输入框与「发送」按钮。
    /// </summary>
    public bool CanSendChat
        => _isJoined && _isAlive
           && (_phase != UndercoverPhases.Describe || _isMyTurnToSpeak);

    /// <inheritdoc />
    public string SendChatHint
    {
        get
        {
            if (!_isJoined) return "";
            if (!_isAlive) return "你已出局，无法发言。";
            if (_phase != UndercoverPhases.Describe) return "";
            if (_isMyTurnToSpeak) return "";
            return _currentSpeakerName.Length > 0
                ? $"等待 {_currentSpeakerName} 发言…"
                : "等待其他人发言…";
        }
    }

    /// <inheritdoc />
    public void OnPlayerSelected(PlayerRow row) => Refresh();

    /// <inheritdoc />
    public void OnState(GameStateDto state)
    {
        if (state == null) return;

        _state = state;
        _payload = TryParsePayload(state);
        _rules = _payload?.Rules;

        var newRound = _payload?.Round != _round || _payload?.Phase != _phase;

        _phase = _payload?.Phase is { Length: > 0 } p ? p : UndercoverPhases.Waiting;
        _round = _payload?.Round ?? 0;
        _yourWord = _payload?.YourWord ?? "";
        _yourRole = _payload?.YourRole ?? "";
        _yourRoleKnown = _payload?.YourRoleKnown ?? false;
        // 白板：负载已下发（局中 / 结算）且自己没有词。未开局时不算，避免误报。
        _isBlank = _payload is { HasWord: false } && _phase != UndercoverPhases.Waiting;
        _yourRoleDesc = _payload?.YourRoleDesc ?? "";
        _categoryHint = _payload?.CategoryHint ?? "";
        _roleSummary = _payload?.RoleSummary ?? "";
        _yourId = state.YourId;
        _isAlive = state.IsAlive;
        _isJoined = !string.IsNullOrEmpty(state.RoomId);
        _ownName = state.Players.FirstOrDefault(p => p.IsYou)?.Name ?? "";

        _currentSpeakerId = _payload?.CurrentSpeakerId ?? -1;
        _currentSpeakerName = _payload?.CurrentSpeakerName ?? "";
        _isMyTurnToSpeak = _payload?.IsMyTurnToSpeak ?? false;
        _spokenNames = _payload?.SpokenNames ?? new List<string>();
        _totalSpeakers = _payload?.TotalSpeakers ?? 0;

        if (newRound)
        {
            _voteSubmitted = false;
            _lastVoteTarget = -1;
        }

        Refresh();
        RaiseDisplayChanged();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _actions.Clear();
        _actions.AddRange(BuildActions());

        ActionsList.Clear();
        foreach (var a in _actions) ActionsList.Add(a);

        ActionHint = BuildHint();
        OnPropertyChanged(nameof(IsActionsEmpty));
        RefreshChips();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _state = null;
        _payload = null;
        _rules = null;
        _phase = UndercoverPhases.Waiting;
        _round = 0;
        _yourWord = "";
        _yourRole = "";
        _yourRoleKnown = false;
        _isBlank = false;
        _yourRoleDesc = "";
        _categoryHint = "";
        _roleSummary = "";
        _yourId = 0;
        _isAlive = true;
        _isJoined = false;
        _ownName = "";
        _voteSubmitted = false;
        _currentSpeakerId = -1;
        _currentSpeakerName = "";
        _isMyTurnToSpeak = false;
        _spokenNames = new List<string>();
        _totalSpeakers = 0;

        _actions.Clear();
        ActionsList.Clear();
        Chips.Clear();
        ActionHint = "";
        RaiseDisplayChanged();
    }

    // ==================== 命令 ====================

    public System.Windows.Input.ICommand StartGame { get; }
    public System.Windows.Input.ICommand SkipSpeak { get; }
    public System.Windows.Input.ICommand ConfirmVote { get; }
    public System.Windows.Input.ICommand AbstainVote { get; }

    private async Task StartGameAsync()
    {
        try
        {
            var r = await _client.StartGameAsync();
            if (!r.ok) _bridge.AppendLog("⚠️ " + r.msg, LogKinds.Error);
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>轮到自己时跳过本轮发言。</summary>
    private async Task SkipSpeakAsync()
    {
        try
        {
            await _client.CallGameOpAsync(UndercoverOps.Skip,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = 1 }, Json));
            _bridge.AppendLog("✅ 本轮跳过发言。");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task VoteAsync(int targetId)
    {
        try
        {
            await _client.CallGameOpAsync(UndercoverOps.Vote,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = targetId }, Json));
            _voteSubmitted = true;
            _lastVoteTarget = targetId;
            _bridge.AppendLog(targetId < 0 ? "✅ 已弃票。" : $"✅ 已投票 → [{targetId}]");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    // ==================== 绑定属性 ====================

    public string Phase => _phase;
    public string PhaseDisplay => UndercoverPhases.DisplayName(_phase);
    public string RoundText => _round <= 0 ? "准备阶段" : $"第 {_round} 轮";

    /// <summary>我的词（白板显示共享文案「（你是白板，没有词）」）。</summary>
    public string YourWordDisplay => _yourWord.Length > 0 ? _yourWord : UndercoverText.BlankWordDisplay;
    public bool HasWord => _yourWord.Length > 0;

    /// <summary>
    /// 我是白板：服务端已下发负载且我没有词。
    /// 白板必然知道自己没有词（这是规则本身，不算泄露阵营）。
    /// </summary>
    public bool IsBlank => _isBlank;

    /// <summary>白板提示文案（共享常量，客户端不自己拼）。</summary>
    public string BlankHintText => UndercoverText.BlankSelfHint;

    /// <summary>白板提示的可见性。</summary>
    public bool HasBlankHint => _isBlank;

    public string YourRole => _yourRole;

    /// <summary>真实身份是否已揭晓（只有整局结束后才为 true）。</summary>
    public bool IsRoleRevealed => _yourRoleKnown && _yourRole.Length > 0;

    /// <summary>身份保密中：局中不下发阵营，只能显示「保密」。</summary>
    public bool IsRoleHidden => _isJoined && !IsRoleRevealed && _phase != UndercoverPhases.Waiting;

    /// <summary>身份保密时的徽标文案（共享常量）。</summary>
    public string RoleHiddenDisplay => UndercoverText.UnknownRoleDisplay;

    /// <summary>「不会被告知自己阵营」的说明（共享常量）。</summary>
    public string RoleHiddenNotice => UndercoverText.SecretRoleNotice;

    /// <summary>保密说明的可见性（与身份徽标同步）。</summary>
    public bool HasRoleHiddenNotice => IsRoleHidden;

    /// <summary>
    /// 身份展示：**局中只显示「保密」**，绝不让玩家看到自己是卧底还是平民；结算后才显示真实身份。
    /// </summary>
    public string YourRoleDisplay => IsRoleRevealed
        ? $"{UndercoverRoles.Emoji(_yourRole)} {_yourRole}"
        : UndercoverText.UnknownRoleDisplay;

    public string YourRoleDesc => _yourRoleDesc;
    public string CategoryText => _categoryHint.Length > 0 ? $"分类提示：{_categoryHint}" : "";
    public bool HasCategory => _categoryHint.Length > 0;

    public string RoleSummary => _roleSummary;
    public bool HasRoleSummary => _roleSummary.Length > 0;

    // ---- 轮流发言（供面板展示） ----

    /// <summary>是否轮到我发言（描述阶段）。</summary>
    public bool IsMyTurnToSpeak => _isMyTurnToSpeak;

    /// <summary>「🎤 张三」这样的当前发言人标识；没人轮到时有别的提示。</summary>
    public string SpeakerText => _currentSpeakerName.Length == 0
        ? ""
        : _isMyTurnToSpeak ? $"🎤 轮到你发言" : $"🎤 正在发言：{_currentSpeakerName}";

    /// <summary>「已发言 2/4」。仅描述阶段有意义。</summary>
    public string SpeakProgressText => _totalSpeakers <= 0
        ? ""
        : $"已发言 {_spokenNames.Count}/{_totalSpeakers}";

    public bool HasSpeakerText => SpeakerText.Length > 0;

    public int YourId => _yourId;
    public bool IsAlive => _isAlive;
    public string AliveText => _isAlive ? "存活" : "已出局";
    public bool IsHost => _bridge.IsHost;
    public string MeText => _isJoined ? $"[{_yourId}] {_ownName}" : "";

    public int MinPlayers => _rules?.MinPlayers ?? 3;
    public int PlayerCount => _bridge.PlayerCount;
    public bool CanStart => _bridge.CanStartGame && _phase == UndercoverPhases.Waiting && PlayerCount >= MinPlayers;

    public string ActionHint
    {
        get => _actionHint;
        private set
        {
            if (!Set(ref _actionHint, value)) return;
            OnPropertyChanged(nameof(HasActionHint));
        }
    }

    public bool HasActionHint => ActionHint.Length > 0;
    public bool IsActionsEmpty => _actions.Count == 0;

    // ==================== 内部 ====================

    private IEnumerable<GameAction> BuildActions()
    {
        switch (_phase)
        {
            case UndercoverPhases.Waiting:
                yield return new GameAction
                {
                    Label = "开始游戏",
                    Command = StartGame,
                    IsEnabled = CanStart,
                    Variant = "primary",
                    Hint = CanStart ? null : (_bridge.IsHost ? $"至少需要 {MinPlayers} 名玩家" : "只有房主可以开始游戏")
                };
                yield break;

            case UndercoverPhases.Describe:
                if (!_isAlive) yield break;
                if (!_isMyTurnToSpeak) yield break;   // 只有轮到自己才给按钮
                yield return new GameAction
                {
                    Label = "跳过本轮发言",
                    Command = SkipSpeak,
                    Variant = "normal",
                    Hint = "不想描述时点这里，直接轮到下一位"
                };
                yield break;

            case UndercoverPhases.Vote:
                if (!_isAlive) yield break;
                yield return new GameAction
                {
                    Label = "确认投票",
                    Command = ConfirmVote,
                    IsEnabled = !_voteSubmitted && _bridge.SelectedTargetId >= 0,
                    Variant = "danger",
                    Hint = _bridge.SelectedTargetId >= 0 ? $"投给 [{_bridge.SelectedTargetId}]" : "请先在左侧选择玩家"
                };
                yield return new GameAction
                {
                    Label = "弃票",
                    Command = AbstainVote,
                    IsEnabled = !_voteSubmitted,
                    Hint = "本轮不投票"
                };
                yield break;
        }
    }

    private string BuildHint()
    {
        if (!_isJoined) return "";

        return _phase switch
        {
            UndercoverPhases.Waiting => _bridge.IsHost
                ? $"你是房主，人齐（至少 {MinPlayers} 人）后即可开始。"
                : "等待房主开始游戏…",
            UndercoverPhases.Describe => _isAlive
                ? (_isMyTurnToSpeak
                    ? "轮到你发言：用下方输入框描述你的词（**不能说词本身**），发送后自动轮到下一位。"
                    : $"{SpeakProgressText}　等待 {(string.IsNullOrEmpty(_currentSpeakerName) ? "其他人" : _currentSpeakerName)} 发言…")
                : "你已出局，可以继续旁观别人的描述。",
            UndercoverPhases.Vote => _isAlive
                ? (_voteSubmitted
                    ? (_lastVoteTarget < 0 ? "✅ 你已弃票。" : $"✅ 你已投票给 [{_lastVoteTarget}]。")
                    : "点击左侧玩家选出你认为的卧底，再点【确认投票】。")
                : "你已出局，无法投票。",
            UndercoverPhases.GameOver => "本局已结束，可重新开始或继续等待。",
            _ => ""
        };
    }

    private void RefreshChips()
    {
        Chips.Clear();
        if (!_isJoined) return;

        Chips.Add(new StatusChip { Text = RoundText });
        Chips.Add(new StatusChip { Text = PhaseDisplay, ColorHex = UndercoverPhases.Accent(_phase) });
        if (_phase == UndercoverPhases.Describe && _totalSpeakers > 0)
            Chips.Add(new StatusChip { Text = SpeakProgressText, ColorHex = _isMyTurnToSpeak ? "#FFCE6B" : null });
        if (_payload?.Rules is { } r && _phase == UndercoverPhases.Describe)
            Chips.Add(new StatusChip { Text = $"{r.DescribeSeconds}s", ColorHex = "#FFCE6B" });
        else if (_payload?.Rules is { } r2 && _phase == UndercoverPhases.Vote)
            Chips.Add(new StatusChip { Text = $"{r2.VoteSeconds}s", ColorHex = "#FFCE6B" });
    }

    private void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseDisplay));
        OnPropertyChanged(nameof(RoundText));
        OnPropertyChanged(nameof(YourWordDisplay));
        OnPropertyChanged(nameof(HasWord));
        OnPropertyChanged(nameof(IsBlank));
        OnPropertyChanged(nameof(BlankHintText));
        OnPropertyChanged(nameof(HasBlankHint));
        OnPropertyChanged(nameof(YourRole));
        OnPropertyChanged(nameof(IsRoleRevealed));
        OnPropertyChanged(nameof(IsRoleHidden));
        OnPropertyChanged(nameof(RoleHiddenDisplay));
        OnPropertyChanged(nameof(RoleHiddenNotice));
        OnPropertyChanged(nameof(HasRoleHiddenNotice));
        OnPropertyChanged(nameof(YourRoleDisplay));
        OnPropertyChanged(nameof(YourRoleDesc));
        OnPropertyChanged(nameof(CategoryText));
        OnPropertyChanged(nameof(HasCategory));
        OnPropertyChanged(nameof(RoleSummary));
        OnPropertyChanged(nameof(HasRoleSummary));
        OnPropertyChanged(nameof(IsMyTurnToSpeak));
        OnPropertyChanged(nameof(SpeakerText));
        OnPropertyChanged(nameof(HasSpeakerText));
        OnPropertyChanged(nameof(SpeakProgressText));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(SendChatHint));
        OnPropertyChanged(nameof(YourId));
        OnPropertyChanged(nameof(IsAlive));
        OnPropertyChanged(nameof(AliveText));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(MeText));
        OnPropertyChanged(nameof(MinPlayers));
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(CanStart));
    }

    private static UndercoverGameState? TryParsePayload(GameStateDto state)
    {
        if (state.GamePayload == null) return null;
        try { return state.GamePayload.Deserialize<UndercoverGameState>(Json); }
        catch { return null; }
    }
}
