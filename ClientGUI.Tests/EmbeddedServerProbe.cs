using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.Tests;

/// <summary>
/// 内置服务器（进程内宿主）的校验：
/// <list type="bullet">
///   <item><b>默认组</b>（<see cref="RunLogicChecks"/>）：纯逻辑断言，端口序列 / 目录回退 / 人话文案映射 /
///       地址拼装 —— **不绑定端口、不改进程 CWD**，可随默认组反复运行；</item>
///   <item><b>--embedded</b>（<see cref="RunAsync"/>）：端到端探针，真起一个内置服务器，
///       走完「启动 → 漂移端口 → 注册 → 建房 → 进房 → 离开 → 停止 → 同一端口可再次启动」。</item>
/// </list>
/// </summary>
public static class EmbeddedServerProbe
{
    // ==================== 纯逻辑断言（默认组，不绑端口） ====================

    /// <summary>纯逻辑校验：不绑定端口、不改进程工作目录，只验证可确定性推导的规则。</summary>
    public static void RunLogicChecks()
    {
        Console.WriteLine("════════ 内置服务器：纯逻辑校验 ════════");

        // ---- 端口序列：5000 → 5001 … 5100 ----
        var drift = EmbeddedServerHost.PortSequence(new EmbeddedServerOptions
        {
            PreferredPort = 5000,
            PortRangeStart = 5001,
            PortRangeEnd = 5100,
            AllowPortDrift = true
        }).ToList();

        Check.Equal(5000, drift[0], "内置服务器：端口序列以首选端口 5000 开头");
        Check.Equal(5001, drift[1], "内置服务器：漂移从 5001 开始");
        Check.Equal(5100, drift[^1], "内置服务器：漂移到 5100 结束");
        Check.Equal(101, drift.Count, "内置服务器：漂移序列共 101 个端口（5000 + 5001–5100）");
        Check.Equal(101, drift.Distinct().Count(), "内置服务器：漂移序列无重复端口");

        // ---- 关闭漂移时只试首选端口 ----
        var noDrift = EmbeddedServerHost.PortSequence(new EmbeddedServerOptions
        {
            PreferredPort = 5000,
            AllowPortDrift = false
        }).ToList();
        Check.SequenceEqual(new[] { 5000 }, noDrift, "内置服务器：关闭漂移时只尝试首选端口");

        // ---- 端口范围写反也能正确归一 ----
        var reversed = EmbeddedServerHost.PortSequence(new EmbeddedServerOptions
        {
            PreferredPort = 5000,
            PortRangeStart = 5003,
            PortRangeEnd = 5001
        }).ToList();
        Check.SequenceEqual(new[] { 5000, 5001, 5002, 5003 }, reversed,
            "内置服务器：漂移范围写反时自动归一为升序");

        // ---- 地址拼装必须带 /gamehub ----
        Check.Equal("http://127.0.0.1:5000/gamehub", EmbeddedServerHost.FormatUrl("127.0.0.1", 5000),
            "内置服务器：地址带 /gamehub 终结点");
        Check.Equal("http://192.168.1.5:5007/gamehub", EmbeddedServerHost.FormatUrl("192.168.1.5", 5007),
            "内置服务器：漂移端口如实体现在地址里");

        // ---- 工作目录：首选 = 程序目录\embedded-server，回退 = %LOCALAPPDATA%\Werewolf\embedded-server ----
        Check.True(EmbeddedServerPaths.PreferredDirectory().EndsWith(
                       Path.Combine(EmbeddedServerPaths.DirectoryName), StringComparison.Ordinal),
            "内置服务器：首选工作目录是程序目录下的 " + EmbeddedServerPaths.DirectoryName);
        Check.True(EmbeddedServerPaths.FallbackDirectory().Contains(EmbeddedServerPaths.FallbackCompany,
                       StringComparison.Ordinal),
            "内置服务器：回退工作目录带厂商名 " + EmbeddedServerPaths.FallbackCompany);
        Check.True(EmbeddedServerPaths.FallbackDirectory().EndsWith(
                       Path.Combine(EmbeddedServerPaths.DirectoryName), StringComparison.Ordinal),
            "内置服务器：回退工作目录同样叫 " + EmbeddedServerPaths.DirectoryName);

        // 纯函数：一个真实可写的临时目录必须被判定为可写；一个不存在的非法路径必须为不可写。
        var temp = Path.Combine(Path.GetTempPath(), "werewolf-embedded-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check.True(EmbeddedServerPaths.IsWritable(temp), "内置服务器：可写目录判定为可写（纯函数）");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 清理失败不影响 */ }
        }

        Check.False(EmbeddedServerPaths.IsWritable(""), "内置服务器：空路径判定为不可写");
        Check.False(EmbeddedServerPaths.IsWritable(Path.Combine("Z:", "bad|path*", "x")),
            "内置服务器：非法路径判定为不可写（不抛异常）");

        // ---- 人话文案映射：绝不把异常堆栈交给用户 ----
        var inUse = new IOException("Failed to bind to address http://0.0.0.0:5000: address already in use.",
            new SocketException((int)SocketError.AddressAlreadyInUse));
        Check.True(EmbeddedServerHost.IsAddressInUse(inUse),
            "内置服务器：端口占用（IOException + SocketException 10048）可识别");
        Check.Equal("端口已被其他程序占用", EmbeddedServerHost.DescribeBindFailure(inUse),
            "内置服务器：端口占用映射为人话");

        var denied = new IOException("bind failed", new SocketException((int)SocketError.AccessDenied));
        Check.False(EmbeddedServerHost.IsAddressInUse(denied),
            "内置服务器：权限类失败不会被误判为端口占用");
        Check.Equal("没有权限使用该端口", EmbeddedServerHost.DescribeBindFailure(denied),
            "内置服务器：权限类失败映射为人话");

        var plain = new InvalidOperationException("boom");
        Check.False(EmbeddedServerHost.IsAddressInUse(plain), "内置服务器：非网络异常不算端口占用");
        Check.Equal("boom", EmbeddedServerHost.DescribeBindFailure(plain),
            "内置服务器：未知异常回落为一句话描述（不含堆栈）");

        // ---- 未启动时的结果契约 ----
        var fresh = new EmbeddedServerHost(new EmbeddedServerOptions());
        Check.False(fresh.IsRunning, "内置服务器：新建宿主未运行");
        Check.False(fresh.Info.Ok, "内置服务器：新建宿主的启动结果为未成功");
        var runtime = fresh.Snapshot();
        Check.Equal(0, runtime.RoomCount, "内置服务器：未运行时房间数为 0（不抛异常）");
        Check.Equal(0, runtime.PlayerCount, "内置服务器：未运行时玩家数为 0（不抛异常）");

        // ---- E-18：邀请码地址覆盖（回落本机地址后，分享出去的码仍必须是局域网地址） ----
        // 用反射补齐「已进房」的私有状态：不改 GameClient 一行代码，只验证语义。
        var inviteClient = new GameClient();
        SetPrivate(inviteClient, "Endpoint", "http://127.0.0.1:5000/gamehub");
        SetPrivate(inviteClient, "_roomId", "AB12C");
        SetPrivate(inviteClient, "_roomName", "内置房");

        Check.True(inviteClient.InviteUrlOverride == null,
            "内置服务器：InviteUrlOverride 默认 null（现状行为不变）");

        var plainCode = inviteClient.CreateInviteCode("tok-1");
        InvitePayload? parsedPlain = null;
        var plainOk = plainCode != null && InviteCode.TryParse(plainCode, out parsedPlain, out _);
        Check.True(plainOk && parsedPlain?.Url == "http://127.0.0.1:5000/gamehub",
            "内置服务器：未设置覆盖时邀请码携带连接地址（既有行为逐字节不变）");

        inviteClient.InviteUrlOverride = "http://192.168.1.7:5000/gamehub";
        var overridden = inviteClient.CreateInviteCode("tok-1");
        InvitePayload? parsedOverride = null;
        var overriddenOk = overridden != null && InviteCode.TryParse(overridden, out parsedOverride, out _);
        Check.True(overriddenOk && parsedOverride?.Url == "http://192.168.1.7:5000/gamehub",
            "内置服务器：E-18 设置覆盖后邀请码携带局域网地址（好友那份不受本机回落影响）");
        Check.True(overriddenOk && parsedOverride?.RoomId == "AB12C",
            "内置服务器：E-18 覆盖只改地址，房间号与邀请令牌不受影响");

        // ---- 常量与共享约定 ----
        Check.Equal(5000, new EmbeddedServerOptions().PreferredPort, "内置服务器：默认端口 5000");
        Check.Equal(5001, new EmbeddedServerOptions().PortRangeStart, "内置服务器：漂移起点 5001");
        Check.Equal(5100, new EmbeddedServerOptions().PortRangeEnd, "内置服务器：漂移终点 5100");
        Check.True(new EmbeddedServerOptions().AllowPortDrift, "内置服务器：默认开启端口漂移");
        Check.Equal("0.0.0.0", new EmbeddedServerOptions().ListenAddress,
            "内置服务器：默认监听 0.0.0.0（局域网可连）");
        Check.Equal("/gamehub", EmbeddedServerHost.HubPath, "内置服务器：终结点路径固定 /gamehub");
    }

