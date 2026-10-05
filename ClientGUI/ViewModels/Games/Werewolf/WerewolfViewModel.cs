using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels.Games.Werewolf;

/// <summary>
/// 狼人杀操作面板的视图模型：把狼人杀私有负载（<see cref="WerewolfGameState"/>，来自
/// <see cref="GameStateDto.GamePayload"/>）翻译成操作按钮 / 提示 / 状态胶囊，并回传玩家操作。
///
/// 只依赖宿主桥 <see cref="IGameHostBridge"/>（网络客户端 / 选中目标 / 人数 / 是否房主 / 反馈日志），
/// 不感知房间列表、账号、主题等平台模块。
/// </summary>
public sealed class WerewolfViewModel : ObservableObject, IGameViewModel
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly GameClient _client;
    private readonly IGameHostBridge _bridge;
    private readonly WerewolfCommands _commands;
    private readonly WerewolfActionFactory _actionFactory;
    private readonly PhaseCountdown _countdown = new();

    private GameStateDto? _state;
    private WerewolfGameState? _payload;
    private GameRules? _rules;

    private (int Day, string Phase) _roundKey = (-1, "");
    private readonly HashSet<string> _usedThisRound = new();
    private readonly HashSet<string> _potionsUsed = new();

    private string _phase = Phases.Waiting;
    private int _day;
    private string _yourRole = "";
    private string _yourRoleDesc = "";
    private string _actingRole = "";
    private int _yourId;
    private bool _isAlive = true;
    private bool _isJoined;
    private string _ownName = "";
    private string _actionHint = "";
    private string _countdownText = "";

    // ---- v2.1：身份配置 / 情侣 / 警长 ----
    private string _roleSummary = "";
    private string _loverName = "";
    private bool _isLover;
    private string _sheriffName = "";
    private bool _sheriffEnabled;
    private bool _youTransferBadge;
    private int _cupidTargetA = -1;
    private int _cupidTargetB = -1;

    public WerewolfViewModel(GameClient client, IGameHostBridge bridge)
    {
        _client = client;
        _bridge = bridge;

        _commands = new WerewolfCommands
        {
            StartGame = new AsyncRelayCommand(_ => StartGameAsync()),
            Submit = new RelayCommand(p => SubmitSelection(p as string)),
            UseHeal = new AsyncRelayCommand(_ => UseHealAsync()),
            SkipPotion = new AsyncRelayCommand(_ => SkipPotionAsync()),
            AbstainVote = new AsyncRelayCommand(_ => AbstainVoteAsync()),
            SkipHunterShot = new AsyncRelayCommand(_ => SkipHunterShotAsync()),
            SetCupidA = new RelayCommand(_ => SetCupidTarget(lover1: true)),
            SetCupidB = new RelayCommand(_ => SetCupidTarget(lover1: false)),
            ConfirmCupid = new AsyncRelayCommand(_ => ConfirmCupidAsync()),
            TransferBadge = new AsyncRelayCommand(_ => TransferBadgeAsync(_bridge.SelectedTargetId)),
            VoidBadge = new AsyncRelayCommand(_ => TransferBadgeAsync(-1))
        };
        _actionFactory = new WerewolfActionFactory(_commands);

        _countdown.TextChanged += OnCountdownChanged;
    }

    // ==================== IGameViewModel ====================

    /// <inheritdoc />
    public string GameId => GameIds.Werewolf;

    /// <summary>操作面板按钮。</summary>
    public ObservableCollection<GameAction> Actions { get; } = new();

    /// <inheritdoc />
    IReadOnlyList<GameAction> IGameViewModel.Actions => Actions;

    /// <summary>标题栏状态胶囊（天数 / 阶段 / 倒计时）。</summary>
    public ObservableCollection<StatusChip> Chips { get; } = new();

    /// <inheritdoc />
    IReadOnlyList<StatusChip> IGameViewModel.Chips => Chips;

    /// <inheritdoc />
    public string Hint => ActionHint;

    /// <inheritdoc />
    public bool CanSelectPlayer(PlayerRow row)
    {
        if (!CanSelectTargets) return false;
        if (!row.IsAlive || row.IsYou) return false;

        return _phase switch
        {
            Phases.Night => IsMyTurn && _yourRole switch
            {
                Roles.Guard or Roles.Werewolf or Roles.Seer or Roles.Cupid => true,
                Roles.Witch => !_potionsUsed.Contains("poison") && !_usedThisRound.Contains("poison"),
                _ => false
            },
            Phases.Vote => true,
            Phases.HunterShoot => true,
            Phases.SheriffTransfer => _youTransferBadge,
            _ => false
        };
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

        _phase = _payload?.Phase is { Length: > 0 } p ? p : Phases.Waiting;
        _day = _payload?.Day ?? 0;
        _yourRole = _payload?.YourRole ?? "";
        _yourRoleDesc = _payload?.YourRoleDesc ?? "";
        _actingRole = _payload?.ActingRole ?? "";
        _yourId = state.YourId;
        _isAlive = state.IsAlive;
        _isJoined = !string.IsNullOrEmpty(state.RoomId);
        _ownName = state.Players.FirstOrDefault(p => p.IsYou)?.Name ?? "";

        // ---- v2.1：身份配置 / 情侣 / 警长 ----
        _roleSummary = _payload?.RoleSummary ?? "";
        _loverName = _payload?.LoverName ?? "";
        _isLover = _payload?.IsLover ?? false;
        _sheriffName = _payload?.SheriffName ?? "";
        _sheriffEnabled = _payload?.SheriffEnabled ?? false;
        _youTransferBadge = _payload?.YouTransferBadge ?? false;

        // 离开夜晚就清掉丘比特的临时选择（避免下一夜残留）。
        if (_phase != Phases.Night)
        {
            _cupidTargetA = -1;
            _cupidTargetB = -1;
        }

        var roundKey = (_day, _phase);
        var newRound = roundKey != _roundKey;
        if (newRound)
        {
            _roundKey = roundKey;
            _usedThisRound.Clear();
            _countdown.Start(_phase, _rules);
        }

        // 新的一局（天数归零重新开始）时清空药水记录。
        if (_phase == Phases.Waiting || (_day == 0 && newRound))
            _potionsUsed.Clear();

        Refresh();
        RaiseDisplayChanged();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        var plan = _actionFactory.Build(CreateContext());

        Actions.Clear();
        foreach (var action in plan.Actions) Actions.Add(action);
        ActionHint = plan.Hint;

        OnPropertyChanged(nameof(IsActionsEmpty));
        RefreshChips();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _state = null;
        _payload = null;
        _rules = null;
        _roundKey = (-1, "");
        _usedThisRound.Clear();
        _potionsUsed.Clear();
        _phase = Phases.Waiting;
        _day = 0;
        _yourRole = "";
        _yourRoleDesc = "";
        _actingRole = "";
        _yourId = 0;
        _isAlive = true;
        _isJoined = false;
        _ownName = "";
        _roleSummary = "";
        _loverName = "";
        _isLover = false;
        _sheriffName = "";
        _sheriffEnabled = false;
        _youTransferBadge = false;
        _cupidTargetA = -1;
        _cupidTargetB = -1;
        _countdown.Stop();

        Actions.Clear();
        Chips.Clear();
        ActionHint = "";
        RaiseDisplayChanged();
    }

    // ==================== 绑定属性 ====================

    public string Phase => _phase;
    public string PhaseDisplay => Phases.DisplayName(_phase);
    public Brush PhaseBrush => UiBrushes.PhaseBrush(_phase);
    public bool IsWaiting => _phase == Phases.Waiting;

    public int Day => _day;
    public string DayText => _day <= 0 ? "准备阶段" : $"第 {_day} 天";

    public string YourRole => _yourRole;
    public string YourRoleDisplay => string.IsNullOrEmpty(_yourRole) ? "未知" : $"{Roles.Emoji(_yourRole)} {_yourRole}";
    public string YourRoleDesc => _yourRoleDesc;

    /// <summary>夜晚当前轮到行动的角色（中文名）；未轮到或非夜晚为空字符串。</summary>
    public string ActingRole => _actingRole;

    /// <summary>当前是否轮到“我”行动（按身份名比对，忽略空串）。</summary>
    public bool IsMyTurn => _phase == Phases.Night && !string.IsNullOrEmpty(_actingRole) && _actingRole == _yourRole;

    public int YourId => _yourId;
    public bool IsAlive => _isAlive;
    public string AliveText => _isAlive ? "存活" : "已出局";
    public Brush AliveBrush => _isAlive ? UiBrushes.Alive : UiBrushes.Dead;

    public bool IsJoined => _isJoined;
    public bool IsHost => _bridge.IsHost;
    public string MeText => _isJoined ? $"[{_yourId}] {_ownName}" : "";

    public int MinPlayers => _rules?.MinPlayers ?? 4;
    public int PlayerCount => _bridge.PlayerCount;

    // ---- v2.1：身份配置 / 情侣 / 警长（供面板展示） ----

    /// <summary>本局最终身份配置的展示文案（服务端下发）。</summary>
    public string RoleSummary => _roleSummary;
    public bool HasRoleSummary => _roleSummary.Length > 0;

    /// <summary>我是否是情侣，以及另一半是谁。</summary>
    public bool IsLover => _isLover;
    public string LoverName => _loverName;
    public string LoverText => _isLover && _loverName.Length > 0 ? $"💘 你的情侣：{_loverName}（一方出局另一方殉情）" : "";

    /// <summary>当前警长与是否配置了警长。</summary>
    public bool SheriffEnabled => _sheriffEnabled;
    public string SheriffText => _sheriffName.Length > 0 ? $"🎖 当前警长：{_sheriffName}" : SheriffEnabled ? "🎖 警徽已作废" : "";

    public bool CanStartGame => IsHost && IsWaiting && PlayerCount >= MinPlayers;
    public string StartHint => IsHost
        ? $"你是房主，人齐（至少 {MinPlayers} 人）后即可开始。"
        : "等待房主开始游戏…";
    public bool ShowStartHint => !IsHost;

    public string ActionHint
    {
        get => _actionHint;
        private set
        {
            if (!Set(ref _actionHint, value)) return;
            OnPropertyChanged(nameof(HasActionHint));
            OnPropertyChanged(nameof(Hint));
        }
    }

    public bool HasActionHint => ActionHint.Length > 0;

    public string CountdownText
    {
        get => _countdownText;
        private set
        {
            if (!Set(ref _countdownText, value)) return;
            OnPropertyChanged(nameof(HasCountdown));
        }
    }

    public bool HasCountdown => CountdownText.Length > 0;

    /// <summary>
    /// 操作面板是否为空。普通属性（而非在 XAML 里对 Actions.Count 做 DataTrigger）：
    /// 同一面板内监听 ItemsControl 的数据源会在测量期间反复失效，导致无限测量递归（栈溢出）。
    /// </summary>
    public bool IsActionsEmpty => Actions.Count == 0;

    // ==================== 内部：上下文与胶囊 ====================

    private ActionContext CreateContext()
    {
        var own = _state?.Players.FirstOrDefault(p => p.IsYou);
        return new ActionContext
        {
            Phase = _phase,
            Role = _yourRole,
            IsHunter = _yourRole == Roles.Hunter || own?.Tag == Roles.Hunter,
            IsAlive = _isAlive,
            IsHost = IsHost,
            CanStartGame = CanStartGame,
            MinPlayers = MinPlayers,
            SelectedTargetId = _bridge.SelectedTargetId,
            UsedThisRound = _usedThisRound,
            PotionsUsed = _potionsUsed,
            ActingRole = _actingRole,
            IsMyTurn = IsMyTurn,
            CupidTargetA = _cupidTargetA,
            CupidTargetB = _cupidTargetB,
            IsSheriffTransferor = _youTransferBadge && _phase == Phases.SheriffTransfer
        };
    }

    private bool CanSelectTargets =>
        (_isAlive && _phase == Phases.Vote)
        || (_isAlive && _phase == Phases.Night && IsMyTurn)
        || _phase == Phases.HunterShoot
        || (_phase == Phases.SheriffTransfer && _youTransferBadge);

    private void RefreshChips()
    {
        Chips.Clear();
        if (!_isJoined) return;

        Chips.Add(new StatusChip { Text = DayText });
        Chips.Add(new StatusChip { Text = PhaseDisplay, ColorHex = Phases.Accent(_phase) });
        if (CountdownText.Length > 0)
            Chips.Add(new StatusChip { Text = CountdownText, ColorHex = "#FFCE6B" });
        if (_sheriffEnabled && _sheriffName.Length > 0)
            Chips.Add(new StatusChip { Text = $"🎖 警长：{_sheriffName}", ColorHex = "#FFD75E" });
        if (_isLover && _loverName.Length > 0)
            Chips.Add(new StatusChip { Text = $"💘 情侣：{_loverName}", ColorHex = "#FF7FB0" });
    }

    private void RaiseDisplayChanged()
    {
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseDisplay));
        OnPropertyChanged(nameof(PhaseBrush));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(Day));
        OnPropertyChanged(nameof(DayText));
        OnPropertyChanged(nameof(YourRole));
        OnPropertyChanged(nameof(YourRoleDisplay));
        OnPropertyChanged(nameof(YourRoleDesc));
        OnPropertyChanged(nameof(ActingRole));
        OnPropertyChanged(nameof(IsMyTurn));
        OnPropertyChanged(nameof(YourId));
        OnPropertyChanged(nameof(IsAlive));
        OnPropertyChanged(nameof(AliveText));
        OnPropertyChanged(nameof(AliveBrush));
        OnPropertyChanged(nameof(IsJoined));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(MeText));
        OnPropertyChanged(nameof(MinPlayers));
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(CanStartGame));
        OnPropertyChanged(nameof(StartHint));
        OnPropertyChanged(nameof(ShowStartHint));
        OnPropertyChanged(nameof(RoleSummary));
        OnPropertyChanged(nameof(HasRoleSummary));
        OnPropertyChanged(nameof(IsLover));
        OnPropertyChanged(nameof(LoverName));
        OnPropertyChanged(nameof(LoverText));
        OnPropertyChanged(nameof(SheriffEnabled));
        OnPropertyChanged(nameof(SheriffText));
    }

    private void OnCountdownChanged(string text)
    {
        CountdownText = text;
        RefreshChips();
    }

    private static WerewolfGameState? TryParsePayload(GameStateDto state)
    {
        if (state.GamePayload == null) return null;
        try
        {
            return state.GamePayload.Deserialize<WerewolfGameState>(Json);
        }
        catch
        {
            return null;
        }
    }

    // ==================== 命令实现 ====================

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

    private void SubmitSelection(string? parameter)
    {
        switch (_phase)
        {
            case Phases.Night:
                if (_bridge.SelectedTargetId < 0)
                {
                    ActionHint = "请先选择目标。";
                    return;
                }
                _ = NightActionAsync(_bridge.SelectedTargetId, _yourRole == Roles.Witch ? "poison" : "do");
                break;

            case Phases.Vote:
                if (_bridge.SelectedTargetId < 0)
                {
                    ActionHint = "请先选择要放逐的玩家，或点击【弃票】。";
                    return;
                }
                _ = VoteAsync(_bridge.SelectedTargetId);
                break;

            case Phases.HunterShoot:
                if (_bridge.SelectedTargetId < 0)
                {
                    ActionHint = "请先选择开枪目标，或点击【放弃开枪】。";
                    return;
                }
                _ = HunterShotAsync(_bridge.SelectedTargetId);
                break;

            case Phases.SheriffTransfer:
                if (_bridge.SelectedTargetId < 0)
                {
                    ActionHint = "请先选择接手的玩家，或点击【作废警徽】。";
                    return;
                }
                _ = TransferBadgeAsync(_bridge.SelectedTargetId);
                break;
        }
    }

    /// <summary>丘比特：把当前选中的玩家记为情侣①或②（本地暂存，最后一次性提交）。</summary>
    private void SetCupidTarget(bool lover1)
    {
        var id = _bridge.SelectedTargetId;
        if (id < 0) { ActionHint = "请先在左侧选择一名玩家。"; return; }

        if (lover1) _cupidTargetA = id;
        else if (id == _cupidTargetA) { ActionHint = "情侣必须是两名不同的玩家。"; return; }
        else _cupidTargetB = id;

        Refresh();
    }

    private async Task ConfirmCupidAsync()
    {
        if (_cupidTargetA < 0 || _cupidTargetB < 0)
        {
            ActionHint = "请先分别记下两名情侣。";
            return;
        }

        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Cupid,
                JsonSerializer.SerializeToNode(new CupidPayload { TargetA = _cupidTargetA, TargetB = _cupidTargetB }, Json));
            _usedThisRound.Add("cupid");
            _bridge.AppendLog($"✅ 已指定情侣：[{_cupidTargetA}] 与 [{_cupidTargetB}] 成为情侣。");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>警长移交警徽；<paramref name="targetId"/> 为 -1 表示作废警徽。</summary>
    private async Task TransferBadgeAsync(int targetId)
    {
        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Sheriff,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = targetId }, Json));
            _usedThisRound.Add("sheriff");
            _bridge.AppendLog(targetId < 0 ? "✅ 已作废警徽。" : $"✅ 已把警徽移交给 [{targetId}]。");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task NightActionAsync(int targetId, string action)
    {
        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Night,
                JsonSerializer.SerializeToNode(new NightPayload { TargetId = targetId, Action = action }, Json));
            if (action == "poison")
            {
                _potionsUsed.Add("poison");
                _usedThisRound.Add("poison");
                _bridge.AppendLog($"✅ 已提交毒药 → [{targetId}]");
            }
            else
            {
                _usedThisRound.Add("do");
                _bridge.AppendLog($"✅ 已提交夜间行动 → [{targetId}]");
            }
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task UseHealAsync()
    {
        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Night,
                JsonSerializer.SerializeToNode(new NightPayload { TargetId = -2, Action = "heal" }, Json));
            _potionsUsed.Add("heal");
            _usedThisRound.Add("heal");
            _bridge.AppendLog("✅ 已使用解药。");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task SkipPotionAsync()
    {
        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Night,
                JsonSerializer.SerializeToNode(new NightPayload { TargetId = -1, Action = "none" }, Json));
            _usedThisRound.Add("heal"); // 与服务器一致：提交后本晚不再行动
            _bridge.AppendLog("✅ 今晚不使用药水。");
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
            await _client.CallGameOpAsync(WerewolfOps.Vote,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = targetId }, Json));
            _usedThisRound.Add("vote");
            _bridge.AppendLog(targetId < 0 ? "✅ 已弃票。" : $"✅ 已投票 → [{targetId}]");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>弃票（-1）。</summary>
    private Task AbstainVoteAsync() => VoteAsync(-1);

    private async Task HunterShotAsync(int targetId)
    {
        try
        {
            await _client.CallGameOpAsync(WerewolfOps.Hunter,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = targetId }, Json));
            _usedThisRound.Add("hunter");
            _bridge.AppendLog(targetId < 0 ? "✅ 已放弃开枪。" : $"✅ 已开枪 → [{targetId}]");
            Refresh();
        }
        catch (Exception ex)
        {
            _bridge.AppendLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>放弃开枪（-1）。</summary>
    private Task SkipHunterShotAsync() => HunterShotAsync(-1);
}
