using System.Text.Json.Nodes;

namespace Werewolf.Client.Share;

/// <summary>
/// 加密通道上的“操作名”（客户端 → 服务器）。
/// 不再为每个动作开一个 SignalR 方法，而是统一走单一加密入口 <c>Call</c>。
/// </summary>
public static class Ops
{
    /// <summary>账号校验（首次即注册）。</summary>
    public const string Join = "join";

    /// <summary>
    /// 通用游戏生命周期：房主开局（对所有游戏通用）。框架理解它，但不理解任何具体游戏的玩法。
    /// </summary>
    public const string Start = "start";

    public const string Chat = "chat";

    /// <summary>心跳：用于测量往返时延并探测“半开连接”。</summary>
    public const string Ping = "ping";

    // ---------------- 房间操作（多房间改造新增） ----------------

    /// <summary>查询服务器上的房间列表（只读）。</summary>
    public const string RoomList = "roomlist";

    // ---------------- 房主设置（v2.1） ----------------

    /// <summary>房主拉取本房间的设置（最大人数 / 是否加密 / 邀请令牌 / 身份布局）。仅房主可用。</summary>
    public const string HostSettings = "hostsettings";

    /// <summary>房主修改房间设置（最大人数 / 密码 / 身份布局）。仅房主可用。</summary>
    public const string RoomSettings = "roomsettings";

    /// <summary>房主作废并重新生成邀请令牌（旧的邀请码随即失效）。</summary>
    public const string ResetInvite = "resetinvite";

    /// <summary>创建房间并成为房主。</summary>
    public const string CreateRoom = "createroom";

    /// <summary>按房间号加入（加密房间需密码）。</summary>
    public const string JoinRoom = "joinroom";

    /// <summary>离开当前房间（不拆连接）。</summary>
    public const string LeaveRoom = "leaveroom";

    /// <summary>
    /// 主动拉取一次「我视角的房间状态」。
    /// 房间状态平时靠服务器推送，但进房（createroom/joinroom）发生在新界面创建之前时，
    /// 那次广播没人接收，新界面就会一直显示为空——用这个操作补一次初始状态。
    /// </summary>
    public const string State = "roomstate";

    /// <summary>查询「服务器当前可用游戏」（只读）。</summary>
    public const string GameList = "gamelist";

    // ⚠️ 注意：night / vote / hunter 不再是框架 op；它们由狼人杀模块自己的
    //    WerewolfOps 声明，走 GameHub 的「默认分支」路由到当前房间的游戏实例。
}

/// <summary>服务器推送类型（服务器 → 客户端）。</summary>
public static class PushTypes
{
    public const string State = "state";
    public const string Message = "message";
}

/// <summary>统一文案（三端复用，避免前后台拼写漂移）。</summary>
public static class ServerMessages
{
    public const string Ok = "ok";
    public const string NotLoggedIn = "尚未登录，请先登录";
    public const string NotInRoom = "你尚未加入任何房间";
    public const string RoomNotFound = "房间不存在或已关闭";
    public const string WrongPassword = "密码错误";
    public const string GameInProgress = "游戏进行中，无法加入";
    public const string OnlyHostCanStart = "只有房主可以开始游戏";
    public const string TooManyRooms = "房间数已达上限，请稍后再试";
    public const string AlreadyInRoom = "该账号已在房间中";

    /// <summary>createroom 的 GameId 服务器不支持。</summary>
    public const string GameUnavailable = "该游戏不可用";

    /// <summary>gamelist 不可用（理论不出现，兜底）。</summary>
    public const string GameListFailed = "无法获取游戏列表";

    // ---------------- 房主设置（v2.1） ----------------

    /// <summary>非房主尝试修改房间设置。</summary>
    public const string OnlyHostCanEdit = "只有房主可以修改房间设置";

    /// <summary>游戏进行中不允许修改该设置（人数 / 身份布局）。</summary>
    public const string SettingLockedInGame = "游戏进行中，无法修改该设置";

    /// <summary>邀请令牌无效（邀请码已失效或被重置）。</summary>
    public const string InviteExpired = "邀请码已失效，请向房主要一个新的";

    public static string RoomFull(int max) => $"房间已满（最多 {max} 人）";
}

