using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Werewolf;

namespace ClientGUI.Tests;

/// <summary>
/// v2.1 新增内容的纯逻辑 / 无头校验：
/// <list type="bullet">
///   <item>身份布局（<see cref="RoleLayout"/>）：默认布局与改造前等价、严格校验、展示文案；</item>
///   <item>房主设置：权限、人数上下限、改密码后**邀请令牌不变**、重置令牌；</item>
///   <item>邀请令牌：校验与轮换（这是「改密码后邀请码仍有效」的实现基础）。</item>
/// </list>
/// </summary>
public static class NewRolesTests
{
    public static void RunAll()
    {
        RoleLayoutDefaults();
        RoleLayoutValidation();
        InviteTokenStableAcrossPasswordChange();
        RoomSettingsPermissions();
    }

    // ==================== 身份布局 ====================

    /// <summary>
    /// 默认布局（= 按人数自动配置）的行为：神职随人数递增，平民稳定在 2-3 个，
    /// 且**每一种人数都必须通过严格校验**。
    /// </summary>
    private static void RoleLayoutDefaults()
    {
        // 人数 → (狼, 神职数, 民)
        var expected = new (int n, int wolves, int specials, int villagers)[]
        {
            (4, 1, 1, 2), (5, 1, 2, 2), (6, 2, 2, 2), (7, 2, 3, 2), (8, 2, 3, 3),
            (9, 3, 4, 2), (10, 3, 5, 2), (11, 3, 6, 2), (12, 4, 6, 2)
        };

        var allMatch = true;
        foreach (var (n, wolves, specials, villagers) in expected)
        {
            var layout = RoleLayout.Default(n);
            var actualSpecials = layout.Get(Roles.Seer) + layout.Get(Roles.Witch)
                                 + layout.Get(Roles.Hunter) + layout.Get(Roles.Guard)
                                 + layout.Get(Roles.Cupid) + layout.Get(Roles.Sheriff);

            if (layout.Get(Roles.Werewolf) != wolves) allMatch = false;
            if (actualSpecials != specials) allMatch = false;
            if (layout.Get(Roles.Villager) != villagers) allMatch = false;
            if (layout.Total != n) allMatch = false;
        }
        Check.True(allMatch, "布局：默认布局在 4-12 人下「狼/神/民」分配符合设计表");

        // 每一种人数的默认布局都必须能开局（严格校验通过）。
        var allValid = true;
        for (var n = 4; n <= 12; n++)
            if (!RoleLayout.Default(n).ValidateForGame(n).ok) allValid = false;
        Check.True(allValid, "布局：4-12 人的默认布局全部通过开局的严格校验");

        // 神职随人数**单调不减**（新人优先补神职而不是平民）。
        var monotonic = true;
        for (var n = 4; n < 12; n++)
        {
            var a = RoleLayout.Default(n);
            var b = RoleLayout.Default(n + 1);
            var specialsA = a.Get(Roles.Seer) + a.Get(Roles.Witch) + a.Get(Roles.Hunter)
                            + a.Get(Roles.Guard) + a.Get(Roles.Cupid) + a.Get(Roles.Sheriff);
            var specialsB = b.Get(Roles.Seer) + b.Get(Roles.Witch) + b.Get(Roles.Hunter)
                            + b.Get(Roles.Guard) + b.Get(Roles.Cupid) + b.Get(Roles.Sheriff);
            if (specialsB < specialsA) monotonic = false;
        }
        Check.True(monotonic, "布局：神职数量随人数单调不减");

        // 关键阈值：丘比特 10 人起、警长 11 人起、守卫 9 人起、猎人 7 人起。
        Check.Equal(0, RoleLayout.Default(9).Get(Roles.Cupid), "布局：9 人局还没有丘比特");
        Check.Equal(1, RoleLayout.Default(10).Get(Roles.Cupid), "布局：10 人局加入丘比特");
        Check.Equal(0, RoleLayout.Default(10).Get(Roles.Sheriff), "布局：10 人局还没有警长");
        Check.Equal(1, RoleLayout.Default(11).Get(Roles.Sheriff), "布局：11 人局加入警长");
        Check.Equal(1, RoleLayout.Default(9).Get(Roles.Guard), "布局：9 人局加入守卫");
        Check.Equal(0, RoleLayout.Default(8).Get(Roles.Guard), "布局：8 人局还没有守卫");

        // 人数越多，新人优先补神职：4→5 加的是女巫（神职）而不是平民。
        Check.Equal(1, RoleLayout.Default(5).Get(Roles.Witch), "布局：5 人局补的第一个是女巫");

        // 自定义布局的展示文案（开局公布用）。
        var custom = new RoleLayout();
        custom.Set(Roles.Werewolf, 1);
        custom.Set(Roles.Cupid, 1);
        custom.Set(Roles.Seer, 1);
        custom.Set(Roles.Villager, 2);
        var text = custom.Describe();
        Check.True(text.Contains("狼人×1", StringComparison.Ordinal), "布局文案：含狼人数量");
        Check.True(text.Contains("丘比特×1", StringComparison.Ordinal), "布局文案：含丘比特数量");
        Check.True(text.Contains("平民×2", StringComparison.Ordinal), "布局文案：含平民数量");
        Check.True(text.IndexOf("狼人", StringComparison.Ordinal) < text.IndexOf("丘比特", StringComparison.Ordinal),
            "布局文案：狼人排在神职之前");

        Check.Equal(5, custom.Total, "布局：Total 累加各身份数量");
        custom.Set(Roles.Seer, 0);
        Check.Equal(0, custom.Get(Roles.Seer), "布局：数量设为 0 等于移除该身份");
    }

