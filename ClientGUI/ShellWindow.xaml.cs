using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClientGUI.Services;
using Werewolf.Client.Share;

namespace ClientGUI;

/// <summary>
/// 程序**唯一**的顶层窗口（v2.6.0 S1，架构 D1 / D4）。
///
/// 它只做四件事，别的都不做：
/// <list type="number">
///   <item>装页面：一个 <see cref="System.Windows.Controls.ContentControl"/>，内容由
///         <see cref="NavigationService"/> 换（每页自带导航栏，不抽到外壳里）；</item>
///   <item>管窗口 chrome：标题随页变化、最小尺寸、启动尺寸与几何记忆（架构 §7.2）；</item>
///   <item>返回键位：<c>Alt+←</c> / <c>Esc</c> 一律走 <see cref="NavigationService.BackAsync"/>（N9）；</item>
///   <item><b>关闭意图分派</b>：点 × 到底等于什么，取决于**当前是哪一页**（见 <see cref="Shell_OnClosing"/>）。</item>
/// </list>
///
/// 关闭语义（架构 §5.1 / §5.2，逐条可验收）：
/// <list type="bullet">
///   <item>当前页是 <c>RoomPage</c> → **只退出房间**：拦下这次关闭，走与菜单「退出房间」完全同一条命令，
///         含房主的「是否保留房间」询问 → 回到房间列表；<b>不停服务器、不断连、不退出程序</b>（I1）；</item>
///   <item>当前页是 <c>RoomListPage</c> / <c>LoginPage</c> → **完整收尾**：放行关闭 →
///         <c>OnExit</c> → <see cref="AppShutdown"/> 三步（退房间 3s → 断连 3s → 停服务器 5s，总 8s，幂等）（I2）；</item>
///   <item>菜单「退出程序」/ <c>Ctrl+Q</c> → 无论当前页都完整收尾；</item>
///   <item>收尾进行中再点 × → 幂等放行（不重复退房间 / 断连 / 停服务器）。</item>
/// </list>
/// 这张表压在一个纯函数 <see cref="CloseIntent.Resolve"/> 里，因此可以被断言直接钉死
/// （<see cref="CloseIntentSource"/> 仍保持 4 个值，新维度用 <see cref="ShellCloseIntent"/> 表达）。
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>
    /// 是否由「菜单退出 / Ctrl+Q」发起关闭（区别于点 ×）。
    /// 手法与旧多窗口流程里的 <c>_exitRequested</c> 一致：<c>Application.Shutdown()</c> 会走到
    /// <see cref="Shell_OnClosing"/>，不置位的话房间页那次关闭会被判成「只退出房间」而被拦下，
    /// 于是 <c>Shutdown()</c> 永远关不掉窗口、程序退不出去。
    /// </summary>
    private static bool _menuExitRequested;

    private readonly AppSession _session;

    /// <summary>当前页若实现了 <see cref="IPageTitleSource"/>，这里是它（换页时先退订）。</summary>
    private IPageTitleSource? _titleSource;

    /// <summary>上一次的栈深（用来判断这次切页是「前进」还是「后退」，S7 转场方向）。</summary>
    private int _lastDepth;

    /// <summary>转场进行中的临时容器（装着出去的旧页 + 进来的新页）；null = 当前没有动画在跑。</summary>
    private Grid? _activeLayer;

    /// <summary>转场中「进来的那一页」（动画结束时它成为 <see cref="PageHost"/> 的内容）。</summary>
    private FrameworkElement? _activeIncoming;

    /// <summary>
    /// 转场令牌（v2.6.0 S8）：每开一场新转场（含就地收尾）就 +1。
    /// 上一场动画的 <c>Completed</c> 会在稍后才触发，靠它认出「自己已经过期」，
    /// 不去动后一场转场的状态（连续导航时这是必须的）。
    /// </summary>
    private int _transitionToken;

    /// <summary>设计器 / 冒烟用的无参构造：自带一份全新会话（不会真的连服务器）。</summary>
    public ShellWindow() : this(new AppSession())
    {
    }

    public ShellWindow(AppSession session)
    {
        InitializeComponent();

        _session = session;

        // 会话登记进统一收尾：退出房间 → 断开连接 → 停内置服务器都由 AppShutdown 负责。
        // 放这里比旧流程（RunRoomLoop 里登记）更早也更可靠（架构 §5.3）。
        AppShutdown.AttachSession(_session.Client);

        _session.Navigation.Navigated += OnNavigated;

        Loaded += OnLoaded;
    }

    /// <summary>唯一的连接（设置窗口据此决定「修改密码 / 服务器设置」入口是否出现）。</summary>
    public GameClient Client => _session.Client;

    /// <summary>本次会话（只读）。</summary>
    public AppSession Session => _session;

    /// <summary>
    /// 页面宿主 <c>PageHost</c> 的**真实客户区**尺寸（v2.6.0 S5 / T49）。
    ///
    /// 为什么需要暴露它：页面自身没有 `Width/Height`（由外壳撑满），所以「导航栏 ≥56 / 标题居中 ≤1px /
    /// 主按钮通栏 ≥80%」这三类判据在测试里一直只能按**设计尺寸**或**硬编码的 1020×720** 去量 ——
    /// 前者比外壳还大（房间页 1220×780），后者是窗口尺寸的静态复刻、不是真实客户区
    /// （真实客户区 = 窗口尺寸 − 非工作区边框，且会随用户拖拽 / DPI 变化）。
    /// 暴露这一个只读属性后，测试可以「离屏 `Show()` → 读这里的实测值 → 在该尺寸下复测十页」，
    /// 不再需要猜。**只读、不改任何行为**，也不参与布局计算。
    /// </summary>
    public Size PageHostSize => new(PageHost.ActualWidth, PageHost.ActualHeight);

    // ==================== 生命周期 ====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();

        // 根页恒为登录页（架构 N1：Depth==1、CanGoBack==false）。
        _session.Navigation.Reset(new PageRequest(PageId.Login));
    }

    /// <summary>
    /// 页面变化：换内容 + 换窗口标题（<c>狼人杀 · 页面名</c>）。
    ///
    /// v2.6.0 S7：切页带 **Android 式滑动动画**（架构 D6「不做转场动画」按用户明确要求显式推翻，
    /// 见 SHELL-ARCHITECTURE §26）。关键约束是**静止态不变式**：动画期间两页装进一个临时 Grid，
    /// 动画一结束 <see cref="PageHost"/> 的内容立刻回到「就是那一页」——
    /// 因此「外壳只有一个页面宿主 / PageHost 里装的就是当前页」这些既有断言一条都不用改。
    /// </summary>
    private void OnNavigated(IPage? page)
    {
        // ⚠ **先把上一场转场就地收尾**（v2.6.0 S8 重入修复）。
        // 转场进行中 PageHost.Content 装的是**临时 Grid**（不是页面）。不先收尾就取它当「旧页」，
        // 连续两次导航（「浮层出栈」紧接着「房间页出栈」就是这种）会把整层 Grid 塞进下一个转场层 ——
        // 轻则界面停在中途那一页，重则「指定的元素已经是另一个元素的逻辑子元素」直接抛。
        // 收尾之后 PageHost.Content 必然是「一个真的页面」或 null，下面的分支因此都成立。
        FinishTransition();

        var incoming = page as FrameworkElement;
        var outgoing = PageHost.Content as FrameworkElement;

        var slide = PageTransition.Resolve(
            UiStateStore.State.PageTransitionAnimation,
            hasOutgoing: outgoing != null,
            _session.Navigation.Depth,
            _lastDepth);
        _lastDepth = _session.Navigation.Depth;

        // 标题立即跟着换（动画期间标题已经是新页的，和手机系统一致）。
        AttachTitleSource(page);
        Title = ComposeTitle(page);

        if (slide == PageSlide.None || incoming == null || outgoing == null
            || ReferenceEquals(outgoing, incoming))
        {
            PageHost.Content = incoming;
            return;
        }

        // 转场层：临时 Grid，装着「出去的旧页 + 进来的新页」。动画一结束就拆掉。
        // ⚠ 必须先把旧页从 PageHost 摘下来：一个元素不能同时是两个父的逻辑子元素。
        var layer = new Grid { IsHitTestVisible = false };   // 转场期间不接受点击（280ms）
        PageHost.Content = null;
        layer.Children.Add(outgoing);
        layer.Children.Add(incoming);
        Panel.SetZIndex(slide == PageSlide.Forward ? incoming : outgoing, 1);

        PageHost.Content = layer;
        layer.UpdateLayout();

        var width = layer.ActualWidth > 0 ? layer.ActualWidth : Width;
        var forward = slide == PageSlide.Forward;

        var incomingShift = new TranslateTransform(forward ? width : -width * PageTransition.Parallax, 0);
        var outgoingShift = new TranslateTransform(0, 0);
        incoming.RenderTransform = incomingShift;
        outgoing.RenderTransform = outgoingShift;

        // 前进：旧页视差左移并淡到 0.35；后退：新页从 0.35 淡回，旧页整幅滑出右侧。
        incoming.Opacity = forward ? 1 : 0.35;
        outgoing.Opacity = 1;

        var slideIn = new DoubleAnimation(
            forward ? width : -width * PageTransition.Parallax, 0, PageTransition.Duration)
        {
            EasingFunction = PageTransition.EnterEase
        };
        var slideOut = new DoubleAnimation(0, forward ? -width * PageTransition.Parallax : width, PageTransition.Duration)
        {
            EasingFunction = PageTransition.LeaveEase
        };

        var fadeIn = new DoubleAnimation(incoming.Opacity, 1, PageTransition.Duration)
        {
            EasingFunction = PageTransition.EnterEase
        };
        var fadeOut = new DoubleAnimation(1, forward ? 0.35 : 1, PageTransition.Duration)
        {
            EasingFunction = PageTransition.LeaveEase
        };

        _activeLayer = layer;
        _activeIncoming = incoming;

        // 只挂「进来的那一页」的完成事件：它的位移到 0 时整场转场就结束了。
        // ⚠ 带上本场转场的令牌（S8）：上一场动画的 Completed 仍会在稍后触发，
        // 不带令牌的话它会把**这一场**提前收尾（或者反过来把已收尾的再收一次）。
        var token = _transitionToken;
        slideIn.Completed += (_, _) =>
        {
            if (_transitionToken == token) FinishTransition();
        };

        incomingShift.BeginAnimation(TranslateTransform.XProperty, slideIn);
        outgoingShift.BeginAnimation(TranslateTransform.XProperty, slideOut);
        incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 结束转场（S7）：拆掉临时层，把「进来的那一页」放回 <see cref="PageHost"/>，
    /// 并清掉所有动画残留（不停掉动画的话，被钉住的值会一直压在控件上）。
    /// 幂等：没有在途动画时直接返回。
    /// </summary>
    private void FinishTransition()
    {
        // 令牌先 +1：本场之后触发的 Completed（以及上一场遗留的）一律判为过期。
        _transitionToken++;

        var layer = _activeLayer;
        var settled = _activeIncoming;
        _activeLayer = null;
        _activeIncoming = null;

        if (layer == null) return;

        foreach (FrameworkElement child in layer.Children)
        {
            if (child.RenderTransform is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.XProperty, null);
            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.RenderTransform = null;
            child.Opacity = 1;
        }

        PageHost.Content = settled;   // settled 会因此自动从 layer 里摘出去
        layer.Children.Clear();       // 剩下的（出去的旧页）一并丢弃
    }

    /// <summary>
    /// v2.6.0 S4（待办 T44）：页面标题可能在页面存活期间变化（房主设置页拿到房间所属游戏后
    /// 要把游戏名拼回窗口标题）。只对实现了 <see cref="IPageTitleSource"/> 的页面订阅，
    /// 换页时先退掉上一个 —— 否则页面反复进出会累积订阅。
    /// </summary>
    private void AttachTitleSource(IPage? page)
    {
        if (_titleSource != null)
        {
            _titleSource.TitleChanged -= OnPageTitleChanged;
            _titleSource = null;
        }

        if (page is IPageTitleSource source)
        {
            _titleSource = source;
            _titleSource.TitleChanged += OnPageTitleChanged;
        }
    }

    /// <summary>页面自己改了标题 → 重算窗口标题。</summary>
    private void OnPageTitleChanged() => Title = ComposeTitle(_session.Navigation.Current);

    /// <summary>窗口标题 = <c>狼人杀 · 页面标题</c>（页面没有标题时只写产品名）。</summary>
    private static string ComposeTitle(IPage? page)
        => page == null || string.IsNullOrWhiteSpace(page.Title) ? "狼人杀" : "狼人杀 · " + page.Title;

    // ==================== 窗口几何记忆（架构 §7.2） ====================

    /// <summary>恢复上次的窗口几何（离屏修正：显示器变更后不把窗口放到屏幕外）。</summary>
    private void RestoreWindowState()
    {
        var state = UiStateStore.State;

        var width = state.Width >= MinWidth ? state.Width : Width;
        var height = state.Height >= MinHeight ? state.Height : Height;
        Width = width;
        Height = height;

        if (state.Left is { } left && state.Top is { } top && IsOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (state.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>判断一个矩形是否至少有一部分落在虚拟桌面的可见范围内。</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        const double margin = 40;

        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;

        return left + width > vLeft + margin && left < vRight - margin
            && top + height > vTop + margin && top < vBottom - margin;
    }

    /// <summary>把当前窗口几何写回磁盘。</summary>
    private void SaveWindowState()
    {
        var state = UiStateStore.State;

        state.Maximized = WindowState == WindowState.Maximized;

        // 最大化 / 最小化时 RestoreBounds 才是“正常尺寸”，是恢复时应采用的值。
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (!bounds.IsEmpty && !double.IsNaN(bounds.Left) && bounds.Width > 0 && bounds.Height > 0)
        {
            state.Left = bounds.Left;
            state.Top = bounds.Top;
            state.Width = bounds.Width;
            state.Height = bounds.Height;
        }

        UiStateStore.Save();
    }

    // ==================== 返回键位（N9：单一入口） ====================

    private void Shell_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var action = NavKeyMap.Resolve(e.Key, Keyboard.Modifiers, _session.Navigation.CanGoBack);
        if (action != NavAction.Back) return;

        e.Handled = true;
        _ = _session.Navigation.BackAsync();
    }

    // ==================== 关闭意图分派（架构 §5.2 / §5.6） ====================

    private void Shell_OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();

        // v2.6.0 S2/S6：先问纯函数「这次关闭该做什么」，外壳只负责执行（架构 §5.2）。
        var intent = _menuExitRequested ? ShellCloseIntent.MenuExit : ShellCloseIntent.WindowClose;
        var action = CloseIntent.ResolveShellClose(
            intent, _session.Navigation.Current?.Id, AppShutdown.IsShuttingDown, _session.Navigation.CurrentIsOverlay);

        switch (action)
        {
            case ShellCloseAction.DismissOverlay:
                // × 在浮层上 = 收起这一层（与旧多窗口「关掉那个对话框」一致）。
                // 必须在这里拦下：若放它进下面的完整收尾分支，在房间里 Ctrl+, 打开设置再点 ×
                // 就会把服务器一起关掉。用 DismissOverlay 而不是 BackAsync：× 不该被页面的返回拦截挡住。
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() => _session.Navigation.DismissOverlay()));
                break;

            case ShellCloseAction.CancelAndBack:
                // 房间页：点 × = 「退出房间」（I1）—— 拦下这次关闭，
                // 改走与菜单「退出房间」完全同一条路径（含「是否保留房间」询问），成功后出栈回房间列表。
                e.Cancel = true;

                // 不能在 Closing 里同步等待：退出房间的回调要回 UI 线程（与旧多窗口流程同手法）。
                Dispatcher.BeginInvoke(new Action(() => _ = _session.Navigation.BackAsync()));
                break;

            case ShellCloseAction.NeedConfirm:
                // 房间列表页 / 登录页：真正会停服务器，因此保留一次关窗确认（取消则彻底无损）。
                if (CloseIntent.AfterConfirm(AppShutdown.ConfirmClose(this)) == ShellCloseAction.CancelOnly)
                {
                    e.Cancel = true;

                    // 取消退出 → 清掉「菜单退出」标记。否则用户先试了菜单退出又取消，
                    // 之后每一次 × 都会被误判成「退出程序」而直接走完整收尾（S6 一并修掉）。
                    _menuExitRequested = false;
                    break;
                }

                // v2.6.0 S6（BugFix）：ShutdownMode=OnExplicitShutdown，**窗口关闭本身不会让程序退出**。
                // 只放行关闭而不请求退出应用，进程会留在「无窗口但活着」的状态：
                // OnExit 不触发 → AppShutdown 三步不走 → 内置服务器变成孤儿。
                // 用 BeginInvoke 推迟到本次 Closing 结束，避免在 Closing 里递归触发关闭流程。
                Dispatcher.BeginInvoke(new Action(RequestExit));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 菜单「退出程序」/ <c>Ctrl+Q</c>：先置位「菜单退出」再关外壳。
    ///
    /// **必须先置位**：<c>Shutdown()</c> 会走到 <see cref="Shell_OnClosing"/>，
    /// 而房间页的 × 已经被改成「等同于退出房间」（<c>e.Cancel = true</c>）。
    /// 不置位的话这次关闭会被判成 <see cref="CloseIntentSource.RoomWindow"/> 而被拦下，
    /// 于是 <c>Application.Shutdown()</c> 永远关不掉窗口、程序退不出去。
    ///
    /// v2.6.0 S6：这也是「关外壳走完整收尾」分支放行后要走的**同一个**入口
    /// （见 <see cref="Shell_OnClosing"/> 的 <see cref="ShellCloseAction.AllowAndExitApp"/>）——
    /// 窗口关闭本身不会让程序退出，必须显式到这里来。<see cref="AppShutdown.ExitApplication"/>
    /// 为 false 时（仅测试宿主）只置位不真退。
    /// </summary>
    public static void RequestExit()
    {
        _menuExitRequested = true;
        if (AppShutdown.ExitApplication)
            Application.Current?.Shutdown();
    }
}
