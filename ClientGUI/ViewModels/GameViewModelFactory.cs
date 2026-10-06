using ClientGUI.ViewModels.Games;
using ClientGUI.ViewModels.Games.Undercover;
using ClientGUI.ViewModels.Games.Werewolf;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// GUI 注册点：<c>GameId → 面板视图模型</c>。新游戏加一条 case 即可（不是平台模块）。
/// 遇到服务器已上线但本客户端不认识的 GameId → 兜底 <see cref="UnknownGameViewModel"/>。
/// </summary>
internal static class GameViewModelFactory
{
    public static IGameViewModel Create(string gameId, GameClient client, IGameHostBridge bridge)
    {
        var id = (gameId ?? "").Trim();
        return id switch
        {
            GameIds.Werewolf => new WerewolfViewModel(client, bridge),
            GameIds.Undercover => new UndercoverViewModel(client, bridge),
            _ => new UnknownGameViewModel(id)
        };
    }
}
