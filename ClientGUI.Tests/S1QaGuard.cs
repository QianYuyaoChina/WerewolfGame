using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views;
using ClientGUI.Views.Pages;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// v2.6.0 **S1 的 QA 独立验收断言**（严过关 · 背靠背编写，不复用 <see cref="PageCatalog"/> 的任何判定）。
///
/// 为什么要另写一份：架构 §8 承诺「迁移不是删减」，工程师的 <c>PageCatalog</c> 是**自测**；
/// QA 必须用**独立实现**重证同一批结论，否则「断言只增不减」只是自我声明。
/// 本文件与 <c>PageCatalog</c> 不共享任何辅助方法（假页面、订阅计数、取根逻辑全部自己实现）。
///
/// 三批挂载点：
/// <list type="bullet">
///   <item><see cref="RunLogicChecks"/> —— 默认组：关闭意图映射、导航栈、返回键位、退订、Q1=B 代码级；</item>
///   <item><see cref="RunUiChecks"/> —— <c>--ui</c>：旧 10 窗口仍可实例化、外壳唯一宿主、三页骨架实测、菜单栏仍在；</item>
///   <item><see cref="RunLiveQ1BChecksAsync"/> —— <c>--embedded</c>：**真起服**验证「返回登录页不停服务器」（Q1=B ②③）。</item>
/// </list>
/// </summary>
public static class S1QaGuard
{
    // ==================================================================
    //  默认组：纯逻辑
    // ==================================================================

    /// <summary>跑所有不需要 WPF 上下文的 S1 验收断言。</summary>
    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.6.0 S1：QA 独立验收（纯逻辑） ════════");

