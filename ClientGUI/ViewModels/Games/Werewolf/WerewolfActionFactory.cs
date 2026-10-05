using Werewolf.Client.Share;

namespace ClientGUI.ViewModels.Games.Werewolf;

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

    /// <summary>开始一局所需的最少人数（服务端规则）。</summary>
    public int MinPlayers { get; init; } = 4;

    /// <summary>夜晚当前轮到行动的角色（中文名）；未轮到或非夜晚为空字符串。</summary>
    public string ActingRole { get; init; } = "";

    /// <summary>夜晚当前是否轮到自己行动（服务端下发的 ActingRole 与本人身份一致）。</summary>
    public bool IsMyTurn { get; init; }

    /// <summary>当前选中的目标编号；-1 表示未选择。</summary>
    public int SelectedTargetId { get; init; }

    /// <summary>本回合已提交过的动作。</summary>
    public IReadOnlySet<string> UsedThisRound { get; init; } = new HashSet<string>();

    /// <summary>本局已用掉的药水。</summary>
    public IReadOnlySet<string> PotionsUsed { get; init; } = new HashSet<string>();

    // ---- 丘比特 / 警长（v2.1） ----

    /// <summary>丘比特已记下的情侣①编号（-1 表示还没记）。</summary>
    public int CupidTargetA { get; init; } = -1;

    /// <summary>丘比特已记下的情侣②编号（-1 表示还没记）。</summary>
    public int CupidTargetB { get; init; } = -1;

    /// <summary>当前是否由「我」移交警徽（警长被淘汰且轮到其选择）。</summary>
    public bool IsSheriffTransferor { get; init; }
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
public sealed class WerewolfActionFactory
{
    private readonly WerewolfCommands _commands;

    public WerewolfActionFactory(WerewolfCommands commands) => _commands = commands;

    public ActionPlan Build(ActionContext c) => c.Phase switch
    {
        Phases.Waiting => Waiting(c),
        Phases.Night => Night(c),
        Phases.Vote => Vote(c),
        Phases.HunterShoot => HunterShoot(c),
        Phases.SheriffTransfer => SheriffTransfer(c),
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
            Hint = c.CanStartGame ? null : (c.IsHost ? $"至少需要 {c.MinPlayers} 名玩家" : "只有房主可以开始游戏")
        };

        return new ActionPlan(new[] { start }, "");
    }

    // ==================== 夜晚 ====================

    private ActionPlan Night(ActionContext c)
    {
        if (!c.IsAlive) return ActionPlan.HintOnly("你已出局，等待天亮。");

        // 未轮到自己的身份时，不提供任何技能按钮（服务端也会拒绝）。
        if (!c.IsMyTurn)
        {
            var acting = string.IsNullOrEmpty(c.ActingRole) ? "其他角色" : c.ActingRole;
            return ActionPlan.HintOnly($"夜晚进行中，当前轮到【{acting}】行动，请等待…");
        }

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

            case Roles.Cupid:
                return Cupid(c);

            default:
                return ActionPlan.HintOnly("你是平民，夜晚没有行动，请闭眼等待天亮。");
        }
    }

    /// <summary>丘比特：第一夜指定两名玩家成为情侣（需要分别记下两个人再确认）。</summary>
    private ActionPlan Cupid(ActionContext c)
    {
        var a = c.CupidTargetA;
        var b = c.CupidTargetB;
        var picked = a >= 0 && b >= 0;

        var actions = new[]
        {
            new GameAction
            {
                Label = "💘 记为情侣①",
                Command = _commands.SetCupidA,
                IsEnabled = c.SelectedTargetId >= 0,
                Variant = "normal",
                Hint = a >= 0 ? $"已记下 [{a}]" : "把当前选中的玩家记为第一位"
            },
            new GameAction
            {
                Label = "💘 记为情侣②",
                Command = _commands.SetCupidB,
                IsEnabled = c.SelectedTargetId >= 0 && c.SelectedTargetId != a,
                Variant = "normal",
                Hint = b >= 0 ? $"已记下 [{b}]" : "把当前选中的玩家记为第二位"
            },
            new GameAction
            {
                Label = "✅ 确认情侣",
                Command = _commands.ConfirmCupid,
                IsEnabled = picked,
                Variant = "primary",
                Hint = picked ? $"[{a}] 与 [{b}] 成为情侣" : "先记下两名玩家"
            }
        };

        var hint = picked
            ? $"情侣：[{a}] 与 [{b}]。点击【确认情侣】提交；一方出局另一方会殉情。"
            : a >= 0
                ? $"已记下情侣①=[{a}]，再选一名玩家并点击【记为情侣②】，最后确认。"
                : "点击左侧玩家 → 【记为情侣①】，再选另一名 → 【记为情侣②】，最后【确认情侣】。";

        return new ActionPlan(actions, hint);
    }

    /// <summary>警徽移交：警长被淘汰时把警徽交给一名存活玩家（或让它作废）。</summary>
    private ActionPlan SheriffTransfer(ActionContext c)
    {
        if (!c.IsSheriffTransferor) return ActionPlan.HintOnly("等待警长移交警徽…");

        var actions = new[]
        {
            new GameAction
            {
                Label = "🎖 移交警徽",
                Command = _commands.TransferBadge,
                Parameter = "sheriff",
                IsEnabled = c.SelectedTargetId >= 0,
                Variant = "primary",
                Hint = c.SelectedTargetId >= 0 ? $"移交给 [{c.SelectedTargetId}]" : "请先选择玩家"
            },
            new GameAction
            {
                Label = "作废警徽",
                Command = _commands.VoidBadge,
                Variant = "normal",
                Hint = "本局不再有警长"
            }
        };

        var hint = c.SelectedTargetId >= 0
            ? $"已选择 [{c.SelectedTargetId}]，点击【移交警徽】移交；接手者投票计 1.5 票。"
            : "你已出局，点击一名存活玩家接手警徽，或选择让警徽作废。";

        return new ActionPlan(actions, hint);
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
