using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 房间页（v2.6.0 S1 由 <c>MainWindow</c> 搬迁而来）：只负责“登录成功之后”的界面。
/// 布局拆成 <see cref="PlayerPanel"/> / <see cref="ChatPanel"/> / <see cref="ActionPanel"/> 三个组件，
/// 登录与连接由 <see cref="AppSession"/> / 登录页负责，本页只处理生命周期、菜单与焦点。
///
/// **本页的「返回」= 退出房间**（架构 N3 / I1）：
/// <list type="bullet">
///   <item>外壳点 × / Alt+F4 / Esc / Alt+← → <see cref="OnBackRequestedAsync"/> →
///         走与菜单「退出房间」**完全同一条**命令（含房主的「是否保留房间」询问）→ 出栈回房间列表；</item>
///   <item>**不停内置服务器、不断连接、不退出程序** —— 那是登录页 / 房间列表页点 × 才做的事（I2）。</item>
/// </list>
///
/// 视图模型随页面生死（架构 §6.1）：<c>OnEnter</c> 新建 <see cref="GameViewModel"/>，
/// <c>OnLeave</c> 一律 <see cref="GameViewModel.Detach"/> —— 它订阅 5 个 <c>GameClient</c> 事件 +
/// 1 个静态服务事件，页面反复进出若不退订，同一条聊天消息会被处理 N 次（架构 §6.2）。
/// </summary>
public partial class RoomPage : UserControl, IPage
{
    private readonly AppSession _session;

    /// <summary>本页自带的宿主视图模型：进场创建、退场销毁。</summary>
    private GameViewModel? _vm;

    /// <summary>
    /// 「退出房间」已经由本页自己发起出栈（<see cref="OnRoomLeft"/>），
    /// 导航那边不用再走一次 —— 否则一次退房会把房间列表页也一起弹掉。
    /// </summary>
    private bool _suppressAutoBack;

    /// <summary>正在处理一次返回请求（防止连点 × 触发两次退出房间）。</summary>
    private bool _backRequested;

    public RoomPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.Room;

    public string Title => "房间";

    /// <summary>房间页允许返回 —— 但「返回」的含义是**退出房间**（N3）。</summary>
    public bool CanGoBack => true;

    /// <summary>进场：新建视图模型、恢复三栏显隐、注册收尾用的「退出房间」委托。</summary>
    public void OnEnter()
    {
        if (_vm == null)
        {
            _vm = new GameViewModel(_session.Client);
            DataContext = _vm;

            // 视图模型**不感知界面形态**：它只接受「保留 / 不保留」这个结论，
            // 询问本身由本页入栈一个 KeepRoomPage 浮层并等结果（S3），
            // 同步询问那套东西（注入回调 + 无参退房入口）已在 S4 整体删除。
            _vm.RoomLeft += OnRoomLeft;
        }

        RestoreUiState();

        // 从「关于 / 更新日志」返回时本页会走 OnLeave → Detach → _vm = null，再回来是新建的视图模型：
        // OnLoaded 会先按客户端的 LastState 回填一次（服务器那次广播在页面不在场时没人接），
        // 再主动拉一次 roomstate，因此玩家列表与状态不会显示陈旧数据（跨特性要求 §7）。
        _vm.OnLoaded();

        // 设置页里改三栏显隐 → 本页的菜单勾选与布局要跟着同步（架构 §6.2）。
        // S2 起改由 ViewOptions 广播：它是一个与窗口生死无关的小静态类，
        // 这样即使 S4 删掉了 SettingsWindow，这条接线也不会跟着断。
        ViewOptions.Subscribe(OnSettingsViewOptionsChanged);

        // 把「退出房间」注册进统一收尾：程序退出时由 AppShutdown 走同一条离开流程（PRD E-10）。
        AppShutdown.SetLeaveRoomHandler(_vm.LeaveRoomForShutdownAsync);

        ChatView.FocusChatInput();
    }

    /// <summary>
    /// 退场：保存三栏显隐、注销收尾委托、退订所有事件再丢掉视图模型。
    /// ⚠ 这里注销「退出房间」委托是**对的**：能走到退场说明房间已经退了（或本就没进），
    /// 收尾时那一步自然应当跳过（与旧 <c>MainWindow.OnClosing</c> 的 IsLoggedOut 分支等价）。
    /// </summary>
    public void OnLeave()
    {
        var vm = _vm;
        SaveUiState();

        ViewOptions.Unsubscribe(OnSettingsViewOptionsChanged);
        AppShutdown.ClearLeaveRoomHandler();

        if (vm == null) return;

        vm.RoomLeft -= OnRoomLeft;
        vm.Detach();

        DataContext = null;
        _vm = null;
    }

