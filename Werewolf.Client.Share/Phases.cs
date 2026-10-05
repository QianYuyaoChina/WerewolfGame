namespace Werewolf.Client.Share;

/// <summary>服务器阶段名（与 <c>GameStateDto.Phase</c> 字符串一致）。</summary>
public static class Phases
{
    public const string Waiting = "Waiting";
    public const string Night = "Night";
    public const string Day = "Day";
    public const string Discussion = "Discussion";
    public const string Vote = "Vote";
    public const string HunterShoot = "HunterShoot";

    /// <summary>警长淘汰后移交警徽（仅当本局配置了警长时才会出现）。</summary>
    public const string SheriffTransfer = "SheriffTransfer";

    public const string GameOver = "GameOver";

    public static string DisplayName(string phase) => phase switch
    {
        Waiting => "等待开始",
        Night => "🌙 夜晚",
        Day => "☀️ 天亮",
        Discussion => "💬 讨论",
        Vote => "🗳 投票",
        HunterShoot => "🔫 猎人开枪",
        SheriffTransfer => "🎖 警徽移交",
        GameOver => "🏁 游戏结束",
        _ => phase
    };

    /// <summary>阶段横幅用的配色（十六进制字符串，画刷由调用方缓存）。</summary>
    public static string Accent(string phase) => phase switch
    {
        Night => "#5B6CFF",
        Day => "#FFB648",
        Discussion => "#38C9B0",
        Vote => "#FF6B8A",
        HunterShoot => "#E0563F",
        SheriffTransfer => "#E8B33C",
        GameOver => "#9B8CFF",
        _ => "#7C8AA5"
    };

    /// <summary>
    /// 阶段的兜底倒计时秒数（服务端未下发 <see cref="GameRules"/> 时使用）；0 表示不限时。
    /// 正常情况下应以服务端下发的规则为准（见 <see cref="GameRules.SecondsForPhase"/>）。
    /// </summary>
    public static int CountdownSeconds(string phase) => phase switch
    {
        Night => 40,
        Discussion => 45,
        Vote => 45,
        HunterShoot => 30,
        SheriffTransfer => 30,
        _ => 0
    };
}

/// <summary>服务器角色名（与 <c>RoleInfo.Name</c> 一致）。</summary>
public static class Roles
{
    public const string Villager = "平民";
    public const string Werewolf = "狼人";
    public const string Seer = "预言家";
    public const string Witch = "女巫";
    public const string Hunter = "猎人";
    public const string Guard = "守卫";
    public const string Cupid = "丘比特";
    public const string Sheriff = "警长";

    /// <summary>界面展示顺序：狼人 → 神职 → 平民（用于身份配置文案）。</summary>
    public static readonly IReadOnlyList<string> DisplayOrder = new[]
    {
        Werewolf, Cupid, Seer, Witch, Hunter, Guard, Sheriff, Villager
    };

    public static string Emoji(string role) => role switch
    {
        Werewolf => "🐺",
        Seer => "🔮",
        Witch => "🧪",
        Hunter => "🔫",
        Guard => "🛡",
        Cupid => "💘",
        Sheriff => "🎖",
        _ => "👤"
    };
}
