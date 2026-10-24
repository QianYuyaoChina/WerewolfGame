using System.Windows;
using System.Windows.Input;
using ClientGUI.Views.Pages;

namespace ClientGUI.Services;

/// <summary>
/// 页面标识（架构 D3）。**十个页面一次性列全**：枚举稳定后，
/// <see cref="PageFactory"/> 与本表的 diff 才是唯一的变化点，
/// 避免每个批次都去动同一行 switch。
/// </summary>
public enum PageId
{
    /// <summary>登录页（根页，不能再返回）。</summary>
    Login,

    /// <summary>房间列表页。</summary>
    RoomList,

    /// <summary>房间页（菜单栏 + 三栏 + 底部状态条）。</summary>
    Room,

    /// <summary>创建房间（S2：浮层式入栈，带结果返回）。</summary>
    CreateRoom,

    /// <summary>房主设置（S2：浮层式入栈）。</summary>
    HostSettings,

    /// <summary>设置（S2：浮层式入栈，任意页面都能打开）。</summary>
    Settings,

    /// <summary>邀请码（S2：浮层式入栈，参数是要展示的码）。</summary>
    InviteCode,

    /// <summary>保留房间询问（S3）。</summary>
    KeepRoom,

    /// <summary>关于（S3）。</summary>
    About,

    /// <summary>更新日志（S3）。</summary>
    Changelog
}

/// <summary>一次页面请求：目标页 + 可选参数（例如要展示的邀请码文本）。</summary>
public sealed record PageRequest(PageId Id, object? Argument = null);

/// <summary>
/// 页面契约（架构 D3）。页面是 <c>UserControl</c>，不进 <c>Window</c> 栈，
/// 因此它必须自己交代「怎么进场 / 怎么退场 / 能不能返回 / 返回时要不要拦一下」。
/// </summary>
public interface IPage
{
    /// <summary>页面标识（导航栈据此做重复入栈保护）。</summary>
    PageId Id { get; }

    /// <summary>导航栏标题（外壳窗口标题也会拼上它）。</summary>
    string Title { get; }

    /// <summary>false → 导航栏不画返回键，按 Esc 也无效（根页）。</summary>
    bool CanGoBack { get; }

    /// <summary>进场：开始轮询 / 订阅事件 / 拉一次数据。</summary>
    void OnEnter();

    /// <summary>退场：停止轮询 / **退订事件**（页面化后页面会反复进出，漏退订就会累积订阅）。</summary>
    void OnLeave();

    /// <summary>
    /// 返回拦截：<c>false</c> = 取消返回（例如「退出房间」失败，或用户在询问里选了留下）。
    /// 返回键 / Esc / 导航栏返回 / 外壳关闭都走这一个入口（N9：单一入口）。
    /// </summary>
    Task<bool> OnBackRequestedAsync();
}

/// <summary>页面工厂：唯一认识全部页面的地方（便于测试替换成假页面）。</summary>
public interface IPageFactory
{
    /// <summary>按请求创建页面实例（参数从 <see cref="PageRequest.Argument"/> 取）。</summary>
    IPage Create(PageRequest request);
}

/// <summary>
/// 「页面标题会在页面存活期间变化」的可选契约（v2.6.0 S4，待办 T44）。
///
/// 为什么需要它：<see cref="IPage.Title"/> 只在**换页那一刻**被外壳读一次，
/// 此后页面自己改了也没人通知外壳。房主设置页就踩到了这件事 ——
/// 旧窗口的标题是「房主设置 · 🐺 狼人杀」（房间设置拿到后才知道游戏名），
/// 页面化后标题退化成「狼人杀 · 房主设置」，游戏名只能挪到导航栏右槽。
///
/// 这里不动 <see cref="IPage"/>（十个页面都实现它、没有必要全部背一个事件），
/// 而是让"确实需要改标题"的页面额外实现本接口；外壳只在页面实现了它时订阅/退订。
/// </summary>
public interface IPageTitleSource
{
    /// <summary>标题变了（外壳收到后会重算窗口标题）。</summary>
    event Action? TitleChanged;
}

/// <summary>按键归一后的导航动作。</summary>
public enum NavAction
{
    /// <summary>不做任何导航。</summary>
    None,

    /// <summary>返回上一页。</summary>
    Back
}

