using Microsoft.Extensions.Configuration;

namespace Werewolf.Server.Games;

/// <summary>
/// <see cref="IGameConfig"/> 的默认实现：包装 <see cref="IConfiguration"/>，
/// 供 <see cref="IGameContext.Config"/> 暴露给游戏模块按节点名读取强类型配置。
/// 传入的配置为 null（如纯逻辑测试）时，<see cref="GetSection{T}"/> 一律返回 null，
/// 游戏模块据此回退到内置默认值。
/// </summary>
public sealed class GameConfig : IGameConfig
{
    private readonly IConfiguration? _config;

    public GameConfig(IConfiguration? config)
    {
        _config = config;
    }

    /// <inheritdoc />
    public T? GetSection<T>(string name) where T : class, new()
        => string.IsNullOrEmpty(name) ? null : _config?.GetSection(name).Get<T>();
}
