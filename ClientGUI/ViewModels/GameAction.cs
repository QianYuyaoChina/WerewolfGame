using System.Windows.Input;

namespace ClientGUI.ViewModels;

/// <summary>右侧操作面板里的一个按钮。</summary>
public sealed class GameAction
{
    public required string Label { get; init; }
    public required ICommand Command { get; init; }
    public object? Parameter { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Hint { get; init; }

    /// <summary>normal | primary | danger</summary>
    public string Variant { get; init; } = "normal";

    public bool IsPrimary => Variant == "primary";
    public bool IsDanger => Variant == "danger";

    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

/// <summary>聊天/系统消息。</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }
    public required string Text { get; init; }

    /// <summary>info | chat | private | error | phase</summary>
    public string Kind { get; init; } = "info";
}
