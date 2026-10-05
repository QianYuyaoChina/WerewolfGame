namespace Werewolf.Client.Share;

/// <summary>
/// 狼人杀的操作名（客户端引用；服务端同名常量见 <c>Werewolf.Server.Games.Werewolf.WerewolfOps</c>，值必须一致）。
/// 框架只做透传，不理解这些 op 的语义：它们走 GameHub 的「默认分支」路由到当前房间的游戏实例。
/// </summary>
public static class WerewolfOps
{
    /// <summary>夜间行动。payload: { TargetId:int, Action:"do|heal|poison|none" }。</summary>
    public const string Night = "night";

    /// <summary>白天投票放逐。payload: { TargetId:int }（-1 弃票）。</summary>
    public const string Vote = "vote";

    /// <summary>猎人开枪。payload: { TargetId:int }（-1 放弃）。</summary>
    public const string Hunter = "hunter";

    /// <summary>丘比特第一夜指定情侣。payload: <c>CupidPayload { TargetA:int, TargetB:int }</c>。</summary>
    public const string Cupid = "cupid";

    /// <summary>警长移交被警徽。payload: { TargetId:int }（-1 放弃，警徽作废）。</summary>
    public const string Sheriff = "sheriff";
}
