using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.Models;
using ClientGUI.Services;
using ClientGUI.Views;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// v2.5.0 B0 + B1 的 **QA 独立复核断言**（严过关 · 背靠背编写）。
///
/// 与工程师自测的 <see cref="AppShellGuard"/> 的关系：
///   · 彼此**不共享**任何判定代码，本文件从源码文本与真实布局测量两条路重新证明一遍；
///   · 工程师的 A3 只查了 <c>MinHeight</c> 与 <c>HorizontalAlignment</c> 两个**属性**，
///     这里补的是**真实布局后的 ActualHeight / 几何居中**（属性写了不代表量出来达标）；
///   · 只做追加，不删不 relax 任何既有断言。
///
/// 分两组挂载：
///   · <c>RunDocsAndSourceGuards()</c> —— 默认组（纯源码 / 文档扫描，不需要 STA）；
///   · <c>RunMeasuredStyleGuards()</c> —— <c>--ui</c> 组（要实例化窗口并做真实布局测量）。
/// </summary>
public static class AppShellQaGuard
{
    /// <summary>B1 迁移范围内必须挂上骨架的 8 个窗口（与架构 §9 B1「改哪些文件」一致）。</summary>
    private static readonly string[] MigratedWindowFiles =
    {
        @"ClientGUI\Views\LoginWindow.xaml",
        @"ClientGUI\Views\RoomListWindow.xaml",
        @"ClientGUI\MainWindow.xaml",
        @"ClientGUI\Views\CreateRoomWindow.xaml",
        @"ClientGUI\Views\HostSettingsWindow.xaml",
        @"ClientGUI\Views\SettingsWindow.xaml",
        @"ClientGUI\Views\InviteCodeWindow.xaml",
        @"ClientGUI\Views\KeepRoomWindow.xaml"
    };

    /// <summary>AppShell.xaml 必须提供的骨架样式键（架构决策 D1 第 3 点）。</summary>
    private static readonly string[] RequiredShellKeys =
    {
        "NavBarPanel", "NavTitleText", "NavTitleButton", "NavIconButton",
        "PageScroll", "CardPanel", "CardSection", "ListRow", "DividerLine", "FooterBar",
        "BlockPrimaryButton", "BlockSecondaryButton",
        "ChipBorder", "ChipText", "TitleChipBorder", "TitleChipText", "StatusChipBorder",
        "FieldLabel", "FieldInput", "FieldPassword", "FieldError", "FieldHint",
        "SectionTitleText", "BodyText", "MinorText", "FootnoteText",
        "OverlayHost", "OverlayCard", "OverlayTitle"
    };

    /// <summary>腾讯文档以外的「移植到手机要重写」清单（PRD 移植友好度 C1 / C6）。</summary>
    private static readonly string[] ForbiddenControls =
    {
        "DataGrid", "PropertyGrid", "Ribbon", "Canvas.Left", "Canvas.Top"
    };

    // ==================================================================
    //  默认组：文档 / 源码级复核（不需要 WPF 上下文）
    // ==================================================================

    /// <summary>
    /// B0（待办机制）与 B1（风格地基）的源码 / 文档级复核。挂在默认组，
    /// 这样任何人跑一次逻辑校验就会顺带验证「待办文件还在、版本号对、零颜色字面量」。
    /// </summary>
    public static void RunDocsAndSourceGuards()
    {
        Console.WriteLine("════════ v2.5.0 B0 / B1：QA 独立复核（文档 + 源码） ════════");

        var root = FindRepoRoot();
        Check.True(root != null, "QA·B0：能定位仓库根目录（向上找 WerewolfGame.sln）");
        if (root == null) return;

        CheckTodoFile(root);
        CheckVersionAndChangelog();
        CheckThemeKeySymmetry(root);
        CheckAppShell(root);
        CheckMigratedWindows(root);
        CheckOutOfScopeWindows(root);
        CheckNoColorLiterals(root);
        CheckNoForbiddenControls(root);

        // v2.5.0 第 8 条（用户裁定）：关掉最后一个窗口 → 内置服务器也必须停。
        CheckShutdownChain(root);
    }

