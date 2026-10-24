using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// QA（严过关）对「内置服务器」改造的**独立复核**。
///
/// 与工程师自测（<see cref="EmbeddedServerProbe"/>）的区别：
/// <list type="bullet">
///   <item>不复用工程师的任何结论，一律重新证明；</item>
///   <item>端口占用一律用**真实的内置服务器实例**（两边都绑 0.0.0.0）制造，
///         而不是 <c>TcpListener(IPAddress.Loopback, …)</c>（Windows 上不与 0.0.0.0 冲突，测不出真实场景）；</item>
///   <item>漂移后必须**真的连得上并走通建房**，而不是只看 <c>StartAsync</c> 返回 Ok；</item>
///   <item>关窗收尾走 GUI 的真实对象（<c>EmbeddedServerService</c> + <c>AppShutdown</c> +
///         <c>GameViewModel.LeaveRoomForShutdownAsync</c>），并由**另一个客户端**观测离开语义。</item>
/// </list>
///
/// 分两组：
/// <list type="bullet">
///   <item><b>默认组</b>（<see cref="RunLogicChecks"/>）：纯逻辑断言，不绑端口、不改进程 CWD；</item>
///   <item><b>--embeddedqa</b>（<see cref="RunAsync"/>）：真实进程内起服的端到端复核。</item>
/// </list>
/// </summary>
public static class EmbeddedServerQaAudit
{
    // ==================== 默认组：纯逻辑断言（不绑端口 / 不改 CWD） ====================

