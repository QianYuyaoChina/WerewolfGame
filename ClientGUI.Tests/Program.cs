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

Console.WriteLine();
if (Check.FailureList.Count == 0)
{
    Console.WriteLine($"✅ 全部通过：{Check.Passed} 项断言");
    return 0;
}

Console.WriteLine($"❌ 失败 {Check.FailureList.Count} 项，通过 {Check.Passed} 项：");
foreach (var f in Check.FailureList) Console.WriteLine("   · " + f);
return 1;
