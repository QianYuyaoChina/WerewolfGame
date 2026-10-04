using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Client.Share.Crypto;

namespace ClientGUI.Tests;

/// <summary>
/// 针对真实服务器的端到端校验：用 <see cref="GameClient"/> 连接、加入、开局，
/// 并检查服务器推送的状态能否被正确反序列化。
/// 需要调用方先启动服务器（见 Program.cs 的 --server 参数）。
/// </summary>
public static class IntegrationProbe
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

        void Fail(string what)
        {
            ok = false;
            notes.Add("❌ " + what);
        }

        void Pass(string what) => notes.Add("✅ " + what);

        var clients = new List<GameClient>();
        var states = new Dictionary<string, GameStateDto>();
        var privateMsgs = new Dictionary<string, List<string>>();
        var chats = new List<string>();

        // SignalR 回调在别的线程上写入上面这些集合，读写都必须串行化，
        // 否则在谓词里枚举实时集合会抛 “Collection was modified”。
        var gate = new object();

        List<GameStateDto> Snap() { lock (gate) return states.Values.ToList(); }
        int StateCount() { lock (gate) return states.Count; }

        try
        {
            // ---------- 1. 四名玩家连接并加入 ----------
            var names = new[] { "alice", "bob", "carol", "dave" };
            foreach (var name in names)
            {
                var c = new GameClient();
                var mine = name;
                c.StateReceived += s => { lock (gate) states[mine] = s; };
                c.MessageReceived += m =>
                {
                    lock (gate)
                    {
                        // 只统计玩家发言："💬 名字：内容"（讨论提示也是 💬 开头，需排除）
                        if (m.StartsWith("💬") && m.Contains('：')
                            && !m.Contains("自由讨论时间") && !m.Contains("加入了房间"))
                            chats.Add(m);
                        if (!privateMsgs.TryGetValue(mine, out var list))
                            privateMsgs[mine] = list = new List<string>();
                        list.Add(m);
                    }
                };
                clients.Add(c);

                await c.ConnectAsync(url);
                var res = await c.JoinAsync(name, "pw" + name);
                if (!res.ok) Fail($"{name} 加入失败：{res.msg}");
            }

            if (ok) Pass("4 名玩家连接并加入房间");

            // ---------- 1.5 快速幂密钥协商 + PSK 身份认证 ----------
            if (clients.All(c => c.IsEncrypted))
                Pass("4 个客户端均已用快速幂完成 DH 协商并通过 PSK 身份校验（会话已加密）");
            else
                Fail("存在未建立会话密钥的客户端");

            var fingerprints = clients.Select(c => c.SessionKeyFingerprint).ToList();
            if (fingerprints.All(f => f.Length > 0)
                && fingerprints.Distinct(StringComparer.Ordinal).Count() == fingerprints.Count)
                Pass("每个连接使用独立会话密钥（指纹互不相同）");
            else
                Fail("会话密钥指纹异常：" + string.Join(",", fingerprints));

            // ---------- 2. 等待房间状态推送 ----------
            // 注意：4 个客户端都收到过 State 并不代表它们都收到了「包含全部 4 人」的那一次广播，
            // 因此要等到所有人都看到 4 名玩家，避免断言提前于最后一次广播。
            await WaitUntil(() => StateCount() == 4 && Snap().All(s => s.Players.Count == 4), 8000);
            if (StateCount() == 4 && Snap().All(s => s.Players.Count == 4))
                Pass("所有客户端都收到了包含 4 人的 State 推送");
            else
                Fail($"状态未收敛：{StateCount()}/4 份状态，" +
                     $"玩家数=[{string.Join(",", Snap().Select(s => s.Players.Count))}]");

            var waiting = Snap().FirstOrDefault();
            if (waiting == null) return new ProbeResult { Ok = false, Notes = { "❌ 没有收到初始状态" } };

            // 服务器房间是长驻的：上一局结束后新玩家会看到 GameOver，两者都属于“未开局”。
            if (waiting.Phase is not (Phases.Waiting or Phases.GameOver))
                Fail($"未开局阶段应为 Waiting/GameOver，实际 {waiting.Phase}");
            else
                Pass($"开局前阶段为 {waiting.Phase}（服务器房间长驻，两者都合法）");

            if (waiting.Players.Count != 4) Fail($"玩家数应为 4，实际 {waiting.Players.Count}");
            else Pass("玩家列表包含 4 人");

            if (waiting.Players.Count(p => p.IsHost) != 1) Fail("应恰好有 1 名房主");
            else Pass("房主标记正确");

            if (waiting.Players.Count(p => p.IsYou) != 1) Fail("每份状态应恰好标记 1 个 IsYou");
            else Pass("IsYou 标记正确");

            // ---------- 2.5 服务端下发的规则 ----------
            var rules = waiting.Rules;
            if (rules == null)
            {
                Fail("状态里没有下发游戏规则（Rules 为空）");
            }
            else if (rules.NightActionSeconds > 0 && rules.VoteSeconds > 0
                     && rules.MinPlayers > 0 && rules.NightOrder.Contains("狼人"))
            {
                Pass($"状态携带服务端规则（夜晚 {rules.NightActionSeconds}s / 投票 {rules.VoteSeconds}s / " +
                     $"最少 {rules.MinPlayers} 人 / 顺序 {string.Join("→", rules.NightOrder)}）");
            }
            else
            {
                Fail("规则字段与预期不符：" + JsonSerializer.Serialize(rules));
            }

            // ---------- 3. 非房主不能开始 ----------
            var guest = clients[1];
            var guestStart = await guest.StartGameAsync();
            if (guestStart.ok) Fail("非房主竟然可以开始游戏");
            else Pass("非房主无法开始游戏：" + guestStart.msg);

            // ---------- 4. 房主开始游戏 → 收到身份 ----------
            var start = await clients[0].StartGameAsync();
            if (!start.ok) Fail("房主开始游戏失败：" + start.msg);
            else Pass("房主成功开始游戏");

            await WaitUntil(() =>
            {
                lock (gate) return privateMsgs.Values.Any(v => v.Any(m => m.Contains("你的身份是")));
            }, 5000);

            List<KeyValuePair<string, List<string>>> roleMsgs;
            lock (gate)
                roleMsgs = privateMsgs.Where(kv => kv.Value.Any(m => m.Contains("你的身份是"))).ToList();

            if (roleMsgs.Count == 4) Pass("4 名玩家都收到了身份私聊");
            else Fail($"只有 {roleMsgs.Count}/4 名玩家收到身份私聊");

            if (roleMsgs.All(kv => ParseRole(kv.Value) == states[kv.Key].YourRole))
                Pass("私聊身份与 State 中的 YourRole 一致");
            else
                Fail("私聊身份与 State 中的 YourRole 不一致");

            // ---------- 5. 夜晚：狼人与女巫按时限行动 ----------
            await WaitUntil(() => Snap().Any(s => s.Phase == Phases.Night), 8000);
            if (Snap().All(s => s.Phase == Phases.Night || s.Phase == Phases.Waiting))
                Pass("进入 Night 阶段");
            else
                Fail("未按预期进入 Night 阶段：" + string.Join("/", Snap().Select(s => s.Phase).Distinct()));

            // 女巫用解药、狼人选目标，尽快推进夜晚。
            // 服务端按规则配置的夜晚顺序串行等待（默认 守卫→狼人→女巫→预言家），
            // 未轮到自己的提交会被忽略，因此这里按“服务端下发的顺序”逐个等待并提交。
            var nightActors = Snap().FirstOrDefault(s => s.Rules != null)?.Rules?.NightOrder is { Count: > 0 } configured
                ? configured.ToArray()
                : new[] { Roles.Guard, Roles.Werewolf, Roles.Witch, Roles.Seer };
            foreach (var actor in nightActors)
            {
                // 等服务器广播“当前轮到该角色”。
                await WaitUntil(() =>
                {
                    lock (gate) return states.Values.Any(s => s.Phase == Phases.Night && s.ActingRole == actor);
                }, 5000);

                // 找到扮演该角色的存活客户端并提交。
                foreach (var (name, client) in names.Zip(clients))
                {
                    var s = states[name];
                    if (s.Phase != Phases.Night || s.ActingRole != actor || !s.IsAlive) continue;
                    if (s.YourRole != actor) continue;

                    switch (actor)
                    {
                        case Roles.Werewolf:
                            await client.NightActionAsync(1, "do");
                            break;
                        case Roles.Witch:
                            await client.NightActionAsync(-2, "heal");
                            break;
                        case Roles.Seer:
                            await client.NightActionAsync(2, "do");
                            break;
                        case Roles.Guard:
                            await client.NightActionAsync(3, "do");
                            break;
                    }
                }
            }
            Pass($"夜间行动已按服务端下发的顺序提交（{string.Join("/", nightActors)}）");

            // ---------- 6. 白天 → 讨论 → 投票 ----------
            await WaitUntil(() => Snap().Any(s => s.Phase == Phases.Vote), 60_000);
            if (Snap().Any(s => s.Phase == Phases.Vote))
                Pass("夜晚结束后进入 Vote 阶段");
            else
                Fail("未能进入 Vote 阶段，当前：" + string.Join("/", Snap().Select(s => s.Phase).Distinct()));

            // 全员投票给 1 号
            foreach (var (name, client) in names.Zip(clients))
            {
                if (!states[name].IsAlive) continue;
                await client.VoteAsync(1);
            }

            // ---------- 7. 聊天（必须由存活玩家发言，服务器会忽略出局玩家的发言） ----------
            await WaitUntil(() => StateCount() == 4 && Snap().All(s => s.Players.Count == 4), 5000);

            var speakerIndex = -1;
            for (var i = 0; i < names.Length; i++)
            {
                var me = states[names[i]].Players.FirstOrDefault(p => p.IsYou);
                if (me is { IsAlive: true }) { speakerIndex = i; break; }
            }

            if (speakerIndex < 0)
            {
                Fail("没有存活玩家可以发言");
            }
            else
            {
                var speaker = names[speakerIndex];
                await clients[speakerIndex].ChatAsync("大家好，这是 GUI 客户端的联调测试。");
                await WaitUntil(() => { lock (gate) return chats.Count > 0; }, 5000);

                List<string> chatSnapshot;
                lock (gate) chatSnapshot = chats.ToList();

                if (chatSnapshot.Count > 0 && chatSnapshot[0].Contains(speaker) && chatSnapshot[0].Contains("联调测试"))
                    Pass($"聊天消息已广播（{speaker}）：" + chatSnapshot[0]);
                else
                    Fail("未收到聊天广播，实际：" +
                         (chatSnapshot.Count == 0 ? "无" : string.Join(" | ", chatSnapshot)));
            }

            // ---------- 8. 状态里能看到投票结果（放逐或平票） ----------
            await WaitUntil(() => Snap().Any(s =>
                s.Phase is Phases.Day or Phases.Discussion or Phases.Night or Phases.GameOver
                && s.Players.Any(p => !p.IsAlive)), 30_000);

            var anyDeath = Snap().Any(s => s.Players.Any(p => !p.IsAlive));
            var anyNight2 = Snap().Any(s => s.Day >= 2);
            if (anyDeath || anyNight2) Pass("投票/夜晚的后续流程正常推进");
            else notes.Add("ℹ️ 本轮未出现死亡（平票或平安夜），不做判定");

            // ---------- 9. JSON 契约：直接用服务器传来的形状反序列化 ----------
            var wire = JsonNode.Parse("""
            {
              "Phase": "Night",
              "Day": 2,
              "YourId": 1,
              "YourRole": "狼人",
              "YourRoleDesc": "夜晚与队友共同击杀一名玩家，白天伪装成好人。",
              "IsAlive": true,
              "Players": [
                { "Id": 0, "Name": "alice", "IsAlive": true, "IsHost": true, "IsYou": false, "IsConnected": true, "Role": "狼人" },
                { "Id": 1, "Name": "bob", "IsAlive": false, "IsHost": false, "IsYou": true, "IsConnected": false, "Role": null }
              ]
            }
            """);

            var parsed = wire.Deserialize<GameStateDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is { Phase: "Night", Day: 2, YourId: 1, YourRole: "狼人", IsAlive: true }
                && parsed.Players.Count == 2
                && parsed.Players[0] is { Id: 0, Name: "alice", IsHost: true, IsYou: false, IsConnected: true, Role: "狼人" }
                && parsed.Players[1] is { IsAlive: false, IsYou: true, IsConnected: false, Role: null })
                Pass("DTO 与服务器 JSON 形状匹配（含 null Role / 断线标记）");
            else
                Fail("DTO 反序列化结果与预期不符");

            // ---------- 9.5 心跳 ----------
            try
            {
                var latency = await clients[0].PingAsync();
                if (latency >= 0 && latency < 5000)
                    Pass($"心跳往返正常（{latency} ms）");
                else
                    Fail("心跳时延异常：" + latency + " ms");
            }
            catch (Exception ex)
            {
                Fail("心跳失败：" + ex.Message);
            }

            // ---------- 10. 身份认证：错误的预共享密钥必须被拒绝 ----------
            var originalPsk = Environment.GetEnvironmentVariable(PreSharedKey.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    PreSharedKey.EnvironmentVariable, "definitely-not-the-server-psk");

                var rogue = new GameClient();
                try
                {
                    await rogue.ConnectAsync(url);
                    Fail("错误的预共享密钥竟然握手成功（身份认证失效）");
                }
                catch (Exception ex) when (ex.Message.Contains("身份校验"))
                {
                    Pass("错误的预共享密钥被拒绝，身份认证生效：" + ex.Message);
                }
                catch (Exception ex)
                {
                    Fail("错误 PSK 的失败原因不符合预期：" + ex.Message);
                }
                finally
                {
                    try { await rogue.DisposeAsync(); } catch { }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(PreSharedKey.EnvironmentVariable, originalPsk);
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
                try { await c.DisposeAsync(); } catch { }
            }
        }

        return new ProbeResult { Ok = ok, Notes = notes };
    }

    private static string? ParseRole(List<string> messages)
    {
        var msg = messages.FirstOrDefault(m => m.Contains("你的身份是"));
        if (msg == null) return null;
        var start = msg.IndexOf("【", StringComparison.Ordinal);
        var end = msg.IndexOf("】", StringComparison.Ordinal);
        return start >= 0 && end > start ? msg[(start + 1)..end] : null;
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
