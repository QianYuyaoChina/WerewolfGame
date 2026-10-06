using Werewolf.Client.Share;

namespace Werewolf.Server.Games.Undercover;

/// <summary>
/// 谁是卧底的每玩家私有状态（玩法状态只在游戏侧；存活 / 在线等以平台 <see cref="RoomPlayer"/> 为准）。
/// </summary>
public sealed class UndercoverPlayer
{
    public UndercoverPlayer(RoomPlayer player, string role)
    {
        Player = player;
        Role = role;
    }

    /// <summary>关联的平台玩家（同一实例）。</summary>
    public RoomPlayer Player { get; set; }

    /// <summary>本局身份：<see cref="UndercoverRoles"/> 里的常量。</summary>
    public string Role { get; set; }

    /// <summary>本局拿到的词；白板为空字符串。</summary>
    public string Word { get; set; } = "";

    // ---- 投票 ----
    public int VoteTarget { get; set; } = -1;
    public bool VoteSubmitted { get; set; }

    /// <summary>描述阶段是否已点「我说完了」。</summary>
    public bool ReadySubmitted { get; set; }

    /// <summary>是否为卧底。</summary>
    public bool IsUndercover => Role == UndercoverRoles.Undercover;

    /// <summary>是否为白板（没有词）。</summary>
    public bool IsBlank => Role == UndercoverRoles.Blank;

    /// <summary>是否为平民（拿到多数派的词）。</summary>
    public bool IsCivilian => Role == UndercoverRoles.Civilian;

    // ---- 平台字段便捷透传 ----
    public string Username => Player.Username;
    public string ConnectionId => Player.ConnectionId;
    public bool IsConnected => Player.IsConnected;
    public bool IsAlive
    {
        get => Player.IsAlive;
        set => Player.IsAlive = value;
    }
}
