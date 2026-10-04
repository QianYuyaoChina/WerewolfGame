using System.Collections.ObjectModel;
using System.Windows.Threading;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>房间列表中的一行（展示用模型）。</summary>
public sealed class RoomRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public string Status { get; init; } = "";
    public bool HasPassword { get; init; }
    public string Host { get; init; } = "";

    public string CountText => $"{PlayerCount}/{MaxPlayers}";
    public string StatusText => RoomStatus.Name(Status);
    public string LockText => HasPassword ? "🔒" : "—";
    public string HostText => string.IsNullOrWhiteSpace(Host) ? "—" : Host;
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

    private RoomRow? _selectedRoom;
    private string _password = "";
    private string _status = "正在加载房间列表…";
    private bool _showError;
    private bool _isBusy;
    private int _roomCount;

    public RoomListViewModel(GameClient client)
    {
        _client = client;
        _ui = SynchronizationContext.Current;

        _timer.Tick += (_, _) => _ = RefreshAsync();

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        JoinCommand = new AsyncRelayCommand(_ => JoinSelectedAsync(), _ => SelectedRoom != null && !IsBusy);
    }

    // ==================== 集合与命令 ====================

    public ObservableCollection<RoomRow> Rooms { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }

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

    /// <summary>仅当选中的房间需要密码时启用密码框。</summary>
    public bool IsPasswordEnabled => SelectedRoom?.HasPassword == true;

    public string SelectionText => SelectedRoom == null
        ? "请选择房间后点击「加入房间」"
        : $"已选：{SelectedRoom.Id} · {SelectedRoom.Name}";

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

    // ==================== 轮询 ====================

    public void StartPolling() => _timer.Start();

    public void StopPolling() => _timer.Stop();

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
                    PlayerCount = dto.PlayerCount,
                    MaxPlayers = dto.MaxPlayers,
                    Status = dto.Status,
                    HasPassword = dto.HasPassword,
                    Host = dto.Host
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

    /// <summary>创建房间并直接进入（密码为 null/空表示公开房间）。</summary>
    public async Task CreateAndJoinAsync(string name, string? password)
    {
        IsBusy = true;
        try
        {
            var rr = await _client.CreateRoomAsync(name, password);
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

    private void SetError(string message)
    {
        ShowError = true;
        Status = message;
    }
}
