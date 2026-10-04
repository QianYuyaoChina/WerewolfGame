using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.Views;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI;

/// <summary>
/// 主窗口：只负责“登录成功之后”的界面。
/// 布局拆成 <see cref="PlayerPanel"/> / <see cref="ChatPanel"/> / <see cref="ActionPanel"/> 三个组件，
/// 登录与连接由 <see cref="LoginWindow"/> 负责，窗口本身只处理生命周期、菜单与焦点。
/// </summary>
public partial class MainWindow : Window
{
    private readonly GameViewModel _vm;

    /// <summary>是否为“主动退出房间”导致的关闭（区别于直接关窗退出程序）。</summary>
    public bool IsLoggedOut { get; private set; }

    public MainWindow(GameClient session)
    {
        InitializeComponent();

        _vm = new GameViewModel(session);
        DataContext = _vm;

        _vm.SessionEnded += OnSessionEnded;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreUiState();
        _vm.OnLoaded();
        ChatView.FocusChatInput();
    }

    // ==================== 界面状态持久化 ====================

    /// <summary>恢复上次的窗口几何与三栏显隐。</summary>
    private void RestoreUiState()
    {
        var state = UiStateStore.State;

        MenuTogglePlayers.IsChecked = state.ShowPlayers;
        MenuToggleChat.IsChecked = state.ShowChat;
        MenuToggleActions.IsChecked = state.ShowActions;
        ApplyPanelVisibility();

        var width = state.Width >= MinWidth ? state.Width : Width;
        var height = state.Height >= MinHeight ? state.Height : Height;
        Width = width;
        Height = height;

        // 只在虚拟桌面的可见区域内恢复位置，避免显示器变更后窗口跑到屏幕外。
        if (state.Left is { } left && state.Top is { } top && IsOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (state.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>判断一个矩形是否至少有一部分落在虚拟桌面的可见范围内。</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        const double margin = 40;

        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;

        return left + width > vLeft + margin && left < vRight - margin
            && top + height > vTop + margin && top < vBottom - margin;
    }

    /// <summary>把当前窗口几何与三栏显隐写回磁盘。</summary>
    private void SaveUiState()
    {
        var state = UiStateStore.State;

        state.ShowPlayers = MenuTogglePlayers.IsChecked;
        state.ShowChat = MenuToggleChat.IsChecked;
        state.ShowActions = MenuToggleActions.IsChecked;
        state.Maximized = WindowState == WindowState.Maximized;

        // 最大化 / 最小化时 RestoreBounds 才是“正常尺寸”，是恢复时应采用的值。
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (!bounds.IsEmpty && !double.IsNaN(bounds.Left) && bounds.Width > 0 && bounds.Height > 0)
        {
            state.Left = bounds.Left;
            state.Top = bounds.Top;
            state.Width = bounds.Width;
            state.Height = bounds.Height;
        }

        UiStateStore.Save();
    }

    /// <summary>退出房间：关闭本窗口，由 <see cref="App"/> 重新显示登录窗口。</summary>
    private void OnSessionEnded()
    {
        IsLoggedOut = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }

    /// <summary>直接关闭窗口时尽力断开 SignalR 连接（同步等待，超时即放手让进程退出）。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 无论主动退出房间还是直接关窗，都先记住界面状态。
        SaveUiState();

        if (IsLoggedOut) return;   // 退出房间时连接已经断开

        try
        {
            _vm.DisposeAsync().Wait(TimeSpan.FromMilliseconds(400));
        }
        catch
        {
            // 关闭阶段的异常无需打扰用户。
        }
    }

    // ==================== 菜单：游戏 ====================

    /// <summary>退出程序：关闭全部窗口（会走 OnClosing 断开连接）。</summary>
    private void Menu_Exit_OnClick(object sender, RoutedEventArgs e) => RequestExit();

    // ==================== 菜单：视图 ====================

    /// <summary>三个面板开关共用一个处理器：统一按三个开关的当前状态重排布局。</summary>
    private void Menu_TogglePanel_OnClick(object sender, RoutedEventArgs e) => ApplyPanelVisibility();

    /// <summary>
    /// 把三栏的列宽与可见性同步到菜单勾选状态。
    /// 隐藏一栏时必须同时把列宽归零，否则星号/固定列仍会占位留下空白。
    /// </summary>
    private void ApplyPanelVisibility()
    {
        var players = MenuTogglePlayers.IsChecked;
        var chat = MenuToggleChat.IsChecked;
        var actions = MenuToggleActions.IsChecked;

        PlayersColumn.Width = players ? new GridLength(286) : new GridLength(0);
        ActionsColumn.Width = actions ? new GridLength(308) : new GridLength(0);
        ChatColumn.Width = chat ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PlayersView.Visibility = players ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ActionsView.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;

        // 中栏的左右留白取决于相邻栏是否可见，避免隐藏后留下悬空的 14px 空隙。
        ChatView.Margin = new Thickness(players ? 14 : 0, 0, actions ? 14 : 0, 0);
    }

    // ==================== 菜单：帮助 ====================

    private void Menu_About_OnClick(object sender, RoutedEventArgs e) => OpenAbout();

    private void Menu_Changelog_OnClick(object sender, RoutedEventArgs e) => OpenChangelog();

    private void OpenAbout() => new AboutWindow { Owner = this }.ShowDialog();

    private void OpenChangelog() => new ChangelogWindow { Owner = this }.ShowDialog();

    // ==================== 快捷键 ====================

    /// <summary>
    /// 菜单里展示的 InputGestureText 只是“显示文本”，WPF 不会自动绑定按键，
    /// 因此这里显式处理：F1 关于、Ctrl+Q 退出、Ctrl+L 清空消息、Ctrl+G 开始游戏。
    /// </summary>
    private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1:
                OpenAbout();
                e.Handled = true;
                break;

            case Key.Q when Keyboard.Modifiers == ModifierKeys.Control:
                RequestExit();
                e.Handled = true;
                break;

            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                if (_vm.ClearLogCommand.CanExecute(null)) _vm.ClearLogCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.G when Keyboard.Modifiers == ModifierKeys.Control:
                if (_vm.CanStartGame && _vm.StartGameCommand.CanExecute(null))
                    _vm.StartGameCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>关闭全部窗口退出程序（Shutdown 会触发 <see cref="OnClosing"/> 断开连接）。</summary>
    private static void RequestExit() => Application.Current.Shutdown();
}
