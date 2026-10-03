using System.Threading;
using System.Windows;
using ClientGUI.Services;
using ClientGUI.Views;

namespace ClientGUI.Tests;

/// <summary>
/// 界面冒烟检查（<c>--ui</c>）：在 STA 线程上实例化登录窗口与主窗口，
/// 只验证 XAML 能被解析、主题资源（pack URI 字典）都能找到，不真正显示界面。
/// </summary>
public static class UiSmokeCheck
{
    public static void Run()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                Try("LoginWindow", () => new LoginWindow());
                Try("MainWindow", () => new MainWindow(new GameClient()));

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
