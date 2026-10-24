using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 设置页（v2.6.0 S2 由 <c>SettingsWindow</c> 搬迁而来）：集中管理所有客户端设置
/// （外观主题、主界面视图、上次使用的连接信息），并承载「修改密码…」与「服务器设置…」两个账号入口。
///
/// 三处与旧窗口**语义不同**，都是页面化带来的（架构 §9.1 S2 的判据 ②③）：
/// <list type="number">
///   <item><b>会话来源变了</b>：旧窗口是从「谁当 owner」反推会话
///         （<c>ResolveSession(Window)</c> 里一串 <c>switch (owner)</c>）；页面里直接拿
///         <see cref="AppSession.Client"/> —— 会话只有一个，不需要按宿主类型分支（T39 顺带消掉）；</item>
///   <item><b>不再有「同一时刻只开一个」的静态 <c>_open</c> 守卫</b>：改由页面栈的
///         **防重复入栈**保证（同 <see cref="PageId"/> 已在栈里就先弹掉再压新的，N5）；</item>
///   <item><b>关闭 = 出栈</b>：底部「关闭」/ 导航栏「返回」/ Esc 一律走
///         <see cref="NavigationService.BackAsync"/>，不会退出程序、不会停服务器。</item>
/// </list>
///
/// 三栏显隐的联动改由 <see cref="ViewOptions.Notify"/> 广播（不再依赖窗口类的静态事件），
/// 房间页在 <c>OnEnter</c> / <c>OnLeave</c> 里成对订阅 / 退订（架构 §6.2）。
/// </summary>
public partial class SettingsPage : UserControl, IPage
{
    private readonly AppSession _session;

    /// <summary>载入或恢复默认设置时屏蔽控件事件，避免把「回填」当成「用户改动」。</summary>
    private bool _loading;

    /// <summary>账号浮层当前承载的业务（修改密码 / 服务器设置 / 无）。</summary>
    private enum AccountOverlayMode { None, ChangePassword, ServerSettings }

    private AccountOverlayMode _overlayMode = AccountOverlayMode.None;
    private ChangePasswordDialog? _cpDialog;
    private ServerSettingsDialog? _ssDialog;

    public SettingsPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.Settings;

    public string Title => "设置";

    /// <summary>浮层可以返回（Esc / 返回 / 底部「关闭」都走同一个入口，N9）。</summary>
    public bool CanGoBack => true;

    /// <summary>
    /// 进场：**每次都从磁盘重读一遍**（主题可能被别处改过），并按当前会话重算账号入口显隐。
    /// 与旧窗口「构造时读一次」相比这更准确：页面可以被反复进出，而会话身份可能在两次之间变了。
    /// </summary>
    public void OnEnter()
    {
        _loading = true;
        LoadFromState();
        _loading = false;

        ApplyAccountSection();
    }

    /// <summary>退场：收起浮层、丢掉浮层正文（不留下悬挂的订阅）。</summary>
    public void OnLeave()
    {
        AccountOverlay.CloseDialog();
        _overlayMode = AccountOverlayMode.None;
        _cpDialog = null;
        _ssDialog = null;
    }

    /// <summary>返回拦截：设置是即改即存的，没有「未保存」状态，一律放行。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    /// <summary>本页唯一认识的会话（不存在「按宿主窗口类型反推」这条分支）。</summary>
    private GameClient Client => _session.Client;

    // ==================== 读取 / 展示 ====================

    private void LoadFromState()
    {
        var state = UiStateStore.State;

        ThemeSystem.IsChecked = ThemeManager.Preference == AppTheme.System;
        ThemeLight.IsChecked = ThemeManager.Preference == AppTheme.Light;
        ThemeDark.IsChecked = ThemeManager.Preference == AppTheme.Dark;

        ShowPlayers.IsChecked = state.ShowPlayers;
        ShowChat.IsChecked = state.ShowChat;
        ShowActions.IsChecked = state.ShowActions;
        PageAnimation.IsChecked = state.PageTransitionAnimation;

        ServerText.Text = Blank(state.ServerUrl);
        UserText.Text = Blank(state.Username);
        RoomText.Text = Blank(state.LastRoomId);

        UpdateThemeHint();
    }

