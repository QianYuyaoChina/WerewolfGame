using System.IO;
using System.Text.Json;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 谁是卧底的端到端校验（<c>--undercover &lt;url&gt;</c>）。
/// 建议用**小局配置**的服务器（<c>Undercover:DescribeSeconds=3 / VoteSeconds=3</c>）跑，否则要等满时限。
///
/// 覆盖：建房时选游戏 → 发牌（1 卧底 + 其余平民，词不同）→ 视角隔离
/// → **按顺序轮流发言**（没轮到的人插话被服务端拒绝；发言内容其他人都能收到）
/// → 投票放逐卧底 → 平民阵营获胜 → 房间状态变为已结束。
/// </summary>
public static class UndercoverProbe
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
        var inbox = new Dictionary<string, List<string>>();   // 每个客户端收到的房间消息
        var gate = new object();

        async Task<GameClient?> NewClientAsync(string name)
        {
            var client = new GameClient();
            var mine = name;
            var seen = new List<string>();

            client.StateReceived += s => { lock (gate) states[mine] = s; };
            client.MessageReceived += m => { lock (gate) seen.Add(m); };

            lock (gate) inbox[mine] = seen;
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

        /// <summary>某客户端收到的房间消息（副本）。</summary>
        List<string> InboxOf(string name)
        {
            lock (gate) return inbox.TryGetValue(name, out var l) ? new List<string>(l) : new List<string>();
        }

        GameClient? ClientOf(string name)
            => clients.FirstOrDefault(c => string.Equals(c.Username, name, StringComparison.OrdinalIgnoreCase));

        static UndercoverGameState? Payload(GameStateDto? s)
        {
            if (s?.GamePayload == null) return null;
            try { return s.GamePayload.Deserialize<UndercoverGameState>(Json); } catch { return null; }
        }

        try
        {
            var names = new[] { "uc_a", "uc_b", "uc_c", "uc_d" };
            var host = await NewClientAsync(names[0]);
            var others = new List<GameClient>();
            foreach (var n in names.Skip(1))
            {
                var c = await NewClientAsync(n);
                if (c != null) others.Add(c);
            }
            if (host == null || others.Count != 3) return new ProbeResult { Ok = false, Notes = notes };

            Pass("4 个客户端连接并登录成功");

            // ---------- 1. 用新游戏建房（房主在创建时选择游戏） ----------
            var created = await host.CreateRoomAsync("卧底测试房", null, GameIds.Undercover);
            if (!created.Ok) { Fail("用「谁是卧底」建房失败：" + created.Message); return new ProbeResult { Ok = false, Notes = notes }; }
            Pass($"房主选择游戏为「谁是卧底」并建房成功：{created.RoomId}");

            foreach (var c in others)
                await c.JoinRoomAsync(created.RoomId, null);

            await WaitUntil(() => names.All(n => StateOf(n) != null), 8000);

            var info = StateOf(names[0]);
            if (info != null && info.GameId == GameIds.Undercover)
                Pass("房间状态里的 GameId = undercover（房间与游戏绑定正确）");
            else
                Fail("房间状态里的 GameId 不对：" + info?.GameId);

            // ---------- 1b. 房主设置：不同游戏的房间要有明显标识，且不串到狼人杀的身份配置 ----------
            var hs = await host.GetHostSettingsAsync();
            if (hs.Ok && hs.Settings != null)
            {
                if (!hs.Settings.HasPlayerConfig)
                    Pass("房主设置：谁是卧底 HasPlayerConfig=false（不会显示狼人杀的「身份配置」页）");
                else
                    Fail("房主设置：谁是卧底仍然报 HasPlayerConfig=true");

                if (hs.Settings.GameId == GameIds.Undercover && hs.Settings.GameName.Length > 0)
                    Pass($"房主设置：房间带有游戏标识 {hs.Settings.GameEmoji} {hs.Settings.GameName}（{hs.Settings.GameAccent}）");
                else
                    Fail($"房主设置：缺少游戏标识（GameId={hs.Settings.GameId}，GameName={hs.Settings.GameName}）");

                // 保存（不改游戏私有配置）应当成功 —— 对应「保存后窗口不关闭」那条修复的服务端一侧。
                var saved = await host.UpdateRoomSettingsAsync(hs.Settings.MaxPlayers, null, false, null);
                if (saved.Ok) Pass("房主设置：保存房间设置成功（不下发身份配置）");
                else Fail("房主设置：保存失败 —— " + saved.Message);
            }
            else
            {
                Fail("房主设置：读取失败 —— " + hs.Message);
            }

            // ---------- 2. 开局与发牌 ----------
            var started = await host.StartGameAsync();
            if (!started.ok) { Fail("开局失败：" + started.msg); return new ProbeResult { Ok = false, Notes = notes }; }
            Pass("开局成功");

            await WaitUntil(() => names.All(n => Payload(StateOf(n))?.YourWord != null), 8000);

            var dealt = names.Select(n => (Name: n, P: Payload(StateOf(n)))).ToList();

            // v2.3.2：局中**不下发本人身份**，阵营改由「词出现的次数」反推（多数派 = 平民，少数派 = 卧底）。
            var camps = dealt.Where(d => d.P != null)
                             .GroupBy(d => d.P!.YourWord, StringComparer.Ordinal)
                             .OrderByDescending(g => g.Count())
                             .ToList();
            var civilians = camps.Count > 0 ? camps[0].ToList() : new List<(string Name, UndercoverGameState? P)>();
            var undercover = camps.Skip(1).SelectMany(g => g).ToList();

            if (undercover.Count == 1) Pass($"发牌：1 名卧底（{undercover[0].Name}）");
            else Fail($"发牌：卧底数量不是 1，实际 {undercover.Count}");
            if (civilians.Count == 3) Pass("发牌：3 名平民");
            else Fail($"发牌：平民数量不是 3，实际 {civilians.Count}");

            // 视角隔离：局中谁都不该拿到自己的身份（否则等于开天眼）。
            var leakedRoles = dealt.Count(d => (d.P?.YourRole.Length ?? 0) > 0 || (d.P?.YourRoleKnown ?? false));
            if (leakedRoles == 0) Pass("保密：局中没人拿到自己的身份（YourRole 为空 / YourRoleKnown=false）");
            else Fail($"保密：有 {leakedRoles} 人在局中就拿到了自己的身份");

            var leakedDesc = dealt.Count(d =>
                (d.P?.YourRoleDesc.Contains("卧底", StringComparison.Ordinal) ?? false)
                || (d.P?.YourRoleDesc.Contains("平民", StringComparison.Ordinal) ?? false));
            if (leakedDesc == 0) Pass("保密：身份说明文案不出现「卧底 / 平民」字样");
            else Fail($"保密：有 {leakedDesc} 人的身份说明文案泄露了阵营");

            if (dealt.All(d => d.P?.HasWord ?? false)) Pass("发牌：4 人局不设白板，人人都有词");
            else Fail("发牌：4 人局不该出现白板（没有人拿到空词）");

            if (undercover.Count == 1 && civilians.Count == 3)
            {
                var undercoverWord = undercover[0].P!.YourWord;
                var civilianWord = civilians[0].P!.YourWord;
                if (undercoverWord != civilianWord && undercoverWord.Length > 0 && civilianWord.Length > 0)
                    Pass($"发牌：平民词「{civilianWord}」≠ 卧底词「{undercoverWord}」");
                else
                    Fail("发牌：两边的词相同或为空");

                // 视角隔离：平民的状态里不应出现卧底词。
                var leaked = civilians.Count(c =>
                    StateOf(c.Name)!.GamePayload!.ToJsonString().Contains(undercoverWord, StringComparison.Ordinal));
                if (leaked == 0) Pass("隔离：平民的状态里看不到卧底词");
                else Fail($"隔离：有 {leaked} 名平民的负载里出现了卧底词");

                // ---------- 3. 按顺序轮流发言 ----------
                // 一轮里每个人依次发言：没轮到的人抢话要被服务端拒绝，发言内容所有人都能收到。
                var spokenCount = 0;
                var rejectedByServer = 0;
                var speakerNames = new List<string>();
                var describeDeadline = DateTime.UtcNow.AddMilliseconds(40000);

                while (DateTime.UtcNow < describeDeadline)
                {
                    var hostPayload = Payload(StateOf(names[0]));
                    if (hostPayload == null || hostPayload.Phase != UndercoverPhases.Describe) break;

                    var speaker = hostPayload.CurrentSpeakerName;
                    if (speaker.Length == 0) { await Task.Delay(120); continue; }

                    speakerNames.Add(speaker);
                    var speakerClient = ClientOf(speaker);
                    if (speakerClient == null) { Fail("发言人 " + speaker + " 找不到对应客户端"); break; }

                    // ① 没轮到的人插话 → 服务端必须拒绝。
                    var butler = clients.FirstOrDefault(c =>
                        !string.Equals(c.Username, speaker, StringComparison.OrdinalIgnoreCase));
                    if (butler != null)
                    {
                        var denied = await butler.CallGameOpAsync(Ops.Chat,
                            JsonSerializer.SerializeToNode(new ChatPayload { Text = "插话-不应被广播" }, Json));
                        if (!denied.ok) rejectedByServer++;
                        else Fail("反作弊：没轮到的人插话竟然被放行了（" + butler.Username + "）");
                    }

                    // ② 当前发言人发言 → 广播给全房间，并自动轮到下一位。
                    var line = $"描述-{spokenCount}";
                    await speakerClient.CallGameOpAsync(Ops.Chat,
                        JsonSerializer.SerializeToNode(new ChatPayload { Text = line }, Json));
                    spokenCount++;

                    await WaitUntilAsync(() =>
                    {
                        var p = Payload(StateOf(names[0]));
                        return p == null
                               || p.Phase != UndercoverPhases.Describe
                               || !string.Equals(p.CurrentSpeakerName, speaker, StringComparison.OrdinalIgnoreCase);
                    }, 8000);
                }

                if (rejectedByServer == spokenCount && spokenCount > 0)
                    Pass($"反作弊：{rejectedByServer} 次「没轮到就插话」都被服务端拒绝");
                else if (spokenCount == 0)
                    Fail("轮流发言：一轮下来没有任何人发言");
                else
                    Fail($"反作弊：{spokenCount} 次插话里只有 {rejectedByServer} 次被拒绝");

                // 顺序唯一：同一个人在一轮里只能轮到一次。
                if (speakerNames.Count == speakerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    && speakerNames.Count == 4)
                    Pass($"轮流发言：4 人各轮到一次（{string.Join(" → ", speakerNames)}）");
                else
                    Fail($"轮流发言：发言顺序异常（{string.Join(" → ", speakerNames)}）");

                // 别人的描述要出现在其他人的消息流里（这正是 v2.3.1 修的那个 bug）。
                var heard = names.Count(n =>
                    InboxOf(n).Any(m => m.Contains("描述-", StringComparison.Ordinal)));
                if (heard == 4) Pass("广播：每个人的描述其他 3 人都收到了");
                else Fail($"广播：只有 {heard}/4 人收到了别人的描述");

                var leakedChat = names.Count(n =>
                    InboxOf(n).Any(m => m.Contains("插话-不应被广播", StringComparison.Ordinal)));
                if (leakedChat == 0) Pass("广播：被拒绝的插话没有出现在任何人的消息流里");
                else Fail($"广播：有 {leakedChat} 人收到了本应被拒绝的插话");

                var advanced = await WaitUntilAsync(() => Payload(StateOf(names[0]))?.Phase == UndercoverPhases.Vote, 15000);
                if (advanced) Pass("描述阶段：按顺序发言完毕后自动进入投票");
                else Fail("描述阶段：发言完毕后没有进入投票（仍为 " + Payload(StateOf(names[0]))?.Phase + "）");

                // ---------- 4. 投票放逐卧底 → 平民获胜 ----------
                var undercoverId = StateOf(undercover[0].Name)!.Players
                    .FirstOrDefault(p => p.IsYou)?.Id ?? -1;

                // 注意：服务端禁止投自己（反作弊），所以卧底本人弃票，其余人投卧底。
                foreach (var c in clients)
                {
                    var target = string.Equals(c.Username, undercover[0].Name, StringComparison.OrdinalIgnoreCase)
                        ? -1
                        : undercoverId;

                    await c.CallGameOpAsync(UndercoverOps.Vote,
                        JsonSerializer.SerializeToNode(new TargetPayload { TargetId = target }, Json));
                }

                var finished = await WaitUntilAsync(() =>
                {
                    var s = StateOf(names[0]);
                    return s != null && s.Status == RoomStatus.GameOver;
                }, 40000);

                if (finished) Pass("投票放逐卧底后本局结束（状态 = GameOver）");
                else Fail("投票后本局没有结束，当前状态 = " + StateOf(names[0])?.Status);

                // 结束后身份公开。
                var final = StateOf(names[0]);
                var revealed = final?.Players.Count(p => p.Tag != null) ?? 0;
                if (revealed == 4) Pass("结算：本局结束后所有玩家的身份都已公开");
                else Fail($"结算：结束后只公开了 {revealed}/4 个身份");

                // 结算后才下发真实身份（局中一律保密）。
                var revealedRoles = names.Count(n => Payload(StateOf(n)) is { YourRoleKnown: true }
                                                     && Payload(StateOf(n))!.YourRole.Length > 0);
                if (revealedRoles == 4) Pass("结算：4 份负载都下发了真实身份（YourRoleKnown=true）");
                else Fail($"结算：只有 {revealedRoles}/4 份负载下发了真实身份");

                var campCount = names.Count(n =>
                {
                    var p = Payload(StateOf(n));
                    return p != null && ((p.YourRole == UndercoverRoles.Undercover) == (p.YourWord == undercoverWord));
                });
                if (campCount == 4) Pass("结算：揭晓的身份与手上的词一致");
                else Fail($"结算：有 {4 - campCount} 人的身份与词对不上");
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
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
        => await WaitUntilAsync(condition, timeoutMs);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(120);
        }
        return condition();
    }
}