    /// <summary>
    /// 返回拦截（N3）：房间里 → 真的发一次 <c>leaveroom</c> 才允许出栈；
    /// 不在房间里 → 直接放行（纯导航）；退出失败 → 留在房间里（不放行）。
    /// </summary>
    public async Task<bool> OnBackRequestedAsync()
    {
        if (_backRequested) return false;   // 连点 × / 连按 Esc：只处理一次
        _backRequested = true;
        try
        {
            var joined = _vm?.IsJoined == true || _session.Client.IsJoined;
            return await RoomBackDecision.ResolveAsync(joined, LeaveRoomForBackAsync).ConfigureAwait(true);
        }
        finally
        {
            _backRequested = false;
        }
    }

    /// <summary>
    /// 由「返回」发起的退出房间：期间抑制 <see cref="OnRoomLeft"/> 的自动出栈
    /// （出栈由本次导航统一负责，避免弹两层）。
    /// </summary>
    private async Task<bool> LeaveRoomForBackAsync()
    {
        var vm = _vm;
        if (vm == null) return true;

        _suppressAutoBack = true;
        try
        {
            // 取消（null）→ 拦下这次返回：人还在房间里，房间页当然不能出栈。
            return await LeaveRoomWithPromptAsync(vm).ConfigureAwait(true) == true;
        }
        finally
        {
            _suppressAutoBack = false;
        }
    }

    /// <summary>
    /// 菜单「退出房间」/ 导航栏「退出房间」/ 导航栏「返回」共用的入口
    /// （v2.6.0 S3 起不再绑命令，因为询问是「入栈浮层 + 等结果」的异步过程）：
    /// 三者走**同一条**流程，成功后由 <see cref="OnRoomLeft"/> 自动出栈。
    /// </summary>
    private async void Menu_LeaveRoom_OnClick(object sender, RoutedEventArgs e)
        => _ = LeaveRoomFromUiAsync();

    /// <summary>
    /// 导航栏「返回」（v2.6.0 S6 新增的可见返回键）：与菜单「退出房间」/ 导航栏「退出房间」
    /// **完全同一条路径**（N9 单一出口的可见形态；键盘 <c>Alt+←</c> / <c>Esc</c> 走
    /// <see cref="OnBackRequestedAsync"/>，由 NavigationService 统一分派）。
    /// </summary>
    private void Back_OnClick(object sender, RoutedEventArgs e)
        => _ = LeaveRoomFromUiAsync();

    /// <summary>
    /// UI 主动发起退房（三个入口共用）：带 <c>_backRequested</c> 去重，连点只处理一次。
    /// </summary>
    private async Task LeaveRoomFromUiAsync()
    {
        var vm = _vm;
        if (vm == null) return;

        if (_backRequested) return;   // 连点：只处理一次
        _backRequested = true;
        try
        {
            var left = await LeaveRoomWithPromptAsync(vm).ConfigureAwait(true);

            // v2.6.0 S8：退房成功后**这里**就弹回房间列表，不等 <see cref="OnRoomLeft"/> 那条延迟路径。
            // 取消（null）→ 什么都不做，人留在房间里。
            if (left == true) NavigateBackToList();
        }
        finally
        {
            _backRequested = false;
        }
    }

    /// <summary>
    /// 退房的**唯一**页面侧流程（返回键与菜单共用）：
    /// 先按需问「是否保留房间」，再调视图模型发出 <c>leaveroom</c>。
    /// <returns>
    /// <c>true</c> = 确实已离开房间（允许出栈）；<c>false</c> = 退出失败（留在房间里）；
    /// <c>null</c> = **用户在询问里选了「取消」**（压根没退房，留在房间里）。
    /// </returns>
    /// </summary>
    private async Task<bool?> LeaveRoomWithPromptAsync(GameViewModel vm)
    {
        var keepRoom = await ResolveKeepRoomAsync(vm).ConfigureAwait(true);

        // 取消：不发 leaveroom，也不出栈。
        if (keepRoom == null) return null;

        return await vm.LeaveRoomForPageAsync(keepRoom.Value).ConfigureAwait(true);
    }

