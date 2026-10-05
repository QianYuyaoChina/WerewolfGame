using Werewolf.Client.Share;

namespace Werewolf.Client;

/// <summary>
/// 控制台的每游戏模块：房间标题、指令处理、帮助、状态渲染。
/// 新增游戏 = 实现本接口并在此注册点登记一条（<see cref="ConsoleGameRegistry"/>），不触碰平台指令循环。
/// </summary>
public interface IConsoleGameModule
{
    /// <summary>本模块负责的游戏机器名（<see cref="GameIds"/>）。</summary>
    string GameId { get; }

    /// <summary>追加到 <c>help</c> 的游戏私有帮助文本。</summary>
    string HelpText { get; }

    /// <summary>房间标题里的游戏标识（如「🐺 狼人杀 · 第 1 天 · 🌙 夜晚」）。</summary>
    string RoomHeader(GameStateDto s);

    /// <summary>房间内状态渲染的游戏私有部分（身份 / 玩家列表，每行以「│ 」开头）。</summary>
    string RenderState(GameStateDto s);

    /// <summary>尝试处理一条指令；返回 false 表示不是本游戏指令（交回通用指令）。</summary>
    Task<bool> TryHandleAsync(string line, GameStateDto? state, GameClient client);
}

/// <summary>控制台的游戏注册点：<c>GameId → IConsoleGameModule</c>；未知游戏走兜底模块（不阻断聊天）。</summary>
public static class ConsoleGameRegistry
{
    private static readonly Dictionary<string, IConsoleGameModule> Modules =
        new(StringComparer.Ordinal)
        {
            [GameIds.Werewolf] = new WerewolfConsoleModule()
        };

    /// <summary>服务器上线了本客户端不认识的游戏时的兜底模块。</summary>
    private static readonly IConsoleGameModule Unknown = new UnknownConsoleModule();

    /// <summary>按 GameId 取模块；空 / 未知一律返回兜底模块。</summary>
    public static IConsoleGameModule Resolve(string? gameId)
        => gameId != null && Modules.TryGetValue(gameId, out var module) ? module : Unknown;
}

/// <summary>未知游戏的兜底模块：只提示不支持，聊天 / 旁观仍可用。</summary>
internal sealed class UnknownConsoleModule : IConsoleGameModule
{
    public string GameId => "";

    public string HelpText => "";

    public string RoomHeader(GameStateDto s) => "🧩 未知游戏";

    public string RenderState(GameStateDto s)
        => "│ 本客户端暂不支持该游戏，仅可聊天 / 旁观。";

    public Task<bool> TryHandleAsync(string line, GameStateDto? state, GameClient client)
        => Task.FromResult(false);
}
