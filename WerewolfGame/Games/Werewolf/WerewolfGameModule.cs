using Werewolf.Client.Share;

namespace Werewolf.Server.Games.Werewolf;

/// <summary>
/// 狼人杀的注册单元（元数据 + 工厂）。反射发现要求「公共无参构造」，因此本类不依赖任何构造注入。
/// 新增游戏只需仿照本类新增一个文件，<c>Program.cs</c> 无需改动。
/// </summary>
public sealed class WerewolfGameModule : IGameModule
{
    /// <inheritdoc />
    public GameInfo Info { get; } = new()
    {
        GameId = GameIds.Werewolf,
        DisplayName = "狼人杀",
        Emoji = "🐺",
        Tagline = "村民与狼人的推理对抗，夜晚与白天交替进行",
        MinPlayers = 4,
        MaxPlayers = 12,
        AccentHex = "#9B8CFF",    // 紫：与谁是卧底的青绿一眼区分
        HasPlayerConfig = true,   // 狼人杀有「身份配置」页（房主设置里展示）
        RulesText = "夜晚守卫/狼人/女巫/预言家依次行动；白天讨论后投票放逐；屠边或消灭全部狼人即分出胜负。"
    };

    /// <inheritdoc />
    public IGameInstance CreateInstance(IGameContext context)
        => new WerewolfGameInstance(Info, context);
}