    // ==================== 端到端探针（--embedded） ====================

    /// <summary>
    /// 端到端自检：真起一个内置服务器，走完「启动 → 注册 → 建房 → 进房 → 离开 → 停止 →
    /// 同一端口可再次启动」，并额外验证端口漂移。
    /// </summary>
    public static async Task<bool> RunAsync()
    {
        Console.WriteLine("════════ 内置服务器：端到端自检 ════════");
        Console.WriteLine("   （进程内起服，不依赖外部服务端）");

        var ok = true;

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

        // ---------- 1. 启动（默认端口 5000，允许漂移） ----------
        var options = new EmbeddedServerOptions { PreferredPort = 5000, AllowPortDrift = true };
        var host = new EmbeddedServerHost(options);

        var info = await host.StartAsync();
        if (!info.Ok)
        {
            Fail("内置服务器启动失败：" + info.ErrorMessage);
            await host.DisposeAsync();
            return false;
        }

        Pass($"内置服务器启动成功（端口 {info.Port}）");
        Check.True(info.Port is >= 5000 and <= 5100, $"内置服务器：端口落在 5000–5100（实际 {info.Port}）");
        Check.True(info.ConnectUrl.EndsWith("/gamehub", StringComparison.Ordinal),
            "内置服务器：连接地址带 /gamehub");
        Check.True(info.LocalUrl.EndsWith("/gamehub", StringComparison.Ordinal),
            "内置服务器：本机地址带 /gamehub");
        Check.True(info.LanUrl.EndsWith("/gamehub", StringComparison.Ordinal),
            "内置服务器：局域网地址带 /gamehub");
        Check.True(info.WorkDirectory.Length > 0, "内置服务器：报告了工作目录");
        Check.True(Directory.Exists(info.WorkDirectory),
            $"内置服务器：工作目录已创建（{info.WorkDirectory}）");
        Check.True(File.Exists(Path.Combine(info.WorkDirectory, "appsettings.json")),
            "内置服务器：工作目录里自动生成了 appsettings.json（配置自愈）");
        Check.Equal(info.Port, ExtractPort(info.ConnectUrl),
            "内置服务器：连接地址端口与实际监听端口一致（E-16）");

        // ---------- 2. 端到端：注册 → 建房 → 进房 → 离开 ----------
        GameClient? client = null;
        try
        {
            client = new GameClient();
            await client.ConnectAsync(info.ConnectUrl);
            Pass("已连接内置服务器（走的是同一条 SignalR 通道）");
            Check.True(client.IsEncrypted, "内置服务器：会话密钥已协商（应用层加密照常生效）");

            var me = "host-" + Guid.NewGuid().ToString("N")[..6];
            var login = await client.LoginAsync(me, "pw-" + me);
            if (!login.ok) Fail("登录内置服务器失败：" + login.msg);
            else Pass($"账号校验通过（{me}，首次即注册）");

            var created = await client.CreateRoomAsync("内置服务器探针房", null, null);
            if (!created.Ok) Fail("建房失败：" + created.Message);
            else Pass($"建房成功（房间号 {created.RoomId}）");

            if (created.Ok)
            {
                Check.True(client.IsJoined, "内置服务器：建房后即进入房间");

                var rooms = await client.ListRoomsAsync();
                Check.True(rooms.Ok && rooms.Rooms.Count >= 1,
                    $"内置服务器：房间列表能查到刚建的房（{rooms.Rooms.Count} 个）");

                var snapshot = host.Snapshot();
                Check.True(snapshot.RoomCount >= 1,
                    $"内置服务器：进程内查询到房间数 {snapshot.RoomCount}（≥1）");
                Check.True(snapshot.PlayerCount >= 1,
                    $"内置服务器：进程内查询到已连接玩家数 {snapshot.PlayerCount}（≥1）");

                var left = await client.LeaveRoomAsync(keepRoom: false);
                if (!left.ok) Fail("退出房间失败：" + left.msg);
                else Pass("退出房间成功（走正规离开流程）");
            }

            await client.DisposeAsync();
            client = null;
        }
        catch (Exception ex)
        {
            Fail("内置服务器端到端异常：" + ex.Message);
        }
        finally
        {
            if (client != null)
            {
                try { await client.DisposeAsync(); } catch { /* 忽略 */ }
            }
        }

        // ---------- 2b. 内置管理员：默认密码必须能登录（v2.5.0 第 5 条 · 用户裁定 #2） ----------
        // 管理员是**真正的登录账户**：「保留用户名」只挡注册路径（别人抢注 admin），
        // 登录路径绝不复用这条拦截 —— 否则 admin 会被自己人挡在门外。
        GameClient? adminClient = null;
        try
        {
            adminClient = new GameClient();
            await adminClient.ConnectAsync(info.ConnectUrl);
            var adminLogin = await adminClient.LoginAsync(AdminDefaults.Username, AdminDefaults.DefaultAdminPassword);

            Check.True(adminLogin.ok,
                "内置服务器：内置管理员 admin + 默认密码能登录成功（用户裁定 #2：" + adminLogin.msg + "）");
            Check.True(adminClient.IsAdmin, "内置服务器：管理员登录后 IsAdmin == true（服务端回执写入）");
            Check.True(adminClient.IsLoggedIn, "内置服务器：管理员已登录（IsLoggedIn）");
            if (adminLogin.ok) Pass("内置管理员 admin + 默认密码登录成功（管理员是真账号）");

            // 密码错的管理员登录必须被拒（证明走的是校验、不是放行）。
            var wrong = new GameClient();
            try
            {
                await wrong.ConnectAsync(info.ConnectUrl);
                var badLogin = await wrong.LoginAsync(AdminDefaults.Username, "definitely-wrong-password");
                Check.False(badLogin.ok, "内置服务器：管理员密码错误时被拒（不是无条件放行）");
            }
            finally
            {
                try { await wrong.DisposeAsync(); } catch { /* 忽略 */ }
            }
        }
        catch (Exception ex)
        {
            Fail("管理员登录复核异常：" + ex.Message);
        }
        finally
        {
            if (adminClient != null)
            {
                try { await adminClient.DisposeAsync(); } catch { /* 忽略 */ }
            }
        }

        // ---------- 3. 停止 → 端口必须被释放 ----------
        await host.StopAsync(TimeSpan.FromSeconds(5));
        Check.False(host.IsRunning, "内置服务器：停止后不再处于运行态");

        var released = await WaitForPortFreeAsync(info.Port, TimeSpan.FromSeconds(5));
        Check.True(released, $"内置服务器：停止后端口 {info.Port} 已释放（US-4「5 秒内端口释放」）");
        if (released) Pass($"端口 {info.Port} 已释放");
        else Fail($"端口 {info.Port} 在 5 秒内未释放");

        // ---------- 4. 同一端口可再次启动 ----------
        var again = new EmbeddedServerHost(new EmbeddedServerOptions
        {
            PreferredPort = info.Port,
            AllowPortDrift = false
        });
        var againInfo = await again.StartAsync();
        Check.True(againInfo.Ok, "内置服务器：停止后同一端口可再次启动");
        if (againInfo.Ok) Pass("停止后同一端口可再次启动");
        else Fail("同一端口无法再次启动：" + againInfo.ErrorMessage);
        await again.StopAsync(TimeSpan.FromSeconds(5));

        // ---------- 5. 端口漂移：先用一个真实内置服务器占住端口，另一个应自动换一个 ----------
        // 占用者也必须是「绑定 0.0.0.0 的真服务器」：只绑 127.0.0.1 的 TcpListener
        // 在 Windows 上不会与 0.0.0.0 冲突，测不出真实的占用场景。
        var occupier = await StartOnFreePortAsync(5200, 5299);
        if (occupier.Host != null)
        {
            var busyPort = occupier.Port;
            var drifting = new EmbeddedServerHost(new EmbeddedServerOptions
            {
                PreferredPort = busyPort,
                PortRangeStart = busyPort + 1,
                PortRangeEnd = busyPort + 20,
                AllowPortDrift = true
            });

            var driftInfo = await drifting.StartAsync();
            Check.True(driftInfo.Ok, "内置服务器：首选端口被占用时自动漂移并启动成功");
            Check.True(driftInfo.Ok && driftInfo.Port != busyPort,
                $"内置服务器：漂移后端口与占用端口不同（{driftInfo.Port} ≠ {busyPort}）");
            Check.True(driftInfo.Ok && driftInfo.ConnectUrl.Contains($":{driftInfo.Port}/", StringComparison.Ordinal),
                "内置服务器：漂移后连接地址用的是新端口（E-16）");
            Check.True(driftInfo.BootstrapMessages.Any(m => m.Contains($"{busyPort}", StringComparison.Ordinal)
                                                            && m.Contains($"{driftInfo.Port}", StringComparison.Ordinal)),
                "内置服务器：漂移会明确告知「原端口已被占用，已改用新端口」");
            if (driftInfo.Ok) Pass($"端口 {busyPort} 被占用 → 已改用 {driftInfo.Port}");
            else Fail("端口漂移失败：" + driftInfo.ErrorMessage);

            await drifting.StopAsync(TimeSpan.FromSeconds(5));
            await occupier.Host.StopAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            Console.WriteLine("   · 找不到空闲端口用于漂移验证，跳过");
        }

        // ---------- 6. 关闭漂移 + 端口被占 → 明确失败且人话 ----------
        var occupier2 = await StartOnFreePortAsync(5300, 5399);
        if (occupier2.Host != null)
        {
            var busyPort = occupier2.Port;
            var strict = new EmbeddedServerHost(new EmbeddedServerOptions
            {
                PreferredPort = busyPort,
                AllowPortDrift = false
            });

            var strictInfo = await strict.StartAsync();
            Check.False(strictInfo.Ok, "内置服务器：关闭漂移且端口被占用时启动失败（不静默）");
            Check.True(strictInfo.ErrorMessage.Contains($"{busyPort}", StringComparison.Ordinal),
                "内置服务器：失败原因里点明了被占用的端口");
            Check.True(strictInfo.ErrorMessage.Length > 0 && !strictInfo.ErrorMessage.Contains('\n'),
                "内置服务器：失败原因是一行可读的人话（不是异常堆栈）");
            Pass($"关闭漂移 + 端口 {busyPort} 被占用 → 明确报「端口已被占用」");

            await strict.DisposeAsync();
            await occupier2.Host.StopAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            Console.WriteLine("   · 找不到空闲端口用于「关闭漂移」验证，跳过");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "✅ 内置服务器端到端自检通过" : "❌ 内置服务器端到端自检存在失败项");
        return ok;
    }

