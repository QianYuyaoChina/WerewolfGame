using System.Text.Json;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// v2.1 新身份的端到端校验（<c>--newroles &lt;url&gt;</c>）。
/// 需要一个**小局配置**的服务端：<c>GameRules</c> 的时长调小（夜晚 4 秒 / 讨论 2 秒 / 投票 3 秒 / 猎人 2 秒）。
///
/// 覆盖：
/// <list type="number">
///   <item>房主自定义身份布局后开局，布局被严格校验并**公布给全房间**；</item>
///   <item>实际分配的身份与布局完全一致；</item>
///   <item>警徽是公开信息（所有视角都能看到谁是警长）；</item>
///   <item>丘比特第一夜牵线成功，双方都能在私有负载里看到自己的情侣；</item>
///   <item>情侣一方被狼人击杀后，另一方**当场殉情**。</item>
/// </list>
/// </summary>
public static class NewRolesProbe
{
    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

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
            if (!login.ok) { Fail($"{name} 登录失败：{login.msg}"); return null; }
            return client;
        }

        GameStateDto? StateOf(string name)
        {
            lock (gate) return states.TryGetValue(name, out var s) ? s : null;
        }

        static WerewolfGameState? Payload(GameStateDto? s)
        {
            if (s?.GamePayload == null) return null;
            try { return s.GamePayload.Deserialize<WerewolfGameState>(Json); } catch { return null; }
        }

        try
        {
            var host = await NewClientAsync("nr_host");
            var p2 = await NewClientAsync("nr_b");
            var p3 = await NewClientAsync("nr_c");
            var p4 = await NewClientAsync("nr_d");
            if (host == null || p2 == null || p3 == null || p4 == null)
                return new ProbeResult { Ok = false, Notes = notes };

            Pass("4 个客户端连接并登录成功");

            // ---------- 1. 自定义身份布局（1 狼 + 1 丘比特 + 1 警长 + 1 平民） ----------
            var created = await host.CreateRoomAsync("新身份测试房", "pw123");
            if (!created.Ok) { Fail("建房失败：" + created.Message); return new ProbeResult { Ok = false, Notes = notes }; }
            var roomId = created.RoomId;

            AssertTrue((await p2.JoinRoomAsync(roomId, "pw123")).Ok, "b 加入");
            AssertTrue((await p3.JoinRoomAsync(roomId, "pw123")).Ok, "c 加入");
            AssertTrue((await p4.JoinRoomAsync(roomId, "pw123")).Ok, "d 加入");

            var layout = new RoleLayout();
            layout.Set(Roles.Werewolf, 1);
            layout.Set(Roles.Cupid, 1);
            layout.Set(Roles.Sheriff, 1);
            layout.Set(Roles.Villager, 1);
            var layoutJson = JsonSerializer.Serialize(layout);

            var applied = await host.UpdateRoomSettingsAsync(null, null, false, layoutJson);
            if (applied.Ok) Pass($"房主自定义身份布局成功：{layout.Describe()}");
            else Fail("保存身份布局失败：" + applied.Message);

            // 非法布局（狼人不少于好人）必须被开局校验拦住。
            var bad = new RoleLayout();
            bad.Set(Roles.Werewolf, 3);
            bad.Set(Roles.Villager, 1);
            var badApplied = await host.UpdateRoomSettingsAsync(null, null, false, JsonSerializer.Serialize(bad));
            if (badApplied.Ok) Pass("非法布局可以保存（结构上合法），但开局会被拦下");
            else Pass("非法布局在保存阶段即被拒绝：" + badApplied.Message);

            if (badApplied.Ok)
            {
                var blocked = await host.StartGameAsync();
                if (!blocked.ok) Pass("非法布局阻止开局：" + blocked.msg);
                else Fail("非法布局竟然开局成功");
            }

            // 恢复合法布局后开局。
            AssertTrue((await host.UpdateRoomSettingsAsync(null, null, false, layoutJson)).Ok, "恢复合法布局");

            var started = await host.StartGameAsync();
            if (started.ok) Pass("房主开局成功（自定义布局）");
            else { Fail("开局失败：" + started.msg); return new ProbeResult { Ok = false, Notes = notes }; }

            await WaitUntil(() => Payload(StateOf("nr_host"))?.RoleSummary.Length > 0, 6000);

            // ---------- 2. 布局公布 + 分配一致 ----------
            var hostPayload = Payload(StateOf("nr_host"));
            if (hostPayload == null) Fail("未收到游戏私有负载");
            else if (hostPayload.RoleSummary.Contains("丘比特", StringComparison.Ordinal)
                     && hostPayload.RoleSummary.Contains("警长", StringComparison.Ordinal))
                Pass($"开局已公布身份配置：{hostPayload.RoleSummary}");
            else
                Fail("身份配置未公布或内容不符：" + hostPayload.RoleSummary);

            await WaitUntil(() => new[] { "nr_host", "nr_b", "nr_c", "nr_d" }
                .All(n => Payload(StateOf(n))?.YourRole.Length > 0), 6000);

            var roles = new[] { "nr_host", "nr_b", "nr_c", "nr_d" }
                .Select(n => Payload(StateOf(n))?.YourRole ?? "")
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();

            var expectedRoles = new[] { Roles.Villager, Roles.Sheriff, Roles.Cupid, Roles.Werewolf }
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
            AssertTrue(roles.SequenceEqual(expectedRoles),
                $"实际分配与自定义布局一致（{string.Join(" / ", roles)}）");

            // ---------- 3. 警徽是公开信息 ----------
            var sheriffName = Payload(StateOf("nr_host"))?.SheriffName ?? "";
            var allSeeSheriff = !string.IsNullOrEmpty(sheriffName)
                && new[] { "nr_host", "nr_b", "nr_c", "nr_d" }.All(n =>
                {
                    var s = StateOf(n);
                    return s != null && s.Players.Any(p =>
                        p.Name == sheriffName && p.Tag == Roles.Sheriff);
                });

            if (allSeeSheriff) Pass($"警长「{sheriffName}」在所有玩家的视角里都公开可见");
            else Fail("警长标记未对所有视角公开");

            // ---------- 4. 丘比特牵线 ----------
            var cupidName = new[] { "nr_host", "nr_b", "nr_c", "nr_d" }
                .FirstOrDefault(n => Payload(StateOf(n))?.YourRole == Roles.Cupid) ?? "";
            var wolfName = new[] { "nr_host", "nr_b", "nr_c", "nr_d" }
                .FirstOrDefault(n => Payload(StateOf(n))?.YourRole == Roles.Werewolf) ?? "";
            var others = new[] { "nr_host", "nr_b", "nr_c", "nr_d" }
                .Where(n => n != cupidName && n != wolfName).ToList();

            var cupid = clients.First(c => c.Username == cupidName);
            var wolf = clients.First(c => c.Username == wolfName);

            if (others.Count != 2) Fail("情侣候选不是 2 人，无法继续");
            else
            {
                var a = IdOf(StateOf(cupidName), others[0]);
                var b = IdOf(StateOf(cupidName), others[1]);

                await WaitUntil(() => Payload(StateOf(cupidName))?.ActingRole == Roles.Cupid, 8000);
                await cupid.CallGameOpAsync(WerewolfOps.Cupid,
                    JsonSerializer.SerializeToNode(new CupidPayload { TargetA = a, TargetB = b }, Json));

                await WaitUntil(() => Payload(StateOf(others[0]))?.IsLover == true
                                      && Payload(StateOf(others[1]))?.IsLover == true, 8000);

                var loverA = Payload(StateOf(others[0]));
                var loverB = Payload(StateOf(others[1]));
                if (loverA is { IsLover: true } && loverB is { IsLover: true }
                    && loverA.LoverName == others[1] && loverB.LoverName == others[0])
                    Pass($"丘比特牵线成功：{others[0]} ↔ {others[1]}（双方都能看到自己的情侣）");
                else
                    Fail($"牵线结果不符：{others[0]}→{loverA?.LoverName}，{others[1]}→{loverB?.LoverName}");

                // ---------- 5. 狼人击杀其中一方 → 另一方殉情 ----------
                await WaitUntil(() => Payload(StateOf(wolfName))?.ActingRole == Roles.Werewolf, 10000);
                await wolf.CallGameOpAsync(WerewolfOps.Night,
                    JsonSerializer.SerializeToNode(new NightPayload { TargetId = a, Action = "do" }, Json));

                await WaitUntil(() =>
                {
                    var s = StateOf(cupidName);
                    if (s == null) return false;
                    var dead = s.Players.Where(p => !p.IsAlive).Select(p => p.Name).ToList();
                    return dead.Contains(others[0]) && dead.Contains(others[1]);
                }, 15000);

                var after = StateOf(cupidName);
                var deadNames = after?.Players.Where(p => !p.IsAlive).Select(p => p.Name).ToList() ?? new();
                if (deadNames.Contains(others[0]) && deadNames.Contains(others[1]))
                    Pass($"情侣殉情生效：{others[0]} 出局后 {others[1]} 同时出局");
                else
                    Fail($"殉情未生效，当前出局者：{string.Join("、", deadNames)}");
            }
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

        // 本地断言助手：只记结论，不中断流程（房间清理交给 finally）。
        void AssertTrue(bool condition, string what)
        {
            if (condition) Pass(what);
            else Fail(what);
        }

        static int IdOf(GameStateDto? state, string username)
            => state?.Players.FirstOrDefault(p => p.Name == username)?.Id ?? -1;
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
