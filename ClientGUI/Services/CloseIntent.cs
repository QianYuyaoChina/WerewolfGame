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
    /// <summary>房间内（房间页，S4 前是主窗口）点 × / Alt+F4：**只退出房间**。</summary>
    RoomWindow,

    /// <summary>房间列表窗口点 ×：退出程序，走完整收尾。</summary>
    RoomListWindow,

    /// <summary>菜单「退出程序」/ Ctrl+Q / <c>Application.Shutdown()</c>：完整收尾。</summary>
    ExitProgram,

    /// <summary>系统注销 / 关机 / Ctrl+C（<c>SessionEnding</c>）：完整收尾。</summary>
    SystemSession
}

/// <summary>
/// 「外壳窗口被关闭」这一次关闭的**来源**（v2.6.0 S1 新增，架构 D4 / §5.4）。
///
/// 为什么另立一个枚举、而不是给 <see cref="CloseIntentSource"/> 加值：
/// 现有断言里有两把硬锁 —— 枚举必须是 **4 个值**、且**只有 <see cref="CloseIntentSource.RoomWindow"/>
/// 不需要完整收尾**。单窗口化之后多出来的维度不是「关闭来源」变了，而是
/// 「同一个 × 在不同页面上含义不同」，因此用这个更小的枚举描述来源，
/// 再用纯函数 <see cref="CloseIntent.Resolve"/> 映射回那 4 个值。
/// </summary>
public enum ShellCloseIntent
{
    /// <summary>点外壳窗口的 × / Alt+F4（含义取决于当前页）。</summary>
    WindowClose,

    /// <summary>菜单「退出程序」/ Ctrl+Q：无论当前页都完整收尾。</summary>
    MenuExit,

    /// <summary>系统注销 / 关机 / Ctrl+C。</summary>
    SystemSession
}

/// <summary>
/// 外壳窗口一次关闭最终要做的**动作**（v2.6.0 S6，架构 §5.2 的可执行形态）。
///
/// 为什么要把「动作」也收进纯函数：S6 修了一个真 bug —— <c>ShutdownMode=OnExplicitShutdown</c> 下
/// **窗口关闭不会让程序退出**，而「完整收尾」分支只放行了关闭、没有请求退出应用，
/// 于是进程留在「无窗口但活着」的状态：<c>OnExit</c> 不触发 → <see cref="AppShutdown"/> 三步不走
/// → 内置服务器变成孤儿、用户看到的就是「关了退不掉」。
/// 把「放行后必须显式退出应用」写成枚举值 <see cref="AllowAndExitApp"/>，这个 bug 从此可被断言钉死。
/// </summary>
public enum ShellCloseAction
{
    /// <summary>栈顶是浮层：× = 收起这一层（不退出程序）。</summary>
    DismissOverlay,

    /// <summary>房间页 × = 退出房间：拦下关闭，改走「返回」（I1）。</summary>
    CancelAndBack,

    /// <summary>完整收尾前先问用户（关窗确认，Q4 裁定）。</summary>
    NeedConfirm,

    /// <summary>用户取消了关窗确认：窗口留着，什么都不做。</summary>
    CancelOnly,

    /// <summary>放行关闭，并**显式请求退出应用**（<c>OnExplicitShutdown</c> 下必须这么做）。</summary>
    AllowAndExitApp
}

/// <summary>
/// 关闭意图的**唯一**分派入口（架构 §10 K9）。
/// 这里是纯函数：不碰任何窗口、不碰 Dispatcher，因此可被自动化测试直接调用。
/// </summary>
public static class CloseIntent
{
    /// <summary>
    /// 把「外壳关闭来源 + 当前页 + 是否正在收尾」映射到既有的四值关闭来源
    /// （架构 §5.4 的唯一定义处；<c>AppShutdown</c> 的三步因此一行都不用改）。
    ///
    /// 判定顺序即优先级：
    /// <list type="number">
    ///   <item>正在收尾 → <see cref="CloseIntentSource.ExitProgram"/>（幂等放行，第 6 行）；</item>
    ///   <item>菜单退出 → <see cref="CloseIntentSource.ExitProgram"/>（第 4 行）；</item>
    ///   <item>系统关机 → <see cref="CloseIntentSource.SystemSession"/>（第 5 行）；</item>
    ///   <item>在房间页点 × → <see cref="CloseIntentSource.RoomWindow"/>（只退房间，第 1 行）；</item>
    ///   <item>其余页（登录 / 房间列表）点 × → <see cref="CloseIntentSource.RoomListWindow"/>（完整收尾，第 2、3 行）。</item>
    /// </list>
    /// </summary>
    public static CloseIntentSource Resolve(ShellCloseIntent intent, PageId? currentPage, bool isShuttingDown)
        => isShuttingDown ? CloseIntentSource.ExitProgram
         : intent == ShellCloseIntent.MenuExit ? CloseIntentSource.ExitProgram
         : intent == ShellCloseIntent.SystemSession ? CloseIntentSource.SystemSession
         : currentPage == PageId.Room ? CloseIntentSource.RoomWindow
         : CloseIntentSource.RoomListWindow;

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
        CloseIntentSource.RoomListWindow => "关外壳（登录页／房间列表）：退出程序，完整收尾",
        CloseIntentSource.ExitProgram => "退出程序：完整收尾",
        CloseIntentSource.SystemSession => "系统关机 / 注销：完整收尾",
        _ => "未知关闭来源"
    };

    /// <summary>
    /// 关闭意图 → 外壳要做的动作（**不含**「问用户」那一步，问完再看 <see cref="AfterConfirm"/>）。
    ///
    /// 判定顺序即优先级：
    /// <list type="number">
    ///   <item>栈顶是浮层 → <see cref="ShellCloseAction.DismissOverlay"/>（× = 收起这一层，S2）；</item>
    ///   <item>不需要完整收尾（房间页 ×）→ <see cref="ShellCloseAction.CancelAndBack"/>（I1）；</item>
    ///   <item>需要完整收尾 → <see cref="ShellCloseAction.NeedConfirm"/>（先问，再按 <see cref="AfterConfirm"/> 分派）。</item>
    /// </list>
    /// </summary>
    public static ShellCloseAction ResolveShellClose(
        ShellCloseIntent intent, PageId? currentPage, bool isShuttingDown, bool currentIsOverlay)
        => currentIsOverlay ? ShellCloseAction.DismissOverlay
         : !RequiresFullShutdown(Resolve(intent, currentPage, isShuttingDown)) ? ShellCloseAction.CancelAndBack
         : ShellCloseAction.NeedConfirm;

    /// <summary>
    /// 关窗确认返回后的动作（v2.6.0 S6）：
    /// 确认 → <see cref="ShellCloseAction.AllowAndExitApp"/>（放行关闭**并且**请求退出应用）；
    /// 取消 → <see cref="ShellCloseAction.CancelOnly"/>（什么都不做，调用方还要清掉「菜单退出」标记）。
    /// </summary>
    public static ShellCloseAction AfterConfirm(bool confirmed)
        => confirmed ? ShellCloseAction.AllowAndExitApp : ShellCloseAction.CancelOnly;
}
