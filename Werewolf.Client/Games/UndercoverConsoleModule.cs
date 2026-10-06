using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace Werewolf.Client;

/// <summary>
/// 谁是卧底的控制台模块：把游戏私有负载（<see cref="UndercoverGameState"/>）翻译成
/// 房间标题 / 状态渲染 / 指令处理。只依赖共享协议与 <c>GameClient</c> 的通用 op 透传能力。
/// </summary>
internal sealed class UndercoverConsoleModule : IConsoleGameModule
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();

    public string GameId => GameIds.Undercover;

    public string HelpText => @"
──────────── 谁是卧底指令 ────────────
  数字 (如 3)        投票选出你认为的卧底
  -1                 弃票
  skip                轮到自己时跳过本轮发言（描述阶段）
  描述阶段直接用 chat 发言：轮到谁谁才能发，发完自动轮到下一位";

    public string RoomHeader(GameStateDto s)
    {
        var uc = Parse(s);
        var round = uc?.Round ?? 0;
        var phase = uc?.Phase ?? UndercoverPhases.Waiting;
        return $"🕵️ 谁是卧底 · 第 {round} 轮 · {UndercoverPhases.DisplayName(phase)}";
    }

    public string RenderState(GameStateDto s)
    {
        var uc = Parse(s);
        var sb = new StringBuilder();
        var me = s.Players.FirstOrDefault(p => p.IsYou);

        var word = uc == null ? "" : (uc.YourWord.Length > 0 ? uc.YourWord : UndercoverText.BlankWordDisplay);
        // 视角隔离：局中服务端不下发身份，控制台也只能打印「保密」。
        var role = uc is { YourRoleKnown: true } && uc.YourRole.Length > 0
            ? uc.YourRole
            : UndercoverText.UnknownRoleDisplay;

        sb.AppendLine($"│ 你：[{s.YourId}] {me?.Name}　身份：{role}　{(s.IsAlive ? "存活" : "已出局")}");

        // 局中提醒：不会被告知自己是卧底还是平民（整局结束才揭晓）。
        if (uc is { YourRoleKnown: false } && uc.Phase != UndercoverPhases.Waiting)
            sb.AppendLine($"│ 🔒 {UndercoverText.SecretRoleNotice}");

        sb.AppendLine($"│ 你的词：{word}");
        if (uc is { HasWord: false } && uc.Phase != UndercoverPhases.Waiting)
            sb.AppendLine($"│ ⬜ {UndercoverText.BlankSelfHint}");
        if (!string.IsNullOrEmpty(uc?.CategoryHint))
            sb.AppendLine($"│ 分类提示：{uc!.CategoryHint}");
        if (!string.IsNullOrEmpty(uc?.RoleSummary))
            sb.AppendLine($"│ 本局配置：{uc!.RoleSummary}");

        // 轮流发言进度（描述阶段）：没轮到就等着，别抢话。
        if (uc?.Phase == UndercoverPhases.Describe)
        {
            var speaker = uc.CurrentSpeakerName.Length > 0
                ? (uc.IsMyTurnToSpeak ? "轮到你发言" : $"正在发言：{uc.CurrentSpeakerName}")
                : "等待中…";
            sb.AppendLine($"│ 发言顺序：🎤 {speaker}　已发言 {uc.SpokenNames.Count}/{uc.TotalSpeakers}");
        }
        if (!string.IsNullOrEmpty(s.RoomName) || !string.IsNullOrEmpty(s.HostName))
            sb.AppendLine($"│ 房间：{s.RoomName}　房主：{s.HostName}");

        sb.AppendLine("│ 玩家列表：");
        foreach (var p in s.Players)
        {
            var tag = p.Tag != null ? $" <{p.Tag}>" : "";
            var you = p.IsYou ? " ←你" : "";
            var host = p.IsHost ? " 👑" : "";
            var dc = p.IsConnected ? "" : " ⚠掉线";
            var dead = p.IsAlive ? "" : "（出局）";
            sb.AppendLine($"│   [{p.Id}] {p.Name}{dead}{tag}{host}{you}{dc}");
        }

        return sb.ToString().TrimEnd('\n');
    }

    public async Task<bool> TryHandleAsync(string line, GameStateDto? state, GameClient client)
    {
        var uc = Parse(state);
        if (uc == null) return false;

        // 轮流发言：轮到自己时可以直接 chat 发言，也可以用 skip 跳过本轮。
        if (line.Equals("skip", StringComparison.OrdinalIgnoreCase) && uc.Phase == UndercoverPhases.Describe)
        {
            await client.CallGameOpAsync(UndercoverOps.Skip, Node(new TargetPayload { TargetId = 1 }));
            Print("✅ 本轮跳过发言");
            return true;
        }

        if (int.TryParse(line, out var id))
        {
            if (uc.Phase != UndercoverPhases.Vote)
            {
                Print("⚠️ 当前不是投票阶段。");
                return true;
            }

            await client.CallGameOpAsync(UndercoverOps.Vote, Node(new TargetPayload { TargetId = id }));
            Print(id < 0 ? "✅ 已弃票" : $"✅ 已投票 → [{id}]");
            return true;
        }

        return false;
    }

    private static UndercoverGameState? Parse(GameStateDto? s)
    {
        if (s?.GamePayload == null) return null;
        try { return s.GamePayload.Deserialize<UndercoverGameState>(Json); }
        catch { return null; }
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);

    private static void Print(string message)
    {
        lock (Gate) Console.WriteLine(message);
    }
}