        CheckEnumLocks();
        CheckResolveTable();
        CheckNavigationStack();
        CheckNavKeyMap();
        CheckRoomBackDecision();
        CheckSourceWiring();
    }

    /// <summary>I7：<c>CloseIntentSource</c> 仍是 4 值、仍只有 RoomWindow 不需完整收尾；新枚举恰好 3 值。</summary>
    private static void CheckEnumLocks()
    {
        var legacy = Enum.GetValues<CloseIntentSource>();
        Check.Equal(4, legacy.Length, $"S1-QA：CloseIntentSource 仍是四值（实际 {legacy.Length}）");

        var noShutdown = legacy.Where(s => !CloseIntent.RequiresFullShutdown(s)).ToList();
        Check.Equal(1, noShutdown.Count, "S1-QA：只有一种关闭来源不需要完整收尾");
        Check.Equal(CloseIntentSource.RoomWindow, noShutdown.FirstOrDefault(),
            "S1-QA：不需要完整收尾的必须是 RoomWindow（关房间页=只退房，I1）");

        var shell = Enum.GetValues<ShellCloseIntent>();
        Check.Equal(3, shell.Length, $"S1-QA：ShellCloseIntent 恰好三值（实际 {shell.Length}）");
        Check.SequenceEqual(
            new[] { "MenuExit", "SystemSession", "WindowClose" },
            shell.Select(v => v.ToString()).OrderBy(v => v, StringComparer.Ordinal),
            "S1-QA：ShellCloseIntent 的三个值名与架构 §5.4 一致（未偷偷扩容）");
    }

    /// <summary>
    /// 架构 §5.4 / §5.2 的映射表——QA 按**规范文本**独立写一份期望表，逐格比对。
    /// 覆盖 3 种 intent × 4 种当前页 × 2 种收尾态 = 24 格。
    /// </summary>
    private static void CheckResolveTable()
    {
        PageId?[] pages = { PageId.Login, PageId.RoomList, PageId.Room, null };
        var cells = 0;
        var wrong = new List<string>();

        foreach (var intent in Enum.GetValues<ShellCloseIntent>())
        foreach (var page in pages)
        foreach (var shutting in new[] { false, true })
        {
            var expected = ExpectedResolve(intent, page, shutting);
            var actual = CloseIntent.Resolve(intent, page, shutting);
            cells++;
            if (expected != actual) wrong.Add($"{intent}/{page?.ToString() ?? "null"}/{shutting}=>{actual}(期望{expected})");
        }

        Check.Equal(24, cells, "S1-QA：关闭意图映射覆盖 24 格（3 intent × 4 页 × 2 收尾态）");
        Check.Equal(0, wrong.Count, "S1-QA：关闭意图映射逐格正确（错：" + string.Join("；", wrong) + "）");

        // 单独钉死最关键的三条（失败信息一眼能看出是哪条断的）。
        Check.Equal(CloseIntentSource.RoomWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Room, false),
            "S1-QA：房间页点 × → 只退房间（RoomWindow，I1）");
        Check.Equal(CloseIntentSource.RoomListWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Login, false),
            "S1-QA：登录页点 × → 完整收尾（复用 RoomListWindow 值，I2）");
        Check.Equal(CloseIntentSource.ExitProgram,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, PageId.Room, true),
            "S1-QA：收尾中再点 × → 幂等放行（不重复收尾）");
        Check.Equal(CloseIntentSource.RoomListWindow,
            CloseIntent.Resolve(ShellCloseIntent.WindowClose, null, false),
            "S1-QA：当前页未知（null）时按「非房间」处理 → 完整收尾（保守方向）");
    }

    /// <summary>QA 自己按架构 §5.4 的 if 链写的期望映射（与产品实现的写法故意不同，避免同错同过）。</summary>
    private static CloseIntentSource ExpectedResolve(ShellCloseIntent intent, PageId? page, bool shutting)
    {
        if (shutting) return CloseIntentSource.ExitProgram;
        switch (intent)
        {
            case ShellCloseIntent.MenuExit: return CloseIntentSource.ExitProgram;
            case ShellCloseIntent.SystemSession: return CloseIntentSource.SystemSession;
            default:
                return page == PageId.Room ? CloseIntentSource.RoomWindow : CloseIntentSource.RoomListWindow;
        }
    }

    /// <summary>N1 / N2 / N5 / N7：导航栈的深度、成对回调、防重复入栈、栈底不出栈。</summary>
    private static void CheckNavigationStack()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        Check.Equal(1, nav.Depth, "S1-QA：Reset 后栈深为 1（N1）");
        Check.False(nav.CanGoBack, "S1-QA：根页 CanGoBack=false（不画返回键）");
        Check.Equal(1, factory.Last.EnterCount, "S1-QA：Reset 给根页调了一次 OnEnter");

        var root = factory.Last;
        nav.Push(new PageRequest(PageId.RoomList));
        Check.Equal(2, nav.Depth, "S1-QA：Push 后栈深 +1");
        Check.Equal(1, root.LeaveCount, "S1-QA：Push 时旧栈顶成对收到 OnLeave（N6）");
        Check.Equal(1, factory.Last.EnterCount, "S1-QA：Push 时新页收到 OnEnter");
        Check.True(nav.CanGoBack, "S1-QA：栈深 2 且栈顶可返回 → CanGoBack=true（Q1=B 允许列表页返回）");

        // N5 防重复入栈：同 Id 再压一次，栈深不增长。
        nav.Push(new PageRequest(PageId.RoomList));
        Check.Equal(2, nav.Depth, "S1-QA：同 PageId 重复 Push 不增加栈深（N5）");
        nav.Push(new PageRequest(PageId.RoomList));
        Check.Equal(2, nav.Depth, "S1-QA：连续三次同 PageId Push 之后栈深仍为 2（N5）");

        var back = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back, "S1-QA：BackAsync 在有上层页时返回 true");
        Check.Equal(1, nav.Depth, "S1-QA：BackAsync 之后栈深 -1");

        var bottom = nav.BackAsync().GetAwaiter().GetResult();
        Check.False(bottom, "S1-QA：栈底 BackAsync 返回 false（N7）");
        Check.Equal(1, nav.Depth, "S1-QA：栈底 BackAsync 之后栈深不变（N7）");
    }

    /// <summary>N9：Alt+← / Esc 走同一个返回入口；栈底一律无动作。</summary>
    private static void CheckNavKeyMap()
    {
        Check.Equal(NavAction.Back, NavKeyMap.Resolve(Key.Left, ModifierKeys.Alt, true),
            "S1-QA：Alt+← 可返回时 → Back");
        Check.Equal(NavAction.Back, NavKeyMap.Resolve(Key.Escape, ModifierKeys.None, true),
            "S1-QA：Esc 可返回时 → Back");
        Check.Equal(NavAction.None, NavKeyMap.Resolve(Key.Escape, ModifierKeys.None, false),
            "S1-QA：栈底按 Esc → 无动作（登录页不会莫名其妙退出）");
        Check.Equal(NavAction.None, NavKeyMap.Resolve(Key.Left, ModifierKeys.Alt, false),
            "S1-QA：栈底按 Alt+← → 无动作");
        Check.Equal(NavAction.None, NavKeyMap.Resolve(Key.Right, ModifierKeys.Alt, true),
            "S1-QA：Alt+→ 不是返回键位");
        Check.Equal(NavAction.None, NavKeyMap.Resolve(Key.Escape, ModifierKeys.Alt, true),
            "S1-QA：Alt+Esc 不算返回（避免与系统快捷键撞车）");
    }

    /// <summary>N3：房间页返回拦截的三情形（未进房 / 退房成功 / 退房失败）。</summary>
    private static void CheckRoomBackDecision()
    {
        var leaveCalled = 0;

        var notJoined = RoomBackDecision.ResolveAsync(false, () =>
        {
            leaveCalled++;
            return Task.FromResult(true);
        }).GetAwaiter().GetResult();
        Check.True(notJoined, "S1-QA：未进房 → 允许出栈");
        Check.Equal(0, leaveCalled, "S1-QA：未进房 → 一次 leaveroom 都不发");

        var ok = RoomBackDecision.ResolveAsync(true, () => Task.FromResult(true)).GetAwaiter().GetResult();
        Check.True(ok, "S1-QA：已进房且退房成功 → 允许出栈");

        var fail = RoomBackDecision.ResolveAsync(true, () => Task.FromResult(false)).GetAwaiter().GetResult();
        Check.False(fail, "S1-QA：退房失败 → 留在房间里（不放行，避免界面与服务端错位）");
    }

    /// <summary>
    /// N6 / D5.2：**用真实导航来回 5 轮**（不是直接 new VM 再 Detach），
    /// 用反射数事件订阅者，确认不随进出次数累积 —— 这是页面化最容易出的泄漏。
    /// </summary>
    private static void CheckSubscriptionDoesNotAccumulate()
    {
        var session = new AppSession();
        var nav = session.Navigation;
        var client = session.Client;

        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        var stateBefore = FieldSubscribers(client, "StateReceived");
        var changedBefore = StaticSubscribers(typeof(EmbeddedServerService), "Changed");
        Console.WriteLine($"   · 往返前订阅者：GameClient.StateReceived={stateBefore}，EmbeddedServerService.Changed={changedBefore}");

        var failAt = -1;
        try
        {
            for (var round = 0; round < 5; round++)
            {
                nav.Push(new PageRequest(PageId.Room));       // 列表 → 房间（GameViewModel 订阅 5 个事件）
                nav.BackAsync().GetAwaiter().GetResult();     // 房间 → 列表（必须全部退订）
            }
        }
        catch (Exception ex)
        {
            failAt = 0;
            Console.WriteLine("   · 往返 5 轮异常：" + ex.Message);
        }

        var stateAfter = FieldSubscribers(client, "StateReceived");
        var changedAfter = StaticSubscribers(typeof(EmbeddedServerService), "Changed");
        Console.WriteLine($"   · 往返 5 轮后订阅者：GameClient.StateReceived={stateAfter}，EmbeddedServerService.Changed={changedAfter}");

        Check.Equal(-1, failAt, "S1-QA：列表 ↔ 房间 真实往返 5 轮不抛异常");
        Check.Equal(stateBefore, stateAfter,
            $"S1-QA：往返 5 轮后 GameClient.StateReceived 订阅者不增长（{stateBefore} → {stateAfter}）");
        Check.Equal(changedBefore, changedAfter,
            $"S1-QA：往返 5 轮后静态 EmbeddedServerService.Changed 订阅者不增长（{changedBefore} → {changedAfter}）");

        // Detach 幂等 + 房间页退场后其视图模型确实已退订。
        var vm = new GameViewModel(client);
        Check.False(vm.IsDetached, "S1-QA：新建 GameViewModel 默认未退订");
        vm.Detach();
        Check.True(vm.IsDetached, "S1-QA：GameViewModel.Detach() 之后 IsDetached=true");
        var afterFirst = FieldSubscribers(client, "StateReceived");
        vm.Detach();
        Check.Equal(afterFirst, FieldSubscribers(client, "StateReceived"),
            "S1-QA：Detach() 幂等（第二次调用不再改变订阅数）");

        var list = new RoomListViewModel(client);
        list.StartPolling();
        Check.True(list.IsPolling, "S1-QA：RoomListViewModel.StartPolling() 之后 IsPolling=true");
        list.Detach();
        Check.False(list.IsPolling, "S1-QA：RoomListViewModel.Detach() 之后 IsPolling=false");
    }

    /// <summary>Q1=B 的**代码级**判据：列表页返回是纯导航，连接实例不变、不触发收尾。</summary>
    private static void CheckQ1BCodeLevel()
    {
        var session = new AppSession();
        var client = session.Client;
        var nav = session.Navigation;

        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        var listPage = nav.Current as RoomListPage;
        Check.True(listPage != null, "S1-QA：房间列表当前页确实是 RoomListPage");
        Check.True(listPage?.CanGoBack == true, "S1-QA：RoomListPage.CanGoBack=true（Q1=B）");
        Check.True(listPage?.OnBackRequestedAsync().GetAwaiter().GetResult() == true,
            "S1-QA：RoomListPage.OnBackRequestedAsync() 直接放行（纯导航，不拦返回）");

        var back = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back, "S1-QA：列表页返回执行成功");
        Check.Equal(PageId.Login, nav.Current?.Id, "S1-QA：返回后当前页是登录页");
        Check.Equal(1, nav.Depth, "S1-QA：返回后栈深为 1（Q1=B）");
        Check.False(nav.CanGoBack, "S1-QA：回到登录页后 CanGoBack=false（Q1=B）");
        Check.True(ReferenceEquals(client, session.Client),
            "S1-QA：返回登录页后 GameClient 仍是同一个实例（连接没被重建/拆掉）");
        Check.False(AppShutdown.IsShuttingDown,
            "S1-QA：从列表页返回登录页不会触发完整收尾（不停服务器）");
    }

    /// <summary>源码级接线守卫：启动入口、外壳关闭分派、T41 现状。</summary>
    private static void CheckSourceWiring()
    {
        var root = FindRoot();
        Check.True(root != null, "S1-QA：能定位仓库根目录");
        if (root == null) return;

        // —— App.xaml 不得有 StartupUri（否则会多开一个窗口）——
        var appXaml = Path.Combine(root, "ClientGUI", "App.xaml");
        Check.True(File.Exists(appXaml), "S1-QA：ClientGUI/App.xaml 存在");
        if (File.Exists(appXaml))
        {
            var text = StripComments(File.ReadAllText(appXaml));
            Check.False(text.Contains("StartupUri", StringComparison.Ordinal),
                "S1-QA：App.xaml 没有 StartupUri（启动窗口只由 OnStartup 决定）");
        }

        // —— App.OnStartup 只创建唯一外壳 ——
        var appCs = Path.Combine(root, "ClientGUI", "App.xaml.cs");
        if (File.Exists(appCs))
        {
            var text = File.ReadAllText(appCs);
            Check.True(text.Contains("new ShellWindow().Show()", StringComparison.Ordinal),
                "S1-QA：App.OnStartup 启动的是 ShellWindow（唯一顶层窗口）");
            Check.Equal(1, CountOf(text, "new ShellWindow().Show()"),
                "S1-QA：OnStartup 里 ShellWindow 只被 Show 一次");
            var onStartup = MethodBody(text, "protected override void OnStartup");
            Check.True(onStartup.Length > 0, "S1-QA：能截出 App.OnStartup 的方法体");
            // 注释里也会提到这两个旧方法名（「换回 ShowLogin() 即回退」），必须先剥掉注释再判。
            var onStartupCode = StripLineComments(onStartup);
            Check.False(onStartupCode.Contains("ShowLogin()", StringComparison.Ordinal),
                "S1-QA：OnStartup 里不再调用旧的 ShowLogin()（旧流程只留作回退点）");
            Check.False(onStartupCode.Contains("RunRoomLoop", StringComparison.Ordinal),
                "S1-QA：OnStartup 里不再调用旧的 RunRoomLoop()");
            // ⚠ S4 翻面：S1 时这两条断言「旧流程仍保留（架构 §10.3 的唯一开关式回退点），S4 才删」。
            // S4 就是删它们的那一批 —— 现在断言**已删除**。方向反过来仍是守卫：
            // 谁把回退点加回来，等于把多窗口编排重新引回单窗口外壳，这条会立刻红。
            Check.False(text.Contains("private void ShowLogin()", StringComparison.Ordinal),
                "S4-QA：旧的 ShowLogin() 回退点已随 S4 删除（单窗口外壳不再留多窗口回退开关）");
            Check.False(text.Contains("private void RunRoomLoop(", StringComparison.Ordinal),
                "S4-QA：旧的 RunRoomLoop() 已随 S4 删除");
        }

        // —— 外壳关闭分派接线 ——
        var shellCs = Path.Combine(root, "ClientGUI", "ShellWindow.xaml.cs");
        if (File.Exists(shellCs))
        {
            var text = File.ReadAllText(shellCs);
            // ⚠ S6 规格更新（2026-10-24，主理人代改，QA 因 429 不在线）：外壳原来直接调
            //   CloseIntent.Resolve + RequiresFullShutdown 两个纯函数自己拼分支；S6 把「这次关闭
            //   该做什么」整个收进 ResolveShellClose / AfterConfirm 两个纯函数（否则「放行后忘了
            //   请求退出应用」这个 bug 无法被断言钉死）。守卫意图不变：**外壳不许自己另写一套判断**。
            Check.True(text.Contains("CloseIntent.ResolveShellClose(", StringComparison.Ordinal),
                "S1-QA：外壳关闭走纯函数分派（不另写一套判断）");
            Check.True(text.Contains("CloseIntent.AfterConfirm(", StringComparison.Ordinal),
                "S6-QA：确认框的两种结果也走纯函数（不放任外壳自行解释）");
            Check.True(text.Contains("e.Cancel = true", StringComparison.Ordinal),
                "S1-QA：房间页那次关闭被拦下（e.Cancel=true），改走退出房间");
            Check.True(text.Contains("AppShutdown.ConfirmClose(this)", StringComparison.Ordinal),
                "S1-QA：会真正停服务器的入口仍保留一次关窗确认");
            Check.True(text.Contains("_menuExitRequested = true", StringComparison.Ordinal),
                "S1-QA：菜单退出先置位再 Shutdown（否则房间页会把自己拦在房间里）");
            Check.Equal(2, CountOf(text, "Navigation.BackAsync()"),
                "S1-QA：× 与返回键位共用同一个 BackAsync 出口（N9）");
        }

        // —— S4 收口：旧 10 个 Window 类**已删除** ——
        //   ⚠ 翻面记录：S1 时这里断言的是「都还在（S1 一行未删，S4 才删）」。
        //   S4 就是那一批，所以按计划反向：文件必须消失，且 ClientGUI 里 `: Window` 的类只剩 ShellWindow。
        //   只断言「文件不在」还不够 —— 有人可能把类搬进别的文件名里复活它，所以两条一起上。
        var retiredWindows = new[]
        {
            @"Views\LoginWindow.xaml.cs", @"Views\RoomListWindow.xaml.cs", @"MainWindow.xaml.cs",
            @"Views\CreateRoomWindow.xaml.cs", @"Views\HostSettingsWindow.xaml.cs", @"Views\SettingsWindow.xaml.cs",
            @"Views\InviteCodeWindow.xaml.cs", @"Views\KeepRoomWindow.xaml.cs",
            @"Views\AboutWindow.xaml.cs", @"Views\ChangelogWindow.xaml.cs"
        };
        var stillThere = retiredWindows
            .Where(w => File.Exists(Path.Combine(root, "ClientGUI", w.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();
        Check.Equal(0, stillThere.Count,
            "S4-QA：旧 10 个 Window 类已全部删除（残留：" + string.Join(",", stillThere) + "）");

        // `: Window` 的类只剩 ShellWindow —— 这是 S4 收口最硬的一条：单窗口外壳。
        // 用正则匹配**类声明**，不能直接搜 ": Window"（`WindowStartupLocation.CenterScreen` 之类会误命中）。
        var windowClasses = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "ClientGUI"), "*.xaml.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var match = System.Text.RegularExpressions.Regex.Match(
                StripLineComments(File.ReadAllText(file)), @"class\s+(\w+)\s*:\s*Window\b");
            if (match.Success) windowClasses.Add(match.Groups[1].Value);
        }

        Console.WriteLine("   · S4 收口后 ClientGUI 里的 Window 子类：" + string.Join("，", windowClasses));
        Check.SequenceEqual(new[] { "ShellWindow" }, windowClasses.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            $"S4-QA：整个 ClientGUI 只剩 ShellWindow 一个窗口（实际：{string.Join(",", windowClasses)}）");

        // —— T41：S1 时「产品代码里没有调用方」（缺陷潜在不可达）；S2 修好后由
        //   `RoomListPage` 的「创建房间」调用，且必须走**浮层**而不是换页。
        //   这条守卫因此从「缺陷不可达」升级成「**修复不可回退**」：
        //   调用方消失（回退成没人用）或变多（又有人接入未修的语义）都会红。
        //   「调用」写作 `.PushForResultAsync<`；定义处是 `public Task<T?> PushForResultAsync<T>(`
        //   （前面没有点），按此把定义与调用区分开，避免把声明本身误判成调用。
        var productCallers = Directory
            .EnumerateFiles(Path.Combine(root, "ClientGUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StripLineComments(File.ReadAllText(f)).Contains(".PushForResultAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Check.Equal(1, productCallers.Count,
            "S2-QA（T41）：产品代码里**恰好一个** PushForResultAsync 调用方（实际：" +
            string.Join(",", productCallers) + "）");
        Check.Equal("RoomListPage.xaml.cs", productCallers.FirstOrDefault(),
            "S2-QA（T41）：该调用方就是房间列表页的「创建房间」");

        // 实现侧不许再退回换页语义：PushForResultAsync 必须经 PushCore(overlay: true)。
        var navSource = File.ReadAllText(Path.Combine(root, "ClientGUI", "Services", "NavigationService.cs"));
        var pushForResultBody = MethodBody(navSource, "public Task<T?> PushForResultAsync<T>(");
        Check.True(pushForResultBody.Contains("PushCore(request, overlay: true)", StringComparison.Ordinal),
            "S2-QA（T41）：PushForResultAsync 走 PushCore(overlay: true)（浮层），没有退回 Push（换页）");
        Check.False(pushForResultBody.Contains("Push(request)", StringComparison.Ordinal),
            "S2-QA（T41）：PushForResultAsync 里不再出现 `Push(request)`（T41 的原始写法）");
    }

    // ==================================================================
    //  --ui：真实布局
    // ==================================================================

    /// <summary>跑需要 STA / 真实布局的 S1 验收断言。</summary>
    public static void RunUiChecks()
    {
        Console.WriteLine("   —— v2.6.0 S1：QA 独立验收（真实布局） ——");

        CheckTenPagesStillConstructible();
        CheckShellIsSingleHost();
        CheckPageSkeletonsMeasured();
        CheckRoomPageKeepsMenu();

        // 下面两项要创建**真实页面**（UserControl → 必须 STA），因此归到 --ui 批次。
        CheckQ1BCodeLevel();
        CheckSubscriptionDoesNotAccumulate();
    }

    /// <summary>
    /// S4：十页各自可实例化且 `Id` 自报一致。
    ///
    /// ⚠ 翻面记录：S1 时这里是「10 个旧 Window 仍可实例化」（S1 的承诺是"一行未删"）。
    /// S4 删掉旧窗口后，那条承诺自然失效，但**断言条数不能跟着掉**（旧删 N / 新覆盖 M，M ≥ N）：
    /// 旧表 10 个窗口 × 1 条 = 10 条，新表 10 个页面 × 1 条 = 10 条，条数持平。
    /// 只断言"工厂不抛"还不够 —— `Id` 回错（页面接到别的枚举）是页面化最隐蔽的错，所以合成一条判据。
    /// </summary>
    private static void CheckTenPagesStillConstructible()
    {
        var factory = new PageFactory(new AppSession());
        var ids = new[]
        {
            PageId.Login, PageId.RoomList, PageId.Room, PageId.CreateRoom, PageId.HostSettings,
            PageId.Settings, PageId.InviteCode, PageId.KeepRoom, PageId.About, PageId.Changelog
        };

        foreach (var id in ids)
        {
            try
            {
                var page = factory.Create(new PageRequest(id));
                Check.True(page.Id == id, $"S4-QA：页面 {id} 可实例化且自报同一个 Id（实际 {page.Id}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · 页面 {id} 实例化异常：{ex.Message}");
                Check.True(false, $"S4-QA：页面 {id} 可实例化");
            }
        }
    }

    /// <summary>外壳是唯一宿主：内容只有一个 ContentControl，启动后装的是登录页且不可返回。</summary>
    private static void CheckShellIsSingleHost()
    {
        ShellWindow? shell = null;
        try
        {
            shell = new ShellWindow();

            var host = shell.FindName("PageHost") as ContentControl;
            Check.True(host != null, "S1-QA：外壳暴露唯一页面宿主 PageHost");
            Check.True(ReferenceEquals(shell.Content, host),
                "S1-QA：外壳的直接内容就是 PageHost（没有第二层容器藏第二个页面区）");

            Check.True(shell.MinWidth >= 760, $"S1-QA：外壳最小宽 ≥760（实际 {shell.MinWidth:F0}）");
            Check.True(shell.MinHeight >= 520, $"S1-QA：外壳最小高 ≥520（实际 {shell.MinHeight:F0}）");

            // 真去 Show() 会弹窗，测试环境不允许 → 手动补一次 Loaded，走的正是真实启动路径。
            shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, shell));

            var nav = shell.Session.Navigation;
            Check.Equal(PageId.Login, nav.Current?.Id, "S1-QA：外壳启动后当前页是登录页（根页）");
            Check.Equal(1, nav.Depth, "S1-QA：外壳启动后栈深为 1");
            Check.False(nav.CanGoBack, "S1-QA：外壳启动后不允许返回（根页）");
            Check.True(host?.Content is LoginPage, "S1-QA：PageHost 里装的确实是 LoginPage 实例");
            Check.True(shell.Title.Contains("登录"), $"S1-QA：窗口标题随页变化（实际「{shell.Title}」）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 外壳自检异常：" + ex.Message);
            Check.True(false, "S1-QA：ShellWindow 能实例化并切到登录页");
        }
        finally
        {
            try { shell?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    /// <summary>三页骨架实测：导航栏 ≥56、标题几何居中（实测值打进日志）、主按钮通栏 ≥80%（I5）。</summary>
    private static void CheckPageSkeletonsMeasured()
    {
        (string Name, Func<AppSession, FrameworkElement> Create, double W, double H, bool Primary)[] table =
        {
            ("登录页", s => new LoginPage(s), 470, 792, true),
            ("房间列表页", s => new RoomListPage(s), 960, 640, true),
            ("房间页", s => new RoomPage(s), 1220, 780, false)
        };

        foreach (var (name, create, w, h, expectPrimary) in table)
        {
            try
            {
                var page = create(new AppSession());
                Layout(page, w, h);

                Check.True(page.ActualWidth > 0 && page.ActualHeight > 0,
                    $"S1-QA：{name} 能完成布局（{page.ActualWidth:F0}×{page.ActualHeight:F0}）");

                var bar = page.FindName("NavBar") as FrameworkElement;
                Check.True(bar != null, $"S1-QA：{name} 有导航栏（NavBar）");
                if (bar != null)
                    Check.True(bar.ActualHeight >= 56,
                        $"S1-QA：{name} 导航栏实测高 ≥56（实际 {bar.ActualHeight:F1}）");

                FrameworkElement? title = page.FindName("NavTitleText") as FrameworkElement;
                title ??= page.FindName("NavTitleButton") as FrameworkElement;
                Check.True(title != null, $"S1-QA：{name} 有导航标题");
                if (title != null && bar != null) MeasureCentered(name, title, bar, page);

                if (expectPrimary) MeasurePrimary(name, page, w, h);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · {name} 骨架实测异常：{ex.Message}");
                Check.True(false, $"S1-QA：{name} 骨架实测无异常");
            }
        }
    }

    /// <summary>
    /// 标题相对**整条导航栏**的偏移：这里按架构 I5 的目标值 **≤1px** 判定
    /// （比既有守护的 <c>max(2px, 5%)</c> 更严 —— QA 只收紧、不放松）。
    /// </summary>
    private static void MeasureCentered(string label, FrameworkElement title, FrameworkElement bar, FrameworkElement root)
    {
        try
        {
            var titleLeft = title.TranslatePoint(new Point(0, 0), root).X;
            var barLeft = bar.TranslatePoint(new Point(0, 0), root).X;
            var offset = Math.Abs((titleLeft + title.ActualWidth / 2) - (barLeft + bar.ActualWidth / 2));

            Console.WriteLine($"   · {label}：标题相对整条导航栏偏移 {offset:F2}px（QA 判据 ≤1px）");
            Check.True(offset <= 1.0,
                $"S1-QA：{label} 导航标题几何居中 ≤1px（实测 {offset:F2}px，I5）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · {label} 居中无法换算：{ex.Message}");
            Check.True(false, $"S1-QA：{label} 导航标题居中可测量");
        }
    }

    /// <summary>主按钮通栏：登录页两标签页各量一次，房间列表页量 PrimaryAction。</summary>
    private static void MeasurePrimary(string label, FrameworkElement page, double w, double h)
    {
        if (page.FindName("Tabs") is TabControl tabs && page.FindName("EmbeddedPrimaryAction") != null)
        {
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Layout(page, w, h);
                MeasureOne(page, i == 0 ? "EmbeddedPrimaryAction" : "PrimaryAction", $"{label}·第{i + 1}页");
            }

            tabs.SelectedIndex = 0;
            Layout(page, w, h);
            return;
        }

        MeasureOne(page, "PrimaryAction", label);
    }

    private static void MeasureOne(FrameworkElement page, string elementName, string label)
    {
        if (page.FindName(elementName) is not FrameworkElement button) return;

        var ratio = button.ActualWidth / page.ActualWidth;
        Console.WriteLine($"   · {label} 主按钮：{button.ActualHeight:F0}px 高，通栏 {ratio:P0}");

        Check.True(button.ActualHeight >= 44,
            $"S1-QA：{label} 主按钮热区 ≥44（实际 {button.ActualHeight:F0}）");
        Check.True(ratio >= 0.8,
            $"S1-QA：{label} 主按钮通栏 ≥80%（实际 {ratio:P0}）");
    }

    /// <summary>Q3=A：房间页顶部必须还有菜单栏（Ctrl+G/I/L/Q 与 F1 的可发现性靠它）。</summary>
    private static void CheckRoomPageKeepsMenu()
    {
        try
        {
            var page = new RoomPage(new AppSession());
            Layout(page, 1220, 780);

            var menu = FindLogical<Menu>(page);
            Check.True(menu != null, "S1-QA：房间页顶部仍有菜单栏（Q3=A）");
            if (menu == null) return;

            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();
            Check.Equal(3, headers.Count, $"S1-QA：菜单栏三个顶级菜单（实际 {headers.Count}：{string.Join("/", headers)}）");
            Check.True(headers.Any(h => h.Contains("游戏")), "S1-QA：菜单含「游戏」");
            Check.True(headers.Any(h => h.Contains("视图")), "S1-QA：菜单含「视图」");
            Check.True(headers.Any(h => h.Contains("帮助")), "S1-QA：菜单含「帮助」");

            var all = Flatten(menu).OfType<MenuItem>().ToList();
            foreach (var gesture in new[] { "Ctrl+G", "Ctrl+I", "Ctrl+L", "Ctrl+Q", "F1" })
            {
                Check.True(all.Any(m => (m.InputGestureText ?? "").Contains(gesture, StringComparison.Ordinal)),
                    $"S1-QA：菜单里仍展示 {gesture} 快捷键提示");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 房间页菜单自检异常：" + ex.Message);
            Check.True(false, "S1-QA：房间页菜单栏可检查");
        }
    }

    // ==================================================================
    //  --embedded：真起服验证 Q1=B（不停服务器 / 不断连接）
    // ==================================================================

    /// <summary>
    /// Q1=B 的两个必测点（架构 §0 / §9.1 ⑥⑦）里，**⑥「返回登录页后服务器仍在跑」**
    /// 只在真服务器下才有意义 —— 这里起一个真内置服务器跑一遍导航，再原样停掉。
    /// </summary>
    public static async Task RunLiveQ1BChecksAsync()
    {
        Console.WriteLine("   —— S1-QA：Q1=B 真服务器复核（列表页返回登录页不停服务器） ——");

        var service = EmbeddedServerService.Current;
        var wasRunning = service.IsRunning;

        try
        {
            if (!wasRunning)
            {
                // 端口**不写死**：多人/多机并发跑时写死号会互相抢，抢不到就整组变红，
                // 而这条断言真正要验的是「返回登录页前后**端口没变**」，与具体是哪个号无关。
                // 绑回环的 0 号端口让系统分配 → 取号 → 立刻释放 → 再交给内置服务器。
                service.Port = PickFreeLoopbackPort();
                service.AllowPortDrift = true;
                await service.StartAsync();
            }

            Check.True(service.IsRunning, "S1-QA-Q1B：真服务器已在运行（前置条件）");
            var port = service.Port;

            var session = new AppSession();

            // 真登录（用随机账号，不依赖 admin 的密码是否被改过）。
            session.Login.ServerUrl = service.LocalUrl;
            var user = "qa-shell-" + Guid.NewGuid().ToString("N")[..8];
            var loggedIn = await session.Login.LoginWithCredentialsAsync(service.LocalUrl, user, "pw-" + user);
            Check.True(loggedIn, $"S1-QA-Q1B：能连上真服务器并登录（{user}）");
            Check.True(session.Login.IsLoggedIn, "S1-QA-Q1B：登录态为 true");

            // —— 导航 + 登录页主按钮全部在**同一个** STA 线程上跑：
            //    页面是 UserControl（只能在 STA 上创建），且 LoginViewModel.EnterRoomList()
            //    会回调当前页的 LoginSucceeded 处理器 —— 跨线程调它会抛（真实 App 全程单线程，无此问题）。
            RunOnSta(() =>
            {
                var nav = session.Navigation;

                nav.Reset(new PageRequest(PageId.Login));
                nav.Push(new PageRequest(PageId.RoomList));
                Check.Equal(PageId.RoomList, nav.Current?.Id, "S1-QA-Q1B：先进入房间列表页");

                var back = nav.BackAsync().GetAwaiter().GetResult();
                Check.True(back, "S1-QA-Q1B：列表页返回执行成功（Q1=B）");
                Check.Equal(PageId.Login, nav.Current?.Id, "S1-QA-Q1B：返回到登录页");
                Check.Equal(1, nav.Depth, "S1-QA-Q1B：栈深回到 1");
                Check.False(nav.CanGoBack, "S1-QA-Q1B：登录页 CanGoBack=false");

                // 核心：服务器没被停、连接没被拆。
                Check.True(service.IsRunning,
                    "S1-QA-Q1B：返回登录页后**内置服务器仍在运行**（导航不是关闭，I1/I2）");
                Check.Equal(port, service.Port, "S1-QA-Q1B：服务器端口未变化");
                Check.True(session.Login.IsLoggedIn, "S1-QA-Q1B：返回登录页后连接**未断开**（仍是登录态）");
                Check.False(AppShutdown.IsShuttingDown, "S1-QA-Q1B：没有触发完整收尾");

                // ③ 登录页主按钮：服务器在跑 + 已登录 → 文案切成「进入房间列表」。
                session.Embedded.RefreshComputedState();
                Check.True(session.Embedded.IsOneClickDone,
                    "S1-QA-Q1B：已登录 + 服务器运行 → IsOneClickDone=true");
                Check.Equal("进入房间列表", session.Embedded.MainButtonText,
                    "S1-QA-Q1B：此时主按钮文案是「进入房间列表」（Q1=B 的兜底分支）");

                // 把实测值打出来，报告里引用的是**跑出来的数**，不是推断。
                Console.WriteLine(
                    $"   · Q1=B 实测：返回登录页后服务器 {service.LocalUrl} 仍在运行=" +
                    $"{service.IsRunning}，登录态={session.Login.IsLoggedIn}，" +
                    $"栈深={nav.Depth}，CanGoBack={nav.CanGoBack}，" +
                    $"收尾中={AppShutdown.IsShuttingDown}，主按钮「{session.Embedded.MainButtonText}」");

                // 再点一次「进入房间列表」应能回列表，且不重复开服（端口没变）。
                Check.True(session.Login.EnterRoomList(),
                    "S1-QA-Q1B：「进入房间列表」凭既有会话即可进入（不重复开服）");
                Check.Equal(PageId.RoomList, nav.Current?.Id,
                    "S1-QA-Q1B：点「进入房间列表」后当前页是房间列表页");
                Check.Equal(port, service.Port, "S1-QA-Q1B：回到列表没有重新开服（端口不变）");

                return true;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · Q1=B 真服务器复核异常：" + ex);
            Check.True(false, "S1-QA-Q1B：Q1=B 真服务器链路无异常");
        }
        finally
        {
            try
            {
                if (!wasRunning && service.IsRunning) await service.StopAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("   · 停服务器失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 取一个当前空闲的回环端口交给内置服务器用（避免写死端口在并发环境下互抢）。
    ///
    /// 做法：绑 <c>0</c> 号端口让系统分配 → 读实际号 → 立刻释放。
    /// 「释放到别人抢走」之间有一个极小窗口，因此仍然打开 <c>AllowPortDrift</c> 兜底；
    /// 断言侧不校验具体号，只校验「返回前后不变」。
    /// </summary>
    private static int PickFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // ==================================================================
    //  自用工具（与 PageCatalog 各写各的，不共享）
    // ==================================================================

    /// <summary>
    /// 在独立 STA 线程上跑一段创建 UI 对象的代码（样式与 <c>UiSmokeCheck</c> 一致：
    /// 测试宿主主线程是 MTA，而 <c>UserControl</c> 只能在 STA 上构造）。
    /// </summary>
    private static T RunOnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new InvalidOperationException("STA 线程内失败：" + failure.Message, failure);
        return result;
    }

    /// <summary>反射数一个实例事件的订阅者数量。</summary>
    private static int FieldSubscribers(object target, string eventName)    {
        var field = target.GetType().GetField(eventName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return (field?.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>反射数一个静态事件的订阅者数量。</summary>
    private static int StaticSubscribers(Type type, string eventName)
    {
        var field = type.GetField(eventName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    /// <summary>逻辑树里找第一个 <typeparamref name="T"/>（未挂到窗口的 UserControl 也能查）。</summary>
    private static T? FindLogical<T>(DependencyObject root) where T : DependencyObject
        => Flatten(root).OfType<T>().FirstOrDefault();

    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Flatten(child))
                yield return nested;
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var at = text.IndexOf(token, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// 截取一个方法体：从签名后的第一个 <c>{</c> 起做**花括号配平**，返回配平到的那一段。
    ///
    /// 为什么不用「找第一个行首 8 空格 <c>}</c>」那种粗暴写法：方法体缩进不一定是 8 空格
    /// （类成员就是 4 空格），找不到时会退化成「签名到文件末尾」，
    /// 于是断言可能匹配到**别的方法**里的字符串而假通过 —— 源码级守卫最怕这种假绿。
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var open = source.IndexOf('{', start);
        if (open < 0) return string.Empty;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[start..(i + 1)];
            }
        }

        return source[start..];
    }

    private static string StripComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>剥掉 <c>//</c> 行注释（源码级守卫必须剥，否则「注释里提到某方法」会被误判成调用）。</summary>
    private static string StripLineComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", "");

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ClientGUI"))
                && File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>QA 自己的假页面 / 假工厂（只记录回调次数）。</summary>
    private sealed class QaPage : IPage
    {
        public QaPage(PageId id) => Id = id;

        public PageId Id { get; }
        public string Title => Id.ToString();
        public bool CanGoBack => Id != PageId.Login;
        public int EnterCount { get; private set; }
        public int LeaveCount { get; private set; }

        public void OnEnter() => EnterCount++;
        public void OnLeave() => LeaveCount++;
        public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);
    }

    private sealed class QaPageFactory : IPageFactory
    {
        private readonly List<QaPage> _created = new();

        public QaPage Last => _created[^1];

        public IPage Create(PageRequest request)
        {
            var page = new QaPage(request.Id);
            _created.Add(page);
            return page;
        }
    }
}
