using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Undercover;
using UcOps = Werewolf.Client.Share.UndercoverOps;

namespace ClientGUI.Tests;

/// <summary>
/// QA **独立**复核（v2.3.2）：隐藏本人阵营 + 白板身份。
///
/// 与工程师的 <c>UndercoverTests</c> 刻意正交——那边是「按词出现的次数反推阵营」，
/// 这里是另外四条互相独立的证据链：
/// <list type="bullet">
///   <item>穷举人数 × 多种阈值配置，把 <c>ComputeRoleCounts</c> 当纯函数验规格；</item>
///   <item>**整份 <see cref="GameStateDto"/>**（不是只有 GamePayload）逐字段扫：
///         除白名单字段外，任何字符串都不许出现阵营字样；</item>
///   <item>用假 <see cref="IGameContext"/> 抓开局**私聊文案**，验「平民 / 卧底两条路径文案一致」；</item>
///   <item>真实对局里验存活者 Tag 保密、出局者 Tag 公开、整局结束后全部揭晓。</item>
/// </list>
/// </summary>
public static class UndercoverQaAudit
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>阵营关键字：局中除白名单字段外一律不许出现。</summary>
    private static readonly string[] CampWords = { "卧底", "平民", "白板" };

    /// <summary>允许出现阵营字样的字段路径（游戏名 / 开局公开的身份配置）。</summary>
    private static readonly string[] AllowedPaths = { "$.GameName", "$.GamePayload.RoleSummary" };

    public static void RunAll()
    {
        RoleCountsExhaustive();
        WholeDtoCarriesNoCampInfo();
        PrivateWordTextIsCampNeutral();
        TagPolicyInRealGame();
    }

    // ==================== 1. ComputeRoleCounts 穷举 ====================

    /// <summary>把纯函数当规格来验：人数 × 阈值全排列，四条不变式逐条查。</summary>
    private static void RoleCountsExhaustive()
    {
        var configs = new (string Name, UndercoverRules Rules)[]
        {
            ("默认(7/10)", new UndercoverRules()),
            ("降到 3/4", new UndercoverRules { BlankMinPlayers = 3, BlankTwoMinPlayers = 4 }),
            ("关闭白板(0/0)", new UndercoverRules { BlankMinPlayers = 0, BlankTwoMinPlayers = 0 }),
            ("阈值 999", new UndercoverRules { BlankMinPlayers = 999, BlankTwoMinPlayers = 999 }),
            ("阈值反向(3/2)", new UndercoverRules { BlankMinPlayers = 3, BlankTwoMinPlayers = 2 }),
            ("1 人即白板(1/2)", new UndercoverRules { BlankMinPlayers = 1, BlankTwoMinPlayers = 2 })
        };

        foreach (var (name, rules) in configs)
        {
            for (var n = 3; n <= 12; n++)
            {
                var (u, b) = UndercoverGameInstance.ComputeRoleCounts(n, rules);
                var c = n - u - b;

                Check.True(u >= 1, $"规格[{name}] {n} 人：卧底至少 1 个（实际 {u}）");
                Check.True(b >= 0, $"规格[{name}] {n} 人：白板数不为负（实际 {b}）");
                Check.Equal(n, u + b + c, $"规格[{name}] {n} 人：三段之和等于人数");
                Check.True(c > u + b,
                    $"规格[{name}] {n} 人：铁律 平民({c}) > 卧底+白板({u + b}) [卧底{u}/白板{b}]");
                Check.True(2 * (u + b) < n,
                    $"规格[{name}] {n} 人：卧底阵营严格不足半数（{u + b} / {n}）");
            }
        }

        // 少于 MinPlayers 时 Start 会被直接拒绝：1 / 2 人局虽然 ComputeRoleCounts 不成立，
        // 但必须被人数门禁挡住，否则就会出现「开局即结束」的无意义对局。
        var (u3, b3) = UndercoverGameInstance.ComputeRoleCounts(3);
        Check.Equal(1, u3, "边界：3 人局 1 卧底");
        Check.Equal(0, b3, "边界：3 人局没有白板");

        // 文档化的取舍：10 人局 = 2 卧底 + 2 白板 + 6 平民。
        var (u10, b10) = UndercoverGameInstance.ComputeRoleCounts(10);
        Check.Equal(2, u10, "取舍：10 人局 2 名卧底");
        Check.Equal(2, b10, "取舍：10 人局 2 个白板");
        Check.Equal(6, 10 - u10 - b10, "取舍：10 人局 6 名平民");

        // 白板数量随人数递增且不超过 2。
        var prev = 0;
        for (var n = 3; n <= 12; n++)
        {
            var (_, b) = UndercoverGameInstance.ComputeRoleCounts(n);
            Check.True(b >= prev, $"单调：{n} 人局白板数({b}) 不少于上一档({prev})");
            Check.True(b <= 2, $"上限：{n} 人局白板数({b}) 不超过 2");
            prev = b;
        }

        // 开局门禁：少于 MinPlayers（默认 3）会被 Start 直接拒绝，所以 1 / 2 人局不可达。
        var dir = Path.Combine(Path.GetTempPath(), $"uc_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");
        try
        {
            foreach (var n in new[] { 1, 2 })
            {
                var room = NewRoom(wordFile, n, describeSeconds: 1, voteSeconds: 1);
                var (ok, _) = room.StartGame("conn-host");
                Check.False(ok, $"门禁：{n} 人局无法开局（ ComputeRoleCounts 对 {n} 人不满足铁律，必须被门禁挡住）");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 2. 整份 DTO 逐字段扫描 ====================

    /// <summary>
    /// 10 人局（含 2 白板）开局后，把**整份** GameStateDto 序列化，逐字段扫：
    /// 除白名单外任何字符串都不许带阵营字样。这一步覆盖 YourWord / CategoryHint /
    /// Tag / TagColor / CurrentSpeaker* / SpokenNames / Rules 等全部通用与私有字段。
    /// </summary>
    private static void WholeDtoCarriesNoCampInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            var room = NewRoom(wordFile, 10, describeSeconds: 1, voteSeconds: 1);
            var (ok, msg) = room.StartGame("conn-host");
            Check.True(ok, "全字段扫描：10 人局开局成功（" + msg + "）");
            if (!ok) return;

            var conns = Enumerable.Range(0, 10).Select(i => i == 0 ? "conn-host" : $"conn-{i}").ToList();

            var dtoNodes = new List<JsonNode>();
            var states = new List<UndercoverGameState>();
            foreach (var conn in conns)
            {
                var dto = room.SnapshotFor(conn)!;
                dtoNodes.Add(JsonSerializer.SerializeToNode(dto, Json)!);
                states.Add(dto.GamePayload!.Deserialize<UndercoverGameState>(Json)!);
            }

            // —— 逐字段扫描 ——
            var offenders = new List<string>();
            foreach (var node in dtoNodes)
            {
                foreach (var (p, value) in Walk(node, "$"))
                {
                    if (AllowedPaths.Contains(p)) continue;
                    foreach (var word in CampWords)
                    {
                        if (value.Contains(word, StringComparison.Ordinal))
                            offenders.Add($"{p} = \"{value}\" 含「{word}」");
                    }
                }
            }
            Check.Equal(0, offenders.Count,
                "全字段扫描：除 GameName / RoleSummary 外没有任何字段泄露阵营" +
                (offenders.Count > 0 ? " → " + string.Join("；", offenders.Take(3)) : ""));

            // —— 白板本体 ——
            var blanks = states.Where(s => !s.HasWord).ToList();
            Check.Equal(2, blanks.Count, "全字段扫描：10 人局恰好 2 个白板（视角 HasWord=false）");
            Check.True(blanks.All(b => b.YourWord.Length == 0), "全字段扫描：白板没有词");
            Check.True(states.All(s => s.HasWord == (s.YourWord.Length > 0)),
                "全字段扫描：HasWord 与 YourWord 严格一致");

            // —— 本人阵营不得出现（含白板：局中也不知道自己是白板） ——
            Check.True(states.All(s => s.YourRole.Length == 0), "全字段扫描：局中 10 份负载 YourRole 全为空");
            Check.True(states.All(s => !s.YourRoleKnown), "全字段扫描：局中 10 份负载 YourRoleKnown 全为 false");
            Check.True(states.All(s => s.YourRoleDesc == UndercoverText.HiddenRoleDesc),
                "全字段扫描：局中统一使用共享常量 HiddenRoleDesc（客户端无法据此推断阵营）");
            Check.True(states.All(s => !CampWords.Any(w => s.YourRoleDesc.Contains(w, StringComparison.Ordinal))),
                "全字段扫描：YourRoleDesc 不含任何阵营字样");

            // —— 玩家列表：开局 Tag 必须全为空 ——
            var tags = dtoNodes.SelectMany(n => Walk(n, "$.Players").Where(kv => kv.Path.EndsWith(".Tag", StringComparison.Ordinal))).ToList();
            Check.Equal(0, tags.Count, "全字段扫描：开局没有任何人的身份标签被下发（Tag 全为 null）");

            // —— 反向：任何人的负载里都不许出现**别人的词** ——
            var civilianWord = "苹果";
            var undercoverWord = "梨";
            var civilians = states.Count(s => s.YourWord == civilianWord);
            var spies = states.Count(s => s.YourWord == undercoverWord);
            Check.Equal(6, civilians, "全字段扫描：6 名平民拿到同一个词");
            Check.Equal(2, spies, "全字段扫描：2 名卧底拿到另一个词");

            for (var i = 0; i < states.Count; i++)
            {
                var mine = states[i].YourWord;
                var other = mine == civilianWord ? undercoverWord
                          : mine.Length == 0 ? civilianWord
                          : null;
                if (other != null)
                {
                    Check.False(dtoNodes[i].ToJsonString().Contains(other, StringComparison.Ordinal),
                        $"隔离：第 {i} 位玩家（词={mine}）的整份负载里看不到另一个词「{other}」");
                }
                if (mine.Length > 0)
                {
                    Check.False(dtoNodes[i].ToJsonString().Contains("（无词）", StringComparison.Ordinal),
                        $"隔离：第 {i} 位玩家的负载里不出现结算专用占位文案");
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 3. 开局私聊文案 ====================

    /// <summary>
    /// 用假 <see cref="IGameContext"/> 抓住 <c>SendWordAsync</c> 发给每个人的私聊文案：
    /// ①所有「有词的人」的文案模板必须**逐字相同**（把各自的词抠掉后）——
    ///   否则措辞不同就是一种暗示；②平民与卧底的文案里都不得出现阵营字样；
    /// ③任何人的私聊里都不许出现**另一个阵营的词**。
    /// </summary>
    private static void PrivateWordTextIsCampNeutral()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            const int n = 10;
            var rules = new UndercoverRules
            {
                WordFile = wordFile,
                DescribeSeconds = 1,
                VoteSeconds = 1
            };
            var ctx = new RecordingContext(rules);
            for (var i = 0; i < n; i++)
                ctx.Roster.Add(new RoomPlayer { Username = "u" + i, ConnectionId = "c" + i, IsHost = i == 0 });

            var game = new UndercoverGameInstance(new UndercoverGameModule().Info, ctx);
            var (ok, msg) = game.Start();
            Check.True(ok, "私聊文案：10 人局开局成功（" + msg + "）");
            if (!ok) return;

            // 逐个触发「重连补发」里的同一条私聊分支（SendWordAsync）。
            foreach (var p in ctx.Roster) game.OnPlayerReconnected(p);
            Thread.Sleep(200);

            Check.Equal(n, ctx.Whispers.Count, "私聊文案：开局给每个人都补发了自己的词");

            var words = ctx.Roster.Select(p =>
                game.BuildPayload(p.ConnectionId) is JsonObject jo
                    ? jo.Deserialize<UndercoverGameState>(Json)!.YourWord
                    : null).ToList();

            var held = new List<string>();          // 有词的人抠掉自己的词之后的模板
            var blankTexts = new List<string>();    // 白板的文案
            for (var i = 0; i < ctx.Roster.Count; i++)
            {
                var conn = ctx.Roster[i].ConnectionId;
                var text = ctx.Whispers.FirstOrDefault(w => w.Conn == conn).Text;
                if (text == null) { Check.True(false, $"私聊文案：u{i} 没有收到私聊"); continue; }

                var myWord = words[i] ?? "";
                if (myWord.Length > 0)
                {
                    held.Add(text.Replace(myWord, "{W}"));
                    Check.True(text.Contains(myWord, StringComparison.Ordinal),
                        $"私聊文案：u{i} 收到了自己的词");

                    var foreign = myWord == "苹果" ? "梨" : "苹果";
                    Check.False(text.Contains(foreign, StringComparison.Ordinal),
                        $"隔离：u{i} 的私聊里没有出现另一个词「{foreign}」");

                    foreach (var word in CampWords)
                        Check.False(text.Contains(word, StringComparison.Ordinal),
                            $"保密：u{i} 的私聊文案不含「{word}」字样");
                }
                else
                {
                    blankTexts.Add(text);
                    foreach (var word in new[] { "苹果", "梨" })
                        Check.False(text.Contains(word, StringComparison.Ordinal),
                            $"隔离：白板 u{i} 的私聊里不包含任何具体的词");
                }
            }

            Check.Equal(8, held.Count, "私聊文案：8 个有词的人收到「你的词是…」");
            Check.Equal(2, blankTexts.Count, "私聊文案：2 个白板收到「你是白板…」");

            // 关键：平民与卧底的文案模板必须逐字一致（措辞不同 = 暗示）。
            Check.Equal(1, held.Distinct(StringComparer.Ordinal).Count(),
                "保密：所有拿到词的人（平民与卧底）收到的私聊模板逐字相同");
            Check.True(held.Count > 0 && held[0].Contains("{W}", StringComparison.Ordinal),
                "保密：有词者的文案确实嵌入了自己的词");

            // 白板文案依法提到「白板」（它必然知道自己没词），但不得顺带说出卧底 / 平民或任何词。
            Check.True(blankTexts.All(t => t.Contains(UndercoverRoles.Blank, StringComparison.Ordinal)),
                "白板：开局明确告知「你是白板」");
            Check.True(blankTexts.All(t => !t.Contains("卧底", StringComparison.Ordinal)
                                           && !t.Contains("平民", StringComparison.Ordinal)),
                "白板：白板文案不提「卧底 / 平民」（不替它判断阵营归属）");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 4. 真实对局里的 Tag 策略 ====================

    /// <summary>
    /// 8 人局（2 卧底 + 1 白板 + 5 平民）真实跑到结束。
    /// 选 8 人而不是 7 人：7 人放逐 1 名平民后会立刻 3:3 判定卧底阵营胜而结算，
    /// 「存活者仍保密」这一条就没机会验；8 人（卧底阵营 3 : 平民 5）放逐一人必定还在局中。
    /// 开局无 Tag → 投票放逐一人后**只有出局者**有 Tag、存活者仍然没有 →
    /// 整局结束后全部揭晓，且每人 Tag 与自己那份负载里的 YourRole 一致。
    /// </summary>
    private static void TagPolicyInRealGame()
    {
        const int n = 8;
        var dir = Path.Combine(Path.GetTempPath(), $"uc_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            var room = NewRoom(wordFile, n, describeSeconds: 1, voteSeconds: 1);
            var conns = Enumerable.Range(0, n).Select(i => i == 0 ? "conn-host" : $"conn-{i}").ToList();

            // 发牌之前：房间里还没有任何人的身份，列表里就不该出现身份标签。
            // （v2.3.2 加固 1：曾因 DecoratePlayers 的 revealAll = !_running 在 Waiting 阶段为真，
            //  而 _up 里的人是 SnapshotPlayers 用默认角色「平民」补出来的，
            //  导致未开局时每人标签都写着「平民」。现已用「是否已发牌」标志修掉。）
            var preTags = SnapshotTags(room, conns);
            var preCount = preTags.Sum(v => v.Count(t => t.Tag != null));
            Check.Equal(0, preCount,
                "发牌前：未开局时玩家列表不带任何身份标签（不能写成默认的「平民」）" +
                (preCount > 0 ? $" → 仍有 {preCount} 行（第一行 = 「{preTags[0][0].Tag}」）" : ""));

            var (ok, msg) = room.StartGame(conns[0]);
            Check.True(ok, "真实对局：8 人局开局成功（" + msg + "）");
            if (!ok) return;

            // 开局：谁的 Tag 都不能有（注意：要数「Tag 非空的行」，不是总行数）。
            var startTags = SnapshotTags(room, conns);
            Check.Equal(0, startTags.Sum(v => v.Count(t => t.Tag != null)),
                "真实对局：开局玩家列表不带任何身份标签");
            Check.Equal(n * n, startTags.Sum(v => v.Count),
                "真实对局：开局每个视角都能看到完整的 n 行玩家列表");

            // 跑到第一次投票结束（跳过描述，加快节奏）。
            var firstOut = DriveOneRound(room, conns);
            Check.True(firstOut >= 0, "真实对局：一整套「描述 → 投票」后可以决出一名出局者");
            if (firstOut < 0) return;

            // 出局者公开、存活者仍然保密。
            var midTags = SnapshotTags(room, conns);
            var any = midTags[0];
            Check.True(any.Any(t => !t.IsAlive && t.Tag != null),
                "真实对局：出局者的身份标签已公开");
            Check.True(any.Where(t => t.IsAlive).All(t => t.Tag == null),
                "真实对局：存活者的身份标签仍然保密");

            // 出局者身份必须对每个视角一致，且是三种合法身份之一。
            var legal = new[] { UndercoverRoles.Civilian, UndercoverRoles.Undercover, UndercoverRoles.Blank };
            Check.True(midTags.All(view => view.All(t => t.IsAlive || legal.Contains(t.Tag))),
                "真实对局：公开的身份标签是合法的三种之一");
            Check.Equal(1, any.Count(t => t.Tag != null), "真实对局：此刻只有出局者那一行有标签");

            // 一路跑到整局结束。
            var over = false;
            for (var guard = 0; guard < 8 && !over; guard++)
            {
                DriveOneRound(room, conns);
                over = WaitUntil(() =>
                {
                    var dto = room.SnapshotFor(conns[0]);
                    return dto != null && dto.Status == RoomStatus.GameOver;
                }, 20000);
            }
            Check.True(over, "真实对局：8 人局可以正常走到 GameOver");
            if (!over) return;

            // 结算后：全部揭晓，且与本人负载里的 YourRole 一致。
            var finalTags = SnapshotTags(room, conns);
            Check.True(finalTags.All(v => v.All(t => t.Tag != null)),
                "揭晓：整局结束后所有玩家的身份标签都已公开");

            var roles = conns.Select(c => PayloadOf(room, c)).ToList();
            Check.True(roles.All(p => p != null && p.YourRoleKnown && p.YourRole.Length > 0),
                "揭晓：结算后才下发本人真实身份");

            for (var i = 0; i < conns.Count; i++)
            {
                var mine = finalTags[i].FirstOrDefault(t => t.Id == i);
                Check.True(mine != null && mine.Tag == roles[i]!.YourRole,
                    $"揭晓：第 {i} 位玩家的身份标签与其本人负载一致（{mine?.Tag} / {roles[i]!.YourRole}）");
            }

            var all = roles.Select(p => p!.YourRole).ToList();
            Check.Equal(2, all.Count(r => r == UndercoverRoles.Undercover), "揭晓：8 人局 2 名卧底");
            Check.Equal(1, all.Count(r => r == UndercoverRoles.Blank), "揭晓：8 人局 1 个白板");
            Check.Equal(5, all.Count(r => r == UndercoverRoles.Civilian), "揭晓：8 人局 5 名平民");

            // 白板：没有词，且身份与词一致（卧底拿另一个词、平民拿同一个词）。
            var pairs = roles.Select((p, i) => (Role: p!.YourRole, Word: p.YourWord)).ToList();
            Check.True(pairs.All(x => (x.Role == UndercoverRoles.Blank) == (x.Word.Length == 0)),
                "揭晓：只有白板没有词");
            Check.True(pairs.All(x => x.Role == UndercoverRoles.Blank || x.Word is "苹果" or "梨"),
                "揭晓：有词的人拿到的都是词库里的词");
            Check.True(pairs.Count(x => x.Word == "苹果") == 5 && pairs.Count(x => x.Word == "梨") == 2,
                "揭晓：1 个白板使两支队伍的词分布为 5 / 2");

            // ---- 同一房间**再开一局**：新一局的身份必须重新保密起来 ----
            var (again, againMsg) = room.StartGame(conns[0]);
            Check.True(again, "重开：同一房间可以接着再开一局（" + againMsg + "）");
            if (again)
            {
                Thread.Sleep(400);   // 让第二轮 RunAsync 起来并推一次状态

                var reTags = SnapshotTags(room, conns);
                Check.Equal(0, reTags.Sum(v => v.Count(t => t.Tag != null)),
                    "重开：新一局发牌后立即又开始保密（不能沿用上一局已揭晓的身份）");

                var reRoles = conns.Select(c => PayloadOf(room, c)).ToList();
                Check.True(reRoles.All(p => p != null && p.YourRole.Length == 0 && !p.YourRoleKnown),
                    "重开：新一局没人能在开局拿到自己的身份");
                Check.True(reRoles.All(p => p != null && p.YourRoleDesc == UndercoverText.HiddenRoleDesc),
                    "重开：新一局身份文案回到「保密」版本");

                var reBlanks = reRoles.Count(p => p != null && !p.HasWord);
                Check.Equal(UndercoverGameInstance.ComputeRoleCounts(n).Blank, reBlanks,
                    "重开：新一局重新按人数发出白板");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 辅助 ====================

    private sealed record TagRow(int Id, string Name, bool IsAlive, string? Tag);

    /// <summary>取每个视角看到的玩家列表（含 Tag），返回「每个连接一份」。</summary>
    private static List<List<TagRow>> SnapshotTags(GameRoom room, List<string> conns)
        => conns.Select(c =>
        {
            var dto = room.SnapshotFor(c);
            return dto?.Players.Select(p => new TagRow(p.Id, p.Name, p.IsAlive, p.Tag)).ToList()
                   ?? new List<TagRow>();
        }).ToList();

    /// <summary>
    /// 驱动一整轮：描述阶段按「当前发言人」逐个跳过（快进），等进入投票阶段后让所有人
    /// 集中投给同一名玩家（自己不能投自己），返回本轮出局者的编号（-1 表示没决出）。
    /// </summary>
    private static int DriveOneRound(GameRoom room, List<string> conns)
    {
        // ---- 描述阶段：轮到谁就让谁跳过 ----
        var describeDeadline = DateTime.UtcNow.AddMilliseconds(30000);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (DateTime.UtcNow < describeDeadline)
        {
            var dto = room.SnapshotFor(conns[0]);
            var payload = dto?.GamePayload?.Deserialize<UndercoverGameState>(Json);
            if (dto == null || payload == null) return -1;
            if (payload.Phase == UndercoverPhases.Waiting) { Thread.Sleep(80); continue; }
            if (payload.Phase != UndercoverPhases.Describe) break;

            if (payload.CurrentSpeakerName.Length == 0) { Thread.Sleep(80); continue; }

            var idx = dto.Players.FirstOrDefault(p => string.Equals(p.Name, payload.CurrentSpeakerName,
                StringComparison.OrdinalIgnoreCase))?.Id ?? -1;
            if (idx < 0 || !handled.Add(payload.CurrentSpeakerName)) { Thread.Sleep(80); continue; }

            room.HandleGameAction(UcOps.Skip, ToNode(new TargetPayload { TargetId = 1 }), conns[idx]);
            Thread.Sleep(80);
        }

        if (!WaitUntil(() => PayloadOf(room, conns[0])?.Phase == UndercoverPhases.Vote, 20000))
            return -1;

        // ---- 投票阶段：除目标本人外，其余人都投同一个目标 ----
        var target = FirstAliveExcept(room, conns[0], 0);
        if (target < 0) return -1;

        for (var i = 0; i < conns.Count; i++)
        {
            if (!IsAliveIndex(room, conns[0], i) || i == target) continue;
            VoteFor(room, conns[i], target);
        }

        return WaitUntil(() =>
        {
            var dto = room.SnapshotFor(conns[0]);
            if (dto == null) return false;
            return dto.Players.Any(p => !p.IsAlive)
                   || dto.Status == RoomStatus.GameOver
                   || dto.GamePayload?.Deserialize<UndercoverGameState>(Json)?.Phase == UndercoverPhases.GameOver;
        }, 20000)
            ? OutIndex(room, conns[0])
            : -1;
    }

    private static int OutIndex(GameRoom room, string conn)
    {
        var dto = room.SnapshotFor(conn);
        var dead = dto?.Players.Where(p => !p.IsAlive).Select(p => p.Id).ToList() ?? new List<int>();
        return dead.Count == 0 ? -1 : dead[^1];
    }

    private static int FirstAliveExcept(GameRoom room, string conn, int except)
    {
        var dto = room.SnapshotFor(conn);
        var alive = dto?.Players.Where(p => p.IsAlive && p.Id != except).ToList() ?? new List<PlayerDto>();
        return alive.Count == 0 ? -1 : alive[0].Id;
    }

    private static bool IsAliveIndex(GameRoom room, string conn, int id)
    {
        var dto = room.SnapshotFor(conn);
        return dto?.Players.FirstOrDefault(p => p.Id == id)?.IsAlive ?? false;
    }

    private static void VoteFor(GameRoom room, string conn, int target)
        => room.HandleGameAction(UcOps.Vote, ToNode(new TargetPayload { TargetId = target }), conn);

    private static UndercoverGameState? PayloadOf(GameRoom room, string conn)
    {
        var dto = room.SnapshotFor(conn);
        if (dto?.GamePayload == null) return null;
        try { return dto.GamePayload.Deserialize<UndercoverGameState>(Json); }
        catch { return null; }
    }

    private static JsonObject? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json) as JsonObject;

    /// <summary>遍历 JSON 树，产出 (路径, 字符串值)。</summary>
    private static IEnumerable<(string Path, string Value)> Walk(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                    foreach (var hit in Walk(kv.Value, path + "." + kv.Key))
                        yield return hit;
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    foreach (var hit in Walk(arr[i], $"{path}[{i}]"))
                        yield return hit;
                break;
            case JsonValue val:
                string? s = null;
                try { if (val.TryGetValue<string>(out var str)) s = str; } catch { s = null; }
                if (s != null) yield return (path, s);
                break;
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(80);
        }
        return condition();
    }

    private static GameRoom NewRoom(string wordFile, int playerCount,
                                    int describeSeconds = 1, int voteSeconds = 1)
    {
        var catalog = new GameCatalog();
        catalog.RegisterAll(GameDiscovery.Discover(typeof(UndercoverGameModule).Assembly));

        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };
        // 注意：**不覆盖 Undercover:MinPlayers**（让它回退到默认的 3），
        // 否则「人少不能开局」这条门禁就被测试自己关掉了。
        var config = new GameConfig(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Undercover:WordFile"] = wordFile,
            ["Undercover:DescribeSeconds"] = describeSeconds.ToString(),
            ["Undercover:VoteSeconds"] = voteSeconds.ToString()
        }).Build());

        var manager = new RoomManager(new EncryptedChannel(null!, new CryptoSessionStore()), catalog, options, config);
        manager.BindIdentity("conn-host", "host");
        var created = manager.CreateRoom("conn-host", "QA审计房", null, GameIds.Undercover);
        var room = manager.Find(created.roomId)!;

        for (var i = 1; i < playerCount; i++)
        {
            manager.BindIdentity($"conn-{i}", $"p{i}");
            room.AddOrReconnect($"p{i}", $"conn-{i}");
        }

        return room;
    }

    // ==================== 假的 IGameContext（抓私聊 / 广播） ====================

    /// <summary>
    /// 录制型上下文：把 <c>PrivateAsync</c> / <c>BroadcastAsync</c> 的文本留下来，
    /// 用来独立审视服务端到底对谁说了什么。
    /// </summary>
    private sealed class RecordingContext : IGameContext
    {
        private readonly UndercoverRules _rules;

        public RecordingContext(UndercoverRules rules)
        {
            _rules = rules;
            Config = new SingleConfig(_rules);
        }

        public List<RoomPlayer> Roster { get; } = new();

        public List<(string Conn, string Text)> Whispers { get; } = new();

        public List<string> Broadcasts { get; } = new();

        public string RoomId => "00000";

        public string RoomName => "QA审计房";

        public IReadOnlyList<RoomPlayer> Players => Roster;

        public IReadOnlyList<RoomPlayer> OnlinePlayers => Roster.Where(p => p.IsConnected).ToList();

        public RoomPlayer? FindByConnection(string connId)
            => Roster.FirstOrDefault(p => string.Equals(p.ConnectionId, connId, StringComparison.Ordinal));

        public RoomPlayer? FindByName(string username)
            => Roster.FirstOrDefault(p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));

        public IGameConfig Config { get; }

        public int MaxPlayers => 12;

        public string? GameSettingsJson => null;

        public Task BroadcastAsync(string text)
        {
            lock (Broadcasts) Broadcasts.Add(text);
            return Task.CompletedTask;
        }

        public Task PrivateAsync(string connectionId, string text)
        {
            lock (Whispers) Whispers.Add((connectionId, text));
            return Task.CompletedTask;
        }

        public Task BroadcastStateAsync() => Task.CompletedTask;

        private sealed class SingleConfig : IGameConfig
        {
            private readonly UndercoverRules _rules;
            public SingleConfig(UndercoverRules rules) => _rules = rules;

            public T? GetSection<T>(string name) where T : class, new()
                => string.Equals(name, "Undercover", StringComparison.Ordinal) ? (T)(object)_rules : null;
        }
    }
}
