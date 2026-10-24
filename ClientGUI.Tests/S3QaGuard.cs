using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClientGUI;
using ClientGUI.Models;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views;
using ClientGUI.Views.Pages;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// v2.6.0 **S3（关于 / 更新日志 / 保留房间）的 QA 独立验收**（严过关 · 背靠背）。
///
/// 判据是**先登记、后实测**的：<c>docs/v25/SHELL-ARCHITECTURE.md</c> §18.2 的 A–P 十六条
/// 在工程师交付之前就写死在文档里，本文件只是把它们逐条执行；跑完只回填「实测」列，不改判据。
/// 这样能避免 S2 那轮的毛病 —— 先读实现再写断言，判据被实现「带着走」。
///
/// 三组挂载点（与主理人约定：本文件归 QA，<c>PageCatalog.cs</c> 归工程师，互不改对方文件）：
/// <list type="bullet">
///   <item><see cref="RunLogicChecks"/> —— 默认组：<c>PageId</c> 十项、三层栈（自建 QaPage）、
///         I6 三态映射、唯一判定点、浮层接线、收尾不弹窗、关于页不再 new 旧窗口；</item>
///   <item><see cref="RunUiChecks"/> —— <c>--ui</c>：三页工厂实例化、内容等价（M）、浮层不重入（F）、
///         订阅不累积（K）、旧窗口未破坏（P）；</item>
///   <item><see cref="RunLiveChecksAsync"/> —— <c>--embedded</c>：**T43 真端到端**（I）与返回新鲜度（N），
///         真内置服务器 + 真页面栈 + 真 <c>leaveroom</c>。</item>
/// </list>
/// </summary>
public static class S3QaGuard
{
    // ==================================================================
    //  默认组
    // ==================================================================

    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.6.0 S3：QA 独立验收（关于 / 更新日志 / 保留房间 · T43） ════════");

