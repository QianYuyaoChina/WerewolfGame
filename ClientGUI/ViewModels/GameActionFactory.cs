using ClientGUI.Models;

namespace ClientGUI.ViewModels;

/// <summary>
/// 组装操作面板所需的全部“事实”。由视图模型整理后传入，
/// 工厂本身不碰网络、不读 <see cref="GameStateDto"/>，方便单独校验。
/// </summary>
public sealed class ActionContext
{
    public required string Phase { get; init; }
    public required string Role { get; init; }

    /// <summary>是否为猎人（含“服务器只在玩家行里给出身份”的兜底判断）。</summary>
    public bool IsHunter { get; init; }

    public bool IsAlive { get; init; }
    public bool IsHost { get; init; }
    public bool CanStartGame { get; init; }

    /// <summary>当前选中的目标编号；-1 表示未选择。</summary>
    public int SelectedTargetId { get; init; }

    /// <summary>本回合已提交过的动作。</summary>
    public IReadOnlySet<string> UsedThisRound { get; init; } = new HashSet<string>();

    /// <summary>本局已用掉的药水。</summary>
    public IReadOnlySet<string> PotionsUsed { get; init; } = new HashSet<string>();
}

/// <summary>一次操作面板的构建结果：按钮列表 + 阶段提示。</summary>
public sealed class ActionPlan
{
    public IReadOnlyList<GameAction> Actions { get; }
    public string Hint { get; }

    public ActionPlan(IReadOnlyList<GameAction> actions, string hint)
    {
        Actions = actions;
        Hint = hint;
    }

    /// <summary>没有按钮、只有提示。</summary>
    public static ActionPlan HintOnly(string hint) => new(Array.Empty<GameAction>(), hint);
}

/// <summary>
/// “阶段 + 身份 + 本回合进度” → 右侧操作面板的按钮与提示。
/// 规则集中在这里，界面与视图模型都不再散落 switch。
/// </summary>
public sealed class GameActionFactory
{
    private readonly GameCommands _commands;

    public GameActionFactory(GameCommands commands) => _commands = commands;

    public ActionPlan Build(ActionContext c) => c.Phase switch
    {
        Phases.Waiting => Waiting(c),
        Phases.Night => Night(c),
        Phases.Vote => Vote(c),
        Phases.HunterShoot => HunterShoot(c),
        Phases.Day => ActionPlan.HintOnly("天亮了，等待主持人公布昨夜结果…"),
        Phases.Discussion => ActionPlan.HintOnly("自由讨论中，使用下方输入框发言。"),
        Phases.GameOver => ActionPlan.HintOnly("本局已结束，可重新开始或继续等待。"),
        _ => ActionPlan.HintOnly("")
    };

    // ==================== 等待开始 ====================

    private ActionPlan Waiting(ActionContext c)
    {
        var start = new GameAction
        {
            Label = "开始游戏",
            Command = _commands.StartGame,
            IsEnabled = c.CanStartGame,
            Variant = "primary",
            Hint = c.CanStartGame ? null : (c.IsHost ? "至少需要 4 名玩家" : "只有房主可以开始游戏")
        };

        return new ActionPlan(new[] { start }, "");
    }

    // ==================== 夜晚 ====================

    private ActionPlan Night(ActionContext c)
    {
        if (!c.IsAlive) return ActionPlan.HintOnly("你已出局，等待天亮。");

        switch (c.Role)
        {
            case Roles.Guard:
                return new ActionPlan(
                    new[] { SubmitAction(c, "确认守护", "do") },
                    SelectTargetHint(c, "守护", "do"));

            case Roles.Werewolf:
                return new ActionPlan(
                    new[] { SubmitAction(c, "确认击杀", "do") },
                    SelectTargetHint(c, "击杀", "do"));

            case Roles.Seer:
                return new ActionPlan(
                    new[] { SubmitAction(c, "确认查验", "do") },
                    SelectTargetHint(c, "查验", "do"));

            case Roles.Witch:
                return Witch(c);

            default:
                return ActionPlan.HintOnly("你是平民，夜晚没有行动，请闭眼等待天亮。");
        }
    }

