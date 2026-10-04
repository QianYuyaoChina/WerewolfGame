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
}

/// <summary>服务器推送类型（服务器 → 客户端）。</summary>
public static class PushTypes
{
    public const string State = "state";
    public const string Message = "message";
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

/// <summary>加入房间（登录/注册）。</summary>
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
