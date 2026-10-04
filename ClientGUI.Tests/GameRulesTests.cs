using System.IO;
using System.Text.Json;
using ClientGUI.Services;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// 规则可配置化与界面状态持久化的校验：
/// 规则默认值 / JSON 往返、夜晚顺序规范化，以及界面状态的读写往返。
/// </summary>
public static class GameRulesTests
{
    public static void RunAll()
    {
        Defaults();
        JsonRoundTrip();
        NightOrderNormalization();
        UiStateRoundTrip();
    }

    // ==================== 规则默认值与换算 ====================

    private static void Defaults()
    {
        var rules = new GameRules();

        Check.Equal(40, rules.SecondsForPhase(Phases.Night), "规则：夜晚默认 40 秒");
        Check.Equal(45, rules.SecondsForPhase(Phases.Vote), "规则：投票默认 45 秒");
        Check.Equal(45, rules.SecondsForPhase(Phases.Discussion), "规则：讨论默认 45 秒");
        Check.Equal(30, rules.SecondsForPhase(Phases.HunterShoot), "规则：猎人默认 30 秒");
        Check.Equal(0, rules.SecondsForPhase(Phases.Waiting), "规则：等待阶段不限时");

        Check.Equal(40_000, rules.NightActionMs, "规则：夜晚超时换算为毫秒");
        Check.Equal(45_000, rules.VoteMs, "规则：投票超时换算为毫秒");
        Check.Equal(4, rules.MinPlayers, "规则：默认最少 4 人");
        Check.Equal(12, rules.MaxPlayers, "规则：默认最多 12 人");

        // 配置成 0 / 负数时应兜底为 1 秒，避免阶段被瞬间跳过。
        Check.Equal(1_000, new GameRules { NightActionSeconds = 0 }.NightActionMs,
            "规则：0 秒配置兜底为 1 秒");
        Check.Equal(1_000, new GameRules { VoteSeconds = -5 }.VoteMs,
            "规则：负数秒配置兜底为 1 秒");

        // 夜晚顺序默认必须为空：配置绑定器会往已有的集合属性“追加”，
        // 若预置一套默认顺序，appsettings.json 里的顺序就会被拼在其后而失效。
        Check.Equal(0, new GameRules().NightOrder.Count,
            "规则：夜晚顺序默认留空，避免配置顺序被追加到默认值之后");
    }

    // ==================== 规则 JSON 往返（对应 appsettings.json 的绑定形状） ====================

    private static void JsonRoundTrip()
    {
        var json = """
        {
          "NightActionSeconds": 12,
          "VoteSeconds": 13,
          "DiscussionSeconds": 14,
          "HunterShotSeconds": 15,
          "MinPlayers": 5,
          "MaxPlayers": 9,
          "NightOrder": [ "狼人", "预言家", "守卫", "女巫" ]
        }
        """;

        var rules = JsonSerializer.Deserialize<GameRules>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Check.True(rules != null, "规则：配置 JSON 可反序列化");
        if (rules == null) return;

        Check.Equal(12, rules.NightActionSeconds, "规则：夜晚秒数取自配置");
        Check.Equal(5, rules.MinPlayers, "规则：最少人数取自配置");
        Check.Equal(9, rules.MaxPlayers, "规则：最多人数取自配置");
        Check.SequenceEqual(new[] { "狼人", "预言家", "守卫", "女巫" }, rules.NightOrder,
            "规则：夜晚顺序取自配置");
    }

    // ==================== 夜晚顺序规范化 ====================

    private static void NightOrderNormalization()
    {
        Check.SequenceEqual(
            new[] { Role.Guard, Role.Werewolf, Role.Witch, Role.Seer },
            RoleInfo.ResolveNightOrder(null),
            "夜晚顺序：空配置回退默认顺序");

        Check.SequenceEqual(
            new[] { Role.Werewolf, Role.Guard, Role.Witch, Role.Seer },
            RoleInfo.ResolveNightOrder(new[] { "狼人", "守卫" }),
            "夜晚顺序：按配置排序并把缺失角色补到末尾");

        // 重复项与无法识别的名称都要被过滤，且不影响补齐。
        Check.SequenceEqual(
            new[] { Role.Witch, Role.Guard, Role.Werewolf, Role.Seer },
            RoleInfo.ResolveNightOrder(new[] { "女巫", "女巫", "不存在" }),
            "夜晚顺序：过滤重复与未知角色后补齐标准角色");

        Check.True(RoleInfo.TryParse(" 预言家 ") == Role.Seer, "角色解析：忽略首尾空格");
        Check.True(RoleInfo.TryParse("巫妖王") == null, "角色解析：未知名称返回 null");

        // 模拟配置绑定后的结果（绑定器会把 JSON 数组追加到空集合里）：顺序应以配置为准。
        var bound = new GameRules();
        bound.NightOrder.AddRange(new[] { "狼人", "守卫", "女巫", "预言家" });
        Check.SequenceEqual(
            new[] { Role.Werewolf, Role.Guard, Role.Witch, Role.Seer },
            RoleInfo.ResolveNightOrder(bound.NightOrder),
            "夜晚顺序：绑定后的顺序以配置为准（狼人先行动）");
    }

    // ==================== 界面状态持久化 ====================

    private static void UiStateRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wg_uistate_{Guid.NewGuid():N}.json");
        try
        {
            var saved = new UiState
            {
                Left = 120,
                Top = 80,
                Width = 1280,
                Height = 800,
                Maximized = true,
                ShowPlayers = true,
                ShowChat = false,
                ShowActions = true,
                ServerUrl = "http://localhost:5000/gamehub",
                Username = "alice"
            };
            UiStateStore.Save(saved, path);

            var loaded = UiStateStore.Load(path);
            Check.Equal(120d, loaded.Left ?? double.NaN, "界面状态：位置 Left 往返");
            Check.Equal(80d, loaded.Top ?? double.NaN, "界面状态：位置 Top 往返");
            Check.Equal(1280d, loaded.Width, "界面状态：宽度往返");
            Check.Equal(800d, loaded.Height, "界面状态：高度往返");
            Check.True(loaded.Maximized, "界面状态：最大化标记往返");
            Check.False(loaded.ShowChat, "界面状态：三栏显隐往返");
            Check.Equal("alice", loaded.Username, "界面状态：上次用户名往返");
            Check.Equal("http://localhost:5000/gamehub", loaded.ServerUrl, "界面状态：上次服务器地址往返");

            // 文件缺失 / 内容损坏 → 回退默认值，而不是抛异常。
            var missing = UiStateStore.Load(path + ".nope");
            Check.True(missing.ShowPlayers && missing.ShowChat && missing.ShowActions,
                "界面状态：文件缺失时回退默认可视");

            File.WriteAllText(path, "{ 这不是合法 JSON");
            var broken = UiStateStore.Load(path);
            Check.True(broken.ShowPlayers, "界面状态：文件损坏时回退默认值");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
