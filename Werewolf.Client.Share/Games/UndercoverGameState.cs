namespace Werewolf.Client.Share;

/// <summary>
/// 谁是卧底下发给「某玩家视角」的私有负载（序列化进 <see cref="GameStateDto.GamePayload"/>）。
/// 每个玩家的 DTO 独立生成 —— **你的词只有你自己能看到**（含白板）。
/// </summary>
public sealed class UndercoverGameState
{
    /// <summary>当前阶段（<see cref="UndercoverPhases"/> 下的常量）。</summary>
    public string Phase { get; set; } = "";

    /// <summary>第几轮（每轮 = 一次描述 + 一次投票）。</summary>
    public int Round { get; set; }

    /// <summary>「你」的词（白板为空字符串）。</summary>
    public string YourWord { get; set; } = "";

    /// <summary>「你」是否有词（= <see cref="YourWord"/> 非空）。白板必然为 false。</summary>
    public bool HasWord { get; set; }

    /// <summary>
    /// 「你」的身份：平民 / 卧底 / 白板。
    /// **局中一律为空串**（视角隔离：让玩家自己判断阵营）；只有整局结束后才下发真实身份。
    /// </summary>
    public string YourRole { get; set; } = "";

    /// <summary>
    /// 身份是否已揭晓。false = 局中保密（<see cref="YourRole"/> 为空是**规则**而非缺失）；
    /// true = 整局结束，<see cref="YourRole"/> 是真实身份，可用于结算展示。
    /// </summary>
    public bool YourRoleKnown { get; set; }

    /// <summary>身份说明文案。局中是**不透露阵营**的通用文案。</summary>
    public string YourRoleDesc { get; set; } = "";

    /// <summary>可选：词条的分类提示（如「饮料」）；没有配置则为空。</summary>
    public string CategoryHint { get; set; } = "";

    /// <summary>本局身份配置的公开文案（如「🕵️ 卧底×1、👤 平民×3」）。</summary>
    public string RoleSummary { get; set; } = "";

    // ---------------- 轮流发言（v2.3.1） ----------------

    /// <summary>当前轮到发言的玩家编号；-1 表示此刻没有人需要发言。</summary>
    public int CurrentSpeakerId { get; set; } = -1;

    /// <summary>当前轮到发言的玩家名；空表示此刻没有人需要发言。</summary>
    public string CurrentSpeakerName { get; set; } = "";

    /// <summary>是否轮到「我」发言 —— 只有轮到自己时才能发消息。</summary>
    public bool IsMyTurnToSpeak { get; set; }

    /// <summary>本轮已经发言完毕的玩家名（用于「已发言 2/4」进度展示）。</summary>
    public List<string> SpokenNames { get; set; } = new();

    /// <summary>本轮需要发言的总人数。</summary>
    public int TotalSpeakers { get; set; }

    /// <summary>本局生效的规则（阶段时长 / 人数上下限 / 词库文件名）。</summary>
    public UndercoverRules? Rules { get; set; }
}

/// <summary>
/// 谁是卧底的规则（来自 <c>appsettings.json</c> 的 <c>Undercover</c> 节点；
/// 缺失时回退到这里的默认值，因此没配置也能玩）。
/// </summary>
public sealed class UndercoverRules
{
    /// <summary>每轮描述阶段的秒数（全员同时发言，不能说出词本身）。</summary>
    public int DescribeSeconds { get; set; } = 45;

    /// <summary>投票阶段的秒数。</summary>
    public int VoteSeconds { get; set; } = 30;

    /// <summary>开始一局所需最少人数。</summary>
    public int MinPlayers { get; set; } = 3;

    /// <summary>房间允许的最大人数。</summary>
    public int MaxPlayers { get; set; } = 12;

    /// <summary>
    /// 加入 **1 个白板**所需的最少人数（默认 7）。人数少于此值时不设白板。
    /// 设为 0 或负数 = 完全关闭白板。
    /// </summary>
    public int BlankMinPlayers { get; set; } = 7;

    /// <summary>
    /// 加入 **2 个白板**所需的最少人数（默认 10）。人数少于此值但达到
    /// <see cref="BlankMinPlayers"/> 时只设 1 个白板。小于等于 <see cref="BlankMinPlayers"/> 表示永不设 2 个白板。
    /// </summary>
    public int BlankTwoMinPlayers { get; set; } = 10;

    /// <summary>
    /// 词库文件名（相对服务器工作目录）。缺失时服务器会自动生成一份示例词库并警告。
    /// 格式见文件头注释：每行 <c>平民词|卧底词|分类(可选)</c>。
    /// </summary>
    public string WordFile { get; set; } = "words.undercover.txt";

    public int DescribeMs => SecondsToMs(DescribeSeconds);
    public int VoteMs => SecondsToMs(VoteSeconds);

    private static int SecondsToMs(int seconds) => seconds <= 0 ? 30000 : seconds * 1000;
}
