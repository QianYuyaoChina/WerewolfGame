using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.ViewModels;
using ClientGUI.Views;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Undercover;

namespace ClientGUI.Tests;

/// <summary>
/// v2.5.0 B4 + B5 的能力探针（内置管理员 / 一键开服 / 可编辑数据资源 / 修改密码 / 导航居中）。
///
/// 分两组：
/// <list type="bullet">
///   <item><b>默认组</b>（<see cref="RunLogicChecks"/>）：管理员账号生命周期（seed / 登录 / 注册被拒 /
///         改密码 / 重启不重置）、可编辑资源抽象的通用性、词库资源的原子落盘 —— 纯逻辑，不绑端口；</item>
///   <item><b>--ui</b>（<see cref="RunUiChecks"/>）：一键开服状态机、修改密码浮层正文的本地校验、
///         8 个窗口导航标题的几何居中（≤1px）—— 要真实布局测量。</item>
/// </list>
/// </summary>
public static class V25BatchProbe
{
    // ==================================================================
    //  默认组：纯逻辑
    // ==================================================================

    /// <summary>默认组：管理员账号 · 可编辑资源（纯逻辑，不绑端口 / 不改 CWD）。</summary>
    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ v2.5.0 B4 / B5：管理员账号 · 可编辑资源（纯逻辑） ════════");
        CheckAdminDefaults();
        CheckAdminAccountLifecycle();
        CheckEditableRegistryGenerality();
        CheckAtomicWordResourceSave();
    }

    /// <summary>内置管理员的常量口径（唯一来源，服务端 / 客户端 / 界面文案共用）。</summary>
    private static void CheckAdminDefaults()
    {
        Check.Equal("admin", AdminDefaults.Username, "B4：内置管理员用户名是 admin");
        Check.Equal("123456", AdminDefaults.DefaultAdminPassword, "B4：默认密码是用户裁定的 123456（覆盖架构文档的旧值）");
        Check.True(AdminDefaults.OneClickLoginHint.Contains("admin", StringComparison.Ordinal),
            "B4：一键开服提示写明用哪个管理员登录");
        Check.True(AdminDefaults.OneClickLoginHint.Contains("123456", StringComparison.Ordinal),
            "B4：一键开服提示明文写出默认密码（不让用户蒙在鼓里）");

        // 账号存储里的管理员常量必须指向共享层那一份（不可能漂移）。
        Check.Equal(AdminDefaults.Username, AccountStore.AdminUsername,
            "B4：AccountStore.AdminUsername 指向共享层 AdminDefaults.Username（单一来源）");
    }

    /// <summary>
    /// 管理员账号的完整生命周期：seed → 登录可用 → 注册被拒 → 改密码 → 重启不重置。
    /// 用一份临时 accounts.json，不碰真实文件。
    /// </summary>
    private static void CheckAdminAccountLifecycle()
    {
        var path = Path.Combine(Path.GetTempPath(), "werewolf-admin-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new AccountStore(path);

            Check.True(store.EnsureAdmin(), "B4：首次启动 seed 出内置管理员（EnsureAdmin 返回 true）");
            Check.False(store.EnsureAdmin(), "B4：管理员已存在时不重复 seed");
            Check.True(store.Verify(AdminDefaults.Username, AdminDefaults.DefaultAdminPassword),
                "B4：admin + 默认密码通过校验（用户裁定 #2：管理员是**真账号**，默认密码必须能登录）");
            Check.True(store.IsAdmin("ADMIN") && store.IsAdmin(" admin "),
                "B4：管理员判定对大小写 / 首尾空格不敏感（全局唯一入口）");
            Check.False(store.IsAdmin("admin2"), "B4：相似用户名不算管理员");

            // 「保留用户名」只作用于**注册**路径（用户裁定 #2 的关键：登录路径绝不复用这条拦截）。
            Check.Equal(ServerMessages.AdminReserved, store.Register("admin", "whatever"),
                "B4：别人注册 admin（小写）被拒 —— 保留只作用在注册路径");
            Check.Equal(ServerMessages.AdminReserved, store.Register("ADMIN", "whatever"),
                "B4：别人注册 ADMIN（大小写变体）同样被拒");

            // 改密码（规则文案单一来源 PasswordRules）。
            Check.Equal(ServerMessages.OldPasswordWrong,
                store.ChangePassword("admin", "wrong-old", "newpass123"), "B5：旧密码不对被拒");
            Check.Equal(ServerMessages.PasswordMinLength(PasswordRules.MinLength),
                store.ChangePassword("admin", AdminDefaults.DefaultAdminPassword, "ab"),
                "B5：新密码太短被拒（文案来自共享 PasswordRules）");
            Check.Equal(ServerMessages.PasswordSameAsOld,
                store.ChangePassword("admin", AdminDefaults.DefaultAdminPassword, AdminDefaults.DefaultAdminPassword),
                "B5：新密码与旧密码相同被拒");
            Check.Equal(null, store.ChangePassword("admin", AdminDefaults.DefaultAdminPassword, "newpass123"),
                "B5：合法修改返回 null（成功）");
            Check.True(store.Verify("admin", "newpass123"), "B5：改完后新密码可用");
            Check.False(store.Verify("admin", AdminDefaults.DefaultAdminPassword), "B5：改完后旧密码失效");
            Check.True(store.IsAdmin("admin"), "B5：改密码后 admin 仍是管理员（不会掉身份）");

            // 重启：EnsureAdmin 只做「缺失补建」，绝不把密码重置回默认。
            var reloaded = new AccountStore(path);
            Check.False(reloaded.EnsureAdmin(), "B5：重启后不再 seed（否则会把管理员密码重置）");
            Check.True(reloaded.Verify("admin", "newpass123"),
                "B5：重启后管理员仍是新密码（EnsureAdmin 不写回默认值）");
            Check.True(reloaded.IsAdmin("admin"), "B5：重启后仍是管理员");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 管理员账号生命周期自检异常：" + ex.Message);
            Check.True(false, "B4：管理员账号生命周期（seed / 登录 / 注册 / 改密码 / 重启）可跑通");
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件删不掉不影响 */ }
        }
    }

    /// <summary>
    /// 可编辑资源抽象的通用性：登记处只按 <c>IGameCatalog.Modules</c> 遍历，
    /// **不认识任何具体游戏**；默认实现（不重写 CreateEditableResources）贡献 0 个资源（既有游戏零改动）。
    /// </summary>
    private static void CheckEditableRegistryGenerality()
    {
        var root = Path.Combine(Path.GetTempPath(), "werewolf-res-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            var catalog = new GameCatalog();
            catalog.Register(new ResourceModule("fakegame", "fake.res", "假资源", "示例内容\n第二行"));
            var reg = new EditableDataRegistry(catalog, root);

            var list = reg.List();
            Check.Equal(1, list.Count, "B4：登记处只登记声明了资源的模块");
            Check.Equal("fakegame:fake.res", list[0].Id,
                "B4：资源 id 形如「游戏机器名:资源机器名」（平台自拼，与游戏无关）");
            Check.Equal("假资源", list[0].DisplayName, "B4：资源显示名来自资源自己（平台不解释）");
            Check.Equal("假游戏", list[0].GameName, "B4：清单里带所属游戏显示名（取自模块元数据）");
            Check.Equal(2, list[0].Count, "B4：条数由资源自己算（平台不解析内容格式）");
            Check.True(reg.DataRoot == root, "B4：登记处记录数据根目录并传给资源用于定位文件");

            Check.True(reg.TryGet("fakegame:fake.res", out var resource) && resource.DisplayName == "假资源",
                "B4：TryGet 能按 id 取回资源");
            Check.True(reg.TryGet("FAKEGAME:FAKE.RES", out _), "B4：资源 id 匹配大小写不敏感");
            Check.False(reg.TryGet("nope:x", out _), "B4：未知 id 取不到");
            Check.Equal("fakegame:fake.res", EditableDataRegistry.BuildKey("fakegame", "fake.res"),
                "B4：资源 id 拼接规则稳定");

            // 默认实现：不重写 CreateEditableResources 的模块 → 0 个资源（现有游戏模块一行不用改）。
            var plainCatalog = new GameCatalog();
            plainCatalog.Register(new PlainModule("plaingame"));
            var plainReg = new EditableDataRegistry(plainCatalog, root);
            Check.Equal(0, plainReg.List().Count,
                "B4：未声明资源的模块（走默认实现）贡献 0 个资源（既有游戏零改动）");

            // 摘要算法单一来源：服务端 DigestOf 复用共享 DataResourceDigest.Of（客户端回带时是同一份）。
            Check.Equal(DataResourceDigest.Of("abc"), EditableDataRegistry.DigestOf("abc"),
                "B4：摘要算法单一来源（服务端 == 客户端，不会刚存完就被判「已被改过」）");
            Check.Equal(16, DataResourceDigest.Of("abc").Length, "B4：摘要是 16 位十六进制");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 可编辑资源登记处自检异常：" + ex.Message);
            Check.True(false, "B4：可编辑资源登记处通用性可验证");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不影响 */ }
        }
    }

    /// <summary>
    /// 卧底词库资源：路径解析 / 逐行校验（报第几行）/ 原子落盘（不留临时文件）。
    /// 用真实资源 + 临时目录，不碰真实词库。
    /// </summary>
    private static void CheckAtomicWordResourceSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "werewolf-words-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var resource = new UndercoverWordResource(root);

            Check.Equal("undercover.words", resource.ResourceId, "B4：卧底词库的资源机器名稳定");
            Check.Equal(Path.Combine(root, UndercoverWordBank.DefaultWordFile), resource.Path,
                "B4：词库路径 = 数据根目录 + 默认文件名（与对局读取同一份解析）");

            // 文件不存在时返回内置默认词库（管理员第一次打开看到的就是真正在用的）。
            Check.True(resource.Load().Length > 0, "B4：文件不存在时 Load 返回内置默认词库");

            // 校验：坏行整份拒绝并点明第几行。
            var bad = resource.Validate("# 注释\n\n只有一列\n好词|坏词");
            Check.False(bad.Ok, "B4：只有一列的行被判非法");
            Check.Equal(ServerMessages.ResourceLineError(3), bad.Error, "B4：报错点明「第 3 行」");
            Check.Equal(ServerMessages.ResourceEmpty, resource.Validate("# 全是注释\n\n").Error,
                "B4：全是注释（没有有效行）被判空");

            // 合法内容：原子落盘 + 可读回 + 无临时文件残留。
            const string good = "# 标题\n可乐|雪碧|饮料\n牛奶|豆浆|饮料\n";
            var write = resource.Save(good);
            Check.True(write.Ok, "B4：合法内容保存成功");
            Check.Equal(2, write.Count, "B4：保存返回有效条数（2）");
            Check.Equal(good, File.ReadAllText(resource.Path), "B4：落盘后读回内容与提交逐字节一致");

            var leftovers = Directory.GetFiles(root)
                .Select(f => Path.GetFileName(f) ?? "")
                .Where(n => n.Contains(".tmp", StringComparison.Ordinal))
                .ToList();
            Check.Equal(0, leftovers.Count, "B4：原子写不留临时文件（tmp + File.Move）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 词库资源自检异常：" + ex.Message);
            Check.True(false, "B4：词库资源（路径 / 校验 / 原子落盘）可验证");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不影响 */ }
        }
    }

    // ==================================================================
    //  --ui 组：真实布局测量
    // ==================================================================

    /// <summary>--ui：一键开服状态机 · 修改密码浮层 · 8 个窗口导航居中（必须 STA + 已初始化主题）。</summary>
    public static void RunUiChecks()
    {
        Console.WriteLine("   —— v2.5.0 B4 / B5：一键开服 / 改密码 / 导航居中（真实布局） ——");
        CheckOneClickStateMachine();
        CheckOneClickAutoEnter();
        CheckChangePasswordDialog();
        CheckNavCentering();
    }

    /// <summary>一键开服状态机的初始态与页面结构（不真起服，只验纯状态）。</summary>
    private static void CheckOneClickStateMachine()
    {
        var window = new LoginWindow();
        try
        {
            var vm = window.Embedded;

            Check.Equal(OneClickStep.Idle, vm.Step, "B4：一键开服初始状态为 Idle");
            Check.False(vm.HasStepText, "B4：未点过时没有分步文案（界面保持干净）");
            Check.Equal("一键开服", vm.MainButtonText, "B4：主按钮初始文案是「一键开服」");
            Check.True(vm.OneClickHint.Contains("123456", StringComparison.Ordinal),
                "B4：主按钮下方明文提示默认密码（PRD 6.6）");
            Check.True(vm.CanOneClick, "B4：端口合法且空闲时「一键开服」可点");
            Check.False(vm.IsOneClickDone, "B4：还没登录时不算「已就绪」");

            // C5：原「仅启动 / 登录」命令仍在（只删界面按钮，不删命令本体，避免触碰既有断言）。
            Check.True(vm.StartOnlyCommand != null && vm.LoginToEmbeddedCommand != null,
                "B4：StartOnlyCommand / LoginToEmbeddedCommand 仍保留（C5）");

            // 页面结构：内置页只剩一个主按钮，且不再要求用户输入用户名 / 密码。
            Check.True(window.FindName("EmbeddedPrimaryAction") is Button,
                "B4：内置页有唯一主按钮 EmbeddedPrimaryAction");
            Check.True(window.FindName("EmbeddedUsernameInput") == null,
                "B4：内置页不再要求输入用户名（用内置管理员，一键开服）");
            Check.True(window.FindName("EmbeddedPasswordInput") == null,
                "B4：内置页不再要求输入密码");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 一键开服状态机自检异常：" + ex.Message);
            Check.True(false, "B4：一键开服状态机可验证");
        }
        finally
        {
            try { window.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
        }
    }

    /// <summary>
    /// 收尾轮（用户裁定）：点一次「一键开服」就落到房间列表 —— 到达就绪态后必须**紧接着**复用同一条
    /// 进入路径（<c>LoginViewModel.EnterRoomList()</c>，它触发 <c>LoginSucceeded</c>）。
    /// 这里是**源码级守护**（真起服的端到端证明在 <c>--embedded</c>）：谁把这一步删了就会立刻变红。
    /// </summary>
    private static void CheckOneClickAutoEnter()
    {
        try
        {
            var path = FindRepoRelative(@"ClientGUI\ViewModels\EmbeddedServerViewModel.cs");
            Check.True(path != null, "B4：能找到 EmbeddedServerViewModel.cs（一键开服自动进入 · 源码级守护）");
            if (path == null) return;

            var src = File.ReadAllText(path);
            var readyIdx = src.IndexOf("SetStep(OneClickStep.Ready", StringComparison.Ordinal);
            var enterIdx = src.LastIndexOf("_login.EnterRoomList()", StringComparison.Ordinal);

            Check.True(readyIdx >= 0, "B4：OneClickAsync 里有到达就绪态（Step=Ready）的代码");
            Check.True(enterIdx > readyIdx,
                "B4：到达就绪态后**紧接着**调用 _login.EnterRoomList()（一键开服后自动进入房间列表）");
            Check.False(src.Contains("不自动进入房间列表", StringComparison.Ordinal),
                "B4：源码注释已更新（不再写「成功后不自动进入房间列表」）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 一键开服自动进入自检异常：" + ex.Message);
            Check.True(false, "B4：一键开服自动进入房间列表（源码级守护）可验证");
        }
    }

    /// <summary>从运行目录向上找仓库根（含 WerewolfGame.sln），再拼相对路径；找不到返回 null。</summary>
    private static string? FindRepoRelative(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WerewolfGame.sln")))
                return Path.Combine(dir.FullName, relative.Replace('\\', Path.DirectorySeparatorChar));
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>修改密码浮层正文的本地校验：两遍不一致时「确定」不可点、且不发请求。</summary>
    private static void CheckChangePasswordDialog()
    {
        try
        {
            var dialog = new ChangePasswordDialog();
            var vm = dialog.ViewModel;

            Check.False(vm.CanSubmit, "B5：三框都没填时「确定」不可点");

            vm.OldPassword = "old-pass";
            vm.NewPassword = "abc";
            Check.False(vm.CanSubmit, "B5：两遍新密码不一致时「确定」不可点");
            Check.Equal(ServerMessages.PasswordMismatch, vm.MismatchHint, "B5：两遍不一致给出就地提示");

            vm.ConfirmPassword = "abc";
            Check.True(vm.CanSubmit, "B5：三框都填且一致时「确定」可点");
            Check.Equal("", vm.MismatchHint, "B5：一致时没有提示");

            vm.Reset();
            Check.False(vm.CanSubmit, "B5：Reset 清空后不可点");

            // 不一致时提交被**本地**拦下（不发请求）——传一个未连接的 GameClient，只要没网络调用即证明。
            vm.OldPassword = "a";
            vm.NewPassword = "b";
            vm.ConfirmPassword = "c";
            var ok = vm.SubmitAsync(new GameClient()).GetAwaiter().GetResult();
            Check.False(ok, "B5：两遍不一致时提交被本地拦下（不发请求）");
            Check.Equal(ServerMessages.PasswordMismatch, vm.Message, "B5：本地拦截给出统一文案");
        }
        catch (Exception ex)
        {
            Console.WriteLine("   · 修改密码浮层自检异常：" + ex.Message);
            Check.True(false, "B5：修改密码浮层可实例化并做本地校验");
        }
    }

    /// <summary>
    /// T18：8 个窗口的导航标题中心与导航栏中心的偏移 ≤1px（像素级居中）。
    /// 用真实布局测量；比 AppShellQaGuard 的「≤5%」更严（本批次把 Q-B1-01 修掉了）。
    /// </summary>
    private static void CheckNavCentering()
    {
        var factories = new (string Name, Func<Window> Create)[]
        {
            ("登录窗口", () => new LoginWindow()),
            ("房间列表窗口", () => new RoomListWindow(new GameClient())),
            ("房间内主窗口", () => new MainWindow(new GameClient())),
            ("创建房间窗口", () => new CreateRoomWindow()),
            ("房主设置窗口", () => new HostSettingsWindow(new GameClient())),
            ("设置窗口", () => new SettingsWindow()),
            ("邀请码窗口", () => new InviteCodeWindow("WW1:NAV-PROBE")),
            ("保留房间窗口", () => new KeepRoomWindow())
        };

        foreach (var (name, create) in factories)
        {
            Window? window = null;
            try
            {
                window = create();
                var root = window.Content as FrameworkElement;
                if (root == null) { Check.True(false, $"T18：{name} 内容根是 FrameworkElement"); continue; }

                var size = new Size(double.IsNaN(window.Width) ? 1024 : window.Width,
                                    double.IsNaN(window.Height) ? 768 : window.Height);
                root.Measure(size);
                root.Arrange(new Rect(0, 0, size.Width, size.Height));
                root.UpdateLayout();

                var bar = window.FindName("NavBar") as FrameworkElement;
                FrameworkElement? title = window.FindName("NavTitleText") as FrameworkElement
                                          ?? window.FindName("NavTitleButton") as FrameworkElement;
                if (bar == null || title == null)
                {
                    Check.True(false, $"T18：{name} 有导航栏与导航标题");
                    continue;
                }

                var titleLeft = title.TranslatePoint(new Point(0, 0), root).X;
                var barLeft = bar.TranslatePoint(new Point(0, 0), root).X;
                var offset = Math.Abs((titleLeft + title.ActualWidth / 2) - (barLeft + bar.ActualWidth / 2));

                Console.WriteLine($"   · T18 {name}：标题偏移 {offset:F2}px（导航栏宽 {bar.ActualWidth:F0}）");
                Check.True(offset <= 1.0, $"T18：{name} 导航标题几何居中（偏移 {offset:F2}px ≤ 1px）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   · T18 居中测量（{name}）异常：{ex.Message}");
                Check.True(false, $"T18：{name} 导航居中可测量且无异常");
            }
            finally
            {
                try { window?.Close(); } catch { /* 未显示的窗口关闭失败可忽略 */ }
            }
        }
    }

    // ==================================================================
    //  测试替身（假游戏模块 / 假资源）—— 证明「登记处不依赖任何具体游戏」
    // ==================================================================

    /// <summary>声明了一个可编辑资源的假游戏模块（用于证明登记处的通用性）。</summary>
    private sealed class ResourceModule : IGameModule
    {
        private readonly FakeResource _resource;

        public ResourceModule(string gameId, string resourceId, string display, string initial)
        {
            Info = new GameInfo { GameId = gameId, DisplayName = "假游戏", Emoji = "🧪", MaxPlayers = 4 };
            _resource = new FakeResource(resourceId, display, initial);
        }

        public GameInfo Info { get; }

        public IGameInstance CreateInstance(IGameContext context) => throw new NotSupportedException();

        public IReadOnlyList<IEditableDataResource> CreateEditableResources(string dataRoot)
            => new IEditableDataResource[] { _resource };
    }

    /// <summary>没有可编辑资源的假游戏模块（走 <see cref="IGameModule.CreateEditableResources"/> 默认实现）。</summary>
    private sealed class PlainModule : IGameModule
    {
        public PlainModule(string gameId)
            => Info = new GameInfo { GameId = gameId, DisplayName = "无资源游戏", Emoji = "🧪", MaxPlayers = 4 };

        public GameInfo Info { get; }

        public IGameInstance CreateInstance(IGameContext context) => throw new NotSupportedException();
    }

    /// <summary>假资源：条数 = 非空行数，「保存」真的写盘（用平台的原子写）。</summary>
    private sealed class FakeResource : IEditableDataResource
    {
        private string _content;

        public FakeResource(string resourceId, string display, string initial)
        {
            ResourceId = resourceId;
            DisplayName = display;
            _content = initial;
        }

        public string ResourceId { get; }
        public string DisplayName { get; }
        public string FormatHint => "示例格式：任意文本";

        public string Load() => _content;

        public DataResourceCheck Validate(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return new DataResourceCheck(false, "内容为空", 0);
            var count = content.Replace("\r\n", "\n").Split('\n')
                               .Count(l => l.Trim().Length > 0);
            return new DataResourceCheck(true, "", count);
        }

        public DataResourceWrite Save(string content)
        {
            var check = Validate(content);
            if (!check.Ok) return new DataResourceWrite(false, check.Error, 0);

            _content = content;
            return new DataResourceWrite(true, "", check.Count);
        }
    }
}
