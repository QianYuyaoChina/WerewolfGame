using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.Views;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 界面冒烟检查（<c>--ui</c>）：在 STA 线程上实例化登录窗口、主窗口与两个信息窗口，
/// 并对主窗口做一次真实布局、展开各下拉菜单 —— 只验证 XAML 能被解析、
/// 主题资源（pack URI 字典）与菜单模板都能正确应用，不真正显示界面。
/// </summary>
public static class UiSmokeCheck
{
    public static void Run()
    {
        // 冒烟会真正 Show() 主窗口，触发界面状态的读取与写回；
        // 关闭持久化，避免测试覆盖用户真实的界面偏好。
        UiStateStore.Enabled = false;

        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                // 版本号会显示在「关于」窗口与状态栏：不能带 SDK 附加的 git 提交号。
                Check.True(AppInfo.Version.Length > 0 && !AppInfo.Version.Contains('+'),
                    $"UI：版本号界面友好（{AppInfo.Version}）");

                Try("LoginWindow", () => new LoginWindow());
                Try("RoomListWindow", () => new RoomListWindow(new GameClient()));
                Try("CreateRoomWindow", () => new CreateRoomWindow());
                Try("MainWindow", () => new MainWindow(new GameClient()));
                Try("AboutWindow", () => new AboutWindow());
                Try("ChangelogWindow", () => new ChangelogWindow());

                ExerciseMainWindow();

                app.Shutdown();
            }
            catch (Exception ex)
            {
                Console.WriteLine("   · 初始化失败：" + ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// 真正显示一次主窗口并展开各下拉菜单，强制 Popup 与其内部项模板被实例化。
    /// 环境不允许显示窗口时退化为“只布局”，不视为失败。
    /// </summary>
    private static void ExerciseMainWindow()
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new GameClient());

            if (window.Content is FrameworkElement root)
            {
                root.Measure(new Size(1220, 780));
                root.Arrange(new Rect(0, 0, 1220, 780));
                root.UpdateLayout();
            }
            Check.True(true, "UI：主窗口布局（含菜单栏）无异常");

            // Menu 是逻辑子节点，用逻辑树查找不依赖视觉树是否已生成。
            var menu = FindLogical<Menu>(window);
            Check.True(menu != null, "UI：主窗口存在菜单栏");
            if (menu == null) return;

            // 未显示的窗口没有呈现源，Popup 不会实例化内部条目；尝试真正显示。
            var shown = false;
            try
            {
                // 移到屏幕外显示：只为拿到呈现源，避免在用户屏幕上闪现窗口。
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowActivated = false;
                window.Left = -32000;
                window.Top = -32000;
                window.Show();
                window.UpdateLayout();
                shown = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("   · 环境无法显示窗口，跳过下拉内容校验：" + ex.Message);
            }

            var opened = 0;
            var realized = 0;
            var failures = 0;
            foreach (var item in menu.Items)
            {
                if (item is not MenuItem top) continue;
                try
                {
                    top.IsSubmenuOpen = true;
                    Pump();
                    window.UpdateLayout();

                    // 容器已生成 == 下拉里的项模板（叶子模板）确实被应用。
                    if (top.ItemContainerGenerator.ContainerFromIndex(0) != null) realized++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("   · 展开下拉菜单失败：" + ex);
                    failures++;
                }
                finally
                {
                    top.IsSubmenuOpen = false;
                }
                opened++;
            }

            Check.True(opened >= 3, $"UI：菜单栏包含 {opened} 个下拉菜单（≥3）");
            Check.Equal(0, failures, "UI：展开各下拉菜单均无异常");

            if (shown)
                Check.Equal(opened, realized, "UI：各下拉菜单的项模板均已实例化");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 主窗口布局/菜单展开失败：" + ex);
            Check.True(false, "UI：主窗口布局与菜单展开无异常");
        }
        finally
        {
            try { window?.Close(); } catch { /* 关闭失败无需处理 */ }
        }
    }

    /// <summary>把 Dispatcher 队列里已有的工作跑完（相当于一次 DoEvents）。</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T? FindLogical<T>(DependencyObject treeRoot) where T : class
    {
        if (treeRoot is T match) return match;

        foreach (var child in LogicalTreeHelper.GetChildren(treeRoot))
        {
            if (child is not DependencyObject node) continue;
            var found = FindLogical<T>(node);
            if (found != null) return found;
        }
        return null;
    }

    private static void Try(string name, Func<Window> create)
    {
        try
        {
            var window = create();
            Check.True(window != null, $"UI：{name} 可实例化");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · {name} 失败：{ex}");
            Check.True(false, $"UI：{name} 实例化无异常");
        }
    }
}
