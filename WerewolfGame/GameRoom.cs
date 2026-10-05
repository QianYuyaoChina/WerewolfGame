using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Server.Games;

namespace Werewolf.Server;

/// <summary>
/// 房间宿主（平台层）。只负责：房间身份 / 玩家名单 / 密码 / 在线占用 / 回收 / 房主设置，
/// 以及对外（网络层）暴露「进房 / 离开 / 拉状态 / 开局」。**玩法**全部委托给可插拔的
/// <see cref="IGameInstance"/>；对游戏侧则以 <see cref="IGameContext"/> 暴露平台原语。
/// 本类不引用任何具体游戏类型。
/// </summary>
public class GameRoom : IGameContext
{
    private readonly List<RoomPlayer> _players = new();

    /// <summary>出站加密通道：所有推送都由它加密后发出，业务代码不碰密文。</summary>
    private readonly EncryptedChannel _channel;
    private readonly object _sync = new();

    /// <summary>房间密码的存储值（<see cref="RoomPassword.Hash"/> 的结果）；无密码为 null。绝不出房间对象。</summary>
    private string? _passwordHash;

    /// <summary>绑定本房间的玩法实例（唯一进入游戏玩法的入口）。</summary>
    private readonly IGameInstance _game;

    /// <summary>只读配置门面（游戏经 <see cref="Config"/> 自取规则）。</summary>
    private readonly IGameConfig _config;

    /// <summary>
    /// 房主为本房间设置的「游戏私有配置」（对平台是**不透明字符串**，由游戏自己解释）。
    /// 狼人杀用它承载自定义身份布局；null 表示使用游戏的默认配置。
    /// </summary>
    private string? _gameSettingsJson;

    /// <summary>房主覆盖的最大人数；0 表示沿用游戏元数据里的上限。</summary>
    private int _maxPlayersOverride;

    /// <summary>
    /// 房间邀请令牌：**房间生命周期内稳定**，房主改密码 / 改人数都不会变，
    /// 因此已发出的邀请码不会因为改密码而失效；只有房主主动重置才会换新。
    /// </summary>
    private string _inviteToken;

    /// <summary>无在线玩家起始时间（UTC）；有在线玩家为 null，用于宽限期回收判定。</summary>
    private DateTime? _emptySince;

    /// <summary>是否为「保留房间」：无人也不被回收，一直存在到服务器退出。</summary>
    private bool _persistent;

    /// <summary>
    /// 构造一个房间。密码为 null/空白时视为公开房间（仅保存带盐哈希，不保存明文）。
    /// <paramref name="gameFactory"/> 由 <c>GameCatalog</c> 提供：房间用它创建绑定的游戏实例。
    /// <paramref name="config"/> 为只读配置门面（缺省用空配置，游戏回退内置默认规则）。
    /// </summary>
    public GameRoom(string id, string name, string? password, EncryptedChannel channel,
                    string gameId, Func<IGameContext, IGameInstance> gameFactory, IGameConfig? config = null)
    {
        Id = id ?? "";
        Name = string.IsNullOrWhiteSpace(name) ? "房间" : name.Trim();
        GameId = string.IsNullOrWhiteSpace(gameId) ? GameIds.Default : gameId;
        _channel = channel;
        _config = config ?? new GameConfig(null);
        _inviteToken = NewInviteToken();
        _passwordHash = string.IsNullOrWhiteSpace(password) ? null : RoomPassword.Hash(Id, password);

        CreatedAt = DateTime.UtcNow;

        // 创建玩法实例：房间此时已实现 IGameContext，可供游戏读取房间号/玩家/配置。
        _game = gameFactory(this);
    }

    // ==================== 房间身份信息 ====================

    /// <summary>5 位房间号，唯一标识。</summary>
    public string Id { get; }

    /// <summary>房间名（可重名）。</summary>
    public string Name { get; }

    /// <summary>房间绑定的游戏机器名（<see cref="GameIds"/>）；房间生命周期内不可变。</summary>
    public string GameId { get; }

    /// <summary>是否设置了密码（房主可随时改）。</summary>
    public bool HasPassword { get { lock (_sync) return _passwordHash != null; } }

