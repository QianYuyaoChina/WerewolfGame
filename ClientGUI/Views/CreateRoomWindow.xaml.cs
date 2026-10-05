using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 创建房间对话框：选择游戏（默认狼人杀）+ 房间名（可留空）+ 可选密码（勾选「不设密码」即公开房间）。
/// 确认后 <see cref="Window.DialogResult"/> 为 true，调用方读取 <see cref="RoomName"/> / <see cref="Password"/> / <see cref="SelectedGameId"/>。
/// </summary>
public partial class CreateRoomWindow : Window
{
    private readonly CreateRoomViewModel _vm;

    public CreateRoomWindow(GameClient? session = null)
    {
        InitializeComponent();

        _vm = new CreateRoomViewModel(session);
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

        Loaded += async (_, _) =>
        {
            await _vm.LoadGamesAsync();
            RoomNameInput.Focus();
        };
    }

    /// <summary>房间名（可留空 → 服务端回退默认名）。</summary>
    public string RoomName => _vm.RoomName;

    /// <summary>房间密码；<b>留空即公开房间</b>（不再需要单独勾选）。</summary>
    public string? Password => string.IsNullOrEmpty(_vm.Password) ? null : _vm.Password;

    /// <summary>选中的游戏机器名（默认狼人杀）。</summary>
    public string SelectedGameId => _vm.SelectedGameId;

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
