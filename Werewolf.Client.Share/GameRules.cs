namespace Werewolf.Client.Share;

/// <summary>
/// 游戏规则：把原先散落在服务端常量与客户端硬编码里的“时长 / 夜晚顺序 / 人数上下限”
/// 集中成一份可序列化的数据。
/// 服务端从 <c>appsettings.json</c> 的 <c>GameRules</c> 节点读取，并随状态推送给客户端，
/// 客户端据此显示倒计时与提示，两端不再各写一份硬编码。
/// </summary>
public sealed class GameRules
{
    /// <summary>夜晚单个角色行动的等待超时（秒）。</summary>
    public int NightActionSeconds { get; set; } = 40;

    /// <summary>投票阶段超时（秒）。</summary>
    public int VoteSeconds { get; set; } = 45;

    /// <summary>白天自由讨论超时（秒）。</summary>
    public int DiscussionSeconds { get; set; } = 45;

    /// <summary>猎人开枪超时（秒）。</summary>
    public int HunterShotSeconds { get; set; } = 30;

    /// <summary>
    /// 夜晚行动顺序（角色中文名，如 "守卫" / "狼人" / "女巫" / "预言家"）。
    /// 未列出的标准角色会自动按默认顺序补到末尾，保证配置写漏也不会漏掉某个角色。
    ///
    /// 注意：这里默认留空是刻意的——配置绑定器会**追加**到已有的集合属性上，
    /// 若初值就放一套默认顺序，配置里的顺序会被拼接到其后而失效。
    /// “缺省时的默认顺序”由 <c>RoleInfo.ResolveNightOrder</c> 负责补齐。
    /// </summary>
    public List<string> NightOrder { get; set; } = new();

    /// <summary>开始一局所需的最少人数。</summary>
    public int MinPlayers { get; set; } = 4;

    /// <summary>房间允许的最大人数。</summary>
    public int MaxPlayers { get; set; } = 12;

    public int NightActionMs => SecondsToMs(NightActionSeconds);
    public int VoteMs => SecondsToMs(VoteSeconds);
    public int DiscussionMs => SecondsToMs(DiscussionSeconds);
    public int HunterShotMs => SecondsToMs(HunterShotSeconds);

    /// <summary>某阶段的限时秒数（0 表示不限时），供客户端倒计时使用。</summary>
    public int SecondsForPhase(string phase) => phase switch
    {
        Phases.Night => NightActionSeconds,
        Phases.Discussion => DiscussionSeconds,
        Phases.Vote => VoteSeconds,
        Phases.HunterShoot => HunterShotSeconds,
        _ => 0
    };

    /// <summary>把超时秒数安全地换算成毫秒（至少 1 秒，避免配置成 0 导致瞬间跳过）。</summary>
    private static int SecondsToMs(int seconds) => Math.Max(1, seconds) * 1000;
}
