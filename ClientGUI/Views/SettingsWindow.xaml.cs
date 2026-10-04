using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClientGUI.Services;

namespace ClientGUI.Views;

/// <summary>
/// 设置窗口：集中管理所有客户端设置（外观主题、主界面视图、上次使用的连接信息）。
///
/// 它可以在**任意界面**打开（包括登录时）：
/// <list type="bullet">
///   <item>全局快捷键 Ctrl + , —— 由 <see cref="App"/> 注册的 Window 类级处理器统一接管；</item>
///   <item>主窗口菜单「视图 → 设置…」、登录窗口与房间列表窗口上的「设置」按钮。</item>
/// </list>
/// 同一时刻只允许开一个：重复打开会把已有的窗口激活到前台。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>主界面「三栏显隐」被改动（主窗口据此同步菜单勾选与布局）。</summary>
    public static event Action? ViewOptionsChanged;

    /// <summary>载入或恢复默认设置时，屏蔽控件事件，避免把「回填」当成「用户改动」。</summary>
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();

        _loading = true;
        LoadFromState();
        _loading = false;
    }

    /// <summary>打开设置窗口（已打开则激活到前台）。<paramref name="owner"/> 不可用时自动降级为无属主。</summary>
    public static void Open(Window? owner)
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
                : WindowStartupLocation.CenterScreen
        };

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
}
