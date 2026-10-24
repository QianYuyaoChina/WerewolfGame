using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;

namespace ClientGUI
{
    /// <summary>
    /// 应用程序入口：负责界面流程（唯一外壳窗口 + 页面栈）
    /// 与未处理异常的统一兜底，避免界面直接崩溃退出。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 界面流程自己控制：关掉外壳窗口不代表程序结束（收尾由 AppShutdown 编排）。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 先把主题颜色挂到 Application 级，再创建任何窗口（否则第一帧没有配色）。
            ThemeManager.Initialize();

            // 全局快捷键：任意窗口都能按 Ctrl+, 打开设置。
            RegisterGlobalShortcuts();

            RegisterExceptionHandlers();
            SessionEnding += OnSessionEnding;

            // v2.6.0 S1（架构 §5.3）：界面流程改为「唯一外壳窗口 + 页面栈」。
            // 根页是登录页（ShellWindow.OnLoaded 里 Reset(Login)），登录 / 进房间 / 返回
            // 全部由 NavigationService 换页，App 不再驱动任何 ShowDialog 循环。
            //
            // v2.6.0 S4：旧的「开关式回退点」（ShowLogin / RunRoomLoop + 10 个旧 Window）
            // 已随窗口类一起删除 —— 回退边界到此为止（架构 §10.3：只向前修复）。
            new ShellWindow().Show();
        }

        // ==================== 全局快捷键 ====================

        /// <summary>
        /// 给所有 <see cref="Window"/> 注册类级 PreviewKeyDown：任意页面（含页面内浮层）里
        /// 都能随时按 <c>Ctrl + ,</c> 打开设置。
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

            // v2.6.0 S2：外壳窗口里的设置是**浮层页**（任意页面都能开，Esc / × 收起一层）。
            // 用 PushOverlay 而不是 Push：从房间页打开设置时，房间页不能退场
            //（退场会 Detach 掉 GameViewModel 并注销收尾用的「退出房间」委托 —— 待办 T41）。
            //
            // v2.6.0 S4：只剩唯一外壳窗口，旧窗口那条 "SettingsWindow.Open(...)" 兜底已删除。
            if (sender is ShellWindow shell)
                shell.Session.Navigation.PushOverlay(new PageRequest(PageId.Settings));
        }

        /// <summary>
        /// 是否命中「打开设置」快捷键（<c>Ctrl + ,</c>）。
        /// 抽成纯函数是为了可测：真正的 Ctrl 按键状态在自动化测试里伪造不出来。
        /// </summary>
        public static bool IsSettingsShortcut(Key key, ModifierKeys modifiers)
            => key == Key.OemComma && modifiers == ModifierKeys.Control;

        // ==================== 收尾 ====================

        /// <summary>
        /// 程序退出的**唯一收尾入口**（PRD P0-5 / 流程 C）。
        /// <c>ShutdownMode = OnExplicitShutdown</c>，因此外壳窗口关闭 / 菜单退出 / 系统注销
        /// 最终都汇聚到这里，一处即可全覆盖。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            AppShutdown.RunWithPump();
            base.OnExit(e);
        }

        /// <summary>系统注销 / 关机时同样触发收尾（否则内置服务器会留下孤儿进程）。</summary>
        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
            => AppShutdown.RunWithPump();   // 关闭来源：CloseIntentSource.SystemSession → 完整收尾

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
