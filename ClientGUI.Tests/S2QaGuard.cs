using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views.Pages;

namespace ClientGUI.Tests;

/// <summary>
/// v2.6.0 **S2 的 QA 独立验收断言**（严过关 · 背靠背编写）。
///
/// S2 把「创建房间 / 房主设置 / 设置 / 邀请码」四页迁进单窗口，并按 T41 的施工顺序
/// **先改实现、再补断言**：<see cref="NavigationService"/> 拆出「换页」与「浮层」两种入栈语义。
/// 本文件不共享 <c>PageCatalog</c> / <c>S1QaGuard</c> 的任何辅助方法，独立造页面、独立数订阅，
/// 因为「断言只增不减」若由被测方自己声明，等于没有第三方证据。
///
/// 两组挂载点：
/// <list type="bullet">
///   <item><see cref="RunLogicChecks"/> —— 默认组：浮层语义、结果通道、浮层叠浮层、源码接线；</item>
///   <item><see cref="RunUiChecks"/> —— <c>--ui</c>：四页骨架实测、浮层不改下层页的存活状态、工厂十页（S3 起）。</item>
/// </list>
/// </summary>
public static class S2QaGuard
{
    // ==================================================================
    //  默认组：纯逻辑（不创建任何 WPF 控件，可在 MTA 上跑）
    // ==================================================================

    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.6.0 S2：QA 独立验收（浮层语义 · 纯逻辑） ════════");