    private ActionPlan Witch(ActionContext c)
    {
        var healUsed = c.PotionsUsed.Contains("heal");
        var poisonUsed = c.PotionsUsed.Contains("poison");
        var healSubmitted = c.UsedThisRound.Contains("heal");

        var actions = new[]
        {
            new GameAction
            {
                Label = "🧪 使用解药",
                Command = _commands.UseHeal,
                IsEnabled = !healUsed && !healSubmitted,
                Variant = "primary",
                Hint = healUsed ? "解药本局已用完"
                     : healSubmitted ? "本次已提交"
                     : "救活今晚被狼人击杀的玩家"
            },
            new GameAction
            {
                Label = "☠ 使用毒药",
                Command = _commands.Submit,
                Parameter = "poison",
                IsEnabled = !poisonUsed,
                Variant = "danger",
                Hint = poisonUsed ? "毒药本局已用完"
                     : c.SelectedTargetId >= 0 ? $"目标：[{c.SelectedTargetId}]" : null
            },
            new GameAction
            {
                Label = "今晚不用药",
                Command = _commands.SkipPotion,
                IsEnabled = !healSubmitted,
                Hint = "不使用任何药水"
            }
        };

        var hint = c.SelectedTargetId >= 0
            ? $"已选择毒杀目标 [{c.SelectedTargetId}]，点击【使用毒药】提交；或点击其他玩家更换目标。"
            : "点击左侧玩家选择毒杀目标，或直接使用解药 / 选择今晚不用药。";

        return new ActionPlan(actions, hint);
    }

    // ==================== 投票 ====================

    private ActionPlan Vote(ActionContext c)
    {
        if (!c.IsAlive) return ActionPlan.HintOnly("你已出局，无法投票。");

        if (c.UsedThisRound.Contains("vote"))
        {
            return ActionPlan.HintOnly(c.SelectedTargetId < 0
                ? "✅ 你已提交弃票。"
                : $"✅ 你已投票给 [{c.SelectedTargetId}]。");
        }

        var actions = new[]
        {
            new GameAction
            {
                Label = "确认投票",
                Command = _commands.Submit,
                Parameter = "vote",
                IsEnabled = c.SelectedTargetId >= 0,
                Variant = "primary",
                Hint = c.SelectedTargetId >= 0 ? $"投给 [{c.SelectedTargetId}]" : "请先选择玩家"
            },
            new GameAction
            {
                Label = "弃票",
                Command = _commands.AbstainVote,
                Variant = "normal",
                Hint = "本回合不投票"
            }
        };

        var hint = c.SelectedTargetId >= 0
            ? $"已选择 [{c.SelectedTargetId}]，点击【确认投票】提交。"
            : "点击左侧玩家选择放逐对象，再点击【确认投票】。";

        return new ActionPlan(actions, hint);
    }

    // ==================== 猎人开枪 ====================

    private ActionPlan HunterShoot(ActionContext c)
    {
        if (!c.IsHunter) return ActionPlan.HintOnly("等待猎人选择开枪目标…");

        if (c.UsedThisRound.Contains("hunter"))
        {
            return ActionPlan.HintOnly(c.SelectedTargetId < 0
                ? "✅ 你已放弃开枪。"
                : $"✅ 你已开枪带走 [{c.SelectedTargetId}]。");
        }

        var actions = new[]
        {
            new GameAction
            {
                Label = "🔫 开枪",
                Command = _commands.Submit,
                Parameter = "hunter",
                IsEnabled = c.SelectedTargetId >= 0,
                Variant = "danger",
                Hint = c.SelectedTargetId >= 0 ? $"带走 [{c.SelectedTargetId}]" : "请先选择玩家"
            },
            new GameAction
            {
                Label = "放弃开枪",
                Command = _commands.SkipHunterShot,
                Hint = "不带走任何玩家"
            }
        };

        var hint = c.SelectedTargetId >= 0
            ? $"已选择开枪目标 [{c.SelectedTargetId}]，点击【开枪】提交。"
            : "点击左侧玩家选择开枪目标，或放弃开枪。";

        return new ActionPlan(actions, hint);
    }

    // ==================== 小工具 ====================

    /// <summary>“选择目标 → 提交”的标准按钮（夜间通用行动）。</summary>
    private GameAction SubmitAction(ActionContext c, string label, string kind)
        => new()
        {
            Label = label,
            Command = _commands.Submit,
            Parameter = kind,
            IsEnabled = c.SelectedTargetId >= 0,
            Variant = "primary",
            Hint = c.SelectedTargetId >= 0 ? $"目标：[{c.SelectedTargetId}]" : "请先在左侧选择玩家"
        };

    /// <summary>夜间“先选目标再提交”的提示文案。</summary>
    private static string SelectTargetHint(ActionContext c, string verb, string kind)
        => c.UsedThisRound.Contains(kind)
            ? $"✅ 本回合已提交（目标 [{c.SelectedTargetId}]）。"
            : c.SelectedTargetId >= 0
                ? $"已选择 [{c.SelectedTargetId}]，点击下方按钮提交{verb}。"
                : $"点击左侧玩家选择要{verb}的目标，再点击下方按钮确认。";
}