    /// <summary>创建时间（UTC），列表按此排序。</summary>
    public DateTime CreatedAt { get; }

    /// <summary>游戏元数据允许的最小人数。</summary>
    public int MinPlayers => _game.Info.MinPlayers;

    /// <summary>游戏元数据允许的最大人数（房主可在此基础上调小）。</summary>
    public int MaxPlayersLimit => _game.Info.MaxPlayers;

    /// <summary>房间**实际**的最大人数：房主设过就用房主的值，否则用游戏上限。</summary>
    public int MaxPlayers
    {
        get
        {
            lock (_sync)
            {
                var limit = _game.Info.MaxPlayers;
                return _maxPlayersOverride > 0 ? Math.Min(_maxPlayersOverride, limit) : limit;
            }
        }
    }

    /// <summary>邀请令牌（仅经「房主设置」下发给房主，不随房间列表暴露）。</summary>
    public string InviteToken { get { lock (_sync) return _inviteToken; } }

    /// <summary>房主自定义的游戏私有配置（不透明字符串）；null 表示使用默认。</summary>
    public string? GameSettingsJson { get { lock (_sync) return _gameSettingsJson; } }

    /// <summary>房主用户名（取房间内 IsHost 的玩家；无则 ""）。</summary>
    public string HostUsername
    {
        get { lock (_sync) return _players.FirstOrDefault(p => p.IsHost)?.Username ?? ""; }
    }

    /// <summary>名单人数。</summary>
    public int PlayerCount { get { lock (_sync) return _players.Count; } }

