using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 登录窗口：程序启动后第一个出现的界面（类似网站的登录页）。
///
/// v2.4.0 起重写为两个标签页：
/// <list type="bullet">
///   <item><b>创建内置服务器</b>（默认停留）—— 在这台电脑上直接开一个服务器并自动连上；</item>
///   <item><b>连接现有服务器</b> —— 控件、默认值与邀请码逻辑与改造前逐项一致。</item>
/// </list>
/// 两个标签页**共用同一份用户名 / 密码**（切页不丢），并共享底部状态条。
///
/// 注意：必须保留 <c>new LoginWindow()</c> 的无参可实例化能力 ——
/// <c>ClientGUI.Tests/UiSmokeCheck.cs</c> 的界面冒烟依赖它。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm = new();
    private readonly EmbeddedServerViewModel _embedded;

    public LoginWindow()
    {
        InitializeComponent();

        _embedded = new EmbeddedServerViewModel(_vm);

        // 视图模型可能在非 UI 线程被改写（例如要清空密码），统一同步回两个 PasswordBox。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LoginViewModel.PasswordInput)) return;

            if (PasswordInput.Password != _vm.PasswordInput) PasswordInput.Password = _vm.PasswordInput;
            if (EmbeddedPasswordInput.Password != _vm.PasswordInput)
                EmbeddedPasswordInput.Password = _vm.PasswordInput;
        };
        _vm.LoginSucceeded += OnLoginSucceeded;

        // 上次停留的标签页（P1-5）；默认停在「创建内置服务器」。
        RestoreLastTab();

        Loaded += (_, _) => (Tabs.SelectedItem == EmbeddedTab ? EmbeddedUsernameInput : UsernameInput).Focus();

        Closing += OnClosing;

        // 未登录就关闭窗口时，释放可能残留的连接；同时停掉运行信息轮询。
        Closed += (_, _) =>
        {
            _embedded.StopPolling();
            if (!_vm.IsLoggedIn) _ = _vm.DisposeSessionAsync();
        };
    }

    /// <summary>
    /// 关窗：内置服务器上还有其他玩家时弹一次确认（取消则彻底无损）。
    /// 登录成功导致的关闭（<see cref="Session"/> 已赋值）不是「退出程序」，不弹。
    /// 内置服务器的停止统一由 <see cref="AppShutdown"/>（OnExit）负责（PRD E-11）。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Session != null) return;
        if (!AppShutdown.ConfirmClose(this)) e.Cancel = true;
    }

    /// <summary>Tab2「连接现有服务器」的视图模型（窗口内各绑定通过本属性取到）。</summary>
    public LoginViewModel Login => _vm;

    /// <summary>Tab1「创建内置服务器」的视图模型（窗口内各绑定通过本属性取到）。</summary>
    public EmbeddedServerViewModel Embedded => _embedded;

    /// <summary>登录成功后对外暴露的会话（供主窗口复用）。</summary>
    public GameClient? Session { get; private set; }

    private void OnLoginSucceeded(GameClient session)
    {
        Session = session;
        DialogResult = true;
        Close();
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
    private void LoginWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.IsIdle) return;

        if (Tabs.SelectedItem == EmbeddedTab)
        {
            // 内置页：回车优先「启动并进入」（没起服务器时），已启动则登录。
            if (_embedded.CanStart && _embedded.StartAndEnterCommand.CanExecute(null))
            {
                e.Handled = true;
                _embedded.StartAndEnterCommand.Execute(null);
                return;
            }

            if (_embedded.LoginToEmbeddedCommand.CanExecute(null))
            {
                e.Handled = true;
                _embedded.LoginToEmbeddedCommand.Execute(null);
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

    /// <summary>同上（Tab1，与 Tab2 共用同一份凭据）。</summary>
    private void EmbeddedPasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.Password != _vm.PasswordInput) _vm.PasswordInput = box.Password;
    }

    /// <summary>登录界面也能打开设置（外观主题等）。</summary>
    private void Settings_OnClick(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);
}
