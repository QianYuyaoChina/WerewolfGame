using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace Werewolf.Client;

/// <summary>
/// 狼人杀的控制台模块：把狼人杀私有负载（<see cref="WerewolfGameState"/>，来自
/// <see cref="GameStateDto.GamePayload"/>）翻译成房间标题 / 状态渲染 / 指令处理。
/// 只依赖共享协议与 <c>GameClient</c> 的通用 op 透传能力，不感知房间 / 账号 / 主题等平台模块。
/// </summary>
internal sealed class WerewolfConsoleModule : IConsoleGameModule
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();

    public string GameId => GameIds.Werewolf;

    public string HelpText => @"
──────────── 狼人杀指令 ────────────
  数字 (如 3)        夜间选择目标 / 投票 / 猎人开枪 / 移交警徽
  -1                 弃票 / 放弃开枪 / 作废警徽
  heal               女巫使用解药
  poison 3           女巫毒杀 3 号
  none               女巫今晚不用药
  cupid 3 5          丘比特指定 3 号与 5 号成为情侣";

    public string RoomHeader(GameStateDto s)
    {
        var wf = Parse(s);
        var day = wf?.Day ?? 0;
        var phase = wf?.Phase ?? Phases.Waiting;
        return $"🐺 狼人杀 · 第 {day} 天 · {Phases.DisplayName(phase)}";
    }

    public string RenderState(GameStateDto s)
    {
        var wf = Parse(s);
        var sb = new StringBuilder();
        var me = s.Players.FirstOrDefault(p => p.IsYou);
        var role = string.IsNullOrEmpty(wf?.YourRole) ? "未知" : wf!.YourRole;

        sb.AppendLine($"│ 你：[{s.YourId}] {me?.Name}　身份：{role}　{(s.IsAlive ? "存活" : "已出局")}");
        if (!string.IsNullOrEmpty(wf?.RoleSummary))
            sb.AppendLine($"│ 本局配置：{wf!.RoleSummary}");
        if (wf is { IsLover: true } && !string.IsNullOrEmpty(wf.LoverName))
            sb.AppendLine($"│ 💘 你的情侣：{wf.LoverName}（一方出局另一方殉情）");
        if (wf is { SheriffEnabled: true })
            sb.AppendLine(string.IsNullOrEmpty(wf.SheriffName) ? "│ 🎖 警徽已作废" : $"│ 🎖 当前警长：{wf.SheriffName}");
        if (!string.IsNullOrEmpty(s.RoomName) || !string.IsNullOrEmpty(s.HostName))
            sb.AppendLine($"│ 房间：{s.RoomName}　房主：{s.HostName}");
        sb.AppendLine("│ 玩家列表：");
        foreach (var p in s.Players)
        {
            var tag = p.Tag != null ? $" <{p.Tag}>" : "";
            var you = p.IsYou ? " ←你" : "";
            var host = p.IsHost ? " 👑" : "";
            var dc = p.IsConnected ? "" : " ⚠掉线";
            var dead = p.IsAlive ? "" : "（死亡）";
            sb.AppendLine($"│   [{p.Id}] {p.Name}{dead}{tag}{host}{you}{dc}");
        }
        return sb.ToString().TrimEnd('\n');
    }

    public async Task<bool> TryHandleAsync(string line, GameStateDto? state, GameClient client)
    {
        var wf = Parse(state);
        if (wf == null) return false;

        // 女巫专属指令
        if (wf.Phase == Phases.Night && wf.YourRole == Roles.Witch)
        {
            if (line.Equals("heal", StringComparison.OrdinalIgnoreCase))
            {
                await client.CallGameOpAsync(WerewolfOps.Night, Node(new NightPayload { TargetId = -2, Action = "heal" }));
                Print("✅ 已使用解药");
                return true;
            }
            if (line.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                await client.CallGameOpAsync(WerewolfOps.Night, Node(new NightPayload { TargetId = -1, Action = "none" }));
                Print("✅ 今晚不使用药水");
                return true;
            }
            if (line.StartsWith("poison ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[7..].Trim(), out int pid))
            {
                await client.CallGameOpAsync(WerewolfOps.Night, Node(new NightPayload { TargetId = pid, Action = "poison" }));
                Print($"✅ 已使用毒药 → [{pid}]");
                return true;
            }
        }

        // 丘比特专属指令：cupid 3 5
        if (line.StartsWith("cupid ", StringComparison.OrdinalIgnoreCase)
            && wf.Phase == Phases.Night && wf.YourRole == Roles.Cupid)
        {
            var parts = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
            {
                if (a == b) { Print("⚠️ 情侣必须是两名不同的玩家。"); return true; }
                await client.CallGameOpAsync(WerewolfOps.Cupid, Node(new CupidPayload { TargetA = a, TargetB = b }));
                Print($"💘 已让 [{a}] 与 [{b}] 成为情侣");
                return true;
            }
            Print("⚠️ 用法：cupid 编号1 编号2（例如 cupid 3 5）");
            return true;
        }

        // 纯数字 → 阶段化指令
        if (int.TryParse(line, out int id))
        {
            switch (wf.Phase)
            {
                case Phases.Night:
                    await client.CallGameOpAsync(WerewolfOps.Night, Node(new NightPayload { TargetId = id, Action = "do" }));
                    Print($"✅ 已选择 [{id}]");
                    return true;
                case Phases.Vote:
                    await client.CallGameOpAsync(WerewolfOps.Vote, Node(new TargetPayload { TargetId = id }));
                    Print(id < 0 ? "✅ 已弃票" : $"✅ 已投票 → [{id}]");
                    return true;
                case Phases.HunterShoot:
                    await client.CallGameOpAsync(WerewolfOps.Hunter, Node(new TargetPayload { TargetId = id }));
                    Print(id < 0 ? "✅ 放弃开枪" : $"✅ 开枪 → [{id}]");
                    return true;
                case Phases.SheriffTransfer:
                    await client.CallGameOpAsync(WerewolfOps.Sheriff, Node(new TargetPayload { TargetId = id }));
                    Print(id < 0 ? "✅ 已作废警徽" : $"🎖 已把警徽移交给 [{id}]");
                    return true;
                default:
                    Print("⚠️ 当前阶段不能选择目标。");
                    return true;
            }
        }

        return false;
    }

    private static WerewolfGameState? Parse(GameStateDto? s)
    {
        if (s?.GamePayload == null) return null;
        try
        {
            return s.GamePayload.Deserialize<WerewolfGameState>(Json);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);

    private static void Print(string message)
    {
        lock (Gate) Console.WriteLine(message);
    }
}
