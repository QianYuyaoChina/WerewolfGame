using System.Windows;
using System.Windows.Threading;
using Werewolf.Client.Share;

namespace ClientGUI.Services;

/// <summary>
/// 程序退出的**统一收尾**编排（PRD P0-5 / 流程 C / E-8～E-12）：
/// <list type="number">
///   <item>在房间里 → 走与菜单「退出房间」**完全相同**的离开流程（不弹「是否保留房间」）；</item>
///   <item>断开自己的 SignalR 连接（先摘掉自己这条长连接，Kestrel 才能立刻排空、端口才能马上释放）；</item>
///   <item>停止内置服务器。</item>
/// </list>
///
/// 三条硬性保证：
/// <list type="bullet">
///   <item><b>幂等</b>：Ctrl+C 连按、<c>OnExit</c> 与 <c>Closing</c> 双触发，都只执行一次；</item>
///   <item><b>有预算</b>：退出房间 3s / 停服务器 5s / 总 8s，超时一律放弃等待，绝不允许「关不掉窗口」；</item>
///   <item><b>顺序不可颠倒</b>：退出房间 → 断开连接 → 停服务器（颠倒会让离开请求发不出去）。</item>
/// </list>
///
/// WPF 侧绝对不能用 <c>task.Wait()</c> / <c>.Result</c>：视图模型的回调要回 UI 线程，
/// 阻塞 UI 线程会死锁 —— 因此用嵌套消息泵（<see cref="Dispatcher.PushFrame"/>）等待。
/// </summary>
public static class AppShutdown
{
    /// <summary>退出房间的等待预算（PRD E-8）。</summary>
    public static readonly TimeSpan LeaveRoomBudget = TimeSpan.FromSeconds(3);

    /// <summary>停止内置服务器的等待预算（PRD E-9）。</summary>
    public static readonly TimeSpan StopServerBudget = TimeSpan.FromSeconds(5);

    /// <summary>整个收尾的总预算（PRD E-9）。</summary>
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(8);

    /// <summary>0 = 未执行，1 = 已开始收尾（幂等闸）。</summary>
    private static int _state;

    private static GameClient? _session;
    private static Func<Task>? _leaveRoom;
    private static bool _confirmShown;

    /// <summary>是否已进入收尾（供界面判断：收尾期间「已离开房间」不应再触发关闭）。</summary>
    public static bool IsShuttingDown => Volatile.Read(ref _state) != 0;

    /// <summary>关窗确认的文案（含其他玩家人数）。</summary>
    public static string ConfirmMessage { get; private set; } = "";

    // ==================== 注册 ====================

    /// <summary>登记当前会话：收尾时用来断开自己的连接。</summary>
    public static void AttachSession(GameClient session) => _session = session;

    /// <summary>
    /// 注册「退出房间」委托（由主窗口在 <c>Loaded</c> 时注册、<c>Closed</c> 时注销）。
    /// 未注册即视为「不在房间里」，收尾时跳过这一步。
    /// </summary>
    public static void SetLeaveRoomHandler(Func<Task> handler) => _leaveRoom = handler;

    /// <summary>注销「退出房间」委托（已主动退出房间 / 窗口关闭时）。</summary>
    public static void ClearLeaveRoomHandler() => _leaveRoom = null;

    // ==================== 二次确认 ====================

    /// <summary>
    /// 是否需要在关窗前弹一次确认（PRD Q4 裁定）：
    /// **仅当内置服务器运行中且除自己外还有其他玩家（已连接人数 &gt; 1）**时弹；只有自己时不弹。
    /// 整轮会话最多弹一次。
    /// </summary>
    public static bool ShouldConfirmClose()
    {
        if (_confirmShown) return false;

        var service = EmbeddedServerService.Current;
        if (!service.IsRunning) return false;

        service.Refresh();
        var others = service.Runtime.PlayerCount - 1;   // 去掉自己
        if (others <= 0) return false;

        _confirmShown = true;
        ConfirmMessage = $"还有 {others} 位玩家连在这台电脑的服务器上，关闭会结束服务器并断开他们。\n\n确定要关闭吗？";
        return true;
    }

    /// <summary>
    /// 在窗口 <c>Closing</c> 里调用：需要确认时弹一次。
    /// 返回 false 表示用户**取消** —— 调用方必须 <c>e.Cancel = true</c>，
    /// 且此时不能做任何破坏性动作（不退出房间、不停服务器、不关窗口）。
    /// </summary>
    public static bool ConfirmClose(Window? owner)
    {
        if (!ShouldConfirmClose()) return true;

        var answer = MessageBox.Show(
            owner,
            ConfirmMessage,
            "狼人杀客户端",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return answer == MessageBoxResult.Yes;
    }

    // ==================== 执行 ====================

    /// <summary>执行收尾（幂等）。控制台等无 Dispatcher 的场景可直接 await 本方法。</summary>
    public static async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        await RunCoreAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// WPF 入口：用嵌套消息泵等待收尾完成（UI 保持响应，异步回调能继续跑），
    /// 并套一层 8 秒硬超时 —— 超时立即放行，不等任务。
    /// </summary>
    public static void RunWithPump()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // 没有 WPF 应用（例如测试宿主）：退化为「带预算的同步等待」。
            try { Task.Run(() => RunCoreAsync()).Wait(TotalBudget); }
            catch { /* 收尾失败不阻塞退出 */ }
            return;
        }

        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TotalBudget };
        timeout.Tick += (_, _) => frame.Continue = false;      // 硬超时：立即放行
        timeout.Start();

        _ = RunCoreAsync().ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);                            // UI 保持响应，异步回调能继续跑

        timeout.Stop();
    }

    private static async Task RunCoreAsync()
        // 三步各自有预算，外面再套一层 8 秒总预算：绝不无限等待（PRD E-9）。
        => await WithBudget(RunStepsAsync(), TotalBudget).ConfigureAwait(true);

    private static async Task RunStepsAsync()
    {
        var session = _session;

        // ① 在房间里 → 走与菜单「退出房间」完全相同的路径（不弹「是否保留房间」）。
        var leave = _leaveRoom;
        if (leave != null)
            await WithBudget(Guard(leave), LeaveRoomBudget).ConfigureAwait(true);

        // ② 断开自己的连接：先摘掉自己这条长连接，端口才能立刻释放（US-4「5 秒内端口释放」）。
        if (session != null)
            await WithBudget(Guard(() => session.DisposeAsync().AsTask()), LeaveRoomBudget).ConfigureAwait(true);

        // ③ 停内置服务器。
        var service = EmbeddedServerService.Current;
        if (service.IsRunning || service.IsBusy)
            await WithBudget(Guard(() => service.StopAsync()), StopServerBudget).ConfigureAwait(true);
    }

    /// <summary>包一层：收尾任一步失败都不得中断后续步骤，也不得抛给用户。</summary>
    private static async Task Guard(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch { /* 收尾阶段的异常一律不影响进程退出（E-15） */ }
    }

    /// <summary>等待一个任务，超过预算即放弃（不重试、不抛异常）。</summary>
    private static async Task WithBudget(Task task, TimeSpan budget)
    {
        try
        {
            await Task.WhenAny(task, Task.Delay(budget)).ConfigureAwait(true);
        }
        catch
        {
            // 收尾阶段的异常一律不影响进程退出。
        }
    }
}