    /// <summary>
    /// v2.5.0 第 8 条（用户裁定）：**关掉最后一个窗口 → 内置服务器也必须停**。
    ///
    /// 这是「关窗收尾语义链」的源码级守护：真正的端到端证明在 <c>--embeddedqa</c>
    /// （真起服 + 真收尾 + 端口释放）；这里守住链条的每一环都还在，谁拆了就会立刻变红。
    ///
    /// 与 B2 不冲突：关**房间主窗口**只退出房间（不停服务器），只有关最后一个窗口
    /// （房间列表 / 退出程序 / 系统关机）才走完整收尾 —— 行为表在 <see cref="CloseIntent"/>。
    /// </summary>
    private static void CheckShutdownChain(string root)
    {
        // （一）行为表：只有关主窗口不停服务器；其余三种（含「关最后一个窗口」=关房间列表）都要完整收尾。
        Check.False(CloseIntent.RequiresFullShutdown(CloseIntentSource.RoomWindow),
            "QA·第8条：关房间主窗口只退出房间（不停内置服务器）—— 与 B2 一致");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.RoomListWindow),
            "QA·第8条：关掉最后一个窗口（房间列表）→ 完整收尾（会停内置服务器）");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.ExitProgram),
            "QA·第8条：菜单「退出程序」→ 完整收尾");
        Check.True(CloseIntent.RequiresFullShutdown(CloseIntentSource.SystemSession),
            "QA·第8条：系统关机 / 注销 → 完整收尾");

        // （二）应用永不「关到没窗口就自动退出」：退出必须走显式收尾，收尾里才有「停服务器」。
        var app = Path.Combine(root, "ClientGUI", "App.xaml.cs");
        Check.True(File.Exists(app), "QA·第8条：ClientGUI/App.xaml.cs 存在");
        if (File.Exists(app))
        {
            var text = File.ReadAllText(app);
            Check.True(text.Contains("ShutdownMode.OnExplicitShutdown", StringComparison.Ordinal),
                "QA·第8条：App 用 OnExplicitShutdown（关窗不等于退出，退出必须走显式收尾）");
            Check.True(text.Contains("AppShutdown.RunWithPump()", StringComparison.Ordinal),
                "QA·第8条：App.OnExit（及房间循环 finally）调用 AppShutdown.RunWithPump() 收尾");
            Check.True(text.Contains("Shutdown()", StringComparison.Ordinal),
                "QA·第8条：关闭最后一个窗口后调用 Shutdown() → 触发 OnExit 收尾");
        }

        // （三）收尾的第 ③ 步必须是「停内置服务器」（否则关最后一个窗口会留下孤儿监听）。
        var shutdown = Path.Combine(root, "ClientGUI", "Services", "AppShutdown.cs");
        Check.True(File.Exists(shutdown), "QA·第8条：ClientGUI/Services/AppShutdown.cs 存在");
        if (File.Exists(shutdown))
        {
            var text = File.ReadAllText(shutdown);
            Check.True(text.Contains("EmbeddedServerService.Current", StringComparison.Ordinal)
                       && text.Contains("service.StopAsync()", StringComparison.Ordinal),
                "QA·第8条：AppShutdown 收尾第 ③ 步停内置服务器（EmbeddedServerService.StopAsync）");
        }
    }

    /// <summary>
    /// B1 **范围外**的窗口不得被顺手删改：PRD §4.4.2 把「关于 / 更新日志窗口」列为 P2 允许暂缓，
    /// 本轮只需要登记进待办 T11，不应改动它们的内容。
    /// 这里守住「关于」窗口的三个信息区块仍在 —— 风格迁移不得顺手删掉用户可见的信息。
    /// </summary>
    private static void CheckOutOfScopeWindows(string root)
    {
        var about = Path.Combine(root, "ClientGUI", "Views", "AboutWindow.xaml");
        Check.True(File.Exists(about), "QA·范围：AboutWindow.xaml 存在");
        if (!File.Exists(about)) return;

        var text = File.ReadAllText(about);
        Check.True(text.Contains("AppInfo.SolutionName", StringComparison.Ordinal),
            "QA·范围：关于窗口仍显示「解决方案」信息行（PRD §4.4.2：本轮允许暂缓，不应删改）");
        Check.True(text.Contains("网络加密", StringComparison.Ordinal),
            "QA·范围：关于窗口仍显示「网络加密」说明（PSK + AES-GCM）");
        Check.True(text.Contains("AppInfo.TechStack", StringComparison.Ordinal),
            "QA·范围：关于窗口仍显示技术栈徽章列表");
    }

    /// <summary>B0：docs/TODO.md 必须真实存在、可执行、含 13 条种子条目与本轮闭环记录。</summary>
    private static void CheckTodoFile(string root)
    {
        var path = Path.Combine(root, "docs", "TODO.md");
        Check.True(File.Exists(path), "QA·B0：docs/TODO.md 存在（路径 docs/TODO.md）");
        if (!File.Exists(path)) return;

        var todo = File.ReadAllText(path);

        // ① 禁同步警示：必须同时点名 git commit 与 git push，且写明「禁止」。
        Check.True(todo.Contains("git commit", StringComparison.Ordinal)
                   && todo.Contains("git push", StringComparison.Ordinal)
                   && todo.Contains("禁止", StringComparison.Ordinal),
            "QA·B0：TODO.md 顶部写明「禁止 git commit / git push」");

        // ② 闭环流程与五步。
        Check.True(todo.Contains("闭环流程", StringComparison.Ordinal), "QA·B0：TODO.md 含「闭环流程」章节");
        var stepMarks = new[] { "①", "②", "③", "④", "⑤" };
        Check.Equal(5, stepMarks.Count(todo.Contains), "QA·B0：闭环五步齐全（读 / 挑 / 记 / 销 / 回写）");

        // ③ 条目模板六字段。
        Check.True(todo.Contains("条目模板", StringComparison.Ordinal), "QA·B0：TODO.md 含条目模板");
        var fields = new[] { "为什么：", "落点：", "优先级：", "完成判定：", "来源：" };
        Check.Equal(5, fields.Count(todo.Contains), "QA·B0：条目模板字段齐全（为什么 / 落点 / 优先级 / 完成判定 / 来源）");

        // ④ 四组分组。
        var groups = new[] { "P0 尽快做", "P1 该做", "P2 有空做", "已完成" };
        Check.Equal(4, groups.Count(todo.Contains), "QA·B0：四组分组齐全（P0 / P1 / P2 / 已完成）");

        // ⑤ 13 条种子条目 T1–T13 一条不少（\b 保证 T1 不会被 T13 顶替）。
        var seeds = Enumerable.Range(1, 13).Select(i => "T" + i).ToArray();
        var missing = seeds.Where(t => !Regex.IsMatch(todo, $@"\b{t}\b")).ToList();
        Check.Equal(0, missing.Count, "QA·B0：13 条种子条目（T1–T13）齐全（缺：" + string.Join(",", missing) + "）");

        // ⑥ 闭环第 ⑤ 步「回写新增」：种子之外必须还有新增条目。
        var tNumbers = Regex.Matches(todo, @"\*\*T(\d+)")
                            .Select(m => int.Parse(m.Groups[1].Value))
                            .Distinct()
                            .OrderBy(n => n)
                            .ToList();
        Check.True(tNumbers.Count >= 14,
            $"QA·B0：闭环第⑤步已回写新增条目（条目编号数 {tNumbers.Count} ≥ 14）");

        // ⑦ 本轮闭环记录（挑了 / 做了 / 新增）。
        Check.True(todo.Contains("本轮闭环记录", StringComparison.Ordinal)
                   && todo.Contains("挑", StringComparison.Ordinal)
                   && todo.Contains("回写", StringComparison.Ordinal),
            "QA·B0：TODO.md 含「本轮闭环记录」（挑了 / 做了 / 新增）");
    }

    /// <summary>B0：版本号与更新日志必须与 2.5.0 对齐。</summary>
    private static void CheckVersionAndChangelog()
    {
        Check.Equal("2.5.0", AppInfo.Version, "QA·B0：AppInfo.Version == 2.5.0");
        Check.False(AppInfo.Version.Contains('+'), "QA·B0：版本号不含 SDK 附加的 + 提交号");

        var latest = Changelog.All.Where(c => c.IsLatest).ToList();
        Check.Equal(1, latest.Count, "QA·B0：更新日志有且只有一个 IsLatest 版本");
        Check.True(latest.Count == 1 && latest[0].Version == "2.5.0",
            "QA·B0：IsLatest 指向 2.5.0（实际：" + string.Join(",", latest.Select(c => c.Version)) + "）");

        var v240 = Changelog.All.FirstOrDefault(c => c.Version == "2.4.0");
        Check.True(v240 != null && !v240.IsLatest, "QA·B0：2.4.0 已让位（IsLatest=false）");
        Check.True(Changelog.All.Any(c => c.Version == "2.5.0"),
            "QA·B0：更新日志含 2.5.0 条目");
    }

    /// <summary>B1：两套主题键集合**（从源码文本解析）**必须完全一致 —— 不依赖运行期资源字典。</summary>
    private static void CheckThemeKeySymmetry(string root)
    {
        var darkPath = Path.Combine(root, "ClientGUI", "Themes", "Colors.Dark.xaml");
        var lightPath = Path.Combine(root, "ClientGUI", "Themes", "Colors.Light.xaml");
        Check.True(File.Exists(darkPath) && File.Exists(lightPath), "QA·B1：两套配色字典文件存在");
        if (!File.Exists(darkPath) || !File.Exists(lightPath)) return;

        var dark = ParseKeys(File.ReadAllText(darkPath));
        var light = ParseKeys(File.ReadAllText(lightPath));

        Check.True(dark.Count >= 45, $"QA·B1：深色配色键数 ≥45（实际 {dark.Count}）");
        Check.Equal(dark.Count, light.Count, "QA·B1：两套配色键数一致");
        Check.SequenceEqual(dark, light, "QA·B1：两套配色键名集合排序后完全一致（差集为空）");
        Check.Equal(0, dark.Except(light).Union(light.Except(dark)).Count(),
            "QA·B1：两套配色互为子集（双向差集为空）");

        // 用户裁定 Q12：主操作色沿用现有主色，不引入微信绿 —— 直接比对源码里的取值。
        foreach (var (theme, file) in new[] { ("深色", darkPath), ("浅色", lightPath) })
        {
            var text = File.ReadAllText(file);
            foreach (var pair in new[] { ("ActionBrush", "AccentBrush"),
                                         ("ActionHoverBrush", "AccentHoverBrush"),
                                         ("ActionPressedBrush", "AccentPressedBrush") })
            {
                var a = ValueOf(text, pair.Item1);
                var b = ValueOf(text, pair.Item2);
                Check.True(a != null && a == b,
                    $"QA·B1：{theme}主题 {pair.Item1} 取值沿用 {pair.Item2}（{a} vs {b}）");
            }
        }
    }

    /// <summary>B1：AppShell.xaml 必须存在、键齐、且被 Panels.xaml 合并（架构 D1 的核心杠杆）。</summary>
    private static void CheckAppShell(string root)
    {
        var shellPath = Path.Combine(root, "ClientGUI", "Themes", "AppShell.xaml");
        Check.True(File.Exists(shellPath), "QA·B1：ClientGUI/Themes/AppShell.xaml 存在");
        if (!File.Exists(shellPath)) return;

        var keys = ParseKeys(File.ReadAllText(shellPath));
        var missing = RequiredShellKeys.Where(k => !keys.Contains(k)).ToList();
        Check.Equal(0, missing.Count, "QA·B1：AppShell 骨架样式键齐全（缺：" + string.Join(",", missing) + "）");
        Check.Equal(RequiredShellKeys.Length, keys.Count,
            $"QA·B1：AppShell 键数与架构清单一致（实际 {keys.Count}，期望 {RequiredShellKeys.Length}）");

        // AppShell 自己必须遵守阶梯（新增文件不允许带出「半档」）。
        var shell = File.ReadAllText(shellPath);
        var sizes = Regex.Matches(StripComments(shell), @"FontSize=""(\d+(?:\.\d+)?)""")
                         .Select(m => double.Parse(m.Groups[1].Value))
                         .Distinct()
                         .OrderBy(v => v)
                         .ToList();
        var offLadder = sizes.Where(v => !new[] { 11d, 12d, 14d, 15d, 17d }.Contains(v)).ToList();
        Check.Equal(0, offLadder.Count,
            "QA·B1：AppShell 自身字号全在 11/12/14/15/17 五档内（越界：" + string.Join(",", offLadder) + "）");

        var panelsPath = Path.Combine(root, "ClientGUI", "Themes", "Panels.xaml");
        if (File.Exists(panelsPath))
        {
            // 先剥注释再比顺序：注释里也会提到这两个文件名，直接用 IndexOf 会误判。
            var panels = StripComments(File.ReadAllText(panelsPath));
            Check.True(panels.Contains("AppShell.xaml", StringComparison.Ordinal),
                "QA·B1：Panels.xaml 已合并 AppShell.xaml（一处引用全局生效）");
            Check.True(panels.IndexOf("AppShell.xaml", StringComparison.Ordinal)
                       > panels.IndexOf("Controls.xaml", StringComparison.Ordinal),
                "QA·B1：AppShell 排在 Controls.xaml 之后（BasedOn 依赖顺序）");
        }
    }

    /// <summary>B1：8 个迁移窗口的 XAML 都必须出现导航栏与导航标题（源码级，先于真实测量）。</summary>
    private static void CheckMigratedWindows(string root)
    {
        foreach (var rel in MigratedWindowFiles)
        {
            var path = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
            var name = Path.GetFileNameWithoutExtension(rel);
            Check.True(File.Exists(path), $"QA·B1：{name}.xaml 存在");
            if (!File.Exists(path)) continue;

            var text = StripComments(File.ReadAllText(path));

            Check.True(Regex.IsMatch(text, @"x:Name=""NavBar"""),
                $"QA·B1：{name} 声明了导航栏元素（x:Name=NavBar）");
            Check.True(Regex.IsMatch(text, @"x:Name=""NavTitle(Text|Button)"""),
                $"QA·B1：{name} 声明了导航标题（NavTitleText / NavTitleButton）");
            Check.True(text.Contains("NavBarPanel", StringComparison.Ordinal),
                $"QA·B1：{name} 的导航栏引用 NavBarPanel 样式（不是另写一套）");
            Check.True(Regex.IsMatch(text, @"Background=""\{DynamicResource "),
                $"QA·B1：{name} 的背景走主题资源（DynamicResource）");
        }
    }

    /// <summary>B1：界面 XAML 零颜色字面量（QA 自己实现一遍，不复用 AppShellGuard 的正则）。</summary>
    private static void CheckNoColorLiterals(string root)
    {
        var files = CollectXaml(root, skipColorDictionaries: true);
        Check.True(files.Count >= 20, $"QA·B1：扫描覆盖了 {files.Count} 个 XAML 文件（≥20）");

        var hits = new List<string>();
        var literal = new Regex(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|Color\s*\.\s*From(?:Rgb|Argb)",
            RegexOptions.Compiled);
        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in literal.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetFileName(file)}:{line} {m.Value}");
            }
        }

        Check.Equal(0, hits.Count,
            "QA·B1：界面 XAML 零颜色字面量（命中：" + string.Join("；", hits.Take(5)) + "）");

        // 两套配色字典是颜色的唯一合法来源：确认它们确实**含有**颜色定义（不是空壳）。
        foreach (var f in new[] { "Colors.Dark.xaml", "Colors.Light.xaml" })
        {
            var p = Path.Combine(root, "ClientGUI", "Themes", f);
            if (!File.Exists(p)) continue;
            var n = Regex.Matches(StripComments(File.ReadAllText(p)), @"Color=""#[0-9A-Fa-f]{8}""|>#[0-9A-Fa-f]{8}<").Count;
            Check.True(n >= 20, $"QA·B1：{f} 仍是颜色唯一来源（{n} 处颜色定义）");
        }
    }

    /// <summary>B1：不得出现移植到手机要重写的控件与绝对定位。</summary>
    private static void CheckNoForbiddenControls(string root)
    {
        var hits = new List<string>();
        foreach (var file in CollectXaml(root, skipColorDictionaries: false))
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (var token in ForbiddenControls)
            {
                var at = text.IndexOf(token, StringComparison.Ordinal);
                if (at < 0) continue;
                var line = text[..at].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetFileName(file)}:{line} {token}");
            }
        }

        Check.Equal(0, hits.Count,
            "QA·B1：无 DataGrid / PropertyGrid / Ribbon / 画布绝对定位（命中：" + string.Join("；", hits.Take(5)) + "）");
    }

    // ==================================================================
    //  --ui 组：真实布局测量（必须在 STA + 已初始化主题下跑）
    // ==================================================================

    /// <summary>
    /// B1 的**实测**守护：真正实例化 8 个窗口、做一次完整布局，
    /// 用 ActualWidth / ActualHeight（而不是样式属性）判定导航栏高 ≥56、标题几何居中、
    /// 行高 ≥56、热区 ≥44、主按钮通栏 ≥80%。
    /// </summary>
    public static void RunMeasuredStyleGuards()
    {
        Console.WriteLine("   —— v2.5.0 B1：QA 独立实测（真实布局后量尺寸） ——");

        // （一）8 个窗口：导航栏实测高 ≥56 + 标题几何居中
        foreach (var (label, create) in WindowFactories)
        {
            Window? window = null;
            try
            {
                window = create();
                var root = window.Content as FrameworkElement;
                Check.True(root != null, $"QA·实测：{label} 的内容根是 FrameworkElement");
                if (root == null) continue;

                Arrange(root, window);

                var bar = window.FindName("NavBar") as FrameworkElement;
                Check.True(bar != null, $"QA·实测：{label} 能取到导航栏（NavBar）");
                if (bar != null)
                    Check.True(bar.ActualHeight >= 56,
                        $"QA·实测：{label} 导航栏**实测**高 ≥56（实际 {bar.ActualHeight:F1}）");

                FrameworkElement? title = window.FindName("NavTitleText") as FrameworkElement;
                title ??= window.FindName("NavTitleButton") as FrameworkElement;
                Check.True(title != null, $"QA·实测：{label} 能取到导航标题");
                if (title != null && bar != null)
                {
                    Check.Equal(HorizontalAlignment.Center, title.HorizontalAlignment,
                        $"QA·实测：{label} 标题 HorizontalAlignment=Center");
                    CheckCentered(label, title, bar, root);
                }

                // 主操作按钮：每个带主按钮的窗口都量一次通栏占比（MainWindow 只迁骨架，本来就没有）。
                CheckPrimaryButtons(window, root, label);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · QA·实测（{label}）异常：{ex.Message}");
                Check.True(false, $"QA·实测：{label} 骨架测量无异常");
            }
            finally
            {
                try { window?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
            }
        }

        // （二）行高 / 热区 / 通栏：在真实窗口里量，不只看样式属性
        MeasureTapTargets();
    }

    /// <summary>
    /// 导航标题是否真的落在导航栏的水平中心 —— 属性写了 <c>HorizontalAlignment=Center</c> 也要量一遍。
    ///
    /// 判据说明：导航栏是 <c>Auto | * | Auto</c> 三列，标题居中在**中间列**；
    /// 左右槽位不等宽时，标题相对「整条导航栏」会有偏移（QA 记录 Q-B1-01，已报 P3）。
    /// 这里守住「偏移不超过导航栏宽度的 5%」这条回归底线，
    /// 同时把每个窗口的真实偏移打进日志，肉眼可复核。
    /// </summary>
    private static void CheckCentered(string label, FrameworkElement title, FrameworkElement bar, FrameworkElement root)
    {
        try
        {
            var titleLeft = title.TranslatePoint(new Point(0, 0), root).X;
            var barLeft = bar.TranslatePoint(new Point(0, 0), root).X;
            Check.True(bar.ActualWidth > 0, $"QA·实测：{label} 导航栏已完成布局（宽 {bar.ActualWidth:F0}）");

            var offset = Math.Abs((titleLeft + title.ActualWidth / 2) - (barLeft + bar.ActualWidth / 2));
            Console.WriteLine(offset > 2.0
                ? $"   · ⚠ {label}：标题相对整条导航栏偏移 {offset:F1}px（左右槽位不等宽，见 QA 记录 Q-B1-01）"
                : $"   · {label}：标题居中（偏移 {offset:F1}px）");

            Check.True(offset <= Math.Max(2.0, bar.ActualWidth * 0.05),
                $"QA·实测：{label} 标题居中（偏移 {offset:F1}px ≤ 导航栏宽的 5%）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   · QA·实测（{label} 居中）无法换算坐标：{ex.Message}");
            Check.True(false, $"QA·实测：{label} 标题居中可测量");
        }
    }

    /// <summary>
    /// 主操作按钮通栏：宽度 ≥ 内容区 80%（PRD §4.2.2 / 架构 A5）。
    /// 登录窗口有两页、各带一个主按钮，切页后分别测量（未选中的页没有视觉树）。
    /// </summary>
    private static void CheckPrimaryButtons(Window window, FrameworkElement root, string label)
    {
        if (window is LoginWindow && window.FindName("Tabs") is TabControl tabs)
        {
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Arrange(root, window);
                var key = i == 0 ? "EmbeddedPrimaryAction" : "PrimaryAction";
                MeasurePrimary(window, root, key, $"{label}·第{i + 1}页");
            }

            tabs.SelectedIndex = 0;
            return;
        }

        MeasurePrimary(window, root, "PrimaryAction", label);
    }

    /// <summary>量一个主按钮的实际宽高与通栏占比（找不到就跳过：该窗口本来就没有主按钮）。</summary>
    private static void MeasurePrimary(Window window, FrameworkElement root, string elementName, string label)
    {
        var button = window.FindName(elementName) as FrameworkElement;
        if (button == null) return;

        Check.True(root.ActualWidth > 0, $"QA·实测：{label} 内容区已完成布局（宽 {root.ActualWidth:F0}）");
        if (root.ActualWidth <= 0) return;

        var ratio = button.ActualWidth / root.ActualWidth;
        Console.WriteLine($"   · {label} 主按钮通栏占比：{button.ActualWidth:F0}/{root.ActualWidth:F0} = {ratio:P0}");

        Check.True(button.ActualHeight >= 44,
            $"QA·实测：{label} 主按钮热区 ≥44（实际 {button.ActualHeight:F1}）");
        Check.True(ratio >= 0.8,
            $"QA·实测：{label} 主按钮通栏 ≥80%（{button.ActualWidth:F0}/{root.ActualWidth:F0} = {ratio:P0}）");
    }

    /// <summary>行高 ≥56、主按钮 ≥48、导航按钮 ≥44×44、输入框 ≥44、主按钮通栏 ≥80%（全部实测）。</summary>
    private static void MeasureTapTargets()
    {
        var window = new RoomListWindow(new GameClient());
        try
        {
            var root = window.Content as FrameworkElement;
            if (root != null) Arrange(root, window);

            // 列表行：LoadContent 后量一次
            var list = window.FindName("RoomList") as ListBox;
            var row = list?.ItemTemplate?.LoadContent() as FrameworkElement;
            Check.True(row != null, "QA·实测：房间列表行模板可加载");
            if (row != null)
            {
                row.Measure(new Size(900, 400));
                Console.WriteLine($"   · 房间列表行高实测：{row.DesiredSize.Height:F1}px（要求 ≥56）");
                Check.True(row.DesiredSize.Height >= 56,
                    $"QA·实测：列表行高 ≥56（实际 {row.DesiredSize.Height:F1}）");
            }

            // 通栏主按钮：量真实高度 + 通栏占比
            var primaryStyle = window.FindResource("BlockPrimaryButton") as Style;
            Check.True(primaryStyle != null, "QA·实测：能解析到 BlockPrimaryButton 样式");
            if (primaryStyle != null)
            {
                var b = new Button { Style = primaryStyle, Content = "一键开服" };
                b.Measure(new Size(343, 400));
                Console.WriteLine($"   · 通栏主按钮高实测：{b.DesiredSize.Height:F1}px（要求 ≥48）");
                Check.True(b.DesiredSize.Height >= 48,
                    $"QA·实测：通栏主按钮高 ≥48（实际 {b.DesiredSize.Height:F1}）");
            }

            // 导航栏热区按钮
            var navStyle = window.FindResource("NavIconButton") as Style;
            Check.True(navStyle != null, "QA·实测：能解析到 NavIconButton 样式");
            if (navStyle != null)
            {
                var n = new Button { Style = navStyle, Content = "设置" };
                n.Measure(new Size(343, 400));
                Console.WriteLine($"   · 导航按钮热区实测：Min {n.MinWidth}×{n.MinHeight}，高 {n.DesiredSize.Height:F1}px（要求 ≥44）");
                Check.True(n.MinWidth >= 44 && n.MinHeight >= 44 && n.DesiredSize.Height >= 44,
                    $"QA·实测：导航按钮热区 ≥44×44（Min {n.MinWidth}×{n.MinHeight}，实测高 {n.DesiredSize.Height:F1}）");
            }

            // 输入框
            var fieldStyle = window.FindResource("FieldInput") as Style;
            Check.True(fieldStyle != null, "QA·实测：能解析到 FieldInput 样式");
            if (fieldStyle != null)
            {
                var t = new TextBox { Style = fieldStyle, Text = "12345" };
                t.Measure(new Size(343, 400));
                Console.WriteLine($"   · 输入框高实测：{t.DesiredSize.Height:F1}px（要求 ≥44）");
                Check.True(t.DesiredSize.Height >= 44,
                    $"QA·实测：输入框高 ≥44（实际 {t.DesiredSize.Height:F1}）");
            }

            // 主按钮通栏：在同一个已布局的窗口里量真实占比
            var primary = window.FindName("PrimaryAction") as FrameworkElement;
            Check.True(primary != null, "QA·实测：房间列表能取到主操作按钮");
            if (primary != null && root != null && root.ActualWidth > 0)
            {
                Check.True(primary.ActualHeight >= 44,
                    $"QA·实测：主操作按钮热区 ≥44（实际 {primary.ActualHeight:F1}）");

                var ratio = primary.ActualWidth / root.ActualWidth;
                Console.WriteLine($"   · 房间列表主按钮通栏占比：{primary.ActualWidth:F0}/{root.ActualWidth:F0} = {ratio:P0}");
                Check.True(ratio >= 0.8,
                    $"QA·实测：主按钮通栏 ≥80%（{primary.ActualWidth:F0}/{root.ActualWidth:F0} = {ratio:P0}）");
            }
        }
        finally
        {
            try { window.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    /// <summary>8 个被迁移窗口的构造工厂（与工程师的 MigratedWindows 独立再列一份）。</summary>
    private static readonly (string Label, Func<Window> Create)[] WindowFactories =
    {
        ("登录窗口", () => new LoginWindow()),
        ("房间列表窗口", () => new RoomListWindow(new GameClient())),
        ("房间内主窗口", () => new MainWindow(new GameClient())),
        ("创建房间窗口", () => new CreateRoomWindow()),
        ("房主设置窗口", () => new HostSettingsWindow(new GameClient())),
        ("设置窗口", () => new SettingsWindow()),
        ("邀请码窗口", () => new InviteCodeWindow("WW1:QA-GUARD")),
        ("保留房间窗口", () => new KeepRoomWindow())
    };

    /// <summary>未显示的窗口也要完整走一次 Measure / Arrange，才读得到 ActualWidth / ActualHeight。</summary>
    private static void Arrange(FrameworkElement root, Window window)
    {
        var size = new Size(double.IsNaN(window.Width) ? 1024 : window.Width,
                            double.IsNaN(window.Height) ? 768 : window.Height);
        root.Measure(size);
        root.Arrange(new Rect(0, 0, size.Width, size.Height));
        root.UpdateLayout();
    }

    // ==================================================================
    //  源码扫描工具（与 AppShellGuard 各自实现，互不复用）
    // ==================================================================

    /// <summary>收集 ClientGUI 下的 XAML 源文件（跳过 obj 中间产物；可选跳过两套配色字典）。</summary>
    private static List<string> CollectXaml(string root, bool skipColorDictionaries)
    {
        var dir = Path.Combine(root, "ClientGUI");
        var files = new List<string>();
        if (!Directory.Exists(dir)) return files;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var name = Path.GetFileName(file);
            if (skipColorDictionaries && (name == "Colors.Dark.xaml" || name == "Colors.Light.xaml")) continue;

            files.Add(file);
        }

        return files;
    }

    /// <summary>从 XAML 文本里抽所有 x:Key 的名字（排序后返回）。</summary>
    private static List<string> ParseKeys(string xaml)
        => Regex.Matches(StripComments(xaml), @"x:Key=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

    /// <summary>取某个 SolidColorBrush 键在源码里的 Color 取值。</summary>
    private static string? ValueOf(string xaml, string key)
    {
        var m = Regex.Match(xaml, $@"x:Key=""{key}""\s+Color=""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
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