    private static void RoleLayoutValidation()
    {
        // ① 总数必须等于人数（严格校验）。
        var four = new RoleLayout();
        four.Set(Roles.Werewolf, 1);
        four.Set(Roles.Seer, 1);
        four.Set(Roles.Villager, 2);

        Check.True(four.ValidateForGame(4).ok, "校验：总数与人数一致的合法布局通过");
        var mismatch = four.ValidateForGame(5);
        Check.False(mismatch.ok, "校验：总数与人数不一致时拒绝开局");
        Check.True(mismatch.msg.Contains("5", StringComparison.Ordinal), "校验：提示里带上当前人数");

        // ② 至少 1 名狼人。
        var noWolf = new RoleLayout();
        noWolf.Set(Roles.Villager, 4);
        Check.False(noWolf.ValidateForGame(4).ok, "校验：没有狼人时拒绝开局");

        // ③ 狼人必须少于好人（3 狼 1 民这种废局要被拦住）。
        var tooManyWolves = new RoleLayout();
        tooManyWolves.Set(Roles.Werewolf, 3);
        tooManyWolves.Set(Roles.Villager, 1);
        Check.False(tooManyWolves.ValidateForGame(4).ok, "校验：狼人不少于好人时拒绝开局");

        // ④ 用户示例「1 狼 1 神 2 民」应当合法。
        Check.True(four.ValidateForGame(4).ok, "校验：1 狼 1 神 2 民 的配置合法");

        // ⑤ 警长最多 1 名。
        var twoSheriffs = new RoleLayout();
        twoSheriffs.Set(Roles.Werewolf, 1);
        twoSheriffs.Set(Roles.Sheriff, 2);
        twoSheriffs.Set(Roles.Villager, 2);
        Check.False(twoSheriffs.ValidateForGame(5).ok, "校验：警长最多 1 名");
    }

    // ==================== 邀请令牌与房主设置 ====================

    /// <summary>★ 核心需求：房主改密码不能影响已发出的邀请令牌（否则旧邀请码会集体失效）。</summary>
    private static void InviteTokenStableAcrossPasswordChange()
    {
        var room = NewRoom(out _, out var connId);

        var token0 = room.InviteToken;
        Check.True(token0.Length >= 16, "邀请令牌：长度足够（≥16）");
        Check.True(room.VerifyInviteToken(token0), "邀请令牌：自身校验通过");
        Check.False(room.VerifyInviteToken("wrong-token"), "邀请令牌：错误令牌被拒");
        Check.False(room.VerifyInviteToken(null), "邀请令牌：空令牌被拒");

        // 改密码 → 令牌必须不变。
        Check.True(room.UpdateSettings(connId, null, "new-password", false, null).ok, "房主设置：改密码成功");
        Check.Equal(token0, room.InviteToken, "邀请令牌：改密码后令牌保持不变（旧邀请码仍有效）");
        Check.True(room.VerifyPassword("new-password"), "房主设置：新密码立即生效");
        Check.False(room.VerifyPassword("old-password"), "房主设置：旧密码立即失效");

        // 改人数 → 令牌同样不变。
        Check.True(room.UpdateSettings(connId, 8, null, false, null).ok, "房主设置：改人数成功");
        Check.Equal(token0, room.InviteToken, "邀请令牌：改人数后令牌保持不变");

        // 只有显式重置才换令牌。
        var rotated = room.RotateInviteToken();
        Check.NotEqual(token0, rotated, "邀请令牌：重置后令牌变化（旧邀请码失效）");
        Check.False(room.VerifyInviteToken(token0), "邀请令牌：重置后旧令牌立即失效");
        Check.True(room.VerifyInviteToken(rotated), "邀请令牌：重置后新令牌可用");

        // 清除密码（改公开房）也不影响令牌。
        Check.True(room.UpdateSettings(connId, null, null, clearPassword: true, null).ok, "房主设置：清除密码成功");
        Check.False(room.HasPassword, "房主设置：清除后房间变为公开");
        Check.Equal(rotated, room.InviteToken, "邀请令牌：清除密码后令牌保持不变");
    }

