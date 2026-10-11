using System.Windows;
using System.Windows.Controls;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>
/// 「修改密码」浮层的正文（v2.5.0 第 7 条）：旧密码 + 新密码 + 再输一遍。
///
/// 为什么是 <see cref="UserControl"/> 而不是 Window：新增 Window 子类会扩大
/// <c>ClientGUI.Tests</c> 的窗口结构契约（S1）；浮层正文塞进 <c>OverlayDialog</c> 即可（K11）。
///
/// <see cref="PasswordBox.Password"/> 不支持绑定，因此这里手动双向同步到
/// <see cref="ChangePasswordViewModel"/>；同步时用「不等才赋值」避免死循环。
/// </summary>
public partial class ChangePasswordDialog : UserControl
{
    /// <summary>本浮层的视图模型（宿主浮层点击「确定」时调 <see cref="ChangePasswordViewModel.SubmitAsync"/>）。</summary>
    public ChangePasswordViewModel ViewModel { get; } = new();

    public ChangePasswordDialog()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    /// <summary>把焦点给第一个框（浮层显示时调用）。</summary>
    public void FocusFirstField()
    {
        try { OldPasswordBox.Focus(); } catch { /* 未显示时焦点失败无需处理 */ }
    }

    /// <summary>清空三个框（每次打开浮层时调用）。</summary>
    public void ResetFields()
    {
        ViewModel.Reset();
        OldPasswordBox.Password = "";
        NewPasswordBox.Password = "";
        ConfirmPasswordBox.Password = "";
    }

    private void OldPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.OldPassword != OldPasswordBox.Password) ViewModel.OldPassword = OldPasswordBox.Password;
    }

    private void NewPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NewPassword != NewPasswordBox.Password) ViewModel.NewPassword = NewPasswordBox.Password;
    }

    private void ConfirmPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ConfirmPassword != ConfirmPasswordBox.Password)
            ViewModel.ConfirmPassword = ConfirmPasswordBox.Password;
    }
}
