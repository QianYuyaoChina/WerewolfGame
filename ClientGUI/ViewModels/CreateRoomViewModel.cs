using System.Collections.ObjectModel;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 创建房间对话框的视图模型：房间名（可留空 → 服务端回退默认名）+ 可选密码 + 选择游戏（默认狼人杀）。
/// 勾选「不设密码」即创建公开房间。
/// </summary>
public sealed class CreateRoomViewModel : ObservableObject
{
    private readonly GameClient? _client;

    private string _roomName = "";
    private string _password = "";
    private GameInfo? _selectedGame;

    public CreateRoomViewModel(GameClient? client = null)
    {
        _client = client;
        CreateCommand = new RelayCommand(_ => Create());

        // 预置狼人杀，保证在游戏列表拉取完成前也始终有可选项（默认选中狼人杀）。
        SelectedGame = DefaultWerewolf();
        Games.Add(SelectedGame);
    }

    /// <summary>校验通过、可提交创建时触发。</summary>
    public event Action? CreateSucceeded;

    public RelayCommand CreateCommand { get; }

    /// <summary>可选游戏列表（服务器下发；拉取失败时保留默认狼人杀）。</summary>
    public ObservableCollection<GameInfo> Games { get; } = new();

    /// <summary>当前选中的游戏；默认狼人杀。</summary>
    public GameInfo? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (Set(ref _selectedGame, value)) OnPropertyChanged(nameof(SelectedGameId));
        }
    }

    /// <summary>选中的游戏机器名（供创建房间使用）；未选时回退狼人杀。</summary>
    public string SelectedGameId => SelectedGame?.GameId ?? GameIds.Default;

    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    /// <summary>房间密码；<b>留空即公开房间</b>（不再需要单独勾选「不设密码」）。</summary>
    public string Password { get => _password; set => Set(ref _password, value); }

    /// <summary>
    /// 拉取服务器可用游戏填充下拉；失败/为空时保留默认狼人杀。
    /// 尽量保持当前选中的游戏（按 GameId 匹配）。
    /// </summary>
    public async Task LoadGamesAsync()
    {
        if (_client == null) return;

        try
        {
            var result = await _client.ListGamesAsync();
            if (!result.Ok || result.Games.Count == 0) return;

            var keepId = SelectedGameId;

            Games.Clear();
            foreach (var game in result.Games) Games.Add(game);

            SelectedGame =
                Games.FirstOrDefault(g => g.GameId == keepId)
                ?? Games.FirstOrDefault(g => g.GameId == GameIds.Default)
                ?? Games[0];
        }
        catch
        {
            // 拉取失败 → 保留默认狼人杀，创建流程照常可用。
        }
    }

    private void Create()
    {
        // 房间名可留空（服务端回退默认名）；密码留空即公开房间；游戏取 SelectedGameId。
        CreateSucceeded?.Invoke();
    }

    /// <summary>默认狼人杀元数据（与服务端 <c>WerewolfGameModule.Info</c> 对齐）。</summary>
    private static GameInfo DefaultWerewolf() => new()
    {
        GameId = GameIds.Werewolf,
        DisplayName = "狼人杀",
        Emoji = "🐺",
        Tagline = "村民与狼人的推理对抗，夜晚与白天交替进行",
        MinPlayers = 4,
        MaxPlayers = 12
    };
}
