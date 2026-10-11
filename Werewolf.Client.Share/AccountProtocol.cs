namespace Werewolf.Client.Share;

// ══════════════════════════════════════════════════════════════
//  账号域新增的负载与 DTO（v2.5.0 第 3 / 4 条：彩蛋 + 称号）
//
//  设计约束（架构 §10 K2 / K3 / K6）：
//    · 不新增 SignalR 方法、不改 Call 契约 —— 只加 Ops 常量与这里的 Payload；
//    · 业务异常一律 return RpcReply.Error(...)，文案统一放 ServerMessages；
//    · **称号归属只认会话身份**：SetTitlePayload 里刻意**没有**用户名字段，
//      服务端一律按「连接绑定的用户名」写入，请求里带的用户名根本无处可传。
// ══════════════════════════════════════════════════════════════

/// <summary>
/// <c>join</c> 回执的 Payload：登录即把「是否管理员 / 当前称号」带给客户端，省一次往返。
/// 两个数都只由服务端写入 —— 客户端不得自行置位（PRD 5.3）。
/// </summary>
public sealed class JoinAckPayload
{
    /// <summary>该账号是否为内置管理员（服务端判定）。</summary>
    public bool IsAdmin { get; set; }

    /// <summary>该账号当前的称号；无称号为 ""。</summary>
    public string Title { get; set; } = "";
}

/// <summary>会话身份（<c>Ops.Session</c> 的下行 DTO）：用户名 / 是否管理员 / 当前称号。</summary>
public sealed class SessionInfoDto
{
    /// <summary>当前连接绑定的用户名。</summary>
    public string Username { get; set; } = "";

    /// <summary>是否管理员（服务端判定，客户端不得自称）。</summary>
    public bool IsAdmin { get; set; }

    /// <summary>当前称号；无称号为 ""。</summary>
    public string Title { get; set; } = "";
}

/// <summary>
/// 设置 / 清除自己的称号（<c>Ops.SetTitle</c>）。
/// <b>刻意不含用户名字段</b>：只能改自己的，服务端一律以会话身份为准（PRD 4.8 / 架构 K6）。
/// </summary>
public sealed class SetTitlePayload
{
    /// <summary>新称号；空 / 全空白 = 清除称号。</summary>
    public string Title { get; set; } = "";
}

/// <summary>设置称号的回执 Payload：回带服务端**规范化后**的真实值（便于界面立即刷新）。</summary>
public sealed class SetTitleAckPayload
{
    /// <summary>服务端写入的称号（已去空白）；清除时为 ""。</summary>
    public string Title { get; set; } = "";
}

/// <summary>彩蛋口令上行（<c>Ops.Egg</c>）。</summary>
public sealed class EggPayload
{
    /// <summary>用户输入的口令（服务端忽略大小写与首尾空格）。</summary>
    public string Code { get; set; } = "";
}

/// <summary>彩蛋口令下行。**永远不报错** —— 未命中也是 <c>Ok=true</c> + 一条占位文案（PRD 3.6）。</summary>
public sealed class EggReplyPayload
{
    /// <summary>是否命中了已知口令。</summary>
    public bool Matched { get; set; }

    /// <summary>命中内容的种类（见 <see cref="EggKinds"/>）；未命中为 ""。</summary>
    public string Kind { get; set; } = "";

    /// <summary>给用户看的文案（由服务端下发，客户端不内嵌任何彩蛋内容，PRD 3.5）。</summary>
    public string Message { get; set; } = "";

    /// <summary>进入称号编辑态时回带的当前称号；其他情况为 ""。</summary>
    public string Title { get; set; } = "";
}

/// <summary>彩蛋内容的种类（客户端按它决定「接下来显示什么」，语义由服务端下发）。</summary>
public static class EggKinds
{
    /// <summary>进入称号编辑态（对应口令 <see cref="EggCodes.Surprise"/>）。</summary>
    public const string Title = "title";
}

