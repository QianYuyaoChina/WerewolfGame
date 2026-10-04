using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>
/// 创建房间对话框：房间名（可留空）+ 可选密码（勾选「不设密码」即公开房间）。
/// 确认后 <see cref="Window.DialogResult"/> 为 true，调用方读取 <see cref="RoomName"/> / <see cref="Password"/>。
/// </summary>
public partial class CreateRoomWindow : Window
{
    private readonly CreateRoomViewModel _vm = new();

    public CreateRoomWindow()
    {
        InitializeComponent();

        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreateRoomViewModel.Password)
                && PasswordInput.Password != _vm.Password)
            {
                PasswordInput.Password = _vm.Password;
            }
        };
        _vm.CreateSucceeded += OnCreateSucceeded;

        Loaded += (_, _) => RoomNameInput.Focus();
    }

    /// <summary>房间名（可留空 → 服务端回退默认名）。</summary>
    public string RoomName => _vm.RoomName;

    /// <summary>房间密码；勾选「不设密码」时返回 null。</summary>
    public string? Password => _vm.NoPassword || string.IsNullOrEmpty(_vm.Password) ? null : _vm.Password;

    private void OnCreateSucceeded()
    {
        DialogResult = true;
        Close();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.Password != _vm.Password) _vm.Password = box.Password;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
