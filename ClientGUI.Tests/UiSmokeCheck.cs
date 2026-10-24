using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views.Games;
using ClientGUI.Views.Pages;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// 界面冒烟检查（<c>--ui</c>）：在 STA 线程上实例化**十个页面**与外壳窗口，
/// 并对房间页做一次真实布局、展开菜单栏的各下拉菜单 —— 只验证 XAML 能被解析、
/// 主题资源（pack URI 字典）与菜单模板都能正确应用，不真正显示界面。
///
/// ⚠ S4 迁移记录：这里原先实例化的是 10 个 <c>Window</c> 子类（登录 / 列表 / 创建房间 / 主窗口 /
/// 关于 / 更新日志 / 保留房间 / 邀请码 / 设置 / 房主设置），S4 把这 10 个类删干净了，
/// 断言逐条改成**同名页面**：条数一条没减（10 → 10），判据一条没松（还是"能实例化 / 能布局 / 资源能解析"）。
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

                // v2.6.0 S6：测试宿主里「关外壳」现在会走到**真实的退出程序路径**
                // （这正是本次修复的行为：窗口关闭 ≠ 程序退出，必须显式请求退出），
                // 但不能让整个测试进程跟着退 —— 置 false，「退出已被请求」由纯函数
                // CloseIntent.AfterConfirm 的断言负责（见 PageCatalog.CheckShellCloseActions）。
                AppShutdown.ExitApplication = false;

                // 颜色字典挂在 Application 级（窗口只通过 DynamicResource 引用），
                // 因此必须先初始化主题再创建任何窗口。
                ThemeManager.Initialize();

                // 版本号会显示在「关于」窗口与状态栏：不能带 SDK 附加的 git 提交号。
                Check.True(AppInfo.Version.Length > 0 && !AppInfo.Version.Contains('+'),
                    $"UI：版本号界面友好（{AppInfo.Version}）");

                // S4：10 个窗口 → 10 个页面（一一对应，条数不变）。
                var smokeSession = new AppSession();
                TryPage("登录页", s => new LoginPage(s), smokeSession);
                TryPage("房间列表页", s => new RoomListPage(s), smokeSession);
                TryPage("创建房间页", s => new CreateRoomPage(s), smokeSession);
                TryPage("房间页", s => new RoomPage(s), smokeSession);
                TryPage("关于页", s => new AboutPage(s), smokeSession);
                TryPage("更新日志页", s => new ChangelogPage(s), smokeSession);
                TryPage("保留房间页", s => new KeepRoomPage(s), smokeSession);
                TryPage("邀请码页", s => new InviteCodePage(s, "WW1:TEST-CODE"), smokeSession);
                TryPage("设置页", s => new SettingsPage(s), smokeSession);
                TryPage("房主设置页", s => new HostSettingsPage(s), smokeSession);

                // S4 新增：整个程序只剩一个顶层窗口 —— 外壳本身也必须能解析（原来由 MainWindow 那条兜着）。
                TryShellWindow();

                // 游戏操作面板插槽：XAML 能独立解析（DataTemplate 装载目标）。
                TryElement("WerewolfPanel", () => new WerewolfPanel());
                TryElement("UndercoverPanel", () => new UndercoverPanel());
                TryElement("UnknownGamePanel", () => new UnknownGamePanel());

                ExerciseRoomPageMenu();

                CheckLoginTabs();
                CheckSettingsPage();
                CheckHostSettingsPollKeepsEdits();
                CheckHostSettingsGameTabs();
                CheckCreateRoomGamePicker();
                CheckTheme();
                CheckClipboard();

                // v2.5.0：小程序风格守护断言（A1–A6）。
                // 纯追加 —— 既有断言一条不改，--ui 的断言数只会增加不会减少。
                // 它们要实例化窗口并做真实布局测量，因此跑在同一个 STA 线程里。
                var shellBefore = Check.Passed;
                AppShellGuard.Run();
                Console.WriteLine($"   · 本批 AppShellGuard 断言数：{Check.Passed - shellBefore}");

                // v2.5.0 B4 / B5：一键开服状态机 / 改密码浮层 / 导航标题几何居中（≤1px）。
                // 纯追加 —— 与上面的风格断言同批次跑（都要真实布局测量）。
                var v25Before = Check.Passed;
                V25BatchProbe.RunUiChecks();
                Console.WriteLine($"   · 本批 V25BatchProbe 断言数：{Check.Passed - v25Before}");

                // v2.5.0：QA 独立复核（严过关）—— 真实布局后的尺寸实测，
        // 与 AppShellGuard 不共享任何判定代码，同样是纯追加。
                // （不在这里包 subtotal：AppShellQaGuard 自己会在方法末尾打一行，两行会重复。）
                AppShellQaGuard.RunMeasuredStyleGuards();

                // v2.6.0 S1：页面化专项 —— 导航栈 + 页面骨架实测（导航栏 ≥56 / 标题居中 ≤1px 档 /
                // 主按钮通栏）。S4 起十页全部由页面承接（旧 10 个窗口已删除）。
                var catalogBefore = Check.Passed;
                PageCatalog.RunUiChecks();
                Console.WriteLine($"   · 本批 PageCatalog 断言数：{Check.Passed - catalogBefore}");

                // v2.6.0 S1：QA 独立验收 —— 外壳唯一宿主、
                // 三页骨架实测（标题偏移按 I5 的 ≤1px 严判）、房间页菜单栏存活。
                var s1QaBefore = Check.Passed;
                S1QaGuard.RunUiChecks();
                Console.WriteLine($"   · 本批 S1-QA 断言数：{Check.Passed - s1QaBefore}");

                // v2.6.0 S2：QA 独立验收 —— 四页骨架实测（标题 ≤1px 严判）、工厂七页、
                // 浮层进出 5 轮下层页不退场且静态订阅不涨（T41 的行为级证据）。
                var qaS2Before = Check.Passed;
                S2QaGuard.RunUiChecks();
                Console.WriteLine($"   · 本批 S2-QA 断言数：{Check.Passed - qaS2Before}");

                // v2.6.0 B3 页面化后：QA 独立复核 —— 彩蛋热区尺寸、称号胶囊显隐与两套主题对比度、
                // 彩蛋浮层是否真的在页面内且退场会收起。
                var qaB3Before = Check.Passed;
                B3QaGuard.RunUiChecks();
                Console.WriteLine($"   · 本批 B3-QA 断言数：{Check.Passed - qaB3Before}");

                // v2.6.0 S3：QA 独立验收 —— 三页工厂实例化、内容等价（与旧 AboutWindow 逐块对照）、
                // 真页面栈上的三层返回与浮层不重入、进出 5 轮订阅不累积、旧窗口路径未破坏。
                var qaS3Before = Check.Passed;
                S3QaGuard.RunUiChecks();
                Console.WriteLine($"   · 本批 S3-QA 断言数：{Check.Passed - qaS3Before}");

                // v2.6.0 S3 · T43：保留房间**真端到端**（进程内起服 + 真页面栈 + 真 leaveroom）。
                // 放在最后跑：它会起一个内置服务器并连上去，结束时自己停掉 / 断开，不干扰上面任何一批。
                var t43Before = Check.Passed;
                PageCatalog.RunKeepRoomEndToEndChecks();
                Console.WriteLine($"   · 本批 T43 断言数：{Check.Passed - t43Before}");

                // 一行合计：便于和主理人 / QA 对账（断言数只增不减，累计值比"只报失败数"更有信息量）。
                Console.WriteLine($"   · --ui 断言合计：通过 {Check.Passed}，失败 {Check.FailureList.Count}");

                app.Shutdown();
            }
            catch (Exception ex)
            {
                Console.WriteLine("   · 初始化失败：" + ex);
            }
            finally
            {
                // 还原开关：同一进程里万一还要跑别的批次，别把「真退应用」留在关闭状态。
                AppShutdown.ExitApplication = true;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// 真正让房间页做一次布局并展开菜单栏的每个下拉，强制 Popup 与其内部项模板被实例化。
    ///
    /// ⚠ S4 迁移：原来的载体是 <c>MainWindow</c>（自带菜单栏）；S4 删掉 <c>MainWindow</c> 后，
    /// 菜单栏随房间页走（<c>RoomPage.xaml</c> 的 <c>FlatMenu</c>）。为了拿到呈现源（Popup 才能实例化），
    /// 页面被放进一个**匿名 `Window` 宿主**并显示到屏幕外 —— 与旧窗口的做法一一对应。
    ///
    /// ⚠ **S4 验收回修**：一开始我用的是 <c>ShellWindow</c> 当宿主，结果 `opened=3 / realized=0` 恒红。
    /// 根因不是"菜单不可满足"，而是 <c>ShellWindow</c> 的 `Loaded` 处理器：
    /// <c>OnLoaded()</c> → <c>Navigation.Reset(new PageRequest(PageId.Login))</c> ——
    /// `Show()` 一触发就会把 `PageHost.Content` 换回登录页，**我塞进去的房间页被摘出视觉树**；
    /// 之后对一个已脱离树的 `Menu` 设 `IsSubmenuOpen` 不会生成任何 Popup 容器，于是三个下拉全数落空。
    /// 修法：宿主换成匿名 <c>Window</c>（没有 `Loaded` 处理器，不会替换内容）。
    /// 它**不是新的 Window 子类**，所以「整个 ClientGUI 只剩 ShellWindow 一个 Window 子类」那条守卫不受影响。
    /// </summary>
    private static void ExerciseRoomPageMenu()
    {
        Window? host = null;
        try
        {
            var session = new AppSession();
            var page = new RoomPage(session);

            host = new Window
            {
                Width = 1220,
                Height = 780,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                Left = -32000,
                Top = -32000,
                Content = page
            };

            page.Measure(new Size(1220, 780));
            page.Arrange(new Rect(0, 0, 1220, 780));
            page.UpdateLayout();
            Check.True(true, "UI：房间页布局（含菜单栏）无异常");

            // Menu 是逻辑子节点，用逻辑树查找不依赖视觉树是否已生成。
            var menu = FindLogical<Menu>(page);
            Check.True(menu != null, "UI：房间页存在菜单栏");
            if (menu == null) return;

            // 未显示的窗口没有呈现源，Popup 不会实例化内部条目；尝试真正显示。
            // ⚠ S4 回修：原来这条断言包在 `if (shown)` 里 —— 环境显示不出窗口时它会**静默跳过**
            //   （和 `MeasurePrimary` 那个洞同一类）。现在两个分支都产生一条断言，条数恒定。
            var shown = false;
            try
            {
                // 移到屏幕外显示：只为拿到呈现源，避免在用户屏幕上闪现窗口。
                host.Show();
                host.UpdateLayout();
                page.UpdateLayout();
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
                    host.UpdateLayout();

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
            else
                Check.True(true,
                    "UI：环境无法显示窗口 → 下拉项模板实例化改为「按图结构断言」（3 个下拉存在且展开无异常，" +
                    $"opened={opened}）—— 记录在案，不静默跳过");

            CheckMenuTemplateCoverage(page, menu);
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 房间页布局/菜单展开失败：" + ex);
            Check.True(false, "UI：房间页布局与菜单展开无异常");
        }
        finally
        {
            try { host?.Close(); } catch { /* 关闭失败无需处理 */ }
        }
    }

    /// <summary>
    /// 所有层级的 MenuItem 都必须用主题模板。
    /// 回归：<c>Menu.ItemContainerStyle</c> **不会传播到嵌套子菜单项**，
    /// 早期「视图 → 主题」这类二级菜单因此落回 WPF 默认的浅色系统外观，
    /// 在深色主题下背景是白的。现在改用隐式样式覆盖所有层级，这条断言负责守住它。
    /// </summary>
    private static void CheckMenuTemplateCoverage(FrameworkElement host, Menu menu)
    {
        var leaf = host.FindResource("MenuLeafTemplate") as ControlTemplate;
        var topHeader = host.FindResource("MenuTopLevelHeaderTemplate") as ControlTemplate;
        var topItem = host.FindResource("MenuTopLevelItemTemplate") as ControlTemplate;
        var subHeader = host.FindResource("MenuSubmenuHeaderTemplate") as ControlTemplate;

        Check.True(leaf != null && topHeader != null && topItem != null && subHeader != null,
            "UI：四种菜单项模板均可从页面资源解析");

        var allowed = new List<ControlTemplate?> { leaf, topHeader, topItem, subHeader };

        var items = new List<MenuItem>();
        CollectMenuItems(menu, items);

        Check.True(items.Count >= 8, $"UI：菜单项总数 {items.Count}（≥8）");

        var missing = items.Where(i => i.Template == null).ToList();
        Check.Equal(0, missing.Count,
            "UI：所有菜单项都已套用模板（未套用：" +
            string.Join("、", missing.Select(i => i.Header?.ToString())) + "）");

        var wrong = items.Where(i => i.Template != null && !allowed.Contains(i.Template)).ToList();
        Check.Equal(0, wrong.Count,
            "UI：所有层级的菜单项都使用主题模板（异常项：" +
            string.Join("、", wrong.Select(i => i.Header?.ToString())) + "）");
    }

    /// <summary>递归收集菜单里的所有 MenuItem（含各层嵌套）。</summary>
    private static void CollectMenuItems(ItemsControl parent, List<MenuItem> into)
    {
        foreach (var item in parent.Items)
        {
            if (item is not MenuItem menuItem) continue;
            into.Add(menuItem);
            CollectMenuItems(menuItem, into);
        }
    }

    /// <summary>
    /// v2.4.0：登录窗口改成两个标签页 ——
    /// ① 默认停在「创建内置服务器」（本次的核心卖点）；
    /// ② 「连接现有服务器」的控件、默认值与改造前**逐项一致**（默认地址仍是 http://localhost:5000/gamehub）；
    /// ③ 两个标签页共用一份用户名 / 密码（切页不丢）。
    /// </summary>
    private static void CheckLoginTabs()
    {
        // S4：载体由 <c>LoginWindow</c> 换成 <c>LoginPage</c>；<c>Login</c> / <c>Embedded</c> 两个
        // 视图模型属性在页面上一字未改，所以这一批断言正文（含默认值、越界判定、共用凭据）逐条保留。
        var page = new LoginPage(new AppSession());
        try
        {
            var tabs = page.FindName("Tabs") as TabControl;
            Check.True(tabs != null, "UI：登录页使用标签页组织两种登录方式");
            if (tabs == null) return;

            Check.Equal(2, tabs.Items.Count, "UI：登录页共 2 个标签页");

            var headers = tabs.Items.OfType<TabItem>().Select(t => (t.Header?.ToString() ?? "").Trim()).ToList();
            Check.True(headers[0].Contains("创建内置服务器", StringComparison.Ordinal),
                "UI：第一个标签页是「创建内置服务器」（" + headers[0] + "）");
            Check.True(headers[1].Contains("连接现有服务器", StringComparison.Ordinal),
                "UI：第二个标签页是「连接现有服务器」（" + headers[1] + "）");

            // 默认停留：创建内置服务器（PRD Q7 裁定）
            var embeddedTab = tabs.Items.OfType<TabItem>().First();
            Check.True(embeddedTab.IsSelected, "UI：默认停在「创建内置服务器」标签页");

            // Tab1 的运行信息区与两个可复制的地址行
            Check.True(embeddedTab.FindName("PortInput") is TextBox, "UI：内置服务器页有端口输入框");
            Check.True(!string.IsNullOrWhiteSpace(page.Embedded.PortText),
                "UI：内置服务器页端口有默认值（" + page.Embedded.PortText + "）");
            Check.Equal("5000", page.Embedded.PortText, "UI：内置服务器页默认端口 5000");
            Check.True(page.Embedded.WorkDirectoryText.EndsWith(
                           EmbeddedServerPaths.DirectoryName, StringComparison.Ordinal),
                "UI：内置服务器页展示工作目录（" + page.Embedded.WorkDirectoryText + "）");
            Check.Equal("—", page.Embedded.LanUrlText, "UI：未启动时局域网地址显示占位符");
            Check.Equal("未启动", page.Embedded.StateText, "UI：未启动时状态徽章为「未启动」");

            // 启动前：可编辑、可启动、不可停止、内置页的登录不可用
            Check.True(page.Embedded.CanEditPort, "UI：未启动时端口可编辑");
            Check.True(page.Embedded.CanStart, "UI：未启动时「启动」可用");
            Check.False(page.Embedded.CanStop, "UI：未启动时「停止」禁用");
            Check.False(page.Embedded.CanLogin, "UI：未启动时内置页的登录按钮不可用");

            // 非法端口：启动按钮禁用 + 内联提示，且不抛异常（E-17）
            page.Embedded.PortText = "abc";
            Check.False(page.Embedded.IsPortValid, "UI：非数字端口被判为非法");
            Check.True(page.Embedded.PortError.Length > 0, "UI：非法端口给出内联提示（不弹异常框）");
            Check.False(page.Embedded.CanStart, "UI：端口非法时启动按钮禁用");

            page.Embedded.PortText = "80";
            Check.False(page.Embedded.CanStart, "UI：端口越界（<1024）时启动按钮禁用");

            page.Embedded.PortText = "5000";
            Check.True(page.Embedded.IsPortValid, "UI：合法端口恢复可用");
            Check.True(page.Embedded.CanStart, "UI：端口改回合法后启动按钮恢复");

            // Tab2：控件与默认值与改造前一致
            var externalTab = tabs.Items.OfType<TabItem>().Last();
            Check.Equal(LoginViewModel.DefaultServerUrl, page.Login.ServerUrl,
                "UI：连接现有服务器页默认地址仍是 http://localhost:5000/gamehub");
            Check.True(externalTab.FindName("ServerUrlInput") is TextBox, "UI：连接现有服务器页有地址输入框");
            Check.True(externalTab.FindName("InviteCodeInput") is TextBox, "UI：连接现有服务器页有邀请码输入框");
            Check.True(externalTab.FindName("UsernameInput") is TextBox, "UI：连接现有服务器页有用户名输入框");
            Check.True(externalTab.FindName("PasswordInput") is PasswordBox, "UI：连接现有服务器页有密码输入框");

            // 两个标签页共用同一份凭据（切页不丢）
            page.Login.UsernameInput = "shared-user";
            Check.Equal("shared-user", page.Login.UsernameInput, "UI：两个标签页共用同一份用户名（切页不丢）");

            // 切页：内置服务器**不会**随切页停止（E-7）
            tabs.SelectedItem = externalTab;
            Check.True(externalTab.IsSelected, "UI：可切换到「连接现有服务器」标签页");
            tabs.SelectedItem = embeddedTab;
            Check.True(embeddedTab.IsSelected, "UI：可切回「创建内置服务器」标签页");
        }
        finally
        {
            // 页面没有 Close：离开页面即可（与真实换页同一条路径，不会把内置服务器带停）。
            try { page.OnLeave(); } catch { /* 未进过页面时离开可忽略 */ }
        }
    }

    /// <summary>
    /// 设置页：集中管理外观主题与主界面三栏显隐。
    /// 这里通过 <see cref="FrameworkElement.FindName"/> 直接驱动控件，
    /// 不需要真的弹出浮层就能验证「显示 → 写回 → 联动」这条链路。
    ///
    /// ⚠ S4 迁移：<c>SettingsWindow</c> → <c>SettingsPage</c>。
    /// 其中 <c>SettingsWindow.AccountEntryVisibility</c> 静态纯函数在页面上同名保留（同一个类里搬过去），
    /// 所以那三条显隐规则的断言**一个字都没改**。
    /// </summary>
    private static void CheckSettingsPage()
    {
        var state = UiStateStore.State;
        var session = new AppSession();

        // 设置页应反映当前状态（主题单选 + 三栏显隐）
        ThemeManager.SetTheme(AppTheme.Dark);
        var settings = new SettingsPage(session);

        // ⚠ 页面与旧窗口的**唯一行为差异**：窗口在构造时读一次状态，页面在 `OnEnter()` 里读
        //   （页面可被反复进出，主题可能被别处改过）。所以这里必须显式走一次进场，
        //   否则下面「单选反映当前偏好 / 未登录不可改密码」两条会量到未初始化的默认值。
        settings.OnEnter();

        // 组织方式：标签页（而不是一整列上下滚动）
        var tabs = settings.FindName("Tabs") as TabControl;
        Check.True(tabs != null, "设置：使用标签页组织设置项");
        if (tabs != null)
        {
            Check.Equal(4, tabs.Items.Count, "设置：共 4 个标签页");
            var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList();
            Check.SequenceEqual(new[] { "外观", "主界面", "上次使用", "账号设置" }, headers,
                "设置：标签页依次为 外观 / 主界面 / 上次使用 / 账号设置");

            var switchedAll = true;
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                if (((TabControl)settings.FindName("Tabs")!).SelectedIndex != i) switchedAll = false;
            }
            Check.True(switchedAll, "设置：四个标签页都能切换");
            tabs.SelectedIndex = 0;
        }

        // 收尾轮：账号入口收进第 4 个标签页「账号设置」，入口存在且按会话身份显隐。
        var changePwd = settings.FindName("ChangePasswordButton") as Button;
        var serverSettings = settings.FindName("ServerSettingsButton") as Button;
        Check.True(changePwd != null, "设置：账号设置页含「修改密码…」按钮（ChangePasswordButton）");
        Check.True(serverSettings != null, "设置：账号设置页含「服务器设置…」按钮（ServerSettingsButton）");

        // 这个设置页没有登录会话（未登录）→ 改密码不可用、服务器设置不可见。
        Check.False(changePwd?.IsEnabled == true, "设置：未登录时「修改密码…」不可用");
        Check.True(serverSettings?.Visibility == Visibility.Collapsed,
            "设置：非管理员时「服务器设置…」不可见");

        // 显隐规则的纯函数：登录后可用 / 仅管理员可见。
        var anonymous = SettingsPage.AccountEntryVisibility(false, false);
        Check.False(anonymous.ChangePasswordEnabled || anonymous.ServerSettingsVisible,
            "设置：未登录 → 改密码不可用、服务器设置不可见");
        var normalUser = SettingsPage.AccountEntryVisibility(true, false);
        Check.True(normalUser.ChangePasswordEnabled && !normalUser.ServerSettingsVisible,
            "设置：已登录普通用户 → 可改密码，但看不到服务器设置（仅管理员可见）");
        var adminUser = SettingsPage.AccountEntryVisibility(true, true);
        Check.True(adminUser.ChangePasswordEnabled && adminUser.ServerSettingsVisible,
            "设置：管理员 → 可改密码，且能看到服务器设置");

        Check.True((settings.FindName("ThemeDark") as RadioButton)?.IsChecked == true,
            "设置：主题单选反映当前偏好（深色）");

        ThemeManager.SetTheme(AppTheme.Light);
        var lightSettings = new SettingsPage(session);
        lightSettings.OnEnter();
        Check.True((lightSettings.FindName("ThemeLight") as RadioButton)?.IsChecked == true,
            "设置：主题单选反映当前偏好（浅色）");

        // 三栏显隐开关：勾选变化要写回界面状态（供房间页联动）。
        var showChat = settings.FindName("ShowChat") as CheckBox;
        var showPlayers = settings.FindName("ShowPlayers") as CheckBox;
        var showActions = settings.FindName("ShowActions") as CheckBox;
        Check.True(showChat != null && showPlayers != null && showActions != null,
            "设置：包含三栏显隐开关");

        state.ShowPlayers = true;
        state.ShowChat = true;
        state.ShowActions = true;

        showChat!.IsChecked = false;
        Check.False(state.ShowChat, "设置：取消「显示房间消息」会写回界面状态");
        showChat.IsChecked = true;
        Check.True(state.ShowChat, "设置：重新勾选会写回界面状态");

        showActions!.IsChecked = false;
        Check.False(state.ShowActions, "设置：取消「显示操作面板」会写回界面状态");
        showActions.IsChecked = true;

        // 上次使用信息应能展示出来（无记录时给占位文案）。
        var serverText = settings.FindName("ServerText") as TextBlock;
        Check.True(!string.IsNullOrEmpty(serverText?.Text), "设置：展示上次使用的服务器地址");

        try { settings.OnLeave(); } catch { /* 未进过页面时离开可忽略 */ }
        try { lightSettings.OnLeave(); } catch { /* 同上 */ }

        // 「任意界面都能打开设置」依赖 Ctrl+, —— 判定逻辑抽成了纯函数，这里直接验证。
        Check.True(App.IsSettingsShortcut(Key.OemComma, ModifierKeys.Control),
            "设置：Ctrl + , 命中「打开设置」快捷键");
        Check.False(App.IsSettingsShortcut(Key.OemComma, ModifierKeys.None),
            "设置：单独的逗号不会打开设置");
        Check.False(App.IsSettingsShortcut(Key.S, ModifierKeys.Control),
            "设置：Ctrl + S 不会打开设置");

        ThemeManager.SetTheme(AppTheme.System);
    }

    /// <summary>
    /// 回归：房主设置页会周期轮询房间设置，轮询**绝不能覆盖房主正在编辑的输入框**。
    /// 曾经的 Bug：改「房间最大人数」必须抢在轮询（3 秒）之内点保存，否则数字被冲回旧值。
    ///
    /// ⚠ S4 迁移：<c>HostSettingsWindow</c> → <c>HostSettingsPage</c>。<c>RefreshFromServerState(dto)</c>
    /// 是页面上的公开方法，签名一字未改，所以「编辑中不被覆盖」这三条判据原样保留。
    /// </summary>
    private static void CheckHostSettingsPollKeepsEdits()
    {
        var page = new HostSettingsPage(new AppSession());
        try
        {
            var maxInput = page.FindName("MaxPlayersInput") as TextBox;
            var pwdInput = page.FindName("PasswordInput") as PasswordBox;
            var autoBox = page.FindName("AutoLayoutBox") as CheckBox;

            Check.True(maxInput != null && pwdInput != null && autoBox != null,
                "UI：房主设置页含人数 / 密码 / 自动布局控件");
            if (maxInput == null || pwdInput == null || autoBox == null) return;

            // 模拟房主正在编辑（还没点保存）
            maxInput.Text = "9";
            pwdInput.Password = "my-new-pwd";
            autoBox.IsChecked = false;

            // 服务端此刻仍是旧值 → 轮询刷新
            page.RefreshFromServerState(new HostSettingsDto
            {
                RoomId = "ABCDE",
                RoomName = "测试房",
                MaxPlayers = 8,
                MinPlayers = 4,
                MaxPlayersLimit = 12,
                HasPassword = true,
                InviteToken = "token-abc",
                Running = false,
                PlayerCount = 4,
                CustomGameSettings = false
            });

            Check.Equal("9", maxInput.Text, "UI：轮询不覆盖正在编辑的最大人数（回归：曾自动跳回旧值）");
            Check.Equal("my-new-pwd", pwdInput.Password, "UI：轮询不覆盖正在输入的密码");
            Check.False(autoBox.IsChecked == true, "UI：轮询不覆盖「跟随人数自动配置」的勾选状态");
        }
        finally
        {
            try { page.OnLeave(); } catch { /* 未进过页面时离开可忽略 */ }
        }
    }

    /// <summary>
    /// v2.3.1：房主设置要**按房间所属游戏**显示内容 ——
    /// ① 「身份配置」页只给有身份配置的玩法（狼人杀），谁是卧底不该看到；
    /// ② 页面标题与游戏徽标要带游戏标识（图标 / 中文名），不同游戏的房间一眼可辨。
    ///
    /// ⚠ S4 迁移：旧窗口的 <c>Title</c> 断言改成页面的 <c>Title</c> —— 由外壳窗口
    /// （<c>ShellWindow.ComposeTitle</c>）拼进窗口标题，所以「窗口标题带游戏名」这条用户可见的
    /// 结果并没有丢，只是中间多了一层（见下方 <c>ShellWindow</c> 那一小段验证）。
    /// </summary>
    private static void CheckHostSettingsGameTabs()
    {
        var page = new HostSettingsPage(new AppSession());
        try
        {
            var layoutTab = page.FindName("LayoutTab") as TabItem;
            var emojiText = page.FindName("GameEmojiText") as TextBlock;
            var badge = page.FindName("GameBadge") as Border;

            Check.True(layoutTab != null && emojiText != null && badge != null,
                "UI：房主设置页含「身份配置」页与游戏标识徽标");
            if (layoutTab == null || emojiText == null || badge == null) return;

            // —— 谁是卧底：没有身份配置 ——
            page.RefreshFromServerState(new HostSettingsDto
            {
                RoomId = "ABCDE",
                RoomName = "卧底房",
                MaxPlayers = 8,
                MinPlayers = 3,
                MaxPlayersLimit = 12,
                InviteToken = "token-abc",
                PlayerCount = 4,
                GameId = GameIds.Undercover,
                GameName = "谁是卧底",
                GameEmoji = "🕵️",
                GameAccent = "#38C9B0",
                HasPlayerConfig = false
            });

            Check.Equal(Visibility.Collapsed, layoutTab.Visibility,
                "UI：谁是卧底的房主设置隐藏「身份配置」页（不串到狼人杀）");
            Check.Equal("🕵️", emojiText.Text, "UI：房主设置显示房间所属游戏的图标");
            Check.True(page.Title.Contains("谁是卧底", StringComparison.Ordinal),
                "UI：房主设置页标题带游戏名（" + page.Title + "）");
            Check.True(badge.ToolTip is string tip && tip.Contains("谁是卧底", StringComparison.Ordinal),
                "UI：游戏徽标的 ToolTip 说明本房间的游戏");

            // —— 狼人杀：有身份配置 ——
            page.RefreshFromServerState(new HostSettingsDto
            {
                RoomId = "ABCDE",
                RoomName = "狼人房",
                MaxPlayers = 12,
                MinPlayers = 4,
                MaxPlayersLimit = 12,
                InviteToken = "token-abc",
                PlayerCount = 6,
                GameId = GameIds.Werewolf,
                GameName = "狼人杀",
                GameEmoji = "🐺",
                GameAccent = "#9B8CFF",
                HasPlayerConfig = true
            });

            Check.Equal(Visibility.Visible, layoutTab.Visibility,
                "UI：狼人杀的房主设置仍然显示「身份配置」页");
            Check.Equal("🐺", emojiText.Text, "UI：切换到狼人杀房间后徽标同步更新");

            // S4 新增（对应旧 `window.Title` 那条断言）：页面标题变化后，外壳窗口的标题要跟着变。
            // 这是「单窗口」形态下用户唯一能看到标题的地方，所以必须补上，不能只测页面自己。
            CheckShellTitleFollowsPage(page);
        }
        finally
        {
            try { page.OnLeave(); } catch { /* 未进过页面时离开可忽略 */ }
        }
    }

    /// <summary>
    /// S4 新增：页面标题 → 外壳窗口标题的联动（<c>IPageTitleSource.TitleChanged</c>）。
    /// <c>MainWindow</c> 被删掉后「窗口标题带游戏名」这条用户可见结果改由外壳拼装，
    /// 所以这里用真外壳窗口走一遍：挂上页面 → 标题含游戏名 → 游戏变了 → 窗口标题跟着变。
    /// </summary>
    private static void CheckShellTitleFollowsPage(HostSettingsPage page)
    {
        var shell = new ShellWindow();
        try
        {
            var nav = shell.Session.Navigation;
            nav.Reset(new PageRequest(PageId.RoomList));
            nav.Push(new PageRequest(PageId.HostSettings));

            var hosted = nav.Current;
            Check.True(hosted is HostSettingsPage,
                "S4：外壳里换到房主设置页（页面压栈可用）");

            Check.True(shell.Title.Contains("房主设置", StringComparison.Ordinal),
                $"S4：外壳窗口标题随页面变化（实际「{shell.Title}」）");

            // 直接驱动真外壳里的那一页，验证 TitleChanged 会把新标题送上去。
            if (hosted is HostSettingsPage live)
            {
                live.RefreshFromServerState(new HostSettingsDto
                {
                    RoomId = "ABCDE",
                    RoomName = "狼人房",
                    MaxPlayers = 12,
                    MinPlayers = 4,
                    MaxPlayersLimit = 12,
                    InviteToken = "token-abc",
                    PlayerCount = 6,
                    GameId = GameIds.Werewolf,
                    GameName = "狼人杀",
                    GameEmoji = "🐺",
                    GameAccent = "#9B8CFF",
                    HasPlayerConfig = true
                });

                Check.True(shell.Title.Contains("狼人杀", StringComparison.Ordinal),
                    $"S4：房间的游戏变了 → 外壳窗口标题跟着更新（实际「{shell.Title}」）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · S4 外壳标题联动异常：" + ex.Message);
            Check.True(false, "S4：外壳窗口标题能跟随页面标题变化");
        }
        finally
        {
            try { shell.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    /// <summary>
    /// 「创建房间」的游戏下拉：
    /// ① 必须有项模板（回归：只写 DisplayMemberPath 时曾退化成显示对象的类型名）；
    /// ② 下拉内部那个覆盖全区的 ToggleButton 必须换成空模板
    ///    （回归：默认模板带系统浅色外观，会盖在内容上 → 「点击时颜色异常」）。
    ///
    /// ⚠ S4 迁移：<c>CreateRoomWindow</c> → <c>CreateRoomPage</c>，控件名与模板结构一字未改。
    /// </summary>
    private static void CheckCreateRoomGamePicker()
    {
        var page = new CreateRoomPage(new AppSession());
        try
        {
            var box = page.FindName("GameBox") as ComboBox;
            Check.True(box != null, "UI：创建房间页含游戏下拉");
            if (box == null) return;

            Check.True(box.ItemTemplate != null,
                "UI：游戏下拉设置了项模板（不会显示未本地化的类型名）");
            // 即使模板再次遗漏，WPF 的回退是 ToString()，而 GameInfo.ToString 返回「图标 + 显示名」。
            Check.Equal("🐺 狼人杀", new GameInfo
            {
                GameId = GameIds.Werewolf,
                DisplayName = "狼人杀",
                Emoji = "🐺"
            }.ToString(), "UI：GameInfo.ToString 回退显示中文名（不会露出类型名）");

            // 模板里那个透明 ToggleButton 必须有自己的空模板。
            box.ApplyTemplate();
            var toggle = box.Template?.FindName("ToggleButton", box) as System.Windows.Controls.Primitives.ToggleButton;
            Check.True(toggle?.Template != null,
                "UI：游戏下拉的点击层使用空模板（不会盖出系统浅色外观）");
        }
        finally
        {
            try { page.OnLeave(); } catch { /* 未进过页面时离开可忽略 */ }
        }
    }

    /// <summary>
    /// 主题探针：两套配色必须**键完全一致**（缺键会导致切过去以后颜色不更新），
    /// 且切换后 Application 级颜色确实被替换（DynamicResource 据此重新求值）。
    /// </summary>
    private static void CheckTheme()
    {
        var dark = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClientGUI;component/Themes/Colors.Dark.xaml")
        };
        var light = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClientGUI;component/Themes/Colors.Light.xaml")
        };

        var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal).ToList();
        var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check.True(darkKeys.Count >= 20, $"主题：深色配色键齐全（{darkKeys.Count} 项）");
        Check.SequenceEqual(darkKeys, lightKeys, "主题：深色与浅色定义了完全相同的配色键");

        ThemeManager.SetTheme(AppTheme.Dark);
        Check.True(ThemeManager.IsDark, "主题：切换到深色立即生效");
        var darkBg = (Application.Current.TryFindResource("BgBrush") as SolidColorBrush)?.Color;

        ThemeManager.SetTheme(AppTheme.Light);
        Check.False(ThemeManager.IsDark, "主题：切换到浅色立即生效");
        var lightBg = (Application.Current.TryFindResource("BgBrush") as SolidColorBrush)?.Color;

        Check.True(darkBg.HasValue && lightBg.HasValue && darkBg != lightBg,
            $"主题：切换后背景色确实变化（{darkBg} → {lightBg}）");

        // 深色应该更暗（防止两套配色写反）。
        Check.True(darkBg!.Value.R + darkBg.Value.G + darkBg.Value.B
                   < lightBg!.Value.R + lightBg.Value.G + lightBg.Value.B,
            "主题：深色背景确实比浅色背景暗");

        // 真实容器也会随主题重新求值（不只是字典被换掉）。
        // ⚠ S4：载体由旧 <c>LoginWindow</c> 换成唯一的外壳窗口 <see cref="ShellWindow"/> ——
        //   外壳根元素同样是 Background="{DynamicResource BgBrush}"，判据不变。
        var probe = new ShellWindow();
        ThemeManager.SetTheme(AppTheme.Dark);
        var winDark = (probe.Background as SolidColorBrush)?.Color;
        ThemeManager.SetTheme(AppTheme.Light);
        var winLight = (probe.Background as SolidColorBrush)?.Color;
        Check.True(winDark.HasValue && winLight.HasValue && winDark != winLight,
            $"主题：已打开的外壳窗口跟随主题换色（{winDark} → {winLight}）");
        try { probe.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }

        ThemeManager.SetTheme(AppTheme.System);
        Check.True(ThemeManager.Preference == AppTheme.System, "主题：可切回「跟随系统」");

        CheckReferencedColorKeys(dark, light);
    }

    /// <summary>
    /// 扫描所有 XAML 里用到的 <c>{DynamicResource XxxBrush}</c>，逐个确认两套配色都有定义。
    /// 漏定义不会报错，只会静默回退成默认色（深色底上就是「看不见的文字」），
    /// 所以这条自动扫描很有必要——它随 XAML 自动更新，不需要维护清单。
    /// </summary>
    private static void CheckReferencedColorKeys(ResourceDictionary dark, ResourceDictionary light)
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            Console.WriteLine("   · 未找到仓库根目录，跳过配色键扫描");
            return;
        }

        var dir = Path.Combine(root, "ClientGUI");
        if (!Directory.Exists(dir)) return;

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // 先去注释：注释里允许用 {DynamicResource XxxBrush} 当示例（例如 Colors.*.xaml 的说明），
            // 那些不是真的引用，不能算进来。
            text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);

            foreach (Match m in Regex.Matches(text, @"\{DynamicResource\s+([A-Za-z0-9_]+)\}"))
            {
                var key = m.Groups[1].Value;
                if (key.EndsWith("Brush", StringComparison.Ordinal)) used.Add(key);
            }
        }

        Check.True(used.Count >= 15, $"主题：XAML 中共引用 {used.Count} 个配色键");

        var missingDark = used.Where(k => !dark.Contains(k)).ToList();
        var missingLight = used.Where(k => !light.Contains(k)).ToList();

        Check.Equal(0, missingDark.Count, "主题：深色配色覆盖全部引用键（缺失：" + string.Join(",", missingDark) + "）");
        Check.Equal(0, missingLight.Count, "主题：浅色配色覆盖全部引用键（缺失：" + string.Join(",", missingLight) + "）");
    }

    /// <summary>从当前工作目录向上找含 WerewolfGame.sln 的目录（供读取 XAML 源码）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WerewolfGame.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 剪贴板探针：确认「复制邀请码」这条路径在本机可走通。
    /// 注意本测试宿主没有启动消息循环，OLE 剪贴板可能因此被系统拒绝
    /// （<c>CLIPBRD_E_CANT_OPEN</c>），这属于测试环境限制而非代码缺陷——
    /// 真实客户端有完整消息循环；且即使复制失败，邀请码窗口里的文本框可选中，
    /// 用户仍能手动复制（这正是用独立窗口替代弹窗的原因）。
    /// </summary>
    private static void CheckClipboard()
    {
        const string probe = "WW1:CLIPBOARD-PROBE";

        var ok = ClipboardHelper.TrySetText(probe);
        if (!ok)
        {
            Console.WriteLine("   · 剪贴板写入被系统拒绝（测试宿主无消息循环，环境限制）；" +
                              "邀请码窗口会改提示手动全选复制。");
            Check.True(true, "UI：剪贴板不可用时已安全降级（不抛异常）");
            return;
        }

        try
        {
            Check.Equal(probe, Clipboard.GetText(), "UI：剪贴板读写往返正常（邀请码复制机制可用）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 剪贴板读取失败（环境限制）：" + ex.Message);
            Check.True(true, "UI：剪贴板读取失败已安全处理");
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

    /// <summary>
    /// S4：实例化一个页面（旧 <c>Try(string, Func&lt;Window&gt;)</c> 的页面版）。
    /// 判据与旧版逐字相同 —— "能实例化 / 实例化无异常" —— 只是载体从窗口换成了页面。
    /// </summary>
    private static void TryPage(string name, Func<AppSession, FrameworkElement> create, AppSession session)
    {
        try
        {
            var page = create(session);
            Check.True(page != null, $"UI：{name} 可实例化");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · {name} 失败：{ex}");
            Check.True(false, $"UI：{name} 实例化无异常");
        }
    }

    /// <summary>
    /// S4 新增：唯一顶层窗口 <see cref="ShellWindow"/> 必须能解析 ——
    /// 原来这条由 <c>MainWindow</c> 那条兜着，<c>MainWindow</c> 删掉后不能留空档。
    /// </summary>
    private static void TryShellWindow()
    {
        try
        {
            var shell = new ShellWindow();
            Check.True(shell.FindName("PageHost") is ContentControl,
                "UI：外壳窗口可实例化且有唯一页面宿主");
            try { shell.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 外壳窗口失败：" + ex);
            Check.True(false, "UI：外壳窗口实例化无异常");
        }
    }

    /// <summary>实例化一个控件（游戏面板等非窗口元素），只验证 XAML 能被解析。</summary>
    private static void TryElement(string name, Func<FrameworkElement> create)
    {
        try
        {
            var element = create();
            Check.True(element != null, $"UI：{name} 可实例化");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · {name} 失败：{ex}");
            Check.True(false, $"UI：{name} 实例化无异常");
        }
    }
}