/// <summary>已知口令（服务端权威；客户端不内嵌，只做输入）。</summary>
public static class EggCodes
{
    /// <summary>「惊喜」口令：进入称号编辑态。</summary>
    public const string Surprise = "surprise";
}

// ══════════════════════════════════════════════════════════════
//  修改密码（v2.5.0 第 7 条）与「可编辑数据资源」（v2.5.0 第 5 条）
//
//  同一套设计约束：不新增 SignalR 方法、只加 Ops + Payload；
//  业务异常一律 RpcReply.Error，文案统一放 ServerMessages。
//  **改密码的请求里刻意没有用户名字段** —— 只能改自己的，服务端按会话身份处理。
// ══════════════════════════════════════════════════════════════

/// <summary>修改自己的密码（<c>Ops.ChangePassword</c>）。刻意不含用户名：只能改自己的。</summary>
public sealed class ChangePasswordPayload
{
    /// <summary>当前密码（服务端会先校验它）。</summary>
    public string OldPassword { get; set; } = "";

    /// <summary>新密码。</summary>
    public string NewPassword { get; set; } = "";
}

/// <summary>可编辑资源清单里的一项（平台只透传，不解释资源内容的含义）。</summary>
public sealed class DataResourceItemDto
{
    /// <summary>资源标识（形如 <c>游戏机器名:资源机器名</c>），提交时原样回传。</summary>
    public string Id { get; set; } = "";

    /// <summary>所属游戏机器名。</summary>
    public string GameId { get; set; } = "";

    /// <summary>所属游戏显示名（如「谁是卧底」）。</summary>
    public string GameName { get; set; } = "";

    /// <summary>资源显示名（如「谁是卧底 · 词库」）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>格式说明（多行文本，由游戏自己给出，平台原样展示）。</summary>
    public string FormatHint { get; set; } = "";

    /// <summary>当前有效条数（由游戏自己算，平台不解释）。</summary>
    public int Count { get; set; }
}

/// <summary>可编辑资源清单（<c>Ops.DataResources</c> 的下行）。</summary>
public sealed class DataResourceListPayload
{
    /// <summary>资源清单；服务器上没有可编辑资源时为空列表（界面给空态文案）。</summary>
    public List<DataResourceItemDto> Items { get; set; } = new();
}

/// <summary>按标识取一个资源（请求体；也用于保存请求）。</summary>
public sealed class DataResourceRefPayload
{
    /// <summary>资源标识。</summary>
    public string Id { get; set; } = "";
}

/// <summary>资源全文（<c>Ops.DataResourceGet</c> 的下行）。</summary>
public sealed class DataResourceContentPayload
{
    /// <summary>资源标识。</summary>
    public string Id { get; set; } = "";

    /// <summary>当前全文（UTF-8 文本）。</summary>
    public string Content { get; set; } = "";

    /// <summary>内容摘要（取回时下发，保存时回带；用于「服务器上的内容已被改过」的二次确认）。</summary>
    public string Digest { get; set; } = "";

    /// <summary>当前有效条数。</summary>
    public int Count { get; set; }

    /// <summary>格式说明（随全文一起下发，编辑界面无需再查清单）。</summary>
    public string FormatHint { get; set; } = "";

    /// <summary>资源显示名。</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>整份提交一个资源（<c>Ops.DataResourceSave</c> 的上行）。</summary>
public sealed class DataResourceSavePayload
{
    /// <summary>资源标识。</summary>
    public string Id { get; set; } = "";

    /// <summary>提交的全文（**整份覆盖**，不做行级 diff）。</summary>
    public string Content { get; set; } = "";

    /// <summary>取回时服务端下发的摘要；留空表示不做冲突检查。</summary>
    public string Digest { get; set; } = "";
}

/// <summary>保存结果（<c>Ops.DataResourceSave</c> 的下行；失败时 Message 为人话原因）。</summary>
public sealed class DataResourceSaveResult
{
    /// <summary>是否保存成功。</summary>
    public bool Ok { get; set; }