    /// <summary>默认组复核：只做可确定性推导的断言，不产生任何端口绑定与目录副作用。</summary>
    public static void RunLogicChecks()
    {
        // ---- 端口漂移序列（与工程师断言不同角度：去重、越界、自定义首选端口） ----
        var inRange = EmbeddedServerHost.PortSequence(new EmbeddedServerOptions
        {
            PreferredPort = 5005,
            PortRangeStart = 5001,
            PortRangeEnd = 5100,
            AllowPortDrift = true
        }).ToList();
        Check.Equal(5005, inRange[0], "QA：首选端口落在漂移范围内时仍排在第一位");
        Check.Equal(100, inRange.Count, "QA：首选端口落在漂移范围内时序列去重（共 100 个）");
        Check.False(inRange.Any(p => p is < 1024 or > 65535), "QA：漂移序列不产生越界端口");

        var custom = EmbeddedServerHost.PortSequence(new EmbeddedServerOptions
        {
            PreferredPort = 6000,
            PortRangeStart = 5001,
            PortRangeEnd = 5100,
            AllowPortDrift = true
        }).ToList();
        Check.Equal(6000, custom[0], "QA：自定义首选端口优先于漂移范围");
        Check.Equal(5100, custom[^1], "QA：自定义首选端口时漂移仍到 5100 结束");

        // ---- 人话文案映射：补齐工程师未覆盖的 SocketError 分支 ----
        Check.Equal("该地址在当前网络环境下不可用",
            EmbeddedServerHost.DescribeBindFailure(
                new IOException("x", new SocketException((int)SocketError.AddressNotAvailable))),
            "QA：AddressNotAvailable 映射为人话");
        Check.Equal("网络不可用",
            EmbeddedServerHost.DescribeBindFailure(
                new IOException("x", new SocketException((int)SocketError.NetworkDown))),
            "QA：NetworkDown 映射为人话");
        Check.Equal("网络不可达",
            EmbeddedServerHost.DescribeBindFailure(
                new IOException("x", new SocketException((int)SocketError.NetworkUnreachable))),
            "QA：NetworkUnreachable 映射为人话");
        Check.Equal("没有权限",
            EmbeddedServerHost.DescribeBindFailure(new UnauthorizedAccessException("nope")),
            "QA：UnauthorizedAccessException 映射为人话");

        // 多层嵌套也能取到最内层 SocketException（真实异常是 IOException→…→SocketException）
        var nested = new IOException("outer",
            new InvalidOperationException("middle",
                new SocketException((int)SocketError.AddressAlreadyInUse)));
        Check.True(EmbeddedServerHost.IsAddressInUse(nested),
            "QA：多层嵌套异常仍能识别「端口已被占用」");

        // ---- 收尾预算常量（PRD E-8 / E-9） ----
        Check.Equal(TimeSpan.FromSeconds(3), AppShutdown.LeaveRoomBudget, "QA：退出房间预算 3 秒");
        Check.Equal(TimeSpan.FromSeconds(5), AppShutdown.StopServerBudget, "QA：停内置服务器预算 5 秒");
        Check.Equal(TimeSpan.FromSeconds(8), AppShutdown.TotalBudget, "QA：收尾总预算 8 秒");
        Check.Equal(TimeSpan.FromSeconds(3), EmbeddedServerConsole.LeaveRoomBudget,
            "QA：控制台退出房间预算与 GUI 一致（3 秒）");
        Check.Equal(TimeSpan.FromSeconds(5), EmbeddedServerConsole.StopServerBudget,
            "QA：控制台停服务器预算与 GUI 一致（5 秒）");
        Check.Equal(TimeSpan.FromSeconds(8), EmbeddedServerConsole.TotalBudget,
            "QA：控制台收尾总预算与 GUI 一致（8 秒）");

        // ---- 未启动 / 无其他玩家时一律不弹确认（PRD Q4 裁定） ----
        Check.False(AppShutdown.ShouldConfirmClose(),
            "QA：内置服务器未运行时关窗不弹确认");
        Check.False(EmbeddedServerService.Current.IsRunning,
            "QA：进程级单例初始状态为未运行");
        Check.False(AppShutdown.IsShuttingDown, "QA：初始状态未进入收尾");

        // ---- 工作目录隔离的契约（D3） ----
        Check.True(Path.IsPathRooted(EmbeddedServerPaths.PreferredDirectory()),
            "QA：首选工作目录是绝对路径（不受进程 CWD 影响）");
        Check.True(Path.IsPathRooted(EmbeddedServerPaths.FallbackDirectory()),
            "QA：回退工作目录是绝对路径");
        Check.Equal("embedded-server", EmbeddedServerPaths.DirectoryName, "QA：工作目录名固定 embedded-server");
        Check.True(Path.IsPathRooted(UiStateStore.DefaultPath),
            "QA：客户端自身状态文件用绝对路径（切 CWD 不会被带偏）");
        Check.True(UiStateStore.DefaultPath.Contains("WerewolfClient", StringComparison.Ordinal),
            "QA：客户端状态文件仍落在 %APPDATA%\\WerewolfClient（" + UiStateStore.DefaultPath + "）");
        Check.True(Path.GetFileName(UiStateStore.DefaultPath).Equals("ui-state.json", StringComparison.OrdinalIgnoreCase),
            "QA：客户端状态文件名仍是 ui-state.json");

        // ---- D7 / E-18：Share 侧必须提供「邀请码地址覆盖」且默认不影响既有行为 ----
        var inviteOverride = typeof(GameClient).GetProperty("InviteUrlOverride",
            BindingFlags.Public | BindingFlags.Instance);
        Check.True(inviteOverride != null,
            "QA：GameClient 提供 InviteUrlOverride（E-18：回落本机地址时邀请码仍保留局域网地址）");
        Check.True(inviteOverride != null && inviteOverride.PropertyType == typeof(string),
            "QA：InviteUrlOverride 是可空字符串（默认 null → 现状行为不变）");

        // 默认 null 时邀请码必须与 Endpoint 完全一致（逐字节）—— 既有行为不变。
        // 用反射把「已进房」状态补齐（私有字段），不改 GameClient 一行代码。
        var plain = new GameClient();
        typeof(GameClient).GetProperty("Endpoint")!.SetValue(plain, "http://192.168.1.7:5000/gamehub");
        SetPrivateField(plain, "_roomId", "AB12C");
        SetPrivateField(plain, "_roomName", "复核房");

        var codeA = plain.CreateInviteCode("qa-token");
        var fallbackEndpoint = plain.Endpoint;
        Check.True(codeA != null, "QA：默认（未设置覆盖）时仍能生成邀请码");

        InvitePayload? parsedA = null;
        var parseErr = "";
        var parsedOk = codeA != null && InviteCode.TryParse(codeA!, out parsedA, out parseErr);
        Check.True(parsedOk && parsedA != null && parsedA.Url == fallbackEndpoint,
            "QA：InviteUrlOverride 未设置时邀请码携带的就是连接地址（既有行为不变，" +
            (parsedOk && parsedA != null ? parsedA.Url : parseErr) + "）");

        // E-18 的**语义**（不只是属性存在）：回落时只改「分享出去的地址」，不改「我连的地址」。
        if (inviteOverride != null)
        {
            // 模拟一次 E-18 回落：连接地址变成 127.0.0.1，覆盖值仍是局域网地址。
            var fallen = new GameClient();
            typeof(GameClient).GetProperty("Endpoint")!.SetValue(fallen, "http://127.0.0.1:5000/gamehub");
            SetPrivateField(fallen, "_roomId", "AB12C");
            SetPrivateField(fallen, "_roomName", "复核房");
            inviteOverride.SetValue(fallen, "http://192.168.1.7:5000/gamehub");

            var codeB = fallen.CreateInviteCode("qa-token");
            Check.True(codeB != null, "QA：设置了覆盖后仍能生成邀请码");

            InvitePayload? parsedB = null;
            var errB = "";
            var okB = codeB != null && InviteCode.TryParse(codeB!, out parsedB, out errB);
            Check.True(okB && parsedB != null
                       && parsedB.Url == "http://192.168.1.7:5000/gamehub",
                "QA（E-18）：回落后生成的邀请码仍是**局域网**地址（好友那份不受影响，" +
                (okB && parsedB != null ? parsedB.Url : errB) + "）");
            Check.True(okB && parsedB != null && parsedB.RoomId == "AB12C",
                "QA（E-18）：覆盖地址不影响邀请码里的房间号");
            Check.Equal("http://127.0.0.1:5000/gamehub", fallen.Endpoint,
                "QA（E-18）：覆盖只改分享出去的码，不改我自己实际连的地址");
        }

        // ---- E-19：两个客户端 csproj 的清理目标必须同时覆盖 Build 与 Publish ----
        var root = FindRepoRoot();
        if (root == null)
        {
            Console.WriteLine("   · 未找到仓库根目录，跳过 csproj 清理目标复核");
        }
        else
        {
            foreach (var relative in new[] { "ClientGUI/ClientGUI.csproj", "Werewolf.Client/Werewolf.Client.csproj" })
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Check.True(File.Exists(path), "QA：存在 " + relative);
                if (!File.Exists(path)) continue;

                var text = File.ReadAllText(path);
                Check.True(Regex.IsMatch(text, @"AfterTargets\s*=\s*""[^""]*Publish[^""]*"""),
                    "QA：" + relative + " 的清理目标挂在 Publish 之后（dotnet publish 出来也必须干净，E-19）");
                Check.True(text.Contains("$(PublishDir)", StringComparison.Ordinal),
                    "QA：" + relative + " 的清理目标覆盖发布目录 $(PublishDir)（E-19）");
                Check.True(text.Contains("$(OutDir)", StringComparison.Ordinal),
                    "QA：" + relative + " 的清理目标覆盖输出目录 $(OutDir)（E-19）");
                Check.True(text.Contains("$(OutDir)accounts.json", StringComparison.Ordinal)
                           && text.Contains("$(PublishDir)accounts.json", StringComparison.Ordinal),
                    "QA：" + relative + " 一并清掉 accounts.json（实测它也是构建产物，不是历史残留）");
                Check.False(Regex.IsMatch(text, @"\$\(PublishDir\)Werewolf\.Server\.dll"),
                    "QA：" + relative + " 的清理目标不得删掉 Werewolf.Server.dll（内置服务器依赖它）");
            }
        }

