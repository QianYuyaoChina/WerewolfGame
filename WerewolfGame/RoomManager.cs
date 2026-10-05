using System.Collections.Concurrent;
using Werewolf.Client.Share;
using Werewolf.Server.Games;

namespace Werewolf.Server;

/// <summary>
/// 房间注册表：以房间号为键管理多个 <see cref="GameRoom"/>，并维护「连接 → 房间 / 账号」映射。
/// 设计为**纯注册表、无后台线程**，便于测试直接 new；周期回收由 <see cref="RoomRecycleService"/> 负责。
/// 依赖 <see cref="IGameCatalog"/> 取「可用游戏 / 实例工厂」，本类不感知任何具体游戏。
/// 所有字典均为 <see cref="ConcurrentDictionary{TKey,TValue}"/>，跨房间只读房间的只读快照属性，
/// 绝不在持有注册表操作时去遍历某个房间的玩家集合。
/// </summary>
public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, string> _connRoom = new();
    private readonly ConcurrentDictionary<string, string> _connUser = new();

    private readonly EncryptedChannel _channel;
    private readonly IGameCatalog _catalog;
    private readonly RoomOptions _options;
    private readonly IGameConfig? _config;

    public RoomManager(EncryptedChannel channel, IGameCatalog catalog, RoomOptions options, IGameConfig? config = null)
    {
        _channel = channel;
        _catalog = catalog;
        _options = options;
        _config = config;
    }

    /// <summary>当前存活房间数。</summary>
    public int Count => _rooms.Count;

    /// <summary>列出全部房间摘要（按创建时间排序）。</summary>
    public List<RoomInfoDto> List()
        => _rooms.Values
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.ToInfo())
            .ToList();

    /// <summary>按房间号查找房间；不存在返回 null。</summary>
    public GameRoom? Find(string roomId)
        => !string.IsNullOrEmpty(roomId) && _rooms.TryGetValue(roomId, out var room) ? room : null;

    /// <summary>账号校验通过后绑定「连接 → 用户名」。</summary>
    public void BindIdentity(string connId, string username)
    {
        if (string.IsNullOrEmpty(connId)) return;
        _connUser[connId] = username ?? "";
    }

    /// <summary>取该连接绑定的用户名；未绑定时为空字符串。</summary>
    public string GetUsername(string connId)
        => !string.IsNullOrEmpty(connId) && _connUser.TryGetValue(connId, out var u) ? u : "";

    /// <summary>取该连接当前所在房间；不在任何房间时返回 null。</summary>
    public GameRoom? GetRoomByConnection(string connId)
    {
        if (string.IsNullOrEmpty(connId) || !_connRoom.TryGetValue(connId, out var roomId)) return null;
        return Find(roomId);
    }

    /// <summary>
    /// 创建房间并让发起连接成为房主。房间绑定到指定游戏：
    /// <paramref name="gameId"/> 缺失 / 空白 → 回退 <see cref="GameIds.Default"/>（狼人杀）；
    /// 非空但服务器不支持 → 返回 <see cref="ServerMessages.GameUnavailable"/>。
    /// 返回 (成功, 提示, 房间号, 是否重连)。
    /// </summary>
    public (bool ok, string msg, string roomId, bool reconnected)
        CreateRoom(string connId, string name, string? password, string? gameId)
    {
        var username = GetUsername(connId);
        if (string.IsNullOrEmpty(username)) return (false, ServerMessages.NotLoggedIn, "", false);

        // ---- 解析游戏：缺失回退 werewolf；未知拒绝 ----
        var requested = (gameId ?? "").Trim();
        string resolved;
        if (requested.Length == 0) resolved = GameIds.Default;
        else if (_catalog.Contains(requested)) resolved = requested;
        else return (false, ServerMessages.GameUnavailable, "", false);

        if (!_catalog.TryGetFactory(resolved, out var factory))
            return (false, ServerMessages.GameUnavailable, "", false);

        if (_rooms.Count >= Math.Max(1, _options.MaxRooms))
            return (false, ServerMessages.TooManyRooms, "", false);

        var roomName = (name ?? "").Trim();
        if (roomName.Length == 0) roomName = $"{username} 的房间";
        if (roomName.Length > 40) roomName = roomName[..40];

        string roomId;
        try
        {
            roomId = RoomId.Generate(id => _rooms.ContainsKey(id));
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, "", false);
        }

        var room = new GameRoom(roomId, roomName, password, _channel, resolved, factory, _config);
        if (!_rooms.TryAdd(roomId, room))
            return (false, ServerMessages.TooManyRooms, "", false);

        var (ok, msg, _, _) = room.AddOrReconnect(username, connId);
        if (!ok)
        {
            _rooms.TryRemove(roomId, out _);
            return (false, msg, "", false);
        }

        // 一个连接同一时刻只在一个房间：成功后才脱离可能存在的旧房间（保留账号身份）。
        DetachFromOtherRoom(connId, roomId);

        _connRoom[connId] = roomId;
        room.TouchOccupancy();
        return (true, ServerMessages.Ok, roomId, false);
    }

    /// <summary>
    /// 按房间号加入。加密房间校验密码；命中「本房玩家」视为重连并跳过密码校验。
    /// 返回 (成功, 提示, 房间号, 是否重连)。
    /// </summary>
    public (bool ok, string msg, string roomId, bool reconnected)
        JoinRoom(string roomId, string connId, string? password)
    {
        var username = GetUsername(connId);
        if (string.IsNullOrEmpty(username)) return (false, ServerMessages.NotLoggedIn, "", false);

        var room = Find(roomId);
        if (room == null) return (false, ServerMessages.RoomNotFound, "", false);

        // 命中本房玩家即视为重连，跳过密码校验（客户端仍会带上内存中的密码）。
        var alreadyMember = room.GetPlayerByName(username) != null;
        if (!alreadyMember && !room.VerifyPassword(password))
            return (false, ServerMessages.WrongPassword, "", false);

        var (ok, msg, _, reconnected) = room.AddOrReconnect(username, connId);
        if (!ok) return (false, msg, "", false);

        // 成功加入后才脱离旧房间，避免加入失败时丢失原房间归属。
        DetachFromOtherRoom(connId, roomId);

        _connRoom[connId] = roomId;
        room.TouchOccupancy();
        return (true, reconnected ? "reconnected" : "joined", roomId, reconnected);
    }

    /// <summary>
    /// 主动离开房间（保留账号身份，连接不断）。
    /// <paramref name="keepRoom"/> 仅在「自己是房主且房间已无其他成员」时生效：
    /// true → 把房间标记为保留（不参与回收）；false → 立即关闭并移除房间。
    /// 房间内还有其他成员时，房主会转让给下一位，房间始终保留。
    /// </summary>
    /// <returns>(房间, 玩家, 新房主用户名, 房间是否已被关闭) 供上层广播。</returns>
    public (GameRoom? room, RoomPlayer? player, string newHost, bool roomClosed)
        LeaveRoom(string connId, bool keepRoom = false)
    {
        var room = GetRoomByConnection(connId);
        if (room == null)
        {
            Detach(connId);   // 清理可能残留的映射
            return (null, null, "", false);
        }

        var player = room.GetPlayer(connId);
        var (detached, _, newHost) = Detach(connId);
        if (detached == null) return (null, null, "", false);

        // 房间已空：由房主当时的意愿决定「保留」还是「关闭」。
        if (detached.PlayerCount == 0)
        {
            if (keepRoom)
            {
                detached.Persistent = true;
                return (detached, player, "", false);
            }

            CloseRoom(detached.Id);
            return (detached, player, "", true);
        }

        return (detached, player, newHost, false);
    }

    /// <summary>关闭并移除房间（同时清理可能残留的「连接 → 房间」映射）。</summary>
    public bool CloseRoom(string roomId)
    {
        if (string.IsNullOrEmpty(roomId) || !_rooms.TryRemove(roomId, out _)) return false;

        foreach (var mapping in _connRoom)
            if (mapping.Value == roomId) _connRoom.TryRemove(mapping.Key, out _);

        return true;
    }

    /// <summary>连接断开：脱离房间并清掉「连接 → 账号」映射。返回 (房间, 玩家, 新房主) 供上层广播。</summary>
    public (GameRoom? room, RoomPlayer? player, string newHost) LeaveByConnection(string connId)
    {
        var result = Detach(connId);
        _connUser.TryRemove(connId, out _);
        return result;
    }

    /// <summary>
    /// 回收超宽限期的空房（无在线玩家）。返回回收数量。
    /// 被标记为「保留」的房间永不回收，一直存在到服务器退出。
    /// </summary>
    public int RecycleExpired()
    {
        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Max(0, _options.EmptyGraceSeconds));
        var removed = 0;

        foreach (var kv in _rooms)
        {
            var room = kv.Value;
            if (room.Persistent) continue;
            if (room.ConnectedCount != 0) continue;

            var emptySince = room.EmptySince;
            if (emptySince == null) continue;
            if (now - emptySince.Value <= grace) continue;

            if (!_rooms.TryRemove(kv.Key, out _)) continue;
            removed++;

            // 清理可能残留的「连接 → 房间」映射。
            foreach (var mapping in _connRoom)
                if (mapping.Value == kv.Key) _connRoom.TryRemove(mapping.Key, out _);
        }

        return removed;
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 把连接从当前房间摘除（房间内标记断线 / 未开局则移出名单），并清理映射。
    /// 返回 (房间, 玩家, 新房主用户名)。
    /// </summary>
    private (GameRoom? room, RoomPlayer? player, string newHost) Detach(string connId)
    {
        if (string.IsNullOrEmpty(connId) || !_connRoom.TryRemove(connId, out var roomId))
            return (null, null, "");

        var room = Find(roomId);
        if (room == null) return (null, null, "");

        var player = room.GetPlayer(connId);
        var newHost = room.OnDisconnected(connId);
        room.TouchOccupancy();
        return (room, player, newHost);
    }

    /// <summary>若连接已映射到别的房间，先摘除旧房间映射（保留账号身份）。</summary>
    private void DetachFromOtherRoom(string connId, string keepRoomId)
    {
        if (_connRoom.TryGetValue(connId, out var current) && current != keepRoomId)
            Detach(connId);
    }
}
