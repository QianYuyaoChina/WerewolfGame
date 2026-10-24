using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClientGUI.Controls;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using ClientGUI.Views.Pages;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// v2.6.0 **B3（彩蛋 + 称号）在页面化之后的 QA 独立验收**（严过关 · 背靠背）。
///
/// 为什么这一批要重验：B3 原本挂在 <c>RoomListWindow</c>（旧窗口）上，S2 把彩蛋热区与
/// 称号胶囊搬到了 <c>RoomListPage</c> / <c>PlayerRowTemplate</c>。<b>搬家最容易丢的是
/// 「不报错」「空称号不渲染」「热区还在标题按钮上」这类静默语义</b>，所以五条逐项重证。
///
/// 两组挂载点：
/// <list type="bullet">
///   <item><see cref="RunLogicChecks"/> —— 默认组：5 击窗口三情形、称号校验口径、服务端权威、状态下发；</item>
///   <item><see cref="RunUiChecks"/> —— <c>--ui</c>：热区尺寸、称号胶囊显隐与两套主题的对比度、页面内浮层。</item>
/// </list>
/// </summary>
public static class B3QaGuard
{
    // ==================================================================
    //  默认组
    // ==================================================================

    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.6.0 B3：QA 独立验收（彩蛋 5 击 · 称号权威） ════════");