        CheckPushVsOverlay();
        CheckDismissOverlay();
        CheckResultChannel();
        CheckOverlayNesting();
        CheckPendingNeverHangs();
        CheckFactoryAndWiring();
    }

    /// <summary>
    /// T41 的核心：<c>Push</c>（换页）与 <c>PushOverlay</c>（浮层）的 Enter/Leave 序列**必须不同**。
    /// 判据用「计数」而不是「有没有调」：浮层进出多轮后下层页的 <c>Enters</c> 必须仍是 1。
    /// </summary>
    private static void CheckPushVsOverlay()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        var login = factory.Last;
        Check.Equal(1, nav.Depth, "S2-QA：Reset 后栈深 1（N1）");
        Check.Equal(1, login.Enters, "S2-QA：根页进场一次");

        // ① 换页：旧栈顶**退场**。
        nav.Push(new PageRequest(PageId.RoomList));
        var list = factory.Last;
        Check.Equal(2, nav.Depth, "S2-QA：Push 后栈深 +1");
        Check.Equal(1, login.Leaves, "S2-QA：Push（换页）让下层页 OnLeave 一次 —— 与浮层相反");
        Check.Equal(1, list.Enters, "S2-QA：Push 后新页进场一次");

        // ② 浮层：下层页**不退场**（订阅与轮询继续）。
        nav.PushOverlay(new PageRequest(PageId.Settings));
        var settings = factory.Last;
        Check.Equal(3, nav.Depth, "S2-QA：浮层也占一层（栈深 +1，不是 +0 也不是 +2）");
        Check.Equal(0, list.Leaves, "S2-QA（T41）：浮层入栈**不调**下层页 OnLeave（房间列表页退场 0 次）");
        Check.Equal(1, settings.Enters, "S2-QA：浮层页自己进场一次");
        Check.True(nav.CurrentIsOverlay, "S2-QA：栈顶被标记为浮层（CurrentIsOverlay）");

        // ③ 浮层出栈：下层页**不该再收一次 OnEnter**（否则订阅按浮层次数翻倍）。
        var back = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back, "S2-QA：BackAsync 弹出浮层返回 true");
        Check.Equal(1, settings.Leaves, "S2-QA：浮层出栈时自己 OnLeave 一次");
        Check.Equal(1, list.Enters, "S2-QA（T41）：浮层出栈**不再**给下层页调 OnEnter（仍是 1 次）");
        Check.Equal(2, nav.Depth, "S2-QA：浮层出栈后栈深回到 2");
        Check.False(nav.CurrentIsOverlay, "S2-QA：栈顶回到普通页");

        // ④ 换页式出栈与浮层相反：回来的那页要重新进场。
        var back2 = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back2, "S2-QA：再返回一层成功");
        Check.Equal(2, login.Enters, "S2-QA：换页式出栈让下层页**重新** OnEnter（与浮层形成对照）");
        Check.Equal(1, nav.Depth, "S2-QA：回到根页栈深 1");
    }

    /// <summary>外壳点 × 收起浮层：只有栈顶真是浮层才动手（否则返回 false，交给关闭意图表）。</summary>
    private static void CheckDismissOverlay()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Login));
        Check.False(nav.DismissOverlay(), "S2-QA：栈顶不是浮层时 DismissOverlay 返回 false（不动栈）");
        Check.Equal(1, nav.Depth, "S2-QA：DismissOverlay 返回 false 时栈深不变");

        nav.Push(new PageRequest(PageId.RoomList));
        Check.False(nav.DismissOverlay(), "S2-QA：普通页在栈顶时 DismissOverlay 仍返回 false");
        Check.Equal(2, nav.Depth, "S2-QA：普通页不会被 × 误弹");

        nav.PushOverlay(new PageRequest(PageId.HostSettings));
        var host = factory.Last;
        Check.True(nav.DismissOverlay(), "S2-QA：栈顶是浮层时 DismissOverlay 返回 true");
        Check.Equal(1, host.Leaves, "S2-QA：被收起的浮层 OnLeave 一次");
        Check.Equal(2, nav.Depth, "S2-QA：收起浮层后栈深 -1");
        Check.False(nav.CurrentIsOverlay, "S2-QA：收起之后栈顶不再是浮层");
    }

    /// <summary>带结果返回的三条路径：Complete 给结果 / 返回视为取消 / 没人等时不动栈。</summary>
    private static void CheckResultChannel()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);
        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        // ① 正常给结果。
        var task = nav.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));
        Check.False(task.IsCompleted, "S2-QA：PushForResultAsync 返回未完成的等待（同步不抢跑）");
        Check.Equal(3, nav.Depth, "S2-QA：带结果的浮层同样占一层");

        var payload = new object();
        Check.True(nav.Complete(payload), "S2-QA：Complete 在有等待者时返回 true 并出栈");
        Check.True(task.Wait(2000), "S2-QA：Complete 之后等待结束（不会挂住）");
        Check.True(ReferenceEquals(payload, task.Result), "S2-QA：等待者拿到的是页面交回的那个结果对象");
        Check.Equal(2, nav.Depth, "S2-QA：Complete 之后栈深回到 2");

        // ② 直接返回 = 取消，结果为 null。
        var cancelling = nav.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));
        Check.True(nav.BackAsync().GetAwaiter().GetResult(), "S2-QA：浮层可以直接返回（取消）");
        Check.True(cancelling.Wait(2000), "S2-QA：取消之后等待也结束（不会挂住）");
        Check.True(cancelling.Result == null, "S2-QA：取消时结果为 null");

        // ③ 没有等待者时 Complete 不许动栈（页面被普通 Push 打开时的兜底）。
        var depthBefore = nav.Depth;
        Check.False(nav.Complete(new object()), "S2-QA：没有等待者时 Complete 返回 false");
        Check.Equal(depthBefore, nav.Depth, "S2-QA：没有等待者时 Complete 不动栈（页面不会莫名消失）");
    }

    /// <summary>
    /// **浮层叠浮层**：在「创建房间」（带结果返回）上再开一层设置，收起设置时
    /// **不能**把创建房间的等待一起判成「取消」——否则创建页还在屏幕上、
    /// 它的等待已经结束，用户再点「创建」就没反应了（静默失败）。
    /// </summary>
    private static void CheckOverlayNesting()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);
        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        var create = nav.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));
        Check.Equal(3, nav.Depth, "S2-QA：创建房间浮层入栈（栈深 3）");

        // 用户在创建房间里按 Ctrl+, 打开设置（S2 之后的真实路径）。
        nav.PushOverlay(new PageRequest(PageId.Settings));
        Check.Equal(4, nav.Depth, "S2-QA：设置浮层叠在创建房间之上（栈深 4）");

        // 收起设置（Esc / 点 ×）——只该关掉设置这一层。
        Check.True(nav.DismissOverlay(), "S2-QA：收起最上面的设置浮层成功");
        Check.Equal(3, nav.Depth, "S2-QA：收起设置后栈深回到 3（创建房间还在）");
        Check.Equal(PageId.CreateRoom, nav.Current?.Id, "S2-QA：当前页仍是创建房间");

        Check.False(create.IsCompleted,
            "S2-QA（T41 叠加）：收起上层的设置浮层**不得**把「创建房间」的等待判成取消");
        Check.True(nav.Complete(new object()),
            "S2-QA（T41 叠加）：创建房间此时仍应有人等结果（Complete 返回 true）");
    }

    /// <summary>
    /// 「等待者永远挂住」的两条路径（原 P3）：同 Id 连点重复入栈、浮层开着时 <c>Reset</c>。
    /// 两条都必须**当场按取消结算**，否则 <c>async void</c> 调用方的 await 永不返回
    /// （处理器挂在堆上、页面引用不释放，用户侧表现为「点了没反应」）。
    /// </summary>
    private static void CheckPendingNeverHangs()
    {
        // ① 连点两次「创建房间」：第一次的等待必须被结算成取消，不能悬着。
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);
        nav.Reset(new PageRequest(PageId.Login));
        nav.Push(new PageRequest(PageId.RoomList));

        var first = nav.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));
        var second = nav.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));
        Check.True(first.Wait(2000), "S2-QA：同 Id 连点两次时，第一次的等待被结算（不会永久挂住）");
        Check.True(first.Result == null, "S2-QA：连点产生的那次等待按「取消」结算（结果 null）");
        Check.Equal(3, nav.Depth, "S2-QA：连点之后栈深仍是 3（旧格被替换，不是叠两层）");

        var payload = new object();
        Check.True(nav.Complete(payload), "S2-QA：连点之后第二次等待仍然有效（Complete 返回 true）");
        Check.True(second.Wait(2000), "S2-QA：第二次等待拿到结果");
        Check.True(ReferenceEquals(payload, second.Result), "S2-QA：第二次等待拿到的是提交的那个结果");

        // ② 浮层开着时 Reset：等待者必须被结算，不能悬着。
        var factory2 = new QaPageFactory();
        var nav2 = new NavigationService(factory2);
        nav2.Reset(new PageRequest(PageId.Login));
        nav2.Push(new PageRequest(PageId.RoomList));
        var pending = nav2.PushForResultAsync<object>(new PageRequest(PageId.CreateRoom));

        nav2.Reset(new PageRequest(PageId.Login));
        Check.True(pending.Wait(2000), "S2-QA：浮层开着时 Reset，等待者被结算（不会永久挂住）");
        Check.True(pending.Result == null, "S2-QA：Reset 清栈时等待者按「取消」结算");
        Check.Equal(1, nav2.Depth, "S2-QA：Reset 之后栈深 1（浮层被清掉）");
    }

    /// <summary>源码级接线：七页工厂 + 谁用浮层 + 页面里不许再 new 旧窗口 + 外壳 × 走 DismissOverlay。</summary>
    private static void CheckFactoryAndWiring()
    {
        var root = FindRoot();
        Check.True(root != null, "S2-QA：能定位仓库根目录");
        if (root == null) return;

        var navPath = Path.Combine(root, "ClientGUI", "Services", "NavigationService.cs");
        var navSource = File.ReadAllText(navPath);

        // ① 工厂认识十页（S2 的七页 + S3 的三页）。
        //   ⚠ 翻面记录：S2 那轮这里写的是「KeepRoom / About / Changelog 本轮**不该**已接入」，
        //   那是**当时**的记账本（开工令：页数契约 7 页，S3 才扩到 10）。S3 交付后按新契约翻面，
        //   断言条数不变（3 条 False → 3 条 True），删改理由记在 docs §18 的翻面记录里。
        var branches = new[]
        {
            "Login", "RoomList", "Room", "CreateRoom", "HostSettings",
            "Settings", "InviteCode", "KeepRoom", "About", "Changelog"
        };
        var missing = branches.Where(id => !navSource.Contains($"PageId.{id} => new", StringComparison.Ordinal)).ToList();
        Check.Equal(0, missing.Count,
            "S2-QA：PageFactory 已接十页（S2 七页 + S3 三页；缺：" + string.Join(",", missing) + "）");
        Check.True(navSource.Contains("throw new NotSupportedException", StringComparison.Ordinal),
            "S2-QA：工厂仍保留 NotSupportedException 兜底分支（不静默返回空页）");
        foreach (var id in new[] { "KeepRoom", "About", "Changelog" })
            Check.True(navSource.Contains($"PageId.{id} => new", StringComparison.Ordinal),
                $"S2-QA：PageId.{id} 已按 S3 契约接入（页数契约 7 → 10）");

        // ② 四个新页必须走浮层：页面里一处都不许 new 旧窗口。
        var pagesDir = Path.Combine(root, "ClientGUI", "Views", "Pages");
        var oldWindows = new[] { "CreateRoomWindow", "HostSettingsWindow", "SettingsWindow", "InviteCodeWindow" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pagesDir, "*.cs"))
        {
            var code = StripLineComments(File.ReadAllText(file));
            foreach (var window in oldWindows)
                if (code.Contains($"new {window}(", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:new {window}");
        }

        Check.Equal(0, offenders.Count,
            "S2-QA：四个新页面里没有任何 `new 旧窗口`（旧窗口只留给 S4 删除前的老路径；实际：" +
            string.Join(",", offenders) + "）");

        // ③ 谁在开浮层：设置任意页面可开（App 的 Ctrl+,）、房间页三处、列表页一处。
        var appSource = File.ReadAllText(Path.Combine(root, "ClientGUI", "App.xaml.cs"));
        Check.True(appSource.Contains("PushOverlay(new PageRequest(PageId.Settings))", StringComparison.Ordinal),
            "S2-QA：Ctrl+, 打开设置走浮层（从房间页打开时房间页不能退场）");

        var roomPage = File.ReadAllText(Path.Combine(pagesDir, "RoomPage.xaml.cs"));
        foreach (var id in new[] { "Settings", "HostSettings", "InviteCode" })
            Check.True(roomPage.Contains($"PushOverlay(new PageRequest(PageId.{id}", StringComparison.Ordinal),
                $"S2-QA：房间页打开 {id} 走浮层");
        Check.False(roomPage.Contains("Push(new PageRequest(PageId.Settings", StringComparison.Ordinal),
            "S2-QA：房间页不再用换页方式打开设置（那会把房间页 Detach 掉）");

        // ④ 外壳点 × 时先收起浮层，再走关闭意图表；BackAsync 仍是两个入口（N9 不破）。
        var shell = File.ReadAllText(Path.Combine(root, "ClientGUI", "ShellWindow.xaml.cs"));
        Check.True(shell.Contains("CurrentIsOverlay", StringComparison.Ordinal),
            "S2-QA：外壳判断「栈顶是浮层」");
        Check.True(shell.Contains("DismissOverlay()", StringComparison.Ordinal),
            "S2-QA：外壳在栈顶是浮层时用 DismissOverlay 收起（而不是走完整收尾）");
        Check.Equal(2, CountOf(shell, "Navigation.BackAsync()"),
            "S2-QA：外壳里 Navigation.BackAsync() 仍是 2 处（N9 单一出口没被绕过）");
    }

    // ==================================================================
    //  --ui：真实页面（STA）
    // ==================================================================

    public static void RunUiChecks()
    {
        Console.WriteLine("   —— v2.6.0 S2：QA 独立验收（真实页面 · 真实布局） ——");

        CheckFourPagesMeasured();
        CheckFactoryCreatesSevenPages();
        CheckOverlayKeepsBelowPageAlive();
        CheckViewOptionsBroadcast();
    }

    /// <summary>四页骨架实测：导航栏 ≥56、标题 ≤1px、主按钮通栏 ≥80% 且热区 ≥44。</summary>
    private static void CheckFourPagesMeasured()
    {
        (string Name, Func<AppSession, FrameworkElement> Create, double W, double H, string[] Names)[] table =
        {
            ("创建房间页", s => new CreateRoomPage(s), 448, 537,
                new[] { "NavBar", "NavTitleText", "GameBox", "RoomNameInput", "PasswordInput", "PrimaryAction" }),
            ("房主设置页", s => new HostSettingsPage(s), 584, 680,
                new[] { "NavBar", "NavTitleText", "Tabs", "MaxPlayersInput", "ClearPasswordBox", "LayoutList", "InviteCodeBox", "PrimaryAction" }),
            ("设置页", s => new SettingsPage(s), 540, 614,
                new[] { "NavBar", "NavTitleText", "Tabs", "ThemeSystem", "ThemeLight", "ThemeDark", "ServerSettingsButton", "ChangePasswordButton", "PrimaryAction" }),
            ("邀请码页", s => new InviteCodePage(s, "QA-CODE"), 508, 440,
                new[] { "NavBar", "NavTitleText", "CodeBox", "StatusText", "PrimaryAction" })
        };

        foreach (var (name, create, w, h, names) in table)
        {
            try
            {
                var page = create(new AppSession());
                Layout(page, w, h);

                Check.True(page.ActualWidth > 0 && page.ActualHeight > 0,
                    $"S2-QA：{name} 能完成布局（{page.ActualWidth:F0}×{page.ActualHeight:F0}）");

                var missing = names.Where(n => page.FindName(n) == null).ToList();
                Check.Equal(0, missing.Count,
                    $"S2-QA：{name} 命名契约齐全（缺：{string.Join(",", missing)}）");

                var bar = page.FindName("NavBar") as FrameworkElement;
                Check.True(bar != null, $"S2-QA：{name} 有导航栏");
                if (bar != null)
                    Check.True(bar.ActualHeight >= 56,
                        $"S2-QA：{name} 导航栏实测高 ≥56（实际 {bar.ActualHeight:F1}）");

                if (bar != null && page.FindName("NavTitleText") is FrameworkElement title)
                    MeasureCentered(name, title, bar, page);

                if (page.FindName("PrimaryAction") is FrameworkElement primary)
                {
                    var ratio = primary.ActualWidth / page.ActualWidth;
                    Console.WriteLine($"   · {name} 主按钮：{primary.ActualHeight:F0}px 高，通栏 {ratio:P0}");
                    Check.True(primary.ActualHeight >= 44,
                        $"S2-QA：{name} 主按钮热区 ≥44（实际 {primary.ActualHeight:F0}）");
                    Check.True(ratio >= 0.8,
                        $"S2-QA：{name} 主按钮通栏 ≥80%（实际 {ratio:P0}）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · {name} 实测异常：{ex.Message}");
                Check.True(false, $"S2-QA：{name} 骨架实测无异常");
            }
        }
    }

    /// <summary>
    /// 工厂十页逐一实例化：Id 必须原样回得来（页面接错枚举是页面化最隐蔽的错）。
    /// ⚠ S3 翻面：原来是「七页 + 三页抛 NotSupportedException」，页数契约 7 → 10 后
    /// 三页改为**正向**实例化；「兜底分支还在」这条改由「越界枚举必须抛」来行为级证明
    /// （比原来只在源码里找 `throw new NotSupportedException` 字符串更硬）。
    /// </summary>
    private static void CheckFactoryCreatesSevenPages()
    {
        var factory = new PageFactory(new AppSession());
        var ids = new[]
        {
            PageId.Login, PageId.RoomList, PageId.Room,
            PageId.CreateRoom, PageId.HostSettings, PageId.Settings, PageId.InviteCode,
            PageId.KeepRoom, PageId.About, PageId.Changelog     // S3 新增三页（页数契约 7 → 10）
        };

        foreach (var id in ids)
        {
            try
            {
                var page = factory.Create(new PageRequest(id, "QA"));
                Check.Equal(id, page.Id, $"S2-QA：工厂创建 PageId.{id} 后页面自报同一个 Id");
                Check.True(!string.IsNullOrWhiteSpace(page.Title), $"S2-QA：PageId.{id} 有导航标题");

                // 邀请码页必须把 PageRequest.Argument 里的码显示出来（参数丢了就成空码页）。
                if (id == PageId.InviteCode && page is FrameworkElement invite && invite.FindName("CodeBox") is TextBox codeBox)
                    Check.True(codeBox.Text.Contains("QA", StringComparison.Ordinal),
                        $"S2-QA：邀请码页显示 PageRequest.Argument 传进来的码（实际「{codeBox.Text}」）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · 工厂创建 {id} 异常：{ex.Message}");
                Check.True(false, $"S2-QA：工厂能创建 PageId.{id}");
            }
        }

        // 兜底分支的行为级证明：枚举里没有的号必须抛，而不是静默返回一个空页。
        var outOfRangeThrew = false;
        try { factory.Create(new PageRequest((PageId)999)); }
        catch (NotSupportedException) { outOfRangeThrew = true; }
        catch (Exception ex) { Console.WriteLine($"   · 越界 PageId 抛了非契约异常：{ex.GetType().Name}"); }

        Check.True(outOfRangeThrew,
            "S2-QA：越界 PageId 仍抛 NotSupportedException（兜底分支没被十页全覆盖顺手删掉）");
    }

    /// <summary>
    /// T41 的**行为级**证据：在真实房间列表页上开/关设置浮层 5 轮，
    /// 静态 <c>EmbeddedServerService.Changed</c> 的订阅者数不许涨；
    /// 同时浮层期间下层页 <c>IsHitTestVisible</c> 为 false（模态感），收起后恢复 true。
    /// </summary>
    private static void CheckOverlayKeepsBelowPageAlive()
    {
        try
        {
            var session = new AppSession();
            var nav = session.Navigation;

            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));

            var listPage = nav.Current as RoomListPage;
            Check.True(listPage != null, "S2-QA：栈顶是真实的房间列表页");

            var before = FieldSubscribers(EmbeddedServerService.Current, "Changed");
            Console.WriteLine($"   · 房间列表页进场后：EmbeddedServerService.Changed 订阅者 {before}");

            var after = before;
            for (var i = 0; i < 5; i++)
            {
                nav.PushOverlay(new PageRequest(PageId.Settings));

                if (i == 0)
                {
                    Check.False(listPage!.IsHitTestVisible,
                        "S2-QA（T41）：浮层期间下层页 IsHitTestVisible=false（模态感，N8）");
                    Check.Equal(PageId.Settings, nav.Current?.Id, "S2-QA：当前页是设置浮层");
                }

                nav.DismissOverlay();
                after = FieldSubscribers(EmbeddedServerService.Current, "Changed");

                if (i == 0)
                {
                    Check.True(listPage!.IsHitTestVisible,
                        "S2-QA（T41）：收起浮层后下层页恢复可点");
                    Check.Equal(PageId.RoomList, nav.Current?.Id, "S2-QA：收起后回到房间列表页");
                }
            }

            Console.WriteLine($"   · 浮层进出 5 轮后：EmbeddedServerService.Changed 订阅者 {after}");
            Check.Equal(before, after,
                "S2-QA（T41）：浮层进出 5 轮后静态订阅者数不涨（下层页未退场也未重复进场）");
            Check.Equal(2, nav.Depth, "S2-QA：5 轮之后栈深仍为 2（没有残留浮层）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 浮层存活自检异常：" + ex.Message);
            Check.True(false, "S2-QA：真实页面的浮层进出可检查");
        }
    }

    /// <summary>
    /// 三栏显隐的广播改由 <see cref="ViewOptions"/> 承担（S2 抽出来的独立通知点，不再挂在窗口类上）。
    /// 这里验两件事：①设置改动**真的**能让房间页收起中栏；②页面反复进出不累积订阅。
    /// </summary>
    private static void CheckViewOptionsBroadcast()
    {
        var state = UiStateStore.State;
        var savedChat = state.ShowChat;

        try
        {
            var session = new AppSession();
            var nav = session.Navigation;
            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.Room));

            var room = nav.Current as RoomPage;
            Check.True(room != null, "S2-QA：栈顶是真实的房间页");
            if (room == null) return;

            var chatView = room.FindName("ChatView") as FrameworkElement;
            var chatColumn = room.FindName("ChatColumn") as ColumnDefinition;
            Check.True(chatView != null && chatColumn != null, "S2-QA：房间页有中栏（ChatView / ChatColumn）");
            if (chatView == null || chatColumn == null) return;

            // ① 广播生效：把「显示房间消息」关掉，房间页应当立刻收起中栏。
            state.ShowChat = false;
            ViewOptions.Notify();
            Check.Equal(Visibility.Collapsed, chatView.Visibility,
                "S2-QA：设置页关掉「显示房间消息」→ 房间页中栏收起（ViewOptions 广播生效）");
            Check.True(chatColumn.Width.Value == 0,
                $"S2-QA：中栏列宽收到 0（实际 {chatColumn.Width.Value}）");

            state.ShowChat = true;
            ViewOptions.Notify();
            Check.Equal(Visibility.Visible, chatView.Visibility,
                "S2-QA：再打开「显示房间消息」→ 房间页中栏恢复");

            // ② 不累积订阅：房间页反复进出 5 轮后，ViewOptions.Changed 的订阅者数不涨。
            var before = StaticFieldSubscribers(typeof(ViewOptions), "Changed");
            for (var i = 0; i < 5; i++)
            {
                nav.BackAsync().GetAwaiter().GetResult();   // 房间页 → 列表页（房间页 OnLeave 必须退订）
                nav.Push(new PageRequest(PageId.Room));     // 列表页 → 房间页（重新进场、重新订阅）
            }

            var after = StaticFieldSubscribers(typeof(ViewOptions), "Changed");
            Console.WriteLine($"   · 房间页进出 5 轮前后：ViewOptions.Changed 订阅者 {before} → {after}");
            Check.True(after <= before,
                $"S2-QA：房间页进出 5 轮后 ViewOptions 订阅者不涨（{before} → {after}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 三栏显隐广播自检异常：" + ex.Message);
            Check.True(false, "S2-QA：三栏显隐广播可检查");
        }
        finally
        {
            state.ShowChat = savedChat;
            ViewOptions.Notify();
        }
    }

    // ==================================================================
    //  自用工具（与 PageCatalog / S1QaGuard 各写各的）
    // ==================================================================
    private sealed class QaPage : IPage
    {
        public QaPage(PageId id) => Id = id;

        public PageId Id { get; }

        public string Title => "QA-" + Id;

        public bool CanGoBack => true;

        public int Enters { get; private set; }

        public int Leaves { get; private set; }

        public void OnEnter() => Enters++;

        public void OnLeave() => Leaves++;

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

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    /// <summary>标题相对**整条导航栏**的几何偏移，按 I5 的 **≤1px** 判（比既有 5% 判据更严）。</summary>
    private static void MeasureCentered(string label, FrameworkElement title, FrameworkElement bar, FrameworkElement root)
    {
        var titleLeft = title.TranslatePoint(new Point(0, 0), root).X;
        var barLeft = bar.TranslatePoint(new Point(0, 0), root).X;
        var offset = Math.Abs((titleLeft + title.ActualWidth / 2) - (barLeft + bar.ActualWidth / 2));

        Console.WriteLine($"   · {label}：标题相对整条导航栏偏移 {offset:F2}px（QA 判据 ≤1px）");
        Check.True(offset <= 1.0, $"S2-QA：{label} 标题几何居中 ≤1px（实测 {offset:F2}px）");
    }

    /// <summary>数一个**实例**事件背后的订阅者（<c>EmbeddedServerService.Current.Changed</c> 是实例事件）。</summary>
    private static int FieldSubscribers(object target, string eventName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var field = target.GetType().GetField(eventName, flags);
        return (field?.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>数一个**静态**事件背后的订阅者（<c>ViewOptions</c> 是静态类）。</summary>
    private static int StaticFieldSubscribers(Type type, string eventName)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var field = type.GetField(eventName, flags);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    /// <summary>剥掉 <c>//</c> 行注释：源码级守卫必须先剥，否则「注释里提到」会被误判成调用。</summary>
    private static string StripLineComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", "");

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ClientGUI")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}
