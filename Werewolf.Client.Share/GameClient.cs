using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Client.Share;

/// <summary>登录（连接 + 账号校验）的结果。</summary>
/// <param name="Ok">是否成功登录。</param>
/// <param name="Message">给用户看的结果说明。</param>
/// <param name="Reconnected">是否恢复了已有房间身份（断线重连）。</param>
public readonly record struct LoginResult(bool Ok, string Message, bool Reconnected)
{
    public static LoginResult Fail(string message) => new(false, message, false);

    public static LoginResult Success(bool reconnected)
        => new(true, reconnected ? "已恢复房间身份。" : "登录成功。", reconnected);
}

/// <summary>创建 / 加入房间的返回。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">服务端返回的提示（成功为 "created"/"joined"/"reconnected"）。</param>
/// <param name="RoomId">所在房间号；失败时为空。</param>
/// <param name="Reconnected">是否为恢复已有房间身份。</param>
public readonly record struct RoomResult(bool Ok, string Message, string RoomId, bool Reconnected);

/// <summary>
/// 对 SignalR <see cref="HubConnection"/> 的封装，承载**整个网络收发逻辑**：
/// <list type="number">
///   <item>连接后先做一次 PSK 认证的 Diffie-Hellman 握手（快速幂协商 + HMAC 双向身份校验），
///         双方“约定”出同一把会话密钥，并能识别中间人；</item>
///   <item>之后所有请求/响应/服务器推送都在应用层用该密钥 AES-GCM 加解密（不再依赖 TLS）。</item>
/// </list>
/// 传输仍走 SignalR（负责重连与心跳），但信道内容是加密信封，明文不可读。
/// 多房间改造后：登录（账号校验）与进房分离，客户端可浏览 / 创建 / 加入多个房间；重连会自动恢复原房间。
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private const string HandshakeMethod = "Handshake";
    private const string ConfirmMethod = "Confirm";
    private const string CallMethod = "Call";
    private const string PushMethod = "Push";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HubConnection? _conn;

    /// <summary>握手后建立的会话加密器；未握手时为 null。</summary>
    private SessionCipher? _cipher;

    // ---------------- 身份 / 房间记忆（均为内存态，不落盘） ----------------

    /// <summary>用于断线自动重连后恢复身份。</summary>
    private string _username = "";
    private string _password = "";

    /// <summary>账号是否已校验通过。</summary>
    private bool _loggedIn;

    /// <summary>当前是否在某个房间内。</summary>
    private bool _joined;

    /// <summary>当前房间号（用于重连时恢复房间）；离开房间时清空。</summary>
    private string _roomId = "";

    /// <summary>当前房间密码（内存态，仅用于重连恢复）；离开房间时清空。</summary>
    private string? _roomPassword;

    private string _roomName = "";
    private string _hostName = "";
    private bool _reconnecting;

    /// <summary>当前服务器地址（连接成功后填充）。</summary>
    public string Endpoint { get; private set; } = "";

    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    /// <summary>是否正处于自动重连过程中。</summary>
    public bool IsReconnecting => _reconnecting;

    /// <summary>账号已校验通过。</summary>
    public bool IsLoggedIn => _loggedIn;

    /// <summary>是否已在某房间内（语义=「已在房间」，沿用旧名，GUI 直接复用）。</summary>
    public bool IsJoined => _joined;

    /// <summary>会话密钥是否已协商完成（加密通道是否就绪）。</summary>
    public bool IsEncrypted => _cipher != null;

    /// <summary>当前会话密钥指纹（仅用于展示/自检）。</summary>
    public string SessionKeyFingerprint => _cipher?.Fingerprint ?? "";

    /// <summary>最近一次心跳测得的往返时延（毫秒）；−1 表示尚未测量。</summary>
    public int LastLatencyMs { get; private set; } = -1;

    public string Username => _username;

    /// <summary>当前所在房间号；不在房间时为空字符串。</summary>
    public string CurrentRoomId => _roomId;

    /// <summary>当前房间名；不在房间时为空字符串。</summary>
    public string CurrentRoomName => _roomName;

    /// <summary>当前房主用户名；未知时为空字符串。</summary>
    public string CurrentHostName => _hostName;

    /// <summary>
    /// 最近一次收到（或拉取到）的房间状态。
    /// 供界面在「进房早于界面创建」时立即回填，避免出现空白的主界面。
    /// </summary>
    public GameStateDto? LastState { get; private set; }

    // ---------------- 事件（均在后台线程触发，订阅方需自行调度到 UI 线程） ----------------

    public event Action<string>? MessageReceived;
    public event Action<GameStateDto>? StateReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception?>? ConnectionClosed;
    public event Action<string>? Reconnected;

    /// <summary>收到房间列表（便于推送式刷新；本轮以拉取为主）。</summary>
    public event Action<RoomListResult>? RoomListReceived;

    // ---------------- 连接 ----------------

    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        await DisposeConnectionAsync();

        Endpoint = url;
        _loggedIn = false;
        _joined = false;
        _cipher = null;
        _reconnecting = false;
        IsManuallyClosed = false;
        LastState = null;

        // 明文 HTTP 传输：机密性由应用层的“PSK 认证 DH 握手 + AES-GCM”保证，而非 TLS。
        _conn = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new BackoffRetryPolicy())
            .Build();

        // 心跳：客户端每 15 秒发一次 KeepAlive，服务端 30 秒收不到即判定掉线；
        // 客户端若 45 秒收不到服务端消息则认为连接已失效（半开连接也能被及时发现）。
        _conn.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _conn.ServerTimeout = TimeSpan.FromSeconds(45);

        // 服务器只推一种加密信封，按 Type 分派。
        _conn.On<string>(PushMethod, OnPush);

        _conn.Reconnecting += ex =>
        {
            _reconnecting = true;
            _loggedIn = false;
            _joined = false;
            _cipher = null;
            LastLatencyMs = -1;
            LastState = null;
            StatusChanged?.Invoke("连接中断，正在重连…");
            return Task.CompletedTask;
        };

        _conn.Reconnected += async _ =>
        {
            _reconnecting = false;
            StatusChanged?.Invoke("已重新连接，正在重新协商密钥…");
            try
            {
                // 自动重连会得到新的 ConnectionId，服务端会话也已失效：
                // 必须重新握手（含身份认证），再用同样的账号登录，最后恢复房间身份。
                await HandshakeAsync();

                if (string.IsNullOrEmpty(_username)) return;

                var login = await LoginAsync(_username, _password);   // ① 账号校验
                if (!login.ok) { StatusChanged?.Invoke("恢复身份失败：" + login.msg); return; }

                if (string.IsNullOrEmpty(_roomId))                    // ② 当时不在房间 → 仅恢复登录
                {
                    Reconnected?.Invoke("已重新登录。");
                    return;
                }

                var rr = await JoinRoomAsync(_roomId, _roomPassword); // ③ 恢复房间（内存中的房间号+密码）
                if (rr.Ok)
                {
                    _joined = true;
                    Reconnected?.Invoke(rr.Reconnected ? "身份已恢复，继续游戏。" : "已重新加入房间。");
                }
                else
                {
                    StatusChanged?.Invoke("恢复房间失败：" + rr.Message);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("恢复身份失败：" + ex.Message);
            }
        };

        _conn.Closed += ex =>
        {
            _reconnecting = false;
            _loggedIn = false;
            _joined = false;
            _cipher = null;
            LastLatencyMs = -1;
            LastState = null;
            ConnectionClosed?.Invoke(ex);
            return Task.CompletedTask;
        };

        await _conn.StartAsync(ct);
        await HandshakeAsync(ct);
        StatusChanged?.Invoke("已连接服务器（身份已校验 · 会话加密）");
    }

    public async Task DisconnectAsync()
    {
        await DisposeConnectionAsync();
        _cipher = null;
        _loggedIn = false;
        _joined = false;
        LastState = null;
        IsManuallyClosed = true;
        StatusChanged?.Invoke("已断开连接");
    }

    /// <summary>是否为“用户主动断开/退出房间”，用于区分掉线。</summary>
    public bool IsManuallyClosed { get; private set; }

    // ---------------- 登录（连接 + 账号校验） ----------------

    /// <summary>
    /// 一次完成“连接服务器 → 认证握手 → 登录/注册”，并做与服务器一致的参数校验。
    /// 不再隐式进房；登录成功后由界面进入房间列表。校验规则放在这里，界面层只负责显示 <see cref="LoginResult.Message"/>。
    /// </summary>
    public async Task<LoginResult> ConnectAndLoginAsync(string url, string username, string password)
    {
        url = (url ?? "").Trim();
        username = (username ?? "").Trim();
        password ??= "";

        if (url.Length == 0) return LoginResult.Fail("请填写服务器地址。");
        if (username.Length == 0) return LoginResult.Fail("请填写用户名。");
        if (password.Length < 3) return LoginResult.Fail("密码至少 3 位。");

        await ConnectAsync(url);

        var res = await LoginAsync(username, password);
        if (!res.ok)
        {
            // 登录失败不留半连接，直接断开，方便用户改完账号重新登录。
            await DisconnectAsync();
            return LoginResult.Fail("登录失败：" + res.msg);
        }

        return LoginResult.Success(false);
    }

    // ---------------- 服务器方法：账号 ----------------

    /// <summary>账号校验（已注册即校验密码；首次即注册）。</summary>
    public async Task<JoinResult> LoginAsync(string username, string password)
    {
        var reply = await CallAsync(Ops.Join, Node(new JoinPayload
        {
            Username = username,
            Password = password
        }));

        var res = new JoinResult { ok = reply.Ok, msg = reply.Msg };
        if (res.ok)
        {
            _username = username;
            _password = password;
            _loggedIn = true;
        }
        return res;
    }

    // ---------------- 服务器方法：房间 ----------------

    /// <summary>查询房间列表（只读）。</summary>
    public async Task<RoomListResult> ListRoomsAsync()
    {
        var reply = await CallAsync(Ops.RoomList);
        if (!reply.Ok) return new RoomListResult { Ok = false, Msg = reply.Msg, Rooms = new() };

        var result = reply.Payload?.Deserialize<RoomListResult>(JsonOptions)
                     ?? new RoomListResult { Ok = false, Msg = "无法解析房间列表。" };
        if (result.Ok) RoomListReceived?.Invoke(result);
        return result;
    }

    /// <summary>查询服务器当前可用游戏（gamelist）。</summary>
    public async Task<GameListResult> ListGamesAsync()
    {
        var reply = await CallAsync(Ops.GameList);
        if (!reply.Ok) return new GameListResult { Ok = false, Msg = reply.Msg, Games = new() };

        return reply.Payload?.Deserialize<GameListResult>(JsonOptions)
               ?? new GameListResult { Ok = false, Msg = ServerMessages.GameListFailed };
    }

    /// <summary>创建房间并成为房主；密码为空/null 即公开房间；gameId 为 null/空白 → 服务端回退狼人杀。</summary>
    public async Task<RoomResult> CreateRoomAsync(string name, string? password, string? gameId = null)
    {
        var reply = await CallAsync(Ops.CreateRoom, Node(new CreateRoomPayload
        {
            Name = name ?? "",
            Password = password,
            GameId = gameId
        }));

        var result = ApplyRoomReply(reply);
        if (result.Ok) _roomPassword = password;
        return result;
    }

    /// <summary>按房间号加入；加密房间需正确密码。</summary>
    public async Task<RoomResult> JoinRoomAsync(string roomId, string? password)
    {
        var reply = await CallAsync(Ops.JoinRoom, Node(new JoinRoomPayload
        {
            RoomId = roomId ?? "",
            Password = password
        }));

        var result = ApplyRoomReply(reply);
        if (result.Ok) _roomPassword = password;
        return result;
    }

    /// <summary>离开当前房间（不拆连接）；成功后清空内存中的房间号与密码。</summary>
    /// <param name="keepRoom">
    /// 仅当自己是房主且房间内已无其他人时生效：<c>true</c> 保留房间（一直存在到服务器退出），
    /// <c>false</c> 由服务端立即关闭房间。房间内还有其他人时该参数无意义（房主会转让给下一位）。
    /// </param>
    public async Task<JoinResult> LeaveRoomAsync(bool keepRoom = false)
    {
        var reply = await CallAsync(Ops.LeaveRoom, Node(new LeaveRoomPayload { KeepRoom = keepRoom }));
        var res = new JoinResult { ok = reply.Ok, msg = reply.Msg };
        if (res.ok)
        {
            _joined = false;
            _roomId = "";
            _roomPassword = null;
            _roomName = "";
            _hostName = "";
            LastState = null;
        }
        return res;
    }

    // ---------------- 邀请码 ----------------

    /// <summary>生成当前房间的邀请码（含服务器地址与房间密码）；不在房间时返回 null。</summary>
    public string? CreateInviteCode()
        => string.IsNullOrEmpty(_roomId)
            ? null
            : InviteCode.Create(Endpoint, _roomId, _roomPassword, _roomName);

    /// <summary>解析邀请码（静态便捷入口，供界面在未连接时预览）。</summary>
    public static bool TryParseInvite(string? code, out InvitePayload? invite, out string error)
        => InviteCode.TryParse(code, out invite, out error);

    /// <summary>
    /// 用邀请码快速进房。码里已包含服务器地址、房间号与房间密码：
    /// 若当前不在该服务器上，会先切换到码中的地址并用内存中的账号重新登录，再进房。
    /// 注意：邀请码不含账号，调用方必须先完成登录（或本客户端此前已登录过）。
    /// </summary>
    public async Task<RoomResult> JoinByInviteAsync(InvitePayload invite)
    {
        if (invite == null || string.IsNullOrWhiteSpace(invite.RoomId))
            return new RoomResult(false, "邀请码内容不完整。", "", false);

        var target = (invite.Url ?? "").Trim();
        if (target.Length == 0)
            return new RoomResult(false, "邀请码缺少服务器地址。", "", false);

        // 没连或连的不是同一台服务器 → 先切到码里的地址并重新登录（账号在内存里）。
        if (!IsConnected || !SameEndpoint(Endpoint, target))
        {
            await ConnectAsync(target);

            if (!string.IsNullOrEmpty(_username))
            {
                var login = await LoginAsync(_username, _password);
                if (!login.ok) return new RoomResult(false, "在该服务器登录失败：" + login.msg, "", false);
            }
        }

        return await JoinRoomAsync(invite.RoomId, invite.Password);
    }

    /// <summary>比较两个服务器地址是否指向同一端点（忽略大小写与结尾斜杠）。</summary>
    private static bool SameEndpoint(string a, string b)
        => string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>把创建 / 加入房间的响应落到本地状态。</summary>
    private RoomResult ApplyRoomReply(RpcReply reply)
    {
        if (!reply.Ok) return new RoomResult(false, reply.Msg, "", false);

        var payload = reply.Payload?.Deserialize<RoomActionResult>(JsonOptions);
        var roomId = payload?.RoomId ?? "";
        var reconnected = payload?.Reconnected ?? false;

        if (roomId.Length > 0)
        {
            _roomId = roomId;
            _roomName = payload?.Room?.Name ?? "";
            _hostName = payload?.Room?.Host ?? "";
            _joined = true;
        }

        return new RoomResult(true, reply.Msg, roomId, reconnected);
    }

    // ---------------- 服务器方法：游戏 ----------------

    public async Task<JoinResult> StartGameAsync()
    {
        var reply = await CallAsync(Ops.Start);
        return new JoinResult { ok = reply.Ok, msg = reply.Msg };
    }

    /// <summary>
    /// 调用「本房间当前游戏」的一次操作（框架只透传，不理解 op 语义）。
    /// 具体 op 名与 payload 由各游戏模块声明，例如狼人杀的
    /// <c>WerewolfOps.Night</c> / <c>WerewolfOps.Vote</c> / <c>WerewolfOps.Hunter</c>。
    /// </summary>
    public Task<JoinResult> CallGameOpAsync(string op, JsonNode? payload = null)
    {
        var replyTask = CallAsync(op, payload);
        return AwaitReplyAsync(replyTask);
    }

    private static async Task<JoinResult> AwaitReplyAsync(Task<RpcReply> replyTask)
    {
        var reply = await replyTask;
        return new JoinResult { ok = reply.Ok, msg = reply.Msg };
    }

    public Task ChatAsync(string text)
        => CallAsync(Ops.Chat, Node(new ChatPayload { Text = text }));

    /// <summary>心跳：向服务端发一次空操作并测量往返时延。失败即说明连接已不可用。</summary>
    public async Task<int> PingAsync()
    {
        var sw = Stopwatch.StartNew();
        var reply = await CallAsync(Ops.Ping);
        sw.Stop();

        if (!reply.Ok) throw new InvalidOperationException(reply.Msg);

        LastLatencyMs = (int)sw.ElapsedMilliseconds;
        return LastLatencyMs;
    }

    // ---------------- 内部：加密通道 ----------------

    /// <summary>
    /// PSK 认证握手：交换 DH 公钥与 nonce → 派生密钥 → 校验服务端身份 → 回送本端身份证明。
    /// 任一步失败都抛异常，绝不留下“未认证”的会话。
    /// </summary>
    private async Task HandshakeAsync(CancellationToken ct = default)
    {
        EnsureConnection();

        var dh = new DhKeyExchange();
        var clientNonce = RandomNumberGenerator.GetBytes(32);

        var hello = new HandshakeHello
        {
            ClientPublic = dh.PublicValue.ToString(),
            ClientNonce = Convert.ToBase64String(clientNonce)
        };

        var replyJson = await _conn!.InvokeAsync<string>(
            HandshakeMethod, JsonSerializer.Serialize(hello, JsonOptions), ct);

        var reply = JsonSerializer.Deserialize<HandshakeReply>(replyJson, JsonOptions)
                    ?? throw new InvalidOperationException("服务器握手响应无法解析。");

        var serverPublic = BigInteger.Parse(reply.ServerPublic);
        var serverNonce = Convert.FromBase64String(reply.ServerNonce);

        var keys = HandshakeCrypto.DeriveKeys(
            dh.DeriveSharedSecret(serverPublic), clientNonce, serverNonce, PreSharedKey.Resolve());

        // ① 校验服务端身份：只有掌握 PSK 的一方才能算出正确的 HMAC。
        var serverTranscript = HandshakeCrypto.Transcript(
            "server", dh.PublicValue, serverPublic, clientNonce, serverNonce);

        if (!HandshakeCrypto.VerifyProof(keys.VerifyKey, serverTranscript, reply.ServerProof))
            throw new InvalidOperationException("服务器身份校验失败：可能存在中间人攻击，或两端预共享密钥不一致。");

        // ② 回送本端身份证明，由服务端校验后才会接受后续调用。
        var clientTranscript = HandshakeCrypto.Transcript(
            "client", dh.PublicValue, serverPublic, clientNonce, serverNonce);
        var clientProof = HandshakeCrypto.Proof(keys.VerifyKey, clientTranscript);

        var accepted = await _conn!.InvokeAsync<bool>(ConfirmMethod, clientProof, ct);
        if (!accepted)
            throw new InvalidOperationException("服务器拒绝了客户端身份校验。");

        _cipher = SessionCipher.FromKey(keys.SessionKey);
    }

    /// <summary>把一次请求放进加密信封发给服务端，并解密响应信封。</summary>
    private async Task<RpcReply> CallAsync(string op, JsonNode? payload = null)
    {
        EnsureConnection();
        var cipher = _cipher ?? throw new InvalidOperationException("会话密钥尚未协商完成。");

        var request = new RpcRequest { Op = op, Payload = payload };
        var envelope = cipher.Encrypt(JsonSerializer.Serialize(request, JsonOptions));

        var replyEnvelope = await _conn!.InvokeAsync<string>(CallMethod, envelope);
        var reply = JsonSerializer.Deserialize<RpcReply>(cipher.Decrypt(replyEnvelope), JsonOptions);
        return reply ?? RpcReply.Error("无法解析服务器响应。");
    }

    /// <summary>收到加密推送：解密后按类型分派到事件。</summary>
    private void OnPush(string envelope)
    {
        try
        {
            var cipher = _cipher;
            if (cipher == null) return;

            var push = JsonSerializer.Deserialize<PushMessage>(cipher.Decrypt(envelope), JsonOptions);
            if (push == null) return;

            switch (push.Type)
            {
                case PushTypes.State:
                    var state = push.Payload?.Deserialize<GameStateDto>(JsonOptions);
                    if (state != null) ApplyState(state);
                    break;

                case PushTypes.Message:
                    var text = push.Payload?.Deserialize<MessagePayload>(JsonOptions)?.Text ?? "";
                    MessageReceived?.Invoke(text);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageReceived?.Invoke("⚠️ 无法解密服务器消息：" + ex.Message);
        }
    }

    /// <summary>
    /// 主动拉取一次「我视角的房间状态」并触发 <see cref="StateReceived"/>。
    /// 进房（createroom / joinroom）时服务器会广播一次状态，但那一刻新界面往往还没创建、
    /// 因此没人接收；界面打开后调用它补齐初始状态（玩家列表 / 房主标识 / 阶段都靠它）。
    /// 不在房间时返回 null。
    /// </summary>
    public async Task<GameStateDto?> RefreshStateAsync()
    {
        var reply = await CallAsync(Ops.State);
        if (!reply.Ok) return null;

        var state = reply.Payload?.Deserialize<GameStateDto>(JsonOptions);
        if (state != null) ApplyState(state);
        return state;
    }

    /// <summary>把一份状态落到本地记忆并通知订阅者。</summary>
    private void ApplyState(GameStateDto state)
    {
        LastState = state;
        SyncRoomFromState(state);
        StateReceived?.Invoke(state);
    }

    /// <summary>用状态推送里的房间信息同步本地记忆（房间号为空时不覆盖，兼容旧服务端）。</summary>
    private void SyncRoomFromState(GameStateDto state)
    {
        if (!string.IsNullOrEmpty(state.RoomId)) _roomId = state.RoomId;
        _roomName = state.RoomName ?? "";
        _hostName = state.HostName ?? "";
    }

    /// <summary>把负载对象序列化成 JSON 节点（静态类型保证字段被完整序列化）。</summary>
    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions);

    private void EnsureConnection()
    {
        if (_conn == null)
            throw new InvalidOperationException("尚未连接服务器。");
        if (_conn.State != HubConnectionState.Connected)
            throw new InvalidOperationException("与服务器的连接已断开。");
    }

    private async Task DisposeConnectionAsync()
    {
        if (_conn == null) return;
        var conn = _conn;
        _conn = null;
        try { await conn.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
        _cipher = null;
    }

    /// <summary>
    /// 自动重连的退避策略：0s → 2s → 5s → 10s → 20s，之后固定每 30 秒重试一次（不放弃）。
    /// 相比固定的几次重试，长时间掉线时仍能自动恢复。
    /// </summary>
    private sealed class BackoffRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Steps =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20)
        };

        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => retryContext.PreviousRetryCount < Steps.Length
                ? Steps[(int)retryContext.PreviousRetryCount]
                : MaxDelay;
    }
}
