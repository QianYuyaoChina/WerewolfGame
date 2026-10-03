using System.Collections.ObjectModel;
using System.Windows.Input;
using ClientGUI.Models;
using ClientGUI.Services;

namespace ClientGUI.ViewModels;

/// <summary>
/// 主窗口的视图模型：只做“服务器状态 → 界面状态”的翻译与用户操作的转发。
/// · 网络协议细节在 <see cref="GameClient"/>；
/// · 登录流程在 <see cref="LoginViewModel"/>；
/// · 操作按钮的组装在 <see cref="GameActionFactory"/>；
/// · 消息列表与阶段倒计时分别在 <see cref="GameLog"/> 与 <see cref="PhaseCountdown"/>。
/// </summary>
public sealed class GameViewModel : ObservableObject
{
    private readonly GameClient _client;
    private readonly GameLog _log = new();
    private readonly PhaseCountdown _countdown = new();
    private readonly GameCommands _commands;
    private readonly GameActionFactory _actionFactory;

    /// <summary>UI 线程上下文：服务器回调来自后台线程，状态更新必须回到这里。</summary>
    private readonly SynchronizationContext? _ui;

    // ---------- 服务器状态 ----------
    private GameStateDto? _state;

    /// <summary>当前回合标识（天数 + 阶段），用于判断是否进入新一轮。</summary>
    private (int Day, string Phase) _roundKey = (-1, "");

    private int _selectedTargetId = -1;
    private int _lastStateHash = int.MinValue;

    /// <summary>本回合已经提交过的动作（避免重复提交）。</summary>
    private readonly HashSet<string> _usedThisRound = new();

    /// <summary>整局已经用掉的药水。</summary>
    private readonly HashSet<string> _potionsUsed = new();

    // ---------- 界面状态 ----------
    private string _connectionStatus = "未连接";
    private string _logStatus = "";
    private bool _isJoined;
    private bool _isConnected;
    private string _yourRole = "";
    private string _yourRoleDesc = "";
    private int _yourId;
    private bool _isAlive = true;
    private int _day;
    private string _phase = Phases.Waiting;
    private bool _isHost;
    private string _countdownText = "";
    private string _actionHint = "";
    private string _chatText = "";

    /// <summary>无参构造：供设计器与纯逻辑校验使用（自带一个未连接的客户端）。</summary>
    public GameViewModel() : this(new GameClient())
    {
    }

    public GameViewModel(GameClient client)
    {
        _client = client;
        _ui = SynchronizationContext.Current;

        _commands = new GameCommands
        {
            StartGame = new AsyncRelayCommand(_ => StartGameAsync()),
            Submit = new RelayCommand(_ => SubmitSelection()),
            UseHeal = new AsyncRelayCommand(_ => UseHealAsync()),
            SkipPotion = new AsyncRelayCommand(_ => SkipPotionAsync()),
            AbstainVote = new AsyncRelayCommand(_ => AbstainVoteAsync()),
            SkipHunterShot = new AsyncRelayCommand(_ => SkipHunterShotAsync())
        };
        _actionFactory = new GameActionFactory(_commands);

        DisconnectCommand = new AsyncRelayCommand(_ => LeaveRoomAsync());
        SendChatCommand = new RelayCommand(_ => SendChat());

        _countdown.TextChanged += text => CountdownText = text;

        _client.MessageReceived += message => OnUi(() => OnMessageReceived(message));
        _client.StateReceived += state => OnUi(() => OnStateReceived(state));
        _client.StatusChanged += status => OnUi(() => OnStatusChanged(status));
        _client.ConnectionClosed += ex => OnUi(() => OnConnectionClosed(ex));
        _client.Reconnected += info => OnUi(() => OnReconnected(info));

        // 进入主窗口时已经登录成功（登录窗口负责连接与加入）。
        IsConnected = _client.IsConnected;
        IsJoined = _client.IsJoined;
        ConnectionStatus = _client.IsJoined ? "已加入房间"
            : _client.IsConnected ? "已连接服务器"
            : "未连接";
    }

