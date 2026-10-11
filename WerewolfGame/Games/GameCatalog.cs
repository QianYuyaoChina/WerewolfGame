using System.Collections.Concurrent;
using Werewolf.Client.Share;

namespace Werewolf.Server.Games;

/// <summary>
/// 游戏注册表：平台查找「服务器当前可用游戏」与「实例工厂」的唯一入口。
/// 线程安全（内部 <see cref="ConcurrentDictionary{TKey,TValue}"/>）；构造后只读，可被多线程并发读取。
/// </summary>
public interface IGameCatalog
{
    /// <summary>服务器当前可用游戏（元数据，供 gamelist 下发）。</summary>
    IReadOnlyList<GameInfo> Available { get; }

    bool Contains(string gameId);

    GameInfo? Describe(string gameId);

    /// <summary>取该游戏的实例工厂；未知返回 false。</summary>
    bool TryGetFactory(string gameId, out Func<IGameContext, IGameInstance> factory);

    /// <summary>缺失 / 未知 gameId 的兜底解析：null / 空白 → Default；未知 → 原样返回（由调用方决定拒绝）。</summary>
    string ResolveOrDefault(string? gameId);

    /// <summary>
    /// 注册表里的全部游戏模块（v2.5.0 第 5 条新增）。
    ///
    /// 为什么需要它：<c>EditableDataRegistry</c> 要在构造时向每个模块问一次
    /// 「你有哪些可编辑资源」，而「实例工厂」那条链（<see cref="TryGetFactory"/>）
    /// 只给得出创建实例的委托、拿不到模块本身。
    ///
    /// 带默认实现（返回空）：既有实现方 / 测试替身零改动。
    /// </summary>
    IReadOnlyList<IGameModule> Modules => Array.Empty<IGameModule>();
}

/// <summary><see cref="IGameCatalog"/> 的默认实现：注册单元为 <see cref="IGameModule"/>。</summary>
public sealed class GameCatalog : IGameCatalog
{
    private readonly ConcurrentDictionary<string, IGameModule> _modules =
        new(StringComparer.Ordinal);

    /// <summary>注册一个游戏模块；重复注册以最后一次为准（幂等）。</summary>
    public void Register(IGameModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var info = module.Info;
        if (info == null || string.IsNullOrWhiteSpace(info.GameId)) return;

        _modules[info.GameId] = module;
    }

    /// <summary>批量注册；忽略 null 集合。</summary>
    public void RegisterAll(IEnumerable<IGameModule> modules)
    {
        if (modules == null) return;
        foreach (var module in modules) Register(module);
    }

    /// <inheritdoc />
    public IReadOnlyList<GameInfo> Available
        => _modules.Values
            .Select(m => m.Info)
            .OrderBy(i => i.GameId, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    public bool Contains(string gameId)
        => !string.IsNullOrEmpty(gameId) && _modules.ContainsKey(gameId);

    /// <inheritdoc />
    public GameInfo? Describe(string gameId)
        => Contains(gameId) ? _modules[gameId].Info : null;

    /// <inheritdoc />
    public bool TryGetFactory(string gameId, out Func<IGameContext, IGameInstance> factory)
    {
        if (Contains(gameId))
        {
            var module = _modules[gameId];
            factory = module.CreateInstance;
            return true;
        }

        factory = null!;
        return false;
    }

    /// <inheritdoc />
    public string ResolveOrDefault(string? gameId)
        => string.IsNullOrWhiteSpace(gameId) ? GameIds.Default : gameId.Trim();

    /// <summary>注册表里的全部游戏模块（v2.5.0 第 5 条）：按 GameId 排序，结果稳定便于断言。</summary>
    public IReadOnlyList<IGameModule> Modules
        => _modules.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
}
