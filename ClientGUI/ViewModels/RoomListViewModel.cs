using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClientGUI.Services;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>房间列表中的一行（展示用模型）。</summary>
public sealed class RoomRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>房间绑定的游戏机器名。</summary>
    public string GameId { get; init; } = GameIds.Werewolf;

    /// <summary>游戏显示名（服务器冗余下发，便于即时渲染）。</summary>
    public string GameName { get; init; } = "";

    /// <summary>游戏图标。</summary>
    public string GameEmoji { get; init; } = "";

    /// <summary>游戏主题色（十六进制），列表里用它给「游戏」列上色，不同游戏的房间一眼可辨。</summary>
    public string GameAccent { get; init; } = "";

    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public string Status { get; init; } = "";
    public bool HasPassword { get; init; }
    public string Host { get; init; } = "";

    /// <summary>
    /// 房主的称号（服务端权威，随房间列表自然下发）。空 = 无称号 → 界面不渲染称号胶囊。
    /// </summary>
    public string HostTitle { get; init; } = "";

    public string CountText => $"{PlayerCount}/{MaxPlayers}";
    public string StatusText => RoomStatus.Name(Status);
    public string LockText => HasPassword ? "🔒" : "—";
    public string HostText => string.IsNullOrWhiteSpace(Host) ? "—" : Host;

    /// <summary>「图标 + 显示名」；缺失时回退到机器名。</summary>
    public string GameText => (GameEmoji, GameName) switch
    {
        ("", "") => string.IsNullOrWhiteSpace(GameId) ? "—" : GameId,
        ("", _) => GameName,
        (_, "") => GameEmoji,
        _ => $"{GameEmoji} {GameName}"
    };
}