    /// <summary>在线人数（已连接且持有连接号）。</summary>
    public int ConnectedCount
    {
        get { lock (_sync) return _players.Count(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId)); }
    }

    /// <summary>无在线玩家的起始时间（UTC）；有在线玩家时为 null。</summary>
    public DateTime? EmptySince { get { lock (_sync) return _emptySince; } }

    /// <summary>
    /// 是否为「保留房间」：房主在房间空无一人时选择保留，房间将一直存在到服务器退出
    /// （不参与宽限期回收）。一旦有新玩家加入并再次成为房主，语义不变——是否保留只由房主退出时决定。
    /// </summary>
    public bool Persistent
    {
        get { lock (_sync) return _persistent; }
        set { lock (_sync) _persistent = value; }
    }

    /// <summary>是否进行中（委托游戏实例的状态）。</summary>
    public bool IsRunning => _game.Status == GameStatus.Running;

    /// <summary>
    /// 维护在线占用时间戳：有在线玩家时清空 <see cref="EmptySince"/>；
    /// 归零时记录首次归零时间（保留最早值，便于宽限期判定）。每次玩家增/减后调用。
    /// </summary>
    public void TouchOccupancy()
    {
        lock (_sync)
        {
            var anyOnline = _players.Any(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId));
            if (anyOnline) _emptySince = null;
            else _emptySince ??= DateTime.UtcNow;
        }
    }

    // ==================== 密码 / 邀请令牌 ====================

    /// <summary>校验密码：公开房间一律放行；加密房间比对带盐哈希。</summary>
    public bool VerifyPassword(string? password)
    {
        string? hash;
        lock (_sync) hash = _passwordHash;
        return hash == null || RoomPassword.Verify(Id, password, hash);
    }

    /// <summary>校验邀请令牌（常量时间比较）。令牌匹配即可进房，无需房间密码。</summary>
    public bool VerifyInviteToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        string current;
        lock (_sync) current = _inviteToken;
        if (current.Length == 0) return false;

        var a = Encoding.UTF8.GetBytes(current);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>作废并重新生成邀请令牌（旧的邀请码随即失效）。返回新令牌。</summary>
    public string RotateInviteToken()
    {
        lock (_sync)
        {
            _inviteToken = NewInviteToken();
            return _inviteToken;
        }
    }

    private static string NewInviteToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
                  .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ==================== 房主设置 ====================

    /// <summary>把当前设置整理成房主视角的 DTO（含邀请令牌，因此只能发给房主）。</summary>
    public HostSettingsDto ToHostSettings()
    {
        lock (_sync)
        {
            return new HostSettingsDto
            {
                RoomId = Id,
                RoomName = Name,
                MaxPlayers = MaxPlayers,
                MinPlayers = MinPlayers,
                MaxPlayersLimit = MaxPlayersLimit,
                HasPassword = _passwordHash != null,
                InviteToken = _inviteToken,
                Running = _game.Status == GameStatus.Running,
                PlayerCount = _players.Count,
                GameSettingsJson = _gameSettingsJson,
                CustomGameSettings = _gameSettingsJson != null
            };
        }
    }

    /// <summary>
    /// 房主修改房间设置。只传需要改的项：
    /// <paramref name="maxPlayers"/> / <paramref name="newPassword"/> 为 null 表示不动；
    /// <paramref name="clearPassword"/> 为 true 表示改成公开房间；
    /// <paramref name="gameSettingsJson"/> 为 null 表示不动、空字符串表示恢复默认、其他值原样存入。
    /// </summary>
    public (bool ok, string msg) UpdateSettings(string connId, int? maxPlayers, string? newPassword,
                                                bool clearPassword, string? gameSettingsJson)
    {
        lock (_sync)
        {
            var me = string.IsNullOrEmpty(connId)
                ? null
                : _players.FirstOrDefault(p => p.ConnectionId == connId);
            if (me == null) return (false, ServerMessages.NotInRoom);
            if (!me.IsHost) return (false, ServerMessages.OnlyHostCanEdit);

            var running = _game.Status == GameStatus.Running;

            if (maxPlayers.HasValue)
            {
                if (running) return (false, ServerMessages.SettingLockedInGame);

                var value = maxPlayers.Value;
                if (value < MinPlayers || value > MaxPlayersLimit)
                    return (false, $"房间人数需在 {MinPlayers} - {MaxPlayersLimit} 之间。");
                if (value < _players.Count)
                    return (false, $"当前已有 {_players.Count} 人，人数上限不能小于它。");

                _maxPlayersOverride = value == MaxPlayersLimit ? 0 : value;
            }

            if (clearPassword)
            {
                _passwordHash = null;   // 改成公开房间；邀请令牌不变，因此旧邀请码依然有效
            }
            else if (newPassword != null)
            {
                if (newPassword.Length < 3) return (false, "房间密码至少 3 位。");
                _passwordHash = RoomPassword.Hash(Id, newPassword);
            }

            if (gameSettingsJson != null)
            {
                if (running) return (false, ServerMessages.SettingLockedInGame);
                _gameSettingsJson = gameSettingsJson.Length == 0 ? null : gameSettingsJson;
            }

            return (true, "");
        }
    }

    // ==================== 玩家管理 ====================

    /// <summary>按用户名（忽略大小写）查找房间内玩家。</summary>
    public RoomPlayer? GetPlayerByName(string username)
    {
        lock (_sync)
            return _players.FirstOrDefault(p =>
                string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按连接号查找房间内玩家。</summary>
    public RoomPlayer? GetPlayer(string connId)
    {
        lock (_sync)
            return string.IsNullOrEmpty(connId)
                ? null
                : _players.FirstOrDefault(p => p.ConnectionId == connId);
    }

    /// <summary>房间内已连接（可推送）的玩家快照。</summary>
    public List<RoomPlayer> ConnectedPlayers()
    {
        lock (_sync)
            return _players
                .Where(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId))
                .ToList();
    }

    /// <summary>生成房间列表摘要（含游戏元数据与房间实际人数上限）。</summary>
    public RoomInfoDto ToInfo()
    {
        lock (_sync)
        {
            var info = _game.Info;
            return new RoomInfoDto
            {
                Id = Id,
                Name = Name,
                GameId = GameId,
                GameName = info.DisplayName,
                GameEmoji = info.Emoji,
                PlayerCount = _players.Count,
                MaxPlayers = MaxPlayers,
                Status = _game.Status.ToString(),
                HasPassword = _passwordHash != null,
                Host = _players.FirstOrDefault(p => p.IsHost)?.Username ?? ""
            };
        }
    }

    /// <summary>加入或重连。返回 (成功, 错误, 玩家对象, 是否重连)</summary>
    public (bool ok, string msg, RoomPlayer? player, bool reconnected)
        AddOrReconnect(string username, string connId)
    {
        (bool ok, string msg, RoomPlayer? player, bool reconnected) result;
        RoomPlayer? joined = null;
        RoomPlayer? reconnected = null;

        lock (_sync)
        {
            var existing = _players.FirstOrDefault(p =>
                string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

            // ---- 已存在：重连 ----
            if (existing != null)
            {
                if (existing.IsConnected && existing.ConnectionId != connId)
                {
                    result = (false, ServerMessages.AlreadyInRoom, null, false);
                }
                else
                {
                    existing.ConnectionId = connId;
                    existing.IsConnected = true;
                    reconnected = existing;
                    result = (true, "", existing, true);
                }
            }
            // ---- 新玩家 ----
            else if (_game.Status == GameStatus.Running)
            {
                result = (false, ServerMessages.GameInProgress, null, false);
            }
            else
            {
                var max = MaxPlayers;
                if (_players.Count >= max)
                {
                    result = (false, ServerMessages.RoomFull(max), null, false);
                }
                else if (string.IsNullOrWhiteSpace(username))
                {
                    result = (false, "用户名不能为空", null, false);
                }
                else
                {
                    var p = new RoomPlayer
                    {
                        Username = username,
                        ConnectionId = connId,
                        IsHost = _players.Count == 0,
                        IsConnected = true
                    };
                    _players.Add(p);
                    joined = p;
                    result = (true, "", p, false);
                }
            }
        }

        // 通知游戏（在房间锁之外：游戏通知内部可能经 IGameContext 回调平台）。
        if (reconnected != null) _game.OnPlayerReconnected(reconnected);
        if (joined != null) _game.OnPlayerJoined(joined);

        return result;
    }

    /// <summary>
    /// 连接离开房间：未开局时把玩家移出名单，开局中只标记断线（局内继续）。
    /// 若离开者正是房主且房间内还有成员，则按**加入顺序**把房主让给下一位
    /// （优先让给仍在线的成员，避免房主落在一个掉线玩家身上导致房间无法开局）。
    /// </summary>
    /// <returns>新房主的用户名；没有发生转让时返回空字符串。</returns>
    public string OnDisconnected(string connId)
    {
        RoomPlayer? left = null;
        var stillInGame = false;
        var newHost = "";

        lock (_sync)
        {
            var p = _players.FirstOrDefault(x => x.ConnectionId == connId);
            if (p == null) return "";

            p.IsConnected = false;
            p.ConnectionId = "";
            left = p;
            stillInGame = _game.Status == GameStatus.Running;

            // 游戏未运行时，直接踢出列表。
            if (!stillInGame) _players.Remove(p);

            if (p.IsHost)
            {
                // 按加入顺序找下一位：先找最早加入且仍在线的，其次找最早加入的。
                var next = _players.FirstOrDefault(x => x.IsConnected && !string.IsNullOrEmpty(x.ConnectionId))
                           ?? _players.FirstOrDefault();
                if (next != null)
                {
                    foreach (var q in _players) q.IsHost = false;
                    next.IsHost = true;
                    newHost = next.Username;
                }
            }
        }

        if (left != null) _game.OnPlayerLeft(left, stillInGame);
        return newHost;
    }

    // ==================== 开局 / 游戏操作（平台外壳） ====================

    /// <summary>
    /// 房主开局：平台负责房主校验与在线玩家清理，玩法条件（人数下限 / 身份布局校验等）由游戏实例判断；
    /// 成功后启动游戏主循环。
    /// </summary>
    public (bool ok, string msg) StartGame(string connId)
    {
        lock (_sync)
        {
            if (_game.Status == GameStatus.Running) return (false, "游戏已开始");

            var host = _players.FirstOrDefault(p => p.IsHost);
            if (host == null || host.ConnectionId != connId)
                return (false, ServerMessages.OnlyHostCanStart);

            // 清理断线玩家，保证开局名单干净。
            _players.RemoveAll(p => !p.IsConnected);
        }

        var (ok, msg) = _game.Start();
        if (!ok) return (ok, msg);

        _ = Task.Run(_game.RunAsync);   // 游戏在此驱动自身主循环
        return (true, "");
    }

    /// <summary>
    /// 处理一次「本房间当前游戏」的操作（框架只做透传，不理解 op 语义）。
    /// 已由网络层保证连接已登录；此处再解析「连接 → 玩家」，随后交给游戏实例。
    /// </summary>
    public (bool ok, string msg) HandleGameAction(string op, JsonObject? payload, string connectionId)
    {
        var player = GetPlayer(connectionId);
        if (player == null) return (false, ServerMessages.NotInRoom);

        return _game.HandleAction(op, payload, connectionId);
    }

    /// <summary>
    /// 取某个连接视角下的当前状态快照；该连接不在本房间时返回 null。
    /// 通用字段由平台填充，游戏私有负载与玩家标签由游戏实例填充。
    /// </summary>
    public GameStateDto? SnapshotFor(string connId)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(connId)) return null;

            var viewer = _players.FirstOrDefault(p => p.ConnectionId == connId);
            if (viewer == null) return null;

            return BuildStateFor(viewer);
        }
    }

    /// <summary>组装某玩家视角的通用 DTO，并回调游戏补齐私有负载与玩家标签（调用方需持有房间锁）。</summary>
    private GameStateDto BuildStateFor(RoomPlayer viewer)
    {
        var status = _game.Status;

        var dto = new GameStateDto
        {
            GameId = GameId,
            RoomId = Id,
            RoomName = Name,
            HostName = _players.FirstOrDefault(p => p.IsHost)?.Username ?? "",
            Running = status == GameStatus.Running,
            Status = status.ToString(),
            YourId = _players.IndexOf(viewer),
            IsAlive = viewer.IsAlive,
            Players = _players.Select((p, i) => new PlayerDto
            {
                Id = i,
                Name = p.Username,
                IsAlive = p.IsAlive,
                IsHost = p.IsHost,
                IsConnected = p.IsConnected,
                IsYou = ReferenceEquals(p, viewer)
            }).ToList()
        };

        _game.DecoratePlayers(dto.Players, viewer.Username);
        dto.GamePayload = _game.BuildPayload(viewer.ConnectionId);
        return dto;
    }

    // ==================== 消息推送（IGameContext） ====================

    /// <inheritdoc />
    public async Task BroadcastAsync(string message)
    {
        foreach (var p in OnlinePlayers)
            await _channel.SendMessageAsync(p.ConnectionId, message);
    }

    /// <inheritdoc />
    public Task PrivateAsync(string connectionId, string text)
        => string.IsNullOrEmpty(connectionId) ? Task.CompletedTask : _channel.SendMessageAsync(connectionId, text);

    /// <summary>遍历在线玩家，逐个生成「该玩家视角」的状态 DTO 后加密推送（视角隔离）。</summary>
    public async Task BroadcastStateAsync()
    {
        List<RoomPlayer> viewers;
        lock (_sync) viewers = _players.Where(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId)).ToList();

        foreach (var viewer in viewers)
        {
            GameStateDto dto;
            lock (_sync)
            {
                if (viewer.ConnectionId.Length == 0) continue;
                dto = BuildStateFor(viewer);
            }
            await _channel.SendStateAsync(viewer.ConnectionId, dto);
        }
    }

    // ==================== IGameContext：对游戏暴露的平台原语 ====================

    /// <inheritdoc />
    public string RoomId => Id;

    /// <inheritdoc />
    public string RoomName => Name;

    /// <inheritdoc />
    public IReadOnlyList<RoomPlayer> Players { get { lock (_sync) return _players.ToList(); } }

    /// <inheritdoc />
    public IReadOnlyList<RoomPlayer> OnlinePlayers
    {
        get
        {
            lock (_sync)
                return _players
                    .Where(p => p.IsConnected && !string.IsNullOrEmpty(p.ConnectionId))
                    .ToList();
        }
    }

    /// <inheritdoc />
    public RoomPlayer? FindByConnection(string connId) => GetPlayer(connId);

    /// <inheritdoc />
    public RoomPlayer? FindByName(string username) => GetPlayerByName(username);

    /// <inheritdoc />
    public IGameConfig Config => _config;
}