    /// <summary>
    /// 「房主 + 房内无他人」时弹出 <see cref="PageId.KeepRoom"/> 浮层并等结果。
    ///
    /// 翻译仍然只有一个来源：「取消」之外的值（<c>true</c> / <c>false</c> / 没显式选的 <c>true</c>）
    /// 统一交给 <see cref="KeepRoomDecision.Resolve"/>（<c>null</c> = 用户没显式选 → **保留**）。
    /// 页面里不重复写「null 算保留」的判断。
    /// <returns><c>null</c> = 用户选了「取消」（不退房）。</returns>
    /// </summary>
    private async Task<bool?> ResolveKeepRoomAsync(GameViewModel vm)
    {
        if (!vm.NeedsKeepRoomDecision) return false;   // 不是「房主独自退房」→ 无需询问，按不保留

        // 收尾期（AppShutdown 正在退出）不弹询问：浮层需要人回答，会阻塞退出流程。
        if (AppShutdown.IsShuttingDown) return false;

        var choice = await _session.Navigation
            .PushForDecisionAsync(new PageRequest(PageId.KeepRoom))
            .ConfigureAwait(true);

        // KeepRoomChoice.Cancel 是「不退出房间」，它不属于「保留 / 关闭」这个二选一，
        // 所以单独返回 null —— 别把它塞进 Resolve，那会退化成「不保留」而把房间关掉。
        if (choice == KeepRoomChoice.Cancel) return null;

        return KeepRoomDecision.Resolve(choice == KeepRoomChoice.Keep);
    }

    // ==================== 退出房间 ====================

    /// <summary>
    /// 视图模型报告「已经离开房间」（菜单 / 导航栏的「退出房间」按钮走这条）：
    /// 自动出栈回房间列表。收尾期（AppShutdown 正在走退出流程）不再出栈。
    ///
    /// v2.6.0 S8：改用 <see cref="NavigationService.PopTo"/> —— 事件已经证明「房退掉了」，
    /// 再走 <c>BackAsync</c> 会被房间页的返回拦截重新判断一次「是不是还在房间里」，
    /// 而那条路上还有连点去重之类的瞬时标志，时序一错就把这次出栈吞掉
    ///（界面永远停在房间页，正是 S8 要修的现象）。<c>PopTo</c> 幂等，和发起方那次重复调用不冲突。
    /// </summary>
    private void OnRoomLeft()
    {
        if (_suppressAutoBack) return;
        if (AppShutdown.IsShuttingDown) return;

        Dispatcher.BeginInvoke(new Action(NavigateBackToList));
    }

    /// <summary>
    /// 退房成功后回房间列表（v2.6.0 S8，唯一出口）。
    ///
    /// 优先 <see cref="NavigationService.PopTo"/>：结论已经定了（<c>leaveroom</c> 都发出去了），
    /// 一路弹到房间列表，中途不该再被任何返回拦截 / 连点去重之类的瞬时标志拦一下 ——
    /// 那些东西时序一错就把这次出栈吞掉，界面永远停在房间页（S8 修的就是它）。
    /// 房间列表不在栈里时（理论上不该发生）退回普通返回，至少不会把人卡在房间里。
    /// <c>PopTo</c> 幂等，所以本方法被重复调用（发起方一次 + <see cref="OnRoomLeft"/> 一次）不会多弹。
    /// </summary>
    private void NavigateBackToList()
    {
        if (_session.Navigation.PopTo(PageId.RoomList)) return;
        _ = _session.Navigation.BackAsync();
    }

    // ==================== 界面状态持久化（三栏显隐） ====================

    /// <summary>恢复上次的三栏显隐（窗口几何由 <see cref="ShellWindow"/> 负责）。</summary>
    private void RestoreUiState()
    {
        var state = UiStateStore.State;

        MenuTogglePlayers.IsChecked = state.ShowPlayers;
        MenuToggleChat.IsChecked = state.ShowChat;
        MenuToggleActions.IsChecked = state.ShowActions;
        ApplyPanelVisibility();
    }

    /// <summary>把三栏显隐写回磁盘。</summary>
    private void SaveUiState()
    {
        var state = UiStateStore.State;

        state.ShowPlayers = MenuTogglePlayers.IsChecked;
        state.ShowChat = MenuToggleChat.IsChecked;
        state.ShowActions = MenuToggleActions.IsChecked;
        UiStateStore.Save();
    }

    // ==================== 菜单：游戏 ====================

    /// <summary>退出程序：关闭外壳（外壳会按「菜单退出」走完整收尾）。</summary>
    private void Menu_Exit_OnClick(object sender, RoutedEventArgs e) => ShellWindow.RequestExit();

    /// <summary>
    /// 生成当前房间的邀请码：用**独立窗口**展示（而非弹窗）。
    /// 弹窗里的文字无法用鼠标选中，一旦剪贴板复制失败就没法把码取出来；
    /// 独立窗口里的文本框可选中，并自带「复制」按钮与重试。
    /// </summary>
    private void Menu_Invite_OnClick(object sender, RoutedEventArgs e) => ShowInviteCode();