    /// <summary>把回调切到 UI 线程执行。</summary>
    private void OnUi(Action action)
    {
        if (_ui == null) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    // ==================== 集合 ====================

    public ObservableCollection<PlayerRow> Players { get; } = new();
    public ObservableCollection<GameAction> Actions { get; } = new();

    /// <summary>房间消息（绑定在界面上的就是它）。</summary>
    public ObservableCollection<LogEntry> Log => _log.Entries;

    // ==================== 命令与事件 ====================

    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand SendChatCommand { get; }

    /// <summary>房主开始游戏：左栏按钮与操作面板里的“开始游戏”共用同一个命令。</summary>
    public ICommand StartGameCommand => _commands.StartGame;

    /// <summary>主动断开（退出房间）后触发，界面据此回到登录窗口。</summary>
    public event Action? SessionEnded;

    // ==================== 绑定属性 ====================

    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string LogStatus { get => _logStatus; private set => Set(ref _logStatus, value); }

    public bool IsJoined
    {
        get => _isJoined;
        private set
        {
            if (!Set(ref _isJoined, value)) return;
            OnPropertyChanged(nameof(ShowGamePanel));
            OnPropertyChanged(nameof(CanLeaveRoom));
            OnPropertyChanged(nameof(CanChat));
            OnPropertyChanged(nameof(ChatHint));
            OnPropertyChanged(nameof(EndpointText));
            OnPropertyChanged(nameof(MeText));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanLeaveRoom));
        }
    }

    /// <summary>已加入房间后允许主动退出。</summary>
    public bool CanLeaveRoom => IsJoined || IsConnected;

    /// <summary>主区域（三栏）只在房间内显示。</summary>
    public bool ShowGamePanel => IsJoined;

    public string YourRole
    {
        get => _yourRole;
        private set
        {
            if (Set(ref _yourRole, value)) OnPropertyChanged(nameof(YourRoleDisplay));
        }
    }

    public string YourRoleDisplay =>
        string.IsNullOrEmpty(YourRole) ? "未知" : $"{Roles.Emoji(YourRole)} {YourRole}";

    public string YourRoleDesc { get => _yourRoleDesc; private set => Set(ref _yourRoleDesc, value); }

    public int YourId
    {
        get => _yourId;
        private set
        {
            if (Set(ref _yourId, value)) OnPropertyChanged(nameof(MeText));
        }
    }

    public bool IsAlive
    {
        get => _isAlive;
        private set
        {
            if (!Set(ref _isAlive, value)) return;
            OnPropertyChanged(nameof(AliveText));
            OnPropertyChanged(nameof(AliveBrush));
            OnPropertyChanged(nameof(CanChat));
            OnPropertyChanged(nameof(ChatHint));
        }
    }

    public string AliveText => IsAlive ? "存活" : "已出局";
    public System.Windows.Media.Brush AliveBrush => IsAlive ? UiBrushes.Alive : UiBrushes.Dead;

    public int Day
    {
        get => _day;
        private set
        {
            if (Set(ref _day, value)) OnPropertyChanged(nameof(DayText));
        }
    }

    public string DayText => Day <= 0 ? "准备阶段" : $"第 {Day} 天";

    public string Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(PhaseDisplay));
            OnPropertyChanged(nameof(PhaseBrush));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(CanStartGame));
        }
    }

    public string PhaseDisplay => Phases.DisplayName(Phase);
    public System.Windows.Media.Brush PhaseBrush => UiBrushes.PhaseBrush(Phase);
    public bool IsWaiting => Phase == Phases.Waiting;

    public bool IsHost
    {
        get => _isHost;
        private set
        {
            if (!Set(ref _isHost, value)) return;
            OnPropertyChanged(nameof(CanStartGame));
            OnPropertyChanged(nameof(StartHint));
            OnPropertyChanged(nameof(ShowStartHint));
        }
    }

    public bool CanStartGame => IsHost && IsWaiting;
    public string StartHint => IsHost
        ? "你是房主，人齐（至少 4 人）后即可开始。"
        : "等待房主开始游戏…";
    public bool ShowStartHint => !IsHost;

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

    /// <summary>
    /// 操作面板是否为空。必须是普通属性（而不是在 XAML 里对 <c>Actions.Count</c> 做 DataTrigger）：
    /// 在同一个面板内监听 ItemsControl 的数据源会在测量期间反复失效，导致无限测量递归（栈溢出）。
    /// </summary>
    public bool IsActionsEmpty => Actions.Count == 0;

    public string ChatText { get => _chatText; set => Set(ref _chatText, value); }
    public bool CanChat => IsJoined && IsAlive;
    public string ChatHint => !IsJoined ? "" : IsAlive ? "" : "你已出局，无法发言。";

    public string EndpointText => IsJoined ? $"服务器：{_client.Endpoint}" : "";
    public string MeText => IsJoined ? $"[{YourId}] {_client.Username}" : "";

    // ==================== 退出房间 ====================

    private async Task LeaveRoomAsync()
    {
        _countdown.Stop();

        await _client.DisconnectAsync();

        IsJoined = false;
        IsConnected = false;
        _state = null;
        _roundKey = (-1, "");
        _selectedTargetId = -1;
        _lastStateHash = int.MinValue;
        Players.Clear();
        Actions.Clear();
        ActionHint = "";
        ChatText = "";
        YourRole = "";
        YourRoleDesc = "";
        Day = 0;
        Phase = Phases.Waiting;
        IsAlive = true;
        IsHost = false;

        ConnectionStatus = "未连接";
        LogStatus = "已断开连接。";
        AddLog("🔌 已断开与服务器的连接。");

        SessionEnded?.Invoke();
    }

    // ==================== 房主开始 ====================

    private async Task StartGameAsync()
    {
        try
        {
            var r = await _client.StartGameAsync();
            if (r.ok) return;

            AddLog("⚠️ " + r.msg, LogKinds.Error);
            LogStatus = r.msg;
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    // ==================== 发言 ====================

    private void SendChat()
    {
        var text = (ChatText ?? "").Trim();
        if (text.Length == 0) return;
        if (!CanChat)
        {
            LogStatus = "你已出局，无法发言。";
            return;
        }

        ChatText = "";

        // 直接在 UI 线程上等待，异常只写日志，不冒泡到全局。
        _ = SendChatCoreAsync(text);
    }

    private async Task SendChatCoreAsync(string text)
    {
        try
        {
            await _client.ChatAsync(text);
        }
        catch (Exception ex)
        {
            AddLog("⚠️ 发言失败：" + ex.Message, LogKinds.Error);
        }
    }

    // ==================== 服务器事件 ====================

    private void OnStatusChanged(string status) => ConnectionStatus = status;

    private void OnConnectionClosed(Exception? ex)
    {
        IsConnected = false;
        if (!IsJoined) return;
        ConnectionStatus = "连接已断开";
        AddLog("⚠️ 与服务器的连接已断开。", LogKinds.Error);
    }

    private void OnReconnected(string info)
    {
        IsConnected = true;
        AddLog("🔄 " + info);
    }

    private void OnMessageReceived(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        AddLog(message, GameLog.Classify(message));
    }

    private void OnStateReceived(GameStateDto s)
    {
        var hash = ComputeHash(s);
        if (hash == _lastStateHash)
        {
            // 完全相同的状态，跳过（保留列表滚动位置与选中目标）。
            return;
        }
        _lastStateHash = hash;
        _state = s;

        var roundKey = (s.Day, s.Phase);
        var newRound = roundKey != _roundKey;
        if (newRound)
        {
            _roundKey = roundKey;
            _usedThisRound.Clear();
            _selectedTargetId = -1;
            _countdown.Start(s.Phase);
        }

        // 新的一局（天数归零重新开始）时清空药水记录。
        if (s.Phase == Phases.Waiting || (s.Day == 0 && newRound))
            _potionsUsed.Clear();

        Day = s.Day;
        Phase = s.Phase;
        YourRole = s.YourRole;
        YourRoleDesc = s.YourRoleDesc;
        YourId = s.YourId;
        IsAlive = s.IsAlive;
        IsHost = s.Players.FirstOrDefault(p => p.IsYou)?.IsHost ?? false;

        RefreshPlayers();
        RefreshActions();
    }

    private static int ComputeHash(GameStateDto s)
    {
        var h = new HashCode();
        h.Add(s.Phase);
        h.Add(s.Day);
        h.Add(s.YourId);
        h.Add(s.YourRole);
        h.Add(s.IsAlive);
        foreach (var p in s.Players)
        {
            h.Add(p.Id);
            h.Add(p.Name);
            h.Add(p.IsAlive);
            h.Add(p.IsHost);
            h.Add(p.IsYou);
            h.Add(p.IsConnected);
            h.Add(p.Role);
        }
        return h.ToHashCode();
    }

    // ==================== 玩家列表 ====================

    private void RefreshPlayers()
    {
        var s = _state;
        if (s == null) return;

        Players.Clear();
        foreach (var p in s.Players.OrderBy(p => p.Id))
        {
            var row = new PlayerRow
            {
                Id = p.Id,
                Name = p.Name,
                IsAlive = p.IsAlive,
                IsHost = p.IsHost,
                IsYou = p.IsYou,
                IsConnected = p.IsConnected,
                Role = p.Role
            };

            // 关键：新行必须立刻算出可点选状态，否则玩家列表看起来“不能点”。
            row.Selectable = IsSelectable(row);
            row.IsTarget = row.Id == _selectedTargetId;
            Players.Add(row);
        }
    }

    /// <summary>左侧列表点选玩家（可再次点击取消）。</summary>
    public void SelectPlayer(PlayerRow row)
    {
        if (!CanSelectTargets || !IsSelectable(row)) return;

        _selectedTargetId = _selectedTargetId == row.Id ? -1 : row.Id;
        UpdateTargetHighlight();
        RefreshActions();
    }

    private void UpdateTargetHighlight()
    {
        foreach (var p in Players)
        {
            p.Selectable = CanSelectTargets && IsSelectable(p);
            p.IsTarget = p.Id == _selectedTargetId;
        }
    }

    private bool IsSelectable(PlayerRow p)
    {
        // 出局玩家仍可能处于猎人开枪阶段（猎人可选择目标或放弃）。
        if (!p.IsAlive || p.IsYou) return false;
        return Phase switch
        {
            Phases.Night => YourRole switch
            {
                Roles.Guard or Roles.Werewolf or Roles.Seer => true,
                Roles.Witch => !_potionsUsed.Contains("poison") && !_usedThisRound.Contains("poison"),
                _ => false
            },
            Phases.Vote => true,
            Phases.HunterShoot => true,
            _ => false
        };
    }

    /// <summary>当前玩家是否处于“有目标可选”的状态。</summary>
    private bool CanSelectTargets =>
        (IsAlive && Phase is Phases.Night or Phases.Vote)
        || Phase == Phases.HunterShoot;

    // ==================== 操作面板 ====================

    private void RefreshActions()
    {
        var plan = _actionFactory.Build(CreateActionContext());

        Actions.Clear();
        foreach (var action in plan.Actions) Actions.Add(action);
        ActionHint = plan.Hint;
        OnPropertyChanged(nameof(IsActionsEmpty));

        // 阶段/存活状态可能变化，重新计算玩家高亮。
        UpdateTargetHighlight();
    }

    private ActionContext CreateActionContext()
    {
        var s = _state;
        return new ActionContext
        {
            Phase = Phase,
            Role = YourRole,
            IsHunter = YourRole == Roles.Hunter
                       || s?.Players.FirstOrDefault(p => p.IsYou)?.Role == Roles.Hunter,
            IsAlive = IsAlive,
            IsHost = IsHost,
            CanStartGame = CanStartGame,
            SelectedTargetId = _selectedTargetId,
            UsedThisRound = _usedThisRound,
            PotionsUsed = _potionsUsed
        };
    }

    // ==================== 提交动作 ====================

    private void SubmitSelection()
    {
        switch (Phase)
        {
            case Phases.Night:
                if (_selectedTargetId < 0)
                {
                    ActionHint = "请先选择目标。";
                    return;
                }
                _ = NightActionAsync(_selectedTargetId, YourRole == Roles.Witch ? "poison" : "do");
                break;

            case Phases.Vote:
                if (_selectedTargetId < 0)
                {
                    ActionHint = "请先选择要放逐的玩家，或点击【弃票】。";
                    return;
                }
                _ = VoteAsync(_selectedTargetId);
                break;

            case Phases.HunterShoot:
                if (_selectedTargetId < 0)
                {
                    ActionHint = "请先选择开枪目标，或点击【放弃开枪】。";
                    return;
                }
                _ = HunterShotAsync(_selectedTargetId);
                break;
        }
    }

    private async Task NightActionAsync(int targetId, string action)
    {
        try
        {
            await _client.NightActionAsync(targetId, action);
            if (action == "poison")
            {
                _potionsUsed.Add("poison");
                _usedThisRound.Add("poison");
                AddLog($"✅ 已提交毒药 → [{targetId}]");
            }
            else
            {
                _usedThisRound.Add("do");
                AddLog($"✅ 已提交夜间行动 → [{targetId}]");
            }
            // 夜间行动提交后清空选择（与服务器一致：本晚不再提示目标）。
            _selectedTargetId = -1;
            RefreshAfterSubmit();
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task UseHealAsync()
    {
        try
        {
            await _client.NightActionAsync(-2, "heal");
            _potionsUsed.Add("heal");
            _usedThisRound.Add("heal");
            AddLog("✅ 已使用解药。");
            RefreshActions();
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task SkipPotionAsync()
    {
        try
        {
            await _client.NightActionAsync(-1, "none");
            _usedThisRound.Add("heal"); // 与服务器一致：提交后本晚不再行动
            AddLog("✅ 今晚不使用药水。");
            RefreshActions();
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    private async Task VoteAsync(int targetId)
    {
        try
        {
            await _client.VoteAsync(targetId);
            _usedThisRound.Add("vote");
            AddLog(targetId < 0 ? "✅ 已弃票。" : $"✅ 已投票 → [{targetId}]");
            _selectedTargetId = targetId;   // 保留选择，方便界面回显“你已投票给 X”
            RefreshAfterSubmit();
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>弃票（-1）。</summary>
    private Task AbstainVoteAsync() => VoteAsync(-1);

    private async Task HunterShotAsync(int targetId)
    {
        try
        {
            await _client.HunterShotAsync(targetId);
            _usedThisRound.Add("hunter");
            AddLog(targetId < 0 ? "✅ 已放弃开枪。" : $"✅ 已开枪 → [{targetId}]");
            _selectedTargetId = targetId;
            RefreshAfterSubmit();
        }
        catch (Exception ex)
        {
            AddLog("⚠️ " + ex.Message, LogKinds.Error);
        }
    }

    /// <summary>放弃开枪（-1）。</summary>
    private Task SkipHunterShotAsync() => HunterShotAsync(-1);

    /// <summary>提交成功后的统一收尾：刷新高亮与按钮。</summary>
    private void RefreshAfterSubmit()
    {
        UpdateTargetHighlight();
        RefreshActions();
    }

    // ==================== 生命周期 ====================

    private void AddLog(string text, string kind = LogKinds.Info) => _log.Add(text, kind);

    public void OnLoaded()
    {
        if (Log.Count == 0)
        {
            AddLog("🐺 狼人杀客户端已就绪。");
            AddLog("点击左侧玩家选择目标，右侧提交本阶段的操作；出局后仍可旁观。");
        }
    }

    /// <summary>窗口关闭时尽力断开连接。</summary>
    public async Task DisposeAsync()
    {
        _countdown.Stop();
        try { await _client.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
    }
}
