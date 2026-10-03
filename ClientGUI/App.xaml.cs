using System.Windows;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.Views;

namespace ClientGUI
{
    /// <summary>
    /// 应用程序入口：负责界面流程（登录窗口 → 主窗口 → 可回到登录窗口）
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

        /// <summary>显示登录窗口；登录成功则进入主窗口，否则结束程序。</summary>
        private void ShowLogin()
        {
            var login = new LoginWindow();
            var loggedIn = login.ShowDialog() == true;

            if (loggedIn && login.Session != null)
            {
                ShowMain(login.Session);
                return;
            }

            Shutdown();
        }

        /// <summary>用登录成功的会话打开主窗口；退出房间后回到登录窗口。</summary>
        private void ShowMain(GameClient session)
        {
            var main = new MainWindow(session);
            main.Closed += (_, _) =>
            {
                if (main.IsLoggedOut) ShowLogin();
                else Shutdown();
            };
            main.Show();
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
