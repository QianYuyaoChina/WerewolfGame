namespace Werewolf.Client.Share;

/// <summary>
/// 游戏机器名（协议 / 存储 / 文件命名唯一使用它；稳定不变，禁止散落字面量）。
/// 三端（服务端 / GUI / 控制台）都引用这里的常量。
/// </summary>
public static class GameIds
{
    /// <summary>狼人杀（本轮唯一实现的游戏）。</summary>
    public const string Werewolf = "werewolf";

    /// <summary>谁是卧底（预留，本轮不实现）。</summary>
    public const string Undercover = "undercover";

    /// <summary>海龟汤（预留，本轮不实现）。</summary>
    public const string TurtleSoup = "turtle_soup";

    /// <summary>缺失 / 未知 GameId 时的回退值（服务端 <c>ResolveOrDefault</c> 使用）。</summary>
    public const string Default = Werewolf;
}
