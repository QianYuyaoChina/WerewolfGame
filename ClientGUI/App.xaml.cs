using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.Views;
using Werewolf.Client.Share;

namespace ClientGUI
{
    /// <summary>
    /// 应用程序入口：负责界面流程（登录 → 房间列表 → 主窗口 → 退出房间回房间列表）
    /// 与未处理异常的统一兜底，避免界面直接崩溃退出。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 界面流程自己控制：登录窗口关闭不代表程序结束。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 先把主题颜色挂到 Application 级，再创建任何窗口（否则第一帧没有配色）。
            ThemeManager.Initialize();

            // 全局快捷键：任意窗口都能按 Ctrl+, 打开设置。
            RegisterGlobalShortcuts();

            RegisterExceptionHandlers();
            ShowLogin();
        }

        // ==================== 全局快捷键 ====================

        /// <summary>
        /// 给所有 <see cref="Window"/> 注册类级 PreviewKeyDown：
        /// 登录窗口、房间列表、主窗口乃至各个小对话框里都能随时按 <c>Ctrl + ,</c> 打开设置。
        /// 用 Preview 是为了先于获得焦点的控件拿到按键（避免被输入框吞掉）。
        /// </summary>
        private static void RegisterGlobalShortcuts()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                UIElement.PreviewKeyDownEvent,
                new KeyEventHandler(OnAnyWindowPreviewKeyDown));
        }

        private static void OnAnyWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsSettingsShortcut(e.Key, Keyboard.Modifiers)) return;

            e.Handled = true;   // 别让 , 落到输入框里
            SettingsWindow.Open(sender as Window);
        }

        /// <summary>
        /// 是否命中「打开设置」快捷键（<c>Ctrl + ,</c>）。
        /// 抽成纯函数是为了可测：真正的 Ctrl 按键状态在自动化测试里伪造不出来。
        /// </summary>
        public static bool IsSettingsShortcut(Key key, ModifierKeys modifiers)
            => key == Key.OemComma && modifiers == ModifierKeys.Control;

        // ==================== 界面流程 ====================

        /// <summary>显示登录窗口；登录成功则进入房间列表，否则结束程序。</summary>
        private void ShowLogin()
        {
            var login = new LoginWindow();
            var loggedIn = login.ShowDialog() == true;

            if (loggedIn && login.Session != null)
            {
                RunRoomLoop(login.Session);
                return;
            }

            Shutdown();
        }

        /// <summary>
        /// 房间列表 → 主窗口循环：退出房间回到房间列表（连接不拆）；
        /// 关闭任一窗口则退出程序。退出程序前统一释放连接。
        /// 若登录时已通过邀请码直接进房，则跳过房间列表直接进主窗口。
        /// </summary>
        private void RunRoomLoop(GameClient session)
        {
            try
            {
                while (true)
                {
                    if (!session.IsJoined)   // 邀请码可能已经把我们带进房间了
                    {
                        var list = new RoomListWindow(session);
                        if (list.ShowDialog() != true) break;   // 关闭房间列表 → 退出程序
                    }

                    var main = new MainWindow(session);
                    main.ShowDialog();
                    if (!main.IsLoggedOut) break;           // 直接关窗 → 退出程序；否则回到房间列表
                }
            }
            finally
            {
                try { session.DisposeAsync().AsTask().Wait(TimeSpan.FromMilliseconds(400)); }
                catch { /* 忽略关闭异常 */ }
            }

            Shutdown();
        }

        // ==================== 异常兜底 ====================

        private void RegisterExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    "发生未处理的错误：\n\n" + args.Exception.Message,
                    "狼人杀客户端",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    MessageBox.Show(
                        "发生严重错误：\n\n" + ex.Message,
                        "狼人杀客户端",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        }
    }
}
