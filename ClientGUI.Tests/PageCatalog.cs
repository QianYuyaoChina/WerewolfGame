using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClientGUI.Controls;
using ClientGUI.Models;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views.Pages;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 一个页面的探针：三套守护断言（<see cref="AppShellGuard"/> / <see cref="AppShellQaGuard"/> /
/// <see cref="V25BatchProbe"/>）将来都从这里取数据 —— 但 S1 不动它们（旧窗口还在，条数一条不减），
/// 本文件只负责**新增**页面形态的断言。
/// </summary>
/// <param name="Name">人话名字（失败信息里显示）。</param>
/// <param name="Id">页面标识（与 <see cref="NavigationService"/> 同一套）。</param>
/// <param name="Create">构造一个页面实例（页面是 <c>UserControl</c>，不是窗口）。</param>
/// <param name="ExpectPrimary">是否有通栏主按钮（有则量「≥44 高 + ≥80% 宽」）。</param>
/// <param name="DesignWidth">设计宽（页面没有 Width 属性，布局测量时由这里给）。</param>
/// <param name="DesignHeight">设计高。</param>
public sealed record PageProbe(
    string Name,
    PageId Id,
    Func<AppSession, FrameworkElement> Create,
    bool ExpectPrimary,
    double DesignWidth,
    double DesignHeight);

/// <summary>
/// v2.6.0 S1 页面化专项断言（架构 §8.3 的 P1–P11）。
///
/// 分两批跑，理由是**WPF 线程要求**：
/// <list type="bullet">
///   <item><see cref="RunLogicChecks"/>：纯逻辑（栈、关闭意图映射、退订、键位），跑在默认组；</item>
///   <item><see cref="RunUiChecks"/>：要真实布局测量（页面骨架 / 导航标题居中 / 主按钮通栏），跑在 <c>--ui</c> 的 STA 线程。</item>
/// </list>
/// 两条历史注记（S1 写的话，S4 已翻面，留在此处免得读代码的人被旧话误导）：
/// <list type="bullet">
///   <item><b>S1 期间</b>本文件是纯增量：旧 8 个窗口仍由原来的表断言着，一条既有断言没改没删；</item>
///   <item><b>S4 之后</b>那 10 个窗口类已删除、三张窗口表已切到页面（见 <c>AppShellGuard.MigratedPages</c> /
///         <c>AppShellQaGuard</c> / <c>V25BatchProbe</c>），本文件不再有「窗口 vs 页面」并存的问题，
///         转而承担「旧窗口**真的没了**」的全仓归零守卫（`CheckPushWiring` / `CheckS3PushWiring`）。</item>
/// </list>
/// </summary>
public static class PageCatalog
{
    /// <summary>
    /// 已迁移的页面：S1 三页（登录 / 房间列表 / 房间）+ S2 四页（创建房间 / 房主设置 / 设置 / 邀请码）
    /// + S3 三页（关于 / 更新日志 / 保留房间）—— 共 **10 页**，与 <see cref="PageId"/> 的十个值一一对应。
    /// </summary>
    public static IReadOnlyList<PageProbe> All { get; } = new List<PageProbe>
    {
        new("登录页", PageId.Login, session => new LoginPage(session), true, 470, 792),
        new("房间列表页", PageId.RoomList, session => new RoomListPage(session), true, 960, 640),
        new("房间页", PageId.Room, session => new RoomPage(session), false, 1220, 780),

        // ——— S2 四页：设计尺寸沿用各自旧窗口的 ClientSize（内容宽度一致，视觉不跳）———
        new("创建房间页", PageId.CreateRoom, session => new CreateRoomPage(session), true, 448, 537),
        new("房主设置页", PageId.HostSettings, session => new HostSettingsPage(session), true, 584, 680),
        new("设置页", PageId.Settings, session => new SettingsPage(session), true, 540, 614),
        new("邀请码页", PageId.InviteCode, session => new InviteCodePage(session, "WW1:S2-PAGE"), true, 508, 440),

        // ——— S3 三页：尺寸同样沿用旧窗口（关于 470×560 / 更新日志 560×620 / 保留房间 456×541）———
        // 保留房间页**有**通栏主按钮（「保留房间」），关于 / 更新日志没有（底部是右对齐的两个按钮）。
        new("关于页", PageId.About, session => new AboutPage(session), false, 470, 560),
        new("更新日志页", PageId.Changelog, session => new ChangelogPage(session), false, 560, 620),
        new("保留房间页", PageId.KeepRoom, session => new KeepRoomPage(session), true, 456, 541)
    };

    /// <summary>S2 新迁的 4 页（单独列出来，便于「新增页面没进表 = 等于没迁」这条自检定位）。</summary>
    public static IReadOnlyList<PageProbe> S2Pages { get; } = All
        .Where(p => p.Id is PageId.CreateRoom or PageId.HostSettings or PageId.Settings or PageId.InviteCode)
        .ToList();

    /// <summary>S3 新迁的 3 页（同上，单独列出来便于定位）。</summary>
    public static IReadOnlyList<PageProbe> S3Pages { get; } = All
        .Where(p => p.Id is PageId.About or PageId.Changelog or PageId.KeepRoom)
        .ToList();

    /// <summary>受骨架守护的页面 = 已迁移的全部页面（原 <c>MigratedWindows</c> 的页面版，S3 起 10 页）。</summary>
    public static IReadOnlyList<PageProbe> SkeletonGuarded => All;

    // ==================== 纯逻辑批次（默认组） ====================

    /// <summary>跑所有不需要 WPF 上下文的页面化断言。</summary>
    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.6.0 S1+S2：页面化专项（纯逻辑） ════════");

        CheckPushForResult();
        CheckOverlaySemantics();
        CheckOverlayResultChannel();
        CheckShellCloseIntent();
        CheckShellCloseActions();
        CheckPageTransition();
        CheckTransitionReentrancy();
        CheckPopTo();
        CheckRoomBackDecision();
        CheckDetach();
        CheckNavKeyMap();

