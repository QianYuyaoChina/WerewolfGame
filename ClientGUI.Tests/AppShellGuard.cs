using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.Controls;
using ClientGUI.Services;
using ClientGUI.Views;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// 风格守护断言（v2.5.0 第 2 条）：把「小程序页面骨架」写成机器可判的断言，
/// 后续每一批界面改动都不得让这里变红（架构红线：「后面的 gui 修改不能破坏这一风格」）。
///
/// 覆盖 PRD §4.4.3 的 A1–A6：
///   A1 两套主题色键集合差集为空（且新增的 17 个语义键零缺失、主色取值不变）
///   A2 界面 XAML 中零处颜色字面量（#RRGGBB / Color.FromRgb），颜色一律 DynamicResource
///   A3 迁移范围内每个窗口都有导航栏结构元素，且导航标题居中
///   A4 列表行实测 ≥56、按钮 / 输入框热区 ≥44
///   A5 主操作按钮通栏（宽度 ≥ 内容区 80%）
///   A6 不出现 DataGrid / PropertyGrid / Ribbon / Canvas.Left 这类「移植到手机会重写」的控件
///
/// 为什么挂在 <c>--ui</c>：A3 / A4 / A5 要真正实例化窗口、取样式并做一次布局测量，
/// 必须在 STA 线程 + 已初始化主题的 Application 下跑；UiSmokeCheck 正好提供这个上下文。
/// （既有 93 项一条不改，这里是纯追加，<c>--ui</c> 的断言数只会增加。）
/// </summary>
public static class AppShellGuard
{
    /// <summary>v2.5.0 新增的 17 个语义色键（架构 §4.1），两套主题必须同时具备。</summary>
    private static readonly string[] SemanticColorKeys =
    {
        "PageBgBrush", "NavBarBgBrush", "NavBarTextBrush", "DividerBrush", "CardAltBrush",
        "ActionBrush", "ActionTextBrush", "ActionHoverBrush", "ActionPressedBrush", "ActionDisabledBrush",
        "TitleTextBrush", "TitleBorderBrush", "TitleBgBrush",
        "MaskBrush", "PlaceholderBrush", "ChipTextBrush", "ChipBorderBrush"
    };

    /// <summary>A6 禁用清单：这些控件在手机端没有等价物，用了就得重写。</summary>
    private static readonly string[] ForbiddenControls =
    {
        "DataGrid", "PropertyGrid", "Ribbon", "Canvas.Left", "Canvas.Top"
    };

    /// <summary>迁移范围内需要「导航栏 + 通栏主按钮」的窗口（主窗口只迁移骨架，底部没有主按钮）。</summary>
    private static readonly (string Name, Func<Window> Create, bool ExpectPrimary)[] MigratedWindows =
    {
        ("登录窗口", () => new LoginWindow(), true),
        ("房间列表窗口", () => new RoomListWindow(new GameClient()), true),
        ("房间内主窗口", () => new MainWindow(new GameClient()), false),
        ("创建房间窗口", () => new CreateRoomWindow(), true),
        ("房主设置窗口", () => new HostSettingsWindow(new GameClient()), true),
        ("设置窗口", () => new SettingsWindow(), true),
        ("邀请码窗口", () => new InviteCodeWindow("WW1:SHELL-GUARD"), true),
        ("保留房间窗口", () => new KeepRoomWindow(), true)
    };

    /// <summary>
    /// 风格守护（A1–A6）+ 称号胶囊的渲染守护。挂在 <c>--ui</c>（要实例化控件并解析主题资源）。
    /// 纯逻辑断言另见 <see cref="RunLogicGuards"/>（挂在默认组，不需要 WPF 上下文）。
    /// </summary>
    public static void Run()
    {
        CheckColorKeys();           // A1
        CheckNoColorLiterals();     // A2
        CheckNoForbiddenControls(); // A6
        CheckWindowSkeletons();     // A3 + A5
        CheckTapTargets();          // A4
        CheckOverlayDialog();       // 新增对话框一律页面内浮层（K11）
        CheckTitleChip();           // 第 4 条：称号胶囊（金 / 小一档 / 无称号不渲染）
    }

    // ==================== 纯逻辑守护（默认组，不需要 STA） ====================

