namespace ClientGUI.Services;

/// <summary>
/// 「是否保留房间」询问的**三选一**答案（v2.6.0 S8）。
///
/// 为什么不能用 <c>bool?</c>：退房这件事其实有<b>三种</b>结局 ——
/// <list type="bullet">
///   <item><see cref="Keep"/>：退出房间，房间留在服务器上；</item>
///   <item><see cref="Close"/>：退出房间，房间一并关闭；</item>
///   <item><see cref="Cancel"/>：<b>不退出房间</b>（用户改主意了，回到房间里继续待着）。</item>
/// </list>
/// 而 <c>bool?</c> 的第三态 <c>null</c> 已经被 I6 占用了（「用户没显式选择」⇒ 保留），
/// 取消挤不进去 —— 硬塞就会变成「随手关掉询问 = 退房」，正是 I6 当年要防的事故。
/// 因此取消单独取一个值，由 <see cref="KeepRoomDecision.ResolveChoice"/> 统一翻译。
/// </summary>
public enum KeepRoomChoice
{
    /// <summary>退出房间，房间保留在服务器上。</summary>
    Keep,

    /// <summary>退出房间，并立即关闭房间。</summary>
    Close,

    /// <summary>取消这次退出：不退出房间，回到房间页。</summary>
    Cancel
}

/// <summary>
/// 「是否保留房间」询问的判定 —— 纯函数，**全仓唯一**的三态翻译入口（I6）。
///
/// 语义：「保留房间」= <c>true</c>、「关闭房间」= <c>false</c>；
/// **用户没有显式选择**（返回 / Esc / 外壳点 × → 结果为 <c>null</c>）一律按「保留房间」处理 ——
/// 随手关掉询问不该把房主的房间关掉。
///
/// v2.6.0 S4：询问载体已从旧窗口（模态 <c>DialogResult</c>）换成 <c>KeepRoomPage</c> 浮层页，
/// 本判定的语义一行未改 —— 页面只负责"交回用户的选择（或没选）"，翻译成布尔的活始终在这一处。
/// v2.6.0 S8：新增「取消」这一出口后，翻译统一先落到 <see cref="ResolveChoice"/>，
/// <see cref="Resolve"/> 变成它的一层薄封装 —— 规矩仍然只有一份，不会出现第二个判定点。
/// </summary>
public static class KeepRoomDecision
{
    /// <summary>
    /// 把窗口的 <see cref="System.Windows.Window.DialogResult"/>（可能是 <c>null</c>）翻译成「是否保留房间」。
    /// </summary>
    /// <param name="dialogResult">
    /// 窗口关闭时的 DialogResult：<c>true</c> = 保留、<c>false</c> = 关闭、<c>null</c> = 用户未显式选择。
    /// </param>
    /// <returns><c>null</c> → <c>true</c>（保留）；其余按原值。</returns>
    public static bool Resolve(bool? dialogResult)
        => ResolveChoice(dialogResult) == KeepRoomChoice.Keep;

    /// <summary>
    /// 把浮层交回的原始结果翻译成「下一步做什么」。
    /// <list type="bullet">
    ///   <item><see cref="KeepRoomChoice"/> → 原样（「取消」只有这一条路能表达）；</item>
    ///   <item><c>bool</c> → <c>true</c> = <see cref="KeepRoomChoice.Keep"/>、<c>false</c> = <see cref="KeepRoomChoice.Close"/>；</item>
    ///   <item><c>null</c>（返回 / Esc / 外壳 ×，用户没显式选）→ <see cref="KeepRoomChoice.Keep"/>（I6）。</item>
    /// </list>
    /// </summary>
    public static KeepRoomChoice ResolveChoice(object? raw)
    {
        if (raw is KeepRoomChoice choice) return choice;
        if (raw is bool yes) return yes ? KeepRoomChoice.Keep : KeepRoomChoice.Close;

        // 没显式选择 ⇒ 保留（与 Resolve 同一条规矩，见 I6）。
        return KeepRoomChoice.Keep;
    }
}
