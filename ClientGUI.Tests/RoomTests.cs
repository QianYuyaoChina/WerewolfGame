using System.Text.Json;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// 多房间改造的纯逻辑校验（不依赖真实服务器）：
/// 房间号生成、房间密码带盐哈希、房间注册表（建/列/入/离/密码门禁/满员/进行中拒绝/回收宽限期）
/// 以及新增协议的 JSON 往返。
/// </summary>
public static class RoomTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void RunAll()
    {
        RoomIdTests();
        RoomPasswordTests();
        RoomManagerCreateAndGateTests();
        RoomManagerFullAndReconnectTests();
        RoomRecycleTests();
        ProtocolRoundTrip();
    }

    // ==================== 房间号 ====================

    private static void RoomIdTests()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var allValid = true;
        for (var i = 0; i < 1000; i++)
        {
            var id = RoomId.Generate(_ => false);
            if (id.Length != RoomId.Length || !id.All(c => RoomId.Alphabet.Contains(c)))
                allValid = false;
            ids.Add(id);
        }

        Check.True(allValid, "房间号：长度均为 5 且字符均来自既定字符集");
        Check.Equal(1000, ids.Count, "房间号：1000 次生成无重复（冲突重试生效）");

        Check.Equal(31, RoomId.Alphabet.Length, "房间号：字符集为 31 个字符");
        Check.False(RoomId.Alphabet.Contains('0'), "房间号：字符集排除 0");
        Check.False(RoomId.Alphabet.Contains('O'), "房间号：字符集排除 O");
        Check.False(RoomId.Alphabet.Contains('1'), "房间号：字符集排除 1");
        Check.False(RoomId.Alphabet.Contains('I'), "房间号：字符集排除 I");
        Check.False(RoomId.Alphabet.Contains('L'), "房间号：字符集排除 L");

        var threw = false;
        try { RoomId.Generate(_ => true); }
        catch (InvalidOperationException) { threw = true; }
        Check.True(threw, "房间号：始终冲突时抛 InvalidOperationException");
    }

    // ==================== 房间密码 ====================

    private static void RoomPasswordTests()
    {
        var h1 = RoomPassword.Hash("ABCDE", "secret");
        var h2 = RoomPassword.Hash("ABCDE", "secret");
        var h3 = RoomPassword.Hash("FGHJK", "secret");   // 同密码、不同房间号

        Check.True(h1.StartsWith("v2:", StringComparison.Ordinal), "房间密码：存储值带 v2: 前缀");
        Check.Equal(h1, h2, "房间密码：同房间同密码哈希稳定");
        Check.NotEqual(h1, h3, "房间密码：同密码不同房间号哈希不同（加盐生效）");
        Check.False(h1.Contains("secret", StringComparison.Ordinal), "房间密码：存储值不含明文");

        Check.True(RoomPassword.Verify("ABCDE", "secret", h1), "房间密码：正确密码校验通过");
        Check.False(RoomPassword.Verify("ABCDE", "wrong", h1), "房间密码：错误密码校验失败");
        Check.False(RoomPassword.Verify("FGHJK", "secret", h1), "房间密码：换房间号校验失败");
        Check.False(RoomPassword.Verify("ABCDE", "secret", ""), "房间密码：存储值缺失时校验失败");
        Check.False(RoomPassword.Verify("ABCDE", "secret", null), "房间密码：存储值 null 时校验失败");
    }

    // ==================== 注册表：建房 / 列表 / 密码门禁 ====================

    private static void RoomManagerCreateAndGateTests()
    {
        var mgr = NewManager();
        foreach (var (conn, user) in new[]
                 {
                     ("c-host", "host"), ("c-bob", "bob"), ("c-carol", "carol"),
                     ("c-dave", "dave"), ("c-eve", "eve")
                 })
            mgr.BindIdentity(conn, user);

        // 未登录（未绑定身份）不能建房。
        var anon = mgr.CreateRoom("c-anon", "x", null);
        Check.False(anon.ok, "注册表：未登录不能创建房间");
        Check.Equal(ServerMessages.NotLoggedIn, anon.msg, "注册表：未登录建房返回统一文案");

        // 建房并成为房主。
        var created = mgr.CreateRoom("c-host", "新手房", "123");
        Check.True(created.ok, "注册表：创建房间成功");
        Check.Equal(RoomId.Length, created.roomId.Length, "注册表：房间号为 5 位");
        Check.Equal(1, mgr.List().Count, "注册表：列表包含 1 个房间");

        var info = mgr.Find(created.roomId)!.ToInfo();
        Check.Equal("新手房", info.Name, "注册表：房间名正确");
        Check.True(info.HasPassword, "注册表：加密房 HasPassword=true");
        Check.Equal("host", info.Host, "注册表：房主为创建者");
        Check.Equal(RoomStatus.Waiting, info.Status, "注册表：初始状态为等待中（Waiting）");
        Check.Equal(1, info.PlayerCount, "注册表：初始人数为 1");
        Check.Equal(4, info.MaxPlayers, "注册表：人数上限沿用规则（4）");

        Check.Equal("host", mgr.GetUsername("c-host"), "注册表：身份绑定可读回");
        Check.True(ReferenceEquals(mgr.Find(created.roomId), mgr.GetRoomByConnection("c-host")),
            "注册表：可按连接找到所在房间");

        // 列表不得泄露密码哈希。
        var listJson = JsonSerializer.Serialize(mgr.List(), Json);
        Check.False(listJson.Contains(RoomPassword.Hash(created.roomId, "123"), StringComparison.Ordinal),
            "注册表：房间列表不含密码哈希");

        // 密码门禁。
        var wrong = mgr.JoinRoom(created.roomId, "c-bob", "bad");
        Check.False(wrong.ok, "注册表：错误密码被拒");
        Check.Equal(ServerMessages.WrongPassword, wrong.msg, "注册表：错误密码返回统一文案");

        var okJoin = mgr.JoinRoom(created.roomId, "c-bob", "123");
        Check.True(okJoin.ok, "注册表：正确密码加入成功");
        Check.False(okJoin.reconnected, "注册表：新玩家非重连");
        Check.Equal(2, mgr.Find(created.roomId)!.PlayerCount, "注册表：加入后人数为 2");

        // 房间不存在。
        var none = mgr.JoinRoom("ZZZZZ", "c-carol", null);
        Check.False(none.ok, "注册表：房间不存在被拒");
        Check.Equal(ServerMessages.RoomNotFound, none.msg, "注册表：房间不存在返回统一文案");

        // 公开房间（无密码）无需密码即可加入。
        var pub = mgr.CreateRoom("c-eve", "公开房", null);
        Check.True(pub.ok, "注册表：创建公开房间成功");
        Check.False(mgr.Find(pub.roomId)!.HasPassword, "注册表：公开房间 HasPassword=false");
        Check.Equal(2, mgr.List().Count, "注册表：列表包含 2 个房间");
        var pubJoin = mgr.JoinRoom(pub.roomId, "c-carol", "任意密码");
        Check.True(pubJoin.ok, "注册表：公开房间忽略密码直接放行");
    }

    // ==================== 注册表：满员 / 进行中 / 重连 ====================

    private static void RoomManagerFullAndReconnectTests()
    {
        var mgr = NewManager();
        foreach (var (conn, user) in new[]
                 {
                     ("c-host", "host"), ("c-bob", "bob"), ("c-carol", "carol"),
                     ("c-dave", "dave"), ("c-eve", "eve"), ("c-frank", "frank")
                 })
            mgr.BindIdentity(conn, user);

        var room0 = mgr.CreateRoom("c-host", "满员房", "123");
        Check.True(room0.ok, "注册表：满员测试建房成功");
        var roomId = room0.roomId;

        Check.True(mgr.JoinRoom(roomId, "c-bob", "123").ok, "注册表：bob 加入");
        Check.True(mgr.JoinRoom(roomId, "c-carol", "123").ok, "注册表：carol 加入");
        Check.True(mgr.JoinRoom(roomId, "c-dave", "123").ok, "注册表：dave 加入");
        Check.Equal(4, mgr.Find(roomId)!.PlayerCount, "注册表：房间已满 4 人");

        var full = mgr.JoinRoom(roomId, "c-eve", "123");
        Check.False(full.ok, "注册表：满员拒绝加入");
        Check.Equal(ServerMessages.RoomFull(4), full.msg, "注册表：满员返回统一文案");

        // 开局 → Running。
        var room = mgr.Find(roomId)!;
        var start = room.StartGame("c-host");
        Check.True(start.ok, "注册表：房主开局成功");
        Check.Equal(RoomStatus.Running, room.ToInfo().Status, "注册表：开局后状态为游戏进行中");

        // 进行中禁止新玩家加入。
        var inProgress = mgr.JoinRoom(roomId, "c-frank", "123");
        Check.False(inProgress.ok, "注册表：进行中拒绝加入");
        Check.Equal(ServerMessages.GameInProgress, inProgress.msg, "注册表：进行中返回统一文案");

        // 重连：对局进行中掉线仍保留名单，重连时跳过密码校验。
        mgr.LeaveByConnection("c-bob");
        Check.True(room.GetPlayerByName("bob") != null, "注册表：对局中掉线仍保留在名单（不清局）");

        mgr.BindIdentity("c-bob2", "bob");
        var re = mgr.JoinRoom(roomId, "c-bob2", null);   // 不带密码
        Check.True(re.ok, "注册表：重连跳过密码校验（命中本房玩家）");
        Check.True(re.reconnected, "注册表：重连标记为 true");
    }

    // ==================== 房间回收宽限期 ====================

    private static void RoomRecycleTests()
    {
        // 宽限期 0 秒：空房立即被回收。
        var mgr = NewManager(graceSeconds: 0);
        mgr.BindIdentity("c1", "u1");
        var created = mgr.CreateRoom("c1", "待回收房", null);
        Check.True(created.ok, "回收：建房成功");

        mgr.LeaveRoom("c1");   // 主动离开 → 房间无在线玩家
        Check.Equal(0, mgr.Find(created.roomId)!.ConnectedCount, "回收：离开后无在线玩家");
        Check.True(mgr.Find(created.roomId)!.EmptySince != null, "回收：无在线玩家时记录 EmptySince");

        Thread.Sleep(20);
        Check.Equal(1, mgr.RecycleExpired(), "回收：超过宽限期(0s)后回收空房");
        Check.True(mgr.Find(created.roomId) == null, "回收：房间已从列表移除");

        // 宽限期很长：不回收。
        var mgr2 = NewManager(graceSeconds: 3600);
        mgr2.BindIdentity("c2", "u2");
        var created2 = mgr2.CreateRoom("c2", "保留房", null);
        mgr2.LeaveRoom("c2");
        Check.Equal(0, mgr2.RecycleExpired(), "回收：宽限期内不回收");
        Check.True(mgr2.Find(created2.roomId) != null, "回收：宽限期内房间仍在列表");
    }

    // ==================== 协议 JSON 往返 ====================

    private static void ProtocolRoundTrip()
    {
        var create = new CreateRoomPayload { Name = "新手房", Password = "123" };
        var createBack = JsonSerializer.Deserialize<CreateRoomPayload>(JsonSerializer.Serialize(create), Json);
        Check.Equal("新手房", createBack!.Name, "协议：CreateRoomPayload.Name 往返");
        Check.Equal("123", createBack.Password, "协议：CreateRoomPayload.Password 往返");

        var join = new JoinRoomPayload { RoomId = "ABCDE", Password = null };
        var joinBack = JsonSerializer.Deserialize<JoinRoomPayload>(JsonSerializer.Serialize(join), Json);
        Check.Equal("ABCDE", joinBack!.RoomId, "协议：JoinRoomPayload.RoomId 往返");
        Check.True(joinBack.Password == null, "协议：JoinRoomPayload.Password 可为 null");

        var info = new RoomInfoDto
        {
            Id = "ABCDE",
            Name = "房间甲",
            PlayerCount = 3,
            MaxPlayers = 12,
            Status = RoomStatus.Waiting,
            HasPassword = true,
            Host = "张三"
        };
        var infoBack = JsonSerializer.Deserialize<RoomInfoDto>(JsonSerializer.Serialize(info), Json);
        Check.Equal("ABCDE", infoBack!.Id, "协议：RoomInfoDto.Id 往返");
        Check.Equal(3, infoBack.PlayerCount, "协议：RoomInfoDto.PlayerCount 往返");
        Check.Equal(12, infoBack.MaxPlayers, "协议：RoomInfoDto.MaxPlayers 往返");
        Check.Equal(RoomStatus.Waiting, infoBack.Status, "协议：RoomInfoDto.Status 往返");
        Check.True(infoBack.HasPassword, "协议：RoomInfoDto.HasPassword 往返");
        Check.Equal("张三", infoBack.Host, "协议：RoomInfoDto.Host 往返");

        var list = new RoomListResult { Ok = true, Msg = "", Rooms = new() { info } };
        var listBack = JsonSerializer.Deserialize<RoomListResult>(JsonSerializer.Serialize(list), Json);
        Check.Equal(1, listBack!.Rooms.Count, "协议：RoomListResult.Rooms 往返");
        Check.Equal("ABCDE", listBack.Rooms[0].Id, "协议：RoomListResult 内层房间往返");

        var action = new RoomActionResult { RoomId = "ABCDE", Reconnected = false, Room = info };
        var actionBack = JsonSerializer.Deserialize<RoomActionResult>(JsonSerializer.Serialize(action), Json);
        Check.Equal("ABCDE", actionBack!.RoomId, "协议：RoomActionResult.RoomId 往返");
        Check.True(actionBack.Room != null && actionBack.Room.Host == "张三",
            "协议：RoomActionResult.Room.Host 往返");

        Check.Equal("等待中", RoomStatus.Name(RoomStatus.Waiting), "协议：RoomStatus.Name 等待中");
        Check.Equal("游戏进行中", RoomStatus.Name(RoomStatus.Running), "协议：RoomStatus.Name 进行中");
        Check.Equal("已结束待重开", RoomStatus.Name(RoomStatus.GameOver), "协议：RoomStatus.Name 已结束");
    }

    // ==================== 辅助 ====================

    /// <summary>构造一个可独立测试的注册表（依赖用最简对象，不产生后台线程）。</summary>
    private static RoomManager NewManager(int maxRooms = 100, int graceSeconds = 60)
    {
        var channel = new EncryptedChannel(null!, new CryptoSessionStore());
        var rules = new GameRules
        {
            MinPlayers = 4,
            MaxPlayers = 4,
            NightActionSeconds = 1,
            DiscussionSeconds = 1,
            VoteSeconds = 1,
            HunterShotSeconds = 1
        };
        var options = new RoomOptions
        {
            MaxRooms = maxRooms,
            EmptyGraceSeconds = graceSeconds,
            RecycleScanSeconds = 1
        };
        return new RoomManager(channel, rules, options);
    }
}
