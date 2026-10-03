using System.ComponentModel;
using System.Windows;
using ClientGUI.Services;
using ClientGUI.Views;
using ClientGUI.ViewModels;

namespace ClientGUI;

/// <summary>
/// 主窗口：只负责“登录成功之后”的界面。
/// 布局拆成 <see cref="PlayerPanel"/> / <see cref="ChatPanel"/> / <see cref="ActionPanel"/> 三个组件，
/// 登录与连接由 <see cref="LoginWindow"/> 负责，窗口本身只处理生命周期与焦点。
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
        _vm.OnLoaded();
        ChatView.FocusChatInput();
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
}
