namespace Werewolf.Server.Games;

/// <summary>资源整份内容的校验结果。</summary>
/// <param name="Ok">是否通过（不通过时整份不落盘）。</param>
/// <param name="Error">失败时给人看的原因（含「第几行」这类可定位信息）。</param>
/// <param name="Count">通过时的有效条数。</param>
public readonly record struct DataResourceCheck(bool Ok, string Error, int Count);

/// <summary>资源整份内容的落盘结果。</summary>
/// <param name="Ok">是否写盘成功。</param>
/// <param name="Error">失败时给人看的原因。</param>
/// <param name="Count">成功时的有效条数。</param>
public readonly record struct DataResourceWrite(bool Ok, string Error, int Count);

/// <summary>
/// 「服务器上一个可被管理员远程编辑的数据资源」（v2.5.0 第 5 条 / 架构 D5）。
///
/// 设计要点 —— **平台层零游戏后门**：平台只认识本接口的六个成员，
/// 不知道资源叫什么名字、里面是什么格式、怎么算「有效一条」；
/// 词库文件名、<c>|</c> 分隔、行格式校验全部只活在游戏模块自己的实现里。
/// </summary>
public interface IEditableDataResource
{
    /// <summary>资源机器名（由游戏自己定，平台不解释，如 <c>undercover.words</c>）。</summary>
    string ResourceId { get; }

    /// <summary>显示名（如「谁是卧底 · 词库」）。</summary>
    string DisplayName { get; }

    /// <summary>格式说明（多行；游戏自己给，平台原样下发给客户端展示）。</summary>
    string FormatHint { get; }

    /// <summary>当前全文（UTF-8）。文件不存在时返回游戏自己的默认内容。</summary>
    string Load();

    /// <summary>校验整份内容；返回 (是否通过, 错误文案, 有效条数)。</summary>
    DataResourceCheck Validate(string content);

    /// <summary>校验通过后**原子落盘**；返回 (是否成功, 错误文案, 有效条数)。</summary>
    DataResourceWrite Save(string content);
}
