using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 房间列表窗口：登录成功后进入。展示服务器上所有房间，支持刷新、创建、选中加密房输密码加入。
/// 加入 / 创建成功后以 <see cref="Window.DialogResult"/> = true 关闭，交回控制权给 <see cref="App"/>。
/// </summary>
public partial class RoomListWindow : Window
{
    private readonly RoomListViewModel _vm;
    private readonly GameClient _session;

    /// <summary>是否已经进房（进房导致的关闭不是「退出程序」，不该弹关窗确认）。</summary>
    private bool _enteredRoom;

    public RoomListWindow(GameClient session)
    {
        InitializeComponent();

        _session = session;
        _vm = new RoomListViewModel(session);
        DataContext = _vm;

        // PasswordBox.Password 不可绑定，手动双向同步。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoomListViewModel.Password)
                && PasswordInput.Password != _vm.Password)
            {
                PasswordInput.Password = _vm.Password;
            }
        };
        _vm.RoomEntered += OnRoomEntered;

        Loaded += async (_, _) =>
        {
            _vm.StartPolling();
            await _vm.RefreshAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => _vm.StopPolling();
    }

    private void OnRoomEntered()
    {
        _enteredRoom = true;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 关窗：内置服务器上还有其他玩家时弹一次确认（取消则彻底无损）。
    /// 连接与内置服务器的释放统一交给 <see cref="AppShutdown"/>（OnExit）。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 已经进房（DialogResult=true 的关闭）不是“退出程序”，不该弹确认。
        if (_enteredRoom) return;

        if (!AppShutdown.ConfirmClose(this)) e.Cancel = true;
    }

    /// <summary>回车即加入选中房间。</summary>
    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.IsIdle) return;
        if (_vm.JoinCommand.CanExecute(null)) _vm.JoinCommand.Execute(null);
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.Password != _vm.Password) _vm.Password = box.Password;
    }

    private void CreateRoom_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateRoomWindow(_session) { Owner = this };
        if (dialog.ShowDialog() == true)
            _ = _vm.CreateAndJoinAsync(dialog.RoomName, dialog.Password, dialog.SelectedGameId);
    }

    /// <summary>房间列表界面也能打开设置（外观主题等）。</summary>
    private void Settings_OnClick(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);
}
