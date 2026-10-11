using Werewolf.Client.Share;

namespace Werewolf.Server.Games.Undercover;

/// <summary>
/// 谁是卧底的操作名（服务端引用；共享库同名常量见 <c>Werewolf.Client.Share.UndercoverOps</c>，值必须一致）。
/// 框架只做透传：这些 op 走 GameHub 的「默认分支」路由到本实例的 <c>HandleAction</c>。
/// </summary>
public static class UndercoverOps
{
    /// <summary>复用通用开局 op（<c>Ops.Start</c>）；此处列出以便游戏自述完整协议。</summary>
    public const string Start = "start";

    /// <summary>投票放逐。payload: { TargetId:int }（-1 弃票）。</summary>
    public const string Vote = "uc_vote";

    /// <summary>轮到发言时跳过。payload: { TargetId:int }（&gt;=0 表示跳过）。</summary>
    public const string Skip = "uc_skip";
}

/// <summary>
/// 谁是卧底的**服务端文案常量**。所有拒绝原因 / 提示都从这里出，
/// 避免同一句话在多个分支里各写一遍（改玩法时容易漏改、也容易被断言抓不到）。
/// </summary>
public static class UndercoverMessages
{
    /// <summary>人数没到开局下限。</summary>
    public static string NeedMorePlayers(int minPlayers) => $"至少需要 {minPlayers} 名玩家";

    /// <summary>
    /// 人够了，但这个人数**无法保证「平民多于卧底阵营」**（铁律）——
    /// 典型场景：<c>Undercover:MinPlayers</c> 被配成 1 或 2，此时开局瞬间就会判卧底阵营胜。
    /// </summary>
    public static string CannotBalance(int count, int undercover, int blank, int civilian)
        => $"人数不足：{count} 人局的配额是「卧底{undercover}+白板{blank} vs 平民{civilian}」，" +
           "无法保证平民多于卧底阵营（否则开局即判卧底阵营胜）";

    /// <summary>同上，并顺带告诉房主还需要多少人。</summary>
    public static string CannotBalanceWithHint(int count, int undercover, int blank, int civilian, int needed)
        => CannotBalance(count, undercover, blank, civilian) + $"，至少需要 {needed} 名玩家";
}

/// <summary>谁是卧底的游戏模块：元数据 + 工厂（由 <c>GameDiscovery</c> 反射自动收录）。</summary>
public sealed class UndercoverGameModule : IGameModule
{
    /// <inheritdoc />
    public GameInfo Info { get; } = new()
    {
        GameId = GameIds.Undercover,
        DisplayName = "谁是卧底",
        Emoji = "🕵️",
        Tagline = "大多数人拿到同一个词，卧底拿到相近的另一个词",
        MinPlayers = 3,
        MaxPlayers = 12,
        AccentHex = "#38C9B0",     // 青绿：与狼人杀的紫色一眼区分
        HasPlayerConfig = false    // 谁是卧底没有「身份配置」页（玩法由词库决定）
    };

    /// <inheritdoc />
    public IGameInstance CreateInstance(IGameContext context) => new UndercoverGameInstance(Info, context);

    /// <summary>
    /// 本游戏允许管理员远程编辑的数据资源：只有词库一个（v2.5.0 第 5 条）。
    /// 平台只拿到「资源接口」，不知道它其实是 <c>words.undercover.txt</c>。
    /// </summary>
    public IReadOnlyList<IEditableDataResource> CreateEditableResources(string dataRoot)
        => new IEditableDataResource[] { new UndercoverWordResource(dataRoot) };
}