        CheckPageIdContract();          // B
        CheckThreeLayerStack();         // C（纯 QaPage，不依赖产品页）
        CheckKeepRoomThreeStates();     // D
        CheckCloseRoomIsExplicitOnly(); // E
        CheckKeepRoomOverlayWiring();   // F（源码侧）
        CheckSingleDecisionPoint();     // G
        CheckShutdownNeverPrompts();    // H
        CheckAboutPushesChangelog();    // J
    }

    /// <summary>B：<c>PageId</c> 十项齐备且顺序稳定（顺序是 S1 定下的契约，S3 只应在末尾追加）。</summary>
    private static void CheckPageIdContract()
    {
        var expected = new[]
        {
            "Login", "RoomList", "Room", "CreateRoom", "HostSettings",
            "Settings", "InviteCode", "KeepRoom", "About", "Changelog"
        };

        var actual = Enum.GetNames(typeof(PageId));
        Check.SequenceEqual(expected, actual,
            "S3-QA（B）：PageId 十项且顺序与架构 §9.1 一致（S3 只在末尾追加三项）");
        Check.Equal(10, actual.Length, "S3-QA（B）：PageId 恰好十项（多一项说明加了枚举却没加页面）");
    }

    /// <summary>
    /// C：**三层栈** 房间 → 关于 → 更新日志 → 返回 → 返回。
    ///
    /// 用自建 <see cref="QaPage"/> 压栈而不是产品页，理由有两个：
    /// ① 能**精确数** <c>OnEnter</c> / <c>OnLeave</c> 次数（产品页不暴露这两个计数）；
    /// ② 产品页的真实压栈另有 <see cref="RunUiChecks"/> 里的实测，两边互补，不互相顶替。
    /// 关键判据不是「深度对」，而是**返回时下层页必须再收一次 <c>OnEnter</c>、且只收一次**
    /// （收两次 = 轮询/订阅翻倍；收不到 = 页面看起来回来其实是死的）。
    /// </summary>
    private static void CheckThreeLayerStack()
    {
        var factory = new QaPageFactory();
        var nav = new NavigationService(factory);

        nav.Reset(new PageRequest(PageId.Room));
        var room = factory.Last;

        nav.Push(new PageRequest(PageId.About));
        var about = factory.Last;

        nav.Push(new PageRequest(PageId.Changelog));
        var log = factory.Last;

        Check.Equal(3, nav.Depth, "S3-QA（C）：房间 → 关于 → 更新日志 得三层栈（深度 3）");
        Check.Equal(1, room.Leaves, "S3-QA（C）：换页式入栈让房间页退场一次");

        var back1 = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back1, "S3-QA（C）：第一次返回成功（更新日志 → 关于）");
        Check.Equal(2, nav.Depth, "S3-QA（C）：深度 3 → 2");
        Check.Equal(PageId.About, nav.Current?.Id, "S3-QA（C）：回到关于页");
        Check.Equal(2, about.Enters,
            "S3-QA（C）：关于页**重新进场**且只进一次（Enters 2 = 首次 + 返回；3 就说明重复进场）");

        var back2 = nav.BackAsync().GetAwaiter().GetResult();
        Check.True(back2, "S3-QA（C）：第二次返回成功（关于 → 房间）");
        Check.Equal(1, nav.Depth, "S3-QA（C）：深度 2 → 1");
        Check.Equal(PageId.Room, nav.Current?.Id, "S3-QA（C）：回到房间页");
        Check.Equal(2, room.Enters, "S3-QA（C）：房间页重新进场且只进一次");
        Check.Equal(1, log.Leaves, "S3-QA（C）：更新日志页退场一次");
    }

    /// <summary>
    /// D：**I6 核心**。三态映射只能有一个答案：<c>null</c>（用户没显式选）= **保留**。
    ///
    /// 只验函数还不够 —— 页面化最容易出的错是「函数对，但调用方自己又翻译了一遍」，
    /// 所以这里同时断言 <c>RoomPage.ResolveKeepRoomAsync</c> 的**源码形态**：
    /// 必须把结论交给 <c>KeepRoomDecision.Resolve</c>，不许写成 <c>?? false</c> / <c>== true</c>。
    ///
    /// v2.6.0 S8：返回类型从 <c>bool?</c> 放宽成 <c>bool?</c>（语义不变），
    /// 但多了一个「取消」出口 —— 取消<b>不属于</b>「保留 / 关闭」这个二选一，
    /// 所以它单独返回 <c>null</c>，绝不许塞进 <c>Resolve</c>（那会退化成「不保留」而把房间关掉）。
    ///
    /// ⚠ 这条是 S3 开工前就提醒过工程师的最高危点：<c>LeaveRoomAsync</c> 里原本有
    /// <c>ConfirmKeepRoom?.Invoke() ?? false</c>，异步化后若照抄，按 Esc 就等于「关闭房间」。
    /// </summary>
    private static void CheckKeepRoomThreeStates()
    {
        Check.True(KeepRoomDecision.Resolve(null), "S3-QA（D）：<null> ⇒ 保留（Esc / 返回 / × 都不能关房）");
        Check.True(KeepRoomDecision.Resolve(true), "S3-QA（D）：<true> ⇒ 保留");
        Check.False(KeepRoomDecision.Resolve(false), "S3-QA（D）：<false> ⇒ 关闭房间（只有显式点按钮才到这）");

        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（D）：能定位仓库根目录"); return; }

        var roomSrc = StripLineComments(
            File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "RoomPage.xaml.cs")));
        var body = MethodBody(roomSrc, "private async Task<bool?> ResolveKeepRoomAsync(");

        Check.True(body.Length > 0, "S3-QA（D）：能取到 ResolveKeepRoomAsync 的方法体");
        Check.True(body.Contains("KeepRoomDecision.Resolve(", StringComparison.Ordinal),
            "S3-QA（D）：三态翻译交给 KeepRoomDecision.Resolve（唯一判定点）");
        Check.False(body.Contains("?? false", StringComparison.Ordinal),
            "S3-QA（D）：没有 `?? false` —— 否则「没选」会被翻成「关闭房间」，I6 打穿");
        Check.False(body.Contains("== true", StringComparison.Ordinal),
            "S3-QA（D）：没有 `== true` —— 那会把「没选」翻成「关闭房间」");

        // S8：取消必须先被挡在外面，不能流进 Resolve（Resolve 会把非 true 全部翻成 false = 关闭房间）。
        Check.True(body.Contains("if (choice == KeepRoomChoice.Cancel) return null;", StringComparison.Ordinal),
            "S8-QA（D）：「取消」先返回 null，不进 Resolve（否则会被翻成「关闭房间」）");
    }

    /// <summary>
    /// E：<c>false</c>（关闭房间）只允许有一个来源 —— 「关闭房间」按钮。
    /// 数的是**剥注释后**的调用点，避免被文档注释里的示例文字骗到。
    /// </summary>
    private static void CheckCloseRoomIsExplicitOnly()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（E）：能定位仓库根目录"); return; }

        var pageSrc = StripLineComments(
            File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "KeepRoomPage.xaml.cs")));

        Check.Equal(1, CountOf(pageSrc, "Finish(false)"),
            "S3-QA（E）：「关闭房间」是**唯一**产出 false 的出口（恰好 1 处）");
        Check.Equal(2, CountOf(pageSrc, "Finish(true)"),
            "S3-QA（E）：「保留房间」按钮与导航栏返回都产出 true（恰好 2 处）");
        Check.True(pageSrc.Contains("private void CloseRoom_OnClick", StringComparison.Ordinal),
            "S3-QA（E）：产出 false 的确实是「关闭房间」按钮的处理器");
    }

    /// <summary>
    /// F（源码侧）：保留房间页必须走**浮层**通道。
    ///
    /// 这不是风格选择：<c>LeaveRoomAsync</c> 是「停心跳 → await 用户确认 → await leaveroom」的长事务，
    /// 中途若把房间页 <c>OnLeave</c> 掉，它会 <c>Detach()</c> + <c>_vm = null</c>，
    /// <c>RoomLeft</c> 订阅被摘 → 房间没退、心跳已停的僵尸态（S3 开工前的第 3 条高危口径）。
    /// </summary>
    private static void CheckKeepRoomOverlayWiring()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（F）：能定位仓库根目录"); return; }

        var navSrc = File.ReadAllText(Path.Combine(root, "ClientGUI", "Services", "NavigationService.cs"));
        var body = MethodBody(navSrc, "public Task<KeepRoomChoice> PushForDecisionAsync(");

        Check.True(body.Length > 0, "S3-QA（F）：能取到 PushForDecisionAsync 的方法体");
        Check.True(body.Contains("PushCore(request, overlay: true)", StringComparison.Ordinal),
            "S3-QA（F）：值类型决策通道确实以 overlay 方式入栈");
        Check.False(body.Contains("Push(request)", StringComparison.Ordinal),
            "S3-QA（F）：没有走换页式 Push（那会把下层房间页 Detach 掉）");
        Check.True(body.Contains("_stack[^1].Pending = source", StringComparison.Ordinal),
            "S3-QA（F）：等待者挂在**刚压进去那一格**上（S2 的 P2 修法不能回退）");

        var pagesDir = Path.Combine(root, "ClientGUI", "Views", "Pages");
        var callers = Directory.EnumerateFiles(pagesDir, "*.cs")
            .Where(f => StripLineComments(File.ReadAllText(f))
                .Contains("PushForDecisionAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Check.Equal(1, callers.Count,
            "S3-QA（F）：产品代码里恰好一个 PushForDecisionAsync 调用方（实际：" +
            string.Join(",", callers) + "）");
        Check.Equal("RoomPage.xaml.cs", callers.FirstOrDefault(),
            "S3-QA（F）：该调用方是房间页的「退出房间」流程");
    }

    /// <summary>
    /// G：**唯一判定点**。全仓（剥注释后）把三态翻译成布尔的落点必须只有**一处**：
    /// <c>RoomPage</c> 里的 <c>KeepRoomDecision.Resolve</c>。
    ///
    /// ⚠ 翻面记录：S3 时这里是"两处"（旧 <c>MainWindow</c> + 新 <c>RoomPage</c>），旧窗口是 S4 才删的。
    /// S4 删掉 `MainWindow` 之后，产品侧只剩页面这一处 —— 判据从 2 收成 1，是**更严**而不是更松。
    /// 另验一条更隐蔽的：房间页必须调**带参**的 <c>LeaveRoomForPageAsync(bool)</c>，
    /// 不能调无参版本 —— 无参版本内部会退回到同步询问（<c>ConfirmKeepRoom</c>），页面化后那条路没人应答。
    /// </summary>
    private static void CheckSingleDecisionPoint()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（G）：能定位仓库根目录"); return; }

        var gui = Path.Combine(root, "ClientGUI");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var code = StripLineComments(File.ReadAllText(file));
            var count = CountOf(code, "KeepRoomDecision.Resolve(");
            if (count > 0) hits.Add($"{Path.GetFileName(file)}×{count}");
        }

        Console.WriteLine("   · KeepRoomDecision.Resolve 的产品调用点：" + string.Join("，", hits));
        Check.Equal(1, hits.Count,
            "S4-QA（G）：保留/关闭的判定只剩一个入口（RoomPage），旧 MainWindow 已删，没有第二套口径");
        Check.Equal("RoomPage.xaml.cs×1", hits.FirstOrDefault(),
            "S4-QA（G）：唯一判定点就是房间页那一处");

        var roomSrc = StripLineComments(
            File.ReadAllText(Path.Combine(gui, "Views", "Pages", "RoomPage.xaml.cs")));
        var vmSrc = StripLineComments(File.ReadAllText(Path.Combine(gui, "ViewModels", "GameViewModel.cs")));

        Check.True(roomSrc.Contains("LeaveRoomForPageAsync(keepRoom.Value)", StringComparison.Ordinal),
            "S3-QA（G）：房间页把**已问过的结论**交给视图模型（带参重载；S8 起是 bool? 解包后再传）");
        // S4：无参版本已随 T47 一起删除，这条从「不许调」转成**防复活**（有人加回来就会红）。
        Check.False(roomSrc.Contains("LeaveRoomForPageAsync()", StringComparison.Ordinal),
            "S4-QA（G）：房间页没有调无参版本（该重载 S4 已删除，本条转为防复活）");

        // 同步询问的判定方法也必须绝迹。⚠ 必须**带左括号**：页面里合法存在
        // `ResolveKeepRoomAsync(`（异步询问），不写括号会把 `ResolveKeepRoomAsync` 也算成命中、恒红。
        Check.Equal(0, CountOf(vmSrc, "ResolveKeepRoom("),
            "S4-QA（G）：同步判定方法 ResolveKeepRoom( 已删除（ResolveKeepRoomAsync 是页面侧的异步询问，合法保留）");

        // S4 收口：同步询问那套东西（`ConfirmKeepRoom` / `ResolveKeepRoom` / `DisconnectCommand`）
        // 必须**整体消失**，而不只是"页面不绑它"。留着就是留给下一个人踩的坑。
        Check.Equal(0, CountOf(roomSrc + vmSrc, "ConfirmKeepRoom"),
            "S4-QA（G）：ConfirmKeepRoom 同步询问入口已整体删除（T47 销号）");
        Check.Equal(0, CountOf(vmSrc, "DisconnectCommand"),
            "S4-QA（G）：DisconnectCommand 已删除（T48 销号）");

        var roomXaml = StripXmlComments(
            File.ReadAllText(Path.Combine(gui, "Views", "Pages", "RoomPage.xaml")));
        Check.Equal(0, CountOf(roomXaml, "DisconnectCommand"),
            "S4-QA（G）：房间页没有绑回 DisconnectCommand（那条同步入口在页面里没人应答，会静默按「关闭房间」处理）");
        Check.True(roomXaml.Contains("Menu_LeaveRoom_OnClick", StringComparison.Ordinal),
            "S4-QA（G）：房间页两个「退出房间」入口都走同一个异步处理器");
    }

    /// <summary>
    /// H：程序收尾**不许**弹询问。这条如果破了，退出时会挂在一个没人能点的页面上等 pending，
    /// 表现是「点了退出程序，进程不消失」—— 用户只会当成卡死。
    /// </summary>
    private static void CheckShutdownNeverPrompts()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（H）：能定位仓库根目录"); return; }

        var vmSrc = StripLineComments(
            File.ReadAllText(Path.Combine(root, "ClientGUI", "ViewModels", "GameViewModel.cs")));

        var shutdown = MethodBody(vmSrc, "public Task LeaveRoomForShutdownAsync(");
        Check.True(shutdown.Length > 0, "S4-QA（H）：能取到 LeaveRoomForShutdownAsync 的方法体（表达式体也算）");
        Check.False(shutdown.Contains("PushForDecisionAsync", StringComparison.Ordinal),
            "S4-QA（H）：收尾路径不 await 页面（否则退出必然悬挂）");

        // ⚠ 翻面记录：S3 时这条断言的是「收尾仍用 `ConfirmKeepRoom = () => false` 抑制询问」。
        // T47 把同步询问入口整体删了，收尾改成把 `keepRoom: false` **直给**唯一实现。
        // 判据的**意图不变**（收尾不许弹询问、按不保留处理），只是载体换了 —— 断言条数 2 → 3。
        Check.True(shutdown.Contains("keepRoom: false", StringComparison.Ordinal),
            "S4-QA（H）：收尾显式按「不保留」处理（keepRoom:false 直给唯一实现，不再靠同步委托抑制）");
        Check.False(shutdown.Contains("ConfirmKeepRoom", StringComparison.Ordinal),
            "S4-QA（H）：收尾路径不再引用同步询问委托（T47 已整体删除）");

        var core = MethodBody(vmSrc, "private async Task LeaveRoomCoreAsync(");
        Check.True(core.Length > 0, "S4-QA（H）：退房实现被抽成 LeaveRoomCoreAsync（唯一实现）");
        Check.False(core.Contains("ConfirmKeepRoom", StringComparison.Ordinal),
            "S4-QA（H）：唯一实现里**不再**读取询问委托（问与退已拆开）");
    }

    /// <summary>J：关于页的「更新日志」必须是换页，不是 <c>new ChangelogWindow()</c>。</summary>
    private static void CheckAboutPushesChangelog()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S3-QA（J）：能定位仓库根目录"); return; }

        var src = StripLineComments(
            File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "AboutPage.xaml.cs")));

        Check.Equal(0, CountOf(src, "new ChangelogWindow("),
            "S3-QA（J）：关于页不再 new 旧更新日志窗口");
        Check.True(src.Contains("PageId.Changelog", StringComparison.Ordinal),
            "S3-QA（J）：「更新日志」按钮 push 到 ChangelogPage");
    }

    // ==================================================================
    //  --ui
    // ==================================================================

    public static void RunUiChecks()
    {
        Console.WriteLine("   —— S3-QA：三页工厂 / 内容等价 / 浮层不重入 / 订阅不累积 ——");

        CheckFactoryCreatesThreePages();    // A
        CheckProductPagesStackAndOverlay(); // C+F 的行为侧
        CheckAboutContentEquivalence();     // M
        CheckChangelogContentEquivalence(); // M
        CheckSubscribeDoesNotAccumulate();  // K
        CheckOldPathStillWorks();           // P
    }

    /// <summary>A：三页真的能被工厂造出来，且自报的 <c>Id</c> 与请求一致（接错枚举是页面化最隐蔽的错）。</summary>
    private static void CheckFactoryCreatesThreePages()
    {
        var factory = new PageFactory(new AppSession());

        foreach (var id in new[] { PageId.KeepRoom, PageId.About, PageId.Changelog })
        {
            try
            {
                var page = factory.Create(new PageRequest(id));
                Check.Equal(id, page.Id, $"S3-QA（A）：工厂创建 PageId.{id} 后自报同一个 Id");
                Check.True(page is FrameworkElement, $"S3-QA（A）：PageId.{id} 是可布局的页面元素");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · 工厂创建 {id} 异常：{ex.Message}");
                Check.True(false, $"S3-QA（A）：工厂能创建 PageId.{id}");
            }
        }
    }

    /// <summary>
    /// C+F 的行为侧：真页面栈上跑一遍「房间 → 关于 → 更新日志 → 返回 → 返回」，
    /// 再用**视图模型实例是否换新**来判定房间页到底有没有退场 / 重新进场。
    ///
    /// 为什么用对象身份而不是计数：产品页不暴露 <c>OnEnter</c> 次数，而
    /// <c>RoomPage.OnEnter</c> 每次都会 <c>new GameViewModel</c>、<c>OnLeave</c> 会把它置 null，
    /// 所以「引用变了」是「真的退场又进场了」的必要条件，「引用没变」是「没退场」的充分证据。
    /// </summary>
    private static void CheckProductPagesStackAndOverlay()
    {
        try
        {
            var session = new AppSession();
            var nav = session.Navigation;

            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.Room));

            var room = nav.Current as RoomPage;
            Check.True(room != null, "S3-QA（C）：栈顶是真实的房间页");
            if (room == null) return;

            var vmInRoom = room.DataContext;

            // ① 换页式：房间 → 关于 → 更新日志。
            //    深度 5 = 登录页 + 房间列表页 + 房间页 + 关于页 + 更新日志页；
            //    「三层」说的是房间以上的那三段，所以这里断言的是**绝对深度 5**，
            //    再靠下面两次返回把 5 → 4 → 3 逐层退回去（只断言增量容易漏掉多余的层）。
            nav.Push(new PageRequest(PageId.About));
            nav.Push(new PageRequest(PageId.Changelog));
            Check.Equal(5, nav.Depth,
                "S3-QA（C）：真页面栈形成「登录/列表/房间/关于/更新日志」五层（房间以上正好三段）");
            Check.Equal(PageId.Changelog, nav.Current?.Id, "S3-QA（C）：当前页是更新日志");

            // ② 逐层返回，回到房间页时视图模型必须**换新**（说明 OnLeave/OnEnter 都真的跑了）。
            Check.True(nav.BackAsync().GetAwaiter().GetResult(), "S3-QA（C）：返回上一层成功");
            Check.Equal(PageId.About, nav.Current?.Id, "S3-QA（C）：先回到关于页");
            Check.Equal(4, nav.Depth, "S3-QA（C）：深度 5 → 4");
            Check.True(nav.BackAsync().GetAwaiter().GetResult(), "S3-QA（C）：再返回一层成功");
            Check.Equal(PageId.Room, nav.Current?.Id, "S3-QA（C）：回到房间页");
            Check.Equal(3, nav.Depth, "S3-QA（C）：深度 4 → 3（回到房间后没有残留层）");
            Check.False(ReferenceEquals(vmInRoom, room.DataContext),
                "S3-QA（C）：房间页**重新进场**（DataContext 换成了新的 GameViewModel，说明退场时真的 Detach 过）");

            // ③ 浮层式：设置盖在房间上，收起后视图模型**不能**换新（换新 = 下层页被误 OnLeave，T41 回归）。
            var vmBeforeOverlay = room.DataContext;
            nav.PushOverlay(new PageRequest(PageId.Settings));
            Check.True(nav.CurrentIsOverlay, "S3-QA（F）：设置以浮层入栈");
            nav.DismissOverlay();
            Check.True(ReferenceEquals(vmBeforeOverlay, room.DataContext),
                "S3-QA（F）：浮层收起后房间页视图模型**没有**重建（下层页全程在场，T41 语义保持）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 真页面栈实测异常：" + ex.Message);
            Check.True(false, "S3-QA（C）：真页面栈可实测");
        }
    }

    /// <summary>
    /// M：**内容等价**。B1 那轮出过「`AboutWindow.xaml` 被掏空但源数据还在」的事故，
    /// 所以这里钉的是「页面形体」而不是数据源。
    ///
    /// ⚠ 翻面记录：S3 时对照物是**旧窗口 XAML**（`Views/AboutWindow.xaml`）；
    /// S4 把它删了，`File.ReadAllText` 会直接抛 `FileNotFoundException`。
    /// 于是把那张 token 表升级成**金标**：这 13 个 token 就是"关于页该有的样子"，
    /// 从旧窗口逐条抄下来固化成常量 —— 被对照的文件没了，判据反而更稳（不再随旧文件漂移）。
    /// 同时加一条：旧文件**必须**已经不在（否则说明 S4 没删干净）。
    /// </summary>
    private static void CheckAboutContentEquivalence()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S4-QA（M）：能定位仓库根目录"); return; }

        var newXaml = File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "AboutPage.xaml"));

        Check.False(File.Exists(Path.Combine(root, "ClientGUI", "Views", "AboutWindow.xaml")),
            "S4-QA（M）：旧 AboutWindow.xaml 已删除（对照物退役，判据改由金标 token 表承担）");

        // 金标 token 表（从旧窗口逐条抄下来）：图标 / 产品名 / 版本 / 框架 / 简介 /
        // 三条信息 / 技术栈 / 免责按钮。
        var tokens = new[]
        {
            "🐺",
            "AppInfo.ProductName",
            "AppInfo.Version",
            "AppInfo.Framework",
            "AppInfo.Description",
            "解决方案", "客户端", "网络加密",
            "AppInfo.SolutionName",
            "AppInfo.TechStack",
            "技术栈",
            "更新日志", "关闭"
        };

        var missingInPage = tokens.Where(t => !newXaml.Contains(t, StringComparison.Ordinal)).ToList();
        Check.Equal(0, missingInPage.Count,
            "S4-QA（M）：关于页保留了全部形体（金标 " + tokens.Length + " 个 token；缺：" +
            string.Join(",", missingInPage) + "）");

        // 运行时再量一次：图标尺寸是旧窗口的硬契约（58×58），技术栈徽章必须真的渲染出来。
        var page = new AboutPage(new AppSession());
        Layout(page, 1020, 720);

        var icon = page.FindName("AppIcon") as FrameworkElement;
        Check.True(icon != null && Math.Abs(icon.Width - 58) < 0.01 && Math.Abs(icon.Height - 58) < 0.01,
            $"S4-QA（M）：🐺 图标仍是 58×58（实际 {icon?.Width}×{icon?.Height}）");

        var stack = page.FindName("TechStackList") as ItemsControl;
        Check.True(stack != null && stack.Items.Count == AppInfo.TechStack.Count && stack.Items.Count > 0,
            $"S4-QA（M）：技术栈徽章全部渲染（{stack?.Items.Count} / {AppInfo.TechStack.Count} 项）");

        var changelogButton = page.FindName("ChangelogButton") as Button;
        var closeAction = page.FindName("CloseAction") as Button;
        Check.True(changelogButton?.Content as string == "更新日志" && closeAction?.Content as string == "关闭",
            "S3-QA（M）：底部两个按钮（更新日志 / 关闭）都在且文案未变");
    }

    /// <summary>
    /// M（更新日志页）：条目数按**真实渲染出来的条目**数，与数据源条数对齐；
    /// 并验「长文本可滚动」是可断言的形态（<c>ScrollableHeight &gt; 0</c>），
    /// 而不是「有个 ScrollViewer 就算过」（内容变短时那种断言永远绿）。
    /// </summary>
    private static void CheckChangelogContentEquivalence()
    {
        try
        {
            var page = new ChangelogPage(new AppSession());
            Layout(page, 1020, 720);

            var list = page.FindName("ChangelogList") as ItemsControl;
            Check.True(list != null && list.Items.Count == Changelog.All.Count,
                $"S3-QA（M）：更新日志渲染全部 {Changelog.All.Count} 条（实际 {list?.Items.Count}）");

            Check.Equal("2.5.0", Changelog.All[0].Version,
                "S3-QA（M）：最新一条在最前（版本倒序，2.5.0 居首）");
            Check.True(Changelog.All[0].IsLatest && !Changelog.All[^1].IsLatest,
                "S3-QA（M）：只有首条带「最新」标记");
            Check.Equal(7, Changelog.All[0].Items.Count,
                "S3-QA（M）：2.5.0 的七条更新要点一条不少");

            var scroller = FindLogical<ScrollViewer>(page);
            Check.True(scroller != null, "S3-QA（M）：更新日志页有滚动容器");
            Check.True(scroller != null && scroller.ScrollableHeight > 0,
                $"S3-QA（M）：长文本真的可滚动（ScrollableHeight={scroller?.ScrollableHeight:F1}px > 0）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 更新日志页实测异常：" + ex.Message);
            Check.True(false, "S3-QA（M）：更新日志页可实测");
        }
    }

    /// <summary>
    /// K：**订阅不累积**。这是本批最该盯的泄漏面 —— 关于 / 更新日志走的是换页式入栈，
    /// 房间页会被反复 <c>OnLeave</c> / <c>OnEnter</c>；只要退订漏一个事件，
    /// 同一条聊天消息就会被处理 N 次、心跳也会变成 N 个。
    ///
    /// 数的是**订阅者总数**（实例事件的调用列表 + 静态事件），进出 5 轮前后必须相等（不是"不涨"就行）。
    /// </summary>
    private static void CheckSubscribeDoesNotAccumulate()
    {
        try
        {
            var session = new AppSession();
            var nav = session.Navigation;

            nav.Reset(new PageRequest(PageId.Login));
            nav.Push(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.Room));

            var room = nav.Current as RoomPage;
            Check.True(room != null, "S3-QA（K）：栈顶是真实的房间页");
            if (room == null) return;

            var client = session.Client;
            var stateBefore = FieldSubscribers(client, "StateReceived");
            var messageBefore = FieldSubscribers(client, "MessageReceived");
            var serverBefore = FieldSubscribers(EmbeddedServerService.Current, "Changed");
            var optionsBefore = StaticSubscribers(typeof(ViewOptions), "Changed");
            Console.WriteLine(
                $"   · 房间页首次进场：StateReceived={stateBefore}，MessageReceived={messageBefore}，" +
                $"ServerChanged={serverBefore}，ViewOptions={optionsBefore}");

            for (var i = 0; i < 5; i++)
            {
                nav.Push(new PageRequest(PageId.About));     // 房间页 OnLeave（必须全部退订）
                nav.Push(new PageRequest(PageId.Changelog)); // 关于页 OnLeave
                nav.BackAsync().GetAwaiter().GetResult();    // 回关于页
                nav.BackAsync().GetAwaiter().GetResult();    // 回房间页（OnEnter 重新订阅一次）
            }

            var stateAfter = FieldSubscribers(client, "StateReceived");
            var messageAfter = FieldSubscribers(client, "MessageReceived");
            var serverAfter = FieldSubscribers(EmbeddedServerService.Current, "Changed");
            var optionsAfter = StaticSubscribers(typeof(ViewOptions), "Changed");
            Console.WriteLine(
                $"   · 进出 5 轮后：StateReceived={stateAfter}，MessageReceived={messageAfter}，" +
                $"ServerChanged={serverAfter}，ViewOptions={optionsAfter}");

            Check.Equal(stateBefore, stateAfter,
                "S3-QA（K）：进出 5 轮后 GameClient.StateReceived 订阅者数不变");
            Check.Equal(messageBefore, messageAfter,
                "S3-QA（K）：进出 5 轮后 GameClient.MessageReceived 订阅者数不变");
            Check.Equal(serverBefore, serverAfter,
                "S3-QA（K）：进出 5 轮后静态 EmbeddedServerService.Changed 订阅者数不变（最易漏的一条）");
            Check.Equal(optionsBefore, optionsAfter,
                "S3-QA（K）：进出 5 轮后静态 ViewOptions.Changed 订阅者数不变");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 订阅累积自检异常：" + ex.Message);
            Check.True(false, "S3-QA（K）：订阅累积可检查");
        }
    }

    /// <summary>
    /// P：**旧路径已彻底退役**（S4 的翻面）。
    ///
    /// ⚠ 翻面记录：S3 时这条断言的是「旧窗口仍在、`MainWindow` 仍走 `KeepRoomWindow`、S4 才删」；
    /// S4 就是删它们的那一批，于是判据反转为：「旧类不存在、没有别处还在 `new` 它们」。
    /// 条数从 3 涨到 4（旧删 N / 新覆盖 M，M ≥ N）。
    /// 仍然保留"可实例化"这一侧的覆盖：十页由 <see cref="CheckFactoryCreatesThreePages"/> 与
    /// `S1QaGuard.CheckTenPagesStillConstructible` 每轮实测，不会因为这里翻面就没人管。
    /// </summary>
    private static void CheckOldPathStillWorks()
    {
        var root = FindRoot();
        if (root == null) { Check.True(false, "S4-QA（P）：能定位仓库根目录"); return; }

        var gui = Path.Combine(root, "ClientGUI");
        var retiredTypes = new[] { "KeepRoomWindow", "MainWindow", "LoginWindow", "RoomListWindow" };

        // ① 这些类型在产品代码里已经**没人 new** 了（旧文件删了但别处若还引用，编译就过不了；
        //    这里防的是"换个文件名把类复活"）。
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var code = StripLineComments(File.ReadAllText(file));
            foreach (var type in retiredTypes)
                if (code.Contains($"new {type}(", StringComparison.Ordinal) ||
                    code.Contains($"new {type} (", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:new {type}");
        }

        Check.Equal(0, offenders.Count,
            "S4-QA（P）：产品代码里不再 new 任何旧窗口类（实际：" + string.Join(",", offenders) + "）");

        // ② 两个关键文件必须真的不在了（`MainWindow.xaml.cs` 是最容易漏删的一个）。
        Check.False(File.Exists(Path.Combine(gui, "MainWindow.xaml.cs")),
            "S4-QA（P）：旧 MainWindow.xaml.cs 已删除");
        Check.False(File.Exists(Path.Combine(gui, "Views", "KeepRoomWindow.xaml.cs")),
            "S4-QA（P）：旧 KeepRoomWindow.xaml.cs 已删除");

        // ③ 房间页的「退出房间」链路仍在（删窗口不能顺手把功能删掉）。
        var roomSrc = StripLineComments(File.ReadAllText(Path.Combine(gui, "Views", "Pages", "RoomPage.xaml.cs")));
        Check.True(roomSrc.Contains("Menu_LeaveRoom_OnClick", StringComparison.Ordinal),
            "S4-QA（P）：房间页「退出房间」处理器仍在（删旧窗口不许把功能一起删掉）");
    }

    // ==================================================================
    //  --embedded：T43 真端到端 + 返回新鲜度
    // ==================================================================

    private const string Tab = "      ";

    /// <summary>
    /// **I（T43）**：从「用户点了退出房间」到「房间在服务端真的没了 / 还在」的端到端链路。
    ///
    /// S1 记的缺口 Q-S1-02 是「模态窗口驱动不了，只能三段推理」；S3 把询问改成页面浮层后
    /// 这条链终于可以**真驱动**：入栈 → 等结果 → 真发 <c>leaveroom</c> → 用 <c>ListRoomsAsync</c>
    /// 从服务端回读房间是否还在。四条出口各跑一遍（对应判据 O 的三个「保留」入口 + 一个「关闭」），
    /// 因为 <c>null</c> 有两条**机制不同**的来路（Esc 走 <c>BackAsync</c> 会被页面拦截；
    /// 外壳 × 走 <c>DismissOverlay</c> 直接弹、不过拦截），只测一条等于没测另一条的语义。
    /// </summary>
    public static async Task RunLiveChecksAsync()
    {
        Console.WriteLine("   —— S3-QA：T43 保留房间真端到端（真内置服务器 + 真页面栈 + 真 leaveroom） ——");

        var service = EmbeddedServerService.Current;
        var wasRunning = service.IsRunning;
        GameClient? peer = null;

        try
        {
            if (!wasRunning)
            {
                service.Port = PickFreeLoopbackPort();
                service.AllowPortDrift = true;
                await service.StartAsync();
            }

            Check.True(service.IsRunning, "S3-QA-T43：真内置服务器在运行（前置条件）");

            // RunOnStaAsync 自己会泵消息直到内部任务完成，因此这里是**同步**取结果（返回 T，不是 Task&lt;T&gt;）。
            var outcome = RunOnStaAsync(async () =>
            {
                var session = new AppSession();
                var host = "qa-s3-" + Guid.NewGuid().ToString("N")[..8];
                session.Login.ServerUrl = service.LocalUrl;

                var loggedIn = await session.Login.LoginWithCredentialsAsync(service.LocalUrl, host, "pw-" + host);
                Check.True(loggedIn, $"S3-QA-T43：房主真登录成功（{host}）");
                if (!loggedIn) return true;

                var client = session.Client;
                var nav = session.Navigation;

                var created = await client.CreateRoomAsync("qa-s3-keep", null);
                Check.True(created.Ok, "S3-QA-T43：建房成功（房主独自在房内）");
                var roomId = created.RoomId;

                nav.Reset(new PageRequest(PageId.Login));
                nav.Push(new PageRequest(PageId.RoomList));
                nav.Push(new PageRequest(PageId.Room));

                // 等待房间状态回填（OnLoaded → RefreshStateAsync 是异步的，不等到就退房会走错分支）。
                var ready = await WaitUntilAsync(
                    () => nav.Current is RoomPage r && r.DataContext is GameViewModel g && g.NeedsKeepRoomDecision,
                    10000);
                Check.True(ready, "S3-QA-T43：房间页拿到状态且「房主独自在房内」（NeedsKeepRoomDecision=true）");

                async Task<bool> RoomStillListedAsync()
                {
                    var list = await client.ListRoomsAsync();
                    return list.Rooms.Any(r => r.Id == roomId);
                }

                // 驱动一次「退出房间」。
                //
                // ⚠ 必须从 <see cref="NavigationService.BackAsync"/> 进，不能直接调
                // <c>room.OnBackRequestedAsync()</c>：那个方法只回答「准不准出栈」，**弹栈由 BackAsync 负责**。
                // 第一版我图省事直接调页面方法，结果三次退房全都没人弹栈（深度一路涨到 3），
                // 报出来的红是「测试写错」而不是产品缺陷 —— 记在这里免得下一轮再犯。
                async Task<bool> LeaveAsync(string label, Func<NavigationService, Task> answer)
                {
                    var room = nav.Current as RoomPage;
                    Check.True(room != null, $"S3-QA-T43（{label}）：退房前栈顶是房间页");
                    if (room == null) return false;

                    var vm = room.DataContext;
                    var backTask = nav.BackAsync();   // 故意不 await：它会停下来等用户回答询问

                    var overlayUp = await WaitUntilAsync(() => nav.CurrentIsOverlay, 5000);
                    Check.True(overlayUp, $"S3-QA-T43（{label}）：询问以**浮层**形式入栈（CurrentIsOverlay=true）");
                    Check.Equal(PageId.KeepRoom, nav.Current?.Id, $"S3-QA-T43（{label}）：浮层就是保留房间页");
                    Check.True(ReferenceEquals(vm, room.DataContext),
                        $"S3-QA-T43（{label}）：浮层期间房间页**未被 OnLeave**（VM 实例不变，T41 语义在真路径上成立）");
                    Check.False(room.IsHitTestVisible, $"S3-QA-T43（{label}）：浮层期间房间页不可点（N8）");

                    await answer(nav);

                    var popped = await backTask;
                    Check.True(popped, $"S3-QA-T43（{label}）：BackAsync 真的把房间页弹掉了（N3）");
                    var home = await WaitUntilAsync(() => nav.Current?.Id == PageId.RoomList, 5000);
                    Check.True(home, $"S3-QA-T43（{label}）：出栈回到房间列表页");
                    Check.Equal(2, nav.Depth, $"S3-QA-T43（{label}）：栈深回到 2（只剩登录页 + 列表页）");
                    return popped;
                }

                async Task ReEnterAsync(string label)
                {
                    var join = await client.JoinRoomAsync(roomId, null);
                    Check.True(join.Ok, $"S3-QA-T43（{label}）：重新进房成功");
                    nav.Push(new PageRequest(PageId.Room));
                    var ok = await WaitUntilAsync(
                        () => nav.Current is RoomPage r && r.DataContext is GameViewModel g && g.NeedsKeepRoomDecision,
                        10000);
                    Check.True(ok, $"S3-QA-T43（{label}）：再次满足「房主独自在房内」");
                }

                // ① 导航栏「保留房间」按钮 → Complete(true) → 保留
                await LeaveAsync("保留按钮", n =>
                {
                    Check.True(n.Complete(true), "S3-QA-T43（保留按钮）：Complete(true) 找到了等待者并出栈");
                    return Task.CompletedTask;
                });
                Check.True(await RoomStillListedAsync(),
                    "S3-QA-T43（保留按钮）：房间**仍在**服务端房间列表里（keepRoom=true 真的发到了服务端）");

                // ② Esc → BackAsync → 页面放行 → pending 结算 null → Resolve(null) = 保留
                await ReEnterAsync("Esc");
                await LeaveAsync("Esc", n => n.BackAsync());
                Check.True(await RoomStillListedAsync(),
                    "S3-QA-T43（Esc）：按 Esc 也是**保留**（null → Resolve → true，I6 的底线）");

                // ③ 外壳 × → DismissOverlay → 直接弹（不经页面拦截）→ null → 保留
                await ReEnterAsync("外壳×");
                await LeaveAsync("外壳×", n =>
                {
                    Check.True(n.DismissOverlay(), "S3-QA-T43（外壳×）：DismissOverlay 认出了栈顶浮层");
                    return Task.CompletedTask;
                });
                Check.True(await RoomStillListedAsync(),
                    "S3-QA-T43（外壳×）：点 × 也是**保留**（这条走的是 DismissOverlay，与 Esc 机制不同）");

                // ④ N：返回新鲜度 —— 再拉一个玩家进房，去关于 / 更新日志转一圈再回来，数据必须一模一样。
                //    先把自己重新叫进房间：场景③结束时房主已经在房间列表页了（不是 bug，是退房的结果）。
                var rejoin = await client.JoinRoomAsync(roomId, null);
                Check.True(rejoin.Ok, "S3-QA-T43（N）：房主重新进房成功");
                nav.Push(new PageRequest(PageId.Room));
                var hostIn = await WaitUntilAsync(
                    () => nav.Current is RoomPage r && r.DataContext is GameViewModel g && g.Players.Count >= 1,
                    10000);
                Check.True(hostIn, "S3-QA-T43（N）：房主重新进入房间页且看得到自己");

                peer = new GameClient();
                await peer.ConnectAsync(service.LocalUrl);
                var peerUser = "qa-s3-peer-" + Guid.NewGuid().ToString("N")[..6];
                // 密码要过服务端的长度规则（「pw」两字符会被拒），用与房主同款的 pw-前缀。
                var peerLogin = await peer.LoginAsync(peerUser, "pw-" + peerUser);
                Check.True(peerLogin.ok, $"S3-QA-T43（N）：第二名玩家登录成功（{peerUser} / {peerLogin.msg}）");
                var peerJoin = await peer.JoinRoomAsync(roomId, null);
                Check.True(peerJoin.Ok, "S3-QA-T43（N）：第二名玩家进房成功");

                var twoPlayers = await WaitUntilAsync(
                    () => nav.Current is RoomPage r && r.DataContext is GameViewModel g && g.Players.Count >= 2,
                    10000);
                Check.True(twoPlayers, "S3-QA-T43（N）：房间页看到两名玩家（前置）");

                var roomPage = nav.Current as RoomPage;
                var vmBefore = roomPage?.DataContext as GameViewModel;
                var snapshot = vmBefore?.Players
                    .Select(p => $"{p.Id}|{p.Name}|{p.IsHost}|{p.IsYou}|{p.Title}")
                    .ToList() ?? new List<string>();
                var statusBefore = vmBefore?.RoomHeaderText ?? "";

                nav.Push(new PageRequest(PageId.About));
                nav.Push(new PageRequest(PageId.Changelog));
                Check.Equal(5, nav.Depth,
                    "S3-QA-T43（N）：登录 / 列表 / 房间 / 关于 / 更新日志 五层，没有多余层");
                Check.True(nav.BackAsync().GetAwaiter().GetResult(), "S3-QA-T43（N）：从更新日志返回关于");
                Check.True(nav.BackAsync().GetAwaiter().GetResult(), "S3-QA-T43（N）：从关于返回房间");

                var restored = await WaitUntilAsync(
                    () => nav.Current is RoomPage && (nav.Current as RoomPage)!.DataContext is GameViewModel,
                    5000);
                Check.True(restored, "S3-QA-T43（N）：回到房间页并重新装配视图模型");

                var vmAfter = (nav.Current as RoomPage)?.DataContext as GameViewModel;
                var refilled = await WaitUntilAsync(
                    () => vmAfter != null && vmAfter.Players.Count == snapshot.Count, 10000);

                // 互斥证据：①VM 换了（真的退场又进场）②名单逐项相同 ③状态文本相同。
                Check.False(ReferenceEquals(vmBefore, vmAfter),
                    "S3-QA-T43（N）：回来后视图模型是**新对象**（说明确实走了 OnLeave → OnEnter，不是靠旧对象苟着）");
                Check.True(refilled, "S3-QA-T43（N）：玩家名单在超时内回填完成");
                Check.SequenceEqual(snapshot,
                    vmAfter?.Players.Select(p => $"{p.Id}|{p.Name}|{p.IsHost}|{p.IsYou}|{p.Title}").ToList()
                    ?? new List<string>(),
                    "S3-QA-T43（N）：返回后玩家名单逐项与离开前一致（Id/昵称/房主/本人/称号）");
                Check.Equal(statusBefore, vmAfter?.RoomHeaderText ?? "",
                    "S3-QA-T43（N）：返回后房间头文本与离开前一致（数据来自 roomstate 回填，不是碰巧没变）");

                Console.WriteLine($"{Tab}· N 实测：离开前 {snapshot.Count} 人 / 回来后 {vmAfter?.Players.Count} 人；" +
                                  $"VM 重建={!ReferenceEquals(vmBefore, vmAfter)}；房间头「{statusBefore}」");

                // ⑤ 「关闭房间」按钮 → 唯一产出 false 的出口。
                //    ⚠ 必须让队友**真的退房**（LeaveRoomAsync），只断开连接不够：
                //    断线玩家仍留在 Players 里（界面显示「⚠ 掉线」），NeedsKeepRoomDecision 会一直是 false，
                //    那样流程根本不弹询问，测出来的红是假红（第一版就踩了这个）。
                var peerLeave = await peer.LeaveRoomAsync();
                Check.True(peerLeave.ok, "S3-QA-T43（关闭房间）：第二名玩家已退房（前置）");
                await peer.DisposeAsync();
                peer = null;
                var alone = await WaitUntilAsync(
                    () => nav.Current is RoomPage r && r.DataContext is GameViewModel g && g.NeedsKeepRoomDecision,
                    10000);
                Check.True(alone, "S3-QA-T43（关闭房间）：第二名玩家离开后重新满足「房主独自在房内」");

                await LeaveAsync("关闭房间", n =>
                {
                    Check.True(n.Complete(false), "S3-QA-T43（关闭房间）：Complete(false) 找到了等待者并出栈");
                    return Task.CompletedTask;
                });
                Check.False(await RoomStillListedAsync(),
                    "S3-QA-T43（关闭房间）：房间**已从**服务端房间列表消失（只有这条出口能关房）");

                return true;
            });

            if (!outcome) Check.True(false, "S3-QA-T43：真端到端链路跑完");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · T43 真端到端异常：" + ex);
            Check.True(false, "S3-QA-T43：真端到端链路无异常");
        }
        finally
        {
            try { if (peer != null) await peer.DisposeAsync(); } catch { /* 清理失败不影响结论 */ }
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

    // ==================================================================
    //  自用工具（与 PageCatalog / S1QaGuard / S2QaGuard 各写各的，不共享）
    // ==================================================================

    /// <summary>没有 <c>OnEnter</c> 计数的页面替身：只用来精确数进场 / 退场次数。</summary>
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

    /// <summary>
    /// 在**带消息泵的 STA 线程**上跑一段异步代码。
    ///
    /// 为什么不能像别处那样 <c>thread.Join()</c> 干等：这批断言里有真实的 <c>await</c>
    /// （登录 / 建房 / 等浮层 / 等状态回填），续体必须回到同一个 STA 线程上执行 WPF 操作。
    /// 所以这里：① 装 <see cref="DispatcherSynchronizationContext"/>；
    /// ② 用 <see cref="DispatcherFrame"/> 泵消息直到任务完成；③ 超时抛 <see cref="TimeoutException"/>
    /// （宁可红一条，也不要把整个测试宿主挂死）。
    /// </summary>
    private static T RunOnStaAsync<T>(Func<Task<T>> body, int timeoutMs = 180000)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var task = body();
                var frame = new DispatcherFrame();

                var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(timeoutMs)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();

                task.ContinueWith(
                    _ =>
                    {
                        timer.Stop();
                        frame.Continue = false;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.FromCurrentSynchronizationContext());

                Dispatcher.PushFrame(frame);

                if (!task.IsCompleted)
                    throw new TimeoutException($"STA 线程内 {timeoutMs}ms 未完成（多半是某个 await 挂了）");

                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            throw new InvalidOperationException("STA(dispatcher) 线程内失败：" + failure.Message, failure);

        return result;
    }

    /// <summary>轮询等待一个条件成立；返回 false = 超时（调用方据此报红，而不是抛异常）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20).ConfigureAwait(true);
        }

        return condition();
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static T? FindLogical<T>(DependencyObject treeRoot) where T : class
    {
        if (treeRoot is T match) return match;

        foreach (var child in LogicalTreeHelper.GetChildren(treeRoot))
        {
            if (child is not DependencyObject node) continue;
            var found = FindLogical<T>(node);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>数一个**实例**事件背后的订阅者。</summary>
    private static int FieldSubscribers(object target, string eventName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var field = target.GetType().GetField(eventName, flags);
        return (field?.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    /// <summary>数一个**静态**事件背后的订阅者。</summary>
    private static int StaticSubscribers(Type type, string eventName)
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

    /// <summary>剥掉 <c>//</c> 行注释（含 <c>///</c>）：源码级守卫必须先剥，否则注释里的示例会被当成调用。</summary>
    private static string StripLineComments(string text)
        => Regex.Replace(text, @"//[^\n]*", "");

    /// <summary>剥掉 XML 注释：XAML 里「说明自己不再绑某命令」的那句话不能被当成真的绑定了。</summary>
    private static string StripXmlComments(string text)
        => Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>
    /// 取一个方法的**完整方法体**（按大括号配平，直到括号清零）。
    ///
    /// 为什么不用「找到第一个 <c>\n        }</c>」那种土办法：B3 那轮踩过 —— 方法里有空行 /
    /// 内部类型时会被提前截断，截出来的"方法体"其实只剩半截，断言就变成假绿。
    /// 这里还必须传**完整签名**（含参数列表与结尾括号），否则会先匹配到 switch 分支里的调用行。
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";

        var open = source.IndexOf('{', start);
        var semi = source.IndexOf(';', start);

        // 表达式体方法（`public Task X() => Y(false);`）根本没有大括号：
        // 若分号先于左大括号出现，就取到分号为止 —— 否则会一路吞到下一个方法的 `{`，
        // 截出来的是**别人的方法体**，断言就会对着错误的代码做判断（B3 那轮踩过同类）。
        if (semi >= 0 && (open < 0 || semi < open)) return source[start..(semi + 1)];

        if (open < 0) return "";

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

        return "";
    }

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
