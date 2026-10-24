using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 房间列表页（v2.6.0 S1 由 <c>RoomListWindow</c> 搬迁而来）：登录成功后进入。
/// 展示服务器上所有房间，支持刷新、创建、选中加密房输密码加入。
///
/// 两个 v2.5.0 职责原样保留：
/// <list type="number">
///   <item>导航栏居中标题「房间列表」是**彩蛋热区**：3 秒内点 5 下弹出输入框（第 3 条）；</item>
///   <item>导航栏显示内置服务器运行胶囊 —— 关主窗口不停服务器，回到这里要能看出来（PRD 8.6）。</item>
/// </list>
///
/// v2.6.0 S1 的两处新语义：
/// <list type="number">
///   <item><b>返回 = 回登录页</b>（Q1=B）：纯导航，**不断连接、不停内置服务器**；</item>
///   <item><b>视图模型随页面生死</b>：<c>OnEnter</c> 新建、<c>OnLeave</c> 一律 <c>Detach()</c>。
///         本页会被反复进出（列表 ↔ 房间），漏退订就会把 <c>EmbeddedServerService.Changed</c>
///         的订阅按次数累积起来（架构 §6.2 的硬性发现）。</item>
/// </list>
/// </summary>
public partial class RoomListPage : UserControl, IPage
{
    private readonly AppSession _session;

    /// <summary>本页自带的视图模型：进场创建、退场销毁（它带 3 秒轮询与静态事件订阅）。</summary>
    private RoomListViewModel? _vm;

    /// <summary>彩蛋输入框的视图模型（懒建：多数用户一辈子也触发不到彩蛋）。</summary>
    private EggViewModel? _eggVm;

    /// <summary>彩蛋输入框的浮层正文（懒建，与视图模型同生命周期）。</summary>
    private EggDialog? _eggDialog;

    public RoomListPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.RoomList;

    public string Title => "房间列表";

    /// <summary>Q1=B：可以从房间列表返回登录页。</summary>
    public bool CanGoBack => true;

    /// <summary>进场：没有视图模型就新建一个（页面重建 = 视图模型重建，订阅不累积）。</summary>
    public void OnEnter()
    {
        if (_vm == null)
        {
            _vm = new RoomListViewModel(_session.Client);
            DataContext = _vm;

            // PasswordBox.Password 不可绑定，手动双向同步。
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.RoomEntered += OnRoomEntered;
        }

        _vm.StartPolling();
        _ = _vm.RefreshAsync();
    }

    /// <summary>退场：退订 + 停轮询（<see cref="RoomListViewModel.Detach"/> 幂等），再丢掉视图模型。</summary>
    public void OnLeave()
    {
        var vm = _vm;
        if (vm == null) return;

        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.RoomEntered -= OnRoomEntered;
        vm.Detach();
        vm.StopPolling();

        DataContext = null;
        _vm = null;

        // 彩蛋浮层与它的视图模型随页面一起回收（下次进来重新点 5 下才会再弹）。
        EggOverlay.CloseDialog();
        _eggDialog = null;
        _eggVm = null;
    }

    /// <summary>返回上一页（登录页）：纯导航，不碰连接、不碰内置服务器（Q1=B / N4）。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 事件 ====================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RoomListViewModel.Password)) return;
        if (_vm == null) return;
        if (PasswordInput.Password != _vm.Password) PasswordInput.Password = _vm.Password;
    }

    /// <summary>成功加入 / 创建房间：入栈房间页（返回时回到本页，连接不复用重建）。</summary>
    private void OnRoomEntered()
        => _session.Navigation.Push(new PageRequest(PageId.Room));

    // ==================== 交互 ====================

    /// <summary>回登录页（导航栏「返回」按钮）：走统一返回入口（N9）。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.BackAsync();

    /// <summary>回车即加入选中房间。</summary>
    private void Page_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key != Key.Enter || !_vm.IsIdle) return;
        if (_vm.JoinCommand.CanExecute(null)) _vm.JoinCommand.Execute(null);
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (_vm != null && box.Password != _vm.Password) _vm.Password = box.Password;
    }

    /// <summary>
    /// 创建房间（v2.6.0 S2 起是**浮层页**，架构 §9.1）：
    /// <see cref="NavigationService.PushForResultAsync{T}"/> 等待创建页把输入交回来，
    /// 拿到结果后仍由本页的视图模型去建房 + 进房 —— 连接与列表状态都留在本页。
    ///
    /// 浮层式入栈意味着本页**不会**收到 <c>OnLeave</c>：轮询照常、订阅照常，
    /// 因此浮层进出多少轮都不会让 <c>EmbeddedServerService.Changed</c> 的订阅数变多（T41）。
    /// </summary>
    private async void CreateRoom_OnClick(object sender, RoutedEventArgs e)
    {
        var result = await _session.Navigation
            .PushForResultAsync<CreateRoomResult>(new PageRequest(PageId.CreateRoom))
            .ConfigureAwait(true);

        // 用户点了「取消」/ 返回 → 结果为空，什么都不做（还留在房间列表页）。
        if (result == null || _vm == null) return;

        _ = _vm.CreateAndJoinAsync(result.RoomName, result.Password, result.GameId);
    }

    /// <summary>房间列表界面也能打开设置（外观主题等）。S2 起改成浮层页，不再弹旧窗口。</summary>
    private void Settings_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.PushOverlay(new PageRequest(PageId.Settings));

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

        if (_vm != null && _vm.RegisterTitleTap()) OpenEggDialog();
        e.Handled = true;
    }

    /// <summary>打开彩蛋输入框（页面内浮层，不是 Window）。</summary>
    private void OpenEggDialog()
    {
        _eggVm ??= new EggViewModel(_session.Client);
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

        if (_vm == null) return;
        if (!string.IsNullOrEmpty(notice)) _vm.ShowNotice(notice);
        _ = _vm.RefreshAsync();
    }

    /// <summary>收起彩蛋浮层并复位计数（下次重新点 5 下才会再弹）。</summary>
    private void CloseEggDialog()
    {
        EggOverlay.CloseDialog();
        _vm?.ResetTitleTaps();
    }
}
