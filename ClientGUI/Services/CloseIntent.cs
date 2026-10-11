namespace ClientGUI.Services;

/// <summary>
/// 一次「关闭」来自哪里（PRD 第 8 条 / 架构决策 D8）。
///
/// 为什么要这张表：关主窗口 / 关房间列表 / 退出程序 / 系统关机，四者的期望行为**不同**
/// （关主窗口只退房间，其余要「退房间 → 断连接 → 停服务器」的完整收尾），
/// 判据散落在三个窗口的 Closing 里，改一处必然忘另外两处。
/// 收进一个枚举 + 一个纯函数后，整张行为表一眼看全，也能被单测直接断言。
/// </summary>
public enum CloseIntentSource
{
    /// <summary>房间内主窗口（<c>MainWindow</c>）点 × / Alt+F4：**只退出房间**。</summary>
    RoomWindow,

    /// <summary>房间列表窗口点 ×：退出程序，走完整收尾。</summary>
    RoomListWindow,

    /// <summary>菜单「退出程序」/ Ctrl+Q / <c>Application.Shutdown()</c>：完整收尾。</summary>
    ExitProgram,

    /// <summary>系统注销 / 关机 / Ctrl+C（<c>SessionEnding</c>）：完整收尾。</summary>
    SystemSession
}

/// <summary>
/// 关闭意图的**唯一**分派入口（架构 §10 K9）。
/// 这里是纯函数：不碰任何窗口、不碰 Dispatcher，因此可被自动化测试直接调用。
/// </summary>
public static class CloseIntent
{
    /// <summary>
    /// 该关闭来源是否需要「完整收尾」（退出房间 → 断开连接 → 停内置服务器，8 秒预算）。
    ///
    /// 只有关主窗口是 false —— 关主窗口不停服务器、不拆连接、不退出程序，
    /// 它等价于按下导航栏的「退出房间」（PRD 8.1 / 8.2 / 8.7）。
    /// </summary>
    public static bool RequiresFullShutdown(CloseIntentSource source)
        => source != CloseIntentSource.RoomWindow;

    /// <summary>
    /// 该关闭来源的**人话**说明（日志 / 提示 / 测试失败信息用，也方便将来加行为列时对照）。
    /// 与 <see cref="RequiresFullShutdown"/> 同源，改行为时两处一起改。
    /// </summary>
    public static string Describe(CloseIntentSource source) => source switch
    {
        CloseIntentSource.RoomWindow => "关主窗口：只退出房间，回房间列表（不停服务器 / 不退出程序）",
        CloseIntentSource.RoomListWindow => "关房间列表：退出程序，完整收尾",
        CloseIntentSource.ExitProgram => "退出程序：完整收尾",
        CloseIntentSource.SystemSession => "系统关机 / 注销：完整收尾",
        _ => "未知关闭来源"
    };
}
