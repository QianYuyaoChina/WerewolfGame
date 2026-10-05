using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 房主交接 / 房间保留 / 邀请码的端到端校验（<c>--hostinvite &lt;url&gt;</c>）。
/// 需要调用方先启动服务端。
/// 覆盖：
/// <list type="number">
///   <item>加密房建好后由他人凭密码加入；</item>
///   <item>邀请码生成 → 解析（含服务器地址 / 房间号 / 密码）→ 新客户端免输密码进房；</item>
///   <item>房主退出且房间内还有人 → 房主按加入顺序转让，房间不关闭；</item>
///   <item>最后一人主动退房且不保留 → 房间立即关闭并从列表消失；</item>
///   <item>房主退出且房间无人 → 选择保留 → 房间仍在列表，且他人可重新进入并成为新房主。</item>
/// </list>
/// </summary>
public static class HostInviteProbe
{
    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public static async Task<ProbeResult> RunAsync(string url)
    {
        var notes = new List<string>();
        var ok = true;

        void Pass(string what) => notes.Add("✅ " + what);
        void Fail(string what) { ok = false; notes.Add("❌ " + what); }

        var clients = new List<GameClient>();
        var states = new Dictionary<string, GameStateDto>();
        var gate = new object();

        async Task<GameClient?> NewClientAsync(string name)
        {
            var client = new GameClient();
            var mine = name;
            client.StateReceived += s => { lock (gate) states[mine] = s; };

            clients.Add(client);
            await client.ConnectAsync(url);

            var login = await client.LoginAsync(name, "pw" + name);
            if (!login.ok)
            {
                Fail($"{name} 登录失败：{login.msg}");
                return null;
            }
            return client;
        }

        GameStateDto? StateOf(string name)
        {
            lock (gate) return states.TryGetValue(name, out var s) ? s : null;
        }

        try
        {
            var host = await NewClientAsync("host_x");
            var bob = await NewClientAsync("bob_x");
            var carol = await NewClientAsync("carol_x");
            var dave = await NewClientAsync("dave_x");
            if (host == null || bob == null || carol == null || dave == null)
                return new ProbeResult { Ok = false, Notes = notes };

            Pass("4 个客户端连接并登录成功");

            // ---------- 0. 进房后主动拉取状态（回归：进房广播早于界面创建时主界面会空白） ----------
            var solo = await NewClientAsync("solo_x");
            if (solo != null)
            {
                var soloRoom = await solo.CreateRoomAsync("状态拉取房", null);
                if (!soloRoom.Ok)
                {
                    Fail("状态拉取房创建失败：" + soloRoom.Message);
                }
                else
                {
                    var snap = await solo.RefreshStateAsync();
                    if (snap == null)
                    {
                        Fail("主动拉取房间状态返回空");
                    }
                    else if (snap.RoomId == soloRoom.RoomId && snap.Players.Count == 1
                             && snap.Players[0].IsHost && snap.Players[0].IsYou)
                    {
                        Pass($"进房后可主动拉取到初始状态（{snap.Players[0].Name} 被标记为房主）");
                    }
                    else
                    {
                        Fail($"拉取到的状态不符：房间={snap.RoomId} 人数={snap.Players.Count} " +
                             $"房主标记={snap.Players[0].IsHost}");
                    }

                    if (solo.LastState != null) Pass("客户端缓存了最近一次状态（供主界面即时回填）");
                    else Fail("客户端未缓存最近一次状态");

                    await solo.LeaveRoomAsync();   // 单人不保留 → 关闭，避免留下空房
                }
            }

            // ---------- 1. 建房 + 密码加入 ----------
            var created = await host.CreateRoomAsync("邀请码测试房", "pw123");
            if (!created.Ok)
            {
                Fail("建房失败：" + created.Message);
                return new ProbeResult { Ok = false, Notes = notes };
            }

            var roomId = created.RoomId;
            Pass($"房主建房成功：{roomId}（加密房）");

            var j1 = await bob.JoinRoomAsync(roomId, "pw123");
            var j2 = await carol.JoinRoomAsync(roomId, "pw123");
            if (j1.Ok && j2.Ok) Pass("两名玩家凭密码加入成功");
            else Fail($"密码加入失败：bob={j1.Message} carol={j2.Message}");

            await WaitUntil(() => StateOf("host_x")?.Players.Count == 3, 6000);

            var info = (await host.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == roomId);
            if (info == null) Fail("房间列表中找不到刚创建的房间");
            else if (info.PlayerCount == 3 && info.Host == "host_x" && info.HasPassword)
                Pass($"列表一致：{info.Name} {info.PlayerCount}/{info.MaxPlayers} 房主={info.Host} 加密={info.HasPassword}");
            else Fail($"列表内容不符：人数={info.PlayerCount} 房主={info.Host} 加密={info.HasPassword}");

            // ---------- 2. 房主设置 + 邀请码（令牌制：改密码后旧码仍有效） ----------
            var hs = await host.GetHostSettingsAsync();
            if (!hs.Ok || hs.Settings == null)
            {
                Fail("房主拉取房间设置失败：" + hs.Message);
            }
            else
            {
                Pass($"房主拉取设置：人数上限 {hs.Settings.MaxPlayers}（允许 {hs.Settings.MinPlayers}-{hs.Settings.MaxPlayersLimit}）、加密={hs.Settings.HasPassword}");

                var token = hs.Settings.InviteToken;
                var code = host.CreateInviteCode(token);

                if (string.IsNullOrEmpty(code) || !code.StartsWith(InviteCode.Prefix, StringComparison.Ordinal))
                {
                    Fail("未能生成邀请码");
                }
                else if (code.Contains("pw123", StringComparison.Ordinal))
                {
                    Fail("邀请码里出现了房间密码明文");
                }
                else if (!GameClient.TryParseInvite(code, out var invite, out var inviteError) || invite == null)
                {
                    Fail("邀请码解析失败：" + inviteError);
                }
                else if (invite.RoomId != roomId || invite.InviteToken != token
                         || !string.Equals(invite.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    Fail($"邀请码内容不符：room={invite.RoomId} token={(invite.InviteToken == token ? "ok" : "bad")} url={invite.Url}");
                }
                else
                {
                    Pass("邀请码还原出正确的服务器地址 / 房间号 / 邀请令牌（不含密码）");

                    // ★ 关键需求：房主修改密码后，先前发出的邀请码必须仍然有效。
                    var changed = await host.UpdateRoomSettingsAsync(null, "brand-new-pwd", false);
                    if (changed.Ok) Pass("房主已修改房间密码");
                    else Fail("房主改密码失败：" + changed.Message);

                    var viaInvite = await dave.JoinByInviteAsync(invite);
                    if (viaInvite.Ok && viaInvite.RoomId == roomId)
                        Pass("改密码后旧邀请码依然能进房（邀请令牌与密码解耦）");
                    else
                        Fail("改密码后旧邀请码失效了：" + viaInvite.Message);

                    // 反面：不带令牌时，旧密码必须被拒、新密码必须通过。
                    var eve = await NewClientAsync("eve_x");
                    if (eve != null)
                    {
                        var wrong = await eve.JoinRoomAsync(roomId, "pw123");
                        if (!wrong.Ok) Pass("不带令牌用旧密码进房被拒：" + wrong.Message);
                        else Fail("旧密码竟然还能进房");

                        var right = await eve.JoinRoomAsync(roomId, "brand-new-pwd");
                        if (right.Ok) Pass("不带令牌用新密码可以正常进房");
                        else Fail("新密码加入失败：" + right.Message);

                        await eve.LeaveRoomAsync();
                    }

                    // 重置邀请令牌 → 旧邀请码立即失效（房主主动作废的唯一方式）。
                    var rotated = await host.ResetInviteAsync();
                    if (rotated.Ok && rotated.Settings != null) Pass("房主重置邀请令牌成功");
                    else Fail("重置邀请令牌失败：" + rotated.Message);

                    var frank = await NewClientAsync("frank_x");
                    if (frank != null)
                    {
                        var expired = await frank.JoinByInviteAsync(invite);
                        if (!expired.Ok) Pass("重置后旧邀请码立即失效：" + expired.Message);
                        else Fail("重置后旧邀请码竟然还能进房");
                    }
                }
            }

            await WaitUntil(() => StateOf("carol_x")?.Players.Count == 4, 6000);

            // ---------- 3. 房主退出且房间还有人 → 转让 ----------
            var hostLeave = await host.LeaveRoomAsync();
            if (hostLeave.ok) Pass("房主退出成功（房间内还有其他人）");
            else Fail("房主退出失败：" + hostLeave.msg);

            await WaitUntil(() => StateOf("bob_x")?.HostName == "bob_x", 6000);

            var afterLeave = (await bob.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == roomId);
            if (afterLeave == null) Fail("房主退出后房间被误关闭");
            else if (afterLeave.Host == "bob_x" && afterLeave.PlayerCount == 3)
                Pass($"房主已转让给第二位加入者：房主={afterLeave.Host} 人数={afterLeave.PlayerCount}");
            else Fail($"转让结果不符：房主={afterLeave.Host} 人数={afterLeave.PlayerCount}");

            if (StateOf("bob_x")?.Players.FirstOrDefault(p => p.IsYou)?.IsHost == true)
                Pass("新房主自己的状态里也标记为房主（👑）");
            else Fail("新房主的状态里未标记 IsHost");

            // ---------- 4. 最后一人主动退房且不保留 → 关闭 ----------
            await bob.LeaveRoomAsync();
            await carol.LeaveRoomAsync();
            var lastLeave = await dave.LeaveRoomAsync();

            if (!lastLeave.ok) Fail("最后一人退出失败：" + lastLeave.msg);
            else if (lastLeave.msg == "closed") Pass("最后一人主动退房且不保留 → 服务端关闭房间");
            else Fail("最后一人退出未关闭房间，返回：" + lastLeave.msg);

            var gone = (await dave.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == roomId);
            if (gone == null) Pass("已关闭的房间从列表中消失");
            else Fail("房间应已关闭，但仍出现在列表中");

            // ---------- 5. 保留房间 ----------
            var keepRoomCreated = await dave.CreateRoomAsync("保留房", "keep123");
            if (!keepRoomCreated.Ok)
            {
                Fail("创建保留房失败：" + keepRoomCreated.Message);
                return new ProbeResult { Ok = false, Notes = notes };
            }

            var keepId = keepRoomCreated.RoomId;
            var keepLeave = await dave.LeaveRoomAsync(keepRoom: true);
            if (keepLeave.ok) Pass("房主（单人房）选择保留并退出");
            else Fail("保留退出失败：" + keepLeave.msg);

            await Task.Delay(400);
            var kept = (await dave.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == keepId);
            if (kept == null) Fail("选择保留后房间竟然消失了");
            else if (kept.PlayerCount == 0 && kept.Host == "")
                Pass($"保留房仍在列表中：{kept.Name} 人数=0 房主=（无）状态={RoomStatus.Name(kept.Status)}");
            else Fail($"保留房状态不符：人数={kept.PlayerCount} 房主={kept.Host}");

            // 他人重新进入被保留的房间：密码仍生效，且自动成为新房主。
            var rejoin = await bob.JoinRoomAsync(keepId, "keep123");
            if (rejoin.Ok) Pass("他人可重新进入被保留的房间（房间密码仍然有效）");
            else Fail("重新进入保留房失败：" + rejoin.Message);

            await Task.Delay(400);
            var keptAgain = (await bob.ListRoomsAsync()).Rooms.FirstOrDefault(r => r.Id == keepId);
            if (keptAgain != null && keptAgain.Host == "bob_x")
                Pass("重新进入保留房的人自动成为新房主");
            else Fail($"保留房新房主不符：{keptAgain?.Host ?? "（房间不存在）"}");

            // 新房主不保留 → 房间关闭，收尾干净。
            var closeIt = await bob.LeaveRoomAsync();
            if (closeIt.ok && closeIt.msg == "closed") Pass("新房主选择不保留 → 房间被关闭");
            else Fail("新房主退出未关闭房间：" + closeIt.msg);
        }
        catch (Exception ex)
        {
            Fail("异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in clients)
            {
                try { await c.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
            }
        }

        return new ProbeResult { Ok = ok, Notes = notes };
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }
}
