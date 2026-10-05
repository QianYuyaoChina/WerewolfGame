using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>标题栏 / 面板头部的状态胶囊（游戏自定义：狼人杀 = 天数 / 阶段 / 倒计时）。</summary>
public sealed class StatusChip
{
    public string Text { get; init; } = "";

    /// <summary>胶囊文字颜色（十六进制，如 "#FF6B8A"）；为空则用主题默认文字色。</summary>
    public string? ColorHex { get; init; }
}

/// <summary>
/// 宿主暴露给游戏面板的桥：当前选中目标 / 房主 / 人数 / 网络客户端 / 反馈日志。
/// 游戏面板只依赖本接口，不感知宿主实现。
/// </summary>
public interface IGameHostBridge
{
    /// <summary>网络客户端（游戏操作经 <see cref="GameClient.CallGameOpAsync"/> 透传）。</summary>
    GameClient Client { get; }

    /// <summary>左栏当前选中的目标编号；-1 表示未选择。</summary>
    int SelectedTargetId { get; }

    /// <summary>当前房间人数。</summary>
    int PlayerCount { get; }

    /// <summary>本人是否为房主。</summary>
    bool IsHost { get; }

    /// <summary>宿主视角下「是否满足开局的基本条件」（房主 + 未进行中）；精细门禁由游戏面板自行判断。</summary>
    bool CanStartGame { get; }

    /// <summary>向房间消息区追加一条反馈（游戏面板的本地提示 / 错误）。</summary>
    void AppendLog(string text, string kind = "info");
}

/// <summary>
/// 每个游戏的「操作面板」视图模型契约（GUI 插槽装载的对象）。
/// 宿主只通过本接口与游戏面板交互；新增游戏 = 新增一个实现 + 一条模板 + 一条注册 case。
/// </summary>
public interface IGameViewModel
{
    /// <summary>本面板绑定的游戏机器名。</summary>
    string GameId { get; }

    /// <summary>操作面板按钮（绑定到插槽）。</summary>
    IReadOnlyList<GameAction> Actions { get; }

    /// <summary>标题栏 / 面板头的状态胶囊（游戏自定义）。</summary>
    IReadOnlyList<StatusChip> Chips { get; }

    /// <summary>阶段提示文字。</summary>
    string Hint { get; }

    /// <summary>左栏玩家行是否可作为本游戏当前操作的「目标」。</summary>
    bool CanSelectPlayer(PlayerRow row);

    /// <summary>玩家行被点选：宿主已翻转选中态；面板据此重算 Actions / Hint / Chips。</summary>
    void OnPlayerSelected(PlayerRow row);

    /// <summary>收到一次服务器状态（宿主已更新通用字段）。面板缓存之并重算。</summary>
    void OnState(GameStateDto state);

    /// <summary>选中目标 / 回合进度变化后重算（不重新拉状态）。</summary>
    void Refresh();

    /// <summary>离开房间 / 换游戏时清空。</summary>
    void Reset();
}
