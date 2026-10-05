namespace Werewolf.Server.Games.Werewolf;

/// <summary>
/// 狼人杀的操作名（服务端引用；共享库同名常量见 <c>Werewolf.Client.Share.WerewolfOps</c>，值必须一致）。
/// 框架只做透传：这些 op 走 GameHub 的「默认分支」路由到狼人杀实例的 <c>HandleAction</c>。
/// </summary>
public static class WerewolfOps
{
    /// <summary>复用通用开局 op（<c>Ops.Start</c>）；此处列出以便狼人杀模块自述完整协议。</summary>
    public const string Start = "start";

    /// <summary>夜间行动。payload: { TargetId:int, Action:"do|heal|poison|none" }。</summary>
    public const string Night = "night";

    /// <summary>白天投票放逐。payload: { TargetId:int }（-1 弃票）。</summary>
    public const string Vote = "vote";

    /// <summary>猎人开枪。payload: { TargetId:int }（-1 放弃）。</summary>
    public const string Hunter = "hunter";

    /// <summary>丘比特第一夜指定情侣。payload: <c>CupidPayload { TargetA:int, TargetB:int }</c>。</summary>
    public const string Cupid = "cupid";

    /// <summary>警长移交警徽。payload: { TargetId:int }（-1 放弃，警徽作废）。</summary>
    public const string Sheriff = "sheriff";
}