    private static void RoomSettingsPermissions()
    {
        var room = NewRoom(out _, out var hostConn);

        // 非房主不能改（用一个新连接加入房间后尝试）。
        Check.True(room.AddOrReconnect("guest", "conn-guest").ok, "房主设置：第二位玩家加入成功");

        var denied = room.UpdateSettings("conn-guest", 8, null, false, null);
        Check.False(denied.ok, "房主设置：非房主修改被拒");
        Check.Equal(ServerMessages.OnlyHostCanEdit, denied.msg, "房主设置：非房主返回统一文案");

        Check.False(room.UpdateSettings("conn-nobody", 8, null, false, null).ok, "房主设置：不在房间的连接被拒");

        // 人数范围与下限。
        Check.False(room.UpdateSettings(hostConn, 99, null, false, null).ok, "房主设置：超出游戏上限被拒");
        Check.False(room.UpdateSettings(hostConn, 1, null, false, null).ok, "房主设置：低于最少人数被拒");
        Check.False(room.UpdateSettings(hostConn, null, "ab", false, null).ok, "房主设置：密码过短被拒");
        Check.True(room.UpdateSettings(hostConn, 6, null, false, null).ok, "房主设置：合法人数被接受");
        Check.Equal(6, room.MaxPlayers, "房主设置：人数上限立即生效");
        Check.Equal(6, room.ToInfo().MaxPlayers, "房主设置：房间列表也反映新的人数上限");

        // 自定义游戏配置（身份布局）落库并被读取。
        var layout = new RoleLayout();
        layout.Set(Roles.Werewolf, 1);
        layout.Set(Roles.Cupid, 1);
        layout.Set(Roles.Villager, 2);
        var json = System.Text.Json.JsonSerializer.Serialize(layout);

        Check.True(room.UpdateSettings(hostConn, null, null, false, json).ok, "房主设置：保存身份布局成功");
        Check.Equal(json, room.GameSettingsJson, "房主设置：身份布局原样保存");
        Check.True(room.ToHostSettings().CustomGameSettings, "房主设置：标记为自定义配置");

        // 传空串 = 恢复默认。
        Check.True(room.UpdateSettings(hostConn, null, null, false, "").ok, "房主设置：恢复默认配置成功");
        Check.True(room.GameSettingsJson == null, "房主设置：恢复默认后不再保存自定义配置");
        Check.False(room.ToHostSettings().CustomGameSettings, "房主设置：恢复默认后标记清除");

        // 房主设置 DTO 人数上下限来自游戏元数据。
        var settings = room.ToHostSettings();
        Check.Equal(4, settings.MinPlayers, "房主设置：最少人数来自游戏元数据");
        Check.Equal(12, settings.MaxPlayersLimit, "房主设置：人数上限来自游戏元数据");
        Check.Equal(RoomId.Length, settings.RoomId.Length, "房主设置：返回房间号");
        Check.True(settings.InviteToken.Length > 0, "房主设置：返回邀请令牌（供生成邀请码）");
    }

    // ==================== 辅助 ====================

    /// <summary>建一个无头房间（不连服务器），返回房间与房主连接号。</summary>
    private static GameRoom NewRoom(out RoomManager manager, out string hostConnId)
    {
        var catalog = new GameCatalog();
        catalog.Register(new WerewolfGameModule());

        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };
        manager = new RoomManager(new EncryptedChannel(null!, new CryptoSessionStore()), catalog, options);

        hostConnId = "conn-host";
        manager.BindIdentity(hostConnId, "host");

        var created = manager.CreateRoom(hostConnId, "设置测试房", "old-password", GameIds.Werewolf);
        return manager.Find(created.roomId)!;
    }
}