/// <summary>房间在列表中的状态（服务端是唯一权威，客户端只做展示映射）。</summary>
public static class RoomStatus
{
    public const string Waiting = "Waiting";
    public const string Running = "Running";
    public const string GameOver = "GameOver";

    public static string Name(string status) => status switch
    {
        Waiting => "等待中",
        Running => "游戏进行中",
        GameOver => "已结束待重开",
        _ => status
    };
}

/// <summary>加密请求信封的明文结构。</summary>
public sealed class RpcRequest
{
    public string Op { get; set; } = "";
    public JsonNode? Payload { get; set; }
}

/// <summary>加密响应信封的明文结构。</summary>
public sealed class RpcReply
{
    public bool Ok { get; set; } = true;
    public string Msg { get; set; } = "";
    public JsonNode? Payload { get; set; }

    public static RpcReply Success(string msg = "") => new() { Ok = true, Msg = msg };
    public static RpcReply Error(string msg) => new() { Ok = false, Msg = msg };
}

/// <summary>加密推送信封的明文结构。</summary>
public sealed class PushMessage
{
    public string Type { get; set; } = "";
    public JsonNode? Payload { get; set; }
}

// ---------------- 握手（身份认证） ----------------

/// <summary>客户端握手上行：DH 公钥 + 客户端随机数。</summary>
public sealed class HandshakeHello
{
    public string ClientPublic { get; set; } = "";
    public string ClientNonce { get; set; } = "";
}

/// <summary>服务端握手下行：DH 公钥 + 服务端随机数 + 服务端身份校验值（HMAC）。</summary>
public sealed class HandshakeReply
{
    public string ServerPublic { get; set; } = "";
    public string ServerNonce { get; set; } = "";
    public string ServerProof { get; set; } = "";
}

// ---------------- 各操作的负载 ----------------

/// <summary>加入（登录/注册）：仅做账号校验，不再隐式进房。</summary>
public sealed class JoinPayload
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>夜间行动：<c>Action</c> 为 "do"/"heal"/"poison"/"none"。</summary>
public sealed class NightPayload
{
    public int TargetId { get; set; }
    public string Action { get; set; } = "do";
}

/// <summary>仅带一个目标编号的操作（投票 / 猎人开枪）。</summary>
public sealed class TargetPayload
{
    public int TargetId { get; set; }
}

/// <summary>聊天发言。</summary>
public sealed class ChatPayload
{
    public string Text { get; set; } = "";
}

/// <summary>服务器广播/私聊文本的负载。</summary>
public sealed class MessagePayload
{
    public string Text { get; set; } = "";
}

// ---------------- 房间请求负载 ----------------

/// <summary>创建房间。Name 可空 → 服务端回退 "&lt;用户名&gt; 的房间"；Password 空/null → 公开房间。</summary>
public sealed class CreateRoomPayload
{
    public string Name { get; set; } = "";
    public string? Password { get; set; }

    /// <summary>房间绑定的游戏机器名；null/空白 → 服务端回退 <see cref="GameIds.Werewolf"/>。</summary>
    public string? GameId { get; set; }
}

/// <summary>加入房间。RoomId 必填；Password 仅对加密房间有效。</summary>
public sealed class JoinRoomPayload
{
    public string RoomId { get; set; } = "";
    public string? Password { get; set; }

    /// <summary>
    /// 邀请令牌（来自邀请码）。令牌匹配时**跳过密码校验**，
    /// 这样房主即使改了房间密码，先前发出的邀请码依然有效。
    /// </summary>
    public string? InviteToken { get; set; }
}

// ---------------- 房主设置（v2.1） ----------------

/// <summary>房主视角的房间设置（含邀请令牌，因此**只发给房主**）。</summary>
public sealed class HostSettingsDto
{
    public string RoomId { get; set; } = "";
    public string RoomName { get; set; } = "";

    /// <summary>房间当前的最大人数（可能是房主设过的覆盖值）。</summary>
    public int MaxPlayers { get; set; }

    /// <summary>游戏允许的最小人数（下限，房主不能改到它以下）。</summary>
    public int MinPlayers { get; set; }

    /// <summary>游戏允许的最大人数（上限，房主不能改到它以上）。</summary>
    public int MaxPlayersLimit { get; set; }