        // ---- 主题：WarnBrush 两套都有，且 LoginPage.xaml 无硬编码色值 ----
        if (root != null)
        {
            var dark = Path.Combine(root, "ClientGUI", "Themes", "Colors.Dark.xaml");
            var light = Path.Combine(root, "ClientGUI", "Themes", "Colors.Light.xaml");
            Check.True(File.Exists(dark) && File.Exists(light), "QA：两套主题文件都存在");
            if (File.Exists(dark) && File.Exists(light))
            {
                var darkText = File.ReadAllText(dark);
                var lightText = File.ReadAllText(light);
                Check.True(darkText.Contains("WarnBrush", StringComparison.Ordinal),
                    "QA：深色主题定义了 WarnBrush（状态徽章启动中 / 停止中）");
                Check.True(lightText.Contains("WarnBrush", StringComparison.Ordinal),
                    "QA：浅色主题定义了同名 WarnBrush（两套键必须一致）");

                var darkValue = MatchBrush(darkText, "WarnBrush");
                var lightValue = MatchBrush(lightText, "WarnBrush");
                Check.True(darkValue.Length > 0 && lightValue.Length > 0, "QA：两套主题的 WarnBrush 都取到了色值");
                Check.NotEqual(darkValue, lightValue, "QA：WarnBrush 在两套主题里取不同色值（不是复制粘贴）");
            }

            // S4：LoginWindow.xaml 已随旧窗口删除，扫描对象改指它迁过去的页面（判据一字未改）。
            var login = Path.Combine(root, "ClientGUI", "Views", "Pages", "LoginPage.xaml");
            Check.True(File.Exists(login), "QA：LoginPage.xaml 存在（旧 LoginWindow.xaml 已于 S4 删除）");
            if (File.Exists(login))
            {
                var hard = HardcodedColors(File.ReadAllText(login));
                Check.Equal(0, hard.Count,
                    "QA：LoginPage.xaml 无硬编码色值（一律 DynamicResource）" +
                    (hard.Count > 0 ? "：" + string.Join(", ", hard) : ""));
            }
        }