    /// <summary>保存后的有效条数（失败时为 0）。</summary>
    public int Count { get; set; }

    /// <summary>给用户看的文案（成功形如「已保存 N 条，下一局生效」）。</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 资源内容摘要的**唯一**实现（服务端签发、客户端回带；两端共用同一份算法，
/// 否则会出现「刚保存完就被判成『服务器上的内容已被改过』」）。
/// 口径与资源格式无关：UTF-8 字节的 SHA-256 取前 8 字节 → 16 位十六进制。
/// </summary>
public static class DataResourceDigest
{
    /// <summary>算内容摘要。</summary>
    public static string Of(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content ?? "");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);

        var chars = new char[16];
        for (var i = 0; i < 8; i++)
        {
            var hex = hash[i].ToString("x2");
            chars[i * 2] = hex[0];
            chars[i * 2 + 1] = hex[1];
        }

        return new string(chars);
    }
}

/// <summary>
/// 称号的**唯一**校验口径（服务端与客户端共用同一份，避免两端规则漂移）。
///
/// 上限口径（PRD Q2 / 架构 U5）：「最多 8 个汉字或 16 个字符」——
/// 用**加权长度**实现：汉字与全角字符算 2，英文字母 / 数字算 1，上限 16。
/// 这样「8 个汉字」与「16 个英文字母」恰好等价，与给用户看的文案一致。
/// </summary>
public static class TitleRules
{
    /// <summary>称号的最大加权长度（汉字算 2 / 其余算 1）。</summary>
    public const int MaxLength = 16;

    /// <summary>去掉首尾空白后的称号；空 / 全空白一律视为「清除」。</summary>
    public static string Normalize(string? raw) => (raw ?? "").Trim();

    /// <summary>
    /// 校验称号。返回 (是否通过, 错误文案)。
    /// 空 / 全空白是**合法**的，含义是「清除称号」—— 与「超长」「含控制字符」区分开。
    /// </summary>
    public static (bool ok, string error) Validate(string? raw)
    {
        var text = Normalize(raw);
        if (text.Length == 0) return (true, "");

        foreach (var ch in text)
        {
            if (char.IsControl(ch)) return (false, ServerMessages.TitleBadChars);
        }

        if (WeightedLength(text) > MaxLength) return (false, ServerMessages.TitleTooLong);
        return (true, "");
    }

    /// <summary>
    /// 称号的加权长度：汉字 / 全角算 2，其余算 1（非 BMP 字符的代理对合计算 2）。
    /// 与「最多 8 个汉字或 16 个字符」这句用户文案一一对应。
    /// </summary>
    public static int WeightedLength(string text)
    {
        var total = 0;
        foreach (var ch in text)
            total += IsWide(ch) ? 2 : 1;   // 代理对的每个半代理各算 1，一对正好 2
        return total;
    }

    /// <summary>是否东亚宽字符 / 全角字符（CJK、假名、全角标点、韩文音节等）。</summary>
    private static bool IsWide(char c)
        => c is >= '\u1100' and <= '\u115F'
           or '\u2329' or '\u232A'
           or (>= '\u2E80' and <= '\u303E')
           or (>= '\u3041' and <= '\u33FF')
           or (>= '\u3400' and <= '\u4DBF')
           or (>= '\u4E00' and <= '\u9FFF')
           or (>= '\uA000' and <= '\uA4CF')
           or (>= '\uAC00' and <= '\uD7A3')
           or (>= '\uF900' and <= '\uFAFF')
           or (>= '\uFE10' and <= '\uFE19')
           or (>= '\uFE30' and <= '\uFE6F')
           or (>= '\uFF00' and <= '\uFF60')
           or (>= '\uFFE0' and <= '\uFFE6');
}