    private static string Blank(string? text)
        => string.IsNullOrWhiteSpace(text) ? "（暂无）" : text;

    private void UpdateThemeHint()
    {
        var preference = ThemeManager.Preference switch
        {
            AppTheme.Dark => "深色",
            AppTheme.Light => "浅色",
            _ => "跟随系统"
        };
        CurrentThemeText.Text = $"当前：{preference}（实际{(ThemeManager.IsDark ? "深色" : "浅色")}）";
    }

    /// <summary>
    /// 按会话身份显隐「账号设置」标签页上的入口。**只做显隐**：
    /// 真正的权限由服务端裁定（非管理员发起管理员请求会被拒），客户端自显自隐不构成放行。
    /// </summary>
    private void ApplyAccountSection()
    {
        var loggedIn = Client.IsLoggedIn;
        var isAdmin = Client.IsAdmin;
        var (changeEnabled, serverVisible) = AccountEntryVisibility(loggedIn, isAdmin);

        ChangePasswordButton.IsEnabled = changeEnabled;
        ServerSettingsButton.Visibility = serverVisible ? Visibility.Visible : Visibility.Collapsed;

        AccountHint.Text = !loggedIn
            ? "登录后可以在这里修改密码；管理员还能远程编辑服务器上的数据资源。"
            : isAdmin
                ? "已登录：可以修改自己的密码；作为管理员，还能远程编辑服务器上的数据资源（下一局生效）。"
                : "已登录：可以在这里修改自己的密码。";
    }

    /// <summary>
    /// 账号入口的显隐规则（**纯函数**，便于断言「登录后可用 / 仅管理员可见」）：
    /// 「修改密码…」登录后可用；「服务器设置…」仅管理员可见。
    /// 客户端**只做显隐**，真正的权限由服务端二次校验（非管理员发起管理员 op 会被拒）。
    /// </summary>
    public static (bool ChangePasswordEnabled, bool ServerSettingsVisible) AccountEntryVisibility(bool loggedIn, bool isAdmin)
        => (loggedIn, isAdmin);

    // ==================== 交互 ====================