/// <summary>
/// 返回键位映射（架构 N9）：<c>Alt + ←</c> 与 <c>Esc</c> 走**同一个**返回入口。
/// 纯函数，不碰任何界面对象，因此可以被自动化测试直接断言。
/// </summary>
public static class NavKeyMap
{
    /// <summary>
    /// 判定一次按键是否应触发返回。
    /// 栈底（<paramref name="canGoBack"/> = false）时一律返回 <see cref="NavAction.None"/>，
    /// 这样 Esc 在登录页上不会莫名其妙地做点什么。
    /// </summary>
    public static NavAction Resolve(Key key, ModifierKeys modifiers, bool canGoBack)
    {
        if (!canGoBack) return NavAction.None;

        if (key == Key.Left && modifiers == ModifierKeys.Alt) return NavAction.Back;
        if (key == Key.Escape && modifiers == ModifierKeys.None) return NavAction.Back;

        return NavAction.None;
    }
}

/// <summary>
/// 页面栈（架构 D3 / D4）。**唯一**的导航入口：入栈、出栈、外壳关闭三条路径都收在这里，
/// 因此「谁在什么时候调了 <c>OnEnter</c> / <c>OnLeave</c>」只有一处需要维护。
///
/// 两种入栈方式，语义**刻意不同**（v2.6.0 S2 补齐第二种）：
/// <list type="bullet">
///   <item><see cref="Push"/>（**换页**）：旧栈顶 <c>OnLeave</c> → 新页 <c>OnEnter</c>。
///         用于「列表 → 房间」这种真的换了一个地方；旧页的轮询与订阅必须停掉。</item>
///   <item><see cref="PushOverlay"/>（**浮层**，S2 起用于设置 / 房主设置 / 邀请码 / 创建房间）：
///         下层页**不调 <c>OnLeave</c>**（继续订阅、继续轮询），只把它的
///         <c>IsHitTestVisible</c> 置 false（N8：模态感）；出栈时**也不调下层 <c>OnEnter</c>**
///         —— 若调了，订阅会按浮层进出的次数翻倍。</item>
/// </list>
/// 为什么必须分开：从房间页打开设置时，如果走 <see cref="Push"/>，
/// <c>RoomPage.OnLeave</c> 会把 <c>GameViewModel.Detach()</c> 掉、还会注销
/// <c>AppShutdown</c> 的「退出房间」委托 —— 于是关掉设置前退出程序，第 ① 步直接空转
/// （待办 T41 的记录）。
/// </summary>
public sealed class NavigationService
{
    /// <summary>
    /// 栈上的一格：页面 + 它是不是「浮层」（决定出栈时要不要给下层调 <c>OnEnter</c>）
    /// + **这一格自己的「等待结果」**（<see cref="PushForResultAsync{T}"/>，S2-QA 复核后从导航服务挪到这里）。
    ///
    /// 为什么等待者必须挂在格子上、不能挂在导航服务上（P2 的根因）：
    /// 挂着只有一个字段时，任何一次出栈都会把它结算成「取消」——
    /// 于是「创建房间浮层开着 → Ctrl+, 打开设置 → 收起设置」就会把创建房间的等待判成取消，
    /// 用户再点「创建」时 <see cref="NavigationService.Complete"/> 返回 false，房间静默不建。
    /// 挂到格子上之后，「弹出哪一格就只结算哪一格」，上层的浮层来去自由。
    /// </summary>
    private sealed class Entry
    {
        public Entry(IPage page, bool overlay)
        {
            Page = page;
            Overlay = overlay;
        }

        public IPage Page { get; }

        public bool Overlay { get; }

        /// <summary>这一格在等的结果（只有 <see cref="PushForResultAsync{T}"/> 压进来的格子才有）。</summary>
        public TaskCompletionSource<object?>? Pending { get; set; }
    }

    private readonly IPageFactory _factory;
    private readonly List<Entry> _stack = new();

    public NavigationService(IPageFactory factory) => _factory = factory;

    /// <summary>栈顶页面（空栈时为 null）。</summary>
    public IPage? Current => _stack.Count == 0 ? null : _stack[^1].Page;

    /// <summary>栈深（供断言：页面切换是否真的发生了；浮层也算一层）。</summary>
    public int Depth => _stack.Count;

    /// <summary>栈顶是不是浮层（浮层出栈时不会重入下层页）。</summary>
    public bool CurrentIsOverlay => _stack.Count > 0 && _stack[^1].Overlay;

