using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Werewolf.Client.Share;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Server;

/// <summary>
/// 网络入口。对外只暴露三个方法：
/// <list type="bullet">
///   <item><c>Handshake</c>：交换 DH 公钥与 nonce，派生密钥并回送服务端身份证明；</item>
///   <item><c>Confirm</c>：校验客户端身份证明，通过后会话才生效；</item>
///   <item><c>Call</c>：唯一加密入口，解密请求 → 分发 → 加密响应。</item>
/// </list>
/// 多房间改造后：<c>join</c> 收敛为「账号校验」；房间操作走 <c>roomlist/createroom/joinroom/leaveroom</c>；
/// <c>start/night/vote/hunter/chat</c> 先按连接解析房间再分发。具体业务逻辑仍由 <see cref="GameRoom"/> 承担。
/// </summary>
public class GameHub : Hub
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly RoomManager _rooms;
    private readonly AccountStore _accounts;
    private readonly CryptoSessionStore _sessions;
    private readonly EncryptedChannel _channel;

    public GameHub(RoomManager rooms, AccountStore accounts,
                   CryptoSessionStore sessions, EncryptedChannel channel)
    {
        _rooms = rooms;
        _accounts = accounts;
        _sessions = sessions;
        _channel = channel;
    }

    // ==================== 密钥协商 + 身份认证 ====================

    /// <summary>
    /// 客户端送来 DH 公钥与随机数，服务端用自己的临时私钥算出共享密钥，
    /// 再用 PSK 派生出会话密钥与校验密钥，并回送自己的公钥、随机数与身份证明。
    /// 只有掌握同一份 PSK 的客户端才能校验该证明并继续。
    /// </summary>
    public Task<string> Handshake(string helloJson)
    {
        var hello = JsonSerializer.Deserialize<HandshakeHello>(helloJson, Json)
                    ?? throw new HubException("无效的握手请求。");

        if (!BigInteger.TryParse(hello.ClientPublic, out var clientPublic))
            throw new HubException("无效的密钥协商参数。");

        byte[] clientNonce;
        try { clientNonce = Convert.FromBase64String(hello.ClientNonce); }
        catch { throw new HubException("无效的握手随机数。"); }

        var dh = new DhKeyExchange();
        var serverNonce = RandomNumberGenerator.GetBytes(32);

        var keys = HandshakeCrypto.DeriveKeys(
            dh.DeriveSharedSecret(clientPublic), clientNonce, serverNonce, PreSharedKey.Resolve());

        // 服务端身份证明（供客户端校验）与客户端应给出的记录（供 Confirm 校验）。
        var serverTranscript = HandshakeCrypto.Transcript(
            "server", clientPublic, dh.PublicValue, clientNonce, serverNonce);
        var clientTranscript = HandshakeCrypto.Transcript(
            "client", clientPublic, dh.PublicValue, clientNonce, serverNonce);

        _sessions.SetPending(Context.ConnectionId, new PendingSession
        {
            SessionKey = keys.SessionKey,
            VerifyKey = keys.VerifyKey,
            ClientTranscript = clientTranscript
        });

        var reply = new HandshakeReply
        {
            ServerPublic = dh.PublicValue.ToString(),
            ServerNonce = Convert.ToBase64String(serverNonce),
            ServerProof = HandshakeCrypto.Proof(keys.VerifyKey, serverTranscript)
        };

        return Task.FromResult(JsonSerializer.Serialize(reply, Json));
    }

    /// <summary>校验客户端身份证明；通过后该连接才拥有可用会话密钥。</summary>
    public Task<bool> Confirm(string clientProof)
    {
        var pending = _sessions.GetPending(Context.ConnectionId);
        if (pending == null) return Task.FromResult(false);

        if (!HandshakeCrypto.VerifyProof(pending.VerifyKey, pending.ClientTranscript, clientProof))
        {
            // 身份校验失败：丢弃中间状态，该连接无法进行任何加密调用。
            _sessions.Remove(Context.ConnectionId);
            return Task.FromResult(false);
        }

        _sessions.Promote(Context.ConnectionId, pending);
        return Task.FromResult(true);
    }

    // ==================== 加密 RPC ====================

    /// <summary>唯一加密入口：请求/响应都是会话密钥加密过的信封。</summary>
    public async Task<string> Call(string envelope)
    {
        var cipher = _sessions.Get(Context.ConnectionId)
            ?? throw new HubException("会话密钥尚未协商，请先握手。");

        RpcRequest request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(cipher.Decrypt(envelope), Json)
                      ?? throw new HubException("请求内容为空。");
        }
        catch (HubException) { throw; }
        catch (Exception ex) { throw new HubException("无法解密请求：" + ex.Message); }

        var reply = await DispatchAsync(request);
        return cipher.Encrypt(JsonSerializer.Serialize(reply, Json));
    }

    private async Task<RpcReply> DispatchAsync(RpcRequest request)
    {
        try
        {
            return request.Op switch
            {
                Ops.Join => HandleJoin(request.Payload),
                Ops.RoomList => HandleRoomList(),
                Ops.CreateRoom => await HandleCreateRoomAsync(request.Payload),
                Ops.JoinRoom => await HandleJoinRoomAsync(request.Payload),
                Ops.LeaveRoom => await HandleLeaveRoomAsync(),
                Ops.Start => await HandleStartAsync(),
                Ops.Night => HandleNight(request.Payload),
                Ops.Vote => HandleVote(request.Payload),
                Ops.Hunter => HandleHunter(request.Payload),
                Ops.Chat => await HandleChatAsync(request.Payload),
                Ops.Ping => RpcReply.Success(),
                _ => RpcReply.Error("未知操作：" + request.Op)
            };
        }
        catch (Exception ex)
        {
            return RpcReply.Error(ex.Message);
        }
    }

    // ==================== 账号 / 房间操作 ====================

    /// <summary>账号校验（首次即注册），成功后绑定「连接 → 用户名」。不再隐式进房。</summary>
    private RpcReply HandleJoin(JsonNode? payload)
    {
        var join = payload?.Deserialize<JoinPayload>(Json) ?? new JoinPayload();
        var username = (join.Username ?? "").Trim();
        var password = join.Password ?? "";

        if (string.IsNullOrWhiteSpace(username)) return RpcReply.Error("用户名不能为空");
        if (username.Length > 20) return RpcReply.Error("用户名过长（最多 20 字符）");
        if (password.Length < 3) return RpcReply.Error("密码至少 3 位");

        // ---- 账号：首次即注册；否则校验 ----
        if (!_accounts.Exists(username))
        {
            var err = _accounts.Register(username, password);
            if (err != null) return RpcReply.Error(err);
        }
        else if (!_accounts.Verify(username, password))
        {
            return RpcReply.Error("密码错误");
        }

        _rooms.BindIdentity(Context.ConnectionId, username);
        return RpcReply.Success(ServerMessages.Ok);
    }

    /// <summary>房间列表（只读）：返回所有房间摘要。</summary>
    private RpcReply HandleRoomList()
    {
        var result = new RoomListResult { Ok = true, Msg = "", Rooms = _rooms.List() };
        return new RpcReply
        {
            Ok = true,
            Msg = "",
            Payload = JsonSerializer.SerializeToNode(result, Json)
        };
    }

    private async Task<RpcReply> HandleCreateRoomAsync(JsonNode? payload)
    {
        if (string.IsNullOrEmpty(_rooms.GetUsername(Context.ConnectionId)))
            return RpcReply.Error(ServerMessages.NotLoggedIn);

        var req = payload?.Deserialize<CreateRoomPayload>(Json) ?? new CreateRoomPayload();
        var (ok, msg, roomId, _) = _rooms.CreateRoom(Context.ConnectionId, req.Name ?? "", req.Password);
        if (!ok) return RpcReply.Error(msg);

        var room = _rooms.Find(roomId);
        if (room != null)
        {
            var username = _rooms.GetUsername(Context.ConnectionId);
            await room.BroadcastAsync($"👋 {username} 创建了房间 {roomId}。");
            await room.BroadcastStateAsync();
        }

        var result = new RoomActionResult
        {
            RoomId = roomId,
            Reconnected = false,
            Room = room?.ToInfo()
        };
        return new RpcReply
        {
            Ok = true,
            Msg = "created",
            Payload = JsonSerializer.SerializeToNode(result, Json)
        };
    }

    private async Task<RpcReply> HandleJoinRoomAsync(JsonNode? payload)
    {
        if (string.IsNullOrEmpty(_rooms.GetUsername(Context.ConnectionId)))
            return RpcReply.Error(ServerMessages.NotLoggedIn);

        var req = payload?.Deserialize<JoinRoomPayload>(Json) ?? new JoinRoomPayload();
        var roomId = (req.RoomId ?? "").Trim();
        if (roomId.Length == 0) return RpcReply.Error(ServerMessages.RoomNotFound);

        var (ok, msg, joinedRoomId, reconnected) = _rooms.JoinRoom(roomId, Context.ConnectionId, req.Password);
        if (!ok) return RpcReply.Error(msg);

        var room = _rooms.Find(joinedRoomId);
        if (room != null)
        {
            var username = _rooms.GetUsername(Context.ConnectionId);
            if (reconnected)
            {
                await room.BroadcastAsync($"🔄 {username} 重新连接。");
                if (room.IsRunning)
                {
                    var player = room.GetPlayer(Context.ConnectionId);
                    if (player != null) await room.ResendRoleAsync(player);   // 补发身份私聊
                }
            }
            else
            {
                await room.BroadcastAsync($"👋 {username} 加入了房间。");
            }
            await room.BroadcastStateAsync();
        }

        var result = new RoomActionResult
        {
            RoomId = joinedRoomId,
            Reconnected = reconnected,
            Room = room?.ToInfo()
        };
        return new RpcReply
        {
            Ok = true,
            Msg = reconnected ? "reconnected" : "joined",
            Payload = JsonSerializer.SerializeToNode(result, Json)
        };
    }

    private async Task<RpcReply> HandleLeaveRoomAsync()
    {
        var (room, player) = _rooms.LeaveRoom(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        if (player != null)
        {
            bool removed = !room.IsRunning;
            await room.BroadcastAsync(removed
                ? $"❌ {player.Username} 离开了房间。"
                : $"⚠️ {player.Username} 离开了房间（局内继续，可重新加入）。");
            await room.BroadcastStateAsync();
        }
        return RpcReply.Success("left");
    }

    private async Task<RpcReply> HandleStartAsync()
    {
        var room = _rooms.GetRoomByConnection(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        var (ok, msg) = room.StartGame(Context.ConnectionId);
        if (!ok) await _channel.SendMessageAsync(Context.ConnectionId, "⚠️ " + msg);
        return ok ? RpcReply.Success() : RpcReply.Error(msg);
    }

    private RpcReply HandleNight(JsonNode? payload)
    {
        var room = _rooms.GetRoomByConnection(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        var act = payload?.Deserialize<NightPayload>(Json) ?? new NightPayload();
        room.SubmitNightAction(Context.ConnectionId, act.TargetId, act.Action ?? "do");
        return RpcReply.Success();
    }

    private RpcReply HandleVote(JsonNode? payload)
    {
        var room = _rooms.GetRoomByConnection(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
        room.SubmitVote(Context.ConnectionId, act.TargetId);
        return RpcReply.Success();
    }

    private RpcReply HandleHunter(JsonNode? payload)
    {
        var room = _rooms.GetRoomByConnection(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
        room.SubmitHunterShot(Context.ConnectionId, act.TargetId);
        return RpcReply.Success();
    }

    private async Task<RpcReply> HandleChatAsync(JsonNode? payload)
    {
        var room = _rooms.GetRoomByConnection(Context.ConnectionId);
        if (room == null) return RpcReply.Error(ServerMessages.NotInRoom);

        var player = room.GetPlayer(Context.ConnectionId);
        if (player == null || !player.IsAlive) return RpcReply.Error("你已出局，无法发言。");

        var text = payload?.Deserialize<ChatPayload>(Json)?.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return RpcReply.Error("消息不能为空。");

        await room.BroadcastAsync($"💬 {player.Username}：{text.Trim()}");
        return RpcReply.Success();
    }

    // ==================== 连接生命周期 ====================

    /// <summary>
    /// 断开清理顺序（见设计 §6）：
    /// ① 移除会话密钥；② 脱离房间并清掉连接映射；③ 广播离开/掉线并刷新状态。
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.Remove(Context.ConnectionId);
        var (room, player) = _rooms.LeaveByConnection(Context.ConnectionId);

        if (room != null && player != null)
        {
            bool removed = !room.IsRunning;
            await room.BroadcastAsync(removed
                ? $"❌ {player.Username} 离开了房间。"
                : $"⚠️ {player.Username} 掉线了（可重新连接）。");
            await room.BroadcastStateAsync();
        }

        await base.OnDisconnectedAsync(exception);
    }
}
