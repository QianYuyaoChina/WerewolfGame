namespace Werewolf.Client.Share;

/// <summary>
/// 谁是卧底的操作名（客户端引用；服务端同名常量见 <c>Werewolf.Server.Games.Undercover.UndercoverOps</c>，值必须一致）。
/// 与狼人杀一样，框架只做透传：这些 op 走 GameHub 的「默认分支」路由到当前房间的游戏实例。
/// </summary>
public static class UndercoverOps
{
    /// <summary>投票放逐。payload: { TargetId:int }（-1 弃票）。</summary>
    public const string Vote = "uc_vote";

    /// <summary>轮到发言时跳过（不想描述）。payload: { TargetId:int }（&gt;=0 表示跳过）。</summary>
    public const string Skip = "uc_skip";
}

/// <summary>谁是卧底的阶段名（游戏私有；框架不感知）。</summary>
public static class UndercoverPhases
{
    public const string Waiting = "Waiting";

    /// <summary>描述阶段：所有存活玩家在聊天框里描述自己的词（不能说出词本身）。</summary>
    public const string Describe = "Describe";

    /// <summary>投票阶段：投票放逐一名玩家。</summary>
    public const string Vote = "Vote";

    public const string GameOver = "GameOver";

    public static string DisplayName(string phase) => phase switch
    {
        Waiting => "等待开始",
        Describe => "💬 描述",
        Vote => "🗳 投票",
        GameOver => "🏁 游戏结束",
        _ => phase
    };

    /// <summary>阶段横幅配色（十六进制；画刷由调用方缓存）。</summary>
    public static string Accent(string phase) => phase switch
    {
        Describe => "#38C9B0",
        Vote => "#FF6B8A",
        GameOver => "#9B8CFF",
        _ => "#7C8AA5"
    };

    public static int CountdownSeconds(string phase) => phase switch
    {
        Describe => 45,
        Vote => 30,
        _ => 0
    };
}

/// <summary>谁是卧底的身份名（与 <c>UndercoverGameInstance</c> 里的一致）。</summary>
public static class UndercoverRoles
{
    public const string Civilian = "平民";
    public const string Undercover = "卧底";
    public const string Blank = "白板";

    public static string Emoji(string role) => role switch
    {
        Undercover => "🕵️",
        Blank => "⬜",
        _ => "👤"
    };

    /// <summary>玩家行标签色（十六进制）。</summary>
    public static string? ColorHex(string role) => role switch
    {
        Undercover => "#FF6B8A",
        Blank => "#B9A5FF",
        Civilian => "#9BD4A6",
        _ => null
    };

    /// <summary>
    /// 是否属于**卧底阵营**（胜负判定时与卧底同阵营）。白板没有自己的词，天然站在卧底一侧。
    /// </summary>
    public static bool IsSpyCamp(string role)
        => role == Undercover || role == Blank;
}

/// <summary>
/// 谁是卧底的**共享文案常量**：服务端下发、两个客户端直接展示。
/// 客户端不得自己拼这些文案（避免深浅色主题 / 双端文案漂移，也避免误写出会泄露阵营的话）。
/// </summary>
public static class UndercoverText
{
    /// <summary>局中身份不明时的统一展示文案（不透露阵营）。</summary>
    public const string UnknownRoleDisplay = "🎭 保密";

    /// <summary>
    /// 局中身份不明时的说明文案：**不得出现「卧底 / 平民」字样**（否则等于开天眼）。
    /// </summary>
    public const string HiddenRoleDesc =
        "描述你拿到的词，但不能说出词本身；如果你没有词，就听别人的描述来推测。";

    /// <summary>「你是白板、没有词」的展示文案（白板必然知道自己没词，不算泄露阵营）。</summary>
    public const string BlankWordDisplay = "（你是白板，没有词）";

    /// <summary>白板的自助提示。</summary>
    public const string BlankSelfHint = "你没有词：听着别人的描述，把自己伪装成正常人。";

    /// <summary>玩法提示里「不会被告知自己阵营」的说明。</summary>
    public const string SecretRoleNotice = "你不会被告知自己是卧底还是平民，请靠大家的描述自己判断。";

    /// <summary>白板在结算揭晓里的占位文案。</summary>
    public const string NoWordPlaceholder = "（无词）";
}
