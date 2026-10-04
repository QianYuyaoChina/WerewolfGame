using System.Collections.ObjectModel;

namespace ClientGUI.ViewModels;

/// <summary>日志类型（决定 <c>LogEntryTemplate</c> 里的文字颜色）。</summary>
public static class LogKinds
{
    public const string Info = "info";
    public const string Chat = "chat";
    public const string Private = "private";
    public const string Error = "error";
    public const string Phase = "phase";
}

/// <summary>
/// 房间消息列表：负责分行、按前缀分类，以及限制总条数避免长时间运行后占用过多内存。
/// </summary>
public sealed class GameLog
{
    /// <summary>最多保留的日志条数。</summary>
    public const int MaxEntries = 800;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>写入一条（多行的）消息；每行生成一条带时间戳的日志。</summary>
    public void Add(string text, string kind = LogKinds.Info)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            Entries.Add(new LogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Text = line,
                Kind = kind
            });
        }

        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    /// <summary>清空全部消息（供菜单「视图 → 清空消息」使用）。</summary>
    public void Clear() => Entries.Clear();

    /// <summary>按消息前缀判断日志类型。</summary>
    public static string Classify(string message)
    {
        if (message.Contains('⚠') || message.Contains('❌')) return LogKinds.Error;
        if (message.StartsWith("💬", StringComparison.Ordinal)) return LogKinds.Chat;
        if (message.StartsWith("🎭", StringComparison.Ordinal)
            || message.StartsWith("🔍", StringComparison.Ordinal)
            || message.StartsWith("【", StringComparison.Ordinal)) return LogKinds.Private;
        if (message.StartsWith("════", StringComparison.Ordinal)) return LogKinds.Phase;
        return LogKinds.Info;
    }
}