    public bool HasPassword { get; set; }

    /// <summary>本房间的邀请令牌：房间生命周期内稳定，改密码 / 改人数都不会变。</summary>
    public string InviteToken { get; set; } = "";

    /// <summary>是否已开局（开局后人数与身份布局不可改）。</summary>
    public bool Running { get; set; }

    /// <summary>当前参与人数（用于校验身份布局）。</summary>
    public int PlayerCount { get; set; }

    /// <summary>当前游戏私有配置的原始 JSON（未自定义时为空）。由各游戏的「设置」界面解析。</summary>
    public string? GameSettingsJson { get; set; }

    /// <summary>游戏私有配置是否为房主自定义（false 表示跟随游戏的默认配置）。</summary>
    public bool CustomGameSettings { get; set; }
}

/// <summary>房主修改房间设置。只需要传要改的字段（null 表示不动）。</summary>
public sealed class UpdateRoomSettingsPayload
{
    /// <summary>新的最大人数；null 表示不改。</summary>
    public int? MaxPlayers { get; set; }

    /// <summary>新的房间密码；null 表示不改。</summary>
    public string? NewPassword { get; set; }

    /// <summary>true 表示改成公开房间（忽略 NewPassword）。</summary>
    public bool ClearPassword { get; set; }

    /// <summary>
    /// 本游戏的私有配置（不透明 JSON 字符串，由游戏自己解释；狼人杀放的是身份布局）。
    /// null 表示不改、空字符串表示恢复默认、其他值原样保存。
    /// </summary>
    public string? GameSettingsJson { get; set; }
}

/// <summary>丘比特第一夜指定两名玩家成为情侣。</summary>
public sealed class CupidPayload
{
    /// <summary>情侣中的第一位玩家编号。</summary>
    public int TargetA { get; set; }

    /// <summary>情侣中的第二位玩家编号。</summary>
    public int TargetB { get; set; }
}

/// <summary>
/// 离开房间。
/// <c>KeepRoom=true</c> 且房间内没有其他人时，房间被标记为「保留」（一直存在到服务器退出）；
/// 否则房间若已无人会被立即关闭。房间内还有其他成员时该字段无意义（房主会转让给下一位）。
/// </summary>
public sealed class LeaveRoomPayload
{
    public bool KeepRoom { get; set; }
}

// ---------------- 房间数据对象 ----------------

/// <summary>房间列表摘要（服务端 → 客户端）。</summary>
public sealed class RoomInfoDto
{
    /// <summary>5 位房间号（唯一标识）。</summary>
    public string Id { get; set; } = "";

    /// <summary>房间名（可重名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>房间绑定的游戏机器名（<see cref="GameIds"/>）。</summary>
    public string GameId { get; set; } = GameIds.Werewolf;

    /// <summary>游戏显示名（冗余，便于列表即时渲染，无需再查游戏目录）。</summary>
    public string GameName { get; set; } = "";

    /// <summary>游戏图标 emoji（冗余）。</summary>
    public string GameEmoji { get; set; } = "";

    /// <summary>名单人数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>上限（来自该游戏的 <see cref="GameInfo.MaxPlayers"/>）。</summary>
    public int MaxPlayers { get; set; }

    /// <summary>"Waiting" | "Running" | "GameOver"（用 <see cref="RoomStatus"/>）。</summary>
    public string Status { get; set; } = "";

    /// <summary>是否加密。</summary>
    public bool HasPassword { get; set; }

    /// <summary>房主用户名（无则 ""）。</summary>
    public string Host { get; set; } = "";
}

/// <summary>房间列表结果（同时用作 roomlist 的 Payload 与客户端返回类型）。</summary>
public sealed class RoomListResult
{
    /// <summary>客户端用；服务端 payload 中恒 true。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>客户端用；服务端 payload 中恒 ""。</summary>
    public string Msg { get; set; } = "";

    public List<RoomInfoDto> Rooms { get; set; } = new();
}

/// <summary>createroom / joinroom 的 Payload。</summary>
public sealed class RoomActionResult
{
    public string RoomId { get; set; } = "";
    public bool Reconnected { get; set; }

    /// <summary>冗余一份摘要，便于客户端即时显示。</summary>
    public RoomInfoDto? Room { get; set; }
}
