using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 登录页（v2.6.0 S1 由 <c>LoginWindow</c> 搬迁而来）：外壳窗口的**根页**。
///
/// 双标签页（创建内置服务器 / 连接现有服务器）、共享的用户名密码与状态条、
/// 邀请码、「一键开服 → 自动进入房间列表」的行为与迁移前逐项一致 ——
/// 变的只有「登录成功后做什么」：原来是 <c>DialogResult = true</c> 关窗，
/// 现在是 <see cref="NavigationService.Reset"/> 换页（清栈，N2）。
///
/// 本页**不持有** <see cref="GameClient"/>：连接属于 <see cref="AppSession"/>，
/// 页面反复进出也不会重建它（否则退房间就断连，I1 破）。
///
/// 另外保留 <c>new LoginPage(session)</c> 的可实例化能力 ——
/// <c>ClientGUI.Tests/PageCatalog.cs</c> 的页面骨架断言依赖它。
/// </summary>
public partial class LoginPage : UserControl, IPage
{
    private readonly AppSession _session;
    private readonly LoginViewModel _vm;
    private readonly EmbeddedServerViewModel _embedded;

    public LoginPage(AppSession session)
    {
        InitializeComponent();

        _session = session;
        _vm = session.Login;
        _embedded = session.Embedded;

        // 视图模型可能在非 UI 线程被改写（例如要清空密码），统一同步回密码框。
        _vm.PropertyChanged += OnLoginPropertyChanged;

        // 上次停留的标签页（P1-5）；默认停在「创建内置服务器」。
        RestoreLastTab();
    }

    // ==================== IPage ====================

    public PageId Id => PageId.Login;

    public string Title => "登录";

    /// <summary>根页：导航栏不画返回键，按 Esc 也无效（N1 / N7）。</summary>
    public bool CanGoBack => false;

    /// <summary>
    /// 进场：重新订阅「登录成功」，并把焦点落到主按钮上。
    /// 从房间列表返回本页时连接**没有断**（Q1=B），因此这里只刷新派生文案，不动连接。
    /// </summary>
    public void OnEnter()
    {
        _vm.LoginSucceeded -= OnLoginSucceeded;   // 防重复订阅（进出多次也只挂一次）
        _vm.LoginSucceeded += OnLoginSucceeded;

        // 回到本页时主按钮可能已经该显示「进入房间列表」了（服务器在跑 + 已登录）。
        _embedded.RefreshComputedState();

        // 内置页不再有输入框：焦点直接落在「一键开服」主按钮上，回车即可开服。
        var target = Tabs.SelectedItem == EmbeddedTab
            ? (Control)EmbeddedPrimaryAction
            : UsernameInput;
        target.Focus();
    }

    /// <summary>退场：退订登录成功事件（订阅必须成对，N6）。</summary>
    public void OnLeave()
    {
        _vm.LoginSucceeded -= OnLoginSucceeded;
    }

    /// <summary>根页本来就不能返回；即使被调用也不允许出栈。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(false);

    // ==================== 对外暴露（与窗口版保持同名，便于断言） ====================

    /// <summary>Tab2「连接现有服务器」的视图模型（页内各绑定通过本属性取到）。</summary>
    public LoginViewModel Login => _vm;

    /// <summary>Tab1「创建内置服务器」的视图模型（页内各绑定通过本属性取到）。</summary>
    public EmbeddedServerViewModel Embedded => _embedded;

    /// <summary>会话（供设置窗口等按需取用）；页面不拥有它，只借来看。</summary>
    public Werewolf.Client.Share.GameClient Session => _session.Client;

    // ==================== 事件 ====================

    private void OnLoginPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LoginViewModel.PasswordInput)) return;
        if (PasswordInput.Password != _vm.PasswordInput) PasswordInput.Password = _vm.PasswordInput;
    }

    /// <summary>
    /// 登录成功（含「一键开服 → 进入房间列表」）：换到房间列表页。
    /// 用 <see cref="NavigationService.Reset"/> 清栈 —— 登录页是根，进列表后不该还能返回它（N2）。
    /// </summary>
    private void OnLoginSucceeded(Werewolf.Client.Share.GameClient session)
    {
        _session.Navigation.Reset(new PageRequest(PageId.RoomList));
    }

    // ==================== 标签页 ====================

    /// <summary>恢复上次停留的标签页（P1-5，默认「创建内置服务器」）。</summary>
    private void RestoreLastTab()
    {
        var saved = UiStateStore.State.LastLoginTab;
        Tabs.SelectedItem = string.Equals(saved, "external", StringComparison.OrdinalIgnoreCase)
            ? ExternalTab
            : EmbeddedTab;
    }

    /// <summary>记住本次停留的标签页，下次启动自动带入（P1-5）。</summary>
    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UiStateStore.State.LastLoginTab = Tabs.SelectedItem == ExternalTab ? "external" : "embedded";
        UiStateStore.Save();
    }

    // ==================== 交互 ====================

    /// <summary>回车即登录（用户名、密码框都适用；按当前所在标签页分派）。</summary>
    private void Page_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.IsIdle) return;

        if (Tabs.SelectedItem == EmbeddedTab)
        {
            // 内置页：回车 = 点「一键开服」（就绪后 = 「进入房间列表」）。
            if (_embedded.CanOneClick && _embedded.OneClickCommand.CanExecute(null))
            {
                e.Handled = true;
                _embedded.OneClickCommand.Execute(null);
            }
            return;
        }

        e.Handled = true;
        if (_vm.LoginCommand.CanExecute(null)) _vm.LoginCommand.Execute(null);
    }

    /// <summary><see cref="PasswordBox.Password"/> 不支持绑定，只能手动同步（Tab2）。</summary>
    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.Password != _vm.PasswordInput) _vm.PasswordInput = box.Password;
    }

    /// <summary>
    /// 登录界面也能打开设置（外观主题等）。
    /// v2.6.0 S4：旧 <c>SettingsWindow.Open(Window)</c> 随窗口类一起删除，改走浮层页 ——
    /// 页面自己就是会话的持有者，不需要再"从宿主窗口反查会话"那一层。
    /// </summary>
    private void Settings_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.PushOverlay(new PageRequest(PageId.Settings));
}
