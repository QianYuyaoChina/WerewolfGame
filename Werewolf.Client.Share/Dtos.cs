using System.Text.Json.Nodes;

namespace Werewolf.Client.Share;

/// <summary>与服务器 <c>PlayerDto</c> 对应的传输对象（通用信封的一部分）。</summary>
public class PlayerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsAlive { get; set; } = true;
    public bool IsHost { get; set; }
    public bool IsYou { get; set; }
    public bool IsConnected { get; set; } = true;

    /// <summary>通用：游戏自定义短标签（狼人杀=身份名；卧底=「已发言」等）；无标签为 null。</summary>
    public string? Tag { get; set; }

    /// <summary>通用：标签颜色（十六进制，如 "#FFA07A"）；可空，空则由界面用主题默认色。</summary>
    public string? TagColor { get; set; }
}

/// <summary>
/// 与服务器 <c>GameStateDto</c> 对应的传输对象（通用信封 + 游戏私有负载）。
/// 平台只填充通用字段；各游戏通过 <see cref="GamePayload"/> 携带自己的私有状态，
/// 每个玩家独立生成一份（视角隔离）。
/// </summary>
public class GameStateDto
{
    /// <summary>房间绑定的游戏机器名（<see cref="GameIds"/>）。</summary>
    public string GameId { get; set; } = GameIds.Werewolf;

    /// <summary>当前所属房间号（5 位）。</summary>
    public string RoomId { get; set; } = "";

    /// <summary>当前房间名。</summary>
    public string RoomName { get; set; } = "";

    /// <summary>当前房主用户名。</summary>
    public string HostName { get; set; } = "";

    /// <summary>是否进行中（等价于 <see cref="Status"/> == Running）。</summary>
    public bool Running { get; set; }

    /// <summary>与房间列表一致的状态（"Waiting" | "Running" | "GameOver"，用 <see cref="RoomStatus"/>）。</summary>
    public string Status { get; set; } = RoomStatus.Waiting;

    /// <summary>「你」在玩家列表中的编号（与 <see cref="PlayerDto.Id"/> 一致）。</summary>
    public int YourId { get; set; }

    /// <summary>通用：是否仍在游戏中（未出局）。</summary>
    public bool IsAlive { get; set; } = true;

    public List<PlayerDto> Players { get; set; } = new();

    /// <summary>
    /// 游戏私有负载（各游戏自定义，框架原样透传）。
    /// 狼人杀解析为 <see cref="WerewolfGameState"/>；其他游戏各有自己的类型。
    /// </summary>
    public JsonNode? GamePayload { get; set; }
}

/// <summary>服务器 <c>Join</c> / <c>Start</c> 的匿名返回值。</summary>
public class JoinResult
{
    public bool ok { get; set; }
    public string msg { get; set; } = "";
}

/// <summary>可用游戏列表结果（gamelist 的 Payload；同时用作客户端返回类型）。</summary>
public sealed class GameListResult
{
    /// <summary>客户端用；服务端 payload 中恒 true。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>客户端用；服务端 payload 中恒 ""。</summary>
    public string Msg { get; set; } = "";

    public List<GameInfo> Games { get; set; } = new();
}
