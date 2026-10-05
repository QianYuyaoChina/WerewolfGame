using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace Werewolf.Server.Games;

/// <summary>游戏状态：框架据此决定房间状态（列表 Status）与「进行中能否加入」。</summary>
public enum GameStatus
{
    Waiting,
    Running,
    GameOver
}

/// <summary>该玩家行的游戏自定义标签（狼人杀 = 身份名 + 颜色）；无标签返回 null。</summary>
public readonly record struct PlayerTag(string Text, string? ColorHex);

/// <summary>
/// 房间暴露给游戏实例的「平台能力」门面。房间实现它，游戏只依赖它。
/// 线程 / 锁约定：实现方内部自行加锁，调用方不应假设无锁；不得在持有房间锁时 await。
/// </summary>
public interface IGameContext
{
    /// <summary>房间号（5 位）。</summary>
    string RoomId { get; }

    /// <summary>房间名。</summary>
    string RoomName { get; }

    /// <summary>玩家快照，顺序 = 玩家编号（与 <see cref="PlayerDto.Id"/> 一致）。仅在本调用内有效。</summary>
    IReadOnlyList<RoomPlayer> Players { get; }

    /// <summary>在线（可推送）玩家快照。</summary>
    IReadOnlyList<RoomPlayer> OnlinePlayers { get; }

    RoomPlayer? FindByConnection(string connId);

    RoomPlayer? FindByName(string username);

    /// <summary>只读配置门面：游戏自取规则，无需改组合根。</summary>
    IGameConfig Config { get; }

    /// <summary>广播一条系统消息（加密后发所有在线玩家）。</summary>
    Task BroadcastAsync(string text);

    /// <summary>向单个连接私聊一条消息（加密）。</summary>
    Task PrivateAsync(string connectionId, string text);

    /// <summary>请求房间广播一次「各玩家视角状态」（房间会回调 BuildPayload / DecoratePlayers）。</summary>
    Task BroadcastStateAsync();
}

/// <summary>只读配置门面：按节点名取强类型配置（缺失返回 null），避免游戏模块改组合根。</summary>
public interface IGameConfig
{
    T? GetSection<T>(string name) where T : class, new();
}

/// <summary>一个游戏实例 = 一个房间绑定的一份玩法状态。</summary>
public interface IGameInstance
{
    /// <summary>本游戏元数据（房间据此填充 ToInfo 的显示名 / 图标 / 人数上限）。</summary>
    GameInfo Info { get; }

    string GameId { get; }
    GameStatus Status { get; }

    /// <summary>
    /// 房主开局：校验人数下限、初始化本局状态，并把 Status 置为 Running。
    /// 框架已完成：房主身份校验、满员门禁、在线玩家清理；实例只需判「是否符合本游戏开局条件」。
    /// 返回 (成功, 错误文案)。
    /// </summary>
    (bool ok, string msg) Start();

    /// <summary>
    /// 开局成功后由房间调用一次；游戏在此驱动自身主循环。
    /// 允许无阶段、纯事件驱动（海龟汤类）；必须容忍被调用时 Status 已变为 GameOver。
    /// </summary>
    Task RunAsync();

    /// <summary>
    /// 处理本游戏的一次操作。op 由框架按「当前房间的游戏实例」路由到这里
    /// （框架只做透传，不理解 op 语义）。框架已做：房间存在性、连接→玩家解析、未开局兜底。
    /// </summary>
    (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId);

    /// <summary>
    /// 为本房间某玩家生成「该玩家视角」的游戏私有负载（序列化进 GameStateDto.GamePayload）。
    /// 视角不在房间时返回 null。
    /// </summary>
    JsonNode? BuildPayload(string viewerConnectionId);

    /// <summary>用游戏自定义标签修饰玩家行（狼人杀填身份名 / 颜色）；rows 与 IGameContext.Players 同序。</summary>
    void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername);

    /// <summary>重开清场（保留名单 / 房主）；由房间在 GameOver 后再次 Start 前调用。</summary>
    void Reset();

    /// <summary>玩家变更通知（可选，空实现即可）。</summary>
    void OnPlayerJoined(RoomPlayer player) { }

    void OnPlayerLeft(RoomPlayer player, bool stillInGame) { }

    void OnPlayerReconnected(RoomPlayer player) { }
}

/// <summary>游戏模块 = 元数据 + 工厂（服务端注册单元）。反射发现要求「公共无参构造」。</summary>
public interface IGameModule
{
    GameInfo Info { get; }

    IGameInstance CreateInstance(IGameContext context);
}
