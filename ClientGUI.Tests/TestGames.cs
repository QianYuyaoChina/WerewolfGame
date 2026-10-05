using Werewolf.Client.Share;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Werewolf;

namespace ClientGUI.Tests;

/// <summary>
/// 纯逻辑测试用的狼人杀模块（服务端注册单元）。名称仍为 <see cref="GameIds.Werewolf"/>，
/// 但人数上下限可自由配置，用于在无真实服务器的情况下驱动 <c>RoomManager</c> / <c>GameRoom</c>。
/// 工厂复用真实 <see cref="WerewolfGameInstance"/>，因此断言的是真实玩法行为，而非替身。
/// </summary>
internal sealed class TestWerewolfModule : IGameModule
{
    public TestWerewolfModule(int minPlayers, int maxPlayers)
    {
        Info = new GameInfo
        {
            GameId = GameIds.Werewolf,
            DisplayName = "狼人杀",
            Emoji = "🐺",
            Tagline = "测试用狼人杀模块",
            MinPlayers = minPlayers,
            MaxPlayers = maxPlayers
        };
    }

    /// <inheritdoc />
    public GameInfo Info { get; }

    /// <summary>最近一次由工厂创建的实例（供测试直接断言开局重置等行为）。</summary>
    public WerewolfGameInstance? LastInstance { get; private set; }

    /// <inheritdoc />
    public IGameInstance CreateInstance(IGameContext context)
        => LastInstance = new WerewolfGameInstance(Info, context);
}

/// <summary>构造测试用游戏目录（注册且仅注册一个狼人杀模块）。</summary>
internal static class TestGames
{
    /// <summary>
    /// 生成只含狼人杀模块的游戏目录。默认人数上下限与内置狼人杀一致（4 / 12），
    /// 传更小的值可构造「小局」（如 3 人房）。
    /// </summary>
    public static (GameCatalog Catalog, TestWerewolfModule Module) WerewolfOnly(int minPlayers = 4, int maxPlayers = 12)
    {
        var module = new TestWerewolfModule(minPlayers, maxPlayers);
        var catalog = new GameCatalog();
        catalog.Register(module);
        return (catalog, module);
    }
}

/// <summary>
/// 极简 <see cref="IGameConfig"/> 测试替身：只提供 <c>GameRules</c> 节点，
/// 用于在无 appsettings 的情况下给狼人杀实例注入人数上下限 / 时长。
/// </summary>
internal sealed class TestGameConfig : IGameConfig
{
    private readonly GameRules _rules;

    public TestGameConfig(GameRules rules) => _rules = rules;

    public T? GetSection<T>(string name) where T : class, new()
        => name == "GameRules" && typeof(T) == typeof(GameRules) ? (T)(object)_rules : null;
}