        // ---- 平台层不得出现任何具体游戏标识（PRD §11 硬约束） ----
        // 只扫本次**新增**的平台层文件：改造过的既有文件里可能本来就有游戏标识
        // （例如控制台客户端建房时的默认游戏选择），那不是本次引入的，不该由本次背锅。
        if (root != null)
        {
            var newFiles = new[]
            {
                "WerewolfGame/ServerBootstrap.cs",
                "WerewolfGame/EmbeddedServerHost.cs",
                "WerewolfGame/EmbeddedServerPaths.cs",
                "ClientGUI/Services/EmbeddedServerService.cs",
                "ClientGUI/ViewModels/EmbeddedServerViewModel.cs",
                "ClientGUI/Services/AppShutdown.cs",
                "Werewolf.Client/EmbeddedServerConsole.cs",
                "ClientGUI.Tests/EmbeddedServerProbe.cs"
            };

            foreach (var relative in newFiles)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Check.True(File.Exists(path), "QA：本次新增文件存在（" + relative + "）");
                if (!File.Exists(path)) continue;

                var text = File.ReadAllText(path);
                Check.False(text.Contains("GameIds.", StringComparison.Ordinal),
                    "QA：平台层不得出现具体游戏标识 GameIds.*（" + relative + "）");
                Check.False(text.Contains("UndercoverGameInstance", StringComparison.Ordinal),
                    "QA：平台层不得出现 UndercoverGameInstance（" + relative + "）");
                Check.False(text.Contains("WerewolfGameInstance", StringComparison.Ordinal),
                    "QA：平台层不得出现 WerewolfGameInstance（" + relative + "）");
                Check.False(text.Contains("words.undercover.txt", StringComparison.Ordinal),
                    "QA：平台层不得硬编码词库文件名（" + relative + "）");
            }
        }

        // ---- 不新增 SignalR 方法 / 不改 Call 契约：Ops 常量集合必须与基线一致 ----
        var ops = typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
                             .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                             .Select(f => (string)f.GetRawConstantValue()!)
                             .OrderBy(v => v, StringComparer.Ordinal)
                             .ToList();
        Check.True(ops.Contains("leaveroom"), "QA：Ops 仍含 leaveroom（关窗收尾复用既有退出房间）");
        Check.True(ops.Contains("createroom") && ops.Contains("joinroom") && ops.Contains("roomlist"),
            "QA：Ops 仍含房间操作全套（未为内置服务器新增专用方法）");
        Check.Equal(ops.Count, ops.Distinct().Count(), "QA：Ops 常量无重复（未新增重复语义的方法）");
    }

    // ==================== --embeddedqa：真实端到端复核 ====================

    /// <summary>
    /// 真实端到端复核（进程内起服）：
    /// ① 端口漂移用真实内置服务器制造占用，且漂移后**真的连得上并走通建房**；
    /// ② 漂移范围被占满时给出人话失败，而不是静默换端口；
    /// ③ 关窗收尾走 GUI 真实对象，并由另一个客户端观测「正规退出房间」语义；
    /// ④ 工作目录隔离：服务器文件落进 embedded-server\，客户端程序目录不被污染。
    /// </summary>
    public static async Task<bool> RunAsync()
    {
        var ok = true;
        var originalCwd = Directory.GetCurrentDirectory();

        void Fail(string what)
        {
            ok = false;
            Check.True(false, what);
        }

        void Pass(string what)
        {
            Check.True(true, what);
            Console.WriteLine("   ✅ " + what);
        }

        try
        {
            // 界面状态：复核期间不碰用户真实偏好。
            UiStateStore.Enabled = false;

            // ---------- ① 工作目录隔离（先做：它会切进程 CWD，做完必须还原） ----------
            var programDir = AppContext.BaseDirectory;
            var filesBefore = Directory.Exists(programDir)
                ? Directory.GetFiles(programDir)
                           .Select(f => Path.GetFileName(f) ?? "")
                           .Where(n => n.Length > 0)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var isolationHost = new EmbeddedServerHost(new EmbeddedServerOptions
            {
                PreferredPort = FindFreePort(6300, 6399),
                AllowPortDrift = false
            });

            var isolationInfo = await isolationHost.StartAsync();
            Check.True(isolationInfo.Ok, "QA：隔离复核用的内置服务器启动成功");

            if (isolationInfo.Ok)
            {
                var cwdDuring = Path.GetFullPath(Directory.GetCurrentDirectory());
                var workDir = Path.GetFullPath(isolationInfo.WorkDirectory);
                Check.Equal(workDir, cwdDuring, "QA：内置服务器运行期间进程 CWD 已切到工作目录（D3）");
                Check.True(Directory.Exists(workDir), "QA：工作目录已真实创建（" + workDir + "）");
                Check.True(File.Exists(Path.Combine(workDir, "appsettings.json")),
                    "QA：appsettings.json 落在 embedded-server\\ 而不是客户端程序目录");

                // 客户端自己的状态文件不受 CWD 切换影响（绝对路径）
                var statePath = UiStateStore.DefaultPath;
                Check.True(Path.IsPathRooted(statePath) && !statePath.StartsWith(workDir, StringComparison.Ordinal),
                    "QA：客户端状态文件 ui-state.json 未被带进服务器工作目录");

                // 相对路径解析基准确实是工作目录（等价于「在那个目录里单独启动服务端」）
                Check.Equal(workDir, Path.GetFullPath(Directory.GetCurrentDirectory()),
                    "QA：服务端按相对路径读写时解析到工作目录");

                await isolationHost.StopAsync(TimeSpan.FromSeconds(5));

                var cwdAfter = Path.GetFullPath(Directory.GetCurrentDirectory());
                Check.Equal(Path.GetFullPath(originalCwd), cwdAfter, "QA：停止后进程 CWD 已还原（不污染宿主后续行为）");

                var filesAfter = Directory.GetFiles(programDir)
                                          .Select(f => Path.GetFileName(f) ?? "")
                                          .Where(n => n.Length > 0)
                                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = filesAfter.Where(n => !filesBefore.Contains(n)).ToList();
                Check.Equal(0, added.Count,
                    "QA：内置服务器启动 / 停止期间客户端程序目录**没有**新增文件" +
                    (added.Count > 0 ? "：" + string.Join(", ", added) : ""));
                Pass("工作目录隔离成立：服务端文件落进 embedded-server\\，客户端程序目录零新增");
            }
            else
            {
                Fail("工作目录隔离复核用的服务器启动失败：" + isolationInfo.ErrorMessage);
            }

            // ---------- ② 端口漂移：真实占用 + 漂移后必须真的能用 ----------
            var occupier = await StartOnFreePortAsync(6100, 6140);
            if (occupier.Host == null)
            {
                Fail("找不到空闲端口，无法复核端口漂移");
            }
            else
            {
                var busy = occupier.Port;
                var drifting = new EmbeddedServerHost(new EmbeddedServerOptions
                {
                    PreferredPort = busy,
                    PortRangeStart = busy + 1,
                    PortRangeEnd = busy + 5,
                    AllowPortDrift = true
                });

                var info = await drifting.StartAsync();
                Check.True(info.Ok, "QA：首选端口被真实占用（0.0.0.0）时自动漂移并启动成功");
                Check.True(info.Ok && info.Port != busy,
                    $"QA：漂移后端口与占用端口不同（{info.Port} ≠ {busy}）");

                if (info.Ok)
                {
                    // 三处地址同一个实际端口（E-16 / P1-6）
                    Check.True(info.LocalUrl.Contains($":{info.Port}/", StringComparison.Ordinal)
                               && info.LanUrl.Contains($":{info.Port}/", StringComparison.Ordinal)
                               && info.ConnectUrl.Contains($":{info.Port}/", StringComparison.Ordinal),
                        "QA：本机地址 / 局域网地址 / 自动连接地址三处同一个实际端口（E-16）");
                    Check.True(info.BootstrapMessages.Any(m =>
                                   m.Contains(busy.ToString(), StringComparison.Ordinal)
                                   && m.Contains(info.Port.ToString(), StringComparison.Ordinal)),
                        "QA：漂移提示点明了「原端口 → 新端口」（界面 / 控制台直接可见）");

                    // 漂移后必须真的能连：走通注册 → 建房 → 房间列表
                    GameClient? client = null;
                    try
                    {
                        client = new GameClient();
                        await client.ConnectAsync(info.LocalUrl);
                        Check.True(client.IsEncrypted, "QA：漂移后的服务器会话加密照常生效");

                        var me = "qa" + Guid.NewGuid().ToString("N")[..6];
                        var login = await client.LoginAsync(me, "pw-" + me);
                        Check.True(login.ok, "QA：能登录漂移后的内置服务器（" + login.msg + "）");

                        var created = await client.CreateRoomAsync("QA漂移房", null, null);
                        Check.True(created.Ok, "QA：能在漂移后的内置服务器上建房（" + created.Message + "）");

                        var rooms = await client.ListRoomsAsync();
                        Check.True(rooms.Ok && rooms.Rooms.Count >= 1, "QA：漂移后的服务器房间列表可查");

                        var snap = drifting.Snapshot();
                        Check.True(snap.RoomCount >= 1 && snap.PlayerCount >= 1,
                            $"QA：进程内查到房间 {snap.RoomCount} / 玩家 {snap.PlayerCount}");

                        if (client.IsJoined) await client.LeaveRoomAsync(keepRoom: false);
                        Pass($"端口 {busy} 被真实内置服务器占用 → 漂移到 {info.Port} 且能正常连上、建房");
                    }
                    catch (Exception ex)
                    {
                        Fail("漂移后的服务器无法正常使用：" + ex.Message);
                    }
                    finally
                    {
                        if (client != null)
                        {
                            try { await client.DisposeAsync(); } catch { /* 忽略 */ }
                        }
                    }

                    await drifting.StopAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    Fail("端口漂移失败：" + info.ErrorMessage);
                }

                // ---------- ③ 漂移范围被占满 → 人话失败，不静默 ----------
                var blockers = new List<EmbeddedServerHost>();
                var blocked = new List<int>();
                for (var p = busy + 1; p <= busy + 3; p++)
                {
                    var blocker = new EmbeddedServerHost(new EmbeddedServerOptions
                    {
                        PreferredPort = p,
                        AllowPortDrift = false
                    });
                    var bInfo = await blocker.StartAsync();
                    if (bInfo.Ok) { blockers.Add(blocker); blocked.Add(p); }
                    else await blocker.DisposeAsync();
                }

                Check.Equal(3, blocked.Count, "QA：成功占满漂移范围用于验证「全被占用」分支");

                var exhausted = new EmbeddedServerHost(new EmbeddedServerOptions
                {
                    PreferredPort = busy,
                    PortRangeStart = busy + 1,
                    PortRangeEnd = busy + 3,
                    AllowPortDrift = true
                });
                var exhaustedInfo = await exhausted.StartAsync();
                Check.False(exhaustedInfo.Ok, "QA：漂移范围全被占用时启动失败（不静默换别的端口）");
                Check.True(exhaustedInfo.ErrorMessage.Contains(busy.ToString(), StringComparison.Ordinal),
                    "QA：失败原因点明了首选端口 " + busy);
                Check.True(exhaustedInfo.ErrorMessage.Contains("已被占用", StringComparison.Ordinal),
                    "QA：失败原因是人话「已被占用」（" + exhaustedInfo.ErrorMessage + "）");
                Check.True(exhaustedInfo.ErrorMessage.Contains("连接现有服务器", StringComparison.Ordinal),
                    "QA：失败原因给出了可执行的下一步（改用「连接现有服务器」）");
                Check.False(exhaustedInfo.ErrorMessage.Contains('\n')
                            || exhaustedInfo.ErrorMessage.Contains("   at ", StringComparison.Ordinal)
                            || exhaustedInfo.ErrorMessage.Contains("System.", StringComparison.Ordinal),
                    "QA：失败原因是一行可读的人话，不是异常堆栈（GUI 绝不弹堆栈框）");
                Pass($"漂移范围 {busy + 1}–{busy + 3} 全被占用 → 明确报「已被占用」并给出下一步");
                await exhausted.DisposeAsync();

                foreach (var b in blockers) await b.StopAsync(TimeSpan.FromSeconds(5));
                await occupier.Host.StopAsync(TimeSpan.FromSeconds(5));
            }

            // ---------- ④ 关窗收尾：GUI 真实对象 + 另一个客户端观测离开语义（E-10） ----------
            await RunShutdownSemanticsAsync(Pass, Fail);

            // ---------- ⑤ 控制台收尾：EmbeddedServerConsole.ShutdownAsync 同一套语义（E-12） ----------
            await RunConsoleShutdownSemanticsAsync(Pass, Fail);
        }
        finally
        {
            try { Directory.SetCurrentDirectory(originalCwd); } catch { /* 尽力还原 */ }
        }

        return ok;
    }

    /// <summary>
    /// 关窗收尾语义复核（PRD E-10 / Q4 / Q4-b）：
    /// <list type="number">
    ///   <item>只有自己时不弹确认；</item>
    ///   <item>房间里还有别人时弹一次，且**取消完全无损**；</item>
    ///   <item>真走收尾时，另一个客户端看到的是**正规退出房间**（「离开了房间」），而不是掉线；</item>
    ///   <item>收尾后内置服务器停止、端口释放。</item>
    /// </list>
    /// 用的是 GUI 侧的**真实对象**：<c>EmbeddedServerService.Current</c> + <c>AppShutdown</c> +
    /// <c>GameViewModel.LeaveRoomForShutdownAsync</c>。
    /// </summary>
    private static async Task RunShutdownSemanticsAsync(Action<string> pass, Action<string> fail)
    {
        var service = EmbeddedServerService.Current;
        service.Port = FindFreePort(6400, 6499);
        service.AllowPortDrift = false;   // 端口必须确定，便于复核

        var info = await service.StartAsync();
        if (!info.Ok)
        {
            fail("关窗收尾复核：内置服务器启动失败：" + info.ErrorMessage);
            return;
        }

        var port = info.Port;
        GameClient? host = null;
        GameClient? guest = null;

        try
        {
            host = new GameClient();
            await host.ConnectAsync(info.LocalUrl);
            var hostName = "qa-host-" + Guid.NewGuid().ToString("N")[..6];
            var hostLogin = await host.LoginAsync(hostName, "pw-" + hostName);
            if (!hostLogin.ok) { fail("关窗收尾复核：房主登录失败：" + hostLogin.msg); return; }

            var room = await host.CreateRoomAsync("QA收尾房", null, null);
            if (!room.Ok) { fail("关窗收尾复核：建房失败：" + room.Message); return; }

            // GUI 真实对象：主窗口视图模型（用已进房的客户端构造，等价于主窗口 Loaded 后的状态）
            var vm = new GameViewModel(host);
            AppShutdown.AttachSession(host);
            AppShutdown.SetLeaveRoomHandler(vm.LeaveRoomForShutdownAsync);

            // ---- 只有自己：不弹确认（Q4 裁定） ----
            service.Refresh();
            Check.False(AppShutdown.ShouldConfirmClose(),
                "QA：房间里只有自己时关窗不弹确认（Q4 裁定）");

            // ---- 第二个客户端进同一房间 ----
            var messages = new List<string>();
            guest = new GameClient();
            guest.MessageReceived += m => { lock (messages) messages.Add(m); };
            await guest.ConnectAsync(info.LocalUrl);
            var guestName = "qa-guest-" + Guid.NewGuid().ToString("N")[..6];
            var guestLogin = await guest.LoginAsync(guestName, "pw-" + guestName);
            if (!guestLogin.ok) { fail("关窗收尾复核：第二位玩家登录失败：" + guestLogin.msg); return; }

            var joined = await guest.JoinRoomAsync(host.CurrentRoomId, null);
            if (!joined.Ok) { fail("关窗收尾复核：第二位玩家进房失败：" + joined.Message); return; }

            await WaitUntilAsync(() =>
            {
                service.Refresh();
                return service.Runtime.PlayerCount >= 2;
            }, TimeSpan.FromSeconds(5));

            // ---- 房间里还有别人：弹一次确认 ----
            Check.True(AppShutdown.ShouldConfirmClose(),
                "QA：房间里还有其他玩家（人数 > 1）时关窗弹一次确认（Q4 裁定）");
            Check.True(AppShutdown.ConfirmMessage.Contains("1 位玩家", StringComparison.Ordinal),
                "QA：确认文案点明了还有几位玩家（" + AppShutdown.ConfirmMessage.Replace('\n', ' ') + "）");
            Check.False(AppShutdown.ShouldConfirmClose(),
                "QA：同一轮会话只弹一次确认（连按/重复触发不重复弹）");

            // ---- 取消必须完全无损：不退出房间、不停服务器、不关窗口 ----
            //    （ConfirmClose 真弹 MessageBox 需要 UI，这里直接验证「取消分支」的前置条件：
            //      确认判定发生在任何破坏性动作之前，此时服务器仍在运行、房主仍在房间里。）
            Check.True(service.IsRunning, "QA：确认框弹出时（未点确认）内置服务器仍在运行 —— 取消即无损");
            Check.True(host.IsJoined, "QA：确认框弹出时（未点确认）房主仍在房间里 —— 取消即无损");
            var guestState = await guest.RefreshStateAsync();
            Check.True(guestState != null && guestState.Players.Count >= 2,
                "QA：确认框弹出时另一位玩家仍看到房主在线 —— 取消即无损");
            pass("「还有别人 → 弹一次确认」且确认发生在任何破坏性动作之前（取消即完全无损）");

            // ---- 真走收尾：另一个客户端必须看到**正规退出房间** ----
            lock (messages) messages.Clear();
            await AppShutdown.RunAsync();

            var observed = await WaitUntilAsync(() =>
            {
                lock (messages) return messages.Any(m => m.Contains(hostName, StringComparison.Ordinal));
            }, TimeSpan.FromSeconds(5));

            List<string> snapshot;
            lock (messages) snapshot = new List<string>(messages);

            Check.True(observed, "QA：关窗收尾后另一位玩家在 5 秒内收到了离开通知（不是等超时掉线）");
            Check.True(snapshot.Any(m => m.Contains("离开了房间", StringComparison.Ordinal)),
                "QA：另一位玩家看到的是**正规退出房间**「离开了房间」（E-10）");
            Check.False(snapshot.Any(m => m.Contains("掉线", StringComparison.Ordinal)),
                "QA：另一位玩家看到的不是「掉线」——证明走的是 leaveroom 而不是硬断线");
            Check.True(snapshot.Any(m => m.Contains("成为新房主", StringComparison.Ordinal)),
                "QA：房主身份按既有规则转让给了房间里剩下的玩家");
            pass("关窗收尾走了正规「退出房间」：另一位玩家看到离开广播 + 房主转让，而不是掉线");

            // ---- 收尾后：服务器停止 + 端口释放 ----
            Check.False(service.IsRunning, "QA：收尾后内置服务器已停止");
            Check.False(host.IsJoined, "QA：收尾后房主已不在房间里");
            var released = await WaitUntilAsync(() => IsPortFree(port), TimeSpan.FromSeconds(5));
            Check.True(released, $"QA：收尾后端口 {port} 在 5 秒内释放（US-4）");
            if (released) pass($"收尾后端口 {port} 已释放，无残留监听");
            else fail($"收尾后端口 {port} 在 5 秒内未释放");
        }
        catch (Exception ex)
        {
            fail("关窗收尾复核异常：" + ex.Message);
        }
        finally
        {
            AppShutdown.ClearLeaveRoomHandler();
            if (guest != null) { try { await guest.DisposeAsync(); } catch { /* 忽略 */ } }
            if (host != null) { try { await host.DisposeAsync(); } catch { /* 忽略 */ } }

            if (service.IsRunning || service.IsBusy) await service.StopAsync();
            try { Directory.SetCurrentDirectory(AppContext.BaseDirectory); } catch { /* 尽力 */ }
        }
    }

    /// <summary>
    /// 控制台端收尾语义复核（PRD E-12）：
    /// 走 <see cref="EmbeddedServerConsole.ShutdownAsync"/>（与 GUI 同一套顺序与预算），
    /// 由另一个客户端观测「正规退出房间」，并确认内置服务器停止、端口释放。
    /// 控制台的 Ctrl+C 需要真实终端信号，沙箱里发不出去 —— 这里验的是 Ctrl+C **会调用的那段收尾代码**，
    /// 信号触发本身标注为未覆盖。
    /// </summary>
    private static async Task RunConsoleShutdownSemanticsAsync(Action<string> pass, Action<string> fail)
    {
        var host = new EmbeddedServerHost(new EmbeddedServerOptions
        {
            PreferredPort = FindFreePort(6500, 6599),
            AllowPortDrift = false
        });

        var info = await host.StartAsync();
        if (!info.Ok)
        {
            fail("控制台收尾复核：内置服务器启动失败：" + info.ErrorMessage);
            return;
        }

        var port = info.Port;
        GameClient? hostClient = null;
        GameClient? guest = null;

        try
        {
            hostClient = new GameClient();
            await hostClient.ConnectAsync(info.LocalUrl);
            var hostName = "qa-cli-" + Guid.NewGuid().ToString("N")[..6];
            var login = await hostClient.LoginAsync(hostName, "pw-" + hostName);
            if (!login.ok) { fail("控制台收尾复核：房主登录失败：" + login.msg); return; }

            var room = await hostClient.CreateRoomAsync("QA控制台收尾房", null, null);
            if (!room.Ok) { fail("控制台收尾复核：建房失败：" + room.Message); return; }

            var messages = new List<string>();
            guest = new GameClient();
            guest.MessageReceived += m => { lock (messages) messages.Add(m); };
            await guest.ConnectAsync(info.LocalUrl);
            var guestName = "qa-cli2-" + Guid.NewGuid().ToString("N")[..6];
            var guestLogin = await guest.LoginAsync(guestName, "pw-" + guestName);
            if (!guestLogin.ok) { fail("控制台收尾复核：第二位玩家登录失败：" + guestLogin.msg); return; }

            var joined = await guest.JoinRoomAsync(hostClient.CurrentRoomId, null);
            if (!joined.Ok) { fail("控制台收尾复核：第二位玩家进房失败：" + joined.Message); return; }

            await WaitUntilAsync(() =>
            {
                var s = guest.RefreshStateAsync().GetAwaiter().GetResult();
                return s != null && s.Players.Count >= 2;
            }, TimeSpan.FromSeconds(5));

            Check.True(hostClient.IsJoined, "QA：控制台收尾前房主确实在房间里（前置条件）");

            lock (messages) messages.Clear();
            await EmbeddedServerConsole.ShutdownAsync(hostClient, host);

            var observed = await WaitUntilAsync(() =>
            {
                lock (messages) return messages.Any(m => m.Contains(hostName, StringComparison.Ordinal));
            }, TimeSpan.FromSeconds(5));

            List<string> snapshot;
            lock (messages) snapshot = new List<string>(messages);

            Check.True(observed, "QA：控制台收尾后另一位玩家在 5 秒内收到离开通知");
            Check.True(snapshot.Any(m => m.Contains("离开了房间", StringComparison.Ordinal)),
                "QA：控制台收尾走的是正规「退出房间」（E-12）");
            Check.False(snapshot.Any(m => m.Contains("掉线", StringComparison.Ordinal)),
                "QA：控制台收尾不是硬断线（另一位玩家没看到「掉线」）");
            Check.False(host.IsRunning, "QA：控制台收尾后内置服务器已停止");

            var released = await WaitUntilAsync(() => IsPortFree(port), TimeSpan.FromSeconds(5));
            Check.True(released, $"QA：控制台收尾后端口 {port} 在 5 秒内释放");
            if (released) pass("控制台收尾与 GUI 同语义：正规退出房间 + 停服务器 + 端口释放");
            else fail($"控制台收尾后端口 {port} 在 5 秒内未释放");
        }
        catch (Exception ex)
        {
            fail("控制台收尾复核异常：" + ex.Message);
        }
        finally
        {
            if (guest != null) { try { await guest.DisposeAsync(); } catch { /* 忽略 */ } }
            if (hostClient != null) { try { await hostClient.DisposeAsync(); } catch { /* 忽略 */ } }
            await host.StopAsync(TimeSpan.FromSeconds(5));
            try { Directory.SetCurrentDirectory(AppContext.BaseDirectory); } catch { /* 尽力 */ }
        }
    }

    // ==================== 工具 ====================

    /// <summary>用反射写一个私有字段（只为把被测对象摆到目标状态，不改产品代码）。</summary>
    private static void SetPrivateField(object target, string field, object? value)
        => target.GetType()
                 .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                 ?.SetValue(target, value);

    private static string MatchBrush(string xaml, string key)
    {
        var m = Regex.Match(xaml, $@"x:Key=""{key}""\s+Color=""(#?[0-9A-Fa-f]+)""");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> HardcodedColors(string xaml)
    {
        // 只挑「像颜色」的字面量：#RGB / #RRGGBB / #AARRGGBB，且不是资源引用的一部分。
        var found = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"#[0-9A-Fa-f]{8}\b|#[0-9A-Fa-f]{6}\b|#[0-9A-Fa-f]{3}\b"))
            found.Add(m.Value);

        return found;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && File.Exists(Path.Combine(dir.FullName, "WerewolfGame.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(120);
        }

        return condition();
    }

    /// <summary>端口是否空闲：必须用 <see cref="IPAddress.Any"/>（内置服务器绑的是 0.0.0.0）。</summary>
    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindFreePort(int from, int to)
    {
        for (var port = from; port <= to; port++)
            if (IsPortFree(port)) return port;

        return from;
    }

    private sealed record Occupier(EmbeddedServerHost? Host, int Port);

    /// <summary>在指定范围里用**真实的内置服务器**占住一个端口（两边都绑 0.0.0.0，反映真实占用）。</summary>
    private static async Task<Occupier> StartOnFreePortAsync(int from, int to)
    {
        for (var port = from; port <= to; port++)
        {
            if (!IsPortFree(port)) continue;

            var host = new EmbeddedServerHost(new EmbeddedServerOptions
            {
                PreferredPort = port,
                AllowPortDrift = false
            });

            var info = await host.StartAsync();
            if (info.Ok) return new Occupier(host, port);

            await host.DisposeAsync();
        }

        return new Occupier(null, 0);
    }
}
