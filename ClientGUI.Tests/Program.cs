using ClientGUI.Tests;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string DefaultUrl = "http://localhost:5000/gamehub";

// 保持若干客户端在线：--hold alice bob carol 90
var holdIndex = Array.IndexOf(args, "--hold");
if (holdIndex >= 0)
{
    var rest = args.Skip(holdIndex + 1).ToList();
    var seconds = 60;
    if (rest.Count > 0 && int.TryParse(rest[^1], out var parsed))
    {
        seconds = parsed;
        rest.RemoveAt(rest.Count - 1);
    }
    if (rest.Count == 0) rest.AddRange(new[] { "alice", "bob", "carol" });
    return await HoldMode.RunAsync(rest.ToArray(), seconds, DefaultUrl);
}

// 自动对局机器人：--bots bob carol dave 90
var botsIndex = Array.IndexOf(args, "--bots");
if (botsIndex >= 0)
{
    var rest = args.Skip(botsIndex + 1).ToList();
    var seconds = 60;
    if (rest.Count > 0 && int.TryParse(rest[^1], out var parsed))
    {
        seconds = parsed;
        rest.RemoveAt(rest.Count - 1);
    }
    if (rest.Count == 0) rest.AddRange(new[] { "alice", "bob", "carol" });
    return await BotMode.RunAsync(rest.ToArray(), seconds, DefaultUrl);
}

// 界面冒烟：--ui（只解析 XAML，不显示窗口）
if (Array.IndexOf(args, "--ui") >= 0)
{
    Console.WriteLine("════════ ClientGUI 界面冒烟 ════════");
    UiSmokeCheck.Run();
    Console.WriteLine();
    foreach (var f in Check.FailureList) Console.WriteLine("   · " + f);
    Console.WriteLine(Check.FailureList.Count == 0
        ? $"✅ 界面冒烟通过：{Check.Passed} 项断言"
        : $"❌ 界面冒烟失败 {Check.FailureList.Count} 项");
    return Check.FailureList.Count == 0 ? 0 : 1;
}

// 协议 / 边界探针（QA 独立编写）：--protocol <url>
var protocolIndex = Array.IndexOf(args, "--protocol");
if (protocolIndex >= 0 && protocolIndex + 1 < args.Length)
{
    var url = args[protocolIndex + 1];
    Console.WriteLine($"════════ 协议 / 边界探针（{url}） ════════");
    return await ProtocolBoundaryProbe.RunAsync(url);
}

// 多房间端到端探针：--rooms <url>
var roomsIndex = Array.IndexOf(args, "--rooms");
if (roomsIndex >= 0 && roomsIndex + 1 < args.Length)
{
    var url = args[roomsIndex + 1];
    Console.WriteLine($"════════ 多房间端到端探针（{url}） ════════");
    return await MultiRoomProbe.RunAsync(url);
}

// 房间生命周期探针：--lifecycle <url>
var lifecycleIndex = Array.IndexOf(args, "--lifecycle");
if (lifecycleIndex >= 0 && lifecycleIndex + 1 < args.Length)
{
    var url = args[lifecycleIndex + 1];
    Console.WriteLine($"════════ 房间生命周期探针（{url}） ════════");
    return await LifecycleProbe.RunAsync(url);
}

// 断线重连探针：--reconnect <url> [秒]
var reconnectIndex = Array.IndexOf(args, "--reconnect");
if (reconnectIndex >= 0)
{
    var url = reconnectIndex + 1 < args.Length ? args[reconnectIndex + 1] : DefaultUrl;
    var seconds = 60;
    if (reconnectIndex + 2 < args.Length && int.TryParse(args[reconnectIndex + 2], out var parsed))
        seconds = parsed;

    Console.WriteLine($"════════ 断线重连探针（{url}） ════════");
    return await ReconnectProbe.RunAsync(url, seconds);
}

