namespace Werewolf.Server.Games.Werewolf;

/// <summary>
/// 狼人杀的每玩家私有状态（从原 <c>Player</c> 拆出的狼人字段）。
/// 通过 <see cref="Player"/> 关联平台通用玩家（<see cref="RoomPlayer"/>）：存活 / 在线等以平台为准，
/// 身份 / 药水 / 夜晚与投票数据等玩法状态只在本类型上。
/// </summary>
public sealed class WerewolfPlayer
{
    public WerewolfPlayer(RoomPlayer player)
    {
        Player = player;
    }

    /// <summary>关联的平台玩家（同一实例，平台字段以它为准）。</summary>
    public RoomPlayer Player { get; set; }

    /// <summary>本局身份。</summary>
    public Role Role { get; set; }

    // ---- 女巫 / 守卫状态 ----
    public bool HasAntidote { get; set; } = true;
    public bool HasPoison { get; set; } = true;
    public int LastGuardTarget { get; set; } = -1;

    // ---- 夜间行动 ----
    public int NightTarget { get; set; } = -1;

    /// <summary>第二名目标（仅丘比特用：情侣的另一半）。</summary>
    public int NightTarget2 { get; set; } = -1;

    public string? NightActionType { get; set; }
    public bool NightActionSubmitted { get; set; }

    // ---- 投票 ----
    public int VoteTarget { get; set; } = -1;
    public bool VoteSubmitted { get; set; }

    // ---- 猎人 ----
    public bool HunterCanShoot { get; set; }
    public bool HunterShotSubmitted { get; set; }

    // ---- 丘比特（情侣关系，双向记录） ----
    /// <summary>情侣另一方的用户名；不是情侣时为 null。</summary>
    public string? LoverUsername { get; set; }

    // ---- 警长 ----
    /// <summary>是否持有警徽（本局配置了警长身份且未被淘汰时才可能为 true）。</summary>
    public bool HasBadge { get; set; }

    /// <summary>警徽移交给谁（-1 表示未选/放弃）。</summary>
    public int SheriffTransferTarget { get; set; } = -1;

    /// <summary>警徽移交是否已提交。</summary>
    public bool SheriffTransferSubmitted { get; set; }

    /// <summary>是否为狼人阵营。</summary>
    public bool IsWolf => Role == Role.Werewolf;

    /// <summary>是否为情侣。</summary>
    public bool IsLover => !string.IsNullOrEmpty(LoverUsername);

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
