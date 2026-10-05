using System.Text.Json;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games.Werewolf;

namespace ClientGUI.Tests;

/// <summary>
/// QA（严过关）独立编写的多房间端到端探针（<c>--rooms &lt;url&gt;</c>）。
/// 不采信工程师自测结论，用自设计场景证明「多房间并行 / 隔离 / 密码 / 人数边界 / 错误路径」确实可用。
/// 期望服务端为默认配置（MinPlayers=4、MaxPlayers=12）。
/// </summary>
public static class MultiRoomProbe
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class C
    {
        public required string Name { get; init; }
        public GameClient Client { get; } = new();
        public GameStateDto? State { get; set; }
        public List<string> Messages { get; } = new();
        public readonly object Gate = new();
    }

    private static int _pass;
    private static readonly List<string> _fail = new();
    private static readonly List<string> _notes = new();
    private static readonly List<string> _warn = new();

    private static void Ok(string what) { _pass++; _notes.Add("✅ " + what); }
    private static void Bad(string what) { _fail.Add(what); _notes.Add("❌ " + what); }
    private static void Warn(string what) { _warn.Add(what); _notes.Add("⚠️ " + what); }
    private static void Eq<T>(T exp, T act, string what)
    {
        if (EqualityComparer<T>.Default.Equals(exp, act)) Ok(what);
        else Bad($"{what}（期望 {exp}，实际 {act}）");
    }
    private static void True(bool v, string what) { if (v) Ok(what); else Bad(what); }

    public static async Task<int> RunAsync(string url)
    {
        _pass = 0; _fail.Clear(); _notes.Clear(); _warn.Clear();

        HeadlessChecks();

        var clients = new Dictionary<string, C>(StringComparer.Ordinal);

        C Get(string name)
        {
            if (!clients.TryGetValue(name, out var c))
            {
                c = new C { Name = name };
                c.Client.StateReceived += s => { lock (c.Gate) c.State = s; };
                c.Client.MessageReceived += m => { lock (c.Gate) c.Messages.Add(m); };
                clients[name] = c;
            }
            return c;
        }
        async Task<C> Login(string name)
        {
            var c = Get(name);
            await c.Client.ConnectAsync(url);
            var r = await c.Client.LoginAsync(name, "pw" + name);
            if (!r.ok) Bad($"{name} 登录失败：{r.msg}");
            return c;
        }
        List<string> MsgsOf(C c) { lock (c.Gate) return c.Messages.ToList(); }
        GameStateDto? StateOf(C c) { lock (c.Gate) return c.State; }

        try
        {
            // ================= 登录 =================
            var roomA = new[] { "alice", "bob", "carol", "dave" };   // 加密房
            var roomB = new[] { "eve", "frank", "gina", "henry" };   // 公开房
            var pwUsers = new[] { "paula", "paulb", "paulc", "pauld" };
            var pubUsers = new[] { "pubh", "pubg" };
            var small = new[] { "s00", "s01" };
            var lvUsers = new[] { "lv0", "lv1", "lv2" };
            var errUsers = new[] { "nobody", "dup1", "dup2", "nmer" };
            var fullUsers = Enumerable.Range(0, 12).Select(i => "f" + i.ToString("00")).ToArray();

            foreach (var n in roomA.Concat(roomB).Concat(pwUsers).Concat(pubUsers)
                         .Concat(small).Concat(lvUsers).Concat(errUsers).Concat(fullUsers))
                await Login(n);
            Ok($"{roomA.Length + roomB.Length + pwUsers.Length + pubUsers.Length + small.Length + lvUsers.Length + errUsers.Length + fullUsers.Length} 名客户端登录");

            // 未登录客户端（仅握手，不 join）
            var anon = Get("anon");
            await anon.Client.ConnectAsync(url);

            // ================= B5：建房 → 列表立即可见 / 字段正确 =================
            var a = await Get("alice").Client.CreateRoomAsync("新手房A", "A123");
            Eq(true, a.Ok, "B5 alice 创建加密房成功");
            if (!a.Ok) throw new InvalidOperationException("无法创建房间 A：" + a.Message);
            var aid = a.RoomId;

            // 立即拉列表（不等任何间隔）
            var immediate = await Get("alice").Client.ListRoomsAsync();
            True(immediate.Rooms.Any(r => r.Id == aid), "B5 新建房间在列表中立即可见");

            foreach (var n in new[] { "bob", "carol", "dave" })
                await Get(n).Client.JoinRoomAsync(aid, "A123");

            var b = await Get("eve").Client.CreateRoomAsync("公开房B", null);
            Eq(true, b.Ok, "B5 eve 创建公开房成功");
            var bid = b.RoomId;
            foreach (var n in new[] { "frank", "gina", "henry" })
                await Get(n).Client.JoinRoomAsync(bid, null);

            var list = await Get("alice").Client.ListRoomsAsync();
            var ra = list.Rooms.FirstOrDefault(r => r.Id == aid);
            var rb = list.Rooms.FirstOrDefault(r => r.Id == bid);
            True(ra != null && rb != null, "B5 列表同时包含两个房间");
            if (ra != null && rb != null)
            {
                Eq(4, ra.PlayerCount, "B5 房间A 人数=4");
                Eq(12, ra.MaxPlayers, "B5 房间A 上限=12（默认）");
                Eq("Waiting", ra.Status, "B5 房间A 状态=Waiting");
                Eq(true, ra.HasPassword, "B5 房间A 加密标记=true");
                Eq("alice", ra.Host, "B5 房间A 房主=alice");
                Eq(4, rb.PlayerCount, "B5 房间B 人数=4");
                Eq(false, rb.HasPassword, "B5 房间B 加密标记=false");
                Eq("eve", rb.Host, "B5 房间B 房主=eve");
            }

            // ================= C8：列表不泄露密码（明文/哈希） =================
            var raw = JsonSerializer.Serialize(list, Json);
            True(!raw.Contains("A123", StringComparison.Ordinal), "C8 列表 JSON 不含房间密码明文(A123)");
            True(!raw.Contains("v2:", StringComparison.Ordinal), "C8 列表 JSON 不含密码哈希前缀(v2:)");

            // ================= C6/C7：密码门禁 =================
            var sec = await Get("paula").Client.CreateRoomAsync("sec", "secpw9");
            var sid = sec.RoomId;
            var wrong = await Get("paulb").Client.JoinRoomAsync(sid, "nopenope");
            Eq(ServerMessages.WrongPassword, wrong.Message, "C6 加密房错误密码返回「密码错误」");
            var empty = await Get("paulc").Client.JoinRoomAsync(sid, "");
            Eq(ServerMessages.WrongPassword, empty.Message, "C6 加密房空密码被拒（密码错误）");
            var nullPw = await Get("pauld").Client.JoinRoomAsync(sid, null);
            Eq(ServerMessages.WrongPassword, nullPw.Message, "C6 加密房 null 密码被拒（密码错误）");
            var right = await Get("pauld").Client.JoinRoomAsync(sid, "secpw9");
            Eq(true, right.Ok, "C6 加密房正确密码加入成功");

            var pub = await Get("pubh").Client.CreateRoomAsync("pubroom", null);
            var pid = pub.RoomId;
            var pubJoin = await Get("pubg").Client.JoinRoomAsync(pid, "任意密码XYZ");
            Eq(true, pubJoin.Ok, "C7 无密码房传入任意密码仍可直接加入");

            // ================= E14：未登录 =================
            var anonCreate = await anon.Client.CreateRoomAsync("x", null);
            Eq(ServerMessages.NotLoggedIn, anonCreate.Message, "E14 未登录 createroom → 尚未登录，请先登录");
            var anonJoin = await anon.Client.JoinRoomAsync("ABCDE", null);
            Eq(ServerMessages.NotLoggedIn, anonJoin.Message, "E14 未登录 joinroom → 尚未登录，请先登录");
            var anonList = await anon.Client.ListRoomsAsync();
            if (!anonList.Ok && anonList.Msg == ServerMessages.NotLoggedIn)
                Ok("E14 未登录 roomlist → 尚未登录，请先登录");
            else
                Warn($"E14 未登录 roomlist 未被拒绝（Ok={anonList.Ok}, 返回房间数={anonList.Rooms.Count}）——符合设计 §3.1（roomlist 无失败），但与验收清单不符，供交付总监裁定");

            // ================= E15：未进房 =================
            var nb = Get("nobody");
            var s1 = await nb.Client.StartGameAsync();
            Eq(ServerMessages.NotInRoom, s1.msg, "E15 未进房 start → 你尚未加入任何房间");
            // GameClient.ChatAsync 返回 void（丢弃 RpcReply）→ 无法读取错误文案；
            // 改为验证「未进房发言不产生任何广播」以证明服务端确实拒绝了。
            await nb.Client.ChatAsync("E15CHAT");
            await Task.Delay(400);
            True(!MsgsOf(Get("alice")).Any(m => m.Contains("E15CHAT")),
                "E15 未进房 chat 未生效（服务端拒绝、同房玩家未收到）");

            // ================= E16：房间不存在 =================
            var none = await nb.Client.JoinRoomAsync("ZZZZZ", null);
            Eq(ServerMessages.RoomNotFound, none.Message, "E16 加入不存在房间 → 房间不存在或已关闭");

            // ================= E17：非房主 start =================
            var guestStart = await Get("bob").Client.StartGameAsync();
            Eq(ServerMessages.OnlyHostCanStart, guestStart.msg, "E17 非房主 start → 只有房主可以开始游戏");

            // ================= E18：同账号多连接 =================
            var dup = await Get("dup1").Client.CreateRoomAsync("dup", null);
            var did = dup.RoomId;
            var dup2 = await Get("dup2").Client.LoginAsync("dup1", "pwdup1");
            Eq(true, dup2.ok, "E18 dup2 以 dup1 账号登录成功");
            var dupJoin = await Get("dup2").Client.JoinRoomAsync(did, null);
            Eq(ServerMessages.AlreadyInRoom, dupJoin.Message, "E18 同账号再进同一房间 → 该账号已在房间中");
            var dupOther = await Get("dup2").Client.JoinRoomAsync(pid, null);
            Eq(true, dupOther.Ok, "E18 同账号进入不同房间 → 允许");

            // ================= 房间隔离（B4） =================
            await Get("alice").Client.ChatAsync("A房专属消息-A1");
            await Get("eve").Client.ChatAsync("B房专属消息-B1");
            await Task.Delay(800);
            True(MsgsOf(Get("dave")).Any(m => m.Contains("A房专属消息-A1")), "B4 A 房消息送达同房玩家 dave");
            True(!MsgsOf(Get("eve")).Any(m => m.Contains("A房专属消息-A1")), "B4 A 房消息未泄漏到 B 房");
            True(MsgsOf(Get("frank")).Any(m => m.Contains("B房专属消息-B1")), "B4 B 房消息送达同房玩家 frank");
            True(!MsgsOf(Get("alice")).Any(m => m.Contains("B房专属消息-B1")), "B4 B 房消息未泄漏到 A 房");

            // ================= D12：人数不足 =================
            var smallRoom = await Get("s00").Client.CreateRoomAsync("smallroom", null);
            var smid = smallRoom.RoomId;
            await Get("s01").Client.JoinRoomAsync(smid, null);
            var tooFew = await Get("s00").Client.StartGameAsync();
            Eq(false, tooFew.ok, "D12 人数不足(2<4)时房主 start 被拒");

            // ================= D10/独立推进：两个房间同时开局 =================
            var startA = await Get("alice").Client.StartGameAsync();
            Eq(true, startA.ok, "B4 房主 alice 在房间A 开局成功");

            // A 房开局后：B 房仍应保持等待（独立性）
            await Task.Delay(1200);
            var bStateEarly = StateOf(Get("eve"));
            True(Wf(bStateEarly)?.Phase == Phases.Waiting, "B4 A 房开局后 B 房保持 Waiting（互不影响）");
            True(!MsgsOf(Get("eve")).Any(m => m.Contains("你的身份是")), "B4 B 房玩家未收到 A 房身份私聊");

            var startB = await Get("eve").Client.StartGameAsync();
            Eq(true, startB.ok, "B4 房主 eve 在房间B 开局成功");

            // A 房 4 人、B 房 4 人各自收到身份
            await Task.Delay(1500);
            var gotA = roomA.Count(n => MsgsOf(Get(n)).Any(m => m.Contains("你的身份是")));
            var gotB = roomB.Count(n => MsgsOf(Get(n)).Any(m => m.Contains("你的身份是")));
            Eq(4, gotA, "B4 房间A 4/4 收到身份私聊");
            Eq(4, gotB, "B4 房间B 4/4 收到身份私聊");

            // 两房状态各自推进（Running）
            var aSt = StateOf(Get("alice"));
            var bSt = StateOf(Get("eve"));
            True(Wf(aSt)?.Phase != Phases.Waiting && aSt != null && aSt.RoomId == aid, "B4 房间A 独立推进（非 Waiting，房间号正确）");
            True(Wf(bSt)?.Phase != Phases.Waiting && bSt != null && bSt.RoomId == bid, "B4 房间B 独立推进（非 Waiting，房间号正确）");

            var list2 = await Get("alice").Client.ListRoomsAsync();
            var ra2 = list2.Rooms.FirstOrDefault(r => r.Id == aid);
            var rb2 = list2.Rooms.FirstOrDefault(r => r.Id == bid);
            Eq("Running", ra2?.Status ?? "?", "B5 开局后房间A 列表状态=Running");
            Eq("Running", rb2?.Status ?? "?", "B5 开局后房间B 列表状态=Running");

            // ================= D13：进行中拒绝加入 =================
            var inProg = await nb.Client.JoinRoomAsync(aid, "A123");
            Eq(ServerMessages.GameInProgress, inProg.Message, "D13 房间开局后新玩家加入 → 游戏进行中，无法加入");

            // ================= D12：满员（12） =================
            var full = await Get("f00").Client.CreateRoomAsync("fullroom", null);
            var fid = full.RoomId;
            for (var i = 1; i < 12; i++)
                await Get("f" + i.ToString("00")).Client.JoinRoomAsync(fid, null);
            var flist = await Get("f00").Client.ListRoomsAsync();
            var fr = flist.Rooms.FirstOrDefault(r => r.Id == fid);
            Eq(12, fr?.PlayerCount ?? -1, "D12 满员房人数=12");
            // 第 13 人
            await LoginExtra(clients, "f99", url);
            var fullJoin = await Get("f99").Client.JoinRoomAsync(fid, null);
            Eq(ServerMessages.RoomFull(12), fullJoin.Message, "D12 满员后再加入 → 房间已满（最多 12 人）");

            // ================= E20：房间名边界 =================
            var nmEmpty = await Get("nmer").Client.CreateRoomAsync("", null);
            Eq("nmer 的房间", Get("nmer").Client.CurrentRoomName, "E20 空房间名 → 回退「<用户名> 的房间」");
            var longName = new string('X', 60);
            await Get("nmer").Client.CreateRoomAsync(longName, null);
            Eq(40, Get("nmer").Client.CurrentRoomName.Length, "E20 60 字房间名 → 截断到 40 字");
            Eq(longName[..40], Get("nmer").Client.CurrentRoomName, "E20 截断内容为前 40 字");

            // ================= F22：leaveroom 人数减少 =================
            var lv = await Get("lv0").Client.CreateRoomAsync("leaveroom", null);
            var lid = lv.RoomId;
            await Get("lv1").Client.JoinRoomAsync(lid, null);
            await Get("lv2").Client.JoinRoomAsync(lid, null);
            var before = (await Get("lv0").Client.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == lid)?.PlayerCount ?? -1;
            var left = await Get("lv1").Client.LeaveRoomAsync();
            Eq(true, left.ok, "F22 leaveroom 成功");
            True(!Get("lv1").Client.IsJoined && Get("lv1").Client.IsConnected, "F22 退出房间后连接保留、已不在房间");
            await Task.Delay(500);
            var after = (await Get("lv0").Client.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == lid)?.PlayerCount ?? -1;
            Eq(before - 1, after, "F22 退出房间后列表人数减少 1");
        }
        catch (Exception ex)
        {
            Bad("探针异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in clients.Values)
                try { await c.Client.DisposeAsync(); } catch { }
        }

        foreach (var n in _notes) Console.WriteLine("   " + n);
        Console.WriteLine();
        Console.WriteLine(_fail.Count == 0
            ? $"✅ MultiRoomProbe 通过：{_pass} 项断言"
            : $"❌ MultiRoomProbe 失败 {_fail.Count} 项，通过 {_pass} 项");
        foreach (var f in _fail) Console.WriteLine("   · " + f);
        foreach (var w in _warn) Console.WriteLine("   ⚠️ " + w);
        return _fail.Count == 0 ? 0 : 1;
    }

    private static async Task LoginExtra(Dictionary<string, C> clients, string name, string url)
    {
        var c = new C { Name = name };
        c.Client.StateReceived += s => { lock (c.Gate) c.State = s; };
        c.Client.MessageReceived += m => { lock (c.Gate) c.Messages.Add(m); };
        clients[name] = c;
        await c.Client.ConnectAsync(url);
        await c.Client.LoginAsync(name, "pw" + name);
    }

    // ==================== 纯逻辑（无需服务器） ====================

    /// <summary>从通用状态里解析狼人杀私有负载（阶段字段已迁入 GamePayload）。</summary>
    private static WerewolfGameState? Wf(GameStateDto? s)
        => s?.GamePayload?.Deserialize<WerewolfGameState>(Json);

    private static void HeadlessChecks()
    {
        // C9：带房间号盐的单向哈希 —— 同密码不同房间号存储值不同
        var h1 = RoomPassword.Hash("ABCDE", "secret");
        var h2 = RoomPassword.Hash("FGHJK", "secret");
        True(h1.StartsWith("v2:", StringComparison.Ordinal), "C9 房间密码存储值带 v2: 前缀");
        True(h1 != h2, "C9 同密码不同房间号哈希不同（加盐生效）");
        True(!h1.Contains("secret", StringComparison.Ordinal), "C9 存储值不含明文");

        // MaxRooms（纯逻辑，独立于服务器配置）
        var channel = new EncryptedChannel(null!, new CryptoSessionStore());
        var (catalog, _) = TestGames.WerewolfOnly(minPlayers: 4, maxPlayers: 12);
        var mgr = new RoomManager(channel, catalog, new RoomOptions { MaxRooms = 2, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 });
        mgr.BindIdentity("c1", "u1"); mgr.BindIdentity("c2", "u2"); mgr.BindIdentity("c3", "u3");
        var r1 = mgr.CreateRoom("c1", "r1", null, GameIds.Werewolf);
        var r2 = mgr.CreateRoom("c2", "r2", null, GameIds.Werewolf);
        var r3 = mgr.CreateRoom("c3", "r3", null, GameIds.Werewolf);
        Eq(true, r1.ok && r2.ok, "E19 上限=2 时前 2 个房间可创建");
        Eq(false, r3.ok, "E19 上限=2 时第 3 个房间被拒");
        Eq(ServerMessages.TooManyRooms, r3.msg, "E19 超上限返回「房间数已达上限，请稍后再试」");

        // D10：开局会重置死亡 / 药水状态（headless 经狼人杀实例断言）
        var chan2 = new EncryptedChannel(null!, new CryptoSessionStore());
        var (_, wfModule) = TestGames.WerewolfOnly(minPlayers: 3, maxPlayers: 3);
        // 给房间注入「3 人小局」规则（人数上下限由本游戏规则决定，与生产 appsettings 同路径）。
        var smallRules = new GameRules
        {
            MinPlayers = 3,
            MaxPlayers = 3,
            NightActionSeconds = 1,
            DiscussionSeconds = 1,
            VoteSeconds = 1,
            HunterShotSeconds = 1
        };
        var room = new GameRoom("HD001", "headless", null, chan2, GameIds.Werewolf,
            wfModule.CreateInstance, new TestGameConfig(smallRules));
        room.AddOrReconnect("h", "hc");
        room.AddOrReconnect("a", "ac");
        room.AddOrReconnect("b", "bc");

        var victim = room.GetPlayer("ac")!;   // 连接号 → 玩家（用户名 "a"）
        victim.IsAlive = false;

        // 预热：触发 SnapshotPlayers 建立狼人私有点位，再把药水标记为“已用”，模拟上一局的残留。
        var instance = wfModule.LastInstance!;
        instance.DecoratePlayers(new List<PlayerDto>(), "h");
        var victimWp = GetWerewolfPlayer(instance, victim.Username)!;
        victimWp.HasAntidote = false;
        victimWp.HasPoison = false;

        var start = room.StartGame("hc");
        True(start.ok, "D10 房主开局成功");
        True(victim.IsAlive, "D10 开局重置：死亡状态被清空");
        True(victimWp.HasAntidote, "D10 开局重置：解药状态被清空");
        True(victimWp.HasPoison, "D10 开局重置：毒药状态被清空");
    }

    /// <summary>反射读取狼人杀实例的私有玩家状态（username → <see cref="WerewolfPlayer"/>）。</summary>
    private static WerewolfPlayer? GetWerewolfPlayer(WerewolfGameInstance instance, string username)
    {
        var field = typeof(WerewolfGameInstance).GetField("_wp",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(instance) is not Dictionary<string, WerewolfPlayer> wp) return null;
        return wp.TryGetValue(username, out var p) ? p : null;
    }
}