    /// <summary>能否返回：栈深 &gt; 1 且栈顶页面允许返回。</summary>
    public bool CanGoBack => _stack.Count > 1 && Current!.CanGoBack;

    /// <summary>页面变化通知（外壳据此换 <c>ContentControl.Content</c> 与窗口标题）。</summary>
    public event Action<IPage?>? Navigated;

    /// <summary>
    /// 重置栈并设根页（登录成功 / 一键开服成功 / 点「进入房间列表」）。
    /// **清栈**是有意的（N2）：否则「进房间列表」后还能返回到登录页残留。
    /// </summary>
    public void Reset(PageRequest root)
    {
        // 先让所有在场页退场（倒序，最后进的最先出）。
        // 清栈也必须结算每一格的等待者（S2-QA 的 P3-2）：否则「浮层开着时被 Reset」
        // 会让调用方的 await 永久挂住（async void 处理器跟着一起泄漏）。
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            var entry = _stack[i];
            entry.Pending?.TrySetResult(null);
            entry.Pending = null;
            entry.Page.OnLeave();
        }
        _stack.Clear();

        var page = _factory.Create(root);
        _stack.Add(new Entry(page, overlay: false));
        page.OnEnter();
        Navigated?.Invoke(page);
    }

    /// <summary>换页式入栈（旧栈顶会 <c>OnLeave</c>）。</summary>
    public bool Push(PageRequest request) => PushCore(request, overlay: false);

    /// <summary>
    /// 浮层式入栈（下层页**不退场**：订阅与轮询继续，只是不可点）。
    /// 设置 / 房主设置 / 邀请码 / 创建房间都走这条（S2）。
    /// </summary>
    public bool PushOverlay(PageRequest request) => PushCore(request, overlay: true);

    private bool PushCore(PageRequest request, bool overlay)
    {
        var existing = _stack.FindLastIndex(e => e.Page.Id == request.Id);
        if (existing >= 0)
        {
            // 同 PageId 已在栈中：先把它（及其上面的页）弹掉再压新的 —— 等价「复用并刷新」（N5）。
            // 被弹掉的格子若正在等结果，必须**当场**按「取消」结算（S2-QA 的 P3-1）：
            // 连点两次「创建房间」时，第一次的 await 否则会永远不返回。
            for (var i = _stack.Count - 1; i >= existing; i--)
            {
                var gone = _stack[i];
                gone.Pending?.TrySetResult(null);
                gone.Pending = null;
                gone.Page.OnLeave();
                _stack.RemoveAt(i);
            }
        }
        else if (!overlay)
        {
            // 换页：旧栈顶退场（停轮询 / 退订），新页进场。
            Current?.OnLeave();
        }

        // 浮层：让下层页不可点（N8 模态感）。**不调它的 OnLeave** —— 这是 T41 的核心。
        if (overlay && Current is UIElement below) below.IsHitTestVisible = false;

        var page = _factory.Create(request);
        _stack.Add(new Entry(page, overlay));
        page.OnEnter();
        Navigated?.Invoke(page);
        return true;
    }

    /// <summary>
    /// 返回到上一页。栈底返回 false（N7）；栈顶页可以拦下这次返回（N3）。
    /// 浮层与普通页的差别收在 <see cref="PopCore"/> 里，调用方不必关心。
    /// </summary>
    public async Task<bool> BackAsync()
    {
        if (_stack.Count <= 1) return false;

        var top = _stack[^1].Page;
        if (!top.CanGoBack) return false;

        if (!await top.OnBackRequestedAsync().ConfigureAwait(true)) return false;

        PopCore();
        return true;
    }

    /// <summary>
    /// 收起最上面这一层**浮层**（外壳点 × 时用，v2.6.0 S2）。
    ///
    /// 为什么需要它、而不是让外壳直接 <see cref="BackAsync"/>：
    /// <list type="bullet">
    ///   <item>语义不同 —— 点 × 是「关掉最上面这一层」，它<b>不该</b>被页面的返回拦截
    ///         （<see cref="IPage.OnBackRequestedAsync"/>）挡住，也不该在深层页上变成「退出房间」；</item>
    ///   <item>安全 —— 只有栈顶确实是浮层才动手（返回 <c>true</c>）；否则返回 <c>false</c>，
    ///         外壳再去走它那张关闭意图表。这样「×× 把程序退了」这类事故在类型层面就不可能发生。</item>
    /// </list>
    /// </summary>
    public bool DismissOverlay()
    {
        if (!CurrentIsOverlay) return false;
        PopCore();
        return true;
    }

    /// <summary>
    /// **直接**弹到指定页面为止（v2.6.0 S8）。只弹，不逐页问 <see cref="IPage.OnBackRequestedAsync"/>。
    ///
    /// 为什么需要它、而不是连着调 <see cref="BackAsync"/>：
    /// 「退完房回房间列表」这个结论**已经定了**（<c>leaveroom</c> 都发出去了），
    /// 再让房间页的返回拦截判断一次「是不是还在房间里」纯属绕远路 ——
    /// 而那条路上还有「连点去重」<c>_backRequested</c> 之类的瞬时标志，
    /// 一旦时序错开就会把这次出栈吞掉，界面永远停在中途那一页（S8 修的就是它）。
    /// 结论已定时就走这里：一路弹到目标页，中途没有任何东西能说"不"。
    ///
    /// 幂等：栈顶已经是目标页时直接返回 true（多调几次也不会多弹）。
    /// 目标页不在栈里时返回 false，栈保持不动（宁可留在原页，也不要把根页弹掉）。
    /// </summary>
    public bool PopTo(PageId target)
    {
        // 目标页不在栈里 → **一律不动栈**（宁可留在原页，也不要稀里糊涂一路弹到根页）。
        var found = false;
        for (var i = 0; i < _stack.Count; i++)
        {
            if (_stack[i].Page.Id == target) { found = true; break; }
        }
        if (!found) return false;

        // 一路弹到目标页为止；栈底（根页）永不弹出。
        while (_stack.Count > 1 && _stack[^1].Page.Id != target) PopCore();

        return _stack[^1].Page.Id == target;
    }

    /// <summary>
    /// 浮层式入栈并等待一个结果（一次二选一决策，例如「创建房间」或 S3 的「是否保留房间」）。
    /// 页面通过 <see cref="Complete"/> 带结果出栈；直接返回（<see cref="BackAsync"/>）则结果为 <c>null</c>。
    ///
    /// 等待者挂在**刚压进去那一格**上（S2-QA 复核后的修法）：
    /// 这样「上层再叠一层浮层再收起」不会误伤这一层的等待（P2），
    /// 「同 PageId 被复用弹掉」「清栈」也会各自结算自己那一格（P3）。
    /// </summary>
    public Task<T?> PushForResultAsync<T>(PageRequest request) where T : class
    {
        var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先压栈再挂等待者：PushCore 内部可能为了「同 PageId 复用」删掉若干格子，
        // 等它删完再挂，保证挂到的就是本层的格子（而不是被删掉那格）。
        PushCore(request, overlay: true);

        if (_stack.Count > 0)
        {
            _stack[^1].Pending = source;
        }
        else
        {
            source.TrySetResult(null);   // 理论上不会发生（PushCore 一定会压一格）；防御性兜底，不悬挂
        }

        return UnwrapAsync<T>(source.Task);
    }

    /// <summary>
    /// 浮层式入栈并等待一个**决策**（S3 的「是否保留房间」；S8 起扩展成三选一）。
    /// 页面通过 <see cref="Complete"/> 带结果出栈；直接返回（<see cref="BackAsync"/>）则结果为 <c>null</c>。
    /// <para>
    /// 为什么不是 <c>PushForResultAsync&lt;bool&gt;</c>：C# **不允许**仅靠泛型约束区分重载
    /// （两个只差 <c>where T : class</c> / <c>where T : struct</c> 的方法签名相同，会 CS0111），
    /// 而 <c>bool</c> 也不满足现有方法的 <c>where T : class</c>。
    /// 与其削弱现有泛型方法「取消 = <c>null</c>」的契约（那是 <c>CreateRoomResult</c> 等调用方的依赖），
    /// 不如给决策单开一条通道。
    /// </para>
    /// <para>
    /// S8：返回类型从 <c>bool?</c> 换成 <see cref="KeepRoomChoice"/> ——
    /// 「保留 / 关闭 / 取消」三选一里，「取消」在 <c>bool?</c> 上表达不出来
    /// （<c>null</c> 已被 I6 的「没显式选 ⇒ 保留」占用）。
    /// 翻译仍由 <see cref="KeepRoomDecision.ResolveChoice"/> 一处完成，本方法只负责搬运。
    /// </para>
    /// </summary>
    public Task<KeepRoomChoice> PushForDecisionAsync(PageRequest request)
    {
        var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PushCore(request, overlay: true);

        if (_stack.Count > 0) _stack[^1].Pending = source;
        else source.TrySetResult(null);

        return UnwrapChoiceAsync(source.Task);
    }

    /// <summary>页面完成决策：记录结果并出栈。<paramref name="result"/> 为 null 表示「取消」。</summary>
    /// <returns>true = 栈顶确实有一格在等结果且已出栈；false = 栈顶不是在等结果的浮层（栈未动）。</returns>
    public bool Complete(object? result)
    {
        if (_stack.Count == 0) return false;

        var top = _stack[^1];
        var pending = top.Pending;
        if (pending == null) return false;   // 栈顶不是在等结果的浮层 → 不动栈（调用方走自己的兜底）

        // 先摘掉等待者，免得 PopCore 把它当「取消」再补一个 null。
        top.Pending = null;
        PopCore();
        pending.TrySetResult(result);
        return true;
    }

    /// <summary>出栈并成对调用 <c>OnLeave</c> / <c>OnEnter</c>（内部唯一实现）。</summary>
    private void PopCore()
    {
        if (_stack.Count == 0) return;

        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.Page.OnLeave();

        if (top.Overlay)
        {
            // 浮层出栈：下层页一直在场，**不能再给它调一次 OnEnter**
            //（否则它的轮询 / 订阅会按浮层进出次数翻倍）——只把可点性还给它。
            if (Current is UIElement below) below.IsHitTestVisible = true;
        }
        else
        {
            // 换页式出栈：回来的那一页重新进场（重新开始轮询 / 重新订阅）。
            Current?.OnEnter();
        }

        // 只结算**被弹出的这一格**自己的等待者（没人显式给结果 → 视为「取消」）。
        // 上层 / 下层的等待者一律不受影响：这是 P2 的修法关键。
        top.Pending?.TrySetResult(null);
        top.Pending = null;

        Navigated?.Invoke(Current);
    }

    /// <summary>把 <c>object?</c> 的结果拆箱成 <typeparamref name="T"/>（null 保持 null）。</summary>
    private static async Task<T?> UnwrapAsync<T>(Task<object?> source) where T : class
        => await source.ConfigureAwait(false) as T;

    /// <summary>
    /// 把 <c>object?</c> 的结果拆箱成 <see cref="KeepRoomChoice"/>
    /// （翻译只有 <see cref="KeepRoomDecision.ResolveChoice"/> 一处，这里不自己判）。
    /// </summary>
    private static async Task<KeepRoomChoice> UnwrapChoiceAsync(Task<object?> source)
        => KeepRoomDecision.ResolveChoice(await source.ConfigureAwait(false));
}

