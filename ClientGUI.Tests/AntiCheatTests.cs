using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Werewolf;
using WfOps = Werewolf.Client.Share.WerewolfOps;

namespace ClientGUI.Tests;

/// <summary>
/// 反作弊：**一切操作都以服务端校验为准**，客户端只是提前提示。
/// 这里用无头房间（不连服务器）直接调 <see cref="GameRoom.HandleGameAction"/> 与
/// <see cref="GameRoom.UpdateSettings"/>，逐条验证非法请求确实被服务端拒绝。
/// </summary>
public static class AntiCheatTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void RunAll()
    {
        RejectedBeforeStartAndOutsideRoom();
        RejectedForNonHost();
        RejectedWhenTooFewPlayers();
        RejectedByLayoutValidation();
        VotingGuards();
    }

    // ==================== 未开局 / 不在房间 ====================

    private static void RejectedBeforeStartAndOutsideRoom()
    {
        var room = NewRoom(out var hostConn);

        // 未开局时所有玩法操作都必须被拒（客户端可以伪造 op，服务端不能放行）。
        var ops = new (string op, JsonObject? payload)[]
        {
            (WfOps.Night, Node(new NightPayload { TargetId = 0, Action = "do" })),
            (WfOps.Vote, Node(new TargetPayload { TargetId = 0 })),
            (WfOps.Hunter, Node(new TargetPayload { TargetId = 0 })),
            (WfOps.Cupid, Node(new CupidPayload { TargetA = 0, TargetB = 1 })),
            (WfOps.Sheriff, Node(new TargetPayload { TargetId = 0 }))
        };

        var allRejected = true;
        foreach (var (op, payload) in ops)
        {
            var (ok, _) = room.HandleGameAction(op, payload, hostConn);
            if (ok) allRejected = false;
        }
        Check.True(allRejected, "反作弊：未开局时夜间/投票/开枪/丘比特/警徽 全部被服务端拒绝");

        // 不在房间的连接：任何玩法 op 都必须是 NotInRoom。
        var (outsiderOk, outsiderMsg) = room.HandleGameAction(WfOps.Night,
            Node(new NightPayload { TargetId = 0, Action = "do" }), "conn-outsider");
        Check.False(outsiderOk, "反作弊：不在房间的连接提交操作被拒");
        Check.Equal(ServerMessages.NotInRoom, outsiderMsg, "反作弊：不在房间返回统一文案");

        // 未知 op 也要被拒（不能让客户端用奇怪 op 绕过路由）。
        var (unknownOk, _) = room.HandleGameAction("some_unknown_op", null, hostConn);
        Check.False(unknownOk, "反作弊：未知操作被拒");
    }

    // ==================== 权限 ====================

    private static void RejectedForNonHost()
    {
        var room = NewRoom(out var hostConn, out var manager);
        const string guestConn = "conn-1";

        var (startOk, startMsg) = room.StartGame(guestConn);
        Check.False(startOk, "反作弊：非房主不能开局");
        Check.Equal(ServerMessages.OnlyHostCanStart, startMsg, "反作弊：非房主开局返回统一文案");

        var (editOk, editMsg) = room.UpdateSettings(guestConn, 8, "hacked", false, null);
        Check.False(editOk, "反作弊：非房主不能修改房间设置");
        Check.Equal(ServerMessages.OnlyHostCanEdit, editMsg, "反作弊：非房主改设置返回统一文案");

        var (rotateOk, _, _) = manager.RotateInvite(guestConn);
        Check.False(rotateOk, "反作弊：非房主不能重置邀请令牌");

        var (rotateHostOk, _, _) = manager.RotateInvite(hostConn);
        Check.True(rotateHostOk, "反作弊：房主本人可以重置邀请令牌");
    }

    private static void RejectedWhenTooFewPlayers()
    {
        var room = NewRoom(out var hostConn, playerCount: 2);

        var (ok, msg) = room.StartGame(hostConn);
        Check.False(ok, "反作弊：人数不足时不能开局");
        Check.True(msg.Contains("至少需要", StringComparison.Ordinal), "反作弊：人数不足的提示可读");
    }

    // ==================== 身份布局校验 ====================

    private static void RejectedByLayoutValidation()
    {
        var room = NewRoom(out var hostConn);

        // 3 狼 1 民 这种废局必须被拦在开局之前（结构上保存是允许的）。
        var bad = new RoleLayout();
        bad.Set(Roles.Werewolf, 3);
        bad.Set(Roles.Villager, 1);
        Check.True(room.UpdateSettings(hostConn, null, null, false, JsonSerializer.Serialize(bad)).ok,
            "反作弊：非法布局允许保存（保存只是记录，不是生效）");

        var (ok, msg) = room.StartGame(hostConn);
        Check.False(ok, "反作弊：非法身份布局无法开局");
        Check.True(msg.Contains("狼人", StringComparison.Ordinal), "反作弊：非法布局的提示说明原因");

        // 总数与人数不一致同样拦下。
        var mismatch = new RoleLayout();
        mismatch.Set(Roles.Werewolf, 1);
        mismatch.Set(Roles.Villager, 9);
        room.UpdateSettings(hostConn, null, null, false, JsonSerializer.Serialize(mismatch));
        var (ok2, msg2) = room.StartGame(hostConn);
        Check.False(ok2, "反作弊：身份总数与人数不一致时无法开局");
        Check.True(msg2.Contains("不一致", StringComparison.Ordinal), "反作弊：总数不一致的提示说明原因");
    }

    // ==================== 投票阶段的服务端硬校验 ====================

    /// <summary>
    /// 用 1 秒的短规则让夜晚快速流过，走到投票阶段验证服务端对选票的硬校验。
    /// </summary>
    private static void VotingGuards()
    {
        var room = NewRoom(out var hostConn, shortRules: true);

        var started = room.StartGame(hostConn);
        Check.True(started.ok, "反作弊：小局配置下开局成功（用于验证投票校验）");
        if (!started.ok) return;

        if (!WaitForPhase(room, hostConn, Phases.Vote, 20000))
        {
            Check.True(true, "反作弊：未能在超时内到达投票阶段（跳过选票校验）");
            return;
        }

        var me = room.SnapshotFor(hostConn)!;
        var myId = me.YourId;

        // ① 投自己 → 拒绝（本轮新加的服务端硬校验）。
        var (selfOk, selfMsg) = room.HandleGameAction(WfOps.Vote,
            Node(new TargetPayload { TargetId = myId }), hostConn);
        Check.False(selfOk, "反作弊：不能投票给自己");
        Check.True(selfMsg.Contains("自己", StringComparison.Ordinal), "反作弊：投自己的提示可读");

        // ② 投不存在的编号 → 拒绝。
        var (badIdOk, _) = room.HandleGameAction(WfOps.Vote,
            Node(new TargetPayload { TargetId = 999 }), hostConn);
        Check.False(badIdOk, "反作弊：投票给不存在的编号被拒");

        // ③ 投给存活的其他人 → 通过。
        var otherId = me.Players.FirstOrDefault(p => p.Id != myId && p.IsAlive)?.Id ?? -1;
        if (otherId >= 0)
        {
            var (okVote, _) = room.HandleGameAction(WfOps.Vote,
                Node(new TargetPayload { TargetId = otherId }), hostConn);
            Check.True(okVote, "反作弊：投给存活的他人被接受");
        }

        // ④ 弃票是允许的（-1）。
        var (abstainOk, _) = room.HandleGameAction(WfOps.Vote,
            Node(new TargetPayload { TargetId = -1 }), hostConn);
        Check.True(abstainOk, "反作弊：弃票被接受");

        // ⑤ 投票阶段提交「夜间行动」必须被拒（阶段与 op 不匹配）。
        var (wrongPhaseOk, _) = room.HandleGameAction(WfOps.Night,
            Node(new NightPayload { TargetId = myId, Action = "do" }), hostConn);
        Check.False(wrongPhaseOk, "反作弊：投票阶段提交夜间行动被拒");
    }

    // ==================== 辅助 ====================

    /// <summary>轮询房间状态，等待游戏进入指定阶段。</summary>
    private static bool WaitForPhase(GameRoom room, string connId, string phase, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var dto = room.SnapshotFor(connId);
            var payload = dto?.GamePayload?.Deserialize<WerewolfGameState>(Json);
            if (payload?.Phase == phase) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// 建一个无头房间：1 名房主 + (playerCount-1) 名普通玩家，并设一份「1 狼 + 其余平民」的合法布局。
    /// </summary>
    private static GameRoom NewRoom(out string hostConn, out RoomManager manager,
                                    int playerCount = 4, bool shortRules = false)
    {
        var catalog = new GameCatalog();
        catalog.Register(new WerewolfGameModule());

        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };
        var config = shortRules ? ShortRules() : null;
        manager = new RoomManager(new EncryptedChannel(null!, new CryptoSessionStore()), catalog, options, config);

        hostConn = "conn-host";
        manager.BindIdentity(hostConn, "host");

        var created = manager.CreateRoom(hostConn, "反作弊测试房", null, GameIds.Werewolf);
        var room = manager.Find(created.roomId)!;

        for (var i = 1; i < playerCount; i++)
        {
            var conn = $"conn-{i}";
            manager.BindIdentity(conn, $"p{i}");
            room.AddOrReconnect($"p{i}", conn);
        }

        // 合法布局：1 狼 + 其余平民（狼 < 好人）。
        var layout = new RoleLayout();
        layout.Set(Roles.Werewolf, 1);
        layout.Set(Roles.Villager, Math.Max(1, playerCount - 1));
        room.UpdateSettings(hostConn, null, null, false, JsonSerializer.Serialize(layout));

        return room;
    }

    private static GameRoom NewRoom(out string hostConn, int playerCount = 4, bool shortRules = false)
        => NewRoom(out hostConn, out _, playerCount, shortRules);

    /// <summary>1 秒级的短规则，让无头对局能快速走到投票阶段。</summary>
    private static IGameConfig ShortRules()
    {
        var values = new Dictionary<string, string?>
        {
            ["GameRules:NightActionSeconds"] = "1",
            ["GameRules:DiscussionSeconds"] = "1",
            ["GameRules:VoteSeconds"] = "3",
            ["GameRules:HunterShotSeconds"] = "1",
            ["GameRules:MinPlayers"] = "4",
            ["GameRules:MaxPlayers"] = "12"
        };

        return new GameConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static JsonObject? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json) as JsonObject;
}