/// <summary>
/// 房间列表窗口的视图模型：进入即拉取一次列表，之后每 5 秒轮询（另有手动刷新）。
/// 选中加密房间时启用密码框；加入失败在状态行标红；创建成功后直接进入房间。
/// </summary>
public sealed class RoomListViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly GameClient _client;
    private readonly SynchronizationContext? _ui;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    /// <summary>
    /// 彩蛋计数器（3 秒 5 击）。放在视图模型里而不是窗口代码里：判定是纯逻辑，
    /// 窗口只负责把「按下了」这件事转进来（架构 §8.1）。
    /// </summary>
    private readonly EggTapCounter _eggTap = new();

    // ---------- 订阅句柄（页面化后必须能退订，架构 §6.2） ----------
    // 静态事件 EmbeddedServerService.Changed 原来接的是匿名 lambda，**无法 -=**；
    // 页面化之后房间列表页会被反复进出（列表 ↔ 房间），不退订就按次数累积 → 提成字段。

    private readonly EventHandler _onTimerTick;
    private readonly Action _onServerChanged;

    private RoomRow? _selectedRoom;
    private string _password = "";
    private string _inviteCode = "";
    private string _status = "正在加载房间列表…";
    private bool _showError;
    private bool _isBusy;
    private int _roomCount;

    public RoomListViewModel(GameClient client)
    {
        _client = client;
        _ui = SynchronizationContext.Current;

        _onTimerTick = (_, _) => _ = RefreshAsync();
        _timer.Tick += _onTimerTick;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        JoinCommand = new AsyncRelayCommand(_ => JoinSelectedAsync(), _ => SelectedRoom != null && !IsBusy);
        UseInviteCommand = new AsyncRelayCommand(_ => UseInviteAsync(), _ => !IsBusy);

        // 内置服务器启停时同步导航栏的运行胶囊（关主窗口不停服务器，PRD 8.6）。
        _onServerChanged = () => Post(NotifyServerStatusChanged);
        EmbeddedServerService.Current.Changed += _onServerChanged;
    }

    // ==================== 集合与命令 ====================

    public ObservableCollection<RoomRow> Rooms { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }

    /// <summary>用邀请码直接加入（邀请码里带了服务器地址与房间密码）。</summary>
    public AsyncRelayCommand UseInviteCommand { get; }

    /// <summary>是否已成功进入房间（窗口据此关闭并交回控制权）。</summary>
    public bool EnteredRoom { get; private set; }

    /// <summary>成功进入房间后触发（成功创建 / 加入都会触发）。</summary>
    public event Action? RoomEntered;

    // ==================== 绑定属性 ====================

    public RoomRow? SelectedRoom
    {
        get => _selectedRoom;
        set
        {
            if (!Set(ref _selectedRoom, value)) return;
            Password = "";
            OnPropertyChanged(nameof(IsPasswordEnabled));
            OnPropertyChanged(nameof(SelectionText));
        }
    }

    public string Password { get => _password; set => Set(ref _password, value); }

    /// <summary>邀请码输入（可选）：粘贴好友分享的码后点「加入」。</summary>
    public string InviteCode { get => _inviteCode; set => Set(ref _inviteCode, value); }

    /// <summary>仅当选中的房间需要密码时启用密码框。</summary>
    public bool IsPasswordEnabled => SelectedRoom?.HasPassword == true;

    public string SelectionText => SelectedRoom == null
        ? "请选择房间后点击「加入房间」"
        : $"已选：{SelectedRoom.Id} · {SelectedRoom.GameText} · {SelectedRoom.Name}";

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>最近一次操作是否失败（用于把状态行标红）。</summary>
    public bool ShowError { get => _showError; private set => Set(ref _showError, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    public int RoomCount { get => _roomCount; private set => Set(ref _roomCount, value); }

    /// <summary>房间列表为空（用于显示空态提示）。</summary>
    public bool IsEmpty => RoomCount == 0;

    // ==================== 内置服务器运行状态（PRD 8.6） ====================

    /// <summary>本进程拉起的内置服务器是否正在运行。</summary>
    public bool IsEmbeddedServerRunning => EmbeddedServerService.Current.IsRunning;

    /// <summary>运行胶囊的文案（未运行时为空，胶囊整体收起）。</summary>
    public string EmbeddedServerText => IsEmbeddedServerRunning ? "内置服务器运行中" : "";

    private void NotifyServerStatusChanged()
    {
        OnPropertyChanged(nameof(IsEmbeddedServerRunning));
        OnPropertyChanged(nameof(EmbeddedServerText));
    }

    /// <summary>把回调切回 UI 线程（服务的通知可能来自后台线程）。</summary>
    private void Post(Action action)
    {
        if (_ui == null) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    // ==================== 彩蛋（第 3 条） ====================

    /// <summary>
    /// 记一次「点中导航标题」。返回 true = 命中「3 秒 5 击」，调用方据此弹出输入框。
    /// 未达成（次数不足 / 超时）一律返回 false 且**静默清零**，不给任何可见反馈。
    /// </summary>
    public bool RegisterTitleTap() => _eggTap.RegisterTap(Environment.TickCount64);

    /// <summary>复位彩蛋计数（关掉输入框后调用，下次重新点 5 下才会再弹）。</summary>
    public void ResetTitleTaps() => _eggTap.Reset();

    /// <summary>在状态行显示一句普通提示（不标红），如「称号已保存」。</summary>
    public void ShowNotice(string message)
    {
        ShowError = false;
        Status = message;
    }

    // ==================== 轮询 ====================

    public void StartPolling() => _timer.Start();

    public void StopPolling() => _timer.Stop();

    /// <summary>轮询是否在跑（页面退场后必须为 false，架构 P9 的可断言点）。</summary>
    public bool IsPolling => _timer.IsEnabled;

    /// <summary>是否已退订（<see cref="Detach"/> 幂等：第二次调用直接返回）。</summary>
    public bool IsDetached { get; private set; }

    /// <summary>
    /// 停轮询 + 退订**全部**外部事件（页面退场时调用，架构 §6.2）。
    /// 重点是那条静态事件 <see cref="EmbeddedServerService.Changed"/>：
    /// 它比页面活得久，忘了退就按「进列表页次数」累积。
    /// </summary>
    public void Detach()
    {
        if (IsDetached) return;
        IsDetached = true;

        StopPolling();
        _timer.Tick -= _onTimerTick;
        EmbeddedServerService.Current.Changed -= _onServerChanged;
    }

    // ==================== 操作 ====================

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var previousId = SelectedRoom?.Id;

            var result = await _client.ListRoomsAsync();
            if (!result.Ok)
            {
                SetError(string.IsNullOrEmpty(result.Msg) ? "无法获取房间列表。" : result.Msg);
                return;
            }

            Rooms.Clear();
            foreach (var dto in result.Rooms)
            {
                Rooms.Add(new RoomRow
                {
                    Id = dto.Id,
                    Name = dto.Name,
                    GameId = dto.GameId,
                    GameName = dto.GameName,
                    GameEmoji = dto.GameEmoji,
                    GameAccent = dto.GameAccent,
                    PlayerCount = dto.PlayerCount,
                    MaxPlayers = dto.MaxPlayers,
                    Status = dto.Status,
                    HasPassword = dto.HasPassword,
                    Host = dto.Host,
                    HostTitle = dto.HostTitle
                });
            }

            RoomCount = Rooms.Count;
            OnPropertyChanged(nameof(IsEmpty));

            // 保持之前选中的房间（若仍在列表中）。
            SelectedRoom = previousId == null ? null : Rooms.FirstOrDefault(r => r.Id == previousId);

            ShowError = false;
            Status = Rooms.Count == 0
                ? "暂无房间，点击「创建房间」开一局吧。"
                : $"共 {Rooms.Count} 个房间。";
        }
        catch (Exception ex)
        {
            SetError("无法获取房间列表：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加入选中的房间（加密房间需先输入密码）。</summary>
    public async Task JoinSelectedAsync()
    {
        var room = SelectedRoom;
        if (room == null)
        {
            SetError("请先选择一个房间。");
            return;
        }

        if (room.HasPassword && string.IsNullOrEmpty(Password))
        {
            SetError("该房间需要密码。");
            return;
        }

        IsBusy = true;
        try
        {
            var rr = await _client.JoinRoomAsync(room.Id, room.HasPassword ? Password : null);
            if (!rr.Ok)
            {
                SetError(string.IsNullOrEmpty(rr.Message) ? "加入失败。" : rr.Message);
                return;
            }

            ShowError = false;
            Status = $"已加入房间 {rr.RoomId}。";
            EnteredRoom = true;
            RoomEntered?.Invoke();
        }
        catch (Exception ex)
        {
            SetError("加入失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>创建房间并直接进入（密码为 null/空表示公开房间；gameId 缺失 → 服务端回退狼人杀）。</summary>
    public async Task CreateAndJoinAsync(string name, string? password, string? gameId)
    {
        IsBusy = true;
        try
        {
            var rr = await _client.CreateRoomAsync(name, password, gameId);
            if (!rr.Ok)
            {
                SetError(string.IsNullOrEmpty(rr.Message) ? "创建失败。" : rr.Message);
                return;
            }

            ShowError = false;
            Status = $"已创建并进入房间 {rr.RoomId}。";
            EnteredRoom = true;
            RoomEntered?.Invoke();
        }
        catch (Exception ex)
        {
            SetError("创建失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 用邀请码快速加入：码里带服务器地址与房间密码，
    /// 若当前连的不是同一台服务器，会自动切换并重新登录后进房。
    /// </summary>
    public async Task UseInviteAsync()
    {
        if (!GameClient.TryParseInvite(InviteCode, out var invite, out var error) || invite == null)
        {
            SetError(error);
            return;
        }

        IsBusy = true;
        try
        {
            var rr = await _client.JoinByInviteAsync(invite);
            if (!rr.Ok)
            {
                SetError(string.IsNullOrEmpty(rr.Message) ? "加入失败。" : rr.Message);
                return;
            }

            ShowError = false;
            Status = $"已通过邀请码加入房间 {rr.RoomId}。";
            InviteCode = "";
            EnteredRoom = true;
            RoomEntered?.Invoke();
        }
        catch (Exception ex)
        {
            SetError("加入失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetError(string message)
    {
        ShowError = true;
        Status = message;
    }
}
