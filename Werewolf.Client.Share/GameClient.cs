using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Client.Share;

/// <summary>登录（连接 + 加入房间）的结果。</summary>
/// <param name="Ok">是否成功进入房间。</param>
/// <param name="Message">给用户看的结果说明。</param>
/// <param name="Reconnected">是否恢复了已有房间身份（断线重连）。</param>
public readonly record struct LoginResult(bool Ok, string Message, bool Reconnected)
{
    public static LoginResult Fail(string message) => new(false, message, false);

    public static LoginResult Success(bool reconnected)
        => new(true, reconnected ? "已恢复房间身份。" : "已加入房间。", reconnected);
}

/// <summary>
/// 对 SignalR <see cref="HubConnection"/> 的封装，承载**整个网络收发逻辑**：
/// <list type="number">
///   <item>连接后先做一次 PSK 认证的 Diffie-Hellman 握手（快速幂协商 + HMAC 双向身份校验），
///         双方“约定”出同一把会话密钥，并能识别中间人；</item>
///   <item>之后所有请求/响应/服务器推送都在应用层用该密钥 AES-GCM 加解密（不再依赖 TLS）。</item>
/// </list>
/// 传输仍走 SignalR（负责重连与心跳），但信道内容是加密信封，明文不可读。
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

    /// <summary>用于断线自动重连后恢复身份。</summary>
    private string _username = "";
    private string _password = "";
    private bool _joined;
    private bool _reconnecting;

    /// <summary>当前服务器地址（连接成功后填充）。</summary>
    public string Endpoint { get; private set; } = "";

    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    /// <summary>是否正处于自动重连过程中。</summary>
    public bool IsReconnecting => _reconnecting;

    /// <summary>已成功加入房间（已通过 Join 校验）。</summary>
    public bool IsJoined => _joined;

    /// <summary>会话密钥是否已协商完成（加密通道是否就绪）。</summary>
    public bool IsEncrypted => _cipher != null;

    /// <summary>当前会话密钥指纹（仅用于展示/自检）。</summary>
    public string SessionKeyFingerprint => _cipher?.Fingerprint ?? "";

    /// <summary>最近一次心跳测得的往返时延（毫秒）；−1 表示尚未测量。</summary>
    public int LastLatencyMs { get; private set; } = -1;

    public string Username => _username;

    // ---------------- 事件（均在后台线程触发，订阅方需自行调度到 UI 线程） ----------------

    public event Action<string>? MessageReceived;
    public event Action<GameStateDto>? StateReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception?>? ConnectionClosed;
    public event Action<string>? Reconnected;

    // ---------------- 连接 ----------------

    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        await DisposeConnectionAsync();

        Endpoint = url;
        _joined = false;
        _cipher = null;
        _reconnecting = false;

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
            _joined = false;
            _cipher = null;
            LastLatencyMs = -1;
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
                // 必须重新握手（含身份认证），再用同样的账号 Join 才能恢复房间身份。
                await HandshakeAsync();

                if (!string.IsNullOrEmpty(_username))
                {
                    var res = await JoinAsync(_username, _password);
                    if (res.ok)
                    {
                        _joined = true;
                        Reconnected?.Invoke(res.msg == "reconnected"
                            ? "身份已恢复，继续游戏。"
                            : "已重新加入房间。");
                        return;
                    }
                    StatusChanged?.Invoke("恢复身份失败：" + res.msg);
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
            _joined = false;
            _cipher = null;
            LastLatencyMs = -1;
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
        IsManuallyClosed = true;
        StatusChanged?.Invoke("已断开连接");
    }

    /// <summary>是否为“用户主动断开/退出房间”，用于区分掉线。</summary>
    public bool IsManuallyClosed { get; private set; }

    // ---------------- 登录（连接 + 加入房间） ----------------

    /// <summary>
    /// 一次完成“连接服务器 → 认证握手 → 登录/注册 → 加入房间”，并做与服务器一致的参数校验。
    /// 校验规则放在这里，界面层只负责显示 <see cref="LoginResult.Message"/>。
    /// </summary>
    public async Task<LoginResult> ConnectAndJoinAsync(string url, string username, string password)
    {
        url = (url ?? "").Trim();
        username = (username ?? "").Trim();
        password ??= "";

        if (url.Length == 0) return LoginResult.Fail("请填写服务器地址。");
        if (username.Length == 0) return LoginResult.Fail("请填写用户名。");
        if (password.Length < 3) return LoginResult.Fail("密码至少 3 位。");

        await ConnectAsync(url);

        var res = await JoinAsync(username, password);
        if (!res.ok)
        {
            // 登录失败不留半连接，直接断开，方便用户改完账号重新登录。
            await DisconnectAsync();
            return LoginResult.Fail("加入失败：" + res.msg);
        }

        return LoginResult.Success(res.msg == "reconnected");
    }

    // ---------------- 服务器方法 ----------------

    public async Task<JoinResult> JoinAsync(string username, string password)
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
            _joined = true;
        }
        return res;
    }

    public async Task<JoinResult> StartGameAsync()
    {
        var reply = await CallAsync(Ops.Start);
        return new JoinResult { ok = reply.Ok, msg = reply.Msg };
    }

    /// <summary>夜间行动。action: "do"（通用）/"heal"/"poison"/"none"。</summary>
    public Task NightActionAsync(int targetId, string action = "do")
        => CallAsync(Ops.Night, Node(new NightPayload { TargetId = targetId, Action = action }));

    public Task VoteAsync(int targetId)
        => CallAsync(Ops.Vote, Node(new TargetPayload { TargetId = targetId }));

    public Task HunterShotAsync(int targetId)
        => CallAsync(Ops.Hunter, Node(new TargetPayload { TargetId = targetId }));

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
                    if (state != null) StateReceived?.Invoke(state);
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