        CheckEggTapCounter();
        CheckTitleRules();
        CheckServerAuthority();
        CheckStateCarriesTitle();
        CheckClientNeverErrors();
    }

    /// <summary>
    /// PRD 8.3：**3 秒窗口内点 5 下**。三情形 + 两条边界 + 不重复触发。
    /// 判据全部用「时间戳进、布尔出」，不依赖真实时钟（这类逻辑最容易在重构时被悄悄改错）。
    /// </summary>
    private static void CheckEggTapCounter()
    {
        // ① 窗口内 5 击 → 第 5 击触发。
        var counter = new EggTapCounter();
        var fired = false;
        for (var i = 0; i < 4; i++)
            Check.False(counter.RegisterTap(i * 100L), $"B3-QA：第 {i + 1} 击（窗口内）不触发");

        fired = counter.RegisterTap(400);
        Check.True(fired, "B3-QA：3 秒窗口内第 5 击触发彩蛋");
        Check.Equal(0, counter.Count, "B3-QA：触发后计数立即清零（第 6 击不会重复弹）");
        Check.False(counter.RegisterTap(500), "B3-QA：第 6 击不重复触发");

        // ② 超出窗口 → 静默清零，从本次点击重新计数。
        var slow = new EggTapCounter();
        slow.RegisterTap(0);
        slow.RegisterTap(100);
        slow.RegisterTap(200);
        slow.RegisterTap(300);
        Check.False(slow.RegisterTap(3400), "B3-QA：第 5 击距首击超过 3 秒 → 不触发");
        Check.Equal(1, slow.Count, "B3-QA：超窗后静默清零并从本次点击重新开始（Count=1）");

        // ③ 边界：恰好 3000ms 应当**触发**（判据是 ≤，不是 <）。
        var edge = new EggTapCounter();
        edge.RegisterTap(0);
        edge.RegisterTap(750);
        edge.RegisterTap(1500);
        edge.RegisterTap(2250);
        Check.True(edge.RegisterTap(3000), "B3-QA：首末击恰好 3000ms → 仍触发（窗口判据是 ≤）");

        var over = new EggTapCounter();
        over.RegisterTap(0);
        over.RegisterTap(750);
        over.RegisterTap(1500);
        over.RegisterTap(2250);
        Check.False(over.RegisterTap(3001), "B3-QA：3001ms → 不触发（多 1ms 就不算）");

        // ④ 慢速连点（每次间隔 1000ms）：第 4 击时首末正好 3000 仍累积，第 5 击必然超窗。
        var slow2 = new EggTapCounter();
        var hits = 0;
        for (var i = 0; i < 5; i++)
            if (slow2.RegisterTap(i * 1000L)) hits++;
        Check.Equal(0, hits, "B3-QA：每秒点一下（共 5 下）不触发（窗口是首末 3 秒，不是相邻 3 秒）");

        // ⑤ 自定义窗口：3 击 / 500ms 也要按同一口径工作。
        var custom = new EggTapCounter(requiredTaps: 3, windowMs: 500);
        Check.False(custom.RegisterTap(0), "B3-QA：自定义 3 击口径 —— 第 1 击不触发");
        Check.False(custom.RegisterTap(100), "B3-QA：自定义 3 击口径 —— 第 2 击不触发");
        Check.True(custom.RegisterTap(400), "B3-QA：自定义 3 击口径 —— 第 3 击在 500ms 内触发");
    }

    /// <summary>
    /// 称号口径（PRD 4.7 / 架构 U5）：汉字与全角算 2、其余算 1，上限 <c>16</c>；
    /// 控制字符一律拒绝；空 / 全空白**合法**且含义是「清除」。
    /// </summary>
    private static void CheckTitleRules()
    {
        Check.Equal(4, TitleRules.WeightedLength("测试"), "B3-QA：加权长度 —— 2 个汉字 = 4");
        Check.Equal(2, TitleRules.WeightedLength("ab"), "B3-QA：加权长度 —— 2 个字母 = 2");
        Check.Equal(3, TitleRules.WeightedLength("狼a"), "B3-QA：加权长度 —— 汉字 2 + 字母 1 = 3");

        // 上限两侧：8 个汉字 = 16 通过；9 个汉字 = 18 拒绝。
        var eightCjk = new string('狼', 8);
        var nineCjk = new string('狼', 9);
        Check.Equal(16, TitleRules.WeightedLength(eightCjk), "B3-QA：8 个汉字加权正好 16");
        Check.True(TitleRules.Validate(eightCjk).ok, "B3-QA：8 个汉字（=16）通过");
        Check.False(TitleRules.Validate(nineCjk).ok, "B3-QA：9 个汉字（=18）拒绝");
        Check.Equal(ServerMessages.TitleTooLong, TitleRules.Validate(nineCjk).error,
            "B3-QA：超长的错误文案是「太长」而不是别的");

        // 16 个字母通过 / 17 个拒绝（与「8 汉字」等价的另一半）。
        Check.True(TitleRules.Validate(new string('a', 16)).ok, "B3-QA：16 个字母通过");
        Check.False(TitleRules.Validate(new string('a', 17)).ok, "B3-QA：17 个字母拒绝");

        // 混合：7 汉字 + 2 字母 = 16 通过；再加 1 字母 = 17 拒绝。
        var mixed = new string('狼', 7) + "ab";
        Check.Equal(16, TitleRules.WeightedLength(mixed), "B3-QA：混合称号加权 7×2+2×1=16");
        Check.True(TitleRules.Validate(mixed).ok, "B3-QA：混合称号（=16）通过");
        Check.False(TitleRules.Validate(mixed + "c").ok, "B3-QA：混合称号（=17）拒绝");

        // 控制字符：换行 / 制表 / 退格都算，且文案是「非法字符」。
        foreach (var bad in new[] { "狼\n人", "狼\t人", "狼\u0001人" })
        {
            var (ok, error) = TitleRules.Validate(bad);
            Check.False(ok, $"B3-QA：含控制字符的称号被拒绝（{bad.Replace("\n", "\\n").Replace("\t", "\\t")}）");
            Check.Equal(ServerMessages.TitleBadChars, error, "B3-QA：控制字符的错误文案是「非法字符」");
        }

        // 空 / 全空白合法（= 清除），且不能被当成「非法字符」。
        Check.True(TitleRules.Validate("").ok, "B3-QA：空称号合法（含义是清除）");
        Check.True(TitleRules.Validate("    ").ok, "B3-QA：全空白称号合法（清除）");
        Check.Equal("狼", TitleRules.Normalize("  狼  "), "B3-QA：Normalize 去掉首尾空白");
    }

    /// <summary>
    /// 服务端权威（PRD 4.8）：**只能改自己** —— 请求体里没有用户名字段，
    /// 归属一律按「连接绑定的用户名」；彩蛋口令与「未命中不报错」都在服务端。
    /// </summary>
    private static void CheckServerAuthority()
    {
        var root = FindRoot();
        Check.True(root != null, "B3-QA：能定位仓库根目录");
        if (root == null) return;

        var protocol = File.ReadAllText(Path.Combine(root, "Werewolf.Client.Share", "AccountProtocol.cs"));

        // ① 写称号的 Payload 只有 Title（没有 Username / User / Name 这类字段可被伪造）。
        var payloadBody = BraceBody(protocol, "class SetTitlePayload");
        Check.True(payloadBody.Contains("Title", StringComparison.Ordinal),
            "B3-QA：SetTitlePayload 有 Title 字段");
        foreach (var forbidden in new[] { "Username", "UserName", "User", "PlayerName", "Account" })
            Check.False(payloadBody.Contains(forbidden, StringComparison.Ordinal),
                $"B3-QA：SetTitlePayload **不得**含 {forbidden}（否则等于允许改别人的称号）");

        var hub = File.ReadAllText(Path.Combine(root, "WerewolfGame", "GameHub.cs"));

        // ② 服务端从连接取用户名，且没有从请求体取。
        //    注意：这里必须用**完整签名**定位，`Ops.SetTitle => await HandleSetTitleAsync(...)` 那行
        //    也含同样的方法名，用短名会截到 switch 分支里去（源码级守卫最容易在这里假绿 / 假红）。
        var setTitle = BraceBody(hub, "private async Task<RpcReply> HandleSetTitleAsync(");
        Check.True(setTitle.Contains("GetUsername(Context.ConnectionId)", StringComparison.Ordinal),
            "B3-QA：改称号的用户名来自连接（GetUsername(Context.ConnectionId)）");
        Check.False(setTitle.Contains("req.Username", StringComparison.Ordinal),
            "B3-QA：改称号不从请求体读用户名");

        // ③ 校验用共享的 TitleRules（两端同一口径），不是服务端另写一份。
        Check.True(setTitle.Contains("TitleRules.Validate", StringComparison.Ordinal),
            "B3-QA：服务端用共享的 TitleRules.Validate 校验（两端不会漂移）");
        Check.True(setTitle.Contains("TitleRules.Normalize", StringComparison.Ordinal),
            "B3-QA：服务端先 Normalize 再校验（首尾空白不算长度）");

        // ④ 彩蛋：命中判定在服务端，客户端只认 Kind；未命中不报错。
        var egg = BraceBody(hub, "private RpcReply HandleEgg(");
        Check.True(egg.Contains("EggCodes.Surprise", StringComparison.Ordinal),
            "B3-QA：口令 surprise 的命中判定在服务端");
        Check.True(egg.Contains("EggKinds.Title", StringComparison.Ordinal),
            "B3-QA：命中后下发 Kind=title（客户端据此切称号编辑态）");
        Check.True(egg.Contains("EggPending", StringComparison.Ordinal),
            "B3-QA：未命中给占位文案（EggPending），不是错误");
        Check.False(egg.Contains("RpcReply.Error", StringComparison.Ordinal),
            "B3-QA：彩蛋 op **永远不返回错误**（PRD 3.6）");
    }

    /// <summary>称号随 <c>roomlist</c> / <c>state</c> 自然下发：房主列与房间内玩家行两处都要有。</summary>
    private static void CheckStateCarriesTitle()
    {
        var root = FindRoot();
        if (root == null) return;

        var hub = File.ReadAllText(Path.Combine(root, "WerewolfGame", "GameHub.cs"));
        var titleOfCalls = CountOf(hub, "TitleOf(");
        Check.True(titleOfCalls >= 2,
            $"B3-QA：PlayerDto 的 Title 至少两处从账号库取（房间列表 + 房间状态；实际 {titleOfCalls} 处）");

        var protocol = File.ReadAllText(Path.Combine(root, "Werewolf.Client.Share", "AccountProtocol.cs"));
        Check.True(protocol.Contains("class SetTitlePayload", StringComparison.Ordinal),
            "B3-QA：找到 SetTitlePayload 定义");

        // 用反射查 DTO 字段（比在某个文件里搜字符串稳：DTO 可能挪文件、也可能被 part 类拆开）。
        Check.True(typeof(RoomInfoDto).GetProperty("HostTitle") != null,
            "B3-QA：RoomInfoDto 带 HostTitle（房间列表房主列要用）");
        Check.True(typeof(PlayerDto).GetProperty("Title") != null,
            "B3-QA：PlayerDto 带 Title（房间内玩家行要用）");

        // 客户端两处渲染点：房间列表的房主列 + 房间内玩家行（后者在共享的 PlayerRowTemplate 里）。
        var listPage = File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "RoomListPage.xaml"));
        Check.True(listPage.Contains("TitleChip", StringComparison.Ordinal),
            "B3-QA：房间列表页渲染称号胶囊");

        var panels = File.ReadAllText(Path.Combine(root, "ClientGUI", "Themes", "Panels.xaml"));
        var rowTemplate = SliceBetween(panels, "x:Key=\"PlayerRowTemplate\"", "</DataTemplate>");
        Check.True(rowTemplate.Contains("TitleChip", StringComparison.Ordinal),
            "B3-QA：房间内玩家行模板渲染称号胶囊");
        Check.True(rowTemplate.Contains("{Binding Title}", StringComparison.Ordinal),
            "B3-QA：玩家行的胶囊绑定 PlayerRow.Title");
    }

    /// <summary>客户端侧：未命中的口令**不许**显示成错误（PRD 3.6），只有真失败才报错。</summary>
    private static void CheckClientNeverErrors()
    {
        var root = FindRoot();
        if (root == null) return;

        var eggVm = File.ReadAllText(Path.Combine(root, "ClientGUI", "ViewModels", "EggViewModel.cs"));
        var submit = BraceBody(eggVm, "SubmitAsync");

        Check.True(submit.Contains("EggPending", StringComparison.Ordinal),
            "B3-QA：未命中时客户端用服务端给的占位文案");
        Check.True(submit.Contains("SetMessage(string.IsNullOrEmpty(reply.Message) ? ServerMessages.EggPending : reply.Message, false)",
                StringComparison.Ordinal) ||
            Regex.IsMatch(submit, @"EggPending[^;]*,\s*false\s*\)"),
            "B3-QA：占位文案以「非错误」方式展示（第二个参数 false）");
        Check.True(submit.Contains("EggKinds.Title", StringComparison.Ordinal),
            "B3-QA：客户端只按 Kind 切称号编辑态（不根据文案猜）");
    }

    // ==================================================================
    //  --ui：真实布局
    // ==================================================================

    public static void RunUiChecks()
    {
        Console.WriteLine("   —— v2.6.0 B3：QA 独立验收（热区 / 胶囊 / 页面内浮层） ——");

        CheckEggHotArea();
        CheckTitleChipVisibility();
        CheckTitleChipThemeContrast();
        CheckEggOverlayIsInPage();
    }

    /// <summary>5 击热区仍在导航栏标题按钮上，且热区尺寸够点（≥44 高）。</summary>
    private static void CheckEggHotArea()
    {
        try
        {
            var page = new RoomListPage(new AppSession());
            Layout(page, 960, 640);

            var button = page.FindName("NavTitleButton") as FrameworkElement;
            Check.True(button != null, "B3-QA：房间列表页的导航标题是按钮（5 击热区还在标题上）");
            if (button == null) return;

            Console.WriteLine($"   · 彩蛋热区（标题按钮）：{button.ActualWidth:F0}×{button.ActualHeight:F0}px");
            Check.True(button.ActualHeight >= 44,
                $"B3-QA：彩蛋热区高度 ≥44（实测 {button.ActualHeight:F0}，手指点得中）");

            var xaml = File.ReadAllText(Path.Combine(FindRoot()!, "ClientGUI", "Views", "Pages", "RoomListPage.xaml"));
            Check.True(xaml.Contains("NavTitle_OnPreviewMouseLeftButtonDown", StringComparison.Ordinal),
                "B3-QA：热区接的是**按下**事件（连点被系统识别成双击也照常计数，PRD 8.3 未行）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 彩蛋热区自检异常：" + ex.Message);
            Check.True(false, "B3-QA：彩蛋热区可检查");
        }
    }

    /// <summary>无称号 → 整个胶囊 Collapsed（不留空框）；有称号 → 显示文本。</summary>
    private static void CheckTitleChipVisibility()
    {
        try
        {
            var chip = new TitleChip();
            Check.Equal(Visibility.Collapsed, chip.Visibility,
                "B3-QA：初始无称号时胶囊 Collapsed（不留空框占位）");

            chip.Text = "   ";
            Check.Equal(Visibility.Collapsed, chip.Visibility,
                "B3-QA：全空白称号同样 Collapsed（不是只有空串才收起）");

            chip.Text = "狼王";
            Check.Equal(Visibility.Visible, chip.Visibility, "B3-QA：有称号时胶囊显示");

            // 量「按内容自适应」必须用 DesiredSize：直接 Arrange 成 200 宽的话 ActualWidth 恒为 200，
            // 那等于什么都没验（胶囊是不是通栏都看不出来）。
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = chip.DesiredSize;

            var text = chip.FindName("ChipText") as TextBlock;
            Check.True(text != null, "B3-QA：胶囊内有文本元素");
            Check.Equal("狼王", text?.Text ?? "", "B3-QA：胶囊显示的就是称号本身");

            Console.WriteLine($"   · 称号胶囊：按内容自适应宽 {desired.Width:F1}px（高 {desired.Height:F1}px）");
            Check.True(desired.Width > 0 && desired.Width < 120,
                $"B3-QA：胶囊按内容自适应（实测宽 {desired.Width:F1}，不是通栏）");

            chip.Text = "";
            Check.Equal(Visibility.Collapsed, chip.Visibility, "B3-QA：清掉称号后胶囊重新收起（清除称号即时生效）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 称号胶囊自检异常：" + ex.Message);
            Check.True(false, "B3-QA：称号胶囊可检查");
        }
    }

    /// <summary>
    /// 金色胶囊在**深浅两套主题**下都要读得出来：从主题文件解析色键，
    /// 按 WCAG 相对亮度算对比度（小字 12px，按 3:1 的「大字/图形」档做下限，同时要求是金色系）。
    /// </summary>
    private static void CheckTitleChipThemeContrast()
    {
        var root = FindRoot();
        if (root == null) return;

        foreach (var theme in new[] { "Dark", "Light" })
        {
            var path = Path.Combine(root, "ClientGUI", "Themes", $"Colors.{theme}.xaml");
            var xaml = File.ReadAllText(path);

            var text = ParseBrush(xaml, "TitleTextBrush");
            var border = ParseBrush(xaml, "TitleBorderBrush");
            var card = ParseBrush(xaml, "CardBrush");

            Check.True(text != null && border != null && card != null,
                $"B3-QA：{theme} 主题里称号三个色键齐全（文字 / 边框 / 卡片底）");
            if (text == null || border == null || card == null) continue;

            var contrast = Contrast(text.Value, card.Value);
            Console.WriteLine($"   · {theme} 主题：称号字 {text} / 卡片底 {card} → 对比度 {contrast:F2}:1");

            Check.True(contrast >= 3.0,
                $"B3-QA：{theme} 主题下称号文字对比度 ≥3:1（实测 {contrast:F2}）");
            Check.True(IsGold(text.Value),
                $"B3-QA：{theme} 主题下称号字是金色系（R>G>B 且 R-B 有明显差）");
            Check.True(IsGold(border.Value), $"B3-QA：{theme} 主题下胶囊描边同为金色系");
        }
    }

    /// <summary>彩蛋输入框是**页面内浮层**（不是 Window），页面退场时必须收起。</summary>
    private static void CheckEggOverlayIsInPage()
    {
        var root = FindRoot();
        if (root == null) return;

        var xaml = File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "RoomListPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "ClientGUI", "Views", "Pages", "RoomListPage.xaml.cs"));

        Check.True(xaml.Contains("x:Name=\"EggOverlay\"", StringComparison.Ordinal),
            "B3-QA：彩蛋输入框是页面内的 OverlayDialog（不是新开一个 Window）");
        Check.False(code.Contains("new EggWindow", StringComparison.Ordinal),
            "B3-QA：没有为了彩蛋再开 Window（页面化之后不该有新窗口）");

        var onLeave = BraceBody(code, "public void OnLeave()");
        Check.True(onLeave.Contains("EggOverlay", StringComparison.Ordinal),
            "B3-QA：房间列表页退场时收起彩蛋浮层（否则复用时残留一个开着的输入框）");
    }

    // ==================================================================
    //  自用工具
    // ==================================================================

    /// <summary>取一个类型 / 方法的花括号体（按配平截取，避免截到别的方法）。</summary>
    private static string BraceBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";

        var open = source.IndexOf('{', start);
        if (open < 0) return "";

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[start..(i + 1)];
            }
        }

        return source[start..];
    }

    /// <summary>按「起止标记」截一段（XAML 的属性里有 <c>{Binding}</c>，花括号配平不适用）。</summary>
    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return "";

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static Color? ParseBrush(string xaml, string key)
    {
        var match = Regex.Match(xaml, $"x:Key=\"{Regex.Escape(key)}\"[^>]*Color=\"#([0-9A-Fa-f]{{6,8}})\"");
        if (!match.Success) return null;

        var hex = match.Groups[1].Value;
        if (hex.Length == 6) hex = "FF" + hex;

        var a = int.Parse(hex[..2], NumberStyles.HexNumber);
        var r = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
        var g = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
        var b = int.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
        return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
    }

    private static double Contrast(Color fg, Color bg)
    {
        var l1 = RelativeLuminance(fg);
        var l2 = RelativeLuminance(bg);
        var light = Math.Max(l1, l2);
        var dark = Math.Min(l1, l2);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static bool IsGold(Color c) => c.R > c.G && c.G > c.B && c.R - c.B >= 40;

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ClientGUI")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}