// 谁是卧底端到端：--undercover <url>（建议小局配置）
var undercoverIndex = Array.IndexOf(args, "--undercover");
if (undercoverIndex >= 0)
{
    var url = undercoverIndex + 1 < args.Length ? args[undercoverIndex + 1] : DefaultUrl;
    Console.WriteLine($"════════ 谁是卧底端到端（{url}） ════════");
    var uc = await UndercoverProbe.RunAsync(url);
    Console.WriteLine();
    foreach (var note in uc.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(uc.Ok ? "✅ 通过" : "❌ 存在问题");
    return uc.Ok ? 0 : 1;
}

// 谁是卧底「白板」端到端（QA 新增，≥7 人局）：--ucblank <url> [人数]
var ucBlankIndex = Array.IndexOf(args, "--ucblank");
if (ucBlankIndex >= 0)
{
    var url = ucBlankIndex + 1 < args.Length ? args[ucBlankIndex + 1] : DefaultUrl;
    var count = 7;
    if (ucBlankIndex + 2 < args.Length && int.TryParse(args[ucBlankIndex + 2], out var parsed) && parsed >= 3)
        count = parsed;

    Console.WriteLine($"════════ 谁是卧底 · 白板端到端（{url}，{count} 人） ════════");
    var blank = await UndercoverBlankProbe.RunAsync(url, count);
    Console.WriteLine();
    foreach (var note in blank.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(blank.Ok ? "✅ 通过" : "❌ 存在问题");
    return blank.Ok ? 0 : 1;
}

// 新身份（丘比特 / 警长 / 自定义布局）：--newroles <url>（需小局配置）
var newRolesIndex = Array.IndexOf(args, "--newroles");
if (newRolesIndex >= 0)
{
    var url = newRolesIndex + 1 < args.Length ? args[newRolesIndex + 1] : DefaultUrl;
    Console.WriteLine($"════════ 新身份端到端（{url}） ════════");
    var newRoles = await NewRolesProbe.RunAsync(url);
    Console.WriteLine();
    foreach (var note in newRoles.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(newRoles.Ok ? "✅ 通过" : "❌ 存在问题");
    return newRoles.Ok ? 0 : 1;
}

// 房主交接 / 房间保留 / 邀请码：--hostinvite <url>
var hostInviteIndex = Array.IndexOf(args, "--hostinvite");
if (hostInviteIndex >= 0)
{
    var url = hostInviteIndex + 1 < args.Length ? args[hostInviteIndex + 1] : DefaultUrl;
    Console.WriteLine($"════════ 房主交接 / 保留房间 / 邀请码（{url}） ════════");
    var hostInvite = await HostInviteProbe.RunAsync(url);
    Console.WriteLine();
    foreach (var note in hostInvite.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(hostInvite.Ok ? "✅ 通过" : "❌ 存在问题");
    return hostInvite.Ok ? 0 : 1;
}

// 彩蛋 + 称号的协议级端到端探针：--egg [url]
var eggIndex = Array.IndexOf(args, "--egg");
if (eggIndex >= 0)
{
    var url = eggIndex + 1 < args.Length && !args[eggIndex + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[eggIndex + 1]
        : DefaultUrl;
    Console.WriteLine($"════════ 彩蛋 / 称号 端到端探针（{url}） ════════");
    var egg = await TitleEggProbe.RunAsync(url);
    Console.WriteLine();
    foreach (var note in egg.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(egg.Ok ? "✅ 通过" : "❌ 存在问题");
    return egg.Ok ? 0 : 1;
}

// 内置服务器端到端自检：--embedded（进程内起服，不依赖外部服务端）
if (Array.IndexOf(args, "--embedded") >= 0)
{
    Console.WriteLine("════════ 内置服务器端到端自检 ════════");
    var embeddedOk = await EmbeddedServerProbe.RunAsync();

    // v2.6.0 S1：QA 独立复核 —— Q1=B「列表页返回登录页不停服务器」需要真服务器才验得准。
    await S1QaGuard.RunLiveQ1BChecksAsync();

    // v2.6.0 S3 · T43：保留房间**真端到端**（真内置服务器 + 真页面栈 + 真 leaveroom）+
    // 返回新鲜度（房间 → 关于 → 更新日志 → 返回 → 返回，玩家名单逐项比对）。
    await S3QaGuard.RunLiveChecksAsync();

    Console.WriteLine();
    foreach (var f in Check.FailureList) Console.WriteLine("   · " + f);
    Console.WriteLine(Check.FailureList.Count == 0
        ? $"✅ 内置服务器自检通过：{Check.Passed} 项断言"
        : $"❌ 内置服务器自检失败 {Check.FailureList.Count} 项");
    return embeddedOk && Check.FailureList.Count == 0 ? 0 : 1;
}

// 内置服务器：QA 独立复核（真实起服 + 关窗收尾语义）—— --embeddedqa
if (Array.IndexOf(args, "--embeddedqa") >= 0)
{
    Console.WriteLine("════════ 内置服务器：QA 独立复核 ════════");
    var qaOk = await EmbeddedServerQaAudit.RunAsync();
    Console.WriteLine();
    foreach (var f in Check.FailureList) Console.WriteLine("   · " + f);
    Console.WriteLine(Check.FailureList.Count == 0
        ? $"✅ 内置服务器 QA 复核通过：{Check.Passed} 项断言"
        : $"❌ 内置服务器 QA 复核失败 {Check.FailureList.Count} 项");
    return qaOk && Check.FailureList.Count == 0 ? 0 : 1;
}

// 端到端联调：--server <url>
var serverIndex = Array.IndexOf(args, "--server");
if (serverIndex >= 0 && serverIndex + 1 < args.Length)
{
    var url = args[serverIndex + 1];
    Console.WriteLine($"════════ ClientGUI 端到端联调（{url}） ════════");
    var result = await IntegrationProbe.RunAsync(url);
    Console.WriteLine();
    foreach (var note in result.Notes) Console.WriteLine("   " + note);
    Console.WriteLine();
    Console.WriteLine(result.Ok ? "✅ 端到端联调通过" : "❌ 端到端联调存在问题");
    return result.Ok ? 0 : 1;
}

// 默认：纯逻辑校验
Console.WriteLine("════════ ClientGUI 逻辑校验 ════════");
await ActionComputationTests.RunAllAsync();

Console.WriteLine("════════ 加密算法校验 ════════");
CryptoTests.RunAll();

Console.WriteLine("════════ 规则与界面状态校验 ════════");
GameRulesTests.RunAll();

Console.WriteLine("════════ 新身份与房主设置校验 ════════");
NewRolesTests.RunAll();

Console.WriteLine("════════ 反作弊：服务端校验 ════════");
AntiCheatTests.RunAll();

Console.WriteLine("════════ 谁是卧底：词库与发牌 ════════");
UndercoverTests.RunAll();

Console.WriteLine("════════ 谁是卧底：QA 独立复核（隐藏阵营 / 白板） ════════");
UndercoverQaAudit.RunAll();

Console.WriteLine("════════ 多房间纯逻辑校验 ════════");
RoomTests.RunAll();

Console.WriteLine("════════ 扩展性验证（假游戏） ════════");
FakeGameProbe.RunAll();

Console.WriteLine("════════ 扩展性验证（QA 独立第三游戏） ════════");
IndependentGameProbe.RunAll();

// 标题由 EmbeddedServerProbe.RunLogicChecks() 自己打印（避免与这里重复输出同一行）。
EmbeddedServerProbe.RunLogicChecks();

Console.WriteLine("════════ 内置服务器：QA 独立复核（纯逻辑） ════════");
EmbeddedServerQaAudit.RunLogicChecks();

// v2.5.0 B4 / B5 的能力探针（内置管理员 / 可编辑资源 / 修改密码 / 一键开服 / 导航居中）。
V25BatchProbe.RunLogicChecks();

// v2.6.0 S1：页面化专项（导航栈 / 关闭意图映射 / 退订 / 返回键位）—— 纯逻辑批次。
PageCatalog.RunLogicChecks();

// v2.6.0 S1：QA 独立验收（不复用 PageCatalog 的判定，另写一份重证同一批结论）。
S1QaGuard.RunLogicChecks();

// v2.6.0 S2：QA 独立验收（浮层语义 / 结果通道 / 浮层叠浮层 / 七页工厂接线）。
S2QaGuard.RunLogicChecks();

// v2.6.0 B3 页面化后：QA 独立复核（彩蛋 5 击窗口 / 称号校验口径 / 服务端权威 / 状态下发）。
B3QaGuard.RunLogicChecks();

// v2.6.0 S3：QA 独立验收（关于 / 更新日志 / 保留房间三页 + 唯一判定点 + 收尾不弹窗）。
// 判据是**交付前**就登记在 docs/v25/SHELL-ARCHITECTURE.md §18 的 A–P，跑完只回填实测列。
S3QaGuard.RunLogicChecks();

Console.WriteLine();
AppShellQaGuard.RunDocsAndSourceGuards();

// v2.5.0 B2 / B3 的纯逻辑守护：关闭意图表 / 彩蛋计数 / 称号校验（不需要 WPF 上下文）。
Console.WriteLine();
AppShellGuard.RunLogicGuards();

Console.WriteLine();
if (Check.FailureList.Count == 0)
{
    Console.WriteLine($"✅ 全部通过：{Check.Passed} 项断言");
    return 0;
}

Console.WriteLine($"❌ 失败 {Check.FailureList.Count} 项，通过 {Check.Passed} 项：");
foreach (var f in Check.FailureList) Console.WriteLine("   · " + f);
return 1;
