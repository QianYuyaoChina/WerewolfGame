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
            SessionEnding += OnSessionEnding;
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
        /// 程序退出的**唯一收尾入口**（PRD P0-5 / 流程 C）。
        /// <c>ShutdownMode = OnExplicitShutdown</c> 且 <see cref="RunRoomLoop"/> 结束必调 <c>Shutdown()</c>，
        /// 因此登录窗口 / 房间列表窗口 / 主窗口三个关闭入口最终都汇聚到这里，一处即可全覆盖。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            AppShutdown.RunWithPump();
            base.OnExit(e);
        }

        /// <summary>系统注销 / 关机时同样触发收尾（否则内置服务器会留下孤儿进程）。</summary>
        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
            => AppShutdown.RunWithPump();   // 关闭来源：CloseIntentSource.SystemSession → 完整收尾

        /// <summary>
        /// 房间列表 → 主窗口循环：退出房间回到房间列表（连接不拆）；
        /// 关闭任一窗口则退出程序。退出程序前统一释放连接。
        /// 若登录时已通过邀请码直接进房，则跳过房间列表直接进主窗口。
        /// </summary>
        private void RunRoomLoop(GameClient session)
        {
            // 会话登记进统一收尾：退出房间 → 断开连接 → 停内置服务器都由 AppShutdown 负责。
            AppShutdown.AttachSession(session);

            try
            {
                while (true)
                {
                    if (!session.IsJoined)   // 邀请码可能已经把我们带进房间了
                    {
                    // 关闭来源：CloseIntentSource.RoomListWindow → 完整收尾（退房间 → 断连 → 停服务器）。
                    var list = new RoomListWindow(session);
                    if (list.ShowDialog() != true) break;   // 关闭房间列表 → 退出程序
                    }

                    // 关闭来源：CloseIntentSource.RoomWindow → 只退出房间（不停服务器 / 不退出程序），
                    // 关窗后回到本循环的房间列表；循环只有在这里退出时才会走 finally 的完整收尾。
                    var main = new MainWindow(session);
                    main.ShowDialog();
                    if (!main.IsLoggedOut) break;           // 直接关窗 → 退出程序；否则回到房间列表
                }
            }
            finally
            {
                // 这里**不能**提前释放连接：否则「退出房间」请求发不出去（PRD E-10）。
                // 统一交给 AppShutdown（OnExit 也会调，幂等）。
                AppShutdown.RunWithPump();
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
