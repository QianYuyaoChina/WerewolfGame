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
/// 具体业务逻辑仍由 <see cref="GameRoom"/> 承担，Hub 只做协议与分发。
/// </summary>
public class GameHub : Hub
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly GameRoomService _service;
    private readonly AccountStore _accounts;
    private readonly CryptoSessionStore _sessions;
    private readonly EncryptedChannel _channel;

    public GameHub(GameRoomService service, AccountStore accounts,
                   CryptoSessionStore sessions, EncryptedChannel channel)
    {
        _service = service;
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
                Ops.Join => await HandleJoinAsync(request.Payload),
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

    // ==================== 各操作的处理 ====================

    private async Task<RpcReply> HandleJoinAsync(JsonNode? payload)
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

        // ---- 加入 / 重连房间 ----
        var room = _service.Room;
        var (ok, msg, player, reconnected) = room.AddOrReconnect(username, Context.ConnectionId);
        if (!ok || player == null) return RpcReply.Error(msg);

        if (reconnected)
        {
            await SendTextToOthersAsync($"🔄 {username} 重新连接。");
            if (room.IsRunning)
                await room.ResendRoleAsync(player);   // 补发身份私聊
        }
        else
        {
            await SendTextToOthersAsync($"👋 {username} 加入了房间。");
        }

        await room.BroadcastStateAsync();
        return RpcReply.Success(reconnected ? "reconnected" : "joined");
    }

    private async Task<RpcReply> HandleStartAsync()
    {
        var (ok, msg) = _service.Room.StartGame(Context.ConnectionId);
        if (!ok) await _channel.SendMessageAsync(Context.ConnectionId, "⚠️ " + msg);
        return ok ? RpcReply.Success() : RpcReply.Error(msg);
    }

    private RpcReply HandleNight(JsonNode? payload)
    {
        var act = payload?.Deserialize<NightPayload>(Json) ?? new NightPayload();
        _service.Room.SubmitNightAction(Context.ConnectionId, act.TargetId, act.Action ?? "do");
        return RpcReply.Success();
    }

    private RpcReply HandleVote(JsonNode? payload)
    {
        var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
        _service.Room.SubmitVote(Context.ConnectionId, act.TargetId);
        return RpcReply.Success();
    }

    private RpcReply HandleHunter(JsonNode? payload)
    {
        var act = payload?.Deserialize<TargetPayload>(Json) ?? new TargetPayload();
        _service.Room.SubmitHunterShot(Context.ConnectionId, act.TargetId);
        return RpcReply.Success();
    }

    private async Task<RpcReply> HandleChatAsync(JsonNode? payload)
    {
        var room = _service.Room;
        var player = room.GetPlayer(Context.ConnectionId);
        if (player == null || !player.IsAlive) return RpcReply.Error("你已出局，无法发言。");

        var text = payload?.Deserialize<ChatPayload>(Json)?.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return RpcReply.Error("消息不能为空。");

        await room.BroadcastAsync($"💬 {player.Username}：{text.Trim()}");
        return RpcReply.Success();
    }

    // ==================== 连接生命周期 ====================

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var room = _service.Room;
        var player = room.GetPlayer(Context.ConnectionId);
        room.OnDisconnected(Context.ConnectionId);
        _sessions.Remove(Context.ConnectionId);

        if (player != null)
        {
            bool removed = !room.IsRunning;
            await room.BroadcastAsync(removed
                ? $"❌ {player.Username} 离开了房间。"
                : $"⚠️ {player.Username} 掉线了（可重新连接）。");
        }
        await room.BroadcastStateAsync();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>给房间内除自己以外的所有连接发文本（相当于原来的 <c>Clients.Others</c>）。</summary>
    private async Task SendTextToOthersAsync(string text)
    {
        foreach (var player in _service.Room.ConnectedPlayers())
        {
            if (player.ConnectionId == Context.ConnectionId) continue;
            await _channel.SendMessageAsync(player.ConnectionId, text);
        }
    }
}
