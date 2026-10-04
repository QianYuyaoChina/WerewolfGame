using System.Windows;
using System.Windows.Threading;
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

            RegisterExceptionHandlers();
            ShowLogin();
        }

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
        /// </summary>
        private void RunRoomLoop(GameClient session)
        {
            try
            {
                while (true)
                {
                    var list = new RoomListWindow(session);
                    if (list.ShowDialog() != true) break;   // 关闭房间列表 → 退出程序

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
