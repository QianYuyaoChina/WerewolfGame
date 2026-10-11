using Werewolf.Client.Share;
using Werewolf.Server.Games;

namespace Werewolf.Server.Games.Undercover;

/// <summary>
/// 谁是卧底的**词库资源**：把「词库文件」包装成平台认识的 <see cref="IEditableDataResource"/>
/// （v2.5.0 第 5 条 / 架构 D5 + D6）。
///
/// 这里是全仓库**唯一**知道词库文件名、行格式与 <c>|</c> 分隔的地方之一
/// （另一个是 <see cref="UndercoverWordBank"/> 的解析）；平台层一个字都不认识。
///
/// 三条硬性质：
/// <list type="number">
///   <item><b>校验前置</b>：整份内容先过 <see cref="Validate"/>，不通过就整份不落盘，并给出「第几行」；</item>
///   <item><b>原子落盘</b>：交给 <see cref="AtomicFileWriter"/>（临时文件 + 原子替换）；</item>
///   <item><b>下一局生效</b>：只改磁盘，不动任何正在进行的对局（对局开局时已把词读进内存）。</item>
/// </list>
/// </summary>
public sealed class UndercoverWordResource : IEditableDataResource
{
    /// <summary>资源机器名（对外的标识后缀，与游戏机器名拼成 <c>undercover:undercover.words</c>）。</summary>
    public const string ResourceKey = "undercover.words";

    /// <summary>行数上限（防止有人一粘贴就是几十万行，把服务器拖死）。</summary>
    public const int MaxLines = 5000;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="dataRoot">服务器数据根目录（= 工作目录）。</param>
    /// <param name="wordFile">词库文件名；空 / 空白 → <see cref="UndercoverWordBank.DefaultWordFile"/>。</param>
    public UndercoverWordResource(string dataRoot, string? wordFile = null)
    {
        WordFile = string.IsNullOrWhiteSpace(wordFile)
            ? UndercoverWordBank.DefaultWordFile
            : wordFile.Trim();

        Path = UndercoverWordBank.ResolvePath(dataRoot, WordFile);
    }

    /// <summary>实际使用的词库文件名。</summary>
    public string WordFile { get; }

    /// <summary>实际读写的绝对路径（与对局读取同一份解析结果）。</summary>
    public string Path { get; }

    /// <inheritdoc />
    public string ResourceId => ResourceKey;

    /// <inheritdoc />
    public string DisplayName => "谁是卧底 · 词库";

    /// <inheritdoc />
    public string FormatHint =>
        "每行写一对词，用竖线 | 分隔：\n" +
        "    平民词|卧底词\n" +
        "    平民词|卧底词|分类        ← 第三列可选，只作为给玩家的分类提示\n" +
        "以 # 开头的是注释，空行会被忽略；至少 1 行、最多 " + MaxLines + " 行。\n" +
        "保存后**下一局**生效：正在进行的这一局继续用原来的词。";

    /// <summary>
    /// 当前全文。文件不存在时返回**程序内置的默认词库**（与对局读取的兜底同源，
    /// 这样管理员第一次打开编辑器看到的就是真正在用的词）。
    /// </summary>
    public string Load()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllText(Path) : UndercoverWordBank.DefaultContent;
        }
        catch
        {
            return UndercoverWordBank.DefaultContent;
        }
    }

    /// <inheritdoc />
    public DataResourceCheck Validate(string content)
    {
        var text = content ?? "";
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        if (rawLines.Length > MaxLines)
            return new DataResourceCheck(false, $"行数太多（最多 {MaxLines} 行）", 0);

        var pairs = 0;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|');

            // 列数与解析口径一致：至少两列，前两列都不能是空的（第三列可选，多余列忽略）。
            if (parts.Length < 2
                || parts[0].Trim().Length == 0
                || parts[1].Trim().Length == 0)
            {
                return new DataResourceCheck(false, ServerMessages.ResourceLineError(i + 1), 0);
            }

            pairs++;
        }

        if (pairs == 0) return new DataResourceCheck(false, ServerMessages.ResourceEmpty, 0);

        return new DataResourceCheck(true, "", pairs);
    }

    /// <inheritdoc />
    public DataResourceWrite Save(string content)
    {
        var check = Validate(content);
        if (!check.Ok) return new DataResourceWrite(false, check.Error, 0);

        var (ok, error) = AtomicFileWriter.TryWrite(Path, content ?? "");
        if (!ok) return new DataResourceWrite(false, ServerMessages.ResourceSaveFailed + "（" + error + "）", 0);

        return new DataResourceWrite(true, "", check.Count);
    }
}
