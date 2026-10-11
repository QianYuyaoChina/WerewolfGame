namespace Werewolf.Client.Share;

/// <summary>
/// 内置管理员账号的**唯一**常量来源（v2.5.0 第 5 / 6 条）。
///
/// 为什么放在共享层：服务端要用它 seed 账号、判定保留用户名，客户端要在界面上**明文**
/// 告诉用户「将以管理员 admin 登录（默认密码 ...）」——两端引用同一份常量，
/// 就不可能出现在文档 / 界面 / 代码里写着三个不同密码的经典事故。
///
/// 用户裁定（v2.5.0 收尾轮，覆盖架构文档早先写的 123456）：默认密码就是 <c>123456</c>，
/// 理由是用户明确要「一键开服用 123456 好记」，他自己后面可以随时改。
/// </summary>
public static class AdminDefaults
{
    /// <summary>内置管理员的用户名（大小写不敏感）。</summary>
    public const string Username = "admin";

    /// <summary>内置管理员的默认密码（用户裁定：123456）。</summary>
    public const string DefaultAdminPassword = "123456";

    /// <summary>弱口令提示文案（界面与文档共用一句，避免各写各的）。</summary>
    public const string WeakPasswordNotice = "内置管理员的默认密码是弱口令，开服后建议尽快修改密码。";

    /// <summary>
    /// 一键开服时显示在主按钮下方的那行小字：**明文**说明会用谁登录，让人心里有数。
    /// </summary>
    public static string OneClickLoginHint
        => $"将以管理员 {Username} 登录（默认密码 {DefaultAdminPassword}，开服后建议尽快修改）";
}
