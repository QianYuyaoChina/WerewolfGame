using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>
/// 登录窗口：程序启动后第一个出现的界面（类似网站的登录页）。
/// 登录成功后把已建立连接的 <see cref="GameClient"/> 交给主窗口，自身关闭。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm = new();

    public LoginWindow()
    {
        InitializeComponent();

        DataContext = _vm;

        // 视图模型可能在非 UI 线程被改写（例如要清空密码），统一同步回 PasswordBox。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.PasswordInput)
                && PasswordInput.Password != _vm.PasswordInput)
            {
                PasswordInput.Password = _vm.PasswordInput;
            }
        };
        _vm.LoginSucceeded += OnLoginSucceeded;

        Loaded += (_, _) => UsernameInput.Focus();

        // 未登录就关闭窗口时，释放可能残留的连接。
        Closed += (_, _) =>
        {
            if (!_vm.IsLoggedIn) _ = _vm.DisposeSessionAsync();
        };
    }

    /// <summary>登录成功后对外暴露的会话（供主窗口复用）。</summary>
    public GameClient? Session { get; private set; }

    private void OnLoginSucceeded(GameClient session)
    {
        Session = session;
        DialogResult = true;
        Close();
    }

    // ==================== 交互 ====================

    /// <summary>回车即登录（用户名和密码框都适用）。</summary>
    private void LoginWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.IsIdle) return;
        e.Handled = true;

        if (_vm.LoginCommand.CanExecute(null)) _vm.LoginCommand.Execute(null);
    }

    /// <summary><see cref="PasswordBox.Password"/> 不支持绑定，只能手动同步。</summary>
    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.Password != _vm.PasswordInput) _vm.PasswordInput = box.Password;
    }
}
