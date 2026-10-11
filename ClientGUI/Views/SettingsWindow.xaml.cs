using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 设置窗口：集中管理所有客户端设置（外观主题、主界面视图、上次使用的连接信息），
/// 并在 v2.5.0 第 5 / 7 条起承载「修改密码…」与「服务器设置…」两个账号入口。
///
/// 它可以在**任意界面**打开（包括登录时）：
/// <list type="bullet">
///   <item>全局快捷键 Ctrl + , —— 由 <see cref="App"/> 注册的 Window 类级处理器统一接管；</item>
///   <item>主窗口菜单「视图 → 设置…」、登录窗口与房间列表窗口上的「设置」按钮。</item>
/// </list>
/// 同一时刻只允许开一个：重复打开会把已有的窗口激活到前台。
///
/// 账号入口显隐由**会话身份**决定（<see cref="GameClient.IsAdmin"/> 是服务端回执写入的只读值）：
/// 「修改密码…」登录后可用；「服务器设置…」仅管理员可见。服务端仍会二次校验，客户端只做显隐。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>主界面「三栏显隐」被改动（主窗口据此同步菜单勾选与布局）。</summary>
    public static event Action? ViewOptionsChanged;

    /// <summary>载入或恢复默认设置时，屏蔽控件事件，避免把「回填」当成「用户改动」。</summary>
    private bool _loading;

    /// <summary>本次打开所带的会话（可为 null —— 登录前打开设置时就没有会话）。</summary>
    private GameClient? _session;

    /// <summary>账号浮层当前承载的业务（修改密码 / 服务器设置 / 无）。</summary>
    private enum AccountOverlayMode { None, ChangePassword, ServerSettings }

    private AccountOverlayMode _overlayMode = AccountOverlayMode.None;
    private ChangePasswordDialog? _cpDialog;
    private ServerSettingsDialog? _ssDialog;

    public SettingsWindow()
    {
        InitializeComponent();

        _loading = true;
        LoadFromState();
        _loading = false;

        ApplyAccountSection();
    }

    /// <summary>打开设置窗口（已打开则激活到前台）。<paramref name="owner"/> 不可用时自动降级为无属主。</summary>
    public static void Open(Window? owner) => Open(owner, ResolveSession(owner));

    /// <summary>
    /// 打开设置窗口（带会话）。会话决定「账号设置」标签页上的入口显隐：
    /// 已登录 → 可改密码；管理员 → 多一个「服务器设置…」。
    /// </summary>
    public static void Open(Window? owner, GameClient? session)
    {
        if (_open != null)
        {
            try { _open.Activate(); } catch { /* 激活失败无需处理 */ }
            return;
        }

        var window = new SettingsWindow
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            WindowStartupLocation = owner is { IsLoaded: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            _session = session
        };
        window.ApplyAccountSection();

        _open = window;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _open = null;
        }
    }

    /// <summary>
    /// 从打开设置的窗口里取会话（Ctrl + , 的全局入口只拿得到 owner 窗口）。
    /// 认识的三个窗口各自暴露只读 <c>Session</c>；其余窗口（对话框等）没有会话。
    /// </summary>
    private static GameClient? ResolveSession(Window? owner) => owner switch
    {
        LoginWindow login => login.Session,
        RoomListWindow list => list.Session,
        MainWindow main => main.Session,
        _ => null
    };

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
        var loggedIn = _session is { IsLoggedIn: true };
        var isAdmin = _session is { IsAdmin: true };
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
        UiStateStore.Save();

        ViewOptionsChanged?.Invoke();
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

        ViewOptionsChanged?.Invoke();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // ==================== 账号浮层（v2.5.0 第 5 / 7 条） ====================

    /// <summary>「修改密码…」：打开浮层，正文是三框的 <see cref="ChangePasswordDialog"/>。</summary>
    private void ChangePassword_OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is not { IsLoggedIn: true }) return;

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
        if (_session is not { IsAdmin: true }) return;

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
        if (_session == null) return;
        await dialog.LoadAsync(_session);
        AccountOverlay.PrimaryEnabled = dialog.ViewModel.CanSave;
    }

    /// <summary>浮层主按钮：按当前业务分派（修改密码 → 提交；服务器设置 → 保存）。</summary>
    private async void AccountOverlay_OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;

        if (_overlayMode == AccountOverlayMode.ChangePassword && _cpDialog != null)
        {
            AccountOverlay.PrimaryEnabled = false;
            var ok = await _cpDialog.ViewModel.SubmitAsync(_session);

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
            await _ssDialog.SaveAsync(_session);

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