    // ==================== 工具 ====================

    /// <summary>用反射补齐 GameClient 的私有状态（仅测试用，不改 Share 一行代码）。</summary>
    private static void SetPrivate(GameClient client, string name, object? value)
    {
        typeof(GameClient)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(client, value);

        typeof(GameClient)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .SetValue(client, value);
    }

    private static int ExtractPort(string url)
    {
        var host = url.Replace("http://", "", StringComparison.Ordinal);
        var colon = host.LastIndexOf(':');
        var tail = colon < 0 ? "" : host[(colon + 1)..];
        var slash = tail.IndexOf('/');
        var text = slash >= 0 ? tail[..slash] : tail;
        return int.TryParse(text, out var port) ? port : -1;
    }

    private static async Task<bool> WaitForPortFreeAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortFree(port)) return true;
            await Task.Delay(120);
        }

        return IsPortFree(port);
    }

    /// <summary>
    /// 端口是否空闲：用 <see cref="IPAddress.Any"/> 试探绑定。
    /// 内置服务器监听的是 0.0.0.0，因此必须用 Any 才能真实反映它是否还在占着端口。
    /// </summary>
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

    private sealed record Occupier(EmbeddedServerHost? Host, int Port);

    /// <summary>
    /// 在指定范围里用**真实的内置服务器**占住一个端口（用于验证漂移），
    /// 返回宿主与占用的端口号；整个范围都占不到时 <c>Host</c> 为 null。
    /// </summary>
    private static async Task<Occupier> StartOnFreePortAsync(int from, int to)
    {
        for (var port = from; port <= to; port++)
        {
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