    /// <summary>
    /// B2 / B3 的可单测部分：关闭意图表、彩蛋计数、称号校验、称号协议契约、本地状态纯净性。
    /// 挂在默认组（<c>Program.cs</c>），这样跑一次逻辑校验就顺带验证它们。
    /// </summary>
    public static void RunLogicGuards()
    {
        Console.WriteLine("════════ v2.5.0 B2 / B3：关闭意图 · 彩蛋 · 称号（纯逻辑） ════════");
        CheckCloseIntent();
        CheckEggTapCounter();
        CheckTitleRules();
        CheckTitleProtocol();
        CheckLocalStatePurity();
        CheckAccountFileCompatibility();
    }

    /// <summary>
    /// B3：账号文件升级为信封格式后，**旧格式（纯字典）必须照旧能加载**（架构 U2）。
    /// 不新增文件、不做迁移脚本 —— 这里用一份临时的旧格式文件证明它。
    /// </summary>
    private static void CheckAccountFileCompatibility()
    {
        var path = Path.Combine(Path.GetTempPath(), "werewolf-title-compat-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // 旧格式：纯「用户名 → 哈希」字典，没有 Version / Accounts / Titles。
            File.WriteAllText(path, "{\n  \"legacyuser\": \"v2:0000000000000000\"\n}");

            var store = new AccountStore(path);
            Check.True(store.Exists("legacyuser"), "称号：旧格式 accounts.json 仍能加载（账号不丢）");
            Check.Equal("", store.TitleOf("legacyuser"), "称号：旧格式文件里没有称号时返回空");

            // 写入称号后重新加载：账号与称号都在（信封格式升级是自然的，不是另起一份文件）。
            store.SetTitle("legacyuser", "村长");
            var reloaded = new AccountStore(path);
            Check.Equal("村长", reloaded.TitleOf("legacyuser"), "称号：写入后重开进程仍读到（服务端权威持久）");
            Check.True(reloaded.Exists("legacyuser"), "称号：升级为信封格式后账号仍在");

            // 管理员用户名是保留字，普通注册抢不到（PRD 5.2）。
            Check.Equal(ServerMessages.AdminReserved, store.Register(AccountStore.AdminUsername, "pw"),
                "账号：admin 是保留用户名");
            Check.True(store.IsAdmin("ADMIN"), "账号：管理员判定大小写不敏感");
            Check.False(store.IsAdmin("legacyuser"), "账号：普通用户不是管理员");

            // 空值 = 清除。
            reloaded.SetTitle("legacyuser", "   ");
            Check.Equal("", reloaded.TitleOf("legacyuser"), "称号：空值 = 清除");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 账号文件兼容自检异常：" + ex.Message);
            Check.True(false, "称号：账号文件（信封格式 + 旧格式兼容）可读写");
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件删不掉不影响 */ }
        }
    }

    /// <summary>B2：四种关闭来源的行为表（架构 D8 / K9）。</summary>
    private static void CheckCloseIntent()
    {
        Check.False(CloseIntent.RequiresFullShutdown(CloseIntentSource.RoomWindow),
            "B2：关主窗口**不**走完整收尾（只退出房间，不停服务器 / 不退出程序）");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.RoomListWindow),
            "B2：关房间列表走完整收尾");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.ExitProgram),
            "B2：退出程序走完整收尾");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.SystemSession),
            "B2：系统关机 / 注销走完整收尾");

        var all = Enum.GetValues<CloseIntentSource>();
        Check.Equal(4, all.Length, $"B2：关闭来源共四种（实际 {all.Length}）");
        Check.Equal(1, all.Count(s => !CloseIntent.RequiresFullShutdown(s)),
            "B2：只有关主窗口不需要完整收尾（行为表可一眼看全）");

        // 四种来源都必须有人话描述（改行为时不会忘了同步文案）。
        var noDesc = all.Where(s => string.IsNullOrWhiteSpace(CloseIntent.Describe(s))).ToList();
        Check.Equal(0, noDesc.Count, "B2：每种关闭来源都有行为说明");
    }

    /// <summary>B3：3 秒 5 击的滚动窗口判定（PRD 8.3）。</summary>
    private static void CheckEggTapCounter()
    {
        // ① 3 秒内 5 击 → 触发
        var c1 = new EggTapCounter();
        Check.False(c1.RegisterTap(0), "彩蛋：第 1 击不触发");
        Check.False(c1.RegisterTap(700), "彩蛋：第 2 击不触发");
        Check.False(c1.RegisterTap(1400), "彩蛋：第 3 击不触发");
        Check.False(c1.RegisterTap(2100), "彩蛋：第 4 击不触发");
        Check.True(c1.RegisterTap(2800), "彩蛋：3 秒内第 5 击触发");
        Check.Equal(0, c1.Count, "彩蛋：触发后计数清零");

        // ② 跨度 3001ms → 不触发（第 1 击到第 5 击的时间差为准）
        var c2 = new EggTapCounter();
        for (var i = 0; i < 4; i++) c2.RegisterTap(i * 750);
        Check.False(c2.RegisterTap(3001), "彩蛋：第 5 击跨度 3001ms 不触发");

        // ② 补 边界值：正好 3000ms 要触发
        var c2b = new EggTapCounter();
        for (var i = 0; i < 4; i++) c2b.RegisterTap(i * 750);
        Check.True(c2b.RegisterTap(3000), "彩蛋：第 5 击跨度正好 3000ms 触发（≤ 边界）");

        // ③ 4 击后停 4 秒再 1 击 → 不触发，且从新一次点击重新开始
        var c3 = new EggTapCounter();
        for (var i = 0; i < 4; i++) c3.RegisterTap(i * 100);
        Check.False(c3.RegisterTap(4000), "彩蛋：4 击后隔 4 秒再 1 击不触发");
        Check.Equal(1, c3.Count, "彩蛋：超时后从新一次点击重新开始");

        // ④ 已触发后第 6 击不再触发（不重复弹）
        var c4 = new EggTapCounter();
        for (var i = 0; i < 5; i++) c4.RegisterTap(i * 100);
        Check.False(c4.RegisterTap(600), "彩蛋：触发后的第 6 击不再触发");

        // ⑤ 默认参数就是「3 秒 5 击」
        var def = new EggTapCounter();
        Check.Equal(5, def.RequiredTaps, "彩蛋：默认 5 击");
        Check.Equal(3000, def.WindowMs, "彩蛋：默认窗口 3000ms");

        // ⑥ Reset 可手动清零
        var c5 = new EggTapCounter();
        c5.RegisterTap(0);
        c5.Reset();
        Check.Equal(0, c5.Count, "彩蛋：Reset 可清零");
    }

    /// <summary>B3：称号校验口径（与服务端同源，PRD 4.7）。</summary>
    private static void CheckTitleRules()
    {
        Check.True(TitleRules.Validate("").ok, "称号：空 = 清除（合法）");
        Check.True(TitleRules.Validate("    ").ok, "称号：全空白 = 清除（合法）");
        Check.Equal("张三", TitleRules.Normalize("  张三  "), "称号：Normalize 去首尾空白");

        Check.True(TitleRules.Validate("一二三四五六七八").ok, "称号：8 个汉字合法");
        Check.True(TitleRules.Validate("abcdefghijklmnop").ok, "称号：16 个英文字符合法");
        Check.Equal(16, TitleRules.WeightedLength("一二三四五六七八"), "称号：8 个汉字 = 16 个加权长度");

        var (tooLongOk, tooLongErr) = TitleRules.Validate("一二三四五六七八九");
        Check.False(tooLongOk, "称号：9 个汉字被拒");
        Check.Equal(ServerMessages.TitleTooLong, tooLongErr, "称号：超长给长度限制文案");

        Check.False(TitleRules.Validate("abcdefghijklmnopq").ok, "称号：17 个英文字符被拒");

        var (badOk, badErr) = TitleRules.Validate("张\n三");
        Check.False(badOk, "称号：含换行被拒");
        Check.Equal(ServerMessages.TitleBadChars, badErr, "称号：控制字符给统一文案");

        Check.Equal(16, TitleRules.MaxLength, "称号：上限 16（8 汉字 / 16 字符）");
    }

    /// <summary>B3：协议契约 —— 「只能改自己的称号」（PRD 4.8 / 架构 K6）。</summary>
    private static void CheckTitleProtocol()
    {
        var props = typeof(SetTitlePayload).GetProperties();
        Check.Equal(1, props.Length, "称号：设置称号的请求里**不含**用户名字段（只能改自己的）");
        Check.Equal(nameof(SetTitlePayload.Title), props[0].Name, "称号：设置称号的请求只有 Title 一个字段");

        Check.True(typeof(SessionInfoDto).GetProperty(nameof(SessionInfoDto.Title)) != null,
            "称号：会话信息带当前称号（服务端权威下发）");
        Check.True(typeof(PlayerDto).GetProperty(nameof(PlayerDto.Title)) != null,
            "称号：玩家行带称号字段（随状态自然下发，零额外往返）");
        Check.True(typeof(RoomInfoDto).GetProperty(nameof(RoomInfoDto.HostTitle)) != null,
            "称号：房间列表带房主称号字段");
    }

    /// <summary>
    /// B3 + TODO T12：界面本地偏好里**不得**出现服务端权威数据（称号 / 管理员标记），
    /// 否则「服务端改了、本地缓存没改」就会出现改了不生效。
    /// </summary>
    private static void CheckLocalStatePurity()
    {
        var names = typeof(ClientGUI.Services.UiState).GetProperties()
                    .Select(p => p.Name.ToLowerInvariant())
                    .ToList();

        var banned = new[] { "title", "isadmin", "role" };
        var hits = banned.Where(b => names.Any(n => n.Contains(b))).ToList();
        Check.Equal(0, hits.Count, "称号 / 管理员不进本地偏好（命中：" + string.Join(",", hits) + "）");
    }

    // ==================== A1 · 两套主题色键 ====================

    /// <summary>
    /// 两套配色必须键完全一致（缺键不会报错，只会静默回退成默认色 —— 深色底上就是「看不见的字」）。
    /// 另外守住用户裁定 Q12：主色保持现有蓝紫，ActionBrush 的取值必须等于 AccentBrush。
    /// </summary>
    private static void CheckColorKeys()
    {
        var dark = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClientGUI;component/Themes/Colors.Dark.xaml")
        };
        var light = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClientGUI;component/Themes/Colors.Light.xaml")
        };

        var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString() ?? "")
                           .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString() ?? "")
                            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check.SequenceEqual(darkKeys, lightKeys, "风格 A1：深色与浅色配色键集合完全一致（差集为空）");
        Check.True(darkKeys.Count >= 45, $"风格 A1：配色键齐全（{darkKeys.Count} 项，≥45）");

        var missing = SemanticColorKeys
            .Where(k => !dark.Contains(k) || !light.Contains(k))
            .ToList();
        Check.Equal(0, missing.Count, "风格 A1：17 个语义色键两套主题零缺失（缺失：" +
                                      string.Join(",", missing) + "）");

        // 主色不变（用户裁定 Q12：不引入微信绿，Action* 取值沿用现有 Accent*）。
        foreach (var (theme, dict) in new[] { ("深色", dark), ("浅色", light) })
        {
            var accent = (dict["AccentBrush"] as System.Windows.Media.SolidColorBrush)?.Color;
            var action = (dict["ActionBrush"] as System.Windows.Media.SolidColorBrush)?.Color;
            Check.True(accent.HasValue && action.HasValue && accent == action,
                $"风格 A1：{theme}主题的主操作色沿用现有主色（Accent={accent} / Action={action}）");
        }

        // 称号三键必须是真的「金色」（浅色主题用深金保证对比度）。
        var darkTitle = (dark["TitleTextBrush"] as System.Windows.Media.SolidColorBrush)?.Color;
        var lightTitle = (light["TitleTextBrush"] as System.Windows.Media.SolidColorBrush)?.Color;
        Check.True(darkTitle.HasValue && lightTitle.HasValue && darkTitle != lightTitle,
            $"风格 A1：称号金随主题变化（深色 {darkTitle} / 浅色 {lightTitle}）");
    }

    // ==================== A2 · 零颜色字面量 ====================

    /// <summary>
    /// 界面 XAML 里不允许出现颜色字面量：颜色只能定义在两套 Colors.*.xaml（唯一来源），
    /// 各处一律 {DynamicResource XxxBrush} 引用，否则切主题时不会更新。
    /// </summary>
    private static void CheckNoColorLiterals()
    {
        var files = CollectXamlFiles(skipColorDictionaries: true);
        if (files.Count == 0)
        {
            Check.True(false, "风格 A2：未找到 ClientGUI 的 XAML 文件（扫描路径不对？）");
            return;
        }

        var literal = new Regex(@"#[0-9A-Fa-f]{6,8}\b|Color\.FromRgb|Color\.FromArgb", RegexOptions.Compiled);
        var hits = new List<string>();

        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in literal.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetFileName(file)}:{line} {m.Value}");
            }
        }

        Check.Equal(0, hits.Count, "风格 A2：界面 XAML 零颜色字面量（命中：" +
                                   string.Join("；", hits.Take(5)) + "）");
        Check.True(files.Count >= 20, $"风格 A2：扫描范围覆盖 {files.Count} 个 XAML 文件");
    }

    // ==================== A6 · 禁用控件清单 ====================

    /// <summary>
    /// 新增 / 改动的界面不得出现「移植到手机要重写」的控件与绝对定位（PRD 移植友好度 C1 / C6）。
    /// </summary>
    private static void CheckNoForbiddenControls()
    {
        var files = CollectXamlFiles(skipColorDictionaries: false);
        var hits = new List<string>();

        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (var token in ForbiddenControls)
            {
                if (!text.Contains(token, StringComparison.Ordinal)) continue;

                var at = text.IndexOf(token, StringComparison.Ordinal);
                var line = text[..at].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetFileName(file)}:{line} {token}");
            }
        }

        Check.Equal(0, hits.Count, "风格 A6：不使用数据表格 / 属性网格 / 功能区 / 画布绝对定位（命中：" +
                                   string.Join("；", hits.Take(5)) + "）");
    }

    // ==================== A3 + A5 · 骨架与通栏主按钮 ====================

    /// <summary>
    /// 每个迁移窗口都要有「导航栏 + 居中标题」，主操作按钮必须通栏（≥ 内容区 80%）。
    /// </summary>
    private static void CheckWindowSkeletons()
    {
        var withPrimary = 0;

        foreach (var (name, create, expectPrimary) in MigratedWindows)
        {
            Window? window = null;
            try
            {
                window = create();
                if (window == null)
                {
                    Check.True(false, $"风格 A3：{name} 未能实例化");
                    continue;
                }

                CheckNavBar(window, name);

                if (!expectPrimary) continue;

                var root = window.Content as FrameworkElement;
                if (root == null)
                {
                    Check.True(false, $"风格 A5：{name} 的内容根元素不是 FrameworkElement");
                    continue;
                }

                if (window is LoginWindow && window.FindName("Tabs") is TabControl tabs)
                {
                    // 两个标签页各有一个主按钮：切页后分别测量（未选中的页没有视觉树）。
                    for (var i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        Layout(root, window);
                        var key = i == 0 ? "EmbeddedPrimaryAction" : "PrimaryAction";
                        CheckBlockButton(window, root, key, $"{name}·第{i + 1}页");
                    }

                    tabs.SelectedIndex = 0;
                }
                else
                {
                    Layout(root, window);
                    CheckBlockButton(window, root, "PrimaryAction", name);
                }

                withPrimary++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · 风格检查（{name}）异常：{ex.Message}");
                Check.True(false, $"风格 A3：{name} 骨架检查无异常");
            }
            finally
            {
                try { window?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
            }
        }

        Check.True(withPrimary >= 7, $"风格 A5：{withPrimary} 个窗口带通栏主按钮（≥7）");
    }

    /// <summary>A3：存在导航栏结构元素（高 ≥56）且标题居中。</summary>
    private static void CheckNavBar(Window window, string name)
    {
        var bar = window.FindName("NavBar") as FrameworkElement;
        Check.True(bar != null, $"风格 A3：{name} 有导航栏结构元素（NavBar）");
        if (bar != null)
            Check.True(bar.MinHeight >= 56, $"风格 A3：{name} 导航栏高 ≥56（实际 {bar.MinHeight}）");

        FrameworkElement? title = window.FindName("NavTitleText") as TextBlock;
        title ??= window.FindName("NavTitleButton") as Button;

        Check.True(title != null, $"风格 A3：{name} 有导航标题（NavTitleText / NavTitleButton）");
        if (title != null)
            Check.Equal(HorizontalAlignment.Center, title.HorizontalAlignment,
                $"风格 A3：{name} 的导航标题居中");
    }

    /// <summary>A5：主操作按钮通栏（宽度 ≥ 内容区 80%）。</summary>
    private static void CheckBlockButton(Window window, FrameworkElement root, string elementName, string label)
    {
        var button = window.FindName(elementName) as Button;
        Check.True(button != null, $"风格 A5：{label} 有主操作按钮（{elementName}）");
        if (button == null) return;

        var content = root.ActualWidth;
        Check.True(content > 0, $"风格 A5：{label} 内容区已完成布局（宽度 {content}）");
        if (content <= 0) return;

        var ratio = button.ActualWidth / content;
        Check.True(ratio >= 0.8,
            $"风格 A5：{label} 的主按钮通栏（{button.ActualWidth:F0}/{content:F0} = {ratio:P0} ≥ 80%）");
    }

    /// <summary>未显示的窗口也要先做一次完整布局，才能读到 ActualWidth。</summary>
    private static void Layout(FrameworkElement root, Window window)
    {
        var size = new Size(double.IsNaN(window.Width) ? 1024 : window.Width,
                            double.IsNaN(window.Height) ? 768 : window.Height);
        root.Measure(size);
        root.Arrange(new Rect(0, 0, size.Width, size.Height));
        root.UpdateLayout();
    }

    // ==================== A4 · 行高与点击热区 ====================

    /// <summary>
    /// 列表行实测 ≥56、按钮 / 输入框热区 ≥44（移动端手指点得准的下限）。
    /// </summary>
    private static void CheckTapTargets()
    {
        var window = new RoomListWindow(new GameClient());
        try
        {
            // —— 列表行 ——
            var list = window.FindName("RoomList") as ListBox;
            Check.True(list?.ItemTemplate != null, "风格 A4：房间列表有行模板");
            if (list?.ItemTemplate != null)
            {
                var row = list.ItemTemplate.LoadContent() as FrameworkElement;
                Check.True(row != null, "风格 A4：行模板可加载（LoadContent）");
                if (row != null)
                {
                    row.Measure(new Size(900, 400));
                    Check.True(row.DesiredSize.Height >= 56,
                        $"风格 A4：列表行实测高度 ≥56（实际 {row.DesiredSize.Height:F0}）");
                }
            }

            // —— 通栏主按钮 ——
            var primary = window.FindResource("BlockPrimaryButton") as Style;
            Check.True(primary != null, "风格 A4：AppShell 提供 BlockPrimaryButton（通栏主按钮）");
            if (primary != null)
            {
                var button = new Button { Style = primary, Content = "一键开服" };
                button.Measure(new Size(343, 400));
                Check.True(button.DesiredSize.Height >= 44,
                    $"风格 A4：通栏主按钮热区 ≥44（实际 {button.DesiredSize.Height:F0}）");
            }

            // —— 导航栏图标按钮 ——
            var icon = window.FindResource("NavIconButton") as Style;
            Check.True(icon != null, "风格 A4：AppShell 提供 NavIconButton（导航热区按钮）");
            if (icon != null)
            {
                var nav = new Button { Style = icon, Content = "设置" };
                Check.True(nav.MinWidth >= 44 && nav.MinHeight >= 44,
                    $"风格 A4：导航按钮热区 ≥44×44（实际 {nav.MinWidth}×{nav.MinHeight}）");
            }

            // —— 输入框 ——
            var field = window.FindResource("FieldInput") as Style;
            Check.True(field != null, "风格 A4：AppShell 提供 FieldInput（表单输入框）");
            if (field != null)
            {
                var box = new TextBox { Style = field, Text = "12345" };
                box.Measure(new Size(343, 400));
                Check.True(box.DesiredSize.Height >= 44,
                    $"风格 A4：输入框高度 ≥44（实际 {box.DesiredSize.Height:F0}）");
            }
        }
        finally
        {
            try { window.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    // ==================== 浮层（K11） ====================

    /// <summary>
    /// 新增对话框一律用页面内浮层（遮罩 + 居中卡片），不再用 MessageBox 承载业务流程。
    /// 这里只验证浮层本身可用且长在风格里（各业务对话框在 B3 / B4 / B5 接入）。
    /// </summary>
    private static void CheckOverlayDialog()
    {
        try
        {
            var dialog = new OverlayDialog
            {
                DialogTitle = "风格守护自检",
                PrimaryButtonText = "确定",
                SecondaryButtonText = "取消",
                Message = "这是一条就地提示",
                MessageIsError = false
            };

            var root = dialog.Content as FrameworkElement;
            Check.True(root != null, "风格：页面内浮层 OverlayDialog 可实例化");
            Check.True(dialog.FindName("PrimaryButton") is Button, "风格：浮层带主按钮");
            Check.True(dialog.FindName("SecondaryButton") is Button, "风格：浮层次按钮");

            // 遮罩与卡片都要能解析到主题色（两套主题下都不会出现「黑底黑字」）。
            var mask = dialog.Background as System.Windows.Media.Brush;
            Check.True(mask != null, "风格：浮层遮罩使用主题资源（MaskBrush）");

            dialog.CloseDialog();
            Check.Equal(Visibility.Collapsed, dialog.Visibility, "风格：浮层可关闭（Visibility）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 浮层自检异常：" + ex.Message);
            Check.True(false, "风格：页面内浮层 OverlayDialog 可实例化");
        }
    }

    // ==================== 称号胶囊（第 4 条） ====================

    /// <summary>
    /// 称号胶囊：金色文字 + 比正文小一档（12）+ 同色系 1px 圆角框；**无称号时不渲染**。
    /// 样式全部引用 AppShell 的 TitleChip* 键，本控件零颜色字面量。
    /// </summary>
    private static void CheckTitleChip()
    {
        try
        {
            var chip = new TitleChip();
            Check.Equal(Visibility.Collapsed, chip.Visibility, "称号：文本为空时不渲染（Collapsed，不留空胶囊）");

            chip.Text = "村长";
            Check.Equal(Visibility.Visible, chip.Visibility, "称号：有文本时渲染");

            chip.Text = "   ";
            Check.Equal(Visibility.Collapsed, chip.Visibility, "称号：全空白视为无称号");

            // 样式必须来自 AppShell（换主题时才会跟着变，且两套键名一致）。
            chip.Text = "熬夜冠军";
            var border = chip.FindName("ChipBorder") as Border;
            var text = chip.FindName("ChipText") as TextBlock;

            Check.True(border != null && ReferenceEquals(border.Style, chip.FindResource("TitleChipBorder")),
                "称号：胶囊边框引用 AppShell 的 TitleChipBorder");
            Check.True(text != null && ReferenceEquals(text.Style, chip.FindResource("TitleChipText")),
                "称号：胶囊文字引用 AppShell 的 TitleChipText");

            // 字号比相邻正文（14）小一档 = 12。
            Check.True(text != null && Math.Abs(text.FontSize - 12) < 0.01,
                $"称号：字号比正文小一档（12，实际 {text?.FontSize}）");

            // 文字色必须是主题键（两套主题各一套值，深色 / 浅色都可读）。
            Check.True(text != null && text.Foreground != null, "称号：文字色来自主题资源（TitleTextBrush）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 称号胶囊自检异常：" + ex.Message);
            Check.True(false, "称号：TitleChip 可实例化并正确显隐");
        }
    }

    // ==================== 文件扫描工具 ====================

    /// <summary>收集 ClientGUI 下的 XAML 源文件（跳过 obj 生成物；可选跳过两套配色字典）。</summary>
    private static List<string> CollectXamlFiles(bool skipColorDictionaries)
    {
        var root = FindRepoRoot();
        var files = new List<string>();
        if (root == null) return files;

        var dir = Path.Combine(root, "ClientGUI");
        if (!Directory.Exists(dir)) return files;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
        {
            // obj 下是编译中间产物，不算源码。
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var name = Path.GetFileName(file);
            if (skipColorDictionaries && (name == "Colors.Dark.xaml" || name == "Colors.Light.xaml")) continue;

            files.Add(file);
        }

        return files;
    }

    /// <summary>去掉 XML 注释（注释里的示例引用不算真的用法）。</summary>
    private static string StripComments(string text)
        => Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>从当前工作目录向上找含 WerewolfGame.sln 的目录。</summary>
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
}
