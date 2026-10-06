using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Server.Games.Undercover;
using UcOps = Werewolf.Client.Share.UndercoverOps;

namespace ClientGUI.Tests;

/// <summary>
/// 谁是卧底**白板**端到端校验（<c>--ucblank &lt;url&gt;</c>，QA 新增）。
///
/// 原有的 <c>--undercover</c> 是 4 人局，永远出不了白板，因此白板这条链路在端到端层面是空的。
/// 这里补一条 **7 人局**（<c>BlankMinPlayers=7</c> → 2 卧底 + 1 白板 + 4 平民）的完整对局：
/// <list type="bullet">
///   <item>开局真的发出了 1 个白板，且白板视角 <c>HasWord=false</c>、词为空；</item>
///   <item>局中**任何时刻**都没有人能从自己的负载里读出本人阵营（含持续监控玩家行 Tag）；</item>
///   <item>出局者的身份当场公开、存活者仍然保密；</item>
///   <item>一路打到 GameOver，验收结算揭晓把白板算进卧底阵营后的身份分布。</item>
/// </list>
/// 建议用小局配置（Undercover:DescribeSeconds / VoteSeconds 调小）。
/// </summary>
public static class UndercoverBlankProbe
{
    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<ProbeResult> RunAsync(string url, int playerCount = 7)
    {
        var notes = new List<string>();
        var ok = true;

        void Pass(string what) => notes.Add("✅ " + what);
        void Fail(string what) { ok = false; notes.Add("❌ " + what); }

        var clients = new List<GameClient>();
        var states = new Dictionary<string, GameStateDto>();
        var gate = new object();

        // 局中任何时候只要有人看到「存活玩家的 Tag 非空」或「自己的 YourRole 非空」就记账。
        var privacyBreaches = new List<string>();

        async Task<GameClient?> NewClientAsync(string name)
        {
            var client = new GameClient();
            client.StateReceived += s =>
            {
                lock (gate)
                {
                    states[name] = s;
                    Audit(s, name);
                }
            };
            clients.Add(client);
            await client.ConnectAsync(url);
            var login = await client.LoginAsync(name, "pw" + name);
            if (!login.ok) { Fail($"{name} 登录失败：{login.msg}"); return null; }
            return client;
        }

        // ---- 逐条 + 逐字段地审计一份「某玩家视角」的状态 ----
        void Audit(GameStateDto s, string who)
        {
            var payload = Payload(s);
            if (payload == null) return;

            // 整局结束后一切揭晓，不再要求保密。
            if (payload.YourRoleKnown) return;

            if (payload.YourRole.Length > 0)
                lock (privacyBreaches) privacyBreaches.Add($"{who}：局中 YourRole 已泄露 = {payload.YourRole}");

            foreach (var word in new[] { "卧底", "平民", "白板" })
            {
                if (payload.YourRoleDesc.Contains(word, StringComparison.Ordinal))
                    lock (privacyBreaches)
                        privacyBreaches.Add($"{who}：局中 YourRoleDesc 含「{word}」= {payload.YourRoleDesc}");
            }

            foreach (var row in s.Players)
            {
                if (row.IsAlive && row.Tag != null)
                    lock (privacyBreaches)
                        privacyBreaches.Add($"{who}：局中看到存活玩家 {row.Name} 的 Tag = {row.Tag}");
            }
        }

        GameStateDto? StateOf(string name)
        {
            lock (gate) return states.TryGetValue(name, out var s) ? s : null;
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
            var names = Enumerable.Range(0, playerCount).Select(i => $"ucb{i}").ToArray();

            foreach (var n in names)
                if (await NewClientAsync(n) == null)
                    return new ProbeResult { Ok = false, Notes = notes };

            var host = ClientOf(names[0])!;
            Pass($"{playerCount} 个客户端连接并登录成功");

            var created = await host.CreateRoomAsync("白板测试房", null, GameIds.Undercover);
            if (!created.Ok)
            {
                Fail("建房失败：" + created.Message);
                return new ProbeResult { Ok = false, Notes = notes };
            }

            foreach (var n in names.Skip(1))
                await ClientOf(n)!.JoinRoomAsync(created.RoomId, null);

            await WaitUntilAsync(() => names.All(n => StateOf(n) != null), 10000);

            // ---------- 开局 ----------
            var started = await host.StartGameAsync();
            if (!started.ok)
            {
                Fail("开局失败：" + started.msg);
                return new ProbeResult { Ok = false, Notes = notes };
            }
            Pass("开局成功");

            await WaitUntilAsync(() => names.All(n => Payload(StateOf(n)) != null), 12000);

            var dealt = names.Select(n => (Name: n, P: Payload(StateOf(n)))).ToList();
            if (dealt.Any(d => d.P == null))
            {
                Fail("开局后有客户端没拿到游戏负载");
                return new ProbeResult { Ok = false, Notes = notes };
            }

            // ---------- 1. 真的发出了一个白板 ----------
            var blanks = dealt.Where(d => !d.P!.HasWord).ToList();
            var expectedBlank = playerCount >= 10 ? 2 : playerCount >= 7 ? 1 : 0;
            if (blanks.Count == expectedBlank)
                Pass($"发牌：{playerCount} 人局恰好 {expectedBlank} 个白板（{(blanks.Count > 0 ? string.Join("、", blanks.Select(b => b.Name)) : "无")}）");
            else
                Fail($"发牌：白板数量应为 {expectedBlank}，实际 {blanks.Count}");

            foreach (var b in blanks)
            {
                if (b.P!.YourWord.Length == 0) Pass($"白板 {b.Name} 的词为空且 HasWord=false");
                else Fail($"白板 {b.Name} 竟然有词：{b.P!.YourWord}");
            }

            // ---------- 2. 词分布 ----------
            var holders = dealt.Where(d => d.P!.HasWord).ToList();
            var camps = holders.GroupBy(d => d.P!.YourWord, StringComparer.Ordinal)
                               .OrderByDescending(g => g.Count())
                               .ToList();
            if (camps.Count == 2)
            {
                var majority = camps[0];
                var minority = camps[1];
                Pass($"发牌：多数词「{majority.Key}」×{majority.Count()} vs 少数词「{minority.Key}」×{minority.Count()}");

                var expected = UndercoverGameInstance.ComputeRoleCounts(playerCount);
                if (minority.Count() == expected.Undercover)
                    Pass($"发牌：卧底数 {minority.Count()} 与 ComputeRoleCounts({playerCount}) 一致");
                else
                    Fail($"发牌：卧底数应为 {expected.Undercover}，实际 {minority.Count()}");

                var civilian = majority.Count();
                if (civilian > minority.Count() + blanks.Count)
                    Pass($"铁律：平民 {civilian} 严格多于卧底阵营 {minority.Count() + blanks.Count}（含白板）");
                else
                    Fail($"铁律被打破：平民 {civilian} 未多于卧底阵营 {minority.Count() + blanks.Count}");
            }
            else
            {
                Fail($"发牌：有词的人里出现了 {camps.Count} 种词（应为 2 种）");
            }

            // ---------- 3. 局中没人知道自己阵营 ----------
            var roleLeaked = dealt.Count(d => d.P!.YourRole.Length > 0 || d.P!.YourRoleKnown);
            if (roleLeaked == 0) Pass($"保密：开局 {playerCount} 份负载都没有本人身份");
            else Fail($"保密：有 {roleLeaked} 人在局中就拿到了自己的身份");

            var descLeaked = dealt.Count(d =>
                d.P!.YourRoleDesc.Contains("卧底", StringComparison.Ordinal)
                || d.P!.YourRoleDesc.Contains("平民", StringComparison.Ordinal));
            if (descLeaked == 0) Pass("保密：身份说明文案不含「卧底 / 平民」字样");
            else Fail($"保密：有 {descLeaked} 人的身份说明文案泄露了阵营");

            // 反向：任何人的负载里都不许出现**另一个阵营的词**。
            if (camps.Count == 2)
            {
                var majorityWord = camps[0].Key;
                var minorityWord = camps[1].Key;
                var foreign = 0;
                foreach (var d in dealt)
                {
                    var mine = d.P!.YourWord;
                    var other = mine == majorityWord ? minorityWord
                              : mine == minorityWord ? majorityWord
                              : null;
                    if (other == null) continue;   // 白板没有自己的词，谈不上「别人的词」
                    if (StateOf(d.Name)!.GamePayload!.ToJsonString().Contains(other, StringComparison.Ordinal))
                        foreign++;
                }
                if (foreign == 0) Pass("隔离：没有任何人的负载里出现另一个阵营的词");
                else Fail($"隔离：有 {foreign} 人的负载里出现了对方的词");
            }

            // 公开信息：白板数量要公布（只含数量、不指向任何人）。
            var summary = dealt[0].P!.RoleSummary;
            if (blanks.Count > 0)
            {
                if (summary.Contains($"白板×{blanks.Count}", StringComparison.Ordinal))
                    Pass($"公开：身份配置文案公布了白板数量（{summary}）");
                else
                    Fail($"公开：身份配置文案没有白板数量 —— {summary}");
            }

            var startTags = names.Sum(n => StateOf(n)?.Players.Count(p => p.Tag != null) ?? 0);
            if (startTags == 0) Pass("保密：开局玩家列表没有暴露任何人的身份");
            else Fail($"保密：开局就有 {startTags} 个身份标签被下发");

            // ---------- 4. 一路打到 GameOver ----------
            var over = false;
            for (var round = 0; round < 8 && !over; round++)
            {
                await FastForwardDescribeAsync(names, ClientOf, StateOf, Payload);
                if (!await WaitUntilAsync(() => Payload(StateOf(names[0]))?.Phase == UndercoverPhases.Vote, 20000))
                {
                    over = Payload(StateOf(names[0]))?.Phase == UndercoverPhases.GameOver;
                    break;
                }

                var victim = PickVictim(StateOf(names[0]));
                if (victim < 0) break;

                foreach (var n in names)
                {
                    var row = StateOf(n)?.Players.FirstOrDefault(p => p.IsYou);
                    if (row == null || !row.IsAlive) continue;

                    var target = row.Id == victim ? -1 : victim;   // 不能投自己
                    await ClientOf(n)!.CallGameOpAsync(UcOps.Vote,
                        JsonSerializer.SerializeToNode(new TargetPayload { TargetId = target }, Json));
                }

                await WaitUntilAsync(() =>
                {
                    var s = StateOf(names[0]);
                    return s != null && (s.Status == RoomStatus.GameOver
                                         || Payload(s)?.Phase == UndercoverPhases.GameOver);
                }, 25000);

                var s2 = StateOf(names[0]);
                over = s2 != null && (s2.Status == RoomStatus.GameOver
                                      || Payload(s2)?.Phase == UndercoverPhases.GameOver);
            }

            if (over) Pass($"流程：{playerCount} 人局一路打到结算（GameOver）");
            else Fail("流程：对局没能走到结算阶段");

            if (!over) return new ProbeResult { Ok = ok, Notes = notes };

            // ---------- 5. 结算揭晓 ----------
            var final = StateOf(names[0]);
            var finalPayload = Payload(final);

            var revealedTags = final?.Players.Count(p => p.Tag != null) ?? 0;
            if (revealedTags == playerCount) Pass($"结算：{playerCount} 名玩家的身份全部公开");
            else Fail($"结算：只公开了 {revealedTags}/{playerCount} 个身份");

            var roles = names.Select(n => Payload(StateOf(n))).ToList();
            var known = roles.Count(p => p is { YourRoleKnown: true } && p!.YourRole.Length > 0);
            if (known == playerCount) Pass($"结算：{playerCount} 份负载都下发了本人真实身份");
            else Fail($"结算：只有 {known}/{playerCount} 份负载下发了真实身份");

            var allRoles = roles.Where(p => p != null).Select(p => p!.YourRole).ToList();
            var count = new Dictionary<string, int>
            {
                [UndercoverRoles.Undercover] = allRoles.Count(r => r == UndercoverRoles.Undercover),
                [UndercoverRoles.Blank] = allRoles.Count(r => r == UndercoverRoles.Blank),
                [UndercoverRoles.Civilian] = allRoles.Count(r => r == UndercoverRoles.Civilian)
            };
            var expect = UndercoverGameInstance.ComputeRoleCounts(playerCount);
            if (count[UndercoverRoles.Undercover] == expect.Undercover
                && count[UndercoverRoles.Blank] == expect.Blank
                && count[UndercoverRoles.Civilian] == playerCount - expect.Undercover - expect.Blank)
                Pass($"结算：身份分布与规格一致 " +
                     $"（🕵️×{count[UndercoverRoles.Undercover]}、⬜×{count[UndercoverRoles.Blank]}、" +
                     $"👤×{count[UndercoverRoles.Civilian]}）");
            else
                Fail($"结算：身份分布不符 —— 🕵️{count[UndercoverRoles.Undercover]}/" +
                     $"⬜{count[UndercoverRoles.Blank]}/👤{count[UndercoverRoles.Civilian]}，" +
                     $"期望 🕵️{expect.Undercover}/⬜{expect.Blank}/👤{playerCount - expect.Undercover - expect.Blank}");

            // 白板本人：结算后才确认自己是白板，且始终没有词。
            foreach (var n in names)
            {
                var p = Payload(StateOf(n));
                if (p == null) continue;
                var row = StateOf(n)?.Players.FirstOrDefault(x => x.IsYou);
                if (p.YourRole == UndercoverRoles.Blank)
                {
                    if (p.YourWord.Length == 0 && !p.HasWord) Pass($"结算：白板 {n} 始终没有词");
                    else Fail($"结算：白板 {n} 竟然有词 {p.YourWord}");
                }
                if (row != null && row.Tag == p.YourRole)
                    continue;
                Fail($"结算：{n} 的身份标签({row?.Tag}) 与其本人负载({p.YourRole}) 不一致");
                break;
            }
            if (roles.Where(p => p != null).All(p => p!.YourRoleKnown))
                Pass("结算：每行玩家列表的身份与本人负载里的身份逐一核对一致");

            // ---------- 6. 全程隐私监控 ----------
            lock (privacyBreaches)
            {
                if (privacyBreaches.Count == 0)
                    Pass("全程：整局过程中没有任何一帧状态泄露本人身份或存活玩家的阵营");
                else
                    Fail($"全程：发现 {privacyBreaches.Count} 例泄露 → " +
                         string.Join("；", privacyBreaches.Take(4)));
            }

            if (finalPayload != null && finalPayload.YourRoleKnown)
                Pass("结算：负载上「身份已揭晓」标记正确置位");
        }
        catch (Exception ex)
        {
            Fail("异常：" + ex);
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

    /// <summary>挑一个还没出局的、且不是第 0 位玩家的目标（保证每轮都能出人）。</summary>
    private static int PickVictim(GameStateDto? state)
    {
        if (state == null) return -1;
        var candidate = state.Players.FirstOrDefault(p => p.IsAlive && p.Id != 0);
        return candidate?.Id ?? -1;
    }

    /// <summary>
    /// 描述阶段快进：轮到谁，谁就点「跳过本轮发言」，直到描述阶段结束。
    /// 这样不用真的等满 DescribeSeconds，又能把「轮流发言」这条链路走完。
    /// </summary>
    private static async Task FastForwardDescribeAsync(
        string[] names,
        Func<string, GameClient?> clientOf,
        Func<string, GameStateDto?> stateOf,
        Func<GameStateDto?, UndercoverGameState?> payload)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(40000);
        var spoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (DateTime.UtcNow < deadline)
        {
            var hostState = stateOf(names[0]);
            var p = payload(hostState);
            if (p == null) break;
            if (p.Phase == UndercoverPhases.Waiting) { await Task.Delay(100); continue; }
            if (p.Phase != UndercoverPhases.Describe) return;

            if (p.CurrentSpeakerName.Length == 0) { await Task.Delay(100); continue; }

            var speaker = names.FirstOrDefault(n =>
                string.Equals(n, p.CurrentSpeakerName, StringComparison.OrdinalIgnoreCase));
            if (speaker == null || !spoken.Add(p.CurrentSpeakerName + "|" + p.Round))
            {
                await Task.Delay(100);
                continue;
            }

            await clientOf(speaker)!.CallGameOpAsync(UcOps.Skip,
                JsonSerializer.SerializeToNode(new TargetPayload { TargetId = 1 }, Json));

            await WaitUntilAsync(() =>
            {
                var cur = payload(stateOf(names[0]));
                return cur == null
                       || cur.Phase != UndercoverPhases.Describe
                       || !string.Equals(cur.CurrentSpeakerName, speaker, StringComparison.OrdinalIgnoreCase);
            }, 8000);
        }
    }

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
