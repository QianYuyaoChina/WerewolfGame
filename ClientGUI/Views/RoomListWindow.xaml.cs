using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 房间列表窗口：登录成功后进入。展示服务器上所有房间，支持刷新、创建、选中加密房输密码加入。
/// 加入 / 创建成功后以 <see cref="Window.DialogResult"/> = true 关闭，交回控制权给 <see cref="App"/>。
///
/// 两个 v2.5.0 新增职责：
/// <list type="number">
///   <item>导航栏居中标题「房间列表」是**彩蛋热区**：3 秒内点 5 下弹出输入框（第 3 条）；</item>
///   <item>导航栏显示内置服务器运行胶囊 —— 关主窗口不停服务器，回到这里要能看出来（PRD 8.6）。</item>
/// </list>
/// </summary>
public partial class RoomListWindow : Window
{
    private readonly RoomListViewModel _vm;
    private readonly GameClient _session;

    /// <summary>
    /// 本窗口持有的会话（已登录）。设置窗口据此决定是否显示「修改密码 / 服务器设置」入口
    /// （v2.5.0 第 5 / 7 条）——只读，不暴露可修改的通道。
    /// </summary>
    public GameClient Session => _session;

    /// <summary>彩蛋输入框的视图模型（懒建：多数用户一辈子也触发不到彩蛋）。</summary>
    private EggViewModel? _eggVm;

    /// <summary>彩蛋输入框的浮层正文（懒建，与视图模型同生命周期）。</summary>
    private EggDialog? _eggDialog;

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
    /// 关窗：这是**真正会停服务器**的入口，因此保留关窗确认（取消则彻底无损）。
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

    // ==================== 彩蛋（第 3 条） ====================

    /// <summary>
    /// 导航栏标题的每一次**按下**都记一次（用底层按下事件而不是双击事件，
    /// 这样被系统识别成双击的连点也会照常计入次数 —— PRD 8.3 最后一行）。
    /// 判定与清零全在 <see cref="EggTapCounter"/> 里，未达成时**不给任何反馈**。
    /// </summary>
    private void NavTitle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 输入框已经打开 → 不再计数（不重复弹，已开的保持在最前）。
        if (EggOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            return;
        }

        if (_vm.RegisterTitleTap()) OpenEggDialog();
        e.Handled = true;
    }

    /// <summary>打开彩蛋输入框（页面内浮层，不是 Window）。</summary>
    private void OpenEggDialog()
    {
        _eggVm ??= new EggViewModel(_session);
        _eggVm.Reset();

        _eggDialog ??= new EggDialog { DataContext = _eggVm };

        EggOverlay.DialogTitle = "彩蛋";
        EggOverlay.DialogBody = _eggDialog;
        SyncEggButtons();
        EggOverlay.ShowDialog();
    }

    /// <summary>浮层按钮文案随形态切换（口令态 = 确定，称号态 = 保存）。</summary>
    private void SyncEggButtons()
    {
        if (_eggVm == null) return;

        EggOverlay.PrimaryButtonText = _eggVm.PrimaryButtonText;
        EggOverlay.SecondaryButtonText = "取消";
    }

    /// <summary>把视图模型的就地提示同步到浮层上（不弹框）。</summary>
    private void SyncEggMessage()
    {
        if (_eggVm == null) return;

        EggOverlay.Message = _eggVm.Message;
        EggOverlay.MessageIsError = _eggVm.MessageIsError;
    }

    /// <summary>主按钮：口令态去问服务端，称号态保存。</summary>
    private async void EggOverlay_OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_eggVm == null) return;

        EggOverlay.PrimaryEnabled = false;
        EggSubmitResult result;
        try
        {
            result = await _eggVm.SubmitAsync();
        }
        finally
        {
            EggOverlay.PrimaryEnabled = true;
        }

        SyncEggMessage();
        SyncEggButtons();

        if (result == EggSubmitResult.Close) FinishEgg(_eggVm.Notice);
    }

    /// <summary>次按钮 / 点遮罩：取消，直接收起浮层。</summary>
    private void EggOverlay_OnSecondaryClick(object sender, RoutedEventArgs e) => CloseEggDialog();

    /// <summary>称号保存 / 清除成功：收起浮层，把结果写在状态行并立刻刷新一次列表。</summary>
    private void FinishEgg(string notice)
    {
        CloseEggDialog();

        if (!string.IsNullOrEmpty(notice)) _vm.ShowNotice(notice);
        _ = _vm.RefreshAsync();
    }

    /// <summary>收起彩蛋浮层并复位计数（下次重新点 5 下才会再弹）。</summary>
    private void CloseEggDialog()
    {
        EggOverlay.CloseDialog();
        _vm.ResetTitleTaps();
    }
}
