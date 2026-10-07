using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 主窗口的**宿主**视图模型：只做平台职责——
/// 连接 / 心跳 / 房间信息 / 玩家列表 / 聊天 / 日志 / 保留询问 / 退出房间。
/// 玩法（阶段 / 身份 / 操作按钮）全部委托给插槽面板 <see cref="Game"/>（<see cref="IGameViewModel"/>），
/// 宿主不感知任何具体游戏类型；状态到达时按 <c>GameId</c> 切换面板。
/// </summary>
public sealed class GameViewModel : ObservableObject, IGameHostBridge
{
    private readonly GameClient _client;
    private readonly GameLog _log = new();

    /// <summary>心跳定时器：周期性地与服务端互发一次空操作，用于探测连接是否仍然可用。</summary>
    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };

    /// <summary>UI 线程上下文：服务器回调来自后台线程，状态更新必须回到这里。</summary>
    private readonly SynchronizationContext? _ui;

    // ---------- 服务器状态 ----------
    private GameStateDto? _state;
    private IGameViewModel? _game;
    private int _selectedTargetId = -1;
    private int _lastStateHash = int.MinValue;

    // ---------- 界面状态 ----------
    private string _connectionStatus = "未连接";
    private string _logStatus = "";
    private string _roomId = "";
    private string _roomName = "";
    private string _hostName = "";
    private bool _isJoined;
    private bool _isConnected;
    private bool _isReconnecting;
    private int _latencyMs = -1;
    private bool _isAlive = true;
    private int _yourId;
    private bool _isHost;
    private bool _isRunning;
    private string _chatText = "";

    /// <summary>无参构造：供设计器与纯逻辑校验使用（自带一个未连接的客户端）。</summary>
    public GameViewModel() : this(new GameClient())
    {
    }

    public GameViewModel(GameClient client)
    {
        _client = client;
        _ui = SynchronizationContext.Current;

        DisconnectCommand = new AsyncRelayCommand(_ => LeaveRoomAsync());
        SendChatCommand = new RelayCommand(_ => SendChat());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        StartGameCommand = new AsyncRelayCommand(_ => StartGameAsync());

        // 心跳：每 20 秒测一次往返时延；失败即视为连接异常（后续由 SignalR 重连接管）。
        _heartbeat.Tick += (_, _) => _ = PingOnceAsync();

        // 消息条数变化时刷新「清空消息」菜单项的可用状态。
        _log.Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));

        _client.MessageReceived += message => OnUi(() => OnMessageReceived(message));
        _client.StateReceived += state => OnUi(() => OnStateReceived(state));
        _client.StatusChanged += status => OnUi(() => OnStatusChanged(status));
        _client.ConnectionClosed += ex => OnUi(() => OnConnectionClosed(ex));
        _client.Reconnected += info => OnUi(() => OnReconnected(info));

        // 进入主窗口时已经登录并成功加入房间（登录/房间列表负责连接与加入）。
        IsConnected = _client.IsConnected;
        IsJoined = _client.IsJoined;
        _roomId = _client.CurrentRoomId;
        _roomName = _client.CurrentRoomName;
        _hostName = _client.CurrentHostName;
        ConnectionStatus = _client.IsJoined ? "已加入房间"
            : _client.IsConnected ? "已连接服务器"
            : "未连接";

        // 进房（createroom / joinroom）早于本界面创建时，服务器那次状态广播没人接收，
        // 这里先用客户端缓存的状态回填一次，界面打开时就不会是空的；
        // 若缓存也没有，OnLoaded 会主动向服务器拉取（见 RefreshStateAsync）。
        if (_client.LastState != null) OnStateReceived(_client.LastState);
    }

    /// <summary>把回调切到 UI 线程执行。</summary>
    private void OnUi(Action action)
    {
        if (_ui == null) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    // ==================== 集合与插槽 ====================

    public ObservableCollection<PlayerRow> Players { get; } = new();

    /// <summary>房间消息（绑定在界面上的就是它）。</summary>
    public ObservableCollection<LogEntry> Log => _log.Entries;

    /// <summary>当前装载的游戏面板（随 <c>GameId</c> 切换）；标题栏胶囊绑定 <c>Game.Chips</c>。</summary>
    public IGameViewModel? Game
    {
        get => _game;
        private set
        {
            if (Set(ref _game, value)) OnPropertyChanged(nameof(ShowGamePanel));
        }
    }

    // ==================== 命令与事件 ====================

    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand SendChatCommand { get; }
    public AsyncRelayCommand StartGameCommand { get; }

    /// <summary>清空房间消息记录（菜单「视图 → 清空消息」）。</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>是否还有消息可清空（用于菜单项启用状态）。</summary>
    public bool HasLog => _log.Entries.Count > 0;

    /// <summary>退出房间（不拆连接）后触发，界面据此回到房间列表。</summary>
    public event Action? RoomLeft;

    // ==================== IGameHostBridge ====================

    /// <inheritdoc />
    GameClient IGameHostBridge.Client => _client;

    /// <inheritdoc />
    public int SelectedTargetId => _selectedTargetId;

    /// <inheritdoc />
    public int PlayerCount => Players.Count;

    /// <summary>
    /// 宿主视角下「是否满足开局的基本条件」（房主 + 未进行中）。
    /// 精细门禁（人数下限等）由游戏面板按自身规则判断。
    /// </summary>
    bool IGameHostBridge.CanStartGame => CanStartGame;

    /// <inheritdoc />
    public void AppendLog(string text, string kind = "info") => AddLog(text, kind);

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
            OnPropertyChanged(nameof(CanStartGame));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanLeaveRoom));
            OnPropertyChanged(nameof(ConnectionIndicatorText));
            OnPropertyChanged(nameof(ConnectionIndicatorBrush));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(HasLatency));
        }
    }

    /// <summary>是否正在自动重连（连接中断但尚未放弃）。</summary>
    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set
        {
            if (!Set(ref _isReconnecting, value)) return;
            OnPropertyChanged(nameof(ConnectionIndicatorText));
            OnPropertyChanged(nameof(ConnectionIndicatorBrush));
        }
    }

    /// <summary>最近一次心跳往返时延（毫秒）；−1 表示未测量。</summary>
    public int LatencyMs
    {
        get => _latencyMs;
        private set
        {
            if (!Set(ref _latencyMs, value)) return;
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(HasLatency));
        }
    }

    /// <summary>状态栏连接指示灯文案：已连接 / 重连中 / 未连接。</summary>
    public string ConnectionIndicatorText =>
        !IsConnected ? "🔴 未连接"
        : IsReconnecting ? "🟡 重连中…"
        : "🟢 已连接";

    /// <summary>连接指示灯配色。</summary>
    public Brush ConnectionIndicatorBrush =>
        !IsConnected ? UiBrushes.Dead
        : IsReconnecting ? UiBrushes.Warning
        : UiBrushes.Alive;

    /// <summary>状态栏延迟文案（未连接或未测量时为空）。</summary>
    public string LatencyText => HasLatency ? $"延迟 {LatencyMs} ms" : "";

    /// <summary>是否有可展示的延迟数值。</summary>
    public bool HasLatency => IsConnected && LatencyMs >= 0;

    /// <summary>已加入房间后允许主动退出。</summary>
    public bool CanLeaveRoom => IsJoined || IsConnected;

    /// <summary>主区域（三栏）只在房间内显示。</summary>
    public bool ShowGamePanel => IsJoined;

    public bool IsAlive
    {
        get => _isAlive;
        private set
        {
            if (!Set(ref _isAlive, value)) return;
            OnPropertyChanged(nameof(CanChat));
            OnPropertyChanged(nameof(ChatHint));
        }
    }

    public int YourId { get => _yourId; private set => Set(ref _yourId, value); }

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

    /// <summary>宿主视角「是否可以请求开始」（房主 + 未进行中）；精细门禁见游戏面板。</summary>
    public bool CanStartGame => IsHost && IsJoined && !_isRunning;
    public string StartHint => IsHost
        ? "你是房主，人齐后即可开始。"
        : "等待房主开始游戏…";
    public bool ShowStartHint => !IsHost;

    public string ChatText { get => _chatText; set => Set(ref _chatText, value); }

    /// <summary>
    /// 是否可发言：既要有房间 + 未出局，还要**游戏面板允许**（按顺序发言的玩法只有轮到自己才行）。
    /// </summary>
    public bool CanChat => IsJoined && IsAlive && (Game?.CanSendChat ?? true);

    /// <summary>不能发言时的说明（显示在输入框上）。</summary>
    public string ChatHint => !IsJoined ? ""
        : !IsAlive ? "你已出局，无法发言。"
        : (Game?.SendChatHint ?? "");

    // ---------------- 游戏标识（区分不同游戏的房间） ----------------

    private string _gameName = "";
    private string _gameEmoji = "";
    private string _gameAccent = "";

    /// <summary>当前房间的游戏显示名（服务端下发）。</summary>
    public string GameNameText => _gameName;

    /// <summary>「🐺 狼人杀」这样的游戏标识（未进房 / 未知时给中性文案）。</summary>
    public string GameLabelText => _gameName.Length == 0 ? "🎮 多游戏平台" : $"{_gameEmoji} {_gameName}";

    /// <summary>游戏主题色（十六进制）；空则用主题默认色。</summary>
    public string GameAccentHex => _gameAccent;

    /// <summary>窗口标题随房间绑定的游戏变化，让不同游戏的房间一眼可辨。</summary>
    public string WindowTitle => _gameName.Length == 0
        ? "多游戏平台 · 客户端"
        : $"{_gameEmoji} {_gameName} · 客户端";

    public string EndpointText => IsJoined ? $"服务器：{_client.Endpoint}" : "";

    // ==================== 房间信息 ====================

    /// <summary>当前房间号（来自状态推送）。</summary>
    public string RoomId
    {
        get => _roomId;
        private set
        {
            if (!Set(ref _roomId, value)) return;
            OnPropertyChanged(nameof(RoomHeaderText));
            OnPropertyChanged(nameof(HasRoomInfo));
        }
    }

    /// <summary>当前房间名。</summary>
    public string RoomName
    {
        get => _roomName;
        private set
        {
            if (!Set(ref _roomName, value)) return;
            OnPropertyChanged(nameof(RoomHeaderText));
        }
    }

    /// <summary>当前房主用户名。</summary>
    public string HostName
    {
        get => _hostName;
        private set
        {
            if (!Set(ref _hostName, value)) return;
            OnPropertyChanged(nameof(RoomHeaderText));
        }
    }

    /// <summary>标题条展示的「房间号 · 房间名 · 房主」。</summary>
    public string RoomHeaderText
    {
        get
        {
            if (string.IsNullOrEmpty(RoomId)) return "";
            var parts = new List<string> { $"房间 {RoomId}" };
            if (!string.IsNullOrEmpty(RoomName)) parts.Add(RoomName);
            if (!string.IsNullOrEmpty(HostName)) parts.Add($"房主：{HostName}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>是否有房间信息可展示。</summary>
    public bool HasRoomInfo => !string.IsNullOrEmpty(RoomId);

    /// <summary>状态栏右下角的版本标识（与「关于」窗口同源）。</summary>
    public string VersionText => $"客户端 GUI · v{AppInfo.Version}";

    // ==================== 邀请码 / 房间保留 ====================

    /// <summary>
    /// 生成当前房间的邀请码。邀请令牌只有房主能从服务器拿到（<c>hostsettings</c>），
    /// 因此非房主会返回带失败提示的结果。
    /// </summary>
    public async Task<(bool ok, string code, string message)> GenerateInviteCodeAsync()
    {
        if (!IsJoined) return (false, "", "请先加入或创建一个房间。");
        if (!IsHost) return (false, "", "只有房主可以生成邀请码。");

        try
        {
            var settings = await _client.GetHostSettingsAsync();
            if (!settings.Ok || settings.Settings == null)
                return (false, "", string.IsNullOrEmpty(settings.Message) ? "无法获取房间设置。" : settings.Message);

            var code = _client.CreateInviteCode(settings.Settings.InviteToken);
            return string.IsNullOrEmpty(code)
                ? (false, "", "无法生成邀请码。")
                : (true, code, "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    /// <summary>
    /// 是否需要询问「是否保留房间」：本人是房主、已进房，且房间内没有其他玩家。
    /// 房间内还有其他人时会自动把房主转让给下一位，无需询问。
    /// </summary>
    public bool NeedsKeepRoomDecision => IsJoined && IsHost && !Players.Any(p => !p.IsYou);

    /// <summary>
    /// 房主退出房间时是否需要「是否保留房间」的询问，以及询问结果。
    /// 仅在「本人是房主 + 已进房 + 房间内没有其他玩家」时询问；未注入询问（非 GUI 宿主）按「不保留」处理。
    /// </summary>
    public bool ResolveKeepRoom()
        => NeedsKeepRoomDecision && (ConfirmKeepRoom?.Invoke() ?? false);

    /// <summary>
    /// 房主退出且房间内没有其他人时，由视图注入「是否保留房间」的询问（true = 保留）。
    /// 未注入（例如非 GUI 宿主）时按「不保留」处理。
    /// </summary>
    public Func<bool>? ConfirmKeepRoom { get; set; }

    // ==================== 玩家选择 ====================

    /// <summary>左侧列表点选玩家（可再次点击取消）。是否可选由当前游戏面板决定。</summary>
    public void SelectPlayer(PlayerRow row)
    {
        var game = Game;
        if (game == null || !game.CanSelectPlayer(row)) return;

        _selectedTargetId = _selectedTargetId == row.Id ? -1 : row.Id;
        UpdateTargetHighlight();
        game.OnPlayerSelected(row);
    }

    private void UpdateTargetHighlight()
    {
        var game = Game;
        foreach (var p in Players)
        {
            p.Selectable = game != null && game.CanSelectPlayer(p);
            p.IsTarget = p.Id == _selectedTargetId;
        }
    }

    // ==================== 退出房间 ====================

    /// <summary>
    /// 程序收尾时复用「退出房间」的**同一条**路径（PRD E-10 / Q4-b）：
    /// 与菜单项唯一的差别是不弹「是否保留房间」——关窗时弹窗会阻塞退出，
    /// 按默认策略「不保留」处理。菜单里点「退出房间」仍然照旧弹。
    /// </summary>
    public Task LeaveRoomForShutdownAsync()
    {
        ConfirmKeepRoom = () => false;   // 抑制弹窗；LeaveRoomAsync 开头同步读取它
        return LeaveRoomAsync();
    }

    private async Task LeaveRoomAsync()
    {
        _heartbeat.Stop();

        // 房主退出且房间内没有其他人时，询问是否保留房间（仅 GUI 提供该交互）。
        var keepRoom = ResolveKeepRoom();

        // 只离开房间，不拆连接（连接由房间列表窗口继续复用）。
        try
        {
            var res = await _client.LeaveRoomAsync(keepRoom);
            if (!res.ok)
            {
                AddLog("⚠️ 退出房间失败：" + res.msg, LogKinds.Error);
                _heartbeat.Start();
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog("⚠️ 退出房间失败：" + ex.Message, LogKinds.Error);
            _heartbeat.Start();
            return;
        }

        IsJoined = false;
        LatencyMs = -1;
        _state = null;
        _selectedTargetId = -1;
        _lastStateHash = int.MinValue;
        _isRunning = false;
        Players.Clear();
        Game?.Reset();
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(CanStartGame));
        ChatText = "";
        IsAlive = true;
        IsHost = false;
        RoomId = "";
        RoomName = "";
        HostName = "";

        IsConnected = _client.IsConnected;
        ConnectionStatus = "已离开房间";
        LogStatus = keepRoom ? "已退出房间（房间已保留）。" : "已退出房间。";
        AddLog(keepRoom
            ? "🚪 已退出房间，房间已保留（可在房间列表重新进入）。"
            : "🚪 已退出房间，返回房间列表。");

        RoomLeft?.Invoke();
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
            LogStatus = ChatHint.Length > 0 ? ChatHint : "当前无法发言。";
            return;
        }

        // 发送后立刻清空输入框（v2.3.1）；失败时下面会把原文回填，不让用户白打一遍。
        ChatText = "";

        // 直接在 UI 线程上等待，异常只写日志，不冒泡到全局。
        _ = SendChatCoreAsync(text);
    }

    private async Task SendChatCoreAsync(string text)
    {
        try
        {
            // 用「带回执」的调用：轮流发言的玩法会在服务端拒绝插话，这里要把原因显示出来。
            var r = await _client.CallGameOpAsync(Ops.Chat,
                JsonSerializer.SerializeToNode(new ChatPayload { Text = text }));

            if (r.ok) return;

            AddLog("⚠️ " + (string.IsNullOrEmpty(r.msg) ? "发言失败。" : r.msg), LogKinds.Error);
            if (ChatText.Length == 0) ChatText = text;   // 没发出去就把原文还给用户
        }
        catch (Exception ex)
        {
            AddLog("⚠️ 发言失败：" + ex.Message, LogKinds.Error);
            if (ChatText.Length == 0) ChatText = text;
        }
    }

    // ==================== 服务器事件 ====================

    private void OnStatusChanged(string status)
    {
        ConnectionStatus = status;

        // 连接状态可能随重连过程变化，一并同步指示灯。
        IsConnected = _client.IsConnected;
        IsReconnecting = _client.IsReconnecting;
    }

    private void OnConnectionClosed(Exception? ex)
    {
        IsConnected = false;
        IsReconnecting = false;
        LatencyMs = -1;
        if (!IsJoined) return;
        ConnectionStatus = "连接已断开";
        AddLog("⚠️ 与服务器的连接已断开。", LogKinds.Error);
    }

    private void OnReconnected(string info)
    {
        IsConnected = true;
        IsReconnecting = false;
        AddLog("🔄 " + info);
    }

    /// <summary>心跳一次：测量往返时延；失败时只提示，等待自动重连接管。</summary>
    private async Task PingOnceAsync()
    {
        if (!IsConnected) return;

        try
        {
            LatencyMs = await _client.PingAsync();
        }
        catch (Exception ex)
        {
            LatencyMs = -1;
            LogStatus = "心跳失败：" + ex.Message;
        }
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

        // 按 GameId 切换游戏面板（新游戏 = 新面板；未知游戏 = 兜底面板）。
        if (Game == null || Game.GameId != s.GameId)
            Game = GameViewModelFactory.Create(s.GameId, _client, this);

        _isRunning = s.Running;
        IsAlive = s.IsAlive;
        YourId = s.YourId;
        IsHost = s.Players.FirstOrDefault(p => p.IsYou)?.IsHost ?? false;

        // 房间信息随状态下发（旧服务端可能为空，保留原有值）。
        if (!string.IsNullOrEmpty(s.RoomId)) RoomId = s.RoomId;
        RoomName = s.RoomName;
        HostName = s.HostName;

        // 游戏标识（显示名 / 图标 / 主题色）：用于标题栏与房间头部区分不同游戏。
        if (!string.IsNullOrEmpty(s.GameName))
        {
            _gameName = s.GameName;
            _gameEmoji = s.GameEmoji;
            _gameAccent = s.GameAccent;
            OnPropertyChanged(nameof(GameNameText));
            OnPropertyChanged(nameof(GameLabelText));
            OnPropertyChanged(nameof(GameAccentHex));
            OnPropertyChanged(nameof(WindowTitle));
        }

        // 收到带房间号的状态即确认「已在房间」——不依赖构造时机，避免进房早于界面创建时判断失准。
        if (!string.IsNullOrEmpty(s.RoomId)) IsJoined = true;

        // 宿主不解析游戏私有负载；任何状态变化都可能意味着换回合，保守清空选中目标。
        _selectedTargetId = -1;

        RefreshPlayers();
        Game!.OnState(s);
        UpdateTargetHighlight();

        // 面板可能改变了「现在能不能发言」（如换成别人发言）→ 同步聊天区的可用状态。
        OnPropertyChanged(nameof(CanChat));
        OnPropertyChanged(nameof(ChatHint));
        OnPropertyChanged(nameof(CanStartGame));
    }

    private static int ComputeHash(GameStateDto s)
    {
        var h = new HashCode();
        h.Add(s.GameId);
        h.Add(s.RoomId);
        h.Add(s.Running);
        h.Add(s.Status);
        h.Add(s.YourId);
        h.Add(s.IsAlive);
        h.Add(s.GamePayload?.ToJsonString());
        foreach (var p in s.Players)
        {
            h.Add(p.Id);
            h.Add(p.Name);
            h.Add(p.IsAlive);
            h.Add(p.IsHost);
            h.Add(p.IsYou);
            h.Add(p.IsConnected);
            h.Add(p.Tag);
            h.Add(p.TagColor);
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
            Players.Add(new PlayerRow
            {
                Id = p.Id,
                Name = p.Name,
                IsAlive = p.IsAlive,
                IsHost = p.IsHost,
                IsYou = p.IsYou,
                IsConnected = p.IsConnected,
                Tag = p.Tag,
                TagColor = p.TagColor
            });
        }

        // 人数变化会影响“能否开始游戏”，需要让菜单/按钮重新求值。
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(CanStartGame));
    }

    // ==================== 生命周期 ====================

    private void AddLog(string text, string kind = LogKinds.Info) => _log.Add(text, kind);

    /// <summary>清空消息记录，并在状态栏给出反馈。</summary>
    private void ClearLog()
    {
        if (!HasLog) return;
        _log.Clear();
        LogStatus = "已清空消息记录。";
    }

    public void OnLoaded()
    {
        if (Log.Count == 0)
        {
            AddLog("🐺 狼人杀客户端已就绪。");
            AddLog("点击左侧玩家选择目标，右侧提交本阶段的操作；出局后仍可旁观。");
        }

        // 进入主窗口后开始心跳（此时必然已连接）。
        if (IsConnected) _heartbeat.Start();

        // 主动补一次房间状态：进房广播可能早于本界面创建（那时没人接收），
        // 不补的话玩家列表、房主标识、开始游戏按钮都会是空的。
        _ = RefreshStateAsync();
    }

    /// <summary>向服务器拉取一次当前房间状态（失败只记录，不影响使用）。</summary>
    private async Task RefreshStateAsync()
    {
        if (!IsJoined) return;

        try
        {
            await _client.RefreshStateAsync();
        }
        catch (Exception ex)
        {
            LogStatus = "刷新房间状态失败：" + ex.Message;
        }
    }

    /// <summary>窗口关闭时尽力断开连接。</summary>
    public async Task DisposeAsync()
    {
        _heartbeat.Stop();
        try { await _client.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
    }
}