        // v2.6.0 S5 / T42：居中判据统一为 ≤1px 之后的**防漂守卫**（纯源码扫描，不需要 WPF）。
        CheckSingleCenteringCriterion();
    }

    /// <summary>P5：带结果返回 —— 页面给了结果就拿到结果，直接返回就是 null（取消）。</summary>
    private static void CheckPushForResult()
    {
        var factory = new FakeFactory();
        var nav = new NavigationService(factory);
        nav.Reset(new PageRequest(PageId.Login));

        var pending = nav.PushForResultAsync<string>(new PageRequest(PageId.InviteCode));
        factory.Last.AllowBack = true;
        nav.Complete("WW1:TEST-CODE");
        pending.Wait(TimeSpan.FromSeconds(2));
        Check.True(pending.IsCompleted, "P5：PushForResultAsync 在页面完成决策后结束");
        Check.Equal<string?>("WW1:TEST-CODE", pending.IsCompleted ? pending.Result : null,
            "P5：页面带结果出栈 → 调用方拿到该结果");

        var cancelled = nav.PushForResultAsync<string>(new PageRequest(PageId.About));
        factory.Last.AllowBack = true;
        nav.BackAsync().GetAwaiter().GetResult();
        cancelled.Wait(TimeSpan.FromSeconds(2));
        Check.True(cancelled.IsCompleted && cancelled.Result == null,
            "P5：页面直接返回（取消）→ 结果为 null");
    }

    /// <summary>
    /// P12：**两种入栈语义的分界**（T41 的实现侧判据 —— 先改实现、再补断言）。
    ///
    /// 要钉死的就是一件事：浮层式入栈（<see cref="NavigationService.PushOverlay"/> /
    /// <see cref="NavigationService.PushForResultAsync{T}"/>）**不许**碰下层页的生命周期 ——
    /// <list type="bullet">
    ///   <item>入栈时不给下层页调 <c>OnLeave</c>（否则房间页会被 Detach、收尾用的「退出房间」委托被注销）；</item>
    ///   <item>出栈时**也不给下层页调 <c>OnEnter</c>**（否则轮询与订阅会按浮层进出的次数翻倍）；</item>
    ///   <item>栈深只 +1（浮层自己占一层，不是 +0 也不是 +2）。</item>
    /// </list>
    /// 用 <see cref="FakePage"/> 数进出次数是最直接的观测手段：真页面的 <c>OnEnter</c> 是幂等的
    /// （<c>_vm != null</c> 就跳过），拿真页面反而看不出一半的事故。
    /// </summary>
    private static void CheckOverlaySemantics()
    {
        var factory = new FakeFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        var login = factory.Last;

        nav.Push(new PageRequest(PageId.RoomList));
        var list = factory.Last;
        Check.Equal(1, list.Enters, "P12：换页入栈后新页进场一次");
        Check.Equal(1, login.Leaves, "P12：换页入栈会让旧栈顶退场（Push 与浮层的分界就在这里）");

        nav.PushOverlay(new PageRequest(PageId.Settings));
        Check.Equal(3, nav.Depth, "P12：浮层式入栈让栈深恰好 +1（浮层自己占一层）");
        Check.True(nav.CurrentIsOverlay, "P12：浮层入栈后 CurrentIsOverlay == true");
        Check.Equal(0, list.Leaves, "P12：浮层式入栈**不**给下层页调 OnLeave（T41 的核心）");
        Check.Equal(1, list.Enters, "P12：浮层式入栈不给下层页调 OnEnter");

        // 同 PageId 已在栈中：先弹掉再压新的（N5），栈深不变。
        nav.PushOverlay(new PageRequest(PageId.Settings));
        Check.Equal(3, nav.Depth, "P12：重复打开同一个浮层不增加栈深（N5）");

        var popped = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(popped, "P12：浮层可以返回（CanGoBack == true）");
        Check.Equal(2, nav.Depth, "P12：浮层出栈后栈深回到 2");
        Check.Equal(0, list.Leaves, "P12：浮层出栈不给下层页补一次 OnLeave");
        Check.Equal(1, list.Enters, "P12：浮层出栈**不**让下层页重新进场（否则订阅会翻倍）");
        Check.False(nav.CurrentIsOverlay, "P12：回到普通页后 CurrentIsOverlay == false");

        // × 收起一层的入口（S2 新增）：只有栈顶确实是浮层才动手。
        Check.False(nav.DismissOverlay(), "P12：栈顶不是浮层时 DismissOverlay 返回 false（× 交回关闭意图表）");
        nav.PushOverlay(new PageRequest(PageId.InviteCode));
        Check.True(nav.DismissOverlay(), "P12：栈顶是浮层时 DismissOverlay 收起这一层");
        Check.Equal(2, nav.Depth, "P12：DismissOverlay 之后栈深 -1");
        Check.Equal(1, list.Enters, "P12：DismissOverlay 同样不会让下层页重新进场");

        // 没有等待者时 Complete 返回 false 且不动栈（页面被普通 Push 打开时的兜底）。
        var depthBefore = nav.Depth;
        Check.False(nav.Complete("nobody-waits"), "P12：没有等待者时 Complete 返回 false");
        Check.Equal(depthBefore, nav.Depth, "P12：没有等待者时 Complete 不动栈（页面不会莫名消失）");
    }


    /// <summary>
    /// P13：**结果通道只结算「被弹出的那一格」**（S2-QA 复核发现的 P2 的回归断言）。
    ///
    /// 事故形态（真实用户路径，无需并发）：
    /// 房间列表点「创建房间」（`PushForResultAsync` 在 await）→ 按 `Ctrl+,` 打开设置（浮层叠浮层）
    /// → 收起设置 → 回到创建房间页点「创建」→ **房间静默不建**。
    /// 根因是等待者原本挂在导航服务的**单个字段**上，`PopCore` 在任何一次出栈后都会把它判成「取消」；
    /// 修法是把等待者挂到**栈格**上，出栈时只结算被弹出的那一格。
    /// </summary>
    private static void CheckOverlayResultChannel()
    {
        var factory = new FakeFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        // —— 主场景：等结果的那一层被另一层浮层盖住再露出，等待必须还活着 ——
        var create = nav.PushForResultAsync<CreateRoomResult>(new PageRequest(PageId.CreateRoom));
        factory.Last.AllowBack = true;
        Check.Equal(3, nav.Depth, "P13：带结果返回的浮层同样只占一层（2 → 3）");

        nav.PushOverlay(new PageRequest(PageId.Settings));
        Check.Equal(4, nav.Depth, "P13：创建房间页之上还能再叠一层设置浮层（4）");

        Check.True(nav.DismissOverlay(), "P13：收起上层的设置浮层");
        Check.Equal(3, nav.Depth, "P13：收起后回到创建房间那层");
        Check.False(create.IsCompleted,
            "P13：收起**上层**浮层不会把下层的等待判成取消（P2 的回归点）");

        var delivered = nav.Complete(new CreateRoomResult("回归房", null, "werewolf"));
        Check.True(delivered, "P13：下层浮层之后仍能正常完成并带上结果");
        create.Wait(TimeSpan.FromSeconds(2));
        Check.True(create.IsCompleted && create.Result is { RoomName: "回归房" },
            "P13：调用方拿到的是页面真正交回的结果（不是 null）");

        // —— P3-1：同 PageId 复用弹掉旧格子时，旧格子的等待必须当场按「取消」结算 ——
        var first = nav.PushForResultAsync<CreateRoomResult>(new PageRequest(PageId.CreateRoom));
        var second = nav.PushForResultAsync<CreateRoomResult>(new PageRequest(PageId.CreateRoom));
        first.Wait(TimeSpan.FromSeconds(2));
        Check.True(first.IsCompleted && first.Result == null,
            "P13：连点两次「创建房间」—— 被复用的那一格按取消结算，不会永久挂住");
        Check.False(second.IsCompleted, "P13：第二次的等待仍在（它才是当前那一层）");
        nav.Complete(new CreateRoomResult("第二间", null, "werewolf"));
        second.Wait(TimeSpan.FromSeconds(2));
        Check.True(second.IsCompleted && second.Result is { RoomName: "第二间" },
            "P13：第二次的等待照常拿到结果");

        // —— P3-2：清栈（Reset）必须把在场格子的等待全部结算，不能悬挂 ——
        var pending = nav.PushForResultAsync<CreateRoomResult>(new PageRequest(PageId.CreateRoom));
        nav.Reset(new PageRequest(PageId.Login));
        pending.Wait(TimeSpan.FromSeconds(2));
        Check.True(pending.IsCompleted && pending.Result == null,
            "P13：Reset 会把在场浮层的等待按取消结算（清栈不留悬挂的 await）");
        Check.Equal(1, nav.Depth, "P13：Reset 之后栈深回到 1");
    }


    /// <summary>P6：外壳关闭意图 → 既有四值关闭来源（架构 §5.4）。</summary>
    private static void CheckShellCloseIntent()
    {
        Check.Equal(CloseIntentSource.RoomWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Room, false),
            "P6：房间页点 × → 只退出房间（RoomWindow）");

        Check.Equal(CloseIntentSource.RoomListWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.RoomList, false),
            "P6：房间列表页点 × → 完整收尾（RoomListWindow）");

        Check.Equal(CloseIntentSource.RoomListWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Login, false),
            "P6：登录页点 × → 完整收尾（复用 RoomListWindow 值）");

        Check.Equal(CloseIntentSource.ExitProgram,
            CloseIntent.Resolve(ShellCloseIntent.MenuExit, PageId.Room, false),
            "P6：菜单退出 / Ctrl+Q → 无论在哪页都完整收尾（ExitProgram）");

        Check.Equal(CloseIntentSource.SystemSession,
            CloseIntent.Resolve(ShellCloseIntent.SystemSession, PageId.Login, false),
            "P6：系统关机 / 注销 → SystemSession");

        Check.Equal(CloseIntentSource.ExitProgram,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Room, true),
            "P6：收尾进行中再点 × → 幂等放行为 ExitProgram（不再退房间 / 停服务器）");

        // 附：新枚举不得偷偷扩成四值（与 CloseIntentSource 的「4 值硬锁」同源纪律）。
        Check.Equal(3, Enum.GetValues<ShellCloseIntent>().Length, "P6：ShellCloseIntent 恰好三个值");
    }

    /// <summary>P7：房间页返回 = 退出房间，三种情形的判定。</summary>
    private static void CheckRoomBackDecision()
    {
        var leaveCalled = false;

        var notJoined = RoomBackDecision.ResolveAsync(false, () =>
        {
            leaveCalled = true;
            return Task.FromResult(true);
        }).GetAwaiter().GetResult();

        Check.True(notJoined, "P7：未进房 → 允许出栈（纯导航）");
        Check.False(leaveCalled, "P7：未进房 → 不白发一次 leaveroom 请求");

        var success = RoomBackDecision.ResolveAsync(true, () => Task.FromResult(true))
            .GetAwaiter().GetResult();
        Check.True(success, "P7：已进房且退出成功 → 允许出栈");

        var failed = RoomBackDecision.ResolveAsync(true, () => Task.FromResult(false))
            .GetAwaiter().GetResult();
        Check.False(failed, "P7：退出失败 → 留在房间里（不放行）");
    }

    /// <summary>P8 / P9：退订能力 —— 幂等 + 停轮询 + 来回 5 轮后订阅者不增加。</summary>
    private static void CheckDetach()
    {
        var client = new GameClient();

        var game = new GameViewModel(client);
        game.Detach();
        Check.True(game.IsDetached, "P8：GameViewModel.Detach() 之后 IsDetached == true");

        game.Detach();   // 幂等：第二次不应抛，也不应改变状态
        Check.True(game.IsDetached, "P8：GameViewModel.Detach() 幂等（第二次调用仍为 true）");

        var list = new RoomListViewModel(client);
        list.StartPolling();
        var pollingBefore = list.IsPolling;
        list.Detach();
        Check.True(pollingBefore, "P9：RoomListViewModel.StartPolling() 之后轮询确实在跑");
        Check.False(list.IsPolling, "P9：RoomListViewModel.Detach() 之后轮询停止");

        // 页面进出 5 轮：订阅者数量必须回到基线（这正是「页面化后按次数累积」的事故点）。
        var stateBefore = SubscriberCount(client, nameof(GameClient.StateReceived));
        for (var i = 0; i < 5; i++)
        {
            var vm = new GameViewModel(client);
            vm.Detach();
        }
        var stateAfter = SubscriberCount(client, nameof(GameClient.StateReceived));
        Console.WriteLine($"   · 房间页来回 5 轮：GameClient.StateReceived 订阅者 {stateBefore} → {stateAfter}");
        Check.Equal(stateBefore, stateAfter,
            $"P8：房间页来回 5 轮后 GameClient.StateReceived 订阅者不增加（{stateBefore} → {stateAfter}）");

        var changedBefore = StaticSubscriberCount(typeof(EmbeddedServerService), nameof(EmbeddedServerService.Changed));
        for (var i = 0; i < 5; i++)
        {
            var vm = new RoomListViewModel(client);
            vm.Detach();
        }
        var changedAfter = StaticSubscriberCount(typeof(EmbeddedServerService), nameof(EmbeddedServerService.Changed));
        Console.WriteLine($"   · 列表页来回 5 轮：静态 EmbeddedServerService.Changed 订阅者 {changedBefore} → {changedAfter}");
        Check.Equal(changedBefore, changedAfter,
            $"P9：列表页来回 5 轮后静态 Changed 订阅者不增加（{changedBefore} → {changedAfter}）");
    }

    /// <summary>P10：返回键位（Alt+← / Esc），栈底一律无动作。</summary>
    private static void CheckNavKeyMap()
    {
        Check.Equal(NavAction.Back, NavKeyMap.Resolve(Key.Left, ModifierKeys.Alt, true),
            "P10：Alt+← 在可返回时触发返回");
        Check.Equal(NavAction.Back, NavKeyMap.Resolve(Key.Escape, ModifierKeys.None, true),
            "P10：Esc 在可返回时触发返回");
        Check.Equal(NavAction.None, NavKeyMap.Resolve(Key.Escape, ModifierKeys.None, false),
            "P10：栈底（登录页）按 Esc 不做任何事");
    }

    // ==================== 界面批次（--ui 的 STA 线程） ====================

    /// <summary>跑所有需要真实布局的页面化断言（P1 / P2 / P3 / P4 / P11 + 页面骨架实测）。</summary>
    public static void RunUiChecks()
    {
        Console.WriteLine("   —— v2.6.0 S1+S2：页面化专项（导航栈 + 页面骨架实测 + 浮层闭环） ——");

        CheckCatalog();
        CheckNavigationStack();
        CheckPageSkeletons();
        CheckRealPageRoundTrip();
        CheckShellWindow();
        CheckShellCloseWiring();

        // ——— v2.6.0 S5 / T49：真客户区几何（设计尺寸组之上再补一组"真实视口"） ———
        RunRealViewportChecks();

        // ——— v2.6.0 S2：四个表单/设置页 ———
        CheckS2PageStructure();
        CheckOverlayRoundTrip();
        CheckPushWiring();

        // ——— v2.6.0 S3：关于 / 更新日志 / 保留房间 ———
        CheckS3PageStructure();
        CheckS3PushWiring();
    }

    /// <summary>
    /// 外壳窗口本身：XAML 能解析、只有一个页面宿主、关闭时不抛异常。
    /// 这是「程序启动只出现一个窗口」的最小可验证形式（真去 Show() 会弹窗，测试环境不允许）。
    /// </summary>
    private static void CheckShellWindow()
    {
        ShellWindow? shell = null;
        try
        {
            shell = new ShellWindow();
            Check.True(shell.Content is ContentControl, "S1：外壳窗口的内容是页面宿主（ContentControl）");
            Check.True(shell.MinWidth >= 760 && shell.MinHeight >= 520,
                $"S1：外壳最小尺寸不低于最小的页面（{shell.MinWidth:F0}×{shell.MinHeight:F0}）");
            Check.True(shell.FindName("PageHost") is ContentControl,
                "S1：外壳暴露唯一的页面宿主 PageHost（换页只换它的内容）");

            // 手动触发一次 Loaded（真去 Show() 会弹窗，测试环境不允许）：
            // 这一段覆盖的正是「程序启动只出现一个窗口 + 根页是登录页」。
            shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, shell));
            Check.True((shell.Content as ContentControl)?.Content is LoginPage,
                "S1：外壳启动后页面宿主里的内容就是登录页（唯一顶层窗口）");
            Check.True(shell.Title.Contains("登录"),
                $"S1：窗口标题随页变化（实际「{shell.Title}」）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S1 外壳窗口异常：{ex.Message}");
            Check.True(false, "S1：ShellWindow 能实例化（XAML 可解析）");
        }
        finally
        {
            try { shell?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    /// <summary>
    /// 真实三页闭环（S1 的交付判据）：登录页 → 房间列表页 → 房间页 → 返回列表 → 返回登录。
    /// 用的是真页面 + 真 <see cref="NavigationService"/>，只有「连接」是空的（不联网）。
    /// </summary>
    private static void CheckRealPageRoundTrip()
    {
        Console.WriteLine("   —— S1 自检：登录 → 列表 → 房间 → 返回列表 → 返回登录 ——");

        try
        {
            var session = new AppSession();
            var nav = session.Navigation;

            nav.Reset(new PageRequest(PageId.Login));
            Check.True(nav.Current is LoginPage, "S1：根页是登录页（唯一的顶层窗口里只有这一页）");
            Check.Equal(1, nav.Depth, "S1：登录页栈深 1（N1）");
            Check.False(nav.CanGoBack, "S1：登录页不允许返回（不画返回键）");

            nav.Push(new PageRequest(PageId.RoomList));
            Check.True(nav.Current is RoomListPage, "S1：登录成功 → 进入房间列表页");
            Check.True(nav.CanGoBack, "S1：房间列表页可以返回（Q1=B）");

            nav.Push(new PageRequest(PageId.Room));
            Check.True(nav.Current is RoomPage, "S1：加入房间 → 进入房间页");
            Check.Equal(3, nav.Depth, "S1：三个页面各占一层（登录 / 列表 / 房间）");

            // 房间页返回 = 退出房间；此时并未真正进房，因此是纯导航（不发 leaveroom）。
            var leftRoom = nav.BackAsync().GetAwaiter().GetResult();
            Check.True(leftRoom, "S1：房间页返回成功（未进房 → 纯导航）");
            Check.True(nav.Current is RoomListPage, "S1：从房间页返回后回到房间列表页");
            Check.Equal(2, nav.Depth, "S1：返回后栈深回到 2（只弹了一层）");

            // 列表页返回 = 回登录页：**不拆连接、不停服务器**（Q1=B 的导航侧）。
            var backToLogin = nav.BackAsync().GetAwaiter().GetResult();
            Check.True(backToLogin, "S1：房间列表页返回成功（Q1=B）");
            Check.True(nav.Current is LoginPage, "S1：从房间列表返回后回到登录页");
            Check.Equal(1, nav.Depth, "S1：回到登录页后栈深为 1（Q1=B 的深度判据）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S1 三页闭环异常：{ex.Message}");
            Check.True(false, "S1：三页闭环（登录 → 列表 → 房间 → 返回 ×2）不抛异常");
        }
    }

    /// <summary>
    /// 外壳关闭**动作**表（v2.6.0 S6 新增，默认组）：
    /// <c>ShutdownMode=OnExplicitShutdown</c> 下「窗口关闭 ≠ 程序退出」，所以「完整收尾 + 用户确认」
    /// 必须落到 <see cref="ShellCloseAction.AllowAndExitApp"/> —— 少这一步，进程会留在无窗口状态、
    /// 内置服务器变孤儿（S6 修的就是这个 bug）。这张表把四种动作全部钉死。
    /// </summary>
    private static void CheckShellCloseActions()
    {
        // ① 浮层在最上面：× = 只收起这一层（无论当前页是什么、在不在收尾）。
        Check.Equal(ShellCloseAction.DismissOverlay,
            CloseIntent.ResolveShellClose(ShellCloseIntent.WindowClose, PageId.Settings, false, true),
            "S6：× 在浮层上 = 收起这一层（不停服务器、不退出程序）");
        Check.Equal(ShellCloseAction.DismissOverlay,
            CloseIntent.ResolveShellClose(ShellCloseIntent.MenuExit, PageId.Room, false, true),
            "S6：菜单退出但栈顶是浮层时，同样先收起浮层");

        // ② 房间页 × = 退出房间（拦下关闭改走返回，I1）。
        Check.Equal(ShellCloseAction.CancelAndBack,
            CloseIntent.ResolveShellClose(ShellCloseIntent.WindowClose, PageId.Room, false, false),
            "S6：房间页 × = 退出房间并回列表（I1）");

        // ③ 列表页 / 登录页 × = 先问用户（真正会停服务器）。
        Check.Equal(ShellCloseAction.NeedConfirm,
            CloseIntent.ResolveShellClose(ShellCloseIntent.WindowClose, PageId.RoomList, false, false),
            "S6：房间列表页 × = 先弹关窗确认");
        Check.Equal(ShellCloseAction.NeedConfirm,
            CloseIntent.ResolveShellClose(ShellCloseIntent.WindowClose, PageId.Login, false, false),
            "S6：登录页 × = 先弹关窗确认");

        // ④ 菜单退出 / 正在收尾：无论当前页都要完整收尾（也要先过确认那一步）。
        Check.Equal(ShellCloseAction.NeedConfirm,
            CloseIntent.ResolveShellClose(ShellCloseIntent.MenuExit, PageId.Room, false, false),
            "S6：菜单退出程序 = 完整收尾（房间里也退）");
        Check.Equal(ShellCloseAction.NeedConfirm,
            CloseIntent.ResolveShellClose(ShellCloseIntent.WindowClose, PageId.Room, true, false),
            "S6：收尾进行中再点 × = 幂等放行（同样走确认后的退出应用）");

        // ⑤ 确认框的两种结果 —— S6 的核心：确认 → 必须**显式退出应用**。
        Check.Equal(ShellCloseAction.AllowAndExitApp, CloseIntent.AfterConfirm(true),
            "S6：用户确认关闭 → 放行关闭**并**请求退出应用（OnExplicitShutdown 下少了这一步就退不掉）");
        Check.Equal(ShellCloseAction.CancelOnly, CloseIntent.AfterConfirm(false),
            "S6：用户取消关闭 → 什么都不做（调用方还要清掉菜单退出标记）");

        // ⑥ 默认必须真退应用（只有测试宿主才会把开关关掉，且跑完会还原）。
        Check.True(AppShutdown.ExitApplication,
            "S6：默认必须真正退出应用（AppShutdown.ExitApplication 默认 true）");
    }

    /// <summary>
    /// S7 页面切换动画的方向表（默认组）。动画本身是视觉的、没法断言，
    /// 但「往哪边滑」完全由栈深变化决定 —— 这张表把
    /// 「关掉不动 / 首屏不动 / 入栈前进 / 出栈后退」钉死（架构 §26）。
    /// </summary>
    private static void CheckPageTransition()
    {
        // 关掉动画 → 栈深怎么变都不播（设置 → 主界面可关）。
        Check.Equal(PageSlide.None, PageTransition.Resolve(false, true, 2, 1),
            "S7：用户关掉页面切换动画 → 不播（设置里可关）");

        // 首屏没有旧页 → 没什么可滑的。
        Check.Equal(PageSlide.None, PageTransition.Resolve(true, false, 1, 0),
            "S7：首屏没有旧页 → 不播动画");

        // 入栈（含同层复用 / Reset 后首次压栈）→ 前进：新页从右侧滑入。
        Check.Equal(PageSlide.Forward, PageTransition.Resolve(true, true, 2, 1),
            "S7：入栈 → 新页从右侧滑入（前进）");
        Check.Equal(PageSlide.Forward, PageTransition.Resolve(true, true, 1, 1),
            "S7：深度不变（同层复用）也按前进处理");

        // 出栈 → 后退：旧页滑出右侧，新页从左侧回来（Android 返回手势）。
        Check.Equal(PageSlide.Backward, PageTransition.Resolve(true, true, 1, 2),
            "S7：出栈 → 旧页滑出右侧、新页从左侧回来（后退）");
        Check.Equal(PageSlide.Backward, PageTransition.Resolve(true, true, 2, 3),
            "S7：多层出栈同样判后退");

        // 参数区间：防止有人把动画调到 3 秒、或把视差设成 100%。
        Check.True(PageTransition.Duration > TimeSpan.Zero
            && PageTransition.Duration <= TimeSpan.FromMilliseconds(400),
            $"S7：动画时长落在 0–400ms（实际 {PageTransition.Duration.TotalMilliseconds:F0}ms）");
        Check.True(PageTransition.Parallax > 0 && PageTransition.Parallax < 0.5,
            $"S7：视差系数落在 0–0.5（实际 {PageTransition.Parallax}）");

        // 默认开着：用户没动过设置时就该有动画。
        Check.True(UiStateStore.State.PageTransitionAnimation,
            "S7：页面切换动画默认开启（设置 → 主界面可关）");
    }

    /// <summary>
    /// v2.6.0 S8：**连续两次导航**的收尾（退房询问那一类「浮层出栈 → 房间页出栈」就是它）。
    ///
    /// 现象是「点了按钮却停在原来那一页」：转场期间 <c>PageHost.Content</c> 装的是一个临时 Grid，
    /// 第二次导航若直接把它当「旧页」塞进下一个转场层，界面就再也回不到正确的那一页。
    /// 修法是「开新转场前先把上一场就地收尾」+「给每场转场发令牌，过期的 Completed 不再动手」。
    /// 动画本身是视觉的，但这两条**结构性**约束可以钉成源码断言。
    /// </summary>
    private static void CheckTransitionReentrancy()
    {
        try
        {
            var root = FindRepoRoot();
            var path = Path.Combine(root, "ClientGUI", "ShellWindow.xaml.cs");
            if (!File.Exists(path))
            {
                Check.True(false, "S8：ShellWindow.xaml.cs 存在（转场重入的收尾就在这里）");
                return;
            }

            var source = File.ReadAllText(path);

            // ① 开新转场前必须先收尾上一场，且**早于**取「旧页」。
            var settle = source.IndexOf("FinishTransition();", StringComparison.Ordinal);
            var readOutgoing = source.IndexOf("PageHost.Content as FrameworkElement", StringComparison.Ordinal);
            var buildLayer = source.IndexOf("var layer = new Grid", StringComparison.Ordinal);
            Check.True(settle >= 0, "S8：OnNavigated 里会就地收尾上一场转场（FinishTransition）");
            Check.True(readOutgoing >= 0 && settle < readOutgoing,
                "S8：先收尾上一场，再取「旧页」（否则拿到的是转场用的临时 Grid，不是页面）");
            Check.True(buildLayer >= 0 && settle < buildLayer,
                "S8：先收尾上一场，再搭新的转场层");

            // ② 每场转场有令牌：上一场遗留的 Completed 不许动后一场的状态。
            Check.True(source.Contains("_transitionToken"),
                "S8：转场带令牌（上一场动画的 Completed 不得收尾后一场转场）");
            Check.True(source.Contains("if (_transitionToken == token) FinishTransition();"),
                "S8：过期的 Completed 被丢弃（连续导航时界面不会停在中途那一页）");

            // ③ 退房后必须弹到房间列表，而不是「再问一次房间页能不能出栈」。
            var roomPath = Path.Combine(root, "ClientGUI", "Views", "Pages", "RoomPage.xaml.cs");
            Check.True(File.Exists(roomPath) && File.ReadAllText(roomPath).Contains("PopTo(PageId.RoomList)"),
                "S8：退房成功后直接 PopTo 房间列表（结论已定，不再过一遍返回拦截）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · S8 转场重入自检异常：" + ex.Message);
            Check.True(false, "S8：转场重入的收尾与令牌接线齐全");
        }
    }

    /// <summary>
    /// v2.6.0 S8：<see cref="NavigationService.PopTo"/> —— 结论已定时一路弹到目标页。
    /// 用假页面（不需要 WPF 上下文），所以能留在默认组。
    /// </summary>
    private static void CheckPopTo()
    {
        var nav = new NavigationService(new FakeFactory());
        nav.Reset(new PageRequest(PageId.Login));

        // ① 目标页不在栈里 → 不动栈、返回 false（宁可留在原页，也不能把根页弹掉）。
        nav.Push(new PageRequest(PageId.RoomList));
        nav.Push(new PageRequest(PageId.Room));
        Check.Equal(3, nav.Depth, "S8：PopTo 前栈深 3（登录 / 列表 / 房间）");
        Check.False(nav.PopTo(PageId.HostSettings), "S8：目标页不在栈里 → 返回 false");
        Check.Equal(3, nav.Depth, "S8：目标页不在栈里时栈保持不动（不会一路弹到根页）");

        // ② 一路弹到目标页：房间 → 列表 → 停在列表。
        Check.True(nav.PopTo(PageId.RoomList), "S8：一路弹到房间列表 → 返回 true");
        Check.Equal(2, nav.Depth, "S8：只弹掉房间这一层（栈深 3 → 2）");
        Check.True(nav.Current is not null && nav.Current.Id == PageId.RoomList,
            "S8：栈顶确实是房间列表页");

        // ③ 幂等：已经在目标页上再调一次，不多弹。
        Check.True(nav.PopTo(PageId.RoomList), "S8：栈顶已是目标页 → 仍然返回 true");
        Check.Equal(2, nav.Depth, "S8：重复 PopTo 不会多弹（幂等）");

        // ④ 根页保护：目标页就是栈底时，也不会把栈弹空。
        Check.True(nav.PopTo(PageId.Login), "S8：栈底（登录页）可以被认出来");
        Check.Equal(1, nav.Depth, "S8：弹到栈底之前会停住，根页永不弹出");
    }

    /// <summary>
    /// 外壳关闭意图的**接线**守卫（源码级）：分派逻辑本身由 <see cref="CloseIntent.Resolve"/> 的
    /// 6 条纯逻辑断言 + <see cref="CheckShellCloseActions"/> 的动作表钉死，这里只防「外壳不按那张表走」——
    /// 例如有人把 <c>RequiresFullShutdown</c> 的分支写反、忘了关窗前确认，
    /// 或者（S6 的真实 bug）放行关闭后没有请求退出应用。
    /// </summary>
    private static void CheckShellCloseWiring()
    {
        try
        {
            var root = FindRepoRoot();
            var path = Path.Combine(root, "ClientGUI", "ShellWindow.xaml.cs");
            if (!File.Exists(path))
            {
                Check.True(false, "S1：ShellWindow.xaml.cs 存在（唯一顶层窗口的关闭分派）");
                return;
            }

            var source = File.ReadAllText(path);

            Check.True(source.Contains("CloseIntent.ResolveShellClose("),
                "S1：外壳关闭走 CloseIntent.ResolveShellClose（不在外壳里另写一套判断）");
            Check.True(source.Contains("CloseIntent.AfterConfirm("),
                "S6：确认框的两种结果也走 CloseIntent.AfterConfirm（不放任外壳自行解释）");
            Check.True(source.Contains("AppShutdown.ConfirmClose(this)"),
                "S1：真正会停服务器的入口仍保留关窗确认（Q4 裁定）");
            Check.True(source.Contains("RequestExit"),
                "S6：完整收尾放行后必须显式请求退出应用（OnExplicitShutdown 下窗口关闭不会让程序退出）");
            Check.True(source.Contains("AppShutdown.ExitApplication"),
                "S6：退出应用那一步经 AppShutdown.ExitApplication 开关（测试宿主据此不跟着进程一起退）");
            Check.True(source.Contains("_menuExitRequested = false"),
                "S6：用户取消退出后必须清掉菜单退出标记，否则下一次 × 会被误判成退出程序");

            // v2.6.0 S7：页面切换动画的接线（架构 D6 的「不做动画」已按用户要求推翻，见 §26）。
            Check.True(source.Contains("PageTransition.Resolve("),
                "S7：切页方向走 PageTransition.Resolve 纯函数（不在外壳里另写一套判断）");
            Check.True(source.Contains("BeginAnimation("),
                "S7：外壳确实在播切换动画（不是只写了判定、没真的播）");
            Check.True(source.Contains("FinishTransition"),
                "S7：动画一结束就拆掉临时层 —— 静止态回到「PageHost.Content 就是当前页」，既有断言才不用改");
            Check.Equal(2, CountOccurrences(source, "Navigation.BackAsync()"),
                "S1：× 与返回键位都只经 NavigationService.BackAsync 这一个出口（N9）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S1 外壳接线守卫异常：{ex.Message}");
            Check.True(false, "S1：外壳关闭分派接线可检查");
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }

    /// <summary>从可执行目录往上找仓库根（有 <c>docs/TODO.md</c> 与 <c>ClientGUI</c> 的目录）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ClientGUI")) &&
                File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>P11：页面表自检（防止悄悄少迁一页 —— 页面没进表 = 等于没迁）。</summary>
    private static void CheckCatalog()
    {
        // 页数契约 S3 由 7 改成 10：十个 PageId 全部有页面（规格变更，报告里逐条列明）。
        Check.Equal(10, All.Count, $"P11：S1+S2+S3 已迁移页面共十页（实际 {All.Count}）");
        Check.Equal(4, S2Pages.Count, $"P11：S2 的四页都在表里（实际 {S2Pages.Count}）");
        Check.Equal(3, S3Pages.Count, $"P11：S3 的三页都在表里（实际 {S3Pages.Count}）");
        Check.Equal(Enum.GetValues<PageId>().Length, All.Count,
            $"P11：页面表条数 = PageId 枚举值个数（{All.Count} = {Enum.GetValues<PageId>().Length}，不留未迁的枚举值）");
        Check.Equal(All.Count, SkeletonGuarded.Count,
            "P11：受骨架守护的页面数 = 已迁移页面数（不多不少）");
        Check.Equal(All.Count, All.Select(p => p.Id).Distinct().Count(),
            "P11：页面标识互不重复（重复会让防重复入栈保护失效）");
    }

    /// <summary>P1–P4：导航栈的入栈 / 出栈 / 深度 / 防重复 / 栈底不出栈。</summary>
    private static void CheckNavigationStack()
    {
        var factory = new FakeFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        Check.Equal(1, nav.Depth, "P1：Reset 之后栈深为 1");
        Check.False(nav.CanGoBack, "P1：根页不允许返回（登录页不画返回键）");
        Check.Equal(PageId.Login, nav.Current?.Id, "P1：Reset 后的当前页就是根页");
        Check.Equal(1, factory.Last.Enters, "P2：入栈（Reset）会给新页调一次 OnEnter");

        var login = factory.Last;
        nav.Push(new PageRequest(PageId.RoomList));
        Check.Equal(2, nav.Depth, "P2：Push 之后栈深 +1");
        Check.Equal(1, login.Leaves, "P2：Push 时旧栈顶成对收到 OnLeave（不再轮询 / 已退订）");
        Check.Equal(1, factory.Last.Enters, "P2：Push 时新页收到 OnEnter");

        var pushed = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(pushed, "P2：BackAsync 成功返回 true");
        Check.Equal(1, nav.Depth, "P2：BackAsync 之后栈深 -1");
        Check.Equal(1, pushed ? factory.Created[1].Leaves : -1,
            "P2：BackAsync 会给出栈页调 OnLeave（退订成对）");

        nav.Push(new PageRequest(PageId.RoomList));
        nav.Push(new PageRequest(PageId.RoomList));
        Check.Equal(2, nav.Depth, "P3：同 PageId 重复 Push 不增加栈深（防重复入栈）");

        while (nav.Depth > 1) nav.BackAsync().GetAwaiter().GetResult();
        var bottomBack = nav.BackAsync().GetAwaiter().GetResult();
        Check.False(bottomBack, "P4：栈底 BackAsync 返回 false");
        Check.Equal(1, nav.Depth, "P4：栈底 BackAsync 之后栈深不变");
    }

    /// <summary>
    /// 页面骨架实测（页面形态下守住 I5 / I8）：
    /// 每页都要能布局、导航栏 ≥56、标题几何居中（阈值与窗口形态**完全相同，未放宽**）、
    /// 带主按钮的页面主按钮 ≥44 高且通栏 ≥80%。
    /// </summary>
    private static void CheckPageSkeletons()
    {
        foreach (var probe in SkeletonGuarded)
        {
            FrameworkElement? page = null;
            try
            {
                var session = new AppSession();
                page = probe.Create(session);

                Arrange(page, probe.DesignWidth, probe.DesignHeight);
                Check.True(page.ActualWidth > 0 && page.ActualHeight > 0,
                    $"页面实测：{probe.Name} 能完成布局（{page.ActualWidth:F0}×{page.ActualHeight:F0}）");

                var bar = page.FindName("NavBar") as FrameworkElement;
                Check.True(bar != null, $"页面实测：{probe.Name} 能取到导航栏（NavBar）");
                if (bar != null)
                    Check.True(bar.ActualHeight >= 56,
                        $"页面实测：{probe.Name} 导航栏实测高 ≥56（实际 {bar.ActualHeight:F1}）");

                FrameworkElement? title = page.FindName("NavTitleText") as FrameworkElement;
                title ??= page.FindName("NavTitleButton") as FrameworkElement;
                Check.True(title != null, $"页面实测：{probe.Name} 能取到导航标题");
                if (title != null && bar != null) VerifyTitleCentered(page, bar, title, probe.Name);

                // 十页**每页都进**（不再 `if (probe.ExpectPrimary)` 预筛）：没有主按钮的页由
                // MeasurePrimary(required: false) 记一条「按设计无此按钮」的绿断言，
                // 这样每一页的断言条数都是稳定的、可对账的（S4 收尾修正，堵住静默吞断言）。
                CheckPrimary(page, probe);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · 页面实测（{probe.Name}）异常：{ex.Message}");
                Check.True(false, $"页面实测：{probe.Name} 骨架测量无异常");
            }
        }
    }

    /// <summary>
    /// 导航标题是否真的落在导航栏的水平中心 —— **全测试工程唯一的居中判据**（v2.6.0 S5 / T42）。
    ///
    /// 判据：**偏移 ≤ 1px**（像素绝对值）。
    /// <para>
    /// 为什么删掉了原来那条「≤ max(2px, 导航栏宽 5%)」：它对任何导航栏都恒 ≥ 2px，
    /// 比 1px **松**；两套口径并存的结果是「同一个页面，A 文件说合格、B 文件说不够」，
    /// 谁都能挑对自己有利的那把尺子 —— 这正是 T42 要收的口子。
    /// </para>
    /// 现在这条判据只写在**这一个方法**里，`PageCatalog` 与 `V25BatchProbe` 都调它；
    /// 另加一条源码守卫（<see cref="CheckSingleCenteringCriterion"/>）禁止别处再写出旧公式，
    /// 免得以后又漂出第二个口径。日志里仍打印「偏移 / 导航栏宽」两个数，便于排查。
    /// </summary>
    public static void VerifyTitleCentered(FrameworkElement root, FrameworkElement bar, FrameworkElement title, string label)
    {
        try
        {
            var titleLeft = title.TranslatePoint(new Point(0, 0), root).X;
            var barLeft = bar.TranslatePoint(new Point(0, 0), root).X;
            var offset = Math.Abs((titleLeft + title.ActualWidth / 2) - (barLeft + bar.ActualWidth / 2));

            Console.WriteLine($"   · {label}：标题居中（偏移 {offset:F2}px / 导航栏宽 {bar.ActualWidth:F0}）");
            Check.True(offset <= 1.0,
                $"居中判据（T42 统一为 ≤1px）：{label} 标题偏移 {offset:F2}px");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · {label} 居中无法换算坐标：{ex.Message}");
            Check.True(false, $"居中判据（T42）：{label} 标题居中可测量");
        }
    }

    /// <summary>
    /// T42 的**防漂守卫**：居中判据只允许出现在 <see cref="VerifyTitleCentered"/> 一处。
    /// 扫描我负责的三个文件，禁止再出现旧的「≤ max(2px, 导航栏宽 5%)」公式。
    ///
    /// 只扫这三个（不含 QA 的 guard）：`AppShellQaGuard.cs` 里目前仍是旧公式，属 QA 的文件，
    /// 按协作纪律我不改 —— 已在消息里通报，由她并入 S5 收口。
    /// </summary>
    private static void CheckSingleCenteringCriterion()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            Check.True(false, "T42：能定位仓库根目录（防漂守卫的取样前提）");
            return;
        }

        var mine = new[] { "PageCatalog.cs", "V25BatchProbe.cs", "UiSmokeCheck.cs" };
        var offenders = new List<string>();

        foreach (var name in mine)
        {
            var path = Path.Combine(root, "ClientGUI.Tests", name);
            if (!File.Exists(path))
            {
                offenders.Add($"{name}:缺失");
                continue;
            }

            var code = StripLineComments(File.ReadAllText(path));

            // ⚠ 令牌用 + 拼出来，**不能写字面量**：本守卫自己就在 PageCatalog.cs 里，
            //   写成字面量会让它扫到自己（第一次运行就是这么红的：期望 0、实际 2）。
            //   拼接之后源码里不再出现那段连续文本，检测才算真的在找"别处有没有复活"。
            foreach (var token in new[] { "Math.Max" + "(2.0,", "Math.Max" + "(2," })
            {
                if (code.Contains(token, StringComparison.Ordinal)) offenders.Add($"{name}:{token}");
            }
        }

        Check.Equal(0, offenders.Count,
            "T42：居中判据只有一处（禁止旧的 max(2px, 5%) 公式复活，残留：" + string.Join(",", offenders) + "）");
    }

    /// <summary>
    /// 主按钮通栏：登录页两个标签页各量一次（未选中的页没有视觉树），其余页面量 <c>PrimaryAction</c>。
    ///
    /// ⚠ **S4 收尾修正（QA 恒真扫描，必修 2）**：原实现是
    /// <code>if (page.FindName("Tabs") is TabControl tabs &amp;&amp; page.FindName("EmbeddedPrimaryAction") != null) { …6 条… } else { …3 条… }</code>
    /// —— 登录页一旦丢掉 <c>Tabs</c>（或首个标签页主按钮改名），就会**静默落进 else**：
    /// 登录页 6 条降成 3 条，**不红**，只有对账时才发现条数少了。这比恒绿更危险（恒绿至少还占着条数）。
    /// 现在改成**按页面标识显式分派 + 缺件判红**，任何一条缺失都会立刻变红，不会再无声降级。
    /// </summary>
    private static void CheckPrimary(FrameworkElement page, PageProbe probe)
    {
        // 登录页是唯一「标签页里各有一个主按钮」的页面（内置服务器页 / 连接现有服务器页）。
        if (probe.Id == PageId.Login)
        {
            var tabs = page.FindName("Tabs") as TabControl;
            Check.True(tabs != null, "页面实测：登录页仍有 TabControl（Tabs）");
            Check.True(page.FindName("EmbeddedPrimaryAction") != null,
                "页面实测：登录页的「内置服务器」标签页仍有主按钮（EmbeddedPrimaryAction）");

            if (tabs == null)
            {
                // 已经红了；再量一次兜底，保证「登录页的断言数」不会因为这一处结构变化而掉。
                MeasurePrimary(page, "PrimaryAction", probe.Name, required: true);
                return;
            }

            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Arrange(page, probe.DesignWidth, probe.DesignHeight);
                MeasurePrimary(page, i == 0 ? "EmbeddedPrimaryAction" : "PrimaryAction",
                    $"{probe.Name}·第{i + 1}页", required: true);
            }

            tabs.SelectedIndex = 0;
            Arrange(page, probe.DesignWidth, probe.DesignHeight);
            return;
        }

        // 表里 <c>ExpectPrimary == false</c> 的页面（房间 / 关于 / 更新日志）按设计就没有通栏主按钮。
        // 仍然走一次 MeasurePrimary（required = false）—— 它会产生一条「本页按设计无此按钮」的绿断言，
        // 而不是让这个分支**直接 return**（那又会变成"条数无声消失"）。
        MeasurePrimary(page, "PrimaryAction", probe.Name, required: probe.ExpectPrimary);
    }

    /// <summary>
    /// 量一个主按钮的「宽 &gt; 0 / 高 ≥ 44 / 通栏 ≥ 80%」（3 条断言）。
    ///
    /// ⚠ **S4 收尾修正（必需 1）**：原实现第一行是
    /// <code>if (page.FindName(elementName) is not FrameworkElement button) return;</code>
    /// —— 按钮被删或改名时**一条断言都不产生**。而 <c>ExpectPrimary == true</c> 的 7 页全靠这里，
    /// 每页 3 条 ⇒ **最多 21 条断言可以无声消失**（`--ui` 只掉数、零红）。
    /// 现在这条路径**任何分支都至少产生 1 条断言** —— 「要不要量」由调用方的表（`ExpectPrimary`）决定，
    /// 「量不到」在 required 时判红、在非 required 时记一条显式绿断言。**没有一条路径是静默 return 的。**
    /// <paramref name="required"/> 为真（表里 <c>ExpectPrimary == true</c>）时找不到按钮直接判红；
    /// 为假时也留一条显式断言（「本页按设计无此按钮」），**条数可对账，是这条防线成立的前提**。
    /// </summary>
    private static void MeasurePrimary(FrameworkElement page, string elementName, string label, bool required = false)
    {
        if (page.FindName(elementName) is not FrameworkElement button)
        {
            // required = 调用方按表判定「这一页必须有通栏主按钮」→ 缺件是结构性回归，判红。
            // required = false（房间 / 关于 / 更新日志：按设计就没有通栏主按钮）→ 仍记一条绿，
            // 这样「这一页为什么没量」是**有据可查**的，而不是函数悄悄 return 掉。
            Check.True(!required,
                $"页面实测：{label} 有主按钮 {elementName}（表里 ExpectPrimary=true，缺件属结构性回归）");
            Console.WriteLine($"   · {label} 无 {elementName}（表里 ExpectPrimary={required}，按设计不量通栏）");
            return;
        }

        Check.True(page.ActualWidth > 0, $"页面实测：{label} 内容区已完成布局（宽 {page.ActualWidth:F0}）");
        if (page.ActualWidth <= 0) return;

        var ratio = button.ActualWidth / page.ActualWidth;
        Console.WriteLine($"   · {label} 主按钮通栏占比：{button.ActualWidth:F0}/{page.ActualWidth:F0} = {ratio:P0}");

        Check.True(button.ActualHeight >= 44,
            $"页面实测：{label} 主按钮热区 ≥44（实际 {button.ActualHeight:F1}）");
        Check.True(ratio >= 0.8,
            $"页面实测：{label} 主按钮通栏 ≥80%（{button.ActualWidth:F0}/{page.ActualWidth:F0} = {ratio:P0}）");
    }

    // ==================== v2.6.0 S5 · T49：真客户区几何（含 760×520 临界组） ====================

    /// <summary>
    /// **T49：把页面几何从"静态视口"测到"真显示的外壳客户区"**，并补上最小窗口（760×520）临界组。
    ///
    /// 为什么这组必须存在 —— 现有两套视口都不是运行时真值：
    /// <list type="bullet">
    ///   <item><b>设计尺寸组</b>（<see cref="CheckPageSkeletons"/>）：沿用旧窗口 `ClientSize`，
    ///         房间页 1220×780、登录页 470×792，**比外壳 1020×720 还大**，运行时根本不存在；</item>
    ///   <item><b>1020×720 组</b>（QA 那份）：是"外壳尺寸"的静态复刻，不是客户区
    ///         （客户区 = 窗口 − 非工作区边框，还会随 DPI / 拖拽变化）。</item>
    /// </list>
    /// 于是"窗口缩到最小还能不能用"从来没人验过：`MinWidth/MinHeight = 760×520` 时，
    /// 主按钮还够不够 80% 通栏、导航标题还居不居中，**都是未知**。本方法就是补这个缺口。
    ///
    /// 三组**并存**、各验各的（阈值一个都没放宽：≤1px / ≥56 / ≥44 / ≥80%）：
    /// <list type="number">
    ///   <item>设计尺寸组：内容宽度契约（页面自身体形不缩水）；</item>
    ///   <item>真客户区组：默认窗口尺寸下，装进 `PageHost` 后还满足三条判据；</item>
    ///   <item>760×520 临界组：缩到最小窗口仍满足三条判据。</item>
    /// </list>
    ///
    /// 环境不支持离屏 `Show()` 时**不留静默分支**：产出一条显式断言说明"未拿到真实视口"，
    /// 并降级按 `MinWidth/MinHeight` 静态复刻继续跑（条数不会因环境退化而消失）。
    /// </summary>
    public static void RunRealViewportChecks()
    {
        Console.WriteLine("   —— v2.6.0 S5 · T49：真客户区几何（默认视口 + 760×520 临界组） ——");

        ShellWindow? shell = null;

        // ⚠ **Q-T49-01 回修**：真客户区组的尺寸必须**跨机器可复现**，否则"主按钮 ≥80%"
        //   这一条的严格程度会随机器漂。
        //
        //   第一版为什么不可复现：`ShellWindow.Loaded` 会调 `RestoreWindowState()`，它读
        //   `UiStateStore.State` 的 **Width/Height** 并覆盖 XAML 声明的 1020×720。
        //   而 `UiState` 的 C# 默认值是 **1220 / 780**（沿用旧 `MainWindow` 的设计尺寸）——
        //   所以第一版量到的是 1205×743（1220×780 减非工作区），**不是外壳声明尺寸**。
        //   换台机器只要 `UiState` 默认值或持久化内容不同，这组就换了视口。
        //
        //   修法（两道保险，都不改阈值）：
        //   ① `UiStateStore.Enabled = false`（走内存默认值、**不读磁盘**），`finally` 还原；
        //   ② `Show()` 之后把窗口尺寸**显式压回 XAML 声明值**（Show 前先抓下来）——
        //      这样无论 `RestoreWindowState` 从哪儿拿到什么值，量到的都是外壳的设计视口。
        var previousEnabled = UiStateStore.Enabled;
        UiStateStore.Enabled = false;
        try
        {
            shell = new ShellWindow();

            // Show() 还没发生，此时 Width/Height 仍是 XAML 声明值（1020×720）。
            var designWidth = shell.Width;
            var designHeight = shell.Height;

            // 离屏显示：只为拿到真实的布局与客户区尺寸，不在用户屏幕上闪一下。
            // ⚠ 顺序很重要：先 Show()（会触发 Loaded → RestoreWindowState + Reset(LoginPage)），
            //   之后再设尺寸 / 塞页面 —— 反过来会被 Loaded 覆盖掉（S4 在菜单用例上栽过一次）。
            var shown = false;
            try
            {
                shell.WindowStartupLocation = WindowStartupLocation.Manual;
                shell.ShowActivated = false;
                shell.Left = -32000;
                shell.Top = -32000;
                shell.Show();
                shell.UpdateLayout();
                shown = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("   · T49：环境无法离屏显示外壳窗口：" + ex.Message);
            }

            // 显式断言（不是静默跳过）：环境退化时这条会红，条数不会消失。
            Check.True(shown, "T49：能离屏显示外壳窗口并从 PageHost 读到真实客户区");

            // ② 把窗口压回外壳的设计尺寸（消除 RestoreWindowState 带来的漂移）。
            if (shown)
            {
                shell.Width = designWidth;
                shell.Height = designHeight;
                shell.UpdateLayout();
            }

            Console.WriteLine($"   · T49 外壳设计尺寸：{designWidth:F0}×{designHeight:F0}（XAML 声明值，Show 前抓取）");

            var viewport = shown
                ? shell.PageHostSize
                : new Size(designWidth, designHeight);
            Console.WriteLine($"   · T49 真客户区：{viewport.Width:F0}×{viewport.Height:F0}" +
                              $"（来源：{(shown ? "PageHost.ActualWidth/Height" : "设计尺寸静态复刻")}，" +
                              $"窗口 {shell.Width:F0}×{shell.Height:F0}）");
            Check.True(viewport.Width > 0 && viewport.Height > 0,
                $"T49：客户区尺寸有效（{viewport.Width:F0}×{viewport.Height:F0}）");

            // 客户区必须小于窗口：这是"客户区 ≠ 窗口尺寸"的直接证据，也是本组存在的理由。
            if (shown)
            {
                Check.True(viewport.Width <= shell.Width + 0.5 && viewport.Height <= shell.Height + 0.5,
                    $"T49：客户区不超过窗口尺寸（客户区 {viewport.Width:F0}×{viewport.Height:F0} / " +
                    $"窗口 {shell.Width:F0}×{shell.Height:F0}）");

                // Q-T49-01 的可复现性判据：量到的窗口必须回到外壳声明值，
                // 否则说明又被 `RestoreWindowState`（UiState 默认 / 持久化）带跑了。
                Check.True(Math.Abs(shell.Width - designWidth) < 0.5 &&
                           Math.Abs(shell.Height - designHeight) < 0.5,
                    $"T49：真客户区组用的是外壳设计尺寸（{shell.Width:F0}×{shell.Height:F0}，" +
                    $"应等于 {designWidth:F0}×{designHeight:F0}）");
            }

            MeasureAllPagesInShell(shell, shown, "真客户区");

            // ——— 760×520 临界组：缩到最小窗口再跑一遍同一套判据 ———
            if (shown)
            {
                shell.Width = shell.MinWidth;
                shell.Height = shell.MinHeight;
                shell.UpdateLayout();
            }

            var minViewport = shown
                ? shell.PageHostSize
                : new Size(shell.MinWidth, shell.MinHeight);
            Console.WriteLine($"   · T49 最小窗口客户区：{minViewport.Width:F0}×{minViewport.Height:F0}" +
                              $"（MinWidth/MinHeight = {shell.MinWidth:F0}×{shell.MinHeight:F0}）");
            Check.True(minViewport.Width > 0 && minViewport.Height > 0,
                $"T49：最小窗口客户区尺寸有效（{minViewport.Width:F0}×{minViewport.Height:F0}）");
            Check.True(minViewport.Width <= shell.MinWidth + 0.5 && minViewport.Height <= shell.MinHeight + 0.5,
                "T49：最小窗口的客户区不超过 MinWidth/MinHeight（否则说明尺寸约束没生效）");

            MeasureAllPagesInShell(shell, shown, "760×520 临界组");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · T49 真客户区几何异常：" + ex);
            Check.True(false, "T49：真客户区几何（默认视口 + 760×520 临界组）可测");
        }
        finally
        {
            try { shell?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
            UiStateStore.Enabled = previousEnabled;   // 还原（不影响同一批里后面的用例）
        }
    }

    /// <summary>
    /// 在当前外壳视口下逐页复测三条判据（导航栏 ≥56 / 标题居中 ≤1px / 主按钮 ≥44 且 ≥80% 通栏）。
    /// <paramref name="live"/> 为真时把页面真正塞进 `PageHost` 让 WPF 自己布局（拿到的就是运行时几何）；
    /// 为假时（环境无法显示）按 `MinWidth/MinHeight` 静态复刻，判据不变、条数不减。
    /// </summary>
    private static void MeasureAllPagesInShell(ShellWindow shell, bool live, string groupLabel)
    {
        var host = shell.FindName("PageHost") as ContentControl;
        Check.True(host != null, $"T49（{groupLabel}）：外壳暴露唯一的页面宿主 PageHost");
        if (host == null) return;

        var fallback = new Size(shell.MinWidth, shell.MinHeight);

        foreach (var probe in All)
        {
            try
            {
                var page = probe.Create(new AppSession());

                if (live)
                {
                    host.Content = page;       // 真布局：由 ContentControl 撑满客户区
                    shell.UpdateLayout();
                }
                else
                {
                    page.Measure(fallback);
                    page.Arrange(new Rect(0, 0, fallback.Width, fallback.Height));
                    page.UpdateLayout();
                }

                // ① 导航栏 ≥56（阈值与设计尺寸组逐字相同，未放宽）
                var bar = page.FindName("NavBar") as FrameworkElement;
                if (bar == null)
                {
                    Check.True(false, $"T49（{groupLabel}）：{probe.Name} 有导航栏（NavBar）");
                    continue;
                }

                Check.True(bar.ActualHeight >= 56,
                    $"T49（{groupLabel}）：{probe.Name} 导航栏 ≥56（实测 {bar.ActualHeight:F1}）");

                // ② 标题居中 ≤1px（复用 T42 统一后的唯一判据）
                FrameworkElement? title = page.FindName("NavTitleText") as FrameworkElement
                                          ?? page.FindName("NavTitleButton") as FrameworkElement;
                if (title == null)
                {
                    Check.True(false, $"T49（{groupLabel}）：{probe.Name} 有导航标题");
                }
                else
                {
                    VerifyTitleCentered(page, bar, title, $"{probe.Name}（{groupLabel}）");
                }

                // ③ 主按钮：热区 ≥44 + 通栏 ≥80%（登录页两个标签页各量一次）
                MeasurePrimaryInViewport(page, probe, groupLabel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · T49（{groupLabel}）{probe.Name} 异常：{ex.Message}");
                Check.True(false, $"T49（{groupLabel}）：{probe.Name} 视口几何可测");
            }
        }
    }

    /// <summary>
    /// 真视口下的主按钮测量 —— 与 <see cref="MeasurePrimary"/> **判据相同**（≥44 高 / ≥80% 通栏），
    /// 但不做 `Arrange(page, DesignWidth, DesignHeight)`（那会把刚拿到的真实视口覆盖回设计尺寸）。
    /// 没有主按钮的页由 `ExpectPrimary` 决定：为真判红（缺件），为假记一条"按设计无此按钮"的绿。
    /// </summary>
    private static void MeasurePrimaryInViewport(FrameworkElement page, PageProbe probe, string groupLabel)
    {
        if (probe.Id == PageId.Login && page.FindName("Tabs") is TabControl tabs)
        {
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                page.UpdateLayout();
                MeasureOne(page, i == 0 ? "EmbeddedPrimaryAction" : "PrimaryAction",
                    $"{probe.Name}·第{i + 1}页（{groupLabel}）", required: true);
            }

            tabs.SelectedIndex = 0;
            page.UpdateLayout();
            return;
        }

        MeasureOne(page, "PrimaryAction", $"{probe.Name}（{groupLabel}）", probe.ExpectPrimary);
    }

    /// <summary>量单个主按钮（真视口版）：找不到时按 <paramref name="required"/> 判红或记绿，绝不静默。</summary>
    private static void MeasureOne(FrameworkElement page, string elementName, string label, bool required)
    {
        if (page.FindName(elementName) is not FrameworkElement button)
        {
            Check.True(!required, $"T49：{label} 有主按钮 {elementName}（表里 ExpectPrimary=true，缺件属结构性回归）");
            return;
        }

        var content = page.ActualWidth;
        Check.True(content > 0, $"T49：{label} 内容区已完成布局（宽 {content:F0}）");
        if (content <= 0) return;

        Console.WriteLine($"   · T49 {label} 主按钮：{button.ActualHeight:F0}px 高，" +
                          $"通栏 {button.ActualWidth / content:P0}（{button.ActualWidth:F0}/{content:F0}）");

        Check.True(button.ActualHeight >= 44,
            $"T49：{label} 主按钮热区 ≥44（实测 {button.ActualHeight:F1}）");
        Check.True(button.ActualWidth / content >= 0.8,
            $"T49：{label} 主按钮通栏 ≥80%（{button.ActualWidth:F0}/{content:F0} = " +
            $"{button.ActualWidth / content:P0}）");
    }

    /// <summary>
    /// 页面没有 Width/Height，用**设计尺寸**量一次（与窗口形态的 <c>Layout(root, window)</c> 等价）。
    ///
    /// ⚠ 这里的宽度是**设计尺寸**（沿用各自旧窗口的 `ClientSize`，代表"内容宽度一致、视觉不跳"这条契约），
    /// 与**运行时视口**不是一回事：S4 之后所有页面都装在 <see cref="ClientGUI.ShellWindow"/> 的
    /// 1020×720 <c>PageHost</c>（<c>ContentControl</c> 撑满）里，所以房间页的 1220×780、登录页的 470×792
    /// 在运行时**并不存在**。因此：
    /// <list type="bullet">
    ///   <item>本文件的「导航栏 ≥56 / 标题居中 / 主按钮通栏」量的都是设计尺寸 —— 它保证的是**页面自身体形不缩水**；</item>
    ///   <item>「装进外壳后还够不够宽」由 <c>AppShellQaGuard</c> 的 1020×720 那一组另行实测（T37 分工）。</item>
    /// </list>
    /// 不要用本组数据去下「运行时主按钮通栏够宽」的结论。
    /// </summary>
    private static void Arrange(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    // ==================== v2.6.0 S2：四页的结构契约与浮层闭环 ====================

    /// <summary>
    /// S2 四页的**结构契约**：把旧窗口那套命名契约原样搬过来比对。
    /// 只加不减 —— 旧窗口的那几条断言在 S4 删窗口之前一条都不动。
    /// </summary>
    private static void CheckS2PageStructure()
    {
        Console.WriteLine("   —— v2.6.0 S2：四个表单/设置页的结构契约 ——");

        var session = new AppSession();

        // ——— 创建房间页 ———
        try
        {
            var page = new CreateRoomPage(session);
            var gameBox = page.FindName("GameBox") as ComboBox;

            Check.True(gameBox != null, "S2：创建房间页有游戏下拉（GameBox）");
            Check.True(gameBox?.ItemTemplate != null,
                "S2：GameBox 仍带 ItemTemplate（不写成未本地化的类型名）");
            Check.True(page.FindName("RoomNameInput") is TextBox, "S2：创建房间页有房间名输入框");
            Check.True(page.FindName("PasswordInput") is PasswordBox, "S2：创建房间页有密码框");
            Check.True(page.FindName("PrimaryAction") is Button, "S2：创建房间页有通栏主按钮（创建）");
            Check.Equal(PageId.CreateRoom, page.Id, "S2：创建房间页的页面标识正确");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 创建房间页结构异常：{ex.Message}");
            Check.True(false, "S2：创建房间页能实例化并取到结构契约里的控件");
        }

        // ——— 房主设置页 ———
        try
        {
            var page = new HostSettingsPage(session);
            var tabs = page.FindName("Tabs") as TabControl;

            Check.True(tabs != null, "S2：房主设置页仍有 Tabs（TabControl）");
            Check.Equal(3, tabs?.Items.Count ?? -1, "S2：房主设置页仍是三个标签页（房间/身份配置/邀请码）");
            Check.Equal("房间|身份配置|邀请码", HeaderText(tabs),
                "S2：房主设置页的标签顺序保持 房间/身份配置/邀请码");

            Check.True(page.FindName("LayoutTab") is TabItem, "S2：房主设置页有「身份配置」标签（LayoutTab）");
            Check.True(page.FindName("MaxPlayersInput") is TextBox, "S2：房主设置页有最大人数输入框");
            Check.True(page.FindName("PasswordInput") is PasswordBox, "S2：房主设置页有房间密码框");
            Check.True(page.FindName("ClearPasswordBox") is CheckBox, "S2：房主设置页有「清除密码」勾选");
            Check.True(page.FindName("AutoLayoutBox") is CheckBox, "S2：房主设置页有「跟随人数自动配置」勾选");
            Check.True(page.FindName("LayoutList") is ItemsControl, "S2：房主设置页有身份数量列表（LayoutList）");
            Check.True(page.FindName("InviteCodeBox") is TextBox, "S2：房主设置页有邀请码框");
            Check.True(page.FindName("GameEmojiText") is TextBlock, "S2：房主设置页有游戏图标（GameEmojiText）");
            Check.True(page.FindName("GameBadge") is Border, "S2：房主设置页有游戏徽章（GameBadge）");
            Check.Equal(PageId.HostSettings, page.Id, "S2：房主设置页的页面标识正确");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 房主设置页结构异常：{ex.Message}");
            Check.True(false, "S2：房主设置页能实例化并取到结构契约里的控件");
        }

        // ——— 设置页（I4：4 个标签页且顺序不可变）———
        try
        {
            var page = new SettingsPage(session);
            var tabs = page.FindName("Tabs") as TabControl;

            Check.True(tabs != null, "S2：设置页仍有 Tabs（TabControl）");
            Check.Equal(4, tabs?.Items.Count ?? -1, "S2：设置页仍是四个标签页");
            Check.Equal("外观|主界面|上次使用|账号设置", HeaderText(tabs),
                "S2：设置页标签顺序固定为 外观/主界面/上次使用/账号设置（I4）");

            Check.True(page.FindName("ThemeSystem") is RadioButton, "S2：设置页有「跟随系统」主题项");
            Check.True(page.FindName("ThemeLight") is RadioButton, "S2：设置页有「浅色」主题项");
            Check.True(page.FindName("ThemeDark") is RadioButton, "S2：设置页有「深色」主题项");
            Check.True(page.FindName("ShowPlayers") is CheckBox, "S2：设置页有玩家列表显隐");
            Check.True(page.FindName("ShowChat") is CheckBox, "S2：设置页有房间消息显隐");
            Check.True(page.FindName("ShowActions") is CheckBox, "S2：设置页有操作面板显隐");
            Check.True(page.FindName("ServerText") is TextBlock, "S2：设置页有「上次使用的服务器地址」");
            Check.True(page.FindName("ChangePasswordButton") is Button, "S2：设置页有「修改密码…」入口");
            Check.True(page.FindName("ServerSettingsButton") is Button, "S2：设置页有「服务器设置…」入口");
            Check.True(page.FindName("AccountOverlay") is OverlayDialog, "S2：设置页的账号浮层仍在页面层（K11）");

            // 账号入口显隐仍是纯函数（页面版），与旧窗口那份判定逐条一致。
            Check.True(SettingsPage.AccountEntryVisibility(true, false).ChangePasswordEnabled,
                "S2：设置页纯函数——已登录可改密码");
            Check.False(SettingsPage.AccountEntryVisibility(false, false).ChangePasswordEnabled,
                "S2：设置页纯函数——未登录不可改密码");
            Check.True(SettingsPage.AccountEntryVisibility(true, true).ServerSettingsVisible,
                "S2：设置页纯函数——管理员可见服务器设置");
            Check.False(SettingsPage.AccountEntryVisibility(true, false).ServerSettingsVisible,
                "S2：设置页纯函数——非管理员看不到服务器设置");
            Check.Equal(PageId.Settings, page.Id, "S2：设置页的页面标识正确");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 设置页结构异常：{ex.Message}");
            Check.True(false, "S2：设置页能实例化并取到结构契约里的控件");
        }

        // ——— 邀请码页 ———
        try
        {
            const string code = "WW1:S2-INVITE";
            var page = new InviteCodePage(session, code);

            var box = page.FindName("CodeBox") as TextBox;
            Check.True(box != null, "S2：邀请码页有只读文本框（CodeBox）");
            Check.Equal(code, box?.Text ?? "", "S2：邀请码通过 PageRequest.Argument 传进页面");
            Check.True(page.FindName("StatusText") is TextBlock, "S2：邀请码页有就地状态行");
            Check.True(page.FindName("PrimaryAction") is Button, "S2：邀请码页有「复制邀请码」主按钮");
            Check.Equal(PageId.InviteCode, page.Id, "S2：邀请码页的页面标识正确");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 邀请码页结构异常：{ex.Message}");
            Check.True(false, "S2：邀请码页能实例化并取到结构契约里的控件");
        }
    }

    /// <summary>标签页页头文本（拼接成一行便于一条断言比完顺序）。</summary>
    private static string HeaderText(TabControl? tabs)
        => tabs == null
            ? ""
            : string.Join("|", tabs.Items.Cast<TabItem>().Select(t => t.Header?.ToString() ?? ""));

    /// <summary>
    /// S2 的浮层闭环（真页面 + 真栈）：房间页 → 设置浮层 → 出栈。
    /// 与 P12 的区别：这里量的是**真页面**的可观测状态（<c>IsHitTestVisible</c>、
    /// <c>DataContext</c> 是否还在、是否同一个实例），P12 量的是进出次数。
    /// </summary>
    private static void CheckOverlayRoundTrip()
    {
        Console.WriteLine("   —— S2 自检：房间页 → 设置浮层 → 出栈（下层页不退场） ——");

        try
        {
            var session = new AppSession();
            var nav = session.Navigation;

            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.Room));

            var room = nav.Current as RoomPage;
            Check.True(room != null, "S2：进入房间页");
            if (room == null) return;

            Check.True(room.DataContext is GameViewModel, "S2：房间页进场后挂上了视图模型");

            nav.PushOverlay(new PageRequest(PageId.Settings));
            Check.True(nav.Current is SettingsPage, "S2：房间页打开设置 → 栈顶是设置页");
            Check.Equal(4, nav.Depth, "S2：浮层恰好占一层（栈深 3 → 4）");
            Check.True(nav.CurrentIsOverlay, "S2：栈顶被标记为浮层");
            Check.False(room.IsHitTestVisible, "S2：浮层期间下层房间页不可点（N8 的模态感）");
            Check.True(room.DataContext is GameViewModel,
                "S2：浮层期间房间页**没有**退场（视图模型还在 → 订阅与收尾委托都还在，T41）");

            var back = nav.BackAsync().GetAwaiter().GetResult();
            Check.True(back, "S2：浮层可以返回（Esc / 返回 / 关闭同一个出口）");
            Check.Equal(3, nav.Depth, "S2：浮层出栈后栈深回到 3");
            Check.True(ReferenceEquals(nav.Current, room), "S2：回到的是**同一个**房间页实例（没有重建）");
            Check.True(room.IsHitTestVisible, "S2：浮层出栈后下层页恢复可点");
            Check.True(room.DataContext is GameViewModel, "S2：房间页的视图模型仍是同一个（没有再收一次 OnEnter）");

            // 进出 5 轮：订阅者必须回到基线（页面化最典型的事故点）。
            var client = session.Client;
            var before = SubscriberCount(client, nameof(GameClient.StateReceived));
            for (var i = 0; i < 5; i++)
            {
                nav.PushOverlay(new PageRequest(PageId.Settings));
                nav.BackAsync().GetAwaiter().GetResult();
            }
            var after = SubscriberCount(client, nameof(GameClient.StateReceived));
            Console.WriteLine($"   · 设置浮层进出 5 轮：GameClient.StateReceived 订阅者 {before} → {after}");
            Check.Equal(before, after,
                $"S2：设置浮层进出 5 轮后 GameClient.StateReceived 订阅者不增加（{before} → {after}）");

            // × 收起一层（S2 新增）：栈顶是浮层时 DismissOverlay 生效，且不影响下层。
            nav.PushOverlay(new PageRequest(PageId.HostSettings));
            Check.True(nav.DismissOverlay(), "S2：浮层在场时 DismissOverlay 收起这一层");
            Check.True(ReferenceEquals(nav.Current, room), "S2：收起浮层后仍停在同一个房间页");
            Check.True(room.DataContext is GameViewModel, "S2：收起浮层不会拆掉房间页的视图模型");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S2 浮层闭环异常：{ex.Message}");
            Check.True(false, "S2：浮层闭环（房间 → 设置 → 出栈 ×5）不抛异常");
        }
    }

    /// <summary>
    /// S2 的**入口接线守卫**（源码级）：四个旧窗口在页面里一处都不能再被 <c>new</c>，
    /// 四条入口必须都走页面栈。
    ///
    /// ⚠ S4 升级：S2 时这条守卫的口径是「窗口类本身还在（S4 才删），但页面已经不再依赖它们」——
    /// 也就是说它只能证明**页面没在用**，不能证明**窗口真的没了**（旧 <c>App</c> / 旧窗口之间
    /// 还在互相 new 的话照样绿）。S4 删完 10 个窗口类之后，这条守卫翻成**三段式**：
    /// <list type="number">
    ///   <item>页面侧：四条入口仍走页面栈（原样保留，防回退）；</item>
    ///   <item>全仓侧：`ClientGUI` 里 `new XxxWindow` **0 处**（原来只扫 `Views/Pages`）；</item>
    ///   <item>文件侧：`Views` 下只剩 `ShellWindow` 一个 `*Window.xaml`（类可以被搬进别的文件名复活，所以按类型 + 按文件各钉一次）。</item>
    /// </list>
    /// </summary>
    private static void CheckPushWiring()
    {
        Console.WriteLine("   —— S2：入口接线守卫（源码级，S4 起含全仓归零） ——");

        try
        {
            var root = FindRepoRoot();
            var pages = Path.Combine(root, "ClientGUI", "Views", "Pages");

            string ReadPage(string fileName)
            {
                var path = Path.Combine(pages, fileName);
                // ⚠ S4 收尾修正：原来缺文件返回 ""，而下一条断言是 `Check.False("".Contains(...))` —— 恒绿。
                // 「文件被删/改名」本该是红的，返回空串等于把结构性回归洗成绿。所以这里先判存在。
                Check.True(File.Exists(path), $"S2：接线守卫的取样文件存在（{fileName}）");
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }

            var list = ReadPage("RoomListPage.xaml.cs");
            Check.True(list.Contains(".PushForResultAsync<CreateRoomResult>", StringComparison.Ordinal),
                "S2：列表页用「带结果返回」打开创建房间页（浮层式入栈）");
            Check.False(list.Contains("new CreateRoomWindow", StringComparison.Ordinal),
                "S2：列表页不再 new 创建房间窗口（该类已于 S4 删除）");

            var room = ReadPage("RoomPage.xaml.cs");
            Check.True(room.Contains("PushOverlay(new PageRequest(PageId.Settings))", StringComparison.Ordinal),
                "S2：设置入口走浮层式入栈（房间页不退场）");
            Check.True(room.Contains("PushOverlay(new PageRequest(PageId.HostSettings))", StringComparison.Ordinal),
                "S2：房主设置入口走浮层式入栈");
            Check.True(room.Contains("PushOverlay(new PageRequest(PageId.InviteCode, code))", StringComparison.Ordinal),
                "S2：邀请码入口走浮层式入栈并把码作为参数传过去");
            Check.False(room.Contains("new SettingsWindow", StringComparison.Ordinal),
                "S2：房间页不再 new 设置窗口（该类已于 S4 删除）");
            Check.False(room.Contains("new HostSettingsWindow", StringComparison.Ordinal),
                "S2：房间页不再 new 房主设置窗口（该类已于 S4 删除）");
            Check.False(room.Contains("new InviteCodeWindow", StringComparison.Ordinal),
                "S2：房间页不再 new 邀请码窗口（该类已于 S4 删除）");

            var app = File.ReadAllText(Path.Combine(root, "ClientGUI", "App.xaml.cs"));
            Check.True(app.Contains("PushOverlay(new PageRequest(PageId.Settings))", StringComparison.Ordinal),
                "S2：Ctrl+, 也走浮层式入栈（登录页 / 列表页 / 房间页都能开设置）");

            // —— S4 新增：全仓 `new XxxWindow` 归零（原来只扫 Views/Pages，看不见旧 App 与新页之外的调用方）——
            var gui = Path.Combine(root, "ClientGUI");
            var retiredNew = new[]
            {
                "new LoginWindow", "new RoomListWindow", "new CreateRoomWindow", "new HostSettingsWindow",
                "new SettingsWindow", "new InviteCodeWindow", "new KeepRoomWindow", "new AboutWindow",
                "new ChangelogWindow", "new MainWindow"
            };

            var offenders = SourceFiles(gui)
                .SelectMany(f =>
                {
                    var code = StripLineComments(File.ReadAllText(f));
                    return retiredNew
                        .Where(token => code.Contains(token, StringComparison.Ordinal))
                        .Select(token => $"{Path.GetFileName(f)}:{token}");
                })
                .ToList();
            Check.Equal(0, offenders.Count,
                "S4：全仓 0 处 new 旧窗口（残留：" + string.Join(",", offenders) + "）");

            // —— S4 新增：文件侧也要归零。10 个旧窗口的 XAML 与代码后置必须都不在 ——
            var retiredFiles = new[]
            {
                "LoginWindow", "RoomListWindow", "CreateRoomWindow", "HostSettingsWindow", "SettingsWindow",
                "InviteCodeWindow", "KeepRoomWindow", "AboutWindow", "ChangelogWindow"
            }
            .SelectMany(name => new[] { $"{name}.xaml", $"{name}.xaml.cs" })
            .Append("MainWindow.xaml")
            .Append("MainWindow.xaml.cs")
            .ToList();

            var stillThere = retiredFiles
                .Where(rel =>
                    File.Exists(Path.Combine(gui, rel))
                    || File.Exists(Path.Combine(gui, "Views", rel)))
                .ToList();
            Check.Equal(0, stillThere.Count,
                "S4：10 个旧窗口的 20 个文件全部已删除（残留：" + string.Join(",", stillThere) + "）");

            // —— S4 新增：`Views` 层只允许 ShellWindow 这一个 Window（外壳在外层目录）——
            var strayWindows = Directory.EnumerateFiles(Path.Combine(gui, "Views"), "*Window.xaml", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f) ?? "")
                .ToList();
            Check.Equal(0, strayWindows.Count,
                "S4：Views 目录下不再有任何 *Window.xaml（残留：" + string.Join(",", strayWindows) + "）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S2 入口接线守卫异常：{ex.Message}");
            Check.True(false, "S2：入口接线（创建房间 / 设置 / 房主设置 / 邀请码）可检查");
        }
    }

    /// <summary>
    /// 枚举 `ClientGUI` 下的源码文件（<c>*.cs</c> / <c>*.xaml</c>），跳过 `bin` / `obj`
    /// —— 构建产物里含大量同名类型与旧副本，扫进来会误报。
    /// </summary>
    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (var pattern in new[] { "*.cs", "*.xaml" })
        {
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                yield return file;
            }
        }
    }

    /// <summary>剥掉 <c>//</c> 行注释（含 <c>///</c> 文档注释）—— 注释里提到旧名字不算残留。</summary>
    private static string StripLineComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", "");

    /// <summary>剥掉 XAML / C# 的 <c>&lt;!-- --&gt;</c> 注释块。</summary>
    private static string StripXmlComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    // ==================== v2.6.0 S3：三页的结构契约与入口接线 ====================

    /// <summary>
    /// S3 三页的**结构契约**：把旧窗口那套形体原样搬过来比对 ——
    /// 关于页的「信息表三行 / 技术栈徽章 / 🐺 58×58 图标」一条都不能少（上轮有人误删过）。
    /// </summary>
    private static void CheckS3PageStructure()
    {
        Console.WriteLine("   —— v2.6.0 S3：关于 / 更新日志 / 保留房间 的结构契约 ——");

        var session = new AppSession();

        // ——— 关于页 ———
        try
        {
            var page = new AboutPage(session);
            Arrange(page, 470, 560);

            Check.Equal(PageId.About, page.Id, "S3：关于页的页面标识正确");

            var icon = page.FindName("AppIcon") as Border;
            Check.True(icon != null, "S3：关于页有 🐺 图标徽章（AppIcon）");
            Check.True(icon?.ActualWidth == 58 && icon?.ActualHeight == 58,
                $"S3：关于页图标仍是 58×58（实际 {icon?.ActualWidth:F0}×{icon?.ActualHeight:F0}）");

            var table = page.FindName("InfoTable") as Border;
            Check.True(table != null, "S3：关于页有信息表（InfoTable）");

            var grid = table?.Child as Grid;
            Check.Equal(3, grid?.RowDefinitions.Count ?? -1,
                $"S3：关于页信息表恰好三行（解决方案 / 客户端 / 网络加密，实际 {grid?.RowDefinitions.Count ?? -1}）");

            var labels = grid == null
                ? new List<string>()
                : grid.Children.OfType<TextBlock>()
                    .Select(t => (t.Text ?? "").Trim())
                    .Where(t => t.Length > 0)
                    .ToList();
            Check.True(labels.Contains("解决方案") && labels.Contains("客户端") && labels.Contains("网络加密"),
                "S3：关于页信息表三行的标签齐全（" + string.Join("/", labels) + "）");

            var tech = page.FindName("TechStackList") as ItemsControl;
            Check.True(tech != null, "S3：关于页有技术栈徽章区（TechStackList）");
            Check.True(tech?.ItemTemplate != null,
                "S3：技术栈用项模板渲染成徽章（不是一串裸文本）");
            Check.Equal(AppInfo.TechStack.Count, tech?.Items.Count ?? -1,
                $"S3：技术栈条目数与 AppInfo.TechStack 一致（{tech?.Items.Count ?? -1}）");
            Check.True(AppInfo.TechStack.Count >= 4,
                $"S3：技术栈至少 4 项（实际 {AppInfo.TechStack.Count}）");
            Check.True(tech != null && tech.ActualWidth > 0 && tech.ActualHeight > 0,
                "S3：技术栈区在真实布局里占了位置（不是被挤成 0 高）");

            Check.True(page.FindName("ChangelogButton") is Button,
                "S3：关于页仍有「更新日志」按钮（ChangelogButton）");
            var close = page.FindName("CloseAction") as Button;
            Check.True(close != null && (close.Content?.ToString() ?? "") == "关闭",
                "S3：关于页仍有「关闭」按钮（与旧窗口一致）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 关于页结构异常：{ex.Message}");
            Check.True(false, "S3：关于页能实例化并取到结构契约里的控件");
        }

        // ——— 更新日志页 ———
        try
        {
            var page = new ChangelogPage(session);
            Arrange(page, 560, 620);

            Check.Equal(PageId.Changelog, page.Id, "S3：更新日志页的页面标识正确");

            var list = page.FindName("ChangelogList") as ItemsControl;
            Check.True(list != null, "S3：更新日志页有版本列表（ChangelogList）");
            Check.Equal(Changelog.All.Count, list?.Items.Count ?? -1,
                $"S3：版本列表条目数与 Changelog.All 一致（{list?.Items.Count ?? -1}）");
            Check.True(Changelog.All.Count >= 3, $"S3：版本历史至少 3 条（实际 {Changelog.All.Count}）");

            // 倒序：版本号必须严格递减（最新在最上，旧窗口的行为）。
            var pairs = Changelog.All.Select(e => e.Version)
                .Zip(Changelog.All.Skip(1).Select(e => e.Version), (newer, older) => (newer, older));
            var descending = pairs.All(p => System.Version.TryParse(p.newer, out var a) &&
                                            System.Version.TryParse(p.older, out var b) &&
                                            a.CompareTo(b) > 0);
            Check.True(descending,
                "S3：更新日志按版本倒序排列（" + string.Join(" > ", Changelog.All.Select(e => e.Version)) + ")");
            Check.True(Changelog.All[0].IsLatest && Changelog.All.Skip(1).All(e => !e.IsLatest),
                "S3：「最新」标记只挂在第一条上");

            Check.True(FindVisual<ScrollViewer>(page) != null,
                "S3：更新日志在可滚动容器里（长文本不会溢出页面）");

            var close = page.FindName("CloseAction") as Button;
            Check.True(close != null && (close.Content?.ToString() ?? "") == "关闭",
                "S3：更新日志页底部仍有「关闭」按钮");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 更新日志页结构异常：{ex.Message}");
            Check.True(false, "S3：更新日志页能实例化并取到结构契约里的控件");
        }

        // ——— 保留房间页 ———
        try
        {
            var page = new KeepRoomPage(session);
            Arrange(page, 456, 541);

            Check.Equal(PageId.KeepRoom, page.Id, "S3：保留房间页的页面标识正确");

            var keep = page.FindName("PrimaryAction") as Button;
            Check.True(keep != null && (keep.Content?.ToString() ?? "") == "保留房间",
                "S3：保留房间页的主按钮是「保留房间」");
            var close = page.FindName("CloseRoomAction") as Button;
            Check.True(close != null && (close.Content?.ToString() ?? "") == "关闭房间",
                "S3：保留房间页有「关闭房间」按钮");
            Check.True(page.FindName("KeepHintText") is TextBlock,
                "S3：保留房间页说明了「返回 = 保留」（KeepHintText）");

            // 三态判定仍然只有一个入口（I6 / T35 的底线）。
            Check.True(KeepRoomDecision.Resolve(true), "S3：点「保留房间」→ 保留");
            Check.False(KeepRoomDecision.Resolve(false), "S3：点「关闭房间」→ 不保留");
            Check.True(KeepRoomDecision.Resolve(null), "S3：没选（返回 / Esc / ×）→ 保留");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · 保留房间页结构异常：{ex.Message}");
            Check.True(false, "S3：保留房间页能实例化并取到结构契约里的控件");
        }
    }

    /// <summary>
    /// S3 的**入口接线守卫**（源码级）：三个旧窗口（关于 / 更新日志 / 保留房间）在页面里一处都不能再被 <c>new</c>；
    /// 关于 / 更新日志走 <c>Push</c>（换页），保留房间询问走 <c>PushForDecisionAsync</c>（浮层 + 等结果）。
    /// </summary>
    private static void CheckS3PushWiring()
    {
        Console.WriteLine("   —— S3：入口接线守卫（源码级） ——");

        try
        {
            var root = FindRepoRoot();
            var pages = Path.Combine(root, "ClientGUI", "Views", "Pages");
            var roomPath = Path.Combine(pages, "RoomPage.xaml.cs");
            var roomXamlPath = Path.Combine(pages, "RoomPage.xaml");

            Check.True(File.Exists(roomPath), "S3：RoomPage.xaml.cs 存在（接线守卫的取样文件）");
            if (!File.Exists(roomPath)) return;

            var room = File.ReadAllText(roomPath);

            Check.True(room.Contains("Push(new PageRequest(PageId.About))", StringComparison.Ordinal),
                "S3：帮助 → 关于走页面压栈（Push，不是浮层也不是旧窗口）");
            Check.True(room.Contains("Push(new PageRequest(PageId.Changelog))", StringComparison.Ordinal),
                "S3：帮助 → 更新日志走页面压栈");
            Check.Equal(2, CountOccurrences(room, "Push(new PageRequest(PageId.About))"),
                "S3：F1 与菜单「关于」两条入口都接到同一个页面压栈（不多不少两处）");
            Check.True(room.Contains("PushForDecisionAsync(new PageRequest(PageId.KeepRoom))", StringComparison.Ordinal),
                "S3：退房询问走「浮层 + 等结果」（PushForDecisionAsync），不是同步弹窗");
            // S8：结论先解包（keepRoom.Value）再交给视图模型；三态翻译仍然只有一个落点。
            // 「取消」在更早的一步就被 return null 挡掉了，所以它进不了 Resolve —— 见 S3QaGuard（D）。
            Check.True(room.Contains("KeepRoomDecision.Resolve(choice == KeepRoomChoice.Keep)", StringComparison.Ordinal),
                "S3：三态翻译仍然只调 KeepRoomDecision.Resolve（不在页面里重写判定）");
            Check.True(room.Contains("vm.LeaveRoomForPageAsync(keepRoom.Value)", StringComparison.Ordinal),
                "S3：问完之后交给 LeaveRoomForPageAsync(bool) —— 视图模型的同步签名一行未改");
            Check.True(room.Contains("KeepRoomChoice.Cancel", StringComparison.Ordinal),
                "S8：房间页认得出「取消」这个第三结局（不是只有保留 / 关闭）");
            Check.True(room.Contains("PopTo(PageId.RoomList)", StringComparison.Ordinal),
                "S8：退房成功后直接弹到房间列表（结论已定，不再过一遍返回拦截）");

            var keepXamlPath = Path.Combine(pages, "KeepRoomPage.xaml");
            Check.True(File.Exists(keepXamlPath)
                       && File.ReadAllText(keepXamlPath).Contains("x:Name=\"CancelAction\"", StringComparison.Ordinal),
                "S8：保留房间询问页上有「取消」按钮（CancelAction）—— 用户能改主意不退房");

            // 旧窗口在这三页里一处都不能再被创建。
            // ⚠ S4 升级：S3 时这条只能证明「页面没在用它们」（窗口类本身还在）—— 现在窗口类已删，
            //   所以口径从「这三页里不许出现」升级成「**全仓**不许出现」。三者一起上：
            //   全仓源码（剥行注释）/ Views 目录下不许有残留文件（下面 CheckPushWiring 已按 20 个文件名钉死）。
            var offenders = SourceFiles(Path.Combine(root, "ClientGUI"))
                .SelectMany(f =>
                {
                    var code = StripLineComments(File.ReadAllText(f));
                    return new[] { "new AboutWindow", "new ChangelogWindow", "new KeepRoomWindow" }
                        .Where(token => code.Contains(token, StringComparison.Ordinal))
                        .Select(token => $"{Path.GetFileName(f)}:{token}");
                })
                .ToList();
            Check.Equal(0, offenders.Count,
                "S4：全仓 0 处 new 关于 / 更新日志 / 保留房间 三个旧窗口（残留：" +
                string.Join(",", offenders) + "）");

            // 「退出房间」不再绑命令（命令里 await 不出浮层的结论）—— 菜单与导航栏都走同一个 Click。
            // ⚠ S4 升级：S3 只需证明「页面没绑回 DisconnectCommand」（命令本体还在视图模型上，另有别的宿主可能绑）。
            //   T48 把命令本体也删了，所以判据改成**代码里 0 处出现**；XML 注释里解释"为什么不绑"不算残留，
            //   所以先剥注释 —— 否则这条会因为一句说明而恒红，逼着后来人删掉有价值的注释。
            Check.True(File.Exists(roomXamlPath), "S3：RoomPage.xaml 存在");
            if (File.Exists(roomXamlPath))
            {
                var xaml = StripXmlComments(File.ReadAllText(roomXamlPath));
                Check.Equal(0, CountOccurrences(xaml, "DisconnectCommand"),
                    "S4：房间页 XAML 里 0 处 DisconnectCommand（命令本体已删，注释不算）");

                var disconnectCode = SourceFiles(Path.Combine(root, "ClientGUI"))
                    .SelectMany(f =>
                    {
                        var raw = File.ReadAllText(f);
                        var code = f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                            ? StripXmlComments(raw)
                            : StripLineComments(raw);
                        return code.Contains("DisconnectCommand", StringComparison.Ordinal)
                            ? new[] { Path.GetFileName(f) ?? "" }
                            : Array.Empty<string>();
                    })
                    .ToList();
                Check.Equal(0, disconnectCode.Count,
                    "S4：全仓 DisconnectCommand 代码 0 处（T48 销号，残留：" + string.Join(",", disconnectCode) + "）");

                // 两个「退出房间」入口（菜单 + 导航栏）共用同一个 Click，不多不少两处。
                Check.Equal(2, CountOccurrences(xaml, "Click=\"Menu_LeaveRoom_OnClick\""),
                    "S3：菜单与导航栏两处「退出房间」都走同一个 Click 处理器");

                // v2.6.0 S6：房间页补上可见「返回」键（手机式导航）—— 之前只有菜单 / 右侧按钮 / 键盘三条路。
                Check.Equal(1, CountOccurrences(xaml, "Click=\"Back_OnClick\""),
                    "S6：房间页导航栏有且仅有一个「返回」键（放在左槽位最前）");
                Check.True(room.Contains("Back_OnClick", StringComparison.Ordinal),
                    "S6：返回键有处理器");
                Check.Equal(3, CountOccurrences(room, "LeaveRoomFromUiAsync()"),
                    "S6：菜单 / 导航栏退出房间 / 返回 三个入口共用同一个 LeaveRoomFromUiAsync（一条路径）");
            }

            // 页面工厂：十个 PageId 全部有页面（少了任何一个都会在切页时抛 NotSupportedException）。
            var navSource = File.ReadAllText(Path.Combine(root, "ClientGUI", "Services", "NavigationService.cs"));
            var missing = Enum.GetValues<PageId>()
                .Where(id => !navSource.Contains($"PageId.{id} => new", StringComparison.Ordinal))
                .ToList();
            Check.Equal(0, missing.Count,
                "S3：PageFactory 覆盖全部十个 PageId（缺：" + string.Join(",", missing) + "）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · S3 入口接线守卫异常：{ex.Message}");
            Check.True(false, "S3：入口接线（关于 / 更新日志 / 保留房间）可检查");
        }
    }

    // ==================== v2.6.0 S3 · T43：保留房间端到端 ====================

    /// <summary>
    /// **T43：真端到端**（真内置服务器 + 真页面栈 + 真 <c>leaveroom</c>）。
    ///
    /// 两条路径都必须走完「进房 → 房主独自退房 → 浮层出现 → 出栈回房间列表页 → 服务器仍在跑」：
    /// <list type="number">
    ///   <item><b>①</b> 在浮层上按「返回」/ Esc（= 没显式选）→ <c>null</c> → 保留（房间还在服务器上）；</item>
    ///   <item><b>②</b> 点「关闭房间」→ <c>false</c> → 房间从服务器上移除。</item>
    /// </list>
    /// 之所以必须"真"：这条链路的每一环（<c>NeedsKeepRoomDecision</c> 的人数判断、服务器的 keepRoom 处理、
    /// 出栈后的 <c>IsJoined</c> 归零）都是假页面测不出来的。
    ///
    /// 顺带把**跨特性要求**一并钉死：房间页 → 关于 → 更新日志 → 返回 → 返回 之后，
    /// 玩家列表 / 房间号 / 标题与离开前逐一相同，且 <c>GameClient.StateReceived</c> 与
    /// <c>EmbeddedServerService.Changed</c> 的订阅者数**一个都不许涨**。
    ///
    /// 跑在 <c>--ui</c> 的 STA 线程最后（<see cref="UiSmokeCheck"/> 调用），自带清理：
    /// 结束时停掉自己启动的服务器、断开自己建立的连接，不污染其他断言。
    /// </summary>
    public static void RunKeepRoomEndToEndChecks()
    {
        Console.WriteLine("   —— v2.6.0 S3 · T43：保留房间端到端（真服务器 + 真页面栈） ——");

        var service = EmbeddedServerService.Current;
        var startedByUs = false;
        AppSession? session = null;

        // ⚠ 先复现真实宿主的**线程模型**，否则这条路根本走不通：
        // 真实客户端整条消息循环都跑在 DispatcherFrame 里（Application.Run → PushFrame），
        // 而 PushFrame 会把 `SynchronizationContext.Current` 换成 DispatcherSynchronizationContext ——
        // 于是事件处理器里的 `await` 续体回到 UI 线程，`Players.Clear()` 之类才安全。
        // 测试宿主没有 Application.Run()，本方法体又是在多次 PushFrame **之间**执行的（此时上下文为 null），
        // 续体因此会落到线程池 → ObservableCollection 在非 UI 线程被改 → 抛 NotSupportedException。
        // 这不是产品缺陷（真实宿主不会发生），所以在这里显式补上同一个上下文，跑完再还原。
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            RunKeepRoomEndToEndCore(service, ref startedByUs, ref session);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>T43 的主体（拆出来只是为了能用 try/finally 管住线程上下文的还原）。</summary>
    private static void RunKeepRoomEndToEndCore(
        EmbeddedServerService service, ref bool startedByUs, ref AppSession? session)
    {
        try
        {
            // ---------- 0. 真服务器：没跑就起一个（用了就负责停） ----------
            if (!service.IsRunning)
            {
                var start = service.StartAsync();
                PumpUntil(start, TimeSpan.FromSeconds(30));
                startedByUs = service.IsRunning;
            }

            Check.True(service.IsRunning, "T43：内置服务器已启动（端到端前提）");
            if (!service.IsRunning)
            {
                Console.WriteLine("   · T43：内置服务器无法启动，端到端跳过（" + service.StatusText + "）");
                return;
            }

            var url = service.Info.ConnectUrl;

            session = new AppSession();
            var client = session.Client;
            var nav = session.Navigation;

            var connect = client.ConnectAsync(url);
            PumpUntil(connect, TimeSpan.FromSeconds(20));
            Check.True(client.IsConnected, $"T43：已连上内置服务器（{url}）");

            var user = "t43-" + Guid.NewGuid().ToString("N")[..6];
            var login = client.LoginAsync(user, "pw-" + user);
            PumpUntil(login, TimeSpan.FromSeconds(20));
            Check.True(login.IsCompleted && login.Result.ok,
                "T43：登录内置服务器成功（首次即注册）");

            var created = client.CreateRoomAsync("T43 保留房间端到端", null, null);
            PumpUntil(created, TimeSpan.FromSeconds(20));
            var roomId = created.IsCompleted && created.Result.Ok ? created.Result.RoomId : "";
            Check.True(roomId.Length > 0, $"T43：建房成功（房间号 {roomId}）");
            if (roomId.Length == 0) return;
            Check.True(client.IsJoined, "T43：建房后即已在房间内");

            // ---------- 1. 登录页 → 列表页 → 房间页（真页面栈） ----------
            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.Room));

            var room = nav.Current as RoomPage;
            Check.True(room != null, "T43：进入房间页");
            if (room == null) return;

            PumpUntil(() => (room.DataContext as GameViewModel)?.NeedsKeepRoomDecision == true,
                TimeSpan.FromSeconds(10));
            var vm = room.DataContext as GameViewModel;
            Check.True(vm?.NeedsKeepRoomDecision == true,
                "T43：房主独自在房 → NeedsKeepRoomDecision == true（询问的前提）");
            if (vm == null) return;

            Check.Equal(1, vm.Players.Count, $"T43：屋里只有自己一人（玩家数 {vm.Players.Count}）");

            // ---------- 2. 跨特性：房间 → 关于 → 更新日志 → 返回 → 返回 ----------
            var subBefore = SubscriberCount(client, nameof(GameClient.StateReceived));
            var changedBefore = SubscriberCount(service, nameof(EmbeddedServerService.Changed));
            var roomIdBefore = vm.RoomId;
            var headerBefore = vm.RoomHeaderText;
            var playersBefore = string.Join(",", vm.Players.Select(p => p.Name));

            nav.Push(new PageRequest(PageId.About));
            Check.True(nav.Current is AboutPage, "S3：帮助 → 关于是页面压栈（栈顶 = 关于页）");
            Check.Equal(4, nav.Depth, "S3：关于页占第 4 层");
            Pump();
            Check.True(room.DataContext == null,
                "S3：压栈期间房间页已退场（视图模型被释放，OnLeave 成对调用）");
            var subWhileAway = SubscriberCount(client, nameof(GameClient.StateReceived));
            Console.WriteLine($"   · 跨特性：关于页在场时 StateReceived 订阅者 {subBefore} → {subWhileAway}");
            Check.True(subWhileAway < subBefore,
                $"S3：房间页退场后 GameClient.StateReceived 订阅者减少（{subBefore} → {subWhileAway}，没有悬空订阅）");

            nav.Push(new PageRequest(PageId.Changelog));
            Check.True(nav.Current is ChangelogPage, "S3：关于页的「更新日志」继续压栈（栈顶 = 更新日志页）");
            Check.Equal(5, nav.Depth, "S3：房间 → 关于 → 更新日志 = 5 层栈");

            var backToAbout = nav.BackAsync();
            PumpUntil(backToAbout, TimeSpan.FromSeconds(5));
            Check.True(nav.Current is AboutPage, "S3：更新日志 → 返回 回到关于页");

            var backToRoom = nav.BackAsync();
            PumpUntil(backToRoom, TimeSpan.FromSeconds(5));
            Check.True(nav.Current is RoomPage, "S3：关于 → 返回 回到房间页");
            Check.Equal(3, nav.Depth, "S3：两级返回之后栈深回到 3");

            var room2 = nav.Current as RoomPage;
            Check.True(ReferenceEquals(room, room2), "S3：回到的是同一个房间页实例");
            var vm2 = room2?.DataContext as GameViewModel;
            Check.True(vm2 != null, "S3：房间页重新进场后重新挂上了视图模型");
            Check.True(!ReferenceEquals(vm, vm2), "S3：视图模型是新的（旧的已随 OnLeave 销毁）");
            Check.True(vm.IsDetached, "S3：退场的那份视图模型确实退订了（IsDetached == true）");
            if (vm2 == null) return;

            Console.WriteLine($"   · 跨特性：玩家 {playersBefore} → {string.Join(",", vm2.Players.Select(p => p.Name))}");
            Console.WriteLine($"   · 跨特性：StateReceived 订阅者 {subBefore} → {SubscriberCount(client, nameof(GameClient.StateReceived))}");

            Check.Equal(1, vm2.Players.Count, "S3：回来后玩家列表仍是 1 人（不是空的陈旧数据）");
            Check.Equal(playersBefore, string.Join(",", vm2.Players.Select(p => p.Name)),
                "S3：回来后玩家名字逐一相同（从 LastState 回填 / 重新拉 roomstate）");
            Check.Equal(roomIdBefore, vm2.RoomId, "S3：回来后房间号不变");
            Check.Equal(headerBefore, vm2.RoomHeaderText, "S3：回来后房间标题条不变");
            Check.Equal(subBefore, SubscriberCount(client, nameof(GameClient.StateReceived)),
                $"S3：进出关于 / 更新日志一轮后 GameClient.StateReceived 订阅者不增加（{subBefore}）");
            Check.Equal(changedBefore, SubscriberCount(service, nameof(EmbeddedServerService.Changed)),
                $"S3：同一轮后 EmbeddedServerService.Changed 订阅者不增加（{changedBefore}）");
            Check.True(vm2.NeedsKeepRoomDecision, "S3：回来后仍然是「房主独自在房」（状态没丢）");

            // ---------- 3. T43 路径①：返回 = 保留 ----------
            // 外壳的「返回」就是 NavigationService.BackAsync（N9 单一出口）：
            // 它会先 await RoomPage.OnBackRequestedAsync（内部把浮层压进去并等结论），拿到 true 之后才出栈。
            var shellBack = nav.BackAsync();
            PumpUntil(() => nav.Current is KeepRoomPage, TimeSpan.FromSeconds(5));
            Check.True(nav.Current is KeepRoomPage, "T43-①：退房时弹出「是否保留房间」浮层（KeepRoomPage）");
            Check.True(nav.CurrentIsOverlay, "T43-①：询问是浮层式入栈（下层房间页不退场）");
            Check.True(room2!.DataContext is GameViewModel,
                "T43-①：浮层期间房间页仍在场（视图模型没被拆掉 —— T41 的核心）");

            // 用户在浮层上按「返回」/ Esc：外壳再调一次 BackAsync（与真实按键同一条路径）。
            var escBack = nav.BackAsync();
            PumpUntil(escBack, TimeSpan.FromSeconds(5));
            PumpUntil(shellBack, TimeSpan.FromSeconds(20));
            Check.True(shellBack.IsCompleted && shellBack.Result,
                "T43-①：返回 = 保留 → 退房成功（允许房间页出栈）");
            Check.True(nav.Current is RoomListPage, "T43-①：出栈回房间列表页");
            Check.Equal(2, nav.Depth, "T43-①：栈深回到 2（只退了房间这一层）");
            Check.True(service.IsRunning, "T43-①：保留房间不动内置服务器（IsRunning == true）");

            var roomsAfterKeep = client.ListRoomsAsync();
            PumpUntil(roomsAfterKeep, TimeSpan.FromSeconds(10));
            Check.True(roomsAfterKeep.IsCompleted && roomsAfterKeep.Result.Rooms.Any(r => r.Id == roomId),
                "T43-①：房间仍在服务器上（「保留」的硬证据）");

            // ---------- 4. T43 路径②：关闭房间 = false ----------
            var rejoin = client.JoinRoomAsync(roomId, null);
            PumpUntil(rejoin, TimeSpan.FromSeconds(20));
            Check.True(rejoin.IsCompleted && rejoin.Result.Ok, "T43-②：重新进入同一房间（① 确实保留了它）");

            var refresh = client.RefreshStateAsync();
            PumpUntil(refresh, TimeSpan.FromSeconds(10));

            nav.Push(new PageRequest(PageId.Room));
            var room3 = nav.Current as RoomPage;
            Check.True(room3 != null, "T43-②：再次进入房间页");
            PumpUntil(() => (room3?.DataContext as GameViewModel)?.NeedsKeepRoomDecision == true,
                TimeSpan.FromSeconds(10));
            var vm3 = room3?.DataContext as GameViewModel;
            Check.True(vm3?.NeedsKeepRoomDecision == true, "T43-②：重新进房后仍是「房主独自在房」");
            if (room3 == null) return;

            var shellBack2 = nav.BackAsync();
            PumpUntil(() => nav.Current is KeepRoomPage, TimeSpan.FromSeconds(5));
            Check.True(nav.Current is KeepRoomPage, "T43-②：再次弹出保留房间浮层");

            var keepPage = nav.Current as KeepRoomPage;
            var closeButton = keepPage?.FindName("CloseRoomAction") as Button;
            Check.True(closeButton != null, "T43-②：浮层上有「关闭房间」按钮（CloseRoomAction）");
            closeButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            PumpUntil(shellBack2, TimeSpan.FromSeconds(20));
            Check.True(shellBack2.IsCompleted && shellBack2.Result,
                "T43-②：关闭房间 → 退房成功（允许房间页出栈）");
            Check.True(nav.Current is RoomListPage, "T43-②：出栈回房间列表页");
            Check.Equal(2, nav.Depth, "T43-②：栈深回到 2");
            Check.True(service.IsRunning, "T43-②：关闭房间也不停内置服务器（IsRunning == true）");

            var roomsAfterClose = client.ListRoomsAsync();
            PumpUntil(roomsAfterClose, TimeSpan.FromSeconds(10));
            Check.True(roomsAfterClose.IsCompleted
                       && !roomsAfterClose.Result.Rooms.Any(r => r.Id == roomId),
                "T43-②：房间已从服务器移除（「关闭」的硬证据）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · T43 端到端异常：{ex}");
            Check.True(false, "T43：保留房间端到端（真服务器）不抛异常");
        }
        finally
        {
            if (session != null)
            {
                try
                {
                    var dispose = session.Client.DisposeAsync().AsTask();
                    PumpUntil(dispose, TimeSpan.FromSeconds(10));
                }
                catch { /* 清理失败不影响结论 */ }
            }

            if (startedByUs)
            {
                try
                {
                    var stop = service.StopAsync();
                    PumpUntil(stop, TimeSpan.FromSeconds(15));
                }
                catch { /* 同上 */ }
            }
        }
    }

    // ==================== 工具：把 Dispatcher 队列跑空 ====================

    /// <summary>把当前 Dispatcher 队列里已有的工作跑完（相当于一次 DoEvents）。</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 边泵 Dispatcher 边等一个条件成立（async 续体是投递到 Dispatcher 的，光 <c>Wait()</c> 会死锁）。
    /// 超时后不再等，由调用方的断言给出结论。
    /// </summary>
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        Pump();
    }

    /// <summary>同上，等一个 <see cref="Task"/> 完成。</summary>
    private static void PumpUntil(Task task, TimeSpan timeout)
        => PumpUntil(() => task.IsCompleted, timeout);

    /// <summary>在视觉树里找第一个指定类型的元素（用于「页面里确实有个 ScrollViewer」这类断言）。</summary>
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisual<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }

        return null;
    }

    // ==================== 反射：数订阅者 ====================
    /// <summary>
    /// 数一个事件当前挂了几个处理器。
    /// <c>event</c> 是字段式声明时编译成同名私有委托字段，反射即可读到；
    /// C# 里无法直接读取订阅者数量，这是不污染产品代码的唯一办法。
    /// </summary>
    private static int SubscriberCount(object target, string eventName)
    {
        var field = target.GetType().GetField(eventName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return (field?.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>同上，取静态事件（<see cref="EmbeddedServerService.Changed"/> 是全局的，最容易泄漏）。</summary>
    private static int StaticSubscriberCount(Type type, string eventName)
    {
        var field = type.GetField(eventName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    // ==================== 假页面（只记录调用，不碰界面） ====================

    private sealed class FakePage : IPage
    {
        public FakePage(PageId id, string title, bool canGoBack = true)
        {
            Id = id;
            Title = title;
            CanGoBack = canGoBack;
        }

        public PageId Id { get; }
        public string Title { get; }
        public bool CanGoBack { get; }

        /// <summary>进入 / 退场次数（用来断言「成对调用」）。</summary>
        public int Enters { get; private set; }
        public int Leaves { get; private set; }

        /// <summary>返回拦截结果（false = 取消返回）。</summary>
        public bool AllowBack { get; set; } = true;

        public void OnEnter() => Enters++;

        public void OnLeave() => Leaves++;

        public Task<bool> OnBackRequestedAsync() => Task.FromResult(AllowBack);
    }

    private sealed class FakeFactory : IPageFactory
    {
        private readonly List<FakePage> _created = new();

        public IPage Create(PageRequest request)
        {
            var page = new FakePage(request.Id, request.Id.ToString());
            _created.Add(page);
            return page;
        }

        /// <summary>最后创建的页面。</summary>
        public FakePage Last => _created[^1];

        /// <summary>按创建顺序的全部页面。</summary>
        public IReadOnlyList<FakePage> Created => _created;
    }
}
