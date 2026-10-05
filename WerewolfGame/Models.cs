namespace Werewolf.Server;

/// <summary>
/// 房间内的玩家：平台通用字段（房间身份 / 连接 / 房主 / 存活）。
/// 平台的房间宿主、聊天门禁、列表只依赖这些通用字段；狼人杀的私有字段
/// （身份 / 药水 / 夜晚目标 / 投票 / 猎人状态）见 <c>Werewolf.Server.Games.Werewolf.WerewolfPlayer</c>。
/// </summary>
public class RoomPlayer
{
    /// <summary>唯一标识（用户名，忽略大小写比较）。</summary>
    public string Username { get; set; } = "";

    /// <summary>当前连接号；掉线时为空字符串。</summary>
    public string ConnectionId { get; set; } = "";

    /// <summary>是否在线（已连接且持有连接号）。</summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>是否为房主。</summary>
    public bool IsHost { get; set; }

    /// <summary>是否仍在游戏中（未出局）。由游戏写、平台读（聊天门禁 / 列表）。</summary>
    public bool IsAlive { get; set; } = true;
}

// 传输对象（PlayerDto / GameStateDto / RoomInfoDto 等）统一定义在共享库 Werewolf.Client.Share，
// 服务端与两个客户端复用同一份，避免字段漂移。
