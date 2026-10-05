namespace Werewolf.Client.Share;

/// <summary>
/// 狼人杀下发给「某玩家视角」的私有负载（序列化进 <see cref="GameStateDto.GamePayload"/>）。
/// 这些字段已从通用 <see cref="GameStateDto"/> 移除，改由狼人杀模块自行承载；
/// 每个玩家的 DTO 独立生成，因此「你的身份 / 身份说明」不会串台。
/// </summary>
public sealed class WerewolfGameState
{
    /// <summary>当前阶段（<see cref="Phases"/> 下的常量字符串）。</summary>
    public string Phase { get; set; } = "";

    /// <summary>第几天（夜晚 / 白天共用同一计数）。</summary>
    public int Day { get; set; }

    /// <summary>「你」的身份中文名（<c>RoleInfo.Name</c>）；未开局时为空。</summary>
    public string YourRole { get; set; } = "";

    /// <summary>「你」的身份说明文案。</summary>
    public string YourRoleDesc { get; set; } = "";

    /// <summary>夜晚当前轮到行动的角色中文名；白天 / 等待 / 结束为空字符串。</summary>
    public string ActingRole { get; set; } = "";

    /// <summary>本局生效的规则（时长 / 夜晚顺序 / 人数上下限）；可为 null。</summary>
    public GameRules? Rules { get; set; }

    // ---------------- 身份布局与新身份（v2.1） ----------------

    /// <summary>本局最终身份配置的展示文案，如「🐺 狼人×2、🔮 预言家×1、👤 平民×2」。</summary>
    public string RoleSummary { get; set; } = "";

    /// <summary>本局的警长玩家名；未配置警长或警徽已作废时为空。</summary>
    public string SheriffName { get; set; } = "";

    /// <summary>「你」的情侣玩家名（丘比特指定）；你不是情侣时为空。</summary>
    public string LoverName { get; set; } = "";

    /// <summary>「你」是否是丘比特指定的情侣之一。</summary>
    public bool IsLover { get; set; }

    /// <summary>本局是否配置了警长身份（用于界面决定是否显示警徽相关提示）。</summary>
    public bool SheriffEnabled { get; set; }

    /// <summary>当前是否由「我」移交警徽（我是被淘汰的警长，且正处于警徽移交阶段）。</summary>
    public bool YouTransferBadge { get; set; }
}