/// <summary>
/// 真页面工厂：唯一认识「<see cref="PageId"/> → 页面类」映射的地方。
/// S1 迁了登录 / 房间列表 / 房间；S2 补齐创建房间 / 房主设置 / 设置 / 邀请码；
/// S3 补齐关于 / 更新日志 / 保留房间 —— **十个 <see cref="PageId"/> 全部有页面**，
/// 下面的兜底分支从此只为「将来新增枚举值却忘了加页面」而存在（宁可抛，也不要静默返回空页）。
/// </summary>
public sealed class PageFactory : IPageFactory
{
    private readonly AppSession _session;

    public PageFactory(AppSession session) => _session = session;

    public IPage Create(PageRequest request) => request.Id switch
    {
        PageId.Login => new LoginPage(_session),
        PageId.RoomList => new RoomListPage(_session),
        PageId.Room => new RoomPage(_session),
        PageId.CreateRoom => new CreateRoomPage(_session),
        PageId.HostSettings => new HostSettingsPage(_session),
        PageId.Settings => new SettingsPage(_session),
        PageId.InviteCode => new InviteCodePage(_session, request.Argument as string ?? ""),
        PageId.KeepRoom => new KeepRoomPage(_session),
        PageId.About => new AboutPage(_session),
        PageId.Changelog => new ChangelogPage(_session),
        _ => throw new NotSupportedException(
            $"页面 {request.Id} 没有对应的页面类（十个已迁移页面见 SHELL-ARCHITECTURE §9.1）")
    };
}