    private void Theme_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<AppTheme>(tag, ignoreCase: true, out var theme)) return;

        ThemeManager.SetTheme(theme);   // 立即生效并持久化
        UpdateThemeHint();
    }

    private void View_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var state = UiStateStore.State;
        state.ShowPlayers = ShowPlayers.IsChecked == true;
        state.ShowChat = ShowChat.IsChecked == true;
        state.ShowActions = ShowActions.IsChecked == true;

        // v2.6.0 S7：动效开关只影响自己（不触发 ViewOptions 广播）。
        state.PageTransitionAnimation = PageAnimation.IsChecked == true;
        UiStateStore.Save();

        // 房间页据此同步「视图」菜单的勾选与三栏布局（架构 §6.2 的订阅/退订成对）。
        ViewOptions.Notify();
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "将把所有设置恢复为默认值（主题跟随系统、三栏全部显示、清除记住的服务器/用户名/房间号）。\n\n确定继续吗？",
            "恢复默认设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        UiStateStore.Reset();
        ThemeManager.ReloadFromState();

        _loading = true;
        LoadFromState();
        _loading = false;

        ViewOptions.Notify();
    }

    /// <summary>底部「关闭」= 出栈（不是关程序：不退出房间、不断连接、不停服务器）。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>导航栏「返回」：与「关闭」同一条路径。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    // ==================== 账号浮层 ====================

    /// <summary>「修改密码…」：打开浮层，正文是三框的 <see cref="ChangePasswordDialog"/>。</summary>
    private void ChangePassword_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Client.IsLoggedIn) return;

        _ssDialog = null;
        var dialog = new ChangePasswordDialog();
        dialog.ResetFields();
        _cpDialog = dialog;
        _overlayMode = AccountOverlayMode.ChangePassword;

        // 三框都填、两遍一致时「确定」才可点（视图模型算出来，界面只跟随）。
        dialog.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChangePasswordViewModel.CanSubmit))
                AccountOverlay.PrimaryEnabled = dialog.ViewModel.CanSubmit;
        };

        AccountOverlay.DialogTitle = "修改密码";
        AccountOverlay.DialogBody = dialog;
        AccountOverlay.PrimaryButtonText = "确定";
        AccountOverlay.SecondaryButtonText = "关闭";
        AccountOverlay.PrimaryEnabled = dialog.ViewModel.CanSubmit;
        AccountOverlay.ShowDialog();

        dialog.FocusFirstField();
    }

    /// <summary>「服务器设置…」（仅管理员）：打开浮层，正文是资源编辑器 <see cref="ServerSettingsDialog"/>。</summary>
    private void ServerSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Client.IsAdmin) return;

        _cpDialog = null;
        var dialog = new ServerSettingsDialog();
        dialog.Reset();
        _ssDialog = dialog;
        _overlayMode = AccountOverlayMode.ServerSettings;

        dialog.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ServerSettingsViewModel.CanSave)
                or nameof(ServerSettingsViewModel.IsBusy))
                AccountOverlay.PrimaryEnabled = dialog.ViewModel.CanSave;
        };

        AccountOverlay.DialogTitle = "服务器设置";
        AccountOverlay.DialogBody = dialog;
        AccountOverlay.PrimaryButtonText = "保存";
        AccountOverlay.SecondaryButtonText = "关闭";
        AccountOverlay.PrimaryEnabled = false;
        AccountOverlay.ShowDialog();

        _ = LoadServerSettingsAsync(dialog);
    }

    /// <summary>拉取资源清单并自动载入第一份资源（失败时浮层里已显示人话原因）。</summary>
    private async Task LoadServerSettingsAsync(ServerSettingsDialog dialog)
    {
        try
        {
            await dialog.LoadAsync(Client);
        }
        catch
        {
            // 网络异常由对话框自己的视图模型写进 Message（这里只保证不把异常抛到 UI 线程）。
        }

        if (_ssDialog == dialog) AccountOverlay.PrimaryEnabled = dialog.ViewModel.CanSave;
    }

    /// <summary>浮层主按钮：按当前业务分派（修改密码 → 提交；服务器设置 → 保存）。</summary>
    private async void AccountOverlay_OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_overlayMode == AccountOverlayMode.ChangePassword && _cpDialog != null)
        {
            AccountOverlay.PrimaryEnabled = false;
            var ok = await _cpDialog.ViewModel.SubmitAsync(Client);

            // 成功 / 失败都以服务端文案为准，就地显示；成功时视图模型已清空输入框。
            AccountOverlay.Message = _cpDialog.ViewModel.Message;
            AccountOverlay.MessageIsError = _cpDialog.ViewModel.MessageIsError;
            AccountOverlay.PrimaryEnabled = _cpDialog.ViewModel.CanSubmit;
            if (ok) AccountOverlay.SecondaryButtonText = "关闭";
            return;
        }

        if (_overlayMode == AccountOverlayMode.ServerSettings && _ssDialog != null)
        {
            AccountOverlay.PrimaryEnabled = false;
            await _ssDialog.SaveAsync(Client);

            AccountOverlay.Message = _ssDialog.ViewModel.Message;
            AccountOverlay.MessageIsError = _ssDialog.ViewModel.MessageIsError;
            AccountOverlay.PrimaryEnabled = _ssDialog.ViewModel.CanSave;
        }
    }

    /// <summary>浮层次按钮 / 点遮罩：关闭浮层并复位业务状态。</summary>
    private void AccountOverlay_OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        AccountOverlay.CloseDialog();
        _overlayMode = AccountOverlayMode.None;
        _cpDialog = null;
        _ssDialog = null;
    }
}
