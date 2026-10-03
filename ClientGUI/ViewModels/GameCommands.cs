using System.Windows.Input;

namespace ClientGUI.ViewModels;

/// <summary>
/// 操作面板用到的命令集合。视图模型只创建一次，供 <see cref="GameActionFactory"/>
/// 在每次刷新时复用（也可以让异步命令的“执行中禁用”状态跨刷新保持）。
/// </summary>
public sealed class GameCommands
{
    /// <summary>房主开始游戏。</summary>
    public required ICommand StartGame { get; init; }

    /// <summary>提交当前选择（夜间行动 / 毒药 / 投票 / 开枪），参数见 <see cref="GameAction.Parameter"/>。</summary>
    public required ICommand Submit { get; init; }

    /// <summary>女巫使用解药。</summary>
    public required ICommand UseHeal { get; init; }

    /// <summary>女巫今晚不用药。</summary>
    public required ICommand SkipPotion { get; init; }

    /// <summary>投票阶段弃票（-1）。</summary>
    public required ICommand AbstainVote { get; init; }

    /// <summary>猎人放弃开枪（-1）。</summary>
    public required ICommand SkipHunterShot { get; init; }
}
