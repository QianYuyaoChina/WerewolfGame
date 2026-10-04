using System.Collections.Concurrent;
using Werewolf.Client.Share;

namespace Werewolf.Server;

/// <summary>
/// 房间注册表：以房间号为键管理多个 <see cref="GameRoom"/>，并维护「连接 → 房间 / 账号」映射。
/// 设计为**纯注册表、无后台线程**，便于测试直接 new；周期回收由 <see cref="RoomRecycleService"/> 负责。
/// 所有字典均为 <see cref="ConcurrentDictionary{TKey,TValue}"/>，跨房间只读房间的只读快照属性，
/// 绝不在持有注册表操作时去遍历某个房间的玩家集合。
/// </summary>
public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, string> _connRoom = new();
    private readonly ConcurrentDictionary<string, string> _connUser = new();

    private readonly EncryptedChannel _channel;
    private readonly GameRules _rules;
    private readonly RoomOptions _options;

    public RoomManager(EncryptedChannel channel, GameRules rules, RoomOptions options)
    {
        _channel = channel;
        _rules = rules;
        _options = options;
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
    /// 创建房间并让发起连接成为房主。
    /// 返回 (成功, 提示, 房间号, 是否重连)。
    /// </summary>
    public (bool ok, string msg, string roomId, bool reconnected)
        CreateRoom(string connId, string name, string? password)
    {
        var username = GetUsername(connId);
        if (string.IsNullOrEmpty(username)) return (false, ServerMessages.NotLoggedIn, "", false);
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

        var room = new GameRoom(roomId, roomName, password, _channel, _rules);
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

    /// <summary>主动离开房间（保留账号身份，连接不断）。返回 (房间, 玩家) 供上层广播。</summary>
    public (GameRoom? room, Player? player) LeaveRoom(string connId) => Detach(connId);

    /// <summary>连接断开：脱离房间并清掉「连接 → 账号」映射。返回 (房间, 玩家) 供上层广播。</summary>
    public (GameRoom? room, Player? player) LeaveByConnection(string connId)
    {
        var result = Detach(connId);
        _connUser.TryRemove(connId, out _);
        return result;
    }

    /// <summary>
    /// 回收超宽限期的空房（无在线玩家）。返回回收数量。
    /// </summary>
    public int RecycleExpired()
    {
        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Max(0, _options.EmptyGraceSeconds));
        var removed = 0;

        foreach (var kv in _rooms)
        {
            var room = kv.Value;
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

    /// <summary>把连接从当前房间摘除（房间内标记断线 / 未开局则移出名单），并清理映射。</summary>
    private (GameRoom? room, Player? player) Detach(string connId)
    {
        if (string.IsNullOrEmpty(connId) || !_connRoom.TryRemove(connId, out var roomId))
            return (null, null);

        var room = Find(roomId);
        if (room == null) return (null, null);

        var player = room.GetPlayer(connId);
        room.OnDisconnected(connId);
        room.TouchOccupancy();
        return (room, player);
    }

    /// <summary>若连接已映射到别的房间，先摘除旧房间映射（保留账号身份）。</summary>
    private void DetachFromOtherRoom(string connId, string keepRoomId)
    {
        if (_connRoom.TryGetValue(connId, out var current) && current != keepRoomId)
            Detach(connId);
    }
}
