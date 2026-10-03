using System.Text.Json;
using ClientGUI.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClientGUI.Services;

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
/// 对 SignalR <see cref="HubConnection"/> 的薄封装：
/// 负责连接、调用服务器方法，并把服务器推送转成事件。
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HubConnection? _conn;

    /// <summary>用于断线自动重连后恢复身份。</summary>
    private string _username = "";
    private string _password = "";
    private bool _joined;

    /// <summary>当前服务器地址（连接成功后填充）。</summary>
    public string Endpoint { get; private set; } = "";

    public bool IsConnected => _conn?.State == HubConnectionState.Connected;

    /// <summary>已成功加入房间（已通过 Join 校验）。</summary>
    public bool IsJoined => _joined;

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

        _conn = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .Build();

        _conn.On<string>("Message", m => MessageReceived?.Invoke(m));

        // 服务器用 object 序列化 DTO，这里按 JSON 元素手动反序列化，避免类型不匹配。
        _conn.On<JsonElement>("State", el =>
        {
            try
            {
                var dto = el.Deserialize<GameStateDto>(JsonOptions);
                if (dto != null) StateReceived?.Invoke(dto);
            }
            catch (Exception ex)
            {
                MessageReceived?.Invoke("⚠️ 无法解析服务器状态：" + ex.Message);
            }
        });

        _conn.Reconnecting += ex =>
        {
            _joined = false;
            StatusChanged?.Invoke("连接中断，正在重连…");
            return Task.CompletedTask;
        };

        _conn.Reconnected += async _ =>
        {
            StatusChanged?.Invoke("已重新连接，正在恢复身份…");
            // 自动重连会得到新的 ConnectionId，必须用同样的账号重新 Join 才能恢复房间身份。
            if (!string.IsNullOrEmpty(_username))
            {
                try
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
                catch (Exception ex)
                {
                    StatusChanged?.Invoke("恢复身份失败：" + ex.Message);
                }
            }
        };

        _conn.Closed += ex =>
        {
            _joined = false;
            ConnectionClosed?.Invoke(ex);
            return Task.CompletedTask;
        };

        await _conn.StartAsync(ct);
        StatusChanged?.Invoke("已连接服务器");
    }

    public async Task DisconnectAsync()
    {
        await DisposeConnectionAsync();
        StatusChanged?.Invoke("已断开连接");
    }

    // ---------------- 登录（连接 + 加入房间） ----------------

    /// <summary>
    /// 一次完成“连接服务器 → 登录/注册 → 加入房间”，并做与服务器一致的参数校验。
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
        EnsureConnection();
        var res = await _conn!.InvokeAsync<JoinResult>("Join", username, password);
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
        EnsureConnection();
        return await _conn!.InvokeAsync<JoinResult>("Start");
    }

    /// <summary>夜间行动。action: "do"（通用）/"heal"/"poison"/"none"。</summary>
    public Task NightActionAsync(int targetId, string action = "do")
    {
        EnsureConnection();
        return _conn!.SendAsync("NightAction", targetId, action);
    }

    public Task VoteAsync(int targetId)
    {
        EnsureConnection();
        return _conn!.SendAsync("Vote", targetId);
    }

    public Task HunterShotAsync(int targetId)
    {
        EnsureConnection();
        return _conn!.SendAsync("HunterShot", targetId);
    }

    public Task ChatAsync(string text)
    {
        EnsureConnection();
        return _conn!.SendAsync("Chat", text);
    }

    // ---------------- 内部 ----------------

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
    }
}
