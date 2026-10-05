using Werewolf.Client.Share;

namespace ClientGUI.ViewModels.Games;

/// <summary>
/// 兜底面板的视图模型：服务器上线了本客户端不认识的游戏时使用。
/// 只提示「本客户端暂不支持该游戏」，不影响聊天 / 玩家列表等宿主功能。
/// </summary>
public sealed class UnknownGameViewModel : ObservableObject, IGameViewModel
{
    private static readonly IReadOnlyList<GameAction> NoActions = Array.Empty<GameAction>();
    private static readonly IReadOnlyList<StatusChip> NoChips = Array.Empty<StatusChip>();

    private string _hint;

    public UnknownGameViewModel(string gameId)
    {
        GameId = string.IsNullOrWhiteSpace(gameId) ? "(未知)" : gameId;
        _hint = $"本客户端暂不支持游戏「{GameId}」，请升级客户端后再试。聊天与玩家列表仍可正常使用。";
    }

    /// <inheritdoc />
    public string GameId { get; }

    /// <inheritdoc />
    public IReadOnlyList<GameAction> Actions => NoActions;

    /// <inheritdoc />
    public IReadOnlyList<StatusChip> Chips => NoChips;

    /// <inheritdoc />
    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    /// <inheritdoc />
    public bool CanSelectPlayer(PlayerRow row) => false;

    /// <inheritdoc />
    public void OnPlayerSelected(PlayerRow row) { }

    /// <inheritdoc />
    public void OnState(GameStateDto state) { }

    /// <inheritdoc />
    public void Refresh() { }

    /// <inheritdoc />
    public void Reset() => Hint = "本客户端暂不支持当前游戏。";
}