    private async void ShowInviteCode()
    {
        if (_vm == null) return;

        var (ok, code, message) = await _vm.GenerateInviteCodeAsync();
        if (!ok || code.Length == 0)
        {
            MessageBox.Show(string.IsNullOrEmpty(message) ? "无法生成邀请码。" : message, "邀请码",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // S2：改成浮层页，码通过 PageRequest.Argument 传过去（页面因此不需要视图模型）。
        _ = _session.Navigation.PushOverlay(new PageRequest(PageId.InviteCode, code));
    }

    // ==================== 菜单：视图 ====================

    /// <summary>三个面板开关共用一个处理器：统一按三个开关的当前状态重排布局。</summary>
    private void Menu_TogglePanel_OnClick(object sender, RoutedEventArgs e) => ApplyPanelVisibility();

    /// <summary>
    /// 打开设置页（外观主题 / 主界面视图 / 上次使用信息 / 账号设置）。
    /// S2 起是**浮层页**：本页不退场（订阅与轮询继续），只是暂时不可点 —— 这正是 T41 的核心语义。
    /// </summary>
    private void Menu_Settings_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.PushOverlay(new PageRequest(PageId.Settings));

    /// <summary>打开房主设置页（最大人数 / 密码 / 身份配置 / 邀请码）。仅房主可用。</summary>
    private void Menu_HostSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        if (!_vm.IsHost)
        {
            MessageBox.Show("只有房主可以修改房间设置。", "房主设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ = _session.Navigation.PushOverlay(new PageRequest(PageId.HostSettings));
    }

    /// <summary>
    /// 设置窗口里改动了三栏显隐 → 把菜单勾选与布局同步过来
    /// （设置与「视图」菜单是同一份状态的两种入口，必须保持一致）。
    /// </summary>
    private void OnSettingsViewOptionsChanged()
    {
        var state = UiStateStore.State;
        MenuTogglePlayers.IsChecked = state.ShowPlayers;
        MenuToggleChat.IsChecked = state.ShowChat;
        MenuToggleActions.IsChecked = state.ShowActions;
        ApplyPanelVisibility();
    }

    /// <summary>
    /// 把三栏的列宽与可见性同步到菜单勾选状态。
    /// 隐藏一栏时必须同时把列宽归零，否则星号/固定列仍会占位留下空白。
    /// </summary>
    private void ApplyPanelVisibility()    {
        var players = MenuTogglePlayers.IsChecked;
        var chat = MenuToggleChat.IsChecked;
        var actions = MenuToggleActions.IsChecked;

        PlayersColumn.Width = players ? new GridLength(286) : new GridLength(0);
        ActionsColumn.Width = actions ? new GridLength(308) : new GridLength(0);
        ChatColumn.Width = chat ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PlayersView.Visibility = players ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ActionsView.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;

        // 中栏的左右留白取决于相邻栏是否可见，避免隐藏后留下悬空的 14px 空隙。
        ChatView.Margin = new Thickness(players ? 14 : 0, 0, actions ? 14 : 0, 0);
    }

    // ==================== 菜单：帮助 ====================

    /// <summary>
    /// 帮助 → 关于：v2.6.0 S3 起是**页面压栈**（<c>Push</c>，不是浮层）。
    ///
    /// 为什么是压栈而不是浮层：关于/更新日志是"换一屏"的独立内容（自带返回、自成一页），
    /// 不需要房间页在下面继续轮询与接收消息；压栈让房间页走 <c>OnLeave</c> 正常退订，
    /// 回来时由 <c>OnEnter</c> 重新订阅并回填状态（架构 §16.5 的跨特性要求）。
    /// </summary>
    private void Menu_About_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.Push(new PageRequest(PageId.About));

    /// <summary>帮助 → 更新日志：同上，页面压栈。</summary>
    private void Menu_Changelog_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.Push(new PageRequest(PageId.Changelog));

    // ==================== 快捷键 ====================

    /// <summary>
    /// 菜单里展示的 InputGestureText 只是“显示文本”，WPF 不会自动绑定按键，
    /// 因此这里显式处理：F1 关于、Ctrl+Q 退出、Ctrl+L 清空消息、Ctrl+G 开始游戏、Ctrl+I 邀请码。
    /// </summary>
    private void Page_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        switch (e.Key)
        {
            case Key.F1:
                _ = _session.Navigation.Push(new PageRequest(PageId.About));
                e.Handled = true;
                break;

            case Key.I when Keyboard.Modifiers == ModifierKeys.Control:
                ShowInviteCode();
                e.Handled = true;
                break;

            case Key.Q when Keyboard.Modifiers == ModifierKeys.Control:
                ShellWindow.RequestExit();
                e.Handled = true;
                break;

            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                if (_vm.ClearLogCommand.CanExecute(null)) _vm.ClearLogCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.G when Keyboard.Modifiers == ModifierKeys.Control:
                if (_vm.CanStartGame && _vm.StartGameCommand.CanExecute(null))
                    _vm.StartGameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
