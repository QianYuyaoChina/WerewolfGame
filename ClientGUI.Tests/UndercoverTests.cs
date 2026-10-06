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
/// 谁是卧底的纯逻辑 / 无头校验：词库解析与自愈、发牌与**视角隔离**、白板规则、模块被反射发现。
///
/// v2.3.2 起**服务端不再下发本人身份**（局中 <see cref="UndercoverGameState.YourRole"/> 恒为空），
/// 因此这里不再用 <c>YourRole</c> 找卧底，而是改用「**词出现的次数**」区分多数派 / 少数派
/// （平民都拿同一个词、卧底拿另一个词），并在整局结束后再校验一次揭晓出来的身份 —— 校验强度不降反升。
/// </summary>
public static class UndercoverTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void RunAll()
    {
        WordBankParsing();
        WordBankAutoGenerate();
        WordBankEmbeddedDefault();
        ModuleIsDiscovered();
        DealRevealsOnlyYourOwnWord();
        BlankRoleCountsAcrossPlayerCounts();
        BlankDealAcrossPlayerCounts();
        RoleRevealedOnlyAfterGameOver();
        NoIdentityBeforeDealing();
        StartRejectsUnbalancedTinyRooms();
    }

    // ==================== 词库 ====================

    private static void WordBankParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uc_words_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, """
# 这是注释
可乐|雪碧|饮料

牛奶|豆浆
这是一行没有分隔符的垃圾

|空平民词|饮料
葡萄|提子|水果
""");

        try
        {
            var pairs = UndercoverWordBank.Load(path);
            Check.Equal(3, pairs.Count, "词库：只解析出合法的 3 对词（注释/空行/非法行都被忽略）");
            Check.Equal("可乐", pairs[0].Civilian, "词库：第一对平民词");
            Check.Equal("雪碧", pairs[0].Undercover, "词库：第一对卧底词");
            Check.Equal("饮料", pairs[0].Category, "词库：第三列是分类提示");
            Check.Equal("", pairs[1].Category, "词库：没有第三列时分类为空");
            Check.Equal("葡萄", pairs[2].Civilian, "词库：后续行照常解析");
        }
        finally
        {
            File.Delete(path);
        }

        Check.Equal(0, UndercoverWordBank.Load(Path.Combine(Path.GetTempPath(), "not_exist_uc.txt")).Count,
            "词库：文件不存在时返回空列表（由 EnsureSample 负责生成）");
    }

    private static void WordBankAutoGenerate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_bank_{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "words.undercover.txt");

        try
        {
            var warning = UndercoverWordBank.EnsureSample(path);
            Check.True(warning != null, "词库自愈：文件缺失时给出警告文案");
            Check.True(File.Exists(path), "词库自愈：自动生成了示例词库文件");

            var pairs = UndercoverWordBank.Load(path);
            Check.True(pairs.Count >= 20, $"词库自愈：示例词库可直接玩（{pairs.Count} 对）");

            // 已存在时不重复生成。
            Check.True(UndercoverWordBank.EnsureSample(path) == null, "词库自愈：文件已存在时不再生成");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 默认词库（源码目录的 words.undercover.txt）必须在**编译期**就进程序：
    /// ① 能按固定资源名读出；② 内容与源码文件一致；③ 缺词库时用**它**生成，而不是那份短示例；
    /// ④ 工作目录里没有词库文件也能开局（LoadOrDefault 直接用内置那份）。
    /// </summary>
    private static void WordBankEmbeddedDefault()
    {
        var embedded = UndercoverWordBank.ReadEmbedded();
        Check.True(embedded != null, "默认词库：程序集里嵌有 " + UndercoverWordBank.EmbeddedResourceName);
        if (embedded == null) return;

        var pairs = UndercoverWordBank.ParseLines(embedded.Split('\n'));
        Check.True(pairs.Count >= 20, $"默认词库：内置词库可直接玩（{pairs.Count} 对）");

        // 必须比内置的短示例多，否则说明嵌进去的还是旧示例（编译没接上）。
        var samplePairs = UndercoverWordBank.ParseLines(UndercoverWordBank.SampleContent.Split('\n'));
        Check.True(pairs.Count > samplePairs.Count,
            $"默认词库：用的是源码目录那一份（{pairs.Count} 对 > 示例 {samplePairs.Count} 对）");

        // 内容必须合法：每行两列都有值、且平民词 ≠ 卧底词。
        var bad = pairs.Count(p => p.Civilian.Length == 0 || p.Undercover.Length == 0
                                   || p.Civilian == p.Undercover);
        Check.Equal(0, bad, "默认词库：没有空词或两列相同的词对");

        // 与源码目录里的文件逐字一致（排除行尾换行差异）。
        var root = FindRepoRoot();
        if (root != null)
        {
            var sourcePath = Path.Combine(root, "WerewolfGame", "words.undercover.txt");
            if (File.Exists(sourcePath))
            {
                var onDisk = File.ReadAllText(sourcePath).Replace("\r\n", "\n").TrimEnd();
                Check.Equal(onDisk, embedded.Replace("\r\n", "\n").TrimEnd(),
                    "默认词库：程序内嵌内容与源码目录的 words.undercover.txt 一致");
            }
        }

        // 缺词库时用内置那份生成。
        var dir = Path.Combine(Path.GetTempPath(), $"uc_emb_{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "words.undercover.txt");
        try
        {
            Check.True(UndercoverWordBank.EnsureSample(path) != null, "默认词库：缺失时生成词库文件");
            var generated = File.ReadAllText(path);
            Check.Equal(embedded, generated, "默认词库：生成的内容就是程序内置那一份");

            // 已有词库时不覆盖用户自己改的内容。
            File.WriteAllText(path, "苹果|梨|水果\n");
            Check.True(UndercoverWordBank.EnsureSample(path) == null, "默认词库：已存在时不覆盖");
            Check.Equal("苹果", UndercoverWordBank.Load(path)[0].Civilian, "默认词库：用户改过的词被保留");

            // 文件不存在（例如工作目录只读写不进去）时，也要能拿到内置词库开局。
            var missing = Path.Combine(dir, "no-such-file.txt");
            Check.True(UndercoverWordBank.LoadOrDefault(missing).Count >= 20,
                "默认词库：文件不存在时直接用内置词库（不至于开不了局）");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static void ModuleIsDiscovered()
    {
        var catalog = new GameCatalog();
        catalog.RegisterAll(GameDiscovery.Discover(typeof(UndercoverGameModule).Assembly));

        Check.True(catalog.Contains(GameIds.Undercover), "接入点：谁是卧底模块被反射自动收录（无需改组合根）");
        var info = catalog.Describe(GameIds.Undercover);
        Check.True(info != null && info.DisplayName == "谁是卧底" && info.Emoji.Length > 0,
            "接入点：元数据（显示名 / 图标）正确");
        Check.True(catalog.Contains(GameIds.Werewolf), "接入点：加入新游戏不影响既有游戏");
    }

    // ==================== 发牌与视角隔离 ====================

    /// <summary>
    /// 4 人局发牌 + 视角隔离。阵营由「词出现的次数」反推（局中拿不到自己的身份）。
    /// </summary>
    private static void DealRevealsOnlyYourOwnWord()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_deal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            var room = NewRoom(wordFile, playerCount: 4);
            var started = room.StartGame("conn-host");
            Check.True(started.ok, "发牌：4 人局用单行词库开局成功");
            if (!started.ok) return;

            var conns = Enumerable.Range(0, 4)
                .Select(i => i == 0 ? "conn-host" : $"conn-{i}")
                .ToList();

            var payloads = new List<(int Id, UndercoverGameState Payload, GameStateDto Dto)>();
            for (var i = 0; i < conns.Count; i++)
            {
                var dto = room.SnapshotFor(conns[i])!;
                var payload = dto.GamePayload!.Deserialize<UndercoverGameState>(Json)!;
                payloads.Add((i, payload, dto));
            }

            // —— 阵营只能靠词反推：平民都拿同一个词（多数派），卧底拿另一个词（少数派） ——
            var undercover = MinorityCamp(payloads);
            var civilians = MajorityCamp(payloads);

            Check.Equal(1, undercover.Count, "发牌：4 人局恰好 1 名卧底");
            Check.Equal(3, civilians.Count, "发牌：其余都是平民");
            Check.True(undercover[0].Payload.YourWord == "梨", "发牌：卧底拿到卧底词（梨）");
            Check.True(civilians.All(c => c.Payload.YourWord == "苹果"), "发牌：所有平民拿到同一个词（苹果）");
            Check.True(payloads.All(p => p.Payload.CategoryHint == "水果"), "发牌：分类提示已下发");

            // 视角隔离：平民的私有负载里**不该出现卧底词**（反向同样成立）。
            var leaked = civilians.Count(c => c.Dto.GamePayload!.ToJsonString().Contains("梨", StringComparison.Ordinal));
            Check.Equal(0, leaked, "隔离：平民的私有负载里看不到卧底词");
            var leakedBack = undercover.Count(u => u.Dto.GamePayload!.ToJsonString().Contains("苹果", StringComparison.Ordinal));
            Check.Equal(0, leakedBack, "隔离：卧底的私有负载里看不到平民词");

            // 视角隔离（v2.3.2）：局中任何人**都拿不到自己的身份**。
            Check.True(payloads.All(p => p.Payload.YourRole.Length == 0),
                "保密：局中每份负载的 YourRole 都是空串（不开天眼）");
            Check.True(payloads.All(p => !p.Payload.YourRoleKnown),
                "保密：局中每份负载的 YourRoleKnown 都是 false");
            Check.True(payloads.All(p => !RevealsCamp(p.Payload)),
                "保密：局中身份相关文案（YourRole / YourRoleDesc）不含「卧底 / 平民」字样");
            Check.True(payloads.All(p => p.Payload.HasWord), "发牌：4 人局不设白板，人人都有词");

            // 存活时身份保密（玩家行标签为空），出局才公开。
            var anyTag = payloads.SelectMany(p => p.Dto.Players).Any(p => p.Tag != null);
            Check.False(anyTag, "保密：开局时玩家列表不显示任何人的身份");

            // 公开信息：本局身份配置可下发（数量不是「是谁」，属于玩法的一部分）。
            Check.True(payloads[0].Payload.RoleSummary.Contains("卧底×1", StringComparison.Ordinal),
                "公开：身份配置文案含卧底数量");
            Check.False(payloads[0].Payload.RoleSummary.Contains("白板", StringComparison.Ordinal),
                "公开：小局身份配置文案不出现白板");

            // 未开局/阶段不匹配的操作要被拒。
            var badVote = room.HandleGameAction(UcOps.Vote, Node(new TargetPayload { TargetId = 1 }), conns[0]);
            Check.False(badVote.ok, "反作弊：描述阶段（非投票阶段）提交选票被拒");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 白板：人数阈值与铁律 ====================

    /// <summary>
    /// 纯逻辑：全人数区间的卧底 / 白板数量。铁律「平民 &gt; 卧底 + 白板」必须在每个人数上都成立。
    /// </summary>
    private static void BlankRoleCountsAcrossPlayerCounts()
    {
        for (var n = 3; n <= 12; n++)
        {
            var (undercover, blank) = UndercoverGameInstance.ComputeRoleCounts(n);
            var civilian = n - undercover - blank;

            Check.True(civilian > undercover + blank,
                $"铁律：{n} 人局平民({civilian}) 严格多于卧底阵营({undercover + blank})");
            Check.True(undercover >= 1, $"铁律：{n} 人局至少有 1 名卧底");

            var expectBlank = n >= 10 ? 2 : n >= 7 ? 1 : 0;
            Check.Equal(expectBlank, blank, $"白板：{n} 人局恰好 {expectBlank} 个白板");
        }

        // 配置可覆盖阈值：门槛降到 3 人也要守住铁律（先减白板，再 clamp 卧底）。
        var tight = new UndercoverRules { BlankMinPlayers = 3, BlankTwoMinPlayers = 4 };
        for (var n = 3; n <= 12; n++)
        {
            var (undercover, blank) = UndercoverGameInstance.ComputeRoleCounts(n, tight);
            var civilian = n - undercover - blank;
            Check.True(civilian > undercover + blank,
                $"铁律（自定义阈值）：{n} 人局平民({civilian}) 严格多于卧底阵营({undercover + blank})");
        }

        // 关掉白板。
        var noBlank = new UndercoverRules { BlankMinPlayers = 0, BlankTwoMinPlayers = 0 };
        var (u12, b12) = UndercoverGameInstance.ComputeRoleCounts(12, noBlank);
        Check.Equal(0, b12, "白板：BlankMinPlayers=0 时完全不设白板");
        Check.True(12 - u12 > u12, "白板：关闭白板后铁律依然成立");
    }

    /// <summary>
    /// 真实发牌（走 <c>GameRoom.StartGame</c>）：各人数下的白板数量与铁律。
    /// </summary>
    private static void BlankDealAcrossPlayerCounts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_blank_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            foreach (var n in new[] { 4, 6, 7, 9, 10, 12 })
            {
                var room = NewRoom(wordFile, playerCount: n);
                var started = room.StartGame("conn-host");
                Check.True(started.ok, $"白板：{n} 人局开局成功");
                if (!started.ok) continue;

                var conns = Enumerable.Range(0, n)
                    .Select(i => i == 0 ? "conn-host" : $"conn-{i}")
                    .ToList();

                var payloads = new List<UndercoverGameState>();
                for (var i = 0; i < conns.Count; i++)
                {
                    var dto = room.SnapshotFor(conns[i])!;
                    payloads.Add(dto.GamePayload!.Deserialize<UndercoverGameState>(Json)!);
                }

                var blank = payloads.Count(p => !p.HasWord);
                var withWord = payloads.Where(p => p.HasWord).ToList();
                var minority = withWord
                    .GroupBy(p => p.YourWord, StringComparer.Ordinal)
                    .OrderBy(g => g.Count())
                    .First().Count();
                var undercover = minority;
                var civilian = payloads.Count - undercover - blank;

                var expectBlank = n >= 10 ? 2 : n >= 7 ? 1 : 0;
                Check.Equal(expectBlank, blank, $"白板：{n} 人局真实发牌恰好 {expectBlank} 个白板");
                Check.True(blank == 0 || payloads.All(p => p.HasWord || p.YourWord.Length == 0),
                    $"白板：{n} 人局白板的 YourWord 为空且 HasWord=false");

                Check.True(civilian > undercover + blank,
                    $"铁律：{n} 人局真实发牌平民({civilian}) 严格多于卧底阵营({undercover + blank})");

                var expect = UndercoverGameInstance.ComputeRoleCounts(n);
                Check.Equal(expect.Blank, blank, $"白板：{n} 人局发牌与规格计算一致（白板数）");
                Check.Equal(expect.Undercover, undercover, $"白板：{n} 人局发牌与规格计算一致（卧底数）");

                // 公开文案要包含白板数量（且不指向具体是谁）。
                var summary = payloads[0].RoleSummary;
                if (blank > 0)
                    Check.True(summary.Contains($"白板×{blank}", StringComparison.Ordinal),
                        $"公开：{n} 人局身份配置文案含白板数量");
                else
                    Check.False(summary.Contains("白板", StringComparison.Ordinal),
                        $"公开：{n} 人局身份配置文案不含白板");

                // 局中仍然拿不到自己的身份。
                Check.True(payloads.All(p => p.YourRole.Length == 0 && !p.YourRoleKnown),
                    $"保密：{n} 人局局中无人拿到自己的身份");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 结算揭晓 ====================

    /// <summary>
    /// 只有**整局结束**后才会下发真实身份（v2.3.2）。
    /// 用 3 人小局跑完整流程：1 卧底 + 2 平民，投掉任意 1 人都必然分出胜负。
    /// </summary>
    private static void RoleRevealedOnlyAfterGameOver()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_over_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            var room = NewRoom(wordFile, playerCount: 3, describeSeconds: 1, voteSeconds: 10);
            var started = room.StartGame("conn-host");
            Check.True(started.ok, "结算：3 人局开局成功");
            if (!started.ok) return;

            var conns = new[] { "conn-host", "conn-1", "conn-2" };

            // 局中：谁都拿不到自己的身份。
            var mid = conns.Select(c => PayloadOf(room, c)).ToList();
            Check.True(mid.All(p => p != null && p.YourRole.Length == 0 && !p.YourRoleKnown),
                "揭晓：局中 3 份负载都没有身份");

            // 3 人局只有一个卧底：投掉 1 人即分出胜负（走掉平民 → 1:1 卧底阵营胜；走掉卧底 → 平民胜）。
            var over = false;
            for (var attempt = 0; attempt < 2 && !over; attempt++)
            {
                if (!WaitUntil(() => PayloadOf(room, conns[0])?.Phase == UndercoverPhases.Vote, 25000))
                    break;

                Vote(room, conns[0], 1);
                Vote(room, conns[1], 0);
                Vote(room, conns[2], 1);

                over = WaitUntil(() => PayloadOf(room, conns[0])?.Phase == UndercoverPhases.GameOver, 25000);
            }

            Check.True(over, "揭晓：3 人局投出一人后本局结束（进入 GameOver）");
            if (!over) return;

            var finals = conns.Select(c => PayloadOf(room, c)).ToList();
            Check.True(finals.All(p => p != null), "揭晓：结算后 3 份负载都还在");
            if (finals.Any(p => p == null)) return;

            Check.True(finals.All(p => p!.YourRoleKnown), "揭晓：结算后负载才标记「身份已揭晓」");
            Check.True(finals.All(p => p!.YourRole.Length > 0), "揭晓：结算后负载才出现真实身份");

            var roles = finals.Select(p => p!.YourRole).ToList();
            Check.Equal(1, roles.Count(r => r == UndercoverRoles.Undercover), "揭晓：3 人局恰好 1 个卧底");
            Check.Equal(2, roles.Count(r => r == UndercoverRoles.Civilian), "揭晓：3 人局其余 2 人是平民");

            // 身份与词一致：拿卧底词的正是卧底，拿平民词的是平民。
            Check.True(finals.All(p => (p!.YourRole == UndercoverRoles.Undercover) == (p.YourWord == "梨")),
                "揭晓：身份与手上的词一致（卧底拿梨、平民拿苹果）");
            Check.True(finals.All(p => p!.HasWord == (p.YourWord.Length > 0)),
                "揭晓：HasWord 与 YourWord 一致");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 加固 1：发牌之前没有身份 ====================

    /// <summary>
    /// 未开局（还没发牌）时：
    /// ① 玩家列表不能给任何人打上「平民」标签（那时谁都没有身份，标签本身是错的）；
    /// ② 私有负载同样不能给出身份。
    /// 发牌后 / 出局 / 结算这三条既有语义由 <c>UndercoverQaAudit.TagPolicyInRealGame</c> 继续把关。
    /// </summary>
    private static void NoIdentityBeforeDealing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc_pre_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            const int n = 5;
            var room = NewRoom(wordFile, playerCount: n);
            var conns = Enumerable.Range(0, n)
                .Select(i => i == 0 ? "conn-host" : $"conn-{i}")
                .ToList();

            // —— 发牌之前 ——
            var preTags = conns.SelectMany(c => room.SnapshotFor(c)!.Players).ToList();
            Check.True(preTags.Count > 0, "发牌前：能看到玩家列表");
            Check.Equal(0, preTags.Count(p => p.Tag != null), "发牌前：玩家列表不带任何身份标签");
            Check.Equal(0, preTags.Count(p => p.TagColor != null), "发牌前：玩家列表不带身份配色");

            var prePayloads = conns.Select(c => PayloadOf(room, c)).ToList();
            Check.True(prePayloads.All(p => p != null && p.YourRole.Length == 0),
                "发牌前：私有负载不给身份");
            Check.True(prePayloads.All(p => p != null && !p.YourRoleKnown),
                "发牌前：私有负载不标记「已揭晓」");

            // —— 发牌之后：立即恢复「存活保密」（不能沿用发牌前的空白以外的东西） ——
            var started = room.StartGame("conn-host");
            Check.True(started.ok, "发牌前：5 人局开局成功");
            if (!started.ok) return;

            var postTags = conns.SelectMany(c => room.SnapshotFor(c)!.Players).ToList();
            Check.Equal(0, postTags.Count(p => p.Tag != null), "发牌后：开局仍然不带任何身份标签");
            var postPayloads = conns.Select(c => PayloadOf(room, c)).ToList();
            Check.True(postPayloads.All(p => p != null && p.YourRole.Length == 0 && !p.YourRoleKnown),
                "发牌后：开局没人拿到自己的身份");

            // 注：「重开一局后重新保密」由 UndercoverQaAudit.TagPolicyInRealGame 覆盖
            // （GameRoom 没有暴露 Reset，纯逻辑侧不再重复造一个入口）。
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 加固 2：人数不足以平衡时拒绝开局 ====================

    /// <summary>
    /// 即便有人把 <c>Undercover:MinPlayers</c> 配成 1 / 2，<c>Start()</c> 也必须拒绝开局：
    /// 这两种人数下「平民 &gt; 卧底阵营」在数学上不成立，开局瞬间就会判卧底阵营胜。
    /// </summary>
    private static void StartRejectsUnbalancedTinyRooms()
    {
        // 纯逻辑：MinBalancedPlayers 给出的建议人数本身必须满足铁律。
        var needed = UndercoverGameInstance.MinBalancedPlayers();
        Check.True(needed >= UndercoverGameInstance.MinPlayablePlayers,
            $"门禁：建议的最少人数（{needed}）不低于开局下限 {UndercoverGameInstance.MinPlayablePlayers}");
        var (nu, nb) = UndercoverGameInstance.ComputeRoleCounts(needed);
        Check.True(needed - nu - nb > nu + nb, $"门禁：建议人数 {needed} 人本身满足铁律");

        var dir = Path.Combine(Path.GetTempPath(), $"uc_tiny_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var wordFile = Path.Combine(dir, "words.txt");
        File.WriteAllText(wordFile, "苹果|梨|水果\n");

        try
        {
            foreach (var n in new[] { 1, 2 })
            {
                // 故意把人数下限配成 1（模拟被改坏的 appsettings.json）。
                var room = NewRoom(wordFile, playerCount: n, minPlayersOverride: 1);
                var (ok, msg) = room.StartGame("conn-host");
                Check.False(ok, $"门禁：MinPlayers 配成 1 时，{n} 人局仍然无法开局");
                Check.True(msg.Length > 0, $"门禁：{n} 人局被拒时给了原因（{msg}）");
            }

            // 3 人局（刚好是下限）仍然可以正常开局。
            var three = NewRoom(wordFile, playerCount: 3, minPlayersOverride: 1);
            var (ok3, msg3) = three.StartGame("conn-host");
            Check.True(ok3, "门禁：3 人局（铁律刚好成立）可以开局（" + msg3 + "）");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
        }
    }

    // ==================== 辅助 ====================

    /// <summary>局中负载里**透露阵营**的字段是否出现了「卧底 / 平民」字样（防文案泄露）。</summary>
    private static bool RevealsCamp(UndercoverGameState payload)
        => payload.YourRole.Contains("卧底", StringComparison.Ordinal)
           || payload.YourRole.Contains("平民", StringComparison.Ordinal)
           || payload.YourRoleDesc.Contains("卧底", StringComparison.Ordinal)
           || payload.YourRoleDesc.Contains("平民", StringComparison.Ordinal);

    /// <summary>多数派（拿到同一个词的人最多的一批）= 平民阵营。</summary>
    private static List<(int Id, UndercoverGameState Payload, GameStateDto Dto)> MajorityCamp(
        List<(int Id, UndercoverGameState Payload, GameStateDto Dto)> all)
        => all.GroupBy(p => p.Payload.YourWord, StringComparer.Ordinal)
              .OrderByDescending(g => g.Count())
              .First()
              .ToList();

    /// <summary>少数派（拿到「另一个词」的那批）= 卧底。</summary>
    private static List<(int Id, UndercoverGameState Payload, GameStateDto Dto)> MinorityCamp(
        List<(int Id, UndercoverGameState Payload, GameStateDto Dto)> all)
        => all.GroupBy(p => p.Payload.YourWord, StringComparer.Ordinal)
              .OrderByDescending(g => g.Count())
              .Skip(1)
              .SelectMany(g => g)
              .ToList();

    private static UndercoverGameState? PayloadOf(GameRoom room, string conn)
    {
        var dto = room.SnapshotFor(conn);
        if (dto?.GamePayload == null) return null;
        try { return dto.GamePayload.Deserialize<UndercoverGameState>(Json); }
        catch { return null; }
    }

    private static void Vote(GameRoom room, string conn, int targetId)
        => room.HandleGameAction(UcOps.Vote, Node(new TargetPayload { TargetId = targetId }), conn);

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(120);
        }
        return condition();
    }

    private static GameRoom NewRoom(string wordFile, int playerCount,
                                    int describeSeconds = 2, int voteSeconds = 2,
                                    int? minPlayersOverride = null)
    {
        var catalog = new GameCatalog();
        catalog.RegisterAll(GameDiscovery.Discover(typeof(UndercoverGameModule).Assembly));

        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };
        var config = new GameConfig(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Undercover:WordFile"] = wordFile,
            // 默认把下限设成「当前人数」，让各种人数的局都能开起来；
            // minPlayersOverride 用于模拟被改坏的配置（例如 Undercover:MinPlayers=1）。
            ["Undercover:MinPlayers"] = (minPlayersOverride ?? playerCount).ToString(),
            ["Undercover:DescribeSeconds"] = describeSeconds.ToString(),
            ["Undercover:VoteSeconds"] = voteSeconds.ToString()
        }).Build());

        var manager = new RoomManager(new EncryptedChannel(null!, new CryptoSessionStore()), catalog, options, config);
        manager.BindIdentity("conn-host", "host");
        var created = manager.CreateRoom("conn-host", "卧底测试房", null, GameIds.Undercover);
        var room = manager.Find(created.roomId)!;

        for (var i = 1; i < playerCount; i++)
        {
            manager.BindIdentity($"conn-{i}", $"p{i}");
            room.AddOrReconnect($"p{i}", $"conn-{i}");
        }

        return room;
    }

    /// <summary>从测试输出目录向上找含 WerewolfGame.sln 的目录（用于比对源码目录里的默认词库）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WerewolfGame.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static JsonObject? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json) as JsonObject;
}
