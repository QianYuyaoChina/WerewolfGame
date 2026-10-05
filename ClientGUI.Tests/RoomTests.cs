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
        HostTransferTests();
        KeepRoomTests();
        InviteCodeTests();
        ProtocolRoundTrip();
    }

    // ==================== 房间号 ====================

    private static void RoomIdTests()
    {
        // 按真实用法生成：把已用房间号作为「是否存在」判据传进去（RoomManager 就是这么用的）。
        // 注意不要断言「纯随机 1000 次不重复」——那是生日悖论，约 1.7% 概率误报，
        // 真正要验证的是「冲突时能自动重试并拿到不重复的房间号」。
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var allValid = true;
        for (var i = 0; i < 1000; i++)
        {
            var id = RoomId.Generate(taken.Contains);
            if (id.Length != RoomId.Length || !id.All(c => RoomId.Alphabet.Contains(c)))
                allValid = false;
            taken.Add(id);
        }

        Check.True(allValid, "房间号：长度均为 5 且字符均来自既定字符集");
        Check.Equal(1000, taken.Count, "房间号：按真实用法生成 1000 次互不重复（冲突自动重试）");

        Check.Equal(31, RoomId.Alphabet.Length, "房间号：字符集为 31 个字符");
        Check.False(RoomId.Alphabet.Contains('0'), "房间号：字符集排除 0");
        Check.False(RoomId.Alphabet.Contains('O'), "房间号：字符集排除 O");
        Check.False(RoomId.Alphabet.Contains('1'), "房间号：字符集排除 1");
        Check.False(RoomId.Alphabet.Contains('I'), "房间号：字符集排除 I");
        Check.False(RoomId.Alphabet.Contains('L'), "房间号：字符集排除 L");

        // 前 3 次都判定为冲突 → 必须重试后成功（验证重试确实生效）。
        var attempts = 0;
        var retried = RoomId.Generate(_ => ++attempts <= 3);
        Check.Equal(4, attempts, "房间号：连续冲突时按次数重试");
        Check.Equal(RoomId.Length, retried.Length, "房间号：重试后仍返回合法房间号");

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
        var anon = mgr.CreateRoom("c-anon", "x", null, GameIds.Werewolf);
        Check.False(anon.ok, "注册表：未登录不能创建房间");
        Check.Equal(ServerMessages.NotLoggedIn, anon.msg, "注册表：未登录建房返回统一文案");

        // 建房并成为房主。
        var created = mgr.CreateRoom("c-host", "新手房", "123", GameIds.Werewolf);
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
        var pub = mgr.CreateRoom("c-eve", "公开房", null, GameIds.Werewolf);
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

        var room0 = mgr.CreateRoom("c-host", "满员房", "123", GameIds.Werewolf);
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
        // 宽限期 0 秒：**掉线**留下的空房在扫描时立即被回收（主动退房不走这条路，见下）。
        var mgr = NewManager(graceSeconds: 0);
        mgr.BindIdentity("c1", "u1");
        var created = mgr.CreateRoom("c1", "待回收房", null, GameIds.Werewolf);
        Check.True(created.ok, "回收：建房成功");

        mgr.LeaveByConnection("c1");   // 掉线 → 房间无在线玩家，但仍先等待宽限期
        Check.True(mgr.Find(created.roomId) != null, "回收：掉线不会立刻关闭房间（走宽限期）");
        Check.Equal(0, mgr.Find(created.roomId)!.ConnectedCount, "回收：掉线后无在线玩家");
        Check.True(mgr.Find(created.roomId)!.EmptySince != null, "回收：无在线玩家时记录 EmptySince");

        Thread.Sleep(20);
        Check.Equal(1, mgr.RecycleExpired(), "回收：超过宽限期(0s)后回收空房");
        Check.True(mgr.Find(created.roomId) == null, "回收：房间已从列表移除");

        // 宽限期很长：不回收。
        var mgr2 = NewManager(graceSeconds: 3600);
        mgr2.BindIdentity("c2", "u2");
        var created2 = mgr2.CreateRoom("c2", "保留房", null, GameIds.Werewolf);
        mgr2.LeaveByConnection("c2");
        Check.Equal(0, mgr2.RecycleExpired(), "回收：宽限期内不回收");
        Check.True(mgr2.Find(created2.roomId) != null, "回收：宽限期内房间仍在列表");

        // 主动退出（leaveroom）且不保留 → 立即关闭，不必等宽限期。
        var mgr3 = NewManager(graceSeconds: 3600);
        mgr3.BindIdentity("c3", "u3");
        var created3 = mgr3.CreateRoom("c3", "主动退房", null, GameIds.Werewolf);
        var left = mgr3.LeaveRoom("c3");
        Check.True(left.roomClosed, "回收：主动退出且不保留 → 立即关闭房间");
        Check.True(mgr3.Find(created3.roomId) == null, "回收：关闭后房间不在列表中");
    }

    // ==================== 房主转让 ====================

    /// <summary>
    /// 房主离开 / 掉线时，房主按「加入顺序」转让给下一位（第一个是房主，第二个接手，以此类推）。
    /// </summary>
    private static void HostTransferTests()
    {
        var mgr = NewManager();
        foreach (var (conn, user) in new[] { ("c-a", "alpha"), ("c-b", "bravo"), ("c-c", "charlie") })
            mgr.BindIdentity(conn, user);

        var created = mgr.CreateRoom("c-a", "转让房", null, GameIds.Werewolf);
        Check.True(created.ok, "转让：建房成功");
        var roomId = created.roomId;

        Check.True(mgr.JoinRoom(roomId, "c-b", null).ok, "转让：bravo 加入");
        Check.True(mgr.JoinRoom(roomId, "c-c", null).ok, "转让：charlie 加入");
        Check.Equal("alpha", mgr.Find(roomId)!.HostUsername, "转让：初始房主为最早加入者");

        // 房主离开 → 转让给「第二个进房间的人」。
        var leave1 = mgr.LeaveRoom("c-a");
        Check.False(leave1.roomClosed, "转让：房间内还有人，不关闭");
        Check.Equal("bravo", leave1.newHost, "转让：房主离开后由第二个加入者接手");
        Check.Equal("bravo", mgr.Find(roomId)!.HostUsername, "转让：房间摘要里的房主已更新");
        Check.Equal(2, mgr.Find(roomId)!.PlayerCount, "转让：离开者已移出名单");

        // 再离开 → 传给第三位（以此类推）。
        var leave2 = mgr.LeaveRoom("c-b");
        Check.Equal("charlie", leave2.newHost, "转让：按加入顺序依次转让");

        // 最后一人离开且不保留 → 关闭房间。
        var leave3 = mgr.LeaveRoom("c-c");
        Check.True(leave3.roomClosed, "转让：最后一人离开且不保留 → 关闭房间");
        Check.True(mgr.Find(roomId) == null, "转让：关闭后房间不在列表中");

        // 对局中房主掉线：仍保留在名单，但房主同样要转让。
        var mgr2 = NewManager();
        foreach (var (conn, user) in new[]
                 { ("h", "host"), ("p1", "p1"), ("p2", "p2"), ("p3", "p3") })
            mgr2.BindIdentity(conn, user);

        var r2 = mgr2.CreateRoom("h", "对局房", null, GameIds.Werewolf);
        mgr2.JoinRoom(r2.roomId, "p1", null);
        mgr2.JoinRoom(r2.roomId, "p2", null);
        mgr2.JoinRoom(r2.roomId, "p3", null);
        var room2 = mgr2.Find(r2.roomId)!;
        Check.True(room2.StartGame("h").ok, "转让：对局开始");
        Check.Equal(RoomStatus.Running, room2.ToInfo().Status, "转让：对局中状态为进行中");

        var dc = mgr2.LeaveByConnection("h");
        Check.Equal("p1", dc.newHost, "转让：对局中房主掉线也转让给下一位");
        Check.True(room2.GetPlayerByName("host") != null, "转让：对局中掉线者仍留在名单");
        Check.Equal(4, room2.PlayerCount, "转让：对局中掉线不移出名单");
        Check.Equal("p1", room2.HostUsername, "转让：对局中房主已更新");
    }

    // ==================== 房间保留 / 关闭 ====================

    /// <summary>
    /// 房主退出且房间无人时的两种结果：保留（永不被回收）或关闭（立即从列表移除）。
    /// </summary>
    private static void KeepRoomTests()
    {
        // ① 单人房主选择「保留」→ 房间仍在，回收扫描也动不了它。
        var mgr = NewManager(graceSeconds: 0);
        mgr.BindIdentity("h", "host");
        var created = mgr.CreateRoom("h", "保留房", "pw", GameIds.Werewolf);
        var roomId = created.roomId;

        var keep = mgr.LeaveRoom("h", keepRoom: true);
        Check.False(keep.roomClosed, "保留：房主选择保留 → 房间不关闭");
        Check.True(mgr.Find(roomId) != null, "保留：房间仍在列表中");
        Check.True(mgr.Find(roomId)!.Persistent, "保留：房间被标记为保留");
        Check.Equal(0, mgr.Find(roomId)!.PlayerCount, "保留：房间已无成员");
        Check.Equal(1, mgr.List().Count(r => r.Id == roomId), "保留：保留房仍出现在房间列表中（可被他人加入）");

        Thread.Sleep(20);
        Check.Equal(0, mgr.RecycleExpired(), "保留：宽限期(0s)已过也不回收");
        Check.True(mgr.Find(roomId) != null, "保留：回收扫描后房间仍在");

        // ② 保留的房间仍可被他人凭房间号 + 密码加入，且新人自动成为房主。
        mgr.BindIdentity("n", "newbie");
        var rejoin = mgr.JoinRoom(roomId, "n", "pw");
        Check.True(rejoin.ok, "保留：他人可凭房间号 + 密码加入保留房间");
        Check.False(rejoin.reconnected, "保留：新玩家不是重连");
        Check.Equal("newbie", mgr.Find(roomId)!.HostUsername, "保留：加入者自动成为新房主");

        // ③ 单人房主选择「不保留」→ 立即关闭。
        var mgr2 = NewManager();
        mgr2.BindIdentity("h2", "host2");
        var created2 = mgr2.CreateRoom("h2", "临时房", null, GameIds.Werewolf);
        var closed = mgr2.LeaveRoom("h2", keepRoom: false);
        Check.True(closed.roomClosed, "关闭：房主不保留 → 房间被关闭");
        Check.True(mgr2.Find(created2.roomId) == null, "关闭：房间已从列表移除");
        Check.Equal(0, mgr2.List().Count, "关闭：列表为空");

        // ④ 房间内还有别人时，keepRoom 不生效（必然保留，只是转让房主）。
        var mgr3 = NewManager();
        mgr3.BindIdentity("h3", "host3");
        mgr3.BindIdentity("g3", "guest3");
        var created3 = mgr3.CreateRoom("h3", "有人房", null, GameIds.Werewolf);
        mgr3.JoinRoom(created3.roomId, "g3", null);

        var leaveWithKeep = mgr3.LeaveRoom("h3", keepRoom: true);
        Check.False(leaveWithKeep.roomClosed, "保留：房间里还有人时不关闭");
        Check.Equal("guest3", leaveWithKeep.newHost, "保留：房主转让给剩下的人");
        Check.Equal(1, mgr3.Find(created3.roomId)!.PlayerCount, "保留：房间内仍有 1 人");
    }

    // ==================== 邀请码 ====================

    /// <summary>邀请码：加密封装「服务器地址 + 房间号 + 密码」，且不泄露明文。</summary>
    private static void InviteCodeTests()
    {
        const string url = "http://192.168.1.8:5000/gamehub";

        var code = InviteCode.Create(url, "ABCDE", "s3cret", "新手房");
        Check.True(code.StartsWith(InviteCode.Prefix, StringComparison.Ordinal), "邀请码：带 WW1: 前缀");
        Check.False(code.Contains("s3cret", StringComparison.Ordinal), "邀请码：不含房间密码明文");
        Check.False(code.Contains("ABCDE", StringComparison.Ordinal), "邀请码：不含房间号明文");
        Check.False(code.Contains("192.168", StringComparison.Ordinal), "邀请码：不含服务器地址明文");
        Check.True(InviteCode.LooksLikeCode(code), "邀请码：LooksLikeCode 可识别");
        Check.False(InviteCode.LooksLikeCode("http://localhost:5000/gamehub"), "邀请码：普通地址不被误判");

        Check.True(InviteCode.TryParse(code, out var parsed, out var error), "邀请码：可解析（" + error + "）");
        Check.Equal(url, parsed!.Url, "邀请码：服务器地址还原");
        Check.Equal("ABCDE", parsed.RoomId, "邀请码：房间号还原");
        Check.Equal("s3cret", parsed.Password, "邀请码：房间密码还原");
        Check.Equal("新手房", parsed.RoomName, "邀请码：房间名还原");
        Check.True(parsed.HasPassword, "邀请码：标记含密码");

        // 随机 nonce：同一房间两次生成密文不同，但都能解出同样内容。
        var code2 = InviteCode.Create(url, "ABCDE", "s3cret", "新手房");
        Check.NotEqual(code, code2, "邀请码：两次生成密文不同");
        Check.True(InviteCode.TryParse(code2, out var parsed2, out _) && parsed2!.RoomId == "ABCDE",
            "邀请码：同一房间的多个码都可解析");

        // 公开房间：不带密码。
        var openCode = InviteCode.Create(url, "FGHJK", null, null);
        Check.True(InviteCode.TryParse(openCode, out var open, out _), "邀请码：公开房间码可解析");
        Check.False(open!.HasPassword, "邀请码：公开房间不带密码");
        Check.True(open.Password == null, "邀请码：公开房间密码为 null");
        Check.True(open.RoomName == null, "邀请码：未提供房间名时为 null");

        // 篡改与非法输入。
        var tampered = code[..^2] + (code[^1] == 'A' ? "BB" : "AA");
        Check.False(InviteCode.TryParse(tampered, out _, out var tamperError), "邀请码：被篡改后解析失败");
        Check.True(tamperError.Length > 0, "邀请码：篡改时给出失败原因");

        Check.False(InviteCode.TryParse("", out _, out _), "邀请码：空串解析失败");
        Check.False(InviteCode.TryParse("   ", out _, out _), "邀请码：空白串解析失败");
        Check.False(InviteCode.TryParse("WW1:", out _, out _), "邀请码：只有前缀解析失败");
        Check.False(InviteCode.TryParse("WW1:!!!!", out _, out _), "邀请码：非法字符解析失败");
        Check.False(InviteCode.TryParse("HELLO:" + code[4..], out _, out _), "邀请码：前缀不符解析失败");
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

        var leave = new LeaveRoomPayload { KeepRoom = true };
        var leaveBack = JsonSerializer.Deserialize<LeaveRoomPayload>(JsonSerializer.Serialize(leave), Json);
        Check.True(leaveBack!.KeepRoom, "协议：LeaveRoomPayload.KeepRoom 往返");
        Check.False(JsonSerializer.Deserialize<LeaveRoomPayload>("{}", Json)!.KeepRoom,
            "协议：LeaveRoomPayload 默认不保留");
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 构造一个可独立测试的注册表（依赖用最简对象，不产生后台线程）。
    /// 房间人数上限来自该游戏模块的元数据，因此这里注册一个「4 人上限」的狼人杀测试模块。
    /// </summary>
    private static RoomManager NewManager(int maxRooms = 100, int graceSeconds = 60)
    {
        var channel = new EncryptedChannel(null!, new CryptoSessionStore());
        var (catalog, _) = TestGames.WerewolfOnly(minPlayers: 4, maxPlayers: 4);
        var options = new RoomOptions
        {
            MaxRooms = maxRooms,
            EmptyGraceSeconds = graceSeconds,
            RecycleScanSeconds = 1
        };
        return new RoomManager(channel, catalog, options);
    }
}
