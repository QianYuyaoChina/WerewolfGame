using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// QA（严过关）**独立编写**的协议 / 边界探针（<c>--protocol &lt;url&gt;</c>），针对真实服务端。
/// 覆盖：gamelist 元数据、createroom 无 GameId 回退、未知 GameId 拒绝、roomlist 元数据一致性、
/// 玩家视角隔离（多客户端）、规则随状态下发、夜间按 ActingRole 顺序提交、等待中不泄露身份。
/// </summary>
public static class ProtocolBoundaryProbe
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static int _pass;
    private static readonly List<string> _fail = new();
    private static void Ok(string w) { _pass++; Console.WriteLine("   ✅ " + w); }
    private static void Bad(string w) { _fail.Add(w); Console.WriteLine("   ❌ " + w); }
    private static void Eq<T>(T e, T a, string w)
    {
        if (EqualityComparer<T>.Default.Equals(e, a)) Ok(w);
        else Bad($"{w}（期望 <{e}>，实际 <{a}>）");
    }
    private static void True(bool v, string w) { if (v) Ok(w); else Bad(w); }

    private sealed class C
    {
        public required string Name { get; init; }
        public GameClient Client { get; } = new();
    }

    public static async Task<int> RunAsync(string url)
    {
        _pass = 0; _fail.Clear();
        var all = new List<C>();

        async Task<C> Login(string name)
        {
            var c = new C { Name = name };
            all.Add(c);
            await c.Client.ConnectAsync(url);
            var r = await c.Client.LoginAsync(name, "pw" + name);
            if (!r.ok) Bad($"{name} 登录失败：{r.msg}");
            return c;
        }

        async Task<bool> WaitUntil(Func<Task<bool>> cond, int ms)
        {
            var dl = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < dl) { if (await cond()) return true; await Task.Delay(100); }
            return await cond();
        }

        try
        {
            var a = await Login("qa_a");
            var b = await Login("qa_b");
            var c = await Login("qa_c");
            var d = await Login("qa_d");

            // ================= 10) gamelist 元数据 =================
            var games = await a.Client.ListGamesAsync();
            var wolf = games.Games.FirstOrDefault(g => g.GameId == GameIds.Werewolf);
            True(games.Ok && wolf != null, "S10 gamelist 可用且包含 werewolf");
            if (wolf != null)
            {
                Eq("werewolf", wolf.GameId, "S10 GameId=werewolf");
                True(wolf.DisplayName is { Length: > 0 } dn && dn.Contains("狼人"), "S10 中文显示名非空且含「狼人」");
                True(!string.IsNullOrWhiteSpace(wolf.Emoji), "S10 有 emoji");
                Eq(4, wolf.MinPlayers, "S10 人数下限=4");
                Eq(12, wolf.MaxPlayers, "S10 人数上限=12");
                True(!string.IsNullOrWhiteSpace(wolf.Tagline), "S10 简介非空");
            }

            // ================= 8) createroom 不带 GameId → 回退 werewolf =================
            var noGame = await a.Client.CreateRoomAsync("无GameId房", null);   // gameId 省略
            True(noGame.Ok && noGame.RoomId.Length > 0, "S8 createroom 不带 GameId 成功建房");
            await Task.Delay(400);
            var noGameState = await a.Client.RefreshStateAsync();
            Eq(GameIds.Werewolf, noGameState?.GameId, "S8 回退房间确实绑定 werewolf");

            var listAfter = await a.Client.ListRoomsAsync();
            var noGameInfo = listAfter.Rooms.FirstOrDefault(r => r.Id == noGame.RoomId);
            Eq(GameIds.Werewolf, noGameInfo?.GameId, "S8 房间列表也显示 GameId=werewolf");

            // ================= 9) createroom 未知 GameId → 拒绝 =================
            var bad = await a.Client.CreateRoomAsync("坏房", null, "nope");
            True(!bad.Ok, "S9 未知 GameId 建房被拒");
            Eq(ServerMessages.GameUnavailable, bad.Message, "S9 文案为「该游戏不可用」");

            // ================= 11) roomlist 元数据与 gamelist 一致 =================
            var roomsNow = await a.Client.ListRoomsAsync();
            var meta = games.Games.ToDictionary(g => g.GameId, g => g);
            var allConsistent = roomsNow.Rooms.Count > 0;
            foreach (var r in roomsNow.Rooms)
            {
                if (!meta.TryGetValue(r.GameId, out var g)) { allConsistent = false; Bad($"S11 房间 {r.Id} 的 GameId={r.GameId} 不在 gamelist"); continue; }
                if (r.GameName != g.DisplayName) { allConsistent = false; Bad($"S11 房间 {r.Id} GameName({r.GameName})≠gamelist({g.DisplayName})"); }
                if (r.GameEmoji != g.Emoji) { allConsistent = false; Bad($"S11 房间 {r.Id} GameEmoji≠gamelist"); }
            }
            True(allConsistent, "S11 每个房间的 GameId/GameName/GameEmoji 与 gamelist 一致");

            // ================= 专用房：4 人，用于隔离 / 规则 / 阶段 =================
            // a 已在 noGame 房；新建一局 4 人房会让 a 脱离旧房。
            var room = await a.Client.CreateRoomAsync("隔离测试房", null, GameIds.Werewolf);
            True(room.Ok, "S12 建立 4 人狼人杀房（房主 a）");
            await b.Client.JoinRoomAsync(room.RoomId, null);
            await c.Client.JoinRoomAsync(room.RoomId, null);
            await d.Client.JoinRoomAsync(room.RoomId, null);
            await Task.Delay(400);

            // -------- 17) 等待中不泄露身份（旧版行为：所有人显示「平民」） --------
            var lobby = await a.Client.RefreshStateAsync();
            var lobbyPayload = lobby?.GamePayload?.Deserialize<WerewolfGameState>(Json);
            Eq(RoomStatus.Waiting, lobby?.Status, "S17 开局前状态为 Waiting");
            True(lobby != null && lobby.Players.All(p => p.Tag is null or "平民"),
                "S17 等待中所有玩家标签均为「平民」或空（不泄露真实身份）");
            var realRoles = new[] { "狼人", "预言家", "女巫", "猎人", "守卫" };
            True(lobby != null && !lobby.Players.Any(p => p.Tag != null && realRoles.Contains(p.Tag)),
                "S17 等待中无任一玩家暴露真实身份");

            // -------- 开局 --------
            var start = await a.Client.StartGameAsync();
            True(start.ok, "S14 房主开局成功（4 人）");
            await WaitUntil(async () =>
            {
                var s = await a.Client.RefreshStateAsync();
                var p = s?.GamePayload?.Deserialize<WerewolfGameState>(Json);
                return p is { } && p.Phase == "Night";
            }, 8000);

            // -------- 12) 玩家视角隔离 + 14) 规则随状态下发 --------
            var viewers = new List<(C C, GameStateDto State, WerewolfGameState Payload)>();
            foreach (var v in new[] { a, b, c, d })
            {
                var st = await v.Client.RefreshStateAsync();
                var pl = st?.GamePayload?.Deserialize<WerewolfGameState>(Json);
                if (st == null || pl == null) { Bad($"S12 {v.Name} 取不到状态/负载"); continue; }
                viewers.Add((v, st, pl));
            }

            Eq(4, viewers.Count, "S12 四名玩家都能取到状态");
            foreach (var (vc, st, pl) in viewers)
            {
                var self = st.Players.FirstOrDefault(p => p.IsYou);
                Eq(pl.YourRole, self?.Tag, $"S12 {vc.Name} 自视角标签 == 自己 payload 的身份");
                Eq(4, st.Players.Count, $"S12 {vc.Name} 看到 4 名玩家");
                True(self?.TagColor is { Length: > 0 } tc && tc.StartsWith('#'),
                    $"S16 {vc.Name} 身份标签带十六进制颜色（TagColor）");
            }

            // 非狼人不能看到任何他人的身份；狼人只看得到狼队友。
            var leaked = new List<string>();
            foreach (var (vc, st, pl) in viewers)
            {
                var isWolf = pl.YourRole == "狼人";
                foreach (var o in st.Players.Where(p => !p.IsYou))
                {
                    var other = o.Tag;
                    if (other == null) continue;
                    if (!isWolf) leaked.Add($"{vc.Name}({pl.YourRole}) 看到他人 {o.Name}={other}");
                    else if (other != "狼人") leaked.Add($"{vc.Name}(狼) 看到 {o.Name}={other}");
                }
            }
            True(leaked.Count == 0, "S12 视角隔离：非狼人看不到任何他人身份，狼人只能看到狼队友");
            foreach (var l in leaked) Bad("S12 泄露：" + l);

            // 结构隔离：payload 字段集**固定**，不含「他人生份表」。
            // v2.1 新增的字段同样都是「本人视角或公开信息」：
            // RoleSummary=公开的身份配置、SheriffName=公开的警长、LoverName/IsLover=自己的情侣、
            // SheriffEnabled/YouTransferBadge=与本人相关的开关，均不泄露他人身份（上面的 Tag 检查已覆盖）。
            var expectedKeys = new[]
            {
                "Phase", "Day", "YourRole", "YourRoleDesc", "ActingRole", "Rules",
                "RoleSummary", "SheriffName", "IsLover", "LoverName", "SheriffEnabled", "YouTransferBadge"
            };
            var keysOk = viewers.All(v =>
            {
                var keys = (v.State.GamePayload as JsonObject)!.Select(kv => kv.Key).OrderBy(k => k).ToArray();
                return keys.SequenceEqual(expectedKeys.OrderBy(k => k).ToArray());
            });
            True(keysOk, "S12 payload 字段集固定（本人视角 + 公开信息），无他人生份字段");

            // -------- 14) 规则随状态下发（值来自 appsettings） --------
            var anyRules = viewers[0].Payload.Rules;
            True(anyRules != null, "S14 GamePayload 携带 Rules");
            if (anyRules != null)
            {
                Eq(40, anyRules.NightActionSeconds, "S14 规则：NightActionSeconds=40（appsettings）");
                Eq(45, anyRules.VoteSeconds, "S14 规则：VoteSeconds=45（appsettings）");
                Eq(45, anyRules.DiscussionSeconds, "S14 规则：DiscussionSeconds=45（appsettings）");
                Eq(30, anyRules.HunterShotSeconds, "S14 规则：HunterShotSeconds=30（appsettings）");
                Eq(4, anyRules.MinPlayers, "S14 规则：MinPlayers=4");
                Eq(12, anyRules.MaxPlayers, "S14 规则：MaxPlayers=12");
                True(anyRules.NightOrder.Count > 0, "S14 规则：夜晚顺序非空");
            }

            // -------- 14) 夜间行动按 ActingRole 顺序提交 --------
            var nightViewer = viewers.FirstOrDefault(v => v.Payload.Phase == "Night");
            if (nightViewer.Payload.Phase != "Night")
            {
                Console.WriteLine("   ⚠️ S14 刷新时已非夜晚阶段，跳过 ActingRole 顺序断言");
            }
            else
            {
                var acting = nightViewer.Payload.ActingRole;
                True(!string.IsNullOrEmpty(acting), "S14 夜间下发了非空的 ActingRole");
                if (!string.IsNullOrEmpty(acting))
                {
                    var nonActor = viewers.FirstOrDefault(v => v.Payload.YourRole != acting && v.State.IsAlive);
                    if (nonActor.State != null)
                    {
                        var r = await nonActor.C.Client.CallGameOpAsync(WerewolfOps.Night,
                            JsonSerializer.SerializeToNode(new NightPayload { TargetId = -1, Action = "none" }));
                        True(!r.ok && r.msg.Contains("还没轮到你"), "S14 非当前行动角色的夜间提交被拒（还没轮到你行动）");
                    }
                    var actor = viewers.FirstOrDefault(v => v.Payload.YourRole == acting && v.State.IsAlive);
                    if (actor.State != null)
                    {
                        var r = await actor.C.Client.CallGameOpAsync(WerewolfOps.Night,
                            JsonSerializer.SerializeToNode(new NightPayload { TargetId = -1, Action = "none" }));
                        True(r.ok, "S14 当前行动角色的夜间提交被接受");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Bad("探针异常：" + ex);
        }
        finally
        {
            foreach (var c in all) { try { await c.Client.DisposeAsync(); } catch { } }
        }

        Console.WriteLine();
        Console.WriteLine(_fail.Count == 0
            ? $"✅ ProtocolBoundaryProbe 通过：{_pass} 项断言"
            : $"❌ ProtocolBoundaryProbe 失败 {_fail.Count} 项，通过 {_pass} 项");
        foreach (var f in _fail) Console.WriteLine("   · " + f);
        return _fail.Count == 0 ? 0 : 1;
    }
}
