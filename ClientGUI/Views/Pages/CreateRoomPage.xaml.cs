using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 创建房间页（v2.6.0 S2 由 <c>CreateRoomWindow</c> 搬迁而来）：
/// 选择游戏（默认狼人杀）+ 房间名（可留空）+ 可选密码（留空即公开房间）。
///
/// **两种入栈语义都支持，但正常路径是浮层**（架构 §9.1 / T41）：
/// <list type="bullet">
///   <item>正常路径 —— 房间列表页用 <see cref="NavigationService.PushForResultAsync{T}"/> 打开本页。
///         栈顶是本页、下层是<b>仍在轮询</b>的房间列表页（浮层式入栈：下层不退场、不退订）；
///         点「创建」时本页把「游戏 / 房间名 / 密码」打包成 <see cref="CreateRoomResult"/> 交给
///         <see cref="NavigationService.Complete"/>，由列表页接着去建房 + 进房。</item>
///   <item>兜底路径 —— 万一被普通 <c>Push</c> 打开（没有等待者），<see cref="NavigationService.Complete"/>
///         会返回 <c>false</c>（栈不动），本页就退回「普通返回」，绝不会卡在半路。</item>
/// </list>
///
/// 为什么本页**不自己建房**：建房要用的连接、列表状态与「忙碌中」标记都在
/// <see cref="RoomListViewModel"/> 上；浮层页一旦自己去发请求，两处状态就会打架（架构 D5）。
/// </summary>
public partial class CreateRoomPage : UserControl, IPage
{
    private readonly AppSession _session;

    /// <summary>本页自带的视图模型：进场创建、退场销毁（它订阅 PropertyChanged 与一个自定义事件）。</summary>
    private CreateRoomViewModel? _vm;

    public CreateRoomPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.CreateRoom;

    public string Title => "创建房间";

    /// <summary>浮层也能返回（Esc / 导航栏「返回」/ 底部「取消」都走同一个入口，N9）。</summary>
    public bool CanGoBack => true;

    /// <summary>进场：建视图模型、接事件、拉一次游戏列表（拉不到就保留默认狼人杀）。</summary>
    public void OnEnter()
    {
        if (_vm == null)
        {
            _vm = new CreateRoomViewModel(_session.Client);
            DataContext = _vm;

            // PasswordBox.Password 不可绑定 → 手动双向同步（与旧窗口逐字相同）。
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.CreateSucceeded += OnCreateSucceeded;
        }

        _ = LoadGamesAsync();
    }

    /// <summary>退场：退订两个事件、丢掉视图模型（页面反复进出不会累积订阅）。</summary>
    public void OnLeave()
    {
        var vm = _vm;
        if (vm == null) return;

        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.CreateSucceeded -= OnCreateSucceeded;

        DataContext = null;
        _vm = null;
    }

    /// <summary>返回拦截：本页没有需要确认的破坏性动作，一律放行（结果按「取消」= null 回传）。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 事件 ====================

    /// <summary>视图模型算出了新密码 → 同步到密码框（反过来由 PasswordChanged 同步）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CreateRoomViewModel.Password)) return;
        if (_vm == null) return;
        if (PasswordInput.Password != _vm.Password) PasswordInput.Password = _vm.Password;
    }

    /// <summary>
    /// 「创建」被按下（<see cref="CreateRoomViewModel.CreateCommand"/> 已在 VM 侧校验完毕）：
    /// 把输入打包成结果交回去。栈由 <see cref="NavigationService.Complete"/> 负责出栈，
    /// 本页**不自己**调 <c>BackAsync</c>（否则会把两层一起弹掉）。
    /// </summary>
    private void OnCreateSucceeded()
    {
        var vm = _vm;
        if (vm == null) return;

        var result = new CreateRoomResult(
            vm.RoomName,
            string.IsNullOrEmpty(vm.Password) ? null : vm.Password,
            vm.SelectedGameId);

        // Complete 返回 true = 确实有一层浮层在等结果且已出栈；false = 没人等（被普通 Push 打开）。
        if (_session.Navigation.Complete(result)) return;

        _ = _session.Navigation.BackAsync();
    }

    /// <summary>拉取服务器可用游戏；失败/为空时视图模型内部会保留默认狼人杀。</summary>
    private async Task LoadGamesAsync()
    {
        var vm = _vm;
        if (vm == null) return;

        try
        {
            await vm.LoadGamesAsync();
        }
        catch
        {
            // 拉不到游戏列表不影响创建流程（默认狼人杀已在 VM 里预置）。
        }

        // 期间页面可能已经退场（连点返回）：只有还活着才抢焦点。
        if (_vm == vm) RoomNameInput.Focus();
    }

    // ==================== 交互 ====================

    /// <summary>导航栏「返回」：走统一返回入口（N9），结果按「取消」处理。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>底部「取消」：与「返回」完全同一条路径。</summary>
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (_vm != null && box.Password != _vm.Password) _vm.Password = box.Password;
    }
}
