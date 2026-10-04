using System.Text.Json.Nodes;

namespace Werewolf.Client.Share;

/// <summary>
/// 加密通道上的“操作名”（客户端 → 服务器）。
/// 不再为每个动作开一个 SignalR 方法，而是统一走单一加密入口 <c>Call</c>。
/// </summary>
public static class Ops
{
    public const string Join = "join";
    public const string Start = "start";
    public const string Night = "night";
    public const string Vote = "vote";
    public const string Hunter = "hunter";
    public const string Chat = "chat";

    /// <summary>心跳：用于测量往返时延并探测“半开连接”。</summary>
    public const string Ping = "ping";

    // ---------------- 房间操作（多房间改造新增） ----------------

    /// <summary>查询服务器上的房间列表（只读）。</summary>
    public const string RoomList = "roomlist";

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
}

/// <summary>加入房间。RoomId 必填；Password 仅对加密房间有效。</summary>
public sealed class JoinRoomPayload
{
    public string RoomId { get; set; } = "";
    public string? Password { get; set; }
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

    /// <summary>名单人数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>上限（来自 GameRules.MaxPlayers）。</summary>
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
